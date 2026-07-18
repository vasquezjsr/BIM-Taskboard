using System;
using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

internal static class WeldLogExcelExportService
{
	private const uint HeaderStyleIndex = 1U;

	private static readonly string[] Headers =
	{
		"Weld Number",
		"Date",
		"Welder ID",
		"Initials",
		"Material",
		"Weld Type",
		"Assembly"
	};

	private static readonly double[] ColumnWidthsInches =
	{
		18,
		12,
		14,
		10,
		22,
		14,
		22
	};

	internal static void Export(string filePath, IReadOnlyList<WeldLogExportRow> rows)
	{
		if (string.IsNullOrWhiteSpace(filePath))
		{
			throw new ArgumentException("Choose a file path.", nameof(filePath));
		}

		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");

		using (SpreadsheetDocument document = SpreadsheetDocument.Create(filePath, SpreadsheetDocumentType.Workbook))
		{
			WorkbookPart workbookPart = document.AddWorkbookPart();
			workbookPart.Workbook = new Workbook();

			WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
			stylesPart.Stylesheet = CreateStylesheet();
			stylesPart.Stylesheet.Save();

			WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
			SheetData sheetData = new SheetData();
			worksheetPart.Worksheet = new Worksheet(
				CreateColumns(),
				sheetData,
				CreateSheetViews(),
				CreateAutoFilter(Headers.Length));

			AppendHeaderRow(sheetData);

			uint rowIndex = 2;
			foreach (WeldLogExportRow row in rows ?? Array.Empty<WeldLogExportRow>())
			{
				AppendDataRow(sheetData, rowIndex, row);
				rowIndex++;
			}

			Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
			sheets.Append(new Sheet
			{
				Id = workbookPart.GetIdOfPart(worksheetPart),
				SheetId = 1U,
				Name = "Weld Log"
			});

			workbookPart.Workbook.Save();
		}
	}

	private static void AppendHeaderRow(SheetData sheetData)
	{
		Row headerRow = new Row { RowIndex = 1U };
		for (int column = 0; column < Headers.Length; column++)
		{
			headerRow.Append(CreateTextCell(column, 1U, Headers[column], HeaderStyleIndex));
		}

		sheetData.Append(headerRow);
	}

	private static void AppendDataRow(SheetData sheetData, uint rowIndex, WeldLogExportRow row)
	{
		Row dataRow = new Row { RowIndex = rowIndex };
		dataRow.Append(
			CreateTextCell(0, rowIndex, row.WeldNumber ?? string.Empty),
			CreateTextCell(1, rowIndex, row.Date ?? string.Empty),
			CreateTextCell(2, rowIndex, row.WelderId ?? string.Empty),
			CreateTextCell(3, rowIndex, row.Initials ?? string.Empty),
			CreateTextCell(4, rowIndex, row.Material ?? string.Empty),
			CreateTextCell(5, rowIndex, row.WeldType ?? string.Empty),
			CreateTextCell(6, rowIndex, row.Assembly ?? string.Empty));
		sheetData.Append(dataRow);
	}

	private static Cell CreateTextCell(int columnIndex, uint rowIndex, string text, uint? styleIndex = null)
	{
		Cell cell = new Cell
		{
			CellReference = GetCellReference(columnIndex, rowIndex),
			DataType = CellValues.InlineString,
			InlineString = new InlineString(new Text(text ?? string.Empty))
		};

		if (styleIndex.HasValue)
		{
			cell.StyleIndex = styleIndex.Value;
		}

		return cell;
	}

	private static Columns CreateColumns()
	{
		Columns columns = new Columns();
		for (int column = 0; column < ColumnWidthsInches.Length; column++)
		{
			uint columnNumber = (uint)(column + 1);
			columns.Append(new Column
			{
				Min = columnNumber,
				Max = columnNumber,
				Width = ColumnWidthsInches[column],
				CustomWidth = true
			});
		}

		return columns;
	}

	private static SheetViews CreateSheetViews()
	{
		SheetView sheetView = new SheetView
		{
			WorkbookViewId = 0U,
			TabSelected = true
		};
		sheetView.Append(new Pane
		{
			VerticalSplit = 1D,
			TopLeftCell = "A2",
			ActivePane = PaneValues.BottomLeft,
			State = PaneStateValues.Frozen
		});
		return new SheetViews(sheetView);
	}

	private static AutoFilter CreateAutoFilter(int columnCount)
	{
		return new AutoFilter { Reference = "A1:" + GetColumnLetter(columnCount - 1) + "1" };
	}

	private static Stylesheet CreateStylesheet()
	{
		Fonts fonts = new Fonts(
			new Font(),
			new Font(new Bold()));
		fonts.Count = (uint)fonts.ChildElements.Count;

		Fills fills = new Fills(
			new Fill(new PatternFill { PatternType = PatternValues.None }),
			new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
			new Fill(new PatternFill(new ForegroundColor { Rgb = "FFD9E1F2" })
			{
				PatternType = PatternValues.Solid
			}));
		fills.Count = (uint)fills.ChildElements.Count;

		Borders borders = new Borders(new Border());
		borders.Count = (uint)borders.ChildElements.Count;

		CellFormats cellFormats = new CellFormats(
			new CellFormat(),
			new CellFormat
			{
				FontId = 1U,
				FillId = 2U,
				BorderId = 0U,
				ApplyFont = true,
				ApplyFill = true,
				ApplyAlignment = true,
				Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center }
			});
		cellFormats.Count = (uint)cellFormats.ChildElements.Count;

		return new Stylesheet(fonts, fills, borders, cellFormats);
	}

	private static string GetCellReference(int columnIndex, uint rowIndex)
	{
		return GetColumnLetter(columnIndex) + rowIndex;
	}

	private static string GetColumnLetter(int columnIndexZeroBased)
	{
		int dividend = columnIndexZeroBased + 1;
		string columnName = string.Empty;
		while (dividend > 0)
		{
			int modulo = (dividend - 1) % 26;
			columnName = Convert.ToChar('A' + modulo) + columnName;
			dividend = (dividend - modulo) / 26;
		}

		return columnName;
	}
}
