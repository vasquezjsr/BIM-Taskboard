import type { CustomBoard, ProjectBoardType, SheetColumnDefinition, TaskGroup } from '../types';
import { getProjectSubBoardOrder } from '../types';
import {
  buildSheetColumnSlots,
  getBoardSheetColumnOrder,
  getBoardSheetColumns,
  isFixedSheetColumnId,
  normalizeSheetColumns,
  type BoardSheetColumnOrderMap,
  type BoardSheetColumnsMap,
  type SheetColumnSlot,
} from './sheetColumns';
import { getMainGroups, taskBranchBoardType, type SheetRow } from './groupRows';
import type { Task } from '../types';

const LEADING_FIXED_COLUMNS = ['title', 'description', 'status', 'assignee'] as const;

export interface MainOverviewColumnLayout {
  unionOrder: string[];
  unionSlots: SheetColumnSlot[];
  sectionColumnIds: Map<ProjectBoardType, Set<string>>;
  customColumns: SheetColumnDefinition[];
}

export function getMainOverviewSectionBoardTypes(
  taskGroups: TaskGroup[],
  clientId: string,
  projectId: string,
  subBoardTabOrder: ProjectBoardType[],
  customBoards: CustomBoard[]
): ProjectBoardType[] {
  const tabOrder = getProjectSubBoardOrder(projectId, subBoardTabOrder, customBoards);
  const sectionTypes = new Set(
    getMainGroups(taskGroups, clientId, projectId)
      .filter((group) => group.tier === 'section' && group.sectionBoardType)
      .map((group) => group.sectionBoardType as ProjectBoardType)
  );

  const ordered: ProjectBoardType[] = [];
  for (const boardType of tabOrder) {
    if (boardType !== 'main') ordered.push(boardType);
  }
  for (const boardType of sectionTypes) {
    if (!ordered.includes(boardType)) ordered.push(boardType);
  }
  return ordered;
}

export function getMainOverviewSectionColumns(
  sectionBoardType: ProjectBoardType,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  boardSheetColumns: BoardSheetColumnsMap
): SheetColumnDefinition[] {
  const base = getBoardSheetColumns(sectionBoardType, boardSheetColumns);
  const extras = mainOverviewSectionSheetColumns[sectionBoardType];
  if (!extras?.length) return base;

  const baseIds = new Set(base.map((column) => column.id));
  const merged = [...base];
  for (const column of normalizeSheetColumns(extras)) {
    if (!baseIds.has(column.id)) merged.push(column);
  }
  return merged;
}

/** Stored Main Overview section order is authoritative — do not merge default workflow columns back in. */
export function resolveStoredMainOverviewSectionColumnOrder(
  sectionBoardType: ProjectBoardType,
  stored: string[],
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  boardSheetColumns: BoardSheetColumnsMap
): string[] {
  const columns = getMainOverviewSectionColumns(
    sectionBoardType,
    mainOverviewSectionSheetColumns,
    boardSheetColumns
  );
  const customIds = new Set(columns.map((column) => column.id));
  const sectionExtras = mainOverviewSectionSheetColumns[sectionBoardType] ?? [];

  const seen = new Set<string>();
  const result: string[] = [];

  for (const id of stored) {
    if (id === 'board') continue;
    const isValidFixed = isFixedSheetColumnId(id) && id !== 'board';
    const isValidCustom = customIds.has(id);
    if ((isValidFixed || isValidCustom) && !seen.has(id)) {
      result.push(id);
      seen.add(id);
    }
  }

  for (const column of sectionExtras) {
    if (!seen.has(column.id)) {
      result.push(column.id);
      seen.add(column.id);
    }
  }

  return result;
}

export function normalizeMainOverviewSectionColumnOrder(
  raw: BoardSheetColumnOrderMap | undefined | null,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  boardSheetColumns: BoardSheetColumnsMap
): BoardSheetColumnOrderMap {
  if (!raw) return {};
  const result: BoardSheetColumnOrderMap = {};
  for (const [key, stored] of Object.entries(raw)) {
    if (!stored?.length) continue;
    result[key as ProjectBoardType] = resolveStoredMainOverviewSectionColumnOrder(
      key as ProjectBoardType,
      stored,
      mainOverviewSectionSheetColumns,
      boardSheetColumns
    );
  }
  return result;
}

export function getMainOverviewSectionColumnOrder(
  sectionBoardType: ProjectBoardType,
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  boardSheetColumns: BoardSheetColumnsMap
): string[] {
  const stored = mainOverviewSectionColumnOrder[sectionBoardType];

  if (stored?.length) {
    return resolveStoredMainOverviewSectionColumnOrder(
      sectionBoardType,
      stored,
      mainOverviewSectionSheetColumns,
      boardSheetColumns
    );
  }

  return getBoardSheetColumnOrder(
    sectionBoardType,
    boardSheetColumnOrder,
    boardSheetColumns,
    false
  );
}

export function buildMainOverviewColumnLayout(
  sectionBoardTypes: ProjectBoardType[],
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  boardSheetColumns: BoardSheetColumnsMap
): MainOverviewColumnLayout {
  const sectionColumnIds = new Map<ProjectBoardType, Set<string>>();
  const customById = new Map<string, SheetColumnDefinition>();
  const unionOrder: string[] = [];
  const seen = new Set<string>();

  const pushId = (id: string) => {
    if (seen.has(id)) return;
    seen.add(id);
    unionOrder.push(id);
  };

  for (const id of LEADING_FIXED_COLUMNS) pushId(id);

  for (const sectionBoardType of sectionBoardTypes) {
    const order = getMainOverviewSectionColumnOrder(
      sectionBoardType,
      mainOverviewSectionColumnOrder,
      mainOverviewSectionSheetColumns,
      boardSheetColumnOrder,
      boardSheetColumns
    );
    sectionColumnIds.set(sectionBoardType, new Set(order));

    for (const column of getMainOverviewSectionColumns(
      sectionBoardType,
      mainOverviewSectionSheetColumns,
      boardSheetColumns
    )) {
      customById.set(column.id, column);
    }

    for (const id of order) {
      if (id === 'board') continue;
      if (LEADING_FIXED_COLUMNS.includes(id as (typeof LEADING_FIXED_COLUMNS)[number])) continue;
      pushId(id);
    }
  }

  pushId('board');

  const customColumns = unionOrder
    .filter((id) => !isFixedSheetColumnId(id))
    .map((id) => customById.get(id))
    .filter((column): column is SheetColumnDefinition => Boolean(column));

  return {
    unionOrder,
    unionSlots: buildSheetColumnSlots(unionOrder, customColumns, true),
    sectionColumnIds,
    customColumns,
  };
}

export function resolveOverviewSectionBoardType(
  group: TaskGroup,
  taskGroups: TaskGroup[]
): ProjectBoardType | null {
  if (group.tier === 'section' && group.sectionBoardType) return group.sectionBoardType;
  let current: TaskGroup | undefined = group;
  while (current) {
    if (current.tier === 'section' && current.sectionBoardType) {
      return current.sectionBoardType;
    }
    current = current.parentId
      ? taskGroups.find((entry) => entry.id === current!.parentId)
      : undefined;
  }
  return null;
}

export function resolveOverviewSectionForTask(
  task: Task,
  taskGroups: TaskGroup[]
): ProjectBoardType | null {
  const branch = taskBranchBoardType(task, taskGroups);
  return branch === 'main' ? null : branch;
}

export function resolveOverviewSectionForRow(
  row: SheetRow,
  taskGroups: TaskGroup[]
): ProjectBoardType | null {
  if (row.type === 'group') {
    return resolveOverviewSectionBoardType(row.group, taskGroups);
  }
  return resolveOverviewSectionForTask(row.task, taskGroups);
}

export function isColumnVisibleInOverviewSection(
  layout: MainOverviewColumnLayout,
  sectionBoardType: ProjectBoardType | null,
  columnId: string
): boolean {
  if (!sectionBoardType) {
    return columnId === 'title' || columnId === 'board';
  }
  return layout.sectionColumnIds.get(sectionBoardType)?.has(columnId) ?? false;
}

export interface MainOverviewSectionSheetLayout {
  columnOrder: string[];
  columnSlots: SheetColumnSlot[];
  sheetColumns: SheetColumnDefinition[];
}

export function getMainOverviewSectionSheetLayout(
  sectionBoardType: ProjectBoardType,
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  boardSheetColumns: BoardSheetColumnsMap
): MainOverviewSectionSheetLayout {
  const sheetColumns = getMainOverviewSectionColumns(
    sectionBoardType,
    mainOverviewSectionSheetColumns,
    boardSheetColumns
  );
  const columnOrder = getMainOverviewSectionColumnOrder(
    sectionBoardType,
    mainOverviewSectionColumnOrder,
    mainOverviewSectionSheetColumns,
    boardSheetColumnOrder,
    boardSheetColumns
  );
  return {
    columnOrder,
    sheetColumns,
    columnSlots: buildSheetColumnSlots(columnOrder, sheetColumns, false),
  };
}

export const OVERVIEW_SECTION_COL_DRAG_PREFIX = 'col:section:';

export function overviewSectionColDragId(
  sectionBoardType: ProjectBoardType,
  columnId: string
): string {
  return `${OVERVIEW_SECTION_COL_DRAG_PREFIX}${sectionBoardType}/${columnId}`;
}

export function parseOverviewSectionColDragId(
  id: string
): { sectionBoardType: ProjectBoardType; columnId: string } | null {
  if (!id.startsWith(OVERVIEW_SECTION_COL_DRAG_PREFIX)) return null;
  const rest = id.slice(OVERVIEW_SECTION_COL_DRAG_PREFIX.length);
  const slash = rest.indexOf('/');
  if (slash === -1) return null;
  return {
    sectionBoardType: rest.slice(0, slash) as ProjectBoardType,
    columnId: rest.slice(slash + 1),
  };
}

export function splitMainOverviewSheetRows(
  sheetRows: SheetRow[],
  sectionBoardTypes: ProjectBoardType[]
): {
  preludeRows: SheetRow[];
  sectionRows: Map<ProjectBoardType, SheetRow[]>;
} {
  const preludeRows: SheetRow[] = [];
  const sectionRows = new Map<ProjectBoardType, SheetRow[]>();
  let currentSection: ProjectBoardType | null = null;

  for (const row of sheetRows) {
    if (row.type === 'group' && row.group.tier === 'section' && row.group.sectionBoardType) {
      currentSection = row.group.sectionBoardType;
      if (!sectionRows.has(currentSection)) {
        sectionRows.set(currentSection, []);
      }
      continue;
    }
    if (currentSection) {
      sectionRows.get(currentSection)!.push(row);
    } else {
      preludeRows.push(row);
    }
  }

  for (const sectionBoardType of sectionBoardTypes) {
    if (!sectionRows.has(sectionBoardType)) {
      sectionRows.set(sectionBoardType, []);
    }
  }

  return { preludeRows, sectionRows };
}
