using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class CreateAssemblyDialogWindow : Window
{
	private readonly IReadOnlyList<AssemblyNamingCategoryOption> _options;

	internal TextBox txtAssemblyName;

	internal ComboBox cmbNamingCategory;

	internal TextBox txtAssemblyPackageName;

	internal CheckBox chkChainSpooling;

	internal TextBlock txtDialogTitle;

	public string PersistedPackageNameSnapshot { get; private set; }

	public string EnteredAssemblyName { get; private set; }

	public ElementId SelectedNamingCategoryId { get; private set; }

	public bool ChainSpoolingEnabled { get; private set; }

	public CreateAssemblyDialogWindow(IReadOnlyList<AssemblyNamingCategoryOption> namingCategoryOptions)
	{
		_options = namingCategoryOptions ?? new List<AssemblyNamingCategoryOption>();
		InitializeComponent();
		cmbNamingCategory.ItemsSource = _options;
		cmbNamingCategory.DisplayMemberPath = "DisplayName";
		cmbNamingCategory.SelectedIndex = ((_options.Count <= 0) ? (-1) : 0);
		cmbNamingCategory.IsEnabled = _options.Count > 1;
		CreateAssemblyDialogSettings createAssemblyDialogSettings = CreateAssemblyDialogSettings.Load();
		txtAssemblyPackageName.Text = createAssemblyDialogSettings.LastPackageName ?? string.Empty;
		txtAssemblyName.Text = ResolveInitialAssemblyName(createAssemblyDialogSettings);
		if (chkChainSpooling != null)
		{
			chkChainSpooling.IsChecked = createAssemblyDialogSettings.LastChainSpooling;
		}
	}

	private static string ResolveInitialAssemblyName(CreateAssemblyDialogSettings settings)
	{
		string text = AssemblyTypeNaming.Sanitize(settings?.LastAssemblyName ?? string.Empty);
		if (text.Length > 0)
		{
			return text;
		}
		string text2 = AssemblyTypeNaming.Sanitize(settings?.LastPackageName ?? string.Empty);
		if (text2.Length > 0)
		{
			return text2;
		}
		return "Assembly-01";
	}

	private void BtnOk_Click(object sender, RoutedEventArgs e)
	{
		string text = AssemblyTypeNaming.Sanitize(txtAssemblyName.Text);
		if (text.Length == 0)
		{
			SsSavantMessageBox.Show(this, "Enter an assembly name for the new assembly. Avoid characters such as \\ / : { } [ ] | ; < > ?", base.Title, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (!string.Equals(text, (txtAssemblyName.Text ?? string.Empty).Trim(), StringComparison.Ordinal))
		{
			txtAssemblyName.Text = text;
		}
		if (!(cmbNamingCategory.SelectedItem is AssemblyNamingCategoryOption assemblyNamingCategoryOption))
		{
			SsSavantMessageBox.Show(this, "Choose a naming category.", base.Title, MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		EnteredAssemblyName = text;
		SelectedNamingCategoryId = assemblyNamingCategoryOption.CategoryId;
		PersistedPackageNameSnapshot = (txtAssemblyPackageName.Text ?? string.Empty).Trim();
		ChainSpoolingEnabled = chkChainSpooling?.IsChecked == true;
		base.DialogResult = true;
		Close();
	}

	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}

	public void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.CreateAssemblyDialogWindow.xaml");
		SpoolingManagerXamlLoader.ApplyWindow(this, source);
		SsSavantNeonChrome.ApplyChromelessDialog(this, allowResize: false);
		txtDialogTitle = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtDialogTitle");
		SsSavantNeonChrome.ApplyNeonDialogTitle(txtDialogTitle, useScriptFont: true);
		txtAssemblyName = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtAssemblyName");
		cmbNamingCategory = SpoolingManagerXamlLoader.Find<ComboBox>(this, "cmbNamingCategory");
		txtAssemblyPackageName = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtAssemblyPackageName");
		chkChainSpooling = SpoolingManagerXamlLoader.Find<CheckBox>(this, "chkChainSpooling");
		SpoolingManagerXamlLoader.FindButtonByContent(this, "OK").Click += BtnOk_Click;
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Cancel").Click += BtnCancel_Click;
	}
}
