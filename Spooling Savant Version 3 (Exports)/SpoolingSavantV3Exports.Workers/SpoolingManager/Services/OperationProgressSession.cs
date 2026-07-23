using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Views;
using SpoolingSavantV3Exports.Workers.UI;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Modeless progress UI for long SS Manager operations. Uses <see cref="Window.Show"/> (never
/// ShowDialog) so Revit / external-event handlers are not wedged. Stays in front via Owner + Topmost.
/// </summary>
internal static class OperationProgressSession
{
	private static OperationProgressWindow _window;
	private static WeakReference _ownerWindow;
	private static volatile bool _cancelRequested;
	private static volatile bool _pauseRequested;

	public static bool IsVisible => _window != null && _window.IsLoaded && _window.IsVisible;

	/// <summary>
	/// True once the user clicked Cancel on the progress window. Long operations poll this
	/// between work items (the dispatcher pump in <see cref="Report"/> lets the click through).
	/// </summary>
	public static bool CancelRequested => _cancelRequested;

	/// <summary>True while the user has the progress window paused.</summary>
	public static bool PauseRequested => _pauseRequested;

	/// <summary>
	/// Blocks the calling (Revit UI) thread while paused, pumping the dispatcher so the
	/// progress window stays responsive — that is what lets Resume and Cancel clicks
	/// arrive while we wait. Returns when unpaused or when Cancel is clicked.
	/// </summary>
	public static void WaitWhilePaused()
	{
		while (_pauseRequested && !_cancelRequested)
		{
			DispatcherFramePump();
			System.Threading.Thread.Sleep(60);
		}
	}

	public static void Show(string title, Window owner = null, IntPtr ownerHwnd = default,
		bool allowCancel = false, string neonProgressText = null)
	{
		InvokeUi(() =>
		{
			CloseCore();
			_cancelRequested = false;
			_pauseRequested = false;
			_ownerWindow = owner != null ? new WeakReference(owner) : null;
			_window = new OperationProgressWindow(title);
			_window.Topmost = true;
			_window.ShowInTaskbar = false;
			SsSavantDialogForeground.Attach(_window);
			if (!string.IsNullOrWhiteSpace(neonProgressText))
			{
				_window.EnableNeonLetterProgress(neonProgressText);
			}
			if (allowCancel)
			{
				_window.EnableCancelButton();
				_window.EnablePauseButton();
				_window.CancelRequested += () => _cancelRequested = true;
				_window.PauseChanged += paused => _pauseRequested = paused;
			}

			if (owner != null)
			{
				_window.Owner = owner;
				_window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
			}
			else if (ownerHwnd != IntPtr.Zero)
			{
				try
				{
					new WindowInteropHelper(_window).Owner = ownerHwnd;
					_window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
				}
				catch
				{
					_window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
				}
			}
			else
			{
				_window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
			}

			_window.SetProgress(0.0, "Starting…", string.Empty);
			_window.Show();
			BringToFrontCore();
		});
	}

	public static void Report(double percent, string status, string detail = null)
	{
		InvokeUi(() =>
		{
			if (_window == null)
			{
				return;
			}
			_window.SetProgress(percent, status, detail);
			BringToFrontCore();
			DispatcherFramePump();
		});
	}

	public static void Close()
	{
		InvokeUi(CloseCore);
	}

	/// <summary>
	/// Shows a completed summary in the same chrome as the progress window (modeless — never ShowDialog).
	/// First line of <paramref name="message"/> is the bold status; remaining lines are muted detail.
	/// </summary>
	public static void ShowCompleted(string title, string message, Window owner = null, IntPtr ownerHwnd = default)
	{
		SplitMessage(message, out string status, out string detail);
		InvokeUi(() =>
		{
			if (_window == null || !_window.IsLoaded)
			{
				Show(title, owner, ownerHwnd);
			}
			else if (!string.IsNullOrWhiteSpace(title))
			{
				_window.Title = title;
			}

			if (_window == null)
			{
				return;
			}

			_window.SetCompleted(
				string.IsNullOrWhiteSpace(status) ? "Done" : status,
				detail);
			BringToFrontCore();
			DispatcherFramePump();
		});
	}

	private static void SplitMessage(string message, out string status, out string detail)
	{
		status = string.Empty;
		detail = string.Empty;
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		string text = message.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
		int newline = text.IndexOf('\n');
		if (newline < 0)
		{
			status = text;
			return;
		}

		status = text.Substring(0, newline).TrimEnd();
		detail = text.Substring(newline + 1).Trim();
	}

	private static void CloseCore()
	{
		if (_window == null)
		{
			return;
		}

		OperationProgressWindow window = _window;
		_window = null;
		_ownerWindow = null;
		try
		{
			window.Topmost = false;
			window.Close();
		}
		catch
		{
		}
	}

	private static void BringToFrontCore()
	{
		if (_window == null)
		{
			return;
		}

		try
		{
			_window.Topmost = true;
			if (!_window.IsVisible)
			{
				_window.Show();
			}
			_window.Activate();
			Window owner = TryGetOwner();
			if (owner != null && !ReferenceEquals(_window.Owner, owner))
			{
				_window.Owner = owner;
			}
		}
		catch
		{
		}
	}

	private static Window TryGetOwner()
	{
		if (_ownerWindow != null && _ownerWindow.IsAlive)
		{
			return _ownerWindow.Target as Window;
		}
		return null;
	}

	private static void InvokeUi(Action action)
	{
		if (action == null)
		{
			return;
		}

		Dispatcher dispatcher = null;
		try
		{
			if (_window != null)
			{
				dispatcher = _window.Dispatcher;
			}
			else if (Application.Current != null)
			{
				dispatcher = Application.Current.Dispatcher;
			}
		}
		catch
		{
			dispatcher = null;
		}

		if (dispatcher == null || dispatcher.CheckAccess())
		{
			action();
			return;
		}

		try
		{
			dispatcher.Invoke(DispatcherPriority.Send, action);
		}
		catch
		{
			try
			{
				action();
			}
			catch
			{
			}
		}
	}

	/// <summary>Let the progress window paint while Revit work continues on the same UI thread.</summary>
	private static void DispatcherFramePump()
	{
		try
		{
			Dispatcher dispatcher = _window?.Dispatcher ?? Application.Current?.Dispatcher;
			if (dispatcher == null)
			{
				return;
			}
			dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
		}
		catch
		{
		}
	}
}
