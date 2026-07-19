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

	private ApplyAssemblyPackageRequest _pendingApplyReloadHint;

	private UIApplication _uiapp;

	private AssemblyRow _assemblyShiftAnchorRow;

	public static readonly DependencyProperty PackageTitleEditorProperty = DependencyProperty.RegisterAttached("PackageTitleEditor", typeof(bool), typeof(SpoolingManagerPane), new PropertyMetadata(false, OnPackageTitleEditorChanged));

	internal Image imgLogo;

	internal TextBlock txtLogoPlaceholder;

	internal System.Windows.Controls.TextBox txtSearch;

	internal System.Windows.Controls.CheckBox chkSelectAll;

	internal ItemsControl treeAssemblies;

	internal ScrollViewer scrollAssemblyTree;

	internal System.Windows.Controls.TextBox txtPackageNumber;

	internal System.Windows.Controls.Button btnPlotPackages;

	internal System.Windows.Controls.Button btnExportToBoardroom;

	internal System.Windows.Controls.Button btnCreateSpoolMap;

	public SpoolingManagerKind ProductKind { get; set; }

	private string ToolDisplayName => ProductKind switch
	{
		SpoolingManagerKind.Mmc => "MMC SS Manager", 
		SpoolingManagerKind.MmcTesting => "MMC SS Manager (Testing)", 
		SpoolingManagerKind.AutoDimensionLab => "SS Manager (Auto Dim) — Testing", 
		_ => "SS Manager V3", 
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
	}

	public void LoadAssemblies(UIApplication uiapp)
	{
		_uiapp = uiapp;
		TryShowWorkersHotloadBanner();
		ApplyPlotPackagesButtonVisibility();
		if (((uiapp != null) ? uiapp.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(uiapp.Application.VersionNumber);
		}
		LoadSavedLogo();
		ApplyUiAppearance();
		RefreshAssemblies();
	}

	/// <summary>
	/// Banner lives in Workers (not the locked host DLL) so every SS Manager ribbon
	/// reload shows which build is actually running.
	/// </summary>
	private static void TryShowWorkersHotloadBanner()
	{
		try
		{
			Assembly asm = typeof(SpoolingManagerPane).Assembly;
			string tag = CreateSpoolSheetsHandler.DiagnosticBuildTag;
			string ver = asm.GetName().Version?.ToString() ?? "?";
			string loc = asm.Location ?? "(no location)";
			string lwt = File.Exists(loc) ? File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss") : "n/a";
			// Always show — proves which Workers build the ribbon actually loaded.
			TaskDialog.Show(
				"SS Manager V3",
				"Workers loaded:\n"
				+ tag + "\n"
				+ "Version: " + ver + "\n"
				+ "LastWriteTime: " + lwt + "\n\n"
				+ loc);
		}
		catch (Exception ex)
		{
			try
			{
				TaskDialog.Show("SS Manager V3", "Workers banner failed:\n" + ex.Message);
			}
			catch
			{
			}
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
			return;
		}
		UIDocument activeUIDocument = _uiapp.ActiveUIDocument;
		Document doc = activeUIDocument.Document;
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
		HashSet<ElementId> hashSet = CreateSpoolSheetsHandler.GetAssemblyInstanceIdsHavingSpoolSheet(regularSheetBranch: UsesRegularSheetBranchForActiveProduct(), doc: doc, displayedAssemblyInstanceIds: visibleAssemblyIdsInActiveView);
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
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		UIDocument activeUIDocument = _uiapp.ActiveUIDocument;
		Document document = activeUIDocument.Document;
		List<AssemblyRow> selectedRows = _allRows.Where((AssemblyRow x) => x.IsSelected).ToList();
		List<ElementId> list = CollectTemporaryVisibilityTargets(document, activeUIDocument, selectedRows);
		if (list.Count == 0)
		{
			System.Windows.MessageBox.Show("Check one or more assemblies in the list and/or select fabrication pipework or model assemblies in Revit, then try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		RevitRequestBridge.Initialize();
		if ((int)RevitRequestBridge.RaiseAssemblyTemporaryVisibility(new AssemblyTemporaryVisibilityRequest
		{
			Action = action,
			MemberElementIds = list
		}) == 2)
		{
			System.Windows.MessageBox.Show("Revit did not run the request (another dialog may be open or the session is busy). Close other dialogs and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
		try
		{
			AssemblySettingsWindow assemblySettingsWindow = new AssemblySettingsWindow(_uiapp, ProductKind);
			Window window = Window.GetWindow(this);
			if (window != null)
			{
				assemblySettingsWindow.Owner = window;
			}
			if (assemblySettingsWindow.ShowDialog() == true)
			{
				LoadSavedLogo();
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("Could not open Settings.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void LoadSavedLogo()
	{
		imgLogo.Source = null;
		txtLogoPlaceholder.Visibility = Visibility.Visible;
		SpoolingManagerSettings settings = SpoolingManagerSettings.Load(ProductKind);
		string logoPath = settings.LogoImagePath ?? string.Empty;
		BitmapSource bitmapSource = AssemblySettingsWindow.DecodeLogoBitmap(logoPath);
		if (bitmapSource == null)
		{
			bitmapSource = AssemblySettingsWindow.DecodeLogoBitmap(ResolveBundledLogoPath());
		}
		if (bitmapSource != null)
		{
			imgLogo.Source = bitmapSource;
			txtLogoPlaceholder.Visibility = Visibility.Collapsed;
		}
	}

	private static string ResolveBundledLogoPath()
	{
		try
		{
			string deployed = Path.Combine(InstallLayout.GetAddinsRoot(), "Spooling-Savant-V3-Exports", "Icons", "SpoolingSavantLogo.png");
			if (File.Exists(deployed))
			{
				return deployed;
			}
		}
		catch
		{
		}

		return string.Empty;
	}

	private void BtnCreateSpoolSheets_Click(object sender, RoutedEventArgs e)
	{
		List<AssemblyRow> list = (from x in _allRows
			where x.IsSelected
			orderby x.SpoolName
			select x).ToList();
		if (list.Count == 0)
		{
			System.Windows.MessageBox.Show("Please select at least one assembly before creating spool sheets.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (string.IsNullOrWhiteSpace(SpoolingManagerSettings.Load(ProductKind).TitleBlockName))
		{
			System.Windows.MessageBox.Show("Please select a Title Block in Settings first.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
			OperationProgressSession.Show(ToolDisplayName, hostWindow, hostHwnd);
			OperationProgressSession.Report(1.0, "Queued…", list.Count + " spool(s) selected");
			RevitRequestBridge.RaiseCreateSheets(createSpoolSheetsRequest);
		}
		catch (Exception ex)
		{
			OperationProgressSession.Close();
			System.Windows.MessageBox.Show("Failed to start sheet creation.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void BtnOpenSheets_Click(object sender, RoutedEventArgs e)
	{
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Invalid comparison between Unknown and I4
		List<AssemblyRow> list = _allRows.Where((AssemblyRow x) => x.IsSelected).ToList();
		if (list.Count == 0)
		{
			System.Windows.MessageBox.Show("Please select at least one assembly before opening sheets.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
			System.Windows.MessageBox.Show("No spool sheets were found for the selected rows.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if ((int)RevitRequestBridge.RaiseOpenSpoolSheets(list2.ConvertAll((ViewSheet s) => ((Element)s).Id)) == 2)
		{
			System.Windows.MessageBox.Show("Revit could not open sheets (for example, another dialog may be blocking the session). Close it and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
			System.Windows.MessageBox.Show("Please select at least one assembly before renaming sheets.", "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			System.Windows.MessageBox.Show("No active Revit document was found.", "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
			System.Windows.MessageBox.Show("Failed to start sheet rename.\n\n" + ex.Message, "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Hand);
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
			System.Windows.MessageBox.Show("Please select at least one assembly before refreshing.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
			System.Windows.MessageBox.Show("Failed to start assembly refresh.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
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
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
			System.Windows.MessageBox.Show("No packages were found in the assembly list. Refresh the list, then try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
			System.Windows.MessageBoxResult overwrite = System.Windows.MessageBox.Show(
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
				System.Windows.MessageBox.Show("Revit did not queue Create Spool Map—another modal dialog may be open, or the session is busy. Close other dialogs and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("Failed to start Create Spool Map.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
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
			System.Windows.MessageBox.Show("Select assemblies whose packages you want to plot (grouped by S-Package). Choose report types next, then a folder for the PDFs.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
				System.Windows.MessageBox.Show("Revit did not queue Plot Packages—another modal dialog may be open, or the session is busy. Close other dialogs and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("Failed to start plot.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
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
			System.Windows.MessageBox.Show("Select assemblies whose packages you want to export (grouped by S-Package).", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}

		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}

		SpoolingManagerSettings settings = SpoolingManagerSettings.Load(ProductKind);
		string apiBaseUrl = BoardroomApiClient.NormalizeBaseUrl(settings.BoardroomApiBaseUrl);
		IReadOnlyList<BoardroomProjectOption> projects;
		using (BoardroomApiClient api = new BoardroomApiClient(apiBaseUrl))
		{
			if (!api.TryHealth(out string healthMessage))
			{
				System.Windows.MessageBox.Show(
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
				System.Windows.MessageBox.Show(
					"Could not load Boardroom projects.\n\n" + ex.Message,
					ToolDisplayName,
					MessageBoxButton.OK,
					MessageBoxImage.Exclamation);
				return;
			}

			if (projects.Count == 0)
			{
				System.Windows.MessageBox.Show(
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
					System.Windows.MessageBox.Show("Revit did not queue Boardroom export—another modal dialog may be open, or the session is busy. Close other dialogs and try again.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
				}
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show("Failed to start Boardroom export.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
			}
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
			System.Windows.MessageBox.Show("Please select at least one assembly before adding to a package.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string text = ((txtPackageNumber != null) ? (txtPackageNumber.Text ?? string.Empty).Trim() : string.Empty);
		if (text.Length == 0)
		{
			System.Windows.MessageBox.Show("Enter a package value for S-Package (for example a package number).", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		ApplyAssemblyPackageRequest applyAssemblyPackageRequest = new ApplyAssemblyPackageRequest
		{
			AssemblyIds = list.Select((AssemblyRow x) => x.AssemblyId).ToList(),
			PackageValue = text,
			ProductKind = ProductKind
		};
		_pendingApplyReloadHint = CloneApplyReloadHint(applyAssemblyPackageRequest);
		try
		{
			RevitRequestBridge.RaiseApplyAssemblyPackage(applyAssemblyPackageRequest);
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("Failed to apply S-Package.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
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
			System.Windows.MessageBox.Show("Please select at least one assembly before removing from a package.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (_uiapp == null || _uiapp.ActiveUIDocument == null)
		{
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (System.Windows.MessageBox.Show($"Clear S-Package on {list.Count} selected assembly instance(s)? They will no longer appear under that package until reassigned.", ToolDisplayName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
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
				System.Windows.MessageBox.Show("Failed to clear S-Package.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
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
			_pendingApplyReloadHint = null;
			if (pendingApplyReloadHint != null && TryRefreshRowsAfterPackageApply(pendingApplyReloadHint))
			{
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
			System.Windows.MessageBox.Show("No active Revit document was found.", ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
			System.Windows.MessageBox.Show("Failed to apply S-Package.\n\n" + ex.Message, ToolDisplayName, MessageBoxButton.OK, MessageBoxImage.Hand);
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
		imgLogo = SpoolingManagerXamlLoader.Find<Image>(this, "imgLogo");
		txtLogoPlaceholder = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtLogoPlaceholder");
		txtSearch = SpoolingManagerXamlLoader.Find<System.Windows.Controls.TextBox>(this, "txtSearch");
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
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Create Spool Sheets").Click += BtnCreateSpoolSheets_Click;
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Open Sheets").Click += BtnOpenSheets_Click;
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Rename Sheets").Click += BtnRenameSheets_Click;
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Refresh Assembly").Click += BtnRefreshSheets_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnAddToPackage").Click += BtnAddToPackage_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnRemoveFromPackage").Click += BtnRemoveFromPackage_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnCollapsePackages").Click += BtnCollapsePackages_Click;
		SpoolingManagerXamlLoader.Find<System.Windows.Controls.Button>(this, "btnExpandPackages").Click += BtnExpandPackages_Click;
		chkSelectAll.Click += ChkSelectAll_Click;
		txtSearch.TextChanged += TxtSearch_TextChanged;
		treeAssemblies.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(AssemblyList_PreviewMouseLeftButtonDown));
	}
}
