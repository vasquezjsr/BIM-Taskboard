# BIM-Taskboard

A desktop task management app built for BIM teams — organize work by client, project, and board type, then assign tasks to Detailers and Support Specialists.

## Features

- **Client → Project → Board hierarchy**
  - Main Overview, Detailers, Deliverables, RFI, Documents & Shop Drawings
- **Status columns**: Not Ready, Ready, In Progress, On Hold, Complete
- **Employee board**: drag tasks to team members; tasks auto-sort by priority and due date
- **Dark mode** with organized pastel color levels:
  - Level 1 (Lavender): main tabs — Clients / Employees
  - Level 2 (Mint): client tabs
  - Level 3 (Peach): project tabs
  - Level 4 (Sky): board type tabs
  - Rose: employee board
- **Local persistence** — data saved automatically in browser storage

## Getting Started

```bash
npm install
npm run electron:dev
```

For web-only development:

```bash
npm run dev
```

## Build Desktop App

```bash
npm run electron:build
```

The installer will be in the `release/` folder.

## Docs

- **[Order of Operations](./ORDER_OF_OPERATIONS.md)** — end-to-end Boardroom → SSv3 → Fab shop sequence

## Usage

1. **Clients tab** — select a client, then a project, then a board type. Create tasks with "+ New Task" and drag them between status columns.
2. **Employees tab** — drag tasks from Unassigned onto a team member. Tasks order by due date and priority (drag within a column to reprioritize).
3. Add clients, projects, and employees with the "+ Client", "+ Project", and "+ Employee" buttons.
