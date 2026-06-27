import { v4 as uuid } from 'uuid';
import type { Project, ProjectBoardType, Task, TaskGroup } from '../types';
import {
  TEMPLATE_DOCUMENTS_HEADER,
  TEMPLATE_DOCUMENTS_SAMPLE_TASKS,
  TEMPLATE_RFI_LOG_HEADER,
  TEMPLATE_RFI_SAMPLE_TASKS,
} from '../data/templateBoardSamples';
import { getFlatBoardHeaders } from './groupRows';
import { isTemplateProject } from './projectTemplate';

function flatBoardHasHeader(
  taskGroups: TaskGroup[],
  clientId: string,
  projectId: string,
  boardType: ProjectBoardType,
  headerName: string
): boolean {
  return getFlatBoardHeaders(taskGroups, clientId, projectId, boardType).some(
    (group) => group.name === headerName
  );
}

function applyFlatBoardSamples(
  clientId: string,
  projectId: string,
  boardType: 'rfi' | 'documents',
  headerName: string,
  sampleTasks: Array<{ title: string; description: string; status: string }>,
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  if (flatBoardHasHeader(taskGroups, clientId, projectId, boardType, headerName)) {
    return { taskGroups, tasks };
  }

  const headerId = uuid();
  const createdAt = new Date().toISOString();
  const headerGroup: TaskGroup = {
    id: headerId,
    name: headerName,
    clientId,
    projectId,
    boardType,
    tier: 'parent',
    parentId: null,
    sectionBoardType: null,
    sortOrder: 0,
  };

  const newTasks: Task[] = sampleTasks.map((sample, index) => ({
    id: uuid(),
    title: sample.title,
    description: sample.description,
    status: sample.status as Task['status'],
    assigneeIds: [],
    clientId,
    projectId,
    boardType,
    groupId: headerId,
    parentTaskId: null,
    priority: index,
    dueDate: null,
    createdAt,
  }));

  return {
    taskGroups: [...taskGroups, headerGroup],
    tasks: [...tasks, ...newTasks],
  };
}

/** Seed RFI log + contract documents/submittals on the project template only. */
export function applyTemplateBoardSamples(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const template = projects.find(isTemplateProject);
  if (!template) return { taskGroups, tasks };

  let nextGroups = taskGroups;
  let nextTasks = tasks;

  const rfiResult = applyFlatBoardSamples(
    template.clientId,
    template.id,
    'rfi',
    TEMPLATE_RFI_LOG_HEADER,
    TEMPLATE_RFI_SAMPLE_TASKS,
    nextGroups,
    nextTasks
  );
  nextGroups = rfiResult.taskGroups;
  nextTasks = rfiResult.tasks;

  const documentsResult = applyFlatBoardSamples(
    template.clientId,
    template.id,
    'documents',
    TEMPLATE_DOCUMENTS_HEADER,
    TEMPLATE_DOCUMENTS_SAMPLE_TASKS,
    nextGroups,
    nextTasks
  );

  return {
    taskGroups: documentsResult.taskGroups,
    tasks: documentsResult.tasks,
  };
}
