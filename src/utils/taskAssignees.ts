import type { Task } from '../types';

export type TaskWithLegacyAssignee = Task & { assigneeId?: string | null };

export function getTaskAssigneeIds(task: TaskWithLegacyAssignee): string[] {
  if (task.assigneeIds?.length) return task.assigneeIds;
  if (task.assigneeId?.trim()) return [task.assigneeId];
  return [];
}

export function hasTaskAssignees(task: TaskWithLegacyAssignee): boolean {
  return getTaskAssigneeIds(task).length > 0;
}

export function taskHasAssignee(task: TaskWithLegacyAssignee, employeeId: string): boolean {
  return getTaskAssigneeIds(task).includes(employeeId);
}

export function normalizeTaskAssignees(task: TaskWithLegacyAssignee): Task {
  const { assigneeId: _legacy, ...rest } = task;
  const assigneeIds = [...new Set(getTaskAssigneeIds(task))];
  return { ...rest, assigneeIds };
}

export function remapTaskAssigneeIds(
  task: TaskWithLegacyAssignee,
  idMap: Map<string, string>
): Task {
  const normalized = normalizeTaskAssignees(task);
  return {
    ...normalized,
    assigneeIds: normalized.assigneeIds.map((id) => (idMap.has(id) ? idMap.get(id)! : id)),
  };
}
