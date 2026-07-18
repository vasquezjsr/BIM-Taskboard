using System;
using System.Xml.Serialization;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

[Serializable]
public class SpoolScheduleOption
{
	public const string PlacementTopLeft = "Top Left";

	public const string PlacementTopRight = "Top Right";

	[XmlElement("Name")]
	public string Name { get; set; } = string.Empty;

	/// <summary>Top Left or Top Right.</summary>
	[XmlElement("Placement")]
	public string Placement { get; set; } = PlacementTopLeft;

	public static bool IsTopRight(string placement)
	{
		return string.Equals(placement, PlacementTopRight, StringComparison.OrdinalIgnoreCase);
	}

	public static string NormalizePlacement(string placement)
	{
		return IsTopRight(placement) ? PlacementTopRight : PlacementTopLeft;
	}
}
