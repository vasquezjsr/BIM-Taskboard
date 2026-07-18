import type { Task, TaskStatusDefinition } from '../types';
import {
  DEFAULT_DELIVERABLES_TASK_STATUSES,
  DEFAULT_RFI_TASK_STATUSES,
  DEFAULT_TASK_STATUSES,
  normalizeTaskStatuses,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from './taskStatuses';
import { syncStatusColorMaps } from './statusColorSync';

const BUILT_IN_STATUSES: TaskStatusDefinition[] = [
  ...DEFAULT_TASK_STATUSES,
  ...DEFAULT_DELIVERABLES_TASK_STATUSES,
  ...DEFAULT_RFI_TASK_STATUSES,
];

const BUILT_IN_ID_BY_LABEL = new Map<string, string>(
  BUILT_IN_STATUSES.map((status) => [normalizeStatusLabel(status.label), status.id])
);

const BUILT_IN_BY_ID = new Map<string, TaskStatusDefinition>(
  BUILT_IN_STATUSES.map((status) => [status.id, status])
);

export function normalizeStatusLabel(label: string): string {
  return label.trim().toLowerCase();
}

function collectAllStatuses(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): TaskStatusDefinition[] {
  const list: TaskStatusDefinition[] = [];

  for (const statuses of Object.values(boardTaskStatuses)) {
    list.push(...normalizeTaskStatuses(statuses));
  }

  for (const projectBoards of Object.values(projectBoardTaskStatuses)) {
    if (!projectBoards) continue;
    for (const statuses of Object.values(projectBoards)) {
      list.push(...normalizeTaskStatuses(statuses));
    }
  }

  return list;
}

export function findDuplicateStatusLabels(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): { label: string; ids: string[] }[] {
  const byLabel = new Map<string, Set<string>>();

  for (const status of collectAllStatuses(boardTaskStatuses, projectBoardTaskStatuses)) {
    const key = normalizeStatusLabel(status.label);
    const ids = byLabel.get(key) ?? new Set<string>();
    ids.add(status.id);
    byLabel.set(key, ids);
  }

  return [...byLabel.entries()]
    .filter(([, ids]) => ids.size > 1)
    .map(([label, ids]) => ({ label, ids: [...ids] }))
    .sort((left, right) => left.label.localeCompare(right.label));
}

export function buildStatusIdAliasMap(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): Map<string, string> {
  const aliasMap = new Map<string, string>();
  const duplicates = findDuplicateStatusLabels(boardTaskStatuses, projectBoardTaskStatuses);

  for (const { label, ids } of duplicates) {
    const canonicalId = pickCanonicalStatusId(label, ids);
    for (const id of ids) {
      if (id !== canonicalId) aliasMap.set(id, canonicalId);
    }
  }

  return aliasMap;
}

export function pickCanonicalStatusId(label: string, candidateIds: string[]): string {
  const builtInId = BUILT_IN_ID_BY_LABEL.get(normalizeStatusLabel(label));
  if (builtInId && candidateIds.includes(builtInId)) return builtInId;

  const slugId = candidateIds.find((id) => !id.startsWith('status-'));
  if (slugId) return slugId;

  return [...candidateIds].sort()[0];
}

export function buildStatusIdToLabelMap(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  tasks: Task[]
): Map<string, string> {
  const map = new Map<string, string>();

  for (const status of collectAllStatuses(boardTaskStatuses, projectBoardTaskStatuses)) {
    map.set(status.id, normalizeStatusLabel(status.label));
  }

  for (const status of BUILT_IN_STATUSES) {
    map.set(status.id, normalizeStatusLabel(status.label));
  }

  for (const task of tasks) {
    if (map.has(task.status)) continue;
    map.set(task.status, normalizeStatusLabel(task.status));
  }

  return map;
}

export function findAllStatusIdsForLabel(
  label: string,
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  tasks: Task[]
): string[] {
  const normalized = normalizeStatusLabel(label);
  const ids = new Set<string>();
  const idToLabel = buildStatusIdToLabelMap(boardTaskStatuses, projectBoardTaskStatuses, tasks);

  for (const [id, statusLabel] of idToLabel) {
    if (statusLabel === normalized) ids.add(id);
  }

  return [...ids];
}

export function expandTaskBoardVisibleStatusIds(
  visibleIds: string[],
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  tasks: Task[]
): Set<string> {
  const idToLabel = buildStatusIdToLabelMap(boardTaskStatuses, projectBoardTaskStatuses, tasks);
  const visibleLabels = new Set<string>();

  for (const id of visibleIds) {
    const label = idToLabel.get(id);
    if (label) visibleLabels.add(label);
  }

  const expanded = new Set(visibleIds);
  for (const [id, label] of idToLabel) {
    if (visibleLabels.has(label)) expanded.add(id);
  }

  return expanded;
}

function remapStatusList(
  list: TaskStatusDefinition[] | undefined,
  aliasMap: Map<string, string>
): TaskStatusDefinition[] | undefined {
  if (!list?.length) return list;

  const seenIds = new Set<string>();
  const result: TaskStatusDefinition[] = [];

  for (const status of normalizeTaskStatuses(list)) {
    const canonicalId = aliasMap.get(status.id) ?? status.id;
    if (seenIds.has(canonicalId)) continue;
    seenIds.add(canonicalId);

    const builtIn = BUILT_IN_BY_ID.get(canonicalId);
    result.push(
      builtIn
        ? {
            ...builtIn,
            ...status,
            id: canonicalId,
            label: builtIn.label,
            color: status.color || builtIn.color,
            countsAsComplete: status.countsAsComplete ?? builtIn.countsAsComplete,
            ...(status.autoAssignTeam !== undefined
              ? { autoAssignTeam: status.autoAssignTeam }
              : builtIn.autoAssignTeam !== undefined
                ? { autoAssignTeam: builtIn.autoAssignTeam }
                : {}),
            ...(status.autoAssignEmployeeId !== undefined
              ? { autoAssignEmployeeId: status.autoAssignEmployeeId }
              : builtIn.autoAssignEmployeeId !== undefined
                ? { autoAssignEmployeeId: builtIn.autoAssignEmployeeId }
                : {}),
          }
        : { ...status, id: canonicalId }
    );
  }

  return result;
}

function remapBoardTaskStatuses(
  boardTaskStatuses: BoardTaskStatusesMap,
  aliasMap: Map<string, string>
): BoardTaskStatusesMap {
  if (aliasMap.size === 0) return boardTaskStatuses;

  const next: BoardTaskStatusesMap = { ...boardTaskStatuses };
  for (const [boardType, list] of Object.entries(boardTaskStatuses)) {
    const remapped = remapStatusList(list, aliasMap);
    if (remapped !== list) next[boardType as keyof BoardTaskStatusesMap] = remapped;
  }
  return next;
}

function remapProjectBoardTaskStatuses(
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  aliasMap: Map<string, string>
): ProjectBoardTaskStatusesMap {
  if (aliasMap.size === 0) return projectBoardTaskStatuses;

  const next: ProjectBoardTaskStatusesMap = { ...projectBoardTaskStatuses };
  for (const [projectId, boards] of Object.entries(projectBoardTaskStatuses)) {
    if (!boards) continue;
    let projectChanged = false;
    const syncedBoards = { ...boards };

    for (const [boardType, list] of Object.entries(boards)) {
      const remapped = remapStatusList(list, aliasMap);
      if (remapped !== list) {
        projectChanged = true;
        syncedBoards[boardType as keyof BoardTaskStatusesMap] = remapped;
      }
    }

    if (projectChanged) next[projectId] = syncedBoards;
  }

  return next;
}

function remapTasks(tasks: Task[], aliasMap: Map<string, string>): { tasks: Task[]; remappedTaskCount: number } {
  if (aliasMap.size === 0) return { tasks, remappedTaskCount: 0 };

  let remappedTaskCount = 0;
  const next = tasks.map((task) => {
    const canonicalId = aliasMap.get(task.status);
    if (!canonicalId) return task;
    remappedTaskCount += 1;
    return { ...task, status: canonicalId };
  });

  return { tasks: next, remappedTaskCount };
}

function remapTaskBoardVisibleStatuses(
  visibleIds: string[],
  aliasMap: Map<string, string>
): string[] {
  if (aliasMap.size === 0) return visibleIds;
  return [...new Set(visibleIds.map((id) => aliasMap.get(id) ?? id))];
}

/** Merge statuses that share the same label but different ids across boards/projects. */
export function consolidateDuplicateStatuses(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  tasks: Task[],
  taskBoardVisibleStatuses: string[]
): {
  boardTaskStatuses: BoardTaskStatusesMap;
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap;
  tasks: Task[];
  taskBoardVisibleStatuses: string[];
  consolidatedCount: number;
  remappedTaskCount: number;
} {
  const aliasMap = buildStatusIdAliasMap(boardTaskStatuses, projectBoardTaskStatuses);
  const consolidatedCount = findDuplicateStatusLabels(boardTaskStatuses, projectBoardTaskStatuses).length;

  if (aliasMap.size === 0) {
    return {
      boardTaskStatuses,
      projectBoardTaskStatuses,
      tasks,
      taskBoardVisibleStatuses,
      consolidatedCount: 0,
      remappedTaskCount: 0,
    };
  }

  let nextBoardTaskStatuses = remapBoardTaskStatuses(boardTaskStatuses, aliasMap);
  let nextProjectBoardTaskStatuses = remapProjectBoardTaskStatuses(projectBoardTaskStatuses, aliasMap);
  const { tasks: nextTasks, remappedTaskCount } = remapTasks(tasks, aliasMap);
  const nextTaskBoardVisibleStatuses = remapTaskBoardVisibleStatuses(taskBoardVisibleStatuses, aliasMap);

  const syncedColors = syncStatusColorMaps(nextBoardTaskStatuses, nextProjectBoardTaskStatuses);

  return {
    boardTaskStatuses: syncedColors.boardTaskStatuses,
    projectBoardTaskStatuses: syncedColors.projectBoardTaskStatuses,
    tasks: nextTasks,
    taskBoardVisibleStatuses: nextTaskBoardVisibleStatuses,
    consolidatedCount,
    remappedTaskCount,
  };
}

export function dedupeTaskBoardStatusOptionsByLabel(
  statuses: TaskStatusDefinition[]
): TaskStatusDefinition[] {
  const byLabel = new Map<string, TaskStatusDefinition>();

  for (const status of statuses) {
    const key = normalizeStatusLabel(status.label);
    const existing = byLabel.get(key);
    if (!existing) {
      byLabel.set(key, {
        ...status,
        id: pickCanonicalStatusId(status.label, [status.id]),
      });
      continue;
    }

    const canonicalId = pickCanonicalStatusId(status.label, [existing.id, status.id]);
    const builtIn = BUILT_IN_BY_ID.get(canonicalId);
    byLabel.set(key, {
      ...(builtIn ?? existing),
      ...existing,
      ...status,
      id: canonicalId,
      label: builtIn?.label ?? existing.label ?? status.label,
      color: existing.color || status.color || builtIn?.color || '#94a3b8',
    });
  }

  return [...byLabel.values()];
}
