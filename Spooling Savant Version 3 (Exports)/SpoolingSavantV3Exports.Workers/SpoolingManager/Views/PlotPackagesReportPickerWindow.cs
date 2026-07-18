using System;
using System.Windows;
using System.Windows.Controls;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

internal sealed class PlotPackagesReportPickerWindow : Window
{
	private const double DialogPadding = 12.0;

	private readonly CheckBox _chkSpools;
	private readonly CheckBox _chkSpoolMap;
	private readonly CheckBox _chkAssemblyList;
	private readonly CheckBox _chkBom;
	private readonly CheckBox _chkCutList;
	private readonly CheckBox _chkWeldLog;
	private readonly CheckBox _chkTigerStop;
	private readonly CheckBox _chkPcfFiles;
	private readonly TextBox _txtProject;
	private readonly TextBox _txtCreatedBy;
	private readonly TextBox _txtDate;

	public PlotPackagesReportOptions SelectedOptions { get; private set; }

	public PlotPackagesReportPickerWindow(string defaultProjectName = null, string defaultCreatedBy = null, string defaultDateText = null)
	{
		Title = "Plot Packages";
		Width = 420.0;
		MinWidth = 420.0;
		MaxWidth = 420.0;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		SsSavantChrome.MergeInto(this);

		_chkSpools = new CheckBox { Content = "Spools Combined", IsChecked = true };
		_chkSpoolMap = new CheckBox { Content = "Spool Map", IsChecked = true };
		_chkAssemblyList = new CheckBox { Content = "Assembly List", IsChecked = true };
		_chkBom = new CheckBox { Content = "Bill of Materials", IsChecked = true };
		_chkCutList = new CheckBox { Content = "Cut List", IsChecked = true };
		_chkWeldLog = new CheckBox { Content = "Weld Log", IsChecked = true };
		_chkTigerStop = new CheckBox { Content = "TigerStop (Copper / PVC CSV)", IsChecked = true };
		_chkPcfFiles = new CheckBox { Content = "PCF Files (all materials)", IsChecked = true, Margin = new Thickness(0.0, 0.0, 0.0, 8.0) };

		_txtProject = new TextBox
		{
			Text = defaultProjectName ?? string.Empty,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		_txtCreatedBy = new TextBox
		{
			Text = string.IsNullOrWhiteSpace(defaultCreatedBy) ? (Environment.UserName ?? string.Empty) : defaultCreatedBy,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		_txtDate = new TextBox
		{
			Text = string.IsNullOrWhiteSpace(defaultDateText) ? DateTime.Now.ToString("M/d/yyyy h:mm:ss tt") : defaultDateText,
			Margin = new Thickness(0.0, 0.0, 0.0, 0.0)
		};

		var title = new TextBlock
		{
			Text = "Which reports do you want to plot?",
			Style = TryFindResource("SsSavantDialogTitleText") as Style
		};
		var hint = new TextBlock
		{
			Text = "Choose one or more outputs, then click Continue. On the next step you pick the folder; PDF copies of each report (including Spool Map) are saved there for field printing. Existing files with the same names are replaced.",
			Style = TryFindResource("SsSavantDialogHintText") as Style
		};
		var infoTitle = new TextBlock
		{
			Text = "Report information",
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 8.0, 0.0, 6.0)
		};
		var infoHint = new TextBlock
		{
			Text = "These values appear on Assembly List, Bill of Materials, and Cut List headers.",
			Style = TryFindResource("SsSavantDialogHintText") as Style,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};

		var continueButton = new Button
		{
			Content = "Continue",
			Width = 100.0,
			IsDefault = true,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		var cancelButton = new Button
		{
			Content = "Cancel",
			Width = 100.0,
			IsCancel = true,
			Margin = new Thickness(0.0)
		};

		continueButton.Click += (_, __) =>
		{
			var options = new PlotPackagesReportOptions
			{
				IncludeSpoolsCombined = _chkSpools.IsChecked == true,
				IncludeSpoolMap = _chkSpoolMap.IsChecked == true,
				IncludeAssemblyList = _chkAssemblyList.IsChecked == true,
				IncludeBillOfMaterials = _chkBom.IsChecked == true,
				IncludeCutList = _chkCutList.IsChecked == true,
				IncludeWeldLog = _chkWeldLog.IsChecked == true,
				IncludeTigerStop = _chkTigerStop.IsChecked == true,
				IncludePcfFiles = _chkPcfFiles.IsChecked == true,
				ProjectName = (_txtProject.Text ?? string.Empty).Trim(),
				CreatedBy = (_txtCreatedBy.Text ?? string.Empty).Trim(),
				DateText = (_txtDate.Text ?? string.Empty).Trim()
			};

			if (!options.AnySelected)
			{
				MessageBox.Show(this, "Select at least one report type.", Title, MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}

			SelectedOptions = options;
			DialogResult = true;
			Close();
		};

		cancelButton.Click += (_, __) =>
		{
			DialogResult = false;
			Close();
		};

		var footer = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 16.0, 0.0, 0.0)
		};
		footer.Children.Add(continueButton);
		footer.Children.Add(cancelButton);

		var layout = new StackPanel
		{
			Children =
			{
				title,
				hint,
				_chkSpools,
				_chkSpoolMap,
				_chkAssemblyList,
				_chkBom,
				_chkCutList,
				_chkWeldLog,
				_chkTigerStop,
				_chkPcfFiles,
				infoTitle,
				infoHint,
				CreateFieldCaption("Project"),
				_txtProject,
				CreateFieldCaption("Created By"),
				_txtCreatedBy,
				CreateFieldCaption("Date"),
				_txtDate,
				footer
			}
		};

		var root = new Border();
		SsSavantDialogChrome.ApplyThemedBorder(root, new Thickness(DialogPadding));
		root.Child = layout;
		Content = root;
	}

	private TextBlock CreateFieldCaption(string caption)
	{
		return new TextBlock
		{
			Text = caption,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
	}
}
