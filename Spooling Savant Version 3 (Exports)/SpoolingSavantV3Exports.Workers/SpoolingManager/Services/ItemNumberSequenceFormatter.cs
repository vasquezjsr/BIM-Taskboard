using System;
using System.Globalization;
using System.Text;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Formats item-number series tokens from a starting value:
/// 1 → 1,2,3…; 01 → 01,02,03…; A → A,B,C… (Excel-style after Z).
/// </summary>
internal static class ItemNumberSequenceFormatter
{
	public static string Format(string startToken, int zeroBasedIndex)
	{
		if (zeroBasedIndex < 0)
		{
			zeroBasedIndex = 0;
		}

		string start = (startToken ?? string.Empty).Trim();
		if (start.Length == 0)
		{
			start = "1";
		}

		if (IsAllDigits(start))
		{
			int width = start.Length;
			if (!long.TryParse(start, NumberStyles.Integer, CultureInfo.InvariantCulture, out long startValue)
				|| startValue < 1)
			{
				startValue = 1;
			}

			long value = startValue + zeroBasedIndex;
			if (value < 1)
			{
				value = 1;
			}

			string raw = value.ToString(CultureInfo.InvariantCulture);
			if (raw.Length >= width)
			{
				return raw;
			}

			return raw.PadLeft(width, '0');
		}

		if (IsAllLetters(start))
		{
			bool upper = char.IsUpper(start[0]);
			long startIndex = LettersToIndex(start);
			return IndexToLetters(startIndex + zeroBasedIndex, upper);
		}

		// Mixed / unknown: fall back to plain 1-based numeric with no padding.
		return (1 + zeroBasedIndex).ToString(CultureInfo.InvariantCulture);
	}

	public static string NormalizeStartToken(string value, string fallback = "1")
	{
		string text = (value ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return fallback;
		}

		if (IsAllDigits(text) || IsAllLetters(text))
		{
			return text;
		}

		return fallback;
	}

	private static bool IsAllDigits(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}

		for (int i = 0; i < value.Length; i++)
		{
			if (!char.IsDigit(value[i]))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsAllLetters(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}

		for (int i = 0; i < value.Length; i++)
		{
			if (!char.IsLetter(value[i]))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>A=1 … Z=26, AA=27 (1-based).</summary>
	private static long LettersToIndex(string letters)
	{
		long result = 0;
		foreach (char c in letters)
		{
			int digit = char.ToUpperInvariant(c) - 'A' + 1;
			if (digit < 1 || digit > 26)
			{
				return 1;
			}

			result = result * 26 + digit;
		}

		return result < 1 ? 1 : result;
	}

	private static string IndexToLetters(long index, bool upper)
	{
		if (index < 1)
		{
			index = 1;
		}

		StringBuilder sb = new StringBuilder();
		long n = index;
		while (n > 0)
		{
			n--;
			int rem = (int)(n % 26);
			char c = (char)((upper ? 'A' : 'a') + rem);
			sb.Insert(0, c);
			n /= 26;
		}

		return sb.ToString();
	}
}
