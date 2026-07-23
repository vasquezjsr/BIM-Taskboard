using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.UI;

namespace SpoolingSavantV3Exports.Workers.UI
{
	/// <summary>
	/// Keeps Spooling Savant dialogs above Revit when the user activates Revit
	/// (taskbar click, Alt-Tab, etc.). Owned windows alone are not enough for
	/// chromeless dialogs hosted from a dockable pane.
	/// </summary>
	internal static class SsSavantDialogForeground
	{
		private static readonly List<WeakReference<Window>> OpenWindows = new List<WeakReference<Window>>();
		private static readonly object Sync = new object();

		private static IntPtr _hook;
		private static WinEventDelegate _hookProc;
		private static int _revitProcessId;
		private static Dispatcher _uiDispatcher;

		private const uint EventSystemForeground = 0x0003;
		private const uint WineventOutOfContext = 0x0000;

		private delegate void WinEventDelegate(
			IntPtr hWinEventHook,
			uint eventType,
			IntPtr hwnd,
			int idObject,
			int idChild,
			uint dwEventThread,
			uint dwmsEventTime);

		[DllImport("user32.dll")]
		private static extern IntPtr SetWinEventHook(
			uint eventMin,
			uint eventMax,
			IntPtr hmodWinEventProc,
			WinEventDelegate lpfnWinEventProc,
			uint idProcess,
			uint idThread,
			uint dwFlags);

		[DllImport("user32.dll")]
		private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(
			IntPtr hWnd,
			IntPtr hWndInsertAfter,
			int x,
			int y,
			int cx,
			int cy,
			uint uFlags);

		private static readonly IntPtr HwndTop = new IntPtr(0);
		private const uint SwpNoMove = 0x0002;
		private const uint SwpNoSize = 0x0001;
		private const uint SwpShowWindow = 0x0040;
		private const uint SwpNoActivate = 0x0010;

		/// <summary>
		/// Parents the dialog to Revit's main window and registers it so it is
		/// pulled forward whenever Revit becomes the foreground app.
		/// Call before <c>Show</c> / <c>ShowDialog</c>.
		/// </summary>
		internal static void Attach(Window window, UIApplication uiapp = null)
		{
			if (window == null)
			{
				return;
			}

			_uiDispatcher = window.Dispatcher ?? Dispatcher.CurrentDispatcher;
			EnsureRevitProcessId();
			EnsureHook();

			IntPtr revitHwnd = ResolveRevitHwnd(uiapp);
			if (revitHwnd != IntPtr.Zero)
			{
				try
				{
					new WindowInteropHelper(window).Owner = revitHwnd;
				}
				catch
				{
				}
			}

			window.ShowInTaskbar = false;
			Register(window);
			window.Closed -= OnWindowClosed;
			window.Closed += OnWindowClosed;
		}

		/// <summary>Same as <see cref="Attach"/>, used from chrome helpers that lack UIApplication.</summary>
		internal static void Attach(Window window)
		{
			Attach(window, null);
		}

		private static void OnWindowClosed(object sender, EventArgs e)
		{
			if (sender is Window window)
			{
				window.Closed -= OnWindowClosed;
				Unregister(window);
			}
		}

		private static void Register(Window window)
		{
			lock (Sync)
			{
				PruneDeadRefs();
				foreach (WeakReference<Window> existing in OpenWindows)
				{
					if (existing.TryGetTarget(out Window live) && ReferenceEquals(live, window))
					{
						return;
					}
				}
				OpenWindows.Add(new WeakReference<Window>(window));
			}
		}

		private static void Unregister(Window window)
		{
			lock (Sync)
			{
				OpenWindows.RemoveAll(wr =>
					!wr.TryGetTarget(out Window live) || ReferenceEquals(live, window));
			}
		}

		private static void PruneDeadRefs()
		{
			OpenWindows.RemoveAll(wr => !wr.TryGetTarget(out _));
		}

		private static void EnsureRevitProcessId()
		{
			if (_revitProcessId != 0)
			{
				return;
			}
			try
			{
				_revitProcessId = Process.GetCurrentProcess().Id;
			}
			catch
			{
				_revitProcessId = 0;
			}
		}

		private static void EnsureHook()
		{
			if (_hook != IntPtr.Zero)
			{
				return;
			}

			_hookProc = OnWinEvent;
			_hook = SetWinEventHook(
				EventSystemForeground,
				EventSystemForeground,
				IntPtr.Zero,
				_hookProc,
				0,
				0,
				WineventOutOfContext);
		}

		private static void OnWinEvent(
			IntPtr hWinEventHook,
			uint eventType,
			IntPtr hwnd,
			int idObject,
			int idChild,
			uint dwEventThread,
			uint dwmsEventTime)
		{
			if (eventType != EventSystemForeground || hwnd == IntPtr.Zero)
			{
				return;
			}

			GetWindowThreadProcessId(hwnd, out uint processId);
			if (_revitProcessId == 0 || processId != (uint)_revitProcessId)
			{
				return;
			}

			Dispatcher dispatcher = _uiDispatcher;
			if (dispatcher == null)
			{
				return;
			}

			try
			{
				dispatcher.BeginInvoke(new Action(BringOpenDialogsForward), DispatcherPriority.Normal);
			}
			catch
			{
			}
		}

		private static void BringOpenDialogsForward()
		{
			List<Window> windows;
			lock (Sync)
			{
				PruneDeadRefs();
				windows = new List<Window>(OpenWindows.Count);
				foreach (WeakReference<Window> wr in OpenWindows)
				{
					if (wr.TryGetTarget(out Window window)
						&& window.IsLoaded
						&& window.IsVisible)
					{
						windows.Add(window);
					}
				}
			}

			foreach (Window window in windows)
			{
				try
				{
					IntPtr hwnd = new WindowInteropHelper(window).Handle;
					if (hwnd != IntPtr.Zero)
					{
						// Raise in Z-order without stealing keyboard focus from Revit.
						SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
					}
					if (window.WindowState == WindowState.Minimized)
					{
						window.WindowState = WindowState.Normal;
					}
					window.Show();
				}
				catch
				{
				}
			}
		}

		private static IntPtr ResolveRevitHwnd(UIApplication uiapp)
		{
			try
			{
				if (uiapp != null)
				{
					IntPtr handle = uiapp.MainWindowHandle;
					if (handle != IntPtr.Zero)
					{
						return handle;
					}
				}
			}
			catch
			{
			}

			try
			{
				using (Process process = Process.GetCurrentProcess())
				{
					process.Refresh();
					return process.MainWindowHandle;
				}
			}
			catch
			{
				return IntPtr.Zero;
			}
		}
	}
}
