using System;
using System.Collections.Generic;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

internal static class AssemblyMemberSyncService
{
	internal const string SyncTransactionName = "Spooling Savant V3 (Exports): Sync assembly member";

	internal static void SyncAfterMemberChange(Application app, Document doc, AssemblyInstance assembly)
	{
		if (app == null || doc == null || assembly == null)
			return;

		SpoolingManagerKind productKind = SpoolingManagerKind.Standard;
		SpoolingManagerSettings settings = SpoolingManagerSettings.Load(productKind);
		bool membersNeedProcessing = AssemblyHasMemberNeedingSync(doc, assembly);
		bool weldMembersNeedingSync = settings.NumberWeldsEnabled && AssemblyHasWeldMemberNeedingSync(doc, assembly);

		// Hangers are numbered/tagged separately — do not run pipe/fitting sync (or full sheet retag)
		// just because a hanger was added to an assembly that already has a spool sheet.
		if (!membersNeedProcessing && !weldMembersNeedingSync)
			return;

		bool hasSpoolSheet = TryGetSpoolSheet(doc, assembly, settings, productKind, out _);
		bool forceFullSheetRefresh = hasSpoolSheet && weldMembersNeedingSync;

		FabricationSavantParameterSync.EnsurePackageParameterForAssembly(app, doc, assembly);
		FabricationSavantParameterSync.SyncAssemblyMemberParameters(app, doc, assembly);

		string package = FabricationSavantParameterSync.TryGetAssemblyPackageValue(doc, assembly);
		if (!string.IsNullOrWhiteSpace(package))
			FabricationSavantParameterSync.ApplyPackageToMembersWithoutValue(doc, assembly, package);

		HashSet<ElementId> membersNeedingSync = CollectMemberIdsNeedingSync(doc, assembly);

		CreateSpoolSheetsHandler.AssignAssemblyItemNumbers(doc, assembly, productKind, settings);

		if (settings.NumberWeldsEnabled)
		{
			string weldPrefix = CreateSpoolSheetsHandler.ComputeAssemblySWeldPrefix(doc, assembly, settings);
			CreateSpoolSheetsHandler.AssignAssemblySWeldNumbers(doc, assembly, weldPrefix);
		}

		if (settings.ContinuationTagsEnabled)
			CreateSpoolSheetsHandler.AssignAssemblyContinuationValues(app, doc, assembly);

		RefreshTagsIfSheetExists(doc, assembly, productKind, settings, membersNeedingSync, forceFullSheetRefresh || membersNeedProcessing);
		RefreshWeldLogIfSheetExists(doc, assembly, productKind, settings, forceFullSheetRefresh || settings.NumberWeldsEnabled);
	}

	private static bool AssemblyHasWeldMemberNeedingSync(Document doc, AssemblyInstance assembly)
	{
		try
		{
			foreach (ElementId memberId in assembly.GetMemberIds())
			{
				Element member = doc.GetElement(memberId);
				if (member is FabricationPart fabricationPart &&
					FabricationPartClassification.IsWeldPart(fabricationPart) &&
					!FabricationPartClassification.IsOletPart(fabricationPart) &&
					string.IsNullOrWhiteSpace(CreateSpoolSheetsHandler.GetSWeldValue(fabricationPart)))
				{
					return true;
				}
			}
		}
		catch
		{
		}

		return false;
	}

	private static void RefreshWeldLogIfSheetExists(
		Document doc,
		AssemblyInstance assembly,
		SpoolingManagerKind productKind,
		SpoolingManagerSettings settings,
		bool shouldRefresh)
	{
		if (!shouldRefresh || doc == null || assembly == null || settings == null || !settings.WeldLogEnabled)
		{
			return;
		}

		if (!TryGetSpoolSheet(doc, assembly, settings, productKind, out ViewSheet sheet))
		{
			return;
		}

		TextNoteType weldLogTextNoteType = CreateSpoolSheetsHandler.FindTextNoteType(doc, settings.WeldLogTextNoteTypeName);
		if (weldLogTextNoteType == null)
		{
			return;
		}

		try
		{
			CreateSpoolSheetsHandler.FillWeldLogOnSheet(doc, sheet, assembly, settings, weldLogTextNoteType);
		}
		catch
		{
			// Auto-sync must never interrupt the user's edit session.
		}

		try
		{
			SpoolSheetQrCodeService.PlaceOrUpdateOnSheet(doc, sheet, assembly, settings);
		}
		catch
		{
			// Auto-sync must never interrupt the user's edit session.
		}
	}

	private static bool TryGetSpoolSheet(
		Document doc,
		AssemblyInstance assembly,
		SpoolingManagerSettings settings,
		SpoolingManagerKind productKind,
		out ViewSheet sheet)
	{
		sheet = null;
		if (doc == null || assembly == null)
		{
			return false;
		}

		bool regularSheetBranch = CreateSpoolSheetsHandler.UsesRegularSheetBranch(settings, productKind);
		return CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(
			doc,
			regularSheetBranch,
			new[] { assembly.Id }).TryGetValue(assembly.Id, out sheet) && sheet != null;
	}

	// True only when the assembly has a member that has not yet been processed by a previous sync/generation
	// (missing package # or fabrication item number). A member that has both has already been handled, so
	// moves/parameter edits of existing members leave every member "processed" and the whole sync is skipped.
	private static bool AssemblyHasMemberNeedingSync(Document doc, AssemblyInstance assembly)
	{
		try
		{
			foreach (ElementId memberId in assembly.GetMemberIds())
			{
				Element member = doc.GetElement(memberId);
				if (member != null && MemberNeedsSync(member))
					return true;
			}
		}
		catch
		{
		}

		return false;
	}

	private static bool MemberNeedsSync(Element element)
	{
		if (element == null || FabricationPartClassification.IsFabricationHanger(element))
			return false;

		if (HasEmptyPackage(element))
			return true;

		if (element is FabricationPart fabricationPart)
		{
			if (FabricationPartClassification.IsWeldPart(fabricationPart) &&
				!FabricationPartClassification.IsOletPart(fabricationPart))
			{
				return string.IsNullOrWhiteSpace(CreateSpoolSheetsHandler.GetSWeldValue(fabricationPart));
			}

			return string.IsNullOrWhiteSpace(CreateSpoolSheetsHandler.GetFabricationItemNumber(fabricationPart));
		}

		return false;
	}

	private static bool HasEmptyPackage(Element element)
	{
		Parameter parameter = element?.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
		if (parameter == null)
			return true;

		string value = parameter.StorageType == StorageType.String
			? parameter.AsString()
			: parameter.AsValueString();
		return string.IsNullOrWhiteSpace(value);
	}

	private static HashSet<ElementId> CollectMemberIdsNeedingSync(Document doc, AssemblyInstance assembly)
	{
		HashSet<ElementId> memberIds = new HashSet<ElementId>();
		if (doc == null || assembly == null)
		{
			return memberIds;
		}
		try
		{
			foreach (ElementId memberId in assembly.GetMemberIds())
			{
				Element member = doc.GetElement(memberId);
				if (member != null && MemberNeedsSync(member))
				{
					memberIds.Add(memberId);
				}
			}
		}
		catch
		{
		}
		return memberIds;
	}

	private static void RefreshTagsIfSheetExists(
		Document doc,
		AssemblyInstance assembly,
		SpoolingManagerKind productKind,
		SpoolingManagerSettings settings,
		ICollection<ElementId> restrictToPartIds,
		bool forceFullSheetRefresh = false)
	{
		if (!CreateSpoolSheetsHandler.HasAnyTaggingEnabled(settings))
			return;

		bool regularSheetBranch = CreateSpoolSheetsHandler.UsesRegularSheetBranch(settings, productKind);
		Dictionary<ElementId, ViewSheet> sheets = CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(
			doc,
			regularSheetBranch,
			new[] { assembly.Id });

		if (!sheets.TryGetValue(assembly.Id, out ViewSheet sheet) || sheet == null)
			return;

		FamilySymbol tagType = CreateSpoolSheetsHandler.FindTagType(doc, settings.TagTypeName);
		if (tagType == null)
			return;

		FamilySymbol hangerTagType = null;
		if (!string.IsNullOrWhiteSpace(settings.HangerTagTypeName))
			hangerTagType = CreateSpoolSheetsHandler.FindTagType(doc, settings.HangerTagTypeName);

		FamilySymbol ductTagType = null;
		if (!string.IsNullOrWhiteSpace(settings.DuctTagTypeName))
			ductTagType = CreateSpoolSheetsHandler.FindTagType(doc, settings.DuctTagTypeName);

		FamilySymbol weldTagType = null;
		if (settings.NumberWeldsEnabled)
			weldTagType = CreateSpoolSheetsHandler.FindTagType(doc, settings.WeldTagTypeName);

		FamilySymbol assemblyTagType = null;
		if (settings.ContinuationTagsEnabled)
			assemblyTagType = CreateSpoolSheetsHandler.FindTagType(doc, settings.AssemblyTagTypeName);

		foreach (View view in CreateSpoolSheetsHandler.FindAssemblyViews(doc, assembly))
		{
			try
			{
				if (!CreateSpoolSheetsHandler.TryGetExistingViewSheetSettings(
						view,
						settings,
						out string placement,
						out bool tagEnabled,
						out _))
				{
					continue;
				}

				if (!tagEnabled)
					continue;

				CreateSpoolSheetsHandler.RestrictViewToAssemblyElements(doc, assembly, view);
				if (forceFullSheetRefresh)
				{
					CreateSpoolSheetsHandler.RemoveAssemblyFabricationTags(doc, assembly, view, tagType, weldTagType, hangerTagType, ductTagType);
				}

				doc.Regenerate();
				HashSet<string> existingTaggedItemNumbers = forceFullSheetRefresh
					? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
					: CreateSpoolSheetsHandler.GetExistingTaggedItemNumbers(doc, view);
				CreateSpoolSheetsHandler.CreateTags(
					doc,
					assembly,
					view,
					tagType,
					placement,
					productKind,
					settings,
					existingTaggedItemNumbers,
					settings.NumberWeldsEnabled ? string.Empty : null,
					weldTagType,
					assemblyTagType,
					forceFullSheetRefresh ? null : restrictToPartIds,
					hangerTagType,
					ductTagType);
			}
			catch
			{
				// Auto-sync must never interrupt the user's edit session.
			}
		}
	}
}
