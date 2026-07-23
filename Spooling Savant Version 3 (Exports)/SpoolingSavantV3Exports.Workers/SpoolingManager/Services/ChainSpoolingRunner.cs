using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Commands;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// After the first spool, pick the next part down the line; gather free parts on the
/// path back to the last assembly and create the next spool (numeric names advance).
/// Esc ends the session.
/// </summary>
internal static class ChainSpoolingRunner
{
	internal static Result Run(
		UIApplication application,
		UIDocument uidoc,
		Document doc,
		ElementId seedAssemblyId,
		CreateAssemblyCommand.CreateSpoolSession session,
		string toolTitle,
		ref string message)
	{
		if (application == null || uidoc == null || doc == null
			|| seedAssemblyId == null || seedAssemblyId == ElementId.InvalidElementId
			|| session == null)
		{
			return Result.Cancelled;
		}

		ElementId lastAssemblyId = seedAssemblyId;
		int createdCount = 0;

		while (true)
		{
			try
			{
				uidoc.Selection.SetElementIds(new List<ElementId>());
			}
			catch
			{
			}

			Reference pick;
			try
			{
				pick = uidoc.Selection.PickObject(
					ObjectType.Element,
					new TraceSpoolEndpointSelectionFilter(),
					"Chain Spooling: click the next part down the line (Esc to stop).");
			}
			catch (Autodesk.Revit.Exceptions.OperationCanceledException)
			{
				break;
			}

			if (pick?.ElementId == null || pick.ElementId == ElementId.InvalidElementId)
			{
				continue;
			}

			List<ElementId> members;
			try
			{
				members = SpoolTracePathGatherer
					.GatherMembersBetweenPickAndAssembly(doc, pick.ElementId, lastAssemblyId)
					.ToList();
			}
			catch (Exception ex)
			{
				SsSavantMessageBox.Show(ex.Message, toolTitle);
				continue;
			}

			if (members.Count == 0)
			{
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
				toolTitle,
				ref message,
				session,
				promptDialog: false);

			if (createResult != Result.Succeeded)
			{
				break;
			}

			createdCount++;
			if (session.LastCreatedAssemblyId != null
				&& session.LastCreatedAssemblyId != ElementId.InvalidElementId)
			{
				lastAssemblyId = session.LastCreatedAssemblyId;
			}
		}

		return createdCount > 0 ? Result.Succeeded : Result.Cancelled;
	}
}
