using System;
using System.IO;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;

internal static class InstallLayout
{
	private const string DefaultRevitYear = "2024";

	private const string AddinFolderName = "Spooling-Savant-V3-Exports";

	private const string ModuleFolderName = "SpoolingManager";

	public static string CurrentRevitYear { get; set; } = "2024";

	public static void ApplyRevitVersionNumber(string versionNumber)
	{
		if (!string.IsNullOrWhiteSpace(versionNumber))
		{
			string text = versionNumber.Trim();
			int num = text.IndexOf('/');
			if (num >= 0)
			{
				text = text.Substring(0, num);
			}
			int num2 = text.IndexOf('.');
			if (num2 >= 0)
			{
				text = text.Substring(0, num2);
			}
			if (text.Length >= 4 && char.IsDigit(text[0]))
			{
				CurrentRevitYear = text;
			}
		}
	}

	public static string GetAddinsRoot()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", string.IsNullOrWhiteSpace(CurrentRevitYear) ? "2024" : CurrentRevitYear);
	}

	public static string GetModuleFolder()
	{
		return Path.Combine(GetAddinsRoot(), AddinFolderName, ModuleFolderName);
	}

	public static string GetPreferredModuleFolder()
	{
		return GetModuleFolder();
	}
}
