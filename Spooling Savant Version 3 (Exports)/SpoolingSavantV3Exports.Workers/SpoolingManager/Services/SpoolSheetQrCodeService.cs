using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using QRCoder;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Places a 1" tracking QR in the titleblock QR slot on assembly spool sheets.
/// Payload: <c>SSV3|P={package}|A={assembly}</c>, or a URL when <see cref="SpoolingManagerSettings.QrTrackingUrlBase"/> is set.
/// </summary>
internal static class SpoolSheetQrCodeService
{
	internal const string QrMarker = "SSV3-QR";
	internal const string ImageTypeNamePrefix = "SSV3-QR|";
	private const string LegacyQrMarker = "SSV2-QR";
	private const string LegacyImageTypeNamePrefix = "SSV2-QR|";

	/// <summary>1/8" left margin + 1-1/4" empty cell; QR sits in the next 1" slot.</summary>
	private const double QrLeftFromTitleBlockInches = 0.125 + 1.25;

	/// <summary>1/8" bottom margin — QR fills the 1" tall project-strip cell.</summary>
	private const double QrBottomFromTitleBlockInches = 0.125;

	private const double QrSizeInches = 1.0;

	internal static string BuildPayload(string package, string assemblyName, string urlBase)
	{
		string p = (package ?? string.Empty).Trim();
		string a = (assemblyName ?? string.Empty).Trim();
		string baseUrl = (urlBase ?? string.Empty).Trim();
		if (baseUrl.Length > 0)
		{
			string sep = baseUrl.Contains("?") ? "&" : "?";
			return baseUrl
				+ sep
				+ "p=" + Uri.EscapeDataString(p)
				+ "&a=" + Uri.EscapeDataString(a);
		}

		return "SSV3|P=" + p + "|A=" + a;
	}

	internal static bool PlaceOrUpdateOnSheet(
		Document doc,
		ViewSheet sheet,
		AssemblyInstance assembly,
		SpoolingManagerSettings settings)
	{
		if (doc == null || sheet == null || assembly == null)
		{
			return false;
		}

		if (settings != null && !settings.PlaceTrackingQrOnSpoolSheets)
		{
			RemoveExistingQrImages(doc, sheet);
			return false;
		}

		string package = ReadPackage(assembly);
		string assemblyName = AssemblyDisplayName.Get(assembly);
		string payload = BuildPayload(package, assemblyName, settings?.QrTrackingUrlBase);
		if (string.IsNullOrWhiteSpace(payload))
		{
			return false;
		}

		BoundingBoxXYZ titleBlock = CreateSpoolSheetsHandler.GetTitleBlockBounds(doc, sheet);
		if (titleBlock?.Min == null)
		{
			return false;
		}

		XYZ lowerLeft = new XYZ(
			titleBlock.Min.X + QrLeftFromTitleBlockInches / 12.0,
			titleBlock.Min.Y + QrBottomFromTitleBlockInches / 12.0,
			0.0);
		double sizeFeet = QrSizeInches / 12.0;

		RemoveExistingQrImages(doc, sheet);

		string pngPath = null;
		try
		{
			pngPath = WriteQrPng(payload);
			ImageTypeOptions typeOptions = new ImageTypeOptions(pngPath, false, ImageTypeSource.Import);
			ImageType imageType = ImageType.Create(doc, typeOptions);
			try
			{
				imageType.Name = ImageTypeNamePrefix + SanitizeTypeName(assemblyName);
			}
			catch
			{
				// Name may collide; placement still works.
			}

			ImagePlacementOptions placement = new ImagePlacementOptions
			{
				Location = lowerLeft,
				PlacementPoint = BoxPlacement.BottomLeft
			};
			ImageInstance instance = ImageInstance.Create(doc, sheet, imageType.Id, placement);
			instance.Width = sizeFeet;
			instance.Height = sizeFeet;
			// Setting Width/Height can scale about center — snap LL back to the QR slot.
			AnchorImageBottomLeft(doc, sheet, instance, lowerLeft);
			TrySetComments(instance, QrMarker + "|" + payload);
			return true;
		}
		finally
		{
			if (!string.IsNullOrWhiteSpace(pngPath))
			{
				try { File.Delete(pngPath); } catch { }
			}
		}
	}

	internal static int EnsureOnSheets(
		Document doc,
		IEnumerable<(ViewSheet Sheet, AssemblyInstance Assembly)> sheetAssemblies,
		SpoolingManagerSettings settings)
	{
		if (doc == null || sheetAssemblies == null)
		{
			return 0;
		}

		int placed = 0;
		foreach ((ViewSheet sheet, AssemblyInstance assembly) in sheetAssemblies)
		{
			if (sheet == null || assembly == null)
			{
				continue;
			}

			try
			{
				if (PlaceOrUpdateOnSheet(doc, sheet, assembly, settings))
				{
					placed++;
				}
			}
			catch
			{
				// Plot ensure must not abort the whole package export.
			}
		}

		return placed;
	}

	private static void AnchorImageBottomLeft(Document doc, ViewSheet sheet, ImageInstance image, XYZ lowerLeft)
	{
		if (doc == null || sheet == null || image == null || lowerLeft == null)
		{
			return;
		}

		BoundingBoxXYZ box = image.get_BoundingBox(sheet);
		if (box?.Min == null)
		{
			return;
		}

		XYZ delta = new XYZ(lowerLeft.X - box.Min.X, lowerLeft.Y - box.Min.Y, 0.0);
		if (delta.GetLength() < 1e-9)
		{
			return;
		}

		ElementTransformUtils.MoveElement(doc, image.Id, delta);
	}

	private static string WriteQrPng(string payload)
	{
		string path = Path.Combine(Path.GetTempPath(), "SSV3-QR-" + Guid.NewGuid().ToString("N") + ".png");
		using (QRCodeGenerator generator = new QRCodeGenerator())
		using (QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q))
		using (QRCode qr = new QRCode(data))
		using (Bitmap bitmap = qr.GetGraphic(20))
		{
			bitmap.Save(path, ImageFormat.Png);
		}

		return path;
	}

	private static void RemoveExistingQrImages(Document doc, ViewSheet sheet)
	{
		List<ElementId> toDelete = new List<ElementId>();
		HashSet<ElementId> typeIds = new HashSet<ElementId>();

		foreach (ImageInstance image in new FilteredElementCollector(doc, sheet.Id)
			.OfClass(typeof(ImageInstance))
			.Cast<ImageInstance>())
		{
			if (!IsTrackingQr(image))
			{
				continue;
			}

			toDelete.Add(image.Id);
			try
			{
				ElementId typeId = image.GetTypeId();
				if (typeId != null && typeId != ElementId.InvalidElementId)
				{
					typeIds.Add(typeId);
				}
			}
			catch
			{
			}
		}

		if (toDelete.Count > 0)
		{
			doc.Delete(toDelete);
		}

		foreach (ElementId typeId in typeIds)
		{
			try
			{
				Element type = doc.GetElement(typeId);
				if (type is ImageType imageType)
				{
					string typeName = imageType.Name ?? string.Empty;
					if (typeName.StartsWith(ImageTypeNamePrefix, StringComparison.OrdinalIgnoreCase)
						|| typeName.StartsWith(LegacyImageTypeNamePrefix, StringComparison.OrdinalIgnoreCase))
					{
						doc.Delete(typeId);
					}
				}
			}
			catch
			{
				// Type still in use elsewhere — leave it.
			}
		}
	}

	private static bool IsTrackingQr(ImageInstance image)
	{
		if (image == null)
		{
			return false;
		}

		string comments = ReadComments(image);
		if (!string.IsNullOrWhiteSpace(comments)
			&& (comments.StartsWith(QrMarker, StringComparison.OrdinalIgnoreCase)
				|| comments.StartsWith(LegacyQrMarker, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		try
		{
			Element type = image.Document?.GetElement(image.GetTypeId());
			string typeName = type?.Name ?? string.Empty;
			if (typeName.StartsWith(ImageTypeNamePrefix, StringComparison.OrdinalIgnoreCase)
				|| typeName.StartsWith(LegacyImageTypeNamePrefix, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		catch
		{
		}

		string name = image.Name ?? string.Empty;
		return name.IndexOf(QrMarker, StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf(LegacyQrMarker, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static string ReadPackage(AssemblyInstance assembly)
	{
		try
		{
			Parameter p = assembly?.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
			if (p != null && p.StorageType == StorageType.String)
			{
				return p.AsString() ?? string.Empty;
			}
		}
		catch
		{
		}

		return string.Empty;
	}

	private static string ReadComments(Element element)
	{
		try
		{
			Parameter p = element?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
			if (p != null && p.StorageType == StorageType.String)
			{
				return p.AsString() ?? string.Empty;
			}
		}
		catch
		{
		}

		return string.Empty;
	}

	private static void TrySetComments(Element element, string value)
	{
		try
		{
			Parameter p = element?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
			if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
			{
				p.Set(value ?? string.Empty);
			}
		}
		catch
		{
		}
	}

	private static string SanitizeTypeName(string name)
	{
		string text = (name ?? "Spool").Trim();
		foreach (char c in Path.GetInvalidFileNameChars())
		{
			text = text.Replace(c, '_');
		}

		if (text.Length > 80)
		{
			text = text.Substring(0, 80);
		}

		return string.IsNullOrWhiteSpace(text) ? "Spool" : text;
	}
}
