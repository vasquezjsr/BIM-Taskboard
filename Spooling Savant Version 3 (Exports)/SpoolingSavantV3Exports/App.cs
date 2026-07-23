using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SpoolingSavantV3Exports
{
    public class App : IExternalApplication
    {
        private const string TabName = "Spooling Savant 3.0";
        private const string PanelName = "Spooling Savant";
        private const string LegacyTabName = "Spooling Savant V3 (Exports)";
        private const string ProductDisplayName = "Spooling Savant 3.0";
        private const string CreateSpoolV2ButtonName = "CreateSpoolV2Button";
        private const string TraceSpoolV2ButtonName = "TraceSpoolV2Button";
        private const string LegacyImportPcfButtonName = "ImportPcfButton";
        private const string LegacyExportPcfButtonName = "ExportPcfButton";

        private static UIControlledApplication _uiControlledApplication;
        private static ExternalEvent _ssManagerDocumentOpenedEvent;
        private static SsManagerDocumentOpenedHandler _ssManagerDocumentOpenedHandler;
        private static bool _hotloadDependencyResolveRegistered;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                string addinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                EnsureHotloadWorkerDependencies(addinDir);
                SsSavantInstallOptions.LoadFromAddinDirectory(addinDir);

                _uiControlledApplication = application;
                EnsureTab(application);
                RegisterSsManagerPane(application);
                RegisterButtons(application);
                CleanupLegacyPcfButtons(application);
                CleanupLegacyReleasePanel(application);
                CleanupLegacyProductTab(application);
                RegisterSsManagerDocumentRefresh(application);
                SsSavantDocumentSharedParameters.Register(application);
                SsSavantAssemblyMemberSync.Register(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(ProductDisplayName, "Failed to create the Spooling Savant ribbon.\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                SsSavantDocumentSharedParameters.Unregister(application);
                SsSavantAssemblyMemberSync.Unregister(application);
                if (_uiControlledApplication?.ControlledApplication != null)
                {
                    _uiControlledApplication.ControlledApplication.DocumentOpened -= OnDocumentOpenedRefreshSsManager;
                }
            }
            catch
            {
            }

            return Result.Succeeded;
        }

        private static void EnsureHotloadWorkerDependencies(string addinDirectory)
        {
            if (_hotloadDependencyResolveRegistered || string.IsNullOrEmpty(addinDirectory))
                return;

            string hotloadDir = Path.Combine(addinDirectory, "Hotload");

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    string simpleName = new AssemblyName(args.Name).Name;
                    if (string.IsNullOrEmpty(simpleName))
                        return null;

                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (string.Equals(asm.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                                return asm;
                        }
                        catch
                        {
                        }
                    }

                    foreach (string dir in new[] { hotloadDir, addinDirectory })
                    {
                        if (string.IsNullOrWhiteSpace(dir))
                            continue;

                        string candidate = Path.Combine(dir, simpleName + ".dll");
                        if (File.Exists(candidate))
                            return SsSavantHotloadWorkerAssemblyLoad.LoadFromPath(candidate);
                    }
                }
                catch
                {
                }

                return null;
            };

            _hotloadDependencyResolveRegistered = true;

            foreach (string dllName in new[]
                     {
                         "BouncyCastle.Cryptography.dll",
                         "itextsharp.dll",
                         "ClosedXML.Parser.dll",
                         "DocumentFormat.OpenXml.Framework.dll",
                         "DocumentFormat.OpenXml.dll",
                         "ExcelNumberFormat.dll",
                         "RBush.dll",
                         "SixLabors.Fonts.dll",
                         "ClosedXML.dll"
                     })
            {
                foreach (string dir in new[] { hotloadDir, addinDirectory })
                {
                    if (string.IsNullOrWhiteSpace(dir))
                        continue;

                    string path = Path.Combine(dir, dllName);
                    if (!File.Exists(path))
                        continue;

                    try
                    {
                        SsSavantHotloadWorkerAssemblyLoad.LoadFromPath(path);
                        break;
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void EnsureTab(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch
            {
                // Tab already exists.
            }
        }

        private static void RegisterSsManagerPane(UIControlledApplication application)
        {
            application.RegisterDockablePane(
                SsManagerPaneIds.PaneId,
                ProductDisplayName,
                new SsManagerPaneProvider(SsManagerPaneHost.Instance));
        }

        private static void RegisterButtons(UIControlledApplication application)
        {
            RibbonPanel panel = GetOrCreatePanel(application, TabName, PanelName);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string iconPath = Path.Combine(Path.GetDirectoryName(assemblyPath) ?? string.Empty, "Icons", "SS Manager32.png");
            System.Windows.Media.Imaging.BitmapImage icon = File.Exists(iconPath) ? LoadBitmapImage(iconPath) : null;

            if (FindPushButton(panel, "SsManagerV2Button") == null)
            {
                PushButtonData ssManagerButton = new PushButtonData(
                    "SsManagerV2Button",
                    "Spooling\nSavant",
                    assemblyPath,
                    typeof(SsManagerBridgeLauncher).FullName)
                {
                    ToolTip = "Open Spooling Savant (Together w/ BIM Boardroom).",
                    LongDescription = "Manage spool assemblies, packages, sheets, plotting, and BIM Boardroom export."
                };

                if (icon != null)
                {
                    ssManagerButton.LargeImage = icon;
                    ssManagerButton.Image = icon;
                }

                panel.AddItem(ssManagerButton);
            }

            if (FindPushButton(panel, CreateSpoolV2ButtonName) == null)
            {
                PushButtonData createSpoolButton = new PushButtonData(
                    CreateSpoolV2ButtonName,
                    "Create\nSpool",
                    assemblyPath,
                    typeof(CreateSpoolWithSsManagerBridgeLauncher).FullName)
                {
                    ToolTip = "Create a spool assembly from the current selection.",
                    LongDescription = "Creates a spool assembly from the current selection: assembly name, naming category, and optional S-Package (Package Name)."
                };

                if (icon != null)
                {
                    createSpoolButton.LargeImage = icon;
                    createSpoolButton.Image = icon;
                }

                panel.AddItem(createSpoolButton);
            }

            if (FindPushButton(panel, TraceSpoolV2ButtonName) == null)
            {
                PushButtonData traceSpoolButton = new PushButtonData(
                    TraceSpoolV2ButtonName,
                    "Trace\nSpool",
                    assemblyPath,
                    typeof(TraceSpoolWithSsManagerBridgeLauncher).FullName)
                {
                    ToolTip = "Trace a spool by picking endpoints, then create the assembly.",
                    LongDescription = "Pick fabrication (or native pipe) endpoints to gather a connected path, then create the spool assembly."
                };

                if (icon != null)
                {
                    traceSpoolButton.LargeImage = icon;
                    traceSpoolButton.Image = icon;
                }

                panel.AddItem(traceSpoolButton);
            }
        }

        /// <summary>
        /// Import/Export PCF moved to a separate add-in — remove leftover ribbon buttons.
        /// </summary>
        private static void CleanupLegacyPcfButtons(UIControlledApplication application)
        {
            if (application == null)
                return;

            foreach (RibbonPanel panel in application.GetRibbonPanels(TabName).ToList())
            {
                foreach (string name in new[] { LegacyImportPcfButtonName, LegacyExportPcfButtonName })
                {
                    PushButton button = FindPushButton(panel, name);
                    if (button != null)
                        TryRemoveRibbonItem(panel, button);
                }
            }
        }

        /// <summary>
        /// Removes the old Release overflow panel from earlier builds.
        /// </summary>
        private static void CleanupLegacyReleasePanel(UIControlledApplication application)
        {
            if (application == null)
                return;

            foreach (RibbonPanel panel in application.GetRibbonPanels(TabName).ToList())
            {
                if (!string.Equals(panel.Name, "Release", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (PushButton pushButton in EnumeratePushButtons(panel).ToList())
                    TryRemoveRibbonItem(panel, pushButton);
            }
        }

        /// <summary>
        /// Removes leftover ribbon items from older "Spooling Savant 3.0" tab builds.
        /// </summary>
        private static void CleanupLegacyProductTab(UIControlledApplication application)
        {
            if (application == null)
                return;

            try
            {
                foreach (RibbonPanel panel in application.GetRibbonPanels(LegacyTabName).ToList())
                {
                    foreach (PushButton pushButton in EnumeratePushButtons(panel).ToList())
                        TryRemoveRibbonItem(panel, pushButton);
                }
            }
            catch
            {
                // Legacy tab may not exist.
            }
        }

        private static RibbonPanel GetOrCreatePanel(UIControlledApplication application, string tabName, string panelName)
        {
            foreach (RibbonPanel panel in application.GetRibbonPanels(tabName))
            {
                if (string.Equals(panel.Name, panelName, StringComparison.OrdinalIgnoreCase))
                    return panel;
            }

            return application.CreateRibbonPanel(tabName, panelName);
        }

        private static IEnumerable<PushButton> EnumeratePushButtons(RibbonPanel panel)
        {
            if (panel == null)
                yield break;

            foreach (RibbonItem item in panel.GetItems())
            {
                if (item is PushButton pushButton)
                    yield return pushButton;
            }
        }

        private static PushButton FindPushButton(RibbonPanel panel, string commandName)
        {
            if (panel == null || string.IsNullOrWhiteSpace(commandName))
                return null;

            foreach (PushButton pushButton in EnumeratePushButtons(panel))
            {
                if (string.Equals(pushButton.Name, commandName, StringComparison.OrdinalIgnoreCase))
                    return pushButton;
            }

            return null;
        }

        private static void TryRemoveRibbonItem(RibbonPanel panel, RibbonItem item)
        {
            if (panel == null || item == null)
                return;

            foreach (string methodName in new[] { "RemoveItem", "Remove" })
            {
                try
                {
                    MethodInfo removeItem = panel.GetType().GetMethod(
                        methodName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(RibbonItem) },
                        null);
                    if (removeItem == null)
                        continue;

                    removeItem.Invoke(panel, new object[] { item });
                    return;
                }
                catch
                {
                }
            }
        }

        private static System.Windows.Media.Imaging.BitmapImage LoadBitmapImage(string path)
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static void RegisterSsManagerDocumentRefresh(UIControlledApplication application)
        {
            _ssManagerDocumentOpenedHandler = new SsManagerDocumentOpenedHandler();
            _ssManagerDocumentOpenedEvent = ExternalEvent.Create(_ssManagerDocumentOpenedHandler);
            application.ControlledApplication.DocumentOpened += OnDocumentOpenedRefreshSsManager;
        }

        private static void OnDocumentOpenedRefreshSsManager(object sender, DocumentOpenedEventArgs e)
        {
            try
            {
                _ssManagerDocumentOpenedHandler?.RequestRefresh();
                _ssManagerDocumentOpenedEvent?.Raise();
            }
            catch
            {
            }
        }
    }

    public abstract class HotloadCommandBase : IExternalCommand
    {
        protected abstract string WorkerAssemblyFileName { get; }
        protected abstract string WorkerTypeName { get; }
        protected abstract string ToolName { get; }

        public virtual Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                string programDataHotload = GetHotloadDirectory(commandData);
                string addinDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string fallbackMissingPath = Path.Combine(programDataHotload, WorkerAssemblyFileName);
                if (!SsSavantWorkerHotloadResolve.TryGetWorkerLoadPaths(programDataHotload, addinDirectory, out string hotloadDirectory, out string workerPath))
                {
                    TaskDialog.Show(ToolName, "Worker DLL not found:\n" + fallbackMissingPath);
                    return Result.Cancelled;
                }

                using (new SsSavantHotloadAssemblyResolver(hotloadDirectory, addinDirectory))
                {
                    SsSavantHotloadWorkerAssemblyLoad.InvalidateWorkersCache();
                    Assembly workerAssembly = LoadHotloadWorkerAssembly(workerPath);
                    Type workerType = workerAssembly.GetType(WorkerTypeName, false, false);
                    if (workerType == null)
                    {
                        TaskDialog.Show(ToolName, "Worker type not found:\n" + WorkerTypeName);
                        return Result.Failed;
                    }

                    IExternalCommand command = Activator.CreateInstance(workerType) as IExternalCommand;
                    if (command == null)
                    {
                        TaskDialog.Show(ToolName, "Worker type does not implement IExternalCommand:\n" + WorkerTypeName);
                        return Result.Failed;
                    }

                    return command.Execute(commandData, ref message, elements);
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show(ToolName, ex.ToString());
                return Result.Failed;
            }
        }

        protected virtual string GetHotloadDirectory(ExternalCommandData commandData)
        {
            string version = commandData.Application.Application.VersionNumber;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk",
                "Revit",
                "Addins",
                version,
                "Spooling-Savant-V3-Exports",
                "Hotload");
        }

        protected static Assembly LoadHotloadWorkerAssembly(string assemblyPath)
        {
            return SsSavantHotloadWorkerAssemblyLoad.LoadFromPath(assemblyPath);
        }

        internal static Result TryEnsureSsManagerPane(
            UIApplication app,
            bool showPaneAfter,
            bool interactive)
        {
            const string workerAssemblyFileName = "SpoolingSavantV3Exports.Workers.dll";
            const string paneTypeName = "SpoolingSavantV3Exports.Workers.SpoolingManager.Views.SpoolingManagerPane";
            const string dialogTitle = "Spooling Savant 3.0";

            try
            {
                if (app == null)
                    return Result.Failed;

                string programDataHotload = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Autodesk",
                    "Revit",
                    "Addins",
                    app.Application.VersionNumber,
                    "Spooling-Savant-V3-Exports",
                    "Hotload");
                string addinDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string fallbackMissingPath = Path.Combine(programDataHotload, workerAssemblyFileName);
                if (!SsSavantWorkerHotloadResolve.TryGetWorkerLoadPaths(programDataHotload, addinDirectory, out string hotloadDirectory, out string workerPath))
                {
                    if (interactive)
                        TaskDialog.Show(dialogTitle, "Worker DLL not found:\n" + fallbackMissingPath);
                    return Result.Cancelled;
                }

                using (new SsSavantHotloadAssemblyResolver(hotloadDirectory, addinDirectory))
                {
                    // Drop any cached Workers load so Plot Packages / export use this rebuild, not an older shadow.
                    SsSavantHotloadWorkerAssemblyLoad.InvalidateWorkersCache();
                    Assembly workerAssembly = LoadHotloadWorkerAssembly(workerPath);
                    Type paneType = workerAssembly.GetType(paneTypeName, false, false);
                    if (paneType == null)
                    {
                        if (interactive)
                            TaskDialog.Show(dialogTitle, "Worker pane type not found:\n" + paneTypeName);
                        return Result.Failed;
                    }

                    object paneObject = Activator.CreateInstance(paneType);

                    PropertyInfo productKindProperty = paneType.GetProperty("ProductKind");
                    if (productKindProperty != null && productKindProperty.CanWrite)
                    {
                        try
                        {
                            object kindValue = Enum.Parse(productKindProperty.PropertyType, "Standard");
                            productKindProperty.SetValue(paneObject, kindValue);
                        }
                        catch
                        {
                        }
                    }

                    MethodInfo loadAssembliesMethod = paneType.GetMethod("LoadAssemblies");
                    loadAssembliesMethod?.Invoke(paneObject, new object[] { app });

                    UserControl paneControl = paneObject as UserControl;
                    if (paneControl == null)
                    {
                        if (interactive)
                            TaskDialog.Show(dialogTitle, "Loaded pane is not a WPF UserControl.");
                        return Result.Failed;
                    }

                    SsManagerPaneHost.Instance.SetPaneContent(paneControl);

                    if (showPaneAfter)
                        app.GetDockablePane(SsManagerPaneIds.PaneId).Show();

                    return Result.Succeeded;
                }
            }
            catch (Exception ex)
            {
                if (interactive)
                    TaskDialog.Show(dialogTitle, ex.ToString());
                return Result.Failed;
            }
        }
    }

    internal static class SsManagerPaneIds
    {
        internal static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("D4E9F2B3-5C6F-7081-9B02-3F5E7C9D1A24"));
    }

    internal sealed class SsManagerPaneProvider : IDockablePaneProvider
    {
        private readonly FrameworkElement _element;

        internal SsManagerPaneProvider(FrameworkElement element)
        {
            _element = element;
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _element;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }
    }

    internal sealed class SsManagerPaneHost : Page
    {
        private static readonly SsManagerPaneHost InstanceValue = new SsManagerPaneHost();
        internal static SsManagerPaneHost Instance => InstanceValue;

        private SsManagerPaneHost()
        {
        }

        internal void SetPaneContent(UserControl content)
        {
            Content = content;
        }
    }

    internal sealed class SsManagerDocumentOpenedHandler : IExternalEventHandler
    {
        private UIApplication _pendingApp;

        internal void RequestRefresh()
        {
            // Raised from DocumentOpened; actual app is supplied in Execute.
        }

        public void Execute(UIApplication app)
        {
            try
            {
                if (app == null)
                    return;

                HotloadCommandBase.TryEnsureSsManagerPane(app, showPaneAfter: false, interactive: false);
            }
            catch
            {
            }
        }

        public string GetName() => nameof(SsManagerDocumentOpenedHandler);
    }

    [Transaction(TransactionMode.Manual)]
    public class SsManagerBridgeLauncher : HotloadCommandBase
    {
        protected override string WorkerAssemblyFileName => "SpoolingSavantV3Exports.Workers.dll";
        protected override string WorkerTypeName => "SpoolingSavantV3Exports.Workers.SpoolingManager.Views.SpoolingManagerPane";
        protected override string ToolName => "Spooling Savant 3.0";

        public override Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                return TryEnsureSsManagerPane(commandData.Application, showPaneAfter: true, interactive: true);
            }
            catch (Exception ex)
            {
                TaskDialog.Show(ToolName, ex.ToString());
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CreateSpoolWithSsManagerBridgeLauncher : HotloadCommandBase
    {
        protected override string WorkerAssemblyFileName => "SpoolingSavantV3Exports.Workers.dll";
        protected override string WorkerTypeName => "SpoolingSavantV3Exports.Workers.SpoolingManager.Commands.CreateAssemblyCommand";
        protected override string ToolName => "Create Spool";
    }

    [Transaction(TransactionMode.Manual)]
    public class TraceSpoolWithSsManagerBridgeLauncher : HotloadCommandBase
    {
        protected override string WorkerAssemblyFileName => "SpoolingSavantV3Exports.Workers.dll";
        protected override string WorkerTypeName => "SpoolingSavantV3Exports.Workers.SpoolingManager.Commands.TraceSpoolCommand";
        protected override string ToolName => "Trace Spool";
    }
}
