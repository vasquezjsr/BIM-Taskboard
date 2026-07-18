import type { AppPermission, DashboardAssignments, Employee, OrgCategory } from '../types';
import {
  FAB_STAFF_IDS,
  FIELD_STAFF_IDS,
  PM_STAFF_IDS,
  SHIPPING_STAFF_IDS,
} from '../data/departmentStaff';
import { createDefaultDashboardAssignments } from '../data/dashboards';
import type { EmployeePermissionsMap } from './orgChart';
import { inferOrgCategory, isOrgOwner, orgCategoryLabel } from './orgChart';
import {
  canAccessOrgChart,
  canAddColumns,
  canAssignFabLeadsPermission,
  canAssignFabWorkersPermission,
  canAssignTasks,
  canDeleteTime,
  canEditBudgetHours,
  canEditClientsProjects,
  canEditFabCollab,
  canEditFabStatus,
  canEditPmAssigns,
  canEditTasks,
  canEditWeldLog,
  canViewWeldLogDashboard,
  canFabClock,
  canLogTime,
  canManageColumns,
  canManageOrg,
  canManageStatuses,
  canViewActivityLog,
  canViewDashboard,
  canViewOwnerDashboard,
  canViewTimeTracking,
  canViewVisibilityDashboard,
} from './permissions';

/** Matrix columns on Access Control (permissions + nav visibility). */
export type VisibilityNavColumn =
  | 'budget'
  | 'org'
  | 'columns'
  | 'edit-pm-assigns'
  | 'assign-fab-leads'
  | 'assign-fab-workers'
  | 'edit-fab-status'
  | 'fab-clock'
  | 'edit-weld-log'
  | 'edit-fab-collab'
  | 'log-time'
  | 'delete-time'
  | 'edit-clients-projects'
  | 'edit-tasks'
  | 'assign-tasks'
  | 'manage-statuses'
  | 'add-columns'
  | 'owner'
  | 'pm'
  | 'fab'
  | 'shipping'
  | 'field'
  | 'weld-log'
  | 'time-tracking'
  | 'org-chart'
  | 'activity'
  | 'visibility';

export const VISIBILITY_NAV_COLUMNS: {
  id: VisibilityNavColumn;
  label: string;
  tooltip: string;
  /** permission = capability chips; dashboard = main-nav tab visibility */
  kind: 'permission' | 'dashboard';
}[] = [
  {
    id: 'budget',
    label: 'Edit budget hours',
    tooltip:
      'Permission to edit project budget hours in project settings. Detailers often have this by default.',
    kind: 'permission',
  },
  {
    id: 'org',
    label: 'Manage org & permissions',
    tooltip:
      'Permission to edit the roster, org chart, Access Control, and dashboard role assignments. Also unlocks seeing all projects on the PM Dashboard.',
    kind: 'permission',
  },
  {
    id: 'columns',
    label: 'Manage & restore columns',
    tooltip:
      'Permission to delete spreadsheet columns and restore them from the Activity Log.',
    kind: 'permission',
  },
  {
    id: 'edit-pm-assigns',
    label: 'Edit PM assigns',
    tooltip: 'Assign project managers from the PM Dashboard.',
    kind: 'permission',
  },
  {
    id: 'assign-fab-leads',
    label: 'Assign fab leads',
    tooltip: 'Assign Dept Leads on packages in the Shop Queued view.',
    kind: 'permission',
  },
  {
    id: 'assign-fab-workers',
    label: 'Assign fab workers',
    tooltip: 'Assign package owners and assembly workers in Shop Fabrication.',
    kind: 'permission',
  },
  {
    id: 'edit-fab-status',
    label: 'Edit fab status',
    tooltip: 'Change package/assembly status and clear SSv3 exports on Shop packages.',
    kind: 'permission',
  },
  {
    id: 'fab-clock',
    label: 'Fab clock',
    tooltip: 'Clock in and out on Shop packages.',
    kind: 'permission',
  },
  {
    id: 'edit-weld-log',
    label: 'Edit weld log',
    tooltip:
      'Tap-fill and save all weld log rows (Shop and Field). Field users with Weld Log view can still tap Field Welds without this.',
    kind: 'permission',
  },
  {
    id: 'edit-fab-collab',
    label: 'Fab package notes',
    tooltip: 'Add or remove package photos and comments on Shop packages.',
    kind: 'permission',
  },
  {
    id: 'log-time',
    label: 'Log time',
    tooltip: 'Create and edit time entries, and clock out open entries.',
    kind: 'permission',
  },
  {
    id: 'delete-time',
    label: 'Delete time',
    tooltip: 'Delete time entries on Time Tracking.',
    kind: 'permission',
  },
  {
    id: 'edit-clients-projects',
    label: 'Edit clients & projects',
    tooltip: 'Add or edit clients, projects, and project settings (not budget hours).',
    kind: 'permission',
  },
  {
    id: 'edit-tasks',
    label: 'Edit tasks',
    tooltip: 'Create, edit, move, duplicate, and delete tasks and groups on boards.',
    kind: 'permission',
  },
  {
    id: 'assign-tasks',
    label: 'Assign tasks',
    tooltip: 'Change task assignees on the spreadsheet and Task Board.',
    kind: 'permission',
  },
  {
    id: 'manage-statuses',
    label: 'Manage statuses',
    tooltip: 'Add, edit, reorder, and remove board statuses.',
    kind: 'permission',
  },
  {
    id: 'add-columns',
    label: 'Add columns',
    tooltip: 'Add, rename, and reorder spreadsheet columns. Deleting columns still needs Manage & restore columns.',
    kind: 'permission',
  },
  {
    id: 'owner',
    label: 'Owner Dashboard',
    tooltip:
      'Whether this role can see the Owner Dashboard tab. Does not grant edit rights inside the dashboard.',
    kind: 'dashboard',
  },
  {
    id: 'pm',
    label: 'PM Dashboard',
    tooltip:
      'Whether this role can see the PM Dashboard tab. Does not grant edit rights inside the dashboard.',
    kind: 'dashboard',
  },
  {
    id: 'fab',
    label: 'Shop Dashboard',
    tooltip:
      'Whether this role can see the Shop Dashboard tab. Does not grant edit rights inside the dashboard.',
    kind: 'dashboard',
  },
  {
    id: 'shipping',
    label: 'Shipping Dashboard',
    tooltip:
      'Whether this role can see the Shipping Dashboard tab. Does not grant edit rights inside the dashboard.',
    kind: 'dashboard',
  },
  {
    id: 'field',
    label: 'Field Dashboard',
    tooltip:
      'Whether this role can see the Field Dashboard tab. Does not grant edit rights inside the dashboard.',
    kind: 'dashboard',
  },
  {
    id: 'weld-log',
    label: 'Weld Log Dashboard',
    tooltip:
      'Whether this role can see the Weld Log Dashboard (clients → jobs → assemblies). Field users can tap-fill Field Welds; Shop edit-weld-log fills any row.',
    kind: 'dashboard',
  },
  {
    id: 'time-tracking',
    label: 'Time Tracking',
    tooltip:
      'Whether this role can see the Time Tracking tab. Whose hours they can open still follows org-chart reporting.',
    kind: 'dashboard',
  },
  {
    id: 'org-chart',
    label: 'Organizational Chart',
    tooltip:
      'Whether this role can see the Organizational Chart tab. Editing the chart still requires Manage org & permissions.',
    kind: 'dashboard',
  },
  {
    id: 'activity',
    label: 'Activity Log',
    tooltip:
      'Whether this role can see the Activity Log tab. Restoring columns still requires Manage & restore columns.',
    kind: 'dashboard',
  },
  {
    id: 'visibility',
    label: 'Access Control',
    tooltip:
      'Whether this role can see the Access Control tab. Editing this page still requires Manage org & permissions.',
    kind: 'dashboard',
  },
];

/** Main-nav / dashboard visibility columns only. */
export const DASHBOARD_VISIBILITY_COLUMNS = VISIBILITY_NAV_COLUMNS.filter(
  (column) => column.kind === 'dashboard'
);

/** Capability permission columns (Budget, Org, Columns). */
export const PERMISSION_COLUMNS = VISIBILITY_NAV_COLUMNS.filter(
  (column) => column.kind === 'permission'
);

export const NAV_COLUMN_PERMISSION: Record<VisibilityNavColumn, AppPermission> = {
  budget: 'edit-budget-hours',
  org: 'manage-org',
  columns: 'manage-columns',
  'edit-pm-assigns': 'edit-pm-assigns',
  'assign-fab-leads': 'assign-fab-leads',
  'assign-fab-workers': 'assign-fab-workers',
  'edit-fab-status': 'edit-fab-status',
  'fab-clock': 'fab-clock',
  'edit-weld-log': 'edit-weld-log',
  'edit-fab-collab': 'edit-fab-collab',
  'log-time': 'log-time',
  'delete-time': 'delete-time',
  'edit-clients-projects': 'edit-clients-projects',
  'edit-tasks': 'edit-tasks',
  'assign-tasks': 'assign-tasks',
  'manage-statuses': 'manage-statuses',
  'add-columns': 'add-columns',
  owner: 'view-owner-dashboard',
  pm: 'view-pm-dashboard',
  fab: 'view-fab-dashboard',
  shipping: 'view-shipping-dashboard',
  field: 'view-field-dashboard',
  'weld-log': 'view-weld-log-dashboard',
  'time-tracking': 'view-time-tracking',
  'org-chart': 'view-org-chart',
  activity: 'view-activity-log',
  visibility: 'view-visibility-dashboard',
};

/** Always-on main nav tabs (no AppPermission gate). */
export const ALWAYS_VISIBLE_NAV_TABS = ['Clients', 'Task Board', 'Employees'] as const;

export type AccessControlDepartmentId =
  | 'office'
  | 'pm'
  | 'field'
  | 'fab'
  | 'shipping'
  | 'unassigned';

export interface AccessControlDepartment {
  id: AccessControlDepartmentId;
  label: string;
  description: string;
}

export const ACCESS_CONTROL_DEPARTMENTS: AccessControlDepartment[] = [
  {
    id: 'office',
    label: 'Office',
    description: 'Owner, BIM leadership, support, and detailers.',
  },
  {
    id: 'pm',
    label: 'Project Management',
    description: 'Project Managers and Assistant PMs.',
  },
  {
    id: 'field',
    label: 'Field',
    description: 'Site Superintendents, Foremen, and Crew Leads.',
  },
  {
    id: 'fab',
    label: 'Fab Shop',
    description: 'Shop Super, Warehouse Lead, Dept Managers, and Workers.',
  },
  {
    id: 'shipping',
    label: 'Shipping',
    description: 'Shipping Manager and shipping workers.',
  },
  {
    id: 'unassigned',
    label: 'Unassigned operations',
    description: 'Operations people not yet placed on a department roster.',
  },
];

export interface VisibilityMatrixRow {
  id: string;
  label: string;
  cells: Record<VisibilityNavColumn, boolean>;
  /** Department group for Access Control job-level layout. */
  departmentId?: AccessControlDepartmentId;
}

export type JobLevelNavVisibilityMap = Record<string, Record<VisibilityNavColumn, boolean>>;

const OFF: Record<VisibilityNavColumn, boolean> = {
  budget: false,
  org: false,
  columns: false,
  'edit-pm-assigns': false,
  'assign-fab-leads': false,
  'assign-fab-workers': false,
  'edit-fab-status': false,
  'fab-clock': false,
  'edit-weld-log': false,
  'edit-fab-collab': false,
  'log-time': false,
  'delete-time': false,
  'edit-clients-projects': false,
  'edit-tasks': false,
  'assign-tasks': false,
  'manage-statuses': false,
  'add-columns': false,
  owner: false,
  pm: false,
  fab: false,
  shipping: false,
  field: false,
  'weld-log': false,
  'time-tracking': false,
  'org-chart': false,
  activity: false,
  visibility: false,
};

const ALL_EDIT_PERMS_ON: Partial<Record<VisibilityNavColumn, boolean>> = {
  'edit-pm-assigns': true,
  'assign-fab-leads': true,
  'assign-fab-workers': true,
  'edit-fab-status': true,
  'fab-clock': true,
  'edit-weld-log': true,
  'edit-fab-collab': true,
  'log-time': true,
  'delete-time': true,
  'edit-clients-projects': true,
  'edit-tasks': true,
  'assign-tasks': true,
  'manage-statuses': true,
  'add-columns': true,
};

function cells(overrides: Partial<Record<VisibilityNavColumn, boolean>>): Record<VisibilityNavColumn, boolean> {
  // Time Tracking is on for every job level by default (was previously always-visible).
  return { ...OFF, 'time-tracking': true, ...overrides };
}

/** Default Access Control matrix by job level (grouped by department in the UI). */
export const DEFAULT_JOB_LEVEL_VISIBILITY_ROWS: VisibilityMatrixRow[] = [
  {
    id: 'owner',
    label: 'Owner',
    departmentId: 'office',
    cells: cells({
      budget: true,
      org: true,
      columns: true,
      ...ALL_EDIT_PERMS_ON,
      owner: true,
      pm: true,
      fab: true,
      shipping: true,
      field: true,
      'weld-log': true,
      'org-chart': true,
      activity: true,
      visibility: true,
    }),
  },
  {
    id: 'bim-manager',
    label: 'BIM Manager',
    departmentId: 'office',
    cells: cells({
      budget: true,
      org: true,
      columns: true,
      ...ALL_EDIT_PERMS_ON,
      pm: true,
      fab: true,
      shipping: true,
      field: true,
      'weld-log': true,
      'org-chart': true,
      activity: true,
      visibility: true,
    }),
  },
  {
    id: 'operations-manager',
    label: 'Operations Manager',
    departmentId: 'office',
    cells: cells({
      org: true,
      columns: true,
      ...ALL_EDIT_PERMS_ON,
      pm: true,
      fab: true,
      shipping: true,
      field: true,
      'weld-log': true,
      'org-chart': true,
      activity: true,
      visibility: true,
    }),
  },
  {
    id: 'support-manager',
    label: 'Support Manager',
    departmentId: 'office',
    cells: cells({
      'log-time': true,
      'edit-clients-projects': true,
      'edit-tasks': true,
      'assign-tasks': true,
      pm: true,
      fab: true,
      shipping: true,
      field: true,
      'org-chart': true,
    }),
  },
  {
    id: 'detailer',
    label: 'Detailer (any)',
    departmentId: 'office',
    cells: cells({
      budget: true,
      'log-time': true,
      'edit-tasks': true,
      'assign-tasks': true,
      'org-chart': true,
    }),
  },
  {
    id: 'support-specialist',
    label: 'Support Specialist',
    departmentId: 'office',
    cells: cells({
      'log-time': true,
      'edit-clients-projects': true,
      'edit-tasks': true,
      'assign-tasks': true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-pm-lead',
    label: 'Lead',
    departmentId: 'pm',
    cells: cells({
      'edit-pm-assigns': true,
      'log-time': true,
      'edit-tasks': true,
      'assign-tasks': true,
      pm: true,
      fab: true,
      shipping: true,
      field: true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-pm-staff',
    label: 'Staff',
    departmentId: 'pm',
    cells: cells({
      'log-time': true,
      'edit-tasks': true,
      'assign-tasks': true,
      pm: true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-field-lead',
    label: 'Lead',
    departmentId: 'field',
    cells: cells({
      'log-time': true,
      'edit-tasks': true,
      'assign-tasks': true,
      field: true,
      'weld-log': true,
      pm: true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-field-staff',
    label: 'Staff',
    departmentId: 'field',
    cells: cells({
      'log-time': true,
      field: true,
      'weld-log': true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-fab-lead',
    label: 'Lead',
    departmentId: 'fab',
    cells: cells({
      'assign-fab-leads': true,
      'assign-fab-workers': true,
      'edit-fab-status': true,
      'fab-clock': true,
      'edit-weld-log': true,
      'edit-fab-collab': true,
      'log-time': true,
      fab: true,
      'weld-log': true,
      shipping: true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-fab-staff',
    label: 'Staff',
    departmentId: 'fab',
    cells: cells({
      'assign-fab-workers': true,
      'edit-fab-status': true,
      'fab-clock': true,
      'edit-weld-log': true,
      'edit-fab-collab': true,
      'log-time': true,
      fab: true,
      'weld-log': true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-shipping-lead',
    label: 'Lead',
    departmentId: 'shipping',
    cells: cells({
      'log-time': true,
      'edit-tasks': true,
      'assign-tasks': true,
      shipping: true,
      fab: true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-shipping-staff',
    label: 'Staff',
    departmentId: 'shipping',
    cells: cells({
      'log-time': true,
      shipping: true,
      'org-chart': true,
    }),
  },
  {
    id: 'ops-unassigned',
    label: 'Unassigned',
    departmentId: 'unassigned',
    cells: cells({
      'log-time': true,
      'org-chart': true,
    }),
  },
];

function cloneDefaultRows(): JobLevelNavVisibilityMap {
  return Object.fromEntries(
    DEFAULT_JOB_LEVEL_VISIBILITY_ROWS.map((row) => [row.id, { ...row.cells }])
  );
}

export const DEFAULT_JOB_LEVEL_NAV_VISIBILITY: JobLevelNavVisibilityMap = cloneDefaultRows();

export function normalizeJobLevelNavVisibility(
  value: JobLevelNavVisibilityMap | null | undefined
): JobLevelNavVisibilityMap {
  const base = cloneDefaultRows();
  if (!value) return base;
  for (const row of DEFAULT_JOB_LEVEL_VISIBILITY_ROWS) {
    const incoming = value[row.id];
    if (!incoming) continue;
    base[row.id] = { ...row.cells, ...incoming };
  }
  return base;
}

export function buildJobLevelVisibilityRows(
  defaults: JobLevelNavVisibilityMap
): VisibilityMatrixRow[] {
  return DEFAULT_JOB_LEVEL_VISIBILITY_ROWS.map((row) => ({
    id: row.id,
    label: row.label,
    departmentId: row.departmentId,
    cells: { ...row.cells, ...(defaults[row.id] ?? {}) },
  }));
}

export function groupJobLevelVisibilityByDepartment(
  rows: VisibilityMatrixRow[]
): { department: AccessControlDepartment; rows: VisibilityMatrixRow[] }[] {
  return ACCESS_CONTROL_DEPARTMENTS.map((department) => ({
    department,
    rows: rows.filter((row) => row.departmentId === department.id),
  })).filter((group) => group.rows.length > 0);
}

const DETAILER_CATEGORIES: OrgCategory[] = [
  'plumbing-detailer',
  'mechanical-detailer',
  'sheet-metal-detailer',
  'jr-detailer',
];

const PM_SET = new Set<string>(PM_STAFF_IDS);
const FIELD_SET = new Set<string>(FIELD_STAFF_IDS);
const FAB_SET = new Set<string>(FAB_STAFF_IDS);
const SHIP_SET = new Set<string>(SHIPPING_STAFF_IDS);

/** Dashboard roles treated as leads (not workers / assistant staff). */
export const OPS_LEAD_ROLES = {
  pm: ['project-manager'] as const,
  field: ['site-superintendent'] as const,
  fab: [
    'shop-super',
    'warehouse-lead',
    'dept-manager-mech',
    'dept-manager-plmb',
    'dept-manager-hvac',
  ] as const,
  shipping: ['shipping-manager'] as const,
};

function roleMemberIdSet(
  assignments: DashboardAssignments,
  dashboard: keyof typeof OPS_LEAD_ROLES,
  roleIds: readonly string[]
): Set<string> {
  const ids = new Set<string>();
  const board = assignments[dashboard] as Record<string, string[] | undefined>;
  for (const roleId of roleIds) {
    for (const id of board[roleId] ?? []) ids.add(id);
  }
  return ids;
}

function opsLeadIds(assignments: DashboardAssignments) {
  return {
    pm: roleMemberIdSet(assignments, 'pm', OPS_LEAD_ROLES.pm),
    field: roleMemberIdSet(assignments, 'field', OPS_LEAD_ROLES.field),
    fab: roleMemberIdSet(assignments, 'fab', OPS_LEAD_ROLES.fab),
    shipping: roleMemberIdSet(assignments, 'shipping', OPS_LEAD_ROLES.shipping),
  };
}

/** Employees matching a defaults-matrix job-level row. */
export function employeesForJobLevelRow(
  rowId: string,
  employees: Employee[],
  assignments: DashboardAssignments | null | undefined = null
): Employee[] {
  const resolved = assignments ?? createDefaultDashboardAssignments();
  const leads = opsLeadIds(resolved);

  switch (rowId) {
    case 'owner':
      return employees.filter((employee) => inferOrgCategory(employee) === 'owner');
    case 'bim-manager':
      return employees.filter((employee) => inferOrgCategory(employee) === 'bim-manager');
    case 'operations-manager':
      return employees.filter((employee) => inferOrgCategory(employee) === 'operations-manager');
    case 'support-manager':
      return employees.filter((employee) => inferOrgCategory(employee) === 'support-manager');
    case 'detailer':
      return employees.filter((employee) =>
        DETAILER_CATEGORIES.includes(inferOrgCategory(employee))
      );
    case 'support-specialist':
      return employees.filter((employee) => inferOrgCategory(employee) === 'support-specialist');
    case 'ops-pm-lead':
      return employees.filter((employee) => leads.pm.has(employee.id));
    case 'ops-pm-staff':
      return employees.filter(
        (employee) => PM_SET.has(employee.id) && !leads.pm.has(employee.id)
      );
    case 'ops-field-lead':
      return employees.filter((employee) => leads.field.has(employee.id));
    case 'ops-field-staff':
      return employees.filter(
        (employee) => FIELD_SET.has(employee.id) && !leads.field.has(employee.id)
      );
    case 'ops-fab-lead':
      return employees.filter((employee) => leads.fab.has(employee.id));
    case 'ops-fab-staff':
      return employees.filter(
        (employee) => FAB_SET.has(employee.id) && !leads.fab.has(employee.id)
      );
    case 'ops-shipping-lead':
      return employees.filter((employee) => leads.shipping.has(employee.id));
    case 'ops-shipping-staff':
      return employees.filter(
        (employee) => SHIP_SET.has(employee.id) && !leads.shipping.has(employee.id)
      );
    case 'ops-unassigned':
      return employees.filter(
        (employee) =>
          inferOrgCategory(employee) === 'operations-staff' &&
          !PM_SET.has(employee.id) &&
          !FIELD_SET.has(employee.id) &&
          !FAB_SET.has(employee.id) &&
          !SHIP_SET.has(employee.id)
      );
    default:
      return [];
  }
}

const DASHBOARD_COLUMNS: VisibilityNavColumn[] = [
  'owner',
  'pm',
  'fab',
  'shipping',
  'field',
  'weld-log',
  'time-tracking',
  'org-chart',
  'activity',
  'visibility',
];

/**
 * Align ops employees' dashboard view permissions to Access Control lead/staff defaults.
 * Leaves Budget / Org / Columns chips unchanged.
 */
export function syncOpsDashboardPermissionsFromDefaults(
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap,
  assignments: DashboardAssignments | null | undefined,
  defaults: JobLevelNavVisibilityMap = DEFAULT_JOB_LEVEL_NAV_VISIBILITY
): EmployeePermissionsMap {
  const next: EmployeePermissionsMap = { ...employeePermissions };
  const opsRows = [
    'ops-pm-lead',
    'ops-pm-staff',
    'ops-field-lead',
    'ops-field-staff',
    'ops-fab-lead',
    'ops-fab-staff',
    'ops-shipping-lead',
    'ops-shipping-staff',
    'ops-unassigned',
  ] as const;

  for (const rowId of opsRows) {
    const cells = defaults[rowId] ?? DEFAULT_JOB_LEVEL_NAV_VISIBILITY[rowId]!;
    for (const employee of employeesForJobLevelRow(rowId, employees, assignments)) {
      const granted = new Set(next[employee.id] ?? []);
      for (const column of DASHBOARD_COLUMNS) {
        const permission = NAV_COLUMN_PERMISSION[column];
        if (cells[column]) granted.add(permission);
        else granted.delete(permission);
      }
      next[employee.id] = [...granted];
    }
  }

  return next;
}

/**
 * Why a cell may stay on after removing the permission chip (runtime / job-level grants).
 * Empty string = checkbox can clear effective access by toggling the chip.
 */
export function navColumnLockReason(
  column: VisibilityNavColumn,
  employee: Employee,
  visibilityDashboardJobLevels: OrgCategory[]
): string {
  const category = inferOrgCategory(employee);
  const editPermissionColumns = new Set<VisibilityNavColumn>([
    'edit-pm-assigns',
    'assign-fab-leads',
    'assign-fab-workers',
    'edit-fab-status',
    'fab-clock',
    'edit-weld-log',
    'edit-fab-collab',
    'log-time',
    'delete-time',
    'edit-clients-projects',
    'edit-tasks',
    'assign-tasks',
    'manage-statuses',
    'add-columns',
  ]);
  if (isOrgOwner(employee)) {
    if (column === 'budget' || column === 'org' || editPermissionColumns.has(column)) {
      return 'Owners keep this via runtime grant';
    }
    if (
      column === 'owner' ||
      column === 'pm' ||
      column === 'fab' ||
      column === 'shipping' ||
      column === 'field' ||
      column === 'time-tracking'
    ) {
      return 'Owners keep this dashboard via runtime grant';
    }
  }
  if (category === 'bim-manager' && editPermissionColumns.has(column)) {
    return 'BIM Manager keeps this via runtime grant';
  }
  if (
    column === 'org' &&
    (category === 'bim-manager' || category === 'operations-manager')
  ) {
    return 'BIM Manager / Ops Manager keep Manage org via runtime grant';
  }
  if (column === 'budget' && employee.role === 'detailer') {
    return 'Detailers keep Budget via role grant';
  }
  if (
    column === 'columns' &&
    (category === 'owner' || category === 'bim-manager' || category === 'operations-manager')
  ) {
    return 'Owner / BIM Manager / Ops Manager keep Columns via runtime grant';
  }
  if (
    column === 'activity' &&
    (category === 'owner' || category === 'bim-manager' || category === 'operations-manager')
  ) {
    return 'Owner / BIM Manager / Ops Manager keep Activity via runtime grant';
  }
  if (column === 'visibility' && visibilityDashboardJobLevels.includes(category)) {
    return 'On via Access Control job-level access — uncheck that job level below to remove for everyone in it';
  }
  if (column === 'org-chart' && employee.role === 'detailer') {
    return 'Detailers keep Org Chart via budget-hours grant';
  }
  return '';
}

export function employeeNavVisibility(
  employeeId: string,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap,
  visibilityDashboardJobLevels: OrgCategory[]
): Record<VisibilityNavColumn, boolean> {
  return {
    budget: canEditBudgetHours(employeeId, employees, employeePermissions),
    org: canManageOrg(employeeId, employees, employeePermissions),
    columns: canManageColumns(employeeId, employees, employeePermissions),
    'edit-pm-assigns': canEditPmAssigns(employeeId, employees, employeePermissions),
    'assign-fab-leads': canAssignFabLeadsPermission(employeeId, employees, employeePermissions),
    'assign-fab-workers': canAssignFabWorkersPermission(
      employeeId,
      employees,
      employeePermissions
    ),
    'edit-fab-status': canEditFabStatus(employeeId, employees, employeePermissions),
    'fab-clock': canFabClock(employeeId, employees, employeePermissions),
    'edit-weld-log': canEditWeldLog(employeeId, employees, employeePermissions),
    'edit-fab-collab': canEditFabCollab(employeeId, employees, employeePermissions),
    'log-time': canLogTime(employeeId, employees, employeePermissions),
    'delete-time': canDeleteTime(employeeId, employees, employeePermissions),
    'edit-clients-projects': canEditClientsProjects(employeeId, employees, employeePermissions),
    'edit-tasks': canEditTasks(employeeId, employees, employeePermissions),
    'assign-tasks': canAssignTasks(employeeId, employees, employeePermissions),
    'manage-statuses': canManageStatuses(employeeId, employees, employeePermissions),
    'add-columns': canAddColumns(employeeId, employees, employeePermissions),
    owner: canViewOwnerDashboard(employeeId, employees, employeePermissions),
    pm: canViewDashboard('pm', employeeId, employees, employeePermissions),
    fab: canViewDashboard('fab', employeeId, employees, employeePermissions),
    shipping: canViewDashboard('shipping', employeeId, employees, employeePermissions),
    field: canViewDashboard('field', employeeId, employees, employeePermissions),
    'weld-log': canViewWeldLogDashboard(employeeId, employees, employeePermissions),
    'time-tracking': canViewTimeTracking(employeeId, employees, employeePermissions),
    'org-chart': canAccessOrgChart(employeeId, employees, employeePermissions),
    activity: canViewActivityLog(employeeId, employees, employeePermissions),
    visibility: canViewVisibilityDashboard(
      employeeId,
      employees,
      employeePermissions,
      visibilityDashboardJobLevels
    ),
  };
}

export function buildLiveRosterVisibilityRows(
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap,
  visibilityDashboardJobLevels: OrgCategory[]
): VisibilityMatrixRow[] {
  return [...employees]
    .sort((a, b) => a.name.localeCompare(b.name))
    .map((employee) => ({
      id: employee.id,
      label: `${employee.name} (${orgCategoryLabel(inferOrgCategory(employee))})`,
      cells: employeeNavVisibility(
        employee.id,
        employees,
        employeePermissions,
        visibilityDashboardJobLevels
      ),
    }));
}
