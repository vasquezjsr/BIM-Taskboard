using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;

namespace SpoolingSavantV3Exports.Workers.UI
{
    /// <summary>
    /// SS Manager / Find and Filter chrome for dockable panes.
    /// Windows are routed to <see cref="SsSavantDialogChrome"/> (Revit Options style).
    /// </summary>
    internal static class SsSavantChrome
    {
        private const string ChromeResourceLogicalName = "Themes.SsSavantChromeResources.xaml";

        internal static void MergeInto(FrameworkElement target)
        {
            if (target == null)
            {
                return;
            }

            if (target is Window)
            {
                SsSavantDialogChrome.MergeInto(target);
                return;
            }

            ResourceDictionary dictionary = LoadChromeDictionary();
            if (dictionary != null)
            {
                target.Resources.MergedDictionaries.Add(dictionary);
            }

            SsSavantUiAppearance.ApplyToElement(target);
        }

        private static ResourceDictionary LoadChromeDictionary()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(ChromeResourceLogicalName))
            {
                if (stream == null)
                {
                    return null;
                }

                using (StreamReader reader = new StreamReader(stream))
                {
                    string xaml = reader.ReadToEnd();
                    xaml = Regex.Replace(xaml, "\\s+x:Class=\"[^\"]+\"", string.Empty);
                    return (ResourceDictionary)XamlReader.Parse(xaml);
                }
            }
        }
    }
}
