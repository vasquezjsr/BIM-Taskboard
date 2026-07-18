# Golden Auto-Dim Examples

## Why this exists

You already have projects (e.g. Test R24) where auto-dim is correct for dozens of assemblies.
What we lacked was a **durable memory** of those correct sheets so Workers / agents stop “rediscovering”
wrong witnesses (OD heel, gasket face, tilted Linear, missing H stub).

This folder is that memory. It is **not** “match Test R24” — it is **match the locked rules**, using
approved examples as proof.

## Virtual / golden model (in Cursor)

Open the interactive canvas beside chat:

- [`golden-autodim-spool.canvas.tsx`](../../../../Users/vasqu/.cursor/projects/c-Apps-BIM-Taskboard/canvases/golden-autodim-spool.canvas.tsx) — Elevation Right L-spool with Correct / Wrong toggles and golden dim highlights.

That is the “virtual model in here.” A blank `.rvt` can’t teach fabrication dims; this canvas + `GOLDEN_AUTODIM_EXAMPLES.json` is the durable memory agents use.

## Golden Revit project (optional later)

A blank `.rvt` with no Fabrication database cannot reproduce spool dims. Practical phases:

1. **Now — example registry + Cursor canvas (done):** JSON + interactive Elevation Right canvas.
2. **Next — seed Golden AutoDim.rvt:** In Revit, **Save As** a small project, copy in 5–10
   representative assemblies (L-elbow+flanges, olet stack, tee branch, copper + steel). Keep it local
   under `C:\Revit\Golden AutoDim\` (or similar). Point new examples at that path.
3. **Then — verify on regen:** After Create/Refresh, an agent/script compares Elevation dims to
   the golden entry for that assembly name (inches ± 1/16″, angle ≤ 0.05°, required roles present).

You apply examples by generating a sheet that looks right, then asking the agent:
“Capture this elevation as golden for assembly X.”

## Adding an example checklist

### From the teaching canvas (preferred for new families)

1. Draw the assembly (Pipe + fittings + Dims).
2. Say in chat: **“capture this board as golden for &lt;name&gt;”**  
   — or click **Submit as golden** on the canvas (opens a chat that asks the agent to capture).
3. The agent appends an entry to `GOLDEN_AUTODIM_EXAMPLES.json` (including board snapshot).
4. Future agents read that file + `.cursor/rules/spool-autodim-golden.mdc` and must not regress it.

### From Revit (sheet that already looks right)

1. Open the good sheet / elevation in Revit.
2. Confirm rules: elbow/tee/olet → **C**, flange → **F** face / backside **E**, pipe end → **E**, dims square.
3. Ask the agent: “Capture this elevation as golden for assembly X.”
4. Agent dumps views + dims into `GOLDEN_AUTODIM_EXAMPLES.json`.
5. Rebuild Workers only if code changed; examples alone already train the agent.

## Failure policy

If regen diverges from a golden example for the same assembly:

- Prefer geometric **C** / flange **F** over max-span / OD lesson indices.
- Never leave a view missing a whole H or V run dim that the golden set has.
- Never ship tilted Linear dims.
