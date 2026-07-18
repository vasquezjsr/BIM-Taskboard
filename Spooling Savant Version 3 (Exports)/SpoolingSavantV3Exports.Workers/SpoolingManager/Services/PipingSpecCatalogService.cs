using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Maintains a piping specification / catalog workbook. New unique Item Codes from PCF exports
/// are appended; existing codes are skipped. Uses Zip+XML xlsx (no ClosedXML/OpenXml).
/// </summary>
internal static class PipingSpecCatalogService
{
	internal const string DefaultFileName = "Piping Specification Catalog.xlsx";
	internal const string SheetName = "Catalog";

	private static readonly string[] Headers =
	{
		"Item Code",
		"Type",
		"Nominal Size",
		"OD",
		"Wall",
		"ID",
		"Schedule",
		"Material",
		"Spec"
	};

	private static readonly double[] ColumnWidths =
	{
		28, 12, 14, 12, 12, 12, 10, 18, 12
	};

	/// <summary>Default catalog under Documents\Spooling Savant Test Reports (OneDrive-aware via MyDocuments).</summary>
	internal static string DefaultCatalogPath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
			"Spooling Savant Test Reports",
			DefaultFileName);

	internal static string ResolveCatalogPath(SpoolingManagerSettings settings)
	{
		string configured = settings?.PipingSpecCatalogPath?.Trim();
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return Path.GetFullPath(configured);
		}

		return Path.GetFullPath(DefaultCatalogPath);
	}

	/// <summary>
	/// Upserts catalog rows for the given fabrication parts into the settings catalog path
	/// and, when different, into the PCF output folder catalog.
	/// </summary>
	internal static CatalogUpsertResult UpsertFromParts(
		Document doc,
		IEnumerable<FabricationPart> parts,
		string outputFolder,
		SpoolingManagerSettings settings)
	{
		string primary = ResolveCatalogPath(settings);
		try
		{
			List<CatalogRow> rows = BuildRows(doc, parts, settings).ToList();
			if (rows.Count == 0)
			{
				EnsureWorkbook(primary);
				return new CatalogUpsertResult(primary, 0, 0, null);
			}

			int primaryAdded = UpsertRows(primary, rows);
			int packageAdded = 0;

			if (!string.IsNullOrWhiteSpace(outputFolder))
			{
				string packageCatalog = Path.GetFullPath(Path.Combine(outputFolder, DefaultFileName));
				if (!string.Equals(primary, packageCatalog, StringComparison.OrdinalIgnoreCase))
				{
					packageAdded = UpsertRows(packageCatalog, rows);
				}
			}

			return new CatalogUpsertResult(primary, primaryAdded, packageAdded, null);
		}
		catch (Exception ex)
		{
			// Last-resort CSV if the xlsx path is locked (Excel has the file open).
			try
			{
				List<CatalogRow> rows = BuildRows(doc, parts, settings).ToList();
				string csvPath = Path.ChangeExtension(primary, ".csv");
				int added = UpsertCsvRows(csvPath, rows);
				return new CatalogUpsertResult(
					csvPath,
					added,
					0,
					"Could not update .xlsx (" + ex.Message + "). Wrote CSV instead: " + csvPath);
			}
			catch (Exception csvEx)
			{
				return new CatalogUpsertResult(primary, 0, 0, ex.Message + " | CSV fallback: " + csvEx.Message);
			}
		}
	}

	internal readonly struct CatalogUpsertResult
	{
		internal CatalogUpsertResult(string primaryPath, int primaryAdded, int packageCopyAdded, string error)
		{
			PrimaryPath = primaryPath ?? string.Empty;
			PrimaryAdded = primaryAdded;
			PackageCopyAdded = packageCopyAdded;
			Error = error ?? string.Empty;
		}

		internal string PrimaryPath { get; }
		internal int PrimaryAdded { get; }
		internal int PackageCopyAdded { get; }
		internal string Error { get; }
		internal bool Succeeded => string.IsNullOrWhiteSpace(Error);
	}

	internal static void EnsureWorkbook(string filePath)
	{
		if (string.IsNullOrWhiteSpace(filePath))
		{
			throw new ArgumentException("Catalog path is required.", nameof(filePath));
		}

		if (File.Exists(filePath))
		{
			return;
		}

		WriteWorkbook(filePath, Array.Empty<CatalogRow>());
	}

	private static void EnsureCsv(string filePath)
	{
		if (string.IsNullOrWhiteSpace(filePath) || File.Exists(filePath))
		{
			return;
		}

		WriteCsv(filePath, Array.Empty<CatalogRow>());
	}

	private static int UpsertCsvRows(string filePath, IReadOnlyList<CatalogRow> incoming)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");

		List<CatalogRow> existing = File.Exists(filePath)
			? ReadAllCsvRows(filePath)
			: new List<CatalogRow>();

		var codes = new HashSet<string>(
			existing.Select(r => r.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)),
			StringComparer.OrdinalIgnoreCase);

		int added = 0;
		foreach (CatalogRow row in incoming)
		{
			if (row == null || string.IsNullOrWhiteSpace(row.ItemCode) || !codes.Add(row.ItemCode))
			{
				continue;
			}

			existing.Add(row);
			added++;
		}

		if (added > 0 || !File.Exists(filePath))
		{
			List<CatalogRow> sorted = existing
				.OrderBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.NominalSizeInches)
				.ThenBy(r => r.Schedule, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.Material, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.ItemCode, StringComparer.OrdinalIgnoreCase)
				.ToList();
			WriteCsv(filePath, sorted);
		}

		return added;
	}

	private static void WriteCsv(string filePath, IReadOnlyList<CatalogRow> rows)
	{
		var sb = new StringBuilder();
		sb.AppendLine(string.Join(",", Headers.Select(CsvEscape)));
		foreach (CatalogRow row in rows ?? Array.Empty<CatalogRow>())
		{
			sb.AppendLine(string.Join(",",
				CsvEscape(row.ItemCode),
				CsvEscape(row.Type),
				CsvEscape(row.NominalSize),
				CsvEscape(row.Od),
				CsvEscape(row.Wall),
				CsvEscape(row.Id),
				CsvEscape(row.Schedule),
				CsvEscape(row.Material),
				CsvEscape(row.Spec)));
		}

		File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
	}

	private static string CsvEscape(string value)
	{
		string text = value ?? string.Empty;
		if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
		{
			return text;
		}

		return "\"" + text.Replace("\"", "\"\"") + "\"";
	}

	private static List<CatalogRow> ReadAllCsvRows(string filePath)
	{
		var rows = new List<CatalogRow>();
		if (!File.Exists(filePath))
		{
			return rows;
		}

		string[] lines = File.ReadAllLines(filePath);
		for (int i = 1; i < lines.Length; i++)
		{
			string line = lines[i];
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			List<string> cells = ParseCsvLine(line);
			if (cells.Count == 0 || string.IsNullOrWhiteSpace(cells[0]))
			{
				continue;
			}

			rows.Add(new CatalogRow
			{
				ItemCode = cells.ElementAtOrDefault(0) ?? string.Empty,
				Type = cells.ElementAtOrDefault(1) ?? string.Empty,
				NominalSize = cells.ElementAtOrDefault(2) ?? string.Empty,
				Od = cells.ElementAtOrDefault(3) ?? string.Empty,
				Wall = cells.ElementAtOrDefault(4) ?? string.Empty,
				Id = cells.ElementAtOrDefault(5) ?? string.Empty,
				Schedule = cells.ElementAtOrDefault(6) ?? string.Empty,
				Material = cells.ElementAtOrDefault(7) ?? string.Empty,
				Spec = cells.ElementAtOrDefault(8) ?? string.Empty
			});
		}

		return rows;
	}

	private static List<string> ParseCsvLine(string line)
	{
		var cells = new List<string>();
		if (line == null)
		{
			return cells;
		}

		var current = new StringBuilder();
		bool inQuotes = false;
		for (int i = 0; i < line.Length; i++)
		{
			char ch = line[i];
			if (inQuotes)
			{
				if (ch == '"')
				{
					if (i + 1 < line.Length && line[i + 1] == '"')
					{
						current.Append('"');
						i++;
					}
					else
					{
						inQuotes = false;
					}
				}
				else
				{
					current.Append(ch);
				}
			}
			else if (ch == '"')
			{
				inQuotes = true;
			}
			else if (ch == ',')
			{
				cells.Add(current.ToString());
				current.Clear();
			}
			else
			{
				current.Append(ch);
			}
		}

		cells.Add(current.ToString());
		return cells;
	}

	private static int UpsertRows(string filePath, IReadOnlyList<CatalogRow> incoming)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");

		List<CatalogRow> existing = File.Exists(filePath)
			? ReadAllRows(filePath)
			: new List<CatalogRow>();

		// Recover rows from a prior OpenXml/CSV fallback so they are not lost on first good xlsx write.
		string csvSibling = Path.ChangeExtension(filePath, ".csv");
		if (File.Exists(csvSibling))
		{
			foreach (CatalogRow row in ReadAllCsvRows(csvSibling))
			{
				if (row == null || string.IsNullOrWhiteSpace(row.ItemCode))
				{
					continue;
				}

				if (existing.Any(e => string.Equals(e.ItemCode, row.ItemCode, StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}

				existing.Add(row);
			}
		}

		var codes = new HashSet<string>(
			existing.Select(r => r.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)),
			StringComparer.OrdinalIgnoreCase);

		int added = 0;
		foreach (CatalogRow row in incoming)
		{
			if (row == null || string.IsNullOrWhiteSpace(row.ItemCode) || !codes.Add(row.ItemCode))
			{
				continue;
			}

			existing.Add(row);
			added++;
		}

		if (added > 0 || !File.Exists(filePath))
		{
			List<CatalogRow> sorted = existing
				.OrderBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.NominalSizeInches)
				.ThenBy(r => r.Schedule, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.Material, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.ItemCode, StringComparer.OrdinalIgnoreCase)
				.ToList();
			WriteWorkbook(filePath, sorted);
		}

		return added;
	}

	private static void WriteWorkbook(string filePath, IReadOnlyList<CatalogRow> rows)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
		var grid = new List<IReadOnlyList<string>> { Headers };
		foreach (CatalogRow row in rows ?? Array.Empty<CatalogRow>())
		{
			grid.Add(new[]
			{
				row.ItemCode ?? string.Empty,
				row.Type ?? string.Empty,
				row.NominalSize ?? string.Empty,
				row.Od ?? string.Empty,
				row.Wall ?? string.Empty,
				row.Id ?? string.Empty,
				row.Schedule ?? string.Empty,
				row.Material ?? string.Empty,
				row.Spec ?? string.Empty
			});
		}

		try
		{
			MinimalSpreadsheetXlsx.Write(filePath, SheetName, grid, ColumnWidths);
		}
		catch (IOException ex)
		{
			throw new IOException(
				"Could not update the catalog. Close \"" + Path.GetFileName(filePath) + "\" in Excel if it is open, then try again. " + ex.Message,
				ex);
		}
	}

	private static List<CatalogRow> ReadAllRows(string filePath)
	{
		var rows = new List<CatalogRow>();
		if (!File.Exists(filePath))
		{
			// Prefer sibling CSV if a prior OpenXml clash left only CSV.
			string csvSibling = Path.ChangeExtension(filePath, ".csv");
			if (File.Exists(csvSibling))
			{
				return ReadAllCsvRows(csvSibling);
			}

			return rows;
		}

		try
		{
			List<List<string>> grid = MinimalSpreadsheetXlsx.Read(filePath, SheetName);
			foreach (List<string> cells in grid.Skip(1))
			{
				if (cells == null || cells.Count == 0)
				{
					continue;
				}

				string itemCode = (cells.ElementAtOrDefault(0) ?? string.Empty).Trim();
				if (string.IsNullOrWhiteSpace(itemCode))
				{
					continue;
				}

				string nominal = (cells.ElementAtOrDefault(2) ?? string.Empty).Trim();
				rows.Add(new CatalogRow
				{
					ItemCode = itemCode,
					Type = (cells.ElementAtOrDefault(1) ?? string.Empty).Trim(),
					NominalSize = nominal,
					NominalSizeInches = ParseNominalInches(nominal),
					Od = (cells.ElementAtOrDefault(3) ?? string.Empty).Trim(),
					Wall = (cells.ElementAtOrDefault(4) ?? string.Empty).Trim(),
					Id = (cells.ElementAtOrDefault(5) ?? string.Empty).Trim(),
					Schedule = (cells.ElementAtOrDefault(6) ?? string.Empty).Trim(),
					Material = (cells.ElementAtOrDefault(7) ?? string.Empty).Trim(),
					Spec = (cells.ElementAtOrDefault(8) ?? string.Empty).Trim()
				});
			}
		}
		catch
		{
			string csvSibling = Path.ChangeExtension(filePath, ".csv");
			if (File.Exists(csvSibling))
			{
				return ReadAllCsvRows(csvSibling);
			}
		}

		return rows;
	}

	private static IEnumerable<CatalogRow> BuildRows(
		Document doc,
		IEnumerable<FabricationPart> parts,
		SpoolingManagerSettings settings)
	{
		if (doc == null || parts == null)
		{
			yield break;
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

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (FabricationPart part in parts.Where(p => p != null).Distinct())
		{
			if (FabricationPartClassification.IsIgnoredForSpoolDimensioning(part, doc)
				|| FabricationPartClassification.IsWeldPart(part)
				|| FabricationPartClassification.IsGasketPart(part)
				|| FabricationPartClassification.IsBoltKitPart(part))
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

			CatalogRow row = BuildRow(part, doc, family);
			if (row == null || string.IsNullOrWhiteSpace(row.ItemCode) || !seen.Add(row.ItemCode))
			{
				continue;
			}

			yield return row;
		}
	}

	private static CatalogRow BuildRow(FabricationPart part, Document doc, FabricationMaterialFamily family)
	{
		string typeLabel = ResolveTypeLabel(part, doc);
		string typeCode = ResolveTypeCode(part, doc);
		string material = FabricationMaterialKind.GetRawMaterialText(part, doc);
		string familyCode = ResolveFamilyCode(family, material);
		string gradeCode = SanitizeCodeToken(SimplifyGrade(material));

		string sizeRaw = FirstNonEmpty(
			FabricationPartClassification.GetParamString(part, doc, "S-Size"),
			FabricationPartClassification.GetParamString(part, doc, "Product Entry"),
			FabricationPartClassification.GetParamString(part, doc, "Alias"));

		double nps = ParseNominalInches(sizeRaw);
		string schedule = ResolveSchedule(part, doc, sizeRaw);
		string scheduleCode = FormatScheduleCode(schedule);

		TryResolveDimensions(part, doc, nps, schedule, out double od, out double wall, out double id);

		string itemCode = BuildItemCode(typeCode, familyCode, gradeCode, nps, scheduleCode);
		if (string.IsNullOrWhiteSpace(itemCode))
		{
			return null;
		}

		return new CatalogRow
		{
			ItemCode = itemCode,
			Type = typeLabel,
			NominalSize = nps > 0 ? FormatInches(nps, "0.###") : CleanDisplay(sizeRaw),
			NominalSizeInches = nps,
			Od = od > 0 ? FormatInches(od, "0.000") : string.Empty,
			Wall = wall > 0 ? FormatInches(wall, "0.000") : string.Empty,
			Id = id > 0 ? FormatInches(id, "0.000") : string.Empty,
			Schedule = string.IsNullOrWhiteSpace(schedule) ? string.Empty : schedule,
			Material = string.IsNullOrWhiteSpace(material)
				? (family == FabricationMaterialFamily.Unknown
					? "Other"
					: FabricationMaterialKind.DisplayName(family))
				: material,
			Spec = family == FabricationMaterialFamily.Unknown
				? "Other"
				: FabricationMaterialKind.DisplayName(family)
		};
	}

	private static string BuildItemCode(
		string typeCode,
		string familyCode,
		string gradeCode,
		double nps,
		string scheduleCode)
	{
		var parts = new List<string>();
		if (!string.IsNullOrWhiteSpace(typeCode))
		{
			parts.Add(typeCode);
		}

		if (!string.IsNullOrWhiteSpace(familyCode))
		{
			parts.Add(familyCode);
		}

		if (!string.IsNullOrWhiteSpace(gradeCode)
			&& !string.Equals(gradeCode, familyCode, StringComparison.OrdinalIgnoreCase))
		{
			parts.Add(gradeCode);
		}

		if (nps > 0)
		{
			parts.Add(FormatNpsToken(nps));
		}

		if (!string.IsNullOrWhiteSpace(scheduleCode))
		{
			parts.Add(scheduleCode);
		}

		return parts.Count == 0 ? null : string.Join("-", parts);
	}

	private static string ResolveTypeLabel(FabricationPart part, Document doc)
	{
		if (FabricationPartClassification.IsStraightPipeRun(part))
		{
			return "Pipe";
		}
		if (FabricationPartClassification.IsElbowPart(part, doc))
		{
			return "Elbow";
		}
		if (FabricationPartClassification.IsTeePart(part, doc))
		{
			return "Tee";
		}
		if (FabricationPartClassification.IsReducerPart(part, doc))
		{
			return "Reducer";
		}
		if (FabricationPartClassification.IsFlangePart(part, doc))
		{
			return "Flange";
		}
		if (FabricationPartClassification.IsValvePart(part, doc))
		{
			return "Valve";
		}
		if (FabricationPartClassification.IsOletPart(part))
		{
			return "Olet";
		}

		return "Fitting";
	}

	private static string ResolveTypeCode(FabricationPart part, Document doc)
	{
		if (FabricationPartClassification.IsStraightPipeRun(part))
		{
			return "PIPE";
		}
		if (FabricationPartClassification.IsElbowPart(part, doc))
		{
			double angle = TryReadElbowAngleDegrees(part, doc);
			if (angle > 0)
			{
				if (Math.Abs(angle - 90.0) < 8.0)
				{
					return "EL90";
				}
				if (Math.Abs(angle - 45.0) < 8.0)
				{
					return "EL45";
				}
				return "EL" + Math.Round(angle).ToString("0", CultureInfo.InvariantCulture);
			}

			return "EL90";
		}
		if (FabricationPartClassification.IsTeePart(part, doc))
		{
			return "TEE";
		}
		if (FabricationPartClassification.IsReducerPart(part, doc))
		{
			return "RED";
		}
		if (FabricationPartClassification.IsFlangePart(part, doc))
		{
			return "FLG";
		}
		if (FabricationPartClassification.IsValvePart(part, doc))
		{
			return "VALVE";
		}
		if (FabricationPartClassification.IsOletPart(part))
		{
			return "OLET";
		}

		return "FIT";
	}

	private static double TryReadElbowAngleDegrees(FabricationPart part, Document doc)
	{
		string[] names = { "Angle", "Bend Angle", "Elbow Angle" };
		foreach (string name in names)
		{
			string raw = FabricationPartClassification.GetParamString(part, doc, name);
			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}

			string cleaned = raw.Replace("°", string.Empty).Trim();
			if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
				&& value > 0)
			{
				return value;
			}
		}

		return 0.0;
	}

	private static string ResolveFamilyCode(FabricationMaterialFamily family, string material)
	{
		string upper = (material ?? string.Empty).ToUpperInvariant();
		if (family == FabricationMaterialFamily.Copper
			|| upper.Contains("COPPER")
			|| Regex.IsMatch(upper, @"\bCU\b"))
		{
			return "CU";
		}

		if (family == FabricationMaterialFamily.Pvc
			|| upper.Contains("CPVC")
			|| Regex.IsMatch(upper, @"\bPVC\b"))
		{
			return "PVC";
		}

		if (upper.Contains("STAINLESS") || Regex.IsMatch(upper, @"\bSS\b") || upper.Contains("304") || upper.Contains("316"))
		{
			return "SS";
		}

		if (family == FabricationMaterialFamily.CastIron || upper.Contains("CAST IRON") || upper.Contains("DUCTILE"))
		{
			return "CI";
		}

		if (family == FabricationMaterialFamily.Steel || upper.Contains("CARBON") || Regex.IsMatch(upper, @"\bCS\b") || upper.Contains("A106") || upper.Contains("A53") || upper.Contains("A234"))
		{
			return "CS";
		}

		return family switch
		{
			FabricationMaterialFamily.CastIron => "CI",
			FabricationMaterialFamily.Copper => "CU",
			FabricationMaterialFamily.Pvc => "PVC",
			FabricationMaterialFamily.Unknown => "OTH",
			_ => "CS"
		};
	}

	private static string SimplifyGrade(string material)
	{
		if (string.IsNullOrWhiteSpace(material))
		{
			return string.Empty;
		}

		string text = material.Trim();
		text = Regex.Replace(text, @"\s+", " ");
		text = Regex.Replace(text, @"\bGR(?:ADE)?\.?\s*", "Gr ", RegexOptions.IgnoreCase);
		return text;
	}

	private static string ResolveSchedule(FabricationPart part, Document doc, string sizeRaw)
	{
		string[] names = { "Schedule", "Pipe Schedule", "S-Schedule", "Wall Thickness Schedule" };
		foreach (string name in names)
		{
			string value = FabricationPartClassification.GetParamString(part, doc, name);
			string parsed = ExtractSchedule(value);
			if (!string.IsNullOrWhiteSpace(parsed))
			{
				return parsed;
			}
		}

		string fromSize = ExtractSchedule(sizeRaw);
		if (!string.IsNullOrWhiteSpace(fromSize))
		{
			return fromSize;
		}

		string product = FabricationPartClassification.GetParamString(part, doc, "Product Entry");
		string fromProduct = ExtractSchedule(product);
		if (!string.IsNullOrWhiteSpace(fromProduct))
		{
			return fromProduct;
		}

		string alias = FabricationPartClassification.GetParamString(part, doc, "Alias");
		return ExtractSchedule(alias);
	}

	private static string ExtractSchedule(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		Match sch = Regex.Match(text, @"\bSCH(?:EDULE)?[\s\.\-]*([0-9]{1,3}|STD|XS|XXS|XH|XXH)\b", RegexOptions.IgnoreCase);
		if (sch.Success)
		{
			return NormalizeScheduleToken(sch.Groups[1].Value);
		}

		Match sToken = Regex.Match(text, @"\bS[\s\-]?([0-9]{1,3})\b", RegexOptions.IgnoreCase);
		if (sToken.Success)
		{
			return NormalizeScheduleToken(sToken.Groups[1].Value);
		}

		Match bare = Regex.Match(text, @"\b(STD|XS|XXS|XH|XXH)\b", RegexOptions.IgnoreCase);
		if (bare.Success)
		{
			return NormalizeScheduleToken(bare.Groups[1].Value);
		}

		return string.Empty;
	}

	private static string NormalizeScheduleToken(string token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return string.Empty;
		}

		string t = token.Trim().ToUpperInvariant();
		if (t == "XH")
		{
			return "XS";
		}
		if (t == "XXH")
		{
			return "XXS";
		}

		return t;
	}

	private static string FormatScheduleCode(string schedule)
	{
		if (string.IsNullOrWhiteSpace(schedule))
		{
			return string.Empty;
		}

		string t = schedule.Trim().ToUpperInvariant();
		if (Regex.IsMatch(t, @"^\d+$"))
		{
			return "S" + t;
		}

		return "S" + t;
	}

	private static void TryResolveDimensions(
		FabricationPart part,
		Document doc,
		double nps,
		string schedule,
		out double od,
		out double wall,
		out double id)
	{
		od = TryReadLengthInches(part, doc, "Outside Diameter", "OD", "Outer Diameter", "Main Primary Diameter");
		wall = TryReadLengthInches(part, doc, "Wall Thickness", "Thickness", "Wall");
		id = TryReadLengthInches(part, doc, "Inside Diameter", "ID", "Inner Diameter");

		if (od <= 0 || wall <= 0)
		{
			if (PipeDimensionTables.TryGet(nps, schedule, out double tableOd, out double tableWall))
			{
				if (od <= 0)
				{
					od = tableOd;
				}
				if (wall <= 0)
				{
					wall = tableWall;
				}
			}
		}

		if (id <= 0 && od > 0 && wall > 0)
		{
			id = od - (2.0 * wall);
			if (id < 0)
			{
				id = 0;
			}
		}
	}

	private static double TryReadLengthInches(FabricationPart part, Document doc, params string[] names)
	{
		foreach (string name in names)
		{
			Autodesk.Revit.DB.Parameter parameter = part?.LookupParameter(name);
			if (parameter == null && doc != null)
			{
				try
				{
					ElementId typeId = ((Element)part).GetTypeId();
					if (typeId != null && typeId != ElementId.InvalidElementId)
					{
						parameter = doc.GetElement(typeId)?.LookupParameter(name);
					}
				}
				catch
				{
				}
			}

			if (parameter == null || !parameter.HasValue)
			{
				string asText = FabricationPartClassification.GetParamString(part, doc, name);
				double parsed = ParseNominalInches(asText);
				if (parsed > 0)
				{
					return parsed;
				}

				continue;
			}

			try
			{
				if (parameter.StorageType == StorageType.Double)
				{
					// Revit internal length is feet.
					return parameter.AsDouble() * 12.0;
				}

				string valueString = parameter.AsValueString();
				double fromValue = ParseNominalInches(valueString);
				if (fromValue > 0)
				{
					return fromValue;
				}
			}
			catch
			{
			}
		}

		return 0.0;
	}

	private static double ParseNominalInches(string sizeText)
	{
		if (string.IsNullOrWhiteSpace(sizeText))
		{
			return 0.0;
		}

		string text = sizeText.Trim();
		Match leading = Regex.Match(text, @"^\s*(\d+\s+\d+/\d+|\d+/\d+|\d*\.\d+|\d+)");
		if (!leading.Success)
		{
			return 0.0;
		}

		string token = leading.Groups[1].Value.Trim();
		if (token.Contains(" "))
		{
			string[] bits = token.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (bits.Length == 2
				&& double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double whole)
				&& bits[1].Contains('/'))
			{
				string[] frac = bits[1].Split('/');
				if (frac.Length == 2
					&& double.TryParse(frac[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
					&& double.TryParse(frac[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
					&& Math.Abs(den) > 1e-9)
				{
					return whole + (num / den);
				}
			}
		}

		if (token.Contains('/'))
		{
			string[] frac = token.Split('/');
			if (frac.Length == 2
				&& double.TryParse(frac[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
				&& double.TryParse(frac[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
				&& Math.Abs(den) > 1e-9)
			{
				return num / den;
			}
		}

		if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
		{
			return value;
		}

		return 0.0;
	}

	private static string FormatNpsToken(double nps)
	{
		if (Math.Abs(nps - Math.Round(nps)) < 1e-6)
		{
			return Math.Round(nps).ToString("0", CultureInfo.InvariantCulture);
		}

		// Common fractions: 0.5 -> 1/2 style as 0.5 for code stability
		return nps.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', 'P');
	}

	private static string FormatInches(double inches, string format)
	{
		return inches.ToString(format, CultureInfo.InvariantCulture) + " in";
	}

	private static string CleanDisplay(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
	}

	private static string FirstNonEmpty(params string[] values)
	{
		foreach (string value in values)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value.Trim();
			}
		}

		return string.Empty;
	}

	private static string SanitizeCodeToken(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}

		var sb = new StringBuilder(value.Length);
		foreach (char c in value.Trim().ToUpperInvariant())
		{
			if (char.IsLetterOrDigit(c))
			{
				sb.Append(c);
			}
		}

		return sb.ToString();
	}

	private sealed class CatalogRow
	{
		public string ItemCode { get; set; }
		public string Type { get; set; }
		public string NominalSize { get; set; }
		public double NominalSizeInches { get; set; }
		public string Od { get; set; }
		public string Wall { get; set; }
		public string Id { get; set; }
		public string Schedule { get; set; }
		public string Material { get; set; }
		public string Spec { get; set; }
	}

	/// <summary>ASME B36.10M / common NPS OD and wall for Sch 40 / 80 / STD / XS.</summary>
	private static class PipeDimensionTables
	{
		private static readonly Dictionary<string, (double Od, double Wall)> Table =
			new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
			{
				["0.5|40"] = (0.840, 0.109),
				["0.5|STD"] = (0.840, 0.109),
				["0.5|80"] = (0.840, 0.147),
				["0.5|XS"] = (0.840, 0.147),
				["0.75|40"] = (1.050, 0.113),
				["0.75|STD"] = (1.050, 0.113),
				["0.75|80"] = (1.050, 0.154),
				["0.75|XS"] = (1.050, 0.154),
				["1|40"] = (1.315, 0.133),
				["1|STD"] = (1.315, 0.133),
				["1|80"] = (1.315, 0.179),
				["1|XS"] = (1.315, 0.179),
				["1.25|40"] = (1.660, 0.140),
				["1.25|STD"] = (1.660, 0.140),
				["1.25|80"] = (1.660, 0.191),
				["1.25|XS"] = (1.660, 0.191),
				["1.5|40"] = (1.900, 0.145),
				["1.5|STD"] = (1.900, 0.145),
				["1.5|80"] = (1.900, 0.200),
				["1.5|XS"] = (1.900, 0.200),
				["2|40"] = (2.375, 0.154),
				["2|STD"] = (2.375, 0.154),
				["2|80"] = (2.375, 0.218),
				["2|XS"] = (2.375, 0.218),
				["2.5|40"] = (2.875, 0.203),
				["2.5|STD"] = (2.875, 0.203),
				["2.5|80"] = (2.875, 0.276),
				["2.5|XS"] = (2.875, 0.276),
				["3|40"] = (3.500, 0.216),
				["3|STD"] = (3.500, 0.216),
				["3|80"] = (3.500, 0.300),
				["3|XS"] = (3.500, 0.300),
				["3.5|40"] = (4.000, 0.226),
				["3.5|STD"] = (4.000, 0.226),
				["3.5|80"] = (4.000, 0.318),
				["3.5|XS"] = (4.000, 0.318),
				["4|40"] = (4.500, 0.237),
				["4|STD"] = (4.500, 0.237),
				["4|80"] = (4.500, 0.337),
				["4|XS"] = (4.500, 0.337),
				["5|40"] = (5.563, 0.258),
				["5|STD"] = (5.563, 0.258),
				["5|80"] = (5.563, 0.375),
				["5|XS"] = (5.563, 0.375),
				["6|40"] = (6.625, 0.280),
				["6|STD"] = (6.625, 0.280),
				["6|80"] = (6.625, 0.432),
				["6|XS"] = (6.625, 0.432),
				["8|40"] = (8.625, 0.322),
				["8|STD"] = (8.625, 0.322),
				["8|80"] = (8.625, 0.500),
				["8|XS"] = (8.625, 0.500),
				["10|40"] = (10.750, 0.365),
				["10|STD"] = (10.750, 0.365),
				["10|80"] = (10.750, 0.594),
				["10|XS"] = (10.750, 0.500),
				["12|40"] = (12.750, 0.406),
				["12|STD"] = (12.750, 0.375),
				["12|80"] = (12.750, 0.688),
				["12|XS"] = (12.750, 0.500),
			};

		internal static bool TryGet(double nps, string schedule, out double od, out double wall)
		{
			od = 0;
			wall = 0;
			if (nps <= 0 || string.IsNullOrWhiteSpace(schedule))
			{
				return false;
			}

			string key = nps.ToString("0.###", CultureInfo.InvariantCulture) + "|" + schedule.Trim().ToUpperInvariant();
			if (!Table.TryGetValue(key, out (double Od, double Wall) dims))
			{
				// Try rounded whole NPS
				string wholeKey = Math.Round(nps, 3).ToString("0.###", CultureInfo.InvariantCulture) + "|" + schedule.Trim().ToUpperInvariant();
				if (!Table.TryGetValue(wholeKey, out dims))
				{
					return false;
				}
			}

			od = dims.Od;
			wall = dims.Wall;
			return true;
		}
	}
}
