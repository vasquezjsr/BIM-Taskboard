using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

internal static class DimensionInspectorService
{
	private static readonly object ExportLock = new object();

	private static string ExportFolder =>
		Path.Combine(Path.GetTempPath(), "SpoolingSavantV3Exports-DimInspector");

	public static DimensionInspectorReport BuildReport(UIDocument uidoc, bool exportViewImage)
	{
		return CreateSpoolSheetsHandler.BuildDimensionInspectorReport(uidoc, exportViewImage);
	}

	public static string TryExportActiveViewImage(Document doc, View view)
	{
		if (doc == null || view == null)
		{
			return null;
		}
		lock (ExportLock)
		{
			try
			{
				Directory.CreateDirectory(ExportFolder);
				string prefix = Path.Combine(ExportFolder, "view-" + view.Id.Value);
				foreach (string old in Directory.GetFiles(ExportFolder, "view-" + view.Id.Value + "*"))
				{
					try
					{
						File.Delete(old);
					}
					catch
					{
					}
				}
				ImageExportOptions options = new ImageExportOptions
				{
					ExportRange = ExportRange.SetOfViews,
					FilePath = prefix,
					FitDirection = FitDirectionType.Horizontal,
					HLRandWFViewsFileType = ImageFileType.PNG,
					ImageResolution = ImageResolution.DPI_72,
					ZoomType = ZoomFitType.FitToPage,
					ShadowViewsFileType = ImageFileType.PNG
				};
				options.SetViewsAndSheets(new List<ElementId> { view.Id });
				doc.ExportImage(options);
				string exported = Directory.GetFiles(ExportFolder, "view-" + view.Id.Value + "*.png")
					.OrderByDescending(File.GetLastWriteTimeUtc)
					.FirstOrDefault();
				return exported;
			}
			catch
			{
				return null;
			}
		}
	}
}
