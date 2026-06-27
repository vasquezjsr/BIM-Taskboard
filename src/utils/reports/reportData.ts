import type {
  Client,
  CustomBoard,
  Employee,
  OrgTeam,
  Project,
  ProjectBoardType,
  Task,
  TaskGroup,
  TaskStatusDefinition,
  TimeEntry,
} from '../../types';
import { getBoardLabel, getProjectSubBoardOrder } from '../../types';
import { canViewEmployeeTime } from '../permissions';
import { employeeNameById } from '../orgChart';
import type { EmployeeReportsToMap } from '../orgChart';
import {
  resolveLevelGroupForTask,
  resolveTradeGroupForTask,
  taskBranchBoardType,
} from '../groupRows';
import {
  getBoardTaskStatuses,
  isCompleteStatusForTask,
  statusBoardForTask,
  statusLabel,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from '../taskStatuses';
import { formatEntryTimeRange, getEntryTaskLabel } from '../timeEntry';
import {
  entriesInRange,
  formatCalendarPeriodLabel,
  getViewRange,
  sumHours,
} from '../timeCalendar';
import type { CalendarView } from '../timeCalendar';

export interface ReportDataContext {
  clients: Client[];
  projects: Project[];
  employees: Employee[];
  tasks: Task[];
  taskGroups: TaskGroup[];
  timeEntries: TimeEntry[];
  orgTeams: OrgTeam[];
  employeeReportsTo: EmployeeReportsToMap;
  boardTaskStatuses: BoardTaskStatusesMap;
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap;
  customBoards: CustomBoard[];
  subBoardTabOrder: ProjectBoardType[];
  currentUserId: string | null;
  generatedAt: string;
  generatedByName: string;
}

export interface ReportPeriod {
  kind: 'week' | 'month' | 'day';
  anchorDate: string;
  start: string;
  end: string;
  label: string;
}

export function buildReportPeriod(kind: 'week' | 'month' | 'day', anchorDate: string): ReportPeriod {
  const range = getViewRange(anchorDate, kind as CalendarView);
  return {
    kind,
    anchorDate,
    start: range.start,
    end: range.end,
    label: formatCalendarPeriodLabel(anchorDate, kind as CalendarView),
  };
}

export function visibleTimeEntries(ctx: ReportDataContext): TimeEntry[] {
  return ctx.timeEntries.filter((entry) =>
    canViewEmployeeTime(
      ctx.currentUserId,
      entry.employeeId,
      ctx.employees,
      ctx.employeeReportsTo
    )
  );
}

export function activeProjects(ctx: ReportDataContext): Project[] {
  return ctx.projects
    .filter((project) => !project.isTemplate)
    .sort((a, b) => a.name.localeCompare(b.name));
}

export function filterContextForProjects(
  ctx: ReportDataContext,
  projectIds: string[] | undefined
): ReportDataContext {
  if (!projectIds || projectIds.length === 0) return ctx;

  const idSet = new Set(projectIds);
  return {
    ...ctx,
    projects: ctx.projects.filter((project) => idSet.has(project.id)),
    tasks: ctx.tasks.filter((task) => task.projectId == null || idSet.has(task.projectId)),
    taskGroups: ctx.taskGroups.filter((group) => idSet.has(group.projectId)),
    timeEntries: ctx.timeEntries.filter(
      (entry) => entry.projectId == null || idSet.has(entry.projectId)
    ),
    customBoards: ctx.customBoards.filter((board) => idSet.has(board.projectId)),
    projectBoardTaskStatuses: Object.fromEntries(
      Object.entries(ctx.projectBoardTaskStatuses).filter(([projectId]) => idSet.has(projectId))
    ),
  };
}

export function clientName(ctx: ReportDataContext, id: string | null): string {
  if (!id) return '—';
  return ctx.clients.find((client) => client.id === id)?.name ?? '—';
}

export function projectName(ctx: ReportDataContext, id: string | null): string {
  if (!id) return '—';
  return ctx.projects.find((project) => project.id === id)?.name ?? '—';
}

export function employeeName(ctx: ReportDataContext, id: string | null): string {
  if (!id) return '—';
  return employeeNameById(ctx.employees, id);
}

export function formatStatus(status: string): string {
  return status
    .split('-')
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

function collectAllStatusDefinitions(ctx: ReportDataContext): TaskStatusDefinition[] {
  const byId = new Map<string, TaskStatusDefinition>();
  const add = (statuses: TaskStatusDefinition[] | undefined) => {
    if (!statuses) return;
    for (const status of statuses) byId.set(status.id, status);
  };

  for (const statuses of Object.values(ctx.boardTaskStatuses)) add(statuses);
  for (const projectStatuses of Object.values(ctx.projectBoardTaskStatuses)) {
    for (const statuses of Object.values(projectStatuses)) add(statuses);
  }

  return [...byId.values()];
}

export function resolveTaskStatusLabel(ctx: ReportDataContext, task: Task): string {
  const statuses = getBoardTaskStatuses(
    statusBoardForTask(task, ctx.taskGroups),
    ctx.boardTaskStatuses,
    task.projectId,
    ctx.projectBoardTaskStatuses
  );
  return statusLabel(task.status, statuses);
}

export function resolveStatusIdLabel(ctx: ReportDataContext, statusId: string, task?: Task): string {
  if (task) return resolveTaskStatusLabel(ctx, task);
  const known = statusLabel(statusId, collectAllStatusDefinitions(ctx));
  return known === statusId ? formatStatus(statusId) : known;
}

export function isTaskComplete(ctx: ReportDataContext, task: Task): boolean {
  return isCompleteStatusForTask(task, ctx.boardTaskStatuses);
}

export function isOpenTask(ctx: ReportDataContext, task: Task): boolean {
  return task.boardType !== 'employee' && !isTaskComplete(ctx, task);
}

export interface ProjectProgressRow {
  client: string;
  project: string;
  scope: string;
  complete: string;
  progress: string;
  breakdown: string;
}

function boardUsesTradeLevelBreakdown(boardType: ProjectBoardType): boolean {
  return boardType === 'detailers' || boardType === 'deliverables';
}

function sortTaskGroups(groups: TaskGroup[]): TaskGroup[] {
  return [...groups].sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
}

export function formatProgressBreakdown(ctx: ReportDataContext, tasks: Task[]): string {
  const statusCounts = new Map<string, number>();
  for (const task of tasks) {
    const label = resolveTaskStatusLabel(ctx, task);
    statusCounts.set(label, (statusCounts.get(label) ?? 0) + 1);
  }
  return [...statusCounts.entries()]
    .map(([status, count]) => `${status}: ${count}`)
    .join(' · ');
}

function formatProgressCounts(
  ctx: ReportDataContext,
  tasks: Task[]
): Pick<ProjectProgressRow, 'complete' | 'progress'> {
  const completeCount = tasks.filter((task) => isTaskComplete(ctx, task)).length;
  const total = tasks.length;
  const pct = total === 0 ? 0 : Math.round((completeCount / total) * 100);
  return {
    complete: `${completeCount}/${total}`,
    progress: `${pct}%`,
  };
}

function appendTradeLevelBreakdownRows(
  ctx: ReportDataContext,
  boardLabel: string,
  boardTasks: Task[],
  rows: ProjectProgressRow[]
): void {
  const tasksByTrade = new Map<string, Task[]>();
  const ungroupedByTrade: Task[] = [];

  for (const task of boardTasks) {
    const trade = resolveTradeGroupForTask(task.groupId, ctx.taskGroups);
    if (!trade) {
      ungroupedByTrade.push(task);
      continue;
    }
    const bucket = tasksByTrade.get(trade.id) ?? [];
    bucket.push(task);
    tasksByTrade.set(trade.id, bucket);
  }

  const trades = sortTaskGroups(
    [...tasksByTrade.keys()]
      .map((tradeId) => ctx.taskGroups.find((group) => group.id === tradeId))
      .filter((group): group is TaskGroup => Boolean(group))
  );

  for (const trade of trades) {
    const tradeTasks = tasksByTrade.get(trade.id) ?? [];
    const tradeCounts = formatProgressCounts(ctx, tradeTasks);
    rows.push({
      client: '',
      project: '',
      scope: `${boardLabel} · ${trade.name}`,
      ...tradeCounts,
      breakdown: formatProgressBreakdown(ctx, tradeTasks),
    });

    const tasksByLevel = new Map<string, Task[]>();
    const ungroupedByLevel: Task[] = [];

    for (const task of tradeTasks) {
      const level = resolveLevelGroupForTask(task.groupId, ctx.taskGroups);
      if (!level) {
        ungroupedByLevel.push(task);
        continue;
      }
      const bucket = tasksByLevel.get(level.id) ?? [];
      bucket.push(task);
      tasksByLevel.set(level.id, bucket);
    }

    const levels = sortTaskGroups(
      [...tasksByLevel.keys()]
        .map((levelId) => ctx.taskGroups.find((group) => group.id === levelId))
        .filter((group): group is TaskGroup => Boolean(group))
    );

    for (const level of levels) {
      const levelTasks = tasksByLevel.get(level.id) ?? [];
      const levelCounts = formatProgressCounts(ctx, levelTasks);
      rows.push({
        client: '',
        project: '',
        scope: `${boardLabel} · ${trade.name} · ${level.name}`,
        ...levelCounts,
        breakdown: formatProgressBreakdown(ctx, levelTasks),
      });
    }

    if (ungroupedByLevel.length > 0) {
      const levelCounts = formatProgressCounts(ctx, ungroupedByLevel);
      rows.push({
        client: '',
        project: '',
        scope: `${boardLabel} · ${trade.name} · Ungrouped`,
        ...levelCounts,
        breakdown: formatProgressBreakdown(ctx, ungroupedByLevel),
      });
    }
  }

  if (ungroupedByTrade.length > 0) {
    const tradeCounts = formatProgressCounts(ctx, ungroupedByTrade);
    rows.push({
      client: '',
      project: '',
      scope: `${boardLabel} · Ungrouped`,
      ...tradeCounts,
      breakdown: formatProgressBreakdown(ctx, ungroupedByTrade),
    });
  }
}

export function buildProjectProgressRows(
  ctx: ReportDataContext,
  project: Project
): ProjectProgressRow[] {
  const client = clientName(ctx, project.clientId);
  const projectTasks = ctx.tasks.filter(
    (task) => task.projectId === project.id && task.boardType !== 'employee'
  );
  if (projectTasks.length === 0) return [];

  const rows: ProjectProgressRow[] = [];
  const overallCounts = formatProgressCounts(ctx, projectTasks);
  rows.push({
    client,
    project: project.name,
    scope: 'Overall',
    ...overallCounts,
    breakdown: formatProgressBreakdown(ctx, projectTasks),
  });

  const boardOrder: ProjectBoardType[] = [
    'main',
    ...getProjectSubBoardOrder(project.id, ctx.subBoardTabOrder, ctx.customBoards),
  ];

  for (const boardType of boardOrder) {
    const boardTasks = projectTasks.filter(
      (task) => taskBranchBoardType(task, ctx.taskGroups) === boardType
    );
    if (boardTasks.length === 0) continue;

    const boardLabel = getBoardLabel(boardType, ctx.customBoards);
    const boardCounts = formatProgressCounts(ctx, boardTasks);
    rows.push({
      client: '',
      project: '',
      scope: boardLabel,
      ...boardCounts,
      breakdown: formatProgressBreakdown(ctx, boardTasks),
    });

    if (boardUsesTradeLevelBreakdown(boardType)) {
      appendTradeLevelBreakdownRows(ctx, boardLabel, boardTasks, rows);
    }
  }

  return rows;
}

export interface ReportProjectGroup {
  clientId: string;
  clientName: string;
  projects: Project[];
}

export function buildReportProjectGroups(
  clients: Client[],
  projects: Project[]
): ReportProjectGroup[] {
  const activeProjects = projects
    .filter((project) => !project.isTemplate)
    .sort((a, b) => a.name.localeCompare(b.name));

  const clientById = new Map(clients.map((client) => [client.id, client]));
  const projectsByClientId = new Map<string, Project[]>();

  for (const project of activeProjects) {
    const bucket = projectsByClientId.get(project.clientId) ?? [];
    bucket.push(project);
    projectsByClientId.set(project.clientId, bucket);
  }

  const groups: ReportProjectGroup[] = [];

  for (const client of [...clients].sort((a, b) => a.name.localeCompare(b.name))) {
    const clientProjects = projectsByClientId.get(client.id);
    if (!clientProjects?.length) continue;
    groups.push({
      clientId: client.id,
      clientName: client.name,
      projects: [...clientProjects].sort((a, b) => a.name.localeCompare(b.name)),
    });
    projectsByClientId.delete(client.id);
  }

  for (const [clientId, clientProjects] of projectsByClientId) {
    if (clientProjects.length === 0) continue;
    groups.push({
      clientId,
      clientName: clientById.get(clientId)?.name ?? 'Other projects',
      projects: [...clientProjects].sort((a, b) => a.name.localeCompare(b.name)),
    });
  }

  return groups;
}

export function timeEntriesInPeriod(ctx: ReportDataContext, period: ReportPeriod): TimeEntry[] {
  return entriesInRange(visibleTimeEntries(ctx), period.start, period.end);
}

export function entryRows(
  ctx: ReportDataContext,
  entries: TimeEntry[]
): string[][] {
  return entries
    .slice()
    .sort((a, b) => a.date.localeCompare(b.date) || a.createdAt.localeCompare(b.createdAt))
    .map((entry) => [
      entry.date,
      employeeName(ctx, entry.employeeId),
      getEntryTaskLabel(entry, ctx.tasks),
      formatEntryTimeRange(entry),
      entry.hours.toLocaleString(),
      clientName(ctx, entry.clientId),
      projectName(ctx, entry.projectId),
      entry.note || '',
    ]);
}

export function totalHours(entries: TimeEntry[]): number {
  return sumHours(entries);
}

export function sanitizeFilename(name: string): string {
  return name.replace(/[<>:"/\\|?*]+/g, '-').replace(/\s+/g, '-');
}
