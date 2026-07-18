using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ABMEP.Work;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Writes ISOGEN-style PCF files split by package and material family
/// (Steel, Cast Iron, Copper, PVC, and unclassified Other).
/// Also upserts unique Item Codes into the piping specification catalog workbook.
/// </summary>
internal static class PcfExportService
{
	internal sealed class ExportResult
	{
		internal List<string> WrittenFiles { get; } = new List<string>();
		internal PipingSpecCatalogService.CatalogUpsertResult Catalog { get; set; }
	}

	internal static ExportResult Export(
		Document doc,
		IList<(FabricationPart Part, string AssemblyName)> partsWithAssemblies,
		string packageLabel,
		string outputFolder,
		SpoolingManagerSettings settings)
	{
		var result = new ExportResult();
		if (doc == null || partsWithAssemblies == null || partsWithAssemblies.Count == 0 || string.IsNullOrWhiteSpace(outputFolder))
		{
			return result;
		}

		List<string> copperKeywords = FabricationMaterialKind.ParseKeywords(
			settings?.PcfCopperKeywords,
			FabricationMaterialKind.DefaultCopperKeywords);
		List<string> pvcKeywords = FabricationMaterialKind.ParseKeywords(
			settings?.PcfPvcKeywords,
			FabricationMaterialKind.DefaultPvcKeywords);
		List<string> steelKeywords = FabricationMaterialKind.ParseKeywords(
			settings?.PcfSteelKeywords,
			FabricationMaterialKind.DefaultSteelKeywords);
		List<string> castIronKeywords = FabricationMaterialKind.ParseKeywords(
			FabricationMaterialKind.SanitizeCastIronKeywordsSetting(settings?.PcfCastIronKeywords),
			FabricationMaterialKind.DefaultCastIronKeywords);

		List<FabricationPart> syncParts = partsWithAssemblies
			.Select(entry => entry.Part)
			.Where(part => part != null)
			.Distinct()
			.ToList();
		if (syncParts.Count > 0)
		{
			PipeFittingsBomPdfCommand.EnsureBomParametersAndSyncForExport(doc, syncParts);
		}

		string package = string.IsNullOrWhiteSpace(packageLabel) ? "Package" : packageLabel.Trim();
		var classified = new List<(FabricationPart Part, string AssemblyName, FabricationMaterialFamily Family, bool IsWeld)>();

		foreach ((FabricationPart part, string assemblyName) in partsWithAssemblies)
		{
			if (part == null)
			{
				continue;
			}

			// Shop welds belong in the PCF so import can recreate joints. Still skip gaskets/bolts.
			bool isWeld = FabricationPartClassification.IsWeldPart(part);
			if (!isWeld && FabricationPartClassification.IsIgnoredForSpoolDimensioning(part, doc))
			{
				continue;
			}

			if (FabricationPartClassification.IsGasketPart(part) || FabricationPartClassification.IsBoltKitPart(part))
			{
				continue;
			}

			FabricationMaterialFamily family = FabricationMaterialKind.Resolve(
				part,
				doc,
				copperKeywords,
				pvcKeywords,
				steelKeywords,
				castIronKeywords);
			classified.Add((part, assemblyName ?? string.Empty, family, isWeld));
		}

		// Welds / blank-material fittings inherit their spool's known family so they stay in the same PCF.
		var assemblyFamily = new Dictionary<string, FabricationMaterialFamily>(StringComparer.OrdinalIgnoreCase);
		foreach ((FabricationPart _, string assemblyName, FabricationMaterialFamily family, bool isWeld) in classified)
		{
			if (isWeld || family == FabricationMaterialFamily.Unknown || string.IsNullOrWhiteSpace(assemblyName))
			{
				continue;
			}

			if (!assemblyFamily.ContainsKey(assemblyName))
			{
				assemblyFamily[assemblyName] = family;
			}
		}

		var byFamily = new Dictionary<FabricationMaterialFamily, List<(FabricationPart Part, string AssemblyName)>>();
		foreach ((FabricationPart part, string assemblyName, FabricationMaterialFamily resolved, bool _) in classified)
		{
			FabricationMaterialFamily family = resolved;
			if (family == FabricationMaterialFamily.Unknown
				&& !string.IsNullOrWhiteSpace(assemblyName)
				&& assemblyFamily.TryGetValue(assemblyName, out FabricationMaterialFamily fromAssembly))
			{
				family = fromAssembly;
			}

			if (!byFamily.TryGetValue(family, out List<(FabricationPart Part, string AssemblyName)> list))
			{
				list = new List<(FabricationPart Part, string AssemblyName)>();
				byFamily[family] = list;
			}

			list.Add((part, assemblyName));
		}

		List<FabricationPart> catalogParts = new List<FabricationPart>();
		foreach (KeyValuePair<FabricationMaterialFamily, List<(FabricationPart Part, string AssemblyName)>> pair in byFamily.OrderBy(x => x.Key))
		{
			string materialLabel = pair.Key == FabricationMaterialFamily.Unknown
				? "Other"
				: FabricationMaterialKind.DisplayName(pair.Key);
			string fileStem = SanitizeFileStem(package + " - PCF - " + materialLabel);
			string path = Path.Combine(outputFolder, fileStem + ".pcf");
			List<string> fields = PlotPackageExportColumns.ParsePcfFields(settings?.PcfFields);
			WritePcf(path, package, materialLabel, pair.Value, doc, fields);
			result.WrittenFiles.Add(path);
			catalogParts.AddRange(pair.Value.Select(entry => entry.Part));
		}

		// Stale Cast Iron PCFs from older misclassification confuse re-imports — remove when this run has none.
		string staleCastIron = Path.Combine(
			outputFolder,
			SanitizeFileStem(package + " - PCF - " + FabricationMaterialKind.DisplayName(FabricationMaterialFamily.CastIron)) + ".pcf");
		if (!byFamily.ContainsKey(FabricationMaterialFamily.CastIron) && File.Exists(staleCastIron))
		{
			try
			{
				File.Delete(staleCastIron);
			}
			catch
			{
			}
		}

		if (catalogParts.Count > 0)
		{
			result.Catalog = PipingSpecCatalogService.UpsertFromParts(doc, catalogParts, outputFolder, settings);
		}

		return result;
	}

	private static void WritePcf(
		string path,
		string package,
		string materialLabel,
		List<(FabricationPart Part, string AssemblyName)> rows,
		Document doc,
		List<string> fields)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
		var sb = new StringBuilder();
		sb.AppendLine("ISOGEN-FILES");
		sb.AppendLine("UNITS-BORE Inch");
		sb.AppendLine("UNITS-CO-ORDS Feet");
		sb.AppendLine("UNITS-WEIGHT Pounds");
		sb.AppendLine("PIPELINE-REFERENCE " + SanitizeToken(package + "-" + materialLabel));
		if (fields.Any(f => string.Equals(f, PlotPackageExportColumns.PcfPipingSpec, StringComparison.OrdinalIgnoreCase)))
		{
			sb.AppendLine("    PIPING-SPEC " + SanitizeToken(materialLabel));
		}
		sb.AppendLine("    DATE-DMY " + DateTime.Now.ToString("dd-MMM-yy", CultureInfo.InvariantCulture));

		int componentIndex = 0;
		foreach ((FabricationPart part, string assemblyName) in rows
			.OrderBy(x => x.AssemblyName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => CreateSpoolSheetsHandler.GetFabricationItemNumber(x.Part), StringComparer.OrdinalIgnoreCase))
		{
			componentIndex++;
			AppendComponent(sb, doc, part, assemblyName, materialLabel, componentIndex, fields);
		}

		File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
	}

	private static void AppendComponent(
		StringBuilder sb,
		Document doc,
		FabricationPart part,
		string assemblyName,
		string materialLabel,
		int componentIndex,
		List<string> fields)
	{
		string componentType = ResolveComponentType(part, doc);
		string itemNumber = CreateSpoolSheetsHandler.GetFabricationItemNumber(part);
		string size = ResolveSizeText(part, doc);
		double boreInches = ResolveBoreInches(part, doc, size);

		List<XYZ> ends = GetComponentEndPoints(part, componentType);
		sb.AppendLine(componentType);

		// Always write size for round-trip import (even if not in the selected field list).
		if (boreInches > 1e-6)
		{
			sb.AppendLine("    NOMINAL-SIZE " + boreInches.ToString("0.###", CultureInfo.InvariantCulture));
			if (!string.IsNullOrWhiteSpace(size))
			{
				sb.AppendLine("    PRODUCT-ENTRY " + SanitizeProductEntry(size));
			}
		}
		else if (!string.IsNullOrWhiteSpace(size))
		{
			sb.AppendLine("    NOMINAL-SIZE " + SanitizeProductEntry(size));
			sb.AppendLine("    PRODUCT-ENTRY " + SanitizeProductEntry(size));
		}

		// Prefer the catalog figure (Fig.604-2 / No604-2) so import can pick the same palette button.
		string itemCode = FirstNonBlank(
			itemNumber,
			SafePartSizeProperty(() => part.ProductName),
			SafePartSizeProperty(() => part.Name),
			FabricationPartClassification.GetParamString(part, doc, "Alias"));

		if (!string.IsNullOrWhiteSpace(itemCode))
		{
			sb.AppendLine("    ITEM-CODE " + SanitizeToken(itemCode));
		}

		string description = FirstNonBlank(
			SafePartSizeProperty(() => part.ProductName),
			SafePartSizeProperty(() => part.Name),
			itemNumber);
		if (!string.IsNullOrWhiteSpace(description)
			&& !string.Equals(SanitizeToken(description), SanitizeToken(itemCode ?? string.Empty), StringComparison.OrdinalIgnoreCase))
		{
			sb.AppendLine("    DESCRIPTION " + SanitizeToken(description));
		}

		bool writeEndPoints = false;
		foreach (string field in fields)
		{
			if (string.Equals(field, PlotPackageExportColumns.PcfComponentId, StringComparison.OrdinalIgnoreCase))
			{
				sb.AppendLine("    COMPONENT-IDENTIFIER " + componentIndex.ToString(CultureInfo.InvariantCulture));
			}
			else if (string.Equals(field, PlotPackageExportColumns.PcfItemCode, StringComparison.OrdinalIgnoreCase))
			{
				// Already written above for round-trip matching.
			}
			else if (string.Equals(field, PlotPackageExportColumns.PcfSkey, StringComparison.OrdinalIgnoreCase))
			{
				sb.AppendLine("    SKEY " + SanitizeToken(componentType));
			}
			else if (string.Equals(field, PlotPackageExportColumns.PcfPipingSpec, StringComparison.OrdinalIgnoreCase))
			{
				sb.AppendLine("    PIPING-SPEC " + SanitizeToken(materialLabel));
			}
			else if (string.Equals(field, PlotPackageExportColumns.PcfSpoolId, StringComparison.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrWhiteSpace(assemblyName))
				{
					sb.AppendLine("    SPOOL-ID " + SanitizeToken(assemblyName));
				}
			}
			else if (string.Equals(field, PlotPackageExportColumns.PcfEndPoint, StringComparison.OrdinalIgnoreCase))
			{
				// Geometry is written once after the field loop so tees always get every port.
				writeEndPoints = true;
			}
		}

		// Always emit END-POINTs when requested (same as NOMINAL-SIZE — required for import).
		if (writeEndPoints || fields.Count == 0)
		{
			AppendComponentEndPoints(sb, part, componentType, ends, boreInches);
		}
	}

	/// <summary>
	/// Writes connector END-POINTs (and CENTRE-POINT for elbows/tees). Tees must export every
	/// End port; flanges export hub then face when connectivity is known.
	/// </summary>
	private static void AppendComponentEndPoints(
		StringBuilder sb,
		FabricationPart part,
		string componentType,
		List<XYZ> fallbackEnds,
		double fallbackBoreInches)
	{
		if (string.Equals(componentType, "TEE", StringComparison.OrdinalIgnoreCase))
		{
			List<(XYZ Point, double BoreInches)> teePorts = GetTeePortEnds(part, fallbackBoreInches);
			if (teePorts.Count >= 2)
			{
				foreach ((XYZ point, double bore) in teePorts)
				{
					AppendEndPoint(sb, point, bore > 1e-6 ? bore : fallbackBoreInches);
				}

				XYZ teeCentre = GetFittingCentreFromEndConnectors(part);
				if (teeCentre != null)
				{
					AppendCentrePoint(sb, teeCentre);
				}

				return;
			}
		}

		// ISOGEN: elbows require CENTRE-POINT (bend intersection) between/after END-POINTs.
		if (string.Equals(componentType, "ELBOW", StringComparison.OrdinalIgnoreCase))
		{
			// Always write C1 then C2 (Connector.Id) — never XYZ-sorted origins.
			List<(XYZ Point, double BoreInches)> elbowPorts = GetTeePortEnds(part, fallbackBoreInches);
			if (elbowPorts.Count >= 2)
			{
				AppendEndPoint(sb, elbowPorts[0].Point, elbowPorts[0].BoreInches > 1e-6 ? elbowPorts[0].BoreInches : fallbackBoreInches);
				AppendEndPoint(sb, elbowPorts[1].Point, elbowPorts[1].BoreInches > 1e-6 ? elbowPorts[1].BoreInches : fallbackBoreInches);
				XYZ centre = GetElbowCentrePoint(part);
				if (centre != null)
				{
					AppendCentrePoint(sb, centre);
				}

				return;
			}
		}

		if (string.Equals(componentType, "FLANGE", StringComparison.OrdinalIgnoreCase))
		{
			if (TryGetFlangeHubThenFace(part, out XYZ hub, out XYZ face))
			{
				AppendEndPoint(sb, hub, fallbackBoreInches);
				AppendEndPoint(sb, face, fallbackBoreInches);
				return;
			}
		}

		// Reducers / adapters / couplings: C1 then C2 with their own bores.
		if (string.Equals(componentType, "REDUCER-CONCENTRIC", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(componentType, "REDUCER-ECCENTRIC", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(componentType, "ADAPTER", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(componentType, "COUPLING", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(componentType, "MISC-COMPONENT", StringComparison.OrdinalIgnoreCase))
		{
			List<(XYZ Point, double BoreInches)> ports = GetTeePortEnds(part, fallbackBoreInches);
			if (ports.Count >= 2)
			{
				int limit = Math.Min(2, ports.Count);
				for (int i = 0; i < limit; i++)
				{
					AppendEndPoint(
						sb,
						ports[i].Point,
						ports[i].BoreInches > 1e-6 ? ports[i].BoreInches : fallbackBoreInches);
				}

				return;
			}
		}

		List<XYZ> writeEnds = fallbackEnds ?? new List<XYZ>();
		if (writeEnds.Count == 0)
		{
			writeEnds = GetTeePortEnds(part, fallbackBoreInches).Select(p => p.Point).ToList();
		}

		if (writeEnds.Count >= 2)
		{
			int limit = Math.Min(2, writeEnds.Count);
			for (int i = 0; i < limit; i++)
			{
				AppendEndPoint(sb, writeEnds[i], fallbackBoreInches);
			}
		}
		else if (writeEnds.Count == 1)
		{
			AppendEndPoint(sb, writeEnds[0], fallbackBoreInches);
			XYZ origin = GetPartOrigin(part) ?? writeEnds[0];
			AppendEndPoint(sb, origin, fallbackBoreInches);
		}
		else
		{
			XYZ origin = GetPartOrigin(part) ?? XYZ.Zero;
			AppendEndPoint(sb, origin, fallbackBoreInches);
			AppendEndPoint(sb, origin + new XYZ(0.1, 0.0, 0.0), fallbackBoreInches);
		}
	}

	private static void AppendEndPoint(StringBuilder sb, XYZ point, double boreInches)
	{
		sb.Append("    END-POINT ");
		sb.Append(FormatCoord(point.X)).Append(" ");
		sb.Append(FormatCoord(point.Y)).Append(" ");
		sb.Append(FormatCoord(point.Z)).Append(" ");
		sb.AppendLine(boreInches.ToString("0.###", CultureInfo.InvariantCulture));
	}

	private static void AppendCentrePoint(StringBuilder sb, XYZ point)
	{
		sb.Append("    CENTRE-POINT ");
		sb.Append(FormatCoord(point.X)).Append(" ");
		sb.Append(FormatCoord(point.Y)).Append(" ");
		sb.AppendLine(FormatCoord(point.Z));
	}

	/// <summary>
	/// Bend centre = intersection of the two inward face axes (ISOGEN CENTRE-POINT).
	/// </summary>
	private static XYZ GetElbowCentrePoint(FabricationPart part)
	{
		return GetFittingCentreFromEndConnectors(part);
	}

	private static XYZ GetFittingCentreFromEndConnectors(FabricationPart part)
	{
		List<Connector> ends = FabricationConnectorEnds.GetEndConnectorsById(part);
		if (ends.Count < 2)
		{
			return null;
		}

		XYZ o0 = ends[0].Origin;
		XYZ o1 = ends[1].Origin;
		XYZ in0 = TryGetConnectorInward(ends[0]);
		XYZ in1 = TryGetConnectorInward(ends[1]);
		if (in0 == null || in1 == null)
		{
			return null;
		}

		return TryIntersectRays(o0, in0, o1, in1);
	}

	private static XYZ TryGetConnectorInward(Connector connector)
	{
		try
		{
			XYZ outward = connector?.CoordinateSystem?.BasisZ;
			if (outward != null && outward.GetLength() > 1e-9)
			{
				return outward.Normalize().Negate();
			}
		}
		catch
		{
		}

		return null;
	}

	/// <summary>Closest intersection of rays origin0+s*dir0 and origin1+t*dir1 (s,t >= 0 preferred).</summary>
	private static XYZ TryIntersectRays(XYZ origin0, XYZ dir0, XYZ origin1, XYZ dir1)
	{
		if (origin0 == null || dir0 == null || origin1 == null || dir1 == null)
		{
			return null;
		}

		XYZ d0 = dir0.Normalize();
		XYZ d1 = dir1.Normalize();
		XYZ w0 = origin0 - origin1;
		double a = d0.DotProduct(d0);
		double b = d0.DotProduct(d1);
		double c = d1.DotProduct(d1);
		double d = d0.DotProduct(w0);
		double e = d1.DotProduct(w0);
		double denom = a * c - b * b;
		double s;
		double t;
		if (Math.Abs(denom) < 1e-12)
		{
			// Parallel — fall back to average of face points offset by mean takeout.
			s = 0;
			t = (b > c ? d / b : e / c);
		}
		else
		{
			s = (b * e - c * d) / denom;
			t = (a * e - b * d) / denom;
		}

		XYZ p0 = origin0 + d0.Multiply(s);
		XYZ p1 = origin1 + d1.Multiply(t);
		return (p0 + p1) * 0.5;
	}

	/// <summary>
	/// Hub end = connector linked to pipe/elbow/weld (or the non-flange neighbor). Face = the other end.
	/// </summary>
	private static bool TryGetFlangeHubThenFace(FabricationPart part, out XYZ hub, out XYZ face)
	{
		hub = null;
		face = null;
		List<Connector> ends = new List<Connector>();
		try
		{
			foreach (Connector connector in part.ConnectorManager.Connectors)
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				try
				{
					if (connector.ConnectorType != ConnectorType.End)
					{
						continue;
					}
				}
				catch
				{
				}

				ends.Add(connector);
			}
		}
		catch
		{
			return false;
		}

		if (ends.Count < 2)
		{
			return false;
		}

		Connector hubConn = null;
		Connector faceConn = null;
		foreach (Connector connector in ends)
		{
			bool toPipeFit = false;
			bool toFlange = false;
			bool toOther = false;
			try
			{
				if (connector.IsConnected)
				{
					foreach (Connector r in connector.AllRefs)
					{
						if (r?.Owner == null || r.Owner.Id == part.Id)
						{
							continue;
						}

						string name = ((r.Owner as FabricationPart)?.ProductName ?? r.Owner.Name ?? string.Empty)
							.ToUpperInvariant();
						if (name.Contains("FLANGE") || name.Contains("GASKET") || name.Contains("BOLT"))
						{
							toFlange = true;
						}
						else if (name.Contains("PIPE") || name.Contains("WELD") || name.Contains("ELBOW")
							|| name.Contains("TEE") || name.Contains("FITTING") || name.Contains("COPPER")
							|| name.Contains("TUBE") || name.Contains("PVC") || name.Contains("COUPLING")
							|| name.Contains("ADAPTER") || name.Contains("REDUCER") || name.Contains("UNION"))
						{
							toPipeFit = true;
						}
						else
						{
							toOther = true;
						}
					}
				}
			}
			catch
			{
			}

			// Hub = toward the run (pipe/fitting). Face = toward mate flange / free end.
			if ((toPipeFit || toOther) && !toFlange)
			{
				hubConn = connector;
			}
			else if (toFlange && !toPipeFit)
			{
				faceConn = connector;
			}
		}

		if (hubConn != null && faceConn != null && !ReferenceEquals(hubConn, faceConn))
		{
			hub = hubConn.Origin;
			face = faceConn.Origin;
			return true;
		}

		if (hubConn != null)
		{
			hub = hubConn.Origin;
			face = ends.First(c => !ReferenceEquals(c, hubConn)).Origin;
			return true;
		}

		if (faceConn != null)
		{
			face = faceConn.Origin;
			hub = ends.First(c => !ReferenceEquals(c, faceConn)).Origin;
			return true;
		}

		return false;
	}

	private static string ResolveComponentType(FabricationPart part, Document doc)
	{
		if (FabricationPartClassification.IsWeldPart(part))
		{
			return "WELD";
		}

		// Fitting keywords before IsStraightPipeRun — copper couplings/adapters/reducers often
		// carry a Length dim and were falsely exported as PIPE.
		string corpus = string.Join(
			" ",
			part.Name ?? string.Empty,
			FabricationPartClassification.GetParamString(part, doc, "Alias"),
			FabricationPartClassification.GetParamString(part, doc, "Product Entry"),
			FabricationPartClassification.GetParamString(part, doc, "Product Long Description"),
			FabricationPartClassification.GetParamString(part, doc, "Description"),
			FabricationPartClassification.GetParamString(part, doc, "eM_Fitting Type")).ToUpperInvariant();

		if (FabricationPartClassification.IsElbowPart(part, doc)
			|| corpus.Contains("ELBOW")
			|| corpus.Contains(" ELL ")
			|| corpus.EndsWith(" ELL", StringComparison.Ordinal))
		{
			return "ELBOW";
		}

		if (FabricationPartClassification.IsTeePart(part, doc))
		{
			return "TEE";
		}

		if (FabricationPartClassification.IsReducerPart(part, doc) || corpus.Contains("REDUCER"))
		{
			return "REDUCER-CONCENTRIC";
		}

		if ((corpus.Contains("CAP") && !corpus.Contains("CAPACITY") && !corpus.Contains("CAPTURE"))
			|| corpus.Contains("END CAP"))
		{
			return "CAP";
		}

		if (corpus.Contains("COUPLING") || corpus.Contains("UNION") || corpus.Contains("NIPPLE"))
		{
			return "COUPLING";
		}

		// "Flange Adapter" stays FLANGE (hub/face span). Plain adapters are ADAPTER.
		if (corpus.Contains("ADAPTER") && !corpus.Contains("FLANGE"))
		{
			return "ADAPTER";
		}

		if (FabricationPartClassification.IsFlangePart(part, doc) || corpus.Contains("FLANGE"))
		{
			return "FLANGE";
		}

		if (FabricationPartClassification.IsValvePart(part, doc))
		{
			return "VALVE";
		}

		if (FabricationPartClassification.IsOletPart(part))
		{
			return "OLET";
		}

		if (FabricationPartClassification.IsStraightPipeRun(part) || part.IsAStraight())
		{
			return "PIPE";
		}

		return "MISC-COMPONENT";
	}

	private static List<XYZ> GetAllEndConnectorOrigins(FabricationPart part)
	{
		return GetTeePortEnds(part, 0)
			.Select(port => port.Point)
			.ToList();
	}

	/// <summary>
	/// Every fabrication End port in Edit Part C1/C2/… order (Connector.Id), with bore from radius.
	/// </summary>
	private static List<(XYZ Point, double BoreInches)> GetTeePortEnds(FabricationPart part, double fallbackBoreInches)
	{
		var ports = new List<(XYZ Point, double BoreInches)>();
		foreach (Connector connector in FabricationConnectorEnds.GetEndConnectorsById(part))
		{
			if (connector?.Origin == null)
			{
				continue;
			}

			double bore = fallbackBoreInches;
			try
			{
				if (connector.Radius > 1e-9)
				{
					// Connector.Radius is feet; PCF END-POINT bore is nominal diameter inches.
					bore = connector.Radius * 24.0;
				}
			}
			catch
			{
			}

			ports.Add((connector.Origin, bore > 1e-6 ? bore : fallbackBoreInches));
		}

		return ports;
	}

	private static void AddOrMergePort(List<(XYZ Point, double BoreInches)> ports, XYZ point, double boreInches)
	{
		if (ports == null || point == null)
		{
			return;
		}

		for (int i = 0; i < ports.Count; i++)
		{
			if (ports[i].Point.DistanceTo(point) <= 0.01)
			{
				if (boreInches > ports[i].BoreInches)
				{
					ports[i] = (ports[i].Point, boreInches);
				}

				return;
			}
		}

		ports.Add((point, boreInches));
	}

	/// <summary>
	/// Pipe runs often have mid-span tap connectors from olets. Writing the first two
	/// sorted origins truncates the PCF to the first olet — use true End connectors,
	/// or the farthest pair when End typing is unavailable.
	/// </summary>
	private static List<XYZ> GetComponentEndPoints(FabricationPart part, string componentType)
	{
		if (string.Equals(componentType, "TEE", StringComparison.OrdinalIgnoreCase))
		{
			List<(XYZ Point, double BoreInches)> teePorts = GetTeePortEnds(part, 0);
			if (teePorts.Count >= 3)
			{
				return teePorts.Select(p => p.Point).ToList();
			}

			List<XYZ> teeEnds = GetAllEndConnectorOrigins(part);
			if (teeEnds.Count >= 3)
			{
				return teeEnds;
			}
		}

		bool preferRunEnds = string.Equals(componentType, "PIPE", StringComparison.OrdinalIgnoreCase)
			|| FabricationPartClassification.IsStraightPipeRun(part);

		if (preferRunEnds)
		{
			List<XYZ> runEnds = GetStraightRunEndPoints(part);
			if (runEnds.Count >= 2)
			{
				return runEnds;
			}
		}

		return GetConnectorOrigins(part);
	}

	private static List<XYZ> GetStraightRunEndPoints(FabricationPart part)
	{
		List<XYZ> endTyped = new List<XYZ>();
		List<XYZ> all = new List<XYZ>();
		try
		{
			ConnectorManager manager = part?.ConnectorManager;
			if (manager?.Connectors == null)
			{
				return endTyped;
			}

			foreach (Connector connector in manager.Connectors)
			{
				if (connector?.Origin == null)
				{
					continue;
				}

				all.Add(connector.Origin);
				try
				{
					if (connector.ConnectorType == ConnectorType.End)
					{
						endTyped.Add(connector.Origin);
					}
				}
				catch
				{
					endTyped.Add(connector.Origin);
				}
			}
		}
		catch
		{
		}

		List<XYZ> source = endTyped.Count >= 2 ? endTyped : all;
		return PickFarthestPair(source);
	}

	private static List<XYZ> PickFarthestPair(IList<XYZ> points)
	{
		if (points == null || points.Count == 0)
		{
			return new List<XYZ>();
		}

		if (points.Count == 1)
		{
			return new List<XYZ> { points[0] };
		}

		if (points.Count == 2)
		{
			return points
				.OrderBy(p => p.X)
				.ThenBy(p => p.Y)
				.ThenBy(p => p.Z)
				.ToList();
		}

		double best = -1.0;
		XYZ a = points[0];
		XYZ b = points[1];
		for (int i = 0; i < points.Count; i++)
		{
			for (int j = i + 1; j < points.Count; j++)
			{
				double d = points[i].DistanceTo(points[j]);
				if (d > best)
				{
					best = d;
					a = points[i];
					b = points[j];
				}
			}
		}

		return new List<XYZ> { a, b }
			.OrderBy(p => p.X)
			.ThenBy(p => p.Y)
			.ThenBy(p => p.Z)
			.ToList();
	}

	private static List<XYZ> GetConnectorOrigins(FabricationPart part)
	{
		List<XYZ> points = new List<XYZ>();
		try
		{
			ConnectorManager manager = part?.ConnectorManager;
			if (manager?.Connectors == null)
			{
				return points;
			}

			foreach (Connector connector in manager.Connectors)
			{
				if (connector == null || connector.Origin == null)
				{
					continue;
				}

				points.Add(connector.Origin);
			}
		}
		catch
		{
		}

		return points
			.OrderBy(p => p.X)
			.ThenBy(p => p.Y)
			.ThenBy(p => p.Z)
			.ToList();
	}

	private static XYZ GetPartOrigin(FabricationPart part)
	{
		try
		{
			Location loc = ((Element)part).Location;
			if (loc is LocationPoint locationPoint && locationPoint.Point != null)
			{
				return locationPoint.Point;
			}

			if (loc is LocationCurve locationCurve && locationCurve.Curve != null)
			{
				return locationCurve.Curve.Evaluate(0.0, true);
			}
		}
		catch
		{
		}

		return null;
	}

	private static string ResolveSizeText(FabricationPart part, Document doc)
	{
		if (part == null)
		{
			return string.Empty;
		}

		foreach (string candidate in new[]
		{
			SafePartSizeProperty(() => part.ProductSizeDescription),
			SafePartSizeProperty(() => part.Size),
			SafePartSizeProperty(() => part.FreeSize),
			FabricationPartClassification.GetParamString(part, doc, "S-Size"),
			FabricationPartClassification.GetParamString(part, doc, "Product Entry"),
			FabricationPartClassification.GetParamString(part, doc, "Size"),
			FabricationPartClassification.GetParamString(part, doc, "Product Size Description")
		})
		{
			if (!string.IsNullOrWhiteSpace(candidate) && PcfParser.ParseSizeInches(candidate) > 1e-6)
			{
				return candidate.Trim();
			}
		}

		foreach (string candidate in new[]
		{
			SafePartSizeProperty(() => part.ProductSizeDescription),
			SafePartSizeProperty(() => part.Size),
			FabricationPartClassification.GetParamString(part, doc, "S-Size"),
			FabricationPartClassification.GetParamString(part, doc, "Product Entry")
		})
		{
			if (!string.IsNullOrWhiteSpace(candidate))
			{
				return candidate.Trim();
			}
		}

		return string.Empty;
	}

	private static string SafePartSizeProperty(Func<string> getter)
	{
		try
		{
			return getter()?.Trim() ?? string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string FirstNonBlank(params string[] values)
	{
		if (values == null)
		{
			return string.Empty;
		}

		foreach (string value in values)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value.Trim();
			}
		}

		return string.Empty;
	}

	private static double ResolveBoreInches(FabricationPart part, Document doc, string sizeText)
	{
		double boreInches = PcfParser.ParseSizeInches(sizeText);
		if (boreInches > 1e-6)
		{
			return boreInches;
		}

		boreInches = TryGetMainPrimaryDiameterInches(part, doc);
		if (boreInches > 1e-6)
		{
			return boreInches;
		}

		return TryGetConnectorBoreInches(part);
	}

	private static double TryGetMainPrimaryDiameterInches(FabricationPart part, Document doc)
	{
		try
		{
			Parameter parameter = ((Element)part)?.LookupParameter("Main Primary Diameter");
			if (parameter == null && doc != null)
			{
				ElementId typeId = ((Element)part).GetTypeId();
				if (typeId != null && typeId != ElementId.InvalidElementId)
				{
					parameter = doc.GetElement(typeId)?.LookupParameter("Main Primary Diameter");
				}
			}

			if (parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Double)
			{
				// Revit length is feet.
				return parameter.AsDouble() * 12.0;
			}

			string asText = FabricationPartClassification.GetParamString(part, doc, "Main Primary Diameter");
			return PcfParser.ParseSizeInches(asText);
		}
		catch
		{
			return 0.0;
		}
	}

	private static double TryGetConnectorBoreInches(FabricationPart part)
	{
		try
		{
			ConnectorManager manager = part?.ConnectorManager;
			if (manager?.Connectors == null)
			{
				return 0.0;
			}

			double best = 0.0;
			foreach (Connector connector in manager.Connectors)
			{
				if (connector == null)
				{
					continue;
				}

				try
				{
					// Connector radius is in feet.
					double diameterInches = connector.Radius * 2.0 * 12.0;
					if (diameterInches > best)
					{
						best = diameterInches;
					}
				}
				catch
				{
				}
			}

			return best;
		}
		catch
		{
			return 0.0;
		}
	}

	private static string FormatCoord(double value)
	{
		return value.ToString("0.000000", CultureInfo.InvariantCulture);
	}

	private static string SanitizeProductEntry(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "UNKNOWN";
		}

		// Keep size punctuation so "6x3/4\"" round-trips; only strip control/path junk.
		var sb = new StringBuilder(value.Trim().Length);
		foreach (char c in value.Trim())
		{
			if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '/' || c == 'x' || c == 'X' || c == '"' || c == '\'')
			{
				sb.Append(c);
			}
			else if (char.IsWhiteSpace(c))
			{
				sb.Append(' ');
			}
		}

		string cleaned = sb.ToString().Trim();
		return cleaned.Length == 0 ? "UNKNOWN" : cleaned;
	}

	private static string SanitizeToken(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "UNKNOWN";
		}

		var sb = new StringBuilder(value.Trim().Length);
		foreach (char c in value.Trim())
		{
			if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
			{
				sb.Append(c);
			}
			else if (char.IsWhiteSpace(c))
			{
				sb.Append('-');
			}
		}

		string cleaned = sb.ToString();
		return cleaned.Length == 0 ? "UNKNOWN" : cleaned;
	}

	private static string SanitizeFileStem(string stem)
	{
		if (string.IsNullOrWhiteSpace(stem))
		{
			return "PCF";
		}

		char[] invalid = Path.GetInvalidFileNameChars();
		var sb = new StringBuilder(stem.Trim().Length);
		foreach (char c in stem.Trim())
		{
			sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
		}

		string cleaned = sb.ToString().Trim();
		return cleaned.Length == 0 ? "PCF" : cleaned;
	}
}
