import type { CSSProperties } from 'react';
import type { Task } from '../types';
import { sheetRowPaddingLeft, type SheetRow } from './groupRows';

export type SheetFixedColumnKey =
  | 'row'
  | 'collapse'
  | 'drag'
  | 'title'
  | 'project'
  | 'description'
  | 'status'
  | 'assignee'
  | 'due'
  | 'board'
  | 'actions';

export const SHEET_COLUMN_WIDTHS_KEY = 'bim-spreadsheet-column-widths-v7';

const LEGACY_SHEET_COLUMN_WIDTHS_KEYS = [
  'bim-spreadsheet-column-widths-v6',
  'bim-spreadsheet-column-widths-v5',
  'bim-spreadsheet-column-widths-v4',
  'bim-spreadsheet-column-widths-v3',
  'bim-spreadsheet-column-widths-v2',
  'bim-spreadsheet-column-widths-v1',
];
export const SHEET_COLUMN_RESIZED_KEY = 'bim-spreadsheet-column-resized-v3';
export const SHEET_COLUMN_LOCKED_KEY = 'bim-spreadsheet-column-locked-v1';

/** Starting widths tuned to typical cell content, not arbitrary large values. */
export const SHEET_DEFAULT_WIDTHS: Record<SheetFixedColumnKey, number> = {
  row: 56,
  collapse: 26,
  drag: 28,
  title: 220,
  project: 220,
  description: 280,
  status: 106,
  assignee: 108,
  due: 126,
  board: 104,
  actions: 82,
};

export const SHEET_MIN_WIDTHS: Record<SheetFixedColumnKey, number> = {
  row: 48,
  collapse: 24,
  drag: 28,
  title: 80,
  project: 120,
  description: 100,
  status: 86,
  assignee: 86,
  due: 100,
  board: 80,
  actions: 72,
};

type PerBoardColumnWidths = Record<string, Record<string, number>>;
type PerBoardResizedColumns = Record<string, string[]>;
type PerBoardLockedColumns = Record<string, string[]>;

export function normalizeSheetColumnWidths(
  widths: Record<string, number>
): Record<string, number> {
  const next = { ...widths };
  const rowWidth = next.row ?? SHEET_DEFAULT_WIDTHS.row;
  if (rowWidth < SHEET_MIN_WIDTHS.row) {
    next.row = SHEET_MIN_WIDTHS.row;
  }
  const collapseWidth = next.collapse ?? SHEET_DEFAULT_WIDTHS.collapse;
  if (collapseWidth < SHEET_MIN_WIDTHS.collapse) {
    next.collapse = SHEET_MIN_WIDTHS.collapse;
  }
  if (collapseWidth > 36) {
    next.collapse = SHEET_DEFAULT_WIDTHS.collapse;
  }
  const dragWidth = next.drag ?? SHEET_DEFAULT_WIDTHS.drag;
  if (dragWidth < SHEET_MIN_WIDTHS.drag) {
    next.drag = SHEET_MIN_WIDTHS.drag;
  }
  return next;
}

function readPerBoardColumnWidths(): PerBoardColumnWidths {
  try {
    const saved = localStorage.getItem(SHEET_COLUMN_WIDTHS_KEY);
    if (saved) return JSON.parse(saved) as PerBoardColumnWidths;
  } catch {
    /* ignore */
  }

  for (const legacyKey of LEGACY_SHEET_COLUMN_WIDTHS_KEYS) {
    try {
      const legacySaved = localStorage.getItem(legacyKey);
      if (!legacySaved) continue;
      const global = normalizeSheetColumnWidths({
        ...SHEET_DEFAULT_WIDTHS,
        ...JSON.parse(legacySaved),
      });
      return { __legacy_global__: global };
    } catch {
      /* ignore */
    }
  }

  return {};
}

function readPerBoardResizedColumns(): PerBoardResizedColumns {
  try {
    const saved = localStorage.getItem(SHEET_COLUMN_RESIZED_KEY);
    if (saved) {
      const parsed = JSON.parse(saved) as PerBoardResizedColumns | string[];
      if (Array.isArray(parsed)) {
        return { __legacy_global__: parsed };
      }
      return parsed;
    }
  } catch {
    /* ignore */
  }
  try {
    const legacy = localStorage.getItem('bim-spreadsheet-column-resized-v2');
    if (legacy) {
      return { __legacy_global__: JSON.parse(legacy) as string[] };
    }
  } catch {
    /* ignore */
  }
  return {};
}

function resolveBoardLayout<T>(
  map: Record<string, T>,
  boardType: string,
  fallback: T
): T {
  if (map[boardType]) return map[boardType];
  if (map.__legacy_global__) return map.__legacy_global__;
  return fallback;
}

export function loadSheetColumnWidths(boardType: string): Record<string, number> {
  const all = readPerBoardColumnWidths();
  const stored = resolveBoardLayout(all, boardType, {});
  return normalizeSheetColumnWidths({
    ...SHEET_DEFAULT_WIDTHS,
    ...stored,
  });
}

export function saveSheetColumnWidths(
  boardType: string,
  widths: Record<string, number>
): void {
  const all = readPerBoardColumnWidths();
  delete all.__legacy_global__;
  all[boardType] = widths;
  localStorage.setItem(SHEET_COLUMN_WIDTHS_KEY, JSON.stringify(all));
}

export function loadUserResizedColumns(boardType: string): Set<string> {
  const all = readPerBoardResizedColumns();
  const stored = resolveBoardLayout(all, boardType, [] as string[]);
  return new Set(stored);
}

export function saveUserResizedColumns(boardType: string, columns: Set<string>): void {
  const all = readPerBoardResizedColumns();
  delete all.__legacy_global__;
  all[boardType] = [...columns];
  localStorage.setItem(SHEET_COLUMN_RESIZED_KEY, JSON.stringify(all));
}

function readPerBoardLockedColumns(): PerBoardLockedColumns {
  try {
    const saved = localStorage.getItem(SHEET_COLUMN_LOCKED_KEY);
    if (saved) return JSON.parse(saved) as PerBoardLockedColumns;
  } catch {
    /* ignore */
  }
  return {};
}

export function loadLockedColumns(boardType: string): Set<string> {
  const all = readPerBoardLockedColumns();
  const stored = resolveBoardLayout(all, boardType, [] as string[]);
  return new Set(stored);
}

export function saveLockedColumns(boardType: string, columns: Set<string>): void {
  const all = readPerBoardLockedColumns();
  delete all.__legacy_global__;
  all[boardType] = [...columns];
  localStorage.setItem(SHEET_COLUMN_LOCKED_KEY, JSON.stringify(all));
}

export function overviewSectionSizingKey(sectionBoardType: string): string {
  return `main:section:${sectionBoardType}`;
}

export interface OverviewSectionSizingState {
  columnWidths: Record<string, number>;
  userResizedColumns: Set<string>;
  lockedColumns: Set<string>;
}

export function loadOverviewSectionSizing(
  sectionBoardType: string
): OverviewSectionSizingState {
  const sectionKey = overviewSectionSizingKey(sectionBoardType);
  const allWidths = readPerBoardColumnWidths();
  const hasSectionWidths =
    Boolean(allWidths[sectionKey]) && Object.keys(allWidths[sectionKey]!).length > 0;
  return {
    columnWidths: hasSectionWidths
      ? loadSheetColumnWidths(sectionKey)
      : loadSheetColumnWidths(sectionBoardType),
    userResizedColumns: loadUserResizedColumns(sectionKey),
    lockedColumns: loadLockedColumns(sectionKey),
  };
}

export function saveOverviewSectionSizing(
  sectionBoardType: string,
  state: OverviewSectionSizingState
): void {
  const sectionKey = overviewSectionSizingKey(sectionBoardType);
  saveSheetColumnWidths(sectionKey, state.columnWidths);
  saveUserResizedColumns(sectionKey, state.userResizedColumns);
  saveLockedColumns(sectionKey, state.lockedColumns);
}

/** Compact columns hug their content; title/description take remaining space. */
export const SHEET_CONTENT_SIZED_COLUMNS = new Set<SheetFixedColumnKey>([
  'status',
  'assignee',
  'due',
  'board',
  'actions',
]);

const CELL_HORIZONTAL_PAD = 24;
const HEADER_EXTRA = 40;
const DATE_INPUT_WIDTH = 132;
const DURATION_INPUT_WIDTH = 220;
const PROGRESS_BAR_WIDTH = 128;
const PROGRESS_LABEL_WIDTH = 34;

export interface AutoFitMeasureContext {
  sheetRows: SheetRow[];
  statusLabels: string[];
  branchLabels: string[];
  branchLabelByBoard: Record<string, string>;
  tasks: Task[];
  headerLabel?: string;
}

export function isContentSizedColumn(key: string): key is SheetFixedColumnKey {
  return SHEET_CONTENT_SIZED_COLUMNS.has(key as SheetFixedColumnKey);
}

export function sheetColumnHeaderStyle(
  key: string,
  width: number,
  userResized: boolean
): CSSProperties {
  if (!userResized && isContentSizedColumn(key)) {
    return { minWidth: width, width };
  }
  if (!userResized && (key === 'title' || key === 'description')) {
    return { minWidth: width };
  }
  return { width, minWidth: width, maxWidth: width };
}

export function sheetColStyle(key: string, width: number, userResized: boolean): CSSProperties {
  if (!userResized && isContentSizedColumn(key)) {
    return { width: '1%', minWidth: width };
  }
  if (!userResized && (key === 'title' || key === 'description')) {
    return { minWidth: width };
  }
  return { width, minWidth: width };
}

let measureCtx: CanvasRenderingContext2D | null = null;

function getMeasureContext(): CanvasRenderingContext2D {
  if (!measureCtx) {
    const canvas = document.createElement('canvas');
    measureCtx = canvas.getContext('2d');
    if (!measureCtx) throw new Error('Canvas 2D unavailable');
  }
  return measureCtx;
}

function textWidth(text: string, font: string): number {
  const ctx = getMeasureContext();
  ctx.font = font;
  return ctx.measureText(text).width;
}

const HEADER_FONT = '600 11px Inter, system-ui, sans-serif';
const GROUP_FONT = '600 13px Inter, system-ui, sans-serif';
const CELL_FONT = '13px Inter, system-ui, sans-serif';
const BUTTON_FONT = '600 12px Inter, system-ui, sans-serif';

function headerWidth(label: string, extra = HEADER_EXTRA): number {
  return textWidth(label.toUpperCase(), HEADER_FONT) + extra;
}

function measureTitleColumn(rows: SheetRow[], headerLabel: string): number {
  let max = headerWidth(headerLabel);
  for (const row of rows) {
    const indent = sheetRowPaddingLeft(row.depth);
    if (row.type === 'group') {
      max = Math.max(
        max,
        textWidth(row.group.name, GROUP_FONT) + indent + CELL_HORIZONTAL_PAD
      );
    } else {
      const badge = row.task.parentTaskId ? 36 : 0;
      max = Math.max(
        max,
        textWidth(row.task.title, CELL_FONT) + indent + badge + CELL_HORIZONTAL_PAD
      );
    }
  }
  return max;
}

function measureDescriptionColumn(rows: SheetRow[], headerLabel: string): number {
  let max = headerWidth(headerLabel);
  for (const row of rows) {
    if (row.type !== 'task') continue;
    const description = row.task.description?.trim();
    if (!description) continue;
    max = Math.max(max, textWidth(description, CELL_FONT) + CELL_HORIZONTAL_PAD);
  }
  return max;
}

function measureStatusColumn(rows: SheetRow[], statusLabels: string[], headerLabel: string): number {
  let max = headerWidth(headerLabel, HEADER_EXTRA + 22);
  for (const label of statusLabels) {
    max = Math.max(max, textWidth(label, CELL_FONT) + 44);
  }
  if (rows.some((row) => row.type === 'group')) {
    max = Math.max(max, PROGRESS_BAR_WIDTH + PROGRESS_LABEL_WIDTH + CELL_HORIZONTAL_PAD);
  }
  return max;
}

function measureAssigneeColumn(tasks: Task[], headerLabel: string): number {
  let max = headerWidth(headerLabel);
  for (const task of tasks) {
    const count = task.assigneeIds?.length ?? 0;
    if (count === 0) continue;
    max = Math.max(max, count * 28 + Math.max(0, count - 1) * 4 + CELL_HORIZONTAL_PAD);
  }
  return max;
}

function measureBranchColumn(
  rows: SheetRow[],
  branchLabels: string[],
  branchLabelByBoard: Record<string, string>,
  headerLabel: string
): number {
  let max = headerWidth(headerLabel);
  for (const label of branchLabels) {
    max = Math.max(max, textWidth(label, CELL_FONT) + 44);
  }
  for (const row of rows) {
    if (row.type !== 'task') continue;
    const label = branchLabelByBoard[row.task.boardType ?? ''] ?? '';
    if (label) max = Math.max(max, textWidth(label, CELL_FONT) + 44);
  }
  return max;
}

function measureActionsColumn(headerLabel: string): number {
  return Math.max(headerWidth(headerLabel || '+', 28), textWidth('+ New', BUTTON_FONT) + 36);
}

function measureElementNaturalWidth(el: HTMLElement): number {
  const clone = el.cloneNode(true) as HTMLElement;
  const box = document.createElement('div');
  box.style.cssText =
    'position:fixed;left:-10000px;top:0;visibility:hidden;width:max-content;max-width:none;overflow:visible;white-space:nowrap;';

  const stripConstraints = (node: HTMLElement) => {
    node.style.width = 'auto';
    node.style.minWidth = '0';
    node.style.maxWidth = 'none';
    node.style.flex = '0 0 auto';
    node.style.overflow = 'visible';
    node.style.textOverflow = 'clip';
    node.style.whiteSpace = 'nowrap';
    Array.from(node.children).forEach((child) => stripConstraints(child as HTMLElement));
  };

  stripConstraints(clone);
  box.appendChild(clone);
  document.body.appendChild(box);
  const width = box.getBoundingClientRect().width;
  document.body.removeChild(box);
  return width;
}

function measureCellNaturalWidth(cell: HTMLElement): number {
  const thContent = cell.querySelector('.thContent');
  if (thContent) return measureElementNaturalWidth(thContent as HTMLElement);

  const marked = cell.querySelector('[data-col-measure]');
  if (marked) return measureElementNaturalWidth(marked as HTMLElement);

  const select = cell.querySelector('select');
  if (select instanceof HTMLSelectElement) {
    let max = 0;
    for (let i = 0; i < select.options.length; i++) {
      max = Math.max(max, textWidth(select.options[i].text, CELL_FONT));
    }
    return max + 44;
  }

  const dateInput = cell.querySelector('input[type="date"]');
  if (dateInput) return DATE_INPUT_WIDTH;

  const durationCell = cell.querySelector('.durationCell');
  if (durationCell) return DURATION_INPUT_WIDTH;

  const textInput = cell.querySelector('input:not([type="checkbox"])');
  if (textInput instanceof HTMLInputElement) {
    let width = textWidth(textInput.value || textInput.placeholder || '—', CELL_FONT) + CELL_HORIZONTAL_PAD;
    const padded = textInput.closest('[style*="padding"]') as HTMLElement | null;
    if (padded) {
      width += Number.parseFloat(getComputedStyle(padded).paddingLeft) || 0;
    }
    if (cell.querySelector('.subtaskBadge')) width += 36;
    return width;
  }

  const groupName = cell.querySelector('.groupNameInput, .groupName');
  if (groupName instanceof HTMLInputElement) {
    return textWidth(groupName.value, GROUP_FONT) + CELL_HORIZONTAL_PAD;
  }
  if (groupName instanceof HTMLElement) {
    return textWidth(groupName.textContent ?? '', GROUP_FONT) + CELL_HORIZONTAL_PAD;
  }

  return measureElementNaturalWidth(cell);
}

function measureColumnContentWidthDOM(table: HTMLTableElement, columnIndex: number): number {
  let max = 0;
  table.querySelectorAll('tr').forEach((row) => {
    const cell = row.children.item(columnIndex - 1);
    if (!(cell instanceof HTMLTableCellElement)) return;
    if (cell.colSpan > 1) return;
    max = Math.max(max, measureCellNaturalWidth(cell));
  });
  return max;
}

/** Excel-style auto-fit: measure from source data first, then unconstrained DOM. */
export function autoFitColumnWidth(
  columnKey: string,
  table: HTMLTableElement,
  columnIndex: number,
  ctx: AutoFitMeasureContext
): number {
  const headerLabel = ctx.headerLabel ?? '';

  let measured = 0;
  switch (columnKey) {
    case 'title':
      measured = measureTitleColumn(ctx.sheetRows, headerLabel || 'Title');
      break;
    case 'description':
      measured = measureDescriptionColumn(ctx.sheetRows, headerLabel || 'Description');
      break;
    case 'status':
      measured = measureStatusColumn(ctx.sheetRows, ctx.statusLabels, headerLabel || 'Status');
      break;
    case 'assignee':
      measured = measureAssigneeColumn(ctx.tasks, headerLabel || 'Assignee');
      break;
    case 'due':
      measured = Math.max(headerWidth(headerLabel || 'Due Date'), DATE_INPUT_WIDTH);
      break;
    case 'board':
      measured = measureBranchColumn(
        ctx.sheetRows,
        ctx.branchLabels,
        ctx.branchLabelByBoard,
        headerLabel || 'Board'
      );
      break;
    case 'actions':
      measured = measureActionsColumn(headerLabel);
      break;
    case 'row':
      measured = 56;
      break;
    case 'collapse':
      measured = 26;
      break;
    case 'drag':
      measured = 28;
      break;
    default:
      measured = measureColumnContentWidthDOM(table, columnIndex);
      break;
  }

  const domMeasured = measureColumnContentWidthDOM(table, columnIndex);
  return Math.max(measured, domMeasured) + 8;
}
