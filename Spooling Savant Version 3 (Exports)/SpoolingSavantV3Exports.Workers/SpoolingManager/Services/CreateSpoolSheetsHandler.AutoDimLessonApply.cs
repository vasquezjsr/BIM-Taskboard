using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>Apply taught lessons before/after place: offset override + snap repair.</summary>
public partial class CreateSpoolSheetsHandler
{
	/// <summary>
	/// Resolve model offset from lessons when available; else standard stack/slot math.
	/// </summary>
	private static double ResolveOffsetPreferringLessons(
		View scaleView,
		DimensionType dimType,
		int stackIndex,
		bool isHorizontalMeasurement,
		bool hasDimAnnotations,
		int placementSign,
		string dimensionPolicyRole,
		string roleKey,
		out int forcedOffsetSign)
	{
		forcedOffsetSign = placementSign == 0 ? 1 : Math.Sign(placementSign);
		// Teach Auto-Dim retired — never apply AutoDimLessons.json; universal slot math only.
		return ResolveSpoolLinearDimensionModelOffset(
			scaleView, dimType, stackIndex, isHorizontalMeasurement, hasDimAnnotations);
	}

	private static string TryBuildLessonRoleKey(Document doc, Reference refA, Reference refB, bool isHorizontal)
	{
		ClassifyLessonHost(doc, refA, out string kindA);
		ClassifyLessonHost(doc, refB, out string kindB);
		return AutoDimLessonStore.BuildRoleKey(kindA, kindB, isHorizontal);
	}

	private static void ClassifyLessonHost(Document doc, Reference r, out string kind)
	{
		kind = "Unknown";
		if (doc == null || r == null)
		{
			return;
		}
		try
		{
			Element e = doc.GetElement(r.ElementId);
			if (e is FabricationPart fp)
			{
				if (IsOletPart(fp))
				{
					kind = "Olet";
				}
				else if (FabricationPartClassification.IsFlangePart(fp, doc))
				{
					kind = "Flange";
				}
				else if (FabricationPartClassification.IsElbowPart(fp, doc))
				{
					kind = "Elbow";
				}
				else if (FabricationPartClassification.IsTeePart(fp, doc))
				{
					kind = "Tee";
				}
				else if (FabricationPartClassification.IsStraightPipeRun(fp))
				{
					kind = "Pipe";
				}
				else
				{
					kind = "Fitting";
				}
			}
		}
		catch
		{
		}
	}

	/// <summary>Teach Auto-Dim retired — no lesson-based dim moves.</summary>
	private static int RepairDimsFromTaughtLessons(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		SpoolingManagerSettings spoolSettings)
	{
		return 0;
	}
}
