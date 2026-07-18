using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers
{
    /// <summary>Shared fabrication part classification for BOM, connector sync, and spool sheets.</summary>
    public static class FabricationPartClassification
    {
        private const string ProductLongDescriptionParameterName = "Product Long Description";

        /// <summary>Straight pipe runs only: excludes welds, joints, fittings, valves, gaskets, and bolt kits.</summary>
        public static bool IsStraightPipeRun(FabricationPart part)
        {
            if (part == null)
                return false;

            if (IsWeldPart(part))
                return false;

            if (IsJointPart(part))
                return false;

            if (IsGasketPart(part) || IsBoltKitPart(part))
                return false;

            return GetFabricationSortPriority(part) == 0;
        }

        public static bool IsOletPart(FabricationPart part)
        {
            if (part == null)
                return false;

            string combined = GetClassificationText(part);
            // "Thread O Let" / "Thread-O-Let" do not contain contiguous "OLET" until spaces/hyphens are removed.
            // That miss dropped every CRAH Thread-O-Let from auto-dim and learning-loop audits.
            string compact = combined
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
            if (compact.Contains("ANVILET"))
                return true;
            // Butt-weld / socket outlets share olet/anvilet rules (OUTLET ≠ OLET substring — must check explicitly).
            if (compact.Contains("OUTLET"))
                return true;
            if (compact.Contains("OLET")
                || compact.Contains("WELDOLET")
                || compact.Contains("THREADOLET")
                || compact.Contains("SOCKOLET")
                || compact.Contains("NIPOLET")
                || compact.Contains("ELBOWLET")
                || compact.Contains("LATROLET"))
            {
                return true;
            }
            // Common fabrication Alias for Thread-O-Let.
            string alias = GetParamString(part, "Alias").ToUpperInvariant().Trim();
            return alias == "TOL" || alias == "WOL" || alias == "SOL" || alias == "NOL";
        }

        public static bool IsWeldPart(FabricationPart part)
        {
            if (part == null)
                return false;

            // Weldolet / threadolet / sockolet names contain "WELD" or "OLET" but are branch fittings, not weld joints.
            if (IsOletPart(part))
                return false;

            string combined = string.Join(" ",
                part.Name ?? string.Empty,
                GetParamString(part, "Alias"),
                GetParamString(part, "Product Entry"),
                GetParamString(part, ProductLongDescriptionParameterName),
                GetParamString(part, "Description"),
                GetParamString(part, "eM_Fitting Type"),
                GetParamString(part, "eM_Service Type"),
                GetParamString(part, "CID")).ToUpperInvariant();

            // "Weld Neck Flange" has "WELD" but is a flange fitting — do not call IsFlangePart (circular).
            if (combined.Contains("FLANGE") || combined.Contains("WNRF") || combined.Contains("SORF")
                || combined.Contains("WELD NECK") || combined.Contains("WELD-NECK"))
            {
                return false;
            }

            return combined.Contains("WELD") || combined.Contains("JOINT");
        }

        public static bool IsJointPart(FabricationPart part)
        {
            if (part == null)
                return false;

            return GetClassificationText(part).IndexOf("JOINT", StringComparison.Ordinal) >= 0;
        }

        public static bool IsGasketPart(FabricationPart part)
        {
            if (part == null)
                return false;

            return GetClassificationText(part).Contains("GASKET");
        }

        public static bool IsBoltKitPart(FabricationPart part)
        {
            if (part == null)
                return false;

            string combined = GetClassificationText(part);
            return combined.Contains("BOLT KIT") ||
                   combined.Contains("BOLTKIT") ||
                   (combined.Contains("BOLT") && combined.Contains("KIT"));
        }

        /// <summary>
        /// MEP Fabrication hangers — numbered/tagged separately from pipe/fitting item numbers.
        /// </summary>
        public static bool IsFabricationHanger(Element element)
        {
            if (element == null)
                return false;

            try
            {
                Category category = element.Category;
                if (category != null && category.Id.Value == (long)BuiltInCategory.OST_FabricationHangers)
                    return true;
            }
            catch
            {
            }

            return element is FabricationPart part && IsFabricationHanger(part);
        }

        public static bool IsFabricationHanger(FabricationPart part)
        {
            if (part == null)
                return false;

            try
            {
                Category category = ((Element)part).Category;
                if (category != null && category.Id.Value == (long)BuiltInCategory.OST_FabricationHangers)
                    return true;
            }
            catch
            {
            }

            string name = (categoryNameSafe(part) ?? string.Empty).ToUpperInvariant();
            return name.Contains("HANGER");
        }

        private static string categoryNameSafe(FabricationPart part)
        {
            try
            {
                return ((Element)part).Category?.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Copper / press / solder / braze corpus — used to scope copper-only auto-dim overrides without touching steel lessons.</summary>
        public static bool IsCopperPart(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return false;

            string corpus = GetExpandedSearchCorpus(part, doc);
            if (string.IsNullOrWhiteSpace(corpus))
                return false;

            return corpus.Contains("COPPER")
                || corpus.Contains(" PRESS")
                || corpus.Contains("PRESS ")
                || corpus.Contains("SOLDER")
                || corpus.Contains("BRAZE")
                || corpus.Contains("CWS")
                || corpus.Contains("CWR")
                || corpus.Contains("DWV");
        }

        public static bool IsElbowPart(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return false;

            string corpus = GetExpandedSearchCorpus(part, doc);
            if (corpus.Contains("ELBOW")
                || corpus.Contains(" ELL ")
                || corpus.Contains(" ELL,")
                || corpus.EndsWith(" ELL", StringComparison.Ordinal)
                || corpus.StartsWith("ELL ", StringComparison.Ordinal)
                || corpus == "ELL")
            {
                return true;
            }

            // Copper / pressed / formed bends often omit the word "elbow".
            if (corpus.Contains("BEND") && (corpus.Contains("90") || corpus.Contains("45") || corpus.Contains("180")))
                return true;
            if ((corpus.Contains("90 DEG") || corpus.Contains("90°") || corpus.Contains("45 DEG") || corpus.Contains("45°"))
                && (corpus.Contains("COPPER") || corpus.Contains("PRESS") || corpus.Contains("SOLDER") || corpus.Contains("BRAZE")))
            {
                return true;
            }

            string fittingType = GetParamString(part, doc, "eM_Fitting Type");
            if (fittingType.IndexOf("ELBOW", StringComparison.OrdinalIgnoreCase) >= 0
                || fittingType.IndexOf("BEND", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        public static bool IsTeePart(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return false;

            string corpus = GetExpandedSearchCorpus(part, doc);
            if (string.IsNullOrWhiteSpace(corpus))
                return false;

            // Alias is often just "Tee" with no surrounding tokens.
            return corpus == "TEE"
                || corpus.StartsWith("TEE ", StringComparison.Ordinal)
                || corpus.EndsWith(" TEE", StringComparison.Ordinal)
                || corpus.Contains(" TEE")
                || corpus.Contains("TEE ")
                || corpus.Contains("-TEE")
                || corpus.Contains("TEE,")
                || corpus.Contains("TEE REDUC")
                || corpus.Contains("TEE EQUAL");
        }

        public static bool IsReducerPart(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return false;

            string corpus = GetExpandedSearchCorpus(part, doc);
            return corpus.Contains("REDUCER") || corpus.Contains(" CONC ") || corpus.Contains(" ECC ");
        }

        public static bool IsValvePart(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return false;

            doc = doc ?? ((Element)part).Document;
            if (GetFabricationSortPriority(part, doc) == 2)
                return true;

            string corpus = GetExpandedSearchCorpus(part, doc);
            return corpus.Contains("VALVE") || corpus.Contains("BUTTERFLY");
        }

        /// <summary>Fabrication components worth locating on a spool sheet (not gaskets/welds/artifacts).</summary>
        public static bool IsMeaningfulSpoolComponent(FabricationPart part, Document doc = null)
        {
            if (part == null || IsIgnoredForSpoolDimensioning(part, doc))
                return false;

            doc = doc ?? ((Element)part).Document;
            if (IsFlangePart(part, doc) || IsOletPart(part))
                return false;

            if (IsElbowPart(part, doc) || IsTeePart(part, doc) || IsReducerPart(part, doc))
                return true;

            int priority = GetFabricationSortPriority(part, doc);
            return priority == 1 || priority == 2;
        }

        /// <summary>Parts that should never receive spool dimensions (gaskets, welds, bolt kits, etc.).</summary>
        public static bool IsIgnoredForSpoolDimensioning(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return true;

            if (IsGasketPart(part) || IsWeldPart(part) || IsBoltKitPart(part))
                return true;

            if (IsValvePart(part, doc))
                return true;

            string corpus = GetExpandedSearchCorpus(part, doc);
            if (corpus.Contains("GASKET") || corpus.Contains(" COUPLING") && corpus.Contains(" FLEX"))
                return true;

            return false;
        }

        /// <summary>Flanges are fitting-like in fabrication but must be tagged and C-F dimensioned separately.</summary>
        public static bool IsFlangePart(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return false;

            doc = doc ?? ((Element)part).Document;
            // Straight cut pipe segments must never be treated as flanges — catalog text on pipe types
            // can contain false-positive flange tokens and breaks branch takeoff numbering/dimensioning.
            if (IsStraightFabricationPipeSegment(part, doc))
                return false;

            // Gaskets often say "Full Face" / "150#" — never treat them as flanges.
            if (IsGasketPart(part))
                return false;

            string corpus = GetExpandedSearchCorpus(part, doc);
            if (corpus.Contains("GASKET") || corpus.Contains("SHOP WELD") || corpus.Contains("FIELD WELD"))
                return false;

            return CorpusIndicatesFlange(corpus);
        }

        private static bool IsStraightFabricationPipeSegment(FabricationPart part, Document doc)
        {
            string fittingType = GetParamString(part, doc, "eM_Fitting Type");
            string serviceType = GetParamString(part, doc, "eM_Service Type");
            if (serviceType.IndexOf("PIPEWORK", StringComparison.OrdinalIgnoreCase) >= 0 &&
                fittingType.IndexOf("STRAIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return LooksLikeStraightPipeFromLongDescription(part, doc);
        }

        /// <summary>Detects flanges from expanded type/parameter text (e.g. "150# FF" has no FLANGE token).</summary>
        private static bool CorpusIndicatesFlange(string corpus)
        {
            if (string.IsNullOrWhiteSpace(corpus))
                return false;

            if (corpus.Contains("FLANGE") || corpus.Contains("WELD NECK") || corpus.Contains("WELD-NECK"))
                return true;

            // Fabrication flange type names often use FF / RFF abbreviations instead of "FLANGE".
            // Require rating+FF or explicit flange codes — bare "FULL FACE" (gaskets) must not match.
            if (corpus.Contains("# FF") || corpus.Contains("#FF") || corpus.Contains("-FF"))
                return true;

            if (corpus.Contains(" RFF") || corpus.Contains(" WNRF") || corpus.Contains(" SORF") ||
                corpus.Contains(" SOFF") || corpus.Contains(" LFF") ||
                (corpus.Contains("BLIND") && (corpus.Contains("#") || corpus.Contains("FLANGE"))))
                return true;

            // Copper companion / braze-on / solder / slip / van-stone style flanges.
            if (corpus.Contains("COMPANION")
                || corpus.Contains("VAN STONE")
                || corpus.Contains("VANSTONE")
                || corpus.Contains("BRAZE-ON")
                || corpus.Contains("BRAZE ON")
                || (corpus.Contains("SOLDER") && corpus.Contains("FLANGE"))
                || (corpus.Contains("SLIP") && corpus.Contains("FLANGE"))
                || (corpus.Contains("COPPER") && (corpus.Contains("FLANGE") || corpus.Contains(" FF") || corpus.Contains("#FF"))))
            {
                return true;
            }

            return false;
        }

        public static string GetExpandedSearchCorpus(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return string.Empty;

            var element = (Element)part;
            var tokens = new List<string>();
            if (!string.IsNullOrWhiteSpace(element.Name))
                tokens.Add(element.Name.Trim());

            if (doc != null)
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    Element typeElement = doc.GetElement(typeId);
                    if (typeElement != null)
                    {
                        if (!string.IsNullOrWhiteSpace(typeElement.Name))
                            tokens.Add(typeElement.Name.Trim());
                        if (typeElement is ElementType elementType && !string.IsNullOrWhiteSpace(elementType.FamilyName))
                            tokens.Add(elementType.FamilyName.Trim());
                    }
                }
            }

            tokens.Add(GetParamString(part, doc, "Alias"));
            tokens.Add(GetParamString(part, doc, "Product Entry"));
            tokens.Add(GetParamString(part, doc, ProductLongDescriptionParameterName));
            tokens.Add(GetParamString(part, doc, "Product Short Description"));
            tokens.Add(GetParamString(part, doc, "Description"));
            tokens.Add(GetParamString(part, doc, "CID"));
            tokens.Add(GetParamString(part, doc, "eM_Fitting Type"));
            tokens.Add(GetParamString(part, doc, "eM_Service Type"));
            tokens.Add(GetParamString(part, doc, "Part Material"));
            tokens.Add(GetParamString(part, doc, "Material"));

            return string.Join(" ",
                tokens.Where(x => !string.IsNullOrWhiteSpace(x))).ToUpperInvariant();
        }

        public static int GetFabricationSortPriority(FabricationPart part, Document doc = null)
        {
            if (part == null)
                return 99;

            doc = doc ?? ((Element)part).Document;
            if (IsFlangePart(part, doc))
                return 1;

            string fittingType = GetParamString(part, doc, "eM_Fitting Type");
            string serviceType = GetParamString(part, doc, "eM_Service Type");

            bool isPipework = serviceType.IndexOf("PIPEWORK", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isValve = serviceType.IndexOf("VALVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           fittingType.IndexOf("VALVE", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isValve &&
                isPipework &&
                fittingType.IndexOf("STRAIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }

            if (!isValve &&
                isPipework &&
                fittingType.IndexOf("FITTING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (LooksLikeStraightPipeFromLongDescription(part, doc))
                    return 0;

                return 1;
            }

            if (isValve)
                return 2;

            string alias = GetParamString(part, doc, "Alias").ToUpperInvariant();
            if (alias.Contains("PIPE") || alias.Contains("STRAIGHT"))
                return 0;

            if (alias.Contains("FITTING"))
                return 1;

            if (alias.Contains("VALVE"))
                return 2;

            string length = GetParamString(part, doc, "Length");
            string angle = GetParamString(part, doc, "Angle");
            string combined = string.Join(" ",
                part.Name ?? string.Empty,
                alias,
                GetParamString(part, doc, "Product Entry"),
                GetParamString(part, doc, ProductLongDescriptionParameterName),
                GetParamString(part, doc, "CID")).ToUpperInvariant();

            if (combined.Contains("VALVE"))
                return 2;

            if (!string.IsNullOrWhiteSpace(length) &&
                string.IsNullOrWhiteSpace(angle))
            {
                if (IsFlangePart(part, doc))
                    return 1;

                // Length-only fittings (copper couplings, adapters, reducers) are NOT pipe.
                string fittingCorpus = combined;
                if (fittingCorpus.Contains("COUPLING")
                    || fittingCorpus.Contains("ADAPTER")
                    || fittingCorpus.Contains("REDUCER")
                    || fittingCorpus.Contains("UNION")
                    || fittingCorpus.Contains("ELBOW")
                    || fittingCorpus.Contains("TEE")
                    || fittingCorpus.Contains("CAP ")
                    || fittingCorpus.Contains("NIPPLE"))
                {
                    return 1;
                }

                return 0;
            }

            if (combined.Contains("PIPE") || combined.Contains("STRAIGHT"))
                return 0;

            return 1;
        }

        private static bool LooksLikeStraightPipeFromLongDescription(FabricationPart part, Document doc = null)
        {
            string description = (GetParamString(part, doc, ProductLongDescriptionParameterName) ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(description))
                return false;

            if (description.IndexOf("ELBOW", StringComparison.Ordinal) >= 0) return false;
            if (description.IndexOf("TEE", StringComparison.Ordinal) >= 0) return false;
            if (description.IndexOf("REDUCER", StringComparison.Ordinal) >= 0) return false;
            if (description.IndexOf("CAP ", StringComparison.Ordinal) >= 0) return false;
            if (description.IndexOf("COUPLING", StringComparison.Ordinal) >= 0) return false;
            if (description.IndexOf("UNION", StringComparison.Ordinal) >= 0) return false;
            if (description.IndexOf("ADAPTER", StringComparison.Ordinal) >= 0) return false;
            if (description.IndexOf("ADAPTOR", StringComparison.Ordinal) >= 0) return false;
            if (description.IndexOf("FLANGE", StringComparison.Ordinal) >= 0) return false;

            return description.IndexOf("PIPE", StringComparison.Ordinal) >= 0;
        }

        private static string GetClassificationText(FabricationPart part)
        {
            return string.Join(" ",
                part.Name ?? string.Empty,
                GetParamString(part, "Alias"),
                GetParamString(part, "Product Entry"),
                GetParamString(part, "eM_Fitting Type"),
                GetParamString(part, "eM_Service Type"),
                GetParamString(part, "Service Type"),
                GetParamString(part, ProductLongDescriptionParameterName),
                GetParamString(part, "CID")).ToUpperInvariant();
        }

        internal static string GetParamString(Element element, string parameterName)
        {
            return ReadParameterAsString(element?.LookupParameter(parameterName));
        }

        internal static string GetParamString(Element element, Document doc, string parameterName)
        {
            if (element == null || string.IsNullOrWhiteSpace(parameterName))
                return string.Empty;

            string value = GetParamString(element, parameterName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            try
            {
                if (doc == null)
                    return string.Empty;

                ElementId typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    return string.Empty;

                return GetParamString(doc.GetElement(typeId), parameterName);
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string ReadParameterAsString(Parameter parameter)
        {
            if (parameter == null)
                return string.Empty;

            try
            {
                if (!parameter.HasValue)
                {
                    if (parameter.StorageType == StorageType.String)
                    {
                        string value = parameter.AsString();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value.Trim();
                    }

                    string valueStringNoValue = parameter.AsValueString();
                    return string.IsNullOrWhiteSpace(valueStringNoValue) ? string.Empty : valueStringNoValue.Trim();
                }

                if (parameter.StorageType == StorageType.String)
                {
                    string value = parameter.AsString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }

                string valueString = parameter.AsValueString();
                if (!string.IsNullOrWhiteSpace(valueString))
                    return valueString.Trim();

                switch (parameter.StorageType)
                {
                    case StorageType.Integer:
                        return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double:
                        return parameter.AsDouble().ToString("0.########", CultureInfo.InvariantCulture);
                    case StorageType.ElementId:
                        return parameter.AsElementId().Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch
            {
            }

            return string.Empty;
        }
    }
}
