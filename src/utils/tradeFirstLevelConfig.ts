import { v4 as uuid } from 'uuid';
import type { ProjectBoardType, Task, TaskGroup } from '../types';
import { generateBuildingLevels } from './buildingLevels';
import { looksLikeLevelGroupName, looksLikeTradeGroupName } from './groupRows';

const LEGACY_TEMPLATE_LEVEL_NAMES: Record<string, string> = {
  'Level Underground': 'UG',
  'Level 01': 'Level 1',
  'Level 02': 'Level 2',
  'Level Roof': 'Roof',
};

const TRADE_FIRST_SECTIONS: ProjectBoardType[] = ['detailers', 'deliverables', 'spooling'];

function collectSubtree(groupId: string, groups: TaskGroup[]): TaskGroup[] {
  const result: TaskGroup[] = [];
  const queue = [groupId];
  while (queue.length) {
    const id = queue.shift()!;
    const group = groups.find((entry) => entry.id === id);
    if (!group) continue;
    result.push(group);
    groups.filter((entry) => entry.parentId === id).forEach((child) => queue.push(child.id));
  }
  return result;
}

function replaceLevelInText(text: string, fromLevel: string, toLevel: string): string {
  if (!text || fromLevel === toLevel) return text;
  return text.split(fromLevel).join(toLevel);
}

/** Rename legacy template level labels to UG / Level N / Roof. */
export function normalizeLegacyTemplateLevelNames(
  projectId: string,
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const renameEntries = Object.entries(LEGACY_TEMPLATE_LEVEL_NAMES);
  if (renameEntries.length === 0) {
    return { taskGroups, tasks };
  }

  let nextGroups = taskGroups;
  for (const [from, to] of renameEntries) {
    nextGroups = nextGroups.map((group) =>
      group.projectId === projectId && group.name === from ? { ...group, name: to } : group
    );
  }

  const nextTasks = tasks.map((task) => {
    if (task.projectId !== projectId) return task;
    let title = task.title;
    let description = task.description;
    for (const [from, to] of renameEntries) {
      title = replaceLevelInText(title, from, to);
      description = replaceLevelInText(description, from, to);
    }
    if (title === task.title && description === task.description) return task;
    return { ...task, title, description };
  });

  return { taskGroups: nextGroups, tasks: nextTasks };
}

function duplicateTradeLevelChild(
  sourceLevelGroup: TaskGroup,
  targetLevelName: string,
  sortOrder: number,
  projectId: string,
  clientId: string,
  groups: TaskGroup[],
  tasks: Task[]
): { groups: TaskGroup[]; tasks: Task[] } {
  const newLevelId = uuid();
  const newGroups: TaskGroup[] = [
    {
      ...sourceLevelGroup,
      id: newLevelId,
      name: targetLevelName,
      clientId,
      projectId,
      sortOrder,
    },
  ];
  const newTasks: Task[] = [];

  const subtree = collectSubtree(sourceLevelGroup.id, groups).filter(
    (group) => group.id !== sourceLevelGroup.id
  );
  const groupIdMap = new Map<string, string>([[sourceLevelGroup.id, newLevelId]]);

  for (const node of subtree) {
    const newId = uuid();
    groupIdMap.set(node.id, newId);
    newGroups.push({
      ...node,
      id: newId,
      clientId,
      projectId,
      parentId: node.parentId ? groupIdMap.get(node.parentId) ?? null : null,
    });
  }

  const sourceGroupIds = new Set([sourceLevelGroup.id, ...subtree.map((group) => group.id)]);
  for (const task of tasks.filter(
    (entry) => entry.projectId === projectId && entry.groupId && sourceGroupIds.has(entry.groupId)
  )) {
    newTasks.push({
      ...task,
      id: uuid(),
      title: replaceLevelInText(task.title, sourceLevelGroup.name, targetLevelName),
      description: replaceLevelInText(task.description, sourceLevelGroup.name, targetLevelName),
      clientId,
      projectId,
      groupId: task.groupId ? groupIdMap.get(task.groupId) ?? null : null,
      parentTaskId: null,
      createdAt: new Date().toISOString(),
    });
  }

  return { groups: newGroups, tasks: newTasks };
}

/** Ensure trade-first boards (Plumbing → Level 1, etc.) match selected building levels. */
export function applyTradeFirstProjectLevelConfig(
  projectId: string,
  clientId: string,
  activeLevels: string[],
  groups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const activeSet = new Set(activeLevels);
  const projectGroups = groups.filter((group) => group.projectId === projectId);
  let nextGroups = [...groups];
  let nextTasks = [...tasks];

  const sections = projectGroups.filter(
    (group) =>
      group.tier === 'section' &&
      group.sectionBoardType &&
      TRADE_FIRST_SECTIONS.includes(group.sectionBoardType)
  );

  for (const section of sections) {
    const tradeGroups = projectGroups.filter(
      (group) =>
        group.parentId === section.id &&
        group.tier === 'parent' &&
        (looksLikeTradeGroupName(group.name) || group.boardType === 'main')
    );

    for (const trade of tradeGroups) {
      const levelChildren = nextGroups
        .filter(
          (group) =>
            group.projectId === projectId &&
            group.parentId === trade.id &&
            group.tier === 'child' &&
            looksLikeLevelGroupName(group.name)
        )
        .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));

      if (levelChildren.length === 0) continue;

      const removeIds = new Set<string>();
      for (const levelGroup of levelChildren) {
        if (!activeSet.has(levelGroup.name)) {
          for (const node of collectSubtree(levelGroup.id, nextGroups)) {
            removeIds.add(node.id);
          }
        }
      }

      if (removeIds.size > 0) {
        nextGroups = nextGroups.filter((group) => !removeIds.has(group.id));
        nextTasks = nextTasks.filter(
          (task) => task.projectId !== projectId || !task.groupId || !removeIds.has(task.groupId)
        );
      }

      const refreshedLevels = nextGroups
        .filter(
          (group) =>
            group.projectId === projectId &&
            group.parentId === trade.id &&
            group.tier === 'child' &&
            looksLikeLevelGroupName(group.name)
        )
        .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));

      const referenceLevel =
        refreshedLevels.find((group) => group.name === 'Level 1') ??
        refreshedLevels.find((group) => group.name.startsWith('Level ')) ??
        refreshedLevels[0];
      if (!referenceLevel) continue;

      const existingNames = new Set(refreshedLevels.map((group) => group.name));
      for (let i = 0; i < activeLevels.length; i++) {
        const levelName = activeLevels[i]!;
        if (existingNames.has(levelName)) continue;
        const duplicated = duplicateTradeLevelChild(
          referenceLevel,
          levelName,
          i,
          projectId,
          clientId,
          nextGroups,
          nextTasks
        );
        nextGroups = [...nextGroups, ...duplicated.groups];
        nextTasks = [...nextTasks, ...duplicated.tasks];
        existingNames.add(levelName);
      }

      nextGroups = nextGroups.map((group) => {
        if (
          group.projectId === projectId &&
          group.parentId === trade.id &&
          group.tier === 'child' &&
          looksLikeLevelGroupName(group.name)
        ) {
          const order = activeLevels.indexOf(group.name);
          if (order >= 0) return { ...group, sortOrder: order };
        }
        return group;
      });
    }
  }

  return { taskGroups: nextGroups, tasks: nextTasks };
}

/** Undo v104 template expansion — restore 4 legacy levels per trade, drop Level 3–8. */
export function revertTemplateExpandedLevels(
  projectId: string,
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const EXPANDED_LEVEL_NAMES = new Set([
    'Level 3',
    'Level 4',
    'Level 5',
    'Level 6',
    'Level 7',
    'Level 8',
  ]);

  const STANDARD_TO_LEGACY: Record<string, string> = {
    UG: 'Level Underground',
    'Level 1': 'Level 01',
    'Level 2': 'Level 02',
    Roof: 'Level Roof',
  };

  const removeIds = new Set<string>();
  for (const group of taskGroups) {
    if (group.projectId !== projectId) continue;
    if (!EXPANDED_LEVEL_NAMES.has(group.name)) continue;
    for (const node of collectSubtree(group.id, taskGroups)) {
      removeIds.add(node.id);
    }
  }

  let nextGroups = taskGroups.filter((group) => !removeIds.has(group.id));
  let nextTasks = tasks.filter(
    (task) => task.projectId !== projectId || !task.groupId || !removeIds.has(task.groupId)
  );

  nextGroups = nextGroups.map((group) => {
    if (group.projectId !== projectId) return group;
    const legacyName = STANDARD_TO_LEGACY[group.name];
    return legacyName ? { ...group, name: legacyName } : group;
  });

  nextTasks = nextTasks.map((task) => {
    if (task.projectId !== projectId) return task;
    let title = task.title;
    let description = task.description;
    for (const [from, to] of Object.entries(STANDARD_TO_LEGACY)) {
      title = replaceLevelInText(title, from, to);
      description = replaceLevelInText(description, from, to);
    }
    if (title === task.title && description === task.description) return task;
    return { ...task, title, description };
  });

  return { taskGroups: nextGroups, tasks: nextTasks };
}

/** @deprecated Never auto-run on template — only used to undo v104. */
export function ensureTemplateStandardBuildingLevels(
  projectId: string,
  clientId: string,
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const buildingLevels = generateBuildingLevels(10);
  const activeLevels = [...buildingLevels];

  let nextGroups = taskGroups;
  let nextTasks = tasks;

  const normalized = normalizeLegacyTemplateLevelNames(projectId, nextGroups, nextTasks);
  nextGroups = normalized.taskGroups;
  nextTasks = normalized.tasks;

  const leveled = applyTradeFirstProjectLevelConfig(
    projectId,
    clientId,
    activeLevels,
    nextGroups,
    nextTasks
  );

  return leveled;
}
