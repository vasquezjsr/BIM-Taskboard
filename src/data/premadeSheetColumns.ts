import type { ProjectBoardType, SheetColumnDefinition, SheetColumnType } from '../types';
import { DEFAULT_SHEET_COLUMN_ALIGNMENT } from '../utils/sheetColumnConstants';
import type { BoardSheetColumnOrderMap, BoardSheetColumnsMap } from '../utils/sheetColumns';
import {
  getBoardLocalSheetColumns,
  getBoardSheetColumnOrder,
} from '../utils/sheetColumns';
import {
  getMainOverviewSectionColumnOrder,
  resolveStoredMainOverviewSectionColumnOrder,
} from '../utils/mainOverviewColumns';
import { WORKFLOW_DUE_DATE_BOARDS } from './workflowDueDateColumns';

export const PREMADE_MATERIAL_COLUMN_ID = 'col-material';

export const PREMADE_MATERIAL_OPTIONS = [
  'PVC',
  'CPVC',
  'DWV PVC',
  'NHCI',
  'HUBxSPGT',
  'SWCI',
  'PEX',
  'HDPE',
  'PP',
  'GALV',
  'DUCTILE',
  'SWEAT CU',
  'PRESS CU',
  'BRAZED CU',
  'GRV CU',
  'CS SCH40 WELD',
  'CS SCH80 WELD',
  'CS SCH40 THD',
  'CS SCH80 THD',
  'CS SCH40 GRV',
  'CS SCH80 GRV',
  'CS SCH40 SW',
  'CS SCH80 SW',
  'SS SCH10 WELD',
  'SS SCH40 WELD',
  'SS SCH10 THD',
  'SS SCH40 THD',
  'SS SCH10 GRV',
  'SS SCH40 GRV',
  'SS SCH10 SW',
  'SS SCH40 SW',
] as const;

export interface PremadeSheetColumnTemplate {
  id: string;
  label: string;
  description: string;
  type: SheetColumnType;
  options?: readonly string[];
  boardTypes: readonly ProjectBoardType[];
}

export const PREMADE_SHEET_COLUMNS: PremadeSheetColumnTemplate[] = [
  {
    id: PREMADE_MATERIAL_COLUMN_ID,
    label: 'Material',
    description: 'Piping and fabrication material — 31 standard options',
    type: 'dropdown',
    options: PREMADE_MATERIAL_OPTIONS,
    boardTypes: WORKFLOW_DUE_DATE_BOARDS,
  },
];

export function getPremadeSheetColumn(premadeId: string): PremadeSheetColumnTemplate | undefined {
  return PREMADE_SHEET_COLUMNS.find((entry) => entry.id === premadeId);
}

export function premadeAppliesToBoard(
  template: PremadeSheetColumnTemplate,
  boardType: ProjectBoardType
): boolean {
  return template.boardTypes.includes(boardType);
}

export function premadeColumnsForBoard(
  boardType: ProjectBoardType
): PremadeSheetColumnTemplate[] {
  return PREMADE_SHEET_COLUMNS.filter((template) => premadeAppliesToBoard(template, boardType));
}

export function premadeColumnToDefinition(
  template: PremadeSheetColumnTemplate
): SheetColumnDefinition {
  const column: SheetColumnDefinition = {
    id: template.id,
    label: template.label,
    type: template.type,
    headerAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
    cellAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
  };
  if (template.type === 'dropdown') {
    column.options = [...(template.options ?? [])];
  }
  return column;
}

function isLegacyMaterialColumn(column: SheetColumnDefinition): boolean {
  return (
    column.label.trim().toLowerCase() === 'material' && column.id !== PREMADE_MATERIAL_COLUMN_ID
  );
}

function insertColumnIdInOrder(order: string[], columnId: string): string[] {
  if (order.includes(columnId)) return order;
  const assigneeIndex = order.indexOf('assignee');
  const insertAt = assigneeIndex >= 0 ? assigneeIndex + 1 : order.length;
  return [...order.slice(0, insertAt), columnId, ...order.slice(insertAt)];
}

/** Idempotently add all premade columns to workflow boards and strip duplicate Material columns. */
export function ensurePremadeSheetColumns(
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
} {
  const removedIds = new Set<string>();
  let nextColumns: BoardSheetColumnsMap = { ...boardSheetColumns };

  for (const [boardType, columns] of Object.entries(nextColumns)) {
    if (!columns?.length) continue;
    const remaining = columns.filter((column) => {
      if (isLegacyMaterialColumn(column)) {
        removedIds.add(column.id);
        return false;
      }
      return true;
    });
    if (remaining.length !== columns.length) {
      nextColumns = { ...nextColumns, [boardType]: remaining };
    }
  }

  let nextOrder: BoardSheetColumnOrderMap = { ...boardSheetColumnOrder };
  for (const [boardType, order] of Object.entries(nextOrder)) {
    if (!order?.length) continue;
    const filtered = order.filter(
      (id) => !removedIds.has(id) || id === PREMADE_MATERIAL_COLUMN_ID
    );
    if (filtered.length !== order.length) {
      nextOrder = { ...nextOrder, [boardType]: filtered };
    }
  }

  for (const template of PREMADE_SHEET_COLUMNS) {
    for (const boardType of template.boardTypes) {
      const local = getBoardLocalSheetColumns(boardType, nextColumns).filter(
        (column) => !isLegacyMaterialColumn(column)
      );
      const hasColumn = local.some((column) => column.id === template.id);
      nextColumns = {
        ...nextColumns,
        [boardType]: hasColumn
          ? local
          : [...local, premadeColumnToDefinition(template)],
      };

      const order = getBoardSheetColumnOrder(
        boardType,
        nextOrder,
        nextColumns,
        boardType === 'main'
      ).filter((id) => !removedIds.has(id));
      nextOrder = {
        ...nextOrder,
        [boardType]: insertColumnIdInOrder(order, template.id),
      };
    }
  }

  return {
    boardSheetColumns: nextColumns,
    boardSheetColumnOrder: nextOrder,
  };
}

export function boardOrderIncludesColumn(
  boardType: ProjectBoardType,
  columnId: string,
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  isOverview: boolean
): boolean {
  return getBoardSheetColumnOrder(
    boardType,
    boardSheetColumnOrder,
    boardSheetColumns,
    isOverview
  ).includes(columnId);
}

export function overviewSectionOrderIncludesColumn(
  sectionBoardType: ProjectBoardType,
  columnId: string,
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  boardSheetColumns: BoardSheetColumnsMap
): boolean {
  return getMainOverviewSectionColumnOrder(
    sectionBoardType,
    mainOverviewSectionColumnOrder,
    mainOverviewSectionSheetColumns,
    boardSheetColumnOrder,
    boardSheetColumns
  ).includes(columnId);
}

export function appendPremadeColumnToBoardState(
  boardType: ProjectBoardType,
  premadeId: string,
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
} | null {
  const template = getPremadeSheetColumn(premadeId);
  if (!template || !premadeAppliesToBoard(template, boardType)) return null;

  let nextColumns = boardSheetColumns;
  const local = getBoardLocalSheetColumns(boardType, nextColumns).filter(
    (column) => !isLegacyMaterialColumn(column)
  );
  if (!local.some((column) => column.id === premadeId)) {
    nextColumns = {
      ...nextColumns,
      [boardType]: [...local, premadeColumnToDefinition(template)],
    };
  }

  const order = getBoardSheetColumnOrder(
    boardType,
    boardSheetColumnOrder,
    nextColumns,
    boardType === 'main'
  );
  if (order.includes(premadeId)) {
    return { boardSheetColumns: nextColumns, boardSheetColumnOrder };
  }

  return {
    boardSheetColumns: nextColumns,
    boardSheetColumnOrder: {
      ...boardSheetColumnOrder,
      [boardType]: insertColumnIdInOrder(order, premadeId),
    },
  };
}

export function appendPremadeColumnToOverviewSectionState(
  sectionBoardType: ProjectBoardType,
  premadeId: string,
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;
} | null {
  const boardUpdate = appendPremadeColumnToBoardState(
    sectionBoardType,
    premadeId,
    boardSheetColumns,
    boardSheetColumnOrder
  );
  if (!boardUpdate) return null;

  const order = getMainOverviewSectionColumnOrder(
    sectionBoardType,
    mainOverviewSectionColumnOrder,
    mainOverviewSectionSheetColumns,
    boardUpdate.boardSheetColumnOrder,
    boardUpdate.boardSheetColumns
  );
  if (order.includes(premadeId)) {
    return {
      ...boardUpdate,
      mainOverviewSectionColumnOrder,
    };
  }

  const nextOrder = insertColumnIdInOrder(order, premadeId);
  return {
    ...boardUpdate,
    mainOverviewSectionColumnOrder: {
      ...mainOverviewSectionColumnOrder,
      [sectionBoardType]: resolveStoredMainOverviewSectionColumnOrder(
        sectionBoardType,
        nextOrder,
        mainOverviewSectionSheetColumns,
        boardUpdate.boardSheetColumns
      ),
    },
  };
}

export function listAvailablePremadeForTargets(
  targets: ProjectBoardType[],
  isOverview: boolean,
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap
): PremadeSheetColumnTemplate[] {
  return PREMADE_SHEET_COLUMNS.filter((premade) => {
    const applicableTargets = targets.filter((target) => premadeAppliesToBoard(premade, target));
    if (applicableTargets.length === 0) return false;

    return applicableTargets.some((target) => {
      const present = isOverview
        ? overviewSectionOrderIncludesColumn(
            target,
            premade.id,
            mainOverviewSectionColumnOrder,
            mainOverviewSectionSheetColumns,
            boardSheetColumnOrder,
            boardSheetColumns
          )
        : boardOrderIncludesColumn(
            target,
            premade.id,
            boardSheetColumns,
            boardSheetColumnOrder,
            target === 'main'
          );
      return !present;
    });
  });
}

export function ensurePremadeInMainOverviewSectionOrders(
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap
): BoardSheetColumnOrderMap {
  let nextOrder = { ...mainOverviewSectionColumnOrder };

  for (const boardType of WORKFLOW_DUE_DATE_BOARDS) {
    const stored = nextOrder[boardType];
    if (!stored?.length) continue;
    if (stored.includes(PREMADE_MATERIAL_COLUMN_ID)) continue;
    nextOrder = {
      ...nextOrder,
      [boardType]: insertColumnIdInOrder(stored, PREMADE_MATERIAL_COLUMN_ID),
    };
  }

  return nextOrder;
}
