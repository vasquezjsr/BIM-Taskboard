using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public class AssemblyTemporaryVisibilityHandler : IExternalEventHandler
{
	private sealed class ElementIdValueComparer : IEqualityComparer<ElementId>
	{
		public bool Equals(ElementId x, ElementId y)
		{
			if (x == y)
			{
				return true;
			}
			if (x == (ElementId)null || y == (ElementId)null)
			{
				return false;
			}
			return x.Value == y.Value;
		}

		public int GetHashCode(ElementId obj)
		{
			if (obj == null)
			{
				return 0;
			}
			return obj.Value.GetHashCode();
		}
	}

	public AssemblyTemporaryVisibilityRequest PendingRequest { get; set; }

	public void Execute(UIApplication app)
	{
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Expected O, but got Unknown
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		if (((app != null) ? app.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}
		AssemblyTemporaryVisibilityRequest pendingRequest = PendingRequest;
		PendingRequest = null;
		if (pendingRequest == null || pendingRequest.MemberElementIds == null || pendingRequest.MemberElementIds.Count == 0)
		{
			return;
		}
		UIDocument val = ((app != null) ? app.ActiveUIDocument : null);
		Document val2 = ((val != null) ? val.Document : null);
		if (val2 == null)
		{
			TaskDialog.Show(GetToolTitle(), "No active document.");
			return;
		}
		View activeView = val2.ActiveView;
		if (activeView == null)
		{
			TaskDialog.Show(GetToolTitle(), "No active view.");
			return;
		}
		if (!activeView.CanUseTemporaryVisibilityModes())
		{
			TaskDialog.Show(GetToolTitle(), "The active view does not support temporary hide/isolate.");
			return;
		}
		List<ElementId> list = pendingRequest.MemberElementIds.Where((ElementId id) => id != (ElementId)null && id != ElementId.InvalidElementId).Distinct(new ElementIdValueComparer()).ToList();
		if (list.Count == 0)
		{
			return;
		}
		string text = ((pendingRequest.Action == AssemblyTemporaryVisibilityAction.IsolateMembers) ? "Spooling Savant V3 (Exports): Temporary isolate" : "Spooling Savant V3 (Exports): Temporary hide");
		try
		{
			Transaction val3 = new Transaction(val2, text);
			try
			{
				val3.Start();
				if (activeView.IsInTemporaryViewMode((TemporaryViewMode)2))
				{
					activeView.DisableTemporaryViewMode((TemporaryViewMode)2);
				}
				if (pendingRequest.Action == AssemblyTemporaryVisibilityAction.IsolateMembers)
				{
					activeView.IsolateElementsTemporary((ICollection<ElementId>)list);
				}
				else
				{
					activeView.HideElementsTemporary((ICollection<ElementId>)list);
				}
				val3.Commit();
			}
			finally
			{
				((IDisposable)val3)?.Dispose();
			}
			val.Selection.SetElementIds((ICollection<ElementId>)list);
		}
		catch (Exception ex)
		{
			TaskDialog.Show(GetToolTitle(), "Visibility change failed.\n\n" + ex.Message);
		}
	}

	private static string GetToolTitle()
	{
		return "SS Manager V3";
	}

	public string GetName()
	{
		return "SS Manager: isolate / hide assembly members";
	}
}
