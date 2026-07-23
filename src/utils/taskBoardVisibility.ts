import type { Task, TaskStatusDefinition } from '../types';
import {
  DEFAULT_DELIVERABLES_TASK_STATUSES,
  DEFAULT_RFI_TASK_STATUSES,
  DEFAULT_TASK_STATUSES,
  getBoardTaskStatuses,
  getStatusColor,
  isCompleteStatus,
  normalizeTaskStatuses,
  statusBoardForTask,
  statusLabel,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from './taskStatuses';
import {
  dedupeTaskBoardStatusOptionsByLabel,
  expandTaskBoardVisibleStatusIds,
} from './statusConsolidation';

export const TASK_BOARD_DEFAULT_HIDDEN_STATUS_IDS = new Set([
  'not-started',
  'not-ready',
  'complete',
]);

const STATUS_ORDER = [
  ...DEFAULT_TASK_STATUSES.map((status) => status.id),
  ...DEFAULT_DELIVERABLES_TASK_STATUSES.map((status) => status.id),
  ...DEFAULT_RFI_TASK_STATUSES.map((status) => status.id),
];

function statusSortIndex(statusId: string): number {
  const index = STATUS_ORDER.indexOf(statusId);
  return index === -1 ? STATUS_ORDER.length : index;
}

/** Complete (and any countsAsComplete status) never appear on the Team Task Board. */
export function isTaskBoardCompleteStatus(
  statusId: string,
  boardTaskStatuses: BoardTaskStatusesMap = {},
  projectBoardTaskStatuses?: ProjectBoardTaskStatusesMap,
  tasks: Task[] = []
): boolean {
  if (statusId === 'complete') return true;

  for (const list of Object.values(boardTaskStatuses)) {
    if (isCompleteStatus(statusId, normalizeTaskStatuses(list))) return true;
  }
  for (const projectStatuses of Object.values(projectBoardTaskStatuses ?? {})) {
    for (const list of Object.values(projectStatuses ?? {})) {
      if (isCompleteStatus(statusId, normalizeTaskStatuses(list))) return true;
    }
  }
  for (const task of tasks) {
    if (task.status !== statusId) continue;
    const statuses = getBoardTaskStatuses(
      statusBoardForTask(task),
      boardTaskStatuses,
      task.projectId,
      projectBoardTaskStatuses
    );
    if (isCompleteStatus(statusId, statuses)) return true;
  }
  return isCompleteStatus(statusId, DEFAULT_TASK_STATUSES);
}

export function collectTaskBoardStatusOptions(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  tasks: Task[]
): TaskStatusDefinition[] {
  const map = new Map<string, TaskStatusDefinition>();

  for (const list of Object.values(boardTaskStatuses)) {
    for (const status of normalizeTaskStatuses(list)) {
      map.set(status.id, status);
    }
  }

  for (const projectStatuses of Object.values(projectBoardTaskStatuses)) {
    for (const list of Object.values(projectStatuses)) {
      for (const status of normalizeTaskStatuses(list)) {
        map.set(status.id, status);
      }
    }
  }

  for (const status of DEFAULT_TASK_STATUSES) {
    if (!map.has(status.id)) map.set(status.id, status);
  }

  for (const status of DEFAULT_DELIVERABLES_TASK_STATUSES) {
    if (!map.has(status.id)) map.set(status.id, status);
  }

  const known = [...map.values()];
  for (const task of tasks) {
    if (map.has(task.status)) continue;
    map.set(task.status, {
      id: task.status,
      label: statusLabel(task.status, known) || task.status,
      color: getStatusColor(task.status, known),
    });
  }

  return dedupeTaskBoardStatusOptionsByLabel(
    [...map.values()].sort(
      (left, right) =>
        statusSortIndex(left.id) - statusSortIndex(right.id) ||
        left.label.localeCompare(right.label)
    )
  );
}

function isCompleteOption(status: TaskStatusDefinition): boolean {
  return Boolean(status.countsAsComplete) || status.id === 'complete';
}

export function buildDefaultTaskBoardVisibleStatuses(options: TaskStatusDefinition[]): string[] {
  return options
    .map((status) => status.id)
    .filter(
      (statusId) =>
        !TASK_BOARD_DEFAULT_HIDDEN_STATUS_IDS.has(statusId) &&
        !options.find((status) => status.id === statusId)?.countsAsComplete
    );
}

export function normalizeTaskBoardVisibleStatuses(
  stored: string[] | undefined | null,
  options: TaskStatusDefinition[]
): string[] {
  const optionIds = new Set(options.map((status) => status.id));
  const stripComplete = (ids: string[]) =>
    ids.filter((statusId) => {
      if (!optionIds.has(statusId)) return false;
      if (TASK_BOARD_DEFAULT_HIDDEN_STATUS_IDS.has(statusId)) return false;
      const option = options.find((status) => status.id === statusId);
      if (option && isCompleteOption(option)) return false;
      return true;
    });

  if (!stored?.length) {
    return stripComplete(buildDefaultTaskBoardVisibleStatuses(options));
  }

  const visible = stripComplete(stored.filter((statusId) => optionIds.has(statusId)));
  return visible.length ? visible : stripComplete(buildDefaultTaskBoardVisibleStatuses(options));
}

export function isTaskVisibleOnTaskBoard(
  task: Task,
  visibleStatusIds: Set<string>,
  boardTaskStatuses?: BoardTaskStatusesMap,
  projectBoardTaskStatuses?: ProjectBoardTaskStatusesMap,
  tasks?: Task[]
): boolean {
  const boards = boardTaskStatuses ?? {};
  const projectBoards = projectBoardTaskStatuses ?? {};
  const allTasks = tasks ?? [];

  // Hard rule: completed work leaves the Team Task Board immediately.
  const statuses = getBoardTaskStatuses(
    statusBoardForTask(task),
    boards,
    task.projectId,
    projectBoards
  );
  if (isCompleteStatus(task.status, statuses)) return false;
  if (isTaskBoardCompleteStatus(task.status, boards, projectBoards, allTasks)) return false;

  if (visibleStatusIds.has(task.status)) return true;
  if (!boardTaskStatuses || !projectBoardTaskStatuses || !tasks) return false;

  const expanded = expandTaskBoardVisibleStatusIds(
    [...visibleStatusIds],
    boardTaskStatuses,
    projectBoardTaskStatuses,
    tasks
  );
  if (expanded.has(task.status)) {
    // Expanded aliases can reintroduce complete — block those too.
    if (isCompleteStatus(task.status, statuses)) return false;
    return true;
  }
  return false;
}

export function taskBoardVisibleStatusSet(visibleStatusIds: string[]): Set<string> {
  return new Set(visibleStatusIds);
}
