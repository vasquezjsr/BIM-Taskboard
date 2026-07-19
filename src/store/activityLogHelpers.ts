import type { ProjectBoardType, SheetColumnDefinition, Task, TaskGroup } from '../types';
import type { BoardSheetColumnOrderMap, BoardSheetColumnsMap } from '../utils/sheetColumns';
import {
  getBoardLocalSheetColumns,
  getBoardSheetColumnOrder,
  getBoardSheetColumns,
  appendSheetColumnDefinition,
  isFixedSheetColumnId,
  isMainOverviewSharedColumn,
  propagateMainSheetColumnToAllBoards,
  removeMainSheetColumnFromAllBoards,
} from '../utils/sheetColumns';
import type { ActivityLogEntry, DeletedColumnArchive } from '../utils/activityLog';
import { appendActivityLogEntry } from '../utils/activityLog';
import { getBoardLabel } from '../types';
import type { CustomBoard } from '../types';

export function resolveActivityActorId(
  currentUserId: string | null,
  viewAsOriginalUserId: string | null
): string | null {
  return viewAsOriginalUserId ?? currentUserId;
}

export function captureColumnFieldValues(
  tasks: Task[],
  taskGroups: TaskGroup[],
  columnId: string
): Pick<DeletedColumnArchive, 'taskFieldValues' | 'groupFieldValues'> {
  const taskFieldValues: DeletedColumnArchive['taskFieldValues'] = {};
  for (const task of tasks) {
    const customValue = task.customFields?.[columnId];
    const durationValue = task.durationFields?.[columnId];
    if (customValue === undefined && durationValue === undefined) continue;
    taskFieldValues[task.id] = {
      ...(customValue !== undefined ? { customFields: { [columnId]: customValue } } : {}),
      ...(durationValue !== undefined ? { durationFields: { [columnId]: durationValue } } : {}),
    };
  }

  const groupFieldValues: DeletedColumnArchive['groupFieldValues'] = {};
  for (const group of taskGroups) {
    const durationValue = group.durationFields?.[columnId];
    if (!durationValue) continue;
    groupFieldValues[group.id] = { [columnId]: durationValue };
  }

  return { taskFieldValues, groupFieldValues };
}

export function columnActivitySummary(
  action: 'deleted' | 'restored' | 'created',
  column: SheetColumnDefinition,
  boardType: ProjectBoardType,
  sectionBoardType: ProjectBoardType | null,
  customBoards: CustomBoard[]
): string {
  const boardLabel = sectionBoardType
    ? `${getBoardLabel('main', customBoards)} · ${getBoardLabel(sectionBoardType, customBoards)}`
    : getBoardLabel(boardType, customBoards);
  const verb = action === 'deleted' ? 'Removed' : action === 'restored' ? 'Restored' : 'Added';
  return `${verb} column "${column.label}" on ${boardLabel}`;
}

export function buildColumnDeleteArchive(params: {
  archiveId: string;
  activityLogId: string;
  actorId: string | null;
  boardType: ProjectBoardType;
  sectionBoardType: ProjectBoardType | null;
  column: SheetColumnDefinition;
  columnOrderBefore: string[];
  wasMainOverviewShared: boolean;
  tasks: Task[];
  taskGroups: TaskGroup[];
}): DeletedColumnArchive {
  const { taskFieldValues, groupFieldValues } = captureColumnFieldValues(
    params.tasks,
    params.taskGroups,
    params.column.id
  );
  return {
    id: params.archiveId,
    deletedAt: new Date().toISOString(),
    deletedById: params.actorId,
    activityLogId: params.activityLogId,
    boardType: params.boardType,
    sectionBoardType: params.sectionBoardType,
    column: params.column,
    columnOrderBefore: params.columnOrderBefore,
    wasMainOverviewShared: params.wasMainOverviewShared,
    taskFieldValues,
    groupFieldValues,
  };
}

export function applyColumnArchiveRestore(
  archive: DeletedColumnArchive,
  state: {
    boardSheetColumns: BoardSheetColumnsMap;
    boardSheetColumnOrder: BoardSheetColumnOrderMap;
    mainOverviewSectionSheetColumns: BoardSheetColumnsMap;
    mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;
    tasks: Task[];
    taskGroups: TaskGroup[];
    customBoards: CustomBoard[];
  }
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap;
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;
  tasks: Task[];
  taskGroups: TaskGroup[];
} {
  const allBoardTypes = [
    'main',
    'project-managers',
    'rfi',
    'documents',
    'detailers',
    'deliverables',
    'spooling',
    'fab',
    'shipping',
    'field',
    ...state.customBoards.map((board) => board.id),
  ] as ProjectBoardType[];

  let boardSheetColumns = state.boardSheetColumns;
  let boardSheetColumnOrder = state.boardSheetColumnOrder;
  let mainOverviewSectionSheetColumns = state.mainOverviewSectionSheetColumns;
  let mainOverviewSectionColumnOrder = state.mainOverviewSectionColumnOrder;

  if (archive.sectionBoardType) {
    const extras = mainOverviewSectionSheetColumns[archive.sectionBoardType] ?? [];
    if (!extras.some((column) => column.id === archive.column.id)) {
      mainOverviewSectionSheetColumns = {
        ...mainOverviewSectionSheetColumns,
        [archive.sectionBoardType]: [...extras, archive.column],
      };
    }
    const order = getBoardSheetColumnOrder(
      archive.sectionBoardType,
      mainOverviewSectionColumnOrder,
      {
        ...boardSheetColumns,
        ...mainOverviewSectionSheetColumns,
        [archive.sectionBoardType]: mainOverviewSectionSheetColumns[archive.sectionBoardType] ?? [],
      },
      false
    );
    const restoredOrder = archive.columnOrderBefore.length
      ? archive.columnOrderBefore
      : [...order.filter((id) => id !== archive.column.id), archive.column.id];
    mainOverviewSectionColumnOrder = {
      ...mainOverviewSectionColumnOrder,
      [archive.sectionBoardType]: restoredOrder.includes(archive.column.id)
        ? restoredOrder
        : [...restoredOrder, archive.column.id],
    };
  } else if (archive.wasMainOverviewShared) {
    boardSheetColumns = propagateMainSheetColumnToAllBoards(
      appendSheetColumnDefinition(boardSheetColumns, 'main', archive.column),
      archive.column,
      allBoardTypes
    );
    for (const board of allBoardTypes) {
      const order = getBoardSheetColumnOrder(
        board,
        boardSheetColumnOrder,
        boardSheetColumns,
        board === 'main'
      );
      const restoredOrder = archive.columnOrderBefore.includes(archive.column.id)
        ? archive.columnOrderBefore
        : [...order.filter((id) => id !== archive.column.id), archive.column.id];
      boardSheetColumnOrder = {
        ...boardSheetColumnOrder,
        [board]: restoredOrder.includes(archive.column.id)
          ? restoredOrder
          : [...restoredOrder, archive.column.id],
      };
    }
  } else {
    // Fixed columns live in order only — never re-add them as custom definitions.
    // Shared Main columns removed from a sub-board also stay order-only.
    if (
      !isFixedSheetColumnId(archive.column.id) &&
      !isMainOverviewSharedColumn(archive.column.id, boardSheetColumns)
    ) {
      const current = getBoardLocalSheetColumns(archive.boardType, boardSheetColumns);
      if (!current.some((column) => column.id === archive.column.id)) {
        boardSheetColumns = {
          ...boardSheetColumns,
          [archive.boardType]: [...current, archive.column],
        };
      }
    }
    const order = getBoardSheetColumnOrder(
      archive.boardType,
      boardSheetColumnOrder,
      boardSheetColumns,
      archive.boardType === 'main'
    );
    const restoredOrder = archive.columnOrderBefore.includes(archive.column.id)
      ? archive.columnOrderBefore
      : [...order.filter((id) => id !== archive.column.id), archive.column.id];
    boardSheetColumnOrder = {
      ...boardSheetColumnOrder,
      [archive.boardType]: restoredOrder.includes(archive.column.id)
        ? restoredOrder
        : [...restoredOrder, archive.column.id],
    };
  }

  let tasks = state.tasks.map((task) => {
    const saved = archive.taskFieldValues[task.id];
    if (!saved) return task;
    return {
      ...task,
      customFields: { ...(task.customFields ?? {}), ...(saved.customFields ?? {}) },
      durationFields: { ...(task.durationFields ?? {}), ...(saved.durationFields ?? {}) },
    };
  });

  let taskGroups = state.taskGroups.map((group) => {
    const saved = archive.groupFieldValues[group.id];
    if (!saved) return group;
    return {
      ...group,
      durationFields: { ...(group.durationFields ?? {}), ...saved },
    };
  });

  return {
    boardSheetColumns,
    boardSheetColumnOrder,
    mainOverviewSectionSheetColumns,
    mainOverviewSectionColumnOrder,
    tasks,
    taskGroups,
  };
}

export function logActivity(
  activityLog: ActivityLogEntry[],
  entry: Omit<ActivityLogEntry, 'id' | 'timestamp'>,
  createId: () => string
): ActivityLogEntry[] {
  return appendActivityLogEntry(activityLog, entry, createId);
}

export function findColumnDefinition(
  boardType: ProjectBoardType,
  columnId: string,
  boardSheetColumns: BoardSheetColumnsMap,
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap,
  sectionBoardType?: ProjectBoardType | null
): SheetColumnDefinition | null {
  if (sectionBoardType) {
    const sectionColumn = (mainOverviewSectionSheetColumns[sectionBoardType] ?? []).find(
      (column) => column.id === columnId
    );
    if (sectionColumn) return sectionColumn;
  }
  return (
    getBoardSheetColumns(boardType, boardSheetColumns).find((column) => column.id === columnId) ??
    null
  );
}

export function stripColumnFromState(
  boardType: ProjectBoardType,
  columnId: string,
  state: {
    boardSheetColumns: BoardSheetColumnsMap;
    boardSheetColumnOrder: BoardSheetColumnOrderMap;
    mainOverviewSectionSheetColumns: BoardSheetColumnsMap;
    mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;
    tasks: Task[];
    taskGroups: TaskGroup[];
    customBoards: CustomBoard[];
  },
  sectionBoardType?: ProjectBoardType | null
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap;
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;
  tasks: Task[];
  taskGroups: TaskGroup[];
} {
  const allBoardTypes = [
    'main',
    'project-managers',
    'rfi',
    'documents',
    'detailers',
    'deliverables',
    'spooling',
    'fab',
    'shipping',
    'field',
    ...state.customBoards.map((board) => board.id),
  ] as ProjectBoardType[];

  if (sectionBoardType) {
    const extras = state.mainOverviewSectionSheetColumns[sectionBoardType] ?? [];
    const nextSectionSheetColumns = {
      ...state.mainOverviewSectionSheetColumns,
      [sectionBoardType]: extras.filter((column) => column.id !== columnId),
    };
    const order = getBoardSheetColumnOrder(
      sectionBoardType,
      state.mainOverviewSectionColumnOrder,
      {
        ...state.boardSheetColumns,
        ...nextSectionSheetColumns,
      },
      false
    ).filter((id) => id !== columnId);
    // Keep the board tab layout identical to this Main Overview section.
    const boardOrder = order.filter((id) => id !== 'board');
    return {
      ...state,
      mainOverviewSectionSheetColumns: nextSectionSheetColumns,
      mainOverviewSectionColumnOrder: {
        ...state.mainOverviewSectionColumnOrder,
        [sectionBoardType]: order,
      },
      boardSheetColumnOrder: {
        ...state.boardSheetColumnOrder,
        [sectionBoardType]: boardOrder,
      },
      tasks: state.tasks.map((task) => {
        const customFields = { ...(task.customFields ?? {}) };
        const durationFields = { ...(task.durationFields ?? {}) };
        delete customFields[columnId];
        delete durationFields[columnId];
        return { ...task, customFields, durationFields };
      }),
      taskGroups: state.taskGroups.map((group) => {
        if (!group.durationFields?.[columnId]) return group;
        const durationFields = { ...group.durationFields };
        delete durationFields[columnId];
        return { ...group, durationFields };
      }),
    };
  }

  const sharedFromMain = isMainOverviewSharedColumn(columnId, state.boardSheetColumns);
  const isOverview = boardType === 'main';
  const nextBoardSheetColumns = sharedFromMain
    ? removeMainSheetColumnFromAllBoards(state.boardSheetColumns, columnId, allBoardTypes)
    : {
        ...state.boardSheetColumns,
        [boardType]: getBoardLocalSheetColumns(boardType, state.boardSheetColumns).filter(
          (column) => column.id !== columnId
        ),
      };

  const nextBoardSheetColumnOrder = { ...state.boardSheetColumnOrder };
  let nextMainOverviewSectionColumnOrder = { ...state.mainOverviewSectionColumnOrder };
  if (sharedFromMain) {
    for (const type of allBoardTypes) {
      nextBoardSheetColumnOrder[type] = getBoardSheetColumnOrder(
        type,
        nextBoardSheetColumnOrder,
        nextBoardSheetColumns,
        type === 'main'
      ).filter((id) => id !== columnId);
      if (type !== 'main') {
        const overviewOrder = (
          nextMainOverviewSectionColumnOrder[type] ?? nextBoardSheetColumnOrder[type] ?? []
        ).filter((id) => id !== columnId);
        nextMainOverviewSectionColumnOrder[type] = overviewOrder;
      }
    }
  } else {
    nextBoardSheetColumnOrder[boardType] = getBoardSheetColumnOrder(
      boardType,
      nextBoardSheetColumnOrder,
      nextBoardSheetColumns,
      isOverview
    ).filter((id) => id !== columnId);
    if (boardType !== 'main') {
      const overviewOrder = (
        nextMainOverviewSectionColumnOrder[boardType] ?? nextBoardSheetColumnOrder[boardType] ?? []
      ).filter((id) => id !== columnId);
      nextMainOverviewSectionColumnOrder[boardType] = overviewOrder;
    }
  }

  return {
    boardSheetColumns: nextBoardSheetColumns,
    boardSheetColumnOrder: nextBoardSheetColumnOrder,
    mainOverviewSectionSheetColumns: state.mainOverviewSectionSheetColumns,
    mainOverviewSectionColumnOrder: nextMainOverviewSectionColumnOrder,
    tasks: state.tasks.map((task) => {
      const customFields = { ...(task.customFields ?? {}) };
      const durationFields = { ...(task.durationFields ?? {}) };
      delete customFields[columnId];
      delete durationFields[columnId];
      return { ...task, customFields, durationFields };
    }),
    taskGroups: state.taskGroups.map((group) => {
      if (!group.durationFields?.[columnId]) return group;
      const durationFields = { ...group.durationFields };
      delete durationFields[columnId];
      return { ...group, durationFields };
    }),
  };
}
