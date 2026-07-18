using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Run overall (E–C) and vertical drop dimensions for L-shaped spools — same rules on every view.
/// Uses collinear horizontal run pipe, not the vertical drop leg, for run overall.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	public enum SpoolRunDimensionKind
	{
		RunOverallHorizontal,
		VerticalDrop
	}

	public sealed class SpoolRunDimensionIntent
	{
		public SpoolRunDimensionKind Kind { get; set; }
		public Element WitnessAPart { get; set; }
		public Element WitnessBPart { get; set; }
		public XYZ WitnessAPointWorld { get; set; }
		public XYZ WitnessBPointWorld { get; set; }
		public SpoolRunSegmentOrientation Orientation { get; set; }
		public int StackSlot { get; set; }
	}

	public static IReadOnlyList<SpoolRunDimensionIntent> BuildSpoolRunDimensionIntents(
		View view,
		IList<FabricationPart> parts,
		XYZ unitAxis)
	{
		List<SpoolRunDimensionIntent> intents = new List<SpoolRunDimensionIntent>();
		if (view == null || parts == null || parts.Count == 0 || unitAxis == null)
		{
			return intents;
		}

		int oletStackDepth = 0;
		foreach (SpoolOletRunStackPlan plan in BuildOletRunStackPlans(parts))
		{
			if (plan?.Dimensions == null)
			{
				continue;
			}

			oletStackDepth = Math.Max(oletStackDepth, plan.Dimensions.Count);
		}

		int overallStackSlot = Math.Max(oletStackDepth, 1);

		if (TryBuildRunOverallHorizontalIntent(view, parts, unitAxis, overallStackSlot, out SpoolRunDimensionIntent runOverall))
		{
			intents.Add(runOverall);
		}

		if (TryBuildVerticalDropIntent(view, parts, unitAxis, out SpoolRunDimensionIntent verticalDrop))
		{
			intents.Add(verticalDrop);
		}

		return intents;
	}

	private static bool TryBuildRunOverallHorizontalIntent(
		View view,
		IList<FabricationPart> parts,
		XYZ unitAxis,
		int stackSlot,
		out SpoolRunDimensionIntent intent)
	{
		intent = null;
		List<FabricationPart> partList = parts as List<FabricationPart> ?? parts.ToList();
		XYZ vn = view?.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			return false;
		}

		vn = vn.Normalize();
		XYZ runAxis = unitAxis;
		FabricationPart pipePart = GetDominantCollinearRunPipePart(partList, runAxis, vn);
		if (pipePart == null)
		{
			return false;
		}

		if (!TryGetPrimaryPipeEndAnchor(partList, pipePart, runAxis, vn, out FabricationPart openEndOwner, out XYZ openEndPoint))
		{
			return false;
		}

		double openEndScalar = DotInPlane(openEndPoint, runAxis, vn);
		FabricationPart startOwner = null;
		XYZ startPoint = null;
		if (!TryResolvePipeRunStartFittingAnchor(partList, pipePart, openEndOwner, runAxis, vn, out startOwner, out startPoint)
			&& !TryResolveLeftRunExtentAnchor(view, partList, pipePart, runAxis, vn, openEndScalar, out startOwner, out startPoint)
			&& !TryGetMinimumPipeEndAnchor(partList, pipePart, runAxis, vn, out startOwner, out startPoint))
		{
			return false;
		}

		double startScalar = DotInPlane(startPoint, runAxis, vn);
		double endScalar = DotInPlane(openEndPoint, runAxis, vn);
		if (endScalar < startScalar)
		{
			runAxis = runAxis.Negate();
			if (!TryGetPrimaryPipeEndAnchor(partList, pipePart, runAxis, vn, out openEndOwner, out openEndPoint)
				|| !TryResolvePipeRunStartFittingAnchor(partList, pipePart, openEndOwner, runAxis, vn, out startOwner, out startPoint))
			{
				if (!TryResolveLeftRunExtentAnchor(view, partList, pipePart, runAxis, vn, DotInPlane(openEndPoint, runAxis, vn), out startOwner, out startPoint)
					&& !TryGetMinimumPipeEndAnchor(partList, pipePart, runAxis, vn, out startOwner, out startPoint))
				{
					return false;
				}
			}
		}

		if (startPoint.DistanceTo(openEndPoint) < SegmentLengthEpsilonFeet)
		{
			return false;
		}

		intent = new SpoolRunDimensionIntent
		{
			Kind = SpoolRunDimensionKind.RunOverallHorizontal,
			WitnessAPart = (Element)startOwner,
			WitnessBPart = (Element)openEndOwner,
			WitnessAPointWorld = startPoint,
			WitnessBPointWorld = openEndPoint,
			Orientation = ClassifySegmentOrientation(startPoint, openEndPoint),
			StackSlot = stackSlot
		};
		return true;
	}

	private static bool TryBuildVerticalDropIntent(
		View view,
		IList<FabricationPart> parts,
		XYZ primaryUnitAxis,
		out SpoolRunDimensionIntent intent)
	{
		intent = null;
		List<FabricationPart> partList = parts as List<FabricationPart> ?? parts.ToList();
		XYZ vn = view?.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09 || primaryUnitAxis == null)
		{
			return false;
		}

		vn = vn.Normalize();
		XYZ primaryAxisInPlane = ProjectVectorToViewPlane(primaryUnitAxis, vn);
		if (primaryAxisInPlane == null || primaryAxisInPlane.GetLength() < 1E-09)
		{
			return false;
		}

		primaryAxisInPlane = primaryAxisInPlane.Normalize();
		XYZ upInPlane = ProjectVectorToViewPlane(view.UpDirection, vn);
		if (upInPlane == null || upInPlane.GetLength() < 1E-09)
		{
			return false;
		}

		upInPlane = upInPlane.Normalize();
		foreach (FabricationPart dropPipe in partList
			.Where((p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, partList))
			.OrderByDescending(GetFabricationStraightLineLength))
		{
			if (!TryGetFabricationRunAxisInViewPlane(view, dropPipe, out XYZ dropAxis))
			{
				continue;
			}

			if (Math.Abs(dropAxis.DotProduct(primaryAxisInPlane)) > 0.9)
			{
				continue;
			}

			double minScalar = double.MaxValue;
			double maxScalar = double.MinValue;
			XYZ bottomPoint = null;
			FabricationPart elbow = null;
			XYZ elbowPoint = null;
			foreach (Connector connector in ListConnectors(dropPipe))
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				double scalar = DotInPlane(connector.Origin, dropAxis, vn);
				if (scalar < minScalar)
				{
					minScalar = scalar;
					bottomPoint = connector.Origin;
				}

				FabricationPart mate = FindMateAtConnector(dropPipe, connector, partList);
				if (mate == null || IsOletPart(mate) || IsGasketPart(mate) || IsWeldPart(mate) || !IsFittingLikeForSpoolDim(mate))
				{
					continue;
				}

				if (scalar > maxScalar)
				{
					maxScalar = scalar;
					elbow = mate;
					elbowPoint = GetFabricationFittingDimensionAnchor(mate, dropPipe, null, partList);
				}
			}

			if (elbow == null || bottomPoint == null || elbowPoint == null)
			{
				continue;
			}

			Element topPart = (Element)elbow;
			XYZ topPoint = elbowPoint;
			Element bottomPart = (Element)dropPipe;
			XYZ bottomAnchorPoint = bottomPoint;
			if (DotInPlane(bottomAnchorPoint, upInPlane, vn) > DotInPlane(topPoint, upInPlane, vn))
			{
				topPart = bottomPart;
				bottomPart = (Element)elbow;
				topPoint = bottomAnchorPoint;
				bottomAnchorPoint = elbowPoint;
			}

			if (bottomAnchorPoint.DistanceTo(topPoint) < SegmentLengthEpsilonFeet)
			{
				continue;
			}

			intent = new SpoolRunDimensionIntent
			{
				Kind = SpoolRunDimensionKind.VerticalDrop,
				WitnessAPart = topPart,
				WitnessBPart = bottomPart,
				WitnessAPointWorld = topPoint,
				WitnessBPointWorld = bottomAnchorPoint,
				Orientation = ClassifySegmentOrientation(topPoint, bottomAnchorPoint),
				StackSlot = 0
			};
			return true;
		}

		return false;
	}

	/// <summary>Places E→C on every offset vertical drop leg (L-spool left leg, etc.). Uses its own stack counter.</summary>
	internal static int TryCreateSpoolVerticalDropLegDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		XYZ primaryUnitAxis,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		out string diagnostic)
	{
		diagnostic = null;
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}

		int placed = 0;
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		List<string> failures = new List<string>();
		foreach (SpoolRunDimensionIntent intent in BuildAllVerticalDropIntents(view, parts, primaryUnitAxis))
		{
			if (intent?.WitnessAPart == null || intent.WitnessBPart == null
				|| intent.WitnessAPointWorld == null || intent.WitnessBPointWorld == null)
			{
				continue;
			}

			long idLo = Math.Min(intent.WitnessAPart.Id.Value, intent.WitnessBPart.Id.Value);
			long idHi = Math.Max(intent.WitnessAPart.Id.Value, intent.WitnessBPart.Id.Value);
			string key = idLo + "->" + idHi;
			if (!seen.Add(key))
			{
				continue;
			}

			ResolveHorizontalRunSegmentRefRoles(intent.WitnessAPart, intent.WitnessBPart, out FabricationDimensionRefRole? roleA, out FabricationDimensionRefRole? roleB);
			int offsetSign = intent.Orientation == SpoolRunSegmentOrientation.Vertical ? -1 : 1;
			if (TryPlaceSpoolLinearDimensionSleeveStyle(
				doc,
				view,
				intent.WitnessAPart,
				intent.WitnessAPointWorld,
				intent.WitnessBPart,
				intent.WitnessBPointWorld,
				spoolSettings,
				ref stackIndex,
				out string failureDetail,
				roleA,
				roleB,
				offsetSign))
			{
				placed++;
				TryAppendAutoDimPlacementLog(
					view.Name,
					"VerticalDropLeg slot=" + intent.StackSlot
					+ " a=" + GetElementIdValue(intent.WitnessAPart.Id)
					+ " b=" + GetElementIdValue(intent.WitnessBPart.Id));
			}
			else if (!string.IsNullOrWhiteSpace(failureDetail))
			{
				failures.Add(failureDetail);
			}
		}

		if (placed == 0 && failures.Count > 0)
		{
			diagnostic = "Auto-dimension (vertical drop leg): " + failures[0];
		}

		return placed;
	}

	private static IEnumerable<SpoolRunDimensionIntent> BuildAllVerticalDropIntents(
		View view,
		IList<FabricationPart> parts,
		XYZ primaryUnitAxis)
	{
		List<FabricationPart> partList = parts as List<FabricationPart> ?? parts?.ToList();
		if (view == null || partList == null || partList.Count == 0 || primaryUnitAxis == null)
		{
			yield break;
		}

		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			yield break;
		}

		vn = vn.Normalize();
		XYZ primaryAxisInPlane = ProjectVectorToViewPlane(primaryUnitAxis, vn);
		if (primaryAxisInPlane == null || primaryAxisInPlane.GetLength() < 1E-09)
		{
			yield break;
		}

		primaryAxisInPlane = primaryAxisInPlane.Normalize();
		XYZ upInPlane = ProjectVectorToViewPlane(view.UpDirection, vn);
		if (upInPlane == null || upInPlane.GetLength() < 1E-09)
		{
			yield break;
		}

		upInPlane = upInPlane.Normalize();
		HashSet<long> seenPipes = new HashSet<long>();
		foreach (FabricationPart dropPipe in partList
			.Where((p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, partList))
			.OrderByDescending(GetFabricationStraightLineLength))
		{
			if (dropPipe == null || !seenPipes.Add(((Element)dropPipe).Id.Value))
			{
				continue;
			}

			if (!TryGetFabricationRunAxisInViewPlane(view, dropPipe, out XYZ dropAxis))
			{
				continue;
			}

			if (Math.Abs(dropAxis.DotProduct(primaryAxisInPlane)) > 0.9)
			{
				continue;
			}

			double minScalar = double.MaxValue;
			double maxScalar = double.MinValue;
			XYZ bottomPoint = null;
			FabricationPart elbow = null;
			XYZ elbowPoint = null;
			foreach (Connector connector in ListConnectors(dropPipe))
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				double scalar = DotInPlane(connector.Origin, dropAxis, vn);
				FabricationPart mate = FindMateAtConnector(dropPipe, connector, partList);
				if (mate == null)
				{
					if (scalar < minScalar)
					{
						minScalar = scalar;
						bottomPoint = connector.Origin;
					}
					continue;
				}

				if (IsOletPart(mate) || IsGasketPart(mate) || IsWeldPart(mate) || !IsFittingLikeForSpoolDim(mate))
				{
					continue;
				}

				if (scalar > maxScalar)
				{
					maxScalar = scalar;
					elbow = mate;
					elbowPoint = GetFabricationFittingDimensionAnchor(mate, dropPipe, null, partList);
				}
			}

			if (elbow == null || bottomPoint == null || elbowPoint == null)
			{
				continue;
			}

			Element topPart = (Element)elbow;
			XYZ topPoint = elbowPoint;
			Element bottomPart = (Element)dropPipe;
			XYZ bottomAnchorPoint = bottomPoint;
			if (DotInPlane(bottomAnchorPoint, upInPlane, vn) > DotInPlane(topPoint, upInPlane, vn))
			{
				topPart = bottomPart;
				bottomPart = (Element)elbow;
				topPoint = bottomAnchorPoint;
				bottomAnchorPoint = elbowPoint;
			}

			if (bottomAnchorPoint.DistanceTo(topPoint) < SegmentLengthEpsilonFeet)
			{
				continue;
			}

			yield return new SpoolRunDimensionIntent
			{
				Kind = SpoolRunDimensionKind.VerticalDrop,
				WitnessAPart = topPart,
				WitnessBPart = bottomPart,
				WitnessAPointWorld = topPoint,
				WitnessBPointWorld = bottomAnchorPoint,
				Orientation = ClassifySegmentOrientation(topPoint, bottomAnchorPoint),
				StackSlot = 0
			};
		}
	}

	private static int TryValidateRunDimensionPlans(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		XYZ unitAxis,
		SpoolingManagerSettings spoolSettings,
		List<string> failureNotes)
	{
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}

		DimensionType dimType = TryResolveLinearDimensionType(doc, spoolSettings);
		bool hasDimAnnotations = spoolSettings?.AutoDimAnnotations == true;
		int planned = 0;

		foreach (SpoolRunDimensionIntent intent in BuildSpoolRunDimensionIntents(view, parts, unitAxis))
		{
			FabricationPart partA = intent.WitnessAPart as FabricationPart;
			FabricationPart partB = intent.WitnessBPart as FabricationPart;
			if (partA == null || partB == null)
			{
				failureNotes?.Add("Run dim skip: missing witness part.");
				continue;
			}

			if (!TryResolveFabricationOriginReference(doc, view, partA, out Reference refA, out string diagA))
			{
				failureNotes?.Add("Run dim skip (" + intent.Kind + " A): " + diagA);
				continue;
			}

			if (!TryResolveFabricationOriginReference(doc, view, partB, out Reference refB, out string diagB))
			{
				failureNotes?.Add("Run dim skip (" + intent.Kind + " B): " + diagB);
				continue;
			}

			if (!TryResolveSpoolDimensionOffsetDirection(view, intent.Orientation, out XYZ offsetDirection, out string offsetDiag))
			{
				failureNotes?.Add("Run dim skip (" + intent.Kind + "): " + offsetDiag);
				continue;
			}

			if (!TrySynthesizeSpoolLinearDimensionLine(
				view,
				intent.WitnessAPointWorld,
				intent.WitnessBPointWorld,
				offsetDirection,
				intent.StackSlot,
				dimType,
				hasDimAnnotations,
				out Line dimensionLine,
				out double offsetDistanceFeet,
				out string synthDiag))
			{
				failureNotes?.Add("Run dim skip (" + intent.Kind + "): " + synthDiag);
				continue;
			}

			planned++;
			TryAppendAutoDimPlacementLog(
				view.Name,
				intent.Kind
				+ " slot=" + intent.StackSlot
				+ " " + intent.Orientation
				+ " offsetDist=" + offsetDistanceFeet.ToString("0.###", CultureInfo.InvariantCulture)
				+ " a=" + GetElementIdValue(intent.WitnessAPart.Id)
				+ " b=" + GetElementIdValue(intent.WitnessBPart.Id)
				+ " line=" + FormatLineEndpoints(dimensionLine));
		}

		return planned;
	}
}
