using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Step 5 — synthesize axis-aligned Linear dimension lines in the view plane.
/// Endpoints are forced flat; witness references carry back to true fitting refs (Step 2).
/// Never creates DetailLine / DetailCurve helpers — only model Linear dimensions via NewDimension.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	/// <summary>Synthesized dimension line ready for <see cref="Document.Create.NewDimension"/>.</summary>
	public sealed class SpoolLinearDimensionLinePlan
	{
		public Line DimensionLine { get; set; }
		public XYZ WitnessAWorld { get; set; }
		public XYZ WitnessBWorld { get; set; }
		public Reference WitnessAReference { get; set; }
		public Reference WitnessBReference { get; set; }
		public SpoolRunSegmentOrientation Orientation { get; set; }
		public XYZ OffsetDirectionView { get; set; }
		public double OffsetDistanceFeet { get; set; }
		public int StackSlot { get; set; }
		public bool IsAxisAligned { get; set; }
	}

	private const double AxisAlignmentToleranceFeet = 1E-09;

	/// <summary>
	/// Build a forced horizontal/vertical dimension line from witness world points and Step 4 offset direction.
	/// The line is never drawn directly between raw reference snap points.
	/// </summary>
	public static bool TrySynthesizeSpoolLinearDimensionLine(
		View view,
		XYZ witnessA,
		XYZ witnessB,
		XYZ offsetDirectionView,
		int stackSlot,
		DimensionType dimensionTypeForOffsetScale,
		bool hasDimAnnotations,
		out Line dimensionLine,
		out double offsetDistanceFeet,
		out string diagnostic)
	{
		dimensionLine = null;
		offsetDistanceFeet = 0.0;
		diagnostic = string.Empty;
		if (view == null || witnessA == null || witnessB == null)
		{
			diagnostic = "Missing view or witness points.";
			return false;
		}

		if (offsetDirectionView == null || offsetDirectionView.GetLength() < 1E-09)
		{
			diagnostic = "Missing offset direction.";
			return false;
		}

		XYZ chord = witnessB - witnessA;
		if (chord == null || chord.GetLength() < SegmentLengthEpsilonFeet)
		{
			diagnostic = "Witness points are too close to synthesize a dimension line.";
			return false;
		}

		bool isHorizontalMeasurement = IsViewHorizontalMeasurement(view, chord);
		offsetDistanceFeet = ResolveSpoolLinearDimensionModelOffset(
			view,
			dimensionTypeForOffsetScale,
			stackSlot,
			isHorizontalMeasurement,
			hasDimAnnotations);

		if (!TryBuildStackedLinearDimensionLine(view, witnessA, witnessB, offsetDistanceFeet, offsetDirectionView, out dimensionLine)
			|| dimensionLine == null)
		{
			diagnostic = "Could not build stacked linear dimension line in the view plane.";
			return false;
		}

		if (!TryValidateLinearOnlyDimensionLine(view, dimensionLine, out diagnostic))
		{
			dimensionLine = null;
			return false;
		}

		return true;
	}

	/// <summary>Spec Step 5/7 — reject any dimension line that is not perfectly axis-aligned in the view.</summary>
	public static bool TryValidateLinearOnlyDimensionLine(View view, Line line, out string diagnostic)
	{
		diagnostic = string.Empty;
		if (line == null)
		{
			diagnostic = "Dimension line is null.";
			return false;
		}

		if (!line.IsBound)
		{
			diagnostic = "Dimension line must be bound.";
			return false;
		}

		if (!TryIsViewLinearDimensionLine(view, line))
		{
			diagnostic = "Dimension line is not horizontal/vertical in the view plane.";
			return false;
		}

		XYZ p1 = line.GetEndPoint(0);
		XYZ p2 = line.GetEndPoint(1);
		if (!IsViewPlaneAxisAlignedSegment(view, p1, p2))
		{
			diagnostic = "Dimension endpoints are not axis-aligned — check reference source.";
			return false;
		}

		return true;
	}

	public static bool TryBuildSpoolLinearDimensionLinePlan(
		View view,
		XYZ witnessA,
		XYZ witnessB,
		Reference refA,
		Reference refB,
		SpoolRunSegmentPlan segmentPlan,
		int stackSlot,
		DimensionType dimensionTypeForOffsetScale,
		bool hasDimAnnotations,
		out SpoolLinearDimensionLinePlan plan,
		out string diagnostic)
	{
		plan = null;
		diagnostic = string.Empty;
		if (segmentPlan == null)
		{
			diagnostic = "Missing segment plan.";
			return false;
		}

		if (!TrySynthesizeSpoolLinearDimensionLine(
			view,
			witnessA,
			witnessB,
			segmentPlan.OffsetDirectionView,
			stackSlot,
			dimensionTypeForOffsetScale,
			hasDimAnnotations,
			out Line dimensionLine,
			out double offsetDistanceFeet,
			out diagnostic))
		{
			return false;
		}

		plan = new SpoolLinearDimensionLinePlan
		{
			DimensionLine = dimensionLine,
			WitnessAWorld = witnessA,
			WitnessBWorld = witnessB,
			WitnessAReference = refA,
			WitnessBReference = refB,
			Orientation = segmentPlan.Orientation,
			OffsetDirectionView = segmentPlan.OffsetDirectionView,
			OffsetDistanceFeet = offsetDistanceFeet,
			StackSlot = stackSlot,
			IsAxisAligned = true
		};
		return true;
	}

	private static bool IsViewPlaneAxisAlignedSegment(View view, XYZ p1, XYZ p2)
	{
		if (p1 == null || p2 == null)
		{
			return false;
		}

		if (!TryGetViewPlaneAxes(view, out XYZ viewNormal, out XYZ right, out XYZ up))
		{
			bool worldFlat = Math.Abs(p1.X - p2.X) < AxisAlignmentToleranceFeet
				|| Math.Abs(p1.Y - p2.Y) < AxisAlignmentToleranceFeet
				|| Math.Abs(p1.Z - p2.Z) < AxisAlignmentToleranceFeet;
			return worldFlat;
		}

		if (!TryGetViewSketchPlane(view, out XYZ planeOrigin, out _))
		{
			planeOrigin = p1;
		}

		XYZ rel1 = ProjectToSketchPlane(p1, planeOrigin, viewNormal) - planeOrigin;
		XYZ rel2 = ProjectToSketchPlane(p2, planeOrigin, viewNormal) - planeOrigin;
		double deltaRight = Math.Abs(rel1.DotProduct(right) - rel2.DotProduct(right));
		double deltaUp = Math.Abs(rel1.DotProduct(up) - rel2.DotProduct(up));
		return deltaRight < AxisAlignmentToleranceFeet || deltaUp < AxisAlignmentToleranceFeet;
	}

	private static int TryValidateSpoolLineSynthesisPlans(
		Document doc,
		View view,
		IList<FabricationPart> parts,
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

		foreach (SpoolOletRunStackPlan oletPlan in BuildOletRunStackPlans(parts))
		{
			if (oletPlan?.Dimensions == null)
			{
				continue;
			}

			foreach (SpoolOletStackDimensionIntent intent in oletPlan.Dimensions)
			{
				FabricationPart anchorPart = intent.AnchorPart as FabricationPart;
				if (intent.OletPart == null || anchorPart == null)
				{
					failureNotes?.Add("Line synthesis skip: missing olet or anchor part.");
					continue;
				}

				if (!TryResolveFabricationOriginReference(doc, view, intent.OletPart, out Reference oletRef, out string oletDiag))
				{
					failureNotes?.Add("Line synthesis skip: " + oletDiag);
					continue;
				}

				if (!TryResolveFabricationOriginReference(doc, view, anchorPart, out Reference anchorRef, out string anchorDiag))
				{
					failureNotes?.Add("Line synthesis skip: " + anchorDiag);
					continue;
				}

				if (!TryResolveOletPickUpSegmentPlan(view, intent, out SpoolRunSegmentPlan segmentPlan, out string segmentDiag))
				{
					failureNotes?.Add("Line synthesis skip: " + segmentDiag);
					continue;
				}

				if (!TryBuildSpoolLinearDimensionLinePlan(
					view,
					intent.AnchorPointWorld,
					intent.OletTakeoffPointWorld,
					anchorRef,
					oletRef,
					segmentPlan,
					intent.StackSlot,
					dimType,
					hasDimAnnotations,
					out SpoolLinearDimensionLinePlan linePlan,
					out string synthDiag))
				{
					failureNotes?.Add("Line synthesis skip: " + synthDiag);
					continue;
				}

				planned++;
				LogSynthesizedLinePlan(view, "OletPickUp", linePlan, intent.OletPart, anchorPart);
			}
		}

		foreach (SpoolRunSegmentPlan segment in BuildSpoolRunSegmentPlans(view, parts))
		{
			if (segment?.From?.PointWorld == null || segment.To?.PointWorld == null)
			{
				continue;
			}

			if (!TrySynthesizeSpoolLinearDimensionLine(
				view,
				segment.From.PointWorld,
				segment.To.PointWorld,
				segment.OffsetDirectionView,
				0,
				dimType,
				hasDimAnnotations,
				out Line dimensionLine,
				out double offsetDistanceFeet,
				out string synthDiag))
			{
				failureNotes?.Add("Run segment synthesis skip: " + synthDiag);
				continue;
			}

			planned++;
			TryAppendAutoDimPlacementLog(
				view.Name,
				"RunSegmentSynth path=" + segment.PathIndex
				+ " " + segment.Orientation
				+ " offsetDist=" + offsetDistanceFeet.ToString("0.###", CultureInfo.InvariantCulture)
				+ " line=" + FormatLineEndpoints(dimensionLine));
		}

		return planned;
	}

	private static void LogSynthesizedLinePlan(
		View view,
		string label,
		SpoolLinearDimensionLinePlan plan,
		FabricationPart witnessBPart,
		FabricationPart witnessAPart)
	{
		if (view == null || plan?.DimensionLine == null)
		{
			return;
		}

		TryAppendAutoDimPlacementLog(
			view.Name,
			label + " slot=" + plan.StackSlot
			+ " " + plan.Orientation
			+ " offsetDist=" + plan.OffsetDistanceFeet.ToString("0.###", CultureInfo.InvariantCulture)
			+ " anchor=" + GetElementIdValue(((Element)witnessAPart).Id)
			+ " olet=" + GetElementIdValue(((Element)witnessBPart).Id)
			+ " line=" + FormatLineEndpoints(plan.DimensionLine));
	}

	private static string FormatLineEndpoints(Line line)
	{
		if (line == null || !line.IsBound)
		{
			return "?";
		}

		XYZ p1 = line.GetEndPoint(0);
		XYZ p2 = line.GetEndPoint(1);
		return string.Format(
			CultureInfo.InvariantCulture,
			"({0:F3},{1:F3},{2:F3})-({3:F3},{4:F3},{5:F3})",
			p1.X,
			p1.Y,
			p1.Z,
			p2.X,
			p2.Y,
			p2.Z);
	}
}
