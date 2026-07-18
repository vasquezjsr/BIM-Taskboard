using System;
using Autodesk.Revit.DB;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Models;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

/// <summary>
/// Below-line witness labels (E-C, F-F, C-C, etc.) on auto-placed Linear dimensions.
/// Controlled by <see cref="Models.SpoolingManagerSettings.AutoDimAnnotations"/>.
/// </summary>
public partial class CreateSpoolSheetsHandler
{
	private static string FormatSpoolDimRuleAnnotation(FabricationDimensionRefRole fromRole, FabricationDimensionRefRole toRole)
	{
		return FormatSpoolDimAnnotationLetter(fromRole) + "-" + FormatSpoolDimAnnotationLetter(toRole);
	}

	private static string FormatSpoolDimAnnotationLetter(FabricationDimensionRefRole role)
	{
		return role switch
		{
			FabricationDimensionRefRole.FlangeFace => "F",
			FabricationDimensionRefRole.PipeOpenEnd => "E",
			FabricationDimensionRefRole.VerticalDropEnd => "E",
			FabricationDimensionRefRole.PipeCenterline => "CL",
			FabricationDimensionRefRole.OletBranch => "C",
			_ => "C"
		};
	}

	private static string FormatSpoolDimensionWitnessPair(SpoolDimensionWitnessLetter from, SpoolDimensionWitnessLetter to)
	{
		return ToWitnessLetterChar(from) + "-" + ToWitnessLetterChar(to);
	}

	private static string ToWitnessLetterChar(SpoolDimensionWitnessLetter letter)
	{
		return letter switch
		{
			SpoolDimensionWitnessLetter.F => "F",
			SpoolDimensionWitnessLetter.C => "C",
			SpoolDimensionWitnessLetter.E => "E",
			_ => "?"
		};
	}

	private static bool IsValidSpoolDimWitnessLabel(string label)
	{
		if (string.IsNullOrWhiteSpace(label))
		{
			return false;
		}
		return !label.Contains("?", StringComparison.Ordinal)
			&& !label.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
			&& !label.Contains("Disallowed", StringComparison.OrdinalIgnoreCase);
	}

	private static void TryApplySpoolAutoDimensionBelowLabel(
		Document doc,
		View view,
		Dimension dim,
		SpoolingManagerSettings settings,
		FabricationDimensionRefRole? witnessRoleA = null,
		FabricationDimensionRefRole? witnessRoleB = null)
	{
		if (settings?.AutoDimAnnotations != true || doc == null || dim == null)
		{
			return;
		}

		try
		{
			ReferenceArray references = dim.References;
			if (dim.Segments != null && dim.Segments.Size > 1 && references != null && references.Size > 1)
			{
				int segmentCount = Math.Min(dim.Segments.Size, references.Size - 1);
				for (int i = 0; i < segmentCount; i++)
				{
					DimensionSegment segment = dim.Segments.get_Item(i);
					if (segment == null)
					{
						continue;
					}
					SpoolWitnessClassification w0 = ClassifyDimensionWitness(doc, references.get_Item(i));
					SpoolWitnessClassification w1 = ClassifyDimensionWitness(doc, references.get_Item(i + 1));
					string segmentLabel = FormatSpoolDimensionWitnessPair(w0.Letter, w1.Letter);
					if (!IsValidSpoolDimWitnessLabel(segmentLabel))
					{
						continue;
					}
					segment.Below = segmentLabel;
					TryAppendAutoDimDiagnosticLog(
						"dim-annotation",
						view?.Name ?? "?",
						"id=" + ((Element)dim).Id.Value + " seg=" + i + " below=\"" + segmentLabel + "\"",
						0,
						0);
				}
				return;
			}
		}
		catch
		{
		}

		string label = null;
		if (witnessRoleA.HasValue && witnessRoleB.HasValue)
		{
			label = FormatSpoolDimRuleAnnotation(witnessRoleA.Value, witnessRoleB.Value);
		}
		else
		{
			SpoolDimensionPatternClassification pattern = ClassifyDimensionPattern(doc, dim);
			label = pattern?.PatternLabel;
		}

		if (!IsValidSpoolDimWitnessLabel(label))
		{
			return;
		}

		TryApplySpoolDimensionBelowAnnotation(doc, view, dim, label);
	}

	private static bool TryApplySpoolDimensionBelowAnnotation(Document doc, View view, Dimension dim, string belowText)
	{
		if (dim == null || string.IsNullOrWhiteSpace(belowText))
		{
			return false;
		}
		try
		{
			if (dim.Segments != null && dim.Segments.Size > 0)
			{
				foreach (DimensionSegment segment in dim.Segments)
				{
					if (segment != null)
					{
						segment.Below = belowText;
					}
				}
			}
			else
			{
				dim.Below = belowText;
			}
			TryAppendAutoDimDiagnosticLog(
				"dim-annotation",
				view?.Name ?? "?",
				"id=" + ((Element)dim).Id.Value + " below=\"" + belowText + "\"",
				0,
				0);
			return true;
		}
		catch (Exception ex)
		{
			try
			{
				TryAppendAutoDimDiagnosticLog(
					"dim-annotation",
					view?.Name ?? "?",
					"FAIL id=" + ((Element)dim).Id.Value + " " + ex.Message,
					0,
					0);
			}
			catch
			{
			}
			return false;
		}
	}
}
