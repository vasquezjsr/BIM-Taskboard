import { jsPDF } from 'jspdf';
import { autoTable } from 'jspdf-autotable';
import type { ReportId } from './definitions';
import { reportById, reportNeedsProjectScope, selectionNeedsProjects } from './definitions';
import type { ReportDataContext, ReportPeriod } from './reportData';
import {
  activeProjects,
  buildProjectProgressRows,
  buildReportPeriod,
  clientName,
  employeeName,
  entryRows,
  filterContextForProjects,
  isOpenTask,
  projectName,
  resolveTaskStatusLabel,
  sanitizeFilename,
  timeEntriesInPeriod,
  totalHours,
  visibleTimeEntries,
} from './reportData';
import { employeeNameById } from '../orgChart';
import { formatEntryTimeRange, getEntryTaskLabel } from '../timeEntry';

export interface GeneratedReportFile {
  filename: string;
  blob: Blob;
}

export interface GenerateReportsOptions {
  reportIds: ReportId[];
  periodDate: string;
  projectIds?: string[];
}

const TABLE_HEAD = [['Date', 'Employee', 'Task', 'Time', 'Hours', 'Client', 'Project', 'Note']];

function createDoc(orientation: 'portrait' | 'landscape' = 'portrait'): jsPDF {
  return new jsPDF({ orientation, unit: 'pt', format: 'letter' });
}

function addReportHeader(
  doc: jsPDF,
  ctx: ReportDataContext,
  title: string,
  subtitle?: string
): number {
  doc.setFont('helvetica', 'bold');
  doc.setFontSize(16);
  doc.text('BIM Boardroom', 40, 40);
  doc.setFont('helvetica', 'normal');
  doc.setFontSize(10);
  doc.setTextColor(100);
  doc.text(`Generated ${new Date(ctx.generatedAt).toLocaleString()} by ${ctx.generatedByName}`, 40, 56);
  doc.setTextColor(0);

  doc.setFont('helvetica', 'bold');
  doc.setFontSize(14);
  doc.text(title, 40, 80);

  let y = 96;
  if (subtitle) {
    doc.setFont('helvetica', 'normal');
    doc.setFontSize(11);
    doc.setTextColor(80);
    doc.text(subtitle, 40, y);
    doc.setTextColor(0);
    y += 18;
  }

  return y;
}

function docToBlob(doc: jsPDF): Blob {
  return doc.output('blob');
}

function generateWeeklyByProject(
  ctx: ReportDataContext,
  period: ReportPeriod
): GeneratedReportFile[] {
  const files: GeneratedReportFile[] = [];
  const allEntries = timeEntriesInPeriod(ctx, period);

  for (const project of activeProjects(ctx)) {
    const entries = allEntries.filter((entry) => entry.projectId === project.id);

    const doc = createDoc();
    const client = clientName(ctx, project.clientId);
    const y = addReportHeader(
      doc,
      ctx,
      `Weekly Time Report — ${project.name}`,
      `${client} · ${period.label} · ${totalHours(entries).toLocaleString()} hrs`
    );

    if (entries.length === 0) {
      doc.setFont('helvetica', 'normal');
      doc.setFontSize(11);
      doc.setTextColor(100);
      doc.text('No time logged for this project during this period.', 40, y + 12);
    } else {
      autoTable(doc, {
        head: TABLE_HEAD,
        body: entryRows(ctx, entries),
        startY: y + 8,
        styles: { fontSize: 8, cellPadding: 4 },
        headStyles: { fillColor: [61, 53, 96] },
        margin: { left: 40, right: 40 },
      });
    }

    files.push({
      filename: `${sanitizeFilename(`Weekly-Time-${project.name}-${period.start}`)}.pdf`,
      blob: docToBlob(doc),
    });
  }

  return files;
}

function generateWeeklySummary(ctx: ReportDataContext, period: ReportPeriod): GeneratedReportFile {
  const entries = timeEntriesInPeriod(ctx, period);
  const doc = createDoc();
  const y = addReportHeader(
    doc,
    ctx,
    'Weekly Time Summary',
    `${period.label} · ${totalHours(entries).toLocaleString()} hrs total`
  );

  const byProject = new Map<string, number>();
  const byEmployee = new Map<string, number>();
  for (const entry of entries) {
    const projectKey = projectName(ctx, entry.projectId);
    byProject.set(projectKey, (byProject.get(projectKey) ?? 0) + entry.hours);
    const employeeKey = employeeName(ctx, entry.employeeId);
    byEmployee.set(employeeKey, (byEmployee.get(employeeKey) ?? 0) + entry.hours);
  }

  autoTable(doc, {
    head: [['Project', 'Hours']],
    body: [...byProject.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([name, hours]) => [name, hours.toLocaleString()]),
    startY: y + 8,
    styles: { fontSize: 9 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  const afterProject = (doc as jsPDF & { lastAutoTable?: { finalY: number } }).lastAutoTable?.finalY ?? y + 40;

  autoTable(doc, {
    head: [['Employee', 'Hours']],
    body: [...byEmployee.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([name, hours]) => [name, hours.toLocaleString()]),
    startY: afterProject + 20,
    styles: { fontSize: 9 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  const afterEmployee = (doc as jsPDF & { lastAutoTable?: { finalY: number } }).lastAutoTable?.finalY ?? afterProject + 40;

  autoTable(doc, {
    head: TABLE_HEAD,
    body: entryRows(ctx, entries),
    startY: afterEmployee + 20,
    styles: { fontSize: 8, cellPadding: 3 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return {
    filename: `${sanitizeFilename(`Weekly-Time-Summary-${period.start}`)}.pdf`,
    blob: docToBlob(doc),
  };
}

function generateWeeklyByEmployee(ctx: ReportDataContext, period: ReportPeriod): GeneratedReportFile {
  const entries = timeEntriesInPeriod(ctx, period);
  const doc = createDoc();
  const y = addReportHeader(doc, ctx, 'Weekly Time by Employee', period.label);

  const grouped = new Map<string, typeof entries>();
  for (const entry of entries) {
    const key = entry.employeeId;
    const bucket = grouped.get(key);
    if (bucket) bucket.push(entry);
    else grouped.set(key, [entry]);
  }

  let cursorY = y + 8;
  for (const [employeeId, employeeEntries] of [...grouped.entries()].sort((a, b) =>
    employeeName(ctx, a[0]).localeCompare(employeeName(ctx, b[0]))
  )) {
    doc.setFont('helvetica', 'bold');
    doc.setFontSize(11);
    doc.text(
      `${employeeName(ctx, employeeId)} — ${totalHours(employeeEntries).toLocaleString()} hrs`,
      40,
      cursorY
    );
    cursorY += 14;

    autoTable(doc, {
      head: [['Date', 'Task', 'Time', 'Hours', 'Project', 'Note']],
      body: employeeEntries
        .slice()
        .sort((a, b) => a.date.localeCompare(b.date))
        .map((entry) => [
          entry.date,
          getEntryTaskLabel(entry, ctx.tasks),
          formatEntryTimeRange(entry),
          entry.hours.toLocaleString(),
          projectName(ctx, entry.projectId),
          entry.note || '',
        ]),
      startY: cursorY,
      styles: { fontSize: 8, cellPadding: 3 },
      headStyles: { fillColor: [61, 53, 96] },
      margin: { left: 40, right: 40 },
    });

    cursorY = ((doc as jsPDF & { lastAutoTable?: { finalY: number } }).lastAutoTable?.finalY ?? cursorY) + 20;
    if (cursorY > 680) {
      doc.addPage();
      cursorY = 40;
    }
  }

  return {
    filename: `${sanitizeFilename(`Weekly-Time-by-Employee-${period.start}`)}.pdf`,
    blob: docToBlob(doc),
  };
}

function generateMonthlySummary(ctx: ReportDataContext, period: ReportPeriod): GeneratedReportFile {
  const entries = timeEntriesInPeriod(ctx, period);
  const doc = createDoc();
  const y = addReportHeader(
    doc,
    ctx,
    'Monthly Time Summary',
    `${period.label} · ${totalHours(entries).toLocaleString()} hrs total`
  );

  autoTable(doc, {
    head: TABLE_HEAD,
    body: entryRows(ctx, entries),
    startY: y + 8,
    styles: { fontSize: 8, cellPadding: 3 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return {
    filename: `${sanitizeFilename(`Monthly-Time-${period.start}`)}.pdf`,
    blob: docToBlob(doc),
  };
}

function generateDailyDetail(ctx: ReportDataContext, period: ReportPeriod): GeneratedReportFile {
  const entries = timeEntriesInPeriod(ctx, period);
  const doc = createDoc();
  const y = addReportHeader(
    doc,
    ctx,
    'Daily Time Detail',
    `${period.label} · ${totalHours(entries).toLocaleString()} hrs total`
  );

  autoTable(doc, {
    head: TABLE_HEAD,
    body: entryRows(ctx, entries),
    startY: y + 8,
    styles: { fontSize: 8, cellPadding: 4 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return {
    filename: `${sanitizeFilename(`Daily-Time-${period.start}`)}.pdf`,
    blob: docToBlob(doc),
  };
}

function generateClientsPortfolio(ctx: ReportDataContext): GeneratedReportFile {
  const doc = createDoc();
  const y = addReportHeader(doc, ctx, 'Client & Project Portfolio');

  const rows: string[][] = [];
  for (const client of [...ctx.clients].sort((a, b) => a.name.localeCompare(b.name))) {
    const projects = activeProjects(ctx).filter((project) => project.clientId === client.id);
    if (projects.length === 0) {
      rows.push([client.name, '—', '—', '—', '—']);
      continue;
    }
    for (const project of projects) {
      rows.push([
        client.name,
        project.name,
        project.billingType === 'lump-sum' ? 'Lump Sum' : 'Time & Material',
        project.budgetHours?.toLocaleString() ?? '—',
        [...project.detailerIds, ...project.supportIds]
          .map((id) => employeeName(ctx, id))
          .filter(Boolean)
          .join(', ') || '—',
      ]);
    }
  }

  autoTable(doc, {
    head: [['Client', 'Project', 'Billing', 'Budget Hrs', 'Team']],
    body: rows,
    startY: y + 8,
    styles: { fontSize: 8, cellPadding: 4 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return { filename: 'Client-Project-Portfolio.pdf', blob: docToBlob(doc) };
}

function generateBudgetStatus(ctx: ReportDataContext): GeneratedReportFile {
  const doc = createDoc();
  const y = addReportHeader(doc, ctx, 'Project Budget & Hours Status');
  const loggedByProject = new Map<string, number>();
  for (const entry of visibleTimeEntries(ctx)) {
    if (!entry.projectId) continue;
    loggedByProject.set(
      entry.projectId,
      (loggedByProject.get(entry.projectId) ?? 0) + entry.hours
    );
  }

  const rows = activeProjects(ctx).map((project) => {
    const logged = loggedByProject.get(project.id) ?? 0;
    const budget = project.budgetHours;
    const spent = project.totalHoursSpent;
    const remaining =
      budget !== null ? Math.max(0, budget - logged).toLocaleString() : '—';
    return [
      clientName(ctx, project.clientId),
      project.name,
      budget?.toLocaleString() ?? '—',
      spent?.toLocaleString() ?? '—',
      logged.toLocaleString(),
      remaining,
    ];
  });

  autoTable(doc, {
    head: [['Client', 'Project', 'Budget Hrs', 'Spent Hrs', 'Time Logged', 'Budget Remaining']],
    body: rows,
    startY: y + 8,
    styles: { fontSize: 8, cellPadding: 4 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return { filename: 'Project-Budget-Hours-Status.pdf', blob: docToBlob(doc) };
}

function generateTaskStatusSummary(ctx: ReportDataContext): GeneratedReportFile {
  const doc = createDoc();
  const y = addReportHeader(doc, ctx, 'Task Status Summary');
  const counts = new Map<string, number>();
  for (const task of ctx.tasks) {
    if (task.boardType === 'employee') continue;
    const label = resolveTaskStatusLabel(ctx, task);
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }

  autoTable(doc, {
    head: [['Status', 'Task Count']],
    body: [...counts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([status, count]) => [status, count.toLocaleString()]),
    startY: y + 8,
    styles: { fontSize: 9 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return { filename: 'Task-Status-Summary.pdf', blob: docToBlob(doc) };
}

function generateTasksByAssignee(ctx: ReportDataContext): GeneratedReportFile {
  const doc = createDoc();
  const y = addReportHeader(doc, ctx, 'Tasks by Assignee');
  const openTasks = ctx.tasks.filter((task) => isOpenTask(ctx, task));

  const rows: string[][] = [];
  for (const task of openTasks.sort((a, b) => a.title.localeCompare(b.title))) {
    const assignees =
      task.assigneeIds.map((id) => employeeName(ctx, id)).join(', ') || 'Unassigned';
    rows.push([
      assignees,
      task.title,
      resolveTaskStatusLabel(ctx, task),
      projectName(ctx, task.projectId),
      task.dueDate ?? '—',
    ]);
  }

  autoTable(doc, {
    head: [['Assignee', 'Task', 'Status', 'Project', 'Due']],
    body: rows,
    startY: y + 8,
    styles: { fontSize: 8, cellPadding: 3 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return { filename: 'Tasks-by-Assignee.pdf', blob: docToBlob(doc) };
}

function generateOpenTasksList(ctx: ReportDataContext): GeneratedReportFile {
  const doc = createDoc();
  const y = addReportHeader(doc, ctx, 'Open Tasks List');
  const openTasks = ctx.tasks.filter((task) => isOpenTask(ctx, task));

  autoTable(doc, {
    head: [['Task', 'Status', 'Project', 'Assignees', 'Due']],
    body: openTasks
      .slice()
      .sort((a, b) => (a.dueDate ?? '9999').localeCompare(b.dueDate ?? '9999'))
      .map((task) => [
        task.title,
        resolveTaskStatusLabel(ctx, task),
        projectName(ctx, task.projectId),
        task.assigneeIds.map((id) => employeeName(ctx, id)).join(', ') || 'Unassigned',
        task.dueDate ?? '—',
      ]),
    startY: y + 8,
    styles: { fontSize: 8, cellPadding: 3 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return { filename: 'Open-Tasks-List.pdf', blob: docToBlob(doc) };
}

function generateProjectProgress(ctx: ReportDataContext): GeneratedReportFile {
  const doc = createDoc('landscape');
  const margin = 32;
  const pageWidth = doc.internal.pageSize.getWidth();
  const tableWidth = pageWidth - margin * 2;
  const y = addReportHeader(doc, ctx, 'Project Task Progress');
  const rows: string[][] = [];

  for (const project of activeProjects(ctx)) {
    for (const row of buildProjectProgressRows(ctx, project)) {
      rows.push([
        row.client,
        row.project,
        row.scope,
        row.complete,
        row.progress,
        row.breakdown,
      ]);
    }
  }

  autoTable(doc, {
    head: [['Client', 'Project', 'Scope', 'Complete', 'Progress', 'Status Breakdown']],
    body: rows,
    startY: y + 6,
    tableWidth,
    styles: {
      fontSize: 7,
      cellPadding: { top: 1.5, right: 3, bottom: 1.5, left: 3 },
      lineWidth: 0.25,
      lineColor: [180, 180, 180],
      valign: 'middle',
      overflow: 'linebreak',
    },
    headStyles: {
      fillColor: [61, 53, 96],
      fontSize: 7,
      cellPadding: { top: 2, right: 3, bottom: 2, left: 3 },
    },
    margin: { left: margin, right: margin },
    columnStyles: {
      0: { cellWidth: 96 },
      1: { cellWidth: 104 },
      2: { cellWidth: 156 },
      3: { cellWidth: 52, halign: 'center' },
      4: { cellWidth: 48, halign: 'center' },
      5: { cellWidth: 'auto' },
    },
  });

  return { filename: 'Project-Task-Progress.pdf', blob: docToBlob(doc) };
}

function generateTeamRoster(ctx: ReportDataContext): GeneratedReportFile {
  const doc = createDoc();
  const y = addReportHeader(doc, ctx, 'Team Roster');
  const rows: string[][] = [];

  for (const team of [...ctx.orgTeams].sort((a, b) => a.sortOrder - b.sortOrder)) {
    if (team.memberIds.length === 0) {
      rows.push([team.name, '—', '—']);
      continue;
    }
    for (const memberId of team.memberIds) {
      const employee = ctx.employees.find((item) => item.id === memberId);
      rows.push([team.name, employee?.name ?? memberId, employee?.role ?? '—']);
    }
  }

  autoTable(doc, {
    head: [['Team', 'Member', 'Role']],
    body: rows,
    startY: y + 8,
    styles: { fontSize: 9 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return { filename: 'Team-Roster.pdf', blob: docToBlob(doc) };
}

function generateReportingStructure(ctx: ReportDataContext): GeneratedReportFile {
  const doc = createDoc();
  const y = addReportHeader(doc, ctx, 'Reporting Structure');
  const rows: string[][] = [];

  for (const employee of [...ctx.employees].sort((a, b) => a.name.localeCompare(b.name))) {
    const managerIds = ctx.employeeReportsTo[employee.id] ?? [];
    const managers =
      managerIds.map((id) => employeeNameById(ctx.employees, id)).join(', ') || '—';
    rows.push([employee.name, managers]);
  }

  autoTable(doc, {
    head: [['Employee', 'Reports To']],
    body: rows,
    startY: y + 8,
    styles: { fontSize: 9 },
    headStyles: { fillColor: [61, 53, 96] },
    margin: { left: 40, right: 40 },
  });

  return { filename: 'Reporting-Structure.pdf', blob: docToBlob(doc) };
}

export function generateReports(
  ctx: ReportDataContext,
  options: GenerateReportsOptions
): GeneratedReportFile[] {
  const files: GeneratedReportFile[] = [];

  if (selectionNeedsProjects(options.reportIds)) {
    if (!options.projectIds || options.projectIds.length === 0) {
      throw new Error('Select at least one project.');
    }
  }

  const scopedCtx = filterContextForProjects(ctx, options.projectIds);

  for (const reportId of options.reportIds) {
    const definition = reportById(reportId);
    if (!definition) continue;

    const reportCtx = reportNeedsProjectScope(reportId) ? scopedCtx : ctx;

    const period =
      definition.periodKind === 'none'
        ? null
        : buildReportPeriod(definition.periodKind, options.periodDate);

    switch (reportId) {
      case 'time-weekly-by-project':
        files.push(...generateWeeklyByProject(reportCtx, period!));
        break;
      case 'time-weekly-summary':
        files.push(generateWeeklySummary(reportCtx, period!));
        break;
      case 'time-weekly-by-employee':
        files.push(generateWeeklyByEmployee(reportCtx, period!));
        break;
      case 'time-monthly-summary':
        files.push(generateMonthlySummary(reportCtx, period!));
        break;
      case 'time-daily-detail':
        files.push(generateDailyDetail(reportCtx, period!));
        break;
      case 'clients-portfolio':
        files.push(generateClientsPortfolio(reportCtx));
        break;
      case 'clients-budget-status':
        files.push(generateBudgetStatus(reportCtx));
        break;
      case 'tasks-status-summary':
        files.push(generateTaskStatusSummary(reportCtx));
        break;
      case 'tasks-by-assignee':
        files.push(generateTasksByAssignee(reportCtx));
        break;
      case 'tasks-open-list':
        files.push(generateOpenTasksList(reportCtx));
        break;
      case 'tasks-project-progress':
        files.push(generateProjectProgress(reportCtx));
        break;
      case 'org-team-roster':
        files.push(generateTeamRoster(reportCtx));
        break;
      case 'org-reporting-structure':
        files.push(generateReportingStructure(reportCtx));
        break;
      default:
        break;
    }
  }

  return files;
}

export function downloadReportFiles(files: GeneratedReportFile[]): void {
  for (const file of files) {
    const url = URL.createObjectURL(file.blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = file.filename;
    link.click();
    URL.revokeObjectURL(url);
  }
}

export function buildReportContextFromStore(state: {
  clients: import('../../types').Client[];
  projects: import('../../types').Project[];
  employees: import('../../types').Employee[];
  tasks: import('../../types').Task[];
  taskGroups: import('../../types').TaskGroup[];
  timeEntries: import('../../types').TimeEntry[];
  orgTeams: import('../../types').OrgTeam[];
  employeeReportsTo: import('../orgChart').EmployeeReportsToMap;
  boardTaskStatuses: import('../taskStatuses').BoardTaskStatusesMap;
  projectBoardTaskStatuses: import('../taskStatuses').ProjectBoardTaskStatusesMap;
  customBoards: import('../../types').CustomBoard[];
  subBoardTabOrder: import('../../types').ProjectBoardType[];
  currentUserId: string | null;
}): ReportDataContext {
  const generatedByName = state.currentUserId
    ? employeeNameById(state.employees, state.currentUserId)
    : 'Unknown';

  return {
    clients: state.clients,
    projects: state.projects,
    employees: state.employees,
    tasks: state.tasks,
    taskGroups: state.taskGroups,
    timeEntries: state.timeEntries,
    orgTeams: state.orgTeams,
    employeeReportsTo: state.employeeReportsTo,
    boardTaskStatuses: state.boardTaskStatuses,
    projectBoardTaskStatuses: state.projectBoardTaskStatuses,
    customBoards: state.customBoards,
    subBoardTabOrder: state.subBoardTabOrder,
    currentUserId: state.currentUserId,
    generatedAt: new Date().toISOString(),
    generatedByName,
  };
}
