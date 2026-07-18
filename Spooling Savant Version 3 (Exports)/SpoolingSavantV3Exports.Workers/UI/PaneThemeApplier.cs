using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpoolingSavantV3Exports.Workers.UI
{
    internal static class PaneThemeApplier
    {
        internal static void Apply(FrameworkElement target, PaneColorPalette palette)
        {
            if (target == null || palette == null)
                return;

            palette = EnsureReadableContrast(palette);
            SetBrush(target.Resources, "SsSavantChromeBackground", palette.ChromeBackground);
            SetBrush(target.Resources, "VgOnDemandChromeBackground", palette.ChromeBackground);
            SetBrush(target.Resources, "SsSavantListWellBackground", palette.ListWellBackground);
            SetBrush(target.Resources, "SsSavantShortcutTileBackground", palette.ShortcutTileBackground);
            SetBrush(target.Resources, "SsSavantInputBackground", palette.InputBackground);
            SetBrush(target.Resources, "SsSavantForegroundPrimary", palette.ForegroundPrimary);
            SetBrush(target.Resources, "SsSavantForegroundMuted", palette.ForegroundMuted);
            SetBrush(target.Resources, "SsSavantBorderOuter", palette.BorderOuter);
            SetBrush(target.Resources, "SsSavantListBorder", palette.ListBorder);
            SetBrush(target.Resources, "SsSavantHighlightBrush", palette.HighlightBrush);
            SetBrush(target.Resources, "SsSavantPressedBrush", palette.PressedBrush);
            SetBrush(target.Resources, "SsSavantForegroundTitle", palette.ForegroundPrimary);

            ApplySystemColors(target.Resources, palette);
            ClearThemedControlOverrides(target);

            if (target is Control control)
            {
                control.Background = GetBrush(target.Resources, "SsSavantChromeBackground");
                control.Foreground = GetBrush(target.Resources, "SsSavantForegroundPrimary");
            }
        }

        internal static void ClearThemedControlOverrides(DependencyObject root)
        {
            if (root == null)
                return;

            if (root is ComboBox || root is TextBox || root is PasswordBox)
            {
                var control = (Control)root;
                control.ClearValue(Control.BackgroundProperty);
                control.ClearValue(Control.ForegroundProperty);
                control.ClearValue(Control.BorderBrushProperty);
                control.ClearValue(Control.BorderThicknessProperty);
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                ClearThemedControlOverrides(VisualTreeHelper.GetChild(root, i));
            }
        }

        internal static PaneColorPalette EnsureReadableContrast(PaneColorPalette palette)
        {
            if (palette == null)
                return null;

            string foreground = palette.ForegroundPrimary;
            string muted = palette.ForegroundMuted;
            string inputBackground = palette.InputBackground;

            if (!HasSufficientContrast(foreground, palette.ListWellBackground))
                foreground = PickForegroundForBackground(palette.ListWellBackground);
            if (!HasSufficientContrast(muted, palette.ListWellBackground))
                muted = PickMutedForBackground(palette.ListWellBackground, foreground);

            if (!HasSufficientContrast(foreground, inputBackground))
                foreground = PickForegroundForBackground(inputBackground);
            if (!HasSufficientContrast(muted, inputBackground))
                muted = PickMutedForBackground(inputBackground, foreground);

            if (!HasSufficientContrast(foreground, palette.ChromeBackground))
                foreground = PickForegroundForBackground(palette.ChromeBackground);

            if (string.Equals(foreground, palette.ForegroundPrimary, StringComparison.OrdinalIgnoreCase)
                && string.Equals(muted, palette.ForegroundMuted, StringComparison.OrdinalIgnoreCase))
            {
                return palette;
            }

            return new PaneColorPalette(
                palette.ChromeBackground,
                palette.ListWellBackground,
                palette.ShortcutTileBackground,
                inputBackground,
                foreground,
                muted,
                palette.BorderOuter,
                palette.ListBorder,
                palette.HighlightBrush,
                palette.PressedBrush);
        }

        private static string PickForegroundForBackground(string backgroundHex)
        {
            if (!TryParseRgb(backgroundHex, out int r, out int g, out int b))
                return "#1E1E1E";

            return RelativeLuminance(r, g, b) < 0.45 ? "#FFFFFF" : "#1E1E1E";
        }

        private static string PickMutedForBackground(string backgroundHex, string primaryForegroundHex)
        {
            if (!TryParseRgb(backgroundHex, out int r, out int g, out int b))
                return "#5A5A5A";

            return RelativeLuminance(r, g, b) < 0.45 ? "#C8C8C8" : "#5A5A5A";
        }

        private static bool HasSufficientContrast(string foregroundHex, string backgroundHex)
        {
            if (!TryParseRgb(foregroundHex, out int fr, out int fg, out int fb)
                || !TryParseRgb(backgroundHex, out int br, out int bg, out int bb))
            {
                return true;
            }

            double fgLum = RelativeLuminance(fr, fg, fb);
            double bgLum = RelativeLuminance(br, bg, bb);
            double lighter = Math.Max(fgLum, bgLum);
            double darker = Math.Min(fgLum, bgLum);
            double ratio = (lighter + 0.05) / (darker + 0.05);
            return ratio >= 4.0;
        }

        private static double RelativeLuminance(int r, int g, int b)
        {
            double rs = r / 255.0;
            double gs = g / 255.0;
            double bs = b / 255.0;
            rs = rs <= 0.03928 ? rs / 12.92 : Math.Pow((rs + 0.055) / 1.055, 2.4);
            gs = gs <= 0.03928 ? gs / 12.92 : Math.Pow((gs + 0.055) / 1.055, 2.4);
            bs = bs <= 0.03928 ? bs / 12.92 : Math.Pow((bs + 0.055) / 1.055, 2.4);
            return 0.2126 * rs + 0.7152 * gs + 0.0722 * bs;
        }

        private static bool TryParseRgb(string hex, out int r, out int g, out int b)
        {
            r = g = b = 0;
            hex = (hex ?? string.Empty).Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal))
                hex = hex.Substring(1);

            if (hex.Length != 6 && hex.Length != 8)
                return false;

            if (hex.Length == 8)
                hex = hex.Substring(2);

            return int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
                && int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
                && int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
        }

        private static void ApplySystemColors(ResourceDictionary resources, PaneColorPalette palette)
        {
            SetBrush(resources, SystemColors.WindowBrushKey, palette.InputBackground);
            SetBrush(resources, SystemColors.WindowTextBrushKey, palette.ForegroundPrimary);
            SetBrush(resources, SystemColors.ControlBrushKey, palette.InputBackground);
            SetBrush(resources, SystemColors.ControlTextBrushKey, palette.ForegroundPrimary);
            SetBrush(resources, SystemColors.HighlightBrushKey, palette.HighlightBrush);
            SetBrush(resources, SystemColors.HighlightTextBrushKey, palette.ForegroundPrimary);
        }

        private static Brush GetBrush(ResourceDictionary resources, string key)
        {
            return resources[key] as Brush;
        }

        private static void SetBrush(ResourceDictionary resources, object key, string hex)
        {
            if (resources == null || key == null || string.IsNullOrWhiteSpace(hex))
                return;

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            if (brush.CanFreeze)
                brush.Freeze();

            resources[key] = brush;
        }
    }
}

