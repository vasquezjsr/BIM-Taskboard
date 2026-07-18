using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class ApplyAssemblyPackageRequest
{
	public List<ElementId> AssemblyIds { get; set; } = new List<ElementId>();

	public string PackageValue { get; set; }

	public bool ClearPackage { get; set; }

	public bool SuppressCompletionDialog { get; set; }

	public SpoolingManagerKind ProductKind { get; set; }
}
