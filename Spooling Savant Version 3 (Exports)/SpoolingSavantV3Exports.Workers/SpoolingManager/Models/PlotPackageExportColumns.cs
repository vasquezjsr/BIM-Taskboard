using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

/// <summary>Selectable TigerStop CSV / PCF export fields for Plot Packages settings.</summary>
public static class PlotPackageExportColumns
{
	public const string TigerStopQuantity = "Quantity";
	public const string TigerStopLengthInches = "LengthInches";
	public const string TigerStopPackage = "Package";
	public const string TigerStopItemNumber = "ItemNumber";
	public const string TigerStopSize = "Size";
	public const string TigerStopLengthFtIn = "LengthFtIn";
	public const string TigerStopMaterial = "Material";
	public const string TigerStopSpool = "Spool";

	public const string PcfItemCode = "ITEM-CODE";
	public const string PcfSkey = "SKEY";
	public const string PcfPipingSpec = "PIPING-SPEC";
	public const string PcfSpoolId = "SPOOL-ID";
	public const string PcfEndPoint = "END-POINT";
	public const string PcfComponentId = "COMPONENT-IDENTIFIER";

	public static readonly string[] TigerStopAll =
	{
		TigerStopQuantity,
		TigerStopLengthInches,
		TigerStopPackage,
		TigerStopItemNumber,
		TigerStopSize,
		TigerStopLengthFtIn,
		TigerStopMaterial,
		TigerStopSpool
	};

	public static readonly string[] TigerStopDefault =
	{
		TigerStopQuantity,
		TigerStopLengthInches,
		TigerStopPackage,
		TigerStopItemNumber,
		TigerStopSize,
		TigerStopLengthFtIn,
		TigerStopMaterial,
		TigerStopSpool
	};

	public static readonly string[] PcfAll =
	{
		PcfComponentId,
		PcfItemCode,
		PcfSkey,
		PcfPipingSpec,
		PcfSpoolId,
		PcfEndPoint
	};

	public static readonly string[] PcfDefault =
	{
		PcfComponentId,
		PcfItemCode,
		PcfSkey,
		PcfPipingSpec,
		PcfSpoolId,
		PcfEndPoint
	};

	public static string DefaultTigerStopColumnsCsv => string.Join(",", TigerStopDefault);

	public static string DefaultPcfFieldsCsv => string.Join(",", PcfDefault);

	public static List<string> ParseTigerStopColumns(string raw)
	{
		return Parse(raw, TigerStopAll, TigerStopDefault, NormalizeTigerStopName);
	}

	public static List<string> ParsePcfFields(string raw)
	{
		return Parse(raw, PcfAll, PcfDefault, name => name.Trim().ToUpperInvariant());
	}

	private static List<string> Parse(
		string raw,
		string[] allowed,
		string[] defaults,
		Func<string, string> normalize)
	{
		HashSet<string> allowedSet = new HashSet<string>(allowed.Select(normalize), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> canonical = allowed.ToDictionary(normalize, x => x, StringComparer.OrdinalIgnoreCase);

		List<string> parsed = (raw ?? string.Empty)
			.Split(new[] { ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(token => normalize(token))
			.Where(token => token.Length > 0 && allowedSet.Contains(token))
			.Select(token => canonical[token])
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		return parsed.Count > 0 ? parsed : defaults.ToList();
	}

	private static string NormalizeTigerStopName(string name)
	{
		string trimmed = (name ?? string.Empty).Trim();
		if (string.Equals(trimmed, "LengthDecimal", StringComparison.OrdinalIgnoreCase))
		{
			return TigerStopLengthInches;
		}

		return trimmed;
	}
}
