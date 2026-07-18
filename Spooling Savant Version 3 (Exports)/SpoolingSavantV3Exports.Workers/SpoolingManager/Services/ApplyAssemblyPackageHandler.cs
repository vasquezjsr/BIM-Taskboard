using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Application = Autodesk.Revit.ApplicationServices.Application;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public class ApplyAssemblyPackageHandler : IExternalEventHandler
{
	internal const string PackageParameterName = "S-Package";

	public ApplyAssemblyPackageRequest PendingRequest { get; set; }

	public void Execute(UIApplication app)
	{
		//IL_01ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c1: Expected O, but got Unknown
		//IL_01c3: Unknown result type (might be due to invalid IL or missing references)
		if (((app != null) ? app.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}
		ApplyAssemblyPackageRequest pendingRequest = PendingRequest;
		PendingRequest = null;
		if (pendingRequest == null || pendingRequest.AssemblyIds == null || pendingRequest.AssemblyIds.Count == 0)
		{
			return;
		}
		bool clearPackage = pendingRequest.ClearPackage;
		string text = (pendingRequest.PackageValue ?? string.Empty).Trim();
		if (!clearPackage && text.Length == 0)
		{
			return;
		}
		UIDocument activeUIDocument = app.ActiveUIDocument;
		Document val = ((activeUIDocument != null) ? activeUIDocument.Document : null);
		if (val == null)
		{
			return;
		}
		string toolWindowTitle = CreateSpoolSheetsHandler.GetToolWindowTitle(pendingRequest.ProductKind);
		List<AssemblyInstance> list = new List<AssemblyInstance>();
		int num = 0;
		foreach (ElementId item in pendingRequest.AssemblyIds.Distinct())
		{
			Element element = val.GetElement(item);
			AssemblyInstance val2 = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			if (val2 == null)
			{
				num++;
			}
			else
			{
				list.Add(val2);
			}
		}
		HashSet<long> hashSet = new HashSet<long>();
		List<Element> list2 = new List<Element>();
		foreach (AssemblyInstance item2 in list)
		{
			foreach (ElementId memberId in item2.GetMemberIds())
			{
				if (hashSet.Add(memberId.Value))
				{
					Element element2 = val.GetElement(memberId);
					if (element2 != null)
					{
						list2.Add(element2);
					}
				}
			}
		}
		string value = (clearPackage ? string.Empty : text);
		int num2 = 0;
		int num3 = 0;
		int skippedMissing = 0;
		int skippedReadOnly = 0;
		int skippedWrongType = 0;
		int skippedMissing2 = 0;
		int skippedReadOnly2 = 0;
		int skippedWrongType2 = 0;
		string text2 = (clearPackage ? "Spooling Savant V3 (Exports): Clear S-Package" : "Spooling Savant V3 (Exports): S-Package");
		Transaction val3 = new Transaction(val, text2);
		try
		{
			val3.Start();
			foreach (AssemblyInstance item3 in list)
			{
				if (TrySetPackageParameter((Element)(object)item3, value, ref skippedMissing, ref skippedReadOnly, ref skippedWrongType))
				{
					num2++;
				}
			}
			foreach (Element item4 in list2)
			{
				if (TrySetPackageParameter(item4, value, ref skippedMissing2, ref skippedReadOnly2, ref skippedWrongType2))
				{
					num3++;
				}
			}
			using (AssemblyMemberChangeCoordinator.SuppressAutoSync())
			{
				val3.Commit();
			}
		}
		finally
		{
			((IDisposable)val3)?.Dispose();
		}
		string text3 = (clearPackage ? $"S-Package cleared on {num2} assembly instance(s) and {num3} member element(s)." : $"S-Package set to \"{text}\" on {num2} assembly instance(s) and {num3} member element(s).");
		if (num > 0)
		{
			text3 += $"\n{num} id(s) were not assembly instances.";
		}
		if (skippedMissing > 0)
		{
			text3 += $"\n{skippedMissing} assembly instance(s) had no S-Package parameter after binding.";
		}
		if (skippedReadOnly > 0)
		{
			text3 += $"\n{skippedReadOnly} read-only S-Package parameter(s) skipped on assemblies.";
		}
		if (skippedWrongType > 0)
		{
			text3 += $"\n{skippedWrongType} non-text S-Package parameter(s) skipped on assemblies.";
		}
		if (skippedMissing2 > 0)
		{
			text3 += $"\n{skippedMissing2} member element(s) had no S-Package parameter after binding.";
		}
		if (skippedReadOnly2 > 0)
		{
			text3 += $"\n{skippedReadOnly2} read-only S-Package parameter(s) skipped on members.";
		}
		if (skippedWrongType2 > 0)
		{
			text3 += $"\n{skippedWrongType2} non-text S-Package parameter(s) skipped on members.";
		}
		if (!pendingRequest.SuppressCompletionDialog || num2 + num3 == 0)
		{
			RevitRequestBridge.ShowOperationSummary(toolWindowTitle, text3);
		}
		RevitRequestBridge.NotifyApplyAssemblyPackageCompleted();
	}

	private static bool TrySetPackageParameter(Element element, string value, ref int skippedMissing, ref int skippedReadOnly, ref int skippedWrongType)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Invalid comparison between Unknown and I4
		Parameter val = element.LookupParameter("S-Package");
		if (val == null)
		{
			skippedMissing++;
			return false;
		}
		if (((APIObject)val).IsReadOnly)
		{
			skippedReadOnly++;
			return false;
		}
		if ((int)val.StorageType != 3)
		{
			skippedWrongType++;
			return false;
		}
		val.Set(value ?? string.Empty);
		return true;
	}

	public string GetName()
	{
		return "SS Manager: Apply S-Package";
	}
}
