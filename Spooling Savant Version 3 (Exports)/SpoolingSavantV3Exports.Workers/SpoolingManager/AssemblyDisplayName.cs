using System;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager;

internal static class AssemblyDisplayName
{
	public static string Get(AssemblyInstance assembly)
	{
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Invalid comparison between Unknown and I4
		if (assembly == null)
		{
			return string.Empty;
		}
		string text = string.Empty;
		Document document = ((Element)assembly).Document;
		if (document != null)
		{
			ElementId typeId = ((Element)assembly).GetTypeId();
			if (typeId != (ElementId)null && typeId != ElementId.InvalidElementId)
			{
				Element element = document.GetElement(typeId);
				AssemblyType val = (AssemblyType)(object)((element is AssemblyType) ? element : null);
				if (!string.IsNullOrWhiteSpace((val != null) ? ((Element)val).Name : null))
				{
					text = ((Element)val).Name.Trim();
				}
			}
		}
		string text2 = (string.IsNullOrWhiteSpace(assembly.AssemblyTypeName) ? string.Empty : assembly.AssemblyTypeName.Trim());
		string text3 = string.Empty;
		Parameter val2 = assembly.get_Parameter((BuiltInParameter)(-1001203));
		if (val2 != null && (int)val2.StorageType == 3)
		{
			string text4 = val2.AsString();
			if (!string.IsNullOrWhiteSpace(text4))
			{
				text3 = text4.Trim();
			}
		}
		if (text3.Length > 0 && (text.Length == 0 || text2.Length == 0 || string.Equals(text, text2, StringComparison.OrdinalIgnoreCase)))
		{
			return text3;
		}
		if (text.Length > 0 && text2.Length > 0 && !string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
		{
			return text2;
		}
		if (text.Length > 0)
		{
			return text;
		}
		if (text2.Length > 0)
		{
			return text2;
		}
		if (text3.Length > 0)
		{
			return text3;
		}
		string name = ((Element)assembly).Name;
		if (!string.IsNullOrWhiteSpace(name))
		{
			return name.Trim();
		}
		return string.Empty;
	}
}
