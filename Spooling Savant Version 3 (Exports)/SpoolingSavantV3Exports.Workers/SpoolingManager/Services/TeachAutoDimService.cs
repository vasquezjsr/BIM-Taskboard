using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>Capture Linear dims from the active dimensioned spool view for the Teach dialog.</summary>
public static class TeachAutoDimService
{
	public static TeachAutoDimReport BuildReport(UIDocument uidoc)
	{
		var report = new TeachAutoDimReport { Success = false };
		if (uidoc == null)
		{
			report.StatusMessage = "No active document.";
			return report;
		}
		Document doc = uidoc.Document;
		View view = uidoc.ActiveView;
		if (doc == null || view == null || view is View3D || view.IsTemplate)
		{
			report.StatusMessage = "Select a dimensioned spool detail view (Front/Back/Left/Right/Top).";
			return report;
		}

		report.ViewName = view.Name ?? "?";
		report.AssemblyName = ResolveAssemblyName(doc, view);
		if (!CreateSpoolSheetsHandler.TryGetViewPlaneAxesPublic(view, out _, out XYZ right, out XYZ up))
		{
			report.StatusMessage = "Could not resolve view axes.";
			return report;
		}

		List<TeachAutoDimListItem> items = new List<TeachAutoDimListItem>();
		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>()
				.OrderBy(d => ((Element)d).Id.Value))
			{
				if (dim == null || !dim.IsValidObject || dim.DimensionType == null
					|| !CreateSpoolSheetsHandler.IsLinearDimensionTypePublic(dim.DimensionType))
				{
					continue;
				}
				TeachAutoDimListItem item = CaptureItem(doc, view, dim, right, up);
				if (item != null)
				{
					items.Add(item);
				}
			}
		}
		catch (Exception ex)
		{
			report.StatusMessage = "Failed to enumerate dims: " + ex.Message;
			return report;
		}

		report.Dimensions = items;
		report.Success = true;
		report.StatusMessage = items.Count == 0
			? "No Linear dims in this view — Create/Refresh first, then Teach."
			: items.Count + " Linear dim(s). Mark Content ✓/✗ and Placement ✓/✗ independently, fix in Revit, Refresh, Finish.";
		return report;
	}

	public static TeachAutoDimReport FinishTeach(
		UIDocument uidoc,
		IList<long> contentCorrectIds,
		IList<long> contentIncorrectIds,
		IList<long> placementCorrectIds,
		IList<long> placementIncorrectIds,
		IDictionary<string, string> incorrectReasons)
	{
		TeachAutoDimReport report = BuildReport(uidoc);
		if (!report.Success)
		{
			return report;
		}

		HashSet<long> contentOk = new HashSet<long>(contentCorrectIds ?? new long[0]);
		HashSet<long> contentBad = new HashSet<long>(contentIncorrectIds ?? new long[0]);
		HashSet<long> placeOk = new HashSet<long>(placementCorrectIds ?? new long[0]);
		HashSet<long> placeBad = new HashSet<long>(placementIncorrectIds ?? new long[0]);

		foreach (TeachAutoDimListItem item in report.Dimensions)
		{
			if (item == null)
			{
				continue;
			}
			item.ContentCorrect = contentOk.Contains(item.DimensionId);
			item.ContentIncorrect = contentBad.Contains(item.DimensionId);
			item.PlacementCorrect = placeOk.Contains(item.DimensionId);
			item.PlacementIncorrect = placeBad.Contains(item.DimensionId);
			if (incorrectReasons != null
				&& incorrectReasons.TryGetValue(item.DimensionId.ToString(), out string reason))
			{
				item.IncorrectReason = reason;
			}
			else if (item.PlacementIncorrect)
			{
				item.IncorrectReason = "FarOffset";
			}
			else if (item.ContentIncorrect)
			{
				item.IncorrectReason = "Incorrect";
			}
		}

		(int posN, int antiN) = AutoDimLessonStore.UpsertFromTeach(
			report.Dimensions, report.ViewName, report.AssemblyName);
		report.PositiveLessonsWritten = posN;
		report.AntiPatternsWritten = antiN;
		report.StatusMessage =
			"Taught " + posN + " positive lesson(s), " + antiN + " anti-pattern(s) (content + placement). Saved to AutoDimLessons.json. Refresh Assembly to apply.";
		CreateSpoolSheetsHandler.LogTeachFinished(report.AssemblyName, report.ViewName, posN, antiN);
		return report;
	}

	private static TeachAutoDimListItem CaptureItem(Document doc, View view, Dimension dim, XYZ right, XYZ up)
	{
		if (!CreateSpoolSheetsHandler.TryMeasureDimensionSheetOffsetPublic(view, dim, right, up, out double sheetFeet, out int sign))
		{
			sheetFeet = 0;
			sign = 1;
		}
		double spanInches = 0;
		try
		{
			if (dim.Value.HasValue)
			{
				spanInches = dim.Value.Value * 12.0;
			}
		}
		catch
		{
		}

		string kindA = "Unknown";
		string kindB = "Unknown";
		string prodA = null;
		string prodB = null;
		long idA = 0;
		long idB = 0;
		try
		{
			ReferenceArray refs = dim.References;
			if (refs != null && refs.Size >= 2)
			{
				ClassifyHost(doc, refs.get_Item(0), out idA, out kindA, out prodA);
				ClassifyHost(doc, refs.get_Item(1), out idB, out kindB, out prodB);
			}
		}
		catch
		{
		}

		bool isHorizontal = true;
		if (CreateSpoolSheetsHandler.TryGetDimensionLineSegmentInViewPublic(view, dim, out _, out _, out bool isUpAxis))
		{
			isHorizontal = !isUpAxis;
		}

		string roleKey = AutoDimLessonStore.BuildRoleKey(kindA, kindB, isHorizontal);
		string policy = GuessPolicyRole(kindA, kindB, isHorizontal);
		string label = (isHorizontal ? "H " : "V ")
			+ spanInches.ToString("0.###") + "\"  "
			+ kindA + "→" + kindB
			+ "  offset=" + (sheetFeet * 12.0).ToString("0.###") + "\" sheet"
			+ "  id=" + ((Element)dim).Id.Value;

		return new TeachAutoDimListItem
		{
			DimensionId = ((Element)dim).Id.Value,
			DisplayLabel = label,
			RoleKey = roleKey,
			PolicyRole = policy,
			IsHorizontal = isHorizontal,
			OffsetSign = sign,
			OffsetSheetFeet = sheetFeet,
			SpanInches = spanInches,
			HostKindA = kindA,
			HostKindB = kindB,
			ProductHintA = prodA,
			ProductHintB = prodB,
			HostIdA = idA,
			HostIdB = idB
		};
	}

	private static void ClassifyHost(Document doc, Reference r, out long id, out string kind, out string product)
	{
		id = 0;
		kind = "Unknown";
		product = null;
		if (doc == null || r == null)
		{
			return;
		}
		Element e = doc.GetElement(r.ElementId);
		if (e == null)
		{
			return;
		}
		id = e.Id.Value;
		product = e.Name;
		if (e is FabricationPart fp)
		{
			if (CreateSpoolSheetsHandler.IsOletPartPublic(fp))
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

	private static string GuessPolicyRole(string kindA, string kindB, bool isHorizontal)
	{
		string a = AutoDimLessonStore.NormalizeKind(kindA);
		string b = AutoDimLessonStore.NormalizeKind(kindB);
		if (a == "Olet" || b == "Olet")
		{
			return "olet-pickup";
		}
		if ((a == "Flange" || b == "Flange") && (a == "Elbow" || b == "Elbow" || a == "Tee" || b == "Tee"))
		{
			return isHorizontal ? "run-overall-h" : "vertical-drop";
		}
		if (a == "Flange" || b == "Flange")
		{
			return "fitting-flange-stub";
		}
		return isHorizontal ? "run-overall-h" : "vertical-drop";
	}

	private static string ResolveAssemblyName(Document doc, View view)
	{
		try
		{
			if (view != null && view.IsAssemblyView)
			{
				Element asm = doc.GetElement(view.AssociatedAssemblyInstanceId);
				if (asm is AssemblyInstance ai)
				{
					return AssemblyDisplayName.Get(ai);
				}
			}
		}
		catch
		{
		}
		return "?";
	}
}
