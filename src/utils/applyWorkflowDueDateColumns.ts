import type { ProjectBoardType } from '../types';
import {
  WORKFLOW_DUE_DATE_BOARDS,
  WORKFLOW_DUE_DATE_COLUMN_IDS,
  WORKFLOW_DUE_DATE_COLUMNS,
  WORKFLOW_DUE_DATE_MARKER_COLUMN_ID,
  isWorkflowDueDateColumn,
} from '../data/workflowDueDateColumns';
import {
  appendSheetColumnDefinition,
  getBoardLocalSheetColumns,
  getBoardSheetColumnOrder,
  type BoardSheetColumnOrderMap,
  type BoardSheetColumnsMap,
} from './sheetColumns';

function replaceDueWithWorkflowDates(order: string[]): string[] {
  const workflowIds = WORKFLOW_DUE_DATE_COLUMN_IDS;
  const withoutDue = order.filter((id) => id !== 'due' && !isWorkflowDueDateColumn(id));
  const dueIndex = order.indexOf('due');
  const assigneeIndex = withoutDue.indexOf('assignee');
  const insertAt =
    dueIndex >= 0
      ? Math.min(dueIndex, withoutDue.length)
      : assigneeIndex >= 0
        ? assigneeIndex + 1
        : withoutDue.length;

  return [
    ...withoutDue.slice(0, insertAt),
    ...workflowIds,
    ...withoutDue.slice(insertAt),
  ];
}

function ensureWorkflowColumnsOnBoard(
  map: BoardSheetColumnsMap,
  boardType: ProjectBoardType
): BoardSheetColumnsMap {
  let next = map;
  for (const column of WORKFLOW_DUE_DATE_COLUMNS) {
    next = appendSheetColumnDefinition(next, boardType, column);
  }
  return next;
}

function stripWorkflowColumnsFromBoard(
  map: BoardSheetColumnsMap,
  boardType: ProjectBoardType
): BoardSheetColumnsMap {
  const current = getBoardLocalSheetColumns(boardType, map);
  const remaining = current.filter((column) => !isWorkflowDueDateColumn(column.id));
  if (remaining.length === current.length) return map;
  return {
    ...map,
    [boardType]: remaining,
  };
}

function ensureDueColumnInOrder(
  orderMap: BoardSheetColumnOrderMap,
  boardType: ProjectBoardType,
  boardSheetColumns: BoardSheetColumnsMap,
  isOverview: boolean
): BoardSheetColumnOrderMap {
  const order = getBoardSheetColumnOrder(
    boardType,
    orderMap,
    boardSheetColumns,
    isOverview
  );
  const cleaned = order.filter((id) => !isWorkflowDueDateColumn(id));
  if (cleaned.includes('due')) {
    return { ...orderMap, [boardType]: cleaned };
  }

  const assigneeIndex = cleaned.indexOf('assignee');
  const insertAt = assigneeIndex >= 0 ? assigneeIndex + 1 : cleaned.length;
  const nextOrder = [...cleaned.slice(0, insertAt), 'due', ...cleaned.slice(insertAt)];
  return { ...orderMap, [boardType]: nextOrder };
}

export function workflowDueDateColumnsApplied(
  boardSheetColumns: BoardSheetColumnsMap
): boolean {
  return getBoardLocalSheetColumns('main', boardSheetColumns).some(
    (column) => column.id === WORKFLOW_DUE_DATE_MARKER_COLUMN_ID
  );
}

export function applyWorkflowDueDateColumns(
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
} {
  if (workflowDueDateColumnsApplied(boardSheetColumns)) {
    return { boardSheetColumns, boardSheetColumnOrder };
  }

  let nextColumns = boardSheetColumns;
  let nextOrder = boardSheetColumnOrder;

  for (const boardType of WORKFLOW_DUE_DATE_BOARDS) {
    nextColumns = ensureWorkflowColumnsOnBoard(nextColumns, boardType);
    const isOverview = boardType === 'main';
    const currentOrder = getBoardSheetColumnOrder(
      boardType,
      nextOrder,
      nextColumns,
      isOverview
    );
    nextOrder = {
      ...nextOrder,
      [boardType]: replaceDueWithWorkflowDates(currentOrder),
    };
  }

  for (const boardType of ['project-managers', 'rfi', 'documents'] as const) {
    nextColumns = stripWorkflowColumnsFromBoard(nextColumns, boardType);
    nextOrder = ensureDueColumnInOrder(
      nextOrder,
      boardType,
      nextColumns,
      false
    );
  }

  return {
    boardSheetColumns: nextColumns,
    boardSheetColumnOrder: nextOrder,
  };
}
