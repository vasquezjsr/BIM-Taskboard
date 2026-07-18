using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public sealed class OpenSpoolSheetsHandler : IExternalEventHandler
{
	public List<ElementId> PendingSheetIds { get; set; }

	public void Execute(UIApplication app)
	{
		if (((app != null) ? app.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}
		if (PendingSheetIds == null || PendingSheetIds.Count == 0)
		{
			return;
		}
		UIDocument val = ((app != null) ? app.ActiveUIDocument : null);
		Document val2 = ((val != null) ? val.Document : null);
		if (val2 == null)
		{
			PendingSheetIds = null;
			return;
		}
		ElementId val3 = PendingSheetIds[0];
		PendingSheetIds.RemoveAt(0);
		Element element = val2.GetElement(val3);
		ViewSheet val4 = (ViewSheet)(object)((element is ViewSheet) ? element : null);
		if (val4 != null)
		{
			try
			{
				val.RequestViewChange((View)(object)val4);
			}
			catch
			{
			}
		}
		if (PendingSheetIds.Count > 0)
		{
			RevitRequestBridge.ScheduleOpenSpoolSheetsContinue(app);
		}
		else
		{
			PendingSheetIds = null;
		}
	}

	public string GetName()
	{
		return "SS Manager: open spool sheets";
	}
}
