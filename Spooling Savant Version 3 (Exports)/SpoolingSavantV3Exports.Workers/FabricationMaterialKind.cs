using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers
{
	/// <summary>Material family for TigerStop / PCF plot-package splits.</summary>
	public enum FabricationMaterialFamily
	{
		Unknown = 0,
		Copper = 1,
		Pvc = 2,
		Steel = 3,
		CastIron = 4
	}

	/// <summary>
	/// Resolves Copper / PVC / Steel / Cast Iron from fabrication material fields.
	/// Carbon Steel (CS / S-Material / Product Material Description) always wins over
	/// ambiguous Cast Iron keyword hits.
	/// </summary>
	public static class FabricationMaterialKind
	{
		public const string DefaultCopperKeywords = "COPPER, CU";
		public const string DefaultPvcKeywords = "PVC, CPVC";
		public const string DefaultSteelKeywords = "STEEL, CARBON STEEL, STAINLESS, SS, CS";
		// No bare "CI" / "DUCTILE" — those false-match catalog text. Use abbr CI via ResolveAbbreviation.
		public const string DefaultCastIronKeywords = "CAST IRON, DUCTILE IRON, GRAY IRON, GREY IRON";

		private static readonly Regex NonToken = new Regex(@"[^A-Z0-9]+", RegexOptions.Compiled);

		public static string DisplayName(FabricationMaterialFamily family)
		{
			return family switch
			{
				FabricationMaterialFamily.Copper => "Copper",
				FabricationMaterialFamily.Pvc => "PVC",
				FabricationMaterialFamily.Steel => "Steel",
				FabricationMaterialFamily.CastIron => "Cast Iron",
				_ => "Unknown"
			};
		}

		public static string GetRawMaterialText(FabricationPart part, Document doc = null)
		{
			if (part == null)
			{
				return string.Empty;
			}

			doc = doc ?? ((Element)part).Document;
			foreach (string name in new[]
			{
				"S-Material",
				"Product Material Description",
				"Part Material",
				"Material"
			})
			{
				string value = CleanMaterialValue(FabricationPartClassification.GetParamString(part, doc, name));
				if (!string.IsNullOrWhiteSpace(value))
				{
					return value;
				}
			}

			return string.Empty;
		}

		public static FabricationMaterialFamily Resolve(
			FabricationPart part,
			Document doc,
			IEnumerable<string> copperKeywords,
			IEnumerable<string> pvcKeywords,
			IEnumerable<string> steelKeywords,
			IEnumerable<string> castIronKeywords)
		{
			if (part == null)
			{
				return FabricationMaterialFamily.Unknown;
			}

			doc = doc ?? ((Element)part).Document;

			string abbr = (FabricationPartClassification.GetParamString(part, doc, "Material Abbreviation") ?? string.Empty)
				.Trim()
				.ToUpperInvariant();
			string material = (GetRawMaterialText(part, doc) ?? string.Empty).ToUpperInvariant();
			string corpus = (abbr + " " + material + " " + FabricationPartClassification.GetExpandedSearchCorpus(part, doc))
				.ToUpperInvariant();

			// Hard rules first — never let keyword noise override clear CS / Carbon Steel.
			if (IsExplicitCarbonSteel(abbr, material, corpus))
			{
				return FabricationMaterialFamily.Steel;
			}

			if (IsExplicitCastIron(abbr, material, corpus))
			{
				return FabricationMaterialFamily.CastIron;
			}

			FabricationMaterialFamily fromAbbr = ResolveAbbreviation(abbr);
			if (fromAbbr != FabricationMaterialFamily.Unknown)
			{
				return fromAbbr;
			}

			if (!string.IsNullOrWhiteSpace(material))
			{
				FabricationMaterialFamily fromMaterial = ResolveFromCorpus(
					material,
					SanitizeCopperKeywords(copperKeywords),
					SanitizePvcKeywords(pvcKeywords),
					SanitizeSteelKeywords(steelKeywords),
					SanitizeCastIronKeywords(castIronKeywords));
				if (fromMaterial != FabricationMaterialFamily.Unknown)
				{
					return fromMaterial;
				}
			}

			if (string.IsNullOrWhiteSpace(corpus))
			{
				return FabricationMaterialFamily.Unknown;
			}

			return ResolveFromCorpus(
				corpus,
				SanitizeCopperKeywords(copperKeywords),
				SanitizePvcKeywords(pvcKeywords),
				SanitizeSteelKeywords(steelKeywords),
				SanitizeCastIronKeywords(castIronKeywords));
		}

		/// <summary>
		/// Upgrade legacy keyword lists that false-match Carbon Steel parts into Cast Iron PCFs.
		/// </summary>
		public static string SanitizeCastIronKeywordsSetting(string raw)
		{
			List<string> parsed = ParseKeywords(raw, DefaultCastIronKeywords);
			var safe = new List<string>();
			foreach (string keyword in parsed)
			{
				string key = keyword.Trim().ToUpperInvariant();
				if (key == "CI" || key == "DUCTILE" || key == "DI")
				{
					continue;
				}

				safe.Add(keyword.Trim());
			}

			if (safe.Count == 0)
			{
				return DefaultCastIronKeywords;
			}

			return string.Join(", ", safe);
		}

		private static bool IsExplicitCarbonSteel(string abbr, string material, string corpus)
		{
			string token = string.IsNullOrWhiteSpace(abbr)
				? string.Empty
				: abbr.Split(new[] { '-', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
			if (token == "CS" || token == "SS")
			{
				return true;
			}

			if (ContainsPhrase(material, "CARBON STEEL") || ContainsPhrase(corpus, "CARBON STEEL"))
			{
				return true;
			}

			// "STEEL" without cast-iron markers.
			if ((ContainsToken(material, "STEEL") || ContainsToken(corpus, "STEEL"))
				&& !IsExplicitCastIron(abbr, material, corpus))
			{
				// Avoid treating stainless-only phrases oddly — stainless is still steel family.
				return true;
			}

			return false;
		}

		private static bool IsExplicitCastIron(string abbr, string material, string corpus)
		{
			string token = string.IsNullOrWhiteSpace(abbr)
				? string.Empty
				: abbr.Split(new[] { '-', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
			if (token == "CI" || token == "DI")
			{
				// Abbreviation CI/DI only counts when material text is not carbon steel.
				if (ContainsPhrase(material, "CARBON STEEL") || token == "CS")
				{
					return false;
				}

				return true;
			}

			return ContainsPhrase(material, "CAST IRON")
				|| ContainsPhrase(corpus, "CAST IRON")
				|| ContainsPhrase(material, "DUCTILE IRON")
				|| ContainsPhrase(corpus, "DUCTILE IRON")
				|| ContainsPhrase(material, "GRAY IRON")
				|| ContainsPhrase(corpus, "GRAY IRON")
				|| ContainsPhrase(material, "GREY IRON")
				|| ContainsPhrase(corpus, "GREY IRON");
		}

		private static FabricationMaterialFamily ResolveAbbreviation(string abbr)
		{
			if (string.IsNullOrWhiteSpace(abbr))
			{
				return FabricationMaterialFamily.Unknown;
			}

			string token = abbr.Split(new[] { '-', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
			switch (token)
			{
				case "CS":
				case "SS":
				case "AS":
					return FabricationMaterialFamily.Steel;
				case "CI":
				case "DI":
					return FabricationMaterialFamily.CastIron;
				case "CU":
					return FabricationMaterialFamily.Copper;
				case "PVC":
				case "CPVC":
					return FabricationMaterialFamily.Pvc;
				default:
					return FabricationMaterialFamily.Unknown;
			}
		}

		private static FabricationMaterialFamily ResolveFromCorpus(
			string corpusUpper,
			IEnumerable<string> copperKeywords,
			IEnumerable<string> pvcKeywords,
			IEnumerable<string> steelKeywords,
			IEnumerable<string> castIronKeywords)
		{
			// Steel before cast-iron keyword fallthrough — CS parts must not become Cast Iron PCFs.
			if (MatchesAny(corpusUpper, steelKeywords))
			{
				return FabricationMaterialFamily.Steel;
			}

			if (MatchesAny(corpusUpper, copperKeywords))
			{
				return FabricationMaterialFamily.Copper;
			}

			if (MatchesAny(corpusUpper, pvcKeywords))
			{
				return FabricationMaterialFamily.Pvc;
			}

			if (MatchesAny(corpusUpper, castIronKeywords))
			{
				return FabricationMaterialFamily.CastIron;
			}

			return FabricationMaterialFamily.Unknown;
		}

		public static List<string> ParseKeywords(string raw, string defaults)
		{
			string source = string.IsNullOrWhiteSpace(raw) ? defaults : raw;
			return source
				.Split(new[] { ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(token => token.Trim())
				.Where(token => token.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		public static string CleanMaterialValue(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return string.Empty;
			}

			string result = value.Trim();
			int colon = result.IndexOf(':');
			if (colon >= 0 && colon < result.Length - 1)
			{
				result = result.Substring(colon + 1);
			}

			return result.Trim();
		}

		private static IEnumerable<string> SanitizeCastIronKeywords(IEnumerable<string> keywords)
		{
			return ParseKeywords(SanitizeCastIronKeywordsSetting(string.Join(", ", keywords ?? Array.Empty<string>())), DefaultCastIronKeywords);
		}

		private static IEnumerable<string> SanitizeSteelKeywords(IEnumerable<string> keywords)
		{
			List<string> list = (keywords ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
			return list.Count > 0 ? list : ParseKeywords(null, DefaultSteelKeywords);
		}

		private static IEnumerable<string> SanitizeCopperKeywords(IEnumerable<string> keywords)
		{
			List<string> list = (keywords ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
			return list.Count > 0 ? list : ParseKeywords(null, DefaultCopperKeywords);
		}

		private static IEnumerable<string> SanitizePvcKeywords(IEnumerable<string> keywords)
		{
			List<string> list = (keywords ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
			return list.Count > 0 ? list : ParseKeywords(null, DefaultPvcKeywords);
		}

		private static bool ContainsPhrase(string corpusUpper, string phrase)
		{
			if (string.IsNullOrWhiteSpace(corpusUpper) || string.IsNullOrWhiteSpace(phrase))
			{
				return false;
			}

			string padded = " " + NonToken.Replace(corpusUpper.ToUpperInvariant(), " ") + " ";
			string needle = " " + NonToken.Replace(phrase.ToUpperInvariant(), " ") + " ";
			while (padded.Contains("  "))
			{
				padded = padded.Replace("  ", " ");
			}

			while (needle.Contains("  "))
			{
				needle = needle.Replace("  ", " ");
			}

			return padded.IndexOf(needle, StringComparison.Ordinal) >= 0;
		}

		private static bool ContainsToken(string corpusUpper, string token)
		{
			if (string.IsNullOrWhiteSpace(corpusUpper) || string.IsNullOrWhiteSpace(token))
			{
				return false;
			}

			HashSet<string> tokens = new HashSet<string>(
				NonToken.Split(corpusUpper.ToUpperInvariant()).Where(t => t.Length > 0),
				StringComparer.Ordinal);
			return tokens.Contains(token.Trim().ToUpperInvariant());
		}

		private static bool MatchesAny(string corpusUpper, IEnumerable<string> keywords)
		{
			if (string.IsNullOrWhiteSpace(corpusUpper) || keywords == null)
			{
				return false;
			}

			HashSet<string> tokens = new HashSet<string>(
				NonToken.Split(corpusUpper).Where(t => t.Length > 0),
				StringComparer.Ordinal);

			foreach (string keyword in keywords)
			{
				if (string.IsNullOrWhiteSpace(keyword))
				{
					continue;
				}

				string key = keyword.Trim().ToUpperInvariant();
				if (key.IndexOf(' ') >= 0)
				{
					if (ContainsPhrase(corpusUpper, key))
					{
						return true;
					}

					continue;
				}

				if (tokens.Contains(key))
				{
					return true;
				}
			}

			return false;
		}
	}
}
