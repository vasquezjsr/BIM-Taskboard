using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SpoolingSavantV3Exports
{
    /// <summary>
    /// Reflection wrapper around <see cref="AssemblyLoadContext"/> so SpoolingSavantV3Exports (net48) can reload
    /// <c>SpoolingSavantV3Exports.Workers.dll</c> on Revit 2025+ without restarting.
    /// </summary>
    internal static class SsSavantCollectibleAssemblyLoadContext
    {
        private static readonly Type ContextType =
            Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader", throwOnError: false);

        private static string[] _probeDirectories = Array.Empty<string>();

        internal static bool IsAvailable => ContextType != null;

        internal static Assembly LoadFromDirectory(
            string workerAssemblyPath,
            string workerDirectory,
            string[] extraProbeDirectories,
            out object context)
        {
            context = null;
            if (!IsAvailable || string.IsNullOrWhiteSpace(workerAssemblyPath))
                return null;

            _probeDirectories = BuildProbeList(workerDirectory, extraProbeDirectories);

            string name = "SpoolingSavantV3Exports.Workers." + Guid.NewGuid().ToString("N");
            context = Activator.CreateInstance(ContextType, name, true);
            HookResolving(context);

            MethodInfo loadFromPath = ContextType.GetMethod(
                "LoadFromAssemblyPath",
                new[] { typeof(string) });
            if (loadFromPath == null)
                return null;

            return (Assembly)loadFromPath.Invoke(context, new object[] { workerAssemblyPath });
        }

        internal static Assembly LoadAssemblyFromContext(object context, string assemblyPath)
        {
            if (context == null || ContextType == null || string.IsNullOrWhiteSpace(assemblyPath))
                return null;

            MethodInfo loadFromPath = ContextType.GetMethod(
                "LoadFromAssemblyPath",
                new[] { typeof(string) });
            return loadFromPath == null
                ? null
                : (Assembly)loadFromPath.Invoke(context, new object[] { assemblyPath });
        }

        internal static void TryUnload(object context)
        {
            if (context == null || ContextType == null)
                return;

            MethodInfo unload = ContextType.GetMethod("Unload", Type.EmptyTypes);
            unload?.Invoke(context, null);
            _probeDirectories = Array.Empty<string>();
        }

        private static void HookResolving(object context)
        {
            EventInfo resolving = ContextType.GetEvent("Resolving");
            if (resolving == null)
                return;

            MethodInfo handlerMethod = typeof(SsSavantCollectibleAssemblyLoadContext).GetMethod(
                nameof(ResolveAssembly),
                BindingFlags.Static | BindingFlags.NonPublic);
            Delegate handler = Delegate.CreateDelegate(resolving.EventHandlerType, handlerMethod);
            resolving.AddEventHandler(context, handler);
        }

        private static Assembly ResolveAssembly(object alc, AssemblyName assemblyName)
        {
            if (assemblyName == null)
                return null;

            string fileName = assemblyName.Name + ".dll";
            foreach (string directory in _probeDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                string candidate = Path.Combine(directory, fileName);
                if (!File.Exists(candidate))
                    continue;

                return LoadAssemblyFromContext(alc, candidate);
            }

            return null;
        }

        private static string[] BuildProbeList(string workerDirectory, string[] extraProbeDirectories)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(workerDirectory))
                list.Add(workerDirectory);

            if (extraProbeDirectories != null)
            {
                foreach (string directory in extraProbeDirectories)
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
    }
}
