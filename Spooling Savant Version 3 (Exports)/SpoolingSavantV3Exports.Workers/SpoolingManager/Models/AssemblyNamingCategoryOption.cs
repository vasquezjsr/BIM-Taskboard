using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

public sealed class AssemblyNamingCategoryOption
{
	public ElementId CategoryId { get; }

	public string DisplayName { get; }

	public AssemblyNamingCategoryOption(ElementId categoryId, string displayName)
	{
		CategoryId = categoryId;
		DisplayName = displayName ?? string.Empty;
	}
}
