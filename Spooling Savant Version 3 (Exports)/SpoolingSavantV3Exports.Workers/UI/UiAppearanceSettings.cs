using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SpoolingSavantV3Exports.Workers.UI
{
    internal sealed class UiAppearanceSettings
    {
        internal const string DefaultChromeBackground = "#2B2B2B";
        internal const string DefaultListWellBackground = "#232323";
        internal const string DefaultShortcutTileBackground = "#505050";
        internal const string DefaultInputBackground = "#3A3A3A";
        internal const string DefaultForegroundPrimary = "#FFFFFF";
        internal const string DefaultForegroundMuted = "#D8D8D8";
        internal const string DefaultBorderOuter = "#6A6A6A";
        internal const string DefaultListBorder = "#777777";
        internal const string DefaultThemeId = UiThemeCatalog.MatchRevitThemeId;

        internal string ThemeId { get; set; } = DefaultThemeId;
        internal bool UseRevitGraphics { get; set; } = true;

        internal string ChromeBackground { get; set; } = DefaultChromeBackground;
        internal string ListWellBackground { get; set; } = DefaultListWellBackground;
        internal string ShortcutTileBackground { get; set; } = DefaultShortcutTileBackground;
        internal string InputBackground { get; set; } = DefaultInputBackground;
        internal string ForegroundPrimary { get; set; } = DefaultForegroundPrimary;
        internal string ForegroundMuted { get; set; } = DefaultForegroundMuted;
        internal string BorderOuter { get; set; } = DefaultBorderOuter;
        internal string ListBorder { get; set; } = DefaultListBorder;

        internal PaneColorPalette ToPalette()
        {
            return new PaneColorPalette(
                NormalizeHex(ChromeBackground, DefaultChromeBackground),
                NormalizeHex(ListWellBackground, DefaultListWellBackground),
                NormalizeHex(ShortcutTileBackground, DefaultShortcutTileBackground),
                NormalizeHex(InputBackground, DefaultInputBackground),
                NormalizeHex(ForegroundPrimary, DefaultForegroundPrimary),
                NormalizeHex(ForegroundMuted, DefaultForegroundMuted),
                NormalizeHex(BorderOuter, DefaultBorderOuter),
                NormalizeHex(ListBorder, DefaultListBorder),
                highlightBrush: "#505050",
                pressedBrush: "#383838");
        }

        internal void ApplyPalette(PaneColorPalette palette)
        {
            if (palette == null)
                return;

            ChromeBackground = palette.ChromeBackground;
            ListWellBackground = palette.ListWellBackground;
            ShortcutTileBackground = palette.ShortcutTileBackground;
            InputBackground = palette.InputBackground;
            ForegroundPrimary = palette.ForegroundPrimary;
            ForegroundMuted = palette.ForegroundMuted;
            BorderOuter = palette.BorderOuter;
            ListBorder = palette.ListBorder;
        }

        internal UiAppearanceSettings Clone()
        {
            return new UiAppearanceSettings
            {
                ThemeId = ThemeId,
                UseRevitGraphics = UseRevitGraphics,
                ChromeBackground = ChromeBackground,
                ListWellBackground = ListWellBackground,
                ShortcutTileBackground = ShortcutTileBackground,
                InputBackground = InputBackground,
                ForegroundPrimary = ForegroundPrimary,
                ForegroundMuted = ForegroundMuted,
                BorderOuter = BorderOuter,
                ListBorder = ListBorder
            };
        }

        internal static string NormalizeHex(string value, string fallback)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0)
                return fallback;

            if (!value.StartsWith("#", StringComparison.Ordinal))
                value = "#" + value;

            if (value.Length != 7 && value.Length != 9)
                return fallback;

            if (!value.Skip(1).All(c => Uri.IsHexDigit(c)))
                return fallback;

            return value.ToUpperInvariant();
        }
    }
}

