using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Forms;
using Grid = System.Windows.Controls.Grid;
using RevitView = Autodesk.Revit.DB.View;
using Visibility = System.Windows.Visibility;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;
using SpoolingSavantV3Exports.Workers.SpoolingManager.ViewModels;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public partial class SpoolingManagerPane : System.Windows.Controls.UserControl
{
	private const int VkShift = 16;

	private readonly ObservableCollection<PackageAssemblyGroup> _packageGroups = new ObservableCollection<PackageAssemblyGroup>();

	private readonly List<AssemblyRow> _allRows = new List<AssemblyRow>();

	private readonly DispatcherTimer _searchDebounceTimer;

	private DispatcherTimer _themeWatchTimer;

	private UITheme _lastObservedRevitTheme;

	private bool _themeWatchInitialized;

	private bool _buttonClickSoundsEnabled = true;

	private ApplyAssemblyPackageRequest _pendingApplyReloadHint;

	private bool _deselectRowsAfterPackageApply;

	private UIApplication _uiapp;

	private AssemblyRow _assemblyShiftAnchorRow;

	public static readonly DependencyProperty PackageTitleEditorProperty = DependencyProperty.RegisterAttached("PackageTitleEditor", typeof(bool), typeof(SpoolingManagerPane), new PropertyMetadata(false, OnPackageTitleEditorChanged));

	internal TextBlock txtPaneTitleMode;

	internal TextBlock txtPaneTitleMain;

	internal Border brdNeonSign;

	internal Border brdPaneShell;

	internal System.Windows.Controls.TextBox txtSearch;
	internal System.Windows.Controls.ComboBox cmbSpoolContentMode;

	internal System.Windows.Controls.CheckBox chkSelectAll;

	internal ItemsControl treeAssemblies;

	internal ScrollViewer scrollAssemblyTree;

	internal System.Windows.Controls.TextBox txtPackageNumber;

	internal System.Windows.Controls.Button btnPlotPackages;

	internal System.Windows.Controls.Button btnExportToBoardroom;

	internal System.Windows.Controls.Button btnCreateSpoolMap;

	internal System.Windows.Controls.Button btnAddToPackage;

	internal System.Windows.Controls.Button btnRemoveFromPackage;

	internal System.Windows.Controls.Button btnCreateSpoolSheets;

	internal System.Windows.Controls.Button btnOpenSheets;

	internal System.Windows.Controls.Button btnRenameSheets;

	internal System.Windows.Controls.Button btnRefreshSheets;

	public SpoolingManagerKind ProductKind { get; set; }

	private string ToolDisplayName => ProductKind switch
	{
		SpoolingManagerKind.Mmc => "MMC Spooling Savant",
		SpoolingManagerKind.MmcTesting => "MMC Spooling Savant (Testing)",
		SpoolingManagerKind.AutoDimensionLab => "Spooling Savant (Auto Dim) — Testing",
		_ => "Spooling Savant",
	};

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);

	private static bool IsShiftKeyDownPhysically()
	{
		return (GetAsyncKeyState(16) & 0x8000) != 0;
	}

	private static bool IsShiftSelectGesture()
	{
		if ((Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
		{
			return IsShiftKeyDownPhysically();
		}
		return true;
	}

	private bool UsesRegularSheetBranchForActiveProduct()
	{
		return CreateSpoolSheetsHandler.UsesRegularSheetBranch(SpoolingManagerSettings.Load(ProductKind), ProductKind);
	}

	private void ApplyPlotPackagesButtonVisibility()
	{
		Visibility standardOnly = (ProductKind != SpoolingManagerKind.Standard) ? Visibility.Collapsed : Visibility.Visible;
		if (btnPlotPackages != null)
		{
			btnPlotPackages.Visibility = standardOnly;
		}
		if (btnExportToBoardroom != null)
		{
			btnExportToBoardroom.Visibility = standardOnly;
		}
		if (btnCreateSpoolMap != null)
		{
			btnCreateSpoolMap.Visibility = standardOnly;
		}
	}

	public SpoolingManagerPane()
	{
		InitializeComponent();
		InitializeNeonSign();
		ButtonClickSoundService.Attach(this, () => _buttonClickSoundsEnabled);
		treeAssemblies.DataContext = _packageGroups;
		if (scrollAssemblyTree != null && treeAssemblies != null)
		{
			ForwardTreeViewWheelToScrollViewer(treeAssemblies, scrollAssemblyTree);
		}
		RevitRequestBridge.Initialize();
		RevitRequestBridge.RenameSheetsCompleted += OnRenameSheetsCompleted;
		RevitRequestBridge.ApplyAssemblyPackageCompleted += OnApplyAssemblyPackageCompleted;
		SsSavantUiAppearance.SettingsChanged += OnUiAppearanceSettingsChanged;
		ApplyUiAppearance();
		StartRevitThemeWatcher();
		_searchDebounceTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(280.0)
		};
		_searchDebounceTimer.Tick += delegate
		{
			_searchDebounceTimer.Stop();
			ApplyFilter();
		};
		base.Unloaded += delegate
		{
			RevitRequestBridge.RenameSheetsCompleted -= OnRenameSheetsCompleted;
			RevitRequestBridge.ApplyAssemblyPackageCompleted -= OnApplyAssemblyPackageCompleted;
			SsSavantUiAppearance.SettingsChanged -= OnUiAppearanceSettingsChanged;
			_searchDebounceTimer.Stop();
			StopRevitThemeWatcher();
			_pendingApplyReloadHint = null;
		};
	}

	private void OnUiAppearanceSettingsChanged()
	{
		ApplyUiAppearance();
	}

	private void ApplyUiAppearance()
	{
		SsSavantUiAppearance.ApplyToElement(this);
		RefreshNeonSignForTheme();
	}

	// Watches Revit's UI theme (light/dark) while the pane is open so the pane re-themes
	// live when the user switches themes, without needing to reload the pane. Only re-applies
	// when the pane is set to follow Revit ("Match Revit"); explicit theme picks are left alone.
	private void StartRevitThemeWatcher()
	{
		try
		{
			_lastObservedRevitTheme = UIThemeManager.CurrentTheme;
			_themeWatchInitialized = true;
		}
		catch
		{
			_themeWatchInitialized = false;
		}
		_themeWatchTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(750.0)
		};
		_themeWatchTimer.Tick += OnRevitThemeWatchTick;
		_themeWatchTimer.Start();
	}

	private void StopRevitThemeWatcher()
	{
		if (_themeWatchTimer != null)
		{
			_themeWatchTimer.Stop();
			_themeWatchTimer.Tick -= OnRevitThemeWatchTick;
			_themeWatchTimer = null;
		}
	}

	private void OnRevitThemeWatchTick(object sender, EventArgs e)
	{
		// If a modal dialog is up (e.g. the post-generation summary shown from an external
		// event), this timer is being pumped inside that dialog's nested message loop. Touching
		// the Revit API from here can wedge Revit's API context and leave it frozen after the
		// dialog closes. Skip the tick until the thread is no longer modal.
		if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
		{
			return;
		}
		UITheme uITheme;
		try
		{
			uITheme = UIThemeManager.CurrentTheme;
		}
		catch
		{
			return;
		}
		if (!_themeWatchInitialized)
		{
			_lastObservedRevitTheme = uITheme;
			_themeWatchInitialized = true;
			return;
		}
		if (uITheme == _lastObservedRevitTheme)
		{
			return;
		}
		_lastObservedRevitTheme = uITheme;
		if (UiThemeCatalog.UsesRevitColors(SsSavantAppearanceStore.Current))
		{
			ApplyUiAppearance();
		}
		RefreshNeonSignForTheme();
	}

	public void LoadAssemblies(UIApplication uiapp)
	{
		_uiapp = uiapp;
		SpoolingManagerSettings.SetActiveProject(uiapp?.ActiveUIDocument?.Document);
		_buttonClickSoundsEnabled = SpoolingManagerSettings.Load(ProductKind).ButtonClickSoundsEnabled;
		ApplyPlotPackagesButtonVisibility();
		SyncSpoolContentModeComboFromSettings();
		if (((uiapp != null) ? uiapp.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(uiapp.Application.VersionNumber);
		}
		ApplyUiAppearance();
		RefreshAssemblies();
	}

	private bool _syncingSpoolContentMode;

	private void SyncSpoolContentModeComboFromSettings()
	{
		if (cmbSpoolContentMode == null)
			return;

		_syncingSpoolContentMode = true;
		try
		{
			string want = SpoolingManagerSettings.Load(ProductKind).GetSpoolContentModeTag();
			foreach (object item in cmbSpoolContentMode.Items)
			{
				if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, want, StringComparison.OrdinalIgnoreCase))
				{
					cmbSpoolContentMode.SelectedItem = cbi;
					UpdatePaneTitleFromTag(want);
					return;
				}
			}

			if (cmbSpoolContentMode.Items.Count > 0)
				cmbSpoolContentMode.SelectedIndex = 0;
			UpdatePaneTitleFromTag("Fabrication");
		}
		finally
		{
			_syncingSpoolContentMode = false;
		}
	}

	private void CmbSpoolContentMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingSpoolContentMode || cmbSpoolContentMode?.SelectedItem is not ComboBoxItem selected)
			return;

		string tag = selected.Tag as string ?? "Fabrication";
		SpoolingManagerSettings settings = SpoolingManagerSettings.Load(ProductKind);
		string previous = settings.GetSpoolContentModeTag();
		bool modeChanged = !string.Equals(previous, tag, StringComparison.OrdinalIgnoreCase);

		if (modeChanged)
		{
			settings.SetSpoolContentModeTag(tag);
			if (string.Equals(tag, "NativePipe", StringComparison.OrdinalIgnoreCase))
			{
				settings.NumberWeldsEnabled = false;
				settings.WeldLogEnabled = false;
			}
			settings.Save(ProductKind);
		}

		// Flip on every dropdown change (Fabrication / Families / Duct / Hangers).
		PlaySpoolContentFlipTransition(
			midpointAction: () =>
			{
				UpdatePaneTitleFromTag(tag);
				ClearAssemblyRowsForModeSwap();
			},
			completedAction: RefreshAssemblies);
	}

	/// <summary>
	/// Cheap visual reset used at the flip midpoint so the pane expands with a clean list.
	/// The expensive Revit queries run in <see cref="RefreshAssemblies"/> after the flip
	/// finishes; doing them at the midpoint blocked the UI thread and stalled the animation.
	/// </summary>
	private void ClearAssemblyRowsForModeSwap()
	{
		foreach (AssemblyRow row in _allRows)
		{
			row.PropertyChanged -= OnAssemblyRowPropertyChanged;
		}
		_allRows.Clear();
		_packageGroups.Clear();
		UpdateActionButtonsEnabledState();
	}

	private void UpdatePaneTitleFromTag(string tag)
	{
		if (txtPaneTitleMode == null)
		{
			return;
		}

		if (string.Equals(tag, "Duct", StringComparison.OrdinalIgnoreCase))
		{
			txtPaneTitleMode.Text = "Duct";
		}
		else if (string.Equals(tag, "Hangers", StringComparison.OrdinalIgnoreCase))
		{
			txtPaneTitleMode.Text = "Hangers";
		}
		else if (string.Equals(tag, "NativePipe", StringComparison.OrdinalIgnoreCase))
		{
			txtPaneTitleMode.Text = "Families";
		}
		else
		{
			txtPaneTitleMode.Text = "Fabrication";
		}
	}

	// ---- Neon sign (open once) ----------------------------------------------------

	private DropShadowEffect _neonMainGlow;
	private DropShadowEffect _neonModeGlow;
	private DropShadowEffect _neonPaneShellGlow;
	private bool _neonLit;
	private bool _neonModeActive = true;
	private bool _neonOpenStrikePlayed;
	private DispatcherTimer _neonWarmupTimer;

	private const double NeonOffTextOpacity = 0.22;
	private static readonly TimeSpan NeonWarmupDelay = TimeSpan.FromSeconds(0.65);

	/// <summary>
	/// Dark mode: on pane open, neons stay dark for a short warm-up, then flicker on
	/// and stay lit. Light mode: plain Revit-style header / border, no neon.
	/// </summary>
	private void InitializeNeonSign()
	{
		_neonMainGlow = txtPaneTitleMain?.Effect as DropShadowEffect;
		_neonModeGlow = txtPaneTitleMode?.Effect as DropShadowEffect;
		_neonPaneShellGlow = brdPaneShell?.Effect as DropShadowEffect;
		RefreshNeonSignForTheme();
		Loaded += OnNeonSignLoaded;
		Unloaded += OnNeonSignUnloaded;
	}

	private void OnNeonSignLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnNeonSignLoaded;
		// Start dark, wait for warm-up, then strike — only when the pane first appears.
		Dispatcher.BeginInvoke(new Action(BeginNeonOpenWarmup), DispatcherPriority.Loaded);
	}

	private void OnNeonSignUnloaded(object sender, RoutedEventArgs e)
	{
		StopNeonWarmupTimer();
	}

	private void BeginNeonOpenWarmup()
	{
		if (!_neonModeActive || _neonOpenStrikePlayed)
		{
			return;
		}

		StopNeonAnimations();
		_neonLit = false;
		SsSavantNeonChrome.ApplyPaneSignChrome(brdNeonSign, txtPaneTitleMain, txtPaneTitleMode, neon: true, lit: false);
		SsSavantNeonChrome.ApplyPaneOuterShell(brdPaneShell, neon: true, lit: false);

		StopNeonWarmupTimer();
		_neonWarmupTimer = new DispatcherTimer
		{
			Interval = NeonWarmupDelay
		};
		_neonWarmupTimer.Tick += delegate
		{
			StopNeonWarmupTimer();
			PlayNeonOpenStrike();
		};
		_neonWarmupTimer.Start();
	}

	private void StopNeonWarmupTimer()
	{
		if (_neonWarmupTimer != null)
		{
			_neonWarmupTimer.Stop();
			_neonWarmupTimer = null;
		}
	}

	/// <summary>
	/// Neon strike after warm-up — ribbon open or first appearance this session.
	/// </summary>
	private void PlayNeonOpenStrike()
	{
		if (!_neonModeActive)
		{
			return;
		}
		_neonOpenStrikePlayed = true;
		StopNeonAnimations();
		_neonLit = false;
		SsSavantNeonChrome.ApplyPaneSignChrome(brdNeonSign, txtPaneTitleMain, txtPaneTitleMode, neon: true, lit: false);
		SsSavantNeonChrome.ApplyPaneOuterShell(brdPaneShell, neon: true, lit: false);
		IgniteNeonSign();
	}

	private void RefreshNeonSignForTheme()
	{
		_neonModeActive = SsSavantNeonChrome.IsNeonEnabled;
		StopNeonAnimations();
		if (!_neonModeActive)
		{
			StopNeonWarmupTimer();
			_neonLit = false;
			SsSavantNeonChrome.ApplyPaneSignChrome(brdNeonSign, txtPaneTitleMain, txtPaneTitleMode, neon: false, lit: true);
			SsSavantNeonChrome.ApplyPaneOuterShell(brdPaneShell, neon: false, lit: true);
			Background = brdPaneShell?.Background;
			_neonMainGlow = null;
			_neonModeGlow = null;
			_neonPaneShellGlow = null;
			return;
		}

		// Restore glow effect objects after a light→dark switch (they were cleared).
		if (txtPaneTitleMain != null && txtPaneTitleMain.Effect == null)
		{
			txtPaneTitleMain.Effect = new DropShadowEffect
			{
				Color = SsSavantNeonChrome.NeonTitleGlow,
				BlurRadius = 18,
				ShadowDepth = 0,
				Opacity = 0.0
			};
		}
		if (txtPaneTitleMode != null && txtPaneTitleMode.Effect == null)
		{
			txtPaneTitleMode.Effect = new DropShadowEffect
			{
				Color = SsSavantNeonChrome.NeonModeGlow,
				BlurRadius = 14,
				ShadowDepth = 0,
				Opacity = 0.0
			};
		}
		if (brdPaneShell != null && brdPaneShell.Effect == null)
		{
			brdPaneShell.Effect = new DropShadowEffect
			{
				Color = SsSavantNeonChrome.DarkBorderColor,
				BlurRadius = 14,
				ShadowDepth = 0,
				Opacity = 0.0
			};
		}
		_neonMainGlow = txtPaneTitleMain?.Effect as DropShadowEffect;
		_neonModeGlow = txtPaneTitleMode?.Effect as DropShadowEffect;
		_neonPaneShellGlow = brdPaneShell?.Effect as DropShadowEffect;

		// After the open strike, stay lit across theme ticks. Before that, stay dark
		// until PlayNeonOpenStrike runs.
		bool lit = _neonOpenStrikePlayed || _neonLit;
		_neonLit = lit;
		SsSavantNeonChrome.ApplyPaneSignChrome(brdNeonSign, txtPaneTitleMain, txtPaneTitleMode, neon: true, lit: lit);
		SsSavantNeonChrome.ApplyPaneOuterShell(brdPaneShell, neon: true, lit: lit);
		Background = brdPaneShell?.Background;
	}

	private void IgniteNeonSign()
	{
		if (!_neonModeActive)
		{
			return;
		}
		if (_neonLit)
		{
			return;
		}
		_neonLit = true;
		SsSavantNeonChrome.ApplyPaneSignChrome(brdNeonSign, txtPaneTitleMain, txtPaneTitleMode, neon: true, lit: true);
		SsSavantNeonChrome.ApplyPaneOuterShell(brdPaneShell, neon: true, lit: true);

		// Set the final (lit) base values, then run a flicker that overrides them
		// briefly — when it finishes the base values remain.
		try
		{
			if (txtPaneTitleMain != null)
			{
				txtPaneTitleMain.Opacity = 1.0;
				txtPaneTitleMain.BeginAnimation(OpacityProperty, BuildNeonStrike(NeonOffTextOpacity, 1.0));
			}
			if (txtPaneTitleMode != null)
			{
				txtPaneTitleMode.Opacity = 1.0;
				txtPaneTitleMode.BeginAnimation(OpacityProperty, BuildNeonStrike(NeonOffTextOpacity, 1.0));
			}
			_neonMainGlow?.BeginAnimation(DropShadowEffect.OpacityProperty, BuildNeonStrike(0.0, 0.95));
			if (_neonMainGlow != null)
			{
				_neonMainGlow.Opacity = 0.95;
			}
			_neonModeGlow?.BeginAnimation(DropShadowEffect.OpacityProperty, BuildNeonStrike(0.0, 0.95));
			if (_neonModeGlow != null)
			{
				_neonModeGlow.Opacity = 0.95;
			}
			_neonPaneShellGlow?.BeginAnimation(DropShadowEffect.OpacityProperty, BuildNeonStrike(0.0, 0.45));
			if (_neonPaneShellGlow != null)
			{
				_neonPaneShellGlow.Opacity = 0.45;
			}
		}
		catch
		{
			// If anything about animation fails, land in the fully lit state.
			StopNeonAnimations();
		}
	}

	private void StopNeonAnimations()
	{
		txtPaneTitleMain?.BeginAnimation(OpacityProperty, null);
		txtPaneTitleMode?.BeginAnimation(OpacityProperty, null);
		_neonMainGlow?.BeginAnimation(DropShadowEffect.OpacityProperty, null);
		_neonModeGlow?.BeginAnimation(DropShadowEffect.OpacityProperty, null);
		_neonPaneShellGlow?.BeginAnimation(DropShadowEffect.OpacityProperty, null);
	}

	/// <summary>
	/// Neon tube strike: a few abrupt off/on flashes before holding steady. Discrete
	/// keyframes give the hard flicker of a real sign catching. FillBehavior.Stop lets
	/// the pre-set lit base value take over when the flicker ends.
	/// </summary>
	private static DoubleAnimationUsingKeyFrames BuildNeonStrike(double off, double lit)
	{
		DoubleAnimationUsingKeyFrames animation = new DoubleAnimationUsingKeyFrames
		{
			Duration = TimeSpan.FromMilliseconds(560),
			FillBehavior = FillBehavior.Stop
		};
		void At(double ms, double fraction)
		{
			animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
				off + (lit - off) * fraction,
				KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
		}
		At(0, 0.0);
		At(50, 0.85);
		At(95, 0.10);
		At(160, 0.90);
		At(215, 0.25);
		At(290, 1.0);
		At(360, 0.55);
		At(430, 1.0);
		At(560, 1.0);
		return animation;
	}

	/// <summary>
	/// Horizontal flip illusion when the Spool Content mode changes: the pane squeezes to
	/// an edge-on sliver, the title/list swap at the midpoint, then it expands back out.
	/// Keep <paramref name="midpointAction"/> cheap (UI-only) — anything slow stalls the
	/// animation because it runs on the UI thread between the two halves. Expensive work
	/// (Revit element queries) belongs in <paramref name="completedAction"/>, which is
	/// dispatched at Background priority after the expand has rendered.
	/// </summary>
	private void PlaySpoolContentFlipTransition(Action midpointAction, Action completedAction = null)
	{
		try
		{
			ScaleTransform flip = new ScaleTransform(1.0, 1.0);
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
			RenderTransform = flip;
			DoubleAnimation shrink = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(170))
			{
				EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
			};
			shrink.Completed += (_, __) =>
			{
				try
				{
					midpointAction?.Invoke();
				}
				catch
				{
				}
				DoubleAnimation expand = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(170))
				{
					EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
				};
				expand.Completed += (_, ___) =>
				{
					flip.BeginAnimation(ScaleTransform.ScaleXProperty, null);
					flip.ScaleX = 1.0;
					RenderTransform = null;
					if (completedAction != null)
					{
						// Let the fully expanded frame paint before the heavy refresh runs.
						Dispatcher.BeginInvoke(completedAction, DispatcherPriority.Background);
					}
				};
				flip.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
			};
			flip.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
		}
		catch
		{
			midpointAction?.Invoke();
			completedAction?.Invoke();
		}
	}

	private void RefreshAssemblies()
	{
		foreach (AssemblyRow allRow in _allRows)
		{
			allRow.PropertyChanged -= OnAssemblyRowPropertyChanged;
		}
		_allRows.Clear();
		_packageGroups.Clear();
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			UpdateActionButtonsEnabledState();
			return;
		}
		UIDocument activeUIDocument = _uiapp.ActiveUIDocument;
		Document doc = activeUIDocument.Document;
		SpoolingManagerSettings.SetActiveProject(doc);
		RevitView activeView = doc.ActiveView;
		if (activeView == null)
		{
			return;
		}
		List<ElementId> visibleAssemblyIdsInActiveView = GetVisibleAssemblyIdsInActiveView(doc, activeView);
		List<AssemblyInstance> list = (from x in visibleAssemblyIdsInActiveView.Select(delegate(ElementId id)
			{
				Element element = doc.GetElement(id);
				return (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			})
			where x != null
			select x).OrderBy((AssemblyInstance x) => AssemblyDisplayName.Get(x), StringComparer.OrdinalIgnoreCase).ToList();

		string contentMode = SpoolingManagerSettings.Load(ProductKind).GetSpoolContentModeTag();
		list = list.Where((AssemblyInstance a) => AssemblyMatchesSpoolContentMode(doc, a, contentMode)).ToList();

		HashSet<ElementId> hashSet = CreateSpoolSheetsHandler.GetAssemblyInstanceIdsHavingSpoolSheet(regularSheetBranch: UsesRegularSheetBranchForActiveProduct(), doc: doc, displayedAssemblyInstanceIds: list.Select(a => ((Element)a).Id).ToList());
		foreach (AssemblyInstance item in list)
		{
			AssemblyRow assemblyRow = new AssemblyRow
			{
				AssemblyId = ((Element)item).Id,
				SpoolName = AssemblyDisplayName.Get(item),
				SPackage = ReadSPackageFromAssembly(item),
				IsSelected = false,
				HasSpoolSheet = hashSet.Contains(((Element)item).Id)
			};
			assemblyRow.PropertyChanged += OnAssemblyRowPropertyChanged;
			_allRows.Add(assemblyRow);
		}
		ApplyFilter();
		chkSelectAll.IsChecked = false;
		_assemblyShiftAnchorRow = null;
		UpdateActionButtonsEnabledState();
	}

	/// <summary>
	/// Fabrication = has fabrication pipework (may also include hangers).
	/// Families = has native pipe/fitting/accessory.
	/// Duct = fabrication members are ductwork only (no pipework, no hangers, no native).
	/// Hangers = fabrication members are hangers only (no pipework, no duct, no native).
	/// </summary>
	private static bool AssemblyMatchesSpoolContentMode(Document doc, AssemblyInstance assembly, string contentMode)
	{
		if (doc == null || assembly == null)
			return false;

		bool hasNative = false;
		bool hasPipework = false;
		bool hasDuct = false;
		bool hasHanger = false;

		foreach (ElementId memberId in assembly.GetMemberIds())
		{
			Element member = doc.GetElement(memberId);
			if (member == null)
				continue;

			if (NativePipeSpoolSupport.IsNativePipeworkElement(member))
			{
				hasNative = true;
				continue;
			}

			if (FabricationPartClassification.IsFabricationHanger(member))
			{
				hasHanger = true;
				continue;
			}

			if (FabricationPartClassification.IsFabricationDuctwork(member))
			{
				hasDuct = true;
				continue;
			}

			if (FabricationPartClassification.IsFabricationPipeworkContent(member))
			{
				hasPipework = true;
			}
		}

		if (string.Equals(contentMode, "NativePipe", StringComparison.OrdinalIgnoreCase))
		{
			return hasNative;
		}

		if (string.Equals(contentMode, "Duct", StringComparison.OrdinalIgnoreCase))
		{
			return hasDuct && !hasPipework && !hasHanger && !hasNative;
		}

		if (string.Equals(contentMode, "Hangers", StringComparison.OrdinalIgnoreCase))
		{
			return hasHanger && !hasPipework && !hasDuct && !hasNative;
		}

		// Fabrication pipework mode: must include pipework content (mixed hangers OK).
		return hasPipework;
	}

	private static string ReadSPackageFromAssembly(AssemblyInstance assembly)
	{
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Invalid comparison between Unknown and I4
		if (assembly == null)
		{
			return string.Empty;
		}
		Parameter val = ((Element)assembly).LookupParameter("S-Package");
		if (val == null || (int)val.StorageType != 3)
		{
			return string.Empty;
		}
		return (val.AsString() ?? string.Empty).Trim();
	}

	private static List<ElementId> GetVisibleAssemblyIdsInActiveView(Document doc, RevitView activeView)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Expected O, but got Unknown
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Expected O, but got Unknown
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Expected O, but got Unknown
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Expected O, but got Unknown
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Expected O, but got Unknown
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Expected O, but got Unknown
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Expected O, but got Unknown
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Expected O, but got Unknown
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Expected O, but got Unknown
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Expected O, but got Unknown
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		foreach (AssemblyInstance item in new FilteredElementCollector(doc, ((Element)activeView).Id).OfClass(typeof(AssemblyInstance)))
		{
			AssemblyInstance val = item;
			if (val != null)
			{
				hashSet.Add(((Element)val).Id);
			}
		}
		LogicalOrFilter val2 = new LogicalOrFilter((IList<ElementFilter>)new List<ElementFilter>
		{
			(ElementFilter)new ElementClassFilter(typeof(FabricationPart)),
			(ElementFilter)new ElementClassFilter(typeof(Pipe)),
			(ElementFilter)new ElementClassFilter(typeof(FlexPipe)),
			(ElementFilter)new ElementClassFilter(typeof(Duct)),
			(ElementFilter)new ElementClassFilter(typeof(FlexDuct)),
			(ElementFilter)new ElementClassFilter(typeof(CableTray)),
			(ElementFilter)new ElementClassFilter(typeof(Conduit)),
			(ElementFilter)new ElementClassFilter(typeof(FamilyInstance))
		});
		foreach (Element item2 in new FilteredElementCollector(doc, ((Element)activeView).Id).WherePasses((ElementFilter)(object)val2))
		{
			if (item2 != null)
			{
				ElementId assemblyInstanceId = item2.AssemblyInstanceId;
				if (assemblyInstanceId != (ElementId)null && assemblyInstanceId != ElementId.InvalidElementId)
				{
					hashSet.Add(assemblyInstanceId);
				}
			}
		}
		return hashSet.ToList();
	}

	private void OnAssemblyRowPropertyChanged(object sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != "IsSelected" || !(sender is AssemblyRow item))
		{
			return;
		}
		foreach (PackageAssemblyGroup packageGroup in _packageGroups)
		{
			if (packageGroup.Assemblies.Contains(item))
			{
				packageGroup.RefreshHeaderFromChildren();
				break;
			}
		}
		UpdateSelectAllState();
		UpdateActionButtonsEnabledState();
	}

	/// <summary>
	/// Sheet/package action buttons need checked rows; Spool Map and Plot Packages
	/// additionally need at least one checked row that belongs to a package.
	/// </summary>
	private void UpdateActionButtonsEnabledState()
	{
		bool anySelected = _allRows.Any((AssemblyRow x) => x.IsSelected);
		bool packageSelected = _allRows.Any((AssemblyRow x) => x.IsSelected && !string.IsNullOrWhiteSpace(x.SPackage));
		if (btnAddToPackage != null)
		{
			btnAddToPackage.IsEnabled = anySelected;
		}
		if (btnRemoveFromPackage != null)
		{
			btnRemoveFromPackage.IsEnabled = anySelected;
		}
		if (btnCreateSpoolSheets != null)
		{
			btnCreateSpoolSheets.IsEnabled = anySelected;
		}
		if (btnOpenSheets != null)
		{
			btnOpenSheets.IsEnabled = anySelected;
		}
		if (btnRenameSheets != null)
		{
			btnRenameSheets.IsEnabled = anySelected;
		}
		if (btnRefreshSheets != null)
		{
			btnRefreshSheets.IsEnabled = anySelected;
		}
		if (btnCreateSpoolMap != null)
		{
			btnCreateSpoolMap.IsEnabled = packageSelected;
		}
		if (btnPlotPackages != null)
		{
			btnPlotPackages.IsEnabled = packageSelected;
		}
		if (btnExportToBoardroom != null)
		{
			btnExportToBoardroom.IsEnabled = packageSelected;
		}
	}

	private void ApplyFilter()
	{
		string text = ((txtSearch != null) ? (txtSearch.Text ?? string.Empty) : string.Empty);
		string trimmed = text.Trim();
		IEnumerable<AssemblyRow> enumerable = _allRows;
		if (!string.IsNullOrWhiteSpace(trimmed))
		{
			enumerable = enumerable.Where((AssemblyRow x) => !string.IsNullOrWhiteSpace(x.SpoolName) && x.SpoolName.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase));
		}
		RebuildGroupedView(enumerable);
	}

	private static string PackageGroupKey(AssemblyRow row)
	{
		string text = row?.SPackage;
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}
		return string.Empty;
	}

	private void RebuildGroupedView(IEnumerable<AssemblyRow> filteredRows)
	{
		_packageGroups.Clear();
		foreach (IGrouping<string, AssemblyRow> item in (from g in filteredRows.OrderBy((AssemblyRow x) => x.SpoolName ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList().GroupBy(PackageGroupKey)
			orderby string.IsNullOrEmpty(g.Key) ? 1 : 0
			select g).ThenBy((IGrouping<string, AssemblyRow> g) => g.Key, StringComparer.OrdinalIgnoreCase))
		{
			string canonicalPackageKey = (string.IsNullOrEmpty(item.Key) ? string.Empty : item.Key);
			PackageAssemblyGroup packageGroup = new PackageAssemblyGroup(canonicalPackageKey, item.ToList());
			packageGroup.RenameHandler = delegate(string newPackageValue)
			{
				ApplyInlinePackageRename(packageGroup, newPackageValue);
			};
			_packageGroups.Add(packageGroup);
		}
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			SetAllPackageRootsExpanded(expand: true);
		}, DispatcherPriority.Render);
		UpdateSelectAllState();
	}

	private void SetAllPackageRootsExpanded(bool expand)
	{
		foreach (PackageAssemblyGroup packageGroup in _packageGroups)
		{
			packageGroup.PackageTreeExpanded = expand;
		}
	}

	private void BtnCollapsePackages_Click(object sender, RoutedEventArgs e)
	{
		SetAllPackageRootsExpanded(expand: false);
	}

	private void BtnExpandPackages_Click(object sender, RoutedEventArgs e)
	{
		SetAllPackageRootsExpanded(expand: true);
	}

	private List<AssemblyRow> GetVisibleAssemblyRowsInTreeOrder()
	{
		List<AssemblyRow> list = new List<AssemblyRow>();
		foreach (PackageAssemblyGroup packageGroup in _packageGroups)
		{
			foreach (AssemblyRow assembly in packageGroup.Assemblies)
			{
				list.Add(assembly);
			}
		}
		return list;
	}

	private static void ForwardTreeViewWheelToScrollViewer(ItemsControl scrollHost, ScrollViewer scrollViewer)
	{
		scrollHost.PreviewMouseWheel += delegate(object _, MouseWheelEventArgs e)
		{
			if (scrollViewer != null && !(scrollViewer.ScrollableHeight <= 0.0))
			{
				MouseWheelEventArgs e2 = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
				{
					RoutedEvent = UIElement.MouseWheelEvent,
					Source = scrollViewer
				};
				scrollViewer.RaiseEvent(e2);
				e.Handled = true;
			}
		};
	}

	private void BtnRefresh_Click(object sender, RoutedEventArgs e)
	{
		RefreshAssemblies();
	}

	private void BtnClear_Click(object sender, RoutedEventArgs e)
	{
		foreach (AssemblyRow allRow in _allRows)
		{
			allRow.IsSelected = false;
		}
		chkSelectAll.IsChecked = false;
		_assemblyShiftAnchorRow = null;
	}

	private void BtnIsolate_Click(object sender, RoutedEventArgs e)
	{
		RequestAssemblyTemporaryVisibility(AssemblyTemporaryVisibilityAction.IsolateMembers);
	}

	private void BtnHide_Click(object sender, RoutedEventArgs e)
	{
		RequestAssemblyTemporaryVisibility(AssemblyTemporaryVisibilityAction.HideMembers);
	}

	private void RequestAssemblyTemporaryVisibility(AssemblyTemporaryVisibilityAction action)
	{
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Invalid comparison between Unknown and I4
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		UIDocument activeUIDocument = _uiapp.ActiveUIDocument;
		Document document = activeUIDocument.Document;
		List<AssemblyRow> selectedRows = _allRows.Where((AssemblyRow x) => x.IsSelected).ToList();
		List<ElementId> list = CollectTemporaryVisibilityTargets(document, activeUIDocument, selectedRows);
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Check one or more assemblies in the list and/or select fabrication pipework or model assemblies in Revit, then try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		RevitRequestBridge.Initialize();
		if ((int)RevitRequestBridge.RaiseAssemblyTemporaryVisibility(new AssemblyTemporaryVisibilityRequest
		{
			Action = action,
			MemberElementIds = list
		}) == 2)
		{
			SsSavantMessageBox.Show("Revit did not run the request (another dialog may be open or the session is busy). Close other dialogs and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private static List<ElementId> CollectTemporaryVisibilityTargets(Document doc, UIDocument uidoc, List<AssemblyRow> selectedRows)
	{
		HashSet<long> seen = new HashSet<long>();
		List<ElementId> result = new List<ElementId>();
		if (selectedRows != null)
		{
			foreach (AssemblyRow selectedRow in selectedRows)
			{
				ElementId assemblyId = selectedRow.AssemblyId;
				if (assemblyId == (ElementId)null || assemblyId == ElementId.InvalidElementId)
				{
					continue;
				}
				Element element = doc.GetElement(assemblyId);
				AssemblyInstance val = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
				if (val == null)
				{
					continue;
				}
				addId(assemblyId);
				foreach (ElementId memberId in val.GetMemberIds())
				{
					addId(memberId);
				}
			}
		}
		object obj;
		if (uidoc == null)
		{
			obj = null;
		}
		else
		{
			Selection selection = uidoc.Selection;
			obj = ((selection != null) ? selection.GetElementIds() : null);
		}
		ICollection<ElementId> collection = (ICollection<ElementId>)obj;
		if (collection != null)
		{
			foreach (ElementId item in collection)
			{
				Element element2 = doc.GetElement(item);
				if (element2 == null)
				{
					continue;
				}
				AssemblyInstance val2 = (AssemblyInstance)(object)((element2 is AssemblyInstance) ? element2 : null);
				if (val2 != null)
				{
					addId(((Element)val2).Id);
					foreach (ElementId memberId2 in val2.GetMemberIds())
					{
						addId(memberId2);
					}
				}
				else
				{
					if (element2.Category == null || element2.Category.Id.Value != -2008208)
					{
						continue;
					}
					addId(item);
					ElementId assemblyInstanceId = element2.AssemblyInstanceId;
					if (!(assemblyInstanceId != (ElementId)null) || !(assemblyInstanceId != ElementId.InvalidElementId))
					{
						continue;
					}
					Element element3 = doc.GetElement(assemblyInstanceId);
					AssemblyInstance val3 = (AssemblyInstance)(object)((element3 is AssemblyInstance) ? element3 : null);
					if (val3 == null)
					{
						continue;
					}
					addId(assemblyInstanceId);
					foreach (ElementId memberId3 in val3.GetMemberIds())
					{
						addId(memberId3);
					}
				}
			}
		}
		return result;
		void addId(ElementId id)
		{
			if (!(id == (ElementId)null) && !(id == ElementId.InvalidElementId) && seen.Add(id.Value))
			{
				result.Add(id);
			}
		}
	}

	private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
	{
		bool valueOrDefault = chkSelectAll.IsChecked == true;
		foreach (AssemblyRow item in GetVisibleAssemblyRowsInTreeOrder())
		{
			item.IsSelected = valueOrDefault;
		}
	}

	private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_searchDebounceTimer != null)
		{
			_searchDebounceTimer.Stop();
			_searchDebounceTimer.Start();
		}
	}

	private static ApplyAssemblyPackageRequest CloneApplyReloadHint(ApplyAssemblyPackageRequest req)
	{
		if (req == null || req.AssemblyIds == null || req.AssemblyIds.Count == 0)
		{
			return null;
		}
		return new ApplyAssemblyPackageRequest
		{
			AssemblyIds = new List<ElementId>(req.AssemblyIds),
			PackageValue = req.PackageValue,
			ClearPackage = req.ClearPackage,
			ProductKind = req.ProductKind
		};
	}

	private bool TryRefreshRowsAfterPackageApply(ApplyAssemblyPackageRequest req)
	{
		UIApplication uiapp = _uiapp;
		if (((uiapp != null) ? uiapp.ActiveUIDocument : null) == null || req == null || req.AssemblyIds == null || req.AssemblyIds.Count == 0 || _allRows.Count == 0)
		{
			return false;
		}
		Document document = _uiapp.ActiveUIDocument.Document;
		HashSet<ElementId> hashSet = new HashSet<ElementId>(req.AssemblyIds);
		if (hashSet.Count == 0)
		{
			return false;
		}
		List<AssemblyRow> list = new List<AssemblyRow>();
		foreach (AssemblyRow allRow in _allRows)
		{
			if (hashSet.Contains(allRow.AssemblyId))
			{
				list.Add(allRow);
			}
		}
		if (list.Count != hashSet.Count)
		{
			return false;
		}
		foreach (AssemblyRow item in list)
		{
			Element element = document.GetElement(item.AssemblyId);
			AssemblyInstance val = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			if (val == null)
			{
				return false;
			}
			item.SPackage = ReadSPackageFromAssembly(val);
		}
		return true;
	}

	private void AssemblyList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		DependencyObject source = e.OriginalSource as DependencyObject;
		AssemblyRow assemblyRow = FindAncestorAssemblyRowTreeItem(source);
		if (assemblyRow == null)
		{
			return;
		}
		bool num = HasCheckBoxAncestor(source);
		bool flag = IsShiftSelectGesture();
		if (num && !flag)
		{
			return;
		}
		List<AssemblyRow> visibleAssemblyRowsInTreeOrder = GetVisibleAssemblyRowsInTreeOrder();
		if (flag && _assemblyShiftAnchorRow != null && visibleAssemblyRowsInTreeOrder.Contains(_assemblyShiftAnchorRow))
		{
			int num2 = visibleAssemblyRowsInTreeOrder.IndexOf(_assemblyShiftAnchorRow);
			int num3 = visibleAssemblyRowsInTreeOrder.IndexOf(assemblyRow);
			if (num2 >= 0 && num3 >= 0)
			{
				int num4 = Math.Min(num2, num3);
				int num5 = Math.Max(num2, num3);
				for (int i = num4; i <= num5; i++)
				{
					visibleAssemblyRowsInTreeOrder[i].IsSelected = true;
				}
			}
			else
			{
				assemblyRow.IsSelected = !assemblyRow.IsSelected;
				_assemblyShiftAnchorRow = assemblyRow;
			}
		}
		else
		{
			assemblyRow.IsSelected = !assemblyRow.IsSelected;
			_assemblyShiftAnchorRow = assemblyRow;
		}
		UpdateSelectAllState();
		e.Handled = true;
	}

	private static AssemblyRow FindAncestorAssemblyRowTreeItem(DependencyObject source)
	{
		for (DependencyObject dependencyObject = source; dependencyObject != null; dependencyObject = VisualTreeHelper.GetParent(dependencyObject))
		{
			if (dependencyObject is FrameworkElement { DataContext: AssemblyRow dataContext })
			{
				return dataContext;
			}
		}
		return null;
	}

	private static bool HasCheckBoxAncestor(DependencyObject source)
	{
		for (DependencyObject dependencyObject = source; dependencyObject != null; dependencyObject = VisualTreeHelper.GetParent(dependencyObject))
		{
			if (dependencyObject is System.Windows.Controls.CheckBox)
			{
				return true;
			}
		}
		return false;
	}

	private void UpdateSelectAllState()
	{
		List<AssemblyRow> visibleAssemblyRowsInTreeOrder = GetVisibleAssemblyRowsInTreeOrder();
		if (visibleAssemblyRowsInTreeOrder.Count == 0)
		{
			chkSelectAll.IsChecked = false;
			return;
		}
		chkSelectAll.IsChecked = visibleAssemblyRowsInTreeOrder.All((AssemblyRow x) => x.IsSelected);
	}

	private void BtnSettings_Click(object sender, RoutedEventArgs e)
	{
		OpenSettingsWindow();
	}

	private void OpenSettingsWindow()
	{
		SetSettingsButtonLit(true);
		try
		{
			AssemblySettingsWindow assemblySettingsWindow = new AssemblySettingsWindow(_uiapp, ProductKind);
			SsSavantDialogForeground.Attach(assemblySettingsWindow, _uiapp);
			assemblySettingsWindow.ShowDialog();
			_buttonClickSoundsEnabled = SpoolingManagerSettings.Load(ProductKind).ButtonClickSoundsEnabled;
		}
		catch (Exception ex)
		{
			SsSavantMessageBox.Show("Could not open Settings.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			SetSettingsButtonLit(false);
		}
	}

	private void SetSettingsButtonLit(bool lit)
	{
		System.Windows.Controls.Button settingsButton = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnPaneSettings");
		if (settingsButton == null)
		{
			return;
		}

		if (lit && SsSavantNeonChrome.IsNeonEnabled)
		{
			settingsButton.BorderBrush = new SolidColorBrush(SsSavantNeonChrome.DarkBorderColor);
			settingsButton.BorderThickness = new Thickness(2);
			settingsButton.Effect = new DropShadowEffect
			{
				Color = SsSavantNeonChrome.DarkBorderColor,
				BlurRadius = 14,
				ShadowDepth = 0,
				Opacity = 0.85
			};
			if (settingsButton.Content is TextBlock icon)
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
			settingsButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
			settingsButton.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
			settingsButton.Effect = null;
			if (settingsButton.Content is TextBlock icon)
			{
				icon.Effect = null;
				icon.SetResourceReference(TextBlock.ForegroundProperty, "SsSavantForegroundPrimary");
			}
		}
	}

	private void BtnCreateSpoolSheets_Click(object sender, RoutedEventArgs e)
	{
		List<AssemblyRow> list = (from x in _allRows
			where x.IsSelected
			orderby x.SpoolName
			select x).ToList();
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Please select at least one assembly before creating spool sheets.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (string.IsNullOrWhiteSpace(SpoolingManagerSettings.Load(ProductKind).TitleBlockName))
		{
			SsSavantMessageBox.Show("Please select a Title Block in Settings first.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		CreateSpoolSheetsRequest createSpoolSheetsRequest = new CreateSpoolSheetsRequest
		{
			AssemblyIds = list.Select((AssemblyRow x) => x.AssemblyId).ToList(),
			ProductKind = ProductKind
		};
		List<string> existingSpoolSheetDescriptions = GetExistingSpoolSheetDescriptions(_uiapp.ActiveUIDocument.Document, createSpoolSheetsRequest.AssemblyIds);
		if (existingSpoolSheetDescriptions.Count > 0)
		{
			ExistingSheetsPromptWindow existingSheetsPromptWindow = new ExistingSheetsPromptWindow(existingSpoolSheetDescriptions);
			Window window = Window.GetWindow(this);
			if (window != null)
			{
				existingSheetsPromptWindow.Owner = window;
			}
			if (existingSheetsPromptWindow.ShowDialog() != true || existingSheetsPromptWindow.SelectedAction == ExistingSheetAction.Cancel)
			{
				return;
			}
			createSpoolSheetsRequest.ExistingSheetAction = existingSheetsPromptWindow.SelectedAction;
		}
		try
		{
			Window hostWindow = Window.GetWindow(this);
			IntPtr hostHwnd = IntPtr.Zero;
			try
			{
				if (hostWindow != null)
				{
					hostHwnd = new System.Windows.Interop.WindowInteropHelper(hostWindow).Handle;
				}
				else if (_uiapp != null)
				{
					hostHwnd = _uiapp.MainWindowHandle;
				}
			}
			catch
			{
			}
			OperationProgressSession.Show(ToolDisplayName, hostWindow, hostHwnd,
				allowCancel: true, neonProgressText: "Sheets Completed");
			OperationProgressSession.Report(1.0, "Queued…", list.Count + " spool(s) selected");
			RevitRequestBridge.RaiseCreateSheets(createSpoolSheetsRequest);
		}
		catch (Exception ex)
		{
			OperationProgressSession.Close();
			SsSavantMessageBox.Show("Failed to start sheet creation.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void BtnOpenSheets_Click(object sender, RoutedEventArgs e)
	{
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Invalid comparison between Unknown and I4
		List<AssemblyRow> list = _allRows.Where((AssemblyRow x) => x.IsSelected).ToList();
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Please select at least one assembly before opening sheets.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		Document document = _uiapp.ActiveUIDocument.Document;
		bool regularSheetBranch = UsesRegularSheetBranchForActiveProduct();
		Dictionary<ElementId, ViewSheet> dictionary = CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(document, regularSheetBranch, list.Select((AssemblyRow x) => x.AssemblyId).ToList());
		List<ViewSheet> list2 = new List<ViewSheet>();
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		foreach (AssemblyRow item in list)
		{
			if (dictionary.TryGetValue(item.AssemblyId, out var value) && value != null && hashSet.Add(((Element)value).Id))
			{
				list2.Add(value);
			}
		}
		if (list2.Count == 0)
		{
			SsSavantMessageBox.Show("No spool sheets were found for the selected rows.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if ((int)RevitRequestBridge.RaiseOpenSpoolSheets(list2.ConvertAll((ViewSheet s) => ((Element)s).Id)) == 2)
		{
			SsSavantMessageBox.Show("Revit could not open sheets (for example, another dialog may be blocking the session). Close it and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void BtnRenameSheets_Click(object sender, RoutedEventArgs e)
	{
		List<AssemblyRow> list = (from x in _allRows
			where x.IsSelected
			orderby x.SpoolName
			select x).ToList();
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Please select at least one assembly before renaming sheets.", "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		RenameSheetsWindow renameSheetsWindow = new RenameSheetsWindow(list.Select((AssemblyRow x) => new RenameSheetRow
		{
			AssemblyId = x.AssemblyId,
			CurrentName = x.SpoolName,
			NewName = string.Empty
		}).ToList())
		{
			ProductKind = ProductKind
		};
		Window window = Window.GetWindow(this);
		if (window != null)
		{
			renameSheetsWindow.Owner = window;
		}
		if (renameSheetsWindow.ShowDialog() != true || renameSheetsWindow.RenameRequest == null || renameSheetsWindow.RenameRequest.Items.Count == 0)
		{
			return;
		}
		try
		{
			RevitRequestBridge.RaiseRenameSheets(renameSheetsWindow.RenameRequest);
		}
		catch (Exception ex)
		{
			SsSavantMessageBox.Show("Failed to start sheet rename.\n\n" + ex.Message, "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void BtnRefreshSheets_Click(object sender, RoutedEventArgs e)
	{
		List<AssemblyRow> list = (from x in _allRows
			where x.IsSelected
			orderby x.SpoolName
			select x).ToList();
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Please select at least one assembly before refreshing.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		RefreshSheetsRequest request = new RefreshSheetsRequest
		{
			AssemblyIds = list.Select((AssemblyRow x) => x.AssemblyId).ToList(),
			ProductKind = ProductKind
		};
		try
		{
			RevitRequestBridge.RaiseRefreshSheets(request);
		}
		catch (Exception ex)
		{
			SsSavantMessageBox.Show("Failed to start assembly refresh.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void BtnCreateSpoolMap_Click(object sender, RoutedEventArgs e)
	{
		if (ProductKind != SpoolingManagerKind.Standard)
		{
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		List<CreateSpoolMapPackageOption> packageOptions = _packageGroups
			.Where(g => g?.Assemblies != null && g.Assemblies.Count > 0)
			.Select(g =>
			{
				string packageValue = g.Assemblies
					.Select(x => x.SPackage)
					.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
				return new CreateSpoolMapPackageOption
				{
					Label = string.IsNullOrEmpty(packageValue) ? "(No package)" : packageValue,
					PackageValue = packageValue,
					AssemblyIds = g.Assemblies.Select(x => x.AssemblyId).Distinct().ToList()
				};
			})
			.ToList();
		if (packageOptions.Count == 0)
		{
			SsSavantMessageBox.Show("No packages were found in the assembly list. Refresh the list, then try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		SpoolingManagerSettings settings = SpoolingManagerSettings.Load(ProductKind);
		CreateSpoolMapDialogWindow dialog = new CreateSpoolMapDialogWindow(
			_uiapp.ActiveUIDocument.Document,
			packageOptions,
			settings,
			ProductKind);
		Window owner = Window.GetWindow(this);
		if (owner != null)
		{
			dialog.Owner = owner;
		}
		if (dialog.ShowDialog() != true || dialog.SelectedRequest == null)
		{
			return;
		}
		CreateSpoolMapRequest request = dialog.SelectedRequest;
		Document activeDoc = _uiapp.ActiveUIDocument.Document;
		if (CreateSpoolMapHandler.TryDescribeExistingSpoolMap(
			activeDoc,
			request.PackageLabel,
			request.PackageValue,
			out string existingDescription))
		{
			System.Windows.MessageBoxResult overwrite = SsSavantMessageBox.Show(
				"Spool Map already exists for package '" + (request.PackageLabel ?? string.Empty) + "'.\n\n" +
				existingDescription + "\n\nOverwrite the existing Spool Map sheet and views?",
				"Spool Map Already Exists",
				MessageBoxButton.YesNo,
				MessageBoxImage.Question,
				System.Windows.MessageBoxResult.No);
			if (overwrite != System.Windows.MessageBoxResult.Yes)
			{
				return;
			}
			request.OverwriteExisting = true;
		}
		try
		{
			if ((int)RevitRequestBridge.RaiseCreateSpoolMap(request) == 2)
			{
				SsSavantMessageBox.Show("Revit did not queue Create Spool Map—another modal dialog may be open, or the session is busy. Close other dialogs and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
		}
		catch (Exception ex)
		{
			SsSavantMessageBox.Show("Failed to start Create Spool Map.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void BtnExportToBoardroom_Click(object sender, RoutedEventArgs e)
	{
		if (ProductKind != SpoolingManagerKind.Standard)
		{
			return;
		}

		List<AssemblyRow> list = (from x in _allRows
			where x.IsSelected
			orderby x.SpoolName
			select x).ToList();
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Select assemblies whose packages you want to export (grouped by S-Package).", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}

		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}

		SpoolingManagerSettings settings = SpoolingManagerSettings.Load(ProductKind);
		string apiBaseUrl = BoardroomApiClient.NormalizeBaseUrl(settings.BoardroomApiBaseUrl);
		IReadOnlyList<BoardroomProjectOption> projects;
		using (BoardroomApiClient api = new BoardroomApiClient(apiBaseUrl))
		{
			if (!api.TryHealth(out string healthMessage))
			{
				SsSavantMessageBox.Show(
					healthMessage + "\n\nDefault API: " + BoardroomApiClient.DefaultBaseUrl + "\nConfirm Settings → Boardroom if you changed the URL.",
					ToolDisplayName,
					MessageBoxButton.OK,
					MessageBoxImage.Exclamation);
				return;
			}

			try
			{
				projects = api.GetProjects(includeTemplates: false);
			}
			catch (Exception ex)
			{
				SsSavantMessageBox.Show(
					"Could not load Boardroom projects.\n\n" + ex.Message,
					ToolDisplayName,
					MessageBoxButton.OK,
					MessageBoxImage.Exclamation);
				return;
			}

			if (projects.Count == 0)
			{
				SsSavantMessageBox.Show(
					"Boardroom API connected, but returned no non-template projects.\n\n" +
					"In BIM Boardroom, confirm Demo Mechanical (or another client) has projects, then try again.\n\n" +
					"API: " + apiBaseUrl,
					ToolDisplayName,
					MessageBoxButton.OK,
					MessageBoxImage.Exclamation);
				return;
			}

			string defaultFolder = Path.Combine(
				@"C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)\Boardroom",
				"Exports");

			ExportToBoardroomWindow exportWindow = new ExportToBoardroomWindow(api, projects, defaultFolder);
			Window owner = Window.GetWindow(this);
			if (owner != null)
			{
				exportWindow.Owner = owner;
			}

			if (exportWindow.ShowDialog() != true
				|| exportWindow.SelectedProject == null
				|| exportWindow.SelectedTask == null
				|| string.IsNullOrWhiteSpace(exportWindow.OutputFolder))
			{
				return;
			}

			BoardroomProjectOption project = exportWindow.SelectedProject;
			BoardroomTaskOption task = exportWindow.SelectedTask;
			List<PlotPackageBatch> batches = (from g in list.GroupBy((AssemblyRow r) => (!string.IsNullOrWhiteSpace(r.SPackage)) ? r.SPackage.Trim() : string.Empty, StringComparer.OrdinalIgnoreCase)
				select new PlotPackageBatch
				{
					PackageLabel = (string.IsNullOrEmpty(g.Key) ? "(No package)" : g.Key),
					AssemblyIds = g.Select((AssemblyRow x) => x.AssemblyId).Distinct().ToList()
				}).ToList();

			var reportOptions = new PlotPackagesReportOptions
			{
				IncludeSpoolsCombined = true,
				IncludeAssemblyList = true,
				IncludeBillOfMaterials = true,
				IncludeCutList = true,
				IncludeWeldLog = true,
				IncludeTigerStop = true,
				IncludePcfFiles = true,
				ProjectName = string.IsNullOrWhiteSpace(project.ProjectName) ? project.DisplayLabel : project.ProjectName,
				CreatedBy = Environment.UserName ?? string.Empty,
				DateText = DateTime.Now.ToString("M/d/yyyy h:mm:ss tt")
			};

			PlotPackagesRequest request = new PlotPackagesRequest
			{
				OutputFolder = exportWindow.OutputFolder,
				Batches = batches,
				ProductKind = ProductKind,
				ReportOptions = reportOptions,
				ExportToBoardroom = true,
				BoardroomProjectId = project.ProjectId,
				BoardroomProjectName = project.ProjectName,
				BoardroomClientName = project.ClientName,
				BoardroomJobCode = project.JobCode,
				BoardroomTaskId = task.Id,
				BoardroomTaskNumber = task.TaskNumber ?? string.Empty,
				BoardroomTaskTitle = task.Title ?? string.Empty
			};

			try
			{
				if ((int)RevitRequestBridge.RaisePlotPackages(request) == 2)
				{
					SsSavantMessageBox.Show("Revit did not queue Boardroom export—another modal dialog may be open, or the session is busy. Close other dialogs and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
				}
			}
			catch (Exception ex)
			{
				SsSavantMessageBox.Show("Failed to start Boardroom export.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private void BtnPlotPackages_Click(object sender, RoutedEventArgs e)
	{
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01af: Invalid comparison between Unknown and I4
		if (ProductKind != SpoolingManagerKind.Standard)
		{
			return;
		}
		List<AssemblyRow> list = (from x in _allRows
			where x.IsSelected
			orderby x.SpoolName
			select x).ToList();
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Select assemblies whose packages you want to plot (grouped by S-Package). Choose report types next, then a folder for the PDFs.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		List<PlotPackageBatch> batches = (from g in list.GroupBy((AssemblyRow r) => (!string.IsNullOrWhiteSpace(r.SPackage)) ? r.SPackage.Trim() : string.Empty, StringComparer.OrdinalIgnoreCase)
			select new PlotPackageBatch
			{
				PackageLabel = (string.IsNullOrEmpty(g.Key) ? "(No package)" : g.Key),
				AssemblyIds = g.Select((AssemblyRow x) => x.AssemblyId).Distinct().ToList()
			}).ToList();
		string defaultProject = string.Empty;
		try
		{
			Document activeDoc = _uiapp.ActiveUIDocument.Document;
			if (activeDoc?.ProjectInformation != null && !string.IsNullOrWhiteSpace(activeDoc.ProjectInformation.Name))
			{
				defaultProject = activeDoc.ProjectInformation.Name.Trim();
			}
			else if (!string.IsNullOrWhiteSpace(activeDoc?.Title))
			{
				defaultProject = activeDoc.Title.Trim();
			}
		}
		catch
		{
		}
		PlotPackagesReportPickerWindow plotPackagesReportPickerWindow = new PlotPackagesReportPickerWindow(
			defaultProject,
			Environment.UserName,
			DateTime.Now.ToString("M/d/yyyy h:mm:ss tt"));
		Window window = Window.GetWindow(this);
		if (window != null)
		{
			plotPackagesReportPickerWindow.Owner = window;
		}
		if (plotPackagesReportPickerWindow.ShowDialog() != true || plotPackagesReportPickerWindow.SelectedOptions == null)
		{
			return;
		}
		PlotPackagesReportOptions selectedOptions = plotPackagesReportPickerWindow.SelectedOptions;
		string outputFolder;
		using (FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog())
		{
			folderBrowserDialog.Description = "Select folder for PDF files (only the reports you chose; same file names overwrite).";
			if (folderBrowserDialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(folderBrowserDialog.SelectedPath))
			{
				return;
			}
			outputFolder = folderBrowserDialog.SelectedPath.Trim();
		}
		PlotPackagesRequest request = new PlotPackagesRequest
		{
			OutputFolder = outputFolder,
			Batches = batches,
			ProductKind = ProductKind,
			ReportOptions = selectedOptions
		};
		try
		{
			if ((int)RevitRequestBridge.RaisePlotPackages(request) == 2)
			{
				SsSavantMessageBox.Show("Revit did not queue Plot Packages—another modal dialog may be open, or the session is busy. Close other dialogs and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
		}
		catch (Exception ex)
		{
			SsSavantMessageBox.Show("Failed to start plot.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void BtnAddToPackage_Click(object sender, RoutedEventArgs e)
	{
		List<AssemblyRow> list = (from x in _allRows
			where x.IsSelected
			orderby x.SpoolName
			select x).ToList();
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Please select at least one assembly before adding to a package.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string text = ((txtPackageNumber != null) ? (txtPackageNumber.Text ?? string.Empty).Trim() : string.Empty);
		if (text.Length == 0)
		{
			SsSavantMessageBox.Show("Enter a package value for S-Package (for example a package number).", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		ApplyAssemblyPackageRequest applyAssemblyPackageRequest = new ApplyAssemblyPackageRequest
		{
			AssemblyIds = list.Select((AssemblyRow x) => x.AssemblyId).ToList(),
			PackageValue = text,
			ProductKind = ProductKind
		};
		_pendingApplyReloadHint = CloneApplyReloadHint(applyAssemblyPackageRequest);
		_deselectRowsAfterPackageApply = true;
		try
		{
			RevitRequestBridge.RaiseApplyAssemblyPackage(applyAssemblyPackageRequest);
			if (txtPackageNumber != null)
			{
				txtPackageNumber.Text = string.Empty;
			}
		}
		catch (Exception ex)
		{
			_deselectRowsAfterPackageApply = false;
			SsSavantMessageBox.Show("Failed to apply S-Package.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void BtnRemoveFromPackage_Click(object sender, RoutedEventArgs e)
	{
		List<AssemblyRow> list = (from x in _allRows
			where x.IsSelected
			orderby x.SpoolName
			select x).ToList();
		if (list.Count == 0)
		{
			SsSavantMessageBox.Show("Please select at least one assembly before removing from a package.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (SsSavantMessageBox.Show($"Clear S-Package on {list.Count} selected assembly instance(s)? They will no longer appear under that package until reassigned.", ToolDisplayName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
		{
			ApplyAssemblyPackageRequest applyAssemblyPackageRequest = new ApplyAssemblyPackageRequest
			{
				AssemblyIds = list.Select((AssemblyRow x) => x.AssemblyId).ToList(),
				ClearPackage = true,
				ProductKind = ProductKind
			};
			_pendingApplyReloadHint = CloneApplyReloadHint(applyAssemblyPackageRequest);
			try
			{
				RevitRequestBridge.RaiseApplyAssemblyPackage(applyAssemblyPackageRequest);
			}
			catch (Exception ex)
			{
				SsSavantMessageBox.Show("Failed to clear S-Package.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private List<string> GetExistingSpoolSheetDescriptions(Document doc, IEnumerable<ElementId> assemblyIds)
	{
		HashSet<ElementId> hashSet = new HashSet<ElementId>(assemblyIds ?? Enumerable.Empty<ElementId>());
		bool regularSheetBranch = UsesRegularSheetBranchForActiveProduct();
		List<string> list = new List<string>();
		Dictionary<ElementId, ViewSheet> dictionary = CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(doc, regularSheetBranch, hashSet.ToList());
		foreach (ElementId item in hashSet)
		{
			if (!dictionary.TryGetValue(item, out var value))
			{
				continue;
			}
			Element element = doc.GetElement(item);
			AssemblyInstance val = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			string obj = ((val != null) ? AssemblyDisplayName.Get(val) : string.Empty);
			string text = ((((Element)value).Name != null) ? ((Element)value).Name.Trim() : string.Empty);
			string text2 = ((value.SheetNumber != null) ? value.SheetNumber.Trim() : string.Empty);
			string text3 = obj;
			if (string.IsNullOrWhiteSpace(text3))
			{
				text3 = text2;
			}
			if (string.IsNullOrWhiteSpace(text3))
			{
				text3 = text;
			}
			string text4 = text;
			if (string.IsNullOrWhiteSpace(text4))
			{
				text4 = text2;
			}
			if (!string.IsNullOrWhiteSpace(text3) || !string.IsNullOrWhiteSpace(text4))
			{
				if (string.IsNullOrWhiteSpace(text4))
				{
					list.Add(text3);
				}
				else
				{
					list.Add(text3 + " -" + text4);
				}
			}
		}
		return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private void OnApplyAssemblyPackageCompleted()
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			ApplyAssemblyPackageRequest pendingApplyReloadHint = _pendingApplyReloadHint;
			bool deselectAfterApply = _deselectRowsAfterPackageApply;
			_pendingApplyReloadHint = null;
			_deselectRowsAfterPackageApply = false;
			if (pendingApplyReloadHint != null && TryRefreshRowsAfterPackageApply(pendingApplyReloadHint))
			{
				if (deselectAfterApply)
				{
					HashSet<ElementId> applied = new HashSet<ElementId>(pendingApplyReloadHint.AssemblyIds);
					foreach (AssemblyRow allRow in _allRows)
					{
						if (applied.Contains(allRow.AssemblyId))
						{
							allRow.IsSelected = false;
						}
					}
				}
				ApplyFilter();
				UpdateSelectAllState();
			}
			else
			{
				RefreshAssemblies();
			}
		});
	}

	private void ApplyInlinePackageRename(PackageAssemblyGroup group, string newPackageValue)
	{
		if (group == null)
		{
			return;
		}
		string text = (newPackageValue ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			group.RevertDisplayTitleToCommitted();
			return;
		}
		UIApplication uiapp = _uiapp;
		if (((uiapp != null) ? uiapp.ActiveUIDocument : null) == null)
		{
			SsSavantMessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			group.RevertDisplayTitleToCommitted();
			return;
		}
		List<ElementId> list = group.Assemblies.Select((AssemblyRow x) => x.AssemblyId).Distinct().ToList();
		if (list.Count == 0)
		{
			group.RevertDisplayTitleToCommitted();
			return;
		}
		ApplyAssemblyPackageRequest applyAssemblyPackageRequest = new ApplyAssemblyPackageRequest
		{
			AssemblyIds = list,
			PackageValue = text,
			ProductKind = ProductKind,
			SuppressCompletionDialog = true
		};
		_pendingApplyReloadHint = CloneApplyReloadHint(applyAssemblyPackageRequest);
		try
		{
			RevitRequestBridge.RaiseApplyAssemblyPackage(applyAssemblyPackageRequest);
		}
		catch (Exception ex)
		{
			SsSavantMessageBox.Show("Failed to apply S-Package.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
			group.RevertDisplayTitleToCommitted();
		}
	}

	public static bool GetPackageTitleEditor(DependencyObject obj)
	{
		return (bool)obj.GetValue(PackageTitleEditorProperty);
	}

	public static void SetPackageTitleEditor(DependencyObject obj, bool value)
	{
		obj.SetValue(PackageTitleEditorProperty, value);
	}

	private static void OnPackageTitleEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is System.Windows.Controls.TextBox textBox)
		{
			object oldValue = e.OldValue;
			bool flag = default(bool);
			int num;
			if (oldValue is bool)
			{
				flag = (bool)oldValue;
				num = 1;
			}
			else
			{
				num = 0;
			}
			if (((uint)num & (flag ? 1u : 0u)) != 0)
			{
				textBox.LostFocus -= PackageTitleTextBox_LostFocus;
				textBox.KeyDown -= PackageTitleTextBox_KeyDown;
			}
			oldValue = e.NewValue;
			bool flag2 = default(bool);
			int num2;
			if (oldValue is bool)
			{
				flag2 = (bool)oldValue;
				num2 = 1;
			}
			else
			{
				num2 = 0;
			}
			if (((uint)num2 & (flag2 ? 1u : 0u)) != 0)
			{
				textBox.LostFocus += PackageTitleTextBox_LostFocus;
				textBox.KeyDown += PackageTitleTextBox_KeyDown;
			}
		}
	}

	private static void PackageTitleTextBox_LostFocus(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox { DataContext: PackageAssemblyGroup dataContext } textBox && !dataContext.ConsumeRenameLostFocusSuppression())
		{
			dataContext.CommitRenameFromEditor(textBox.Text);
		}
	}

	private static void PackageTitleTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox { DataContext: PackageAssemblyGroup dataContext } textBox)
		{
			if (e.Key == Key.Escape)
			{
				dataContext.RevertDisplayTitleToCommitted();
				e.Handled = true;
			}
			else if (e.Key == Key.Return)
			{
				dataContext.BeginRenameCommitFromEnterKey();
				dataContext.CommitRenameFromEditor(textBox.Text);
				Keyboard.ClearFocus();
				e.Handled = true;
			}
		}
	}

	private void OnRenameSheetsCompleted()
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			RefreshAssemblies();
		});
	}

	public void InitializeComponent()
	{
		System.Windows.Controls.UserControl source = SpoolingManagerXamlLoader.LoadUserControl("SpoolingManager.Views.SpoolingManagerPane.xaml");
		SpoolingManagerXamlLoader.ApplyUserControl(this, source);
		SpoolingManagerXamlLoader.ApplyNamedStyle(this, "btnPaneSettings", "VgSquareButton");
		txtPaneTitleMode = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtPaneTitleMode");
		txtPaneTitleMain = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtPaneTitleMain");
		brdNeonSign = SpoolingManagerXamlLoader.Find<Border>(this, "brdNeonSign");
		brdPaneShell = SpoolingManagerXamlLoader.Find<Border>(this, "brdPaneShell");
		txtSearch = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtSearch");
		cmbSpoolContentMode = SpoolingManagerXamlLoader.Find<System.Windows.Controls.ComboBox>(this, "cmbSpoolContentMode");
		if (cmbSpoolContentMode != null)
		{
			cmbSpoolContentMode.SelectionChanged += CmbSpoolContentMode_SelectionChanged;
		}
		chkSelectAll = SpoolingManagerXamlLoader.Find<System.Windows.Controls.CheckBox>(this, "chkSelectAll");
		treeAssemblies = SpoolingManagerXamlLoader.Find<ItemsControl>(this, "treeAssemblies");
		scrollAssemblyTree = SpoolingManagerXamlLoader.Find<ScrollViewer>(this, "scrollAssemblyTree");
		txtPackageNumber = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtPackageNumber");
		btnPlotPackages = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnPlotPackages");
		btnPlotPackages.Click += BtnPlotPackages_Click;
		btnExportToBoardroom = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnExportToBoardroom");
		if (btnExportToBoardroom != null)
		{
			btnExportToBoardroom.Click += BtnExportToBoardroom_Click;
		}
		btnCreateSpoolMap = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnCreateSpoolMap");
		btnCreateSpoolMap.Click += BtnCreateSpoolMap_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnPaneSettings").Click += BtnSettings_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnRefreshList").Click += BtnRefresh_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnClear").Click += BtnClear_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnIsolate").Click += BtnIsolate_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnHide").Click += BtnHide_Click;
		btnCreateSpoolSheets = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnCreateSpoolSheets");
		btnCreateSpoolSheets.Click += BtnCreateSpoolSheets_Click;
		btnOpenSheets = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnOpenSheets");
		btnOpenSheets.Click += BtnOpenSheets_Click;
		btnRenameSheets = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnRenameSheets");
		btnRenameSheets.Click += BtnRenameSheets_Click;
		btnRefreshSheets = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnRefreshSheets");
		btnRefreshSheets.Click += BtnRefreshSheets_Click;
		btnAddToPackage = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnAddToPackage");
		btnAddToPackage.Click += BtnAddToPackage_Click;
		btnRemoveFromPackage = SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnRemoveFromPackage");
		btnRemoveFromPackage.Click += BtnRemoveFromPackage_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnCollapsePackages").Click += BtnCollapsePackages_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnExpandPackages").Click += BtnExpandPackages_Click;
		chkSelectAll.Click += ChkSelectAll_Click;
		txtSearch.TextChanged += TxtSearch_TextChanged;
		treeAssemblies.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(AssemblyList_PreviewMouseLeftButtonDown));
		UpdateActionButtonsEnabledState();
	}
}
