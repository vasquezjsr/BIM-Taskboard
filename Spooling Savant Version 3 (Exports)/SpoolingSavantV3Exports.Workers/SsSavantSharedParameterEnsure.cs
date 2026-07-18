// Target: .NET Framework 4.8
// Assembly: SpoolingSavantV3Exports.Workers

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SpoolingSavantV3Exports.Workers
{
    /// <summary>Creates shared (project) instance parameters and binds them to element categories when missing.</summary>
    public static class SsSavantSharedParameterEnsure
    {
        private const string SavantParameterGroupName = "Spooling Savant V3 (Exports)";

        private const string DefaultSharedParameterFileContent =
            "# This is a Revit shared parameter file.\r\n" +
            "*META\tVERSION\tMINVERSION\r\n" +
            "META\t2\t1\r\n" +
            "*GROUP\tID\tNAME\r\n" +
            "GROUP\t1\tSpooling Savant V3 (Exports)\r\n" +
            "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n" +
            "PARAM\t2F4A6C8E-1B3D-4F5A-9E7C-0D2B4F6A8C1E\tS-Continuation\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t41BC6DA0-2C5E-406C-A18E-1F4D6E8A0C3F\tS-Material\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t52CD7EB1-3D6F-517D-B29F-205E7F9B1D40\tS-Package\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t63DE8FC2-4E80-628E-C3A0-316F809C2E51\tS-Size\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t5FF5FA60-EE84-4EAB-A26B-CE6EECCA214F\tS-Weld\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\tC4D8E912-3A7F-4E61-9B2C-1F5A8D3E7B60\tS-Connector 1\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\tD5E9FA23-4B80-4F72-AC3D-2A6B9E4F8C71\tS-Connector 2\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\tE6F0AB34-5C91-5083-BD4E-3B7CAF509D82\tS-Hanger Size\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\tF701BC45-6DA2-6194-CE5F-4C8DB0610E93\tS-Rod Length A\tLENGTH\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t1923DE67-8FC4-83B6-E071-6E0FD28320B5\tS-Rod Length B\tLENGTH\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t0812CD56-7EB3-72A5-DF60-5D9EC1721FA4\tS-Strut Length\tLENGTH\t\t1\t1\t\t1\t0\r\n";

        /// <summary>Points Revit at the Spooling Savant V3 (Exports) shared parameter file shipped with the add-in.</summary>
        public static void ConfigureApplicationSharedParameterFile(Application app)
        {
            if (app == null)
                return;

            string path = EnsureSharedParameterFileOnDisk(forceRefresh: true);
            app.SharedParametersFilename = path;
        }

        public static string EnsureSharedParameterFileOnDisk(bool forceRefresh = false)
        {
            string path = Path.GetFullPath(SsSavantSharedParameterPaths.BomSharedParameterFile);
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            if (forceRefresh || !File.Exists(path))
                File.WriteAllText(path, DefaultSharedParameterFileContent, System.Text.Encoding.Unicode);

            return path;
        }
        public static List<Category> CollectBindableCategories(IEnumerable<Element> elements)
        {
            var seen = new HashSet<long>();
            var list = new List<Category>();
            if (elements == null) return list;

            foreach (Element e in elements)
            {
                Category c = e?.Category;
                if (c == null) continue;
                long id = c.Id.Value;
                if (seen.Contains(id)) continue;
                try
                {
                    if (!c.AllowsBoundParameters) continue;
                }
                catch
                {
                    continue;
                }

                seen.Add(id);
                list.Add(c);
            }

            return list;
        }

        /// <summary>True when the document already binds an instance parameter with this Revit-definition name.</summary>
        internal static bool HasInstanceBindingForParameterName(Document doc, string parameterName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(parameterName))
                return false;

            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                Definition key = it.Key;
                if (key == null || !string.Equals(key.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                    continue;
                return it.Current is InstanceBinding;
            }

            return false;
        }

        /// <summary>
        /// Ensures a shared parameter exists in the Spooling Savant V3 (Exports) group and is bound (instance) to the given categories.
        /// Merges with any existing instance binding for the same parameter name.
        /// Parameters are assigned to the <see cref="GroupTypeId.Text"/> property group so they appear under Text in the Properties palette.
        /// </summary>
        public static void EnsureInstanceParameter(
            Application app,
            Document doc,
            string parameterName,
            ForgeTypeId specTypeId,
            IList<Category> categories)
        {
            OverwriteInstanceParameterFromSavantFile(app, doc, parameterName, specTypeId, categories);
        }

        /// <summary>
        /// Removes any project bindings for <paramref name="parameterName"/> and re-binds the definition from the
        /// Spooling Savant V3 (Exports) shared parameter file shipped with the add-in.
        /// </summary>
        public static void OverwriteInstanceParameterFromSavantFile(
            Application app,
            Document doc,
            string parameterName,
            ForgeTypeId specTypeId,
            IList<Category> categories)
        {
            if (app == null || doc == null || string.IsNullOrWhiteSpace(parameterName) ||
                categories == null || categories.Count == 0)
                return;

            CategorySet catSet = BuildCategorySet(categories);
            if (catSet.Size == 0)
                return;

            Definition definition = OpenSavantSharedDefinition(app, parameterName, specTypeId);
            if (definition == null)
                return;

            RemoveAllInstanceBindingsForParameterName(doc, parameterName);

            InstanceBinding binding = app.Create.NewInstanceBinding(catSet);
            if (!doc.ParameterBindings.Insert(definition, binding, GroupTypeId.Text))
                doc.ParameterBindings.ReInsert(definition, binding, GroupTypeId.Text);
        }

        private static CategorySet BuildCategorySet(IList<Category> categories)
        {
            var catSet = new CategorySet();
            foreach (Category c in categories)
            {
                if (c == null)
                    continue;

                try
                {
                    if (c.AllowsBoundParameters)
                        catSet.Insert(c);
                }
                catch
                {
                }
            }

            return catSet;
        }

        private static Definition OpenSavantSharedDefinition(
            Application app,
            string parameterName,
            ForgeTypeId specTypeId)
        {
            string originalSharedParameterFile = app.SharedParametersFilename;
            try
            {
                string path = EnsureSharedParameterFileOnDisk();
                app.SharedParametersFilename = path;

                DefinitionFile definitionFile = app.OpenSharedParameterFile();
                if (definitionFile == null)
                    return null;

                DefinitionGroup group = definitionFile.Groups.get_Item(SavantParameterGroupName);
                if (group == null)
                    group = definitionFile.Groups.Create(SavantParameterGroupName);

                Definition definition = group.Definitions.get_Item(parameterName);
                if (definition != null)
                    return definition;

                var options = new ExternalDefinitionCreationOptions(parameterName, specTypeId)
                {
                    Visible = true
                };
                return group.Definitions.Create(options);
            }
            finally
            {
                app.SharedParametersFilename = originalSharedParameterFile;
            }
        }

        private static void RemoveAllInstanceBindingsForParameterName(Document doc, string parameterName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(parameterName))
                return;

            var definitionsToRemove = new List<Definition>();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                Definition key = it.Key;
                if (key == null || !string.Equals(key.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (it.Current is InstanceBinding)
                    definitionsToRemove.Add(key);
            }

            foreach (Definition definition in definitionsToRemove)
            {
                try
                {
                    doc.ParameterBindings.Remove(definition);
                }
                catch
                {
                }
            }
        }
    }
}
