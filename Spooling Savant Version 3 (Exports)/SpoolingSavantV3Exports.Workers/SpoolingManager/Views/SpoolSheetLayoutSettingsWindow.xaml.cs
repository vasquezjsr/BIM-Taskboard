using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;
using MediaColor = System.Windows.Media.Color;
using Autodesk.Revit.UI;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class SpoolSheetLayoutSettingsWindow : Window
{
	private const string BrowserLocationAssemblies = "Assemblies";

	private const string BrowserLocationSheets = "Sheets";

	private const double DefaultScheduleInsetInches = 0.125;

	private static readonly (string Label, double Inches)[] ScheduleInsetOptions =
	{
		("0\"", 0.0),
		("1/16\"", 1.0 / 16.0),
		("1/8\"", 1.0 / 8.0),
		("3/16\"", 3.0 / 16.0),
		("1/4\"", 1.0 / 4.0),
		("5/16\"", 5.0 / 16.0),
		("3/8\"", 3.0 / 8.0),
		("7/16\"", 7.0 / 16.0),
		("1/2\"", 1.0 / 2.0)
	};

	private readonly SpoolingManagerSettings _settings;

	private readonly SpoolingManagerKind _productKind;

	private static readonly Brush ErrorBorderBrush = new SolidColorBrush((MediaColor)ColorConverter.ConvertFromString("#CC4444"));

	internal TextBlock txtLayoutTitle;

	internal ComboBox cmbScheduleInsetLeftInches;

	internal ComboBox cmbScheduleInsetTopInches;

	internal TextBox txtWeldLogInsetLeftInches;

	internal TextBox txtWeldLogInsetBottomInches;

	internal TextBox txtWeldLogProjectStripInches;

	internal TextBox txtWeldLogRowSpacingInches;

	internal TextBox txtWeldLogMaxRows;

	internal ComboBox cmbProjectBrowserLocation;

	public SpoolSheetLayoutSettingsWindow(UIApplication uiapp, SpoolingManagerSettings settings, SpoolingManagerKind productKind)
	{
		_ = uiapp;
		_settings = settings ?? throw new ArgumentNullException("settings");
		_productKind = productKind;
		InitializeComponent();
		if (_productKind.IsMmcStyle())
		{
			base.Title = "Spool sheet layout (MMC)";
			txtLayoutTitle.Text = "Schedule on sheet (MMC)";
		}
		else if (_productKind != SpoolingManagerKind.AutoDimensionLab)
		{
			txtLayoutTitle.Text = "Schedule on sheet";
		}
		InitializeProjectBrowserLocationOptions();
		InitializeScheduleInsetOptions();
		PopulateFromSettings();
		SpoolingManagerXamlLoader.Find<Button>(this, "btnCancel").Click += BtnCancelLayout_Click;
		SpoolingManagerXamlLoader.Find<Button>(this, "btnOk").Click += BtnOk_Click;
	}

	private void BtnCancelLayout_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}

	private void InitializeProjectBrowserLocationOptions()
	{
		if (cmbProjectBrowserLocation == null)
		{
			return;
		}
		cmbProjectBrowserLocation.Items.Clear();
		cmbProjectBrowserLocation.Items.Add(BrowserLocationAssemblies);
		cmbProjectBrowserLocation.Items.Add(BrowserLocationSheets);
	}

	private void InitializeScheduleInsetOptions()
	{
		FillScheduleInsetCombo(cmbScheduleInsetLeftInches);
		FillScheduleInsetCombo(cmbScheduleInsetTopInches);
	}

	private static void FillScheduleInsetCombo(ComboBox combo)
	{
		if (combo == null)
		{
			return;
		}

		combo.Items.Clear();
		foreach ((string label, double inches) in ScheduleInsetOptions)
		{
			combo.Items.Add(new ScheduleInsetChoice(label, inches));
		}
	}

	private void PopulateFromSettings()
	{
		if (cmbProjectBrowserLocation != null)
		{
			cmbProjectBrowserLocation.SelectedItem = (_settings.UseRegularSheetBranch ? BrowserLocationSheets : BrowserLocationAssemblies);
		}
		SelectScheduleInset(cmbScheduleInsetLeftInches, _settings.ScheduleInsetFromTitleBlockLeftInches);
		SelectScheduleInset(cmbScheduleInsetTopInches, _settings.ScheduleInsetFromTitleBlockTopInches);
		txtWeldLogInsetLeftInches.Text = (_settings.WeldLogInsetFromTitleBlockLeftInches.HasValue ? _settings.WeldLogInsetFromTitleBlockLeftInches.Value.ToString(CultureInfo.CurrentCulture) : string.Empty);
		txtWeldLogInsetBottomInches.Text = (_settings.WeldLogInsetFromTitleBlockBottomInches.HasValue ? _settings.WeldLogInsetFromTitleBlockBottomInches.Value.ToString(CultureInfo.CurrentCulture) : string.Empty);
		txtWeldLogProjectStripInches.Text = (_settings.WeldLogProjectStripHeightInches.HasValue ? _settings.WeldLogProjectStripHeightInches.Value.ToString(CultureInfo.CurrentCulture) : string.Empty);
		txtWeldLogRowSpacingInches.Text = (_settings.WeldLogRowSpacingInches.HasValue ? _settings.WeldLogRowSpacingInches.Value.ToString(CultureInfo.CurrentCulture) : string.Empty);
		txtWeldLogMaxRows.Text = (_settings.WeldLogMaxRows.HasValue ? _settings.WeldLogMaxRows.Value.ToString(CultureInfo.CurrentCulture) : string.Empty);
	}

	private static void SelectScheduleInset(ComboBox combo, double? inches)
	{
		if (combo == null || combo.Items.Count == 0)
		{
			return;
		}

		double target = inches ?? DefaultScheduleInsetInches;
		ScheduleInsetChoice best = null;
		double bestDelta = double.MaxValue;
		foreach (object item in combo.Items)
		{
			if (!(item is ScheduleInsetChoice choice))
			{
				continue;
			}

			double delta = Math.Abs(choice.Inches - target);
			if (delta < bestDelta)
			{
				bestDelta = delta;
				best = choice;
			}
		}

		combo.SelectedItem = best ?? combo.Items[2]; // 1/8"
	}

	private static double? GetSelectedScheduleInsetInches(ComboBox combo)
	{
		if (combo?.SelectedItem is ScheduleInsetChoice choice)
		{
			return choice.Inches;
		}

		return DefaultScheduleInsetInches;
	}

	private void BtnOk_Click(object sender, RoutedEventArgs e)
	{
		ResetValidationVisuals();
		if (TryParseOptionalInches(txtWeldLogInsetLeftInches, "Weld log inset from title block left", out var weldLogLeft)
			&& TryParseOptionalInches(txtWeldLogProjectStripInches, "Weld log project strip height", out var weldLogProjectStrip)
			&& TryParseOptionalInches(txtWeldLogInsetBottomInches, "Weld log inset from title block bottom", out var weldLogBottom)
			&& TryParseOptionalInches(txtWeldLogRowSpacingInches, "Weld log row spacing", out var weldLogRowSpacing)
			&& TryParseOptionalPositiveInt(txtWeldLogMaxRows, "Weld log row count", out var weldLogMaxRows))
		{
			_settings.UseRegularSheetBranch = string.Equals(cmbProjectBrowserLocation?.SelectedItem as string, BrowserLocationSheets, StringComparison.Ordinal);
			_settings.ScheduleInsetFromTitleBlockLeftInches = GetSelectedScheduleInsetInches(cmbScheduleInsetLeftInches);
			_settings.ScheduleInsetFromTitleBlockTopInches = GetSelectedScheduleInsetInches(cmbScheduleInsetTopInches);
			_settings.WeldLogInsetFromTitleBlockLeftInches = weldLogLeft;
			_settings.WeldLogProjectStripHeightInches = weldLogProjectStrip;
			_settings.WeldLogInsetFromTitleBlockBottomInches = weldLogBottom;
			_settings.WeldLogRowSpacingInches = weldLogRowSpacing;
			_settings.WeldLogMaxRows = weldLogMaxRows;
			base.DialogResult = true;
			Close();
		}
	}

	private void ResetValidationVisuals()
	{
		ResetTextBox(txtWeldLogInsetLeftInches);
		ResetTextBox(txtWeldLogInsetBottomInches);
		ResetTextBox(txtWeldLogProjectStripInches);
		ResetTextBox(txtWeldLogRowSpacingInches);
		ResetTextBox(txtWeldLogMaxRows);
	}

	private static void ResetTextBox(TextBox box)
	{
		if (box == null)
		{
			return;
		}
		box.ClearValue(Control.BorderBrushProperty);
		box.ClearValue(Control.BorderThicknessProperty);
	}

	private bool TryParseOptionalPositiveInt(TextBox box, string fieldLabel, out int? value)
	{
		value = null;
		string text = box.Text?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			return true;
		}
		if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var result) && result > 0)
		{
			value = result;
			return true;
		}
		if (box != null)
		{
			box.BorderBrush = ErrorBorderBrush;
			box.BorderThickness = new Thickness(2.0);
		}
		MessageBox.Show(this, fieldLabel + " must be a whole number greater than zero, or leave blank for the built-in default.", "Spooling Savant V3 (Exports)", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		return false;
	}

	private bool TryParseOptionalInches(TextBox box, string fieldLabel, out double? inches)
	{
		inches = null;
		string text = box.Text?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			return true;
		}
		if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var result))
		{
			inches = result;
			return true;
		}
		if (TryParseInchesLoose(text, out var inches2))
		{
			inches = inches2;
			return true;
		}
		if (box != null)
		{
			box.BorderBrush = ErrorBorderBrush;
			box.BorderThickness = new Thickness(2.0);
		}
		MessageBox.Show(this, fieldLabel + " must be a number in inches (e.g. 0.25 or 1/8 or 1/8\"), or leave blank for the built-in default.", "Spooling Savant V3 (Exports)", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		return false;
	}

	private static bool TryParseInchesLoose(string text, out double inches)
	{
		inches = 0.0;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string text2 = text.Trim();
		if (text2.EndsWith("\"", StringComparison.Ordinal))
		{
			text2 = text2.Substring(0, text2.Length - 1).Trim();
		}
		if (text2.EndsWith("in.", StringComparison.OrdinalIgnoreCase))
		{
			text2 = text2.Substring(0, text2.Length - 3).Trim();
		}
		else if (text2.Length >= 2 && text2.EndsWith("in", StringComparison.OrdinalIgnoreCase))
		{
			text2 = text2.Substring(0, text2.Length - 2).Trim();
		}
		if (double.TryParse(text2, NumberStyles.Float, CultureInfo.CurrentCulture, out var result))
		{
			inches = result;
			return true;
		}
		string[] array = text2.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
		if (array.Length == 2 && array[1].IndexOf('/') >= 0 && double.TryParse(array[0], NumberStyles.Float, CultureInfo.CurrentCulture, out var result2) && TryParseSimpleFraction(array[1], out var value))
		{
			inches = result2 + value;
			return true;
		}
		return TryParseSimpleFraction(text2, out inches);
	}

	private static bool TryParseSimpleFraction(string token, out double value)
	{
		value = 0.0;
		if (string.IsNullOrWhiteSpace(token))
		{
			return false;
		}
		int num = token.IndexOf('/');
		if (num <= 0 || num >= token.Length - 1)
		{
			return false;
		}
		string s = token.Substring(0, num).Trim();
		string s2 = token.Substring(num + 1).Trim();
		if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return false;
		}
		if (!double.TryParse(s2, NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) || Math.Abs(result2) < 1E-12)
		{
			return false;
		}
		value = result / result2;
		return true;
	}

	public void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.SpoolSheetLayoutSettingsWindow.xaml");
		SpoolingManagerXamlLoader.ApplyWindow(this, source, _productKind);
		txtLayoutTitle = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtLayoutTitle");
		cmbScheduleInsetLeftInches = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbScheduleInsetLeftInches");
		cmbScheduleInsetTopInches = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbScheduleInsetTopInches");
		txtWeldLogInsetLeftInches = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtWeldLogInsetLeftInches");
		txtWeldLogInsetBottomInches = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtWeldLogInsetBottomInches");
		txtWeldLogProjectStripInches = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtWeldLogProjectStripInches");
		txtWeldLogRowSpacingInches = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtWeldLogRowSpacingInches");
		txtWeldLogMaxRows = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtWeldLogMaxRows");
		cmbProjectBrowserLocation = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbProjectBrowserLocation");
	}

	private sealed class ScheduleInsetChoice
	{
		public ScheduleInsetChoice(string label, double inches)
		{
			Label = label;
			Inches = inches;
		}

		public string Label { get; }

		public double Inches { get; }

		public override string ToString()
		{
			return Label;
		}
	}
}
