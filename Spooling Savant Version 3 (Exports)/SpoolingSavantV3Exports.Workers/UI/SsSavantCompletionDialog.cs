using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SpoolingSavantV3Exports.Workers.UI
{
	/// <summary>
	/// Alpha-themed completion/summary dialog for commands. Sized to fit message content.
	/// </summary>
	public static class SsSavantCompletionDialog
	{
		private const double ContentPadding = 16.0;
		private const double ContentMaxWidth = 640.0;
		private const double ScrollMaxHeight = 520.0;

		/// <summary>Show completion dialog; parents to Revit's main window when possible so it stays above Revit.</summary>
		public static void Show(string windowTitle, string message)
		{
			ShowCore(windowTitle, message, null, null);
		}

		public static void Show(string windowTitle, string message, Window owner)
		{
			ShowCore(windowTitle, message, owner, null);
		}

		/// <summary>Prefer this from <see cref="Autodesk.Revit.UI.IExternalCommand.Execute"/> using <c>commandData.Application.MainWindowHandle</c>.</summary>
		public static void Show(string windowTitle, string message, IntPtr revitMainWindowHandle)
		{
			ShowCore(windowTitle, message, null,
				revitMainWindowHandle != IntPtr.Zero ? revitMainWindowHandle : (IntPtr?)null);
		}

		private static void ShowCore(string windowTitle, string message, Window owner, IntPtr? revitOwnerHandle)
		{
			string body = message ?? string.Empty;

			var window = new Window
			{
				Title = string.IsNullOrWhiteSpace(windowTitle) ? "Spooling Savant" : windowTitle,
				SizeToContent = SizeToContent.WidthAndHeight,
				MinWidth = 320,
				MinHeight = 120,
				MaxWidth = 720,
				MaxHeight = 720,
				ResizeMode = ResizeMode.NoResize,
				ShowInTaskbar = false,
				// Chromeless neon theme (matches the progress window).
				WindowStyle = WindowStyle.None,
				AllowsTransparency = true,
				Background = Brushes.Transparent
			};

			window.MouseLeftButtonDown += (_, e) =>
			{
				if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
				{
					try
					{
						window.DragMove();
					}
					catch
					{
					}
				}
			};

			if (owner != null)
			{
				window.Owner = owner;
				window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
			}
			else
			{
				window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
				IntPtr hwnd = ResolveRevitMainWindowHandle(revitOwnerHandle);
				if (hwnd != IntPtr.Zero)
				{
					new WindowInteropHelper(window).Owner = hwnd;
				}
			}

			SsSavantChrome.MergeInto(window);

			var text = new TextBlock
			{
				Text = body,
				TextWrapping = TextWrapping.Wrap,
				VerticalAlignment = VerticalAlignment.Top,
				HorizontalAlignment = HorizontalAlignment.Left,
				TextAlignment = TextAlignment.Left,
				MaxWidth = ContentMaxWidth,
				FontSize = 12
			};

			var scroll = new ScrollViewer
			{
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				MaxHeight = ScrollMaxHeight,
				Content = text,
				Focusable = false
			};

			var btn = new Button
			{
				Content = "Close",
				Width = 88,
				Height = 23,
				Margin = new Thickness(0, ContentPadding, 0, 0),
				HorizontalAlignment = HorizontalAlignment.Right,
				IsDefault = true,
				IsCancel = true
			};
			btn.Click += (_, __) =>
			{
				window.DialogResult = true;
				window.Close();
			};

			var contentStack = new StackPanel
			{
				Orientation = Orientation.Vertical,
				Children = { scroll, btn }
			};

			// Neon shell in dark mode; same rounded chromeless panel without glow in light mode.
			var contentBorder = new Border
			{
				Child = contentStack,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Top,
				BorderThickness = new Thickness(2),
				CornerRadius = new CornerRadius(12),
				Padding = new Thickness(ContentPadding + 4)
			};
			SsSavantNeonChrome.ApplyShell(contentBorder);
			window.Content = contentBorder;
			SsSavantDialogForeground.Attach(window);

			window.Loaded += (_, __) =>
			{
				try
				{
					text.Measure(new Size(ContentMaxWidth, double.PositiveInfinity));
					text.UpdateLayout();
					window.UpdateLayout();

					double contentWidth = Math.Min(ContentMaxWidth, Math.Max(280, MeasureMessageWidth(text)));
					text.Width = contentWidth;
					text.MaxWidth = contentWidth;

					window.SizeToContent = SizeToContent.WidthAndHeight;
					window.InvalidateMeasure();
					window.UpdateLayout();
					window.Activate();
				}
				catch
				{
				}
			};

			window.ShowDialog();
		}

		private static double MeasureMessageWidth(TextBlock block)
		{
			if (block == null || string.IsNullOrEmpty(block.Text))
			{
				return 360;
			}

			double widest = 280;
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
				widest = Math.Max(widest, probe.WidthIncludingTrailingWhitespace + 4);
			}

			return widest;
		}

		private static IntPtr ResolveRevitMainWindowHandle(IntPtr? explicitHandle)
		{
			if (explicitHandle.HasValue && explicitHandle.Value != IntPtr.Zero)
			{
				return explicitHandle.Value;
			}

			return TryGetCurrentProcessMainWindowHandle();
		}

		private static IntPtr TryGetCurrentProcessMainWindowHandle()
		{
			try
			{
				using (Process p = Process.GetCurrentProcess())
				{
					p.Refresh();
					return p.MainWindowHandle;
				}
			}
			catch
			{
				return IntPtr.Zero;
			}
		}
	}
}
