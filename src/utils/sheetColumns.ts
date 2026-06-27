import type { ProjectBoardType, SheetColumnAlign, SheetColumnDefinition, SheetColumnType } from '../types';
import { DEFAULT_SHEET_COLUMN_ALIGNMENT } from './sheetColumnConstants';
import {
  boardUsesWorkflowDueDates,
  MAIN_OVERVIEW_SHARED_COLUMN_BOARDS,
  WORKFLOW_DUE_DATE_COLUMN_IDS,
  WORKFLOW_DUE_DATE_COLUMNS,
} from '../data/workflowDueDateColumns';
import { BUILT_IN_BOARD_TYPES } from './taskStatuses';
import { isFlatBoard, type FlatBoardType } from './flatBoards';

/** Flat boards hide workflow columns that don't apply to uploads / RFIs. */
const FLAT_BOARD_HIDDEN_COLUMNS: Record<FlatBoardType, readonly string[]> = {
  documents: ['status', 'assignee', 'due', 'duration'],
  rfi: ['assignee', 'due', 'duration'],
};

export function getHiddenColumnsForBoard(boardType: ProjectBoardType): Set<string> {
  if (!isFlatBoard(boardType)) return new Set();
  return new Set(FLAT_BOARD_HIDDEN_COLUMNS[boardType]);
}

export function boardShowsStatusColumn(boardType: ProjectBoardType): boolean {
  return !getHiddenColumnsForBoard(boardType).has('status');
}

export function boardShowsAssigneeColumn(boardType: ProjectBoardType): boolean {
  return !getHiddenColumnsForBoard(boardType).has('assignee');
}

export function filterBoardColumnOrder(boardType: ProjectBoardType, order: string[]): string[] {
  const hidden = getHiddenColumnsForBoard(boardType);
  if (!hidden.size) return order;
  return order.filter((id) => !hidden.has(id));
}

export function filterBoardCustomColumns(
  boardType: ProjectBoardType,
  columns: SheetColumnDefinition[]
): SheetColumnDefinition[] {
  const hidden = getHiddenColumnsForBoard(boardType);
  if (!hidden.size) return columns;
  return columns.filter((column) => !hidden.has(column.id));
}

function applyBoardColumnRules(boardType: ProjectBoardType, order: string[]): string[] {
  return filterBoardColumnOrder(boardType, order);
}

/** @deprecated Use filterBoardColumnOrder('documents', order) */
export function filterDocumentsBoardColumnOrder(order: string[]): string[] {
  return filterBoardColumnOrder('documents', order);
}

/** @deprecated Use filterBoardCustomColumns('documents', columns) */
export function filterDocumentsBoardCustomColumns(
  columns: SheetColumnDefinition[]
): SheetColumnDefinition[] {
  return filterBoardCustomColumns('documents', columns);
}

/** @deprecated Use getHiddenColumnsForBoard */
export const DOCUMENTS_BOARD_HIDDEN_COLUMNS = FLAT_BOARD_HIDDEN_COLUMNS.documents;

export type BoardSheetColumnsMap = Partial<Record<ProjectBoardType, SheetColumnDefinition[]>>;

export const SHEET_COLUMN_TYPES: { id: SheetColumnType; label: string }[] = [
  { id: 'text', label: 'Text' },
  { id: 'date', label: 'Date' },
  { id: 'duration', label: 'Date range (start & end)' },
  { id: 'dropdown', label: 'Dropdown list' },
];

export const SHEET_COLUMN_ALIGNMENTS: { id: SheetColumnAlign; label: string }[] = [
  { id: 'left', label: 'Left' },
  { id: 'center', label: 'Center' },
  { id: 'right', label: 'Right' },
];

export { DEFAULT_SHEET_COLUMN_ALIGNMENT } from './sheetColumnConstants';

export function normalizeSheetColumnAlignment(value?: string | null): SheetColumnAlign {
  if (value === 'left' || value === 'center' || value === 'right') return value;
  return DEFAULT_SHEET_COLUMN_ALIGNMENT;
}

export function normalizeSheetColumnAlignments(col: {
  headerAlignment?: SheetColumnAlign | null;
  cellAlignment?: SheetColumnAlign | null;
  alignment?: SheetColumnAlign | null;
}): { headerAlignment: SheetColumnAlign; cellAlignment: SheetColumnAlign } {
  const fallback = normalizeSheetColumnAlignment(col.alignment);
  return {
    headerAlignment: normalizeSheetColumnAlignment(col.headerAlignment ?? col.alignment ?? fallback),
    cellAlignment: normalizeSheetColumnAlignment(col.cellAlignment ?? col.alignment ?? fallback),
  };
}

export const DEFAULT_DURATION_COLUMN: SheetColumnDefinition = {
  id: 'duration',
  label: 'Duration',
  type: 'duration',
  headerAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
  cellAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
};

export const DEFAULT_BOARD_SHEET_COLUMNS: SheetColumnDefinition[] = [DEFAULT_DURATION_COLUMN];

export function normalizeSheetColumns(columns?: SheetColumnDefinition[] | null): SheetColumnDefinition[] {
  if (!columns?.length) return DEFAULT_BOARD_SHEET_COLUMNS.map((c) => ({ ...c }));
  const seen = new Set<string>();
  const result: SheetColumnDefinition[] = [];
  for (const col of columns) {
    if (!col.id || !col.label?.trim() || seen.has(col.id)) continue;
    seen.add(col.id);
    const type = SHEET_COLUMN_TYPES.some((t) => t.id === col.type) ? col.type : 'text';
    const { headerAlignment, cellAlignment } = normalizeSheetColumnAlignments(
      col as SheetColumnDefinition & { alignment?: SheetColumnAlign }
    );
    const normalized: SheetColumnDefinition = {
      id: col.id,
      label: col.label.trim(),
      type,
      headerAlignment,
      cellAlignment,
    };
    if (type === 'dropdown') {
      normalized.options = (col.options ?? [])
        .map((o) => o.trim())
        .filter(Boolean);
    }
    result.push(normalized);
  }
  return result.length ? result : DEFAULT_BOARD_SHEET_COLUMNS.map((c) => ({ ...c }));
}

export function getBoardLocalSheetColumns(
  boardType: ProjectBoardType,
  boardSheetColumns: BoardSheetColumnsMap
): SheetColumnDefinition[] {
  const list = boardSheetColumns[boardType];
  if (list?.length) return normalizeSheetColumns(list);
  return DEFAULT_BOARD_SHEET_COLUMNS.map((c) => ({ ...c }));
}

export function isMainOverviewSharedColumn(
  columnId: string,
  boardSheetColumns: BoardSheetColumnsMap
): boolean {
  return getBoardLocalSheetColumns('main', boardSheetColumns).some((column) => column.id === columnId);
}

export function mergeMainOverviewColumns(
  boardType: ProjectBoardType,
  boardSheetColumns: BoardSheetColumnsMap
): SheetColumnDefinition[] {
  if (boardType === 'main') {
    return getBoardLocalSheetColumns('main', boardSheetColumns);
  }

  if (!MAIN_OVERVIEW_SHARED_COLUMN_BOARDS.includes(boardType)) {
    return getBoardLocalSheetColumns(boardType, boardSheetColumns);
  }

  const mainColumns = getBoardLocalSheetColumns('main', boardSheetColumns);
  const boardColumns = getBoardLocalSheetColumns(boardType, boardSheetColumns);
  const mainIds = new Set(mainColumns.map((column) => column.id));
  const merged = mainColumns.map((column) => ({ ...column }));

  for (const column of boardColumns) {
    if (!mainIds.has(column.id)) {
      merged.push({ ...column });
    }
  }

  return merged;
}

export function appendSheetColumnDefinition(
  map: BoardSheetColumnsMap,
  boardType: ProjectBoardType,
  column: SheetColumnDefinition
): BoardSheetColumnsMap {
  const current = map[boardType] ?? getBoardLocalSheetColumns(boardType, map);
  if (current.some((entry) => entry.id === column.id)) return map;
  return {
    ...map,
    [boardType]: [...current, { ...column }],
  };
}

export function propagateMainSheetColumnToAllBoards(
  map: BoardSheetColumnsMap,
  column: SheetColumnDefinition,
  boardTypes: ProjectBoardType[]
): BoardSheetColumnsMap {
  let next = map;
  for (const boardType of boardTypes) {
    if (!MAIN_OVERVIEW_SHARED_COLUMN_BOARDS.includes(boardType) && boardType !== 'main') continue;
    next = appendSheetColumnDefinition(next, boardType, column);
  }
  return next;
}

export function removeSheetColumnDefinition(
  map: BoardSheetColumnsMap,
  boardType: ProjectBoardType,
  columnId: string
): BoardSheetColumnsMap {
  const current = map[boardType];
  if (!current?.length) return map;
  const remaining = current.filter((column) => column.id !== columnId);
  if (remaining.length === current.length) return map;
  return {
    ...map,
    [boardType]: remaining,
  };
}

export function removeMainSheetColumnFromAllBoards(
  map: BoardSheetColumnsMap,
  columnId: string,
  boardTypes: ProjectBoardType[]
): BoardSheetColumnsMap {
  let next = map;
  for (const boardType of boardTypes) {
    if (
      boardType !== 'main' &&
      !MAIN_OVERVIEW_SHARED_COLUMN_BOARDS.includes(boardType)
    ) {
      continue;
    }
    next = removeSheetColumnDefinition(next, boardType, columnId);
  }
  return next;
}

export function applySheetColumnUpdates(
  column: SheetColumnDefinition,
  updates: Partial<SheetColumnDefinition>
): SheetColumnDefinition {
  const next = { ...column, ...updates };
  if (next.type === 'dropdown') {
    next.options = (next.options ?? []).map((option) => option.trim()).filter(Boolean);
  } else {
    delete next.options;
  }
  return next;
}

export function syncMainSheetColumnUpdateToAllBoards(
  map: BoardSheetColumnsMap,
  columnId: string,
  updates: Partial<SheetColumnDefinition>,
  boardTypes: ProjectBoardType[]
): BoardSheetColumnsMap {
  let next = map;
  for (const boardType of boardTypes) {
    if (
      boardType !== 'main' &&
      !MAIN_OVERVIEW_SHARED_COLUMN_BOARDS.includes(boardType)
    ) {
      continue;
    }
    const current = next[boardType];
    if (!current?.some((column) => column.id === columnId)) continue;
    next = {
      ...next,
      [boardType]: current.map((column) =>
        column.id === columnId ? applySheetColumnUpdates(column, updates) : column
      ),
    };
  }
  return next;
}

export function syncMainOverviewColumnsToAllBoards(
  map: BoardSheetColumnsMap,
  boardTypes: ProjectBoardType[]
): BoardSheetColumnsMap {
  const mainColumns = getBoardLocalSheetColumns('main', map);
  let next = map;
  for (const boardType of boardTypes) {
    if (boardType === 'main') continue;
    if (!MAIN_OVERVIEW_SHARED_COLUMN_BOARDS.includes(boardType)) continue;
    for (const column of mainColumns) {
      next = appendSheetColumnDefinition(next, boardType, column);
    }
  }
  return next;
}

export function getAllConfiguredBoardTypes(
  customBoards: { id: string }[]
): ProjectBoardType[] {
  return [...BUILT_IN_BOARD_TYPES, ...customBoards.map((board) => board.id as ProjectBoardType)];
}

export function getBoardSheetColumns(
  boardType: ProjectBoardType,
  boardSheetColumns: BoardSheetColumnsMap
): SheetColumnDefinition[] {
  const columns = mergeMainOverviewColumns(boardType, boardSheetColumns);

  if (boardType === 'documents' || boardType === 'rfi') {
    return filterBoardCustomColumns(boardType, columns);
  }
  return columns;
}

export function createDefaultBoardSheetColumns(): BoardSheetColumnsMap {
  const map: BoardSheetColumnsMap = {};
  for (const board of BUILT_IN_BOARD_TYPES) {
    if (board === 'documents' || board === 'rfi') {
      map[board] = [];
      continue;
    }
    const base = DEFAULT_BOARD_SHEET_COLUMNS.map((column) => ({ ...column }));
    map[board] = boardUsesWorkflowDueDates(board)
      ? [...base, ...WORKFLOW_DUE_DATE_COLUMNS.map((column) => ({ ...column }))]
      : base;
  }
  return map;
}

export function normalizeBoardSheetColumns(
  raw: BoardSheetColumnsMap | undefined | null
): BoardSheetColumnsMap {
  const result = createDefaultBoardSheetColumns();
  if (!raw) return result;
  for (const [key, list] of Object.entries(raw)) {
    if (list?.length) {
      result[key as ProjectBoardType] = normalizeSheetColumns(list);
    }
  }
  result.documents = filterBoardCustomColumns('documents', result.documents ?? []);
  result.rfi = filterBoardCustomColumns('rfi', result.rfi ?? []);
  return result;
}

export function customColumnWidthKey(columnId: string): string {
  return `custom:${columnId}`;
}

export function defaultCustomColumnWidth(type: SheetColumnType): number {
  return type === 'duration' ? 220 : 160;
}

export const SHEET_COL_DRAG_PREFIX = 'col:';

export const FIXED_SHEET_COLUMN_IDS = [
  'title',
  'description',
  'status',
  'assignee',
  'due',
  'board',
] as const;

export type FixedSheetColumnId = (typeof FIXED_SHEET_COLUMN_IDS)[number];

export type BoardSheetColumnOrderMap = Partial<Record<ProjectBoardType, string[]>>;

export const FIXED_SHEET_COLUMN_LABELS: Record<FixedSheetColumnId, string> = {
  title: 'Title',
  description: 'Description',
  status: 'Status',
  assignee: 'Assignee',
  due: 'Due Date',
  board: 'Board',
};

export function sheetColDragId(columnId: string): string {
  return `${SHEET_COL_DRAG_PREFIX}${columnId}`;
}

export function parseSheetColDragId(id: string): string | null {
  if (!id.startsWith(SHEET_COL_DRAG_PREFIX)) return null;
  if (id.startsWith('col:section:')) return null;
  return id.slice(SHEET_COL_DRAG_PREFIX.length);
}

export function isFixedSheetColumnId(id: string): id is FixedSheetColumnId {
  return (FIXED_SHEET_COLUMN_IDS as readonly string[]).includes(id);
}

export function defaultBoardColumnOrder(
  customColumns: SheetColumnDefinition[],
  isOverview: boolean,
  boardType?: ProjectBoardType
): string[] {
  if (boardType && isFlatBoard(boardType)) {
    if (boardType === 'documents') {
      return ['title'];
    }
    if (boardType === 'rfi') {
      return ['title', 'status'];
    }
    const order: string[] = ['title'];
    if (boardShowsStatusColumn(boardType)) {
      order.push('status');
    }
    order.push('assignee', 'due');
    order.push(...customColumns.map((c) => c.id));
    return order;
  }
  const order: string[] = ['title', 'description', 'status', 'assignee'];
  if (boardType && boardUsesWorkflowDueDates(boardType)) {
    order.push(...WORKFLOW_DUE_DATE_COLUMN_IDS);
  } else {
    order.push('due');
  }
  order.push(
    ...customColumns
      .filter((column) => !WORKFLOW_DUE_DATE_COLUMN_IDS.includes(column.id))
      .map((column) => column.id)
  );
  if (isOverview) order.push('board');
  return order;
}

export function getBoardSheetColumnOrder(
  boardType: ProjectBoardType,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  boardSheetColumns: BoardSheetColumnsMap,
  isOverview: boolean
): string[] {
  const customColumns = getBoardSheetColumns(boardType, boardSheetColumns);
  const defaults = defaultBoardColumnOrder(customColumns, isOverview, boardType);
  const stored = boardSheetColumnOrder[boardType];
  if (!stored?.length) return applyBoardColumnRules(boardType, defaults);

  const customIds = new Set(customColumns.map((c) => c.id));
  const seen = new Set<string>();
  const result: string[] = [];

  for (const id of stored) {
    if (id === 'board' && !isOverview) continue;
    if ((isFixedSheetColumnId(id) && id !== 'board') || id === 'board' || customIds.has(id)) {
      if (!seen.has(id)) {
        result.push(id);
        seen.add(id);
      }
    }
  }

  for (const id of defaults) {
    if (!seen.has(id)) result.push(id);
  }

  return applyBoardColumnRules(boardType, result);
}

export function createDefaultBoardSheetColumnOrder(): BoardSheetColumnOrderMap {
  const map: BoardSheetColumnOrderMap = {};
  for (const board of BUILT_IN_BOARD_TYPES) {
    map[board] = defaultBoardColumnOrder(getBoardSheetColumns(board, {}), board === 'main', board);
  }
  return map;
}

export function normalizeBoardSheetColumnOrder(
  raw: BoardSheetColumnOrderMap | undefined | null,
  boardSheetColumns: BoardSheetColumnsMap
): BoardSheetColumnOrderMap {
  const result = createDefaultBoardSheetColumnOrder();
  if (!raw) return result;
  for (const [key, order] of Object.entries(raw)) {
    if (order?.length) {
      result[key as ProjectBoardType] = getBoardSheetColumnOrder(
        key as ProjectBoardType,
        { [key as ProjectBoardType]: order },
        boardSheetColumns,
        key === 'main'
      );
    }
  }
  return stripFlatBoardHiddenColumns(stripBoardColumnFromSubBoardOrders(result));
}

export type SheetColumnSlot =
  | { kind: 'fixed'; id: FixedSheetColumnId }
  | { kind: 'custom'; column: SheetColumnDefinition };

export function buildSheetColumnSlots(
  columnOrder: string[],
  customColumns: SheetColumnDefinition[],
  isOverview: boolean
): SheetColumnSlot[] {
  const customById = new Map(customColumns.map((c) => [c.id, c]));
  const slots: SheetColumnSlot[] = [];

  for (const id of columnOrder) {
    if (id === 'board' && !isOverview) continue;
    if (customById.has(id)) {
      slots.push({ kind: 'custom', column: customById.get(id)! });
    } else if (isFixedSheetColumnId(id) && (id !== 'board' || isOverview)) {
      slots.push({ kind: 'fixed', id });
    }
  }

  return slots;
}

/** Strip hidden columns from flat board persisted layouts. */
export function stripFlatBoardHiddenColumns(
  orderMap: BoardSheetColumnOrderMap
): BoardSheetColumnOrderMap {
  let next = orderMap;
  for (const board of ['documents', 'rfi'] as const) {
    const order = next[board];
    if (!order?.length) continue;
    const filtered = filterBoardColumnOrder(board, order);
    if (filtered.length !== order.length) {
      next = { ...next, [board]: filtered };
    }
  }
  return next;
}

/** @deprecated Use stripFlatBoardHiddenColumns */
export function stripDocumentsBoardHiddenColumns(
  orderMap: BoardSheetColumnOrderMap
): BoardSheetColumnOrderMap {
  return stripFlatBoardHiddenColumns(orderMap);
}

/** Sub-board tabs must not persist the Main Overview-only Board column in their layout. */
export function stripBoardColumnFromSubBoardOrders(
  orderMap: BoardSheetColumnOrderMap
): BoardSheetColumnOrderMap {
  const next: BoardSheetColumnOrderMap = { ...orderMap };
  for (const [key, order] of Object.entries(next)) {
    if (key === 'main' || !order?.length) continue;
    next[key as ProjectBoardType] = order.filter((id) => id !== 'board');
  }
  return next;
}
