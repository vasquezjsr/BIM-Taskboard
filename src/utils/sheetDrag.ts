import type { ProjectBoardType, Task, TaskGroup } from '../types';
import {
  collectDescendantGroupIds,
  getSectionForBoard,
  isSectionUngroupedGroupId,
  isGroupUnderSection,
  type SheetRow,
} from './groupRows';

export const GROUP_DROP_PREFIX = 'group:';
export const TASK_DRAG_PREFIX = 'task:';
export const BOARD_DROP_PREFIX = 'board:';
export const TRASH_DROP_ID = 'trash-drop';

export function taskDragId(taskId: string): string {
  return `${TASK_DRAG_PREFIX}${taskId}`;
}

export function groupDropId(groupId: string): string {
  return `${GROUP_DROP_PREFIX}${groupId}`;
}

export function boardDropId(boardType: ProjectBoardType): string {
  return `${BOARD_DROP_PREFIX}${boardType}`;
}

export function parseTaskDragId(id: string): string | null {
  return id.startsWith(TASK_DRAG_PREFIX) ? id.slice(TASK_DRAG_PREFIX.length) : null;
}

export function parseGroupDropId(id: string): string | null {
  return id.startsWith(GROUP_DROP_PREFIX) ? id.slice(GROUP_DROP_PREFIX.length) : null;
}

export function parseBoardDropId(id: string): ProjectBoardType | null {
  if (!id.startsWith(BOARD_DROP_PREFIX)) return null;
  return id.slice(BOARD_DROP_PREFIX.length) as ProjectBoardType;
}

function isVirtualGroupId(groupId: string): boolean {
  return groupId.startsWith('__');
}

export function findSectionBoardType(groups: TaskGroup[], groupId: string | null): ProjectBoardType | null {
  if (!groupId) return null;
  let current = groups.find((g) => g.id === groupId);
  while (current) {
    if (current.tier === 'section' && current.sectionBoardType) {
      return current.sectionBoardType;
    }
    current = current.parentId ? groups.find((g) => g.id === current!.parentId) : undefined;
  }
  return null;
}

function ghostUngroupedBoardType(groupId: string): ProjectBoardType | null {
  const match = groupId.match(/^__ghost-ungrouped-(.+)__$/);
  if (!match) return null;
  return match[1] as ProjectBoardType;
}

/** Sync branch (boardType) from group placement when a task is moved under a section. */
export function syncTaskBoardFromGroupPlacement(groups: TaskGroup[], tasks: Task[]): Task[] {
  return tasks.map((task) => {
    if (!task.groupId || task.boardType === 'employee') return task;
    const sectionBoard = findSectionBoardType(groups, task.groupId);
    if (sectionBoard && task.boardType !== sectionBoard) {
      return { ...task, boardType: sectionBoard };
    }
    return task;
  });
}

function sameSheetBucket(
  task: Task,
  groupId: string | null,
  bucketBoardType: ProjectBoardType | 'main' | 'employee',
  isOverview: boolean
): boolean {
  if (task.groupId !== groupId) return false;
  if (!isOverview) return task.boardType === bucketBoardType;
  if (groupId !== null) return true;
  return task.boardType === bucketBoardType;
}

function resolveBucketBoardType(
  isOverview: boolean,
  boardType: ProjectBoardType,
  targetGroupId: string | null,
  overGroupRaw: string | null,
  taskGroups: TaskGroup[],
  referenceTask?: Task
): ProjectBoardType | 'main' {
  if (!isOverview) return boardType;
  if (targetGroupId) {
    return findSectionBoardType(taskGroups, targetGroupId) ?? (referenceTask?.boardType as ProjectBoardType) ?? 'main';
  }
  if (overGroupRaw === '__general__' || overGroupRaw === '__ungrouped__') return 'main';
  if (overGroupRaw) {
    const ghostBoard = ghostUngroupedBoardType(overGroupRaw);
    if (ghostBoard) return ghostBoard;
  }
  if (referenceTask?.boardType && referenceTask.boardType !== 'employee') {
    return referenceTask.boardType;
  }
  return 'main';
}

function sortByPriority(tasks: Task[]): Task[] {
  return [...tasks].sort(
    (a, b) => a.priority - b.priority || a.createdAt.localeCompare(b.createdAt)
  );
}

export interface SheetTaskUpdate {
  id: string;
  groupId: string | null;
  priority: number;
  boardType?: ProjectBoardType;
}

export interface SheetGroupUpdate {
  id: string;
  parentId: string | null;
  sortOrder: number;
  tier?: TaskGroup['tier'];
}

export type SheetGroupDropMode = 'nest' | 'reorder';

export type DropPlacement = 'before' | 'after' | 'inside';

export type SheetDropIntent = 'reorder' | 'regroup';

export interface SheetDropHint {
  targetId: string;
  targetKind: 'group' | 'task';
  intent: SheetDropIntent;
  placement: DropPlacement;
}

export function placementFromPointer(
  pointerY: number,
  rect: { top: number; height: number },
  edgeRatio = 0.35
): DropPlacement {
  const relative = pointerY - rect.top;
  const ratio = rect.height > 0 ? relative / rect.height : 0.5;
  if (ratio < edgeRatio) return 'before';
  if (ratio > 1 - edgeRatio) return 'after';
  return 'inside';
}

export function dropIntentLabel(intent: SheetDropIntent): string {
  return intent === 'regroup' ? 'Move into group' : 'Reorder';
}

function sortGroupsByOrder(groups: TaskGroup[]): TaskGroup[] {
  return [...groups].sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
}

function isMovableSheetGroup(group: TaskGroup | undefined): group is TaskGroup {
  if (!group) return false;
  if (group.id.startsWith('__')) return false;
  if (group.tier === 'section') return false;
  return true;
}

function isGroupDropTarget(group: TaskGroup | undefined): group is TaskGroup {
  if (!group) return false;
  if (group.id.startsWith('__')) return false;
  return true;
}

function expectedGroupTierUnderParent(
  parent: TaskGroup | undefined
): TaskGroup['tier'] | undefined {
  if (!parent) return undefined;
  if (parent.tier === 'section') return 'parent';
  if (parent.tier === 'parent') return 'child';
  if (parent.tier === 'child') return 'parent';
  return undefined;
}

function tierAfterReparent(active: TaskGroup, newParent: TaskGroup): TaskGroup['tier'] | undefined {
  const expected = expectedGroupTierUnderParent(newParent);
  if (!expected || active.tier === expected) return undefined;
  return expected;
}

export interface TaskDropTarget {
  intent: SheetDropIntent;
  targetGroupId: string | null;
  insertBeforeTaskId: string | null;
  insertAfterTaskId: string | null;
}

function groupIdFromSheetRow(row: SheetRow): string | null {
  if (row.type !== 'group') return null;
  if (row.group.id.startsWith('__') || isSectionUngroupedGroupId(row.group.id)) return null;
  return row.group.id;
}

function walkSheetRowsToContainingGroup(
  sheetRows: SheetRow[],
  fromRowIndex: number
): string | null {
  for (let i = fromRowIndex; i >= 0; i--) {
    const groupId = groupIdFromSheetRow(sheetRows[i]!);
    if (groupId) return groupId;
  }
  return null;
}

/** Resolve task drop: task rows reorder; group headers regroup. */
export function resolveTaskDropTarget(
  overId: string,
  activeTaskIds: string[],
  tasks: Task[],
  _sheetRows: SheetRow[],
  placement: DropPlacement = 'before'
): TaskDropTarget | null {
  const movingSet = new Set(activeTaskIds);
  const leadTask = tasks.find((task) => task.id === activeTaskIds[0]);

  const directGroupId = parseGroupDropId(overId);
  if (directGroupId) {
    if (isVirtualGroupId(directGroupId) || isSectionUngroupedGroupId(directGroupId)) {
      return {
        intent: 'regroup',
        targetGroupId: null,
        insertBeforeTaskId: null,
        insertAfterTaskId: null,
      };
    }
    const groupTasks = sortByPriority(
      tasks.filter((task) => task.groupId === directGroupId && !task.parentTaskId)
    );
    const firstTask = groupTasks[0];
    const lastTask = groupTasks[groupTasks.length - 1];
    if (placement === 'before' && firstTask) {
      return {
        intent: 'regroup',
        targetGroupId: directGroupId,
        insertBeforeTaskId: firstTask.id,
        insertAfterTaskId: null,
      };
    }
    if (placement === 'after' && lastTask) {
      return {
        intent: 'regroup',
        targetGroupId: directGroupId,
        insertBeforeTaskId: null,
        insertAfterTaskId: lastTask.id,
      };
    }
    return {
      intent: 'regroup',
      targetGroupId: directGroupId,
      insertBeforeTaskId: null,
      insertAfterTaskId: null,
    };
  }

  const overTaskId = parseTaskDragId(overId);
  if (!overTaskId || !leadTask) return null;

  if (movingSet.has(overTaskId)) {
    return {
      intent: 'reorder',
      targetGroupId: leadTask.groupId,
      insertBeforeTaskId: null,
      insertAfterTaskId: null,
    };
  }

  return {
    intent: 'reorder',
    targetGroupId: leadTask.groupId,
    insertBeforeTaskId: placement === 'before' ? overTaskId : null,
    insertAfterTaskId: placement === 'before' ? null : overTaskId,
  };
}

export interface GroupDropAction {
  mode: SheetGroupDropMode;
  targetOverId: string;
  reorderPlacement: DropPlacement;
  /** When set, the drop indicator renders on this row instead of the compute target. */
  hintTargetGroupId?: string;
  hintPlacement?: DropPlacement;
}

export type GroupDropActionResult = GroupDropAction | { blockedReason: string };

function groupHasChildGroups(taskGroups: TaskGroup[], groupId: string): boolean {
  return taskGroups.some(
    (group) => group.parentId === groupId && !group.id.startsWith('__')
  );
}

export function isSheetGroupContainer(
  taskGroups: TaskGroup[],
  group: TaskGroup | undefined
): boolean {
  if (!group) return false;
  return group.tier === 'section' || groupHasChildGroups(taskGroups, group.id);
}

/** Sibling leaf → reorder line; container center → nest; container edge → reorder. */
export function resolveGroupDropAction(
  overId: string,
  activeGroupId: string,
  placement: DropPlacement,
  taskGroups: TaskGroup[],
  tasks: Task[],
  sheetRows: SheetRow[]
): GroupDropActionResult | null {
  const active = taskGroups.find((group) => group.id === activeGroupId);
  if (!active) return null;

  const buildReorder = (
    overGroupId: string,
    reorderPlacement: DropPlacement
  ): GroupDropAction | null => {
    const over = taskGroups.find((group) => group.id === overGroupId);
    if (!over || over.id === active.id) return null;
    if (collectDescendantGroupIds(taskGroups, active.id).includes(over.id)) {
      return null;
    }
    if (active.parentId !== over.parentId) {
      return null;
    }
    return {
      mode: 'reorder',
      targetOverId: groupDropId(over.id),
      reorderPlacement: reorderPlacement === 'before' ? 'before' : 'after',
    };
  };

  const buildNest = (overGroupId: string): GroupDropActionResult | null => {
    if (collectDescendantGroupIds(taskGroups, active.id).includes(overGroupId)) {
      return { blockedReason: 'Cannot move a group into one of its own sub-groups.' };
    }
    const { targetOverId, blockedReason } = resolveGroupDropTargetForMove(
      groupDropId(overGroupId),
      taskGroups,
      activeGroupId,
      tasks,
      sheetRows
    );
    if (!targetOverId) {
      return blockedReason ? { blockedReason } : null;
    }
    return { mode: 'nest', targetOverId, reorderPlacement: 'inside' };
  };

  const resolveOverGroup = (overGroupId: string): GroupDropActionResult | null => {
    const over = taskGroups.find((group) => group.id === overGroupId);
    if (!over) return null;

    const sameParent = active.parentId === over.parentId && over.id !== active.id;
    const overIsContainer = groupHasChildGroups(taskGroups, overGroupId);
    const edgePlacement = placement === 'before' ? 'before' : 'after';

    // Section header: reorder among section children — never "nest into section"
    if (over.tier === 'section') {
      const sectionChildren = sortGroupsByOrder(
        taskGroups.filter(
          (group) =>
            group.parentId === overGroupId &&
            group.id !== active.id &&
            !group.id.startsWith('__')
        )
      );
      if (sectionChildren.length === 0) return null;

      const activeInSection =
        active.parentId === overGroupId || isGroupUnderSection(taskGroups, active.id, overGroupId);

      if (activeInSection) {
        if (placement === 'before' || placement === 'inside') {
          return {
            mode: 'reorder',
            targetOverId: groupDropId(sectionChildren[0]!.id),
            reorderPlacement: 'before',
          };
        }
        if (placement === 'after') {
          const last = sectionChildren[sectionChildren.length - 1]!;
          const reorder = buildReorder(last.id, 'after');
          return (
            reorder ?? {
              mode: 'reorder',
              targetOverId: groupDropId(last.id),
              reorderPlacement: 'after',
            }
          );
        }
        return null;
      }

      if (placement === 'inside') return buildNest(overGroupId);
      return null;
    }

    // Reorder among children via parent container header edges
    if (active.parentId === overGroupId && overIsContainer) {
      if (placement === 'inside') {
        return buildNest(overGroupId);
      }
      const siblings = sortGroupsByOrder(
        taskGroups.filter(
          (group) => group.parentId === overGroupId && group.id !== activeGroupId
        )
      );
      if (placement === 'after' && siblings[siblings.length - 1]) {
        return buildReorder(siblings[siblings.length - 1]!.id, 'after');
      }
      if (siblings[0]) {
        return buildReorder(siblings[0]!.id, 'before');
      }
      return null;
    }

    // Same parent: edges reorder; center drop nests into the hovered group
    if (sameParent) {
      if (placement === 'inside') {
        const nested = buildNest(overGroupId);
        if (nested) return nested;
      }
      return buildReorder(overGroupId, edgePlacement);
    }

    // Different branch: always nest into the hovered group (e.g. level → another trade group).
    const nested = buildNest(overGroupId);
    if (nested) return nested;
    return null;
  };

  const directGroupId = parseGroupDropId(overId);
  if (directGroupId) {
    return resolveOverGroup(directGroupId);
  }

  const overTaskId = parseTaskDragId(overId);
  if (overTaskId) {
    const rowIndex = sheetRows.findIndex(
      (row) => row.type === 'task' && row.task.id === overTaskId
    );
    const containingGroupId =
      rowIndex !== -1
        ? walkSheetRowsToContainingGroup(sheetRows, rowIndex)
        : tasks.find((task) => task.id === overTaskId)?.groupId ?? null;
    if (!containingGroupId) return null;
    return resolveOverGroup(containingGroupId);
  }

  return null;
}

/** Resolve a valid group drop target when moving a group row. */
export function resolveGroupDropTargetForMove(
  overId: string,
  taskGroups: TaskGroup[],
  activeGroupId: string,
  tasks: Task[],
  sheetRows: SheetRow[] = []
): { targetOverId: string | null; blockedReason: string | null } {
  const isBlockedTarget = (groupId: string | null): string | null => {
    if (!groupId || groupId === activeGroupId || groupId.startsWith('__')) return 'invalid';
    if (collectDescendantGroupIds(taskGroups, activeGroupId).includes(groupId)) {
      return 'descendant';
    }
    return null;
  };

  const acceptGroupId = (groupId: string | null): string | null => {
    const blocked = isBlockedTarget(groupId);
    if (blocked === 'descendant') return null;
    if (blocked) return null;
    return groupId ? groupDropId(groupId) : null;
  };

  const directGroupId = parseGroupDropId(overId);
  if (directGroupId) {
    const accepted = acceptGroupId(directGroupId);
    if (!accepted && isBlockedTarget(directGroupId) === 'descendant') {
      return {
        targetOverId: null,
        blockedReason: 'Cannot move a group into one of its own sub-groups.',
      };
    }
    if (accepted) return { targetOverId: accepted, blockedReason: null };
  }

  const walkSheetRowsToGroupTarget = (fromRowIndex: number) => {
    for (let i = fromRowIndex; i >= 0; i--) {
      const row = sheetRows[i];
      if (row?.type !== 'group') continue;
      const blocked = isBlockedTarget(row.group.id);
      if (blocked === 'descendant') {
        return {
          targetOverId: null as string | null,
          blockedReason: 'Cannot move a group into one of its own sub-groups.',
        };
      }
      if (blocked === 'invalid' && row.group.id === activeGroupId) continue;
      if (blocked) continue;
      const accepted = acceptGroupId(row.group.id);
      if (accepted) return { targetOverId: accepted, blockedReason: null };
    }
    return null;
  };

  const taskId = parseTaskDragId(overId);
  if (taskId) {
    const rowIndex = sheetRows.findIndex(
      (row) => row.type === 'task' && row.task.id === taskId
    );
    if (rowIndex !== -1) {
      const walked = walkSheetRowsToGroupTarget(rowIndex);
      if (walked) return walked;
    }

    const task = tasks.find((entry) => entry.id === taskId);
    if (task?.groupId && task.groupId !== activeGroupId) {
      const accepted = acceptGroupId(task.groupId);
      if (accepted) return { targetOverId: accepted, blockedReason: null };
    }
  }

  return { targetOverId: null, blockedReason: null };
}

/** Resolve a drag-over id to a concrete group drop target for task moves. */
export function resolveGroupDropOverId(
  overId: string,
  sheetRows: SheetRow[],
  activeGroupId: string,
  tasks: Task[]
): string | null {
  const directGroupId = parseGroupDropId(overId);
  if (
    directGroupId &&
    directGroupId !== activeGroupId &&
    !directGroupId.startsWith('__')
  ) {
    return overId;
  }

  const taskId = parseTaskDragId(overId);
  if (taskId) {
    const task = tasks.find((entry) => entry.id === taskId);
    if (task?.groupId && task.groupId !== activeGroupId) {
      return groupDropId(task.groupId);
    }

    const rowIndex = sheetRows.findIndex(
      (row) => row.type === 'task' && row.task.id === taskId
    );
    if (rowIndex !== -1) {
      for (let i = rowIndex; i >= 0; i--) {
        const row = sheetRows[i];
        if (
          row?.type === 'group' &&
          row.group.id !== activeGroupId &&
          !row.group.id.startsWith('__')
        ) {
          return groupDropId(row.group.id);
        }
      }
    }
  }

  return null;
}

function filterTopLevelMovingGroups(
  taskGroups: TaskGroup[],
  movingIds: string[]
): TaskGroup[] {
  const selectedSet = new Set(movingIds);
  const groups = movingIds
    .map((id) => taskGroups.find((group) => group.id === id))
    .filter((group): group is TaskGroup => isMovableSheetGroup(group));

  return groups.filter((group) => {
    let parentId = group.parentId;
    while (parentId) {
      if (selectedSet.has(parentId)) return false;
      parentId = taskGroups.find((entry) => entry.id === parentId)?.parentId ?? null;
    }
    return true;
  });
}

function orderGroupsByIdList(groups: TaskGroup[], orderedIds: string[]): TaskGroup[] {
  const orderMap = new Map(orderedIds.map((id, index) => [id, index]));
  return [...groups].sort(
    (a, b) => (orderMap.get(a.id) ?? 0) - (orderMap.get(b.id) ?? 0)
  );
}

function reindexGroupSiblings(
  taskGroups: TaskGroup[],
  projectId: string,
  parentId: string | null,
  excludeIds: Set<string>
): SheetGroupUpdate[] {
  const siblings = sortGroupsByOrder(
    taskGroups.filter(
      (group) =>
        group.projectId === projectId &&
        group.parentId === parentId &&
        !excludeIds.has(group.id)
    )
  );
  return siblings.map((group, index) => ({
    id: group.id,
    parentId: group.parentId,
    sortOrder: index,
  }));
}

function computeBulkNestGroups(
  taskGroups: TaskGroup[],
  projectId: string,
  moving: TaskGroup[],
  over: TaskGroup
): SheetGroupUpdate[] {
  const newParentId = over.id;
  const movingIds = new Set(moving.map((group) => group.id));
  const updates: SheetGroupUpdate[] = [];

  const oldParentIds = new Set(moving.map((group) => group.parentId));
  for (const oldParentId of oldParentIds) {
    updates.push(...reindexGroupSiblings(taskGroups, projectId, oldParentId, movingIds));
  }

  const existingChildren = sortGroupsByOrder(
    taskGroups.filter(
      (group) =>
        group.projectId === projectId &&
        group.parentId === newParentId &&
        !movingIds.has(group.id)
    )
  );

  const allChildren = [...existingChildren, ...moving];
  for (const [index, group] of allChildren.entries()) {
    const nextTier = movingIds.has(group.id) ? tierAfterReparent(group, over) : undefined;
    updates.push({
      id: group.id,
      parentId: newParentId,
      sortOrder: index,
      ...(nextTier ? { tier: nextTier } : {}),
    });
  }

  return updates;
}

function computeBulkReorderGroups(
  taskGroups: TaskGroup[],
  projectId: string,
  moving: TaskGroup[],
  over: TaskGroup,
  reorderPlacement: DropPlacement
): SheetGroupUpdate[] | null {
  const targetParentId = over.parentId;
  const movingIds = new Set(moving.map((group) => group.id));

  for (const group of moving) {
    if (
      targetParentId &&
      collectDescendantGroupIds(taskGroups, group.id).includes(targetParentId)
    ) {
      return null;
    }
  }

  const targetParent = targetParentId
    ? taskGroups.find((group) => group.id === targetParentId && group.projectId === projectId)
    : undefined;

  const siblings = sortGroupsByOrder(
    taskGroups.filter(
      (group) =>
        group.projectId === projectId &&
        group.parentId === targetParentId &&
        !movingIds.has(group.id)
    )
  );

  const overIdx = siblings.findIndex((group) => group.id === over.id);
  if (overIdx === -1) return null;

  const insertIdx = reorderPlacement === 'after' ? overIdx + 1 : overIdx;
  const nextSiblings = [...siblings];
  nextSiblings.splice(insertIdx, 0, ...moving);

  const updates: SheetGroupUpdate[] = [];
  const oldParentIds = new Set(
    moving
      .filter((group) => group.parentId !== targetParentId)
      .map((group) => group.parentId)
  );
  for (const oldParentId of oldParentIds) {
    updates.push(...reindexGroupSiblings(taskGroups, projectId, oldParentId, movingIds));
  }

  const siblingTier = expectedGroupTierUnderParent(targetParent);
  for (const [index, group] of nextSiblings.entries()) {
    const reparentTier =
      targetParent && group.parentId !== targetParentId
        ? tierAfterReparent(group, targetParent)
        : undefined;
    const reorderTier =
      !reparentTier && siblingTier && group.tier !== siblingTier ? siblingTier : undefined;
    const nextTier = reparentTier ?? reorderTier;

    updates.push({
      id: group.id,
      parentId: targetParentId,
      sortOrder: index,
      ...(movingIds.has(group.id) && nextTier ? { tier: nextTier } : {}),
    });
  }

  return updates;
}

export function computeSheetGroupsDrop(
  taskGroups: TaskGroup[],
  projectId: string,
  activeGroupIds: string[],
  overId: string,
  mode: SheetGroupDropMode = 'nest',
  reorderPlacement: DropPlacement = 'before'
): SheetGroupUpdate[] | null {
  const uniqueIds = [...new Set(activeGroupIds)];
  if (uniqueIds.length === 0) return null;
  if (uniqueIds.length === 1) {
    return computeSheetGroupDrop(
      taskGroups,
      projectId,
      uniqueIds[0]!,
      overId,
      mode,
      reorderPlacement
    );
  }

  const overGroupRaw = parseGroupDropId(overId);
  if (!overGroupRaw || overGroupRaw.startsWith('__')) return null;

  const over = taskGroups.find((group) => group.id === overGroupRaw && group.projectId === projectId);
  if (!isGroupDropTarget(over)) return null;

  const moving = orderGroupsByIdList(
    filterTopLevelMovingGroups(taskGroups, uniqueIds)
      .filter((group) => group.id !== over.id)
      .filter((group) => !collectDescendantGroupIds(taskGroups, group.id).includes(over.id)),
    uniqueIds
  );

  if (moving.length === 0) return null;

  if (mode === 'nest') {
    return computeBulkNestGroups(taskGroups, projectId, moving, over);
  }

  if (moving.some((group) => group.parentId !== over.parentId)) {
    return computeBulkNestGroups(taskGroups, projectId, moving, over);
  }

  return computeBulkReorderGroups(taskGroups, projectId, moving, over, reorderPlacement);
}

export function computeSheetGroupDrop(
  taskGroups: TaskGroup[],
  projectId: string,
  activeGroupId: string,
  overId: string,
  mode: SheetGroupDropMode = 'nest',
  reorderPlacement: DropPlacement = 'before'
): SheetGroupUpdate[] | null {
  const active = taskGroups.find((g) => g.id === activeGroupId && g.projectId === projectId);
  if (!isMovableSheetGroup(active)) return null;

  const overGroupRaw = parseGroupDropId(overId);
  if (!overGroupRaw || overGroupRaw.startsWith('__')) return null;

  const over = taskGroups.find((g) => g.id === overGroupRaw && g.projectId === projectId);
  if (!isGroupDropTarget(over) || over.id === active.id) return null;
  if (collectDescendantGroupIds(taskGroups, active.id).includes(over.id)) return null;

  if (mode === 'reorder') {
    if (active.parentId !== over.parentId) {
      return computeSheetGroupDrop(
        taskGroups,
        projectId,
        activeGroupId,
        overId,
        'nest',
        reorderPlacement
      );
    }

    const targetParentId = over.parentId;
    if (
      targetParentId &&
      collectDescendantGroupIds(taskGroups, active.id).includes(targetParentId)
    ) {
      return null;
    }

    const targetParent = targetParentId
      ? taskGroups.find((g) => g.id === targetParentId && g.projectId === projectId)
      : undefined;

    const siblings = sortGroupsByOrder(
      taskGroups.filter(
        (g) =>
          g.projectId === projectId &&
          g.parentId === targetParentId &&
          g.id !== active.id
      )
    );
    const overIdx = siblings.findIndex((g) => g.id === over.id);
    if (overIdx === -1) return null;
    const insertIdx = reorderPlacement === 'after' ? overIdx + 1 : overIdx;
    siblings.splice(insertIdx, 0, active);

    const updates: SheetGroupUpdate[] = [];
    const reparentTier =
      targetParent && active.parentId !== targetParentId
        ? tierAfterReparent(active, targetParent)
        : undefined;
    const siblingTier = expectedGroupTierUnderParent(targetParent);
    const reorderTier =
      !reparentTier && siblingTier && active.tier !== siblingTier ? siblingTier : undefined;
    const nextTier = reparentTier ?? reorderTier;

    if (active.parentId !== targetParentId) {
      const oldSiblings = sortGroupsByOrder(
        taskGroups.filter(
          (g) =>
            g.projectId === projectId &&
            g.parentId === active.parentId &&
            g.id !== active.id
        )
      );
      for (const [index, group] of oldSiblings.entries()) {
        updates.push({ id: group.id, parentId: group.parentId, sortOrder: index });
      }
    }

    for (const [index, group] of siblings.entries()) {
      updates.push({
        id: group.id,
        parentId: targetParentId,
        sortOrder: index,
        ...(group.id === active.id && nextTier ? { tier: nextTier } : {}),
      });
    }

    return updates;
  }

  const newParentId = over.id;
  const nextTier = tierAfterReparent(active, over);
  const newSiblings = sortGroupsByOrder(
    taskGroups.filter(
      (g) => g.projectId === projectId && g.parentId === newParentId && g.id !== active.id
    )
  );

  const updates: SheetGroupUpdate[] = [];

  if (active.parentId !== newParentId) {
    const oldSiblings = sortGroupsByOrder(
      taskGroups.filter(
        (g) =>
          g.projectId === projectId && g.parentId === active.parentId && g.id !== active.id
      )
    );
    for (const [index, group] of oldSiblings.entries()) {
      updates.push({ id: group.id, parentId: group.parentId, sortOrder: index });
    }
  }

  for (const [index, group] of [...newSiblings, active].entries()) {
    updates.push({
      id: group.id,
      parentId: newParentId,
      sortOrder: index,
      ...(group.id === active.id && nextTier ? { tier: nextTier } : {}),
    });
  }

  return updates;
}

/** Leaf group with tasks directly assigned — nest by moving tasks into the target group. */
export function shouldMergeGroupOnNest(
  taskGroups: TaskGroup[],
  tasks: Task[],
  activeGroupId: string
): boolean {
  const hasChildGroups = taskGroups.some(
    (group) => group.parentId === activeGroupId && !group.id.startsWith('__')
  );
  if (hasChildGroups) return false;
  return tasks.some((task) => task.groupId === activeGroupId && !task.parentTaskId);
}

export interface SheetGroupMergeUpdate {
  removeGroupId: string;
  sourceParentId: string | null;
  targetGroupId: string;
  taskUpdates: SheetTaskUpdate[];
  siblingGroupUpdates: SheetGroupUpdate[];
}

export function computeSheetGroupMerge(
  taskGroups: TaskGroup[],
  tasks: Task[],
  projectId: string,
  activeGroupId: string,
  overId: string
): SheetGroupMergeUpdate | null {
  const active = taskGroups.find((g) => g.id === activeGroupId && g.projectId === projectId);
  if (!isMovableSheetGroup(active)) return null;
  if (!shouldMergeGroupOnNest(taskGroups, tasks, activeGroupId)) return null;

  const overGroupRaw = parseGroupDropId(overId);
  if (!overGroupRaw || overGroupRaw.startsWith('__')) return null;

  const over = taskGroups.find((g) => g.id === overGroupRaw && g.projectId === projectId);
  if (!isGroupDropTarget(over) || over.id === active.id) return null;
  if (collectDescendantGroupIds(taskGroups, active.id).includes(over.id)) return null;

  const targetGroupId = over.id;
  const targetRootTasks = sortByPriority(
    tasks.filter(
      (task) =>
        task.projectId === projectId && task.groupId === targetGroupId && !task.parentTaskId
    )
  );
  const sourceRootTasks = sortByPriority(
    tasks.filter(
      (task) =>
        task.projectId === projectId && task.groupId === activeGroupId && !task.parentTaskId
    )
  );

  const taskUpdates: SheetTaskUpdate[] = [
    ...targetRootTasks.map((task, priority) => ({
      id: task.id,
      groupId: targetGroupId,
      priority,
    })),
    ...sourceRootTasks.map((task, index) => ({
      id: task.id,
      groupId: targetGroupId,
      priority: targetRootTasks.length + index,
    })),
  ];

  const oldSiblings = sortGroupsByOrder(
    taskGroups.filter(
      (group) =>
        group.projectId === projectId &&
        group.parentId === active.parentId &&
        group.id !== activeGroupId
    )
  );

  return {
    removeGroupId: activeGroupId,
    sourceParentId: active.parentId,
    targetGroupId,
    taskUpdates,
    siblingGroupUpdates: oldSiblings.map((group, index) => ({
      id: group.id,
      parentId: group.parentId,
      sortOrder: index,
    })),
  };
}

export function computeSheetTaskDrop(
  tasks: Task[],
  taskGroups: TaskGroup[],
  projectId: string,
  boardType: ProjectBoardType,
  activeTaskId: string,
  target: TaskDropTarget
): SheetTaskUpdate[] | null {
  return computeSheetTasksDrop(tasks, taskGroups, projectId, boardType, [activeTaskId], target);
}

export function computeSheetTasksDrop(
  tasks: Task[],
  taskGroups: TaskGroup[],
  projectId: string,
  boardType: ProjectBoardType,
  activeTaskIds: string[],
  target: TaskDropTarget
): SheetTaskUpdate[] | null {
  const movingIds = activeTaskIds.filter((id) =>
    tasks.some((task) => task.id === id && task.projectId === projectId)
  );
  if (movingIds.length === 0) return null;

  const movingSet = new Set(movingIds);
  const isOverview = boardType === 'main';
  const leadTask = tasks.find((task) => task.id === movingIds[0] && task.projectId === projectId);
  if (!leadTask) return null;

  const regroup = target.intent === 'regroup';
  const targetGroupId = regroup ? target.targetGroupId : leadTask.groupId;
  const insertBeforeTaskId = target.insertBeforeTaskId;
  const insertAfterTaskId = target.insertAfterTaskId;

  let nextBoardType = leadTask.boardType;
  const referenceTask = insertBeforeTaskId
    ? tasks.find((task) => task.id === insertBeforeTaskId)
    : undefined;
  const bucketBoardType = resolveBucketBoardType(
    isOverview,
    boardType,
    targetGroupId,
    targetGroupId,
    taskGroups,
    referenceTask
  );

  if (isOverview && typeof leadTask.boardType === 'string') {
    nextBoardType = regroup ? bucketBoardType : leadTask.boardType;
  } else if (!isOverview) {
    nextBoardType = boardType;
  }

  const movingTasks = sortByPriority(
    tasks.filter((task) => task.projectId === projectId && movingSet.has(task.id))
  ).map((task) => ({
    ...task,
    groupId: targetGroupId,
    boardType: nextBoardType as Task['boardType'],
  }));

  const bucketTasks = sortByPriority(
    tasks.filter(
      (task) =>
        task.projectId === projectId &&
        task.boardType !== 'employee' &&
        sameSheetBucket(task, targetGroupId, bucketBoardType, isOverview) &&
        !movingSet.has(task.id)
    )
  );

  if (insertBeforeTaskId) {
    const fullBucket = sortByPriority(
      tasks.filter(
        (task) =>
          task.projectId === projectId &&
          task.boardType !== 'employee' &&
          sameSheetBucket(task, targetGroupId, bucketBoardType, isOverview)
      )
    );
    const insertIndex = fullBucket.findIndex((task) => task.id === insertBeforeTaskId);
    if (insertIndex === -1) {
      bucketTasks.push(...movingTasks);
    } else {
      let targetIndex = 0;
      for (let i = 0; i < insertIndex; i++) {
        if (!movingSet.has(fullBucket[i]!.id)) targetIndex += 1;
      }
      bucketTasks.splice(targetIndex, 0, ...movingTasks);
    }
  } else if (insertAfterTaskId) {
    const fullBucket = sortByPriority(
      tasks.filter(
        (task) =>
          task.projectId === projectId &&
          task.boardType !== 'employee' &&
          sameSheetBucket(task, targetGroupId, bucketBoardType, isOverview)
      )
    );
    const afterIndex = fullBucket.findIndex((task) => task.id === insertAfterTaskId);
    if (afterIndex === -1) {
      bucketTasks.push(...movingTasks);
    } else {
      let targetIndex = 0;
      for (let i = 0; i <= afterIndex; i++) {
        if (!movingSet.has(fullBucket[i]!.id)) targetIndex += 1;
      }
      bucketTasks.splice(targetIndex, 0, ...movingTasks);
    }
  } else {
    bucketTasks.push(...movingTasks);
  }

  return bucketTasks.map((task, priority) => ({
    id: task.id,
    groupId: targetGroupId,
    priority,
    ...(movingSet.has(task.id) && regroup && nextBoardType !== 'employee'
      ? { boardType: nextBoardType as ProjectBoardType }
      : {}),
  }));
}

export function getSheetDragIds(sheetRows: SheetRow[]): string[] {
  const ids: string[] = [];
  for (const row of sheetRows) {
    if (row.type === 'group') {
      ids.push(groupDropId(row.group.id));
    }
    if (row.type === 'task') {
      ids.push(taskDragId(row.task.id));
    }
  }
  return ids;
}

export function collectTaskIdsInGroupSubtrees(
  taskGroups: TaskGroup[],
  rootGroupIds: string[],
  tasks: Task[]
): string[] {
  const groupIds = new Set<string>();
  for (const rootId of rootGroupIds) {
    groupIds.add(rootId);
    for (const descId of collectDescendantGroupIds(taskGroups, rootId)) {
      groupIds.add(descId);
    }
  }
  return tasks
    .filter((task) => task.groupId && groupIds.has(task.groupId))
    .map((task) => task.id);
}

/** Reparent selected groups onto a board section (Main Overview hierarchy). */
export function computeMoveGroupsToBoard(
  taskGroups: TaskGroup[],
  projectId: string,
  clientId: string,
  groupIds: string[],
  targetBoardType: ProjectBoardType
): SheetGroupUpdate[] | null {
  const section = getSectionForBoard(taskGroups, clientId, projectId, targetBoardType);
  if (!section) return null;

  const moving = orderGroupsByIdList(
    filterTopLevelMovingGroups(taskGroups, groupIds).filter(
      (group) => group.clientId === clientId && group.projectId === projectId
    ),
    groupIds
  );
  if (moving.length === 0) return null;

  const movingIds = new Set(moving.map((group) => group.id));
  const updates: SheetGroupUpdate[] = [];

  const oldParentIds = new Set(
    moving
      .map((group) => group.parentId)
      .filter((parentId): parentId is string => parentId !== null && parentId !== section.id)
  );
  for (const oldParentId of oldParentIds) {
    updates.push(...reindexGroupSiblings(taskGroups, projectId, oldParentId, movingIds));
  }

  let nextOrder = sortGroupsByOrder(
    taskGroups.filter(
      (group) =>
        group.projectId === projectId &&
        group.parentId === section.id &&
        !movingIds.has(group.id) &&
        !group.id.startsWith('__')
    )
  ).length;

  for (const group of moving) {
    const needsReparent = group.parentId !== section.id;
    updates.push({
      id: group.id,
      parentId: section.id,
      sortOrder: nextOrder++,
      ...(needsReparent ? { tier: 'parent' as const } : {}),
    });
  }

  return updates;
}
