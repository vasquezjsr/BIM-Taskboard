using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Live mirror of auto-dim commit logic vs the selected Linear dimension in Revit.
/// Select / move a dim → fingerprint changes → DetailText + dump files update.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	private static readonly string WorkspaceDimInspectorDumpPath =
		@"C:\Apps\BIM-Taskboard\dim-inspector-live.txt";

	internal static DimensionInspectorReport BuildDimensionInspectorReport(UIDocument uidoc, bool exportViewImage)
	{
		var report = new DimensionInspectorReport
		{
			Success = false,
			StatusMessage = "No active view.",
			DetailText = "Open a spool view and select a Linear dimension."
		};
		try
		{
		if (uidoc == null)
		{
			WriteDimensionInspectorDump(report.DetailText);
			return report;
		}

		Document doc = uidoc.Document;
		View view = uidoc.ActiveView;
		if (doc == null || view == null)
		{
			WriteDimensionInspectorDump(report.DetailText);
			return report;
		}

		report.ViewName = view.Name;
		// Image export is opt-in only (Refresh Now). Idle export previously fatal-crashed Revit.
		if (exportViewImage)
		{
			try
			{
				report.ViewImagePath = DimensionInspectorService.TryExportActiveViewImage(doc, view);
			}
			catch
			{
				report.ViewImagePath = null;
			}
		}

		if (!TryGetViewPlaneAxes(view, out _, out XYZ right, out XYZ up))
		{
			report.StatusMessage = "View axes unavailable.";
			report.DetailText = BuildAxesFailureText(view);
			WriteDimensionInspectorDump(report.DetailText);
			return report;
		}

		List<Dimension> selected = GetSelectedLinearDimensions(uidoc);
		if (selected.Count == 0)
		{
			report.Success = true;
			report.StatusMessage = "Select a Linear dimension — live ACTUAL vs CODE updates as you move it.";
			report.DetailText = BuildNoSelectionText(view, right, up);
			WriteDimensionInspectorDump(report.DetailText);
			return report;
		}

		var sb = new StringBuilder();
		sb.AppendLine("══════════════════════════════════════════════════════════════");
		sb.AppendLine(" DIMENSION INSPECTOR — live ACTUAL (Revit) vs CODE (auto-dim)");
		sb.AppendLine(" Updated UTC: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
		sb.AppendLine(" Dump: " + ProgramDataDimInspectorDumpPath());
		sb.AppendLine(" Dump: " + WorkspaceDimInspectorDumpPath);
		sb.AppendLine("══════════════════════════════════════════════════════════════");
		sb.AppendLine();
		sb.AppendLine("VIEW");
		sb.AppendLine("  Name:   " + (view.Name ?? "?"));
		sb.AppendLine("  Id:     " + view.Id.Value);
		sb.AppendLine("  Scale:  " + Math.Max(view.Scale, 1) + " (sheetFeet * scale = modelFeet)");
		sb.AppendLine("  Type:   " + view.ViewType);
		sb.AppendLine();

		for (int i = 0; i < selected.Count; i++)
		{
			AppendDimensionLogicBlock(sb, doc, view, selected[i], right, up, i + 1, selected.Count);
		}

		sb.AppendLine();
		sb.AppendLine("CODE PATH (what Create/Refresh uses when placing this kind of dim)");
		sb.AppendLine("  1. TryBuildLessonRoleKey → roleKey (sorted HostA_HostB_H|V)");
		sb.AppendLine("  2. ResolveOffsetPreferringLessons(roleKey, policy, stackIndex)");
		sb.AppendLine("       → lesson TargetOffsetSheetFeet if taught, else slot math");
		sb.AppendLine("  3. ResolveSpoolLinearDimensionModelOffset");
		sb.AppendLine("       slot0: 3/8\" sheet (no annot) or 1/2\" (with annot) × view.Scale");
		sb.AppendLine("       slot≥1: firstOffset + slot × snap (default 1/4\" sheet)");
		sb.AppendLine("  4. TryBuildViewLinearDimensionLine: dim line at chord MID ± offsetSigned");
		sb.AppendLine("  5. TryCommitNewDimension → Dimension.DimensionLine");
		sb.AppendLine();
		sb.AppendLine("MEASURE (ACTUAL offset shown above)");
		sb.AppendLine("  TryMeasureDimensionSheetOffset:");
		sb.AppendLine("    dim-line mid vs witness-chord mid along offset axis");
		sb.AppendLine("    H measure → offset along Up; V measure → offset along Right");
		sb.AppendLine("    offsetSheetFeet = |delta_model| / view.Scale");

		string detail = sb.ToString();
		report.Success = true;
		report.StatusMessage = selected.Count == 1
			? "Live: 1 Linear selected — move it in Revit to see CODE vs ACTUAL change."
			: "Live: " + selected.Count + " Linears selected.";
		report.DetailText = detail;
		WriteDimensionInspectorDump(detail);
		return report;
		}
		catch (Exception ex)
		{
			report.Success = false;
			report.StatusMessage = "Inspector read failed (safe).";
			report.DetailText = "Exception during Dim Inspector report:\n" + ex.Message;
			try { WriteDimensionInspectorDump(report.DetailText); } catch { }
			return report;
		}
	}

	internal static string ComputeDimensionInspectorFingerprint(UIDocument uidoc)
	{
		if (uidoc?.Document == null || uidoc.ActiveView == null)
		{
			return "empty";
		}
		try
		{
			Document doc = uidoc.Document;
			View view = uidoc.ActiveView;
			var parts = new List<string>
			{
				"v=" + view.Id.Value,
				"s=" + Math.Max(view.Scale, 1)
			};
			List<Dimension> dims = GetSelectedLinearDimensions(uidoc);
			if (dims.Count == 0)
			{
				parts.Add("sel=0");
				return string.Join("|", parts);
			}

			if (!TryGetViewPlaneAxes(view, out _, out XYZ right, out XYZ up))
			{
				right = null;
				up = null;
			}

			foreach (Dimension dim in dims.OrderBy(d => ((Element)d).Id.Value))
			{
				long id = ((Element)dim).Id.Value;
				string valueKey = "?";
				try
				{
					if (dim.Value.HasValue)
					{
						valueKey = dim.Value.Value.ToString("0.########", CultureInfo.InvariantCulture);
					}
				}
				catch
				{
				}

				string curveKey = "?";
				try
				{
					Curve c = dim.Curve;
					if (c != null)
					{
						XYZ a = c.GetEndPoint(0);
						XYZ b = c.GetEndPoint(1);
						curveKey = FmtPt(a) + ".." + FmtPt(b);
					}
				}
				catch
				{
				}

				string offKey = "?";
				if (right != null && up != null
					&& TryMeasureDimensionSheetOffset(view, dim, right, up, out double sheetFeet, out int sign))
				{
					offKey = sign + ":" + sheetFeet.ToString("0.########", CultureInfo.InvariantCulture);
				}

				int refCount = 0;
				string refIds = "";
				try
				{
					ReferenceArray refs = dim.References;
					if (refs != null)
					{
						refCount = refs.Size;
						var ids = new List<long>();
						for (int i = 0; i < refs.Size; i++)
						{
							ids.Add(refs.get_Item(i).ElementId.Value);
						}
						refIds = string.Join(",", ids);
					}
				}
				catch
				{
				}

				parts.Add("d=" + id + ";val=" + valueKey + ";crv=" + curveKey + ";off=" + offKey + ";n=" + refCount + ";r=" + refIds);
			}
			return string.Join("|", parts);
		}
		catch
		{
			return "err:" + DateTime.UtcNow.Ticks;
		}
	}

	private static void AppendDimensionLogicBlock(
		StringBuilder sb,
		Document doc,
		View view,
		Dimension dim,
		XYZ right,
		XYZ up,
		int index,
		int total)
	{
		long dimId = ((Element)dim).Id.Value;
		sb.AppendLine("──────────────────────────────────────────────────────────────");
		sb.AppendLine(" SELECTED LINEAR " + index + "/" + total + "  id=" + dimId);
		sb.AppendLine("──────────────────────────────────────────────────────────────");

		double spanInches = 0;
		string spanRaw = "(no Value)";
		try
		{
			if (dim.Value.HasValue)
			{
				spanInches = dim.Value.Value * 12.0;
				spanRaw = dim.Value.Value.ToString("0.########", CultureInfo.InvariantCulture) + " ft"
					+ "  =  " + spanInches.ToString("0.###", CultureInfo.InvariantCulture) + "\"";
			}
		}
		catch (Exception ex)
		{
			spanRaw = "error: " + ex.Message;
		}

		sb.AppendLine("ACTUAL — measured value in Revit");
		sb.AppendLine("  dim.Value:     " + spanRaw);
		sb.AppendLine("  (BOM lengths are separate — witness points drive this span)");

		bool measured = TryMeasureDimensionSheetOffset(view, dim, right, up, out double actualSheetFeet, out int actualSign);
		bool hasSeg = TryGetDimensionLineSegmentInView(view, dim, out XYZ lineA, out XYZ lineB, out bool isUpAxis);
		bool isHorizontal = hasSeg ? !isUpAxis : true;

		sb.AppendLine();
		sb.AppendLine("ACTUAL — placement (live; updates when you drag the dim line)");
		if (measured)
		{
			sb.AppendLine("  offsetSheetFeet: " + actualSheetFeet.ToString("0.######", CultureInfo.InvariantCulture)
				+ "  (" + (actualSheetFeet * 12.0).ToString("0.###", CultureInfo.InvariantCulture) + "\" sheet)");
			sb.AppendLine("  offsetSign:      " + actualSign + "  (+ = along Up for H / Right for V)");
			sb.AppendLine("  offsetModelFeet: " + (actualSheetFeet * Math.Max(view.Scale, 1)).ToString("0.######", CultureInfo.InvariantCulture));
		}
		else
		{
			sb.AppendLine("  (could not measure offset — need ≥2 witness sample points)");
		}
		if (hasSeg)
		{
			sb.AppendLine("  dim-line axis:   " + (isUpAxis ? "Up (VERTICAL measure → offset along Right)" : "Right (HORIZONTAL measure → offset along Up)"));
			sb.AppendLine("  dim-line A:      " + FmtPt(lineA));
			sb.AppendLine("  dim-line B:      " + FmtPt(lineB));
			sb.AppendLine("  dim-line mid:    " + FmtPt((lineA + lineB) * 0.5));
		}

		string kindA = "Unknown";
		string kindB = "Unknown";
		string nameA = "?";
		string nameB = "?";
		long hostIdA = 0;
		long hostIdB = 0;
		try
		{
			ReferenceArray refs = dim.References;
			if (refs != null && refs.Size >= 2)
			{
				ClassifyInspectorHost(doc, refs.get_Item(0), out hostIdA, out kindA, out nameA);
				ClassifyInspectorHost(doc, refs.get_Item(1), out hostIdB, out kindB, out nameB);
			}
			sb.AppendLine();
			sb.AppendLine("ACTUAL — witnesses (what the dimension is anchored to)");
			if (refs != null)
			{
				for (int i = 0; i < refs.Size; i++)
				{
					Reference r = refs.get_Item(i);
					ClassifyInspectorHost(doc, r, out long hid, out string hk, out string hn);
					SpoolWitnessClassification w = ClassifyDimensionWitness(doc, r);
					sb.AppendLine("  [" + i + "] hostId=" + hid + " kind=" + hk + " letter=" + w.Letter
						+ " name=" + (hn ?? "?"));
					sb.AppendLine("       stable=" + (w.Notes ?? ""));
				}
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("  witness error: " + ex.Message);
		}

		string roleKey = AutoDimLessonStore.BuildRoleKey(kindA, kindB, isHorizontal);
		string policy = GuessInspectorPolicyRole(kindA, kindB, isHorizontal);
		SpoolDimensionPatternClassification pattern = ClassifyDimensionPattern(doc, dim);

		sb.AppendLine();
		sb.AppendLine("CODE — classification (same helpers Create/Teach use)");
		sb.AppendLine("  hostA → hostB:  " + kindA + " → " + kindB);
		sb.AppendLine("  roleKey:        " + roleKey);
		sb.AppendLine("  policyRole:     " + policy);
		sb.AppendLine("  pattern:        " + (pattern.PatternLabel ?? "?") + "  (" + pattern.Pattern + ")");
		sb.AppendLine("  pattern lesson: " + (pattern.LessonStatus ?? ""));
		sb.AppendLine("  axis:           " + (isHorizontal ? "H (horizontal measure)" : "V (vertical measure)"));

		// Placement commit also passes hasDimAnnotations when tags sit on the dim.
		// Keep inspector conservative: assume no annot unless Above/Below text is set.
		bool hasAnnot = false;
		try
		{
			hasAnnot = !string.IsNullOrWhiteSpace(dim.Above) || !string.IsNullOrWhiteSpace(dim.Below);
		}
		catch
		{
			hasAnnot = false;
		}

		DimensionType dimType = null;
		try { dimType = dim.DimensionType; } catch { }

		int stackIndex = 0;
		double codeModelNoLesson = ResolveSpoolLinearDimensionModelOffset(
			view, dimType, stackIndex, isHorizontal, hasAnnot);
		int scale = Math.Max(view.Scale, 1);
		double codeSheetNoLesson = codeModelNoLesson / scale;

		int forcedSign;
		double codeModelWithLesson = ResolveOffsetPreferringLessons(
			view, dimType, stackIndex, isHorizontal, hasAnnot,
			actualSign == 0 ? 1 : actualSign, policy, roleKey, out forcedSign);
		double codeSheetWithLesson = codeModelWithLesson / scale;

		bool lessonHit = AutoDimLessonStore.TryGetPositiveLesson(roleKey, policy, isHorizontal, out AutoDimPositiveLesson lesson);
		bool antiHit = measured && AutoDimLessonStore.MatchesAntiPattern(roleKey, policy, isHorizontal, actualSheetFeet);

		sb.AppendLine();
		sb.AppendLine("CODE — intended offset (what auto-dim WOULD place for stack slot 0)");
		sb.AppendLine("  hasDimAnnotations: " + hasAnnot);
		sb.AppendLine("  slot0 formula:     " + (hasAnnot ? "1/2\" sheet" : "3/8\" sheet") + " × scale " + scale);
		sb.AppendLine("  WITHOUT lesson:    " + codeSheetNoLesson.ToString("0.######", CultureInfo.InvariantCulture)
			+ " sheet ft (" + (codeSheetNoLesson * 12.0).ToString("0.###", CultureInfo.InvariantCulture) + "\")"
			+ "  model=" + codeModelNoLesson.ToString("0.######", CultureInfo.InvariantCulture) + " ft");
		sb.AppendLine("  WITH lesson/policy:" + codeSheetWithLesson.ToString("0.######", CultureInfo.InvariantCulture)
			+ " sheet ft (" + (codeSheetWithLesson * 12.0).ToString("0.###", CultureInfo.InvariantCulture) + "\")"
			+ "  model=" + codeModelWithLesson.ToString("0.######", CultureInfo.InvariantCulture) + " ft"
			+ "  forcedSign=" + forcedSign);
		if (lessonHit && lesson != null)
		{
			sb.AppendLine("  LESSON HIT:        YES");
			sb.AppendLine("    RoleKey:         " + (lesson.RoleKey ?? "?"));
			sb.AppendLine("    PolicyRole:      " + (lesson.PolicyRole ?? "?"));
			sb.AppendLine("    TargetOffset:    " + lesson.TargetOffsetSheetFeet.ToString("0.######", CultureInfo.InvariantCulture)
				+ " sheet ft (" + (lesson.TargetOffsetSheetFeet * 12.0).ToString("0.###", CultureInfo.InvariantCulture) + "\")");
			sb.AppendLine("    OffsetSign:      " + lesson.OffsetSign);
			sb.AppendLine("    ContentTaught:   " + lesson.ContentTaught + "  PlacementTaught: " + lesson.PlacementTaught);
			sb.AppendLine("    TeachCount:      " + lesson.TeachCount);
			sb.AppendLine("    Sample span:     " + lesson.SpanInches.ToString("0.###", CultureInfo.InvariantCulture) + "\"");
		}
		else
		{
			sb.AppendLine("  LESSON HIT:        no matching positive lesson for roleKey/policy");
		}
		sb.AppendLine("  ANTI-PATTERN:      " + (antiHit ? "MATCHES a taught rejection (FarOffset/etc.)" : "none"));

		if (measured)
		{
			double deltaSheet = actualSheetFeet - codeSheetWithLesson;
			double deltaInch = deltaSheet * 12.0;
			sb.AppendLine();
			sb.AppendLine("DIFF — ACTUAL placement vs CODE intended (slot0 + lesson)");
			sb.AppendLine("  Δ offset sheet:   " + deltaSheet.ToString("0.######", CultureInfo.InvariantCulture)
				+ " ft  (" + deltaInch.ToString("0.###", CultureInfo.InvariantCulture) + "\")");
			sb.AppendLine("  sign match:       " + (actualSign == forcedSign ? "YES" : "NO  actual=" + actualSign + " code=" + forcedSign));
			if (Math.Abs(deltaSheet) <= AutoDimLessonStore.OffsetTolSheetFeet
				&& actualSign == forcedSign)
			{
				sb.AppendLine("  verdict:          PLACEMENT MATCHES CODE (within 1/16\" sheet)");
			}
			else
			{
				sb.AppendLine("  verdict:          PLACEMENT ≠ CODE — dragging the dim changes ACTUAL only;");
				sb.AppendLine("                    CODE numbers above only change if lessons/stack/annot change.");
			}
		}

		if (lessonHit && lesson != null && lesson.SpanInches > 0.01 && spanInches > 0.01)
		{
			double spanDelta = spanInches - lesson.SpanInches;
			sb.AppendLine();
			sb.AppendLine("DIFF — ACTUAL span vs last taught sample span (content)");
			sb.AppendLine("  actual span:      " + spanInches.ToString("0.###", CultureInfo.InvariantCulture) + "\"");
			sb.AppendLine("  taught sample:    " + lesson.SpanInches.ToString("0.###", CultureInfo.InvariantCulture) + "\"");
			sb.AppendLine("  Δ:                " + spanDelta.ToString("0.###", CultureInfo.InvariantCulture) + "\"");
			sb.AppendLine("  note: span comes from witness anchors, not from dim-line offset.");
		}

		sb.AppendLine();
		sb.AppendLine("HOST IDS (for Cursor / debug)");
		sb.AppendLine("  A: " + hostIdA + "  " + kindA + "  " + nameA);
		sb.AppendLine("  B: " + hostIdB + "  " + kindB + "  " + nameB);
		sb.AppendLine();
	}

	private static List<Dimension> GetSelectedLinearDimensions(UIDocument uidoc)
	{
		var list = new List<Dimension>();
		if (uidoc?.Document == null)
		{
			return list;
		}
		try
		{
			Document doc = uidoc.Document;
			foreach (ElementId id in uidoc.Selection.GetElementIds())
			{
				if (doc.GetElement(id) is Dimension dim)
				{
					DimensionType dt = null;
					try { dt = dim.DimensionType; } catch { }
					if (IsLinearDimensionType(dt))
					{
						list.Add(dim);
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static void ClassifyInspectorHost(Document doc, Reference r, out long id, out string kind, out string name)
	{
		id = 0;
		kind = "Unknown";
		name = "?";
		if (doc == null || r == null)
		{
			return;
		}
		try
		{
			Element e = doc.GetElement(r.ElementId);
			if (e == null)
			{
				return;
			}
			id = e.Id.Value;
			name = e.Name ?? "?";
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

	private static string GuessInspectorPolicyRole(string kindA, string kindB, bool isHorizontal)
	{
		string a = AutoDimLessonStore.NormalizeKind(kindA);
		string b = AutoDimLessonStore.NormalizeKind(kindB);
		if (a == "Olet" || b == "Olet")
		{
			return "olet-pickup";
		}
		if ((a == "Elbow" || b == "Elbow") && (a == "Flange" || b == "Flange"))
		{
			return isHorizontal ? "elbow-flange-h" : "elbow-flange-v";
		}
		if ((a == "Elbow" || b == "Elbow") && (a == "Pipe" || b == "Pipe"))
		{
			return isHorizontal ? "elbow-pipe-h" : "elbow-pipe-v";
		}
		if ((a == "Flange" || b == "Flange") && (a == "Pipe" || b == "Pipe"))
		{
			return isHorizontal ? "flange-pipe-h" : "flange-pipe-v";
		}
		return isHorizontal ? "generic-h" : "generic-v";
	}

	private static string BuildNoSelectionText(View view, XYZ right, XYZ up)
	{
		var sb = new StringBuilder();
		sb.AppendLine("No Linear dimension selected.");
		sb.AppendLine();
		sb.AppendLine("HOW TO USE");
		sb.AppendLine("  1. Activate a dimensioned assembly view (e.g. Elevation Front).");
		sb.AppendLine("  2. Select one Linear dimension in Revit.");
		sb.AppendLine("  3. This panel + dump files update within ~0.5s.");
		sb.AppendLine("  4. Drag the dim line / change witnesses → ACTUAL block updates live.");
		sb.AppendLine("  5. Ask Cursor: \"read dim-inspector-live.txt and explain\"");
		sb.AppendLine();
		sb.AppendLine("VIEW: " + (view?.Name ?? "?") + "  scale=" + Math.Max(view?.Scale ?? 1, 1));
		sb.AppendLine("Dump: " + ProgramDataDimInspectorDumpPath());
		sb.AppendLine("Dump: " + WorkspaceDimInspectorDumpPath);
		WriteDimensionInspectorDump(sb.ToString());
		return sb.ToString();
	}

	private static string BuildAxesFailureText(View view)
	{
		return "Could not resolve view Right/Up axes for view " + (view?.Name ?? "?")
			+ ". Activate a non-sheet drafting/plan/elevation/3D view.";
	}

	private static string FmtPt(XYZ p)
	{
		if (p == null)
		{
			return "(null)";
		}
		return "("
			+ p.X.ToString("0.####", CultureInfo.InvariantCulture) + ", "
			+ p.Y.ToString("0.####", CultureInfo.InvariantCulture) + ", "
			+ p.Z.ToString("0.####", CultureInfo.InvariantCulture) + ")";
	}

	private static string ProgramDataDimInspectorDumpPath()
	{
		try
		{
			return Path.Combine(InstallLayout.GetPreferredModuleFolder(), "DimensionInspectorLive.txt");
		}
		catch
		{
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				"Autodesk", "Revit", "Addins", "2024",
				"Spooling-Savant-V3-Exports", "SpoolingManager", "DimensionInspectorLive.txt");
		}
	}

	private static void WriteDimensionInspectorDump(string text)
	{
		string body = text ?? string.Empty;
		foreach (string path in new[] { ProgramDataDimInspectorDumpPath(), WorkspaceDimInspectorDumpPath })
		{
			try
			{
				string dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir))
				{
					Directory.CreateDirectory(dir);
				}
				File.WriteAllText(path, body);
			}
			catch
			{
			}
		}
	}
}
