import type { Project, ProjectBoardType, Task, TaskGroup } from '../types';
import {
  getBoardTaskStatuses,
  statusBoardForTask,
  type BoardTaskStatusesMap,
} from './taskStatuses';

export const DELIVERABLES_STATUS_ASSIGNEES: Record<string, 'detailers' | 'support'> = {
  'not-started': 'detailers',
  'ready-for-pre-planning': 'support',
  'pre-planning-complete': 'support',
  'support-in-progress': 'support',
  'ready-for-spooling': 'support',
  'spool-in-progress': 'support',
  'spool-qa-review': 'support',
  'spool-approved': 'support',
  'ready-for-fab': 'detailers',
  'on-hold': 'detailers',
  'ready-for-support-team': 'support',
  'detailer-review': 'detailers',
  'fix-mark-ups': 'support',
  complete: 'detailers',
};

export type StatusAutoAssignChoice = 'none' | 'detailers' | 'support' | `person:${string}`;

export const STATUS_AUTO_ASSIGN_TEAM_OPTIONS: { id: StatusAutoAssignChoice; label: string }[] = [
  { id: 'none', label: 'None (manual)' },
  { id: 'detailers', label: 'Detailers' },
  { id: 'support', label: 'Support Team' },
];

/** @deprecated Prefer STATUS_AUTO_ASSIGN_TEAM_OPTIONS + people options from buildStatusAutoAssignOptions */
export const STATUS_AUTO_ASSIGN_OPTIONS = STATUS_AUTO_ASSIGN_TEAM_OPTIONS;

const PERSON_PREFIX = 'person:';

export function personAutoAssignChoice(employeeId: string): StatusAutoAssignChoice {
  return `${PERSON_PREFIX}${employeeId}` as StatusAutoAssignChoice;
}

export function isPersonAutoAssignChoice(choice: string): choice is `person:${string}` {
  return choice.startsWith(PERSON_PREFIX) && choice.length > PERSON_PREFIX.length;
}

export function autoAssignTeamToStoreValue(
  choice: StatusAutoAssignChoice | string
): 'detailers' | 'support' | null {
  if (choice === 'detailers') return 'detailers';
  if (choice === 'support') return 'support';
  return null;
}

export function autoAssignEmployeeIdToStoreValue(
  choice: StatusAutoAssignChoice | string
): string | null {
  if (isPersonAutoAssignChoice(choice)) {
    return choice.slice(PERSON_PREFIX.length);
  }
  return null;
}

export function autoAssignChoiceFromStatus(
  autoAssignTeam: 'detailers' | 'support' | null | undefined,
  autoAssignEmployeeId?: string | null
): StatusAutoAssignChoice {
  if (autoAssignEmployeeId) return personAutoAssignChoice(autoAssignEmployeeId);
  if (autoAssignTeam === 'detailers') return 'detailers';
  if (autoAssignTeam === 'support') return 'support';
  return 'none';
}

export function buildStatusAutoAssignOptions(
  employees: { id: string; name: string }[]
): { id: StatusAutoAssignChoice; label: string; group: 'team' | 'people' }[] {
  const people = [...employees]
    .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
    .map((employee) => ({
      id: personAutoAssignChoice(employee.id),
      label: employee.name,
      group: 'people' as const,
    }));
  return [
    ...STATUS_AUTO_ASSIGN_TEAM_OPTIONS.map((option) => ({ ...option, group: 'team' as const })),
    ...people,
  ];
}

const LEGACY_DELIVERABLES_STATUS_MAP: Record<string, string> = {
  'not-ready': 'not-started',
  ready: 'ready-for-pre-planning',
  'in-progress': 'support-in-progress',
  'ready-for-support-team': 'support-in-progress',
  'on-hold': 'on-hold',
  complete: 'complete',
};

export function isDeliverablesStatus(status: string): boolean {
  return status in DELIVERABLES_STATUS_ASSIGNEES;
}

export function migrateDeliverablesTaskStatus(status: string): string {
  if (isDeliverablesStatus(status)) return status;
  return LEGACY_DELIVERABLES_STATUS_MAP[status] ?? 'not-started';
}

function projectForTask(task: Pick<Task, 'projectId'>, projects: Project[]): Project | undefined {
  if (!task.projectId) return undefined;
  return projects.find((p) => p.id === task.projectId);
}

function teamIds(project: Project, team: 'detailers' | 'support'): string[] {
  return team === 'detailers' ? [...project.detailerIds] : [...project.supportIds];
}

export function resolveStatusAutoAssignTeam(
  board: ProjectBoardType,
  statusId: string,
  boardTaskStatuses: BoardTaskStatusesMap,
  projectId?: string | null,
  projectBoardTaskStatuses?: import('./taskStatuses').ProjectBoardTaskStatusesMap
): 'detailers' | 'support' | null {
  const statuses = getBoardTaskStatuses(
    board,
    boardTaskStatuses,
    projectId,
    projectBoardTaskStatuses
  );
  const def = statuses.find((s) => s.id === statusId);

  if (def && def.autoAssignTeam !== undefined) {
    return def.autoAssignTeam;
  }

  if (board === 'deliverables' && DELIVERABLES_STATUS_ASSIGNEES[statusId]) {
    return DELIVERABLES_STATUS_ASSIGNEES[statusId];
  }

  if (statusId === 'not-started' || statusId === 'not-ready') {
    return 'detailers';
  }

  return null;
}

export function resolveAutoAssigneeIds(
  task: Pick<Task, 'status' | 'boardType' | 'projectId' | 'groupId'>,
  projects: Project[],
  taskGroups: TaskGroup[] = [],
  boardTaskStatuses: BoardTaskStatusesMap = {},
  projectBoardTaskStatuses: import('./taskStatuses').ProjectBoardTaskStatusesMap = {}
): string[] | null {
  const project = projectForTask(task, projects);
  if (!project) return null;

  const board = statusBoardForTask(task, taskGroups);
  const statuses = getBoardTaskStatuses(
    board,
    boardTaskStatuses,
    task.projectId,
    projectBoardTaskStatuses
  );
  const def = statuses.find((s) => s.id === task.status);
  if (def?.autoAssignEmployeeId) {
    return [def.autoAssignEmployeeId];
  }

  const team = resolveStatusAutoAssignTeam(
    board,
    task.status,
    boardTaskStatuses,
    task.projectId,
    projectBoardTaskStatuses
  );
  if (!team) return null;

  return teamIds(project, team);
}

function sameAssigneeIds(left: string[], right: string[]): boolean {
  if (left.length !== right.length) return false;
  const rightSet = new Set(right);
  return left.every((id) => rightSet.has(id));
}

/** Manual edits that should turn status auto-assign back on. */
function shouldResumeStatusAutoAssign(
  assigneeIds: string[],
  autoAssigneeIds: string[] | null
): boolean {
  if (autoAssigneeIds === null) return assigneeIds.length === 0;
  if (assigneeIds.length === 0) return true;
  return sameAssigneeIds(assigneeIds, autoAssigneeIds);
}

function resumeStatusAutoAssignUpdate(
  assigneeIds: string[],
  autoAssigneeIds: string[] | null
): Partial<Pick<Task, 'assigneeIds' | 'assigneesLocked'>> {
  if (autoAssigneeIds === null) {
    return { assigneeIds, assigneesLocked: false };
  }
  return { assigneeIds: autoAssigneeIds, assigneesLocked: false };
}

/** Clear manual lock when assignees again match status automation. */
export function reconcileTaskAssigneeLock(
  task: Task,
  projects: Project[],
  taskGroups: TaskGroup[] = [],
  boardTaskStatuses: BoardTaskStatusesMap = {},
  projectBoardTaskStatuses: import('./taskStatuses').ProjectBoardTaskStatusesMap = {}
): Task {
  const autoAssigneeIds = resolveAutoAssigneeIds(
    task,
    projects,
    taskGroups,
    boardTaskStatuses,
    projectBoardTaskStatuses
  );

  if (shouldResumeStatusAutoAssign(task.assigneeIds, autoAssigneeIds)) {
    return { ...task, ...resumeStatusAutoAssignUpdate(task.assigneeIds, autoAssigneeIds) };
  }

  if (!task.assigneesLocked) return task;

  if (autoAssigneeIds === null) {
    return { ...task, assigneesLocked: false };
  }

  return task;
}

export function applyAutoAssigneesToTask(
  task: Task,
  projects: Project[],
  taskGroups: TaskGroup[] = [],
  boardTaskStatuses: BoardTaskStatusesMap = {},
  projectBoardTaskStatuses: import('./taskStatuses').ProjectBoardTaskStatusesMap = {},
  options?: { force?: boolean }
): Task {
  if (task.assigneesLocked && !options?.force) return task;

  const assigneeIds = resolveAutoAssigneeIds(
    task,
    projects,
    taskGroups,
    boardTaskStatuses,
    projectBoardTaskStatuses
  );
  if (assigneeIds === null) return task;
  return { ...task, assigneeIds, assigneesLocked: false };
}

export function enrichTaskUpdatesWithAutoAssignees(
  task: Task,
  updates: Partial<Task>,
  projects: Project[],
  taskGroups: TaskGroup[] = [],
  boardTaskStatuses: BoardTaskStatusesMap = {},
  projectBoardTaskStatuses: import('./taskStatuses').ProjectBoardTaskStatusesMap = {}
): Partial<Task> {
  const statusTouched = updates.status !== undefined;
  const boardTouched = updates.boardType !== undefined;
  const assigneesLocked = updates.assigneesLocked ?? task.assigneesLocked ?? false;

  if (updates.assigneeIds !== undefined && !statusTouched && !boardTouched) {
    const autoAssigneeIds = resolveAutoAssigneeIds(
      task,
      projects,
      taskGroups,
      boardTaskStatuses,
      projectBoardTaskStatuses
    );
    const nextAssigneeIds = updates.assigneeIds;

    if (nextAssigneeIds.length === 0 && updates.assigneesLocked === true) {
      return { ...updates, assigneeIds: [], assigneesLocked: true };
    }

    if (shouldResumeStatusAutoAssign(nextAssigneeIds, autoAssigneeIds)) {
      return { ...updates, ...resumeStatusAutoAssignUpdate(nextAssigneeIds, autoAssigneeIds) };
    }

    return { ...updates, assigneesLocked: true };
  }

  if (assigneesLocked && updates.assigneesLocked !== false) {
    return updates;
  }

  if (!statusTouched && !boardTouched) return updates;

  const nextTask: Task = { ...task, ...updates, assigneesLocked: false };
  const assigneeIds = resolveAutoAssigneeIds(
    nextTask,
    projects,
    taskGroups,
    boardTaskStatuses,
    projectBoardTaskStatuses
  );
  if (assigneeIds === null) return updates;
  return { ...updates, assigneeIds, assigneesLocked: false };
}

export function applyDeliverablesAutoAssignTeams(
  statuses: import('../types').TaskStatusDefinition[]
): import('../types').TaskStatusDefinition[] {
  return statuses.map((status) => ({
    ...status,
    autoAssignTeam:
      status.autoAssignTeam ??
      DELIVERABLES_STATUS_ASSIGNEES[status.id] ??
      null,
  }));
}
