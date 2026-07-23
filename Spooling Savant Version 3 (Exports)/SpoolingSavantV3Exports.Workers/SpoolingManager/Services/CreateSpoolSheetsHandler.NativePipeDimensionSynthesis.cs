using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Native pipe auto-dimensions for assembly views.
/// Pipe end faces are used when available. Fitting centerline points that have no
/// family center references are witnessed by a tiny, nearly-invisible detail curve
/// owned by the assembly view (reference planes measure correctly but do not display
/// in assembly views, so they cannot be used here).
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	private const string NativeInvisibleLineStyleName = "SS-Invisible";

	private static bool TryPlaceNativeReferenceDimension(
		Document doc,
		View view,
		Element witnessAElement,
		XYZ witnessAWorld,
		Element witnessBElement,
		XYZ witnessBWorld,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		out string failureDetail)
	{
		failureDetail = null;
		if (doc == null || view == null || witnessAElement == null || witnessBElement == null
			|| witnessAWorld == null || witnessBWorld == null)
		{
			failureDetail = "Missing document, view, elements, or witness points.";
			return false;
		}
		if (!TryGetViewSketchPlane(view, out XYZ planeOrigin, out XYZ planeNormal))
		{
			failureDetail = "Could not resolve view sketch plane.";
			return false;
		}
		XYZ vn = planeNormal.Normalize();
		XYZ a = ProjectToSketchPlane(witnessAWorld, planeOrigin, vn);
		XYZ b = ProjectToSketchPlane(witnessBWorld, planeOrigin, vn);
		XYZ chord = b - a;
		if (chord.GetLength() < 1.0 / 24.0)
		{
			failureDetail = "Witness points project too close together in the view plane.";
			return false;
		}
		if (!TryGetViewPlaneAxes(view, out _, out XYZ right, out XYZ up))
		{
			failureDetail = "Could not resolve view plane axes.";
			return false;
		}

		double deltaRight = chord.DotProduct(right);
		double deltaUp = chord.DotProduct(up);
		bool horizontal = Math.Abs(deltaRight) >= Math.Abs(deltaUp);
		XYZ runDir = horizontal
			? (deltaRight >= 0.0 ? right : right.Negate())
			: (deltaUp >= 0.0 ? up : up.Negate());
		double expectedSpan = horizontal ? Math.Abs(deltaRight) : Math.Abs(deltaUp);
		XYZ perpDir = vn.CrossProduct(runDir);
		if (expectedSpan < 1.0 / 24.0 || perpDir.GetLength() < 1E-09)
		{
			failureDetail = "Could not resolve an axis-aligned native dimension span.";
			return false;
		}
		perpDir = perpDir.Normalize();

		List<Reference> refsA = CollectNativeWitnessReferences(witnessAElement, witnessAWorld, runDir);
		List<Reference> refsB = CollectNativeWitnessReferences(witnessBElement, witnessBWorld, runDir.Negate());

		// Fitting families commonly expose zero center references. In assembly views a
		// ReferencePlane can receive a dimension that measures correctly but never draws,
		// so we place a tiny detail curve at the centerline point instead and leave it
		// unhidden (hiding the curve also hides the dimension).
		List<ElementId> createdHelpers = new List<ElementId>();
		if (refsA.Count == 0)
		{
			Reference helperRef = TryCreateNativeCenterlineDetailReference(
				doc, view, a, perpDir, createdHelpers);
			if (helperRef != null)
			{
				refsA.Add(helperRef);
			}
		}
		if (refsB.Count == 0)
		{
			Reference helperRef = TryCreateNativeCenterlineDetailReference(
				doc, view, b, perpDir, createdHelpers);
			if (helperRef != null)
			{
				refsB.Add(helperRef);
			}
		}
		if (refsA.Count == 0 || refsB.Count == 0)
		{
			CleanupNativeHelperElements(doc, createdHelpers);
			failureDetail = "Native witness references were not found (pipe end refs="
				+ refsA.Count + ", fitting/pipe center refs=" + refsB.Count
				+ ") and a centerline detail witness could not be created.";
			return false;
		}
		if (createdHelpers.Count > 0)
		{
			try
			{
				RegenTracked(doc);
				FlushPendingRegen(doc);
			}
			catch
			{
			}
		}

		DimensionType dimType = TryResolveLinearDimensionType(doc, spoolSettings);
		bool hasAnnotations = spoolSettings?.AutoDimAnnotations == true;
		string lastFailure = null;
		foreach (XYZ offsetDirection in new[] { perpDir.Negate(), perpDir })
		{
			double offset = ResolveSpoolLinearDimensionModelOffset(
				view, dimType, stackIndex, horizontal, hasAnnotations);
			if (!TryBuildStackedLinearDimensionLine(view, a, b, offset, offsetDirection, out Line dimLine)
				|| dimLine == null)
			{
				lastFailure = "Could not build the dimension line in the view plane.";
				continue;
			}
			if (!TryValidateLinearOnlyDimensionLine(view, dimLine, out string lineDiagnostic))
			{
				lastFailure = lineDiagnostic;
				continue;
			}

			foreach (Reference refA in refsA)
			{
				foreach (Reference refB in refsB)
				{
					if (AreSameDimensionReference(doc, refA, refB))
					{
						continue;
					}
					Dimension placed = null;
					try
					{
						ReferenceArray references = new ReferenceArray();
						references.Append(refA);
						references.Append(refB);
						placed = dimType != null
							? doc.Create.NewDimension(view, dimLine, references, dimType)
							: doc.Create.NewDimension(view, dimLine, references);
					}
					catch (Exception ex)
					{
						lastFailure = "Native dimension failed: " + ex.Message;
						continue;
					}
					if (placed == null)
					{
						lastFailure = "Native dimension returned null.";
						continue;
					}

					double measured;
					try
					{
						measured = placed.Value ?? 0.0;
					}
					catch
					{
						measured = 0.0;
					}
					double tolerance = Math.Max(0.03, expectedSpan * 0.01);
					if (Math.Abs(measured - expectedSpan) <= tolerance)
					{
						// Leave helpers: they are near-invisible and must stay visible for
						// the dimension to remain visible in the assembly view.
						stackIndex++;
						return true;
					}
					try
					{
						doc.Delete(placed.Id);
					}
					catch
					{
					}
					lastFailure = "Native dimension measured "
						+ measured.ToString("0.###") + " ft, expected "
						+ expectedSpan.ToString("0.###") + " ft.";
				}
			}
		}

		CleanupNativeHelperElements(doc, createdHelpers);
		failureDetail = lastFailure ?? "No compatible native witness pair was found.";
		return false;
	}

	/// <summary>
	/// Tiny detail curve through the centerline witness point, perpendicular to the run.
	/// Owned by the assembly view so the dimension can actually display.
	/// </summary>
	private static Reference TryCreateNativeCenterlineDetailReference(
		Document doc,
		View view,
		XYZ pointInPlane,
		XYZ perpDir,
		List<ElementId> createdHelpers)
	{
		try
		{
			double halfLength = Math.Max(doc.Application.ShortCurveTolerance * 1.5, 1.0 / 256.0) * 0.5;
			XYZ p0 = pointInPlane - perpDir.Multiply(halfLength);
			XYZ p1 = pointInPlane + perpDir.Multiply(halfLength);
			Line curve = Line.CreateBound(p0, p1);
			DetailCurve detail = doc.Create.NewDetailCurve(view, curve);
			if (detail == null)
			{
				return null;
			}
			createdHelpers.Add(detail.Id);
			TryApplyNativeInvisibleLineStyle(doc, detail);
			return new Reference(detail);
		}
		catch
		{
			return null;
		}
	}

	private static void TryApplyNativeInvisibleLineStyle(Document doc, DetailCurve detail)
	{
		if (doc == null || detail == null)
		{
			return;
		}
		try
		{
			GraphicsStyle style = EnsureNativeInvisibleLineStyle(doc);
			if (style != null)
			{
				detail.LineStyle = style;
			}
		}
		catch
		{
		}
	}

	private static GraphicsStyle EnsureNativeInvisibleLineStyle(Document doc)
	{
		Categories categories = doc.Settings.Categories;
		Category linesCategory = categories.get_Item(BuiltInCategory.OST_Lines);
		if (linesCategory == null)
		{
			return null;
		}
		Category sub = null;
		foreach (Category candidate in linesCategory.SubCategories)
		{
			if (candidate != null
				&& string.Equals(candidate.Name, NativeInvisibleLineStyleName, StringComparison.OrdinalIgnoreCase))
			{
				sub = candidate;
				break;
			}
		}
		if (sub == null)
		{
			sub = categories.NewSubcategory(linesCategory, NativeInvisibleLineStyleName);
			sub.LineColor = new Color(255, 255, 255);
			try
			{
				sub.SetLineWeight(1, GraphicsStyleType.Projection);
			}
			catch
			{
			}
		}
		return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
	}

	private static void CleanupNativeHelperElements(Document doc, List<ElementId> helpers)
	{
		if (doc == null || helpers == null)
		{
			return;
		}
		foreach (ElementId id in helpers)
		{
			if (id == null || id == ElementId.InvalidElementId)
			{
				continue;
			}
			try
			{
				doc.Delete(id);
			}
			catch
			{
			}
		}
	}

	private static List<Reference> CollectNativeWitnessReferences(
		Element element,
		XYZ targetWorld,
		XYZ runDirection)
	{
		if (element == null || targetWorld == null)
		{
			return new List<Reference>();
		}
		if (NativePipeSpoolSupport.IsNativePipe(element))
		{
			return CollectNativePipeEndFaceReferences(element, targetWorld, runDirection);
		}
		if (element is FamilyInstance familyInstance)
		{
			return CollectNativeFamilyCenterReferences(familyInstance);
		}
		return new List<Reference>();
	}

	/// <summary>
	/// Only the two family center planes are valid C witnesses. Left/Front face references
	/// are intentionally excluded because they dimension to the fitting edge, not centerline.
	/// </summary>
	private static List<Reference> CollectNativeFamilyCenterReferences(FamilyInstance instance)
	{
		List<Reference> result = new List<Reference>();
		if (instance == null)
		{
			return result;
		}
		foreach (FamilyInstanceReferenceType referenceType in new[]
		{
			FamilyInstanceReferenceType.CenterLeftRight,
			FamilyInstanceReferenceType.CenterFrontBack,
			FamilyInstanceReferenceType.CenterElevation
		})
		{
			try
			{
				IList<Reference> references = instance.GetReferences(referenceType);
				if (references == null)
				{
					continue;
				}
				foreach (Reference reference in references)
				{
					AddUniqueNativeReference(instance.Document, result, reference);
				}
			}
			catch
			{
			}
		}
		return result;
	}

	/// <summary>Find the planar pipe cap face nearest the requested connector endpoint.</summary>
	private static List<Reference> CollectNativePipeEndFaceReferences(
		Element pipe,
		XYZ targetWorld,
		XYZ runDirection)
	{
		List<(Reference Reference, double Score)> ranked = new List<(Reference, double)>();
		if (pipe == null || targetWorld == null)
		{
			return new List<Reference>();
		}
		XYZ axis = runDirection != null && runDirection.GetLength() > 1E-09
			? runDirection.Normalize()
			: null;
		try
		{
			Options options = new Options
			{
				ComputeReferences = true,
				IncludeNonVisibleObjects = true,
				DetailLevel = ViewDetailLevel.Fine
			};
			GeometryElement geometry = pipe.get_Geometry(options);
			if (geometry != null)
			{
				CollectNativePipePlanarFaces(geometry, Transform.Identity, targetWorld, axis, ranked);
			}
		}
		catch
		{
		}
		List<Reference> result = new List<Reference>();
		foreach ((Reference reference, double _) in ranked.OrderBy(item => item.Score))
		{
			AddUniqueNativeReference(pipe.Document, result, reference);
		}
		return result.Take(8).ToList();
	}

	private static void CollectNativePipePlanarFaces(
		GeometryElement geometry,
		Transform transform,
		XYZ targetWorld,
		XYZ axis,
		List<(Reference Reference, double Score)> ranked)
	{
		foreach (GeometryObject geometryObject in geometry)
		{
			if (geometryObject is GeometryInstance instance)
			{
				GeometryElement nested = null;
				try
				{
					nested = instance.GetInstanceGeometry();
				}
				catch
				{
				}
				if (nested != null)
				{
					CollectNativePipePlanarFaces(
						nested,
						transform.Multiply(instance.Transform),
						targetWorld,
						axis,
						ranked);
				}
				continue;
			}
			if (!(geometryObject is Solid solid) || solid.Faces == null)
			{
				continue;
			}
			foreach (Face face in solid.Faces)
			{
				if (!(face is PlanarFace planarFace) || planarFace.Reference == null)
				{
					continue;
				}
				XYZ origin = transform.OfPoint(planarFace.Origin);
				XYZ normal = transform.OfVector(planarFace.FaceNormal).Normalize();
				double alignmentPenalty = axis == null
					? 0.0
					: (1.0 - Math.Abs(normal.DotProduct(axis))) * 100.0;
				double score = origin.DistanceTo(targetWorld) + alignmentPenalty;
				if (alignmentPenalty < 5.0)
				{
					ranked.Add((planarFace.Reference, score));
				}
			}
		}
	}

	private static void AddUniqueNativeReference(
		Document doc,
		List<Reference> references,
		Reference candidate)
	{
		if (doc == null || candidate == null)
		{
			return;
		}
		string stable;
		try
		{
			stable = candidate.ConvertToStableRepresentation(doc);
		}
		catch
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(stable))
		{
			return;
		}
		foreach (Reference existing in references)
		{
			try
			{
				if (string.Equals(
					existing.ConvertToStableRepresentation(doc),
					stable,
					StringComparison.Ordinal))
				{
					return;
				}
			}
			catch
			{
			}
		}
		references.Add(candidate);
	}
}
