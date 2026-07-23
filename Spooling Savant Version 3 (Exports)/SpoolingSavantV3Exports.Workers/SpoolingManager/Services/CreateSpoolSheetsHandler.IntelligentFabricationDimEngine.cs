using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Intelligent Fabrication Dimension Engine — functional-anchor planner (H/V only in active view).
/// Production entry for fabrication spool auto-dim. Does not touch AssemblyLine sheet creation.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	#region Intelligent fabrication dim — constants

	private const double FabDimFirstOffsetSheetInches = 0.375; // 3/8"
	private const double FabDimStackGapSheetInches = 0.25;
	private const double FabDimMaxOffsetSheetInches = 1.5;
	private const double FabDimMinLengthFeet = 1.0 / 24.0;
	private const int FabDimMaxDimensionsPerView = 48;
	private const double FabDimConnectorMateTolFeet = 0.08;
	private const double FabDimPipeLikeColinearDot = 0.95;
	private const double FabDimShortFlangeMaxLengthFeet = 0.75;
	/// <summary>~3° — legacy H/V parallelism check (cos≈0.9986).</summary>
	private const double FabDimAxisParallelDotMin = 0.9986;
	/// <summary>Strict view-axis gate — reject any slanted/aligned dim curve (dot ≥ 0.9999).</summary>
	private const double FabDimExactAxisDotMin = 0.9999;
	/// <summary>
	/// Elbow/tee → elbow/tee across a run that leaves the view plane is not a true length here
	/// (assembly going away from the user). Depth ≥ this AND ≥ ratio of 3D length → skip.
	/// </summary>
	private const double FabDimForeshortenedDepthMinFeet = 2.0 / 12.0; // 2"
	private const double FabDimForeshortenedDepthRatio = 0.30;

	#endregion

	#region Intelligent fabrication dim — nested types

	private enum FabDimPartRole
	{
		Unknown,
		PipeLike,
		ElbowLike,
		TeeLike,
		CrossLike,
		Olet,
		FlangeFace,
		ReducerLike,
		CouplingLike,
		ValveLike,
		EndCapLike,
		PassThrough
	}

	/// <summary>Required purpose tag on every planned dimension intent.</summary>
	private enum DimensionPurpose
	{
		MainRunSegment,
		MainRunOverall,
		OletLocation,
		BranchSegment,
		BranchOverall,
		OpenEndLocation,
		/// <summary>Boxed-out true-45: in-plane H offset between the two run centerlines.</summary>
		BoxedOut45Horizontal,
		/// <summary>Boxed-out true-45: in-plane V offset from facing flange CL to the 45 elbow.</summary>
		BoxedOut45Vertical
	}

	/// <summary>
	/// Three distinct pipe-end concepts:
	/// OpenPipeEnd — normal fabrication datum (physically open).
	/// Connected pipe ends — never normal datums (internal joints); not an anchor kind.
	/// HostPipeEndForOlet — OletLocation-only exception: end of the straight host pipe,
	/// even when that end is connected to a fitting/flange.
	/// </summary>
	private enum FabDimAnchorKind
	{
		None,
		FlangeOuterFace,
		ElbowCenter,
		TeeCenter,
		OletMainStation,
		OpenPipeEnd,
		HostPipeEndForOlet,
		EndCapOuterFace,
		FittingCenter
	}

	private sealed class FabDimGraphNode
	{
		public FabricationPart Part;
		public long PartId;
		public FabDimPartRole Role;
		public List<Connector> Connectors = new List<Connector>();
		public Dictionary<long, FabDimGraphEdge> EdgesByMateId = new Dictionary<long, FabDimGraphEdge>();
	}

	private sealed class FabDimGraphEdge
	{
		public FabDimGraphNode Self;
		public Connector SelfConnector;
		public FabDimGraphNode Mate;
		public Connector MateConnector;
	}

	private sealed class FabDimFunctionalAnchor
	{
		public FabricationPart Part;
		public FabDimPartRole Role;
		public FabDimAnchorKind Kind;
		public XYZ Point;
		public Element OwnerElement;
		public bool IsOpenPipeEnd;
		/// <summary>OletLocation-only: host pipe end baseline (may be connected).</summary>
		public bool IsHostPipeEndForOlet;
		public bool IsOlet;
		public int OrderIndex;
	}

	private sealed class FabDimRun
	{
		public List<FabricationPart> OrderedParts = new List<FabricationPart>();
		public List<FabDimFunctionalAnchor> Anchors = new List<FabDimFunctionalAnchor>();
		public bool IsMainRun;
		public bool IsBranch;
		public FabricationPart HostPipeForOlets;
		public XYZ DominantAxisInView;
		public double LengthFeet;
	}

	private sealed class FabDimIntent
	{
		public FabDimFunctionalAnchor A;
		public FabDimFunctionalAnchor B;
		public DimensionPurpose Purpose;
		public bool BelongsToMainRunGroup;
		public XYZ StackDirectionInView;
		public bool MeasureAlongRight;
		/// <summary>When set, overrides group offset sign (e.g. olet above = +1).</summary>
		public int? OffsetSignOverride;
	}

	private sealed class FabDimPlaceRecord
	{
		public FabDimIntent Intent;
		public Dimension Dimension;
		public long IdA;
		public long IdB;
	}

	#endregion

	#region Intelligent fabrication dim — entry

	/// <summary>
	/// Functional-anchor fabrication auto-dimension engine. Returns count of dimensions placed.
	/// Planning: graph → runs/branches → classify → resolve functional anchors → suppress →
	/// olet locations → branch dims → main segments → main overall → dedupe → side → place H/V → validate.
	/// </summary>
	private static int TryApplyIntelligentFabricationDimensionRules(
		Document doc,
		View view,
		AssemblyInstance assembly,
		List<FabricationPart> parts,
		List<FabricationPart> allParts,
		XYZ unitAxis,
		SpoolingManagerSettings spoolSettings,
		List<string> failureNotes)
	{
		string assemblyName = assembly != null ? AssemblyDisplayName.Get(assembly) : "intel-fab";
		string viewLabel = view?.Name ?? "?";
		int dimsBefore = CountViewLinearDimensions(doc, view);

		try
		{
			if (doc == null || view == null)
			{
				failureNotes?.Add("Intel fab-dim: missing document or view.");
				LogFabDimDiag(assemblyName, viewLabel, "REJECTED missing doc/view", dimsBefore, dimsBefore);
				return 0;
			}

			List<FabricationPart> pool = (allParts != null && allParts.Count > 0) ? allParts : parts;
			if (pool == null || pool.Count == 0)
			{
				failureNotes?.Add("Intel fab-dim: empty fabrication pool.");
				return 0;
			}

			if (!TryGetViewPlaneAxes(view, out XYZ viewNormal, out XYZ right, out XYZ up))
			{
				failureNotes?.Add("Intel fab-dim: could not resolve view plane axes.");
				return 0;
			}

			// 1) Graph
			Dictionary<long, FabDimGraphNode> graph = BuildFabDimConnectorGraph(pool);
			if (graph.Count == 0)
			{
				failureNotes?.Add("Intel fab-dim: no dimensionable fabrication parts in graph.");
				return 0;
			}

			// 2) Runs / branches
			List<FabDimRun> runs = WalkFabDimRuns(graph, pool, viewNormal, unitAxis, right, up);
			if (runs.Count == 0)
			{
				failureNotes?.Add("Intel fab-dim: no runs found.");
				return 0;
			}

			DesignateMainRun(runs);

			// 3–5) Classify already on graph; resolve functional anchors; suppress connected ends & internal joints
			foreach (FabDimRun run in runs)
			{
				ResolveFabDimFunctionalAnchors(run, graph, pool);
				SuppressNonFunctionalAnchors(run, graph, pool, assemblyName, viewLabel);
			}

			// 6–9) Plan intents in required order
			List<FabDimIntent> intents = new List<FabDimIntent>();
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

			PlanOletLocationIntents(runs, graph, pool, view, viewNormal, right, up, intents, seen, assemblyName, viewLabel);
			PlanBranchIntents(runs, view, viewNormal, right, up, intents, seen, assemblyName, viewLabel);
			PlanMainSegmentIntents(runs, view, viewNormal, right, up, intents, seen, assemblyName, viewLabel);
			PlanBoxedOut45Intents(runs, view, viewNormal, right, up, intents, seen, assemblyName, viewLabel);
			PlanMainOverallIntents(runs, view, viewNormal, right, up, intents, seen, assemblyName, viewLabel);

			if (intents.Count == 0)
			{
				failureNotes?.Add("Intel fab-dim: planner produced no dimension intents.");
				LogFabDimDiag(assemblyName, viewLabel, "REJECTED no intents after planning", dimsBefore, dimsBefore);
				return 0;
			}

			// 10) Dedupe already via seen keys; 11–12) side + place H/V only
			int preferredSign = ResolveFabDimPreferredOffsetSign(view, viewNormal, right, up, runs, unitAxis);
			List<FabDimPlaceRecord> placedRecords = new List<FabDimPlaceRecord>();
			int placed = PlaceFabDimIntents(
				doc,
				view,
				viewNormal,
				right,
				up,
				intents,
				pool,
				spoolSettings,
				preferredSign,
				failureNotes,
				placedRecords,
				assemblyName,
				viewLabel);

			// 13) Validate
			int killed = ValidateFabDimPlacements(doc, view, pool, graph, placedRecords, assemblyName, viewLabel);
			placed = Math.Max(0, placed - killed);

			int dimsAfter = CountViewLinearDimensions(doc, view);
			LogFabDimDiag(
				assemblyName,
				viewLabel,
				"COMPLETED placed=" + placed + " validatedKilled=" + killed + " intents=" + intents.Count,
				dimsBefore,
				dimsAfter);

			if (placed == 0)
			{
				failureNotes?.Add("Intel fab-dim: no dimensions could be placed (short/non-HV/failed refs/validation).");
			}

			return placed;
		}
		catch (Exception ex)
		{
			failureNotes?.Add("Intel fab-dim engine failed: " + (ex.Message ?? ex.GetType().Name));
			LogFabDimDiag(assemblyName, viewLabel, "REJECTED exception " + (ex.Message ?? ex.GetType().Name), dimsBefore, CountViewLinearDimensions(doc, view));
			return 0;
		}
	}

	#endregion

	#region Intelligent fabrication dim — classification

	private static FabDimPartRole ClassifyFabDimPartRole(FabricationPart part, IList<FabricationPart> pool)
	{
		if (part == null)
		{
			return FabDimPartRole.Unknown;
		}

		if (IsGasketPart(part) || IsWeldPart(part) || FabricationPartClassification.IsBoltKitPart(part))
		{
			return FabDimPartRole.PassThrough;
		}

		if (IsOletPart(part))
		{
			return FabDimPartRole.Olet;
		}

		Document doc = ((Element)part).Document;
		List<Connector> connectors = ListConnectors(part);

		// Welded-on olets (anvilets/outlets) add a tap connector per olet to a straight host pipe
		// (2 ends + N taps), which made connector-count classification report TeeLike/CrossLike.
		// The bogus "fitting" then failed center resolution AND the pipe's open end was never
		// anchored (open-end anchors are PipeLike-only) — so a run terminating at that open end
		// lost its main overall (e.g. flange → open-end spool SP-05). Classify on non-olet
		// connectors only; real tees/crosses mate to pipes at their branch ports, never to olets.
		if (connectors.Count > 2 && pool != null)
		{
			List<Connector> nonOletConnectors = new List<Connector>(connectors.Count);
			foreach (Connector connector in connectors)
			{
				FabricationPart tapMate = connector != null ? FindMateAtConnector(part, connector, pool) : null;
				if (tapMate != null && IsOletPart(tapMate))
				{
					continue;
				}
				nonOletConnectors.Add(connector);
			}
			if (nonOletConnectors.Count >= 1)
			{
				connectors = nonOletConnectors;
			}
		}
		int count = connectors.Count;

		if (count >= 4)
		{
			return FabDimPartRole.CrossLike;
		}

		if (count == 3)
		{
			return FabDimPartRole.TeeLike;
		}

		if (count == 2)
		{
			XYZ d0 = SafeConnectorDirection(connectors[0]);
			XYZ d1 = SafeConnectorDirection(connectors[1]);
			if (d0 != null && d1 != null)
			{
				double dot = Math.Abs(d0.DotProduct(d1));
				if (dot > FabDimPipeLikeColinearDot)
				{
					if (TryClassifyAsFlangeFaceByGeometry(part, connectors, pool))
					{
						return FabDimPartRole.FlangeFace;
					}

					// Copper Flange Adapter / companion flanges are colinear 2-connector fittings.
					// Thin adapters often report LocationCurve length 0 and both connectors within
					// mate-tol of the same pipe end — geometry classify can miss them. Never fall
					// through to PipeLike when corpus says flange (material-agnostic).
					if (FabricationPartClassification.IsFlangePart(part, doc)
						|| IsFabDimEndCapOrBlind(part))
					{
						return FabDimPartRole.FlangeFace;
					}

					if (IsPipeRunPart(part) || FabricationPartClassification.IsStraightPipeRun(part))
					{
						return FabDimPartRole.PipeLike;
					}

					if (FabricationPartClassification.IsReducerPart(part, doc))
					{
						return FabDimPartRole.ReducerLike;
					}

					string corpus = GetSoftSearchCorpus(part);
					if (corpus.IndexOf("COUPLING", StringComparison.OrdinalIgnoreCase) >= 0
						|| corpus.IndexOf("UNION", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return FabDimPartRole.CouplingLike;
					}

					if (IsValvePart(part) || FabricationPartClassification.IsValvePart(part, doc))
					{
						return FabDimPartRole.ValveLike;
					}

					return FabDimPartRole.PipeLike;
				}

				return FabDimPartRole.ElbowLike;
			}

			return SoftClassifyAmbiguousPart(part, doc);
		}

		if (count == 1)
		{
			if (TryClassifyAsFlangeFaceByGeometry(part, connectors, pool)
				|| FabricationPartClassification.IsFlangePart(part, doc))
			{
				return FabDimPartRole.FlangeFace;
			}

			string corpus = GetSoftSearchCorpus(part);
			if (corpus.IndexOf("CAP", StringComparison.OrdinalIgnoreCase) >= 0
				|| corpus.IndexOf("BLIND", StringComparison.OrdinalIgnoreCase) >= 0
				|| corpus.IndexOf("END CAP", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return FabDimPartRole.EndCapLike;
			}
		}

		if (FabricationPartClassification.IsFlangePart(part, doc))
		{
			return FabDimPartRole.FlangeFace;
		}

		return SoftClassifyAmbiguousPart(part, doc);
	}

	private static FabDimPartRole SoftClassifyAmbiguousPart(FabricationPart part, Document doc)
	{
		if (FabricationPartClassification.IsElbowPart(part, doc))
		{
			return FabDimPartRole.ElbowLike;
		}

		if (FabricationPartClassification.IsTeePart(part, doc))
		{
			return FabDimPartRole.TeeLike;
		}

		if (FabricationPartClassification.IsReducerPart(part, doc))
		{
			return FabDimPartRole.ReducerLike;
		}

		if (IsValvePart(part) || FabricationPartClassification.IsValvePart(part, doc))
		{
			return FabDimPartRole.ValveLike;
		}

		if (FabricationPartClassification.IsFlangePart(part, doc))
		{
			return FabDimPartRole.FlangeFace;
		}

		if (IsPipeRunPart(part) || FabricationPartClassification.IsStraightPipeRun(part))
		{
			return FabDimPartRole.PipeLike;
		}

		SpoolFittingKind kind = ClassifyFabricationPart(part, doc);
		switch (kind)
		{
			case SpoolFittingKind.Elbow:
				return FabDimPartRole.ElbowLike;
			case SpoolFittingKind.Tee:
				return FabDimPartRole.TeeLike;
			case SpoolFittingKind.Flange:
				return FabDimPartRole.FlangeFace;
			case SpoolFittingKind.Olet:
				return FabDimPartRole.Olet;
			case SpoolFittingKind.Valve:
				return FabDimPartRole.ValveLike;
			case SpoolFittingKind.Pipe:
				return FabDimPartRole.PipeLike;
			default:
				return FabDimPartRole.Unknown;
		}
	}

	private static bool TryClassifyAsFlangeFaceByGeometry(
		FabricationPart part,
		List<Connector> connectors,
		IList<FabricationPart> pool)
	{
		if (part == null || connectors == null || connectors.Count == 0)
		{
			return false;
		}

		Document doc = ((Element)part).Document;
		if (FabricationPartClassification.IsFlangePart(part, doc))
		{
			return true;
		}

		double len = GetFabricationStraightLineLength(part);
		// Flange adapters often have no LocationCurve — fall back to connector span.
		if (len <= 1E-09 && connectors.Count >= 2
			&& connectors[0]?.Origin != null && connectors[1]?.Origin != null)
		{
			len = connectors[0].Origin.DistanceTo(connectors[1].Origin);
		}

		bool shortPart = len > 1E-09 && len <= FabDimShortFlangeMaxLengthFeet;
		if (!shortPart && len > FabDimShortFlangeMaxLengthFeet)
		{
			return false;
		}

		// Thin flanges: both connectors can sit inside mate-tol of the same pipe end.
		// Prefer the connector farthest from the nearest pool mate as the open/face side.
		Connector unmated = FindLikelyFlangeOuterConnector(part, connectors, pool);

		if (unmated == null)
		{
			return shortPart && FabricationPartClassification.IsFlangePart(part, doc);
		}

		if (TryFindPlanarFaceNearPoint(part, unmated.Origin, out _))
		{
			return true;
		}

		return FabricationPartClassification.IsFlangePart(part, doc);
	}

	/// <summary>
	/// Outer-face candidate when proximity mate-tol swallows both flange connectors.
	/// Returns the connector farthest from the nearest non-self part connector in the pool,
	/// or a truly unmated connector when one exists.
	/// </summary>
	private static Connector FindLikelyFlangeOuterConnector(
		FabricationPart part,
		List<Connector> connectors,
		IList<FabricationPart> pool)
	{
		if (part == null || connectors == null || connectors.Count == 0)
		{
			return null;
		}

		Connector trulyOpen = null;
		foreach (Connector c in connectors)
		{
			if (c?.Origin == null)
			{
				continue;
			}

			if (FindMateAtConnector(part, c, pool) == null
				&& FindMateAtConnectorViaAllRefs(part, c, pool, out _) == null)
			{
				trulyOpen = c;
				break;
			}
		}

		if (trulyOpen != null)
		{
			return trulyOpen;
		}

		if (pool == null || connectors.Count < 2)
		{
			return null;
		}

		// Both look mated: outer = farthest from the nearest foreign connector origin.
		XYZ nearestForeign = null;
		double nearestDist = double.MaxValue;
		long selfId = GetElementIdValue(((Element)part).Id);
		foreach (FabricationPart other in pool)
		{
			if (other == null || GetElementIdValue(((Element)other).Id) == selfId)
			{
				continue;
			}

			foreach (Connector oc in ListConnectors(other))
			{
				if (oc?.Origin == null)
				{
					continue;
				}

				foreach (Connector c in connectors)
				{
					if (c?.Origin == null)
					{
						continue;
					}

					double d = c.Origin.DistanceTo(oc.Origin);
					if (d < nearestDist)
					{
						nearestDist = d;
						nearestForeign = oc.Origin;
					}
				}
			}
		}

		if (nearestForeign == null || nearestDist > FabDimConnectorMateTolFeet * 2.0)
		{
			return null;
		}

		Connector farthest = null;
		double best = -1;
		foreach (Connector c in connectors)
		{
			if (c?.Origin == null)
			{
				continue;
			}

			double d = c.Origin.DistanceTo(nearestForeign);
			if (d > best)
			{
				best = d;
				farthest = c;
			}
		}

		return farthest;
	}

	private static bool TryFindPlanarFaceNearPoint(FabricationPart part, XYZ nearPoint, out PlanarFace face)
	{
		face = null;
		if (part == null || nearPoint == null)
		{
			return false;
		}

		try
		{
			Options opt = new Options
			{
				ComputeReferences = false,
				DetailLevel = ViewDetailLevel.Fine,
				IncludeNonVisibleObjects = false
			};
			GeometryElement geom = ((Element)part).get_Geometry(opt);
			if (geom == null)
			{
				return false;
			}

			double best = double.MaxValue;
			PlanarFace bestFace = null;
			foreach (GeometryObject go in geom)
			{
				Solid solid = go as Solid;
				if (solid == null || solid.Faces == null || solid.Faces.Size == 0)
				{
					continue;
				}

				foreach (Face f in solid.Faces)
				{
					PlanarFace pf = f as PlanarFace;
					if (pf?.Origin == null)
					{
						continue;
					}

					double d = pf.Origin.DistanceTo(nearPoint);
					if (d < best && d < 0.35)
					{
						best = d;
						bestFace = pf;
					}
				}
			}

			if (bestFace != null)
			{
				face = bestFace;
				return true;
			}
		}
		catch
		{
		}

		return false;
	}

	private static XYZ SafeConnectorDirection(Connector connector)
	{
		if (connector == null)
		{
			return null;
		}

		try
		{
			XYZ dir = connector.CoordinateSystem?.BasisZ;
			if (dir != null && dir.GetLength() > 1E-09)
			{
				return dir.Normalize();
			}
		}
		catch
		{
		}

		return null;
	}

	private static string GetSoftSearchCorpus(FabricationPart part)
	{
		try
		{
			return FabricationPartClassification.GetExpandedSearchCorpus(part) ?? string.Empty;
		}
		catch
		{
			return (((Element)part)?.Name ?? string.Empty);
		}
	}

	private static bool IsFabDimEndCapOrBlind(FabricationPart part)
	{
		if (part == null)
		{
			return false;
		}

		string corpus = GetSoftSearchCorpus(part);
		return corpus.IndexOf("CAP", StringComparison.OrdinalIgnoreCase) >= 0
			|| corpus.IndexOf("BLIND", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	#endregion

	#region Intelligent fabrication dim — graph

	private static Dictionary<long, FabDimGraphNode> BuildFabDimConnectorGraph(IList<FabricationPart> pool)
	{
		Dictionary<long, FabDimGraphNode> graph = new Dictionary<long, FabDimGraphNode>();
		if (pool == null)
		{
			return graph;
		}

		foreach (FabricationPart part in pool)
		{
			if (part == null)
			{
				continue;
			}

			FabDimPartRole role = ClassifyFabDimPartRole(part, pool);
			if (role == FabDimPartRole.PassThrough)
			{
				continue;
			}

			long id = GetElementIdValue(((Element)part).Id);
			graph[id] = new FabDimGraphNode
			{
				Part = part,
				PartId = id,
				Role = role,
				Connectors = ListConnectors(part)
			};
		}

		foreach (FabDimGraphNode node in graph.Values)
		{
			foreach (Connector connector in node.Connectors)
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				FabricationPart mate = FindMateAtConnectorSkippingPassThrough(node.Part, connector, pool, out Connector mateConnector);
				if (mate == null)
				{
					continue;
				}

				long mateId = GetElementIdValue(((Element)mate).Id);
				if (!graph.TryGetValue(mateId, out FabDimGraphNode mateNode))
				{
					continue;
				}

				if (node.EdgesByMateId.ContainsKey(mateId))
				{
					continue;
				}

				FabDimGraphEdge edge = new FabDimGraphEdge
				{
					Self = node,
					SelfConnector = connector,
					Mate = mateNode,
					MateConnector = mateConnector
				};
				node.EdgesByMateId[mateId] = edge;
				if (!mateNode.EdgesByMateId.ContainsKey(node.PartId))
				{
					mateNode.EdgesByMateId[node.PartId] = new FabDimGraphEdge
					{
						Self = mateNode,
						SelfConnector = mateConnector,
						Mate = node,
						MateConnector = connector
					};
				}
			}
		}

		return graph;
	}

	private static FabricationPart FindMateAtConnectorSkippingPassThrough(
		FabricationPart self,
		Connector connector,
		IList<FabricationPart> pool,
		out Connector mateConnector)
	{
		mateConnector = null;
		FabricationPart mate = FindMateAtConnector(self, connector, pool);
		if (mate == null)
		{
			mate = FindMateAtConnectorViaAllRefs(self, connector, pool, out mateConnector);
			if (mate == null)
			{
				return null;
			}
		}
		else
		{
			mateConnector = FindClosestConnector(mate, connector.Origin);
		}

		HashSet<long> visited = new HashSet<long> { GetElementIdValue(((Element)self).Id) };
		int guard = 0;
		while (mate != null && IsAutoDimGraphPassThroughPart(mate) && guard++ < 8)
		{
			long mid = GetElementIdValue(((Element)mate).Id);
			if (!visited.Add(mid))
			{
				return null;
			}

			FabricationPart beyond = null;
			Connector beyondConnector = null;
			foreach (Connector c in ListConnectors(mate))
			{
				if (c?.Origin == null)
				{
					continue;
				}

				if (mateConnector != null && c.Origin.DistanceTo(mateConnector.Origin) < FabDimConnectorMateTolFeet)
				{
					continue;
				}

				FabricationPart candidate = FindMateAtConnector(mate, c, pool);
				if (candidate == null)
				{
					candidate = FindMateAtConnectorViaAllRefs(mate, c, pool, out _);
				}

				if (candidate == null || visited.Contains(GetElementIdValue(((Element)candidate).Id)))
				{
					continue;
				}

				beyond = candidate;
				beyondConnector = FindClosestConnector(candidate, c.Origin);
				break;
			}

			mate = beyond;
			mateConnector = beyondConnector;
		}

		if (mate != null && IsAutoDimGraphPassThroughPart(mate))
		{
			return null;
		}

		return mate;
	}

	private static FabricationPart FindMateAtConnectorViaAllRefs(
		FabricationPart self,
		Connector connector,
		IList<FabricationPart> pool,
		out Connector mateConnector)
	{
		mateConnector = null;
		if (self == null || connector == null || pool == null)
		{
			return null;
		}

		try
		{
			ConnectorSet refs = connector.AllRefs;
			if (refs == null)
			{
				return null;
			}

			long selfId = GetElementIdValue(((Element)self).Id);
			foreach (Connector other in refs)
			{
				if (other?.Owner == null)
				{
					continue;
				}

				Element owner = other.Owner;
				if (GetElementIdValue(owner.Id) == selfId)
				{
					continue;
				}

				FabricationPart fab = owner as FabricationPart;
				if (fab == null)
				{
					continue;
				}

				if (!pool.Any(p => p != null && GetElementIdValue(((Element)p).Id) == GetElementIdValue(owner.Id)))
				{
					continue;
				}

				mateConnector = other;
				return fab;
			}
		}
		catch
		{
		}

		return null;
	}

	private static Connector FindClosestConnector(FabricationPart part, XYZ origin)
	{
		if (part == null || origin == null)
		{
			return null;
		}

		Connector best = null;
		double bestDist = double.MaxValue;
		foreach (Connector c in ListConnectors(part))
		{
			if (c?.Origin == null)
			{
				continue;
			}

			double d = c.Origin.DistanceTo(origin);
			if (d < bestDist)
			{
				bestDist = d;
				best = c;
			}
		}

		return best;
	}

	/// <summary>True when a pipe connector has a fabrication mate (not genuinely open).</summary>
	private static bool IsPipeConnectorConnected(FabricationPart pipe, Connector connector, IList<FabricationPart> pool)
	{
		if (pipe == null || connector?.Origin == null)
		{
			return false;
		}

		FabricationPart mate = FindMateAtConnector(pipe, connector, pool)
			?? FindMateAtConnectorViaAllRefs(pipe, connector, pool, out _);
		return mate != null && !IsAutoDimGraphPassThroughPart(mate);
	}

	private static bool HasAnyOpenPipeConnector(FabricationPart pipe, IList<FabricationPart> pool)
	{
		if (pipe == null)
		{
			return false;
		}

		foreach (Connector c in ListConnectors(pipe))
		{
			if (c?.Origin == null)
			{
				continue;
			}

			if (!IsPipeConnectorConnected(pipe, c, pool))
			{
				return true;
			}
		}

		return false;
	}

	#endregion

	#region Intelligent fabrication dim — runs

	private static List<FabDimRun> WalkFabDimRuns(
		Dictionary<long, FabDimGraphNode> graph,
		IList<FabricationPart> pool,
		XYZ viewNormal,
		XYZ unitAxis,
		XYZ right,
		XYZ up)
	{
		List<FabDimRun> runs = new List<FabDimRun>();
		HashSet<string> seenKeys = new HashSet<string>(StringComparer.Ordinal);
		List<(FabDimGraphNode node, Connector open)> opens = FindFabDimOpenConnectors(graph);

		foreach ((FabDimGraphNode node, Connector open) in opens)
		{
			FabDimRun run = TraceFabDimRun(node, open, graph);
			if (run == null || run.OrderedParts.Count < 1)
			{
				continue;
			}

			string key = BuildFabDimRunKey(run);
			if (!seenKeys.Add(key))
			{
				continue;
			}

			run.DominantAxisInView = EstimateRunDominantAxisInView(run, viewNormal, right, up, unitAxis);
			run.LengthFeet = EstimateRunLength(run);
			runs.Add(run);
		}

		// Fallback: longest pipe chain when no open ends (closed spool / flange-capped).
		if (runs.Count == 0)
		{
			FabDimGraphNode seed = graph.Values
				.Where(n => n.Role == FabDimPartRole.FlangeFace || n.Role == FabDimPartRole.EndCapLike || n.Role == FabDimPartRole.ElbowLike)
				.OrderByDescending(n => n.EdgesByMateId.Count)
				.FirstOrDefault()
				?? graph.Values.FirstOrDefault();
			if (seed != null)
			{
				FabDimRun run = TraceFabDimRun(seed, seed.Connectors.FirstOrDefault(), graph);
				if (run != null && run.OrderedParts.Count >= 1)
				{
					run.DominantAxisInView = EstimateRunDominantAxisInView(run, viewNormal, right, up, unitAxis);
					run.LengthFeet = EstimateRunLength(run);
					runs.Add(run);
				}
			}
		}

		return runs.OrderByDescending(r => r.LengthFeet).ToList();
	}

	private static List<(FabDimGraphNode node, Connector open)> FindFabDimOpenConnectors(Dictionary<long, FabDimGraphNode> graph)
	{
		List<(FabDimGraphNode, Connector)> opens = new List<(FabDimGraphNode, Connector)>();
		foreach (FabDimGraphNode node in graph.Values)
		{
			foreach (Connector connector in node.Connectors)
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				bool mated = false;
				foreach (FabDimGraphEdge edge in node.EdgesByMateId.Values)
				{
					if (edge.SelfConnector?.Origin != null
						&& edge.SelfConnector.Origin.DistanceTo(connector.Origin) < FabDimConnectorMateTolFeet)
					{
						mated = true;
						break;
					}
				}

				if (!mated)
				{
					opens.Add((node, connector));
				}
			}
		}

		return opens;
	}

	private static FabDimRun TraceFabDimRun(
		FabDimGraphNode seed,
		Connector entryOpen,
		Dictionary<long, FabDimGraphNode> graph)
	{
		FabDimRun run = new FabDimRun();
		HashSet<long> visited = new HashSet<long>();
		FabDimGraphNode current = seed;
		Connector enteredVia = entryOpen;
		int guard = 0;

		while (current != null && visited.Add(current.PartId) && guard++ < 256)
		{
			run.OrderedParts.Add(current.Part);

			FabDimGraphEdge nextEdge = null;
			foreach (FabDimGraphEdge edge in current.EdgesByMateId.Values)
			{
				if (edge.Mate == null || visited.Contains(edge.Mate.PartId))
				{
					continue;
				}

				if (enteredVia != null
					&& edge.SelfConnector?.Origin != null
					&& enteredVia.Origin != null
					&& edge.SelfConnector.Origin.DistanceTo(enteredVia.Origin) < FabDimConnectorMateTolFeet)
				{
					continue;
				}

				// Prefer continuing through pipe / fittings; defer olet branch exits.
				if (edge.Mate.Role == FabDimPartRole.Olet)
				{
					continue;
				}

				nextEdge = edge;
				break;
			}

			if (nextEdge == null)
			{
				foreach (FabDimGraphEdge edge in current.EdgesByMateId.Values)
				{
					if (edge.Mate != null && !visited.Contains(edge.Mate.PartId) && edge.Mate.Role != FabDimPartRole.Olet)
					{
						nextEdge = edge;
						break;
					}
				}
			}

			if (nextEdge == null)
			{
				break;
			}

			enteredVia = nextEdge.MateConnector;
			current = nextEdge.Mate;
		}

		return run;
	}

	private static void DesignateMainRun(List<FabDimRun> runs)
	{
		if (runs == null || runs.Count == 0)
		{
			return;
		}

		FabDimRun main = runs.OrderByDescending(r => r.LengthFeet).First();
		foreach (FabDimRun run in runs)
		{
			run.IsMainRun = ReferenceEquals(run, main);
			run.IsBranch = !run.IsMainRun;
		}
	}

	private static string BuildFabDimRunKey(FabDimRun run)
	{
		if (run?.OrderedParts == null || run.OrderedParts.Count == 0)
		{
			return string.Empty;
		}

		List<long> ids = run.OrderedParts
			.Where(p => p != null)
			.Select(p => GetElementIdValue(((Element)p).Id))
			.ToList();
		string forward = string.Join("-", ids);
		ids.Reverse();
		string reverse = string.Join("-", ids);
		return string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse;
	}

	private static double EstimateRunLength(FabDimRun run)
	{
		if (run?.OrderedParts == null || run.OrderedParts.Count < 2)
		{
			return run?.OrderedParts?.Sum(p => GetFabricationStraightLineLength(p)) ?? 0;
		}

		XYZ first = TryGetFabricationPartOrigin(run.OrderedParts[0]);
		XYZ last = TryGetFabricationPartOrigin(run.OrderedParts[run.OrderedParts.Count - 1]);
		if (first != null && last != null)
		{
			return first.DistanceTo(last);
		}

		return run.OrderedParts.Sum(p => GetFabricationStraightLineLength(p));
	}

	private static XYZ EstimateRunDominantAxisInView(FabDimRun run, XYZ viewNormal, XYZ right, XYZ up, XYZ unitAxis)
	{
		XYZ hint = unitAxis;
		if (run?.OrderedParts != null)
		{
			foreach (FabricationPart p in run.OrderedParts)
			{
				if (p != null && IsPipeRunPart(p) && TryGetFabricationLineDirection(p, out XYZ dir) && dir != null)
				{
					hint = dir;
					break;
				}
			}
		}

		XYZ projected = ProjectVectorToViewPlane(hint ?? XYZ.BasisX, viewNormal);
		if (projected == null || projected.GetLength() < 1E-09)
		{
			return right;
		}

		projected = projected.Normalize();
		double alongR = Math.Abs(projected.DotProduct(right));
		double alongU = Math.Abs(projected.DotProduct(up));
		return alongR >= alongU ? right : up;
	}

	#endregion

	#region Intelligent fabrication dim — functional anchors

	private static void ResolveFabDimFunctionalAnchors(
		FabDimRun run,
		Dictionary<long, FabDimGraphNode> graph,
		IList<FabricationPart> pool)
	{
		run.Anchors = new List<FabDimFunctionalAnchor>();
		if (run?.OrderedParts == null)
		{
			return;
		}

		FabricationPart primaryRun = run.OrderedParts.FirstOrDefault(p => p != null && IsPipeRunPart(p));
		run.HostPipeForOlets = primaryRun;

		int order = 0;
		for (int i = 0; i < run.OrderedParts.Count; i++)
		{
			FabricationPart part = run.OrderedParts[i];
			if (part == null)
			{
				continue;
			}

			long id = GetElementIdValue(((Element)part).Id);
			FabDimPartRole role = graph.TryGetValue(id, out FabDimGraphNode node)
				? node.Role
				: ClassifyFabDimPartRole(part, pool);

			// Never auto-anchor reducers / couplings / valves unless needed elsewhere.
			if (role == FabDimPartRole.ReducerLike
				|| role == FabDimPartRole.CouplingLike
				|| role == FabDimPartRole.ValveLike
				|| role == FabDimPartRole.PassThrough
				|| role == FabDimPartRole.Unknown)
			{
				continue;
			}

			// Pipes: only genuinely open ends (resolved below); skip mid-run pipe bodies.
			if (role == FabDimPartRole.PipeLike)
			{
				TryAddOpenPipeEndAnchors(run, part, pool, order);
				order++;
				continue;
			}

			// Olets are planned separately as locations on host — skip here.
			if (role == FabDimPartRole.Olet)
			{
				continue;
			}

			FabDimAnchorKind kind;
			XYZ point = ResolveFunctionalAnchorPoint(part, role, primaryRun, pool, out kind);
			if (point == null || kind == FabDimAnchorKind.None)
			{
				LogFabDimEngineFile(
					"REJECTED purpose=n/a ids=" + id
					+ " anchorType=" + role
					+ " reason=fitting-center-unresolved (no connected-pipe-end substitute)");
				continue;
			}

			run.Anchors.Add(new FabDimFunctionalAnchor
			{
				Part = part,
				Role = role,
				Kind = kind,
				Point = point,
				OwnerElement = (Element)part,
				IsOpenPipeEnd = false,
				IsOlet = false,
				OrderIndex = order++
			});
		}

		run.Anchors = run.Anchors.OrderBy(a => a.OrderIndex).ToList();
	}

	private static void TryAddOpenPipeEndAnchors(
		FabDimRun run,
		FabricationPart pipe,
		IList<FabricationPart> pool,
		int order)
	{
		foreach (Connector c in ListConnectors(pipe))
		{
			if (c?.Origin == null)
			{
				continue;
			}

			if (IsPipeConnectorConnected(pipe, c, pool))
			{
				continue; // connected pipe ends are NEVER anchors
			}

			run.Anchors.Add(new FabDimFunctionalAnchor
			{
				Part = pipe,
				Role = FabDimPartRole.PipeLike,
				Kind = FabDimAnchorKind.OpenPipeEnd,
				Point = c.Origin,
				OwnerElement = (Element)pipe,
				IsOpenPipeEnd = true,
				IsOlet = false,
				OrderIndex = order
			});
		}
	}

	private static XYZ ResolveFunctionalAnchorPoint(
		FabricationPart part,
		FabDimPartRole role,
		FabricationPart primaryRun,
		IList<FabricationPart> pool,
		out FabDimAnchorKind kind)
	{
		kind = FabDimAnchorKind.None;
		if (part == null)
		{
			return null;
		}

		switch (role)
		{
			case FabDimPartRole.FlangeFace:
			{
				XYZ pt = TryGetFlangeBaseAnchorPoint(part, primaryRun, pool);
				if (pt == null)
				{
					return null;
				}

				kind = FabDimAnchorKind.FlangeOuterFace;
				return pt;
			}
			case FabDimPartRole.EndCapLike:
			{
				XYZ pt = TryGetFlangeBaseAnchorPoint(part, primaryRun, pool)
					?? ResolveOuterFaceFallback(part, primaryRun);
				if (pt == null)
				{
					return null;
				}

				kind = FabDimAnchorKind.EndCapOuterFace;
				return pt;
			}
			case FabDimPartRole.ElbowLike:
			{
				// Hard rule: elbow snap = pipe centerline intersection (the L "dot"), never body/avg.
				XYZ pt = ResolveUniversalCenterlineIntersectionAnchor(part, primaryRun, pool);
				if (pt == null)
				{
					return null; // SKIP — never substitute connected pipe end or body centroid
				}

				kind = FabDimAnchorKind.ElbowCenter;
				return pt;
			}
			case FabDimPartRole.TeeLike:
			case FabDimPartRole.CrossLike:
			{
				XYZ pt = ResolveUniversalCenterlineIntersectionAnchor(part, primaryRun, pool)
					?? GetFabricationFittingDimensionAnchor(part, primaryRun, null, pool);
				if (pt == null)
				{
					return null;
				}

				kind = FabDimAnchorKind.TeeCenter;
				return pt;
			}
			default:
				return null;
		}
	}

	private static XYZ ResolveOuterFaceFallback(FabricationPart part, FabricationPart towardMate)
	{
		XYZ hint = towardMate != null ? TryGetFabricationPartOrigin(towardMate) : null;
		XYZ partOrigin = TryGetFabricationPartOrigin(part);
		Connector best = null;
		double bestDist = -1;
		foreach (Connector c in ListConnectors(part))
		{
			if (c?.Origin == null)
			{
				continue;
			}

			// Without a mate hint, rank by distance from part origin — never assign d=0 to every
			// connector (that previously kept overwriting and landed on the back face).
			double d = hint != null
				? c.Origin.DistanceTo(hint)
				: (partOrigin != null ? c.Origin.DistanceTo(partOrigin) : 0);
			if (d > bestDist)
			{
				bestDist = d;
				best = c;
			}
		}

		return best?.Origin;
	}

	/// <summary>
	/// Flange / blind / end-cap face anchor. Wraps raised-face helper.
	/// </summary>
	private static XYZ TryGetFlangeBaseAnchorPoint(
		FabricationPart flange,
		FabricationPart towardMate,
		IList<FabricationPart> partsPool)
	{
		if (flange == null)
		{
			return null;
		}

		Document doc = ((Element)flange).Document;
		if (!FabricationPartClassification.IsFlangePart(flange, doc) && !IsFabDimEndCapOrBlind(flange))
		{
			return null;
		}

		// HARD RULE: always the OUTER face middle node on first try.
		// Never park on the back / pipe-joint and "push out" afterward.
		XYZ outer = ResolveFlangeOuterFaceCenterPoint(flange, towardMate, partsPool);
		if (outer != null)
		{
			return outer;
		}

		return ResolveOuterFaceFallback(flange, towardMate);
	}

	/// <summary>
	/// Outer-face center = unmated/open flange connector when present; else connector farthest from the
	/// mated pipe-side connector. This is the middle node on the face (idx ~18), not the back joint.
	/// </summary>
	private static XYZ ResolveFlangeOuterFaceCenterPoint(
		FabricationPart flange,
		FabricationPart towardMate,
		IList<FabricationPart> partsPool)
	{
		if (flange == null)
		{
			return null;
		}

		List<Connector> connectors = ListConnectors(flange).Where(c => c?.Origin != null).ToList();
		if (connectors.Count == 0)
		{
			return null;
		}

		// Classify connectors: pipe/weld side = BACK; open or gasket-only side = OUTER FACE middle.
		// Thin Flange Adapters: BOTH connectors can fall inside mate-tol of the same pipe end.
		// In that case closest-to-pipe = BACK, farthest-from-pipe = OUTER FACE (never last-wins).
		Connector pipeSide = null;
		Connector faceSide = null;
		if (partsPool != null)
		{
			List<Connector> pipeMated = new List<Connector>();
			foreach (Connector c in connectors)
			{
				FabricationPart mate = FindMateAtConnector(flange, c, partsPool)
					?? FindMateAtConnectorViaAllRefs(flange, c, partsPool, out _);
				if (mate == null)
				{
					faceSide = faceSide ?? c;
					continue;
				}

				if (IsGasketPart(mate))
				{
					faceSide = c;
					continue;
				}

				if (IsPipeRunPart(mate) || IsWeldPart(mate) || (towardMate != null && mate.Id == towardMate.Id))
				{
					pipeMated.Add(c);
				}
			}

			if (pipeMated.Count >= 2)
			{
				XYZ pipeJoint = null;
				double bestJoint = double.MaxValue;
				foreach (Connector c in pipeMated)
				{
					FabricationPart mate = FindMateAtConnector(flange, c, partsPool)
						?? FindMateAtConnectorViaAllRefs(flange, c, partsPool, out _);
					if (mate == null)
					{
						continue;
					}

					Connector mateConn = FindClosestConnector(mate, c.Origin);
					XYZ joint = mateConn?.Origin ?? TryGetFabricationPartOrigin(mate);
					if (joint == null)
					{
						continue;
					}

					double d = c.Origin.DistanceTo(joint);
					if (d < bestJoint)
					{
						bestJoint = d;
						pipeJoint = joint;
					}
				}

				if (pipeJoint != null)
				{
					Connector closest = null;
					Connector farthest = null;
					double minD = double.MaxValue;
					double maxD = -1;
					foreach (Connector c in pipeMated)
					{
						double d = c.Origin.DistanceTo(pipeJoint);
						if (d < minD)
						{
							minD = d;
							closest = c;
						}

						if (d > maxD)
						{
							maxD = d;
							farthest = c;
						}
					}

					pipeSide = closest;
					if (farthest != null
						&& closest != null
						&& farthest.Origin.DistanceTo(closest.Origin) > 1e-9)
					{
						faceSide = faceSide ?? farthest;
					}
				}
				else
				{
					pipeSide = pipeMated[0];
				}
			}
			else if (pipeMated.Count == 1)
			{
				pipeSide = pipeMated[0];
			}
		}

		if (faceSide != null && (pipeSide == null || faceSide.Origin.DistanceTo(pipeSide.Origin) > 1e-9))
		{
			return faceSide.Origin;
		}

		// Resolve inboard (pipe-joint / BACK). Never treat a face gasket as inboard.
		XYZ inboard = pipeSide?.Origin;
		if (inboard == null && towardMate != null && partsPool != null)
		{
			inboard = TryMatedConnectorOriginTowardPart(flange, towardMate, partsPool)
				?? TryMatedConnectorOriginTowardPartThroughJoints(flange, towardMate, partsPool);
		}
		if (inboard == null && towardMate != null)
		{
			inboard = TryGetFabricationPartOrigin(towardMate);
		}

		List<Connector> open = new List<Connector>();
		foreach (Connector c in connectors)
		{
			if (!IsPipeConnectorConnected(flange, c, partsPool))
			{
				open.Add(c);
			}
		}

		// Open/unmated = OUTER face middle node. Prefer farthest from inboard/pipe mate.
		if (open.Count > 0)
		{
			if (inboard != null)
			{
				Connector bestOpen = null;
				double bestDist = -1;
				foreach (Connector c in open)
				{
					double d = c.Origin.DistanceTo(inboard);
					if (d > bestDist)
					{
						bestDist = d;
						bestOpen = c;
					}
				}
				return bestOpen?.Origin;
			}

			return open[open.Count - 1].Origin;
		}

		// Both connected: connector farthest from the pipe-side / inboard mate = outer face.
		if (inboard != null)
		{
			Connector best = null;
			double bestDist = -1;
			foreach (Connector c in connectors)
			{
				double d = c.Origin.DistanceTo(inboard);
				if (d > bestDist)
				{
					bestDist = d;
					best = c;
				}
			}
			return best?.Origin;
		}

		return null;
	}

	/// <summary>
	/// Remove connected pipe ends and any residual internal-joint anchors after resolution.
	/// </summary>
	private static void SuppressNonFunctionalAnchors(
		FabDimRun run,
		Dictionary<long, FabDimGraphNode> graph,
		IList<FabricationPart> pool,
		string assemblyName,
		string viewLabel)
	{
		if (run?.Anchors == null)
		{
			return;
		}

		List<FabDimFunctionalAnchor> kept = new List<FabDimFunctionalAnchor>();
		foreach (FabDimFunctionalAnchor a in run.Anchors)
		{
			if (a == null || a.Point == null || a.OwnerElement == null)
			{
				continue;
			}

			if (a.Kind == FabDimAnchorKind.OpenPipeEnd)
			{
				if (!a.IsOpenPipeEnd || !HasAnyOpenPipeConnector(a.Part, pool))
				{
					LogFabDimDiag(
						assemblyName,
						viewLabel,
						"REJECTED purpose=OpenEndLocation ids=" + GetElementIdValue(a.OwnerElement.Id)
						+ " anchorTypes=OpenPipeEnd reason=connected-pipe-end-suppressed",
						0,
						0);
					continue;
				}

				// Re-verify each open end is still unmated.
				bool stillOpen = false;
				foreach (Connector c in ListConnectors(a.Part))
				{
					if (c?.Origin != null
						&& c.Origin.DistanceTo(a.Point) < FabDimConnectorMateTolFeet
						&& !IsPipeConnectorConnected(a.Part, c, pool))
					{
						stillOpen = true;
						break;
					}
				}

				if (!stillOpen)
				{
					LogFabDimDiag(
						assemblyName,
						viewLabel,
						"REJECTED purpose=OpenEndLocation ids=" + GetElementIdValue(a.OwnerElement.Id)
						+ " anchorTypes=OpenPipeEnd reason=connected-pipe-end-suppressed",
						0,
						0);
					continue;
				}
			}

			kept.Add(a);
		}

		run.Anchors = kept;
	}

	#endregion

	#region Intelligent fabrication dim — planner

	private static void PlanOletLocationIntents(
		List<FabDimRun> runs,
		Dictionary<long, FabDimGraphNode> graph,
		IList<FabricationPart> pool,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> intents,
		HashSet<string> seen,
		string assemblyName,
		string viewLabel)
	{
		if (graph == null)
		{
			return;
		}

		// Group olets by their straight host pipe — each host has its own short-end baseline.
		Dictionary<long, List<(FabricationPart olet, XYZ station)>> byHost =
			new Dictionary<long, List<(FabricationPart, XYZ)>>();

		foreach (FabDimGraphNode node in graph.Values)
		{
			if (node.Role != FabDimPartRole.Olet || node.Part == null)
			{
				continue;
			}

			FabricationPart host = FindOletHostPipe(node, graph);
			if (host == null || !IsPipeRunPart(host))
			{
				LogFabDimDiag(assemblyName, viewLabel,
					"REJECTED purpose=OletLocation ids=" + node.PartId
					+ " reason=no-straight-host-pipe", 0, 0);
				continue;
			}

			XYZ station = ResolveOletCenterStationOnHost(node.Part, host, pool);
			if (station == null)
			{
				LogFabDimDiag(assemblyName, viewLabel,
					"REJECTED purpose=OletLocation ids=" + node.PartId
					+ " anchorTypes=OletMainStation reason=olet-center-station-unresolved", 0, 0);
				continue;
			}

			long hostId = GetElementIdValue(((Element)host).Id);
			if (!byHost.TryGetValue(hostId, out List<(FabricationPart, XYZ)> list))
			{
				list = new List<(FabricationPart, XYZ)>();
				byHost[hostId] = list;
			}

			list.Add((node.Part, station));
		}

		if (byHost.Count == 0)
		{
			return;
		}

		foreach (KeyValuePair<long, List<(FabricationPart olet, XYZ station)>> kv in byHost)
		{
			FabricationPart host = null;
			if (pool != null)
			{
				host = pool.FirstOrDefault(p => p != null && GetElementIdValue(((Element)p).Id) == kv.Key);
			}

			if (host == null && graph.TryGetValue(kv.Key, out FabDimGraphNode hostNode))
			{
				host = hostNode.Part;
			}

			if (host == null)
			{
				continue;
			}

			PlanOletLocationsForHostPipe(
				host,
				kv.Value,
				pool,
				view,
				viewNormal,
				right,
				up,
				intents,
				seen,
				assemblyName,
				viewLabel);
		}
	}

	/// <summary>
	/// Olet center station = point on the host pipe centerline at the olet takeoff.
	/// Prefer the host connector that mates to the olet (true tap on host CL).
	/// Thread-O-Lets often have parallel connectors, so axis-intersection returns null — fall back to
	/// projecting the olet connector nearest the host CL onto that CL.
	/// Never top of olet / outlet tip / body edge.
	/// </summary>
	private static XYZ ResolveOletCenterStationOnHost(
		FabricationPart olet,
		FabricationPart host,
		IList<FabricationPart> pool)
	{
		if (olet == null || host == null)
		{
			return null;
		}

		// 1) Host tap connector mated to this olet — already on the host CL.
		if (pool != null)
		{
			long oletId = GetElementIdValue(((Element)olet).Id);
			foreach (Connector hostConn in ListConnectors(host))
			{
				if (hostConn?.Origin == null)
				{
					continue;
				}

				FabricationPart mate = FindMateAtConnector(host, hostConn, pool);
				if (mate != null && GetElementIdValue(((Element)mate).Id) == oletId)
				{
					return hostConn.Origin;
				}
			}
		}

		// 2) Classic branch∩host intersection when the fitting exposes crossing axes.
		XYZ station = ResolveUniversalCenterlineIntersectionAnchor(olet, host, pool);
		if (station != null)
		{
			return ProjectPointOntoHostPipeCenterline(station, host);
		}

		// 3) Project the olet connector nearest the host CL onto the host CL.
		if (TryGetHostPipeEndPoints(host, out XYZ pipeStart, out XYZ pipeEnd, out XYZ pipeDir)
			&& pipeStart != null && pipeDir != null)
		{
			XYZ bestOrigin = null;
			double bestDist = double.MaxValue;
			foreach (Connector c in ListConnectors(olet))
			{
				if (c?.Origin == null)
				{
					continue;
				}

				double d = DistancePointToUnboundedLine(pipeStart, pipeDir, c.Origin);
				if (d < bestDist)
				{
					bestDist = d;
					bestOrigin = c.Origin;
				}
			}

			if (bestOrigin != null && bestDist <= 0.5)
			{
				return ProjectPointOntoHostPipeCenterline(bestOrigin, host);
			}
		}

		station = GetFabricationFittingDimensionAnchor(olet, host, null, pool);
		return ProjectPointOntoHostPipeCenterline(station, host);
	}

	private static XYZ ProjectPointOntoHostPipeCenterline(XYZ point, FabricationPart host)
	{
		if (point == null || host == null)
		{
			return point;
		}

		if (!TryGetHostPipeEndPoints(host, out XYZ pipeStart, out _, out XYZ pipeDir)
			|| pipeStart == null || pipeDir == null)
		{
			return point;
		}

		double t = (point - pipeStart).DotProduct(pipeDir);
		return pipeStart + pipeDir.Multiply(t);
	}

	/// <summary>
	/// Olet location: short end of the straight HOST PIPE (HostPipeEndForOlet → olet center).
	/// Flange faces are reserved for main-run flange→elbow dims — never the olet baseline.
	/// </summary>
	private static void PlanOletLocationsForHostPipe(
		FabricationPart host,
		List<(FabricationPart olet, XYZ station)> olets,
		IList<FabricationPart> pool,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> intents,
		HashSet<string> seen,
		string assemblyName,
		string viewLabel)
	{
		if (host == null || olets == null || olets.Count == 0)
		{
			return;
		}

		if (!TryGetHostPipeEndPoints(host, out XYZ pipeStart, out XYZ pipeEnd, out XYZ pipeDir)
			|| pipeStart == null || pipeEnd == null || pipeDir == null)
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=OletLocation hostPipe=" + GetElementIdValue(((Element)host).Id)
				+ " reason=host-pipe-ends-unresolved", 0, 0);
			return;
		}

		long hostId = GetElementIdValue(((Element)host).Id);

		// Group short-end: end closer to the olet group (min axis-distance to any olet; tie → smaller sum).
		double minFromStart = double.MaxValue;
		double minFromEnd = double.MaxValue;
		double sumFromStart = 0;
		double sumFromEnd = 0;
		foreach ((FabricationPart olet, XYZ station) o in olets)
		{
			double dStart = Math.Abs((o.station - pipeStart).DotProduct(pipeDir));
			double dEnd = Math.Abs((pipeEnd - o.station).DotProduct(pipeDir));
			minFromStart = Math.Min(minFromStart, dStart);
			minFromEnd = Math.Min(minFromEnd, dEnd);
			sumFromStart += dStart;
			sumFromEnd += dEnd;
		}

		bool useStart = minFromStart < minFromEnd - 1E-09
			|| (Math.Abs(minFromStart - minFromEnd) <= 1E-09 && sumFromStart <= sumFromEnd);
		XYZ shortPipeEnd = useStart ? pipeStart : pipeEnd;
		string baselineLabel = useStart ? "PipeStart" : "PipeEnd";

		// Host pipe dominant H/V in active view — lock olet dims to that axis.
		XYZ pipeInView = ProjectVectorToViewPlane(pipeDir, viewNormal) ?? pipeDir;
		bool alongRight = Math.Abs(pipeInView.DotProduct(right)) >= Math.Abs(pipeInView.DotProduct(up));
		XYZ measureAxis = alongRight ? right : up;
		XYZ stackDir = alongRight ? up : right; // horizontal pipe → stack above/below along Up

		FabDimFunctionalAnchor baseline = ResolveOletShortEndDraftingBaseline(host, shortPipeEnd, pool, out baselineLabel);
		if (baseline == null)
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=OletLocation hostPipe=" + hostId + " reason=short-end-baseline-unresolved", 0, 0);
			return;
		}

		XYZ baselinePoint = baseline.Point;

		List<(FabricationPart olet, XYZ station, double distFromBaseline, double distStart, double distEnd)> ordered = olets
			.Select(o =>
			{
				double dStart = Math.Abs((o.station - pipeStart).DotProduct(pipeDir));
				double dEnd = Math.Abs((pipeEnd - o.station).DotProduct(pipeDir));
				XYZ delta = ProjectVectorToViewPlane(o.station - baselinePoint, viewNormal) ?? (o.station - baselinePoint);
				double dist = Math.Abs(delta.DotProduct(measureAxis));
				return (o.olet, o.station, dist, dStart, dEnd);
			})
			.OrderBy(x => x.dist)
			.ToList();

		foreach (var item in ordered)
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"Purpose: OletLocation"
				+ " HostPipe: " + hostId
				+ " PipeStart: " + FormatXyz(pipeStart)
				+ " PipeEnd: " + FormatXyz(pipeEnd)
				+ " OletStation: " + FormatXyz(item.station)
				+ " DistanceFromStart: " + (item.distStart * 12.0).ToString("F2", CultureInfo.InvariantCulture) + " in"
				+ " DistanceFromEnd: " + (item.distEnd * 12.0).ToString("F2", CultureInfo.InvariantCulture) + " in"
				+ " SelectedBaseline: " + baselineLabel
				+ " From: " + baseline.Kind
				+ " To: OletCenterStation"
				+ " Axis: " + (alongRight ? "Horizontal" : "Vertical")
				+ " Side: " + (alongRight ? "Above" : "Right"),
				0, 0);

			// Hard guard: olet baseline must be HostPipeEndForOlet only (never flange/elbow/tee/cap).
			if (baseline.Kind != FabDimAnchorKind.HostPipeEndForOlet)
			{
				LogFabDimDiag(assemblyName, viewLabel,
					"REJECTED purpose=OletLocation From=" + baseline.Kind + " To=OletCenterStation"
					+ " reason=Olets may only be dimensioned from the short end of their host pipe.", 0, 0);
				continue;
			}

			// Olet locate: measure ALONG the host pipe; park the dim on the side the olet faces.
			// H host + facing user → Up. V host + in-plane facing → that L/R side.
			// V host + facing user → L/R discretionary at place (clear assembly, no dim contact).
			XYZ facing = ResolveOletBranchFacing(item.olet, host, item.station, pool);
			// End-on suppress is ONLY for vertical hosts (phantom riser olets into the view).
			// Horizontal-run olets must still locate along the pipe when visible on the sheet —
			// even if the branch faces the user (reads as a circle on the run).
			if (!alongRight && !IsOletBranchVisibleInView(facing, viewNormal))
			{
				LogFabDimDiag(assemblyName, viewLabel,
					"REJECTED purpose=OletLocation hostPipe=" + hostId
					+ " reason=olet-not-visible-in-view-branch-end-on-vertical-host", 0, 0);
				continue;
			}

			int? oletOffsetSign = ResolveOletLocationOffsetSign(
				facing, viewNormal, right, up, alongRight, out string sideLabel);

			LogFabDimDiag(assemblyName, viewLabel,
				"Purpose: OletLocation facing-side"
				+ " HostPipe: " + hostId
				+ " Axis: " + (alongRight ? "Horizontal" : "Vertical")
				+ " Side: " + sideLabel
				+ " OffsetSign: " + (oletOffsetSign.HasValue ? oletOffsetSign.Value.ToString(CultureInfo.InvariantCulture) : "discretionary"),
				0, 0);

			FabDimFunctionalAnchor oletAnchor = new FabDimFunctionalAnchor
			{
				Part = item.olet,
				Role = FabDimPartRole.Olet,
				Kind = FabDimAnchorKind.OletMainStation,
				Point = item.station,
				// Olet element (not host). Same-owner host+host made NewDimension return 0".
				OwnerElement = (Element)item.olet,
				IsOlet = true,
				IsHostPipeEndForOlet = false,
				IsOpenPipeEnd = false,
				OrderIndex = -1
			};

			TryAddFabDimIntent(
				intents,
				seen,
				baseline,
				oletAnchor,
				DimensionPurpose.OletLocation,
				belongsToMain: false,
				alongRight,
				stackDir,
				view,
				viewNormal,
				right,
				up,
				assemblyName,
				viewLabel,
				lockPreferredAxis: true,
				offsetSignOverride: oletOffsetSign);
		}
	}

	/// <summary>
	/// Branch direction the olet faces: free connector BasisZ / origin away from the host CL.
	/// Used to park the locate dim on the facing side (Top view: left-facing olet → left).
	/// </summary>
	private static XYZ ResolveOletBranchFacing(
		FabricationPart olet,
		FabricationPart host,
		XYZ station,
		IList<FabricationPart> pool)
	{
		if (olet == null || station == null)
		{
			return null;
		}

		TryGetHostPipeEndPoints(host, out _, out _, out XYZ pipeDir);
		pipeDir = pipeDir?.Normalize();

		long hostId = host != null ? GetElementIdValue(((Element)host).Id) : -1;
		XYZ best = null;
		double bestScore = -1.0;

		foreach (Connector c in ListConnectors(olet))
		{
			if (c?.Origin == null)
			{
				continue;
			}

			FabricationPart mate = pool != null ? FindMateAtConnector(olet, c, pool) : null;
			bool matesHost = mate != null && GetElementIdValue(((Element)mate).Id) == hostId;
			if (matesHost)
			{
				continue;
			}

			XYZ fromStation = c.Origin - station;
			if (pipeDir != null)
			{
				fromStation = fromStation - pipeDir.Multiply(fromStation.DotProduct(pipeDir));
			}

			XYZ dir = SafeConnectorDirection(c);
			if (dir != null && pipeDir != null)
			{
				dir = dir - pipeDir.Multiply(dir.DotProduct(pipeDir));
			}

			XYZ candidate = null;
			if (dir != null && dir.GetLength() > 1E-09)
			{
				candidate = dir.Normalize();
				// Point outward from host (same hemisphere as connector origin off CL).
				if (fromStation.GetLength() > 1E-09 && candidate.DotProduct(fromStation) < 0.0)
				{
					candidate = candidate.Negate();
				}
			}
			else if (fromStation.GetLength() > 1E-09)
			{
				candidate = fromStation.Normalize();
			}

			if (candidate == null)
			{
				continue;
			}

			double score = fromStation.GetLength();
			if (score > bestScore)
			{
				bestScore = score;
				best = candidate;
			}
		}

		if (best != null)
		{
			return best;
		}

		// Fallback: fitting anchor / instance origin projected off the host axis.
		XYZ tip = GetFabricationFittingDimensionAnchor(olet, host, null, pool);
		if (tip == null)
		{
			try { tip = ((LocationPoint)((Element)olet).Location)?.Point; } catch { tip = null; }
		}

		if (tip == null)
		{
			return null;
		}

		XYZ delta = tip - station;
		if (pipeDir != null)
		{
			delta = delta - pipeDir.Multiply(delta.DotProduct(pipeDir));
		}

		return delta.GetLength() > 1E-09 ? delta.Normalize() : null;
	}

	/// <summary>
	/// Olet dim offset sign from facing:
	/// - Horizontal host + facing user → Up (+1 along view Up).
	/// - Vertical host + in-plane facing → Left/Right of that facing.
	/// - Vertical host + facing user → null (place-time L/R pick, clear assembly, avoid other dims).
	/// </summary>
	private static int? ResolveOletLocationOffsetSign(
		XYZ facing,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		bool hostAlongRight,
		out string sideLabel)
	{
		sideLabel = hostAlongRight ? "Above" : "Right";
		if (facing == null || viewNormal == null || right == null || up == null)
		{
			return 1;
		}

		viewNormal = viewNormal.Normalize();
		right = right.Normalize();
		up = up.Normalize();

		double towardUser = -facing.DotProduct(viewNormal);
		XYZ inPlane = ProjectVectorToViewPlane(facing, viewNormal);
		double inPlaneLen = inPlane != null ? inPlane.GetLength() : 0.0;
		bool facingTowardUser = Math.Abs(towardUser) >= Math.Max(inPlaneLen, 1E-09) * 0.55
			&& towardUser > 0.0;

		if (hostAlongRight)
		{
			// Measure H → offset along Up only (sideways H offset collapses to 0").
			if (facingTowardUser || inPlaneLen < 1E-09)
			{
				sideLabel = "Above (facing user)";
				return 1;
			}

			int upSign = Math.Sign(inPlane.DotProduct(up));
			if (upSign == 0)
			{
				sideLabel = "Above";
				return 1;
			}

			sideLabel = upSign > 0 ? "Above" : "Below";
			return upSign;
		}

		// Vertical host → offset Left/Right along view Right.
		if (facingTowardUser || inPlaneLen < 1E-09)
		{
			sideLabel = "Left/Right (facing user, discretionary)";
			return null;
		}

		int lrSign = Math.Sign(inPlane.DotProduct(right));
		if (lrSign == 0)
		{
			sideLabel = "Left/Right (facing user, discretionary)";
			return null;
		}

		sideLabel = lrSign > 0 ? "Right (olet facing)" : "Left (olet facing)";
		return lrSign;
	}

	/// <summary>
	/// Short-end drafting baseline for olet: always HostPipeEndForOlet at the short pipe end
	/// (end of pipe → olet center). Flange faces are for main-run flange→elbow only.
	/// </summary>
	private static FabDimFunctionalAnchor ResolveOletShortEndDraftingBaseline(
		FabricationPart host,
		XYZ shortPipeEnd,
		IList<FabricationPart> pool,
		out string baselineLabel)
	{
		baselineLabel = "HostPipeEndForOlet";
		if (host == null || shortPipeEnd == null)
		{
			return null;
		}

		// Prefer a short stub/coupling mated at the short end when present — better Point refs than the
		// long host body, and keeps OwnerElement distinct from the olet.
		Element owner = (Element)host;
		if (pool != null)
		{
			foreach (Connector c in ListConnectors(host))
			{
				if (c?.Origin == null || c.Origin.DistanceTo(shortPipeEnd) > FabDimConnectorMateTolFeet)
				{
					continue;
				}

				FabricationPart mate = FindMateAtConnector(host, c, pool);
				if (mate == null || GetElementIdValue(((Element)mate).Id) == GetElementIdValue(((Element)host).Id))
				{
					continue;
				}

				Document doc = ((Element)mate).Document;
				// NEVER flange — olets dimension to end of PIPE only (short host end → olet center).
				if (FabricationPartClassification.IsFlangePart(mate, doc))
				{
					continue;
				}
				if (IsPipeRunPart(mate) || IsGasketPart(mate) || IsWeldPart(mate))
				{
					// Use the mated stub/pipelet for the reference owner when it's the short tip piece.
					double mateLen = 0;
					if (TryGetHostPipeEndPoints(mate, out XYZ ms, out XYZ me, out _) && ms != null && me != null)
					{
						mateLen = ms.DistanceTo(me);
					}
					if (mateLen <= FabDimShortFlangeMaxLengthFeet || IsGasketPart(mate) || IsWeldPart(mate))
					{
						owner = (Element)mate;
						baselineLabel = "HostPipeEndForOlet(pipeEnd)";
						break;
					}
				}
			}
		}

		return new FabDimFunctionalAnchor
		{
			Part = host,
			Role = FabDimPartRole.PipeLike,
			Kind = FabDimAnchorKind.HostPipeEndForOlet,
			Point = shortPipeEnd,
			OwnerElement = owner,
			IsOpenPipeEnd = false,
			IsHostPipeEndForOlet = true,
			IsOlet = false,
			OrderIndex = -1
		};
	}

	private static bool IsValidOletLocationBaseline(FabDimFunctionalAnchor a)
	{
		return a != null && a.Kind == FabDimAnchorKind.HostPipeEndForOlet;
	}

	private static bool IsFittingAnchorKind(FabDimAnchorKind kind)
	{
		return kind == FabDimAnchorKind.FlangeOuterFace
			|| kind == FabDimAnchorKind.ElbowCenter
			|| kind == FabDimAnchorKind.TeeCenter
			|| kind == FabDimAnchorKind.EndCapOuterFace
			|| kind == FabDimAnchorKind.FittingCenter;
	}

	private static string FormatXyz(XYZ p)
	{
		if (p == null)
		{
			return "null";
		}

		return "("
			+ p.X.ToString("F3", CultureInfo.InvariantCulture) + ","
			+ p.Y.ToString("F3", CultureInfo.InvariantCulture) + ","
			+ p.Z.ToString("F3", CultureInfo.InvariantCulture) + ")";
	}

	/// <summary>
	/// Two physical ends of a straight pipe segment + unit direction start→end.
	/// </summary>
	private static bool TryGetHostPipeEndPoints(
		FabricationPart host,
		out XYZ pipeStart,
		out XYZ pipeEnd,
		out XYZ pipeDirection)
	{
		pipeStart = null;
		pipeEnd = null;
		pipeDirection = null;
		if (host == null)
		{
			return false;
		}

		List<Connector> connectors = ListConnectors(host)
			.Where(c => c?.Origin != null)
			.OrderBy(c => c.Origin.X)
			.ThenBy(c => c.Origin.Y)
			.ThenBy(c => c.Origin.Z)
			.ToList();

		if (connectors.Count < 2)
		{
			return false;
		}

		// Prefer the two farthest connectors (true ends on a straight).
		Connector a = connectors[0];
		Connector b = connectors[0];
		double best = -1;
		for (int i = 0; i < connectors.Count; i++)
		{
			for (int j = i + 1; j < connectors.Count; j++)
			{
				double d = connectors[i].Origin.DistanceTo(connectors[j].Origin);
				if (d > best)
				{
					best = d;
					a = connectors[i];
					b = connectors[j];
				}
			}
		}

		pipeStart = a.Origin;
		pipeEnd = b.Origin;
		XYZ dir = pipeEnd - pipeStart;
		if (dir.GetLength() < 1E-09)
		{
			XYZ d0 = SafeConnectorDirection(a);
			if (d0 == null || d0.GetLength() < 1E-09)
			{
				return false;
			}

			pipeDirection = d0.Normalize();
			return true;
		}

		pipeDirection = dir.Normalize();
		return true;
	}

	private static FabricationPart FindOletHostPipe(FabDimGraphNode oletNode, Dictionary<long, FabDimGraphNode> graph)
	{
		foreach (FabDimGraphEdge edge in oletNode.EdgesByMateId.Values)
		{
			if (edge.Mate != null && edge.Mate.Role == FabDimPartRole.PipeLike)
			{
				return edge.Mate.Part;
			}
		}

		foreach (FabDimGraphEdge edge in oletNode.EdgesByMateId.Values)
		{
			if (edge.Mate != null && edge.Mate.Role != FabDimPartRole.Olet && IsPipeRunPart(edge.Mate.Part))
			{
				return edge.Mate.Part;
			}
		}

		foreach (FabDimGraphEdge edge in oletNode.EdgesByMateId.Values)
		{
			if (edge.Mate != null && edge.Mate.Role != FabDimPartRole.Olet)
			{
				return edge.Mate.Part;
			}
		}

		return null;
	}

	private static void PlanBranchIntents(
		List<FabDimRun> runs,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> intents,
		HashSet<string> seen,
		string assemblyName,
		string viewLabel)
	{
		foreach (FabDimRun run in runs.Where(r => r.IsBranch))
		{
			List<FabDimFunctionalAnchor> anchors = GetFunctionalAnchorsForSegments(run);
			if (anchors.Count < 2)
			{
				continue;
			}

			bool alongRight = PreferAlongRight(run, right, up);
			XYZ stackDir = alongRight ? up : right;

			for (int i = 0; i < anchors.Count - 1; i++)
			{
				TryAddFabDimIntent(
					intents, seen, anchors[i], anchors[i + 1],
					DimensionPurpose.BranchSegment, belongsToMain: false,
					alongRight, stackDir, view, viewNormal, right, up, assemblyName, viewLabel);
			}

			// Never branch-overall across elbows/tees — same foreshortened end-on trap as main.
			// Segments already cover consecutive anchors.
		}
	}

	private static void PlanMainSegmentIntents(
		List<FabDimRun> runs,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> intents,
		HashSet<string> seen,
		string assemblyName,
		string viewLabel)
	{
		FabDimRun main = runs?.FirstOrDefault(r => r.IsMainRun);
		if (main == null)
		{
			return;
		}

		List<FabDimFunctionalAnchor> anchors = GetFunctionalAnchorsForSegments(main);
		if (anchors.Count < 2)
		{
			return;
		}

		for (int i = 0; i < anchors.Count - 1; i++)
		{
			// Axis from THIS segment only — flange→elbow horizontal, elbow→top flange vertical.
			XYZ segDelta = anchors[i + 1].Point - anchors[i].Point;
			XYZ inPlane = ProjectVectorToViewPlane(segDelta, viewNormal) ?? segDelta;
			bool alongRight = Math.Abs(inPlane.DotProduct(right)) >= Math.Abs(inPlane.DotProduct(up));
			XYZ stackDir = alongRight ? up : right;

			TryAddFabDimIntent(
				intents, seen, anchors[i], anchors[i + 1],
				DimensionPurpose.MainRunSegment, belongsToMain: true,
				alongRight, stackDir, view, viewNormal, right, up, assemblyName, viewLabel);
		}

		// Open-end location dims: open end → nearest functional fitting/flange on the run.
		foreach (FabDimFunctionalAnchor open in main.Anchors.Where(a => a.Kind == FabDimAnchorKind.OpenPipeEnd))
		{
			FabDimFunctionalAnchor nearest = anchors
				.Where(a => a.Kind != FabDimAnchorKind.OpenPipeEnd)
				.OrderBy(a => a.Point.DistanceTo(open.Point))
				.FirstOrDefault();
			if (nearest == null)
			{
				continue;
			}

			XYZ segDelta = nearest.Point - open.Point;
			XYZ inPlane = ProjectVectorToViewPlane(segDelta, viewNormal) ?? segDelta;
			bool alongRight = Math.Abs(inPlane.DotProduct(right)) >= Math.Abs(inPlane.DotProduct(up));
			XYZ stackDir = alongRight ? up : right;

			TryAddFabDimIntent(
				intents, seen, open, nearest,
				DimensionPurpose.OpenEndLocation, belongsToMain: true,
				alongRight, stackDir, view, viewNormal, right, up, assemblyName, viewLabel);
		}
	}

	/// <summary>
	/// Boxed-out true-45: when a 45° elbow bend lies in the view plane, place equal-leg H and V
	/// offsets (facing flange ↔ opposite run flange, facing flange ↔ 45 elbow). Does not alter lessons.
	/// </summary>
	private static void PlanBoxedOut45Intents(
		List<FabDimRun> runs,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> intents,
		HashSet<string> seen,
		string assemblyName,
		string viewLabel)
	{
		if (runs == null || viewNormal == null || right == null || up == null)
		{
			return;
		}

		foreach (FabDimRun run in runs)
		{
			List<FabDimFunctionalAnchor> anchors = GetFunctionalAnchorsForSegments(run);
			if (anchors.Count < 3)
			{
				continue;
			}

			for (int i = 0; i < anchors.Count; i++)
			{
				FabDimFunctionalAnchor elbow = anchors[i];
				if (elbow?.Kind != FabDimAnchorKind.ElbowCenter || elbow.Part == null || elbow.Point == null)
				{
					continue;
				}

				if (!IsApproximately45ElbowPart(elbow.Part))
				{
					continue;
				}

				if (!IsElbowBendInViewPlane(elbow.Part, viewNormal))
				{
					LogFabDimDiag(assemblyName, viewLabel,
						"REJECTED purpose=BoxedOut45 ids=" + GetElementIdValue(((Element)elbow.Part).Id)
						+ " reason=45-bend-not-in-view-plane", 0, 0);
					continue;
				}

				FabDimFunctionalAnchor flangeBefore = null;
				for (int j = i - 1; j >= 0; j--)
				{
					if (anchors[j].Kind == FabDimAnchorKind.FlangeOuterFace
						|| anchors[j].Kind == FabDimAnchorKind.EndCapOuterFace)
					{
						flangeBefore = anchors[j];
						break;
					}
				}

				FabDimFunctionalAnchor flangeAfter = null;
				for (int j = i + 1; j < anchors.Count; j++)
				{
					if (anchors[j].Kind == FabDimAnchorKind.FlangeOuterFace
						|| anchors[j].Kind == FabDimAnchorKind.EndCapOuterFace)
					{
						flangeAfter = anchors[j];
						break;
					}
				}

				if (flangeBefore == null || flangeAfter == null)
				{
					LogFabDimDiag(assemblyName, viewLabel,
						"REJECTED purpose=BoxedOut45 ids=" + GetElementIdValue(((Element)elbow.Part).Id)
						+ " reason=need-flange-each-side-of-45", 0, 0);
					continue;
				}

				FabDimFunctionalAnchor facingFlange = PickViewFacingFlangeAnchor(
					flangeBefore, flangeAfter, viewNormal);
				FabDimFunctionalAnchor oppositeFlange =
					ReferenceEquals(facingFlange, flangeBefore) ? flangeAfter : flangeBefore;
				if (facingFlange == null || oppositeFlange == null)
				{
					continue;
				}

				XYZ stackH = up;
				XYZ stackV = right;

				// Outside corner of the true-45 box (opposite the kick) — sheet H + V only.
				// Example Front: kick up/right ⇒ H below with the run dim, V left at the assembly end.
				XYZ kick = ProjectVectorToViewPlane(facingFlange.Point - elbow.Point, viewNormal)
					?? (facingFlange.Point - elbow.Point);
				int boxSignAlongRight = kick.DotProduct(right) >= 0 ? -1 : 1;
				int boxSignAlongUp = kick.DotProduct(up) >= 0 ? -1 : 1;

				TryAddFabDimIntent(
					intents, seen, facingFlange, elbow,
					DimensionPurpose.BoxedOut45Horizontal, belongsToMain: run.IsMainRun,
					preferAlongRight: true, stackH, view, viewNormal, right, up, assemblyName, viewLabel,
					lockPreferredAxis: true,
					offsetSignOverride: boxSignAlongUp);

				TryAddFabDimIntent(
					intents, seen, facingFlange, elbow,
					DimensionPurpose.BoxedOut45Vertical, belongsToMain: run.IsMainRun,
					preferAlongRight: false, stackV, view, viewNormal, right, up, assemblyName, viewLabel,
					lockPreferredAxis: true,
					offsetSignOverride: boxSignAlongRight);

				LogFabDimDiag(assemblyName, viewLabel,
					"PLANNED purpose=BoxedOut45 pair"
					+ " elbow=" + GetElementIdValue(((Element)elbow.Part).Id)
					+ " facingFlange=" + GetElementIdValue(facingFlange.OwnerElement.Id)
					+ " oppositeFlange=" + GetElementIdValue(oppositeFlange.OwnerElement.Id)
					+ " hSign=" + boxSignAlongUp.ToString(CultureInfo.InvariantCulture)
					+ " vSign=" + boxSignAlongRight.ToString(CultureInfo.InvariantCulture),
					0, 0);
			}
		}
	}

	/// <summary>
	/// Sign along <paramref name="axis"/> that moves from <paramref name="from"/> toward <paramref name="toward"/>.
	/// </summary>
	private static int ResolveBoxedOutOffsetSign(XYZ from, XYZ toward, XYZ axis)
	{
		if (from == null || toward == null || axis == null || axis.GetLength() < 1E-09)
		{
			return 1;
		}

		double delta = (toward - from).DotProduct(axis.Normalize());
		if (Math.Abs(delta) < 1E-09)
		{
			return 1;
		}

		return delta > 0 ? 1 : -1;
	}

	/// <summary>
	/// Vertical-host only: branch nearly parallel to view direction ⇒ end-on into depth.
	/// Do not use this to suppress horizontal-run olet locate dims.
	/// </summary>
	private static bool IsOletBranchVisibleInView(XYZ branchFacing, XYZ viewNormal)
	{
		if (branchFacing == null || viewNormal == null
			|| branchFacing.GetLength() < 1E-09 || viewNormal.GetLength() < 1E-09)
		{
			return true;
		}

		double alongView = Math.Abs(branchFacing.Normalize().DotProduct(viewNormal.Normalize()));
		return alongView < 0.85;
	}

	private static bool IsApproximately45ElbowPart(FabricationPart elbow)
	{
		if (elbow == null)
		{
			return false;
		}

		List<Connector> connectors = ListConnectors(elbow)
			.Where(c => c?.Origin != null && c.CoordinateSystem != null)
			.ToList();
		if (connectors.Count < 2)
		{
			return false;
		}

		XYZ d0 = connectors[0].CoordinateSystem.BasisZ?.Normalize();
		XYZ d1 = connectors[1].CoordinateSystem.BasisZ?.Normalize();
		if (d0 == null || d1 == null)
		{
			return false;
		}

		double degrees = d0.AngleTo(d1) * 180.0 / Math.PI;
		// Fabrication 45 elbows expose ~135° between outward connector normals (or ~45°).
		return Math.Abs(degrees - 135.0) <= 12.0 || Math.Abs(degrees - 45.0) <= 12.0;
	}

	private static bool IsElbowBendInViewPlane(FabricationPart elbow, XYZ viewNormal)
	{
		if (elbow == null || viewNormal == null || viewNormal.GetLength() < 1E-09)
		{
			return false;
		}

		List<Connector> connectors = ListConnectors(elbow)
			.Where(c => c?.Origin != null && c.CoordinateSystem != null)
			.ToList();
		if (connectors.Count < 2)
		{
			return false;
		}

		XYZ d0 = ProjectVectorToViewPlane(connectors[0].CoordinateSystem.BasisZ, viewNormal);
		XYZ d1 = ProjectVectorToViewPlane(connectors[1].CoordinateSystem.BasisZ, viewNormal);
		if (d0 == null || d1 == null || d0.GetLength() < 0.35 || d1.GetLength() < 0.35)
		{
			return false;
		}

		double degrees = d0.Normalize().AngleTo(d1.Normalize()) * 180.0 / Math.PI;
		return Math.Abs(degrees - 45.0) <= 15.0 || Math.Abs(degrees - 135.0) <= 15.0;
	}

	private static FabDimFunctionalAnchor PickViewFacingFlangeAnchor(
		FabDimFunctionalAnchor a,
		FabDimFunctionalAnchor b,
		XYZ viewNormal)
	{
		double scoreA = ScoreFlangeFacingView(a, viewNormal);
		double scoreB = ScoreFlangeFacingView(b, viewNormal);
		if (scoreA < 0.35 && scoreB < 0.35)
		{
			// Neither faces the camera — prefer the flange closer to "up" in world as a stable pick.
			return a;
		}

		return scoreA >= scoreB ? a : b;
	}

	private static double ScoreFlangeFacingView(FabDimFunctionalAnchor flange, XYZ viewNormal)
	{
		if (flange?.Part == null || viewNormal == null)
		{
			return 0;
		}

		XYZ faceDir = TryGetFlangeOuterFaceDirection(flange.Part);
		if (faceDir == null || faceDir.GetLength() < 1E-09)
		{
			return 0;
		}

		return Math.Abs(faceDir.Normalize().DotProduct(viewNormal.Normalize()));
	}

	private static XYZ TryGetFlangeOuterFaceDirection(FabricationPart flange)
	{
		if (flange == null)
		{
			return null;
		}

		List<Connector> connectors = ListConnectors(flange).Where(c => c?.Origin != null).ToList();
		if (connectors.Count == 0)
		{
			return null;
		}

		// Prefer an unmated connector (true outer face); else farthest-from-centroid connector.
		foreach (Connector c in connectors)
		{
			bool mated = false;
			try
			{
				ConnectorSet refs = c.AllRefs;
				if (refs != null)
				{
					foreach (Connector r in refs)
					{
						if (r?.Owner != null && r.Owner.Id != ((Element)flange).Id)
						{
							mated = true;
							break;
						}
					}
				}
			}
			catch
			{
				mated = false;
			}

			if (!mated && c.CoordinateSystem != null)
			{
				return c.CoordinateSystem.BasisZ;
			}
		}

		XYZ origin = TryGetFabricationPartOrigin(flange);
		Connector best = null;
		double bestDist = -1;
		foreach (Connector c in connectors)
		{
			double d = origin != null ? c.Origin.DistanceTo(origin) : 0;
			if (d >= bestDist)
			{
				bestDist = d;
				best = c;
			}
		}

		return best?.CoordinateSystem?.BasisZ;
	}

	private static void PlanMainOverallIntents(
		List<FabDimRun> runs,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> intents,
		HashSet<string> seen,
		string assemblyName,
		string viewLabel)
	{
		FabDimRun main = runs?.FirstOrDefault(r => r.IsMainRun);
		if (main == null)
		{
			return;
		}

		List<FabDimFunctionalAnchor> anchors = GetFunctionalAnchorsForSegments(main);
		if (anchors.Count < 2)
		{
			return;
		}

		// Multi-anchor runs always use segment dims (open→elbow, elbow→flange, …).
		// Do NOT fall back to first→last overall when a corner foreshortens into the view
		// (end-on flange): multi-axis gate fails, and overall would jump open→flange and
		// skip the elbow center — producing a second nearly-identical vertical dim.
		if (anchors.Count > 2 || HasIntermediateElbowOrTee(anchors))
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=MainRunOverall reason=multi-anchor-run-use-segments-only", 0, 0);
			return;
		}

		// Skip L-shaped / multi-axis overall when only two anchors still span both axes.
		XYZ delta = anchors[anchors.Count - 1].Point - anchors[0].Point;
		XYZ inPlane = ProjectVectorToViewPlane(delta, viewNormal) ?? delta;
		double dH = Math.Abs(inPlane.DotProduct(right));
		double dV = Math.Abs(inPlane.DotProduct(up));
		if (dH >= FabDimMinLengthFeet && dV >= FabDimMinLengthFeet)
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=MainRunOverall reason=multi-axis-run-use-segments-only", 0, 0);
			return;
		}

		bool alongRight = PreferAlongRight(main, right, up);
		XYZ stackDir = alongRight ? up : right;

		TryAddFabDimIntent(
			intents, seen, anchors[0], anchors[anchors.Count - 1],
			DimensionPurpose.MainRunOverall, belongsToMain: true,
			alongRight, stackDir, view, viewNormal, right, up, assemblyName, viewLabel);
	}

	private static bool HasIntermediateElbowOrTee(List<FabDimFunctionalAnchor> anchors)
	{
		if (anchors == null || anchors.Count < 3)
		{
			return false;
		}

		for (int i = 1; i < anchors.Count - 1; i++)
		{
			FabDimAnchorKind kind = anchors[i].Kind;
			if (kind == FabDimAnchorKind.ElbowCenter || kind == FabDimAnchorKind.TeeCenter)
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsCornerCenterAnchorKind(FabDimAnchorKind kind)
	{
		return kind == FabDimAnchorKind.ElbowCenter || kind == FabDimAnchorKind.TeeCenter;
	}

	/// <summary>
	/// True when the 3D segment between anchors has meaningful depth along the view normal
	/// (run going away from / toward the user) — not a true length in this orthographic view.
	/// </summary>
	private static bool IsSegmentForeshortenedInView(XYZ a, XYZ b, XYZ viewNormal)
	{
		if (a == null || b == null || viewNormal == null || viewNormal.GetLength() < 1E-09)
		{
			return false;
		}

		XYZ delta = b - a;
		double length = delta.GetLength();
		if (length < FabDimMinLengthFeet)
		{
			return false;
		}

		double depth = Math.Abs(delta.DotProduct(viewNormal.Normalize()));
		return depth >= FabDimForeshortenedDepthMinFeet
			&& (depth / length) >= FabDimForeshortenedDepthRatio;
	}

	/// <summary>
	/// Functional anchors for segment/overall: flanges, elbows, tees, end caps, open ends.
	/// Never olets here (handled as OletLocation). Never internal pipe joints.
	/// </summary>
	private static List<FabDimFunctionalAnchor> GetFunctionalAnchorsForSegments(FabDimRun run)
	{
		if (run?.Anchors == null)
		{
			return new List<FabDimFunctionalAnchor>();
		}

		return run.Anchors
			.Where(a =>
				a != null
				&& a.Point != null
				&& (a.Kind == FabDimAnchorKind.FlangeOuterFace
					|| a.Kind == FabDimAnchorKind.ElbowCenter
					|| a.Kind == FabDimAnchorKind.TeeCenter
					|| a.Kind == FabDimAnchorKind.EndCapOuterFace
					|| a.Kind == FabDimAnchorKind.OpenPipeEnd))
			.OrderBy(a => a.OrderIndex)
			.ToList();
	}

	private static bool PreferAlongRight(FabDimRun run, XYZ right, XYZ up)
	{
		XYZ axis = run?.DominantAxisInView;
		if (axis == null)
		{
			return true;
		}

		return Math.Abs(axis.DotProduct(right)) >= Math.Abs(axis.DotProduct(up));
	}

	private static void TryAddFabDimIntent(
		List<FabDimIntent> intents,
		HashSet<string> seen,
		FabDimFunctionalAnchor a,
		FabDimFunctionalAnchor b,
		DimensionPurpose purpose,
		bool belongsToMain,
		bool preferAlongRight,
		XYZ stackDirHint,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		string assemblyName,
		string viewLabel,
		bool lockPreferredAxis = false,
		int? offsetSignOverride = null)
	{
		if (a?.Point == null || b?.Point == null || a.OwnerElement == null || b.OwnerElement == null)
		{
			return;
		}

		if (ReferenceEquals(a, b)
			|| a.OwnerElement.Id == b.OwnerElement.Id)
		{
			// Same element on both ends → Revit NewDimension returns 0". Never allow for OletLocation.
			if (purpose == DimensionPurpose.OletLocation)
			{
				LogFabDimDiag(assemblyName, viewLabel,
					"REJECTED purpose=OletLocation ids=" + DescribeAnchorPair(a, b)
					+ " reason=same-owner-would-create-zero-length-dim", 0, 0);
			}
			return;
		}

		// OletLocation: short-end baseline only — HostPipeEndForOlet → OletMainStation (end of pipe → olet center).
		if (purpose == DimensionPurpose.OletLocation)
		{
			FabDimFunctionalAnchor from = a.Kind == FabDimAnchorKind.OletMainStation ? b : a;
			FabDimFunctionalAnchor to = a.Kind == FabDimAnchorKind.OletMainStation ? a : b;
			bool validFrom = to.Kind == FabDimAnchorKind.OletMainStation
				&& from.Kind == FabDimAnchorKind.HostPipeEndForOlet;
			if (!validFrom
				|| from.Kind == FabDimAnchorKind.FlangeOuterFace
				|| from.Kind == FabDimAnchorKind.ElbowCenter
				|| from.Kind == FabDimAnchorKind.TeeCenter
				|| from.Kind == FabDimAnchorKind.FittingCenter
				|| from.Kind == FabDimAnchorKind.EndCapOuterFace)
			{
				LogFabDimDiag(assemblyName, viewLabel,
					"REJECTED purpose=OletLocation From=" + from.Kind + " To=" + to.Kind
					+ " reason=Olets may only be dimensioned from the short end of their host pipe.", 0, 0);
				return;
			}
		}
		else if (a.Kind == FabDimAnchorKind.HostPipeEndForOlet || b.Kind == FabDimAnchorKind.HostPipeEndForOlet)
		{
			// HostPipeEndForOlet is never a normal fabrication datum.
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=" + purpose + " ids=" + DescribeAnchorPair(a, b)
				+ " reason=host-pipe-end-for-olet-only", 0, 0);
			return;
		}

		// Never olet→olet chain.
		if (a.IsOlet && b.IsOlet)
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=" + purpose + " ids=" + DescribeAnchorPair(a, b)
				+ " reason=olet-to-olet-forbidden", 0, 0);
			return;
		}

		// Never dimension internal joints (pipe↔fitting when pipe end is connected).
		// HostPipeEndForOlet is excluded — it is the olet-location exception.
		if (IsInternalJointPair(a, b))
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=" + purpose + " ids=" + DescribeAnchorPair(a, b)
				+ " anchorTypes=" + a.Kind + "+" + b.Kind
				+ " reason=internal-joint-forbidden", 0, 0);
			return;
		}

		// Layered rule (does not change lessons): never corner→corner when the connecting
		// segment leaves the view plane — that dim is foreshortened / not a true length.
		if ((purpose == DimensionPurpose.MainRunSegment || purpose == DimensionPurpose.BranchSegment)
			&& IsCornerCenterAnchorKind(a.Kind)
			&& IsCornerCenterAnchorKind(b.Kind)
			&& IsSegmentForeshortenedInView(a.Point, b.Point, viewNormal))
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=" + purpose + " ids=" + DescribeAnchorPair(a, b)
				+ " anchorTypes=" + a.Kind + "+" + b.Kind
				+ " reason=foreshortened-corner-to-corner-not-true-length", 0, 0);
			return;
		}

		// Olet stations lock to host-pipe dominant H/V — never fall back to the orthogonal axis.
		bool forceAxis = lockPreferredAxis || purpose == DimensionPurpose.OletLocation;
		if (!TryPickViewAxisMeasurement(a.Point, b.Point, viewNormal, right, up, preferAlongRight,
			out bool alongRight, out double chosenDelta, out XYZ stackDir, lockPreferredAxis: forceAxis))
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=" + purpose + " ids=" + DescribeAnchorPair(a, b)
				+ " anchorTypes=" + a.Kind + "+" + b.Kind
				+ " reason=chosen-axis-too-short-or-degenerate", 0, 0);
			return;
		}

		long idA = GetElementIdValue(a.OwnerElement.Id);
		long idB = GetElementIdValue(b.OwnerElement.Id);
		string key = (idA <= idB ? idA + "|" + idB : idB + "|" + idA)
			+ "|" + purpose.ToString();
		if (!seen.Add(key))
		{
			LogFabDimDiag(assemblyName, viewLabel,
				"REJECTED purpose=" + purpose + " ids=" + DescribeAnchorPair(a, b)
				+ " reason=duplicate", 0, 0);
			return;
		}

		intents.Add(new FabDimIntent
		{
			A = a,
			B = b,
			Purpose = purpose,
			BelongsToMainRunGroup = belongsToMain,
			StackDirectionInView = stackDirHint ?? stackDir,
			MeasureAlongRight = alongRight,
			OffsetSignOverride = offsetSignOverride
		});

		LogFabDimDiag(assemblyName, viewLabel,
			"PLANNED purpose=" + purpose
			+ " ids=" + DescribeAnchorPair(a, b)
			+ " anchorTypes=" + a.Kind + "+" + b.Kind
			+ " axis=" + (alongRight ? "H" : "V")
			+ " delta=" + chosenDelta.ToString("F4", CultureInfo.InvariantCulture),
			0, 0);
	}

	private static bool IsInternalJointPair(FabDimFunctionalAnchor a, FabDimFunctionalAnchor b)
	{
		// Olet-location short-end exception (pipe end or flange-at-short-end) is never an internal joint.
		if ((a != null && a.IsHostPipeEndForOlet) || (b != null && b.IsHostPipeEndForOlet)
			|| (a != null && a.Kind == FabDimAnchorKind.HostPipeEndForOlet)
			|| (b != null && b.Kind == FabDimAnchorKind.HostPipeEndForOlet))
		{
			return false;
		}

		bool aPipeConnected = a.Role == FabDimPartRole.PipeLike && !a.IsOpenPipeEnd;
		bool bPipeConnected = b.Role == FabDimPartRole.PipeLike && !b.IsOpenPipeEnd;
		bool aFitting = IsFittingAnchorKind(a.Kind);
		bool bFitting = IsFittingAnchorKind(b.Kind);

		return (aPipeConnected && bFitting) || (bPipeConnected && aFitting);
	}

	/// <summary>
	/// Pick H or V from view axes. Require min length on the CHOSEN axis only
	/// (ignore the other so olet horizontal stations work with vertical offset).
	/// Never uses (B-A).Normalize() as dim line direction.
	/// When lockPreferredAxis is true (olet stations), never fall back to the orthogonal axis.
	/// Rejects multi-plane corner spans (both H and V significant) unless axis is locked.
	/// </summary>
	private static bool TryPickViewAxisMeasurement(
		XYZ a,
		XYZ b,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		bool preferAlongRight,
		out bool alongRight,
		out double chosenDelta,
		out XYZ stackDir,
		bool lockPreferredAxis = false)
	{
		alongRight = preferAlongRight;
		chosenDelta = 0;
		stackDir = preferAlongRight ? up : right;

		if (a == null || b == null || right == null || up == null)
		{
			return false;
		}

		XYZ delta = b - a;
		XYZ inPlane = ProjectVectorToViewPlane(delta, viewNormal) ?? delta;
		double deltaH = Math.Abs(inPlane.DotProduct(right));
		double deltaV = Math.Abs(inPlane.DotProduct(up));

		// Corner / two-plane span: both axes are fabrication-significant → never one diagonal dim.
		// Olet stations lock one axis and may have a small orthogonal offset (branch height).
		const double multiPlaneMinFeet = 3.0 / 12.0; // 3"
		if (!lockPreferredAxis && deltaH >= multiPlaneMinFeet && deltaV >= multiPlaneMinFeet)
		{
			return false;
		}

		if (preferAlongRight)
		{
			if (deltaH >= FabDimMinLengthFeet)
			{
				alongRight = true;
				chosenDelta = deltaH;
				stackDir = up;
				return true;
			}

			if (!lockPreferredAxis && deltaV >= FabDimMinLengthFeet)
			{
				alongRight = false;
				chosenDelta = deltaV;
				stackDir = right;
				return true;
			}
		}
		else
		{
			if (deltaV >= FabDimMinLengthFeet)
			{
				alongRight = false;
				chosenDelta = deltaV;
				stackDir = right;
				return true;
			}

			if (!lockPreferredAxis && deltaH >= FabDimMinLengthFeet)
			{
				alongRight = true;
				chosenDelta = deltaH;
				stackDir = up;
				return true;
			}
		}

		return false;
	}

	private static string DescribeAnchorPair(FabDimFunctionalAnchor a, FabDimFunctionalAnchor b)
	{
		long idA = a?.OwnerElement != null ? GetElementIdValue(a.OwnerElement.Id) : 0;
		long idB = b?.OwnerElement != null ? GetElementIdValue(b.OwnerElement.Id) : 0;
		return idA.ToString(CultureInfo.InvariantCulture) + "+" + idB.ToString(CultureInfo.InvariantCulture);
	}

	#endregion

	#region Intelligent fabrication dim — side + place

	private static int ResolveFabDimPreferredOffsetSign(
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimRun> runs,
		XYZ unitAxis)
	{
		FabDimRun branch = runs?.FirstOrDefault(r => r.IsBranch && r.DominantAxisInView != null);
		if (branch?.DominantAxisInView != null)
		{
			XYZ facing = ProjectVectorToViewPlane(branch.DominantAxisInView, viewNormal);
			if (facing != null && facing.GetLength() > 1E-09)
			{
				facing = facing.Normalize();
				double alongUp = facing.DotProduct(up);
				double alongRight = facing.DotProduct(right);
				if (Math.Abs(alongUp) >= Math.Abs(alongRight))
				{
					return alongUp >= 0 ? 1 : -1;
				}

				return alongRight >= 0 ? 1 : -1;
			}
		}

		FabDimRun main = runs?.FirstOrDefault(r => r.IsMainRun);
		if (main?.DominantAxisInView != null)
		{
			return ResolveSpoolDimensionPlacementOffsetSign(view, main.DominantAxisInView, 1, main.DominantAxisInView, viewNormal);
		}

		return 1;
	}

	private static int PlaceFabDimIntents(
		Document doc,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> intents,
		IList<FabricationPart> pool,
		SpoolingManagerSettings spoolSettings,
		int preferredSign,
		List<string> failureNotes,
		List<FabDimPlaceRecord> placedRecords,
		string assemblyName,
		string viewLabel)
	{
		List<FabDimIntent> oletGroup = intents.Where(i => i.Purpose == DimensionPurpose.OletLocation).ToList();
		List<FabDimIntent> mainGroup = intents.Where(i => i.BelongsToMainRunGroup && i.Purpose != DimensionPurpose.OletLocation).ToList();
		List<FabDimIntent> otherGroup = intents.Where(i => !i.BelongsToMainRunGroup && i.Purpose != DimensionPurpose.OletLocation).ToList();

		int placed = 0;
		int budget = FabDimMaxDimensionsPerView;

		// Shared per-side stack + slot-0 line position across olet/main/other.
		// Slot 0 clears origin parts only; slot N = that line + N × Dim Line Snap Distance.
		Dictionary<string, int> stackBySide = new Dictionary<string, int>(StringComparer.Ordinal);
		Dictionary<string, double> sideSlot0AbsPos = new Dictionary<string, double>(StringComparer.Ordinal);

		// Olets first (inner): facing-side park.
		int oletSign = ResolveOletGroupFacingSign(oletGroup, view, right, up, pool);
		placed += PlaceFabDimIntentGroup(
			doc, view, viewNormal, right, up, oletGroup, pool, spoolSettings, oletSign,
			flipWholeGroupOnFail: false, stackOutsideOletsOnVertical: false, stackBySide, sideSlot0AbsPos,
			ref budget, failureNotes, placedRecords, assemblyName, viewLabel);

		// Main-run: when olets exist, stack flange-to-flange on the SAME side (outer), not opposite.
		int mainSign = oletGroup.Count > 0
			? oletSign
			: (preferredSign == 0 ? 1 : Math.Sign(preferredSign));
		bool stackMainOutsideOlets = oletGroup.Count > 0;
		placed += PlaceFabDimIntentGroup(
			doc, view, viewNormal, right, up, mainGroup, pool, spoolSettings, mainSign,
			flipWholeGroupOnFail: true, stackOutsideOletsOnVertical: stackMainOutsideOlets, stackBySide, sideSlot0AbsPos,
			ref budget, failureNotes, placedRecords, assemblyName, viewLabel);
		placed += PlaceFabDimIntentGroup(
			doc, view, viewNormal, right, up, otherGroup, pool, spoolSettings,
			preferredSign == 0 ? 1 : Math.Sign(preferredSign),
			flipWholeGroupOnFail: false, stackOutsideOletsOnVertical: false, stackBySide, sideSlot0AbsPos,
			ref budget, failureNotes, placedRecords, assemblyName, viewLabel);

		return placed;
	}

	/// <summary>Facing-side sign for the olet group (Left/Right/Up), default +1.</summary>
	private static int ResolveOletGroupFacingSign(
		List<FabDimIntent> oletGroup,
		View view,
		XYZ right,
		XYZ up,
		IList<FabricationPart> pool)
	{
		if (oletGroup == null || oletGroup.Count == 0)
		{
			return 1;
		}

		FabDimIntent first = oletGroup[0];
		if (first?.OffsetSignOverride != null && first.OffsetSignOverride.Value != 0)
		{
			return Math.Sign(first.OffsetSignOverride.Value);
		}

		if (first != null && !first.MeasureAlongRight && first.A != null && first.B != null)
		{
			return ResolveFabDimOutsideOffsetSign(
				view, right?.Normalize(), first.A, first.B, pool, 1);
		}

		return 1;
	}

	private static int PlaceFabDimIntentGroup(
		Document doc,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> group,
		IList<FabricationPart> pool,
		SpoolingManagerSettings spoolSettings,
		int preferredSign,
		bool flipWholeGroupOnFail,
		bool stackOutsideOletsOnVertical,
		Dictionary<string, int> stackBySide,
		Dictionary<string, double> sideSlot0AbsPos,
		ref int budget,
		List<string> failureNotes,
		List<FabDimPlaceRecord> placedRecords,
		string assemblyName,
		string viewLabel)
	{
		if (group == null || group.Count == 0 || budget <= 0)
		{
			return 0;
		}

		if (stackBySide == null)
		{
			stackBySide = new Dictionary<string, int>(StringComparer.Ordinal);
		}

		if (sideSlot0AbsPos == null)
		{
			sideSlot0AbsPos = new Dictionary<string, double>(StringComparer.Ordinal);
		}

		int sign = preferredSign == 0 ? 1 : Math.Sign(preferredSign);
		int placed = TryPlaceFabDimGroupOnce(
			doc, view, viewNormal, right, up, group, pool, spoolSettings, sign,
			stackOutsideOletsOnVertical, stackBySide, sideSlot0AbsPos, ref budget, failureNotes, placedRecords, assemblyName, viewLabel);
		if (placed == 0 && flipWholeGroupOnFail)
		{
			placed = TryPlaceFabDimGroupOnce(
				doc, view, viewNormal, right, up, group, pool, spoolSettings, -sign,
				stackOutsideOletsOnVertical: false, stackBySide, sideSlot0AbsPos, ref budget, failureNotes, placedRecords, assemblyName, viewLabel);
		}

		return placed;
	}

	private static int TryPlaceFabDimGroupOnce(
		Document doc,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		List<FabDimIntent> group,
		IList<FabricationPart> pool,
		SpoolingManagerSettings spoolSettings,
		int offsetSign,
		bool stackOutsideOletsOnVertical,
		Dictionary<string, int> stackBySide,
		Dictionary<string, double> sideSlot0AbsPos,
		ref int budget,
		List<string> failureNotes,
		List<FabDimPlaceRecord> placedRecords,
		string assemblyName,
		string viewLabel)
	{
		int placed = 0;
		int maxSlots = ComputeFabDimMaxStackSlots();
		if (stackBySide == null)
		{
			stackBySide = new Dictionary<string, int>(StringComparer.Ordinal);
		}

		if (sideSlot0AbsPos == null)
		{
			sideSlot0AbsPos = new Dictionary<string, double>(StringComparer.Ordinal);
		}

		// Innermost first: olets, then segments, then flange-to-flange overall (outer stack).
		List<FabDimIntent> ordered = group
			.OrderBy(i => i.Purpose == DimensionPurpose.OletLocation ? 0
				: i.Purpose == DimensionPurpose.MainRunOverall ? 2
				: 1)
			.ThenBy(i => i.Purpose == DimensionPurpose.OletLocation
				? i.A.Point.DistanceTo(i.B.Point)
				: 0)
			.ToList();

		foreach (FabDimIntent intent in ordered)
		{
			if (budget <= 0)
			{
				failureNotes?.Add("Intel fab-dim: hit max " + FabDimMaxDimensionsPerView + " dims/view.");
				break;
			}

			// Always lock preferred axis — never fall back to orthogonal (avoids diagonal encouragement).
			if (!TryPickViewAxisMeasurement(
				intent.A.Point,
				intent.B.Point,
				viewNormal,
				right,
				up,
				intent.MeasureAlongRight,
				out bool alongRight,
				out double chosenDelta,
				out _,
				lockPreferredAxis: true))
			{
				LogFabDimDiag(assemblyName, viewLabel,
					"REJECTED purpose=" + intent.Purpose + " ids=" + DescribeAnchorPair(intent.A, intent.B)
					+ " reason=pre-place-axis-too-short", 0, 0);
				continue;
			}

			// Olet: pull to the side the olet faces (planner OffsetSignOverride). Facing-user on a
			// vertical host leaves override null → try Right then Left for clearance.
			// Main/branch: park on the OUTSIDE of the whole assembly along the offset axis.
			int placeSign;
			bool oletTryOpposite = false;
			if (intent.Purpose == DimensionPurpose.OletLocation)
			{
				if (intent.OffsetSignOverride.HasValue)
				{
					placeSign = intent.OffsetSignOverride.Value == 0
						? 1
						: Math.Sign(intent.OffsetSignOverride.Value);
					// Facing-locked side: do not flip across the pipe; bump stack instead.
					oletTryOpposite = false;
				}
				else if (alongRight)
				{
					placeSign = 1; // H host facing user → Up
					oletTryOpposite = false;
				}
				else
				{
					placeSign = ResolveFabDimOutsideOffsetSign(
						view, right?.Normalize(), intent.A, intent.B, pool, 1);
					oletTryOpposite = true; // discretionary L/R — avoid other dims
				}
			}
			else if (intent.OffsetSignOverride.HasValue)
			{
				placeSign = intent.OffsetSignOverride.Value;
			}
			else if (stackOutsideOletsOnVertical
				&& !alongRight
				&& intent.Purpose == DimensionPurpose.MainRunOverall)
			{
				// Flange-to-flange stacks outside the olet dim on the same L/R side.
				placeSign = offsetSign == 0 ? 1 : Math.Sign(offsetSign);
			}
			else
			{
				XYZ offsetAxisHint = (alongRight ? up : right)?.Normalize();
				int fallback = alongRight ? -1 : Math.Sign(offsetSign == 0 ? 1 : offsetSign);
				placeSign = ResolveFabDimOutsideOffsetSign(
					view, offsetAxisHint, intent.A, intent.B, pool, fallback);
			}

			string sideKey = FabDimOffsetSideKey(alongRight, placeSign);
			if (!stackBySide.TryGetValue(sideKey, out int stackIndex))
			{
				stackIndex = 0;
			}
			if (stackIndex >= maxSlots)
			{
				failureNotes?.Add("Intel fab-dim: stack offset would exceed " + FabDimMaxOffsetSheetInches + "\" on side " + sideKey + " — skipping.");
				continue;
			}

			try
			{
				string failureDetail;
				Dimension placedDim;
				int tryStack = stackIndex;
				bool ok = TryPlaceFabDimOrthogonalDimension(
					doc,
					view,
					viewNormal,
					right,
					up,
					intent,
					alongRight,
					placeSign,
					pool,
					spoolSettings,
					sideKey,
					sideSlot0AbsPos,
					ref tryStack,
					out failureDetail,
					out placedDim);

				// Discretionary olet L/R: if place failed or the dim contacts another, flip side once.
				bool contacts = ok && FabDimDimensionContactsOthers(doc, view, placedDim);
				if ((!ok || contacts)
					&& oletTryOpposite
					&& intent.Purpose == DimensionPurpose.OletLocation)
				{
					Dimension firstDim = placedDim;
					int firstSign = placeSign;
					string firstKey = sideKey;
					int firstStack = tryStack;

					if (firstDim != null)
					{
						try { if (firstDim.IsValidObject) doc.Delete(((Element)firstDim).Id); } catch { }
						placedDim = null;
					}

					int altSign = -firstSign;
					string altKey = FabDimOffsetSideKey(alongRight, altSign);
					if (!stackBySide.TryGetValue(altKey, out tryStack))
					{
						tryStack = 0;
					}

					bool altOk = TryPlaceFabDimOrthogonalDimension(
						doc,
						view,
						viewNormal,
						right,
						up,
						intent,
						alongRight,
						altSign,
						pool,
						spoolSettings,
						altKey,
						sideSlot0AbsPos,
						ref tryStack,
						out string altFail,
						out Dimension altDim);

					if (altOk && altDim != null && !FabDimDimensionContactsOthers(doc, view, altDim))
					{
						ok = true;
						placedDim = altDim;
						placeSign = altSign;
						sideKey = altKey;
						failureDetail = null;
					}
					else if (altOk && altDim != null && !ok)
					{
						// First failed entirely — keep alt even if it also contacts.
						ok = true;
						placedDim = altDim;
						placeSign = altSign;
						sideKey = altKey;
						failureDetail = null;
					}
					else
					{
						if (altDim != null)
						{
							try { if (altDim.IsValidObject) doc.Delete(((Element)altDim).Id); } catch { }
						}

						// Restore first side when it placed but contacted (alt no better).
						if (contacts)
						{
							tryStack = firstStack > 0 ? firstStack - 1 : 0;
							ok = TryPlaceFabDimOrthogonalDimension(
								doc,
								view,
								viewNormal,
								right,
								up,
								intent,
								alongRight,
								firstSign,
								pool,
								spoolSettings,
								firstKey,
								sideSlot0AbsPos,
								ref tryStack,
								out failureDetail,
								out placedDim);
							placeSign = firstSign;
							sideKey = firstKey;
						}
						else
						{
							ok = false;
							placedDim = null;
							failureDetail = altFail ?? failureDetail;
						}
					}
				}

				if (ok)
				{
					stackBySide[sideKey] = tryStack;
				}

				if (ok && placedDim != null)
				{
					placed++;
					budget--;
					placedRecords?.Add(new FabDimPlaceRecord
					{
						Intent = intent,
						Dimension = placedDim,
						IdA = GetElementIdValue(intent.A.OwnerElement.Id),
						IdB = GetElementIdValue(intent.B.OwnerElement.Id)
					});

					LogFabDimDiag(assemblyName, viewLabel,
						"CREATED purpose=" + intent.Purpose
						+ " ids=" + DescribeAnchorPair(intent.A, intent.B)
						+ " anchorTypes=" + intent.A.Kind + "+" + intent.B.Kind
						+ " axis=" + (alongRight ? "H" : "V")
						+ " side=" + FabDimOffsetSideKey(alongRight, placeSign)
						+ " delta=" + chosenDelta.ToString("F4", CultureInfo.InvariantCulture),
						0, 0);
				}
				else
				{
					LogFabDimDiag(assemblyName, viewLabel,
						"REJECTED purpose=" + intent.Purpose
						+ " ids=" + DescribeAnchorPair(intent.A, intent.B)
						+ " reason=place-failed " + (failureDetail ?? "unknown"),
						0, 0);
					if (!string.IsNullOrWhiteSpace(failureDetail))
					{
						failureNotes?.Add("Intel fab-dim place: " + failureDetail);
					}
				}
			}
			catch (Exception ex)
			{
				LogFabDimDiag(assemblyName, viewLabel,
					"REJECTED purpose=" + intent.Purpose
					+ " ids=" + DescribeAnchorPair(intent.A, intent.B)
					+ " reason=place-exception " + (ex.Message ?? ex.GetType().Name),
					0, 0);
				failureNotes?.Add("Intel fab-dim place exception: " + (ex.Message ?? ex.GetType().Name));
			}
		}

		return placed;
	}

	/// <summary>
	/// Place one planned intent as a flat view-axis dimension.
	/// Proven via Revit MCP: same fabrication refs + exact Right/Up dim line → orthogonal result;
	/// chord-aligned dim lines produce the visible tilt. Never builds from (B−A).
	/// </summary>
	private static bool TryPlaceFabDimOrthogonalDimension(
		Document doc,
		View view,
		XYZ viewNormal,
		XYZ right,
		XYZ up,
		FabDimIntent intent,
		bool alongRight,
		int offsetSign,
		IList<FabricationPart> pool,
		SpoolingManagerSettings spoolSettings,
		string sideKey,
		Dictionary<string, double> sideSlot0AbsPos,
		ref int stackIndex,
		out string failureDetail,
		out Dimension placedDim)
	{
		failureDetail = null;
		placedDim = null;

		if (doc == null || view == null || intent?.A?.OwnerElement == null || intent.B?.OwnerElement == null
			|| intent.A.Point == null || intent.B.Point == null || right == null || up == null)
		{
			failureDetail = "Missing inputs for orthogonal fab-dim placement.";
			return false;
		}

		// Use the view's native axes (not a drifted projected copy).
		XYZ measureAxis = (alongRight ? view.RightDirection : view.UpDirection)?.Normalize();
		XYZ offsetAxis = (alongRight ? view.UpDirection : view.RightDirection)?.Normalize();
		if (measureAxis == null || offsetAxis == null)
		{
			failureDetail = "View Right/Up axes unavailable.";
			return false;
		}

		// Olet locate: never park the dim line along the pipe axis. Horizontal measure → offset Up;
		// vertical measure → offset Left/Right (sign = olet facing). Honor planner sign — do not
		// force +1 (that parked left-facing Top-view olets on the right).
		int sign = offsetSign == 0 ? 1 : Math.Sign(offsetSign);
		if (intent.Purpose == DimensionPurpose.OletLocation)
		{
			if (alongRight)
			{
				offsetAxis = view.UpDirection?.Normalize() ?? offsetAxis;
			}
			else
			{
				offsetAxis = view.RightDirection?.Normalize() ?? offsetAxis;
			}
		}
		DimensionType dimType = TryResolveLinearDimensionType(doc, spoolSettings);
		if (dimType == null || !IsLinearDimensionType(dimType))
		{
			failureDetail = "No linear dimension type.";
			return false;
		}

		// Slot 0: olet/segment clears origin parts only. Boxed-out + overalls clear the whole
		// assembly so H dims park past the bottom flange (not through the riser body).
		// Slot N: same dim-line as slot 0 + N × Dimension Line Snap Distance.
		bool clearWholeAssembly = intent.Purpose == DimensionPurpose.BoxedOut45Horizontal
			|| intent.Purpose == DimensionPurpose.BoxedOut45Vertical
			|| intent.Purpose == DimensionPurpose.MainRunOverall
			|| intent.Purpose == DimensionPurpose.BranchOverall;
		double offsetSigned = ResolveFabDimClearanceThenSnapOffset(
			view, dimType, stackIndex, sign, offsetAxis, intent.A, intent.B,
			clearWholeAssembly ? pool : null,
			sideKey, sideSlot0AbsPos);

		if (!TryBuildFabDimOrthogonalDimensionLine(
			view, viewNormal, measureAxis, offsetAxis, intent.A.Point, intent.B.Point, alongRight, offsetSigned, out Line dimLine)
			|| dimLine == null
			|| !IsDimensionDirectionViewAxisAligned(view, dimLine.Direction))
		{
			failureDetail = "Could not build an exact horizontal/vertical dimension line.";
			return false;
		}

		List<(FabricationDimensionRefRole roleA, FabricationDimensionRefRole roleB)> rolePairs =
			BuildFabDimRoleAttempts(intent.A.Kind, intent.B.Kind)
				.Where(p => p.roleA.HasValue && p.roleB.HasValue)
				.Select(p => (p.roleA.Value, p.roleB.Value))
				.ToList();
		rolePairs.Add((MapFabDimAnchorToRefRole(intent.A.Kind), MapFabDimAnchorToRefRole(intent.B.Kind)));

		string lastFail = null;
		HashSet<string> tried = new HashSet<string>(StringComparer.Ordinal);

		foreach ((FabricationDimensionRefRole roleA, FabricationDimensionRefRole roleB) roles in rolePairs)
		{
			List<Reference> refsA = GetAllFabricationInstanceDimensionReferences(
				intent.A.OwnerElement, view, intent.A.Point, roles.roleA, measureAxis);
			List<Reference> refsB = GetAllFabricationInstanceDimensionReferences(
				intent.B.OwnerElement, view, intent.B.Point, roles.roleB, measureAxis);

			// Also try scored best singles.
			if (TryPickBestScoredFabricationAnchorReference(
				intent.A.OwnerElement, view, intent.A.Point, roles.roleA, measureAxis, applySnapFilter: false, out Reference bestA)
				&& bestA != null)
			{
				refsA.Insert(0, bestA);
			}
			if (TryPickBestScoredFabricationAnchorReference(
				intent.B.OwnerElement, view, intent.B.Point, roles.roleB, measureAxis, applySnapFilter: false, out Reference bestB)
				&& bestB != null)
			{
				refsB.Insert(0, bestB);
			}

			refsA = FilterFabDimRefsToAnchorTarget(intent.A, view, refsA, measureAxis, offsetAxis);
			refsB = FilterFabDimRefsToAnchorTarget(intent.B, view, refsB, measureAxis, offsetAxis);
			refsA = refsA.Where(r => r != null).Take(8).ToList();
			refsB = refsB.Where(r => r != null).Take(8).ToList();
			if (refsA.Count == 0 || refsB.Count == 0)
			{
				lastFail = "No fabrication refs near intended snap targets.";
				continue;
			}

			foreach (Reference refA in refsA)
			{
				foreach (Reference refB in refsB)
				{
					string key;
					try
					{
						key = refA.ConvertToStableRepresentation(doc) + "|" + refB.ConvertToStableRepresentation(doc);
					}
					catch
					{
						key = GetElementIdValue(refA.ElementId) + "|" + GetElementIdValue(refB.ElementId) + "|" + roles.roleA + "|" + roles.roleB;
					}
					if (!tried.Add(key))
					{
						continue;
					}

					ReferenceArray primary = new ReferenceArray();
					primary.Append(refA);
					primary.Append(refB);
					ReferenceArray swapped = new ReferenceArray();
					swapped.Append(refB);
					swapped.Append(refA);

					// Corner / boxed-out spans: drag-stable Linear (temp axis-lock helper, then removed).
					// Plain two-ref Linear snaps back onto the 45° chord when the user touches it.
					bool dragStable = intent.Purpose == DimensionPurpose.BoxedOut45Horizontal
						|| intent.Purpose == DimensionPurpose.BoxedOut45Vertical
						|| NeedsDragStableFabDim(intent, viewNormal, right, up);
					bool committed = dragStable
						? TryCommitDragStableSheetLinearDimension(
							doc, view, dimLine, primary, swapped, dimType, measureAxis, out Dimension dim, out string commitError)
						: TryCommitNewDimension(
							doc, view, dimLine, primary, swapped, dimType, out dim, out commitError);
					if (!committed || dim == null)
					{
						lastFail = commitError ?? "NewDimension failed";
						continue;
					}

					// Hard law: curve must be sheet Right or Up — never pipe-parallel / 45° tilt.
					XYZ sheetRight = view.RightDirection?.Normalize();
					XYZ sheetUp = view.UpDirection?.Normalize();
					if (!TryRejectRevitAlignedDimensionCurve(dim, view, dimLine, out string tiltReason)
						|| !IsFabDimCreatedCurveViewAxis(view, viewNormal, sheetRight, sheetUp, dim)
						|| !IsDimensionDirectionViewAxisAligned(view, TryReadDimensionCurveDirection(dim)))
					{
						lastFail = tiltReason ?? "Dimension curve is not sheet-axis aligned.";
						try
						{
							if (dim.IsValidObject)
							{
								try { ((Element)dim).Pinned = false; } catch { }
								doc.Delete(((Element)dim).Id);
							}
						}
						catch { }
						continue;
					}

					double dimValue;
					string valueString;
					try
					{
						dimValue = dim.Value ?? -1.0;
						valueString = (dim.ValueString ?? string.Empty).Trim();
					}
					catch (Exception ex)
					{
						lastFail = ex.Message;
						try { doc.Delete(((Element)dim).Id); } catch { }
						continue;
					}

					if (dimValue < FabDimMinLengthFeet || valueString.StartsWith("-", StringComparison.Ordinal))
					{
						lastFail = "Zero-length dimension (value=" + valueString + ").";
						try { doc.Delete(((Element)dim).Id); } catch { }
						continue;
					}

					XYZ delta = intent.B.Point - intent.A.Point;
					XYZ inPlane = ProjectVectorToViewPlane(delta, viewNormal) ?? delta;
					double expected = Math.Abs(inPlane.DotProduct(measureAxis));
					double trueLen = inPlane.GetLength();
					// Reject true-length / √2 readings (19¼" on a 13⅝" box leg). Was 1.55 — that allowed √2.
					if (expected >= FabDimMinLengthFeet
						&& (dimValue < expected * 0.88 || dimValue > expected * 1.12))
					{
						lastFail = "Dimension value (" + valueString + ") does not match sheet-projected span.";
						try { doc.Delete(((Element)dim).Id); } catch { }
						continue;
					}
					if (trueLen >= FabDimMinLengthFeet
						&& Math.Abs(trueLen - expected) > expected * 0.08
						&& Math.Abs(dimValue - trueLen) <= trueLen * 0.06)
					{
						lastFail = "Dimension value matches true length, not sheet H/V projection.";
						try { doc.Delete(((Element)dim).Id); } catch { }
						continue;
					}

					TryApplySpoolAutoDimensionBelowLabel(doc, view, dim, spoolSettings, roles.roleA, roles.roleB);
					placedDim = dim;
					stackIndex++;
					return true;
				}
			}
		}

		failureDetail = lastFail ?? "No fabrication reference pair produced an orthogonal dimension.";
		return false;
	}

	/// <summary>
	/// True when witnesses span both sheet axes — plain Linear will snap to the chord on drag.
	/// </summary>
	private static bool NeedsDragStableFabDim(FabDimIntent intent, XYZ viewNormal, XYZ right, XYZ up)
	{
		if (intent?.A?.Point == null || intent.B?.Point == null || right == null || up == null)
		{
			return false;
		}

		XYZ inPlane = ProjectVectorToViewPlane(intent.B.Point - intent.A.Point, viewNormal)
			?? (intent.B.Point - intent.A.Point);
		const double multiPlaneMinFeet = 3.0 / 12.0;
		return Math.Abs(inPlane.DotProduct(right)) >= multiPlaneMinFeet
			&& Math.Abs(inPlane.DotProduct(up)) >= multiPlaneMinFeet;
	}

	private static List<(FabricationDimensionRefRole? roleA, FabricationDimensionRefRole? roleB)> BuildFabDimRoleAttempts(
		FabDimAnchorKind kindA,
		FabDimAnchorKind kindB)
	{
		FabricationDimensionRefRole primaryA = MapFabDimAnchorToRefRole(kindA);
		FabricationDimensionRefRole primaryB = MapFabDimAnchorToRefRole(kindB);

		var attempts = new List<(FabricationDimensionRefRole?, FabricationDimensionRefRole?)>
		{
			(primaryA, primaryB),
			(primaryB, primaryA),
			(FabricationDimensionRefRole.PipeOpenEnd, FabricationDimensionRefRole.PipeCenterline),
			(FabricationDimensionRefRole.PipeCenterline, FabricationDimensionRefRole.PipeOpenEnd),
			(FabricationDimensionRefRole.PipeCenterline, FabricationDimensionRefRole.FlangeFace),
			(FabricationDimensionRefRole.FlangeFace, FabricationDimensionRefRole.RunStartFitting),
			(FabricationDimensionRefRole.RunStartFitting, FabricationDimensionRefRole.FlangeFace),
			(FabricationDimensionRefRole.PipeOpenEnd, FabricationDimensionRefRole.RunStartFitting),
			(FabricationDimensionRefRole.RunStartFitting, FabricationDimensionRefRole.RunStartFitting),
			(FabricationDimensionRefRole.PipeOpenEnd, FabricationDimensionRefRole.OletBranch),
			(FabricationDimensionRefRole.FlangeFace, FabricationDimensionRefRole.OletBranch),
			(FabricationDimensionRefRole.PipeCenterline, FabricationDimensionRefRole.FlangeFace),
			(null, null)
		};

		return attempts
			.GroupBy(a => (a.Item1?.ToString() ?? "auto") + "|" + (a.Item2?.ToString() ?? "auto"))
			.Select(g => g.First())
			.ToList();
	}

	private static FabricationDimensionRefRole MapFabDimAnchorToRefRole(FabDimAnchorKind kind)
	{
		switch (kind)
		{
			case FabDimAnchorKind.FlangeOuterFace:
			case FabDimAnchorKind.EndCapOuterFace:
				return FabricationDimensionRefRole.FlangeFace;
			case FabDimAnchorKind.OpenPipeEnd:
			case FabDimAnchorKind.HostPipeEndForOlet:
				return FabricationDimensionRefRole.PipeOpenEnd;
			case FabDimAnchorKind.OletMainStation:
				// Snap is the olet takeoff station; reference must come from the olet element.
				return FabricationDimensionRefRole.OletBranch;
			case FabDimAnchorKind.ElbowCenter:
			case FabDimAnchorKind.TeeCenter:
			case FabDimAnchorKind.FittingCenter:
				return FabricationDimensionRefRole.RunStartFitting;
			default:
				return FabricationDimensionRefRole.RunStartFitting;
		}
	}


	/// <summary>
	/// Offset from the outside of the dimensioned parts along offsetAxis, plus sheet clearance (3/8").
	/// Prevents dim lines from running through pipe bodies when anchors sit on the centerline.
	/// </summary>

	private static string FabDimOffsetSideKey(bool measureAlongRight, int offsetSign)
	{
		int sign = offsetSign == 0 ? 1 : Math.Sign(offsetSign);
		if (measureAlongRight)
		{
			return sign > 0 ? "top" : "bottom";
		}

		return sign > 0 ? "right" : "left";
	}

	/// <summary>
	/// Choose +/- offset so the dim parks beyond the near outside edge of the assembly.
	/// Elev Right L-spool V → right; Elev Left L-spool V → left; H → usually below.
	/// </summary>
	private static int ResolveFabDimOutsideOffsetSign(
		View view,
		XYZ offsetAxis,
		FabDimFunctionalAnchor anchorA,
		FabDimFunctionalAnchor anchorB,
		IList<FabricationPart> pool,
		int fallbackSign)
	{
		offsetAxis = offsetAxis?.Normalize();
		if (offsetAxis == null)
		{
			return fallbackSign == 0 ? 1 : Math.Sign(fallbackSign);
		}

		if (!TryGetFabDimOffsetBounds(view, offsetAxis, anchorA, anchorB, pool, out double geomMin, out double geomMax, out double midCl))
		{
			return fallbackSign == 0 ? 1 : Math.Sign(fallbackSign);
		}

		double distToMin = midCl - geomMin;
		double distToMax = geomMax - midCl;
		// Closer to the min edge → outside is beyond min (sign -1). Closer to max → +1.
		if (distToMin <= distToMax)
		{
			return -1;
		}

		return 1;
	}

	/// <summary>
	/// True when <paramref name="dim"/> sits on nearly the same offset line as another same-axis
	/// linear dim with overlapping measure span (sheet contact / stacked collision).
	/// </summary>
	private static bool FabDimDimensionContactsOthers(Document doc, View view, Dimension dim)
	{
		if (doc == null || view == null || dim == null || !dim.IsValidObject)
		{
			return false;
		}

		if (!TryGetFabDimCurveEnds(dim, out XYZ a, out XYZ b, out XYZ dir)
			|| !TryGetViewPlaneAxes(view, out _, out XYZ right, out XYZ up))
		{
			return false;
		}

		bool measureAlongRight = Math.Abs(dir.DotProduct(right)) >= Math.Abs(dir.DotProduct(up));
		XYZ measureAxis = measureAlongRight ? right : up;
		XYZ offsetAxis = measureAlongRight ? up : right;
		double midOff = ((a + b) * 0.5).DotProduct(offsetAxis);
		double mMin = Math.Min(a.DotProduct(measureAxis), b.DotProduct(measureAxis));
		double mMax = Math.Max(a.DotProduct(measureAxis), b.DotProduct(measureAxis));
		double contactTol = ConvertSheetOffsetToModelDistance(view, (1.0 / 16.0) / 12.0);
		long selfId = GetElementIdValue(((Element)dim).Id);

		IEnumerable<Dimension> others;
		try
		{
			others = new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>()
				.Where(d => d != null && d.IsValidObject && d.DimensionType != null && IsLinearDimensionType(d.DimensionType));
		}
		catch
		{
			return false;
		}

		foreach (Dimension other in others)
		{
			if (GetElementIdValue(((Element)other).Id) == selfId)
			{
				continue;
			}

			if (!TryGetFabDimCurveEnds(other, out XYZ oa, out XYZ ob, out XYZ odir))
			{
				continue;
			}

			bool otherAlongRight = Math.Abs(odir.DotProduct(right)) >= Math.Abs(odir.DotProduct(up));
			if (otherAlongRight != measureAlongRight)
			{
				continue;
			}

			double oMid = ((oa + ob) * 0.5).DotProduct(offsetAxis);
			if (Math.Abs(oMid - midOff) > contactTol)
			{
				continue;
			}

			double oMin = Math.Min(oa.DotProduct(measureAxis), ob.DotProduct(measureAxis));
			double oMax = Math.Max(oa.DotProduct(measureAxis), ob.DotProduct(measureAxis));
			if (mMax >= oMin - contactTol && oMax >= mMin - contactTol)
			{
				return true;
			}
		}

		return false;
	}

	private static bool TryGetFabDimCurveEnds(Dimension dim, out XYZ a, out XYZ b, out XYZ dir)
	{
		a = null;
		b = null;
		dir = null;
		try
		{
			Curve curve = dim?.Curve;
			if (curve == null || !curve.IsBound)
			{
				return false;
			}

			a = curve.GetEndPoint(0);
			b = curve.GetEndPoint(1);
			if (a == null || b == null || a.DistanceTo(b) < 1E-09)
			{
				return false;
			}

			dir = (b - a).Normalize();
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Slot 0: park just clear of the parts this dim originates from — dim LINE and TEXT
	/// (never whole-assembly / flange OD). Slot N: same dim-line as slot 0 on this side
	/// plus N × Dimension Line Snap Distance from the active dim style.
	/// </summary>
	private static double ResolveFabDimClearanceThenSnapOffset(
		View view,
		DimensionType dimType,
		int stackIndex,
		int offsetSign,
		XYZ offsetAxis,
		FabDimFunctionalAnchor anchorA,
		FabDimFunctionalAnchor anchorB,
		IList<FabricationPart> poolForBounds,
		string sideKey,
		Dictionary<string, double> sideSlot0AbsPos)
	{
		int sign = offsetSign == 0 ? 1 : Math.Sign(offsetSign);
		// Beyond exterior: small gap + text band (text sits on the assembly side of the line).
		double clearGapModel = ResolveFabDimOriginClearGapModel(view, dimType);

		TryGetFabDimOffsetBounds(
			view, offsetAxis, anchorA, anchorB, poolForBounds,
			out double geomMin, out double geomMax, out double midCl);

		double snapModel = ResolveFabDimStyleSnapModel(view, dimType);

		// Stacked: nest from the first dim line on this side — do not re-clear wider geometry.
		if (stackIndex > 0
			&& !string.IsNullOrEmpty(sideKey)
			&& sideSlot0AbsPos != null
			&& sideSlot0AbsPos.TryGetValue(sideKey, out double slot0Abs))
		{
			return (slot0Abs + (stackIndex * snapModel * sign)) - midCl;
		}

		double first = ResolveFabDimExteriorClearanceOffset(
			view, offsetAxis, sign, clearGapModel, anchorA, anchorB, poolForBounds);

		if (!double.IsInfinity(geomMin) && !double.IsInfinity(geomMax))
		{
			double linePos = midCl + first;
			const double eps = 1.0 / 96.0;
			bool throughBody = linePos > geomMin - eps && linePos < geomMax + eps;
			if (throughBody)
			{
				// Preferred side still clips — pull off the other side of the same bounds.
				sign = -sign;
				first = ResolveFabDimExteriorClearanceOffset(
					view, offsetAxis, sign, clearGapModel, anchorA, anchorB, poolForBounds);
			}
		}

		if (stackIndex > 0)
		{
			first += stackIndex * snapModel * sign;
		}

		if (stackIndex <= 0
			&& !string.IsNullOrEmpty(sideKey)
			&& sideSlot0AbsPos != null)
		{
			sideSlot0AbsPos[sideKey] = midCl + first;
		}

		return first;
	}

	/// <summary>
	/// Sheet gap past origin-part exterior so both the dim line and its text clear the body.
	/// Uses dim-style Text Size + Text Offset (defaults match Linear 3/32" Arial: 3/32" + 1/16")
	/// plus 1/16" air gap.
	/// </summary>
	private static double ResolveFabDimOriginClearGapModel(View view, DimensionType dimType)
	{
		const double airGapSheetFeet = 1.0 / 192.0; // 1/16"
		const double defaultTextSizeSheetFeet = 3.0 / 32.0 / 12.0;
		const double defaultTextOffsetSheetFeet = 1.0 / 16.0 / 12.0;

		double textSizeSheet = defaultTextSizeSheetFeet;
		double textOffsetSheet = defaultTextOffsetSheetFeet;
		if (TryGetDimensionTypeSheetDistanceFeet(dimType, BuiltInParameter.TEXT_SIZE, out double ts)
			&& ts > 1E-09)
		{
			textSizeSheet = ts;
		}

		// "Text Offset" on Linear Dimension Style (no stable BIP across Revit builds).
		TryGetDimensionTypeParameterSheetFeet(dimType, "Text Offset", ref textOffsetSheet);

		double sheetFeet = airGapSheetFeet + textSizeSheet + textOffsetSheet;
		return ConvertSheetOffsetToModelDistance(view, sheetFeet);
	}

	private static void TryGetDimensionTypeParameterSheetFeet(
		DimensionType dimType,
		string parameterName,
		ref double sheetFeet)
	{
		if (dimType == null || string.IsNullOrWhiteSpace(parameterName))
		{
			return;
		}

		try
		{
			foreach (Parameter p in dimType.Parameters)
			{
				if (p == null || p.StorageType != StorageType.Double)
				{
					continue;
				}

				Definition def = p.Definition;
				if (def == null || !string.Equals(def.Name, parameterName, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				double v = p.AsDouble();
				if (v > 1E-09)
				{
					sheetFeet = v;
				}

				return;
			}
		}
		catch
		{
		}
	}

	private static double ResolveFabDimStyleSnapModel(View view, DimensionType dimType)
	{
		const double defaultSnapSheetFeet = 1.0 / 48.0; // 1/4"
		double snapSheetFeet = defaultSnapSheetFeet;
		if (TryGetDimensionTypeSheetDistanceFeet(
				dimType, BuiltInParameter.DIM_STYLE_DIM_LINE_SNAP_DIST, out double fromStyle)
			&& fromStyle > 1E-09)
		{
			snapSheetFeet = fromStyle;
		}

		return ConvertSheetOffsetToModelDistance(view, snapSheetFeet);
	}

	private static bool TryGetFabDimOffsetBounds(
		View view,
		XYZ offsetAxis,
		FabDimFunctionalAnchor anchorA,
		FabDimFunctionalAnchor anchorB,
		IList<FabricationPart> poolForBounds,
		out double geomMin,
		out double geomMax,
		out double midCl)
	{
		geomMin = double.PositiveInfinity;
		geomMax = double.NegativeInfinity;
		midCl = 0;
		offsetAxis = offsetAxis?.Normalize();
		if (offsetAxis == null)
		{
			return false;
		}

		int midCount = 0;
		if (anchorA?.Point != null)
		{
			midCl += anchorA.Point.DotProduct(offsetAxis);
			midCount++;
		}
		if (anchorB?.Point != null)
		{
			midCl += anchorB.Point.DotProduct(offsetAxis);
			midCount++;
		}
		if (midCount > 0)
		{
			midCl /= midCount;
		}

		// Full pool only when choosing outside L/R/T/B for the assembly.
		if (poolForBounds != null)
		{
			foreach (FabricationPart part in poolForBounds)
			{
				AccrueFabDimOffsetBounds(view, offsetAxis, part as Element, ref geomMin, ref geomMax);
			}
		}
		AccrueFabDimOffsetBounds(view, offsetAxis, anchorA?.OwnerElement, ref geomMin, ref geomMax);
		AccrueFabDimOffsetBounds(view, offsetAxis, anchorB?.OwnerElement, ref geomMin, ref geomMax);
		return !double.IsInfinity(geomMin) && !double.IsInfinity(geomMax);
	}

	private static double ResolveFabDimExteriorClearanceOffset(
		View view,
		XYZ offsetAxis,
		int sign,
		double clearanceModel,
		FabDimFunctionalAnchor anchorA,
		FabDimFunctionalAnchor anchorB,
		IList<FabricationPart> poolForBounds)
	{
		sign = sign == 0 ? 1 : Math.Sign(sign);
		offsetAxis = offsetAxis?.Normalize();
		if (offsetAxis == null)
		{
			return clearanceModel * sign;
		}

		if (!TryGetFabDimOffsetBounds(view, offsetAxis, anchorA, anchorB, poolForBounds, out double geomMin, out double geomMax, out double midCl))
		{
			return clearanceModel * sign;
		}

		double target = sign > 0 ? (geomMax + clearanceModel) : (geomMin - clearanceModel);
		return target - midCl;
	}

	private static void AccrueFabDimOffsetBounds(
		View view,
		XYZ offsetAxis,
		Element el,
		ref double geomMin,
		ref double geomMax)
	{
		if (el == null || view == null || offsetAxis == null)
		{
			return;
		}
		BoundingBoxXYZ bb = null;
		try { bb = el.get_BoundingBox(view); } catch { bb = null; }
		if (bb == null)
		{
			try { bb = el.get_BoundingBox(null); } catch { bb = null; }
		}
		if (bb == null || bb.Min == null || bb.Max == null)
		{
			return;
		}
		XYZ[] corners =
		{
			new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
			new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
			new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
			new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
			new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
			new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
			new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
			new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
		};
		for (int i = 0; i < corners.Length; i++)
		{
			double o = corners[i].DotProduct(offsetAxis);
			geomMin = Math.Min(geomMin, o);
			geomMax = Math.Max(geomMax, o);
		}
	}


	/// <summary>
	/// Keep only fabrication refs that sit on the intended functional snap:
	/// - FlangeOuterFace / EndCap: OUTER face CENTER on run CL (reject back-of-flange and bottom nodes).
	/// - HostPipeEndForOlet: pipe-end only (never a flange face).
	/// - OletMainStation: olet center station.
	/// - ElbowCenter: CL intersection.
	/// </summary>
	private static List<Reference> FilterFabDimRefsToAnchorTarget(
		FabDimFunctionalAnchor anchor,
		View view,
		List<Reference> refs,
		XYZ measureAxis,
		XYZ offsetAxis)
	{
		List<Reference> kept = new List<Reference>();
		if (anchor?.OwnerElement == null || refs == null || refs.Count == 0 || anchor.Point == null)
		{
			return refs ?? kept;
		}

		bool flangeFace = anchor.Kind == FabDimAnchorKind.FlangeOuterFace
			|| anchor.Kind == FabDimAnchorKind.EndCapOuterFace;
		// Fabrication curve refs often fail GetGeometryObjectFromReference — do NOT drop those.
		// Only reject samples we can prove are on the wrong flange face / far off target.
		const double maxOffTargetFeet = 0.15; // ~1.8"
		XYZ backPoint = flangeFace ? TryGetFlangeBackConnectorPoint(anchor.OwnerElement as FabricationPart, anchor.Point) : null;

		foreach (Reference reference in refs)
		{
			if (reference == null)
			{
				continue;
			}
			if (!TryGetReferenceSampleWorldPointForTarget(anchor.OwnerElement, reference, anchor.Point, out XYZ sample)
				|| sample == null)
			{
				// Unknown sample: keep (scored list already preferred the target).
				kept.Add(reference);
				continue;
			}

			double toTarget = sample.DistanceTo(anchor.Point);
			if (flangeFace && backPoint != null)
			{
				double toBack = sample.DistanceTo(backPoint);
				// Hard reject back-of-flange / weld-neck side when outer target is known.
				if (toBack + 1e-6 < toTarget)
				{
					continue;
				}
			}

			if (toTarget > maxOffTargetFeet)
			{
				continue;
			}

			kept.Add(reference);
		}

		if (kept.Count == 0)
		{
			// Last resort: original order, but for flanges still strip proven back-face samples.
			if (!flangeFace)
			{
				return refs;
			}

			foreach (Reference reference in refs)
			{
				if (reference == null)
				{
					continue;
				}
				if (TryGetReferenceSampleWorldPointForTarget(anchor.OwnerElement, reference, anchor.Point, out XYZ sample)
					&& sample != null
					&& backPoint != null
					&& sample.DistanceTo(backPoint) + 1e-6 < sample.DistanceTo(anchor.Point))
				{
					continue;
				}
				kept.Add(reference);
			}
			if (kept.Count == 0)
			{
				return refs;
			}
		}

		return kept.OrderBy(r =>
		{
			if (!TryGetReferenceSampleWorldPointForTarget(anchor.OwnerElement, r, anchor.Point, out XYZ s) || s == null)
			{
				return double.MaxValue * 0.5;
			}
			return s.DistanceTo(anchor.Point);
		}).ToList();
	}

	/// <summary>
	/// Other flange connector from the intended outer-face target — i.e. the back / pipe-joint side.
	/// </summary>
	private static XYZ TryGetFlangeBackConnectorPoint(FabricationPart flange, XYZ outerFaceTarget)
	{
		if (flange == null || outerFaceTarget == null)
		{
			return null;
		}

		XYZ best = null;
		double bestDist = -1;
		foreach (Connector c in ListConnectors(flange))
		{
			if (c?.Origin == null)
			{
				continue;
			}

			double d = c.Origin.DistanceTo(outerFaceTarget);
			if (d > bestDist)
			{
				bestDist = d;
				best = c.Origin;
			}
		}

		// Only treat as "back" when it is a distinct face (~> 1/8").
		return bestDist > 0.01 ? best : null;
	}

	/// <summary>
	/// Build a bound dim line along measureAxis, offset along offsetAxis.
	/// Never uses (B−A) as the line direction.
	/// </summary>
	private static bool TryBuildFabDimOrthogonalDimensionLine(
		View view,
		XYZ viewNormal,
		XYZ measureAxis,
		XYZ offsetAxis,
		XYZ anchorA,
		XYZ anchorB,
		bool alongRight,
		double offsetSigned,
		out Line dimLine)
	{
		dimLine = null;
		if (view == null || anchorA == null || anchorB == null || measureAxis == null || offsetAxis == null)
		{
			return false;
		}

		measureAxis = measureAxis.Normalize();
		offsetAxis = offsetAxis.Normalize();

		if (!TryGetViewSketchPlane(view, out XYZ planeOrigin, out _))
		{
			planeOrigin = view.Origin;
		}

		if (planeOrigin == null)
		{
			return false;
		}

		XYZ a = ProjectToSketchPlane(anchorA, planeOrigin, viewNormal);
		XYZ b = ProjectToSketchPlane(anchorB, planeOrigin, viewNormal);
		double mA = a.DotProduct(measureAxis);
		double mB = b.DotProduct(measureAxis);
		double oA = a.DotProduct(offsetAxis);
		double oB = b.DotProduct(offsetAxis);
		double span = Math.Abs(mB - mA);
		if (span < FabDimMinLengthFeet)
		{
			return false;
		}

		double midMeasure = (mA + mB) * 0.5;
		double sharedOffset = (oA + oB) * 0.5 + offsetSigned;
		double half = Math.Max(span * 0.5 + 0.25, 0.5);

		XYZ center = planeOrigin
			+ measureAxis.Multiply(midMeasure - planeOrigin.DotProduct(measureAxis))
			+ offsetAxis.Multiply(sharedOffset - planeOrigin.DotProduct(offsetAxis));

		// Unbound along the view axis — proven via MCP: Bound/Unbound H/V → sheet dims;
		// any diagonal line → true-length tilted Linear (e.g. 19¼" on a 13⅝" box leg).
		dimLine = Line.CreateUnbound(center, measureAxis);
		return dimLine != null
			&& IsDimensionDirectionViewAxisAligned(view, dimLine.Direction)
			&& (Math.Abs(dimLine.Direction.Normalize().DotProduct(measureAxis)) >= FabDimExactAxisDotMin);
	}

	private static bool IsFabDimLineExactlyViewAxis(XYZ viewNormal, XYZ right, XYZ up, XYZ direction)
	{
		if (direction == null || right == null || up == null)
		{
			return false;
		}

		XYZ inPlane = ProjectVectorToViewPlane(direction, viewNormal);
		if (inPlane == null || inPlane.GetLength() < 1E-09)
		{
			return false;
		}

		inPlane = inPlane.Normalize();
		XYZ r = right.Normalize();
		XYZ u = up.Normalize();
		double alongR = Math.Abs(inPlane.DotProduct(r));
		double alongU = Math.Abs(inPlane.DotProduct(u));
		return alongR >= FabDimExactAxisDotMin || alongU >= FabDimExactAxisDotMin;
	}

	private static bool IsFabDimCreatedCurveViewAxis(View view, XYZ viewNormal, XYZ right, XYZ up, Dimension dim)
	{
		if (dim == null)
		{
			return false;
		}

		try
		{
			Curve curve = dim.Curve;
			if ((GeometryObject)(object)curve == (GeometryObject)null)
			{
				return false;
			}

			return IsFabDimCreatedCurveViewAxis(view, viewNormal, right, up, curve);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsFabDimCreatedCurveViewAxis(View view, XYZ viewNormal, XYZ right, XYZ up, Curve curve)
	{
		if ((GeometryObject)(object)curve == (GeometryObject)null)
		{
			return false;
		}

		XYZ dir;
		try
		{
			if (curve is Line line)
			{
				dir = line.Direction;
			}
			else
			{
				dir = curve.GetEndPoint(1) - curve.GetEndPoint(0);
			}
		}
		catch
		{
			return false;
		}

		if (viewNormal == null || right == null || up == null)
		{
			if (view == null || !TryGetViewPlaneAxes(view, out viewNormal, out right, out up))
			{
				return false;
			}
		}

		return IsFabDimLineExactlyViewAxis(viewNormal, right, up, dir);
	}

	private static int ComputeFabDimMaxStackSlots()
	{
		double first = FabDimFirstOffsetSheetInches;
		double gap = FabDimStackGapSheetInches;
		double max = FabDimMaxOffsetSheetInches;
		if (gap < 1E-06)
		{
			return 1;
		}

		int slots = (int)Math.Floor((max - first) / gap) + 1;
		return Math.Max(1, slots);
	}

	#endregion

	#region Intelligent fabrication dim — validate

	/// <summary>
	/// After place: delete tilted dims, dims using connected pipe ends (except OletLocation host-pipe ends),
	/// invalid olet baselines, and duplicates.
	/// </summary>
	private static int ValidateFabDimPlacements(
		Document doc,
		View view,
		IList<FabricationPart> pool,
		Dictionary<long, FabDimGraphNode> graph,
		List<FabDimPlaceRecord> placedRecords,
		string assemblyName,
		string viewLabel)
	{
		int killed = 0;
		killed += DeleteAnyRemainingTiltedLinearDimensions(doc, view);

		HashSet<long> oletDimIds = new HashSet<long>();
		if (placedRecords != null)
		{
			foreach (FabDimPlaceRecord rec in placedRecords)
			{
				if (rec?.Dimension == null || !rec.Dimension.IsValidObject)
				{
					continue;
				}

				if (rec.Intent?.Purpose == DimensionPurpose.OletLocation)
				{
					oletDimIds.Add(GetElementIdValue(((Element)rec.Dimension).Id));

					// Hard rule: olet must be HostPipeEndForOlet → OletMainStation.
					bool validBaseline = rec.Intent.A != null
						&& rec.Intent.B != null
						&& ((IsValidOletLocationBaseline(rec.Intent.A) && rec.Intent.B.Kind == FabDimAnchorKind.OletMainStation)
							|| (IsValidOletLocationBaseline(rec.Intent.B) && rec.Intent.A.Kind == FabDimAnchorKind.OletMainStation));
					if (!validBaseline)
					{
						try
						{
							ElementId id = ((Element)rec.Dimension).Id;
							doc.Delete(id);
							killed++;
							LogFabDimDiag(assemblyName, viewLabel,
								"REJECTED purpose=OletLocation From=" + rec.Intent.A.Kind + " To=" + rec.Intent.B.Kind
								+ " reason=Olets may only be dimensioned from the short end of their host pipe.", 0, 0);
						}
						catch
						{
						}
					}
				}
			}
		}

		HashSet<string> pairKeys = new HashSet<string>(StringComparer.Ordinal);
		List<ElementId> kill = new List<ElementId>();

		XYZ viewNormal = null;
		XYZ right = null;
		XYZ up = null;
		TryGetViewPlaneAxes(view, out viewNormal, out right, out up);

		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim == null || !dim.IsValidObject || dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}

				long dimId = GetElementIdValue(((Element)dim).Id);

				if (!IsFabDimCreatedCurveViewAxis(view, viewNormal, right, up, dim))
				{
					kill.Add(((Element)dim).Id);
					LogFabDimDiag(assemblyName, viewLabel, "REJECTED purpose=validate ids=dim:" + dimId + " reason=tilted", 0, 0);
					continue;
				}

				// OletLocation may legally reference a connected host-pipe end.
				if (!oletDimIds.Contains(dimId) && DimReferencesConnectedPipeEnd(doc, dim, pool, graph))
				{
					kill.Add(((Element)dim).Id);
					LogFabDimDiag(assemblyName, viewLabel, "REJECTED purpose=validate ids=dim:" + dimId + " reason=connected-pipe-end", 0, 0);
					continue;
				}

				// Include measure axis so boxed-out 45 H+V (same witnesses, different axes) both survive.
				string pairKey = BuildDimReferencePairKey(dim, right, up);
				if (!string.IsNullOrEmpty(pairKey) && !pairKeys.Add(pairKey))
				{
					kill.Add(((Element)dim).Id);
					LogFabDimDiag(assemblyName, viewLabel, "REJECTED purpose=validate ids=dim:" + dimId + " reason=duplicate", 0, 0);
				}
			}
		}
		catch
		{
		}

		foreach (ElementId id in kill.Distinct())
		{
			try
			{
				doc.Delete(id);
				killed++;
			}
			catch
			{
			}
		}

		if (killed > 0)
		{
			try { DoRegenNow(doc); } catch { }
		}

		return killed;
	}

	/// <summary>True when a linear dimension curve is exactly parallel to view Right or Up (dot ≥ 0.9999).</summary>
	private static bool IsLinearDimensionAxisAligned(View view, Dimension dim)
	{
		if (view == null || dim == null)
		{
			return false;
		}

		TryGetViewPlaneAxes(view, out XYZ viewNormal, out XYZ right, out XYZ up);
		return IsFabDimCreatedCurveViewAxis(view, viewNormal, right, up, dim);
	}

	private static bool IsLinearDimensionAxisAligned(View view, Curve curve)
	{
		if (view == null || (GeometryObject)(object)curve == (GeometryObject)null)
		{
			return false;
		}

		TryGetViewPlaneAxes(view, out XYZ viewNormal, out XYZ right, out XYZ up);
		return IsFabDimCreatedCurveViewAxis(view, viewNormal, right, up, curve);
	}

	/// <summary>Hard law: never leave a tilted Linear dim on a spool view.</summary>
	private static int DeleteAnyRemainingTiltedLinearDimensions(Document doc, View view)
	{
		if (doc == null || view == null || view is View3D || view.IsTemplate)
		{
			return 0;
		}

		TryGetViewPlaneAxes(view, out XYZ viewNormal, out XYZ right, out XYZ up);

		List<ElementId> kill = new List<ElementId>();
		foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
			.OfClass(typeof(Dimension))
			.Cast<Dimension>())
		{
			try
			{
				if (dim == null || !dim.IsValidObject)
				{
					continue;
				}

				// Kill any non-sheet-axis dim line — including Linear that Revit reoriented to a chord.
				if (dim.DimensionType != null && !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}

				XYZ dir = TryReadDimensionCurveDirection(dim);
				if (dir == null || !IsDimensionDirectionViewAxisAligned(view, dir))
				{
					kill.Add(((Element)dim).Id);
				}
			}
			catch
			{
				try { kill.Add(((Element)dim).Id); } catch { }
			}
		}

		int n = 0;
		foreach (ElementId id in kill.Distinct())
		{
			try
			{
				// Pinned corner dims cannot be deleted until unpinned — never leave tilted pinned.
				Element el = doc.GetElement(id);
				if (el != null)
				{
					try { el.Pinned = false; } catch { }
				}
				doc.Delete(id);
				n++;
			}
			catch
			{
			}
		}

		if (n > 0)
		{
			try { DoRegenNow(doc); } catch { }
		}

		return n;
	}

	private static bool DimReferencesConnectedPipeEnd(
		Document doc,
		Dimension dim,
		IList<FabricationPart> pool,
		Dictionary<long, FabDimGraphNode> graph)
	{
		if (doc == null || dim == null || pool == null)
		{
			return false;
		}

		try
		{
			ReferenceArray refs = dim.References;
			if (refs == null)
			{
				return false;
			}

			for (int i = 0; i < refs.Size; i++)
			{
				Reference r = refs.get_Item(i);
				if (r == null)
				{
					continue;
				}

				Element e = doc.GetElement(r.ElementId);
				FabricationPart fab = e as FabricationPart;
				if (fab == null || !IsPipeRunPart(fab))
				{
					continue;
				}

				// If this pipe has NO open connectors, any dim to it is a connected-end abuse.
				if (!HasAnyOpenPipeConnector(fab, pool))
				{
					return true;
				}
			}
		}
		catch
		{
		}

		return false;
	}

	private static string BuildDimReferencePairKey(Dimension dim, XYZ right = null, XYZ up = null)
	{
		try
		{
			ReferenceArray refs = dim.References;
			if (refs == null || refs.Size < 2)
			{
				return string.Empty;
			}

			List<long> ids = new List<long>();
			for (int i = 0; i < refs.Size; i++)
			{
				ids.Add(refs.get_Item(i).ElementId.Value);
			}

			ids.Sort();
			string key = string.Join("|", ids);

			// Same element pair can legally carry both an H and a V dim (true-45 box-out).
			try
			{
				Curve curve = dim.Curve;
				XYZ dir = null;
				if (curve is Line line)
				{
					dir = line.Direction;
				}
				else if ((GeometryObject)(object)curve != null)
				{
					dir = (curve.GetEndPoint(1) - curve.GetEndPoint(0));
				}

				if (dir != null && dir.GetLength() > 1E-09 && right != null && up != null)
				{
					dir = dir.Normalize();
					double alongR = Math.Abs(dir.DotProduct(right.Normalize()));
					double alongU = Math.Abs(dir.DotProduct(up.Normalize()));
					key += alongR >= alongU ? "|H" : "|V";
				}
			}
			catch
			{
			}

			return key;
		}
		catch
		{
			return string.Empty;
		}
	}

	#endregion

	#region Intelligent fabrication dim — diagnostics

	private static void LogFabDimDiag(string assemblyName, string viewLabel, string message, int dimsBefore, int dimsAfter)
	{
		LogFabDimEngineFile(message);
		try
		{
			TryAppendAutoDimDiagnosticLog(assemblyName ?? "intel-fab", viewLabel ?? "?", message ?? string.Empty, dimsBefore, dimsAfter);
		}
		catch
		{
		}
	}

	private static void LogFabDimEngineFile(string message)
	{
		try
		{
			string folder = InstallLayout.GetPreferredModuleFolder();
			if (string.IsNullOrWhiteSpace(folder))
			{
				return;
			}

			Directory.CreateDirectory(folder);
			string path = Path.Combine(folder, "IntelligentFabDimEngine.log");
			string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
				+ "\t" + (message ?? string.Empty) + Environment.NewLine;
			File.AppendAllText(path, line, Encoding.UTF8);
		}
		catch
		{
		}
	}

	#endregion
}
