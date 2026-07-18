# Spooling Savant V3 (Exports)

> **Order of operations (Boardroom + SSv3 + Fab):** see [`../ORDER_OF_OPERATIONS.md`](../ORDER_OF_OPERATIONS.md).

Experimental fork of Spooling Savant V2 for BIM Boardroom export work.

- **Source location:** `C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)`
- **Assemblies:** `SpoolingSavantV3Exports` / `SpoolingSavantV3Exports.Workers`
- **Revit deploy folder:** `Spooling-Savant-V3-Exports` (does not overwrite V2)

Production V2 remains at `C:\Addins\Spooling Savant Version 2`.

## Build

```powershell
.\scripts\Build-Ribbon-Shell.ps1 -Configuration Release
.\scripts\Build-Workers.ps1 -Configuration Release
```

## Boardroom integration notes

### Package export targets
- Packages export into **Fab** (shop build) and later **Shipping**.
- Transport: shared export folder (`boardroom-package.json` + report files).
- **Live connection:** with BIM Boardroom running, SS Manager loads projects and Spooling-board tasks from `http://127.0.0.1:17321` (Settings → Boardroom).
- Spooler picks a **Spooling task**; export attaches to that task. Fab package/assemblies appear only after the task is marked **Ready for Fab** in Boardroom.
- Reports: include all Plot Package reports (Spools Combined, **Spool Map**, Assembly List, BOM, Cut List, Weld Log, TigerStop, PCF). PDFs are written into the chosen export folder for field printing. TigerStop / PCF may be stub/fake files until shop machines are wired.
- **S-Package on Sheets/Views:** S-Package is bound to Assemblies, fabrication parts, **Sheets**, and **Views**. Create Spool Map / Create Spool Sheets stamp the package value on those sheets and views so Spool Map can export with the rest of the package reports.

### Boardroom API
- Default: `http://127.0.0.1:17321`
- `GET /health`, `GET /v1/projects`, `GET /v1/projects/{id}/tasks?boardType=spooling`
- Boardroom Electron must be open so the snapshot is published.

### Future — Fab shop workstation time tracking
When we build the fab-shop worker workstation dashboard:
- Workers **clock in / clock out** of a project (or package/assembly job).
- Capture **start time** and **finish time** so labor is tracked **per project**.
- This feeds Boardroom time tracking for fab shop hours (same idea as existing time entries, scoped to shop workstation sessions).
