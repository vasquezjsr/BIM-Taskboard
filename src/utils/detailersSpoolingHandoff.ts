import type { ProjectBoardType, Task, TaskGroup } from '../types';

/** Marks a Spooling-board task that should still appear on Detailers. */
export const DETAILERS_MIRROR_FIELD = 'bbMirrorDetailers';

/** Detailers level/trade group to keep using while the task lives on Spooling. */
export const DETAILERS_MIRROR_GROUP_FIELD = 'bbDetailersGroupId';

/** Spooling-phase statuses Detailers should still see for process tracking. */
export const DETAILERS_MIRROR_SPOOLING_STATUSES = new Set([
  'ready-for-spooling',
  'spool-in-progress',
  'spool-qa-review',
  'spool-approved',
  'ready-for-fab',
  'detailer-review',
  'fix-mark-ups',
  'on-hold',
]);

export function isDetailersMirroredSpoolingTask(task: Task | null | undefined): boolean {
  if (!task) return false;
  if (task.boardType !== 'spooling') return false;
  if (task.customFields?.[DETAILERS_MIRROR_FIELD] !== '1') return false;
  return DETAILERS_MIRROR_SPOOLING_STATUSES.has(task.status);
}

/**
 * Package is in the Detailers↔Spooling dual-visibility phase — even if boardType
 * was incorrectly flipped back to Detailers by group sync.
 */
export function isDetailersSpoolingHandoffPackage(task: Task | null | undefined): boolean {
  if (!task || task.parentTaskId) return false;
  if (!DETAILERS_MIRROR_SPOOLING_STATUSES.has(task.status)) return false;
  if (task.boardType === 'spooling') {
    return (
      task.customFields?.[DETAILERS_MIRROR_FIELD] === '1' ||
      !!detailersMirrorGroupId(task)
    );
  }
  if (task.boardType === 'detailers') {
    return (
      task.status === 'ready-for-spooling' ||
      task.customFields?.[DETAILERS_MIRROR_FIELD] === '1' ||
      !!detailersMirrorGroupId(task)
    );
  }
  return false;
}

export function detailersMirrorGroupId(task: Task): string | null {
  const raw = task.customFields?.[DETAILERS_MIRROR_GROUP_FIELD];
  return raw && raw.trim() ? raw.trim() : null;
}

/** Effective group for Detailers sheet placement (keeps the original level). */
export function effectiveGroupIdForDetailersBoard(task: Task): string | null {
  if (isDetailersMirroredSpoolingTask(task)) {
    return detailersMirrorGroupId(task) ?? task.groupId;
  }
  return task.groupId;
}

/**
 * When Detailers sets Ready for Spooling: move ownership to Spooling,
 * keep the Detailers group for dual visibility, and stamp the mirror flag.
 */
export function applyDetailersReadyForSpoolingHandoff(
  task: Task,
  updates: Partial<Task>
): Partial<Task> {
  if (!updates.status || updates.status !== 'ready-for-spooling') return updates;
  if (
    updates.status === task.status &&
    task.boardType === 'spooling' &&
    task.customFields?.[DETAILERS_MIRROR_FIELD] === '1'
  ) {
    return updates;
  }

  const fromBoard =
    (updates.boardType as ProjectBoardType | undefined) ??
    (task.boardType !== 'employee' && task.boardType !== 'main'
      ? (task.boardType as ProjectBoardType)
      : null);

  const stickyGroup = stickyDetailersGroupId(task);
  const wasDetailers =
    fromBoard === 'detailers' ||
    task.boardType === 'detailers' ||
    task.customFields?.[DETAILERS_MIRROR_FIELD] === '1' ||
    !!stickyGroup;

  // Handoff applies when leaving Detailers, or when re-asserting Ready for Spooling
  // on an already-Spooling package (repairs mirror lost in Fab demote / export loops).
  if (!wasDetailers && task.boardType !== 'detailers' && task.boardType !== 'spooling') {
    return updates;
  }

  const detailersGroupId =
    task.boardType === 'detailers'
      ? task.groupId
      : stickyGroup ?? task.groupId;

  return {
    ...updates,
    boardType: 'spooling',
    // Keep Detailers level placement — do not let branch enrich re-home the group.
    groupId: detailersGroupId,
    customFields: {
      ...(task.customFields ?? {}),
      ...(updates.customFields ?? {}),
      [DETAILERS_MIRROR_FIELD]: '1',
      [DETAILERS_MIRROR_GROUP_FIELD]: detailersGroupId ?? '',
    },
  };
}

/** Spooling status that explicitly hands the package back to Detailers as Rework. */
export const RETURN_TO_DETAILING_STATUS = 'return-to-detailing';

/**
 * When Spooling sets Return to Detailing: move ownership back to Detailers,
 * land on Rework (visible / actionable), and clear the mirror flag.
 */
export function applySpoolingReturnToDetailingHandoff(
  task: Task,
  updates: Partial<Task>
): Partial<Task> {
  if (!updates.status || updates.status !== RETURN_TO_DETAILING_STATUS) return updates;

  const onSpooling =
    task.boardType === 'spooling' ||
    (updates.boardType as ProjectBoardType | undefined) === 'spooling' ||
    task.customFields?.[DETAILERS_MIRROR_FIELD] === '1';

  if (!onSpooling) return updates;

  const detailersGroupId = detailersMirrorGroupId(task) ?? task.groupId;
  const nextFields = clearDetailersSpoolingMirrorFields({
    ...(task.customFields ?? {}),
    ...(updates.customFields ?? {}),
  });

  return {
    ...updates,
    status: 'rework',
    boardType: 'detailers',
    groupId: detailersGroupId,
    customFields: nextFields,
  };
}

/**
 * If status leaves the Detailers↔Spooling tracking set, drop the mirror flag.
 * Only return ownership to Detailers when the task is still on Spooling
 * (e.g. rework / not-started). Never yank Fab/Shipping/Field packages back —
 * that was sending Material Pulled packages to Detailers as Not Started.
 */
export function applyDetailersSpoolingMirrorCleanup(
  task: Task,
  updates: Partial<Task>
): Partial<Task> {
  if (!updates.status || updates.status === task.status) return updates;
  if (task.customFields?.[DETAILERS_MIRROR_FIELD] !== '1') return updates;
  if (DETAILERS_MIRROR_SPOOLING_STATUSES.has(updates.status)) return updates;

  const detailersGroupId = detailersMirrorGroupId(task) ?? task.groupId;
  const nextFields = {
    ...(task.customFields ?? {}),
    ...(updates.customFields ?? {}),
  };
  delete nextFields[DETAILERS_MIRROR_FIELD];
  delete nextFields[DETAILERS_MIRROR_GROUP_FIELD];

  const nextBoard =
    (updates.boardType as ProjectBoardType | undefined) ??
    (task.boardType !== 'employee' && task.boardType !== 'main'
      ? (task.boardType as ProjectBoardType)
      : null);

  // Downstream shop boards own the package after Ready for Fab — clear mirror only.
  if (
    nextBoard === 'fab' ||
    nextBoard === 'shipping' ||
    nextBoard === 'field' ||
    task.boardType === 'fab' ||
    task.boardType === 'shipping' ||
    task.boardType === 'field'
  ) {
    return {
      ...updates,
      customFields: nextFields,
    };
  }

  // Still on Spooling (or Detailers mirror): hand ownership back to Detailers.
  return {
    ...updates,
    boardType: 'detailers',
    groupId: detailersGroupId,
    customFields: nextFields,
  };
}

/** Clear Detailers mirror stamps (e.g. when returning ownership to Detailers). */
export function clearDetailersSpoolingMirrorFields(
  customFields: Record<string, string | null> | null | undefined
): Record<string, string> {
  const next: Record<string, string> = {};
  for (const [key, value] of Object.entries(customFields ?? {})) {
    if (value != null) next[key] = value;
  }
  delete next[DETAILERS_MIRROR_FIELD];
  delete next[DETAILERS_MIRROR_GROUP_FIELD];
  return next;
}

/**
 * Drop only the live mirror flag while keeping `bbDetailersGroupId` sticky.
 * Used when promoting Spooling → Fab so Fab statuses don't yank to Detailers,
 * but demote/back-to-Spooling can restore dual visibility.
 */
export function clearDetailersMirrorFlagKeepGroup(
  customFields: Record<string, string | null> | null | undefined
): Record<string, string> {
  const next: Record<string, string> = {};
  for (const [key, value] of Object.entries(customFields ?? {})) {
    if (value != null) next[key] = value;
  }
  delete next[DETAILERS_MIRROR_FIELD];
  return next;
}

/** Sticky Detailers group from mirror fields, or current group while mirrored/on Detailers. */
export function stickyDetailersGroupId(task: Task): string | null {
  return (
    detailersMirrorGroupId(task) ??
    (task.customFields?.[DETAILERS_MIRROR_FIELD] === '1' || task.boardType === 'detailers'
      ? task.groupId
      : null)
  );
}

/**
 * Re-stamp Detailers mirror after Fab/Shipping demote back to Spooling,
 * or repair a handoff package that lost `bbMirrorDetailers`.
 */
export function stampDetailersSpoolingMirror(
  task: Task,
  groupId: string | null | undefined
): { groupId: string | null; customFields: Record<string, string> } {
  const detailersGroupId = (groupId && groupId.trim()) || null;
  const customFields: Record<string, string> = {};
  for (const [key, value] of Object.entries(task.customFields ?? {})) {
    if (value != null) customFields[key] = value;
  }
  customFields[DETAILERS_MIRROR_FIELD] = '1';
  customFields[DETAILERS_MIRROR_GROUP_FIELD] = detailersGroupId ?? '';
  return {
    groupId: detailersGroupId,
    customFields,
  };
}

/**
 * Restore Detailers↔Spooling dual ownership:
 * - Spooling roots missing mirror stamps
 * - Detailers roots stuck on Ready for Spooling (or other handoff statuses) that
 *   never finished / were yanked back to Detailers by group sync
 */
export function repairDetailersSpoolingMirror(
  tasks: Task[],
  groups: TaskGroup[]
): Task[] {
  const groupExists = (id: string | null | undefined) =>
    !!id && groups.some((g) => g.id === id);

  return tasks.map((task) => {
    if (task.parentTaskId) return task;
    if (!DETAILERS_MIRROR_SPOOLING_STATUSES.has(task.status)) return task;

    // Detailers-owned packages in handoff statuses always belong on Spooling too.
    if (task.boardType === 'detailers') {
      const preferred =
        (detailersMirrorGroupId(task) && groupExists(detailersMirrorGroupId(task))
          ? detailersMirrorGroupId(task)
          : null) ??
        (task.groupId &&
        groupExists(task.groupId) &&
        findSectionBoardTypeForGroup(groups, task.groupId) === 'detailers'
          ? task.groupId
          : null) ??
        task.groupId;
      const stamped = stampDetailersSpoolingMirror(task, preferred);
      return {
        ...task,
        boardType: 'spooling' as const,
        groupId: stamped.groupId,
        customFields: stamped.customFields,
      };
    }

    if (task.boardType !== 'spooling') return task;

    const sticky = detailersMirrorGroupId(task);
    const liveGroupOk =
      !!task.groupId &&
      groupExists(task.groupId) &&
      findSectionBoardTypeForGroup(groups, task.groupId) === 'detailers';

    if (task.customFields?.[DETAILERS_MIRROR_FIELD] === '1') {
      const preferred = sticky && groupExists(sticky) ? sticky : liveGroupOk ? task.groupId : sticky;
      if (preferred && task.groupId !== preferred && groupExists(preferred)) {
        return {
          ...task,
          groupId: preferred,
          customFields: {
            ...(task.customFields ?? {}),
            [DETAILERS_MIRROR_GROUP_FIELD]: preferred,
          },
        };
      }
      return task;
    }

    const restoreId =
      (sticky && groupExists(sticky) && sticky) ||
      (liveGroupOk ? task.groupId : null);
    if (!restoreId) return task;

    const stamped = stampDetailersSpoolingMirror(task, restoreId);
    return {
      ...task,
      groupId: stamped.groupId,
      customFields: stamped.customFields,
    };
  });
}

function findSectionBoardTypeForGroup(
  groups: TaskGroup[],
  groupId: string
): ProjectBoardType | null {
  let current = groups.find((g) => g.id === groupId);
  while (current) {
    if (current.tier === 'section' && current.sectionBoardType) {
      return current.sectionBoardType;
    }
    current = current.parentId ? groups.find((g) => g.id === current!.parentId) : undefined;
  }
  return null;
}

/** Nested assembly / subtask ids under a parent package task (BFS). */
export function collectDescendantTaskIds(tasks: Task[], rootId: string): string[] {
  const childrenByParent = new Map<string, string[]>();
  for (const task of tasks) {
    if (!task.parentTaskId) continue;
    const list = childrenByParent.get(task.parentTaskId);
    if (list) list.push(task.id);
    else childrenByParent.set(task.parentTaskId, [task.id]);
  }
  const ids: string[] = [];
  const queue = [...(childrenByParent.get(rootId) ?? [])];
  while (queue.length > 0) {
    const id = queue.shift()!;
    ids.push(id);
    const kids = childrenByParent.get(id);
    if (kids?.length) queue.push(...kids);
  }
  return ids;
}
