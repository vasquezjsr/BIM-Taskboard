using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Sheet layout hard laws for auto-dims: no duplicate same-role strings, and dim lines / witness
/// extensions never cross.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	/// <summary>
	/// Delete duplicate Linear dims that share the same host pair and nearly the same value
	/// (e.g. olet repair re-placing an already-good E→C).
	/// </summary>
	private static int PurgeDuplicateViewLinearDimensions(Document doc, View view)
	{
		if (doc == null || view == null || view is View3D || view.IsTemplate)
		{
			return 0;
		}
		const double valueTolFeet = 1.0 / 16.0 / 12.0;
		List<Dimension> dims;
		try
		{
			dims = new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>()
				.Where(d => d != null && d.IsValidObject && d.DimensionType != null && IsLinearDimensionType(d.DimensionType))
				.OrderBy(d => ((Element)d).Id.Value)
				.ToList();
		}
		catch
		{
			return 0;
		}
		HashSet<long> kill = new HashSet<long>();
		for (int i = 0; i < dims.Count; i++)
		{
			if (kill.Contains(((Element)dims[i]).Id.Value))
			{
				continue;
			}
			if (!TryGetDimensionHostPairKey(dims[i], out string keyI) || !TryGetDimensionValueFeet(dims[i], out double valI))
			{
				continue;
			}
			for (int j = i + 1; j < dims.Count; j++)
			{
				if (kill.Contains(((Element)dims[j]).Id.Value))
				{
					continue;
				}
				if (!TryGetDimensionHostPairKey(dims[j], out string keyJ) || !TryGetDimensionValueFeet(dims[j], out double valJ))
				{
					continue;
				}
				if (!string.Equals(keyI, keyJ, StringComparison.Ordinal) || Math.Abs(valI - valJ) > valueTolFeet)
				{
					continue;
				}
				kill.Add(((Element)dims[j]).Id.Value);
			}
		}
		int n = 0;
		foreach (long id in kill)
		{
			try
			{
				doc.Delete(new ElementId(id));
				n++;
			}
			catch
			{
			}
		}
		if (n > 0)
		{
			try { DoRegenNow(doc); } catch { }
			TryAppendAutoDimDiagnosticLog("layout", view.Name, "purged duplicate Linear dims=" + n, 0, n);
		}
		return n;
	}

	/// <summary>
	/// Crossing is prevented at place-time (exterior sides + slot-0 gap). Do not shove dims
	/// after the fact — MoveDimExterior was pushing unstacked dims inches past 3/8".
	/// </summary>
	private static int UncrossViewLinearDimensions(Document doc, View view)
	{
		return 0;
	}

	private sealed class DimLayoutSeg
	{
		public Dimension Dim;
		public XYZ LineA;
		public XYZ LineB;
		public bool IsUpAxis;
		public List<(XYZ a, XYZ b)> Extensions = new List<(XYZ, XYZ)>();
	}

	private static List<DimLayoutSeg> CollectDimLayoutSegments(Document doc, View view)
	{
		List<DimLayoutSeg> segs = new List<DimLayoutSeg>();
		try
		{
			foreach (Dimension dim in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				if (dim == null || !dim.IsValidObject || dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
				{
					continue;
				}
				if (!TryGetDimensionLineSegmentInView(view, dim, out XYZ a, out XYZ b, out bool isUp))
				{
					continue;
				}
				DimLayoutSeg seg = new DimLayoutSeg
				{
					Dim = dim,
					LineA = a,
					LineB = b,
					IsUpAxis = isUp
				};
				AppendWitnessExtensions(doc, view, dim, a, b, seg.Extensions);
				segs.Add(seg);
			}
		}
		catch
		{
		}
		return segs;
	}

	private static void AppendWitnessExtensions(Document doc, View view, Dimension dim, XYZ lineA, XYZ lineB, List<(XYZ a, XYZ b)> extensions)
	{
		if (doc == null || view == null || dim?.References == null || lineA == null || lineB == null || extensions == null)
		{
			return;
		}
		XYZ lineDir = (lineB - lineA);
		if (lineDir.GetLength() < 1E-09)
		{
			return;
		}
		lineDir = lineDir.Normalize();
		for (int i = 0; i < dim.References.Size; i++)
		{
			Reference r = dim.References.get_Item(i);
			Element host = doc.GetElement(r.ElementId);
			if (host == null)
			{
				continue;
			}
			XYZ target = Midpoint(lineA, lineB);
			if (!TryGetReferenceSampleWorldPointForTarget(host, r, target, out XYZ witness) || witness == null)
			{
				continue;
			}
			// Closest point on dim line to witness = extension foot.
			XYZ w = ProjectToSketchPlane(witness, view.Origin ?? lineA, view.ViewDirection)
				?? witness;
			XYZ foot = lineA + lineDir.Multiply((w - lineA).DotProduct(lineDir));
			if (w.DistanceTo(foot) > 1E-06)
			{
				extensions.Add((w, foot));
			}
		}
	}

	private static bool DimLayoutsInterfere(View view, DimLayoutSeg a, DimLayoutSeg b)
	{
		if (a == null || b == null)
		{
			return false;
		}
		// Dim line vs dim line — always a hard conflict.
		if (ViewSegmentsIntersect(view, a.LineA, a.LineB, b.LineA, b.LineB))
		{
			return true;
		}
		// Orthogonal H↔V pairs on different exterior faces are not stacked. Their short witnesses
		// near fittings must not shove both dims farther from the pipe (falsely escalate past 3/8").
		// Only same-axis stacks (both pulled up/down, or both left/right) check extension-vs-line.
		if (a.IsUpAxis != b.IsUpAxis)
		{
			return false;
		}
		// Witness extension of A vs dim line of B (same-side / same-axis nest conflict)
		foreach ((XYZ e0, XYZ e1) ext in a.Extensions)
		{
			if (ViewSegmentsIntersect(view, ext.e0, ext.e1, b.LineA, b.LineB))
			{
				return true;
			}
		}
		foreach ((XYZ e0, XYZ e1) ext in b.Extensions)
		{
			if (ViewSegmentsIntersect(view, ext.e0, ext.e1, a.LineA, a.LineB))
			{
				return true;
			}
		}
		return false;
	}

	private static int MoveDimExterior(
		Document doc,
		DimLayoutSeg seg,
		XYZ centroid,
		XYZ right,
		XYZ up,
		double snap,
		HashSet<long> alreadyMoved)
	{
		if (doc == null || seg?.Dim == null || alreadyMoved == null)
		{
			return 0;
		}
		long id = ((Element)seg.Dim).Id.Value;
		if (!alreadyMoved.Add(id))
		{
			return 0;
		}
		XYZ offsetAxis = seg.IsUpAxis ? right : up;
		if (offsetAxis == null || offsetAxis.GetLength() < 1E-09)
		{
			return 0;
		}
		offsetAxis = offsetAxis.Normalize();
		XYZ mid = Midpoint(seg.LineA, seg.LineB);
		XYZ move = offsetAxis.Multiply(snap);
		if (centroid != null)
		{
			double dPlus = (mid + offsetAxis.Multiply(snap)).DistanceTo(centroid);
			double dMinus = (mid - offsetAxis.Multiply(snap)).DistanceTo(centroid);
			move = dPlus >= dMinus ? offsetAxis.Multiply(snap) : offsetAxis.Negate().Multiply(snap);
		}
		else
		{
			// No centroid: horizontal dims below (−up), vertical dims outside (+right).
			move = seg.IsUpAxis ? right.Multiply(snap) : up.Negate().Multiply(snap);
		}
		// Never MoveElement Linear dims — rematerializes as tilted. Recreate on forced axis.
		try
		{
			View ownerView = doc.GetElement(seg.Dim.OwnerViewId) as View;
			if (ownerView != null && TryNudgeLinearDimensionAxisAligned(doc, ownerView, seg.Dim, move, right, up))
			{
				return 1;
			}
		}
		catch
		{
		}
		return 0;
	}

	private static bool TryGetDimensionHostPairKey(Dimension dim, out string key)
	{
		key = null;
		if (dim?.References == null || dim.References.Size < 2)
		{
			return false;
		}
		List<long> ids = new List<long>();
		for (int i = 0; i < dim.References.Size; i++)
		{
			ids.Add(dim.References.get_Item(i).ElementId.Value);
		}
		ids.Sort();
		key = string.Join("-", ids);
		return ids.Count >= 2;
	}

	private static bool TryGetDimensionValueFeet(Dimension dim, out double value)
	{
		value = 0.0;
		try
		{
			double? v = dim.Value;
			if (!v.HasValue)
			{
				return false;
			}
			value = v.Value;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetDimensionLineSegmentInView(View view, Dimension dim, out XYZ a, out XYZ b, out bool isUpAxis)
	{
		a = null;
		b = null;
		isUpAxis = false;
		if (view == null || dim == null || !TryGetViewPlaneAxes(view, out XYZ vn, out XYZ right, out XYZ up))
		{
			return false;
		}
		Curve curve = dim.Curve;
		if ((GeometryObject)(object)curve == (GeometryObject)null)
		{
			return false;
		}
		if (!TryGetDimensionOrLineDirection(curve, out XYZ dir) || dir == null)
		{
			return false;
		}
		XYZ inPlane = ProjectVectorToViewPlane(dir, vn);
		if (inPlane == null || inPlane.GetLength() < 1E-09)
		{
			return false;
		}
		inPlane = inPlane.Normalize();
		isUpAxis = Math.Abs(inPlane.DotProduct(up)) >= Math.Abs(inPlane.DotProduct(right));
		XYZ mid;
		try
		{
			Line asLine = curve as Line;
			mid = asLine != null ? asLine.Origin : curve.Evaluate(0.5, true);
		}
		catch
		{
			return false;
		}
		if (!TryGetDimensionValueFeet(dim, out double len) || len < 1E-06)
		{
			len = 1.0;
		}
		double half = (len * 0.5) + (1.0 / 12.0); // pad so witness rakes still detect
		a = mid - inPlane.Multiply(half);
		b = mid + inPlane.Multiply(half);
		return a != null && b != null;
	}

	private static XYZ Midpoint(XYZ a, XYZ b)
	{
		if (a == null || b == null)
		{
			return a ?? b;
		}
		return (a + b) * 0.5;
	}

	private static bool ViewSegmentsIntersect(View view, XYZ a0, XYZ a1, XYZ b0, XYZ b1)
	{
		if (view == null || a0 == null || a1 == null || b0 == null || b1 == null)
		{
			return false;
		}
		if (!TryGetViewPlaneAxes(view, out _, out XYZ right, out XYZ up))
		{
			return false;
		}
		XYZ origin = view.Origin ?? a0;
		UV A0 = ToViewUv(a0, origin, right, up);
		UV A1 = ToViewUv(a1, origin, right, up);
		UV B0 = ToViewUv(b0, origin, right, up);
		UV B1 = ToViewUv(b1, origin, right, up);
		return UvSegmentsIntersect(A0, A1, B0, B1);
	}

	private static UV ToViewUv(XYZ p, XYZ origin, XYZ right, XYZ up)
	{
		XYZ d = p - origin;
		return new UV(d.DotProduct(right), d.DotProduct(up));
	}

	private static bool UvSegmentsIntersect(UV p1, UV p2, UV p3, UV p4)
	{
		double d1 = UvCross(p3, p4, p1);
		double d2 = UvCross(p3, p4, p2);
		double d3 = UvCross(p1, p2, p3);
		double d4 = UvCross(p1, p2, p4);
		if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
		{
			return true;
		}
		const double eps = 1E-09;
		if (Math.Abs(d1) < eps && UvOnSegment(p3, p4, p1)) return true;
		if (Math.Abs(d2) < eps && UvOnSegment(p3, p4, p2)) return true;
		if (Math.Abs(d3) < eps && UvOnSegment(p1, p2, p3)) return true;
		if (Math.Abs(d4) < eps && UvOnSegment(p1, p2, p4)) return true;
		return false;
	}

	private static double UvCross(UV a, UV b, UV p)
	{
		return (b.U - a.U) * (p.V - a.V) - (b.V - a.V) * (p.U - a.U);
	}

	private static bool UvOnSegment(UV a, UV b, UV p)
	{
		return p.U >= Math.Min(a.U, b.U) - 1E-09
			&& p.U <= Math.Max(a.U, b.U) + 1E-09
			&& p.V >= Math.Min(a.V, b.V) - 1E-09
			&& p.V <= Math.Max(a.V, b.V) + 1E-09;
	}
}
