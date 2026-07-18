using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class PlotPackageBatch
{
	public string PackageLabel { get; set; }

	public List<ElementId> AssemblyIds { get; set; } = new List<ElementId>();
}
