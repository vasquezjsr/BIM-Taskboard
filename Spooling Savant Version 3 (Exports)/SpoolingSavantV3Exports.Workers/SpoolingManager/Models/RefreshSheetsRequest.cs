using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class RefreshSheetsRequest
{
	public List<ElementId> AssemblyIds { get; set; } = new List<ElementId>();

	public SpoolingManagerKind ProductKind { get; set; }

	public string SWeldPrefix { get; set; }
}
