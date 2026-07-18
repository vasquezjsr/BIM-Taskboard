using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class DimensionInspectorWindow : Window
{
	private TextBlock txtStatus;
	private Image imgView;
	private TextBlock txtNoImage;
	private TextBox txtDetails;

	public DimensionInspectorWindow()
	{
		InitializeComponent();
		DimensionInspectorSession.ReportReady -= OnReportReady;
		DimensionInspectorSession.ReportReady += OnReportReady;
	}

	private void OnReportReady(DimensionInspectorReport report)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.Invoke(() => OnReportReady(report));
			return;
		}
		ApplyReport(report);
	}

	public void ApplyReport(DimensionInspectorReport report)
	{
		if (report == null)
		{
			return;
		}
		txtStatus.Text = (report.ViewName != null ? report.ViewName + " — " : string.Empty) + (report.StatusMessage ?? string.Empty);
		txtDetails.Text = report.DetailText ?? string.Empty;
		if (!string.IsNullOrWhiteSpace(report.ViewImagePath) && File.Exists(report.ViewImagePath))
		{
			try
			{
				BitmapImage bitmap = new BitmapImage();
				bitmap.BeginInit();
				bitmap.CacheOption = BitmapCacheOption.OnLoad;
				bitmap.UriSource = new Uri(report.ViewImagePath, UriKind.Absolute);
				bitmap.EndInit();
				bitmap.Freeze();
				imgView.Source = bitmap;
				txtNoImage.Visibility = Visibility.Collapsed;
				imgView.Visibility = Visibility.Visible;
				return;
			}
			catch
			{
			}
		}
		imgView.Source = null;
		imgView.Visibility = Visibility.Collapsed;
		txtNoImage.Visibility = Visibility.Visible;
	}

	private void BtnRefresh_Click(object sender, RoutedEventArgs e)
	{
		DimensionInspectorSession.RequestRefresh(exportViewImage: true);
	}

	private void BtnClose_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	protected override void OnClosed(EventArgs e)
	{
		DimensionInspectorSession.ReportReady -= OnReportReady;
		base.OnClosed(e);
	}

	private void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.DimensionInspectorWindow.xaml");
		SpoolingManagerXamlLoader.ApplyWindow(this, source);
		txtStatus = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtStatus");
		imgView = SpoolingManagerXamlLoader.Find<Image>(this, "imgView");
		txtNoImage = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtNoImage");
		txtDetails = SpoolingManagerXamlLoader.Find<TextBox>(this, "txtDetails");
		SpoolingManagerXamlLoader.Find<Button>(this, "btnRefresh").Click += BtnRefresh_Click;
		SpoolingManagerXamlLoader.FindButtonByContent(this, "Close").Click += BtnClose_Click;
	}
}
