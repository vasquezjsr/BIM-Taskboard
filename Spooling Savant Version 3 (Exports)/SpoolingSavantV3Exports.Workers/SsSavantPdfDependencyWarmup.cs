using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SpoolingSavantV3Exports.Workers
{
        /// <summary>
        /// Resolves PDF/Excel satellites for Plot Packages and similar ExternalEvent handlers.
        /// Reuses an already-loaded copy when Revit preloaded it from another path (avoids HRESULT 0x80131040).
        /// </summary>
        internal static class SsSavantPdfDependencyWarmup
    {
        private static readonly object Gate = new object();

        internal static void EnsurePdfDependenciesLoaded()
        {
            try
            {
                lock (Gate)
                {
                    if (IsItextSharpAlreadyLoaded())
                        return;

                    foreach (string dll in new[] { "BouncyCastle.Cryptography.dll", "itextsharp.dll" })
                    {
                        string simpleName = Path.GetFileNameWithoutExtension(dll);
                        if (IsAssemblyLoaded(simpleName))
                            continue;

                        foreach (string dir in SsSavantWorkerAssemblyPaths.ResolveSatelliteProbeDirectories(typeof(SsSavantPdfDependencyWarmup).Assembly))
                        {
                            if (string.IsNullOrWhiteSpace(dir))
                                continue;

                            string path = Path.Combine(dir, dll);
                            if (!File.Exists(path))
                                continue;

                            try
                            {
                                Assembly.LoadFrom(Path.GetFullPath(path));
                                break;
                            }
                            catch (FileLoadException)
                            {
                                if (IsAssemblyLoaded(simpleName))
                                    break;
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsItextSharpAlreadyLoaded()
        {
            return IsAssemblyLoaded("itextsharp");
        }

        private static bool IsAssemblyLoaded(string simpleName)
        {
            try
            {
                return AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}
