using System;
using System.IO;
using System.Reflection;

namespace SpoolingSavantV3Exports.Workers
{
    /// <summary>On-disk shared parameter file for Spooling Savant parameters bound at document open.</summary>
    public static class SsSavantSharedParameterPaths
    {
        public static string BomSharedParameterFile
        {
            get
            {
                try
                {
                    string workerDir = SsSavantWorkerAssemblyPaths.ResolveWorkersDllDirectory(
                        typeof(SsSavantSharedParameterPaths).Assembly);
                    string addinRoot = SsSavantWorkerAssemblyPaths.ResolveAddInRootNextToHotload(workerDir);
                    if (!string.IsNullOrWhiteSpace(addinRoot))
                    {
                        string deployed = Path.Combine(addinRoot, "Parameters", "Spooling-Savant-V3-Exports_SharedParameters.txt");
                        if (File.Exists(deployed))
                            return deployed;

                        string deployedParent = Path.Combine(addinRoot, "..", "Parameters", "Spooling-Savant-V3-Exports_SharedParameters.txt");
                        deployedParent = Path.GetFullPath(deployedParent);
                        if (File.Exists(deployedParent))
                            return deployedParent;
                    }
                }
                catch
                {
                }

                return Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
                    "..",
                    "Parameters",
                    "Spooling-Savant-V3-Exports_SharedParameters.txt");
            }
        }
    }
}
