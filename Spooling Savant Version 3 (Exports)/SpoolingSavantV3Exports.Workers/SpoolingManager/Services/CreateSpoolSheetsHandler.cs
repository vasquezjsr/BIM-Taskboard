using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using Autodesk.Revit.DB;
using RevitPoint = Autodesk.Revit.DB.Point;
using Autodesk.Revit.UI;
using Document = Autodesk.Revit.DB.Document;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public partial class CreateSpoolSheetsHandler : IExternalEventHandler
{
	public const string DiagnosticBuildTag = "2026-07-18-2248-NO-WELD-ORGANIZE";

	internal static string GetWorkersBuildBanner()
	{
		return "Workers build tag: " + DiagnosticBuildTag;
	}

	private static View3D _autoDimReferencePickView3D;

	private static int _regenCount;

	private static long _regenTicks;

	private static readonly Dictionary<string, long> _autoDimTicks = new Dictionary<string, long>();

	private static readonly Dictionary<string, int> _autoDimCounts = new Dictionary<string, int>();

	private static Document _docFabPoolDoc;

	private static List<FabricationPart> _docFabPoolCache;

	private static Document _fabConnectorIndexDoc;

	private static Dictionary<(long, long, long), List<(FabricationPart part, XYZ origin)>> _fabConnectorIndexCache;

	// The assembly's connected neighborhood for the auto-dim pass currently running. Built once per assembly
	// during batch sheet generation and reused across views; cleared at the end of each assembly loop.
	private static List<FabricationPart> _autoDimNeighborhoodPool;

	private static Dictionary<ElementId, int> _autoDimNeighborhoodDepth;

	private static string _autoDimNeighborhoodSeedKey;

	// Set by CollectDominantRunBaselineIntents when it splits a run at a mid-run flange into C-F + F-E + C-E.
	// The along-run collectors are then suppressed so they don't add duplicate/overlapping C-E and E-E dims.
	private static bool _autoDimMidRunFlangeSplit;

	/// <summary>
	/// Master switch for spool auto-dimension placement. False = no rules run, no dims placed, existing manual dims preserved.
	/// Re-enable when lesson-driven placement is rebuilt.
	/// </summary>
	// Auto-dimension placement is controlled only by per-view Auto Dim checkboxes in Spooling Manager settings
	// (ViewBuildOption.AutoDimEnabled / IsAutoDimEnabledForExistingView). Never add a compile-time kill switch.

	private static Document _taggablePartsCacheDoc;

	private static readonly Dictionary<ElementId, List<FabricationPart>> _taggablePartsCache = new Dictionary<ElementId, List<FabricationPart>>();

	// Title block bounds are constant per sheet but are queried many times per sheet (once per weld-log slot
	// plus during viewport placement). Cache per sheet for the duration of a single Create run.
	private static Document _titleBlockBoundsCacheDoc;

	private static readonly Dictionary<ElementId, BoundingBoxXYZ> _titleBlockBoundsCache = new Dictionary<ElementId, BoundingBoxXYZ>();

	// The part "search corpus" (name + type + family + ~7 string parameter lookups) is rebuilt dozens of
	// times per part by the classification predicates (IsFlangePart, IsOletPart, IsWeldPart, etc.) across
	// every auto-dim collector, tagging, and weld-log pass. It depends only on part identity/parameters
	// which do not change during a Create run, so cache the no-extra-parameter result per part.
	private static Document _searchCorpusCacheDoc;

	private static readonly Dictionary<ElementId, string> _searchCorpusCache = new Dictionary<ElementId, string>();

	private static Document _viewExtentCacheDoc;

	private static readonly Dictionary<(long viewId, int flags), List<XYZ>> _viewExtentCache = new Dictionary<(long, int), List<XYZ>>();

	private static Document _assemblyPartsCacheDoc;

	private static readonly Dictionary<ElementId, List<FabricationPart>> _assemblyPartsCache = new Dictionary<ElementId, List<FabricationPart>>();

	// Whole-document FabricationPart collection is expensive; cache it for the duration of a single
	// Create run so the auto-dimension neighborhood search doesn't rebuild it once per view/flange.
	private static List<FabricationPart> GetCachedDocumentFabricationMatePool(Document doc)
	{
		if (doc == null)
		{
			return new List<FabricationPart>();
		}
		if (!ReferenceEquals(_docFabPoolDoc, doc) || _docFabPoolCache == null)
		{
			_docFabPoolCache = BuildDocumentFabricationMatePool(doc);
			_docFabPoolDoc = doc;
		}
		return _docFabPoolCache;
	}

	// Connector spatial index for the whole document, cell size 0.25 ft. Built once per run and reused for
	// every assembly's neighborhood flood (the index is identical across assemblies; only the seeds differ).
	private static Dictionary<(long, long, long), List<(FabricationPart part, XYZ origin)>> GetCachedFabConnectorIndex(Document doc)
	{
		if (doc == null)
		{
			return new Dictionary<(long, long, long), List<(FabricationPart, XYZ)>>();
		}
		if (ReferenceEquals(_fabConnectorIndexDoc, doc) && _fabConnectorIndexCache != null)
		{
			return _fabConnectorIndexCache;
		}
		const double cell = 0.25;
		Dictionary<(long, long, long), List<(FabricationPart part, XYZ origin)>> index = new Dictionary<(long, long, long), List<(FabricationPart, XYZ)>>();
		foreach (FabricationPart docPart in GetCachedDocumentFabricationMatePool(doc))
		{
			if (docPart == null)
			{
				continue;
			}
			foreach (Connector connector in ListConnectors(docPart))
			{
				XYZ origin = connector?.Origin;
				if (origin == null)
				{
					continue;
				}
				(long, long, long) key = ((long)Math.Floor(origin.X / cell), (long)Math.Floor(origin.Y / cell), (long)Math.Floor(origin.Z / cell));
				if (!index.TryGetValue(key, out List<(FabricationPart, XYZ)> bucket))
				{
					bucket = new List<(FabricationPart, XYZ)>();
					index[key] = bucket;
				}
				bucket.Add((docPart, origin));
			}
		}
		_fabConnectorIndexCache = index;
		_fabConnectorIndexDoc = doc;
		return index;
	}

	private static void RegenTracked(Document d)
	{
		if (d == null)
		{
			return;
		}
		if (_batchSheetGeneration)
		{
			_regenPendingDuringBatch = true;
			return;
		}
		DoRegenNow(d);
	}

	internal static void RequestRegenerate(Document d)
	{
		RegenTracked(d);
	}

	private static void DoRegenNow(Document d)
	{
		if (d == null)
		{
			return;
		}
		long start = Stopwatch.GetTimestamp();
		d.Regenerate();
		_regenTicks += Stopwatch.GetTimestamp() - start;
		_regenCount++;
	}

	private static void FlushPendingRegen(Document d)
	{
		if (!_regenPendingDuringBatch || d == null)
		{
			return;
		}
		_regenPendingDuringBatch = false;
		DoRegenNow(d);
	}

	/// <summary>One regen now — coalesced during batch sheet generation via <see cref="FlushPendingRegen"/>.</summary>
	private static void RegenForViewportFit(Document doc)
	{
		if (doc == null)
		{
			return;
		}
		if (_batchSheetGeneration)
		{
			_regenPendingDuringBatch = true;
			FlushPendingRegen(doc);
			return;
		}
		DoRegenNow(doc);
	}

	internal static void BeginBatchRegenCoalescing()
	{
		_batchSheetGeneration = true;
		_regenPendingDuringBatch = false;
	}

	internal static void EndBatchRegenCoalescing(Document doc)
	{
		FlushPendingRegen(doc);
		_batchSheetGeneration = false;
		_regenPendingDuringBatch = false;
	}

	private static void StampAutoDim(string stage, long startMark)
	{
		long delta = Stopwatch.GetTimestamp() - startMark;
		if (!_autoDimTicks.ContainsKey(stage))
		{
			_autoDimTicks[stage] = 0L;
			_autoDimCounts[stage] = 0;
		}
		_autoDimTicks[stage] += delta;
		_autoDimCounts[stage]++;
	}

	private class ViewBuildOption
	{
		public bool Include { get; set; }

		public string Label { get; set; }

		public string Placement { get; set; }

		public string TemplateName { get; set; }

		public bool TagEnabled { get; set; }

		public bool AutoDimEnabled { get; set; }

		public string SheetRotation { get; set; }

		public Func<Document, AssemblyInstance, View> CreateView { get; set; }
	}

	private class TagLayoutData
	{
		public IndependentTag Tag { get; set; }

		public XYZ AnchorPoint { get; set; }

		public Reference Reference { get; set; }
	}

	internal class TagCreationResult
	{
		public int CreatedCount { get; set; }

		public int PartsEvaluated { get; set; }

		public int ElementReferenceAttempts { get; set; }

		public int ElementReferenceSuccesses { get; set; }

		public int FaceReferenceAttempts { get; set; }

		public int FaceReferenceSuccesses { get; set; }

		public int ByCategoryLeaderSuccesses { get; set; }

		public int ByCategoryNoLeaderSuccesses { get; set; }

		public int TypedCreateSuccesses { get; set; }

		public int Exceptions { get; set; }
	}

	private class PendingGroupedTag
	{
		public string GroupingKey { get; set; }

		public XYZ AnchorPoint { get; set; }

		public Element Element { get; set; }
	}

	private class AssemblyContinuationTarget
	{
		public AssemblyInstance ConnectedAssembly { get; set; }

		public Element TagMember { get; set; }

		public XYZ ConnectionPoint { get; set; }

		public string ContinuationValue { get; set; }
	}

	private sealed class SpoolPerf
	{
		private static readonly string[] StepOrder = new string[9]
		{
			"Prepare Spools",
			"Create Sheet",
			"Name Sheet",
			"Create Views",
			"Scale and Annotate Views",
			"Place Views",
			"Place Schedule",
			"Commit",
			"Document Regenerate"
		};

		private readonly Dictionary<string, long> _ticks = new Dictionary<string, long>();

		private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

		private readonly List<string> _order = new List<string>();

		public bool Enabled { get; set; }

		public long Mark()
		{
			if (!Enabled)
			{
				return 0L;
			}
			return Stopwatch.GetTimestamp();
		}

		public void Add(string stage, long startMark)
		{
			if (!Enabled || startMark == 0L)
			{
				return;
			}
			long num = Stopwatch.GetTimestamp() - startMark;
			if (!_ticks.ContainsKey(stage))
			{
				_ticks[stage] = 0L;
				_counts[stage] = 0;
				_order.Add(stage);
			}
			_ticks[stage] += num;
			_counts[stage]++;
		}

		public void AddMeasured(string stage, long elapsedTicks)
		{
			if (!Enabled || elapsedTicks <= 0L)
			{
				return;
			}
			if (!_ticks.ContainsKey(stage))
			{
				_ticks[stage] = 0L;
				_counts[stage] = 0;
				_order.Add(stage);
			}
			_ticks[stage] += elapsedTicks;
			_counts[stage]++;
		}

		public void Regen(Document doc)
		{
			long startMark = Mark();
			RegenTracked(doc);
			Add("Regenerate", startMark);
		}

		public double TotalMs(long totalMark)
		{
			if (!Enabled || totalMark == 0L)
			{
				return 0.0;
			}
			return ToMs(Stopwatch.GetTimestamp() - totalMark);
		}

		public string OrderedReport(long totalMark, long regenTicks, int regenCount)
		{
			if (!Enabled)
			{
				return string.Empty;
			}
			double totalMs = TotalMs(totalMark);
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("Steps (in order):");
			foreach (string stage in StepOrder)
			{
				if (string.Equals(stage, "Document Regenerate", StringComparison.Ordinal))
				{
					double regenMs = ToMs(regenTicks);
					string regenAvg = regenCount > 0 ? FormatStepDuration(regenMs / regenCount) : "0.0s";
					sb.AppendLine($"  {stage}: {FormatStepDuration(regenMs)}  ({regenCount}x, {regenAvg} avg)");
					continue;
				}
				double ms = _ticks.TryGetValue(stage, out long ticks) ? ToMs(ticks) : 0.0;
				int count = _counts.TryGetValue(stage, out int c) ? c : 0;
				string suffix = count > 0 ? $"  ({count}x, {FormatStepDuration(ms / count)} avg)" : "  (skipped)";
				sb.AppendLine($"  {stage}: {FormatStepDuration(ms)}{suffix}");
			}
			sb.AppendLine($"  TOTAL: {FormatStepDuration(totalMs)}");
			return sb.ToString().TrimEnd();
		}

		public string Report(long totalMark)
		{
			return OrderedReport(totalMark, 0L, 0);
		}

		private static double ToMs(long ticks)
		{
			return (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
		}
	}

	private static string FormatStepDuration(double ms)
	{
		if (ms < 0.0)
		{
			ms = 0.0;
		}
		if (ms < 1000.0)
		{
			return (ms / 1000.0).ToString("0.0") + "s";
		}
		int totalSeconds = (int)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);
		int minutes = totalSeconds / 60;
		int seconds = totalSeconds % 60;
		if (minutes > 0)
		{
			return minutes + "m " + seconds + "s";
		}
		return seconds + "s";
	}

	// Master switch for the testing/debug reporting: the post-generation timing + generation report shown in the
	// completed dialog (and saved as SpoolGenReport_*.txt) and the per-run AutoDimDiagnostics.log. Off => the
	// regular short "N sheet(s) created" completed dialog and no TestingReports spew. Flip to true when debugging.
	private static readonly bool TestingReportsEnabled = false;

	private static readonly bool AutoDimPlacementLogEnabled = false;

	private static bool _batchSheetGeneration;

	private static bool _regenPendingDuringBatch;

	private const int SpoolSheetViewScale = 24;

	private const double OneEighthInchOnSheet = 1.0 / 96.0;

	private const double QuarterInchOnSheet = 1.0 / 48.0;

	private const double HalfInchOnSheet = 1.0 / 24.0;

	private const double TagLeaderBaselineInchesOnSheet = 0.5;

	private const double TagLeaderMaxInchesOnSheet = 1.25;

	private const double MmcTagLeaderOffsetInchesOnSheet = 0.8;

	private const double ScheduleLeftVisualCompensation = -0.0068359375;

	private const double ScheduleCreateOriginOffsetRightInches = 3.0 / 32.0;

	private const string MmcSpoolSheetName = "Spool";

	private const double FabConnectorMateToleranceFeet = 0.08;

	private const double MinFabricationDimensionLengthFeet = 1.0 / 24.0;

	internal static readonly string[] WeldLogSourceViewLabels = new string[6] { "3D Ortho", "Back View", "Front View", "Left View", "Right View", "Top View" };

	private const double DefaultWeldLogLeftInsetInches = 0.125;

	private const double DefaultWeldLogProjectStripHeightInches = 1.125;

	private const double DefaultWeldLogRowSpacingInches = 0.25;

	private const double DefaultWeldLogTextPaddingInches = 1.0 / 32.0;

	/// <summary>Horizontal nudge after the column line + padding. Keep 0 so text sits 1/32" past each WELD NUMBER line.</summary>
	private const double DefaultWeldLogTextOffsetLeftInches = 0.0;

	private const double DefaultWeldLogTextOffsetUpInches = 0.1875;

	private const double DefaultWeldLogColumnCompressPerColumnInches = 0.0;

	private const int DefaultWeldLogColumnCount = 4;

	private const int DefaultWeldLogRowCount = 6;


	// Four weld-log column sets after the 1/8" left inset.
	/// <summary>
	/// WELD NUMBER column lines relative to the left inset (1/8").
	/// Measured set widths: 4-3/16", 4-3/16", 4-1/16" (+ trailing set to match).
	/// </summary>
	private static readonly double[] DefaultWeldLogWeldNumberColumnLeftOffsetsInches = new double[4]
	{
		0.0,
		4.0 + 3.0 / 16.0,
		2.0 * (4.0 + 3.0 / 16.0),
		2.0 * (4.0 + 3.0 / 16.0) + (4.0 + 1.0 / 16.0)
	};

	public CreateSpoolSheetsRequest PendingRequest { get; set; }

	public void Execute(UIApplication app)
	{
		//IL_077b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0856: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Expected O, but got Unknown
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		if (((app != null) ? app.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}
		ResetAutoDimDiagnosticLog();
		_batchSheetGeneration = true;
		UIDocument activeUIDocument = app.ActiveUIDocument;
		Document document = activeUIDocument.Document;
		CreateSpoolSheetsRequest pendingRequest = PendingRequest;
		if (pendingRequest == null || pendingRequest.AssemblyIds.Count == 0)
		{
			OperationProgressSession.Close();
			return;
		}
		SpoolingManagerKind productKind = pendingRequest.ProductKind;
		SpoolingManagerSettings spoolingManagerSettings = SpoolingManagerSettings.Load(productKind);
		bool flag = UsesRegularSheetBranch(spoolingManagerSettings, productKind);
		string toolWindowTitle = GetToolWindowTitle(productKind);
		SpoolPerf spoolPerf = new SpoolPerf
		{
			Enabled = true
		};
		_regenCount = 0;
		_regenTicks = 0L;
		_autoDimTicks.Clear();
		_autoDimCounts.Clear();
		_docFabPoolDoc = null;
		_docFabPoolCache = null;
		_fabConnectorIndexDoc = null;
		_fabConnectorIndexCache = null;
		_autoDimNeighborhoodPool = null;
		_autoDimNeighborhoodDepth = null;
		_autoDimNeighborhoodSeedKey = null;
		_taggablePartsCacheDoc = null;
		_taggablePartsCache.Clear();
		_titleBlockBoundsCacheDoc = null;
		_titleBlockBoundsCache.Clear();
		_searchCorpusCacheDoc = null;
		_searchCorpusCache.Clear();
		_viewExtentCacheDoc = null;
		_viewExtentCache.Clear();
		_assemblyPartsCacheDoc = null;
		_assemblyPartsCache.Clear();
		long perfTotalMark = spoolPerf.Mark();
		ReportSheetProgress(2.0, "Checking settings…", "Title block, tags, and schedule");
		long validateSetupMark = spoolPerf.Mark();
		FamilySymbol val = FindTitleBlock(document, spoolingManagerSettings.TitleBlockName);
		if (val == null)
		{
			_batchSheetGeneration = false;
			OperationProgressSession.Close();
			MessageBox.Show("Title Block NOT FOUND:\n" + spoolingManagerSettings.TitleBlockName, toolWindowTitle);
			return;
		}
		FamilySymbol val2 = null;
		FamilySymbol hangerTagType = null;
		FamilySymbol ductTagType = null;
		FamilySymbol val2b = null;
		FamilySymbol val2c = null;
		if (HasAnyTaggingEnabled(spoolingManagerSettings))
		{
			val2 = FindTagType(document, spoolingManagerSettings.TagTypeName);
			if (val2 == null)
			{
				OperationProgressSession.Close();
				MessageBox.Show("Pipe/Fitting Tag NOT FOUND:\n" + spoolingManagerSettings.TagTypeName, toolWindowTitle);
				return;
			}
		}
		if (spoolingManagerSettings.NumberWeldsEnabled)
		{
			val2b = FindTagType(document, spoolingManagerSettings.WeldTagTypeName);
			if (val2b == null)
			{
				OperationProgressSession.Close();
				MessageBox.Show("Weld Tag Type NOT FOUND:\n" + spoolingManagerSettings.WeldTagTypeName, toolWindowTitle);
				return;
			}
		}
		if (spoolingManagerSettings.ContinuationTagsEnabled)
		{
			val2c = FindTagType(document, spoolingManagerSettings.AssemblyTagTypeName);
			if (val2c == null)
			{
				OperationProgressSession.Close();
				MessageBox.Show("Continuation Tag Type NOT FOUND:\n" + spoolingManagerSettings.AssemblyTagTypeName, toolWindowTitle);
				return;
			}
		}
		TextNoteType weldLogTextNoteType = null;
		if (spoolingManagerSettings.WeldLogEnabled)
		{
			weldLogTextNoteType = FindTextNoteType(document, spoolingManagerSettings.WeldLogTextNoteTypeName);
			if (weldLogTextNoteType == null)
			{
				OperationProgressSession.Close();
				MessageBox.Show("Weld Log Text Type NOT FOUND:\n" + spoolingManagerSettings.WeldLogTextNoteTypeName, toolWindowTitle);
				return;
			}
		}
		ElementType val3 = FindViewportType(document, spoolingManagerSettings.ViewportTypeName);
		spoolPerf.Add("ValidateSetup", validateSetupMark);
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int numWeldLogNotes = 0;
		List<string> list = new List<string>();
		List<SpoolScheduleOption> scheduleOptions = spoolingManagerSettings.GetEffectiveScheduleOptions();
		List<ViewSchedule> schedules = new List<ViewSchedule>();
		List<(ViewSchedule Schedule, string Placement)> scheduleEntries = new List<(ViewSchedule, string)>();
		foreach (SpoolScheduleOption option in scheduleOptions)
		{
			ViewSchedule found = FindSchedule(document, option.Name);
			if (found == null)
			{
				list.Add("Schedule not found: " + option.Name);
			}
			else
			{
				schedules.Add(found);
				scheduleEntries.Add((found, SpoolScheduleOption.NormalizePlacement(option.Placement)));
			}
		}
		ViewSchedule val4 = schedules.Count > 0 ? schedules[0] : null;
		if (HasAnyTaggingEnabled(spoolingManagerSettings))
		{
			if (!string.IsNullOrWhiteSpace(spoolingManagerSettings.HangerTagTypeName))
			{
				hangerTagType = FindTagType(document, spoolingManagerSettings.HangerTagTypeName);
				if (hangerTagType == null)
				{
					list.Add("Hanger Tag not found: " + spoolingManagerSettings.HangerTagTypeName);
				}
			}
			if (!string.IsNullOrWhiteSpace(spoolingManagerSettings.DuctTagTypeName))
			{
				ductTagType = FindTagType(document, spoolingManagerSettings.DuctTagTypeName);
				if (ductTagType == null)
				{
					list.Add("Duct Tag not found: " + spoolingManagerSettings.DuctTagTypeName);
				}
			}
		}
		string fatalError = null;
		if (pendingRequest.ExistingSheetAction == ExistingSheetAction.RegenerateExisting)
		{
			ReportSheetProgress(6.0, "Closing open sheets…", "So existing sheets can be replaced");
			LeaveOpenViewsBlockingSpoolSheetRegenerate(activeUIDocument, document, pendingRequest.AssemblyIds, flag);
		}
		Transaction val5 = new Transaction(document, "Spooling Savant V3 Exports: Create Spool Sheets");
		try
		{
			val5.Start();
			long activateSymbolsMark = spoolPerf.Mark();
			if (!val.IsActive)
			{
				val.Activate();
				RegenTracked(document);
			}
			if (val2 != null && !val2.IsActive)
			{
				val2.Activate();
				RegenTracked(document);
			}
			if (hangerTagType != null && !hangerTagType.IsActive)
			{
				hangerTagType.Activate();
				RegenTracked(document);
			}
			if (ductTagType != null && !ductTagType.IsActive)
			{
				ductTagType.Activate();
				RegenTracked(document);
			}
			if (val2b != null && !val2b.IsActive)
			{
				val2b.Activate();
				RegenTracked(document);
			}
			if (val2c != null && !val2c.IsActive)
			{
				val2c.Activate();
				RegenTracked(document);
			}
			spoolPerf.Add("ActivateSymbols", activateSymbolsMark);
			SpoolSheetGenerationContext generationContext = new SpoolSheetGenerationContext
			{
				App = app,
				Uidoc = activeUIDocument,
				Doc = document,
				Settings = spoolingManagerSettings,
				ProductKind = productKind,
				RegularSheetBranch = flag,
				Request = pendingRequest,
				TitleBlock = val,
				TagType = val2,
				HangerTagType = hangerTagType,
				DuctTagType = ductTagType,
				WeldTagType = val2b,
				AssemblyTagType = val2c,
				ViewportType = val3,
				Schedule = val4,
				Schedules = schedules,
				ScheduleEntries = scheduleEntries,
				WeldLogTextNoteType = weldLogTextNoteType,
				Perf = spoolPerf,
				Messages = list
			};
			ReportSheetProgress(8.0, "Preparing work list…", pendingRequest.AssemblyIds.Count + " assembly(s)");
			List<SpoolSheetWorkItem> sheetWorks = BuildSpoolSheetWorkItems(generationContext);
			List<SpoolViewWorkItem> viewWorks = BuildSpoolViewWorkItems(sheetWorks, spoolingManagerSettings, productKind);
			generationContext.TotalSheets = Math.Max(1, sheetWorks.Count);
			RunSpoolSheetAssemblyLine(generationContext, sheetWorks, viewWorks);
			num = generationContext.CreatedSheets;
			num2 = generationContext.CreatedViews;
			num3 = generationContext.CreatedTags;
			numWeldLogNotes = generationContext.WeldLogNotes;
			ReportSheetProgress(96.0, "Saving…", "Commit transaction");
			long startMark10 = spoolPerf.Mark();
			FlushPendingRegen(document);
			using (AssemblyMemberChangeCoordinator.SuppressAutoSync())
			{
				val5.Commit();
			}
			spoolPerf.Add("Commit", startMark10);
			ReportSheetProgress(100.0, "Done", num + " sheet(s) created");
		}
		catch (Exception ex)
		{
			fatalError = ex.Message;
			try
			{
				if (val5.HasStarted() && val5.GetStatus() == TransactionStatus.Started)
				{
					val5.RollBack();
				}
			}
			catch
			{
			}
		}
		finally
		{
			_batchSheetGeneration = false;
			_regenPendingDuringBatch = false;
			((IDisposable)val5)?.Dispose();
		}
		string text3 = $"{num} sheet(s) created.";
		double totalMs = spoolPerf.TotalMs(perfTotalMark);
		if (totalMs > 0.0)
		{
			text3 = text3 + "\nCompleted in " + FormatStepDuration(totalMs);
		}
		if (TestingReportsEnabled)
		{
			string timingSummary = spoolPerf.OrderedReport(perfTotalMark, _regenTicks, _regenCount);
			if (_autoDimTicks.Count > 0)
			{
				StringBuilder autoDimSummary = new StringBuilder();
				autoDimSummary.AppendLine("Auto-dim sub-phases (in order):");
				string[] autoDimOrder = new string[2] { "Collect.AllIntents", "Place.AllIntents" };
				foreach (string stage in autoDimOrder)
				{
					if (!_autoDimTicks.TryGetValue(stage, out long ticks))
					{
						continue;
					}
					double ms = (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
					int count = _autoDimCounts.TryGetValue(stage, out int c) ? c : 0;
					string avg = count > 0 ? FormatStepDuration(ms / count) : "0.0s";
					autoDimSummary.AppendLine($"  {stage}: {FormatStepDuration(ms)}  ({count}x, {avg} avg)");
				}
				timingSummary = timingSummary + "\n\n" + autoDimSummary.ToString().TrimEnd();
			}
			if (!string.IsNullOrEmpty(timingSummary))
			{
				text3 = text3 + "\n\n" + timingSummary;
			}
			string text4 = BuildGenerationReport(toolWindowTitle, spoolingManagerSettings, productKind, pendingRequest.AssemblyIds.Count, num, num2, num3, spoolPerf, perfTotalMark);
			string text5 = TrySaveGenerationReport(text4);
			if (!string.IsNullOrEmpty(text5))
			{
				text3 = text3 + "\n\nDetailed report saved to:\n" + text5;
			}
		}
		if (list.Count > 0)
		{
			text3 = text3 + "\n\n" + string.Join("\n", list.Take(25));
		}
		if (!string.IsNullOrEmpty(fatalError))
		{
			text3 = text3 + "\n\nSheet creation failed and was rolled back:\n" + fatalError;
		}
		// Reuse the progress window chrome (modeless) instead of a native TaskDialog.
		OperationProgressSession.ShowCompleted(toolWindowTitle, text3);
	}

	public string GetName()
	{
		return "Create Spool Sheets";
	}

	private static void ReportSheetProgress(double percent, string status, string detail = null)
	{
		try
		{
			OperationProgressSession.Report(percent, status, detail);
		}
		catch
		{
		}
	}

	private static string BuildGenerationReport(string toolTitle, SpoolingManagerSettings settings, SpoolingManagerKind kind, int assembliesSelected, int createdSheets, int createdViews, int createdTags, SpoolPerf perf, long perfTotalMark)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine(toolTitle + " — Generation Report");
		stringBuilder.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
		try
		{
			Version version = typeof(CreateSpoolSheetsHandler).Assembly.GetName().Version;
			if (version != null)
			{
				stringBuilder.AppendLine("Worker build: " + version.ToString());
			}
		}
		catch
		{
		}
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("Settings:");
		stringBuilder.AppendLine("  Title block: " + OrNone(settings.TitleBlockName));
		stringBuilder.AppendLine("  Schedules: " + (settings.GetEffectiveScheduleOptions().Count == 0
			? OrNone(null)
			: string.Join(", ", settings.GetEffectiveScheduleOptions()
				.Select(option => option.Name + " (" + SpoolScheduleOption.NormalizePlacement(option.Placement) + ")"))));
		stringBuilder.AppendLine("  Viewport type: " + OrDefault(settings.ViewportTypeName));
		stringBuilder.AppendLine("  Pipe/Fitting Tag: " + (HasAnyTaggingEnabled(settings) ? OrNone(settings.TagTypeName) : "(tagging off)"));
		if (HasAnyTaggingEnabled(settings))
		{
			stringBuilder.AppendLine("  Hanger Tag: " + OrNone(settings.HangerTagTypeName));
			stringBuilder.AppendLine("  Duct Tag: " + OrNone(settings.DuctTagTypeName));
		}
		stringBuilder.AppendLine("  View scale: " + FormatViewScale(settings, kind));
		stringBuilder.AppendLine("  Sheet branch: " + (UsesRegularSheetBranch(settings, kind) ? "Regular (Project Browser > Sheets)" : "Assembly"));
		List<string> list = DescribeIncludedViews(settings, kind);
		stringBuilder.AppendLine("  Views (" + list.Count + "): " + ((list.Count > 0) ? string.Join(", ", list) : "(none)"));
		stringBuilder.AppendLine();
		double num = perf.TotalMs(perfTotalMark);
		stringBuilder.AppendLine("Totals:");
		stringBuilder.AppendLine("  Assemblies selected: " + assembliesSelected);
		stringBuilder.AppendLine("  Sheets created: " + createdSheets);
		stringBuilder.AppendLine("  Views created: " + createdViews);
		stringBuilder.AppendLine("  Tags created: " + createdTags);
		stringBuilder.AppendLine("  Total time: " + FormatStepDuration(num));
		if (createdSheets > 0)
		{
			stringBuilder.AppendLine("  Per sheet: " + FormatStepDuration(num / (double)createdSheets));
		}
		double regenMs = (double)_regenTicks * 1000.0 / (double)Stopwatch.Frequency;
		string regenAvg = _regenCount > 0 ? FormatStepDuration(regenMs / _regenCount) : "0.0s";
		stringBuilder.AppendLine("  Regenerations: " + _regenCount + " (" + FormatStepDuration(regenMs) + " total, " + regenAvg + " avg)");
		stringBuilder.AppendLine();
		if (_autoDimTicks.Count > 0)
		{
			stringBuilder.AppendLine("Auto-dimension sub-phases (in order):");
			string[] autoDimOrder = new string[2] { "Collect.AllIntents", "Place.AllIntents" };
			foreach (string stage in autoDimOrder)
			{
				if (!_autoDimTicks.TryGetValue(stage, out long ticks))
				{
					continue;
				}
				double ms = (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
				int count = _autoDimCounts.TryGetValue(stage, out int c) ? c : 0;
				string avg = count > 0 ? FormatStepDuration(ms / (double)count) : "0.0s";
				stringBuilder.AppendLine("  " + stage + ": " + FormatStepDuration(ms) + "  (" + count + "x, " + avg + " avg)");
			}
			stringBuilder.AppendLine();
		}
		stringBuilder.Append(perf.OrderedReport(perfTotalMark, _regenTicks, _regenCount));
		return stringBuilder.ToString();
	}

	private static List<string> DescribeIncludedViews(SpoolingManagerSettings settings, SpoolingManagerKind kind)
	{
		List<string> list = new List<string>();
		foreach (ViewBuildOption item in from x in BuildViewOptions(settings, kind)
			where x.Include
			select x)
		{
			string text = string.Empty;
			if (item.TagEnabled)
			{
				text += " [tag]";
			}
			if (item.AutoDimEnabled && !kind.IsMmcTesting())
			{
				text += " [autodim]";
			}
			list.Add(item.Label + text);
		}
		return list;
	}

	private static string FormatViewScale(SpoolingManagerSettings settings, SpoolingManagerKind kind)
	{
		double num = settings.GetSpoolSheetScaleInchesPerFoot(kind);
		if (num <= 0.0)
		{
			num = (kind.IsMmcStyle() ? 1.5 : 0.5);
		}
		int num2 = Math.Max(1, (int)Math.Round(12.0 / num));
		return num.ToString("0.###") + "\" = 1'-0\"  (1:" + num2 + ")";
	}

	private static string OrNone(string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}
		return "(none)";
	}

	private static string OrDefault(string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}
		return "(default)";
	}

	private static string TrySaveGenerationReport(string report)
	{
		string path = "SpoolGenReport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
		string[] array = new string[2]
		{
			Path.Combine(SpoolingManagerSettings.SettingsFolderPath, "TestingReports"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spooling-Savant-V2", "SpoolingManager", "TestingReports")
		};
		foreach (string text in array)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(text))
				{
					continue;
				}
				Directory.CreateDirectory(text);
				string text2 = Path.Combine(text, path);
				File.WriteAllText(text2, report, Encoding.UTF8);
				return text2;
			}
			catch
			{
			}
		}
		return null;
	}

	// Truncate AutoDimDiagnostics.log at the start of each generation run so the file always shows ONLY the
	// latest run instead of endlessly appending (it had grown to multiple MB, which made it look like it was
	// "not overwriting" because fresh entries were buried at the bottom).
	private static void ResetAutoDimDiagnosticLog()
	{
		if (!TestingReportsEnabled && !AutoDimPlacementLogEnabled)
		{
			return;
		}
		try
		{
			string header = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t--- new generation run ---" + Environment.NewLine;
			string[] array = new string[2]
			{
				Path.Combine(SpoolingManagerSettings.SettingsFolderPath, "TestingReports"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spooling-Savant-V2", "SpoolingManager", "TestingReports")
			};
			foreach (string text in array)
			{
				if (string.IsNullOrWhiteSpace(text))
				{
					continue;
				}
				Directory.CreateDirectory(text);
				if (TestingReportsEnabled)
				{
					File.WriteAllText(Path.Combine(text, "AutoDimDiagnostics.log"), header, Encoding.UTF8);
				}
				if (AutoDimPlacementLogEnabled)
				{
					try
					{
						Version workerVersion = typeof(CreateSpoolSheetsHandler).Assembly.GetName().Version;
						header = header + "worker=" + workerVersion + Environment.NewLine;
					}
					catch
					{
					}
					File.WriteAllText(Path.Combine(text, "AutoDimPlacement.log"), header, Encoding.UTF8);
				}
				return;
			}
		}
		catch
		{
		}
	}

	private static void TryAppendAutoDimDiagnosticLog(string assemblyName, string viewLabel, string message, int dimsBefore, int dimsAfter)
	{
		if (!TestingReportsEnabled && !AutoDimPlacementLogEnabled)
		{
			return;
		}
		try
		{
			string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			string contents = text + "\t" + assemblyName + "\t" + viewLabel + "\tbefore=" + dimsBefore + "\tafter=" + dimsAfter + "\t" + message + Environment.NewLine;
			string[] array = new string[2]
			{
				Path.Combine(SpoolingManagerSettings.SettingsFolderPath, "TestingReports"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spooling-Savant-V2", "SpoolingManager", "TestingReports")
			};
			foreach (string text2 in array)
			{
				if (string.IsNullOrWhiteSpace(text2))
				{
					continue;
				}
				Directory.CreateDirectory(text2);
				string logName = AutoDimPlacementLogEnabled ? "AutoDimPlacement.log" : "AutoDimDiagnostics.log";
				if (TestingReportsEnabled)
				{
					logName = "AutoDimDiagnostics.log";
				}
				File.AppendAllText(Path.Combine(text2, logName), contents, Encoding.UTF8);
				return;
			}
		}
		catch
		{
		}
	}

	private static void TryAppendAutoDimPlacementLog(string viewLabel, string message)
	{
		if (!AutoDimPlacementLogEnabled)
		{
			return;
		}
		TryAppendAutoDimDiagnosticLog("placement", viewLabel, message, 0, 0);
	}

	private static bool AreSameDimensionReference(Document doc, Reference a, Reference b)
	{
		if (a == null || b == null)
		{
			return false;
		}
		try
		{
			return string.Equals(a.ConvertToStableRepresentation(doc), b.ConvertToStableRepresentation(doc), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return Reference.Equals(a, b);
		}
	}

	/// <summary>Bare pipe E-E: pick a different witness ref for each open end on the same fabrication pipe.</summary>
	private static bool TryResolveOppositePipeOpenEndReferences(
		Element pipeElement,
		View view,
		XYZ nearEndWorld,
		XYZ farEndWorld,
		XYZ axisHint,
		out Reference nearEndRef,
		out Reference farEndRef)
	{
		nearEndRef = null;
		farEndRef = null;
		if (pipeElement == null || nearEndWorld == null || farEndWorld == null)
		{
			return false;
		}
		if (!TryPickBestScoredFabricationAnchorReference(pipeElement, view, nearEndWorld, FabricationDimensionRefRole.PipeOpenEnd, axisHint, applySnapFilter: false, out nearEndRef))
		{
			return false;
		}
		Document doc = pipeElement.Document;
		string nearStable = null;
		try
		{
			nearStable = nearEndRef.ConvertToStableRepresentation(doc);
		}
		catch
		{
		}
		Reference best = null;
		double bestScore = double.NegativeInfinity;
		foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(pipeElement, view))
		{
			if (item.Reference == null || IsWholeElementDimensionReference(pipeElement, item.Reference))
			{
				continue;
			}
			try
			{
				if (nearStable != null && string.Equals(item.Reference.ConvertToStableRepresentation(doc), nearStable, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
			}
			catch
			{
				continue;
			}
			double score = ScoreFabricationInstanceReferenceForDimension(pipeElement, item.Reference, item.Curve, farEndWorld, FabricationDimensionRefRole.PipeOpenEnd, axisHint, applySnapFilter: false);
			if (score > bestScore)
			{
				bestScore = score;
				best = item.Reference;
			}
		}
		farEndRef = best;
		return farEndRef != null && !double.IsNegativeInfinity(bestScore);
	}

	/// <summary>Assembly is pipe only — place required E-E (open end to open end).</summary>
	private static bool TryCreateBarePipeOpenEndToOpenEndDimension(
		Document doc,
		View view,
		List<FabricationPart> parts,
		XYZ unitAxis,
		XYZ vn,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		out string diagnostic)
	{
		diagnostic = null;
		FabricationPart pipePart = GetDominantCollinearRunPipePart(parts, unitAxis, vn) ?? GetDominantStraightFabricationPart(parts);
		if (pipePart == null)
		{
			diagnostic = "Auto-dimension (bare pipe E-E): no straight pipe run in assembly.";
			return false;
		}
		FabricationPart minOwner = null;
		XYZ minPt = null;
		FabricationPart maxOwner = null;
		XYZ maxPt = null;
		bool hasOletOnRun = parts.Any((FabricationPart p) => p != null && IsOletPart(p));
		if (hasOletOnRun
			&& TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, vn, forward: false, out minOwner, out minPt)
			&& TryIsOpenCollinearPipeEnd(parts, minOwner, minPt)
			&& TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, vn, forward: true, out maxOwner, out maxPt)
			&& TryIsOpenCollinearPipeEnd(parts, maxOwner, maxPt))
		{
			// Olet/outlet on host run — overall spans the full weld-connected collinear chain, not one segment.
		}
		else if (!TryGetMinimumPipeEndAnchor(parts, pipePart, unitAxis, vn, out minOwner, out minPt)
			|| !TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out maxOwner, out maxPt))
		{
			diagnostic = "Auto-dimension (bare pipe E-E): could not resolve both open pipe ends.";
			return false;
		}
		if (minPt.DistanceTo(maxPt) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension (bare pipe E-E): pipe ends are too close together.";
			return false;
		}
		if (TryPlaceSpoolLinearDimensionSleeveStyle(
			doc,
			view,
			(Element)(object)minOwner,
			minPt,
			(Element)(object)maxOwner,
			maxPt,
			spoolSettings,
			ref stackIndex,
			out string failureDetail,
			FabricationDimensionRefRole.PipeOpenEnd,
			FabricationDimensionRefRole.PipeOpenEnd))
		{
			return true;
		}
		diagnostic = "Auto-dimension (bare pipe E-E): could not place dimension in this view.";
		if (!string.IsNullOrEmpty(failureDetail))
		{
			diagnostic = diagnostic + " " + failureDetail;
		}
		return false;
	}

	private static void TryLogPlacedDimensionReferences(View view, Dimension dim, Reference refA, Reference refB)
	{
		try
		{
			Document doc = view?.Document;
			if (doc == null)
			{
				return;
			}
			string Stable(Reference reference)
			{
				try
				{
					return reference?.ConvertToStableRepresentation(doc) ?? "null";
				}
				catch (Exception ex)
				{
					return "<err:" + ex.Message + ">";
				}
			}
			string message = "placed-dim id=" + ((Element)dim).Id.Value
				+ " value=\"" + (dim.ValueString ?? "?") + "\""
				+ " refA=" + Stable(refA)
				+ " refB=" + Stable(refB);
			TryAppendAutoDimDiagnosticLog("placed-ref", view?.Name ?? "?", message, 0, 0);
		}
		catch
		{
		}
	}

	private static void TryLogDimensionAttemptFailure(View view, Reference refA, Reference refB, string reason)
	{
		try
		{
			Document doc = view?.Document;
			if (doc == null || string.IsNullOrWhiteSpace(reason))
			{
				return;
			}
			string Stable(Reference reference)
			{
				try
				{
					return reference?.ConvertToStableRepresentation(doc) ?? "null";
				}
				catch (Exception ex)
				{
					return "<err:" + ex.Message + ">";
				}
			}
			string message = "dim-attempt-fail refA=" + Stable(refA) + " refB=" + Stable(refB) + " reason=" + reason;
			TryAppendAutoDimDiagnosticLog("placed-ref", view?.Name ?? "?", message, 0, 0);
		}
		catch
		{
		}
	}

	private enum FabricationDimensionRefRole
	{
		Generic,
		RunStartFitting,
		PipeOpenEnd,
		OletBranch,
		VerticalDropEnd,
		FlangeFace,
		PipeCenterline
	}

	private static FabricationDimensionRefRole ResolveFabricationDimensionRefRole(Element element, bool isPipeEndAnchor)
	{
		FabricationPart fabricationPart = (FabricationPart)(object)((element is FabricationPart) ? element : null);
		if (fabricationPart != null && IsPipeRunPart(fabricationPart))
		{
			return FabricationDimensionRefRole.PipeOpenEnd;
		}
		if (isPipeEndAnchor)
		{
			return FabricationDimensionRefRole.PipeOpenEnd;
		}
		if (fabricationPart != null && IsOletPart(fabricationPart))
		{
			return FabricationDimensionRefRole.OletBranch;
		}
		return FabricationDimensionRefRole.RunStartFitting;
	}

	private static int TryExtractFabricationInstanceCurveIndex(Document doc, Reference reference)
	{
		if (doc == null || reference == null)
		{
			return -1;
		}
		string stable;
		try
		{
			stable = reference.ConvertToStableRepresentation(doc);
		}
		catch
		{
			return -1;
		}
		if (string.IsNullOrWhiteSpace(stable))
		{
			return -1;
		}
		if (stable.EndsWith(":LINEAR", StringComparison.OrdinalIgnoreCase))
		{
			stable = stable.Substring(0, stable.Length - ":LINEAR".Length);
		}
		int lastColon = stable.LastIndexOf(':');
		if (lastColon < 0 || lastColon >= stable.Length - 1)
		{
			return -1;
		}
		if (int.TryParse(stable.Substring(lastColon + 1), out int index))
		{
			return index;
		}
		return -1;
	}

	private static double ScoreFabricationInstanceReferenceForDimension(Element element, Reference reference, Curve curve, XYZ targetWorld, FabricationDimensionRefRole role, XYZ axisHint = null, bool applySnapFilter = true)
	{
		if (element == null || reference == null)
		{
			return double.NegativeInfinity;
		}
		Document doc = element.Document;
		string stable;
		try
		{
			stable = reference.ConvertToStableRepresentation(doc);
		}
		catch
		{
			return double.NegativeInfinity;
		}
		if (string.IsNullOrWhiteSpace(stable) || stable.IndexOf(":LINEAR", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return double.NegativeInfinity;
		}
		int index = TryExtractFabricationInstanceCurveIndex(doc, reference);
		if (element is FabricationPart valvePart && IsValvePart(valvePart))
		{
			return double.NegativeInfinity;
		}
		if (applySnapFilter && element is FabricationPart && curve != null)
		{
		}
		int preferredIndex = role switch
		{
			FabricationDimensionRefRole.PipeOpenEnd => 8,
			FabricationDimensionRefRole.OletBranch => 12,
			FabricationDimensionRefRole.RunStartFitting => 10,
			FabricationDimensionRefRole.VerticalDropEnd => 6,
			FabricationDimensionRefRole.FlangeFace => 18,
			FabricationDimensionRefRole.PipeCenterline => 8,
			_ => -1
		};
		double indexPriority;
		if (preferredIndex >= 0 && index == preferredIndex)
		{
			indexPriority = 20000.0;
		}
		else
		{
			switch (index)
			{
			case 10:
				indexPriority = 10000.0;
				break;
			case 12:
				indexPriority = 9000.0;
				break;
			case 8:
				indexPriority = 8000.0;
				break;
			case 6:
				indexPriority = 7000.0;
				break;
			default:
				indexPriority = (index >= 0) ? (double)index : 0.0;
				break;
			}
		}
		double length = (curve != null && curve.IsBound) ? curve.Length : 0.0;
		double dist = 0.0;
		XYZ distPoint = null;
		if (targetWorld != null)
		{
			if (curve != null && curve.IsBound)
			{
				if (role == FabricationDimensionRefRole.PipeOpenEnd || role == FabricationDimensionRefRole.VerticalDropEnd)
				{
					dist = MinimumDistanceFromBoundedCurveEndpointsToPoint(curve, targetWorld);
				}
				else
				{
					XYZ closest = ClosestPointOnBoundedModelCurveWorld(curve, targetWorld);
					if (closest != null)
					{
						dist = closest.DistanceTo(targetWorld);
						distPoint = closest;
					}
				}
			}
			else if (TryGetReferenceSampleWorldPointForTarget(element, reference, targetWorld, out XYZ sample) && sample != null)
			{
				dist = sample.DistanceTo(targetWorld);
				distPoint = sample;
			}
		}
		if (role == FabricationDimensionRefRole.PipeOpenEnd)
		{
			const double maxPipeEndPickDistFeet = 2.0;
			if (dist > maxPipeEndPickDistFeet)
			{
				return double.NegativeInfinity;
			}
			double shortEndBonus = (length < 1.0) ? 50000.0 : ((length < 5.0) ? 10000.0 : 0.0);
			double indexTie = (preferredIndex >= 0 && index == preferredIndex) ? 1.0 : 0.0;
			return 100000.0 - dist * 50000.0 + shortEndBonus - length * 100.0 + indexTie;
		}
		if (role == FabricationDimensionRefRole.FlangeFace)
		{
			// Face CENTER on the run CL only. Index 9 is often a bottom bolt-circle node ~1.75"
			// off the CL and tilts every H dim to the elbow.
			double pickDist = dist;
			const double maxFlangeFaceOffTargetFeet = 0.04; // ~1/2" — must sit on intended face-center target
			if (pickDist > maxFlangeFaceOffTargetFeet)
			{
				return double.NegativeInfinity;
			}
			// Prefer true face-center indices; never boost the classic off-CL "9" node.
			double capBonus = (index == 18) ? 4000.0
				: ((index == 8 || index == 10 || index == 12 || index == 7) ? 1500.0 : 0.0);
			if (index == 9)
			{
				capBonus = -5000.0;
			}
			return 50000.0 - pickDist * 20000.0 + capBonus - length * 50.0;
		}
		if (role == FabricationDimensionRefRole.VerticalDropEnd)
		{
			const double maxDropEndPickDistFeet = 3.0;
			if (dist > maxDropEndPickDistFeet)
			{
				return double.NegativeInfinity;
			}
			double shortEndBonus = (length < 1.0) ? 25000.0 : ((length < 5.0) ? 5000.0 : 0.0);
			return 50000.0 - dist * 25000.0 + shortEndBonus - length * 50.0;
		}
		if (role == FabricationDimensionRefRole.PipeCenterline)
		{
			// Perpendicular distance to a 20' main-run centerline is ~0 everywhere on the pipe, so the old
			// lengthBonus favored the longest curve and Revit snapped to the far open end (~15'+ C-C ghosts).
			// Score by axial distance to the branch anchor along the measurement axis instead.
			double pickDist = dist;
			if (axisHint != null && distPoint != null && targetWorld != null && axisHint.GetLength() > 1E-09)
			{
				pickDist = Math.Abs((distPoint - targetWorld).DotProduct(axisHint.Normalize()));
			}
			const double maxCenterlinePickDistFeet = 1.5;
			if (pickDist > maxCenterlinePickDistFeet)
			{
				return double.NegativeInfinity;
			}
			return 50000.0 - pickDist * 20000.0 - length * 5.0;
		}
		if (role == FabricationDimensionRefRole.OletBranch)
		{
			// Olet/anvilet refs sit off the horizontal pick-up axis; axial scoring along the run rejected every
			// candidate and none of the stacked E-C pick-ups could place (CHW-16 Front).
			const double maxOletBranchPickDistFeet = 2.5;
			if (dist > maxOletBranchPickDistFeet)
			{
				return double.NegativeInfinity;
			}
			return 50000.0 - dist * 20000.0 - length * 5.0;
		}
		if (role == FabricationDimensionRefRole.RunStartFitting)
		{
			// Centerline-intersection snaps only (elbow point 7 / tee hub); port-face centers and corners are rejected upstream.
			if (length > 2.0)
			{
				return double.NegativeInfinity;
			}
			double pickDist = dist;
			if (axisHint != null && distPoint != null && targetWorld != null && axisHint.GetLength() > 1E-09)
			{
				pickDist = Math.Abs((distPoint - targetWorld).DotProduct(axisHint.Normalize()));
			}
			const double maxFittingCenterPickDistFeet = 0.35;
			if (pickDist > maxFittingCenterPickDistFeet)
			{
				return double.NegativeInfinity;
			}
			double shortCurveBonus = (length < 0.5) ? 5000.0 : 0.0;
			double indexBonus = (index == 10) ? 3000.0 : 0.0;
			return 50000.0 - pickDist * 20000.0 + shortCurveBonus + indexBonus - length * 5.0;
		}
		double lengthBonus = length * 10.0;
		return indexPriority + lengthBonus - dist;
	}

	private static double TryGetFabricationReferenceAxialDistanceToTarget(Element element, View view, Reference reference, XYZ targetWorld, XYZ axisUnit)
	{
		if (element == null || reference == null || targetWorld == null || axisUnit == null || axisUnit.GetLength() < 1E-09)
		{
			return double.MaxValue;
		}
		XYZ axis = axisUnit.Normalize();
		foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(element, view))
		{
			if (item.Reference == null || item.Curve == null)
			{
				continue;
			}
			Document doc = element.Document;
			try
			{
				if (!string.Equals(item.Reference.ConvertToStableRepresentation(doc), reference.ConvertToStableRepresentation(doc), StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
			}
			catch
			{
				continue;
			}
			XYZ closest = ClosestPointOnBoundedModelCurveWorld(item.Curve, targetWorld);
			if (closest == null)
			{
				return double.MaxValue;
			}
			return Math.Abs((closest - targetWorld).DotProduct(axis));
		}
		if (TryGetReferenceSampleWorldPointForTarget(element, reference, targetWorld, out XYZ sample) && sample != null)
		{
			return Math.Abs((sample - targetWorld).DotProduct(axis));
		}
		return double.MaxValue;
	}

	private static bool TryPickBestFlangeSideReferenceByMaxSpan(
		Document doc,
		View dimView,
		View view,
		XYZ pStart,
		XYZ pEnd,
		XYZ perp,
		XYZ vn,
		XYZ chord,
		List<Reference> flangeRefs,
		bool flangeOnFitSide,
		Reference lockedOtherRef,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		out Reference bestFlangeRef,
		out Dimension placedDim,
		out string failureDetail,
		int offsetSign,
		bool lockOffsetSign,
		XYZ branchFacingDirection,
		string dimensionPolicyRole)
	{
		bestFlangeRef = null;
		placedDim = null;
		failureDetail = null;
		double bestSpan = double.NegativeInfinity;
		int stackSnapshot = stackIndex;
		foreach (Reference flangeRef in flangeRefs)
		{
			Reference refFit = flangeOnFitSide ? flangeRef : lockedOtherRef;
			Reference refPipe = flangeOnFitSide ? lockedOtherRef : flangeRef;
			int stackAttempt = stackIndex;
			if (!TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, pStart, pEnd, perp, vn, chord, refFit, refPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, logPlacement: false))
			{
				continue;
			}
			double committedValue;
			try
			{
				committedValue = placedDim?.Value ?? 0.0;
			}
			catch
			{
				committedValue = 0.0;
			}
			if (committedValue > bestSpan)
			{
				bestSpan = committedValue;
				bestFlangeRef = flangeRef;
			}
			try
			{
				if (placedDim != null)
				{
					doc.Delete(placedDim.Id);
				}
			}
			catch
			{
			}
			placedDim = null;
			stackIndex = stackAttempt;
		}
		stackIndex = stackSnapshot;
		return bestFlangeRef != null;
	}

	private static List<Reference> GetAllFabricationInstanceDimensionReferences(Element element, View view, XYZ targetWorld, FabricationDimensionRefRole role, XYZ axisHint = null)
	{
		List<Reference> list = new List<Reference>();
		if (element == null)
		{
			return list;
		}
		Document doc = element.Document;
		HashSet<string> seenStables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// For flanges: do NOT prepend lessons first — they often resolve to index 9 (bottom node off CL)
		// and tilt H dims. Score face-center points against the intended CL target first.
		if (role != FabricationDimensionRefRole.FlangeFace)
		{
			PrependLessonWitnessReferences(doc, view, element, role, list, seenStables);
		}

		List<(Reference reference, double score)> scored = new List<(Reference, double)>();
		foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(element, view))
		{
			if (item.Reference == null || IsWholeElementDimensionReference(element, item.Reference))
			{
				continue;
			}
			string stable;
			try
			{
				stable = item.Reference.ConvertToStableRepresentation(doc);
			}
			catch
			{
				continue;
			}
			if (string.IsNullOrWhiteSpace(stable)
				|| stable.IndexOf(":LINEAR", StringComparison.OrdinalIgnoreCase) >= 0
				|| !seenStables.Add(stable))
			{
				continue;
			}
			double score = ScoreFabricationInstanceReferenceForDimension(element, item.Reference, item.Curve, targetWorld, role, axisHint);
			if (double.IsNegativeInfinity(score))
			{
				continue;
			}
			scored.Add((item.Reference, score));
		}
		foreach ((Reference reference, double score) item2 in scored.OrderByDescending(((Reference reference, double score) s) => s.score))
		{
			list.Add(item2.reference);
		}

		// Flange lessons last, and only if they sit on the intended face-center target.
		if (role == FabricationDimensionRefRole.FlangeFace && element is FabricationPart flangePart)
		{
			if (TryResolveFabricationOriginReference(doc, view, flangePart, out Reference lessonRef, out _)
				&& lessonRef != null)
			{
				string stableLesson;
				try
				{
					stableLesson = lessonRef.ConvertToStableRepresentation(doc);
				}
				catch
				{
					stableLesson = null;
				}
				double lessonScore = ScoreFabricationInstanceReferenceForDimension(
					element, lessonRef, null, targetWorld, role, axisHint);
				if (!string.IsNullOrWhiteSpace(stableLesson)
					&& seenStables.Add(stableLesson)
					&& !double.IsNegativeInfinity(lessonScore))
				{
					list.Add(lessonRef);
				}
			}
		}

		return list;
	}

	/// <summary>Flange F refs for stub C-F — lesson indices first, then scored curve fallback when view lessons miss.</summary>
	private static List<Reference> GetFlangeFaceDimensionReferencesWithFallback(Element element, View view, XYZ targetWorld, XYZ axisHint)
	{
		List<Reference> list = GetAllFabricationInstanceDimensionReferences(element, view, targetWorld, FabricationDimensionRefRole.FlangeFace, axisHint);
		if (list.Count > 0 || !(element is FabricationPart flangePart))
		{
			return list;
		}
		Document doc = element.Document;
		if (TryResolveFlangeFaceReference(doc, view, flangePart, out Reference lessonRef, out _))
		{
			list.Add(lessonRef);
			return list;
		}
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<(Reference reference, double score)> scored = new List<(Reference, double)>();
		foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(element, view))
		{
			if (item.Reference == null || IsWholeElementDimensionReference(element, item.Reference))
			{
				continue;
			}
			string stable;
			try
			{
				stable = item.Reference.ConvertToStableRepresentation(doc);
			}
			catch
			{
				continue;
			}
			if (string.IsNullOrWhiteSpace(stable) || !seen.Add(stable))
			{
				continue;
			}
			double score = ScoreFabricationInstanceReferenceForDimension(element, item.Reference, item.Curve, targetWorld, FabricationDimensionRefRole.FlangeFace, axisHint);
			if (double.IsNegativeInfinity(score))
			{
				continue;
			}
			scored.Add((item.Reference, score));
		}
		foreach ((Reference reference, double score) item in scored.OrderByDescending(s => s.score))
		{
			list.Add(item.reference);
		}
		return list;
	}

	/// <summary>Step 2 lesson refs first — pipe :14/:11 SURFACE (E), elbow/tee :10 (C), olet :12/:10 (C).</summary>
	private static void PrependLessonWitnessReferences(
		Document doc,
		View view,
		Element element,
		FabricationDimensionRefRole role,
		List<Reference> list,
		HashSet<string> seenStables)
	{
		if (doc == null || view == null || element == null || list == null || seenStables == null)
		{
			return;
		}

		FabricationPart part = element as FabricationPart;
		if (part == null)
		{
			return;
		}

		bool wantsLessonRef = role == FabricationDimensionRefRole.PipeOpenEnd
			|| role == FabricationDimensionRefRole.VerticalDropEnd
			|| role == FabricationDimensionRefRole.OletBranch
			|| role == FabricationDimensionRefRole.RunStartFitting
			|| role == FabricationDimensionRefRole.FlangeFace;
		if (!wantsLessonRef)
		{
			return;
		}

		if (!TryResolveFabricationOriginReference(doc, view, part, out Reference lessonRef, out _))
		{
			return;
		}

		string stable;
		try
		{
			stable = lessonRef.ConvertToStableRepresentation(doc);
		}
		catch
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(stable) || !seenStables.Add(stable))
		{
			return;
		}

		list.Insert(0, lessonRef);
	}

	private static bool TryResolveFabricationAnchorReference(
		Element element,
		View view,
		XYZ targetWorld,
		FabricationDimensionRefRole role,
		XYZ axisHint,
		out Reference reference)
	{
		return TryResolveFabricationAnchorReference(element, view, targetWorld, role, axisHint, allowSpecializedResolvers: true, out reference);
	}

	private static bool TryResolveFabricationAnchorReference(
		Element element,
		View view,
		XYZ targetWorld,
		FabricationDimensionRefRole role,
		XYZ axisHint,
		bool allowSpecializedResolvers,
		out Reference reference)
	{
		reference = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		if (!(element is FabricationPart part))
		{
			return TryGetDimensionReferenceAtWorldPoint(element, view, targetWorld, axisHint, null, out reference, out _);
		}
		if (IsValvePart(part))
		{
			reference = null;
			return false;
		}
		if (role == FabricationDimensionRefRole.FlangeFace
			|| role == FabricationDimensionRefRole.PipeOpenEnd
			|| role == FabricationDimensionRefRole.VerticalDropEnd
			|| role == FabricationDimensionRefRole.RunStartFitting
			|| role == FabricationDimensionRefRole.OletBranch)
		{
			if (TryResolveFabricationOriginReference(part.Document, view, part, out Reference lessonRef, out _))
			{
				reference = lessonRef;
				return true;
			}
			if (role == FabricationDimensionRefRole.FlangeFace)
			{
				return TryPickBestScoredFabricationAnchorReference(element, view, targetWorld, role, axisHint, applySnapFilter: false, out reference);
			}
		}
		return TryPickBestScoredFabricationAnchorReference(element, view, targetWorld, role, axisHint, applySnapFilter: false, out reference);
	}

	private static bool TryPickBestScoredFabricationAnchorReference(
		Element element,
		View view,
		XYZ targetWorld,
		FabricationDimensionRefRole role,
		XYZ axisHint,
		bool applySnapFilter,
		out Reference reference)
	{
		reference = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		Reference best = null;
		double bestScore = double.NegativeInfinity;
		foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(element, view))
		{
			if (item.Reference == null || IsWholeElementDimensionReference(element, item.Reference))
			{
				continue;
			}
			double score = ScoreFabricationInstanceReferenceForDimension(element, item.Reference, item.Curve, targetWorld, role, axisHint, applySnapFilter);
			if (score > bestScore)
			{
				bestScore = score;
				best = item.Reference;
			}
		}
		if (best == null || bestScore == double.NegativeInfinity)
		{
			return false;
		}
		reference = best;
		return true;
	}

	private static Reference TryResolveFabricationCenterlineReference(Element element, View view, XYZ targetWorld, XYZ axisHint)
	{
		if (element == null || targetWorld == null || !(element is FabricationPart))
		{
			return null;
		}
		if (TryPickBestScoredFabricationAnchorReference(element, view, targetWorld, FabricationDimensionRefRole.RunStartFitting, axisHint, applySnapFilter: false, out Reference reference))
		{
			return reference;
		}
		return null;
	}

	private static int CountViewLinearDimensions(Document doc, View view)
	{
		if (doc == null || view == null)
		{
			return 0;
		}
		try
		{
			int num = new FilteredElementCollector(doc, ((Element)view).Id).OfCategory(BuiltInCategory.OST_Dimensions).WhereElementIsNotElementType().GetElementCount();
			if (num > 0)
			{
				return num;
			}
		}
		catch
		{
		}
		try
		{
			return (from Dimension d in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(Dimension))
				where d != null && d.OwnerViewId == ((Element)view).Id
				select d).Count();
		}
		catch
		{
			return 0;
		}
	}

	private static bool IsAssemblyAssociatedView(View view)
	{
		if (view == null)
		{
			return false;
		}
		try
		{
			return view.AssociatedAssemblyInstanceId != ElementId.InvalidElementId;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryEnsureActiveDocumentView(UIDocument uidoc, View view)
	{
		if (view == null)
		{
			return false;
		}
		if (uidoc == null)
		{
			return true;
		}
		try
		{
			if (uidoc.ActiveView != null && ((Element)uidoc.ActiveView).Id == ((Element)view).Id)
			{
				return true;
			}
			uidoc.RequestViewChange(view);
		}
		catch
		{
		}
		try
		{
			uidoc.ActiveView = view;
		}
		catch
		{
			return false;
		}
		try
		{
			return uidoc.ActiveView != null && ((Element)uidoc.ActiveView).Id == ((Element)view).Id;
		}
		catch
		{
			return false;
		}
	}

	private static void EnsureAutoDimHasElevationView(SpoolingManagerSettings settings)
	{
		if (settings == null)
		{
			return;
		}
		bool hasElevationView = settings.IncludeBackView || settings.IncludeFrontView || settings.IncludeLeftView || settings.IncludeRightView || settings.IncludeTopView;
		if (hasElevationView)
		{
			return;
		}
		bool wantsElevationAutoDim = settings.AutoDimBackView || settings.AutoDimFrontView || settings.AutoDimLeftView || settings.AutoDimRightView || settings.AutoDimTopView;
		if (!wantsElevationAutoDim)
		{
			return;
		}
		settings.IncludeTopView = true;
		settings.AutoDimTopView = true;
		if (string.IsNullOrWhiteSpace(settings.TemplateTopView) && !string.IsNullOrWhiteSpace(settings.Template3D))
		{
			settings.TemplateTopView = settings.Template3D;
		}
		if (string.IsNullOrWhiteSpace(settings.PlacementTopView))
		{
			settings.PlacementTopView = "Bottom Right";
		}
	}

	private static void TryEnsureSpoolViewAutoDimensionVisibility(Document doc, View view)
	{
		if (view == null || view is View3D)
		{
			return;
		}
		TryEnsureSpoolViewGeometryForAnnotation(doc, view);
		TrySetViewCategoryVisible(view, BuiltInCategory.OST_Dimensions);
		TryExpandViewAnnotationCropForDimensions(view);
	}

	private static void TryAllowAnnotationGraphicsOutsideTemplateControl(View view)
	{
		if (view == null || view.ViewTemplateId == ElementId.InvalidElementId)
		{
			return;
		}
		try
		{
			ICollection<ElementId> nonControlledTemplateParameterIds = view.GetNonControlledTemplateParameterIds();
			List<ElementId> list = ((nonControlledTemplateParameterIds == null) ? new List<ElementId>() : nonControlledTemplateParameterIds.ToList());
			bool flag = false;
			foreach (Parameter parameter2 in ((Element)view).Parameters)
			{
				Parameter parameter = parameter2;
				if (parameter == null || ((APIObject)parameter).IsReadOnly)
				{
					continue;
				}
				Definition definition = parameter.Definition;
				string text = ((definition != null) ? definition.Name : null) ?? string.Empty;
				if ((text.IndexOf("Annotation", StringComparison.OrdinalIgnoreCase) >= 0 ||
				     text.IndexOf("Discipline", StringComparison.OrdinalIgnoreCase) >= 0 ||
				     text.Equals("Visibility/Graphics Overrides", StringComparison.OrdinalIgnoreCase)) &&
				    !list.Any((ElementId id) => id == parameter.Id))
				{
					list.Add(parameter.Id);
					flag = true;
				}
			}
			if (flag)
			{
				view.SetNonControlledTemplateParameterIds((ICollection<ElementId>)list);
			}
		}
		catch
		{
		}
	}

	private static void TryUnhideAllViewDimensions(Document doc, View view)
	{
		if (doc == null || view == null)
		{
			return;
		}
		try
		{
			List<ElementId> list = new FilteredElementCollector(doc, ((Element)view).Id)
				.OfCategory(BuiltInCategory.OST_Dimensions)
				.WhereElementIsNotElementType()
				.ToElementIds()
				.ToList();
			if (list.Count > 0)
			{
				view.UnhideElements((ICollection<ElementId>)(object)list);
			}
		}
		catch
		{
		}
	}

	private static void TrySetViewCategoryVisible(View view, BuiltInCategory category)
	{
		if (view == null)
		{
			return;
		}
		try
		{
			ElementId val = new ElementId(category);
			if (view.CanCategoryBeHidden(val))
			{
				view.SetCategoryHidden(val, false);
			}
		}
		catch
		{
		}
	}

	private static bool IsAutoDimEnabledForView(SpoolingManagerKind kind, bool autoDimRequested, bool viewIncluded, bool is3DView)
	{
		if (kind.IsMmcTesting() || is3DView || !viewIncluded)
		{
			return false;
		}
		return autoDimRequested;
	}

	private static List<ViewBuildOption> BuildViewOptions(SpoolingManagerSettings settings, SpoolingManagerKind kind)
	{
		return new List<ViewBuildOption>
		{
			new ViewBuildOption
			{
				Include = settings.Include3DOrtho,
				Label = "3D Ortho",
				Placement = settings.Placement3D,
				TemplateName = settings.Template3D,
				TagEnabled = settings.Tag3D,
				AutoDimEnabled = IsAutoDimEnabledForView(kind, settings.AutoDim3D, settings.Include3DOrtho, is3DView: true),
				CreateView = (Document doc, AssemblyInstance assembly) => (View)(object)AssemblyViewUtils.Create3DOrthographic(doc, ((Element)assembly).Id)
			},
			new ViewBuildOption
			{
				Include = settings.IncludeBackView,
				Label = "Back View",
				Placement = settings.PlacementBackView,
				TemplateName = settings.TemplateBackView,
				TagEnabled = settings.TagBackView,
				AutoDimEnabled = IsAutoDimEnabledForView(kind, settings.AutoDimBackView, settings.IncludeBackView, is3DView: false),
				SheetRotation = settings.BackViewRotation,
				CreateView = (Document doc, AssemblyInstance assembly) => (View)(object)AssemblyViewUtils.CreateDetailSection(doc, ((Element)assembly).Id, (AssemblyDetailViewOrientation)8)
			},
			new ViewBuildOption
			{
				Include = settings.IncludeFrontView,
				Label = "Front View",
				Placement = settings.PlacementFrontView,
				TemplateName = settings.TemplateFrontView,
				TagEnabled = settings.TagFrontView,
				AutoDimEnabled = IsAutoDimEnabledForView(kind, settings.AutoDimFrontView, settings.IncludeFrontView, is3DView: false),
				SheetRotation = settings.FrontViewRotation,
				CreateView = (Document doc, AssemblyInstance assembly) => (View)(object)AssemblyViewUtils.CreateDetailSection(doc, ((Element)assembly).Id, (AssemblyDetailViewOrientation)7)
			},
			new ViewBuildOption
			{
				Include = settings.IncludeLeftView,
				Label = "Left View",
				Placement = settings.PlacementLeftView,
				TemplateName = settings.TemplateLeftView,
				TagEnabled = settings.TagLeftView,
				AutoDimEnabled = IsAutoDimEnabledForView(kind, settings.AutoDimLeftView, settings.IncludeLeftView, is3DView: false),
				SheetRotation = settings.LeftViewRotation,
				CreateView = (Document doc, AssemblyInstance assembly) => (View)(object)AssemblyViewUtils.CreateDetailSection(doc, ((Element)assembly).Id, (AssemblyDetailViewOrientation)5)
			},
			new ViewBuildOption
			{
				Include = settings.IncludeRightView,
				Label = "Right View",
				Placement = settings.PlacementRightView,
				TemplateName = settings.TemplateRightView,
				TagEnabled = settings.TagRightView,
				AutoDimEnabled = IsAutoDimEnabledForView(kind, settings.AutoDimRightView, settings.IncludeRightView, is3DView: false),
				SheetRotation = settings.RightViewRotation,
				CreateView = (Document doc, AssemblyInstance assembly) => (View)(object)AssemblyViewUtils.CreateDetailSection(doc, ((Element)assembly).Id, (AssemblyDetailViewOrientation)6)
			},
			new ViewBuildOption
			{
				Include = settings.IncludeTopView,
				Label = "Top View",
				Placement = settings.PlacementTopView,
				TemplateName = settings.TemplateTopView,
				TagEnabled = settings.TagTopView,
				AutoDimEnabled = IsAutoDimEnabledForView(kind, settings.AutoDimTopView, settings.IncludeTopView, is3DView: false),
				SheetRotation = settings.TopViewRotation,
				CreateView = (Document doc, AssemblyInstance assembly) => (View)(object)AssemblyViewUtils.CreateDetailSection(doc, ((Element)assembly).Id, (AssemblyDetailViewOrientation)3)
			}
		};
	}

	internal static bool HasAnyTaggingEnabled(SpoolingManagerSettings settings)
	{
		if (!settings.Tag3D && !settings.TagBackView && !settings.TagFrontView && !settings.TagLeftView && !settings.TagRightView)
		{
			return settings.TagTopView;
		}
		return true;
	}

	private static string TryApplyAutoDimensions(UIDocument uidoc, Document doc, ViewSheet sheet, View view, AssemblyInstance assembly, ViewBuildOption option, SpoolingManagerSettings spoolSettings, View3D referencePickView3D)
	{
		if (doc == null || sheet == null || view == null || assembly == null || option == null)
		{
			return null;
		}
		if (view is View3D || view.IsTemplate)
		{
			return null;
		}
		View priorActiveView = null;
		bool restoreActiveView = false;
		if (uidoc != null)
		{
			try
			{
				priorActiveView = uidoc.ActiveView;
			}
			catch
			{
				priorActiveView = null;
			}
		}
		_autoDimReferencePickView3D = referencePickView3D;
		try
		{
			if (!_batchSheetGeneration && uidoc != null && priorActiveView != null && ((Element)priorActiveView).Id != ((Element)view).Id)
			{
				restoreActiveView = TryEnsureActiveDocumentView(uidoc, view);
			}
			else if (!_batchSheetGeneration && uidoc != null)
			{
				TryEnsureActiveDocumentView(uidoc, view);
			}
			if (!_batchSheetGeneration && uidoc != null)
			{
				try
				{
					uidoc.RequestViewChange(view);
				}
				catch
				{
				}
				ICollection<ElementId> memberIds = null;
				try
				{
					memberIds = assembly.GetMemberIds();
				}
				catch
				{
					memberIds = null;
				}
				if (memberIds != null && memberIds.Count > 0)
				{
					try
					{
						uidoc.ShowElements(memberIds);
					}
					catch
					{
					}
				}
			}
			if (!_batchSheetGeneration)
			{
				try
				{
					RegenTracked(doc);
				}
				catch
				{
				}
				if (uidoc != null)
				{
					try
					{
						uidoc.RefreshActiveView();
					}
					catch
					{
					}
				}
			}
			EnsureEditableSpoolViewCrop(view);
			if (!_batchSheetGeneration)
			{
				FitSpoolAssemblyDetailViewCropToContent(doc, assembly, view, includeTagExtents: false);
				ExpandViewCropForSpoolAnnotation(doc, view, assembly);
			}
			TryEnsureSpoolViewAutoDimensionVisibility(doc, view);
			int dimsBefore = 0;
			if (!_batchSheetGeneration)
			{
				dimsBefore = CountViewLinearDimensions(doc, view);
			}
			RemoveViewLinearDimensions(doc, view);
			RemoveViewAutoDimensionDetailCurves(doc, view);
			if (!TryApplySpoolAssemblyAutoDimensions(doc, view, assembly, spoolSettings, out var diagnostic))
			{
				TryUnhideAllViewDimensions(doc, view);
				TryAppendAutoDimDiagnosticLog(AssemblyDisplayName.Get(assembly), option.Label, diagnostic ?? "unknown failure", dimsBefore, CountViewLinearDimensions(doc, view));
				return string.IsNullOrEmpty(diagnostic)
					? "Auto-dimension did not create a model dimension (unknown reason)."
					: diagnostic;
			}
			if (!_batchSheetGeneration)
			{
				try
				{
					RegenTracked(doc);
				}
				catch
				{
				}
			}
			else
			{
				FlushPendingRegen(doc);
			}
			TryEnsureSpoolViewAutoDimensionVisibility(doc, view);
			TryUnhideAllViewDimensions(doc, view);
			if (!_batchSheetGeneration)
			{
				int dimsAfter = CountViewLinearDimensions(doc, view);
				TryAppendAutoDimDiagnosticLog(AssemblyDisplayName.Get(assembly), option.Label, "completed", dimsBefore, dimsAfter);
				// Placement may intentionally place nothing (clean slate). Only fit crop when dims exist.
				if (dimsAfter > 0)
				{
					FitSpoolAssemblyDetailViewCropToContent(doc, assembly, view, includeTagExtents: false, includeDimensionExtents: true, includeDimensionLabelExtents: true);
				}
			}
			return null;
		}
		catch (Exception ex)
		{
			return "Auto-dimension failed: " + ex.Message;
		}
		finally
		{
			_autoDimReferencePickView3D = null;
			if (restoreActiveView && uidoc != null && priorActiveView != null)
			{
				try
				{
					uidoc.ActiveView = priorActiveView;
				}
				catch
				{
				}
			}
		}
	}

	// Re-runs auto-dimensioning for an already-generated spool view after its assembly gains/loses members.
	// Uses the document-only dimension path (no UIDocument / no active-view change) so it is safe to call from
	// the background member-sync. Existing auto-dims are cleared first so the updated geometry is measured cleanly
	// instead of stacking a second set of dimensions on the view.
	internal static bool ReapplyAutoDimensionsForView(Document doc, View view, AssemblyInstance assembly, SpoolingManagerSettings settings)
	{
		if (doc == null || view == null || assembly == null || settings == null)
		{
			return false;
		}
		if (view is View3D || view.IsTemplate)
		{
			return false;
		}
		try
		{
			RestrictViewToAssemblyElements(doc, assembly, view);
			RemoveViewLinearDimensions(doc, view);
			RemoveViewAutoDimensionDetailCurves(doc, view);
			RegenTracked(doc);
			EnsureEditableSpoolViewCrop(view);
			FitSpoolAssemblyDetailViewCropToContent(doc, assembly, view, includeTagExtents: false);
			ExpandViewCropForSpoolAnnotation(doc, view, assembly);
			TryEnsureSpoolViewAutoDimensionVisibility(doc, view);
			if (!TryApplySpoolAssemblyAutoDimensions(doc, view, assembly, settings, out var _))
			{
				return false;
			}
			RegenTracked(doc);
			TryEnsureSpoolViewAutoDimensionVisibility(doc, view);
			TryUnhideAllViewDimensions(doc, view);
			FitSpoolAssemblyDetailViewCropToContent(doc, assembly, view, includeTagExtents: false, includeDimensionExtents: true, includeDimensionLabelExtents: true);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void RemoveViewLinearDimensions(Document doc, View view)
	{
		if (doc == null || view == null)
		{
			return;
		}
		try
		{
			List<ElementId> list = new FilteredElementCollector(doc, ((Element)view).Id)
				.OfCategory(BuiltInCategory.OST_Dimensions)
				.WhereElementIsNotElementType()
				.ToElementIds()
				.ToList();
			if (list.Count > 0)
			{
				doc.Delete((ICollection<ElementId>)(object)list);
			}
		}
		catch
		{
		}
	}

	// Auto-dimension must never leave detail curves on spool views. Older builds used temporary span-reference
	// detail lines; clear all of them whenever dimensions are regenerated.
	private static void RemoveViewAutoDimensionDetailCurves(Document doc, View view)
	{
		if (doc == null || view == null)
		{
			return;
		}
		try
		{
			List<ElementId> detailCurves = new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(DetailCurve))
				.WhereElementIsNotElementType()
				.ToElementIds()
				.ToList();
			if (detailCurves.Count > 0)
			{
				doc.Delete((ICollection<ElementId>)(object)detailCurves);
			}
		}
		catch
		{
		}
	}

	// Mirrors TryGetExistingViewSheetSettings' view-name matching, but reports whether Auto Dim is enabled for the view.
	internal static bool IsAutoDimEnabledForExistingView(View view, SpoolingManagerSettings settings)
	{
		if (view == null || settings == null || view is View3D || view.IsTemplate)
		{
			return false;
		}
		string text = (((Element)view).Name ?? string.Empty).ToUpperInvariant();
		if (text.Contains("BACK"))
		{
			return settings.AutoDimBackView;
		}
		if (text.Contains("FRONT"))
		{
			return settings.AutoDimFrontView;
		}
		if (text.Contains("LEFT"))
		{
			return settings.AutoDimLeftView;
		}
		if (text.Contains("RIGHT"))
		{
			return settings.AutoDimRightView;
		}
		if (text.Contains("TOP"))
		{
			return settings.AutoDimTopView;
		}
		return false;
	}

	private static BoundingBoxXYZ TryCopyViewCropBox(View view)
	{
		if (view == null)
		{
			return null;
		}
		try
		{
			if (!view.CropBoxActive)
			{
				return null;
			}
			BoundingBoxXYZ cropBox = view.CropBox;
			if (cropBox == null || cropBox.Transform == null)
			{
				return null;
			}
			return new BoundingBoxXYZ
			{
				Transform = cropBox.Transform,
				Min = cropBox.Min,
				Max = cropBox.Max
			};
		}
		catch
		{
			return null;
		}
	}

	private static void TryRestoreViewCropBox(View view, BoundingBoxXYZ savedCropBox)
	{
		if (view == null || savedCropBox == null)
		{
			return;
		}
		try
		{
			view.CropBox = savedCropBox;
			view.CropBoxVisible = false;
		}
		catch
		{
		}
	}

	private static void ExpandViewCropForSpoolAnnotation(Document doc, View view, AssemblyInstance assembly)
	{
		if (doc == null || view == null || assembly == null || view is View3D)
		{
			return;
		}
		bool cropBoxActive;
		try
		{
			cropBoxActive = view.CropBoxActive;
		}
		catch
		{
			return;
		}
		if (!cropBoxActive)
		{
			return;
		}
		double num = ConvertSheetOffsetToModelDistance(view, 1.0 / 24.0);
		try
		{
			BoundingBoxXYZ cropBox = view.CropBox;
			XYZ min = cropBox.Min;
			XYZ max = cropBox.Max;
			min = new XYZ(min.X - num * 0.35, min.Y - num, min.Z);
			max = new XYZ(max.X + num * 0.35, max.Y + num, max.Z);
			cropBox.Min = min;
			cropBox.Max = max;
			view.CropBox = cropBox;
		}
		catch
		{
		}
	}

	internal static bool UsesRegularSheetBranch(SpoolingManagerSettings settings, SpoolingManagerKind kind)
	{
		return settings?.UseRegularSheetBranch ?? false;
	}

	internal static string GetToolWindowTitle(SpoolingManagerKind kind)
	{
		return kind switch
		{
			SpoolingManagerKind.Mmc => "MMC SS Manager",
			SpoolingManagerKind.MmcTesting => "MMC SS Manager (Testing)",
			SpoolingManagerKind.AutoDimensionLab => "SS Manager (Auto Dim) — Testing",
			_ => "SS Manager V3",
		};
	}

	private static bool HasExistingSpoolSheet(Document doc, AssemblyInstance assembly, bool regularBranch)
	{
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		if (regularBranch)
		{
			return FindSpoolSheet(doc, ((Element)assembly).Id, regularSheetBranch: true) != null;
		}
		return ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))).Cast<ViewSheet>().Any((ViewSheet x) => ((View)x).IsAssemblyView && ((View)x).AssociatedAssemblyInstanceId == ((Element)assembly).Id);
	}

	private static string GetFabricationPartSearchCorpus(FabricationPart part, params string[] extraParameterNames)
	{
		if (part == null)
		{
			return string.Empty;
		}
		Element element = (Element)(object)part;
		// Fast path: the vast majority of callers pass no extra parameters. Cache that result per part for
		// the duration of a run so the ~7 LookupParameter calls below aren't repeated dozens of times.
		bool cacheable = extraParameterNames == null || extraParameterNames.Length == 0;
		ElementId partId = element.Id;
		if (cacheable)
		{
			Document corpusDoc = element.Document;
			if (!ReferenceEquals(_searchCorpusCacheDoc, corpusDoc))
			{
				_searchCorpusCache.Clear();
				_searchCorpusCacheDoc = corpusDoc;
			}
			if (_searchCorpusCache.TryGetValue(partId, out string cachedCorpus))
			{
				return cachedCorpus;
			}
		}
		List<string> tokens = new List<string>();
		if (!string.IsNullOrWhiteSpace(element.Name))
		{
			tokens.Add(element.Name.Trim());
		}
		Document document = element.Document;
		ElementId typeId = element.GetTypeId();
		if (document != null && typeId != (ElementId)null && typeId != ElementId.InvalidElementId)
		{
			Element element2 = document.GetElement(typeId);
			if (element2 != null)
			{
				if (!string.IsNullOrWhiteSpace(element2.Name))
				{
					tokens.Add(element2.Name.Trim());
				}
				ElementType val = (ElementType)(object)((element2 is ElementType) ? element2 : null);
				if (val != null && !string.IsNullOrWhiteSpace(val.FamilyName))
				{
					tokens.Add(val.FamilyName.Trim());
				}
			}
		}
		tokens.Add(GetPartParameterValue(element, "Alias"));
		tokens.Add(GetPartParameterValue(element, "Product Entry"));
		tokens.Add(GetPartParameterValue(element, "Product Long Description"));
		tokens.Add(GetPartParameterValue(element, "Description"));
		tokens.Add(GetPartParameterValue(element, "CID"));
		tokens.Add(GetPartParameterValue(element, "eM_Fitting Type"));
		tokens.Add(GetPartParameterValue(element, "eM_Service Type"));
		if (extraParameterNames != null)
		{
			foreach (string text in extraParameterNames)
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					tokens.Add(GetPartParameterValue(element, text));
				}
			}
		}
		string corpus = string.Join(" ", tokens.Where((string x) => !string.IsNullOrWhiteSpace(x))).ToUpperInvariant();
		if (cacheable)
		{
			_searchCorpusCache[partId] = corpus;
		}
		return corpus;
	}

	private static bool IsWeldPart(FabricationPart part)
	{
		if (part == null)
		{
			return false;
		}
		if (IsOletPart(part))
		{
			return false;
		}
		// A "Weld Neck Flange" has "WELD" in its type name but is a flange, not a weld joint.
		// Never treat a flange as a weld, or it gets dropped from spool parts, tagging, and dimensioning.
		if (FabricationPartClassification.IsFlangePart(part, ((Element)part).Document))
		{
			return false;
		}
		if (FabricationPartClassification.IsWeldPart(part) || FabricationPartClassification.IsJointPart(part))
		{
			return true;
		}
		string text = GetFabricationPartSearchCorpus(part);
		if (!text.Contains("WELD"))
		{
			return text.Contains("JOINT");
		}
		return true;
	}

	/// <summary>Welds, gaskets, bolt kits, and hangers skip pipe/fitting Item Number — hangers are numbered separately.</summary>
	private static bool ShouldExcludeFromItemNumbering(FabricationPart part, IList<FabricationPart> partsPool = null)
	{
		if (part == null)
		{
			return true;
		}
		if (FabricationPartClassification.IsFabricationHanger(part))
		{
			return true;
		}
		if (IsGasketPart(part) || IsWeldPart(part))
		{
			return true;
		}
		if (FabricationPartClassification.IsBoltKitPart(part))
		{
			return true;
		}
		return false;
	}

	private static bool ShouldClearFabricationItemNumber(FabricationPart part)
	{
		if (part == null || FabricationPartClassification.IsFabricationHanger(part))
		{
			return false;
		}
		if (IsGasketPart(part) || IsWeldPart(part))
		{
			return true;
		}
		return FabricationPartClassification.IsBoltKitPart(part);
	}

	private static bool IsGasketPart(FabricationPart part)
	{
		if (part == null)
		{
			return false;
		}
		return GetFabricationPartSearchCorpus(part, "eM_Service Type", "Service Type").Contains("GASKET");
	}

	private static bool IsFieldWeldPart(FabricationPart part)
	{
		if (part == null || !IsWeldPart(part))
		{
			return false;
		}
		return GetFabricationPartSearchCorpus(part).Contains("FIELD");
	}

	private static bool ShouldReceiveSWeldTag(FabricationPart part)
	{
		if (part == null)
		{
			return false;
		}
		if (IsOletPart(part))
		{
			return true;
		}
		return IsWeldPart(part);
	}

	// Weld numbers/tags are optionally prefixed with the assembly's Package # (plus a dash), controlled by the
	// annotation setting. This replaces the old per-run prefix prompt so generation, refresh, and the background
	// member-sync all derive the same prefix from data on the assembly.
	internal static string ComputeAssemblySWeldPrefix(Document doc, AssemblyInstance assembly, SpoolingManagerSettings settings)
	{
		if (doc == null || assembly == null || settings == null || !settings.WeldTagIncludePackageNumber)
		{
			return string.Empty;
		}
		string package = FabricationSavantParameterSync.TryGetAssemblyPackageValue(doc, assembly);
		if (string.IsNullOrWhiteSpace(package))
		{
			return string.Empty;
		}
		return NormalizeSWeldPrefix(package.Trim());
	}

	internal static void AssignAssemblySWeldNumbers(Document doc, AssemblyInstance assembly, string prefix)
	{
		if (doc == null || assembly == null)
		{
			return;
		}
		string normalizedPrefix = NormalizeSWeldPrefix(prefix);
		string assemblyName = AssemblyDisplayName.Get(assembly)?.Trim();
		if (string.IsNullOrWhiteSpace(assemblyName))
		{
			assemblyName = "Spool";
		}
		string shopOletNumberStem = normalizedPrefix + assemblyName + "-";
		List<FabricationPart> list = (from x in assembly.GetMemberIds()
			select doc.GetElement(x)).OfType<FabricationPart>().ToList();
		List<FabricationPart> list2 = new List<FabricationPart>();
		List<FabricationPart> list3 = new List<FabricationPart>();
		foreach (FabricationPart item in list)
		{
			if (IsOletPart(item))
			{
				list2.Add(item);
			}
			else if (IsWeldPart(item))
			{
				if (IsFieldWeldPart(item))
				{
					list3.Add(item);
				}
				else
				{
					list2.Add(item);
				}
			}
		}
		SortSWeldNumberingTargets(list2);
		SortSWeldNumberingTargets(list3);
		int num = 1;
		foreach (FabricationPart item2 in list2)
		{
			SetSWeldValue(item2, shopOletNumberStem + num.ToString("D2"));
			num++;
		}
		num = 1;
		foreach (FabricationPart item3 in list3)
		{
			SetSWeldValue(item3, normalizedPrefix + "FW-" + num.ToString("D2"));
			num++;
		}
	}

	private static void SortSWeldNumberingTargets(List<FabricationPart> parts)
	{
		parts.Sort((FabricationPart a, FabricationPart b) =>
		{
			int num = GetFabricationSortPriority(a).CompareTo(GetFabricationSortPriority(b));
			if (num != 0)
			{
				return num;
			}
			return ((Element)a).Id.Value.CompareTo(((Element)b).Id.Value);
		});
	}

	private static string NormalizeSWeldPrefix(string prefix)
	{
		prefix = prefix?.Trim() ?? string.Empty;
		if (string.IsNullOrEmpty(prefix))
		{
			return prefix;
		}
		if (!prefix.EndsWith("-", StringComparison.Ordinal))
		{
			prefix += "-";
		}
		return prefix;
	}

	private static void WriteStringParameter(Element element, string parameterName, string value)
	{
		if (element == null || string.IsNullOrWhiteSpace(parameterName))
		{
			return;
		}
		Parameter val = element.LookupParameter(parameterName);
		if (val == null || ((APIObject)val).IsReadOnly)
		{
			return;
		}
		try
		{
			val.Set(value ?? string.Empty);
		}
		catch
		{
		}
	}

	private static string ReadStringParameter(Element element, string parameterName)
	{
		if (element == null || string.IsNullOrWhiteSpace(parameterName))
		{
			return string.Empty;
		}
		Parameter val = element.LookupParameter(parameterName);
		if (val == null || !val.HasValue)
		{
			return string.Empty;
		}
		string text = val.AsString();
		if (string.IsNullOrWhiteSpace(text))
		{
			text = val.AsValueString();
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}
		return string.Empty;
	}

	private static void SetSWeldValue(FabricationPart part, string value)
	{
		if (part == null)
		{
			return;
		}
		Element element = (Element)(object)part;
		WriteStringParameter(element, SsSavantSharedParameterBootstrap.SWeldParameterName, value);
	}

	internal static string GetSWeldValue(FabricationPart part)
	{
		if (part == null)
		{
			return string.Empty;
		}
		Parameter val = ((Element)part).LookupParameter(SsSavantSharedParameterBootstrap.SWeldParameterName);
		if (val == null)
		{
			return string.Empty;
		}
		string text = val.AsString();
		if (string.IsNullOrWhiteSpace(text))
		{
			text = val.AsValueString();
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}
		return string.Empty;
	}

	internal static string GetSMaterialValue(FabricationPart part)
	{
		if (part == null)
		{
			return string.Empty;
		}
		Parameter val = ((Element)part).LookupParameter(SsSavantSharedParameterBootstrap.SMaterialParameterName);
		if (val == null)
		{
			return string.Empty;
		}
		string text = val.AsString();
		if (string.IsNullOrWhiteSpace(text))
		{
			text = val.AsValueString();
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}
		return string.Empty;
	}

	internal static string GetWeldTypeLabel(FabricationPart part)
	{
		if (part == null)
		{
			return string.Empty;
		}
		if (IsOletPart(part))
		{
			return "O-Let";
		}
		if (IsFieldWeldPart(part))
		{
			return "Field Weld";
		}
		if (IsWeldPart(part))
		{
			return "Shop Weld";
		}
		return string.Empty;
	}

	private static int GetWeldLogSortGroup(FabricationPart part)
	{
		return IsFieldWeldPart(part) ? 1 : 0;
	}

	private static int CompareWeldLogParts(FabricationPart a, FabricationPart b)
	{
		int groupCompare = GetWeldLogSortGroup(a).CompareTo(GetWeldLogSortGroup(b));
		if (groupCompare != 0)
		{
			return groupCompare;
		}
		int sweldCompare = string.Compare(GetSWeldValue(a), GetSWeldValue(b), StringComparison.OrdinalIgnoreCase);
		if (sweldCompare != 0)
		{
			return sweldCompare;
		}
		return GetElementIdValue(((Element)a).Id).CompareTo(GetElementIdValue(((Element)b).Id));
	}

	private static List<FabricationPart> SortWeldLogParts(IEnumerable<FabricationPart> parts)
	{
		List<FabricationPart> list = parts?.ToList() ?? new List<FabricationPart>();
		list.Sort(CompareWeldLogParts);
		return list;
	}

	internal static List<WeldLogExportRow> CollectWeldLogRowsForAssemblies(Document doc, IEnumerable<ElementId> assemblyIds)
	{
		List<WeldLogExportRow> rows = new List<WeldLogExportRow>();
		if (doc == null || assemblyIds == null)
		{
			return rows;
		}
		foreach (ElementId assemblyId in assemblyIds)
		{
			if (assemblyId == (ElementId)null || assemblyId == ElementId.InvalidElementId)
			{
				continue;
			}
			Element element = doc.GetElement(assemblyId);
			AssemblyInstance assembly = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			if (assembly == null)
			{
				continue;
			}
			List<FabricationPart> parts = SortWeldLogParts((from x in assembly.GetMemberIds()
				select doc.GetElement(x)).OfType<FabricationPart>()
				.Where(ShouldReceiveSWeldTag)
				.Where((FabricationPart x) => !string.IsNullOrWhiteSpace(GetSWeldValue(x))));
			foreach (FabricationPart part in parts)
			{
				rows.Add(new WeldLogExportRow
				{
					WeldNumber = GetSWeldValue(part),
					Material = GetSMaterialValue(part),
					WeldType = GetWeldTypeLabel(part)
				});
			}
		}
		return rows;
	}

	private static void AppendSWeldTags(Document doc, AssemblyInstance assembly, View view, FamilySymbol tagType, string placement, SpoolingManagerKind kind, SpoolingManagerSettings settings, List<TagLayoutData> layoutTags, TagCreationResult result, ICollection<ElementId> restrictToPartIds = null)
	{
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Expected O, but got Unknown
		HashSet<ElementId> hashSet = GetExistingTaggedSWeldPartIds(doc, view, tagType);
		List<FabricationPart> list = (from x in GetTaggablePartsForView(doc, assembly, view)
			where ShouldReceiveSWeldTag(x) && !string.IsNullOrWhiteSpace(GetSWeldValue(x))
			orderby GetSWeldValue(x), ((Element)x).Id.Value
			select x).ToList();
		int num = 0;
		foreach (FabricationPart item in list)
		{
			ElementId id = ((Element)item).Id;
			if (restrictToPartIds != null && !restrictToPartIds.Contains(id))
			{
				continue;
			}
			if (hashSet.Contains(id))
			{
				continue;
			}
			try
			{
				result.PartsEvaluated++;
				Element val = (Element)(object)item;
				XYZ elementAnchorPoint = GetElementAnchorPoint(val, view);
				if (elementAnchorPoint == null)
				{
					continue;
				}
				Reference val2 = new Reference(val);
				result.ElementReferenceAttempts++;
				IndependentTag val3 = TryCreateTagWithStrategies(doc, view, tagType, val2, elementAnchorPoint, result);
				if (val3 == null && view is View3D view3D)
				{
					Reference val4 = Get3DPickedReference(view3D, val, elementAnchorPoint, (FindReferenceTarget)1);
					if (val4 != null)
					{
						result.ElementReferenceAttempts++;
						val3 = TryCreateTagWithStrategies(doc, view, tagType, val4, elementAnchorPoint, result);
						if (val3 != null)
						{
							val2 = val4;
						}
					}
				}
				if (val3 == null)
				{
					continue;
				}
				result.ElementReferenceSuccesses++;
				val3.HasLeader = true;
				XYZ tagHeadPoint = GetTagHeadPoint(val, view, placement, kind, settings, elementAnchorPoint);
				tagHeadPoint = OffsetTagHeadForSWeldTag(view, tagHeadPoint, num, IsOletPart(item));
				SetTagHeadPosition(val3, tagHeadPoint);
				layoutTags.Add(new TagLayoutData
				{
					Tag = val3,
					AnchorPoint = elementAnchorPoint,
					Reference = val2
				});
				hashSet.Add(id);
				result.CreatedCount++;
				num++;
			}
			catch
			{
				result.Exceptions++;
			}
		}
	}

	private static void AppendOletFittingTags(Document doc, AssemblyInstance assembly, View view, FamilySymbol tagType, string placement, SpoolingManagerKind kind, SpoolingManagerSettings settings, List<TagLayoutData> layoutTags, TagCreationResult result, ICollection<ElementId> restrictToPartIds = null)
	{
		HashSet<ElementId> hashSet = GetExistingTaggedOletFittingPartIds(doc, view, tagType);
		List<FabricationPart> list = (from x in GetTaggablePartsForView(doc, assembly, view)
			where IsOletPart(x) && !string.IsNullOrWhiteSpace(GetFabricationItemNumber(x))
			orderby GetFabricationItemNumber(x), ((Element)x).Id.Value
			select x).ToList();
		foreach (FabricationPart item in list)
		{
			ElementId id = ((Element)item).Id;
			if (restrictToPartIds != null && !restrictToPartIds.Contains(id))
			{
				continue;
			}
			if (hashSet.Contains(id))
			{
				continue;
			}
			try
			{
				result.PartsEvaluated++;
				Element val = (Element)(object)item;
				XYZ elementAnchorPoint = GetElementAnchorPoint(val, view);
				if (elementAnchorPoint == null)
				{
					continue;
				}
				Reference val2 = new Reference(val);
				result.ElementReferenceAttempts++;
				IndependentTag val3 = TryCreateTagWithStrategies(doc, view, tagType, val2, elementAnchorPoint, result);
				if (val3 == null && view is View3D view3D)
				{
					Reference val4 = Get3DPickedReference(view3D, val, elementAnchorPoint, (FindReferenceTarget)1);
					if (val4 != null)
					{
						result.ElementReferenceAttempts++;
						val3 = TryCreateTagWithStrategies(doc, view, tagType, val4, elementAnchorPoint, result);
						if (val3 != null)
						{
							val2 = val4;
						}
					}
				}
				if (val3 == null)
				{
					continue;
				}
				result.ElementReferenceSuccesses++;
				val3.HasLeader = true;
				SetTagHeadPosition(val3, GetTagHeadPoint(val, view, placement, kind, settings, elementAnchorPoint));
				layoutTags.Add(new TagLayoutData
				{
					Tag = val3,
					AnchorPoint = elementAnchorPoint,
					Reference = val2
				});
				hashSet.Add(id);
				result.CreatedCount++;
			}
			catch
			{
				result.Exceptions++;
			}
		}
	}

	private static bool IsOletBranchStubPipe(FabricationPart pipe, IList<FabricationPart> pool)
	{
		if (!IsPipeRunPart(pipe))
		{
			return false;
		}
		double length = GetFabricationStraightLineLength(pipe);
		if (length < 1.0 / 24.0)
		{
			return false;
		}
		foreach (Connector connector in ListConnectors(pipe))
		{
			if (((connector != null) ? connector.Origin : null) == null)
			{
				continue;
			}
			FabricationPart mate = FindMateAtConnector(pipe, connector, pool);
			if (mate != null && IsOletPart(mate))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>Second-chance tag pass for any numbered part the primary loop could not tag (branch stubs, short pipes, etc.).</summary>
	private static void AppendUntaggedBranchTakeoffPipeTags(Document doc, AssemblyInstance assembly, View view, FamilySymbol tagType, string placement, SpoolingManagerKind kind, SpoolingManagerSettings settings, List<TagLayoutData> layoutTags, TagCreationResult result, ICollection<ElementId> restrictToPartIds, HashSet<ElementId> alreadyTaggedPartIds)
	{
		if (doc == null || assembly == null || view == null || tagType == null)
		{
			return;
		}
		List<FabricationPart> pool = GetTaggablePartsForView(doc, assembly, view);
		foreach (FabricationPart pipe in pool)
		{
			ElementId partId = ((Element)pipe).Id;
			if (restrictToPartIds != null && !restrictToPartIds.Contains(partId))
			{
				continue;
			}
			if (ShouldExcludeFromItemNumbering(pipe, pool))
			{
				continue;
			}
			if (alreadyTaggedPartIds.Contains(partId) || string.IsNullOrWhiteSpace(GetFabricationItemNumber(pipe)))
			{
				continue;
			}
			try
			{
				result.PartsEvaluated++;
				Element element = (Element)(object)pipe;
				List<XYZ> anchorCandidates = GetElementAnchorCandidates(element, view);
				if (anchorCandidates.Count == 0)
				{
					continue;
				}
				IndependentTag tag = null;
				Reference reference = null;
				XYZ anchor = null;
				foreach (XYZ candidate in anchorCandidates)
				{
					if (view is View3D view3D)
					{
						Reference picked = Get3DPickedReference(view3D, element, candidate, (FindReferenceTarget)1);
						if (picked != null)
						{
							result.ElementReferenceAttempts++;
							tag = TryCreateTagWithStrategies(doc, view, tagType, picked, candidate, result);
							if (tag != null)
							{
								result.ElementReferenceSuccesses++;
								reference = picked;
								anchor = candidate;
								break;
							}
						}
					}
					Reference elementRef = new Reference(element);
					result.ElementReferenceAttempts++;
					tag = TryCreateTagWithStrategies(doc, view, tagType, elementRef, candidate, result);
					if (tag != null)
					{
						result.ElementReferenceSuccesses++;
						reference = elementRef;
						anchor = candidate;
						break;
					}
				}
				if (tag == null || anchor == null)
				{
					continue;
				}
				tag.HasLeader = true;
				SetTagHeadPosition(tag, GetTagHeadPoint(element, view, placement, kind, settings, anchor));
				layoutTags.Add(new TagLayoutData
				{
					Tag = tag,
					AnchorPoint = anchor,
					Reference = reference
				});
				alreadyTaggedPartIds.Add(partId);
				result.CreatedCount++;
			}
			catch
			{
				result.Exceptions++;
			}
		}
	}

	private static void AppendFlangeTags(Document doc, AssemblyInstance assembly, View view, FamilySymbol tagType, string placement, SpoolingManagerKind kind, SpoolingManagerSettings settings, List<TagLayoutData> layoutTags, TagCreationResult result)
	{
		HashSet<ElementId> hashSet = GetExistingTaggedFlangePartIds(doc, view, tagType);
		foreach (TagLayoutData layoutTag in layoutTags)
		{
			if (layoutTag?.Tag == null)
			{
				continue;
			}
			try
			{
				ISet<ElementId> taggedIds = layoutTag.Tag.GetTaggedLocalElementIds();
				if (taggedIds == null)
				{
					continue;
				}
				foreach (ElementId taggedId in taggedIds)
				{
					hashSet.Add(taggedId);
				}
			}
			catch
			{
			}
		}
		List<FabricationPart> list = (from x in GetTaggablePartsForView(doc, assembly, view)
			where FabricationPartClassification.IsFlangePart(x, doc) && !string.IsNullOrWhiteSpace(GetFabricationItemNumber(x))
			orderby GetFabricationItemNumber(x), ((Element)x).Id.Value
			select x).ToList();
		foreach (FabricationPart item in list)
		{
			ElementId id = ((Element)item).Id;
			if (hashSet.Contains(id))
			{
				continue;
			}
			try
			{
				result.PartsEvaluated++;
				Element val = (Element)(object)item;
				XYZ elementAnchorPoint = GetElementAnchorPoint(val, view);
				if (elementAnchorPoint == null)
				{
					continue;
				}
				Reference val2 = new Reference(val);
				result.ElementReferenceAttempts++;
				IndependentTag val3 = TryCreateTagWithStrategies(doc, view, tagType, val2, elementAnchorPoint, result);
				if (val3 == null && view is View3D view3D)
				{
					Reference val4 = Get3DPickedReference(view3D, val, elementAnchorPoint, (FindReferenceTarget)1);
					if (val4 != null)
					{
						result.ElementReferenceAttempts++;
						val3 = TryCreateTagWithStrategies(doc, view, tagType, val4, elementAnchorPoint, result);
						if (val3 != null)
						{
							val2 = val4;
						}
					}
				}
				if (val3 == null)
				{
					continue;
				}
				result.ElementReferenceSuccesses++;
				val3.HasLeader = true;
				SetTagHeadPosition(val3, GetTagHeadPoint(val, view, placement, kind, settings, elementAnchorPoint));
				layoutTags.Add(new TagLayoutData
				{
					Tag = val3,
					AnchorPoint = elementAnchorPoint,
					Reference = val2
				});
				hashSet.Add(id);
				result.CreatedCount++;
			}
			catch
			{
				result.Exceptions++;
			}
		}
	}

	internal static HashSet<ElementId> GetExistingTaggedFabricationPartIds(Document doc, View view, FamilySymbol tagType)
	{
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		if (doc == null || view == null)
		{
			return hashSet;
		}
		IEnumerable<IndependentTag> enumerable;
		try
		{
			enumerable = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(IndependentTag))).Cast<IndependentTag>().ToList();
		}
		catch
		{
			return hashSet;
		}
		foreach (IndependentTag item in enumerable)
		{
			if (tagType != null && item.GetTypeId() != ((Element)tagType).Id)
			{
				continue;
			}
			ISet<ElementId> taggedLocalElementIds;
			try
			{
				taggedLocalElementIds = item.GetTaggedLocalElementIds();
			}
			catch
			{
				continue;
			}
			if (taggedLocalElementIds == null)
			{
				continue;
			}
			foreach (ElementId item2 in taggedLocalElementIds)
			{
				Element element = doc.GetElement(item2);
				if (element is FabricationPart)
				{
					hashSet.Add(item2);
				}
			}
		}
		return hashSet;
	}

	internal static HashSet<ElementId> GetExistingTaggedFlangePartIds(Document doc, View view, FamilySymbol fittingTagType)
	{
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		if (doc == null || view == null)
		{
			return hashSet;
		}
		IEnumerable<IndependentTag> enumerable;
		try
		{
			enumerable = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(IndependentTag))).Cast<IndependentTag>().ToList();
		}
		catch
		{
			return hashSet;
		}
		foreach (IndependentTag item in enumerable)
		{
			if (fittingTagType != null && item.GetTypeId() != ((Element)fittingTagType).Id)
			{
				continue;
			}
			ISet<ElementId> taggedLocalElementIds;
			try
			{
				taggedLocalElementIds = item.GetTaggedLocalElementIds();
			}
			catch
			{
				continue;
			}
			if (taggedLocalElementIds == null)
			{
				continue;
			}
			foreach (ElementId item2 in taggedLocalElementIds)
			{
				Element element = doc.GetElement(item2);
				if (element is FabricationPart fabricationPart && FabricationPartClassification.IsFlangePart(fabricationPart, doc))
				{
					hashSet.Add(item2);
				}
			}
		}
		return hashSet;
	}

	internal static HashSet<ElementId> GetExistingTaggedOletFittingPartIds(Document doc, View view, FamilySymbol fittingTagType)
	{
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		if (doc == null || view == null)
		{
			return hashSet;
		}
		IEnumerable<IndependentTag> enumerable;
		try
		{
			enumerable = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(IndependentTag))).Cast<IndependentTag>().ToList();
		}
		catch
		{
			return hashSet;
		}
		foreach (IndependentTag item in enumerable)
		{
			if (fittingTagType != null && item.GetTypeId() != ((Element)fittingTagType).Id)
			{
				continue;
			}
			ISet<ElementId> taggedLocalElementIds;
			try
			{
				taggedLocalElementIds = item.GetTaggedLocalElementIds();
			}
			catch
			{
				continue;
			}
			if (taggedLocalElementIds == null)
			{
				continue;
			}
			foreach (ElementId item2 in taggedLocalElementIds)
			{
				Element element = doc.GetElement(item2);
				if (element is FabricationPart part && IsOletPart(part))
				{
					hashSet.Add(item2);
				}
			}
		}
		return hashSet;
	}

	internal static HashSet<ElementId> GetExistingTaggedSWeldPartIds(Document doc, View view, FamilySymbol weldTagType)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		if (doc == null || view == null)
		{
			return hashSet;
		}
		IEnumerable<IndependentTag> enumerable;
		try
		{
			enumerable = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(IndependentTag))).Cast<IndependentTag>().ToList();
		}
		catch
		{
			return hashSet;
		}
		foreach (IndependentTag item in enumerable)
		{
			if (weldTagType != null && item.GetTypeId() != ((Element)weldTagType).Id)
			{
				continue;
			}
			ISet<ElementId> taggedLocalElementIds;
			try
			{
				taggedLocalElementIds = item.GetTaggedLocalElementIds();
			}
			catch
			{
				continue;
			}
			if (taggedLocalElementIds == null)
			{
				continue;
			}
			foreach (ElementId item2 in taggedLocalElementIds)
			{
				Element element = doc.GetElement(item2);
				if (element is FabricationPart part && ShouldReceiveSWeldTag(part) && !string.IsNullOrWhiteSpace(GetSWeldValue(part)))
				{
					hashSet.Add(item2);
				}
			}
		}
		return hashSet;
	}

	internal static void AssignAssemblyContinuationValues(Autodesk.Revit.ApplicationServices.Application app, Document doc, AssemblyInstance assembly)
	{
		if (doc == null || assembly == null)
		{
			return;
		}
		FabricationSavantParameterSync.EnsureContinuationParameterForAssembly(app, doc, assembly);
		ClearAssemblyContinuationValues(doc, assembly);
		foreach (AssemblyContinuationTarget item in GetAssemblyContinuationTargets(doc, assembly))
		{
			if (item?.TagMember == null || string.IsNullOrWhiteSpace(item.ContinuationValue))
			{
				continue;
			}
			SetSContinuationValue(item.TagMember, item.ContinuationValue);
		}
	}

	private static void ClearAssemblyContinuationValues(Document doc, AssemblyInstance assembly)
	{
		if (doc == null || assembly == null)
		{
			return;
		}
		foreach (ElementId memberId in assembly.GetMemberIds())
		{
			Element element = doc.GetElement(memberId);
			if (element is FabricationPart)
			{
				SetSContinuationValue(element, string.Empty);
			}
		}
	}

	internal static string FormatContinuationValue(AssemblyInstance connectedAssembly)
	{
		string text = AssemblyDisplayName.Get(connectedAssembly)?.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}
		return text;
	}

	private static string NormalizeContinuationValue(string value)
	{
		string text = value?.Trim() ?? string.Empty;
		const string prefix = "Continued on ";
		if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(prefix.Length).Trim();
		}
		return text;
	}

	private static string GetSContinuationValue(Element element)
	{
		if (element == null)
		{
			return string.Empty;
		}
		string text = ReadStringParameter(element, SsSavantSharedParameterBootstrap.SContinuationParameterName);
		return NormalizeContinuationValue(text);
	}

	private static bool HasContinuationValue(Element element)
	{
		return !string.IsNullOrWhiteSpace(GetSContinuationValue(element));
	}

	private static bool IsContinuationTagSymbol(Document doc, ElementId tagTypeId)
	{
		if (doc == null || tagTypeId == (ElementId)null || tagTypeId == ElementId.InvalidElementId)
		{
			return false;
		}
		Element element = doc.GetElement(tagTypeId);
		FamilySymbol val = (FamilySymbol)(object)((element is FamilySymbol) ? element : null);
		if (val == null)
		{
			return false;
		}
		string familyName = ((ElementType)val).FamilyName ?? string.Empty;
		string typeName = ((Element)val).Name ?? string.Empty;
		return familyName.IndexOf("Continuation", StringComparison.OrdinalIgnoreCase) >= 0
			|| typeName.IndexOf("Continuation", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool IsContinuationRelatedTag(
		Document doc,
		IndependentTag tag,
		ISet<ElementId> assemblyMemberIds)
	{
		if (doc == null || tag == null)
		{
			return false;
		}
		if (IsContinuationTagSymbol(doc, tag.GetTypeId()))
		{
			return true;
		}
		string tagText = GetIndependentTagText(tag);
		if (!string.IsNullOrWhiteSpace(tagText)
			&& tagText.IndexOf("Continued on", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		ISet<ElementId> taggedLocalElementIds;
		try
		{
			taggedLocalElementIds = tag.GetTaggedLocalElementIds();
		}
		catch
		{
			return false;
		}
		if (taggedLocalElementIds == null)
		{
			return false;
		}
		foreach (ElementId taggedId in taggedLocalElementIds)
		{
			if (assemblyMemberIds != null && !assemblyMemberIds.Contains(taggedId))
			{
				continue;
			}
			Element taggedElement = doc.GetElement(taggedId);
			if (taggedElement is FabricationPart && HasContinuationValue(taggedElement))
			{
				return true;
			}
		}
		return false;
	}

	private static string BuildContinuationPlacementKey(ElementId tagMemberId, string continuationValue)
	{
		string text = NormalizeContinuationValue(continuationValue);
		return GetElementIdValue(tagMemberId) + "|" + text;
	}

	private static string BuildContinuationPlacementKey(AssemblyInstance connectedAssembly, string continuationValue)
	{
		string text = NormalizeContinuationValue(continuationValue);
		if (connectedAssembly == null)
		{
			return text;
		}
		return GetElementIdValue(((Element)connectedAssembly).Id) + "|" + text;
	}

	private static int GetContinuationTagMemberPreferenceScore(Element element)
	{
		FabricationPart val = (FabricationPart)(object)((element is FabricationPart) ? element : null);
		if (val == null)
		{
			return 0;
		}
		if (IsWeldPart(val))
		{
			return 3;
		}
		if (IsGasketPart(val))
		{
			return 1;
		}
		if (FabricationPartClassification.IsStraightPipeRun(val))
		{
			return 2;
		}
		return 1;
	}

	private static bool IsBetterContinuationTagMember(Element candidate, Element current)
	{
		if (candidate == null)
		{
			return false;
		}
		if (current == null)
		{
			return true;
		}
		int scoreCompare = GetContinuationTagMemberPreferenceScore(candidate).CompareTo(GetContinuationTagMemberPreferenceScore(current));
		if (scoreCompare != 0)
		{
			return scoreCompare > 0;
		}
		return GetElementIdValue(candidate.Id).CompareTo(GetElementIdValue(current.Id)) < 0;
	}

	private static void SetSContinuationValue(Element element, string value)
	{
		if (element == null)
		{
			return;
		}
		FabricationSavantParameterSync.SetSavantTextParameter(
			element,
			SsSavantSharedParameterBootstrap.SContinuationParameterName,
			value ?? string.Empty);
	}

	private static void SyncContinuationValueToTaggedElements(
		Document doc,
		IndependentTag tag,
		string continuationValue)
	{
		if (doc == null || tag == null || string.IsNullOrWhiteSpace(continuationValue))
		{
			return;
		}
		ISet<ElementId> taggedLocalElementIds;
		try
		{
			taggedLocalElementIds = tag.GetTaggedLocalElementIds();
		}
		catch
		{
			return;
		}
		if (taggedLocalElementIds == null)
		{
			return;
		}
		foreach (ElementId taggedId in taggedLocalElementIds)
		{
			Element taggedElement = doc.GetElement(taggedId);
			if (taggedElement is FabricationPart)
			{
				SetSContinuationValue(taggedElement, continuationValue);
			}
		}
	}

	private static bool TryApplyTagType(IndependentTag tag, FamilySymbol tagType)
	{
		if (tag == null || tagType == null)
		{
			return false;
		}
		if (tag.GetTypeId() == ((Element)tagType).Id)
		{
			return true;
		}
		try
		{
			((Element)tag).ChangeTypeId(((Element)tagType).Id);
		}
		catch
		{
		}
		return tag.GetTypeId() == ((Element)tagType).Id;
	}

	private static void DeleteTagIfWrongType(Document doc, IndependentTag tag, FamilySymbol tagType)
	{
		if (doc == null || tag == null || tagType == null)
		{
		 return;
		}
		if (tag.GetTypeId() == ((Element)tagType).Id)
		{
			return;
		}
		try
		{
			doc.Delete(((Element)tag).Id);
		}
		catch
		{
		}
	}

	private static IndependentTag TryCreateContinuationTag(
		Document doc,
		View view,
		FamilySymbol continuationTagType,
		Reference reference,
		XYZ point,
		TagCreationResult result)
	{
		if (doc == null || view == null || continuationTagType == null || reference == null || point == null)
		{
			return null;
		}
		try
		{
			IndependentTag tag = IndependentTag.Create(
				doc,
				((Element)continuationTagType).Id,
				((Element)view).Id,
				reference,
				true,
				(TagOrientation)0,
				point);
			if (tag != null && TryApplyTagType(tag, continuationTagType))
			{
				if (result != null)
				{
					result.TypedCreateSuccesses++;
				}
				return tag;
			}
			DeleteTagIfWrongType(doc, tag, continuationTagType);
		}
		catch
		{
		}
		return null;
	}

	private static List<AssemblyContinuationTarget> GetAssemblyContinuationTargets(Document doc, AssemblyInstance assembly)
	{
		List<AssemblyContinuationTarget> list = new List<AssemblyContinuationTarget>();
		if (doc == null || assembly == null)
		{
			return list;
		}
		ElementId selfId = ((Element)assembly).Id;
		Dictionary<string, AssemblyContinuationTarget> dictionary = new Dictionary<string, AssemblyContinuationTarget>(StringComparer.OrdinalIgnoreCase);
		foreach (ElementId memberId in assembly.GetMemberIds())
		{
			Element member = doc.GetElement(memberId);
			if (member == null)
			{
				continue;
			}
			foreach (Connector connector in ListConnectorsForElement(member))
			{
				if (((connector != null) ? connector.Origin : null) == null)
				{
					continue;
				}
				AssemblyInstance val = TryResolveConnectedAssemblyAcrossBoundary(doc, selfId, member, connector);
				if (val == null)
				{
					continue;
				}
				Element continuationTagMember = GetContinuationTagMember(doc, selfId, member, connector);
				if (continuationTagMember == null)
				{
					continue;
				}
				XYZ connectionPoint = GetContinuationConnectionPoint(continuationTagMember, connector);
				if (connectionPoint == null)
				{
					continue;
				}
				string continuationValue = FormatContinuationValue(val);
				if (string.IsNullOrWhiteSpace(continuationValue))
				{
					continue;
				}
				string key = BuildContinuationPlacementKey(val, continuationValue);
				if (dictionary.TryGetValue(key, out AssemblyContinuationTarget existing))
				{
					if (IsBetterContinuationTagMember(continuationTagMember, existing.TagMember))
					{
						dictionary[key] = new AssemblyContinuationTarget
						{
							ConnectedAssembly = val,
							TagMember = continuationTagMember,
							ConnectionPoint = connectionPoint,
							ContinuationValue = continuationValue
						};
					}
					continue;
				}
				dictionary[key] = new AssemblyContinuationTarget
				{
					ConnectedAssembly = val,
					TagMember = continuationTagMember,
					ConnectionPoint = connectionPoint,
					ContinuationValue = continuationValue
				};
			}
		}
		list.AddRange(dictionary.Values);
		return list;
	}

	private static Element GetContinuationTagMember(Document doc, ElementId selfAssemblyId, Element sourceMember, Connector sourceConnector)
	{
		if (sourceMember == null || sourceConnector == null)
		{
			return null;
		}
		ConnectorSet val = null;
		try
		{
			val = sourceConnector.AllRefs;
		}
		catch
		{
		}
		if (val != null)
		{
			foreach (Connector allRef in val)
			{
				Connector val2 = allRef;
				Element val3 = ((val2 != null) ? val2.Owner : null);
				if (val3 != null && val3.Id != sourceMember.Id && IsAssemblyConnectionPassThrough(val3, selfAssemblyId))
				{
					return val3;
				}
			}
		}
		if (sourceMember is FabricationPart)
		{
			return sourceMember;
		}
		return null;
	}

	private static XYZ GetContinuationConnectionPoint(Element tagMember, Connector sourceConnector)
	{
		if (tagMember == null)
		{
			return null;
		}
		Element val = ((sourceConnector != null) ? sourceConnector.Owner : null);
		if (val != null && val.Id == tagMember.Id && sourceConnector.Origin != null)
		{
			return sourceConnector.Origin;
		}
		foreach (Connector item in ListConnectorsForElement(tagMember))
		{
			if (((item != null) ? item.Origin : null) != null)
			{
				return item.Origin;
			}
		}
		return GetElementAnchorPoint(tagMember, null);
	}

	private static AssemblyInstance TryResolveConnectedAssemblyAcrossBoundary(Document doc, ElementId selfAssemblyId, Element sourceMember, Connector sourceConnector)
	{
		if (doc == null || selfAssemblyId == (ElementId)null || sourceMember == null || sourceConnector == null)
		{
			return null;
		}
		ConnectorSet val = null;
		try
		{
			val = sourceConnector.AllRefs;
		}
		catch
		{
		}
		if (val == null)
		{
			return null;
		}
		Queue<Element> queue = new Queue<Element>();
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		hashSet.Add(sourceMember.Id);
		foreach (Connector allRef in val)
		{
			Connector val2 = allRef;
			Element val3 = ((val2 != null) ? val2.Owner : null);
			if (val3 != null && val3.Id != sourceMember.Id)
			{
				queue.Enqueue(val3);
			}
		}
		while (queue.Count > 0)
		{
			Element val4 = queue.Dequeue();
			if (val4 == null || !hashSet.Add(val4.Id))
			{
				continue;
			}
			AssemblyInstance assemblyInstanceFromElement = GetAssemblyInstanceFromElement(doc, val4, selfAssemblyId);
			if (assemblyInstanceFromElement != null)
			{
				return assemblyInstanceFromElement;
			}
			if (!IsAssemblyConnectionPassThrough(val4, selfAssemblyId))
			{
				continue;
			}
			foreach (Connector item in ListConnectorsForElement(val4))
			{
				ConnectorSet val5 = null;
				try
				{
					val5 = item?.AllRefs;
				}
				catch
				{
				}
				if (val5 == null)
				{
					continue;
				}
				foreach (Connector allRef2 in val5)
				{
					Connector val6 = allRef2;
					Element val7 = ((val6 != null) ? val6.Owner : null);
					if (val7 != null && val7.Id != val4.Id && !hashSet.Contains(val7.Id))
					{
						queue.Enqueue(val7);
					}
				}
			}
		}
		return null;
	}

	private static AssemblyInstance GetAssemblyInstanceFromElement(Document doc, Element element, ElementId selfAssemblyId)
	{
		if (doc == null || element == null)
		{
			return null;
		}
		if (element is AssemblyInstance val && ((Element)val).Id != selfAssemblyId)
		{
			return val;
		}
		ElementId assemblyInstanceId = element.AssemblyInstanceId;
		if (assemblyInstanceId == (ElementId)null || assemblyInstanceId == ElementId.InvalidElementId || assemblyInstanceId == selfAssemblyId)
		{
			return null;
		}
		Element element2 = doc.GetElement(assemblyInstanceId);
		return (AssemblyInstance)(object)((element2 is AssemblyInstance) ? element2 : null);
	}

	private static bool IsAssemblyConnectionPassThrough(Element element, ElementId selfAssemblyId)
	{
		FabricationPart val = (FabricationPart)(object)((element is FabricationPart) ? element : null);
		if (val == null)
		{
			return false;
		}
		if (!IsWeldPart(val) && !IsGasketPart(val))
		{
			return false;
		}
		ElementId assemblyInstanceId = ((Element)val).AssemblyInstanceId;
		if (assemblyInstanceId == (ElementId)null || assemblyInstanceId == ElementId.InvalidElementId)
		{
			return true;
		}
		return assemblyInstanceId == selfAssemblyId;
	}

	private static long GetElementIdValue(ElementId id)
	{
		if (id == (ElementId)null || id == ElementId.InvalidElementId)
		{
			return -1L;
		}
		return id.Value;
	}

	private static void AppendContinuationAssemblyTags(Document doc, AssemblyInstance assembly, View view, FamilySymbol continuationTagType, string placement, SpoolingManagerKind kind, SpoolingManagerSettings settings, List<TagLayoutData> layoutTags, TagCreationResult result)
	{
		if (continuationTagType == null)
		{
			return;
		}
		RemoveExistingContinuationTags(doc, view, continuationTagType, assembly);
		try
		{
			RegenTracked(doc);
		}
		catch
		{
		}
		HashSet<string> hashSet = GetExistingContinuationTaggedTargetKeys(doc, view, continuationTagType);
		HashSet<string> placedLocationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AssemblyContinuationTarget assemblyContinuationTarget in GetAssemblyContinuationTargets(doc, assembly))
		{
			Element tagMember = assemblyContinuationTarget.TagMember;
			XYZ connectionPoint = assemblyContinuationTarget.ConnectionPoint;
			if (tagMember == null || connectionPoint == null || string.IsNullOrWhiteSpace(assemblyContinuationTarget.ContinuationValue))
			{
				continue;
			}
			SetSContinuationValue(tagMember, assemblyContinuationTarget.ContinuationValue);
			string normalizedValue = NormalizeContinuationValue(assemblyContinuationTarget.ContinuationValue);
			string continuationTargetKey = BuildContinuationPlacementKey(assemblyContinuationTarget.ConnectedAssembly, assemblyContinuationTarget.ContinuationValue);
			string memberTargetKey = BuildContinuationPlacementKey(tagMember.Id, assemblyContinuationTarget.ContinuationValue);
			string locationKey = BuildContinuationLocationKey(connectionPoint, normalizedValue);
			if (hashSet.Contains(continuationTargetKey)
				|| hashSet.Contains(memberTargetKey)
				|| hashSet.Contains(normalizedValue)
				|| placedLocationKeys.Contains(locationKey))
			{
				continue;
			}
			try
			{
				result.PartsEvaluated++;
				Reference val = new Reference(tagMember);
				result.ElementReferenceAttempts++;
				IndependentTag val2 = TryCreateContinuationTag(doc, view, continuationTagType, val, connectionPoint, result);
				if (val2 == null && view is View3D view3D)
				{
					Reference val3 = Get3DPickedReference(view3D, tagMember, connectionPoint, (FindReferenceTarget)1);
					if (val3 != null && val3.ElementId == tagMember.Id)
					{
						result.ElementReferenceAttempts++;
						val2 = TryCreateContinuationTag(doc, view, continuationTagType, val3, connectionPoint, result);
						if (val2 != null)
						{
							val = val3;
						}
					}
				}
				if (val2 == null)
				{
					continue;
				}
				SyncContinuationValueToTaggedElements(doc, val2, assemblyContinuationTarget.ContinuationValue);
				try
				{
					RegenTracked(doc);
				}
				catch
				{
				}
				result.ElementReferenceSuccesses++;
				val2.HasLeader = true;
				XYZ tagHeadPoint = GetTagHeadPoint(tagMember, view, placement, kind, settings, connectionPoint) ?? GetContinuationTagHeadPoint(view, connectionPoint);
				SetTagHeadPosition(val2, tagHeadPoint);
				StraightenIndependentTagLeader(val2, val, connectionPoint, tagHeadPoint);
				layoutTags.Add(new TagLayoutData
				{
					Tag = val2,
					AnchorPoint = connectionPoint,
					Reference = val
				});
				hashSet.Add(continuationTargetKey);
				hashSet.Add(memberTargetKey);
				hashSet.Add(normalizedValue);
				placedLocationKeys.Add(locationKey);
				result.CreatedCount++;
			}
			catch
			{
				result.Exceptions++;
			}
		}
	}

	private static XYZ GetContinuationTagHeadPoint(View view, XYZ connectionPoint)
	{
		if (view == null || connectionPoint == null)
		{
			return connectionPoint;
		}
		try
		{
			XYZ val = view.UpDirection?.Normalize() ?? XYZ.BasisZ;
			XYZ val2 = view.RightDirection?.Normalize() ?? XYZ.BasisX;
			double num = 0.45 / 12.0;
			return connectionPoint + val * num + val2 * num;
		}
		catch
		{
			return connectionPoint;
		}
	}

	internal static HashSet<string> GetExistingContinuationTaggedTargetKeys(Document doc, View view, FamilySymbol continuationTagType)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (doc == null || view == null)
		{
			return hashSet;
		}
		IEnumerable<IndependentTag> enumerable;
		try
		{
			enumerable = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(IndependentTag))).Cast<IndependentTag>().ToList();
		}
		catch
		{
		 return hashSet;
		}
		foreach (IndependentTag item in enumerable)
		{
			if (continuationTagType != null && item.GetTypeId() != ((Element)continuationTagType).Id)
			{
				continue;
			}
			ISet<ElementId> taggedLocalElementIds;
			try
			{
				taggedLocalElementIds = item.GetTaggedLocalElementIds();
			}
			catch
			{
				continue;
			}
			if (taggedLocalElementIds == null)
			{
				continue;
			}
			foreach (ElementId item2 in taggedLocalElementIds)
			{
				Element element = doc.GetElement(item2);
				if (element is FabricationPart)
				{
					string continuationValue = GetSContinuationValue(element);
					if (!string.IsNullOrWhiteSpace(continuationValue))
					{
						string normalized = NormalizeContinuationValue(continuationValue);
						hashSet.Add(normalized);
						hashSet.Add(BuildContinuationPlacementKey(item2, continuationValue));
					}
					else
					{
						hashSet.Add(GetElementIdValue(item2).ToString());
					}
				}
			}
		}
		return hashSet;
	}

	private static string BuildContinuationLocationKey(XYZ connectionPoint, string normalizedValue)
	{
		if (connectionPoint == null)
		{
			return normalizedValue ?? string.Empty;
		}
		return string.Format(
			System.Globalization.CultureInfo.InvariantCulture,
			"{0:0.###}|{1:0.###}|{2:0.###}|{3}",
			connectionPoint.X,
			connectionPoint.Y,
			connectionPoint.Z,
			normalizedValue ?? string.Empty);
	}

	private static void RemoveExistingContinuationTags(
		Document doc,
		View view,
		FamilySymbol continuationTagType,
		AssemblyInstance assembly)
	{
		if (doc == null || view == null)
		{
			return;
		}
		List<ElementId> tagsToDelete = new List<ElementId>();
		IEnumerable<IndependentTag> tags;
		try
		{
			tags = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(IndependentTag)))
				.Cast<IndependentTag>()
				.ToList();
		}
		catch
		{
			return;
		}
		foreach (IndependentTag tag in tags)
		{
			if (tag == null || !IsContinuationTagSymbol(doc, tag.GetTypeId()))
			{
				continue;
			}
			tagsToDelete.Add(((Element)tag).Id);
		}
		if (tagsToDelete.Count == 0)
		{
			return;
		}
		try
		{
			doc.Delete(tagsToDelete);
		}
		catch
		{
		}
	}

	private static XYZ OffsetTagHeadForSWeldTag(View view, XYZ headPoint, int weldTagIndex, bool isOletSecondaryTag)
	{
		if (view == null || headPoint == null)
		{
			return headPoint;
		}
		try
		{
			XYZ val = view.UpDirection?.Normalize() ?? XYZ.BasisZ;
			XYZ val2 = view.RightDirection?.Normalize() ?? XYZ.BasisX;
			// Keep the stacking nudge a constant PAPER distance (pinned to the scale-12 look)
			// so weld tags stay equally spread on the sheet at any generation scale.
			double scaleRatio = Math.Max((view.Scale > 0) ? view.Scale : 1, 1) / 12.0;
			double num = (0.35 / 12.0) * scaleRatio;
			if (isOletSecondaryTag)
			{
				num += (0.2 / 12.0) * scaleRatio;
			}
			double num2 = (double)weldTagIndex * (0.22 / 12.0) * scaleRatio;
			return headPoint + val * (num + num2) + val2 * (num + num2);
		}
		catch
		{
			return headPoint;
		}
	}

	private static void DeleteExistingAssemblyViews(Document doc, AssemblyInstance assembly)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		List<ElementId> first = (from View x in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))
			where x.IsAssemblyView && x.AssociatedAssemblyInstanceId == ((Element)assembly).Id
			select ((Element)x).Id).ToList();
		List<ElementId> second = (from ViewSheet x in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
			where ((View)x).IsAssemblyView && ((View)x).AssociatedAssemblyInstanceId == ((Element)assembly).Id
			select ((Element)x).Id).ToList();
		List<ElementId> list = first.Concat(second).Distinct().ToList();
		if (list.Count > 0)
		{
			doc.Delete((ICollection<ElementId>)list);
		}
	}

	private static void DeleteExistingSpoolViewsAndSheet(Document doc, AssemblyInstance assembly, bool regularBranch)
	{
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		if (!regularBranch)
		{
			DeleteExistingAssemblyViews(doc, assembly);
			return;
		}
		List<ElementId> list = (from View x in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))
			where x.IsAssemblyView && x.AssociatedAssemblyInstanceId == ((Element)assembly).Id
			select ((Element)x).Id).ToList();
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		foreach (Viewport item in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(Viewport))).Cast<Viewport>())
		{
			if (list.Contains(item.ViewId))
			{
				Element element = doc.GetElement(item.SheetId);
				ViewSheet val = (ViewSheet)(object)((element is ViewSheet) ? element : null);
				if (val != null && !((View)val).IsAssemblyView)
				{
					hashSet.Add(((Element)val).Id);
				}
			}
		}
		if (hashSet.Count > 0)
		{
			doc.Delete((ICollection<ElementId>)hashSet.ToList());
		}
		if (list.Count > 0)
		{
			doc.Delete((ICollection<ElementId>)list);
		}
	}

	/// <summary>
	/// Revit cannot delete a sheet/view that is open. Leave those tabs (and switch ActiveView)
	/// before the create-sheets transaction regenerates existing spool sheets.
	/// </summary>
	private static void LeaveOpenViewsBlockingSpoolSheetRegenerate(
		UIDocument uidoc,
		Document doc,
		IList<ElementId> assemblyIds,
		bool regularBranch)
	{
		if (uidoc == null || doc == null || assemblyIds == null || assemblyIds.Count == 0)
		{
			return;
		}

		HashSet<ElementId> doomed = CollectSpoolSheetAndViewIdsForAssemblies(doc, assemblyIds, regularBranch);
		if (doomed.Count == 0)
		{
			return;
		}

		CloseOpenUiViews(uidoc, doomed);
		EnsureActiveViewNotAmong(uidoc, doc, doomed);
	}

	private static HashSet<ElementId> CollectSpoolSheetAndViewIdsForAssemblies(
		Document doc,
		IList<ElementId> assemblyIds,
		bool regularBranch)
	{
		HashSet<ElementId> ids = new HashSet<ElementId>();
		if (doc == null || assemblyIds == null)
		{
			return ids;
		}

		HashSet<ElementId> assemblySet = new HashSet<ElementId>(assemblyIds.Where(id => id != null && id != ElementId.InvalidElementId));
		if (assemblySet.Count == 0)
		{
			return ids;
		}

		List<ElementId> assemblyViews = (from View x in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))
			where x != null && x.IsAssemblyView && assemblySet.Contains(x.AssociatedAssemblyInstanceId)
			select ((Element)x).Id).ToList();
		foreach (ElementId viewId in assemblyViews)
		{
			ids.Add(viewId);
		}

		if (!regularBranch)
		{
			foreach (ViewSheet sheet in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))).Cast<ViewSheet>())
			{
				if (sheet != null
					&& ((View)sheet).IsAssemblyView
					&& assemblySet.Contains(((View)sheet).AssociatedAssemblyInstanceId))
				{
					ids.Add(((Element)sheet).Id);
				}
			}
			return ids;
		}

		HashSet<ElementId> assemblyViewSet = new HashSet<ElementId>(assemblyViews);
		foreach (Viewport viewport in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(Viewport))).Cast<Viewport>())
		{
			if (viewport == null || !assemblyViewSet.Contains(viewport.ViewId))
			{
				continue;
			}

			Element element = doc.GetElement(viewport.SheetId);
			ViewSheet sheet = element as ViewSheet;
			if (sheet != null && !((View)sheet).IsAssemblyView)
			{
				ids.Add(((Element)sheet).Id);
			}
		}

		return ids;
	}

	private static void CloseOpenUiViews(UIDocument uidoc, HashSet<ElementId> viewIds)
	{
		if (uidoc == null || viewIds == null || viewIds.Count == 0)
		{
			return;
		}

		try
		{
			List<UIView> open = uidoc.GetOpenUIViews()?.ToList() ?? new List<UIView>();
			foreach (UIView uiView in open)
			{
				if (uiView == null || !viewIds.Contains(uiView.ViewId))
				{
					continue;
				}

				try
				{
					uiView.Close();
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private static void EnsureActiveViewNotAmong(UIDocument uidoc, Document doc, HashSet<ElementId> viewIds)
	{
		if (uidoc == null || doc == null || viewIds == null || viewIds.Count == 0)
		{
			return;
		}

		View active;
		try
		{
			active = uidoc.ActiveView;
		}
		catch
		{
			return;
		}

		if (active == null || !viewIds.Contains(((Element)active).Id))
		{
			return;
		}

		View safe = FindSafeViewToActivate(doc, viewIds);
		if (safe != null)
		{
			TryEnsureActiveDocumentView(uidoc, safe);
		}
	}

	private static View FindSafeViewToActivate(Document doc, HashSet<ElementId> avoidIds)
	{
		if (doc == null)
		{
			return null;
		}

		avoidIds = avoidIds ?? new HashSet<ElementId>();
		View best = null;
		foreach (View view in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))).Cast<View>())
		{
			if (view == null || view.IsTemplate || !view.CanBePrinted)
			{
				continue;
			}
			if (avoidIds.Contains(((Element)view).Id))
			{
				continue;
			}
			if (view is ViewSheet)
			{
				continue;
			}

			string name = ((Element)view).Name ?? string.Empty;
			if (view is View3D)
			{
				if (name.IndexOf("{3D}", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.Equals("3D", StringComparison.OrdinalIgnoreCase))
				{
					return view;
				}
				best = best ?? view;
				continue;
			}

			if (view is ViewPlan && best == null)
			{
				best = view;
			}
			else if (best == null)
			{
				best = view;
			}
		}

		return best;
	}

	internal static TagCreationResult CreateTags(Document doc, AssemblyInstance assembly, View view, FamilySymbol tagType, string placement, SpoolingManagerKind kind = SpoolingManagerKind.Standard, SpoolingManagerSettings settings = null, ISet<string> existingTaggedItemNumbers = null, string sweldNumberPrefix = null, FamilySymbol weldTagType = null, FamilySymbol assemblyTagType = null, ICollection<ElementId> restrictToPartIds = null, FamilySymbol hangerTagType = null, FamilySymbol ductTagType = null)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Expected O, but got Unknown
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Expected O, but got Unknown
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Expected O, but got Unknown
		//IL_039a: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a1: Expected O, but got Unknown
		Options val = new Options
		{
			ComputeReferences = true,
			IncludeNonVisibleObjects = false,
			View = view
		};
		Options val2 = new Options
		{
			ComputeReferences = true,
			IncludeNonVisibleObjects = false
		};
		TagCreationResult tagCreationResult = new TagCreationResult();
		bool sweldTagging = sweldNumberPrefix != null;
		bool flag = view is View3D;
		List<TagLayoutData> list = new List<TagLayoutData>();
		Dictionary<string, Reference> dictionary = new Dictionary<string, Reference>(StringComparer.OrdinalIgnoreCase);
		List<PendingGroupedTag> list2 = new List<PendingGroupedTag>();
		HashSet<ElementId> alreadyTaggedPartIds = GetExistingTaggedFabricationPartIds(doc, view, null);
		List<FabricationPart> taggableParts = GetTaggablePartsForView(doc, assembly, view);
		foreach (FabricationPart item in taggableParts)
		{
			try
			{
				ElementId partId = ((Element)item).Id;
				if (restrictToPartIds != null && !restrictToPartIds.Contains(partId))
				{
					continue;
				}
				if (alreadyTaggedPartIds.Contains(partId))
				{
					continue;
				}
				if (ShouldExcludeFromItemNumbering(item, taggableParts)
					&& !FabricationPartClassification.IsFabricationHanger(item))
				{
					continue;
				}
				FamilySymbol partTagType = ResolveFabricationPartTagType(item, tagType, hangerTagType, ductTagType);
				if (partTagType == null)
				{
					continue;
				}
				Element val3 = (Element)(object)item;
				if (settings != null && settings.ContinuationTagsEnabled && HasContinuationValue(val3))
				{
					continue;
				}
				if (alreadyTaggedPartIds.Contains(partId))
				{
					continue;
				}
				string fabricationItemNumber = GetFabricationItemNumber(item);
				tagCreationResult.PartsEvaluated++;
				string fabricationItemGroupingKey = GetFabricationItemGroupingKey(item);
				XYZ elementAnchorPoint = GetElementAnchorPoint(val3, view);
				if (elementAnchorPoint == null)
				{
					continue;
				}
				List<XYZ> elementAnchorCandidates = GetElementAnchorCandidates(val3, view);
				if (elementAnchorCandidates.Count == 0)
				{
					elementAnchorCandidates.Add(elementAnchorPoint);
				}
				XYZ point = elementAnchorPoint;
				IndependentTag val4 = null;
				Reference val5 = null;
				if (flag)
				{
					View3D view3D = (View3D)(object)((view is View3D) ? view : null);
					try
					{
						val5 = new Reference(val3);
						tagCreationResult.ElementReferenceAttempts++;
						val4 = TryCreateTagWithStrategies(doc, view, partTagType, val5, point, tagCreationResult);
						if (val4 != null)
						{
							tagCreationResult.ElementReferenceSuccesses++;
						}
					}
					catch
					{
					}
					foreach (XYZ item2 in elementAnchorCandidates)
					{
						if (val4 != null)
						{
							break;
						}
						Reference val6 = Get3DPickedReference(view3D, val3, item2, (FindReferenceTarget)1);
						if (val6 != null)
						{
							tagCreationResult.ElementReferenceAttempts++;
							val4 = TryCreateTagWithStrategies(doc, view, partTagType, val6, item2, tagCreationResult);
							if (val4 != null)
							{
								tagCreationResult.ElementReferenceSuccesses++;
								val5 = val6;
								point = item2;
								break;
							}
						}
					}
					if (val4 == null)
					{
						foreach (XYZ item3 in elementAnchorCandidates)
						{
							Reference val7 = Get3DPickedReference(view3D, val3, item3, (FindReferenceTarget)16);
							if (val7 != null)
							{
								tagCreationResult.FaceReferenceAttempts++;
								val4 = TryCreateTagWithStrategies(doc, view, partTagType, val7, item3, tagCreationResult);
								if (val4 != null)
								{
									tagCreationResult.FaceReferenceSuccesses++;
									val5 = val7;
									point = item3;
									break;
								}
							}
						}
					}
					if (val4 == null)
					{
						foreach (XYZ item4 in elementAnchorCandidates)
						{
							Reference val8 = Get3DPickedReference(view3D, val3, item4, (FindReferenceTarget)4);
							if (val8 != null)
							{
								tagCreationResult.FaceReferenceAttempts++;
								val4 = TryCreateTagWithStrategies(doc, view, partTagType, val8, item4, tagCreationResult);
								if (val4 != null)
								{
									tagCreationResult.FaceReferenceSuccesses++;
									val5 = val8;
									point = item4;
									break;
								}
							}
						}
					}
					if (val4 == null)
					{
						foreach (Reference subelementReference in GetSubelementReferences(val3))
						{
							tagCreationResult.FaceReferenceAttempts++;
							val4 = TryCreateTagWithStrategies(doc, view, partTagType, subelementReference, point, tagCreationResult);
							if (val4 != null)
							{
								tagCreationResult.FaceReferenceSuccesses++;
								val5 = subelementReference;
								break;
							}
						}
					}
					goto IL_03c6;
				}
				if ((GeometryObject)(object)(val3.get_Geometry(val) ?? val3.get_Geometry(val2)) == (GeometryObject)null)
				{
					continue;
				}
				tagCreationResult.ElementReferenceAttempts++;
				val5 = new Reference(val3);
				val4 = TryCreateTagWithStrategies(doc, view, partTagType, val5, point, tagCreationResult);
				if (val4 != null)
				{
					tagCreationResult.ElementReferenceSuccesses++;
				}
				goto IL_03c6;
				IL_03c6:
				if (val4 != null)
				{
					goto IL_049d;
				}
				GeometryElement val9 = val3.get_Geometry(val) ?? val3.get_Geometry(val2);
				if ((GeometryObject)(object)val9 == (GeometryObject)null)
				{
					continue;
				}
				foreach (XYZ item5 in elementAnchorCandidates)
				{
					foreach (Reference allTagReference in GetAllTagReferences(val9, view, item5))
					{
						tagCreationResult.FaceReferenceAttempts++;
						val4 = TryCreateTagWithStrategies(doc, view, partTagType, allTagReference, item5, tagCreationResult);
						if (val4 != null)
						{
							tagCreationResult.FaceReferenceSuccesses++;
							val5 = allTagReference;
							point = item5;
							break;
						}
					}
					if (val4 != null)
					{
						break;
					}
				}
				goto IL_049d;
				IL_049d:
				if (val4 != null)
				{
					goto IL_0574;
				}
				GeometryElement val10 = val3.get_Geometry(val) ?? val3.get_Geometry(val2);
				if ((GeometryObject)(object)val10 == (GeometryObject)null)
				{
					continue;
				}
				foreach (XYZ item6 in elementAnchorCandidates)
				{
					foreach (Reference allEdgeReference in GetAllEdgeReferences(val10, item6, view))
					{
						tagCreationResult.FaceReferenceAttempts++;
						val4 = TryCreateTagWithStrategies(doc, view, partTagType, allEdgeReference, item6, tagCreationResult);
						if (val4 != null)
						{
							tagCreationResult.FaceReferenceSuccesses++;
							val5 = allEdgeReference;
							point = item6;
							break;
						}
					}
					if (val4 != null)
					{
						break;
					}
				}
				goto IL_0574;
				IL_0574:
				if (val4 == null)
				{
					if (!string.IsNullOrWhiteSpace(fabricationItemGroupingKey) && !IsOletBranchStubPipe(item, taggableParts))
					{
						list2.Add(new PendingGroupedTag
						{
							GroupingKey = fabricationItemGroupingKey,
							AnchorPoint = elementAnchorPoint,
							Element = val3
						});
					}
					continue;
				}
				val4.HasLeader = true;
				SetTagHeadPosition(val4, GetTagHeadPoint(val3, view, placement, kind, settings, elementAnchorPoint));
				list.Add(new TagLayoutData
				{
					Tag = val4,
					AnchorPoint = elementAnchorPoint,
					Reference = val5
				});
				if (!string.IsNullOrWhiteSpace(fabricationItemGroupingKey) && val5 != null && !dictionary.ContainsKey(fabricationItemGroupingKey))
				{
					dictionary[fabricationItemGroupingKey] = val5;
				}
				if (existingTaggedItemNumbers != null && !string.IsNullOrWhiteSpace(fabricationItemNumber))
				{
					existingTaggedItemNumbers.Add(fabricationItemNumber);
				}
				alreadyTaggedPartIds.Add(partId);
				tagCreationResult.CreatedCount++;
			}
			catch
			{
				tagCreationResult.Exceptions++;
			}
		}
		if (tagType != null)
		{
			AppendFlangeTags(doc, assembly, view, tagType, placement, kind, settings, list, tagCreationResult);
			AppendUntaggedBranchTakeoffPipeTags(doc, assembly, view, tagType, placement, kind, settings, list, tagCreationResult, restrictToPartIds, alreadyTaggedPartIds);
		}
		if (sweldTagging)
		{
			FamilySymbol val12 = weldTagType ?? tagType;
			if (val12 != null)
			{
				AppendSWeldTags(doc, assembly, view, val12, placement, kind, settings, list, tagCreationResult, restrictToPartIds);
			}
		}
		if (settings != null && settings.ContinuationTagsEnabled && assemblyTagType != null)
		{
			AppendContinuationAssemblyTags(doc, assembly, view, assemblyTagType, placement, kind, settings, list, tagCreationResult);
		}
		foreach (PendingGroupedTag item7 in list2)
		{
			try
			{
				if (item7 != null && !string.IsNullOrWhiteSpace(item7.GroupingKey) && item7.AnchorPoint != null && dictionary.TryGetValue(item7.GroupingKey, out var value) && value != null)
				{
					FamilySymbol groupedTagType = tagType;
					if (item7.Element is FabricationPart groupedPart)
					{
						groupedTagType = ResolveFabricationPartTagType(groupedPart, tagType, hangerTagType, ductTagType) ?? tagType;
					}
					if (groupedTagType == null)
					{
						continue;
					}
					IndependentTag val11 = TryCreateTagWithStrategies(doc, view, groupedTagType, value, item7.AnchorPoint, tagCreationResult);
					if (val11 != null)
					{
						val11.HasLeader = true;
						SetTagHeadPosition(val11, GetTagHeadPoint(item7.Element, view, placement, kind, settings, item7.AnchorPoint));
						list.Add(new TagLayoutData
						{
							Tag = val11,
							AnchorPoint = item7.AnchorPoint,
							Reference = value
						});
						tagCreationResult.CreatedCount++;
					}
				}
			}
			catch
			{
				tagCreationResult.Exceptions++;
			}
		}
		if (list.Count > 1)
		{
			ResolveTagOverlaps(view, list, kind, settings);
		}
		double tagLeaderBaselineInchesOnSheet = GetTagLeaderBaselineInchesOnSheet(view, kind, settings);
		double tagLeaderMaxInchesOnSheet = GetTagLeaderMaxInchesOnSheet(view, kind, settings, tagLeaderBaselineInchesOnSheet);
		EnsureMinimumTagHeadDistances(view, list, tagLeaderBaselineInchesOnSheet / 12.0);
		ClampTagHeadDistances(view, list, tagLeaderMaxInchesOnSheet / 12.0);
		FinalizeTagLeaderMode(view, list);
		TryOrganizeCreatedSpoolTags(doc, assembly, view, list);
		return tagCreationResult;
	}

	/// <summary>
	/// Post-pass after placement: optional host-ring organize for MEP item tags only.
	/// Never runs on View3D and never touches weld (SWeld) tags — both crash Revit.
	/// </summary>
	private static void TryOrganizeCreatedSpoolTags(
		Document doc,
		AssemblyInstance assembly,
		View view,
		IList<TagLayoutData> layoutTags)
	{
		if (doc == null || assembly == null || view == null)
		{
			return;
		}

		// Weld tags on 3D + SpoolTagOrganizer have crashed Revit (stack overflow).
		// Leave weld tags exactly where AppendSWeldTags placed them.
		if (view is View3D)
		{
			return;
		}

		try
		{
			ICollection<ElementId> memberIds = assembly.GetMemberIds();
			if (memberIds == null || memberIds.Count == 0)
			{
				return;
			}

			HashSet<ElementId> tagIds = new HashSet<ElementId>();
			if (layoutTags != null)
			{
				foreach (TagLayoutData layout in layoutTags)
				{
					if (layout?.Tag != null && !IsWeldIndependentTag(doc, layout.Tag))
					{
						tagIds.Add(((Element)layout.Tag).Id);
					}
				}
			}

			// Refresh/recreate paths may skip creating tags that already exist — still organize them.
			foreach (IndependentTag tag in new FilteredElementCollector(doc, ((Element)view).Id)
				.OfClass(typeof(IndependentTag))
				.Cast<IndependentTag>())
			{
				if (tag == null || tag.IsOrphaned || IsWeldIndependentTag(doc, tag))
				{
					continue;
				}

				try
				{
					foreach (Reference reference in tag.GetTaggedReferences())
					{
						if (reference != null && memberIds.Contains(reference.ElementId))
						{
							tagIds.Add(((Element)tag).Id);
							break;
						}
					}
				}
				catch
				{
				}
			}

			if (tagIds.Count == 0)
			{
				return;
			}

			doc.Regenerate();
			// Prefer 1/2"–3/4"; organizer expands up to 2" when elbows are crowded so tags
			// never stay on the assembly or stacked on each other.
			SpoolTagOrganizer.Organize(
				doc,
				view,
				memberIds,
				tagIds,
				minimumDistanceInches: 0.50,
				maximumDistanceInches: 0.75,
				tagClearanceInches: 0.08,
				minLeaderSeparationInches: 0.06);
		}
		catch (Exception ex)
		{
			try
			{
				string logDir = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
					"Autodesk",
					"Revit",
					"Addins",
					"2024",
					"Spooling-Savant-V3-Exports",
					"SpoolingManager");
				Directory.CreateDirectory(logDir);
				File.AppendAllText(
					Path.Combine(logDir, "SpoolTagOrganizer.log"),
					DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
					+ "\tWRAPPER_ERROR " + DiagnosticBuildTag + " " + ex.Message + Environment.NewLine);
			}
			catch
			{
			}
		}
	}

	/// <summary>True when the tag is hosted on a fabrication weld / SWeld part.</summary>
	private static bool IsWeldIndependentTag(Document doc, IndependentTag tag)
	{
		if (doc == null || tag == null)
		{
			return false;
		}

		try
		{
			foreach (Reference reference in tag.GetTaggedReferences())
			{
				if (reference == null)
				{
					continue;
				}

				FabricationPart part = doc.GetElement(reference.ElementId) as FabricationPart;
				if (part != null && ShouldReceiveSWeldTag(part))
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

	private static List<FabricationPart> GetTaggablePartsForView(Document doc, AssemblyInstance assembly, View view)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		// This view-scoped collector is invoked several times per sheet (tagging + weld log). The set of
		// fabrication parts visible in a spool view is stable once the view is built, so cache it per view
		// for the duration of a single Create run.
		if (doc != null && view != null)
		{
			if (!ReferenceEquals(_taggablePartsCacheDoc, doc))
			{
				_taggablePartsCache.Clear();
				_taggablePartsCacheDoc = doc;
			}
			if (_taggablePartsCache.TryGetValue(((Element)view).Id, out List<FabricationPart> cached))
			{
				return cached;
			}
		}
		List<FabricationPart> list = new List<FabricationPart>();
		try
		{
			list = (from FabricationPart x in (IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(FabricationPart))
				where x != null
				select x).ToList();
		}
		catch
		{
		}
		if (list.Count == 0)
		{
			list = (from x in assembly.GetMemberIds()
				select doc.GetElement(x)).OfType<FabricationPart>().ToList();
		}
		if (doc != null && view != null)
		{
			_taggablePartsCache[((Element)view).Id] = list;
		}
		return list;
	}

	internal static void RestrictViewToAssemblyElements(Document doc, AssemblyInstance assembly, View view)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		if (doc == null || assembly == null || view == null)
		{
			return;
		}
		HashSet<ElementId> assemblyMemberIds = new HashSet<ElementId>(assembly.GetMemberIds());
		List<ElementId> list = new List<ElementId>();
		try
		{
			list = (from FabricationPart x in (IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(FabricationPart))
				where x != null && !assemblyMemberIds.Contains(((Element)x).Id)
				select ((Element)x).Id).ToList();
		}
		catch
		{
		}
		if (list.Count == 0)
		{
			return;
		}
		try
		{
			view.HideElements((ICollection<ElementId>)list);
		}
		catch
		{
		}
	}

	internal static FamilySymbol ResolveFabricationPartTagType(
		FabricationPart part,
		FamilySymbol pipeFittingTagType,
		FamilySymbol hangerTagType,
		FamilySymbol ductTagType)
	{
		if (part == null)
		{
			return pipeFittingTagType;
		}
		Category category = ((Element)part).Category;
		if (category == null)
		{
			return pipeFittingTagType;
		}
		long categoryId;
		try
		{
			categoryId = category.Id.Value;
		}
		catch
		{
			return pipeFittingTagType;
		}
		if (categoryId == (long)BuiltInCategory.OST_FabricationHangers)
		{
			return hangerTagType;
		}
		if (categoryId == (long)BuiltInCategory.OST_FabricationDuctwork)
		{
			return ductTagType;
		}
		return pipeFittingTagType;
	}

	internal static void RemoveAssemblyFabricationTags(
		Document doc,
		AssemblyInstance assembly,
		View view,
		FamilySymbol tagType,
		FamilySymbol weldTagType,
		FamilySymbol hangerTagType = null,
		FamilySymbol ductTagType = null)
	{
		if (doc == null || view == null)
		{
			return;
		}

		HashSet<ElementId> tagTypeIds = new HashSet<ElementId>();
		if (tagType != null)
		{
			tagTypeIds.Add(tagType.Id);
		}
		if (weldTagType != null)
		{
			tagTypeIds.Add(weldTagType.Id);
		}
		if (hangerTagType != null)
		{
			tagTypeIds.Add(hangerTagType.Id);
		}
		if (ductTagType != null)
		{
			tagTypeIds.Add(ductTagType.Id);
		}
		if (tagTypeIds.Count == 0)
		{
			return;
		}

		HashSet<ElementId> tagsToDelete = new HashSet<ElementId>();
		foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
		{
			if (!tagTypeIds.Contains(tag.GetTypeId()))
			{
				continue;
			}

			ISet<ElementId> taggedIds;
			try
			{
				taggedIds = tag.GetTaggedLocalElementIds();
			}
			catch
			{
				continue;
			}

			if (taggedIds == null || taggedIds.Count == 0)
			{
				continue;
			}

			foreach (ElementId taggedId in taggedIds)
			{
				if (doc.GetElement(taggedId) is FabricationPart)
				{
					tagsToDelete.Add(tag.Id);
					break;
				}
			}
		}

		foreach (ElementId tagId in tagsToDelete)
		{
			try
			{
				doc.Delete(tagId);
			}
			catch
			{
			}
		}
	}

	private static Reference Get3DPickedReference(View3D view3D, Element element, XYZ anchorPoint, FindReferenceTarget target)
	{
		if (view3D == null)
		{
			return null;
		}
		return GetViewPickedReference((View)(object)view3D, element, anchorPoint, target);
	}

	private static IEnumerable<XYZ> BuildReferencePickRayDirections(View view)
	{
		if (view == null)
		{
			yield break;
		}
		XYZ viewDirection = view.ViewDirection;
		XYZ rightDirection = view.RightDirection;
		XYZ upDirection = view.UpDirection;
		if (viewDirection == null || viewDirection.GetLength() < 1E-09 || rightDirection == null || rightDirection.GetLength() < 1E-09 || upDirection == null || upDirection.GetLength() < 1E-09)
		{
			yield break;
		}
		viewDirection = viewDirection.Normalize();
		rightDirection = rightDirection.Normalize();
		upDirection = upDirection.Normalize();
		XYZ[] array = new XYZ[14]
		{
			viewDirection,
			viewDirection.Negate(),
			rightDirection,
			rightDirection.Negate(),
			upDirection,
			upDirection.Negate(),
			(viewDirection + rightDirection).Normalize(),
			(viewDirection - rightDirection).Normalize(),
			(-viewDirection + rightDirection).Normalize(),
			(-viewDirection - rightDirection).Normalize(),
			(viewDirection + upDirection).Normalize(),
			(viewDirection - upDirection).Normalize(),
			(-viewDirection + upDirection).Normalize(),
			(-viewDirection - upDirection).Normalize()
		};
		XYZ[] array2 = array;
		foreach (XYZ item in array2)
		{
			if (item != null && item.GetLength() > 1E-09)
			{
				yield return item.Normalize();
			}
		}
	}

	private static Reference GetViewPickedReference(View view, Element element, XYZ anchorPoint, FindReferenceTarget target)
	{
		View3D view3D = (View3D)(object)((view is View3D) ? view : null);
		if (view3D == null || element == null || anchorPoint == null)
		{
			return null;
		}
		double referenceRayLength = GetReferenceRayLength(element, view);
		ReferenceIntersector val;
		try
		{
			val = new ReferenceIntersector(element.Id, target, view3D);
		}
		catch
		{
			return null;
		}
		foreach (XYZ item in BuildReferencePickRayDirections(view))
		{
			ReferenceWithContext val2 = null;
			try
			{
				XYZ val3 = anchorPoint - item.Multiply(referenceRayLength);
				val2 = val.FindNearest(val3, item);
			}
			catch
			{
			}
			if (val2 == null)
			{
				continue;
			}
			Reference reference = val2.GetReference();
			if (reference != null && !(reference.ElementId != element.Id) && !IsWholeElementDimensionReference(element, reference))
			{
				return reference;
			}
		}
		return null;
	}

	private static bool TryPickViewDimensionReferenceAtPoint(Element element, View view, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = targetWorld;
		if (element == null || view == null || targetWorld == null)
		{
			return false;
		}
		FindReferenceTarget[] array = new FindReferenceTarget[2]
		{
			(FindReferenceTarget)16,
			(FindReferenceTarget)4
		};
		FindReferenceTarget[] array2 = array;
		foreach (FindReferenceTarget target in array2)
		{
			Reference viewPickedReference = GetViewPickedReference(view, element, targetWorld, target);
			if (viewPickedReference != null)
			{
				reference = viewPickedReference;
				return true;
			}
		}
		View3D val = _autoDimReferencePickView3D ?? TryFindAssembly3DOrthographicView(element.Document, element);
		if (val != null)
		{
			FindReferenceTarget[] array3 = new FindReferenceTarget[3]
			{
				(FindReferenceTarget)16,
				(FindReferenceTarget)4,
				(FindReferenceTarget)1
			};
			foreach (FindReferenceTarget target2 in array3)
			{
				Reference val2 = Get3DPickedReference(val, element, targetWorld, target2);
				if (val2 != null && TryAcceptGeometricDimensionReference(element, val2, targetWorld, out reference, out referencePointWorld))
				{
					return true;
				}
			}
		}
		if (TryExtractEdgeReferencesForDimension(element, view, targetWorld, out reference, out referencePointWorld) && IsGeometricLinearDimensionReference(element, reference))
		{
			return true;
		}
		if (TryExtractFaceReferencesForDimension(element, view, targetWorld, null, out reference, out referencePointWorld) && IsGeometricLinearDimensionReference(element, reference))
		{
			return true;
		}
		return false;
	}

	private static bool TryAcceptGeometricDimensionReference(Element element, Reference candidate, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = targetWorld;
		if (candidate != null && TryAcceptUsableLinearDimensionReference(element, candidate))
		{
			reference = candidate;
			return true;
		}
		return false;
	}

	private static double GetReferenceRayLength(Element element, View view)
	{
		BoundingBoxXYZ val = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
		if (val == null)
		{
			return 10.0;
		}
		double num = val.Max.X - val.Min.X;
		double num2 = val.Max.Y - val.Min.Y;
		double num3 = val.Max.Z - val.Min.Z;
		return Math.Max(Math.Sqrt(num * num + num2 * num2 + num3 * num3) * 2.0, 5.0);
	}

	private static IndependentTag TryCreateTagWithStrategies(Document doc, View view, FamilySymbol tagType, Reference reference, XYZ point, TagCreationResult result)
	{
		List<Func<IndependentTag>> list = new List<Func<IndependentTag>>
		{
			() => TryCreateContinuationTag(doc, view, tagType, reference, point, result),
			delegate
			{
				IndependentTag val2 = IndependentTag.Create(doc, ((Element)view).Id, reference, true, (TagMode)0, (TagOrientation)0, point);
				if (val2 != null && TryApplyTagType(val2, tagType))
				{
					if (result != null)
					{
						result.ByCategoryLeaderSuccesses++;
					}
					return val2;
				}
				DeleteTagIfWrongType(doc, val2, tagType);
				return null;
			},
			delegate
			{
				IndependentTag val2 = IndependentTag.Create(doc, ((Element)view).Id, reference, false, (TagMode)0, (TagOrientation)0, point);
				if (val2 != null && TryApplyTagType(val2, tagType))
				{
					val2.HasLeader = true;
					if (result != null)
					{
						result.ByCategoryNoLeaderSuccesses++;
					}
					return val2;
				}
				DeleteTagIfWrongType(doc, val2, tagType);
				return null;
			}
		};
		for (int num = 0; num < list.Count; num++)
		{
			try
			{
				IndependentTag val = list[num]();
				if (val != null)
				{
					return val;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	internal static bool TryCreateSpoolMapAssemblyTag(
		Document doc,
		View3D view3D,
		AssemblyInstance assembly,
		FamilySymbol assemblyTagType,
		int tagIndex = 0)
	{
		if (doc == null || view3D == null || assembly == null || assemblyTagType == null)
		{
			return false;
		}
		try
		{
			if (!assemblyTagType.IsActive)
			{
				assemblyTagType.Activate();
				doc.Regenerate();
			}
		}
		catch
		{
		}

		XYZ tagPoint = GetSpoolMapAssemblyTagPoint(assembly, view3D)
			?? GetSpoolMapAssemblyTagPointFromMembers(doc, assembly, view3D);
		if (tagPoint == null)
		{
			return false;
		}

		try
		{
			IndependentTag directTag = IndependentTag.Create(
				doc,
				assemblyTagType.Id,
				view3D.Id,
				new Reference(assembly),
				true,
				TagOrientation.Horizontal,
				tagPoint);
			if (directTag != null && TryApplySpoolMapAssemblyTagType(directTag, assemblyTagType))
			{
				FinalizeSpoolMapAssemblyTag(directTag, view3D, tagPoint, tagIndex);
				return true;
			}
			DeleteSpoolMapTagIfWrongType(doc, directTag, assemblyTagType);
		}
		catch
		{
		}

		HashSet<string> seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<Reference> references = new List<Reference>();
		FindReferenceTarget[] pickTargets =
		{
			(FindReferenceTarget)1,
			(FindReferenceTarget)16,
			(FindReferenceTarget)4
		};

		void TryAddReference(Reference reference)
		{
			if (reference == null)
			{
				return;
			}
			try
			{
				string stable = reference.ConvertToStableRepresentation(doc);
				if (!string.IsNullOrWhiteSpace(stable) && seenReferences.Add(stable))
				{
					references.Add(reference);
				}
			}
			catch
			{
				string fallbackKey = reference.ElementId.Value.ToString();
				if (seenReferences.Add(fallbackKey))
				{
					references.Add(reference);
				}
			}
		}

		try
		{
			TryAddReference(new Reference(assembly));
		}
		catch
		{
		}

		foreach (FindReferenceTarget pickTarget in pickTargets)
		{
			TryAddReference(Get3DPickedReference(view3D, assembly, tagPoint, pickTarget));
		}

		int membersChecked = 0;
		foreach (ElementId memberId in assembly.GetMemberIds())
		{
			Element member = doc.GetElement(memberId);
			if (member == null)
			{
				continue;
			}
			try
			{
				TryAddReference(new Reference(member));
			}
			catch
			{
			}
			XYZ memberPoint = GetSpoolMapAssemblyTagPoint(member, view3D) ?? tagPoint;
			foreach (FindReferenceTarget pickTarget in pickTargets)
			{
				TryAddReference(Get3DPickedReference(view3D, member, memberPoint, pickTarget));
			}
			membersChecked++;
			if (membersChecked >= 8)
			{
				break;
			}
		}

		foreach (Reference reference in references)
		{
			IndependentTag typedTag = TryCreateContinuationTag(doc, (View)(object)view3D, assemblyTagType, reference, tagPoint, null);
			if (typedTag != null)
			{
				FinalizeSpoolMapAssemblyTag(typedTag, view3D, tagPoint, tagIndex);
				if (SpoolMapAssemblyTagSucceeded(doc, typedTag, assembly, assemblyTagType))
				{
					return true;
				}
				DeleteSpoolMapTag(doc, typedTag);
			}
			IndependentTag categoryTag = TryCreateTagWithStrategies(doc, (View)(object)view3D, assemblyTagType, reference, tagPoint, null);
			if (categoryTag != null)
			{
				FinalizeSpoolMapAssemblyTag(categoryTag, view3D, tagPoint, tagIndex);
				if (SpoolMapAssemblyTagSucceeded(doc, categoryTag, assembly, assemblyTagType))
				{
					return true;
				}
				DeleteSpoolMapTag(doc, categoryTag);
			}
		}

		return false;
	}

	private static bool SpoolMapAssemblyTagSucceeded(
		Document doc,
		IndependentTag tag,
		AssemblyInstance assembly,
		FamilySymbol assemblyTagType)
	{
		if (tag == null || assembly == null || assemblyTagType == null || tag.GetTypeId() != assemblyTagType.Id)
		{
			return false;
		}
		if (TagIsOnAssembly(tag, assembly))
		{
			return true;
		}
		try
		{
			ISet<ElementId> taggedIds = tag.GetTaggedLocalElementIds();
			if (taggedIds == null)
			{
				return false;
			}
			foreach (ElementId taggedId in taggedIds)
			{
				if (taggedId == assembly.Id)
				{
					return true;
				}
				if (assembly.GetMemberIds().Contains(taggedId))
				{
					return true;
				}
				Element taggedElement = doc?.GetElement(taggedId);
				if (taggedElement is AssemblyInstance taggedAssembly && taggedAssembly.Id == assembly.Id)
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

	private static void DeleteSpoolMapTag(Document doc, IndependentTag tag)
	{
		if (doc == null || tag == null)
		{
			return;
		}
		try
		{
			doc.Delete(tag.Id);
		}
		catch
		{
		}
	}

	private static XYZ GetSpoolMapAssemblyTagPointFromMembers(Document doc, AssemblyInstance assembly, View3D view3D)
	{
		if (doc == null || assembly == null)
		{
			return null;
		}
		List<XYZ> points = new List<XYZ>();
		foreach (ElementId memberId in assembly.GetMemberIds())
		{
			Element member = doc.GetElement(memberId);
			if (member == null)
			{
				continue;
			}
			XYZ point = GetSpoolMapAssemblyTagPoint(member, view3D);
			if (point != null)
			{
				points.Add(point);
			}
			if (points.Count >= 8)
			{
				break;
			}
		}
		if (points.Count == 0)
		{
			return null;
		}
		return new XYZ(
			points.Average(p => p.X),
			points.Average(p => p.Y),
			points.Average(p => p.Z));
	}

	private static bool TagIsOnAssembly(IndependentTag tag, AssemblyInstance assembly)
	{
		if (tag == null || assembly == null)
		{
			return false;
		}
		try
		{
			ISet<ElementId> taggedIds = tag.GetTaggedLocalElementIds();
			return taggedIds != null && taggedIds.Contains(assembly.Id);
		}
		catch
		{
			return false;
		}
	}

	private static void FinalizeSpoolMapAssemblyTag(IndependentTag tag, View3D view3D, XYZ anchorPoint, int tagIndex = 0)
	{
		if (tag == null || view3D == null || anchorPoint == null)
		{
			return;
		}
		try
		{
			tag.HasLeader = true;
			XYZ tagHeadPoint = GetSpoolMapTagHeadPoint(view3D, anchorPoint, tagIndex);
			tag.TagHeadPosition = tagHeadPoint;
		}
		catch
		{
		}
	}

	private static XYZ GetSpoolMapTagHeadPoint(View3D view3D, XYZ anchorPoint, int tagIndex = 0)
	{
		if (view3D == null || anchorPoint == null)
		{
			return anchorPoint;
		}
		try
		{
			XYZ up = view3D.UpDirection?.Normalize() ?? XYZ.BasisZ;
			XYZ right = view3D.RightDirection?.Normalize() ?? XYZ.BasisX;
			double baseOffsetFeet = 2.0 / 12.0;
			double stackOffsetFeet = Math.Max(0, tagIndex) * (5.0 / 12.0);
			return anchorPoint
				+ up * (baseOffsetFeet + stackOffsetFeet)
				+ right * (baseOffsetFeet + stackOffsetFeet * 0.5);
		}
		catch
		{
			return anchorPoint;
		}
	}

	private static XYZ GetSpoolMapAssemblyTagPoint(Element element, View view = null)
	{
		if (element == null)
		{
			return null;
		}
		BoundingBoxXYZ box = (view != null ? element.get_BoundingBox(view) : null) ?? element.get_BoundingBox(null);
		if (box != null)
		{
			return (box.Min + box.Max) * 0.5;
		}
		AssemblyInstance assembly = element as AssemblyInstance;
		if (assembly != null)
		{
			try
			{
				return assembly.GetTransform()?.Origin;
			}
			catch
			{
			}
		}
		return null;
	}

	private static bool TryApplySpoolMapAssemblyTagType(IndependentTag tag, FamilySymbol assemblyTagType)
	{
		if (tag == null || assemblyTagType == null)
		{
			return false;
		}
		if (tag.GetTypeId() == assemblyTagType.Id)
		{
			return true;
		}
		try
		{
			tag.ChangeTypeId(assemblyTagType.Id);
		}
		catch
		{
		}
		return tag.GetTypeId() == assemblyTagType.Id;
	}

	private static void DeleteSpoolMapTagIfWrongType(Document doc, IndependentTag tag, FamilySymbol assemblyTagType)
	{
		if (doc == null || tag == null || assemblyTagType == null || tag.GetTypeId() == assemblyTagType.Id)
		{
			return;
		}
		try
		{
			doc.Delete(tag.Id);
		}
		catch
		{
		}
	}

	private static void Apply3DDirection(AssemblyInstance assembly, View3D view3D, string direction)
	{
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Expected O, but got Unknown
		//IL_00cb: Expected O, but got Unknown
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Expected O, but got Unknown
		if (assembly != null && view3D != null)
		{
			BoundingBoxXYZ val = assembly.get_BoundingBox(view3D) ?? assembly.get_BoundingBox(null);
			if (val != null)
			{
				XYZ val2 = new XYZ((val.Min.X + val.Max.X) * 0.5, (val.Min.Y + val.Max.Y) * 0.5, (val.Min.Z + val.Max.Z) * 0.5);
				XYZ val3 = GetHorizontalDirectionVector(direction).Negate();
				XYZ val4 = new XYZ(val3.X, val3.Y, 0.75).Normalize();
				XYZ val5 = val2 + val4.Multiply(Get3DEyeDistance(val));
				XYZ val6 = (val2 - val5).Normalize();
				XYZ val7 = XYZ.BasisZ.CrossProduct(val6).Normalize();
				XYZ val8 = val6.CrossProduct(val7).Normalize();
				view3D.SetOrientation(new ViewOrientation3D(val5, val8, val6));
			}
		}
	}

	private static XYZ GetHorizontalDirectionVector(string direction)
	{
		//IL_01cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Expected O, but got Unknown
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Expected O, but got Unknown
		//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b4: Expected O, but got Unknown
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Expected O, but got Unknown
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_0141: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		return (XYZ)((direction ?? string.Empty).Trim().ToUpperInvariant() switch
		{
			"N" => (object)new XYZ(0.0, 1.0, 0.0), 
			"NE" => new XYZ(1.0, 1.0, 0.0).Normalize(), 
			"E" => (object)new XYZ(1.0, 0.0, 0.0), 
			"SE" => new XYZ(1.0, -1.0, 0.0).Normalize(), 
			"S" => (object)new XYZ(0.0, -1.0, 0.0), 
			"SW" => new XYZ(-1.0, -1.0, 0.0).Normalize(), 
			"W" => (object)new XYZ(-1.0, 0.0, 0.0), 
			_ => new XYZ(-1.0, 1.0, 0.0).Normalize(), 
		});
	}

	private static double Get3DEyeDistance(BoundingBoxXYZ bbox)
	{
		double num = bbox.Max.X - bbox.Min.X;
		double num2 = bbox.Max.Y - bbox.Min.Y;
		double num3 = bbox.Max.Z - bbox.Min.Z;
		return Math.Max(Math.Sqrt(num * num + num2 * num2 + num3 * num3) * 2.5, 10.0);
	}

	private static void ApplyViewTemplate(Document doc, View view, string templateName)
	{
		if (doc != null && view != null && !string.IsNullOrWhiteSpace(templateName))
		{
			View val = FindViewTemplate(doc, templateName);
			if (val != null)
			{
				view.ViewTemplateId = ((Element)val).Id;
			}
		}
	}

	private static void DetachSpoolViewFromTemplate(View view)
	{
		if (view == null)
		{
			return;
		}
		try
		{
			if (view.ViewTemplateId != ElementId.InvalidElementId)
			{
				view.ViewTemplateId = ElementId.InvalidElementId;
			}
		}
		catch
		{
		}
	}

	private static void ApplySpoolViewTemplateFromSettings(Document doc, View view, string templateName)
	{
		if (doc == null || view == null || string.IsNullOrWhiteSpace(templateName))
		{
			return;
		}
		View template = FindViewTemplate(doc, templateName);
		if (template == null)
		{
			return;
		}
		if (TryApplyViewTemplateParametersOnce(view, template))
		{
			return;
		}
		try
		{
			view.ViewTemplateId = ((Element)template).Id;
		}
		catch
		{
			return;
		}
		try
		{
			RegenTracked(doc);
		}
		catch
		{
		}
		DetachSpoolViewFromTemplate(view);
	}

	private static bool TryApplyViewTemplateParametersOnce(View view, View template)
	{
		if (view == null || template == null)
		{
			return false;
		}
		try
		{
			view.ApplyViewTemplateParameters(template);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void TryEnsureSpoolViewFineDetail(View view)
	{
		if (view == null)
		{
			return;
		}
		try
		{
			if (view.DetailLevel != ViewDetailLevel.Fine)
			{
				view.DetailLevel = ViewDetailLevel.Fine;
			}
		}
		catch
		{
		}
	}

	private static void TryEnsureSpoolViewGeometryForAnnotation(Document doc, View view)
	{
		if (view == null)
		{
			return;
		}
		TryEnsureSpoolViewFineDetail(view);
		TrySetViewCategoryVisible(view, BuiltInCategory.OST_FabricationPipework);
		if (view is View3D)
		{
			return;
		}
		TrySetViewCategoryVisible(view, BuiltInCategory.OST_Dimensions);
		TrySetViewCategoryVisible(view, BuiltInCategory.OST_Lines);
		TryUnhideAllViewDimensions(doc, view);
	}

	private static void ApplyViewScale(View view, int scale)
	{
		if (view == null)
		{
			return;
		}
		try
		{
			if (view.Scale != scale)
			{
				view.Scale = scale;
			}
		}
		catch
		{
		}
	}

	private static void EnsureSpoolAssemblyDetailViewCrop(View view)
	{
		EnsureEditableSpoolViewCrop(view);
	}

	private static void EnsureEditableSpoolViewCrop(View view)
	{
		if (view == null || view is View3D)
		{
			return;
		}
		try
		{
			ElementId viewTemplateId = view.ViewTemplateId;
			if (viewTemplateId != ElementId.InvalidElementId)
			{
				TryAllowCropOutsideTemplateControl(view);
			}
			if (!view.CropBoxActive)
			{
				if (viewTemplateId != ElementId.InvalidElementId)
				{
					view.ViewTemplateId = ElementId.InvalidElementId;
				}
				ActivateSpoolViewCrop(view);
				if (viewTemplateId != ElementId.InvalidElementId)
				{
					view.ViewTemplateId = viewTemplateId;
					TryAllowCropOutsideTemplateControl(view);
					if (!view.CropBoxActive)
					{
						ActivateSpoolViewCrop(view);
					}
				}
			}
			view.CropBoxVisible = false;
		}
		catch
		{
		}
	}

	private static void ActivateSpoolViewCrop(View view)
	{
		view.CropBoxActive = true;
		view.CropBoxVisible = false;
		view.CropBoxActive = false;
		view.CropBoxActive = true;
		view.CropBoxVisible = false;
	}

	private static void TryAllowCropOutsideTemplateControl(View view)
	{
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Expected O, but got Unknown
		if (view == null)
		{
			return;
		}
		ICollection<ElementId> nonControlledTemplateParameterIds = view.GetNonControlledTemplateParameterIds();
		List<ElementId> list = ((nonControlledTemplateParameterIds == null) ? new List<ElementId>() : nonControlledTemplateParameterIds.ToList());
		bool flag = false;
		foreach (Parameter parameter2 in ((Element)view).Parameters)
		{
			Parameter parameter = parameter2;
			if (parameter != null && !((APIObject)parameter).IsReadOnly)
			{
				Definition definition = parameter.Definition;
				string text = ((definition != null) ? definition.Name : null) ?? string.Empty;
				if ((text.Equals("Crop View", StringComparison.OrdinalIgnoreCase) || text.Equals("Crop Region Visible", StringComparison.OrdinalIgnoreCase) || text.Equals("Annotation Crop", StringComparison.OrdinalIgnoreCase)) && !list.Any((ElementId id) => id == parameter.Id))
				{
					list.Add(parameter.Id);
					flag = true;
				}
			}
		}
		if (flag)
		{
			view.SetNonControlledTemplateParameterIds((ICollection<ElementId>)list);
		}
	}

	private static void FinalizeSpoolViewForSheetPlacement(Document doc, AssemblyInstance assembly, View view, bool includeTagExtents, bool includeDimensionExtents, bool includeDimensionLabelExtents = false)
	{
		if (doc == null || assembly == null || view == null)
		{
			return;
		}
		try
		{
			RegenTracked(doc);
		}
		catch
		{
		}
		FitSpoolAssemblyViewCropToContent(doc, assembly, view, includeTagExtents, includeDimensionExtents, includeDimensionLabelExtents);
		try
		{
			RegenTracked(doc);
		}
		catch
		{
		}
	}

	private static void FitSpoolAssemblyViewCropToContent(Document doc, AssemblyInstance assembly, View view, bool includeTagExtents, bool includeDimensionExtents = false, bool includeDimensionLabelExtents = false)
	{
		if (doc == null || assembly == null || view == null)
		{
			return;
		}
		try
		{
			if (view is View3D)
			{
				return;
			}
			FitSpoolAssemblyDetailViewCropToContent(doc, assembly, view, includeTagExtents, includeDimensionExtents, includeDimensionLabelExtents);
		}
		catch
		{
		}
	}

	private static Transform TryBuildDetailViewCropTransform(View view, XYZ centerWorld)
	{
		if (view == null || centerWorld == null)
		{
			return null;
		}
		XYZ normal = view.ViewDirection;
		XYZ right = view.RightDirection;
		XYZ up = view.UpDirection;
		if (normal == null || normal.GetLength() < 1E-09)
		{
			return null;
		}
		normal = normal.Normalize();
		if (right == null || right.GetLength() < 1E-09)
		{
			right = normal.CrossProduct((up != null && up.GetLength() > 1E-09) ? up : XYZ.BasisZ);
		}
		if (right.GetLength() < 1E-09)
		{
			return null;
		}
		right = right.Normalize();
		if (up == null || up.GetLength() < 1E-09)
		{
			up = right.CrossProduct(normal);
		}
		up = up.Normalize();
		up = right.CrossProduct(normal).Normalize();
		Transform transform = Transform.Identity;
		transform.Origin = centerWorld;
		transform.BasisX = right;
		transform.BasisY = up;
		transform.BasisZ = normal;
		return transform;
	}

	private static bool TryForceSpoolDetailViewCropActive(View view)
	{
		if (view == null || view is View3D)
		{
			return false;
		}
		EnsureEditableSpoolViewCrop(view);
		try
		{
			if (view.CropBoxActive)
			{
				return true;
			}
		}
		catch
		{
		}
		ElementId templateId = view.ViewTemplateId;
		try
		{
			if (templateId != ElementId.InvalidElementId)
			{
				TryAllowCropOutsideTemplateControl(view);
				view.ViewTemplateId = ElementId.InvalidElementId;
			}
			ActivateSpoolViewCrop(view);
			view.CropBoxActive = true;
			bool active = view.CropBoxActive;
			if (templateId != ElementId.InvalidElementId)
			{
				view.ViewTemplateId = templateId;
				TryAllowCropOutsideTemplateControl(view);
				EnsureEditableSpoolViewCrop(view);
				active = view.CropBoxActive;
			}
			return active;
		}
		catch
		{
			try
			{
				if (templateId != ElementId.InvalidElementId)
				{
					view.ViewTemplateId = templateId;
				}
			}
			catch
			{
			}
			return false;
		}
	}

	private static List<XYZ> CollectSpoolViewExtentPoints(Document doc, AssemblyInstance assembly, View view, bool includeTagExtents, bool includeDimensionExtents, bool includeDimensionLabelExtents = false)
	{
		List<XYZ> points = new List<XYZ>();
		if (doc == null || assembly == null || view == null)
		{
			return points;
		}
		int flags = (includeTagExtents ? 1 : 0) | (includeDimensionExtents ? 2 : 0) | (includeDimensionLabelExtents ? 4 : 0);
		long viewId = ((Element)view).Id.Value;
		if (_batchSheetGeneration && doc != null)
		{
			if (!ReferenceEquals(_viewExtentCacheDoc, doc))
			{
				_viewExtentCache.Clear();
				_viewExtentCacheDoc = doc;
			}
			if (_viewExtentCache.TryGetValue((viewId, flags), out List<XYZ> cached))
			{
				return new List<XYZ>(cached);
			}
		}
		List<XYZ> memberPoints = CollectDetailViewMemberCropPoints(doc, assembly, view).ToList();
		points.AddRange(memberPoints);
		if (includeDimensionExtents)
		{
			points.AddRange(CollectDimensionCropPoints(doc, view));
		}
		else if (includeDimensionLabelExtents)
		{
			points.AddRange(CollectDimensionLabelCropPoints(doc, view));
		}
		if (points.Count == 0)
		{
			List<XYZ> fallbackMemberPoints = CollectDetailViewMemberCropPoints(doc, assembly, view).ToList();
			if (fallbackMemberPoints.Count > 0)
			{
				points.AddRange(fallbackMemberPoints);
			}
			else
			{
				BoundingBoxXYZ assemblyBounds = assembly.get_BoundingBox(view);
				if (assemblyBounds != null)
				{
					points.AddRange(GetBoundingBoxCorners(assemblyBounds));
				}
			}
		}
		if (includeTagExtents && memberPoints.Count > 0)
		{
			XYZ center = new XYZ(memberPoints.Average((XYZ p) => p.X), memberPoints.Average((XYZ p) => p.Y), memberPoints.Average((XYZ p) => p.Z));
			double maxSpan = ComputePointCloudMaxSpan(memberPoints) * 1.05 + 0.25;
			foreach (XYZ tagHead in CollectTagHeadPositions(doc, view))
			{
				if (tagHead != null && center.DistanceTo(tagHead) <= maxSpan)
				{
					points.Add(tagHead);
				}
			}
		}
		List<XYZ> result = points.Where((XYZ p) => p != null).ToList();
		if (_batchSheetGeneration && doc != null)
		{
			_viewExtentCache[(viewId, flags)] = result;
		}
		return result;
	}

	private static bool ViewHasSpoolDimensions(Document doc, View view)
	{
		return CountViewLinearDimensions(doc, view) > 0;
	}

	private static double ComputePointCloudMaxSpan(IList<XYZ> points)
	{
		if (points == null || points.Count == 0)
		{
			return 1.0 / 12.0;
		}
		double minX = points.Min((XYZ p) => p.X);
		double minY = points.Min((XYZ p) => p.Y);
		double minZ = points.Min((XYZ p) => p.Z);
		double maxX = points.Max((XYZ p) => p.X);
		double maxY = points.Max((XYZ p) => p.Y);
		double maxZ = points.Max((XYZ p) => p.Z);
		return Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
	}

	private static IEnumerable<XYZ> CollectDetailViewMemberCropPoints(Document doc, AssemblyInstance assembly, View view)
	{
		// Fit the crop to the FULL physical bounding box of every assembly member (matches the
		// known-good E-Tools behavior). Using centerline-only "tight" points plus an outlier
		// filter under-sizes the crop and drops legitimately long members (e.g. a long run off
		// an elbow), which slices real geometry off at the crop edge. Enclose everything here and
		// let FitSpoolViewportOnSheet shrink the scale so the whole spool still fits the sheet.
		foreach (ElementId memberId in assembly.GetMemberIds())
		{
			Element element = doc.GetElement(memberId);
			if (element == null || IsElementHiddenInView(element, view))
			{
				continue;
			}
			BoundingBoxXYZ bbox = null;
			try
			{
				bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
			}
			catch
			{
			}
			if (bbox != null)
			{
				foreach (XYZ corner in GetBoundingBoxCorners(bbox))
				{
					yield return corner;
				}
				continue;
			}
			foreach (XYZ point in TryCollectElementTightViewPoints(element, view))
			{
				yield return point;
			}
		}
	}

	private static XYZ TryGetElementViewCenter(Element element, View view)
	{
		if (element == null || view == null)
		{
			return null;
		}
		List<XYZ> points = TryCollectElementTightViewPoints(element, view).ToList();
		if (points.Count > 0)
		{
			return new XYZ(points.Average((XYZ p) => p.X), points.Average((XYZ p) => p.Y), points.Average((XYZ p) => p.Z));
		}
		return GetBoundingBoxCenter(element.get_BoundingBox(view));
	}

	private static IEnumerable<XYZ> TryCollectElementTightViewPoints(Element element, View view)
	{
		if (element == null || view == null)
		{
			yield break;
		}
		Location location = element.Location;
		LocationCurve locationCurve = location as LocationCurve;
		if (locationCurve != null)
		{
			Curve curve = locationCurve.Curve;
			if (curve != null && curve.IsBound)
			{
				XYZ end0 = null;
				XYZ end1 = null;
				try
				{
					end0 = curve.GetEndPoint(0);
					end1 = curve.GetEndPoint(1);
				}
				catch
				{
				}
				if (end0 != null)
				{
					yield return end0;
				}
				if (end1 != null)
				{
					yield return end1;
				}
				XYZ mid = null;
				try
				{
					mid = curve.Evaluate(0.5, true);
				}
				catch
				{
				}
				if (mid != null)
				{
					yield return mid;
				}
				yield break;
			}
		}
		LocationPoint locationPoint = location as LocationPoint;
		if (locationPoint != null && locationPoint.Point != null)
		{
			yield return locationPoint.Point;
			yield break;
		}
		BoundingBoxXYZ bbox = element.get_BoundingBox(view);
		if (bbox == null)
		{
			yield break;
		}
		XYZ center = GetBoundingBoxCenter(bbox);
		if (center != null)
		{
			yield return center;
		}
	}

	private static Transform TryBuildView3DSectionBoxTransform(View3D view3D, XYZ centerWorld)
	{
		if (view3D == null || centerWorld == null)
		{
			return null;
		}
		ViewOrientation3D orientation = view3D.GetOrientation();
		if (orientation == null)
		{
			return null;
		}
		XYZ forward = orientation.ForwardDirection;
		XYZ up = orientation.UpDirection;
		if (forward == null || forward.GetLength() < 1E-09)
		{
			return null;
		}
		forward = forward.Normalize();
		if (up == null || up.GetLength() < 1E-09)
		{
			up = XYZ.BasisZ;
		}
		up = up.Normalize();
		XYZ right = forward.CrossProduct(up);
		if (right.GetLength() < 1E-09)
		{
			up = (Math.Abs(forward.DotProduct(XYZ.BasisZ)) > 0.99) ? XYZ.BasisX : XYZ.BasisZ;
			right = forward.CrossProduct(up).Normalize();
			up = right.CrossProduct(forward).Normalize();
		}
		else
		{
			right = right.Normalize();
			up = right.CrossProduct(forward).Normalize();
		}
		Transform transform = Transform.Identity;
		transform.Origin = centerWorld;
		transform.BasisX = right;
		transform.BasisY = up;
		transform.BasisZ = forward;
		return transform;
	}

	private static double GetBoundingBoxMaxSpan(BoundingBoxXYZ bbox)
	{
		if (bbox == null)
		{
			return 1.0;
		}
		double num = bbox.Max.X - bbox.Min.X;
		double num2 = bbox.Max.Y - bbox.Min.Y;
		double num3 = bbox.Max.Z - bbox.Min.Z;
		return Math.Max(num, Math.Max(num2, num3));
	}

	private static List<XYZ> Collect3DViewSectionBoxPoints(Document doc, AssemblyInstance assembly, View3D view3D, bool includeTagExtents)
	{
		return CollectSpoolViewExtentPoints(doc, assembly, (View)(object)view3D, includeTagExtents, includeDimensionExtents: false);
	}

	private static void TrySetView3DSectionBox(View3D view3D, BoundingBoxXYZ sectionBox)
	{
		if (view3D == null || sectionBox == null)
		{
			return;
		}
		try
		{
			view3D.IsSectionBoxActive = true;
			view3D.SetSectionBox(sectionBox);
			return;
		}
		catch
		{
		}
		ElementId templateId = view3D.ViewTemplateId;
		if (templateId == ElementId.InvalidElementId)
		{
			return;
		}
		try
		{
			view3D.ViewTemplateId = ElementId.InvalidElementId;
			view3D.IsSectionBoxActive = true;
			view3D.SetSectionBox(sectionBox);
			view3D.ViewTemplateId = templateId;
		}
		catch
		{
			try
			{
				view3D.ViewTemplateId = templateId;
			}
			catch
			{
			}
		}
	}

	private static void FitSpoolAssembly3DViewSectionBoxToContent(Document doc, AssemblyInstance assembly, View3D view3D, bool includeTagExtents, bool includeDimensionExtents = false)
	{
		if (doc == null || assembly == null || view3D == null)
		{
			return;
		}
		try
		{
			RegenTracked(doc);
		}
		catch
		{
		}
		List<XYZ> list = CollectSpoolViewExtentPoints(doc, assembly, (View)(object)view3D, includeTagExtents, includeDimensionExtents);
		if (list.Count == 0)
		{
			return;
		}
		double marginFeet = ConvertSheetOffsetToModelDistance((View)(object)view3D, includeDimensionExtents ? 0.25 : (1.0 / 24.0));
		Transform sectionTransform = null;
		try
		{
			BoundingBoxXYZ existingSectionBox = view3D.GetSectionBox();
			if (existingSectionBox?.Transform != null)
			{
				sectionTransform = existingSectionBox.Transform;
			}
		}
		catch
		{
		}
		if (sectionTransform == null)
		{
			XYZ centerWorld = new XYZ(list.Average((XYZ p) => p.X), list.Average((XYZ p) => p.Y), list.Average((XYZ p) => p.Z));
			sectionTransform = TryBuildView3DSectionBoxTransform(view3D, centerWorld);
		}
		if (sectionTransform == null)
		{
			return;
		}
		if (!TryComputeLocalBounds(list, sectionTransform.Inverse, out var localMin, out var localMax))
		{
			return;
		}
		double minDepth = Math.Max(1.0 / 12.0, marginFeet);
		if (localMax.Z - localMin.Z < minDepth)
		{
			double midZ = (localMin.Z + localMax.Z) * 0.5;
			localMin = new XYZ(localMin.X, localMin.Y, midZ - minDepth * 0.5);
			localMax = new XYZ(localMax.X, localMax.Y, midZ + minDepth * 0.5);
		}
		BoundingBoxXYZ sectionBox = new BoundingBoxXYZ
		{
			Transform = sectionTransform,
			Min = new XYZ(localMin.X - marginFeet, localMin.Y - marginFeet, localMin.Z - marginFeet),
			Max = new XYZ(localMax.X + marginFeet, localMax.Y + marginFeet, localMax.Z + marginFeet)
		};
		TrySetView3DSectionBox(view3D, sectionBox);
	}

	private static void FitSpoolAssemblyDetailViewCropToContent(Document doc, AssemblyInstance assembly, View view, bool includeTagExtents, bool includeDimensionExtents = false, bool includeDimensionLabelExtents = false)
	{
		if (doc == null || assembly == null || view == null || view is View3D)
		{
			return;
		}
		EnsureEditableSpoolViewCrop(view);
		bool cropBoxActive;
		try
		{
			cropBoxActive = view.CropBoxActive;
		}
		catch
		{
			return;
		}
		if (!cropBoxActive)
		{
			return;
		}
		BoundingBoxXYZ cropBox = view.CropBox;
		if (((cropBox != null) ? cropBox.Transform : null) == null)
		{
			return;
		}
		List<XYZ> list = CollectSpoolViewExtentPoints(doc, assembly, view, includeTagExtents, includeDimensionExtents, includeDimensionLabelExtents);
		if (list.Count == 0)
		{
			return;
		}
		ElementId viewTemplateId = view.ViewTemplateId;
		if (viewTemplateId != ElementId.InvalidElementId)
		{
			TryAllowCropOutsideTemplateControl(view);
		}
		if (TryApplyDetailViewCropBox(view, cropBox, list) || viewTemplateId == ElementId.InvalidElementId)
		{
			return;
		}
		try
		{
			view.ViewTemplateId = ElementId.InvalidElementId;
			BoundingBoxXYZ cropBox2 = view.CropBox;
			if (cropBox2 != null)
			{
				TryApplyDetailViewCropBox(view, cropBox2, list);
			}
			view.ViewTemplateId = viewTemplateId;
		}
		catch
		{
		}
	}

	private static void TryExpandViewAnnotationCropForDimensions(View view)
	{
		if (view == null || view is View3D)
		{
			return;
		}
		try
		{
			Parameter annotationCropActive = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
			if (annotationCropActive == null)
			{
				annotationCropActive = view.LookupParameter("Annotation Crop");
			}
			if (annotationCropActive != null && !((APIObject)annotationCropActive).IsReadOnly)
			{
				annotationCropActive.Set(0);
			}
		}
		catch
		{
		}
		try
		{
			ViewCropRegionShapeManager cropManager = view.GetCropRegionShapeManager();
			if (cropManager == null)
			{
				return;
			}
			double offsetFeet = 2.0;
			cropManager.TopAnnotationCropOffset = offsetFeet;
			cropManager.BottomAnnotationCropOffset = offsetFeet;
			cropManager.LeftAnnotationCropOffset = offsetFeet;
			cropManager.RightAnnotationCropOffset = offsetFeet;
		}
		catch
		{
		}
	}

	private static void TryTightenSpoolViewAnnotationCrop(View view)
	{
		if (view == null || view is View3D)
		{
			return;
		}
		try
		{
			Parameter annotationCropActive = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
			if (annotationCropActive == null)
			{
				annotationCropActive = view.LookupParameter("Annotation Crop");
			}
			if (annotationCropActive != null && !((APIObject)annotationCropActive).IsReadOnly)
			{
				annotationCropActive.Set(0);
			}
		}
		catch
		{
		}
		try
		{
			ViewCropRegionShapeManager cropManager = view.GetCropRegionShapeManager();
			if (cropManager == null)
			{
				return;
			}
			double minOffset = 0.0;
			cropManager.TopAnnotationCropOffset = minOffset;
			cropManager.BottomAnnotationCropOffset = minOffset;
			cropManager.LeftAnnotationCropOffset = minOffset;
			cropManager.RightAnnotationCropOffset = minOffset;
		}
		catch
		{
		}
	}

	private static bool TryApplyDetailViewCropBox(View view, BoundingBoxXYZ cropBox, IList<XYZ> modelPoints)
	{
		if (view == null || ((cropBox != null) ? cropBox.Transform : null) == null || modelPoints == null || modelPoints.Count == 0)
		{
			return false;
		}
		try
		{
			Transform inverse = cropBox.Transform.Inverse;
			double minX = double.PositiveInfinity;
			double minY = double.PositiveInfinity;
			double maxX = double.NegativeInfinity;
			double maxY = double.NegativeInfinity;
			foreach (XYZ modelPoint in modelPoints)
			{
				XYZ local = inverse.OfPoint(modelPoint);
				minX = Math.Min(minX, local.X);
				minY = Math.Min(minY, local.Y);
				maxX = Math.Max(maxX, local.X);
				maxY = Math.Max(maxY, local.Y);
			}
			if (minX >= maxX || minY >= maxY)
			{
				return false;
			}
			double marginFeet = ConvertSheetOffsetToModelDistance(view, 1.0 / 24.0);
			Transform transform = cropBox.Transform;
			view.CropBox = new BoundingBoxXYZ
			{
				Transform = transform,
				Min = new XYZ(minX - marginFeet, minY - marginFeet, cropBox.Min.Z),
				Max = new XYZ(maxX + marginFeet, maxY + marginFeet, cropBox.Max.Z)
			};
			view.CropBoxVisible = false;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryComputeLocalBounds(IList<XYZ> modelPoints, Transform toLocal, out XYZ localMin, out XYZ localMax)
	{
		localMin = null;
		localMax = null;
		if (modelPoints == null || modelPoints.Count == 0 || toLocal == null)
		{
			return false;
		}
		double minX = double.PositiveInfinity;
		double minY = double.PositiveInfinity;
		double minZ = double.PositiveInfinity;
		double maxX = double.NegativeInfinity;
		double maxY = double.NegativeInfinity;
		double maxZ = double.NegativeInfinity;
		foreach (XYZ modelPoint in modelPoints)
		{
			if (modelPoint == null)
			{
				continue;
			}
			XYZ val = toLocal.OfPoint(modelPoint);
			minX = Math.Min(minX, val.X);
			minY = Math.Min(minY, val.Y);
			minZ = Math.Min(minZ, val.Z);
			maxX = Math.Max(maxX, val.X);
			maxY = Math.Max(maxY, val.Y);
			maxZ = Math.Max(maxZ, val.Z);
		}
		if (minX >= maxX || minY >= maxY || minZ >= maxZ)
		{
			return false;
		}
		localMin = new XYZ(minX, minY, minZ);
		localMax = new XYZ(maxX, maxY, maxZ);
		return true;
	}

	private static IEnumerable<XYZ> CollectDetailViewCropPoints(Document doc, AssemblyInstance assembly, View view, bool includeTagExtents)
	{
		foreach (ElementId memberId in assembly.GetMemberIds())
		{
			Element element = doc.GetElement(memberId);
			if (element == null)
			{
				continue;
			}
			BoundingBoxXYZ bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
			if (bbox == null)
			{
				continue;
			}
			foreach (XYZ corner in GetBoundingBoxCorners(bbox))
			{
				yield return corner;
			}
		}
		if (!includeTagExtents)
		{
			yield break;
		}
		foreach (XYZ tagHead in CollectTagHeadPositions(doc, view))
		{
			yield return tagHead;
		}
	}

	private static bool IsElementHiddenInView(Element element, View view)
	{
		if (element == null || view == null)
		{
			return false;
		}
		try
		{
			return element.IsHidden(view);
		}
		catch
		{
			return false;
		}
	}

	private static XYZ GetBoundingBoxCenter(BoundingBoxXYZ bbox)
	{
		if (bbox == null)
		{
			return null;
		}
		List<XYZ> list = GetBoundingBoxCorners(bbox).ToList();
		if (list.Count == 0)
		{
			return null;
		}
		return new XYZ(list.Average((XYZ x) => x.X), list.Average((XYZ x) => x.Y), list.Average((XYZ x) => x.Z));
	}

	// View filtering can drop olet takeoffs as crop outliers even though they belong on the run. Without them,
	// hasOletsOnRun stays false and the olet horizontal layout never runs.
	private static List<FabricationPart> EnsureOletPartsForAutoDim(List<FabricationPart> allParts, List<FabricationPart> filtered)
	{
		if (allParts == null || filtered == null || filtered.Count == 0)
		{
			return filtered;
		}
		HashSet<ElementId> keptIds = new HashSet<ElementId>(from p in filtered
			where p != null
			select ((Element)p).Id);
		bool expanded;
		do
		{
			expanded = false;
			foreach (FabricationPart part in allParts)
			{
				if (part == null || !IsOletPart(part) || keptIds.Contains(((Element)part).Id))
				{
					continue;
				}
				foreach (Connector connector in ListConnectors(part))
				{
					if (((connector != null) ? connector.Origin : null) == null)
					{
						continue;
					}
					FabricationPart mate = FindMateAtConnector(part, connector, allParts);
					if (mate != null && keptIds.Contains(((Element)mate).Id))
					{
						keptIds.Add(((Element)part).Id);
						expanded = true;
						break;
					}
				}
			}
		}
		while (expanded);
		return allParts.Where((FabricationPart p) => p != null && keptIds.Contains(((Element)p).Id)).ToList();
	}

	private static List<FabricationPart> ExpandFilteredPartsWithFlangeConnections(List<FabricationPart> allParts, List<FabricationPart> filtered, Document doc)
	{
		if (allParts == null || filtered == null || allParts.Count == 0 || filtered.Count == 0)
		{
			return filtered;
		}
		HashSet<ElementId> keptIds = new HashSet<ElementId>(from p in filtered
			where p != null
			select ((Element)p).Id);
		bool expanded;
		do
		{
			expanded = false;
			foreach (FabricationPart part in allParts)
			{
				if (part == null || keptIds.Contains(((Element)part).Id))
				{
					continue;
				}
				if (!FabricationPartClassification.IsFlangePart(part, doc) && !IsFittingToFlangePassThroughPart(part, allParts))
				{
					continue;
				}
				foreach (Connector connector in ListConnectors(part))
				{
					if (((connector != null) ? connector.Origin : null) == null)
					{
						continue;
					}
					FabricationPart mate = FindMateAtConnector(part, connector, allParts);
					if (mate != null && keptIds.Contains(((Element)mate).Id))
					{
						keptIds.Add(((Element)part).Id);
						expanded = true;
						break;
					}
				}
			}
		}
		while (expanded);
		List<FabricationPart> expandedList = allParts.Where((FabricationPart p) => p != null && keptIds.Contains(((Element)p).Id)).ToList();
		return (expandedList.Count > 0) ? expandedList : filtered;
	}

	private static List<FabricationPart> FilterLocalFabricationPartsForView(Document doc, View view, List<FabricationPart> parts)
	{
		if (parts == null || parts.Count <= 1)
		{
			return parts;
		}
		List<(Element element, XYZ center)> members = new List<(Element, XYZ)>();
		foreach (FabricationPart part in parts)
		{
			if (part == null)
			{
				continue;
			}
			XYZ center = TryGetElementViewCenter((Element)(object)part, view) ?? GetFabricationCenterPoint(part);
			if (center != null)
			{
				members.Add(((Element)(object)part, center));
			}
		}
		if (members.Count <= 1)
		{
			return parts;
		}
		HashSet<ElementId> keptIds = new HashSet<ElementId>(from e in FilterDistantAssemblyCropOutliers(members)
			select e.Id);
		bool expanded;
		do
		{
			expanded = false;
			foreach (FabricationPart part in parts)
			{
				if (part == null || keptIds.Contains(((Element)part).Id))
				{
					continue;
				}
				foreach (Connector connector in ListConnectors(part))
				{
					if (((connector != null) ? connector.Origin : null) == null)
					{
						continue;
					}
					FabricationPart mate = FindMateAtConnector(part, connector, parts);
					if (mate != null && keptIds.Contains(((Element)mate).Id))
					{
						keptIds.Add(((Element)part).Id);
						expanded = true;
						break;
					}
				}
			}
		}
		while (expanded);
		List<FabricationPart> filtered = parts.Where((FabricationPart p) => p != null && keptIds.Contains(((Element)p).Id)).ToList();
		return (filtered.Count > 0) ? filtered : parts;
	}

	private static IEnumerable<Element> FilterDistantAssemblyCropOutliers(List<(Element element, XYZ center)> members)
	{
		if (members == null || members.Count == 0)
		{
			yield break;
		}
		if (members.Count == 1)
		{
			yield return members[0].element;
			yield break;
		}
		List<Element> kept = new List<Element>();
		foreach ((Element element, XYZ center) candidate in members)
		{
			List<(Element element, XYZ center)> others = members.Where((m) => m.element.Id != candidate.element.Id).ToList();
			if (others.Count == 0)
			{
				kept.Add(candidate.element);
				continue;
			}
			XYZ val = new XYZ(others.Average((m) => m.center.X), others.Average((m) => m.center.Y), others.Average((m) => m.center.Z));
			double num = 1.0 / 12.0;
			for (int i = 0; i < others.Count; i++)
			{
				for (int j = i + 1; j < others.Count; j++)
				{
					num = Math.Max(num, others[i].center.DistanceTo(others[j].center));
				}
			}
			if (candidate.center.DistanceTo(val) <= num * 1.35 + 0.5)
			{
				kept.Add(candidate.element);
			}
		}
		if (kept.Count == 0)
		{
			foreach ((Element element, XYZ _) in members)
			{
				yield return element;
			}
			yield break;
		}
		foreach (Element item in kept)
		{
			yield return item;
		}
	}

	private static IEnumerable<XYZ> CollectDimensionLabelCropPoints(Document doc, View view)
	{
		if (doc == null || view == null)
		{
			yield break;
		}
		List<Dimension> list;
		try
		{
			list = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfCategory(BuiltInCategory.OST_Dimensions).WhereElementIsNotElementType()).Cast<Dimension>().ToList();
		}
		catch
		{
			yield break;
		}
		foreach (Dimension item in list)
		{
			if (item == null)
			{
				continue;
			}
			TryUnhideSpoolDimensionInView(view, item);
			XYZ textPosition = null;
			try
			{
				textPosition = item.TextPosition;
			}
			catch
			{
			}
			if (textPosition != null)
			{
				yield return textPosition;
			}
		}
	}

	private static IEnumerable<XYZ> CollectDimensionCropPoints(Document doc, View view)
	{
		if (doc == null || view == null)
		{
			yield break;
		}
		List<Dimension> list;
		try
		{
			list = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfCategory(BuiltInCategory.OST_Dimensions).WhereElementIsNotElementType()).Cast<Dimension>().ToList();
		}
		catch
		{
			yield break;
		}
		foreach (Dimension item in list)
		{
			foreach (XYZ point in CollectSingleDimensionCropPoints(view, item))
			{
				yield return point;
			}
		}
	}

	private static IEnumerable<XYZ> CollectSingleDimensionCropPoints(View view, Dimension dimension)
	{
		if (dimension == null)
		{
			yield break;
		}
		TryUnhideSpoolDimensionInView(view, dimension);
		Curve curve = null;
		try
		{
			curve = dimension.Curve;
		}
		catch
		{
		}
		if (curve != null && curve.IsBound)
		{
			XYZ end0 = null;
			XYZ end1 = null;
			try
			{
				end0 = curve.GetEndPoint(0);
				end1 = curve.GetEndPoint(1);
			}
			catch
			{
			}
			if (end0 != null)
			{
				yield return end0;
			}
			if (end1 != null)
			{
				yield return end1;
			}
		}
		XYZ textPosition = null;
		try
		{
			textPosition = dimension.TextPosition;
		}
		catch
		{
		}
		if (textPosition != null)
		{
			yield return textPosition;
		}
		XYZ origin = null;
		try
		{
			origin = dimension.Origin;
		}
		catch
		{
		}
		if (origin != null)
		{
			yield return origin;
		}
	}

	private static IEnumerable<XYZ> CollectTagHeadPositions(Document doc, View view)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		List<XYZ> list = new List<XYZ>();
		List<IndependentTag> list2;
		try
		{
			list2 = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(IndependentTag))).Cast<IndependentTag>().ToList();
		}
		catch
		{
			return list;
		}
		foreach (IndependentTag item in list2)
		{
			if (item == null)
			{
				continue;
			}
			try
			{
				XYZ tagHeadPosition = item.TagHeadPosition;
				if (tagHeadPosition != null)
				{
					list.Add(tagHeadPosition);
				}
			}
			catch
			{
			}
		}
		return list;
	}

	private static int GetSpoolSheetViewScale(SpoolingManagerKind kind, SpoolingManagerSettings settings)
	{
		if (kind == SpoolingManagerKind.AutoDimensionLab)
		{
			return 24;
		}
		double num = settings?.GetSpoolSheetScaleInchesPerFoot(kind) ?? 0.5;
		if (num <= 0.0)
		{
			num = (kind.IsMmcStyle() ? 1.5 : 0.5);
		}
		return Math.Max(1, (int)Math.Round(12.0 / num));
	}

	private static Viewport PlaceViewOnSheet(Document doc, ViewSheet sheet, View view, string placement)
	{
		try
		{
			RegenTracked(doc);
		}
		catch
		{
		}
		XYZ sheetPlacementPoint = GetSheetPlacementPoint(doc, sheet, placement);
		Viewport val = Viewport.Create(doc, ((Element)sheet).Id, ((Element)view).Id, sheetPlacementPoint);
		if (val == null)
		{
			return null;
		}
		val.SetBoxCenter(sheetPlacementPoint);
		return val;
	}

	internal static void RecenterViewport(Document doc, ViewSheet sheet, Viewport viewport, string placement)
	{
		if (viewport == null)
		{
			return;
		}
		RegenTracked(doc);
		XYZ sheetPlacementPoint = GetSheetPlacementPoint(doc, sheet, placement);
		try
		{
			viewport.SetBoxCenter(sheetPlacementPoint);
			RegenTracked(doc);
			viewport.SetBoxCenter(sheetPlacementPoint);
		}
		catch
		{
		}
	}

	// Keeps a placed viewport fully inside the sheet. Preserves the configured view scale
	// when the view already fits; only shrinks the scale when the view is larger than the
	// sheet, then clamps the viewport position so nothing runs off the sheet edge.
	private static void FitSpoolViewportOnSheet(Document doc, ViewSheet sheet, View view, Viewport viewport, string placement)
	{
		if (doc == null || sheet == null || view == null || viewport == null)
		{
			return;
		}
		try
		{
			if (!TryGetSheetDrawableBounds(doc, sheet, out var minX, out var maxX, out var minY, out var maxY))
			{
				return;
			}
			double marginX = Math.Max((maxX - minX) * 0.04, 1.0 / 48.0);
			double marginY = Math.Max((maxY - minY) * 0.04, 1.0 / 48.0);
			double maxWidth = maxX - minX - 2.0 * marginX;
			double maxHeight = maxY - minY - 2.0 * marginY;
			if (maxWidth <= 0.0 || maxHeight <= 0.0)
			{
				return;
			}
			int maxIterations = _batchSheetGeneration ? 3 : 8;
			RegenForViewportFit(doc);
			for (int i = 0; i < maxIterations; i++)
			{
				Outline boxOutline = viewport.GetBoxOutline();
				if (boxOutline == null || boxOutline.IsEmpty)
				{
					break;
				}
				double width = boxOutline.MaximumPoint.X - boxOutline.MinimumPoint.X;
				double height = boxOutline.MaximumPoint.Y - boxOutline.MinimumPoint.Y;
				TryAppendAutoDimDiagnosticLog("view-scale", view?.Name ?? "?", "FitSpoolViewportOnSheet iter=" + i + " scale=" + view.Scale + " boxW=" + width.ToString("F3") + " boxH=" + height.ToString("F3") + " maxW=" + maxWidth.ToString("F3") + " maxH=" + maxHeight.ToString("F3"), 0, 0);
				if (width <= maxWidth && height <= maxHeight)
				{
					break;
				}
				int currentScale = view.Scale;
				if (currentScale <= 0)
				{
					break;
				}
				double ratio = Math.Max(width / maxWidth, height / maxHeight);
				int newScale = SnapUpToStandardViewScale((int)Math.Ceiling((double)currentScale * ratio));
				if (newScale <= currentScale)
				{
					newScale = currentScale + 1;
				}
				TryAppendAutoDimDiagnosticLog("view-scale", view?.Name ?? "?", "FitSpoolViewportOnSheet rescale " + currentScale + " -> " + newScale, 0, 0);
				if (!TrySetSpoolViewScale(view, newScale))
				{
					break;
				}
				RegenForViewportFit(doc);
			}
			viewport.SetBoxCenter(GetSheetPlacementPoint(doc, sheet, placement));
			RegenForViewportFit(doc);
			ClampViewportWithinBounds(viewport, minX, maxX, minY, maxY);
		}
		catch
		{
		}
	}

	private static bool TryGetSheetDrawableBounds(Document doc, ViewSheet sheet, out double minX, out double maxX, out double minY, out double maxY)
	{
		minX = (maxX = (minY = (maxY = 0.0)));
		BoundingBoxXYZ titleBlockBounds = GetTitleBlockBounds(doc, sheet);
		if (titleBlockBounds != null)
		{
			minX = titleBlockBounds.Min.X;
			maxX = titleBlockBounds.Max.X;
			minY = titleBlockBounds.Min.Y;
			maxY = titleBlockBounds.Max.Y;
			return maxX > minX && maxY > minY;
		}
		try
		{
			BoundingBoxUV outline = ((View)sheet).Outline;
			minX = outline.Min.U;
			maxX = outline.Max.U;
			minY = outline.Min.V;
			maxY = outline.Max.V;
			return maxX > minX && maxY > minY;
		}
		catch
		{
			return false;
		}
	}

	private static void ClampViewportWithinBounds(Viewport viewport, double minX, double maxX, double minY, double maxY)
	{
		try
		{
			Outline boxOutline = viewport.GetBoxOutline();
			if (boxOutline == null || boxOutline.IsEmpty)
			{
				return;
			}
			double width = boxOutline.MaximumPoint.X - boxOutline.MinimumPoint.X;
			double height = boxOutline.MaximumPoint.Y - boxOutline.MinimumPoint.Y;
			XYZ center = viewport.GetBoxCenter();
			double newX = center.X;
			double newY = center.Y;
			double halfWidth = width * 0.5;
			double halfHeight = height * 0.5;
			if (width <= maxX - minX)
			{
				if (center.X - halfWidth < minX)
				{
					newX = minX + halfWidth;
				}
				else if (center.X + halfWidth > maxX)
				{
					newX = maxX - halfWidth;
				}
			}
			else
			{
				newX = (minX + maxX) * 0.5;
			}
			if (height <= maxY - minY)
			{
				if (center.Y - halfHeight < minY)
				{
					newY = minY + halfHeight;
				}
				else if (center.Y + halfHeight > maxY)
				{
					newY = maxY - halfHeight;
				}
			}
			else
			{
				newY = (minY + maxY) * 0.5;
			}
			if (Math.Abs(newX - center.X) > 1E-09 || Math.Abs(newY - center.Y) > 1E-09)
			{
				viewport.SetBoxCenter(new XYZ(newX, newY, 0.0));
			}
		}
		catch
		{
		}
	}

	private static int SnapUpToStandardViewScale(int requiredScale)
	{
		int[] array = new int[12]
		{
			12, 16, 24, 32, 48, 64, 96, 128, 192, 256,
			384, 512
		};
		foreach (int num in array)
		{
			if (num >= requiredScale)
			{
				return num;
			}
		}
		return requiredScale;
	}

	// Sets the view scale even when a view template controls it, so a large spool can always be
	// shrunk to fit the sheet instead of being left oversized (which reads as "cut off").
	private static bool TrySetSpoolViewScale(View view, int newScale)
	{
		if (view == null || newScale <= 0)
		{
			return false;
		}
		try
		{
			view.Scale = newScale;
			if (view.Scale == newScale)
			{
				return true;
			}
		}
		catch
		{
		}
		try
		{
			TryAllowScaleOutsideTemplateControl(view);
			view.Scale = newScale;
			if (view.Scale == newScale)
			{
				return true;
			}
		}
		catch
		{
		}
		try
		{
			ElementId templateId = view.ViewTemplateId;
			if (templateId != ElementId.InvalidElementId)
			{
				view.ViewTemplateId = ElementId.InvalidElementId;
				view.Scale = newScale;
				view.ViewTemplateId = templateId;
				TryAllowScaleOutsideTemplateControl(view);
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private static void TryAllowScaleOutsideTemplateControl(View view)
	{
		if (view == null)
		{
			return;
		}
		try
		{
			ICollection<ElementId> nonControlled = view.GetNonControlledTemplateParameterIds();
			List<ElementId> list = ((nonControlled == null) ? new List<ElementId>() : nonControlled.ToList());
			bool changed = false;
			foreach (Parameter parameter in ((Element)view).Parameters)
			{
				if (parameter == null || ((APIObject)parameter).IsReadOnly)
				{
					continue;
				}
				Definition definition = parameter.Definition;
				string name = ((definition != null) ? definition.Name : null) ?? string.Empty;
				if ((name.Equals("View Scale", StringComparison.OrdinalIgnoreCase) || name.Equals("Scale", StringComparison.OrdinalIgnoreCase)) && !list.Any((ElementId id) => id == parameter.Id))
				{
					list.Add(parameter.Id);
					changed = true;
				}
			}
			if (changed)
			{
				view.SetNonControlledTemplateParameterIds((ICollection<ElementId>)list);
			}
		}
		catch
		{
		}
	}

	internal static bool ApplyViewCropRegionRotation(Document doc, View view, string rotationSetting)
	{
		if (doc == null || view == null || view is View3D)
		{
			return false;
		}
		double num = ParseViewCropRotationRadians(rotationSetting);
		double viewCropRegionRotationRadians = GetViewCropRegionRotationRadians(view);
		double num2 = NormalizeRotationRadians(num - viewCropRegionRotationRadians);
		if (Math.Abs(num2) < 1E-06)
		{
			try
			{
				view.CropBoxVisible = false;
			}
			catch
			{
			}
			return false;
		}
		return TryRotateViewCropRegionElement(doc, view, num2);
	}

	private static double GetViewCropRegionRotationRadians(View view)
	{
		BoundingBoxXYZ val = ((view != null) ? view.CropBox : null);
		if (((val != null) ? val.Transform : null) == null)
		{
			return 0.0;
		}
		XYZ viewDirection = view.ViewDirection;
		if (viewDirection == null || viewDirection.GetLength() < 1E-09)
		{
			return 0.0;
		}
		viewDirection = viewDirection.Normalize();
		XYZ val2 = ProjectOntoViewPlane(view.RightDirection, viewDirection);
		XYZ val3 = ProjectOntoViewPlane(val.Transform.BasisX, viewDirection);
		if (val2.GetLength() < 1E-09 || val3.GetLength() < 1E-09)
		{
			return 0.0;
		}
		val2 = val2.Normalize();
		val3 = val3.Normalize();
		double x = Math.Max(-1.0, Math.Min(1.0, val2.DotProduct(val3)));
		return Math.Atan2(val2.CrossProduct(val3).DotProduct(viewDirection), x);
	}

	private static XYZ ProjectOntoViewPlane(XYZ vector, XYZ viewDirection)
	{
		if (vector == null)
		{
			return XYZ.Zero;
		}
		return vector - viewDirection * vector.DotProduct(viewDirection);
	}

	private static double NormalizeRotationRadians(double angleRadians)
	{
		while (angleRadians > Math.PI)
		{
			angleRadians -= Math.PI * 2.0;
		}
		while (angleRadians <= -Math.PI)
		{
			angleRadians += Math.PI * 2.0;
		}
		return angleRadians;
	}

	private static double ParseViewCropRotationRadians(string rotationSetting)
	{
		switch ((rotationSetting ?? string.Empty).Trim().ToUpperInvariant())
		{
		case "90° CW":
		case "90 CW":
			return Math.PI / 2.0;
		case "90° CCW":
		case "90 CCW":
			return -Math.PI / 2.0;
		case "180°":
		case "180":
			return Math.PI;
		default:
			return 0.0;
		}
	}

	private static bool HasViewCropRotation(string rotationSetting)
	{
		return Math.Abs(ParseViewCropRotationRadians(rotationSetting)) > 1E-06;
	}

	private static bool TryRotateViewCropRegionElement(Document doc, View view, double angleRadians)
	{
		if (!view.CropBoxActive)
		{
			return false;
		}
		bool cropBoxVisible = false;
		try
		{
			cropBoxVisible = view.CropBoxVisible;
		}
		catch
		{
		}
		try
		{
			view.CropBoxVisible = true;
			RegenTracked(doc);
		}
		catch
		{
			return false;
		}
		Element val = FindViewCropRegionElement(doc, view);
		if (val == null)
		{
			try
			{
				view.CropBoxVisible = cropBoxVisible;
			}
			catch
			{
			}
			return false;
		}
		Line viewCropRegionRotationAxis = GetViewCropRegionRotationAxis(view);
		if ((GeometryObject)(object)viewCropRegionRotationAxis == (GeometryObject)null)
		{
			try
			{
				view.CropBoxVisible = cropBoxVisible;
			}
			catch
			{
			}
			return false;
		}
		try
		{
			ElementTransformUtils.RotateElement(doc, val.Id, viewCropRegionRotationAxis, angleRadians);
			RegenTracked(doc);
			view.CropBoxVisible = false;
			return true;
		}
		catch
		{
			try
			{
				view.CropBoxVisible = cropBoxVisible;
			}
			catch
			{
			}
			return false;
		}
	}

	private static Element FindViewCropRegionElement(Document doc, View view)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Expected O, but got Unknown
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Expected O, but got Unknown
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Expected O, but got Unknown
		try
		{
			ElementCategoryFilter val = new ElementCategoryFilter((BuiltInCategory)(-2000278));
			ICollection<ElementId> dependentElements = ((Element)view).GetDependentElements((ElementFilter)(object)val);
			if (dependentElements != null && dependentElements.Count > 0)
			{
				Element element = doc.GetElement(dependentElements.First());
				if (element != null)
				{
					return element;
				}
			}
		}
		catch
		{
		}
		long value = ((Element)view).Id.Value;
		for (int i = -3; i <= 3; i++)
		{
			if (i == 0)
			{
				continue;
			}
			Element element2 = doc.GetElement(new ElementId(value + i));
			object obj2;
			if (element2 == null)
			{
				obj2 = null;
			}
			else
			{
				Category category = element2.Category;
				obj2 = ((category != null) ? category.Id : null);
			}
			if (!((ElementId)obj2 != new ElementId((BuiltInCategory)(-2000278))))
			{
				Parameter val2 = element2.get_Parameter((BuiltInParameter)(-1005112));
				if (val2 != null && string.Equals(val2.AsString(), ((Element)view).Name, StringComparison.Ordinal))
				{
					return element2;
				}
			}
		}
		try
		{
			Element val3 = ((IEnumerable<Element>)new FilteredElementCollector(doc, ((Element)view).Id).OfCategory((BuiltInCategory)(-2000278)).WhereElementIsNotElementType()).FirstOrDefault();
			if (val3 != null)
			{
				return val3;
			}
		}
		catch
		{
		}
		return FindViewCropRegionElementByVisibilityTrick(doc, view);
	}

	private static Element FindViewCropRegionElementByVisibilityTrick(Document doc, View view)
	{
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			_ = view.CropBoxVisible;
		}
		catch
		{
		}
		try
		{
			if (!view.CropBoxActive)
			{
				return null;
			}
			view.CropBoxVisible = false;
			RegenTracked(doc);
			ICollection<ElementId> collection = new FilteredElementCollector(doc, ((Element)view).Id).ToElementIds();
			view.CropBoxVisible = true;
			RegenTracked(doc);
			return ((IEnumerable<Element>)new FilteredElementCollector(doc, ((Element)view).Id).WhereElementIsNotElementType().Excluding(collection).OfCategory((BuiltInCategory)(-2000278))).FirstOrDefault();
		}
		catch
		{
			return null;
		}
		finally
		{
			try
			{
				view.CropBoxVisible = false;
			}
			catch
			{
			}
		}
	}

	private static Line GetViewCropRegionRotationAxis(View view)
	{
		BoundingBoxXYZ cropBox = view.CropBox;
		if (((cropBox != null) ? cropBox.Transform : null) == null)
		{
			return null;
		}
		XYZ val = cropBox.Transform.OfPoint((cropBox.Min + cropBox.Max) * 0.5);
		XYZ viewDirection = view.ViewDirection;
		if (viewDirection == null || viewDirection.GetLength() < 1E-09)
		{
			return null;
		}
		viewDirection = viewDirection.Normalize();
		return Line.CreateBound(val, val + viewDirection);
	}

	/// <summary>
	/// Apply title shorten/center/clearance to every viewport on the sheet.
	/// </summary>
	internal static void TryPositionAllViewportTitlesOnSheet(Document doc, ViewSheet sheet)
	{
		if (doc == null || sheet == null)
		{
			return;
		}

		foreach (ElementId viewportId in sheet.GetAllViewports())
		{
			Element element = doc.GetElement(viewportId);
			Viewport viewport = element as Viewport;
			if (viewport == null)
			{
				continue;
			}

			TryPositionViewportTitleBelow(viewport);
		}
	}

	/// <summary>
	/// Shorten the viewport title line to the view name, center it under the
	/// assembly (fallback: viewbox), and place the title so its top is 1/8"
	/// below the viewbox. Push farther only if tags/dims still collide.
	/// </summary>
	private static void TryPositionViewportTitleBelow(Viewport viewport)
	{
		if (viewport == null)
		{
			return;
		}

		try
		{
			if ((int)viewport.Rotation != 0)
			{
				return;
			}

			Document doc = viewport.Document;
			if (doc == null)
			{
				return;
			}

			Outline boxOutline = viewport.GetBoxOutline();
			if (boxOutline == null || boxOutline.IsEmpty)
			{
				return;
			}

			double boxMinX = boxOutline.MinimumPoint.X;
			double boxMinY = boxOutline.MinimumPoint.Y;
			double boxMaxX = boxOutline.MaximumPoint.X;
			double boxWidth = boxMaxX - boxMinX;

			View view = doc.GetElement(viewport.ViewId) as View;
			string viewName = ((Element)view)?.Name ?? string.Empty;

			double lineLength = EstimateViewportTitleLineLength(viewName);
			lineLength = Math.Min(lineLength, Math.Max(boxWidth * 0.45, lineLength));

			// Center under the VIEWBOX (not the assembly footprint).
			double viewboxCenterX = (boxMinX + boxMaxX) * 0.5;

			const double viewboxClearanceInches = 0.125; // 1/8" below viewbox
			double viewboxClearance = viewboxClearanceInches / 12.0;

			// Seed: line roughly centered; then correct using measured label outline
			// so the FULL title (bubble + name) is centered under the viewbox.
			double labelLeftX = viewboxCenterX - lineLength * 0.5;
			double seedLabelY = boxMinY - viewboxClearance;

			viewport.LabelLineLength = lineLength;
			viewport.LabelOffset = new XYZ(labelLeftX - boxMinX, seedLabelY - boxMinY, 0.0);

			try
			{
				doc.Regenerate();
			}
			catch
			{
			}

			Outline labelOutline = null;
			try
			{
				labelOutline = viewport.GetLabelOutline();
			}
			catch
			{
			}

			if (labelOutline != null && !labelOutline.IsEmpty)
			{
				double labelCenterX =
					(labelOutline.MinimumPoint.X + labelOutline.MaximumPoint.X) * 0.5;
				double labelTopY = labelOutline.MaximumPoint.Y;
				double targetTopY = boxMinY - viewboxClearance;

				double deltaX = labelCenterX - viewboxCenterX; // >0 means title too far right
				double deltaY = labelTopY - targetTopY; // >0 means title too high

				if (Math.Abs(deltaX) > 1e-9 || Math.Abs(deltaY) > 1e-9)
				{
					XYZ offset = viewport.LabelOffset;
					viewport.LabelOffset = new XYZ(
						offset.X - deltaX,
						offset.Y - deltaY,
						0.0);
				}
			}

			// Do NOT call EnsureViewportTitleClearance here. On elevations with wide
			// dims it shoved titles to the title block. GitHub rule stays: 1/8" under
			// the blue viewbox only.
		}
		catch
		{
		}
	}

	/// <summary>
	/// Push the viewport title down until its outline is at least
	/// <paramref name="clearanceInches"/> from the assembly, tags, and dimensions.
	/// </summary>
	private static void EnsureViewportTitleClearance(
		Document doc,
		Viewport viewport,
		View view,
		double clearanceInches)
	{
		if (doc == null || viewport == null || view == null)
		{
			return;
		}

		if (!viewport.HasViewportTransforms())
		{
			return;
		}

		const double tagBodyWidthInches = 0.55;
		const double tagBodyHeightInches = 0.22;
		double clearance = Math.Max(0.0, clearanceInches) / 12.0;
		double halfW = tagBodyWidthInches / 2.0 / 12.0;
		double halfH = tagBodyHeightInches / 2.0 / 12.0;

		if (!TryGetViewToSheetTransforms(
			viewport,
			view,
			out Transform modelToProjection,
			out Transform projectionToSheet))
		{
			return;
		}

		try
		{
			doc.Regenerate();
		}
		catch
		{
		}

		Outline labelOutline;
		try
		{
			labelOutline = viewport.GetLabelOutline();
		}
		catch
		{
			return;
		}

		if (labelOutline == null || labelOutline.IsEmpty)
		{
			return;
		}

		double labelMinX = labelOutline.MinimumPoint.X;
		double labelMaxX = labelOutline.MaximumPoint.X;
		double labelMaxY = labelOutline.MaximumPoint.Y;

		double minAllowedTitleTop = double.PositiveInfinity;

		if (TryGetAssemblySheetBounds(
			doc,
			viewport,
			view,
			out double asmMinX,
			out double asmMaxX,
			out double asmMinY,
			out _))
		{
			// Assembly shares horizontal space with the title → keep 1/8" under it.
			if (!(asmMaxX < labelMinX - clearance || asmMinX > labelMaxX + clearance))
			{
				minAllowedTitleTop = Math.Min(minAllowedTitleTop, asmMinY - clearance);
			}
		}

		foreach (IndependentTag tag in new FilteredElementCollector(doc, ((Element)view).Id)
			.OfClass(typeof(IndependentTag))
			.Cast<IndependentTag>())
		{
			if (tag == null || tag.IsOrphaned)
			{
				continue;
			}

			if (!TryProjectModelPointToSheet(
				modelToProjection,
				projectionToSheet,
				tag.TagHeadPosition,
				out XYZ sheetHead))
			{
				continue;
			}

			double tagMinX = sheetHead.X - halfW;
			double tagMaxX = sheetHead.X + halfW;
			double tagMinY = sheetHead.Y - halfH;

			if (tagMaxX < labelMinX - clearance || tagMinX > labelMaxX + clearance)
			{
				continue;
			}

			minAllowedTitleTop = Math.Min(minAllowedTitleTop, tagMinY - clearance);
		}

		foreach (Dimension dimension in new FilteredElementCollector(doc, ((Element)view).Id)
			.OfClass(typeof(Dimension))
			.Cast<Dimension>())
		{
			if (dimension == null)
			{
				continue;
			}

			BoundingBoxXYZ box = null;
			try
			{
				box = dimension.get_BoundingBox(view);
			}
			catch
			{
			}

			if (box == null)
			{
				continue;
			}

			if (!TryProjectModelPointToSheet(
				modelToProjection,
				projectionToSheet,
				(box.Min + box.Max) * 0.5,
				out XYZ sheetCenter))
			{
				continue;
			}

			double dimHalfW = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) * 0.15;
			dimHalfW = Math.Max(dimHalfW, 0.20 / 12.0);
			double dimHalfH = 0.12 / 12.0;
			double dimMinX = sheetCenter.X - dimHalfW;
			double dimMaxX = sheetCenter.X + dimHalfW;
			double dimMinY = sheetCenter.Y - dimHalfH;

			if (dimMaxX < labelMinX - clearance || dimMinX > labelMaxX + clearance)
			{
				continue;
			}

			minAllowedTitleTop = Math.Min(minAllowedTitleTop, dimMinY - clearance);
		}

		if (double.IsPositiveInfinity(minAllowedTitleTop) || labelMaxY <= minAllowedTitleTop + 1e-9)
		{
			return;
		}

		double pushDown = labelMaxY - minAllowedTitleTop;
		XYZ offset = viewport.LabelOffset;
		viewport.LabelOffset = new XYZ(offset.X, offset.Y - pushDown, 0.0);
	}

	private static bool TryGetViewToSheetTransforms(
		Viewport viewport,
		View view,
		out Transform modelToProjection,
		out Transform projectionToSheet)
	{
		modelToProjection = null;
		projectionToSheet = null;

		try
		{
			IList<TransformWithBoundary> transforms = view.GetModelToProjectionTransforms();
			if (transforms == null || transforms.Count == 0)
			{
				return false;
			}

			modelToProjection = transforms[0].GetModelToProjectionTransform();
			projectionToSheet = viewport.GetProjectionToSheetTransform();
			return modelToProjection != null && projectionToSheet != null;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryProjectModelPointToSheet(
		Transform modelToProjection,
		Transform projectionToSheet,
		XYZ modelPoint,
		out XYZ sheetPoint)
	{
		sheetPoint = null;
		try
		{
			sheetPoint = projectionToSheet.OfPoint(modelToProjection.OfPoint(modelPoint));
			return sheetPoint != null;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Sheet-space AABB of the assembly members visible in the viewport.
	/// </summary>
	private static bool TryGetAssemblySheetBounds(
		Document doc,
		Viewport viewport,
		View view,
		out double minX,
		out double maxX,
		out double minY,
		out double maxY)
	{
		minX = double.PositiveInfinity;
		maxX = double.NegativeInfinity;
		minY = double.PositiveInfinity;
		maxY = double.NegativeInfinity;

		if (doc == null || viewport == null || view == null)
		{
			return false;
		}

		if (!TryGetViewToSheetTransforms(
			viewport,
			view,
			out Transform modelToProjection,
			out Transform projectionToSheet))
		{
			return false;
		}

		IEnumerable<Element> members = null;
		try
		{
			ElementId assemblyId = view.AssociatedAssemblyInstanceId;
			if (assemblyId != null && assemblyId != ElementId.InvalidElementId)
			{
				AssemblyInstance assembly = doc.GetElement(assemblyId) as AssemblyInstance;
				if (assembly != null)
				{
					members = assembly.GetMemberIds()
						.Select(doc.GetElement)
						.Where(e => e != null);
				}
			}
		}
		catch
		{
		}

		if (members == null)
		{
			try
			{
				members = new FilteredElementCollector(doc, ((Element)view).Id)
					.OfCategory(BuiltInCategory.OST_FabricationPipework)
					.WhereElementIsNotElementType()
					.ToElements();
			}
			catch
			{
				return false;
			}
		}

		foreach (Element element in members)
		{
			BoundingBoxXYZ box = null;
			try
			{
				box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
			}
			catch
			{
			}

			if (box == null)
			{
				continue;
			}

			foreach (XYZ corner in EnumerateBoxCorners(box))
			{
				if (!TryProjectModelPointToSheet(
					modelToProjection,
					projectionToSheet,
					corner,
					out XYZ sheetPoint))
				{
					continue;
				}

				minX = Math.Min(minX, sheetPoint.X);
				maxX = Math.Max(maxX, sheetPoint.X);
				minY = Math.Min(minY, sheetPoint.Y);
				maxY = Math.Max(maxY, sheetPoint.Y);
			}
		}

		return !double.IsPositiveInfinity(minX)
			&& !double.IsNegativeInfinity(maxX)
			&& !double.IsPositiveInfinity(minY)
			&& !double.IsNegativeInfinity(maxY);
	}

	private static IEnumerable<XYZ> EnumerateBoxCorners(BoundingBoxXYZ box)
	{
		XYZ min = box.Min;
		XYZ max = box.Max;
		yield return new XYZ(min.X, min.Y, min.Z);
		yield return new XYZ(max.X, min.Y, min.Z);
		yield return new XYZ(min.X, max.Y, min.Z);
		yield return new XYZ(max.X, max.Y, min.Z);
		yield return new XYZ(min.X, min.Y, max.Z);
		yield return new XYZ(max.X, min.Y, max.Z);
		yield return new XYZ(min.X, max.Y, max.Z);
		yield return new XYZ(max.X, max.Y, max.Z);
	}

	/// <summary>
	/// Approximate sheet-space length of the viewport title underline for a view name.
	/// </summary>
	private static double EstimateViewportTitleLineLength(string viewName)
	{
		string name = (viewName ?? string.Empty).Trim();
		if (name.Length == 0)
		{
			return 1.0 / 12.0; // 1"
		}

		const double inchesPerChar = 0.10;
		double inches = Math.Max(0.55, name.Length * inchesPerChar);
		inches += 0.08;
		return inches / 12.0;
	}

	private static XYZ GetSheetPlacementPoint(Document doc, ViewSheet sheet, string placement)
	{
		//IL_0374: Unknown result type (might be due to invalid IL or missing references)
		//IL_037a: Expected O, but got Unknown
		//IL_02dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e2: Expected O, but got Unknown
		//IL_0302: Unknown result type (might be due to invalid IL or missing references)
		//IL_0308: Expected O, but got Unknown
		//IL_02ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f5: Expected O, but got Unknown
		//IL_033b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0341: Expected O, but got Unknown
		//IL_0361: Unknown result type (might be due to invalid IL or missing references)
		//IL_0367: Expected O, but got Unknown
		//IL_034e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0354: Expected O, but got Unknown
		//IL_0315: Unknown result type (might be due to invalid IL or missing references)
		//IL_031b: Expected O, but got Unknown
		//IL_0328: Unknown result type (might be due to invalid IL or missing references)
		//IL_032e: Expected O, but got Unknown
		BoundingBoxXYZ titleBlockBounds = GetTitleBlockBounds(doc, sheet);
		double num;
		double num2;
		double num3;
		double num4;
		if (titleBlockBounds != null)
		{
			num = titleBlockBounds.Min.X;
			num2 = titleBlockBounds.Max.X;
			num3 = titleBlockBounds.Min.Y;
			num4 = titleBlockBounds.Max.Y;
		}
		else
		{
			BoundingBoxUV outline = ((View)sheet).Outline;
			num = outline.Min.U;
			num2 = outline.Max.U;
			num3 = outline.Min.V;
			num4 = outline.Max.V;
		}
		double num5 = num2 - num;
		double num6 = num4 - num3;
		double num7 = Math.Max(num5 * 0.04, 1.0 / 96.0);
		double num8 = Math.Max(num6 * 0.04, 1.0 / 96.0);
		double num9 = Math.Max(num6 * 0.1, 1.0 / 96.0);
		double num10 = num + num7;
		double num11 = num2 - num7;
		double num12 = num3 + num9;
		double num13 = num4 - num8;
		double num14 = num10 + (num11 - num10) / 6.0;
		double num15 = (num10 + num11) * 0.5;
		double num16 = num11 - (num11 - num10) / 6.0;
		double num17 = num12 + (num13 - num12) / 6.0;
		double num18 = (num12 + num13) * 0.5;
		double num19 = num13 - (num13 - num12) / 6.0;
		return (XYZ)((placement ?? string.Empty).Trim().ToLowerInvariant() switch
		{
			"top left" => (object)new XYZ(num14, num19, 0.0), 
			"top center" => (object)new XYZ(num15, num19, 0.0), 
			"top right" => (object)new XYZ(num16, num19, 0.0), 
			"middle left" => (object)new XYZ(num14, num18, 0.0), 
			"middle right" => (object)new XYZ(num16, num18, 0.0), 
			"bottom left" => (object)new XYZ(num14, num17, 0.0), 
			"bottom center" => (object)new XYZ(num15, num17, 0.0), 
			"bottom right" => (object)new XYZ(num16, num17, 0.0), 
			_ => (object)new XYZ(num15, num18, 0.0), 
		});
	}

	/// <summary>Title-block inset for schedule content corners (1/8").</summary>
	private const double ScheduleTitleBlockInsetFeet = 0.125 / 12.0;

	/// <summary>Vertical gap between stacked same-side schedules (1/16").</summary>
	private const double ScheduleStackGapFeet = 1.0 / 192.0;

	private static double GetScheduleLeftInsetFeet(SpoolingManagerSettings settings)
	{
		if (settings != null && settings.ScheduleInsetFromTitleBlockLeftInches.HasValue)
		{
			return settings.ScheduleInsetFromTitleBlockLeftInches.Value / 12.0;
		}
		return ScheduleTitleBlockInsetFeet;
	}

	private static double GetScheduleTopInsetFeet(SpoolingManagerSettings settings)
	{
		if (settings != null && settings.ScheduleInsetFromTitleBlockTopInches.HasValue)
		{
			return settings.ScheduleInsetFromTitleBlockTopInches.Value / 12.0;
		}
		return ScheduleTitleBlockInsetFeet;
	}

	/// <summary>
	/// Places one side's schedules: create all first (so Filter-by-Sheet sizes settle), then stack
	/// top-to-bottom with a 1/16" content gap. Top Right mirrors the left inset from the right edge.
	/// </summary>
	private static void PlaceScheduleColumnOnSheet(
		Document doc,
		ViewSheet sheet,
		List<ViewSchedule> schedules,
		bool alignTopRight,
		SpoolingManagerSettings spoolSettings)
	{
		if (doc == null || sheet == null || schedules == null || schedules.Count == 0)
		{
			return;
		}

		XYZ topLeftInset = GetScheduleTopLeftPoint(doc, sheet, spoolSettings);
		XYZ topRightInset = GetScheduleTopRightPoint(doc, sheet, spoolSettings);
		double columnTopY = alignTopRight ? topRightInset.Y : topLeftInset.Y;
		double? columnRightX = alignTopRight ? topRightInset.X : (double?)null;
		// Always seed Create with Top Left Point semantics (known good), then shift right if needed.
		XYZ seed = new XYZ(topLeftInset.X, columnTopY, 0.0);

		List<ScheduleSheetInstance> instances = new List<ScheduleSheetInstance>(schedules.Count);
		foreach (ViewSchedule schedule in schedules)
		{
			if (schedule == null)
			{
				continue;
			}

			try
			{
				instances.Add(ScheduleSheetInstance.Create(
					doc,
					((Element)sheet).Id,
					((Element)schedule).Id,
					seed));
			}
			catch
			{
				// Keep placing remaining schedules in the column.
			}
		}

		if (instances.Count == 0)
		{
			return;
		}

		// Batch sheet generation coalesces RegenTracked into a no-op. Filter-by-Sheet sizes stay at the
		// unfiltered row count until a real regenerate — that was shoving the stack ~2ft off-sheet.
		DoRegenNow(doc);
		_regenPendingDuringBatch = false;

		double nextTopY = columnTopY;
		foreach (ScheduleSheetInstance instance in instances)
		{
			XYZ desiredTopLeft = new XYZ(seed.X, nextTopY, 0.0);
			AlignScheduleContentCorner(doc, sheet, instance, desiredTopLeft, columnRightX);

			if (!TryGetScheduleContentCorners(doc, sheet, instance, out _, out _, out XYZ contentBottomLeft))
			{
				continue;
			}

			nextTopY = contentBottomLeft.Y - ScheduleStackGapFeet;
		}
	}

	/// <summary>
	/// Moves a placed schedule so Point (top-left cell) and optional content top-right X match targets.
	/// </summary>
	private static void AlignScheduleContentCorner(
		Document doc,
		ViewSheet sheet,
		ScheduleSheetInstance instance,
		XYZ desiredContentTopLeft,
		double? desiredContentRightX)
	{
		if (instance == null || desiredContentTopLeft == null)
		{
			return;
		}

		if (!TryGetScheduleContentCorners(doc, sheet, instance, out XYZ contentTopLeft, out XYZ contentTopRight, out _))
		{
			return;
		}

		double dy = desiredContentTopLeft.Y - contentTopLeft.Y;
		double dx;
		if (desiredContentRightX.HasValue)
		{
			dx = desiredContentRightX.Value - contentTopRight.X;
		}
		else
		{
			dx = desiredContentTopLeft.X - contentTopLeft.X;
		}

		if (Math.Abs(dx) > 1e-9 || Math.Abs(dy) > 1e-9)
		{
			ElementTransformUtils.MoveElement(doc, ((Element)instance).Id, new XYZ(dx, dy, 0.0));
		}
	}

	/// <summary>
	/// Content corners from Point (top-left cell). Right/bottom strip schedule chrome using the
	/// larger of left/top bbox overhang so asymmetric right padding is handled.
	/// </summary>
	private static bool TryGetScheduleContentCorners(
		Document doc,
		ViewSheet sheet,
		ScheduleSheetInstance instance,
		out XYZ contentTopLeft,
		out XYZ contentTopRight,
		out XYZ contentBottomLeft)
	{
		contentTopLeft = null;
		contentTopRight = null;
		contentBottomLeft = null;
		if (instance == null)
		{
			return false;
		}

		RegenTracked(doc);
		XYZ point = instance.Point;
		BoundingBoxXYZ bbox = ((Element)instance).get_BoundingBox((View)sheet);
		if (point == null || bbox == null)
		{
			return false;
		}

		double leftPad = Math.Max(0.0, point.X - bbox.Min.X);
		double topPad = Math.Max(0.0, bbox.Max.Y - point.Y);
		// When Point sits on the bbox left (leftPad ~ 0) the right chrome still matches the top chrome.
		double chrome = Math.Max(leftPad, topPad);
		if (chrome < 1e-9)
		{
			chrome = 1.0 / 144.0; // 1/12" typical schedule graphics pad
		}

		double contentMaxX = bbox.Max.X - chrome;
		double contentMinY = bbox.Min.Y + chrome;
		if (contentMaxX < point.X)
		{
			contentMaxX = point.X;
		}
		if (contentMinY > point.Y)
		{
			contentMinY = point.Y;
		}

		// Unfiltered Filter-by-Sheet schedules can report the full project row count (~2ft+).
		// Reject that so stacking never jumps off the sheet; caller must regen first.
		double contentHeight = point.Y - contentMinY;
		BoundingBoxXYZ titleBlockBounds = GetTitleBlockBounds(doc, sheet);
		double maxPlausibleHeight = titleBlockBounds != null
			? Math.Max(0.25, titleBlockBounds.Max.Y - titleBlockBounds.Min.Y)
			: 1.0;
		if (contentHeight > maxPlausibleHeight)
		{
			return false;
		}

		contentTopLeft = new XYZ(point.X, point.Y, 0.0);
		contentTopRight = new XYZ(contentMaxX, point.Y, 0.0);
		contentBottomLeft = new XYZ(point.X, contentMinY, 0.0);
		return true;
	}

	private static XYZ GetScheduleTopLeftPoint(Document doc, ViewSheet sheet, SpoolingManagerSettings spoolSettings)
	{
		double scheduleLeftInsetFeet = GetScheduleLeftInsetFeet(spoolSettings);
		double scheduleTopInsetFeet = GetScheduleTopInsetFeet(spoolSettings);
		BoundingBoxXYZ titleBlockBounds = GetTitleBlockBounds(doc, sheet);
		if (titleBlockBounds == null)
		{
			BoundingBoxUV outline = ((View)sheet).Outline;
			return new XYZ(outline.Min.U + scheduleLeftInsetFeet, outline.Max.V - scheduleTopInsetFeet, 0.0);
		}
		return new XYZ(titleBlockBounds.Min.X + scheduleLeftInsetFeet, titleBlockBounds.Max.Y - scheduleTopInsetFeet, 0.0);
	}

	private static XYZ GetScheduleTopRightPoint(Document doc, ViewSheet sheet, SpoolingManagerSettings spoolSettings)
	{
		// Same horizontal inset as left, mirrored from the title block right edge.
		double scheduleRightInsetFeet = GetScheduleLeftInsetFeet(spoolSettings);
		double scheduleTopInsetFeet = GetScheduleTopInsetFeet(spoolSettings);
		BoundingBoxXYZ titleBlockBounds = GetTitleBlockBounds(doc, sheet);
		if (titleBlockBounds == null)
		{
			BoundingBoxUV outline = ((View)sheet).Outline;
			return new XYZ(outline.Max.U - scheduleRightInsetFeet, outline.Max.V - scheduleTopInsetFeet, 0.0);
		}
		return new XYZ(titleBlockBounds.Max.X - scheduleRightInsetFeet, titleBlockBounds.Max.Y - scheduleTopInsetFeet, 0.0);
	}

	internal static BoundingBoxXYZ GetTitleBlockBounds(Document doc, ViewSheet sheet)
	{
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		if (doc == null || sheet == null)
		{
			return null;
		}
		if (!ReferenceEquals(_titleBlockBoundsCacheDoc, doc))
		{
			_titleBlockBoundsCache.Clear();
			_titleBlockBoundsCacheDoc = doc;
		}
		ElementId sheetId = ((Element)sheet).Id;
		if (_titleBlockBoundsCache.TryGetValue(sheetId, out BoundingBoxXYZ cached))
		{
			return cached;
		}
		BoundingBoxXYZ bounds = (from FamilyInstance x in (IEnumerable)new FilteredElementCollector(doc, sheetId).OfCategory((BuiltInCategory)(-2000280)).OfClass(typeof(FamilyInstance))
			select x.get_BoundingBox(sheet)).FirstOrDefault((BoundingBoxXYZ x) => x != null);
		_titleBlockBoundsCache[sheetId] = bounds;
		return bounds;
	}

	internal static TextNoteType FindTextNoteType(Document doc, string typeName)
	{
		if (doc == null || string.IsNullOrWhiteSpace(typeName))
		{
			return null;
		}
		return ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))).Cast<TextNoteType>().FirstOrDefault((TextNoteType x) => string.Equals(((Element)x).Name, typeName, StringComparison.OrdinalIgnoreCase));
	}

	internal static List<string> GetAssemblySWeldValuesForView(Document doc, AssemblyInstance assembly, View view)
	{
		if (doc == null || assembly == null || view == null)
		{
			return new List<string>();
		}
		return SortWeldLogParts(GetTaggablePartsForView(doc, assembly, view)
			.Where((FabricationPart x) => ShouldReceiveSWeldTag(x) && !string.IsNullOrWhiteSpace(GetSWeldValue(x))))
			.Select(GetSWeldValue)
			.ToList();
	}

	internal static View FindAssemblyViewOnSheetForWeldLog(Document doc, ViewSheet sheet, AssemblyInstance assembly, string viewLabel)
	{
		if (doc == null || sheet == null || assembly == null)
		{
			return null;
		}
		string text = (string.IsNullOrWhiteSpace(viewLabel) ? "3D Ortho" : viewLabel.Trim());
		View view = null;
		foreach (ElementId allViewport in sheet.GetAllViewports())
		{
			Element element = doc.GetElement(allViewport);
			Viewport val = (Viewport)(object)((element is Viewport) ? element : null);
			if (val == null)
			{
				continue;
			}
			Element element2 = doc.GetElement(val.ViewId);
			View val2 = (View)(object)((element2 is View) ? element2 : null);
			if (val2 == null || val2.AssociatedAssemblyInstanceId != ((Element)assembly).Id)
			{
				continue;
			}
			if (ViewMatchesWeldLogSourceLabel(val2, text))
			{
				return val2;
			}
			view = view ?? val2;
		}
		if (!string.Equals(text, "3D Ortho", StringComparison.OrdinalIgnoreCase))
		{
			return FindAssemblyViewOnSheetForWeldLog(doc, sheet, assembly, "3D Ortho");
		}
		return view;
	}

	private static bool ViewMatchesWeldLogSourceLabel(View view, string label)
	{
		if (view == null)
		{
			return false;
		}
		if (string.Equals(label, "3D Ortho", StringComparison.OrdinalIgnoreCase))
		{
			return view is View3D;
		}
		string text = (((Element)view).Name ?? string.Empty).ToUpperInvariant();
		if (string.Equals(label, "Back View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("BACK");
		}
		if (string.Equals(label, "Front View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("FRONT");
		}
		if (string.Equals(label, "Left View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("LEFT");
		}
		if (string.Equals(label, "Right View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("RIGHT");
		}
		if (string.Equals(label, "Top View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("TOP");
		}
		return false;
	}

	private static double GetWeldLogLeftInsetFeet(SpoolingManagerSettings settings)
	{
		if (settings != null && settings.WeldLogInsetFromTitleBlockLeftInches.HasValue)
		{
			return settings.WeldLogInsetFromTitleBlockLeftInches.Value / 12.0;
		}
		return DefaultWeldLogLeftInsetInches / 12.0;
	}

	private static double GetWeldLogBottomInsetFeet(SpoolingManagerSettings settings)
	{
		if (settings != null && settings.WeldLogInsetFromTitleBlockBottomInches.HasValue)
		{
			return settings.WeldLogInsetFromTitleBlockBottomInches.Value / 12.0;
		}
		int rowCount = GetWeldLogRowCount(settings);
		double projectStripHeightInches = GetWeldLogProjectStripHeightInches(settings);
		double rowSpacingInches = GetWeldLogRowSpacingInches(settings);
		return (projectStripHeightInches + (rowCount - 1) * rowSpacingInches) / 12.0;
	}

	private static int GetWeldLogRowCount(SpoolingManagerSettings settings)
	{
		if (settings != null && settings.WeldLogMaxRows.HasValue && settings.WeldLogMaxRows.Value > 0)
		{
			return settings.WeldLogMaxRows.Value;
		}
		return DefaultWeldLogRowCount;
	}

	private static double GetWeldLogProjectStripHeightInches(SpoolingManagerSettings settings)
	{
		if (settings != null && settings.WeldLogProjectStripHeightInches.HasValue)
		{
			return settings.WeldLogProjectStripHeightInches.Value;
		}
		return DefaultWeldLogProjectStripHeightInches;
	}

	private static double GetWeldLogRowSpacingInches(SpoolingManagerSettings settings)
	{
		if (settings != null && settings.WeldLogRowSpacingInches.HasValue)
		{
			return settings.WeldLogRowSpacingInches.Value;
		}
		return DefaultWeldLogRowSpacingInches;
	}

	private static double GetWeldLogRowSpacingFeet(SpoolingManagerSettings settings)
	{
		return GetWeldLogRowSpacingInches(settings) / 12.0;
	}

	/// <summary>
	/// Distance from the weld-log left inset to the text origin for a WELD NUMBER column.
	/// Column lines follow <see cref="DefaultWeldLogWeldNumberColumnLeftOffsetsInches"/>;
	/// text starts <see cref="DefaultWeldLogTextPaddingInches"/> (1/32") past each line.
	/// </summary>
	private static double GetWeldLogColumnOffsetFeet(int columnIndex)
	{
		if (columnIndex <= 0)
		{
			return DefaultWeldLogTextPaddingInches / 12.0;
		}
		int num = Math.Min(columnIndex, DefaultWeldLogWeldNumberColumnLeftOffsetsInches.Length - 1);
		double num2 = DefaultWeldLogWeldNumberColumnLeftOffsetsInches[num]
			+ DefaultWeldLogTextPaddingInches
			- columnIndex * DefaultWeldLogColumnCompressPerColumnInches;
		return num2 / 12.0;
	}

	private static double GetWeldLogTextOffsetLeftFeet(SpoolingManagerSettings settings)
	{
		double inches = (settings != null && settings.WeldLogTextOffsetLeftInches.HasValue)
			? settings.WeldLogTextOffsetLeftInches.Value
			: DefaultWeldLogTextOffsetLeftInches;
		return inches / 12.0;
	}

	private static double GetWeldLogTextOffsetUpFeet(SpoolingManagerSettings settings)
	{
		double inches = (settings != null && settings.WeldLogTextOffsetUpInches.HasValue)
			? settings.WeldLogTextOffsetUpInches.Value
			: DefaultWeldLogTextOffsetUpInches;
		return inches / 12.0;
	}

	private static XYZ GetWeldLogSlotPoint(Document doc, ViewSheet sheet, SpoolingManagerSettings settings, int columnIndex, int rowIndex)
	{
		double weldLogLeftInsetFeet = GetWeldLogLeftInsetFeet(settings);
		double weldLogBottomInsetFeet = GetWeldLogBottomInsetFeet(settings);
		double weldLogColumnOffsetFeet = GetWeldLogColumnOffsetFeet(columnIndex);
		double weldLogTextOffsetLeftFeet = GetWeldLogTextOffsetLeftFeet(settings);
		double weldLogTextOffsetUpFeet = GetWeldLogTextOffsetUpFeet(settings);
		double num = rowIndex * GetWeldLogRowSpacingFeet(settings);
		BoundingBoxXYZ titleBlockBounds = GetTitleBlockBounds(doc, sheet);
		if (titleBlockBounds == null)
		{
			BoundingBoxUV outline = ((View)sheet).Outline;
			return new XYZ(outline.Min.U + weldLogLeftInsetFeet + weldLogColumnOffsetFeet + weldLogTextOffsetLeftFeet, outline.Min.V + weldLogBottomInsetFeet - num + weldLogTextOffsetUpFeet, 0.0);
		}
		return new XYZ(titleBlockBounds.Min.X + weldLogLeftInsetFeet + weldLogColumnOffsetFeet + weldLogTextOffsetLeftFeet, titleBlockBounds.Min.Y + weldLogBottomInsetFeet - num + weldLogTextOffsetUpFeet, 0.0);
	}

	private static void RemoveWeldLogTextNotes(Document doc, ViewSheet sheet, ElementId textNoteTypeId)
	{
		if (doc == null || sheet == null || textNoteTypeId == null || textNoteTypeId == ElementId.InvalidElementId)
		{
			return;
		}
		List<ElementId> list = new List<ElementId>();
		foreach (TextNote item in (IEnumerable)new FilteredElementCollector(doc, ((Element)sheet).Id).OfClass(typeof(TextNote)))
		{
			TextNote val = item;
			if (val != null && val.GetTypeId() == textNoteTypeId)
			{
				list.Add(((Element)val).Id);
			}
		}
		if (list.Count > 0)
		{
			doc.Delete(list);
		}
	}

	internal static int FillWeldLogOnSheet(Document doc, ViewSheet sheet, AssemblyInstance assembly, SpoolingManagerSettings settings, TextNoteType textNoteType)
	{
		if (doc == null || sheet == null || assembly == null || settings == null || textNoteType == null || !settings.WeldLogEnabled)
		{
			return 0;
		}
		View val = FindAssemblyViewOnSheetForWeldLog(doc, sheet, assembly, settings.WeldLogSourceViewLabel);
		if (val == null)
		{
			return 0;
		}
		List<string> assemblySWeldValuesForView = GetAssemblySWeldValuesForView(doc, assembly, val);
		ElementId id = ((Element)textNoteType).Id;
		RemoveWeldLogTextNotes(doc, sheet, id);
		int weldLogRowCount = GetWeldLogRowCount(settings);
		int num = DefaultWeldLogColumnCount * weldLogRowCount;
		if (assemblySWeldValuesForView.Count == 0)
		{
			return 0;
		}
		TextNoteOptions val2 = new TextNoteOptions(id);
		val2.HorizontalAlignment = HorizontalTextAlignment.Left;
		int num2 = 0;
		for (int i = 0; i < assemblySWeldValuesForView.Count && i < num; i++)
		{
			int rowIndex = i % weldLogRowCount;
			int columnIndex = i / weldLogRowCount;
			XYZ point = GetWeldLogSlotPoint(doc, sheet, settings, columnIndex, rowIndex);
			TextNote.Create(doc, ((Element)sheet).Id, point, assemblySWeldValuesForView[i], val2);
			num2++;
		}
		return num2;
	}

	private static Reference GetBestTagReference(GeometryElement geometry, View view)
	{
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Expected O, but got Unknown
		if (view == null)
		{
			return null;
		}
		Face val = null;
		double num = double.MaxValue;
		XYZ val2 = view.ViewDirection.Normalize();
		foreach (Solid solid in GetSolids(geometry))
		{
			foreach (Face face in solid.Faces)
			{
				Face val3 = face;
				if ((GeometryObject)(object)val3 == (GeometryObject)null || val3.Reference == null)
				{
					continue;
				}
				PlanarFace val4 = (PlanarFace)(object)((val3 is PlanarFace) ? val3 : null);
				if ((GeometryObject)(object)val4 == (GeometryObject)null)
				{
					if ((GeometryObject)(object)val == (GeometryObject)null)
					{
						val = val3;
					}
					continue;
				}
				double num2 = val4.FaceNormal.Normalize().DotProduct(val2);
				if (num2 < num)
				{
					num = num2;
					val = (Face)(object)val4;
				}
			}
		}
		if (!((GeometryObject)(object)val != (GeometryObject)null))
		{
			return null;
		}
		return val.Reference;
	}

	private static IEnumerable<Reference> GetAllTagReferences(GeometryElement geometry, View view, XYZ targetPoint)
	{
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Expected O, but got Unknown
		if (view == null)
		{
			return Enumerable.Empty<Reference>();
		}
		List<Tuple<Reference, double>> list = new List<Tuple<Reference, double>>();
		foreach (Solid solid in GetSolids(geometry))
		{
			foreach (Face face in solid.Faces)
			{
				Face val = face;
				if (!((GeometryObject)(object)val == (GeometryObject)null) && val.Reference != null)
				{
					string stable = null;
					try
					{
						stable = val.Reference.ConvertToStableRepresentation(((Element)view).Document);
					}
					catch
					{
					}
					if (string.IsNullOrWhiteSpace(stable) || !list.Any((Tuple<Reference, double> x) => x.Item1 != null && SafeStableRepresentationEquals(x.Item1, stable, ((Element)view).Document)))
					{
						list.Add(Tuple.Create<Reference, double>(val.Reference, GetFaceDistanceScore(val, targetPoint, view)));
					}
				}
			}
		}
		return (from x in list
			orderby x.Item2
			select x.Item1).ToList();
	}

	private static IEnumerable<Reference> GetAllEdgeReferences(GeometryElement geometry, XYZ targetPoint, View view)
	{
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Expected O, but got Unknown
		List<Tuple<Reference, double>> list = new List<Tuple<Reference, double>>();
		foreach (Solid solid in GetSolids(geometry))
		{
			foreach (Edge edge in solid.Edges)
			{
				Edge val = edge;
				if (!((GeometryObject)(object)val == (GeometryObject)null) && val.Reference != null)
				{
					XYZ edgeSamplePoint = GetEdgeSamplePoint(val);
					if (edgeSamplePoint != null)
					{
						double item = ((targetPoint == null) ? 0.0 : edgeSamplePoint.DistanceTo(targetPoint));
						list.Add(Tuple.Create<Reference, double>(val.Reference, item));
					}
				}
			}
		}
		return (from x in list
			orderby x.Item2
			select x.Item1).ToList();
	}

	private static IEnumerable<Reference> GetSubelementReferences(Element element)
	{
		List<Reference> list = new List<Reference>();
		if (element == null)
		{
			return list;
		}
		try
		{
			foreach (Subelement subelement in element.GetSubelements())
			{
				if (subelement == null)
				{
					continue;
				}
				try
				{
					Reference reference = subelement.GetReference();
					if (reference != null)
					{
						list.Add(reference);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static bool SafeStableRepresentationEquals(Reference reference, string stableRepresentation, Document doc)
	{
		if (reference == null || string.IsNullOrWhiteSpace(stableRepresentation) || doc == null)
		{
			return false;
		}
		try
		{
			return string.Equals(reference.ConvertToStableRepresentation(doc), stableRepresentation, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static double GetFaceDistanceScore(Face face, XYZ targetPoint, View view)
	{
		if ((GeometryObject)(object)face == (GeometryObject)null)
		{
			return double.MaxValue;
		}
		XYZ faceSamplePoint = GetFaceSamplePoint(face);
		if (faceSamplePoint == null || targetPoint == null)
		{
			return double.MaxValue;
		}
		double num = faceSamplePoint.DistanceTo(targetPoint);
		PlanarFace val = (PlanarFace)(object)((face is PlanarFace) ? face : null);
		if ((GeometryObject)(object)val == (GeometryObject)null || view == null)
		{
			return num;
		}
		try
		{
			double num2 = 1.0 - Math.Abs(val.FaceNormal.Normalize().DotProduct(view.ViewDirection.Normalize()));
			return num + num2;
		}
		catch
		{
			return num;
		}
	}

	private static XYZ GetFaceSamplePoint(Face face)
	{
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Expected O, but got Unknown
		if ((GeometryObject)(object)face == (GeometryObject)null)
		{
			return null;
		}
		try
		{
			BoundingBoxUV boundingBox = face.GetBoundingBox();
			if (boundingBox == null)
			{
				return null;
			}
			UV val = new UV((boundingBox.Min.U + boundingBox.Max.U) * 0.5, (boundingBox.Min.V + boundingBox.Max.V) * 0.5);
			return face.Evaluate(val);
		}
		catch
		{
			return null;
		}
	}

	private static XYZ GetEdgeSamplePoint(Edge edge)
	{
		if ((GeometryObject)(object)edge == (GeometryObject)null)
		{
			return null;
		}
		try
		{
			Curve val = edge.AsCurve();
			if ((GeometryObject)(object)val == (GeometryObject)null)
			{
				return null;
			}
			return val.Evaluate(0.5, true);
		}
		catch
		{
			return null;
		}
	}

	private static bool HasReferenceableGeometry(GeometryElement geometry)
	{
		return GetSolids(geometry).Any();
	}

	private static IEnumerable<Solid> GetSolids(GeometryElement geometry)
	{
		foreach (GeometryObject item in geometry)
		{
			Solid val = (Solid)(object)((item is Solid) ? item : null);
			if ((GeometryObject)(object)val != (GeometryObject)null && val.Faces.Size > 0 && val.Volume > 0.0)
			{
				yield return val;
				continue;
			}
			GeometryInstance instance = (GeometryInstance)(object)((item is GeometryInstance) ? item : null);
			if ((GeometryObject)(object)instance == (GeometryObject)null)
			{
				continue;
			}
			GeometryElement val2 = null;
			try
			{
				val2 = instance.GetSymbolGeometry();
			}
			catch
			{
			}
			if ((GeometryObject)(object)val2 != (GeometryObject)null)
			{
				foreach (Solid solid in GetSolids(val2))
				{
					yield return solid;
				}
			}
			GeometryElement val3 = null;
			try
			{
				val3 = instance.GetInstanceGeometry();
			}
			catch
			{
			}
			if ((GeometryObject)(object)val3 == (GeometryObject)null)
			{
				continue;
			}
			foreach (Solid solid2 in GetSolids(val3))
			{
				yield return solid2;
			}
		}
	}

	private static void SetTagHeadPosition(IndependentTag tag, XYZ point)
	{
		if (tag == null || point == null)
		{
			return;
		}
		try
		{
			tag.TagHeadPosition = point;
		}
		catch
		{
		}
	}

	private static XYZ GetTagHeadPoint(Element element, View view, string placement, SpoolingManagerKind kind, SpoolingManagerSettings settings, XYZ anchorPoint = null)
	{
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		BoundingBoxXYZ val = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
		if (val == null)
		{
			return null;
		}
		List<XYZ> list = GetBoundingBoxCorners(val).ToList();
		if (list.Count == 0)
		{
			return null;
		}
		XYZ right = view.RightDirection.Normalize();
		XYZ up = view.UpDirection.Normalize();
		XYZ val2 = (XYZ)(((object)anchorPoint) ?? ((object)new XYZ(list.Average((XYZ x) => x.X), list.Average((XYZ x) => x.Y), list.Average((XYZ x) => x.Z))));
		double num = list.Min((XYZ c) => c.DotProduct(right));
		double num2 = list.Max((XYZ c) => c.DotProduct(right));
		double num3 = list.Min((XYZ c) => c.DotProduct(up));
		double num4 = list.Max((XYZ c) => c.DotProduct(up));
		double num5 = Math.Max(num2 - num, 0.1);
		double num6 = Math.Max(num4 - num3, 0.1);
		double sheetFeet = GetTagLeaderBaselineInchesOnSheet(view, kind, settings) / 12.0;
		double num7 = Math.Max(ConvertSheetOffsetToModelDistance(view, sheetFeet), num5 * 0.05);
		double num8 = Math.Max(ConvertSheetOffsetToModelDistance(view, sheetFeet), num6 * 0.05);
		double num9 = (num + num2) * 0.5;
		double num10 = (num3 + num4) * 0.5;
		switch ((placement ?? string.Empty).Trim().ToLowerInvariant())
		{
		case "top left":
			num9 = num - num7;
			num10 = num4 + num8;
			break;
		case "top center":
			num10 = num4 + num8;
			break;
		case "top right":
			num9 = num2 + num7;
			num10 = num4 + num8;
			break;
		case "middle left":
			num9 = num - num7;
			break;
		case "middle right":
			num9 = num2 + num7;
			break;
		case "bottom left":
			num9 = num - num7;
			num10 = num3 - num8;
			break;
		case "bottom center":
			num10 = num3 - num8;
			break;
		case "bottom right":
			num9 = num2 + num7;
			num10 = num3 - num8;
			break;
		}
		return val2 + right.Multiply(num9 - val2.DotProduct(right)) + up.Multiply(num10 - val2.DotProduct(up));
	}

	private static XYZ GetElementAnchorPoint(Element element, View view)
	{
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Expected O, but got Unknown
		BoundingBoxXYZ val = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
		if (val == null)
		{
			return null;
		}
		List<XYZ> list = GetBoundingBoxCorners(val).ToList();
		if (list.Count == 0)
		{
			return null;
		}
		return new XYZ(list.Average((XYZ x) => x.X), list.Average((XYZ x) => x.Y), list.Average((XYZ x) => x.Z));
	}

	private static List<XYZ> GetElementAnchorCandidates(Element element, View view)
	{
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Expected O, but got Unknown
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b3: Expected O, but got Unknown
		//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cc: Expected O, but got Unknown
		//IL_01db: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e5: Expected O, but got Unknown
		//IL_01f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fe: Expected O, but got Unknown
		//IL_020d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0217: Expected O, but got Unknown
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		//IL_0230: Expected O, but got Unknown
		//IL_02d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02dd: Expected O, but got Unknown
		List<XYZ> list = new List<XYZ>();
		BoundingBoxXYZ val = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
		if (val == null)
		{
			return list;
		}
		List<XYZ> list2 = GetBoundingBoxCorners(val).ToList();
		if (list2.Count == 0)
		{
			return list;
		}
		XYZ val2 = new XYZ(list2.Average((XYZ x) => x.X), list2.Average((XYZ x) => x.Y), list2.Average((XYZ x) => x.Z));
		list.Add(val2);
		double num = list2.Min((XYZ x) => x.X);
		double num2 = list2.Max((XYZ x) => x.X);
		double num3 = list2.Min((XYZ x) => x.Y);
		double num4 = list2.Max((XYZ x) => x.Y);
		double num5 = list2.Min((XYZ x) => x.Z);
		double num6 = list2.Max((XYZ x) => x.Z);
		list.Add(new XYZ(num, val2.Y, val2.Z));
		list.Add(new XYZ(num2, val2.Y, val2.Z));
		list.Add(new XYZ(val2.X, num3, val2.Z));
		list.Add(new XYZ(val2.X, num4, val2.Z));
		list.Add(new XYZ(val2.X, val2.Y, num5));
		list.Add(new XYZ(val2.X, val2.Y, num6));
		list.AddRange(list2);
		FabricationPart val3 = (FabricationPart)(object)((element is FabricationPart) ? element : null);
		if (val3 != null)
		{
			List<XYZ> fabricationConnectorPoints = GetFabricationConnectorPoints(val3);
			list.AddRange(fabricationConnectorPoints);
			if (fabricationConnectorPoints.Count >= 2)
			{
				XYZ item = new XYZ(fabricationConnectorPoints.Average((XYZ x) => x.X), fabricationConnectorPoints.Average((XYZ x) => x.Y), fabricationConnectorPoints.Average((XYZ x) => x.Z));
				list.Add(item);
			}
		}
		return (from x in list
			group x by $"{Math.Round(x.X, 6)}|{Math.Round(x.Y, 6)}|{Math.Round(x.Z, 6)}" into x
			select x.First()).ToList();
	}

	private static List<XYZ> GetFabricationConnectorPoints(FabricationPart part)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Expected O, but got Unknown
		List<XYZ> list = new List<XYZ>();
		if (part == null)
		{
			return list;
		}
		try
		{
			ConnectorManager connectorManager = part.ConnectorManager;
			if (connectorManager == null)
			{
				return list;
			}
			foreach (Connector connector in connectorManager.Connectors)
			{
				Connector val = connector;
				if (val != null)
				{
					XYZ origin = val.Origin;
					if (origin != null)
					{
						list.Add(origin);
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static double ConvertSheetOffsetToModelDistance(View view, double sheetFeet)
	{
		if (view == null)
		{
			return sheetFeet;
		}
		int num = 1;
		try
		{
			num = Math.Max(view.Scale, 1);
		}
		catch
		{
			num = 1;
		}
		return sheetFeet * (double)num;
	}

	private static double GetTagLeaderBaselineInchesOnSheet(View view, SpoolingManagerKind kind, SpoolingManagerSettings settings)
	{
		if (kind.IsMmcStyle())
		{
			return 0.8;
		}
		return 0.5 * GetTagLeaderScaleFactor(view, kind);
	}

	private static double GetTagLeaderMaxInchesOnSheet(View view, SpoolingManagerKind kind, SpoolingManagerSettings settings, double baselineInches)
	{
		if (kind.IsMmcStyle())
		{
			return Math.Max(baselineInches + 0.2, baselineInches * 2.0);
		}
		return 1.25 * GetTagLeaderScaleFactor(view, kind);
	}

	private static double GetTagLeaderScaleFactor(View view, SpoolingManagerKind kind)
	{
		// Tag leader/gap sizes are expressed as constant PAPER (sheet) inches so the
		// annotation layout looks identical at every generation scale. Downstream helpers
		// run these sheet values through ConvertSheetOffsetToModelDistance (which multiplies
		// by view.Scale), so the on-sheet appearance is scale-independent. Lower this constant
		// to pull tags closer to the geometry, raise it to push them out. Using 24.0/scale here
		// instead produced a constant MODEL distance, which made leaders cramp at coarse scales
		// (e.g. 1/2") and stretch when scaled up.
		return 1.0;
	}

	private static double GetTagLeaderGapInchesOnSheet(View view, SpoolingManagerKind kind, SpoolingManagerSettings settings, double inchesOnSheet)
	{
		if (kind.IsMmcStyle())
		{
			double tagLeaderBaselineInchesOnSheet = GetTagLeaderBaselineInchesOnSheet(view, kind, settings);
			return Math.Max(inchesOnSheet, tagLeaderBaselineInchesOnSheet * 0.5);
		}
		return inchesOnSheet * GetTagLeaderScaleFactor(view, kind);
	}

	private static void OrganizeTags(View view, AssemblyInstance assembly, string placement, IList<TagLayoutData> tags)
	{
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Expected O, but got Unknown
		BoundingBoxXYZ val = assembly.get_BoundingBox(view) ?? assembly.get_BoundingBox(null);
		if (val == null)
		{
			return;
		}
		List<XYZ> list = GetBoundingBoxCorners(val).ToList();
		if (list.Count == 0)
		{
			return;
		}
		XYZ right = view.RightDirection.Normalize();
		XYZ up = view.UpDirection.Normalize();
		XYZ val2 = new XYZ(list.Average((XYZ x) => x.X), list.Average((XYZ x) => x.Y), list.Average((XYZ x) => x.Z));
		double num = list.Min((XYZ c) => c.DotProduct(right));
		double num2 = list.Max((XYZ c) => c.DotProduct(right));
		double num3 = list.Min((XYZ c) => c.DotProduct(up));
		double num4 = list.Max((XYZ c) => c.DotProduct(up));
		Math.Max(num2 - num, 0.1);
		Math.Max(num4 - num3, 0.1);
		double num5 = 1.0 / 48.0;
		double num6 = 1.0 / 48.0;
		string text = (placement ?? string.Empty).Trim().ToLowerInvariant();
		if (text.Contains("left") || text.Contains("right") || text == "middle center")
		{
			bool num7 = text.Contains("left");
			bool flag = text.Contains("right");
			double num8 = (num7 ? (num - num5) : (num2 + num5));
			if (!num7 && !flag)
			{
				num8 = (num + num2) * 0.5;
			}
			double end = num3;
			List<TagLayoutData> list2 = (from x in tags
				where x.Tag != null && x.AnchorPoint != null
				orderby x.AnchorPoint.DotProduct(up) descending
				select x).ToList();
			List<double> list3 = DistributeCoordinates(num4, end, list2.Count);
			for (int num9 = 0; num9 < list2.Count; num9++)
			{
				XYZ val3 = val2 + right.Multiply(num8 - val2.DotProduct(right)) + up.Multiply(list3[num9] - val2.DotProduct(up));
				SetTagHeadPosition(list2[num9].Tag, val3);
				ApplyFreeEndLeaderLayout(list2[num9], val3);
			}
		}
		else
		{
			double num10 = (text.Contains("top") ? (num4 + num6) : (num3 - num6));
			double end2 = num2;
			List<TagLayoutData> list4 = (from x in tags
				where x.Tag != null && x.AnchorPoint != null
				orderby x.AnchorPoint.DotProduct(right)
				select x).ToList();
			List<double> list5 = DistributeCoordinates(num, end2, list4.Count);
			for (int num11 = 0; num11 < list4.Count; num11++)
			{
				XYZ val4 = val2 + right.Multiply(list5[num11] - val2.DotProduct(right)) + up.Multiply(num10 - val2.DotProduct(up));
				SetTagHeadPosition(list4[num11].Tag, val4);
				ApplyFreeEndLeaderLayout(list4[num11], val4);
			}
		}
	}

	private static void ResolveTagOverlaps(View view, IList<TagLayoutData> tags, SpoolingManagerKind kind, SpoolingManagerSettings settings)
	{
		if (view == null || tags == null || tags.Count <= 1)
		{
			return;
		}
		XYZ right = view.RightDirection.Normalize();
		XYZ up = view.UpDirection.Normalize();
		double sheetFeet = GetTagLeaderGapInchesOnSheet(view, kind, settings, 0.25) / 12.0;
		double minVerticalGap = ConvertSheetOffsetToModelDistance(view, sheetFeet);
		double minHorizontalGap = ConvertSheetOffsetToModelDistance(view, sheetFeet);
		List<TagLayoutData> list = (from x in tags
			where x?.Tag != null && x.AnchorPoint != null
			orderby x.AnchorPoint.DotProduct(up) descending, x.AnchorPoint.DotProduct(right)
			select x).ToList();
		List<TagLayoutData> list2 = new List<TagLayoutData>();
		foreach (TagLayoutData item in list)
		{
			XYZ tagHeadPosition = GetTagHeadPosition(item.Tag);
			if (tagHeadPosition == null)
			{
				continue;
			}
			if (IsOverlappingAny(tagHeadPosition, list2, right, up, minHorizontalGap, minVerticalGap))
			{
				XYZ val = FindRadialNonOverlappingPoint(item.AnchorPoint, tagHeadPosition, list2, right, up, minHorizontalGap, minVerticalGap);
				if (val != null)
				{
					tagHeadPosition = val;
					SetTagHeadPosition(item.Tag, tagHeadPosition);
				}
			}
			list2.Add(item);
		}
	}

	private static void EnsureMinimumTagHeadDistances(View view, IList<TagLayoutData> tags, double minSheetDistance)
	{
		if (view == null || tags == null || tags.Count == 0)
		{
			return;
		}
		XYZ val = view.RightDirection.Normalize();
		XYZ val2 = view.UpDirection.Normalize();
		double num = ConvertSheetOffsetToModelDistance(view, minSheetDistance);
		foreach (TagLayoutData tag in tags)
		{
			if (tag?.Tag == null || tag.AnchorPoint == null)
			{
				continue;
			}
			XYZ tagHeadPosition = GetTagHeadPosition(tag.Tag);
			if (tagHeadPosition != null)
			{
				double num2 = tagHeadPosition.DotProduct(val) - tag.AnchorPoint.DotProduct(val);
				double num3 = tagHeadPosition.DotProduct(val2) - tag.AnchorPoint.DotProduct(val2);
				double num4 = Math.Sqrt(num2 * num2 + num3 * num3);
				if (!(num4 >= num) && !(num4 < 1E-09))
				{
					double num5 = num / num4;
					XYZ point = tag.AnchorPoint + val.Multiply(num2 * num5) + val2.Multiply(num3 * num5);
					SetTagHeadPosition(tag.Tag, point);
				}
			}
		}
	}

	private static void ClampTagHeadDistances(View view, IList<TagLayoutData> tags, double maxSheetDistance)
	{
		if (view == null || tags == null || tags.Count == 0)
		{
			return;
		}
		XYZ val = view.RightDirection.Normalize();
		XYZ val2 = view.UpDirection.Normalize();
		double num = ConvertSheetOffsetToModelDistance(view, maxSheetDistance);
		foreach (TagLayoutData tag in tags)
		{
			if (tag?.Tag == null || tag.AnchorPoint == null)
			{
				continue;
			}
			XYZ tagHeadPosition = GetTagHeadPosition(tag.Tag);
			if (tagHeadPosition != null)
			{
				double num2 = tagHeadPosition.DotProduct(val) - tag.AnchorPoint.DotProduct(val);
				double num3 = tagHeadPosition.DotProduct(val2) - tag.AnchorPoint.DotProduct(val2);
				double num4 = Math.Sqrt(num2 * num2 + num3 * num3);
				if (!(num4 <= num) && !(num4 < 1E-09))
				{
					double num5 = num / num4;
					XYZ point = tag.AnchorPoint + val.Multiply(num2 * num5) + val2.Multiply(num3 * num5);
					SetTagHeadPosition(tag.Tag, point);
				}
			}
		}
	}

	private static bool IsOverlappingAny(XYZ headPoint, IList<TagLayoutData> placed, XYZ right, XYZ up, double minHorizontalGap, double minVerticalGap)
	{
		foreach (TagLayoutData item in placed)
		{
			XYZ tagHeadPosition = GetTagHeadPosition(item.Tag);
			if (tagHeadPosition != null)
			{
				double num = Math.Abs(tagHeadPosition.DotProduct(up) - headPoint.DotProduct(up));
				double num2 = Math.Abs(tagHeadPosition.DotProduct(right) - headPoint.DotProduct(right));
				if (num < minVerticalGap && num2 < minHorizontalGap)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static XYZ FindRadialNonOverlappingPoint(XYZ anchorPoint, XYZ currentHead, IList<TagLayoutData> placed, XYZ right, XYZ up, double minHorizontalGap, double minVerticalGap)
	{
		if (anchorPoint == null || currentHead == null)
		{
			return null;
		}
		double num = currentHead.DotProduct(right) - anchorPoint.DotProduct(right);
		double num2 = currentHead.DotProduct(up) - anchorPoint.DotProduct(up);
		double num3 = Math.Max(Math.Sqrt(num * num + num2 * num2), Math.Max(minHorizontalGap, minVerticalGap));
		double num4 = Math.Atan2(num2, num);
		double[] array = new double[12]
		{
			0.0,
			Math.PI / 12.0,
			-Math.PI / 12.0,
			Math.PI / 6.0,
			-Math.PI / 6.0,
			Math.PI / 3.0,
			-Math.PI / 3.0,
			Math.PI / 2.0,
			-Math.PI / 2.0,
			Math.PI * 2.0 / 3.0,
			Math.PI * -2.0 / 3.0,
			Math.PI
		};
		for (int i = 0; i < 7; i++)
		{
			double num5 = num3 + (double)i * Math.Max(minHorizontalGap, minVerticalGap);
			double[] array2 = array;
			foreach (double num6 in array2)
			{
				double num7 = num4 + num6;
				XYZ val = anchorPoint + right.Multiply(Math.Cos(num7) * num5) + up.Multiply(Math.Sin(num7) * num5);
				if (!IsOverlappingAny(val, placed, right, up, minHorizontalGap, minVerticalGap))
				{
					return val;
				}
			}
		}
		return null;
	}

	private static XYZ GetTagHeadPosition(IndependentTag tag)
	{
		if (tag == null)
		{
			return null;
		}
		try
		{
			return tag.TagHeadPosition;
		}
		catch
		{
			return null;
		}
	}

	private static double GetTagHeadProjection(IndependentTag tag, XYZ axis)
	{
		XYZ tagHeadPosition = GetTagHeadPosition(tag);
		if (tagHeadPosition != null)
		{
			return tagHeadPosition.DotProduct(axis);
		}
		return 0.0;
	}

	private static void ApplyFreeEndLeaderLayout(TagLayoutData data, XYZ headPoint)
	{
		if (data?.Tag != null && data.AnchorPoint != null && headPoint != null)
		{
			try
			{
				data.Tag.HasLeader = true;
			}
			catch
			{
			}
			TrySetLeaderEndCondition(data.Tag, "Free");
			if (data.Reference != null)
			{
				TryInvokeTagPointSetter(data.Tag, "SetLeaderEnd", data.Reference, data.AnchorPoint);
			}
			StraightenIndependentTagLeader(data.Tag, data.Reference, data.AnchorPoint, headPoint);
		}
	}

	private static void StraightenIndependentTagLeader(IndependentTag tag, Reference reference, XYZ anchorPoint, XYZ headPoint)
	{
		if (tag == null)
		{
			return;
		}
		try
		{
			tag.HasLeader = false;
			tag.HasLeader = true;
			TrySetLeaderEndCondition(tag, "Free");
			if (reference != null && anchorPoint != null)
			{
				TryInvokeTagPointSetter(tag, "SetLeaderEnd", reference, anchorPoint);
			}
			if (headPoint != null)
			{
				SetTagHeadPosition(tag, headPoint);
			}
		}
		catch
		{
		}
	}

	private static void FinalizeTagLeaderMode(View view, IList<TagLayoutData> tags)
	{
		if (view == null || tags == null || tags.Count == 0)
		{
			return;
		}
		foreach (TagLayoutData tag in tags)
		{
			if (tag?.Tag != null)
			{
				try
				{
					tag.Tag.HasLeader = true;
				}
				catch
				{
				}
				TrySetLeaderEndCondition(tag.Tag, "Free");
				XYZ tagHeadPosition = GetTagHeadPosition(tag.Tag);
				if (tagHeadPosition != null)
				{
					ApplyFreeEndLeaderLayout(tag, tagHeadPosition);
				}
			}
		}
	}

	private static void TrySetLeaderEndCondition(IndependentTag tag, string enumName)
	{
		if (tag == null || string.IsNullOrWhiteSpace(enumName))
		{
			return;
		}
		try
		{
			PropertyInfo property = ((object)tag).GetType().GetProperty("LeaderEndCondition");
			if (!(property == null) && property.CanWrite)
			{
				object value = Enum.Parse(property.PropertyType, enumName, ignoreCase: true);
				property.SetValue(tag, value);
			}
		}
		catch
		{
		}
	}

	private static void TryInvokeTagPointSetter(IndependentTag tag, string methodName, Reference reference, XYZ point)
	{
		if (tag == null || reference == null || point == null)
		{
			return;
		}
		try
		{
			MethodInfo method = ((object)tag).GetType().GetMethod(methodName, new Type[2]
			{
				typeof(Reference),
				typeof(XYZ)
			});
			if (!(method == null))
			{
				method.Invoke(tag, new object[2] { reference, point });
			}
		}
		catch
		{
		}
	}

	private static List<double> SpreadCoordinates(IList<double> desired, double minSpacing, bool ascending)
	{
		List<double> list = desired.ToList();
		if (list.Count <= 1)
		{
			return list;
		}
		if (ascending)
		{
			for (int i = 1; i < list.Count; i++)
			{
				if (list[i] < list[i - 1] + minSpacing)
				{
					list[i] = list[i - 1] + minSpacing;
				}
			}
		}
		else
		{
			for (int j = 1; j < list.Count; j++)
			{
				if (list[j] > list[j - 1] - minSpacing)
				{
					list[j] = list[j - 1] - minSpacing;
				}
			}
		}
		double num = desired.Average();
		double num2 = list.Average();
		double num3 = num - num2;
		for (int k = 0; k < list.Count; k++)
		{
			list[k] += num3;
		}
		return list;
	}

	private static List<double> DistributeCoordinates(double start, double end, int count)
	{
		List<double> list = new List<double>();
		if (count <= 0)
		{
			return list;
		}
		if (count == 1)
		{
			list.Add((start + end) * 0.5);
			return list;
		}
		double num = (end - start) / (double)(count - 1);
		for (int i = 0; i < count; i++)
		{
			list.Add(start + num * (double)i);
		}
		return list;
	}

	private static IEnumerable<XYZ> GetBoundingBoxCorners(BoundingBoxXYZ bbox)
	{
		Transform transform = bbox.Transform ?? Transform.Identity;
		XYZ min = bbox.Min;
		XYZ max = bbox.Max;
		yield return transform.OfPoint(new XYZ(min.X, min.Y, min.Z));
		yield return transform.OfPoint(new XYZ(min.X, min.Y, max.Z));
		yield return transform.OfPoint(new XYZ(min.X, max.Y, min.Z));
		yield return transform.OfPoint(new XYZ(min.X, max.Y, max.Z));
		yield return transform.OfPoint(new XYZ(max.X, min.Y, min.Z));
		yield return transform.OfPoint(new XYZ(max.X, min.Y, max.Z));
		yield return transform.OfPoint(new XYZ(max.X, max.Y, min.Z));
		yield return transform.OfPoint(new XYZ(max.X, max.Y, max.Z));
	}

	internal static List<FabricationPart> GetAssemblyFabricationParts(Document doc, AssemblyInstance assembly)
	{
		if (doc == null || assembly == null)
		{
			return new List<FabricationPart>();
		}
		ElementId assemblyId = ((Element)assembly).Id;
		if (!ReferenceEquals(_assemblyPartsCacheDoc, doc))
		{
			_assemblyPartsCache.Clear();
			_assemblyPartsCacheDoc = doc;
		}
		if (_assemblyPartsCache.TryGetValue(assemblyId, out List<FabricationPart> cached))
		{
			return cached;
		}
		List<FabricationPart> parts = (from id in assembly.GetMemberIds()
			select doc.GetElement(id)).OfType<FabricationPart>().ToList();
		_assemblyPartsCache[assemblyId] = parts;
		return parts;
	}

	internal static void AssignAssemblyItemNumbers(
		Document doc,
		AssemblyInstance assembly,
		SpoolingManagerKind kind = SpoolingManagerKind.Standard,
		SpoolingManagerSettings settings = null)
	{
		List<FabricationPart> source = GetAssemblyFabricationParts(doc, assembly);
		foreach (FabricationPart item in source.Where(ShouldClearFabricationItemNumber))
		{
			ClearFabricationItemNumber(item);
		}
		List<IGrouping<string, FabricationPart>> list = (from x in source.Where((FabricationPart x) => !ShouldExcludeFromItemNumbering(x, source)).ToList().GroupBy(GetFabricationItemGroupingKey, StringComparer.OrdinalIgnoreCase)
			orderby GetFabricationSortPriority(x.First())
			select x).ThenBy((IGrouping<string, FabricationPart> x) => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

		if (settings != null && settings.ItemNumberCustomFormatEnabled)
		{
			AssignAssemblyItemNumbersCustom(list, settings);
		}
		else if (kind.IsMmcStyle())
		{
			List<IGrouping<string, FabricationPart>> list2 = list.Where((IGrouping<string, FabricationPart> g) => IsMmcStraightFabricationPart(g.First())).ToList();
			List<IGrouping<string, FabricationPart>> list3 = list.Where((IGrouping<string, FabricationPart> g) => !IsMmcStraightFabricationPart(g.First())).ToList();
			for (int num = 0; num < list2.Count; num++)
			{
				string itemNumber = $"P-{num + 1:D3}-S";
				SetFabricationItemNumberOnGroup(list2[num], itemNumber);
			}
			for (int num2 = 0; num2 < list3.Count; num2++)
			{
				string itemNumber2 = $"P-{num2 + 1:D3}-F";
				SetFabricationItemNumberOnGroup(list3[num2], itemNumber2);
			}
		}
		else
		{
			for (int num3 = 0; num3 < list.Count; num3++)
			{
				SetFabricationItemNumberOnGroup(list[num3], (num3 + 1).ToString());
			}
		}

		AssignAssemblyHangerNumbers(doc, assembly);
	}

	/// <summary>
	/// Separate Straight / Fitting / Valve series using configurable prefix + suffix
	/// (e.g. P-001-S, P-001-F, P-001-V).
	/// </summary>
	private static void AssignAssemblyItemNumbersCustom(
		List<IGrouping<string, FabricationPart>> groups,
		SpoolingManagerSettings settings)
	{
		if (groups == null || groups.Count == 0 || settings == null)
		{
			return;
		}

		int digits = settings.ItemNumberDigits < 1 ? 1 : settings.ItemNumberDigits;
		List<IGrouping<string, FabricationPart>> straights = groups
			.Where((IGrouping<string, FabricationPart> g) => GetFabricationSortPriority(g.First()) == 0)
			.ToList();
		List<IGrouping<string, FabricationPart>> valves = groups
			.Where((IGrouping<string, FabricationPart> g) => GetFabricationSortPriority(g.First()) == 2)
			.ToList();
		List<IGrouping<string, FabricationPart>> fittings = groups
			.Where((IGrouping<string, FabricationPart> g) =>
			{
				int priority = GetFabricationSortPriority(g.First());
				return priority != 0 && priority != 2;
			})
			.ToList();

		AssignSeriesItemNumbers(
			straights,
			settings.ItemNumberStraightPrefix,
			settings.ItemNumberStraightSuffix,
			digits);
		AssignSeriesItemNumbers(
			fittings,
			settings.ItemNumberFittingPrefix,
			settings.ItemNumberFittingSuffix,
			digits);
		AssignSeriesItemNumbers(
			valves,
			settings.ItemNumberValvePrefix,
			settings.ItemNumberValveSuffix,
			digits);
	}

	private static void AssignSeriesItemNumbers(
		List<IGrouping<string, FabricationPart>> groups,
		string prefix,
		string suffix,
		int digits)
	{
		if (groups == null || groups.Count == 0)
		{
			return;
		}

		string safePrefix = prefix ?? string.Empty;
		string safeSuffix = suffix ?? string.Empty;
		string format = "D" + digits.ToString(CultureInfo.InvariantCulture);
		for (int i = 0; i < groups.Count; i++)
		{
			string number = (i + 1).ToString(format, CultureInfo.InvariantCulture);
			SetFabricationItemNumberOnGroup(groups[i], safePrefix + number + safeSuffix);
		}
	}

	/// <summary>
	/// Numbers each hanger individually: Clevis → C-1, C-2…; Unistrut → U-1, U-2….
	/// Sorted by Family, then Size (same family/size still get distinct numbers).
	/// </summary>
	internal static void AssignAssemblyHangerNumbers(Document doc, AssemblyInstance assembly)
	{
		if (doc == null || assembly == null)
		{
			return;
		}

		List<FabricationPart> hangers = GetAssemblyFabricationParts(doc, assembly)
			.Where(FabricationPartClassification.IsFabricationHanger)
			.ToList();
		if (hangers.Count == 0)
		{
			return;
		}

		hangers.Sort(CompareHangersForNumbering);

		int clevisIndex = 1;
		int unistrutIndex = 1;
		foreach (FabricationPart hanger in hangers)
		{
			if (IsUnistrutHanger(hanger))
			{
				SetFabricationItemNumber(hanger, "U-" + unistrutIndex.ToString(CultureInfo.InvariantCulture));
				unistrutIndex++;
			}
			else
			{
				// Clevis and any other hanger type use the C- series.
				SetFabricationItemNumber(hanger, "C-" + clevisIndex.ToString(CultureInfo.InvariantCulture));
				clevisIndex++;
			}
		}
	}

	private static int CompareHangersForNumbering(FabricationPart a, FabricationPart b)
	{
		int familyCompare = string.Compare(GetHangerFamilyName(a), GetHangerFamilyName(b), StringComparison.OrdinalIgnoreCase);
		if (familyCompare != 0)
		{
			return familyCompare;
		}

		int sizeCompare = CompareHangerSize(GetHangerSize(a), GetHangerSize(b));
		if (sizeCompare != 0)
		{
			return sizeCompare;
		}

		long idA = ((Element)a)?.Id?.Value ?? 0L;
		long idB = ((Element)b)?.Id?.Value ?? 0L;
		return idA.CompareTo(idB);
	}

	private static string GetHangerFamilyName(FabricationPart part)
	{
		string family = GetPartParameterValue((Element)(object)part, "Family");
		if (!string.IsNullOrWhiteSpace(family))
		{
			return family.Trim();
		}

		string longDescription = GetPartParameterValue((Element)(object)part, "Product Long Description");
		if (!string.IsNullOrWhiteSpace(longDescription))
		{
			return longDescription.Trim();
		}

		return ((Element)part)?.Name?.Trim() ?? string.Empty;
	}

	private static string GetHangerSize(FabricationPart part)
	{
		foreach (string parameterName in new[] { "Product Entry", "Size", "Product Size Description", "E-Hanger Size" })
		{
			string value = GetPartParameterValue((Element)(object)part, parameterName);
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value.Trim();
			}
		}

		return string.Empty;
	}

	private static int CompareHangerSize(string sizeA, string sizeB)
	{
		bool hasA = TryParseHangerSizeInches(sizeA, out double inchesA);
		bool hasB = TryParseHangerSizeInches(sizeB, out double inchesB);
		if (hasA && hasB)
		{
			int numeric = inchesA.CompareTo(inchesB);
			if (numeric != 0)
			{
				return numeric;
			}
		}
		else if (hasA != hasB)
		{
			return hasA ? -1 : 1;
		}

		return string.Compare(sizeA ?? string.Empty, sizeB ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryParseHangerSizeInches(string size, out double inches)
	{
		inches = 0.0;
		if (string.IsNullOrWhiteSpace(size))
		{
			return false;
		}

		StringBuilder digits = new StringBuilder();
		bool sawDecimal = false;
		foreach (char c in size)
		{
			if (char.IsDigit(c))
			{
				digits.Append(c);
			}
			else if (c == '.' && !sawDecimal)
			{
				digits.Append(c);
				sawDecimal = true;
			}
			else if (digits.Length > 0)
			{
				break;
			}
		}

		return digits.Length > 0 &&
			double.TryParse(digits.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out inches);
	}

	private static bool IsUnistrutHanger(FabricationPart part)
	{
		string corpus = string.Join(" ",
			GetHangerFamilyName(part),
			GetPartParameterValue((Element)(object)part, "Product Long Description"),
			GetPartParameterValue((Element)(object)part, "Product Name"),
			GetPartParameterValue((Element)(object)part, "Alias"),
			((Element)part)?.Name ?? string.Empty).ToUpperInvariant();

		if (corpus.IndexOf("CLEVIS", StringComparison.Ordinal) >= 0)
		{
			return false;
		}

		return corpus.IndexOf("UNISTRUT", StringComparison.Ordinal) >= 0
			|| corpus.IndexOf("UNI-STRUT", StringComparison.Ordinal) >= 0
			|| corpus.IndexOf("UNI STRUT", StringComparison.Ordinal) >= 0
			|| corpus.IndexOf("STRUT", StringComparison.Ordinal) >= 0;
	}

	private static void SetFabricationItemNumber(FabricationPart part, string itemNumber)
	{
		Parameter val = part?.get_Parameter((BuiltInParameter)(-1140975));
		if (val == null || ((APIObject)val).IsReadOnly)
		{
			return;
		}

		try
		{
			val.Set(itemNumber ?? string.Empty);
		}
		catch
		{
		}
	}

	private static bool IsMmcStraightFabricationPart(FabricationPart part)
	{
		return GetFabricationSortPriority(part) == 0;
	}

	private static void SetFabricationItemNumberOnGroup(IGrouping<string, FabricationPart> group, string itemNumber)
	{
		foreach (FabricationPart item in group)
		{
			Parameter val = item.get_Parameter((BuiltInParameter)(-1140975));
			if (val != null && !((APIObject)val).IsReadOnly)
			{
				try
				{
					val.Set(itemNumber);
				}
				catch
				{
				}
			}
		}
	}

	private static void ClearFabricationItemNumber(FabricationPart part)
	{
		Parameter val = ((part != null) ? part.get_Parameter((BuiltInParameter)(-1140975)) : null);
		if (val == null || ((APIObject)val).IsReadOnly)
		{
			return;
		}
		try
		{
			val.Set(string.Empty);
		}
		catch
		{
		}
	}

	private static int GetFabricationSortPriority(FabricationPart part)
	{
		return FabricationPartClassification.GetFabricationSortPriority(part, ((Element)part)?.Document);
	}

	private static bool IsPipeLikePart(FabricationPart part)
	{
		if (part == null)
		{
			return false;
		}
		if ((((Element)part).Name ?? string.Empty).Trim().IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		string partParameterValue = GetPartParameterValue((Element)(object)part, "Alias");
		if (!string.IsNullOrWhiteSpace(partParameterValue) && (partParameterValue.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0 || partParameterValue.IndexOf("straight", StringComparison.OrdinalIgnoreCase) >= 0))
		{
			return true;
		}
		string partParameterValue2 = GetPartParameterValue((Element)(object)part, "Product Entry");
		if (!string.IsNullOrWhiteSpace(partParameterValue2))
		{
			if (partParameterValue2.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return partParameterValue2.IndexOf("straight", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			return true;
		}
		return false;
	}

	private static string GetFabricationItemGroupingKey(FabricationPart part)
	{
		List<string> values = new List<string>
		{
			NormalizeGroupingToken((part != null) ? ((Element)part).Name : null),
			NormalizeGroupingToken(GetPartParameterValue((Element)(object)part, "Size")),
			NormalizeGroupingToken(GetPartParameterValue((Element)(object)part, "Product Entry")),
			NormalizeGroupingToken(GetPartParameterValue((Element)(object)part, "Alias")),
			NormalizeGroupingToken(GetPartParameterValue((Element)(object)part, "Service Type")),
			NormalizeGroupingToken(GetPartParameterValue((Element)(object)part, "Length")),
			NormalizeGroupingToken(GetPartParameterValue((Element)(object)part, "Angle"))
		};
		if (IsPipeLikePart(part))
		{
			double geomLen = GetFabricationStraightLineLength(part);
			if (geomLen > 1.0 / 24.0)
			{
				values.Add(NormalizeGroupingToken((Math.Round(geomLen * 96.0) / 96.0).ToString("F4")));
			}
		}
		return string.Join("|", values);
	}

	private static string GetPartParameterValue(Element element, string parameterName)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Invalid comparison between Unknown and I4
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Invalid comparison between Unknown and I4
		if (element == null || string.IsNullOrWhiteSpace(parameterName))
		{
			return string.Empty;
		}
		try
		{
			Parameter val = element.LookupParameter(parameterName);
			if (val == null)
			{
				return string.Empty;
			}
			string text = val.AsValueString();
			if (!string.IsNullOrWhiteSpace(text))
			{
				return text;
			}
			text = val.AsString();
			if (!string.IsNullOrWhiteSpace(text))
			{
				return text;
			}
			if ((int)val.StorageType == 2)
			{
				return val.AsDouble().ToString("0.######");
			}
			if ((int)val.StorageType == 1)
			{
				return val.AsInteger().ToString();
			}
		}
		catch
		{
		}
		return string.Empty;
	}

	private static string NormalizeGroupingToken(string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim().ToUpperInvariant();
		}
		return string.Empty;
	}

	internal static string GetFabricationItemNumber(FabricationPart part)
	{
		if (part == null)
		{
			return string.Empty;
		}
		Parameter val = part.get_Parameter((BuiltInParameter)(-1140975));
		if (val == null)
		{
			return string.Empty;
		}
		string text = val.AsString();
		if (string.IsNullOrWhiteSpace(text))
		{
			text = val.AsValueString();
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}
		return string.Empty;
	}

	internal static HashSet<string> GetExistingTaggedItemNumbers(Document doc, View view)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (doc == null || view == null)
		{
			return hashSet;
		}
		IEnumerable<IndependentTag> enumerable;
		try
		{
			enumerable = ((IEnumerable)new FilteredElementCollector(doc, ((Element)view).Id).OfClass(typeof(IndependentTag))).Cast<IndependentTag>().ToList();
		}
		catch
		{
			return hashSet;
		}
		foreach (IndependentTag item in enumerable)
		{
			string independentTagText = GetIndependentTagText(item);
			if (string.IsNullOrWhiteSpace(independentTagText))
			{
				continue;
			}
			string[] array = independentTagText.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < array.Length; i++)
			{
				string text = array[i].Trim();
				if (!string.IsNullOrWhiteSpace(text))
				{
					hashSet.Add(text);
				}
			}
		}
		return hashSet;
	}

	internal static IEnumerable<View> FindAssemblyViews(Document doc, AssemblyInstance assembly)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		if (doc == null || assembly == null)
		{
			return Enumerable.Empty<View>();
		}
		return (from View x in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))
			where x != null && !x.IsTemplate && x.IsAssemblyView && x.AssociatedAssemblyInstanceId == ((Element)assembly).Id && !(x is ViewSchedule)
			orderby ((object)x.ViewType/*cast due to .constrained prefix*/).ToString(), ((Element)x).Name
			select x).ToList();
	}

	private static int CompareSpoolSheetOrder(ViewSheet a, ViewSheet b)
	{
		if (a == b)
		{
			return 0;
		}
		string strA = ((a != null) ? a.SheetNumber : null) ?? string.Empty;
		string strB = ((b != null) ? b.SheetNumber : null) ?? string.Empty;
		int num = string.Compare(strA, strB, StringComparison.OrdinalIgnoreCase);
		if (num != 0)
		{
			return num;
		}
		string strA2 = ((a != null) ? ((Element)a).Name : null) ?? string.Empty;
		string strB2 = ((b != null) ? ((Element)b).Name : null) ?? string.Empty;
		return string.Compare(strA2, strB2, StringComparison.OrdinalIgnoreCase);
	}

	private static ViewSheet PickBestSpoolSheet(IReadOnlyCollection<ViewSheet> sheets)
	{
		if (sheets == null || sheets.Count == 0)
		{
			return null;
		}
		ViewSheet val = null;
		foreach (ViewSheet sheet in sheets)
		{
			if (sheet != null && (val == null || CompareSpoolSheetOrder(sheet, val) < 0))
			{
				val = sheet;
			}
		}
		return val;
	}

	internal static Dictionary<ElementId, ViewSheet> FindSpoolSheetsForAssemblies(Document doc, bool regularSheetBranch, ICollection<ElementId> assemblyInstanceIds)
	{
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		Dictionary<ElementId, ViewSheet> dictionary = new Dictionary<ElementId, ViewSheet>();
		if (doc == null || assemblyInstanceIds == null || assemblyInstanceIds.Count == 0)
		{
			return dictionary;
		}
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		foreach (ElementId assemblyInstanceId in assemblyInstanceIds)
		{
			if (assemblyInstanceId != (ElementId)null && assemblyInstanceId != ElementId.InvalidElementId)
			{
				hashSet.Add(assemblyInstanceId);
			}
		}
		if (hashSet.Count == 0)
		{
			return dictionary;
		}
		if (!regularSheetBranch)
		{
			Dictionary<ElementId, List<ViewSheet>> dictionary2 = new Dictionary<ElementId, List<ViewSheet>>();
			foreach (ViewSheet item in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))).Cast<ViewSheet>())
			{
				if (item == null || !((View)item).IsAssemblyView)
				{
					continue;
				}
				ElementId associatedAssemblyInstanceId = ((View)item).AssociatedAssemblyInstanceId;
				if (!(associatedAssemblyInstanceId == (ElementId)null) && !(associatedAssemblyInstanceId == ElementId.InvalidElementId) && hashSet.Contains(associatedAssemblyInstanceId))
				{
					if (!dictionary2.TryGetValue(associatedAssemblyInstanceId, out var value))
					{
						value = (dictionary2[associatedAssemblyInstanceId] = new List<ViewSheet>());
					}
					value.Add(item);
				}
			}
			{
				foreach (ElementId item2 in hashSet)
				{
					if (dictionary2.TryGetValue(item2, out var value2))
					{
						ViewSheet val = PickBestSpoolSheet(value2);
						if (val != null)
						{
							dictionary[item2] = val;
						}
					}
				}
				return dictionary;
			}
		}
		Dictionary<ElementId, ElementId> dictionary3 = new Dictionary<ElementId, ElementId>();
		foreach (View item3 in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))).Cast<View>())
		{
			if (item3 == null || !item3.IsAssemblyView)
			{
				continue;
			}
			ElementId associatedAssemblyInstanceId2 = item3.AssociatedAssemblyInstanceId;
			if (!(associatedAssemblyInstanceId2 == (ElementId)null) && !(associatedAssemblyInstanceId2 == ElementId.InvalidElementId) && hashSet.Contains(associatedAssemblyInstanceId2))
			{
				ElementId id = ((Element)item3).Id;
				if (id != (ElementId)null && id != ElementId.InvalidElementId)
				{
					dictionary3[id] = associatedAssemblyInstanceId2;
				}
			}
		}
		Dictionary<ElementId, List<ViewSheet>> dictionary4 = new Dictionary<ElementId, List<ViewSheet>>();
		foreach (Viewport item4 in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(Viewport))).Cast<Viewport>())
		{
			if (item4 == null)
			{
				continue;
			}
			ElementId viewId = item4.ViewId;
			if (viewId == (ElementId)null || viewId == ElementId.InvalidElementId || !dictionary3.TryGetValue(viewId, out var value3))
			{
				continue;
			}
			Element element = doc.GetElement(item4.SheetId);
			ViewSheet val2 = (ViewSheet)(object)((element is ViewSheet) ? element : null);
			if (val2 != null && !((View)val2).IsAssemblyView)
			{
				if (!dictionary4.TryGetValue(value3, out var value4))
				{
					value4 = (dictionary4[value3] = new List<ViewSheet>());
				}
				value4.Add(val2);
			}
		}
		foreach (ElementId item5 in hashSet)
		{
			if (dictionary4.TryGetValue(item5, out var value5))
			{
				ViewSheet val3 = PickBestSpoolSheet(value5);
				if (val3 != null)
				{
					dictionary[item5] = val3;
				}
			}
		}
		return dictionary;
	}

	internal static ViewSheet FindSpoolSheet(Document doc, ElementId assemblyId, bool regularSheetBranch)
	{
		if (doc == null || assemblyId == (ElementId)null || assemblyId == ElementId.InvalidElementId)
		{
			return null;
		}
		FindSpoolSheetsForAssemblies(doc, regularSheetBranch, (ICollection<ElementId>)(object)new ElementId[1] { assemblyId }).TryGetValue(assemblyId, out var value);
		return value;
	}

	internal static HashSet<ElementId> GetAssemblyInstanceIdsHavingSpoolSheet(Document doc, bool regularSheetBranch, ICollection<ElementId> displayedAssemblyInstanceIds)
	{
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0151: Unknown result type (might be due to invalid IL or missing references)
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		if (doc == null)
		{
			return hashSet;
		}
		if (displayedAssemblyInstanceIds != null && displayedAssemblyInstanceIds.Count == 0)
		{
			return hashSet;
		}
		HashSet<ElementId> hashSet2 = ((displayedAssemblyInstanceIds != null && displayedAssemblyInstanceIds.Count > 0) ? new HashSet<ElementId>(displayedAssemblyInstanceIds) : null);
		if (!regularSheetBranch)
		{
			foreach (ViewSheet item in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))).Cast<ViewSheet>())
			{
				if (item != null && ((View)item).IsAssemblyView)
				{
					ElementId associatedAssemblyInstanceId = ((View)item).AssociatedAssemblyInstanceId;
					if (!(associatedAssemblyInstanceId == (ElementId)null) && !(associatedAssemblyInstanceId == ElementId.InvalidElementId) && (hashSet2 == null || hashSet2.Contains(associatedAssemblyInstanceId)))
					{
						hashSet.Add(associatedAssemblyInstanceId);
					}
				}
			}
			return hashSet;
		}
		HashSet<ElementId> hashSet3 = new HashSet<ElementId>();
		foreach (Viewport item2 in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(Viewport))).Cast<Viewport>())
		{
			if (item2 == null)
			{
				continue;
			}
			Element element = doc.GetElement(item2.SheetId);
			ViewSheet val = (ViewSheet)(object)((element is ViewSheet) ? element : null);
			if (val != null && !((View)val).IsAssemblyView)
			{
				ElementId viewId = item2.ViewId;
				if (viewId != (ElementId)null && viewId != ElementId.InvalidElementId)
				{
					hashSet3.Add(viewId);
				}
			}
		}
		foreach (View item3 in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))).Cast<View>())
		{
			if (item3 == null || !item3.IsAssemblyView)
			{
				continue;
			}
			ElementId associatedAssemblyInstanceId2 = item3.AssociatedAssemblyInstanceId;
			if (!(associatedAssemblyInstanceId2 == (ElementId)null) && !(associatedAssemblyInstanceId2 == ElementId.InvalidElementId) && (hashSet2 == null || hashSet2.Contains(associatedAssemblyInstanceId2)))
			{
				ElementId id = ((Element)item3).Id;
				if (id != (ElementId)null && id != ElementId.InvalidElementId && hashSet3.Contains(id))
				{
					hashSet.Add(associatedAssemblyInstanceId2);
				}
			}
		}
		return hashSet;
	}

	internal static bool TryGetExistingViewTagSettings(View view, SpoolingManagerSettings settings, out string placement, out bool tagEnabled)
	{
		string rotation;
		return TryGetExistingViewSheetSettings(view, settings, out placement, out tagEnabled, out rotation);
	}

	private static bool DoesViewMatchBuildOption(View view, ViewBuildOption option)
	{
		if (view == null || option == null)
		{
			return false;
		}
		if (string.Equals(option.Label, "3D Ortho", StringComparison.OrdinalIgnoreCase))
		{
			return view is View3D;
		}
		if (view is View3D)
		{
			return false;
		}
		string text = (((Element)view).Name ?? string.Empty).ToUpperInvariant();
		if (string.Equals(option.Label, "Back View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("BACK");
		}
		if (string.Equals(option.Label, "Front View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("FRONT");
		}
		if (string.Equals(option.Label, "Left View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("LEFT");
		}
		if (string.Equals(option.Label, "Right View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("RIGHT");
		}
		if (string.Equals(option.Label, "Top View", StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains("TOP");
		}
		return false;
	}

	internal static bool TryGetExistingViewSheetSettings(View view, SpoolingManagerSettings settings, out string placement, out bool tagEnabled, out string rotation)
	{
		placement = "Middle Left";
		tagEnabled = false;
		rotation = "0°";
		if (view == null || settings == null)
		{
			return false;
		}
		if (view is View3D)
		{
			placement = settings.Placement3D;
			tagEnabled = settings.Tag3D;
			return true;
		}
		string text = (((Element)view).Name ?? string.Empty).ToUpperInvariant();
		if (text.Contains("BACK"))
		{
			placement = settings.PlacementBackView;
			tagEnabled = settings.TagBackView;
			rotation = settings.BackViewRotation;
			return true;
		}
		if (text.Contains("FRONT"))
		{
			placement = settings.PlacementFrontView;
			tagEnabled = settings.TagFrontView;
			rotation = settings.FrontViewRotation;
			return true;
		}
		if (text.Contains("LEFT"))
		{
			placement = settings.PlacementLeftView;
			tagEnabled = settings.TagLeftView;
			rotation = settings.LeftViewRotation;
			return true;
		}
		if (text.Contains("RIGHT"))
		{
			placement = settings.PlacementRightView;
			tagEnabled = settings.TagRightView;
			rotation = settings.RightViewRotation;
			return true;
		}
		if (text.Contains("TOP"))
		{
			placement = settings.PlacementTopView;
			tagEnabled = settings.TagTopView;
			rotation = settings.TopViewRotation;
			return true;
		}
		return false;
	}

	internal static void TryRecenterSheetViewportForView(Document doc, ViewSheet sheet, View view, string placement)
	{
		if (doc == null || sheet == null || view == null)
		{
			return;
		}
		foreach (ElementId allViewport in sheet.GetAllViewports())
		{
			Element element = doc.GetElement(allViewport);
			Viewport val = (Viewport)(object)((element is Viewport) ? element : null);
			if (val != null && !(val.ViewId != ((Element)view).Id))
			{
				RecenterViewport(doc, sheet, val, placement);
			}
		}
	}

	private static string GetIndependentTagText(IndependentTag tag)
	{
		if (tag == null)
		{
			return string.Empty;
		}
		try
		{
			PropertyInfo property = ((object)tag).GetType().GetProperty("TagText");
			if (property != null)
			{
				object value = property.GetValue(tag);
				if (value != null)
				{
					return value.ToString();
				}
			}
		}
		catch
		{
		}
		string partParameterValue = GetPartParameterValue((Element)(object)tag, "Tag #");
		if (!string.IsNullOrWhiteSpace(partParameterValue))
		{
			return partParameterValue;
		}
		return string.Empty;
	}

	private static FamilySymbol FindTitleBlock(Document doc, string displayName)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		return ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))).Cast<FamilySymbol>().FirstOrDefault((FamilySymbol x) => ((Element)x).Category != null && ((Element)x).Category.Id.Value == -2000280L && string.Equals(((ElementType)x).FamilyName + " : " + ((Element)x).Name, displayName, StringComparison.OrdinalIgnoreCase));
	}

	internal static FamilySymbol FindTagType(Document doc, string displayName)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		return ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))).Cast<FamilySymbol>().FirstOrDefault((FamilySymbol x) => string.Equals(((ElementType)x).FamilyName + " : " + ((Element)x).Name, displayName, StringComparison.OrdinalIgnoreCase));
	}

	private static ElementType FindViewportType(Document doc, string typeName)
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrWhiteSpace(typeName))
		{
			return null;
		}
		return ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ElementType))).Cast<ElementType>().FirstOrDefault((ElementType x) => !string.IsNullOrWhiteSpace(x.FamilyName) && x.FamilyName.IndexOf("Viewport", StringComparison.OrdinalIgnoreCase) >= 0 && string.Equals(((Element)x).Name, typeName, StringComparison.OrdinalIgnoreCase));
	}

	private static ViewSchedule FindSchedule(Document doc, string scheduleName)
	{
		if (string.IsNullOrWhiteSpace(scheduleName))
		{
			return null;
		}
		ViewSchedule result = null;
		foreach (ViewSchedule item in ScheduleViewEnumeration.EnumerateViewSchedules(doc))
		{
			if (item != null && string.Equals(((Element)item).Name, scheduleName, StringComparison.OrdinalIgnoreCase))
			{
				if (!((View)item).IsTemplate)
				{
					return item;
				}
				result = item;
			}
		}
		return result;
	}

	private static View FindViewTemplate(Document doc, string templateName)
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrWhiteSpace(templateName))
		{
			return null;
		}
		return ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(View))).Cast<View>().FirstOrDefault((View x) => x.IsTemplate && string.Equals(((Element)x).Name, templateName, StringComparison.OrdinalIgnoreCase));
	}

	private static bool TryApplySpoolAssemblyAutoDimensions(Document doc, View view, AssemblyInstance assembly, SpoolingManagerSettings spoolSettings, out string diagnostic)
	{
		diagnostic = null;
		if (doc == null || view == null || assembly == null)
		{
			return false;
		}
		List<FabricationPart> fabricationParts = GetAssemblyFabricationParts(doc, assembly)
			.Where((p) => !IsGasketPart(p) && !IsWeldPart(p))
			.ToList();
		if (fabricationParts.Count > 0)
		{
			if (TryFabricationSpoolAutoDimensionsWork(doc, view, assembly, fabricationParts, spoolSettings, out string fabricationDiagnostic))
			{
				return true;
			}
			diagnostic = string.IsNullOrEmpty(fabricationDiagnostic)
				? "Auto-dimension could not place a model dimension on fabrication members (check Auto Dim is on, open pipe end exists, and view is not 3D)."
				: fabricationDiagnostic;
			return false;
		}
		if (TryNativeMepSpoolAutoDimensions(doc, view, assembly, spoolSettings, out diagnostic))
		{
			return true;
		}
		if (string.IsNullOrEmpty(diagnostic))
		{
			diagnostic = "Auto-dimension skipped: assembly has no fabrication parts or pipe/fitting elements to measure.";
		}
		return false;
	}

	private static bool TryFabricationSpoolAutoDimensionsWork(Document doc, View view, AssemblyInstance assembly, List<FabricationPart> parts, SpoolingManagerSettings spoolSettings, out string diagnostic)
	{
		diagnostic = null;
		if (doc == null || view == null || assembly == null)
		{
			diagnostic = "Auto-dimension: missing document, view, or assembly.";
			return false;
		}
		if (parts == null || parts.Count == 0)
		{
			diagnostic = "Auto-dimension: assembly has no fabrication parts to measure.";
			return false;
		}
		if (view is View3D || view.IsTemplate)
		{
			diagnostic = "Auto-dimension: requires a non-template 2D spool view.";
			return false;
		}
		List<FabricationPart> allParts = parts;
		List<FabricationPart> viewParts = FilterLocalFabricationPartsForView(doc, view, allParts);
		viewParts = ExpandFilteredPartsWithFlangeConnections(allParts, viewParts, doc);
		viewParts = EnsureOletPartsForAutoDim(allParts, viewParts);
		if (viewParts == null || viewParts.Count == 0)
		{
			diagnostic = "Auto-dimension: no fabrication parts are visible in this view.";
			return false;
		}
		if (!TryGetRunAxisInViewPlane(view, viewParts, out XYZ unitAxis))
		{
			XYZ viewDirection = view.ViewDirection;
			if (viewDirection == null || viewDirection.GetLength() < 1E-09)
			{
				diagnostic = "Auto-dimension: view has no valid view direction, so the pipe run axis could not be resolved.";
				return false;
			}
			viewDirection = viewDirection.Normalize();
			XYZ rightDirection = view.RightDirection;
			if (rightDirection == null || rightDirection.GetLength() < 1E-09)
			{
				diagnostic = "Auto-dimension: view has no valid right direction, so the pipe run axis could not be resolved.";
				return false;
			}
			rightDirection = rightDirection.Normalize();
			XYZ projected = rightDirection - viewDirection * rightDirection.DotProduct(viewDirection);
			if (projected.GetLength() < 1E-09)
			{
				diagnostic = "Auto-dimension: could not project a horizontal axis in the view plane for the pipe run.";
				return false;
			}
			unitAxis = projected.Normalize();
		}
		List<string> failureNotes = new List<string>();
		int placed = TryApplyIntelligentFabricationDimensionRules(
			doc, view, assembly, viewParts, allParts, unitAxis, spoolSettings, failureNotes);
		// Final sheet-law sweep: nothing at an angle to the title block may remain.
		try { DeleteAnyRemainingTiltedLinearDimensions(doc, view); } catch { }
		if (placed > 0)
		{
			return true;
		}
		diagnostic = failureNotes.Count > 0
			? string.Join("; ", failureNotes)
			: "Auto-dimension rules: no dimension intents could be placed in this view.";
		return false;
	}

	private static bool TryApplyFabricationSpoolAutoDimensions(Document doc, View view, AssemblyInstance assembly)
	{
		string diagnostic;
		return TryApplySpoolAssemblyAutoDimensions(doc, view, assembly, null, out diagnostic);
	}

	private static bool TryNativeMepSpoolAutoDimensions(Document doc, View view, AssemblyInstance assembly, SpoolingManagerSettings spoolSettings, out string diagnostic)
	{
		diagnostic = null;
		List<Element> list = (from id in assembly.GetMemberIds()
			select doc.GetElement(id) into e
			where e != null && e.Category != null
			select e).ToList();
		List<Element> list2 = list.Where(IsNativePipeElement).ToList();
		List<Element> list3 = list.Where(IsNativePipeFittingElement).ToList();
		if (list2.Count == 0 || list3.Count == 0)
		{
			return false;
		}
		Element val = list2.OrderByDescending(GetMepCurveLineLength).FirstOrDefault();
		if (val == null || GetMepCurveLineLength(val) < 1.0 / 24.0)
		{
			return false;
		}
		Line mepCenterLine = GetMepCenterLine(val);
		if ((GeometryObject)(object)mepCenterLine == (GeometryObject)null)
		{
			return false;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			return false;
		}
		vn = vn.Normalize();
		XYZ val2 = mepCenterLine.Direction.Normalize();
		val2 -= vn * val2.DotProduct(vn);
		if (val2.GetLength() < 1E-09)
		{
			XYZ rightDirection = view.RightDirection;
			if (rightDirection == null || rightDirection.GetLength() < 1E-09)
			{
				return false;
			}
			rightDirection = rightDirection.Normalize();
			val2 = rightDirection - vn * rightDirection.DotProduct(vn);
			if (val2.GetLength() < 1E-09)
			{
				return false;
			}
		}
		XYZ unitAxis = val2.Normalize();
		Element val3 = (from f in list3
			select new
			{
				F = f,
				C = GetElementApproxCenter(f)
			} into x
			where x.C != null
			orderby DotInPlane(x.C, unitAxis, vn)
			select x.F).FirstOrDefault();
		if (val3 == null)
		{
			return false;
		}
		XYZ elementApproxCenter = GetElementApproxCenter(val3);
		if (elementApproxCenter == null)
		{
			return false;
		}
		if (!TryGetNativePipeOpenEnd(val, list, unitAxis, vn, out var pipeEndPt))
		{
			return false;
		}
		double num = DotInPlane(pipeEndPt, unitAxis, vn);
		double num2 = DotInPlane(elementApproxCenter, unitAxis, vn);
		if (num < num2)
		{
			unitAxis = unitAxis.Negate();
			if (!TryGetNativePipeOpenEnd(val, list, unitAxis, vn, out pipeEndPt))
			{
				return false;
			}
			elementApproxCenter = GetElementApproxCenter(val3);
			if (elementApproxCenter == null)
			{
				return false;
			}
		}
		if (pipeEndPt.DistanceTo(elementApproxCenter) < 1.0 / 24.0)
		{
			return false;
		}
		int stackIndex = 0;
		string failureDetail;
		return TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, val3, elementApproxCenter, val, pipeEndPt, spoolSettings, ref stackIndex, out failureDetail);
	}

	private static bool IsNativePipeElement(Element e)
	{
		if (((e != null) ? e.Category : null) == null)
		{
			return false;
		}
		try
		{
			return (int)e.Category.Id.Value == -2008044;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsNativePipeFittingElement(Element e)
	{
		if (((e != null) ? e.Category : null) == null)
		{
			return false;
		}
		try
		{
			return (int)e.Category.Id.Value == -2008049;
		}
		catch
		{
			return false;
		}
	}

	private static Line GetMepCenterLine(Element e)
	{
		MEPCurve val = (MEPCurve)(object)((e is MEPCurve) ? e : null);
		if (val != null)
		{
			Location location = ((Element)val).Location;
			LocationCurve val2 = (LocationCurve)(object)((location is LocationCurve) ? location : null);
			if (val2 != null)
			{
				Curve curve = val2.Curve;
				Line val3 = (Line)(object)((curve is Line) ? curve : null);
				if (val3 != null)
				{
					return val3;
				}
			}
		}
		return null;
	}

	private static double GetMepCurveLineLength(Element e)
	{
		Line mepCenterLine = GetMepCenterLine(e);
		if ((GeometryObject)(object)mepCenterLine == (GeometryObject)null)
		{
			return 0.0;
		}
		try
		{
			return ((Curve)mepCenterLine).Length;
		}
		catch
		{
			return 0.0;
		}
	}

	private static XYZ GetElementApproxCenter(Element e)
	{
		if (e == null)
		{
			return null;
		}
		try
		{
			Location location = e.Location;
			LocationPoint val = (LocationPoint)(object)((location is LocationPoint) ? location : null);
			if (val != null)
			{
				return val.Point;
			}
		}
		catch
		{
		}
		Line mepCenterLine = GetMepCenterLine(e);
		if ((GeometryObject)(object)mepCenterLine != (GeometryObject)null)
		{
			return (((Curve)mepCenterLine).GetEndPoint(0) + ((Curve)mepCenterLine).GetEndPoint(1)) * 0.5;
		}
		try
		{
			BoundingBoxXYZ val2 = e.get_BoundingBox(null);
			if (val2 != null)
			{
				return (val2.Min + val2.Max) * 0.5;
			}
		}
		catch
		{
		}
		return null;
	}

	private static List<Connector> ListConnectorsForElement(Element element)
	{
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Expected O, but got Unknown
		List<Connector> list = new List<Connector>();
		FabricationPart val = (FabricationPart)(object)((element is FabricationPart) ? element : null);
		if (val != null)
		{
			return ListConnectors(val);
		}
		ConnectorManager val2 = null;
		MEPCurve val3 = (MEPCurve)(object)((element is MEPCurve) ? element : null);
		if (val3 != null)
		{
			val2 = val3.ConnectorManager;
		}
		else
		{
			FamilyInstance val4 = (FamilyInstance)(object)((element is FamilyInstance) ? element : null);
			if (val4 != null)
			{
				try
				{
					if (val4.MEPModel != null)
					{
						val2 = val4.MEPModel.ConnectorManager;
					}
				}
				catch
				{
				}
			}
		}
		if (val2 == null)
		{
			return list;
		}
		foreach (Connector connector in val2.Connectors)
		{
			Connector val5 = connector;
			if (((val5 != null) ? val5.Origin : null) != null)
			{
				list.Add(val5);
			}
		}
		return list;
	}

	private static Element FindMateAtConnectorElement(Element self, Connector connector, List<Element> pool)
	{
		if (self == null || ((connector != null) ? connector.Origin : null) == null)
		{
			return null;
		}
		XYZ origin = connector.Origin;
		foreach (Element item in pool)
		{
			if (item == null || item.Id == self.Id)
			{
				continue;
			}
			foreach (Connector item2 in ListConnectorsForElement(item))
			{
				if (((item2 != null) ? item2.Origin : null) != null && origin.DistanceTo(item2.Origin) < 0.08)
				{
					return item;
				}
			}
		}
		return null;
	}

	private static bool TryGetNativePipeOpenEnd(Element pipeElem, List<Element> members, XYZ unitAxis, XYZ vn, out XYZ pipeEndPt)
	{
		pipeEndPt = null;
		double num = double.MinValue;
		foreach (Connector item in ListConnectorsForElement(pipeElem))
		{
			if (((item != null) ? item.Origin : null) != null && FindMateAtConnectorElement(pipeElem, item, members) == null)
			{
				double num2 = DotInPlane(item.Origin, unitAxis, vn);
				if (num2 > num)
				{
					num = num2;
					pipeEndPt = item.Origin;
				}
			}
		}
		if (pipeEndPt != null)
		{
			return true;
		}
		foreach (Connector item2 in ListConnectorsForElement(pipeElem))
		{
			if (((item2 != null) ? item2.Origin : null) != null)
			{
				double num3 = DotInPlane(item2.Origin, unitAxis, vn);
				if (num3 > num)
				{
					num = num3;
					pipeEndPt = item2.Origin;
				}
			}
		}
		if (pipeEndPt != null)
		{
			return true;
		}
		Line mepCenterLine = GetMepCenterLine(pipeElem);
		if ((GeometryObject)(object)mepCenterLine != (GeometryObject)null)
		{
			XYZ endPoint = ((Curve)mepCenterLine).GetEndPoint(0);
			XYZ endPoint2 = ((Curve)mepCenterLine).GetEndPoint(1);
			double num4 = DotInPlane(endPoint, unitAxis, vn);
			double num5 = DotInPlane(endPoint2, unitAxis, vn);
			pipeEndPt = ((num4 >= num5) ? endPoint : endPoint2);
			return true;
		}
		return false;
	}

	private static bool TryCreateSpoolPipeEndToTargetDimension(Document doc, View view, List<FabricationPart> parts, XYZ unitAxis, SpoolingManagerSettings spoolSettings, Func<FabricationPart, bool> targetFilter, string ruleLabel, ref int stackIndex, out string diagnostic)
	{
		diagnostic = null;
		FabricationPart pipePart = GetDominantStraightFabricationPart(parts);
		if (pipePart == null)
		{
			diagnostic = "Auto-dimension (" + ruleLabel + "): no dominant straight fabrication pipe run was found in the assembly.";
			return false;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (" + ruleLabel + "): view direction is invalid.";
			return false;
		}
		vn = vn.Normalize();
		List<FabricationPart> source = parts.Where((FabricationPart p) => ((Element)p).Id != ((Element)pipePart).Id && !IsGasketPart(p) && !IsWeldPart(p)).ToList();
		List<FabricationPart> list = source.Where(targetFilter).ToList();
		if (!TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out var endOwner, out var pipeEndPt))
		{
			diagnostic = "Auto-dimension (" + ruleLabel + "): could not find an open (unconnected) pipe-end anchor along the run, or no connector projects past the fitting.";
			return false;
		}
		FabricationPart val = null;
		XYZ fabricationFittingDimensionAnchor = null;
		if (list.Count > 0)
		{
			val = (from p in list
				let anchor = GetFabricationFittingDimensionAnchor(p, pipePart, endOwner, parts)
				where anchor != null
				orderby ScalarAlong(p, unitAxis, vn)
				select p).FirstOrDefault();
			if (val != null)
			{
				fabricationFittingDimensionAnchor = GetFabricationFittingDimensionAnchor(val, pipePart, endOwner, parts);
			}
		}
		if (val == null || fabricationFittingDimensionAnchor == null)
		{
			diagnostic = "Auto-dimension (" + ruleLabel + "): no matching target part was found beside the dominant pipe run.";
			return false;
		}
		double num = DotInPlane(pipeEndPt, unitAxis, vn);
		double num2 = DotInPlane(fabricationFittingDimensionAnchor, unitAxis, vn);
		if (num < num2)
		{
			unitAxis = unitAxis.Negate();
			if (!TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out endOwner, out pipeEndPt))
			{
				diagnostic = "Auto-dimension (" + ruleLabel + "): after aligning run direction, pipe-end anchor could not be resolved.";
				return false;
			}
			fabricationFittingDimensionAnchor = GetFabricationFittingDimensionAnchor(val, pipePart, endOwner, parts);
			if (fabricationFittingDimensionAnchor == null)
			{
				diagnostic = "Auto-dimension (" + ruleLabel + "): could not re-resolve target center after flipping run axis.";
				return false;
			}
		}
		if (pipeEndPt.DistanceTo(fabricationFittingDimensionAnchor) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension (" + ruleLabel + "): pipe end is too close to the target center to place a meaningful dimension.";
			return false;
		}
		if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, (Element)(object)val, fabricationFittingDimensionAnchor, (Element)(object)endOwner, pipeEndPt, spoolSettings, ref stackIndex, out var failureDetail))
		{
			return true;
		}
		diagnostic = "Auto-dimension (" + ruleLabel + "): could not create a linear dimension in this 2D view.";
		if (!string.IsNullOrEmpty(failureDetail))
		{
			diagnostic = diagnostic + " " + failureDetail;
		}
		return false;
	}

	private static IEnumerable<XYZ> CollectFabricationPartSnapPoints(FabricationPart part, View view)
	{
		if (part == null)
		{
			yield break;
		}
		XYZ origin = TryGetFabricationPartOrigin(part);
		if (origin != null)
		{
			yield return origin;
		}
		foreach (Connector connector in ListConnectors(part))
		{
			if (((connector != null) ? connector.Origin : null) != null)
			{
				yield return connector.Origin;
			}
		}
		foreach (XYZ vertex in CollectGeometryVertexSnapPoints((Element)(object)part, view))
		{
			yield return vertex;
		}
	}

	private static IEnumerable<XYZ> CollectGeometryVertexSnapPoints(Element element, View view)
	{
		List<XYZ> points = new List<XYZ>();
		if (element == null)
		{
			return points;
		}
		XYZ viewNormal = null;
		if (view != null)
		{
			try
			{
				viewNormal = view.ViewDirection;
				if (viewNormal != null && viewNormal.GetLength() > 1E-09)
				{
					viewNormal = viewNormal.Normalize();
				}
			}
			catch
			{
				viewNormal = null;
			}
		}
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (Options item in BuildModelGeometryOptionsForReferenceExtraction())
		{
			GeometryElement val = null;
			try
			{
				val = element.get_Geometry(item);
			}
			catch
			{
				val = null;
			}
			if ((GeometryObject)(object)val == (GeometryObject)null)
			{
				continue;
			}
			CollectGeometryVertexSnapPointsFromGeometry(val, Transform.Identity, viewNormal, seen, points);
		}
		return points;
	}

	private static void CollectGeometryVertexSnapPointsFromGeometry(GeometryElement geo, Transform localToWorld, XYZ viewNormal, HashSet<string> seen, List<XYZ> points)
	{
		if ((GeometryObject)(object)geo == (GeometryObject)null || localToWorld == null || points == null)
		{
			return;
		}
		foreach (GeometryObject item in geo)
		{
			Solid val = (Solid)(object)((item is Solid) ? item : null);
			if (val != null && val.Edges != null)
			{
				foreach (Edge edge in val.Edges)
				{
					Edge val2 = edge;
					Curve val3 = null;
					try
					{
						val3 = val2.AsCurve();
					}
					catch
					{
						val3 = null;
					}
					if ((GeometryObject)(object)val3 == (GeometryObject)null || !val3.IsBound)
					{
						continue;
					}
					AddUniqueGeometrySnapPoint(points, seen, localToWorld.OfPoint(val3.GetEndPoint(0)));
					AddUniqueGeometrySnapPoint(points, seen, localToWorld.OfPoint(val3.GetEndPoint(1)));
				}
				continue;
			}
			Curve val4 = (Curve)(object)((item is Curve) ? item : null);
			if (val4 != null && val4.IsBound)
			{
				AddUniqueGeometrySnapPoint(points, seen, localToWorld.OfPoint(val4.GetEndPoint(0)));
				AddUniqueGeometrySnapPoint(points, seen, localToWorld.OfPoint(val4.GetEndPoint(1)));
				continue;
			}
			GeometryInstance val5 = (GeometryInstance)(object)((item is GeometryInstance) ? item : null);
			if (val5 == null)
			{
				continue;
			}
			Transform localToWorld2;
			try
			{
				localToWorld2 = localToWorld.Multiply(val5.Transform);
			}
			catch
			{
				continue;
			}
			GeometryElement val6 = null;
			try
			{
				val6 = val5.GetInstanceGeometry();
			}
			catch
			{
				val6 = null;
			}
			if ((GeometryObject)(object)val6 != (GeometryObject)null)
			{
				CollectGeometryVertexSnapPointsFromGeometry(val6, localToWorld2, viewNormal, seen, points);
			}
			GeometryElement val7 = null;
			try
			{
				val7 = val5.GetSymbolGeometry();
			}
			catch
			{
				val7 = null;
			}
			if ((GeometryObject)(object)val7 != (GeometryObject)null)
			{
				CollectGeometryVertexSnapPointsFromGeometry(val7, localToWorld2, viewNormal, seen, points);
			}
		}
	}

	private static void AddUniqueGeometrySnapPoint(List<XYZ> points, HashSet<string> seen, XYZ worldPoint)
	{
		if (worldPoint == null || points == null || seen == null)
		{
			return;
		}
		string item = string.Format("{0:F4}|{1:F4}|{2:F4}", worldPoint.X, worldPoint.Y, worldPoint.Z);
		if (!seen.Add(item))
		{
			return;
		}
		points.Add(worldPoint);
	}

	private static bool TryResolvePipeRunStartFittingAnchor(List<FabricationPart> parts, FabricationPart pipePart, FabricationPart openEndOwner, XYZ unitAxis, XYZ viewNormal, out FabricationPart owner, out XYZ point)
	{
		owner = null;
		point = null;
		if (pipePart == null || parts == null || parts.Count == 0)
		{
			return false;
		}
		double bestScalar = double.MaxValue;
		FabricationPart bestFitting = null;
		XYZ bestConnectorOrigin = null;
		foreach (Connector connector in ListConnectors(pipePart))
		{
			if (((connector != null) ? connector.Origin : null) == null)
			{
				continue;
			}
			FabricationPart mate = FindMateAtConnector(pipePart, connector, parts);
			if (mate == null || IsOletPart(mate) || IsGasketPart(mate) || IsWeldPart(mate) || !IsFittingLikeForSpoolDim(mate))
			{
				continue;
			}
			double scalar = DotInPlane(connector.Origin, unitAxis, viewNormal);
			if (scalar < bestScalar)
			{
				bestScalar = scalar;
				bestFitting = mate;
				bestConnectorOrigin = connector.Origin;
			}
		}
		if (bestFitting == null)
		{
			return false;
		}
		owner = bestFitting;
		point = GetFabricationFittingDimensionAnchor(bestFitting, pipePart, openEndOwner, parts);
		return point != null;
	}

	private static bool TryFindRunStartFittingForCollinearRun(
		IList<FabricationPart> parts,
		FabricationPart pipePart,
		XYZ unitAxis,
		XYZ viewNormal,
		out FabricationPart fitting,
		out XYZ point)
	{
		fitting = null;
		point = null;
		if (parts == null || pipePart == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}

		double bestScalar = double.MaxValue;
		foreach (FabricationPart candidate in parts)
		{
			if (candidate == null || IsGasketPart(candidate) || IsWeldPart(candidate) || IsOletPart(candidate) || IsValvePart(candidate))
			{
				continue;
			}

			if (!IsFittingLikeForSpoolDim(candidate) && !FabricationPartClassification.IsFlangePart(candidate, ((Element)candidate).Document))
			{
				continue;
			}

			if (!IsFittingMatedToCollinearRun(candidate, pipePart, parts))
			{
				continue;
			}

			XYZ anchor = GetFabricationFittingDimensionAnchor(candidate, pipePart, null, parts);
			if (anchor == null)
			{
				continue;
			}

			double scalar = DotInPlane(anchor, unitAxis, viewNormal);
			if (scalar < bestScalar)
			{
				bestScalar = scalar;
				fitting = candidate;
				point = anchor;
			}
		}

		return fitting != null && point != null;
	}

	private static bool IsFittingMatedToCollinearRun(FabricationPart fitting, FabricationPart pipePart, IList<FabricationPart> parts)
	{
		if (fitting == null)
		{
			return false;
		}
		if (pipePart != null)
		{
			foreach (Connector connector in ListConnectors(pipePart))
			{
				FabricationPart mate = FindMateAtConnector(pipePart, connector, parts);
				if (mate != null && ((Element)mate).Id == ((Element)fitting).Id)
				{
					return true;
				}
			}
		}
		return IsFittingOnCollinearRunNetwork(fitting, parts, pipePart, null, null);
	}

	/// <summary>True when this fitting mates (directly or through one joint) to any collinear run pipe — includes series elbows after a tee/olet branch.</summary>
	private static bool IsFittingOnCollinearRunNetwork(
		FabricationPart fitting,
		IList<FabricationPart> parts,
		FabricationPart runHint,
		XYZ unitAxis,
		XYZ viewNormal)
	{
		if (fitting == null || parts == null)
		{
			return false;
		}
		if (unitAxis == null || viewNormal == null)
		{
			if (runHint != null && TryGetFabricationLineDirection(runHint, out XYZ dir) && dir != null)
			{
				unitAxis = dir.Normalize();
				viewNormal = XYZ.BasisZ;
			}
			else
			{
				return false;
			}
		}
		foreach (FabricationPart mate in EnumerateMatedFabricationParts(fitting, parts))
		{
			if (IsPipeRunPart(mate) && !IsOletBranchTakeoffPipe(mate, parts)
				&& IsCollinearWithPrimaryRun(mate, parts, unitAxis, viewNormal))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsShortNonDominantRunPipe(FabricationPart legPipe, FabricationPart dominantPipe)
	{
		if (legPipe == null || !IsPipeRunPart(legPipe))
		{
			return false;
		}
		if (dominantPipe == null)
		{
			return GetFabricationStraightLineLength(legPipe) <= 12.0;
		}
		if (((Element)legPipe).Id == ((Element)dominantPipe).Id)
		{
			return false;
		}
		return GetFabricationStraightLineLength(legPipe) < GetFabricationStraightLineLength(dominantPipe) * 0.85;
	}

	private static bool IsRunAxisAlignedSpanInView(XYZ ptA, XYZ ptB, XYZ runAxis, XYZ viewNormal)
	{
		if (ptA == null || ptB == null || runAxis == null || viewNormal == null)
		{
			return false;
		}
		XYZ chord = ptB - ptA;
		XYZ chordInPlane = chord - viewNormal * chord.DotProduct(viewNormal);
		if (chordInPlane == null || chordInPlane.GetLength() < 1.0 / 24.0)
		{
			return false;
		}
		XYZ axis = runAxis - viewNormal * runAxis.DotProduct(viewNormal);
		if (axis == null || axis.GetLength() < 1E-09)
		{
			return false;
		}
		return Math.Abs(chordInPlane.Normalize().DotProduct(axis.Normalize())) > 0.85;
	}

	private static bool IsCollinearMainRunFittingPair(
		FabricationPart partA,
		FabricationPart partB,
		XYZ ptA,
		XYZ ptB,
		IList<FabricationPart> parts,
		XYZ primaryAxisInPlane,
		XYZ vn)
	{
		if (partA == null || partB == null || ptA == null || ptB == null || parts == null || primaryAxisInPlane == null)
		{
			return false;
		}
		if (!IsRunAxisAlignedSpanInView(ptA, ptB, primaryAxisInPlane, vn))
		{
			return false;
		}
		return IsFittingOnCollinearRunNetwork(partA, parts, null, primaryAxisInPlane, vn)
			&& IsFittingOnCollinearRunNetwork(partB, parts, null, primaryAxisInPlane, vn);
	}

	private static bool AreFittingsLinkedByCollinearRun(
		FabricationPart start,
		FabricationPart goal,
		IList<FabricationPart> parts,
		XYZ unitAxis,
		XYZ viewNormal)
	{
		if (start == null || goal == null || parts == null || ((Element)start).Id == ((Element)goal).Id)
		{
			return false;
		}
		Queue<(FabricationPart part, FabricationPart fromPipe)> queue = new Queue<(FabricationPart, FabricationPart)>();
		queue.Enqueue((start, null));
		HashSet<long> visited = new HashSet<long> { ((Element)start).Id.Value };
		while (queue.Count > 0)
		{
			(FabricationPart current, FabricationPart fromPipe) = queue.Dequeue();
			if (((Element)current).Id == ((Element)goal).Id)
			{
				return true;
			}
			if (IsPipeRunPart(current))
			{
				if (fromPipe != null && !IsCollinearWithPrimaryRun(current, parts, unitAxis, viewNormal))
				{
					continue;
				}
				foreach (FabricationPart mate in EnumerateMatedFabricationParts(current, parts))
				{
					if (mate == null || !visited.Add(((Element)mate).Id.Value))
					{
						continue;
					}
					if (IsFittingLikeForSpoolDim(mate) && !IsOletPart(mate) && !IsValvePart(mate)
						&& !FabricationPartClassification.IsFlangePart(mate, ((Element)mate).Document))
					{
						queue.Enqueue((mate, current));
					}
					else if (IsGasketPart(mate) || IsWeldPart(mate))
					{
						FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, parts);
						if (beyond != null && visited.Add(((Element)beyond).Id.Value))
						{
							queue.Enqueue((beyond, current));
						}
					}
				}
				continue;
			}
			foreach (FabricationPart mate in EnumerateMatedFabricationParts(current, parts))
			{
				if (mate == null || fromPipe != null && ((Element)mate).Id == ((Element)fromPipe).Id)
				{
					continue;
				}
				if (IsPipeRunPart(mate) && !IsOletBranchTakeoffPipe(mate, parts)
					&& IsCollinearWithPrimaryRun(mate, parts, unitAxis, viewNormal)
					&& visited.Add(((Element)mate).Id.Value))
				{
					queue.Enqueue((mate, current));
				}
				else if ((IsGasketPart(mate) || IsWeldPart(mate))
					&& FindFarSideMateThroughJoint(mate, current, parts) is FabricationPart beyond
					&& IsPipeRunPart(beyond) && visited.Add(((Element)beyond).Id.Value))
				{
					queue.Enqueue((beyond, current));
				}
			}
		}
		return false;
	}

	private static bool ShouldPlaceFittingPipeFittingCenterPair(
		FabricationPart partA,
		FabricationPart partB,
		XYZ ptA,
		XYZ ptB,
		FabricationPart legPipe,
		FabricationPart dominantPipe,
		IList<FabricationPart> parts,
		XYZ primaryAxisInPlane,
		XYZ upInPlane,
		XYZ vn)
	{
		if (parts != null && IsCollinearMainRunFittingPair(partA, partB, ptA, ptB, parts, primaryAxisInPlane, vn))
		{
			return false;
		}
		return IsFittingCenterPairOffsetLeg(ptA, ptB, primaryAxisInPlane, vn);
	}

	/// <summary>True open pipe end (E) anywhere on the collinear run — unmated connector at max/min scalar.</summary>
	private static bool TryResolveCollinearAssemblyOpenEnd(IList<FabricationPart> parts, XYZ unitAxis, XYZ viewNormal, bool forward, out FabricationPart endOwner, out XYZ point)
	{
		endOwner = null;
		point = null;
		if (parts == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		List<FabricationPart> partList = parts as List<FabricationPart> ?? parts.ToList();
		FabricationPart runHint = GetDominantCollinearRunPipePart(partList, unitAxis, viewNormal);
		double bestScalar = forward ? double.MinValue : double.MaxValue;
		foreach (FabricationPart part in parts)
		{
			if (part == null || !IsPipeRunPart(part) || IsOletBranchTakeoffPipe(part, partList))
			{
				continue;
			}
			if (runHint != null && !IsCollinearWithPrimaryRun(part, partList, unitAxis, viewNormal))
			{
				continue;
			}
			foreach (Connector connector in ListConnectors(part))
			{
				if (connector?.Origin == null || FindMateAtConnector(part, connector, partList) != null)
				{
					continue;
				}
				double scalar = DotInPlane(connector.Origin, unitAxis, viewNormal);
				if (forward ? scalar > bestScalar : scalar < bestScalar)
				{
					bestScalar = scalar;
					endOwner = part;
					point = connector.Origin;
				}
			}
		}
		return endOwner != null && point != null;
	}

	/// <summary>The part on the OTHER side of a weld/gasket joint from <paramref name="cameFrom"/>, if any.</summary>
	private static FabricationPart FindFarSideMateThroughJoint(FabricationPart joint, FabricationPart cameFrom, IList<FabricationPart> parts)
	{
		if (joint == null || cameFrom == null || parts == null)
		{
			return null;
		}
		foreach (Connector connector in ListConnectors(joint))
		{
			if (connector?.Origin == null)
			{
				continue;
			}
			FabricationPart mate = FindMateAtConnector(joint, connector, parts);
			if (mate != null && ((Element)mate).Id != ((Element)cameFrom).Id)
			{
				return mate;
			}
		}
		return null;
	}

	/// <summary>The collinear pipe on the far side of a flange (or bolted flange pair) from <paramref name="fromPipe"/>.</summary>
	private static bool TryAdvancePastFlangeRunJoint(FabricationPart fromPipe, FabricationPart flange, IList<FabricationPart> parts, XYZ unitAxis, XYZ viewNormal, out FabricationPart nextPipe)
	{
		nextPipe = null;
		if (fromPipe == null || flange == null || parts == null)
		{
			return false;
		}
		Document doc = ((Element)flange).Document;
		foreach (Connector connector in ListConnectors(flange))
		{
			FabricationPart mate = FindMateAtConnector(flange, connector, parts);
			if (mate == null || ((Element)mate).Id == ((Element)fromPipe).Id)
			{
				continue;
			}
			if (FabricationPartClassification.IsFlangePart(mate, doc))
			{
				foreach (Connector connector2 in ListConnectors(mate))
				{
					FabricationPart beyond = FindMateAtConnector(mate, connector2, parts);
					if (beyond != null && IsPipeRunPart(beyond) && IsCollinearWithPrimaryRun(beyond, parts, unitAxis, viewNormal)
						&& ((Element)beyond).Id != ((Element)fromPipe).Id)
					{
						nextPipe = beyond;
						return true;
					}
				}
				continue;
			}
			if (IsPipeRunPart(mate) && IsCollinearWithPrimaryRun(mate, parts, unitAxis, viewNormal))
			{
				nextPipe = mate;
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Walks from <paramref name="startPipe"/> in the direction of <paramref name="unitAxis"/>, following the
	/// pipe through field-weld/gasket joints and bolted flange pairs, to the TRUE open pipe end — even when
	/// that end belongs to a downstream pipe segment after a flange connection.
	/// </summary>
	private static bool TryWalkPipeRunToOpenEnd(FabricationPart startPipe, IList<FabricationPart> parts, XYZ unitAxis, XYZ viewNormal, out FabricationPart endOwner, out XYZ point)
	{
		endOwner = null;
		point = null;
		if (startPipe == null || parts == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		FabricationPart current = startPipe;
		HashSet<long> visited = new HashSet<long>();
		for (int guard = 0; guard < 64; guard++)
		{
			if (current == null || !visited.Add(((Element)current).Id.Value))
			{
				return false;
			}
			Connector bestConnector = null;
			double bestScalar = double.MinValue;
			foreach (Connector connector in ListConnectors(current))
			{
				if (connector?.Origin == null)
				{
					continue;
				}
				double scalar = DotInPlane(connector.Origin, unitAxis, viewNormal);
				if (scalar > bestScalar)
				{
					bestScalar = scalar;
					bestConnector = connector;
				}
			}
			if (bestConnector == null)
			{
				return false;
			}
			FabricationPart mate = FindMateAtConnector(current, bestConnector, parts);
			if (mate == null)
			{
				endOwner = current;
				point = bestConnector.Origin;
				return true;
			}
			if (IsWeldPart(mate) || IsGasketPart(mate))
			{
				FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, parts);
				if (beyond != null && IsPipeRunPart(beyond))
				{
					current = beyond;
					continue;
				}
				return false;
			}
			Document doc = ((Element)current).Document;
			if (FabricationPartClassification.IsFlangePart(mate, doc) && TryAdvancePastFlangeRunJoint(current, mate, parts, unitAxis, viewNormal, out FabricationPart nextPipe))
			{
				current = nextPipe;
				continue;
			}
			return false;
		}
		return false;
	}

	/// <summary>Collinear pipe on the straight-through side of a pass-through tee/reducer (not the branch port).</summary>
	private static bool TryGetPassThroughCollinearPipeBeyondFitting(
		FabricationPart fitting,
		FabricationPart fromPipe,
		IList<FabricationPart> parts,
		XYZ unitAxis,
		XYZ viewNormal,
		out FabricationPart nextPipe)
	{
		nextPipe = null;
		if (fitting == null || fromPipe == null || parts == null || IsPipeRunPart(fitting))
		{
			return false;
		}
		List<FabricationPart> partsList = parts as List<FabricationPart> ?? parts.ToList();
		foreach (Connector connector in ListConnectors(fitting))
		{
			FabricationPart mate = FindMateAtConnector(fitting, connector, partsList);
			if (mate == null || IsGasketPart(mate) || IsWeldPart(mate))
			{
				continue;
			}
			if (((Element)mate).Id == ((Element)fromPipe).Id)
			{
				continue;
			}
			if (IsPipeRunPart(mate) && !IsOletBranchTakeoffPipe(mate, partsList) && IsCollinearWithPrimaryRun(mate, partsList, unitAxis, viewNormal))
			{
				nextPipe = mate;
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Walk the collinear run in <paramref name="unitAxis"/> direction to the true termination:
	/// open pipe end (E) or terminating fitting center (C). Pass-through tees/reducers are crossed; elbows stop at C.
	/// </summary>
	private static bool TryWalkCollinearRunToTermination(
		FabricationPart startPipe,
		IList<FabricationPart> parts,
		XYZ unitAxis,
		XYZ viewNormal,
		out FabricationPart endOwner,
		out XYZ point)
	{
		endOwner = null;
		point = null;
		if (startPipe == null || parts == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		FabricationPart current = startPipe;
		FabricationPart cameFrom = null;
		HashSet<long> visited = new HashSet<long>();
		for (int guard = 0; guard < 64; guard++)
		{
			if (current == null || !visited.Add(((Element)current).Id.Value))
			{
				return false;
			}
			if (IsPipeRunPart(current))
			{
				List<Connector> forwardConnectors = new List<Connector>();
				foreach (Connector connector in ListConnectors(current))
				{
					if (connector?.Origin == null)
					{
						continue;
					}
					if (cameFrom != null)
					{
						FabricationPart backMate = FindMateAtConnector(current, connector, parts);
						if (backMate != null && ((Element)backMate).Id == ((Element)cameFrom).Id)
						{
							continue;
						}
					}
					forwardConnectors.Add(connector);
				}
				forwardConnectors.Sort((a, b) => DotInPlane(b.Origin, unitAxis, viewNormal).CompareTo(DotInPlane(a.Origin, unitAxis, viewNormal)));
				Connector chosenConnector = null;
				FabricationPart mate = null;
				foreach (Connector connector in forwardConnectors)
				{
					FabricationPart candidateMate = FindMateAtConnector(current, connector, parts);
					if (IsOletPart(candidateMate))
					{
						continue;
					}
					chosenConnector = connector;
					mate = candidateMate;
					break;
				}
				if (chosenConnector == null)
				{
					return false;
				}
				if (mate == null)
				{
					endOwner = current;
					point = chosenConnector.Origin;
					return true;
				}
				if (IsWeldPart(mate) || IsGasketPart(mate))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, parts);
					if (beyond != null && IsPipeRunPart(beyond))
					{
						cameFrom = current;
						current = beyond;
						continue;
					}
					return false;
				}
				Document doc = ((Element)current).Document;
				if (FabricationPartClassification.IsFlangePart(mate, doc) && TryAdvancePastFlangeRunJoint(current, mate, parts, unitAxis, viewNormal, out FabricationPart nextPipe))
				{
					cameFrom = current;
					current = nextPipe;
					continue;
				}
				if (IsFittingLikeForSpoolDim(mate) && !IsOletPart(mate) && !IsValvePart(mate))
				{
					if (TryGetPassThroughCollinearPipeBeyondFitting(mate, current, parts, unitAxis, viewNormal, out FabricationPart throughPipe))
					{
						cameFrom = mate;
						current = throughPipe;
						continue;
					}
					endOwner = mate;
					point = GetFabricationFittingDimensionAnchor(mate, current, null, parts);
					return point != null;
				}
				return false;
			}
			return false;
		}
		return false;
	}

	/// <summary>
	/// A tee (or reducer) sitting mid-run with its far side continuing straight along the SAME axis is a
	/// pass-through, not a run boundary — it has no elbow-style turn, so the true run extremity is further
	/// out, past it. Without this, a run split into two collinear pipe segments by a tee (main pipe -&gt;
	/// tee -&gt; continuation pipe, both ends open) would stop at the tee and produce a partial C-E instead
	/// of the overall E-E the fabricator needs, with the tee's own center collapsing into a nested C-E.
	/// </summary>
	private static bool TryExtendRunPastPassThroughFitting(FabricationPart fitting, FabricationPart pipePart, IList<FabricationPart> parts, XYZ unitAxis, XYZ viewNormal, out FabricationPart farOwner, out XYZ farPt)
	{
		farOwner = null;
		farPt = null;
		if (fitting == null || pipePart == null || parts == null || IsPipeRunPart(fitting))
		{
			return false;
		}
		List<FabricationPart> partsList = parts as List<FabricationPart> ?? parts.ToList();
		foreach (Connector connector in ListConnectors(fitting))
		{
			FabricationPart mate = FindMateAtConnector(fitting, connector, partsList);
			if (mate == null || IsGasketPart(mate) || IsWeldPart(mate))
			{
				continue;
			}
			if (((Element)mate).Id == ((Element)pipePart).Id)
			{
				continue;
			}
			if (!IsPipeRunPart(mate) || IsOletBranchTakeoffPipe(mate, partsList) || !IsCollinearWithPrimaryRun(mate, partsList, unitAxis, viewNormal))
			{
				continue;
			}
			if (TryGetPrimaryPipeEndAnchor(partsList, mate, unitAxis, viewNormal, out farOwner, out farPt))
			{
				return true;
			}
		}
		return false;
	}

	private static bool TryIsOpenCollinearPipeEnd(IList<FabricationPart> parts, FabricationPart pipe, XYZ pt)
	{
		if (pipe == null || pt == null || !IsPipeRunPart(pipe) || parts == null)
		{
			return false;
		}
		foreach (Connector connector in ListConnectors(pipe))
		{
			if (connector?.Origin == null || connector.Origin.DistanceTo(pt) > 1.0 / 24.0)
			{
				continue;
			}
			if (FindMateAtConnector(pipe, connector, parts) == null)
			{
				return true;
			}
		}
		return false;
	}

	private static bool TryResolvePipeEndFittingAnchor(List<FabricationPart> parts, FabricationPart pipePart, XYZ pipeEndPt, FabricationPart openEndOwner, out FabricationPart owner, out XYZ point)
	{
		owner = null;
		point = null;
		if (pipePart == null || pipeEndPt == null || parts == null || parts.Count == 0)
		{
			return false;
		}
		Connector nearestConnector = null;
		double nearestDist = double.MaxValue;
		foreach (Connector connector in ListConnectors(pipePart))
		{
			if (((connector != null) ? connector.Origin : null) == null)
			{
				continue;
			}
			double dist = connector.Origin.DistanceTo(pipeEndPt);
			if (dist < nearestDist)
			{
				nearestDist = dist;
				nearestConnector = connector;
			}
		}
		if (nearestConnector == null || nearestDist > 1.0)
		{
			return false;
		}
		FabricationPart mate = FindMateAtConnector(pipePart, nearestConnector, parts);
		if (mate == null || IsOletPart(mate) || IsGasketPart(mate) || IsWeldPart(mate) || !IsFittingLikeForSpoolDim(mate))
		{
			foreach (FabricationPart mated in EnumerateMatedFabricationParts(pipePart, parts))
			{
				if (IsFittingLikeForSpoolDim(mated) && !IsOletPart(mated) && !IsGasketPart(mated) && !IsWeldPart(mated))
				{
					XYZ connPt = TryMatedConnectorOriginTowardPart(pipePart, mated, parts);
					if (connPt != null && connPt.DistanceTo(pipeEndPt) <= 1.0)
					{
						mate = mated;
						break;
					}
				}
			}
		}
		if (mate == null || IsOletPart(mate) || IsGasketPart(mate) || IsWeldPart(mate) || !IsFittingLikeForSpoolDim(mate))
		{
			return false;
		}
		owner = mate;
		point = GetFabricationFittingDimensionAnchor(mate, pipePart, openEndOwner, parts);
		return point != null;
	}

	private static bool TryResolveLeftRunExtentAnchor(View view, List<FabricationPart> parts, FabricationPart pipePart, XYZ unitAxis, XYZ viewNormal, double openEndScalar, out FabricationPart owner, out XYZ point)
	{
		owner = null;
		point = null;
		if (parts == null || parts.Count == 0)
		{
			return false;
		}
		double num = double.MaxValue;
		foreach (FabricationPart part in parts)
		{
			if (part == null || IsGasketPart(part) || IsWeldPart(part))
			{
				continue;
			}
			foreach (XYZ item in CollectFabricationPartSnapPoints(part, view))
			{
				double num2 = DotInPlane(item, unitAxis, viewNormal);
				if (!(num2 >= openEndScalar - 1.0 / 24.0) && num2 < num)
				{
					num = num2;
					owner = part;
					point = item;
				}
			}
		}
		return point != null;
	}

	private static bool AreBoltedFlangePair(FabricationPart a, FabricationPart b, IList<FabricationPart> parts)
	{
		if (a == null || b == null || parts == null)
		{
			return false;
		}
		Document doc = ((Element)a).Document;
		if (!FabricationPartClassification.IsFlangePart(a, doc) || !FabricationPartClassification.IsFlangePart(b, doc))
		{
			return false;
		}
		if (AreFabricationPartsDirectlyConnected(a, b))
		{
			return true;
		}
		// Bolted pairs often mate through a gasket, not flange-to-flange directly.
		foreach (Connector connector in ListConnectors(a))
		{
			FabricationPart joint = FindMateAtConnector(a, connector, parts);
			if (joint == null || (!IsGasketPart(joint) && !IsWeldPart(joint)))
			{
				continue;
			}
			FabricationPart beyond = FindFarSideMateThroughJoint(joint, a, parts);
			if (beyond != null && ((Element)beyond).Id == ((Element)b).Id)
			{
				return true;
			}
		}
		return false;
	}

	private static FabricationPart FindCollinearPipeMateForFlange(FabricationPart flange, IList<FabricationPart> parts, XYZ unitAxis, XYZ viewNormal)
	{
		if (flange == null || parts == null)
		{
			return null;
		}
		foreach (Connector connector in ListConnectors(flange))
		{
			FabricationPart mate = FindMateAtConnector(flange, connector, parts);
			if (mate != null && IsPipeRunPart(mate) && IsCollinearWithPrimaryRun(mate, parts, unitAxis, viewNormal))
			{
				return mate;
			}
		}
		return null;
	}

	private static FabricationPart FindRunContextPipeForFlange(FabricationPart flange, IList<FabricationPart> parts, FabricationPart pipePart, XYZ unitAxis, XYZ viewNormal)
	{
		if (flange == null || parts == null)
		{
			return pipePart;
		}
		foreach (Connector connector in ListConnectors(flange))
		{
			FabricationPart mate = FindMateAtConnector(flange, connector, parts);
			if (mate != null && IsPipeRunPart(mate) && !IsOletBranchTakeoffPipe(mate, parts))
			{
				return mate;
			}
		}
		return FindCollinearPipeMateForFlange(flange, parts, unitAxis, viewNormal) ?? pipePart;
	}

	/// <summary>All fabrication parts connected to <paramref name="seed"/> through the assembly graph (for finding branch flanges).</summary>
	private static HashSet<long> GetAssemblyReachablePartIds(FabricationPart seed, IList<FabricationPart> partsPool, bool excludeOletBranchTakeoffs)
	{
		HashSet<long> ids = new HashSet<long>();
		if (seed == null || partsPool == null)
		{
			return ids;
		}
		Queue<FabricationPart> queue = new Queue<FabricationPart>();
		queue.Enqueue(seed);
		while (queue.Count > 0)
		{
			FabricationPart current = queue.Dequeue();
			if (current == null || !ids.Add(((Element)current).Id.Value))
			{
				continue;
			}
			foreach (Connector connector in ListConnectors(current))
			{
				if (connector?.Origin == null)
				{
					continue;
				}
				FabricationPart mate = FindMateAtConnector(current, connector, partsPool);
				if (mate == null)
				{
					continue;
				}
				if (IsGasketPart(mate) || IsWeldPart(mate))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, partsPool);
					if (beyond != null)
					{
						queue.Enqueue(beyond);
					}
					continue;
				}
				if (excludeOletBranchTakeoffs && IsOletBranchTakeoffPipe(mate, partsPool))
				{
					continue;
				}
				if (IsOletPart(mate) && !IsOletHostRunPipe(current, partsPool))
				{
					continue;
				}
				queue.Enqueue(mate);
			}
		}
		return ids;
	}

	/// <summary>
	/// Flanges and olets on the run — graph-reachable from run start, including branch legs (L-spool flange on stub after top elbow).
	/// </summary>
	private static List<(FabricationPart part, XYZ point, double scalar)> CollectRunIntermediateSnapAnchors(
		View view,
		List<FabricationPart> parts,
		FabricationPart pipePart,
		FabricationPart minOwner,
		FabricationPart maxOwner,
		XYZ unitAxis,
		XYZ viewNormal,
		double minScalar,
		double maxScalar,
		double endTol)
	{
		List<(FabricationPart, XYZ, double)> list = new List<(FabricationPart, XYZ, double)>();
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		void TryAdd(FabricationPart part, XYZ pt, bool requireScalarBand)
		{
			if (part == null || pt == null)
			{
				return;
			}
			if (minOwner != null && ((Element)part).Id == ((Element)minOwner).Id)
			{
				return;
			}
			if (maxOwner != null && ((Element)part).Id == ((Element)maxOwner).Id)
			{
				return;
			}
			double num = DotInPlane(pt, unitAxis, viewNormal);
			if (requireScalarBand && (num < minScalar - endTol || num > maxScalar + endTol))
			{
				return;
			}
			string item = ((Element)part).Id.Value.ToString() + ":" + num.ToString("F4");
			if (seen.Add(item))
			{
				list.Add((part, pt, num));
			}
		}
		// Olet pick-ups are dimensioned by TryPlaceOletRunStackDimensions — do not also chain them here
		// or the same anchor→olet spans appear twice (stacked pick-ups + run chain starters).
		foreach (FabricationPart part in parts)
		{
			if (part == null || !IsFittingLikeForSpoolDim(part) || IsOletPart(part) || IsValvePart(part) || IsGasketPart(part) || IsWeldPart(part))
			{
				continue;
			}
			if (FabricationPartClassification.IsFlangePart(part, ((Element)part).Document))
			{
				continue;
			}
			if (!IsFittingOnCollinearRunNetwork(part, parts, pipePart, unitAxis, viewNormal))
			{
				continue;
			}
			FabricationPart contextPipe = pipePart;
			foreach (Connector connector in ListConnectors(part))
			{
				FabricationPart mate = FindMateAtConnector(part, connector, parts);
				if (mate != null && IsPipeRunPart(mate) && IsCollinearWithPrimaryRun(mate, parts, unitAxis, viewNormal))
				{
					contextPipe = mate;
					break;
				}
			}
			if (contextPipe == null || !IsCollinearWithPrimaryRun(contextPipe, parts, unitAxis, viewNormal))
			{
				continue;
			}
			XYZ fittingAnchor = GetFabricationFittingDimensionAnchor(part, contextPipe, maxOwner, parts);
			if (fittingAnchor != null)
			{
				if (maxOwner != null && part != maxOwner
					&& FabricationPartClassification.IsElbowPart(part, ((Element)part).Document)
					&& FabricationPartClassification.IsElbowPart(maxOwner, ((Element)maxOwner).Document)
					&& AreFittingsLinkedByCollinearRun(part, maxOwner, parts, unitAxis, viewNormal))
				{
					XYZ maxAnchor = GetFabricationFittingDimensionAnchor(maxOwner, contextPipe, maxOwner, parts);
					if (maxAnchor != null
						&& DotInPlane(fittingAnchor, unitAxis, viewNormal) < DotInPlane(maxAnchor, unitAxis, viewNormal) - endTol)
					{
						continue;
					}
				}
				TryAdd(part, fittingAnchor, requireScalarBand: true);
			}
		}
		foreach (FabricationPart part in parts)
		{
			if (part == null || !FabricationPartClassification.IsFlangePart(part, ((Element)part).Document))
			{
				continue;
			}
			FabricationPart contextPipe = FindRunContextPipeForFlange(part, parts, pipePart, unitAxis, viewNormal);
			if (contextPipe == null || !IsCollinearWithPrimaryRun(contextPipe, parts, unitAxis, viewNormal))
			{
				continue;
			}
			// Branch stub pipes (top elbow → flange on L-spools) are collinear in elevation but must never
			// enter the main horizontal run chain — only flanges on the dominant run pipe network.
			if (pipePart != null && ((Element)contextPipe).Id != ((Element)pipePart).Id
				&& GetFabricationStraightLineLength(contextPipe) < GetFabricationStraightLineLength(pipePart) * 0.5)
			{
				continue;
			}
			XYZ fittingAnchor = GetFabricationFittingDimensionAnchor(part, contextPipe, maxOwner, parts);
			if (fittingAnchor != null)
			{
				TryAdd(part, fittingAnchor, requireScalarBand: true);
			}
		}
		return list.GroupBy(((FabricationPart part, XYZ point, double scalar) a) => ((Element)a.part).Id.Value).Select((IGrouping<long, (FabricationPart part, XYZ point, double scalar)> g) => g.OrderBy(((FabricationPart part, XYZ point, double scalar) a) => IsOletPart(a.part) ? 0 : 1).First()).ToList();
	}

	private static List<(FabricationPart part, XYZ point, double scalar)> BuildOrderedRunDimensionChain(
		View view,
		List<FabricationPart> parts,
		FabricationPart pipePart,
		FabricationPart minOwner,
		XYZ minPt,
		double minScalar,
		FabricationPart maxOwner,
		XYZ maxPt,
		double maxScalar,
		FabricationPart passThroughFitting,
		XYZ passThroughPt,
		XYZ unitAxis,
		XYZ viewNormal,
		double endTol)
	{
		List<(FabricationPart part, XYZ point, double scalar)> chain = new List<(FabricationPart, XYZ, double)>
		{
			(minOwner, minPt, minScalar)
		};
		if (passThroughFitting != null && passThroughPt != null)
		{
			chain.Add((passThroughFitting, passThroughPt, DotInPlane(passThroughPt, unitAxis, viewNormal)));
		}
		foreach (var item in CollectRunIntermediateSnapAnchors(view, parts, pipePart, minOwner, maxOwner, unitAxis, viewNormal, minScalar, maxScalar, endTol))
		{
			chain.Add(item);
		}
		chain = chain.GroupBy(((FabricationPart part, XYZ point, double scalar) a) => ((Element)a.part).Id.Value)
			.Select((IGrouping<long, (FabricationPart part, XYZ point, double scalar)> g) => g.First())
			.OrderBy(((FabricationPart part, XYZ point, double scalar) a) => a.scalar)
			.ThenBy(((FabricationPart part, XYZ point, double scalar) a) => minOwner != null && ((Element)a.part).Id == ((Element)minOwner).Id ? 0 : 1)
			.ToList();
		chain.Add((maxOwner, maxPt, maxScalar));
		return chain;
	}

	private static void ResolveRunSegmentRefRoles(
		Element elemA,
		Element elemB,
		XYZ ptB,
		Element maxOwner,
		XYZ maxPt,
		FabricationDimensionRefRole runEndRole,
		out FabricationDimensionRefRole? roleA,
		out FabricationDimensionRefRole? roleB)
	{
		ResolveHorizontalRunSegmentRefRoles(elemA, elemB, out roleA, out roleB);
		if (runEndRole != FabricationDimensionRefRole.PipeOpenEnd
			&& elemB is FabricationPart pipeB
			&& IsPipeRunPart(pipeB)
			&& maxOwner != null
			&& ((Element)pipeB).Id == ((Element)maxOwner).Id
			&& ptB != null
			&& maxPt != null
			&& ptB.DistanceTo(maxPt) < 1.0 / 24.0)
		{
			roleB = runEndRole;
		}
	}

	/// <summary>True when two dimension intents measure the same element pair at the same witness points (either direction).</summary>
	private static bool AreSameRunDimensionSpan(
		Element elemA1,
		XYZ ptA1,
		Element elemB1,
		XYZ ptB1,
		Element elemA2,
		XYZ ptA2,
		Element elemB2,
		XYZ ptB2,
		double pointTol = 1.0 / 24.0)
	{
		if (elemA1 == null || elemB1 == null || elemA2 == null || elemB2 == null
			|| ptA1 == null || ptB1 == null || ptA2 == null || ptB2 == null)
		{
			return false;
		}
		bool forward = elemA1.Id == elemA2.Id && elemB1.Id == elemB2.Id
			&& ptA1.DistanceTo(ptA2) < pointTol && ptB1.DistanceTo(ptB2) < pointTol;
		bool reverse = elemA1.Id == elemB2.Id && elemB1.Id == elemA2.Id
			&& ptA1.DistanceTo(ptB2) < pointTol && ptB1.DistanceTo(ptA2) < pointTol;
		return forward || reverse;
	}

	/// <summary>
	/// Universal run chain placement: consecutive starter segments share the 3/8" tier; overall stacks above.
	/// </summary>
	private static int TryPlaceSpoolRunChainDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		List<(FabricationPart part, XYZ point, double scalar)> chain,
		FabricationPart minOwner,
		XYZ minPt,
		FabricationPart maxOwner,
		XYZ maxPt,
		bool addOverallWhenFittingOnRun,
		FabricationDimensionRefRole runEndRole,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		out string failureDetail,
		string logTag,
		int offsetSign = 1,
		bool lockOffsetSign = false,
		XYZ runAxisInView = null,
		XYZ viewNormal = null)
	{
		failureDetail = null;
		if (doc == null || view == null || chain == null || chain.Count < 2)
		{
			return 0;
		}
		bool singleSpanOverallOnly = addOverallWhenFittingOnRun && chain.Count == 2
			&& minOwner != null && maxOwner != null
			&& ((Element)minOwner).Id != ((Element)maxOwner).Id;
		List<(Element a, XYZ ptA, Element b, XYZ ptB, double length, bool isOverall)> segments = new List<(Element, XYZ, Element, XYZ, double, bool)>();
		(Element a, XYZ ptA, Element b, XYZ ptB, double length, bool isOverall)? loneChainSegment = null;
		for (int i = 0; i < chain.Count - 1; i++)
		{
			var segA = chain[i];
			var segB = chain[i + 1];
			if (segA.part != null && segB.part != null && ((Element)segA.part).Id == ((Element)segB.part).Id)
			{
				continue;
			}
			if (AreBoltedFlangePair(segA.part, segB.part, parts))
			{
				continue;
			}
			double length = segB.point.DistanceTo(segA.point);
			if (length < 1.0 / 12.0)
			{
				continue;
			}
			var chainSegment = ((Element)(object)segA.part, segA.point, (Element)(object)segB.part, segB.point, length, isOverall: false);
			if (singleSpanOverallOnly)
			{
				loneChainSegment = chainSegment;
				continue;
			}
			segments.Add(chainSegment);
		}
		if (addOverallWhenFittingOnRun && minOwner != null && maxOwner != null
			&& ((Element)minOwner).Id != ((Element)maxOwner).Id)
		{
			double overallLen = maxPt.DistanceTo(minPt);
			if (overallLen >= 1.0 / 12.0)
			{
				segments.Add(((Element)(object)minOwner, minPt, (Element)(object)maxOwner, maxPt, overallLen, isOverall: true));
			}
		}
		List<(Element a, XYZ ptA, Element b, XYZ ptB, double length, bool isOverall)> starterSegments = segments
			.Where((s) => !s.isOverall)
			.OrderBy((s) => s.length)
			.ToList();
		List<(Element a, XYZ ptA, Element b, XYZ ptB, double length, bool isOverall)> overallSegments = segments
			.Where((s) => s.isOverall)
			.ToList();
		int placed = 0;
		int starterStackBase = stackIndex;
		bool anyStarterPlaced = false;
		foreach (var seg in starterSegments)
		{
			TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, logTag + " starter " + FormatElementAnchor(seg.a, seg.ptA) + " to " + FormatElementAnchor(seg.b, seg.ptB), 0, 0);
			ResolveRunSegmentRefRoles(seg.a, seg.b, seg.ptB, (Element)(object)maxOwner, maxPt, runEndRole, out FabricationDimensionRefRole? roleA, out FabricationDimensionRefRole? roleB);
			int starterSlot = starterStackBase;
			if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, seg.a, seg.ptA, seg.b, seg.ptB, spoolSettings, ref starterSlot, out failureDetail, roleA, roleB, offsetSign, lockOffsetSign))
			{
				placed++;
				anyStarterPlaced = true;
			}
		}
		if (anyStarterPlaced)
		{
			stackIndex = starterStackBase + 1;
		}
		foreach (var seg in overallSegments)
		{
			TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, logTag + " overall " + FormatElementAnchor(seg.a, seg.ptA) + " to " + FormatElementAnchor(seg.b, seg.ptB), 0, 0);
			ResolveRunSegmentRefRoles(seg.a, seg.b, seg.ptB, (Element)(object)maxOwner, maxPt, runEndRole, out FabricationDimensionRefRole? roleA, out FabricationDimensionRefRole? roleB);
			if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, seg.a, seg.ptA, seg.b, seg.ptB, spoolSettings, ref stackIndex, out failureDetail, roleA, roleB, offsetSign, lockOffsetSign))
			{
				placed++;
			}
		}
		if (placed == 0 && loneChainSegment.HasValue)
		{
			var seg = loneChainSegment.Value;
			TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, logTag + " lone-chain fallback " + FormatElementAnchor(seg.a, seg.ptA) + " to " + FormatElementAnchor(seg.b, seg.ptB), 0, 0);
			ResolveRunSegmentRefRoles(seg.a, seg.b, seg.ptB, (Element)(object)maxOwner, maxPt, runEndRole, out FabricationDimensionRefRole? roleA, out FabricationDimensionRefRole? roleB);
			if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, seg.a, seg.ptA, seg.b, seg.ptB, spoolSettings, ref stackIndex, out failureDetail, roleA, roleB, offsetSign, lockOffsetSign))
			{
				placed++;
			}
		}
		return placed;
	}

	private static void ResolveHorizontalRunSegmentRefRoles(Element elemA, Element elemB, out FabricationDimensionRefRole? roleA, out FabricationDimensionRefRole? roleB)
	{
		roleA = null;
		roleB = null;
		if (elemA is FabricationPart partA)
		{
			Document doc = ((Element)partA).Document;
			if (FabricationPartClassification.IsFlangePart(partA, doc))
			{
				roleA = FabricationDimensionRefRole.FlangeFace;
			}
			else if (IsPipeRunPart(partA))
			{
				roleA = FabricationDimensionRefRole.PipeOpenEnd;
			}
			else if (IsFittingLikeForSpoolDim(partA))
			{
				roleA = FabricationDimensionRefRole.RunStartFitting;
			}
		}
		if (elemB is FabricationPart partB)
		{
			Document doc = ((Element)partB).Document;
			if (FabricationPartClassification.IsFlangePart(partB, doc))
			{
				roleB = FabricationDimensionRefRole.FlangeFace;
			}
			else if (IsPipeRunPart(partB))
			{
				roleB = FabricationDimensionRefRole.PipeOpenEnd;
			}
			else if (IsFittingLikeForSpoolDim(partB))
			{
				roleB = FabricationDimensionRefRole.RunStartFitting;
			}
		}
	}

	/// <summary>
	/// Pass-through tee/reducer on a collinear run: two straight-through pipe ports on the same axis (branch ignored).
	/// </summary>
	private static bool IsPassThroughCollinearRunFitting(
		FabricationPart fitting,
		IList<FabricationPart> parts,
		XYZ unitAxis,
		XYZ viewNormal)
	{
		if (fitting == null || parts == null || IsPipeRunPart(fitting) || IsOletPart(fitting) || IsValvePart(fitting)
			|| IsGasketPart(fitting) || IsWeldPart(fitting))
		{
			return false;
		}
		Document doc = ((Element)fitting).Document;
		if (FabricationPartClassification.IsElbowPart(fitting, doc) || FabricationPartClassification.IsFlangePart(fitting, doc))
		{
			return false;
		}
		int collinearPipeMates = 0;
		foreach (Connector connector in ListConnectors(fitting))
		{
			FabricationPart mate = FindMateAtConnector(fitting, connector, parts);
			if (mate != null && IsPipeRunPart(mate) && !IsOletBranchTakeoffPipe(mate, parts)
				&& IsCollinearWithPrimaryRun(mate, parts, unitAxis, viewNormal))
			{
				collinearPipeMates++;
			}
		}
		return collinearPipeMates >= 2;
	}

	private static List<FabricationPart> GetPassThroughCollinearRunFittings(
		IList<FabricationPart> parts,
		FabricationPart pipePart,
		XYZ unitAxis,
		XYZ viewNormal)
	{
		List<FabricationPart> fittings = new List<FabricationPart>();
		if (parts == null || pipePart == null)
		{
			return fittings;
		}
		foreach (FabricationPart part in parts)
		{
			if (part == null || !IsPassThroughCollinearRunFitting(part, parts, unitAxis, viewNormal))
			{
				continue;
			}
			if (!IsFittingOnCollinearRunNetwork(part, parts, pipePart, unitAxis, viewNormal))
			{
				continue;
			}
			fittings.Add(part);
		}
		return fittings;
	}

	private static FabricationPart FindCollinearRunPipeMateForFitting(
		FabricationPart fitting,
		IList<FabricationPart> parts,
		FabricationPart runHint,
		XYZ unitAxis,
		XYZ viewNormal)
	{
		if (fitting == null || parts == null)
		{
			return runHint;
		}
		FabricationPart best = null;
		double bestLen = -1.0;
		foreach (Connector connector in ListConnectors(fitting))
		{
			FabricationPart mate = FindMateAtConnector(fitting, connector, parts);
			if (mate == null || !IsPipeRunPart(mate) || IsOletBranchTakeoffPipe(mate, parts)
				|| !IsCollinearWithPrimaryRun(mate, parts, unitAxis, viewNormal))
			{
				continue;
			}
			double len = GetFabricationStraightLineLength(mate);
			if (len > bestLen)
			{
				bestLen = len;
				best = mate;
			}
		}
		return best ?? runHint;
	}

	/// <summary>
	/// pipe—tee—pipe: overall E→E plus chain E→C and C→E. pipe—tee—pipe—fitting: overall E→last C plus E→C and C→C.
	/// Same rules on horizontal and vertical collinear runs (view axis only).
	/// </summary>
	private static bool TryResolvePassThroughTeeRunDimensionExtents(
		List<FabricationPart> parts,
		FabricationPart pipePart,
		XYZ unitAxis,
		XYZ viewNormal,
		out FabricationPart minOwner,
		out XYZ minPt,
		out FabricationPart maxOwner,
		out XYZ maxPt,
		out List<(FabricationPart part, XYZ point, double scalar)> intermediateAnchors)
	{
		minOwner = null;
		minPt = null;
		maxOwner = null;
		maxPt = null;
		intermediateAnchors = new List<(FabricationPart, XYZ, double)>();
		if (parts == null || pipePart == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		List<FabricationPart> passThroughFittings = GetPassThroughCollinearRunFittings(parts, pipePart, unitAxis, viewNormal);
		if (passThroughFittings.Count == 0)
		{
			return false;
		}
		if (!TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, viewNormal, forward: false, out minOwner, out minPt)
			|| !TryIsOpenCollinearPipeEnd(parts, minOwner, minPt))
		{
			return false;
		}
		if (!TryWalkCollinearRunToTermination(pipePart, parts, unitAxis, viewNormal, out maxOwner, out maxPt)
			&& !TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, viewNormal, forward: true, out maxOwner, out maxPt))
		{
			return false;
		}
		if (IsPipeRunPart(maxOwner) && !TryIsOpenCollinearPipeEnd(parts, maxOwner, maxPt)
			&& TryResolvePipeEndFittingAnchor(parts, maxOwner, maxPt, null, out FabricationPart farFit, out XYZ farFitPt))
		{
			maxOwner = farFit;
			maxPt = farFitPt;
		}
		double minScalar = DotInPlane(minPt, unitAxis, viewNormal);
		double maxScalar = DotInPlane(maxPt, unitAxis, viewNormal);
		if (maxScalar < minScalar)
		{
			FabricationPart swapOwner = minOwner;
			minOwner = maxOwner;
			maxOwner = swapOwner;
			XYZ swapPt = minPt;
			minPt = maxPt;
			maxPt = swapPt;
			minScalar = DotInPlane(minPt, unitAxis, viewNormal);
			maxScalar = DotInPlane(maxPt, unitAxis, viewNormal);
		}
		double endTol = 1.0 / 24.0;
		foreach (FabricationPart fitting in passThroughFittings)
		{
			FabricationPart contextPipe = FindCollinearRunPipeMateForFitting(fitting, parts, pipePart, unitAxis, viewNormal);
			XYZ anchor = GetFabricationFittingDimensionAnchor(fitting, contextPipe, maxOwner, parts);
			if (anchor == null)
			{
				continue;
			}
			double scalar = DotInPlane(anchor, unitAxis, viewNormal);
			if (scalar <= minScalar + endTol || scalar >= maxScalar - endTol)
			{
				continue;
			}
			if (minOwner != null && ((Element)fitting).Id == ((Element)minOwner).Id)
			{
				continue;
			}
			if (maxOwner != null && ((Element)fitting).Id == ((Element)maxOwner).Id)
			{
				continue;
			}
			intermediateAnchors.Add((fitting, anchor, scalar));
		}
		intermediateAnchors = intermediateAnchors
			.GroupBy(((FabricationPart part, XYZ point, double scalar) a) => ((Element)a.part).Id.Value)
			.Select((IGrouping<long, (FabricationPart part, XYZ point, double scalar)> g) => g.First())
			.OrderBy(((FabricationPart part, XYZ point, double scalar) a) => a.scalar)
			.ToList();
		return intermediateAnchors.Count >= 1;
	}

	/// <summary>True open pipe end (E) on the collinear run network containing <paramref name="pipePart"/>.</summary>
	private static bool TryResolveCollinearRunOpenEndAnchor(
		IList<FabricationPart> parts,
		FabricationPart pipePart,
		XYZ unitAxis,
		XYZ viewNormal,
		out FabricationPart owner,
		out XYZ point)
	{
		owner = null;
		point = null;
		if (parts == null || pipePart == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		List<FabricationPart> partList = parts as List<FabricationPart> ?? parts.ToList();
		if (TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, viewNormal, forward: false, out owner, out point)
			&& TryIsOpenCollinearPipeEnd(parts, owner, point))
		{
			return true;
		}
		if (TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, viewNormal, forward: true, out owner, out point)
			&& TryIsOpenCollinearPipeEnd(parts, owner, point))
		{
			return true;
		}
		if (TryGetMinimumPipeEndAnchor(partList, pipePart, unitAxis, viewNormal, out owner, out point)
			&& TryIsOpenCollinearPipeEnd(parts, owner, point))
		{
			return true;
		}
		if (TryGetPrimaryPipeEndAnchor(partList, pipePart, unitAxis, viewNormal, out owner, out point)
			&& TryIsOpenCollinearPipeEnd(parts, owner, point))
		{
			return true;
		}
		return (TryWalkPipeRunToOpenEnd(pipePart, parts, unitAxis, viewNormal, out owner, out point)
			|| TryWalkPipeRunToOpenEnd(pipePart, parts, unitAxis.Negate(), viewNormal, out owner, out point))
			&& TryIsOpenCollinearPipeEnd(parts, owner, point);
	}

	/// <summary>Run-terminating fitting center (C) on the collinear pipe — elbow at an L-turn, not a pass-through tee.</summary>
	private static bool TryResolveCollinearRunFittingEndAnchor(
		IList<FabricationPart> parts,
		FabricationPart pipePart,
		FabricationPart openEndOwner,
		XYZ openEndPt,
		XYZ unitAxis,
		XYZ viewNormal,
		out FabricationPart owner,
		out XYZ point)
	{
		owner = null;
		point = null;
		if (parts == null || pipePart == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		List<FabricationPart> partList = parts as List<FabricationPart> ?? parts.ToList();
		if (TryResolvePipeRunStartFittingAnchor(partList, pipePart, openEndOwner, unitAxis, viewNormal, out owner, out point)
			&& owner != null && (openEndOwner == null || ((Element)owner).Id != ((Element)openEndOwner).Id)
			&& !IsPassThroughCollinearRunFitting(owner, parts, unitAxis, viewNormal))
		{
			return point != null;
		}
		if (TryFindRunStartFittingForCollinearRun(parts, pipePart, unitAxis, viewNormal, out owner, out point)
			&& owner != null && (openEndOwner == null || ((Element)owner).Id != ((Element)openEndOwner).Id)
			&& !IsPassThroughCollinearRunFitting(owner, parts, unitAxis, viewNormal))
		{
			return point != null;
		}
		foreach (Connector connector in ListConnectors(pipePart))
		{
			if (connector?.Origin == null)
			{
				continue;
			}
			if (openEndPt != null && connector.Origin.DistanceTo(openEndPt) < 1.0 / 24.0)
			{
				continue;
			}
			FabricationPart mate = FindMateAtConnector(pipePart, connector, partList);
			if (mate == null || IsOletPart(mate) || IsGasketPart(mate) || IsWeldPart(mate) || IsValvePart(mate)
				|| !IsFittingLikeForSpoolDim(mate)
				|| IsPassThroughCollinearRunFitting(mate, parts, unitAxis, viewNormal))
			{
				continue;
			}
			owner = mate;
			point = GetFabricationFittingDimensionAnchor(mate, pipePart, openEndOwner, partList);
			if (point != null)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// One open E and one terminating fitting C on the same collinear run (L-spool main horizontal leg, etc.).
	/// Skips pass-through tee runs and two-open-end runs (handled by other paths).
	/// </summary>
	private static bool TryResolveCollinearMainRunDimensionExtents(
		List<FabricationPart> parts,
		FabricationPart pipePart,
		XYZ unitAxis,
		XYZ viewNormal,
		out FabricationPart minOwner,
		out XYZ minPt,
		out FabricationPart maxOwner,
		out XYZ maxPt)
	{
		minOwner = null;
		minPt = null;
		maxOwner = null;
		maxPt = null;
		if (parts == null || pipePart == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		if (GetPassThroughCollinearRunFittings(parts, pipePart, unitAxis, viewNormal).Count > 0)
		{
			return false;
		}
		if (!TryResolveCollinearRunOpenEndAnchor(parts, pipePart, unitAxis, viewNormal, out FabricationPart openEndOwner, out XYZ openEndPt))
		{
			return false;
		}
		FabricationPart openEndFar = null;
		XYZ openEndFarPt = null;
		bool hasFarOpen = TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, viewNormal, forward: true, out openEndFar, out openEndFarPt)
			&& TryIsOpenCollinearPipeEnd(parts, openEndFar, openEndFarPt);
		if (hasFarOpen && openEndFar != null && openEndOwner != null
			&& ((Element)openEndFar).Id != ((Element)openEndOwner).Id
			&& openEndFarPt.DistanceTo(openEndPt) >= 1.0 / 24.0)
		{
			return false;
		}
		if (!TryResolveCollinearRunFittingEndAnchor(parts, pipePart, openEndOwner, openEndPt, unitAxis, viewNormal, out FabricationPart fittingEndOwner, out XYZ fittingEndPt))
		{
			return false;
		}
		if (openEndOwner != null && fittingEndOwner != null
			&& ((Element)openEndOwner).Id == ((Element)fittingEndOwner).Id
			&& openEndPt.DistanceTo(fittingEndPt) < 1.0 / 24.0)
		{
			return false;
		}
		double openScalar = DotInPlane(openEndPt, unitAxis, viewNormal);
		double fitScalar = DotInPlane(fittingEndPt, unitAxis, viewNormal);
		if (openScalar <= fitScalar)
		{
			minOwner = openEndOwner;
			minPt = openEndPt;
			maxOwner = fittingEndOwner;
			maxPt = fittingEndPt;
		}
		else
		{
			minOwner = fittingEndOwner;
			minPt = fittingEndPt;
			maxOwner = openEndOwner;
			maxPt = openEndPt;
		}
		return minPt.DistanceTo(maxPt) >= 1.0 / 24.0;
	}

	private static bool TryCreateSpoolCollinearMainRunChainDimensions(
		Document doc,
		View view,
		List<FabricationPart> parts,
		FabricationPart pipePart,
		XYZ unitAxis,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		out string diagnostic,
		string logTag)
	{
		diagnostic = null;
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (collinear main run): view direction is invalid.";
			return false;
		}
		vn = vn.Normalize();
		if (!TryResolveCollinearMainRunDimensionExtents(parts, pipePart, unitAxis, vn,
			out FabricationPart minOwner, out XYZ minPt,
			out FabricationPart maxOwner, out XYZ maxPt))
		{
			return false;
		}
		double minScalar = DotInPlane(minPt, unitAxis, vn);
		double maxScalar = DotInPlane(maxPt, unitAxis, vn);
		if (maxScalar < minScalar)
		{
			unitAxis = unitAxis.Negate();
			minScalar = DotInPlane(minPt, unitAxis, vn);
			maxScalar = DotInPlane(maxPt, unitAxis, vn);
		}
		if (minPt.DistanceTo(maxPt) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension (collinear main run): run extent is too short to dimension.";
			return false;
		}
		double endTol = 1.0 / 24.0;
		List<(FabricationPart part, XYZ point, double scalar)> chain = BuildOrderedRunDimensionChain(
			view, parts, pipePart, minOwner, minPt, minScalar, maxOwner, maxPt, maxScalar,
			null, null, unitAxis, vn, endTol);
		TryAppendAutoDimDiagnosticLog("collinear-main-run", view.Name,
			logTag + $" min={FormatElementAnchor(minOwner, minPt)} max={FormatElementAnchor(maxOwner, maxPt)}", 0, 0);
		string failureDetail = null;
		int placed = TryPlaceSpoolRunChainDimensions(
			doc, view, parts, chain, minOwner, minPt, maxOwner, maxPt,
			addOverallWhenFittingOnRun: true,
			FabricationDimensionRefRole.PipeOpenEnd,
			spoolSettings, ref stackIndex, out failureDetail, logTag,
			runAxisInView: unitAxis, viewNormal: vn);
		if (placed > 0)
		{
			return true;
		}
		diagnostic = "Auto-dimension (collinear main run): could not place run chain dimensions in this view.";
		if (!string.IsNullOrEmpty(failureDetail))
		{
			diagnostic = diagnostic + " " + failureDetail;
		}
		return false;
	}

	/// <summary>
	/// When a branch-terminal flange (F) extends past the main collinear run termination, place overall E→F
	/// from the main run open end (E) — same rule on horizontal and vertical primary axes in the view.
	/// </summary>
	private static int TryCreateSpoolOpenEndToFlangeOverallDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		XYZ primaryUnitAxis,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		List<string> failureNotes)
	{
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			return 0;
		}
		vn = vn.Normalize();
		List<FabricationPart> partList = parts as List<FabricationPart> ?? parts.ToList();
		FabricationPart dominantPipe = GetDominantCollinearRunPipePart(partList, primaryUnitAxis, vn);
		if (dominantPipe == null)
		{
			return 0;
		}
		if (!TryResolveCollinearMainRunDimensionExtents(partList, dominantPipe, primaryUnitAxis, vn,
			out FabricationPart minOwner, out XYZ minPt,
			out FabricationPart maxOwner, out XYZ maxPt))
		{
			return 0;
		}
		Element openEndPart = null;
		XYZ openEndPt = null;
		double runTerminationScalar = 0.0;
		if (IsPipeRunPart(minOwner) && TryIsOpenCollinearPipeEnd(partList, minOwner, minPt))
		{
			openEndPart = (Element)(object)minOwner;
			openEndPt = minPt;
			runTerminationScalar = DotInPlane(maxPt, primaryUnitAxis, vn);
		}
		else if (IsPipeRunPart(maxOwner) && TryIsOpenCollinearPipeEnd(partList, maxOwner, maxPt))
		{
			openEndPart = (Element)(object)maxOwner;
			openEndPt = maxPt;
			runTerminationScalar = DotInPlane(minPt, primaryUnitAxis, vn);
		}
		if (openEndPart == null || openEndPt == null)
		{
			return 0;
		}
		double dominantLen = GetFabricationStraightLineLength(dominantPipe);
		FabricationPart runStartFitting = IsFittingLikeForSpoolDim(maxOwner) && !IsPipeRunPart(maxOwner) ? maxOwner
			: (IsFittingLikeForSpoolDim(minOwner) && !IsPipeRunPart(minOwner) ? minOwner : null);
		int placed = 0;
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (FabricationPart flangePart in partList)
		{
			if (flangePart == null || !FabricationPartClassification.IsFlangePart(flangePart, ((Element)flangePart).Document))
			{
				continue;
			}
			if (!IsBranchTerminalFlange(flangePart, partList, dominantPipe, primaryUnitAxis, vn, dominantLen))
			{
				continue;
			}
			if (!TryResolveElbowForBranchTerminalFlange(flangePart, partList, runStartFitting, dominantPipe, primaryUnitAxis, vn,
				out _, out _, out _, out XYZ flangePt)
				|| flangePt == null)
			{
				continue;
			}
			double flangeScalar = DotInPlane(flangePt, primaryUnitAxis, vn);
			if (flangeScalar <= runTerminationScalar + 1.0 / 24.0)
			{
				continue;
			}
			string spanKey = openEndPart.Id.Value + "->" + ((Element)flangePart).Id.Value;
			if (!seen.Add(spanKey))
			{
				continue;
			}
			TryAppendAutoDimDiagnosticLog("open-end-flange-overall", view.Name,
				"E-F overall " + FormatElementAnchor(openEndPart, openEndPt) + " to " + FormatElementAnchor((Element)(object)flangePart, flangePt), 0, 0);
			if (TryPlaceSpoolLinearDimensionSleeveStyle(
				doc,
				view,
				openEndPart,
				openEndPt,
				(Element)(object)flangePart,
				flangePt,
				spoolSettings,
				ref stackIndex,
				out string failureDetail,
				FabricationDimensionRefRole.PipeOpenEnd,
				FabricationDimensionRefRole.FlangeFace))
			{
				placed++;
			}
			else if (!string.IsNullOrWhiteSpace(failureDetail))
			{
				failureNotes?.Add("Open-end to flange overall (E-F): " + failureDetail);
			}
		}
		return placed;
	}

	private static bool TryCreateSpoolPassThroughTeeRunDimensions(
		Document doc,
		View view,
		List<FabricationPart> parts,
		FabricationPart pipePart,
		XYZ unitAxis,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		out string diagnostic)
	{
		diagnostic = null;
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (pass-through tee run): view direction is invalid.";
			return false;
		}
		vn = vn.Normalize();
		if (!TryResolvePassThroughTeeRunDimensionExtents(parts, pipePart, unitAxis, vn,
			out FabricationPart minOwner, out XYZ minPt,
			out FabricationPart maxOwner, out XYZ maxPt,
			out List<(FabricationPart part, XYZ point, double scalar)> intermediateAnchors))
		{
			return false;
		}
		double minScalar = DotInPlane(minPt, unitAxis, vn);
		double maxScalar = DotInPlane(maxPt, unitAxis, vn);
		if (maxScalar < minScalar)
		{
			unitAxis = unitAxis.Negate();
			minScalar = DotInPlane(minPt, unitAxis, vn);
			maxScalar = DotInPlane(maxPt, unitAxis, vn);
		}
		if (minPt.DistanceTo(maxPt) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension (pass-through tee run): run extent is too short to dimension.";
			return false;
		}
		List<(FabricationPart part, XYZ point, double scalar)> chain = new List<(FabricationPart, XYZ, double)>
		{
			(minOwner, minPt, minScalar)
		};
		chain.AddRange(intermediateAnchors);
		chain.Add((maxOwner, maxPt, maxScalar));
		TryAppendAutoDimDiagnosticLog("pass-through-tee-run", view.Name,
			$"min={FormatElementAnchor(minOwner, minPt)} max={FormatElementAnchor(maxOwner, maxPt)} intermediates={intermediateAnchors.Count}", 0, 0);
		string failureDetail = null;
		int placed = TryPlaceSpoolRunChainDimensions(
			doc, view, parts, chain, minOwner, minPt, maxOwner, maxPt,
			addOverallWhenFittingOnRun: true,
			FabricationDimensionRefRole.PipeOpenEnd,
			spoolSettings, ref stackIndex, out failureDetail, "pass-through-tee",
			runAxisInView: unitAxis, viewNormal: vn);
		if (placed > 0)
		{
			return true;
		}
		diagnostic = "Auto-dimension (pass-through tee run): could not place run chain dimensions in this view.";
		if (!string.IsNullOrEmpty(failureDetail))
		{
			diagnostic = diagnostic + " " + failureDetail;
		}
		return false;
	}

	private static bool TryCreateSpoolAssemblyHorizontalRunDimensions(Document doc, View view, List<FabricationPart> parts, XYZ unitAxis, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string diagnostic)
	{
		diagnostic = null;
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (assembly horizontal): view direction is invalid.";
			return false;
		}
		vn = vn.Normalize();
		bool hasMainRunFitting = parts.Any((FabricationPart p) => p != null && IsFittingLikeForSpoolDim(p) && !IsOletPart(p));
		if (!hasMainRunFitting)
		{
			return TryCreateBarePipeOpenEndToOpenEndDimension(doc, view, parts, unitAxis, vn, spoolSettings, ref stackIndex, out diagnostic);
		}
		FabricationPart pipePart = GetDominantCollinearRunPipePart(parts, unitAxis, vn);
		if (pipePart == null)
		{
			diagnostic = "Auto-dimension (assembly horizontal): no dominant straight pipe run was found in the assembly.";
			return false;
		}
		if (TryCreateSpoolPassThroughTeeRunDimensions(doc, view, parts, pipePart, unitAxis, spoolSettings, ref stackIndex, out diagnostic))
		{
			return true;
		}
		if (TryCreateSpoolCollinearMainRunChainDimensions(doc, view, parts, pipePart, unitAxis, spoolSettings, ref stackIndex, out diagnostic, "collinear-main"))
		{
			return true;
		}
		diagnostic = null;
		if (!TryWalkCollinearRunToTermination(pipePart, parts, unitAxis, vn, out FabricationPart maxOwner, out XYZ maxPt)
			&& !TryWalkPipeRunToOpenEnd(pipePart, parts, unitAxis, vn, out maxOwner, out maxPt)
			&& !TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, vn, forward: true, out maxOwner, out maxPt)
			&& !TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out maxOwner, out maxPt))
		{
			diagnostic = "Auto-dimension (assembly horizontal): could not resolve the open pipe end along the run.";
			return false;
		}
		if (IsPipeRunPart(maxOwner) && !TryIsOpenCollinearPipeEnd(parts, maxOwner, maxPt)
			&& TryResolvePipeEndFittingAnchor(parts, maxOwner, maxPt, null, out FabricationPart farFit, out XYZ farFitPt))
		{
			maxOwner = farFit;
			maxPt = farFitPt;
		}
		double num = DotInPlane(maxPt, unitAxis, vn);
		// Bare pipe with no fittings on the assembly: E-E (open end to open end) is required — the only
		// valid overall dimension. When any main-run fitting exists, overall must anchor to C or F instead.
		FabricationPart minOwner = null;
		XYZ minPt = null;
		if (!TryResolvePipeRunStartFittingAnchor(parts, pipePart, maxOwner, unitAxis, vn, out minOwner, out minPt)
			&& !TryFindRunStartFittingForCollinearRun(parts, pipePart, unitAxis, vn, out minOwner, out minPt)
			&& !TryResolveLeftRunExtentAnchor(view, parts, pipePart, unitAxis, vn, num, out minOwner, out minPt)
			&& !TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, vn, forward: false, out minOwner, out minPt))
		{
			if (!hasMainRunFitting
				&& (TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis.Negate(), vn, out minOwner, out minPt)
					|| TryWalkPipeRunToOpenEnd(pipePart, parts, unitAxis.Negate(), vn, out minOwner, out minPt)))
			{
				// Pure pipe run, no fitting anywhere (possibly several segments joined end-to-end by field
				// welds) — E-E overall is the only option and is correct here.
			}
			else
			{
				diagnostic = "Auto-dimension (assembly horizontal): could not resolve elbow/tee center (C) at run start — refusing pipe-end-to-pipe-end (E-E).";
				return false;
			}
		}
		if (IsPipeRunPart(minOwner) && !IsOletPart(minOwner) && hasMainRunFitting && !TryIsOpenCollinearPipeEnd(parts, minOwner, minPt))
		{
			diagnostic = "Auto-dimension (assembly horizontal): run start resolved to pipe end (E) but a fitting is on this run — need elbow/tee center (C).";
			return false;
		}
		// A tee (or reducer) sitting mid-run with its far side continuing straight along the same axis
		// (not a turn, not a branch) is a pass-through, not the true run boundary. Extend past it to the
		// real open end, and remember the tee's own center as a nested intermediate anchor so we still
		// get its C-E stacked inside the overall E-E instead of losing the rest of the run.
		FabricationPart passThroughFitting = null;
		XYZ passThroughPt = null;
		if (TryExtendRunPastPassThroughFitting(minOwner, pipePart, parts, unitAxis, vn, out var passFarOwner, out var passFarPt))
		{
			passThroughFitting = minOwner;
			passThroughPt = minPt;
			minOwner = passFarOwner;
			minPt = passFarPt;
		}
		double num2 = DotInPlane(minPt, unitAxis, vn);
		double num3 = DotInPlane(maxPt, unitAxis, vn);
		if (num3 < num2)
		{
			unitAxis = unitAxis.Negate();
			if (!TryWalkCollinearRunToTermination(pipePart, parts, unitAxis, vn, out maxOwner, out maxPt)
				&& !TryWalkPipeRunToOpenEnd(pipePart, parts, unitAxis, vn, out maxOwner, out maxPt)
				&& !TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, vn, forward: true, out maxOwner, out maxPt)
				&& !TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out maxOwner, out maxPt))
			{
				diagnostic = "Auto-dimension (assembly horizontal): could not re-resolve open end after axis alignment.";
				return false;
			}
			if (IsPipeRunPart(maxOwner) && !TryIsOpenCollinearPipeEnd(parts, maxOwner, maxPt)
				&& TryResolvePipeEndFittingAnchor(parts, maxOwner, maxPt, null, out FabricationPart farFitFlip, out XYZ farFitPtFlip))
			{
				maxOwner = farFitFlip;
				maxPt = farFitPtFlip;
			}
			num3 = DotInPlane(maxPt, unitAxis, vn);
			if (!TryResolvePipeRunStartFittingAnchor(parts, pipePart, maxOwner, unitAxis, vn, out minOwner, out minPt)
				&& !TryFindRunStartFittingForCollinearRun(parts, pipePart, unitAxis, vn, out minOwner, out minPt)
				&& !TryResolveLeftRunExtentAnchor(view, parts, pipePart, unitAxis, vn, num3, out minOwner, out minPt)
				&& !TryResolveCollinearAssemblyOpenEnd(parts, unitAxis, vn, forward: false, out minOwner, out minPt)
				&& !(!hasMainRunFitting
					&& (TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis.Negate(), vn, out minOwner, out minPt)
						|| TryWalkPipeRunToOpenEnd(pipePart, parts, unitAxis.Negate(), vn, out minOwner, out minPt))))
			{
				diagnostic = "Auto-dimension (assembly horizontal): could not re-resolve fitting center (C) after axis alignment.";
				return false;
			}
			passThroughFitting = null;
			passThroughPt = null;
			if (TryExtendRunPastPassThroughFitting(minOwner, pipePart, parts, unitAxis, vn, out passFarOwner, out passFarPt))
			{
				passThroughFitting = minOwner;
				passThroughPt = minPt;
				minOwner = passFarOwner;
				minPt = passFarPt;
			}
			num2 = DotInPlane(minPt, unitAxis, vn);
		}
		if (minPt.DistanceTo(maxPt) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension (assembly horizontal): run extent is too short to dimension.";
			return false;
		}
		double endTol = 1.0 / 24.0;
		string text = null;
		TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, $"full-run min={FormatElementAnchor(minOwner, minPt)} max={FormatElementAnchor(maxOwner, maxPt)}", 0, 0);

		List<(FabricationPart part, XYZ point, double scalar)> chain = BuildOrderedRunDimensionChain(
			view, parts, pipePart, minOwner, minPt, num2, maxOwner, maxPt, num3,
			passThroughFitting, passThroughPt, unitAxis, vn, endTol);

		int num4 = TryPlaceSpoolRunChainDimensions(
			doc, view, parts, chain, minOwner, minPt, maxOwner, maxPt,
			addOverallWhenFittingOnRun: hasMainRunFitting,
			FabricationDimensionRefRole.PipeOpenEnd,
			spoolSettings, ref stackIndex, out text, "horizontal",
			runAxisInView: unitAxis, viewNormal: vn);
		if (num4 > 0)
		{
			diagnostic = null;
			return true;
		}
		diagnostic = "Auto-dimension (assembly horizontal): could not place horizontal dimensions in this view.";
		if (!string.IsNullOrEmpty(text))
		{
			diagnostic = diagnostic + " " + text;
		}
		return false;
	}

	private static bool TryCreateSpoolDominantPipeRunLengthDimension(Document doc, View view, List<FabricationPart> parts, XYZ unitAxis, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string diagnostic)
	{
		diagnostic = null;
		FabricationPart pipePart = GetDominantStraightFabricationPart(parts);
		if (pipePart == null)
		{
			diagnostic = "Auto-dimension (pipe run length): no dominant straight fabrication pipe run was found in the assembly.";
			return false;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (pipe run length): view direction is invalid.";
			return false;
		}
		vn = vn.Normalize();
		if (!TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out var endOwnerMax, out var pipeEndMax) || !TryGetMinimumPipeEndAnchor(parts, pipePart, unitAxis, vn, out var endOwnerMin, out var pipeEndMin))
		{
			diagnostic = "Auto-dimension (pipe run length): could not resolve both ends of the dominant pipe run.";
			return false;
		}
		if (pipeEndMax.DistanceTo(pipeEndMin) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension (pipe run length): pipe ends are too close together to dimension.";
			return false;
		}
		if (DotInPlane(pipeEndMin, unitAxis, vn) > DotInPlane(pipeEndMax, unitAxis, vn))
		{
			FabricationPart val = endOwnerMax;
			endOwnerMax = endOwnerMin;
			endOwnerMin = val;
			XYZ val2 = pipeEndMax;
			pipeEndMax = pipeEndMin;
			pipeEndMin = val2;
		}
		FabricationPart val3 = endOwnerMin ?? pipePart;
		FabricationPart val4 = endOwnerMax ?? pipePart;
		if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, (Element)(object)val3, pipeEndMin, (Element)(object)val4, pipeEndMax, spoolSettings, ref stackIndex, out var failureDetail))
		{
			return true;
		}
		diagnostic = "Auto-dimension (pipe run length): could not create a linear dimension in this 2D view.";
		if (!string.IsNullOrEmpty(failureDetail))
		{
			diagnostic = diagnostic + " " + failureDetail;
		}
		return false;
	}

	private static bool TryCreateSpoolFittingCenterToFittingCenterDimension(Document doc, View view, List<FabricationPart> parts, XYZ unitAxis, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string diagnostic)
	{
		diagnostic = null;
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (fitting center to fitting center): view direction is invalid.";
			return false;
		}
		vn = vn.Normalize();
		List<FabricationPart> list = (from p in parts
			where IsFittingLikeForSpoolDim(p) && !IsOletPart(p)
			orderby ScalarAlong(p, unitAxis, vn)
			select p).ToList();
		if (list.Count < 2)
		{
			diagnostic = "Auto-dimension (fitting center to fitting center): need at least two fittings in the assembly.";
			return false;
		}
		FabricationPart val = list.First();
		FabricationPart val2 = list.Last();
		if (((Element)val).Id == ((Element)val2).Id)
		{
			diagnostic = "Auto-dimension (fitting center to fitting center): only one distinct fitting was found.";
			return false;
		}
		XYZ fabricationFittingDimensionAnchor = GetFabricationFittingDimensionAnchor(val, null, null, parts);
		XYZ fabricationFittingDimensionAnchor2 = GetFabricationFittingDimensionAnchor(val2, null, null, parts);
		if (fabricationFittingDimensionAnchor == null || fabricationFittingDimensionAnchor2 == null)
		{
			diagnostic = "Auto-dimension (fitting center to fitting center): could not resolve fitting anchor points.";
			return false;
		}
		if (fabricationFittingDimensionAnchor.DistanceTo(fabricationFittingDimensionAnchor2) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension (fitting center to fitting center): fittings are too close together to dimension.";
			return false;
		}
		if (DotInPlane(fabricationFittingDimensionAnchor, unitAxis, vn) > DotInPlane(fabricationFittingDimensionAnchor2, unitAxis, vn))
		{
			FabricationPart val3 = val;
			val = val2;
			val2 = val3;
			XYZ val4 = fabricationFittingDimensionAnchor;
			fabricationFittingDimensionAnchor = fabricationFittingDimensionAnchor2;
			fabricationFittingDimensionAnchor2 = val4;
		}
		if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, (Element)(object)val, fabricationFittingDimensionAnchor, (Element)(object)val2, fabricationFittingDimensionAnchor2, spoolSettings, ref stackIndex, out var failureDetail))
		{
			return true;
		}
		diagnostic = "Auto-dimension (fitting center to fitting center): could not create a linear dimension in this 2D view.";
		if (!string.IsNullOrEmpty(failureDetail))
		{
			diagnostic = diagnostic + " " + failureDetail;
		}
		return false;
	}

	private static bool TryCreateSpoolPipeEndFittingPipeChainDimension(Document doc, View view, List<FabricationPart> parts, XYZ unitAxis, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string diagnostic)
	{
		diagnostic = null;
		FabricationPart pipePart = GetDominantStraightFabricationPart(parts);
		if (pipePart == null)
		{
			diagnostic = "Auto-dimension (pipe end chain): no dominant straight fabrication pipe run was found in the assembly.";
			return false;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (pipe end chain): view direction is invalid.";
			return false;
		}
		vn = vn.Normalize();
		if (!TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out var endOwnerMax, out var pipeEndMax) || !TryGetMinimumPipeEndAnchor(parts, pipePart, unitAxis, vn, out var endOwnerMin, out var pipeEndMin))
		{
			diagnostic = "Auto-dimension (pipe end chain): could not resolve both open pipe ends along the run.";
			return false;
		}
		if (pipeEndMax.DistanceTo(pipeEndMin) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension (pipe end chain): pipe ends are too close together to chain dimension.";
			return false;
		}
		double num = DotInPlane(pipeEndMin, unitAxis, vn);
		double num2 = DotInPlane(pipeEndMax, unitAxis, vn);
		FabricationPart val = (from p in parts
			where IsFittingLikeForSpoolDim(p) && !IsOletPart(p) && ((Element)p).Id != ((Element)pipePart).Id
			let scalar = ScalarAlong(p, unitAxis, vn)
			where scalar > num + 1.0 / 48.0 && scalar < num2 - 1.0 / 48.0
			orderby Math.Abs(scalar - (num + num2) * 0.5)
			select p).FirstOrDefault();
		if (val == null)
		{
			diagnostic = "Auto-dimension (pipe end chain): no fitting center was found between the two pipe ends.";
			return false;
		}
		XYZ fabricationFittingDimensionAnchor = GetFabricationFittingDimensionAnchor(val, pipePart, endOwnerMax, parts);
		if (fabricationFittingDimensionAnchor == null)
		{
			diagnostic = "Auto-dimension (pipe end chain): could not resolve the fitting center anchor.";
			return false;
		}
		if (TryPlaceSpoolLinearDimensionChainStyle(doc, view, new (Element, XYZ)[3]
		{
			((Element)(object)endOwnerMin, pipeEndMin),
			((Element)(object)val, fabricationFittingDimensionAnchor),
			((Element)(object)endOwnerMax, pipeEndMax)
		}, spoolSettings, ref stackIndex, out var failureDetail))
		{
			return true;
		}
		diagnostic = "Auto-dimension (pipe end chain): could not create a chained model Dimension.";
		if (!string.IsNullOrEmpty(failureDetail))
		{
			diagnostic = diagnostic + " " + failureDetail;
		}
		return false;
	}

	private static bool TryCreateSingleSpoolPipeEndToFittingDimension(Document doc, View view, List<FabricationPart> parts, XYZ unitAxis, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string diagnostic)
	{
		diagnostic = null;
		FabricationPart pipePart = GetDominantStraightFabricationPart(parts);
		if (pipePart == null)
		{
			diagnostic = "Auto-dimension: no dominant straight fabrication pipe run was found in the assembly.";
			return false;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension: view direction is invalid.";
			return false;
		}
		vn = vn.Normalize();
		List<FabricationPart> source = parts.Where((FabricationPart p) => ((Element)p).Id != ((Element)pipePart).Id && !IsGasketPart(p) && !IsWeldPart(p)).ToList();
		if (!TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out var endOwner, out var pipeEndPt))
		{
			diagnostic = "Auto-dimension: could not find an open (unconnected) pipe-end anchor along the run, or no connector projects past the fitting.";
			return false;
		}
		double openEndScalar = DotInPlane(pipeEndPt, unitAxis, vn);
		FabricationPart val = (from p in source
			where IsFittingLikeForSpoolDim(p) && !IsOletPart(p)
			let anchor = GetFabricationFittingDimensionAnchor(p, pipePart, endOwner, parts)
			where anchor != null
			orderby Math.Abs(DotInPlane(anchor, unitAxis, vn) - openEndScalar)
			select p).FirstOrDefault();
		if (val == null)
		{
			val = source.FirstOrDefault();
		}
		if (val == null)
		{
			diagnostic = "Auto-dimension: no fitting or second part found beside the dominant pipe run.";
			return false;
		}
		XYZ fabricationFittingDimensionAnchor = GetFabricationFittingDimensionAnchor(val, pipePart, endOwner, parts);
		if (fabricationFittingDimensionAnchor == null)
		{
			diagnostic = "Auto-dimension: could not get an anchor point on the primary fitting (joint, centerline, or bounding box).";
			return false;
		}
		double num = openEndScalar;
		double num2 = DotInPlane(fabricationFittingDimensionAnchor, unitAxis, vn);
		if (num < num2)
		{
			unitAxis = unitAxis.Negate();
			if (!TryGetPrimaryPipeEndAnchor(parts, pipePart, unitAxis, vn, out endOwner, out pipeEndPt))
			{
				diagnostic = "Auto-dimension: after aligning run direction, pipe-end anchor could not be resolved.";
				return false;
			}
			fabricationFittingDimensionAnchor = GetFabricationFittingDimensionAnchor(val, pipePart, endOwner, parts);
			if (fabricationFittingDimensionAnchor == null)
			{
				diagnostic = "Auto-dimension: could not re-resolve fitting center after flipping run axis.";
				return false;
			}
		}
		if (pipeEndPt.DistanceTo(fabricationFittingDimensionAnchor) < 1.0 / 24.0)
		{
			diagnostic = "Auto-dimension: pipe end is too close to the fitting center to place a meaningful dimension.";
			return false;
		}
		if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, (Element)(object)val, fabricationFittingDimensionAnchor, (Element)(object)endOwner, pipeEndPt, spoolSettings, ref stackIndex, out var failureDetail))
		{
			return true;
		}
		diagnostic = "Auto-dimension: could not create a model Dimension (try another view template or confirm geometry is visible in this view).";
		if (!string.IsNullOrEmpty(failureDetail))
		{
			diagnostic = diagnostic + " " + failureDetail;
		}
		return false;
	}

	/// <summary>Flange mated to this pipe connector (directly or through one weld/gasket).</summary>
	private static FabricationPart ResolveFlangeAtPipeConnector(FabricationPart pipe, Connector connector, IList<FabricationPart> parts)
	{
		if (pipe == null || connector == null || parts == null)
		{
			return null;
		}
		FabricationPart mate = FindMateAtConnector(pipe, connector, parts);
		if (mate == null)
		{
			return null;
		}
		Document doc = ((Element)pipe).Document;
		if (FabricationPartClassification.IsFlangePart(mate, doc))
		{
			return mate;
		}
		if (IsGasketPart(mate) || IsWeldPart(mate))
		{
			FabricationPart beyond = FindFarSideMateThroughJoint(mate, pipe, parts);
			if (beyond != null && FabricationPartClassification.IsFlangePart(beyond, doc))
			{
				return beyond;
			}
		}
		return null;
	}

	private static FabricationPart ResolveFittingAtPipeConnector(FabricationPart pipe, Connector connector, IList<FabricationPart> parts)
	{
		if (pipe == null || connector == null || parts == null)
		{
			return null;
		}
		foreach (FabricationPart mate in EnumerateMatedFabricationParts(pipe, parts))
		{
			if (!IsConnectorMatedToPart(connector, pipe, mate, parts))
			{
				continue;
			}
			if (IsFittingLikeForSpoolDim(mate) && !IsOletPart(mate) && !IsValvePart(mate))
			{
				return mate;
			}
			if (IsGasketPart(mate) || IsWeldPart(mate))
			{
				FabricationPart beyond = FindFarSideMateThroughJoint(mate, pipe, parts);
				if (beyond != null && IsFittingLikeForSpoolDim(beyond) && !IsOletPart(beyond) && !IsValvePart(beyond))
				{
					return beyond;
				}
			}
		}
		return null;
	}

	private static bool IsConnectorMatedToPart(Connector connector, FabricationPart self, FabricationPart mate, IList<FabricationPart> partsPool)
	{
		if (connector == null || self == null || mate == null)
		{
			return false;
		}
		FabricationPart atConnector = FindMateAtConnector(self, connector, partsPool);
		if (atConnector != null && ((Element)atConnector).Id == ((Element)mate).Id)
		{
			return true;
		}
		if (IsGasketPart(atConnector) || IsWeldPart(atConnector))
		{
			FabricationPart beyond = FindFarSideMateThroughJoint(atConnector, self, partsPool);
			if (beyond != null && ((Element)beyond).Id == ((Element)mate).Id)
			{
				return true;
			}
		}
		ConnectorSet refs = connector.AllRefs;
		if (refs == null)
		{
			return false;
		}
		foreach (Connector refConn in refs)
		{
			if (refConn?.Owner is FabricationPart refPart && ((Element)refPart).Id == ((Element)mate).Id)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>True fitting (C) at each end of a pipe leg — fitting-pipe-fitting topology.</summary>
	private static bool TryResolveFittingsAtPipeEnds(
		FabricationPart pipe,
		IList<FabricationPart> parts,
		XYZ legAxis,
		XYZ viewNormal,
		out FabricationPart lowFitting,
		out XYZ lowPt,
		out FabricationPart highFitting,
		out XYZ highPt)
	{
		lowFitting = null;
		lowPt = null;
		highFitting = null;
		highPt = null;
		if (pipe == null || parts == null || legAxis == null || viewNormal == null)
		{
			return false;
		}
		double lowScalar = double.MaxValue;
		double highScalar = double.MinValue;
		foreach (Connector connector in ListConnectors(pipe))
		{
			if (connector?.Origin == null)
			{
				continue;
			}
			FabricationPart fit = ResolveFittingAtPipeConnector(pipe, connector, parts);
			if (fit == null)
			{
				FabricationPart jointMate = FindMateAtConnector(pipe, connector, parts);
				if (jointMate != null && (IsGasketPart(jointMate) || IsWeldPart(jointMate)))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(jointMate, pipe, parts);
					if (beyond != null && IsFittingLikeForSpoolDim(beyond) && !IsOletPart(beyond) && !IsValvePart(beyond))
					{
						fit = beyond;
					}
				}
			}
			if (fit == null)
			{
				continue;
			}
			double scalar = DotInPlane(connector.Origin, legAxis, viewNormal);
			XYZ anchor = GetFabricationFittingDimensionAnchor(fit, pipe, null, parts);
			if (anchor == null)
			{
				continue;
			}
			if (scalar < lowScalar)
			{
				lowScalar = scalar;
				lowFitting = fit;
				lowPt = anchor;
			}
			if (scalar > highScalar)
			{
				highScalar = scalar;
				highFitting = fit;
				highPt = anchor;
			}
		}
		if (lowFitting == null || highFitting == null || lowPt == null || highPt == null)
		{
			return false;
		}
		if (((Element)lowFitting).Id == ((Element)highFitting).Id)
		{
			return false;
		}
		return lowPt.DistanceTo(highPt) >= 1.0 / 24.0;
	}

	/// <summary>Raised face F with fallback — never leave a flange without an anchor.</summary>
	private static bool TryResolveFlangeFaceAnchorPoint(FabricationPart flange, FabricationPart pipeHint, IList<FabricationPart> parts, out XYZ point)
	{
		point = null;
		if (flange == null)
		{
			return false;
		}
		if (TryGetFlangeRaisedFaceAnchorPoint(flange, pipeHint, parts, out point))
		{
			return true;
		}
		point = GetFabricationFittingDimensionAnchor(flange, pipeHint, null, parts);
		return point != null;
	}

	private static bool ShouldSkipFlangeBranchTraversal(
		FabricationPart mate,
		FabricationPart dominantPipe,
		double dominantLen,
		IList<FabricationPart> parts,
		XYZ primaryUnitAxis,
		XYZ viewNormal,
		FabricationPart runStartFitting)
	{
		if (mate == null || IsValvePart(mate) || IsGasketPart(mate) || IsWeldPart(mate))
		{
			return true;
		}
		if (runStartFitting != null && ((Element)mate).Id == ((Element)runStartFitting).Id)
		{
			return true;
		}
		if (IsPipeRunPart(mate))
		{
			if (IsVerticalDropLeg(mate, parts, primaryUnitAxis, viewNormal))
			{
				return true;
			}
			if (dominantPipe != null
				&& IsCollinearWithPrimaryRun(mate, parts, primaryUnitAxis, viewNormal)
				&& dominantLen > 1.0 / 24.0
				&& GetFabricationStraightLineLength(mate) > dominantLen * 0.25)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// BFS from a fitting to the nearest terminal flange on a branch leg (top elbow → flange stub).
	/// Never walks down the vertical drop or along the main horizontal run.
	/// </summary>
	private static bool TryFindAdjacentTerminalFlangeFromFitting(
		FabricationPart fitting,
		IList<FabricationPart> parts,
		FabricationPart runStartFitting,
		FabricationPart dominantPipe,
		XYZ primaryUnitAxis,
		XYZ viewNormal,
		out FabricationPart flange,
		out FabricationPart stubPipe,
		out XYZ fittingPt,
		out XYZ flangePt)
	{
		flange = null;
		stubPipe = null;
		fittingPt = null;
		flangePt = null;
		if (fitting == null || parts == null)
		{
			return false;
		}
		if (TryResolveDirectFlangeOnFitting(fitting, parts, out flange, out fittingPt, out flangePt))
		{
			return true;
		}
		double dominantLen = dominantPipe != null ? GetFabricationStraightLineLength(dominantPipe) : 0.0;
		Document doc = ((Element)fitting).Document;
		Queue<(FabricationPart part, FabricationPart from, FabricationPart firstPipe, int depth)> queue = new Queue<(FabricationPart, FabricationPart, FabricationPart, int)>();
		queue.Enqueue((fitting, null, null, 0));
		HashSet<long> visited = new HashSet<long> { ((Element)fitting).Id.Value };
		for (int guard = 0; guard < 48 && queue.Count > 0; guard++)
		{
			(FabricationPart current, FabricationPart from, FabricationPart firstPipe, int depth) = queue.Dequeue();
			if (current == null || depth > 8)
			{
				continue;
			}
			if (FabricationPartClassification.IsFlangePart(current, doc))
			{
				stubPipe = firstPipe;
				fittingPt = GetFabricationFittingDimensionAnchor(fitting, stubPipe, null, parts);
				XYZ face = ResolveFlangeFaceAnchorForDimension(current, fitting, parts);
				if (fittingPt != null && face != null && fittingPt.DistanceTo(face) >= 1.0 / 48.0)
				{
					flange = current;
					flangePt = face;
					return true;
				}
				continue;
			}
			foreach (Connector connector in ListConnectors(current))
			{
				if (connector?.Origin == null)
				{
					continue;
				}
				foreach (FabricationPart mate in EnumerateMatedFabricationParts(current, parts))
				{
					if (from != null && ((Element)mate).Id == ((Element)from).Id)
					{
						continue;
					}
					if (IsGasketPart(mate) || IsWeldPart(mate))
					{
						FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, parts);
						if (beyond != null && visited.Add(((Element)beyond).Id.Value))
						{
							FabricationPart pipeHint = firstPipe ?? (IsPipeRunPart(beyond) ? beyond : null);
							queue.Enqueue((beyond, current, pipeHint, depth + 1));
						}
						continue;
					}
					if (ShouldSkipFlangeBranchTraversal(mate, dominantPipe, dominantLen, parts, primaryUnitAxis, viewNormal, runStartFitting))
					{
						continue;
					}
					if (!visited.Add(((Element)mate).Id.Value))
					{
						continue;
					}
					FabricationPart nextPipe = firstPipe ?? (IsPipeRunPart(mate) ? mate : null);
					queue.Enqueue((mate, current, nextPipe, depth + 1));
				}
			}
		}
		return false;
	}

	private static IEnumerable<FabricationPart> EnumerateMatedFabricationParts(FabricationPart self, IList<FabricationPart> partsPool)
	{
		if (self == null || partsPool == null)
		{
			yield break;
		}
		HashSet<long> poolIds = new HashSet<long>(partsPool.Where((p) => p != null).Select((p) => ((Element)p).Id.Value));
		HashSet<long> yielded = new HashSet<long>();
		foreach (Connector connector in ListConnectors(self))
		{
			if (connector == null)
			{
				continue;
			}
			ConnectorSet refs = connector.AllRefs;
			if (refs != null)
			{
				foreach (Connector refConn in refs)
				{
					if (refConn?.Owner is FabricationPart mate
						&& poolIds.Contains(((Element)mate).Id.Value)
						&& ((Element)mate).Id != ((Element)self).Id
						&& yielded.Add(((Element)mate).Id.Value))
					{
						yield return mate;
					}
				}
			}
			FabricationPart distMate = FindMateAtConnector(self, connector, partsPool);
			if (distMate != null && yielded.Add(((Element)distMate).Id.Value))
			{
				yield return distMate;
			}
		}
	}

	/// <summary>Stub/branch flanges only — not weld-necks sitting on the dominant horizontal run.</summary>
	private static bool IsBranchTerminalFlange(
		FabricationPart flange,
		IList<FabricationPart> parts,
		FabricationPart dominantPipe,
		XYZ unitAxis,
		XYZ viewNormal,
		double dominantLen)
	{
		if (flange == null || !FabricationPartClassification.IsFlangePart(flange, ((Element)flange).Document))
		{
			return false;
		}
		// Elbow+flange with no pipe between still counts — never let context-pipe fallback hide it.
		if (TryFlangeHasNearbyBranchFitting(flange, parts, runStartFitting: null, maxDepth: 4, out _))
		{
			return true;
		}
		FabricationPart contextPipe = FindRunContextPipeForFlange(flange, parts, dominantPipe, unitAxis, viewNormal);
		if (contextPipe == null)
		{
			return true;
		}
		if (dominantPipe != null && ((Element)contextPipe).Id == ((Element)dominantPipe).Id)
		{
			return false;
		}
		return !(dominantLen > 1.0 / 24.0
			&& IsCollinearWithPrimaryRun(contextPipe, parts, unitAxis, viewNormal)
			&& GetFabricationStraightLineLength(contextPipe) > dominantLen * 0.45);
	}

	/// <summary>Any elbow/tee within a few hops of this flange (direct elbow+flange counts).</summary>
	private static bool TryFlangeHasNearbyBranchFitting(
		FabricationPart flange,
		IList<FabricationPart> parts,
		FabricationPart runStartFitting,
		int maxDepth,
		out FabricationPart fitting)
	{
		fitting = null;
		if (flange == null || parts == null)
		{
			return false;
		}
		Document doc = ((Element)flange).Document;
		Queue<(FabricationPart part, FabricationPart from, int depth)> queue = new Queue<(FabricationPart, FabricationPart, int)>();
		queue.Enqueue((flange, null, 0));
		HashSet<long> visited = new HashSet<long> { ((Element)flange).Id.Value };
		while (queue.Count > 0)
		{
			(FabricationPart current, FabricationPart from, int depth) = queue.Dequeue();
			if (current == null || depth > maxDepth)
			{
				continue;
			}
			if (IsFittingLikeForSpoolDim(current) && !IsOletPart(current) && !IsValvePart(current)
				&& !FabricationPartClassification.IsFlangePart(current, doc)
				&& (runStartFitting == null || ((Element)current).Id != ((Element)runStartFitting).Id))
			{
				fitting = current;
				return true;
			}
			foreach (FabricationPart mate in EnumerateMatedFabricationParts(current, parts))
			{
				if (from != null && ((Element)mate).Id == ((Element)from).Id)
				{
					continue;
				}
				if (IsValvePart(mate))
				{
					continue;
				}
				if (IsGasketPart(mate) || IsWeldPart(mate))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, parts);
					if (beyond != null && visited.Add(((Element)beyond).Id.Value))
					{
						queue.Enqueue((beyond, current, depth + 1));
					}
					continue;
				}
				if (!visited.Add(((Element)mate).Id.Value))
				{
					continue;
				}
				queue.Enqueue((mate, current, depth + 1));
			}
		}
		return false;
	}

	/// <summary>
	/// Immediate elbow/tee → flange on the same connector (through weld/gasket at most).
	/// This is the universal C-F rule when two fittings are together.
	/// </summary>
	private static bool TryResolveFlangeAdjacentToFitting(
		FabricationPart fitting,
		IList<FabricationPart> parts,
		out FabricationPart flange,
		out XYZ fittingPt,
		out XYZ flangePt)
	{
		flange = null;
		fittingPt = null;
		flangePt = null;
		if (fitting == null || parts == null)
		{
			return false;
		}
		Document doc = ((Element)fitting).Document;
		foreach (FabricationPart mate in EnumerateMatedFabricationParts(fitting, parts))
		{
			FabricationPart flangeCandidate = null;
			if (FabricationPartClassification.IsFlangePart(mate, doc))
			{
				flangeCandidate = mate;
			}
			else if (IsGasketPart(mate) || IsWeldPart(mate))
			{
				FabricationPart beyond = FindFarSideMateThroughJoint(mate, fitting, parts);
				if (beyond != null && FabricationPartClassification.IsFlangePart(beyond, doc))
				{
					flangeCandidate = beyond;
				}
			}
			else if (IsPipeRunPart(mate) && GetFabricationStraightLineLength(mate) <= 2.0)
			{
				if (TryResolveFittingToFlangeOnPipeEnds(mate, parts, out FabricationPart pipeFit, out XYZ pipeFitPt, out FabricationPart pipeFlange, out XYZ pipeFlangePt)
					&& pipeFit != null && pipeFlange != null && ((Element)pipeFit).Id == ((Element)fitting).Id)
				{
					flange = pipeFlange;
					fittingPt = pipeFitPt;
					flangePt = pipeFlangePt;
					return true;
				}
			}
			if (flangeCandidate == null)
			{
				continue;
			}
			XYZ fitAnchor = GetFabricationFittingDimensionAnchor(fitting, null, null, parts);
			XYZ face = ResolveFlangeFaceAnchorForDimension(flangeCandidate, fitting, parts);
			if (fitAnchor != null && face != null && fitAnchor.DistanceTo(face) >= 1.0 / 48.0)
			{
				flange = flangeCandidate;
				fittingPt = fitAnchor;
				flangePt = face;
				return true;
			}
		}
		return false;
	}

	private static XYZ ResolveFlangeFaceAnchorForDimension(FabricationPart flange, FabricationPart toward, IList<FabricationPart> parts)
	{
		if (flange == null)
		{
			return null;
		}
		if (TryGetFlangeRaisedFaceAnchorPoint(flange, toward, parts, out XYZ face))
		{
			return face;
		}
		XYZ towardPt = toward != null ? GetFabricationFittingDimensionAnchor(toward, null, null, parts) : null;
		if (towardPt != null)
		{
			Connector best = null;
			double bestDist = -1.0;
			foreach (Connector connector in ListConnectors(flange))
			{
				if (connector?.Origin == null)
				{
					continue;
				}
				double dist = connector.Origin.DistanceTo(towardPt);
				if (dist > bestDist)
				{
					bestDist = dist;
					best = connector;
				}
			}
			if (best != null)
			{
				return best.Origin;
			}
		}
		return TryGetFabricationPartOrigin(flange);
	}

	/// <summary>BFS from a branch-terminal flange to the elbow/tee it caps (top elbow on L-spool stub).</summary>
	private static bool TryResolveElbowForBranchTerminalFlange(
		FabricationPart flange,
		IList<FabricationPart> parts,
		FabricationPart runStartFitting,
		FabricationPart dominantPipe,
		XYZ primaryUnitAxis,
		XYZ viewNormal,
		out FabricationPart fitting,
		out FabricationPart stubPipe,
		out XYZ fittingPt,
		out XYZ flangePt)
	{
		fitting = null;
		stubPipe = null;
		fittingPt = null;
		flangePt = null;
		if (flange == null || parts == null)
		{
			return false;
		}
		flangePt = ResolveFlangeFaceAnchorForDimension(flange, null, parts);
		if (flangePt == null)
		{
			return false;
		}
		double dominantLen = dominantPipe != null ? GetFabricationStraightLineLength(dominantPipe) : 0.0;
		Document doc = ((Element)flange).Document;
		Queue<(FabricationPart part, FabricationPart from, FabricationPart firstPipe, int depth)> queue = new Queue<(FabricationPart, FabricationPart, FabricationPart, int)>();
		queue.Enqueue((flange, null, null, 0));
		HashSet<long> visited = new HashSet<long> { ((Element)flange).Id.Value };
		for (int guard = 0; guard < 48 && queue.Count > 0; guard++)
		{
			(FabricationPart current, FabricationPart from, FabricationPart firstPipe, int depth) = queue.Dequeue();
			if (current == null || depth > 8)
			{
				continue;
			}
			if (IsFittingLikeForSpoolDim(current) && !IsOletPart(current) && !IsValvePart(current)
				&& !FabricationPartClassification.IsFlangePart(current, doc)
				&& (runStartFitting == null || ((Element)current).Id != ((Element)runStartFitting).Id))
			{
				fittingPt = GetFabricationFittingDimensionAnchor(current, stubPipe ?? firstPipe, null, parts);
				if (fittingPt != null && fittingPt.DistanceTo(flangePt) >= 1.0 / 48.0)
				{
					fitting = current;
					stubPipe = firstPipe;
					return true;
				}
			}
			foreach (FabricationPart mate in EnumerateMatedFabricationParts(current, parts))
			{
				if (from != null && ((Element)mate).Id == ((Element)from).Id)
				{
					continue;
				}
				if (IsGasketPart(mate) || IsWeldPart(mate))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, parts);
					if (beyond != null && visited.Add(((Element)beyond).Id.Value))
					{
						FabricationPart pipeHint = firstPipe ?? (IsPipeRunPart(beyond) ? beyond : null);
						queue.Enqueue((beyond, current, pipeHint, depth + 1));
					}
					continue;
				}
				if (ShouldSkipFlangeBranchTraversal(mate, dominantPipe, dominantLen, parts, primaryUnitAxis, viewNormal, runStartFitting))
				{
					continue;
				}
				if (!visited.Add(((Element)mate).Id.Value))
				{
					continue;
				}
				FabricationPart nextPipe = firstPipe ?? (IsPipeRunPart(mate) ? mate : null);
				queue.Enqueue((mate, current, nextPipe, depth + 1));
			}
		}
		return false;
	}

	private static bool TryPlaceFittingFlangeStubDimension(
		Document doc,
		View view,
		FabricationPart fitting,
		XYZ fittingPt,
		FabricationPart flange,
		XYZ flangePt,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		out string failureDetail)
	{
		failureDetail = null;
		if (doc == null || view == null || fitting == null || flange == null || fittingPt == null || flangePt == null)
		{
			failureDetail = "Missing stub dimension anchors.";
			return false;
		}
		XYZ chord = flangePt - fittingPt;
		if (chord == null || chord.GetLength() < 1.0 / 48.0)
		{
			failureDetail = "Stub elbow-to-flange span is too short.";
			return false;
		}
		XYZ chordUnit = chord.Normalize();
		List<Reference> fitRefs = GetAllFabricationInstanceDimensionReferences((Element)(object)fitting, view, fittingPt, FabricationDimensionRefRole.RunStartFitting, chordUnit);
		if (fitRefs.Count == 0)
		{
			Reference centerRef = TryResolveFabricationCenterlineReference((Element)(object)fitting, view, fittingPt, chordUnit);
			if (centerRef != null)
			{
				fitRefs.Add(centerRef);
			}
			if (TryPickBestScoredFabricationAnchorReference((Element)(object)fitting, view, fittingPt, FabricationDimensionRefRole.RunStartFitting, chordUnit, applySnapFilter: false, out Reference scoredFit))
			{
				fitRefs.Add(scoredFit);
			}
		}
		List<Reference> flangeRefs = GetFlangeFaceDimensionReferencesWithFallback((Element)(object)flange, view, flangePt, chordUnit.Negate());
		if (fitRefs.Count == 0 || flangeRefs.Count == 0)
		{
			failureDetail = "Stub C-F: no dimension references (fit=" + fitRefs.Count + " flange=" + flangeRefs.Count + ").";
			int savedStack = stackIndex;
			foreach (int sign in new[] { 1, -1 })
			{
				stackIndex = savedStack;
				if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, (Element)(object)fitting, fittingPt, (Element)(object)flange, flangePt, spoolSettings, ref stackIndex, out failureDetail, FabricationDimensionRefRole.RunStartFitting, FabricationDimensionRefRole.FlangeFace, sign, lockOffsetSign: false, branchFacingDirection: null, dimensionPolicyRole: "fitting-flange-stub"))
				{
					return true;
				}
			}
			stackIndex = savedStack;
			return false;
		}
		if (!TryGetViewSketchPlane(view, out XYZ planeOrigin, out XYZ planeNormal))
		{
			failureDetail = "Could not resolve view sketch plane.";
			return false;
		}
		View dimView = view;
		try
		{
			View activeView = doc.ActiveView;
			if (activeView != null && ((Element)activeView).Id == ((Element)view).Id)
			{
				dimView = activeView;
			}
		}
		catch
		{
		}
		XYZ pFitP = ProjectToSketchPlane(fittingPt, planeOrigin, planeNormal);
		XYZ pFlangeP = ProjectToSketchPlane(flangePt, planeOrigin, planeNormal);
		XYZ vn = planeNormal.Normalize();
		XYZ chordInPlane = pFlangeP - pFitP;
		XYZ perp = vn.CrossProduct(chordInPlane.GetLength() > 1E-09 ? chordInPlane.Normalize() : view.RightDirection);
		if (perp.GetLength() < 1E-09)
		{
			perp = view.UpDirection;
		}
		perp = perp.Normalize();
		Document refDoc = view.Document;
		int savedStackIdx = stackIndex;
		string lastFail = null;
		Reference lockedFitRef = TryResolveFabricationCenterlineReference((Element)(object)fitting, view, fittingPt, chordUnit);
		if (lockedFitRef == null)
		{
			TryResolveFabricationAnchorReference((Element)(object)fitting, view, fittingPt, FabricationDimensionRefRole.RunStartFitting, chordUnit, out lockedFitRef);
		}
		if (lockedFitRef == null && fitRefs.Count > 0)
		{
			lockedFitRef = fitRefs[0];
		}
		foreach (int sign in new[] { 1, -1 })
		{
			stackIndex = savedStackIdx;
			string pickFail = null;
			if (lockedFitRef != null
				&& TryPickBestFlangeSideReferenceByMaxSpan(doc, dimView, view, pFitP, pFlangeP, perp, vn, chordInPlane, flangeRefs, flangeOnFitSide: false, lockedFitRef, spoolSettings, ref stackIndex, out Reference bestFlangeRef, out Dimension placedDim, out pickFail, sign, lockOffsetSign: false, branchFacingDirection: null, dimensionPolicyRole: "fitting-flange-stub")
				&& TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, pFitP, pFlangeP, perp, vn, chordInPlane, lockedFitRef, bestFlangeRef, spoolSettings, ref stackIndex, out failureDetail, out placedDim, sign, lockOffsetSign: false, branchFacing: null, dimensionPolicyRole: "fitting-flange-stub", witnessRoleA: FabricationDimensionRefRole.RunStartFitting, witnessRoleB: FabricationDimensionRefRole.FlangeFace))
			{
				return true;
			}
			lastFail = pickFail ?? failureDetail;
		}
		foreach (int sign in new[] { 1, -1 })
		{
			stackIndex = savedStackIdx;
			Reference bestFitRef = null;
			Reference bestFlangeRef = null;
			double bestSpan = double.NegativeInfinity;
			foreach (Reference fitRef in fitRefs.Take(8))
			{
				foreach (Reference flangeRef in flangeRefs.Take(8))
				{
					if (refDoc != null && AreSameDimensionReference(refDoc, fitRef, flangeRef))
					{
						continue;
					}
					int stackAttempt = stackIndex;
					if (!TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, pFitP, pFlangeP, perp, vn, chordInPlane, fitRef, flangeRef, spoolSettings, ref stackIndex, out string commitFail, out Dimension placedDim, sign, lockOffsetSign: false, branchFacing: null, dimensionPolicyRole: "fitting-flange-stub", logPlacement: false, witnessRoleA: FabricationDimensionRefRole.RunStartFitting, witnessRoleB: FabricationDimensionRefRole.FlangeFace))
					{
						lastFail = commitFail;
						stackIndex = stackAttempt;
						continue;
					}
					double committedValue;
					try
					{
						committedValue = placedDim?.Value ?? 0.0;
					}
					catch
					{
						committedValue = 0.0;
					}
					if (committedValue > bestSpan)
					{
						bestSpan = committedValue;
						bestFitRef = fitRef;
						bestFlangeRef = flangeRef;
					}
					try
					{
						if (placedDim != null)
						{
							doc.Delete(placedDim.Id);
						}
					}
					catch
					{
					}
					stackIndex = stackAttempt;
				}
			}
			if (bestFitRef != null && bestFlangeRef != null
				&& TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, pFitP, pFlangeP, perp, vn, chordInPlane, bestFitRef, bestFlangeRef, spoolSettings, ref stackIndex, out failureDetail, out _, sign, lockOffsetSign: false, branchFacing: null, dimensionPolicyRole: "fitting-flange-stub", witnessRoleA: FabricationDimensionRefRole.RunStartFitting, witnessRoleB: FabricationDimensionRefRole.FlangeFace))
			{
				return true;
			}
		}
		stackIndex = savedStackIdx;
		failureDetail = lastFail ?? "Stub C-F: all reference pairs failed to commit.";
		foreach (int sign in new[] { 1, -1 })
		{
			stackIndex = savedStackIdx;
			if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, (Element)(object)fitting, fittingPt, (Element)(object)flange, flangePt, spoolSettings, ref stackIndex, out failureDetail, FabricationDimensionRefRole.RunStartFitting, FabricationDimensionRefRole.FlangeFace, sign, lockOffsetSign: false, branchFacingDirection: null, dimensionPolicyRole: "fitting-flange-stub"))
			{
				return true;
			}
		}
		stackIndex = savedStackIdx;
		if (!string.IsNullOrWhiteSpace(lastFail))
		{
			failureDetail = lastFail;
		}
		return false;
	}

	/// <summary>Flange (F) mated directly to a fitting through weld/gasket — elbow-to-flange with no pipe between.</summary>
	private static bool TryResolveDirectFlangeOnFitting(
		FabricationPart fitting,
		IList<FabricationPart> parts,
		out FabricationPart flange,
		out XYZ fittingPt,
		out XYZ flangePt)
	{
		return TryResolveFlangeAdjacentToFitting(fitting, parts, out flange, out fittingPt, out flangePt);
	}

	/// <summary>Walk from a fitting's branch connector to the first terminal flange (L-spool stub leg).</summary>
	private static bool TryWalkFromPartToTerminalFlange(
		FabricationPart start,
		FabricationPart cameFrom,
		FabricationPart runStartFitting,
		IList<FabricationPart> parts,
		out FabricationPart flange,
		out FabricationPart stubPipe,
		out XYZ flangePt)
	{
		flange = null;
		stubPipe = null;
		flangePt = null;
		if (start == null || parts == null)
		{
			return false;
		}
		Document doc = ((Element)start).Document;
		Queue<(FabricationPart part, FabricationPart from, FabricationPart firstPipe)> queue = new Queue<(FabricationPart, FabricationPart, FabricationPart)>();
		queue.Enqueue((start, cameFrom, IsPipeRunPart(start) ? start : null));
		HashSet<long> visited = new HashSet<long>();
		for (int guard = 0; guard < 32 && queue.Count > 0; guard++)
		{
			(FabricationPart current, FabricationPart from, FabricationPart firstPipe) = queue.Dequeue();
			if (current == null || !visited.Add(((Element)current).Id.Value))
			{
				continue;
			}
			if (runStartFitting != null && ((Element)current).Id == ((Element)runStartFitting).Id)
			{
				continue;
			}
			if (FabricationPartClassification.IsFlangePart(current, doc))
			{
				stubPipe = firstPipe;
				if (TryResolveFlangeFaceAnchorPoint(current, stubPipe, parts, out flangePt))
				{
					flange = current;
					return true;
				}
				continue;
			}
			foreach (Connector connector in ListConnectors(current))
			{
				if (connector?.Origin == null)
				{
					continue;
				}
				FabricationPart mate = FindMateAtConnector(current, connector, parts);
				if (mate == null || (from != null && ((Element)mate).Id == ((Element)from).Id))
				{
					continue;
				}
				if (IsGasketPart(mate) || IsWeldPart(mate))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(mate, current, parts);
					if (beyond != null)
					{
						FabricationPart nextPipe = firstPipe ?? (IsPipeRunPart(beyond) ? beyond : null);
						queue.Enqueue((beyond, current, nextPipe));
					}
					continue;
				}
				if (IsValvePart(mate))
				{
					continue;
				}
				FabricationPart pipeHint = firstPipe ?? (IsPipeRunPart(mate) ? mate : null);
				queue.Enqueue((mate, current, pipeHint));
			}
		}
		return false;
	}

	private static FabricationPart ResolveMateThroughJoint(FabricationPart pipe, Connector connector, IList<FabricationPart> parts)
	{
		FabricationPart mate = FindMateAtConnector(pipe, connector, parts);
		if (mate == null)
		{
			return null;
		}
		if (IsGasketPart(mate) || IsWeldPart(mate))
		{
			return FindFarSideMateThroughJoint(mate, pipe, parts) ?? mate;
		}
		return mate;
	}

	/// <summary>One pipe leg with a fitting (C) on one end and a flange (F) on the other — e.g. top elbow to flange stub on an L-spool.</summary>
	private static bool TryResolveFittingToFlangeOnPipeEnds(
		FabricationPart pipe,
		IList<FabricationPart> parts,
		out FabricationPart fitting,
		out XYZ fittingPt,
		out FabricationPart flange,
		out XYZ flangePt)
	{
		fitting = null;
		fittingPt = null;
		flange = null;
		flangePt = null;
		if (pipe == null || parts == null)
		{
			return false;
		}
		List<Connector> connectors = ListConnectors(pipe);
		if (connectors.Count < 2)
		{
			return false;
		}
		FabricationPart fitPart = null;
		FabricationPart flangePart = null;
		foreach (Connector connector in connectors)
		{
			if (connector?.Origin == null)
			{
				continue;
			}
			FabricationPart fitCandidate = ResolveFittingAtPipeConnector(pipe, connector, parts);
			if (fitCandidate != null)
			{
				fitPart = fitCandidate;
			}
			FabricationPart flangeCandidate = ResolveFlangeAtPipeConnector(pipe, connector, parts);
			if (flangeCandidate != null)
			{
				flangePart = flangeCandidate;
			}
		}
		if (fitPart == null || flangePart == null || ((Element)fitPart).Id == ((Element)flangePart).Id)
		{
			return false;
		}
		fittingPt = GetFabricationFittingDimensionAnchor(fitPart, pipe, null, parts);
		if (fittingPt == null || !TryResolveFlangeFaceAnchorPoint(flangePart, pipe, parts, out flangePt) || flangePt == null)
		{
			return false;
		}
		fitting = fitPart;
		flange = flangePart;
		return fittingPt.DistanceTo(flangePt) >= 1.0 / 24.0;
	}

	/// <summary>
	/// Places C→F on every fitting-to-flange stub leg (top elbow → flange on L-spool, etc.).
	/// Uses branch graph walk from each non-run-start fitting — not the main horizontal run chain.
	/// </summary>
	private static int TryCreateSpoolFittingToFlangeStubDimensions(
		Document doc,
		View view,
		IList<FabricationPart> parts,
		XYZ primaryUnitAxis,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		List<string> failureNotes)
	{
		if (doc == null || view == null || parts == null)
		{
			return 0;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			return 0;
		}
		vn = vn.Normalize();
		List<FabricationPart> partList = parts as List<FabricationPart> ?? parts.ToList();
		FabricationPart dominantPipe = GetDominantCollinearRunPipePart(partList, primaryUnitAxis, vn);
		FabricationPart runStartFitting = null;
		if (dominantPipe != null)
		{
			TryResolvePipeRunStartFittingAnchor(partList, dominantPipe, null, primaryUnitAxis, vn, out runStartFitting, out _);
		}
		double dominantLen = dominantPipe != null ? GetFabricationStraightLineLength(dominantPipe) : 0.0;
		int placed = 0;
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

		// First: zero-hop elbow/tee → flange pairs (universal C-F when fittings are together).
		foreach (FabricationPart fitting in partList)
		{
			if (fitting == null || IsOletPart(fitting) || IsGasketPart(fitting) || IsWeldPart(fitting) || IsValvePart(fitting) || !IsFittingLikeForSpoolDim(fitting))
			{
				continue;
			}
			if (runStartFitting != null && ((Element)fitting).Id == ((Element)runStartFitting).Id)
			{
				continue;
			}
			if (FabricationPartClassification.IsFlangePart(fitting, ((Element)fitting).Document))
			{
				continue;
			}
			if (!TryResolveFlangeAdjacentToFitting(fitting, partList, out FabricationPart directFlange, out XYZ directFitPt, out XYZ directFlangePt))
			{
				continue;
			}
			string directKey = ((Element)fitting).Id.Value + "->" + ((Element)directFlange).Id.Value;
			if (!seen.Add(directKey))
			{
				continue;
			}
			TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, "fitting-flange-stub-direct " + FormatElementAnchor((Element)(object)fitting, directFitPt) + " to " + FormatElementAnchor((Element)(object)directFlange, directFlangePt), 0, 0);
			if (TryPlaceFittingFlangeStubDimension(doc, view, fitting, directFitPt, directFlange, directFlangePt, spoolSettings, ref stackIndex, out string directFail))
			{
				placed++;
			}
			else if (!string.IsNullOrWhiteSpace(directFail))
			{
				failureNotes?.Add("Fitting-to-flange stub: " + directFail);
			}
		}

		// Primary: flange-centric — every branch-terminal flange → nearest elbow/tee (top elbow on L-spool).
		foreach (FabricationPart flangePart in partList)
		{
			if (flangePart == null || !FabricationPartClassification.IsFlangePart(flangePart, ((Element)flangePart).Document))
			{
				continue;
			}
			if (!IsBranchTerminalFlange(flangePart, partList, dominantPipe, primaryUnitAxis, vn, dominantLen))
			{
				continue;
			}
			if (!TryResolveElbowForBranchTerminalFlange(flangePart, partList, runStartFitting, dominantPipe, primaryUnitAxis, vn, out FabricationPart fitting, out _, out XYZ fittingPt, out XYZ flangePt))
			{
				continue;
			}
			string flangeKey = ((Element)fitting).Id.Value + "->" + ((Element)flangePart).Id.Value;
			if (!seen.Add(flangeKey))
			{
				continue;
			}
			TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, "fitting-flange-stub-flange-centric " + FormatElementAnchor((Element)(object)fitting, fittingPt) + " to " + FormatElementAnchor((Element)(object)flangePart, flangePt), 0, 0);
			if (TryPlaceFittingFlangeStubDimension(doc, view, fitting, fittingPt, flangePart, flangePt, spoolSettings, ref stackIndex, out string flangeFail))
			{
				placed++;
			}
			else if (!string.IsNullOrWhiteSpace(flangeFail))
			{
				failureNotes?.Add("Fitting-to-flange stub: " + flangeFail);
			}
		}

		foreach (FabricationPart fitting in partList)
		{
			if (fitting == null || IsOletPart(fitting) || IsGasketPart(fitting) || IsWeldPart(fitting) || IsValvePart(fitting) || !IsFittingLikeForSpoolDim(fitting))
			{
				continue;
			}
			if (runStartFitting != null && ((Element)fitting).Id == ((Element)runStartFitting).Id)
			{
				continue;
			}
			if (FabricationPartClassification.IsFlangePart(fitting, ((Element)fitting).Document))
			{
				continue;
			}
			if (!TryFindAdjacentTerminalFlangeFromFitting(fitting, partList, runStartFitting, dominantPipe, primaryUnitAxis, vn, out FabricationPart flange, out _, out XYZ fittingPt, out XYZ flangePt))
			{
				continue;
			}
			string key = ((Element)fitting).Id.Value + "->" + ((Element)flange).Id.Value;
			if (!seen.Add(key))
			{
				continue;
			}
			TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, "fitting-flange-stub " + FormatElementAnchor((Element)(object)fitting, fittingPt) + " to " + FormatElementAnchor((Element)(object)flange, flangePt), 0, 0);
			if (TryPlaceFittingFlangeStubDimension(doc, view, fitting, fittingPt, flange, flangePt, spoolSettings, ref stackIndex, out string failureDetail))
			{
				placed++;
			}
			else if (!string.IsNullOrWhiteSpace(failureDetail))
			{
				failureNotes?.Add("Fitting-to-flange stub: " + failureDetail);
			}
		}

		// Pipe-ended legs: fitting C on one end, flange F on the other (short stub pipe between).
		foreach (FabricationPart pipe in partList)
		{
			if (pipe == null || !IsPipeRunPart(pipe) || IsOletBranchTakeoffPipe(pipe, partList))
			{
				continue;
			}
			if (IsVerticalDropLeg(pipe, partList, primaryUnitAxis, vn))
			{
				continue;
			}
			if (dominantPipe != null && ((Element)pipe).Id == ((Element)dominantPipe).Id)
			{
				continue;
			}
			double pipeLen = GetFabricationStraightLineLength(pipe);
			if (dominantLen > 1.0 / 24.0 && pipeLen > dominantLen * 0.5 && IsCollinearWithPrimaryRun(pipe, partList, primaryUnitAxis, vn))
			{
				continue;
			}
			if (!TryResolveFittingToFlangeOnPipeEnds(pipe, partList, out FabricationPart fitting2, out XYZ fittingPt2, out FabricationPart flange2, out XYZ flangePt2))
			{
				continue;
			}
			if (runStartFitting != null && ((Element)fitting2).Id == ((Element)runStartFitting).Id)
			{
				continue;
			}
			string pipeKey = ((Element)fitting2).Id.Value + "->" + ((Element)flange2).Id.Value;
			if (!seen.Add(pipeKey))
			{
				continue;
			}
			TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, "fitting-flange-stub-pipe " + FormatElementAnchor((Element)(object)fitting2, fittingPt2) + " to " + FormatElementAnchor((Element)(object)flange2, flangePt2), 0, 0);
			if (TryPlaceFittingFlangeStubDimension(doc, view, fitting2, fittingPt2, flange2, flangePt2, spoolSettings, ref stackIndex, out string pipeFailure))
			{
				placed++;
			}
			else if (!string.IsNullOrWhiteSpace(pipeFailure))
			{
				failureNotes?.Add("Fitting-to-flange stub: " + pipeFailure);
			}
		}

		return placed;
	}

	private static bool IsFittingCenterPairOffsetLeg(XYZ ptA, XYZ ptB, XYZ primaryAxisInPlane, XYZ vn)
	{
		if (ptA == null || ptB == null || primaryAxisInPlane == null || vn == null)
		{
			return false;
		}
		XYZ chord = ptB - ptA;
		XYZ chordInPlane = chord - vn * chord.DotProduct(vn);
		if (chordInPlane == null || chordInPlane.GetLength() < 1.0 / 24.0)
		{
			return false;
		}
		return Math.Abs(chordInPlane.Normalize().DotProduct(primaryAxisInPlane)) < 0.75;
	}

	/// <summary>Offset C-C legs that read as vertical drop in this view — not branch horizontal spans reserved for top/plan views.</summary>
	private static bool IsVerticalDropStyleOffsetLeg(XYZ ptA, XYZ ptB, XYZ primaryAxisInPlane, XYZ upInPlane, XYZ vn)
	{
		if (!IsFittingCenterPairOffsetLeg(ptA, ptB, primaryAxisInPlane, vn))
		{
			return false;
		}
		if (upInPlane == null || upInPlane.GetLength() < 1E-09)
		{
			return false;
		}
		XYZ chord = ptB - ptA;
		double verticalMag = Math.Abs(chord.DotProduct(upInPlane));
		if (verticalMag < 1.0 / 12.0)
		{
			return false;
		}
		XYZ chordInPlane = chord - vn * chord.DotProduct(vn);
		double inPlaneMag = chordInPlane?.GetLength() ?? 0.0;
		return verticalMag >= inPlaneMag * 0.35;
	}

	private static bool TryResolvePipeLegAxisInView(View view, FabricationPart pipe, XYZ vn, out XYZ legAxis)
	{
		legAxis = null;
		if (pipe == null || vn == null)
		{
			return false;
		}
		if (TryGetFabricationRunAxisInViewPlane(view, pipe, out legAxis))
		{
			return true;
		}
		if (!TryGetFabricationLineDirection(pipe, out XYZ rawDir))
		{
			return false;
		}
		legAxis = ProjectVectorToViewPlane(rawDir, vn);
		if (legAxis == null || legAxis.GetLength() < 1E-09)
		{
			return false;
		}
		legAxis = legAxis.Normalize();
		return true;
	}

	private static bool TryResolveOtherFittingOnPipeLeg(
		FabricationPart pipe,
		FabricationPart knownFitting,
		IList<FabricationPart> parts,
		XYZ legAxis,
		XYZ vn,
		out FabricationPart otherFitting,
		out XYZ knownPt,
		out XYZ otherPt)
	{
		otherFitting = null;
		knownPt = null;
		otherPt = null;
		if (pipe == null || knownFitting == null || parts == null)
		{
			return false;
		}
		if (legAxis == null || legAxis.GetLength() < 1E-09)
		{
			if (!TryGetFabricationLineDirection(pipe, out XYZ rawDir) || rawDir == null)
			{
				List<XYZ> ends = new List<XYZ>();
				foreach (Connector connector in ListConnectors(pipe))
				{
					if (connector?.Origin != null)
					{
						ends.Add(connector.Origin);
					}
				}
				if (ends.Count < 2)
				{
					return false;
				}
				legAxis = (ends[1] - ends[0]).Normalize();
			}
			else
			{
				legAxis = rawDir.Normalize();
			}
		}
		if (!TryResolveFittingsAtPipeEnds(pipe, parts, legAxis, vn, out FabricationPart lowFitting, out XYZ lowPt, out FabricationPart highFitting, out XYZ highPt))
		{
			return false;
		}
		if (((Element)lowFitting).Id == ((Element)knownFitting).Id)
		{
			otherFitting = highFitting;
			knownPt = lowPt;
			otherPt = highPt;
		}
		else if (((Element)highFitting).Id == ((Element)knownFitting).Id)
		{
			otherFitting = lowFitting;
			knownPt = highPt;
			otherPt = lowPt;
		}
		else
		{
			return false;
		}
		return otherFitting != null && knownPt != null && otherPt != null;
	}

	private static bool TryPlaceSpoolFittingCenterPairDimension(
		Document doc,
		View view,
		FabricationPart partA,
		XYZ ptA,
		FabricationPart partB,
		XYZ ptB,
		FabricationPart legPipe,
		FabricationPart dominantPipe,
		IList<FabricationPart> parts,
		XYZ primaryAxisInPlane,
		XYZ upInPlane,
		XYZ vn,
		SpoolingManagerSettings spoolSettings,
		ref int stackIndex,
		HashSet<string> seen,
		List<string> placementFails,
		string logTag)
	{
		if (partA == null || partB == null || ptA == null || ptB == null)
		{
			return false;
		}
		if (((Element)partA).Id == ((Element)partB).Id)
		{
			return false;
		}
		if (ptA.DistanceTo(ptB) < 1.0 / 24.0)
		{
			return false;
		}
		if (!ShouldPlaceFittingPipeFittingCenterPair(partA, partB, ptA, ptB, legPipe, dominantPipe, parts, primaryAxisInPlane, upInPlane, vn))
		{
			return false;
		}
		long idLo = Math.Min(((Element)partA).Id.Value, ((Element)partB).Id.Value);
		long idHi = Math.Max(((Element)partA).Id.Value, ((Element)partB).Id.Value);
		string pairKey = idLo + "->" + idHi;
		if (seen.Contains(pairKey))
		{
			return false;
		}
		FabricationPart topPart = partA;
		XYZ topPt = ptA;
		FabricationPart bottomPart = partB;
		XYZ bottomPt = ptB;
		if (DotInPlane(bottomPt, upInPlane, vn) > DotInPlane(topPt, upInPlane, vn))
		{
			topPart = partB;
			topPt = ptB;
			bottomPart = partA;
			bottomPt = ptA;
		}
		TryAppendAutoDimDiagnosticLog("CHW-16-anchor", view.Name, logTag + " C-C " + FormatElementAnchor((Element)(object)topPart, topPt) + " to " + FormatElementAnchor((Element)(object)bottomPart, bottomPt), 0, 0);
		foreach (int sign in new[] { -1, 1 })
		{
			int savedStack = stackIndex;
			if (TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, (Element)(object)topPart, topPt, (Element)(object)bottomPart, bottomPt, spoolSettings, ref stackIndex, out string failureDetail, FabricationDimensionRefRole.RunStartFitting, FabricationDimensionRefRole.RunStartFitting, sign))
			{
				seen.Add(pairKey);
				return true;
			}
			stackIndex = savedStack;
			if (!string.IsNullOrWhiteSpace(failureDetail))
			{
				placementFails?.Add(failureDetail);
			}
		}
		return false;
	}

	private static int TryCreateSpoolVerticalDropToElbowDimension(Document doc, View view, List<FabricationPart> parts, XYZ primaryUnitAxis, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string diagnostic)
	{
		diagnostic = null;
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (fitting C-C): view direction is invalid.";
			return 0;
		}
		vn = vn.Normalize();
		XYZ primaryAxisInPlane = ProjectVectorToViewPlane(primaryUnitAxis, vn);
		if (primaryAxisInPlane == null || primaryAxisInPlane.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (fitting C-C): primary run axis is invalid in this view.";
			return 0;
		}
		primaryAxisInPlane = primaryAxisInPlane.Normalize();
		XYZ upInPlane = ProjectVectorToViewPlane(view.UpDirection, vn);
		if (upInPlane == null || upInPlane.GetLength() < 1E-09)
		{
			diagnostic = "Auto-dimension (fitting C-C): view up direction is invalid in this view.";
			return 0;
		}
		upInPlane = upInPlane.Normalize();
		int placed = 0;
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		List<string> placementFails = new List<string>();
		Document assemblyDoc = parts.Count > 0 ? ((Element)parts[0]).Document : doc;
		FabricationPart dominantPipe = GetDominantCollinearRunPipePart(parts, primaryUnitAxis, vn);

		// Fitting-centric first: direct elbow→elbow (Z-offset with no pipe) and elbow→pipe→elbow.
		foreach (FabricationPart fitting in parts)
		{
			if (fitting == null || IsOletPart(fitting) || IsGasketPart(fitting) || IsWeldPart(fitting) || IsValvePart(fitting) || !IsFittingLikeForSpoolDim(fitting))
			{
				continue;
			}
			if (FabricationPartClassification.IsFlangePart(fitting, assemblyDoc))
			{
				continue;
			}
			foreach (FabricationPart mate in EnumerateMatedFabricationParts(fitting, parts))
			{
				if (IsFittingLikeForSpoolDim(mate) && !IsOletPart(mate) && !IsValvePart(mate)
					&& !FabricationPartClassification.IsFlangePart(mate, assemblyDoc)
					&& ((Element)mate).Id != ((Element)fitting).Id)
				{
					XYZ ptA = GetFabricationFittingDimensionAnchor(fitting, null, null, parts);
					XYZ ptB = GetFabricationFittingDimensionAnchor(mate, null, null, parts);
					if (TryPlaceSpoolFittingCenterPairDimension(doc, view, fitting, ptA, mate, ptB, null, dominantPipe, parts, primaryAxisInPlane, upInPlane, vn, spoolSettings, ref stackIndex, seen, placementFails, "fitting-direct"))
					{
						placed++;
					}
					continue;
				}
				FabricationPart legPipe = null;
				FabricationPart otherFitting = null;
				if (IsPipeRunPart(mate) && !IsOletBranchTakeoffPipe(mate, parts))
				{
					legPipe = mate;
				}
				else if (IsGasketPart(mate) || IsWeldPart(mate))
				{
					FabricationPart beyond = FindFarSideMateThroughJoint(mate, fitting, parts);
					if (beyond != null && IsPipeRunPart(beyond) && !IsOletBranchTakeoffPipe(beyond, parts))
					{
						legPipe = beyond;
					}
					else if (beyond != null && IsFittingLikeForSpoolDim(beyond) && !IsOletPart(beyond) && !IsValvePart(beyond)
						&& !FabricationPartClassification.IsFlangePart(beyond, assemblyDoc)
						&& ((Element)beyond).Id != ((Element)fitting).Id)
					{
						otherFitting = beyond;
					}
				}
				if (legPipe != null)
				{
					TryResolvePipeLegAxisInView(view, legPipe, vn, out XYZ legAxis);
					if (TryResolveOtherFittingOnPipeLeg(legPipe, fitting, parts, legAxis, vn, out FabricationPart otherOnPipe, out XYZ knownPt, out XYZ otherPt)
						&& TryPlaceSpoolFittingCenterPairDimension(doc, view, fitting, knownPt, otherOnPipe, otherPt, legPipe, dominantPipe, parts, primaryAxisInPlane, upInPlane, vn, spoolSettings, ref stackIndex, seen, placementFails, "fitting-pipe-leg"))
					{
						placed++;
					}
				}
				else if (otherFitting != null)
				{
					XYZ ptA = GetFabricationFittingDimensionAnchor(fitting, null, null, parts);
					XYZ ptB = GetFabricationFittingDimensionAnchor(otherFitting, null, null, parts);
					if (TryPlaceSpoolFittingCenterPairDimension(doc, view, fitting, ptA, otherFitting, ptB, null, dominantPipe, parts, primaryAxisInPlane, upInPlane, vn, spoolSettings, ref stackIndex, seen, placementFails, "fitting-joint"))
					{
						placed++;
					}
				}
			}
		}

		// Pipe scan: fitting-pipe-fitting on offset legs and short series pipes (elbow→pipe→elbow on branch run).
		foreach (FabricationPart dropPipe in from p in parts
			where IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts)
			orderby GetFabricationStraightLineLength(p)
			select p)
		{
			if (!TryResolvePipeLegAxisInView(view, dropPipe, vn, out XYZ dropAxis))
			{
				continue;
			}
			bool collinearWithMain = Math.Abs(dropAxis.DotProduct(primaryAxisInPlane)) > 0.9;
			if (collinearWithMain && !IsShortNonDominantRunPipe(dropPipe, dominantPipe))
			{
				continue;
			}
			if (!TryResolveFittingsAtPipeEnds(dropPipe, parts, dropAxis, vn, out FabricationPart lowFitting, out XYZ lowPt, out FabricationPart highFitting, out XYZ highPt))
			{
				continue;
			}
			if (TryPlaceSpoolFittingCenterPairDimension(doc, view, lowFitting, lowPt, highFitting, highPt, dropPipe, dominantPipe, parts, primaryAxisInPlane, upInPlane, vn, spoolSettings, ref stackIndex, seen, placementFails, "fitting-pipe-fitting"))
			{
				placed++;
			}
		}

		if (placed == 0)
		{
			diagnostic = placementFails.Count > 0
				? "Auto-dimension (fitting C-C): " + placementFails[0]
				: "Auto-dimension (fitting C-C): no qualifying fitting-pipe-fitting leg was found.";
		}
		return placed;
	}

	private static bool TryGetFabricationRunAxisInViewPlane(View view, FabricationPart part, out XYZ unitAxis)
	{
		unitAxis = null;
		if (view == null || part == null)
		{
			return false;
		}
		XYZ vn = view.ViewDirection;
		if (vn == null || vn.GetLength() < 1E-09)
		{
			return false;
		}
		vn = vn.Normalize();
		Location obj = ((Element)part).Location;
		LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
		if (val == null)
		{
			return false;
		}
		Curve curve = val.Curve;
		Line val2 = (Line)(object)((curve is Line) ? curve : null);
		if (val2 == null)
		{
			return false;
		}
		XYZ direction = val2.Direction;
		if (direction == null || direction.GetLength() < 1E-09)
		{
			return false;
		}
		direction = direction.Normalize();
		direction -= vn * direction.DotProduct(vn);
		if (direction.GetLength() < 1E-09)
		{
			return false;
		}
		unitAxis = direction.Normalize();
		return true;
	}

	private static FabricationPart GetDominantStraightFabricationPart(List<FabricationPart> parts)
	{
		FabricationPart val = parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts)).OrderByDescending(GetFabricationStraightLineLength).FirstOrDefault();
		if (val != null)
		{
			return val;
		}
		return parts.Where((FabricationPart p) => !IsGasketPart(p) && !IsWeldPart(p)).OrderByDescending(GetFabricationStraightLineLength).FirstOrDefault((FabricationPart p) => GetFabricationStraightLineLength(p) > 1.0 / 24.0);
	}

	// Longest pipe collinear with the view run axis. Olet-split L-shapes must not pick the vertical drop
	// leg over the ~20' horizontal span (CHW-16 Front stacked pick-ups + run overall).
	private static FabricationPart GetDominantCollinearRunPipePart(List<FabricationPart> parts, XYZ unitAxis, XYZ vn)
	{
		if (parts == null || unitAxis == null || vn == null)
		{
			return null;
		}
		FabricationPart collinear = parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && !IsVerticalDropLeg(p, parts, unitAxis, vn) && IsCollinearWithPrimaryRun(p, parts, unitAxis, vn)).OrderByDescending(GetFabricationStraightLineLength).FirstOrDefault();
		return collinear ?? GetDominantStraightFabricationPart(parts);
	}

	private static double GetFabricationStraightLineLength(FabricationPart p)
	{
		Location obj = ((p != null) ? ((Element)p).Location : null);
		LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
		if (val != null)
		{
			Curve curve = val.Curve;
			Line val2 = (Line)(object)((curve is Line) ? curve : null);
			if (val2 != null)
			{
				try
				{
					return ((Curve)val2).Length;
				}
				catch
				{
					return 0.0;
				}
			}
		}
		return 0.0;
	}

	private static bool TryGetFabricationLineDirection(FabricationPart part, out XYZ direction)
	{
		direction = null;
		Location obj = ((part != null) ? ((Element)part).Location : null);
		LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
		if (val == null)
		{
			return false;
		}
		Curve curve = val.Curve;
		Line val2 = (Line)(object)((curve is Line) ? curve : null);
		if (val2 == null)
		{
			return false;
		}
		direction = val2.Direction;
		return direction != null && direction.GetLength() > 1E-09;
	}

	// Used before unitAxis exists. Drop legs must not outvote the collinear main run in axis buckets (CHW-16 Front).
	private static bool IsLikelyVerticalDropLegForRunAxisDetection(FabricationPart pipePart, IList<FabricationPart> parts, View view)
	{
		if (!IsPipeRunPart(pipePart) || IsOletBranchTakeoffPipe(pipePart, parts))
		{
			return false;
		}
		if (GetFabricationStraightLineLength(pipePart) < 2.0)
		{
			return false;
		}
		if (!TryGetFabricationLineDirection(pipePart, out XYZ dir))
		{
			return false;
		}
		dir = dir.Normalize();
		// Vertical-only spools have no competing horizontal run — keep the vertical leg in axis buckets.
		double thisLen = GetFabricationStraightLineLength(pipePart);
		bool hasCompetingHorizontalRun = parts.Any((FabricationPart p) =>
		{
			if (p == null || ((Element)p).Id == ((Element)pipePart).Id || !IsPipeRunPart(p) || IsOletBranchTakeoffPipe(p, parts))
			{
				return false;
			}
			if (GetFabricationStraightLineLength(p) < Math.Max(thisLen * 0.35, 2.0))
			{
				return false;
			}
			if (!TryGetFabricationLineDirection(p, out XYZ otherDir))
			{
				return false;
			}
			otherDir = otherDir.Normalize();
			XYZ up = view?.UpDirection;
			if (up == null || up.GetLength() < 1E-09)
			{
				return Math.Abs(otherDir.Z) < 0.5;
			}
			return Math.Abs(otherDir.DotProduct(up.Normalize())) < 0.5;
		});
		if (!hasCompetingHorizontalRun)
		{
			return false;
		}
		bool hasNonOletFittingMate = false;
		foreach (Connector connector in ListConnectors(pipePart))
		{
			FabricationPart mate = FindMateAtConnector(pipePart, connector, parts);
			if (mate != null && IsFittingLikeForSpoolDim(mate) && !IsOletPart(mate))
			{
				hasNonOletFittingMate = true;
				break;
			}
		}
		if (!hasNonOletFittingMate)
		{
			return false;
		}
		XYZ upDir = view?.UpDirection;
		if (upDir != null && upDir.GetLength() > 1E-09 && Math.Abs(dir.DotProduct(upDir.Normalize())) > 0.85)
		{
			return true;
		}
		return Math.Abs(dir.Z) > 0.85;
	}

	private static bool TryGetRunAxisInViewPlane(View view, List<FabricationPart> parts, out XYZ unitAxis)
	{
		unitAxis = null;
		XYZ val = ((view != null) ? view.ViewDirection : null);
		if (val == null || val.GetLength() < 1E-09)
		{
			return false;
		}
		val = val.Normalize();
		// Sum length by in-plane direction. Olet-split runs are several short collinear segments; picking
		// only the longest single piece lets a vertical drop (~8') beat a ~20' horizontal span and disables
		// the whole olet horizontal stack (E-C pick-ups + run overall) on Front View (CHW-16). Host-run
		// pipes with side olets must stay in the buckets; only branch stubs and drop legs are excluded.
		Dictionary<string, (XYZ direction, double totalLength)> axisBuckets = new Dictionary<string, (XYZ, double)>(StringComparer.Ordinal);
		foreach (FabricationPart item in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && !IsLikelyVerticalDropLegForRunAxisDetection(p, parts, view)))
		{
			Location obj = ((item != null) ? ((Element)item).Location : null);
			LocationCurve val3 = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
			if (val3 == null)
			{
				continue;
			}
			Curve curve = val3.Curve;
			Line val4 = (Line)(object)((curve is Line) ? curve : null);
			if (val4 == null)
			{
				continue;
			}
			XYZ direction = val4.Direction;
			if (direction == null || direction.GetLength() < 1E-09)
			{
				continue;
			}
			direction = direction.Normalize();
			direction -= val * direction.DotProduct(val);
			if (direction.GetLength() < 1E-09)
			{
				continue;
			}
			direction = direction.Normalize();
			double segmentLength;
			try
			{
				segmentLength = ((Curve)val4).Length;
			}
			catch
			{
				segmentLength = 0.0;
			}
			if (segmentLength < 1E-09)
			{
				continue;
			}
			string key = CanonicalInPlaneAxisKey(direction);
			if (axisBuckets.TryGetValue(key, out (XYZ direction, double totalLength) existing))
			{
				axisBuckets[key] = (existing.direction, existing.totalLength + segmentLength);
			}
			else
			{
				axisBuckets[key] = (direction, segmentLength);
			}
		}
		XYZ val2 = null;
		double num = 0.0;
		foreach ((XYZ direction, double totalLength) bucket in axisBuckets.Values)
		{
			if (bucket.totalLength > num)
			{
				num = bucket.totalLength;
				val2 = bucket.direction;
			}
		}
		if (val2 == null)
		{
			XYZ rightDirection = view.RightDirection;
			if (rightDirection == null || rightDirection.GetLength() < 1E-09)
			{
				return false;
			}
			rightDirection = rightDirection.Normalize();
			rightDirection -= val * rightDirection.DotProduct(val);
			if (rightDirection.GetLength() < 1E-09)
			{
				return false;
			}
			val2 = rightDirection.Normalize();
		}
		unitAxis = val2.Normalize();
		return true;
	}

	private static string CanonicalInPlaneAxisKey(XYZ direction)
	{
		if (direction == null || direction.GetLength() < 1E-09)
		{
			return string.Empty;
		}
		XYZ d = direction.Normalize();
		if (d.X < -1E-09 || (Math.Abs(d.X) <= 1E-09 && d.Y < -1E-09) || (Math.Abs(d.X) <= 1E-09 && Math.Abs(d.Y) <= 1E-09 && d.Z < -1E-09))
		{
			d = d.Negate();
		}
		return d.X.ToString("F4") + "|" + d.Y.ToString("F4") + "|" + d.Z.ToString("F4");
	}

	private static double ScalarAlong(FabricationPart part, XYZ unitAxis, XYZ viewNormal)
	{
		XYZ fabricationCenterPoint = GetFabricationCenterPoint(part);
		if (fabricationCenterPoint == null)
		{
			return double.MaxValue;
		}
		return DotInPlane(fabricationCenterPoint, unitAxis, viewNormal);
	}

	private static double DotInPlane(XYZ point, XYZ unitAxis, XYZ viewNormal)
	{
		return (point - viewNormal * point.DotProduct(viewNormal)).DotProduct(unitAxis);
	}

	private static bool TryGetPrimaryPipeEndAnchor(List<FabricationPart> parts, FabricationPart pipeRestrict, XYZ unitAxis, XYZ viewNormal, out FabricationPart endOwner, out XYZ pipeEndPt)
	{
		endOwner = null;
		pipeEndPt = null;
		double num = double.MinValue;
		IEnumerable<FabricationPart> enumerable;
		if (pipeRestrict == null)
		{
			enumerable = parts.Where(IsPipeRunPart);
		}
		else
		{
			IEnumerable<FabricationPart> enumerable2 = (IEnumerable<FabricationPart>)(object)new FabricationPart[1] { pipeRestrict };
			enumerable = enumerable2;
		}
		IEnumerable<FabricationPart> enumerable3 = enumerable;
		foreach (FabricationPart item in enumerable3)
		{
			if (IsOletBranchTakeoffPipe(item, parts))
			{
				continue;
			}
			foreach (Connector item2 in ListConnectors(item))
			{
				if (((item2 != null) ? item2.Origin : null) != null && FindMateAtConnector(item, item2, parts) == null)
				{
					double num2 = DotInPlane(item2.Origin, unitAxis, viewNormal);
					if (num2 > num)
					{
						num = num2;
						endOwner = item;
						pipeEndPt = item2.Origin;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return true;
		}
		foreach (FabricationPart item3 in enumerable3)
		{
			if (IsOletBranchTakeoffPipe(item3, parts))
			{
				continue;
			}
			foreach (Connector item4 in ListConnectors(item3))
			{
				if (((item4 != null) ? item4.Origin : null) != null)
				{
					double num3 = DotInPlane(item4.Origin, unitAxis, viewNormal);
					if (num3 > num)
					{
						num = num3;
						endOwner = item3;
						pipeEndPt = item4.Origin;
					}
				}
			}
		}
		if (pipeEndPt == null && parts.Count > 0)
		{
			foreach (FabricationPart item5 in enumerable3)
			{
				if (IsOletBranchTakeoffPipe(item5, parts))
				{
					continue;
				}
				Location obj = ((item5 != null) ? ((Element)item5).Location : null);
				LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
				if (val == null)
				{
					continue;
				}
				Curve curve = val.Curve;
				Line val2 = (Line)(object)((curve is Line) ? curve : null);
				if (val2 != null)
				{
					XYZ endPoint = ((Curve)val2).GetEndPoint(0);
					XYZ endPoint2 = ((Curve)val2).GetEndPoint(1);
					double num4 = DotInPlane(endPoint, unitAxis, viewNormal);
					double num5 = DotInPlane(endPoint2, unitAxis, viewNormal);
					XYZ val3 = ((num4 >= num5) ? endPoint : endPoint2);
					double num6 = Math.Max(num4, num5);
					if (num6 > num)
					{
						num = num6;
						endOwner = item5;
						pipeEndPt = val3;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return endOwner != null;
		}
		return false;
	}

	private static bool TryGetCollinearRunPipeEndAnchor(List<FabricationPart> parts, XYZ unitAxis, XYZ viewNormal, out FabricationPart endOwner, out XYZ pipeEndPt)
	{
		endOwner = null;
		pipeEndPt = null;
		if (parts == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		double maxScalar = double.MinValue;
		foreach (FabricationPart item in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && IsCollinearWithPrimaryRun(p, parts, unitAxis, viewNormal)))
		{
			foreach (Connector item2 in ListConnectors(item))
			{
				if (((item2 != null) ? item2.Origin : null) != null && FindMateAtConnector(item, item2, parts) == null)
				{
					double scalar = DotInPlane(item2.Origin, unitAxis, viewNormal);
					if (scalar > maxScalar)
					{
						maxScalar = scalar;
						endOwner = item;
						pipeEndPt = item2.Origin;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return endOwner != null;
		}
		foreach (FabricationPart item3 in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && IsCollinearWithPrimaryRun(p, parts, unitAxis, viewNormal)))
		{
			foreach (Connector item4 in ListConnectors(item3))
			{
				if (((item4 != null) ? item4.Origin : null) != null)
				{
					double scalar2 = DotInPlane(item4.Origin, unitAxis, viewNormal);
					if (scalar2 > maxScalar)
					{
						maxScalar = scalar2;
						endOwner = item3;
						pipeEndPt = item4.Origin;
					}
				}
			}
		}
		if (pipeEndPt == null)
		{
			foreach (FabricationPart item5 in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && IsCollinearWithPrimaryRun(p, parts, unitAxis, viewNormal)))
			{
				Location obj = ((item5 != null) ? ((Element)item5).Location : null);
				LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
				if (val == null)
				{
					continue;
				}
				Curve curve = val.Curve;
				Line val2 = (Line)(object)((curve is Line) ? curve : null);
				if (val2 != null)
				{
					XYZ endPoint = ((Curve)val2).GetEndPoint(0);
					XYZ endPoint2 = ((Curve)val2).GetEndPoint(1);
					double num4 = DotInPlane(endPoint, unitAxis, viewNormal);
					double num5 = DotInPlane(endPoint2, unitAxis, viewNormal);
					XYZ val3 = ((num4 >= num5) ? endPoint : endPoint2);
					double num6 = Math.Max(num4, num5);
					if (num6 > maxScalar)
					{
						maxScalar = num6;
						endOwner = item5;
						pipeEndPt = val3;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return endOwner != null;
		}
		return false;
	}

	private static bool TryGetCollinearRunMinimumExtentAnchor(List<FabricationPart> parts, XYZ unitAxis, XYZ viewNormal, out FabricationPart endOwner, out XYZ pipeEndPt)
	{
		endOwner = null;
		pipeEndPt = null;
		if (parts == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		double minScalar = double.MaxValue;
		foreach (FabricationPart item in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && IsCollinearWithPrimaryRun(p, parts, unitAxis, viewNormal)))
		{
			foreach (Connector item2 in ListConnectors(item))
			{
				if (((item2 != null) ? item2.Origin : null) == null)
				{
					continue;
				}
				double scalar = DotInPlane(item2.Origin, unitAxis, viewNormal);
				if (scalar < minScalar)
				{
					minScalar = scalar;
					endOwner = item;
					pipeEndPt = item2.Origin;
				}
			}
		}
		if (pipeEndPt == null)
		{
			foreach (FabricationPart item3 in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && IsCollinearWithPrimaryRun(p, parts, unitAxis, viewNormal)))
			{
				Location obj = ((item3 != null) ? ((Element)item3).Location : null);
				LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
				if (val == null)
				{
					continue;
				}
				Curve curve = val.Curve;
				Line val2 = (Line)(object)((curve is Line) ? curve : null);
				if (val2 != null)
				{
					XYZ endPoint = ((Curve)val2).GetEndPoint(0);
					XYZ endPoint2 = ((Curve)val2).GetEndPoint(1);
					double num4 = DotInPlane(endPoint, unitAxis, viewNormal);
					double num5 = DotInPlane(endPoint2, unitAxis, viewNormal);
					XYZ val3 = ((num4 <= num5) ? endPoint : endPoint2);
					double num6 = Math.Min(num4, num5);
					if (num6 < minScalar)
					{
						minScalar = num6;
						endOwner = item3;
						pipeEndPt = val3;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return endOwner != null;
		}
		return false;
	}

	private static bool TryGetCollinearRunPipeStartAnchor(List<FabricationPart> parts, XYZ unitAxis, XYZ viewNormal, out FabricationPart endOwner, out XYZ pipeEndPt)
	{
		endOwner = null;
		pipeEndPt = null;
		if (parts == null || unitAxis == null || viewNormal == null)
		{
			return false;
		}
		double minScalar = double.MaxValue;
		foreach (FabricationPart item in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && IsCollinearWithPrimaryRun(p, parts, unitAxis, viewNormal)))
		{
			foreach (Connector item2 in ListConnectors(item))
			{
				if (((item2 != null) ? item2.Origin : null) != null && FindMateAtConnector(item, item2, parts) == null)
				{
					double scalar = DotInPlane(item2.Origin, unitAxis, viewNormal);
					if (scalar < minScalar)
					{
						minScalar = scalar;
						endOwner = item;
						pipeEndPt = item2.Origin;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return endOwner != null;
		}
		foreach (FabricationPart item3 in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && IsCollinearWithPrimaryRun(p, parts, unitAxis, viewNormal)))
		{
			foreach (Connector item4 in ListConnectors(item3))
			{
				if (((item4 != null) ? item4.Origin : null) != null)
				{
					double scalar2 = DotInPlane(item4.Origin, unitAxis, viewNormal);
					if (scalar2 < minScalar)
					{
						minScalar = scalar2;
						endOwner = item3;
						pipeEndPt = item4.Origin;
					}
				}
			}
		}
		if (pipeEndPt == null)
		{
			foreach (FabricationPart item5 in parts.Where((FabricationPart p) => IsPipeRunPart(p) && !IsOletBranchTakeoffPipe(p, parts) && IsCollinearWithPrimaryRun(p, parts, unitAxis, viewNormal)))
			{
				Location obj = ((item5 != null) ? ((Element)item5).Location : null);
				LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
				if (val == null)
				{
					continue;
				}
				Curve curve = val.Curve;
				Line val2 = (Line)(object)((curve is Line) ? curve : null);
				if (val2 != null)
				{
					XYZ endPoint = ((Curve)val2).GetEndPoint(0);
					XYZ endPoint2 = ((Curve)val2).GetEndPoint(1);
					double num4 = DotInPlane(endPoint, unitAxis, viewNormal);
					double num5 = DotInPlane(endPoint2, unitAxis, viewNormal);
					XYZ val3 = ((num4 <= num5) ? endPoint : endPoint2);
					double num6 = Math.Min(num4, num5);
					if (num6 < minScalar)
					{
						minScalar = num6;
						endOwner = item5;
						pipeEndPt = val3;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return endOwner != null;
		}
		return false;
	}

	private static bool TryGetMinimumPipeEndAnchor(List<FabricationPart> parts, FabricationPart pipeRestrict, XYZ unitAxis, XYZ viewNormal, out FabricationPart endOwner, out XYZ pipeEndPt)
	{
		endOwner = null;
		pipeEndPt = null;
		double num = double.MaxValue;
		IEnumerable<FabricationPart> enumerable;
		if (pipeRestrict == null)
		{
			enumerable = parts.Where(IsPipeRunPart);
		}
		else
		{
			IEnumerable<FabricationPart> enumerable2 = (IEnumerable<FabricationPart>)(object)new FabricationPart[1] { pipeRestrict };
			enumerable = enumerable2;
		}
		IEnumerable<FabricationPart> enumerable3 = enumerable;
		foreach (FabricationPart item in enumerable3)
		{
			foreach (Connector item2 in ListConnectors(item))
			{
				if (((item2 != null) ? item2.Origin : null) != null && FindMateAtConnector(item, item2, parts) == null)
				{
					double num2 = DotInPlane(item2.Origin, unitAxis, viewNormal);
					if (num2 < num)
					{
						num = num2;
						endOwner = item;
						pipeEndPt = item2.Origin;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return endOwner != null;
		}
		foreach (FabricationPart item3 in enumerable3)
		{
			foreach (Connector item4 in ListConnectors(item3))
			{
				if (((item4 != null) ? item4.Origin : null) != null)
				{
					double num3 = DotInPlane(item4.Origin, unitAxis, viewNormal);
					if (num3 < num)
					{
						num = num3;
						endOwner = item3;
						pipeEndPt = item4.Origin;
					}
				}
			}
		}
		if (pipeEndPt == null && parts.Count > 0)
		{
			foreach (FabricationPart item5 in enumerable3)
			{
				Location obj = ((item5 != null) ? ((Element)item5).Location : null);
				LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
				if (val == null)
				{
					continue;
				}
				Curve curve = val.Curve;
				Line val2 = (Line)(object)((curve is Line) ? curve : null);
				if (val2 != null)
				{
					XYZ endPoint = ((Curve)val2).GetEndPoint(0);
					XYZ endPoint2 = ((Curve)val2).GetEndPoint(1);
					double num4 = DotInPlane(endPoint, unitAxis, viewNormal);
					double num5 = DotInPlane(endPoint2, unitAxis, viewNormal);
					XYZ val3 = ((num4 <= num5) ? endPoint : endPoint2);
					double num6 = Math.Min(num4, num5);
					if (num6 < num)
					{
						num = num6;
						endOwner = item5;
						pipeEndPt = val3;
					}
				}
			}
		}
		if (pipeEndPt != null)
		{
			return endOwner != null;
		}
		return false;
	}

	private static bool IsPipeRunPart(FabricationPart part)
	{
		if (part == null || IsGasketPart(part) || IsWeldPart(part))
		{
			return false;
		}
		if (IsOletPart(part))
		{
			return false;
		}
		if (FabricationPartClassification.IsFlangePart(part, ((Element)part).Document))
		{
			return false;
		}
		if (GetFabricationSortPriority(part) != 0)
		{
			return IsPipeLikePart(part);
		}
		return true;
	}

	private static bool IsFittingLikeForSpoolDim(FabricationPart part)
	{
		if (part == null || IsGasketPart(part) || IsWeldPart(part))
		{
			return false;
		}
		if (IsValvePart(part))
		{
			return false;
		}
		if (IsPipeRunPart(part))
		{
			return false;
		}
		return true;
	}

	/// <summary>
	/// F, E, olet branch, and valves use other snap rules. Everything else dimensions at connector centerline intersection (C).
	/// </summary>
	private static bool ShouldSnapFittingToCenterlineIntersection(FabricationPart part)
	{
		if (part == null || IsGasketPart(part) || IsWeldPart(part) || IsValvePart(part))
		{
			return false;
		}
		Document doc = part.Document;
		if (FabricationPartClassification.IsFlangePart(part, doc))
		{
			return false;
		}
		return !IsPipeRunPart(part);
	}

	private static bool IsOletPart(FabricationPart part)
	{
		return FabricationPartClassification.IsOletPart(part);
	}

	private static bool IsInlineBranchTakeoffFitting(FabricationPart fitting, FabricationPart hostPipe, List<FabricationPart> parts, XYZ unitAxis, XYZ vn)
	{
		if (fitting == null || hostPipe == null || parts == null || parts.Count == 0)
		{
			return false;
		}
		if (!IsFittingLikeForSpoolDim(fitting))
		{
			return false;
		}
		foreach (Connector connector in ListConnectors(fitting))
		{
			if (((connector != null) ? connector.Origin : null) == null)
			{
				continue;
			}
			FabricationPart mate = FindMateAtConnector(fitting, connector, parts);
			if (mate == null || ((Element)mate).Id == ((Element)hostPipe).Id)
			{
				continue;
			}
			if (IsPipeRunPart(mate) && !IsCollinearWithPrimaryRun(mate, parts, unitAxis, vn) && !IsVerticalDropLeg(mate, parts, unitAxis, vn))
			{
				return true;
			}
		}
		return false;
	}

	private static bool ShouldUseOletHorizontalDimension(FabricationPart part, FabricationPart hostPipe, List<FabricationPart> parts, XYZ unitAxis, XYZ vn)
	{
		return IsOletPart(part) && !IsInlineBranchTakeoffFitting(part, hostPipe, parts, unitAxis, vn);
	}

	private static bool AreFabricationPartsDirectlyConnected(FabricationPart a, FabricationPart b)
	{
		if (a == null || b == null)
		{
			return false;
		}
		foreach (Connector connector in ListConnectors(a))
		{
			if (connector == null)
			{
				continue;
			}
			ConnectorSet refs = connector.AllRefs;
			if (refs == null)
			{
				continue;
			}
			foreach (Connector refConnector in refs)
			{
				if (refConnector?.Owner is FabricationPart mate && ((Element)mate).Id == ((Element)b).Id)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static FabricationPart TryFindMateAtConnectorViaSpatialIndex(
		FabricationPart self,
		Connector connector,
		IList<FabricationPart> pool,
		Document doc)
	{
		XYZ origin = connector?.Origin;
		if (origin == null || pool == null || doc == null)
		{
			return null;
		}
		HashSet<long> poolIds = new HashSet<long>();
		foreach (FabricationPart poolPart in pool)
		{
			if (poolPart != null)
			{
				poolIds.Add(((Element)poolPart).Id.Value);
			}
		}
		if (poolIds.Count == 0)
		{
			return null;
		}
		Dictionary<(long, long, long), List<(FabricationPart part, XYZ origin)>> index = GetCachedFabConnectorIndex(doc);
		const double cell = 0.25;
		long cellX = (long)Math.Floor(origin.X / cell);
		long cellY = (long)Math.Floor(origin.Y / cell);
		long cellZ = (long)Math.Floor(origin.Z / cell);
		FabricationPart bestMate = null;
		double bestDistance = FabConnectorMateToleranceFeet;
		for (long dx = -1; dx <= 1; dx++)
		{
			for (long dy = -1; dy <= 1; dy++)
			{
				for (long dz = -1; dz <= 1; dz++)
				{
					if (!index.TryGetValue((cellX + dx, cellY + dy, cellZ + dz), out List<(FabricationPart part, XYZ origin)> bucket))
					{
						continue;
					}
					foreach ((FabricationPart part, XYZ mateOrigin) entry in bucket)
					{
						FabricationPart candidate = entry.part;
						if (candidate == null || ((Element)candidate).Id == ((Element)self).Id)
						{
							continue;
						}
						if (!poolIds.Contains(((Element)candidate).Id.Value))
						{
							continue;
						}
						double distance = origin.DistanceTo(entry.mateOrigin);
						if (distance < bestDistance)
						{
							bestDistance = distance;
							bestMate = candidate;
						}
					}
				}
			}
		}
		return bestMate;
	}

	private static FabricationPart FindMateAtConnector(FabricationPart self, Connector connector, IList<FabricationPart> pool)
	{
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Expected O, but got Unknown
		if (self == null || ((connector != null) ? connector.Origin : null) == null)
		{
			return null;
		}
		Document doc = ((Element)self).Document;
		if (doc != null && pool != null && pool.Count > 12)
		{
			FabricationPart indexedMate = TryFindMateAtConnectorViaSpatialIndex(self, connector, pool, doc);
			if (indexedMate != null)
			{
				return indexedMate;
			}
		}
		XYZ origin = connector.Origin;
		foreach (FabricationPart item in pool)
		{
			if (item == null || ((Element)item).Id == ((Element)self).Id)
			{
				continue;
			}
			ConnectorManager connectorManager = item.ConnectorManager;
			if (connectorManager == null)
			{
				continue;
			}
			foreach (Connector connector2 in connectorManager.Connectors)
			{
				Connector val = connector2;
				if (((val != null) ? val.Origin : null) != null && origin.DistanceTo(val.Origin) < 0.08)
				{
					return item;
				}
			}
		}
		return null;
	}

	/// <summary>
	/// Universal dimension origin (world point) for a fabrication fitting.
	/// Rules never vary by spool — only by fitting kind:
	/// Elbow/Tee/Olet C = connector-ray intersection, else mated-pipe-axis intersection; never connector origin or instance origin.
	/// Flange F = raised face at run; never raw instance origin.
	/// Pipe open end E is resolved elsewhere (TryWalkPipeRunToOpenEnd / open connector), not here.
	/// </summary>
	private static XYZ GetFabricationFittingDimensionAnchor(FabricationPart fitting, FabricationPart primaryStraightRun, FabricationPart pipeEndOwner, IList<FabricationPart> partsPool)
	{
		if (fitting == null || IsValvePart(fitting))
		{
			return null;
		}

		Document doc = ((Element)fitting).Document;
		SpoolFittingKind kind = ClassifyFabricationPart(fitting, doc);
		if (kind == SpoolFittingKind.Elbow || kind == SpoolFittingKind.Tee || kind == SpoolFittingKind.Olet
			|| FabricationPartClassification.IsElbowPart(fitting, doc) || FabricationPartClassification.IsTeePart(fitting, doc)
			|| IsOletPart(fitting))
		{
			return ResolveUniversalCenterlineIntersectionAnchor(fitting, primaryStraightRun, partsPool);
		}

		if (kind == SpoolFittingKind.Flange || FabricationPartClassification.IsFlangePart(fitting, doc))
		{
			return ResolveUniversalFlangeFaceAnchor(fitting, primaryStraightRun, pipeEndOwner, partsPool);
		}

		return null;
	}

	private static XYZ ResolveUniversalCenterlineIntersectionAnchor(FabricationPart fitting, FabricationPart primaryStraightRun, IList<FabricationPart> partsPool)
	{
		if (TryGetFabricationConnectorIntersectionCenter(fitting, out XYZ intersection))
		{
			return intersection;
		}

		if (TryGetElbowCenterFromMatedPipeAxes(fitting, primaryStraightRun, partsPool, out XYZ pipeAxisCenter))
		{
			return pipeAxisCenter;
		}

		return null;
	}

	private static XYZ ResolveUniversalFlangeFaceAnchor(FabricationPart fitting, FabricationPart primaryStraightRun, FabricationPart pipeEndOwner, IList<FabricationPart> partsPool)
	{
		FabricationPart runPipeHint = primaryStraightRun ?? pipeEndOwner;
		if (TryGetFlangeRaisedFaceAnchorPoint(fitting, runPipeHint, partsPool, out XYZ raisedFace))
		{
			return raisedFace;
		}
		return null;
	}

	/// <summary>
	/// Raised face F at the bolt/gasket side of a flange — never the pipe-side weld neck (back face).
	/// <paramref name="towardMate"/> may be a run pipe, elbow, tee, or other inboard fitting.
	/// </summary>
	private static bool TryGetFlangeRaisedFaceAnchorPoint(FabricationPart flange, FabricationPart towardMate, IList<FabricationPart> partsPool, out XYZ point)
	{
		point = null;
		if (flange == null || partsPool == null)
		{
			return false;
		}
		Document doc = ((Element)flange).Document;
		foreach (Connector connector in ListConnectors(flange))
		{
			if (connector?.Origin == null)
			{
				continue;
			}
			FabricationPart mate = FindMateAtConnector(flange, connector, partsPool);
			if (mate != null && FabricationPartClassification.IsFlangePart(mate, doc))
			{
				point = connector.Origin;
				return true;
			}
		}
		foreach (Connector connector in ListConnectors(flange))
		{
			if (connector?.Origin == null)
			{
				continue;
			}
			FabricationPart mate = FindMateAtConnector(flange, connector, partsPool);
			if (IsGasketPart(mate))
			{
				point = connector.Origin;
				return true;
			}
		}
		XYZ inboardPt = null;
		if (towardMate != null)
		{
			inboardPt = TryMatedConnectorOriginTowardPart(flange, towardMate, partsPool);
			if (inboardPt == null)
			{
				inboardPt = TryMatedConnectorOriginTowardPartThroughJoints(flange, towardMate, partsPool);
			}
		}
		if (inboardPt != null)
		{
			Connector bestOutboard = null;
			double bestDist = -1.0;
			foreach (Connector connector in ListConnectors(flange))
			{
				if (connector?.Origin == null)
				{
					continue;
				}
				double dist = connector.Origin.DistanceTo(inboardPt);
				if (dist > bestDist)
				{
					bestDist = dist;
					bestOutboard = connector;
				}
			}
			if (bestOutboard != null)
			{
				point = bestOutboard.Origin;
				return true;
			}
		}
		return false;
	}

	private static XYZ TryMatedConnectorOriginTowardPartThroughJoints(FabricationPart self, FabricationPart wantedMate, IList<FabricationPart> partsPool)
	{
		if (self == null || wantedMate == null || partsPool == null)
		{
			return null;
		}
		foreach (FabricationPart mate in EnumerateMatedFabricationParts(self, partsPool))
		{
			if (((Element)mate).Id == ((Element)wantedMate).Id)
			{
				return TryMatedConnectorOriginTowardPart(self, mate, partsPool);
			}
			if (IsGasketPart(mate) || IsWeldPart(mate))
			{
				FabricationPart beyond = FindFarSideMateThroughJoint(mate, self, partsPool);
				if (beyond != null && ((Element)beyond).Id == ((Element)wantedMate).Id)
				{
					return TryMatedConnectorOriginTowardPart(self, mate, partsPool);
				}
			}
		}
		return null;
	}

	/// <summary>
	/// Intersects the centerlines of the two straight pipe runs mated to this fitting (e.g. an elbow's
	/// before-leg and after-leg) to find the true theoretical bend center. Falls back to the mated pipes'
	/// closest-approach midpoint when the two centerlines don't intersect exactly (skew due to modeling
	/// tolerance).
	/// </summary>
	private static bool TryGetElbowCenterFromMatedPipeAxes(FabricationPart fitting, FabricationPart primaryStraightRun, IList<FabricationPart> partsPool, out XYZ center)
	{
		center = null;
		if (fitting == null || partsPool == null)
		{
			return false;
		}
		List<(XYZ origin, XYZ direction)> pipeLines = new List<(XYZ, XYZ)>();
		foreach (Connector connector in ListConnectors(fitting))
		{
			if (connector?.Origin == null)
			{
				continue;
			}
			FabricationPart mate = FindMateAtConnector(fitting, connector, partsPool);
			if (mate == null || !IsPipeRunPart(mate) || !TryGetFabricationLineDirection(mate, out XYZ dir))
			{
				continue;
			}
			// The connector origin IS a point on the mated pipe's centerline (that's where they meet),
			// so a line through it along the pipe's own direction is that pipe's true centerline.
			pipeLines.Add((connector.Origin, dir.Normalize()));
		}
		if (pipeLines.Count < 2)
		{
			return false;
		}
		if (TryIntersectUnboundedLines3D(pipeLines[0].origin, pipeLines[0].direction, pipeLines[1].origin, pipeLines[1].direction, out center))
		{
			return true;
		}
		return TryGetClosestApproachMidpoint(pipeLines[0].origin, pipeLines[0].direction, pipeLines[1].origin, pipeLines[1].direction, 1.0, out center);
	}

	private static bool TryGetConnectorAxisLine(Connector connector, out XYZ origin, out XYZ direction)
	{
		origin = null;
		direction = null;
		if (connector?.Origin == null)
		{
			return false;
		}
		origin = connector.Origin;
		try
		{
			Transform coordinateSystem = connector.CoordinateSystem;
			if (coordinateSystem != null)
			{
				direction = coordinateSystem.BasisZ;
				if (direction != null && direction.GetLength() > 1E-09)
				{
					direction = direction.Normalize();
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static double DistancePointToUnboundedLine(XYZ lineOrigin, XYZ lineDirectionUnit, XYZ point)
	{
		if (lineOrigin == null || lineDirectionUnit == null || point == null)
		{
			return double.MaxValue;
		}
		XYZ delta = point - lineOrigin;
		XYZ parallel = lineDirectionUnit.Multiply(delta.DotProduct(lineDirectionUnit));
		return (delta - parallel).GetLength();
	}

	private static bool TryIntersectUnboundedLines3D(XYZ originA, XYZ dirA, XYZ originB, XYZ dirB, out XYZ point)
	{
		point = null;
		if (originA == null || dirA == null || originB == null || dirB == null)
		{
			return false;
		}
		double a = dirA.DotProduct(dirA);
		double b = dirA.DotProduct(dirB);
		double c = dirB.DotProduct(dirB);
		double d = dirA.DotProduct(originA - originB);
		double e = dirB.DotProduct(originA - originB);
		double denom = a * c - b * b;
		if (Math.Abs(denom) < 1E-12)
		{
			return false;
		}
		double tA = (b * e - c * d) / denom;
		double tB = (a * e - b * d) / denom;
		XYZ onA = originA + dirA.Multiply(tA);
		XYZ onB = originB + dirB.Multiply(tB);
		if (onA.DistanceTo(onB) > 0.25)
		{
			return false;
		}
		point = (onA + onB) * 0.5;
		return true;
	}

	/// <summary>
	/// Center of multi-port fittings (tee, elbow, cross, etc.): where connector centerline axes meet inside the body.
	/// </summary>
	private static bool TryGetFabricationConnectorIntersectionCenter(FabricationPart fitting, out XYZ center)
	{
		center = null;
		if (fitting == null)
		{
			return false;
		}
		List<(XYZ origin, XYZ direction)> axes = new List<(XYZ, XYZ)>();
		foreach (Connector connector in ListConnectors(fitting))
		{
			if (TryGetConnectorAxisLine(connector, out XYZ origin, out XYZ direction))
			{
				axes.Add((origin, direction));
			}
		}
		if (axes.Count < 2)
		{
			return false;
		}
		XYZ interiorHint = TryGetFabricationPartOrigin(fitting);
		if (interiorHint == null)
		{
			interiorHint = new XYZ(
				axes.Average((axis) => axis.origin.X),
				axes.Average((axis) => axis.origin.Y),
				axes.Average((axis) => axis.origin.Z));
		}
		for (int i = 0; i < axes.Count; i++)
		{
			axes[i] = (axes[i].origin, OrientConnectorAxisInward(axes[i].origin, axes[i].direction, interiorHint));
		}
		if (axes.Count == 2)
		{
			if (TryIntersectUnboundedLines3D(axes[0].origin, axes[0].direction, axes[1].origin, axes[1].direction, out center))
			{
				return true;
			}
			return TryGetClosestApproachMidpoint(axes[0].origin, axes[0].direction, axes[1].origin, axes[1].direction, 0.5, out center);
		}
		XYZ best = null;
		double bestScore = double.MaxValue;
		for (int i = 0; i < axes.Count; i++)
		{
			for (int j = i + 1; j < axes.Count; j++)
			{
				if (!TryIntersectUnboundedLines3D(axes[i].origin, axes[i].direction, axes[j].origin, axes[j].direction, out XYZ candidate)
					&& !TryGetClosestApproachMidpoint(axes[i].origin, axes[i].direction, axes[j].origin, axes[j].direction, 0.5, out candidate))
				{
					continue;
				}
				double score = axes.Sum((axis) => DistancePointToUnboundedLine(axis.origin, axis.direction, candidate));
				if (score < bestScore)
				{
					bestScore = score;
					best = candidate;
				}
			}
		}
		const double maxAxisMissFeet = 0.2;
		if (best != null && bestScore <= maxAxisMissFeet * axes.Count)
		{
			center = best;
			return true;
		}
		return false;
	}

	private static XYZ OrientConnectorAxisInward(XYZ connectorOrigin, XYZ axisDirection, XYZ interiorHint)
	{
		if (connectorOrigin == null || axisDirection == null || interiorHint == null)
		{
			return axisDirection;
		}
		XYZ towardInterior = interiorHint - connectorOrigin;
		if (towardInterior.GetLength() < 1E-09)
		{
			return axisDirection;
		}
		return axisDirection.DotProduct(towardInterior.Normalize()) < 0.0 ? axisDirection.Negate() : axisDirection;
	}

	private static bool TryGetClosestApproachMidpoint(
		XYZ originA,
		XYZ dirA,
		XYZ originB,
		XYZ dirB,
		double maxSeparationFeet,
		out XYZ midpoint)
	{
		midpoint = null;
		if (originA == null || dirA == null || originB == null || dirB == null)
		{
			return false;
		}
		double a = dirA.DotProduct(dirA);
		double b = dirA.DotProduct(dirB);
		double c = dirB.DotProduct(dirB);
		double d = dirA.DotProduct(originA - originB);
		double e = dirB.DotProduct(originA - originB);
		double denom = a * c - b * b;
		if (Math.Abs(denom) < 1E-12)
		{
			return false;
		}
		double tA = (b * e - c * d) / denom;
		double tB = (a * e - b * d) / denom;
		XYZ onA = originA + dirA.Multiply(tA);
		XYZ onB = originB + dirB.Multiply(tB);
		if (onA.DistanceTo(onB) > maxSeparationFeet)
		{
			return false;
		}
		midpoint = (onA + onB) * 0.5;
		return true;
	}

	private static XYZ TryGetFabricationFittingBodyCenter(FabricationPart fitting)
	{
		if (fitting == null)
		{
			return null;
		}
		if (TryGetFabricationConnectorIntersectionCenter(fitting, out XYZ intersection))
		{
			return intersection;
		}
		try
		{
			Location location = ((Element)fitting).Location;
			LocationCurve val = (LocationCurve)(object)((location is LocationCurve) ? location : null);
			if (val != null && (GeometryObject)(object)val.Curve != (GeometryObject)null)
			{
				XYZ val2 = val.Curve.Evaluate(0.5, true);
				if (val2 != null)
				{
					return val2;
				}
			}
		}
		catch
		{
		}
		List<Connector> list = ListConnectors(fitting);
		if (list.Count >= 2)
		{
			return new XYZ(list.Average((Connector c) => c.Origin.X), list.Average((Connector c) => c.Origin.Y), list.Average((Connector c) => c.Origin.Z));
		}
		try
		{
			BoundingBoxXYZ val3 = fitting.get_BoundingBox(null);
			if (val3 != null)
			{
				return (val3.Min + val3.Max) * 0.5;
			}
		}
		catch
		{
		}
		return null;
	}

	private static XYZ TryMatedConnectorOriginTowardPart(FabricationPart fitting, FabricationPart wantedMate, IList<FabricationPart> partsPool)
	{
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Expected O, but got Unknown
		if (((fitting != null) ? fitting.ConnectorManager : null) == null || wantedMate == null || partsPool == null)
		{
			return null;
		}
		foreach (Connector connector in fitting.ConnectorManager.Connectors)
		{
			Connector val = connector;
			if (((val != null) ? val.Origin : null) != null)
			{
				FabricationPart val2 = FindMateAtConnector(fitting, val, partsPool);
				if (val2 != null && ((Element)val2).Id == ((Element)wantedMate).Id)
				{
					return val.Origin;
				}
			}
		}
		return null;
	}

	private static XYZ GetFabricationCenterPoint(FabricationPart part)
	{
		XYZ origin = TryGetFabricationPartOrigin(part);
		if (origin != null)
		{
			return origin;
		}
		List<XYZ> fabricationConnectorPoints = GetFabricationConnectorPoints(part);
		if (fabricationConnectorPoints.Count > 0)
		{
			return new XYZ(fabricationConnectorPoints.Average((XYZ p) => p.X), fabricationConnectorPoints.Average((XYZ p) => p.Y), fabricationConnectorPoints.Average((XYZ p) => p.Z));
		}
		try
		{
			Location location = ((Element)part).Location;
			LocationPoint val = (LocationPoint)(object)((location is LocationPoint) ? location : null);
			if (val != null)
			{
				return val.Point;
			}
		}
		catch
		{
		}
		return null;
	}

	private static List<Connector> ListConnectors(FabricationPart part)
	{
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Expected O, but got Unknown
		List<Connector> list = new List<Connector>();
		if (((part != null) ? part.ConnectorManager : null) == null)
		{
			return list;
		}
		foreach (Connector connector in part.ConnectorManager.Connectors)
		{
			Connector val = connector;
			if (((val != null) ? val.Origin : null) != null)
			{
				list.Add(val);
			}
		}
		return list;
	}

	private static bool IsLinearDimensionType(DimensionType dimType)
	{
		if (dimType == null)
		{
			return false;
		}
		try
		{
			if ((int)dimType.StyleType != 0)
			{
				return false;
			}
		}
		catch
		{
			return false;
		}
		string name = ((Element)dimType).Name ?? string.Empty;
		if (IsAlignedDimensionTypeName(name))
		{
			return false;
		}
		if (name.IndexOf("angular", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		if (name.IndexOf("radial", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		if (name.IndexOf("diameter", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		return true;
	}

	private static bool IsAlignedDimensionTypeName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}
		return name.IndexOf("align", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static DimensionType TryGetLinearDimensionTypeDefault(Document doc)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			IList<DimensionType> source = (from DimensionType dt in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(DimensionType))
				where IsLinearDimensionType(dt)
				select dt).ToList();
			DimensionType val = source.FirstOrDefault((DimensionType dt) => (((Element)dt).Name ?? string.Empty).IndexOf("linear", StringComparison.OrdinalIgnoreCase) >= 0);
			if (val != null)
			{
				return val;
			}
			val = source.FirstOrDefault();
			return val;
		}
		catch
		{
			return null;
		}
	}

	private static DimensionType TryResolveLinearDimensionType(Document doc, SpoolingManagerSettings spoolSettings)
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		if (doc == null)
		{
			return null;
		}
		string pick = spoolSettings?.AutoDimensionTypeName?.Trim();
		if (string.IsNullOrEmpty(pick))
		{
			return TryGetLinearDimensionTypeDefault(doc);
		}
		try
		{
			IList<DimensionType> source = (from DimensionType dt in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(DimensionType))
				where IsLinearDimensionType(dt)
				select dt).ToList();
			DimensionType val = source.FirstOrDefault((DimensionType dt) => string.Equals(((Element)dt).Name, pick, StringComparison.OrdinalIgnoreCase));
			if (val != null)
			{
				return val;
			}
			val = source.FirstOrDefault((DimensionType dt) => (((Element)dt).Name ?? string.Empty).IndexOf(pick, StringComparison.OrdinalIgnoreCase) >= 0);
			if (val != null)
			{
				return val;
			}
		}
		catch
		{
		}
		return TryGetLinearDimensionTypeDefault(doc);
	}

	private static bool TryGetDimensionTypeSheetDistanceFeet(DimensionType dimType, BuiltInParameter builtInParameter, out double sheetFeet)
	{
		sheetFeet = 0.0;
		if (dimType == null)
		{
			return false;
		}
		try
		{
			Parameter parameter = dimType.get_Parameter(builtInParameter);
			if (parameter != null && parameter.StorageType == StorageType.Double)
			{
				sheetFeet = parameter.AsDouble();
				return sheetFeet > 1E-09;
			}
		}
		catch
		{
		}
		return false;
	}

	private static double ResolveSpoolLinearDimensionModelOffset(View scaleView, DimensionType dimType, int stackIndex, bool isHorizontalMeasurement, bool hasDimAnnotations)
	{
		// Both horizontal (pulled up/down) and vertical (pulled left/right) dimensions start at 3/8" from the
		// geometry with no annotations, and 1/2" when tags/annotations are applied to the dimension. Same rule
		// for every view (Front/Back/Left/Right/Top).
		const double defaultSnapSheetFeet = 1.0 / 48.0;
		const double firstOffsetNoAnnotationSheetFeet = 3.0 / (12.0 * 8.0);
		const double firstOffsetWithAnnotationSheetFeet = 1.0 / 24.0;
		double snapSheetFeet = defaultSnapSheetFeet;
		if (!TryGetDimensionTypeSheetDistanceFeet(dimType, BuiltInParameter.DIM_STYLE_DIM_LINE_SNAP_DIST, out snapSheetFeet))
		{
			snapSheetFeet = defaultSnapSheetFeet;
		}
		double snapModelSpacing = ConvertSheetOffsetToModelDistance(scaleView, snapSheetFeet);
		int slot = Math.Max(stackIndex, 0);
		double firstOffsetSheetFeet = hasDimAnnotations ? firstOffsetWithAnnotationSheetFeet : firstOffsetNoAnnotationSheetFeet;
		double firstOffsetModel = ConvertSheetOffsetToModelDistance(scaleView, firstOffsetSheetFeet);
		if (slot == 0)
		{
			return firstOffsetModel;
		}
		return firstOffsetModel + (double)slot * snapModelSpacing;
	}

	private static bool IsViewHorizontalMeasurement(View view, XYZ chord)
	{
		if (view == null || chord == null || chord.GetLength() < 1E-09)
		{
			return true;
		}
		XYZ viewNormal = view.ViewDirection;
		if (viewNormal == null || viewNormal.GetLength() < 1E-09)
		{
			return true;
		}
		viewNormal = viewNormal.Normalize();
		XYZ right = ProjectVectorToViewPlane(view.RightDirection, viewNormal);
		XYZ up = ProjectVectorToViewPlane(view.UpDirection, viewNormal);
		if (right.GetLength() < 1E-09 || up.GetLength() < 1E-09)
		{
			return true;
		}
		right = right.Normalize();
		up = up.Normalize();
		XYZ chordInPlane = ProjectVectorToViewPlane(chord, viewNormal);
		if (chordInPlane == null || chordInPlane.GetLength() < 1E-09)
		{
			return true;
		}
		chordInPlane = chordInPlane.Normalize();
		return Math.Abs(chordInPlane.DotProduct(right)) >= Math.Abs(chordInPlane.DotProduct(up));
	}

	private static int ResolveSpoolDimensionPlacementOffsetSign(View view, XYZ chord, int intentOffsetSign, XYZ branchFacing = null, XYZ viewNormal = null)
	{
		if (branchFacing != null && viewNormal != null && TryGetViewPlaneAxes(view, out _, out var right, out var up))
		{
			XYZ facingInPlane = ProjectVectorToViewPlane(branchFacing, viewNormal);
			if (facingInPlane != null && facingInPlane.GetLength() > 1E-09)
			{
				facingInPlane = facingInPlane.Normalize();
				if (Math.Abs(facingInPlane.DotProduct(up)) >= Math.Abs(facingInPlane.DotProduct(right)))
				{
					return facingInPlane.DotProduct(up) >= 0.0 ? 1 : -1;
				}
				return facingInPlane.DotProduct(right) >= 0.0 ? 1 : -1;
			}
		}
		if (intentOffsetSign < 0)
		{
			return -1;
		}
		return IsViewHorizontalMeasurement(view, chord) ? 1 : -1;
	}

	private static bool ShouldOffsetBranchFacingAlongViewUp(View view, XYZ branchFacing, XYZ viewNormal, XYZ chord)
	{
		if (view == null || branchFacing == null || viewNormal == null || !TryGetViewPlaneAxes(view, out _, out var right, out var up))
		{
			return false;
		}
		XYZ facingInPlane = ProjectVectorToViewPlane(branchFacing, viewNormal);
		if (facingInPlane == null || facingInPlane.GetLength() < 1E-09)
		{
			return false;
		}
		facingInPlane = facingInPlane.Normalize();
		if (IsViewHorizontalMeasurement(view, chord))
		{
			return Math.Abs(facingInPlane.DotProduct(up)) >= Math.Abs(facingInPlane.DotProduct(right));
		}
		return Math.Abs(facingInPlane.DotProduct(up)) > Math.Abs(facingInPlane.DotProduct(right));
	}

	private static void AlignAnchorsForStackedLinearDimension(XYZ anchorA, XYZ anchorB, XYZ offsetAxis, out XYZ alignedA, out XYZ alignedB)
	{
		alignedA = anchorA;
		alignedB = anchorB;
		if (anchorA == null || anchorB == null || offsetAxis == null || offsetAxis.GetLength() < 1E-09)
		{
			return;
		}
		XYZ unit = offsetAxis.Normalize();
		double coordA = anchorA.DotProduct(unit);
		double coordB = anchorB.DotProduct(unit);
		double targetCoord = Math.Max(coordA, coordB);
		alignedA = anchorA + unit.Multiply(targetCoord - coordA);
		alignedB = anchorB + unit.Multiply(targetCoord - coordB);
	}

	private static void CoerceAnchorPointsForViewLinearDimension(View view, XYZ planeOrigin, XYZ planeNormal, ref XYZ anchorA, ref XYZ anchorB, int placementOffsetSign, XYZ branchFacing = null)
	{
		if (view == null || planeOrigin == null || planeNormal == null || anchorA == null || anchorB == null)
		{
			return;
		}
		if (!TryGetViewPlaneAxes(view, out _, out var right, out var up))
		{
			return;
		}
		XYZ chord = anchorB - anchorA;
		if (chord == null || chord.GetLength() < 1E-09)
		{
			return;
		}
		XYZ relA = ProjectToSketchPlane(anchorA, planeOrigin, planeNormal) - planeOrigin;
		XYZ relB = ProjectToSketchPlane(anchorB, planeOrigin, planeNormal) - planeOrigin;
		if (ShouldOffsetBranchFacingAlongViewUp(view, branchFacing, planeNormal, chord))
		{
			double sharedRight = (relA.DotProduct(right) + relB.DotProduct(right)) * 0.5;
			anchorA = planeOrigin + right.Multiply(sharedRight) + up.Multiply(relA.DotProduct(up));
			anchorB = planeOrigin + right.Multiply(sharedRight) + up.Multiply(relB.DotProduct(up));
			return;
		}
		if (IsViewHorizontalMeasurement(view, chord))
		{
			double sharedUp = (relA.DotProduct(up) + relB.DotProduct(up)) * 0.5;
			anchorA = planeOrigin + right.Multiply(relA.DotProduct(right)) + up.Multiply(sharedUp);
			anchorB = planeOrigin + right.Multiply(relB.DotProduct(right)) + up.Multiply(sharedUp);
			return;
		}
		double coordRightA = relA.DotProduct(right);
		double coordRightB = relB.DotProduct(right);
		double sharedRightSide = (placementOffsetSign < 0) ? Math.Min(coordRightA, coordRightB) : Math.Max(coordRightA, coordRightB);
		anchorA = planeOrigin + right.Multiply(sharedRightSide) + up.Multiply(relA.DotProduct(up));
		anchorB = planeOrigin + right.Multiply(sharedRightSide) + up.Multiply(relB.DotProduct(up));
	}

	private static bool ShouldUseDetailCurveDimensionHelpers(Element first, Element second, View view)
	{
		return false;
	}

	private static bool ShouldUseDetailCurveChainHelpers((Element element, XYZ targetWorld)[] anchors, View view)
	{
		return false;
	}

	private static void DeleteDetailCurveHelper(Document doc, DetailCurve curve)
	{
		if (doc == null || curve == null)
		{
			return;
		}
		try
		{
			doc.Delete(((Element)curve).Id);
		}
		catch
		{
		}
	}

	private static void DeleteReferenceHelperElement(Document doc, ElementId helperId)
	{
		if (doc == null || helperId == null || helperId == ElementId.InvalidElementId)
		{
			return;
		}
		try
		{
			doc.Delete(helperId);
		}
		catch
		{
		}
	}

	private static bool TryGetCurveReference(Curve curve, out Reference reference)
	{
		reference = null;
		if ((GeometryObject)(object)curve == (GeometryObject)null)
		{
			return false;
		}
		reference = curve.Reference;
		if (reference != null)
		{
			return true;
		}
		try
		{
			reference = curve.GetEndPointReference(0);
			if (reference != null)
			{
				return true;
			}
		}
		catch
		{
		}
		try
		{
			reference = curve.GetEndPointReference(1);
			return reference != null;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryCreateSpoolReferenceHelper(Document doc, View view, XYZ anchorWorld, XYZ helperDirectionUnit, out Reference reference, out ElementId helperElementId, out string failureDetail)
	{
		reference = null;
		helperElementId = ElementId.InvalidElementId;
		failureDetail = "Temporary detail/reference helper curves are disabled for auto-dimensioning.";
		return false;
	}

	private static void TryUnhideSpoolDimensionInView(View view, Dimension dim)
	{
		if (view == null || dim == null)
		{
			return;
		}
		TryUnhideElementsInView(view, ((Element)dim).Id);
	}

	private static void TryUnhideElementsInView(View view, params ElementId[] elementIds)
	{
		if (view == null || elementIds == null || elementIds.Length == 0)
		{
			return;
		}
		List<ElementId> list = elementIds.Where((ElementId id) => id != null && id != ElementId.InvalidElementId).ToList();
		if (list.Count == 0)
		{
			return;
		}
		try
		{
			view.UnhideElements((ICollection<ElementId>)(object)list);
		}
		catch
		{
		}
	}

	private static bool TryGetViewPlaneAxes(View view, out XYZ viewNormal, out XYZ right, out XYZ up)
	{
		viewNormal = null;
		right = null;
		up = null;
		if (view == null)
		{
			return false;
		}
		viewNormal = view.ViewDirection;
		if (viewNormal == null || viewNormal.GetLength() < 1E-09)
		{
			return false;
		}
		viewNormal = viewNormal.Normalize();
		right = ProjectVectorToViewPlane(view.RightDirection, viewNormal);
		up = ProjectVectorToViewPlane(view.UpDirection, viewNormal);
		if (right.GetLength() < 1E-09 || up.GetLength() < 1E-09)
		{
			return false;
		}
		right = right.Normalize();
		up = up.Normalize();
		return true;
	}

	private static bool TryGetViewLinearDimensionAxes(View view, XYZ anchorA, XYZ anchorB, out XYZ dimensionLineAxis, out XYZ referenceLineAxis)
	{
		dimensionLineAxis = null;
		referenceLineAxis = null;
		if (!TryGetViewPlaneAxes(view, out var _, out var right, out var up) || anchorA == null || anchorB == null)
		{
			return false;
		}
		if (IsViewHorizontalMeasurement(view, anchorB - anchorA))
		{
			dimensionLineAxis = right;
			referenceLineAxis = up;
		}
		else
		{
			dimensionLineAxis = up;
			referenceLineAxis = right;
		}
		return true;
	}

	private static XYZ ProjectVectorToViewPlane(XYZ vector, XYZ viewNormalUnit)
	{
		if (vector == null || viewNormalUnit == null)
		{
			return vector;
		}
		return vector - viewNormalUnit.Multiply(vector.DotProduct(viewNormalUnit));
	}

	/// <summary>
	/// Places a dimension line offset along an explicit in-view stack direction.
	/// Measurement axis is the view Right/Up perpendicular to the stack direction — never the chord.
	/// Example: stack along Up → measure along Right (horizontal dim).
	/// </summary>
	private static bool TryBuildStackedLinearDimensionLine(
		View view,
		XYZ anchorA,
		XYZ anchorB,
		double offsetDistanceFeet,
		XYZ stackDirectionInView,
		out Line dimLine)
	{
		dimLine = null;
		if (view == null || anchorA == null || anchorB == null || stackDirectionInView == null || offsetDistanceFeet <= 0.0)
		{
			return false;
		}
		if (!TryGetViewPlaneAxes(view, out XYZ viewNormal, out XYZ right, out XYZ up))
		{
			return false;
		}
		if (!TryGetViewSketchPlane(view, out XYZ planeOrigin, out _))
		{
			planeOrigin = anchorA;
		}
		if (planeOrigin == null || viewNormal == null || viewNormal.GetLength() < 1E-09)
		{
			return false;
		}
		viewNormal = viewNormal.Normalize();
		XYZ a = ProjectToSketchPlane(anchorA, planeOrigin, viewNormal);
		XYZ b = ProjectToSketchPlane(anchorB, planeOrigin, viewNormal);

		XYZ stackDir = ProjectVectorToViewPlane(stackDirectionInView, viewNormal);
		if (stackDir == null || stackDir.GetLength() < 1E-09)
		{
			return false;
		}
		stackDir = stackDir.Normalize();

		// Stack direction chooses the offset axis; measure on the other view axis.
		bool stackAlongUp = Math.Abs(stackDir.DotProduct(up)) >= Math.Abs(stackDir.DotProduct(right));
		XYZ runAxis = stackAlongUp ? right : up;
		stackDir = stackAlongUp ? up : right;
		if (stackDirectionInView.DotProduct(stackDir) < 0.0)
		{
			stackDir = stackDir.Negate();
		}

		double span = Math.Abs((b - a).DotProduct(runAxis));
		if (span < 1.0 / 96.0)
		{
			return false;
		}

		double half = Math.Max(span * 0.5 + 0.25, 0.5);
		XYZ aProj = planeOrigin
			+ runAxis.Multiply(a.DotProduct(runAxis) - planeOrigin.DotProduct(runAxis))
			+ stackDir.Multiply(a.DotProduct(stackDir) - planeOrigin.DotProduct(stackDir));
		XYZ bProj = planeOrigin
			+ runAxis.Multiply(b.DotProduct(runAxis) - planeOrigin.DotProduct(runAxis))
			+ stackDir.Multiply(b.DotProduct(stackDir) - planeOrigin.DotProduct(stackDir));
		XYZ mid = (aProj + bProj) * 0.5;
		XYZ lineOrigin = mid + stackDir.Multiply(offsetDistanceFeet);
		dimLine = Line.CreateBound(lineOrigin - runAxis.Multiply(half), lineOrigin + runAxis.Multiply(half));
		return dimLine != null && dimLine.IsBound;
	}

	private static bool TryBuildViewLinearDimensionLine(View view, XYZ anchorA, XYZ anchorB, double offsetSigned, out Line dimLine, XYZ branchFacing = null)
	{
		dimLine = null;
		if (view == null || anchorA == null || anchorB == null)
		{
			return false;
		}
		if (!TryGetViewPlaneAxes(view, out var viewNormal, out var right, out var up))
		{
			return false;
		}
		if (!TryGetViewSketchPlane(view, out var planeOrigin, out _))
		{
			planeOrigin = view.Origin;
		}
		if (planeOrigin == null)
		{
			return false;
		}
		XYZ relA = ProjectToSketchPlane(anchorA, planeOrigin, viewNormal) - planeOrigin;
		XYZ relB = ProjectToSketchPlane(anchorB, planeOrigin, viewNormal) - planeOrigin;
		double coordRightA = relA.DotProduct(right);
		double coordRightB = relB.DotProduct(right);
		double coordUpA = relA.DotProduct(up);
		double coordUpB = relB.DotProduct(up);
		XYZ chord = anchorB - anchorA;
		bool horizontalMeasurement = IsViewHorizontalMeasurement(view, chord);
		XYZ center;
		XYZ axis;
		double halfLen;
		if (horizontalMeasurement)
		{
			double offsetCoord = (coordUpA + coordUpB) * 0.5 + offsetSigned;
			double spanMin = Math.Min(coordRightA, coordRightB);
			double spanMax = Math.Max(coordRightA, coordRightB);
			halfLen = Math.Max((spanMax - spanMin) * 0.5 + 0.25, 0.5);
			double midRight = (spanMin + spanMax) * 0.5;
			center = planeOrigin + right.Multiply(midRight) + up.Multiply(offsetCoord);
			axis = right;
		}
		else if (ShouldOffsetBranchFacingAlongViewUp(view, branchFacing, viewNormal, chord))
		{
			double sharedRight = (coordRightA + coordRightB) * 0.5;
			double spanMin = Math.Min(coordUpA, coordUpB);
			double spanMax = Math.Max(coordUpA, coordUpB);
			halfLen = Math.Max((spanMax - spanMin) * 0.5 + 0.25, 0.5);
			double offsetCoord = (offsetSigned < 0.0) ? spanMin + offsetSigned : spanMax + offsetSigned;
			double midUp = (spanMin + spanMax) * 0.5;
			center = planeOrigin + right.Multiply(sharedRight) + up.Multiply(offsetCoord);
			axis = up;
		}
		else
		{
			double baseRight = (offsetSigned < 0.0) ? Math.Min(coordRightA, coordRightB) : Math.Max(coordRightA, coordRightB);
			double offsetCoord = baseRight + offsetSigned;
			double spanMin = Math.Min(coordUpA, coordUpB);
			double spanMax = Math.Max(coordUpA, coordUpB);
			halfLen = Math.Max((spanMax - spanMin) * 0.5 + 0.25, 0.5);
			double midUp = (spanMin + spanMax) * 0.5;
			center = planeOrigin + right.Multiply(offsetCoord) + up.Multiply(midUp);
			axis = up;
		}
		dimLine = Line.CreateBound(center - axis.Multiply(halfLen), center + axis.Multiply(halfLen));
		return (GeometryObject)(object)dimLine != (GeometryObject)null && ((Curve)dimLine).IsBound;
	}

	private static bool TryValidateSpoolDimensionReferencePoint(XYZ targetWorld, XYZ referencePointWorld, double maxDistanceFeet = 0.5)
	{
		if (targetWorld == null || referencePointWorld == null)
		{
			return false;
		}
		return referencePointWorld.DistanceTo(targetWorld) <= maxDistanceFeet;
	}

	private static void TryRegenerateForNewCurveReferences(Document doc)
	{
		if (doc == null)
		{
			return;
		}
		try
		{
			RegenTracked(doc);
		}
		catch
		{
		}
	}

	private static bool TryPlaceSpoolLinearDimensionSleeveStyle(Document doc, View view, Element elemNearFitting, XYZ fittingTargetWorld, Element elemNearPipeEnd, XYZ pipeEndTargetWorld, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string failureDetail, FabricationDimensionRefRole? forcedFitRole = null, FabricationDimensionRefRole? forcedPipeRole = null, int offsetSign = 1, bool lockOffsetSign = false, XYZ branchFacingDirection = null, string dimensionPolicyRole = null, XYZ stackDirectionInView = null)
	{
		return TryPlaceSpoolLinearDimensionSleeveStyle(doc, view, elemNearFitting, fittingTargetWorld, elemNearPipeEnd, pipeEndTargetWorld, spoolSettings, ref stackIndex, out failureDetail, out _, forcedFitRole, forcedPipeRole, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView);
	}

	private static bool TryPlaceSpoolLinearDimensionSleeveStyle(Document doc, View view, Element elemNearFitting, XYZ fittingTargetWorld, Element elemNearPipeEnd, XYZ pipeEndTargetWorld, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string failureDetail, out Dimension placedDim, FabricationDimensionRefRole? forcedFitRole = null, FabricationDimensionRefRole? forcedPipeRole = null, int offsetSign = 1, bool lockOffsetSign = false, XYZ branchFacingDirection = null, string dimensionPolicyRole = null, XYZ stackDirectionInView = null)
	{
		failureDetail = null;
		placedDim = null;
		if (doc == null || view == null || elemNearFitting == null || elemNearPipeEnd == null || fittingTargetWorld == null || pipeEndTargetWorld == null)
		{
			failureDetail = "Missing document, view, elements, or anchor points.";
			return false;
		}
		View dimView = view;
		try
		{
			View activeView = doc.ActiveView;
			if (activeView != null && ((Element)activeView).Id == ((Element)view).Id)
			{
				dimView = activeView;
			}
		}
		catch
		{
		}
		if (!TryGetViewSketchPlane(view, out var planeOrigin, out var planeNormal))
		{
			failureDetail = "Could not resolve view sketch plane.";
			return false;
		}
		XYZ val = pipeEndTargetWorld - fittingTargetWorld;
		if (val == null || val.GetLength() < 1E-09)
		{
			failureDetail = "Fitting and pipe-end targets coincide.";
			return false;
		}
		XYZ chordUnit = val.Normalize();
		XYZ val2 = ProjectToSketchPlane(fittingTargetWorld, planeOrigin, planeNormal);
		XYZ val3 = ProjectToSketchPlane(pipeEndTargetWorld, planeOrigin, planeNormal);
		XYZ val4 = planeNormal;
		val4 = val4.Normalize();
		XYZ val5 = val3 - val2;
		XYZ val6 = val5 - val4 * val5.DotProduct(val4);
		if (val6.GetLength() < 1E-09)
		{
			XYZ rightDirection = view.RightDirection;
			if (rightDirection == null || rightDirection.GetLength() < 1E-09)
			{
				failureDetail = "Run direction projects to a point in the view plane.";
				return false;
			}
			rightDirection = rightDirection.Normalize();
			val6 = rightDirection - val4 * rightDirection.DotProduct(val4);
			if (val6.GetLength() < 1E-09)
			{
				failureDetail = "Could not build an in-plane run direction.";
				return false;
			}
		}
		val6 = val6.Normalize();
		XYZ val7 = val4.CrossProduct(val6);
		if (val7.GetLength() < 1E-09)
		{
			XYZ upDirection = view.UpDirection;
			if (upDirection == null || upDirection.GetLength() < 1E-09)
			{
				failureDetail = "Could not build a perpendicular offset direction.";
				return false;
			}
			val7 = upDirection - val4 * upDirection.DotProduct(val4);
			if (val7.GetLength() < 1E-09)
			{
				failureDetail = "View up direction is degenerate in-plane.";
				return false;
			}
		}
		val7 = val7.Normalize();
		// Never dimension a single FITTING to itself (elbow C to same elbow, etc.).
		// Bare pipe E-E is different: one pipe, two open ends — always required when nothing else is on the run.
		if (elemNearFitting != null && elemNearPipeEnd != null && elemNearFitting.Id == elemNearPipeEnd.Id)
		{
			if (elemNearFitting is FabricationPart samePart && IsFittingLikeForSpoolDim(samePart))
			{
				failureDetail = "Refusing to dimension a single fitting to itself.";
				return false;
			}
			if (!(elemNearFitting is FabricationPart barePipe && IsPipeRunPart(barePipe) && val.GetLength() >= 1.0 / 24.0))
			{
				failureDetail = "Refusing same-element dimension unless it is a bare pipe open-end to open-end (E-E).";
				return false;
			}
		}
		// Belt-and-suspenders: shop/field welds and gaskets are never valid dimension anchors, no matter
		// which caller resolved them. A weld or gasket showing up here means an anchor-resolution helper
		// walked to the wrong element (e.g. stopped at a weld joint instead of continuing to the real
		// fitting/pipe end past it) — refuse instead of placing a meaningless dimension onto it.
		if ((elemNearFitting is FabricationPart fpA && (IsWeldPart(fpA) || IsGasketPart(fpA)))
			|| (elemNearPipeEnd is FabricationPart fpB && (IsWeldPart(fpB) || IsGasketPart(fpB))))
		{
			failureDetail = "Refusing to dimension to a weld/gasket element — not a valid anchor.";
			return false;
		}

		if (val.GetLength() < 1.0
			&& !string.Equals(dimensionPolicyRole, "fitting-flange-stub", StringComparison.Ordinal)
			&& elemNearFitting is FabricationPart shortFit
			&& elemNearPipeEnd is FabricationPart shortOther
			&& AreFabricationPartsDirectlyConnected(shortFit, shortOther)
			&& (IsFittingLikeForSpoolDim(shortFit) || IsFittingLikeForSpoolDim(shortOther)))
		{
			failureDetail = "Refusing short fitting-to-adjacent-stub dimension.";
			return false;
		}
		FabricationDimensionRefRole fitRole = forcedFitRole ?? ResolveFabricationDimensionRefRole(elemNearFitting, isPipeEndAnchor: false);
		FabricationDimensionRefRole pipeRole = forcedPipeRole ?? ResolveFabricationDimensionRefRole(elemNearPipeEnd, isPipeEndAnchor: true);
		if (elemNearFitting is FabricationPart bareA && elemNearPipeEnd is FabricationPart bareB
			&& bareA.Id == bareB.Id && IsPipeRunPart(bareA)
			&& fitRole == FabricationDimensionRefRole.PipeOpenEnd && pipeRole == FabricationDimensionRefRole.PipeOpenEnd)
		{
			int stackBarePipe = stackIndex;
			if (TryResolveOppositePipeOpenEndReferences(elemNearFitting, view, fittingTargetWorld, pipeEndTargetWorld, chordUnit, out Reference refNearEnd, out Reference refFarEnd)
				&& TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, refNearEnd, refFarEnd, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
			{
				return true;
			}
			stackIndex = stackBarePipe;
		}
		List<Reference> fitCandidates = ((elemNearFitting is FabricationPart) ? GetAllFabricationInstanceDimensionReferences(elemNearFitting, view, fittingTargetWorld, fitRole, chordUnit) : CollectFabricationDimensionReferenceCandidates(elemNearFitting, view, fittingTargetWorld, chordUnit, val7));
		Document refDoc = view?.Document;
		bool isFittingFlangeStubPolicy = string.Equals(dimensionPolicyRole, "fitting-flange-stub", StringComparison.Ordinal);
		List<Reference> pipeCandidates = ((elemNearPipeEnd is FabricationPart) ? (isFittingFlangeStubPolicy ? GetFlangeFaceDimensionReferencesWithFallback(elemNearPipeEnd, view, pipeEndTargetWorld, chordUnit.Negate()) : GetAllFabricationInstanceDimensionReferences(elemNearPipeEnd, view, pipeEndTargetWorld, pipeRole, chordUnit)) : CollectFabricationDimensionReferenceCandidates(elemNearPipeEnd, view, pipeEndTargetWorld, chordUnit.Negate(), val7));
		if (isFittingFlangeStubPolicy)
		{
			int savedStubStack = stackIndex;
			Reference lockedFitRef = TryResolveFabricationCenterlineReference(elemNearFitting, view, fittingTargetWorld, chordUnit);
			if (lockedFitRef == null)
			{
				TryResolveFabricationAnchorReference(elemNearFitting, view, fittingTargetWorld, FabricationDimensionRefRole.RunStartFitting, chordUnit, out lockedFitRef);
			}
			if (lockedFitRef == null && fitCandidates.Count > 0)
			{
				lockedFitRef = fitCandidates[0];
			}
			foreach (int sign in new[] { offsetSign, -offsetSign })
			{
				stackIndex = savedStubStack;
				if (lockedFitRef != null
					&& TryPickBestFlangeSideReferenceByMaxSpan(doc, dimView, view, val2, val3, val7, val4, val5, pipeCandidates, flangeOnFitSide: false, lockedFitRef, spoolSettings, ref stackIndex, out Reference bestFlangeRef, out _, out failureDetail, sign, lockOffsetSign: false, branchFacingDirection, dimensionPolicyRole)
					&& TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, lockedFitRef, bestFlangeRef, spoolSettings, ref stackIndex, out failureDetail, out placedDim, sign, lockOffsetSign: false, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
				{
					return true;
				}
			}
			stackIndex = savedStubStack;
			foreach (int sign in new[] { offsetSign, -offsetSign })
			{
				stackIndex = savedStubStack;
				Reference bestFitRef = null;
				Reference bestFlangeRef = null;
				double bestSpan = double.NegativeInfinity;
				for (int fitIdx = 0; fitIdx < Math.Min(fitCandidates.Count, 6); fitIdx++)
				{
					Reference refFit = fitCandidates[fitIdx];
					for (int pipeIdx = 0; pipeIdx < Math.Min(pipeCandidates.Count, 6); pipeIdx++)
					{
						Reference refPipe = pipeCandidates[pipeIdx];
						if (refDoc != null && AreSameDimensionReference(refDoc, refFit, refPipe))
						{
							continue;
						}
						int stackAttempt = stackIndex;
						if (!TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, refFit, refPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, sign, lockOffsetSign: false, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, logPlacement: false, witnessRoleA: fitRole, witnessRoleB: pipeRole))
						{
							stackIndex = stackAttempt;
							continue;
						}
						double committedValue;
						try
						{
							committedValue = placedDim?.Value ?? 0.0;
						}
						catch
						{
							committedValue = 0.0;
						}
						if (committedValue > bestSpan)
						{
							bestSpan = committedValue;
							bestFitRef = refFit;
							bestFlangeRef = refPipe;
						}
						try
						{
							if (placedDim != null)
							{
								doc.Delete(placedDim.Id);
							}
						}
						catch
						{
						}
						stackIndex = stackAttempt;
					}
				}
				if (bestFitRef != null && bestFlangeRef != null
					&& TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, bestFitRef, bestFlangeRef, spoolSettings, ref stackIndex, out failureDetail, out placedDim, sign, lockOffsetSign: false, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
				{
					return true;
				}
			}
			stackIndex = savedStubStack;
		}
		try
		{
			if (TestingReportsEnabled)
			{
				Document logDoc = view?.Document;
				if (logDoc != null && (elemNearFitting is FabricationPart || elemNearPipeEnd is FabricationPart))
				{
					string Stable(Reference reference)
					{
						try
						{
							return reference?.ConvertToStableRepresentation(logDoc) ?? "null";
						}
						catch
						{
							return "?";
						}
					}
					TryAppendAutoDimDiagnosticLog("placed-ref", view.Name, "dim-candidates fit=" + fitCandidates.Count + " [" + string.Join(", ", fitCandidates.Take(6).Select(Stable)) + "] pipe=" + pipeCandidates.Count + " [" + string.Join(", ", pipeCandidates.Take(6).Select(Stable)) + "]", 0, 0);
				}
			}
		}
		catch
		{
		}
		string text = null;
		double expectedSpan = val5.GetLength();
		// A flanged joint exposes two centerline snap points: the outer weld face and the flanged/raised face at the
		// bolted (gasket) joint. The break must land on the joint face, but the index-blessed connector references
		// cannot be probed up-front, and the first one that validates is usually the weld face - one flange-length
		// short of the joint.
		//
		// The OTHER ("anchor") side of a C-F / F-E is a fitting center or a pipe open end, and it must land on the
		// exact same reference the overall C-E dimension resolves to. The overall dim uses the FIRST candidate that
		// validates (e.g. the elbow's centerline-intersection). We must NOT just take the farthest reference: an elbow
		// also exposes outer-radius references beyond its centerline that would overshoot the true center.
		//
		// So do a PRECISE pass: lock the anchor to the first candidate that yields any valid dimension (same as the
		// overall dim), then among the flange candidates for that anchor keep the LARGEST validated span - the gasket
		// joint face is the innermost flange face, so it is farther than the weld face. If none commit, fall through
		// to the normal first-valid pass.
		bool preciseFlangeJoint = expectedSpan > 0.5 && (fitRole == FabricationDimensionRefRole.FlangeFace || pipeRole == FabricationDimensionRefRole.FlangeFace)
			&& !isFittingFlangeStubPolicy;
		bool bothFlangeFaces = fitRole == FabricationDimensionRefRole.FlangeFace && pipeRole == FabricationDimensionRefRole.FlangeFace;
		bool bothFittingCenters = fitRole == FabricationDimensionRefRole.RunStartFitting && pipeRole == FabricationDimensionRefRole.RunStartFitting;
		if (bothFittingCenters && expectedSpan > 0.5)
		{
			int stackIndexPreciseCenters = stackIndex;
			List<(Reference refFit, Reference refPipe, double axialMiss)> ranked = new List<(Reference, Reference, double)>();
			foreach (Reference refFit in fitCandidates)
			{
				foreach (Reference refPipe in pipeCandidates)
				{
					if (refDoc != null && AreSameDimensionReference(refDoc, refFit, refPipe))
					{
						continue;
					}
					double axialMiss = TryGetFabricationReferenceAxialDistanceToTarget(elemNearFitting, view, refFit, fittingTargetWorld, chordUnit)
						+ TryGetFabricationReferenceAxialDistanceToTarget(elemNearPipeEnd, view, refPipe, pipeEndTargetWorld, chordUnit);
					ranked.Add((refFit, refPipe, axialMiss));
				}
			}
			foreach ((Reference refFit, Reference refPipe, double axialMiss) pair in ranked.OrderBy((t) => t.axialMiss))
			{
				if (TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, pair.refFit, pair.refPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
				{
					return true;
				}
			}
			stackIndex = stackIndexPreciseCenters;
		}
		if (preciseFlangeJoint && bothFlangeFaces)
		{
			// True flange-face-to-flange-face overall: BOTH ends must land on their outboard raised face.
			// That is the reference pair with the LARGEST valid span across both flanges. The single-anchor
			// precise pass below only maximizes ONE flange (it locks the other to its first candidate, i.e. the
			// inner weld/joint face), which left the overall a full flange-face short (e.g. 5'-3 1/4" instead of
			// 5'-6 3/16"). Scan every fit x pipe candidate pair and keep the widest one that validates.
			int stackIndexPreciseBoth = stackIndex;
			Reference bestFit = null;
			Reference bestPipe = null;
			double bestSpanValue = double.NegativeInfinity;
			foreach (Reference refFit in fitCandidates)
			{
				foreach (Reference refPipe in pipeCandidates)
				{
					if (refDoc != null && AreSameDimensionReference(refDoc, refFit, refPipe))
					{
						continue;
					}
					int stackIndexAttempt = stackIndex;
					if (TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, refFit, refPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, logPlacement: false, witnessRoleA: fitRole, witnessRoleB: pipeRole))
					{
						double committedValue;
						try
						{
							committedValue = placedDim?.Value ?? 0.0;
						}
						catch
						{
							committedValue = 0.0;
						}
						if (committedValue > bestSpanValue)
						{
							bestSpanValue = committedValue;
							bestFit = refFit;
							bestPipe = refPipe;
						}
						// Measure-only trial: discard and re-commit the widest winner after the full scan.
						try
						{
							if (placedDim != null)
							{
								doc.Delete(placedDim.Id);
							}
						}
						catch
						{
						}
						placedDim = null;
						stackIndex = stackIndexAttempt;
					}
					else if (!string.IsNullOrEmpty(failureDetail))
					{
						text = failureDetail;
					}
				}
			}
			if (bestFit != null && bestPipe != null && TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, bestFit, bestPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
			{
				return true;
			}
			stackIndex = stackIndexPreciseBoth;
		}
		else if (preciseFlangeJoint)
		{
			bool fitIsFlange = fitRole == FabricationDimensionRefRole.FlangeFace;
			List<Reference> flangeSideRefs = fitIsFlange ? fitCandidates : pipeCandidates;
			List<Reference> otherSideRefs = fitIsFlange ? pipeCandidates : fitCandidates;
			FabricationDimensionRefRole otherRole = fitIsFlange ? pipeRole : fitRole;
			Element otherElement = fitIsFlange ? elemNearPipeEnd : elemNearFitting;
			XYZ otherTargetWorld = fitIsFlange ? pipeEndTargetWorld : fittingTargetWorld;
			bool otherWantsCenter = otherRole == FabricationDimensionRefRole.RunStartFitting || otherRole == FabricationDimensionRefRole.PipeCenterline;
			int stackIndexPrecise = stackIndex;
			if (otherWantsCenter)
			{
				Reference bestOtherRef = TryResolveFabricationCenterlineReference(otherElement, view, otherTargetWorld, chordUnit);
				if (bestOtherRef == null)
				{
					double bestAxial = double.MaxValue;
					foreach (Reference otherRef in otherSideRefs)
					{
						double axial = TryGetFabricationReferenceAxialDistanceToTarget(otherElement, view, otherRef, otherTargetWorld, chordUnit);
						if (axial < bestAxial)
						{
							bestAxial = axial;
							bestOtherRef = otherRef;
						}
					}
				}
				if (bestOtherRef != null
					&& TryPickBestFlangeSideReferenceByMaxSpan(doc, dimView, view, val2, val3, val7, val4, val5, flangeSideRefs, fitIsFlange, bestOtherRef, spoolSettings, ref stackIndex, out Reference bestFlangeRef, out _, out failureDetail, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole))
				{
					Reference refFit = fitIsFlange ? bestFlangeRef : bestOtherRef;
					Reference refPipe = fitIsFlange ? bestOtherRef : bestFlangeRef;
					if (TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, refFit, refPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
					{
						return true;
					}
				}
			}
			else
			{
				foreach (Reference otherRef in otherSideRefs)
				{
					if (!TryPickBestFlangeSideReferenceByMaxSpan(doc, dimView, view, val2, val3, val7, val4, val5, flangeSideRefs, fitIsFlange, otherRef, spoolSettings, ref stackIndex, out Reference bestFlangeRef, out _, out failureDetail, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole))
					{
						continue;
					}
					Reference refFit = fitIsFlange ? bestFlangeRef : otherRef;
					Reference refPipe = fitIsFlange ? otherRef : bestFlangeRef;
					if (TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, refFit, refPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
					{
						return true;
					}
				}
			}
			stackIndex = stackIndexPrecise;
		}
		bool singleCenterLock = expectedSpan > 0.5 && !bothFlangeFaces && !bothFittingCenters
			&& (fitRole == FabricationDimensionRefRole.RunStartFitting || pipeRole == FabricationDimensionRefRole.RunStartFitting);
		if (singleCenterLock)
		{
			int stackIndexCenterLock = stackIndex;
			bool fitIsCenter = fitRole == FabricationDimensionRefRole.RunStartFitting;
			Element centerElement = fitIsCenter ? elemNearFitting : elemNearPipeEnd;
			Element otherElement = fitIsCenter ? elemNearPipeEnd : elemNearFitting;
			XYZ centerTarget = fitIsCenter ? fittingTargetWorld : pipeEndTargetWorld;
			XYZ otherTarget = fitIsCenter ? pipeEndTargetWorld : fittingTargetWorld;
			FabricationDimensionRefRole otherRole = fitIsCenter ? pipeRole : fitRole;
			Reference centerRef = TryResolveFabricationCenterlineReference(centerElement, view, centerTarget, chordUnit);
			if (centerRef == null)
			{
				TryResolveFabricationAnchorReference(centerElement, view, centerTarget, FabricationDimensionRefRole.RunStartFitting, chordUnit, out centerRef);
			}
			if (centerRef != null && TryResolveFabricationAnchorReference(otherElement, view, otherTarget, otherRole, fitIsCenter ? chordUnit.Negate() : chordUnit, out Reference otherRef))
			{
				Reference refFit = fitIsCenter ? centerRef : otherRef;
				Reference refPipe = fitIsCenter ? otherRef : centerRef;
				if (TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, refFit, refPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
				{
					return true;
				}
			}
			stackIndex = stackIndexCenterLock;
		}
		if (elemNearFitting is FabricationPart && elemNearPipeEnd is FabricationPart
			&& !bothFlangeFaces && !preciseFlangeJoint)
		{
			int stackIndexLocked = stackIndex;
			if (TryResolveFabricationAnchorReference(elemNearFitting, view, fittingTargetWorld, fitRole, chordUnit, out Reference lockedFitRef)
				&& TryResolveFabricationAnchorReference(elemNearPipeEnd, view, pipeEndTargetWorld, pipeRole, chordUnit.Negate(), out Reference lockedPipeRef)
				&& (refDoc == null || !AreSameDimensionReference(refDoc, lockedFitRef, lockedPipeRef))
				&& TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, lockedFitRef, lockedPipeRef, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
			{
				return true;
			}
			stackIndex = stackIndexLocked;
		}
		int fitLimit = Math.Min(fitCandidates.Count, 3);
		int pipeLimit = Math.Min(pipeCandidates.Count, 3);
		for (int fitIdx = 0; fitIdx < fitLimit; fitIdx++)
		{
			Reference refFit = fitCandidates[fitIdx];
			for (int pipeIdx = 0; pipeIdx < pipeLimit; pipeIdx++)
			{
				Reference refPipe = pipeCandidates[pipeIdx];
				if (refDoc != null && AreSameDimensionReference(refDoc, refFit, refPipe))
				{
					continue;
				}
				if (TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, refFit, refPipe, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
				{
					return true;
				}
				if (!string.IsNullOrEmpty(failureDetail))
				{
					text = failureDetail;
					TryLogDimensionAttemptFailure(view, refFit, refPipe, failureDetail);
				}
			}
		}
		if (TryResolveSpoolDimensionReferences(view, elemNearFitting, elemNearPipeEnd, fittingTargetWorld, pipeEndTargetWorld, chordUnit, val7, fitRole, pipeRole, out var refFit2, out var refPipe2, out var nativeFailureDetail))
		{
			if (TryCommitSpoolLinearDimensionOffsetAttempts(doc, dimView, view, val2, val3, val7, val4, val5, refFit2, refPipe2, spoolSettings, ref stackIndex, out failureDetail, out placedDim, offsetSign, lockOffsetSign, branchFacingDirection, dimensionPolicyRole, stackDirectionInView, witnessRoleA: fitRole, witnessRoleB: pipeRole))
			{
				return true;
			}
		}
		failureDetail = (nativeFailureDetail ?? failureDetail ?? text ?? "Could not create a linear dimension from model geometry references.") + $" (fitting ref candidates={fitCandidates.Count}, pipe ref candidates={pipeCandidates.Count}).";
		return false;
	}

	private static bool TryPlaceSpoolLinearDimensionChainStyle(Document doc, View view, (Element element, XYZ targetWorld)[] anchors, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string failureDetail)
	{
		failureDetail = null;
		if (doc == null || view == null || anchors == null || anchors.Length < 3)
		{
			failureDetail = "Missing document, view, or chain anchors.";
			return false;
		}
		for (int i = 0; i < anchors.Length; i++)
		{
			if (anchors[i].element == null || anchors[i].targetWorld == null)
			{
				failureDetail = "One or more chain anchors are missing element or point data.";
				return false;
			}
		}
		View dimView = view;
		if (!TryGetViewSketchPlane(view, out var planeOrigin, out var planeNormal))
		{
			failureDetail = "Could not resolve view sketch plane.";
			return false;
		}
		XYZ val = anchors[anchors.Length - 1].targetWorld - anchors[0].targetWorld;
		if (val == null || val.GetLength() < 1E-09)
		{
			failureDetail = "Chain endpoints coincide.";
			return false;
		}
		XYZ chordUnit = val.Normalize();
		XYZ[] array = anchors.Select(((Element element, XYZ targetWorld) a) => ProjectToSketchPlane(a.targetWorld, planeOrigin, planeNormal)).ToArray();
		XYZ val2 = planeNormal;
		val2 = val2.Normalize();
		XYZ val3 = array[array.Length - 1] - array[0];
		XYZ val4 = val3 - val2 * val3.DotProduct(val2);
		if (val4.GetLength() < 1E-09)
		{
			failureDetail = "Chain run direction projects to a point in the view plane.";
			return false;
		}
		val4 = val4.Normalize();
		XYZ val5 = val2.CrossProduct(val4);
		if (val5.GetLength() < 1E-09)
		{
			XYZ upDirection = view.UpDirection;
			if (upDirection == null || upDirection.GetLength() < 1E-09)
			{
				failureDetail = "Could not build a perpendicular offset direction.";
				return false;
			}
			val5 = upDirection - val2 * upDirection.DotProduct(val2);
			if (val5.GetLength() < 1E-09)
			{
				failureDetail = "View up direction is degenerate in-plane.";
				return false;
			}
		}
		val5 = val5.Normalize();
		Reference[] array2 = new Reference[anchors.Length];
		for (int num = 0; num < anchors.Length; num++)
		{
			XYZ capHint = ((num == 0) ? chordUnit.Negate() : ((num == anchors.Length - 1) ? chordUnit : null));
			if (!TryGetDimensionReferenceAtWorldPoint(anchors[num].element, view, anchors[num].targetWorld, capHint ?? chordUnit, val5, out array2[num], out var _))
			{
				array2 = null;
				break;
			}
		}
		if (array2 != null && TryCommitSpoolChainDimensionOffsetAttempts(doc, dimView, view, array[0], array[array.Length - 1], val5, val2, val3, array2, spoolSettings, ref stackIndex, out failureDetail))
		{
			return true;
		}
		failureDetail = failureDetail ?? "Could not resolve dimension references for one or more chain anchors.";
		return false;
	}

	private static bool TryCommitSpoolChainDimensionOffsetAttempts(Document doc, View dimView, View scaleView, XYZ pStartP, XYZ pEndP, XYZ perp, XYZ vn, XYZ chord, Reference[] references, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string failureDetail)
	{
		failureDetail = null;
		if (doc == null || dimView == null || scaleView == null || pStartP == null || pEndP == null || perp == null || vn == null || chord == null || references == null || references.Length < 3)
		{
			failureDetail = "Missing inputs for chain dimension commit.";
			return false;
		}
		if (!TryGetViewLinearDimensionAxes(scaleView, pStartP, pEndP, out var _, out var offsetAxis))
		{
			failureDetail = "Could not resolve horizontal/vertical linear dimension axes for chain dimension.";
			return false;
		}
		DimensionType dimType = TryResolveLinearDimensionType(doc, spoolSettings);
		bool isHorizontalMeasurement = IsViewHorizontalMeasurement(scaleView, chord);
		double num3 = ResolveSpoolLinearDimensionModelOffset(scaleView, dimType, stackIndex, isHorizontalMeasurement, spoolSettings?.AutoDimAnnotations == true);
		int preferredOffsetSign = ResolveSpoolDimensionPlacementOffsetSign(scaleView, chord, 1);
		ReferenceArray val = new ReferenceArray();
		Reference[] array = references;
		foreach (Reference item in array)
		{
			val.Append(item);
		}
		ReferenceArray val2 = new ReferenceArray();
		for (int num4 = references.Length - 1; num4 >= 0; num4--)
		{
			val2.Append(references[num4]);
		}
		string text = null;
		int[] array2 = new int[2] { preferredOffsetSign, -preferredOffsetSign };
		foreach (int num5 in array2)
		{
			AlignAnchorsForStackedLinearDimension(pStartP, pEndP, offsetAxis, out var alignedStartP, out var alignedEndP);
			double num6 = num3 * (double)num5;
			string errorMessage = null;
			string rejectReason = null;
			if (TryBuildViewLinearDimensionLine(scaleView, alignedStartP, alignedEndP, num6, out var val6) && TryIsViewLinearDimensionLine(scaleView, val6) && TryCommitNewDimension(doc, dimView, val6, val, val2, dimType, out var dim, out errorMessage) && TryValidateCreatedDimension(dim, dimView, out rejectReason, -1.0, chord, val6))
			{
				TryUnhideSpoolDimensionInView(dimView, dim);
				TryApplySpoolAutoDimensionBelowLabel(doc, dimView, dim, spoolSettings);
				stackIndex++;
				return true;
			}
			if (!string.IsNullOrEmpty(rejectReason ?? errorMessage))
			{
				text = rejectReason ?? errorMessage;
			}
		}
		failureDetail = (string.IsNullOrEmpty(text) ? "NewDimension returned null or failed after offset attempts." : ("Revit: " + text));
		return false;
	}

	private static bool TryGetViewSketchPlane(View view, out XYZ planeOrigin, out XYZ planeNormal)
	{
		planeOrigin = null;
		planeNormal = null;
		if (view == null)
		{
			return false;
		}
		try
		{
			if (view.SketchPlane != null)
			{
				Plane plane = view.SketchPlane.GetPlane();
				if (plane != null)
				{
					planeNormal = plane.Normal;
					if (planeNormal != null && planeNormal.GetLength() > 1E-09)
					{
						planeNormal = planeNormal.Normalize();
						planeOrigin = plane.Origin;
						return planeOrigin != null;
					}
				}
			}
		}
		catch
		{
		}
		try
		{
			planeNormal = view.ViewDirection;
			if (planeNormal != null && planeNormal.GetLength() > 1E-09)
			{
				planeNormal = planeNormal.Normalize();
				planeOrigin = view.Origin;
				return planeOrigin != null;
			}
		}
		catch
		{
		}
		return false;
	}

	private static XYZ ProjectToSketchPlane(XYZ point, XYZ planeOrigin, XYZ unitNormal)
	{
		if (point == null || planeOrigin == null || unitNormal == null)
		{
			return point;
		}
		double num = (point - planeOrigin).DotProduct(unitNormal);
		return point - unitNormal.Multiply(num);
	}

	/// <summary>
	/// Sheet H/V Linear that stays H/V when the user moves it.
	/// Two free corner refs alone make Revit snap the dim onto the chord (10⅞″ @ 45°) on touch.
	/// Autodesk workaround: include a temporary DetailCurve ref along the measure axis, create the
	/// Linear dim, delete the helper, regenerate, and verify every remaining ref still resolves.
	/// No pin. Helper is never left on the sheet.
	/// </summary>
	private static bool TryCommitDragStableSheetLinearDimension(
		Document doc,
		View dimView,
		Line dimLine,
		ReferenceArray primaryOrder,
		ReferenceArray swappedOrder,
		DimensionType dimType,
		XYZ measureAxis,
		out Dimension dim,
		out string errorMessage)
	{
		dim = null;
		errorMessage = null;
		if (doc == null || dimView == null || dimLine == null || primaryOrder == null || swappedOrder == null
			|| measureAxis == null || measureAxis.GetLength() < 1E-09)
		{
			return TryCommitNewDimension(doc, dimView, dimLine, primaryOrder, swappedOrder, dimType, out dim, out errorMessage);
		}

		if (!IsDimensionDirectionViewAxisAligned(dimView, dimLine.Direction))
		{
			errorMessage = "Intended dimension line is not sheet-horizontal/vertical.";
			return false;
		}

		if (dimType == null)
		{
			dimType = TryGetLinearDimensionTypeDefault(doc);
		}
		if (!IsLinearDimensionType(dimType))
		{
			errorMessage = "No linear dimension type available.";
			return false;
		}

		if (!TryCreateSheetAxisLockDetailCurve(doc, dimView, dimLine, measureAxis, out DetailCurve lockCurve, out Reference lockRef)
			|| lockCurve == null || lockRef == null)
		{
			return TryCommitNewDimension(doc, dimView, dimLine, primaryOrder, swappedOrder, dimType, out dim, out errorMessage);
		}

		ElementId lockId = ((Element)lockCurve).Id;
		ReferenceArray[] orders =
		{
			PrependDimensionReference(lockRef, primaryOrder),
			PrependDimensionReference(lockRef, swappedOrder)
		};

		string lastError = null;
		foreach (ReferenceArray order in orders)
		{
			Dimension created = null;
			try
			{
				created = doc.Create.NewDimension(dimView, dimLine, order, dimType);
			}
			catch (Exception ex)
			{
				lastError = ex.Message;
				created = null;
			}

			if (created == null)
			{
				continue;
			}

			try
			{
				if (created.DimensionType == null || !IsLinearDimensionType(created.DimensionType))
				{
					created.DimensionType = dimType;
				}
			}
			catch
			{
			}

			// Remove helper BEFORE regen so commit never sees a dead third reference.
			try
			{
				if (doc.GetElement(lockId) != null)
				{
					doc.Delete(lockId);
				}
			}
			catch
			{
			}
			lockCurve = null;

			try { doc.Regenerate(); } catch { }

			if (!IsDragStableSheetLinearDimensionValid(doc, dimView, created, dimLine, out lastError))
			{
				try
				{
					if (created.IsValidObject)
					{
						doc.Delete(((Element)created).Id);
					}
				}
				catch
				{
				}
				created = null;
				continue;
			}

			TryUnhideSpoolDimensionInView(dimView, created);
			dim = created;
			return true;
		}

		// Lock curve may still exist if both orders failed before delete.
		try
		{
			if (doc.GetElement(lockId) != null)
			{
				doc.Delete(lockId);
			}
		}
		catch
		{
		}

		errorMessage = lastError ?? "Drag-stable linear dimension failed.";
		// Last resort: plain linear (may reorient on drag) — better than no dim.
		return TryCommitNewDimension(doc, dimView, dimLine, primaryOrder, swappedOrder, dimType, out dim, out errorMessage);
	}

	private static bool IsDragStableSheetLinearDimensionValid(
		Document doc,
		View dimView,
		Dimension dim,
		Line intendedDimLine,
		out string errorMessage)
	{
		errorMessage = null;
		if (doc == null || dim == null || !dim.IsValidObject)
		{
			errorMessage = "Dimension invalid after axis-lock helper removal.";
			return false;
		}

		try
		{
			if (dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
			{
				errorMessage = "Dimension is not Linear after axis-lock helper removal.";
				return false;
			}
		}
		catch
		{
			errorMessage = "Could not read dimension type after axis-lock helper removal.";
			return false;
		}

		try
		{
			ReferenceArray refs = dim.References;
			if (refs == null || refs.Size < 2)
			{
				errorMessage = "Dimension lost its fabrication references.";
				return false;
			}

			for (int i = 0; i < refs.Size; i++)
			{
				Reference r = refs.get_Item(i);
				if (r == null || doc.GetElement(r.ElementId) == null)
				{
					errorMessage = "Dimension has an invalid/deleted reference.";
					return false;
				}
			}
		}
		catch
		{
			errorMessage = "Could not validate dimension references.";
			return false;
		}

		if (!TryRejectRevitAlignedDimensionCurve(dim, dimView, intendedDimLine, out errorMessage)
			|| !IsDimensionDirectionViewAxisAligned(dimView, TryReadDimensionCurveDirection(dim)))
		{
			if (string.IsNullOrEmpty(errorMessage))
			{
				errorMessage = "Dimension curve is not sheet-axis aligned.";
			}
			return false;
		}

		return true;
	}

	private static bool TryCreateSheetAxisLockDetailCurve(
		Document doc,
		View dimView,
		Line dimLine,
		XYZ measureAxis,
		out DetailCurve lockCurve,
		out Reference lockRef)
	{
		lockCurve = null;
		lockRef = null;
		if (doc == null || dimView == null || dimLine == null || measureAxis == null)
		{
			return false;
		}

		try
		{
			measureAxis = measureAxis.Normalize();
			XYZ origin = null;
			try { origin = dimLine.Origin; } catch { origin = null; }
			if (origin == null)
			{
				try
				{
					if (dimLine.IsBound)
					{
						origin = (dimLine.GetEndPoint(0) + dimLine.GetEndPoint(1)) * 0.5;
					}
				}
				catch
				{
					origin = null;
				}
			}
			if (origin == null)
			{
				return false;
			}

			const double halfFeet = 0.25;
			Line dummyLine = Line.CreateBound(
				origin - measureAxis.Multiply(halfFeet),
				origin + measureAxis.Multiply(halfFeet));
			CurveElement created = doc.Create.NewDetailCurve(dimView, dummyLine);
			lockCurve = created as DetailCurve;
			if (lockCurve?.GeometryCurve == null)
			{
				try { if (created != null) doc.Delete(created.Id); } catch { }
				lockCurve = null;
				return false;
			}

			lockRef = lockCurve.GeometryCurve.Reference;
			return lockRef != null;
		}
		catch
		{
			lockCurve = null;
			lockRef = null;
			return false;
		}
	}

	private static ReferenceArray PrependDimensionReference(Reference head, ReferenceArray tail)
	{
		ReferenceArray result = new ReferenceArray();
		if (head != null)
		{
			result.Append(head);
		}
		if (tail != null)
		{
			int n = tail.Size;
			for (int i = 0; i < n; i++)
			{
				Reference r = tail.get_Item(i);
				if (r != null)
				{
					result.Append(r);
				}
			}
		}
		return result;
	}

	private static bool TryCommitNewDimension(Document doc, View dimView, Line dimLine, ReferenceArray primaryOrder, ReferenceArray swappedOrder, DimensionType dimType, out Dimension dim, out string errorMessage)
	{
		dim = null;
		errorMessage = null;
		if (doc == null || dimView == null || (GeometryObject)(object)dimLine == (GeometryObject)null || primaryOrder == null || swappedOrder == null)
		{
			return false;
		}
		// Hard law: intended line must already be sheet H/V before NewDimension.
		if (!IsDimensionDirectionViewAxisAligned(dimView, dimLine.Direction))
		{
			errorMessage = "Intended dimension line is not sheet-horizontal/vertical.";
			return false;
		}
		if (dimType == null)
		{
			dimType = TryGetLinearDimensionTypeDefault(doc);
		}
		if (!IsLinearDimensionType(dimType))
		{
			errorMessage = "No linear dimension type available.";
			return false;
		}
		ReferenceArray[] orders = new ReferenceArray[2] { primaryOrder, swappedOrder };
		foreach (ReferenceArray order in orders)
		{
			try
			{
				dim = doc.Create.NewDimension(dimView, dimLine, order, dimType);
			}
			catch (Exception ex)
			{
				dim = null;
				errorMessage = ex.Message;
			}
			if (dim == null)
			{
				continue;
			}
			try
			{
				if (dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
				{
					dim.DimensionType = dimType;
				}
			}
			catch
			{
			}
			if (dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
			{
				try { doc.Delete(dim.Id); } catch { }
				dim = null;
				errorMessage = "Dimension type is not linear after creation.";
				continue;
			}

			// Regen then fail-closed on any non-sheet-axis curve (diagonal Linear = true-length tilt).
			try { doc.Regenerate(); } catch { }
			if (!TryRejectRevitAlignedDimensionCurve(dim, dimView, dimLine, out errorMessage)
				|| !IsDimensionDirectionViewAxisAligned(dimView, TryReadDimensionCurveDirection(dim)))
			{
				try { if (dim.IsValidObject) doc.Delete(dim.Id); } catch { }
				dim = null;
				if (string.IsNullOrEmpty(errorMessage))
				{
					errorMessage = "Revit created a non-sheet-axis dimension; rejecting tilted placement.";
				}
				continue;
			}

			TryUnhideSpoolDimensionInView(dimView, dim);
			return true;
		}
		return false;
	}

	private static XYZ TryReadDimensionCurveDirection(Dimension dim)
	{
		if (dim == null)
		{
			return null;
		}
		try
		{
			if (dim.Curve is Line line)
			{
				XYZ dir = null;
				try { dir = line.Direction; } catch { dir = null; }
				if (dir != null && dir.GetLength() > 1E-09)
				{
					return dir;
				}
				if (line.IsBound)
				{
					return line.GetEndPoint(1) - line.GetEndPoint(0);
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static bool TryRejectRevitAlignedDimensionCurve(Dimension dim, View dimView, Line intendedDimLine, out string errorMessage)
	{
		errorMessage = null;
		if (dim == null || dimView == null)
		{
			return true;
		}
		try
		{
			// Revit Linear dims often return an UNBOUND Line — use Direction, not GetEndPoint.
			if (dim.Curve is Line actual)
			{
				XYZ dir = null;
				try { dir = actual.Direction; } catch { dir = null; }
				if (dir == null || dir.GetLength() < 1E-09)
				{
					try
					{
						if (actual.IsBound)
						{
							dir = actual.GetEndPoint(1) - actual.GetEndPoint(0);
						}
					}
					catch { }
				}
				if (dir != null && dir.GetLength() > 1E-09
					&& !IsDimensionDirectionViewAxisAligned(dimView, dir))
				{
					errorMessage = "Revit created a non-axis-aligned dimension curve; rejecting aligned/tilted placement.";
					try { dim.Document.Delete(dim.Id); } catch { }
					return false;
				}
			}
		}
		catch
		{
		}
		if (intendedDimLine != null)
		{
			XYZ intendedDir = null;
			try { intendedDir = intendedDimLine.Direction; } catch { }
			if (intendedDir != null && intendedDir.GetLength() > 1E-09
				&& !IsDimensionDirectionViewAxisAligned(dimView, intendedDir))
			{
				errorMessage = "Intended dimension line is not axis-aligned in the view.";
				try { dim.Document.Delete(dim.Id); } catch { }
				return false;
			}
		}
		return true;
	}

	/// <summary>True when direction is parallel to view Right or Up (dot ≥ 0.9999 ≈ 0.8°).</summary>
	private static bool IsDimensionDirectionViewAxisAligned(View view, XYZ direction)
	{
		if (view == null || direction == null || direction.GetLength() < 1E-09)
		{
			return false;
		}
		if (!TryGetViewPlaneAxes(view, out XYZ viewNormal, out XYZ right, out XYZ up))
		{
			return false;
		}
		XYZ inPlane = ProjectVectorToViewPlane(direction, viewNormal);
		if (inPlane == null || inPlane.GetLength() < 1E-09)
		{
			return false;
		}
		inPlane = inPlane.Normalize();
		const double minDot = 0.9999;
		return Math.Abs(inPlane.DotProduct(right)) >= minDot
			|| Math.Abs(inPlane.DotProduct(up)) >= minDot;
	}

	private static bool TryValidateCreatedDimensionOrientation(View dimView, Line dimLine, XYZ measurementChord, out string errorMessage)
	{
		errorMessage = null;
		if (dimView == null || dimLine == null)
		{
			return true;
		}
		if (!TryIsViewLinearDimensionLine(dimView, dimLine))
		{
			errorMessage = "Created dimension line is not horizontal/vertical in the view.";
			return false;
		}
		return true;
	}

	private static bool TryValidateCreatedDimension(Dimension dim, View dimView, out string errorMessage, double expectedSpanFeet = -1.0, XYZ measurementChord = null, Line intendedDimLine = null)
	{
		errorMessage = null;
		if (dim == null)
		{
			errorMessage = "Revit returned a null dimension.";
			return false;
		}
		try
		{
			if (!dim.IsValidObject)
			{
				errorMessage = "Revit returned an invalid dimension object.";
				return false;
			}
		}
		catch
		{
		}
		try
		{
			if (dimView != null && dim.OwnerViewId != ((Element)dimView).Id)
			{
				errorMessage = "Dimension was created in a different view than the target assembly elevation.";
				return false;
			}
		}
		catch
		{
		}
		try
		{
			if (dim.DimensionType == null || !IsLinearDimensionType(dim.DimensionType))
			{
				errorMessage = "Dimension type is not linear; rejecting non-linear placement.";
				try
				{
					dim.Document.Delete(dim.Id);
				}
				catch
				{
				}
				return false;
			}
		}
		catch
		{
		}
		Line orientationLine = intendedDimLine;
		if (orientationLine != null)
		{
			try
			{
				if (!orientationLine.IsBound)
				{
					orientationLine = null;
				}
			}
			catch
			{
				orientationLine = null;
			}
		}
		if (orientationLine != null && !TryValidateCreatedDimensionOrientation(dimView, orientationLine, measurementChord, out errorMessage))
		{
			try
			{
				dim.Document.Delete(dim.Id);
			}
			catch
			{
			}
			return false;
		}
		if (!TryRejectRevitAlignedDimensionCurve(dim, dimView, orientationLine, out errorMessage))
		{
			return false;
		}
		double dimValue;
		string valueString;
		try
		{
			dimValue = dim.Value ?? 0.0;
			valueString = (dim.ValueString ?? string.Empty).Trim();
		}
		catch (Exception ex)
		{
			errorMessage = "Could not read dimension value: " + ex.Message;
			try
			{
				dim.Document.Delete(dim.Id);
			}
			catch
			{
			}
			return false;
		}
		if (string.Equals(valueString, "0\"", StringComparison.OrdinalIgnoreCase) || string.Equals(valueString, "0", StringComparison.OrdinalIgnoreCase))
		{
			errorMessage = "Zero-length dimension (value=" + valueString + ").";
			try
			{
				dim.Document.Delete(dim.Id);
			}
			catch
			{
			}
			return false;
		}
		if (dimValue < 1.0 / 96.0)
		{
			errorMessage = "Zero-length dimension (value=" + (dim.ValueString ?? "?") + ").";
			try
			{
				dim.Document.Delete(dim.Id);
			}
			catch
			{
			}
			return false;
		}
		// Long spans (e.g. an overall run C-E) must land close to the intended anchor span; a loose 0.5x window
		// lets the resolver grab a wrong mid-run reference (e.g. the near end of the open-end pipe). Tighten the
		// lower bound as the span grows so a clearly-wrong reference is rejected and the correct end is chosen.
		double lowerBoundFactor = (expectedSpanFeet >= 6.0) ? 0.75 : 0.5;
		if (expectedSpanFeet > 1.0 / 12.0 && dimValue < expectedSpanFeet * lowerBoundFactor)
		{
			errorMessage = "Dimension value (" + (dim.ValueString ?? "?") + ") is much shorter than the anchor span (~" + expectedSpanFeet.ToString("F1") + " ft).";
			try
			{
				dim.Document.Delete(dim.Id);
			}
			catch
			{
			}
			return false;
		}
		if (expectedSpanFeet > 1.0 / 12.0 && dimValue > expectedSpanFeet * 1.35)
		{
			errorMessage = "Dimension value (" + (dim.ValueString ?? "?") + ") is much longer than the anchor span (~" + expectedSpanFeet.ToString("F1") + " ft).";
			try
			{
				dim.Document.Delete(dim.Id);
			}
			catch
			{
			}
			return false;
		}
		// Pick-up and branch dims (under ~3 ft) must land within a few inches of the intent span; a loose window
		// lets Revit snap to a tee port face or weld neck instead of the centerline or raised flange face.
		if (expectedSpanFeet > 1.0 / 12.0 && expectedSpanFeet < 3.0 && Math.Abs(dimValue - expectedSpanFeet) > 4.0 / 12.0)
		{
			errorMessage = "Dimension value (" + (dim.ValueString ?? "?") + ") differs from the anchor span (~" + expectedSpanFeet.ToString("F2") + " ft) by more than 4\".";
			try
			{
				dim.Document.Delete(dim.Id);
			}
			catch
			{
			}
			return false;
		}
		return true;
	}

	private static bool TryIsViewLinearDimensionLine(View view, Line dimLine)
	{
		if (view == null || dimLine == null)
		{
			return false;
		}
		XYZ direction = null;
		try
		{
			direction = dimLine.Direction;
		}
		catch
		{
			direction = null;
		}
		if (direction == null || direction.GetLength() < 1E-09)
		{
			try
			{
				if (dimLine.IsBound)
				{
					direction = dimLine.GetEndPoint(1) - dimLine.GetEndPoint(0);
				}
			}
			catch
			{
				return false;
			}
		}
		return IsDimensionDirectionViewAxisAligned(view, direction);
	}

	private static bool IsDimensionLineAlignedToSkewedChord(View view, Line dimLine, XYZ chord)
	{
		if (view == null || dimLine == null || chord == null || chord.GetLength() < 1E-09)
		{
			return false;
		}
		try
		{
			if (!dimLine.IsBound)
			{
				return false;
			}
		}
		catch
		{
			return false;
		}
		if (!TryGetViewPlaneAxes(view, out var viewNormal, out var right, out var up))
		{
			return false;
		}
		XYZ dimDirection;
		try
		{
			dimDirection = dimLine.GetEndPoint(1) - dimLine.GetEndPoint(0);
		}
		catch
		{
			return false;
		}
		XYZ chordInPlane = ProjectVectorToViewPlane(chord, viewNormal);
		if (dimDirection.GetLength() < 1E-09 || chordInPlane.GetLength() < 1E-09)
		{
			return false;
		}
		dimDirection = dimDirection.Normalize();
		chordInPlane = chordInPlane.Normalize();
		bool chordIsViewAxisAligned = Math.Abs(Math.Abs(chordInPlane.DotProduct(right)) - 1.0) < 1E-03 || Math.Abs(Math.Abs(chordInPlane.DotProduct(up)) - 1.0) < 1E-03;
		if (chordIsViewAxisAligned)
		{
			return false;
		}
		return Math.Abs(dimDirection.DotProduct(chordInPlane)) > 0.995;
	}

	private static bool TryCommitSpoolLinearDimensionOffsetAttempts(Document doc, View dimView, View scaleView, XYZ pFitP, XYZ pPipeP, XYZ perp, XYZ vn, XYZ chord, Reference refFit, Reference refPipe, SpoolingManagerSettings spoolSettings, ref int stackIndex, out string failureDetail, out Dimension createdDim, int offsetSign = 1, bool lockOffsetSign = false, XYZ branchFacing = null, string dimensionPolicyRole = null, XYZ stackDirectionInView = null, bool logPlacement = true, FabricationDimensionRefRole? witnessRoleA = null, FabricationDimensionRefRole? witnessRoleB = null)
	{
		failureDetail = null;
		createdDim = null;
		if (doc == null || dimView == null || scaleView == null || pFitP == null || pPipeP == null || perp == null || vn == null || chord == null || refFit == null || refPipe == null)
		{
			failureDetail = "Missing inputs for dimension commit.";
			return false;
		}
		DimensionType dimType = TryResolveLinearDimensionType(doc, spoolSettings);
		// When stackDirection is provided, measurement axis is perpendicular to stack (not chord-inferred).
		bool isHorizontalMeasurement;
		if (stackDirectionInView != null && stackDirectionInView.GetLength() > 1E-09
			&& TryGetViewPlaneAxes(scaleView, out _, out XYZ rightAxis, out XYZ upAxis))
		{
			XYZ stackInPlane = ProjectVectorToViewPlane(stackDirectionInView, vn) ?? stackDirectionInView;
			isHorizontalMeasurement = Math.Abs(stackInPlane.DotProduct(upAxis)) >= Math.Abs(stackInPlane.DotProduct(rightAxis));
		}
		else
		{
			isHorizontalMeasurement = IsViewHorizontalMeasurement(scaleView, chord);
		}
		// Expected span is the PROJECTED H or V separation — never the diagonal chord length.
		double expectedSpanFeet = ResolveViewAxisProjectedSpanFeet(scaleView, pFitP, pPipeP, isHorizontalMeasurement);
		double num3 = ResolveSpoolLinearDimensionModelOffset(scaleView, dimType, stackIndex, isHorizontalMeasurement, spoolSettings?.AutoDimAnnotations == true);
		int preferredOffsetSign = ResolveSpoolDimensionPlacementOffsetSign(scaleView, chord, offsetSign, branchFacing, vn);
		ReferenceArray val = new ReferenceArray();
		val.Append(refFit);
		val.Append(refPipe);
		ReferenceArray val2 = new ReferenceArray();
		val2.Append(refPipe);
		val2.Append(refFit);
		string text = null;
		if (!TryGetViewLinearDimensionAxes(scaleView, pFitP, pPipeP, out var _, out var primaryOffsetAxis))
		{
			failureDetail = "Could not resolve horizontal/vertical linear dimension axes.";
			return false;
		}
		XYZ fitP = pFitP;
		XYZ pipeP = pPipeP;
		if (TryGetViewSketchPlane(scaleView, out var planeOrigin, out var planeNormal))
		{
			// Always coerce onto a shared H or V station line — required even when stackDirection is set.
			CoerceAnchorPointsForViewLinearDimension(scaleView, planeOrigin, planeNormal, ref fitP, ref pipeP, preferredOffsetSign, branchFacing);
		}
		AlignAnchorsForStackedLinearDimension(fitP, pipeP, primaryOffsetAxis, out var alignedFitP, out var alignedPipeP);
		if (stackDirectionInView != null && stackDirectionInView.GetLength() > 1E-09)
		{
			string errorMessage = null;
			string rejectReason = null;
			Dimension dim = null;
			double signedOffset = num3 * (double)(preferredOffsetSign == 0 ? 1 : Math.Sign(preferredOffsetSign));
			// Stack builder takes absolute distance; fold sign into stack direction.
			XYZ signedStack = stackDirectionInView.Normalize().Multiply(preferredOffsetSign < 0 ? -1.0 : 1.0);
			if (TryBuildStackedLinearDimensionLine(scaleView, alignedFitP, alignedPipeP, Math.Abs(signedOffset), signedStack, out var dimLine)
				&& TryIsViewLinearDimensionLine(scaleView, dimLine)
				&& TryCommitNewDimension(doc, dimView, dimLine, val, val2, dimType, out dim, out errorMessage)
				&& TryValidateCreatedDimension(dim, dimView, out rejectReason, expectedSpanFeet, chord, dimLine))
			{
				createdDim = dim;
				TryUnhideSpoolDimensionInView(dimView, dim);
				if (logPlacement)
				{
					TryLogPlacedDimensionReferences(dimView, dim, refFit, refPipe);
				}
				TryApplySpoolAutoDimensionBelowLabel(doc, dimView, dim, spoolSettings, witnessRoleA, witnessRoleB);
				stackIndex++;
				return true;
			}
			if (!string.IsNullOrEmpty(rejectReason ?? errorMessage))
			{
				text = rejectReason ?? errorMessage;
			}
		}
		int[] signOrder = lockOffsetSign
			? new int[1] { preferredOffsetSign }
			: new int[2] { preferredOffsetSign, -preferredOffsetSign };
		signOrder = signOrder.Distinct().ToArray();
		foreach (int placementSign in signOrder)
		{
			double offsetSigned = num3 * (double)placementSign;
			string errorMessage = null;
			string rejectReason = null;
			Dimension dim = null;
			if (TryBuildViewLinearDimensionLine(scaleView, alignedFitP, alignedPipeP, offsetSigned, out var dimLine, branchFacing) && TryIsViewLinearDimensionLine(scaleView, dimLine) && TryCommitNewDimension(doc, dimView, dimLine, val, val2, dimType, out dim, out errorMessage) && TryValidateCreatedDimension(dim, dimView, out rejectReason, expectedSpanFeet, chord, dimLine))
			{
				createdDim = dim;
				TryUnhideSpoolDimensionInView(dimView, dim);
				if (logPlacement)
				{
					TryLogPlacedDimensionReferences(dimView, dim, refFit, refPipe);
				}
				TryApplySpoolAutoDimensionBelowLabel(doc, dimView, dim, spoolSettings, witnessRoleA, witnessRoleB);
				stackIndex++;
				return true;
			}
			if (!string.IsNullOrEmpty(rejectReason ?? errorMessage))
			{
				text = rejectReason ?? errorMessage;
			}
		}
		failureDetail = (string.IsNullOrEmpty(text) ? "Could not place a view-linear dimension (horizontal/vertical only)." : text);
		return false;
	}

	/// <summary>Projected horizontal or vertical separation in the active view (feet).</summary>
	private static double ResolveViewAxisProjectedSpanFeet(View view, XYZ a, XYZ b, bool horizontalMeasurement)
	{
		if (view == null || a == null || b == null || !TryGetViewPlaneAxes(view, out XYZ viewNormal, out XYZ right, out XYZ up))
		{
			return a != null && b != null ? a.DistanceTo(b) : 0.0;
		}

		XYZ delta = ProjectVectorToViewPlane(b - a, viewNormal) ?? (b - a);
		return horizontalMeasurement
			? Math.Abs(delta.DotProduct(right))
			: Math.Abs(delta.DotProduct(up));
	}

	private static IEnumerable<Options> BuildGeometryOptionsForReferenceExtraction(View cutView)
	{
		if (cutView != null)
		{
			bool[] includeFlags = new bool[2] { true, false };
			foreach (bool includeNonVisibleObjects in includeFlags)
			{
				yield return new Options
				{
					ComputeReferences = true,
					IncludeNonVisibleObjects = includeNonVisibleObjects,
					View = cutView
				};
			}
		}
		foreach (Options item in BuildModelGeometryOptionsForReferenceExtraction())
		{
			yield return item;
		}
	}

	private static IEnumerable<Options> BuildModelGeometryOptionsForReferenceExtraction()
	{
		ViewDetailLevel[] array = new ViewDetailLevel[3]
		{
			ViewDetailLevel.Fine,
			ViewDetailLevel.Medium,
			ViewDetailLevel.Coarse
		};
		ViewDetailLevel[] array2 = array;
		ViewDetailLevel[] array3 = array2;
		foreach (ViewDetailLevel dl in array3)
		{
			bool[] array4 = new bool[2] { true, false };
			foreach (bool includeNonVisibleObjects in array4)
			{
				yield return new Options
				{
					ComputeReferences = true,
					IncludeNonVisibleObjects = includeNonVisibleObjects,
					DetailLevel = dl
				};
			}
		}
	}

	private static bool TryResolveSpoolDimensionReferences(View view, Element elemNearFitting, Element elemNearPipeEnd, XYZ fittingTargetWorld, XYZ pipeEndTargetWorld, XYZ chordUnit, XYZ dimensionLineDirectionWorld, FabricationDimensionRefRole fitRole, FabricationDimensionRefRole pipeRole, out Reference refFit, out Reference refPipe, out string failureDetail)
	{
		refFit = null;
		refPipe = null;
		failureDetail = null;
		try
		{
			bool flag3 = elemNearFitting is FabricationPart
				? TryResolveFabricationAnchorReference(elemNearFitting, view, fittingTargetWorld, fitRole, chordUnit, out refFit)
				: TryGetDimensionReferenceAtWorldPoint(elemNearFitting, view, fittingTargetWorld, chordUnit, dimensionLineDirectionWorld, out refFit, out var _);
			bool flag4 = elemNearPipeEnd is FabricationPart
				? TryResolveFabricationAnchorReference(elemNearPipeEnd, view, pipeEndTargetWorld, pipeRole, chordUnit.Negate(), out refPipe)
				: TryGetDimensionReferenceAtWorldPoint(elemNearPipeEnd, view, pipeEndTargetWorld, chordUnit.Negate(), dimensionLineDirectionWorld, out refPipe, out var _);
			if (flag3 && flag4 && refFit != null && refPipe != null)
			{
				return true;
			}
			refFit = null;
			refPipe = null;
			List<string> list = new List<string>();
			if (!flag3 || refFit == null)
			{
				list.Add("fitting (" + DescribeElementForDimensionDiagnostic(elemNearFitting) + ")");
			}
			if (!flag4 || refPipe == null)
			{
				list.Add("pipe end (" + DescribeElementForDimensionDiagnostic(elemNearPipeEnd) + ")");
			}
			failureDetail = "Could not resolve dimension references on the " + string.Join(" and ", list) + ". Try confirming fabrication geometry is visible in this view or pick another view template.";
			return false;
		}
		catch (Exception ex)
		{
			refFit = null;
			refPipe = null;
			failureDetail = "Reference resolution failed (caught): " + ex.Message;
			return false;
		}
	}

	private static string FormatElementAnchor(Element element, XYZ point)
	{
		if (element == null)
		{
			return "null";
		}
		string text = element.Id.Value.ToString();
		string text2 = DescribeElementForDimensionDiagnostic(element);
		if (point == null)
		{
			return text + ":" + text2;
		}
		return text + ":" + text2 + " @(" + point.X.ToString("F3") + "," + point.Y.ToString("F3") + "," + point.Z.ToString("F3") + ")";
	}

	private static List<Reference> CollectFabricationDimensionReferenceCandidates(Element element, View view, XYZ targetWorld, XYZ capNormalHint, XYZ dimensionLineDirectionWorld)
	{
		List<Reference> list = new List<Reference>();
		if (element == null || targetWorld == null)
		{
			return list;
		}
		void TryAddReference(Reference candidate)
		{
			if (candidate == null || IsWholeElementDimensionReference(element, candidate))
			{
				return;
			}
			Document document = element.Document;
			if (element is FabricationPart && !IsFabricationInstanceDimensionReference(document, candidate))
			{
				return;
			}
			string stableRepresentation;
			try
			{
				stableRepresentation = candidate.ConvertToStableRepresentation(document);
			}
			catch
			{
				return;
			}
			if (string.IsNullOrWhiteSpace(stableRepresentation))
			{
				return;
			}
			foreach (Reference item in list)
			{
				if (SafeStableRepresentationEquals(item, stableRepresentation, document))
				{
					return;
				}
			}
			list.Add(candidate);
		}
		XYZ val = capNormalHint;
		val = ((val == null || !(val.GetLength() > 1E-09)) ? null : val.Normalize());
		FamilyInstance val2 = (FamilyInstance)(object)((element is FamilyInstance) ? element : null);
		if (val2 != null && TryGetFamilyInstanceDimensionReferenceSleeveStyle(val2, targetWorld, dimensionLineDirectionWorld, out var reference, out _))
		{
			TryAddReference(reference);
			return list;
		}
		if (element is FabricationPart)
		{
			foreach (Reference rankedRef in CollectFabricationInstanceCurveReferencesRanked(element, view, targetWorld, val))
			{
				TryAddReference(rankedRef);
			}
			if (list.Count == 0)
			{
				foreach (FabricationInstanceCurveRef item in from c in EnumerateFabricationInstanceCurveReferences(element, view)
					where c.Curve != null && c.Curve.IsBound
					orderby c.Curve.Length descending
					select c)
				{
					TryAddReference(item.Reference);
				}
			}
		}
		if (element is FabricationPart && TryGetFabricationInstanceCurveReferenceNearest(element, view, targetWorld, val, out reference, out _))
		{
			TryAddReference(reference);
		}
		if (TryGetSubelementReferenceNearest(element, targetWorld, out reference, out _))
		{
			TryAddReference(reference);
		}
		if (TryGetLocationCurveWholeReferenceNearest(element, targetWorld, out reference, out _))
		{
			TryAddReference(reference);
		}
		if (TryGetLocationCurveEndReferenceNearest(element, targetWorld, out reference, out _))
		{
			TryAddReference(reference);
		}
		if (TryGetDimensionReferenceFromElementGeometryPaths(element, view, targetWorld, val, out reference, out _))
		{
			TryAddReference(reference);
		}
		if (TryExtractEdgeReferencesForDimension(element, view, targetWorld, out reference, out _))
		{
			TryAddReference(reference);
		}
		if (TryExtractFaceReferencesForDimension(element, view, targetWorld, val, out reference, out _))
		{
			TryAddReference(reference);
		}
		if (TryGetSubelementReferenceWeakFallback(element, targetWorld, out reference, out _))
		{
			TryAddReference(reference);
		}
		foreach (Reference subelementReference in GetSubelementReferences(element))
		{
			TryAddReference(subelementReference);
		}
		if (TryGetFabricationDimensionReferenceFrom3DView(element, targetWorld, out reference, out _))
		{
			TryAddReference(reference);
		}
		CollectTagStyleGeometryReferences(element, view, targetWorld, TryAddReference);
		if (element is FabricationPart fabricationPart)
		{
			foreach (XYZ item2 in BuildFabricationReferencePickPoints(fabricationPart, targetWorld))
			{
				if (TryGetSubelementReferenceNearest(element, item2, out reference, out _))
				{
					TryAddReference(reference);
				}
				if (TryGetLocationCurveEndReferenceNearest(element, item2, out reference, out _))
				{
					TryAddReference(reference);
				}
				if (TryGetFabricationDimensionReferenceFrom3DView(element, item2, out reference, out _))
				{
					TryAddReference(reference);
				}
				CollectTagStyleGeometryReferences(element, view, item2, TryAddReference);
			}
		}
		return list;
	}

	private static void CollectTagStyleGeometryReferences(Element element, View view, XYZ targetWorld, Action<Reference> tryAddReference)
	{
		if (element == null || targetWorld == null || tryAddReference == null)
		{
			return;
		}
		Options val = new Options
		{
			ComputeReferences = true,
			IncludeNonVisibleObjects = false,
			View = view
		};
		Options val2 = new Options
		{
			ComputeReferences = true,
			IncludeNonVisibleObjects = false
		};
		GeometryElement val3 = null;
		try
		{
			val3 = element.get_Geometry(val);
		}
		catch
		{
			val3 = null;
		}
		if ((GeometryObject)(object)val3 == (GeometryObject)null)
		{
			try
			{
				val3 = element.get_Geometry(val2);
			}
			catch
			{
				val3 = null;
			}
		}
		if ((GeometryObject)(object)val3 != (GeometryObject)null && view != null)
		{
			foreach (Reference allTagReference in GetAllTagReferences(val3, view, targetWorld))
			{
				tryAddReference(allTagReference);
			}
			foreach (Reference allEdgeReference in GetAllEdgeReferences(val3, targetWorld, view))
			{
				tryAddReference(allEdgeReference);
			}
			Reference bestTagReference = GetBestTagReference(val3, view);
			tryAddReference(bestTagReference);
		}
	}

	private static bool TryExtractFaceReferencesForDimension(Element element, View view, XYZ targetWorld, XYZ capHint, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		foreach (Options item in BuildGeometryOptionsForReferenceExtraction(view))
		{
			GeometryElement val = null;
			try
			{
				val = element.get_Geometry(item);
			}
			catch
			{
				val = null;
			}
			if ((GeometryObject)(object)val == (GeometryObject)null)
			{
				continue;
			}
			try
			{
				if (capHint != null)
				{
					double bestDistance = double.MaxValue;
					Reference bestRef = null;
					XYZ bestPt = null;
					PickClosestFaceReferenceInGeometry(val, targetWorld, Transform.Identity, capHint, requirePlanarCap: true, ref bestDistance, ref bestRef, ref bestPt);
					if (bestRef != null && bestPt != null)
					{
						reference = bestRef;
						referencePointWorld = bestPt;
						return true;
					}
				}
				double bestDistance2 = double.MaxValue;
				PickClosestFaceReferenceInGeometry(val, targetWorld, Transform.Identity, capHint, requirePlanarCap: false, ref bestDistance2, ref reference, ref referencePointWorld);
				if (reference != null && referencePointWorld != null)
				{
					return true;
				}
			}
			catch
			{
				reference = null;
				referencePointWorld = null;
			}
		}
		return false;
	}

	private static bool TryExtractEdgeReferencesForDimension(Element element, View view, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		foreach (Options item in BuildGeometryOptionsForReferenceExtraction(view))
		{
			GeometryElement val = null;
			try
			{
				val = element.get_Geometry(item);
			}
			catch
			{
				val = null;
			}
			if ((GeometryObject)(object)val == (GeometryObject)null)
			{
				continue;
			}
			try
			{
				double bestDistance = double.MaxValue;
				PickClosestEdgeReferenceInGeometry(val, targetWorld, Transform.Identity, ref bestDistance, ref reference, ref referencePointWorld);
				if (reference != null && referencePointWorld != null)
				{
					return true;
				}
			}
			catch
			{
				reference = null;
				referencePointWorld = null;
			}
		}
		return false;
	}

	private static XYZ ClosestPointOnEdgeCurveWorld(Curve curve, Transform localToWorld, XYZ targetWorld)
	{
		if ((GeometryObject)(object)curve == (GeometryObject)null || localToWorld == null || targetWorld == null)
		{
			return null;
		}
		XYZ val;
		try
		{
			val = localToWorld.Inverse.OfPoint(targetWorld);
		}
		catch
		{
			return null;
		}
		XYZ val2 = null;
		Line val3 = (Line)(object)((curve is Line) ? curve : null);
		if (val3 != null)
		{
			XYZ endPoint = ((Curve)val3).GetEndPoint(0);
			XYZ val4 = ((Curve)val3).GetEndPoint(1) - endPoint;
			double num = val4.DotProduct(val4);
			if (num < 1E-18)
			{
				val2 = endPoint;
			}
			else
			{
				double val5 = (val - endPoint).DotProduct(val4) / num;
				val5 = Math.Max(0.0, Math.Min(1.0, val5));
				val2 = endPoint + val4.Multiply(val5);
			}
		}
		else
		{
			try
			{
				IntersectionResult obj2 = curve.Project(val);
				val2 = ((obj2 != null) ? obj2.XYZPoint : null);
			}
			catch
			{
				val2 = null;
			}
		}
		if (val2 == null)
		{
			return null;
		}
		try
		{
			return localToWorld.OfPoint(val2);
		}
		catch
		{
			return null;
		}
	}

	private static void PickClosestEdgeReferenceInGeometry(GeometryElement geo, XYZ targetWorld, Transform localToWorld, ref double bestDistance, ref Reference bestRef, ref XYZ bestPt)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Expected O, but got Unknown
		if ((GeometryObject)(object)geo == (GeometryObject)null)
		{
			return;
		}
		foreach (GeometryObject item in geo)
		{
			Solid val = (Solid)(object)((item is Solid) ? item : null);
			if (val != null && val.Edges != null)
			{
				foreach (Edge edge in val.Edges)
				{
					Edge val2 = edge;
					if (((val2 != null) ? val2.Reference : null) == null)
					{
						continue;
					}
					Curve val3 = null;
					try
					{
						val3 = val2.AsCurve();
					}
					catch
					{
						val3 = null;
					}
					if ((GeometryObject)(object)val3 == (GeometryObject)null)
					{
						continue;
					}
					XYZ val4 = ClosestPointOnEdgeCurveWorld(val3, localToWorld, targetWorld);
					if (val4 != null)
					{
						double num = val4.DistanceTo(targetWorld);
						if (num < bestDistance)
						{
							bestDistance = num;
							bestRef = val2.Reference;
							bestPt = val4;
						}
					}
				}
				continue;
			}
			GeometryInstance val5 = (GeometryInstance)(object)((item is GeometryInstance) ? item : null);
			if (val5 != null)
			{
				Transform localToWorld2;
				try
				{
					localToWorld2 = localToWorld.Multiply(val5.Transform);
				}
				catch
				{
					continue;
				}
				GeometryElement val6 = null;
				try
				{
					val6 = val5.GetInstanceGeometry();
				}
				catch
				{
					val6 = null;
				}
				if ((GeometryObject)(object)val6 != (GeometryObject)null)
				{
					PickClosestEdgeReferenceInGeometry(val6, targetWorld, localToWorld2, ref bestDistance, ref bestRef, ref bestPt);
				}
				GeometryElement val7 = null;
				try
				{
					val7 = val5.GetSymbolGeometry();
				}
				catch
				{
					val7 = null;
				}
				if ((GeometryObject)(object)val7 != (GeometryObject)null)
				{
					PickClosestEdgeReferenceInGeometry(val7, targetWorld, localToWorld2, ref bestDistance, ref bestRef, ref bestPt);
				}
			}
		}
	}

	private static bool TryGetLocationCurveWholeReferenceNearest(Element element, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		Location obj = ((element != null) ? element.Location : null);
		LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
		if (val == null)
		{
			return false;
		}
		Curve curve = val.Curve;
		if ((GeometryObject)(object)curve == (GeometryObject)null)
		{
			return false;
		}
		try
		{
			reference = curve.Reference;
			if (reference == null)
			{
				return false;
			}
			referencePointWorld = ClosestPointOnBoundedModelCurveWorld(curve, targetWorld);
			return referencePointWorld != null;
		}
		catch
		{
			reference = null;
			referencePointWorld = null;
			return false;
		}
	}

	private static XYZ ClosestPointOnBoundedModelCurveWorld(Curve curve, XYZ targetWorld)
	{
		if ((GeometryObject)(object)curve == (GeometryObject)null || targetWorld == null)
		{
			return null;
		}
		try
		{
			IntersectionResult val = curve.Project(targetWorld);
			if (((val != null) ? val.XYZPoint : null) != null)
			{
				return val.XYZPoint;
			}
		}
		catch
		{
		}
		Line val2 = (Line)(object)((curve is Line) ? curve : null);
		if (val2 != null)
		{
			XYZ endPoint = ((Curve)val2).GetEndPoint(0);
			XYZ val3 = ((Curve)val2).GetEndPoint(1) - endPoint;
			double num = val3.DotProduct(val3);
			if (num < 1E-18)
			{
				return endPoint;
			}
			double val4 = (targetWorld - endPoint).DotProduct(val3) / num;
			val4 = Math.Max(0.0, Math.Min(1.0, val4));
			return endPoint + val3.Multiply(val4);
		}
		try
		{
			if (curve.IsBound)
			{
				return curve.Evaluate(0.5, true);
			}
		}
		catch
		{
		}
		try
		{
			XYZ endPoint2 = curve.GetEndPoint(0);
			XYZ endPoint3 = curve.GetEndPoint(1);
			return (endPoint2.DistanceTo(targetWorld) <= endPoint3.DistanceTo(targetWorld)) ? endPoint2 : endPoint3;
		}
		catch
		{
		}
		return null;
	}

	private static double MinimumDistanceFromBoundedCurveEndpointsToPoint(Curve curve, XYZ targetWorld)
	{
		if ((GeometryObject)(object)curve == (GeometryObject)null || targetWorld == null)
		{
			return double.MaxValue;
		}
		try
		{
			if (!curve.IsBound)
			{
				return double.MaxValue;
			}
			XYZ endPoint = curve.GetEndPoint(0);
			XYZ endPoint2 = curve.GetEndPoint(1);
			return Math.Min(endPoint.DistanceTo(targetWorld), endPoint2.DistanceTo(targetWorld));
		}
		catch
		{
			return double.MaxValue;
		}
	}

	private static bool TryGetLocationCurveEndReferenceNearest(Element element, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		Location obj = ((element != null) ? element.Location : null);
		LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
		if (val == null)
		{
			return false;
		}
		Curve curve = val.Curve;
		if ((GeometryObject)(object)curve == (GeometryObject)null)
		{
			return false;
		}
		try
		{
			XYZ endPoint = curve.GetEndPoint(0);
			XYZ endPoint2 = curve.GetEndPoint(1);
			Reference endPointReference = curve.GetEndPointReference(0);
			Reference endPointReference2 = curve.GetEndPointReference(1);
			if (endPointReference != null && endPointReference2 != null)
			{
				if (endPoint.DistanceTo(targetWorld) <= endPoint2.DistanceTo(targetWorld))
				{
					reference = endPointReference;
					referencePointWorld = endPoint;
				}
				else
				{
					reference = endPointReference2;
					referencePointWorld = endPoint2;
				}
				return true;
			}
			if (endPointReference != null)
			{
				reference = endPointReference;
				referencePointWorld = endPoint;
				return true;
			}
			if (endPointReference2 != null)
			{
				reference = endPointReference2;
				referencePointWorld = endPoint2;
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool TryGetSubelementReferenceNearest(Element element, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		double num = double.MaxValue;
		Reference val = null;
		XYZ val2 = null;
		foreach (Reference subelementReference in GetSubelementReferences(element))
		{
			if (subelementReference == null)
			{
				continue;
			}
			try
			{
				if (TryGetReferenceSampleWorldPointForTarget(element, subelementReference, targetWorld, out var point) && point != null)
				{
					double num2 = point.DistanceTo(targetWorld);
					if (num2 < num)
					{
						num = num2;
						val = subelementReference;
						val2 = point;
					}
				}
			}
			catch
			{
			}
		}
		if (val == null)
		{
			return false;
		}
		reference = val;
		referencePointWorld = val2;
		return true;
	}

	private static bool TryGetSubelementReferenceWeakFallback(Element element, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = targetWorld;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		foreach (Reference subelementReference in GetSubelementReferences(element))
		{
			if (subelementReference != null)
			{
				reference = subelementReference;
				return true;
			}
		}
		return false;
	}

	private static bool TryGetWholeElementDimensionReference(Element element, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Expected O, but got Unknown
		reference = null;
		referencePointWorld = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		try
		{
			reference = new Reference(element);
			referencePointWorld = targetWorld;
			return reference != null;
		}
		catch
		{
			reference = null;
			referencePointWorld = null;
			return false;
		}
	}

	private static string DescribeElementForDimensionDiagnostic(Element element)
	{
		if (element == null)
		{
			return "unknown element";
		}
		string text = element.Name ?? string.Empty;
		if (element is FabricationPart)
		{
			return "fabrication part " + text;
		}
		return text;
	}

	private static bool TryValidateDimensionReference(Element element, Reference reference)
	{
		return TryAcceptUsableLinearDimensionReference(element, reference);
	}

	private static bool TryAcceptUsableLinearDimensionReference(Element element, Reference reference)
	{
		if (element == null || reference == null || IsWholeElementDimensionReference(element, reference))
		{
			return false;
		}
		if (IsGeometricLinearDimensionReference(element, reference))
		{
			return true;
		}
		try
		{
			foreach (Reference subelementReference in GetSubelementReferences(element))
			{
				if (subelementReference != null && SafeStableRepresentationEquals(subelementReference, reference.ConvertToStableRepresentation(element.Document), element.Document))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return TryIsLocationCurveEndpointReference(element, reference);
	}

	private static bool IsWholeElementDimensionReference(Element element, Reference reference)
	{
		if (element == null || reference == null)
		{
			return true;
		}
		try
		{
			Reference val = new Reference(element);
			return SafeStableRepresentationEquals(reference, val.ConvertToStableRepresentation(element.Document), element.Document);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsGeometricLinearDimensionReference(Element element, Reference reference)
	{
		if (element == null || reference == null)
		{
			return false;
		}
		try
		{
			if (TryIsLocationCurveEndpointReference(element, reference))
			{
				return true;
			}
			GeometryObject geometryObject = element.GetGeometryObjectFromReference(reference);
			if (geometryObject == (GeometryObject)null)
			{
				return false;
			}
			if (geometryObject is Edge || geometryObject is Face)
			{
				return true;
			}
			Curve val = (Curve)(object)((geometryObject is Curve) ? geometryObject : null);
			if ((GeometryObject)(object)val != (GeometryObject)null)
			{
				return true;
			}
			Autodesk.Revit.DB.Point val2 = (Autodesk.Revit.DB.Point)(object)((geometryObject is Autodesk.Revit.DB.Point) ? geometryObject : null);
			return (GeometryObject)(object)val2 != (GeometryObject)null;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryIsLocationCurveEndpointReference(Element element, Reference reference)
	{
		if (element == null || reference == null)
		{
			return false;
		}
		Location obj = element.Location;
		LocationCurve val = (LocationCurve)(object)((obj is LocationCurve) ? obj : null);
		if (val == null)
		{
			return false;
		}
		Curve curve = val.Curve;
		if ((GeometryObject)(object)curve == (GeometryObject)null)
		{
			return false;
		}
		try
		{
			Document document = element.Document;
			string stableRepresentation = reference.ConvertToStableRepresentation(document);
			Reference endPointReference = curve.GetEndPointReference(0);
			Reference endPointReference2 = curve.GetEndPointReference(1);
			if (endPointReference != null && SafeStableRepresentationEquals(endPointReference, stableRepresentation, document))
			{
				return true;
			}
			if (endPointReference2 != null && SafeStableRepresentationEquals(endPointReference2, stableRepresentation, document))
			{
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private static IEnumerable<Options> BuildDimensionGeometryOptions(View view)
	{
		foreach (Options item in BuildModelGeometryOptionsForReferenceExtraction())
		{
			yield return item;
		}
		if (view != null)
		{
			bool[] includeFlags = new bool[2] { true, false };
			foreach (bool includeNonVisibleObjects in includeFlags)
			{
				yield return new Options
				{
					ComputeReferences = true,
					IncludeNonVisibleObjects = includeNonVisibleObjects,
					View = view
				};
			}
		}
	}

	private static void CollectCurveReferencesFromGeometry(GeometryElement geo, XYZ targetWorld, Transform localToWorld, ref double bestDistance, ref Reference bestRef, ref XYZ bestPt)
	{
		if ((GeometryObject)(object)geo == (GeometryObject)null || targetWorld == null)
		{
			return;
		}
		foreach (GeometryObject item in geo)
		{
			Curve val = (Curve)(object)((item is Curve) ? item : null);
			if (val != null && val.Reference != null)
			{
				XYZ val2 = ClosestPointOnBoundedModelCurveWorld(val, targetWorld);
				if (val2 != null)
				{
					double num = val2.DistanceTo(targetWorld);
					if (num < bestDistance)
					{
						bestDistance = num;
						bestRef = val.Reference;
						bestPt = val2;
					}
				}
				continue;
			}
			GeometryInstance val3 = (GeometryInstance)(object)((item is GeometryInstance) ? item : null);
			if (val3 == null)
			{
				continue;
			}
			Transform localToWorld2;
			try
			{
				localToWorld2 = localToWorld.Multiply(val3.Transform);
			}
			catch
			{
				continue;
			}
			GeometryElement val4 = null;
			try
			{
				val4 = val3.GetInstanceGeometry();
			}
			catch
			{
				val4 = null;
			}
			if ((GeometryObject)(object)val4 != (GeometryObject)null)
			{
				CollectCurveReferencesFromGeometry(val4, targetWorld, localToWorld2, ref bestDistance, ref bestRef, ref bestPt);
			}
			GeometryElement val5 = null;
			try
			{
				val5 = val3.GetSymbolGeometry();
			}
			catch
			{
				val5 = null;
			}
			if ((GeometryObject)(object)val5 != (GeometryObject)null)
			{
				CollectCurveReferencesFromGeometry(val5, targetWorld, localToWorld2, ref bestDistance, ref bestRef, ref bestPt);
			}
		}
	}

	private static bool TryGetCurveReferenceFromGeometryNearest(GeometryElement geo, XYZ targetWorld, Transform localToWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		double bestDistance = double.MaxValue;
		Reference bestRef = null;
		XYZ bestPt = null;
		CollectCurveReferencesFromGeometry(geo, targetWorld, localToWorld, ref bestDistance, ref bestRef, ref bestPt);
		if (bestRef == null)
		{
			return false;
		}
		reference = bestRef;
		referencePointWorld = bestPt;
		return true;
	}

	private static bool TryGetPlanarCapFaceReferenceFromGeometry(GeometryElement geo, XYZ targetWorld, XYZ capNormalHintUnit, Transform localToWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		double bestDistance = double.MaxValue;
		Reference bestRef = null;
		XYZ bestPt = null;
		PickClosestFaceReferenceInGeometry(geo, targetWorld, localToWorld, capNormalHintUnit, requirePlanarCap: true, ref bestDistance, ref bestRef, ref bestPt);
		if (bestRef == null)
		{
			return false;
		}
		reference = bestRef;
		referencePointWorld = bestPt;
		return true;
	}

	private static View3D TryFindAssembly3DOrthographicView(Document doc, Element element)
	{
		if (doc == null || element == null)
		{
			return null;
		}
		ElementId assemblyInstanceId = element.AssemblyInstanceId;
		if (assemblyInstanceId == (ElementId)null || assemblyInstanceId == ElementId.InvalidElementId)
		{
			return null;
		}
		Element element2 = doc.GetElement(assemblyInstanceId);
		AssemblyInstance val = (AssemblyInstance)(object)((element2 is AssemblyInstance) ? element2 : null);
		if (val == null)
		{
			return null;
		}
		return FindAssemblyViews(doc, val).OfType<View3D>().FirstOrDefault();
	}

	private static XYZ TryGetFabricationPartOrigin(FabricationPart part)
	{
		if (part == null)
		{
			return null;
		}
		try
		{
			XYZ origin = part.Origin;
			if (origin != null)
			{
				return origin;
			}
		}
		catch
		{
		}
		return null;
	}

	private static Connector TryGetNearestFabricationConnector(FabricationPart part, XYZ targetWorld)
	{
		if (part == null || targetWorld == null)
		{
			return null;
		}
		Connector best = null;
		double bestDistance = double.MaxValue;
		foreach (Connector connector in ListConnectors(part))
		{
			if (((connector != null) ? connector.Origin : null) == null)
			{
				continue;
			}
			double distance = connector.Origin.DistanceTo(targetWorld);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = connector;
			}
		}
		return best;
	}

	private static IEnumerable<XYZ> BuildFabricationReferencePickPoints(FabricationPart part, XYZ targetWorld)
	{
		if (targetWorld != null)
		{
			yield return targetWorld;
		}
		if (part == null)
		{
			yield break;
		}
		XYZ origin = TryGetFabricationPartOrigin(part);
		if (origin != null && (targetWorld == null || origin.DistanceTo(targetWorld) > 1.0 / 48.0))
		{
			yield return origin;
		}
		Connector nearestConnector = TryGetNearestFabricationConnector(part, targetWorld);
		if (((nearestConnector != null) ? nearestConnector.Origin : null) != null && (targetWorld == null || nearestConnector.Origin.DistanceTo(targetWorld) > 1.0 / 48.0))
		{
			yield return nearestConnector.Origin;
		}
	}

	private static bool TryGetFabricationPartReferenceAtWorldPoint(FabricationPart part, View view, XYZ targetWorld, XYZ preferredRunAxis, FabricationDimensionRefRole role, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		if (part == null || targetWorld == null)
		{
			return false;
		}
		return TryGetFabricationInstanceCurveReferenceNearest((Element)(object)part, view, targetWorld, preferredRunAxis, role, out reference, out referencePointWorld);
	}

	private static bool TryGetFabricationPartReferenceAtWorldPoint(FabricationPart part, View view, XYZ targetWorld, XYZ preferredRunAxis, out Reference reference, out XYZ referencePointWorld)
	{
		return TryGetFabricationPartReferenceAtWorldPoint(part, view, targetWorld, preferredRunAxis, FabricationDimensionRefRole.Generic, out reference, out referencePointWorld);
	}

	private static bool IsFabricationInstanceDimensionReference(Document doc, Reference reference)
	{
		if (doc == null || reference == null)
		{
			return false;
		}
		string stable;
		try
		{
			stable = reference.ConvertToStableRepresentation(doc);
		}
		catch
		{
			return false;
		}
		return !string.IsNullOrWhiteSpace(stable) && stable.IndexOf(":INSTANCE:", StringComparison.OrdinalIgnoreCase) >= 0 && stable.IndexOf(":LINEAR", StringComparison.OrdinalIgnoreCase) < 0;
	}

	private readonly struct FabricationInstanceCurveRef
	{
		public FabricationInstanceCurveRef(Reference reference, Curve curve)
		{
			Reference = reference;
			Curve = curve;
		}

		public Reference Reference { get; }

		public Curve Curve { get; }
	}

	private static List<FabricationInstanceCurveRef> EnumerateFabricationInstanceCurveReferences(Element element, View view)
	{
		List<FabricationInstanceCurveRef> results = new List<FabricationInstanceCurveRef>();
		Document doc = element?.Document;
		if (element == null || doc == null)
		{
			return results;
		}
		string elementStable;
		try
		{
			elementStable = new Reference(element).ConvertToStableRepresentation(doc);
		}
		catch
		{
			return results;
		}
		HashSet<string> symbolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Options options in BuildDimensionGeometryOptions(view))
		{
			GeometryElement geo = null;
			try
			{
				geo = element.get_Geometry(options);
			}
			catch
			{
				geo = null;
			}
			if (geo != null)
			{
				CollectFabricationSymbolIdsFromGeometry(geo, doc, symbolIds);
			}
		}
		HashSet<string> seenStables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int[] indexOrder = new int[49];
		int orderPos = 0;
		// 18 = flange outer-face center; 10 = elbow/fitting CL; 17 = flange back (avoid preferring).
		int[] preferred = new int[5] { 18, 10, 12, 8, 6 };
		foreach (int preferredIndex in preferred)
		{
			indexOrder[orderPos++] = preferredIndex;
		}
		for (int index = 0; index <= 48; index++)
		{
			if (index == 18 || index == 10 || index == 12 || index == 8 || index == 6)
			{
				continue;
			}
			indexOrder[orderPos++] = index;
		}
		foreach (string symbolId in symbolIds)
		{
			for (int orderIndex = 0; orderIndex < indexOrder.Length; orderIndex++)
			{
				int index = indexOrder[orderIndex];
				string trialStable = elementStable + ":0:INSTANCE:" + symbolId + ":" + index;
				if (!seenStables.Add(trialStable))
				{
					continue;
				}
				Reference parsedRef;
				try
				{
					parsedRef = Reference.ParseFromStableRepresentation(doc, trialStable);
				}
				catch
				{
					continue;
				}
				GeometryObject geoObj;
				try
				{
					geoObj = element.GetGeometryObjectFromReference(parsedRef);
				}
				catch
				{
					continue;
				}
				if (geoObj == null)
				{
					continue;
				}
				Curve curve = geoObj as Curve;
				results.Add(new FabricationInstanceCurveRef(parsedRef, curve));
			}
		}
		return results;
	}

	private static void CollectFabricationSymbolIdsFromGeometry(GeometryElement geo, Document doc, HashSet<string> symbolIds)
	{
		if (geo == null || doc == null || symbolIds == null)
		{
			return;
		}
		foreach (GeometryObject item in geo)
		{
			Curve curve = item as Curve;
			if (curve?.Reference != null)
			{
				string symbolId = TryExtractFabricationSymbolIdFromCurveReference(doc, curve.Reference);
				if (!string.IsNullOrWhiteSpace(symbolId))
				{
					symbolIds.Add(symbolId);
				}
			}
			GeometryInstance instance = item as GeometryInstance;
			if (instance == null)
			{
				continue;
			}
			GeometryElement instanceGeometry = null;
			GeometryElement symbolGeometry = null;
			try
			{
				instanceGeometry = instance.GetInstanceGeometry();
			}
			catch
			{
			}
			try
			{
				symbolGeometry = instance.GetSymbolGeometry();
			}
			catch
			{
			}
			if (instanceGeometry != null)
			{
				CollectFabricationSymbolIdsFromGeometry(instanceGeometry, doc, symbolIds);
			}
			if (symbolGeometry != null)
			{
				CollectFabricationSymbolIdsFromGeometry(symbolGeometry, doc, symbolIds);
			}
		}
	}

	private static string TryExtractFabricationSymbolIdFromCurveReference(Document doc, Reference reference)
	{
		string stable;
		try
		{
			stable = reference.ConvertToStableRepresentation(doc);
		}
		catch
		{
			return null;
		}
		if (string.IsNullOrWhiteSpace(stable))
		{
			return null;
		}
		int instanceMarker = stable.IndexOf(":INSTANCE:", StringComparison.OrdinalIgnoreCase);
		if (instanceMarker >= 0)
		{
			string afterInstance = stable.Substring(instanceMarker + ":INSTANCE:".Length);
			int lastColon = afterInstance.LastIndexOf(':');
			if (lastColon > 0)
			{
				return afterInstance.Substring(0, lastColon);
			}
		}
		int linearMarker = stable.IndexOf(":LINEAR", StringComparison.OrdinalIgnoreCase);
		if (linearMarker > 0)
		{
			string prefix = stable.Substring(0, linearMarker);
			int colon = prefix.IndexOf(':');
			return colon > 0 ? prefix.Substring(0, colon) : prefix;
		}
		int firstColon = stable.IndexOf(':');
		if (firstColon > 0 && stable.Length > 36)
		{
			return stable.Substring(0, firstColon);
		}
		return null;
	}

	private static bool TryScoreFabricationInstanceCurveForDimension(Curve curve, XYZ targetWorld, XYZ preferredRunAxis, XYZ viewNormalUnit, out double score, out XYZ closestPointWorld)
	{
		score = double.NegativeInfinity;
		closestPointWorld = null;
		if (curve == null || !curve.IsBound || targetWorld == null)
		{
			return false;
		}
		closestPointWorld = ClosestPointOnBoundedModelCurveWorld(curve, targetWorld);
		if (closestPointWorld == null)
		{
			return false;
		}
		double dist = closestPointWorld.DistanceTo(targetWorld);
		const double maxPickDistFeet = 6.0;
		if (dist > maxPickDistFeet)
		{
			return false;
		}
		double length = curve.Length;
		if (length < 1.0 / 384.0)
		{
			return false;
		}
		double alignFactor = 1.0;
		if (preferredRunAxis != null && preferredRunAxis.GetLength() > 1E-09)
		{
			XYZ axis = preferredRunAxis.Normalize();
			XYZ dir = null;
			Line line = curve as Line;
			if (line != null)
			{
				dir = line.Direction;
			}
			else
			{
				XYZ delta = curve.GetEndPoint(1) - curve.GetEndPoint(0);
				if (delta.GetLength() > 1E-09)
				{
					dir = delta.Normalize();
				}
			}
			if (dir != null && viewNormalUnit != null && viewNormalUnit.GetLength() > 1E-09)
			{
				XYZ planeAxis = ProjectVectorToViewPlane(axis, viewNormalUnit);
				XYZ planeDir = ProjectVectorToViewPlane(dir, viewNormalUnit);
				if (planeAxis != null && planeAxis.GetLength() > 1E-09 && planeDir != null && planeDir.GetLength() > 1E-09)
				{
					alignFactor = 0.15 + 0.85 * Math.Abs(planeDir.Normalize().DotProduct(planeAxis.Normalize()));
				}
			}
			else if (dir != null)
			{
				alignFactor = 0.15 + 0.85 * Math.Abs(dir.DotProduct(axis));
			}
		}
		score = length * alignFactor - dist * 0.25;
		return true;
	}

	private static bool TryGetFabricationInstanceCurveReferenceNearest(Element element, View view, XYZ targetWorld, XYZ preferredRunAxis, FabricationDimensionRefRole role, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		double bestScore = double.NegativeInfinity;
		Reference bestRef = null;
		XYZ bestPt = null;
		foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(element, view))
		{
			if (item.Reference == null || IsWholeElementDimensionReference(element, item.Reference))
			{
				continue;
			}
			double itemScore = ScoreFabricationInstanceReferenceForDimension(element, item.Reference, item.Curve, targetWorld, role, preferredRunAxis);
			if (itemScore > bestScore)
			{
				bestScore = itemScore;
				bestRef = item.Reference;
				if (item.Curve != null && item.Curve.IsBound && targetWorld != null)
				{
					bestPt = ClosestPointOnBoundedModelCurveWorld(item.Curve, targetWorld);
				}
				else if (!TryGetReferenceSampleWorldPointForTarget(element, item.Reference, targetWorld, out bestPt) || bestPt == null)
				{
					bestPt = targetWorld;
				}
			}
		}
		if (bestRef == null || IsWholeElementDimensionReference(element, bestRef))
		{
			return false;
		}
		reference = bestRef;
		referencePointWorld = bestPt ?? targetWorld;
		return true;
	}

	private static bool TryGetFabricationInstanceCurveReferenceNearest(Element element, View view, XYZ targetWorld, XYZ preferredRunAxis, out Reference reference, out XYZ referencePointWorld)
	{
		return TryGetFabricationInstanceCurveReferenceNearest(element, view, targetWorld, preferredRunAxis, FabricationDimensionRefRole.Generic, out reference, out referencePointWorld);
	}

	private static IEnumerable<Reference> CollectFabricationInstanceCurveReferencesRanked(Element element, View view, XYZ targetWorld, XYZ preferredRunAxis)
	{
		List<(Reference reference, double score)> ranked = new List<(Reference, double)>();
		if (element == null || targetWorld == null)
		{
			return Array.Empty<Reference>();
		}
		XYZ viewNormal = null;
		try
		{
			viewNormal = view?.ViewDirection;
			if (viewNormal != null && viewNormal.GetLength() > 1E-09)
			{
				viewNormal = viewNormal.Normalize();
			}
		}
		catch
		{
			viewNormal = null;
		}
		foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(element, view))
		{
			if (TryScoreFabricationInstanceCurveForDimension(item.Curve, targetWorld, preferredRunAxis, viewNormal, out double itemScore, out _))
			{
				ranked.Add((item.Reference, itemScore));
			}
		}
		if (ranked.Count == 0)
		{
			foreach (FabricationInstanceCurveRef item in EnumerateFabricationInstanceCurveReferences(element, view))
			{
				Curve curve = item.Curve;
				if (curve == null || !curve.IsBound)
				{
					continue;
				}
				XYZ closest = ClosestPointOnBoundedModelCurveWorld(curve, targetWorld);
				if (closest == null)
				{
					continue;
				}
				ranked.Add((item.Reference, curve.Length - closest.DistanceTo(targetWorld) * 0.25));
			}
		}
		return from pair in ranked
			orderby pair.score descending
			select pair.reference;
	}

	private static bool TryGetFabricationConnectionPointReference(Element element, View view, XYZ connectionPointWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = connectionPointWorld;
		if (element == null || connectionPointWorld == null)
		{
			return false;
		}
		FabricationPart fabricationPart = (FabricationPart)(object)((element is FabricationPart) ? element : null);
		if (fabricationPart != null && TryGetFabricationPartReferenceAtWorldPoint(fabricationPart, view, connectionPointWorld, null, out reference, out referencePointWorld) && reference != null)
		{
			return true;
		}
		if (TryGetFabricationDimensionReferenceFrom3DView(element, connectionPointWorld, out reference, out referencePointWorld) && reference != null)
		{
			return true;
		}
		if (view != null && TryExtractFaceReferencesForDimension(element, view, connectionPointWorld, null, out reference, out referencePointWorld) && reference != null)
		{
			return true;
		}
		if (view != null && TryExtractEdgeReferencesForDimension(element, view, connectionPointWorld, out reference, out referencePointWorld) && reference != null)
		{
			return true;
		}
		if (TryGetSubelementReferenceNearest(element, connectionPointWorld, out reference, out referencePointWorld) && reference != null)
		{
			return true;
		}
		return false;
	}

	private static bool TryGetFabricationDimensionReferenceFrom3DView(Element element, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		View3D val = _autoDimReferencePickView3D ?? TryFindAssembly3DOrthographicView(element.Document, element);
		if (val == null)
		{
			return false;
		}
		FindReferenceTarget[] array = new FindReferenceTarget[3]
		{
			(FindReferenceTarget)16,
			(FindReferenceTarget)4,
			(FindReferenceTarget)1
		};
		foreach (FindReferenceTarget target in array)
		{
			Reference val2 = Get3DPickedReference(val, element, targetWorld, target);
			if (val2 != null && !IsWholeElementDimensionReference(element, val2))
			{
				reference = val2;
				referencePointWorld = targetWorld;
				return true;
			}
		}
		return false;
	}

	private static bool TryGetDimensionReferenceFromElementGeometryPaths(Element element, View view, XYZ targetWorld, XYZ capNormalHint, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		XYZ val = capNormalHint;
		val = ((val == null || !(val.GetLength() > 1E-09)) ? null : val.Normalize());
		foreach (Options item in BuildDimensionGeometryOptions(view))
		{
			GeometryElement val2 = null;
			try
			{
				val2 = element.get_Geometry(item);
			}
			catch
			{
				val2 = null;
			}
			if ((GeometryObject)(object)val2 == (GeometryObject)null)
			{
				continue;
			}
			if (TryGetCurveReferenceFromGeometryNearest(val2, targetWorld, Transform.Identity, out var reference2, out var referencePointWorld2) && reference2 != null && !IsWholeElementDimensionReference(element, reference2))
			{
				reference = reference2;
				referencePointWorld = referencePointWorld2;
				return true;
			}
			double bestEdgeDistance = double.MaxValue;
			Reference bestEdgeRef = null;
			XYZ bestEdgePt = null;
			PickClosestEdgeReferenceInGeometry(val2, targetWorld, Transform.Identity, ref bestEdgeDistance, ref bestEdgeRef, ref bestEdgePt);
			if (bestEdgeRef != null && !IsWholeElementDimensionReference(element, bestEdgeRef))
			{
				reference = bestEdgeRef;
				referencePointWorld = bestEdgePt ?? targetWorld;
				return true;
			}
			if (val != null && TryGetPlanarCapFaceReferenceFromGeometry(val2, targetWorld, val, Transform.Identity, out reference2, out referencePointWorld2) && reference2 != null && !IsWholeElementDimensionReference(element, reference2))
			{
				reference = reference2;
				referencePointWorld = referencePointWorld2;
				return true;
			}
			View val3 = view ?? item.View;
			if (val3 != null)
			{
				foreach (Reference allTagReference in GetAllTagReferences(val2, val3, targetWorld))
				{
					if (allTagReference != null && !IsWholeElementDimensionReference(element, allTagReference))
					{
						reference = allTagReference;
						referencePointWorld = targetWorld;
						return true;
					}
				}
				foreach (Reference allEdgeReference in GetAllEdgeReferences(val2, targetWorld, val3))
				{
					if (allEdgeReference != null && !IsWholeElementDimensionReference(element, allEdgeReference))
					{
						reference = allEdgeReference;
						referencePointWorld = targetWorld;
						return true;
					}
				}
				Reference bestTagReference = GetBestTagReference(val2, val3);
				if (bestTagReference != null && !IsWholeElementDimensionReference(element, bestTagReference))
				{
					reference = bestTagReference;
					referencePointWorld = targetWorld;
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>
	/// Fabrication INSTANCE Point/Curve coords from GetGeometryObjectFromReference are symbol-local.
	/// Must transform by GeometryInstance.Transform before comparing to world snap targets.
	/// </summary>
	private static bool TryGetFabricationInstanceWorldTransform(Element element, out Transform transform)
	{
		transform = null;
		if (element == null)
		{
			return false;
		}

		foreach (Options options in BuildDimensionGeometryOptions(null))
		{
			GeometryElement geo = null;
			try
			{
				geo = element.get_Geometry(options);
			}
			catch
			{
				geo = null;
			}
			if (geo == null)
			{
				continue;
			}

			foreach (GeometryObject item in geo)
			{
				if (item is GeometryInstance instance && instance.Transform != null)
				{
					transform = instance.Transform;
					return true;
				}
			}
		}

		return false;
	}

	private static bool IsFabricationInstanceReference(Document doc, Reference reference)
	{
		if (doc == null || reference == null)
		{
			return false;
		}
		try
		{
			string stable = reference.ConvertToStableRepresentation(doc);
			return !string.IsNullOrWhiteSpace(stable)
				&& stable.IndexOf(":INSTANCE:", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch
		{
			return false;
		}
	}

	private static XYZ ToFabricationWorldPoint(Element element, Reference reference, XYZ localOrWorld)
	{
		if (localOrWorld == null || element == null)
		{
			return localOrWorld;
		}
		if (!IsFabricationInstanceReference(element.Document, reference))
		{
			return localOrWorld;
		}
		if (!TryGetFabricationInstanceWorldTransform(element, out Transform xf) || xf == null)
		{
			return localOrWorld;
		}
		try
		{
			return xf.OfPoint(localOrWorld);
		}
		catch
		{
			return localOrWorld;
		}
	}

	private static bool TryGetReferenceSampleWorldPointForTarget(Element element, Reference reference, XYZ targetWorld, out XYZ point)
	{
		point = null;
		if (element == null || reference == null || targetWorld == null)
		{
			return false;
		}
		GeometryObject geometryObjectFromReference;
		try
		{
			geometryObjectFromReference = element.GetGeometryObjectFromReference(reference);
		}
		catch
		{
			return false;
		}
		if (geometryObjectFromReference == (GeometryObject)null)
		{
			return false;
		}
		try
		{
			RevitPoint val = (RevitPoint)(object)((geometryObjectFromReference is RevitPoint) ? geometryObjectFromReference : null);
			if (val != null)
			{
				// INSTANCE Point.Coord is symbol-local — transform to world or flange face-center (idx 18) never scores.
				point = ToFabricationWorldPoint(element, reference, val.Coord);
				return point != null;
			}
			Curve val2 = (Curve)(object)((geometryObjectFromReference is Curve) ? geometryObjectFromReference : null);
			if (val2 != null)
			{
				if (IsFabricationInstanceReference(element.Document, reference)
					&& TryGetFabricationInstanceWorldTransform(element, out Transform xf)
					&& xf != null)
				{
					// Project in local space against inverse-mapped target, then map hit to world.
					XYZ localTarget = xf.Inverse.OfPoint(targetWorld);
					XYZ localHit = ClosestPointOnBoundedModelCurveWorld(val2, localTarget);
					point = localHit != null ? xf.OfPoint(localHit) : null;
					return point != null;
				}

				point = ClosestPointOnBoundedModelCurveWorld(val2, targetWorld);
				return point != null;
			}
			Edge val3 = (Edge)(object)((geometryObjectFromReference is Edge) ? geometryObjectFromReference : null);
			if (val3 != null)
			{
				Curve val4 = val3.AsCurve();
				if ((GeometryObject)(object)val4 == (GeometryObject)null)
				{
					return false;
				}
				if (IsFabricationInstanceReference(element.Document, reference)
					&& TryGetFabricationInstanceWorldTransform(element, out Transform xfEdge)
					&& xfEdge != null)
				{
					XYZ localTarget = xfEdge.Inverse.OfPoint(targetWorld);
					XYZ localHit = ClosestPointOnBoundedModelCurveWorld(val4, localTarget);
					point = localHit != null ? xfEdge.OfPoint(localHit) : null;
					return point != null;
				}

				point = ClosestPointOnBoundedModelCurveWorld(val4, targetWorld);
				return point != null;
			}
			Face val5 = (Face)(object)((geometryObjectFromReference is Face) ? geometryObjectFromReference : null);
			if (val5 != null)
			{
				IntersectionResult val6 = val5.Project(targetWorld);
				if (val6 != null && val6.XYZPoint != null)
				{
					point = val6.XYZPoint;
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool TryGetFamilyInstanceDimensionReferenceSleeveStyle(FamilyInstance fi, XYZ targetWorld, XYZ dimensionLineDirectionWorld, out Reference reference, out XYZ referencePointWorld)
	{
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		reference = null;
		referencePointWorld = null;
		if (fi == null || targetWorld == null)
		{
			return false;
		}
		bool flag = true;
		if (dimensionLineDirectionWorld != null && dimensionLineDirectionWorld.GetLength() > 1E-09)
		{
			XYZ val = dimensionLineDirectionWorld.Normalize();
			flag = Math.Abs(val.X) >= Math.Abs(val.Y);
		}
		FamilyInstanceReferenceType val2 = (FamilyInstanceReferenceType)(flag ? 1 : 4);
		try
		{
			IList<Reference> references = fi.GetReferences(val2);
			if (references != null && references.Count > 0 && references[0] != null)
			{
				reference = references[0];
				if (!TryGetReferenceSampleWorldPointForTarget((Element)(object)fi, reference, targetWorld, out referencePointWorld) || referencePointWorld == null)
				{
					referencePointWorld = targetWorld;
				}
				return true;
			}
		}
		catch
		{
		}
		return TryGetFamilyInstanceFaceReferenceSleeveFallback(fi, flag, targetWorld, out reference, out referencePointWorld);
	}

	private static bool TryGetFamilyInstanceFaceReferenceSleeveFallback(FamilyInstance fi, bool horizontalDimLine, XYZ targetWorld, out Reference reference, out XYZ referencePointWorld)
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Expected O, but got Unknown
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Expected O, but got Unknown
		//IL_00f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Expected O, but got Unknown
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Expected O, but got Unknown
		reference = null;
		referencePointWorld = null;
		if (fi == null || targetWorld == null)
		{
			return false;
		}
		try
		{
			Options val = new Options
			{
				ComputeReferences = true,
				DetailLevel = (ViewDetailLevel)3
			};
			GeometryElement val2 = fi.get_Geometry(val);
			if ((GeometryObject)(object)val2 == (GeometryObject)null)
			{
				return false;
			}
			foreach (GeometryObject item in val2)
			{
				Solid val3 = (Solid)(object)((item is Solid) ? item : null);
				if ((GeometryObject)(object)val3 == (GeometryObject)null || val3.Faces == null)
				{
					continue;
				}
				foreach (Face face in val3.Faces)
				{
					Face val4 = face;
					if (val4.Area < 1.0 / 144.0)
					{
						continue;
					}
					BoundingBoxUV boundingBox = val4.GetBoundingBox();
					UV val5 = new UV((boundingBox.Min.U + boundingBox.Max.U) * 0.5, (boundingBox.Min.V + boundingBox.Max.V) * 0.5);
					XYZ val6 = val4.ComputeNormal(val5);
					if (val6 == null)
					{
						continue;
					}
					val6 = new XYZ(Math.Abs(val6.X), Math.Abs(val6.Y), Math.Abs(val6.Z));
					if (((horizontalDimLine && val6.X > val6.Y + 0.0001 && val6.X > val6.Z + 0.0001) || (!horizontalDimLine && val6.Y > val6.X + 0.0001 && val6.Y > val6.Z + 0.0001)) && val4.Reference != null)
					{
						reference = val4.Reference;
						if (!TryGetReferenceSampleWorldPointForTarget((Element)(object)fi, reference, targetWorld, out referencePointWorld) || referencePointWorld == null)
						{
							referencePointWorld = targetWorld;
						}
						return true;
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool TryGetDimensionReferenceAtWorldPoint(Element element, View view, XYZ targetWorld, XYZ capNormalHint, XYZ dimensionLineDirectionWorld, out Reference reference, out XYZ referencePointWorld)
	{
		reference = null;
		referencePointWorld = null;
		if (element == null || targetWorld == null)
		{
			return false;
		}
		try
		{
			XYZ val = capNormalHint;
			val = ((val == null || !(val.GetLength() > 1E-09)) ? null : val.Normalize());
			FamilyInstance val2 = (FamilyInstance)(object)((element is FamilyInstance) ? element : null);
			if (val2 != null && TryGetFamilyInstanceDimensionReferenceSleeveStyle(val2, targetWorld, dimensionLineDirectionWorld, out reference, out referencePointWorld))
			{
				return true;
			}
			if (element is FabricationPart fabricationPart)
			{
				if (TryGetFabricationPartReferenceAtWorldPoint(fabricationPart, view, targetWorld, val, out reference, out referencePointWorld))
				{
					return true;
				}
				List<Reference> list = CollectFabricationDimensionReferenceCandidates(element, view, targetWorld, val, dimensionLineDirectionWorld);
				if (list.Count > 0)
				{
					reference = list[0];
					referencePointWorld = targetWorld;
					return true;
				}
				return false;
			}
			if (TryExtractFaceReferencesForDimension(element, view, targetWorld, val, out reference, out referencePointWorld))
			{
				return true;
			}
			if (TryExtractEdgeReferencesForDimension(element, view, targetWorld, out reference, out referencePointWorld))
			{
				return true;
			}
			if (TryGetLocationCurveEndReferenceNearest(element, targetWorld, out reference, out referencePointWorld))
			{
				return true;
			}
			if (TryGetLocationCurveWholeReferenceNearest(element, targetWorld, out reference, out referencePointWorld))
			{
				return true;
			}
			return false;
		}
		catch (Exception)
		{
			reference = null;
			referencePointWorld = null;
			return false;
		}
	}

	private static void PickClosestFaceReferenceInGeometry(GeometryElement geo, XYZ targetWorld, Transform localToWorld, XYZ capNormalHintUnit, bool requirePlanarCap, ref double bestDistance, ref Reference bestRef, ref XYZ bestPt)
	{
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Expected O, but got Unknown
		if ((GeometryObject)(object)geo == (GeometryObject)null)
		{
			return;
		}
		foreach (GeometryObject item in geo)
		{
			Solid val = (Solid)(object)((item is Solid) ? item : null);
			if (val != null && val.Faces != null && val.Faces.Size > 0)
			{
				XYZ val2;
				try
				{
					val2 = localToWorld.Inverse.OfPoint(targetWorld);
				}
				catch
				{
					continue;
				}
				foreach (Face face in val.Faces)
				{
					Face val3 = face;
					if (((val3 != null) ? val3.Reference : null) == null)
					{
						continue;
					}
					XYZ val5;
					if (requirePlanarCap)
					{
						PlanarFace val4 = (PlanarFace)(object)((val3 is PlanarFace) ? val3 : null);
						if ((GeometryObject)(object)val4 == (GeometryObject)null)
						{
							continue;
						}
						if (capNormalHintUnit != null)
						{
							try
							{
								val5 = localToWorld.OfVector(val4.FaceNormal);
								if (val5 == null || val5.GetLength() < 1E-09)
								{
									continue;
								}
								val5 = val5.Normalize();
								goto IL_00e3;
							}
							catch
							{
							}
							continue;
						}
					}
					goto IL_00fb;
					IL_00fb:
					IntersectionResult val6 = null;
					try
					{
						val6 = val3.Project(val2);
					}
					catch
					{
						val6 = null;
					}
					if (((val6 != null) ? val6.XYZPoint : null) != null)
					{
						XYZ val7;
						try
						{
							val7 = localToWorld.OfPoint(val6.XYZPoint);
						}
						catch
						{
							continue;
						}
						double num = val7.DistanceTo(targetWorld);
						if (num < bestDistance)
						{
							bestDistance = num;
							bestRef = val3.Reference;
							bestPt = val7;
						}
					}
					continue;
					IL_00e3:
					if (Math.Abs(val5.DotProduct(capNormalHintUnit)) < 0.55)
					{
						continue;
					}
					goto IL_00fb;
				}
				continue;
			}
			GeometryInstance val8 = (GeometryInstance)(object)((item is GeometryInstance) ? item : null);
			if (val8 != null)
			{
				Transform localToWorld2;
				try
				{
					localToWorld2 = localToWorld.Multiply(val8.Transform);
				}
				catch
				{
					continue;
				}
				GeometryElement val9 = null;
				try
				{
					val9 = val8.GetInstanceGeometry();
				}
				catch
				{
					val9 = null;
				}
				if ((GeometryObject)(object)val9 != (GeometryObject)null)
				{
					PickClosestFaceReferenceInGeometry(val9, targetWorld, localToWorld2, capNormalHintUnit, requirePlanarCap, ref bestDistance, ref bestRef, ref bestPt);
				}
			}
		}
	}

}
