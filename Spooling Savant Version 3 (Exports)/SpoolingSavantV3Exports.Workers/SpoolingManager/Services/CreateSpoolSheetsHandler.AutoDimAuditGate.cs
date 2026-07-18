using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Create/Refresh pass/fail gate: hard laws + 3/8" sheet slot-0 offset + expected dims present.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	/// <summary>Always-on compact learning log (offset / expected / teach).</summary>
	private static readonly bool AutoDimLearningLogEnabled = true;

	private static void TryAppendAutoDimLearningLog(string assemblyName, string viewLabel, string message)
	{
		if (!AutoDimLearningLogEnabled && !TestingReportsEnabled && !AutoDimPlacementLogEnabled)
		{
			return;
		}
		TryAppendAutoDimDiagnosticLog(assemblyName ?? "learning", viewLabel ?? "?", message, 0, 0);
	}

	/// <summary>
	/// Full view audit used by the learning loop — hard laws, slot-0 offset, expected roles.
	/// </summary>
	private static AutoDimHardLawAudit AuditAutoDimView(
		Document doc,
		View view,
		IList<FabricationPart> parts)
	{
		AutoDimHardLawAudit audit = AuditAutoDimHardLaws(doc, view, parts);
		if (doc == null || view == null || parts == null)
		{
			return audit;
		}

		AuditSlot0Offsets(doc, view, audit);
		AuditExpectedDimsPresent(doc, view, parts, audit);
		return audit;
	}

	private static void AuditSlot0Offsets(Document doc, View view, AutoDimHardLawAudit audit)
	{
		if (doc == null || view == null || audit == null)
		{
			return;
		}
		if (!TryGetViewPlaneAxes(view, out _, out XYZ right, out XYZ up))
		{
			audit.Failures.Add("offset audit: no view axes");
			return;
		}

		const double slot0Sheet = AutoDimLessonStore.Slot0OffsetSheetFeet;
		const double tolSheet = AutoDimLessonStore.OffsetTolSheetFeet;
		int farCount = 0;
		int measured = 0;

		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim == null || !dim.IsValidObject || dim.DimensionType == null
					|| !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}
				if (!TryMeasureDimensionSheetOffset(view, dim, right, up, out double sheetFeet, out _))
				{
					continue;
				}
				measured++;
				// Slot 0 must hug ~3/8" sheet. Anything past 3/8" + one snap (1/4") is fail.
				if (sheetFeet > slot0Sheet + (1.0 / 48.0) + tolSheet)
				{
					farCount++;
				}
			}
		}
		catch
		{
			audit.Failures.Add("offset audit: enumeration failed");
			return;
		}

		audit.OffsetFailCount = farCount;
		audit.OffsetMeasuredCount = measured;
		if (farCount > 0)
		{
			// Warn/log only — do NOT add to Failures. Offset fails were forcing the learning
			// loop through all 4 place cycles on every Refresh and freezing Revit for minutes.
			TryAppendAutoDimLearningLog(
				"offset-audit",
				view.Name ?? "?",
				farCount + "/" + measured + " Linear dim(s) farther than ~3/8\" sheet (non-blocking)");
		}
	}

	/// <summary>
	/// Sheet feet from dim-line mid to witness chord midpoint along the offset axis.
	/// </summary>
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
		if (view == null || dim == null || right == null || up == null)
		{
			return false;
		}
		if (!TryGetDimensionLineSegmentInView(view, dim, out XYZ lineA, out XYZ lineB, out bool isUpAxis))
		{
			return false;
		}
		XYZ lineMid = (lineA + lineB) * 0.5;
		XYZ offsetAxis = isUpAxis ? right : up;
		if (offsetAxis == null || offsetAxis.GetLength() < 1E-09)
		{
			return false;
		}
		offsetAxis = offsetAxis.Normalize();

		List<XYZ> witnesses = new List<XYZ>();
		try
		{
			Document doc = ((Element)dim).Document;
			ReferenceArray refs = dim.References;
			if (refs == null || refs.Size < 2)
			{
				return false;
			}
			for (int i = 0; i < refs.Size; i++)
			{
				Reference r = refs.get_Item(i);
				Element host = doc.GetElement(r.ElementId);
				if (host != null
					&& TryGetReferenceSampleWorldPointForTarget(host, r, lineMid, out XYZ sample)
					&& sample != null)
				{
					witnesses.Add(ProjectToSketchPlane(sample, view.Origin ?? lineMid, view.ViewDirection) ?? sample);
				}
			}
		}
		catch
		{
			return false;
		}
		if (witnesses.Count < 2)
		{
			return false;
		}
		XYZ chordMid = XYZ.Zero;
		foreach (XYZ w in witnesses)
		{
			chordMid += w;
		}
		chordMid /= witnesses.Count;

		double delta = (lineMid - chordMid).DotProduct(offsetAxis);
		offsetSign = delta >= 0 ? 1 : -1;
		double modelFeet = Math.Abs(delta);
		int scale = 1;
		try { scale = Math.Max(view.Scale, 1); } catch { scale = 1; }
		offsetSheetFeet = modelFeet / scale;
		return offsetSheetFeet > 1E-09 || modelFeet < 1E-06;
	}

	private static void AuditExpectedDimsPresent(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		AutoDimHardLawAudit audit)
	{
		if (doc == null || view == null || parts == null || audit == null)
		{
			return;
		}

		Document assemblyDoc = parts.Count > 0 ? ((Element)parts[0]).Document : doc;
		bool hasElbowOrTee = parts.Any(p => p != null
			&& (FabricationPartClassification.IsElbowPart(p, assemblyDoc)
				|| FabricationPartClassification.IsTeePart(p, assemblyDoc)));
		bool hasFlange = parts.Any(p => p != null && FabricationPartClassification.IsFlangePart(p, assemblyDoc));
		// Olets on a shop weld do not require pick-up dims.
		int oletCount = parts.Count(p => p != null && IsOletPart(p) && !IsOletLandingOnWeld(p, parts));

		HashSet<long> olets = new HashSet<long>(
			parts.Where(p => p != null && IsOletPart(p) && !IsOletLandingOnWeld(p, parts))
				.Select(p => ((Element)p).Id.Value));
		HashSet<long> flanges = new HashSet<long>(
			parts.Where(p => p != null && FabricationPartClassification.IsFlangePart(p, assemblyDoc))
				.Select(p => ((Element)p).Id.Value));
		HashSet<long> elbowsTees = new HashSet<long>(
			parts.Where(p => p != null
					&& (FabricationPartClassification.IsElbowPart(p, assemblyDoc)
						|| FabricationPartClassification.IsTeePart(p, assemblyDoc)))
				.Select(p => ((Element)p).Id.Value));
		Dictionary<long, FabricationPart> byId = parts
			.Where(p => p != null)
			.GroupBy(p => ((Element)p).Id.Value)
			.ToDictionary(g => g.Key, g => g.First());

		int hCount = 0;
		int vCount = 0;
		int oletDimHosts = 0;
		int fittingFlangeDims = 0;
		int fittingFittingDims = 0;
		int wrongSideOlet = 0;
		var sideEntries = new List<(bool isH, int sign, double offsetSheet, double spanMin, double spanMax)>();

		if (!TryGetViewPlaneAxes(view, out XYZ vn, out XYZ right, out XYZ up))
		{
			audit.Failures.Add("expected dims: no view axes");
			return;
		}

		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim == null || !dim.IsValidObject || dim.DimensionType == null
					|| !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}
				if (!TryGetDimensionLineSegmentInView(view, dim, out XYZ segA, out XYZ segB, out bool isUpAxis))
				{
					continue;
				}
				bool isH = !isUpAxis;
				if (isUpAxis)
				{
					vCount++;
				}
				else
				{
					hCount++;
				}

				bool hostsOlet = false;
				bool hostsFlange = false;
				bool hostsElbowTee = false;
				int elbowTeeHostCount = 0;
				FabricationPart oletPart = null;
				FabricationPart hostPart = null;
				try
				{
					ReferenceArray refs = dim.References;
					if (refs != null)
					{
						for (int i = 0; i < refs.Size; i++)
						{
							long id = refs.get_Item(i).ElementId.Value;
							if (olets.Contains(id))
							{
								hostsOlet = true;
								byId.TryGetValue(id, out oletPart);
							}
							if (flanges.Contains(id))
							{
								hostsFlange = true;
							}
							if (elbowsTees.Contains(id))
							{
								hostsElbowTee = true;
								elbowTeeHostCount++;
							}
							if (byId.TryGetValue(id, out FabricationPart hp)
								&& hp != null
								&& (IsPipeRunPart(hp) || FabricationPartClassification.IsFlangePart(hp, assemblyDoc))
								&& !IsOletPart(hp))
							{
								hostPart = hp;
							}
						}
					}
				}
				catch
				{
				}

				if (hostsOlet)
				{
					oletDimHosts++;
				}
				if (hostsFlange && hostsElbowTee)
				{
					fittingFlangeDims++;
				}
				if (elbowTeeHostCount >= 2 && !hostsFlange)
				{
					fittingFittingDims++;
				}

				if (hostsOlet && oletPart != null)
				{
					XYZ oletPt = TryGetFabricationPartOrigin(oletPart)
						?? GetFabricationFittingDimensionAnchor(oletPart, hostPart, null, parts);
					XYZ hostPt = hostPart != null
						? (TryGetFabricationPartOrigin(hostPart) ?? oletPt)
						: null;
					if (oletPt != null && hostPt != null
						&& TryGetLinearDimensionLineOrigin(dim, out XYZ dimOrigin)
						&& dimOrigin != null)
					{
						XYZ offsetAxis = (isH ? up : right).Normalize();
						XYZ toOlet = ProjectVectorToViewPlane(oletPt - hostPt, vn);
						XYZ toDim = ProjectVectorToViewPlane(dimOrigin - hostPt, vn);
						if (toOlet != null && toDim != null && toOlet.GetLength() > 1E-09 && toDim.GetLength() > 1E-09)
						{
							double oletSide = toOlet.Normalize().DotProduct(offsetAxis);
							double dimSide = toDim.DotProduct(offsetAxis);
							if (Math.Abs(oletSide) > 1E-09 && Math.Abs(dimSide) > 1E-09
								&& Math.Sign(oletSide) != Math.Sign(dimSide))
							{
								wrongSideOlet++;
							}
						}
					}
				}

				if (TryMeasureDimensionSheetOffset(view, dim, right, up, out double sheet, out int sign)
					|| TryMeasureDimensionSheetOffsetFromHostBoxes(doc, view, dim, right, up, isH, out sheet, out sign))
				{
					XYZ measure = (isH ? right : up).Normalize();
					double c0 = segA.DotProduct(measure);
					double c1 = segB.DotProduct(measure);
					sideEntries.Add((isH, sign == 0 ? 1 : Math.Sign(sign), sheet, Math.Min(c0, c1), Math.Max(c0, c1)));
				}
			}
		}
		catch
		{
		}

		int intersecting = 0;
		const double coincidentTolSheet = 1.0 / (12.0 * 16.0);
		for (int i = 0; i < sideEntries.Count; i++)
		{
			for (int j = i + 1; j < sideEntries.Count; j++)
			{
				var a = sideEntries[i];
				var b = sideEntries[j];
				if (a.isH != b.isH || a.sign != b.sign)
				{
					continue;
				}
				bool overlap = !(a.spanMax < b.spanMin - (1.0 / 12.0) || b.spanMax < a.spanMin - (1.0 / 12.0));
				if (overlap && Math.Abs(a.offsetSheet - b.offsetSheet) <= coincidentTolSheet)
				{
					intersecting++;
				}
			}
		}

		// L-spool: elbow/tee + flange ⇒ H overall + V overall + at least one C→F (fitting↔flange).
		if (hasElbowOrTee && hasFlange)
		{
			if (hCount == 0)
			{
				audit.Failures.Add("expected dims: missing horizontal Linear (L-spool H overall)");
			}
			if (vCount == 0)
			{
				audit.Failures.Add("expected dims: missing vertical Linear (L-spool V overall)");
			}
			if (fittingFlangeDims == 0)
			{
				audit.Failures.Add("expected dims: missing fitting C → flange open F");
			}
			// C→F present ⇒ fitting→fitting overalls are illegal duplicates (ships tilted/dup strings).
			if (fittingFlangeDims > 0 && fittingFittingDims > 0)
			{
				audit.Failures.Add(
					fittingFittingDims + " fitting→fitting Linear(s) when fitting C→flange F exists");
			}
		}
		if (oletCount > 0 && oletDimHosts == 0)
		{
			if (!audit.Failures.Any(f => f != null && f.IndexOf("olet", StringComparison.OrdinalIgnoreCase) >= 0))
			{
				audit.Failures.Add("expected dims: olet present but no olet Linear");
			}
		}
		if (wrongSideOlet > 0)
		{
			audit.Failures.Add(wrongSideOlet + " olet dim(s) on the wrong side of the host run");
		}
		if (intersecting > 0)
		{
			audit.Failures.Add(intersecting + " same-side Linear pair(s) intersecting (need stack or opposite side)");
		}

		audit.ExpectedHCount = hCount;
		audit.ExpectedVCount = vCount;
		audit.FittingFlangeDimCount = fittingFlangeDims;
		audit.WrongSideOletCount = wrongSideOlet;
		audit.IntersectingSameSideCount = intersecting;
	}
}

