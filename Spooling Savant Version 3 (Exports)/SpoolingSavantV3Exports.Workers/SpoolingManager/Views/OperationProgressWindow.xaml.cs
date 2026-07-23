using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Views;

public class OperationProgressWindow : Window
{
	internal TextBlock txtStatus;

	internal TextBlock txtDetail;

	internal ScrollViewer detailScroll;

	internal ProgressBar barProgress;

	internal StackPanel pnlNeonLetters;

	internal TextBlock txtPercent;

	internal TextBlock txtCompletedSummary;

	internal Button btnClose;

	internal Button btnCancelOperation;

	internal Button btnPauseOperation;

	private bool _isPaused;

	private DateTime _pauseBeganUtc = DateTime.MinValue;

	private DateTime _progressStartedUtc = DateTime.MinValue;

	private double _smoothedRemainingSeconds = -1.0;

	private bool _neonLetterMode;

	private bool _neonEffectsEnabled = true;

	private TextBlock[] _neonLetterBlocks = Array.Empty<TextBlock>();

	private int _litNeonLetters = -1;

	private Brush _letterLitBrush;

	private Brush _letterUnlitBrush;

	private static readonly FontFamily NeonFontFamily = new FontFamily("Segoe Script, Brush Script MT, Monotype Corsiva");

	/// <summary>Raised once when the user clicks Cancel. The operation polls
	/// <see cref="OperationProgressSession.CancelRequested"/> between work items.</summary>
	public event Action CancelRequested;

	/// <summary>Raised when the user toggles Pause/Resume. True while paused.</summary>
	public event Action<bool> PauseChanged;

	public OperationProgressWindow(string title)
	{
		InitializeComponent();
		base.Title = (string.IsNullOrWhiteSpace(title) ? "Spooling Savant" : title);
	}

	/// <summary>
	/// Replaces the plain progress bar with neon sign letters (e.g. "Sheets Completed")
	/// that light up left to right as the percentage climbs.
	/// </summary>
	public void EnableNeonLetterProgress(string words)
	{
		if (pnlNeonLetters == null || string.IsNullOrWhiteSpace(words))
		{
			return;
		}

		pnlNeonLetters.Children.Clear();
		_neonEffectsEnabled = SsSavantNeonChrome.IsNeonEnabled;
		_letterLitBrush = SsSavantNeonChrome.NeonLetterLitBrush(_neonEffectsEnabled);
		_letterUnlitBrush = SsSavantNeonChrome.NeonLetterUnlitBrush(_neonEffectsEnabled);
		var blocks = new System.Collections.Generic.List<TextBlock>(words.Length);
		foreach (char c in words)
		{
			if (char.IsWhiteSpace(c))
			{
				pnlNeonLetters.Children.Add(new Border { Width = 10 });
				continue;
			}
			TextBlock letter = new TextBlock
			{
				Text = c.ToString(),
				FontFamily = NeonFontFamily,
				FontSize = 24,
				FontWeight = FontWeights.Bold,
				Foreground = _letterUnlitBrush,
				Margin = new Thickness(1, 0, 1, 0)
			};
			pnlNeonLetters.Children.Add(letter);
			blocks.Add(letter);
		}
		_neonLetterBlocks = blocks.ToArray();
		_litNeonLetters = -1;
		_neonLetterMode = true;
		pnlNeonLetters.Visibility = Visibility.Visible;
		if (barProgress != null)
		{
			barProgress.Visibility = Visibility.Collapsed;
		}
		UpdateNeonLetters(0.0);
	}

	/// <summary>Shows the Cancel button for operations that actually honor it.</summary>
	public void EnableCancelButton()
	{
		if (btnCancelOperation != null)
		{
			btnCancelOperation.Visibility = Visibility.Visible;
		}
	}

	/// <summary>Shows the Pause button for operations that poll the pause flag.</summary>
	public void EnablePauseButton()
	{
		if (btnPauseOperation != null)
		{
			btnPauseOperation.Visibility = Visibility.Visible;
		}
	}

	private void UpdateNeonLetters(double percent)
	{
		if (!_neonLetterMode || _neonLetterBlocks.Length == 0)
		{
			return;
		}

		int lit = (int)Math.Round(percent / 100.0 * _neonLetterBlocks.Length);
		if (percent > 0.0 && lit == 0)
		{
			lit = 1; // show life as soon as work starts
		}
		if (lit == _litNeonLetters)
		{
			return;
		}
		_litNeonLetters = lit;

		for (int i = 0; i < _neonLetterBlocks.Length; i++)
		{
			TextBlock letter = _neonLetterBlocks[i];
			if (i < lit)
			{
				if (!ReferenceEquals(letter.Foreground, _letterLitBrush))
				{
					letter.Foreground = _letterLitBrush;
					letter.Effect = SsSavantNeonChrome.NeonLetterLitEffect(_neonEffectsEnabled);
				}
			}
			else if (!ReferenceEquals(letter.Foreground, _letterUnlitBrush))
			{
				letter.Foreground = _letterUnlitBrush;
				letter.Effect = null;
			}
		}
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
		UpdateNeonLetters(percent);
		txtPercent.Text = ((int)percent).ToString() + "%" + BuildRemainingSuffix(percent);
		if (!string.IsNullOrWhiteSpace(status))
		{
			txtStatus.Text = status;
		}
		txtDetail.Text = detail ?? string.Empty;
	}

	private string BuildRemainingSuffix(double percent)
	{
		DateTime now = DateTime.UtcNow;
		if (_progressStartedUtc == DateTime.MinValue)
		{
			_progressStartedUtc = now;
		}
		double elapsedSeconds = (now - _progressStartedUtc).TotalSeconds;

		// Too early (or already done) for a meaningful estimate.
		if (percent < 3.0 || percent >= 99.5 || elapsedSeconds < 2.0)
		{
			return string.Empty;
		}

		double rawRemaining = elapsedSeconds * (100.0 - percent) / percent;
		// Blend with the previous estimate so the number doesn't jump around.
		_smoothedRemainingSeconds = (_smoothedRemainingSeconds < 0.0)
			? rawRemaining
			: 0.6 * _smoothedRemainingSeconds + 0.4 * rawRemaining;

		return "  •  about " + FormatDuration(_smoothedRemainingSeconds) + " left";
	}

	private static string FormatDuration(double seconds)
	{
		if (seconds < 5.0)
		{
			return "a few seconds";
		}
		if (seconds < 60.0)
		{
			// Round up to the nearest 5 seconds so it counts down cleanly.
			int rounded = (int)(Math.Ceiling(seconds / 5.0) * 5.0);
			return rounded + " sec";
		}
		if (seconds < 3600.0)
		{
			int minutes = (int)(seconds / 60.0);
			int remainder = (int)(seconds % 60.0);
			if (minutes >= 10 || remainder < 15)
			{
				return minutes + " min";
			}
			return minutes + " min " + ((int)(Math.Ceiling(remainder / 15.0) * 15.0)) + " sec";
		}
		int hours = (int)(seconds / 3600.0);
		int leftoverMinutes = (int)((seconds % 3600.0) / 60.0);
		return hours + " hr " + leftoverMinutes + " min";
	}

	public void SetCompleted(string status, string detail)
	{
		// Completed layout: neon sign (or nothing) centered in the box, and the
		// one-line summary sits in the bottom row next to the Close button.
		bool hasDetail = !string.IsNullOrWhiteSpace(detail);
		if (txtStatus != null)
		{
			// Hidden (not Collapsed) keeps the top row's layout space, so the
			// centered sign stays exactly where it sat during progress.
			txtStatus.Visibility = hasDetail ? Visibility.Collapsed : Visibility.Hidden;
		}
		if (txtCompletedSummary != null)
		{
			txtCompletedSummary.Text = status ?? string.Empty;
			txtCompletedSummary.Visibility = Visibility.Visible;
		}
		txtDetail.Text = detail ?? string.Empty;
		if (detailScroll != null)
		{
			detailScroll.Visibility = hasDetail ? Visibility.Visible : Visibility.Hidden;
		}
		barProgress.Value = 100.0;
		barProgress.Visibility = Visibility.Collapsed;
		if (_neonLetterMode)
		{
			UpdateNeonLetters(100.0); // fully lit sign on the summary
		}
		txtPercent.Visibility = Visibility.Collapsed;
		if (btnCancelOperation != null)
		{
			btnCancelOperation.Visibility = Visibility.Collapsed;
		}
		if (btnPauseOperation != null)
		{
			btnPauseOperation.Visibility = Visibility.Collapsed;
		}
		if (btnClose != null)
		{
			btnClose.Visibility = Visibility.Visible;
			btnClose.Focus();
		}

		// Only resize when there is extra detail to fit; otherwise keep the exact
		// progress-window size so nothing moves between 100% and the summary.
		if (hasDetail)
		{
			SizeForCompletedMessage(detail);
		}
	}

	private void SizeForCompletedMessage(string detail)
	{
		Width = Math.Max(Width, 520);
		MinWidth = 420;
		MaxHeight = 640;

		double signHeight = _neonLetterMode ? 120.0 : 60.0; // centered sign + breathing room

		if (string.IsNullOrWhiteSpace(detail))
		{
			Height = 96 + signHeight; // padding + bottom row + sign
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
		double detailHeight = Math.Min(estimatedWrapped * 18.0, 320.0);
		double target = detailHeight + signHeight + 96;
		Height = Math.Max(240, Math.Min(target, 600));
		MinHeight = 200;
	}

	private void ShowProgressChrome()
	{
		ResizeMode = ResizeMode.NoResize;
		Height = _neonLetterMode ? 220 : 170;
		MinHeight = 150;
		if (txtStatus != null)
		{
			txtStatus.Visibility = Visibility.Visible;
		}
		if (detailScroll != null)
		{
			detailScroll.Visibility = Visibility.Visible;
		}
		if (txtCompletedSummary != null)
		{
			txtCompletedSummary.Visibility = Visibility.Collapsed;
		}
		if (barProgress != null)
		{
			barProgress.Visibility = _neonLetterMode ? Visibility.Collapsed : Visibility.Visible;
		}
		if (pnlNeonLetters != null)
		{
			pnlNeonLetters.Visibility = _neonLetterMode ? Visibility.Visible : Visibility.Collapsed;
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

	private void BtnCancelOperation_Click(object sender, RoutedEventArgs e)
	{
		if (btnCancelOperation != null)
		{
			btnCancelOperation.IsEnabled = false;
			btnCancelOperation.Content = "Cancelling…";
		}
		if (btnPauseOperation != null)
		{
			btnPauseOperation.IsEnabled = false;
		}
		if (txtStatus != null)
		{
			txtStatus.Text = "Cancelling…";
		}
		CancelRequested?.Invoke();
	}

	private void BtnPauseOperation_Click(object sender, RoutedEventArgs e)
	{
		_isPaused = !_isPaused;
		if (btnPauseOperation != null)
		{
			btnPauseOperation.Content = _isPaused ? "Resume" : "Pause";
		}
		if (_isPaused)
		{
			_pauseBeganUtc = DateTime.UtcNow;
			if (txtStatus != null)
			{
				txtStatus.Text = "Paused";
			}
		}
		else
		{
			// Shift the ETA clock forward by the paused span so the
			// time-remaining estimate ignores the time spent paused.
			if (_pauseBeganUtc != DateTime.MinValue && _progressStartedUtc != DateTime.MinValue)
			{
				_progressStartedUtc += DateTime.UtcNow - _pauseBeganUtc;
			}
			_pauseBeganUtc = DateTime.MinValue;
			if (txtStatus != null)
			{
				txtStatus.Text = "Resuming…";
			}
		}
		PauseChanged?.Invoke(_isPaused);
	}

	public void InitializeComponent()
	{
		Window source = SpoolingManagerXamlLoader.LoadWindow("SpoolingManager.Views.OperationProgressWindow.xaml");
		// No title bar: chromeless rounded sign. Must be set before the window is shown.
		base.WindowStyle = WindowStyle.None;
		base.AllowsTransparency = true;
		SpoolingManagerXamlLoader.ApplyWindow(this, source);
		base.Background = Brushes.Transparent;
		if (Content is Border shell)
		{
			SsSavantNeonChrome.ApplyShell(shell);
		}
		_neonEffectsEnabled = SsSavantNeonChrome.IsNeonEnabled;
		_letterLitBrush = SsSavantNeonChrome.NeonLetterLitBrush(_neonEffectsEnabled);
		_letterUnlitBrush = SsSavantNeonChrome.NeonLetterUnlitBrush(_neonEffectsEnabled);
		txtStatus = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtStatus");
		txtDetail = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtDetail");
		detailScroll = SpoolingManagerXamlLoader.Find<ScrollViewer>(this, "detailScroll");
		barProgress = SpoolingManagerXamlLoader.Find<ProgressBar>(this, "barProgress");
		pnlNeonLetters = SpoolingManagerXamlLoader.Find<StackPanel>(this, "pnlNeonLetters");
		txtPercent = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtPercent");
		txtCompletedSummary = SpoolingManagerXamlLoader.Find<TextBlock>(this, "txtCompletedSummary");
		btnClose = SpoolingManagerXamlLoader.Find<Button>(this, "btnClose");
		btnCancelOperation = SpoolingManagerXamlLoader.Find<Button>(this, "btnCancelOperation");
		btnPauseOperation = SpoolingManagerXamlLoader.Find<Button>(this, "btnPauseOperation");
		if (btnClose != null)
		{
			btnClose.Click += BtnClose_Click;
		}
		if (btnCancelOperation != null)
		{
			btnCancelOperation.Click += BtnCancelOperation_Click;
		}
		if (btnPauseOperation != null)
		{
			btnPauseOperation.Click += BtnPauseOperation_Click;
		}
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
		base.Topmost = true;
		base.ShowInTaskbar = false;
		base.ResizeMode = ResizeMode.NoResize;
	}
}
