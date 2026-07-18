namespace SpoolingSavantV3Exports.Workers.UI
{
    internal sealed class PaneColorPalette
    {
        internal PaneColorPalette(
            string chromeBackground,
            string listWellBackground,
            string shortcutTileBackground,
            string inputBackground,
            string foregroundPrimary,
            string foregroundMuted,
            string borderOuter,
            string listBorder,
            string highlightBrush = null,
            string pressedBrush = null)
        {
            ChromeBackground = chromeBackground;
            ListWellBackground = listWellBackground;
            ShortcutTileBackground = shortcutTileBackground;
            InputBackground = inputBackground;
            ForegroundPrimary = foregroundPrimary;
            ForegroundMuted = foregroundMuted;
            BorderOuter = borderOuter;
            ListBorder = listBorder;
            HighlightBrush = highlightBrush ?? inputBackground;
            PressedBrush = pressedBrush ?? chromeBackground;
        }

        internal string ChromeBackground { get; }
        internal string ListWellBackground { get; }
        internal string ShortcutTileBackground { get; }
        internal string InputBackground { get; }
        internal string ForegroundPrimary { get; }
        internal string ForegroundMuted { get; }
        internal string BorderOuter { get; }
        internal string ListBorder { get; }
        internal string HighlightBrush { get; }
        internal string PressedBrush { get; }
    }
}

