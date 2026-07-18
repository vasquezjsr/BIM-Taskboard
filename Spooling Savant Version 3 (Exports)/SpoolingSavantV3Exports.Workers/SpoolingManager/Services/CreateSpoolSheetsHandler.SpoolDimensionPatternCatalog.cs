using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Step 1 contract — every manually taught witness pattern (F/C/E pairs) must be capturable
/// before auto-dim placement runs. If a pattern is taught, the engine must never skip it on any spool.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	public enum SpoolDimensionWitnessLetter
	{
		Unknown,
		Disallowed,
		F,
		C,
		E
	}

	public enum SpoolDimensionPatternKind
	{
		Unknown,
		OletPickUp,
		RunOverall,
		VerticalDrop,
		TeeBranchStub,
		FlangeToFlange,
		CenterToFlange,
		FlangeToEnd,
		FittingToFitting
	}

	[Flags]
	public enum SpoolTopologyFlags
	{
		None = 0,
		HasElbow = 1,
		HasTee = 2,
		HasFlange = 4,
		HasOletOnRun = 8,
		HasBranchTakeoffPipe = 16,
		HasVerticalDropLeg = 32
	}

	public sealed class SpoolWitnessClassification
	{
		public SpoolDimensionWitnessLetter Letter { get; set; }
		public SpoolFittingKind FittingKind { get; set; }
		public string ProductDescription { get; set; }
		public string StableRepresentation { get; set; }
		public string Notes { get; set; }
	}

	public sealed class SpoolDimensionPatternClassification
	{
		public SpoolDimensionPatternKind Pattern { get; set; }
		public SpoolDimensionWitnessLetter FromLetter { get; set; }
		public SpoolDimensionWitnessLetter ToLetter { get; set; }
		public string PatternLabel { get; set; }
		public bool LessonComplete { get; set; }
		public string LessonStatus { get; set; }
	}

	public static SpoolWitnessClassification ClassifyDimensionWitness(Document doc, Reference reference)
	{
		SpoolWitnessClassification result = new SpoolWitnessClassification
		{
			Letter = SpoolDimensionWitnessLetter.Unknown,
			FittingKind = SpoolFittingKind.Unknown,
			ProductDescription = string.Empty,
			StableRepresentation = string.Empty,
			Notes = string.Empty
		};
		if (doc == null || reference == null)
		{
			result.Notes = "Missing document or reference.";
			return result;
		}

		Element element = doc.GetElement(reference.ElementId);
		FabricationPart part = element as FabricationPart;
		if (part == null)
		{
			result.Notes = "Not a fabrication part.";
			return result;
		}

		result.FittingKind = ClassifyFabricationPart(part, doc);
		result.ProductDescription = GetFabricationPartSearchCorpus(part);
		try
		{
			result.StableRepresentation = reference.ConvertToStableRepresentation(doc) ?? string.Empty;
		}
		catch (Exception ex)
		{
			result.StableRepresentation = "(error: " + ex.Message + ")";
		}

		if (result.FittingKind == SpoolFittingKind.Valve || IsWeldWitnessPart(part))
		{
			result.Letter = SpoolDimensionWitnessLetter.Disallowed;
			result.Notes = "Valves and shop welds are never dimension anchors.";
			return result;
		}

		switch (result.FittingKind)
		{
		case SpoolFittingKind.Flange:
			result.Letter = SpoolDimensionWitnessLetter.F;
			result.Notes = "Flange face (HostFaceExcludeGasket).";
			break;
		case SpoolFittingKind.Elbow:
		case SpoolFittingKind.Tee:
			result.Letter = SpoolDimensionWitnessLetter.C;
			result.Notes = "Fitting centerline intersection.";
			break;
		case SpoolFittingKind.Olet:
			result.Letter = SpoolDimensionWitnessLetter.C;
			result.Notes = "Olet/Anvilet body center.";
			break;
		case SpoolFittingKind.Pipe:
			result.Letter = SpoolDimensionWitnessLetter.E;
			result.Notes = "Pipe open end / run terminus.";
			break;
		default:
			result.Notes = "Unmapped fitting — no stable-representation lesson for this type yet.";
			break;
		}

		return result;
	}

	public static SpoolDimensionPatternClassification ClassifyDimensionPattern(Document doc, Dimension dimension)
	{
		SpoolDimensionPatternClassification result = new SpoolDimensionPatternClassification
		{
			Pattern = SpoolDimensionPatternKind.Unknown,
			FromLetter = SpoolDimensionWitnessLetter.Unknown,
			ToLetter = SpoolDimensionWitnessLetter.Unknown,
			PatternLabel = "?-?",
			LessonComplete = false,
			LessonStatus = "No references."
		};
		if (doc == null || dimension == null)
		{
			return result;
		}

		ReferenceArray references = dimension.References;
		if (references == null || references.Size < 2)
		{
			return result;
		}

		SpoolWitnessClassification w0 = ClassifyDimensionWitness(doc, references.get_Item(0));
		SpoolWitnessClassification w1 = ClassifyDimensionWitness(doc, references.get_Item(1));
		result.FromLetter = w0.Letter;
		result.ToLetter = w1.Letter;
		result.PatternLabel = w0.Letter + "-" + w1.Letter;
		result.Pattern = InferDimensionPattern(w0, w1);
		result.LessonComplete = IsSpoolDimensionPatternLessonComplete(result.Pattern);
		result.LessonStatus = result.LessonComplete
			? "LESSON_COMPLETE"
			: "LESSON_GAP — add a stable-representation lesson before enabling placement";
		return result;
	}

	public static SpoolTopologyFlags DetectSpoolTopologyFlags(IList<FabricationPart> parts, View view, XYZ unitAxis)
	{
		SpoolTopologyFlags flags = SpoolTopologyFlags.None;
		if (parts == null)
		{
			return flags;
		}

		XYZ vn = view?.ViewDirection;
		foreach (FabricationPart part in parts)
		{
			if (part == null)
			{
				continue;
			}

			switch (ClassifyFabricationPart(part, part.Document))
			{
			case SpoolFittingKind.Elbow:
				flags |= SpoolTopologyFlags.HasElbow;
				break;
			case SpoolFittingKind.Tee:
				flags |= SpoolTopologyFlags.HasTee;
				break;
			case SpoolFittingKind.Flange:
				flags |= SpoolTopologyFlags.HasFlange;
				break;
			case SpoolFittingKind.Olet:
				flags |= SpoolTopologyFlags.HasOletOnRun;
				break;
			}
		}

		if (parts.Any((p) => IsOletBranchTakeoffPipe(p, parts)))
		{
			flags |= SpoolTopologyFlags.HasBranchTakeoffPipe;
		}

		if (view != null && unitAxis != null && vn != null
			&& parts.Any((p) => IsVerticalDropLeg(p, parts, unitAxis, vn)))
		{
			flags |= SpoolTopologyFlags.HasVerticalDropLeg;
		}

		return flags;
	}

	public static IReadOnlyList<SpoolDimensionPatternKind> GetRequiredSpoolDimensionPatterns(SpoolTopologyFlags flags)
	{
		List<SpoolDimensionPatternKind> required = new List<SpoolDimensionPatternKind>();
		if (flags.HasFlag(SpoolTopologyFlags.HasOletOnRun))
		{
			required.Add(SpoolDimensionPatternKind.OletPickUp);
		}

		if (flags.HasFlag(SpoolTopologyFlags.HasElbow) || flags.HasFlag(SpoolTopologyFlags.HasTee))
		{
			required.Add(SpoolDimensionPatternKind.RunOverall);
		}

		if (flags.HasFlag(SpoolTopologyFlags.HasVerticalDropLeg))
		{
			required.Add(SpoolDimensionPatternKind.VerticalDrop);
		}

		if (flags.HasFlag(SpoolTopologyFlags.HasTee) || flags.HasFlag(SpoolTopologyFlags.HasBranchTakeoffPipe))
		{
			required.Add(SpoolDimensionPatternKind.TeeBranchStub);
		}

		if (flags.HasFlag(SpoolTopologyFlags.HasFlange))
		{
			required.Add(SpoolDimensionPatternKind.FlangeToFlange);
			required.Add(SpoolDimensionPatternKind.CenterToFlange);
		}

		return required.Distinct().ToList();
	}

	public static bool IsSpoolDimensionPatternLessonComplete(SpoolDimensionPatternKind pattern)
	{
		switch (pattern)
		{
		case SpoolDimensionPatternKind.OletPickUp:
			return HasKindReferenceLesson(SpoolFittingKind.Olet) && HasPipeEndReferenceLesson();
		case SpoolDimensionPatternKind.RunOverall:
			return (HasKindReferenceLesson(SpoolFittingKind.Elbow) || HasKindReferenceLesson(SpoolFittingKind.Tee))
				&& HasPipeEndReferenceLesson();
		case SpoolDimensionPatternKind.VerticalDrop:
			return HasKindReferenceLesson(SpoolFittingKind.Elbow) && HasPipeEndReferenceLesson();
		case SpoolDimensionPatternKind.TeeBranchStub:
			return (HasKindReferenceLesson(SpoolFittingKind.Tee) || HasKindReferenceLesson(SpoolFittingKind.Olet))
				&& HasPipeEndReferenceLesson();
		case SpoolDimensionPatternKind.FlangeToFlange:
		case SpoolDimensionPatternKind.CenterToFlange:
		case SpoolDimensionPatternKind.FlangeToEnd:
			return HasKindReferenceLesson(SpoolFittingKind.Flange);
		default:
			return false;
		}
	}

	public static string BuildSpoolPatternGapReport(SpoolTopologyFlags flags)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("=== Step 1 required patterns for this spool ===");
		foreach (SpoolDimensionPatternKind pattern in GetRequiredSpoolDimensionPatterns(flags))
		{
			bool ok = IsSpoolDimensionPatternLessonComplete(pattern);
			sb.AppendLine((ok ? "[OK] " : "[GAP] ") + pattern + " — " + DescribeSpoolDimensionPattern(pattern));
		}

		return sb.ToString().TrimEnd();
	}

	private static int TryReportSpoolDimensionPatternGaps(
		View view,
		IList<FabricationPart> parts,
		XYZ unitAxis,
		List<string> failureNotes)
	{
		SpoolTopologyFlags flags = DetectSpoolTopologyFlags(parts, view, unitAxis);
		string report = BuildSpoolPatternGapReport(flags);
		TryAppendAutoDimPlacementLog(view?.Name ?? "?", report.Replace(Environment.NewLine, " | "));
		int gaps = 0;
		foreach (SpoolDimensionPatternKind pattern in GetRequiredSpoolDimensionPatterns(flags))
		{
			if (!IsSpoolDimensionPatternLessonComplete(pattern))
			{
				gaps++;
				failureNotes?.Add("Step 1 gap: " + pattern + " — " + DescribeSpoolDimensionPattern(pattern));
			}
		}

		return gaps == 0 ? GetRequiredSpoolDimensionPatterns(flags).Count : 0;
	}

	private static SpoolDimensionPatternKind InferDimensionPattern(SpoolWitnessClassification w0, SpoolWitnessClassification w1)
	{
		if (w0.Letter == SpoolDimensionWitnessLetter.Disallowed || w1.Letter == SpoolDimensionWitnessLetter.Disallowed)
		{
			return SpoolDimensionPatternKind.Unknown;
		}

		bool hasOlet = w0.FittingKind == SpoolFittingKind.Olet || w1.FittingKind == SpoolFittingKind.Olet;
		bool hasFlange = w0.FittingKind == SpoolFittingKind.Flange || w1.FittingKind == SpoolFittingKind.Flange;
		bool hasCenterFitting = IsCenterlineFittingKind(w0.FittingKind) || IsCenterlineFittingKind(w1.FittingKind);
		bool hasPipe = w0.FittingKind == SpoolFittingKind.Pipe || w1.FittingKind == SpoolFittingKind.Pipe;

		if (hasOlet && WitnessPairContains(w0.Letter, w1.Letter, SpoolDimensionWitnessLetter.E, SpoolDimensionWitnessLetter.C))
		{
			return SpoolDimensionPatternKind.OletPickUp;
		}

		if (hasFlange && w0.Letter == SpoolDimensionWitnessLetter.F && w1.Letter == SpoolDimensionWitnessLetter.F)
		{
			return SpoolDimensionPatternKind.FlangeToFlange;
		}

		if (hasFlange && WitnessPairContains(w0.Letter, w1.Letter, SpoolDimensionWitnessLetter.F, SpoolDimensionWitnessLetter.C))
		{
			return SpoolDimensionPatternKind.CenterToFlange;
		}

		if (hasFlange && WitnessPairContains(w0.Letter, w1.Letter, SpoolDimensionWitnessLetter.F, SpoolDimensionWitnessLetter.E))
		{
			return SpoolDimensionPatternKind.FlangeToEnd;
		}

		if (hasCenterFitting && hasPipe && WitnessPairContains(w0.Letter, w1.Letter, SpoolDimensionWitnessLetter.C, SpoolDimensionWitnessLetter.E))
		{
			return w0.FittingKind == SpoolFittingKind.Tee || w1.FittingKind == SpoolFittingKind.Tee
				? SpoolDimensionPatternKind.TeeBranchStub
				: SpoolDimensionPatternKind.RunOverall;
		}

		if (hasCenterFitting && hasPipe && WitnessPairContains(w0.Letter, w1.Letter, SpoolDimensionWitnessLetter.E, SpoolDimensionWitnessLetter.C))
		{
			return SpoolDimensionPatternKind.VerticalDrop;
		}

		if (w0.Letter == SpoolDimensionWitnessLetter.C && w1.Letter == SpoolDimensionWitnessLetter.C)
		{
			return SpoolDimensionPatternKind.FittingToFitting;
		}

		return SpoolDimensionPatternKind.Unknown;
	}

	private static bool HasKindReferenceLesson(SpoolFittingKind kind)
	{
		return GetReferenceLessonsForKind(kind).Count > 0
			|| GetSurfaceReferenceLessonsForKind(kind).Count > 0;
	}

	private static bool HasPipeEndReferenceLesson()
	{
		return HasKindReferenceLesson(SpoolFittingKind.Pipe);
	}

	private static bool IsCenterlineFittingKind(SpoolFittingKind kind)
	{
		return kind == SpoolFittingKind.Elbow || kind == SpoolFittingKind.Tee;
	}

	private static bool WitnessPairContains(
		SpoolDimensionWitnessLetter a,
		SpoolDimensionWitnessLetter b,
		SpoolDimensionWitnessLetter x,
		SpoolDimensionWitnessLetter y)
	{
		return (a == x && b == y) || (a == y && b == x);
	}

	private static bool IsWeldWitnessPart(FabricationPart part)
	{
		return IsWeldPart(part);
	}

	private static string DescribeSpoolDimensionPattern(SpoolDimensionPatternKind pattern)
	{
		switch (pattern)
		{
		case SpoolDimensionPatternKind.OletPickUp:
			return "E→C from host pipe end to each olet/anvilet (stacked)";
		case SpoolDimensionPatternKind.RunOverall:
			return "Run chain: tee mid-run E→E overall + E→C + C→E, or E→last C overall + E→C + C→C with trailing fitting; else C→E at run terminus";
		case SpoolDimensionPatternKind.VerticalDrop:
			return "E→C pipe end to elbow center on drop leg";
		case SpoolDimensionPatternKind.TeeBranchStub:
			return "C→E tee center to branch pipe end";
		case SpoolDimensionPatternKind.FlangeToFlange:
			return "F→F across flange pair";
		case SpoolDimensionPatternKind.CenterToFlange:
			return "C→F or F→C fitting center to flange face";
		case SpoolDimensionPatternKind.FlangeToEnd:
			return "F→E flange to open pipe end";
		default:
			return pattern.ToString();
		}
	}
}
