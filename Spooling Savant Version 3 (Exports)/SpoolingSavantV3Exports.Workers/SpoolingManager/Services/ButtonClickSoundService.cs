using System;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

internal static class ButtonClickSoundService
{
	private static readonly object SyncRoot = new object();
	private static SoundPlayer _player;
	private static SoundPlayer _pageFlipPlayer;

	internal static void Attach(FrameworkElement root, Func<bool> isEnabled)
	{
		if (root == null)
		{
			return;
		}

		root.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
		{
			ButtonBase button = FindButton(e.OriginalSource as DependencyObject);
			if (button != null && button.IsEnabled && (isEnabled?.Invoke() ?? true))
			{
				Play();
			}
		};

		root.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
		{
			if ((e.Key == Key.Enter || e.Key == Key.Space)
				&& Keyboard.FocusedElement is ButtonBase button
				&& button.IsEnabled
				&& (isEnabled?.Invoke() ?? true))
			{
				Play();
			}
		};
	}

	private static ButtonBase FindButton(DependencyObject source)
	{
		for (DependencyObject current = source; current != null; current = GetParent(current))
		{
			if (current is ButtonBase button)
			{
				return button;
			}
		}

		return null;
	}

	private static DependencyObject GetParent(DependencyObject element)
	{
		if (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
		{
			return VisualTreeHelper.GetParent(element);
		}

		return LogicalTreeHelper.GetParent(element);
	}

	private static void Play()
	{
		try
		{
			lock (SyncRoot)
			{
				if (_player == null)
				{
					_player = new SoundPlayer(CreateClickWave());
					_player.Load();
				}

				_player.Play();
			}
		}
		catch
		{
			// Audio is optional and must never interfere with a command.
		}
	}

	/// <summary>Soft paper page-flip, used when switching tabs in the settings window.</summary>
	internal static void PlayPageFlip()
	{
		try
		{
			lock (SyncRoot)
			{
				if (_pageFlipPlayer == null)
				{
					_pageFlipPlayer = new SoundPlayer(CreatePageFlipWave());
					_pageFlipPlayer.Load();
				}

				_pageFlipPlayer.Play();
			}
		}
		catch
		{
			// Audio is optional and must never interfere with a command.
		}
	}

	private static Stream CreateClickWave()
	{
		const int sampleRate = 22050;
		const double durationSeconds = 0.026;
		int sampleCount = (int)(sampleRate * durationSeconds);
		MemoryStream stream = new MemoryStream();

		using (BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + sampleCount * 2);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((short)1);
			writer.Write((short)1);
			writer.Write(sampleRate);
			writer.Write(sampleRate * 2);
			writer.Write((short)2);
			writer.Write((short)16);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
			writer.Write(sampleCount * 2);

			// A short, dry plastic mouse-button snap: filtered noise supplies the
			// mechanical tick while a tiny low body keeps it from sounding metallic.
			uint noiseState = 0x6D2B79F5u;
			double filteredNoise = 0.0;
			for (int i = 0; i < sampleCount; i++)
			{
				double time = (double)i / sampleRate;
				noiseState = noiseState * 1664525u + 1013904223u;
				double noise = ((noiseState >> 8) / 8388607.5) - 1.0;
				filteredNoise += 0.42 * (noise - filteredNoise);
				double snap = filteredNoise * Math.Exp(-time * 420.0);
				double body = Math.Sin(2.0 * Math.PI * 520.0 * time) * Math.Exp(-time * 240.0);
				double sample = 0.28 * snap + 0.07 * body;
				writer.Write((short)(short.MaxValue * sample));
			}
		}

		stream.Position = 0;
		return stream;
	}

	private static Stream CreatePageFlipWave()
	{
		const int sampleRate = 22050;
		const double durationSeconds = 0.13;
		int sampleCount = (int)(sampleRate * durationSeconds);
		MemoryStream stream = new MemoryStream();

		using (BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + sampleCount * 2);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((short)1);
			writer.Write((short)1);
			writer.Write(sampleRate);
			writer.Write(sampleRate * 2);
			writer.Write((short)2);
			writer.Write((short)16);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
			writer.Write(sampleCount * 2);

			// A single book page turning: a quiet, breathy "fft". Soft band-limited
			// noise, gentle hump envelope, and a barely-there settle as the page lands.
			// Deliberately low gain — this should sit under the UI, not announce itself.
			uint noiseState = 0x9E3779B9u;
			double lowPass = 0.0;
			double slowLowPass = 0.0;
			double smoothed = 0.0;
			for (int i = 0; i < sampleCount; i++)
			{
				double time = (double)i / sampleRate;
				double progress = time / durationSeconds;
				noiseState = noiseState * 1664525u + 1013904223u;
				double noise = ((noiseState >> 8) / 8388607.5) - 1.0;

				// Soft mid-band paper noise: remove both the deep rumble and the
				// hissy top end so it reads as paper brushing, not static.
				lowPass += 0.30 * (noise - lowPass);
				slowLowPass += 0.05 * (noise - slowLowPass);
				double paperBand = lowPass - slowLowPass;
				// Extra smoothing pass takes the remaining edge off.
				smoothed += 0.5 * (paperBand - smoothed);

				// One gentle hump peaking a third of the way in — the page lifting
				// and passing — then a faint bump near the end as it settles flat.
				double turn = Math.Exp(-Math.Pow((progress - 0.32) / 0.22, 2.0));
				double settle = 0.35 * Math.Exp(-Math.Pow((progress - 0.85) / 0.07, 2.0));

				double sample = 0.055 * smoothed * (turn + settle);
				writer.Write((short)(short.MaxValue * sample));
			}
		}

		stream.Position = 0;
		return stream;
	}
}
