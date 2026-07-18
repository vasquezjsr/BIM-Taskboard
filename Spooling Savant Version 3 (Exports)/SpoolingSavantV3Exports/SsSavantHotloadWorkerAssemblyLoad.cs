using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SpoolingSavantV3Exports
{
    /// <summary>
    /// Loads <c>SpoolingSavantV3Exports.Workers.dll</c> for ribbon hotload. Reuses the in-memory copy when the path is unchanged.
    /// On Revit 2025+ (CoreCLR), reloads into a collectible <see cref="AssemblyLoadContext"/> when a rebuilt
    /// shadow DLL has the same simple name but a different path/version.
    /// </summary>
    internal static class SsSavantHotloadWorkerAssemblyLoad
    {
        private const string WorkersDllFileName = "SpoolingSavantV3Exports.Workers.dll";

        private static Assembly _collectibleWorkersAssembly;
        private static string _collectibleWorkersPath;
        private static long _collectibleWorkersFileTicks;
        private static object _collectibleContext;
        private static string[] _collectibleProbeDirectories = Array.Empty<string>();

        internal static Assembly LoadFromPath(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new ArgumentException("Assembly path is required.", nameof(assemblyPath));

            string fullPath = Path.GetFullPath(assemblyPath);
            if (!File.Exists(fullPath))
                return Assembly.LoadFrom(fullPath);

            if (IsWorkersAssemblyPath(fullPath))
                return LoadWorkersAssembly(fullPath);

            return LoadSatelliteAssembly(fullPath);
        }

        /// <summary>Called by <see cref="SsSavantHotloadAssemblyResolver"/> while a worker command runs.</summary>
        internal static void SetWorkerProbeDirectories(params string[] directories)
        {
            _collectibleProbeDirectories = directories ?? Array.Empty<string>();
        }

        internal static void ClearWorkerProbeDirectories()
        {
            _collectibleProbeDirectories = Array.Empty<string>();
        }

        /// <summary>Clears cached worker loads so the next <see cref="LoadFromPath"/> reads the file on disk again.</summary>
        internal static void InvalidateWorkersCache()
        {
            UnloadCollectibleWorkersContext();
        }

        private static Assembly LoadWorkersAssembly(string fullPath)
        {
            long fileTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
            Version diskVersion = TryReadAssemblyVersion(fullPath);

            if (CanReuseCachedWorkers(fullPath, fileTicks, diskVersion))
            {
                return _collectibleWorkersAssembly;
            }

            UnloadCollectibleWorkersContext();

            Assembly existingAtPath = FindAlreadyLoadedAtPath(fullPath);
            if (existingAtPath != null && AssemblyVersionsMatch(existingAtPath, diskVersion))
            {
                RememberCollectibleWorkers(existingAtPath, fullPath, null, fileTicks);
                return existingAtPath;
            }

            if (TryLoadWorkersInCollectibleContext(fullPath, out Assembly collectible)
                && AssemblyVersionsMatch(collectible, diskVersion))
            {
                return collectible;
            }

            Assembly loadFile = TryLoadWorkersWithLoadFile(fullPath);
            if (loadFile != null && AssemblyVersionsMatch(loadFile, diskVersion))
            {
                RememberCollectibleWorkers(loadFile, fullPath, null, fileTicks);
                return loadFile;
            }

            try
            {
                Assembly loaded = Assembly.LoadFrom(fullPath);
                if (AssemblyVersionsMatch(loaded, diskVersion))
                {
                    RememberCollectibleWorkers(loaded, fullPath, null, fileTicks);
                    return loaded;
                }
            }
            catch (FileLoadException ex) when (IsAlreadyLoadedDuplicateMessage(ex))
            {
            }

            if (TryLoadWorkersInCollectibleContext(fullPath, out collectible)
                && AssemblyVersionsMatch(collectible, diskVersion))
            {
                return collectible;
            }

            loadFile = TryLoadWorkersWithLoadFile(fullPath);
            if (loadFile != null)
            {
                RememberCollectibleWorkers(loadFile, fullPath, null, fileTicks);
                return loadFile;
            }

            return collectible ?? loadFile ?? existingAtPath;
        }

        private static bool CanReuseCachedWorkers(string fullPath, long fileTicks, Version diskVersion)
        {
            if (_collectibleWorkersAssembly == null
                || !string.Equals(_collectibleWorkersPath, fullPath, StringComparison.OrdinalIgnoreCase)
                || _collectibleWorkersFileTicks != fileTicks)
            {
                return false;
            }

            if (!AssemblyVersionsMatch(_collectibleWorkersAssembly, diskVersion))
            {
                return false;
            }

            Version cachedVersion = _collectibleWorkersAssembly.GetName().Version;
            return !IsDiskVersionNewer(diskVersion, cachedVersion);
        }

        /// <summary>
        /// .NET Framework binds <see cref="Assembly.LoadFrom(string)"/> by identity; <see cref="Assembly.LoadFile(string)"/>
        /// loads from a unique shadow path so rebuilt workers can run without restarting Revit.
        /// </summary>
        private static Assembly TryLoadWorkersWithLoadFile(string fullPath)
        {
            try
            {
                return Assembly.LoadFile(fullPath);
            }
            catch
            {
                return null;
            }
        }

        private static Assembly FindWorkersAssemblyBySimpleName()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(
                            asm.GetName().Name,
                            "SpoolingSavantV3Exports.Workers",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return asm;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool IsDiskVersionNewer(Version diskVersion, Version loadedVersion)
        {
            if (diskVersion == null || loadedVersion == null)
            {
                return false;
            }

            return diskVersion > loadedVersion;
        }

        private static Assembly LoadSatelliteAssembly(string fullPath)
        {
            if (_collectibleContext != null
                && TryResolveFromCollectibleContext(Path.GetFileName(fullPath), out Assembly fromContext))
            {
                return fromContext;
            }

            try
            {
                return Assembly.LoadFrom(fullPath);
            }
            catch (FileLoadException ex) when (IsAlreadyLoadedDuplicateMessage(ex))
            {
                Assembly existingAtPath = FindAlreadyLoadedAtPath(fullPath);
                if (existingAtPath != null)
                    return existingAtPath;
                throw;
            }
        }

        private static bool IsWorkersAssemblyPath(string fullPath)
        {
            return string.Equals(
                Path.GetFileName(fullPath),
                WorkersDllFileName,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryLoadWorkersInCollectibleContext(string fullPath, out Assembly assembly)
        {
            assembly = null;
            if (!SsSavantCollectibleAssemblyLoadContext.IsAvailable)
                return false;

            try
            {
                UnloadCollectibleWorkersContext();

                string workerDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                assembly = SsSavantCollectibleAssemblyLoadContext.LoadFromDirectory(
                    fullPath,
                    workerDirectory,
                    _collectibleProbeDirectories,
                    out object context);

                if (assembly != null && AssemblyVersionsMatch(assembly, TryReadAssemblyVersion(fullPath)))
                {
                    RememberCollectibleWorkers(assembly, fullPath, context, File.GetLastWriteTimeUtc(fullPath).Ticks);
                    return true;
                }

                assembly = null;
                return false;
            }
            catch
            {
                assembly = null;
                return false;
            }
        }

        private static void RememberCollectibleWorkers(Assembly assembly, string fullPath, object context, long fileTicks)
        {
            _collectibleWorkersAssembly = assembly;
            _collectibleWorkersPath = fullPath;
            _collectibleWorkersFileTicks = fileTicks;
            _collectibleContext = context;
        }

        private static void UnloadCollectibleWorkersContext()
        {
            if (_collectibleContext == null)
                return;

            try
            {
                SsSavantCollectibleAssemblyLoadContext.TryUnload(_collectibleContext);
            }
            catch
            {
            }

            _collectibleContext = null;
            _collectibleWorkersAssembly = null;
            _collectibleWorkersPath = null;
            _collectibleWorkersFileTicks = 0;
        }

        private static Version TryReadAssemblyVersion(string assemblyPath)
        {
            try
            {
                return AssemblyName.GetAssemblyName(assemblyPath).Version;
            }
            catch
            {
                return null;
            }
        }

        private static bool AssemblyVersionsMatch(Assembly assembly, Version diskVersion)
        {
            if (assembly == null || diskVersion == null)
                return false;

            Version loadedVersion = assembly.GetName().Version;
            return loadedVersion != null && loadedVersion == diskVersion;
        }

        private static bool TryResolveFromCollectibleContext(string fileName, out Assembly assembly)
        {
            assembly = null;
            if (_collectibleContext == null || string.IsNullOrWhiteSpace(fileName))
                return false;

            foreach (string directory in BuildProbeDirectoryList(Path.GetDirectoryName(_collectibleWorkersPath)))
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                string candidate = Path.Combine(directory, fileName);
                if (!File.Exists(candidate))
                    continue;

                assembly = SsSavantCollectibleAssemblyLoadContext.LoadAssemblyFromContext(_collectibleContext, candidate);
                if (assembly != null)
                    return true;
            }

            return false;
        }

        private static string[] BuildProbeDirectoryList(string primaryDirectory)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(primaryDirectory))
                list.Add(primaryDirectory);

            if (_collectibleProbeDirectories != null)
            {
                foreach (string directory in _collectibleProbeDirectories)
                {
                    if (string.IsNullOrWhiteSpace(directory))
                        continue;

                    bool exists = false;
                    foreach (string existing in list)
                    {
                        if (string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                        list.Add(directory);
                }
            }

            return list.ToArray();
        }

        private static bool IsAlreadyLoadedDuplicateMessage(FileLoadException ex)
        {
            for (Exception cur = ex; cur != null; cur = cur.InnerException)
            {
                string m = cur.Message ?? string.Empty;
                if (m.IndexOf("already loaded", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.IndexOf("same name", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static Assembly FindAlreadyLoadedAtPath(string fullProbePath)
        {
            if (string.IsNullOrWhiteSpace(fullProbePath))
                return null;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (LocationsReferToSameFile(asm, fullProbePath))
                        return asm;
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool LocationsReferToSameFile(Assembly asm, string fullProbePath)
        {
            fullProbePath = Path.GetFullPath(fullProbePath);
            try
            {
                string loc = asm.Location;
                if (!string.IsNullOrEmpty(loc)
                    && string.Equals(Path.GetFullPath(loc), fullProbePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                string cb = asm.CodeBase;
                if (string.IsNullOrEmpty(cb))
                    return false;

                if (!Uri.TryCreate(cb, UriKind.Absolute, out Uri uri) || !uri.IsFile)
                    return false;

                string localPath = Uri.UnescapeDataString(uri.LocalPath ?? string.Empty);
                if (string.IsNullOrEmpty(localPath))
                    return false;

                return string.Equals(Path.GetFullPath(localPath), fullProbePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
