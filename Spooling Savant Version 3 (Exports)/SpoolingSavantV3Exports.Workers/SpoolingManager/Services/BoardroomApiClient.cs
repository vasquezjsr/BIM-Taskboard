using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Web.Script.Serialization;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>One Spooling-board task from the live BIM Boardroom API.</summary>
public sealed class BoardroomTaskOption
{
	public string Id { get; set; } = string.Empty;

	public string TaskNumber { get; set; }

	public string Title { get; set; } = string.Empty;

	public string Status { get; set; } = string.Empty;

	public string ProjectId { get; set; }

	/// <summary>True when Boardroom already has an SSv3 export attached to this task.</summary>
	public bool HasSsv3Export { get; set; }

	public string DisplayLabel
	{
		get
		{
			string number = string.IsNullOrWhiteSpace(TaskNumber) ? string.Empty : TaskNumber.Trim() + " · ";
			string title = string.IsNullOrWhiteSpace(Title) ? "(untitled)" : Title.Trim();
			string status = string.IsNullOrWhiteSpace(Status) ? string.Empty : " [" + Status.Trim() + "]";
			string export = HasSsv3Export ? " (has export)" : string.Empty;
			return number + title + status + export;
		}
	}

	public override string ToString() => DisplayLabel;
}

/// <summary>Read-only loopback client for BIM Boardroom (default http://127.0.0.1:17321).</summary>
public sealed class BoardroomApiClient : IDisposable
{
	public const string DefaultBaseUrl = "http://127.0.0.1:17321";

	private readonly HttpClient _http;
	private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

	public BoardroomApiClient(string baseUrl)
	{
		string trimmed = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
		_http = new HttpClient
		{
			BaseAddress = new Uri(trimmed + "/", UriKind.Absolute),
			Timeout = TimeSpan.FromSeconds(8)
		};
	}

	public static string NormalizeBaseUrl(string baseUrl)
	{
		if (string.IsNullOrWhiteSpace(baseUrl))
		{
			return DefaultBaseUrl;
		}

		return baseUrl.Trim().TrimEnd('/');
	}

	public bool TryHealth(out string message)
	{
		try
		{
			using HttpResponseMessage response = _http.GetAsync("health").GetAwaiter().GetResult();
			string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
			if (!response.IsSuccessStatusCode)
			{
				message = "Boardroom API returned HTTP " + (int)response.StatusCode + ". Is BIM Boardroom running?";
				return false;
			}

			var map = _json.DeserializeObject(body) as Dictionary<string, object>;
			bool snapshotReady = true;
			if (map != null && map.TryGetValue("snapshotReady", out object readyObj) && readyObj is bool ready)
			{
				snapshotReady = ready;
			}

			if (!snapshotReady)
			{
				message = "Boardroom is running but data is not ready yet. Wait a moment and try again.";
				return false;
			}

			message = "Connected to BIM Boardroom at " + (_http.BaseAddress?.ToString() ?? DefaultBaseUrl);
			return true;
		}
		catch (Exception ex)
		{
			message = "Cannot reach BIM Boardroom API.\n\nStart BIM Boardroom (Electron), then try again.\n\n" + ex.Message;
			return false;
		}
	}

	public IReadOnlyList<BoardroomProjectOption> GetProjects(bool includeTemplates = false)
	{
		string path = "v1/projects?includeTemplates=" + (includeTemplates ? "true" : "false");
		string body = GetString(path);
		var results = new List<BoardroomProjectOption>();
		foreach (Dictionary<string, object> row in EnumerateJsonObjects(body))
		{
			results.Add(new BoardroomProjectOption
			{
				ProjectId = ReadString(row, "id"),
				ProjectName = ReadString(row, "name"),
				JobCode = ReadString(row, "jobCode"),
				ClientId = ReadString(row, "clientId"),
				ClientName = ReadString(row, "clientName"),
				IsTemplate = ReadBool(row, "isTemplate"),
				BillingType = ReadString(row, "billingType"),
				RevitYear = ReadString(row, "revitYear")
			});
		}

		results.Sort((a, b) =>
		{
			int byClient = string.Compare(a.ClientName, b.ClientName, StringComparison.OrdinalIgnoreCase);
			if (byClient != 0)
			{
				return byClient;
			}

			return string.Compare(a.ProjectName, b.ProjectName, StringComparison.OrdinalIgnoreCase);
		});
		return results;
	}

	public IReadOnlyList<BoardroomTaskOption> GetSpoolingTasks(string projectId)
	{
		if (string.IsNullOrWhiteSpace(projectId))
		{
			return Array.Empty<BoardroomTaskOption>();
		}

		string path = "v1/projects/" + Uri.EscapeDataString(projectId.Trim()) + "/tasks?boardType=spooling";
		string body = GetString(path);
		var results = new List<BoardroomTaskOption>();
		foreach (Dictionary<string, object> row in EnumerateJsonObjects(body))
		{
			results.Add(new BoardroomTaskOption
			{
				Id = ReadString(row, "id"),
				TaskNumber = NullIfEmpty(ReadString(row, "taskNumber")),
				Title = ReadString(row, "title"),
				Status = ReadString(row, "status"),
				ProjectId = ReadString(row, "projectId"),
				HasSsv3Export = ReadBool(row, "hasSsv3Export")
			});
		}

		results.Sort((a, b) =>
		{
			int byNumber = string.Compare(a.TaskNumber ?? string.Empty, b.TaskNumber ?? string.Empty, StringComparison.OrdinalIgnoreCase);
			if (byNumber != 0)
			{
				return byNumber;
			}

			return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
		});
		return results;
	}

	/// <summary>
	/// JavaScriptSerializer returns object[] for JSON arrays (not ArrayList). Accept both.
	/// </summary>
	private IEnumerable<Dictionary<string, object>> EnumerateJsonObjects(string body)
	{
		object parsed = _json.DeserializeObject(body);
		System.Collections.IEnumerable items = parsed as object[]
			?? parsed as System.Collections.ArrayList
			?? parsed as System.Collections.IList;
		if (items == null)
		{
			yield break;
		}

		foreach (object item in items)
		{
			if (item is Dictionary<string, object> row)
			{
				yield return row;
			}
		}
	}

	private string GetString(string relativePath)
	{
		using HttpResponseMessage response = _http.GetAsync(relativePath).GetAwaiter().GetResult();
		string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode)
		{
			string detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
			throw new InvalidOperationException("Boardroom API HTTP " + (int)response.StatusCode + ": " + detail);
		}

		return body;
	}

	private static string ReadString(Dictionary<string, object> row, string key)
	{
		if (!row.TryGetValue(key, out object value) || value == null)
		{
			return string.Empty;
		}

		return Convert.ToString(value) ?? string.Empty;
	}

	private static bool ReadBool(Dictionary<string, object> row, string key)
	{
		if (!row.TryGetValue(key, out object value) || value == null)
		{
			return false;
		}

		if (value is bool b)
		{
			return b;
		}

		return string.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase);
	}

	private static string NullIfEmpty(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	public void Dispose()
	{
		_http.Dispose();
	}
}
