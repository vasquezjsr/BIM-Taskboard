import type { Task, TaskGroup } from '../types';

export interface FindReplaceFieldSelection {
  title: boolean;
  description: boolean;
  groupName: boolean;
  customColumnIds: string[];
}

export interface FindReplaceOptions {
  find: string;
  replace: string;
  caseSensitive: boolean;
  fields: FindReplaceFieldSelection;
}

export interface FindReplacePreview {
  tasksAffected: number;
  replacementCount: number;
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

export function countTextMatches(
  text: string,
  find: string,
  caseSensitive: boolean
): number {
  if (!find || !text) return 0;

  if (caseSensitive) {
    let count = 0;
    let index = 0;
    while ((index = text.indexOf(find, index)) !== -1) {
      count += 1;
      index += find.length;
    }
    return count;
  }

  const pattern = new RegExp(escapeRegExp(find), 'gi');
  return (text.match(pattern) ?? []).length;
}

export function replaceInText(
  text: string,
  find: string,
  replace: string,
  caseSensitive: boolean
): string {
  if (!find) return text;

  if (caseSensitive) {
    return text.split(find).join(replace);
  }

  const pattern = new RegExp(escapeRegExp(find), 'gi');
  return text.replace(pattern, () => replace);
}

export function previewFindReplace(
  tasks: Task[],
  taskIds: Iterable<string>,
  options: FindReplaceOptions
): FindReplacePreview {
  const idSet = new Set(taskIds);
  const selected = tasks.filter((task) => idSet.has(task.id));
  if (!options.find.trim() || selected.length === 0) {
    return { tasksAffected: 0, replacementCount: 0 };
  }

  let tasksAffected = 0;
  let replacementCount = 0;

  for (const task of selected) {
    let taskChanged = false;

    if (options.fields.title) {
      const matches = countTextMatches(task.title, options.find, options.caseSensitive);
      if (matches > 0) {
        taskChanged = true;
        replacementCount += matches;
      }
    }

    if (options.fields.description) {
      const matches = countTextMatches(task.description, options.find, options.caseSensitive);
      if (matches > 0) {
        taskChanged = true;
        replacementCount += matches;
      }
    }

    for (const columnId of options.fields.customColumnIds) {
      const value = task.customFields?.[columnId];
      if (typeof value !== 'string' || !value) continue;
      const matches = countTextMatches(value, options.find, options.caseSensitive);
      if (matches > 0) {
        taskChanged = true;
        replacementCount += matches;
      }
    }

    if (taskChanged) tasksAffected += 1;
  }

  return { tasksAffected, replacementCount };
}

export function applyFindReplaceToTask(
  task: Task,
  options: FindReplaceOptions
): Partial<Task> | null {
  if (!options.find) return null;

  const updates: Partial<Task> = {};
  let changed = false;

  if (options.fields.title) {
    const nextTitle = replaceInText(
      task.title,
      options.find,
      options.replace,
      options.caseSensitive
    );
    if (nextTitle !== task.title) {
      updates.title = nextTitle;
      changed = true;
    }
  }

  if (options.fields.description) {
    const nextDescription = replaceInText(
      task.description,
      options.find,
      options.replace,
      options.caseSensitive
    );
    if (nextDescription !== task.description) {
      updates.description = nextDescription;
      changed = true;
    }
  }

  if (options.fields.customColumnIds.length > 0) {
    const nextCustomFields = { ...(task.customFields ?? {}) };
    let customChanged = false;

    for (const columnId of options.fields.customColumnIds) {
      const value = task.customFields?.[columnId];
      if (typeof value !== 'string' || !value) continue;
      const nextValue = replaceInText(
        value,
        options.find,
        options.replace,
        options.caseSensitive
      );
      if (nextValue !== value) {
        nextCustomFields[columnId] = nextValue;
        customChanged = true;
      }
    }

    if (customChanged) {
      updates.customFields = nextCustomFields;
      changed = true;
    }
  }

  return changed ? updates : null;
}

export function previewFindReplaceGroups(
  groups: TaskGroup[],
  groupIds: Iterable<string>,
  options: FindReplaceOptions
): FindReplacePreview {
  const idSet = new Set(groupIds);
  const selected = groups.filter((group) => idSet.has(group.id));
  if (!options.find.trim() || selected.length === 0 || !options.fields.groupName) {
    return { tasksAffected: 0, replacementCount: 0 };
  }

  let groupsAffected = 0;
  let replacementCount = 0;

  for (const group of selected) {
    const matches = countTextMatches(group.name, options.find, options.caseSensitive);
    if (matches > 0) {
      groupsAffected += 1;
      replacementCount += matches;
    }
  }

  return { tasksAffected: groupsAffected, replacementCount };
}

export function applyFindReplaceToGroup(
  group: TaskGroup,
  options: FindReplaceOptions
): Partial<TaskGroup> | null {
  if (!options.find || !options.fields.groupName) return null;

  const nextName = replaceInText(
    group.name,
    options.find,
    options.replace,
    options.caseSensitive
  );
  return nextName !== group.name ? { name: nextName } : null;
}
