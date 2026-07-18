using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Step 4 — orientation and offset direction from the connector graph (never view guesswork).
/// Rules are global across all assembly detail views; only View.UpDirection / View.RightDirection
/// change per view so offset side tracks the view plane — never branch on view name or label.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	public enum SpoolRunSegmentOrientation
	{
		Horizontal,
		Vertical
	}

	/// <summary>One ordered point along a physical run path (upstream → downstream).</summary>
	public sealed class SpoolConnectorGraphNode
	{
		public FabricationPart Part { get; set; }
		public ElementId PartId { get; set; }
		public XYZ PointWorld { get; set; }
		public int PathIndex { get; set; }
	}

	/// <summary>Consecutive pair along a traced run path with fixed offset side.</summary>
	public sealed class SpoolRunSegmentPlan
	{
		public SpoolConnectorGraphNode From { get; set; }
		public SpoolConnectorGraphNode To { get; set; }
		public SpoolRunSegmentOrientation Orientation { get; set; }
		public XYZ SegmentDirectionWorld { get; set; }
		public XYZ OffsetDirectionView { get; set; }
		public double LengthFeet { get; set; }
		public int PathIndex { get; set; }
	}

	private sealed class SpoolGraphWalkState
	{
		public FabricationPart Part;
		public Connector EntryConnector;
	}

	private const double ConnectorMateToleranceFeet = 0.08;
	private const double SegmentLengthEpsilonFeet = 1.0 / 24.0;
	private const double VerticalDominanceRatio = 1.05;

	/// <summary>
	/// Classify a world segment as horizontal or vertical by dominant delta axis (Z vs X/Y).
	/// </summary>
	public static SpoolRunSegmentOrientation ClassifySegmentOrientation(XYZ fromWorld, XYZ toWorld)
	{
		if (fromWorld == null || toWorld == null)
		{
			return SpoolRunSegmentOrientation.Horizontal;
		}

		XYZ delta = toWorld - fromWorld;
		double horizontal = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
		double vertical = Math.Abs(delta.Z);
		if (vertical > horizontal * VerticalDominanceRatio)
		{
			return SpoolRunSegmentOrientation.Vertical;
		}

		return SpoolRunSegmentOrientation.Horizontal;
	}

	/// <summary>
	/// Fixed global offset side: horizontal runs → +View.Up; vertical runs → −View.Right.
	/// </summary>
	public static bool TryResolveSpoolDimensionOffsetDirection(
		View view,
		SpoolRunSegmentOrientation orientation,
		out XYZ offsetDirectionView,
		out string diagnostic)
	{
		offsetDirectionView = null;
		diagnostic = string.Empty;
		if (view == null)
		{
			diagnostic = "Missing view.";
			return false;
		}

		XYZ raw = orientation == SpoolRunSegmentOrientation.Vertical
			? NegateSafe(view.RightDirection)
			: view.UpDirection;
		if (raw == null || raw.GetLength() < 1E-09)
		{
			diagnostic = orientation == SpoolRunSegmentOrientation.Vertical
				? "View has no valid RightDirection."
				: "View has no valid UpDirection.";
			return false;
		}

		offsetDirectionView = raw.Normalize();
		return true;
	}

	/// <summary>
	/// Trace all main-run connector-graph paths in physical connection order and expand to segment plans.
	/// </summary>
	public static IReadOnlyList<SpoolRunSegmentPlan> BuildSpoolRunSegmentPlans(View view, IList<FabricationPart> parts)
	{
		if (view == null || parts == null || parts.Count == 0)
		{
			return Array.Empty<SpoolRunSegmentPlan>();
		}

		List<SpoolRunSegmentPlan> plans = new List<SpoolRunSegmentPlan>();
		HashSet<string> seenPathKeys = new HashSet<string>(StringComparer.Ordinal);
		int pathCounter = 0;
		foreach (IReadOnlyList<SpoolConnectorGraphNode> path in BuildAllSpoolConnectorGraphPaths(parts))
		{
			if (path == null || path.Count < 2)
			{
				continue;
			}

			string pathKey = BuildPathKey(path);
			if (!seenPathKeys.Add(pathKey))
			{
				continue;
			}

			IReadOnlyList<SpoolRunSegmentPlan> pathPlans = BuildSegmentPlansForPath(view, path, pathCounter++);
			if (pathPlans != null && pathPlans.Count > 0)
			{
				plans.AddRange(pathPlans);
			}
		}

		return plans;
	}

	/// <summary>Orientation for an olet pick-up measured along the host run (anchor → takeoff).</summary>
	public static bool TryResolveOletPickUpSegmentPlan(
		View view,
		SpoolOletStackDimensionIntent intent,
		out SpoolRunSegmentPlan segmentPlan,
		out string diagnostic)
	{
		segmentPlan = null;
		diagnostic = string.Empty;
		if (view == null || intent == null)
		{
			diagnostic = "Missing view or olet intent.";
			return false;
		}

		XYZ from = intent.AnchorPointWorld;
		XYZ to = intent.OletTakeoffPointWorld;
		if (from == null || to == null)
		{
			diagnostic = "Missing anchor or takeoff point.";
			return false;
		}

		if (from.DistanceTo(to) < SegmentLengthEpsilonFeet)
		{
			diagnostic = "Anchor and takeoff are too close to classify.";
			return false;
		}

		SpoolRunSegmentOrientation orientation = ClassifySegmentOrientation(from, to);
		if (!TryResolveSpoolDimensionOffsetDirection(view, orientation, out XYZ offsetDirection, out diagnostic))
		{
			return false;
		}

		segmentPlan = new SpoolRunSegmentPlan
		{
			From = new SpoolConnectorGraphNode
			{
				Part = intent.HostRunPipe,
				PartId = intent.AnchorPart != null ? intent.AnchorPart.Id : ElementId.InvalidElementId,
				PointWorld = from,
				PathIndex = 0
			},
			To = new SpoolConnectorGraphNode
			{
				Part = intent.OletPart,
				PartId = intent.OletPart != null ? ((Element)intent.OletPart).Id : ElementId.InvalidElementId,
				PointWorld = to,
				PathIndex = 1
			},
			Orientation = orientation,
			SegmentDirectionWorld = (to - from).Normalize(),
			OffsetDirectionView = offsetDirection,
			LengthFeet = from.DistanceTo(to),
			PathIndex = intent.StackSlot
		};
		return true;
	}

	public static IReadOnlyList<SpoolConnectorGraphNode> BuildSpoolConnectorGraphPaths(IList<FabricationPart> parts)
	{
		IReadOnlyList<IReadOnlyList<SpoolConnectorGraphNode>> allPaths = BuildAllSpoolConnectorGraphPaths(parts);
		return allPaths.Count > 0 ? allPaths[0] : Array.Empty<SpoolConnectorGraphNode>();
	}

	public static IReadOnlyList<IReadOnlyList<SpoolConnectorGraphNode>> BuildAllSpoolConnectorGraphPaths(IList<FabricationPart> parts)
	{
		if (parts == null || parts.Count == 0)
		{
			return Array.Empty<IReadOnlyList<SpoolConnectorGraphNode>>();
		}

		List<IReadOnlyList<SpoolConnectorGraphNode>> paths = new List<IReadOnlyList<SpoolConnectorGraphNode>>();
		HashSet<string> visitedWalkKeys = new HashSet<string>(StringComparer.Ordinal);
		foreach ((FabricationPart part, Connector connector) seed in FindRunPathSeedConnectors(parts))
		{
			string seedKey = BuildWalkKey(((Element)seed.part).Id, seed.connector);
			if (!visitedWalkKeys.Add(seedKey))
			{
				continue;
			}

			if (!TryTraceRunPathFromSeed(seed.part, seed.connector, parts, visitedWalkKeys, out List<SpoolConnectorGraphNode> path)
				|| path == null
				|| path.Count < 2)
			{
				continue;
			}

			paths.Add(path);
		}

		return paths;
	}

	private static IReadOnlyList<SpoolRunSegmentPlan> BuildSegmentPlansForPath(
		View view,
		IReadOnlyList<SpoolConnectorGraphNode> path,
		int pathIndex)
	{
		List<SpoolRunSegmentPlan> plans = new List<SpoolRunSegmentPlan>();
		for (int i = 0; i < path.Count - 1; i++)
		{
			SpoolConnectorGraphNode from = path[i];
			SpoolConnectorGraphNode to = path[i + 1];
			if (from?.PointWorld == null || to?.PointWorld == null)
			{
				continue;
			}

			double length = from.PointWorld.DistanceTo(to.PointWorld);
			if (length < SegmentLengthEpsilonFeet)
			{
				continue;
			}

			SpoolRunSegmentOrientation orientation = ClassifySegmentOrientation(from.PointWorld, to.PointWorld);
			if (!TryResolveSpoolDimensionOffsetDirection(view, orientation, out XYZ offsetDirection, out _))
			{
				continue;
			}

			plans.Add(new SpoolRunSegmentPlan
			{
				From = from,
				To = to,
				Orientation = orientation,
				SegmentDirectionWorld = (to.PointWorld - from.PointWorld).Normalize(),
				OffsetDirectionView = offsetDirection,
				LengthFeet = length,
				PathIndex = pathIndex
			});
		}

		return plans;
	}

	private static IEnumerable<(FabricationPart part, Connector connector)> FindRunPathSeedConnectors(IList<FabricationPart> parts)
	{
		List<(FabricationPart, Connector)> seeds = new List<(FabricationPart, Connector)>();
		foreach (FabricationPart part in parts)
		{
			if (part == null || IsOletPart(part) || IsValvePart(part))
			{
				continue;
			}

			foreach (Connector connector in GetMainRunConnectors(part, parts))
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				if (!TryResolvePhysicalMate(part, connector, parts, out FabricationPart mate, out _)
					|| mate == null
					|| IsAutoDimGraphPassThroughPart(mate))
				{
					seeds.Add((part, connector));
					continue;
				}

				if (IsPipeRunPart(part) && (IsFittingLikeForSpoolDim(mate) || FabricationPartClassification.IsFlangePart(mate, ((Element)mate).Document)))
				{
					seeds.Add((part, connector));
				}
			}
		}

		return seeds
			.OrderBy((seed) => GetElementIdValue(((Element)seed.Item1).Id))
			.ThenBy((seed) => FormatConnectorSeed(seed.Item2));
	}

	private static bool TryTraceRunPathFromSeed(
		FabricationPart seedPart,
		Connector seedConnector,
		IList<FabricationPart> parts,
		HashSet<string> visitedWalkKeys,
		out List<SpoolConnectorGraphNode> path)
	{
		path = new List<SpoolConnectorGraphNode>();
		if (seedPart == null || seedConnector?.Origin == null)
		{
			return false;
		}

		SpoolGraphWalkState state = new SpoolGraphWalkState
		{
			Part = seedPart,
			EntryConnector = seedConnector
		};
		if (!TryAppendGraphNode(state.Part, state.EntryConnector?.Origin, parts, path))
		{
			return false;
		}

		int guard = 0;
		while (guard++ < 256)
		{
			if (!TryGetNextWalkState(state.Part, state.EntryConnector, parts, visitedWalkKeys, out SpoolGraphWalkState next))
			{
				break;
			}

			state = next;
			XYZ nodePoint = state.EntryConnector?.Origin ?? TryGetPartDimensionAnchorPoint(state.Part, parts);
			if (nodePoint == null)
			{
				break;
			}

			if (path.Count > 0 && path[path.Count - 1].PointWorld.DistanceTo(nodePoint) < SegmentLengthEpsilonFeet)
			{
				continue;
			}

			if (!TryAppendGraphNode(state.Part, nodePoint, parts, path))
			{
				break;
			}
		}

		for (int i = 0; i < path.Count; i++)
		{
			path[i].PathIndex = i;
		}

		return path.Count >= 2;
	}

	private static bool TryAppendGraphNode(
		FabricationPart part,
		XYZ pointWorld,
		IList<FabricationPart> parts,
		List<SpoolConnectorGraphNode> path)
	{
		if (part == null || pointWorld == null)
		{
			return false;
		}

		XYZ resolved = TryGetPartDimensionAnchorPoint(part, parts) ?? pointWorld;
		path.Add(new SpoolConnectorGraphNode
		{
			Part = part,
			PartId = ((Element)part).Id,
			PointWorld = resolved,
			PathIndex = path.Count
		});
		return true;
	}

	private static bool TryGetNextWalkState(
		FabricationPart current,
		Connector entryConnector,
		IList<FabricationPart> parts,
		HashSet<string> visitedWalkKeys,
		out SpoolGraphWalkState next)
	{
		next = null;
		if (current == null)
		{
			return false;
		}

		List<Connector> exits = GetMainRunExitConnectors(current, entryConnector, parts);
		foreach (Connector exitConnector in exits)
		{
			string walkKey = BuildWalkKey(((Element)current).Id, exitConnector);
			if (!visitedWalkKeys.Add(walkKey))
			{
				continue;
			}

			if (!TryResolvePhysicalMate(current, exitConnector, parts, out FabricationPart mate, out Connector mateConnector))
			{
				return false;
			}

			if (IsAutoDimGraphPassThroughPart(mate))
			{
				if (TryWalkThroughPassThrough(mate, mateConnector, parts, visitedWalkKeys, out next))
				{
					return true;
				}

				continue;
			}

			if (IsOletPart(mate) || IsValvePart(mate))
			{
				continue;
			}

			next = new SpoolGraphWalkState
			{
				Part = mate,
				EntryConnector = mateConnector
			};
			return true;
		}

		return false;
	}

	private static bool TryWalkThroughPassThrough(
		FabricationPart passThrough,
		Connector entryConnector,
		IList<FabricationPart> parts,
		HashSet<string> visitedWalkKeys,
		out SpoolGraphWalkState next)
	{
		next = null;
		if (passThrough == null)
		{
			return false;
		}

		foreach (Connector exitConnector in GetMainRunExitConnectors(passThrough, entryConnector, parts))
		{
			string walkKey = BuildWalkKey(((Element)passThrough).Id, exitConnector);
			if (!visitedWalkKeys.Add(walkKey))
			{
				continue;
			}

			if (!TryResolvePhysicalMate(passThrough, exitConnector, parts, out FabricationPart mate, out Connector mateConnector))
			{
				continue;
			}

			if (IsAutoDimGraphPassThroughPart(mate))
			{
				if (TryWalkThroughPassThrough(mate, mateConnector, parts, visitedWalkKeys, out next))
				{
					return true;
				}

				continue;
			}

			if (IsOletPart(mate) || IsValvePart(mate))
			{
				continue;
			}

			next = new SpoolGraphWalkState
			{
				Part = mate,
				EntryConnector = mateConnector
			};
			return true;
		}

		return false;
	}

	private static IEnumerable<Connector> GetMainRunConnectors(FabricationPart part, IList<FabricationPart> parts)
	{
		if (part == null)
		{
			yield break;
		}

		if (IsPipeRunPart(part) && TryGetHostRunEndConnectors(part, out Connector endA, out Connector endB))
		{
			yield return endA;
			yield return endB;
			yield break;
		}

		foreach (Connector connector in ListConnectors(part))
		{
			if (connector?.Origin == null)
			{
				continue;
			}

			if (IsBranchConnector(part, connector, parts))
			{
				continue;
			}

			yield return connector;
		}
	}

	private static List<Connector> GetMainRunExitConnectors(
		FabricationPart part,
		Connector entryConnector,
		IList<FabricationPart> parts)
	{
		List<Connector> exits = new List<Connector>();
		if (part == null)
		{
			return exits;
		}

		foreach (Connector connector in GetMainRunConnectors(part, parts))
		{
			if (connector?.Origin == null || ReferenceEqualsConnector(connector, entryConnector))
			{
				continue;
			}

			exits.Add(connector);
		}

		exits.Sort((a, b) => string.CompareOrdinal(FormatConnectorSeed(a), FormatConnectorSeed(b)));
		return exits;
	}

	private static bool IsBranchConnector(FabricationPart part, Connector connector, IList<FabricationPart> parts)
	{
		if (part == null || connector == null)
		{
			return false;
		}

		FabricationPart mate = FindMateAtConnector(part, connector, parts);
		if (mate == null)
		{
			return false;
		}

		if (IsOletPart(part))
		{
			return IsPipeRunPart(mate);
		}

		if (IsPipeRunPart(part) && IsOletPart(mate))
		{
			return true;
		}

		if (IsFittingLikeForSpoolDim(part) && IsPipeRunPart(mate) && IsOletBranchTakeoffPipe(mate, parts))
		{
			return true;
		}

		return false;
	}

	private static bool TryResolvePhysicalMate(
		FabricationPart self,
		Connector connector,
		IList<FabricationPart> parts,
		out FabricationPart mate,
		out Connector mateConnector)
	{
		mate = null;
		mateConnector = null;
		if (self == null || connector?.Origin == null || parts == null)
		{
			return false;
		}

		if (TryResolveConnectedMate(self, connector, parts, out mate, out mateConnector))
		{
			return mate != null && mateConnector != null;
		}

		mate = FindMateAtConnector(self, connector, parts);
		if (mate == null)
		{
			return false;
		}

		mateConnector = FindConnectorNearOrigin(mate, connector.Origin);
		return mateConnector != null;
	}

	private static bool TryResolveConnectedMate(
		FabricationPart self,
		Connector connector,
		IList<FabricationPart> parts,
		out FabricationPart mate,
		out Connector mateConnector)
	{
		mate = null;
		mateConnector = null;
		if (connector == null)
		{
			return false;
		}

		try
		{
			ConnectorSet refs = connector.AllRefs;
			if (refs == null)
			{
				return false;
			}

			foreach (Connector refConnector in refs)
			{
				if (refConnector?.Origin == null)
				{
					continue;
				}

				foreach (FabricationPart candidate in parts)
				{
					if (candidate == null || ((Element)candidate).Id == ((Element)self).Id)
					{
						continue;
					}

					Connector matched = FindConnectorNearOrigin(candidate, refConnector.Origin);
					if (matched != null)
					{
						mate = candidate;
						mateConnector = matched;
						return true;
					}
				}
			}
		}
		catch
		{
		}

		return false;
	}

	private static Connector FindConnectorNearOrigin(FabricationPart part, XYZ origin)
	{
		if (part == null || origin == null)
		{
			return null;
		}

		Connector best = null;
		double bestDistance = ConnectorMateToleranceFeet;
		foreach (Connector connector in ListConnectors(part))
		{
			if (connector?.Origin == null)
			{
				continue;
			}

			double distance = connector.Origin.DistanceTo(origin);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = connector;
			}
		}

		return best;
	}

	private static bool IsAutoDimGraphPassThroughPart(FabricationPart part)
	{
		if (part == null)
		{
			return false;
		}

		return IsGasketPart(part) || IsWeldPart(part) || FabricationPartClassification.IsBoltKitPart(part);
	}

	private static XYZ TryGetPartDimensionAnchorPoint(FabricationPart part, IList<FabricationPart> parts)
	{
		if (part == null)
		{
			return null;
		}

		if (IsFittingLikeForSpoolDim(part) || FabricationPartClassification.IsFlangePart(part, ((Element)part).Document))
		{
			return GetFabricationFittingDimensionAnchor(part, null, null, parts);
		}

		if (IsPipeRunPart(part))
		{
			foreach (Connector connector in GetMainRunConnectors(part, parts))
			{
				if (connector?.Origin != null)
				{
					return connector.Origin;
				}
			}
		}

		return TryGetFabricationPartOrigin(part);
	}

	private static bool ReferenceEqualsConnector(Connector left, Connector right)
	{
		if (left == null || right == null)
		{
			return false;
		}

		if (ReferenceEquals(left, right))
		{
			return true;
		}

		return left.Origin != null
			&& right.Origin != null
			&& left.Origin.DistanceTo(right.Origin) < 1E-06;
	}

	private static string BuildWalkKey(ElementId partId, Connector connector)
	{
		return GetElementIdValue(partId).ToString(CultureInfo.InvariantCulture)
			+ "|"
			+ FormatConnectorSeed(connector);
	}

	private static string FormatConnectorSeed(Connector connector)
	{
		if (connector?.Origin == null)
		{
			return "?";
		}

		XYZ origin = connector.Origin;
		return string.Format(
			CultureInfo.InvariantCulture,
			"{0:F4}|{1:F4}|{2:F4}",
			origin.X,
			origin.Y,
			origin.Z);
	}

	private static string BuildPathKey(IReadOnlyList<SpoolConnectorGraphNode> path)
	{
		if (path == null || path.Count == 0)
		{
			return string.Empty;
		}

		string forward = string.Join("->", path.Select((node) => FormatPathNode(node)));
		string reverse = string.Join("->", path.Reverse().Select((node) => FormatPathNode(node)));
		return string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse;
	}

	private static string FormatPathNode(SpoolConnectorGraphNode node)
	{
		if (node == null)
		{
			return "?";
		}

		XYZ point = node.PointWorld;
		if (point == null)
		{
			return GetElementIdValue(node.PartId).ToString(CultureInfo.InvariantCulture);
		}

		return GetElementIdValue(node.PartId).ToString(CultureInfo.InvariantCulture)
			+ "@"
			+ string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3}", point.X, point.Y, point.Z);
	}

	private static XYZ NegateSafe(XYZ vector)
	{
		if (vector == null)
		{
			return null;
		}

		try
		{
			return vector.Negate();
		}
		catch
		{
			return new XYZ(-vector.X, -vector.Y, -vector.Z);
		}
	}

	private static int TryValidateSpoolOrientationPlans(
		View view,
		IList<FabricationPart> parts,
		List<string> failureNotes)
	{
		if (view == null || parts == null)
		{
			return 0;
		}

		int planned = 0;
		foreach (SpoolRunSegmentPlan segment in BuildSpoolRunSegmentPlans(view, parts))
		{
			if (segment?.From?.PointWorld == null || segment.To?.PointWorld == null || segment.OffsetDirectionView == null)
			{
				failureNotes?.Add("Orientation skip: incomplete segment plan.");
				continue;
			}

			planned++;
			TryAppendAutoDimPlacementLog(
				view.Name,
				"RunSegment path=" + segment.PathIndex
				+ " " + segment.Orientation
				+ " len=" + segment.LengthFeet.ToString("0.###", CultureInfo.InvariantCulture)
				+ " from=" + GetElementIdValue(segment.From.PartId)
				+ " to=" + GetElementIdValue(segment.To.PartId)
				+ " offset=(" + segment.OffsetDirectionView.X.ToString("0.###", CultureInfo.InvariantCulture)
				+ "," + segment.OffsetDirectionView.Y.ToString("0.###", CultureInfo.InvariantCulture)
				+ "," + segment.OffsetDirectionView.Z.ToString("0.###", CultureInfo.InvariantCulture) + ")");
		}

		foreach (SpoolOletRunStackPlan oletPlan in BuildOletRunStackPlans(parts))
		{
			if (oletPlan?.Dimensions == null)
			{
				continue;
			}

			foreach (SpoolOletStackDimensionIntent intent in oletPlan.Dimensions)
			{
				if (!TryResolveOletPickUpSegmentPlan(view, intent, out SpoolRunSegmentPlan pickUpSegment, out string diagnostic))
				{
					failureNotes?.Add("Olet orientation skip: " + diagnostic);
					continue;
				}

				planned++;
				TryAppendAutoDimPlacementLog(
					view.Name,
					"OletPickUp slot=" + intent.StackSlot
					+ " " + pickUpSegment.Orientation
					+ " host=" + ((Element)oletPlan.HostRunPipe).Id.Value
					+ " olet=" + ((Element)intent.OletPart).Id.Value
					+ " offset=(" + pickUpSegment.OffsetDirectionView.X.ToString("0.###", CultureInfo.InvariantCulture)
					+ "," + pickUpSegment.OffsetDirectionView.Y.ToString("0.###", CultureInfo.InvariantCulture)
					+ "," + pickUpSegment.OffsetDirectionView.Z.ToString("0.###", CultureInfo.InvariantCulture) + ")");
			}
		}

		return planned;
	}
}
