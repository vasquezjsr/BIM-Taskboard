using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Minimal .xlsx read/write with Zip + XML only — no DocumentFormat.OpenXml / ClosedXML.
/// Avoids Revit hotload OpenXml type-identity clashes.
/// </summary>
internal static class MinimalSpreadsheetXlsx
{
	private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
	private static readonly XNamespace PackageRelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
	private static readonly XNamespace OfficeRelsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
	private static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

	internal static void Write(
		string filePath,
		string sheetName,
		IReadOnlyList<IReadOnlyList<string>> rows,
		IReadOnlyList<double> columnWidths = null)
	{
		if (string.IsNullOrWhiteSpace(filePath))
		{
			throw new ArgumentException("Path is required.", nameof(filePath));
		}

		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
		string safeSheet = string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : SanitizeSheetName(sheetName);
		string tempPath = filePath + ".tmp.xlsx";
		if (File.Exists(tempPath))
		{
			File.Delete(tempPath);
		}

		using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
		using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create))
		{
			WriteEntry(zip, "[Content_Types].xml", BuildContentTypes());
			WriteEntry(zip, "_rels/.rels", BuildPackageRels());
			WriteEntry(zip, "xl/workbook.xml", BuildWorkbook(safeSheet));
			WriteEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels());
			WriteEntry(zip, "xl/styles.xml", BuildStyles());
			WriteEntry(zip, "xl/worksheets/sheet1.xml", BuildSheet(rows, columnWidths));
		}

		if (File.Exists(filePath))
		{
			File.Replace(tempPath, filePath, null);
		}
		else
		{
			File.Move(tempPath, filePath);
		}
	}

	internal static List<List<string>> Read(string filePath, string preferredSheetName = null)
	{
		var result = new List<List<string>>();
		if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
		{
			return result;
		}

		using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
		using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
		{
			List<string> shared = ReadSharedStrings(zip);
			string sheetPath = ResolveSheetPartPath(zip, preferredSheetName) ?? "xl/worksheets/sheet1.xml";
			ZipArchiveEntry sheetEntry = zip.GetEntry(sheetPath)
				?? zip.Entries.FirstOrDefault(e =>
					e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
					&& e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
			if (sheetEntry == null)
			{
				return result;
			}

			XDocument sheetDoc;
			using (Stream entryStream = sheetEntry.Open())
			{
				sheetDoc = XDocument.Load(entryStream);
			}

			XElement sheetData = sheetDoc.Root?.Element(SpreadsheetNs + "sheetData");
			if (sheetData == null)
			{
				return result;
			}

			foreach (XElement row in sheetData.Elements(SpreadsheetNs + "row"))
			{
				var cells = new Dictionary<int, string>();
				int maxCol = 0;
				foreach (XElement cell in row.Elements(SpreadsheetNs + "c"))
				{
					string reference = (string)cell.Attribute("r") ?? string.Empty;
					int col = ColumnIndexFromReference(reference);
					if (col <= 0)
					{
						col = maxCol + 1;
					}

					maxCol = Math.Max(maxCol, col);
					cells[col] = ReadCellValue(cell, shared);
				}

				var values = new List<string>();
				for (int col = 1; col <= maxCol; col++)
				{
					values.Add(cells.TryGetValue(col, out string value) ? value : string.Empty);
				}

				result.Add(values);
			}
		}

		return result;
	}

	private static void WriteEntry(ZipArchive zip, string path, string xml)
	{
		ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
		using (Stream stream = entry.Open())
		using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
		{
			writer.Write(xml);
		}
	}

	private static string BuildContentTypes()
	{
		return
			"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
			+ "<Types xmlns=\"" + ContentTypesNs + "\">"
			+ "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
			+ "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
			+ "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
			+ "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
			+ "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
			+ "</Types>";
	}

	private static string BuildPackageRels()
	{
		return
			"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
			+ "<Relationships xmlns=\"" + PackageRelsNs + "\">"
			+ "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
			+ "</Relationships>";
	}

	private static string BuildWorkbook(string sheetName)
	{
		return
			"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
			+ "<workbook xmlns=\"" + SpreadsheetNs + "\" xmlns:r=\"" + OfficeRelsNs + "\">"
			+ "<sheets>"
			+ "<sheet name=\"" + XmlEscape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/>"
			+ "</sheets>"
			+ "</workbook>";
	}

	private static string BuildWorkbookRels()
	{
		return
			"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
			+ "<Relationships xmlns=\"" + PackageRelsNs + "\">"
			+ "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
			+ "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
			+ "</Relationships>";
	}

	private static string BuildStyles()
	{
		// Style index 0 = default, 1 = bold white on dark header fill.
		return
			"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
			+ "<styleSheet xmlns=\"" + SpreadsheetNs + "\">"
			+ "<fonts count=\"2\">"
			+ "<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>"
			+ "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/><family val=\"2\"/></font>"
			+ "</fonts>"
			+ "<fills count=\"3\">"
			+ "<fill><patternFill patternType=\"none\"/></fill>"
			+ "<fill><patternFill patternType=\"gray125\"/></fill>"
			+ "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F2937\"/><bgColor indexed=\"64\"/></patternFill></fill>"
			+ "</fills>"
			+ "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
			+ "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
			+ "<cellXfs count=\"2\">"
			+ "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
			+ "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
			+ "</cellXfs>"
			+ "</styleSheet>";
	}

	private static string BuildSheet(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<double> columnWidths)
	{
		var sb = new StringBuilder();
		sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
		sb.Append("<worksheet xmlns=\"").Append(SpreadsheetNs).Append("\">");
		sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
		sb.Append("<cols>");
		int colCount = Math.Max(
			columnWidths?.Count ?? 0,
			rows.Count == 0 ? 1 : rows.Max(r => r?.Count ?? 0));
		for (int c = 0; c < colCount; c++)
		{
			double width = columnWidths != null && c < columnWidths.Count ? columnWidths[c] : 14;
			sb.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1)
				.Append("\" width=\"").Append(width.ToString("0.###", CultureInfo.InvariantCulture))
				.Append("\" customWidth=\"1\"/>");
		}

		sb.Append("</cols><sheetData>");
		for (int r = 0; r < (rows?.Count ?? 0); r++)
		{
			IReadOnlyList<string> row = rows[r] ?? Array.Empty<string>();
			int rowNumber = r + 1;
			sb.Append("<row r=\"").Append(rowNumber).Append("\">");
			for (int c = 0; c < row.Count; c++)
			{
				string reference = ColumnName(c + 1) + rowNumber.ToString(CultureInfo.InvariantCulture);
				string style = r == 0 ? " s=\"1\"" : string.Empty;
				sb.Append("<c r=\"").Append(reference).Append('"').Append(style).Append(" t=\"inlineStr\"><is><t>")
					.Append(XmlEscape(row[c] ?? string.Empty))
					.Append("</t></is></c>");
			}

			sb.Append("</row>");
		}

		sb.Append("</sheetData></worksheet>");
		return sb.ToString();
	}

	private static List<string> ReadSharedStrings(ZipArchive zip)
	{
		var list = new List<string>();
		ZipArchiveEntry entry = zip.GetEntry("xl/sharedStrings.xml");
		if (entry == null)
		{
			return list;
		}

		XDocument doc;
		using (Stream stream = entry.Open())
		{
			doc = XDocument.Load(stream);
		}

		foreach (XElement si in doc.Root?.Elements(SpreadsheetNs + "si") ?? Enumerable.Empty<XElement>())
		{
			string text = string.Concat(
				si.Descendants(SpreadsheetNs + "t").Select(t => (string)t ?? string.Empty));
			list.Add(text);
		}

		return list;
	}

	private static string ResolveSheetPartPath(ZipArchive zip, string preferredSheetName)
	{
		ZipArchiveEntry workbookEntry = zip.GetEntry("xl/workbook.xml");
		ZipArchiveEntry relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
		if (workbookEntry == null || relsEntry == null)
		{
			return null;
		}

		XDocument workbook;
		XDocument rels;
		using (Stream stream = workbookEntry.Open())
		{
			workbook = XDocument.Load(stream);
		}

		using (Stream stream = relsEntry.Open())
		{
			rels = XDocument.Load(stream);
		}

		var relMap = rels.Root?
			.Elements(PackageRelsNs + "Relationship")
			.ToDictionary(
				r => (string)r.Attribute("Id") ?? string.Empty,
				r => (string)r.Attribute("Target") ?? string.Empty,
				StringComparer.OrdinalIgnoreCase)
			?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (XElement sheet in workbook.Root?.Element(SpreadsheetNs + "sheets")?.Elements(SpreadsheetNs + "sheet")
			?? Enumerable.Empty<XElement>())
		{
			string name = (string)sheet.Attribute("name") ?? string.Empty;
			string relId = (string)sheet.Attribute(OfficeRelsNs + "id")
				?? (string)sheet.Attribute("id")
				?? string.Empty;
			if (!relMap.TryGetValue(relId, out string target) || string.IsNullOrWhiteSpace(target))
			{
				continue;
			}

			string path = target.Replace('\\', '/');
			if (!path.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
			{
				path = "xl/" + path.TrimStart('/');
			}

			if (string.IsNullOrWhiteSpace(preferredSheetName)
				|| string.Equals(name, preferredSheetName, StringComparison.OrdinalIgnoreCase))
			{
				return path;
			}
		}

		return null;
	}

	private static string ReadCellValue(XElement cell, IReadOnlyList<string> shared)
	{
		string type = (string)cell.Attribute("t") ?? string.Empty;
		if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
		{
			return string.Concat(
				cell.Descendants(SpreadsheetNs + "t").Select(t => (string)t ?? string.Empty));
		}

		string raw = (string)cell.Element(SpreadsheetNs + "v") ?? string.Empty;
		if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase)
			&& int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
			&& index >= 0
			&& index < shared.Count)
		{
			return shared[index] ?? string.Empty;
		}

		return raw;
	}

	private static int ColumnIndexFromReference(string reference)
	{
		if (string.IsNullOrWhiteSpace(reference))
		{
			return 0;
		}

		int value = 0;
		foreach (char ch in reference)
		{
			if (ch < 'A' || ch > 'Z')
			{
				break;
			}

			value = (value * 26) + (ch - 'A' + 1);
		}

		return value;
	}

	private static string ColumnName(int index1Based)
	{
		int n = index1Based;
		var chars = new Stack<char>();
		while (n > 0)
		{
			n--;
			chars.Push((char)('A' + (n % 26)));
			n /= 26;
		}

		return new string(chars.ToArray());
	}

	private static string SanitizeSheetName(string name)
	{
		string cleaned = name.Replace('\\', ' ').Replace('/', ' ').Replace('?', ' ')
			.Replace('*', ' ').Replace('[', ' ').Replace(']', ' ').Replace(':', ' ');
		if (cleaned.Length > 31)
		{
			cleaned = cleaned.Substring(0, 31);
		}

		return string.IsNullOrWhiteSpace(cleaned) ? "Sheet1" : cleaned.Trim();
	}

	private static string XmlEscape(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}

		return value
			.Replace("&", "&amp;")
			.Replace("<", "&lt;")
			.Replace(">", "&gt;")
			.Replace("\"", "&quot;");
	}
}
