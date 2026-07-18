using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ABMEP.Work;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Writes TigerLink-friendly CSV cut lists split by package and Copper/PVC material family.
/// Straight pipe only — fittings and valves are excluded. LengthInches is decimal inches.
/// </summary>
internal static class TigerStopExportService
{
	internal static List<string> Export(
		Document doc,
		IList<(FabricationPart Part, string AssemblyName)> partsWithAssemblies,
		string packageLabel,
		string outputFolder,
		SpoolingManagerSettings settings)
	{
		List<string> written = new List<string>();
		if (doc == null || partsWithAssemblies == null || partsWithAssemblies.Count == 0 || string.IsNullOrWhiteSpace(outputFolder))
		{
			return written;
		}

		List<string> columns = PlotPackageExportColumns.ParseTigerStopColumns(settings?.TigerStopColumns);
		List<string> copperKeywords = FabricationMaterialKind.ParseKeywords(
			settings?.TigerStopCopperKeywords,
			FabricationMaterialKind.DefaultCopperKeywords);
		List<string> pvcKeywords = FabricationMaterialKind.ParseKeywords(
			settings?.TigerStopPvcKeywords,
			FabricationMaterialKind.DefaultPvcKeywords);

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
		var byFamily = new Dictionary<FabricationMaterialFamily, List<(FabricationPart Part, string AssemblyName)>>();

		foreach ((FabricationPart part, string assemblyName) in partsWithAssemblies)
		{
			if (!IsTigerStopPipeOnly(part, doc))
			{
				continue;
			}

			FabricationMaterialFamily family = FabricationMaterialKind.Resolve(
				part,
				doc,
				copperKeywords,
				pvcKeywords,
				Array.Empty<string>(),
				Array.Empty<string>());
			if (family != FabricationMaterialFamily.Copper && family != FabricationMaterialFamily.Pvc)
			{
				continue;
			}

			if (!byFamily.TryGetValue(family, out List<(FabricationPart Part, string AssemblyName)> list))
			{
				list = new List<(FabricationPart Part, string AssemblyName)>();
				byFamily[family] = list;
			}

			list.Add((part, assemblyName ?? string.Empty));
		}

		foreach (KeyValuePair<FabricationMaterialFamily, List<(FabricationPart Part, string AssemblyName)>> pair in byFamily.OrderBy(x => x.Key))
		{
			string materialLabel = FabricationMaterialKind.DisplayName(pair.Key);
			string fileStem = SanitizeFileStem(package + " - TigerStop - " + materialLabel);
			string path = Path.Combine(outputFolder, fileStem + ".csv");
			WriteCsv(path, package, pair.Value, doc, columns);
			written.Add(path);
		}

		return written;
	}

	private static void WriteCsv(
		string path,
		string package,
		List<(FabricationPart Part, string AssemblyName)> rows,
		Document doc,
		List<string> columns)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
		var sb = new StringBuilder();
		sb.AppendLine(string.Join(",", columns.Select(Csv)));

		foreach ((FabricationPart part, string assemblyName) in rows
			.OrderBy(x => x.AssemblyName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => CreateSpoolSheetsHandler.GetFabricationItemNumber(x.Part), StringComparer.OrdinalIgnoreCase))
		{
			double lengthFt = PipeFittingsBomPdfCommand.GetStraightPipeLengthFeet(part);
			double lengthInches = lengthFt * 12.0;
			string itemNumber = CreateSpoolSheetsHandler.GetFabricationItemNumber(part);
			string size = FabricationPartClassification.GetParamString(part, doc, "S-Size");
			if (string.IsNullOrWhiteSpace(size))
			{
				size = FabricationPartClassification.GetParamString(part, doc, "Product Entry");
			}

			string material = FabricationMaterialKind.GetRawMaterialText(part, doc);
			string lengthFtIn = PipeFittingsBomPdfCommand.FormatLengthFeetInches(lengthFt);

			List<string> values = new List<string>(columns.Count);
			foreach (string column in columns)
			{
				values.Add(ResolveColumn(column, package, assemblyName, itemNumber, size, material, lengthInches, lengthFtIn));
			}

			sb.AppendLine(string.Join(",", values.Select(Csv)));
		}

		File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
	}

	private static string ResolveColumn(
		string column,
		string package,
		string assemblyName,
		string itemNumber,
		string size,
		string material,
		double lengthInches,
		string lengthFtIn)
	{
		if (string.Equals(column, PlotPackageExportColumns.TigerStopQuantity, StringComparison.OrdinalIgnoreCase))
		{
			return 1.ToString(CultureInfo.InvariantCulture);
		}
		if (string.Equals(column, PlotPackageExportColumns.TigerStopLengthInches, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(column, "LengthDecimal", StringComparison.OrdinalIgnoreCase))
		{
			return lengthInches.ToString("0.######", CultureInfo.InvariantCulture);
		}
		if (string.Equals(column, PlotPackageExportColumns.TigerStopPackage, StringComparison.OrdinalIgnoreCase))
		{
			return package;
		}
		if (string.Equals(column, PlotPackageExportColumns.TigerStopItemNumber, StringComparison.OrdinalIgnoreCase))
		{
			return itemNumber;
		}
		if (string.Equals(column, PlotPackageExportColumns.TigerStopSize, StringComparison.OrdinalIgnoreCase))
		{
			return size;
		}
		if (string.Equals(column, PlotPackageExportColumns.TigerStopLengthFtIn, StringComparison.OrdinalIgnoreCase))
		{
			return lengthFtIn;
		}
		if (string.Equals(column, PlotPackageExportColumns.TigerStopMaterial, StringComparison.OrdinalIgnoreCase))
		{
			return material;
		}
		if (string.Equals(column, PlotPackageExportColumns.TigerStopSpool, StringComparison.OrdinalIgnoreCase))
		{
			return assemblyName;
		}

		return string.Empty;
	}

	private static string Csv(string value)
	{
		value ??= string.Empty;
		if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
		{
			return "\"" + value.Replace("\"", "\"\"") + "\"";
		}

		return value;
	}

	/// <summary>
	/// TigerStop cut lists are pipe sticks only — never fittings, valves, flanges, olets, etc.
	/// </summary>
	private static bool IsTigerStopPipeOnly(FabricationPart part, Document doc)
	{
		if (part == null || !FabricationPartClassification.IsStraightPipeRun(part))
		{
			return false;
		}

		if (FabricationPartClassification.IsValvePart(part, doc)
			|| FabricationPartClassification.IsElbowPart(part, doc)
			|| FabricationPartClassification.IsTeePart(part, doc)
			|| FabricationPartClassification.IsReducerPart(part, doc)
			|| FabricationPartClassification.IsFlangePart(part, doc)
			|| FabricationPartClassification.IsOletPart(part))
		{
			return false;
		}

		if (FabricationPartClassification.GetFabricationSortPriority(part, doc) != 0)
		{
			return false;
		}

		string corpus = FabricationPartClassification.GetExpandedSearchCorpus(part, doc);
		if (string.IsNullOrWhiteSpace(corpus))
		{
			return true;
		}

		// Word-boundary checks so "STEEL" does not match "TEE", etc.
		string[] excluded =
		{
			"VALVE", "ELBOW", "TEE", "REDUCER", "FLANGE", "COUPLING", "UNION",
			"CAP", "OLET", "OUTLET", "WYE", "CROSS", "BUSHING", "NIPPLE", "ADAPTER"
		};
		foreach (string token in excluded)
		{
			if (Regex.IsMatch(corpus, @"\b" + Regex.Escape(token) + @"\b", RegexOptions.IgnoreCase))
			{
				return false;
			}
		}

		return true;
	}

	private static string SanitizeFileStem(string stem)
	{
		if (string.IsNullOrWhiteSpace(stem))
		{
			return "TigerStop";
		}

		char[] invalid = Path.GetInvalidFileNameChars();
		var sb = new StringBuilder(stem.Trim().Length);
		foreach (char c in stem.Trim())
		{
			sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
		}

		string cleaned = sb.ToString().Trim();
		return cleaned.Length == 0 ? "TigerStop" : cleaned;
	}
}
