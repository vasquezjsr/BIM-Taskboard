using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using iTextSharp.text.pdf;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using RevitDocument = Autodesk.Revit.DB.Document;
using RevitElement = Autodesk.Revit.DB.Element;
using PdfBaseColor = iTextSharp.text.BaseColor;
using PdfRectangle = iTextSharp.text.Rectangle;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Stamps fillable Date / Welder ID / Initials fields onto Spools Combined PDFs
/// using the same weld-log layout as sheet generation.
/// </summary>
internal static class WeldLogPdfEntryFieldsService
{
	public static int TryStampEntryFields(
		RevitDocument doc,
		IList<ViewSheet> sheets,
		IDictionary<ElementId, AssemblyInstance> assemblyBySheetId,
		SpoolingManagerSettings settings,
		string pdfFullPath)
	{
		if (doc == null
			|| sheets == null
			|| sheets.Count == 0
			|| settings == null
			|| !settings.WeldLogEntryFieldsEnabled
			|| string.IsNullOrWhiteSpace(pdfFullPath)
			|| !File.Exists(pdfFullPath))
		{
			return 0;
		}

		string tempPath = pdfFullPath + ".entryfields.tmp.pdf";
		int fieldCount = 0;

		try
		{
			using (PdfReader reader = new PdfReader(pdfFullPath))
			using (FileStream output = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
			using (PdfStamper stamper = new PdfStamper(reader, output))
			{
				int pageCount = reader.NumberOfPages;
				for (int page = 1; page <= pageCount && page <= sheets.Count; page++)
				{
					ViewSheet sheet = sheets[page - 1];
					if (sheet == null)
					{
						continue;
					}

					if (assemblyBySheetId == null
						|| !assemblyBySheetId.TryGetValue(((RevitElement)sheet).Id, out AssemblyInstance assembly)
						|| assembly == null)
					{
						continue;
					}

					List<WeldLogEntryFieldRect> rects =
						CreateSpoolSheetsHandler.GetWeldLogEntryFieldRects(doc, sheet, assembly, settings);
					if (rects.Count == 0)
					{
						continue;
					}

					PdfRectangle pageSize = reader.GetPageSize(page);
					BoundingBoxUV outline = ((View)sheet).Outline;
					if (outline == null)
					{
						continue;
					}

					double sheetWidth = outline.Max.U - outline.Min.U;
					double sheetHeight = outline.Max.V - outline.Min.V;
					if (sheetWidth <= 1e-9 || sheetHeight <= 1e-9)
					{
						continue;
					}

					foreach (WeldLogEntryFieldRect rect in rects)
					{
						float llx = SheetXToPdf(rect.MinX, outline.Min.U, sheetWidth, pageSize.Width);
						float lly = SheetYToPdf(rect.MinY, outline.Min.V, sheetHeight, pageSize.Height);
						float urx = SheetXToPdf(rect.MaxX, outline.Min.U, sheetWidth, pageSize.Width);
						float ury = SheetYToPdf(rect.MaxY, outline.Min.V, sheetHeight, pageSize.Height);
						if (urx <= llx || ury <= lly)
						{
							continue;
						}

						string fieldName = BuildFieldName(page, rect);
						TextField field = new TextField(
							stamper.Writer,
							new PdfRectangle(llx, lly, urx, ury),
							fieldName)
						{
							FontSize = 7f,
							BorderWidth = 0.4f,
							BorderColor = PdfBaseColor.GRAY,
							BackgroundColor = new PdfBaseColor(248, 250, 252),
							TextColor = PdfBaseColor.BLACK,
							Visibility = TextField.VISIBLE,
							Options = TextField.MULTILINE,
						};

						stamper.AddAnnotation(field.GetTextField(), page);
						fieldCount++;
					}
				}

				stamper.FormFlattening = false;
			}

			if (fieldCount > 0)
			{
				File.Copy(tempPath, pdfFullPath, overwrite: true);
			}
		}
		finally
		{
			try
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
			catch
			{
				// Ignore temp cleanup failures.
			}
		}

		return fieldCount;
	}

	private static float SheetXToPdf(double sheetXFeet, double minU, double sheetWidthFeet, float pageWidth)
	{
		return (float)((sheetXFeet - minU) / sheetWidthFeet * pageWidth);
	}

	private static float SheetYToPdf(double sheetYFeet, double minV, double sheetHeightFeet, float pageHeight)
	{
		return (float)((sheetYFeet - minV) / sheetHeightFeet * pageHeight);
	}

	private static string BuildFieldName(int page, WeldLogEntryFieldRect rect)
	{
		string weld = SanitizeName(rect.WeldNumber);
		string kind = SanitizeName(rect.Kind);
		return $"weldlog_p{page}_{rect.SlotIndex}_{kind}_{weld}";
	}

	private static string SanitizeName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "x";
		}

		char[] chars = value.Trim().ToCharArray();
		for (int i = 0; i < chars.Length; i++)
		{
			char c = chars[i];
			if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
			{
				chars[i] = '_';
			}
		}

		return new string(chars);
	}
}
