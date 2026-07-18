using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public sealed class DimensionInspectorHandler : IExternalEventHandler
{
	public DimensionInspectorRequest PendingRequest { get; set; }

	public string GetName()
	{
		return "Dimension Inspector";
	}

	public void Execute(UIApplication app)
	{
		DimensionInspectorRequest request = PendingRequest;
		PendingRequest = null;
		if (request == null)
		{
			return;
		}
		switch (request.Action)
		{
		case DimensionInspectorAction.Open:
			DimensionInspectorSession.Open(app);
			break;
		case DimensionInspectorAction.Close:
			DimensionInspectorSession.Close();
			break;
		case DimensionInspectorAction.Refresh:
		{
			UIDocument uidoc = app?.ActiveUIDocument;
			DimensionInspectorReport report;
			try
			{
				report = uidoc != null
					? DimensionInspectorService.BuildReport(uidoc, request.ExportViewImage)
					: new DimensionInspectorReport { Success = false, StatusMessage = "No active document.", DetailText = "No active document." };
			}
			catch (System.Exception ex)
			{
				report = new DimensionInspectorReport
				{
					Success = false,
					StatusMessage = "Inspector read failed (safe — Revit kept running).",
					DetailText = "Exception during Dim Inspector refresh:\n" + ex.Message
				};
			}
			DimensionInspectorSession.PublishReport(report);
			break;
		}
		}
	}
}
