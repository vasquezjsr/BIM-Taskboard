using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public class RefreshSheetsHandler : IExternalEventHandler
{
	public RefreshSheetsRequest PendingRequest { get; set; }

	public void Execute(UIApplication app)
	{
		//IL_021a: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Expected O, but got Unknown
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		if (((app != null) ? app.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}
		RefreshSheetsRequest pendingRequest = PendingRequest;
		PendingRequest = null;
		if (pendingRequest == null || pendingRequest.AssemblyIds == null || pendingRequest.AssemblyIds.Count == 0)
		{
			return;
		}
		UIDocument activeUIDocument = app.ActiveUIDocument;
		Document val = ((activeUIDocument != null) ? activeUIDocument.Document : null);
		if (val == null)
		{
			return;
		}
		SpoolingManagerKind productKind = pendingRequest.ProductKind;
		SpoolingManagerSettings spoolingManagerSettings = SpoolingManagerSettings.Load(productKind);
		bool regularSheetBranch = CreateSpoolSheetsHandler.UsesRegularSheetBranch(spoolingManagerSettings, productKind);
		string toolWindowTitle = CreateSpoolSheetsHandler.GetToolWindowTitle(productKind);
		FamilySymbol val2 = null;
		FamilySymbol hangerTagType = null;
		FamilySymbol ductTagType = null;
		FamilySymbol val2b = null;
		FamilySymbol val2c = null;
		if (CreateSpoolSheetsHandler.HasAnyTaggingEnabled(spoolingManagerSettings))
		{
			val2 = CreateSpoolSheetsHandler.FindTagType(val, spoolingManagerSettings.TagTypeName);
			if (val2 == null)
			{
				MessageBox.Show("Pipe/Fitting Tag NOT FOUND:\n" + spoolingManagerSettings.TagTypeName, toolWindowTitle);
				return;
			}
			if (!string.IsNullOrWhiteSpace(spoolingManagerSettings.HangerTagTypeName))
			{
				hangerTagType = CreateSpoolSheetsHandler.FindTagType(val, spoolingManagerSettings.HangerTagTypeName);
			}
			if (!string.IsNullOrWhiteSpace(spoolingManagerSettings.DuctTagTypeName))
			{
				ductTagType = CreateSpoolSheetsHandler.FindTagType(val, spoolingManagerSettings.DuctTagTypeName);
			}
		}
		if (spoolingManagerSettings.NumberWeldsEnabled)
		{
			val2b = CreateSpoolSheetsHandler.FindTagType(val, spoolingManagerSettings.WeldTagTypeName);
			if (val2b == null)
			{
				MessageBox.Show("Weld Tag Type NOT FOUND:\n" + spoolingManagerSettings.WeldTagTypeName, toolWindowTitle);
				return;
			}
		}
		if (spoolingManagerSettings.ContinuationTagsEnabled)
		{
			val2c = CreateSpoolSheetsHandler.FindTagType(val, spoolingManagerSettings.AssemblyTagTypeName);
			if (val2c == null)
			{
				MessageBox.Show("Continuation Tag Type NOT FOUND:\n" + spoolingManagerSettings.AssemblyTagTypeName, toolWindowTitle);
				return;
			}
		}
		TextNoteType weldLogTextNoteType = null;
		if (spoolingManagerSettings.WeldLogEnabled)
		{
			weldLogTextNoteType = CreateSpoolSheetsHandler.FindTextNoteType(val, spoolingManagerSettings.WeldLogTextNoteTypeName);
			if (weldLogTextNoteType == null)
			{
				MessageBox.Show("Weld Log Text Type NOT FOUND:\n" + spoolingManagerSettings.WeldLogTextNoteTypeName, toolWindowTitle);
				return;
			}
		}
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int numRedimViews = 0;
		int numCreatedViews = 0;
		int numWeldLogNotes = 0;
		List<string> list = new List<string>();
		Dictionary<ElementId, ViewSheet> dictionary = CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(val, regularSheetBranch, pendingRequest.AssemblyIds);
		Transaction val3 = new Transaction(val, "Spooling Savant V3 Exports: Refresh Assemblies");
		try
		{
			val3.Start();
			CreateSpoolSheetsHandler.BeginBatchRegenCoalescing();
			foreach (ElementId assemblyId in pendingRequest.AssemblyIds)
			{
				Element element = val.GetElement(assemblyId);
				AssemblyInstance val4 = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
				if (val4 == null)
				{
					continue;
				}
				FabricationSavantParameterSync.SyncAssemblyMemberParameters(app.Application, val, val4);
				CreateSpoolSheetsHandler.AssignAssemblyItemNumbers(val, val4, pendingRequest.ProductKind, spoolingManagerSettings);
				if (spoolingManagerSettings.NumberWeldsEnabled)
				{
					CreateSpoolSheetsHandler.AssignAssemblySWeldNumbers(val, val4, CreateSpoolSheetsHandler.ComputeAssemblySWeldPrefix(val, val4, spoolingManagerSettings));
				}
				if (spoolingManagerSettings.ContinuationTagsEnabled)
				{
					CreateSpoolSheetsHandler.AssignAssemblyContinuationValues(app.Application, val, val4);
				}
				num++;
				dictionary.TryGetValue(((Element)val4).Id, out var value);
				if (value == null)
				{
					list.Add(AssemblyDisplayName.Get(val4) + ": no existing sheet was found; item numbers were still refreshed.");
					continue;
				}
				num2++;
				try
				{
					numCreatedViews += CreateSpoolSheetsHandler.TryAddMissingAssemblyViews(
						app, val, value, val4, spoolingManagerSettings, productKind, val2, val2b, val2c, list);
				}
				catch (Exception ex3)
				{
					list.Add(AssemblyDisplayName.Get(val4) + ": failed to add missing views. " + ex3.Message);
				}
				foreach (View item in CreateSpoolSheetsHandler.FindAssemblyViews(val, val4))
				{
					try
					{
						CreateSpoolSheetsHandler.RestrictViewToAssemblyElements(val, val4, item);
						CreateSpoolSheetsHandler.RequestRegenerate(val);
						if (!CreateSpoolSheetsHandler.TryGetExistingViewSheetSettings(item, spoolingManagerSettings, out var placement, out var tagEnabled, out var rotation))
						{
							continue;
						}
						if (!(item is View3D))
						{
							if (CreateSpoolSheetsHandler.IsAutoDimEnabledForExistingView(item, spoolingManagerSettings))
							{
								if (CreateSpoolSheetsHandler.ReapplyAutoDimensionsForView(val, item, val4, spoolingManagerSettings))
								{
									numRedimViews++;
								}
							}
							if (CreateSpoolSheetsHandler.ApplyViewCropRegionRotation(val, item, rotation))
							{
								CreateSpoolSheetsHandler.RequestRegenerate(val);
								CreateSpoolSheetsHandler.TryRecenterSheetViewportForView(val, value, item, placement);
							}
							foreach (ElementId allViewport in value.GetAllViewports())
							{
								Element element2 = val.GetElement(allViewport);
								Viewport val5 = (Viewport)(object)((element2 is Viewport) ? element2 : null);
								if (val5 == null || val5.ViewId != ((Element)item).Id)
								{
									continue;
								}
								try
								{
									if ((int)val5.Rotation != 0)
									{
										val5.Rotation = (ViewportRotation)0;
									}
								}
								catch
								{
								}
							}

							CreateSpoolSheetsHandler.TryPositionAllViewportTitlesOnSheet(val, value);
						}
						if (tagEnabled && val2 != null)
						{
							HashSet<string> existingTaggedItemNumbers = CreateSpoolSheetsHandler.GetExistingTaggedItemNumbers(val, item);
							CreateSpoolSheetsHandler.TagCreationResult tagCreationResult = CreateSpoolSheetsHandler.CreateTags(val, val4, item, val2, placement, productKind, spoolingManagerSettings, existingTaggedItemNumbers, spoolingManagerSettings.NumberWeldsEnabled ? string.Empty : null, val2b, val2c, null, hangerTagType, ductTagType);
							num3 += tagCreationResult.CreatedCount;
						}
					}
					catch (Exception ex)
					{
						list.Add(AssemblyDisplayName.Get(val4) + ": failed to refresh " + ((Element)item).Name + ". " + ex.Message);
					}
				}
				if (spoolingManagerSettings.WeldLogEnabled && weldLogTextNoteType != null && value != null)
				{
					try
					{
						numWeldLogNotes += CreateSpoolSheetsHandler.FillWeldLogOnSheet(val, value, val4, spoolingManagerSettings, weldLogTextNoteType);
					}
					catch (Exception ex2)
					{
						list.Add(AssemblyDisplayName.Get(val4) + ": failed to fill weld log. " + ex2.Message);
					}
				}

				if (value != null)
				{
					try
					{
						SpoolSheetQrCodeService.PlaceOrUpdateOnSheet(val, value, val4, spoolingManagerSettings);
					}
					catch (Exception exQr)
					{
						list.Add(AssemblyDisplayName.Get(val4) + ": failed to place tracking QR. " + exQr.Message);
					}
				}
			}
			using (AssemblyMemberChangeCoordinator.SuppressAutoSync())
			{
				val3.Commit();
			}
		}
		finally
		{
			CreateSpoolSheetsHandler.EndBatchRegenCoalescing(val);
			((IDisposable)val3)?.Dispose();
		}
		string text = num + " assembly(s) refreshed.";
		text = text + "\n" + num2 + " sheet(s) refreshed.";
		if (num3 > 0)
		{
			text = text + "\n" + num3 + " tag(s) created.";
		}
		if (numCreatedViews > 0)
		{
			text = text + "\n" + numCreatedViews + " new view(s) added.";
		}
		if (numRedimViews > 0)
		{
			text = text + "\n" + numRedimViews + " view(s) re-dimensioned.";
		}
		if (numWeldLogNotes > 0)
		{
			text = text + "\n" + numWeldLogNotes + " weld log text note(s) placed.";
		}
		if (list.Count > 0)
		{
			text = text + "\n\n" + string.Join("\n", list.Take(10));
		}
		RevitRequestBridge.ShowOperationSummary(toolWindowTitle, text);
	}

	public string GetName()
	{
		return "Refresh Assemblies";
	}
}
