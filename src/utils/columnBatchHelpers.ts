import type { CustomBoard, ProjectBoardType, SavedSheetColumnTemplate, SheetColumnDefinition } from '../types';
import {
  appendPremadeColumnToBoardState,
  appendPremadeColumnToOverviewSectionState,
} from '../data/premadeSheetColumns';
import { getMainOverviewSectionColumnOrder } from './mainOverviewColumns';
import type { BoardSheetColumnOrderMap, BoardSheetColumnsMap } from './sheetColumns';
import {
  appendSheetColumnDefinition,
  getBoardLocalSheetColumns,
  getBoardSheetColumnOrder,
  getAllConfiguredBoardTypes,
  propagateMainSheetColumnToAllBoards,
} from './sheetColumns';

export function buildSheetColumnDefinition(
  id: string,
  label: string,
  type: SheetColumnDefinition['type'],
  options?: string[],
  headerAlignment?: SheetColumnDefinition['headerAlignment'],
  cellAlignment?: SheetColumnDefinition['cellAlignment']
): SheetColumnDefinition {
  const column: SheetColumnDefinition = {
    id,
    label: label.trim(),
    type,
    headerAlignment: headerAlignment ?? 'center',
    cellAlignment: cellAlignment ?? 'center',
  };
  if (type === 'dropdown') {
    column.options = (options ?? ['Option 1', 'Option 2'])
      .map((option) => option.trim())
      .filter(Boolean);
  }
  return column;
}

export function savedTemplateFromColumn(
  column: SheetColumnDefinition,
  id: string,
  createdAt: string
): SavedSheetColumnTemplate {
  return {
    id,
    label: column.label,
    type: column.type,
    options: column.options ? [...column.options] : undefined,
    headerAlignment: column.headerAlignment,
    cellAlignment: column.cellAlignment,
    createdAt,
  };
}

export function appendCustomColumnToBoardState(
  boardType: ProjectBoardType,
  column: SheetColumnDefinition,
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  customBoards: CustomBoard[]
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
} {
  const order = getBoardSheetColumnOrder(
    boardType,
    boardSheetColumnOrder,
    boardSheetColumns,
    boardType === 'main'
  );
  if (order.includes(column.id)) {
    return { boardSheetColumns, boardSheetColumnOrder };
  }

  const boardIdx = order.indexOf('board');
  const nextOrder =
    boardIdx >= 0
      ? [...order.slice(0, boardIdx), column.id, ...order.slice(boardIdx)]
      : [...order, column.id];

  const allBoardTypes = getAllConfiguredBoardTypes(customBoards);
  const nextBoardSheetColumns =
    boardType === 'main'
      ? propagateMainSheetColumnToAllBoards(
          appendSheetColumnDefinition(boardSheetColumns, 'main', column),
          column,
          allBoardTypes
        )
      : {
          ...boardSheetColumns,
          [boardType]: [...getBoardLocalSheetColumns(boardType, boardSheetColumns), column],
        };

  return {
    boardSheetColumns: nextBoardSheetColumns,
    boardSheetColumnOrder: {
      ...boardSheetColumnOrder,
      [boardType]: nextOrder,
    },
  };
}

export function appendCustomColumnToOverviewSectionState(
  sectionBoardType: ProjectBoardType,
  column: SheetColumnDefinition,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  boardSheetColumns: BoardSheetColumnsMap
): {
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap;
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;
} {
  const currentExtras = mainOverviewSectionSheetColumns[sectionBoardType] ?? [];
  const order = getMainOverviewSectionColumnOrder(
    sectionBoardType,
    mainOverviewSectionColumnOrder,
    mainOverviewSectionSheetColumns,
    boardSheetColumnOrder,
    boardSheetColumns
  );
  if (order.includes(column.id)) {
    return { mainOverviewSectionSheetColumns, mainOverviewSectionColumnOrder };
  }

  const nextSectionColumns = currentExtras.some((entry) => entry.id === column.id)
    ? currentExtras
    : [...currentExtras, column];
  const nextSectionSheetColumns = {
    ...mainOverviewSectionSheetColumns,
    [sectionBoardType]: nextSectionColumns,
  };
  const boardIdx = order.indexOf('board');
  const nextOrder =
    boardIdx >= 0
      ? [...order.slice(0, boardIdx), column.id, ...order.slice(boardIdx)]
      : [...order, column.id];

  return {
    mainOverviewSectionSheetColumns: nextSectionSheetColumns,
    mainOverviewSectionColumnOrder: {
      ...mainOverviewSectionColumnOrder,
      [sectionBoardType]: nextOrder,
    },
  };
}

export function applyPremadeColumnsToTargets(
  targets: ProjectBoardType[],
  premadeIds: string[],
  mode: 'board' | 'overview',
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;
  addedCount: number;
} {
  let nextColumns = boardSheetColumns;
  let nextOrder = boardSheetColumnOrder;
  let nextSectionOrder = mainOverviewSectionColumnOrder;
  let addedCount = 0;

  for (const premadeId of premadeIds) {
    for (const target of targets) {
      if (mode === 'overview') {
        const before = getMainOverviewSectionColumnOrder(
          target,
          nextSectionOrder,
          mainOverviewSectionSheetColumns,
          nextOrder,
          nextColumns
        );
        const update = appendPremadeColumnToOverviewSectionState(
          target,
          premadeId,
          nextColumns,
          nextOrder,
          nextSectionOrder,
          mainOverviewSectionSheetColumns
        );
        if (!update) continue;
        const after = getMainOverviewSectionColumnOrder(
          target,
          update.mainOverviewSectionColumnOrder,
          mainOverviewSectionSheetColumns,
          update.boardSheetColumnOrder,
          update.boardSheetColumns
        );
        if (after.includes(premadeId) && !before.includes(premadeId)) {
          addedCount += 1;
        }
        nextColumns = update.boardSheetColumns;
        nextOrder = update.boardSheetColumnOrder;
        nextSectionOrder = update.mainOverviewSectionColumnOrder;
      } else {
        const before = getBoardSheetColumnOrder(
          target,
          nextOrder,
          nextColumns,
          target === 'main'
        );
        const update = appendPremadeColumnToBoardState(
          target,
          premadeId,
          nextColumns,
          nextOrder
        );
        if (!update) continue;
        const after = getBoardSheetColumnOrder(
          target,
          update.boardSheetColumnOrder,
          update.boardSheetColumns,
          target === 'main'
        );
        if (after.includes(premadeId) && !before.includes(premadeId)) {
          addedCount += 1;
        }
        nextColumns = update.boardSheetColumns;
        nextOrder = update.boardSheetColumnOrder;
      }
    }
  }

  return {
    boardSheetColumns: nextColumns,
    boardSheetColumnOrder: nextOrder,
    mainOverviewSectionColumnOrder: nextSectionOrder,
    addedCount,
  };
}

export function applyCustomColumnToTargets(
  targets: ProjectBoardType[],
  column: SheetColumnDefinition,
  mode: 'board' | 'overview',
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  customBoards: CustomBoard[]
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap;
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;
} {
  let nextColumns = boardSheetColumns;
  let nextOrder = boardSheetColumnOrder;
  let nextSectionColumns = mainOverviewSectionSheetColumns;
  let nextSectionOrder = mainOverviewSectionColumnOrder;

  for (const target of targets) {
    if (mode === 'overview') {
      const update = appendCustomColumnToOverviewSectionState(
        target,
        column,
        nextSectionColumns,
        nextSectionOrder,
        nextOrder,
        nextColumns
      );
      nextSectionColumns = update.mainOverviewSectionSheetColumns;
      nextSectionOrder = update.mainOverviewSectionColumnOrder;
    } else {
      const update = appendCustomColumnToBoardState(
        target,
        column,
        nextColumns,
        nextOrder,
        customBoards
      );
      nextColumns = update.boardSheetColumns;
      nextOrder = update.boardSheetColumnOrder;
    }
  }

  return {
    boardSheetColumns: nextColumns,
    boardSheetColumnOrder: nextOrder,
    mainOverviewSectionSheetColumns: nextSectionColumns,
    mainOverviewSectionColumnOrder: nextSectionOrder,
  };
}
