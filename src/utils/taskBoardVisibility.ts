import type { Task, TaskStatusDefinition } from '../types';
import {
  DEFAULT_DELIVERABLES_TASK_STATUSES,
  DEFAULT_RFI_TASK_STATUSES,
  DEFAULT_TASK_STATUSES,
  getStatusColor,
  normalizeTaskStatuses,
  statusLabel,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from './taskStatuses';
import {
  dedupeTaskBoardStatusOptionsByLabel,
  expandTaskBoardVisibleStatusIds,
} from './statusConsolidation';

export const TASK_BOARD_DEFAULT_HIDDEN_STATUS_IDS = new Set(['not-started', 'not-ready']);

const STATUS_ORDER = [
  ...DEFAULT_TASK_STATUSES.map((status) => status.id),
  ...DEFAULT_DELIVERABLES_TASK_STATUSES.map((status) => status.id),
  ...DEFAULT_RFI_TASK_STATUSES.map((status) => status.id),
];

function statusSortIndex(statusId: string): number {
  const index = STATUS_ORDER.indexOf(statusId);
  return index === -1 ? STATUS_ORDER.length : index;
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

export function buildDefaultTaskBoardVisibleStatuses(options: TaskStatusDefinition[]): string[] {
  return options
    .map((status) => status.id)
    .filter((statusId) => !TASK_BOARD_DEFAULT_HIDDEN_STATUS_IDS.has(statusId));
}

export function normalizeTaskBoardVisibleStatuses(
  stored: string[] | undefined | null,
  options: TaskStatusDefinition[]
): string[] {
  const optionIds = new Set(options.map((status) => status.id));
  if (!stored?.length) {
    return buildDefaultTaskBoardVisibleStatuses(options);
  }

  const visible = stored.filter((statusId) => optionIds.has(statusId));
  return visible.length ? visible : buildDefaultTaskBoardVisibleStatuses(options);
}

export function isTaskVisibleOnTaskBoard(
  task: Task,
  visibleStatusIds: Set<string>,
  boardTaskStatuses?: BoardTaskStatusesMap,
  projectBoardTaskStatuses?: ProjectBoardTaskStatusesMap,
  tasks?: Task[]
): boolean {
  if (visibleStatusIds.has(task.status)) return true;
  if (!boardTaskStatuses || !projectBoardTaskStatuses || !tasks) return false;

  const expanded = expandTaskBoardVisibleStatusIds(
    [...visibleStatusIds],
    boardTaskStatuses,
    projectBoardTaskStatuses,
    tasks
  );
  return expanded.has(task.status);
}

export function taskBoardVisibleStatusSet(visibleStatusIds: string[]): Set<string> {
  return new Set(visibleStatusIds);
}
