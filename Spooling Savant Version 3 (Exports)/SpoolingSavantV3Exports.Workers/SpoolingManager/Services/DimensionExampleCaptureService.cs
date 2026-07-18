using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Records how the user dimensioned a spool using geometry/topology features only.
/// Sheet names, assembly names, and element ids are trace metadata — never used for matching.
/// </summary>
internal static class DimensionExampleCaptureService
{
	private const int SchemaVersion = 1;

	public static CaptureDimensionExampleResult Capture(Document doc, View view, ElementId preferredAssemblyId)
	{
		if (doc == null)
		{
			return Fail("No document.");
		}
		if (view == null)
		{
			return Fail("No active view.");
		}
		if (view is ViewSheet)
		{
			return Fail("You are on a sheet. Double-click a viewport (Front, Left, etc.) so the assembly view is active, dimension it, then capture again.");
		}
		if (!TryGetViewAxes(view, out XYZ viewNormal, out XYZ viewRight, out XYZ viewUp))
		{
			return Fail("This view has no usable view direction. Open an assembly elevation, detail, or 3D view.");
		}
		AssemblyInstance assembly = ResolveAssembly(doc, view, preferredAssemblyId);
		if (assembly == null)
		{
			return Fail("Could not resolve an assembly for this view. Select the assembly in SS Manager or open an assembly view.");
		}
		List<FabricationPart> parts = assembly.GetMemberIds()
			.Select(doc.GetElement)
			.OfType<FabricationPart>()
			.ToList();
		TopologySnapshot topology = BuildTopologySnapshot(parts, view, viewNormal, viewRight, viewUp);
		List<CapturedDimensionRecord> dimensions = CollectDimensions(doc, view, parts, viewNormal, viewRight, viewUp, topology);
		if (dimensions.Count == 0)
		{
			return Fail("No dimensions found in this view. Place dimensions the way you want them, then click Capture Dim Example again.");
		}
		string outputPath = WriteExampleFile(doc, view, assembly, topology, dimensions, viewNormal, viewRight, viewUp);
		AppendIndexEntry(outputPath, topology.TopologyHash, view, dimensions.Count);
		return new CaptureDimensionExampleResult
		{
			Success = true,
			OutputPath = outputPath,
			DimensionCount = dimensions.Count,
			Message = "Captured " + dimensions.Count + " dimension(s).\nTopology hash: " + topology.TopologyHash
		};
	}

	private static CaptureDimensionExampleResult Fail(string message)
	{
		return new CaptureDimensionExampleResult
		{
			Success = false,
			Message = message
		};
	}

	private static AssemblyInstance ResolveAssembly(Document doc, View view, ElementId preferredAssemblyId)
	{
		if (preferredAssemblyId != null && preferredAssemblyId != ElementId.InvalidElementId)
		{
			AssemblyInstance fromSelection = doc.GetElement(preferredAssemblyId) as AssemblyInstance;
			if (fromSelection != null)
			{
				return fromSelection;
			}
		}
		if (view.IsAssemblyView && view.AssociatedAssemblyInstanceId != ElementId.InvalidElementId)
		{
			return doc.GetElement(view.AssociatedAssemblyInstanceId) as AssemblyInstance;
		}
		return null;
	}

	private static bool TryGetViewAxes(View view, out XYZ viewNormal, out XYZ viewRight, out XYZ viewUp)
	{
		viewNormal = null;
		viewRight = null;
		viewUp = null;
		XYZ vn = view?.ViewDirection;
		XYZ right = view?.RightDirection;
		XYZ up = view?.UpDirection;
		if (vn == null || right == null || up == null || vn.GetLength() < 1E-09 || right.GetLength() < 1E-09 || up.GetLength() < 1E-09)
		{
			return false;
		}
		viewNormal = vn.Normalize();
		viewRight = ProjectToPlane(right, viewNormal)?.Normalize();
		viewUp = ProjectToPlane(up, viewNormal)?.Normalize();
		return viewRight != null && viewUp != null;
	}

	private static TopologySnapshot BuildTopologySnapshot(List<FabricationPart> parts, View view, XYZ viewNormal, XYZ viewRight, XYZ viewUp)
	{
		int pipeSegments = 0;
		int oletCount = 0;
		int anviletCount = 0;
		int flangeCount = 0;
		int branchStubCount = 0;
		int fittingCount = 0;
		double longestPipe = 0.0;
		XYZ dominantRunAxis = null;
		Dictionary<string, double> axisBuckets = new Dictionary<string, double>(StringComparer.Ordinal);
		foreach (FabricationPart part in parts)
		{
			string corpus = GetPartCorpus(part);
			if (FabricationPartClassification.IsOletPart(part))
			{
				oletCount++;
				if (corpus.Contains("ANVILET"))
				{
					anviletCount++;
				}
				continue;
			}
			if (FabricationPartClassification.IsFlangePart(part, part.Document))
			{
				flangeCount++;
				continue;
			}
			double len = GetPipeLength(part);
			if (len > 1.0 / 24.0 && TryGetLineDirection(part, out XYZ dir))
			{
				pipeSegments++;
				if (len > longestPipe)
				{
					longestPipe = len;
				}
				XYZ inPlane = ProjectToPlane(dir, viewNormal);
				if (inPlane != null && inPlane.GetLength() > 1E-09)
				{
					inPlane = inPlane.Normalize();
					string key = AxisKey(inPlane);
					axisBuckets.TryGetValue(key, out double existing);
					axisBuckets[key] = existing + len;
				}
			}
			else if (IsFittingLike(part))
			{
				fittingCount++;
			}
		}
		double bestLen = 0.0;
		foreach (KeyValuePair<string, double> bucket in axisBuckets)
		{
			if (bucket.Value > bestLen)
			{
				bestLen = bucket.Value;
				dominantRunAxis = ParseAxisKey(bucket.Key);
			}
		}
		foreach (FabricationPart part in parts)
		{
			if (!IsPipeRunPart(part) || GetPipeLength(part) > 4.0)
			{
				continue;
			}
			if (ConnectsToOlet(part, parts) && !IsCollinearWithAxis(part, dominantRunAxis))
			{
				branchStubCount++;
			}
		}
		bool hasVerticalDrop = parts.Any((FabricationPart p) => IsPipeRunPart(p) && TryGetLineDirection(p, out XYZ d) && Math.Abs(d.Normalize().DotProduct(viewUp)) > 0.85 && GetPipeLength(p) > 2.0);
		string geometryClass = ClassifyViewGeometry(view, viewNormal);
		string hashInput = string.Join("|", new string[]
		{
			"v" + SchemaVersion,
			"vc=" + geometryClass,
			"pipes=" + pipeSegments,
			"olets=" + oletCount,
			"anv=" + anviletCount,
			"fl=" + flangeCount,
			"br=" + branchStubCount,
			"drop=" + (hasVerticalDrop ? 1 : 0),
			"run=" + BucketLength(longestPipe),
			"axis=" + AxisKey(dominantRunAxis)
		});
		return new TopologySnapshot
		{
			TopologyHash = ShortHash(hashInput),
			ViewGeometryClass = geometryClass,
			PipeRunSegmentCount = pipeSegments,
			OletCount = oletCount,
			AnviletCount = anviletCount,
			FlangeCount = flangeCount,
			BranchStubCount = branchStubCount,
			FittingCount = fittingCount,
			HasVerticalDrop = hasVerticalDrop,
			DominantRunLengthBucket = BucketLength(longestPipe),
			DominantRunAxisInView = dominantRunAxis
		};
	}

	private static List<CapturedDimensionRecord> CollectDimensions(Document doc, View view, List<FabricationPart> parts, XYZ viewNormal, XYZ viewRight, XYZ viewUp, TopologySnapshot topology)
	{
		List<CapturedDimensionRecord> records = new List<CapturedDimensionRecord>();
		HashSet<ElementId> partIds = new HashSet<ElementId>(parts.Select((FabricationPart p) => p.Id));
		foreach (Dimension dimension in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
		{
			if (dimension == null || dimension.References == null || dimension.References.Size < 2)
			{
				continue;
			}
			if (!TryGetReferencePoint(doc, view, dimension.References.get_Item(0), partIds, out XYZ ptA, out ElementRole roleA))
			{
				continue;
			}
			if (!TryGetReferencePoint(doc, view, dimension.References.get_Item(1), partIds, out XYZ ptB, out ElementRole roleB))
			{
				continue;
			}
			XYZ chord = ptB - ptA;
			if (chord == null || chord.GetLength() < 1E-09)
			{
				continue;
			}
			double valueFeet = 0.0;
			try
			{
				valueFeet = dimension.Value ?? 0.0;
			}
			catch
			{
			}
			XYZ chordInView = ProjectToPlane(chord, viewNormal);
			XYZ dimLineDir = null;
			XYZ dimLineCenter = null;
			Curve curve = dimension.Curve;
			Line line = curve as Line;
			if (line != null)
			{
				dimLineDir = ProjectToPlane(line.Direction, viewNormal);
				try
				{
					dimLineCenter = (line.GetEndPoint(0) + line.GetEndPoint(1)) * 0.5;
				}
				catch
				{
				}
			}
			if (dimLineCenter == null)
			{
				try
				{
					dimLineCenter = dimension.TextPosition ?? dimension.Origin;
				}
				catch
				{
				}
			}
			XYZ anchorMid = (ptA + ptB) * 0.5;
			XYZ pull = dimLineCenter != null ? ProjectToPlane(dimLineCenter - anchorMid, viewNormal) : null;
			double offsetRight = pull?.DotProduct(viewRight) ?? 0.0;
			double offsetUp = pull?.DotProduct(viewUp) ?? 0.0;
			string pullDirection = ClassifyPullDirection(offsetRight, offsetUp);
			records.Add(new CapturedDimensionRecord
			{
				ValueFeet = valueFeet,
				ValueDisplay = dimension.ValueString ?? string.Empty,
				InferredRole = InferDimensionRole(roleA, roleB, chordInView, topology, viewUp, viewRight),
				ChordDirectionInView = NormalizeOrZero(chordInView),
				DimLineDirectionInView = NormalizeOrZero(dimLineDir),
				PullDirection = pullDirection,
				OffsetAlongViewRightFeet = offsetRight,
				OffsetAlongViewUpFeet = offsetUp,
				OffsetSignRight = offsetRight >= 0.0 ? 1 : -1,
				OffsetSignUp = offsetUp >= 0.0 ? 1 : -1,
				RefA = new DimensionEndpointRecord
				{
					Role = roleA.ToString(),
					Point = ptA
				},
				RefB = new DimensionEndpointRecord
				{
					Role = roleB.ToString(),
					Point = ptB
				}
			});
		}
		return records;
	}

	private static string InferDimensionRole(ElementRole roleA, ElementRole roleB, XYZ chordInView, TopologySnapshot topology, XYZ viewUp, XYZ viewRight)
	{
		if (chordInView == null || chordInView.GetLength() < 1E-09)
		{
			return "Unknown";
		}
		chordInView = chordInView.Normalize();
		double alongRun = topology.DominantRunAxisInView != null ? Math.Abs(chordInView.DotProduct(topology.DominantRunAxisInView.Normalize())) : 0.0;
		double alongUp = Math.Abs(chordInView.DotProduct(viewUp));
		double alongRight = Math.Abs(chordInView.DotProduct(viewRight));
		bool branchRoles = (roleA == ElementRole.Olet || roleA == ElementRole.Anvilet || roleA == ElementRole.BranchStub || roleB == ElementRole.Olet || roleB == ElementRole.Anvilet || roleB == ElementRole.BranchStub);
		bool pipeCenter = roleA == ElementRole.PipeRun || roleB == ElementRole.PipeRun;
		if (branchRoles && pipeCenter && alongUp > 0.75)
		{
			return "BranchHeight";
		}
		if (alongUp > 0.85 && (roleA == ElementRole.ElbowOrFitting || roleB == ElementRole.ElbowOrFitting))
		{
			return "VerticalDrop";
		}
		if (alongRun > 0.85)
		{
			if (roleA == ElementRole.Olet || roleB == ElementRole.Olet || roleA == ElementRole.Anvilet || roleB == ElementRole.Anvilet)
			{
				return "RunPickUp";
			}
			return "RunOverall";
		}
		return "Other";
	}

	private static string ClassifyPullDirection(double offsetRight, double offsetUp)
	{
		if (Math.Abs(offsetUp) >= Math.Abs(offsetRight))
		{
			return offsetUp >= 0.0 ? "Up" : "Down";
		}
		return offsetRight >= 0.0 ? "Right" : "Left";
	}

	private static string ClassifyViewGeometry(View view, XYZ viewNormal)
	{
		if (view is View3D)
		{
			return "View3D";
		}
		if (view.ViewType == ViewType.Detail)
		{
			return "Detail";
		}
		XYZ n = viewNormal?.Normalize();
		if (n == null)
		{
			return "Unknown";
		}
		if (Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z))
		{
			return "ElevationFacingX";
		}
		if (Math.Abs(n.Y) >= Math.Abs(n.X) && Math.Abs(n.Y) >= Math.Abs(n.Z))
		{
			return "ElevationFacingY";
		}
		if (Math.Abs(n.Z) >= Math.Abs(n.X) && Math.Abs(n.Z) >= Math.Abs(n.Y))
		{
			return "ElevationFacingZ";
		}
		return view.ViewType.ToString();
	}

	private static bool TryGetReferencePoint(Document doc, View view, Reference reference, HashSet<ElementId> assemblyPartIds, out XYZ point, out ElementRole role)
	{
		point = null;
		role = ElementRole.Other;
		if (reference == null)
		{
			return false;
		}
		Element element = doc.GetElement(reference.ElementId);
		if (element == null)
		{
			return false;
		}
		FabricationPart part = element as FabricationPart;
		if (part != null)
		{
			role = ClassifyPartRole(part);
			point = GetPartCenter(part, view);
			return point != null;
		}
		role = ElementRole.Other;
		BoundingBoxXYZ bb = element.get_BoundingBox(view);
		if (bb != null)
		{
			point = (bb.Min + bb.Max) * 0.5;
			return true;
		}
		return false;
	}

	private static ElementRole ClassifyPartRole(FabricationPart part)
	{
		if (FabricationPartClassification.IsOletPart(part))
		{
			return GetPartCorpus(part).Contains("ANVILET") ? ElementRole.Anvilet : ElementRole.Olet;
		}
		if (FabricationPartClassification.IsFlangePart(part, part.Document))
		{
			return ElementRole.Flange;
		}
		if (IsPipeRunPart(part))
		{
			return ElementRole.PipeRun;
		}
		if (IsFittingLike(part))
		{
			return ElementRole.ElbowOrFitting;
		}
		return ElementRole.Other;
	}

	private static bool IsPipeRunPart(FabricationPart part)
	{
		if (part == null || FabricationPartClassification.IsOletPart(part) || FabricationPartClassification.IsFlangePart(part, part.Document))
		{
			return false;
		}
		LocationCurve lc = part.Location as LocationCurve;
		return lc != null && lc.Curve is Line && GetPipeLength(part) > 1.0 / 24.0;
	}

	private static bool IsFittingLike(FabricationPart part)
	{
		if (part == null || IsPipeRunPart(part))
		{
			return false;
		}
		return GetPartCorpus(part).Contains("ELBOW") || GetPartCorpus(part).Contains("TEE") || GetPartCorpus(part).Contains("CAP");
	}

	private static bool ConnectsToOlet(FabricationPart pipe, List<FabricationPart> parts)
	{
		ConnectorManager manager = pipe?.ConnectorManager;
		if (manager == null)
		{
			return false;
		}
		foreach (Connector connector in manager.Connectors)
		{
			Connector mate = FindConnectedMate(connector, parts, pipe.Id);
			if (mate != null)
			{
				Element mateElement = mate.Owner;
				if (mateElement is FabricationPart matePart && FabricationPartClassification.IsOletPart(matePart))
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool IsCollinearWithAxis(FabricationPart pipe, XYZ axis)
	{
		if (axis == null || !TryGetLineDirection(pipe, out XYZ dir))
		{
			return false;
		}
		return Math.Abs(dir.Normalize().DotProduct(axis.Normalize())) > 0.85;
	}

	private static Connector FindConnectedMate(Connector connector, List<FabricationPart> parts, ElementId selfId)
	{
		if (connector?.Origin == null)
		{
			return null;
		}
		foreach (FabricationPart part in parts)
		{
			if (part.Id == selfId)
			{
				continue;
			}
			ConnectorManager manager = part.ConnectorManager;
			if (manager == null)
			{
				continue;
			}
			foreach (Connector other in manager.Connectors)
			{
				if (other?.Origin != null && connector.Origin.DistanceTo(other.Origin) < 0.05)
				{
					return other;
				}
			}
		}
		return null;
	}

	private static XYZ GetPartCenter(FabricationPart part, View view)
	{
		LocationCurve lc = part?.Location as LocationCurve;
		if (lc?.Curve is Line line)
		{
			try
			{
				return (line.GetEndPoint(0) + line.GetEndPoint(1)) * 0.5;
			}
			catch
			{
			}
		}
		BoundingBoxXYZ bb = part?.get_BoundingBox(view);
		if (bb != null)
		{
			return (bb.Min + bb.Max) * 0.5;
		}
		return null;
	}

	private static double GetPipeLength(FabricationPart part)
	{
		LocationCurve lc = part?.Location as LocationCurve;
		if (lc?.Curve == null || !lc.Curve.IsBound)
		{
			return 0.0;
		}
		try
		{
			return lc.Curve.Length;
		}
		catch
		{
			return 0.0;
		}
	}

	private static bool TryGetLineDirection(FabricationPart part, out XYZ direction)
	{
		direction = null;
		LocationCurve lc = part?.Location as LocationCurve;
		if (lc?.Curve is Line line)
		{
			direction = line.Direction;
			return direction != null && direction.GetLength() > 1E-09;
		}
		return false;
	}

	private static string GetPartCorpus(FabricationPart part)
	{
		return (((Element)part)?.Name ?? string.Empty).ToUpperInvariant();
	}

	private static XYZ ProjectToPlane(XYZ vector, XYZ planeNormal)
	{
		if (vector == null || planeNormal == null)
		{
			return null;
		}
		XYZ n = planeNormal.Normalize();
		return vector - n.Multiply(vector.DotProduct(n));
	}

	private static double[] NormalizeOrZero(XYZ vector)
	{
		if (vector == null || vector.GetLength() < 1E-09)
		{
			return new double[3];
		}
		XYZ n = vector.Normalize();
		return new[]
		{
			Math.Round(n.X, 4),
			Math.Round(n.Y, 4),
			Math.Round(n.Z, 4)
		};
	}

	private static string BucketLength(double feet)
	{
		if (feet < 2.0)
		{
			return "Short";
		}
		if (feet < 8.0)
		{
			return "Medium";
		}
		return "Long";
	}

	private static string AxisKey(XYZ axis)
	{
		if (axis == null || axis.GetLength() < 1E-09)
		{
			return "0,0,0";
		}
		XYZ n = axis.Normalize();
		return Math.Round(n.X, 3).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(n.Y, 3).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(n.Z, 3).ToString(CultureInfo.InvariantCulture);
	}

	private static XYZ ParseAxisKey(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return null;
		}
		string[] parts = key.Split(',');
		if (parts.Length != 3)
		{
			return null;
		}
		if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
			|| !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
			|| !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
		{
			return null;
		}
		return new XYZ(x, y, z);
	}

	private static string ShortHash(string input)
	{
		using (SHA256 sha = SHA256.Create())
		{
			byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
			return BitConverter.ToString(bytes, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
		}
	}

	private static string WriteExampleFile(Document doc, View view, AssemblyInstance assembly, TopologySnapshot topology, List<CapturedDimensionRecord> dimensions, XYZ viewNormal, XYZ viewRight, XYZ viewUp)
	{
		string folder = ResolveExamplesFolder();
		Directory.CreateDirectory(folder);
		string assemblyToken = SanitizeExampleFileToken(assembly?.Name ?? "Assembly");
		string viewToken = SanitizeExampleFileToken(view?.Name ?? "View");
		string fileName = "example_" + assemblyToken + "_" + viewToken + ".json";
		string path = Path.Combine(folder, fileName);
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("{");
		sb.AppendLine("  \"schemaVersion\": " + SchemaVersion + ",");
		sb.AppendLine("  \"capturedAt\": \"" + DateTime.Now.ToString("o") + "\",");
		sb.AppendLine("  \"matchingPolicy\": \"Use topology + viewGeometry fields only. traceOnly is never used for runtime matching.\",");
		sb.AppendLine("  \"traceOnly\": {");
		sb.AppendLine("    \"documentTitle\": " + JsonString(doc.Title) + ",");
		sb.AppendLine("    \"viewName\": " + JsonString(view.Name) + ",");
		sb.AppendLine("    \"assemblyName\": " + JsonString(assembly?.Name ?? string.Empty));
		sb.AppendLine("  },");
		sb.AppendLine("  \"viewGeometry\": {");
		sb.AppendLine("    \"viewType\": " + JsonString(view.ViewType.ToString()) + ",");
		sb.AppendLine("    \"geometryClass\": " + JsonString(topology.ViewGeometryClass) + ",");
		sb.AppendLine("    \"viewDirection\": " + JsonArray(NormalizeOrZero(viewNormal)) + ",");
		sb.AppendLine("    \"viewUp\": " + JsonArray(NormalizeOrZero(viewUp)) + ",");
		sb.AppendLine("    \"viewRight\": " + JsonArray(NormalizeOrZero(viewRight)));
		sb.AppendLine("  },");
		sb.AppendLine("  \"topology\": {");
		sb.AppendLine("    \"topologyHash\": " + JsonString(topology.TopologyHash) + ",");
		sb.AppendLine("    \"pipeRunSegmentCount\": " + topology.PipeRunSegmentCount + ",");
		sb.AppendLine("    \"oletCount\": " + topology.OletCount + ",");
		sb.AppendLine("    \"anviletCount\": " + topology.AnviletCount + ",");
		sb.AppendLine("    \"flangeCount\": " + topology.FlangeCount + ",");
		sb.AppendLine("    \"branchStubCount\": " + topology.BranchStubCount + ",");
		sb.AppendLine("    \"fittingCount\": " + topology.FittingCount + ",");
		sb.AppendLine("    \"hasVerticalDrop\": " + (topology.HasVerticalDrop ? "true" : "false") + ",");
		sb.AppendLine("    \"dominantRunLengthBucket\": " + JsonString(topology.DominantRunLengthBucket) + ",");
		sb.AppendLine("    \"dominantRunAxisInView\": " + JsonArray(NormalizeOrZero(topology.DominantRunAxisInView)));
		sb.AppendLine("  },");
		sb.AppendLine("  \"dimensions\": [");
		for (int i = 0; i < dimensions.Count; i++)
		{
			CapturedDimensionRecord dim = dimensions[i];
			sb.AppendLine("    {");
			sb.AppendLine("      \"valueFeet\": " + dim.ValueFeet.ToString(CultureInfo.InvariantCulture) + ",");
			sb.AppendLine("      \"valueDisplay\": " + JsonString(dim.ValueDisplay) + ",");
			sb.AppendLine("      \"inferredRole\": " + JsonString(dim.InferredRole) + ",");
			sb.AppendLine("      \"chordDirectionInView\": " + JsonArray(dim.ChordDirectionInView) + ",");
			sb.AppendLine("      \"dimLineDirectionInView\": " + JsonArray(dim.DimLineDirectionInView) + ",");
			sb.AppendLine("      \"pullDirection\": " + JsonString(dim.PullDirection) + ",");
			sb.AppendLine("      \"offsetAlongViewRightFeet\": " + dim.OffsetAlongViewRightFeet.ToString(CultureInfo.InvariantCulture) + ",");
			sb.AppendLine("      \"offsetAlongViewUpFeet\": " + dim.OffsetAlongViewUpFeet.ToString(CultureInfo.InvariantCulture) + ",");
			sb.AppendLine("      \"offsetSignRight\": " + dim.OffsetSignRight + ",");
			sb.AppendLine("      \"offsetSignUp\": " + dim.OffsetSignUp + ",");
			sb.AppendLine("      \"refA\": " + JsonEndpoint(dim.RefA) + ",");
			sb.AppendLine("      \"refB\": " + JsonEndpoint(dim.RefB));
			sb.Append("    }");
			sb.AppendLine(i + 1 < dimensions.Count ? "," : string.Empty);
		}
		sb.AppendLine("  ]");
		sb.AppendLine("}");
		File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
		return path;
	}

	private static void AppendIndexEntry(string outputPath, string topologyHash, View view, int dimensionCount)
	{
		string folder = ResolveExamplesFolder();
		Directory.CreateDirectory(folder);
		string indexPath = Path.Combine(folder, "index.jsonl");
		string line = "{"
			+ "\"capturedAt\":\"" + DateTime.Now.ToString("o") + "\","
			+ "\"file\":" + JsonString(Path.GetFileName(outputPath)) + ","
			+ "\"topologyHash\":" + JsonString(topologyHash) + ","
			+ "\"viewType\":" + JsonString(view.ViewType.ToString()) + ","
			+ "\"dimensionCount\":" + dimensionCount
			+ "}";
		File.AppendAllText(indexPath, line + Environment.NewLine, Encoding.UTF8);
	}

	private static string ResolveExamplesFolder()
	{
		string[] candidates = new string[]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spooling-Savant-V3-Exports", "SpoolingManager", "DimensionExamples"),
			Path.Combine(SpoolingManagerSettings.SettingsFolderPath, "DimensionExamples"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", "2024", "Spooling-Savant-V3-Exports", "SpoolingManager", "DimensionExamples")
		};
		foreach (string candidate in candidates)
		{
			try
			{
				Directory.CreateDirectory(candidate);
				string probe = Path.Combine(candidate, ".write_test");
				File.WriteAllText(probe, "ok");
				File.Delete(probe);
				return candidate;
			}
			catch
			{
			}
		}
		return candidates[1];
	}

	private static string SanitizeExampleFileToken(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "Unknown";
		}
		char[] chars = value.Select((char c) => char.IsLetterOrDigit(c) ? c : '_').ToArray();
		string token = new string(chars).Trim('_');
		while (token.Contains("__"))
		{
			token = token.Replace("__", "_");
		}
		return string.IsNullOrWhiteSpace(token) ? "Unknown" : token;
	}

	private static string JsonString(string value)
	{
		if (value == null)
		{
			return "null";
		}
		return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
	}

	private static string JsonArray(double[] values)
	{
		if (values == null || values.Length == 0)
		{
			return "[0,0,0]";
		}
		return "[" + string.Join(",", values.Select((double v) => v.ToString(CultureInfo.InvariantCulture))) + "]";
	}

	private static string JsonEndpoint(DimensionEndpointRecord endpoint)
	{
		return "{"
			+ "\"role\":" + JsonString(endpoint.Role) + ","
			+ "\"point\":" + JsonArray(new[]
			{
				Math.Round(endpoint.Point.X, 4),
				Math.Round(endpoint.Point.Y, 4),
				Math.Round(endpoint.Point.Z, 4)
			})
			+ "}";
	}

	private enum ElementRole
	{
		Other,
		PipeRun,
		Olet,
		Anvilet,
		BranchStub,
		Flange,
		ElbowOrFitting
	}

	private sealed class TopologySnapshot
	{
		public string TopologyHash { get; set; }
		public string ViewGeometryClass { get; set; }
		public int PipeRunSegmentCount { get; set; }
		public int OletCount { get; set; }
		public int AnviletCount { get; set; }
		public int FlangeCount { get; set; }
		public int BranchStubCount { get; set; }
		public int FittingCount { get; set; }
		public bool HasVerticalDrop { get; set; }
		public string DominantRunLengthBucket { get; set; }
		public XYZ DominantRunAxisInView { get; set; }
	}

	private sealed class CapturedDimensionRecord
	{
		public double ValueFeet { get; set; }
		public string ValueDisplay { get; set; }
		public string InferredRole { get; set; }
		public double[] ChordDirectionInView { get; set; }
		public double[] DimLineDirectionInView { get; set; }
		public string PullDirection { get; set; }
		public double OffsetAlongViewRightFeet { get; set; }
		public double OffsetAlongViewUpFeet { get; set; }
		public int OffsetSignRight { get; set; }
		public int OffsetSignUp { get; set; }
		public DimensionEndpointRecord RefA { get; set; }
		public DimensionEndpointRecord RefB { get; set; }
	}

	private sealed class DimensionEndpointRecord
	{
		public string Role { get; set; }
		public XYZ Point { get; set; }
	}
}
