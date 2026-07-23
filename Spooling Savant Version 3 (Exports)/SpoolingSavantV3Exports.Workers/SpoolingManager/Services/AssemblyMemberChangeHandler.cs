using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public sealed class AssemblyMemberChangeHandler : IExternalEventHandler
{
	public void Execute(UIApplication app)
	{
		if (app?.Application == null)
			return;

		InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);

		List<AssemblyMemberChangeCoordinator.PendingAssemblySync> batch =
			AssemblyMemberChangeCoordinator.TakePendingSyncs();
		if (batch.Count == 0)
			return;

		AssemblyMemberChangeCoordinator.SetProcessing(true);
		try
		{
			foreach (IGrouping<Document, AssemblyMemberChangeCoordinator.PendingAssemblySync> documentGroup in
			         batch.GroupBy(item => item.Document))
			{
				Document doc = documentGroup.Key;
				if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument)
					continue;

				HashSet<ElementId> assemblyIds = new HashSet<ElementId>();
				foreach (AssemblyMemberChangeCoordinator.PendingAssemblySync item in documentGroup)
					assemblyIds.Add(item.AssemblyId);

				Transaction transaction = new Transaction(doc, AssemblyMemberSyncService.SyncTransactionName);
				try
				{
					transaction.Start();
					foreach (ElementId assemblyId in assemblyIds)
					{
						Element element = doc.GetElement(assemblyId);
						AssemblyInstance assembly = element as AssemblyInstance;
						if (assembly == null)
							continue;

						AssemblyMemberSyncService.SyncAfterMemberChange(app.Application, doc, assembly);
					}

					transaction.Commit();
				}
				catch
				{
					if (transaction.HasStarted() && !transaction.HasEnded())
						transaction.RollBack();
				}
				finally
				{
					transaction.Dispose();
				}
			}
		}
		finally
		{
			AssemblyMemberChangeCoordinator.SetProcessing(false);
		}
	}

	public string GetName() => "Spooling Savant: Sync assembly member";
}
