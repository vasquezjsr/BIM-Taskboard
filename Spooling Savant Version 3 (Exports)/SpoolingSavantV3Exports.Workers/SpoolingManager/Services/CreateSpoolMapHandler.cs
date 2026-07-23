using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public sealed class CreateSpoolMapHandler : IExternalEventHandler
{
	private const int SpoolMapViewScale = 24;

	private const string SpoolMapFilterNamePrefix = "SS Spool Map - ";

	private const string DiagnosticBuildTag = "2026-07-13-overwrite-delete-recreate";

	private sealed class SpoolMapProcessResult
	{
		public ElementId View3DId { get; set; }

		public ElementId SheetId { get; set; }
	}

	[ThreadStatic]
	private static string _lastTransactionFailureSummary;

	private enum SheetPlacement
	{
		Left,
		Right
	}

	private static readonly BuiltInCategory[] PackageFilterCategories =
	{
		BuiltInCategory.OST_Assemblies,
		BuiltInCategory.OST_FabricationPipework
	};

	public CreateSpoolMapRequest PendingRequest { get; set; }

	public void Execute(UIApplication app)
	{
		if (app?.Application != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}

		CreateSpoolMapRequest request = PendingRequest;
		PendingRequest = null;
		if (request == null)
		{
			RevitRequestBridge.ShowOperationSummary("Create Spool Map", "Create Spool Map did not receive a valid request. Close and reopen SS Manager, then try again.");
			return;
		}
		if (request.ProductKind != SpoolingManagerKind.Standard)
		{
			RevitRequestBridge.ShowOperationSummary("Create Spool Map", "Create Spool Map is only available in SS Manager.");
			return;
		}
		if (request.AssemblyIds == null || request.AssemblyIds.Count == 0)
		{
			RevitRequestBridge.ShowOperationSummary("Create Spool Map", "The selected package has no assemblies.");
			return;
		}

		UIDocument uidoc = app?.ActiveUIDocument;
		Document doc = uidoc?.Document;
		if (doc == null)
		{
			RevitRequestBridge.ShowOperationSummary("Create Spool Map", "No active Revit document was found.");
			return;
		}

		StringBuilder report = new StringBuilder();
		_lastTransactionFailureSummary = null;
		try
		{
			FamilySymbol titleBlock = FindTitleBlock(doc, request.TitleBlockName);
			if (titleBlock == null)
			{
				throw new InvalidOperationException("Title block '" + request.TitleBlockName + "' was not found.");
			}
			View viewTemplate3D = FindViewTemplate(doc, request.ViewTemplate3DName);
			if (viewTemplate3D == null)
			{
				throw new InvalidOperationException("3D view template '" + request.ViewTemplate3DName + "' was not found.");
			}
			View viewTemplatePlan = FindViewTemplate(doc, request.ViewTemplatePlanName);
			if (viewTemplatePlan == null)
			{
				throw new InvalidOperationException("Floor plan view template '" + request.ViewTemplatePlanName + "' was not found.");
			}
			FamilySymbol assemblyTagType = CreateSpoolSheetsHandler.FindTagType(doc, request.AssemblyTagTypeName);
			if (assemblyTagType == null)
			{
				throw new InvalidOperationException("Assembly tag type '" + request.AssemblyTagTypeName + "' was not found.");
			}

			List<AssemblyInstance> assemblies = request.AssemblyIds
				.Select(id => doc.GetElement(id))
				.OfType<AssemblyInstance>()
				.ToList();
			if (assemblies.Count == 0)
			{
				throw new InvalidOperationException("No valid assembly instances were found for the selected package.");
			}

			SpoolMapProcessResult processResult;
			using (Transaction tx = new Transaction(doc, "SS Manager: Create Spool Map"))
			{
				FailureHandlingOptions failureOptions = tx.GetFailureHandlingOptions();
				failureOptions.SetFailuresPreprocessor(new SpoolMapFailuresPreprocessor());
				failureOptions.SetClearAfterRollback(false);
				tx.SetFailureHandlingOptions(failureOptions);
				tx.Start();
				try
				{
					if (!titleBlock.IsActive)
					{
						try
						{
							titleBlock.Activate();
						}
						catch (Exception ex)
						{
							throw new InvalidOperationException("Failed while activating title block '" + request.TitleBlockName + "'.", ex);
						}
						try
						{
							doc.Regenerate();
						}
						catch (Exception ex)
						{
							throw new InvalidOperationException("Failed while regenerating after title block activation.", ex);
						}
					}

					processResult = ProcessPackage(
						doc,
						uidoc,
						request,
						titleBlock,
						viewTemplate3D,
						viewTemplatePlan,
						assemblies,
						report);
					try
					{
						tx.Commit();
					}
					catch (Exception ex)
					{
						throw new InvalidOperationException("Failed while committing Create Spool Map changes.", ex);
					}
				}
				catch
				{
					if (tx.GetStatus() == TransactionStatus.Started)
					{
						tx.RollBack();
					}
					throw;
				}
			}

			using (Transaction tagTx = new Transaction(doc, "SS Manager: Create Spool Map Tags"))
			{
				FailureHandlingOptions tagFailureOptions = tagTx.GetFailureHandlingOptions();
				tagFailureOptions.SetFailuresPreprocessor(new SpoolMapFailuresPreprocessor());
				tagFailureOptions.SetClearAfterRollback(false);
				tagTx.SetFailureHandlingOptions(tagFailureOptions);
				tagTx.Start();
				try
				{
					View3D view3D = doc.GetElement(processResult.View3DId) as View3D;
					if (view3D == null)
					{
						throw new InvalidOperationException("The spool map 3D view could not be found after setup.");
					}
					Prepare3DViewForAssemblyTagging(view3D);
					TagPackageAssembliesOn3D(doc, view3D, assemblies, assemblyTagType, report);
					try
					{
						tagTx.Commit();
					}
					catch (Exception ex)
					{
						throw new InvalidOperationException("Failed while committing spool map assembly tags.", ex);
					}
				}
				catch
				{
					if (tagTx.GetStatus() == TransactionStatus.Started)
					{
						tagTx.RollBack();
					}
					throw;
				}
			}

			TryOpenSpoolMapSheet(uidoc, processResult.SheetId);

			RevitRequestBridge.ShowOperationSummary("Create Spool Map", "Spool map created.");
		}
		catch (Exception ex)
		{
			string details = FormatExceptionForUser(ex);
			TryWriteDiagnosticLog(ex);
			RevitRequestBridge.ShowOperationSummary(
				"Create Spool Map",
				"Create Spool Map failed (" + DiagnosticBuildTag + ").\r\n\r\n" + details);
		}
		finally
		{
			_lastTransactionFailureSummary = null;
		}
	}

	private static SpoolMapProcessResult ProcessPackage(
		Document doc,
		UIDocument uidoc,
		CreateSpoolMapRequest request,
		FamilySymbol titleBlock,
		View viewTemplate3D,
		View viewTemplatePlan,
		IList<AssemblyInstance> assemblies,
		StringBuilder report)
	{
		string packageLabel = string.IsNullOrWhiteSpace(request.PackageLabel) ? "Package" : request.PackageLabel.Trim();
		string packageValue = request.PackageValue ?? string.Empty;
		if (string.IsNullOrWhiteSpace(packageValue))
		{
			throw new InvalidOperationException("The selected package has no S-Package value. Assign S-Package on the assemblies, refresh the list, then try again.");
		}

		string view3DName = packageLabel + " - 3D Spool Map";
		string viewPlanName = packageLabel + " - Plan Spool Map";
		string sheetName = packageLabel + " Spool Map";

		if (TryDescribeExistingSpoolMap(doc, packageLabel, packageValue, out string existingDescription))
		{
			if (!request.OverwriteExisting)
			{
				throw new InvalidOperationException(
					"Spool Map already exists for package '" + packageLabel + "'.\r\n\r\n" +
					existingDescription + "\r\n\r\n" +
					"Choose Overwrite when prompted, or delete the existing Spool Map sheet and views first.");
			}

			RunStep("overwrite existing Spool Map sheet and views", () =>
			{
				DeleteExistingSpoolMapAssets(doc, uidoc, packageLabel, packageValue, report);
			});
		}

		Level level = RunStep("resolve a level for the package", () => ResolveLevelForPackage(doc, assemblies));
		if (level == null)
		{
			throw new InvalidOperationException("Could not resolve a level for the package assemblies.");
		}

		View3D view3D = RunStep("create or reuse the 3D spool map view", () => GetOrCreate3DView(doc, view3DName, report));
		ViewPlan viewPlan = RunStep("create or reuse the plan spool map view", () => GetOrCreatePlanView(doc, viewPlanName, level, report));

		RunStep("remove spool map views from any existing sheets", () =>
		{
			RemoveViewFromAllSheets(doc, view3D, report);
			RemoveViewFromAllSheets(doc, viewPlan, report);
		});

		RunStep("apply the 3D view template", () => ApplyViewTemplateParameters(view3D, viewTemplate3D));
		RunStep("apply the plan view template", () => ApplyViewTemplateParameters(viewPlan, viewTemplatePlan));
		RunStep("clear the 3D view template assignment", () => ClearAssignedViewTemplate(view3D));
		RunStep("clear the plan view template assignment", () => ClearAssignedViewTemplate(viewPlan));

		RunStep(
			"sync S-Package on package assemblies for the view filter",
			() => EnsurePackageAssembliesVisibleInFilter(doc, assemblies, packageValue, report));

		ParameterFilterElement packageFilter = RunStep(
			"create the S-Package view filter (Assemblies + MEP Fabrication Pipework)",
			() => GetOrCreatePackageFilter(doc, packageLabel, packageValue, assemblies, report));
		RunStep("apply the package filter to the 3D view", () => ApplyPackageFilter(view3D, packageFilter));
		RunStep("apply the package filter to the plan view", () => ApplyPackageFilter(viewPlan, packageFilter));

		List<Element> contentElements = CollectPackageContentElements(doc, assemblies);

		TrySetViewScale(view3D, SpoolMapViewScale);
		TrySetViewScale(viewPlan, SpoolMapViewScale);

		RunStep("enable the 3D section box", () => EnableSectionBoxForPackageContent(view3D, contentElements, report));
		CropPlanViewToContent(viewPlan, contentElements);

		Prepare3DViewForAssemblyTagging(view3D);

		RunStep("lock the 3D view orientation before assembly tagging", () =>
		{
			if (!view3D.IsLocked)
			{
				view3D.SaveOrientationAndLock();
			}
			report.AppendLine("Locked 3D view orientation.");
		});

		try
		{
			doc.Regenerate();
		}
		catch
		{
		}

		ViewSheet sheet = RunStep("create or reuse the spool map sheet", () => GetOrCreateSheet(doc, sheetName, packageLabel, titleBlock, report));
		RunStep("stamp S-Package on spool map sheet and views", () =>
		{
			StampPackageOnViewOrSheet(view3D, packageValue, report);
			StampPackageOnViewOrSheet(viewPlan, packageValue, report);
			StampPackageOnViewOrSheet(sheet, packageValue, report);
		});
		RunStep("place the 3D view on the sheet", () => PlaceViewport(doc, sheet, view3D, SheetPlacement.Left, report));
		RunStep("place the plan view on the sheet", () => PlaceViewport(doc, sheet, viewPlan, SheetPlacement.Right, report));

		report.AppendLine("Set view scale to 1/2\" = 1'-0\".");

		return new SpoolMapProcessResult
		{
			View3DId = view3D.Id,
			SheetId = sheet.Id
		};
	}

	/// <summary>
	/// Finds the package Spool Map sheet by S-Package value, then by sheet name "{package} Spool Map".
	/// </summary>
	public static ViewSheet FindSpoolMapSheet(Document doc, string packageLabel, string packageValue = null)
	{
		if (doc == null)
		{
			return null;
		}

		string label = (packageLabel ?? string.Empty).Trim();
		string value = (packageValue ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(value))
		{
			value = label;
		}

		string expectedName = string.IsNullOrWhiteSpace(label) ? string.Empty : label + " Spool Map";

		List<ViewSheet> sheets = new FilteredElementCollector(doc)
			.OfClass(typeof(ViewSheet))
			.Cast<ViewSheet>()
			.Where(s => s != null && !s.IsTemplate)
			.ToList();

		if (!string.IsNullOrWhiteSpace(value))
		{
			ViewSheet byParam = sheets.FirstOrDefault(s =>
			{
				string package = FabricationSavantParameterSync.TryGetPackageParameter(s);
				return !string.IsNullOrWhiteSpace(package)
					&& string.Equals(package.Trim(), value, StringComparison.OrdinalIgnoreCase)
					&& (string.IsNullOrWhiteSpace(expectedName)
						|| (s.Name ?? string.Empty).IndexOf("Spool Map", StringComparison.OrdinalIgnoreCase) >= 0);
			});
			if (byParam != null)
			{
				return byParam;
			}
		}

		if (!string.IsNullOrWhiteSpace(expectedName))
		{
			return sheets.FirstOrDefault(s =>
				string.Equals(s.Name, expectedName, StringComparison.OrdinalIgnoreCase));
		}

		return null;
	}

	/// <summary>
	/// Returns true when a Spool Map sheet and/or named views already exist for the package.
	/// </summary>
	public static bool TryDescribeExistingSpoolMap(
		Document doc,
		string packageLabel,
		string packageValue,
		out string description)
	{
		description = null;
		if (doc == null)
		{
			return false;
		}

		string label = string.IsNullOrWhiteSpace(packageLabel) ? "Package" : packageLabel.Trim();
		string value = packageValue ?? string.Empty;
		string view3DName = label + " - 3D Spool Map";
		string viewPlanName = label + " - Plan Spool Map";

		ViewSheet sheet = FindSpoolMapSheet(doc, label, value);
		View3D view3D = FindNamedView3D(doc, view3DName);
		ViewPlan viewPlan = FindNamedViewPlan(doc, viewPlanName);
		if (sheet == null && view3D == null && viewPlan == null)
		{
			return false;
		}

		List<string> parts = new List<string>();
		if (sheet != null)
		{
			parts.Add("Sheet '" + sheet.Name + "' (" + sheet.SheetNumber + ")");
		}
		if (view3D != null)
		{
			parts.Add("3D view '" + view3D.Name + "'");
		}
		if (viewPlan != null)
		{
			parts.Add("Plan view '" + viewPlan.Name + "'");
		}
		description = string.Join("\n", parts);
		return true;
	}

	private static void DeleteExistingSpoolMapAssets(
		Document doc,
		UIDocument uidoc,
		string packageLabel,
		string packageValue,
		StringBuilder report)
	{
		if (doc == null)
		{
			return;
		}

		string label = string.IsNullOrWhiteSpace(packageLabel) ? "Package" : packageLabel.Trim();
		string value = packageValue ?? string.Empty;
		string view3DName = label + " - 3D Spool Map";
		string viewPlanName = label + " - Plan Spool Map";

		ViewSheet sheet = FindSpoolMapSheet(doc, label, value);
		View3D view3D = FindNamedView3D(doc, view3DName);
		ViewPlan viewPlan = FindNamedViewPlan(doc, viewPlanName);

		// Capture ids + labels before any delete — Element wrappers are invalid after Delete.
		List<ElementId> idsToDelete = new List<ElementId>();
		List<string> deletedLabels = new List<string>();
		HashSet<ElementId> deleteSet = new HashSet<ElementId>();

		void QueueDelete(Element element, string description)
		{
			if (element == null || element.Id == ElementId.InvalidElementId)
			{
				return;
			}
			if (!deleteSet.Add(element.Id))
			{
				return;
			}
			idsToDelete.Add(element.Id);
			deletedLabels.Add(description);
		}

		if (sheet != null)
		{
			QueueDelete(sheet, "sheet '" + sheet.Name + "' (" + sheet.SheetNumber + ")");
		}
		if (view3D != null)
		{
			try
			{
				if (view3D.IsLocked)
				{
					view3D.Unlock();
				}
			}
			catch
			{
			}
			QueueDelete(view3D, "3D view '" + view3D.Name + "'");
		}
		if (viewPlan != null)
		{
			QueueDelete(viewPlan, "plan view '" + viewPlan.Name + "'");
		}

		if (idsToDelete.Count == 0)
		{
			return;
		}

		LeaveViewsIfActive(uidoc, deleteSet);

		// Sheet first (drops viewports), then views — all by ElementId only.
		ICollection<ElementId> deleted = doc.Delete(idsToDelete);
		int count = deleted?.Count ?? idsToDelete.Count;
		foreach (string deletedLabel in deletedLabels)
		{
			report?.AppendLine("Deleted existing Spool Map " + deletedLabel + ".");
		}
		report?.AppendLine(
			"Overwrite removed " + count + " existing Spool Map element(s) for '" + label +
			"'. Recreating sheet and views.");
	}

	private static void LeaveViewsIfActive(UIDocument uidoc, HashSet<ElementId> idsBeingDeleted)
	{
		if (uidoc == null || idsBeingDeleted == null || idsBeingDeleted.Count == 0)
		{
			return;
		}

		Document doc = uidoc.Document;
		View active = uidoc.ActiveView;
		if (active == null || !idsBeingDeleted.Contains(active.Id))
		{
			return;
		}

		View safe = new FilteredElementCollector(doc)
			.OfClass(typeof(View))
			.Cast<View>()
			.FirstOrDefault(v =>
				v != null
				&& !v.IsTemplate
				&& !idsBeingDeleted.Contains(v.Id)
				&& v.CanBePrinted
				&& (v is View3D || v is ViewPlan || v is ViewSheet));
		if (safe == null)
		{
			return;
		}

		try
		{
			uidoc.RequestViewChange(safe);
		}
		catch
		{
			try
			{
				uidoc.ActiveView = safe;
			}
			catch
			{
			}
		}
	}

	private static View3D FindNamedView3D(Document doc, string viewName)
	{
		if (doc == null || string.IsNullOrWhiteSpace(viewName))
		{
			return null;
		}
		return new FilteredElementCollector(doc)
			.OfClass(typeof(View3D))
			.Cast<View3D>()
			.FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
	}

	private static ViewPlan FindNamedViewPlan(Document doc, string viewName)
	{
		if (doc == null || string.IsNullOrWhiteSpace(viewName))
		{
			return null;
		}
		return new FilteredElementCollector(doc)
			.OfClass(typeof(ViewPlan))
			.Cast<ViewPlan>()
			.FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
	}

	private static void StampPackageOnViewOrSheet(Element element, string packageValue, StringBuilder report)
	{
		if (element == null || string.IsNullOrWhiteSpace(packageValue))
		{
			return;
		}

		if (FabricationSavantParameterSync.TrySetPackageParameter(element, packageValue.Trim()))
		{
			report.AppendLine(
				"Set S-Package='" + packageValue.Trim() + "' on " +
				(element is ViewSheet ? "sheet" : "view") +
				" '" + (element.Name ?? element.Id.ToString()) + "'.");
		}
		else
		{
			report.AppendLine(
				"Could not set S-Package on " +
				(element is ViewSheet ? "sheet" : "view") +
				" '" + (element.Name ?? element.Id.ToString()) +
				"' (parameter missing or read-only — reopen the model so shared parameters bind to Sheets/Views).");
		}
	}

	private static void TryOpenSpoolMapSheet(UIDocument uidoc, ElementId sheetId)
	{
		if (uidoc == null || sheetId == null || sheetId == ElementId.InvalidElementId)
		{
			return;
		}
		ViewSheet sheet = uidoc.Document?.GetElement(sheetId) as ViewSheet;
		if (sheet == null)
		{
			return;
		}
		try
		{
			uidoc.RequestViewChange(sheet);
		}
		catch
		{
			try
			{
				uidoc.ActiveView = sheet;
			}
			catch
			{
			}
		}
	}

	private static T RunStep<T>(string stepDescription, Func<T> action)
	{
		try
		{
			return action();
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException("Failed while " + stepDescription + ".\r\n\r\n" + FormatExceptionForUser(ex), ex);
		}
	}

	private static void RunStep(string stepDescription, Action action)
	{
		RunStep(stepDescription, () =>
		{
			action();
			return true;
		});
	}

	private static void EnsurePackageAssembliesVisibleInFilter(
		Document doc,
		IList<AssemblyInstance> assemblies,
		string packageValue,
		StringBuilder report)
	{
		if (doc == null || assemblies == null || assemblies.Count == 0 || string.IsNullOrWhiteSpace(packageValue))
		{
			return;
		}

		var app = doc.Application;
		int assembliesUpdated = 0;
		foreach (AssemblyInstance assembly in assemblies)
		{
			if (assembly == null)
			{
				continue;
			}
			FabricationSavantParameterSync.EnsurePackageParameterForAssembly(app, doc, assembly);
			string before = FabricationSavantParameterSync.TryGetAssemblyPackageValue(doc, assembly);
			FabricationSavantParameterSync.ApplyPackageToMembersWithoutValue(doc, assembly, packageValue);
			string after = FabricationSavantParameterSync.TryGetAssemblyPackageValue(doc, assembly);
			if (string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after))
			{
				assembliesUpdated++;
			}
		}

		if (assembliesUpdated > 0)
		{
			report?.AppendLine(
				"Set S-Package on " + assembliesUpdated + " assembly instance(s) so the package filter keeps them visible.");
		}

		try
		{
			doc.Regenerate();
		}
		catch
		{
		}
	}

	private static void Prepare3DViewForAssemblyTagging(View3D view3D)
	{
		if (view3D == null)
		{
			return;
		}
		try
		{
			view3D.AreAnnotationCategoriesHidden = false;
		}
		catch
		{
		}
		TrySetViewCategoryVisible(view3D, BuiltInCategory.OST_AssemblyTags);
		TrySetViewCategoryVisible(view3D, BuiltInCategory.OST_Assemblies);
		TrySetViewCategoryVisible(view3D, BuiltInCategory.OST_MultiCategoryTags);
	}

	private static void TrySetViewCategoryVisible(View view, BuiltInCategory category)
	{
		if (view == null)
		{
			return;
		}
		try
		{
			ElementId categoryId = new ElementId(category);
			if (view.CanCategoryBeHidden(categoryId))
			{
				view.SetCategoryHidden(categoryId, false);
			}
		}
		catch
		{
		}
	}

	private static View3D GetOrCreate3DView(Document doc, string viewName, StringBuilder report)
	{
		View3D existing = FindNamedView3D(doc, viewName);
		if (existing != null)
		{
			if (existing.IsLocked)
			{
				RemoveViewFromAllSheets(doc, existing, report);
				ElementId lockedId = existing.Id;
				doc.Delete(lockedId);
				report.AppendLine("Recreated locked 3D view '" + viewName + "'.");
			}
			else
			{
				report.AppendLine("Reused 3D view '" + viewName + "'.");
				return existing;
			}
		}

		ViewFamilyType viewFamilyType = FindViewFamilyType(doc, ViewFamily.ThreeDimensional);
		if (viewFamilyType == null)
		{
			throw new InvalidOperationException("No 3D view family type was found.");
		}

		View3D created = View3D.CreateIsometric(doc, viewFamilyType.Id);
		created.Name = viewName;
		report.AppendLine("Created 3D view '" + viewName + "'.");
		return created;
	}

	private static ViewPlan GetOrCreatePlanView(Document doc, string viewName, Level level, StringBuilder report)
	{
		ViewPlan existing = FindNamedViewPlan(doc, viewName);
		if (existing != null)
		{
			report.AppendLine("Reused plan view '" + viewName + "'.");
			return existing;
		}

		ViewFamilyType viewFamilyType = FindViewFamilyType(doc, ViewFamily.FloorPlan);
		if (viewFamilyType == null)
		{
			throw new InvalidOperationException("No floor plan view family type was found.");
		}

		ViewPlan created = ViewPlan.Create(doc, viewFamilyType.Id, level.Id);
		created.Name = viewName;
		report.AppendLine("Created plan view '" + viewName + "' on level '" + level.Name + "'.");
		return created;
	}

	private static ViewSheet GetOrCreateSheet(Document doc, string sheetName, string packageLabel, FamilySymbol titleBlock, StringBuilder report)
	{
		ViewSheet existing = new FilteredElementCollector(doc)
			.OfClass(typeof(ViewSheet))
			.Cast<ViewSheet>()
			.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase));
		if (existing != null)
		{
			existing.Name = sheetName;
			report.AppendLine("Reused sheet '" + sheetName + "' (" + existing.SheetNumber + ").");
			return existing;
		}

		string sheetNumber = BuildUniqueSheetNumber(doc, packageLabel);
		ViewSheet created = ViewSheet.Create(doc, titleBlock.Id);
		created.SheetNumber = sheetNumber;
		created.Name = sheetName;
		report.AppendLine("Created sheet '" + sheetName + "' (" + sheetNumber + ").");
		return created;
	}

	private static string BuildUniqueSheetNumber(Document doc, string packageLabel)
	{
		string stem = SanitizeSheetToken(packageLabel);
		if (stem.Length == 0)
		{
			stem = "PKG";
		}
		string candidate = "SM-" + stem;
		if (candidate.Length > 20)
		{
			candidate = candidate.Substring(0, 20);
		}
		HashSet<string> used = new HashSet<string>(
			new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber),
			StringComparer.OrdinalIgnoreCase);
		if (!used.Contains(candidate))
		{
			return candidate;
		}
		for (int i = 2; i < 100; i++)
		{
			string numbered = candidate;
			if (numbered.Length > 18)
			{
				numbered = numbered.Substring(0, 18);
			}
			string tryNumber = numbered + i.ToString();
			if (!used.Contains(tryNumber))
			{
				return tryNumber;
			}
		}
		return "SM-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
	}

	private static string SanitizeSheetToken(string value)
	{
		string text = (value ?? string.Empty).Trim();
		if (text.Equals("(No package)", StringComparison.OrdinalIgnoreCase))
		{
			return "NOPKG";
		}
		StringBuilder builder = new StringBuilder(text.Length);
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				builder.Append(char.ToUpperInvariant(c));
			}
			else if (c == '-' || c == '_')
			{
				builder.Append(c);
			}
		}
		return builder.ToString();
	}

	private static ParameterFilterElement GetOrCreatePackageFilter(
		Document doc,
		string filterName,
		string packageValue,
		IList<AssemblyInstance> assemblies,
		StringBuilder report)
	{
		string trimmedName = BuildPackageFilterName(filterName);
		Element parameterSample = FindPackageParameterSample(doc, assemblies);
		ElementId parameterId = ResolvePackageFilterParameterId(doc, parameterSample);
		if (parameterId == null || parameterId == ElementId.InvalidElementId)
		{
			throw new InvalidOperationException("Could not find the string parameter '" + SsSavantSharedParameterBootstrap.PackageParameterName + "' on an assembly or fabrication element in the selected package.");
		}

		IList<ElementId> categories = ResolvePackageFilterCategories(doc);
		if (categories == null || categories.Count == 0)
		{
			throw new InvalidOperationException("No supported categories were found for the S-Package view filter.");
		}
		report?.AppendLine("Package filter categories: " + DescribeFilterCategories(doc, categories) + ".");

		ParameterFilterElement existing = new FilteredElementCollector(doc)
			.OfClass(typeof(ParameterFilterElement))
			.Cast<ParameterFilterElement>()
			.FirstOrDefault(f => string.Equals(f.Name, trimmedName, StringComparison.OrdinalIgnoreCase));
		if (existing != null)
		{
			RemoveFilterFromAllViews(doc, existing.Id);
			try
			{
				doc.Delete(existing.Id);
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException(
					"Could not replace the existing view filter '" + trimmedName + "'. Remove that filter in Manage > Filters (or from the spool map views), then try again."
					+ (string.IsNullOrWhiteSpace(ex.Message) ? string.Empty : " " + ex.Message));
			}
		}

		FilterRule rule;
		try
		{
			rule = ParameterFilterRuleFactory.CreateNotContainsRule(parameterId, packageValue.Trim());
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException(
				"Could not build the S-Package 'does not contain' filter rule for value '" + packageValue + "'. "
				+ FormatExceptionForUser(ex),
				ex);
		}

		ElementParameterFilter elementFilter = new ElementParameterFilter(rule);
		ICollection<ElementId> categoryCollection = categories as ICollection<ElementId> ?? categories.ToList();
		try
		{
			ParameterFilterElement created = ParameterFilterElement.Create(doc, trimmedName, categoryCollection, elementFilter);
			report?.AppendLine("Created package filter '" + trimmedName + "'.");
			return created;
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException(BuildFilterCreateError(doc, trimmedName, packageValue, categories, ex), ex);
		}
	}

	private static ElementId ResolvePackageFilterParameterId(Document doc, Element parameterSample)
	{
		Parameter packageParameter = parameterSample?.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
		if (packageParameter != null && packageParameter.StorageType == StorageType.String && packageParameter.Id != ElementId.InvalidElementId)
		{
			return packageParameter.Id;
		}

		return ElementId.InvalidElementId;
	}

	private static string BuildFilterCreateError(Document doc, string filterName, string packageValue, IList<ElementId> categories, Exception ex)
	{
		StringBuilder builder = new StringBuilder();
		builder.Append("Could not create view filter '").Append(filterName).Append("'.");
		builder.Append(" Rule: S-Package does not contain '").Append(packageValue ?? string.Empty).Append("'.");
		builder.Append(" Categories checked: ").Append(DescribeFilterCategories(doc, categories)).Append('.');
		if (ex is ArgumentException argumentException && !string.IsNullOrWhiteSpace(argumentException.ParamName))
		{
			builder.Append(" Argument: ").Append(argumentException.ParamName).Append('.');
		}
		string detail = FormatExceptionForUser(ex);
		if (!string.IsNullOrWhiteSpace(detail))
		{
			builder.Append(' ').Append(detail);
		}
		return builder.ToString();
	}

	private static string BuildPackageFilterName(string packageLabel)
	{
		string label = (packageLabel ?? string.Empty).Trim();
		if (label.Length == 0)
		{
			label = "Package";
		}
		return SpoolMapFilterNamePrefix + label;
	}

	private static IList<ElementId> ResolvePackageFilterCategories(Document doc)
	{
		List<ElementId> categoryIds = new List<ElementId>();
		foreach (BuiltInCategory builtInCategory in PackageFilterCategories)
		{
			ElementId categoryId = new ElementId(builtInCategory);
			if (!categoryIds.Any(id => id.Value == categoryId.Value))
			{
				categoryIds.Add(categoryId);
			}
		}
		return categoryIds;
	}

	private static string DescribeFilterCategories(Document doc, IList<ElementId> categories)
	{
		if (categories == null || categories.Count == 0)
		{
			return "(none)";
		}
		List<string> names = new List<string>();
		foreach (ElementId categoryId in categories)
		{
			string name = categoryId.Value.ToString();
			try
			{
				Category category = Category.GetCategory(doc, categoryId);
				if (!string.IsNullOrWhiteSpace(category?.Name))
				{
					name = category.Name;
				}
			}
			catch
			{
			}
			names.Add(name);
		}
		return string.Join(", ", names);
	}

	private static void RemoveFilterFromAllViews(Document doc, ElementId filterId)
	{
		if (doc == null || filterId == ElementId.InvalidElementId)
		{
			return;
		}
		foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
		{
			if (view == null || view.IsTemplate)
			{
				continue;
			}
			try
			{
				if (ViewHasFilter(view, filterId))
				{
					view.RemoveFilter(filterId);
				}
			}
			catch
			{
			}
		}
	}

	private static Element FindPackageParameterSample(Document doc, IList<AssemblyInstance> assemblies)
	{
		if (assemblies != null)
		{
			foreach (AssemblyInstance assembly in assemblies)
			{
				if (assembly == null)
				{
					continue;
				}
				foreach (ElementId memberId in assembly.GetMemberIds())
				{
					Element member = doc.GetElement(memberId);
					if (member?.Category?.Id.Value != (long)BuiltInCategory.OST_FabricationPipework)
					{
						continue;
					}
					Parameter parameter = member.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
					if (parameter != null && parameter.StorageType == StorageType.String)
					{
						return member;
					}
				}
			}
			foreach (AssemblyInstance assembly in assemblies)
			{
				if (assembly == null)
				{
					continue;
				}
				foreach (ElementId memberId in assembly.GetMemberIds())
				{
					Element member = doc.GetElement(memberId);
					Parameter parameter = member?.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
					if (parameter != null && parameter.StorageType == StorageType.String)
					{
						return member;
					}
				}
				Parameter assemblyParameter = assembly.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
				if (assemblyParameter != null && assemblyParameter.StorageType == StorageType.String)
				{
					return assembly;
				}
			}
		}

		Element fabricationSample = new FilteredElementCollector(doc)
			.OfCategory(BuiltInCategory.OST_FabricationPipework)
			.WhereElementIsNotElementType()
			.FirstOrDefault(e =>
			{
				Parameter parameter = e.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
				return parameter != null && parameter.StorageType == StorageType.String;
			});
		if (fabricationSample != null)
		{
			return fabricationSample;
		}

		return new FilteredElementCollector(doc)
			.OfCategory(BuiltInCategory.OST_FabricationContainment)
			.WhereElementIsNotElementType()
			.FirstOrDefault(e =>
			{
				Parameter parameter = e.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
				return parameter != null && parameter.StorageType == StorageType.String;
			});
	}

	private static void ApplyPackageFilter(View view, ParameterFilterElement filter)
	{
		if (view == null || filter == null)
		{
		 return;
		}
		ElementId filterId = filter.Id;
		if (!ViewHasFilter(view, filterId))
		{
			view.AddFilter(filterId);
		}
		view.SetFilterVisibility(filterId, false);
	}

	private static bool ViewHasFilter(View view, ElementId filterId)
	{
		if (view == null || filterId == null || filterId == ElementId.InvalidElementId)
		{
			return false;
		}
		return view.GetFilters().Any(id => id.Value == filterId.Value);
	}

	private static void ApplyViewTemplateParameters(View view, View template)
	{
		if (view == null || template == null)
		{
			return;
		}
		ClearAssignedViewTemplate(view);
		view.ApplyViewTemplateParameters(template);
	}

	private static void ClearAssignedViewTemplate(View view)
	{
		if (view == null || view.ViewTemplateId == ElementId.InvalidElementId)
		{
			return;
		}
		try
		{
			view.ViewTemplateId = ElementId.InvalidElementId;
		}
		catch
		{
		}
	}

	private static void TrySetViewScale(View view, int scale)
	{
		if (view == null || scale <= 0)
		{
			return;
		}
		try
		{
			view.Scale = scale;
		}
		catch
		{
		}
	}

	private static List<Element> CollectPackageContentElements(Document doc, IList<AssemblyInstance> assemblies)
	{
		HashSet<ElementId> ids = new HashSet<ElementId>();
		List<Element> elements = new List<Element>();
		foreach (AssemblyInstance assembly in assemblies)
		{
			if (assembly == null)
			{
				continue;
			}
			if (ids.Add(assembly.Id))
			{
				elements.Add(assembly);
			}
			foreach (ElementId memberId in assembly.GetMemberIds())
			{
				if (!ids.Add(memberId))
				{
					continue;
				}
				Element member = doc.GetElement(memberId);
				if (member != null)
				{
					elements.Add(member);
				}
			}
		}
		return elements;
	}

	private static void EnableSectionBoxForPackageContent(View3D view3D, IList<Element> elements, StringBuilder report)
	{
		if (view3D == null)
		{
			return;
		}

		if (elements == null || elements.Count == 0 || !TryGetCombinedBoundingBox(elements, out BoundingBoxXYZ combined))
		{
			view3D.IsSectionBoxActive = true;
			report?.AppendLine("Enabled 3D section box.");
			return;
		}

		double span = Math.Max(
			combined.Max.X - combined.Min.X,
			Math.Max(combined.Max.Y - combined.Min.Y, combined.Max.Z - combined.Min.Z));
		double margin = Math.Max(1.0 / 12.0, span * 0.1);
		BoundingBoxXYZ sectionBox = new BoundingBoxXYZ
		{
			Transform = Transform.Identity,
			Min = new XYZ(
				combined.Min.X - margin,
				combined.Min.Y - margin,
				combined.Min.Z - margin),
			Max = new XYZ(
				combined.Max.X + margin,
				combined.Max.Y + margin,
				combined.Max.Z + margin)
		};
		view3D.IsSectionBoxActive = true;
		view3D.SetSectionBox(sectionBox);
		report?.AppendLine("Enabled 3D section box around package content.");
	}

	private static void CropPlanViewToContent(ViewPlan viewPlan, IList<Element> elements)
	{
		if (viewPlan == null || elements == null || elements.Count == 0 || !TryGetCombinedBoundingBox(elements, out BoundingBoxXYZ combined))
		{
			return;
		}
		try
		{
			viewPlan.CropBoxActive = true;
		}
		catch
		{
			return;
		}
		BoundingBoxXYZ cropBox = viewPlan.CropBox;
		if (cropBox?.Transform == null)
		{
			return;
		}
		Transform inverse = cropBox.Transform.Inverse;
		if (!TryProjectBoundsToLocal(combined, inverse, out XYZ localMin, out XYZ localMax))
		{
			return;
		}
		double margin = Math.Max(1.0 / 12.0, Math.Max(localMax.X - localMin.X, localMax.Y - localMin.Y) * 0.1);
		BoundingBoxXYZ next = new BoundingBoxXYZ
		{
			Transform = cropBox.Transform,
			Min = new XYZ(localMin.X - margin, localMin.Y - margin, cropBox.Min.Z),
			Max = new XYZ(localMax.X + margin, localMax.Y + margin, cropBox.Max.Z)
		};
		try
		{
			viewPlan.CropBox = next;
		}
		catch
		{
		}
	}

	private static void TagPackageAssembliesOn3D(
		Document doc,
		View3D view3D,
		IList<AssemblyInstance> assemblies,
		FamilySymbol assemblyTagType,
		StringBuilder report)
	{
		int created = 0;
		int skipped = 0;
		int tagIndex = 0;
		foreach (AssemblyInstance assembly in assemblies)
		{
			if (assembly == null)
			{
				continue;
			}
			if (TryCreateAssemblyTag(doc, view3D, assembly, assemblyTagType, tagIndex))
			{
				created++;
				tagIndex++;
			}
			else
			{
				skipped++;
			}
		}
		report.AppendLine("Tagged " + created + " assemblies on the 3D view" + (skipped > 0 ? " (" + skipped + " skipped)." : ".") + " (" + CountAssemblyTagsInView(doc, view3D) + " tags visible in view).");
	}

	private static int CountAssemblyTagsInView(Document doc, View3D view3D)
	{
		if (doc == null || view3D == null)
		{
			return 0;
		}
		try
		{
			return new FilteredElementCollector(doc, view3D.Id)
				.OfClass(typeof(IndependentTag))
				.GetElementCount();
		}
		catch
		{
			return 0;
		}
	}

	private static bool TryCreateAssemblyTag(
		Document doc,
		View3D view3D,
		AssemblyInstance assembly,
		FamilySymbol assemblyTagType,
		int tagIndex)
	{
		if (doc == null || view3D == null || assembly == null || assemblyTagType == null)
		{
			return false;
		}
		RemoveExistingAssemblyTags(doc, view3D, assembly, assemblyTagType);
		return CreateSpoolSheetsHandler.TryCreateSpoolMapAssemblyTag(doc, view3D, assembly, assemblyTagType, tagIndex);
	}

	private static void RemoveExistingAssemblyTags(Document doc, View3D view3D, AssemblyInstance assembly, FamilySymbol assemblyTagType)
	{
		List<ElementId> tagIds = new FilteredElementCollector(doc, view3D.Id)
			.OfClass(typeof(IndependentTag))
			.Cast<IndependentTag>()
			.Where(tag =>
			{
				try
				{
					ISet<ElementId> taggedIds = tag.GetTaggedLocalElementIds();
					return taggedIds != null
						&& taggedIds.Contains(assembly.Id)
						&& tag.GetTypeId() == assemblyTagType.Id;
				}
				catch
				{
					return false;
				}
			})
			.Select(tag => tag.Id)
			.ToList();
		if (tagIds.Count > 0)
		{
			doc.Delete(tagIds);
		}
	}

	private static void PlaceViewport(Document doc, ViewSheet sheet, View view, SheetPlacement placement, StringBuilder report)
	{
		if (view == null)
		{
			return;
		}
		ReleaseViewFromOtherSheets(doc, sheet, view, report);
		Viewport existing = new FilteredElementCollector(doc, sheet.Id)
			.OfClass(typeof(Viewport))
			.Cast<Viewport>()
			.FirstOrDefault(vp => vp.ViewId == view.Id);
		if (existing != null)
		{
			doc.Delete(existing.Id);
			existing = null;
			report.AppendLine("Replaced existing viewport for '" + view.Name + "' on the spool map sheet.");
		}
		if (existing == null)
		{
			if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
			{
				throw new InvalidOperationException("View '" + view.Name + "' cannot be placed on sheet '" + sheet.SheetNumber + "'. It may be open in another layout, on another sheet, or disallowed by the view type.");
			}
			XYZ initialPoint = GetSheetTargetPoint(doc, sheet, placement);
			existing = Viewport.Create(doc, sheet.Id, view.Id, initialPoint);
			if (existing == null)
			{
				throw new InvalidOperationException("Could not place view '" + view.Name + "' on sheet '" + sheet.SheetNumber + "'.");
			}
			report.AppendLine("Placed '" + view.Name + "' on sheet.");
		}
		MoveViewportToTarget(doc, sheet, existing, placement);
	}

	private static void ReleaseViewFromOtherSheets(Document doc, ViewSheet targetSheet, View view, StringBuilder report)
	{
		if (doc == null || targetSheet == null || view == null)
		{
			return;
		}
		List<ElementId> viewportIds = new List<ElementId>();
		List<string> sheetLabels = new List<string>();
		foreach (Viewport viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList())
		{
			if (viewport.ViewId != view.Id || viewport.SheetId == targetSheet.Id)
			{
				continue;
			}
			ViewSheet otherSheet = doc.GetElement(viewport.SheetId) as ViewSheet;
			string otherSheetLabel = otherSheet != null ? otherSheet.SheetNumber : viewport.SheetId.Value.ToString();
			viewportIds.Add(viewport.Id);
			sheetLabels.Add(otherSheetLabel);
		}
		for (int i = 0; i < viewportIds.Count; i++)
		{
			doc.Delete(viewportIds[i]);
			report.AppendLine("Removed '" + view.Name + "' from sheet " + sheetLabels[i] + " so it could be placed on the spool map.");
		}
	}

	private static void RemoveViewFromAllSheets(Document doc, View view, StringBuilder report)
	{
		if (doc == null || view == null)
		{
			return;
		}
		List<ElementId> viewportIds = new List<ElementId>();
		List<string> sheetLabels = new List<string>();
		foreach (Viewport viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList())
		{
			if (viewport.ViewId != view.Id)
			{
				continue;
			}
			ViewSheet sheet = doc.GetElement(viewport.SheetId) as ViewSheet;
			string sheetLabel = sheet != null ? sheet.SheetNumber : viewport.SheetId.Value.ToString();
			viewportIds.Add(viewport.Id);
			sheetLabels.Add(sheetLabel);
		}
		for (int i = 0; i < viewportIds.Count; i++)
		{
			doc.Delete(viewportIds[i]);
			report.AppendLine("Removed '" + view.Name + "' from sheet " + sheetLabels[i] + ".");
		}
	}

	private static void MoveViewportToTarget(Document doc, ViewSheet sheet, Viewport viewport, SheetPlacement placement)
	{
		BoundingBoxXYZ box = viewport.get_BoundingBox(sheet);
		if (box == null)
		{
			return;
		}
		XYZ center = (box.Min + box.Max) * 0.5;
		XYZ target = GetSheetTargetPoint(doc, sheet, placement);
		ElementTransformUtils.MoveElement(doc, viewport.Id, target - center);
	}

	private static XYZ GetSheetTargetPoint(Document doc, ViewSheet sheet, SheetPlacement placement)
	{
		BoundingBoxXYZ titleBlockBox = GetTitleBlockBoundingBox(doc, sheet);
		if (titleBlockBox != null)
		{
			double width = titleBlockBox.Max.X - titleBlockBox.Min.X;
			double height = titleBlockBox.Max.Y - titleBlockBox.Min.Y;
			double xFactor = placement == SheetPlacement.Left ? 0.28 : 0.72;
			double yFactor = 0.42;
			return new XYZ(titleBlockBox.Min.X + width * xFactor, titleBlockBox.Min.Y + height * yFactor, 0.0);
		}
		BoundingBoxUV outline = sheet.Outline;
		double outlineWidth = outline.Max.U - outline.Min.U;
		double outlineHeight = outline.Max.V - outline.Min.V;
		double xFactor2 = placement == SheetPlacement.Left ? 0.28 : 0.72;
		return new XYZ(outline.Min.U + outlineWidth * xFactor2, outline.Min.V + outlineHeight * 0.42, 0.0);
	}

	private static BoundingBoxXYZ GetTitleBlockBoundingBox(Document doc, ViewSheet sheet)
	{
		FamilyInstance titleBlock = new FilteredElementCollector(doc, sheet.Id)
			.OfCategory(BuiltInCategory.OST_TitleBlocks)
			.WhereElementIsNotElementType()
			.Cast<FamilyInstance>()
			.FirstOrDefault();
		return titleBlock?.get_BoundingBox(sheet);
	}

	private static Level ResolveLevelForPackage(Document doc, IList<AssemblyInstance> assemblies)
	{
		Dictionary<ElementId, int> levelCounts = new Dictionary<ElementId, int>();
		foreach (AssemblyInstance assembly in assemblies)
		{
			foreach (ElementId memberId in assembly.GetMemberIds())
			{
				Element member = doc.GetElement(memberId);
				if (member?.LevelId == null || member.LevelId == ElementId.InvalidElementId)
				{
					continue;
				}
				levelCounts.TryGetValue(member.LevelId, out int count);
				levelCounts[member.LevelId] = count + 1;
			}
		}
		if (levelCounts.Count > 0)
		{
			ElementId bestLevelId = levelCounts.OrderByDescending(kvp => kvp.Value).First().Key;
			return doc.GetElement(bestLevelId) as Level;
		}
		return new FilteredElementCollector(doc)
			.OfClass(typeof(Level))
			.Cast<Level>()
			.OrderBy(l => l.Elevation)
			.FirstOrDefault();
	}

	private static bool TryGetCombinedBoundingBox(IList<Element> elements, out BoundingBoxXYZ combined)
	{
		combined = null;
		bool hasBounds = false;
		double minX = 0.0;
		double minY = 0.0;
		double minZ = 0.0;
		double maxX = 0.0;
		double maxY = 0.0;
		double maxZ = 0.0;
		foreach (Element element in elements)
		{
			BoundingBoxXYZ box = element.get_BoundingBox(null);
			if (box == null)
			{
				continue;
			}
			if (!hasBounds)
			{
				minX = box.Min.X;
				minY = box.Min.Y;
				minZ = box.Min.Z;
				maxX = box.Max.X;
				maxY = box.Max.Y;
				maxZ = box.Max.Z;
				hasBounds = true;
			}
			else
			{
				minX = Math.Min(minX, box.Min.X);
				minY = Math.Min(minY, box.Min.Y);
				minZ = Math.Min(minZ, box.Min.Z);
				maxX = Math.Max(maxX, box.Max.X);
				maxY = Math.Max(maxY, box.Max.Y);
				maxZ = Math.Max(maxZ, box.Max.Z);
			}
		}
		if (!hasBounds)
		{
			return false;
		}
		combined = new BoundingBoxXYZ
		{
			Min = new XYZ(minX, minY, minZ),
			Max = new XYZ(maxX, maxY, maxZ),
			Transform = Transform.Identity
		};
		return true;
	}

	private static void ExpandBoundingBox(ref BoundingBoxXYZ combined, BoundingBoxXYZ extra)
	{
		if (combined == null || extra == null)
		{
			return;
		}
		combined = new BoundingBoxXYZ
		{
			Min = new XYZ(
				Math.Min(combined.Min.X, extra.Min.X),
				Math.Min(combined.Min.Y, extra.Min.Y),
				Math.Min(combined.Min.Z, extra.Min.Z)),
			Max = new XYZ(
				Math.Max(combined.Max.X, extra.Max.X),
				Math.Max(combined.Max.Y, extra.Max.Y),
				Math.Max(combined.Max.Z, extra.Max.Z)),
			Transform = Transform.Identity
		};
	}

	private static bool TryProjectBoundsToLocal(BoundingBoxXYZ worldBox, Transform inverse, out XYZ localMin, out XYZ localMax)
	{
		localMin = null;
		localMax = null;
		if (worldBox == null || inverse == null)
		{
			return false;
		}
		XYZ[] corners =
		{
			new XYZ(worldBox.Min.X, worldBox.Min.Y, worldBox.Min.Z),
			new XYZ(worldBox.Max.X, worldBox.Min.Y, worldBox.Min.Z),
			new XYZ(worldBox.Min.X, worldBox.Max.Y, worldBox.Min.Z),
			new XYZ(worldBox.Max.X, worldBox.Max.Y, worldBox.Min.Z),
			new XYZ(worldBox.Min.X, worldBox.Min.Y, worldBox.Max.Z),
			new XYZ(worldBox.Max.X, worldBox.Min.Y, worldBox.Max.Z),
			new XYZ(worldBox.Min.X, worldBox.Max.Y, worldBox.Max.Z),
			new XYZ(worldBox.Max.X, worldBox.Max.Y, worldBox.Max.Z)
		};
		bool hasPoint = false;
		double minX = 0.0;
		double minY = 0.0;
		double maxX = 0.0;
		double maxY = 0.0;
		foreach (XYZ corner in corners)
		{
			XYZ local = inverse.OfPoint(corner);
			if (!hasPoint)
			{
				minX = local.X;
				minY = local.Y;
				maxX = local.X;
				maxY = local.Y;
				hasPoint = true;
			}
			else
			{
				minX = Math.Min(minX, local.X);
				minY = Math.Min(minY, local.Y);
				maxX = Math.Max(maxX, local.X);
				maxY = Math.Max(maxY, local.Y);
			}
		}
		if (!hasPoint)
		{
			return false;
		}
		localMin = new XYZ(minX, minY, 0.0);
		localMax = new XYZ(maxX, maxY, 0.0);
		return true;
	}

	private static FamilySymbol FindTitleBlock(Document doc, string displayName)
	{
		return new FilteredElementCollector(doc)
			.OfClass(typeof(FamilySymbol))
			.Cast<FamilySymbol>()
			.FirstOrDefault(x => x.Category != null
				&& x.Category.Id.Value == -2000280L
				&& string.Equals(x.FamilyName + " : " + x.Name, displayName, StringComparison.OrdinalIgnoreCase));
	}

	private static View FindViewTemplate(Document doc, string templateName)
	{
		if (string.IsNullOrWhiteSpace(templateName))
		{
			return null;
		}
		return new FilteredElementCollector(doc)
			.OfClass(typeof(View))
			.Cast<View>()
			.FirstOrDefault(x => x.IsTemplate && string.Equals(x.Name, templateName, StringComparison.OrdinalIgnoreCase));
	}

	private static ViewFamilyType FindViewFamilyType(Document doc, ViewFamily family)
	{
		return new FilteredElementCollector(doc)
			.OfClass(typeof(ViewFamilyType))
			.Cast<ViewFamilyType>()
			.FirstOrDefault(t => t.ViewFamily == family);
	}

	private static void TryWriteDiagnosticLog(Exception ex)
	{
		if (ex == null)
		{
			return;
		}
		try
		{
			string folder = SpoolingManagerSettings.SettingsFolderPath;
			Directory.CreateDirectory(folder);
			string path = Path.Combine(folder, "CreateSpoolMapError.log");
			File.WriteAllText(
				path,
				DiagnosticBuildTag + Environment.NewLine + DateTime.Now.ToString("O") + Environment.NewLine + ex,
				Encoding.UTF8);
		}
		catch
		{
		}
	}

	public string GetName()
	{
		return "SS Manager: Create spool map";
	}

	private static string FormatExceptionForUser(Exception ex)
	{
		if (ex == null)
		{
			return "Unknown error.";
		}
		StringBuilder builder = new StringBuilder();
		if (!string.IsNullOrWhiteSpace(_lastTransactionFailureSummary))
		{
			builder.Append(_lastTransactionFailureSummary.Trim());
		}
		for (Exception current = ex; current != null; current = current.InnerException)
		{
			if (current is ArgumentException argumentException && !string.IsNullOrWhiteSpace(argumentException.ParamName))
			{
				if (builder.Length > 0)
				{
					builder.AppendLine();
				}
				builder.Append("Argument: ").Append(argumentException.ParamName);
			}
			if (!string.IsNullOrWhiteSpace(current.Message))
			{
				if (builder.Length > 0)
				{
					builder.AppendLine();
				}
				builder.Append(current.Message.Trim());
			}
		}
		if (builder.Length == 0)
		{
			builder.Append(ex.GetType().FullName ?? ex.GetType().Name);
			string stackLine = ex.StackTrace?
				.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(stackLine))
			{
				builder.AppendLine().Append(stackLine.Trim());
			}
		}
		return builder.ToString();
	}

	private sealed class SpoolMapFailuresPreprocessor : IFailuresPreprocessor
	{
		public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
		{
			StringBuilder errors = new StringBuilder();
			foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
			{
				if (failure.GetSeverity() != FailureSeverity.Error)
				{
					continue;
				}
				if (errors.Length > 0)
				{
					errors.AppendLine();
				}
				errors.Append(failure.GetDescriptionText());
			}
			if (errors.Length == 0)
			{
				return FailureProcessingResult.Continue;
			}
			_lastTransactionFailureSummary = errors.ToString();
			return FailureProcessingResult.ProceedWithRollBack;
		}
	}
}
