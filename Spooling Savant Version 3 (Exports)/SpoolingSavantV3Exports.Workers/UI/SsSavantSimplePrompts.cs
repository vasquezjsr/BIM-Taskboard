using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpoolingSavantV3Exports.Workers.UI
{
    /// <summary>WPF prompts using Revit Options–style dialog chrome.</summary>
    internal static class SsSavantSimplePrompts
    {
        internal static bool TryPromptString(
            string title,
            string label,
            string defaultValue,
            out string result,
            double width = 440,
            double minInputWidth = 360)
        {
            result = null;
            Window window = new Window
            {
                Title = title,
                Width = width,
                MinWidth = width,
                MaxWidth = width,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            SsSavantChrome.MergeInto(window);

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(
                new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                });

            var textBox = new TextBox { Text = defaultValue ?? string.Empty, MinWidth = minInputWidth };
            panel.Children.Add(textBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var ok = new Button { Content = "OK", Width = 88, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 88, IsCancel = true };
            ok.Click += (_, __) =>
            {
                window.DialogResult = true;
                window.Close();
            };

            cancel.Click += (_, __) =>
            {
                window.DialogResult = false;
                window.Close();
            };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            window.Content = panel;

            window.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    window.DialogResult = false;
                    window.Close();
                }
            };

            window.Loaded += (_, __) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            if (window.ShowDialog() != true)
            {
                return false;
            }

            result = textBox.Text ?? string.Empty;
            return true;
        }
    }
}
