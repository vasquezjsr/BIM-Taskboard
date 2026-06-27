import type { MainTab } from '../../types';

export type ReportId =
  | 'time-weekly-by-project'
  | 'time-weekly-summary'
  | 'time-weekly-by-employee'
  | 'time-monthly-summary'
  | 'time-daily-detail'
  | 'clients-portfolio'
  | 'clients-budget-status'
  | 'tasks-status-summary'
  | 'tasks-by-assignee'
  | 'tasks-open-list'
  | 'tasks-project-progress'
  | 'org-team-roster'
  | 'org-reporting-structure';

export interface ReportDefinition {
  id: ReportId;
  label: string;
  description: string;
  tabs: MainTab[];
  category: string;
  /** Generates one PDF file per project when applicable */
  perProject?: boolean;
  periodKind: 'week' | 'month' | 'day' | 'none';
}

export const REPORT_CATEGORIES = [
  'Time Tracking',
  'Clients & Projects',
  'Task Board',
  'Organization',
] as const;

export const REPORT_DEFINITIONS: ReportDefinition[] = [
  {
    id: 'time-weekly-by-project',
    label: 'Weekly Time by Project',
    description: 'Separate PDF for each project with daily time entries for the week.',
    tabs: ['time-tracking', 'clients'],
    category: 'Time Tracking',
    perProject: true,
    periodKind: 'week',
  },
  {
    id: 'time-weekly-summary',
    label: 'Weekly Time Summary',
    description: 'All projects combined — totals by project and employee.',
    tabs: ['time-tracking'],
    category: 'Time Tracking',
    periodKind: 'week',
  },
  {
    id: 'time-weekly-by-employee',
    label: 'Weekly Time by Employee',
    description: 'Hours logged per employee with project breakdown.',
    tabs: ['time-tracking'],
    category: 'Time Tracking',
    periodKind: 'week',
  },
  {
    id: 'time-monthly-summary',
    label: 'Monthly Time Summary',
    description: 'Month-to-date hours by project and employee.',
    tabs: ['time-tracking'],
    category: 'Time Tracking',
    periodKind: 'month',
  },
  {
    id: 'time-daily-detail',
    label: 'Daily Time Detail',
    description: 'Line-item entries for a single day.',
    tabs: ['time-tracking'],
    category: 'Time Tracking',
    periodKind: 'day',
  },
  {
    id: 'clients-portfolio',
    label: 'Client & Project Portfolio',
    description: 'All clients, active projects, billing type, and team assignments.',
    tabs: ['clients'],
    category: 'Clients & Projects',
    periodKind: 'none',
  },
  {
    id: 'clients-budget-status',
    label: 'Project Budget & Hours Status',
    description: 'Budget hours, hours spent, and time logged per project.',
    tabs: ['clients', 'time-tracking'],
    category: 'Clients & Projects',
    periodKind: 'none',
  },
  {
    id: 'tasks-status-summary',
    label: 'Task Status Summary',
    description: 'Task counts grouped by status across all projects.',
    tabs: ['task-board', 'clients'],
    category: 'Task Board',
    periodKind: 'none',
  },
  {
    id: 'tasks-by-assignee',
    label: 'Tasks by Assignee',
    description: 'Open and in-progress tasks grouped by assignee.',
    tabs: ['task-board'],
    category: 'Task Board',
    periodKind: 'none',
  },
  {
    id: 'tasks-open-list',
    label: 'Open Tasks List',
    description: 'Detailed list of all non-complete tasks with due dates.',
    tabs: ['task-board', 'clients'],
    category: 'Task Board',
    periodKind: 'none',
  },
  {
    id: 'tasks-project-progress',
    label: 'Project Task Progress',
    description:
      'Completion and status breakdown per project, with board rows and trade/level detail for Detailers and Deliverables.',
    tabs: ['task-board', 'clients'],
    category: 'Task Board',
    periodKind: 'none',
  },
  {
    id: 'org-team-roster',
    label: 'Team Roster',
    description: 'Org teams with member names and roles.',
    tabs: ['org-chart'],
    category: 'Organization',
    periodKind: 'none',
  },
  {
    id: 'org-reporting-structure',
    label: 'Reporting Structure',
    description: 'Manager-to-direct-report relationships.',
    tabs: ['org-chart'],
    category: 'Organization',
    periodKind: 'none',
  },
];

export function reportsForTab(tab: MainTab): ReportDefinition[] {
  return REPORT_DEFINITIONS.filter((report) => report.tabs.includes(tab));
}

export function reportById(id: ReportId): ReportDefinition | undefined {
  return REPORT_DEFINITIONS.find((report) => report.id === id);
}

const WORKSPACE_ONLY_REPORTS = new Set<ReportId>([
  'org-team-roster',
  'org-reporting-structure',
]);

export function reportNeedsProjectScope(reportId: ReportId): boolean {
  return !WORKSPACE_ONLY_REPORTS.has(reportId);
}

export function selectionNeedsProjects(reportIds: ReportId[]): boolean {
  return reportIds.some(reportNeedsProjectScope);
}
