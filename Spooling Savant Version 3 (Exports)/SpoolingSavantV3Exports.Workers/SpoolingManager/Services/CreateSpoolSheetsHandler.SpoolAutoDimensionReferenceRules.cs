using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Step 2 — global fitting reference rules and Step 1 stable-representation map.
/// UNIVERSAL ORIGIN RULES (never vary by spool):
/// - Elbow C = connector-ray intersection, else mated-pipe-axis intersection — never connector origin or instance origin.
/// - Tee C = same as elbow.
/// - Pipe E = open end connector on the run (walk through welds); never fitting center.
/// - Flange F = raised face at run; gasket neighbor excluded.
/// - Olet E-C = ALL pick-ups on a host run share ONE short-side pipe open end; stack outward closest-first.
/// Reference lessons are view-invariant: Front, Back, Left, Right, and Top all use this same table.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	public enum DimensionOriginRule
	{
		ConnectorOrigin,
		CenterlineIntersection,
		HostFaceExcludeGasket,
		BranchToShortSide
	}

	public enum SpoolFittingKind
	{
		Unknown,
		Pipe,
		Elbow,
		Tee,
		Flange,
		Olet,
		Valve,
		Weld,
		Other
	}

	/// <summary>Step 1 lesson row: fabrication instance curve index from manual Linear dims.</summary>
	public readonly struct SpoolReferenceLesson
	{
		public SpoolReferenceLesson(SpoolFittingKind kind, int instanceCurveIndex, string notes = null, string geometryGuidPrefix = null)
		{
			Kind = kind;
			InstanceCurveIndex = instanceCurveIndex;
			Notes = notes ?? string.Empty;
			GeometryGuidPrefix = geometryGuidPrefix ?? string.Empty;
		}

		public SpoolFittingKind Kind { get; }
		public int InstanceCurveIndex { get; }
		public string Notes { get; }
		public string GeometryGuidPrefix { get; }
	}

	/// <summary>Step 1 lesson row: fabrication instance face surface index (stable rep ends with :N:SURFACE).</summary>
	public readonly struct SpoolSurfaceReferenceLesson
	{
		public SpoolSurfaceReferenceLesson(SpoolFittingKind kind, int surfaceIndex, string notes = null)
		{
			Kind = kind;
			SurfaceIndex = surfaceIndex;
			Notes = notes ?? string.Empty;
		}

		public SpoolFittingKind Kind { get; }
		public int SurfaceIndex { get; }
		public string Notes { get; }
	}

	private static readonly IReadOnlyDictionary<string, DimensionOriginRule> FittingRules =
		new Dictionary<string, DimensionOriginRule>(StringComparer.OrdinalIgnoreCase)
		{
			{ "Pipe", DimensionOriginRule.ConnectorOrigin },
			{ "Elbow", DimensionOriginRule.CenterlineIntersection },
			{ "Tee", DimensionOriginRule.CenterlineIntersection },
			{ "Flange", DimensionOriginRule.HostFaceExcludeGasket },
			{ "Olet", DimensionOriginRule.BranchToShortSide },
		};

	/// <summary>Curve indices by fitting kind (universal — not tied to any single element id).</summary>
	private static readonly IReadOnlyList<SpoolReferenceLesson> ReferenceLessons = new List<SpoolReferenceLesson>
	{
		new SpoolReferenceLesson(SpoolFittingKind.Elbow, 10, "Elbow 90 SR/LR centerline intersection (C)", "cbaa81fa-e576-4340-9c59-ef0f6e49aee0"),
		new SpoolReferenceLesson(SpoolFittingKind.Tee, 16, "Tee Equal / Tee Reducing centerline intersection (C)"),
		new SpoolReferenceLesson(SpoolFittingKind.Flange, 9, "Weld Neck Flange STD FF 150# face (F)", "692c5042-7a5c-467d-8d83-d0792c69208d"),
		new SpoolReferenceLesson(SpoolFittingKind.Flange, 8, "Weld Neck Flange STD FF 150# face (F)", "692c5042-7a5c-467d-8d83-d0792c69208d"),
		new SpoolReferenceLesson(SpoolFittingKind.Pipe, 10, "Branch stub / small pipe open end curve (E)", "f74ba971-bcb4-4748-96a3-99365e15b97a"),
		new SpoolReferenceLesson(SpoolFittingKind.Pipe, 3, "6'' pipe open end curve (E)"),
		new SpoolReferenceLesson(SpoolFittingKind.Pipe, 6, "6'' pipe open end curve (E)"),
		new SpoolReferenceLesson(SpoolFittingKind.Pipe, 8, "6'' pipe end curve at fitting (E)"),
		new SpoolReferenceLesson(SpoolFittingKind.Pipe, 4, "4'' pipe open end curve (E)"),
		new SpoolReferenceLesson(SpoolFittingKind.Pipe, 7, "4'' pipe end curve at tee (E)"),
		new SpoolReferenceLesson(SpoolFittingKind.Pipe, 10, "4'' Sch 40 pipe end at tee / run terminus (E)", "692c5042-7a5c-467d-8d83-d0792c69208d"),
		new SpoolReferenceLesson(SpoolFittingKind.Olet, 12, "Anvilets - 3000 (FPT) body center (C)", "7601b306-2ca3-4a88-8359-b0b3a0cbe1b4"),
		new SpoolReferenceLesson(SpoolFittingKind.Olet, 12, "Anvilets - 3000 (FPT) body center (C)", "f6278889-e255-4845-85f9-e5dabf61e2f4"),
		new SpoolReferenceLesson(SpoolFittingKind.Olet, 10, "Anvilets (BW) body center (C)", "f74ba971-bcb4-4748-96a3-99365e15b97a"),
	};

	private static readonly IReadOnlyList<SpoolSurfaceReferenceLesson> SurfaceReferenceLessons = new List<SpoolSurfaceReferenceLesson>
	{
		new SpoolSurfaceReferenceLesson(SpoolFittingKind.Flange, 13, "Weld Neck Flange STD FF 150# face (F)"),
		new SpoolSurfaceReferenceLesson(SpoolFittingKind.Pipe, 14, "6'' pipe open end face — horizontal run terminus (E)"),
		new SpoolSurfaceReferenceLesson(SpoolFittingKind.Pipe, 11, "6'' pipe open end face — vertical drop terminus (E)"),
		new SpoolSurfaceReferenceLesson(SpoolFittingKind.Pipe, 14, "4'' pipe open end face (E)"),
	};

	private static readonly HashSet<SpoolFittingKind> UnmappedKindsRequiringStep1 = new HashSet<SpoolFittingKind>();

	public static SpoolFittingKind ClassifyFabricationPart(FabricationPart part, Document doc = null)
	{
		if (part == null)
		{
			return SpoolFittingKind.Unknown;
		}

		if (FabricationPartClassification.IsValvePart(part, doc))
		{
			return SpoolFittingKind.Valve;
		}

		if (FabricationPartClassification.IsFlangePart(part, doc))
		{
			return SpoolFittingKind.Flange;
		}

		if (FabricationPartClassification.IsOletPart(part))
		{
			return SpoolFittingKind.Olet;
		}

		if (FabricationPartClassification.IsElbowPart(part, doc))
		{
			return SpoolFittingKind.Elbow;
		}

		if (FabricationPartClassification.IsTeePart(part, doc))
		{
			return SpoolFittingKind.Tee;
		}

		if (FabricationPartClassification.IsWeldPart(part))
		{
			return SpoolFittingKind.Weld;
		}

		if (FabricationPartClassification.IsStraightPipeRun(part))
		{
			return SpoolFittingKind.Pipe;
		}

		return SpoolFittingKind.Other;
	}

	public static bool TryGetDimensionOriginRule(SpoolFittingKind kind, out DimensionOriginRule rule)
	{
		string key = kind switch
		{
			SpoolFittingKind.Pipe => "Pipe",
			SpoolFittingKind.Elbow => "Elbow",
			SpoolFittingKind.Tee => "Tee",
			SpoolFittingKind.Flange => "Flange",
			SpoolFittingKind.Olet => "Olet",
			_ => null
		};

		if (key != null && FittingRules.TryGetValue(key, out rule))
		{
			return true;
		}

		rule = default;
		return false;
	}

	public static bool IsFittingKindMappedForAutoDim(SpoolFittingKind kind)
	{
		return !UnmappedKindsRequiringStep1.Contains(kind);
	}

	public static bool RequiresStep1Lesson(SpoolFittingKind kind)
	{
		return UnmappedKindsRequiringStep1.Contains(kind);
	}

	public static IReadOnlyList<SpoolReferenceLesson> GetReferenceLessonsForKind(SpoolFittingKind kind)
	{
		return ReferenceLessons.Where((lesson) => lesson.Kind == kind).ToList();
	}

	public static IReadOnlyList<SpoolSurfaceReferenceLesson> GetSurfaceReferenceLessonsForKind(SpoolFittingKind kind)
	{
		return SurfaceReferenceLessons.Where((lesson) => lesson.Kind == kind).ToList();
	}

	/// <summary>
	/// Resolve a dimension witness reference for a fabrication part using Step 2 rules.
	/// Returns false when the fitting type is unmapped (Tee) or no lesson index matches.
	/// </summary>
	public static bool TryResolveFabricationOriginReference(
		Document doc,
		View view,
		FabricationPart part,
		out Reference reference,
		out string diagnostic,
		SpoolFittingKind? kindOverride = null)
	{
		reference = null;
		diagnostic = string.Empty;
		if (doc == null || part == null)
		{
			diagnostic = "Missing document or part.";
			return false;
		}

		SpoolFittingKind kind = kindOverride ?? ClassifyFabricationPart(part, doc);
		if (RequiresStep1Lesson(kind))
		{
			diagnostic = "Fitting kind '" + kind + "' has no Step 1 stable-representation lesson yet.";
			return false;
		}

		if (!TryGetDimensionOriginRule(kind, out DimensionOriginRule rule))
		{
			if (kind == SpoolFittingKind.Weld)
			{
				return TryResolveLessonCurveReference(doc, view, part, kind, out reference, out diagnostic);
			}

			diagnostic = "No DimensionOriginRule for kind '" + kind + "'.";
			return false;
		}

		switch (rule)
		{
		case DimensionOriginRule.ConnectorOrigin:
			return TryResolveConnectorOriginReference(part, out reference, out diagnostic);
		case DimensionOriginRule.CenterlineIntersection:
			return TryResolveLessonCurveReference(doc, view, part, kind, out reference, out diagnostic);
		case DimensionOriginRule.HostFaceExcludeGasket:
			return TryResolveFlangeFaceReference(doc, view, part, out reference, out diagnostic);
		case DimensionOriginRule.BranchToShortSide:
			return TryResolveOletBodyReference(doc, view, part, out reference, out diagnostic);
		default:
			diagnostic = "Unhandled DimensionOriginRule: " + rule;
			return false;
		}
	}

	private static bool TryResolveConnectorOriginReference(FabricationPart part, out Reference reference, out string diagnostic)
	{
		reference = null;
		diagnostic = string.Empty;
		Document doc = part?.Document;
		View view = doc?.ActiveView;
		if (doc == null || view == null)
		{
			diagnostic = "No active view for pipe open-end reference.";
			return false;
		}

		foreach (SpoolSurfaceReferenceLesson surfaceLesson in GetSurfaceReferenceLessonsForKind(SpoolFittingKind.Pipe))
		{
			if (TryGetFabricationInstanceSurfaceReferenceByIndex((Element)part, view, surfaceLesson.SurfaceIndex, out reference)
				&& !IsDisallowedFlangeWitnessReference(doc, reference))
			{
				diagnostic = "ConnectorOrigin via Step 1 pipe face surface index " + surfaceLesson.SurfaceIndex + ".";
				return true;
			}
		}

		foreach (SpoolReferenceLesson lesson in GetReferenceLessonsForKind(SpoolFittingKind.Pipe))
		{
			if (TryGetFabricationInstanceCurveReferenceByIndex((Element)part, view, lesson.InstanceCurveIndex, out reference))
			{
				diagnostic = "ConnectorOrigin via Step 1 pipe curve index " + lesson.InstanceCurveIndex + ".";
				return true;
			}
		}

		diagnostic = "Could not resolve pipe open-end reference from Step 1 pipe lessons.";
		return false;
	}

	private static bool TryResolveLessonCurveReference(
		Document doc,
		View view,
		FabricationPart part,
		SpoolFittingKind kind,
		out Reference reference,
		out string diagnostic)
	{
		reference = null;
		diagnostic = string.Empty;
		List<SpoolReferenceLesson> lessons = GetReferenceLessonsForKind(kind).ToList();
		if (lessons.Count == 0)
		{
			diagnostic = "No Step 1 lesson curve index for kind '" + kind + "'.";
			return false;
		}

		foreach (SpoolReferenceLesson lesson in lessons)
		{
			if (TryGetFabricationInstanceCurveReferenceByIndex((Element)part, view, lesson.InstanceCurveIndex, out reference))
			{
				diagnostic = "CenterlineIntersection curve index " + lesson.InstanceCurveIndex + " (" + lesson.Notes + ").";
				return true;
			}
		}

		diagnostic = "Could not resolve any lesson curve index for kind '" + kind + "'.";
		return false;
	}

	private static bool TryResolveFlangeFaceReference(Document doc, View view, FabricationPart part, out Reference reference, out string diagnostic)
	{
		reference = null;
		diagnostic = string.Empty;
		foreach (SpoolSurfaceReferenceLesson lesson in GetSurfaceReferenceLessonsForKind(SpoolFittingKind.Flange))
		{
			if (!TryGetFabricationInstanceSurfaceReferenceByIndex((Element)part, view, lesson.SurfaceIndex, out reference))
			{
				continue;
			}

			if (IsDisallowedFlangeWitnessReference(doc, reference))
			{
				reference = null;
				diagnostic = "Resolved surface index " + lesson.SurfaceIndex + " but reference is gasket/accessory — rejected.";
				continue;
			}

			diagnostic = "HostFaceExcludeGasket surface index " + lesson.SurfaceIndex + " (" + lesson.Notes + ").";
			return true;
		}

		foreach (SpoolReferenceLesson lesson in GetReferenceLessonsForKind(SpoolFittingKind.Flange))
		{
			if (!TryGetFabricationInstanceCurveReferenceByIndex((Element)part, view, lesson.InstanceCurveIndex, out reference))
			{
				continue;
			}

			if (IsDisallowedFlangeWitnessReference(doc, reference))
			{
				reference = null;
				diagnostic = "Resolved flange curve index " + lesson.InstanceCurveIndex + " but reference is gasket/accessory — rejected.";
				continue;
			}

			diagnostic = "HostFaceExcludeGasket flange curve index " + lesson.InstanceCurveIndex + " (" + lesson.Notes + ").";
			return true;
		}

		diagnostic = "Could not resolve flange face from Step 1 lessons (surface :13:SURFACE or curves :9/:8).";
		return false;
	}

	private static bool TryResolveOletBodyReference(Document doc, View view, FabricationPart part, out Reference reference, out string diagnostic)
	{
		reference = null;
		diagnostic = string.Empty;
		List<SpoolReferenceLesson> lessons = GetReferenceLessonsForKind(SpoolFittingKind.Olet).ToList();
		string corpus = string.Join(" ",
			part.Name ?? string.Empty,
			GetPartParameterValue((Element)part, "Product Entry"),
			GetPartParameterValue((Element)part, "Product Long Description")).ToUpperInvariant();
		IEnumerable<SpoolReferenceLesson> ordered = lessons;
		if (corpus.Contains("FPT") || corpus.Contains("3000"))
		{
			ordered = lessons.OrderByDescending((lesson) => lesson.InstanceCurveIndex == 12);
		}
		else if (corpus.Contains("BW"))
		{
			ordered = lessons.OrderByDescending((lesson) => lesson.InstanceCurveIndex == 10 && string.IsNullOrEmpty(lesson.GeometryGuidPrefix));
		}

		foreach (SpoolReferenceLesson lesson in ordered)
		{
			if (TryGetFabricationInstanceCurveReferenceByIndex((Element)part, view, lesson.InstanceCurveIndex, out reference))
			{
				diagnostic = "Olet body curve index " + lesson.InstanceCurveIndex + " (" + lesson.Notes + ").";
				return true;
			}
		}

		diagnostic = "Could not resolve olet body reference from Step 1 lessons.";
		return false;
	}

	/// <summary>Hard filter: never dimension to gasket or generic pipe accessory faces (flange rule).</summary>
	public static bool IsDisallowedFlangeWitnessReference(Document doc, Reference reference)
	{
		if (doc == null || reference == null)
		{
			return false;
		}

		Element element = doc.GetElement(reference.ElementId);
		if (element == null)
		{
			return false;
		}

		Category category = element.Category;
		if (category == null)
		{
			return false;
		}

		BuiltInCategory builtIn = (BuiltInCategory)category.Id.Value;
		if (builtIn == BuiltInCategory.OST_PipeAccessory)
		{
			return true;
		}

		FabricationPart fabricationPart = element as FabricationPart;
		if (fabricationPart != null && FabricationPartClassification.IsGasketPart(fabricationPart))
		{
			return true;
		}

		string name = element.Name ?? string.Empty;
		return name.IndexOf("gasket", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool TryGetFabricationInstanceCurveReferenceByIndex(Element element, View view, int curveIndex, out Reference reference)
	{
		reference = null;
		foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(element, view))
		{
			if (item.Reference == null)
			{
				continue;
			}

			if (!TryParseInstanceCurveIndex(element.Document, item.Reference, out int index) || index != curveIndex)
			{
				continue;
			}

			reference = item.Reference;
			return true;
		}

		return false;
	}

	private static bool TryGetFabricationInstanceSurfaceReferenceByIndex(Element element, View view, int surfaceIndex, out Reference reference)
	{
		reference = null;
		Document doc = element?.Document;
		if (element == null || doc == null || surfaceIndex < 0)
		{
			return false;
		}

		string elementStable;
		try
		{
			elementStable = new Reference(element).ConvertToStableRepresentation(doc);
		}
		catch
		{
			return false;
		}

		HashSet<string> symbolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Options options in BuildDimensionGeometryOptions(view))
		{
			GeometryElement geo = null;
			try
			{
				geo = element.get_Geometry(options);
			}
			catch
			{
				geo = null;
			}

			if (geo != null)
			{
				CollectFabricationSymbolIdsFromGeometry(geo, doc, symbolIds);
			}
		}

		foreach (string symbolId in symbolIds)
		{
			string trialStable = elementStable + ":0:INSTANCE:" + symbolId + ":" + surfaceIndex + ":SURFACE";
			Reference parsedRef;
			try
			{
				parsedRef = Reference.ParseFromStableRepresentation(doc, trialStable);
			}
			catch
			{
				continue;
			}

			if (parsedRef == null)
			{
				continue;
			}

			try
			{
				GeometryObject geoObj = element.GetGeometryObjectFromReference(parsedRef);
				if (geoObj is Face)
				{
					reference = parsedRef;
					return true;
				}
			}
			catch
			{
			}
		}

		return false;
	}

	private static bool TryParseInstanceCurveIndex(Document doc, Reference reference, out int curveIndex)
	{
		curveIndex = -1;
		if (doc == null || reference == null)
		{
			return false;
		}

		string stable;
		try
		{
			stable = reference.ConvertToStableRepresentation(doc);
		}
		catch
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(stable))
		{
			return false;
		}

		int lastColon = stable.LastIndexOf(':');
		if (lastColon < 0 || lastColon >= stable.Length - 1)
		{
			return false;
		}

		return int.TryParse(stable.Substring(lastColon + 1), out curveIndex);
	}
}
