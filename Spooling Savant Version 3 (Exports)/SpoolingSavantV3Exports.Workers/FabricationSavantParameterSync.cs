using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using SpoolingSavantV3Exports.Workers.SpoolingManager.Services;
using static SpoolingSavantV3Exports.Workers.FabricationPartClassification;

namespace SpoolingSavantV3Exports.Workers
{
    /// <summary>Writes derived S-* values onto fabrication parts from their built-in properties.</summary>
    public static class FabricationSavantParameterSync
    {
        internal const string ProductEntryParameterName = "Product Entry";
        internal const string SizeParameterName = "Size";
        internal const string LegacyEsizeParameterName = "E-Size";
        internal const string SConnector1ParameterName = "S-Connector 1";
        internal const string SConnector2ParameterName = "S-Connector 2";

        public static void SyncAssemblyMemberParameters(Document doc, AssemblyInstance assembly)
        {
            SyncAssemblyMemberParameters(null, doc, assembly);
        }

        public static void SyncAssemblyMemberParameters(Application app, Document doc, AssemblyInstance assembly)
        {
            if (doc == null || assembly == null)
                return;

            List<Element> members = assembly.GetMemberIds()
                .Select(id => doc.GetElement(id))
                .Where(element => element != null)
                .ToList();

            List<FabricationPart> parts = members.OfType<FabricationPart>().ToList();

            bool boundParameters = EnsureSavantParametersForParts(app, doc, parts, members);
            if (boundParameters)
                CreateSpoolSheetsHandler.RequestRegenerate(doc);

            SyncSizeParameters(doc, parts);
            SyncConnectorParameters(doc, parts);
            FabricationHangerParameterSync.SyncAssemblyHangers(app, doc, assembly);
        }

        public static string TryGetAssemblyPackageValue(Document doc, AssemblyInstance assembly)
        {
            if (doc == null || assembly == null)
                return string.Empty;

            string fromAssembly = ReadTextParameter(assembly, SsSavantSharedParameterBootstrap.PackageParameterName);
            if (!string.IsNullOrWhiteSpace(fromAssembly))
                return fromAssembly.Trim();

            foreach (ElementId memberId in assembly.GetMemberIds())
            {
                Element member = doc.GetElement(memberId);
                if (member == null)
                    continue;

                string fromMember = ReadTextParameter(member, SsSavantSharedParameterBootstrap.PackageParameterName);
                if (!string.IsNullOrWhiteSpace(fromMember))
                    return fromMember.Trim();
            }

            return string.Empty;
        }

	public static void ApplyPackageToMembersWithoutValue(Document doc, AssemblyInstance assembly, string package)
	{
		if (doc == null || assembly == null || string.IsNullOrWhiteSpace(package))
			return;

		string normalizedPackage = package.Trim();
		TrySetPackageParameterIfEmpty(assembly, normalizedPackage);

		foreach (ElementId memberId in assembly.GetMemberIds())
		{
			Element member = doc.GetElement(memberId);
			if (member != null && !FabricationPartClassification.IsFabricationHanger(member))
				TrySetPackageParameterIfEmpty(member, normalizedPackage);
		}
	}

	/// <summary>Sets S-Package on any element that has the parameter (assemblies, parts, sheets, views).</summary>
	public static bool TrySetPackageParameter(Element element, string package)
	{
		if (element == null)
			return false;

		Parameter parameter = element.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
		if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
			return false;

		parameter.Set(package ?? string.Empty);
		return true;
	}

	public static string TryGetPackageParameter(Element element)
	{
		return ReadTextParameter(element, SsSavantSharedParameterBootstrap.PackageParameterName);
	}

	public static void EnsurePackageParameterForAssembly(Application app, Document doc, AssemblyInstance assembly)
	{
		if (app == null || doc == null || assembly == null)
			return;

		List<Element> members = assembly.GetMemberIds()
			.Select(id => doc.GetElement(id))
			.Where(element => element != null)
			.ToList();

		members.Add(assembly);

		List<Category> categories = SsSavantSharedParameterEnsure.CollectBindableCategories(members);
		if (categories.Count == 0)
			return;

		SsSavantSharedParameterEnsure.EnsureInstanceParameter(
			app,
			doc,
			SsSavantSharedParameterBootstrap.PackageParameterName,
			SpecTypeId.String.Text,
			categories);
	}

        public static void SyncSizeParameters(Document doc, IEnumerable<FabricationPart> parts)
        {
            if (doc == null || parts == null)
                return;

            foreach (FabricationPart part in parts)
            {
                if (part == null)
                    continue;

                string cleaned = ResolveSizeValue(part, doc);
                SetSavantSizeParameters(part, cleaned);
            }
        }

        public static void SyncConnectorParameters(Document doc, IEnumerable<FabricationPart> parts)
        {
            if (doc == null || parts == null)
                return;

            foreach (FabricationPart part in parts)
            {
                if (part == null)
                    continue;

                if (!FabricationPartClassification.IsStraightPipeRun(part))
                {
                    SetTextParameter(part, SConnector1ParameterName, string.Empty);
                    SetTextParameter(part, SConnector2ParameterName, string.Empty);
                    continue;
                }

                FabricationConnectorEnds.GetConnectorEndLabels(part, doc, out string end1, out string end2);
                SetTextParameter(part, SConnector1ParameterName, NormalizeConnectorValue(end1));
                SetTextParameter(part, SConnector2ParameterName, NormalizeConnectorValue(end2));
            }
        }

        public static void SyncConnectorParametersForAllFabricationPipework(Document doc)
        {
            if (doc == null)
                return;

            List<FabricationPart> parts = new FilteredElementCollector(doc)
                .OfClass(typeof(FabricationPart))
                .Cast<FabricationPart>()
                .Where(part =>
                    part.Category != null &&
                    part.Category.Id.Value == (long)BuiltInCategory.OST_FabricationPipework)
                .ToList();

            SyncConnectorParameters(doc, parts);
        }

        public static string CleanSizeValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value
                .Replace("Ø", string.Empty)
                .Replace("ø", string.Empty)
                .Replace("⌀", string.Empty)
                .Trim();
        }

        private static bool EnsureSavantParametersForParts(
            Application app,
            Document doc,
            IList<FabricationPart> parts,
            IList<Element> members = null)
        {
            if (app == null || doc == null)
                return false;

            List<Category> categories = members != null && members.Count > 0
                ? SsSavantSharedParameterEnsure.CollectBindableCategories(members)
                : SsSavantSharedParameterEnsure.CollectBindableCategories(parts);

            if (categories.Count == 0)
                return false;

            bool boundAny = false;
            boundAny |= EnsureSavantParameterIfMissing(
                app,
                doc,
                SsSavantSharedParameterBootstrap.PackageParameterName,
                categories);
            boundAny |= EnsureSavantParameterIfMissing(
                app,
                doc,
                SsSavantSharedParameterBootstrap.SSizeParameterName,
                categories);
            boundAny |= EnsureSavantParameterIfMissing(app, doc, SConnector1ParameterName, categories);
            boundAny |= EnsureSavantParameterIfMissing(app, doc, SConnector2ParameterName, categories);
            boundAny |= EnsureSavantParameterIfMissing(
                app,
                doc,
                SsSavantSharedParameterBootstrap.SContinuationParameterName,
                categories);

            return boundAny;
        }

        private static string ReadTextParameter(Element element, string parameterName)
        {
            if (element == null || string.IsNullOrWhiteSpace(parameterName))
                return string.Empty;

            Parameter parameter = element.LookupParameter(parameterName);
            return parameter == null ? string.Empty : ReadParameterAsString(parameter);
        }

        private static void TrySetPackageParameterIfEmpty(Element element, string package)
        {
            if (element == null || string.IsNullOrWhiteSpace(package))
                return;

            Parameter parameter = element.LookupParameter(SsSavantSharedParameterBootstrap.PackageParameterName);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
                return;

            string current = ReadParameterAsString(parameter);
            if (!string.IsNullOrWhiteSpace(current))
                return;

            parameter.Set(package);
        }

        private static bool EnsureSavantParameterIfMissing(
            Application app,
            Document doc,
            string parameterName,
            IList<Category> categories)
        {
            if (SsSavantSharedParameterEnsure.HasInstanceBindingForParameterName(doc, parameterName))
                return false;

            SsSavantSharedParameterEnsure.EnsureInstanceParameter(
                app,
                doc,
                parameterName,
                SpecTypeId.String.Text,
                categories);
            return true;
        }

        private static string ResolveSizeValue(FabricationPart part, Document doc)
        {
            if (part == null)
                return string.Empty;

            string fromProductEntry = CleanSizeValue(GetElementParameterString(part, doc, ProductEntryParameterName));
            if (!string.IsNullOrWhiteSpace(fromProductEntry))
                return fromProductEntry;

            return CleanSizeValue(GetElementParameterString(part, doc, SizeParameterName));
        }

        private static void SetSavantSizeParameters(Element element, string value)
        {
            SetTextParameter(element, SsSavantSharedParameterBootstrap.SSizeParameterName, value);
            SetTextParameter(element, LegacyEsizeParameterName, value);
        }

        public static void SetSavantTextParameter(Element element, string parameterName, string value)
        {
            SetTextParameter(element, parameterName, value);
        }

        public static void EnsureContinuationParameterForAssembly(Application app, Document doc, AssemblyInstance assembly)
        {
            if (app == null || doc == null || assembly == null)
                return;

            List<FabricationPart> parts = assembly.GetMemberIds()
                .Select(id => doc.GetElement(id))
                .OfType<FabricationPart>()
                .ToList();

            if (parts.Count == 0)
                return;

            List<Category> categories = SsSavantSharedParameterEnsure.CollectBindableCategories(parts);
            if (categories.Count == 0)
                return;

            SsSavantSharedParameterEnsure.EnsureInstanceParameter(
                app,
                doc,
                SsSavantSharedParameterBootstrap.SContinuationParameterName,
                SpecTypeId.String.Text,
                categories);
        }

        private static string NormalizeConnectorValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim();
            return string.Equals(trimmed, FabricationConnectorEnds.MissingLabel, StringComparison.Ordinal)
                ? string.Empty
                : trimmed;
        }

        private static string GetElementParameterString(Element element, Document doc, string parameterName)
        {
            if (element == null || string.IsNullOrWhiteSpace(parameterName))
                return string.Empty;

            return GetParamString(element, doc, parameterName);
        }

        private static void SetTextParameter(Element element, string parameterName, string value)
        {
            if (element == null || string.IsNullOrWhiteSpace(parameterName))
                return;

            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter == null || parameter.IsReadOnly)
                return;

            string current = ReadParameterAsString(parameter);
            if (string.Equals(current, value ?? string.Empty, StringComparison.Ordinal))
                return;

            value = value ?? string.Empty;
            if (parameter.StorageType == StorageType.String)
            {
                parameter.Set(value);
                return;
            }

            try
            {
                parameter.SetValueString(value);
            }
            catch
            {
            }
        }
    }
}
