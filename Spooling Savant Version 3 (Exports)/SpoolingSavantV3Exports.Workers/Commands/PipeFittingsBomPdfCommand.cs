// Target: .NET Framework 4.8
// Assembly: ABMEP.Work.dll

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ABMEP.Addins.Hangers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

using SpoolingSavantV3Exports.Workers;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;
using SpoolingSavantV3Exports.Workers.UI;

using Wpf = System.Windows.Controls;

// Avoid collisions with iTextSharp types named Document / Element.
using RvtBuiltInCategory = Autodesk.Revit.DB.BuiltInCategory;
using RvtDocument = Autodesk.Revit.DB.Document;
using RvtElementId = Autodesk.Revit.DB.ElementId;
using RvtStorageType = Autodesk.Revit.DB.StorageType;

// iTextSharp
using iTextSharp.text;
using iTextSharp.text.pdf;
using PdfAlign = iTextSharp.text.Element;
using PdfDocument = iTextSharp.text.Document;

namespace ABMEP.Work
{
    [Transaction(TransactionMode.Manual)]
    public class PipeFittingsBomPdfCommand : IExternalCommand
    {
        private const string PARAM_SOURCE_PRODUCT_ENTRY = "Product Entry";
        private const string PARAM_S_SIZE = "S-Size";
        private const string PARAM_SOURCE_MATERIAL = "Part Material";
        private const string PARAM_S_MATERIAL = "S-Material";
        private const string PARAM_DESCRIPTION = "Product Long Description";
        private const string PARAM_LENGTH = "Length";
        private const string DEFAULT_BOM_TITLE = "Pipe & Fittings Bill of Materials";
        private const string PipeBomSaveFileSuffix = "Pipe BOM";
        private static readonly string PipeBomLogoPerProjectStorePath = Path.Combine(
            @"C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)\SpoolingSavantV3Exports.Workers",
            "Settings",
            "PipeBomLogoPerProject.xml");

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            RvtDocument doc = uidoc?.Document;
            if (doc == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            IList<Reference> refs;
            try
            {
                refs = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new FabricationPipeSelectionFilter(),
                    "Select MEP Fabrication pipe and fittings for the BOM.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            if (refs == null || refs.Count == 0)
            {
                SsSavantMessageBox.Show("No elements selected.", "Pipe BOM");
                return Result.Cancelled;
            }

            List<FabricationPart> parts = refs
                .Select(r => doc.GetElement(r) as FabricationPart)
                .Where(fp =>
                    fp != null &&
                    fp.Category != null &&
                    fp.Category.Id.Value == (long)RvtBuiltInCategory.OST_FabricationPipework)
                .ToList();

            if (parts.Count == 0)
            {
                SsSavantMessageBox.Show(
                    "Selected elements do not include any MEP Fabrication Pipework.", "Pipe BOM");
                return Result.Cancelled;
            }

            return RunForFabricationParts(data.Application, doc, parts, ref message);
        }

        /// <summary>
        /// Non-interactive BOM export for Plot Packages: fills header fields from the document and omits completion UI.
        /// </summary>
        internal static Result ExportFabricationPartsBomPdf(
            UIApplication uiApp,
            RvtDocument doc,
            List<FabricationPart> parts,
            string savePathFullPdf,
            ref string message,
            PlotPackageHeaderInfo header = null)
        {
            if (doc == null || uiApp == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            if (string.IsNullOrWhiteSpace(savePathFullPdf))
            {
                message = "BOM save path is empty.";
                return Result.Failed;
            }

            if (parts == null || parts.Count == 0)
            {
                message = "No MEP fabrication pipework in selected assemblies.";
                return Result.Cancelled;
            }

            string pdfStem = Path.GetFileNameWithoutExtension(savePathFullPdf.Trim());

            string logo = TryLoadPipeBomLogoPath(doc);
            var options = new BomOptions
            {
                ProjectName = GetDefaultProjectName(doc),
                Level = GetDefaultLevelName(doc),
                Area = string.Empty,
                CreatedBy = Environment.UserName,
                SavePath = savePathFullPdf.Trim(),
                BomTitle = string.IsNullOrWhiteSpace(pdfStem) ? string.Empty : pdfStem,
                IncludeGaskets = false,
                IncludeBoltKits = false,
                LogoPath = string.IsNullOrWhiteSpace(logo) ? string.Empty : logo.Trim()
            };
            ApplyPlotPackageHeader(options, header);

            return RunForFabricationPartsWithOptions(uiApp, doc, parts, options, showCompletionDialog: false, ref message);
        }

        /// <summary>
        /// Optional header overrides for Plot Packages PDFs (Project / Created By / Date).
        /// </summary>
        internal sealed class PlotPackageHeaderInfo
        {
            public string ProjectName { get; set; }
            public string CreatedBy { get; set; }
            public string DateText { get; set; }
        }

        private static void ApplyPlotPackageHeader(BomOptions options, PlotPackageHeaderInfo header)
        {
            if (options == null || header == null)
                return;

            if (!string.IsNullOrWhiteSpace(header.ProjectName))
                options.ProjectName = header.ProjectName.Trim();
            if (header.CreatedBy != null)
                options.CreatedBy = header.CreatedBy.Trim();
            if (header.DateText != null)
                options.DateText = header.DateText.Trim();
        }

        /// <summary>
        /// One row for the Plot Packages assembly list PDF: optional assembly id (for weight from members) and display label.
        /// </summary>
        internal readonly struct PackageAssemblyListRow
        {
            public PackageAssemblyListRow(RvtElementId assemblyId, string label)
            {
                AssemblyId = assemblyId;
                Label = label ?? string.Empty;
            }

            public RvtElementId AssemblyId { get; }
            public string Label { get; }
        }

        /// <summary>
        /// Non-interactive assembly list for Plot Packages: same PDF chrome as the fabrication BOM (header title, fonts, logo slot).
        /// </summary>
        internal static Result ExportPackageAssemblyListPdf(
            UIApplication uiApp,
            RvtDocument doc,
            IList<PackageAssemblyListRow> assemblyRows,
            string savePathFullPdf,
            ref string message,
            PlotPackageHeaderInfo header = null)
        {
            if (doc == null || uiApp == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            if (string.IsNullOrWhiteSpace(savePathFullPdf))
            {
                message = "Assembly List save path is empty.";
                return Result.Failed;
            }

            if (assemblyRows == null || assemblyRows.Count == 0)
            {
                message = "No spools to list for this package.";
                return Result.Cancelled;
            }

            string pdfStem = Path.GetFileNameWithoutExtension(savePathFullPdf.Trim());

            string logo = TryLoadPipeBomLogoPath(doc);
            var options = new BomOptions
            {
                ProjectName = GetDefaultProjectName(doc),
                Level = GetDefaultLevelName(doc),
                Area = string.Empty,
                CreatedBy = Environment.UserName,
                SavePath = savePathFullPdf.Trim(),
                BomTitle = string.IsNullOrWhiteSpace(pdfStem) ? string.Empty : pdfStem,
                IncludeGaskets = false,
                IncludeBoltKits = false,
                LogoPath = string.IsNullOrWhiteSpace(logo) ? string.Empty : logo.Trim()
            };
            ApplyPlotPackageHeader(options, header);

            using (SsSavantHotloadDependencyScope.ForWorkerAssembly())
            {
                SsSavantPdfDependencyWarmup.EnsurePdfDependenciesLoaded();

                try
                {
                    ExportAssemblyListToPdf(options.SavePath, options, assemblyRows, doc);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }

        /// <summary>
        /// Non-interactive straight-pipe cut list for Plot Packages: landscape 8.5×11 in, same header/title treatment as the Pipe BOM PDFs.
        /// One PDF per distinct pipe description (Product Long Description). Rows include Assembly Name and sort Description → Assembly Name.
        /// </summary>
        internal static Result ExportPackagePipeCutlistPdf(
            UIApplication uiApp,
            RvtDocument doc,
            List<(FabricationPart Part, string AssemblyName)> partsWithAssembly,
            string savePathFullPdf,
            ref string message,
            PlotPackageHeaderInfo header = null,
            List<string> writtenPaths = null)
        {
            if (doc == null || uiApp == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            if (string.IsNullOrWhiteSpace(savePathFullPdf))
            {
                message = "Cut List save path is empty.";
                return Result.Failed;
            }

            if (partsWithAssembly == null || partsWithAssembly.Count == 0)
            {
                message = "No MEP fabrication pipework in selected assemblies.";
                return Result.Cancelled;
            }

            string pdfStem = Path.GetFileNameWithoutExtension(savePathFullPdf.Trim());
            string directory = Path.GetDirectoryName(savePathFullPdf.Trim()) ?? string.Empty;

            string logo = TryLoadPipeBomLogoPath(doc);
            var options = new BomOptions
            {
                ProjectName = GetDefaultProjectName(doc),
                Level = GetDefaultLevelName(doc),
                Area = string.Empty,
                CreatedBy = Environment.UserName,
                SavePath = savePathFullPdf.Trim(),
                BomTitle = string.IsNullOrWhiteSpace(pdfStem) ? string.Empty : pdfStem,
                IncludeGaskets = false,
                IncludeBoltKits = false,
                LogoPath = string.IsNullOrWhiteSpace(logo) ? string.Empty : logo.Trim()
            };
            ApplyPlotPackageHeader(options, header);

            using (SsSavantHotloadDependencyScope.ForWorkerAssembly())
            {
                SsSavantPdfDependencyWarmup.EnsurePdfDependenciesLoaded();

                List<FabricationPart> syncParts = partsWithAssembly
                    .Select(x => x.Part)
                    .Where(p => p != null)
                    .Distinct()
                    .ToList();

                try
                {
                    EnsureBomParametersAndSync(doc, syncParts);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    return Result.Failed;
                }

                try
                {
                    writtenPaths?.Clear();
                    List<(string Description, List<(FabricationPart Part, string AssemblyName)> Members)> byDescription =
                        BuildCutlistGroupsByDescription(partsWithAssembly, doc);
                    if (byDescription.Count == 0)
                    {
                        message = "No straight pipe pieces were found for the cut list.";
                        return Result.Cancelled;
                    }

                    for (int i = 0; i < byDescription.Count; i++)
                    {
                        string description = byDescription[i].Description;
                        List<(FabricationPart Part, string AssemblyName)> members = byDescription[i].Members;
                        string path = ResolveCutlistOutputPath(savePathFullPdf.Trim(), pdfStem, directory, description, byDescription.Count);

                        options.SavePath = path;
                        options.BomTitle = Path.GetFileNameWithoutExtension(path);
                        ExportPipeCutlistToPdf(path, options, members, doc);
                        writtenPaths?.Add(path);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    message = ex.Message;
                    return Result.Cancelled;
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }

        /// <summary>
        /// Back-compat overload: cut list without assembly labels (single PDF, no Assembly Name column sort depth).
        /// </summary>
        internal static Result ExportPackagePipeCutlistPdf(
            UIApplication uiApp,
            RvtDocument doc,
            List<FabricationPart> parts,
            string savePathFullPdf,
            ref string message,
            PlotPackageHeaderInfo header = null)
        {
            List<(FabricationPart Part, string AssemblyName)> tagged = (parts ?? new List<FabricationPart>())
                .Where(p => p != null)
                .Select(p => (p, string.Empty))
                .ToList();
            return ExportPackagePipeCutlistPdf(uiApp, doc, tagged, savePathFullPdf, ref message, header, null);
        }

        private static List<(string Description, List<(FabricationPart Part, string AssemblyName)> Members)> BuildCutlistGroupsByDescription(
            List<(FabricationPart Part, string AssemblyName)> partsWithAssembly,
            RvtDocument doc)
        {
            var map = new Dictionary<string, List<(FabricationPart Part, string AssemblyName)>>(StringComparer.OrdinalIgnoreCase);
            foreach ((FabricationPart Part, string AssemblyName) item in partsWithAssembly)
            {
                if (item.Part == null || !IsStraightPipeCutlistMember(item.Part))
                    continue;

                string description = (GetParamString(item.Part, PARAM_DESCRIPTION, doc) ?? string.Empty).Trim();
                if (!map.TryGetValue(description, out List<(FabricationPart Part, string AssemblyName)> list))
                {
                    list = new List<(FabricationPart Part, string AssemblyName)>();
                    map[description] = list;
                }

                list.Add(item);
            }

            return map
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        /// <summary>Exposes cut-list description groups so Plot Packages can pre-delete output paths.</summary>
        internal static List<(string Description, List<(FabricationPart Part, string AssemblyName)> Members)> PreviewCutlistDescriptionGroups(
            List<(FabricationPart Part, string AssemblyName)> partsWithAssembly,
            RvtDocument doc)
        {
            return BuildCutlistGroupsByDescription(partsWithAssembly ?? new List<(FabricationPart Part, string AssemblyName)>(), doc);
        }

        internal static List<string> PreviewCutlistOutputPaths(
            string savePathFullPdf,
            List<(FabricationPart Part, string AssemblyName)> partsWithAssembly,
            RvtDocument doc)
        {
            List<string> paths = new List<string>();
            string trimmed = (savePathFullPdf ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return paths;

            string pdfStem = Path.GetFileNameWithoutExtension(trimmed);
            string directory = Path.GetDirectoryName(trimmed) ?? string.Empty;
            List<(string Description, List<(FabricationPart Part, string AssemblyName)> Members)> groups =
                PreviewCutlistDescriptionGroups(partsWithAssembly, doc);
            foreach ((string Description, List<(FabricationPart Part, string AssemblyName)> Members) group in groups)
            {
                paths.Add(ResolveCutlistOutputPath(trimmed, pdfStem, directory, group.Description, groups.Count));
            }

            return paths;
        }

        private static string ResolveCutlistOutputPath(
            string savePathFullPdf,
            string pdfStem,
            string directory,
            string description,
            int groupCount)
        {
            if (groupCount <= 1)
                return savePathFullPdf;

            string suffix = string.IsNullOrWhiteSpace(description) ? "Pipe" : description.Trim();
            return Path.Combine(directory, MakeSafeFileName(pdfStem + " - " + suffix) + ".pdf");
        }

        /// <summary>
        /// Same BOM workflow as the Pipe BOM ribbon command after fabrication pipework parts are resolved.
        /// </summary>
        internal static Result RunForFabricationParts(
            UIApplication uiApp,
            RvtDocument doc,
            List<FabricationPart> parts,
            ref string message)
        {
            if (doc == null || uiApp == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            if (parts == null || parts.Count == 0)
            {
                SsSavantMessageBox.Show(
                    "No MEP fabrication pipework was found for this scope.", "Pipe BOM");
                return Result.Cancelled;
            }

            BomOptions options = PromptForBomOptions(doc);
            if (options == null)
                return Result.Cancelled;

            return RunForFabricationPartsWithOptions(uiApp, doc, parts, options, showCompletionDialog: true, ref message);
        }

        private static Result RunForFabricationPartsWithOptions(
            UIApplication uiApp,
            RvtDocument doc,
            List<FabricationPart> parts,
            BomOptions options,
            bool showCompletionDialog,
            ref string message)
        {
            if (parts == null || parts.Count == 0)
            {
                message = "No fabrication pipework in scope.";
                return Result.Cancelled;
            }

            if (options == null)
            {
                message = "BOM options are missing.";
                return Result.Failed;
            }

            using (SsSavantHotloadDependencyScope.ForWorkerAssembly())
            {
                SsSavantPdfDependencyWarmup.EnsurePdfDependenciesLoaded();

                try
                {
                    EnsureBomParametersAndSync(doc, parts);
                }
                catch (Exception ex)
                {
                    if (showCompletionDialog)
                    {
                        SsSavantMessageBox.Show(
                            "Failed to prepare S-Size (from Product Entry) and S-Material:\n" + ex.Message, "Pipe BOM");
                    }

                    message = ex.Message;
                    return Result.Failed;
                }

                Dictionary<BomKey, BomAgg> groups = BuildBomGroups(doc, parts, options);

                if (groups.Count == 0)
                {
                    if (showCompletionDialog)
                    {
                        SsSavantMessageBox.Show(
                            "No valid pipe/fitting data found after filtering the selected parts.", "Pipe BOM");
                    }

                    message = "No valid pipe/fitting data after filtering.";
                    return Result.Cancelled;
                }

                try
                {
                    ExportBomToPdf(options.SavePath, options, groups);
                }
                catch (Exception ex)
                {
                    if (showCompletionDialog)
                    {
                        SsSavantMessageBox.Show(
                            "Failed to create PDF:\n" + ex.Message, "Pipe BOM");
                    }

                    message = ex.Message;
                    return Result.Failed;
                }

                if (showCompletionDialog)
                {
                    SsSavantCompletionDialog.Show("Pipe BOM",
                        "Bill of Materials created:\n" + options.SavePath);
                }

                return Result.Succeeded;
            }
        }

        // =====================================================================
        // BUILD / PARAM HELPERS
        // =====================================================================

        private static Dictionary<BomKey, BomAgg> BuildBomGroups(RvtDocument doc, List<FabricationPart> parts, BomOptions options)
        {
            var groups = new Dictionary<BomKey, BomAgg>();

            foreach (FabricationPart fp in parts)
            {
                if (FabricationPartClassification.IsWeldPart(fp))
                    continue;

                if (FabricationPartClassification.IsJointPart(fp))
                    continue;

                bool isGasket = FabricationPartClassification.IsGasketPart(fp);
                bool isBoltKit = FabricationPartClassification.IsBoltKitPart(fp);

                if (isGasket && !options.IncludeGaskets)
                    continue;

                if (isBoltKit && !options.IncludeBoltKits)
                    continue;

                // Shared classification: flanges = fittings (QTY), valves = priority 2 — not Length-row pipes.
                int sortPriority = FabricationPartClassification.GetFabricationSortPriority(fp, doc);
                bool lengthRow = sortPriority == 0 && !isGasket && !isBoltKit;
                // Within fittings / valves, alphabetical (or alphanumerical) by description.
                string sortKey = lengthRow
                    ? GetFabricationItemGroupingKey(fp)
                    : (GetParamString(fp, PARAM_DESCRIPTION, doc) ?? string.Empty).Trim();

                var key = new BomKey
                {
                    Size = (GetParamString(fp, PARAM_S_SIZE, doc) ?? string.Empty).Trim(),
                    Description = (GetParamString(fp, PARAM_DESCRIPTION, doc) ?? string.Empty).Trim(),
                    Material = (GetParamString(fp, PARAM_S_MATERIAL, doc) ?? string.Empty).Trim(),
                    IsLengthRow = lengthRow
                };

                if (!groups.TryGetValue(key, out BomAgg agg))
                {
                    agg = new BomAgg
                    {
                        SortPriority = sortPriority,
                        SortKey = sortKey
                    };
                    groups[key] = agg;
                }
                else
                {
                    if (sortPriority < agg.SortPriority)
                        agg.SortPriority = sortPriority;

                    if (string.Compare(sortKey, agg.SortKey, StringComparison.OrdinalIgnoreCase) < 0)
                        agg.SortKey = sortKey;
                }

                agg.Count++;
                agg.TotalLengthFt += GetPipeRunLengthFeet(fp, lengthRow);
            }

            return groups;
        }

        private static void EnsureBomParametersAndSync(
            RvtDocument doc,
            List<FabricationPart> parts)
        {
            using (Transaction tx = new Transaction(doc, "Spooling Savant - Prepare Pipe BOM Parameters"))
            {
                tx.Start();

                doc.Regenerate();

                FabricationSavantParameterSync.SyncSizeParameters(doc, parts);

                foreach (FabricationPart part in parts)
                {
                    SyncTextParameter(doc, part, PARAM_SOURCE_MATERIAL, PARAM_S_MATERIAL, CleanMaterialValue, onlyWhenTargetBlank: true);
                }

                FabricationSavantParameterSync.SyncConnectorParameters(doc, parts);

                tx.Commit();
            }
        }

        private static void SyncTextParameter(
            RvtDocument doc,
            Autodesk.Revit.DB.Element element,
            string sourceName,
            string targetName,
            Func<string, string> clean,
            bool onlyWhenTargetBlank = false)
        {
            Parameter target = element.LookupParameter(targetName);
            if (target == null || target.IsReadOnly)
                return;

            // Match BOM grouping: value may live on instance or type. Do not overwrite a target that
            // already resolves to text anywhere the BOM would read from.
            string resolvedTarget = (GetParamString(element, targetName, doc) ?? string.Empty).Trim();
            if (onlyWhenTargetBlank)
            {
                if (!string.IsNullOrWhiteSpace(resolvedTarget))
                    return;
            }

            string sourceValue = (GetParamString(element, sourceName, doc) ?? string.Empty).Trim();
            string cleaned = clean(sourceValue);
            if (string.IsNullOrWhiteSpace(cleaned))
                return;

            if (string.Equals(resolvedTarget, cleaned, StringComparison.Ordinal))
                return;

            if (target.StorageType == RvtStorageType.String)
            {
                target.Set(cleaned);
                return;
            }

            try
            {
                target.SetValueString(cleaned);
            }
            catch
            {
            }
        }

        private static string CleanSizeValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value
                .Replace("Ø", string.Empty)
                .Replace("ø", string.Empty)
                .Replace("⌀", string.Empty)
                .Trim();
        }

        private static string CleanMaterialValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string result = value;
            int colon = result.IndexOf(':');
            if (colon >= 0 && colon < result.Length - 1)
                result = result.Substring(colon + 1);

            return result.Trim();
        }

        private static string GetParamString(Autodesk.Revit.DB.Element element, string name, RvtDocument doc = null)
        {
            if (element == null || string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string value = ReadParameterAsString(element.LookupParameter(name));
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();

            try
            {
                if (doc != null)
                {
                    RvtElementId typeId = element.GetTypeId();
                    if (typeId != null && typeId != RvtElementId.InvalidElementId)
                    {
                        Autodesk.Revit.DB.Element typeElement = doc.GetElement(typeId);
                        value = ReadParameterAsString(typeElement?.LookupParameter(name));
                        if (!string.IsNullOrWhiteSpace(value))
                            return value.Trim();
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ReadParameterAsString(Parameter parameter)
        {
            if (parameter == null)
                return string.Empty;

            try
            {
                if (!parameter.HasValue)
                {
                    if (parameter.StorageType == RvtStorageType.String)
                    {
                        string value = parameter.AsString();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }

                    string valueStringNoValue = parameter.AsValueString();
                    return string.IsNullOrWhiteSpace(valueStringNoValue) ? string.Empty : valueStringNoValue.Trim();
                }

                if (parameter.StorageType == RvtStorageType.String)
                {
                    string value = parameter.AsString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }

                string valueString = parameter.AsValueString();
                if (!string.IsNullOrWhiteSpace(valueString))
                    return valueString;

                switch (parameter.StorageType)
                {
                    case RvtStorageType.Integer:
                        return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case RvtStorageType.Double:
                        return parameter.AsDouble().ToString("0.########", CultureInfo.InvariantCulture);
                    case RvtStorageType.ElementId:
                        return parameter.AsElementId().Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static double? GetSimpleDoubleParam(FabricationPart fp, string name)
        {
            try
            {
                Parameter p = fp.LookupParameter(name);
                if (p == null || !p.HasValue) return null;

                if (p.StorageType == RvtStorageType.Double)
                    return p.AsDouble();

                string value = ReadParameterAsString(p);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariantValue))
                    return invariantValue;

                if (double.TryParse(value, out double localValue))
                    return localValue;

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Total run length in feet for BOM pipe rows (sums straight centerline; falls back to Length param).
        /// </summary>
        private static double GetPipeRunLengthFeet(FabricationPart fp, bool lengthRow)
        {
            if (!lengthRow || fp == null)
                return 0.0;

            try
            {
                double center = fp.CenterlineLength;
                if (!double.IsNaN(center) && !double.IsInfinity(center) && center > 1e-9)
                    return center;
            }
            catch
            {
            }

            return GetSimpleDoubleParam(fp, PARAM_LENGTH) ?? 0.0;
        }

        // =====================================================================
        // UI / FILE HELPERS
        // =====================================================================

        private static BomOptions PromptForBomOptions(RvtDocument doc)
        {
            var w = new PipeBomOptionsWindow(doc);
            if (w.ShowDialog() != true)
                return null;

            return w.Options;
        }

        private static TextBlock PipeBomFieldCaption(string captionText)
        {
            return new TextBlock
            {
                Text = captionText,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private sealed class PipeBomOptionsWindow : Window
        {
            private readonly RvtDocument _doc;
            private readonly Wpf.TextBox _txtProject;
            private readonly Wpf.TextBox _txtLevel;
            private readonly Wpf.TextBox _txtArea;
            private readonly Wpf.TextBox _txtCreatedBy;
            private readonly Wpf.TextBox _txtSaveAs;
            private readonly Wpf.TextBox _txtLogo;
            private readonly Wpf.CheckBox _chkGaskets;
            private readonly Wpf.CheckBox _chkBoltKits;

            internal BomOptions Options { get; private set; }

            internal PipeBomOptionsWindow(RvtDocument doc)
            {
                _doc = doc;
                Title = "Pipe BOM";
                Width = 620;
                MinHeight = 440;
                SizeToContent = SizeToContent.Height;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                SsSavantChrome.MergeInto(this);

                var root = new Wpf.StackPanel { Margin = new Thickness(14) };

                root.Children.Add(PipeBomFieldCaption("Project Name"));
                _txtProject = new Wpf.TextBox { Text = GetDefaultProjectName(doc), Margin = RowMargin };
                root.Children.Add(_txtProject);

                root.Children.Add(PipeBomFieldCaption("Level"));
                _txtLevel = new Wpf.TextBox { Text = GetDefaultLevelName(doc), Margin = RowMargin };
                root.Children.Add(_txtLevel);

                root.Children.Add(PipeBomFieldCaption("Area"));
                _txtArea = new Wpf.TextBox { Margin = RowMargin };
                root.Children.Add(_txtArea);

                root.Children.Add(PipeBomFieldCaption("Created By"));
                _txtCreatedBy = new Wpf.TextBox { Text = Environment.UserName, Margin = RowMargin };
                root.Children.Add(_txtCreatedBy);

                root.Children.Add(PipeBomFieldCaption("Save As"));
                var saveRow = new Wpf.DockPanel { Margin = RowMargin };
                var btnSaveBrowse = new Wpf.Button { Content = "Browse", Width = 88, Margin = new Thickness(8, 0, 0, 0) };
                Wpf.DockPanel.SetDock(btnSaveBrowse, Wpf.Dock.Right);
                btnSaveBrowse.Click += (_, __) =>
                {
                    string chosen = PromptForSavePath(_txtSaveAs.Text, _txtProject.Text);
                    if (!string.IsNullOrWhiteSpace(chosen))
                        _txtSaveAs.Text = StripPipeBomSaveSuffixFromFullStem(
                            Path.GetFileNameWithoutExtension(chosen),
                            _txtProject.Text);
                };

                _txtSaveAs = new Wpf.TextBox { Text = PipeBomSaveFileSuffix };
                saveRow.Children.Add(btnSaveBrowse);
                saveRow.Children.Add(_txtSaveAs);
                root.Children.Add(saveRow);

                var checks = new Wpf.StackPanel { Orientation = Orientation.Horizontal, Margin = RowMargin };
                _chkGaskets = new Wpf.CheckBox { Content = "Include Gaskets", Margin = new Thickness(0, 0, 24, 0) };
                _chkBoltKits = new Wpf.CheckBox { Content = "Include Bolt Kits" };
                checks.Children.Add(_chkGaskets);
                checks.Children.Add(_chkBoltKits);
                root.Children.Add(checks);

                root.Children.Add(PipeBomFieldCaption("Logo"));
                var logoRow = new Wpf.DockPanel { Margin = RowMargin };
                var logoBtns = new Wpf.StackPanel { Orientation = Orientation.Horizontal };
                Wpf.DockPanel.SetDock(logoBtns, Wpf.Dock.Right);
                var btnLogoBrowse = new Wpf.Button { Content = "Browse", Width = 78, Margin = new Thickness(8, 0, 0, 0) };
                btnLogoBrowse.Click += (_, __) =>
                {
                    string chosen = PromptForLogoPath();
                    if (!string.IsNullOrWhiteSpace(chosen))
                        _txtLogo.Text = chosen;
                };

                var btnLogoClear = new Wpf.Button { Content = "Clear", Width = 78, Margin = new Thickness(8, 0, 0, 0) };
                btnLogoClear.Click += (_, __) => _txtLogo.Text = string.Empty;
                logoBtns.Children.Add(btnLogoBrowse);
                logoBtns.Children.Add(btnLogoClear);
                _txtLogo = new Wpf.TextBox();
                logoRow.Children.Add(logoBtns);
                logoRow.Children.Add(_txtLogo);
                root.Children.Add(logoRow);

                string savedLogo = TryLoadPipeBomLogoPath(doc);
                if (!string.IsNullOrWhiteSpace(savedLogo))
                    _txtLogo.Text = savedLogo;

                var buttons = new Wpf.StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
                var ok = new Wpf.Button { Content = "OK", Width = 88, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
                var cancel = new Wpf.Button { Content = "Cancel", Width = 88, IsCancel = true };
                ok.Click += Ok_Click;
                cancel.Click += (_, __) =>
                {
                    DialogResult = false;
                    Close();
                };

                buttons.Children.Add(ok);
                buttons.Children.Add(cancel);
                root.Children.Add(buttons);

                Content = root;

                KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape)
                    {
                        DialogResult = false;
                        Close();
                    }
                };
            }

            private static Thickness RowMargin => new Thickness(0, 0, 0, 10);

            private void Ok_Click(object sender, RoutedEventArgs e)
            {
                string savePath = ResolvePipeBomSavePath(_txtSaveAs.Text, _txtProject.Text);
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    SsSavantMessageBox.Show("Could not resolve the PDF save path.", "Pipe BOM");
                    return;
                }

                string logoTrimmed = (_txtLogo.Text ?? string.Empty).Trim();
                PersistPipeBomLogoForCurrentProject(_doc, logoTrimmed);

                Options = new BomOptions
                {
                    ProjectName = (_txtProject.Text ?? string.Empty).Trim(),
                    Level = (_txtLevel.Text ?? string.Empty).Trim(),
                    Area = (_txtArea.Text ?? string.Empty).Trim(),
                    CreatedBy = (_txtCreatedBy.Text ?? string.Empty).Trim(),
                    SavePath = savePath,
                    IncludeGaskets = _chkGaskets.IsChecked == true,
                    IncludeBoltKits = _chkBoltKits.IsChecked == true,
                    LogoPath = logoTrimmed
                };

                DialogResult = true;
                Close();
            }
        }

        private static string GetPipeBomProjectKey(RvtDocument doc)
        {
            if (doc == null)
                return string.Empty;

            try
            {
                if (!string.IsNullOrWhiteSpace(doc.PathName))
                    return Path.GetFullPath(doc.PathName).ToUpperInvariant();
            }
            catch
            {
            }

            string title = (doc.Title ?? string.Empty).Trim();
            return string.IsNullOrEmpty(title)
                ? "unsaved::(no title)"
                : "unsaved::" + title.ToUpperInvariant();
        }

        private static string TryLoadPipeBomLogoPath(RvtDocument doc)
        {
            string key = GetPipeBomProjectKey(doc);
            if (string.IsNullOrEmpty(key))
                return null;

            PipeBomPerProjectLogoStore store = ReadPipeBomLogoStore();
            foreach (PipeBomPerProjectLogoEntry entry in store.Entries)
            {
                if (string.Equals(entry?.ProjectKey, key, StringComparison.Ordinal))
                    return entry.LogoPath;
            }

            return null;
        }

        private static void PersistPipeBomLogoForCurrentProject(RvtDocument doc, string logoPathTrimmed)
        {
            string key = GetPipeBomProjectKey(doc);
            if (string.IsNullOrEmpty(key))
                return;

            PipeBomPerProjectLogoStore store = ReadPipeBomLogoStore();
            if (store.Entries == null)
                store.Entries = new List<PipeBomPerProjectLogoEntry>();
            List<PipeBomPerProjectLogoEntry> list = store.Entries;
            int idx = list.FindIndex(e => string.Equals(e?.ProjectKey, key, StringComparison.Ordinal));

            if (string.IsNullOrWhiteSpace(logoPathTrimmed))
            {
                if (idx >= 0)
                    list.RemoveAt(idx);
            }
            else
            {
                if (idx >= 0)
                    list[idx].LogoPath = logoPathTrimmed;
                else
                    list.Add(new PipeBomPerProjectLogoEntry { ProjectKey = key, LogoPath = logoPathTrimmed });
            }

            WritePipeBomLogoStore(store);
        }

        private static PipeBomPerProjectLogoStore ReadPipeBomLogoStore()
        {
            try
            {
                if (!File.Exists(PipeBomLogoPerProjectStorePath))
                    return new PipeBomPerProjectLogoStore();

                using (var fs = new FileStream(PipeBomLogoPerProjectStorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var serializer = new XmlSerializer(typeof(PipeBomPerProjectLogoStore));
                    var store = serializer.Deserialize(fs) as PipeBomPerProjectLogoStore ?? new PipeBomPerProjectLogoStore();
                    if (store.Entries == null)
                        store.Entries = new List<PipeBomPerProjectLogoEntry>();
                    return store;
                }
            }
            catch
            {
                return new PipeBomPerProjectLogoStore();
            }
        }

        private static void WritePipeBomLogoStore(PipeBomPerProjectLogoStore store)
        {
            if (store == null)
                return;

            if (store.Entries == null)
                store.Entries = new List<PipeBomPerProjectLogoEntry>();

            string folder = Path.GetDirectoryName(PipeBomLogoPerProjectStorePath);
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string tempPath = PipeBomLogoPerProjectStorePath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var serializer = new XmlSerializer(typeof(PipeBomPerProjectLogoStore));
                serializer.Serialize(fs, store);
            }

            if (File.Exists(PipeBomLogoPerProjectStorePath))
                File.Delete(PipeBomLogoPerProjectStorePath);
            File.Move(tempPath, PipeBomLogoPerProjectStorePath);
        }

        private static string PromptForSavePath(string saveAsDisplayName, string projectName)
        {
            string folder = GetDefaultSaveFolder();
            string suffixStem = string.IsNullOrWhiteSpace(saveAsDisplayName)
                ? PipeBomSaveFileSuffix
                : MakeSafeFileName(saveAsDisplayName.Trim());
            if (string.IsNullOrWhiteSpace(suffixStem))
                suffixStem = PipeBomSaveFileSuffix;

            string fullStem = CombinePipeBomSaveStem(projectName, suffixStem);

            var dlg = new SaveFileDialog();
            dlg.Title = "Save Pipe & Fittings BOM (PDF)";
            dlg.Filter = "PDF files (*.pdf)|*.pdf";
            dlg.InitialDirectory = folder;
            dlg.FileName = fullStem + ".pdf";
            dlg.AddExtension = true;
            dlg.DefaultExt = "pdf";
            dlg.OverwritePrompt = true;

            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static string PromptForLogoPath()
        {
            var dlg = new OpenFileDialog();
            dlg.Title = "Select BOM Logo";
            dlg.Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*";
            dlg.CheckFileExists = true;

            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static string ResolvePipeBomSavePath(string rawInput, string projectName)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
                return BuildDefaultSavePath(projectName);

            string trimmed = rawInput.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmed))
                return BuildDefaultSavePath(projectName);

            bool looksLikePath = Path.IsPathRooted(trimmed) ||
                                 trimmed.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                                 trimmed.IndexOf(Path.AltDirectorySeparatorChar) >= 0;

            if (looksLikePath)
            {
                string full = trimmed;
                if (string.IsNullOrWhiteSpace(Path.GetExtension(full)))
                    full += ".pdf";
                return full;
            }

            string userStem = MakeSafeFileName(Path.GetFileNameWithoutExtension(trimmed));
            if (string.IsNullOrWhiteSpace(userStem))
                userStem = PipeBomSaveFileSuffix;

            return Path.Combine(GetDefaultSaveFolder(), CombinePipeBomSaveStem(projectName, userStem) + ".pdf");
        }

        private static string GetDefaultSaveFolder()
        {
            return Directory.Exists(@"C:\Temp")
                ? @"C:\Temp"
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private static string BuildDefaultSavePath(string projectName)
        {
            return Path.Combine(GetDefaultSaveFolder(), CombinePipeBomSaveStem(projectName, PipeBomSaveFileSuffix) + ".pdf");
        }

        private static string GetPipeBomProjectFilePrefix(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return "Project_";
            string p = MakeSafeFileName(projectName.Trim());
            if (string.IsNullOrWhiteSpace(p)) p = "Project";
            return p + "_";
        }

        private static string CombinePipeBomSaveStem(string projectName, string userSuffixStem)
        {
            string suffix = MakeSafeFileName(string.IsNullOrWhiteSpace(userSuffixStem) ? PipeBomSaveFileSuffix : userSuffixStem.Trim());
            if (string.IsNullOrWhiteSpace(suffix)) suffix = PipeBomSaveFileSuffix;
            return GetPipeBomProjectFilePrefix(projectName) + suffix;
        }

        /// <summary>After Browse: show only the suffix in the Save As box (project prefix is implicit).</summary>
        private static string StripPipeBomSaveSuffixFromFullStem(string fileNameWithoutExtension, string projectName)
        {
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
                return PipeBomSaveFileSuffix;

            string prefix = GetPipeBomProjectFilePrefix(projectName);
            if (fileNameWithoutExtension.Length > prefix.Length &&
                fileNameWithoutExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return fileNameWithoutExtension.Substring(prefix.Length);

            return fileNameWithoutExtension;
        }

        private static string GetDefaultProjectName(RvtDocument doc)
        {
            try
            {
                string projectName = doc.ProjectInformation?.Name;
                if (!string.IsNullOrWhiteSpace(projectName))
                    return projectName;
            }
            catch
            {
            }

            return doc?.Title ?? string.Empty;
        }

        private static string GetDefaultLevelName(RvtDocument doc)
        {
            try
            {
                if (doc?.ActiveView?.GenLevel != null)
                    return doc.ActiveView.GenLevel.Name;
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string MakeSafeFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "PipeBOM";
            var bad = Path.GetInvalidFileNameChars();
            var chars = raw.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(bad, chars[i]) >= 0)
                    chars[i] = '_';
            return new string(chars).Trim();
        }

        // =====================================================================
        // PDF EXPORT
        // =====================================================================

        private static void ExportBomToPdf(
            string path,
            BomOptions options,
            Dictionary<BomKey, BomAgg> groups)
        {
            // Pipe (0) → fittings alphabetical (1) → valves alphanumerical (2).
            var rows = groups
                .OrderBy(kv => kv.Value.SortPriority)
                .ThenBy(kv => kv.Key.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(kv => kv.Key.Size, StringComparer.OrdinalIgnoreCase)
                .ThenBy(kv => kv.Value.SortKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                PdfDocument pdf = new PdfDocument(PageSize.LETTER, 36f, 36f, 36f, 36f);
                PdfWriter.GetInstance(pdf, fs);
                pdf.Open();

                var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
                var normalFont = FontFactory.GetFont(FontFactory.HELVETICA, 9);
                var headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9);

                AddPdfHeader(pdf, options, titleFont, normalFont);

                PdfPTable table = new PdfPTable(5);
                table.WidthPercentage = 100;
                table.SetWidths(new float[] { 1.2f, 3.0f, 2.0f, 0.8f, 1.5f });

                AddHeaderCell(table, "Size", headerFont);
                AddHeaderCell(table, "Description", headerFont);
                AddHeaderCell(table, "Material", headerFont);
                AddHeaderCell(table, "QTY", headerFont);
                AddHeaderCell(table, "Length", headerFont);

                foreach (var kv in rows)
                {
                    BomKey key = kv.Key;
                    BomAgg agg = kv.Value;

                    AddBodyCell(table, key.Size, normalFont);
                    AddBodyCell(table, key.Description, normalFont);
                    AddBodyCell(table, key.Material, normalFont);
                    AddBodyCell(table, key.IsLengthRow ? string.Empty : agg.Count.ToString(CultureInfo.InvariantCulture), normalFont);
                    AddBodyCell(table, key.IsLengthRow ? FormatFeetInches(agg.TotalLengthFt) : string.Empty, normalFont);
                }

                pdf.Add(table);
                pdf.Close();
            }
        }

        private static readonly string[] FabricationWeightParameterNames =
        {
            "Weight",
            "Item Weight",
            "Product Weight",
            "Fabrication Weight",
            "S-Weight",
            "Total Weight",
            "Part Weight",
            "PART WEIGHT"
        };

        private static void ExportPipeCutlistToPdf(
            string path,
            BomOptions options,
            List<(FabricationPart Part, string AssemblyName)> partsWithAssembly,
            RvtDocument doc)
        {
            if (partsWithAssembly == null || partsWithAssembly.Count == 0)
                throw new ArgumentException("No fabrication parts.", nameof(partsWithAssembly));

            var groups = new Dictionary<CutlistBomKey, CutlistAgg>();
            foreach ((FabricationPart Part, string AssemblyName) item in partsWithAssembly)
            {
                FabricationPart fp = item.Part;
                if (fp == null || !IsStraightPipeCutlistMember(fp))
                    continue;

                string size = (GetParamString(fp, PARAM_S_SIZE, doc) ?? string.Empty).Trim();
                string description = (GetParamString(fp, PARAM_DESCRIPTION, doc) ?? string.Empty).Trim();
                string assemblyName = (item.AssemblyName ?? string.Empty).Trim();
                double lenFt = GetPipeRunLengthFeet(fp, lengthRow: true);
                GetFabricationCutlistConnectorEnds(fp, doc, out string connector1, out string connector2);
                string lengthText = FormatFeetInches(lenFt);

                var key = new CutlistBomKey(size, description, lengthText, connector1, connector2, assemblyName);
                if (!groups.TryGetValue(key, out CutlistAgg agg))
                {
                    agg = new CutlistAgg { SortLengthFt = lenFt };
                    groups[key] = agg;
                }

                agg.Count++;
            }

            if (groups.Count == 0)
                throw new InvalidOperationException("No straight pipe pieces were found for the cut list.");

            // Description → Assembly Name → Size → Length → connectors.
            List<KeyValuePair<CutlistBomKey, CutlistAgg>> rows = groups
                .OrderBy(kv => kv.Key.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(kv => kv.Key.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(kv => kv.Key.Size, StringComparer.OrdinalIgnoreCase)
                .ThenBy(kv => kv.Value.SortLengthFt)
                .ThenBy(kv => kv.Key.Connector1, StringComparer.OrdinalIgnoreCase)
                .ThenBy(kv => kv.Key.Connector2, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                iTextSharp.text.Rectangle pageSize = PageSize.LETTER.Rotate();
                PdfDocument pdf = new PdfDocument(pageSize, 36f, 36f, 36f, 36f);
                PdfWriter.GetInstance(pdf, fs);
                pdf.Open();

                var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
                var normalFont = FontFactory.GetFont(FontFactory.HELVETICA, 9);
                var headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9);

                AddPdfHeader(pdf, options, titleFont, normalFont);

                PdfPTable table = new PdfPTable(7);
                table.WidthPercentage = 100;
                table.SetWidths(new float[] { 0.55f, 0.9f, 2.2f, 1.35f, 1.0f, 1.15f, 1.15f });

                AddHeaderCell(table, "Count", headerFont);
                AddHeaderCell(table, "Size", headerFont);
                AddHeaderCell(table, "Description", headerFont);
                AddHeaderCell(table, "Assembly Name", headerFont);
                AddHeaderCell(table, "Length", headerFont);
                AddHeaderCell(table, "Connector 1", headerFont);
                AddHeaderCell(table, "Connector 2", headerFont);

                foreach (KeyValuePair<CutlistBomKey, CutlistAgg> kv in rows)
                {
                    CutlistBomKey key = kv.Key;
                    CutlistAgg agg = kv.Value;

                    AddBodyCell(table, agg.Count.ToString(CultureInfo.InvariantCulture), normalFont);
                    AddBodyCell(table, key.Size, normalFont);
                    AddBodyCell(table, key.Description, normalFont);
                    AddBodyCell(table, key.AssemblyName, normalFont);
                    AddBodyCell(table, key.LengthText, normalFont);
                    AddBodyCell(table, key.Connector1, normalFont);
                    AddBodyCell(table, key.Connector2, normalFont);
                }

                pdf.Add(table);
                pdf.Close();
            }
        }

        /// <summary>Straight fabrication pipe only (same notion as BOM “Length” row): excludes fittings, welds, gaskets, bolt kits, valves.</summary>
        private static bool IsStraightPipeCutlistMember(FabricationPart fp)
        {
            return FabricationPartClassification.IsStraightPipeRun(fp);
        }

        private static void GetFabricationCutlistConnectorEnds(
            FabricationPart part,
            RvtDocument doc,
            out string end1,
            out string end2)
        {
            FabricationConnectorEnds.GetConnectorEndLabels(part, doc, out end1, out end2);
        }

        private readonly struct CutlistBomKey : IEquatable<CutlistBomKey>
        {
            public CutlistBomKey(string size, string description, string lengthText, string connector1, string connector2, string assemblyName)
            {
                Size = size ?? string.Empty;
                Description = description ?? string.Empty;
                LengthText = lengthText ?? string.Empty;
                Connector1 = connector1 ?? string.Empty;
                Connector2 = connector2 ?? string.Empty;
                AssemblyName = assemblyName ?? string.Empty;
            }

            public string Size { get; }
            public string Description { get; }
            public string LengthText { get; }
            public string Connector1 { get; }
            public string Connector2 { get; }
            public string AssemblyName { get; }

            public bool Equals(CutlistBomKey other) =>
                string.Equals(Size, other.Size, StringComparison.Ordinal) &&
                string.Equals(Description, other.Description, StringComparison.Ordinal) &&
                string.Equals(LengthText, other.LengthText, StringComparison.Ordinal) &&
                string.Equals(Connector1, other.Connector1, StringComparison.Ordinal) &&
                string.Equals(Connector2, other.Connector2, StringComparison.Ordinal) &&
                string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is CutlistBomKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 23 + StringComparer.Ordinal.GetHashCode(Size);
                    h = h * 23 + StringComparer.Ordinal.GetHashCode(Description);
                    h = h * 23 + StringComparer.Ordinal.GetHashCode(LengthText);
                    h = h * 23 + StringComparer.Ordinal.GetHashCode(Connector1);
                    h = h * 23 + StringComparer.Ordinal.GetHashCode(Connector2);
                    h = h * 23 + StringComparer.Ordinal.GetHashCode(AssemblyName);
                    return h;
                }
            }
        }

        private sealed class CutlistAgg
        {
            public int Count;
            public double SortLengthFt;
        }

        private static void ExportAssemblyListToPdf(
            string path,
            BomOptions options,
            IList<PackageAssemblyListRow> rows,
            RvtDocument doc)
        {
            if (rows == null || rows.Count == 0)
                throw new ArgumentException("No assembly list rows.", nameof(rows));

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                PdfDocument pdf = new PdfDocument(PageSize.LETTER, 36f, 36f, 36f, 36f);
                PdfWriter.GetInstance(pdf, fs);
                pdf.Open();

                var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
                var normalFont = FontFactory.GetFont(FontFactory.HELVETICA, 9);
                var headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9);

                AddPdfHeader(pdf, options, titleFont, normalFont);

                PdfPTable table = new PdfPTable(2);
                table.WidthPercentage = 100;
                table.SetWidths(new float[] { 3.2f, 1.2f });

                AddHeaderCell(table, "Spool", headerFont);
                AddHeaderCell(table, "Weight (lb)", headerFont);

                double totalLb = 0.0;
                int weightCells = 0;

                foreach (PackageAssemblyListRow row in rows)
                {
                    AddBodyCell(table, row.Label ?? string.Empty, normalFont);

                    string weightText = "—";
                    if (TrySumAssemblyFabricationPartsWeightLb(doc, row.AssemblyId, out double assemblyLb))
                    {
                        weightText = assemblyLb.ToString("N2", CultureInfo.InvariantCulture);
                        totalLb += assemblyLb;
                        weightCells++;
                    }

                    AddNumericBodyCell(table, weightText, normalFont);
                }

                AddBodyCell(table, "Total", headerFont);
                string totalText = weightCells > 0
                    ? totalLb.ToString("N2", CultureInfo.InvariantCulture)
                    : "—";
                AddNumericBodyCell(table, totalText, headerFont);

                pdf.Add(table);
                pdf.Close();
            }
        }

        /// <summary>
        /// Sums weight from all fabrication members in the assembly (pipe, duct, hanger, etc.).
        /// Package BOM still filters pipework only; assembly weight here matches shop expectation for total fab mass on the spool.
        /// </summary>
        private static bool TrySumAssemblyFabricationPartsWeightLb(
            RvtDocument doc,
            RvtElementId assemblyId,
            out double totalPounds)
        {
            totalPounds = 0.0;
            if (doc == null || assemblyId == null || assemblyId == RvtElementId.InvalidElementId)
                return false;

            AssemblyInstance asm = doc.GetElement(assemblyId) as AssemblyInstance;
            if (asm == null)
                return false;

            bool any = false;
            foreach (RvtElementId memberId in asm.GetMemberIds())
            {
                if (memberId == null || memberId == RvtElementId.InvalidElementId)
                    continue;

                FabricationPart fp = doc.GetElement(memberId) as FabricationPart;
                if (fp == null)
                    continue;

                if (TryGetFabricationPartWeightPounds(fp, doc, out double lb))
                {
                    totalPounds += lb;
                    any = true;
                }
            }

            return any;
        }

        private static bool TryGetFabricationPartWeightPounds(FabricationPart fp, RvtDocument doc, out double pounds)
        {
            pounds = 0.0;
            if (fp == null)
                return false;

            foreach (string name in FabricationWeightParameterNames)
            {
                if (TryReadWeightParameterPounds(fp, doc, name, out pounds))
                    return true;
            }

            return TryGetFabricationPartWeightFromWeightNamedParameters(fp, doc, out pounds);
        }

        /// <summary>
        /// Picks the first readable numeric/mass parameter whose definition name contains "weight" (catalog-specific names).
        /// </summary>
        private static bool TryGetFabricationPartWeightFromWeightNamedParameters(
            FabricationPart fp,
            RvtDocument doc,
            out double pounds)
        {
            pounds = 0.0;
            if (fp == null)
                return false;

            if (TryParametersMatchingWeightName(fp.Parameters, out pounds))
                return true;

            if (doc != null)
            {
                RvtElementId typeId = fp.GetTypeId();
                if (typeId != null && typeId != RvtElementId.InvalidElementId)
                {
                    Autodesk.Revit.DB.Element typeEl = doc.GetElement(typeId);
                    if (typeEl != null && TryParametersMatchingWeightName(typeEl.Parameters, out pounds))
                        return true;
                }
            }

            return false;
        }

        private static bool TryParametersMatchingWeightName(ParameterSet parameters, out double pounds)
        {
            pounds = 0.0;
            if (parameters == null)
                return false;

            foreach (Parameter p in parameters)
            {
                if (p == null)
                    continue;

                Definition def = p.Definition;
                string name = def?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (name.IndexOf("weight", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (name.IndexOf("lightweight", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                if (TryConvertParameterToPounds(p, out pounds))
                    return true;
            }

            return false;
        }

        private static bool TryReadWeightParameterPounds(Autodesk.Revit.DB.Element element, RvtDocument doc, string name, out double pounds)
        {
            pounds = 0.0;
            if (element == null || string.IsNullOrWhiteSpace(name))
                return false;

            Parameter p = element.LookupParameter(name);
            if (TryConvertParameterToPounds(p, out pounds))
                return true;

            if (doc != null)
            {
                RvtElementId typeId = element.GetTypeId();
                if (typeId != null && typeId != RvtElementId.InvalidElementId)
                {
                    Autodesk.Revit.DB.Element typeEl = doc.GetElement(typeId);
                    p = typeEl?.LookupParameter(name);
                    if (TryConvertParameterToPounds(p, out pounds))
                        return true;
                }
            }

            return false;
        }

        private static bool TryConvertParameterToPounds(Parameter p, out double pounds)
        {
            pounds = 0.0;
            if (p == null || !p.HasValue)
                return false;

            try
            {
                ForgeTypeId dataType = p.Definition.GetDataType();

                if (p.StorageType == RvtStorageType.Double)
                {
                    double raw = p.AsDouble();
                    if (dataType == SpecTypeId.Mass)
                    {
                        pounds = UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.PoundsMass);
                        return !double.IsNaN(pounds) && !double.IsInfinity(pounds);
                    }

                    pounds = raw;
                    return !double.IsNaN(pounds) && !double.IsInfinity(pounds);
                }

                if (p.StorageType == RvtStorageType.Integer)
                {
                    pounds = p.AsInteger();
                    return true;
                }

                string s = ReadParameterAsString(p);
                return TryParseLooseWeightString(s, out pounds);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseLooseWeightString(string s, out double pounds)
        {
            pounds = 0.0;
            if (string.IsNullOrWhiteSpace(s))
                return false;

            string t = s.Trim();
            bool metricKg = t.IndexOf("kg", StringComparison.OrdinalIgnoreCase) >= 0;
            t = Regex.Replace(t, @"\s*(lbs|lb|pounds|pound|kg)\s*", " ", RegexOptions.IgnoreCase).Trim();

            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                return false;
            }

            pounds = metricKg ? value * 2.2046226218 : value;
            return !double.IsNaN(pounds) && !double.IsInfinity(pounds);
        }

        private static void AddNumericBodyCell(PdfPTable table, string text, Font font)
        {
            var cell = new PdfPCell(new Phrase(text ?? string.Empty, font))
            {
                HorizontalAlignment = PdfAlign.ALIGN_RIGHT,
                VerticalAlignment = PdfAlign.ALIGN_MIDDLE,
                Padding = 3f
            };
            table.AddCell(cell);
        }

        private static void AddPdfHeader(PdfDocument pdf, BomOptions options, Font titleFont, Font normalFont)
        {
            bool hasLogo = !string.IsNullOrWhiteSpace(options.LogoPath) && File.Exists(options.LogoPath);

            if (hasLogo)
            {
                PdfPTable header = new PdfPTable(2);
                header.WidthPercentage = 100;
                header.SetWidths(new float[] { 1.2f, 4.8f });

                PdfPCell logoCell = new PdfPCell { Border = iTextSharp.text.Rectangle.NO_BORDER, Padding = 0f };
                try
                {
                    iTextSharp.text.Image logo = iTextSharp.text.Image.GetInstance(options.LogoPath);
                    logo.ScaleToFit(90f, 45f);
                    logoCell.AddElement(logo);
                }
                catch
                {
                    logoCell.AddElement(new Phrase(string.Empty, normalFont));
                }

                PdfPCell textCell = new PdfPCell { Border = iTextSharp.text.Rectangle.NO_BORDER, Padding = 0f };
                textCell.AddElement(new Paragraph(ResolveBomPdfTitle(options), titleFont));
                textCell.AddElement(new Paragraph(BuildHeaderLine(options), normalFont));
                textCell.AddElement(new Paragraph("Date: " + ResolveHeaderDateText(options), normalFont));

                header.AddCell(logoCell);
                header.AddCell(textCell);
                pdf.Add(header);
            }
            else
            {
                var titlePara = new Paragraph(ResolveBomPdfTitle(options), titleFont)
                {
                    Alignment = PdfAlign.ALIGN_CENTER
                };
                pdf.Add(titlePara);
                pdf.Add(new Paragraph(BuildHeaderLine(options), normalFont));
                pdf.Add(new Paragraph("Date: " + ResolveHeaderDateText(options), normalFont));
            }

            pdf.Add(new Paragraph(" "));
        }

        private static string ResolveHeaderDateText(BomOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options?.DateText))
                return options.DateText.Trim();

            return DateTime.Now.ToString("M/d/yyyy h:mm:ss tt");
        }

        private static string ResolveBomPdfTitle(BomOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options?.BomTitle))
                return options.BomTitle.Trim();

            return DEFAULT_BOM_TITLE;
        }

        private static string BuildHeaderLine(BomOptions options)
        {
            var pieces = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.ProjectName)) pieces.Add("Project: " + options.ProjectName);
            if (!string.IsNullOrWhiteSpace(options.Level)) pieces.Add("Level: " + options.Level);
            if (!string.IsNullOrWhiteSpace(options.Area)) pieces.Add("Area: " + options.Area);
            if (!string.IsNullOrWhiteSpace(options.CreatedBy)) pieces.Add("Created By: " + options.CreatedBy);

            return pieces.Count == 0 ? string.Empty : string.Join("    ", pieces);
        }

        private static void AddHeaderCell(PdfPTable table, string text, Font font)
        {
            var cell = new PdfPCell(new Phrase(text, font))
            {
                HorizontalAlignment = PdfAlign.ALIGN_CENTER,
                VerticalAlignment = PdfAlign.ALIGN_MIDDLE,
                BackgroundColor = BaseColor.LIGHT_GRAY,
                Padding = 4f
            };
            table.AddCell(cell);
        }

        private static void AddBodyCell(PdfPTable table, string text, Font font)
        {
            var cell = new PdfPCell(new Phrase(text ?? string.Empty, font))
            {
                HorizontalAlignment = PdfAlign.ALIGN_LEFT,
                VerticalAlignment = PdfAlign.ALIGN_MIDDLE,
                Padding = 3f
            };
            table.AddCell(cell);
        }

        /// <summary>Feet &amp; inches to nearest 1/8" for cut-list / TigerStop labels.</summary>
        internal static string FormatLengthFeetInches(double lengthFt)
        {
            return FormatFeetInches(lengthFt);
        }

        /// <summary>Straight-pipe centerline length in feet (Length param fallback).</summary>
        internal static double GetStraightPipeLengthFeet(FabricationPart fp)
        {
            return GetPipeRunLengthFeet(fp, lengthRow: true);
        }

        /// <summary>Ensures S-Size / S-Material are synced before TigerStop / PCF / BOM-style exports.</summary>
        internal static void EnsureBomParametersAndSyncForExport(RvtDocument doc, List<FabricationPart> parts)
        {
            EnsureBomParametersAndSync(doc, parts);
        }

        // ----- length formatting: feet & inches to nearest 1/8" -----

        private static string FormatFeetInches(double lengthFt)
        {
            if (lengthFt < 0) lengthFt = 0;

            double totalInches = lengthFt * 12.0;
            double rounded = Math.Round(totalInches * 8.0) / 8.0;

            int feet = (int)Math.Floor(rounded / 12.0);
            double remIn = rounded - feet * 12.0;

            int wholeIn = (int)Math.Floor(remIn + 1e-9);
            double frac = remIn - wholeIn;

            string fracStr = FractionFromEighths(frac);

            if (feet == 0)
            {
                if (wholeIn == 0 && string.IsNullOrEmpty(fracStr))
                    return "0\"";
                if (wholeIn == 0)
                    return $"{fracStr}\"";
                if (string.IsNullOrEmpty(fracStr))
                    return $"{wholeIn}\"";
                return $"{wholeIn} {fracStr}\"";
            }
            else
            {
                if (wholeIn == 0 && string.IsNullOrEmpty(fracStr))
                    return $"{feet}'";
                if (wholeIn == 0)
                    return $"{feet}'-{fracStr}\"";
                if (string.IsNullOrEmpty(fracStr))
                    return $"{feet}'-{wholeIn}\"";
                return $"{feet}'-{wholeIn} {fracStr}\"";
            }
        }

        private static string FractionFromEighths(double frac)
        {
            int eighths = (int)Math.Round(frac * 8.0);
            if (eighths == 0) return "";

            int num = eighths;
            int den = 8;
            int g = Gcd(num, den);
            num /= g;
            den /= g;

            return $"{num}/{den}";
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a == 0 ? 1 : a;
        }

        // =====================================================================
        // SPOOL-SHEET STYLE SORTING / FILTERING
        // =====================================================================

        private static bool IsWeldPart(FabricationPart part)
        {
            if (part == null)
                return false;

            string combined = string.Join(" ",
                part.Name ?? string.Empty,
                GetParamString(part, "Alias"),
                GetParamString(part, "Product Entry"),
                GetParamString(part, "CID"))
                .ToUpperInvariant();

            return combined.Contains("WELD");
        }

        private static bool IsJointPart(FabricationPart part)
        {
            if (part == null)
                return false;

            return GetClassificationText(part).IndexOf("JOINT", StringComparison.Ordinal) >= 0;
        }

        private static bool IsGasketPart(FabricationPart part)
        {
            if (part == null)
                return false;

            return GetClassificationText(part).Contains("GASKET");
        }

        private static bool IsBoltKitPart(FabricationPart part)
        {
            if (part == null)
                return false;

            string combined = GetClassificationText(part);
            return combined.Contains("BOLT KIT") ||
                   combined.Contains("BOLTKIT") ||
                   (combined.Contains("BOLT") && combined.Contains("KIT"));
        }

        private static string GetClassificationText(FabricationPart part)
        {
            return string.Join(" ",
                part.Name ?? string.Empty,
                GetParamString(part, "Alias"),
                GetParamString(part, "Product Entry"),
                GetParamString(part, "eM_Fitting Type"),
                GetParamString(part, "eM_Service Type"),
                GetParamString(part, "Service Type"),
                GetParamString(part, PARAM_DESCRIPTION),
                GetParamString(part, PARAM_S_MATERIAL),
                GetParamString(part, "CID"))
                .ToUpperInvariant();
        }

        /// <summary>
        /// Some straight runs are typed as FITTING in eM metadata; use Product Long Description as a hint.
        /// </summary>
        private static bool LooksLikeStraightPipeFromLongDescription(FabricationPart part)
        {
            string d = (GetParamString(part, PARAM_DESCRIPTION) ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(d))
                return false;

            if (d.IndexOf("ELBOW", StringComparison.Ordinal) >= 0) return false;
            if (d.IndexOf("TEE", StringComparison.Ordinal) >= 0) return false;
            if (d.IndexOf("REDUCER", StringComparison.Ordinal) >= 0) return false;
            if (d.IndexOf("CAP ", StringComparison.Ordinal) >= 0) return false;
            if (d.IndexOf("COUPLING", StringComparison.Ordinal) >= 0) return false;
            if (d.IndexOf("UNION", StringComparison.Ordinal) >= 0) return false;
            if (d.IndexOf("FLANGE", StringComparison.Ordinal) >= 0) return false;

            return d.IndexOf("PIPE", StringComparison.Ordinal) >= 0;
        }

        private static int GetFabricationSortPriority(FabricationPart part)
        {
            if (part == null)
                return 99;

            string fittingType = GetParamString(part, "eM_Fitting Type");
            string serviceType = GetParamString(part, "eM_Service Type");

            bool isPipework = serviceType.IndexOf("PIPEWORK", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isValve = serviceType.IndexOf("VALVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           fittingType.IndexOf("VALVE", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isValve &&
                isPipework &&
                fittingType.IndexOf("STRAIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;

            if (!isValve &&
                isPipework &&
                fittingType.IndexOf("FITTING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (LooksLikeStraightPipeFromLongDescription(part))
                    return 0;
                return 1;
            }

            if (isValve)
                return 2;

            string alias = GetParamString(part, "Alias").ToUpperInvariant();
            if (alias.Contains("PIPE") || alias.Contains("STRAIGHT"))
                return 0;

            if (alias.Contains("FITTING"))
                return 1;

            if (alias.Contains("VALVE"))
                return 2;

            string length = GetParamString(part, "Length");
            string angle = GetParamString(part, "Angle");
            string combined = string.Join(" ",
                part.Name ?? string.Empty,
                alias,
                GetParamString(part, "Product Entry"),
                GetParamString(part, PARAM_DESCRIPTION),
                GetParamString(part, "CID"))
                .ToUpperInvariant();

            if (combined.Contains("VALVE"))
                return 2;

            if (!string.IsNullOrWhiteSpace(length) &&
                string.IsNullOrWhiteSpace(angle))
                return 0;

            if (combined.Contains("PIPE") || combined.Contains("STRAIGHT"))
                return 0;

            return 1;
        }

        private static string GetFabricationItemGroupingKey(FabricationPart part)
        {
            List<string> pieces = new List<string>
            {
                NormalizeGroupingToken(part?.Name),
                NormalizeGroupingToken(GetParamString(part, PARAM_S_SIZE)),
                NormalizeGroupingToken(GetParamString(part, "Alias")),
                NormalizeGroupingToken(GetParamString(part, "Service Type")),
                NormalizeGroupingToken(GetParamString(part, "Length")),
                NormalizeGroupingToken(GetParamString(part, "Angle"))
            };

            return string.Join("|", pieces);
        }

        private static string NormalizeGroupingToken(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
        }

        // =====================================================================
        // GROUPING STRUCTS
        // =====================================================================

        private class BomOptions
        {
            public string ProjectName;
            public string Level;
            public string Area;
            public string CreatedBy;
            public string DateText;
            public string SavePath;
            /// <summary>
            /// Large PDF header title. Empty uses <see cref="DEFAULT_BOM_TITLE"/> (ribbon Pipe BOM). Plot Packages sets this from the output filename stem.
            /// </summary>
            public string BomTitle;
            public bool IncludeGaskets;
            public bool IncludeBoltKits;
            public string LogoPath;
        }

        private struct BomKey : IEquatable<BomKey>
        {
            public string Size;
            public string Description;
            public string Material;
            public bool IsLengthRow;

            public bool Equals(BomKey other)
            {
                return IsLengthRow == other.IsLengthRow &&
                       string.Equals(Size, other.Size, StringComparison.Ordinal) &&
                       string.Equals(Description, other.Description, StringComparison.Ordinal) &&
                       string.Equals(Material, other.Material, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) =>
                obj is BomKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 23 + IsLengthRow.GetHashCode();
                    h = h * 23 + (Size ?? "").GetHashCode();
                    h = h * 23 + (Description ?? "").GetHashCode();
                    h = h * 23 + (Material ?? "").GetHashCode();
                    return h;
                }
            }
        }

        private class BomAgg
        {
            public int Count;
            public double TotalLengthFt;
            public int SortPriority;
            public string SortKey;
        }
    }

    [XmlRoot("PipeBomLogoStore")]
    public sealed class PipeBomPerProjectLogoStore
    {
        public PipeBomPerProjectLogoStore()
        {
            Entries = new List<PipeBomPerProjectLogoEntry>();
        }

        [XmlElement("Entry")]
        public List<PipeBomPerProjectLogoEntry> Entries { get; set; }
    }

    public sealed class PipeBomPerProjectLogoEntry
    {
        [XmlAttribute("projectKey")]
        public string ProjectKey { get; set; }

        [XmlAttribute("logoPath")]
        public string LogoPath { get; set; }
    }
}
