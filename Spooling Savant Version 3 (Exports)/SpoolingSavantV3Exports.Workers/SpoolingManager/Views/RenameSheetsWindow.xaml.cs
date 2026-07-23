using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.ViewModels;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class RenameSheetsWindow : Window
{
	private class SequenceSeed
	{
		public string Prefix { get; set; }

		public int Number { get; set; }

		public int Width { get; set; }

		public SequenceSeed Next()
		{
			return new SequenceSeed
			{
				Prefix = Prefix,
				Number = Number + 1,
				Width = Width
			};
		}

		public string Format()
		{
			return Prefix + Number.ToString("D" + Math.Max(Width, 1));
		}
	}

	private static readonly Regex TrailingNumberRegex = new Regex("^(.*?)(\\d+)$", RegexOptions.Compiled);

	private readonly ObservableCollection<RenameSheetRow> _rows;

	internal DataGrid gridRename;

	public SpoolingManagerKind ProductKind { get; set; }

	public RenameSheetsRequest RenameRequest { get; private set; }

	public RenameSheetsWindow(IEnumerable<RenameSheetRow> rows)
	{
		InitializeComponent();
		_rows = new ObservableCollection<RenameSheetRow>((rows ?? Enumerable.Empty<RenameSheetRow>()).ToList());
		gridRename.ItemsSource = _rows;
	}

	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}

	private void BtnRename_Click(object sender, RoutedEventArgs e)
	{
		List<RenameSheetRow> list = BuildNormalizedRows();
		if (list != null && list.Count != 0)
		{
			RenameRequest = new RenameSheetsRequest
			{
				ProductKind = ProductKind,
				Items = list.Select((RenameSheetRow x) => new RenameSheetItem
				{
					AssemblyId = x.AssemblyId,
					CurrentName = (x.CurrentName ?? string.Empty),
					NewName = (x.NewName ?? string.Empty)
				}).ToList()
			};
			base.DialogResult = true;
			Close();
		}
	}

	private List<RenameSheetRow> BuildNormalizedRows()
	{
		List<RenameSheetRow> list = _rows.Select((RenameSheetRow x) => new RenameSheetRow
		{
			AssemblyId = x.AssemblyId,
			CurrentName = x.CurrentName,
			NewName = (x.NewName ?? string.Empty).Trim()
		}).ToList();
		if (!list.Any((RenameSheetRow x) => !string.IsNullOrWhiteSpace(x.NewName)))
		{
			SsSavantMessageBox.Show("Enter at least one starting name in the New Name column.", "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return null;
		}
		SequenceSeed sequenceSeed = null;
		for (int num = 0; num < list.Count; num++)
		{
			RenameSheetRow renameSheetRow = list[num];
			if (!string.IsNullOrWhiteSpace(renameSheetRow.NewName))
			{
				sequenceSeed = TryCreateSeed(renameSheetRow.NewName);
				if (sequenceSeed == null)
				{
					SsSavantMessageBox.Show("Each filled New Name must end with a number so it can auto-sequence.\n\nExample: CHWR-02", "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Exclamation);
					return null;
				}
				continue;
			}
			if (sequenceSeed == null)
			{
				SsSavantMessageBox.Show("Blank rows before the first filled New Name cannot be auto-sequenced. Enter a starting name for the first group.", "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return null;
			}
			sequenceSeed = sequenceSeed.Next();
			renameSheetRow.NewName = sequenceSeed.Format();
		}
		HashSet<string> hashSet = new HashSet<string>(from g in list.GroupBy((RenameSheetRow x) => x.NewName, StringComparer.OrdinalIgnoreCase)
			where g.Count() > 1
			select g.Key, StringComparer.OrdinalIgnoreCase);
		if (hashSet.Count > 0)
		{
			SsSavantMessageBox.Show("The generated names contain duplicates.\n\n" + string.Join("\n", hashSet.OrderBy((string x) => x)), "Rename Sheets", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return null;
		}
		return list;
	}

	private static SequenceSeed TryCreateSeed(string value)
	{
		Match match = TrailingNumberRegex.Match((value ?? string.Empty).Trim());
		if (!match.Success)
		{
			return null;
		}
		if (!int.TryParse(match.Groups[2].Value, out var result))
		{
			return null;
		}
		return new SequenceSeed
		{
			Prefix = match.Groups[1].Value,
			Number = result,
			Width = match.Groups[2].Value.Length
		};
	}

	public void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.RenameSheetsWindow.xaml");
		SpoolingManagerXamlLoader.ApplyWindow(this, source);
		gridRename = SpoolingManagerXamlLoader.Find<DataGrid>(this, "gridRename");
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Cancel").Click += BtnCancel_Click;
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Rename").Click += BtnRename_Click;
	}
}
