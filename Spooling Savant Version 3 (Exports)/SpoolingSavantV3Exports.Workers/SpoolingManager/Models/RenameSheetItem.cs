using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class RenameSheetItem
{
	public ElementId AssemblyId { get; set; }

	public string CurrentName { get; set; } = string.Empty;

	public string NewName { get; set; } = string.Empty;
}
