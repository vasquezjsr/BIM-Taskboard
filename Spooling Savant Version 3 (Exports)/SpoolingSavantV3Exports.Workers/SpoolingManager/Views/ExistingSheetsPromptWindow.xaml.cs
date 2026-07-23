using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class ExistingSheetsPromptWindow : Window
{
	internal ListBox lstExistingSheets;

	internal Button btnCancel;

	internal Button btnSkipExisting;

	internal Button btnRegenerateExisting;

	public ExistingSheetAction SelectedAction { get; private set; }

	public ExistingSheetsPromptWindow(IEnumerable<string> assemblyNames)
	{
		InitializeComponent();
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Width = 480;
		Height = 340;
		lstExistingSheets.ItemsSource = (from x in assemblyNames
			where !string.IsNullOrWhiteSpace(x)
			orderby x
			select x).ToList();
	}

	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		SelectedAction = ExistingSheetAction.Cancel;
		base.DialogResult = false;
		Close();
	}

	private void BtnSkipExisting_Click(object sender, RoutedEventArgs e)
	{
		SelectedAction = ExistingSheetAction.SkipExisting;
		base.DialogResult = true;
		Close();
	}

	private void BtnRegenerateExisting_Click(object sender, RoutedEventArgs e)
	{
		SelectedAction = ExistingSheetAction.RegenerateExisting;
		base.DialogResult = true;
		Close();
	}

	public void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.ExistingSheetsPromptWindow.xaml");
		// Chromeless rounded sign, matching the sheet generation progress box.
		// Must be set before the window is shown.
		base.WindowStyle = WindowStyle.None;
		base.AllowsTransparency = true;
		SpoolingManagerXamlLoader.ApplyWindow(this, source);
		base.Background = System.Windows.Media.Brushes.Transparent;
		base.ResizeMode = ResizeMode.NoResize;
		if (Content is Border shell)
		{
			SpoolingSavantV3Exports.Workers.UI.SsSavantNeonChrome.ApplyShell(shell);
		}
		SpoolingSavantV3Exports.Workers.UI.SsSavantDialogForeground.Attach(this);
		lstExistingSheets = SpoolingManagerXamlLoader.Find<ListBox>(this, "lstExistingSheets");
		btnCancel = SpoolingManagerXamlLoader.Find<Button>(this, "btnCancel");
		btnSkipExisting = SpoolingManagerXamlLoader.Find<Button>(this, "btnSkipExisting");
		btnRegenerateExisting = SpoolingManagerXamlLoader.Find<Button>(this, "btnRegenerateExisting");
		btnCancel.Click += BtnCancel_Click;
		btnSkipExisting.Click += BtnSkipExisting_Click;
		btnRegenerateExisting.Click += BtnRegenerateExisting_Click;
		// Without a title bar the window still needs to be movable.
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
}
