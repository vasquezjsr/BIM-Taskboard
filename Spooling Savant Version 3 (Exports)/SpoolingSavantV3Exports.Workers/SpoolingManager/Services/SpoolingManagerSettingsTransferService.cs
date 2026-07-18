using System;
using System.IO;
using System.Xml.Serialization;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Export/import SS Manager settings for switching between client presets.
/// Logo images are copied beside the settings file using a portable relative file name.
/// </summary>
internal static class SpoolingManagerSettingsTransferService
{
	public const string ExportFilter = "Spooling Savant V3 (Exports) settings (*.ssmgr.xml)|*.ssmgr.xml|XML files (*.xml)|*.xml";

	public static bool TryExport(SpoolingManagerSettings settings, SpoolingManagerKind kind, string exportFilePath, out string errorMessage)
	{
		errorMessage = null;
		if (settings == null)
		{
			errorMessage = "No settings to export.";
			return false;
		}
		if (string.IsNullOrWhiteSpace(exportFilePath))
		{
			errorMessage = "Choose a file path for export.";
			return false;
		}
		try
		{
			string fullExportPath = Path.GetFullPath(exportFilePath);
			string exportDirectory = Path.GetDirectoryName(fullExportPath);
			if (string.IsNullOrEmpty(exportDirectory))
			{
				errorMessage = "Could not resolve the export folder.";
				return false;
			}
			Directory.CreateDirectory(exportDirectory);
			SpoolingManagerSettings exportCopy = CloneSettings(settings);
			TryBundleLogoForExport(exportCopy, fullExportPath, exportDirectory);
			WriteSettingsXml(fullExportPath, exportCopy);
			return true;
		}
		catch (Exception ex)
		{
			errorMessage = ex.Message;
			return false;
		}
	}

	public static bool TryImport(string importFilePath, SpoolingManagerKind kind, out SpoolingManagerSettings settings, out string errorMessage)
	{
		settings = null;
		errorMessage = null;
		if (string.IsNullOrWhiteSpace(importFilePath) || !File.Exists(importFilePath))
		{
			errorMessage = "Choose a valid settings file to import.";
			return false;
		}
		try
		{
			string fullImportPath = Path.GetFullPath(importFilePath);
			settings = ReadSettingsXml(fullImportPath);
			if (settings == null)
			{
				errorMessage = "The selected file is not a valid Spooling Savant V3 (Exports) settings file.";
				return false;
			}
			NormalizeImportedSettings(settings, kind);
			TryInstallImportedLogo(settings, fullImportPath, kind);
			return true;
		}
		catch (Exception ex)
		{
			settings = null;
			errorMessage = ex.Message;
			return false;
		}
	}

	public static string BuildDefaultExportFileName(SpoolingManagerKind kind)
	{
		string suffix = kind switch
		{
			SpoolingManagerKind.Mmc => "MMC",
			SpoolingManagerKind.MmcTesting => "MMC-Testing",
			SpoolingManagerKind.AutoDimensionLab => "AutoDimLab",
			_ => "Standard"
		};
		return "SpoolingSavantV3Exports-" + suffix + "-Settings.ssmgr.xml";
	}

	private static SpoolingManagerSettings CloneSettings(SpoolingManagerSettings settings)
	{
		XmlSerializer serializer = new XmlSerializer(typeof(SpoolingManagerSettings));
		using MemoryStream stream = new MemoryStream();
		serializer.Serialize(stream, settings);
		stream.Position = 0L;
		return serializer.Deserialize(stream) as SpoolingManagerSettings ?? new SpoolingManagerSettings();
	}

	private static void WriteSettingsXml(string path, SpoolingManagerSettings settings)
	{
		XmlSerializer serializer = new XmlSerializer(typeof(SpoolingManagerSettings));
		using FileStream stream = File.Create(path);
		serializer.Serialize(stream, settings);
	}

	private static SpoolingManagerSettings ReadSettingsXml(string path)
	{
		XmlSerializer serializer = new XmlSerializer(typeof(SpoolingManagerSettings));
		using FileStream stream = File.OpenRead(path);
		return serializer.Deserialize(stream) as SpoolingManagerSettings;
	}

	private static void TryBundleLogoForExport(SpoolingManagerSettings settings, string exportFilePath, string exportDirectory)
	{
		string logoPath = settings.LogoImagePath?.Trim();
		if (string.IsNullOrEmpty(logoPath))
		{
			return;
		}
		try
		{
			logoPath = Path.GetFullPath(logoPath);
		}
		catch
		{
			return;
		}
		if (!File.Exists(logoPath))
		{
			settings.LogoImagePath = string.Empty;
			return;
		}
		string extension = Path.GetExtension(logoPath);
		if (string.IsNullOrEmpty(extension))
		{
			extension = ".png";
		}
		string bundledLogoFileName = Path.GetFileNameWithoutExtension(exportFilePath) + "-logo" + extension;
		string bundledLogoPath = Path.Combine(exportDirectory, bundledLogoFileName);
		File.Copy(logoPath, bundledLogoPath, overwrite: true);
		settings.LogoImagePath = bundledLogoFileName;
	}

	private static void TryInstallImportedLogo(SpoolingManagerSettings settings, string importFilePath, SpoolingManagerKind kind)
	{
		string logoReference = settings.LogoImagePath?.Trim();
		if (string.IsNullOrEmpty(logoReference))
		{
			return;
		}
		string sourceLogoPath = ResolveLogoSourcePath(logoReference, importFilePath);
		if (string.IsNullOrEmpty(sourceLogoPath) || !File.Exists(sourceLogoPath))
		{
			settings.LogoImagePath = string.Empty;
			return;
		}
		string settingsFolderPath = SpoolingManagerSettings.SettingsFolderPath;
		Directory.CreateDirectory(settingsFolderPath);
		string extension = Path.GetExtension(sourceLogoPath);
		if (string.IsNullOrEmpty(extension))
		{
			extension = ".png";
		}
		string destinationPath = Path.Combine(settingsFolderPath, GetLogoBaseFileName(kind) + extension);
		File.Copy(sourceLogoPath, destinationPath, overwrite: true);
		settings.LogoImagePath = Path.GetFullPath(destinationPath);
	}

	private static string ResolveLogoSourcePath(string logoReference, string importFilePath)
	{
		try
		{
			if (Path.IsPathRooted(logoReference) && File.Exists(logoReference))
			{
				return Path.GetFullPath(logoReference);
			}
		}
		catch
		{
		}
		string importDirectory = Path.GetDirectoryName(importFilePath);
		if (string.IsNullOrEmpty(importDirectory))
		{
			return null;
		}
		string relativeCandidate = Path.Combine(importDirectory, logoReference);
		if (File.Exists(relativeCandidate))
		{
			return Path.GetFullPath(relativeCandidate);
		}
		string fileNameCandidate = Path.Combine(importDirectory, Path.GetFileName(logoReference));
		if (File.Exists(fileNameCandidate))
		{
			return Path.GetFullPath(fileNameCandidate);
		}
		return null;
	}

	private static string GetLogoBaseFileName(SpoolingManagerKind kind)
	{
		return kind switch
		{
			SpoolingManagerKind.Mmc or SpoolingManagerKind.MmcTesting => "MmcLogoImage",
			SpoolingManagerKind.AutoDimensionLab => "AutoDimLabLogoImage",
			_ => "LogoImage"
		};
	}

	private static void NormalizeImportedSettings(SpoolingManagerSettings settings, SpoolingManagerKind kind)
	{
		if (settings == null)
		{
			return;
		}
		if (kind != SpoolingManagerKind.AutoDimensionLab && (!settings.SpoolSheetScaleInchesPerFoot.HasValue || settings.SpoolSheetScaleInchesPerFoot.Value <= 0.0))
		{
			settings.SpoolSheetScaleInchesPerFoot = (kind.IsMmcStyle() ? 1.5 : 0.5);
		}
	}
}
