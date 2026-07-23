using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;

namespace SpoolingSavantV3Exports.Workers
{
	/// <summary>Syncs native Revit pipework shared parameters from built-in values.</summary>
	public static class NativePipeParameterSync
	{
		public static void SyncSLengthForDocument(Document doc)
		{
			if (doc == null)
				return;

			foreach (BuiltInCategory bic in new[]
			         {
				         BuiltInCategory.OST_PipeCurves,
				         BuiltInCategory.OST_PipeFitting,
				         BuiltInCategory.OST_PipeAccessory
			         })
			{
				Category category = Category.GetCategory(doc, bic);
				if (category == null)
					continue;

				foreach (Element element in new FilteredElementCollector(doc)
					         .OfCategoryId(category.Id)
					         .WhereElementIsNotElementType())
				{
					TryCopyLengthToSLength(element);
					TryCopySizeToSSize(element);
				}
			}
		}

		public static void SyncSLengthForElements(IEnumerable<Element> elements)
		{
			if (elements == null)
				return;

			foreach (Element element in elements)
			{
				TryCopyLengthToSLength(element);
				TryCopySizeToSSize(element);
			}
		}

		/// <summary>
		/// Native families report their size on the built-in Size parameter (e.g. 6" pipes,
		/// 6"-6" fittings) — copy that to S-Size instead of the Fabrication Product Entry logic.
		/// </summary>
		private static void TryCopySizeToSSize(Element element)
		{
			if (element == null || !NativePipeSpoolSupport.IsNativePipeworkElement(element))
				return;

			Parameter sSize = element.LookupParameter(SsSavantSharedParameterBootstrap.SSizeParameterName);
			if (sSize == null || sSize.IsReadOnly || sSize.StorageType != StorageType.String)
				return;

			string size = TryReadElementSizeText(element);
			if (string.IsNullOrWhiteSpace(size))
				return;

			try
			{
				string current = sSize.AsString();
				if (!string.Equals(current, size, StringComparison.Ordinal))
					sSize.Set(size);
			}
			catch
			{
			}
		}

		private static string TryReadElementSizeText(Element element)
		{
			try
			{
				Parameter builtIn = element.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE);
				if (builtIn != null && builtIn.HasValue)
				{
					string value = builtIn.AsString();
					if (string.IsNullOrWhiteSpace(value))
						value = builtIn.AsValueString();
					if (!string.IsNullOrWhiteSpace(value))
						return value.Trim();
				}
			}
			catch
			{
			}

			try
			{
				Parameter named = element.LookupParameter("Size");
				if (named != null && named.HasValue)
				{
					string value = named.AsString();
					if (string.IsNullOrWhiteSpace(value))
						value = named.AsValueString();
					if (!string.IsNullOrWhiteSpace(value))
						return value.Trim();
				}
			}
			catch
			{
			}

			return string.Empty;
		}

		private static void TryCopyLengthToSLength(Element element)
		{
			if (element == null || !NativePipeSpoolSupport.IsNativePipeworkElement(element))
				return;

			Parameter sLength = element.LookupParameter(SsSavantSharedParameterBootstrap.SLengthParameterName);
			if (sLength == null || sLength.IsReadOnly || sLength.StorageType != StorageType.Double)
				return;

			double feet = TryReadElementLengthFeet(element);
			if (feet < 0)
				return;

			try
			{
				sLength.Set(feet);
			}
			catch
			{
			}
		}

		private static double TryReadElementLengthFeet(Element element)
		{
			try
			{
				Parameter builtIn = element.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
				if (builtIn != null && builtIn.StorageType == StorageType.Double && builtIn.HasValue)
					return builtIn.AsDouble();
			}
			catch
			{
			}

			try
			{
				Parameter named = element.LookupParameter("Length");
				if (named != null && named.StorageType == StorageType.Double && named.HasValue)
					return named.AsDouble();
			}
			catch
			{
			}

			return -1;
		}
	}
}
