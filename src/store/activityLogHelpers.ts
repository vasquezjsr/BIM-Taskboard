import type { ProjectBoardType, SheetColumnDefinition, Task, TaskAttachment, TaskComment, TaskGroup } from '../types';
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
import type { ActivityLogEntry, DeletedColumnArchive, DeletedTaskArchive, TaskRevisionArchive } from '../utils/activityLog';
import {
  appendActivityLogEntry,
  MAX_DELETED_TASK_ARCHIVES,
  MAX_TASK_REVISION_ARCHIVES,
} from '../utils/activityLog';
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

/** Collect a task id and every descendant via parentTaskId. */
export function collectTaskSubtreeIds(tasks: Task[], rootIds: Iterable<string>): Set<string> {
  const byParent = new Map<string | null, Task[]>();
  for (const task of tasks) {
    const key = task.parentTaskId;
    const list = byParent.get(key) ?? [];
    list.push(task);
    byParent.set(key, list);
  }
  const result = new Set<string>();
  const visit = (taskId: string) => {
    if (result.has(taskId)) return;
    result.add(taskId);
    for (const child of byParent.get(taskId) ?? []) {
      visit(child.id);
    }
  };
  for (const rootId of rootIds) visit(rootId);
  return result;
}

export type SoftDeleteTasksResult = {
  tasks: Task[];
  taskAttachments: TaskAttachment[];
  taskComments: TaskComment[];
  deletedTaskArchive: DeletedTaskArchive[];
  activityLog: ActivityLogEntry[];
  removedIds: Set<string>;
};

/**
 * Soft-delete task trees: move tasks + attachments + comments into archives
 * and write Activity Log entries with archiveId for restore.
 */
export function softDeleteTaskTrees(options: {
  tasks: Task[];
  taskAttachments: TaskAttachment[];
  taskComments: TaskComment[];
  deletedTaskArchive: DeletedTaskArchive[];
  activityLog: ActivityLogEntry[];
  rootTaskIds: string[];
  actorId: string | null;
  reason?: string;
  createId: () => string;
  summaryForRoot?: (task: Task, descendantCount: number) => string;
}): SoftDeleteTasksResult {
  const {
    tasks,
    taskAttachments,
    taskComments,
    deletedTaskArchive,
    activityLog,
    rootTaskIds,
    actorId,
    reason = 'user',
    createId,
    summaryForRoot,
  } = options;

  const existing = new Set(tasks.map((task) => task.id));
  const roots = rootTaskIds.filter((id) => existing.has(id));
  if (roots.length === 0) {
    return {
      tasks,
      taskAttachments,
      taskComments,
      deletedTaskArchive,
      activityLog,
      removedIds: new Set(),
    };
  }

  // If parent + child are both selected, only archive the parent tree once.
  const selected = new Set(roots);
  const taskParent = new Map(tasks.map((task) => [task.id, task.parentTaskId] as const));
  const topRoots = roots.filter((id) => {
    let parentId = taskParent.get(id) ?? null;
    while (parentId) {
      if (selected.has(parentId)) return false;
      parentId = taskParent.get(parentId) ?? null;
    }
    return true;
  });

  const removedIds = collectTaskSubtreeIds(tasks, topRoots);
  const removedTasks = tasks.filter((task) => removedIds.has(task.id));
  const removedAttachments = taskAttachments.filter((attachment) => removedIds.has(attachment.taskId));
  const removedComments = taskComments.filter((comment) => removedIds.has(comment.taskId));

  const taskById = new Map(removedTasks.map((task) => [task.id, task]));
  let nextLog = activityLog;
  const newArchives: DeletedTaskArchive[] = [];

  for (const rootId of topRoots) {
    const root = taskById.get(rootId);
    if (!root) continue;
    const treeIds = collectTaskSubtreeIds(removedTasks, [rootId]);
    const treeTasks = removedTasks.filter((task) => treeIds.has(task.id));
    const archiveId = createId();
    const activityLogId = createId();
    const descendantCount = treeTasks.length - 1;
    const summary =
      summaryForRoot?.(root, descendantCount) ??
      (descendantCount > 0
        ? `Deleted task "${root.title}" (+${descendantCount} subtask${descendantCount === 1 ? '' : 's'})`
        : `Deleted task "${root.title}"`);

    newArchives.push({
      id: archiveId,
      deletedAt: new Date().toISOString(),
      deletedById: actorId,
      activityLogId,
      rootTaskId: rootId,
      tasks: treeTasks.map((task) => ({ ...task })),
      attachments: removedAttachments
        .filter((attachment) => treeIds.has(attachment.taskId))
        .map((attachment) => ({
          ...attachment,
          versions: attachment.versions.map((version) => ({ ...version })),
        })),
      comments: removedComments
        .filter((comment) => treeIds.has(comment.taskId))
        .map((comment) => ({ ...comment })),
      reason,
    });

    nextLog = appendActivityLogEntry(
      nextLog,
      {
        actorId,
        action: 'deleted',
        entityType: 'task',
        entityId: rootId,
        summary,
        details: {
          title: root.title,
          boardType: root.boardType,
          projectId: root.projectId,
          subtaskCount: descendantCount,
          reason,
        },
        archiveId,
      },
      () => activityLogId
    );
  }

  return {
    tasks: tasks.filter((task) => !removedIds.has(task.id)),
    taskAttachments: taskAttachments.filter((attachment) => !removedIds.has(attachment.taskId)),
    taskComments: taskComments.filter((comment) => !removedIds.has(comment.taskId)),
    deletedTaskArchive: [...newArchives, ...deletedTaskArchive].slice(0, MAX_DELETED_TASK_ARCHIVES),
    activityLog: nextLog,
    removedIds,
  };
}

export function applyRestoredTaskArchive(
  archive: DeletedTaskArchive,
  state: {
    tasks: Task[];
    taskAttachments: TaskAttachment[];
    taskComments: TaskComment[];
    taskGroups: TaskGroup[];
  }
): {
  tasks: Task[];
  taskAttachments: TaskAttachment[];
  taskComments: TaskComment[];
} | null {
  if (archive.restoredAt || archive.tasks.length === 0) return null;
  const existingIds = new Set(state.tasks.map((task) => task.id));
  if (archive.tasks.some((task) => existingIds.has(task.id))) return null;

  const groupIds = new Set(state.taskGroups.map((group) => group.id));
  const restoredTasks = archive.tasks.map((task) => ({
    ...task,
    groupId: task.groupId && groupIds.has(task.groupId) ? task.groupId : null,
  }));

  const existingAttachmentIds = new Set(state.taskAttachments.map((attachment) => attachment.id));
  const existingCommentIds = new Set(state.taskComments.map((comment) => comment.id));

  return {
    tasks: [...state.tasks, ...restoredTasks],
    taskAttachments: [
      ...state.taskAttachments,
      ...archive.attachments.filter((attachment) => !existingAttachmentIds.has(attachment.id)),
    ],
    taskComments: [
      ...state.taskComments,
      ...archive.comments.filter((comment) => !existingCommentIds.has(comment.id)),
    ],
  };
}

/** Snapshot a task before a change and pair it with a new Activity Log entry id. */
export function buildTaskRevisionArchive(options: {
  task: Task;
  actorId: string | null;
  createId: () => string;
}): { archive: TaskRevisionArchive; activityLogId: string; archiveId: string } {
  const archiveId = options.createId();
  const activityLogId = options.createId();
  return {
    archiveId,
    activityLogId,
    archive: {
      id: archiveId,
      changedAt: new Date().toISOString(),
      changedById: options.actorId,
      activityLogId,
      taskId: options.task.id,
      before: {
        ...options.task,
        assigneeIds: [...(options.task.assigneeIds ?? [])],
        customFields: options.task.customFields ? { ...options.task.customFields } : undefined,
        durationFields: options.task.durationFields ? { ...options.task.durationFields } : undefined,
      },
    },
  };
}

export function prependTaskRevisionArchive(
  archives: TaskRevisionArchive[],
  archive: TaskRevisionArchive
): TaskRevisionArchive[] {
  return [archive, ...archives].slice(0, MAX_TASK_REVISION_ARCHIVES);
}
