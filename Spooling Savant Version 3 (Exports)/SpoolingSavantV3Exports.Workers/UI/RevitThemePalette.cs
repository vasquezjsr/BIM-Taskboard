using System;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SpoolingSavantV3Exports.Workers.UI
{
    /// <summary>
    /// Builds pane colors from Revit's Options &gt; Colors canvas settings
    /// (Background, Selection, etc.), matching the Light and Dark schemes.
    /// </summary>
    internal static class RevitThemePalette
    {
        internal static PaneColorPalette ForCurrentRevitTheme()
        {
            try
            {
                ColorOptions options = ColorOptions.GetColorOptions();
                UITheme uiTheme = UIThemeManager.CurrentTheme;
                return FromColorOptions(options, uiTheme);
            }
            catch
            {
                try
                {
                    return ForTheme(UIThemeManager.CurrentTheme);
                }
                catch
                {
                    return ForTheme(UITheme.Dark);
                }
            }
        }

        internal static PaneColorPalette ForTheme(UITheme theme)
        {
            return theme == UITheme.Light ? CreateLightDefaults() : CreateDarkDefaults();
        }

        private static PaneColorPalette FromColorOptions(ColorOptions options, UITheme uiTheme)
        {
            string canvasBackground = ToHex(options?.BackgroundColor) ?? GetDefaultBackground(uiTheme);
            bool isDark = IsDarkBackground(canvasBackground, uiTheme);

            // Revit's light UI frame is grey; canvas background stays white in Options > Colors.
            string chrome = isDark ? canvasBackground : "#F0F0F0";
            string listWell = isDark ? canvasBackground : canvasBackground;
            string input = isDark
                ? RevitColorMath.Lighten(canvasBackground, 10)
                : "#ECECEC";
            string tile = input;
            string foreground = isDark ? "#FFFFFF" : "#1E1E1E";
            string muted = isDark
                ? RevitColorMath.Blend(canvasBackground, foreground, 0.72)
                : "#5A5A5A";
            string border = isDark
                ? RevitColorMath.Lighten(canvasBackground, 28)
                : "#C8C8C8";
            string listBorder = isDark
                ? RevitColorMath.Lighten(canvasBackground, 34)
                : "#B0B0B0";
            string highlight = isDark
                ? RevitColorMath.Lighten(input, 12)
                : "#E4E4E4";
            string pressed = isDark
                ? RevitColorMath.Darken(input, 8)
                : "#D0D0D0";

            return new PaneColorPalette(
                chrome,
                listWell,
                tile,
                input,
                foreground,
                muted,
                border,
                listBorder,
                highlight,
                pressed);
        }

        private static PaneColorPalette CreateLightDefaults()
        {
            // Options > Colors > Canvas color scheme: Light
            return new PaneColorPalette(
                chromeBackground: "#F0F0F0",
                listWellBackground: "#FFFFFF",
                shortcutTileBackground: "#ECECEC",
                inputBackground: "#ECECEC",
                foregroundPrimary: "#1E1E1E",
                foregroundMuted: "#5A5A5A",
                borderOuter: "#C8C8C8",
                listBorder: "#B0B0B0",
                highlightBrush: "#E4E4E4",
                pressedBrush: "#D0D0D0");
        }

        private static PaneColorPalette CreateDarkDefaults()
        {
            // Options > Colors > Canvas color scheme: Dark (Background RGB 034-041-051)
            const string background = "#222933";
            return new PaneColorPalette(
                chromeBackground: background,
                listWellBackground: background,
                shortcutTileBackground: "#2A313B",
                inputBackground: "#2A313B",
                foregroundPrimary: "#FFFFFF",
                foregroundMuted: "#B8BEC6",
                borderOuter: "#454C56",
                listBorder: "#505869",
                highlightBrush: "#3A414E",
                pressedBrush: "#323844");
        }

        private static string GetDefaultBackground(UITheme theme)
        {
            return theme == UITheme.Light ? "#FFFFFF" : "#222933";
        }

        private static bool IsDarkBackground(string backgroundHex, UITheme uiTheme)
        {
            if (!RevitColorMath.TryParseHex(backgroundHex, out int r, out int g, out int b))
                return uiTheme != UITheme.Light;

            double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
            return luminance < 0.5;
        }

        private static string ToHex(Color color)
        {
            if (color == null)
                return null;

            return string.Format(
                CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}",
                color.Red,
                color.Green,
                color.Blue);
        }
    }

    internal static class RevitColorMath
    {
        internal static string Lighten(string hex, int amount)
        {
            if (!TryParseHex(hex, out int r, out int g, out int b))
                return hex;

            return FormatHex(
                Clamp(r + amount, 0, 255),
                Clamp(g + amount, 0, 255),
                Clamp(b + amount, 0, 255));
        }

        internal static string Darken(string hex, int amount)
        {
            return Lighten(hex, -amount);
        }

        internal static string Blend(string fromHex, string toHex, double toWeight)
        {
            if (!TryParseHex(fromHex, out int r1, out int g1, out int b1))
                return toHex;
            if (!TryParseHex(toHex, out int r2, out int g2, out int b2))
                return fromHex;

            double fromWeight = 1.0 - toWeight;
            return FormatHex(
                Clamp((int)Math.Round(r1 * fromWeight + r2 * toWeight), 0, 255),
                Clamp((int)Math.Round(g1 * fromWeight + g2 * toWeight), 0, 255),
                Clamp((int)Math.Round(b1 * fromWeight + b2 * toWeight), 0, 255));
        }

        internal static bool TryParseHex(string hex, out int r, out int g, out int b)
        {
            r = g = b = 0;
            hex = (hex ?? string.Empty).Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal))
                hex = hex.Substring(1);

            if (hex.Length != 6)
                return false;

            if (!int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r))
                return false;
            if (!int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g))
                return false;
            if (!int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                return false;

            return true;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static string FormatHex(int r, int g, int b)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", r, g, b);
        }
    }
}

