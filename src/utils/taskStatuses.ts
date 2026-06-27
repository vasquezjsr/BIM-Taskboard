import type { ProjectBoardType, Task, TaskGroup, TaskStatusDefinition } from '../types';
import type { BuiltInProjectBoardType } from '../types';
import { findSectionBoardType } from './sheetDrag';

export type BoardTaskStatusesMap = Partial<Record<ProjectBoardType, TaskStatusDefinition[]>>;

/** Per-project overrides for board status lists (e.g. job-specific Deliverables workflow) */
export type ProjectBoardTaskStatusesMap = Record<string, Partial<BoardTaskStatusesMap>>;

export const BUILT_IN_BOARD_TYPES: BuiltInProjectBoardType[] = [
  'main',
  'detailers',
  'deliverables',
  'spooling',
  'rfi',
  'documents',
  'project-managers',
  'field',
  'fab',
  'shipping',
];

export const DEFAULT_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8' },
  { id: 'not-ready', label: 'Not Ready', color: '#fca5a5' },
  { id: 'ready', label: 'Ready', color: '#fde68a' },
  { id: 'in-progress', label: 'In Progress', color: '#93c5fd' },
  { id: 'on-hold', label: 'On Hold', color: '#d8b4fe' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true },
];

/** Project Setup & coordination workflow (Project Management board) */
export const DEFAULT_PM_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8' },
  { id: 'contract-review', label: 'Contract Review', color: '#cbd5e1' },
  { id: 'kickoff-complete', label: 'Kickoff Complete', color: '#fde68a' },
  { id: 'bep-approved', label: 'BEP Approved', color: '#93c5fd' },
  { id: 'model-setup', label: 'Model Setup', color: '#7dd3fc' },
  { id: 'clash-cycle-active', label: 'Clash Cycle Active', color: '#fdba74' },
  { id: 'clashes-resolved', label: 'Clashes Resolved', color: '#6ee7b7' },
  { id: 'ifc-ready', label: 'IFC Ready', color: '#a5b4fc' },
  { id: 'on-hold', label: 'On Hold', color: '#d8b4fe' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true },
];

/** Detailing / modeling workflow */
export const DEFAULT_DETAILERS_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8' },
  { id: 'backgrounds-linked', label: 'Backgrounds Linked', color: '#cbd5e1' },
  { id: 'modeling', label: 'Modeling (LOD 300)', color: '#93c5fd' },
  { id: 'hangers-supports', label: 'Hangers & Supports', color: '#7dd3fc' },
  { id: 'detailer-qa', label: 'Detailer QA', color: '#fde68a' },
  { id: 'ready-for-coordination', label: 'Ready for Coordination', color: '#6ee7b7' },
  { id: 'rework', label: 'Rework', color: '#fca5a5' },
  { id: 'on-hold', label: 'On Hold', color: '#d8b4fe' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true },
];

/** Background & reference document workflow */
export const DEFAULT_DOCUMENTS_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8' },
  { id: 'requested', label: 'Requested', color: '#cbd5e1' },
  { id: 'received', label: 'Received', color: '#fde68a' },
  { id: 'linked', label: 'Linked to Model', color: '#93c5fd' },
  { id: 'verified', label: 'Verified', color: '#6ee7b7' },
  { id: 'on-hold', label: 'On Hold', color: '#d8b4fe' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true },
];

/** Support Specialists + Spooling workflow (Deliverables board) */
export const DEFAULT_DELIVERABLES_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8', autoAssignTeam: 'detailers' },
  {
    id: 'ready-for-pre-planning',
    label: 'Ready for Pre-Planning',
    color: '#fde68a',
    autoAssignTeam: 'support',
  },
  {
    id: 'pre-planning-complete',
    label: 'Pre-Planning Complete',
    color: '#93c5fd',
    autoAssignTeam: 'support',
  },
  {
    id: 'support-in-progress',
    label: 'Support In Progress',
    color: '#f9a8d4',
    autoAssignTeam: 'support',
  },
  {
    id: 'ready-for-spooling',
    label: 'Ready for Spooling',
    color: '#fdba74',
    autoAssignTeam: 'support',
  },
  {
    id: 'spool-in-progress',
    label: 'Spool In Progress',
    color: '#93c5fd',
    autoAssignTeam: 'support',
  },
  {
    id: 'spool-qa-review',
    label: 'Spool QA Review',
    color: '#a5b4fc',
    autoAssignTeam: 'support',
  },
  {
    id: 'spool-approved',
    label: 'Spool Approved',
    color: '#6ee7b7',
    autoAssignTeam: 'support',
  },
  {
    id: 'ready-for-fab',
    label: 'Ready for Fab',
    color: '#fcd34d',
    autoAssignTeam: 'detailers',
  },
  { id: 'on-hold', label: 'On Hold', color: '#d8b4fe', autoAssignTeam: 'detailers' },
  { id: 'detailer-review', label: 'Detailer Review', color: '#fdba74', autoAssignTeam: 'detailers' },
  { id: 'fix-mark-ups', label: 'Fix Mark Ups', color: '#fca5a5', autoAssignTeam: 'support' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true, autoAssignTeam: 'detailers' },
];

/** Support / spooler workflow (Spooling board) */
export const DEFAULT_SPOOLING_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8', autoAssignTeam: 'support' },
  {
    id: 'ready-for-spooling',
    label: 'Ready for Spooling',
    color: '#fdba74',
    autoAssignTeam: 'support',
  },
  {
    id: 'spool-in-progress',
    label: 'Spool In Progress',
    color: '#93c5fd',
    autoAssignTeam: 'support',
  },
  {
    id: 'spool-qa-review',
    label: 'Spool QA Review',
    color: '#a5b4fc',
    autoAssignTeam: 'support',
  },
  {
    id: 'spool-approved',
    label: 'Spool Approved',
    color: '#6ee7b7',
    autoAssignTeam: 'support',
  },
  {
    id: 'ready-for-fab',
    label: 'Ready for Fab',
    color: '#fcd34d',
    autoAssignTeam: 'detailers',
  },
  { id: 'on-hold', label: 'On Hold', color: '#d8b4fe', autoAssignTeam: 'support' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true, autoAssignTeam: 'support' },
];

/** Status workflow for the RFI board only */
export const DEFAULT_RFI_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'waiting-for-response', label: 'Waiting for Response', color: '#93c5fd' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true },
];

/** Field installation workflow — supers, foremen, crew leads */
export const DEFAULT_FIELD_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8' },
  { id: 'mobilization', label: 'Mobilization', color: '#fde68a' },
  { id: 'material-on-site', label: 'Material On Site', color: '#cbd5e1' },
  { id: 'rough-in', label: 'Rough-In', color: '#93c5fd' },
  { id: 'hydro-test', label: 'Hydro / Test', color: '#7dd3fc' },
  { id: 'trim-out', label: 'Trim-Out', color: '#fdba74' },
  { id: 'punch-list', label: 'Punch List', color: '#f9a8d4' },
  { id: 'as-built-update', label: 'As-Built Update', color: '#a5b4fc' },
  { id: 'final-inspection', label: 'Final Inspection', color: '#6ee7b7' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true },
];

/** Fab shop production workflow */
export const DEFAULT_FAB_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8' },
  { id: 'queued', label: 'Queued', color: '#fde68a' },
  { id: 'material-pulled', label: 'Material Pulled', color: '#cbd5e1' },
  { id: 'in-fab-mech', label: 'In Fab (Mech)', color: '#93c5fd' },
  { id: 'in-fab-plmb', label: 'In Fab (Plmb)', color: '#7dd3fc' },
  { id: 'in-fab-hvac', label: 'In Fab (HVAC)', color: '#a5b4fc' },
  { id: 'qa-review', label: 'QA Review', color: '#f9a8d4' },
  { id: 'rework', label: 'Rework', color: '#fca5a5' },
  { id: 'ready-to-ship', label: 'Ready to Ship', color: '#fdba74' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true },
];

/** Shipping & logistics workflow */
export const DEFAULT_SHIPPING_TASK_STATUSES: TaskStatusDefinition[] = [
  { id: 'not-started', label: 'Not Started', color: '#94a3b8' },
  { id: 'staging', label: 'Staging', color: '#fde68a' },
  { id: 'loading', label: 'Loading', color: '#93c5fd' },
  { id: 'in-transit', label: 'In Transit', color: '#fdba74' },
  { id: 'delivered', label: 'Delivered to Site', color: '#6ee7b7' },
  { id: 'received-field', label: 'Received by Field', color: '#a5b4fc' },
  { id: 'complete', label: 'Complete', color: '#86efac', countsAsComplete: true },
];

const EXTRA_STATUS_COLORS = ['#f9a8d4', '#fdba74', '#a5b4fc', '#6ee7b7', '#fcd34d', '#7dd3fc'];

const LEGACY_COLORS: Record<string, string> = {
  'not-started': '#94a3b8',
  'not-ready': '#fca5a5',
  ready: '#fde68a',
  'in-progress': '#93c5fd',
  'on-hold': '#d8b4fe',
  complete: '#86efac',
  'waiting-for-response': '#93c5fd',
};

export function normalizeTaskStatuses(
  statuses?: TaskStatusDefinition[] | null
): TaskStatusDefinition[] {
  if (!statuses?.length) return [...DEFAULT_TASK_STATUSES];
  const seen = new Set<string>();
  const result: TaskStatusDefinition[] = [];
  for (const s of statuses) {
    if (!s.id || !s.label || seen.has(s.id)) continue;
    seen.add(s.id);
    result.push({
      id: s.id,
      label: s.label,
      color: s.color || LEGACY_COLORS[s.id] || '#94a3b8',
      countsAsComplete: s.countsAsComplete ?? s.id === 'complete',
      ...(s.autoAssignTeam !== undefined ? { autoAssignTeam: s.autoAssignTeam } : {}),
    });
  }
  return result.length ? result : [...DEFAULT_TASK_STATUSES];
}

export function getStatusColor(statusId: string, statuses: TaskStatusDefinition[]): string {
  return statuses.find((s) => s.id === statusId)?.color ?? LEGACY_COLORS[statusId] ?? '#94a3b8';
}

export function isCompleteStatus(statusId: string, statuses: TaskStatusDefinition[]): boolean {
  const def = statuses.find((s) => s.id === statusId);
  if (def) return Boolean(def.countsAsComplete);
  return statusId === 'complete';
}

export function pickNewStatusColor(existingCount: number): string {
  return EXTRA_STATUS_COLORS[existingCount % EXTRA_STATUS_COLORS.length];
}

export function statusLabel(statusId: string, statuses: TaskStatusDefinition[]): string {
  return statuses.find((s) => s.id === statusId)?.label ?? statusId;
}

/** Which board's status list applies to a task (explicit branch or group placement). */
export function statusBoardForTask(
  task: { boardType: Task['boardType']; groupId?: string | null },
  groups?: TaskGroup[]
): ProjectBoardType {
  if (task.boardType === 'employee') return 'main';
  if (task.boardType !== 'main') return task.boardType as ProjectBoardType;
  if (groups && task.groupId) {
    const branch = findSectionBoardType(groups, task.groupId);
    if (branch) return branch;
  }
  return 'main';
}

export function getBoardTaskStatuses(
  boardType: ProjectBoardType,
  boardTaskStatuses: BoardTaskStatusesMap,
  projectId?: string | null,
  projectBoardTaskStatuses?: ProjectBoardTaskStatusesMap
): TaskStatusDefinition[] {
  if (boardType === 'rfi') {
    if (projectId && projectBoardTaskStatuses?.[projectId]?.rfi?.length) {
      return normalizeRfiBoardTaskStatuses(projectBoardTaskStatuses[projectId]!.rfi);
    }
    return normalizeRfiBoardTaskStatuses(boardTaskStatuses.rfi);
  }

  if (projectId && projectBoardTaskStatuses?.[projectId]?.[boardType]?.length) {
    return normalizeTaskStatuses(projectBoardTaskStatuses[projectId]![boardType]);
  }
  const list = boardTaskStatuses[boardType];
  if (list?.length) return normalizeTaskStatuses(list);
  const main = boardTaskStatuses.main;
  if (main?.length) return normalizeTaskStatuses(main);
  return [...DEFAULT_TASK_STATUSES];
}

export function createDefaultBoardTaskStatuses(
  from?: TaskStatusDefinition[]
): BoardTaskStatusesMap {
  const base = normalizeTaskStatuses(from).map((s) => ({ ...s }));
  const map: BoardTaskStatusesMap = {};
  for (const board of BUILT_IN_BOARD_TYPES) {
    if (board === 'deliverables') map[board] = DEFAULT_DELIVERABLES_TASK_STATUSES.map((s) => ({ ...s }));
    else if (board === 'spooling') map[board] = DEFAULT_SPOOLING_TASK_STATUSES.map((s) => ({ ...s }));
    else if (board === 'rfi') map[board] = DEFAULT_RFI_TASK_STATUSES.map((s) => ({ ...s }));
    else if (board === 'field') map[board] = DEFAULT_FIELD_TASK_STATUSES.map((s) => ({ ...s }));
    else if (board === 'fab') map[board] = DEFAULT_FAB_TASK_STATUSES.map((s) => ({ ...s }));
    else if (board === 'shipping') map[board] = DEFAULT_SHIPPING_TASK_STATUSES.map((s) => ({ ...s }));
    else if (board === 'detailers') map[board] = DEFAULT_DETAILERS_TASK_STATUSES.map((s) => ({ ...s }));
    else if (board === 'project-managers') map[board] = DEFAULT_PM_TASK_STATUSES.map((s) => ({ ...s }));
    else if (board === 'documents') map[board] = DEFAULT_DOCUMENTS_TASK_STATUSES.map((s) => ({ ...s }));
    else map[board] = base.map((s) => ({ ...s }));
  }
  return map;
}

const RFI_TASK_STATUS_IDS = new Set(DEFAULT_RFI_TASK_STATUSES.map((status) => status.id));

/** RFI board always uses exactly Waiting for Response + Complete. */
export function isRfiBoardStatusListLocked(boardType: ProjectBoardType): boolean {
  return boardType === 'rfi';
}

export function normalizeRfiBoardTaskStatuses(
  statuses?: TaskStatusDefinition[] | null
): TaskStatusDefinition[] {
  const byId = new Map((statuses ?? []).map((status) => [status.id, status] as const));
  return DEFAULT_RFI_TASK_STATUSES.map((defaultStatus) => {
    const custom = byId.get(defaultStatus.id);
    if (!custom) return { ...defaultStatus };
    return {
      ...defaultStatus,
      label: custom.label?.trim() || defaultStatus.label,
      color: custom.color || defaultStatus.color,
      countsAsComplete: custom.countsAsComplete ?? defaultStatus.countsAsComplete,
    };
  });
}

export function normalizeProjectBoardTaskStatuses(
  raw: ProjectBoardTaskStatusesMap | undefined | null
): ProjectBoardTaskStatusesMap {
  if (!raw) return {};
  const result: ProjectBoardTaskStatusesMap = {};
  for (const [projectId, boards] of Object.entries(raw)) {
    if (!boards) continue;
    result[projectId] = {
      ...boards,
      ...(boards.rfi?.length ? { rfi: normalizeRfiBoardTaskStatuses(boards.rfi) } : {}),
    };
  }
  return result;
}

/** Map legacy / invalid RFI task statuses onto the RFI workflow. */
export function migrateRfiTaskStatus(status: string): string {
  if (RFI_TASK_STATUS_IDS.has(status)) return status;
  return 'waiting-for-response';
}

export function migrateRfiBoardTaskStatuses(tasks: Task[]): Task[] {
  return tasks.map((task) => {
    if (task.boardType !== 'rfi') return task;
    const nextStatus = migrateRfiTaskStatus(task.status);
    return nextStatus === task.status ? task : { ...task, status: nextStatus };
  });
}

/** Remap task statuses onto the current board workflow after status set changes. */
export function migrateTasksToBoardStatuses(
  tasks: Task[],
  boardTaskStatuses: BoardTaskStatusesMap,
  taskGroups?: TaskGroup[],
  projectBoardTaskStatuses?: ProjectBoardTaskStatusesMap
): Task[] {
  return tasks.map((task) => {
    const boardType = statusBoardForTask(task, taskGroups);
    const statuses = getBoardTaskStatuses(
      boardType,
      boardTaskStatuses,
      task.projectId,
      projectBoardTaskStatuses
    );
    const validIds = new Set(statuses.map((status) => status.id));
    if (validIds.has(task.status)) return task;
    if (task.status === 'complete' && validIds.has('complete')) return task;
    const fallback =
      statuses.find((status) => status.id === 'not-started')?.id ??
      statuses.find((status) => status.id === 'waiting-for-response')?.id ??
      statuses[0]?.id ??
      'not-started';
    return { ...task, status: fallback };
  });
}

export function normalizeBoardTaskStatuses(
  raw: BoardTaskStatusesMap | undefined | null,
  fallbackGlobal?: TaskStatusDefinition[]
): BoardTaskStatusesMap {
  const fallback = normalizeTaskStatuses(fallbackGlobal);
  const result = createDefaultBoardTaskStatuses(fallback);
  if (!raw) return result;
  for (const [key, list] of Object.entries(raw)) {
    if (list?.length) {
      result[key as ProjectBoardType] = normalizeTaskStatuses(list);
    }
  }
  result.rfi = normalizeRfiBoardTaskStatuses(result.rfi);
  return result;
}

export function isCompleteStatusForTask(
  task: { status: string; boardType: Task['boardType'] },
  boardTaskStatuses: BoardTaskStatusesMap
): boolean {
  const statuses = getBoardTaskStatuses(statusBoardForTask(task), boardTaskStatuses);
  return isCompleteStatus(task.status, statuses);
}
