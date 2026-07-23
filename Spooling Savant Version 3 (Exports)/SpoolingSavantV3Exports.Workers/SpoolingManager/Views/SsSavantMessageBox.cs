using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

/// <summary>
/// Themed drop-in replacement for <see cref="System.Windows.MessageBox"/> so every
/// popup matches the Spooling Savant progress box: chromeless, rounded, dark gradient
/// with the glowing blue border, draggable, with themed buttons.
/// The <see cref="MessageBoxImage"/> parameter is accepted for signature compatibility
/// but not rendered — the chrome itself is the branding.
/// </summary>
public class SsSavantMessageBox : Window
{
	private MessageBoxResult _result;

	private SsSavantMessageBox(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultResult)
	{
		Title = string.IsNullOrWhiteSpace(caption) ? "Spooling Savant" : caption;
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		Topmost = true;
		SizeToContent = SizeToContent.WidthAndHeight;
		MaxWidth = 560;

		SsSavantChrome.MergeInto(this);
		_result = GetDismissResult(button);

		bool neon = SsSavantNeonChrome.IsNeonEnabled;
		Border shell = new Border
		{
			BorderThickness = new Thickness(2),
			CornerRadius = new CornerRadius(12),
			Padding = new Thickness(20, 16, 20, 16),
			MinWidth = 340
		};
		SsSavantNeonChrome.ApplyShell(shell, neon);

		StackPanel root = new StackPanel();

		TextBlock message = new TextBlock
		{
			Text = messageBoxText ?? string.Empty,
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13,
			Margin = new Thickness(0, 4, 0, 18)
		};
		message.SetResourceReference(TextBlock.ForegroundProperty, "SsSavantForegroundPrimary");
		root.Children.Add(message);

		StackPanel buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		foreach ((string label, MessageBoxResult result) in GetButtons(button))
		{
			Button choice = new Button
			{
				Content = label,
				MinWidth = 88,
				Height = 30,
				Margin = new Thickness(8, 0, 0, 0),
				IsDefault = result == ResolveDefault(button, defaultResult),
				IsCancel = result == GetDismissResult(button)
			};
			MessageBoxResult captured = result;
			choice.Click += delegate
			{
				_result = captured;
				try
				{
					DialogResult = true;
				}
				catch
				{
				}
				Close();
			};
			buttons.Children.Add(choice);
		}
		root.Children.Add(buttons);

		shell.Child = root;
		Content = shell;

		SsSavantDialogForeground.Attach(this);

		// Without a title bar the window still needs to be movable.
		MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
		{
			if (e.ButtonState == MouseButtonState.Pressed)
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

	private static (string, MessageBoxResult)[] GetButtons(MessageBoxButton button)
	{
		switch (button)
		{
			case MessageBoxButton.OKCancel:
				return new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) };
			case MessageBoxButton.YesNo:
				return new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No) };
			case MessageBoxButton.YesNoCancel:
				return new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) };
			default:
				return new[] { ("OK", MessageBoxResult.OK) };
		}
	}

	/// <summary>Result returned when the dialog is dismissed with Esc.</summary>
	private static MessageBoxResult GetDismissResult(MessageBoxButton button)
	{
		switch (button)
		{
			case MessageBoxButton.OKCancel:
			case MessageBoxButton.YesNoCancel:
				return MessageBoxResult.Cancel;
			case MessageBoxButton.YesNo:
				return MessageBoxResult.No;
			default:
				return MessageBoxResult.OK;
		}
	}

	private static MessageBoxResult ResolveDefault(MessageBoxButton button, MessageBoxResult requested)
	{
		if (requested != MessageBoxResult.None)
		{
			return requested;
		}
		return button == MessageBoxButton.YesNo || button == MessageBoxButton.YesNoCancel
			? MessageBoxResult.Yes
			: MessageBoxResult.OK;
	}

	private static MessageBoxResult ShowCore(Window owner, string messageBoxText, string caption,
		MessageBoxButton button, MessageBoxResult defaultResult)
	{
		try
		{
			SsSavantMessageBox window = new SsSavantMessageBox(messageBoxText, caption, button, defaultResult);
			if (owner != null && owner.IsLoaded)
			{
				window.Owner = owner;
				window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
			}
			else
			{
				window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
			}
			window.ShowDialog();
			return window._result;
		}
		catch
		{
			// Never lose a message because theming failed.
			return MessageBox.Show(messageBoxText, caption, button, MessageBoxImage.None, defaultResult);
		}
	}

	// MessageBox-compatible overloads --------------------------------------------------

	public static MessageBoxResult Show(string messageBoxText)
		=> ShowCore(null, messageBoxText, null, MessageBoxButton.OK, MessageBoxResult.None);

	public static MessageBoxResult Show(string messageBoxText, string caption)
		=> ShowCore(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxResult.None);

	public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
		=> ShowCore(null, messageBoxText, caption, button, MessageBoxResult.None);

	public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
		=> ShowCore(null, messageBoxText, caption, button, MessageBoxResult.None);

	public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
		=> ShowCore(null, messageBoxText, caption, button, defaultResult);

	public static MessageBoxResult Show(Window owner, string messageBoxText, string caption)
		=> ShowCore(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxResult.None);

	public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button)
		=> ShowCore(owner, messageBoxText, caption, button, MessageBoxResult.None);

	public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
		=> ShowCore(owner, messageBoxText, caption, button, MessageBoxResult.None);

	public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
		=> ShowCore(owner, messageBoxText, caption, button, defaultResult);
}
