using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI.Selection;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Commands;

/// <summary>Fabrication parts that are not already members of an assembly.</summary>
public sealed class TraceSpoolEndpointSelectionFilter : ISelectionFilter
{
	public bool AllowElement(Element elem)
	{
		if (!(elem is FabricationPart part) || part.Category == null)
		{
			return false;
		}

		ElementId assemblyId = part.AssemblyInstanceId;
		if (assemblyId != null && assemblyId != ElementId.InvalidElementId)
		{
			return false;
		}

		return true;
	}

	public bool AllowReference(Reference reference, XYZ position)
	{
		return false;
	}
}
