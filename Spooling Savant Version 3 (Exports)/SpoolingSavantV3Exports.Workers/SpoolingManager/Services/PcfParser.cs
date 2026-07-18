using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>Parsed ISOGEN-style PCF document (Spooling Savant export format).</summary>
internal sealed class PcfDocument
{
	internal string PipelineReference { get; set; } = string.Empty;
	internal string PipingSpec { get; set; } = string.Empty;
	internal string UnitsCoords { get; set; } = "Feet";
	internal string UnitsBore { get; set; } = "Inch";
	internal List<PcfComponent> Components { get; } = new List<PcfComponent>();
}

internal sealed class PcfComponent
{
	internal string Type { get; set; } = string.Empty;
	internal string ComponentIdentifier { get; set; } = string.Empty;
	internal string ItemCode { get; set; } = string.Empty;
	/// <summary>Revit Family name (e.g. No604-2 - Fitting Adapter (FtgxM)) — preferred for palette matching.</summary>
	internal string Family { get; set; } = string.Empty;
	internal string Description { get; set; } = string.Empty;
	internal string Skey { get; set; } = string.Empty;
	internal string PipingSpec { get; set; } = string.Empty;
	internal string SpoolId { get; set; } = string.Empty;
	internal string SizeText { get; set; } = string.Empty;
	internal double NominalSizeInches { get; set; }
	internal List<PcfEndPoint> EndPoints { get; } = new List<PcfEndPoint>();
	/// <summary>ISOGEN CENTRE-POINT — bend/intersection keypoint (elbows, tees).</summary>
	internal XYZ CentrePoint { get; set; }

	internal bool IsStraightPipe =>
		string.Equals(Type, "PIPE", StringComparison.OrdinalIgnoreCase);

	internal bool IsElbow =>
		string.Equals(Type, "ELBOW", StringComparison.OrdinalIgnoreCase);

	internal bool IsTee =>
		string.Equals(Type, "TEE", StringComparison.OrdinalIgnoreCase);

	internal bool IsFlange =>
		string.Equals(Type, "FLANGE", StringComparison.OrdinalIgnoreCase);

	internal bool IsOlet =>
		string.Equals(Type, "OLET", StringComparison.OrdinalIgnoreCase)
		|| (!string.IsNullOrWhiteSpace(Skey) && Skey.IndexOf("OLET", StringComparison.OrdinalIgnoreCase) >= 0);

	internal bool IsCap =>
		string.Equals(Type, "CAP", StringComparison.OrdinalIgnoreCase)
		|| (!string.IsNullOrWhiteSpace(Skey) && Skey.IndexOf("CAP", StringComparison.OrdinalIgnoreCase) >= 0);

	internal bool IsCoupling =>
		string.Equals(Type, "COUPLING", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(Type, "UNION", StringComparison.OrdinalIgnoreCase)
		|| (!string.IsNullOrWhiteSpace(Skey) && Skey.IndexOf("COUPLING", StringComparison.OrdinalIgnoreCase) >= 0)
		|| (!string.IsNullOrWhiteSpace(Skey) && Skey.IndexOf("UNION", StringComparison.OrdinalIgnoreCase) >= 0);

	internal bool IsAdapter =>
		string.Equals(Type, "ADAPTER", StringComparison.OrdinalIgnoreCase)
		|| (!string.IsNullOrWhiteSpace(Skey) && Skey.IndexOf("ADAPTER", StringComparison.OrdinalIgnoreCase) >= 0);

	internal bool IsReducer =>
		Type.StartsWith("REDUCER", StringComparison.OrdinalIgnoreCase)
		|| (!string.IsNullOrWhiteSpace(Skey) && Skey.IndexOf("REDUCER", StringComparison.OrdinalIgnoreCase) >= 0);

	internal bool IsInlineFitting =>
		IsCoupling || IsAdapter || IsReducer
		|| string.Equals(Type, "MISC-COMPONENT", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(Type, "VALVE", StringComparison.OrdinalIgnoreCase);

	internal bool IsWeld =>
		string.Equals(Type, "WELD", StringComparison.OrdinalIgnoreCase)
		|| (!string.IsNullOrWhiteSpace(Skey) && Skey.IndexOf("WELD", StringComparison.OrdinalIgnoreCase) >= 0
			&& Skey.IndexOf("OLET", StringComparison.OrdinalIgnoreCase) < 0
			&& Skey.IndexOf("NECK", StringComparison.OrdinalIgnoreCase) < 0);

	internal bool TryGetSegment(out XYZ start, out XYZ end, out double boreInches)
	{
		start = null;
		end = null;
		boreInches = 0;
		if (EndPoints.Count < 2)
		{
			return false;
		}

		start = EndPoints[0].Point;
		end = EndPoints[1].Point;
		boreInches = Math.Max(EndPoints[0].BoreInches, EndPoints[1].BoreInches);
		if (boreInches <= 1e-6)
		{
			boreInches = NominalSizeInches;
		}

		if (start == null || end == null)
		{
			return false;
		}

		return start.DistanceTo(end) > 1e-6;
	}
}

internal sealed class PcfEndPoint
{
	internal XYZ Point { get; set; }
	internal double BoreInches { get; set; }
}

/// <summary>Reads Spooling Savant / ISOGEN-style PCF text into structured components.</summary>
internal static class PcfParser
{
	private static readonly HashSet<string> ComponentHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"PIPE",
		"ELBOW",
		"TEE",
		"REDUCER-CONCENTRIC",
		"REDUCER-ECCENTRIC",
		"FLANGE",
		"VALVE",
		"OLET",
		"MISC-COMPONENT",
		"CAP",
		"WELD",
		"COUPLING",
		"ADAPTER",
		"SUPPORT"
	};

	internal static PcfDocument ParseFile(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			throw new FileNotFoundException("PCF file not found.", path);
		}

		return Parse(File.ReadAllLines(path));
	}

	internal static PcfDocument Parse(IEnumerable<string> lines)
	{
		var doc = new PcfDocument();
		PcfComponent current = null;

		foreach (string raw in lines ?? Array.Empty<string>())
		{
			string line = raw?.TrimEnd() ?? string.Empty;
			if (line.Length == 0)
			{
				continue;
			}

			string trimmed = line.Trim();
			if (trimmed.StartsWith("UNITS-CO-ORDS", StringComparison.OrdinalIgnoreCase))
			{
				doc.UnitsCoords = ReadValue(trimmed, "UNITS-CO-ORDS");
				continue;
			}

			if (trimmed.StartsWith("UNITS-BORE", StringComparison.OrdinalIgnoreCase))
			{
				doc.UnitsBore = ReadValue(trimmed, "UNITS-BORE");
				continue;
			}

			if (trimmed.StartsWith("PIPELINE-REFERENCE", StringComparison.OrdinalIgnoreCase))
			{
				doc.PipelineReference = ReadValue(trimmed, "PIPELINE-REFERENCE");
				continue;
			}

			if (IsComponentHeader(trimmed))
			{
				current = new PcfComponent { Type = trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0] };
				doc.Components.Add(current);
				continue;
			}

			if (current == null)
			{
				if (trimmed.StartsWith("PIPING-SPEC", StringComparison.OrdinalIgnoreCase))
				{
					doc.PipingSpec = ReadValue(trimmed, "PIPING-SPEC");
				}

				continue;
			}

			if (trimmed.StartsWith("COMPONENT-IDENTIFIER", StringComparison.OrdinalIgnoreCase))
			{
				current.ComponentIdentifier = ReadValue(trimmed, "COMPONENT-IDENTIFIER");
			}
			else if (trimmed.StartsWith("ITEM-CODE", StringComparison.OrdinalIgnoreCase))
			{
				current.ItemCode = ReadValue(trimmed, "ITEM-CODE");
			}
			else if (trimmed.StartsWith("FAMILY", StringComparison.OrdinalIgnoreCase)
				&& !trimmed.StartsWith("FAMILY-", StringComparison.OrdinalIgnoreCase))
			{
				current.Family = ReadValue(trimmed, "FAMILY");
			}
			else if (trimmed.StartsWith("DESCRIPTION", StringComparison.OrdinalIgnoreCase))
			{
				current.Description = ReadValue(trimmed, "DESCRIPTION");
			}
			else if (trimmed.StartsWith("SKEY", StringComparison.OrdinalIgnoreCase))
			{
				current.Skey = ReadValue(trimmed, "SKEY");
			}
			else if (trimmed.StartsWith("PIPING-SPEC", StringComparison.OrdinalIgnoreCase))
			{
				current.PipingSpec = ReadValue(trimmed, "PIPING-SPEC");
			}
			else if (trimmed.StartsWith("SPOOL-ID", StringComparison.OrdinalIgnoreCase))
			{
				current.SpoolId = ReadValue(trimmed, "SPOOL-ID");
			}
			else if (trimmed.StartsWith("NOMINAL-SIZE", StringComparison.OrdinalIgnoreCase)
				|| trimmed.StartsWith("PIPE-DIAMETER", StringComparison.OrdinalIgnoreCase)
				|| trimmed.StartsWith("PRODUCT-ENTRY", StringComparison.OrdinalIgnoreCase)
				|| trimmed.StartsWith("SIZE ", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), "SIZE", StringComparison.OrdinalIgnoreCase))
			{
				string key = trimmed.StartsWith("NOMINAL-SIZE", StringComparison.OrdinalIgnoreCase) ? "NOMINAL-SIZE"
					: trimmed.StartsWith("PIPE-DIAMETER", StringComparison.OrdinalIgnoreCase) ? "PIPE-DIAMETER"
					: trimmed.StartsWith("PRODUCT-ENTRY", StringComparison.OrdinalIgnoreCase) ? "PRODUCT-ENTRY"
					: "SIZE";
				string sizeText = ReadValue(trimmed, key);
				if (!string.IsNullOrWhiteSpace(sizeText))
				{
					current.SizeText = sizeText.Trim();
					double parsed = ParseSizeInches(sizeText);
					if (parsed > current.NominalSizeInches)
					{
						current.NominalSizeInches = parsed;
					}
				}
			}
			else if (trimmed.StartsWith("END-POINT", StringComparison.OrdinalIgnoreCase))
			{
				PcfEndPoint endPoint = ParseEndPoint(trimmed, doc.UnitsCoords, doc.UnitsBore);
				if (endPoint != null)
				{
					current.EndPoints.Add(endPoint);
					if (endPoint.BoreInches > current.NominalSizeInches)
					{
						current.NominalSizeInches = endPoint.BoreInches;
					}
				}
			}
			else if (trimmed.StartsWith("CENTRE-POINT", StringComparison.OrdinalIgnoreCase)
				|| trimmed.StartsWith("CENTER-POINT", StringComparison.OrdinalIgnoreCase))
			{
				XYZ centre = ParseCentrePoint(trimmed, doc.UnitsCoords);
				if (centre != null)
				{
					current.CentrePoint = centre;
				}
			}
		}

		return doc;
	}

	internal static double ParseSizeInches(string sizeText)
	{
		if (string.IsNullOrWhiteSpace(sizeText))
		{
			return 0.0;
		}

		string text = sizeText.Trim();

		// Strip trailing inch marks: 4", 4'', 4″, 4ø, and unit words.
		text = System.Text.RegularExpressions.Regex.Replace(
			text,
			@"[""'\u2032\u2033\u00F8\u2300]+",
			" ");
		text = System.Text.RegularExpressions.Regex.Replace(
			text,
			@"\s*(in|inch|inches)\b.*$",
			string.Empty,
			System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

		// Keep the leading NPS token only (drop "x 3/4", "Sch 40", etc.).
		System.Text.RegularExpressions.Match leading = System.Text.RegularExpressions.Regex.Match(
			text,
			@"^\s*(\d+\s+\d+/\d+|\d+-\d+/\d+|\d+/\d+|\d*\.\d+|\d+)");
		if (!leading.Success)
		{
			return 0.0;
		}

		return ParseSizeToken(leading.Groups[1].Value.Trim());
	}

	private static double ParseSizeToken(string token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return 0.0;
		}

		// "1 1/2" or "1-1/2"
		string[] bits = token.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
		if (bits.Length == 2
			&& double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double whole)
			&& bits[1].Contains('/'))
		{
			string[] frac = bits[1].Split('/');
			if (frac.Length == 2
				&& double.TryParse(frac[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
				&& double.TryParse(frac[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
				&& Math.Abs(den) > 1e-9)
			{
				return whole + (num / den);
			}
		}

		if (token.Contains('/'))
		{
			string[] frac = token.Split('/');
			if (frac.Length == 2
				&& double.TryParse(frac[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
				&& double.TryParse(frac[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
				&& Math.Abs(den) > 1e-9)
			{
				return num / den;
			}
		}

		if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
		{
			return value;
		}

		return 0.0;
	}

	private static bool IsComponentHeader(string trimmed)
	{
		if (string.IsNullOrWhiteSpace(trimmed) || char.IsWhiteSpace(trimmed[0]))
		{
			return false;
		}

		string token = trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0];
		return ComponentHeaders.Contains(token);
	}

	private static string ReadValue(string line, string key)
	{
		int idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
		if (idx < 0)
		{
			return string.Empty;
		}

		string rest = line.Substring(idx + key.Length).Trim();
		return rest;
	}

	private static PcfEndPoint ParseEndPoint(string line, string unitsCoords, string unitsBore)
	{
		string rest = ReadValue(line, "END-POINT");
		string[] parts = rest.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 3)
		{
			return null;
		}

		if (!TryParseDouble(parts[0], out double x)
			|| !TryParseDouble(parts[1], out double y)
			|| !TryParseDouble(parts[2], out double z))
		{
			return null;
		}

		double bore = 0;
		if (parts.Length >= 4)
		{
			TryParseDouble(parts[3], out bore);
		}

		// Spooling Savant export uses feet for coords and inches for bore.
		if (!string.IsNullOrWhiteSpace(unitsCoords)
			&& unitsCoords.IndexOf("MM", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			const double mmToFeet = 1.0 / 304.8;
			x *= mmToFeet;
			y *= mmToFeet;
			z *= mmToFeet;
		}
		else if (!string.IsNullOrWhiteSpace(unitsCoords)
			&& unitsCoords.IndexOf("INCH", StringComparison.OrdinalIgnoreCase) >= 0
			&& unitsCoords.IndexOf("FEET", StringComparison.OrdinalIgnoreCase) < 0)
		{
			x /= 12.0;
			y /= 12.0;
			z /= 12.0;
		}

		if (!string.IsNullOrWhiteSpace(unitsBore)
			&& unitsBore.IndexOf("MM", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			bore /= 25.4;
		}
		else if (!string.IsNullOrWhiteSpace(unitsBore)
			&& unitsBore.IndexOf("FEET", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			bore *= 12.0;
		}

		return new PcfEndPoint
		{
			Point = new XYZ(x, y, z),
			BoreInches = bore
		};
	}

	private static XYZ ParseCentrePoint(string line, string unitsCoords)
	{
		string key = line.StartsWith("CENTER-POINT", StringComparison.OrdinalIgnoreCase)
			? "CENTER-POINT"
			: "CENTRE-POINT";
		string rest = ReadValue(line, key);
		string[] parts = rest.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 3)
		{
			return null;
		}

		if (!TryParseDouble(parts[0], out double x)
			|| !TryParseDouble(parts[1], out double y)
			|| !TryParseDouble(parts[2], out double z))
		{
			return null;
		}

		if (!string.IsNullOrWhiteSpace(unitsCoords)
			&& unitsCoords.IndexOf("MM", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			const double mmToFeet = 1.0 / 304.8;
			x *= mmToFeet;
			y *= mmToFeet;
			z *= mmToFeet;
		}
		else if (!string.IsNullOrWhiteSpace(unitsCoords)
			&& unitsCoords.IndexOf("INCH", StringComparison.OrdinalIgnoreCase) >= 0
			&& unitsCoords.IndexOf("FEET", StringComparison.OrdinalIgnoreCase) < 0)
		{
			x /= 12.0;
			y /= 12.0;
			z /= 12.0;
		}

		return new XYZ(x, y, z);
	}

	private static bool TryParseDouble(string text, out double value)
	{
		return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}
}
