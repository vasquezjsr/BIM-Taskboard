namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

/// <summary>Sheet-space rectangle (feet) for one weld-log Date / Welder ID / Initials entry field.</summary>
public sealed class WeldLogEntryFieldRect
{
	public string Kind { get; set; } = string.Empty;

	public string WeldNumber { get; set; } = string.Empty;

	public int SlotIndex { get; set; }

	public double MinX { get; set; }

	public double MinY { get; set; }

	public double MaxX { get; set; }

	public double MaxY { get; set; }
}
