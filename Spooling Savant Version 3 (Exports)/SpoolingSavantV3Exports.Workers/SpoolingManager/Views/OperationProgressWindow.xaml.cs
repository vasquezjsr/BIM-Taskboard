using System;
using System.Windows;
using System.Windows.Controls;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class OperationProgressWindow : Window
{
	internal TextBlock txtStatus;

	internal TextBlock txtDetail;

	internal ScrollViewer detailScroll;

	internal ProgressBar barProgress;

	internal TextBlock txtPercent;

	internal Button btnClose;

	public OperationProgressWindow(string title)
	{
		InitializeComponent();
		base.Title = (string.IsNullOrWhiteSpace(title) ? "SS Manager V3" : title);
	}

	public void SetProgress(double percent, string status, string detail)
	{
		if (percent < 0.0)
		{
			percent = 0.0;
		}
		if (percent > 100.0)
		{
			percent = 100.0;
		}

		ShowProgressChrome();
		barProgress.Value = percent;
		txtPercent.Text = ((int)percent).ToString() + "%";
		if (!string.IsNullOrWhiteSpace(status))
		{
			txtStatus.Text = status;
		}
		txtDetail.Text = detail ?? string.Empty;
	}

	public void SetCompleted(string status, string detail)
	{
		if (!string.IsNullOrWhiteSpace(status))
		{
			txtStatus.Text = status;
		}
		txtDetail.Text = detail ?? string.Empty;
		barProgress.Value = 100.0;
		barProgress.Visibility = Visibility.Collapsed;
		txtPercent.Visibility = Visibility.Collapsed;
		if (btnClose != null)
		{
			btnClose.Visibility = Visibility.Visible;
			btnClose.Focus();
		}

		SizeForCompletedMessage(detail);
	}

	private void SizeForCompletedMessage(string detail)
	{
		Width = Math.Max(Width, 520);
		MinWidth = 420;
		MaxHeight = 640;
		ResizeMode = ResizeMode.CanResizeWithGrip;

		if (string.IsNullOrWhiteSpace(detail))
		{
			Height = 180;
			MinHeight = 160;
			return;
		}

		int lines = 1;
		foreach (char c in detail)
		{
			if (c == '\n')
			{
				lines++;
			}
		}
		// Long wrapped lines still need vertical room — pad by estimated wrap.
		int estimatedWrapped = Math.Max(lines, (int)Math.Ceiling(detail.Length / 70.0));
		double detailHeight = Math.Min(estimatedWrapped * 18.0, 420.0);
		double target = 72 + detailHeight + 56; // status + detail + Close padding
		Height = Math.Max(220, Math.Min(target, 600));
		MinHeight = 200;
	}

	private void ShowProgressChrome()
	{
		ResizeMode = ResizeMode.NoResize;
		Height = 170;
		MinHeight = 150;
		if (barProgress != null)
		{
			barProgress.Visibility = Visibility.Visible;
		}
		if (txtPercent != null)
		{
			txtPercent.Visibility = Visibility.Visible;
		}
		if (btnClose != null)
		{
			btnClose.Visibility = Visibility.Collapsed;
		}
	}

	private void BtnClose_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	public void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.OperationProgressWindow.xaml");
		SpoolingManagerXamlLoader.ApplyWindow(this, source);
		txtStatus = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtStatus");
		txtDetail = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtDetail");
		detailScroll = SpoolingManagerXamlLoader.Find<ScrollViewer>(this, "detailScroll");
		barProgress = SpoolingManagerXamlLoader.Find<ProgressBar>(this, "barProgress");
		txtPercent = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtPercent");
		btnClose = SpoolingManagerXamlLoader.Find<Button>(this, "btnClose");
		if (btnClose != null)
		{
			btnClose.Click += BtnClose_Click;
		}
		base.Topmost = true;
		base.ShowInTaskbar = false;
		base.ResizeMode = ResizeMode.NoResize;
	}
}
