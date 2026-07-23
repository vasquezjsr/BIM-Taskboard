using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Autodesk.Revit.UI;

namespace SpoolingSavantV3Exports.Workers.UI
{
	/// <summary>
	/// Neon chrome is a dark-mode flourish. In Revit light mode the same rounded
	/// chromeless dialogs stay, but without the glowing blue border / neon lettering —
	/// they use a plain Revit-like light panel instead.
	/// </summary>
	internal static class SsSavantNeonChrome
	{
		internal static readonly Color DarkBorderColor = (Color)ColorConverter.ConvertFromString("#FF4DA3FF");
		internal static readonly Color DarkBgTop = (Color)ColorConverter.ConvertFromString("#FF131730");
		internal static readonly Color DarkBgBottom = (Color)ColorConverter.ConvertFromString("#FF06080F");

		internal static readonly Color LightBorderColor = (Color)ColorConverter.ConvertFromString("#FFC8C8C8");
		internal static readonly Color LightBgTop = (Color)ColorConverter.ConvertFromString("#FFF5F5F5");
		internal static readonly Color LightBgBottom = (Color)ColorConverter.ConvertFromString("#FFE8E8E8");

		internal static readonly Color NeonTitleColor = (Color)ColorConverter.ConvertFromString("#FFFFF4C8");
		internal static readonly Color NeonTitleGlow = (Color)ColorConverter.ConvertFromString("#FFFFC24D");
		internal static readonly Color NeonModeColor = (Color)ColorConverter.ConvertFromString("#FFA9F4FF");
		internal static readonly Color NeonModeGlow = (Color)ColorConverter.ConvertFromString("#FF35D6F8");

		/// <summary>True when Revit's UI theme is Dark — neon effects are allowed.</summary>
		internal static bool IsNeonEnabled
		{
			get
			{
				try
				{
					return UIThemeManager.CurrentTheme != UITheme.Light;
				}
				catch
				{
					return true;
				}
			}
		}

		internal static Brush CreateShellBackground(bool neon)
		{
			if (neon)
			{
				return new LinearGradientBrush(DarkBgTop, DarkBgBottom, 90.0);
			}
			return new LinearGradientBrush(LightBgTop, LightBgBottom, 90.0);
		}

		internal static Brush CreateShellBorderBrush(bool neon)
		{
			return new SolidColorBrush(neon ? DarkBorderColor : LightBorderColor);
		}

		/// <summary>
		/// Applies rounded chromeless shell fill + border to a dialog body border.
		/// Keeps the same layout; only the neon colors change with Revit theme.
		/// </summary>
		internal static void ApplyShell(Border shell, bool? neonOverride = null)
		{
			if (shell == null)
			{
				return;
			}

			bool neon = neonOverride ?? IsNeonEnabled;
			shell.Background = CreateShellBackground(neon);
			shell.BorderBrush = CreateShellBorderBrush(neon);
			if (shell.BorderThickness.Left < 1)
			{
				shell.BorderThickness = new Thickness(2);
			}
			if (shell.CornerRadius.TopLeft < 1)
			{
				shell.CornerRadius = new CornerRadius(12);
			}

			// Soft outer glow only in dark neon mode.
			if (neon)
			{
				shell.Effect = new DropShadowEffect
				{
					Color = DarkBorderColor,
					BlurRadius = 14,
					ShadowDepth = 0,
					Opacity = 0.45
				};
			}
			else
			{
				shell.Effect = null;
			}
		}

		/// <summary>
		/// Makes a window chromeless (no title bar) like the progress dialog, applies
		/// neon/light shell to its root Border, and enables drag-by-body.
		/// </summary>
		internal static void ApplyChromelessDialog(Window window, bool allowResize = false)
		{
			if (window == null)
			{
				return;
			}

			window.WindowStyle = WindowStyle.None;
			window.AllowsTransparency = true;
			window.Background = Brushes.Transparent;
			window.ShowInTaskbar = false;
			window.ResizeMode = allowResize ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;

			if (window.Content is Border shell)
			{
				ApplyShell(shell);
			}

			SsSavantDialogForeground.Attach(window);

			window.MouseLeftButtonDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e)
			{
				if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
				{
					try
					{
						window.DragMove();
					}
					catch
					{
					}
				}
			};
		}

		/// <summary>
		/// Neon title styling for dialog headers (Spooling Savant, Schedule On Sheet, etc.).
		/// Dark mode: warm neon glow. Light mode: plain primary foreground, no glow.
		/// </summary>
		internal static void ApplyNeonDialogTitle(TextBlock title, bool useScriptFont = true)
		{
			if (title == null)
			{
				return;
			}

			bool neon = IsNeonEnabled;
			if (useScriptFont)
			{
				title.FontFamily = new FontFamily("Segoe Script, Brush Script MT, Monotype Corsiva");
				title.FontWeight = FontWeights.Bold;
			}

			if (neon)
			{
				title.Foreground = new SolidColorBrush(NeonTitleColor);
				title.Effect = new DropShadowEffect
				{
					Color = NeonTitleGlow,
					BlurRadius = 18,
					ShadowDepth = 0,
					Opacity = 0.95
				};
				title.Opacity = 1.0;
			}
			else
			{
				PaneColorPalette palette = RevitThemePalette.ForCurrentRevitTheme();
				title.Foreground = BrushFromHex(palette.ForegroundPrimary);
				title.Effect = null;
				title.Opacity = 1.0;
			}
		}

		/// <summary>
		/// Outer rounded pane frame: dialog fill + blue neon outline in dark mode,
		/// plain Revit chrome in light mode.
		/// </summary>
		internal static void ApplyPaneOuterShell(Border shell, bool neon, bool lit)
		{
			if (shell == null)
			{
				return;
			}

			if (neon)
			{
				shell.Background = CreateShellBackground(true);
				shell.BorderThickness = new Thickness(2);
				shell.CornerRadius = new CornerRadius(14);
				shell.BorderBrush = new SolidColorBrush(lit ? DarkBorderColor : (Color)ColorConverter.ConvertFromString("#FF223250"));
				DropShadowEffect glow = shell.Effect as DropShadowEffect;
				if (glow == null || glow.Color != DarkBorderColor)
				{
					glow = new DropShadowEffect
					{
						Color = DarkBorderColor,
						BlurRadius = 14,
						ShadowDepth = 0
					};
					shell.Effect = glow;
				}
				glow.Opacity = lit ? 0.45 : 0.0;
			}
			else
			{
				PaneColorPalette palette = RevitThemePalette.ForCurrentRevitTheme();
				shell.Background = BrushFromHex(palette.ChromeBackground);
				shell.BorderThickness = new Thickness(1);
				shell.CornerRadius = new CornerRadius(14);
				shell.BorderBrush = BrushFromHex(palette.BorderOuter);
				shell.Effect = null;
			}
		}

		/// <summary>
		/// Header titles only — no boxed neon border around Spooling Savant / Fabrication.
		/// Text still warms up with the open strike; the pane's outer shell carries the blue outline.
		/// </summary>
		internal static void ApplyPaneSignChrome(
			Border border,
			TextBlock titleMain,
			TextBlock titleMode,
			bool neon,
			bool lit)
		{
			if (border != null)
			{
				// Header sits flush on the pane fill — no separate neon box.
				border.Background = Brushes.Transparent;
				border.BorderBrush = Brushes.Transparent;
				border.BorderThickness = new Thickness(0);
				border.CornerRadius = new CornerRadius(0);
				border.Effect = null;
			}

			if (titleMain != null)
			{
				if (neon)
				{
					titleMain.Foreground = new SolidColorBrush(NeonTitleColor);
					DropShadowEffect glow = titleMain.Effect as DropShadowEffect;
					if (glow == null)
					{
						glow = new DropShadowEffect
						{
							Color = NeonTitleGlow,
							BlurRadius = 18,
							ShadowDepth = 0
						};
						titleMain.Effect = glow;
					}
					glow.Opacity = lit ? 0.95 : 0.0;
					titleMain.Opacity = lit ? 1.0 : 0.22;
				}
				else
				{
					PaneColorPalette palette = RevitThemePalette.ForCurrentRevitTheme();
					titleMain.Foreground = BrushFromHex(palette.ForegroundPrimary);
					titleMain.Effect = null;
					titleMain.Opacity = 1.0;
				}
			}

			if (titleMode != null)
			{
				if (neon)
				{
					titleMode.Foreground = new SolidColorBrush(NeonModeColor);
					DropShadowEffect glow = titleMode.Effect as DropShadowEffect;
					if (glow == null)
					{
						glow = new DropShadowEffect
						{
							Color = NeonModeGlow,
							BlurRadius = 14,
							ShadowDepth = 0
						};
						titleMode.Effect = glow;
					}
					glow.Opacity = lit ? 0.95 : 0.0;
					titleMode.Opacity = lit ? 1.0 : 0.22;
				}
				else
				{
					PaneColorPalette palette = RevitThemePalette.ForCurrentRevitTheme();
					titleMode.Foreground = BrushFromHex(palette.ForegroundMuted);
					titleMode.Effect = null;
					titleMode.Opacity = 1.0;
				}
			}
		}

		internal static Brush NeonLetterLitBrush(bool neon)
		{
			return neon
				? FrozenSolid(NeonTitleColor)
				: FrozenSolid((Color)ColorConverter.ConvertFromString("#FF1E1E1E"));
		}

		internal static Brush NeonLetterUnlitBrush(bool neon)
		{
			return neon
				? FrozenSolid((Color)ColorConverter.ConvertFromString("#FF2E3350"))
				: FrozenSolid((Color)ColorConverter.ConvertFromString("#FFC8C8C8"));
		}

		internal static Effect NeonLetterLitEffect(bool neon)
		{
			if (!neon)
			{
				return null;
			}
			return new DropShadowEffect
			{
				Color = NeonTitleGlow,
				BlurRadius = 16,
				ShadowDepth = 0,
				Opacity = 0.95
			};
		}

		private static SolidColorBrush BrushFromHex(string hex)
		{
			string value = hex ?? "#F0F0F0";
			if (!value.StartsWith("#", StringComparison.Ordinal))
			{
				value = "#" + value;
			}
			if (value.Length == 7)
			{
				value = "#FF" + value.Substring(1);
			}
			return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
		}

		private static SolidColorBrush FrozenSolid(Color color)
		{
			SolidColorBrush brush = new SolidColorBrush(color);
			if (brush.CanFreeze)
			{
				brush.Freeze();
			}
			return brush;
		}
	}
}
