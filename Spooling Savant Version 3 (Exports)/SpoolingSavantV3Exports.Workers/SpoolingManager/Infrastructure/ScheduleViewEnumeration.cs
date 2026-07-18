using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Infrastructure;

internal static class ScheduleViewEnumeration
{
	/// <summary>
	/// Schedules shown in Assembly Settings: Filter by Sheet must be on, must not be
	/// Revit sheet-instance copies ("Internal"), and must not already be a placed/dedicated
	/// sheet schedule. Reusable masters (e.g. placed many times with Internal copies) stay.
	/// </summary>
	internal static bool IsSelectableScheduleName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}

		return name.IndexOf("Internal", StringComparison.OrdinalIgnoreCase) < 0;
	}

	internal static bool IsSelectableSchedule(Document doc, ViewSchedule schedule)
	{
		if (schedule == null)
		{
			return false;
		}

		try
		{
			if (schedule.IsTemplate)
			{
				return false;
			}
		}
		catch
		{
		}

		string name = schedule.Name;
		if (!IsSelectableScheduleName(name))
		{
			return false;
		}

		try
		{
			ScheduleDefinition definition = schedule.Definition;
			if (definition == null || !definition.IsFilteredBySheet)
			{
				return false;
			}
		}
		catch
		{
			return false;
		}

		return !IsAlreadyPlacedOrDedicatedSheetSchedule(doc, schedule);
	}

	internal static IEnumerable<string> GetSelectableScheduleNames(Document doc)
	{
		HashSet<ElementId> placedScheduleIds = CollectPlacedScheduleIds(doc);
		HashSet<string> namesWithInternalCopies = CollectNamesWithInternalCopies(doc);

		return EnumerateViewSchedules(doc)
			.Where(vs => IsSelectableSchedule(doc, vs, placedScheduleIds, namesWithInternalCopies))
			.Select(vs => vs.Name)
			.Where(n => !string.IsNullOrWhiteSpace(n))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
	}

	private static bool IsSelectableSchedule(
		Document doc,
		ViewSchedule schedule,
		HashSet<ElementId> placedScheduleIds,
		HashSet<string> namesWithInternalCopies)
	{
		if (schedule == null)
		{
			return false;
		}

		try
		{
			if (schedule.IsTemplate)
			{
				return false;
			}
		}
		catch
		{
		}

		string name = schedule.Name;
		if (!IsSelectableScheduleName(name))
		{
			return false;
		}

		try
		{
			ScheduleDefinition definition = schedule.Definition;
			if (definition == null || !definition.IsFilteredBySheet)
			{
				return false;
			}
		}
		catch
		{
			return false;
		}

		return !IsAlreadyPlacedOrDedicatedSheetSchedule(schedule, placedScheduleIds, namesWithInternalCopies);
	}

	/// <summary>
	/// Hides schedules that are already on sheets (or leftover dedicated sheet/assembly schedules),
	/// while keeping reusable Filter-by-Sheet templates.
	/// </summary>
	private static bool IsAlreadyPlacedOrDedicatedSheetSchedule(
		ViewSchedule schedule,
		HashSet<ElementId> placedScheduleIds,
		HashSet<string> namesWithInternalCopies)
	{
		string name = schedule?.Name;
		if (string.IsNullOrWhiteSpace(name))
		{
			return true;
		}

		// "{SheetOrAssembly} – Schedule" leftovers (e.g. CDU-13-CHWR-001 – Schedule)
		if (LooksLikeDedicatedSheetScheduleName(name))
		{
			return true;
		}

		bool placed = placedScheduleIds != null && placedScheduleIds.Contains(schedule.Id);
		if (!placed)
		{
			return false;
		}

		// Placed reusable masters get "... Internal" copies when Filter by Sheet is used.
		bool isReusableMaster = namesWithInternalCopies != null &&
			namesWithInternalCopies.Contains(name);
		return !isReusableMaster;
	}

	private static bool IsAlreadyPlacedOrDedicatedSheetSchedule(Document doc, ViewSchedule schedule)
	{
		HashSet<ElementId> placed = CollectPlacedScheduleIds(doc);
		HashSet<string> withInternal = CollectNamesWithInternalCopies(doc);
		return IsAlreadyPlacedOrDedicatedSheetSchedule(schedule, placed, withInternal);
	}

	internal static bool LooksLikeDedicatedSheetScheduleName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}

		bool hasDigit = false;
		foreach (char c in name)
		{
			if (char.IsDigit(c))
			{
				hasDigit = true;
				break;
			}
		}

		if (!hasDigit)
		{
			return false;
		}

		int scheduleIdx = name.LastIndexOf("Schedule", StringComparison.OrdinalIgnoreCase);
		if (scheduleIdx <= 0)
		{
			return false;
		}

		string before = name.Substring(0, scheduleIdx).TrimEnd();
		if (before.Length == 0)
		{
			return false;
		}

		char last = before[before.Length - 1];
		// En-dash, em-dash, or hyphen separator before "Schedule"
		return last == '–' || last == '—' || last == '-';
	}

	private static HashSet<ElementId> CollectPlacedScheduleIds(Document doc)
	{
		HashSet<ElementId> placed = new HashSet<ElementId>();
		if (doc == null)
		{
			return placed;
		}

		try
		{
			foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc)
				.OfClass(typeof(ScheduleSheetInstance))
				.Cast<ScheduleSheetInstance>())
			{
				if (instance?.ScheduleId != null && instance.ScheduleId != ElementId.InvalidElementId)
				{
					placed.Add(instance.ScheduleId);
				}
			}
		}
		catch
		{
		}

		return placed;
	}

	private static HashSet<string> CollectNamesWithInternalCopies(Document doc)
	{
		HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (doc == null)
		{
			return names;
		}

		const string marker = " Internal";
		foreach (ViewSchedule schedule in EnumerateViewSchedules(doc))
		{
			string name = schedule?.Name;
			if (string.IsNullOrWhiteSpace(name))
			{
				continue;
			}

			int idx = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
			if (idx <= 0)
			{
				continue;
			}

			string masterName = name.Substring(0, idx).TrimEnd();
			if (!string.IsNullOrWhiteSpace(masterName))
			{
				names.Add(masterName);
			}
		}

		return names;
	}

	internal static IEnumerable<ViewSchedule> EnumerateViewSchedules(Document doc)
	{
		if (doc == null)
		{
			yield break;
		}
		HashSet<ElementId> seen = new HashSet<ElementId>();
		foreach (ViewSchedule item in ((IEnumerable)new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule))).Cast<ViewSchedule>())
		{
			if (item != null && seen.Add(((Element)item).Id))
			{
				yield return item;
			}
		}
		foreach (ElementId item2 in new FilteredElementCollector(doc).OfClass(typeof(View)).WhereElementIsNotElementType().ToElementIds())
		{
			Element element = doc.GetElement(item2);
			View val = (View)(object)((element is View) ? element : null);
			if (val != null && (int)val.ViewType == 5)
			{
				ViewSchedule val2 = (ViewSchedule)(object)((val is ViewSchedule) ? val : null);
				if (val2 != null && seen.Add(((Element)val2).Id))
				{
					yield return val2;
				}
			}
		}
	}
}
