namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public sealed class WeldLogExportRow
{
	public string WeldNumber { get; set; } = string.Empty;

	public string Date { get; set; } = string.Empty;

	public string WelderId { get; set; } = string.Empty;

	public string Initials { get; set; } = string.Empty;

	public string Material { get; set; } = string.Empty;

	public string WeldType { get; set; } = string.Empty;

	/// <summary>Assembly / spool name so Boardroom can group Field Welds under the right assembly.</summary>
	public string Assembly { get; set; } = string.Empty;
}
