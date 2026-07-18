using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>Public wrappers so TeachAutoDimService can call private partial helpers.</summary>
public partial class CreateSpoolSheetsHandler
{
	internal static bool TryGetViewPlaneAxesPublic(View view, out XYZ viewNormal, out XYZ right, out XYZ up)
		=> TryGetViewPlaneAxes(view, out viewNormal, out right, out up);

	internal static bool IsLinearDimensionTypePublic(DimensionType dimType)
		=> IsLinearDimensionType(dimType);

	internal static bool TryMeasureDimensionSheetOffsetPublic(
		View view, Dimension dim, XYZ right, XYZ up, out double offsetSheetFeet, out int offsetSign)
		=> TryMeasureDimensionSheetOffset(view, dim, right, up, out offsetSheetFeet, out offsetSign);

	internal static bool TryGetDimensionLineSegmentInViewPublic(
		View view, Dimension dim, out XYZ a, out XYZ b, out bool isUpAxis)
		=> TryGetDimensionLineSegmentInView(view, dim, out a, out b, out isUpAxis);

	internal static bool IsOletPartPublic(FabricationPart part)
		=> IsOletPart(part);

	internal static void LogTeachFinished(string assemblyName, string viewName, int positive, int anti)
	{
		TryAppendAutoDimLearningLog(
			assemblyName ?? "teach",
			viewName ?? "?",
			"teach-finish positive=" + positive + " antiPatterns=" + anti
				+ " store=" + AutoDimLessonStore.LessonsFilePath);
	}
}
