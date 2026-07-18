using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

internal static class FabricationReferenceDiagnosticService
{
	private sealed class GeomStats
	{
		public int Solids;
		public int EdgesWithRef;
		public int FacesWithRef;
		public int CurvesWithRef;
		public int Meshes;
		public int Instances;
		public int Other;
	}

	public static string Run(Document doc, View view, string assemblyName)
	{
		if (doc == null)
		{
			throw new ArgumentNullException(nameof(doc));
		}
		if (view == null)
		{
			throw new ArgumentNullException(nameof(view));
		}

		StringBuilder sb = new StringBuilder();
		sb.AppendLine("=== Fabrication reference diagnostic ===");
		sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
		sb.AppendLine("Active view: " + view.Name + " (id=" + view.Id.Value + ")");

		AssemblyInstance assembly = ResolveAssembly(doc, view, assemblyName);
		if (assembly == null)
		{
			sb.AppendLine("Assembly not found. Tried name '" + (assemblyName ?? string.Empty) + "' and view-associated assembly.");
			return WriteReport(sb.ToString());
		}

		sb.AppendLine("Assembly: " + AssemblyDisplayName.Get(assembly) + " (id=" + ((Element)assembly).Id.Value + ")");

		View3D pickView3D = CreateSpoolSheetsHandler.FindAssemblyViews(doc, assembly).OfType<View3D>().FirstOrDefault();
		sb.AppendLine("Assembly 3D pick view: " + (pickView3D != null ? pickView3D.Name + " (id=" + pickView3D.Id.Value + ")" : "none"));

		ReportViewDimensions(doc, view, sb);

		List<FabricationPart> parts = assembly.GetMemberIds()
			.Select(doc.GetElement)
			.OfType<FabricationPart>()
			.ToList();
		sb.AppendLine("Fabrication members: " + parts.Count);

		foreach (FabricationPart part in parts)
		{
			DiagnosePart(doc, view, pickView3D, part, sb);
		}

		sb.AppendLine();
		sb.AppendLine("=== Done. Compare manual-dimension refs above with auto-dim resolver paths. ===");
		return WriteReport(sb.ToString());
	}

	private static void ReportViewDimensions(Document doc, View view, StringBuilder sb)
	{
		sb.AppendLine();
		sb.AppendLine("=== Dimensions in active view (place Linear dims here for auto-dim reference) ===");
		int count = 0;
		try
		{
			foreach (Dimension dimension in new FilteredElementCollector(doc, view.Id)
				.OfClass(typeof(Dimension))
				.Cast<Dimension>())
			{
				count++;
				string dimTypeName = "?";
				string styleType = "?";
				try
				{
					DimensionType dimensionType = doc.GetElement(dimension.DimensionType.Id) as DimensionType;
					if (dimensionType != null)
					{
						dimTypeName = ((Element)dimensionType).Name ?? "?";
						styleType = dimensionType.StyleType.ToString();
					}
				}
				catch
				{
				}

				sb.AppendLine("  Dim id=" + ((Element)dimension).Id.Value
					+ " value=\"" + (dimension.ValueString ?? "?") + "\""
					+ " type=\"" + dimTypeName + "\""
					+ " style=" + styleType);
				ReferenceArray references = dimension.References;
				if (references == null || references.Size == 0)
				{
					sb.AppendLine("    (no references on dimension)");
					continue;
				}

				for (int i = 0; i < references.Size; i++)
				{
					Reference reference = references.get_Item(i);
					if (reference == null)
					{
						sb.AppendLine("    ref[" + i + "]: null");
						continue;
					}

					Element element = doc.GetElement(reference.ElementId);
					sb.AppendLine("    ref[" + i + "]: elemId=" + reference.ElementId.Value
						+ " name=" + (element?.Name ?? "?")
						+ " type=" + RefTypeName(reference)
						+ " stable=" + Stable(doc, reference));
				}
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("  Error reading dimensions: " + ex.Message);
		}

		if (count == 0)
		{
			sb.AppendLine("  (no dimensions in view — place a manual dim first, then re-run)");
		}
	}

	private static AssemblyInstance ResolveAssembly(Document doc, View view, string assemblyName)
	{
		if (view.IsAssemblyView && view.AssociatedAssemblyInstanceId != ElementId.InvalidElementId)
		{
			Element element = doc.GetElement(view.AssociatedAssemblyInstanceId);
			AssemblyInstance fromView = element as AssemblyInstance;
			if (fromView != null)
			{
				return fromView;
			}
		}

		if (!string.IsNullOrWhiteSpace(assemblyName))
		{
			foreach (AssemblyInstance candidate in new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>())
			{
				if (string.Equals(AssemblyDisplayName.Get(candidate), assemblyName.Trim(), StringComparison.OrdinalIgnoreCase))
				{
					return candidate;
				}
			}
		}

		return null;
	}

	private static void DiagnosePart(Document doc, View view, View3D pickView3D, FabricationPart part, StringBuilder sb)
	{
		sb.AppendLine();
		sb.AppendLine("--- Part Id=" + ((Element)part).Id.Value
			+ " Name=" + ((Element)part).Name
			+ " Category=" + (((Element)part).Category?.Name ?? "?") + " ---");

		ReportSubelements(doc, part, sb);
		ReportLocationCurve(part, sb);
		ReportGeometryStats(part, view, sb);
		ReportExtendedGeometryPaths(doc, part, view, sb);
		ReportConnectors(part, sb);
		Report3DIntersector(doc, pickView3D, part, sb);

		try
		{
			Reference whole = new Reference((Element)part);
			sb.AppendLine("    whole-element ref stable=" + Stable(doc, whole));
		}
		catch (Exception ex)
		{
			sb.AppendLine("    whole-element ref error: " + ex.Message);
		}
	}

	private static void ReportConnectors(FabricationPart part, StringBuilder sb)
	{
		int count = 0;
		try
		{
			ConnectorManager manager = part.ConnectorManager;
			if (manager == null)
			{
				sb.AppendLine("    connectors: none (no ConnectorManager)");
				return;
			}

			foreach (Connector connector in manager.Connectors)
			{
				count++;
				XYZ origin = connector?.Origin;
				sb.AppendLine("    connector[" + count + "]: origin="
					+ FormatPoint(origin)
					+ " domain=" + (connector != null ? connector.Domain.ToString() : "?"));
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("    connectors error: " + ex.Message);
		}

		if (count == 0)
		{
			sb.AppendLine("    connectors: 0");
		}

		try
		{
			sb.AppendLine("    part.Origin: " + FormatPoint(part.Origin));
		}
		catch
		{
		}
	}

	private static void Report3DIntersector(Document doc, View3D pickView3D, FabricationPart part, StringBuilder sb)
	{
		if (pickView3D == null)
		{
			sb.AppendLine("    3D intersector: skipped (no assembly View3D)");
			return;
		}

		List<XYZ> probePoints = new List<XYZ>();
		try
		{
			if (part.Origin != null)
			{
				probePoints.Add(part.Origin);
			}
		}
		catch
		{
		}

		try
		{
			ConnectorManager manager = part.ConnectorManager;
			if (manager != null)
			{
				foreach (Connector connector in manager.Connectors)
				{
					if (connector?.Origin != null)
					{
						probePoints.Add(connector.Origin);
					}
				}
			}
		}
		catch
		{
		}

		if (probePoints.Count == 0)
		{
			sb.AppendLine("    3D intersector: no probe points");
			return;
		}

		FindReferenceTarget[] targets =
		{
			FindReferenceTarget.Edge,
			FindReferenceTarget.Face,
			FindReferenceTarget.Curve
		};

		int probeIndex = 0;
		foreach (XYZ probePoint in probePoints.Distinct(new XyzEquality(1.0 / 64.0)))
		{
			probeIndex++;
			sb.AppendLine("    3D probe[" + probeIndex + "] at " + FormatPoint(probePoint) + ":");
			foreach (FindReferenceTarget target in targets)
			{
				Reference hit = Try3DIntersectorHit(pickView3D, part, probePoint, target);
				sb.AppendLine("      " + target + ": "
					+ (hit != null ? "HIT stable=" + Stable(doc, hit) + " type=" + RefTypeName(hit) : "miss"));
			}
		}
	}

	private static Reference Try3DIntersectorHit(View3D view3D, FabricationPart part, XYZ point, FindReferenceTarget target)
	{
		if (view3D == null || part == null || point == null)
		{
			return null;
		}

		ReferenceIntersector intersector;
		try
		{
			intersector = new ReferenceIntersector(((Element)part).Id, target, view3D);
		}
		catch
		{
			return null;
		}

		XYZ[] directions =
		{
			view3D.ViewDirection,
			view3D.ViewDirection.Negate(),
			view3D.RightDirection,
			view3D.RightDirection.Negate(),
			view3D.UpDirection,
			view3D.UpDirection.Negate()
		};

		double rayLength = 10.0;
		foreach (XYZ direction in directions)
		{
			if (direction == null || direction.GetLength() < 1e-9)
			{
				continue;
			}

			XYZ normalized = direction.Normalize();
			ReferenceWithContext hit;
			try
			{
				hit = intersector.FindNearest(point - normalized.Multiply(rayLength), normalized);
			}
			catch
			{
				continue;
			}

			Reference reference = hit?.GetReference();
			if (reference != null && reference.ElementId == ((Element)part).Id)
			{
				return reference;
			}
		}

		return null;
	}

	private static void ReportExtendedGeometryPaths(Document doc, Element element, View view, StringBuilder sb)
	{
		int curves = 0;
		int edges = 0;
		int faces = 0;
		foreach (Options options in BuildProbeGeometryOptions(view))
		{
			GeometryElement geometry;
			try
			{
				geometry = element.get_Geometry(options);
			}
			catch
			{
				continue;
			}

			if (geometry == null)
			{
				continue;
			}

			CollectRefsFromGeometry(geometry, Transform.Identity, ref curves, ref edges, ref faces);
		}

		sb.AppendLine("    extended geometry paths: curves_ref=" + curves
			+ " edges_ref=" + edges + " faces_ref=" + faces
			+ " (recursive Instance/Mesh, multi DetailLevel)");
	}

	private static IEnumerable<Options> BuildProbeGeometryOptions(View view)
	{
		ViewDetailLevel[] detailLevels = { ViewDetailLevel.Fine, ViewDetailLevel.Medium, ViewDetailLevel.Coarse };
		foreach (ViewDetailLevel detailLevel in detailLevels)
		{
			foreach (bool includeNonVisible in new[] { false, true })
			{
				yield return new Options
				{
					ComputeReferences = true,
					IncludeNonVisibleObjects = includeNonVisible,
					DetailLevel = detailLevel
				};
			}
		}

		if (view == null)
		{
			yield break;
		}

		foreach (bool includeNonVisible in new[] { false, true })
		{
			yield return new Options
			{
				ComputeReferences = true,
				IncludeNonVisibleObjects = includeNonVisible,
				View = view
			};
		}
	}

	private static void CollectRefsFromGeometry(GeometryElement geometry, Transform transform, ref int curves, ref int edges, ref int faces)
	{
		foreach (GeometryObject geometryObject in geometry)
		{
			Curve curve = geometryObject as Curve;
			if (curve?.Reference != null)
			{
				curves++;
			}

			Solid solid = geometryObject as Solid;
			if (solid != null && solid.Faces.Size > 0)
			{
				foreach (Face face in solid.Faces)
				{
					if (face?.Reference != null)
					{
						faces++;
					}
				}

				foreach (Edge edge in solid.Edges)
				{
					if (edge?.Reference != null)
					{
						edges++;
					}
				}

				continue;
			}

			GeometryInstance instance = geometryObject as GeometryInstance;
			if (instance != null)
			{
				GeometryElement instanceGeometry = instance.GetInstanceGeometry();
				if (instanceGeometry != null)
				{
					CollectRefsFromGeometry(instanceGeometry, transform.Multiply(instance.Transform), ref curves, ref edges, ref faces);
				}
			}
		}
	}

	private static void ReportSubelements(Document doc, Element element, StringBuilder sb)
	{
		int count = 0;
		try
		{
			foreach (Subelement sub in element.GetSubelements())
			{
				count++;
				try
				{
					Reference reference = sub.GetReference();
					sb.AppendLine("    subelement[" + count + "] stable=" + Stable(doc, reference)
						+ " type=" + RefTypeName(reference));
				}
				catch (Exception ex)
				{
					sb.AppendLine("    subelement[" + count + "] error: " + ex.Message);
				}
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("    GetSubelements failed: " + ex.Message);
		}

		if (count == 0)
		{
			sb.AppendLine("    (no subelements)");
		}
	}

	private static void ReportLocationCurve(Element element, StringBuilder sb)
	{
		LocationCurve locationCurve = element.Location as LocationCurve;
		if (locationCurve?.Curve == null)
		{
			sb.AppendLine("    location curve: none");
			return;
		}

		Curve curve = locationCurve.Curve;
		try
		{
			Reference end0 = curve.GetEndPointReference(0);
			Reference end1 = curve.GetEndPointReference(1);
			sb.AppendLine("    curve end ref[0]: " + (end0 != null ? "ok" : "NULL")
				+ " type=" + (end0 != null ? RefTypeName(end0) : "-"));
			sb.AppendLine("    curve end ref[1]: " + (end1 != null ? "ok" : "NULL")
				+ " type=" + (end1 != null ? RefTypeName(end1) : "-"));
		}
		catch (Exception ex)
		{
			sb.AppendLine("    curve end ref error: " + ex.Message);
		}
	}

	private static void ReportGeometryStats(Element element, View view, StringBuilder sb)
	{
		GeomStats viewStats = CollectGeometryStats(element, view, includeView: true);
		sb.AppendLine("    geometry[view]: solids=" + viewStats.Solids
			+ " curves_ref=" + viewStats.CurvesWithRef
			+ " edges_ref=" + viewStats.EdgesWithRef
			+ " faces_ref=" + viewStats.FacesWithRef
			+ " meshes=" + viewStats.Meshes
			+ " instances=" + viewStats.Instances
			+ " other=" + viewStats.Other);

		GeomStats modelStats = CollectGeometryStats(element, view: null, includeView: false);
		sb.AppendLine("    geometry[model]: solids=" + modelStats.Solids
			+ " curves_ref=" + modelStats.CurvesWithRef
			+ " edges_ref=" + modelStats.EdgesWithRef
			+ " faces_ref=" + modelStats.FacesWithRef
			+ " meshes=" + modelStats.Meshes
			+ " instances=" + modelStats.Instances
			+ " other=" + modelStats.Other);
	}

	private static GeomStats CollectGeometryStats(Element element, View view, bool includeView)
	{
		GeomStats stats = new GeomStats();
		Options options = new Options
		{
			ComputeReferences = true,
			IncludeNonVisibleObjects = false
		};
		if (includeView && view != null)
		{
			options.View = view;
		}

		GeometryElement geometry;
		try
		{
			geometry = element.get_Geometry(options);
		}
		catch
		{
			return stats;
		}

		if (geometry == null)
		{
			return stats;
		}

		WalkGeometryObjects(geometry, stats);
		return stats;
	}

	private static void WalkGeometryObjects(GeometryElement geometry, GeomStats stats)
	{
		foreach (GeometryObject geometryObject in geometry)
		{
			if (geometryObject is Solid solid && solid.Faces.Size > 0)
			{
				stats.Solids++;
				foreach (Face face in solid.Faces)
				{
					if (face?.Reference != null)
					{
						stats.FacesWithRef++;
					}
				}

				foreach (Edge edge in solid.Edges)
				{
					if (edge?.Reference != null)
					{
						stats.EdgesWithRef++;
					}
				}

				continue;
			}

			if (geometryObject is Curve curve)
			{
				if (curve.Reference != null)
				{
					stats.CurvesWithRef++;
				}

				continue;
			}

			if (geometryObject is Mesh)
			{
				stats.Meshes++;
				continue;
			}

			if (geometryObject is GeometryInstance instance)
			{
				stats.Instances++;
				GeometryElement instanceGeometry = instance.GetInstanceGeometry();
				if (instanceGeometry != null)
				{
					WalkGeometryObjects(instanceGeometry, stats);
				}

				continue;
			}

			stats.Other++;
		}
	}

	private static string FormatPoint(XYZ point)
	{
		if (point == null)
		{
			return "null";
		}

		return "(" + point.X.ToString("0.###") + ", " + point.Y.ToString("0.###") + ", " + point.Z.ToString("0.###") + ")";
	}

	private static string RefTypeName(Reference reference)
	{
		try
		{
			return reference.ElementReferenceType.ToString();
		}
		catch
		{
			return "?";
		}
	}

	private static string Stable(Document doc, Reference reference)
	{
		try
		{
			return reference.ConvertToStableRepresentation(doc);
		}
		catch (Exception ex)
		{
			return "<err: " + ex.Message + ">";
		}
	}

	private static string WriteReport(string contents)
	{
		string[] folders =
		{
			Path.Combine(SpoolingManagerSettings.SettingsFolderPath, "TestingReports"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spooling-Savant-V3-Exports", "SpoolingManager", "TestingReports"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Spooling Savant V3 (Exports)", "TestingReports")
		};

		foreach (string folder in folders)
		{
			if (string.IsNullOrWhiteSpace(folder))
			{
				continue;
			}

			try
			{
				Directory.CreateDirectory(folder);
				string path = Path.Combine(folder, "FabricationRefDiag.log");
				File.WriteAllText(path, contents, Encoding.UTF8);
				return path;
			}
			catch
			{
			}
		}

		throw new InvalidOperationException("Could not write FabricationRefDiag.log to any known folder.");
	}

	private sealed class XyzEquality : IEqualityComparer<XYZ>
	{
		private readonly double _tolerance;

		internal XyzEquality(double tolerance)
		{
			_tolerance = tolerance;
		}

		public bool Equals(XYZ a, XYZ b)
		{
			if (a == null && b == null)
			{
				return true;
			}

			if (a == null || b == null)
			{
				return false;
			}

			return a.DistanceTo(b) <= _tolerance;
		}

		public int GetHashCode(XYZ obj)
		{
			if (obj == null)
			{
				return 0;
			}

			return obj.X.GetHashCode() ^ obj.Y.GetHashCode() ^ obj.Z.GetHashCode();
		}
	}
}
