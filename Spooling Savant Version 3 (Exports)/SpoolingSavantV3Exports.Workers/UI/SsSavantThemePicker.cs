using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpoolingSavantV3Exports.Workers.UI
{
    internal sealed class SsSavantThemePicker
    {
        private sealed class ThemeCard
        {
            internal UiThemePreset Preset { get; set; }
            internal Border Border { get; set; }
            internal TextBlock NameBlock { get; set; }
            internal TextBlock DescriptionBlock { get; set; }
            internal TextBlock CheckGlyph { get; set; }
        }

        private readonly FrameworkElement _hostWindow;
        private readonly Panel _hostPanel;
        private readonly List<ThemeCard> _cards = new List<ThemeCard>();
        private string _selectedThemeId;
        private readonly Action<string> _onThemeSelected;

        internal SsSavantThemePicker(
            FrameworkElement hostWindow,
            Panel hostPanel,
            string initialThemeId,
            Action<string> onThemeSelected)
        {
            _hostWindow = hostWindow;
            _hostPanel = hostPanel;
            _selectedThemeId = UiThemeCatalog.NormalizeThemeId(initialThemeId);
            _onThemeSelected = onThemeSelected;
            Build();
            SelectTheme(_selectedThemeId);
        }

        internal string SelectedThemeId => _selectedThemeId;

        private void Build()
        {
            _hostPanel.Children.Clear();
            _cards.Clear();

            var themeGrid = new Grid
            {
                Margin = new Thickness(-4, 0, -4, 0),
                MinHeight = 360,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            themeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            themeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int index = 0;
            foreach (UiThemePreset preset in UiThemeCatalog.Presets)
            {
                ThemeCard card = CreateThemeCard(preset);
                _cards.Add(card);

                int row = index / 2;
                while (themeGrid.RowDefinitions.Count <= row)
                {
                    themeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }

                Grid.SetRow(card.Border, row);
                Grid.SetColumn(card.Border, index % 2);
                themeGrid.Children.Add(card.Border);
                index++;
            }

            _hostPanel.Children.Add(themeGrid);
        }

        private void SelectTheme(string themeId)
        {
            _selectedThemeId = UiThemeCatalog.NormalizeThemeId(themeId);
            var preview = new UiAppearanceSettings
            {
                ThemeId = _selectedThemeId,
                UseRevitGraphics = UiThemeCatalog.UsesRevitColors(_selectedThemeId)
            };
            PaneThemeApplier.Apply(_hostWindow, UiThemeCatalog.ResolvePalette(preview));
            RefreshThemeCards();
            _onThemeSelected?.Invoke(_selectedThemeId);
        }

        private ThemeCard CreateThemeCard(UiThemePreset preset)
        {
            PaneColorPalette palette = preset.CreatePalette();

            var border = new Border
            {
                Margin = new Thickness(4),
                Padding = new Thickness(12),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                Focusable = true,
                MinHeight = 112,
                SnapsToDevicePixels = true
            };

            var cardRoot = new Grid();
            cardRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = preset.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushFromHex(palette.ForegroundPrimary)
            };
            Grid.SetColumn(name, 0);
            topRow.Children.Add(name);

            var checkGlyph = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Hidden
            };
            checkGlyph.SetResourceReference(TextBlock.ForegroundProperty, "SsSavantForegroundPrimary");
            Grid.SetColumn(checkGlyph, 1);
            topRow.Children.Add(checkGlyph);

            Grid.SetRow(topRow, 0);
            cardRoot.Children.Add(topRow);

            var description = new TextBlock
            {
                Text = preset.Description,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 10),
                Foreground = BrushFromHex(palette.ForegroundMuted)
            };
            Grid.SetRow(description, 1);
            cardRoot.Children.Add(description);

            var swatches = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            swatches.Children.Add(CreateSwatch(palette.ChromeBackground));
            swatches.Children.Add(CreateSwatch(palette.ListWellBackground));
            swatches.Children.Add(CreateSwatch(palette.InputBackground));
            swatches.Children.Add(CreateSwatch(palette.ForegroundPrimary));
            Grid.SetRow(swatches, 2);
            cardRoot.Children.Add(swatches);

            border.Child = cardRoot;

            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    SelectTheme(preset.Id);
                    e.Handled = true;
                }
            };
            border.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter && e.Key != Key.Space)
                    return;

                SelectTheme(preset.Id);
                e.Handled = true;
            };

            return new ThemeCard
            {
                Preset = preset,
                Border = border,
                NameBlock = name,
                DescriptionBlock = description,
                CheckGlyph = checkGlyph
            };
        }

        private static Border CreateSwatch(string hex)
        {
            return new Border
            {
                Width = 34,
                Height = 12,
                Margin = new Thickness(0, 0, 5, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A8A8A")),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
            };
        }

        private void RefreshThemeCards()
        {
            var selectionAccent = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4"));
            if (selectionAccent.CanFreeze)
            {
                selectionAccent.Freeze();
            }

            foreach (ThemeCard card in _cards)
            {
                PaneColorPalette palette = card.Preset.CreatePalette();
                palette = PaneThemeApplier.EnsureReadableContrast(palette) ?? palette;
                bool isSelected = string.Equals(card.Preset.Id, _selectedThemeId, StringComparison.OrdinalIgnoreCase);

                card.Border.Background = BrushFromHex(isSelected ? palette.HighlightBrush : palette.InputBackground);
                card.Border.BorderBrush = isSelected
                    ? selectionAccent
                    : BrushFromHex(palette.ListBorder);
                card.Border.BorderThickness = isSelected ? new Thickness(2) : new Thickness(1);
                card.Border.CornerRadius = new CornerRadius(8);
                card.NameBlock.Foreground = BrushFromHex(palette.ForegroundPrimary);
                card.DescriptionBlock.Foreground = BrushFromHex(palette.ForegroundMuted);
                card.CheckGlyph.Visibility = isSelected ? Visibility.Visible : Visibility.Hidden;
                card.CheckGlyph.Foreground = isSelected ? selectionAccent : card.CheckGlyph.Foreground;
            }
        }

        private static SolidColorBrush BrushFromHex(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }
    }
}
