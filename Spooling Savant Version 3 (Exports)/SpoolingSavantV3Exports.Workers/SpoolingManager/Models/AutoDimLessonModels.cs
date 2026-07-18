using System;
using System.Collections.Generic;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

/// <summary>Persisted teach-loop knowledge (JSON under SpoolingManager settings folder).</summary>
[Serializable]
public sealed class AutoDimLessonStoreDocument
{
	public int Version { get; set; } = 1;

	public DateTime UpdatedUtc { get; set; }

	public List<AutoDimPositiveLesson> Positive { get; set; } = new List<AutoDimPositiveLesson>();

	public List<AutoDimAntiPatternLesson> AntiPatterns { get; set; } = new List<AutoDimAntiPatternLesson>();
}

[Serializable]
public sealed class AutoDimPositiveLesson
{
	public string Id { get; set; }

	/// <summary>Role key e.g. Flange_Elbow_H, Pipe_Olet_V.</summary>
	public string RoleKey { get; set; }

	public string PolicyRole { get; set; }

	public bool IsHorizontal { get; set; }

	/// <summary>+1 or -1 exterior offset sign along view Up (H) or Right (V).</summary>
	public int OffsetSign { get; set; } = 1;

	/// <summary>Taught gap from witness chord mid to dim line, in sheet feet (3/8" = 0.03125).</summary>
	public double TargetOffsetSheetFeet { get; set; } = 3.0 / (12.0 * 8.0);

	/// <summary>True when the user marked content (anchors / span / role) correct.</summary>
	public bool ContentTaught { get; set; }

	/// <summary>True when the user marked placement (offset / side) correct.</summary>
	public bool PlacementTaught { get; set; }

	public double SpanInches { get; set; }

	public string HostKindA { get; set; }

	public string HostKindB { get; set; }

	public string ProductHintA { get; set; }

	public string ProductHintB { get; set; }

	public long SampleHostIdA { get; set; }

	public long SampleHostIdB { get; set; }

	public int TeachCount { get; set; } = 1;

	public DateTime LastTaughtUtc { get; set; }

	public string SourceViewName { get; set; }

	public string SourceAssemblyName { get; set; }
}

[Serializable]
public sealed class AutoDimAntiPatternLesson
{
	public string Id { get; set; }

	public string RoleKey { get; set; }

	public string PolicyRole { get; set; }

	public bool IsHorizontal { get; set; }

	/// <summary>Incorrect | WrongSpan | WrongF | FarOffset | BadPlacement | …</summary>
	public string Reason { get; set; }

	/// <summary>Content vs Placement which aspect was marked incorrect.</summary>
	public string Aspect { get; set; }

	/// <summary>Rejected offset sheet feet when FarOffset / BadPlacement.</summary>
	public double RejectedOffsetSheetFeet { get; set; }

	public string HostKindA { get; set; }

	public string HostKindB { get; set; }

	public double SpanInches { get; set; }

	public int TeachCount { get; set; } = 1;

	public DateTime LastTaughtUtc { get; set; }
}

/// <summary>In-memory row for the Teach dialog list.</summary>
public sealed class TeachAutoDimListItem
{
	public long DimensionId { get; set; }

	public string DisplayLabel { get; set; }

	public string RoleKey { get; set; }

	public string PolicyRole { get; set; }

	public bool IsHorizontal { get; set; }

	public int OffsetSign { get; set; }

	public double OffsetSheetFeet { get; set; }

	public double SpanInches { get; set; }

	public string HostKindA { get; set; }

	public string HostKindB { get; set; }

	public string ProductHintA { get; set; }

	public string ProductHintB { get; set; }

	public long HostIdA { get; set; }

	public long HostIdB { get; set; }

	/// <summary>Content = anchors / span / role. Mutually exclusive with ContentIncorrect.</summary>
	public bool ContentCorrect { get; set; }

	public bool ContentIncorrect { get; set; }

	/// <summary>Placement = offset distance / side. Mutually exclusive with PlacementIncorrect.</summary>
	public bool PlacementCorrect { get; set; }

	public bool PlacementIncorrect { get; set; }

	public string IncorrectReason { get; set; }
}

public enum TeachAutoDimAction
{
	Open,
	Refresh,
	Finish,
	Close
}

public sealed class TeachAutoDimRequest
{
	public TeachAutoDimAction Action { get; set; }

	public List<long> ContentCorrectIds { get; set; } = new List<long>();

	public List<long> ContentIncorrectIds { get; set; } = new List<long>();

	public List<long> PlacementCorrectIds { get; set; } = new List<long>();

	public List<long> PlacementIncorrectIds { get; set; } = new List<long>();

	public Dictionary<string, string> IncorrectReasonsByDimId { get; set; } = new Dictionary<string, string>();
}

public sealed class TeachAutoDimReport
{
	public bool Success { get; set; }

	public string StatusMessage { get; set; }

	public string ViewName { get; set; }

	public string AssemblyName { get; set; }

	public List<TeachAutoDimListItem> Dimensions { get; set; } = new List<TeachAutoDimListItem>();

	public int PositiveLessonsWritten { get; set; }

	public int AntiPatternsWritten { get; set; }
}
