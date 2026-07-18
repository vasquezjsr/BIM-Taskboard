using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SpoolingSavantV3Exports.Workers
{
    /// <summary>
    /// Finds the folder that hosts <c>SpoolingSavantV3Exports.Workers.dll</c>. Ribbon commands load the worker assembly with
    /// <c>Assembly.Load(byte[])</c>, so <see cref="Assembly.Location"/> is empty and must not be passed to
    /// <see cref="Path.GetDirectoryName(string)"/> (throws &quot;The path is not of a legal form.&quot;).
    /// </summary>
    internal static class SsSavantWorkerAssemblyPaths
    {
        internal const string LastBuiltWorkersBinRelativePath = @"Spooling-Savant-V3-Exports\LastBuiltWorkersBin.txt";

        internal static string ResolveWorkersDllDirectory(Assembly workersAssembly)
        {
            if (workersAssembly == null)
                return string.Empty;

            try
            {
                string loc = workersAssembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        return Path.GetFullPath(dir);
                }
            }
            catch (ArgumentException)
            {
            }

            try
            {
                string codeBase = workersAssembly.CodeBase;
                if (!string.IsNullOrEmpty(codeBase) && Uri.TryCreate(codeBase, UriKind.Absolute, out Uri uri))
                {
                    string local = uri.LocalPath;
                    if (!string.IsNullOrEmpty(local))
                    {
                        string dir = Path.GetDirectoryName(local);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            return Path.GetFullPath(dir);
                    }
                }
            }
            catch (ArgumentException)
            {
            }

            string lastBuilt = TryReadLastBuiltWorkersBinDirectory();
            if (!string.IsNullOrEmpty(lastBuilt))
                return lastBuilt;

            string shadow = TryFindNewestWorkerShadowFolder();
            if (!string.IsNullOrEmpty(shadow))
                return shadow;

            return ScanProgramDataForHotloadWorkersFolder();
        }

        internal static string ResolveAddInRootNextToHotload(string workersDllDirectory)
        {
            if (string.IsNullOrWhiteSpace(workersDllDirectory))
                return string.Empty;

            try
            {
                return Path.GetFullPath(Path.Combine(workersDllDirectory, ".."));
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string[] ResolveSatelliteProbeDirectories(Assembly workersAssembly)
        {
            var directories = new List<string>();
            AddDirectory(directories, ResolveWorkersDllDirectory(workersAssembly));
            AddDirectory(directories, ResolveAddInRootNextToHotload(ResolveWorkersDllDirectory(workersAssembly)));
            AddDirectory(directories, TryReadLastBuiltWorkersBinDirectory());
            AddDirectory(directories, TryFindNewestWorkerShadowFolder());
            AddDirectory(directories, ScanProgramDataForHotloadWorkersFolder());
            return directories.ToArray();
        }

        internal static string TryReadLastBuiltWorkersBinDirectory()
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

                    string dll = Path.Combine(line, "SpoolingSavantV3Exports.Workers.dll");
                    if (File.Exists(dll))
                        return Path.GetFullPath(line);
                }
            }
            catch
            {
            }

            return null;
        }

        private static string TryFindNewestWorkerShadowFolder()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Spooling-Savant-V3-Exports",
                    "WorkerShadow");

                if (!Directory.Exists(root))
                    return null;

                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string dll = Path.Combine(dir, "SpoolingSavantV3Exports.Workers.dll");
                    if (!File.Exists(dll))
                        continue;

                    DateTime writeTime = File.GetLastWriteTimeUtc(dll);
                    if (writeTime >= bestTime)
                    {
                        bestTime = writeTime;
                        best = Path.GetFullPath(dir);
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private static string ScanProgramDataForHotloadWorkersFolder()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Autodesk",
                    "Revit",
                    "Addins");

                if (!Directory.Exists(root))
                    return string.Empty;

                foreach (string yearDir in Directory.GetDirectories(root))
                {
                    string hotload = Path.Combine(yearDir, "Spooling-Savant-V3-Exports", "Hotload");
                    string dll = Path.Combine(hotload, "SpoolingSavantV3Exports.Workers.dll");
                    if (File.Exists(dll))
                        return Path.GetFullPath(hotload);
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static void AddDirectory(List<string> directories, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;

            string fullPath = Path.GetFullPath(directory);
            foreach (string existing in directories)
            {
                if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            directories.Add(fullPath);
        }
    }
}
