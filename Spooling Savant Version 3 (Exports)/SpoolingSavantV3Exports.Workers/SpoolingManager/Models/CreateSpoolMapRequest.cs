using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public sealed class CreateSpoolMapPackageOption
{
	public string Label { get; set; }

	public string PackageValue { get; set; }

	public List<ElementId> AssemblyIds { get; set; } = new List<ElementId>();

	public override string ToString()
	{
		return Label ?? string.Empty;
	}
}

public sealed class CreateSpoolMapRequest
{
	public string PackageLabel { get; set; }

	public string PackageValue { get; set; }

	public List<ElementId> AssemblyIds { get; set; } = new List<ElementId>();

	public string TitleBlockName { get; set; }

	public string ViewTemplate3DName { get; set; }

	public string ViewTemplatePlanName { get; set; }

	public string AssemblyTagTypeName { get; set; }

	public SpoolingManagerKind ProductKind { get; set; }

	/// <summary>
	/// When true, delete the existing Spool Map sheet/views for this package and recreate them.
	/// Set from the UI after the user confirms overwrite.
	/// </summary>
	public bool OverwriteExisting { get; set; }
}
