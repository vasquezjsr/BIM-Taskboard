using System;
using System.IO;
using System.Reflection;

namespace SpoolingSavantV3Exports
{
    /// <summary>
    /// Resolves the folder and path used for <see cref="System.Reflection.Assembly.LoadFrom(string)"/> for <c>SpoolingSavantV3Exports.Workers.dll</c>.
    /// When <c>SPOOLING_SAVANT_V2_WORKERS_SOURCE</c>, <c>%LocalAppData%\Spooling-Savant-V3-Exports\LastBuiltWorkersBin.txt</c> (from the latest compile),
    /// or <see cref="SsSavantInstallOptions.RepositoryRoot"/> resolves to a dev build output folder, workers are copied to <c>%LocalAppData%\Spooling-Savant-V3-Exports\WorkerShadow\&lt;stamp&gt;</c> so Revit
    /// can load a new assembly after each rebuild without overwriting DLLs locked under ProgramData Hotload and without restarting Revit.
    /// </summary>
    internal static class SsSavantWorkerHotloadResolve
    {
        internal const string WorkersDllFileName = "SpoolingSavantV3Exports.Workers.dll";

        /// <summary>
        /// Environment variable: folder that contains <c>SpoolingSavantV3Exports.Workers.dll</c> (and satellites). Overrides repo bin discovery.
        /// </summary>
        internal const string WorkersSourceEnvironmentVariable = "SPOOLING_SAVANT_V2_WORKERS_SOURCE";

        /// <summary>
        /// Written by local <c>SpoolingSavantV3Exports.Workers.csproj</c> builds: first line is the absolute folder that contained <c>SpoolingSavantV3Exports.Workers.dll</c> last build (typically …\SpoolingSavantV3Exports.Workers\bin\Debug).
        /// Lets ribbon hotload shadow-copy the newest worker without configuring <c>repositoryRoot</c>.
        /// </summary>
        internal const string LastBuiltWorkersBinRelativePath = @"Spooling-Savant-V3-Exports\LastBuiltWorkersBin.txt";

        /// <summary>
        /// Chooses ProgramData Hotload or a shadow copy of the dev bin output. Returns false if the worker DLL is missing.
        /// </summary>
        internal static bool TryGetWorkerLoadPaths(
            string programDataHotloadDirectory,
            string addinDirectory,
            out string resolverDirectory,
            out string workerAssemblyPath)
        {
            resolverDirectory = programDataHotloadDirectory ?? string.Empty;
            workerAssemblyPath = Path.Combine(resolverDirectory, WorkersDllFileName);

            string devBin = TryResolveDevWorkersBinDirectory(addinDirectory);
            if (!string.IsNullOrEmpty(devBin) &&
                TryEnsureShadowCopy(devBin, out string shadowDir, out string shadowDll))
            {
                resolverDirectory = shadowDir;
                workerAssemblyPath = shadowDll;
                return File.Exists(workerAssemblyPath);
            }

            return File.Exists(workerAssemblyPath);
        }

        private static string TryResolveDevWorkersBinDirectory(string addinDirectory)
        {
            try
            {
                string env = Environment.GetEnvironmentVariable(WorkersSourceEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(env))
                {
                    string expanded = Environment.ExpandEnvironmentVariables(env.Trim());
                    if (Directory.Exists(expanded) &&
                        File.Exists(Path.Combine(expanded, WorkersDllFileName)))
                    {
                        return Path.GetFullPath(expanded);
                    }
                }

                // Prefer the folder from the most recent SpoolingSavantV3Exports.Workers build (written every compile).
                // That avoids a stale or wrong repositoryRoot in SsSavantInstallOptions.txt pointing at the
                // wrong clone or config while Revit locks ProgramData Hotload.
                string lastBuilt = TryReadLastBuiltWorkersBinDirectory();
                if (!string.IsNullOrEmpty(lastBuilt))
                    return lastBuilt;

                SsSavantInstallOptions.LoadFromAddinDirectory(addinDirectory);
                string repo = SsSavantInstallOptions.RepositoryRoot;
                if (!string.IsNullOrWhiteSpace(repo) && Directory.Exists(repo))
                {
                    string debug = Path.Combine(repo, "SpoolingSavantV3Exports.Workers", "bin", "Debug");
                    string release = Path.Combine(repo, "SpoolingSavantV3Exports.Workers", "bin", "Release");
                    string best = null;
                    DateTime bestTime = DateTime.MinValue;

                    foreach (string cand in new[] { debug, release })
                    {
                        if (!Directory.Exists(cand))
                            continue;

                        string dll = Path.Combine(cand, WorkersDllFileName);
                        if (!File.Exists(dll))
                            continue;

                        DateTime t = File.GetLastWriteTimeUtc(dll);
                        if (t >= bestTime)
                        {
                            bestTime = t;
                            best = cand;
                        }
                    }

                    if (!string.IsNullOrEmpty(best))
                        return Path.GetFullPath(best);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string TryReadLastBuiltWorkersBinDirectory()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LastBuiltWorkersBinRelativePath);

                if (!File.Exists(path))
                    return null;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0 || line[0] == '#')
                        continue;

                    if (!Directory.Exists(line))
                        continue;

                    string dll = Path.Combine(line, WorkersDllFileName);
                    if (File.Exists(dll))
                        return Path.GetFullPath(line);
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool TryEnsureShadowCopy(string devBinDirectory, out string shadowDirectory, out string workerAssemblyPath)
        {
            shadowDirectory = null;
            workerAssemblyPath = null;

            try
            {
                string srcDll = Path.Combine(devBinDirectory, WorkersDllFileName);
                if (!File.Exists(srcDll))
                    return false;

                FileInfo fi = new FileInfo(srcDll);
                string versionToken = "0";
                try
                {
                    versionToken = AssemblyName.GetAssemblyName(srcDll).Version.ToString();
                }
                catch
                {
                }

                string stamp = fi.LastWriteTimeUtc.Ticks.ToString() + "_" + fi.Length + "_v" + versionToken;
                shadowDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Spooling-Savant-V3-Exports",
                    "WorkerShadow",
                    stamp);
                workerAssemblyPath = Path.Combine(shadowDirectory, WorkersDllFileName);

                if (File.Exists(workerAssemblyPath))
                {
                    TryRefreshShadowFromSource(devBinDirectory, shadowDirectory, srcDll, workerAssemblyPath);
                    return true;
                }

                if (Directory.Exists(shadowDirectory))
                {
                    try
                    {
                        Directory.Delete(shadowDirectory, recursive: true);
                    }
                    catch
                    {
                        // If delete fails, attempt copy over existing files.
                    }
                }

                Directory.CreateDirectory(shadowDirectory);
                CopyWorkerPayload(devBinDirectory, shadowDirectory);
                return File.Exists(workerAssemblyPath);
            }
            catch
            {
                return false;
            }
        }

        private static void CopyWorkerPayload(string sourceDir, string destDir)
        {
            foreach (string pattern in new[] { "*.dll", "*.pdb" })
            {
                foreach (string file in Directory.GetFiles(sourceDir, pattern))
                {
                    string name = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(destDir, name), overwrite: true);
                }
            }
        }

        private static void TryRefreshShadowFromSource(
            string sourceDir,
            string shadowDir,
            string sourceDllPath,
            string shadowDllPath)
        {
            try
            {
                if (!File.Exists(sourceDllPath) || !File.Exists(shadowDllPath))
                    return;

                bool versionMismatch = false;
                try
                {
                    Version sourceVersion = AssemblyName.GetAssemblyName(sourceDllPath).Version;
                    Version shadowVersion = AssemblyName.GetAssemblyName(shadowDllPath).Version;
                    versionMismatch = sourceVersion != shadowVersion;
                }
                catch
                {
                }

                DateTime sourceUtc = File.GetLastWriteTimeUtc(sourceDllPath);
                DateTime shadowUtc = File.GetLastWriteTimeUtc(shadowDllPath);
                if (!versionMismatch && sourceUtc <= shadowUtc)
                {
                    return;
                }

                CopyWorkerPayload(sourceDir, shadowDir);
            }
            catch
            {
            }
        }
    }
}
