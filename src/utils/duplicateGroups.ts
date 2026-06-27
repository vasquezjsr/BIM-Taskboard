import { v4 as uuid } from 'uuid';
import type { Task, TaskGroup } from '../types';

function copyLabel(label: string): string {
  return label.endsWith(' (Copy)') ? label : `${label} (Copy)`;
}

export function canDuplicateGroup(group: TaskGroup): boolean {
  if (group.id.startsWith('__')) return false;
  if (group.tier === 'section') return false;
  return true;
}

/** Drop selected ids that sit inside another selected group's subtree. */
export function filterTopLevelGroupIds(groupIds: string[], groups: TaskGroup[]): string[] {
  const selected = new Set(groupIds);
  return groupIds.filter((id) => {
    let current = groups.find((g) => g.id === id);
    while (current?.parentId) {
      if (selected.has(current.parentId)) return false;
      current = groups.find((g) => g.id === current!.parentId);
    }
    return true;
  });
}

function collectGroupSubtree(groups: TaskGroup[], rootId: string): TaskGroup[] {
  const root = groups.find((g) => g.id === rootId);
  if (!root) return [];

  const result: TaskGroup[] = [];
  const queue = [root];
  while (queue.length) {
    const node = queue.shift()!;
    result.push(node);
    groups
      .filter((g) => g.parentId === node.id)
      .sort((a, b) => a.sortOrder - b.sortOrder)
      .forEach((child) => queue.push(child));
  }
  return result;
}

function duplicateOneGroup(
  source: TaskGroup,
  groups: TaskGroup[],
  tasks: Task[]
): { groups: TaskGroup[]; tasks: Task[]; newRootId: string } {
  const subtree = collectGroupSubtree(groups, source.id);
  const subtreeIds = new Set(subtree.map((g) => g.id));
  const groupIdMap = new Map<string, string>();
  const newRootId = uuid();
  groupIdMap.set(source.id, newRootId);

  const insertSort = source.sortOrder + 1;
  let nextGroups = groups.map((g) => {
    if (
      g.parentId === source.parentId &&
      g.projectId === source.projectId &&
      g.clientId === source.clientId &&
      g.sortOrder >= insertSort
    ) {
      return { ...g, sortOrder: g.sortOrder + 1 };
    }
    return g;
  });

  const newGroups: TaskGroup[] = [];
  for (const node of subtree) {
    const newId = node.id === source.id ? newRootId : uuid();
    if (node.id !== source.id) groupIdMap.set(node.id, newId);
    newGroups.push({
      ...node,
      id: newId,
      name: node.id === source.id ? copyLabel(node.name) : node.name,
      parentId:
        node.id === source.id
          ? source.parentId
          : node.parentId
            ? groupIdMap.get(node.parentId) ?? null
            : null,
      sortOrder: node.id === source.id ? insertSort : node.sortOrder,
    });
  }

  const sourceTasks = tasks.filter((t) => t.groupId && subtreeIds.has(t.groupId));
  const taskIdMap = new Map<string, string>();
  const newTasks: Task[] = [];

  for (const task of [...sourceTasks].sort((a, b) => a.priority - b.priority)) {
    const newId = uuid();
    taskIdMap.set(task.id, newId);
    newTasks.push({
      ...task,
      id: newId,
      groupId: task.groupId ? groupIdMap.get(task.groupId) ?? null : null,
      parentTaskId:
        task.parentTaskId && taskIdMap.has(task.parentTaskId)
          ? taskIdMap.get(task.parentTaskId)!
          : null,
      title: !task.parentTaskId ? copyLabel(task.title) : task.title,
      createdAt: new Date().toISOString(),
    });
  }

  return {
    groups: [...nextGroups, ...newGroups],
    tasks: [...tasks, ...newTasks],
    newRootId,
  };
}

export function duplicateGroupSubtrees(
  orderedGroupIds: string[],
  groups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[]; newRootGroupIds: string[] } {
  const topLevelIds = filterTopLevelGroupIds(orderedGroupIds, groups);
  const roots = topLevelIds
    .map((id) => groups.find((g) => g.id === id))
    .filter((g): g is TaskGroup => g != null && canDuplicateGroup(g));

  if (roots.length === 0) {
    return { taskGroups: groups, tasks, newRootGroupIds: [] };
  }

  const byParent = new Map<string, TaskGroup[]>();
  for (const root of roots) {
    const key = root.parentId ?? '__root__';
    const bucket = byParent.get(key) ?? [];
    bucket.push(root);
    byParent.set(key, bucket);
  }

  let nextGroups = groups;
  let nextTasks = tasks;
  const newRootGroupIds: string[] = [];

  for (const siblings of byParent.values()) {
    const sorted = [...siblings].sort((a, b) => b.sortOrder - a.sortOrder);
    for (const source of sorted) {
      const result = duplicateOneGroup(source, nextGroups, nextTasks);
      nextGroups = result.groups;
      nextTasks = result.tasks;
      newRootGroupIds.push(result.newRootId);
    }
  }

  return { taskGroups: nextGroups, tasks: nextTasks, newRootGroupIds };
}
