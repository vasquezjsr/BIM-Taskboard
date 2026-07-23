using System;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers.SpoolingManager.Services
{
	/// <summary>Helpers for native Revit Pipe / Pipe Fitting / Pipe Accessory spooling mode.</summary>
	internal static class NativePipeSpoolSupport
	{
		// BuiltInCategory values (Revit API).
		internal const long PipeCategoryId = -2008044L;           // OST_PipeCurves
		internal const long PipeFittingCategoryId = -2008049L;    // OST_PipeFitting
		internal const long PipeAccessoryCategoryId = -2008055L;  // OST_PipeAccessory
		internal const long PipeTagsCategoryId = -2000480L;       // OST_PipeTags
		internal const long PipeFittingTagsCategoryId = -2000485L;// OST_PipeFittingTags
		internal const long PipeAccessoryTagsCategoryId = -2000488L; // OST_PipeAccessoryTags
		internal const long MultiCategoryTagsId = -2000265L;      // OST_MultiCategoryTags (common)

		internal static bool IsNativePipeworkElement(Element element)
		{
			if (element?.Category == null)
				return false;

			try
			{
				long id = element.Category.Id.Value;
				return id == PipeCategoryId
					|| id == PipeFittingCategoryId
					|| id == PipeAccessoryCategoryId;
			}
			catch
			{
				return false;
			}
		}

		internal static bool IsNativePipe(Element element)
		{
			return CategoryIdEquals(element, PipeCategoryId);
		}

		internal static bool IsNativePipeFitting(Element element)
		{
			return CategoryIdEquals(element, PipeFittingCategoryId);
		}

		internal static bool IsNativePipeAccessory(Element element)
		{
			return CategoryIdEquals(element, PipeAccessoryCategoryId);
		}

		internal static bool IsNativePipeFittingOrAccessory(Element element)
		{
			return IsNativePipeFitting(element) || IsNativePipeAccessory(element);
		}

		internal static bool CategoryMatchesNativePipeTags(Category category)
		{
			if (category == null)
				return false;

			try
			{
				long id = category.Id.Value;
				if (id == PipeTagsCategoryId
					|| id == PipeFittingTagsCategoryId
					|| id == PipeAccessoryTagsCategoryId
					|| id == MultiCategoryTagsId)
				{
					return true;
				}
			}
			catch
			{
			}

			string name = category.Name ?? string.Empty;
			if (name.IndexOf("Multi-Category", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("Multi Category", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}

			if (name.IndexOf("Pipe Accessory Tags", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
			if (name.IndexOf("Pipe Fitting Tags", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
			if (name.IndexOf("Pipe Tags", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;

			return false;
		}

		private static bool CategoryIdEquals(Element element, long categoryId)
		{
			if (element?.Category == null)
				return false;

			try
			{
				return element.Category.Id.Value == categoryId;
			}
			catch
			{
				return false;
			}
		}
	}
}
