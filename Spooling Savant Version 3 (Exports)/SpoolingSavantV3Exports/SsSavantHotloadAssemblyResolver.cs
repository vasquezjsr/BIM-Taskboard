using System;
using System.IO;
using System.Reflection;

namespace SpoolingSavantV3Exports
{
    /// <summary>
    /// Probes worker and add-in directories when loading satellites (e.g. PDF, BouncyCastle) next to
    /// <c>SpoolingSavantV3Exports.Workers.dll</c>. Required for shadow hotload folders under %LocalAppData% as well as ProgramData.
    /// </summary>
    internal sealed class SsSavantHotloadAssemblyResolver : IDisposable
    {
        private readonly string[] _directories;

        internal SsSavantHotloadAssemblyResolver(params string[] directories)
        {
            _directories = directories ?? Array.Empty<string>();
            SsSavantHotloadWorkerAssemblyLoad.SetWorkerProbeDirectories(_directories);
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            string simpleName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(simpleName))
            {
                return null;
            }

            // Prefer an already-loaded copy — LoadFrom of a second OpenXml/ClosedXML identity
            // breaks generics (Elements<Sheet> constraint violations).
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return loaded;
                    }
                }
                catch
                {
                }
            }

            string dllName = simpleName + ".dll";
            foreach (string directory in _directories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                string candidatePath = Path.Combine(directory, dllName);
                if (File.Exists(candidatePath))
                {
                    return SsSavantHotloadWorkerAssemblyLoad.LoadFromPath(candidatePath);
                }
            }

            return null;
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= OnResolve;
            SsSavantHotloadWorkerAssemblyLoad.ClearWorkerProbeDirectories();
        }
    }
}
