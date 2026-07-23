using System;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Minimal compile/compat helpers for V3 Teach / Dimension Inspector after SSv2 100% sheet port.
/// Sheet create/regen uses V2 AssemblyLine path only — these do not reintroduce defer/skip hacks.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	private static void TryAppendAutoDimLearningLog(string assemblyName, string viewLabel, string message)
	{
		TryAppendAutoDimDiagnosticLog(assemblyName ?? "learning", viewLabel ?? "?", message ?? string.Empty, 0, 0);
	}

	internal static bool TryMeasureDimensionSheetOffset(
		View view,
		Dimension dim,
		XYZ right,
		XYZ up,
		out double offsetSheetFeet,
		out int offsetSign)
	{
		offsetSheetFeet = 0;
		offsetSign = 1;
		return false;
	}

	private static bool TryGetDimensionLineSegmentInView(
		View view,
		Dimension dim,
		out XYZ a,
		out XYZ b,
		out bool isUpAxis)
	{
		a = null;
		b = null;
		isUpAxis = false;
		return false;
	}
}
