using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public class RenameSheetsHandler : IExternalEventHandler
{
	public RenameSheetsRequest PendingRequest { get; set; }

	public void Execute(UIApplication app)
	{
		//IL_03d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_018f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0196: Expected O, but got Unknown
		//IL_0178: Unknown result type (might be due to invalid IL or missing references)
		//IL_0198: Unknown result type (might be due to invalid IL or missing references)
		if (((app != null) ? app.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}
		RenameSheetsRequest pendingRequest = PendingRequest;
		PendingRequest = null;
		if (pendingRequest == null || pendingRequest.Items == null || pendingRequest.Items.Count == 0)
		{
			return;
		}
		UIDocument activeUIDocument = app.ActiveUIDocument;
		Document doc = ((activeUIDocument != null) ? activeUIDocument.Document : null);
		if (doc == null)
		{
			return;
		}
		SpoolingManagerSettings.SetActiveProject(doc);
		List<string> list = new List<string>();
		int num = 0;
		SpoolingManagerKind productKind = pendingRequest.ProductKind;
		bool regularSheetBranch = CreateSpoolSheetsHandler.UsesRegularSheetBranch(SpoolingManagerSettings.Load(productKind), productKind);
		Dictionary<ElementId, AssemblyInstance> dictionary = (from x in pendingRequest.Items.Select(delegate(RenameSheetItem x)
			{
				Element element = doc.GetElement(x.AssemblyId);
				return (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			})
			where x != null
			select x).ToDictionary((AssemblyInstance x) => ((Element)x).Id, (AssemblyInstance x) => x);
		List<ElementId> assemblyInstanceIds = pendingRequest.Items.Select((RenameSheetItem i) => i.AssemblyId).ToList();
		Dictionary<ElementId, ViewSheet> dictionary2 = CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(doc, regularSheetBranch, assemblyInstanceIds);
		List<string> existingNameConflicts = GetExistingNameConflicts(doc, pendingRequest, dictionary, dictionary2);
		if (existingNameConflicts.Count > 0)
		{
			Views.SsSavantMessageBox.Show(BuildExistingNameConflictMessage(existingNameConflicts), "Rename Sheets");
			RevitRequestBridge.NotifyRenameSheetsCompleted();
			return;
		}
		Transaction val = new Transaction(doc, "Spooling Savant: Rename Sheets");
		try
		{
			val.Start();
			Dictionary<ElementId, string> dictionary3 = new Dictionary<ElementId, string>();
			foreach (RenameSheetItem item in pendingRequest.Items)
			{
				if (dictionary.TryGetValue(item.AssemblyId, out var value))
				{
					string text = "__TMP__" + Guid.NewGuid().ToString("N");
					try
					{
						ApplyAssemblyIdentityName(doc, value, text);
						dictionary3[item.AssemblyId] = text;
					}
					catch (Exception ex)
					{
						list.Add((item.CurrentName ?? "Assembly") + ": " + ex.Message);
					}
				}
			}
			foreach (RenameSheetItem item2 in pendingRequest.Items)
			{
				if (!dictionary.TryGetValue(item2.AssemblyId, out var value2))
				{
					continue;
				}
				string text2 = (item2.NewName ?? string.Empty).Trim();
				if (string.IsNullOrWhiteSpace(text2))
				{
					continue;
				}
				try
				{
					ApplyAssemblyIdentityName(doc, value2, text2);
				}
				catch (Exception ex2)
				{
					list.Add((item2.CurrentName ?? "Assembly") + ": " + ex2.Message);
					continue;
				}
				if (dictionary2.TryGetValue(item2.AssemblyId, out var value3))
				{
					try
					{
						value3.SheetNumber = "__TMP__" + Guid.NewGuid().ToString("N");
					}
					catch
					{
					}
				}
			}
			foreach (RenameSheetItem item3 in pendingRequest.Items)
			{
				string text3 = (item3.NewName ?? string.Empty).Trim();
				if (string.IsNullOrWhiteSpace(text3))
				{
					continue;
				}
				if (dictionary2.TryGetValue(item3.AssemblyId, out var value4))
				{
					try
					{
						((Element)value4).Name = text3;
						value4.SheetNumber = text3;
					}
					catch (Exception ex3)
					{
						list.Add(text3 + ": " + ex3.Message);
					}
				}
				num++;
			}
			using (AssemblyMemberChangeCoordinator.SuppressAutoSync())
			{
				val.Commit();
			}
		}
		finally
		{
			((IDisposable)val)?.Dispose();
		}
		string text4 = num + " sheet(s) renamed.";
		if (list.Count > 0)
		{
			text4 = text4 + "\n\n" + string.Join("\n", list.Distinct().Take(10));
		}
		SsSavantCompletionDialog.Show("Rename Sheets", text4);
		RevitRequestBridge.NotifyRenameSheetsCompleted();
	}

	public string GetName()
	{
		return "Rename Sheets";
	}

	private static void ApplyAssemblyIdentityName(Document doc, AssemblyInstance assembly, string name)
	{
		string text = (name ?? string.Empty).Trim();
		if (assembly != null && doc != null && text.Length != 0)
		{
			Element element = doc.GetElement(((Element)assembly).GetTypeId());
			AssemblyType val = (AssemblyType)(object)((element is AssemblyType) ? element : null);
			if (val != null)
			{
				((Element)val).Name = text;
			}
			assembly.AssemblyTypeName = text;
		}
	}

	private static List<string> GetExistingNameConflicts(Document doc, RenameSheetsRequest request, Dictionary<ElementId, AssemblyInstance> assemblies, Dictionary<ElementId, ViewSheet> sheets)
	{
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (RenameSheetItem item in request.Items)
		{
			string text = (item.NewName ?? string.Empty).Trim();
			if (!string.IsNullOrWhiteSpace(text))
			{
				hashSet.Add(text);
			}
		}
		if (hashSet.Count == 0)
		{
			return new List<string>();
		}
		HashSet<ElementId> hashSet2 = new HashSet<ElementId>(assemblies.Keys);
		HashSet<ElementId> hashSet3 = new HashSet<ElementId>(sheets.Values.Select((ViewSheet x) => ((Element)x).Id));
		HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AssemblyInstance item2 in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance))).Cast<AssemblyInstance>())
		{
			if (!hashSet2.Contains(((Element)item2).Id))
			{
				AddName(existingNames, AssemblyDisplayName.Get(item2));
			}
		}
		foreach (ViewSheet item3 in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))).Cast<ViewSheet>())
		{
			if (!hashSet3.Contains(((Element)item3).Id))
			{
				AddName(existingNames, item3.SheetNumber);
			}
		}
		foreach (View item4 in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))).Cast<View>())
		{
			if (!hashSet3.Contains(((Element)item4).Id))
			{
				AddName(existingNames, ((Element)item4).Name);
			}
		}
		return (from x in hashSet
			where existingNames.Contains(x)
			orderby x
			select x).ToList();
	}

	private static void AddName(HashSet<string> names, string value)
	{
		string text = (value ?? string.Empty).Trim();
		if (!string.IsNullOrWhiteSpace(text))
		{
			names.Add(text);
		}
	}

	private static string BuildExistingNameConflictMessage(List<string> existingConflicts)
	{
		if (existingConflicts.Count == 1)
		{
			return "Cannot rename sheets to \"" + existingConflicts[0] + "\" because it already exists.";
		}
		return "Cannot rename sheets to the following names because they already exist:\n\n" + string.Join("\n", existingConflicts.Select((string x) => "\"" + x + "\""));
	}
}
