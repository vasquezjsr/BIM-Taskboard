using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using RevitView = Autodesk.Revit.DB.View;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public static class RevitRequestBridge
{
	private class PendingTagAllRequest
	{
		public ElementId ViewId { get; set; }

		public List<ElementId> ElementIds { get; set; } = new List<ElementId>();

		public bool CommandPosted { get; set; }

		public int IdleCyclesAfterPost { get; set; }

		public bool DialogHandled { get; set; }

		public bool DialogWatcherStarted { get; set; }

		public bool ViewActivated { get; set; }

		public int TagsBefore { get; set; }

		public int TagsAfter { get; set; }

		public bool CompletionReported { get; set; }
	}

	private static PendingTagAllRequest _pendingTagAllRequest;

	private const string TagAllDialogTitle = "Tag All Not Tagged";

	public static ExternalEvent CreateSheetsExternalEvent { get; private set; }

	public static CreateSpoolSheetsHandler CreateSheetsHandler { get; private set; }

	public static ExternalEvent RefreshSheetsExternalEvent { get; private set; }

	public static RefreshSheetsHandler RefreshSheetsHandler { get; private set; }

	public static ExternalEvent RenameSheetsExternalEvent { get; private set; }

	public static RenameSheetsHandler RenameSheetsHandler { get; private set; }

	public static ExternalEvent ApplyAssemblyPackageExternalEvent { get; private set; }

	public static ApplyAssemblyPackageHandler ApplyAssemblyPackageHandler { get; private set; }

	public static ExternalEvent PlotPackagesExternalEvent { get; private set; }

	public static PlotPackagesHandler PlotPackagesHandler { get; private set; }

	public static ExternalEvent CreateSpoolMapExternalEvent { get; private set; }

	public static CreateSpoolMapHandler CreateSpoolMapHandler { get; private set; }

	public static ExternalEvent AssemblyTemporaryVisibilityExternalEvent { get; private set; }

	public static AssemblyTemporaryVisibilityHandler AssemblyTemporaryVisibilityHandler { get; private set; }

	public static ExternalEvent OpenSpoolSheetsExternalEvent { get; private set; }

	public static OpenSpoolSheetsHandler OpenSpoolSheetsHandler { get; private set; }

	public static event Action RenameSheetsCompleted;

	public static event Action ApplyAssemblyPackageCompleted;

	private static long _handlersWorkerFileTicks;

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	public static void Initialize()
	{
		long workerTicks = GetWorkerAssemblyFileTicks();
		if (CreateSheetsExternalEvent != null && _handlersWorkerFileTicks == workerTicks)
			return;

		CreateSheetsHandler = new CreateSpoolSheetsHandler();
		CreateSheetsExternalEvent = ExternalEvent.Create((IExternalEventHandler)(object)CreateSheetsHandler);
		RefreshSheetsHandler = new RefreshSheetsHandler();
		RefreshSheetsExternalEvent = ExternalEvent.Create((IExternalEventHandler)(object)RefreshSheetsHandler);
		RenameSheetsHandler = new RenameSheetsHandler();
		RenameSheetsExternalEvent = ExternalEvent.Create((IExternalEventHandler)(object)RenameSheetsHandler);
		ApplyAssemblyPackageHandler = new ApplyAssemblyPackageHandler();
		ApplyAssemblyPackageExternalEvent = ExternalEvent.Create((IExternalEventHandler)(object)ApplyAssemblyPackageHandler);
		PlotPackagesHandler = new PlotPackagesHandler();
		PlotPackagesExternalEvent = ExternalEvent.Create((IExternalEventHandler)(object)PlotPackagesHandler);
		CreateSpoolMapHandler = new CreateSpoolMapHandler();
		CreateSpoolMapExternalEvent = ExternalEvent.Create((IExternalEventHandler)(object)CreateSpoolMapHandler);
		AssemblyTemporaryVisibilityHandler = new AssemblyTemporaryVisibilityHandler();
		AssemblyTemporaryVisibilityExternalEvent = ExternalEvent.Create((IExternalEventHandler)(object)AssemblyTemporaryVisibilityHandler);
		OpenSpoolSheetsHandler = new OpenSpoolSheetsHandler();
		OpenSpoolSheetsExternalEvent = ExternalEvent.Create((IExternalEventHandler)(object)OpenSpoolSheetsHandler);
		AssemblyMemberChangeBootstrap.RefreshHandler();
		_handlersWorkerFileTicks = workerTicks;
	}

	private static long GetWorkerAssemblyFileTicks()
	{
		try
		{
			string dir = SsSavantWorkerAssemblyPaths.ResolveWorkersDllDirectory(typeof(RevitRequestBridge).Assembly);
			if (string.IsNullOrWhiteSpace(dir))
				return 0;

			string dll = Path.Combine(dir, "SpoolingSavantV3Exports.Workers.dll");
			if (!File.Exists(dll))
				return 0;

			return File.GetLastWriteTimeUtc(dll).Ticks;
		}
		catch
		{
			return 0;
		}
	}

	public static void RaiseCreateSheets(CreateSpoolSheetsRequest request)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		Initialize();
		CreateSheetsHandler.PendingRequest = request;
		CreateSheetsExternalEvent.Raise();
	}

	public static void RaiseRenameSheets(RenameSheetsRequest request)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		Initialize();
		RenameSheetsHandler.PendingRequest = request;
		RenameSheetsExternalEvent.Raise();
	}

	public static void RaiseRefreshSheets(RefreshSheetsRequest request)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		Initialize();
		RefreshSheetsHandler.PendingRequest = request;
		RefreshSheetsExternalEvent.Raise();
	}

	public static void RaiseApplyAssemblyPackage(ApplyAssemblyPackageRequest request)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		Initialize();
		ApplyAssemblyPackageHandler.PendingRequest = request;
		ApplyAssemblyPackageExternalEvent.Raise();
	}

	public static ExternalEventRequest RaiseCreateSpoolMap(CreateSpoolMapRequest request)
	{
		Initialize();
		CreateSpoolMapHandler.PendingRequest = request;
		ExternalEventRequest result = CreateSpoolMapExternalEvent.Raise();
		if ((int)result == 2)
		{
			CreateSpoolMapHandler.PendingRequest = null;
		}
		return result;
	}

	public static ExternalEventRequest RaisePlotPackages(PlotPackagesRequest request)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Invalid comparison between Unknown and I4
		Initialize();
		PlotPackagesHandler.PendingRequest = request;
		ExternalEventRequest val = PlotPackagesExternalEvent.Raise();
		if ((int)val == 2)
		{
			PlotPackagesHandler.PendingRequest = null;
		}
		return val;
	}

	/// <summary>Removed — Teach Auto-Dim / Dim Inspector UI retired. No-op for any leftover callers.</summary>
	public static void RaiseDimensionInspector(DimensionInspectorRequest request)
	{
	}

	/// <summary>Removed — Teach Auto-Dim / Dim Inspector UI retired. No-op for any leftover callers.</summary>
	public static void RaiseTeachAutoDim(TeachAutoDimRequest request)
	{
	}

	public static ExternalEventRequest RaiseAssemblyTemporaryVisibility(AssemblyTemporaryVisibilityRequest request)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Invalid comparison between Unknown and I4
		Initialize();
		AssemblyTemporaryVisibilityHandler.PendingRequest = request;
		ExternalEventRequest val = AssemblyTemporaryVisibilityExternalEvent.Raise();
		if ((int)val == 2)
		{
			AssemblyTemporaryVisibilityHandler.PendingRequest = null;
		}
		return val;
	}

	public static ExternalEventRequest RaiseOpenSpoolSheets(IReadOnlyList<ElementId> sheetIds)
	{
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Invalid comparison between Unknown and I4
		Initialize();
		if (sheetIds != null && sheetIds.Count != 0)
		{
			List<ElementId> list = new List<ElementId>();
			foreach (ElementId sheetId in sheetIds)
			{
				if (sheetId != (ElementId)null && sheetId != ElementId.InvalidElementId)
				{
					list.Add(sheetId);
				}
			}
			if (list.Count != 0)
			{
				OpenSpoolSheetsHandler.PendingSheetIds = list;
				ExternalEventRequest val = OpenSpoolSheetsExternalEvent.Raise();
				if ((int)val == 2)
				{
					OpenSpoolSheetsHandler.PendingSheetIds = null;
				}
				return val;
			}
			return (ExternalEventRequest)0;
		}
		return (ExternalEventRequest)0;
	}

	internal static void ScheduleOpenSpoolSheetsContinue(UIApplication app)
	{
		Initialize();
		if (app == null || OpenSpoolSheetsExternalEvent == null || OpenSpoolSheetsHandler == null)
		{
			if (OpenSpoolSheetsHandler != null)
			{
				OpenSpoolSheetsHandler.PendingSheetIds = null;
			}
			return;
		}
		EventHandler<IdlingEventArgs> once = null;
		once = delegate
		{
			//IL_0016: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Invalid comparison between Unknown and I4
			app.Idling -= once;
			if ((int)OpenSpoolSheetsExternalEvent.Raise() == 2 && OpenSpoolSheetsHandler != null)
			{
				OpenSpoolSheetsHandler.PendingSheetIds = null;
			}
		};
		app.Idling += once;
	}

	public static void NotifyRenameSheetsCompleted()
	{
		RevitRequestBridge.RenameSheetsCompleted?.Invoke();
	}

	public static void NotifyApplyAssemblyPackageCompleted()
	{
		RevitRequestBridge.ApplyAssemblyPackageCompleted?.Invoke();
	}

	/// <summary>
	/// Shows an operation summary in SS Manager chrome (same look as the progress window).
	/// Uses modeless <see cref="Window.Show"/> — never <c>ShowDialog</c> from an external-event
	/// handler, which wedges Revit's main thread after close.
	/// </summary>
	public static void ShowOperationSummary(string title, string message)
	{
		string safeTitle = (string.IsNullOrWhiteSpace(title) ? "Spooling Savant" : title);
		string safeMessage = message ?? string.Empty;
		try
		{
			OperationProgressSession.ShowCompleted(safeTitle, safeMessage);
		}
		catch
		{
			try
			{
				TaskDialog taskDialog = new TaskDialog(safeTitle)
				{
					TitleAutoPrefix = false,
					MainContent = safeMessage,
					CommonButtons = TaskDialogCommonButtons.Close,
					DefaultButton = TaskDialogResult.Close
				};
				taskDialog.Show();
			}
			catch
			{
				try
				{
					TaskDialog.Show(safeTitle, safeMessage);
				}
				catch
				{
				}
			}
		}
	}

	public static void QueueTagAllNotTagged(UIApplication app, RevitView view, ICollection<ElementId> elementIds)
	{
		if (app == null || view == null || elementIds == null || elementIds.Count == 0)
		{
			return;
		}
		PendingTagAllRequest obj = new PendingTagAllRequest
		{
			ViewId = ((Element)view).Id,
			ElementIds = elementIds.Distinct().ToList()
		};
		UIDocument activeUIDocument = app.ActiveUIDocument;
		obj.TagsBefore = CountFabricationTags((activeUIDocument != null) ? activeUIDocument.Document : null, ((Element)view).Id);
		_pendingTagAllRequest = obj;
		app.Idling -= OnAppIdling;
		app.Idling += OnAppIdling;
		app.ViewActivated -= OnViewActivated;
		app.ViewActivated += OnViewActivated;
		try
		{
			app.ActiveUIDocument.RequestViewChange(view);
		}
		catch
		{
		}
	}

	private static void OnAppIdling(object sender, IdlingEventArgs e)
	{
		UIApplication val = (UIApplication)((sender is UIApplication) ? sender : null);
		if (val == null || _pendingTagAllRequest == null)
		{
			return;
		}
		UIDocument activeUIDocument = val.ActiveUIDocument;
		if (activeUIDocument == null)
		{
			ClearPending(val);
			return;
		}
		Element element = activeUIDocument.Document.GetElement(_pendingTagAllRequest.ViewId);
		RevitView val2 = (RevitView)(object)((element is RevitView) ? element : null);
		if (val2 == null)
		{
			ClearPending(val);
			return;
		}
		if (!_pendingTagAllRequest.ViewActivated)
		{
			try
			{
				activeUIDocument.RequestViewChange(val2);
				return;
			}
			catch
			{
				ClearPending(val);
				return;
			}
		}
		if (_pendingTagAllRequest.CommandPosted)
		{
			_pendingTagAllRequest.IdleCyclesAfterPost++;
			if (_pendingTagAllRequest.DialogHandled)
			{
				ReportCompletion(val);
				ClearPending(val);
			}
			else if (_pendingTagAllRequest.IdleCyclesAfterPost > 200)
			{
				ReportCompletion(val);
				ClearPending(val);
			}
		}
	}

	private static void ClearPending(UIApplication app)
	{
		_pendingTagAllRequest = null;
		if (app != null)
		{
			app.Idling -= OnAppIdling;
			app.ViewActivated -= OnViewActivated;
		}
	}

	private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
	{
		UIApplication val = (UIApplication)((sender is UIApplication) ? sender : null);
		if (val == null || _pendingTagAllRequest == null || _pendingTagAllRequest.CommandPosted || e.CurrentActiveView == null || ((Element)e.CurrentActiveView).Id != _pendingTagAllRequest.ViewId)
		{
			return;
		}
		_pendingTagAllRequest.ViewActivated = true;
		UIDocument activeUIDocument = val.ActiveUIDocument;
		if (activeUIDocument == null)
		{
			ClearPending(val);
			return;
		}
		try
		{
			TryLock3DView(activeUIDocument.Document, _pendingTagAllRequest.ViewId);
			activeUIDocument.Selection.SetElementIds((ICollection<ElementId>)_pendingTagAllRequest.ElementIds);
			RevitCommandId val2 = RevitCommandId.LookupPostableCommandId((PostableCommand)33735);
			if (val2 == null || !val.CanPostCommand(val2))
			{
				ClearPending(val);
				return;
			}
			val.PostCommand(val2);
			_pendingTagAllRequest.CommandPosted = true;
			_pendingTagAllRequest.IdleCyclesAfterPost = 0;
			StartDialogWatcher();
		}
		catch
		{
			ClearPending(val);
		}
	}

	private static void ReportCompletion(UIApplication app)
	{
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		if (_pendingTagAllRequest == null || _pendingTagAllRequest.CompletionReported)
		{
			return;
		}
		_pendingTagAllRequest.CompletionReported = true;
		object obj;
		if (app == null)
		{
			obj = null;
		}
		else
		{
			UIDocument activeUIDocument = app.ActiveUIDocument;
			obj = ((activeUIDocument != null) ? activeUIDocument.Document : null);
		}
		Document doc = (Document)obj;
		_pendingTagAllRequest.TagsAfter = CountFabricationTags(doc, _pendingTagAllRequest.ViewId);
		try
		{
			Views.SsSavantMessageBox.Show($"View activated: {_pendingTagAllRequest.ViewActivated}\n" + $"Command posted: {_pendingTagAllRequest.CommandPosted}\n" + $"Dialog handled: {_pendingTagAllRequest.DialogHandled}\n" + $"Selected elements: {_pendingTagAllRequest.ElementIds.Count}\n" + $"Fabrication tags before: {_pendingTagAllRequest.TagsBefore}\n" + $"Fabrication tags after: {_pendingTagAllRequest.TagsAfter}", "TAG ALL DEBUG");
		}
		catch
		{
		}
	}

	private static int CountFabricationTags(Document doc, ElementId viewId)
	{
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		if (doc == null || viewId == (ElementId)null || viewId == ElementId.InvalidElementId)
		{
			return 0;
		}
		try
		{
			return new FilteredElementCollector(doc, viewId).WhereElementIsNotElementType().OfCategory((BuiltInCategory)(-2008209)).GetElementCount();
		}
		catch
		{
			return 0;
		}
	}

	private static void TryLock3DView(Document doc, ElementId viewId)
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Expected O, but got Unknown
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		if (doc == null || viewId == (ElementId)null || viewId == ElementId.InvalidElementId)
		{
			return;
		}
		Element element = doc.GetElement(viewId);
		View3D val = (View3D)(object)((element is View3D) ? element : null);
		if (val == null)
		{
			return;
		}
		try
		{
			Transaction val2 = new Transaction(doc, "Spooling Savant: Lock 3D View After Tagging");
			try
			{
				val2.Start();
				val.SaveOrientationAndLock();
				val2.Commit();
			}
			finally
			{
				((IDisposable)val2)?.Dispose();
			}
		}
		catch
		{
		}
	}

	private static void StartDialogWatcher()
	{
		if (_pendingTagAllRequest == null || _pendingTagAllRequest.DialogWatcherStarted)
		{
			return;
		}
		_pendingTagAllRequest.DialogWatcherStarted = true;
		ThreadPool.QueueUserWorkItem(delegate
		{
			for (int i = 0; i < 80; i++)
			{
				PendingTagAllRequest pendingTagAllRequest = _pendingTagAllRequest;
				if (pendingTagAllRequest == null || pendingTagAllRequest.DialogHandled)
				{
					break;
				}
				IntPtr intPtr = FindWindow(null, "Tag All Not Tagged");
				if (intPtr != IntPtr.Zero && TryConfirmTagAllDialog(intPtr))
				{
					pendingTagAllRequest.DialogHandled = true;
					break;
				}
				Thread.Sleep(250);
			}
		});
	}

	private static bool TryConfirmTagAllDialog(IntPtr dialogHandle)
	{
		try
		{
			SetForegroundWindow(dialogHandle);
			Thread.Sleep(150);
			AutomationElement automationElement = AutomationElement.FromHandle(dialogHandle);
			if (automationElement != null)
			{
				SelectRadioButton(automationElement, "All objects in current view");
				ToggleCategoryRow(automationElement, "MEP Fabrication Pipework", ToggleState.On);
				ToggleCheckBox(automationElement, "Leader", ToggleState.On);
				AutomationElement automationElement2 = FindByName(automationElement, "OK");
				if (automationElement2 != null && automationElement2.Current.IsEnabled && automationElement2.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern invokePattern)
				{
					invokePattern.Invoke();
					Thread.Sleep(300);
					return FindWindow(null, "Tag All Not Tagged") == IntPtr.Zero;
				}
			}
			System.Windows.Forms.SendKeys.SendWait("{ENTER}");
			Thread.Sleep(300);
			return FindWindow(null, "Tag All Not Tagged") == IntPtr.Zero;
		}
		catch
		{
			return false;
		}
	}

	private static void SelectRadioButton(AutomationElement root, string name)
	{
		AutomationElement automationElement = FindByName(root, name);
		if (!(automationElement == null) && automationElement.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern selectionItemPattern)
		{
			selectionItemPattern.Select();
		}
	}

	private static void ToggleCategoryRow(AutomationElement root, string categoryText, ToggleState desiredState)
	{
		AutomationElement automationElement = root.FindFirst(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.NameProperty, categoryText, PropertyConditionFlags.IgnoreCase), new PropertyCondition(AutomationElement.NameProperty, "MEP Fabrication Pipework Ta"), new PropertyCondition(AutomationElement.NameProperty, "MEP Fabrication Pipework Tags")));
		if (automationElement == null)
		{
			return;
		}
		AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(automationElement);
		while (parent != null && parent.Current.ControlType != ControlType.DataItem && parent.Current.ControlType != ControlType.Custom)
		{
			parent = TreeWalker.ControlViewWalker.GetParent(parent);
		}
		if (!(parent == null))
		{
			AutomationElement automationElement2 = parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
			if (!(automationElement2 == null) && automationElement2.GetCurrentPattern(TogglePattern.Pattern) is TogglePattern { Current: var current } togglePattern && current.ToggleState != desiredState)
			{
				togglePattern.Toggle();
			}
		}
	}

	private static void ToggleCheckBox(AutomationElement root, string name, ToggleState desiredState)
	{
		AutomationElement automationElement = FindByName(root, name);
		if (!(automationElement == null) && automationElement.GetCurrentPattern(TogglePattern.Pattern) is TogglePattern { Current: var current } togglePattern && current.ToggleState != desiredState)
		{
			togglePattern.Toggle();
		}
	}

	private static AutomationElement FindByName(AutomationElement root, string name)
	{
		return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
	}
}
