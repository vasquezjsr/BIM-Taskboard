import type { ProjectBoardType, Task } from '../types';

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
  if (updates.status === task.status && task.boardType === 'spooling') return updates;

  const fromBoard =
    (updates.boardType as ProjectBoardType | undefined) ??
    (task.boardType !== 'employee' && task.boardType !== 'main'
      ? (task.boardType as ProjectBoardType)
      : null);

  const wasDetailers =
    fromBoard === 'detailers' ||
    task.boardType === 'detailers' ||
    task.customFields?.[DETAILERS_MIRROR_FIELD] === '1';

  if (!wasDetailers && task.boardType !== 'detailers') return updates;

  const detailersGroupId =
    task.boardType === 'detailers'
      ? task.groupId
      : detailersMirrorGroupId(task) ?? task.groupId;

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

/** Clear Detailers mirror stamps (e.g. when promoting Spooling → Fab). */
export function clearDetailersSpoolingMirrorFields(
  customFields: Record<string, string> | null | undefined
): Record<string, string> {
  const next = { ...(customFields ?? {}) };
  delete next[DETAILERS_MIRROR_FIELD];
  delete next[DETAILERS_MIRROR_GROUP_FIELD];
  return next;
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
