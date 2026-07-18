using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SpoolingSavantV3Exports
{
    internal static class SsSavantAssemblyMemberSync
    {
        private const string RunnerTypeName = "SpoolingSavantV3Exports.Workers.SpoolingManager.Services.AssemblyMemberSyncRunner";
        private const string SyncAssembliesMethodName = "SyncAssemblies";
        private const string SyncForElementsMethodName = "SyncForElements";
        private const string PackageParameterName = "S-Package";
        private const int FabricationItemNumberBuiltIn = -1140975;

        private static bool _registered;
        private static UIControlledApplication _application;
        private static ExternalEvent _syncExternalEvent;
        private static AssemblyMemberSyncHandler _syncHandler;
        private static bool _isProcessing;

        private static readonly object SyncRoot = new object();
        private static readonly List<PendingAssemblySync> PendingAssemblySyncs = new List<PendingAssemblySync>();
        private static readonly List<PendingElementSync> PendingElementSyncs = new List<PendingElementSync>();

        private static string LogPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk",
                "Revit",
                "Addins",
                "2024",
                "Spooling-Savant-V3-Exports",
                "SpoolingManager",
                "TestingReports",
                "AssemblyMemberSync.log");

        private readonly struct PendingAssemblySync
        {
            internal PendingAssemblySync(Document document, ElementId assemblyId)
            {
                Document = document;
                AssemblyId = assemblyId;
            }

            internal Document Document { get; }
            internal ElementId AssemblyId { get; }
        }

        private readonly struct PendingElementSync
        {
            internal PendingElementSync(Document document, ElementId elementId)
            {
                Document = document;
                ElementId = elementId;
            }

            internal Document Document { get; }
            internal ElementId ElementId { get; }
        }

        internal static void Register(UIControlledApplication application)
        {
            if (application?.ControlledApplication == null || _registered)
                return;

            _application = application;
            _syncHandler = new AssemblyMemberSyncHandler();
            _syncExternalEvent = ExternalEvent.Create(_syncHandler);
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
            application.Idling += OnIdling;
            _registered = true;
            WriteLog("Registered assembly member sync.");
        }

        internal static void Unregister(UIControlledApplication application)
        {
            if (application?.ControlledApplication == null || !_registered)
                return;

            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            application.Idling -= OnIdling;
            _registered = false;
            WriteLog("Unregistered assembly member sync.");
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (_isProcessing || e == null)
                return;

            if (IsOwnTransaction(e))
                return;

            // Only react to explicit assembly-membership operations ("Add Element to Assembly",
            // "Finish Assembly Edit", "Remove Element from Assembly", "Edit Assembly", "Create Assembly", ...).
            // Placing a fabrication part (which splits the host pipe and spawns new segments), moving elements,
            // deleting, and parameter edits must NOT kick off numbering/tagging/dimensioning. The user drives all
            // processing by adding elements with Revit's "Add to Assembly" feature.
            if (!IsAssemblyMembershipTransaction(e))
                return;

            Document doc = e.GetDocument();
            if (doc == null || doc.IsFamilyDocument)
                return;

            HashSet<ElementId> assemblyIds = new HashSet<ElementId>();
            List<PendingElementSync> orphanElements = new List<PendingElementSync>();

            ICollection<ElementId> addedElementIds = e.GetAddedElementIds();
            if (addedElementIds != null)
            {
                foreach (ElementId elementId in addedElementIds)
                    CollectFromElement(doc, elementId, assemblyIds, orphanElements, isNew: true);
            }

            ICollection<ElementId> modifiedElementIds = e.GetModifiedElementIds();
            if (modifiedElementIds != null)
            {
                foreach (ElementId elementId in modifiedElementIds)
                    CollectFromElement(doc, elementId, assemblyIds, orphanElements, isNew: false);
            }

            if (assemblyIds.Count == 0 && orphanElements.Count == 0)
                return;

            lock (SyncRoot)
            {
                foreach (ElementId assemblyId in assemblyIds)
                    PendingAssemblySyncs.Add(new PendingAssemblySync(doc, assemblyId));
                PendingElementSyncs.AddRange(orphanElements);
            }

            WriteLog(
                $"DocumentChanged doc={doc.Title} assemblies={assemblyIds.Count} orphanElements={orphanElements.Count} tx=[{string.Join(", ", e.GetTransactionNames() ?? Array.Empty<string>())}]");

            RequestSync();
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            bool hasPending;
            lock (SyncRoot)
            {
                hasPending = PendingAssemblySyncs.Count > 0 || PendingElementSyncs.Count > 0;
            }

            if (hasPending && !_isProcessing)
                RequestSync();
        }

        private static void RequestSync()
        {
            try
            {
                _syncExternalEvent?.Raise();
            }
            catch (Exception ex)
            {
                WriteLog("ExternalEvent.Raise failed: " + ex.Message);
            }
        }

        private static bool IsOwnTransaction(DocumentChangedEventArgs e)
        {
            ICollection<string> transactionNames = e.GetTransactionNames();
            if (transactionNames == null)
                return false;

            foreach (string transactionName in transactionNames)
            {
                if (!string.IsNullOrWhiteSpace(transactionName) &&
                    transactionName.StartsWith("Spooling Savant V3 (Exports)", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAssemblyMembershipTransaction(DocumentChangedEventArgs e)
        {
            ICollection<string> transactionNames = e.GetTransactionNames();
            if (transactionNames == null)
                return false;

            foreach (string transactionName in transactionNames)
            {
                if (!string.IsNullOrWhiteSpace(transactionName) &&
                    transactionName.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool NeedsMemberSync(Element element)
        {
            if (element == null)
                return false;

            // Hangers are numbered/tagged on a separate path — do not queue assembly sync for them.
            try
            {
                Category category = element.Category;
                if (category != null && category.Id.Value == (long)BuiltInCategory.OST_FabricationHangers)
                    return false;
            }
            catch
            {
            }

            if (NeedsPackageSync(element))
                return true;

            Parameter itemNumber = element.get_Parameter((BuiltInParameter)FabricationItemNumberBuiltIn);
            if (itemNumber == null)
                return false;

            string value = itemNumber.StorageType == StorageType.String
                ? itemNumber.AsString()
                : itemNumber.AsValueString();
            return string.IsNullOrWhiteSpace(value);
        }

        private static bool IsFabricationHangerElement(Element element)
        {
            if (element == null)
                return false;
            try
            {
                Category category = element.Category;
                return category != null && category.Id.Value == (long)BuiltInCategory.OST_FabricationHangers;
            }
            catch
            {
                return false;
            }
        }

        private static void CollectFromElement(
            Document doc,
            ElementId elementId,
            ISet<ElementId> assemblyIds,
            IList<PendingElementSync> orphanElements,
            bool isNew)
        {
            Element element = doc.GetElement(elementId);
            if (element == null)
                return;

            // Adding/editing hangers must not kick off pipe/fitting numbering and sheet retag.
            if (IsFabricationHangerElement(element))
                return;

            AssemblyInstance assembly = element as AssemblyInstance;
            if (assembly != null)
            {
                assemblyIds.Add(assembly.Id);
                return;
            }

            ElementId assemblyInstanceId = element.AssemblyInstanceId;
            if (assemblyInstanceId != null && assemblyInstanceId != ElementId.InvalidElementId)
            {
                if (isNew || NeedsMemberSync(element))
                    assemblyIds.Add(assemblyInstanceId);
                return;
            }

            if (!isNew && HasFabricationItemNumber(element))
            {
                orphanElements.Add(new PendingElementSync(doc, elementId));
                return;
            }

            if (isNew || NeedsMemberSync(element))
                orphanElements.Add(new PendingElementSync(doc, elementId));
        }

        private static bool HasFabricationItemNumber(Element element)
        {
            Parameter itemNumber = element?.get_Parameter((BuiltInParameter)FabricationItemNumberBuiltIn);
            if (itemNumber == null)
                return false;

            string value = itemNumber.StorageType == StorageType.String
                ? itemNumber.AsString()
                : itemNumber.AsValueString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool NeedsPackageSync(Element element)
        {
            Parameter parameter = element.LookupParameter(PackageParameterName);
            if (parameter == null)
                return true;

            string value = parameter.StorageType == StorageType.String
                ? parameter.AsString()
                : parameter.AsValueString();
            return string.IsNullOrWhiteSpace(value);
        }

        private static void TakePending(out List<PendingAssemblySync> assemblies, out List<PendingElementSync> elements)
        {
            lock (SyncRoot)
            {
                assemblies = PendingAssemblySyncs.Count == 0
                    ? new List<PendingAssemblySync>()
                    : new List<PendingAssemblySync>(PendingAssemblySyncs);
                PendingAssemblySyncs.Clear();

                elements = PendingElementSyncs.Count == 0
                    ? new List<PendingElementSync>()
                    : new List<PendingElementSync>(PendingElementSyncs);
                PendingElementSyncs.Clear();
            }
        }

        private static void InvokeRunner(
            UIApplication app,
            Document doc,
            ICollection<ElementId> assemblyIds,
            ICollection<ElementId> elementIds)
        {
            if (app?.Application == null || doc == null)
                return;

            string addinDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string programDataHotload = Path.Combine(addinDirectory, "Hotload");
            if (!SsSavantWorkerHotloadResolve.TryGetWorkerLoadPaths(
                    programDataHotload,
                    addinDirectory,
                    out string resolverDirectory,
                    out string workerAssemblyPath))
            {
                WriteLog("InvokeRunner failed: worker DLL not found.");
                return;
            }

            using (new SsSavantHotloadAssemblyResolver(resolverDirectory, addinDirectory))
            {
                Assembly workerAssembly = SsSavantHotloadWorkerAssemblyLoad.LoadFromPath(workerAssemblyPath);
                Type runnerType = workerAssembly.GetType(RunnerTypeName, false, false);
                if (runnerType == null)
                {
                    WriteLog("InvokeRunner failed: runner type not found.");
                    return;
                }

                if (assemblyIds != null && assemblyIds.Count > 0)
                {
                    MethodInfo syncAssemblies = runnerType.GetMethod(
                        SyncAssembliesMethodName,
                        BindingFlags.Public | BindingFlags.Static);
                    syncAssemblies?.Invoke(null, new object[] { app.Application, doc, assemblyIds });
                }

                if (elementIds != null && elementIds.Count > 0)
                {
                    MethodInfo syncForElements = runnerType.GetMethod(
                        SyncForElementsMethodName,
                        BindingFlags.Public | BindingFlags.Static);
                    syncForElements?.Invoke(null, new object[] { app.Application, doc, elementIds });
                }
            }
        }

        private static void WriteLog(string message)
        {
            try
            {
                string path = LogPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + message + "\r\n");
            }
            catch
            {
            }
        }

        private sealed class AssemblyMemberSyncHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                TakePending(out List<PendingAssemblySync> assemblyBatch, out List<PendingElementSync> elementBatch);
                if ((assemblyBatch.Count == 0 && elementBatch.Count == 0) || app == null)
                    return;

                _isProcessing = true;
                try
                {
                    WriteLog($"Execute start assemblies={assemblyBatch.Count} elements={elementBatch.Count}");

                    foreach (KeyValuePair<Document, List<ElementId>> group in GroupAssembliesByDocument(assemblyBatch))
                    {
                        Document doc = group.Key;
                        if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument)
                            continue;

                        InvokeRunner(app, doc, group.Value, null);
                    }

                    foreach (KeyValuePair<Document, List<ElementId>> group in GroupElementsByDocument(elementBatch))
                    {
                        Document doc = group.Key;
                        if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument)
                            continue;

                        InvokeRunner(app, doc, null, group.Value);
                    }

                    WriteLog("Execute finished.");
                }
                finally
                {
                    _isProcessing = false;
                }
            }

            public string GetName() => nameof(AssemblyMemberSyncHandler);
        }

        private static Dictionary<Document, List<ElementId>> GroupAssembliesByDocument(List<PendingAssemblySync> batch)
        {
            Dictionary<Document, List<ElementId>> grouped = new Dictionary<Document, List<ElementId>>();
            foreach (PendingAssemblySync item in batch)
            {
                if (item.Document == null || item.AssemblyId == null || item.AssemblyId == ElementId.InvalidElementId)
                    continue;

                if (!grouped.TryGetValue(item.Document, out List<ElementId> assemblyIds))
                {
                    assemblyIds = new List<ElementId>();
                    grouped[item.Document] = assemblyIds;
                }

                if (!assemblyIds.Contains(item.AssemblyId))
                    assemblyIds.Add(item.AssemblyId);
            }

            return grouped;
        }

        private static Dictionary<Document, List<ElementId>> GroupElementsByDocument(List<PendingElementSync> batch)
        {
            Dictionary<Document, List<ElementId>> grouped = new Dictionary<Document, List<ElementId>>();
            foreach (PendingElementSync item in batch)
            {
                if (item.Document == null || item.ElementId == null || item.ElementId == ElementId.InvalidElementId)
                    continue;

                if (!grouped.TryGetValue(item.Document, out List<ElementId> elementIds))
                {
                    elementIds = new List<ElementId>();
                    grouped[item.Document] = elementIds;
                }

                if (!elementIds.Contains(item.ElementId))
                    elementIds.Add(item.ElementId);
            }

            return grouped;
        }
    }
}
