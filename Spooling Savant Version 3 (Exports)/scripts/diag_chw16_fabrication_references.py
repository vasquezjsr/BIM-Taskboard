# -*- coding: utf-8 -*-
"""
CHW-16 fabrication reference diagnostic (pyRevit / RevitPythonShell)

Run with the Front elevation view active (or edit ASSEMBLY_NAME / VIEW_NAME below).
Dumps per-part reference strategies to the output window and optionally to a log file.

Usage (pyRevit):
  1. Open the CHW-16 assembly Front View in Revit
  2. Extensions > pyRevit > Dev Tools > Run (or add as custom button)
  3. Point this script and run
"""

from __future__ import print_function

import clr
clr.AddReference("RevitAPI")
clr.AddReference("RevitAPIUI")

from Autodesk.Revit.DB import (
    FilteredElementCollector, BuiltInCategory, Options, ViewDetailLevel,
    FabricationPart, AssemblyInstance, Reference, Line, ReferenceArray,
    Transaction, TransactionStatus, ElementId
)
from Autodesk.Revit.UI import UIDocument

ASSEMBLY_NAME = "CHW-16"
LOG_PATH = None  # e.g. r"C:\Users\...\chw16-ref-diag.txt"


def log(msg):
    print(msg)
    if LOG_PATH:
        with open(LOG_PATH, "a") as f:
            f.write(msg + "\n")


def find_assembly(doc, name):
    for a in FilteredElementCollector(doc).OfClass(AssemblyInstance):
        if a.Name == name:
            return a
    return None


def ref_type_name(ref):
    try:
        return str(ref.ElementReferenceType)
    except:
        return "?"


def stable(doc, ref):
    try:
        return ref.ConvertToStableRepresentation(doc)
    except Exception as ex:
        return "<err: {}>".format(ex)


def geom_stats(element, view):
    opts_view = Options()
    opts_view.ComputeReferences = True
    opts_view.View = view
    opts_view.IncludeNonVisibleObjects = False

    opts_model = Options()
    opts_model.ComputeReferences = True
    opts_model.IncludeNonVisibleObjects = False

    stats = {"solids": 0, "edges_with_ref": 0, "faces_with_ref": 0, "curves_with_ref": 0}
    for label, opts in [("view", opts_view), ("model", opts_model)]:
        try:
            geo = element.get_Geometry(opts)
        except:
            geo = None
        if not geo:
            continue
        for g in geo:
            solid = g if hasattr(g, "Faces") else None
            if solid:
                stats["solids"] += 1
                for face in solid.Faces:
                    if face and face.Reference:
                        stats["faces_with_ref"] += 1
                for edge in solid.Edges:
                    if edge and edge.Reference:
                        stats["edges_with_ref"] += 1
        log("  geometry[{}]: solids={} edges_ref={} faces_ref={}".format(
            label, stats["solids"], stats["edges_with_ref"], stats["faces_with_ref"]))
    return stats


def subelement_report(doc, element):
    count = 0
    try:
        for sub in element.GetSubelements():
            count += 1
            try:
                r = sub.GetReference()
                log("    subelement[{}] stable={} type={}".format(
                    count, stable(doc, r), ref_type_name(r)))
            except Exception as ex:
                log("    subelement[{}] error: {}".format(count, ex))
    except Exception as ex:
        log("    GetSubelements failed: {}".format(ex))
    if count == 0:
        log("    (no subelements)")


def location_curve_report(element):
    loc = element.Location
    if not loc or not hasattr(loc, "Curve"):
        log("    location curve: none")
        return
    c = loc.Curve
    try:
        r0 = c.GetEndPointReference(0)
        r1 = c.GetEndPointReference(1)
        log("    curve end ref[0]: {} type={}".format(
            "ok" if r0 else "NULL", ref_type_name(r0) if r0 else "-"))
        log("    curve end ref[1]: {} type={}".format(
            "ok" if r1 else "NULL", ref_type_name(r1) if r1 else "-"))
    except Exception as ex:
        log("    curve end ref error: {}".format(ex))


def dry_run_new_dimension(doc, view, ref_a, ref_b, label):
    if not ref_a or not ref_b:
        log("    dry-run {}: skipped (missing ref)".format(label))
        return
    t = Transaction(doc, "CHW16 ref diag dry-run")
    t.Start()
    try:
        ra = ReferenceArray()
        ra.Append(ref_a)
        ra.Append(ref_b)
        line = Line.CreateBound(
            __import__("Autodesk.Revit.DB", fromlist=["XYZ"]).XYZ(0, 0, 0),
            __import__("Autodesk.Revit.DB", fromlist=["XYZ"]).XYZ(1, 0, 0))
        dim = doc.Create.NewDimension(view, line, ra)
        log("    dry-run {}: SUCCESS dim id={}".format(label, dim.Id if dim else None))
    except Exception as ex:
        log("    dry-run {}: FAIL {}".format(label, ex))
    finally:
        if t.HasStarted():
            t.RollBack()


def diagnose_part(doc, view, part):
    eid = part.Id.IntegerValue
    log("\n--- Part Id={} Name={} Category={} ---".format(
        eid, part.Name, part.Category.Name if part.Category else "?"))
    subelement_report(doc, part)
    location_curve_report(part)
    geom_stats(part, view)
    try:
        whole = Reference(part)
        log("    whole-element ref stable={}".format(stable(doc, whole)))
    except Exception as ex:
        log("    whole-element ref error: {}".format(ex))


def main():
    uidoc = __revit__.ActiveUIDocument  # noqa: F821 pyRevit injects __revit__
    doc = uidoc.Document
    view = doc.ActiveView
    log("=== CHW-16 fabrication reference diagnostic ===")
    log("Active view: {} (id={})".format(view.Name, view.Id.IntegerValue))

    asm = find_assembly(doc, ASSEMBLY_NAME)
    if not asm:
        log("Assembly '{}' not found.".format(ASSEMBLY_NAME))
        return

    parts = []
    for mid in asm.GetMemberIds():
        el = doc.GetElement(mid)
        if isinstance(el, FabricationPart):
            parts.append(el)

    log("Found {} fabrication members.".format(len(parts)))
    for part in parts:
        diagnose_part(doc, view, part)

  # Optional: dry-run dimension between first two parts with curve-end refs if found
    log("\n=== Done. Review output above before changing dimension code. ===")


if __name__ == "__main__":
    main()
