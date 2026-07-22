import type { Task } from '../types';
import { collectDescendantTaskIds } from './detailersSpoolingHandoff';

/** Fields that follow parent → assemblies on Spooling Dashboard (never Title). */
export function assemblyCascadeUpdatesFromParent(updates: Partial<Task>): Partial<Task> {
  const next: Partial<Task> = {};
  if (updates.status !== undefined) next.status = updates.status;
  if (updates.assigneeIds !== undefined) next.assigneeIds = [...updates.assigneeIds];
  if (updates.assigneesLocked !== undefined) next.assigneesLocked = updates.assigneesLocked;
  if (updates.dueDate !== undefined) next.dueDate = updates.dueDate;
  if (updates.customFields !== undefined) {
    next.customFields = { ...updates.customFields };
  }
  if (updates.durationFields !== undefined) {
    next.durationFields = { ...updates.durationFields };
  }
  return next;
}

export function parentHasAssemblyCascade(updates: Partial<Task>): boolean {
  return Object.keys(assemblyCascadeUpdatesFromParent(updates)).length > 0;
}

/** IDs of nested assemblies under a parent package task. */
export function assemblyIdsUnderParent(tasks: Task[], parentId: string): string[] {
  return collectDescendantTaskIds(tasks, parentId);
}
