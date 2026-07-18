using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>One Boardroom project row from the projects CSV used by SS Manager export.</summary>
public sealed class BoardroomProjectOption
{
	public string ProjectId { get; set; } = string.Empty;

	public string ProjectName { get; set; } = string.Empty;

	public string JobCode { get; set; } = string.Empty;

	public string ClientId { get; set; } = string.Empty;

	public string ClientName { get; set; } = string.Empty;

	public bool IsTemplate { get; set; }

	public string BillingType { get; set; } = string.Empty;

	public string RevitYear { get; set; } = string.Empty;

	public string DisplayLabel
	{
		get
		{
			string client = string.IsNullOrWhiteSpace(ClientName) ? "—" : ClientName.Trim();
			string project = string.IsNullOrWhiteSpace(ProjectName) ? "(unnamed)" : ProjectName.Trim();
			string code = string.IsNullOrWhiteSpace(JobCode) ? string.Empty : " [" + JobCode.Trim() + "]";
			return client + " — " + project + code;
		}
	}

	public override string ToString() => DisplayLabel;
}

/// <summary>Reads BIM Boardroom project lists exported as CSV for SS Manager pickers.</summary>
public static class BoardroomProjectsCsv
{
	public static string DefaultCsvPath =>
		Path.Combine(
			@"C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)\Boardroom",
			"Boardroom-Projects.csv");

	public static IReadOnlyList<BoardroomProjectOption> Load(
		string csvPath,
		bool includeTemplates = false)
	{
		if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
		{
			return Array.Empty<BoardroomProjectOption>();
		}

		string[] lines = File.ReadAllLines(csvPath, Encoding.UTF8);
		if (lines.Length == 0)
		{
			return Array.Empty<BoardroomProjectOption>();
		}

		List<string> header = ParseCsvLine(lines[0]);
		var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < header.Count; i++)
		{
			string key = (header[i] ?? string.Empty).Trim();
			if (key.Length > 0 && !index.ContainsKey(key))
			{
				index[key] = i;
			}
		}

		var results = new List<BoardroomProjectOption>();
		for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
		{
			string line = lines[lineIndex];
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			List<string> cells = ParseCsvLine(line);
			var row = new BoardroomProjectOption
			{
				ProjectId = Get(cells, index, "ProjectId"),
				ProjectName = Get(cells, index, "ProjectName"),
				JobCode = Get(cells, index, "JobCode"),
				ClientId = Get(cells, index, "ClientId"),
				ClientName = Get(cells, index, "ClientName"),
				IsTemplate = ParseBool(Get(cells, index, "IsTemplate")),
				BillingType = Get(cells, index, "BillingType"),
				RevitYear = Get(cells, index, "RevitYear"),
			};

			if (string.IsNullOrWhiteSpace(row.ProjectId) && string.IsNullOrWhiteSpace(row.ProjectName))
			{
				continue;
			}

			if (!includeTemplates && row.IsTemplate)
			{
				continue;
			}

			results.Add(row);
		}

		return results
			.OrderBy(r => r.ClientName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public static bool TryValidate(string csvPath, out string message)
	{
		if (string.IsNullOrWhiteSpace(csvPath))
		{
			message = "No Boardroom projects CSV path is set.";
			return false;
		}

		if (!File.Exists(csvPath))
		{
			message = "Boardroom projects CSV not found:\n" + csvPath;
			return false;
		}

		IReadOnlyList<BoardroomProjectOption> all = Load(csvPath, includeTemplates: true);
		IReadOnlyList<BoardroomProjectOption> active = Load(csvPath, includeTemplates: false);
		message = active.Count + " project(s) available for export ("
			+ all.Count + " row(s) in file; templates excluded from picker).";
		return all.Count > 0;
	}

	private static string Get(List<string> cells, Dictionary<string, int> index, string column)
	{
		if (!index.TryGetValue(column, out int i) || i < 0 || i >= cells.Count)
		{
			return string.Empty;
		}

		return cells[i]?.Trim() ?? string.Empty;
	}

	private static bool ParseBool(string value)
	{
		if (bool.TryParse(value, out bool parsed))
		{
			return parsed;
		}

		return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
	}

	private static List<string> ParseCsvLine(string line)
	{
		var cells = new List<string>();
		if (line == null)
		{
			return cells;
		}

		var current = new StringBuilder();
		bool inQuotes = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			if (inQuotes)
			{
				if (c == '"')
				{
					if (i + 1 < line.Length && line[i + 1] == '"')
					{
						current.Append('"');
						i++;
					}
					else
					{
						inQuotes = false;
					}
				}
				else
				{
					current.Append(c);
				}
			}
			else if (c == '"')
			{
				inQuotes = true;
			}
			else if (c == ',')
			{
				cells.Add(current.ToString());
				current.Clear();
			}
			else
			{
				current.Append(c);
			}
		}

		cells.Add(current.ToString());
		return cells;
	}
}
