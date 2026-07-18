using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public partial class CreateSpoolSheetsHandler
{
	private sealed class SpoolSheetWorkItem
	{
		public AssemblyInstance Assembly { get; set; }

		public string DisplayName { get; set; }

		public ViewSheet Sheet { get; set; }

		public View3D OrthoPickView { get; set; }
	}

	private sealed class SpoolViewWorkItem
	{
		public SpoolSheetWorkItem SheetWork { get; set; }

		public ViewBuildOption Option { get; set; }

		public View View { get; set; }

		public Viewport OrthoViewport { get; set; }
	}

	private sealed class SpoolSheetGenerationContext
	{
		public UIApplication App { get; set; }

		public UIDocument Uidoc { get; set; }

		public Document Doc { get; set; }

		public SpoolingManagerSettings Settings { get; set; }

		public SpoolingManagerKind ProductKind { get; set; }

		public bool RegularSheetBranch { get; set; }

		public CreateSpoolSheetsRequest Request { get; set; }

		public FamilySymbol TitleBlock { get; set; }

		public FamilySymbol TagType { get; set; }

		public FamilySymbol HangerTagType { get; set; }

		public FamilySymbol DuctTagType { get; set; }

		public FamilySymbol WeldTagType { get; set; }

		public FamilySymbol AssemblyTagType { get; set; }

		public ElementType ViewportType { get; set; }

		public ViewSchedule Schedule { get; set; }

		public List<ViewSchedule> Schedules { get; set; }

		/// <summary>Resolved schedules with Top Left / Top Right placement from settings.</summary>
		public List<(ViewSchedule Schedule, string Placement)> ScheduleEntries { get; set; }

		public TextNoteType WeldLogTextNoteType { get; set; }

		public SpoolPerf Perf { get; set; }

		public List<string> Messages { get; set; }

		public int CreatedSheets { get; set; }

		public int CreatedViews { get; set; }

		public int CreatedTags { get; set; }

		public int WeldLogNotes { get; set; }

		public int TotalSheets { get; set; }

		public int CompletedSheets { get; set; }
	}

	private static List<SpoolSheetWorkItem> BuildSpoolSheetWorkItems(SpoolSheetGenerationContext ctx)
	{
		List<SpoolSheetWorkItem> items = new List<SpoolSheetWorkItem>();
		foreach (ElementId assemblyId in ctx.Request.AssemblyIds)
		{
			Element element = ctx.Doc.GetElement(assemblyId);
			AssemblyInstance assembly = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			if (assembly == null)
			{
				continue;
			}
			string displayName = AssemblyDisplayName.Get(assembly);
			if (!assembly.AllowsAssemblyViewCreation())
			{
				ctx.Messages.Add(displayName + ": assembly views cannot be created.");
				continue;
			}
			if (HasExistingSpoolSheet(ctx.Doc, assembly, ctx.RegularSheetBranch))
			{
				if (ctx.Request.ExistingSheetAction == ExistingSheetAction.SkipExisting)
				{
					ctx.Messages.Add(displayName + ": skipped existing assembly sheet.");
					continue;
				}
			}
			items.Add(new SpoolSheetWorkItem
			{
				Assembly = assembly,
				DisplayName = displayName
			});
		}
		return items;
	}

	private static List<SpoolViewWorkItem> BuildSpoolViewWorkItems(List<SpoolSheetWorkItem> sheetWorks, SpoolingManagerSettings settings, SpoolingManagerKind productKind)
	{
		EnsureAutoDimHasElevationView(settings);
		List<ViewBuildOption> viewOptions = (from x in BuildViewOptions(settings, productKind)
			where x.Include
			select x).ToList();
		List<SpoolViewWorkItem> viewWorks = new List<SpoolViewWorkItem>(sheetWorks.Count * Math.Max(viewOptions.Count, 1));
		foreach (SpoolSheetWorkItem sheetWork in sheetWorks)
		{
			foreach (ViewBuildOption option in viewOptions)
			{
				viewWorks.Add(new SpoolViewWorkItem
				{
					SheetWork = sheetWork,
					Option = option
				});
			}
		}
		return viewWorks;
	}

	private static void RunSpoolSheetAssemblyLine(SpoolSheetGenerationContext ctx, List<SpoolSheetWorkItem> sheetWorks, List<SpoolViewWorkItem> viewWorks)
	{
		int total = Math.Max(1, ctx.TotalSheets);
		ReportSheetProgress(10.0, "Prepare Spools", DescribePrepareSpoolsStep(ctx));
		AssemblyLinePrepareSpools(ctx, sheetWorks);
		ReportSheetProgress(18.0, "Create Sheets", "0 of " + total);
		AssemblyLineCreateSheets(ctx, sheetWorks);
		ReportSheetProgress(28.0, "Name Sheets", "Setting sheet names and numbers");
		AssemblyLineNameSheets(ctx, sheetWorks);
		ReportSheetProgress(36.0, "Create Views", "Building viewports for each sheet");
		AssemblyLineCreateViews(ctx, viewWorks);
		ReportSheetProgress(48.0, "Scale and Annotate", DescribeScaleAndAnnotateStep(ctx, viewWorks));
		AssemblyLineScaleAndAnnotateViews(ctx, viewWorks);
		ReportSheetProgress(82.0, "Place Views", "Positioning views on sheets");
		AssemblyLinePlaceViews(ctx, viewWorks);
		ReportSheetProgress(90.0, "Place Schedule", DescribePlaceScheduleStep(ctx));
		AssemblyLinePlaceSchedule(ctx, sheetWorks);
	}

	private static string DescribePrepareSpoolsStep(SpoolSheetGenerationContext ctx)
	{
		List<string> parts = new List<string> { "Item numbers" };
		SpoolingManagerSettings settings = ctx?.Settings;
		if (settings != null && settings.NumberWeldsEnabled)
		{
			parts.Add("S-weld numbers");
		}
		if (settings != null && settings.ContinuationTagsEnabled)
		{
			parts.Add("continuation tags");
		}
		if (ctx?.Request?.ExistingSheetAction == ExistingSheetAction.RegenerateExisting)
		{
			parts.Add("cleanup");
		}
		return string.Join(", ", parts);
	}

	private static string DescribePlaceScheduleStep(SpoolSheetGenerationContext ctx)
	{
		List<string> parts = new List<string>();
		int scheduleCount = ctx?.ScheduleEntries?.Count(entry => entry.Schedule != null)
			?? ctx?.Schedules?.Count(s => s != null)
			?? 0;
		if (scheduleCount == 0 && ctx?.Schedule != null)
		{
			scheduleCount = 1;
		}
		if (scheduleCount > 0)
		{
			parts.Add(scheduleCount > 1 ? scheduleCount + " schedules" : "BOM schedule");
		}
		if (ctx?.Settings?.WeldLogEnabled == true && ctx.WeldLogTextNoteType != null)
		{
			parts.Add("weld log");
		}
		return parts.Count > 0 ? string.Join(" and ", parts) : "Schedule";
	}

	private static string DescribeScaleAndAnnotateStep(SpoolSheetGenerationContext ctx, List<SpoolViewWorkItem> viewWorks)
	{
		bool anyTags = (ctx?.TagType != null || ctx?.HangerTagType != null || ctx?.DuctTagType != null)
			&& viewWorks != null && viewWorks.Any((v) => v.Option?.TagEnabled == true);
		bool anyAutoDim = viewWorks != null && viewWorks.Any((v) => v.Option?.AutoDimEnabled == true && !ctx.ProductKind.IsMmcTesting());
		List<string> parts = new List<string>();
		if (anyTags)
		{
			parts.Add("Tags");
		}
		parts.Add("fit");
		if (anyAutoDim)
		{
			parts.Add("Auto Dim");
		}
		return string.Join(", ", parts);
	}

	private static void AssemblyLinePrepareSpools(SpoolSheetGenerationContext ctx, List<SpoolSheetWorkItem> sheetWorks)
	{
		long mark = ctx.Perf.Mark();
		if (ctx.Request.ExistingSheetAction == ExistingSheetAction.RegenerateExisting)
		{
			foreach (SpoolSheetWorkItem sheetWork in sheetWorks)
			{
				if (!HasExistingSpoolSheet(ctx.Doc, sheetWork.Assembly, ctx.RegularSheetBranch))
				{
					continue;
				}
				DeleteExistingSpoolViewsAndSheet(ctx.Doc, sheetWork.Assembly, ctx.RegularSheetBranch);
				RegenTracked(ctx.Doc);
			}
		}
		foreach (SpoolSheetWorkItem sheetWork in sheetWorks)
		{
			FabricationSavantParameterSync.SyncAssemblyMemberParameters(ctx.App.Application, ctx.Doc, sheetWork.Assembly);
			AssignAssemblyItemNumbers(ctx.Doc, sheetWork.Assembly, ctx.ProductKind, ctx.Settings);
			if (ctx.Settings.NumberWeldsEnabled)
			{
				AssignAssemblySWeldNumbers(ctx.Doc, sheetWork.Assembly, ComputeAssemblySWeldPrefix(ctx.Doc, sheetWork.Assembly, ctx.Settings));
			}
			if (ctx.Settings.ContinuationTagsEnabled)
			{
				AssignAssemblyContinuationValues(ctx.App.Application, ctx.Doc, sheetWork.Assembly);
			}
		}
		FlushPendingRegen(ctx.Doc);
		ctx.Perf.Add("Prepare Spools", mark);
	}

	private static void AssemblyLineCreateSheets(SpoolSheetGenerationContext ctx, List<SpoolSheetWorkItem> sheetWorks)
	{
		long mark = ctx.Perf.Mark();
		int total = Math.Max(1, sheetWorks.Count);
		int index = 0;
		foreach (SpoolSheetWorkItem sheetWork in sheetWorks)
		{
			index++;
			ReportSheetProgress(
				18.0 + (8.0 * index / total),
				"Create Sheets",
				index + " of " + total + " — " + sheetWork.DisplayName);
			try
			{
				sheetWork.Sheet = ctx.RegularSheetBranch
					? ViewSheet.Create(ctx.Doc, ((Element)ctx.TitleBlock).Id)
					: AssemblyViewUtils.CreateSheet(ctx.Doc, ((Element)sheetWork.Assembly).Id, ((Element)ctx.TitleBlock).Id);
			}
			catch (Exception ex)
			{
				ctx.Messages.Add(sheetWork.DisplayName + ": failed to create sheet. " + ex.Message);
				continue;
			}
			if (sheetWork.Sheet == null)
			{
				ctx.Messages.Add(sheetWork.DisplayName + ": sheet was not created.");
				continue;
			}
			ctx.CreatedSheets++;
		}
		ctx.Perf.Add("Create Sheet", mark);
	}

	private static void AssemblyLineNameSheets(SpoolSheetGenerationContext ctx, List<SpoolSheetWorkItem> sheetWorks)
	{
		long mark = ctx.Perf.Mark();
		foreach (SpoolSheetWorkItem sheetWork in sheetWorks)
		{
			if (sheetWork.Sheet == null)
			{
				continue;
			}
			try
			{
				((Element)sheetWork.Sheet).Name = ctx.ProductKind.IsMmcStyle() ? "Spool" : sheetWork.DisplayName + " Spool Sheet";
			}
			catch
			{
			}
			try
			{
				sheetWork.Sheet.SheetNumber = sheetWork.DisplayName;
			}
			catch
			{
			}
		}
		ctx.Perf.Add("Name Sheet", mark);
	}

	private static void AssemblyLineCreateViews(SpoolSheetGenerationContext ctx, List<SpoolViewWorkItem> viewWorks)
	{
		long mark = ctx.Perf.Mark();
		int scale = GetSpoolSheetViewScale(ctx.ProductKind, ctx.Settings);
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			if (viewWork.SheetWork.Sheet == null)
			{
				continue;
			}
			try
			{
				viewWork.View = viewWork.Option.CreateView(ctx.Doc, viewWork.SheetWork.Assembly);
			}
			catch (Exception ex)
			{
				ctx.Messages.Add(viewWork.SheetWork.DisplayName + ": failed to create " + viewWork.Option.Label + ". " + ex.Message);
			}
			if (viewWork.View == null)
			{
				ctx.Messages.Add(viewWork.SheetWork.DisplayName + ": " + viewWork.Option.Label + " was not created.");
				continue;
			}
			ctx.CreatedViews++;
			ApplySpoolViewTemplateFromSettings(ctx.Doc, viewWork.View, viewWork.Option.TemplateName);
			ApplyViewScale(viewWork.View, scale);
			TryEnsureSpoolViewGeometryForAnnotation(ctx.Doc, viewWork.View);
			EnsureSpoolAssemblyDetailViewCrop(viewWork.View);
			if (viewWork.View is View3D view3D)
			{
				Apply3DDirection(viewWork.SheetWork.Assembly, view3D, ctx.Settings.Direction3D);
				try
				{
					view3D.SaveOrientationAndLock();
				}
				catch
				{
				}
				viewWork.SheetWork.OrthoPickView = view3D;
			}
			RestrictViewToAssemblyElements(ctx.Doc, viewWork.SheetWork.Assembly, viewWork.View);
			if (!(viewWork.View is View3D) && HasViewCropRotation(viewWork.Option.SheetRotation))
			{
				ApplyViewCropRegionRotation(ctx.Doc, viewWork.View, viewWork.Option.SheetRotation);
			}
		}
		FlushPendingRegen(ctx.Doc);
		ctx.Perf.Add("Create Views", mark);
	}

	private static void AssemblyLineScaleAndAnnotateViews(SpoolSheetGenerationContext ctx, List<SpoolViewWorkItem> viewWorks)
	{
		long mark = ctx.Perf.Mark();
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			if (!(viewWork.View is View3D) || viewWork.SheetWork.Sheet == null || viewWork.View == null)
			{
				continue;
			}
			viewWork.OrthoViewport = PlaceViewOnSheet(ctx.Doc, viewWork.SheetWork.Sheet, viewWork.View, viewWork.Option.Placement);
			if (viewWork.OrthoViewport == null)
			{
				continue;
			}
			if (ctx.ViewportType != null)
			{
				try
				{
					((Element)viewWork.OrthoViewport).ChangeTypeId(((Element)ctx.ViewportType).Id);
				}
				catch
				{
				}
			}
			RecenterViewport(ctx.Doc, viewWork.SheetWork.Sheet, viewWork.OrthoViewport, viewWork.Option.Placement);
			try
			{
				if ((int)viewWork.OrthoViewport.Rotation != 0)
				{
					viewWork.OrthoViewport.Rotation = (ViewportRotation)0;
				}
			}
			catch
			{
			}
			try
			{
				viewWork.View.CropBoxVisible = false;
			}
			catch
			{
			}
			FitSpoolViewportOnSheet(ctx.Doc, viewWork.SheetWork.Sheet, viewWork.View, viewWork.OrthoViewport, viewWork.Option.Placement);
			TryPositionViewportTitleBelow(viewWork.OrthoViewport);
		}
		FlushPendingRegen(ctx.Doc);
		if (ctx.TagType != null || ctx.HangerTagType != null || ctx.DuctTagType != null)
		{
			foreach (SpoolViewWorkItem viewWork in viewWorks)
			{
				if (viewWork.View == null || !viewWork.Option.TagEnabled)
				{
					continue;
				}
				try
				{
					TagCreationResult tagCreationResult = CreateTags(
						ctx.Doc,
						viewWork.SheetWork.Assembly,
						viewWork.View,
						ctx.TagType,
						viewWork.Option.Placement,
						ctx.ProductKind,
						ctx.Settings,
						null,
						ctx.Settings.NumberWeldsEnabled ? string.Empty : null,
						ctx.WeldTagType,
						ctx.AssemblyTagType,
						null,
						ctx.HangerTagType,
						ctx.DuctTagType);
					ctx.CreatedTags += tagCreationResult.CreatedCount;
					if (tagCreationResult.CreatedCount == 0)
					{
						ctx.Messages.Add(viewWork.SheetWork.DisplayName + ": " + viewWork.Option.Label + " tag debug - "
							+ $"parts={tagCreationResult.PartsEvaluated}, "
							+ $"elem={tagCreationResult.ElementReferenceSuccesses}/{tagCreationResult.ElementReferenceAttempts}, "
							+ $"face={tagCreationResult.FaceReferenceSuccesses}/{tagCreationResult.FaceReferenceAttempts}, "
							+ $"catLeader={tagCreationResult.ByCategoryLeaderSuccesses}, "
							+ $"catNoLeader={tagCreationResult.ByCategoryNoLeaderSuccesses}, "
							+ $"typed={tagCreationResult.TypedCreateSuccesses}, "
							+ $"exceptions={tagCreationResult.Exceptions}");
					}
				}
				catch (Exception ex)
				{
					ctx.Messages.Add(viewWork.SheetWork.DisplayName + ": failed to tag " + viewWork.Option.Label + ". " + ex.Message);
				}
			}

			FlushPendingRegen(ctx.Doc);

			// Re-seat titles on every viewport on every sheet (3D, elevations, etc.).
			HashSet<ElementId> titledSheets = new HashSet<ElementId>();
			foreach (SpoolViewWorkItem viewWork in viewWorks)
			{
				ViewSheet sheet = viewWork.SheetWork?.Sheet;
				if (sheet == null || !titledSheets.Add(((Element)sheet).Id))
				{
					continue;
				}

				TryPositionAllViewportTitlesOnSheet(ctx.Doc, sheet);
			}
		}
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			if (viewWork.View == null || viewWork.View is View3D || viewWork.SheetWork.Sheet == null)
			{
				continue;
			}
			if (!viewWork.Option.AutoDimEnabled || ctx.ProductKind.IsMmcTesting())
			{
				continue;
			}
			FitSpoolAssemblyDetailViewCropToContent(ctx.Doc, viewWork.SheetWork.Assembly, viewWork.View, viewWork.Option.TagEnabled);
			if (viewWork.OrthoViewport != null)
			{
				continue;
			}
			Viewport preFitViewport = PlaceViewOnSheet(ctx.Doc, viewWork.SheetWork.Sheet, viewWork.View, viewWork.Option.Placement);
			if (preFitViewport == null)
			{
				continue;
			}
			if (ctx.ViewportType != null)
			{
				try
				{
					((Element)preFitViewport).ChangeTypeId(((Element)ctx.ViewportType).Id);
				}
				catch
				{
				}
			}
			RecenterViewport(ctx.Doc, viewWork.SheetWork.Sheet, preFitViewport, viewWork.Option.Placement);
			try
			{
				if ((int)preFitViewport.Rotation != 0)
				{
					preFitViewport.Rotation = (ViewportRotation)0;
				}
			}
			catch
			{
			}
			try
			{
				viewWork.View.CropBoxVisible = false;
			}
			catch
			{
			}
			viewWork.OrthoViewport = preFitViewport;
			FitSpoolViewportOnSheet(ctx.Doc, viewWork.SheetWork.Sheet, viewWork.View, preFitViewport, viewWork.Option.Placement);
			TryPositionViewportTitleBelow(preFitViewport);
		}
		FlushPendingRegen(ctx.Doc);
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			if (viewWork.View == null || viewWork.View is View3D)
			{
				continue;
			}
			if (!viewWork.Option.AutoDimEnabled || ctx.ProductKind.IsMmcTesting())
			{
				continue;
			}
			ExpandViewCropForSpoolAnnotation(ctx.Doc, viewWork.View, viewWork.SheetWork.Assembly);
		}
		ElementId lastAssemblyId = ElementId.InvalidElementId;
		List<SpoolViewWorkItem> autoDimViews = viewWorks
			.Where((v) => v.View != null && v.SheetWork?.Sheet != null && v.Option.AutoDimEnabled && !ctx.ProductKind.IsMmcTesting())
			.ToList();
		int autoDimTotal = Math.Max(1, autoDimViews.Count);
		int autoDimIndex = 0;
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			if (viewWork.View == null || viewWork.SheetWork.Sheet == null)
			{
				continue;
			}
			if (!viewWork.Option.AutoDimEnabled || ctx.ProductKind.IsMmcTesting())
			{
				continue;
			}
			autoDimIndex++;
			ReportSheetProgress(
				48.0 + (30.0 * autoDimIndex / autoDimTotal),
				"Auto Dim",
				autoDimIndex + " of " + autoDimTotal + " — " + viewWork.SheetWork.DisplayName + " / " + viewWork.Option.Label);
			ElementId assemblyId = ((Element)viewWork.SheetWork.Assembly).Id;
			if (lastAssemblyId != assemblyId)
			{
				ClearAutoDimNeighborhoodCache();
				FlushPendingRegen(ctx.Doc);
				lastAssemblyId = assemblyId;
			}
			try
			{
				string autoDimMessage = TryApplyAutoDimensions(
					ctx.Uidoc,
					ctx.Doc,
					viewWork.SheetWork.Sheet,
					viewWork.View,
					viewWork.SheetWork.Assembly,
					viewWork.Option,
					ctx.Settings,
					viewWork.SheetWork.OrthoPickView);
				if (!string.IsNullOrEmpty(autoDimMessage))
				{
					ctx.Messages.Add(viewWork.SheetWork.DisplayName + ": " + viewWork.Option.Label + " — " + autoDimMessage);
				}
			}
			catch (Exception ex)
			{
				ctx.Messages.Add(viewWork.SheetWork.DisplayName + ": " + viewWork.Option.Label + " — Auto-dimension error: " + ex.Message);
			}
		}
		ClearAutoDimNeighborhoodCache();
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			if (viewWork.View == null || viewWork.View is View3D)
			{
				continue;
			}
			bool autoDim = viewWork.Option.AutoDimEnabled && !ctx.ProductKind.IsMmcTesting();
			if (autoDim)
			{
				TryUnhideAllViewDimensions(ctx.Doc, viewWork.View);
				FitSpoolAssemblyDetailViewCropToContent(ctx.Doc, viewWork.SheetWork.Assembly, viewWork.View, viewWork.Option.TagEnabled, includeDimensionExtents: true, includeDimensionLabelExtents: true);
				TryExpandViewAnnotationCropForDimensions(viewWork.View);
			}
			else
			{
				FitSpoolAssemblyDetailViewCropToContent(ctx.Doc, viewWork.SheetWork.Assembly, viewWork.View, viewWork.Option.TagEnabled);
				TryTightenSpoolViewAnnotationCrop(viewWork.View);
			}
		}
		FlushPendingRegen(ctx.Doc);
		ctx.Perf.Add("Scale and Annotate Views", mark);

		// Final pass: every viewport on every generated sheet.
		HashSet<ElementId> finalSheets = new HashSet<ElementId>();
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			ViewSheet sheet = viewWork.SheetWork?.Sheet;
			if (sheet == null || !finalSheets.Add(((Element)sheet).Id))
			{
				continue;
			}

			TryPositionAllViewportTitlesOnSheet(ctx.Doc, sheet);
		}
	}

	private static void AssemblyLinePlaceViews(SpoolSheetGenerationContext ctx, List<SpoolViewWorkItem> viewWorks)
	{
		long mark = ctx.Perf.Mark();
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			if (viewWork.View == null || viewWork.SheetWork.Sheet == null)
			{
				continue;
			}
			Viewport viewport = viewWork.OrthoViewport ?? PlaceViewOnSheet(ctx.Doc, viewWork.SheetWork.Sheet, viewWork.View, viewWork.Option.Placement);
			if (viewport == null)
			{
				ctx.Messages.Add(viewWork.SheetWork.DisplayName + ": failed to place " + viewWork.Option.Label + " on sheet.");
				continue;
			}
			if (ctx.ViewportType != null)
			{
				try
				{
					((Element)viewport).ChangeTypeId(((Element)ctx.ViewportType).Id);
				}
				catch
				{
				}
			}
			RecenterViewport(ctx.Doc, viewWork.SheetWork.Sheet, viewport, viewWork.Option.Placement);
			try
			{
				if ((int)viewport.Rotation != 0)
				{
					viewport.Rotation = (ViewportRotation)0;
				}
			}
			catch
			{
			}
			try
			{
				viewWork.View.CropBoxVisible = false;
			}
			catch
			{
			}
			if (viewWork.OrthoViewport == null)
			{
				FitSpoolViewportOnSheet(ctx.Doc, viewWork.SheetWork.Sheet, viewWork.View, viewport, viewWork.Option.Placement);
			}

			TryPositionViewportTitleBelow(viewport);
		}
		FlushPendingRegen(ctx.Doc);

		HashSet<ElementId> placedSheets = new HashSet<ElementId>();
		foreach (SpoolViewWorkItem viewWork in viewWorks)
		{
			ViewSheet sheet = viewWork.SheetWork?.Sheet;
			if (sheet == null || !placedSheets.Add(((Element)sheet).Id))
			{
				continue;
			}

			TryPositionAllViewportTitlesOnSheet(ctx.Doc, sheet);
		}

		ctx.Perf.Add("Place Views", mark);
	}

	private static void AssemblyLinePlaceSchedule(SpoolSheetGenerationContext ctx, List<SpoolSheetWorkItem> sheetWorks)
	{
		long mark = ctx.Perf.Mark();
		List<(ViewSchedule Schedule, string Placement)> entries = (ctx.ScheduleEntries ?? new List<(ViewSchedule, string)>())
			.Where(entry => entry.Schedule != null)
			.ToList();
		if (entries.Count == 0)
		{
			List<ViewSchedule> schedules = (ctx.Schedules ?? new List<ViewSchedule>())
				.Where(schedule => schedule != null)
				.ToList();
			if (schedules.Count == 0 && ctx.Schedule != null)
			{
				schedules.Add(ctx.Schedule);
			}

			// Preserve settings intent: default every schedule to Top Left (never alternate sides).
			foreach (ViewSchedule schedule in schedules)
			{
				entries.Add((schedule, SpoolScheduleOption.PlacementTopLeft));
			}
		}

		if (entries.Count > 0)
		{
			foreach (SpoolSheetWorkItem sheetWork in sheetWorks)
			{
				if (sheetWork.Sheet == null)
				{
					continue;
				}

				try
				{
					List<ViewSchedule> leftColumn = entries
						.Where(entry => !SpoolScheduleOption.IsTopRight(entry.Placement))
						.Select(entry => entry.Schedule)
						.ToList();
					List<ViewSchedule> rightColumn = entries
						.Where(entry => SpoolScheduleOption.IsTopRight(entry.Placement))
						.Select(entry => entry.Schedule)
						.ToList();

					PlaceScheduleColumnOnSheet(ctx.Doc, sheetWork.Sheet, leftColumn, alignTopRight: false, ctx.Settings);
					PlaceScheduleColumnOnSheet(ctx.Doc, sheetWork.Sheet, rightColumn, alignTopRight: true, ctx.Settings);
				}
				catch (Exception ex)
				{
					ctx.Messages.Add(sheetWork.DisplayName + ": failed to place schedule. " + ex.Message);
				}
			}
		}
		if (ctx.Settings.WeldLogEnabled && ctx.WeldLogTextNoteType != null)
		{
			foreach (SpoolSheetWorkItem sheetWork in sheetWorks)
			{
				if (sheetWork.Sheet == null)
				{
					continue;
				}
				try
				{
					ctx.WeldLogNotes += FillWeldLogOnSheet(ctx.Doc, sheetWork.Sheet, sheetWork.Assembly, ctx.Settings, ctx.WeldLogTextNoteType);
				}
				catch (Exception ex)
				{
					ctx.Messages.Add(sheetWork.DisplayName + ": failed to fill weld log. " + ex.Message);
				}
			}
		}

		foreach (SpoolSheetWorkItem sheetWork in sheetWorks)
		{
			if (sheetWork.Sheet == null || sheetWork.Assembly == null)
			{
				continue;
			}

			try
			{
				SpoolSheetQrCodeService.PlaceOrUpdateOnSheet(ctx.Doc, sheetWork.Sheet, sheetWork.Assembly, ctx.Settings);
			}
			catch (Exception ex)
			{
				ctx.Messages.Add(sheetWork.DisplayName + ": failed to place tracking QR. " + ex.Message);
			}
		}

		ctx.Perf.Add("Place Schedule", mark);
	}

	private static void ClearAutoDimNeighborhoodCache()
	{
		_autoDimNeighborhoodPool = null;
		_autoDimNeighborhoodDepth = null;
		_autoDimNeighborhoodSeedKey = null;
		_autoDimMidRunFlangeSplit = false;
	}

	/// <summary>
	/// Creates and places assembly views that are enabled in settings but missing from an existing spool sheet
	/// (e.g. user turned on Top View after the sheet was first generated).
	/// </summary>
	internal static int TryAddMissingAssemblyViews(
		UIApplication app,
		Document doc,
		ViewSheet sheet,
		AssemblyInstance assembly,
		SpoolingManagerSettings settings,
		SpoolingManagerKind productKind,
		FamilySymbol tagType,
		FamilySymbol weldTagType,
		FamilySymbol assemblyTagType,
		List<string> messages)
	{
		if (doc == null || sheet == null || assembly == null || settings == null)
		{
			return 0;
		}
		if (!assembly.AllowsAssemblyViewCreation())
		{
			messages?.Add(AssemblyDisplayName.Get(assembly) + ": assembly views cannot be created.");
			return 0;
		}
		EnsureAutoDimHasElevationView(settings);
		List<View> existingViews = FindAssemblyViews(doc, assembly).ToList();
		List<ViewBuildOption> missingOptions = (from option in BuildViewOptions(settings, productKind)
			where option.Include && !existingViews.Any((view) => DoesViewMatchBuildOption(view, option))
			select option).ToList();
		if (missingOptions.Count == 0)
		{
			return 0;
		}
		SpoolSheetGenerationContext ctx = new SpoolSheetGenerationContext
		{
			App = app,
			Uidoc = app?.ActiveUIDocument,
			Doc = doc,
			Settings = settings,
			ProductKind = productKind,
			TagType = tagType,
			WeldTagType = weldTagType,
			AssemblyTagType = assemblyTagType,
			ViewportType = FindViewportType(doc, settings.ViewportTypeName),
			Messages = messages ?? new List<string>(),
			Perf = new SpoolPerf
			{
				Enabled = false
			}
		};
		SpoolSheetWorkItem sheetWork = new SpoolSheetWorkItem
		{
			Assembly = assembly,
			DisplayName = AssemblyDisplayName.Get(assembly),
			Sheet = sheet,
			OrthoPickView = existingViews.OfType<View3D>().FirstOrDefault()
		};
		List<SpoolViewWorkItem> viewWorks = missingOptions.Select((option) => new SpoolViewWorkItem
		{
			SheetWork = sheetWork,
			Option = option
		}).ToList();
		AssemblyLineCreateViews(ctx, viewWorks);
		AssemblyLineScaleAndAnnotateViews(ctx, viewWorks);
		AssemblyLinePlaceViews(ctx, viewWorks);
		return viewWorks.Count((viewWork) => viewWork.View != null);
	}
}
