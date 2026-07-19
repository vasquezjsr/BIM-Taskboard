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

/** If status leaves the spooling handoff set, drop the mirror and return to Detailers. */
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

  return {
    ...updates,
    boardType: 'detailers',
    groupId: detailersGroupId,
    customFields: nextFields,
  };
}
