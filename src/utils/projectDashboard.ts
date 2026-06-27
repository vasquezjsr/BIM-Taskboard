import type { Client, CustomBoard, Project, ProjectBoardType, Task, TaskGroup } from '../types';
import { getBoardLabel, getProjectSubBoardOrder } from '../types';
import { resolveGroupVisualRole, taskBranchBoardType } from './groupRows';
import {
  getBoardTaskStatuses,
  isCompleteStatus,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from './taskStatuses';

export interface BoardProgressSummary {
  boardType: ProjectBoardType;
  label: string;
  completed: number;
  total: number;
  percent: number;
}

export interface ProjectDashboardSummary {
  project: Project;
  clientName: string;
  overall: BoardProgressSummary;
  boards: BoardProgressSummary[];
  durationLabel: string;
  budgetHoursLabel: string;
}

function formatDate(date: string): string {
  return new Date(date + 'T00:00:00').toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  });
}

function daySpan(start: string, end: string): number {
  const ms = new Date(end + 'T00:00:00').getTime() - new Date(start + 'T00:00:00').getTime();
  return Math.max(1, Math.round(ms / (1000 * 60 * 60 * 24)) + 1);
}

function collectTaskDateBounds(tasks: Task[], groups: TaskGroup[] = []): { start: string | null; end: string | null } {
  const dates: string[] = [];
  for (const task of tasks) {
    if (task.dueDate) dates.push(task.dueDate);
  }
  for (const group of groups) {
    if (resolveGroupVisualRole(group, groups) !== 'level-group') continue;
    for (const range of Object.values(group.durationFields ?? {})) {
      if (range.start) dates.push(range.start);
      if (range.end) dates.push(range.end);
    }
  }
  if (dates.length === 0) return { start: null, end: null };
  dates.sort();
  return { start: dates[0]!, end: dates[dates.length - 1]! };
}

export function formatProjectDuration(
  project: Project,
  tasks: Task[],
  groups: TaskGroup[] = []
): string {
  const start = project.projectStartDate;
  const end = project.projectEndDate;
  if (start && end) {
    return `${formatDate(start)} – ${formatDate(end)} (${daySpan(start, end)} days)`;
  }
  if (start) return `From ${formatDate(start)}`;
  if (end) return `Through ${formatDate(end)}`;

  const bounds = collectTaskDateBounds(tasks, groups);
  if (bounds.start && bounds.end) {
    return `${formatDate(bounds.start)} – ${formatDate(bounds.end)} (${daySpan(bounds.start, bounds.end)} days)`;
  }
  return '—';
}

export function formatBudgetHoursDisplay(project: Project): string {
  const spent = project.totalHoursSpent ?? 0;
  const budget = project.budgetHours;
  const budgetText = budget == null ? '—' : budget.toLocaleString();
  return `${spent.toLocaleString()}/${budgetText} Hours`;
}

export function formatProjectHours(project: Project): string {
  return formatBudgetHoursDisplay(project);
}

function computeProgressForTasks(
  scoped: Task[],
  groups: TaskGroup[],
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  projectId: string
): Pick<BoardProgressSummary, 'completed' | 'total' | 'percent'> {
  const total = scoped.length;
  const completed = scoped.filter((task) => {
    const statuses = getBoardTaskStatuses(
      taskBranchBoardType(task, groups),
      boardTaskStatuses,
      projectId,
      projectBoardTaskStatuses
    );
    return isCompleteStatus(task.status, statuses);
  }).length;
  const percent = total === 0 ? 0 : Math.round((completed / total) * 100);
  return { completed, total, percent };
}

export function buildProjectDashboardSummary(
  project: Project,
  client: Client | undefined,
  tasks: Task[],
  groups: TaskGroup[],
  subBoardTabOrder: ProjectBoardType[],
  customBoards: CustomBoard[],
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): ProjectDashboardSummary {
  const projectTasks = tasks.filter(
    (task) => task.clientId === project.clientId && task.projectId === project.id
  );

  const boardOrder: ProjectBoardType[] = [
    'main',
    ...getProjectSubBoardOrder(project.id, subBoardTabOrder, customBoards),
  ];

  const boards: BoardProgressSummary[] = boardOrder.map((boardType) => {
    const scoped =
      boardType === 'main'
        ? projectTasks
        : projectTasks.filter((task) => taskBranchBoardType(task, groups) === boardType);
    return {
      boardType,
      label: getBoardLabel(boardType, customBoards),
      ...computeProgressForTasks(
        scoped,
        groups,
        boardTaskStatuses,
        projectBoardTaskStatuses,
        project.id
      ),
    };
  });

  const overall = boards.find((board) => board.boardType === 'main') ?? boards[0]!;

  return {
    project,
    clientName: client?.name ?? 'Unknown client',
    overall,
    boards,
    durationLabel: formatProjectDuration(project, projectTasks, groups),
    budgetHoursLabel: formatBudgetHoursDisplay(project),
  };
}

export function buildClientDashboardSummaries(
  clients: Client[],
  projects: Project[],
  tasks: Task[],
  groups: TaskGroup[],
  subBoardTabOrder: ProjectBoardType[],
  customBoards: CustomBoard[],
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): { client: Client; projects: ProjectDashboardSummary[] }[] {
  const clientById = new Map(clients.map((client) => [client.id, client]));

  return [...clients]
    .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
    .map((client) => ({
      client,
      projects: projects
        .filter((project) => project.clientId === client.id && !project.isTemplate)
        .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
        .map((project) =>
          buildProjectDashboardSummary(
            project,
            clientById.get(client.id),
            tasks,
            groups,
            subBoardTabOrder,
            customBoards,
            boardTaskStatuses,
            projectBoardTaskStatuses
          )
        ),
    }))
    .filter((section) => section.projects.length > 0);
}
