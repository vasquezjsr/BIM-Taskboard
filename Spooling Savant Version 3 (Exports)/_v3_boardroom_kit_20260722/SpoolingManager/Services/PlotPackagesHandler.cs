using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using ABMEP.Work;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

public class PlotPackagesHandler : IExternalEventHandler
{
	private sealed class ElementIdEqualityComparer : IEqualityComparer<ElementId>
	{
		public bool Equals(ElementId x, ElementId y)
		{
			if (x == y)
			{
				return true;
			}
			if (x == (ElementId)null || y == (ElementId)null)
			{
				return false;
			}
			return x.Value == y.Value;
		}

		public int GetHashCode(ElementId obj)
		{
			if (obj == null)
			{
				return 0;
			}
			return obj.Value.GetHashCode();
		}
	}

	public PlotPackagesRequest PendingRequest { get; set; }

	public void Execute(UIApplication app)
	{
		//IL_02ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_039e: Unknown result type (might be due to invalid IL or missing references)
		//IL_045f: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_046a: Unknown result type (might be due to invalid IL or missing references)
		//IL_030e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0311: Invalid comparison between Unknown and I4
		//IL_03c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c5: Invalid comparison between Unknown and I4
		//IL_0483: Unknown result type (might be due to invalid IL or missing references)
		//IL_0486: Invalid comparison between Unknown and I4
		//IL_02e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0394: Unknown result type (might be due to invalid IL or missing references)
		//IL_0399: Unknown result type (might be due to invalid IL or missing references)
		//IL_0455: Unknown result type (might be due to invalid IL or missing references)
		//IL_045a: Unknown result type (might be due to invalid IL or missing references)
		if (((app != null) ? app.Application : null) != null)
		{
			InstallLayout.ApplyRevitVersionNumber(app.Application.VersionNumber);
		}
		PlotPackagesRequest pendingRequest = PendingRequest;
		PendingRequest = null;
		if (pendingRequest == null || pendingRequest.Batches == null || pendingRequest.Batches.Count == 0)
		{
			ShowAlphaPlotPackagesDialog(app, "Plot Packages", "Plot Packages did not receive a valid request. Close and reopen SS Manager, then try again.");
			return;
		}
		if (pendingRequest.ProductKind != SpoolingManagerKind.Standard)
		{
			ShowAlphaPlotPackagesDialog(app, "Plot Packages", "Plot Packages is only available in SS Manager.");
			return;
		}
		string text = (pendingRequest.OutputFolder ?? string.Empty).Trim();
		if (text.Length == 0 || !Directory.Exists(text))
		{
			ShowAlphaPlotPackagesDialog(app, "Plot Packages", "Choose a valid output folder before plotting.");
			return;
		}
		UIDocument activeUIDocument = app.ActiveUIDocument;
		Document val = ((activeUIDocument != null) ? activeUIDocument.Document : null);
		if (val == null)
		{
			ShowAlphaPlotPackagesDialog(app, "Plot Packages", "No active Revit document was found.");
			return;
		}
		SpoolingManagerKind productKind = pendingRequest.ProductKind;
		bool regularBranch = CreateSpoolSheetsHandler.UsesRegularSheetBranch(SpoolingManagerSettings.Load(productKind), productKind);
		string toolWindowTitle = pendingRequest.ExportToBoardroom
			? "Export to Boardroom"
			: CreateSpoolSheetsHandler.GetToolWindowTitle(productKind);
		List<string> list = new List<string>();
		StringBuilder stringBuilder = new StringBuilder();
		text = Path.GetFullPath(text);
		PlotPackagesReportOptions plotPackagesReportOptions = pendingRequest.ReportOptions ?? new PlotPackagesReportOptions();
		PipeFittingsBomPdfCommand.PlotPackageHeaderInfo plotHeader = new PipeFittingsBomPdfCommand.PlotPackageHeaderInfo
		{
			ProjectName = plotPackagesReportOptions.ProjectName,
			CreatedBy = plotPackagesReportOptions.CreatedBy,
			DateText = plotPackagesReportOptions.DateText
		};
		try
		{
			using (SsSavantHotloadDependencyScope.ForWorkerAssembly())
			{
				SsSavantPdfDependencyWarmup.EnsurePdfDependenciesLoaded();
				foreach (PlotPackageBatch batch in pendingRequest.Batches)
			{
				if (batch == null || batch.AssemblyIds == null || batch.AssemblyIds.Count == 0)
				{
					stringBuilder.AppendLine("[?] Batch had no assemblies — skipped.");
					continue;
				}
				string text2 = (batch.PackageLabel ?? string.Empty).Trim();
				if (text2.Length == 0)
				{
					text2 = "Package";
				}
				string text3 = batch.PackageLabel ?? text2;
				List<FabricationPart> list2 = ((plotPackagesReportOptions.IncludeBillOfMaterials || plotPackagesReportOptions.IncludeCutList || plotPackagesReportOptions.IncludeTigerStop || plotPackagesReportOptions.IncludePcfFiles) ? CollectFabricationPartsForAssemblies(val, batch.AssemblyIds) : new List<FabricationPart>());
				List<(FabricationPart Part, string AssemblyName)> cutlistParts = (plotPackagesReportOptions.IncludeCutList || plotPackagesReportOptions.IncludeTigerStop || plotPackagesReportOptions.IncludePcfFiles)
					? CollectFabricationPartsWithAssemblyNames(val, batch.AssemblyIds)
					: new List<(FabricationPart Part, string AssemblyName)>();
				if (plotPackagesReportOptions.IncludeSpoolsCombined)
				{
					List<ViewSheet> list3 = CollectDistinctSpoolSheets(val, batch.AssemblyIds, regularBranch);
					string text4 = SanitizeFileStem(text2 + " - Spools Combined");
					string text5 = Path.Combine(text, text4 + ".pdf");
					if (list3.Count == 0)
					{
						stringBuilder.AppendLine("[" + text3 + "] No spool sheets found — skipped «" + text2 + " - Spools Combined».");
					}
					else
					{
						try
						{
							EnsureTrackingQrBeforePlot(val, batch.AssemblyIds, regularBranch, pendingRequest.ProductKind);
							TryDeleteExistingOutput(text5);
							PlotSheetsToPdf(val, list3, text5);
							list.Add(text2 + " - Spools Combined (Plotted)");
						}
						catch (Exception ex)
						{
							stringBuilder.AppendLine("[" + text3 + "] Spools Combined failed: " + ex.Message);
						}
					}
				}
				if (plotPackagesReportOptions.IncludeSpoolMap)
				{
					string packageValue = ResolvePackageValueForBatch(val, batch);
					ViewSheet spoolMapSheet = CreateSpoolMapHandler.FindSpoolMapSheet(val, text2, packageValue);
					string spoolMapStem = SanitizeFileStem(text2 + " - Spool Map");
					string spoolMapPdf = Path.Combine(text, spoolMapStem + ".pdf");
					if (spoolMapSheet == null)
					{
						stringBuilder.AppendLine("[" + text3 + "] No Spool Map sheet found — skipped «" + text2 + " - Spool Map». Create Spool Map for this package first.");
					}
					else
					{
						try
						{
							if (!string.IsNullOrWhiteSpace(packageValue))
							{
								FabricationSavantParameterSync.TrySetPackageParameter(spoolMapSheet, packageValue);
							}
							TryDeleteExistingOutput(spoolMapPdf);
							PlotSheetsToPdf(val, new List<ViewSheet> { spoolMapSheet }, spoolMapPdf);
							list.Add(text2 + " - Spool Map (Plotted)");
						}
						catch (Exception exSpoolMap)
						{
							stringBuilder.AppendLine("[" + text3 + "] Spool Map failed: " + exSpoolMap.Message);
						}
					}
				}
				if (plotPackagesReportOptions.IncludeAssemblyList)
				{
					List<PipeFittingsBomPdfCommand.PackageAssemblyListRow> assemblyRows = BuildOrderedAssemblyListRowsForPackage(val, batch.AssemblyIds, regularBranch);
					string text6 = SanitizeFileStem(text2 + " - Assembly List");
					string text7 = Path.Combine(text, text6 + ".pdf");
					string message = string.Empty;
					TryDeleteExistingOutput(text7);
					Result val2;
					try
					{
						val2 = PipeFittingsBomPdfCommand.ExportPackageAssemblyListPdf(app, val, assemblyRows, text7, ref message, plotHeader);
					}
					catch (Exception ex2)
					{
						val2 = (Result)(-1);
						message = ex2.Message;
					}
					if ((int)val2 == 0)
					{
						list.Add(text2 + " - Assembly List (Plotted)");
					}
					else if ((int)val2 == 1)
					{
						stringBuilder.AppendLine("[" + text3 + "] Assembly List skipped — " + message);
					}
					else
					{
						stringBuilder.AppendLine("[" + text3 + "] Assembly List failed — " + message);
					}
				}
				if (plotPackagesReportOptions.IncludeBillOfMaterials)
				{
					string text8 = SanitizeFileStem(text2 + " - Bill of Materials");
					string text9 = Path.Combine(text, text8 + ".pdf");
					string message2 = string.Empty;
					TryDeleteExistingOutput(text9);
					Result val3;
					try
					{
						val3 = PipeFittingsBomPdfCommand.ExportFabricationPartsBomPdf(app, val, list2, text9, ref message2, plotHeader);
					}
					catch (Exception ex3)
					{
						val3 = (Result)(-1);
						message2 = ex3.Message;
					}
					if ((int)val3 == 0)
					{
						list.Add(text2 + " - Bill of Materials (Plotted)");
					}
					else if ((int)val3 == 1)
					{
						stringBuilder.AppendLine("[" + text3 + "] Bill of Materials skipped — " + message2);
					}
					else
					{
						stringBuilder.AppendLine("[" + text3 + "] Bill of Materials failed — " + message2);
					}
				}
				if (plotPackagesReportOptions.IncludeCutList && cutlistParts.Count > 0)
				{
					string text10 = SanitizeFileStem(text2 + " - Cut List");
					string text11 = Path.Combine(text, text10 + ".pdf");
					string message3 = string.Empty;
					List<string> cutlistWritten = new List<string>();
					Result val4;
					try
					{
						foreach (string path in PipeFittingsBomPdfCommand.PreviewCutlistOutputPaths(text11, cutlistParts, val))
						{
							TryDeleteExistingOutput(path);
						}

						val4 = PipeFittingsBomPdfCommand.ExportPackagePipeCutlistPdf(app, val, cutlistParts, text11, ref message3, plotHeader, cutlistWritten);
					}
					catch (Exception ex4)
					{
						val4 = (Result)(-1);
						message3 = ex4.Message;
					}
					if ((int)val4 == 0)
					{
						if (cutlistWritten.Count <= 1)
						{
							list.Add(text2 + " - Cut List (Plotted)");
						}
						else
						{
							foreach (string written in cutlistWritten)
							{
								list.Add(Path.GetFileNameWithoutExtension(written) + " (Plotted)");
							}
						}
					}
					else if ((int)val4 == 1)
					{
						stringBuilder.AppendLine("[" + text3 + "] Cut List skipped — " + message3);
					}
					else
					{
						stringBuilder.AppendLine("[" + text3 + "] Cut List failed — " + message3);
					}
				}
				if (plotPackagesReportOptions.IncludeWeldLog)
				{
					List<WeldLogExportRow> weldLogRows = CreateSpoolSheetsHandler.CollectWeldLogRowsForAssemblies(val, batch.AssemblyIds);
					string text12 = SanitizeFileStem(text2 + " - Weld Log");
					string text13 = Path.Combine(text, text12 + ".xlsx");
					if (weldLogRows.Count == 0)
					{
						stringBuilder.AppendLine("[" + text3 + "] No weld log entries (no S-Weld values) — skipped «" + text2 + " - Weld Log».");
					}
					else
					{
						try
						{
							TryDeleteExistingOutput(text13);
							WeldLogExcelExportService.Export(text13, weldLogRows);
							list.Add(text2 + " - Weld Log (Exported)");
						}
						catch (Exception ex5)
						{
							stringBuilder.AppendLine("[" + text3 + "] Weld Log failed: " + ex5.Message);
						}
					}
				}
				if (plotPackagesReportOptions.IncludeTigerStop || plotPackagesReportOptions.IncludePcfFiles)
				{
					SpoolingManagerSettings exportSettings = SpoolingManagerSettings.Load(productKind);
					if (plotPackagesReportOptions.IncludeTigerStop)
					{
						try
						{
							List<string> tigerFiles = TigerStopExportService.Export(val, cutlistParts, text2, text, exportSettings);
							if (tigerFiles.Count == 0)
							{
								stringBuilder.AppendLine("[" + text3 + "] TigerStop skipped — no Copper/PVC straight pipe in this package.");
							}
							else
							{
								foreach (string tigerPath in tigerFiles)
								{
									list.Add(Path.GetFileNameWithoutExtension(tigerPath) + " (Exported)");
								}
							}
						}
						catch (Exception exTiger)
						{
							stringBuilder.AppendLine("[" + text3 + "] TigerStop failed: " + exTiger.Message);
						}
					}
					if (plotPackagesReportOptions.IncludePcfFiles)
					{
						try
						{
							PcfExportService.ExportResult pcfResult = PcfExportService.Export(val, cutlistParts, text2, text, exportSettings);
							if (pcfResult.WrittenFiles.Count == 0)
							{
								stringBuilder.AppendLine("[" + text3 + "] PCF skipped — no exportable pipework in this package.");
							}
							else
							{
								foreach (string pcfPath in pcfResult.WrittenFiles)
								{
									list.Add(Path.GetFileNameWithoutExtension(pcfPath) + " (Exported)");
								}

								PipingSpecCatalogService.CatalogUpsertResult catalog = pcfResult.Catalog;
								if (!string.IsNullOrWhiteSpace(catalog.Error))
								{
									list.Add("Piping Specification Catalog (Failed: " + catalog.Error + ")");
								}
								else if (!string.IsNullOrWhiteSpace(catalog.PrimaryPath))
								{
									if (catalog.PrimaryAdded > 0)
									{
										list.Add(
											"Piping Specification Catalog (+"
											+ catalog.PrimaryAdded.ToString(CultureInfo.InvariantCulture)
											+ " new) (Updated)");
									}
									else
									{
										list.Add("Piping Specification Catalog (No new items)");
									}
								}
							}
						}
						catch (Exception exPcf)
						{
							stringBuilder.AppendLine("[" + text3 + "] PCF failed: " + exPcf.Message);
						}
					}
				}
			}
			}
			string message4 = BuildPlotPackagesCompletionBody(list, stringBuilder);
			if (pendingRequest.ExportToBoardroom)
			{
				try
				{
					string manifestPath = WriteBoardroomPackageManifest(val, pendingRequest, text, regularBranch);
					list.Add("boardroom-package.json (Written)");
					message4 = BuildPlotPackagesCompletionBody(list, stringBuilder)
						+ "\r\n\r\nBoardroom manifest:\r\n" + manifestPath;
				}
				catch (Exception exManifest)
				{
					stringBuilder.AppendLine("Boardroom manifest failed: " + exManifest.Message);
					message4 = BuildPlotPackagesCompletionBody(list, stringBuilder);
				}
			}
			ShowAlphaPlotPackagesDialog(app, toolWindowTitle, message4);
		}
		catch (Exception ex5)
		{
			ShowAlphaPlotPackagesDialog(app, toolWindowTitle, "Plot Packages stopped with an unexpected error:\r\n\r\n" + ex5.Message + "\r\n\r\n" + stringBuilder);
		}
	}

	private static void ShowAlphaPlotPackagesDialog(UIApplication app, string title, string message)
	{
		//IL_012a: Unknown result type (might be due to invalid IL or missing references)
		title = (string.IsNullOrWhiteSpace(title) ? "Plot Packages" : title.Trim());
		message = message ?? string.Empty;
		IntPtr revitHwnd = ((app != null) ? app.MainWindowHandle : IntPtr.Zero);
		try
		{
			Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread) ?? Dispatcher.CurrentDispatcher;
			if (dispatcher != null)
			{
				if (dispatcher.CheckAccess())
				{
					ShowWpf();
				}
				else
				{
					dispatcher.Invoke(ShowWpf, DispatcherPriority.Normal);
				}
			}
			else
			{
				ShowWpf();
			}
		}
		catch (Exception ex)
		{
			string text = message + "\r\n\r\n(WPF dialog: " + ex.Message + ")";
			NativeWindow nativeWindow = null;
			try
			{
				System.Windows.Forms.IWin32Window owner = null;
				if (revitHwnd != IntPtr.Zero)
				{
					nativeWindow = new NativeWindow();
					nativeWindow.AssignHandle(revitHwnd);
					owner = nativeWindow;
				}
				System.Windows.Forms.MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			catch
			{
				try
				{
					TaskDialog.Show(title, text);
				}
				catch
				{
				}
			}
			finally
			{
				nativeWindow?.ReleaseHandle();
			}
		}
		void ShowWpf()
		{
			OperationMessageWindow operationMessageWindow = new OperationMessageWindow(title, message);
			try
			{
				if (revitHwnd != IntPtr.Zero)
				{
					new WindowInteropHelper(operationMessageWindow).Owner = revitHwnd;
				}
			}
			catch
			{
			}
			operationMessageWindow.ShowDialog();
		}
	}

	private static void TryDeleteExistingOutput(string path)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(path))
			{
				path = Path.GetFullPath(path);
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}
		catch
		{
		}
	}

	private static List<(FabricationPart Part, string AssemblyName)> CollectFabricationPartsWithAssemblyNames(Document doc, IEnumerable<ElementId> assemblyIds)
	{
		List<(FabricationPart Part, string AssemblyName)> list = new List<(FabricationPart Part, string AssemblyName)>();
		if (doc == null || assemblyIds == null)
		{
			return list;
		}

		HashSet<long> seenMembers = new HashSet<long>();
		foreach (ElementId item in assemblyIds.Distinct())
		{
			if (item == (ElementId)null || item == ElementId.InvalidElementId)
			{
				continue;
			}

			Element element = doc.GetElement(item);
			AssemblyInstance assembly = element as AssemblyInstance;
			if (assembly == null)
			{
				continue;
			}

			string assemblyName = (AssemblyDisplayName.Get(assembly) ?? string.Empty).Trim();
			foreach (ElementId memberId in assembly.GetMemberIds())
			{
				if (memberId == (ElementId)null || memberId == ElementId.InvalidElementId || !seenMembers.Add(memberId.Value))
				{
					continue;
				}

				Element member = doc.GetElement(memberId);
				FabricationPart part = member as FabricationPart;
				if (part?.Category != null && ((Element)part).Category.Id.Value == -2008208)
				{
					list.Add((part, assemblyName));
				}
			}
		}

		return list;
	}

	private static List<FabricationPart> CollectFabricationPartsForAssemblies(Document doc, IEnumerable<ElementId> assemblyIds)
	{
		List<FabricationPart> list = new List<FabricationPart>();
		if (doc == null || assemblyIds == null)
		{
			return list;
		}
		HashSet<long> hashSet = new HashSet<long>();
		foreach (ElementId item in assemblyIds.Distinct())
		{
			if (item == (ElementId)null || item == ElementId.InvalidElementId)
			{
				continue;
			}
			Element element = doc.GetElement(item);
			AssemblyInstance val = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			if (val == null)
			{
				continue;
			}
			foreach (ElementId memberId in val.GetMemberIds())
			{
				if (!(memberId == (ElementId)null) && !(memberId == ElementId.InvalidElementId) && hashSet.Add(memberId.Value))
				{
					Element element2 = doc.GetElement(memberId);
					FabricationPart val2 = (FabricationPart)(object)((element2 is FabricationPart) ? element2 : null);
					if (((val2 != null) ? ((Element)val2).Category : null) != null && ((Element)val2).Category.Id.Value == -2008208)
					{
						list.Add(val2);
					}
				}
			}
		}
		return list;
	}

	private static string BuildPlotPackagesCompletionBody(List<string> plottedLines, StringBuilder detailSummary)
	{
		string text = ((plottedLines != null && plottedLines.Count > 0) ? string.Join(Environment.NewLine, plottedLines) : string.Empty);
		string text2 = ((detailSummary != null && detailSummary.Length > 0) ? detailSummary.ToString().Trim() : string.Empty);
		if (text.Length > 0 && text2.Length > 0)
		{
			return text + Environment.NewLine + Environment.NewLine + text2;
		}
		if (text.Length > 0)
		{
			return text;
		}
		if (text2.Length <= 0)
		{
			return "Nothing was plotted.";
		}
		return text2;
	}

	private static List<PipeFittingsBomPdfCommand.PackageAssemblyListRow> BuildOrderedAssemblyListRowsForPackage(Document doc, IEnumerable<ElementId> assemblyIds, bool regularBranch)
	{
		List<PipeFittingsBomPdfCommand.PackageAssemblyListRow> list = new List<PipeFittingsBomPdfCommand.PackageAssemblyListRow>();
		if (doc == null || assemblyIds == null)
		{
			return list;
		}
		HashSet<long> hashSet = new HashSet<long>();
		List<ElementId> assemblyInstanceIds = assemblyIds.Where((ElementId id) => id != (ElementId)null && id != ElementId.InvalidElementId).Distinct().ToList();
		Dictionary<ElementId, ViewSheet> assemblyToSheet = CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(doc, regularBranch, assemblyInstanceIds);
		foreach (ViewSheet item in CollectDistinctSpoolSheetsFromMap(assemblyToSheet))
		{
			ElementId val = TryResolveAssemblyIdForSpoolSheet(item, assemblyToSheet);
			AssemblyInstance val2 = val != null && val != ElementId.InvalidElementId
				? doc.GetElement(val) as AssemblyInstance
				: null;
			if (val != (ElementId)null)
			{
				hashSet.Add(val.Value);
			}
			string text = ((val2 != null) ? AssemblyDisplayName.Get(val2) : FormatSheetFallbackLabel(item));
			text = (text ?? string.Empty).Trim();
			if (text.Length > 0)
			{
				list.Add(new PipeFittingsBomPdfCommand.PackageAssemblyListRow(val, text));
			}
		}
		List<Tuple<ElementId, string>> list2 = new List<Tuple<ElementId, string>>();
		foreach (ElementId assemblyId in assemblyIds)
		{
			if (assemblyId == (ElementId)null || assemblyId == ElementId.InvalidElementId || hashSet.Contains(assemblyId.Value))
			{
				continue;
			}
			Element element = doc.GetElement(assemblyId);
			AssemblyInstance val3 = (AssemblyInstance)(object)((element is AssemblyInstance) ? element : null);
			if (val3 != null)
			{
				string text2 = (AssemblyDisplayName.Get(val3) ?? string.Empty).Trim();
				if (text2.Length > 0)
				{
					list2.Add(Tuple.Create<ElementId, string>(assemblyId, text2));
				}
			}
		}
		list2.Sort((Tuple<ElementId, string> a, Tuple<ElementId, string> b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
		foreach (Tuple<ElementId, string> item2 in list2)
		{
			list.Add(new PipeFittingsBomPdfCommand.PackageAssemblyListRow(item2.Item1, item2.Item2));
		}
		return list;
	}

	private static ElementId TryResolveAssemblyIdForSpoolSheet(ViewSheet sheet, Dictionary<ElementId, ViewSheet> assemblyToSheet)
	{
		if (sheet == null || assemblyToSheet == null || assemblyToSheet.Count == 0)
		{
			return null;
		}
		long value = ((Element)sheet).Id.Value;
		foreach (KeyValuePair<ElementId, ViewSheet> item in assemblyToSheet)
		{
			ViewSheet value2 = item.Value;
			if (value2 != null && ((Element)value2).Id.Value == value)
			{
				return item.Key;
			}
		}
		return null;
	}

	private static string FormatSheetFallbackLabel(ViewSheet sheet)
	{
		if (sheet == null)
		{
			return string.Empty;
		}
		string text = (sheet.SheetNumber ?? string.Empty).Trim();
		string text2 = (((Element)sheet).Name ?? string.Empty).Trim();
		if (text.Length > 0 && text2.Length > 0)
		{
			return text + " — " + text2;
		}
		if (text.Length > 0)
		{
			return text;
		}
		return text2;
	}

	private static List<ViewSheet> CollectDistinctSpoolSheetsFromMap(Dictionary<ElementId, ViewSheet> assemblyToSheet)
	{
		List<ViewSheet> list = new List<ViewSheet>();
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		if (assemblyToSheet == null || assemblyToSheet.Count == 0)
		{
			return list;
		}
		foreach (ViewSheet value in assemblyToSheet.Values)
		{
			if (value != null && !hashSet.Contains(((Element)value).Id))
			{
				hashSet.Add(((Element)value).Id);
				list.Add(value);
			}
		}
		return list.OrderBy((ViewSheet s) => s.SheetNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy((ViewSheet s) => ((Element)s).Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static string ResolvePackageValueForBatch(Document doc, PlotPackageBatch batch)
	{
		if (batch == null)
		{
			return string.Empty;
		}

		string fromLabel = (batch.PackageLabel ?? string.Empty).Trim();
		if (doc == null || batch.AssemblyIds == null)
		{
			return fromLabel;
		}

		foreach (ElementId assemblyId in batch.AssemblyIds)
		{
			AssemblyInstance assembly = doc.GetElement(assemblyId) as AssemblyInstance;
			if (assembly == null)
			{
				continue;
			}

			string package = FabricationSavantParameterSync.TryGetAssemblyPackageValue(doc, assembly);
			if (!string.IsNullOrWhiteSpace(package))
			{
				return package.Trim();
			}
		}

		return fromLabel;
	}

	private static List<ViewSheet> CollectDistinctSpoolSheets(Document doc, IEnumerable<ElementId> assemblyIds, bool regularBranch)
	{
		List<ElementId> assemblyInstanceIds = ((assemblyIds != null) ? assemblyIds.Where((ElementId id) => id != (ElementId)null && id != ElementId.InvalidElementId).Distinct().ToList() : new List<ElementId>());
		return CollectDistinctSpoolSheetsFromMap(CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(doc, regularBranch, assemblyInstanceIds));
	}

	private static void EnsureTrackingQrBeforePlot(
		Document doc,
		IEnumerable<ElementId> assemblyIds,
		bool regularBranch,
		SpoolingManagerKind productKind)
	{
		if (doc == null || assemblyIds == null)
		{
			return;
		}

		SpoolingManagerSettings settings = SpoolingManagerSettings.Load(productKind);
		if (settings == null || !settings.PlaceTrackingQrOnSpoolSheets)
		{
			return;
		}

		Dictionary<ElementId, ViewSheet> sheetsByAssembly =
			CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(doc, regularBranch, assemblyIds.ToList());
		if (sheetsByAssembly == null || sheetsByAssembly.Count == 0)
		{
			return;
		}

		List<(ViewSheet Sheet, AssemblyInstance Assembly)> pairs = new List<(ViewSheet, AssemblyInstance)>();
		foreach (KeyValuePair<ElementId, ViewSheet> pair in sheetsByAssembly)
		{
			if (pair.Value == null || pair.Key == null || pair.Key == ElementId.InvalidElementId)
			{
				continue;
			}

			AssemblyInstance assembly = doc.GetElement(pair.Key) as AssemblyInstance;
			if (assembly == null)
			{
				continue;
			}

			pairs.Add((pair.Value, assembly));
		}

		if (pairs.Count == 0)
		{
			return;
		}

		using (Transaction tx = new Transaction(doc, "Spooling Savant 3.0: Ensure tracking QR"))
		{
			tx.Start();
			try
			{
				SpoolSheetQrCodeService.EnsureOnSheets(doc, pairs, settings);
				tx.Commit();
			}
			catch
			{
				tx.RollBack();
			}
		}
	}

	private static void PlotSheetsToPdf(Document doc, IList<ViewSheet> sheets, string outputPdfFullPath)
	{
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Expected O, but got Unknown
		if (doc == null || sheets == null || sheets.Count == 0)
		{
			throw new ArgumentException("No sheets to plot.");
		}
		string directoryName = Path.GetDirectoryName(outputPdfFullPath);
		if (string.IsNullOrWhiteSpace(directoryName))
		{
			throw new ArgumentException("PDF output folder is invalid.");
		}
		Directory.CreateDirectory(directoryName);
		directoryName = Path.GetFullPath(directoryName);
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(Path.GetFileName(outputPdfFullPath));
		if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
		{
			throw new ArgumentException("PDF output file name is invalid.");
		}
		IList<ElementId> list = sheets.Select((ViewSheet s) => ((Element)s).Id).Distinct(new ElementIdEqualityComparer()).ToList();
		PDFExportOptions val = new PDFExportOptions
		{
			Combine = true,
			FileName = fileNameWithoutExtension,
			PaperFormat = (ExportPaperFormat)0,
			PaperOrientation = (PageOrientationType)2
		};
		if (!doc.Export(directoryName, list, val))
		{
			throw new InvalidOperationException("Revit could not export spool sheets to PDF (Export returned false). Try exporting one sheet manually via File → Export → PDF to confirm PDF export is allowed in this project.");
		}
		string path = Path.Combine(directoryName, fileNameWithoutExtension + ".pdf");
		path = Path.GetFullPath(path);
		if (!File.Exists(path))
		{
			string[] files = Directory.GetFiles(directoryName, fileNameWithoutExtension + "*.pdf");
			if (files.Length != 1 || !File.Exists(files[0]))
			{
				throw new InvalidOperationException("Revit reported PDF export success but the file was not found:\r\n" + path);
			}
		}
	}

	private static string WriteBoardroomPackageManifest(
		Document doc,
		PlotPackagesRequest request,
		string outputFolder,
		bool regularBranch)
	{
		string path = Path.Combine(outputFolder, "boardroom-package.json");
		var sb = new StringBuilder();
		sb.AppendLine("{");
		sb.AppendLine("  \"schema\": \"bim-boardroom-package-v1\",");
		sb.AppendLine("  \"exportedAt\": \"" + EscapeJson(DateTime.Now.ToString("o")) + "\",");
		sb.AppendLine("  \"targets\": [\"fab\", \"shipping\"],");
		sb.AppendLine("  \"boardroomProject\": {");
		sb.AppendLine("    \"id\": \"" + EscapeJson(request.BoardroomProjectId) + "\",");
		sb.AppendLine("    \"name\": \"" + EscapeJson(request.BoardroomProjectName) + "\",");
		sb.AppendLine("    \"clientName\": \"" + EscapeJson(request.BoardroomClientName) + "\",");
		sb.AppendLine("    \"jobCode\": \"" + EscapeJson(request.BoardroomJobCode) + "\"");
		sb.AppendLine("  },");
		sb.AppendLine("  \"boardroomTask\": {");
		sb.AppendLine("    \"id\": \"" + EscapeJson(request.BoardroomTaskId) + "\",");
		sb.AppendLine("    \"taskNumber\": \"" + EscapeJson(request.BoardroomTaskNumber) + "\",");
		sb.AppendLine("    \"title\": \"" + EscapeJson(request.BoardroomTaskTitle) + "\"");
		sb.AppendLine("  },");
		sb.AppendLine("  \"packages\": [");

		List<PlotPackageBatch> batches = request.Batches ?? new List<PlotPackageBatch>();
		for (int i = 0; i < batches.Count; i++)
		{
			PlotPackageBatch batch = batches[i];
			string label = string.IsNullOrWhiteSpace(batch?.PackageLabel) ? "(No package)" : batch.PackageLabel.Trim();
			List<ElementId> ids = batch?.AssemblyIds ?? new List<ElementId>();
			List<ElementId> lookupIds = ids
				.Where((ElementId id) => id != (ElementId)null && id != ElementId.InvalidElementId)
				.Distinct()
				.ToList();
			Dictionary<ElementId, ViewSheet> assemblyToSheet =
				CreateSpoolSheetsHandler.FindSpoolSheetsForAssemblies(doc, regularBranch, lookupIds);

			sb.AppendLine("    {");
			sb.AppendLine("      \"sPackage\": \"" + EscapeJson(label) + "\",");
			sb.AppendLine("      \"assemblies\": [");

			for (int a = 0; a < ids.Count; a++)
			{
				ElementId id = ids[a];
				AssemblyInstance assembly = doc?.GetElement(id) as AssemblyInstance;
				string name = assembly != null ? (AssemblyDisplayName.Get(assembly) ?? string.Empty) : string.Empty;
				long idValue = id != null ? id.Value : 0L;
				string sheetName = string.Empty;
				string sheetNumber = string.Empty;
				ViewSheet sheet = null;
				if (id != null && assemblyToSheet != null && assemblyToSheet.TryGetValue(id, out sheet) && sheet != null)
				{
					sheetName = (((Element)sheet).Name ?? string.Empty).Trim();
					sheetNumber = (sheet.SheetNumber ?? string.Empty).Trim();
				}
				string qr = "SSV3|P=" + label + "|A=" + name;
				sb.Append("        { \"revitElementId\": " + idValue.ToString(CultureInfo.InvariantCulture)
					+ ", \"name\": \"" + EscapeJson(name) + "\""
					+ ", \"sheetName\": \"" + EscapeJson(sheetName) + "\""
					+ ", \"sheetNumber\": \"" + EscapeJson(sheetNumber) + "\""
					+ ", \"qr\": \"" + EscapeJson(qr) + "\" }");
				sb.AppendLine(a < ids.Count - 1 ? "," : string.Empty);
			}

			sb.AppendLine("      ]");
			sb.Append("    }");
			sb.AppendLine(i < batches.Count - 1 ? "," : string.Empty);
		}

		sb.AppendLine("  ],");
		sb.AppendLine("  \"files\": [");
		HashSet<string> packageLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (PlotPackageBatch batch in batches)
		{
			string label = string.IsNullOrWhiteSpace(batch?.PackageLabel) ? string.Empty : batch.PackageLabel.Trim();
			if (label.Length > 0)
			{
				packageLabels.Add(label);
			}
		}
		List<string> fileNames = Directory.Exists(outputFolder)
			? Directory.GetFiles(outputFolder)
				.Select(Path.GetFileName)
				.Where(name =>
					!string.IsNullOrWhiteSpace(name)
					&& !string.Equals(name, "boardroom-package.json", StringComparison.OrdinalIgnoreCase)
					&& packageLabels.Any(pkg => FileBelongsToSPackage(name, pkg)))
				.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
				.ToList()
			: new List<string>();
		for (int f = 0; f < fileNames.Count; f++)
		{
			string fileName = fileNames[f];
			string ext = (Path.GetExtension(fileName) ?? string.Empty).TrimStart('.').ToLowerInvariant();
			sb.Append("    { \"fileName\": \"" + EscapeJson(fileName) + "\", \"type\": \"" + EscapeJson(ext) + "\" }");
			sb.AppendLine(f < fileNames.Count - 1 ? "," : string.Empty);
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");
		File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return path;
	}

	private static bool FileBelongsToSPackage(string fileName, string sPackage)
	{
		string pkg = (sPackage ?? string.Empty).Trim();
		string name = (fileName ?? string.Empty).Trim();
		if (pkg.Length == 0 || name.Length == 0)
		{
			return false;
		}

		if (string.Equals(name, pkg, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (name.StartsWith(pkg + " - ", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (name.StartsWith(pkg + "-", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return false;
	}

	private static string EscapeJson(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}

		return value
			.Replace("\\", "\\\\")
			.Replace("\"", "\\\"")
			.Replace("\r", "\\r")
			.Replace("\n", "\\n")
			.Replace("\t", "\\t");
	}

	private static string SanitizeFileStem(string label)
	{
		string text = (label ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			text = "Package";
		}
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		foreach (char oldChar in invalidFileNameChars)
		{
			text = text.Replace(oldChar, '_');
		}
		if (text.Length == 0)
		{
			text = "Package";
		}
		return text;
	}

	public string GetName()
	{
		return "SS Manager: Plot packages";
	}
}
