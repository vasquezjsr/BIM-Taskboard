# BIM Boardroom — Complete Instructions

**Audience:** Operators, shop leads, field supers, PMs, BIM managers, and owners documenting how the product works today.

**How to use this notebook:** Each subsection has lined note space. Each chapter ends with summary notes plus blank pages for holes, ideas, and redesigns. Mark gaps freely — this is a living reference of what exists now.

**Generated product snapshot:** BIM Boardroom desktop app (Electron) with optional browser-only Vite development. Data is local Zustand persistence unless you wire external sync.

---

## Part A — Product at a glance

### What BIM Boardroom is
BIM Boardroom is a project-operations workspace for mechanical/BIM shops that need one place for:

- Client and project portfolio management
- Spreadsheet-style task boards across the BIM lifecycle
- Spooling Savant Version 3 (SSv3) package imports from Revit
- Shop (Fab), Shipping, and Field dashboards tied to package handoffs
- Weld log tap-fill for shop and field welds
- Time tracking against projects and packages
- Employees, org chart, job titles, and Access Control permissions

### What it is not (today)
- Not a multi-user cloud SaaS with live conflict resolution
- Not a replacement for Revit or Spooling Savant itself (Boardroom consumes SSv3 exports)
- Not a full ERP / payroll system
- Clients/Task Board spreadsheet path is primary; a Kanban component exists but is not the main Clients UX

### Primary runtimes
| Mode | Command / artifact | Capabilities |
|------|--------------------|--------------|
| Electron (preferred) | `npm run electron:dev` or installed app in `release/` | Local files, SSv3 watch/import, PDF/xlsx preview, menus, Boardroom API |
| Browser Vite (dev) | `npm run dev` | UI exploration; limited file/SSv3 behaviors |

### Brand / shell chrome
- Header mark ◈ **BIM Boardroom**
- Clicking the brand returns to **Clients → Main Dashboard**
- Global actions: **Reports**, **View as…**, signed-in user badge, **Sign out**

### Notes — product definition holes
Use this space for vision, non-goals, and competitors.

---

## Part B — Getting started

### Login
- Screen: name or email + password
- Ambiguous names require email
- Temporary invite passwords can be issued from Employees (manager invite flow)
- Development may auto-establish a session after store hydrate

### First-load / storage
- Persist key: `bim-task-board-storage` (Zustand)
- Splash: “Loading BIM Boardroom…”
- Long hydrate shows a timeout-oriented hint (around 12s)
- Dev dual-port store sync may exist via `/__dev/store-sync`
- Undo history is session-oriented (separate from full disk persist)

### View As
- Preview another employee’s navigation and permission perspective
- Grouped workforce perspectives
- Preview badge while active; exit restores original user
- Activity Log viewing can follow View As; restore actions stay with the real signed-in user

### Electron menus (high level)
- File / Edit / View standard items
- Organization menu (when permitted): Open Org Chart — **Ctrl/Cmd+Shift+P**
- Permissions menu visibility syncs from org-chart access
- DevTools / reload / zoom / fullscreen available under View

### Keyboard shortcuts
| Shortcut | Action |
|----------|--------|
| F5 | Refresh active view |
| Ctrl/Cmd+Z | Undo |
| Ctrl/Cmd+Shift+Z or Ctrl/Cmd+Y | Redo |
| Ctrl+F | Find & Replace (spreadsheet context) |
| Ctrl+C / Ctrl+V | Copy / paste tasks (spreadsheet) |
| Escape | Close modals |

### Notes — onboarding / auth holes

---

## Part C — Main navigation map

### Always-visible tabs (typical)
- **Clients** — portfolio + all project boards
- **Task Board** — employee-centric work lists
- **Employees** — roster / titles / dashboard roles (edit needs manage-org)

### Permission-gated tabs
| Tab | Permission | Purpose |
|-----|------------|---------|
| Owner Dashboard | `view-owner-dashboard` | Portfolio KPIs + lifecycle |
| PM Dashboard | `view-pm-dashboard` | PM project cards + PM tasks |
| Shop Dashboard | `view-fab-dashboard` | Queue / Warehouse / Fabrication |
| Shipping Dashboard | `view-shipping-dashboard` | Logistics → Field handoff |
| Field Dashboard | `view-field-dashboard` | Install + optional Field Welds |
| Weld Log Dashboard | `view-weld-log-dashboard` | Cross-package weld fill |
| Time Tracking | `view-time-tracking` | Hours / calendar / clocks |
| Organizational Chart | `view-org-chart` / manage-org | Reporting tree |
| Activity Log | grant / Owner / BIM / Ops mgr | Audit + restores |
| Access Control | visibility dashboard grant | Permission matrix |

### Lifecycle ordering after office work
**Shop → Shipping → Field** is the fabrication-through-install chain in the main nav.

### Notes — nav IA / naming holes

---

## Part D — Clients, projects, and boards

### Clients Main Dashboard
- Clients as sections; projects as cards
- Progress percentages, budget cues, open into project boards
- Add client / add project
- Template client / template project patterns exist (template projects can seed new jobs)
- Some dashboards hide the template client name

### Board mode layout
Strip hierarchy: **Clients → Projects → Board tabs → Spreadsheet**

### Built-in board types
| Board | Purpose |
|-------|---------|
| Main Overview | Cross-cutting sections mirroring key boards |
| Project Management | Contract / kickoff / BEP / clash / IFC workflow |
| RFI | Request for information (two-status list) |
| Documents | Request → link → verify |
| Detailers | Modeling toward coordination |
| Deliverables | Support pre-planning → spool → Ready for Fab path |
| Spooling | Spool QA and Ready for Fab (+ SSv3 package roots) |
| Fabrication | Shop statuses (also Shop Dashboard) |
| Shipping | Logistics lanes |
| Field | Install lanes |

### Custom boards
- User-created boards (`cb-*`) with sort order
- Drag tasks onto board tabs to change board type

### Project Settings (typical fields)
- Name, job code
- Detailers / support / PMs assignment pools
- Billing: lump-sum / T&M
- Budget hours vs spent hours
- Revit year, model type (cloud/local)
- Levels, schedule dates

### Task spreadsheet — primary board UX
- Hierarchical **groups**: section / parent / child; ungrouped buckets
- Columns: task #, title, status, assignees, due / workflow dates, custom columns
- Custom column types: text, date, duration, dropdown
- Create / nest / promote / drag reorder / multi-select
- Attachments and comments per task
- Assignee lock; status-driven auto-assign
- Settings: Status Settings, Column Settings, Find & Replace
- Task numbers often follow jobCode-#### patterns
- Clipboard copy/paste is session-scoped

### Main Overview sections
Default Main sections tend to mirror Project Management, RFI, Documents, Detailers, Deliverables, Spooling. Fab / Shipping / Field appear as tabs without always getting default Main Overview sections.

### Notes — Clients / projects / board holes

---

## Part E — Status systems (complete defaults)

### Status model
Each board has a status list (optional per-project overrides) with:

- id, label, color
- `countsAsComplete` (group progress)
- optional `autoAssignTeam` (detailers / support)
- optional `autoAssignEmployeeId`

Managed in **Status Settings** from the spreadsheet. RFI status IDs are structurally locked (labels/colors editable).

### Generic / Main defaults
Not Started → Not Ready → Ready → In Progress → On Hold → Complete

### Project Management defaults
Not Started → Contract Review → Kickoff Complete → BEP Approved → Model Setup → Clash Cycle Active → Clashes Resolved → IFC Ready → On Hold → Complete

### Detailers defaults
Not Started → Backgrounds Linked → Modeling (LOD 300) → Hangers & Supports → Detailer QA → Ready for Coordination → Rework → On Hold → Complete

### Documents defaults
Not Started → Requested → Received → Linked to Model → Verified → On Hold → Complete

### Deliverables defaults
Not Started → Ready for Pre-Planning → Pre-Planning Complete → Support In Progress → Ready for Spooling → Spool In Progress → Spool QA Review → Spool Approved → **Ready for Fab** → On Hold → Detailer Review → Fix Mark Ups → Complete

### Spooling defaults
Not Started → Ready for Spooling → Spool In Progress → Spool QA Review → Spool Approved → **Ready for Fab** → On Hold → Complete

### RFI defaults
Waiting for Response → Complete

### Fabrication defaults
Not Started → **Spooling** (return path) → Queued → Pulling Material → Material Pulled → **In Progress** → In Fab (Mech) → In Fab (Plmb) → In Fab (HVAC) → QA Review → Rework → **Ready for Shipping** → Complete

### Shipping defaults
Not Started → Staging → Loading → In Transit → Delivered to Site → **Received by Field** → Complete

### Field defaults
Not Started → Mobilization → Material On Site → Rough-In → Hydro / Test → Trim-Out → Punch List → As-Built Update → Final Inspection → Complete

### Critical automatic handoff statuses
| Status | Where set | What happens |
|--------|-----------|--------------|
| Ready for Fab | Spooling package with SSv3 | Tree promotes to Fab (Queued) |
| Spooling | Fab package | Tree demotes back to Spooling (Spool In Progress); export editable again |
| Ready for Shipping | Fab package | Tree promotes to Shipping (Staging) |
| Received by Field | Shipping package | Tree promotes to Field (Material On Site) |

### Notes — status / workflow holes

---

## Part F — Tasks, groups, assignees, attachments, comments

### Tasks
Core fields: title, description, status, assignees[], group, parent task, priority, due date, custom fields, duration fields, assigneesLocked, taskNumber, boardType, project/client links.

### Groups
- Tiers: section / parent / child
- Progress bars when statuses count as complete
- Main Overview sections can map to `sectionBoardType`

### Assignees
- Multi-assignee chips
- Task Board drag-assign
- Status auto-assign to detailers/support or a specific employee
- Assignees can be locked against auto-assign churn

### Attachments
- Versioned files; upload / remove; open / download
- SSv3 export files can appear as boardroom-absolute paths
- Clear SSv3 export is Spooling-only and blocked when Ready for Fab / already Fab / Shipping / Field locked

### Comments
- Per-task thread
- Package-level Photos + Comments via Package Collab Bar on Shop / Shipping / Field

### Notes — collaboration / data model holes

---

## Part G — Spooling Savant Version 3 integration

### Purpose
Revit Workers (SSv3 Exports) write a package folder; Boardroom watches and attaches packages/assemblies/files to a Spooling task.

### Typical folder
`Spooling Savant Version 3 (Exports)/Boardroom/Exports`

### Manifest
- Schema: `bim-boardroom-package-v1`
- File: `boardroom-package.json`
- Binds Boardroom project id + Spooling task id
- Lists packages, assemblies (Revit element ids, sheet names/numbers, QR), and export files

### Always-on watcher
`useBoardroomExportWatcher` imports when export folders change (Electron). Browser mode cannot fully watch/import the same way.

### What gets created / updated
- Package root task(s) with custom fields (package name, export folder, files JSON, kind=package)
- Assembly child tasks (kind=assembly, sheet metadata, revitElementId)
- File refs: Assembly List, BOM, Cut List, PCF, Spools Combined PDF, Weld Log xlsx, etc.

### Export lock
While Ready for Fab **or** boardType is fab / shipping / field:

- Cannot wipe / replace / clear the SSv3 export until demoted off Fab via Spooling status

### Boardroom API (Electron)
Local publish / snapshot support for SSv3 side tooling may be available via preload IPC (default exports dir, read package, open path, file preview, watch/unwatch, publish snapshot).

### Notes — SSv3 / Revit bridge holes

---

## Part H — Handoff chain (end-to-end)

### Happy path diagram

```
Spooling (+ SSv3 export)
  -- Ready for Fab --> Fab Queued
  -- Shop Queue / Warehouse / Fabrication work -->
  -- Ready for Shipping --> Shipping Staging
  -- Staging → Loading → In Transit → Delivered -->
  -- Received by Field --> Field Material On Site
  -- Install lanes → Complete
```

### Rework path
Fab package status **Spooling** → entire tree returns to Spooling as Spool In Progress → export unlocked for replace.

### Assembly-level parallel tracks
| Track | Storage | Used on |
|-------|---------|---------|
| Fab status | task.status | Shop fabrication |
| Shipping lane | `ssv3ShippingStatus` custom field | Shipping (per assembly) |
| Field install | `ssv3FieldStatus` custom field | Field (per assembly) |

### Notes — handoff / gap / exception cases

---

## Part I — Shop Dashboard (Fab Workstation)

### Purpose
Day-to-day shop UI for SSv3 packages — not the Fabrication spreadsheet board.

### Sub-dashboards
1. **Queued Dashboard** — packages waiting (not-started / queued / pulling-material / material-pulled)
2. **Warehouse Dashboard** — material pull focus; BOM PDF emphasis; status options include Material Pulled
3. **Fabrication Dashboard** — personal/workstation mode for dept leads and workers

### Shop roles (Employees dashboard assignments)
Shop Super, Warehouse Lead, Warehouse Worker, Dept Manager (Mech / Plmb / HVAC), Workers

### Permissions especially relevant
`assign-fab-leads`, `assign-fab-workers`, `edit-fab-status`, `fab-clock`, `edit-weld-log`, `edit-fab-collab`

### UI anatomy
- Project filter (shop-local; independent of Clients selected project)
- Package list → package detail → viewer pane
- Assign Dept Lead on package; Package owner / per-assembly worker
- Package status + assembly status
- Photos / Comments collab bar
- Clock in / Clock out into Time Tracking
- Weld log grid when opening weld workbook / assemblies
- Resizable panes (localStorage `bim-fab-pane-widths-v1`)

### Package progress
Shows assemblies completed (Complete / countsAsComplete) out of total.

### In Progress package rule
If **any** assembly is In Fab (Mech/Plmb/HVAC), package displays/syncs toward **In Progress**.

### Ready for Shipping
Setting package to Ready for Shipping promotes the tree to Shipping and removes it from Fab lists.

### Spooling return
Setting package to Spooling demotes the tree back to the Spooling board.

### Notes — shop ops holes

---

## Part J — Shipping Dashboard

### Purpose
Stage, load, move, deliver packages; hand off to Field; optionally ship individual assemblies.

### Lane filters
Active / Staging / Loading / In Transit / Delivered / Received by Field / Complete / All

### Package actions
- Status dropdown
- Advance package to next lane
- Photos / Comments

### Assembly actions
- Independent shipping status per assembly
- **Ship → next stage** on one assembly
- Multi-select: Set status… / Advance selected
- Fab Complete badge preserved separately from shipping lane

### Progress
Shows assemblies shipped (left Staging) and fab complete counts.

### Received by Field
Promotes package tree to Field (Material On Site) and removes it from Shipping.

### What Shipping does not emphasize
Export file browsers are intentionally de-emphasized here (install/stage work, not fab export review).

### Notes — shipping / logistics holes

---

## Part K — Field Dashboard

### Purpose
Install packages received from Shipping. **Install first** — Field Welds are optional and minority work.

### Lane filters
Active / Material On Site / Rough-In / Hydro–Test / Trim-Out / Punch List / As-Built Update / Final Inspection / Complete / All

### Layout
1. Packages (status + assemblies installed progress)
2. Assemblies (sheet label + install status)
3. Spool sheet PDF (primary viewer)

### Install tracking
- Package advances through Field statuses
- Each assembly has `ssv3FieldStatus`
- Progress: assemblies at Final Inspection / Complete count as installed

### Spool sheets
Selecting an assembly opens Spools Combined page or matching sheet PDF from the export folder (Electron).

### Field Welds (optional)
- Toggle **Field Welds (N)** only appears when that assembly has Field Weld rows
- Tap-fill Date / Welder / Initials for field rows
- Full Weld Log Dashboard remains available for shop-wide weld work

### Entry condition
Packages appear after Shipping status **Received by Field**.

### Notes — field install / punch / as-built holes

---

## Part L — Weld Log Dashboard

### Purpose
Cross-client / cross-project fill of Weld Log.xlsx for SSv3 packages (Fab, Shipping, or Field tracked packages).

### UI pattern
Client tabs → Project tabs → Package list → Assembly list → weld grid (Client board styling; no + Client / + Project; excludes Client Template)

### Permissions
- View: `view-weld-log-dashboard`
- Edit all weld types: `edit-weld-log`
- Field-oriented users without edit may still tap-fill Field Weld rows when they have view access

### Data source
Excel workbook remains source of truth; Boardroom read/write via Electron export folder path. Field Weld detection uses weld type / numbering (not merely Family Name shop vs field).

### Notes — weld QA / WPS / stamps holes

---

## Part M — Time Tracking

### Purpose
Log hours against client / project / task; calendar; absorb Fab clock-ins.

### UI
- Employee selector (self + org-chart downstream visibility rules)
- Date, start/end (snaps), note, optional task link (includes SSv3 packages / quick tasks)
- Calendar day / week / month
- Open clock entries from Shop clock in/out

### Permissions
`view-time-tracking`, `log-time`, `delete-time`

### Budgets interaction
Project budget hours and spent hours appear on Owner / PM / portfolio surfaces and budget reports.

### Notes — time / labor / cost holes

---

## Part N — Task Board (employee work)

### Purpose
Personal / team kanban of assigned tasks outside the Clients spreadsheet.

### Behaviors
- Tasks filtered/grouped by employee
- Drag assign / status changes
- Task Board Settings can hide statuses (defaults often hide Not Started / Not Ready)

### Notes — Task Board holes

---

## Part O — Employees, job titles, dashboard roles

### Employees stages
Leadership, Detailers, Support (office); Project Management, Field, Fab Shop, Shipping (operations)

### Employee cards
- Job title
- Stage placement
- Remove (restore via Activity Log if permitted)
- Invite / temp password flows
- Optional welderId for weld stamps
- Roles: detailer / support-specialist / operations (+ org categories)

### Job Titles modal
- Create / rename / delete titles
- Map title → Employees stage
- Wider layout with column headers for title / stage / people

### Dashboard role assignments
Configured on Employees ops stages:

| Dashboard | Roles |
|-----------|-------|
| PM | Project Manager, Assistant PM |
| Field | Site Superintendent, Foreman, Crew Lead |
| Fab | Shop Super, Warehouse Lead/Worker, Dept Managers, Workers |
| Shipping | Shipping Manager, Worker |

These drive who appears in workstation queues and assignment dropdowns.

### Notes — HR / roster / staging holes

---

## Part P — Organizational Chart

### Purpose
Reporting tree for time visibility, View As perspectives, and management rights.

### Behaviors
- Interactive pan/zoom tree
- Drag reparent when `manage-org`
- Menu shortcut Ctrl/Cmd+Shift+P
- Upstream managers may gain Warehouse/Shop visibility via org relationships

### Org categories (examples)
Owner, BIM Manager, Operations Manager, trade leads, junior detailers, support specialists, etc.

### Notes — org design holes

---

## Part Q — Access Control (Visibility Dashboard)

### Purpose
Job-level matrix of who can open which tabs and hold which capability chips.

### Capability chips (examples)
Edit budget hours; Manage org & permissions; Manage & restore columns; Edit PM assigns; Assign fab leads/workers; Edit fab status; Fab clock; Edit weld log; Fab package notes; Log/Delete time; Edit clients & projects; Edit tasks; Assign tasks; Manage statuses; Add columns

### Navigation visibility chips
Owner / PM / Shop / Shipping / Field / Weld Log / Time Tracking / Org Chart / Activity Log / Access Control

### Who typically opens Access Control
Owner, BIM Manager, Operations Manager categories (or explicit grant)

### Notes — permission model holes

---

## Part R — Owner Dashboard

### Purpose
Portfolio health for owners.

### Typical content
- Active projects, average progress
- Budget vs spent
- At-risk signals
- BIM lifecycle rollup: Project Setup → Detailing → Coordination → Support → Spooling → Fab → Shipping → Field
- Jump into Main Overview for a project

### Notes — owner KPIs holes

---

## Part S — PM Dashboard

### Purpose
Project Managers’ working surface.

### Typical content
- Project cards for assigned PMs (or broader if manage-org)
- Budget / schedule / lifecycle %
- Assign PM (`edit-pm-assigns`)
- Open Project Management board
- List PM-board tasks

### Notes — PM workflow holes

---

## Part T — Activity Log

### Purpose
Audit trail and recoverability.

### Filters
Created / Updated / Deleted / Restored / Status changes + search

### Restores
- Deleted columns (`manage-columns`)
- Deleted employees (`manage-org`)

### Notes — audit / compliance holes

---

## Part U — Reports

### Entry
Global **Reports** button → Reports dialog

### Scope controls
Current-tab relevance or all; multi-select reports; project picker; period when needed; generate/download PDFs; CSV helpers for Boardroom projects.

### Time Tracking reports
Weekly by project; Weekly summary; Weekly by employee; Monthly summary; Daily detail

### Clients & Projects reports
Portfolio; Budget & hours status

### Task Board reports
Status summary; By assignee; Open list; Project progress

### Organization reports
Team roster; Reporting structure

### Notes — reporting / BI holes

---

## Part V — Settings surfaces index

| Setting | Where | What |
|---------|-------|------|
| Project Settings | Clients → project | Job identity, teams, billing, budget, Revit/model, levels, dates |
| Status Settings | Spreadsheet | Board / project status libraries |
| Column Settings | Spreadsheet | Custom columns, order, align, lock widths |
| Find & Replace | Spreadsheet | Text/value replace |
| Task Board Settings | Task Board | Visible statuses for employee board |
| Job Titles | Employees | Catalog + stage mapping |
| Dashboard roles | Employees ops stages | PM / Field / Fab / Shipping membership |
| Access Control | Nav | Permissions + tab visibility |
| Org Chart | Nav | Reporting lines |

### Local UI prefs (examples)
- Fab pane widths
- Spreadsheet column widths / locks
- Various last-view filters

### Notes — settings IA holes

---

## Part W — Photos and package collaboration

### Package Collab Bar
On Shop, Shipping, and Field package headers:

- **Photos** — image attachments on the package task
- **Comments** — package task comment thread

Edit gated largely by `edit-fab-collab` (read-only otherwise).

### Notes — field photos / QA documentation holes

---

## Part X — Permission quick reference

### Navigation grants
`view-owner-dashboard`, `view-pm-dashboard`, `view-fab-dashboard`, `view-shipping-dashboard`, `view-field-dashboard`, `view-weld-log-dashboard`, `view-time-tracking`, `view-activity-log`, `view-org-chart`, `view-visibility-dashboard`

### Shop / weld ops
`assign-fab-leads`, `assign-fab-workers`, `edit-fab-status`, `fab-clock`, `edit-weld-log`, `edit-fab-collab`

### Office ops
`edit-budget-hours`, `manage-org`, `manage-columns`, `edit-pm-assigns`, `edit-clients-projects`, `edit-tasks`, `assign-tasks`, `manage-statuses`, `add-columns`, `log-time`, `delete-time`

### Default tendencies
- Owners receive broad dashboard visibility
- BIM Managers receive many edit grants
- Detailers often receive budget hours by legacy rules
- Column admins tend to Owner / BIM / Ops Manager

### Notes — security / least-privilege holes

---

## Part Y — Data, sync, and desktop IPC notes

### Persistence
- Single-user local store today
- Session undo stack ≠ full durable history of every action

### Electron capabilities used by workstations
- Read file preview (PDF / xlsx)
- Write file bytes (weld log save)
- Watch Boardroom export folders
- Open filesystem paths
- Optional Boardroom API publish for SSv3

### Dev notes
- Concurrent Vite ports for multi-window store sync experiments
- Demo / seed scripts under `scripts/` for projects CSV and demo injection

### Notes — multiplayer / backup / IT holes

---

## Part Z — Glossary

| Term | Meaning |
|------|---------|
| SSv3 | Spooling Savant Version 3 (Revit Workers + Exports) |
| Package | SSv3 package root task (e.g., TP007) |
| Assembly | Child of package mapped to a spool/element |
| Ready for Fab | Spooling status that promotes to Shop |
| Ready for Shipping | Fab status that promotes to Shipping |
| Received by Field | Shipping status that promotes to Field |
| Spools Combined | Multi-page PDF of assembly sheets |
| Field Weld | Weld log row typed/numbered as field work |
| View As | Temporary permission perspective preview |
| Access Control | Visibility / capability matrix UI |

### Notes — glossary additions

---

## Appendix — Blank capture pages preface

The generated notebook continues with extra blank pages after each part for handwriting. Use them for:

- Missing statuses or lanes
- Training scripts
- Role RASCI charts
- Screenshot callouts
- Items that should move between Shop / Shipping / Field
- Questions for BIM + Ops leadership

### Notes — appendix planning
