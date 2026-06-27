import { v4 as uuid } from 'uuid';
import type { Project, Task, TaskGroup } from '../types';
import {
  PM_BOARD_CHECKLIST,
  PM_CHECKLIST_MARKER_GROUP,
} from '../data/pmBoardChecklist';
import { getSectionForBoard } from './groupRows';
import {
  isTemplateProject,
  templateStatusForBoard,
} from './projectTemplate';
import { PROJECT_COORDINATION_GROUP_NAME } from '../data/vdcSeedData';

export function projectPmBoardHasChecklist(
  projectId: string,
  taskGroups: TaskGroup[]
): boolean {
  return taskGroups.some(
    (group) =>
      group.projectId === projectId &&
      group.tier === 'parent' &&
      group.name === PM_CHECKLIST_MARKER_GROUP
  );
}

export function templatePmBoardNeedsChecklist(
  projectId: string,
  taskGroups: TaskGroup[],
  tasks: Task[]
): boolean {
  const clientId = taskGroups.find((group) => group.projectId === projectId)?.clientId ?? '';
  const pmSection = getSectionForBoard(taskGroups, clientId, projectId, 'project-managers');
  if (!pmSection) return false;
  if (projectPmBoardHasChecklist(projectId, taskGroups)) return false;

  const pmParentGroups = taskGroups.filter(
    (group) =>
      group.projectId === projectId &&
      group.parentId === pmSection.id &&
      group.tier === 'parent'
  );

  if (pmParentGroups.length === 0) return true;

  if (
    pmParentGroups.length === 1 &&
    pmParentGroups[0]!.name === PROJECT_COORDINATION_GROUP_NAME &&
    !tasks.some((task) => task.groupId === pmParentGroups[0]!.id)
  ) {
    return true;
  }

  return false;
}

function removeLegacyEmptyPmGroups(
  clientId: string,
  projectId: string,
  taskGroups: TaskGroup[],
  tasks: Task[]
): TaskGroup[] {
  const pmSection = getSectionForBoard(taskGroups, clientId, projectId, 'project-managers');
  if (!pmSection) return taskGroups;

  const removeIds = new Set<string>();
  for (const group of taskGroups) {
    if (
      group.projectId === projectId &&
      group.parentId === pmSection.id &&
      group.tier === 'parent' &&
      group.name === PROJECT_COORDINATION_GROUP_NAME &&
      !tasks.some((task) => task.groupId === group.id)
    ) {
      removeIds.add(group.id);
    }
  }

  if (removeIds.size === 0) return taskGroups;
  return taskGroups.filter((group) => !removeIds.has(group.id));
}

/** Add the standard PM checklist groups and tasks under the project-managers section. */
export function applyPmBoardChecklist(
  clientId: string,
  projectId: string,
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  if (projectPmBoardHasChecklist(projectId, taskGroups)) {
    return { taskGroups, tasks };
  }

  const pmSection = getSectionForBoard(taskGroups, clientId, projectId, 'project-managers');
  if (!pmSection) return { taskGroups, tasks };

  const cleanedGroups = removeLegacyEmptyPmGroups(clientId, projectId, taskGroups, tasks);

  const newGroups: TaskGroup[] = [];
  const newTasks: Task[] = [];
  const createdAt = new Date().toISOString();
  const defaultStatus = templateStatusForBoard('project-managers');

  PM_BOARD_CHECKLIST.forEach((checklistGroup, groupIndex) => {
    const groupId = uuid();
    newGroups.push({
      id: groupId,
      name: checklistGroup.name,
      clientId,
      projectId,
      boardType: 'main',
      tier: 'parent',
      parentId: pmSection.id,
      sectionBoardType: null,
      sortOrder: groupIndex,
    });

    checklistGroup.tasks.forEach((title, taskIndex) => {
      newTasks.push({
        id: uuid(),
        title,
        description: '',
        status: defaultStatus,
        assigneeIds: [],
        clientId,
        projectId,
        boardType: 'project-managers',
        groupId,
        parentTaskId: null,
        priority: taskIndex,
        dueDate: null,
        createdAt,
      });
    });
  });

  return {
    taskGroups: [...cleanedGroups, ...newGroups],
    tasks: [...tasks, ...newTasks],
  };
}

/** Apply PM checklist to the project template only, without touching other boards. */
export function applyTemplatePmBoardChecklist(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const template = projects.find(isTemplateProject);
  if (!template) return { taskGroups, tasks };
  if (!templatePmBoardNeedsChecklist(template.id, taskGroups, tasks)) {
    return { taskGroups, tasks };
  }

  return applyPmBoardChecklist(template.clientId, template.id, taskGroups, tasks);
}
