import * as XLSX from 'xlsx';
import type { ProjectBoardType } from '../types';
import { PROJECT_BOARD_TYPES } from '../types';

/** One Monday item (or subitem) ready to become a Boardroom task. */
export interface MondayImportItem {
  title: string;
  /** Boardroom board this item maps onto (built-in only). */
  boardType: ProjectBoardType;
  /** Source Monday board / section label (for warnings & custom fields). */
  sourceBoardName: string;
  groupName: string;
  statusLabel: string;
  personNames: string[];
  dueDate: string | null;
  description: string;
  /** Parent item title when this row is a nested subitem. */
  parentTitle: string | null;
  extraFields: Record<string, string>;
}

export interface MondayImportEnsuredGroup {
  boardType: ProjectBoardType;
  name: string;
}

export interface MondayImportParseResult {
  boardNameHint: string;
  items: MondayImportItem[];
  groupNames: string[];
  /** Groups to create even when empty (e.g. Mechanical Duct Spooling). */
  ensuredGroups: MondayImportEnsuredGroup[];
  importedBoardNames: string[];
  skippedBoardNames: string[];
  warnings: string[];
}

function cellText(value: unknown): string {
  if (value == null) return '';
  if (value instanceof Date) {
    const y = value.getFullYear();
    const m = String(value.getMonth() + 1).padStart(2, '0');
    const d = String(value.getDate()).padStart(2, '0');
    return `${y}-${m}-${d}`;
  }
  // Keep leading whitespace — Smartsheet Excel uses indent for hierarchy.
  return String(value).replace(/\s+$/g, '');
}

function normalizeHeader(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/[_/]+/g, ' ')
    .replace(/\s+/g, ' ');
}

type ColumnRole =
  | 'name'
  | 'group'
  | 'board'
  | 'status'
  | 'person'
  | 'date'
  | 'dueDate'
  | 'subitems'
  | 'parent'
  | 'notes'
  | 'skip';

function classifyHeader(header: string): ColumnRole {
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
    h === 'primary'
  ) {
    return 'name';
  }
  if (h === 'board' || h === 'board name' || h === 'board type') return 'board';
  if (h === 'group' || h === 'group title' || h === 'section') return 'group';
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
  if (h === 'date' || h === 'timeline') return 'date';
  if (h === 'subitems' || h === 'subitem' || h === 'sub items') return 'subitems';
  if (
    h === 'parent' ||
    h === 'parent item' ||
    h === 'parent name' ||
    h === 'parent task' ||
    h === 'parent row'
  ) {
    return 'parent';
  }
  if (h === 'notes' || h === 'description' || h === 'update' || h === 'updates') return 'notes';
  return 'skip';
}

function parsePersonNames(raw: string): string[] {
  if (!raw.trim()) return [];
  return raw
    .split(/[,;|/]+/)
    .map((part) => part.trim())
    .filter(Boolean);
}

/** Monday Subitems cells list child titles separated by commas. */
function parseSubitemNames(raw: string): string[] {
  if (!raw.trim()) return [];
  return raw
    .split(',')
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

/**
 * Map a Monday status label onto a Boardroom status id.
 * Unknown labels stay as not-started; the original label is kept in customFields.
 */
export function mapMondayStatusToBoardroom(statusLabel: string): string {
  const s = statusLabel.trim().toLowerCase();
  if (!s) return 'not-started';
  // Prefer exact-ish phrases before generic "complete" substring matches
  if (/^(not started|new|todo|to do|open)$/.test(s)) return 'not-started';
  if (/(stuck|blocked|hold|on hold|waiting)/.test(s)) return 'on-hold';
  if (/(rework|revise|revision)/.test(s)) return 'rework';
  if (/(ready.?for.?spool)/.test(s)) return 'ready-for-spooling';
  if (/(ready.?for.?coord)/.test(s)) return 'ready-for-coordination';
  if (/(pre-?planning)/.test(s)) return 'not-started';
  if (/(done|completed|finished|closed|approved)|^complete$/.test(s)) return 'complete';
  if (/(review|qa|checking|coordination)/.test(s)) return 'coordinating';
  if (/(progress|working|doing|started|active|modeling)/.test(s)) return 'modeling';
  if (/(not started|new|todo|to do|open)/.test(s)) return 'not-started';
  return 'not-started';
}

/**
 * Map a Monday board / section title onto a Boardroom board type.
 * Returns null when the board is not part of the template (caller should skip).
 */
export function mapMondayBoardToBoardroom(boardName: string): ProjectBoardType | null {
  const s = boardName.trim().toLowerCase();
  if (!s) return null;

  // Explicit / common Monday labels from job exports
  if (/project setup|project management|project managers?\b|^pm\b/.test(s)) {
    return 'project-managers';
  }
  if (/\brfi\b/.test(s)) return 'rfi';
  if (/\bdocuments?\b/.test(s)) return 'documents';
  if (
    /detailers?|modeling|coordination|model.?coord/.test(s) &&
    !/deliverable/.test(s)
  ) {
    return 'detailers';
  }
  if (/deliverable/.test(s)) return 'deliverables';
  if (/spool/.test(s)) return 'spooling';
  if (/fabricat|\bfab\b/.test(s)) return 'fab';
  if (/shipping|ship\b/.test(s)) return 'shipping';
  if (/\bfield\b|install/.test(s)) return 'field';

  // Exact label match against Boardroom board names
  for (const board of PROJECT_BOARD_TYPES) {
    if (board.id === 'main') continue;
    if (s === board.label.toLowerCase() || s === board.id.replace(/-/g, ' ')) {
      return board.id;
    }
  }

  return null;
}

function rowsFromSheet(sheet: XLSX.WorkSheet): string[][] {
  const rows = XLSX.utils.sheet_to_json<(string | number | Date | null)[]>(sheet, {
    header: 1,
    defval: '',
    raw: false,
  });
  return rows.map((row) => (Array.isArray(row) ? row.map(cellText) : []));
}

function filledCells(row: string[]): string[] {
  return row.map((c) => c.trim()).filter(Boolean);
}

function isNameHeaderRow(row: string[]): boolean {
  return row.some((cell) => classifyHeader(cell) === 'name');
}

function isBannerRow(row: string[]): boolean {
  const filled = filledCells(row);
  return filled.length === 1 && !isNameHeaderRow(row);
}

interface RawRow {
  title: string;
  groupName: string;
  statusLabel: string;
  personNames: string[];
  dueDate: string | null;
  description: string;
  parentFromCol: string | null;
  subitemNames: string[];
  extraFields: Record<string, string>;
}

/** Leading whitespace / tabs before trim — Smartsheet Excel hierarchy. */
function leadingIndentLevel(raw: string): number {
  const match = raw.match(/^[\t \u00a0]*/);
  if (!match) return 0;
  return match[0]!.replace(/\t/g, '  ').replace(/\u00a0/g, ' ').length;
}

function parseBoardSectionRows(
  rows: string[][],
  headerIdx: number,
  endIdx: number,
  defaultGroupName: string,
  boardType: ProjectBoardType | null,
  options?: { honorGroupColumn?: boolean; useIndentParents?: boolean }
): RawRow[] {
  const honorGroupColumn = options?.honorGroupColumn ?? true;
  const useIndentParents = options?.useIndentParents ?? false;
  const headers = rows[headerIdx] ?? [];
  const roles = headers.map(classifyHeader);
  const nameIdx = roles.indexOf('name');
  const titleCol = nameIdx >= 0 ? nameIdx : 0;
  const groupCol = roles.indexOf('group');
  const statusCol = roles.indexOf('status');
  const personCol = roles.indexOf('person');
  const dueDateCol = roles.indexOf('dueDate');
  const dateCol = roles.indexOf('date');
  const datePickCol = dueDateCol >= 0 ? dueDateCol : dateCol;
  const parentCol = roles.indexOf('parent');
  const notesCol = roles.indexOf('notes');
  const subitemsCol = roles.indexOf('subitems');

  const out: RawRow[] = [];
  // Board name is only a fallback label — never treat mid-board Monday banners
  // (e.g. "Level 01 - Batch 0001") as Boardroom groups.
  const fallbackGroup = defaultGroupName || 'Imported';
  /** Spooling: unmatched rows under an open batch nest there; trade *Spooling roots stay peers. */
  const looseNestUnderOpenFrame = boardType === 'spooling';

  type Frame = { title: string; remaining: Set<string> };
  const stack: Frame[] = [];
  /** Indent-based parent stack: { indent, title } */
  const indentStack: Array<{ indent: number; title: string }> = [];

  const pushItem = (partial: {
    title: string;
    groupName?: string;
    statusLabel: string;
    personNames: string[];
    dueDate: string | null;
    description: string;
    parentFromCol: string | null;
    subitemNames: string[];
    extraFields: Record<string, string>;
    indent?: number;
    /** When true, keep an open frame so following rows can nest (banner → batch). */
    openNestingFrame?: boolean;
  }) => {
    let parentTitle = partial.parentFromCol;
    if (!parentTitle && useIndentParents && partial.indent != null) {
      while (indentStack.length > 0 && indentStack[indentStack.length - 1]!.indent >= partial.indent) {
        indentStack.pop();
      }
      parentTitle = indentStack.length > 0 ? indentStack[indentStack.length - 1]!.title : null;
      indentStack.push({ indent: partial.indent, title: partial.title });
    } else if (!parentTitle) {
      let matched = false;
      for (let i = stack.length - 1; i >= 0; i--) {
        if (stack[i]!.remaining.has(partial.title)) {
          stack.length = i + 1;
          stack[i]!.remaining.delete(partial.title);
          parentTitle = stack[i]!.title;
          matched = true;
          break;
        }
      }
      if (!matched) {
        const isSpoolingTradeRoot = /spooling$/i.test(partial.title.trim());
        if (looseNestUnderOpenFrame && stack.length > 0 && !isSpoolingTradeRoot) {
          parentTitle = stack[stack.length - 1]!.title;
        } else {
          stack.length = 0;
          parentTitle = null;
        }
      }
    }

    out.push({
      title: partial.title,
      groupName: (partial.groupName ?? '').trim() || fallbackGroup,
      statusLabel: partial.statusLabel,
      personNames: partial.personNames,
      dueDate: partial.dueDate,
      description: partial.description,
      parentFromCol: parentTitle,
      subitemNames: partial.subitemNames,
      extraFields: partial.extraFields,
    });

    if (partial.subitemNames.length > 0) {
      stack.push({ title: partial.title, remaining: new Set(partial.subitemNames) });
    } else if (partial.openNestingFrame) {
      stack.push({ title: partial.title, remaining: new Set() });
    }
  };

  for (let r = headerIdx + 1; r < endIdx; r++) {
    const row = rows[r] ?? [];
    if (row.every((cell) => !cell.trim())) continue;

    // Monday group banner inside a board — not a Boardroom group.
    // If it matches a pending Subitems name, treat it as a real nested item.
    if (isBannerRow(row)) {
      const banner = filledCells(row)[0]!;
      let matchedParent: string | null = null;
      for (let i = stack.length - 1; i >= 0; i--) {
        if (stack[i]!.remaining.has(banner)) {
          matchedParent = stack[i]!.title;
          stack.length = i + 1;
          stack[i]!.remaining.delete(banner);
          break;
        }
      }
      if (matchedParent) {
        pushItem({
          title: banner,
          statusLabel: '',
          personNames: [],
          dueDate: null,
          description: '',
          parentFromCol: matchedParent,
          subitemNames: [],
          extraFields: {},
          openNestingFrame: true,
        });
      }
      continue;
    }
    if (isNameHeaderRow(row)) break;

    const rawTitle = row[titleCol] ?? '';
    const title = rawTitle.trim();
    if (!title) continue;
    const indent = leadingIndentLevel(rawTitle);

    const extraFields: Record<string, string> = {};
    headers.forEach((header, idx) => {
      if (!header.trim()) return;
      if (roles[idx] !== 'skip') return;
      const value = (row[idx] ?? '').trim();
      if (value) extraFields[header.trim()] = value;
    });

    const groupFromCol =
      honorGroupColumn && groupCol >= 0 ? (row[groupCol] ?? '').trim() : '';

    pushItem({
      title,
      groupName: groupFromCol,
      statusLabel: statusCol >= 0 ? (row[statusCol] ?? '').trim() : '',
      personNames: personCol >= 0 ? parsePersonNames(row[personCol] ?? '') : [],
      dueDate: datePickCol >= 0 ? parseDueDate(row[datePickCol] ?? '') : null,
      description: notesCol >= 0 ? (row[notesCol] ?? '').trim() : '',
      parentFromCol: parentCol >= 0 ? (row[parentCol] ?? '').trim() || null : null,
      subitemNames: subitemsCol >= 0 ? parseSubitemNames(row[subitemsCol] ?? '') : [],
      extraFields,
      indent,
    });
  }

  return out;
}

/**
 * Flat Smartsheet / Boardroom re-export: one header row with Board (+ optional Group / Parent).
 * Hierarchy comes from Parent Task (or leading indent on the name column).
 */
function parseColumnOrientedSheet(
  rows: string[][],
  headerIdx: number,
  warnings: string[]
): {
  items: MondayImportItem[];
  importedBoardNames: string[];
  skippedBoardNames: string[];
  hasExplicitGroups: boolean;
} {
  const headers = rows[headerIdx] ?? [];
  const roles = headers.map(classifyHeader);
  const nameIdx = roles.indexOf('name');
  const titleCol = nameIdx >= 0 ? nameIdx : 0;
  const boardCol = roles.indexOf('board');
  const groupCol = roles.indexOf('group');
  const statusCol = roles.indexOf('status');
  const personCol = roles.indexOf('person');
  const dueDateCol = roles.indexOf('dueDate');
  const dateCol = roles.indexOf('date');
  const datePickCol = dueDateCol >= 0 ? dueDateCol : dateCol;
  const parentCol = roles.indexOf('parent');
  const notesCol = roles.indexOf('notes');

  const items: MondayImportItem[] = [];
  const importedBoardNames: string[] = [];
  const skippedBoardNames: string[] = [];
  const seenImport = new Set<string>();
  const seenSkip = new Set<string>();
  let hasExplicitGroups = false;

  /** Per-board indent stack when Parent column is empty. */
  const indentByBoard = new Map<string, Array<{ indent: number; title: string }>>();

  for (let r = headerIdx + 1; r < rows.length; r++) {
    const row = rows[r] ?? [];
    if (row.every((cell) => !cell.trim())) continue;
    if (isNameHeaderRow(row)) break;

    const rawTitle = row[titleCol] ?? '';
    const title = rawTitle.trim();
    if (!title) continue;

    const boardLabel =
      boardCol >= 0 ? (row[boardCol] ?? '').trim() : '';
    if (!boardLabel) {
      warnings.push(`Row "${title}" has no Board value — skipped.`);
      continue;
    }

    const boardType = mapMondayBoardToBoardroom(boardLabel);
    if (!boardType) {
      if (!seenSkip.has(boardLabel)) {
        seenSkip.add(boardLabel);
        skippedBoardNames.push(boardLabel);
        warnings.push(`Skipped board "${boardLabel}" (not in Boardroom template).`);
      }
      continue;
    }

    if (!seenImport.has(boardLabel)) {
      seenImport.add(boardLabel);
      importedBoardNames.push(boardLabel);
    }

    const groupName =
      groupCol >= 0 ? (row[groupCol] ?? '').trim() || boardLabel : boardLabel;
    if (groupCol >= 0 && (row[groupCol] ?? '').trim() && (row[groupCol] ?? '').trim() !== boardLabel) {
      hasExplicitGroups = true;
    }

    let parentTitle =
      parentCol >= 0 ? (row[parentCol] ?? '').trim() || null : null;

    const indent = leadingIndentLevel(rawTitle);
    if (!parentTitle) {
      const stackKey = `${boardLabel}\0${groupName}`;
      const stack = indentByBoard.get(stackKey) ?? [];
      while (stack.length > 0 && stack[stack.length - 1]!.indent >= indent) {
        stack.pop();
      }
      parentTitle = stack.length > 0 ? stack[stack.length - 1]!.title : null;
      stack.push({ indent, title });
      indentByBoard.set(stackKey, stack);
    }

    const extraFields: Record<string, string> = {};
    headers.forEach((header, idx) => {
      if (!header.trim()) return;
      if (roles[idx] !== 'skip') return;
      const value = (row[idx] ?? '').trim();
      if (value) extraFields[header.trim()] = value;
    });

    items.push({
      title,
      boardType,
      sourceBoardName: boardLabel,
      groupName,
      statusLabel: statusCol >= 0 ? (row[statusCol] ?? '').trim() : '',
      personNames: personCol >= 0 ? parsePersonNames(row[personCol] ?? '') : [],
      dueDate: datePickCol >= 0 ? parseDueDate(row[datePickCol] ?? '') : null,
      description: notesCol >= 0 ? (row[notesCol] ?? '').trim() : '',
      parentTitle,
      extraFields,
    });
  }

  return { items, importedBoardNames, skippedBoardNames, hasExplicitGroups };
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
 * Parent is already resolved in parseBoardSectionRows (stored on parentFromCol).
 */
function assignParents(rows: RawRow[]): Array<RawRow & { parentTitle: string | null }> {
  return rows.map((row) => ({
    ...row,
    parentTitle: row.parentFromCol,
  }));
}

/**
 * On these boards, Monday top-level items are Boardroom groups (not tasks):
 *   Deliverables → MP - Lower Level 02 / Level 01 / Roof
 *   Detailers    → MP - Lower Level LL2 / Level 01 / Roof
 *   Spooling     → Mechanical Piping Spooling / Mechanical Duct / Plumbing
 *   Project Mgmt → Contract Review / Scope of Work Review / …
 *
 * Boardroom only renders one subtask level, so leaving containers as tasks
 * hides the real subtasks.
 */
const ROOT_AS_GROUP_BOARDS: ReadonlySet<ProjectBoardType> = new Set([
  'deliverables',
  'detailers',
  'spooling',
  'project-managers',
]);

function promoteContainerRootsToGroups(items: MondayImportItem[]): {
  items: MondayImportItem[];
  ensuredGroups: MondayImportEnsuredGroup[];
} {
  const boardOrder = [...new Set(items.map((item) => item.boardType))];
  const out: MondayImportItem[] = [];
  const ensuredGroups: MondayImportEnsuredGroup[] = [];
  const ensuredKeys = new Set<string>();

  const ensureGroup = (boardType: ProjectBoardType, name: string) => {
    const key = `${boardType}\0${name}`;
    if (ensuredKeys.has(key)) return;
    ensuredKeys.add(key);
    ensuredGroups.push({ boardType, name });
  };

  for (const boardType of boardOrder) {
    const boardItems = items.filter((item) => item.boardType === boardType);
    if (!ROOT_AS_GROUP_BOARDS.has(boardType)) {
      out.push(...boardItems);
      continue;
    }

    type Node = MondayImportItem & { index: number; parentIndex: number | null };
    const nodes: Node[] = boardItems.map((item, index) => ({
      ...item,
      index,
      parentIndex: null,
    }));

    for (let i = 0; i < nodes.length; i++) {
      const node = nodes[i]!;
      if (!node.parentTitle) continue;
      for (let j = i - 1; j >= 0; j--) {
        if (nodes[j]!.title === node.parentTitle) {
          node.parentIndex = j;
          break;
        }
      }
    }

    // Every top-level Monday item becomes a Boardroom group on these boards
    // (including empty ones like Mechanical Duct Spooling).
    const promotedRootIndexes = new Set<number>();
    for (const node of nodes) {
      if (node.parentIndex == null) {
        promotedRootIndexes.add(node.index);
        ensureGroup(boardType, node.title);
      }
    }

    const findPromotedRootIndex = (node: Node): number | null => {
      let current: Node | null = node;
      const seen = new Set<number>();
      while (current?.parentIndex != null) {
        if (seen.has(current.parentIndex)) break;
        seen.add(current.parentIndex);
        const parent: Node = nodes[current.parentIndex]!;
        if (promotedRootIndexes.has(parent.index)) return parent.index;
        current = parent;
      }
      return null;
    };

    for (const node of nodes) {
      if (promotedRootIndexes.has(node.index)) continue;

      const promotedRootIdx = findPromotedRootIndex(node);
      if (promotedRootIdx == null) {
        out.push({
          title: node.title,
          boardType: node.boardType,
          sourceBoardName: node.sourceBoardName,
          groupName: node.groupName,
          statusLabel: node.statusLabel,
          personNames: node.personNames,
          dueDate: node.dueDate,
          description: node.description,
          parentTitle: node.parentTitle,
          extraFields: node.extraFields,
        });
        continue;
      }

      const parent = node.parentIndex != null ? nodes[node.parentIndex]! : null;
      const parentIsPromotedRoot = parent != null && promotedRootIndexes.has(parent.index);

      out.push({
        title: node.title,
        boardType: node.boardType,
        sourceBoardName: node.sourceBoardName,
        groupName: nodes[promotedRootIdx]!.title,
        statusLabel: node.statusLabel,
        personNames: node.personNames,
        dueDate: node.dueDate,
        description: node.description,
        parentTitle: parentIsPromotedRoot ? null : node.parentTitle,
        extraFields: node.extraFields,
      });
    }
  }

  return { items: out, ensuredGroups };
}

interface BoardBlock {
  name: string;
  headerIdx: number;
  endIdx: number;
}

function findBoardBlocks(rows: string[][]): { projectTitle: string | null; blocks: BoardBlock[] } {
  let projectTitle: string | null = null;
  const blocks: BoardBlock[] = [];

  // First banner before any header is usually the project / workbook title
  for (let i = 0; i < Math.min(rows.length, 5); i++) {
    if (isBannerRow(rows[i] ?? []) && !isNameHeaderRow(rows[i + 1] ?? [])) {
      // title only if next non-empty isn't immediately a header of the same name board
    }
  }
  if (rows.length && isBannerRow(rows[0] ?? [])) {
    const first = filledCells(rows[0]!)[0] ?? '';
    // If row 1 is also a banner (board name), row 0 is the project title
    if (rows[1] && isBannerRow(rows[1])) {
      projectTitle = first;
    }
  }

  for (let i = 0; i < rows.length; i++) {
    if (!isNameHeaderRow(rows[i] ?? [])) continue;

    // Board name is the nearest banner above this header
    let boardName = 'Imported';
    for (let j = i - 1; j >= 0; j--) {
      const row = rows[j] ?? [];
      if (row.every((c) => !c.trim())) continue;
      if (isBannerRow(row)) {
        const banner = filledCells(row)[0]!;
        // Skip workbook title when it sits above the first board
        if (projectTitle && banner === projectTitle && blocks.length === 0) {
          continue;
        }
        boardName = banner;
        break;
      }
      break;
    }

    let endIdx = rows.length;
    for (let k = i + 1; k < rows.length; k++) {
      if (isNameHeaderRow(rows[k] ?? [])) {
        endIdx = k;
        break;
      }
    }
    // Trim end to the banner that precedes the next header (belongs to next board)
    for (let k = endIdx - 1; k > i; k--) {
      const row = rows[k] ?? [];
      if (row.every((c) => !c.trim())) continue;
      if (isBannerRow(row) && isNameHeaderRow(rows[endIdx] ?? [])) {
        endIdx = k;
      }
      break;
    }

    blocks.push({ name: boardName, headerIdx: i, endIdx });
    i = endIdx - 1;
  }

  // Flat export with a single header and no board banners
  if (blocks.length === 0) {
    const headerIdx = rows.findIndex((row) => isNameHeaderRow(row));
    if (headerIdx >= 0) {
      blocks.push({ name: 'Imported', headerIdx, endIdx: rows.length });
    }
  }

  return { projectTitle, blocks };
}

function headerRolesAt(rows: string[][], headerIdx: number): ColumnRole[] {
  return (rows[headerIdx] ?? []).map(classifyHeader);
}

function sheetHasBoardColumn(rows: string[][]): boolean {
  const headerIdx = rows.findIndex((row) => isNameHeaderRow(row));
  if (headerIdx < 0) return false;
  return headerRolesAt(rows, headerIdx).includes('board');
}

/** Parse a Monday.com or Smartsheet Excel/CSV project export (.xlsx / .xls / .csv). */
export function parseMondayBoardFile(
  buffer: ArrayBuffer,
  fileName?: string
): MondayImportParseResult {
  const warnings: string[] = [];
  const workbook = XLSX.read(buffer, { type: 'array', cellDates: true });
  if (!workbook.SheetNames.length) {
    return {
      boardNameHint: '',
      items: [],
      groupNames: [],
      ensuredGroups: [],
      importedBoardNames: [],
      skippedBoardNames: [],
      warnings: ['File has no sheets.'],
    };
  }

  const sheetName =
    workbook.SheetNames.find((name) => !/update/i.test(name)) ?? workbook.SheetNames[0]!;
  const sheet = workbook.Sheets[sheetName];
  if (!sheet) {
    return {
      boardNameHint: '',
      items: [],
      groupNames: [],
      ensuredGroups: [],
      importedBoardNames: [],
      skippedBoardNames: [],
      warnings: ['Could not read sheet.'],
    };
  }

  const rows = rowsFromSheet(sheet);
  const stem = (fileName ?? '').replace(/\.[^.]+$/, '').trim();
  const fromFile = stem
    .replace(/[_-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .replace(/\d{8,}$/, '')
    .trim();

  let items: MondayImportItem[] = [];
  let importedBoardNames: string[] = [];
  let skippedBoardNames: string[] = [];
  let projectTitle: string | null = null;
  let hasExplicitGroups = false;

  // Smartsheet / Boardroom re-export: flat table with a Board column
  if (sheetHasBoardColumn(rows)) {
    const headerIdx = rows.findIndex((row) => isNameHeaderRow(row));
    const parsed = parseColumnOrientedSheet(rows, headerIdx, warnings);
    items = parsed.items;
    importedBoardNames = parsed.importedBoardNames;
    skippedBoardNames = parsed.skippedBoardNames;
    hasExplicitGroups = parsed.hasExplicitGroups;
  } else {
    const found = findBoardBlocks(rows);
    projectTitle = found.projectTitle;
    const blocks = found.blocks;
    const seenSkip = new Set<string>();

    for (const block of blocks) {
      let boardName = block.name;
      let boardType = mapMondayBoardToBoardroom(boardName);

      // Single flat Smartsheet sheet (no Monday banners): map from sheet / file name
      if (!boardType && boardName === 'Imported') {
        boardType =
          mapMondayBoardToBoardroom(sheetName) ??
          mapMondayBoardToBoardroom(fromFile) ??
          null;
        if (boardType) {
          boardName =
            PROJECT_BOARD_TYPES.find((b) => b.id === boardType)?.label ?? sheetName;
        }
      }

      if (!boardType) {
        if (boardName && !seenSkip.has(boardName)) {
          seenSkip.add(boardName);
          skippedBoardNames.push(boardName);
          warnings.push(`Skipped board "${boardName}" (not in Boardroom template).`);
        }
        continue;
      }

      const roles = headerRolesAt(rows, block.headerIdx);
      const hasGroupCol = roles.includes('group');
      const hasParentCol = roles.includes('parent');
      // Smartsheet-style: indent nests children when there is no Parent column
      const useIndentParents = !hasParentCol && boardName === 'Imported';

      const raw = parseBoardSectionRows(
        rows,
        block.headerIdx,
        block.endIdx,
        boardName,
        boardType,
        { honorGroupColumn: hasGroupCol, useIndentParents }
      );
      if (raw.length === 0) continue;

      if (hasGroupCol) {
        const explicit = raw.some(
          (row) => row.groupName.trim() && row.groupName.trim() !== boardName
        );
        if (explicit) hasExplicitGroups = true;
      }

      importedBoardNames.push(boardName);
      const nested = assignParents(raw);
      for (const row of nested) {
        items.push({
          title: row.title,
          boardType,
          sourceBoardName: boardName,
          groupName: row.groupName,
          statusLabel: row.statusLabel,
          personNames: row.personNames,
          dueDate: row.dueDate,
          description: row.description,
          parentTitle: row.parentTitle,
          extraFields: row.extraFields,
        });
      }
    }
  }

  const groupNames = [...new Set(items.map((item) => item.groupName))];
  const boardNameHint = (projectTitle || fromFile).trim();

  // When Group (and usually Parent) are already set, do not promote roots to groups
  const reshaped = hasExplicitGroups
    ? { items, ensuredGroups: ensuredGroupsFromItems(items) }
    : promoteContainerRootsToGroups(items);

  const reshapedGroupNames = [
    ...new Set([
      ...reshaped.items.map((item) => item.groupName),
      ...reshaped.ensuredGroups.map((g) => g.name),
    ]),
  ];

  if (reshaped.items.length === 0 && reshaped.ensuredGroups.length === 0) {
    warnings.push(
      'No importable items found. Known boards (Project Setup, Detailers/Modeling, Deliverables, RFI, Spooling, etc.) were empty or missing. For Smartsheet multi-board jobs, include a Board column (e.g. "MP Deliverables").'
    );
  }

  return {
    boardNameHint,
    items: reshaped.items,
    groupNames: reshapedGroupNames.length ? reshapedGroupNames : groupNames,
    ensuredGroups: reshaped.ensuredGroups,
    importedBoardNames,
    skippedBoardNames,
    warnings,
  };
}

export interface MondayImportSummary {
  projectId: string;
  projectName: string;
  groupCount: number;
  taskCount: number;
  warnings: string[];
}
