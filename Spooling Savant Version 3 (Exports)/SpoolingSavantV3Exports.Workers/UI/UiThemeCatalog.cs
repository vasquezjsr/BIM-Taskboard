using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolingSavantV3Exports.Workers.UI
{
    internal sealed class UiThemePreset
    {
        internal UiThemePreset(
            string id,
            string name,
            string description,
            Func<PaneColorPalette> createPalette,
            bool usesRevitColors = false)
        {
            Id = id;
            Name = name;
            Description = description;
            CreatePalette = createPalette;
            UsesRevitColors = usesRevitColors;
        }

        internal string Id { get; }

        internal string Name { get; }

        internal string Description { get; }

        internal Func<PaneColorPalette> CreatePalette { get; }

        internal bool UsesRevitColors { get; }
    }

    internal static class UiThemeCatalog
    {
        internal const string MatchRevitThemeId = "match-revit";
        internal const string WindowsDarkThemeId = "windows-dark";

        private static readonly IReadOnlyList<UiThemePreset> ThemePresets = new List<UiThemePreset>
        {
            new UiThemePreset(
                MatchRevitThemeId,
                "Match Revit",
                "Follows Revit canvas and UI mode.",
                RevitThemePalette.ForCurrentRevitTheme,
                usesRevitColors: true),

            new UiThemePreset(
                "windows-light",
                "Windows Light",
                "Bright, quiet Windows surfaces.",
                () => CreatePalette(
                    "#F3F3F3",
                    "#FFFFFF",
                    "#F9F9F9",
                    "#FFFFFF",
                    "#1F1F1F",
                    "#5E5E5E",
                    "#D6D6D6",
                    "#C8C8C8",
                    "#E8F2FF",
                    "#D7E9FF")),

            new UiThemePreset(
                WindowsDarkThemeId,
                "Windows Dark",
                "Low-glare Windows dark surfaces.",
                () => CreatePalette(
                    "#202020",
                    "#1C1C1C",
                    "#2B2B2B",
                    "#2B2B2B",
                    "#F5F5F5",
                    "#C8C8C8",
                    "#3D3D3D",
                    "#4A4A4A",
                    "#373737",
                    "#303030")),

            new UiThemePreset(
                "graphite",
                "Graphite",
                "Neutral dark with softer contrast.",
                () => CreatePalette(
                    "#2B2D30",
                    "#242629",
                    "#34373B",
                    "#303236",
                    "#F2F4F8",
                    "#BEC5CD",
                    "#4C5158",
                    "#5A6068",
                    "#3B4047",
                    "#32363C")),

            new UiThemePreset(
                "windows-blue",
                "Windows Blue",
                "Clean pale blue with crisp edges.",
                () => CreatePalette(
                    "#EEF4FB",
                    "#FFFFFF",
                    "#F7FAFD",
                    "#FFFFFF",
                    "#172033",
                    "#5B6678",
                    "#CAD7E5",
                    "#B8C9DA",
                    "#DDEBFA",
                    "#CBDFF4")),

            new UiThemePreset(
                "sage",
                "Sage",
                "Soft green-grey Windows calm.",
                () => CreatePalette(
                    "#F1F5F0",
                    "#FFFFFF",
                    "#F8FAF7",
                    "#FFFFFF",
                    "#1F2A24",
                    "#5F6B63",
                    "#CBD8CD",
                    "#BBCABF",
                    "#E2EDE3",
                    "#D4E3D6"))
        };

        internal static IReadOnlyList<UiThemePreset> Presets => ThemePresets;

        internal static UiThemePreset GetPreset(string themeId)
        {
            if (string.IsNullOrWhiteSpace(themeId))
                return null;

            return ThemePresets.FirstOrDefault(
                preset => string.Equals(preset.Id, themeId, StringComparison.OrdinalIgnoreCase));
        }

        internal static string NormalizeThemeId(string themeId)
        {
            return GetPreset(themeId)?.Id ?? MatchRevitThemeId;
        }

        internal static PaneColorPalette ResolvePalette(UiAppearanceSettings settings)
        {
            if (settings == null)
                return RevitThemePalette.ForCurrentRevitTheme();

            UiThemePreset preset = GetPreset(settings.ThemeId);
            if (preset != null)
                return preset.CreatePalette();

            return settings.UseRevitGraphics
                ? RevitThemePalette.ForCurrentRevitTheme()
                : settings.ToPalette();
        }

        internal static bool UsesRevitColors(UiAppearanceSettings settings)
        {
            if (settings == null)
                return true;

            UiThemePreset preset = GetPreset(settings.ThemeId);
            return preset != null ? preset.UsesRevitColors : settings.UseRevitGraphics;
        }

        internal static bool UsesRevitColors(string themeId)
        {
            return GetPreset(themeId)?.UsesRevitColors == true;
        }

        private static PaneColorPalette CreatePalette(
            string chromeBackground,
            string listWellBackground,
            string shortcutTileBackground,
            string inputBackground,
            string foregroundPrimary,
            string foregroundMuted,
            string borderOuter,
            string listBorder,
            string highlightBrush,
            string pressedBrush)
        {
            return new PaneColorPalette(
                chromeBackground,
                listWellBackground,
                shortcutTileBackground,
                inputBackground,
                foregroundPrimary,
                foregroundMuted,
                borderOuter,
                listBorder,
                highlightBrush,
                pressedBrush);
        }
    }
}

