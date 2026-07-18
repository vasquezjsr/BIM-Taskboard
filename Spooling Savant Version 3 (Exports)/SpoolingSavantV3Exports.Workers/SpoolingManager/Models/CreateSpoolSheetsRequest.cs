using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class CreateSpoolSheetsRequest
{
	public List<ElementId> AssemblyIds { get; set; } = new List<ElementId>();

	public ExistingSheetAction ExistingSheetAction { get; set; } = ExistingSheetAction.SkipExisting;

	public SpoolingManagerKind ProductKind { get; set; }

	public string SWeldPrefix { get; set; }
}
