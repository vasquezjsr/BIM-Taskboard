using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace SpoolingSavantV3Exports
{
    internal static class SsSavantDocumentSharedParameters
    {
        private const string BootstrapTypeName = "SpoolingSavantV3Exports.Workers.SsSavantSharedParameterBootstrap";
        private const string ConfigureMethodName = "ConfigureApplicationSharedParameterFile";
        private const string EnsureMethodName = "EnsureAllForDocument";

        internal static void Register(UIControlledApplication application)
        {
            if (application?.ControlledApplication == null)
                return;

            application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
        }

        internal static void Unregister(UIControlledApplication application)
        {
            if (application?.ControlledApplication == null)
                return;

            application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
        }

        private static void OnApplicationInitialized(object sender, ApplicationInitializedEventArgs e)
        {
            try
            {
                Application app = sender as Application;
                if (app == null)
                    return;

                InvokeBootstrap(app, null, ConfigureMethodName);
            }
            catch
            {
                // Parameter setup must never block Revit startup.
            }
        }

        private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            try
            {
                Document doc = e?.Document;
                Application app = doc?.Application ?? sender as Application;
                if (app == null)
                    return;

                InvokeBootstrap(app, doc, EnsureMethodName);
            }
            catch
            {
                // Parameter binding must never block document open.
            }
        }

        private static void InvokeBootstrap(Application app, Document doc, string methodName)
        {
            if (app == null || string.IsNullOrWhiteSpace(methodName))
                return;

            string addinDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string programDataHotload = Path.Combine(addinDirectory, "Hotload");
            if (!SsSavantWorkerHotloadResolve.TryGetWorkerLoadPaths(
                    programDataHotload,
                    addinDirectory,
                    out string resolverDirectory,
                    out string workerAssemblyPath))
            {
                return;
            }

            using (new SsSavantHotloadAssemblyResolver(resolverDirectory, addinDirectory))
            {
                Assembly workerAssembly = SsSavantHotloadWorkerAssemblyLoad.LoadFromPath(workerAssemblyPath);
                Type bootstrapType = workerAssembly.GetType(BootstrapTypeName, false, false);
                MethodInfo method = bootstrapType?.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    return;

                if (doc == null)
                    method.Invoke(null, new object[] { app });
                else
                    method.Invoke(null, new object[] { app, doc });
            }
        }
    }
}
