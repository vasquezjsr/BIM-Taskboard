import type { ProjectBoardType, TaskStatusDefinition } from '../types';
import {
  BUILT_IN_BOARD_TYPES,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from './taskStatuses';

export interface StatusColorConflict {
  statusId: string;
  label: string;
  colors: string[];
}

function normalizeColor(color: string): string {
  return color.trim().toLowerCase();
}

function colorsEqual(left: string, right: string): boolean {
  return normalizeColor(left) === normalizeColor(right);
}

function collectStatusOccurrences(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): Map<string, TaskStatusDefinition[]> {
  const byId = new Map<string, TaskStatusDefinition[]>();

  const add = (status: TaskStatusDefinition) => {
    const list = byId.get(status.id) ?? [];
    list.push(status);
    byId.set(status.id, list);
  };

  for (const list of Object.values(boardTaskStatuses)) {
    list?.forEach(add);
  }

  for (const projectBoards of Object.values(projectBoardTaskStatuses)) {
    if (!projectBoards) continue;
    for (const list of Object.values(projectBoards)) {
      list?.forEach(add);
    }
  }

  return byId;
}

/** True when the same status id uses more than one color across boards/projects. */
export function findStatusColorConflicts(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): StatusColorConflict[] {
  const conflicts: StatusColorConflict[] = [];

  for (const [statusId, occurrences] of collectStatusOccurrences(
    boardTaskStatuses,
    projectBoardTaskStatuses
  )) {
    const uniqueColors = new Set(occurrences.map((status) => normalizeColor(status.color)));
    if (uniqueColors.size <= 1) continue;

    conflicts.push({
      statusId,
      label: occurrences[0]?.label ?? statusId,
      colors: [...uniqueColors],
    });
  }

  return conflicts.sort((left, right) => left.label.localeCompare(right.label));
}

function resolveCanonicalStatusColor(
  statusId: string,
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): string | null {
  for (const boardType of BUILT_IN_BOARD_TYPES) {
    const match = boardTaskStatuses[boardType]?.find((status) => status.id === statusId);
    if (match?.color) return match.color;
  }

  for (const customList of Object.values(boardTaskStatuses)) {
    const match = customList?.find((status) => status.id === statusId);
    if (match?.color) return match.color;
  }

  for (const projectBoards of Object.values(projectBoardTaskStatuses)) {
    if (!projectBoards) continue;
    for (const list of Object.values(projectBoards)) {
      const match = list?.find((status) => status.id === statusId);
      if (match?.color) return match.color;
    }
  }

  return null;
}

function buildCanonicalStatusColorMap(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): Map<string, string> {
  const canonical = new Map<string, string>();
  const occurrences = collectStatusOccurrences(boardTaskStatuses, projectBoardTaskStatuses);

  for (const statusId of occurrences.keys()) {
    const color = resolveCanonicalStatusColor(statusId, boardTaskStatuses, projectBoardTaskStatuses);
    if (color) canonical.set(statusId, color);
  }

  return canonical;
}

function syncStatusList(
  list: TaskStatusDefinition[] | undefined,
  canonical: Map<string, string>
): TaskStatusDefinition[] | undefined {
  if (!list?.length) return list;

  let changed = false;
  const next = list.map((status) => {
    const color = canonical.get(status.id);
    if (!color || colorsEqual(status.color, color)) return status;
    changed = true;
    return { ...status, color };
  });

  return changed ? next : list;
}

function syncBoardTaskStatuses(
  boardTaskStatuses: BoardTaskStatusesMap,
  canonical: Map<string, string>
): BoardTaskStatusesMap {
  let changed = false;
  const next: BoardTaskStatusesMap = { ...boardTaskStatuses };

  for (const [boardType, list] of Object.entries(boardTaskStatuses)) {
    const synced = syncStatusList(list, canonical);
    if (synced !== list) {
      changed = true;
      next[boardType as ProjectBoardType] = synced ?? [];
    }
  }

  return changed ? next : boardTaskStatuses;
}

function syncProjectBoardTaskStatuses(
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  canonical: Map<string, string>
): ProjectBoardTaskStatusesMap {
  let changed = false;
  const next: ProjectBoardTaskStatusesMap = { ...projectBoardTaskStatuses };

  for (const [projectId, boards] of Object.entries(projectBoardTaskStatuses)) {
    if (!boards) continue;
    let projectChanged = false;
    const syncedBoards = { ...boards };

    for (const [boardType, list] of Object.entries(boards)) {
      const synced = syncStatusList(list, canonical);
      if (synced !== list) {
        projectChanged = true;
        syncedBoards[boardType as ProjectBoardType] = synced ?? [];
      }
    }

    if (projectChanged) {
      changed = true;
      next[projectId] = syncedBoards;
    }
  }

  return changed ? next : projectBoardTaskStatuses;
}

/** Unify colors for each status id across all boards and projects. */
export function syncStatusColorMaps(
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): {
  boardTaskStatuses: BoardTaskStatusesMap;
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap;
  syncedCount: number;
} {
  const conflicts = findStatusColorConflicts(boardTaskStatuses, projectBoardTaskStatuses);
  if (conflicts.length === 0) {
    return { boardTaskStatuses, projectBoardTaskStatuses, syncedCount: 0 };
  }

  const canonical = buildCanonicalStatusColorMap(boardTaskStatuses, projectBoardTaskStatuses);

  return {
    boardTaskStatuses: syncBoardTaskStatuses(boardTaskStatuses, canonical),
    projectBoardTaskStatuses: syncProjectBoardTaskStatuses(projectBoardTaskStatuses, canonical),
    syncedCount: conflicts.length,
  };
}

/** Apply one color to every occurrence of a status id (used when editing a status color). */
export function applyStatusColorGlobally(
  statusId: string,
  color: string,
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): {
  boardTaskStatuses: BoardTaskStatusesMap;
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap;
} {
  const canonical = new Map<string, string>([[statusId, color]]);
  return {
    boardTaskStatuses: syncBoardTaskStatuses(boardTaskStatuses, canonical),
    projectBoardTaskStatuses: syncProjectBoardTaskStatuses(projectBoardTaskStatuses, canonical),
  };
}
