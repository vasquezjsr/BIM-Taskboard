using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Commands;

/// <summary>
/// Trace Spool — pick fabrication endpoints, gather paths between them, create the spool,
/// then keep picking for the next spool (numeric names advance) until Finish with nothing or Esc.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class TraceSpoolCommand : IExternalCommand
{
	internal const string ToolTitle = "Trace Spool";

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIApplication application = commandData.Application;
		if (application?.Application != null)
		{
			InstallLayout.ApplyRevitVersionNumber(application.Application.VersionNumber);
		}

		UIDocument uidoc = application?.ActiveUIDocument;
		Document doc = uidoc?.Document;
		if (doc == null)
		{
			SsSavantMessageBox.Show("No active Revit document.", ToolTitle);
			return Result.Cancelled;
		}

		CreateAssemblyCommand.CreateSpoolSession session = new CreateAssemblyCommand.CreateSpoolSession();
		int createdCount = 0;
		bool promptDialog = true;

		while (true)
		{
			try
			{
				uidoc.Selection.SetElementIds(new List<ElementId>());
			}
			catch
			{
			}

			string status = createdCount == 0
				? "Trace Spool: pick endpoints (flange, olet, fitting…). Finish to create — Esc or Finish with none to stop."
				: "Trace Spool #" + (createdCount + 1) + ": pick next endpoints. Finish to create — Esc or Finish with none to stop.";

			IList<Reference> picks;
			try
			{
				picks = uidoc.Selection.PickObjects(
					ObjectType.Element,
					new TraceSpoolEndpointSelectionFilter(),
					status);
			}
			catch (Autodesk.Revit.Exceptions.OperationCanceledException)
			{
				// Esc / Cancel
				break;
			}

			if (picks == null || picks.Count == 0)
			{
				// Finish with nothing selected
				break;
			}

			List<ElementId> endpoints = picks
				.Select((Reference r) => r?.ElementId)
				.Where((ElementId id) => id != null && id != ElementId.InvalidElementId)
				.Distinct()
				.ToList();

			if (endpoints.Count == 0)
			{
				SsSavantMessageBox.Show("No valid fabrication endpoints were picked.", ToolTitle);
				continue;
			}

			List<ElementId> members;
			try
			{
				members = SpoolTracePathGatherer.GatherMembersBetweenEndpoints(doc, endpoints).ToList();
			}
			catch (Exception ex)
			{
				SsSavantMessageBox.Show(ex.Message, ToolTitle);
				message = ex.Message;
				continue;
			}

			if (members.Count == 0)
			{
				SsSavantMessageBox.Show("Nothing could be gathered from those endpoints.", ToolTitle);
				continue;
			}

			try
			{
				uidoc.Selection.SetElementIds(members);
			}
			catch
			{
			}

			Result createResult = CreateAssemblyCommand.CreateSpoolFromMemberIds(
				application,
				uidoc,
				doc,
				members,
				ToolTitle,
				ref message,
				session,
				promptDialog);

			if (createResult != Result.Succeeded)
			{
				// Dialog cancel or create failure — stop the continuous session.
				break;
			}

			createdCount++;

			// Chain Spooling (click down the line) takes over after the first dialog create.
			if (session.ChainSpooling)
			{
				break;
			}

			promptDialog = false;
		}

		return createdCount > 0 ? Result.Succeeded : Result.Cancelled;
	}
}
