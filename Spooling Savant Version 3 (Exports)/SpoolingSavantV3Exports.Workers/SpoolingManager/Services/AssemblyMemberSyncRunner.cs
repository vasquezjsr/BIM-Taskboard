using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public static class AssemblyMemberSyncRunner
{
	public const string SyncTransactionName = AssemblyMemberSyncService.SyncTransactionName;

	private static string LogPath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
			"Autodesk",
			"Revit",
			"Addins",
			"2024",
			"Spooling-Savant-V3-Exports",
			"SpoolingManager",
			"TestingReports",
			"AssemblyMemberSync.log");

	public static void SyncAssemblies(Application app, Document doc, ICollection<ElementId> assemblyIds)
	{
		if (app == null || doc == null || assemblyIds == null || assemblyIds.Count == 0)
			return;

		HashSet<ElementId> uniqueAssemblyIds = new HashSet<ElementId>();
		foreach (ElementId assemblyId in assemblyIds)
		{
			if (assemblyId != null && assemblyId != ElementId.InvalidElementId)
				uniqueAssemblyIds.Add(assemblyId);
		}

		if (uniqueAssemblyIds.Count == 0)
			return;

		WriteLog($"SyncAssemblies start doc={doc.Title} assemblies={uniqueAssemblyIds.Count}");

		Transaction transaction = new Transaction(doc, SyncTransactionName);
		try
		{
			transaction.Start();
			foreach (ElementId assemblyId in uniqueAssemblyIds)
			{
				Element element = doc.GetElement(assemblyId);
				AssemblyInstance assembly = element as AssemblyInstance;
				if (assembly == null)
					continue;

				WriteLog($"  syncing assembly id={assemblyId.Value}");
				AssemblyMemberSyncService.SyncAfterMemberChange(app, doc, assembly);
			}

			transaction.Commit();
			WriteLog("SyncAssemblies committed");
		}
		catch (System.Exception ex)
		{
			WriteLog("SyncAssemblies failed: " + ex.Message);
			if (transaction.HasStarted() && !transaction.HasEnded())
				transaction.RollBack();
		}
		finally
		{
			transaction.Dispose();
		}
	}

	public static void SyncForElements(Application app, Document doc, ICollection<ElementId> elementIds)
	{
		if (app == null || doc == null || elementIds == null || elementIds.Count == 0)
			return;

		HashSet<ElementId> assemblyIds = AssemblyMemberAssemblyResolver.ResolveAssemblyIds(doc, elementIds);
		if (assemblyIds.Count == 0)
		{
			WriteLog($"SyncForElements: no assembly resolved for {elementIds.Count} element(s) in {doc.Title}");
			return;
		}

		SyncAssemblies(app, doc, assemblyIds);
	}

	private static void WriteLog(string message)
	{
		try
		{
			string path = LogPath;
			string dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(dir))
				Directory.CreateDirectory(dir);

			File.AppendAllText(path, System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + message + "\r\n");
		}
		catch
		{
		}
	}
}
