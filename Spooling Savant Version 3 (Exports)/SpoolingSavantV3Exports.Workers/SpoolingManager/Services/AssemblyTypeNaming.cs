using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

internal static class AssemblyTypeNaming
{
	private static readonly char[] InvalidRevitNameChars = new char[15]
	{
		'\\', '{', '}', '[', ']', '|', ';', '<', '>', '?',
		'\'', '"', ':', '/', '*'
	};

	internal static string Sanitize(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return string.Empty;
		}
		StringBuilder stringBuilder = new StringBuilder(raw.Length);
		string text = raw.Trim();
		foreach (char c in text)
		{
			if (char.IsControl(c))
			{
				continue;
			}
			if (Array.IndexOf(InvalidRevitNameChars, c) >= 0)
			{
				if (stringBuilder.Length == 0 || stringBuilder[stringBuilder.Length - 1] != '-')
				{
					stringBuilder.Append('-');
				}
			}
			else
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString().Trim('-', ' ', '.');
	}

	internal static bool IsUsable(string name)
	{
		return !string.IsNullOrWhiteSpace(Sanitize(name));
	}

	internal static string EnsureUniqueAssemblyTypeName(Document doc, string preferredName, ElementId excludeAssemblyTypeId = null)
	{
		string text = Sanitize(preferredName);
		if (string.IsNullOrWhiteSpace(text))
		{
			text = "Assembly";
		}

		HashSet<string> takenNames = CollectTakenAssemblyTypeNames(doc, excludeAssemblyTypeId);
		if (!takenNames.Contains(text))
		{
			return text;
		}

		for (int num = 2; num < 1000; num++)
		{
			string candidate = text + " (" + num + ")";
			if (!takenNames.Contains(candidate))
			{
				return candidate;
			}
		}

		return text + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
	}

	internal static void ApplyToAssembly(Document doc, AssemblyInstance assembly, string name)
	{
		if (doc == null || assembly == null)
		{
			return;
		}

		string text = Sanitize(name);
		if (text.Length == 0)
		{
			throw new ArgumentException("Assembly name is empty or contains only invalid characters.");
		}

		ElementId currentTypeId = ((Element)assembly).GetTypeId();
		RemoveOrphanAssemblyTypesWithName(doc, text, currentTypeId);
		text = EnsureUniqueAssemblyTypeName(doc, text, currentTypeId);

		Element element = doc.GetElement(currentTypeId);
		AssemblyType assemblyType = (AssemblyType)(object)((element is AssemblyType) ? element : null);
		if (assemblyType != null)
		{
			((Element)assemblyType).Name = text;
		}

		assembly.AssemblyTypeName = text;
	}

	private static HashSet<string> CollectTakenAssemblyTypeNames(Document doc, ElementId excludeAssemblyTypeId)
	{
		HashSet<ElementId> typesReferencedByInstances = new HashSet<ElementId>();
		foreach (AssemblyInstance instance in new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)))
		{
			AssemblyInstance assemblyInstance = instance;
			ElementId typeId = ((Element)assemblyInstance).GetTypeId();
			if (typeId != null && typeId != ElementId.InvalidElementId)
			{
				typesReferencedByInstances.Add(typeId);
			}
		}

		return new HashSet<string>(
			from AssemblyType type in (IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(AssemblyType))
			let typeId = ((Element)type).Id
			where (excludeAssemblyTypeId == null || typeId != excludeAssemblyTypeId)
				&& typesReferencedByInstances.Contains(typeId)
			let typeName = (((Element)type).Name ?? string.Empty).Trim()
			where typeName.Length > 0
			select typeName,
			StringComparer.OrdinalIgnoreCase);
	}

	private static void RemoveOrphanAssemblyTypesWithName(Document doc, string preferredName, ElementId excludeAssemblyTypeId)
	{
		if (doc == null || string.IsNullOrWhiteSpace(preferredName))
		{
			return;
		}

		HashSet<ElementId> typesReferencedByInstances = new HashSet<ElementId>();
		foreach (AssemblyInstance instance in new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)))
		{
			AssemblyInstance assemblyInstance = instance;
			ElementId typeId = ((Element)assemblyInstance).GetTypeId();
			if (typeId != null && typeId != ElementId.InvalidElementId)
			{
				typesReferencedByInstances.Add(typeId);
			}
		}

		List<ElementId> orphanTypeIds = new List<ElementId>();
		foreach (AssemblyType type in new FilteredElementCollector(doc).OfClass(typeof(AssemblyType)))
		{
			AssemblyType assemblyType = type;
			ElementId typeId = ((Element)assemblyType).Id;
			if (typeId == excludeAssemblyTypeId || typesReferencedByInstances.Contains(typeId))
			{
				continue;
			}

			string typeName = (((Element)assemblyType).Name ?? string.Empty).Trim();
			if (string.Equals(typeName, preferredName, StringComparison.OrdinalIgnoreCase))
			{
				orphanTypeIds.Add(typeId);
			}
		}

		if (orphanTypeIds.Count == 0)
		{
			return;
		}

		doc.Delete(orphanTypeIds);
	}
}
