using System;
using System.IO;

namespace SpoolingSavantV3Exports
{
    internal enum SsSavantInstallRole
    {
        User = 0,
        Owner = 1
    }

    /// <summary>
    /// Optional file <c>SsSavantInstallOptions.txt</c> beside <c>SpoolingSavantV3Exports.dll</c> controls install behavior.
    /// </summary>
    internal static class SsSavantInstallOptions
    {
        internal const string FileName = "SsSavantInstallOptions.txt";

        /// <summary>
        /// Default release catalog (ExpandEnvironmentVariables applied). Owner publishes here; all clients read updates from here.
        /// </summary>
        internal const string DefaultReleaseCatalogRoot =
            "%USERPROFILE%\\AsBuilt MEP LLC\\AsBuiltMEP - Documents\\10_ABMEP\\06 - Revit Addin Downloads\\Spooling Savant 3.0";

        private static bool _initialized;
        private static string _releaseCatalogRoot = string.Empty;
        private static string _releaseMsiSourceFolder = string.Empty;
        private static string _repositoryRoot = string.Empty;
        private static SsSavantInstallRole _role = SsSavantInstallRole.User;

        /// <summary>
        /// Folder that contains published version subfolders named like <c>Spooling Savant 3.0 1.0.0.0</c> (after expansion).
        /// </summary>
        internal static string ReleaseCatalogRoot => _releaseCatalogRoot ?? string.Empty;

        /// <summary>
        /// Optional folder that already contains a built <c>Spooling Savant 3.0-Revit-{version}.msi</c> when publishing from Revit (after expansion).
        /// </summary>
        internal static string ReleaseMsiSourceFolder => _releaseMsiSourceFolder ?? string.Empty;

        /// <summary>
        /// Root of the Spooling Savant 3.0 repo / solution (folder that contains <c>Installer\SpoolingSavantV3Exports.Msi</c>). Publish runs <c>dotnet build</c> on the WiX project here.
        /// When set (in <c>SsSavantInstallOptions.txt</c> as <c>repositoryRoot</c>), ribbon hotload can resolve
        /// <c>SpoolingSavantV3Exports.Workers\bin\Debug</c> or <c>Release</c> for shadow copy under %LocalAppData%\Spooling-Savant-V3-Exports\WorkerShadow
        /// so worker rebuilds apply without replacing DLLs locked under ProgramData while Revit runs.
        /// The folder written by the most recent local <c>SpoolingSavantV3Exports.Workers</c> build (<c>%LocalAppData%\Spooling-Savant-V3-Exports\LastBuiltWorkersBin.txt</c>)
        /// is always preferred when present, so shadow hotload does not depend on a correct <c>repositoryRoot</c>.
        /// If unset, the same shadow path is still used when <c>LastBuiltWorkersBin.txt</c> exists (written automatically by local <c>SpoolingSavantV3Exports.Workers</c> builds).
        /// After rebuilding <c>SpoolingSavantV3Exports.Workers</c>, click the SS Manager ribbon button once: <c>TryEnsureSsManagerPane</c> loads the newest shadow DLL into the dockable pane — no Revit restart.
        /// </summary>
        internal static string RepositoryRoot => _repositoryRoot ?? string.Empty;

        /// <summary>
        /// True when <c>ssSavantRole=owner</c> is set in SsSavantInstallOptions.txt (IT break-glass). Does not include key-based unlock.
        /// </summary>
        internal static bool IsDeclaredOwnerInOptionsFile => _role == SsSavantInstallRole.Owner;

        /// <summary>
        /// Owner via options file or successful owner key sign-in for this Windows user.
        /// </summary>
        internal static bool IsOwner => _role == SsSavantInstallRole.Owner;

        internal static SsSavantInstallRole Role => _role;

        internal static void LoadFromAddinDirectory(string addinDirectory)
        {
            if (_initialized)
                return;

            _initialized = true;
            _releaseCatalogRoot = string.Empty;
            _releaseMsiSourceFolder = string.Empty;
            _repositoryRoot = string.Empty;
            _role = SsSavantInstallRole.User;

            if (!string.IsNullOrWhiteSpace(addinDirectory))
            {
                string path = Path.Combine(addinDirectory, FileName);
                if (File.Exists(path))
                    ParseOptionsFile(path);
            }

            if (string.IsNullOrWhiteSpace(_releaseCatalogRoot))
                _releaseCatalogRoot = ExpandPath(DefaultReleaseCatalogRoot);
        }

        private static void ParseOptionsFile(string path)
        {
            try
            {
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = (rawLine ?? string.Empty).Trim();
                    if (line.Length == 0 || line[0] == '#')
                        continue;

                    int eq = line.IndexOf('=');
                    string key = (eq >= 0 ? line.Substring(0, eq) : line).Trim();
                    string value = eq >= 0 ? line.Substring(eq + 1).Trim() : string.Empty;

                    if (key.Equals("releaseCatalogRoot", StringComparison.OrdinalIgnoreCase))
                        _releaseCatalogRoot = ExpandPath(value);
                    else if (key.Equals("releaseMsiSourceFolder", StringComparison.OrdinalIgnoreCase))
                        _releaseMsiSourceFolder = ExpandPath(value);
                    else if (key.Equals("ssSavantRepositoryRoot", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("repositoryRoot", StringComparison.OrdinalIgnoreCase))
                        _repositoryRoot = ExpandPath(value);
                    else if (key.Equals("ssSavantRole", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("installRole", StringComparison.OrdinalIgnoreCase))
                        _role = ParseRole(value);
                }
            }
            catch
            {
                // defaults
            }
        }

        private static SsSavantInstallRole ParseRole(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return SsSavantInstallRole.User;

            if (value.Equals("owner", StringComparison.OrdinalIgnoreCase))
                return SsSavantInstallRole.Owner;

            return SsSavantInstallRole.User;
        }

        private static string ExpandPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            try
            {
                return Environment.ExpandEnvironmentVariables(value.Trim());
            }
            catch
            {
                return value.Trim();
            }
        }
    }
}
