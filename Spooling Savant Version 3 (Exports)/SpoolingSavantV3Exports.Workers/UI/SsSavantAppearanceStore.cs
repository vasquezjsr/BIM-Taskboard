using System;
using System.IO;
using System.Xml.Serialization;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.UI
{
    /// <summary>
    /// Global Spooling Savant V3 (Exports) UI theme persisted for all dialogs and panes.
    /// </summary>
    internal static class SsSavantAppearanceStore
    {
        private static readonly object Sync = new object();
        private static UiAppearanceSettings _current;
        private static bool _loaded;

        internal static string SettingsFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Spooling Savant V3 (Exports)",
                "SsSavantAppearanceSettings.xml");

        internal static UiAppearanceSettings Current
        {
            get
            {
                EnsureLoaded();
                return _current.Clone();
            }
        }

        internal static UiAppearanceSettings CurrentMutable
        {
            get
            {
                EnsureLoaded();
                return _current;
            }
        }

        internal static void Save(UiAppearanceSettings settings)
        {
            if (settings == null)
                return;

            lock (Sync)
            {
                _current = settings.Clone();
                _current.ThemeId = UiThemeCatalog.NormalizeThemeId(_current.ThemeId);
                _current.UseRevitGraphics = UiThemeCatalog.UsesRevitColors(_current);
                if (!_current.UseRevitGraphics)
                    _current.ApplyPalette(UiThemeCatalog.ResolvePalette(_current));

                WriteFile(_current);
                _loaded = true;
            }

            SsSavantUiAppearance.NotifySettingsChanged();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            lock (Sync)
            {
                if (_loaded)
                    return;

                _current = TryReadFile() ?? MigrateFromSpoolingManagerSettings() ?? new UiAppearanceSettings();
                _current.ThemeId = UiThemeCatalog.NormalizeThemeId(_current.ThemeId);
                _loaded = true;
            }
        }

        private static UiAppearanceSettings TryReadFile()
        {
            try
            {
                string path = SettingsFilePath;
                if (!File.Exists(path))
                    return null;

                using (var stream = File.OpenRead(path))
                {
                    var serializer = new XmlSerializer(typeof(UiAppearanceSettings));
                    return serializer.Deserialize(stream) as UiAppearanceSettings;
                }
            }
            catch
            {
                return null;
            }
        }

        private static UiAppearanceSettings MigrateFromSpoolingManagerSettings()
        {
            try
            {
                SpoolingManagerSettings spooling = SpoolingManagerSettings.Load(SpoolingManagerKind.Standard);
                if (spooling == null)
                    return null;

                var appearance = new UiAppearanceSettings
                {
                    ThemeId = string.IsNullOrWhiteSpace(spooling.ThemeId)
                        ? UiAppearanceSettings.DefaultThemeId
                        : spooling.ThemeId,
                    UseRevitGraphics = spooling.UseRevitGraphics,
                    ChromeBackground = spooling.UiChromeBackground,
                    ListWellBackground = spooling.UiListWellBackground,
                    ShortcutTileBackground = spooling.UiShortcutTileBackground,
                    InputBackground = spooling.UiInputBackground,
                    ForegroundPrimary = spooling.UiForegroundPrimary,
                    ForegroundMuted = spooling.UiForegroundMuted,
                    BorderOuter = spooling.UiBorderOuter,
                    ListBorder = spooling.UiListBorder
                };

                WriteFile(appearance);
                return appearance;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteFile(UiAppearanceSettings settings)
        {
            try
            {
                string path = SettingsFilePath;
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(folder))
                    Directory.CreateDirectory(folder);

                using (var stream = File.Create(path))
                {
                    var serializer = new XmlSerializer(typeof(UiAppearanceSettings));
                    serializer.Serialize(stream, settings);
                }
            }
            catch
            {
            }
        }
    }
}
