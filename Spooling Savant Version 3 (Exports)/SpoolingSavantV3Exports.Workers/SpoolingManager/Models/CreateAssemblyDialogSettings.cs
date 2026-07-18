using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

[Serializable]
public sealed class CreateAssemblyDialogSettings
{
	public string LastPackageName { get; set; } = string.Empty;

	public string LastAssemblyName { get; set; } = string.Empty;

	public static string SettingsFilePath => Path.Combine(InstallLayout.GetPreferredModuleFolder(), "CreateAssemblyDialogSettings.xml");

	public static string SuggestNextNumericSuffix(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}
		string text = value.Trim();
		Match match = Regex.Match(text, "^(.*?)(\\d+)$");
		if (!match.Success)
		{
			return text;
		}
		string value2 = match.Groups[1].Value;
		string value3 = match.Groups[2].Value;
		if (!long.TryParse(value3, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return text;
		}
		string text2 = (result + 1).ToString(CultureInfo.InvariantCulture);
		if (text2.Length < value3.Length)
		{
			text2 = text2.PadLeft(value3.Length, '0');
		}
		return value2 + text2;
	}

	public static CreateAssemblyDialogSettings Load()
	{
		try
		{
			string settingsFilePath = SettingsFilePath;
			if (!File.Exists(settingsFilePath))
			{
				return new CreateAssemblyDialogSettings();
			}
			XmlSerializer xmlSerializer = new XmlSerializer(typeof(CreateAssemblyDialogSettings));
			using FileStream stream = File.OpenRead(settingsFilePath);
			return (xmlSerializer.Deserialize(stream) as CreateAssemblyDialogSettings) ?? new CreateAssemblyDialogSettings();
		}
		catch
		{
			return new CreateAssemblyDialogSettings();
		}
	}

	public void Save()
	{
		try
		{
			string settingsFilePath = SettingsFilePath;
			string directoryName = Path.GetDirectoryName(settingsFilePath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			XmlSerializer xmlSerializer = new XmlSerializer(typeof(CreateAssemblyDialogSettings));
			using FileStream stream = File.Create(settingsFilePath);
			xmlSerializer.Serialize(stream, this);
		}
		catch
		{
		}
	}
}
