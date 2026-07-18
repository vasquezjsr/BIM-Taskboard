using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers
{
		/// <summary>
		/// Writes S-Hanger Size / S-Rod Length A|B / S-Strut Length from native fabrication data
		/// (Product Entry, FabricationRodInfo, fabrication dimensions / footprint) when sheets are built.
		/// </summary>
	internal static class FabricationHangerParameterSync
	{
		private const double LengthEpsilonFeet = 1e-4;
		private const double StrutTenInchesFeet = 10.0 / 12.0;
		private const double StrutTwelveInchesFeet = 1.0;

		internal static void SyncAssemblyHangers(Application app, Document doc, AssemblyInstance assembly)
		{
			if (doc == null || assembly == null)
				return;

			List<FabricationPart> hangers = assembly.GetMemberIds()
				.Select(id => doc.GetElement(id))
				.OfType<FabricationPart>()
				.Where(FabricationPartClassification.IsFabricationHanger)
				.ToList();

			if (hangers.Count == 0)
				return;

			EnsureHangerParameters(app ?? doc.Application, doc, hangers);

			foreach (FabricationPart hanger in hangers)
			{
				// Strut/Unistrut: rod + strut length only.
				// Clevis (and any non-strut hanger): hanger size + rod length — never skip size for Clevis.
				if (IsUnistrutHanger(hanger))
				{
					SyncRodLength(hanger);
					SyncStrutLength(hanger);
				}
				else
				{
					SyncHangerSize(hanger);
					SyncRodLength(hanger);
				}
			}
		}

		internal static void SyncHangers(IEnumerable<FabricationPart> hangers)
		{
			if (hangers == null)
				return;

			foreach (FabricationPart hanger in hangers)
			{
				if (!FabricationPartClassification.IsFabricationHanger(hanger))
					continue;

				if (IsUnistrutHanger(hanger))
				{
					SyncRodLength(hanger);
					SyncStrutLength(hanger);
				}
				else
				{
					SyncHangerSize(hanger);
					SyncRodLength(hanger);
				}
			}
		}

		private static void EnsureHangerParameters(Application app, Document doc, IList<FabricationPart> hangers)
		{
			if (app == null || doc == null || hangers == null || hangers.Count == 0)
				return;

			List<Category> categories = SsSavantSharedParameterEnsure.CollectBindableCategories(hangers);
			if (categories.Count == 0)
				return;

			SsSavantSharedParameterEnsure.EnsureInstanceParameter(
				app,
				doc,
				SsSavantSharedParameterBootstrap.SHangerSizeParameterName,
				SpecTypeId.String.Text,
				categories);
			SsSavantSharedParameterEnsure.EnsureInstanceParameter(
				app,
				doc,
				SsSavantSharedParameterBootstrap.SRodLengthAParameterName,
				SpecTypeId.Length,
				categories);
			SsSavantSharedParameterEnsure.EnsureInstanceParameter(
				app,
				doc,
				SsSavantSharedParameterBootstrap.SRodLengthBParameterName,
				SpecTypeId.Length,
				categories);
			SsSavantSharedParameterEnsure.EnsureInstanceParameter(
				app,
				doc,
				SsSavantSharedParameterBootstrap.SStrutLengthParameterName,
				SpecTypeId.Length,
				categories);
		}

		private static void SyncHangerSize(FabricationPart hanger)
		{
			string size = ResolveHangerSizeValue(hanger);
			if (string.IsNullOrWhiteSpace(size))
				return;

			SetTextParameter(hanger, SsSavantSharedParameterBootstrap.SHangerSizeParameterName, size.Trim());
		}

		private static string ResolveHangerSizeValue(FabricationPart hanger)
		{
			foreach (string name in new[] { "Product Entry", "Product Size Description", "Size" })
			{
				string value = ReadTextParameter(hanger, name);
				if (!string.IsNullOrWhiteSpace(value))
					return value;
			}

			// Type-level Product Entry when the instance parameter is blank.
			try
			{
				Element type = hanger?.Document?.GetElement(hanger.GetTypeId());
				if (type != null)
				{
					foreach (string name in new[] { "Product Entry", "Product Size Description", "Size" })
					{
						string value = ReadTextParameter(type, name);
						if (!string.IsNullOrWhiteSpace(value))
							return value;
					}
				}
			}
			catch
			{
			}

			try
			{
				string productSize = hanger?.Size;
				if (!string.IsNullOrWhiteSpace(productSize))
					return productSize;
			}
			catch
			{
			}

			return string.Empty;
		}

		private static void SyncRodLength(FabricationPart hanger)
		{
			TryResolveRodLengthA(hanger, out double rodA);
			TryResolveRodLengthB(hanger, out double rodB);

			if (rodA > LengthEpsilonFeet)
				SetLengthParameter(hanger, SsSavantSharedParameterBootstrap.SRodLengthAParameterName, rodA);
			if (rodB > LengthEpsilonFeet)
				SetLengthParameter(hanger, SsSavantSharedParameterBootstrap.SRodLengthBParameterName, rodB);
		}

		private static bool TryResolveRodLengthA(FabricationPart hanger, out double feet)
		{
			if (TryGetRodLengthFromRodInfoIndex(hanger, 0, out feet))
				return true;
			if (TryGetNamedFabricationDimensionFeet(hanger, "Length A", out feet)
				|| TryGetNamedFabricationDimensionFeet(hanger, "Rod Length A", out feet)
				|| TryGetNamedFabricationDimensionFeet(hanger, "Rod Length", out feet))
				return true;
			return TryReadLengthParameterFeet(hanger, "eM_Length A", out feet);
		}

		private static bool TryResolveRodLengthB(FabricationPart hanger, out double feet)
		{
			if (TryGetRodLengthFromRodInfoIndex(hanger, 1, out feet))
				return true;
			if (TryGetNamedFabricationDimensionFeet(hanger, "Length B", out feet)
				|| TryGetNamedFabricationDimensionFeet(hanger, "Rod Length B", out feet))
				return true;
			return TryReadLengthParameterFeet(hanger, "eM_Length B", out feet);
		}

		private static void SyncStrutLength(FabricationPart hanger)
		{
			if (TryResolveStrutLengthFeet(hanger, out double strutFeet)
				|| TryReadLengthParameterFeet(hanger, "eM_Supported Width", out strutFeet))
			{
				SetLengthParameter(hanger, SsSavantSharedParameterBootstrap.SStrutLengthParameterName, NormalizeStrutSpanFeet(strutFeet));
			}
		}

		/// <summary>
		/// Unistrut / strut hangers only. Clevis is never treated as strut (size must still sync).
		/// </summary>
		private static bool IsUnistrutHanger(FabricationPart part)
		{
			string corpus = string.Join(" ",
				ReadTextParameter(part, "Family"),
				ReadTextParameter(part, "Family and Type"),
				ReadTextParameter(part, "Product Long Description"),
				ReadTextParameter(part, "Product Name"),
				ReadTextParameter(part, "Alias"),
				((Element)part)?.Name ?? string.Empty).ToUpperInvariant();

			// Clevis always wins — never classify as strut, always allow S-Hanger Size.
			if (corpus.IndexOf("CLEVIS", StringComparison.Ordinal) >= 0)
				return false;

			return corpus.IndexOf("UNISTRUT", StringComparison.Ordinal) >= 0
				|| corpus.IndexOf("UNI-STRUT", StringComparison.Ordinal) >= 0
				|| corpus.IndexOf("UNI STRUT", StringComparison.Ordinal) >= 0
				|| corpus.IndexOf("TRAPEZE", StringComparison.Ordinal) >= 0
				|| corpus.IndexOf("STRUT", StringComparison.Ordinal) >= 0;
		}

		private static bool TryGetRodLengthFromRodInfoIndex(FabricationPart hanger, int rodIndex, out double feet)
		{
			feet = 0.0;
			try
			{
				FabricationRodInfo rodInfo = hanger.GetRodInfo();
				if (rodInfo == null || !rodInfo.IsValidObject)
					return false;

				if (rodIndex < 0 || rodIndex >= rodInfo.RodCount)
					return false;

				double length = rodInfo.GetRodLength(rodIndex);
				if (length <= LengthEpsilonFeet)
					return false;

				feet = length;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool TryResolveStrutLengthFeet(FabricationPart hanger, out double widthFeet)
		{
			widthFeet = 0.0;
			if (hanger == null)
				return false;

			double fromFab = 0.0;
			bool hasFab = TryGetFabricationStrutDimensionFeet(hanger, out fromFab);

			// Prefer an explicit Supported Width fabrication dim (native equivalent of eM_Supported Width).
			if (TryGetNamedFabricationDimensionFeet(hanger, "Supported Width", out double supportedWidth)
				&& supportedWidth > LengthEpsilonFeet)
			{
				widthFeet = supportedWidth;
				return true;
			}

			double fromBbox = 0.0;
			bool hasBbox = TryGetHorizontalBoundingWidthFeet(hanger, out fromBbox);

			if (!hasFab && !hasBbox)
				return false;

			widthFeet = Math.Max(fromFab, fromBbox);
			return widthFeet > LengthEpsilonFeet;
		}

		private static bool TryGetNamedFabricationDimensionFeet(FabricationPart hanger, string dimensionName, out double feet)
		{
			feet = 0.0;
			try
			{
				IList<FabricationDimensionDefinition> dims = hanger.GetDimensions();
				if (dims == null)
					return false;

				foreach (FabricationDimensionDefinition dim in dims)
				{
					if (dim == null || !dim.IsValidObject || dim.UnitType != FabricationDimensionUnitType.Linear)
						continue;

					if (!string.Equals(dim.Name, dimensionName, StringComparison.OrdinalIgnoreCase))
						continue;

					double value = hanger.GetDimensionValue(dim);
					if (value <= LengthEpsilonFeet)
						continue;

					feet = value;
					return true;
				}
			}
			catch
			{
				return false;
			}

			return false;
		}

		private static bool TryGetFabricationStrutDimensionFeet(FabricationPart hanger, out double widthFeet)
		{
			widthFeet = 0.0;
			try
			{
				double best = 0.0;
				foreach (FabricationDimensionDefinition dimension in OrderedStrutWidthDimensions(hanger))
				{
					double value = hanger.GetDimensionValue(dimension);
					if (value > best)
						best = value;
				}

				if (best <= LengthEpsilonFeet)
					return false;

				widthFeet = best;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static IReadOnlyList<FabricationDimensionDefinition> OrderedStrutWidthDimensions(FabricationPart hanger)
		{
			if (hanger == null)
				return Array.Empty<FabricationDimensionDefinition>();

			IList<FabricationDimensionDefinition> dims;
			try
			{
				dims = hanger.GetDimensions();
			}
			catch
			{
				return Array.Empty<FabricationDimensionDefinition>();
			}

			if (dims == null || dims.Count == 0)
				return Array.Empty<FabricationDimensionDefinition>();

			var ranked = new List<(int tier, int index, FabricationDimensionDefinition d)>();
			for (int i = 0; i < dims.Count; i++)
			{
				FabricationDimensionDefinition d = dims[i];
				if (d == null || !d.IsValidObject || d.UnitType != FabricationDimensionUnitType.Linear)
					continue;

				int tier = int.MaxValue;
				if (d.Type == FabricationDimensionType.Width)
					tier = 0;
				else if (string.Equals(d.Name, "Width", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(d.Name, "Supported Width", StringComparison.OrdinalIgnoreCase))
					tier = 1;
				else if (IsStrutWidthDimensionName(d.Name))
					tier = 2;

				if (tier != int.MaxValue)
					ranked.Add((tier, i, d));
			}

			ranked.Sort((a, b) =>
			{
				int c = a.tier.CompareTo(b.tier);
				return c != 0 ? c : a.index.CompareTo(b.index);
			});

			return ranked.Select(entry => entry.d).ToList();
		}

		private static bool IsStrutWidthDimensionName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return false;

			string normalized = name.Trim().ToLowerInvariant();
			if (normalized.Contains("rod")
				|| normalized.Contains("height")
				|| normalized.Contains("extension")
				|| normalized.Contains("elevation"))
			{
				return false;
			}

			return normalized == "w"
				|| normalized == "width"
				|| normalized == "l"
				|| normalized == "length"
				|| normalized.Contains("strut")
				|| normalized.Contains("channel")
				|| normalized.Contains("supported");
		}

		private static bool TryGetHorizontalBoundingWidthFeet(FabricationPart hanger, out double widthFeet)
		{
			widthFeet = 0.0;
			BoundingBoxXYZ boundingBox = hanger.get_BoundingBox(null);
			if (boundingBox == null)
				return false;

			Transform transform = boundingBox.Transform ?? Transform.Identity;
			XYZ[] points =
			{
				new XYZ(boundingBox.Min.X, boundingBox.Min.Y, boundingBox.Min.Z),
				new XYZ(boundingBox.Min.X, boundingBox.Min.Y, boundingBox.Max.Z),
				new XYZ(boundingBox.Min.X, boundingBox.Max.Y, boundingBox.Min.Z),
				new XYZ(boundingBox.Min.X, boundingBox.Max.Y, boundingBox.Max.Z),
				new XYZ(boundingBox.Max.X, boundingBox.Min.Y, boundingBox.Max.Z),
				new XYZ(boundingBox.Max.X, boundingBox.Min.Y, boundingBox.Min.Z),
				new XYZ(boundingBox.Max.X, boundingBox.Max.Y, boundingBox.Min.Z),
				new XYZ(boundingBox.Max.X, boundingBox.Max.Y, boundingBox.Max.Z)
			};

			double best = 0.0;
			for (int i = 0; i < points.Length; i++)
			{
				XYZ a = transform.OfPoint(points[i]);
				for (int j = i + 1; j < points.Length; j++)
				{
					XYZ b = transform.OfPoint(points[j]);
					double dx = a.X - b.X;
					double dy = a.Y - b.Y;
					double chord = Math.Sqrt(dx * dx + dy * dy);
					if (chord > best)
						best = chord;
				}
			}

			if (best <= LengthEpsilonFeet)
				return false;

			widthFeet = best;
			return true;
		}

		private static double NormalizeStrutSpanFeet(double rawFeet)
		{
			if (rawFeet <= 0.0)
				return rawFeet;

			const double tol = 1e-9;
			if (rawFeet > StrutTenInchesFeet + tol && rawFeet <= StrutTwelveInchesFeet + tol)
				return StrutTwelveInchesFeet;

			return rawFeet;
		}

		private static bool TryReadLengthParameterFeet(Element element, string parameterName, out double feet)
		{
			feet = 0.0;
			Parameter parameter = element?.LookupParameter(parameterName);
			if (parameter == null || !parameter.HasValue)
				return false;

			if (parameter.StorageType == StorageType.Double)
			{
				feet = parameter.AsDouble();
				return feet > LengthEpsilonFeet;
			}

			string text = parameter.AsValueString() ?? parameter.AsString();
			return TryParseLengthFeet(text, out feet) && feet > LengthEpsilonFeet;
		}

		private static bool TryParseLengthFeet(string text, out double feet)
		{
			feet = 0.0;
			if (string.IsNullOrWhiteSpace(text))
				return false;

			string trimmed = text.Trim().Replace(" ", string.Empty);
			if (trimmed.EndsWith("\"", StringComparison.Ordinal) || trimmed.EndsWith("in", StringComparison.OrdinalIgnoreCase))
			{
				string number = trimmed.TrimEnd('"');
				if (number.EndsWith("in", StringComparison.OrdinalIgnoreCase))
					number = number.Substring(0, number.Length - 2);
				if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double inches)
					|| double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out inches))
				{
					feet = inches / 12.0;
					return feet > LengthEpsilonFeet;
				}
			}

			if (trimmed.EndsWith("'", StringComparison.Ordinal) || trimmed.EndsWith("ft", StringComparison.OrdinalIgnoreCase))
			{
				string number = trimmed.TrimEnd('\'');
				if (number.EndsWith("ft", StringComparison.OrdinalIgnoreCase))
					number = number.Substring(0, number.Length - 2);
				if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double ft)
					|| double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out ft))
				{
					feet = ft;
					return feet > LengthEpsilonFeet;
				}
			}

			return false;
		}

		private static string ReadTextParameter(Element element, string parameterName)
		{
			Parameter parameter = element?.LookupParameter(parameterName);
			if (parameter == null)
				return string.Empty;

			string value = parameter.AsString();
			if (!string.IsNullOrWhiteSpace(value))
				return value;

			return parameter.AsValueString() ?? string.Empty;
		}

		private static void SetTextParameter(Element element, string parameterName, string value)
		{
			Parameter parameter = element?.LookupParameter(parameterName);
			if (parameter == null || parameter.IsReadOnly)
				return;

			string normalized = value ?? string.Empty;
			if (parameter.StorageType == StorageType.String)
			{
				string current = parameter.AsString() ?? string.Empty;
				if (string.Equals(current, normalized, StringComparison.Ordinal))
					return;

				try
				{
					if (parameter.Set(normalized))
						return;
				}
				catch
				{
				}

				try
				{
					parameter.SetValueString(normalized);
				}
				catch
				{
				}
				return;
			}

			try
			{
				parameter.SetValueString(normalized);
			}
			catch
			{
			}
		}

		private static void SetLengthParameter(Element element, string parameterName, double valueFeet)
		{
			if (element == null || string.IsNullOrWhiteSpace(parameterName) || valueFeet <= LengthEpsilonFeet)
				return;

			Parameter parameter = element.LookupParameter(parameterName);
			if (parameter == null || parameter.IsReadOnly)
				return;

			if (parameter.StorageType == StorageType.Double)
			{
				if (parameter.HasValue && Math.Abs(parameter.AsDouble() - valueFeet) < 1e-6)
					return;

				// Sub-foot lengths: prefer value-string so Properties shows 4" instead of 0'-4".
				if (valueFeet > 0.0 && valueFeet < 1.0 - 1e-12)
				{
					try
					{
						string inchesText = (valueFeet * 12.0).ToString("0.###", CultureInfo.InvariantCulture) + "\"";
						parameter.SetValueString(inchesText);
						return;
					}
					catch
					{
					}
				}

				parameter.Set(valueFeet);
				return;
			}

			if (parameter.StorageType == StorageType.String)
			{
				string text = FormatLengthDisplay(valueFeet);
				string current = parameter.AsString() ?? string.Empty;
				if (string.Equals(current, text, StringComparison.Ordinal))
					return;
				parameter.Set(text);
			}
		}

		private static string FormatLengthDisplay(double feet)
		{
			if (feet < 1.0 - 1e-12)
				return (feet * 12.0).ToString("0.###", CultureInfo.InvariantCulture) + "\"";

			int wholeFeet = (int)Math.Floor(feet + 1e-9);
			double inches = (feet - wholeFeet) * 12.0;
			if (inches < 1e-6)
				return wholeFeet.ToString(CultureInfo.InvariantCulture) + "'";

			return wholeFeet.ToString(CultureInfo.InvariantCulture) + "'-"
				+ inches.ToString("0.###", CultureInfo.InvariantCulture) + "\"";
		}
	}
}
