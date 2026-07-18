using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public partial class CreateSpoolSheetsHandler
{
	/// <summary>
	/// Auto-dimension placement for fabrication spools. Gated by per-view Auto Dim checkboxes only —
	/// never disable via a compile-time flag unless the user explicitly requests it.
	/// Fitting reference, orientation, and stacking rules are identical for Front, Back, Left, Right, and Top.
	/// Never place DetailLine/DetailCurve helpers — model Linear dimensions only (Document.Create.NewDimension).
	/// </summary>
	private static int TryApplySpoolAutoDimensionRules(
		Document doc,
		View view,
		AssemblyInstance assembly,
		List<FabricationPart> parts,
		List<FabricationPart> allParts,
		XYZ unitAxis,
		SpoolingManagerSettings spoolSettings,
		List<string> failureNotes)
	{
		if (doc == null || view == null)
		{
			return 0;
		}

		List<FabricationPart> dimPool = (allParts != null && allParts.Count > 0) ? allParts : parts;
		if (dimPool == null || dimPool.Count == 0)
		{
			return 0;
		}

		TryReportSpoolDimensionPatternGaps(view, dimPool, unitAxis, failureNotes);

		// Each physically distinct run/leg gets its OWN stack counter starting at slot 0. Only dimensions
		// that share the exact same run and pull the SAME direction (e.g. horizontal olet pick-ups + horizontal
		// run overall, both pulled up off the same horizontal pipe) legitimately nest outward from each other
		// and share a counter. A vertical drop leg and a branch takeoff are two different runs — even though
		// both happen to pull left/right — so they must never share a counter, or the second one inherits the
		// first's offset and lands beyond the taught 3/8" gap instead of hugging its own pipe.
		int horizontalStackIndex = 0;
		int verticalDropStackIndex = 0;
		int fittingFlangeStubStackIndex = 0;
		int branchTakeoffStackIndex = 0;
		string diagnostic;
		int placed = 0;

		placed += TryPlaceOletRunStackDimensions(doc, view, dimPool, spoolSettings, ref horizontalStackIndex, failureNotes);

		placed += TryCreateSpoolFittingToFlangeStubDimensions(doc, view, dimPool, unitAxis, spoolSettings, ref fittingFlangeStubStackIndex, failureNotes);

		XYZ runAxisForOverall = unitAxis;
		bool horizontalPlaced = TryCreateSpoolAssemblyHorizontalRunDimensions(doc, view, dimPool, unitAxis, spoolSettings, ref horizontalStackIndex, out diagnostic);
		if (horizontalPlaced)
		{
			placed++;
		}
		else
		{
			if (!string.IsNullOrWhiteSpace(diagnostic))
			{
				failureNotes?.Add(diagnostic);
			}
			if (TryGetAlternateRunAxisInViewPlane(view, unitAxis, out XYZ alternateAxis))
			{
				string altDiagnostic = null;
				if (TryCreateSpoolAssemblyHorizontalRunDimensions(doc, view, dimPool, alternateAxis, spoolSettings, ref horizontalStackIndex, out altDiagnostic))
				{
					placed++;
					horizontalPlaced = true;
					runAxisForOverall = alternateAxis;
				}
				else if (!string.IsNullOrWhiteSpace(altDiagnostic))
				{
					failureNotes?.Add("Auto-dimension (alternate run axis): " + altDiagnostic);
				}
			}
		}

		if (!horizontalPlaced)
		{
			placed += TryPlaceOletHostRunOverallDimensions(doc, view, dimPool, spoolSettings, ref horizontalStackIndex, failureNotes);
		}

		// One E→F overall per spool/view axis — never on both primary and alternate in-plane axes (duplicates).
		placed += TryCreateSpoolOpenEndToFlangeOverallDimensions(doc, view, dimPool, runAxisForOverall, spoolSettings, ref horizontalStackIndex, failureNotes);

		int verticalDropLegPlaced = TryCreateSpoolVerticalDropLegDimensions(doc, view, dimPool, unitAxis, spoolSettings, ref verticalDropStackIndex, out diagnostic);
		placed += verticalDropLegPlaced;
		if (verticalDropLegPlaced == 0 && !string.IsNullOrWhiteSpace(diagnostic))
		{
			failureNotes?.Add(diagnostic);
		}

		int fittingCcPlaced = TryCreateSpoolVerticalDropToElbowDimension(doc, view, dimPool, unitAxis, spoolSettings, ref verticalDropStackIndex, out diagnostic);
		placed += fittingCcPlaced;
		if (fittingCcPlaced == 0 && !string.IsNullOrWhiteSpace(diagnostic))
		{
			failureNotes?.Add(diagnostic);
		}

		placed += TryCreateSpoolOletBranchTakeoffDimensions(doc, view, dimPool, spoolSettings, ref branchTakeoffStackIndex, failureNotes);

		return placed;
	}

	private static bool TryGetAlternateRunAxisInViewPlane(View view, XYZ primaryUnitAxis, out XYZ alternateAxis)
	{
		alternateAxis = null;
		if (view == null)
		{
			return false;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			return false;
		}
		vn = vn.Normalize();
		XYZ upInPlane = ProjectVectorToViewPlane(view.UpDirection, vn);
		if (upInPlane == null || upInPlane.GetLength() < 1E-09)
		{
			return false;
		}
		upInPlane = upInPlane.Normalize();
		if (primaryUnitAxis != null)
		{
			XYZ primaryInPlane = ProjectVectorToViewPlane(primaryUnitAxis, vn);
			if (primaryInPlane != null && primaryInPlane.GetLength() > 1E-09
				&& Math.Abs(primaryInPlane.Normalize().DotProduct(upInPlane)) > 0.85)
			{
				return false;
			}
		}
		alternateAxis = upInPlane;
		return true;
	}

	private static int TryCreateSpoolOletBranchTakeoffDimensions(
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

		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			return 0;
		}

		vn = vn.Normalize();
		int placed = 0;
		HashSet<string> seenBranchCc = new HashSet<string>(StringComparer.Ordinal);
		foreach (FabricationPart branchPipe in parts.Where((p) => IsOletBranchTakeoffPipe(p, parts)))
		{
			if (!TryFindOletMateForBranchTakeoffPipe(branchPipe, parts, out FabricationPart olet))
			{
				continue;
			}

			if (!TryGetFabricationRunAxisInViewPlane(view, branchPipe, out XYZ branchAxis))
			{
				continue;
			}

			// Outlet/olet branch vertical run: C→C from host main-pipe centerline to far-end elbow center.
			if (TryResolveBranchFarEndElbow(branchPipe, olet, parts, out FabricationPart elbow, out XYZ elbowCenter)
				&& TryResolveHostRunPipeCenterlineAtOlet(olet, parts, out FabricationPart hostRun, out XYZ hostCenter)
				&& hostCenter.DistanceTo(elbowCenter) >= SegmentLengthEpsilonFeet)
			{
				long idLo = Math.Min(((Element)hostRun).Id.Value, ((Element)elbow).Id.Value);
				long idHi = Math.Max(((Element)hostRun).Id.Value, ((Element)elbow).Id.Value);
				string ccKey = idLo + "->" + idHi;
				if (seenBranchCc.Add(ccKey))
				{
					if (TryPlaceSpoolLinearDimensionSleeveStyle(
						doc,
						view,
						(Element)hostRun,
						hostCenter,
						(Element)elbow,
						elbowCenter,
						spoolSettings,
						ref stackIndex,
						out string ccFailure,
						FabricationDimensionRefRole.PipeCenterline,
						FabricationDimensionRefRole.RunStartFitting,
						-1))
					{
						placed++;
						TryAppendAutoDimPlacementLog(
							view.Name,
							"OletBranchVerticalCC host=" + GetElementIdValue(((Element)hostRun).Id)
							+ " elbow=" + GetElementIdValue(((Element)elbow).Id)
							+ " branch=" + GetElementIdValue(((Element)branchPipe).Id));
						continue;
					}

					if (!string.IsNullOrWhiteSpace(ccFailure))
					{
						failureNotes?.Add("Olet branch C-C: " + ccFailure);
					}
				}
			}

			XYZ oletPoint = GetFabricationFittingDimensionAnchor(olet, branchPipe, null, parts);
			if (oletPoint == null)
			{
				failureNotes?.Add("Branch takeoff skip: could not resolve olet center for " + ((Element)olet).Id.Value);
				continue;
			}

			if (!TryGetBranchPipeOpenEndAwayFromOlet(branchPipe, olet, parts, branchAxis, vn, out XYZ branchEndPoint))
			{
				failureNotes?.Add("Branch takeoff skip: could not resolve branch pipe open end for " + ((Element)branchPipe).Id.Value);
				continue;
			}

			if (oletPoint.DistanceTo(branchEndPoint) < SegmentLengthEpsilonFeet)
			{
				continue;
			}

			if (TryPlaceSpoolLinearDimensionSleeveStyle(
				doc,
				view,
				(Element)olet,
				oletPoint,
				(Element)branchPipe,
				branchEndPoint,
				spoolSettings,
				ref stackIndex,
				out string failureDetail,
				FabricationDimensionRefRole.OletBranch,
				FabricationDimensionRefRole.PipeOpenEnd,
				-1))
			{
				placed++;
				TryAppendAutoDimPlacementLog(
					view.Name,
					"OletBranchTakeoff olet=" + GetElementIdValue(((Element)olet).Id)
					+ " branch=" + GetElementIdValue(((Element)branchPipe).Id));
			}
			else if (!string.IsNullOrWhiteSpace(failureDetail))
			{
				failureNotes?.Add("Branch takeoff: " + failureDetail);
			}
		}

		return placed;
	}

	/// <summary>Host main-run pipe centerline (C) at the side olet/outlet takeoff — not olet center or pipe surface.</summary>
	private static bool TryResolveHostRunPipeCenterlineAtOlet(
		FabricationPart olet,
		IList<FabricationPart> parts,
		out FabricationPart hostRun,
		out XYZ centerPoint)
	{
		hostRun = null;
		centerPoint = null;
		if (olet == null || parts == null || !TryFindOletHostRunMate(olet, parts, out hostRun))
		{
			return false;
		}

		List<FabricationPart> chain = GetWeldConnectedPipeChain(hostRun, parts);
		XYZ seed = null;
		if (!TryGetOletTakeoffPointOnChain(olet, chain, parts, out seed))
		{
			seed = GetFabricationFittingDimensionAnchor(olet, hostRun, null, parts);
		}

		if (seed == null)
		{
			return false;
		}

		FabricationPart bestSegment = hostRun;
		double bestDist = double.MaxValue;
		foreach (FabricationPart segment in chain)
		{
			if (segment == null || !TryGetPipeCenterlinePointNearest(segment, seed, out XYZ onCenterline))
			{
				continue;
			}

			double dist = onCenterline.DistanceTo(seed);
			if (dist < bestDist)
			{
				bestDist = dist;
				bestSegment = segment;
				centerPoint = onCenterline;
			}
		}

		if (centerPoint == null && !TryGetPipeCenterlinePointNearest(hostRun, seed, out centerPoint))
		{
			return false;
		}

		hostRun = bestSegment ?? hostRun;
		return centerPoint != null;
	}

	private static bool TryGetPipeCenterlinePointNearest(FabricationPart pipe, XYZ nearPoint, out XYZ onCenterline)
	{
		onCenterline = null;
		if (pipe == null || nearPoint == null)
		{
			return false;
		}

		Location obj = ((Element)pipe).Location;
		LocationCurve locationCurve = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
		if (locationCurve?.Curve == null)
		{
			return false;
		}

		onCenterline = ClosestPointOnBoundedModelCurveWorld(locationCurve.Curve, nearPoint);
		return onCenterline != null;
	}

	/// <summary>Elbow (C) at the branch end away from the olet/outlet — vertical branch run termination.</summary>
	private static bool TryResolveBranchFarEndElbow(
		FabricationPart branchPipe,
		FabricationPart olet,
		IList<FabricationPart> parts,
		out FabricationPart elbow,
		out XYZ elbowCenter)
	{
		elbow = null;
		elbowCenter = null;
		if (branchPipe == null || parts == null)
		{
			return false;
		}

		XYZ oletAnchor = GetFabricationFittingDimensionAnchor(olet, branchPipe, null, parts) ?? TryGetFabricationPartOrigin(olet);
		double bestDist = double.MinValue;
		foreach (Connector connector in ListConnectors(branchPipe))
		{
			if (connector?.Origin == null)
			{
				continue;
			}

			FabricationPart mate = FindMateAtConnector(branchPipe, connector, parts);
			if (mate != null && olet != null && ((Element)mate).Id == ((Element)olet).Id)
			{
				continue;
			}

			if (mate != null && (IsGasketPart(mate) || IsWeldPart(mate)))
			{
				FabricationPart beyond = FindFarSideMateThroughJoint(mate, branchPipe, parts);
				if (beyond != null)
				{
					mate = beyond;
				}
			}

			if (mate == null || !FabricationPartClassification.IsElbowPart(mate, ((Element)mate).Document))
			{
				continue;
			}

			double dist = oletAnchor != null ? connector.Origin.DistanceTo(oletAnchor) : 0.0;
			if (dist > bestDist)
			{
				bestDist = dist;
				elbow = mate;
			}
		}

		if (elbow == null)
		{
			return false;
		}

		elbowCenter = GetFabricationFittingDimensionAnchor(elbow, branchPipe, null, parts);
		return elbowCenter != null;
	}

	private static bool TryFindOletMateForBranchTakeoffPipe(FabricationPart branchPipe, IList<FabricationPart> parts, out FabricationPart olet)
	{
		olet = null;
		if (branchPipe == null)
		{
			return false;
		}

		foreach (Connector connector in ListConnectors(branchPipe))
		{
			FabricationPart mate = FindMateAtConnector(branchPipe, connector, parts);
			if (mate != null && IsOletPart(mate))
			{
				olet = mate;
				return true;
			}
		}

		return false;
	}

	private static bool TryGetBranchPipeOpenEndAwayFromOlet(
		FabricationPart branchPipe,
		FabricationPart olet,
		IList<FabricationPart> parts,
		XYZ branchAxis,
		XYZ viewNormal,
		out XYZ openEndPoint)
	{
		openEndPoint = null;
		if (branchPipe == null || branchAxis == null || viewNormal == null)
		{
			return false;
		}

		XYZ oletOrigin = TryGetFabricationPartOrigin(olet) ?? oletPointFallback(olet);
		double bestScore = double.MinValue;
		foreach (Connector connector in ListConnectors(branchPipe))
		{
			if (connector?.Origin == null)
			{
				continue;
			}

			FabricationPart mate = FindMateAtConnector(branchPipe, connector, parts);
			if (mate != null && olet != null && ((Element)mate).Id == ((Element)olet).Id)
			{
				continue;
			}

			double distFromOlet = oletOrigin != null ? connector.Origin.DistanceTo(oletOrigin) : 0.0;
			double openEndScore = distFromOlet;
			if (mate == null)
			{
				openEndScore += 10.0;
			}

			if (openEndScore > bestScore)
			{
				bestScore = openEndScore;
				openEndPoint = connector.Origin;
			}
		}

		return openEndPoint != null;
	}

	private static XYZ oletPointFallback(FabricationPart olet)
	{
		foreach (Connector connector in ListConnectors(olet))
		{
			if (connector?.Origin != null)
			{
				return connector.Origin;
			}
		}

		return null;
	}

	private static bool IsValvePart(FabricationPart part)
	{
		return FabricationPartClassification.IsValvePart(part, part?.Document);
	}

	private static List<FabricationPart> BuildDocumentFabricationMatePool(Document doc)
	{
		if (doc == null)
		{
			return new List<FabricationPart>();
		}
		return (from FabricationPart p in new FilteredElementCollector(doc).OfClass(typeof(FabricationPart))
			where p != null && !IsGasketPart(p) && !IsWeldPart(p)
			select p).ToList();
	}

	private static bool IsCollinearWithPrimaryRun(FabricationPart pipe, IList<FabricationPart> parts, XYZ unitAxis, XYZ vn)
	{
		if (!IsPipeRunPart(pipe) || unitAxis == null || vn == null || !TryGetFabricationLineDirection(pipe, out XYZ dir))
		{
			return false;
		}
		dir = dir.Normalize();
		XYZ axis = unitAxis.Normalize();
		dir -= vn * dir.DotProduct(vn);
		axis -= vn * axis.DotProduct(vn);
		if (dir.GetLength() < 1E-09 || axis.GetLength() < 1E-09)
		{
			return false;
		}
		return Math.Abs(dir.Normalize().DotProduct(axis.Normalize())) > 0.85;
	}

	private static bool IsVerticalDropLeg(FabricationPart pipe, IList<FabricationPart> parts, XYZ unitAxis, XYZ vn)
	{
		if (!IsPipeRunPart(pipe) || IsOletBranchTakeoffPipe(pipe, parts) || GetFabricationStraightLineLength(pipe) < 2.0)
		{
			return false;
		}
		// Collinear with the active run axis is always the main run — never a branch drop (vertical-only spools).
		if (unitAxis != null && vn != null && IsCollinearWithPrimaryRun(pipe, parts, unitAxis, vn))
		{
			return false;
		}
		if (!TryGetFabricationLineDirection(pipe, out XYZ dir))
		{
			return false;
		}
		dir = dir.Normalize();
		XYZ up = vn?.Normalize();
		if (up != null && Math.Abs(dir.DotProduct(up)) > 0.85)
		{
			return true;
		}
		if (unitAxis != null)
		{
			XYZ axis = unitAxis.Normalize();
			axis -= vn * axis.DotProduct(vn);
			dir -= vn * dir.DotProduct(vn);
			if (axis.GetLength() > 1E-09 && dir.GetLength() > 1E-09)
			{
				return Math.Abs(dir.Normalize().DotProduct(axis.Normalize())) < 0.25;
			}
		}
		return Math.Abs(dir.Z) > 0.85;
	}

	private static bool IsOletBranchTakeoffPipe(FabricationPart pipe, IList<FabricationPart> parts)
	{
		if (!IsPipeRunPart(pipe))
		{
			return false;
		}
		bool hasOletMate = false;
		foreach (Connector connector in ListConnectors(pipe))
		{
			FabricationPart mate = FindMateAtConnector(pipe, connector, parts);
			if (mate != null && IsOletPart(mate))
			{
				hasOletMate = true;
				break;
			}
		}
		if (!hasOletMate)
		{
			return false;
		}
		return !IsOletHostRunPipe(pipe, parts);
	}

	private static bool IsOletHostRunPipe(FabricationPart pipe, IList<FabricationPart> parts)
	{
		if (!TryGetFabricationLineDirection(pipe, out XYZ pipeDir))
		{
			return GetFabricationStraightLineLength(pipe) > 4.0;
		}
		pipeDir = pipeDir.Normalize();
		bool hasSideOletMate = false;
		int collinearPipeNeighbors = 0;
		int runEndConnections = 0;
		foreach (Connector connector in ListConnectors(pipe))
		{
			FabricationPart mate = FindMateAtConnector(pipe, connector, parts);
			if (mate != null && IsOletPart(mate))
			{
				hasSideOletMate = true;
				continue;
			}
			if (mate == null)
			{
				continue;
			}
			if (IsPipeRunPart(mate) && TryGetFabricationLineDirection(mate, out XYZ mateDir) && Math.Abs(pipeDir.DotProduct(mateDir.Normalize())) > 0.85)
			{
				collinearPipeNeighbors++;
				continue;
			}
			if (IsFittingLikeForSpoolDim(mate) || FabricationPartClassification.IsFlangePart(mate, ((Element)mate).Document))
			{
				runEndConnections++;
			}
		}
		// Side olet/outlet/anvilet on a long collinear segment — host run even when both ends are open pipe (E)
		// and no run-end fitting is counted (olet mates are skipped above).
		if (hasSideOletMate && GetFabricationStraightLineLength(pipe) >= 3.0)
		{
			return true;
		}
		if (collinearPipeNeighbors >= 2)
		{
			return true;
		}
		if (collinearPipeNeighbors >= 1 && runEndConnections >= 1)
		{
			return true;
		}
		if (GetFabricationStraightLineLength(pipe) > 4.0 && runEndConnections >= 2)
		{
			return true;
		}
		return GetFabricationStraightLineLength(pipe) > 4.0 && runEndConnections >= 1;
	}

	private static bool IsFittingToFlangePassThroughPart(FabricationPart part, IList<FabricationPart> partsPool = null)
	{
		if (part == null)
		{
			return false;
		}
		if (IsGasketPart(part) || IsWeldPart(part) || FabricationPartClassification.IsBoltKitPart(part))
		{
			return true;
		}
		if (IsFittingLikeForSpoolDim(part))
		{
			Document doc = ((Element)part).Document;
			if (FabricationPartClassification.IsOletPart(part) ||
				FabricationPartClassification.IsElbowPart(part, doc) ||
				FabricationPartClassification.IsTeePart(part, doc) ||
				FabricationPartClassification.IsReducerPart(part, doc) ||
				FabricationPartClassification.IsFlangePart(part, doc))
			{
				return false;
			}
			double spread = GetFabricationConnectorSpread(part);
			return spread <= 2.0;
		}
		if (IsPipeRunPart(part))
		{
			if (partsPool != null && IsOletBranchTakeoffPipe(part, partsPool))
			{
				return false;
			}
			Location obj = ((Element)part).Location;
			if (obj is LocationCurve)
			{
				return GetFabricationStraightLineLength(part) <= 2.0;
			}
			return GetFabricationConnectorSpread(part) <= 2.0;
		}
		return false;
	}

	private static double GetFabricationConnectorSpread(FabricationPart part)
	{
		List<XYZ> origins = new List<XYZ>();
		foreach (Connector connector in ListConnectors(part))
		{
			if (connector?.Origin != null)
			{
				origins.Add(connector.Origin);
			}
		}
		if (origins.Count < 2)
		{
			return 0.0;
		}
		double max = 0.0;
		for (int i = 0; i < origins.Count; i++)
		{
			for (int j = i + 1; j < origins.Count; j++)
			{
				max = Math.Max(max, origins[i].DistanceTo(origins[j]));
			}
		}
		return max;
	}
}
