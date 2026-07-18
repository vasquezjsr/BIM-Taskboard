using System;
using System.IO;
using System.Reflection;

namespace SpoolingSavantV3Exports.Workers
{
    /// <summary>
    /// Resolves PDF/Excel satellites for <see cref="Autodesk.Revit.UI.ExternalEvent"/> handlers that run outside
    /// the ribbon <see cref="SpoolingSavantV3Exports.SsSavantHotloadAssemblyResolver"/> scope.
    /// </summary>
    internal sealed class SsSavantHotloadDependencyScope : IDisposable
    {
        private readonly ResolveEventHandler _handler;
        private readonly string[] _directories;

        private SsSavantHotloadDependencyScope(string[] directories)
        {
            _directories = directories ?? Array.Empty<string>();
            _handler = OnResolve;
            AppDomain.CurrentDomain.AssemblyResolve += _handler;
            PreloadSatellites();
        }

        internal static SsSavantHotloadDependencyScope ForWorkerAssembly()
        {
            Assembly asm = typeof(SsSavantHotloadDependencyScope).Assembly;
            return new SsSavantHotloadDependencyScope(SsSavantWorkerAssemblyPaths.ResolveSatelliteProbeDirectories(asm));
        }

        private void PreloadSatellites()
        {
            foreach (string dll in new[]
                     {
                         "System.Runtime.CompilerServices.Unsafe.dll",
                         "System.Buffers.dll",
                         "System.Numerics.Vectors.dll",
                         "System.Memory.dll",
                         "Microsoft.Bcl.HashCode.dll",
                         "BouncyCastle.Cryptography.dll",
                         "itextsharp.dll",
                         "System.IO.Packaging.dll",
                         "ClosedXML.Parser.dll",
                         "DocumentFormat.OpenXml.Framework.dll",
                         "DocumentFormat.OpenXml.dll",
                         "ExcelNumberFormat.dll",
                         "RBush.dll",
                         "SixLabors.Fonts.dll",
                         "ClosedXML.dll"
                     })
            {
                if (TryGetLoadedAssembly(Path.GetFileNameWithoutExtension(dll)) != null)
                    continue;

                foreach (string dir in _directories)
                {
                    if (string.IsNullOrWhiteSpace(dir))
                        continue;

                    string path = Path.Combine(dir, dll);
                    if (!File.Exists(path))
                        continue;

                    if (TryLoadSatelliteAssembly(path) != null)
                        break;
                }
            }
        }

        private Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                AssemblyName requested = new AssemblyName(args.Name);
                string simpleName = requested.Name;
                if (string.IsNullOrEmpty(simpleName))
                    return null;

                Assembly loaded = TryGetLoadedAssembly(simpleName);
                if (loaded != null)
                    return loaded;

                string fileName = simpleName + ".dll";
                foreach (string dir in _directories)
                {
                    if (string.IsNullOrWhiteSpace(dir))
                        continue;

                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                        return TryLoadSatelliteAssembly(candidate);
                }
            }
            catch
            {
            }

            return null;
        }

        private static Assembly TryGetLoadedAssembly(string simpleName)
        {
            if (string.IsNullOrWhiteSpace(simpleName))
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

            return null;
        }

        /// <summary>
        /// Load satellites without a second LoadFrom bind (avoids HRESULT 0x80131040 when Revit already loaded the DLL).
        /// </summary>
        private static Assembly TryLoadSatelliteAssembly(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return null;

            string simpleName = Path.GetFileNameWithoutExtension(fullPath);
            Assembly existing = TryGetLoadedAssembly(simpleName);
            if (existing != null)
                return existing;

            fullPath = Path.GetFullPath(fullPath);
            try
            {
                return Assembly.Load(File.ReadAllBytes(fullPath));
            }
            catch (FileLoadException)
            {
                existing = TryGetLoadedAssembly(simpleName);
                if (existing != null)
                    return existing;
            }
            catch (BadImageFormatException)
            {
            }
            catch
            {
            }

            try
            {
                return Assembly.LoadFile(fullPath);
            }
            catch
            {
                return TryGetLoadedAssembly(simpleName);
            }
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= _handler;
        }
    }
}
