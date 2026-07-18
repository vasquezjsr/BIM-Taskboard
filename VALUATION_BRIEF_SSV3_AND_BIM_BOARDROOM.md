# Product Valuation Brief — Spooling Savant V3 (SSv3) + BIM Boardroom

**Purpose of this document:** Give an AI or analyst full product context for valuation, competitive positioning, or investment diligence.  
**Products covered:** (1) Spooling Savant Version 3 Exports / SS Manager V3 — Autodesk Revit add-in; (2) BIM Boardroom — Electron desktop operations app; (3) how they integrate as one vertical workflow for MEP BIM shops.  
**Version context:** BIM Boardroom npm package `bim-task-board` v1.0.0, productName “BIM Boardroom”. Local-first desktop product with Revit companion add-in — not multi-tenant cloud SaaS.

---

## 1. One-sentence summaries

| Product | Summary |
|---------|---------|
| **BIM Boardroom** | Desktop “shop OS” for MEP BIM companies: Client → Project → role-aware boards from detailing through fab, shipping, and field, plus org/permissions, time clocks, and shop workstations fed by Revit package exports. |
| **Spooling Savant V3 (Exports)** | Revit add-in (fork of production Spooling Savant V2) that packages fabrication assemblies into S-Packages, plots shop reports, and exports a structured package + files into Boardroom for fab handoff. |
| **Combined system** | Office VDC boards + Revit spool data + role-based fab floor UI (Queued → Warehouse → Dept Lead/Workers) in one vertical stack — uncommon vs generic PM tools (Asana/Jira) or CAD-only add-ins. |

---

## 2. Problem / market context

MEP (mechanical, electrical, plumbing) fabrication shops that do BIM/VDC typically juggle:

1. **Office work** — modeling, coordination, deliverables, RFIs, documents in project boards.
2. **Revit spooling** — grouping assemblies into packages, creating spool sheets, BOMs, cut lists, weld logs, CNC-oriented files.
3. **Shop floor** — queue packages, pull material (warehouse), fabricate by trade department, fill weld logs, clock time, then ship and install in the field.
4. **People / permissions** — owners, BIM managers, PMs, detailers, support, fab shop (shop super, warehouse, dept managers, workers), shipping, field.

Generic project tools don’t speak Revit package structure. Revit add-ins don’t run shop queues, warehouse BOM pull, weld-log tap-fill, or org-aware dashboards. **This stack connects both.**

---

## 3. Architecture at a glance

```
┌─────────────────────────────────────┐     loopback HTTP :17321      ┌──────────────────────────────────┐
│  Autodesk Revit                     │◄────────────────────────────►│  BIM Boardroom (Electron)         │
│  Spooling Savant V3 / SS Manager V3 │                               │  React UI + Zustand persist       │
│  • Package assemblies (S-Package)   │   watched folder +            │  • Clients / project boards       │
│  • Spool sheets & plot reports      │   boardroom-package.json      │  • Spooling → Ready for Fab       │
│  • Export to Boardroom              │──────────────────────────────►│  • Shop Dashboard (Fab)           │
└─────────────────────────────────────┘                               │  • Time, org, permissions, reports│
                                                                      └──────────────────────────────────┘
```

**Integration contract**

| Direction | Mechanism |
|-----------|-----------|
| Boardroom → SSv3 | Local HTTP API on `127.0.0.1:17321` — health, list projects, list Spooling tasks (with `hasSsv3Export` flag) |
| SSv3 → Boardroom | Shared export folder containing `boardroom-package.json` (schema `bim-boardroom-package-v1`) + sibling report files (PDF, XLSX, CSV, PCF) |
| Ingest | Electron filesystem watch → attach package/assemblies/files to chosen Spooling task |
| Promote gate | Spooling status **Ready for Fab** → move package tree to Fab as **Queued** and **lock** further SSv3 overwrite |

**Deployment model:** Single-machine / local-first. App state in localStorage; attachment blobs in IndexedDB. Windows NSIS installer via electron-builder. Web/Vite mode exists for development (dual ports for View-As testing). Not a networked multi-user cloud backend.

---

# PART I — Spooling Savant V3 (Exports) / SS Manager V3

## 4. What SSv3 is

| Item | Detail |
|------|--------|
| **Product UI name** | SS Manager V3 |
| **Add-in name** | Spooling Savant V3 (Exports) |
| **Host** | Autodesk Revit |
| **Lineage** | Experimental / Boardroom-oriented fork of production Spooling Savant Version 2 (V2 remains a separate production install; V3 deploys to its own ProgramData add-ins folder and does not overwrite V2) |
| **Assemblies** | Ribbon shell DLL + hot-loadable Workers DLL |
| **Primary job** | Group fabrication assemblies into packages, generate spool documentation, plot shop reports, and hand packages to BIM Boardroom |

## 5. SSv3 capabilities (feature inventory)

### 5.1 Package & assembly management (SS Manager pane)

- Search assemblies; Refresh List; Clear; Isolate; Hide
- Package tree grouped by Revit parameter **`S-Package`**
- Collapse All / Expand All package groups
- Indicator when a spool sheet already exists for an assembly
- Package name field → writes **`S-Package`**
- **Add to Package** / **Remove from Package**

### 5.2 Sheet operations

- **Create Spool Sheets**
- **Open Sheets**
- **Rename Sheets**
- **Refresh Assembly**
- **Create Spool Map** — 3D + plan for one package with assembly tags on 3D

### 5.3 Plot Packages (standalone plotting)

User picks report types and an output folder. Typical outputs:

| Report option | Typical file |
|---------------|--------------|
| Spools Combined | `{Package} - Spools Combined.pdf` |
| Assembly List | `{Package} - Assembly List.pdf` |
| Bill of Materials | `{Package} - Bill of Materials.pdf` |
| Cut List | `{Package} - Cut List.pdf` (may split by material) |
| Weld Log | `{Package} - Weld Log.xlsx` (requires `S-Weld` values) |
| TigerStop (Copper / PVC CSV) | CSVs for straight pipe (keyword-driven Copper/PVC) |
| PCF Files (all materials) | `.pcf` by material keywords (Copper / PVC / Steel / Cast Iron) |

Report header fields on list-style reports: **Project**, **Created By**, **Date**.  
Printer setting defaults include **Bluebeam PDF** and print setting **11x17 Spool Sheets**.

### 5.4 Export to Boardroom (integration export)

Same report checkboxes as Plot Packages, plus:

- Live pick of **Boardroom project** (from API)
- Live pick of **Spooling board task** (shows “(has export)” when already attached)
- **Export folder** selection (default under repo `…/Boardroom/Exports`)
- Overwrite confirmation if task already has an export
- Writes all selected reports **and** `boardroom-package.json`

### 5.5 Generate Options / Settings (concrete option set)

**Annotations / Generate Options**

- Number Welds — number/tag shop welds, field welds, o-lets on Generate/Refresh
- Fill Weld Log — sheet text notes per visible weld
- Include Weld Log entry fields — fillable Date / Welder ID / Initials on Spools Combined PDFs
- Continuation Tags
- Prefix weld tags with Package # (e.g. `B0001-CHW-11-01`)
- Place tracking QR on spool sheets — 1" QR; payload like `SSV3|P=…|A=…`
- Optional QR URL base — `{base}?p=…&a=…`

**Sheet Setup**

- Title Block, Viewport Type, View Scale
- Views: 3D Ortho, Back / Front / Left / Right / Top — Direction/Rotation, Tag Y/N, Auto Dim, Placement, View Template

**Schedules**

- Filter-by-Sheet schedules; Top Left / Top Right placement; Add schedule

**Annotation type pickers**

- Pipe/Fitting Tag, Hanger Tag, Duct Tag, Weld Tag Type, Continuation Tag Type, Weld Log Text Type, Weld Log Source View, Linear Dimension Type, Dim Annotations

**TigerStop**

- CSV column order (default includes Quantity, LengthInches, Package, ItemNumber, Size, LengthFtIn, Material, Spool)
- Copper / PVC keywords

**PCF Files**

- Field order, Piping Specification Catalog path
- Copper / PVC / Steel / Cast Iron keywords

**Boardroom tab**

- Boardroom API URL (default `http://127.0.0.1:17321`)
- Test connection / Use default URL

**Layout (gear)**

- Schedule / weld-log insets from title block

### 5.6 Data produced — `boardroom-package.json`

Schema: **`bim-boardroom-package-v1`**

Conceptual structure:

- `schema`, `exportedAt` (ISO)
- `targets`: e.g. `["fab", "shipping"]`
- `boardroomProject`: `{ id, name, clientName, jobCode }`
- `boardroomTask`: `{ id, taskNumber, title }`
- `packages[]`: each with `sPackage` and `assemblies[]` including `revitElementId`, `name`, `sheetName`, `sheetNumber`, `qr`
- `files[]`: `{ fileName, type }` for pdf / xlsx / pcf / etc.

Boardroom attaches sibling files as absolute-path references and **excludes** the JSON itself, `*.bak*`, and Piping Specification Catalog-named files from the attachment set.

### 5.7 SSv3 maturity / known notes

- Built specifically for Boardroom handoff; packages conceptually target **Fab** then **Shipping**.
- TigerStop / PCF paths may be incomplete stubs relative to full V2 production depth (treat as capability surface, not necessarily equal V2 maturity).
- Boardroom Electron must be running for live project/task API during Export to Boardroom.

---

# PART II — BIM Boardroom

## 6. What BIM Boardroom is

| Item | Detail |
|------|--------|
| **Product name** | BIM Boardroom |
| **Form factor** | Electron desktop app (primary); Vite web for development |
| **UI stack** | React 19, TypeScript, Vite 6, Zustand 5, dnd-kit, pdf.js / pdf-lib / jsPDF, SheetJS (xlsx) |
| **Persistence** | localStorage (app state); IndexedDB (file blobs); optional dual-port dev sync |
| **Auth** | Local employee login (id + password); invite-by-email helper text |
| **Purpose** | End-to-end operations from client/project boards through spooling, fab shop, shipping, and field, with org chart, permissions, time tracking, reports, and SSv3 package ingest |

## 7. Main navigation / dashboards (every tab)

Always visible: **Clients**, **Task Board**, **Employees**. Other tabs gated by AppPermissions (+ Owner auto-grants).

| Nav label | What it does |
|-----------|--------------|
| **Owner Dashboard** | Portfolio health: active projects, avg lifecycle progress, hours vs budget, at-risk signals, BIM lifecycle rollup, project list → opens Clients Main Overview |
| **PM Dashboard** | PM’s jobs (or all if manage-org); project cards; PM board tasks; Assign PM; opens Project Management board |
| **Clients** | Portfolio cards **or** board mode: Clients → Projects → Boards (spreadsheet home for all project work) |
| **Task Board** | Office kanban: Detailers \| Support Specialists; Unassigned + per-person columns; drag/reorder by priority/due |
| **Shop Dashboard** (Fab Workstation) | Queued / Warehouse / Fabrication workstations for SSv3 packages after Ready for Fab |
| **Shipping Dashboard** | Shipping board task table (number, title, project, status) |
| **Field Dashboard** | Field board task table |
| **Time Tracking** | Hours form, Quick Tasks, day/week/month calendar, open Fab clocks |
| **Employees** | Roster, job titles, permissions, dashboard roles, Welder ID, invites |
| **Organizational Chart** | Reporting tree; pan/zoom; drag reparent with manage-org |
| **Activity Log** | Audit trail; restore deleted columns (with manage-columns) |
| **Access Control** | Permissions + nav visibility matrix by job level |

**Header (not nav):** Reports dialog (scoped to active area), View As perspectives, Login / user badge.

### Dashboard roles (shape *inside* a tab — not the same as tab permission)

| Dashboard | Assignable roles |
|-----------|------------------|
| PM | Project Managers, Assistant PMs |
| Field | Site Superintendents, Foremen, Crew Leads |
| Fab / Shop | Shop Super, Warehouse Lead, Warehouse Worker, Shop Dept Manager (Mech / Plmb / HVAC), Workers |
| Shipping | Shipping Manager, Workers |

## 8. Project hierarchy & boards (Clients)

**Hierarchy:** Client → Project → boards.

**Default sub-board order:** Project Management → RFI → Documents → Detailers → Deliverables → Spooling → Fabrication → Shipping → Field (+ custom boards).

### 8.1 Main Overview (`main`)

- Rollup of PM, RFI, Documents, Detailers, Deliverables, Spooling (+ custom sections)
- Does **not** include Fab/Shipping/Field by default
- Section groups; union of section columns + trailing Board column

### 8.2 Project Management (`project-managers`)

Statuses: Not Started → Contract Review → Kickoff Complete → BEP Approved → Model Setup → Clash Cycle Active → Clashes Resolved → IFC Ready → On Hold → Complete.  
Template PM checklist; fixed Due Date; linked from PM Dashboard.

### 8.3 RFI (`rfi`)

**Locked** statuses: Waiting for Response → Complete. Flat/minimal board.

### 8.4 Documents (`documents`)

Not Started → Requested → Received → Linked to Model → Verified → On Hold → Complete. Support assignee pool.

### 8.5 Detailers (`detailers`)

Not Started → Backgrounds Linked → Modeling (LOD 300) → Hangers & Supports → Detailer QA → Ready for Coordination → Rework → On Hold → Complete. Trade/level grouping; feeds Task Board Detailers lane.

### 8.6 Deliverables (`deliverables`)

Statuses include pre-planning through spool QA / Ready for Fab / Complete, with auto-assign toward detailers vs support.  
**Critical rule:** Ready for Fab on **Deliverables does not** promote packages to Fab — only **Spooling** + SSv3 export does.

### 8.7 Spooling (`spooling`)

Not Started → Ready for Spooling → Spool In Progress → Spool QA Review → Spool Approved → **Ready for Fab** → On Hold → Complete.  
SSv3 export attaches here; **Ready for Fab + export → promote to Fab Queued**; export locked after promote.

### 8.8 Fabrication (`fab`)

Not Started → Queued → Pulling Material → Material Pulled → In Fab (Mech) → In Fab (Plumb) → In Fab (HVAC) → QA Review → Rework → Ready to Ship → Complete.  
Spreadsheet twin of Shop Dashboard. **No auto-promote to Shipping** yet.

### 8.9 Shipping (`shipping`)

Not Started → Staging → Loading → In Transit → Delivered to Site → Received by Field → Complete.

### 8.10 Field (`field`)

Not Started → Mobilization → Material On Site → Rough-In → Hydro / Test → Trim-Out → Punch List → As-Built Update → Final Inspection → Complete.

### 8.11 Custom boards (`cb-*`)

Per-project named tabs; reorderable; generic or customized statuses; can appear as Main Overview sections.

### 8.12 Project settings (options)

- Detailer / Support / PM team IDs
- Revit year: 2022–2026; model type cloud/local
- Building levels + active levels
- Billing: **lump-sum** (budget hours) or **time-and-material** (hours spent)
- Start/end dates, **job code**, sequential **task numbers**
- Master **template** projects for duplication

## 9. Shop Dashboard / Fab workstation (deep)

Modes conceptually: **Queued** | **Warehouse** | **Personal / Fabrication workstations**.

| Mode | Typical users | Packages shown | Key actions |
|------|---------------|----------------|-------------|
| **Queued** | Shop Super, Owner | All packages (with project filter) | Assign **Dept Lead** only; open files; photos/comments |
| **Warehouse** | Warehouse Lead / Worker | Status Queued or Pulling Material | Focus **Bill of Materials** PDF; Queued → Pulling Material → Material Pulled; clock; photos/comments |
| **Personal workstation** | Dept Mgr / Worker | Packages where user is Dept Lead, package Worker, or assembly assignee | Assign workers/assemblies; fab statuses; sheet viewer; weld-log tap-fill; clock; photos/comments |

**Shared shop tools**

- Clean PDF / sheet viewer (per-assembly pages from Spools Combined, plus other export files)
- Interactive **Weld Log** from export `.xlsx`: tap-fill Date / Welder ID / Initials; write back to file
- **Photos** + **Comments** on packages
- Package **clock in/out** → Time Tracking entries tied to task/client/project
- Independent shop **project filter** (not forced to Clients active project)
- Resizable panes with persisted widths
- Fabrication nav can nest workers under their managers

**SSv3-related custom fields on tasks (examples):** `ssv3Kind`, `ssPackage`, `ssv3Packages`, `ssv3ExportFolder`, `ssv3ExportedAt`, `ssv3Files`, `revitElementId`, QR/sheet metadata, `ssv3FabPackageTaskId`, `ssv3DeptLeadId`, `ssv3WorkerId`.

**Known gap:** Fab status **Ready to Ship** does not auto-create Shipping tasks.

## 10. People systems

### 10.1 Employees stages (UI lanes)

Leadership | Detailers | Support | Project Management | Field | Fab Shop | Shipping

### 10.2 Job levels (`orgCategory`)

Owner, BIM Manager, Operations Manager, Operations Staff, Lead Plumbing/Mechanical/Sheet Metal Detailer, Junior Detailer, Support Specialist Manager, Support Specialist

### 10.3 Job titles

Catalog of default titles (BIM Manager, Ops Manager, trade detailers, PM/APM, Field titles, Shop Super, Warehouse Lead, Shop Dept Managers by trade, Fab Worker, Shipping titles, etc.). **Custom job titles** can be added and placed on a stage (Leadership, Detailers, PM, Fab Shop, etc.). Employees can change job via UI.

### 10.4 Employee fields

Name, role (`detailer` | `support-specialist` | `operations`), orgCategory, jobTitleId, optional **Welder ID**, login credentials, Works Under (reports-to), permission chips, dashboard role assignments, invite-by-email.

### 10.5 Permission catalog (12)

| Permission | Unlocks |
|------------|---------|
| `edit-budget-hours` | Edit project budget hours (+ runtime org-chart view) |
| `manage-org` | Roster, org chart edit, Access Control-related admin, dashboard roles; all projects on PM Dashboard |
| `manage-columns` | Delete/manage columns; restore from Activity Log |
| `view-activity-log` | Activity Log tab |
| `view-org-chart` | Org Chart tab |
| `view-owner-dashboard` | Owner Dashboard |
| `view-pm-dashboard` | PM Dashboard |
| `view-field-dashboard` | Field Dashboard |
| `view-fab-dashboard` | Shop / Fab Dashboard |
| `view-shipping-dashboard` | Shipping Dashboard |
| `view-visibility-dashboard` | Access Control |
| `view-time-tracking` | Time Tracking |

Default permission matrices differ by job level (Owner gets nearly everything; detailers get chart + budget; ops staff get department-scoped extras; etc.). Runtime auto-grants apply for Owner and some categories even if a chip is missing.

### 10.6 Org chart

Reporting tree drives time-entry visibility and Fab strip eligibility for upstream managers. Drag reparent with `manage-org`.

### 10.7 Access Control

Matrix of permissions and nav visibility by job level / department (Office, Project Management, Field, Fab Shop, Shipping, Unassigned operations).

## 11. Time Tracking

- Entry form: employee (if allowed), client/project/task, date, start/end (15-minute snap), hours, note
- **Quick Tasks:** open tasks matching assignee / project team / Fab package assignment
- Calendar: day / week / month
- **Clocked in** strip for open Fab clocks; Clock out completes entry
- Visibility: self, upstream reports, or all if Owner
- Fab Warehouse + personal clocks write entries with package `taskId`

## 12. Collaboration, attachments, comments, activity, reports

### Attachments

- Per-task; versioned (`new` / `replace` / `newVersion`); IndexedDB blobs
- Spooling: clear SSv3 export if not locked after Ready for Fab
- Fab photos filter to image mime types

### Comments

- Author + body + timestamp; mark read; delete; used on tasks and Fab packages

### Activity Log

- Cap ~2000 entries
- Actions: created, updated, deleted, restored, status_changed
- Entity types: task, group, column, employee, status, project, comment, time-entry, permission
- Soft-delete archives for columns and employees (restore with permissions)
- Actor attribution respects View As

### Reports (PDF via jsPDF; some CSV)

- **Time:** Weekly by Project, Weekly Summary, Weekly by Employee, Monthly Summary, Daily Detail
- **Clients:** Client & Project Portfolio, Project Budget & Hours Status
- **Tasks:** Status Summary, By Assignee, Open Tasks List, Project Task Progress
- **Org:** Team Roster, Reporting Structure
- Scope follows active main tab; CSV of clients/projects also available for SSv3/helpers

### Status system

- Per-board status lists (customizable except RFI locked)
- Status Settings: label, color, drag reorder, counts-as-complete, auto-assign team (detailers/support) or specific employee
- Spreadsheet columns: text / date / duration / dropdown; templates; shared Main custom columns; workflow due-date packs

### View As

- Switch UI perspective across office / PM / Field / Shipping / Fab roster without changing signed-in identity for shop browsing in current product direction (shop selection is local view, not forced user switch)
- Useful for demos and role testing (dual Vite ports)

## 13. Boardroom API (for SSv3)

Host: **`127.0.0.1:17321`** (loopback only)

| Endpoint | Role |
|----------|------|
| `GET /health` | Liveness |
| `GET /v1/projects?includeTemplates=` | Project picker for Export to Boardroom |
| `GET /v1/projects/{id}` | Project detail |
| `GET /v1/projects/{id}/tasks?boardType=spooling` | Top-level Spooling tasks; includes whether task already has SSv3 export |

Renderer publishes a debounced store snapshot into the API. Electron also provides IPC for export directory, watch, package JSON read, and file preview/write (PDF/xlsx for weld logs).

## 14. Demo / seed portfolio

If no real projects exist, Boardroom can seed a demo mechanical portfolio (e.g. Office Tower, Hospital Wing, Campus Utility) including Spooling task targets suitable for SSv3 export demos. Seeded employees, org chart, and dashboard assignments support end-to-end walkthroughs.

---

# PART III — How they talk to each other (end-to-end)

## 15. Canonical order of operations

1. **Org / roles in Boardroom** — Employees, Org Chart, Fab roles (Shop Super, Warehouse, Dept Managers, Workers); optional Welder IDs.
2. **Client / Project** — Create client and project; confirm job code and teams; ensure Spooling / Fab / Shipping boards exist.
3. **Spooling task** — Create or select Spooling task; keep Boardroom open so API is live.
4. **SSv3 in Revit** — Set Generate Options; package assemblies under `S-Package`; create spool sheets.
5. **Export to Boardroom** — Pick project + Spooling task + folder; write reports + `boardroom-package.json`.
6. **Ingest** — Boardroom watcher attaches package/assemblies/files to the Spooling task. Re-export allowed until Ready for Fab.
7. **Ready for Fab** — Spooling status → Ready for Fab → promote tree to Fab as Queued; **lock** export overwrite.
8. **Queued** — Shop Super assigns Dept Lead.
9. **Warehouse** — BOM focus; Queued → Pulling Material → Material Pulled; clock optional.
10. **Dept Lead / Workers** — Assign assemblies; fab statuses; sheets; weld-log tap-fill; photos/comments; clock.
11. **Time Tracking** — Package clocks appear as time entries.
12. **Shipping / Field** — Boards and dashboards exist; Fab → Shipping is **manual** today (no auto-handoff).

## 16. Lock / replace rules

- Re-export / Clear SSv3 export allowed on Spooling while unlocked
- After **Ready for Fab** (or task already on Fab), overwrite is blocked to protect shop progress
- Same `exportedAt` + folder can no-op the watcher to avoid wiping shop work

## 17. Hierarchy on import / promote

- One package batch → assemblies nest under Spooling task
- Multiple packages → intermediate Package nodes, then assemblies
- Promote **moves** that tree onto Fab board type (does not clone a parallel tree)

---

# PART IV — Valuation framing (for ChatGPT / analysts)

## 18. What is being valued (asset bundle)

1. **BIM Boardroom** — full desktop application IP (UI, workflows, permissions, shop OS, local API, Electron packaging).
2. **SSv3 Exports add-in** — Revit packaging/plotting/export IP and Boardroom integration layer (fork of V2 spooling lineage).
3. **Integration schema** — `bim-boardroom-package-v1` + watched-folder + Ready-for-Fab promote gate (defensible workflow IP).
4. **Domain process encoding** — status pipelines for PM, detailing, deliverables, spooling, fab, shipping, field; Fab Queued/Warehouse/personal modes; weld log / BOM / clocks.

## 19. Differentiation thesis

| Vs | Differentiation |
|----|-----------------|
| Generic PM (Asana, Jira, Monday) | MEP-specific boards, fab shop modes, Revit package ingest, weld log, BOM warehouse pull |
| Pure Revit add-ins | Doesn’t stop at PDF/CSV dump — continues into role-aware shop queue and time |
| ERP / shop systems | Lightweight local shop OS tied to VDC boards, not full ERP inventory/accounting |
| Cloud BIM platforms | Local-first Electron + Revit loopback; faster for single-shop ops, weaker for multi-site SaaS |

## 20. Monetization angles (hypothetical — not current pricing)

- Per-seat Boardroom license (office + shop seats)
- Per-Revit-seat SSv3 Exports license
- Bundled “Boardroom + SSv3” shop package
- Optional future: multi-machine sync / cloud tenancy / Fab→Shipping automation as upsell

## 21. Maturity signals (honest)

**Strengths**

- Deep status matrices and role/permission systems documented in Order of Operations
- Working SSv3 ↔ Boardroom contract (API + JSON schema + promote/lock)
- Shop UI with BOM, weld-log edit-back, clocks, photos/comments
- Electron packaging path; demo portfolio for sales demos

**Gaps / risks for valuation**

- Local persistence — not enterprise multi-user cloud with conflict resolution
- Fab **Ready to Ship** does not auto-hand off to Shipping
- Shipping / Field dashboards are lighter than Fab workstation depth
- SSv3 TigerStop/PCF may be less mature than core plot/export path
- Single-machine / loopback API model limits multi-office deployment without further productization
- README marketing copy lags behind actual feature depth (product is richer than top-level README suggests)

## 22. Suggested valuation questions to ask ChatGPT

Use this brief and ask for:

1. **Comparable products** in MEP BIM fab / VDC shop software and how this bundle compares.
2. **Build vs buy** replacement cost (Revit add-in + Electron shop OS + integration).
3. **IP value** of the vertical workflow (Ready for Fab gate, package schema, Queued/Warehouse/Personal modes).
4. **Revenue potential** under seat-based licensing for a mid-size MEP fab shop (N Revit seats + N shop tablets).
5. **Risk discount** for local-only architecture vs cloud competitors.
6. **Roadmap value uplift** if Fab→Shipping automation and multi-machine sync were productized.
7. **Strategic buyer types** (MEP contractors, fab software vendors, BIM platform acquirers, Revit add-in companies).

## 23. Tech stack summary (Boardroom)

| Layer | Choice |
|-------|--------|
| Desktop | Electron ~36, electron-builder (Windows NSIS) |
| UI | React 19, TypeScript 5.8, Vite 6 |
| State | Zustand 5 + persist |
| DnD | @dnd-kit |
| PDF | pdfjs-dist, pdf-lib, jspdf + autotable |
| Spreadsheets | xlsx (weld logs) |
| Storage | localStorage + IndexedDB |
| Integration | Node http loopback API; Electron IPC; fs.watch |

## 24. End-to-end value chain (one diagram in words)

**Org & roles → Client/Project boards → Detail/Deliverables/Spooling → SSv3 (Revit) package & plot → Export via API+folder → Boardroom ingest → Ready for Fab → Queued → Warehouse pull → Dept fab/QA/weld log → Ready to Ship (manual Shipping) → Field install — with Time clocks, Reports, Activity audit, and Access Control throughout.**

---

*Document generated for valuation / diligence use from product source and `ORDER_OF_OPERATIONS.md`. Treat feature lists as current product capability inventory; treat pricing and market comps as analysis inputs, not company claims.*
