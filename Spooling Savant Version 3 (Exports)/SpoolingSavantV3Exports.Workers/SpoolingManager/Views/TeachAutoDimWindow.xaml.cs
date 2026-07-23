using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class TeachAutoDimWindow : Window
{
	private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
	private static readonly Brush Paper = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
	private static readonly Brush Card = Brushes.White;

	private TextBlock txtStatus;
	private DataGrid gridDims;
	private readonly ObservableCollection<TeachDimRow> _rows = new ObservableCollection<TeachDimRow>();

	public TeachAutoDimWindow()
	{
		InitializeComponent();
		ForceHighContrast();
		gridDims.ItemsSource = _rows;
		TeachAutoDimSession.ReportReady -= OnReportReady;
		TeachAutoDimSession.ReportReady += OnReportReady;
	}

	/// <summary>
	/// Dialog chrome / pane theme must not win — always black-on-light for this window.
	/// </summary>
	private void ForceHighContrast()
	{
		// Drop merged Revit/SS dialog dictionaries that force black text onto dark wells
		// (or white text onto light cells). Local Window.Resources stay.
		try
		{
			Resources.MergedDictionaries.Clear();
		}
		catch
		{
		}
		Background = Paper;
		Foreground = Ink;
		if (Content is Border border)
		{
			border.Background = Paper;
		}
		if (txtStatus != null)
		{
			txtStatus.Foreground = Ink;
		}
		TextBlock hint = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtHint");
		if (hint != null)
		{
			hint.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
		}
		if (gridDims != null)
		{
			gridDims.Background = Card;
			gridDims.Foreground = Ink;
			gridDims.RowBackground = Card;
			gridDims.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
		}
	}

	private void OnReportReady(TeachAutoDimReport report)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.Invoke(() => OnReportReady(report));
			return;
		}
		ApplyReport(report);
	}

	public void ApplyReport(TeachAutoDimReport report)
	{
		if (report == null)
		{
			return;
		}
		ForceHighContrast();
		txtStatus.Text = (report.AssemblyName != null ? report.AssemblyName + " / " : string.Empty)
			+ (report.ViewName != null ? report.ViewName + " — " : string.Empty)
			+ (report.StatusMessage ?? string.Empty);
		txtStatus.Foreground = Ink;

		Dictionary<long, TeachDimRow> prior = new Dictionary<long, TeachDimRow>();
		foreach (TeachDimRow r in _rows)
		{
			prior[r.DimensionId] = r;
		}

		_rows.Clear();
		if (report.Dimensions == null)
		{
			return;
		}
		foreach (TeachAutoDimListItem item in report.Dimensions)
		{
			if (item == null)
			{
				continue;
			}
			var row = new TeachDimRow(item);
			if (prior.TryGetValue(item.DimensionId, out TeachDimRow old))
			{
				row.ContentCorrect = old.ContentCorrect;
				row.ContentIncorrect = old.ContentIncorrect;
				row.PlacementCorrect = old.PlacementCorrect;
				row.PlacementIncorrect = old.PlacementIncorrect;
			}
			_rows.Add(row);
		}
	}

	private void BtnRefresh_Click(object sender, RoutedEventArgs e)
	{
		TeachAutoDimSession.RequestRefresh();
	}

	private void BtnFinish_Click(object sender, RoutedEventArgs e)
	{
		List<long> contentCorrect = new List<long>();
		List<long> contentIncorrect = new List<long>();
		List<long> placementCorrect = new List<long>();
		List<long> placementIncorrect = new List<long>();
		Dictionary<string, string> reasons = new Dictionary<string, string>();

		foreach (TeachDimRow row in _rows)
		{
			if (row.ContentCorrect)
			{
				contentCorrect.Add(row.DimensionId);
			}
			if (row.ContentIncorrect)
			{
				contentIncorrect.Add(row.DimensionId);
				reasons[row.DimensionId.ToString()] = "Incorrect";
			}
			if (row.PlacementCorrect)
			{
				placementCorrect.Add(row.DimensionId);
			}
			if (row.PlacementIncorrect)
			{
				placementIncorrect.Add(row.DimensionId);
				if (!reasons.ContainsKey(row.DimensionId.ToString()))
				{
					reasons[row.DimensionId.ToString()] = "FarOffset";
				}
				else
				{
					reasons[row.DimensionId.ToString()] = "Incorrect+FarOffset";
				}
			}
		}

		if (contentCorrect.Count == 0 && contentIncorrect.Count == 0
			&& placementCorrect.Count == 0 && placementIncorrect.Count == 0)
		{
			SsSavantMessageBox.Show(
				this,
				"Mark at least one Content or Placement as Correct or Incorrect before Finish.",
				"Teach Auto-Dim",
				MessageBoxButton.OK,
				MessageBoxImage.Information);
			return;
		}

		TeachAutoDimSession.RequestFinish(contentCorrect, contentIncorrect, placementCorrect, placementIncorrect, reasons);
	}

	private void BtnClose_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	protected override void OnClosed(EventArgs e)
	{
		TeachAutoDimSession.ReportReady -= OnReportReady;
		base.OnClosed(e);
	}

	private void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.TeachAutoDimWindow.xaml");
		SpoolingManagerXamlLoader.ApplyWindow(this, source);
		txtStatus = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtStatus");
		gridDims = SpoolingManagerXamlLoader.Find<DataGrid>(this, "gridDims");
		SpoolingManagerXamlLoader.Find<Button>(this, "btnRefresh").Click += BtnRefresh_Click;
		SpoolingManagerXamlLoader.Find<Button>(this, "btnFinish").Click += BtnFinish_Click;
		SpoolingManagerXamlLoader.Find<Button>(this, "btnClose").Click += BtnClose_Click;
	}
}

/// <summary>Row with mutually exclusive Content OK/BAD and Place OK/BAD.</summary>
public sealed class TeachDimRow : INotifyPropertyChanged
{
	private bool _contentCorrect;
	private bool _contentIncorrect;
	private bool _placementCorrect;
	private bool _placementIncorrect;
	private bool _suppress;

	public TeachDimRow(TeachAutoDimListItem item)
	{
		DimensionId = item.DimensionId;
		DisplayLabel = item.DisplayLabel;
		Source = item;
	}

	public long DimensionId { get; }

	public string DisplayLabel { get; }

	public TeachAutoDimListItem Source { get; }

	public bool ContentCorrect
	{
		get => _contentCorrect;
		set
		{
			if (_suppress || _contentCorrect == value)
			{
				return;
			}
			_contentCorrect = value;
			if (value)
			{
				SetExclusive(ref _contentIncorrect, false, nameof(ContentIncorrect));
			}
			OnPropertyChanged();
		}
	}

	public bool ContentIncorrect
	{
		get => _contentIncorrect;
		set
		{
			if (_suppress || _contentIncorrect == value)
			{
				return;
			}
			_contentIncorrect = value;
			if (value)
			{
				SetExclusive(ref _contentCorrect, false, nameof(ContentCorrect));
			}
			OnPropertyChanged();
		}
	}

	public bool PlacementCorrect
	{
		get => _placementCorrect;
		set
		{
			if (_suppress || _placementCorrect == value)
			{
				return;
			}
			_placementCorrect = value;
			if (value)
			{
				SetExclusive(ref _placementIncorrect, false, nameof(PlacementIncorrect));
			}
			OnPropertyChanged();
		}
	}

	public bool PlacementIncorrect
	{
		get => _placementIncorrect;
		set
		{
			if (_suppress || _placementIncorrect == value)
			{
				return;
			}
			_placementIncorrect = value;
			if (value)
			{
				SetExclusive(ref _placementCorrect, false, nameof(PlacementCorrect));
			}
			OnPropertyChanged();
		}
	}

	private void SetExclusive(ref bool field, bool value, string name)
	{
		if (field == value)
		{
			return;
		}
		_suppress = true;
		field = value;
		_suppress = false;
		OnPropertyChanged(name);
	}

	public event PropertyChangedEventHandler PropertyChanged;

	private void OnPropertyChanged([CallerMemberName] string name = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
