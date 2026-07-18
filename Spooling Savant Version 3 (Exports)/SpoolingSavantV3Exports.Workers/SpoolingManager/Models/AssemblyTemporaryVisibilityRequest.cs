using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public class AssemblyTemporaryVisibilityRequest
{
	public AssemblyTemporaryVisibilityAction Action { get; set; }

	public List<ElementId> MemberElementIds { get; set; } = new List<ElementId>();
}
