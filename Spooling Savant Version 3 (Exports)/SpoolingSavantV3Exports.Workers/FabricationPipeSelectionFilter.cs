using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI.Selection;

namespace ABMEP.Addins.Hangers
{
    public class FabricationPipeSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            FabricationPart fabricationPart = elem as FabricationPart;
            if (fabricationPart == null)
            {
                return false;
            }

            return fabricationPart.Category != null
                && fabricationPart.Category.Id.Value == (long) BuiltInCategory.OST_FabricationPipework;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }
}
