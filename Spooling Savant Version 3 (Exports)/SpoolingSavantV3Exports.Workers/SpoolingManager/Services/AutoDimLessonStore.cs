using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>JSON lesson store for the Teach Auto-Dim loop.</summary>
public static class AutoDimLessonStore
{
	public const double Slot0OffsetSheetFeet = 3.0 / (12.0 * 8.0);
	public const double OffsetTolSheetFeet = 1.0 / (12.0 * 16.0); // 1/16" sheet

	private static AutoDimLessonStoreDocument _cache;
	private static readonly object Gate = new object();
	private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

	public static string LessonsFilePath =>
		Path.Combine(SpoolingManagerSettings.SettingsFolderPath, "AutoDimLessons.json");

	public static AutoDimLessonStoreDocument Load(bool forceReload = false)
	{
		lock (Gate)
		{
			if (!forceReload && _cache != null)
			{
				return _cache;
			}
			try
			{
				string path = LessonsFilePath;
				if (File.Exists(path))
				{
					string text = File.ReadAllText(path);
					_cache = Json.Deserialize<AutoDimLessonStoreDocument>(text) ?? new AutoDimLessonStoreDocument();
				}
				else
				{
					_cache = new AutoDimLessonStoreDocument();
				}
			}
			catch
			{
				_cache = new AutoDimLessonStoreDocument();
			}
			_cache.Positive ??= new List<AutoDimPositiveLesson>();
			_cache.AntiPatterns ??= new List<AutoDimAntiPatternLesson>();
			return _cache;
		}
	}

	public static void Save(AutoDimLessonStoreDocument doc)
	{
		if (doc == null)
		{
			return;
		}
		lock (Gate)
		{
			doc.UpdatedUtc = DateTime.UtcNow;
			_cache = doc;
			try
			{
				Directory.CreateDirectory(SpoolingManagerSettings.SettingsFolderPath);
				string json = Json.Serialize(doc);
				File.WriteAllText(LessonsFilePath, json);
			}
			catch
			{
			}
		}
	}

	public static void InvalidateCache()
	{
		lock (Gate)
		{
			_cache = null;
		}
	}

	public static string BuildRoleKey(string hostKindA, string hostKindB, bool isHorizontal)
	{
		string a = NormalizeKind(hostKindA);
		string b = NormalizeKind(hostKindB);
		string axis = isHorizontal ? "H" : "V";
		string[] ordered = new[] { a, b }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
		return ordered[0] + "_" + ordered[1] + "_" + axis;
	}

	public static string NormalizeKind(string kind)
	{
		if (string.IsNullOrWhiteSpace(kind))
		{
			return "Unknown";
		}
		string k = kind.Trim();
		if (k.IndexOf("olet", StringComparison.OrdinalIgnoreCase) >= 0
			|| k.IndexOf("o-let", StringComparison.OrdinalIgnoreCase) >= 0
			|| k.IndexOf("tol", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return "Olet";
		}
		if (k.IndexOf("flange", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return "Flange";
		}
		if (k.IndexOf("elbow", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return "Elbow";
		}
		if (k.IndexOf("tee", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return "Tee";
		}
		if (k.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return "Pipe";
		}
		return k;
	}

	public static bool TryGetPositiveLesson(
		string roleKey,
		string policyRole,
		bool isHorizontal,
		out AutoDimPositiveLesson lesson)
	{
		lesson = null;
		AutoDimLessonStoreDocument doc = Load();
		IEnumerable<AutoDimPositiveLesson> q = doc.Positive
			.Where(p => p != null && p.IsHorizontal == isHorizontal);
		if (!string.IsNullOrWhiteSpace(roleKey))
		{
			lesson = q.Where(p => string.Equals(p.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(p => p.TeachCount)
				.ThenByDescending(p => p.LastTaughtUtc)
				.FirstOrDefault();
		}
		if (lesson == null && !string.IsNullOrWhiteSpace(policyRole))
		{
			lesson = q.Where(p => string.Equals(p.PolicyRole, policyRole, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(p => p.TeachCount)
				.ThenByDescending(p => p.LastTaughtUtc)
				.FirstOrDefault();
		}
		return lesson != null;
	}

	public static bool MatchesAntiPattern(
		string roleKey,
		string policyRole,
		bool isHorizontal,
		double offsetSheetFeet)
	{
		AutoDimLessonStoreDocument doc = Load();
		foreach (AutoDimAntiPatternLesson anti in doc.AntiPatterns)
		{
			if (anti == null || anti.IsHorizontal != isHorizontal)
			{
				continue;
			}
			bool roleMatch = (!string.IsNullOrWhiteSpace(roleKey)
					&& string.Equals(anti.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase))
				|| (!string.IsNullOrWhiteSpace(policyRole)
					&& string.Equals(anti.PolicyRole, policyRole, StringComparison.OrdinalIgnoreCase));
			if (!roleMatch)
			{
				continue;
			}
			if (string.Equals(anti.Reason, "FarOffset", StringComparison.OrdinalIgnoreCase)
				&& anti.RejectedOffsetSheetFeet > Slot0OffsetSheetFeet + OffsetTolSheetFeet
				&& offsetSheetFeet > Slot0OffsetSheetFeet + OffsetTolSheetFeet
				&& Math.Abs(offsetSheetFeet - anti.RejectedOffsetSheetFeet) < 0.05)
			{
				return true;
			}
			if (!string.Equals(anti.Reason, "FarOffset", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static (int positive, int anti) UpsertFromTeach(
		IList<TeachAutoDimListItem> items,
		string viewName,
		string assemblyName)
	{
		AutoDimLessonStoreDocument doc = Load(forceReload: true);
		int posN = 0;
		int antiN = 0;
		DateTime now = DateTime.UtcNow;

		if (items == null)
		{
			Save(doc);
			return (0, 0);
		}

		foreach (TeachAutoDimListItem item in items)
		{
			if (item == null)
			{
				continue;
			}
			bool anyPositive = item.ContentCorrect || item.PlacementCorrect;
			bool anyAnti = item.ContentIncorrect || item.PlacementIncorrect;
			if (!anyPositive && !anyAnti)
			{
				continue;
			}

			string roleKey = item.RoleKey ?? BuildRoleKey(item.HostKindA, item.HostKindB, item.IsHorizontal);

			if (anyPositive)
			{
				AutoDimPositiveLesson existing = doc.Positive.FirstOrDefault(p =>
					p != null
					&& string.Equals(p.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase)
					&& p.IsHorizontal == item.IsHorizontal
					&& (item.PlacementCorrect
						? p.OffsetSign == (item.OffsetSign == 0 ? p.OffsetSign : Math.Sign(item.OffsetSign))
						: true));
				// Prefer match on role+axis; if multiple, prefer same offset sign when teaching placement.
				if (existing == null)
				{
					existing = doc.Positive.FirstOrDefault(p =>
						p != null
						&& string.Equals(p.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase)
						&& p.IsHorizontal == item.IsHorizontal);
				}
				double offsetSheet = item.OffsetSheetFeet > 1E-09 ? item.OffsetSheetFeet : Slot0OffsetSheetFeet;
				if (Math.Abs(offsetSheet - Slot0OffsetSheetFeet) <= OffsetTolSheetFeet * 2)
				{
					offsetSheet = Slot0OffsetSheetFeet;
				}
				if (existing == null)
				{
					existing = new AutoDimPositiveLesson
					{
						Id = Guid.NewGuid().ToString("N"),
						RoleKey = roleKey,
						PolicyRole = item.PolicyRole,
						IsHorizontal = item.IsHorizontal,
						OffsetSign = item.OffsetSign == 0 ? 1 : Math.Sign(item.OffsetSign),
						TargetOffsetSheetFeet = offsetSheet,
						ContentTaught = item.ContentCorrect,
						PlacementTaught = item.PlacementCorrect,
						SpanInches = item.SpanInches,
						HostKindA = NormalizeKind(item.HostKindA),
						HostKindB = NormalizeKind(item.HostKindB),
						ProductHintA = item.ProductHintA,
						ProductHintB = item.ProductHintB,
						SampleHostIdA = item.HostIdA,
						SampleHostIdB = item.HostIdB,
						TeachCount = 1,
						LastTaughtUtc = now,
						SourceViewName = viewName,
						SourceAssemblyName = assemblyName
					};
					doc.Positive.Add(existing);
				}
				else
				{
					existing.TeachCount++;
					existing.LastTaughtUtc = now;
					if (item.ContentCorrect)
					{
						existing.ContentTaught = true;
						existing.SpanInches = item.SpanInches;
						existing.HostKindA = NormalizeKind(item.HostKindA);
						existing.HostKindB = NormalizeKind(item.HostKindB);
						existing.ProductHintA = item.ProductHintA;
						existing.ProductHintB = item.ProductHintB;
						existing.SampleHostIdA = item.HostIdA;
						existing.SampleHostIdB = item.HostIdB;
						existing.PolicyRole = item.PolicyRole ?? existing.PolicyRole;
					}
					if (item.PlacementCorrect)
					{
						existing.PlacementTaught = true;
						existing.TargetOffsetSheetFeet = offsetSheet;
						existing.OffsetSign = item.OffsetSign == 0 ? existing.OffsetSign : Math.Sign(item.OffsetSign);
					}
					existing.SourceViewName = viewName;
					existing.SourceAssemblyName = assemblyName;
				}
				posN++;
			}

			if (item.ContentIncorrect)
			{
				antiN += UpsertAnti(doc, item, roleKey, "Incorrect", "Content", now);
			}
			if (item.PlacementIncorrect)
			{
				antiN += UpsertAnti(doc, item, roleKey, "FarOffset", "Placement", now);
			}
		}

		Save(doc);
		return (posN, antiN);
	}

	private static int UpsertAnti(
		AutoDimLessonStoreDocument doc,
		TeachAutoDimListItem item,
		string roleKey,
		string reason,
		string aspect,
		DateTime now)
	{
		AutoDimAntiPatternLesson existing = doc.AntiPatterns.FirstOrDefault(a =>
			a != null
			&& string.Equals(a.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase)
			&& a.IsHorizontal == item.IsHorizontal
			&& string.Equals(a.Reason, reason, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(a.Aspect ?? "Content", aspect, StringComparison.OrdinalIgnoreCase));
		if (existing == null)
		{
			doc.AntiPatterns.Add(new AutoDimAntiPatternLesson
			{
				Id = Guid.NewGuid().ToString("N"),
				RoleKey = roleKey,
				PolicyRole = item.PolicyRole,
				IsHorizontal = item.IsHorizontal,
				Reason = reason,
				Aspect = aspect,
				RejectedOffsetSheetFeet = item.OffsetSheetFeet,
				HostKindA = NormalizeKind(item.HostKindA),
				HostKindB = NormalizeKind(item.HostKindB),
				SpanInches = item.SpanInches,
				TeachCount = 1,
				LastTaughtUtc = now
			});
		}
		else
		{
			existing.TeachCount++;
			existing.LastTaughtUtc = now;
			existing.RejectedOffsetSheetFeet = item.OffsetSheetFeet;
			existing.Aspect = aspect;
		}
		return 1;
	}
}
