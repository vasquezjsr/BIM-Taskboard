# Spool Auto-Dimension Rules (LOCKED)

> Do **not** regress these without an explicit user request.
> Material does **not** matter — copper, steel, stainless, PVC, cast iron, galvanized: same rules.

## Universal origins (all views: Front / Back / Left / Right / Top)

| Symbol | Kind | Rule |
|--------|------|------|
| **C** | Elbow, Tee | Connector-ray / centerline **intersection**. Never OD heel, outer silhouette, connector origin, or instance origin. |
| **C** | Olet | Body **center**. Stacked dims: short-side pipe **E** → olet **C** (not flange base). |
| **E** | Pipe | Open-end connector on the run (walk through welds). Never fitting center. |
| **F** | Flange | **Base of the flange** (pipe/weld-neck seat on the run). Never gasket faces / accessories. |
| **T** | Tee | Hub / centerline intersection (**C**). |

## Absolute UI rules

1. **Only view-horizontal or view-vertical Linear dims.** Never Aligned. Never a slanted dim line.
2. **Never delete a dimension** unless a proven square replacement already exists (or placement failed before commit).
3. **No DetailLine helpers** — model `Linear` via `Document.Create.NewDimension` only.
4. **Valves / gaskets / welds / bolt kits** — never fitting dimension anchors.
5. Lesson indices (Step 1/2) are **soft prefs** — keep only if sample ≤ ½″ of geometric **C** / flange **F**. Shared indices that land on OD heel must lose.

## Placement policies

0. **Offset (every view: Front / Back / Left / Right / Top):** **First** dim on a side clears **only the originating parts** (not whole-assembly / flange OD). **Stacked** dims on that side nest only by the dimension style **Dimension Line Snap Distance** (Linear 3/32″ Arial = **1/4″**) from the dim under them. Opposite sides and H vs V each start at slot 0.
1. Per-run stack counters — horizontal run, vertical drop, fitting–flange stub, branch takeoff do not share offsets across different physical runs.
2. Olet stack — pick-ups on one host share one short-side **E**; stack outward closest-first. **Always pull the olet dim to the side the olet faces** and clear only that origin. Facing user on V → L/R discretionary, no dim contact. Flange-to-flange outside an olet is a stacked dim — same side, + snap only.
3. Fitting → flange stub (**C→F**) — elbow/tee **C** to flange **F** (incl. zero-hop).
4. Vertical drop legs — **E↔C** as applicable.
5. Open-end → flange overall — one **E→F** per spool/view axis.
6. Branch takeoff (olet) — host **CL** ↔ branch end / olet **C** per policy.

## Golden examples (learn from these — do not invent)

Approved sheets/views live in:

- `GOLDEN_AUTODIM_EXAMPLES.json` — machine-readable captures
- `GOLDEN_AUTODIM.md` — how to add examples and how to seed a Golden `.rvt`

When fixing auto-dim, **read the golden set first**. A regen that fails a golden example is a regression.

## Key code homes

- Rules / lessons: `CreateSpoolSheetsHandler.SpoolAutoDimensionReferenceRules.cs`
- Placement: `CreateSpoolSheetsHandler.AutoDimensionRules.cs`
- Anchors: `GetFabricationFittingDimensionAnchor`, flange face/base resolvers
- Scoring: `ScoreFabricationInstanceReferenceForDimension`
- Classification: `FabricationPartClassification` (elbow / flange / olet / tee — **not** material-gated for dim rules)
