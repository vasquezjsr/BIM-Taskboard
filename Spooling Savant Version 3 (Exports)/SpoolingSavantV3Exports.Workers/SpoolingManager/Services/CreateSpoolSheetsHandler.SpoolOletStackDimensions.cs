using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public partial class CreateSpoolSheetsHandler
{
	/// <summary>One olet pick-up dimension in a stacked series on a host run pipe.</summary>
	public sealed class SpoolOletStackDimensionIntent
	{
		public FabricationPart HostRunPipe { get; set; }
		public FabricationPart OletPart { get; set; }
		public Element AnchorPart { get; set; }
		public XYZ AnchorPointWorld { get; set; }
		public XYZ OletTakeoffPointWorld { get; set; }
		public int StackSlot { get; set; }
		public double DistanceFromAnchorFeet { get; set; }
	}

	/// <summary>Stacked olet pick-up plan for a single host run pipe.</summary>
	public sealed class SpoolOletRunStackPlan
	{
		public FabricationPart HostRunPipe { get; set; }
		public Element AnchorPart { get; set; }
		public XYZ AnchorPointWorld { get; set; }
		public bool RequiresStacking { get; set; }
		public IReadOnlyList<SpoolOletStackDimensionIntent> Dimensions { get; set; } = Array.Empty<SpoolOletStackDimensionIntent>();
	}

	/// <summary>
	/// Step 3 — when multiple olets share a host run, build one pick-up dim per olet from a common short-side anchor,
	/// ordered closest-first with increasing stack slots (0 = innermost). Olets welded to DIFFERENT physical pipe
	/// segments of the SAME overall run (joined end-to-end by field welds) are grouped together and still measured
	/// from one shared, consistent short-side anchor — they must never each pick their own local segment's end, or
	/// they end up pulling dimensions from opposite sides of the run.
	/// </summary>
	public static IReadOnlyList<SpoolOletRunStackPlan> BuildOletRunStackPlans(IList<FabricationPart> parts)
	{
		if (parts == null || parts.Count == 0)
		{
			return Array.Empty<SpoolOletRunStackPlan>();
		}

		Dictionary<long, List<FabricationPart>> oletsByRunKey = new Dictionary<long, List<FabricationPart>>();
		Dictionary<long, List<FabricationPart>> chainByRunKey = new Dictionary<long, List<FabricationPart>>();
		foreach (FabricationPart part in parts)
		{
			if (part == null || !IsOletPart(part))
			{
				continue;
			}

			if (!TryFindOletHostRunMate(part, parts, out FabricationPart mate))
			{
				continue;
			}

			List<FabricationPart> chain = GetWeldConnectedPipeChain(mate, parts);
			FabricationPart representative = chain.OrderByDescending(GetFabricationStraightLineLength).FirstOrDefault() ?? mate;
			long key = ((Element)representative).Id.Value;

			if (!oletsByRunKey.TryGetValue(key, out List<FabricationPart> bucket))
			{
				bucket = new List<FabricationPart>();
				oletsByRunKey[key] = bucket;
				chainByRunKey[key] = chain;
			}

			bucket.Add(part);
		}

		List<SpoolOletRunStackPlan> plans = new List<SpoolOletRunStackPlan>();
		foreach (KeyValuePair<long, List<FabricationPart>> entry in oletsByRunKey)
		{
			List<FabricationPart> chain = chainByRunKey[entry.Key];
			FabricationPart hostRun = chain.OrderByDescending(GetFabricationStraightLineLength).FirstOrDefault();
			if (hostRun == null || entry.Value.Count == 0)
			{
				continue;
			}

			if (!TryBuildSingleRunStackPlan(hostRun, chain, entry.Value, parts, out SpoolOletRunStackPlan plan))
			{
				continue;
			}

			plans.Add(plan);
		}

		return plans;
	}

	private static bool TryBuildSingleRunStackPlan(
		FabricationPart hostRun,
		List<FabricationPart> hostRunChain,
		IList<FabricationPart> oletsOnRun,
		IList<FabricationPart> parts,
		out SpoolOletRunStackPlan plan)
	{
		plan = null;
		if (hostRun == null || oletsOnRun == null || oletsOnRun.Count == 0)
		{
			return false;
		}

		if (!TryResolveShortSideRunAnchor(hostRunChain, parts, oletsOnRun, out Element anchorPart, out XYZ anchorPoint))
		{
			return false;
		}

		List<(FabricationPart olet, XYZ takeoff, double distance)> ordered = new List<(FabricationPart, XYZ, double)>();
		foreach (FabricationPart olet in oletsOnRun)
		{
			if (olet == null || !TryGetOletTakeoffPointOnChain(olet, hostRunChain, parts, out XYZ takeoff))
			{
				continue;
			}

			double distance = anchorPoint.DistanceTo(takeoff);
			ordered.Add((olet, takeoff, distance));
		}

		if (ordered.Count == 0)
		{
			return false;
		}

		ordered.Sort((a, b) => a.distance.CompareTo(b.distance));
		List<SpoolOletStackDimensionIntent> intents = new List<SpoolOletStackDimensionIntent>();
		for (int i = 0; i < ordered.Count; i++)
		{
			(FabricationPart olet, XYZ takeoff, double distance) item = ordered[i];
			intents.Add(new SpoolOletStackDimensionIntent
			{
				HostRunPipe = hostRun,
				OletPart = item.olet,
				AnchorPart = anchorPart,
				AnchorPointWorld = anchorPoint,
				OletTakeoffPointWorld = item.takeoff,
				StackSlot = i,
				DistanceFromAnchorFeet = item.distance
			});
		}

		plan = new SpoolOletRunStackPlan
		{
			HostRunPipe = hostRun,
			AnchorPart = anchorPart,
			AnchorPointWorld = anchorPoint,
			RequiresStacking = intents.Count > 1,
			Dimensions = intents
		};
		return true;
	}

	/// <summary>Immediate host pipe segment the olet is welded/mated onto (before walking the weld chain).</summary>
	private static bool TryFindOletHostRunMate(FabricationPart olet, IList<FabricationPart> parts, out FabricationPart hostRun)
	{
		hostRun = null;
		if (olet == null)
		{
			return false;
		}

		foreach (Connector connector in ListConnectors(olet))
		{
			if (connector?.Origin == null)
			{
				continue;
			}

			FabricationPart mate = FindMateAtConnector(olet, connector, parts);
			if (mate == null || !IsPipeRunPart(mate))
			{
				continue;
			}

			if (!IsOletHostRunPipe(mate, parts))
			{
				continue;
			}

			double len = GetFabricationStraightLineLength(mate);
			if (hostRun == null || len > GetFabricationStraightLineLength(hostRun))
			{
				hostRun = mate;
			}
		}

		return hostRun != null;
	}

	/// <summary>True if the olet's takeoff connector mates to ANY pipe segment belonging to the given chain.</summary>
	private static bool TryGetOletTakeoffPointOnChain(
		FabricationPart olet,
		List<FabricationPart> chain,
		IList<FabricationPart> parts,
		out XYZ takeoffPoint)
	{
		takeoffPoint = null;
		if (olet == null || chain == null || chain.Count == 0)
		{
			return false;
		}

		HashSet<long> chainIds = new HashSet<long>(chain.Select((p) => ((Element)p).Id.Value));
		foreach (Connector connector in ListConnectors(olet))
		{
			if (connector?.Origin == null)
			{
				continue;
			}

			FabricationPart mate = FindMateAtConnector(olet, connector, parts);
			if (mate != null && chainIds.Contains(((Element)mate).Id.Value))
			{
				takeoffPoint = GetFabricationFittingDimensionAnchor(olet, mate, null, parts) ?? connector.Origin;
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Walks outward from <paramref name="start"/> through field-weld/gasket joints (and direct pipe-to-pipe
	/// mates) to collect every FabricationPart that is physically part of the SAME continuous pipe run —
	/// stopping at any fitting, olet, or valve, which marks the true end of this run.
	/// </summary>
	private static List<FabricationPart> GetWeldConnectedPipeChain(FabricationPart start, IList<FabricationPart> parts)
	{
		List<FabricationPart> chain = new List<FabricationPart>();
		if (start == null || parts == null)
		{
			return chain;
		}

		HashSet<long> visited = new HashSet<long>();
		Queue<FabricationPart> queue = new Queue<FabricationPart>();
		queue.Enqueue(start);
		while (queue.Count > 0)
		{
			FabricationPart current = queue.Dequeue();
			if (current == null || !visited.Add(((Element)current).Id.Value))
			{
				continue;
			}

			chain.Add(current);
			foreach (Connector connector in ListConnectors(current))
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				FabricationPart mate = FindMateAtConnector(current, connector, parts);
				if (mate == null)
				{
					continue;
				}

				if (IsWeldPart(mate) || IsGasketPart(mate))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, parts);
					if (beyond != null && IsPipeRunPart(beyond) && !visited.Contains(((Element)beyond).Id.Value))
					{
						queue.Enqueue(beyond);
					}
					continue;
				}

				if (IsPipeRunPart(mate) && !IsOletPart(mate) && !visited.Contains(((Element)mate).Id.Value))
				{
					queue.Enqueue(mate);
				}
			}
		}

		return chain;
	}

	/// <summary>The 2 connectors on a SINGLE pipe segment with the greatest spread between them (its own local ends).</summary>
	private static bool TryGetHostRunEndConnectors(FabricationPart hostRun, out Connector endA, out Connector endB)
	{
		endA = null;
		endB = null;
		List<Connector> connectors = ListConnectors(hostRun)
			.Where((c) => c?.Origin != null)
			.ToList();
		if (connectors.Count < 2)
		{
			return false;
		}

		double bestSpread = -1.0;
		for (int i = 0; i < connectors.Count; i++)
		{
			for (int j = i + 1; j < connectors.Count; j++)
			{
				double spread = connectors[i].Origin.DistanceTo(connectors[j].Origin);
				if (spread > bestSpread)
				{
					bestSpread = spread;
					endA = connectors[i];
					endB = connectors[j];
				}
			}
		}

		return endA != null && endB != null;
	}

	/// <summary>Every connector on the chain whose mate is NOT another member of the same chain — i.e. the true open ends.</summary>
	private static List<Connector> GetChainBoundaryConnectors(List<FabricationPart> chain, IList<FabricationPart> parts)
	{
		List<Connector> boundary = new List<Connector>();
		if (chain == null || chain.Count == 0)
		{
			return boundary;
		}

		HashSet<long> chainIds = new HashSet<long>(chain.Select((p) => ((Element)p).Id.Value));
		foreach (FabricationPart part in chain)
		{
			foreach (Connector connector in ListConnectors(part))
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				FabricationPart mate = FindMateAtConnector(part, connector, parts);
				if (mate == null)
				{
					boundary.Add(connector);
					continue;
				}

				if (IsWeldPart(mate) || IsGasketPart(mate))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(mate, part, parts);
					if (beyond == null || !chainIds.Contains(((Element)beyond).Id.Value))
					{
						boundary.Add(connector);
					}
					continue;
				}

				if (!chainIds.Contains(((Element)mate).Id.Value))
				{
					boundary.Add(connector);
				}
			}
		}

		return boundary;
	}

	/// <summary>
	/// BranchToShortSide for the WHOLE weld-connected run chain: pick the true open end connector (among all
	/// boundary connectors across every segment in the chain) closest to the nearest olet takeoff. Anchor
	/// witness is always the host pipe open end at that connector, never shop weld/gasket.
	/// </summary>
	private static bool TryResolveShortSideRunAnchor(
		List<FabricationPart> hostRunChain,
		IList<FabricationPart> parts,
		IList<FabricationPart> oletsOnRun,
		out Element anchorPart,
		out XYZ anchorPoint)
	{
		anchorPart = null;
		anchorPoint = null;
		if (hostRunChain == null || hostRunChain.Count == 0 || oletsOnRun == null || oletsOnRun.Count == 0)
		{
			return false;
		}

		List<Connector> boundary = GetChainBoundaryConnectors(hostRunChain, parts);
		if (boundary.Count < 2)
		{
			return false;
		}

		double bestSpread = -1.0;
		Connector endA = null;
		Connector endB = null;
		for (int i = 0; i < boundary.Count; i++)
		{
			for (int j = i + 1; j < boundary.Count; j++)
			{
				double spread = boundary[i].Origin.DistanceTo(boundary[j].Origin);
				if (spread > bestSpread)
				{
					bestSpread = spread;
					endA = boundary[i];
					endB = boundary[j];
				}
			}
		}

		if (endA == null || endB == null)
		{
			return false;
		}

		XYZ originA = endA.Origin;
		XYZ originB = endB.Origin;
		XYZ nearestTakeoff = null;
		double nearestTakeoffEndScore = double.MaxValue;
		foreach (FabricationPart olet in oletsOnRun)
		{
			if (!TryGetOletTakeoffPointOnChain(olet, hostRunChain, parts, out XYZ takeoff))
			{
				continue;
			}

			double score = Math.Min(originA.DistanceTo(takeoff), originB.DistanceTo(takeoff));
			if (score < nearestTakeoffEndScore)
			{
				nearestTakeoffEndScore = score;
				nearestTakeoff = takeoff;
			}
		}

		if (nearestTakeoff == null)
		{
			return false;
		}

		Connector shortSideConnector = originA.DistanceTo(nearestTakeoff) <= originB.DistanceTo(nearestTakeoff) ? endA : endB;
		return TryResolveAnchorPartAtChainConnector(hostRunChain, shortSideConnector, parts, out anchorPart, out anchorPoint);
	}

	private static bool TryResolveAnchorPartAtChainConnector(
		List<FabricationPart> hostRunChain,
		Connector runConnector,
		IList<FabricationPart> parts,
		out Element anchorPart,
		out XYZ anchorPoint)
	{
		anchorPart = null;
		anchorPoint = null;
		if (hostRunChain == null || runConnector?.Origin == null)
		{
			return false;
		}

		// The short-side connector may belong to ANY segment of the chain, not just the representative
		// (longest) member — find the actual owning part so the witness element is correct.
		FabricationPart owner = hostRunChain.FirstOrDefault((p) => ListConnectors(p).Any((c) => c?.Origin != null && c.Origin.DistanceTo(runConnector.Origin) < 1E-06));
		anchorPart = (Element)(owner ?? hostRunChain.FirstOrDefault());
		anchorPoint = runConnector.Origin;
		return anchorPart != null;
	}

	private static int TryPlaceOletRunStackDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		List<string> failureNotes)
	{
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}

		int placed = 0;
		foreach (SpoolOletRunStackPlan plan in BuildOletRunStackPlans(parts))
		{
			if (plan?.Dimensions == null)
			{
				continue;
			}

			foreach (SpoolOletStackDimensionIntent intent in plan.Dimensions)
			{
				FabricationPart anchorPart = intent.AnchorPart as FabricationPart;
				if (intent.OletPart == null || anchorPart == null || intent.AnchorPointWorld == null)
				{
					failureNotes?.Add("Olet stack skip: missing anchor or olet part.");
					continue;
				}

				XYZ oletPoint = intent.OletTakeoffPointWorld
					?? GetFabricationFittingDimensionAnchor(intent.OletPart, plan.HostRunPipe, anchorPart, parts);
				if (oletPoint == null)
				{
					failureNotes?.Add("Olet stack skip: could not resolve olet takeoff point.");
					continue;
				}

				if (TryPlaceSpoolLinearDimensionSleeveStyle(
					doc,
					view,
					(Element)anchorPart,
					intent.AnchorPointWorld,
					(Element)intent.OletPart,
					oletPoint,
					spoolSettings,
					ref stackIndex,
					out string failureDetail,
					FabricationDimensionRefRole.PipeOpenEnd,
					FabricationDimensionRefRole.OletBranch))
				{
					placed++;
					TryAppendAutoDimPlacementLog(
						view.Name,
						"OletPickUp placed host=" + GetElementIdValue(((Element)plan.HostRunPipe).Id)
						+ " olet=" + GetElementIdValue(((Element)intent.OletPart).Id)
						+ " slot=" + intent.StackSlot);
				}
				else if (!string.IsNullOrWhiteSpace(failureDetail))
				{
					failureNotes?.Add("Olet stack: " + failureDetail);
				}
			}
		}

		return placed;
	}

	/// <summary>
	/// Full host-run overall (E→E or E→C) stacked after olet/outlet pick-ups — same pipe span as if the olet were not there.
	/// Used when the generic horizontal run path did not place an overall on this axis.
	/// </summary>
	private static int TryPlaceOletHostRunOverallDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		List<string> failureNotes)
	{
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}

		int placed = 0;
		HashSet<long> seenChains = new HashSet<long>();
		foreach (SpoolOletRunStackPlan plan in BuildOletRunStackPlans(parts))
		{
			if (plan?.HostRunPipe == null || plan.Dimensions == null || plan.Dimensions.Count == 0)
			{
				continue;
			}

			long chainKey = ((Element)plan.HostRunPipe).Id.Value;
			if (!seenChains.Add(chainKey))
			{
				continue;
			}

			FabricationPart firstOlet = plan.Dimensions[0].OletPart;
			if (firstOlet == null || !TryFindOletHostRunMate(firstOlet, parts, out FabricationPart hostMate))
			{
				continue;
			}

			List<FabricationPart> chain = GetWeldConnectedPipeChain(hostMate, parts);
			if (!TryResolveHostRunChainOverallWitnesses(chain, parts, out Element partA, out XYZ ptA, out Element partB, out XYZ ptB, out FabricationDimensionRefRole? roleA, out FabricationDimensionRefRole? roleB))
			{
				failureNotes?.Add("Olet host run overall skip: could not resolve chain boundary witnesses for host " + GetElementIdValue(((Element)plan.HostRunPipe).Id));
				continue;
			}

			if (ptA.DistanceTo(ptB) < 1.0 / 24.0)
			{
				continue;
			}

			if (TryPlaceSpoolLinearDimensionSleeveStyle(
				doc,
				view,
				partA,
				ptA,
				partB,
				ptB,
				spoolSettings,
				ref stackIndex,
				out string failureDetail,
				roleA,
				roleB))
			{
				placed++;
				TryAppendAutoDimPlacementLog(
					view.Name,
					"OletHostRunOverall host=" + GetElementIdValue(((Element)plan.HostRunPipe).Id));
			}
			else if (!string.IsNullOrWhiteSpace(failureDetail))
			{
				failureNotes?.Add("Olet host run overall: " + failureDetail);
			}
		}

		return placed;
	}

	private static bool TryResolveHostRunChainOverallWitnesses(
		List<FabricationPart> chain,
		IList<FabricationPart> parts,
		out Element witnessA,
		out XYZ pointA,
		out Element witnessB,
		out XYZ pointB,
		out FabricationDimensionRefRole? roleA,
		out FabricationDimensionRefRole? roleB)
	{
		witnessA = null;
		pointA = null;
		witnessB = null;
		pointB = null;
		roleA = null;
		roleB = null;
		if (chain == null || chain.Count == 0 || parts == null)
		{
			return false;
		}

		List<Connector> boundary = GetChainBoundaryConnectors(chain, parts);
		if (boundary.Count < 2)
		{
			return false;
		}

		double bestSpread = -1.0;
		Connector endA = null;
		Connector endB = null;
		for (int i = 0; i < boundary.Count; i++)
		{
			for (int j = i + 1; j < boundary.Count; j++)
			{
				double spread = boundary[i].Origin.DistanceTo(boundary[j].Origin);
				if (spread > bestSpread)
				{
					bestSpread = spread;
					endA = boundary[i];
					endB = boundary[j];
				}
			}
		}

		if (endA == null || endB == null)
		{
			return false;
		}

		if (!TryResolveChainBoundaryWitness(chain, endA, parts, out witnessA, out pointA, out roleA)
			|| !TryResolveChainBoundaryWitness(chain, endB, parts, out witnessB, out pointB, out roleB))
		{
			return false;
		}

		return witnessA != null && witnessB != null && pointA != null && pointB != null;
	}

	private static bool TryResolveChainBoundaryWitness(
		List<FabricationPart> chain,
		Connector boundaryConnector,
		IList<FabricationPart> parts,
		out Element witnessPart,
		out XYZ witnessPoint,
		out FabricationDimensionRefRole? role)
	{
		witnessPart = null;
		witnessPoint = null;
		role = null;
		if (!TryResolveAnchorPartAtChainConnector(chain, boundaryConnector, parts, out Element pipeElement, out XYZ pipePoint)
			|| pipeElement == null || pipePoint == null)
		{
			return false;
		}

		FabricationPart pipePart = pipeElement as FabricationPart;
		if (pipePart == null)
		{
			return false;
		}

		FabricationPart mate = FindMateAtConnector(pipePart, boundaryConnector, parts);
		if (mate != null && (IsGasketPart(mate) || IsWeldPart(mate)))
		{
			FabricationPart beyond = FindFarSideMateThroughJoint(mate, pipePart, parts);
			if (beyond != null && IsFittingLikeForSpoolDim(beyond) && !IsOletPart(beyond) && !IsValvePart(beyond))
			{
				mate = beyond;
			}
		}

		if (mate != null && IsFittingLikeForSpoolDim(mate) && !IsOletPart(mate) && !IsValvePart(mate))
		{
			XYZ fitPt = GetFabricationFittingDimensionAnchor(mate, pipePart, null, parts);
			if (fitPt != null)
			{
				witnessPart = (Element)mate;
				witnessPoint = fitPt;
				role = FabricationDimensionRefRole.RunStartFitting;
				return true;
			}
		}

		if (FindMateAtConnector(pipePart, boundaryConnector, parts) == null)
		{
			witnessPart = pipeElement;
			witnessPoint = pipePoint;
			role = FabricationDimensionRefRole.PipeOpenEnd;
			return true;
		}

		if (TryResolvePipeEndFittingAnchor(parts as List<FabricationPart> ?? parts.ToList(), pipePart, pipePoint, null, out FabricationPart farFit, out XYZ farPt))
		{
			witnessPart = (Element)farFit;
			witnessPoint = farPt;
			role = FabricationDimensionRefRole.RunStartFitting;
			return true;
		}

		witnessPart = pipeElement;
		witnessPoint = pipePoint;
		role = FabricationDimensionRefRole.PipeOpenEnd;
		return true;
	}

	private static int TryApplyOletStackDimensionPlans(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		List<string> failureNotes)
	{
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}

		int planned = 0;
		foreach (SpoolOletRunStackPlan plan in BuildOletRunStackPlans(parts))
		{
			if (plan?.Dimensions == null)
			{
				continue;
			}

			foreach (SpoolOletStackDimensionIntent intent in plan.Dimensions)
			{
				FabricationPart anchorPart = intent.AnchorPart as FabricationPart;
				if (intent.OletPart == null || anchorPart == null)
				{
					failureNotes?.Add("Olet stack skip: missing anchor or olet part.");
					continue;
				}

				if (!TryResolveFabricationOriginReference(doc, view, intent.OletPart, out _, out string oletDiag))
				{
					failureNotes?.Add("Olet stack skip: " + oletDiag);
					continue;
				}

				if (!TryResolveFabricationOriginReference(doc, view, anchorPart, out _, out string anchorDiag))
				{
					failureNotes?.Add("Olet stack skip: " + anchorDiag);
					continue;
				}

				planned++;
				TryAppendAutoDimPlacementLog(view.Name,
					"OletStack plan host=" + ((Element)plan.HostRunPipe).Id.Value
					+ " olet=" + ((Element)intent.OletPart).Id.Value
					+ " slot=" + intent.StackSlot
					+ " dist=" + intent.DistanceFromAnchorFeet.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
					+ " stacked=" + plan.RequiresStacking);
			}
		}

		return planned;
	}
}
