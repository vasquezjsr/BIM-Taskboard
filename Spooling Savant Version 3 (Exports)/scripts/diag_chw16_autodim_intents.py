# -*- coding: utf-8 -*-
"""
CHW-16 auto-dimension intent diagnostic (pyRevit)

Run with Front View active after a failed Create Spool Sheets pass.
Prints run-axis resolution, olet-horizontal eligibility, and member summary.

Also reads AutoDimPlacement.log if present (written by latest Workers build).

Usage: pyRevit > Run script (or RevitPythonShell)
"""

from __future__ import print_function

import clr
import os

clr.AddReference("RevitAPI")
clr.AddReference("RevitAPIUI")

from Autodesk.Revit.DB import FilteredElementCollector, FabricationPart, AssemblyInstance, Line

ASSEMBLY_NAME = "CHW-16"
LOG_PATHS = [
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "Spooling-Savant", "SpoolingManager", "TestingReports", "AutoDimPlacement.log"),
    os.path.join(os.environ.get("ProgramData", ""), "Spooling Savant", "TestingReports", "AutoDimPlacement.log"),
]


def log(msg):
    print(msg)


def find_assembly(doc, name):
    for a in FilteredElementCollector(doc).OfClass(AssemblyInstance):
        if a.Name == name:
            return a
    return None


def pipe_length(part):
    loc = part.Location
    if not loc or not hasattr(loc, "Curve"):
        return 0.0
    c = loc.Curve
    if not c or not c.IsBound:
        return 0.0
    try:
        return c.Length
    except:
        return 0.0


def project_to_view_plane(vec, view_dir):
    if vec is None:
        return None
    d = view_dir.Normalize()
    v = vec.Normalize()
    v = v - d.Multiply(v.DotProduct(d))
    if v.GetLength() < 1e-9:
        return None
    return v.Normalize()


def canonical_key(direction):
    d = direction.Normalize()
    if d.X < -1e-9 or (abs(d.X) <= 1e-9 and d.Y < -1e-9):
        d = d.Negate()
    return "{0:.4f}|{1:.4f}|{2:.4f}".format(d.X, d.Y, d.Z)


def summarize_run_axes(parts, view):
    view_dir = view.ViewDirection.Normalize()
    buckets = {}
    for p in parts:
        loc = p.Location
        if not loc or not hasattr(loc, "Curve"):
            continue
        c = loc.Curve
        line = c if isinstance(c, Line) else None
        if line is None:
            continue
        in_plane = project_to_view_plane(line.Direction, view_dir)
        if in_plane is None:
            continue
        key = canonical_key(in_plane)
        length = pipe_length(p)
        if key not in buckets:
            buckets[key] = [0.0, in_plane]
        buckets[key][0] += length
    if not buckets:
        log("  (no pipe axes in view plane)")
        return
    for key, (total, direction) in sorted(buckets.items(), key=lambda x: -x[1][0]):
        log("  axis {0} totalLen={1:.2f}' dir=({2:.3f},{3:.3f},{4:.3f})".format(
            key, total, direction.X, direction.Y, direction.Z))


def dump_placement_log():
    for path in LOG_PATHS:
        if not path or not os.path.isfile(path):
            continue
        log("\n=== AutoDimPlacement.log ({}) ===".format(path))
        try:
            with open(path, "r") as f:
                for line in f.readlines()[-40:]:
                    log(line.rstrip())
        except Exception as ex:
            log("  read error: " + str(ex))
        return
    log("\n(No AutoDimPlacement.log found — hotload latest Workers build and run Create Spool Sheets once)")


def main():
    uidoc = __revit__.ActiveUIDocument  # noqa: F821
    doc = uidoc.Document
    view = doc.ActiveView
    log("=== CHW-16 auto-dim diagnostic ===")
    log("View: {} (id={})".format(view.Name, view.Id.IntegerValue))

    asm = find_assembly(doc, ASSEMBLY_NAME)
    if not asm:
        log("Assembly '{}' not found.".format(ASSEMBLY_NAME))
        return

    parts = [doc.GetElement(mid) for mid in asm.GetMemberIds()]
    parts = [p for p in parts if isinstance(p, FabricationPart)]
    log("Fabrication members: {}".format(len(parts)))

    pipes = [p for p in parts if p.Name and "Pipe" in p.Name]
    log("\nLongest pipe segments:")
    for p in sorted(pipes, key=pipe_length, reverse=True)[:6]:
        log("  id={} len={:.2f}' name={}".format(p.Id.IntegerValue, pipe_length(p), p.Name))

    log("\nRun axis buckets (same logic as Workers TryGetRunAxisInViewPlane):")
    summarize_run_axes(parts, view)

    olets = [p for p in parts if p.Name and "ANVILET" in p.Name.upper() or "OLET" in p.Name.upper()]
    log("\nOlet/anvilet count: {}".format(len(olets)))

    dump_placement_log()
    log("\n=== Done ===")


if __name__ == "__main__":
    main()
