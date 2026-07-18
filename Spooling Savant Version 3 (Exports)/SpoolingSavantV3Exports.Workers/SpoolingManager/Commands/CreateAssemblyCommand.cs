using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class CreateAssemblyCommand : IExternalCommand
{
	internal const string ToolTitle = "Create Spool";

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		//IL_0400: Unknown result type (might be due to invalid IL or missing references)
		//IL_0410: Unknown result type (might be due to invalid IL or missing references)
		//IL_0414: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ff: Invalid comparison between Unknown and I4
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_0385: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_0358: Unknown result type (might be due to invalid IL or missing references)
		//IL_035e: Invalid comparison between Unknown and I4
		//IL_03ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		//IL_015a: Expected O, but got Unknown
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d0: Expected O, but got Unknown
		//IL_01d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_020c: Unknown result type (might be due to invalid IL or missing references)
		//IL_027b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0282: Expected O, but got Unknown
		//IL_0284: Unknown result type (might be due to invalid IL or missing references)
		UIApplication application = commandData.Application;
		if (((application != null) ? application.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(application.Application.VersionNumber);
		}
		UIDocument val = ((application != null) ? application.ActiveUIDocument : null);
		Document doc = ((val != null) ? val.Document : null);
		if (doc == null)
		{
			TaskDialog.Show(ToolTitle, "No active Revit document.");
			return (Result)(-1);
		}
		ICollection<ElementId> elementIds = val.Selection.GetElementIds();
		if (elementIds == null || elementIds.Count == 0)
		{
			TaskDialog.Show(ToolTitle, "Select one or more elements that can belong to an assembly, then run the command again.");
			return (Result)1;
		}
		List<ElementId> list = CollectAssemblyMemberCandidates(doc, elementIds);
		if (list.Count == 0)
		{
			TaskDialog.Show(ToolTitle, "None of the selected elements can be used as assembly members (they may already belong to an assembly or are not valid categories).");
			return (Result)1;
		}
		IReadOnlyList<AssemblyNamingCategoryOption> readOnlyList = BuildNamingCategoryOptions(doc, list);
		if (readOnlyList.Count == 0)
		{
			TaskDialog.Show(ToolTitle, "No naming category is valid for the current selection. Try a different selection or naming setup.");
			return (Result)1;
		}
		CreateAssemblyDialogWindow createAssemblyDialogWindow = new CreateAssemblyDialogWindow(readOnlyList);
		IntPtr mainWindowHandle = application.MainWindowHandle;
		if (mainWindowHandle != IntPtr.Zero)
		{
			new WindowInteropHelper(createAssemblyDialogWindow).Owner = mainWindowHandle;
		}
		if (createAssemblyDialogWindow.ShowDialog() != true)
		{
			return (Result)1;
		}
		ElementId selectedNamingCategoryId = createAssemblyDialogWindow.SelectedNamingCategoryId;
		string enteredAssemblyName = createAssemblyDialogWindow.EnteredAssemblyName;
		string text = createAssemblyDialogWindow.PersistedPackageNameSnapshot ?? string.Empty;
		AssemblyInstance val2 = null;
		try
		{
			Transaction val3 = new Transaction(doc, "Spooling Savant V3 (Exports): Create Assembly");
			try
			{
				val3.Start();
				val2 = AssemblyInstance.Create(doc, (ICollection<ElementId>)list, selectedNamingCategoryId);
				if (val2 == null)
				{
					throw new InvalidOperationException("Revit did not return a new assembly instance.");
				}
				val3.Commit();
			}
			finally
			{
				((IDisposable)val3)?.Dispose();
			}
			ElementId id = ((Element)val2).Id;
			string text2 = AssemblyTypeNaming.Sanitize(enteredAssemblyName);
			if (text2.Length == 0)
			{
				throw new InvalidOperationException("Assembly name is empty or contains only invalid characters. Use letters, numbers, spaces, hyphens, and underscores.");
			}
			Transaction val4 = new Transaction(doc, "Spooling Savant V3 (Exports): Name assembly");
			try
			{
				val4.Start();
				Element element = doc.GetElement(id);
				AssemblyInstance val5 = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
				if (val5 == null)
				{
					throw new InvalidOperationException("The new assembly could not be reloaded for naming.");
				}
				AssemblyTypeNaming.ApplyToAssembly(doc, val5, text2);
				val4.Commit();
			}
			finally
			{
				((IDisposable)val4)?.Dispose();
			}
			if (text.Length > 0)
			{
				Transaction val6 = new Transaction(doc, "Spooling Savant V3 (Exports): Apply S-Package");
				try
				{
					val6.Start();
					Element element2 = doc.GetElement(id);
					Parameter obj2 = (((element2 is AssemblyInstance) ? element2 : null) ?? throw new InvalidOperationException("The new assembly could not be reloaded for S-Package.")).LookupParameter("S-Package") ?? throw new InvalidOperationException("S-Package was not found on the new assembly. Reopen the project so Spooling Savant V3 (Exports) can bind shared parameters at load.");
					if (((APIObject)obj2).IsReadOnly)
					{
						throw new InvalidOperationException("S-Package is read-only on this assembly.");
					}
					if ((int)obj2.StorageType != 3)
					{
						throw new InvalidOperationException("S-Package must be a text parameter.");
					}
					obj2.Set(text);
					foreach (ElementId item in list)
					{
						Element element3 = doc.GetElement(item);
						if (element3 != null)
						{
							Parameter val7 = element3.LookupParameter("S-Package");
							if (val7 != null && !((APIObject)val7).IsReadOnly && (int)val7.StorageType == 3)
							{
								val7.Set(text);
							}
						}
					}
					val6.Commit();
				}
				finally
				{
					((IDisposable)val6)?.Dispose();
				}
			}
			Element element4 = doc.GetElement(id);
			val2 = (AssemblyInstance)(object)((element4 is AssemblyInstance) ? element4 : null);
			CreateAssemblyDialogSettings createAssemblyDialogSettings = CreateAssemblyDialogSettings.Load();
			createAssemblyDialogSettings.LastPackageName = text;
			createAssemblyDialogSettings.LastAssemblyName = CreateAssemblyDialogSettings.SuggestNextNumericSuffix(enteredAssemblyName);
			createAssemblyDialogSettings.Save();
			if (val2 != null)
			{
				val.Selection.SetElementIds((ICollection<ElementId>)new List<ElementId> { ((Element)val2).Id });
			}
			return (Result)0;
		}
		catch (Exception ex)
		{
			TaskDialog.Show(ToolTitle, ex.Message);
			message = ex.Message;
			return (Result)(-1);
		}
	}

	private static List<ElementId> CollectAssemblyMemberCandidates(Document doc, ICollection<ElementId> selection)
	{
		List<ElementId> list = new List<ElementId>();
		foreach (ElementId item in selection)
		{
			if (item == (ElementId)null || item == ElementId.InvalidElementId)
			{
				continue;
			}
			Element element = doc.GetElement(item);
			if (element != null && !(element is AssemblyInstance) && element.Category != null)
			{
				ElementId assemblyInstanceId = element.AssemblyInstanceId;
				if (!(assemblyInstanceId != (ElementId)null) || !(assemblyInstanceId != ElementId.InvalidElementId))
				{
					list.Add(item);
				}
			}
		}
		return list;
	}

	private static IReadOnlyList<AssemblyNamingCategoryOption> BuildNamingCategoryOptions(Document doc, IList<ElementId> memberIds)
	{
		Dictionary<long, (ElementId, string)> dictionary = new Dictionary<long, (ElementId, string)>();
		foreach (ElementId memberId in memberIds)
		{
			Element element = doc.GetElement(memberId);
			Category val = ((element != null) ? element.Category : null);
			if (val != null)
			{
				long value = val.Id.Value;
				if (!dictionary.ContainsKey(value))
				{
					dictionary[value] = (val.Id, val.Name ?? string.Empty);
				}
			}
		}
		List<AssemblyNamingCategoryOption> list = new List<AssemblyNamingCategoryOption>();
		foreach (var item in dictionary.Values.OrderBy<(ElementId, string), string>(((ElementId Id, string Name) x) => x.Name, StringComparer.OrdinalIgnoreCase))
		{
			if (AssemblyInstance.IsValidNamingCategory(doc, item.Item1, (ICollection<ElementId>)memberIds))
			{
				list.Add(new AssemblyNamingCategoryOption(item.Item1, item.Item2));
			}
		}
		return list;
	}
}
