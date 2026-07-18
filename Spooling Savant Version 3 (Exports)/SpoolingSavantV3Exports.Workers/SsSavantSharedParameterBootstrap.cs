using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace SpoolingSavantV3Exports.Workers
{
    /// <summary>
    /// Binds SS Manager shared parameters when a project document opens (called from SpoolingSavantV3Exports.dll at load).
    /// </summary>
    public static class SsSavantSharedParameterBootstrap
    {
        internal const string PackageParameterName = "S-Package";
        internal const string SSizeParameterName = "S-Size";
        internal const string SMaterialParameterName = "S-Material";
        internal const string SWeldParameterName = "S-Weld";
        internal const string SContinuationParameterName = "S-Continuation";
        internal const string SConnector1ParameterName = "S-Connector 1";
        internal const string SConnector2ParameterName = "S-Connector 2";
        internal const string SHangerSizeParameterName = "S-Hanger Size";
        internal const string SRodLengthAParameterName = "S-Rod Length A";
        internal const string SRodLengthBParameterName = "S-Rod Length B";
        internal const string SStrutLengthParameterName = "S-Strut Length";

        public static void ConfigureApplicationSharedParameterFile(Application app)
        {
            if (app == null)
                return;

            SsSavantSharedParameterEnsure.ConfigureApplicationSharedParameterFile(app);
        }

        public static void EnsureAllForDocument(Application app, Document doc)
        {
            if (app == null || doc == null || doc.IsFamilyDocument)
                return;

            ConfigureApplicationSharedParameterFile(app);

            using (var transaction = new Transaction(doc, "Spooling Savant V3 (Exports) - Overwrite shared parameters"))
            {
                transaction.Start();
                try
                {
                    List<Category> fabricationPipeAndContainment = GetCategories(
                        doc,
                        BuiltInCategory.OST_FabricationPipework,
                        BuiltInCategory.OST_FabricationContainment);

                    List<Category> fabricationHangers = GetCategories(
                        doc,
                        BuiltInCategory.OST_FabricationHangers);

                    List<Category> materialCategories = MergeCategories(fabricationPipeAndContainment, fabricationHangers);

                    List<Category> packageCategories = MergeCategories(
                        GetCategories(doc, BuiltInCategory.OST_Assemblies),
                        fabricationPipeAndContainment,
                        fabricationHangers,
                        GetCategories(doc, BuiltInCategory.OST_Sheets, BuiltInCategory.OST_Views));

                    List<Category> fabricationPipework = GetCategories(
                        doc,
                        BuiltInCategory.OST_FabricationPipework);

                    Ensure(app, doc, PackageParameterName, SpecTypeId.String.Text, packageCategories);
                    Ensure(app, doc, SSizeParameterName, SpecTypeId.String.Text, fabricationPipeAndContainment);
                    Ensure(app, doc, SMaterialParameterName, SpecTypeId.String.Text, materialCategories);
                    Ensure(app, doc, SWeldParameterName, SpecTypeId.String.Text, fabricationPipeAndContainment);
                    Ensure(app, doc, SContinuationParameterName, SpecTypeId.String.Text, fabricationPipeAndContainment);
                    Ensure(app, doc, SConnector1ParameterName, SpecTypeId.String.Text, fabricationPipeAndContainment);
                    Ensure(app, doc, SConnector2ParameterName, SpecTypeId.String.Text, fabricationPipeAndContainment);
                    Ensure(app, doc, SHangerSizeParameterName, SpecTypeId.String.Text, fabricationHangers);
                    Ensure(app, doc, SRodLengthAParameterName, SpecTypeId.Length, fabricationHangers);
                    Ensure(app, doc, SRodLengthBParameterName, SpecTypeId.Length, fabricationHangers);
                    Ensure(app, doc, SStrutLengthParameterName, SpecTypeId.Length, fabricationHangers);

                    List<FabricationPart> allPipework = new FilteredElementCollector(doc)
                        .OfClass(typeof(FabricationPart))
                        .Cast<FabricationPart>()
                        .Where(part =>
                            part.Category != null &&
                            part.Category.Id.Value == (long)BuiltInCategory.OST_FabricationPipework)
                        .ToList();

                    FabricationSavantParameterSync.SyncSizeParameters(doc, allPipework);
                    FabricationSavantParameterSync.SyncConnectorParameters(doc, allPipework);

                    doc.Regenerate();
                    transaction.Commit();
                }
                catch
                {
                    transaction.RollBack();
                    throw;
                }
            }
        }

        private static void Ensure(
            Application app,
            Document doc,
            string parameterName,
            ForgeTypeId specTypeId,
            IList<Category> categories)
        {
            if (categories == null || categories.Count == 0)
                return;

            SsSavantSharedParameterEnsure.EnsureInstanceParameter(app, doc, parameterName, specTypeId, categories);
        }

        private static List<Category> GetCategories(Document doc, params BuiltInCategory[] builtInCategories)
        {
            var categories = new List<Category>();
            if (doc == null || builtInCategories == null)
                return categories;

            foreach (BuiltInCategory builtInCategory in builtInCategories)
            {
                Category category = Category.GetCategory(doc, builtInCategory);
                if (category == null)
                    continue;

                try
                {
                    if (!category.AllowsBoundParameters)
                        continue;
                }
                catch
                {
                    continue;
                }

                categories.Add(category);
            }

            return categories;
        }

        private static List<Category> MergeCategories(params IList<Category>[] categoryLists)
        {
            var seen = new HashSet<long>();
            var merged = new List<Category>();
            if (categoryLists == null)
                return merged;

            foreach (IList<Category> list in categoryLists)
            {
                if (list == null)
                    continue;

                foreach (Category category in list)
                {
                    if (category == null)
                        continue;

                    if (!seen.Add(category.Id.Value))
                        continue;

                    merged.Add(category);
                }
            }

            return merged;
        }
    }
}
