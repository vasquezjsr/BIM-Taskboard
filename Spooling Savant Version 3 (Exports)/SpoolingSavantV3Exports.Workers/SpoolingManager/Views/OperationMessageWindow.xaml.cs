using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class OperationMessageWindow : Window
{
	internal TextBlock txtMessage;

	public OperationMessageWindow(string title, string message)
	{
		InitializeComponent();
		base.Title = (string.IsNullOrWhiteSpace(title) ? "Spooling Savant" : title);
		txtMessage.Text = message ?? string.Empty;
		Loaded += OnLoadedAdjustSize;
		// Chromeless neon theme: rounded border needs a transparent window, and the
		// missing title bar means the window must be draggable by its body.
		base.WindowStyle = WindowStyle.None;
		base.AllowsTransparency = true;
		base.Background = Brushes.Transparent;
		MouseLeftButtonDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
			{
				try
				{
					DragMove();
				}
				catch
				{
				}
			}
		};
	}

	private void OnLoadedAdjustSize(object sender, RoutedEventArgs e)
	{
		Loaded -= OnLoadedAdjustSize;
		try
		{
			txtMessage?.UpdateLayout();
			UpdateLayout();

			int lineCount = CountLines(txtMessage?.Text);
			double contentWidth = MeasureMessageWidth(txtMessage);
			double targetWidth = Math.Max(MinWidth, Math.Min(MaxWidth, contentWidth + 48));
			double lineHeight = txtMessage?.FontSize > 0 ? txtMessage.FontSize * 1.35 : 16;
			double contentHeight = Math.Max(lineHeight, lineCount * lineHeight) + 72;
			double targetHeight = Math.Max(MinHeight, Math.Min(MaxHeight, contentHeight));

			Width = targetWidth;
			Height = targetHeight;
		}
		catch
		{
		}
	}

	private static int CountLines(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return 1;
		}

		int count = 1;
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] == '\n')
			{
				count++;
			}
		}

		return Math.Max(1, count);
	}

	private static double MeasureMessageWidth(TextBlock block)
	{
		if (block == null || string.IsNullOrEmpty(block.Text))
		{
			return 360;
		}

		double widest = 360;
		string[] lines = block.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		foreach (string line in lines)
		{
			var probe = new FormattedText(
				line.Length == 0 ? " " : line,
				CultureInfo.CurrentUICulture,
				FlowDirection.LeftToRight,
				new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
				block.FontSize,
				Brushes.Black,
				VisualTreeHelper.GetDpi(block).PixelsPerDip);
			widest = Math.Max(widest, probe.WidthIncludingTrailingWhitespace + 8);
		}

		return widest;
	}

	private void BtnOk_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = true;
		Close();
	}

	public void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.OperationMessageWindow.xaml");
		SpoolingManagerXamlLoader.ApplyWindow(this, source);
		if (Content is Border shell)
		{
			SpoolingSavantV3Exports.Workers.UI.SsSavantNeonChrome.ApplyShell(shell);
		}
		SpoolingSavantV3Exports.Workers.UI.SsSavantDialogForeground.Attach(this);
		txtMessage = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtMessage");
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Close").Click += BtnOk_Click;
	}
}
