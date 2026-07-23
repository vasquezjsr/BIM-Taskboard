using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using Autodesk.Revit.DB;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

[Serializable]
public class SpoolingManagerSettings
{
	public string LogoImagePath { get; set; } = string.Empty;

	public string ThemeId { get; set; } = UiAppearanceSettings.DefaultThemeId;

	public bool UseRevitGraphics { get; set; } = true;

	public bool ButtonClickSoundsEnabled { get; set; } = true;

	public string UiChromeBackground { get; set; } = UiAppearanceSettings.DefaultChromeBackground;

	public string UiListWellBackground { get; set; } = UiAppearanceSettings.DefaultListWellBackground;

	public string UiShortcutTileBackground { get; set; } = UiAppearanceSettings.DefaultShortcutTileBackground;

	public string UiInputBackground { get; set; } = UiAppearanceSettings.DefaultInputBackground;

	public string UiForegroundPrimary { get; set; } = UiAppearanceSettings.DefaultForegroundPrimary;

	public string UiForegroundMuted { get; set; } = UiAppearanceSettings.DefaultForegroundMuted;

	public string UiBorderOuter { get; set; } = UiAppearanceSettings.DefaultBorderOuter;

	public string UiListBorder { get; set; } = UiAppearanceSettings.DefaultListBorder;

	public string TitleBlockName { get; set; } = string.Empty;

	public string TagTypeName { get; set; } = string.Empty;

	public string HangerTagTypeName { get; set; } = string.Empty;

	public string DuctTagTypeName { get; set; } = string.Empty;

	/// <summary>
	/// When true, SS Manager targets native Revit Pipes / Pipe Fittings / Pipe Accessories
	/// (tags, dims, shared-param categories) instead of MEP Fabrication Pipework.
	/// </summary>
	public bool UseNativePipework { get; set; }

	/// <summary>
	/// Spool Content dropdown: Fabrication | NativePipe | Duct | Hangers.
	/// Kept in sync with <see cref="UseNativePipework"/> (NativePipe ⇒ true).
	/// </summary>
	public string SpoolContentMode { get; set; } = "Fabrication";

	/// <summary>Normalized Spool Content tag used by the pane list filter.</summary>
	public string GetSpoolContentModeTag()
	{
		string tag = (SpoolContentMode ?? string.Empty).Trim();
		if (string.Equals(tag, "NativePipe", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(tag, "Families", StringComparison.OrdinalIgnoreCase))
		{
			return "NativePipe";
		}
		if (string.Equals(tag, "Duct", StringComparison.OrdinalIgnoreCase))
		{
			return "Duct";
		}
		if (string.Equals(tag, "Hangers", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(tag, "Hanger", StringComparison.OrdinalIgnoreCase))
		{
			return "Hangers";
		}
		if (UseNativePipework)
		{
			return "NativePipe";
		}
		return "Fabrication";
	}

	public void SetSpoolContentModeTag(string tag)
	{
		if (string.Equals(tag, "NativePipe", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(tag, "Families", StringComparison.OrdinalIgnoreCase))
		{
			SpoolContentMode = "NativePipe";
			UseNativePipework = true;
			return;
		}
		if (string.Equals(tag, "Duct", StringComparison.OrdinalIgnoreCase))
		{
			SpoolContentMode = "Duct";
			UseNativePipework = false;
			return;
		}
		if (string.Equals(tag, "Hangers", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(tag, "Hanger", StringComparison.OrdinalIgnoreCase))
		{
			SpoolContentMode = "Hangers";
			UseNativePipework = false;
			return;
		}
		SpoolContentMode = "Fabrication";
		UseNativePipework = false;
	}

	public bool NumberWeldsEnabled { get; set; }

	public bool WeldLogEnabled { get; set; }

	public bool ContinuationTagsEnabled { get; set; }

	public string AssemblyTagTypeName { get; set; } = string.Empty;

	public string WeldTagTypeName { get; set; } = string.Empty;

	// When true, weld numbers/tags are prefixed with the assembly's Package # and a dash (e.g. "B0001-CHW-11-01").
	public bool WeldTagIncludePackageNumber { get; set; }

	/// <summary>
	/// When true, Item Numbers use the Straight/Fitting/Valve prefix+suffix templates
	/// (e.g. P-001-S / P-001-F / P-001-V) instead of built-in Standard/MMC defaults.
	/// </summary>
	public bool ItemNumberCustomFormatEnabled { get; set; }

	public string ItemNumberStraightPrefix { get; set; } = "P-";

	public string ItemNumberStraightSuffix { get; set; } = "-S";

	public string ItemNumberFittingPrefix { get; set; } = "P-";

	public string ItemNumberFittingSuffix { get; set; } = "-F";

	public string ItemNumberValvePrefix { get; set; } = "P-";

	public string ItemNumberValveSuffix { get; set; } = "-V";

	/// <summary>
	/// Starting token for the Straight series (e.g. 1, 01, 001, A).
	/// Digits imply zero-pad width; letters advance A,B,C…
	/// Empty until <see cref="NormalizeItemNumberStarts"/> fills from <see cref="ItemNumberDigits"/>.
	/// </summary>
	public string ItemNumberStraightStart { get; set; } = string.Empty;

	public string ItemNumberFittingStart { get; set; } = string.Empty;

	public string ItemNumberValveStart { get; set; } = string.Empty;

	/// <summary>
	/// Legacy zero-pad width. Prefer <see cref="ItemNumberStraightStart"/> etc.
	/// Kept so older settings XML still round-trips; new UI writes Starts instead.
	/// </summary>
	public int ItemNumberDigits { get; set; } = 3;

	public string WeldLogTextNoteTypeName { get; set; } = string.Empty;

	public string WeldLogSourceViewLabel { get; set; } = "3D Ortho";

	public string ViewportTypeName { get; set; } = string.Empty;

	/// <summary>Legacy primary schedule name. Prefer <see cref="ScheduleOptions"/>.</summary>
	public string ScheduleName { get; set; } = string.Empty;

	/// <summary>Legacy second-schedule toggle. Prefer <see cref="ScheduleOptions"/>.</summary>
	public bool SecondScheduleEnabled { get; set; }

	/// <summary>Legacy second schedule name. Prefer <see cref="ScheduleOptions"/>.</summary>
	public string ScheduleName2 { get; set; } = string.Empty;

	/// <summary>Legacy name-only list. Prefer <see cref="ScheduleOptions"/>.</summary>
	[XmlArray("ScheduleNames")]
	[XmlArrayItem("Name")]
	public List<string> ScheduleNames { get; set; } = new List<string>();

	/// <summary>Schedules to place on each spool sheet with Top Left / Top Right placement.</summary>
	[XmlArray("ScheduleOptions")]
	[XmlArrayItem("Schedule")]
	public List<SpoolScheduleOption> ScheduleOptions { get; set; } = new List<SpoolScheduleOption>();

	[XmlElement(IsNullable = true)]
	public double? ScheduleInsetFromTitleBlockLeftInches { get; set; }

	[XmlElement(IsNullable = true)]
	public double? ScheduleInsetFromTitleBlockTopInches { get; set; }

	[XmlElement(IsNullable = true)]
	public double? WeldLogInsetFromTitleBlockLeftInches { get; set; }

	[XmlElement(IsNullable = true)]
	public double? WeldLogInsetFromTitleBlockBottomInches { get; set; }

	[XmlElement(IsNullable = true)]
	public double? WeldLogRowSpacingInches { get; set; }

	[XmlElement(IsNullable = true)]
	public double? WeldLogProjectStripHeightInches { get; set; }

	[XmlElement(IsNullable = true)]
	public int? WeldLogMaxRows { get; set; }

	[XmlElement(IsNullable = true)]
	public double? WeldLogTextOffsetLeftInches { get; set; }

	[XmlElement(IsNullable = true)]
	public double? WeldLogTextOffsetUpInches { get; set; }

	public string AutoDimensionTypeName { get; set; } = string.Empty;

	public bool AutoDimAnnotations { get; set; }

	/// <summary>
	/// Native pipework only: also dimension each fitting to itself
	/// (fitting end to fitting centerline, e.g. elbow takeout).
	/// </summary>
	public bool NativeFittingSelfDimensionsEnabled { get; set; }

	/// <summary>When true, create/refresh/plot places a tracking QR in the titleblock QR slot.</summary>
	public bool PlaceTrackingQrOnSpoolSheets { get; set; } = true;

	/// <summary>
	/// Optional URL base for QR payload. Blank encodes <c>SSV3|P=…|A=…</c>;
	/// when set, encodes <c>{base}?p=…&amp;a=…</c> for a future tracking app.
	/// </summary>
	public string QrTrackingUrlBase { get; set; } = string.Empty;

	public string PlotPackagesPrinterName { get; set; } = "Bluebeam PDF";

	public string PlotPackagesPrintSettingName { get; set; } = "11x17 Spool Sheets";

	/// <summary>Comma/newline keywords that map parts to TigerStop Copper files.</summary>
	public string TigerStopCopperKeywords { get; set; } = "COPPER, CU ";

	/// <summary>Comma/newline keywords that map parts to TigerStop PVC files.</summary>
	public string TigerStopPvcKeywords { get; set; } = "PVC, CPVC";

	/// <summary>Comma/newline keywords that map parts to PCF Copper files.</summary>
	public string PcfCopperKeywords { get; set; } = "COPPER, CU";

	/// <summary>Comma/newline keywords that map parts to PCF PVC files.</summary>
	public string PcfPvcKeywords { get; set; } = "PVC, CPVC";

	/// <summary>Comma/newline keywords that map parts to PCF Steel files.</summary>
	public string PcfSteelKeywords { get; set; } = "STEEL, CARBON STEEL, STAINLESS, SS, CS";

	/// <summary>Comma/newline keywords that map parts to PCF Cast Iron files.</summary>
	public string PcfCastIronKeywords { get; set; } = "CAST IRON, DUCTILE IRON, GRAY IRON, GREY IRON";

	/// <summary>Ordered TigerStop CSV columns (comma-separated). LengthInches is decimal inches.</summary>
	public string TigerStopColumns { get; set; } = "Quantity,LengthInches,Package,ItemNumber,Size,LengthFtIn,Material,Spool";

	/// <summary>Ordered PCF component fields (comma-separated).</summary>
	public string PcfFields { get; set; } = "COMPONENT-IDENTIFIER,ITEM-CODE,SKEY,PIPING-SPEC,SPOOL-ID,END-POINT";

	/// <summary>
	/// Piping specification / catalog workbook path. New unique Item Codes from PCF exports are appended.
	/// Empty uses Documents\Spooling Savant Test Reports\Piping Specification Catalog.xlsx.
	/// </summary>
	public string PipingSpecCatalogPath { get; set; } = string.Empty;

	/// <summary>
	/// Live BIM Boardroom API base URL (loopback). Used for project + Spooling task pickers.
	/// Default: http://127.0.0.1:17321
	/// </summary>
	public string BoardroomApiBaseUrl { get; set; } = "http://127.0.0.1:17321";

	/// <summary>Legacy CSV path (unused; kept so older settings XML still deserializes).</summary>
	public string BoardroomProjectsCsvPath { get; set; } = string.Empty;

	public bool UseRegularSheetBranch { get; set; }

	[XmlElement("MmcSpoolSheetScaleInchesPerFoot", IsNullable = true)]
	public double? SpoolSheetScaleInchesPerFoot { get; set; }

	public bool Include3DOrtho { get; set; }

	public string Direction3D { get; set; } = "ISO";

	public bool Tag3D { get; set; }

	public bool AutoDim3D { get; set; }

	public string Placement3D { get; set; } = "Top Left";

	public string Template3D { get; set; } = string.Empty;

	public bool IncludeBackView { get; set; }

	public bool TagBackView { get; set; }

	public bool AutoDimBackView { get; set; }

	public string BackViewRotation { get; set; } = "0°";

	public string PlacementBackView { get; set; } = "Top Left";

	public string TemplateBackView { get; set; } = string.Empty;

	public bool IncludeFrontView { get; set; }

	public bool TagFrontView { get; set; }

	public bool AutoDimFrontView { get; set; }

	public string FrontViewRotation { get; set; } = "0°";

	public string PlacementFrontView { get; set; } = "Top Left";

	public string TemplateFrontView { get; set; } = string.Empty;

	public bool IncludeLeftView { get; set; }

	public bool TagLeftView { get; set; }

	public bool AutoDimLeftView { get; set; }

	public string LeftViewRotation { get; set; } = "0°";

	public string PlacementLeftView { get; set; } = "Top Left";

	public string TemplateLeftView { get; set; } = string.Empty;

	public bool IncludeRightView { get; set; }

	public bool TagRightView { get; set; }

	public bool AutoDimRightView { get; set; }

	public string RightViewRotation { get; set; } = "0°";

	public string PlacementRightView { get; set; } = "Top Left";

	public string TemplateRightView { get; set; } = string.Empty;

	public bool IncludeTopView { get; set; }

	public bool TagTopView { get; set; }

	public bool AutoDimTopView { get; set; }

	public string TopViewRotation { get; set; } = "0°";

	public string PlacementTopView { get; set; } = "Top Left";

	public string TemplateTopView { get; set; } = string.Empty;

	public static string SettingsFolderPath => InstallLayout.GetPreferredModuleFolder();

	public static string SettingsFilePath => GetSettingsFilePathForKind(SpoolingManagerKind.Standard);

	/// <summary>
	/// Settings are stored per Revit project. Entry points (pane load/refresh, settings window,
	/// external-event handlers) call <see cref="SetActiveProject"/> so Load/Save resolve to the
	/// current project's own settings file. A new project starts from defaults until saved.
	/// </summary>
	private static string _activeProjectKey;

	public static void SetActiveProject(Document doc)
	{
		_activeProjectKey = ComputeProjectKey(doc);
	}

	/// <summary>
	/// Stable per-project folder name. Prefers ProjectInformation UniqueId (same for all local
	/// copies of a central model and stable across renames), falling back to the model path/title.
	/// </summary>
	private static string ComputeProjectKey(Document doc)
	{
		if (doc == null)
		{
			return null;
		}
		string stableId = null;
		try
		{
			stableId = doc.ProjectInformation?.UniqueId;
		}
		catch
		{
		}
		if (string.IsNullOrWhiteSpace(stableId))
		{
			try
			{
				stableId = doc.PathName;
			}
			catch
			{
			}
		}
		string title = null;
		try
		{
			title = doc.Title;
		}
		catch
		{
		}
		if (string.IsNullOrWhiteSpace(stableId))
		{
			stableId = title;
		}
		if (string.IsNullOrWhiteSpace(stableId))
		{
			return null;
		}
		string readable = SanitizeForFolderName(title);
		string hash = ComputeShortHash(stableId);
		return string.IsNullOrEmpty(readable) ? hash : readable + "_" + hash;
	}

	private static string SanitizeForFolderName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}
		char[] invalid = Path.GetInvalidFileNameChars();
		StringBuilder sb = new StringBuilder(value.Length);
		foreach (char c in value.Trim())
		{
			sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
		}
		string result = sb.ToString();
		return result.Length > 60 ? result.Substring(0, 60) : result;
	}

	private static string ComputeShortHash(string value)
	{
		using SHA256 sha = SHA256.Create();
		byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
		StringBuilder sb = new StringBuilder(16);
		for (int i = 0; i < 8; i++)
		{
			sb.Append(bytes[i].ToString("x2"));
		}
		return sb.ToString();
	}

	public double GetSpoolSheetScaleInchesPerFoot(SpoolingManagerKind kind)
	{
		if (SpoolSheetScaleInchesPerFoot.HasValue && SpoolSheetScaleInchesPerFoot.Value > 0.0)
		{
			return SpoolSheetScaleInchesPerFoot.Value;
		}
		if (!kind.IsMmcStyle())
		{
			return 0.5;
		}
		return 1.5;
	}

	public static string GetSettingsFilePathForKind(SpoolingManagerKind kind)
	{
		string fileName = kind switch
		{
			SpoolingManagerKind.Mmc => "MmcSpoolingManagerSettings.xml", 
			SpoolingManagerKind.MmcTesting => "MmcSpoolingManagerTestingSettings.xml", 
			SpoolingManagerKind.AutoDimensionLab => "SpoolingManagerAutoDimensionLabSettings.xml", 
			_ => "SpoolingManagerSettings.xml", 
		};
		string projectKey = _activeProjectKey;
		if (!string.IsNullOrEmpty(projectKey))
		{
			return Path.Combine(SettingsFolderPath, "Projects", projectKey, fileName);
		}
		// No project context (e.g. appearance migration at startup) — legacy shared file.
		return Path.Combine(SettingsFolderPath, fileName);
	}

	public static SpoolingManagerSettings Load()
	{
		return Load(SpoolingManagerKind.Standard);
	}

	public static SpoolingManagerSettings Load(SpoolingManagerKind kind)
	{
		try
		{
			string settingsFilePathForKind = GetSettingsFilePathForKind(kind);
			if (!File.Exists(settingsFilePathForKind))
			{
				if (kind == SpoolingManagerKind.MmcTesting)
				{
					return Load(SpoolingManagerKind.Mmc);
				}
				SpoolingManagerSettings spoolingManagerSettings = CreateDefaults(kind);
				RepairSpoolSheetViewScale(kind, spoolingManagerSettings);
				RepairLogoImagePathIfStale(kind, spoolingManagerSettings);
				spoolingManagerSettings.NormalizeScheduleOptions();
				spoolingManagerSettings.NormalizeItemNumberStarts();
				return spoolingManagerSettings;
			}
			XmlSerializer xmlSerializer = new XmlSerializer(typeof(SpoolingManagerSettings));
			using FileStream stream = File.OpenRead(settingsFilePathForKind);
			SpoolingManagerSettings spoolingManagerSettings2 = xmlSerializer.Deserialize(stream) as SpoolingManagerSettings;
			spoolingManagerSettings2 = spoolingManagerSettings2 ?? CreateDefaults(kind);
			RepairUseRegularSheetBranchForLegacyMmc(kind, settingsFilePathForKind, spoolingManagerSettings2);
			RepairSpoolSheetViewScale(kind, spoolingManagerSettings2);
			RepairAutoDimFlagsForIncludedViews(kind, settingsFilePathForKind, spoolingManagerSettings2);
			RepairLogoImagePathIfStale(kind, spoolingManagerSettings2);
			spoolingManagerSettings2.NormalizeScheduleOptions();
			spoolingManagerSettings2.NormalizeItemNumberStarts();
			return spoolingManagerSettings2;
		}
		catch
		{
			SpoolingManagerSettings spoolingManagerSettings3 = CreateDefaults(kind);
			RepairSpoolSheetViewScale(kind, spoolingManagerSettings3);
			RepairLogoImagePathIfStale(kind, spoolingManagerSettings3);
			spoolingManagerSettings3.NormalizeScheduleOptions();
			spoolingManagerSettings3.NormalizeItemNumberStarts();
			return spoolingManagerSettings3;
		}
	}

	private static void RepairUseRegularSheetBranchForLegacyMmc(SpoolingManagerKind kind, string settingsFilePath, SpoolingManagerSettings settings)
	{
		if (settings == null || !kind.IsMmcStyle() || string.IsNullOrEmpty(settingsFilePath) || !File.Exists(settingsFilePath))
		{
			return;
		}
		try
		{
			if (!File.ReadAllText(settingsFilePath).Contains("UseRegularSheetBranch"))
			{
				settings.UseRegularSheetBranch = true;
			}
		}
		catch
		{
		}
	}

	private static void RepairAutoDimFlagsForIncludedViews(SpoolingManagerKind kind, string settingsFilePath, SpoolingManagerSettings settings)
	{
		if (settings == null || kind.IsMmcStyle() || string.IsNullOrEmpty(settingsFilePath) || !File.Exists(settingsFilePath))
		{
			return;
		}
		try
		{
			// Only migrate settings saved before per-view Auto Dim flags existed.
			// Re-running on every load forced Auto Dim back on even after the user turned it off.
			if (File.ReadAllText(settingsFilePath).IndexOf("AutoDim", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return;
			}
		}
		catch
		{
			return;
		}
		if (settings.IncludeBackView && !settings.AutoDimBackView)
		{
			settings.AutoDimBackView = true;
		}
		if (settings.IncludeFrontView && !settings.AutoDimFrontView)
		{
			settings.AutoDimFrontView = true;
		}
		if (settings.IncludeLeftView && !settings.AutoDimLeftView)
		{
			settings.AutoDimLeftView = true;
		}
		if (settings.IncludeRightView && !settings.AutoDimRightView)
		{
			settings.AutoDimRightView = true;
		}
		if (settings.IncludeTopView && !settings.AutoDimTopView)
		{
			settings.AutoDimTopView = true;
		}
	}

	private static void RepairSpoolSheetViewScale(SpoolingManagerKind kind, SpoolingManagerSettings settings)
	{
		if (settings != null && kind != SpoolingManagerKind.AutoDimensionLab && (!settings.SpoolSheetScaleInchesPerFoot.HasValue || settings.SpoolSheetScaleInchesPerFoot.Value <= 0.0))
		{
			settings.SpoolSheetScaleInchesPerFoot = (kind.IsMmcStyle() ? 1.5 : 0.5);
		}
	}

	private static void RepairLogoImagePathIfStale(SpoolingManagerKind kind, SpoolingManagerSettings settings)
	{
		if (settings == null)
		{
			return;
		}
		string settingsFolderPath = SettingsFolderPath;
		string text;
		switch (kind)
		{
		case SpoolingManagerKind.Mmc:
		case SpoolingManagerKind.MmcTesting:
			text = "MmcLogoImage";
			break;
		case SpoolingManagerKind.AutoDimensionLab:
			text = "AutoDimLabLogoImage";
			break;
		default:
			text = "LogoImage";
			break;
		}
		string text2 = settings.LogoImagePath?.Trim();
		if (!string.IsNullOrEmpty(text2))
		{
			try
			{
				if (File.Exists(Path.GetFullPath(text2)))
				{
					return;
				}
			}
			catch
			{
			}
		}
		if (!Directory.Exists(settingsFolderPath))
		{
			return;
		}
		string[] collection = new string[4] { ".png", ".jpg", ".jpeg", ".bmp" };
		HashSet<string> allowed = new HashSet<string>(collection, StringComparer.OrdinalIgnoreCase);
		string[] array = (from p in Directory.GetFiles(settingsFolderPath, text + ".*")
			where allowed.Contains(Path.GetExtension(p))
			select p).ToArray();
		if (array.Length == 0)
		{
			return;
		}
		try
		{
			string fullName = (from p in array
				select new FileInfo(p) into f
				orderby f.LastWriteTimeUtc descending
				select f).First().FullName;
			settings.LogoImagePath = Path.GetFullPath(fullName);
		}
		catch
		{
		}
	}

	private static SpoolingManagerSettings CreateDefaults(SpoolingManagerKind kind)
	{
		SpoolingManagerSettings spoolingManagerSettings = new SpoolingManagerSettings();
		if (kind == SpoolingManagerKind.AutoDimensionLab || kind == SpoolingManagerKind.Standard)
		{
			spoolingManagerSettings.IncludeTopView = true;
			spoolingManagerSettings.TagTopView = true;
			spoolingManagerSettings.AutoDimTopView = true;
			spoolingManagerSettings.PlacementTopView = "Bottom Right";
		}
		return spoolingManagerSettings;
	}

	public void Save()
	{
		Save(SpoolingManagerKind.Standard);
	}

	public void Save(SpoolingManagerKind kind)
	{
		NormalizeScheduleOptions();
		NormalizeItemNumberStarts();
		string settingsFilePath = GetSettingsFilePathForKind(kind);
		string directory = Path.GetDirectoryName(settingsFilePath);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}
		XmlSerializer xmlSerializer = new XmlSerializer(typeof(SpoolingManagerSettings));
		using FileStream stream = File.Create(settingsFilePath);
		xmlSerializer.Serialize(stream, this);
	}

	/// <summary>
	/// Returns configured schedules with placement, migrating legacy ScheduleName / ScheduleNames when needed.
	/// </summary>
	public List<SpoolScheduleOption> GetEffectiveScheduleOptions()
	{
		NormalizeScheduleOptions();
		return ScheduleOptions
			.Where(option => option != null && !string.IsNullOrWhiteSpace(option.Name))
			.Select(option => new SpoolScheduleOption
			{
				Name = option.Name.Trim(),
				Placement = SpoolScheduleOption.NormalizePlacement(option.Placement)
			})
			.ToList();
	}

	public List<string> GetEffectiveScheduleNames()
	{
		return GetEffectiveScheduleOptions().Select(option => option.Name).ToList();
	}

	public void NormalizeScheduleOptions()
	{
		if (ScheduleOptions == null)
		{
			ScheduleOptions = new List<SpoolScheduleOption>();
		}

		if (ScheduleNames == null)
		{
			ScheduleNames = new List<string>();
		}

		if (ScheduleOptions.Count == 0)
		{
			if (ScheduleNames.Count > 0)
			{
				for (int i = 0; i < ScheduleNames.Count; i++)
				{
					if (string.IsNullOrWhiteSpace(ScheduleNames[i]))
					{
						continue;
					}

					ScheduleOptions.Add(new SpoolScheduleOption
					{
						Name = ScheduleNames[i].Trim(),
						Placement = i == 0
							? SpoolScheduleOption.PlacementTopLeft
							: (i == 1 ? SpoolScheduleOption.PlacementTopRight : SpoolScheduleOption.PlacementTopLeft)
					});
				}
			}
			else
			{
				if (!string.IsNullOrWhiteSpace(ScheduleName))
				{
					ScheduleOptions.Add(new SpoolScheduleOption
					{
						Name = ScheduleName.Trim(),
						Placement = SpoolScheduleOption.PlacementTopLeft
					});
				}

				if (SecondScheduleEnabled && !string.IsNullOrWhiteSpace(ScheduleName2))
				{
					ScheduleOptions.Add(new SpoolScheduleOption
					{
						Name = ScheduleName2.Trim(),
						Placement = SpoolScheduleOption.PlacementTopRight
					});
				}
			}
		}

		for (int i = ScheduleOptions.Count - 1; i >= 0; i--)
		{
			SpoolScheduleOption option = ScheduleOptions[i];
			if (option == null || string.IsNullOrWhiteSpace(option.Name))
			{
				ScheduleOptions.RemoveAt(i);
				continue;
			}

			option.Name = option.Name.Trim();
			option.Placement = SpoolScheduleOption.NormalizePlacement(option.Placement);
		}

		ScheduleNames = ScheduleOptions.Select(option => option.Name).ToList();
		ScheduleName = ScheduleNames.Count > 0 ? ScheduleNames[0] : string.Empty;
		SecondScheduleEnabled = ScheduleNames.Count > 1;
		ScheduleName2 = ScheduleNames.Count > 1 ? ScheduleNames[1] : string.Empty;
	}

	/// <summary>
	/// Fills empty Start tokens from legacy <see cref="ItemNumberDigits"/> (e.g. 3 → 001).
	/// </summary>
	public void NormalizeItemNumberStarts()
	{
		string fromDigits = BuildDefaultStartFromDigits(ItemNumberDigits);
		ItemNumberStraightStart = CoalesceItemNumberStart(ItemNumberStraightStart, fromDigits);
		ItemNumberFittingStart = CoalesceItemNumberStart(ItemNumberFittingStart, fromDigits);
		ItemNumberValveStart = CoalesceItemNumberStart(ItemNumberValveStart, fromDigits);

		int inferred = InferDigitsFromStart(ItemNumberStraightStart);
		if (inferred > 0)
		{
			ItemNumberDigits = inferred;
		}
	}

	private static string BuildDefaultStartFromDigits(int digits)
	{
		int width = digits < 1 ? 3 : (digits > 8 ? 8 : digits);
		return "1".PadLeft(width, '0');
	}

	private static string CoalesceItemNumberStart(string value, string fallback)
	{
		string text = (value ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return fallback;
		}

		bool digits = true;
		bool letters = true;
		for (int i = 0; i < text.Length; i++)
		{
			if (!char.IsDigit(text[i]))
			{
				digits = false;
			}

			if (!char.IsLetter(text[i]))
			{
				letters = false;
			}
		}

		if (digits || letters)
		{
			return text;
		}

		return fallback;
	}

	private static int InferDigitsFromStart(string start)
	{
		string text = (start ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return 0;
		}

		for (int i = 0; i < text.Length; i++)
		{
			if (!char.IsDigit(text[i]))
			{
				return 0;
			}
		}

		return Math.Min(8, Math.Max(1, text.Length));
	}
}
