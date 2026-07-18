using System;
using System.Windows;

namespace SpoolingSavantV3Exports.Workers.UI
{
    internal static class SsSavantUiAppearance
    {
        internal static event Action SettingsChanged;

        internal static void NotifySettingsChanged()
        {
            SettingsChanged?.Invoke();
        }

        internal static void ApplyToElement(FrameworkElement target)
        {
            if (target == null)
                return;

            UiAppearanceSettings appearance = SsSavantAppearanceStore.Current;
            PaneColorPalette palette = UiThemeCatalog.ResolvePalette(appearance);
            PaneThemeApplier.Apply(target, palette);
        }
    }
}
