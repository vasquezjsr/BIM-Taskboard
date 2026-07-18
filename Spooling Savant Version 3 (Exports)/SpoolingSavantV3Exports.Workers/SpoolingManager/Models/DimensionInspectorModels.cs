namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public enum DimensionInspectorAction
{
	Open,
	Refresh,
	Close
}

public sealed class DimensionInspectorRequest
{
	public DimensionInspectorAction Action { get; set; }

	public bool ExportViewImage { get; set; } = true;
}

public sealed class DimensionInspectorReport
{
	public bool Success { get; set; }

	public string StatusMessage { get; set; }

	public string ViewName { get; set; }

	public string ViewImagePath { get; set; }

	public string DetailText { get; set; }
}
