using System;
using Autodesk.Windows;

namespace SpoolingSavantV3Exports
{
    /// <summary>
    /// Uses AdWindows to hide keyboard-only ribbon items that Revit refuses to remove via the public API.
    /// </summary>
    internal static class SsSavantRibbonVisibility
    {
        internal static void HideRibbonPanel(string tabName, string panelName)
        {
            if (string.IsNullOrWhiteSpace(tabName) || string.IsNullOrWhiteSpace(panelName))
                return;

            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;
                if (ribbon?.Tabs == null)
                    return;

                foreach (RibbonTab tab in ribbon.Tabs)
                {
                    if (tab == null || !TitleMatches(tab.Title, tabName))
                        continue;

                    if (tab.Panels == null)
                        continue;

                    foreach (RibbonPanel panel in tab.Panels)
                    {
                        if (panel?.Source == null)
                            continue;

                        string panelId = panel.Source.Id ?? string.Empty;
                        string panelTitle = panel.Source.Title ?? string.Empty;
                        if (!TitleMatches(panelTitle, panelName) && !TitleMatches(panelId, panelName))
                            continue;

                        panel.IsVisible = false;
                    }
                }
            }
            catch
            {
            }
        }

        internal static void HideKeyboardOnlyCommands(string tabName, string hiddenPanelName, string legacyPanelName, string commandId)
        {
            if (string.IsNullOrWhiteSpace(tabName) || string.IsNullOrWhiteSpace(commandId))
                return;

            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;
                if (ribbon?.Tabs == null)
                    return;

                foreach (RibbonTab tab in ribbon.Tabs)
                {
                    if (tab == null || !TitleMatches(tab.Title, tabName))
                        continue;

                    if (tab.Panels == null)
                        continue;

                    foreach (RibbonPanel panel in tab.Panels)
                    {
                        if (panel?.Source == null)
                            continue;

                        string panelId = panel.Source.Id ?? string.Empty;
                        string panelTitle = panel.Source.Title ?? string.Empty;
                        bool hiddenPanel = TitleMatches(panelTitle, hiddenPanelName)
                            || TitleMatches(panelId, hiddenPanelName)
                            || TitleMatches(panelTitle, legacyPanelName)
                            || TitleMatches(panelId, legacyPanelName);

                        if (hiddenPanel)
                        {
                            panel.IsVisible = false;
                        }

                        HideMatchingItems(panel, commandId, hideAllItemsInHiddenPanel: hiddenPanel);
                    }
                }
            }
            catch
            {
            }
        }

        internal static void HideCreateSpoolFromVisiblePanels(string tabName, string visiblePanelName, string commandId)
        {
            if (string.IsNullOrWhiteSpace(tabName) || string.IsNullOrWhiteSpace(visiblePanelName))
                return;

            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;
                if (ribbon?.Tabs == null)
                    return;

                foreach (RibbonTab tab in ribbon.Tabs)
                {
                    if (tab == null || !TitleMatches(tab.Title, tabName))
                        continue;

                    if (tab.Panels == null)
                        continue;

                    foreach (RibbonPanel panel in tab.Panels)
                    {
                        if (panel?.Source == null)
                            continue;

                        string panelTitle = panel.Source.Title ?? string.Empty;
                        if (!TitleMatches(panelTitle, visiblePanelName))
                            continue;

                        HideMatchingItems(panel, commandId, hideAllItemsInHiddenPanel: false);
                    }
                }
            }
            catch
            {
            }
        }

        private static void HideMatchingItems(RibbonPanel panel, string commandId, bool hideAllItemsInHiddenPanel)
        {
            if (panel?.Source?.Items == null)
                return;

            foreach (RibbonItem item in panel.Source.Items)
            {
                if (item == null)
                    continue;

                if (hideAllItemsInHiddenPanel || ItemMatchesCommand(item, commandId))
                {
                    item.IsVisible = false;
                    item.ShowText = false;
                    item.ShowImage = false;
                }
            }
        }

        private static bool ItemMatchesCommand(RibbonItem item, string commandId)
        {
            if (item == null || string.IsNullOrWhiteSpace(commandId))
                return false;

            if (string.Equals(item.Id, commandId, StringComparison.OrdinalIgnoreCase))
                return true;

            string automationName = item.AutomationName ?? string.Empty;
            if (automationName.IndexOf("Create Spool", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string text = item.Text ?? string.Empty;
            return text.IndexOf("Create Spool", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TitleMatches(string value, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected))
                return false;

            return string.Equals(value?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
