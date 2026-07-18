using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

internal sealed class CreateSpoolMapDialogWindow : Window
{
	private const double DialogPadding = 12.0;

	private readonly ComboBox _cmbPackage;
	private readonly ComboBox _cmbTitleBlock;
	private readonly ComboBox _cmbViewTemplate3D;
	private readonly ComboBox _cmbViewTemplatePlan;
	private readonly ComboBox _cmbAssemblyTagType;

	private readonly IReadOnlyList<CreateSpoolMapPackageOption> _packageOptions;

	public CreateSpoolMapRequest SelectedRequest { get; private set; }

	public CreateSpoolMapDialogWindow(
		Document doc,
		IReadOnlyList<CreateSpoolMapPackageOption> packageOptions,
		SpoolingManagerSettings defaults,
		SpoolingManagerKind productKind)
	{
		_packageOptions = packageOptions ?? Array.Empty<CreateSpoolMapPackageOption>();
		Title = "Create Spool Map";
		Width = 440.0;
		MinWidth = 440.0;
		MaxWidth = 440.0;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		SsSavantChrome.MergeInto(this);

		_cmbPackage = CreatePackageCombo();
		_cmbTitleBlock = CreateCombo();
		_cmbViewTemplate3D = CreateCombo();
		_cmbViewTemplatePlan = CreateCombo();
		_cmbAssemblyTagType = CreateCombo();

		foreach (CreateSpoolMapPackageOption option in _packageOptions.OrderBy(x => x.Label ?? string.Empty, StringComparer.OrdinalIgnoreCase))
		{
			_cmbPackage.Items.Add(option);
		}
		if (_cmbPackage.Items.Count > 0)
		{
			_cmbPackage.SelectedIndex = 0;
		}
		_cmbPackage.SelectionChanged += (_, __) => SyncPackageComboText();
		SyncPackageComboText();

		List<string> templateNames = LoadViewTemplateNames(doc);
		LoadTemplateCombo(_cmbViewTemplate3D, templateNames);
		LoadTemplateCombo(_cmbViewTemplatePlan, templateNames);
		LoadTitleBlocks(doc);
		LoadAssemblyTagTypes(doc);

		SpoolingManagerSettings settings = defaults ?? new SpoolingManagerSettings();
		SelectComboText(_cmbTitleBlock, settings.TitleBlockName);
		SelectComboText(_cmbAssemblyTagType, settings.AssemblyTagTypeName);
		SelectDefaultSpoolMapTemplate(_cmbViewTemplate3D, templateNames, is3D: true);
		SelectDefaultSpoolMapTemplate(_cmbViewTemplatePlan, templateNames, is3D: false);

		var title = new TextBlock
		{
			Text = "Create a spool map sheet",
			Style = TryFindResource("SsSavantDialogTitleText") as Style
		};
		var hint = new TextBlock
		{
			Text = "Choose the package, title block, 3D and floor plan view templates, and assembly tag type. A 3D view and floor plan will be created, filtered to the package, placed on a new sheet, and tagged on the 3D view only.",
			Style = TryFindResource("SsSavantDialogHintText") as Style,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};

		var createButton = new Button
		{
			Content = "Create",
			Width = 100.0,
			IsDefault = true,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		var cancelButton = new Button
		{
			Content = "Cancel",
			Width = 100.0,
			IsCancel = true
		};

		createButton.Click += (_, __) =>
		{
			if (!TryBuildRequest(productKind, out CreateSpoolMapRequest request, out string error))
			{
				MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
			SelectedRequest = request;
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
		footer.Children.Add(createButton);
		footer.Children.Add(cancelButton);

		var layout = new StackPanel
		{
			Children =
			{
				title,
				hint,
				CreateFieldCaption("Package"),
				_cmbPackage,
				CreateFieldCaption("Title block"),
				_cmbTitleBlock,
				CreateFieldCaption("3D view template"),
				_cmbViewTemplate3D,
				CreateFieldCaption("Floor plan view template"),
				_cmbViewTemplatePlan,
				CreateFieldCaption("Assembly tag type (3D view only)"),
				_cmbAssemblyTagType,
				footer
			}
		};

		var root = new Border
		{
			Padding = new Thickness(DialogPadding),
			Child = layout
		};
		Content = root;
	}

	private bool TryBuildRequest(SpoolingManagerKind productKind, out CreateSpoolMapRequest request, out string error)
	{
		request = null;
		error = null;
		if (_cmbPackage.SelectedItem is not CreateSpoolMapPackageOption packageOption)
		{
			error = "Select a package.";
			return false;
		}
		if (packageOption.AssemblyIds == null || packageOption.AssemblyIds.Count == 0)
		{
			error = "The selected package has no assemblies.";
			return false;
		}
		string titleBlock = (_cmbTitleBlock.Text ?? string.Empty).Trim();
		if (titleBlock.Length == 0)
		{
			error = "Select a title block.";
			return false;
		}
		string viewTemplate3D = (_cmbViewTemplate3D.Text ?? string.Empty).Trim();
		if (viewTemplate3D.Length == 0)
		{
			error = "Select a 3D view template.";
			return false;
		}
		string viewTemplatePlan = (_cmbViewTemplatePlan.Text ?? string.Empty).Trim();
		if (viewTemplatePlan.Length == 0)
		{
			error = "Select a floor plan view template.";
			return false;
		}
		string assemblyTag = (_cmbAssemblyTagType.Text ?? string.Empty).Trim();
		if (assemblyTag.Length == 0)
		{
			error = "Select an assembly tag type.";
			return false;
		}
		request = new CreateSpoolMapRequest
		{
			PackageLabel = packageOption.Label,
			PackageValue = packageOption.PackageValue ?? string.Empty,
			AssemblyIds = packageOption.AssemblyIds.ToList(),
			TitleBlockName = titleBlock,
			ViewTemplate3DName = viewTemplate3D,
			ViewTemplatePlanName = viewTemplatePlan,
			AssemblyTagTypeName = assemblyTag,
			ProductKind = productKind
		};
		return true;
	}

	private void SyncPackageComboText()
	{
		if (_cmbPackage.SelectedItem is CreateSpoolMapPackageOption option)
		{
			_cmbPackage.Text = option.Label ?? string.Empty;
		}
	}

	private static ComboBox CreatePackageCombo()
	{
		return new ComboBox
		{
			IsEditable = false,
			DisplayMemberPath = nameof(CreateSpoolMapPackageOption.Label),
			Height = 28.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
	}

	private static ComboBox CreateCombo()
	{
		return new ComboBox
		{
			IsEditable = true,
			Height = 28.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
	}

	private static TextBlock CreateFieldCaption(string text)
	{
		return new TextBlock
		{
			Text = text,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
	}

	private static void SelectComboText(ComboBox combo, string value)
	{
		if (combo == null || string.IsNullOrWhiteSpace(value))
		{
			return;
		}
		string trimmed = value.Trim();
		foreach (object item in combo.Items)
		{
			if (item is string text && string.Equals(text, trimmed, StringComparison.OrdinalIgnoreCase))
			{
				combo.SelectedItem = item;
				combo.Text = text;
				return;
			}
		}
		combo.Text = trimmed;
	}

	private static void SelectDefaultSpoolMapTemplate(ComboBox combo, IReadOnlyList<string> templateNames, bool is3D)
	{
		string preferred = PickDefaultSpoolMapTemplate(templateNames, is3D);
		if (!string.IsNullOrWhiteSpace(preferred))
		{
			SelectComboText(combo, preferred);
		}
	}

	private static string PickDefaultSpoolMapTemplate(IReadOnlyList<string> templateNames, bool is3D)
	{
		List<string> spoolMapTemplates = templateNames
			.Where(name => !string.IsNullOrWhiteSpace(name) && name.IndexOf("Spool Map", StringComparison.OrdinalIgnoreCase) >= 0)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (spoolMapTemplates.Count == 0)
		{
			return null;
		}

		if (is3D)
		{
			return spoolMapTemplates.FirstOrDefault(name => name.IndexOf("3D", StringComparison.OrdinalIgnoreCase) >= 0)
				?? spoolMapTemplates.First();
		}

		return spoolMapTemplates.FirstOrDefault(name => name.IndexOf("Floor Plan", StringComparison.OrdinalIgnoreCase) >= 0)
			?? spoolMapTemplates.FirstOrDefault(name =>
				name.IndexOf("Plan", StringComparison.OrdinalIgnoreCase) >= 0
				&& name.IndexOf("3D", StringComparison.OrdinalIgnoreCase) < 0)
			?? spoolMapTemplates.First();
	}

	private void LoadTitleBlocks(Document doc)
	{
		_cmbTitleBlock.Items.Clear();
		foreach (string item in (from FamilySymbol x in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
				where x.Category != null && x.Category.Id.Value == -2000280L
				orderby x.FamilyName, x.Name
				select $"{x.FamilyName} : {x.Name}").Distinct(StringComparer.OrdinalIgnoreCase))
		{
			_cmbTitleBlock.Items.Add(item);
		}
	}

	private static List<string> LoadViewTemplateNames(Document doc)
	{
		return (from View x in new FilteredElementCollector(doc).OfClass(typeof(View))
			where x.IsTemplate
			orderby x.Name
			select x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static void LoadTemplateCombo(ComboBox combo, IEnumerable<string> templateNames)
	{
		combo.Items.Clear();
		foreach (string templateName in templateNames)
		{
			combo.Items.Add(templateName);
		}
	}

	private void LoadAssemblyTagTypes(Document doc)
	{
		_cmbAssemblyTagType.Items.Clear();
		HashSet<long> seen = new HashSet<long>();
		List<FamilySymbol> symbols = new List<FamilySymbol>();
		foreach (FamilySymbol symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
		{
			if (!IsIncludedAssemblyTagType(symbol))
			{
				continue;
			}
			long key = symbol.Id.Value;
			if (key >= 0 && seen.Add(key))
			{
				symbols.Add(symbol);
			}
		}
		foreach (string item in (from x in symbols
				orderby x.FamilyName, x.Name
				select $"{x.FamilyName} : {x.Name}").Distinct(StringComparer.OrdinalIgnoreCase))
		{
			_cmbAssemblyTagType.Items.Add(item);
		}
	}

	private static bool IsIncludedAssemblyTagType(FamilySymbol symbol)
	{
		if (symbol == null)
		{
			return false;
		}
		if (CategoryMatchesAssemblyTags(symbol.Category))
		{
			return true;
		}
		Category familyCategory = symbol.Family?.FamilyCategory;
		return CategoryMatchesAssemblyTags(familyCategory);
	}

	private static bool CategoryMatchesAssemblyTags(Category category)
	{
		if (category == null)
		{
			return false;
		}
		try
		{
			if (category.Id.Value == (long)BuiltInCategory.OST_AssemblyTags)
			{
				return true;
			}
		}
		catch
		{
		}
		string name = category.Name ?? string.Empty;
		return name.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) >= 0
			&& name.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0;
	}
}
