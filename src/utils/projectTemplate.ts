import { v4 as uuid } from 'uuid';
import type { CustomBoard, Project, ProjectBoardType, Task, TaskGroup, TaskStatus } from '../types';
import { defaultProjectFields, MAIN_SECTION_BOARDS, normalizeProject } from '../types';
import { isLevelGroupName } from './buildingLevels';
import {
  applyTradeFirstProjectLevelConfig,
} from './tradeFirstLevelConfig';
import { defaultSectionName } from './groupRows';
import { defaultStatusForBoard } from './taskStatus';
import { getBoardTaskStatuses, type BoardTaskStatusesMap } from './taskStatuses';

export const TEMPLATE_PROJECT_LABEL = 'Project Template';

export function isTemplateProject(project: Project): boolean {
  return project.isTemplate === true || project.name === TEMPLATE_PROJECT_LABEL;
}

export function createTemplateProjectMetadata(): Pick<Project, 'isTemplate'> {
  return { isTemplate: true };
}

/** Empty main-overview sections — one per built-in board tab, no tasks or nested groups. */
export function buildEmptyProjectBoards(clientId: string, projectId: string): TaskGroup[] {
  return MAIN_SECTION_BOARDS.map((sectionBoardType, index) => ({
    id: uuid(),
    name: defaultSectionName(sectionBoardType),
    clientId,
    projectId,
    boardType: 'main' as ProjectBoardType,
    tier: 'section' as const,
    parentId: null,
    sectionBoardType,
    sortOrder: index,
  }));
}

/** Wipe template content back to empty board tabs only (one-time migration / manual reset). */
export function resetTemplateToEmptyBoards(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[]
): { projects: Project[]; taskGroups: TaskGroup[]; tasks: Task[] } {
  const template = projects.find(isTemplateProject);
  if (!template) return { projects, taskGroups, tasks };

  const templateId = template.id;
  const emptyProject = normalizeProject({
    id: templateId,
    name: TEMPLATE_PROJECT_LABEL,
    clientId: template.clientId,
    ...defaultProjectFields(),
    isTemplate: true,
    buildingLevels: [],
    activeLevels: [],
  });

  return {
    projects: projects.map((project) => (project.id === templateId ? emptyProject : project)),
    taskGroups: [
      ...taskGroups.filter((group) => group.projectId !== templateId),
      ...buildEmptyProjectBoards(template.clientId, templateId),
    ],
    tasks: tasks.filter((task) => task.projectId !== templateId),
  };
}

export function templateStatusForBoard(boardType: ProjectBoardType | 'employee' | 'main'): TaskStatus {
  if (boardType === 'rfi') return 'waiting-for-response';
  return 'not-started';
}

export function sanitizeTemplateProject(project: Project): Project {
  if (!project.isTemplate && project.name !== TEMPLATE_PROJECT_LABEL) return project;
  return {
    ...normalizeProject(project),
    name: TEMPLATE_PROJECT_LABEL,
    isTemplate: true,
    detailerIds: [],
    supportIds: [],
    revitYear: null,
    modelType: null,
    budgetHours: null,
    totalHoursSpent: null,
    projectStartDate: null,
    projectEndDate: null,
    jobCode: null,
  };
}

export function sanitizeTemplateTasks(tasks: Task[], templateProjectId: string): Task[] {
  return tasks.map((task) => {
    if (task.projectId !== templateProjectId) return task;
    return {
      ...task,
      status: templateStatusForBoard(task.boardType),
      assigneeIds: [],
      assigneesLocked: false,
      dueDate: null,
    };
  });
}

export function replaceTemplateName(text: string, templateName: string, newName: string): string {
  if (!text || templateName === newName) return text;
  return text.split(templateName).join(newName);
}

function replaceLevelInText(text: string, fromLevel: string, toLevel: string): string {
  if (!text || fromLevel === toLevel) return text;
  return text.split(fromLevel).join(toLevel);
}

const PM_SECTION_BOARD: ProjectBoardType = 'project-managers';

function isPmCoordinationGroup(g: TaskGroup): boolean {
  return g.tier === 'parent' && g.name === 'Project Coordination';
}

function getSectionBoardType(
  group: TaskGroup,
  groupsById: Map<string, TaskGroup>
): ProjectBoardType | null {
  let current: TaskGroup | undefined = group;
  while (current?.parentId) {
    const parent = groupsById.get(current.parentId);
    if (!parent) break;
    if (parent.tier === 'section' && parent.sectionBoardType) {
      return parent.sectionBoardType;
    }
    current = parent;
  }
  return null;
}

function collectSubtree(groupId: string, groups: TaskGroup[]): TaskGroup[] {
  const result: TaskGroup[] = [];
  const queue = [groupId];
  while (queue.length) {
    const id = queue.shift()!;
    const g = groups.find((x) => x.id === id);
    if (!g) continue;
    result.push(g);
    groups.filter((x) => x.parentId === id).forEach((c) => queue.push(c.id));
  }
  return result;
}

function duplicateLevelSubtree(
  sourceLevelName: string,
  targetLevelName: string,
  projectId: string,
  clientId: string,
  groups: TaskGroup[],
  tasks: Task[],
  boardTaskStatuses: BoardTaskStatusesMap,
  sortOrder: number
): { groups: TaskGroup[]; tasks: Task[] } {
  const groupsById = new Map(groups.map((g) => [g.id, g]));
  const newGroups: TaskGroup[] = [];
  const newTasks: Task[] = [];
  const groupIdMap = new Map<string, string>();

  const sourceParents = groups.filter(
    (g) =>
      g.projectId === projectId &&
      g.tier === 'parent' &&
      g.name === sourceLevelName &&
      !isPmCoordinationGroup(g)
  );

  for (const sourceParent of sourceParents) {
    const sectionType = getSectionBoardType(sourceParent, groupsById);
    if (sectionType === PM_SECTION_BOARD) continue;

    const newParentId = uuid();
    groupIdMap.set(sourceParent.id, newParentId);
    newGroups.push({
      ...sourceParent,
      id: newParentId,
      name: targetLevelName,
      clientId,
      projectId,
      sortOrder,
    });

    const subtree = collectSubtree(sourceParent.id, groups).filter((g) => g.id !== sourceParent.id);
    for (const node of subtree) {
      const newId = uuid();
      groupIdMap.set(node.id, newId);
      const mappedParent = node.parentId ? groupIdMap.get(node.parentId) ?? null : null;
      newGroups.push({
        ...node,
        id: newId,
        clientId,
        projectId,
        parentId: mappedParent,
      });
    }

    const sourceGroupIds = new Set([sourceParent.id, ...subtree.map((g) => g.id)]);
    for (const task of tasks.filter((t) => t.projectId === projectId && t.groupId && sourceGroupIds.has(t.groupId))) {
      const boardType = task.boardType;
      const status =
        boardType === 'detailers' ||
        boardType === 'deliverables' ||
        boardType === 'project-managers'
          ? defaultStatusForBoard(
              boardType as ProjectBoardType,
              getBoardTaskStatuses(boardType as ProjectBoardType, boardTaskStatuses)
            )
          : task.status;

      newTasks.push({
        ...task,
        id: uuid(),
        title: replaceLevelInText(task.title, sourceLevelName, targetLevelName),
        description: replaceLevelInText(task.description, sourceLevelName, targetLevelName),
        status,
        assigneeIds: [],
        clientId,
        projectId,
        groupId: task.groupId ? groupIdMap.get(task.groupId) ?? null : null,
        parentTaskId: null,
        createdAt: new Date().toISOString(),
      });
    }
  }

  return { groups: newGroups, tasks: newTasks };
}

export function applyProjectLevelConfig(
  projectId: string,
  clientId: string,
  buildingLevels: string[],
  activeLevels: string[],
  groups: TaskGroup[],
  tasks: Task[],
  boardTaskStatuses: BoardTaskStatusesMap
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const activeSet = new Set(activeLevels);
  const projectGroups = groups.filter((g) => g.projectId === projectId);

  const levelParents = projectGroups.filter(
    (g) => g.tier === 'parent' && isLevelGroupName(g.name, buildingLevels) && !isPmCoordinationGroup(g)
  );

  const referenceLevel =
    levelParents.find((g) => g.name === 'Level 1')?.name ??
    levelParents.find((g) => g.name.startsWith('Level '))?.name ??
    levelParents[0]?.name;

  let nextGroups = [...groups];
  let nextTasks = [...tasks];

  const removeGroupIds = new Set<string>();
  for (const parent of levelParents) {
    if (!activeSet.has(parent.name)) {
      for (const g of collectSubtree(parent.id, projectGroups)) {
        removeGroupIds.add(g.id);
      }
    }
  }

  nextGroups = nextGroups.filter((g) => g.projectId !== projectId || !removeGroupIds.has(g.id));
  nextTasks = nextTasks.map((t) => {
    if (t.projectId !== projectId) return t;
    if (!t.groupId || !removeGroupIds.has(t.groupId)) return t;
    return { ...t, groupId: null };
  });

  const refreshedProjectGroups = nextGroups.filter((g) => g.projectId === projectId);
  const refreshedLevelNames = new Set(
    refreshedProjectGroups
      .filter((g) => g.tier === 'parent' && isLevelGroupName(g.name, buildingLevels))
      .map((g) => g.name)
  );

  if (referenceLevel) {
    for (let i = 0; i < activeLevels.length; i++) {
      const levelName = activeLevels[i];
      if (refreshedLevelNames.has(levelName)) continue;
      const duplicated = duplicateLevelSubtree(
        referenceLevel,
        levelName,
        projectId,
        clientId,
        refreshedProjectGroups,
        nextTasks.filter((t) => t.projectId === projectId),
        boardTaskStatuses,
        i
      );
      nextGroups = [...nextGroups, ...duplicated.groups];
      nextTasks = [...nextTasks, ...duplicated.tasks];
      refreshedLevelNames.add(levelName);
    }
  }

  for (const g of nextGroups.filter((gr) => gr.projectId === projectId && gr.tier === 'parent')) {
    if (!isLevelGroupName(g.name, buildingLevels) || isPmCoordinationGroup(g)) continue;
    const order = activeLevels.indexOf(g.name);
    if (order >= 0) {
      g.sortOrder = order;
    }
  }

  const tradeFirst = applyTradeFirstProjectLevelConfig(
    projectId,
    clientId,
    activeLevels,
    nextGroups,
    nextTasks
  );

  return { taskGroups: tradeFirst.taskGroups, tasks: tradeFirst.tasks };
}

export function cloneProjectFromTemplate(
  template: Project,
  newClientId: string,
  newProjectId: string,
  newProjectName: string,
  taskGroups: TaskGroup[],
  tasks: Task[],
  customBoards: CustomBoard[],
  boardTaskStatuses: BoardTaskStatusesMap,
  buildingLevels: string[],
  activeLevels: string[]
): {
  project: Project;
  taskGroups: TaskGroup[];
  tasks: Task[];
  customBoards: CustomBoard[];
} {
  const templateGroups = taskGroups.filter((g) => g.projectId === template.id);
  const templateTasks = tasks.filter((t) => t.projectId === template.id);
  const templateBoards = customBoards.filter((b) => b.projectId === template.id);

  const groupIdMap = new Map<string, string>();
  for (const g of templateGroups) {
    groupIdMap.set(g.id, uuid());
  }

  const boardIdMap = new Map<string, `cb-${string}`>();
  for (const b of templateBoards) {
    boardIdMap.set(b.id, `cb-${uuid()}`);
  }

  const clonedGroups: TaskGroup[] = templateGroups.map((g) => ({
    ...g,
    id: groupIdMap.get(g.id)!,
    clientId: newClientId,
    projectId: newProjectId,
    parentId: g.parentId ? groupIdMap.get(g.parentId) ?? null : null,
  }));

  const clonedBoards: CustomBoard[] = templateBoards.map((b) => ({
    ...b,
    id: boardIdMap.get(b.id)!,
    clientId: newClientId,
    projectId: newProjectId,
  }));

  const taskIdMap = new Map<string, string>();
  for (const t of templateTasks) {
    taskIdMap.set(t.id, uuid());
  }

  const remapBoardType = (boardType: Task['boardType']): Task['boardType'] => {
    if (typeof boardType === 'string' && boardIdMap.has(boardType)) {
      return boardIdMap.get(boardType)!;
    }
    return boardType;
  };

  let clonedTasks: Task[] = templateTasks.map((t) => {
    const boardType = remapBoardType(t.boardType);
    const status =
      boardType === 'detailers' ||
      boardType === 'deliverables' ||
      boardType === 'project-managers'
        ? defaultStatusForBoard(
            boardType as ProjectBoardType,
            getBoardTaskStatuses(boardType as ProjectBoardType, boardTaskStatuses)
          )
        : t.status;

    return {
      ...t,
      id: taskIdMap.get(t.id)!,
      title: replaceTemplateName(t.title, template.name, newProjectName),
      description: replaceTemplateName(t.description, template.name, newProjectName),
      status,
      assigneeIds: [],
      clientId: newClientId,
      projectId: newProjectId,
      boardType,
      groupId: t.groupId ? groupIdMap.get(t.groupId) ?? null : null,
      parentTaskId: t.parentTaskId ? taskIdMap.get(t.parentTaskId) ?? null : null,
      createdAt: new Date().toISOString(),
    };
  });

  const leveled = applyProjectLevelConfig(
    newProjectId,
    newClientId,
    buildingLevels,
    activeLevels,
    clonedGroups,
    clonedTasks,
    boardTaskStatuses
  );

  const project: Project = normalizeProject({
    id: newProjectId,
    name: newProjectName,
    clientId: newClientId,
    detailerIds: [...template.detailerIds],
    supportIds: [...template.supportIds],
    pmIds: [...(template.pmIds ?? [])],
    assistantPmIds: [...(template.assistantPmIds ?? [])],
    fieldIds: [...(template.fieldIds ?? [])],
    fieldCrewIds: [...(template.fieldCrewIds ?? [])],
    revitYear: template.revitYear,
    modelType: template.modelType,
    buildingLevels: [...buildingLevels],
    activeLevels: [...activeLevels],
    isTemplate: false,
    billingType: template.billingType,
    budgetHours: template.budgetHours,
    totalHoursSpent: template.totalHoursSpent,
    projectStartDate: template.projectStartDate,
    projectEndDate: template.projectEndDate,
    jobCode: template.jobCode,
    nextTaskNumber: template.nextTaskNumber ?? 1,
  });

  return {
    project,
    taskGroups: leveled.taskGroups,
    tasks: leveled.tasks,
    customBoards: clonedBoards,
  };
}

export interface NewProjectOptions {
  buildingLevels: string[];
  activeLevels: string[];
  useTemplate: boolean;
}

export function createEmptyProject(clientId: string, name: string): Project {
  return {
    id: uuid(),
    name,
    clientId,
    ...defaultProjectFields(),
  };
}
