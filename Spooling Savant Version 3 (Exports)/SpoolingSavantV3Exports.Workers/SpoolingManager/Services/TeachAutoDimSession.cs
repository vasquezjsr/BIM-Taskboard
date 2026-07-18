using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public static class TeachAutoDimSession
{
	public static event Action<TeachAutoDimReport> ReportReady;

	public static bool IsActive { get; private set; }

	public static TeachAutoDimWindow Window { get; private set; }

	private static UIApplication _uiapp;
	private static bool _refreshPending;

	public static void Open(UIApplication uiapp)
	{
		if (uiapp == null)
		{
			return;
		}
		_uiapp = uiapp;
		IsActive = true;
		if (Window == null || !Window.IsLoaded)
		{
			Window = new TeachAutoDimWindow();
			Window.Closed += OnWindowClosed;
			Window.Show();
		}
		else
		{
			Window.Activate();
		}
		uiapp.Idling -= OnIdling;
		uiapp.Idling += OnIdling;
		RequestRefresh();
	}

	public static void Close()
	{
		IsActive = false;
		try
		{
			if (_uiapp != null)
			{
				_uiapp.Idling -= OnIdling;
			}
		}
		catch
		{
		}
		try
		{
			Window?.Close();
		}
		catch
		{
		}
		Window = null;
	}

	public static void RequestRefresh()
	{
		if (!IsActive)
		{
			return;
		}
		_refreshPending = true;
		RevitRequestBridge.RaiseTeachAutoDim(new TeachAutoDimRequest
		{
			Action = TeachAutoDimAction.Refresh
		});
	}

	public static void RequestFinish(
		System.Collections.Generic.List<long> contentCorrectIds,
		System.Collections.Generic.List<long> contentIncorrectIds,
		System.Collections.Generic.List<long> placementCorrectIds,
		System.Collections.Generic.List<long> placementIncorrectIds,
		System.Collections.Generic.Dictionary<string, string> reasons)
	{
		if (!IsActive)
		{
			return;
		}
		_refreshPending = true;
		RevitRequestBridge.RaiseTeachAutoDim(new TeachAutoDimRequest
		{
			Action = TeachAutoDimAction.Finish,
			ContentCorrectIds = contentCorrectIds ?? new System.Collections.Generic.List<long>(),
			ContentIncorrectIds = contentIncorrectIds ?? new System.Collections.Generic.List<long>(),
			PlacementCorrectIds = placementCorrectIds ?? new System.Collections.Generic.List<long>(),
			PlacementIncorrectIds = placementIncorrectIds ?? new System.Collections.Generic.List<long>(),
			IncorrectReasonsByDimId = reasons ?? new System.Collections.Generic.Dictionary<string, string>()
		});
	}

	public static void PublishReport(TeachAutoDimReport report)
	{
		_refreshPending = false;
		ReportReady?.Invoke(report);
	}

	private static void OnWindowClosed(object sender, EventArgs e)
	{
		IsActive = false;
		try
		{
			if (_uiapp != null)
			{
				_uiapp.Idling -= OnIdling;
			}
		}
		catch
		{
		}
		if (Window != null)
		{
			Window.Closed -= OnWindowClosed;
		}
		Window = null;
	}

	private static void OnIdling(object sender, IdlingEventArgs e)
	{
		if (!IsActive || _refreshPending)
		{
			return;
		}
	}
}
