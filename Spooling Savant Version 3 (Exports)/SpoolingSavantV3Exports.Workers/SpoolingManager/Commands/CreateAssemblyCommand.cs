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
		UIApplication application = commandData.Application;
		if (((application != null) ? application.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(application.Application.VersionNumber);
		}
		UIDocument val = ((application != null) ? application.ActiveUIDocument : null);
		Document doc = ((val != null) ? val.Document : null);
		if (doc == null)
		{
			Views.SsSavantMessageBox.Show("No active Revit document.", ToolTitle);
			return (Result)(-1);
		}
		ICollection<ElementId> elementIds = val.Selection.GetElementIds();
		if (elementIds == null || elementIds.Count == 0)
		{
			Views.SsSavantMessageBox.Show("Select one or more elements that can belong to an assembly, then run the command again.", ToolTitle);
			return (Result)1;
		}
		List<ElementId> list = CollectAssemblyMemberCandidates(doc, elementIds);
		if (list.Count == 0)
		{
			Views.SsSavantMessageBox.Show("None of the selected elements can be used as assembly members (they may already belong to an assembly or are not valid categories).", ToolTitle);
			return (Result)1;
		}

		return CreateSpoolFromMemberIds(application, val, doc, list, ToolTitle, ref message);
	}

	/// <summary>
	/// Shared create path used by Create Spool (pre-selection) and Trace Spool (gathered endpoints).
	/// When <paramref name="session"/> is non-null and <paramref name="promptDialog"/> is false,
	/// reuses category/package from the session and the next numeric name from saved settings.
	/// </summary>
	internal static Result CreateSpoolFromMemberIds(
		UIApplication application,
		UIDocument uidoc,
		Document doc,
		List<ElementId> memberIds,
		string toolTitle,
		ref string message,
		CreateSpoolSession session = null,
		bool promptDialog = true)
	{
		if (application == null || doc == null || memberIds == null || memberIds.Count == 0)
		{
			return (Result)1;
		}

		if (session == null)
		{
			session = new CreateSpoolSession();
		}

		List<ElementId> list = CollectAssemblyMemberCandidates(doc, memberIds);
		if (list.Count == 0)
		{
			Views.SsSavantMessageBox.Show("None of the elements can be used as assembly members (they may already belong to an assembly or are not valid categories).", toolTitle);
			return (Result)1;
		}

		ElementId selectedNamingCategoryId;
		string enteredAssemblyName;
		string text;
		bool startChainSpooling = false;

		if (promptDialog || session.NamingCategoryId == null
			|| session.NamingCategoryId == ElementId.InvalidElementId)
		{
			IReadOnlyList<AssemblyNamingCategoryOption> readOnlyList = BuildNamingCategoryOptions(doc, list);
			if (readOnlyList.Count == 0)
			{
				Views.SsSavantMessageBox.Show("No naming category is valid for the current selection. Try a different selection or naming setup.", toolTitle);
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
			selectedNamingCategoryId = createAssemblyDialogWindow.SelectedNamingCategoryId;
			enteredAssemblyName = createAssemblyDialogWindow.EnteredAssemblyName;
			text = createAssemblyDialogWindow.PersistedPackageNameSnapshot ?? string.Empty;
			session.NamingCategoryId = selectedNamingCategoryId;
			session.PackageName = text;
			session.ChainSpooling = createAssemblyDialogWindow.ChainSpoolingEnabled;
			startChainSpooling = session.ChainSpooling;
		}
		else
		{
			selectedNamingCategoryId = session.NamingCategoryId;
			text = session.PackageName ?? string.Empty;
			CreateAssemblyDialogSettings settings = CreateAssemblyDialogSettings.Load();
			enteredAssemblyName = AssemblyTypeNaming.Sanitize(settings.LastAssemblyName ?? string.Empty);
			if (enteredAssemblyName.Length == 0)
			{
				enteredAssemblyName = "Assembly-01";
			}
		}

		AssemblyInstance val2 = null;
		try
		{
			Transaction val3 = new Transaction(doc, "Spooling Savant: Create Assembly");
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
			Transaction val4 = new Transaction(doc, "Spooling Savant: Name assembly");
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
				Transaction val6 = new Transaction(doc, "Spooling Savant: Apply S-Package");
				try
				{
					val6.Start();
					Element element2 = doc.GetElement(id);
					Parameter obj2 = (((element2 is AssemblyInstance) ? element2 : null) ?? throw new InvalidOperationException("The new assembly could not be reloaded for S-Package.")).LookupParameter("S-Package") ?? throw new InvalidOperationException("S-Package was not found on the new assembly. Reopen the project so Spooling Savant can bind shared parameters at load.");
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
			if (promptDialog)
			{
				createAssemblyDialogSettings.LastChainSpooling = session.ChainSpooling;
			}
			createAssemblyDialogSettings.Save();
			session.LastCreatedAssemblyId = id;
			if (val2 != null && uidoc != null)
			{
				uidoc.Selection.SetElementIds((ICollection<ElementId>)new List<ElementId> { ((Element)val2).Id });
			}

			if (startChainSpooling && uidoc != null)
			{
				ChainSpoolingRunner.Run(
					application,
					uidoc,
					doc,
					id,
					session,
					toolTitle,
					ref message);
			}

			return (Result)0;
		}
		catch (Exception ex)
		{
			Views.SsSavantMessageBox.Show(ex.Message, toolTitle);
			message = ex.Message;
			return (Result)(-1);
		}
	}

	/// <summary>Carries naming category / package across continuous Trace / Chain Spooling creates.</summary>
	internal sealed class CreateSpoolSession
	{
		public ElementId NamingCategoryId { get; set; }

		public string PackageName { get; set; } = string.Empty;

		public bool ChainSpooling { get; set; }

		public ElementId LastCreatedAssemblyId { get; set; }
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
