import * as XLSX from 'xlsx';
import type { ProjectBoardType } from '../types';
import { PROJECT_BOARD_TYPES } from '../types';
import {
  mapMondayBoardToBoardroom,
  type MondayImportEnsuredGroup,
  type MondayImportItem,
  type MondayImportParseResult,
} from './mondayBoardImport';

/** Roles the user can assign to spreadsheet columns in the fail-safe mapper. */
export type ImportMapColumnRole =
  | 'skip'
  | 'board'
  | 'group'
  | 'subgroup'
  | 'task'
  | 'subtask'
  | 'parent'
  | 'status'
  | 'person'
  | 'dueDate'
  | 'notes';

export const IMPORT_MAP_COLUMN_ROLES: { id: ImportMapColumnRole; label: string }[] = [
  { id: 'skip', label: 'Ignore' },
  { id: 'board', label: 'Board' },
  { id: 'group', label: 'Group' },
  { id: 'subgroup', label: 'Sub-group' },
  { id: 'task', label: 'Main task' },
  { id: 'subtask', label: 'Sub-task' },
  { id: 'parent', label: 'Parent task' },
  { id: 'status', label: 'Status' },
  { id: 'person', label: 'Assignee' },
  { id: 'dueDate', label: 'Due date' },
  { id: 'notes', label: 'Notes' },
];

export interface ImportSheetPreview {
  fileName: string;
  sheetName: string;
  /** Suggested project name from file stem. */
  boardNameHint: string;
  headers: string[];
  /** Data rows (trimmed cells). */
  rows: string[][];
  /** First N rows for UI preview (same as rows slice). */
  previewRows: string[][];
}

export interface ImportColumnMapping {
  /** Parallel to headers — role for each column index. */
  roles: ImportMapColumnRole[];
  /**
   * When no Board column is mapped (or a cell is blank), send rows here.
   * null = skip rows that have no board.
   */
  defaultBoardType: ProjectBoardType | null;
  /** Carry forward Board/Group/Sub-group/Task when a cell is blank (outline-style sheets). */
  carryForward: boolean;
}

function cellText(value: unknown): string {
  if (value == null) return '';
  if (value instanceof Date) {
    const y = value.getFullYear();
    const m = String(value.getMonth() + 1).padStart(2, '0');
    const d = String(value.getDate()).padStart(2, '0');
    return `${y}-${m}-${d}`;
  }
  return String(value).replace(/\s+$/g, '');
}

function normalizeHeader(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/[_/]+/g, ' ')
    .replace(/\s+/g, ' ');
}

function rowsFromSheet(sheet: XLSX.WorkSheet): string[][] {
  const rows = XLSX.utils.sheet_to_json<(string | number | Date | null)[]>(sheet, {
    header: 1,
    defval: '',
    raw: false,
  });
  return rows.map((row) => (Array.isArray(row) ? row.map(cellText) : []));
}

function looksLikeHeaderRow(row: string[]): boolean {
  const filled = row.map((c) => c.trim()).filter(Boolean);
  if (filled.length < 1) return false;
  const joined = filled.map(normalizeHeader);
  return joined.some((h) =>
    [
      'name',
      'task',
      'tasks',
      'task name',
      'item',
      'item name',
      'title',
      'board',
      'group',
      'status',
      'assigned to',
      'primary column',
      'primary',
    ].includes(h)
  );
}

function parsePersonNames(raw: string): string[] {
  if (!raw.trim()) return [];
  return raw
    .split(/[,;|/]+/)
    .map((part) => part.trim())
    .filter(Boolean);
}

function parseDueDate(raw: string): string | null {
  const text = raw.trim();
  if (!text) return null;
  if (/^\d{4}-\d{2}-\d{2}/.test(text)) return text.slice(0, 10);
  const slash = text.match(/^(\d{1,2})[\/\-.](\d{1,2})[\/\-.](\d{2,4})$/);
  if (slash) {
    const a = Number(slash[1]);
    const b = Number(slash[2]);
    let year = Number(slash[3]);
    if (year < 100) year += 2000;
    const month = a > 12 ? b : a;
    const day = a > 12 ? a : b;
    if (month >= 1 && month <= 12 && day >= 1 && day <= 31) {
      return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
    }
  }
  const parsed = Date.parse(text);
  if (!Number.isNaN(parsed)) {
    const d = new Date(parsed);
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }
  return null;
}

function guessRoleForHeader(header: string): ImportMapColumnRole {
  const h = normalizeHeader(header);
  if (!h) return 'skip';
  if (
    h === 'name' ||
    h === 'item' ||
    h === 'item name' ||
    h === 'task' ||
    h === 'task name' ||
    h === 'tasks' ||
    h === 'title' ||
    h === 'primary column' ||
    h === 'primary' ||
    h === 'main task' ||
    h === 'main tasks'
  ) {
    return 'task';
  }
  if (
    h === 'sub task' ||
    h === 'subtask' ||
    h === 'sub-tasks' ||
    h === 'sub tasks' ||
    h === 'child task' ||
    h === 'child'
  ) {
    return 'subtask';
  }
  if (
    h === 'sub group' ||
    h === 'subgroup' ||
    h === 'sub-group' ||
    h === 'sub groups' ||
    h === 'container' ||
    h === 'category'
  ) {
    return 'subgroup';
  }
  if (h === 'board' || h === 'board name' || h === 'board type') return 'board';
  if (h === 'group' || h === 'group title' || h === 'section') return 'group';
  if (
    h === 'parent' ||
    h === 'parent item' ||
    h === 'parent name' ||
    h === 'parent task' ||
    h === 'parent row'
  ) {
    return 'parent';
  }
  if (h === 'status' || h.endsWith(' status')) return 'status';
  if (
    h === 'person' ||
    h === 'people' ||
    h === 'owner' ||
    h === 'assignee' ||
    h === 'assignees' ||
    h === 'assigned to'
  ) {
    return 'person';
  }
  if (h === 'due date' || h === 'deadline' || h.startsWith('due ')) return 'dueDate';
  if (h === 'date' || h === 'timeline') return 'dueDate';
  if (h === 'notes' || h === 'description' || h === 'update' || h === 'updates') return 'notes';
  return 'skip';
}

/** Read the first usable sheet into a flat header + rows preview for manual mapping. */
export function readImportSheetPreview(
  buffer: ArrayBuffer,
  fileName?: string
): ImportSheetPreview {
  const workbook = XLSX.read(buffer, { type: 'array', cellDates: true });
  if (!workbook.SheetNames.length) {
    throw new Error('File has no sheets.');
  }
  const sheetName =
    workbook.SheetNames.find((name) => !/update/i.test(name)) ?? workbook.SheetNames[0]!;
  const sheet = workbook.Sheets[sheetName];
  if (!sheet) throw new Error('Could not read sheet.');

  const allRows = rowsFromSheet(sheet);
  let headerIdx = allRows.findIndex((row) => looksLikeHeaderRow(row));
  if (headerIdx < 0) {
    headerIdx = allRows.findIndex((row) => row.some((c) => c.trim()));
  }
  if (headerIdx < 0) {
    throw new Error('No header row found in that file.');
  }

  const headers = (allRows[headerIdx] ?? []).map((h) => h.trim());
  let width = headers.length;
  for (let r = headerIdx + 1; r < allRows.length; r++) {
    width = Math.max(width, allRows[r]?.length ?? 0);
  }
  while (headers.length < width) headers.push(`Column ${headers.length + 1}`);

  const rows: string[][] = [];
  for (let r = headerIdx + 1; r < allRows.length; r++) {
    const raw = allRows[r] ?? [];
    if (raw.every((c) => !c.trim())) continue;
    if (
      looksLikeHeaderRow(raw) &&
      raw.some((c) => {
        const n = normalizeHeader(c);
        return n === 'name' || n === 'tasks';
      })
    ) {
      continue;
    }
    const cells = headers.map((_, i) => (raw[i] ?? '').trim());
    if (cells.every((c) => !c)) continue;
    rows.push(cells);
  }

  const stem = (fileName ?? '').replace(/\.[^.]+$/, '').trim();
  const fromFile = stem
    .replace(/[_-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .replace(/\d{8,}$/, '')
    .trim();

  return {
    fileName: fileName ?? '',
    sheetName,
    boardNameHint: fromFile,
    headers,
    rows,
    previewRows: rows.slice(0, 20),
  };
}

export function guessImportColumnMapping(preview: ImportSheetPreview): ImportColumnMapping {
  const roles = preview.headers.map(guessRoleForHeader);
  if (!roles.includes('task') && !roles.includes('subtask')) {
    const first = preview.headers.findIndex((h) => h.trim());
    if (first >= 0) roles[first] = 'task';
  }
  return {
    roles,
    defaultBoardType: 'detailers',
    carryForward: true,
  };
}

function boardLabelForType(boardType: ProjectBoardType): string {
  return PROJECT_BOARD_TYPES.find((b) => b.id === boardType)?.label ?? boardType;
}

function ensuredGroupsFromItems(items: MondayImportItem[]): MondayImportEnsuredGroup[] {
  const out: MondayImportEnsuredGroup[] = [];
  const seen = new Set<string>();
  for (const item of items) {
    const key = `${item.boardType}\0${item.groupName}`;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push({ boardType: item.boardType, name: item.groupName });
  }
  return out;
}

/**
 * Build import items from a flat sheet using the user's column → hierarchy mapping.
 *
 * Hierarchy (Boardroom):
 *   Board → Group → (Sub-group as parent task) → Main task → Sub-task
 */
export function applyImportColumnMapping(
  preview: ImportSheetPreview,
  mapping: ImportColumnMapping
): MondayImportParseResult {
  const warnings: string[] = [];
  const { roles, defaultBoardType, carryForward } = mapping;

  const col = (role: ImportMapColumnRole) => roles.indexOf(role);
  const boardCol = col('board');
  const groupCol = col('group');
  const subgroupCol = col('subgroup');
  const taskCol = col('task');
  const subtaskCol = col('subtask');
  const parentCol = col('parent');
  const statusCol = col('status');
  const personCol = col('person');
  const dueDateCol = col('dueDate');
  const notesCol = col('notes');

  if (taskCol < 0 && subtaskCol < 0) {
    return {
      boardNameHint: preview.boardNameHint,
      items: [],
      groupNames: [],
      ensuredGroups: [],
      importedBoardNames: [],
      skippedBoardNames: [],
      warnings: ['Map at least one column to Main task or Sub-task.'],
    };
  }

  const items: MondayImportItem[] = [];
  const importedBoardNames: string[] = [];
  const skippedBoardNames: string[] = [];
  const seenImport = new Set<string>();
  const seenSkip = new Set<string>();
  const emittedKeys = new Set<string>();

  let lastBoard = '';
  let lastGroup = '';
  let lastSubgroup = '';
  let lastTask = '';

  const pushItem = (partial: {
    title: string;
    boardType: ProjectBoardType;
    sourceBoardName: string;
    groupName: string;
    parentTitle: string | null;
    statusLabel: string;
    personNames: string[];
    dueDate: string | null;
    description: string;
  }) => {
    const key = `${partial.boardType}\0${partial.groupName}\0${partial.parentTitle ?? ''}\0${partial.title}`;
    if (emittedKeys.has(key)) return;
    emittedKeys.add(key);
    items.push({
      title: partial.title,
      boardType: partial.boardType,
      sourceBoardName: partial.sourceBoardName,
      groupName: partial.groupName,
      statusLabel: partial.statusLabel,
      personNames: partial.personNames,
      dueDate: partial.dueDate,
      description: partial.description,
      parentTitle: partial.parentTitle,
      extraFields: {},
    });
  };

  for (const row of preview.rows) {
    let boardLabel = boardCol >= 0 ? (row[boardCol] ?? '').trim() : '';
    let groupName = groupCol >= 0 ? (row[groupCol] ?? '').trim() : '';
    let subgroup = subgroupCol >= 0 ? (row[subgroupCol] ?? '').trim() : '';
    let taskName = taskCol >= 0 ? (row[taskCol] ?? '').trim() : '';
    const subtaskName = subtaskCol >= 0 ? (row[subtaskCol] ?? '').trim() : '';
    const parentFromCol = parentCol >= 0 ? (row[parentCol] ?? '').trim() : '';

    if (carryForward) {
      if (boardLabel) lastBoard = boardLabel;
      else boardLabel = lastBoard;
      if (groupName) lastGroup = groupName;
      else groupName = lastGroup;
      if (subgroup) lastSubgroup = subgroup;
      else if (subgroupCol >= 0) subgroup = lastSubgroup;
      if (taskName) lastTask = taskName;
      else if (taskCol >= 0 && subtaskName) taskName = lastTask;
    }

    if (!taskName && !subtaskName && !subgroup) continue;

    let sourceBoardName = boardLabel;
    let boardType: ProjectBoardType | null = boardLabel
      ? mapMondayBoardToBoardroom(boardLabel)
      : null;
    if (!boardType && defaultBoardType) {
      boardType = defaultBoardType;
      sourceBoardName = sourceBoardName || boardLabelForType(defaultBoardType);
    }
    if (!boardType) {
      const skipLabel = boardLabel || '(no board)';
      if (!seenSkip.has(skipLabel)) {
        seenSkip.add(skipLabel);
        skippedBoardNames.push(skipLabel);
        warnings.push(
          `Skipped rows for "${skipLabel}" — map a Board column or pick a default board.`
        );
      }
      continue;
    }

    if (!seenImport.has(sourceBoardName)) {
      seenImport.add(sourceBoardName);
      importedBoardNames.push(sourceBoardName);
    }

    const resolvedGroup = groupName || sourceBoardName;
    const statusLabel = statusCol >= 0 ? (row[statusCol] ?? '').trim() : '';
    const personNames = personCol >= 0 ? parsePersonNames(row[personCol] ?? '') : [];
    const dueDate = dueDateCol >= 0 ? parseDueDate(row[dueDateCol] ?? '') : null;
    const description = notesCol >= 0 ? (row[notesCol] ?? '').trim() : '';

    // Sub-group → parent task under the group (Boardroom has no nested groups).
    if (subgroup) {
      pushItem({
        title: subgroup,
        boardType,
        sourceBoardName,
        groupName: resolvedGroup,
        parentTitle: null,
        statusLabel: '',
        personNames: [],
        dueDate: null,
        description: '',
      });
    }

    if (taskName) {
      const parentTitle =
        parentFromCol || (subgroup && taskName !== subgroup ? subgroup : null);
      pushItem({
        title: taskName,
        boardType,
        sourceBoardName,
        groupName: resolvedGroup,
        parentTitle: parentTitle && parentTitle !== taskName ? parentTitle : null,
        statusLabel,
        personNames,
        dueDate,
        description,
      });
      if (carryForward) lastTask = taskName;
    }

    if (subtaskName) {
      const parentTitle =
        parentFromCol ||
        (taskName && taskName !== subtaskName ? taskName : null) ||
        (subgroup && subgroup !== subtaskName ? subgroup : null);
      pushItem({
        title: subtaskName,
        boardType,
        sourceBoardName,
        groupName: resolvedGroup,
        parentTitle,
        statusLabel,
        personNames,
        dueDate,
        description,
      });
    }
  }

  const ensuredGroups = ensuredGroupsFromItems(items);
  const groupNames = [...new Set(items.map((i) => i.groupName))];

  if (items.length === 0) {
    warnings.push('No importable rows with the current column mapping.');
  }

  return {
    boardNameHint: preview.boardNameHint,
    items,
    groupNames,
    ensuredGroups,
    importedBoardNames,
    skippedBoardNames,
    warnings,
  };
}

/** Boards the user can pick as the default when Board column is missing. */
export const IMPORT_DEFAULT_BOARD_OPTIONS: { id: ProjectBoardType; label: string }[] =
  PROJECT_BOARD_TYPES.filter((b) => b.id !== 'main');
