using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public sealed class TeachAutoDimHandler : IExternalEventHandler
{
	public TeachAutoDimRequest PendingRequest { get; set; }

	public void Execute(UIApplication app)
	{
		TeachAutoDimRequest request = PendingRequest;
		PendingRequest = null;
		if (request == null)
		{
			return;
		}
		switch (request.Action)
		{
		case TeachAutoDimAction.Open:
			TeachAutoDimSession.Open(app);
			break;
		case TeachAutoDimAction.Close:
			TeachAutoDimSession.Close();
			break;
		case TeachAutoDimAction.Refresh:
		{
			UIDocument uidoc = app?.ActiveUIDocument;
			TeachAutoDimReport report = uidoc != null
				? TeachAutoDimService.BuildReport(uidoc)
				: new TeachAutoDimReport { Success = false, StatusMessage = "No active document." };
			TeachAutoDimSession.PublishReport(report);
			break;
		}
		case TeachAutoDimAction.Finish:
		{
			UIDocument uidoc = app?.ActiveUIDocument;
			TeachAutoDimReport report = uidoc != null
				? TeachAutoDimService.FinishTeach(
					uidoc,
					request.ContentCorrectIds,
					request.ContentIncorrectIds,
					request.PlacementCorrectIds,
					request.PlacementIncorrectIds,
					request.IncorrectReasonsByDimId)
				: new TeachAutoDimReport { Success = false, StatusMessage = "No active document." };
			TeachAutoDimSession.PublishReport(report);
			break;
		}
		}
	}

	public string GetName() => "Teach Auto-Dim";
}
