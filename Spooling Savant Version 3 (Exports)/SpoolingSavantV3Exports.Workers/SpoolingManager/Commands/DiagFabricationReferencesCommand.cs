using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class DiagFabricationReferencesCommand : IExternalCommand
{
	private const string ToolTitle = "Fabrication Ref Diag";
	private const string DefaultAssemblyName = "CHW-16";

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIApplication application = commandData.Application;
		UIDocument uidoc = application?.ActiveUIDocument;
		Document doc = uidoc?.Document;
		if (doc == null)
		{
			message = "No active Revit document.";
			TaskDialog.Show(ToolTitle, message);
			return Result.Failed;
		}

		View view = doc.ActiveView;
		if (view == null)
		{
			message = "No active view.";
			TaskDialog.Show(ToolTitle, message);
			return Result.Failed;
		}

		try
		{
			string reportPath = FabricationReferenceDiagnosticService.Run(doc, view, DefaultAssemblyName);
			TaskDialog.Show(ToolTitle,
				"Reference diagnostic complete.\n\n"
				+ "Active view: " + view.Name + "\n"
				+ "Assembly: " + DefaultAssemblyName + " (or view-associated assembly)\n\n"
				+ "Report written to:\n" + reportPath);
			return Result.Succeeded;
		}
		catch (Exception ex)
		{
			message = ex.Message;
			TaskDialog.Show(ToolTitle, ex.ToString());
			return Result.Failed;
		}
	}
}
