using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace SpoolingSavantV3Exports.Workers.UI
{
    /// <summary>
    /// Revit Options–style chrome for modal dialogs and tool windows.
    /// Always follows Revit's current UI theme (light/dark), not custom pane themes.
    /// </summary>
    internal static class SsSavantDialogChrome
    {
        private const string DialogResourceLogicalName = "Themes.SsSavantDialogResources.xaml";

        internal static void MergeInto(FrameworkElement target)
        {
            if (target == null)
            {
                return;
            }

            ResourceDictionary dictionary = LoadDialogDictionary();
            if (dictionary != null)
            {
                target.Resources.MergedDictionaries.Add(dictionary);
            }

            PaneThemeApplier.Apply(target, RevitThemePalette.ForCurrentRevitTheme());
            PaneThemeApplier.ClearThemedControlOverrides(target);

            if (target is Window window)
            {
                window.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
                window.FontSize = 12;
                window.ClearValue(Window.BackgroundProperty);
                window.ClearValue(Window.ForegroundProperty);
            }
        }

        internal static void ApplyThemedBorder(Border border, Thickness? padding = null)
        {
            if (border == null)
            {
                return;
            }

            border.SetResourceReference(Border.BackgroundProperty, "SsSavantChromeBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "SsSavantBorderOuter");
            border.BorderThickness = new Thickness(0);
            if (padding.HasValue)
            {
                border.Padding = padding.Value;
            }
        }

        internal static void ApplyThemedPanel(Panel panel, Thickness margin)
        {
            if (panel == null)
            {
                return;
            }

            panel.Margin = margin;
        }

        private static ResourceDictionary LoadDialogDictionary()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(DialogResourceLogicalName))
            {
                if (stream == null)
                {
                    return null;
                }

                using (var reader = new StreamReader(stream))
                {
                    string xaml = reader.ReadToEnd();
                    xaml = Regex.Replace(xaml, "\\s+x:Class=\"[^\"]+\"", string.Empty);
                    return (ResourceDictionary)XamlReader.Parse(xaml);
                }
            }
        }
    }
}
