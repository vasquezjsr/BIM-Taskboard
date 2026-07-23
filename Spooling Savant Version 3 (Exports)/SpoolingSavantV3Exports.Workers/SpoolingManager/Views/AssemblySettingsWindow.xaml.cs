using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;
using SpoolingSavantV3Exports.Workers.UI;
using SpoolingSavantV3Exports.Workers;
using Microsoft.Win32;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class AssemblySettingsWindow : Window
{
	private readonly UIApplication _uiapp;

	private readonly SpoolingManagerKind _productKind;

	private SpoolingManagerSettings _settings;

	private static readonly System.Windows.Media.Brush ErrorBorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CC4444"));

	private static readonly System.Windows.Media.Brush ErrorBackgroundBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFD6D6"));

	private static readonly string[] OrthoDirectionOptions = new string[8] { "NW", "N", "NE", "W", "E", "SW", "S", "SE" };

	private static readonly string[] ElevationViewRotationOptions = new string[4] { "0°", "90° CW", "90° CCW", "180°" };

	private static readonly (string Label, double InchesPerFoot)[] SpoolSheetViewScaleOptions = new(string, double)[8]
	{
		("1/8\" = 1'-0\"", 0.125),
		("1/4\" = 1'-0\"", 0.25),
		("3/8\" = 1'-0\"", 0.375),
		("1/2\" = 1'-0\"", 0.5),
		("3/4\" = 1'-0\"", 0.75),
		("1\" = 1'-0\"", 1.0),
		("1 1/2\" = 1'-0\"", 1.5),
		("3\" = 1'-0\"", 3.0)
	};

	internal ComboBox cmb3DDirection;

	internal ComboBox cmb3DPlacement;

	internal ComboBox cmbBackRotation;

	internal ComboBox cmbFrontRotation;

	internal ComboBox cmbLeftRotation;

	internal ComboBox cmbRightRotation;

	internal ComboBox cmbTopRotation;

	internal ComboBox cmbBackPlacement;

	internal ComboBox cmbFrontPlacement;

	internal ComboBox cmbLeftPlacement;

	internal ComboBox cmbRightPlacement;

	internal ComboBox cmbTopPlacement;

	internal ComboBox cmbTitleBlock;

	internal ComboBox cmbTagType;

	internal TextBlock txtTagTypeLabel;

	internal ComboBox cmbHangerTagType;

	internal ComboBox cmbDuctTagType;

	internal ComboBox cmbWeldTagType;

	internal ComboBox cmbAssemblyTagType;

	internal ComboBox cmbWeldLogTextType;

	internal ComboBox cmbWeldLogSourceView;

	internal ComboBox cmbViewportType;

	internal StackPanel pnlScheduleRows;

	internal Button btnAddSchedule;

	private readonly List<string> _availableScheduleNames = new List<string>();

	private readonly List<(ComboBox NameCombo, ComboBox PlacementCombo)> _scheduleRows = new List<(ComboBox, ComboBox)>();

	internal ComboBox cmb3DTemplate;

	internal ComboBox cmbBackTemplate;

	internal ComboBox cmbFrontTemplate;

	internal ComboBox cmbLeftTemplate;

	internal ComboBox cmbRightTemplate;

	internal ComboBox cmbTopTemplate;

	internal CheckBox chk3DOrtho;

	internal CheckBox chk3DTag;

	internal CheckBox chk3DAutoDim;

	internal CheckBox chkBackView;

	internal CheckBox chkBackTag;

	internal CheckBox chkBackAutoDim;

	internal CheckBox chkFrontView;

	internal CheckBox chkFrontTag;

	internal CheckBox chkFrontAutoDim;

	internal CheckBox chkLeftView;

	internal CheckBox chkLeftTag;

	internal CheckBox chkLeftAutoDim;

	internal CheckBox chkRightView;

	internal CheckBox chkRightTag;

	internal CheckBox chkRightAutoDim;

	internal CheckBox chkTopView;

	internal CheckBox chkTopTag;

	internal CheckBox chkTopAutoDim;

	internal CheckBox chkNumberWelds;

	internal CheckBox chkFillWeldLog;

	internal CheckBox chkContinuationTags;

	internal CheckBox chkWeldTagPackagePrefix;

	internal CheckBox chkItemNumberCustomFormat;

	internal System.Windows.Controls.TextBox txtItemNumberStraightPrefix;

	internal System.Windows.Controls.TextBox txtItemNumberStraightSuffix;

	internal System.Windows.Controls.TextBox txtItemNumberFittingPrefix;

	internal System.Windows.Controls.TextBox txtItemNumberFittingSuffix;

	internal System.Windows.Controls.TextBox txtItemNumberValvePrefix;

	internal System.Windows.Controls.TextBox txtItemNumberValveSuffix;

	internal System.Windows.Controls.TextBox txtItemNumberStraightStart;

	internal System.Windows.Controls.TextBox txtItemNumberFittingStart;

	internal System.Windows.Controls.TextBox txtItemNumberValveStart;

	internal CheckBox chkPlaceTrackingQr;

	internal System.Windows.Controls.TextBox txtQrTrackingUrlBase;

	internal TextBlock txtSettingsHeaderTitle;

	internal TextBlock txtTabHeaderSheetSetup;

	internal TextBlock txtTabHeaderSchedules;

	internal TextBlock txtTabHeaderAnnotations;

	internal TextBlock txtTabHeaderTigerStop;

	internal TextBlock txtTabHeaderPcfFiles;

	internal TextBlock txtTabHeaderBoardroom;

	internal TextBlock txtSettingsHeaderSubtitle;

	internal TextBlock lblViewScale;

	internal ComboBox cmbViewScale;

	internal ComboBox cmbAutoDimensionType;

	internal TextBlock lblAutoDimensionType;

	internal TextBlock lblAutoDimAnnotations;

	internal CheckBox chkAutoDimAnnotations;

	internal TextBlock lblAutoDimFittingSelf;

	internal CheckBox chkAutoDimFittingSelf;

	internal Grid grdSpoolViews;

	internal TextBlock txtViewsAutoDimHeader;

	internal Button btnLayoutSettings;

	internal TextBlock txtLayoutSettingsPrompt;

	internal TabControl tabMainSettings;

	internal TextBox txtTigerStopCopperKeywords;

	internal TextBox txtTigerStopPvcKeywords;

	internal TextBox txtPcfCopperKeywords;

	internal TextBox txtPcfPvcKeywords;

	internal TextBox txtPcfSteelKeywords;

	internal TextBox txtPcfCastIronKeywords;

	internal TextBox txtPipingSpecCatalogPath;

	internal Button btnBrowsePipingSpecCatalog;

	internal TextBox txtBoardroomApiBaseUrl;

	internal Button btnTestBoardroomApi;

	internal Button btnUseDefaultBoardroomApi;

	internal TextBlock txtBoardroomApiStatus;

	internal ListBox lstTigerStopColumns;

	internal ComboBox cmbTigerStopColumnAdd;

	internal Button btnTigerStopColumnAdd;

	internal Button btnTigerStopColumnRemove;

	internal Button btnTigerStopColumnUp;

	internal Button btnTigerStopColumnDown;

	internal ListBox lstPcfFields;

	internal ComboBox cmbPcfFieldAdd;

	internal Button btnPcfFieldAdd;

	internal Button btnPcfFieldRemove;

	internal Button btnPcfFieldUp;

	internal Button btnPcfFieldDown;

	internal Button btnImportSettings;

	internal Button btnExportSettings;

	private bool _documentOptionsLoaded;

	private static string _revitOptionsCacheKey;

	private static RevitOptionsSnapshot _revitOptionsCache;

	public AssemblySettingsWindow(UIApplication uiapp, SpoolingManagerKind productKind = SpoolingManagerKind.Standard)
	{
		_uiapp = uiapp;
		_productKind = productKind;
		SpoolingManagerSettings.SetActiveProject(uiapp?.ActiveUIDocument?.Document);
		if (((uiapp != null) ? uiapp.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(uiapp.Application.VersionNumber);
		}
		InitializeComponent();
		ButtonClickSoundService.Attach(this, () => _settings?.ButtonClickSoundsEnabled ?? true);
		if (tabMainSettings != null)
		{
			tabMainSettings.SelectionChanged += TabMainSettings_SelectionChanged;
		}
		ApplyProductChrome();
		SsSavantNeonChrome.ApplyNeonDialogTitle(txtSettingsHeaderTitle, useScriptFont: true);
		// Tab chip borders exist only after the template is applied.
		Dispatcher.BeginInvoke(new Action(UpdateActiveTabTitleNeon), DispatcherPriority.Loaded);
		ApplyLayoutSubDialogToolTip();
		LoadStaticOptions();
		ApplyAutoDimColumnForProduct();
		ConfigureViewScaleRow();
		// Paint the chromeless window first; Revit collectors + settings bind after first frame.
		ContentRendered += OnSettingsContentRendered;
	}

	private void OnSettingsContentRendered(object sender, EventArgs e)
	{
		ContentRendered -= OnSettingsContentRendered;
		if (_documentOptionsLoaded)
		{
			return;
		}
		_documentOptionsLoaded = true;
		LoadRevitOptions();
		LoadSettings();
	}

	private void ConfigureViewScaleRow()
	{
		ApplyViewScaleRow(_productKind != SpoolingManagerKind.AutoDimensionLab);
	}

	private void TabMainSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// SelectionChanged bubbles up from combo boxes and lists inside the tabs;
		// only play the page flip for the tab strip itself.
		if (!ReferenceEquals(e.OriginalSource, tabMainSettings))
		{
			return;
		}
		if (_settings?.ButtonClickSoundsEnabled ?? true)
		{
			ButtonClickSoundService.PlayPageFlip();
		}
		UpdateActiveTabTitleNeon();
		// Re-apply after layout so TabBorder from the control template is available.
		Dispatcher.BeginInvoke(new Action(UpdateActiveTabTitleNeon), DispatcherPriority.Loaded);
	}

	/// <summary>
	/// Neon-lights the selected tab strip label and draws a blue neon border around
	/// that tab chip; other tabs stay plain.
	/// </summary>
	private void UpdateActiveTabTitleNeon()
	{
		TextBlock[] headers =
		{
			txtTabHeaderSheetSetup,
			txtTabHeaderSchedules,
			txtTabHeaderAnnotations,
			txtTabHeaderTigerStop,
			txtTabHeaderPcfFiles,
			txtTabHeaderBoardroom
		};
		int selected = tabMainSettings?.SelectedIndex ?? 0;
		for (int i = 0; i < headers.Length; i++)
		{
			TextBlock header = headers[i];
			if (header == null)
			{
				continue;
			}

			TabItem tabItem = FindParentTabItem(header);
			Border tabChip = FindNamedDescendant<Border>(tabItem, "TabBorder");
			bool isSelected = i == selected;

			if (isSelected)
			{
				SsSavantNeonChrome.ApplyNeonDialogTitle(header, useScriptFont: false);
				header.FontWeight = FontWeights.SemiBold;
				ApplyTabChipNeon(tabChip, lit: true);
			}
			else
			{
				header.Effect = null;
				header.FontWeight = FontWeights.Normal;
				header.SetResourceReference(TextBlock.ForegroundProperty, "SsSavantForegroundPrimary");
				header.Opacity = 1.0;
				ApplyTabChipNeon(tabChip, lit: false);
			}
		}
	}

	private static void ApplyTabChipNeon(Border tabChip, bool lit)
	{
		if (tabChip == null)
		{
			return;
		}

		if (lit && SsSavantNeonChrome.IsNeonEnabled)
		{
			tabChip.BorderBrush = new SolidColorBrush(SsSavantNeonChrome.DarkBorderColor);
			tabChip.BorderThickness = new Thickness(2);
			tabChip.CornerRadius = new CornerRadius(8);
			tabChip.Effect = new DropShadowEffect
			{
				Color = SsSavantNeonChrome.DarkBorderColor,
				BlurRadius = 12,
				ShadowDepth = 0,
				Opacity = 0.75
			};
		}
		else
		{
			tabChip.Effect = null;
			if (tabChip.TemplatedParent is TabItem item && item.IsSelected)
			{
				tabChip.SetResourceReference(Border.BorderBrushProperty, "SsSavantListBorder");
				tabChip.BorderThickness = new Thickness(1, 1, 1, 0);
				tabChip.CornerRadius = new CornerRadius(8, 8, 0, 0);
			}
			else
			{
				tabChip.BorderBrush = System.Windows.Media.Brushes.Transparent;
				tabChip.BorderThickness = new Thickness(1);
				tabChip.CornerRadius = new CornerRadius(8);
			}
		}
	}

	private static TabItem FindParentTabItem(DependencyObject start)
	{
		DependencyObject current = start;
		while (current != null)
		{
			if (current is TabItem tabItem)
			{
				return tabItem;
			}
			current = VisualTreeHelper.GetParent(current);
		}
		return null;
	}

	private static T FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
	{
		if (root == null || string.IsNullOrEmpty(name))
		{
			return null;
		}

		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
			{
				return match;
			}
			T nested = FindNamedDescendant<T>(child, name);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	private void ApplyLayoutSubDialogToolTip()
	{
		if (btnLayoutSettings != null)
		{
			btnLayoutSettings.ToolTip = "Schedule and weld log position on sheet, linear dimension type, and Project Browser location";
		}
	}

	private void ApplyAutoDimColumnForProduct()
	{
		bool showAutoDim = !_productKind.IsMmcStyle();
		if (grdSpoolViews?.ColumnDefinitions != null && grdSpoolViews.ColumnDefinitions.Count > 3)
		{
			grdSpoolViews.ColumnDefinitions[3].Width = (showAutoDim ? new GridLength(88.0) : new GridLength(0.0));
		}
		Visibility autoDimVisibility = (showAutoDim ? Visibility.Visible : Visibility.Collapsed);
		if (txtViewsAutoDimHeader != null)
		{
			txtViewsAutoDimHeader.Visibility = autoDimVisibility;
		}
		if (chkBackAutoDim != null)
		{
			chkBackAutoDim.Visibility = autoDimVisibility;
		}
		if (chkFrontAutoDim != null)
		{
			chkFrontAutoDim.Visibility = autoDimVisibility;
		}
		if (chkLeftAutoDim != null)
		{
			chkLeftAutoDim.Visibility = autoDimVisibility;
		}
		if (chkRightAutoDim != null)
		{
			chkRightAutoDim.Visibility = autoDimVisibility;
		}
		if (chkTopAutoDim != null)
		{
			chkTopAutoDim.Visibility = autoDimVisibility;
		}
		if (chk3DAutoDim != null)
		{
			chk3DAutoDim.Visibility = Visibility.Collapsed;
		}
		Visibility dimSettingsVisibility = (showAutoDim ? Visibility.Visible : Visibility.Collapsed);
		if (lblAutoDimensionType != null)
		{
			lblAutoDimensionType.Visibility = dimSettingsVisibility;
		}
		if (cmbAutoDimensionType != null)
		{
			cmbAutoDimensionType.Visibility = dimSettingsVisibility;
		}
		if (lblAutoDimAnnotations != null)
		{
			lblAutoDimAnnotations.Visibility = dimSettingsVisibility;
		}
		if (chkAutoDimAnnotations != null)
		{
			chkAutoDimAnnotations.Visibility = dimSettingsVisibility;
		}
		if (lblAutoDimFittingSelf != null)
		{
			lblAutoDimFittingSelf.Visibility = dimSettingsVisibility;
		}
		if (chkAutoDimFittingSelf != null)
		{
			chkAutoDimFittingSelf.Visibility = dimSettingsVisibility;
		}
	}

	private void ApplyProductChrome()
	{
		if (_productKind.IsMmcStyle())
		{
			string text = (base.Title = (_productKind.IsMmcTesting() ? "MMC Spool Sheet Manager (Testing)" : "MMC Spool Sheet Manager"));
			txtSettingsHeaderTitle.Text = text;
			txtSettingsHeaderSubtitle.Text = "Configure title block, tag, viewport, schedule, view scale, and spool views.";
			if (btnLayoutSettings != null)
			{
				btnLayoutSettings.Visibility = Visibility.Collapsed;
			}
			if (txtLayoutSettingsPrompt != null)
			{
				txtLayoutSettingsPrompt.Visibility = Visibility.Collapsed;
			}
		}
	}

	private void ApplyViewScaleRow(bool visible)
	{
		Visibility visibility = ((!visible) ? Visibility.Collapsed : Visibility.Visible);
		if (lblViewScale != null)
		{
			lblViewScale.Visibility = visibility;
		}
		if (cmbViewScale != null)
		{
			cmbViewScale.Visibility = visibility;
		}
		if (visible && cmbViewScale != null && cmbViewScale.Items.Count <= 0)
		{
			(string, double)[] spoolSheetViewScaleOptions = SpoolSheetViewScaleOptions;
			for (int i = 0; i < spoolSheetViewScaleOptions.Length; i++)
			{
				var (content, num) = spoolSheetViewScaleOptions[i];
				cmbViewScale.Items.Add(new ComboBoxItem
				{
					Content = content,
					Tag = num
				});
			}
		}
	}

	private void LoadStaticOptions()
	{
		Add3DDirectionOptions(cmb3DDirection);
		AddElevationRotationOptions(cmbBackRotation);
		AddElevationRotationOptions(cmbFrontRotation);
		AddElevationRotationOptions(cmbLeftRotation);
		AddElevationRotationOptions(cmbRightRotation);
		AddElevationRotationOptions(cmbTopRotation);
		AddPlacementOptions(cmb3DPlacement);
		AddPlacementOptions(cmbBackPlacement);
		AddPlacementOptions(cmbFrontPlacement);
		AddPlacementOptions(cmbLeftPlacement);
		AddPlacementOptions(cmbRightPlacement);
		AddPlacementOptions(cmbTopPlacement);
		LoadWeldLogSourceViewOptions(cmbWeldLogSourceView);
		if (cmb3DDirection.Items.Count > 0)
		{
			cmb3DDirection.SelectedIndex = 0;
		}
		SelectDefaultElevationRotation(cmbBackRotation);
		SelectDefaultElevationRotation(cmbFrontRotation);
		SelectDefaultElevationRotation(cmbLeftRotation);
		SelectDefaultElevationRotation(cmbRightRotation);
		SelectDefaultElevationRotation(cmbTopRotation);
		if (cmb3DPlacement.Items.Count > 0)
		{
			cmb3DPlacement.SelectedIndex = 0;
		}
		if (cmbBackPlacement.Items.Count > 0)
		{
			cmbBackPlacement.SelectedIndex = 0;
		}
		if (cmbFrontPlacement.Items.Count > 0)
		{
			cmbFrontPlacement.SelectedIndex = 0;
		}
		if (cmbLeftPlacement.Items.Count > 0)
		{
			cmbLeftPlacement.SelectedIndex = 0;
		}
		if (cmbRightPlacement.Items.Count > 0)
		{
			cmbRightPlacement.SelectedIndex = 0;
		}
		if (cmbTopPlacement.Items.Count > 0)
		{
			cmbTopPlacement.SelectedIndex = 0;
		}
	}

	private static void Add3DDirectionOptions(ComboBox combo)
	{
		combo.Items.Clear();
		string[] orthoDirectionOptions = OrthoDirectionOptions;
		foreach (string newItem in orthoDirectionOptions)
		{
			combo.Items.Add(newItem);
		}
	}

	private static void AddElevationRotationOptions(ComboBox combo)
	{
		if (combo != null)
		{
			combo.Items.Clear();
			string[] elevationViewRotationOptions = ElevationViewRotationOptions;
			foreach (string newItem in elevationViewRotationOptions)
			{
				combo.Items.Add(newItem);
			}
		}
	}

	private static void SelectDefaultElevationRotation(ComboBox combo)
	{
		if (combo != null && combo.Items.Count != 0)
		{
			combo.SelectedIndex = 0;
		}
	}

	private void LoadRevitOptions()
	{
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			return;
		}

		Document document = _uiapp.ActiveUIDocument.Document;
		string cacheKey = BuildRevitOptionsCacheKey(document);
		if (_revitOptionsCache != null
			&& string.Equals(_revitOptionsCacheKey, cacheKey, StringComparison.Ordinal)
			&& ApplyRevitOptionsSnapshot(_revitOptionsCache))
		{
			return;
		}

		LoadTitleBlocks(document);
		LoadTagTypes(document);
		LoadAssemblyTagTypes(document);
		LoadViewportTypes(document);
		LoadSchedules(document);
		LoadTextNoteTypes(document);
		LoadViewTemplates(document);
		LoadLinearDimensionTypes(document);

		_revitOptionsCacheKey = cacheKey;
		_revitOptionsCache = CaptureRevitOptionsSnapshot();
	}

	private static string BuildRevitOptionsCacheKey(Document doc)
	{
		if (doc == null)
		{
			return string.Empty;
		}
		string path = string.Empty;
		try
		{
			path = doc.PathName ?? string.Empty;
		}
		catch
		{
		}
		return path + "|" + (doc.Title ?? string.Empty);
	}

	private sealed class RevitOptionsSnapshot
	{
		public List<string> TitleBlocks = new List<string>();
		public List<string> TagTypes = new List<string>();
		public List<string> HangerTagTypes = new List<string>();
		public List<string> DuctTagTypes = new List<string>();
		public List<string> WeldTagTypes = new List<string>();
		public List<string> AssemblyTagTypes = new List<string>();
		public List<string> ViewportTypes = new List<string>();
		public List<string> ScheduleNames = new List<string>();
		public List<string> TextNoteTypes = new List<string>();
		public List<string> ViewTemplates = new List<string>();
		public List<string> LinearDimensionTypes = new List<string>();
		public string TagTypeLabel;
	}

	private RevitOptionsSnapshot CaptureRevitOptionsSnapshot()
	{
		return new RevitOptionsSnapshot
		{
			TitleBlocks = CaptureComboItems(cmbTitleBlock),
			TagTypes = CaptureComboItems(cmbTagType),
			HangerTagTypes = CaptureComboItems(cmbHangerTagType),
			DuctTagTypes = CaptureComboItems(cmbDuctTagType),
			WeldTagTypes = CaptureComboItems(cmbWeldTagType),
			AssemblyTagTypes = CaptureComboItems(cmbAssemblyTagType),
			ViewportTypes = CaptureComboItems(cmbViewportType),
			ScheduleNames = new List<string>(_availableScheduleNames),
			TextNoteTypes = CaptureComboItems(cmbWeldLogTextType),
			ViewTemplates = CaptureComboItems(cmb3DTemplate).FindAll((string s) => !string.IsNullOrWhiteSpace(s)),
			LinearDimensionTypes = CaptureComboItems(cmbAutoDimensionType),
			TagTypeLabel = txtTagTypeLabel?.Text
		};
	}

	private static List<string> CaptureComboItems(ComboBox combo)
	{
		List<string> list = new List<string>();
		if (combo == null)
		{
			return list;
		}
		foreach (object item in combo.Items)
		{
			if (item != null)
			{
				list.Add(item.ToString());
			}
		}
		return list;
	}

	private bool ApplyRevitOptionsSnapshot(RevitOptionsSnapshot snapshot)
	{
		if (snapshot == null)
		{
			return false;
		}
		FillCombo(cmbTitleBlock, snapshot.TitleBlocks);
		FillCombo(cmbTagType, snapshot.TagTypes);
		FillCombo(cmbHangerTagType, snapshot.HangerTagTypes);
		FillCombo(cmbDuctTagType, snapshot.DuctTagTypes);
		FillCombo(cmbWeldTagType, snapshot.WeldTagTypes);
		FillCombo(cmbAssemblyTagType, snapshot.AssemblyTagTypes);
		FillCombo(cmbViewportType, snapshot.ViewportTypes);
		_availableScheduleNames.Clear();
		if (snapshot.ScheduleNames != null)
		{
			_availableScheduleNames.AddRange(snapshot.ScheduleNames);
		}
		FillCombo(cmbWeldLogTextType, snapshot.TextNoteTypes);
		FillCombo(cmbAutoDimensionType, snapshot.LinearDimensionTypes);
		ApplyViewTemplateSnapshot(snapshot.ViewTemplates);
		if (txtTagTypeLabel != null && !string.IsNullOrWhiteSpace(snapshot.TagTypeLabel))
		{
			txtTagTypeLabel.Text = snapshot.TagTypeLabel;
		}
		return true;
	}

	private static void FillCombo(ComboBox combo, List<string> items)
	{
		if (combo == null)
		{
			return;
		}
		combo.Items.Clear();
		if (items == null)
		{
			return;
		}
		foreach (string item in items)
		{
			combo.Items.Add(item);
		}
	}

	private void ApplyViewTemplateSnapshot(List<string> templates)
	{
		ComboBox[] templateCombos =
		{
			cmb3DTemplate,
			cmbBackTemplate,
			cmbFrontTemplate,
			cmbLeftTemplate,
			cmbRightTemplate,
			cmbTopTemplate
		};
		foreach (ComboBox combo in templateCombos)
		{
			if (combo == null)
			{
				continue;
			}
			combo.Items.Clear();
			combo.Items.Add(string.Empty);
			if (templates == null)
			{
				continue;
			}
			foreach (string item in templates)
			{
				if (!string.IsNullOrWhiteSpace(item))
				{
					combo.Items.Add(item);
				}
			}
		}
	}

	private void LoadLinearDimensionTypes(Document doc)
	{
		if (cmbAutoDimensionType == null)
		{
			return;
		}
		cmbAutoDimensionType.Items.Clear();
		foreach (string item in (from DimensionType dt in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(DimensionType))
			where (int)dt.StyleType == 0
			orderby ((Element)dt).Name
			select ((Element)dt).Name).Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			cmbAutoDimensionType.Items.Add(item);
		}
	}

	private void LoadTextNoteTypes(Document doc)
	{
		cmbWeldLogTextType.Items.Clear();
		foreach (string item in (from TextNoteType t in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
			orderby ((Element)t).Name
			select ((Element)t).Name into n
			where !string.IsNullOrWhiteSpace(n)
			select n).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
		{
			cmbWeldLogTextType.Items.Add(item);
		}
	}

	private static void LoadWeldLogSourceViewOptions(ComboBox combo)
	{
		if (combo == null)
		{
			return;
		}
		combo.Items.Clear();
		foreach (string label in CreateSpoolSheetsHandler.WeldLogSourceViewLabels)
		{
			combo.Items.Add(label);
		}
	}

	private static void SelectWeldLogSourceView(ComboBox combo, string label)
	{
		if (combo == null)
		{
			return;
		}
		string normalized = string.IsNullOrWhiteSpace(label) ? "3D Ortho" : label.Trim();
		int num = Array.FindIndex(CreateSpoolSheetsHandler.WeldLogSourceViewLabels, (string option) => string.Equals(option, normalized, StringComparison.OrdinalIgnoreCase));
		if (num >= 0)
		{
			combo.SelectedIndex = num;
		}
		else
		{
			combo.SelectedIndex = 0;
		}
	}

	private static string GetWeldLogSourceViewLabel(ComboBox combo)
	{
		if (combo?.SelectedItem is string text && !string.IsNullOrWhiteSpace(text))
		{
			return text;
		}
		return combo?.Text?.Trim() ?? "3D Ortho";
	}

	private void LoadTitleBlocks(Document doc)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		cmbTitleBlock.Items.Clear();
		foreach (string item in (from FamilySymbol x in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
			where ((Element)x).Category != null && ((Element)x).Category.Id.Value == -2000280L
			orderby ((ElementType)x).FamilyName, ((Element)x).Name
			select $"{((ElementType)x).FamilyName} : {((Element)x).Name}").Distinct().ToList())
		{
			cmbTitleBlock.Items.Add(item);
		}
	}

	private void LoadTagTypes(Document doc)
	{
		cmbTagType.Items.Clear();
		cmbHangerTagType.Items.Clear();
		cmbDuctTagType.Items.Clear();
		cmbWeldTagType.Items.Clear();
		HashSet<long> pipeKeys = new HashSet<long>();
		HashSet<long> hangerKeys = new HashSet<long>();
		HashSet<long> ductKeys = new HashSet<long>();
		List<FamilySymbol> pipeTags = new List<FamilySymbol>();
		List<FamilySymbol> hangerTags = new List<FamilySymbol>();
		List<FamilySymbol> ductTags = new List<FamilySymbol>();
		bool nativeMode = SpoolingManagerSettings.Load(_productKind).UseNativePipework;
		foreach (FamilySymbol item in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))).Cast<FamilySymbol>())
		{
			long key = GetStableElementIdKey(((Element)item).Id);
			if (key < 0)
			{
				continue;
			}
			if (nativeMode)
			{
				if (IsIncludedNativePipeTagType(item) && pipeKeys.Add(key))
				{
					pipeTags.Add(item);
				}
			}
			else if (IsIncludedFabricationPipeworkTagType(item) && pipeKeys.Add(key))
			{
				pipeTags.Add(item);
			}
			if (!nativeMode && IsIncludedFabricationHangerTagType(item) && hangerKeys.Add(key))
			{
				hangerTags.Add(item);
			}
			if (!nativeMode && IsIncludedFabricationDuctworkTagType(item) && ductKeys.Add(key))
			{
				ductTags.Add(item);
			}
		}
		foreach (string item2 in FormatTagTypeDisplayNames(pipeTags))
		{
			cmbTagType.Items.Add(item2);
			if (!nativeMode)
			{
				cmbWeldTagType.Items.Add(item2);
			}
		}
		foreach (string item2 in FormatTagTypeDisplayNames(hangerTags))
		{
			cmbHangerTagType.Items.Add(item2);
		}
		foreach (string item2 in FormatTagTypeDisplayNames(ductTags))
		{
			cmbDuctTagType.Items.Add(item2);
		}

		if (txtTagTypeLabel != null)
		{
			txtTagTypeLabel.Text = nativeMode
				? "Pipe / Fitting / Accessory Tag"
				: "Pipe/Fitting Tag";
		}
	}

	private static bool IsIncludedNativePipeTagType(FamilySymbol symbol)
	{
		if (symbol == null)
		{
			return false;
		}

		if (NativePipeSpoolSupport.CategoryMatchesNativePipeTags(((Element)symbol).Category))
		{
			return true;
		}

		return NativePipeSpoolSupport.CategoryMatchesNativePipeTags(GetFamilyCategoryForSymbol(symbol));
	}

	private static List<string> FormatTagTypeDisplayNames(IEnumerable<FamilySymbol> symbols)
	{
		return (from x in symbols
			orderby ((ElementType)x).FamilyName, ((Element)x).Name
			select $"{((ElementType)x).FamilyName} : {((Element)x).Name}").Distinct().ToList();
	}

	private void LoadAssemblyTagTypes(Document doc)
	{
		cmbAssemblyTagType.Items.Clear();
		HashSet<long> hashSet = new HashSet<long>();
		List<FamilySymbol> list = new List<FamilySymbol>();
		foreach (FamilySymbol item in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))).Cast<FamilySymbol>())
		{
			if (IsIncludedContinuationTagType(item))
			{
				long stableElementIdKey = GetStableElementIdKey(((Element)item).Id);
				if (stableElementIdKey >= 0 && hashSet.Add(stableElementIdKey))
				{
					list.Add(item);
				}
			}
		}
		foreach (string item2 in (from x in list
			orderby ((ElementType)x).FamilyName, ((Element)x).Name
			select $"{((ElementType)x).FamilyName} : {((Element)x).Name}").Distinct().ToList())
		{
			cmbAssemblyTagType.Items.Add(item2);
		}
	}

	private static bool IsIncludedContinuationTagType(FamilySymbol symbol)
	{
		if (symbol == null)
		{
			return false;
		}
		if (CategoryMatchesContinuationTags(((Element)symbol).Category))
		{
			return true;
		}
		return CategoryMatchesContinuationTags(GetFamilyCategoryForSymbol(symbol));
	}

	private static bool CategoryMatchesContinuationTags(Category category)
	{
		if (category == null)
		{
			return false;
		}
		if (CategoryMatchesAssemblyTags(category))
		{
			return true;
		}
		try
		{
			if (category.Id.Value == (long)BuiltInCategory.OST_MultiCategoryTags)
			{
				return true;
			}
		}
		catch
		{
		}
		string name = category.Name ?? string.Empty;
		if (name.IndexOf("Multi-Category", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		if (name.IndexOf("Multi Category", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		if (CategoryMatchesFabricationPipeworkTags(category))
		{
			return true;
		}
		return name.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool CategoryMatchesAssemblyTags(Category category)
	{
		if (category == null)
		{
			return false;
		}
		try
		{
			if (category.Id.Value == -2000268L)
			{
				return true;
			}
		}
		catch
		{
		}
		return (category.Name ?? string.Empty).IndexOf("Assembly Tags", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static long GetStableElementIdKey(ElementId id)
	{
		if (id == (ElementId)null || id == ElementId.InvalidElementId)
		{
			return -1L;
		}
		return id.Value;
	}

	private static bool IsIncludedFabricationPipeworkTagType(FamilySymbol symbol)
	{
		if (symbol == null)
		{
			return false;
		}
		if (CategoryMatchesFabricationPipeworkTags(((Element)symbol).Category))
		{
			return true;
		}
		if (CategoryMatchesFabricationPipeworkTags(GetFamilyCategoryForSymbol(symbol)))
		{
			return true;
		}
		return IsLegacyPipeTagWithFabricationInFamilyOrTypeName(symbol);
	}

	private static bool IsIncludedFabricationHangerTagType(FamilySymbol symbol)
	{
		if (symbol == null)
		{
			return false;
		}
		if (CategoryMatchesFabricationHangerTags(((Element)symbol).Category))
		{
			return true;
		}
		return CategoryMatchesFabricationHangerTags(GetFamilyCategoryForSymbol(symbol));
	}

	private static bool IsIncludedFabricationDuctworkTagType(FamilySymbol symbol)
	{
		if (symbol == null)
		{
			return false;
		}
		if (CategoryMatchesFabricationDuctworkTags(((Element)symbol).Category))
		{
			return true;
		}
		return CategoryMatchesFabricationDuctworkTags(GetFamilyCategoryForSymbol(symbol));
	}

	private static Category GetFamilyCategoryForSymbol(FamilySymbol symbol)
	{
		try
		{
			Family family = symbol.Family;
			return (family != null) ? family.FamilyCategory : null;
		}
		catch
		{
			return null;
		}
	}

	private static bool CategoryMatchesFabricationPipeworkTags(Category category)
	{
		if (category == null)
		{
			return false;
		}
		try
		{
			if (category.Id.Value == -2008209L)
			{
				return true;
			}
		}
		catch
		{
		}
		string text = category.Name ?? string.Empty;
		if (text.IndexOf("Fabrication Pipework Tags", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		string text2 = text.ToUpperInvariant();
		if (text2.Contains("FABRICATION") && text2.Contains("PIPEWORK"))
		{
			return text2.Contains("TAG");
		}
		return false;
	}

	private static bool CategoryMatchesFabricationHangerTags(Category category)
	{
		if (category == null)
		{
			return false;
		}
		try
		{
			if (category.Id.Value == -2008204L)
			{
				return true;
			}
		}
		catch
		{
		}
		string text = category.Name ?? string.Empty;
		if (text.IndexOf("Fabrication Hanger Tags", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		string text2 = text.ToUpperInvariant();
		if (text2.Contains("FABRICATION") && text2.Contains("HANGER"))
		{
			return text2.Contains("TAG");
		}
		return false;
	}

	private static bool CategoryMatchesFabricationDuctworkTags(Category category)
	{
		if (category == null)
		{
			return false;
		}
		try
		{
			if (category.Id.Value == -2008194L)
			{
				return true;
			}
		}
		catch
		{
		}
		string text = category.Name ?? string.Empty;
		if (text.IndexOf("Fabrication Ductwork Tags", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		string text2 = text.ToUpperInvariant();
		if (text2.Contains("FABRICATION") && text2.Contains("DUCT"))
		{
			return text2.Contains("TAG");
		}
		return false;
	}

	private static bool IsLegacyPipeTagWithFabricationInFamilyOrTypeName(FamilySymbol symbol)
	{
		Category category = ((Element)symbol).Category;
		if (category == null)
		{
			return false;
		}
		if ((category.Name ?? string.Empty).IndexOf("Pipe Tags", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return false;
		}
		return ((((ElementType)symbol).FamilyName ?? string.Empty) + " " + (((Element)symbol).Name ?? string.Empty)).ToLowerInvariant().IndexOf("fabrication", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private void LoadViewportTypes(Document doc)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		cmbViewportType.Items.Clear();
		foreach (string item in (from ElementType x in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ElementType))
			where !string.IsNullOrWhiteSpace(x.FamilyName) && x.FamilyName.IndexOf("Viewport", StringComparison.OrdinalIgnoreCase) >= 0
			orderby ((Element)x).Name
			select ((Element)x).Name).Distinct().ToList())
		{
			cmbViewportType.Items.Add(item);
		}
	}

	private void LoadSchedules(Document doc)
	{
		_availableScheduleNames.Clear();
		_availableScheduleNames.AddRange(ScheduleViewEnumeration.GetSelectableScheduleNames(doc));
		RebuildScheduleRowsFromSettings();
	}

	private void RebuildScheduleRowsFromSettings()
	{
		List<SpoolScheduleOption> options = _settings?.GetEffectiveScheduleOptions() ?? new List<SpoolScheduleOption>();
		if (options.Count == 0)
		{
			options.Add(new SpoolScheduleOption
			{
				Name = string.Empty,
				Placement = SpoolScheduleOption.PlacementTopLeft
			});
		}

		pnlScheduleRows?.Children.Clear();
		_scheduleRows.Clear();
		foreach (SpoolScheduleOption option in options)
		{
			AddScheduleRow(option.Name, option.Placement);
		}
	}

	private void AddScheduleRow(string selectedName = null, string placement = null)
	{
		if (pnlScheduleRows == null)
		{
			return;
		}

		Grid row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		ComboBox nameCombo = new ComboBox
		{
			Height = 28,
			IsEditable = true,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 8, 0)
		};
		foreach (string item in _availableScheduleNames)
		{
			nameCombo.Items.Add(item);
		}
		if (!string.IsNullOrWhiteSpace(selectedName))
		{
			nameCombo.Text = selectedName;
		}
		Grid.SetColumn(nameCombo, 0);
		row.Children.Add(nameCombo);

		ComboBox placementCombo = new ComboBox
		{
			Height = 28,
			IsEditable = false,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 8, 0)
		};
		placementCombo.Items.Add(SpoolScheduleOption.PlacementTopLeft);
		placementCombo.Items.Add(SpoolScheduleOption.PlacementTopRight);
		placementCombo.SelectedItem = SpoolScheduleOption.NormalizePlacement(placement);
		Grid.SetColumn(placementCombo, 1);
		row.Children.Add(placementCombo);

		_scheduleRows.Add((nameCombo, placementCombo));

		Button remove = new Button
		{
			Content = "Remove",
			Height = 28,
			Padding = new Thickness(10, 0, 10, 0),
			VerticalAlignment = VerticalAlignment.Center,
			ToolTip = "Remove this schedule"
		};
		remove.Click += (_, __) =>
		{
			if (_scheduleRows.Count <= 1)
			{
				nameCombo.Text = string.Empty;
				placementCombo.SelectedItem = SpoolScheduleOption.PlacementTopLeft;
				return;
			}

			pnlScheduleRows.Children.Remove(row);
			_scheduleRows.RemoveAll(entry => ReferenceEquals(entry.NameCombo, nameCombo));
		};
		Grid.SetColumn(remove, 2);
		row.Children.Add(remove);

		pnlScheduleRows.Children.Add(row);
	}

	private List<SpoolScheduleOption> CollectScheduleOptionsFromUi()
	{
		List<SpoolScheduleOption> options = new List<SpoolScheduleOption>();
		foreach ((ComboBox nameCombo, ComboBox placementCombo) in _scheduleRows)
		{
			string name = nameCombo?.Text?.Trim() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(name))
			{
				continue;
			}

			options.Add(new SpoolScheduleOption
			{
				Name = name,
				Placement = SpoolScheduleOption.NormalizePlacement(placementCombo?.SelectedItem as string ?? placementCombo?.Text)
			});
		}

		return options;
	}

	private List<string> CollectScheduleNamesFromUi()
	{
		return CollectScheduleOptionsFromUi().Select(option => option.Name).ToList();
	}

	private void WireExportColumnEditors()
	{
		if (btnTigerStopColumnAdd != null)
		{
			btnTigerStopColumnAdd.Click += (_, __) => AddSelectedExportColumn(lstTigerStopColumns, cmbTigerStopColumnAdd, PlotPackageExportColumns.TigerStopAll);
		}
		if (btnTigerStopColumnRemove != null)
		{
			btnTigerStopColumnRemove.Click += (_, __) => RemoveSelectedExportColumn(lstTigerStopColumns, cmbTigerStopColumnAdd, PlotPackageExportColumns.TigerStopAll);
		}
		if (btnTigerStopColumnUp != null)
		{
			btnTigerStopColumnUp.Click += (_, __) => MoveSelectedExportColumn(lstTigerStopColumns, -1);
		}
		if (btnTigerStopColumnDown != null)
		{
			btnTigerStopColumnDown.Click += (_, __) => MoveSelectedExportColumn(lstTigerStopColumns, 1);
		}
		if (btnPcfFieldAdd != null)
		{
			btnPcfFieldAdd.Click += (_, __) => AddSelectedExportColumn(lstPcfFields, cmbPcfFieldAdd, PlotPackageExportColumns.PcfAll);
		}
		if (btnPcfFieldRemove != null)
		{
			btnPcfFieldRemove.Click += (_, __) => RemoveSelectedExportColumn(lstPcfFields, cmbPcfFieldAdd, PlotPackageExportColumns.PcfAll);
		}
		if (btnPcfFieldUp != null)
		{
			btnPcfFieldUp.Click += (_, __) => MoveSelectedExportColumn(lstPcfFields, -1);
		}
		if (btnPcfFieldDown != null)
		{
			btnPcfFieldDown.Click += (_, __) => MoveSelectedExportColumn(lstPcfFields, 1);
		}
	}

	private static void LoadExportColumnList(ListBox listBox, ComboBox addCombo, List<string> selected, string[] allColumns)
	{
		if (listBox == null)
		{
			return;
		}

		listBox.Items.Clear();
		foreach (string column in selected ?? new List<string>())
		{
			listBox.Items.Add(column);
		}

		RefreshExportColumnAddCombo(listBox, addCombo, allColumns);
	}

	private static void RefreshExportColumnAddCombo(ListBox listBox, ComboBox addCombo, string[] allColumns)
	{
		if (addCombo == null || allColumns == null)
		{
			return;
		}

		HashSet<string> selected = new HashSet<string>(
			listBox?.Items.Cast<object>().Select(item => item?.ToString() ?? string.Empty) ?? Enumerable.Empty<string>(),
			StringComparer.OrdinalIgnoreCase);
		addCombo.Items.Clear();
		foreach (string column in allColumns)
		{
			if (!selected.Contains(column))
			{
				addCombo.Items.Add(column);
			}
		}

		addCombo.SelectedIndex = addCombo.Items.Count > 0 ? 0 : -1;
	}

	private static void AddSelectedExportColumn(ListBox listBox, ComboBox addCombo, string[] allColumns)
	{
		if (listBox == null || addCombo?.SelectedItem == null)
		{
			return;
		}

		string column = addCombo.SelectedItem.ToString();
		if (string.IsNullOrWhiteSpace(column))
		{
			return;
		}

		foreach (object item in listBox.Items)
		{
			if (string.Equals(item?.ToString(), column, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}

		listBox.Items.Add(column);
		listBox.SelectedItem = column;
		RefreshExportColumnAddCombo(listBox, addCombo, allColumns);
	}

	private static void RemoveSelectedExportColumn(ListBox listBox, ComboBox addCombo, string[] allColumns)
	{
		if (listBox?.SelectedItem == null)
		{
			return;
		}

		int index = listBox.SelectedIndex;
		listBox.Items.RemoveAt(index);
		if (listBox.Items.Count > 0)
		{
			listBox.SelectedIndex = Math.Min(index, listBox.Items.Count - 1);
		}

		RefreshExportColumnAddCombo(listBox, addCombo, allColumns);
	}

	private static void MoveSelectedExportColumn(ListBox listBox, int delta)
	{
		if (listBox?.SelectedItem == null || listBox.Items.Count < 2)
		{
			return;
		}

		int index = listBox.SelectedIndex;
		int target = index + delta;
		if (target < 0 || target >= listBox.Items.Count)
		{
			return;
		}

		object item = listBox.SelectedItem;
		listBox.Items.RemoveAt(index);
		listBox.Items.Insert(target, item);
		listBox.SelectedIndex = target;
	}

	private static string CollectListBoxColumns(ListBox listBox, string fallbackCsv)
	{
		if (listBox == null || listBox.Items.Count == 0)
		{
			return fallbackCsv;
		}

		return string.Join(",", listBox.Items.Cast<object>().Select(item => item?.ToString()).Where(name => !string.IsNullOrWhiteSpace(name)));
	}

	private void LoadViewTemplates(Document doc)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		List<string> templateNames = (from View x in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))
			where x.IsTemplate
			where ((Element)x).Name.IndexOf("Spool", StringComparison.OrdinalIgnoreCase) >= 0
			orderby ((Element)x).Name
			select ((Element)x).Name).Distinct().ToList();
		LoadTemplateCombo(cmb3DTemplate, templateNames);
		LoadTemplateCombo(cmbBackTemplate, templateNames);
		LoadTemplateCombo(cmbFrontTemplate, templateNames);
		LoadTemplateCombo(cmbLeftTemplate, templateNames);
		LoadTemplateCombo(cmbRightTemplate, templateNames);
		LoadTemplateCombo(cmbTopTemplate, templateNames);
	}

	private static void LoadTemplateCombo(ComboBox combo, IEnumerable<string> templateNames)
	{
		combo.Items.Clear();
		foreach (string templateName in templateNames)
		{
			combo.Items.Add(templateName);
		}
	}

	private static void AddPlacementOptions(ComboBox combo)
	{
		combo.Items.Clear();
		combo.Items.Add("Top Left");
		combo.Items.Add("Top Center");
		combo.Items.Add("Top Right");
		combo.Items.Add("Middle Left");
		combo.Items.Add("Middle Center");
		combo.Items.Add("Middle Right");
		combo.Items.Add("Bottom Left");
		combo.Items.Add("Bottom Center");
		combo.Items.Add("Bottom Right");
	}

	private void LoadSettings()
	{
		_settings = SpoolingManagerSettings.Load(_productKind);
		cmbTitleBlock.Text = _settings.TitleBlockName ?? string.Empty;
		cmbTagType.Text = _settings.TagTypeName ?? string.Empty;
		cmbHangerTagType.Text = _settings.HangerTagTypeName ?? string.Empty;
		cmbDuctTagType.Text = _settings.DuctTagTypeName ?? string.Empty;
		cmbWeldTagType.Text = _settings.WeldTagTypeName ?? string.Empty;
		cmbAssemblyTagType.Text = _settings.AssemblyTagTypeName ?? string.Empty;
		cmbWeldLogTextType.Text = _settings.WeldLogTextNoteTypeName ?? string.Empty;
		SelectWeldLogSourceView(cmbWeldLogSourceView, _settings.WeldLogSourceViewLabel);
		chkNumberWelds.IsChecked = _settings.NumberWeldsEnabled;
		chkFillWeldLog.IsChecked = _settings.WeldLogEnabled;
		chkContinuationTags.IsChecked = _settings.ContinuationTagsEnabled;
		chkWeldTagPackagePrefix.IsChecked = _settings.WeldTagIncludePackageNumber;
		if (chkItemNumberCustomFormat != null)
		{
			chkItemNumberCustomFormat.IsChecked = _settings.ItemNumberCustomFormatEnabled;
		}
		if (txtItemNumberStraightPrefix != null)
		{
			txtItemNumberStraightPrefix.Text = _settings.ItemNumberStraightPrefix ?? "P-";
		}
		if (txtItemNumberStraightSuffix != null)
		{
			txtItemNumberStraightSuffix.Text = _settings.ItemNumberStraightSuffix ?? "-S";
		}
		if (txtItemNumberFittingPrefix != null)
		{
			txtItemNumberFittingPrefix.Text = _settings.ItemNumberFittingPrefix ?? "P-";
		}
		if (txtItemNumberFittingSuffix != null)
		{
			txtItemNumberFittingSuffix.Text = _settings.ItemNumberFittingSuffix ?? "-F";
		}
		if (txtItemNumberValvePrefix != null)
		{
			txtItemNumberValvePrefix.Text = _settings.ItemNumberValvePrefix ?? "P-";
		}
		if (txtItemNumberValveSuffix != null)
		{
			txtItemNumberValveSuffix.Text = _settings.ItemNumberValveSuffix ?? "-V";
		}
		_settings.NormalizeItemNumberStarts();
		if (txtItemNumberStraightStart != null)
		{
			txtItemNumberStraightStart.Text = _settings.ItemNumberStraightStart ?? "001";
		}
		if (txtItemNumberFittingStart != null)
		{
			txtItemNumberFittingStart.Text = _settings.ItemNumberFittingStart ?? "001";
		}
		if (txtItemNumberValveStart != null)
		{
			txtItemNumberValveStart.Text = _settings.ItemNumberValveStart ?? "001";
		}
		if (chkPlaceTrackingQr != null)
		{
			chkPlaceTrackingQr.IsChecked = _settings.PlaceTrackingQrOnSpoolSheets;
		}
		if (txtQrTrackingUrlBase != null)
		{
			txtQrTrackingUrlBase.Text = _settings.QrTrackingUrlBase ?? string.Empty;
		}
		cmbViewportType.Text = _settings.ViewportTypeName ?? string.Empty;
		if (txtTigerStopCopperKeywords != null)
		{
			txtTigerStopCopperKeywords.Text = string.IsNullOrWhiteSpace(_settings.TigerStopCopperKeywords)
				? FabricationMaterialKind.DefaultCopperKeywords
				: _settings.TigerStopCopperKeywords;
		}
		if (txtTigerStopPvcKeywords != null)
		{
			txtTigerStopPvcKeywords.Text = string.IsNullOrWhiteSpace(_settings.TigerStopPvcKeywords)
				? FabricationMaterialKind.DefaultPvcKeywords
				: _settings.TigerStopPvcKeywords;
		}
		if (txtPcfCopperKeywords != null)
		{
			txtPcfCopperKeywords.Text = string.IsNullOrWhiteSpace(_settings.PcfCopperKeywords)
				? FabricationMaterialKind.DefaultCopperKeywords
				: _settings.PcfCopperKeywords;
		}
		if (txtPcfPvcKeywords != null)
		{
			txtPcfPvcKeywords.Text = string.IsNullOrWhiteSpace(_settings.PcfPvcKeywords)
				? FabricationMaterialKind.DefaultPvcKeywords
				: _settings.PcfPvcKeywords;
		}
		if (txtPcfSteelKeywords != null)
		{
			txtPcfSteelKeywords.Text = string.IsNullOrWhiteSpace(_settings.PcfSteelKeywords)
				? FabricationMaterialKind.DefaultSteelKeywords
				: _settings.PcfSteelKeywords;
		}
		if (txtPcfCastIronKeywords != null)
		{
			txtPcfCastIronKeywords.Text = string.IsNullOrWhiteSpace(_settings.PcfCastIronKeywords)
				? FabricationMaterialKind.DefaultCastIronKeywords
				: _settings.PcfCastIronKeywords;
		}
		if (txtPipingSpecCatalogPath != null)
		{
			txtPipingSpecCatalogPath.Text = string.IsNullOrWhiteSpace(_settings.PipingSpecCatalogPath)
				? PipingSpecCatalogService.DefaultCatalogPath
				: _settings.PipingSpecCatalogPath;
		}
		if (txtBoardroomApiBaseUrl != null)
		{
			txtBoardroomApiBaseUrl.Text = BoardroomApiClient.NormalizeBaseUrl(_settings.BoardroomApiBaseUrl);
			if (txtBoardroomApiStatus != null)
			{
				txtBoardroomApiStatus.Text = "Click Test to check the Boardroom API connection.";
			}
		}
		LoadExportColumnList(lstTigerStopColumns, cmbTigerStopColumnAdd, PlotPackageExportColumns.ParseTigerStopColumns(_settings.TigerStopColumns), PlotPackageExportColumns.TigerStopAll);
		LoadExportColumnList(lstPcfFields, cmbPcfFieldAdd, PlotPackageExportColumns.ParsePcfFields(_settings.PcfFields), PlotPackageExportColumns.PcfAll);
		RebuildScheduleRowsFromSettings();
		chk3DOrtho.IsChecked = _settings.Include3DOrtho;
		SetComboText(cmb3DDirection, _settings.Direction3D);
		chk3DTag.IsChecked = _settings.Tag3D;
		chk3DAutoDim.IsChecked = _settings.AutoDim3D;
		SetComboText(cmb3DPlacement, _settings.Placement3D);
		SetComboText(cmb3DTemplate, _settings.Template3D);
		chkBackView.IsChecked = _settings.IncludeBackView;
		chkBackTag.IsChecked = _settings.TagBackView;
		chkBackAutoDim.IsChecked = _settings.AutoDimBackView;
		SetElevationRotation(cmbBackRotation, _settings.BackViewRotation);
		SetComboText(cmbBackPlacement, _settings.PlacementBackView);
		SetComboText(cmbBackTemplate, _settings.TemplateBackView);
		chkFrontView.IsChecked = _settings.IncludeFrontView;
		chkFrontTag.IsChecked = _settings.TagFrontView;
		chkFrontAutoDim.IsChecked = _settings.AutoDimFrontView;
		SetElevationRotation(cmbFrontRotation, _settings.FrontViewRotation);
		SetComboText(cmbFrontPlacement, _settings.PlacementFrontView);
		SetComboText(cmbFrontTemplate, _settings.TemplateFrontView);
		chkLeftView.IsChecked = _settings.IncludeLeftView;
		chkLeftTag.IsChecked = _settings.TagLeftView;
		chkLeftAutoDim.IsChecked = _settings.AutoDimLeftView;
		SetElevationRotation(cmbLeftRotation, _settings.LeftViewRotation);
		SetComboText(cmbLeftPlacement, _settings.PlacementLeftView);
		SetComboText(cmbLeftTemplate, _settings.TemplateLeftView);
		chkRightView.IsChecked = _settings.IncludeRightView;
		chkRightTag.IsChecked = _settings.TagRightView;
		chkRightAutoDim.IsChecked = _settings.AutoDimRightView;
		SetElevationRotation(cmbRightRotation, _settings.RightViewRotation);
		SetComboText(cmbRightPlacement, _settings.PlacementRightView);
		SetComboText(cmbRightTemplate, _settings.TemplateRightView);
		chkTopView.IsChecked = _settings.IncludeTopView;
		chkTopTag.IsChecked = _settings.TagTopView;
		chkTopAutoDim.IsChecked = _settings.AutoDimTopView;
		SetElevationRotation(cmbTopRotation, _settings.TopViewRotation);
		SetComboText(cmbTopPlacement, _settings.PlacementTopView);
		SetComboText(cmbTopTemplate, _settings.TemplateTopView);
		if (_productKind != SpoolingManagerKind.AutoDimensionLab)
		{
			ConfigureViewScaleRow();
			SetViewScaleSelection(_settings.GetSpoolSheetScaleInchesPerFoot(_productKind));
		}
		if (cmbAutoDimensionType != null)
		{
			cmbAutoDimensionType.Text = _settings.AutoDimensionTypeName ?? string.Empty;
		}
		if (chkAutoDimAnnotations != null)
		{
			chkAutoDimAnnotations.IsChecked = _settings.AutoDimAnnotations;
		}
		if (chkAutoDimFittingSelf != null)
		{
			chkAutoDimFittingSelf.IsChecked = _settings.NativeFittingSelfDimensionsEnabled;
		}
		ResetValidationVisuals();
	}

	private void SetViewScaleSelection(double inchesPerFoot)
	{
		if (cmbViewScale == null)
		{
			return;
		}
		ApplyViewScaleRow(visible: true);
		ComboBoxItem comboBoxItem = cmbViewScale.Items.OfType<ComboBoxItem>().FirstOrDefault((ComboBoxItem item) => item.Tag is double num && Math.Abs(num - inchesPerFoot) < 0.001);
		if (comboBoxItem != null)
		{
			cmbViewScale.SelectedItem = comboBoxItem;
			return;
		}
		double defaultInchesPerFoot = (_productKind.IsMmcStyle() ? 1.5 : 0.5);
		ComboBoxItem comboBoxItem2 = cmbViewScale.Items.OfType<ComboBoxItem>().FirstOrDefault((ComboBoxItem item) => item.Tag is double num && Math.Abs(num - defaultInchesPerFoot) < 0.001);
		if (comboBoxItem2 != null)
		{
			cmbViewScale.SelectedItem = comboBoxItem2;
		}
	}

	private static void SetComboText(ComboBox combo, string value)
	{
		if (combo != null && !string.IsNullOrWhiteSpace(value))
		{
			combo.Text = value;
		}
	}

	private static void SetElevationRotation(ComboBox combo, string value)
	{
		if (combo != null)
		{
			string normalized = (string.IsNullOrWhiteSpace(value) ? "0°" : value.Trim());
			int num = Array.FindIndex(ElevationViewRotationOptions, (string option) => string.Equals(option, normalized, StringComparison.OrdinalIgnoreCase));
			if (num >= 0)
			{
				combo.SelectedIndex = num;
			}
			else
			{
				combo.SelectedIndex = 0;
			}
		}
	}

	private static string GetElevationRotation(ComboBox combo)
	{
		if (!(combo?.SelectedItem is string text) || string.IsNullOrWhiteSpace(text))
		{
			return combo?.Text?.Trim() ?? "0°";
		}
		return text;
	}

	private void BtnAddSchedule_Click(object sender, RoutedEventArgs e)
	{
		AddScheduleRow();
	}

	private void BtnBrowsePipingSpecCatalog_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string current = txtPipingSpecCatalogPath?.Text?.Trim();
			if (string.IsNullOrWhiteSpace(current))
			{
				current = PipingSpecCatalogService.DefaultCatalogPath;
			}

			string initialDir = Path.GetDirectoryName(current);
			if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
			{
				initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			}

			var dialog = new Microsoft.Win32.SaveFileDialog
			{
				Title = "Piping Specification Catalog",
				Filter = "Excel Workbook (*.xlsx)|*.xlsx",
				DefaultExt = ".xlsx",
				AddExtension = true,
				OverwritePrompt = false,
				FileName = Path.GetFileName(current),
				InitialDirectory = initialDir
			};

			if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
			{
				return;
			}

			string chosen = Path.GetFullPath(dialog.FileName);
			if (!string.Equals(Path.GetExtension(chosen), ".xlsx", StringComparison.OrdinalIgnoreCase))
			{
				chosen += ".xlsx";
			}

			Directory.CreateDirectory(Path.GetDirectoryName(chosen) ?? ".");
			if (!File.Exists(chosen))
			{
				PipingSpecCatalogService.EnsureWorkbook(chosen);
			}

			if (txtPipingSpecCatalogPath != null)
			{
				txtPipingSpecCatalogPath.Text = chosen;
			}
		}
		catch (Exception ex)
		{
			SsSavantMessageBox.Show(this, "Could not set the catalog file.\n\n" + ex.Message, "Spooling Savant", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void BtnTestBoardroomApi_Click(object sender, RoutedEventArgs e)
	{
		RefreshBoardroomApiStatus(showMessageBox: true);
	}

	private void BtnUseDefaultBoardroomApi_Click(object sender, RoutedEventArgs e)
	{
		if (txtBoardroomApiBaseUrl != null)
		{
			txtBoardroomApiBaseUrl.Text = BoardroomApiClient.DefaultBaseUrl;
		}

		RefreshBoardroomApiStatus();
	}

	private void RefreshBoardroomApiStatus(bool showMessageBox = false)
	{
		string baseUrl = BoardroomApiClient.NormalizeBaseUrl(txtBoardroomApiBaseUrl?.Text);
		bool ok;
		string message;
		try
		{
			using var client = new BoardroomApiClient(baseUrl);
			ok = client.TryHealth(out message);
		}
		catch (Exception ex)
		{
			ok = false;
			message = ex.Message;
		}

		if (txtBoardroomApiStatus != null)
		{
			txtBoardroomApiStatus.Text = message;
		}

		if (showMessageBox)
		{
			SsSavantMessageBox.Show(
				this,
				message,
				"Spooling Savant",
				MessageBoxButton.OK,
				ok ? MessageBoxImage.Information : MessageBoxImage.Exclamation);
		}
	}

	private void BtnLayoutSettings_Click(object sender, RoutedEventArgs e)
	{
		SetLayoutSettingsButtonLit(true);
		try
		{
			SpoolSheetLayoutSettingsWindow spoolSheetLayoutSettingsWindow = new SpoolSheetLayoutSettingsWindow(_uiapp, _settings, _productKind);
			SsSavantDialogForeground.Attach(spoolSheetLayoutSettingsWindow, _uiapp);
			spoolSheetLayoutSettingsWindow.Owner = this;
			spoolSheetLayoutSettingsWindow.ShowDialog();
		}
		finally
		{
			SetLayoutSettingsButtonLit(false);
		}
	}

	private void SetLayoutSettingsButtonLit(bool lit)
	{
		if (btnLayoutSettings == null)
		{
			return;
		}

		if (lit && SsSavantNeonChrome.IsNeonEnabled)
		{
			btnLayoutSettings.BorderBrush = new SolidColorBrush(SsSavantNeonChrome.DarkBorderColor);
			btnLayoutSettings.BorderThickness = new Thickness(2);
			btnLayoutSettings.Effect = new DropShadowEffect
			{
				Color = SsSavantNeonChrome.DarkBorderColor,
				BlurRadius = 14,
				ShadowDepth = 0,
				Opacity = 0.85
			};
			if (btnLayoutSettings.Content is TextBlock icon)
			{
				icon.Foreground = new SolidColorBrush(SsSavantNeonChrome.NeonModeColor);
				icon.Effect = new DropShadowEffect
				{
					Color = SsSavantNeonChrome.NeonModeGlow,
					BlurRadius = 12,
					ShadowDepth = 0,
					Opacity = 0.9
				};
			}
		}
		else
		{
			btnLayoutSettings.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
			btnLayoutSettings.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
			btnLayoutSettings.Effect = null;
			if (btnLayoutSettings.Content is TextBlock icon)
			{
				icon.Effect = null;
				icon.SetResourceReference(TextBlock.ForegroundProperty, "SsSavantForegroundPrimary");
			}
		}
	}

	private void BtnSave_Click(object sender, RoutedEventArgs e)
	{
		ApplySettingsFromUi();
		if (!ValidateRequiredSelections())
		{
			return;
		}
		_settings.Save(_productKind);
		base.DialogResult = true;
		Close();
	}

	private void BtnExportSettings_Click(object sender, RoutedEventArgs e)
	{
		ApplySettingsFromUi();
		const string transferFolder = @"C:\Temp\Spooling Savant Settings";
		Directory.CreateDirectory(transferFolder);
		SaveFileDialog dialog = new SaveFileDialog
		{
			Title = "Export Spooling Savant Settings",
			Filter = SpoolingManagerSettingsTransferService.ExportFilter,
			FileName = SpoolingManagerSettingsTransferService.BuildDefaultExportFileName(_productKind),
			InitialDirectory = transferFolder,
			DefaultExt = ".xml",
			AddExtension = true,
			OverwritePrompt = true
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}
		if (!SpoolingManagerSettingsTransferService.TryExport(
			_settings, _productKind, dialog.FileName, out string errorMessage))
		{
			SsSavantMessageBox.Show(
				this,
				"Settings could not be exported.\n\n" + errorMessage,
				"Spooling Savant",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			return;
		}
		SsSavantMessageBox.Show(
			this,
			"Settings exported successfully.",
			"Spooling Savant",
			MessageBoxButton.OK,
			MessageBoxImage.Information);
	}

	private void BtnImportSettings_Click(object sender, RoutedEventArgs e)
	{
		const string transferFolder = @"C:\Temp\Spooling Savant Settings";
		Directory.CreateDirectory(transferFolder);
		OpenFileDialog dialog = new OpenFileDialog
		{
			Title = "Import Spooling Savant Settings",
			Filter = SpoolingManagerSettingsTransferService.ExportFilter,
			InitialDirectory = transferFolder,
			FileName = "Settings.xml",
			DefaultExt = ".xml",
			CheckFileExists = true,
			Multiselect = false
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}
		if (!SpoolingManagerSettingsTransferService.TryImport(
			dialog.FileName, _productKind, out SpoolingManagerSettings imported, out string errorMessage))
		{
			SsSavantMessageBox.Show(
				this,
				"Settings could not be imported.\n\n" + errorMessage,
				"Spooling Savant",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			return;
		}
		_settings = imported;
		_settings.Save(_productKind);
		LoadSettings();
		SsSavantMessageBox.Show(
			this,
			"Settings imported and saved for this project.",
			"Spooling Savant",
			MessageBoxButton.OK,
			MessageBoxImage.Information);
	}

	private void ApplySettingsFromUi()
	{
		_settings.TitleBlockName = cmbTitleBlock.Text ?? string.Empty;
		_settings.TagTypeName = cmbTagType.Text ?? string.Empty;
		_settings.HangerTagTypeName = cmbHangerTagType.Text ?? string.Empty;
		_settings.DuctTagTypeName = cmbDuctTagType.Text ?? string.Empty;
		_settings.WeldTagTypeName = cmbWeldTagType.Text ?? string.Empty;
		_settings.AssemblyTagTypeName = cmbAssemblyTagType.Text ?? string.Empty;
		_settings.WeldLogTextNoteTypeName = cmbWeldLogTextType.Text ?? string.Empty;
		_settings.WeldLogSourceViewLabel = GetWeldLogSourceViewLabel(cmbWeldLogSourceView);
		_settings.NumberWeldsEnabled = chkNumberWelds.IsChecked == true;
		_settings.WeldLogEnabled = chkFillWeldLog.IsChecked == true;
		_settings.ContinuationTagsEnabled = chkContinuationTags.IsChecked == true;
		_settings.WeldTagIncludePackageNumber = chkWeldTagPackagePrefix.IsChecked == true;
		_settings.ItemNumberCustomFormatEnabled = chkItemNumberCustomFormat?.IsChecked == true;
		_settings.ItemNumberStraightPrefix = (txtItemNumberStraightPrefix?.Text ?? "P-").Trim();
		_settings.ItemNumberStraightSuffix = (txtItemNumberStraightSuffix?.Text ?? "-S").Trim();
		_settings.ItemNumberFittingPrefix = (txtItemNumberFittingPrefix?.Text ?? "P-").Trim();
		_settings.ItemNumberFittingSuffix = (txtItemNumberFittingSuffix?.Text ?? "-F").Trim();
		_settings.ItemNumberValvePrefix = (txtItemNumberValvePrefix?.Text ?? "P-").Trim();
		_settings.ItemNumberValveSuffix = (txtItemNumberValveSuffix?.Text ?? "-V").Trim();
		_settings.ItemNumberStraightStart = ItemNumberSequenceFormatter.NormalizeStartToken(
			txtItemNumberStraightStart?.Text,
			"001");
		_settings.ItemNumberFittingStart = ItemNumberSequenceFormatter.NormalizeStartToken(
			txtItemNumberFittingStart?.Text,
			"001");
		_settings.ItemNumberValveStart = ItemNumberSequenceFormatter.NormalizeStartToken(
			txtItemNumberValveStart?.Text,
			"001");
		_settings.NormalizeItemNumberStarts();
		_settings.PlaceTrackingQrOnSpoolSheets = chkPlaceTrackingQr?.IsChecked == true;
		_settings.QrTrackingUrlBase = (txtQrTrackingUrlBase?.Text ?? string.Empty).Trim();
		_settings.ViewportTypeName = cmbViewportType.Text ?? string.Empty;
		_settings.TigerStopCopperKeywords = string.IsNullOrWhiteSpace(txtTigerStopCopperKeywords?.Text)
			? FabricationMaterialKind.DefaultCopperKeywords
			: txtTigerStopCopperKeywords.Text.Trim();
		_settings.TigerStopPvcKeywords = string.IsNullOrWhiteSpace(txtTigerStopPvcKeywords?.Text)
			? FabricationMaterialKind.DefaultPvcKeywords
			: txtTigerStopPvcKeywords.Text.Trim();
		_settings.PcfCopperKeywords = string.IsNullOrWhiteSpace(txtPcfCopperKeywords?.Text)
			? FabricationMaterialKind.DefaultCopperKeywords
			: txtPcfCopperKeywords.Text.Trim();
		_settings.PcfPvcKeywords = string.IsNullOrWhiteSpace(txtPcfPvcKeywords?.Text)
			? FabricationMaterialKind.DefaultPvcKeywords
			: txtPcfPvcKeywords.Text.Trim();
		_settings.PcfSteelKeywords = string.IsNullOrWhiteSpace(txtPcfSteelKeywords?.Text)
			? FabricationMaterialKind.DefaultSteelKeywords
			: txtPcfSteelKeywords.Text.Trim();
		_settings.PcfCastIronKeywords = FabricationMaterialKind.SanitizeCastIronKeywordsSetting(
			string.IsNullOrWhiteSpace(txtPcfCastIronKeywords?.Text)
				? FabricationMaterialKind.DefaultCastIronKeywords
				: txtPcfCastIronKeywords.Text.Trim());
		string catalogPath = txtPipingSpecCatalogPath?.Text?.Trim() ?? string.Empty;
		_settings.PipingSpecCatalogPath = string.IsNullOrWhiteSpace(catalogPath)
			? PipingSpecCatalogService.DefaultCatalogPath
			: Path.GetFullPath(catalogPath);
		string boardroomApiUrl = txtBoardroomApiBaseUrl?.Text?.Trim() ?? string.Empty;
		_settings.BoardroomApiBaseUrl = BoardroomApiClient.NormalizeBaseUrl(boardroomApiUrl);
		_settings.TigerStopColumns = CollectListBoxColumns(lstTigerStopColumns, PlotPackageExportColumns.DefaultTigerStopColumnsCsv);
		_settings.PcfFields = CollectListBoxColumns(lstPcfFields, PlotPackageExportColumns.DefaultPcfFieldsCsv);
		_settings.ScheduleOptions = CollectScheduleOptionsFromUi();
		_settings.NormalizeScheduleOptions();
		_settings.Include3DOrtho = chk3DOrtho.IsChecked == true;
		_settings.Direction3D = cmb3DDirection.Text ?? string.Empty;
		_settings.Tag3D = chk3DTag.IsChecked == true;
		_settings.AutoDim3D = chk3DAutoDim.IsChecked == true;
		_settings.Placement3D = cmb3DPlacement.Text ?? string.Empty;
		_settings.Template3D = cmb3DTemplate.Text ?? string.Empty;
		_settings.IncludeBackView = chkBackView.IsChecked == true;
		_settings.TagBackView = chkBackTag.IsChecked == true;
		_settings.AutoDimBackView = chkBackAutoDim.IsChecked == true;
		_settings.BackViewRotation = GetElevationRotation(cmbBackRotation);
		_settings.PlacementBackView = cmbBackPlacement.Text ?? string.Empty;
		_settings.TemplateBackView = cmbBackTemplate.Text ?? string.Empty;
		_settings.IncludeFrontView = chkFrontView.IsChecked == true;
		_settings.TagFrontView = chkFrontTag.IsChecked == true;
		_settings.AutoDimFrontView = chkFrontAutoDim.IsChecked == true;
		_settings.FrontViewRotation = GetElevationRotation(cmbFrontRotation);
		_settings.PlacementFrontView = cmbFrontPlacement.Text ?? string.Empty;
		_settings.TemplateFrontView = cmbFrontTemplate.Text ?? string.Empty;
		_settings.IncludeLeftView = chkLeftView.IsChecked == true;
		_settings.TagLeftView = chkLeftTag.IsChecked == true;
		_settings.AutoDimLeftView = chkLeftAutoDim.IsChecked == true;
		_settings.LeftViewRotation = GetElevationRotation(cmbLeftRotation);
		_settings.PlacementLeftView = cmbLeftPlacement.Text ?? string.Empty;
		_settings.TemplateLeftView = cmbLeftTemplate.Text ?? string.Empty;
		_settings.IncludeRightView = chkRightView.IsChecked == true;
		_settings.TagRightView = chkRightTag.IsChecked == true;
		_settings.AutoDimRightView = chkRightAutoDim.IsChecked == true;
		_settings.RightViewRotation = GetElevationRotation(cmbRightRotation);
		_settings.PlacementRightView = cmbRightPlacement.Text ?? string.Empty;
		_settings.TemplateRightView = cmbRightTemplate.Text ?? string.Empty;
		_settings.IncludeTopView = chkTopView.IsChecked == true;
		_settings.TagTopView = chkTopTag.IsChecked == true;
		_settings.AutoDimTopView = chkTopAutoDim.IsChecked == true;
		_settings.TopViewRotation = GetElevationRotation(cmbTopRotation);
		_settings.PlacementTopView = cmbTopPlacement.Text ?? string.Empty;
		_settings.TemplateTopView = cmbTopTemplate.Text ?? string.Empty;
		if (_productKind != SpoolingManagerKind.AutoDimensionLab && cmbViewScale?.SelectedItem is ComboBoxItem { Tag: var tag } && tag is double value)
		{
			_settings.SpoolSheetScaleInchesPerFoot = value;
		}
		if (cmbAutoDimensionType != null)
		{
			_settings.AutoDimensionTypeName = cmbAutoDimensionType.Text?.Trim() ?? string.Empty;
		}
		if (chkAutoDimAnnotations != null)
		{
			_settings.AutoDimAnnotations = chkAutoDimAnnotations.IsChecked == true;
		}
		if (chkAutoDimFittingSelf != null)
		{
			_settings.NativeFittingSelfDimensionsEnabled = chkAutoDimFittingSelf.IsChecked == true;
		}
	}

	private bool ValidateRequiredSelections()
	{
		ResetValidationVisuals();
		bool flag = true;
		flag &= ValidateComboRequired(cmbTitleBlock);
		flag &= ValidateComboRequired(cmbTagType);
		if (chkNumberWelds.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmbWeldTagType);
		}
		if (chkFillWeldLog.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmbWeldLogTextType);
		}
		if (chkContinuationTags.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmbAssemblyTagType);
		}
		flag &= ValidateComboRequired(cmbViewportType);
		flag &= ValidateScheduleRows();
		if (_productKind != SpoolingManagerKind.AutoDimensionLab)
		{
			flag &= ValidateComboRequired(cmbViewScale);
		}
		if (chk3DOrtho.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmb3DDirection);
			flag &= ValidateComboRequired(cmb3DPlacement);
			flag &= ValidateComboRequired(cmb3DTemplate);
		}
		if (chkBackView.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmbBackPlacement);
			flag &= ValidateComboRequired(cmbBackTemplate);
		}
		if (chkFrontView.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmbFrontPlacement);
			flag &= ValidateComboRequired(cmbFrontTemplate);
		}
		if (chkLeftView.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmbLeftPlacement);
			flag &= ValidateComboRequired(cmbLeftTemplate);
		}
		if (chkRightView.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmbRightPlacement);
			flag &= ValidateComboRequired(cmbRightTemplate);
		}
		if (chkTopView.IsChecked == true)
		{
			flag &= ValidateComboRequired(cmbTopPlacement);
			flag &= ValidateComboRequired(cmbTopTemplate);
		}
		if (!flag)
		{
			if (tabMainSettings != null)
			{
				tabMainSettings.SelectedIndex = 0;
			}
			SsSavantMessageBox.Show(this, "Please double check that all necessary options are selected.", "Spooling Savant", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		return flag;
	}

	private bool ValidateScheduleRows()
	{
		if (CollectScheduleOptionsFromUi().Count > 0)
		{
			foreach ((ComboBox nameCombo, ComboBox _) in _scheduleRows)
			{
				ResetCombo(nameCombo);
			}
			return true;
		}

		if (_scheduleRows.Count > 0)
		{
			return ValidateComboRequired(_scheduleRows[0].NameCombo);
		}

		return false;
	}

	private bool ValidateComboRequired(ComboBox combo)
	{
		bool flag = !string.IsNullOrWhiteSpace(combo.Text);
		if (flag)
		{
			ResetCombo(combo);
		}
		else
		{
			combo.BorderBrush = ErrorBorderBrush;
			combo.BorderThickness = new Thickness(2.0);
			combo.Background = ErrorBackgroundBrush;
			combo.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E"));
		}
		return flag;
	}

	private void ResetValidationVisuals()
	{
		ResetCombo(cmbTitleBlock);
		ResetCombo(cmbTagType);
		ResetCombo(cmbHangerTagType);
		ResetCombo(cmbDuctTagType);
		ResetCombo(cmbWeldTagType);
		ResetCombo(cmbAssemblyTagType);
		ResetCombo(cmbWeldLogTextType);
		ResetCombo(cmbViewportType);
		foreach ((ComboBox nameCombo, ComboBox _) in _scheduleRows)
		{
			ResetCombo(nameCombo);
		}
		ResetCombo(cmb3DDirection);
		ResetCombo(cmb3DPlacement);
		ResetCombo(cmb3DTemplate);
		ResetCombo(cmbBackRotation);
		ResetCombo(cmbBackPlacement);
		ResetCombo(cmbBackTemplate);
		ResetCombo(cmbFrontRotation);
		ResetCombo(cmbFrontPlacement);
		ResetCombo(cmbFrontTemplate);
		ResetCombo(cmbLeftRotation);
		ResetCombo(cmbLeftPlacement);
		ResetCombo(cmbLeftTemplate);
		ResetCombo(cmbRightRotation);
		ResetCombo(cmbRightPlacement);
		ResetCombo(cmbRightTemplate);
		ResetCombo(cmbTopRotation);
		ResetCombo(cmbTopPlacement);
		ResetCombo(cmbTopTemplate);
	}

	private void ResetCombo(ComboBox combo)
	{
		if (combo == null)
		{
			return;
		}
		combo.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
		combo.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
		combo.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
		combo.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
	}

	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}

	public void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.AssemblySettingsWindow.xaml");
		SpoolingManagerXamlLoader.ApplyWindow(this, source, _productKind);
		SsSavantNeonChrome.ApplyChromelessDialog(this, allowResize: true);
		SpoolingManagerXamlLoader.ApplyNamedStyle(this, "btnLayoutSettings", "VgSquareButton");
		SpoolingManagerXamlLoader.ApplyNamedStyle(this, "tabMainSettings", "VgRoundedTabControl");
		tabMainSettings = SpoolingManagerXamlLoader.Find<TabControl>(this, "tabMainSettings");
		txtTigerStopCopperKeywords = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtTigerStopCopperKeywords");
		txtTigerStopPvcKeywords = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtTigerStopPvcKeywords");
		txtPcfCopperKeywords = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtPcfCopperKeywords");
		txtPcfPvcKeywords = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtPcfPvcKeywords");
		txtPcfSteelKeywords = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtPcfSteelKeywords");
		txtPcfCastIronKeywords = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtPcfCastIronKeywords");
		txtPipingSpecCatalogPath = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtPipingSpecCatalogPath");
		btnBrowsePipingSpecCatalog = SpoolingManagerXamlLoader.Find<Button>(this, "btnBrowsePipingSpecCatalog");
		if (btnBrowsePipingSpecCatalog != null)
		{
			btnBrowsePipingSpecCatalog.Click += BtnBrowsePipingSpecCatalog_Click;
		}
		txtBoardroomApiBaseUrl = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtBoardroomApiBaseUrl");
		btnTestBoardroomApi = SpoolingManagerXamlLoader.Find<Button>(this, "btnTestBoardroomApi");
		btnUseDefaultBoardroomApi = SpoolingManagerXamlLoader.Find<Button>(this, "btnUseDefaultBoardroomApi");
		txtBoardroomApiStatus = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtBoardroomApiStatus");
		if (btnTestBoardroomApi != null)
		{
			btnTestBoardroomApi.Click += BtnTestBoardroomApi_Click;
		}
		if (btnUseDefaultBoardroomApi != null)
		{
			btnUseDefaultBoardroomApi.Click += BtnUseDefaultBoardroomApi_Click;
		}
		lstTigerStopColumns = SpoolingManagerXamlLoader.Find<ListBox>(this, "lstTigerStopColumns");
		cmbTigerStopColumnAdd = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbTigerStopColumnAdd");
		btnTigerStopColumnAdd = SpoolingManagerXamlLoader.Find<Button>(this, "btnTigerStopColumnAdd");
		btnTigerStopColumnRemove = SpoolingManagerXamlLoader.Find<Button>(this, "btnTigerStopColumnRemove");
		btnTigerStopColumnUp = SpoolingManagerXamlLoader.Find<Button>(this, "btnTigerStopColumnUp");
		btnTigerStopColumnDown = SpoolingManagerXamlLoader.Find<Button>(this, "btnTigerStopColumnDown");
		lstPcfFields = SpoolingManagerXamlLoader.Find<ListBox>(this, "lstPcfFields");
		cmbPcfFieldAdd = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbPcfFieldAdd");
		btnPcfFieldAdd = SpoolingManagerXamlLoader.Find<Button>(this, "btnPcfFieldAdd");
		btnPcfFieldRemove = SpoolingManagerXamlLoader.Find<Button>(this, "btnPcfFieldRemove");
		btnPcfFieldUp = SpoolingManagerXamlLoader.Find<Button>(this, "btnPcfFieldUp");
		btnPcfFieldDown = SpoolingManagerXamlLoader.Find<Button>(this, "btnPcfFieldDown");
		btnImportSettings = SpoolingManagerXamlLoader.Find<Button>(this, "btnImportSettings");
		if (btnImportSettings != null)
		{
			btnImportSettings.Click += BtnImportSettings_Click;
		}
		btnExportSettings = SpoolingManagerXamlLoader.Find<Button>(this, "btnExportSettings");
		if (btnExportSettings != null)
		{
			btnExportSettings.Click += BtnExportSettings_Click;
		}
		WireExportColumnEditors();
		cmb3DDirection = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmb3DDirection");
		cmb3DPlacement = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmb3DPlacement");
		cmbBackRotation = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbBackRotation");
		cmbFrontRotation = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbFrontRotation");
		cmbLeftRotation = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbLeftRotation");
		cmbRightRotation = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbRightRotation");
		cmbTopRotation = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbTopRotation");
		cmbBackPlacement = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbBackPlacement");
		cmbFrontPlacement = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbFrontPlacement");
		cmbLeftPlacement = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbLeftPlacement");
		cmbRightPlacement = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbRightPlacement");
		cmbTopPlacement = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbTopPlacement");
		cmbTitleBlock = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbTitleBlock");
		cmbTagType = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbTagType");
		txtTagTypeLabel = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtTagTypeLabel");
		cmbHangerTagType = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbHangerTagType");
		cmbDuctTagType = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbDuctTagType");
		cmbWeldTagType = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbWeldTagType");
		cmbAssemblyTagType = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbAssemblyTagType");
		cmbWeldLogTextType = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbWeldLogTextType");
		cmbWeldLogSourceView = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbWeldLogSourceView");
		cmbViewportType = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbViewportType");
		pnlScheduleRows = SpoolingManagerXamlLoader.Find<StackPanel>(this, "pnlScheduleRows");
		btnAddSchedule = SpoolingManagerXamlLoader.Find<Button>(this, "btnAddSchedule");
		if (btnAddSchedule != null)
		{
			btnAddSchedule.Click += BtnAddSchedule_Click;
		}
		cmb3DTemplate = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmb3DTemplate");
		cmbBackTemplate = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbBackTemplate");
		cmbFrontTemplate = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbFrontTemplate");
		cmbLeftTemplate = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbLeftTemplate");
		cmbRightTemplate = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbRightTemplate");
		cmbTopTemplate = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbTopTemplate");
		chk3DOrtho = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chk3DOrtho");
		chk3DTag = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chk3DTag");
		chk3DAutoDim = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chk3DAutoDim");
		chkBackView = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkBackView");
		chkBackTag = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkBackTag");
		chkBackAutoDim = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkBackAutoDim");
		chkFrontView = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkFrontView");
		chkFrontTag = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkFrontTag");
		chkFrontAutoDim = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkFrontAutoDim");
		chkLeftView = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkLeftView");
		chkLeftTag = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkLeftTag");
		chkLeftAutoDim = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkLeftAutoDim");
		chkRightView = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkRightView");
		chkRightTag = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkRightTag");
		chkRightAutoDim = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkRightAutoDim");
		chkTopView = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkTopView");
		chkTopTag = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkTopTag");
		chkTopAutoDim = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkTopAutoDim");
		chkNumberWelds = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkNumberWelds");
		chkFillWeldLog = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkFillWeldLog");
		chkContinuationTags = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkContinuationTags");
		chkWeldTagPackagePrefix = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkWeldTagPackagePrefix");
		chkItemNumberCustomFormat = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkItemNumberCustomFormat");
		txtItemNumberStraightPrefix = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberStraightPrefix");
		txtItemNumberStraightSuffix = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberStraightSuffix");
		txtItemNumberFittingPrefix = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberFittingPrefix");
		txtItemNumberFittingSuffix = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberFittingSuffix");
		txtItemNumberValvePrefix = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberValvePrefix");
		txtItemNumberValveSuffix = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberValveSuffix");
		txtItemNumberStraightStart = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberStraightStart");
		txtItemNumberFittingStart = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberFittingStart");
		txtItemNumberValveStart = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtItemNumberValveStart");
		chkPlaceTrackingQr = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkPlaceTrackingQr");
		txtQrTrackingUrlBase = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtQrTrackingUrlBase");
		txtSettingsHeaderTitle = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtSettingsHeaderTitle");
		txtSettingsHeaderSubtitle = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtSettingsHeaderSubtitle");
		txtTabHeaderSheetSetup = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtTabHeaderSheetSetup");
		txtTabHeaderSchedules = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtTabHeaderSchedules");
		txtTabHeaderAnnotations = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtTabHeaderAnnotations");
		txtTabHeaderTigerStop = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtTabHeaderTigerStop");
		txtTabHeaderPcfFiles = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtTabHeaderPcfFiles");
		txtTabHeaderBoardroom = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtTabHeaderBoardroom");
		SsSavantNeonChrome.ApplyNeonDialogTitle(txtSettingsHeaderTitle, useScriptFont: true);
		lblViewScale = SpoolingManagerXamlLoader.Find<TextBlock>(this, "lblViewScale");
		cmbViewScale = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbViewScale");
		lblAutoDimensionType = SpoolingManagerXamlLoader.Find<TextBlock>(this, "lblAutoDimensionType");
		cmbAutoDimensionType = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbAutoDimensionType");
		lblAutoDimAnnotations = SpoolingManagerXamlLoader.Find<TextBlock>(this, "lblAutoDimAnnotations");
		chkAutoDimAnnotations = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkAutoDimAnnotations");
		lblAutoDimFittingSelf = SpoolingManagerXamlLoader.Find<TextBlock>(this, "lblAutoDimFittingSelf");
		chkAutoDimFittingSelf = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkAutoDimFittingSelf");
		grdSpoolViews = SpoolingManagerXamlLoader.Find<Grid>(this, "grdSpoolViews");
		txtViewsAutoDimHeader = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtViewsAutoDimHeader");
		btnLayoutSettings = SpoolingManagerXamlLoader.Find<Button>(this, "btnLayoutSettings");
		txtLayoutSettingsPrompt = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtLayoutSettingsPrompt");
		btnLayoutSettings.Click += BtnLayoutSettings_Click;
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Cancel").Click += BtnCancel_Click;
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Save").Click += BtnSave_Click;
	}
}
