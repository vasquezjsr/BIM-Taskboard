using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class CaptureDimensionExampleRequest
{
	public ElementId AssemblyId { get; set; }

	public SpoolingManagerKind ProductKind { get; set; }
}

public sealed class CaptureDimensionExampleResult
{
	public bool Success { get; set; }

	public string Message { get; set; }

	public string OutputPath { get; set; }

	public int DimensionCount { get; set; }
}
