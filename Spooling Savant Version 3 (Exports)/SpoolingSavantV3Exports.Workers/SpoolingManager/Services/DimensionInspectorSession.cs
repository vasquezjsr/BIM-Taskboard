using System;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

internal static class DimensionInspectorSession
{
	private static UIApplication _uiapp;
	private static string _lastFingerprint;
	private static DateTime _lastRefreshUtc = DateTime.MinValue;
	private static bool _refreshPending;

	public static DimensionInspectorWindow Window { get; private set; }

	public static bool IsActive { get; private set; }

	public static event Action<DimensionInspectorReport> ReportReady;

	public static void Open(UIApplication uiapp)
	{
		if (uiapp == null)
		{
			return;
		}
		_uiapp = uiapp;
		IsActive = true;
		_lastFingerprint = null;
		if (Window == null || !Window.IsLoaded)
		{
			Window = new DimensionInspectorWindow();
			Window.Closed += OnWindowClosed;
			Window.Show();
		}
		else
		{
			Window.Activate();
		}
		uiapp.Idling -= OnIdling;
		uiapp.Idling += OnIdling;
		// Never export images on open/idle — ExportImage during dim edit has fatal-crashed Revit.
		RequestRefresh(exportViewImage: false);
	}

	public static void Close()
	{
		IsActive = false;
		_refreshPending = false;
		if (_uiapp != null)
		{
			_uiapp.Idling -= OnIdling;
		}
		if (Window != null)
		{
			try
			{
				Window.Close();
			}
			catch
			{
			}
			Window = null;
		}
	}

	public static void RequestRefresh(bool exportViewImage)
	{
		if (!IsActive)
		{
			return;
		}
		_refreshPending = true;
		RevitRequestBridge.RaiseDimensionInspector(new DimensionInspectorRequest
		{
			Action = DimensionInspectorAction.Refresh,
			ExportViewImage = exportViewImage
		});
	}

	public static void PublishReport(DimensionInspectorReport report)
	{
		_refreshPending = false;
		ReportReady?.Invoke(report);
	}

	private static void OnWindowClosed(object sender, EventArgs e)
	{
		Close();
	}

	private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
	{
		if (!IsActive || _uiapp?.ActiveUIDocument == null || _refreshPending)
		{
			return;
		}
		if ((DateTime.UtcNow - _lastRefreshUtc).TotalMilliseconds < 450)
		{
			return;
		}
		string fingerprint;
		try
		{
			fingerprint = CreateSpoolSheetsHandler.ComputeDimensionInspectorFingerprint(_uiapp.ActiveUIDocument);
		}
		catch
		{
			return;
		}
		bool changed = !string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal);
		if (!changed)
		{
			return;
		}
		_lastFingerprint = fingerprint;
		_lastRefreshUtc = DateTime.UtcNow;
		// Text-only live refresh. Image export is manual via Refresh Now only.
		RequestRefresh(exportViewImage: false);
	}
}
