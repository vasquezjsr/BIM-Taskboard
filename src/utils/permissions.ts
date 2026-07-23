import type { AppPermission, Employee, OrgCategory } from '../types';
import type { DashboardType } from '../types';
import type { EmployeePermissionsMap, EmployeeReportsToMap } from './orgChart';
import {
  DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS,
  inferOrgCategory,
  isOrgOwner,
  isUpstreamManagerOf,
} from './orgChart';

/** Joe Vasquez — legacy explicit grants */
export const JOE_VASQUEZ_ID = 'emp-support-1';

function getPermissions(
  userId: string | null,
  employeePermissions: EmployeePermissionsMap
): AppPermission[] {
  if (!userId) return [];
  return employeePermissions[userId] ?? [];
}

function hasPermission(
  userId: string | null,
  permission: AppPermission,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap
): boolean {
  if (getPermissions(userId, employeePermissions).includes(permission)) return true;

  const employee = userId ? employees.find((entry) => entry.id === userId) : undefined;

  if (permission === 'edit-budget-hours' && userId) {
    if (employee?.role === 'detailer' || userId === JOE_VASQUEZ_ID || isOrgOwner(employee)) {
      return true;
    }
  }

  if (permission === 'view-org-chart' && userId) {
    if (canEditBudgetHours(userId, employees, employeePermissions)) return true;
  }

  if (permission === 'manage-org' && userId) {
    if (userId === JOE_VASQUEZ_ID || isOrgOwner(employee)) return true;
    // BIM / Ops managers administer Access Control; keep manage-org even if the chip was cleared.
    const category = employee ? inferOrgCategory(employee) : null;
    if (category === 'bim-manager' || category === 'operations-manager') return true;
  }

  if (permission === 'view-owner-dashboard' && isOrgOwner(employee)) {
    return true;
  }

  if (
    permission === 'view-activity-log' &&
    (userId === JOE_VASQUEZ_ID || isOrgOwner(employee) || isColumnAdminCategory(employee))
  ) {
    return true;
  }

  if (
    (permission === 'view-pm-dashboard' ||
      permission === 'view-field-dashboard' ||
      permission === 'view-fab-dashboard' ||
      permission === 'view-shipping-dashboard' ||
      permission === 'view-weld-log-dashboard' ||
      permission === 'view-spooling-dashboard' ||
      permission === 'view-time-tracking' ||
      permission === 'view-visibility-dashboard') &&
    isOrgOwner(employee)
  ) {
    return true;
  }

  if (isDashboardEditPermission(permission) && userId) {
    if (userId === JOE_VASQUEZ_ID || isOrgOwner(employee)) return true;
    const category = employee ? inferOrgCategory(employee) : null;
    if (category === 'bim-manager') return true;
  }

  return false;
}

const DASHBOARD_EDIT_PERMISSION_SET = new Set<AppPermission>([
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

function isDashboardEditPermission(permission: AppPermission): boolean {
  return DASHBOARD_EDIT_PERMISSION_SET.has(permission);
}

function permissionHelper(permission: AppPermission) {
  return (
    userId: string | null,
    employees: Employee[],
    employeePermissions: EmployeePermissionsMap = {}
  ) => hasPermission(userId, permission, employees, employeePermissions);
}

export const canEditPmAssigns = permissionHelper('edit-pm-assigns');
export const canAssignFabLeadsPermission = permissionHelper('assign-fab-leads');
export const canAssignFabWorkersPermission = permissionHelper('assign-fab-workers');
export const canEditFabStatus = permissionHelper('edit-fab-status');
export const canFabClock = permissionHelper('fab-clock');
export const canEditWeldLog = permissionHelper('edit-weld-log');
export const canViewWeldLogDashboard = permissionHelper('view-weld-log-dashboard');
export const canViewSpoolingDashboard = permissionHelper('view-spooling-dashboard');
export const canEditFabCollab = permissionHelper('edit-fab-collab');
export const canLogTime = permissionHelper('log-time');
export const canDeleteTime = permissionHelper('delete-time');

/** Delete own entries anytime you can view them; delete others only with delete-time. */
export function canDeleteTimeEntry(
  viewerId: string | null,
  entryEmployeeId: string,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {},
  employeeReportsTo: EmployeeReportsToMap = {}
): boolean {
  if (
    !canViewEmployeeTime(viewerId, entryEmployeeId, employees, employeeReportsTo)
  ) {
    return false;
  }
  if (viewerId === entryEmployeeId) return true;
  return canDeleteTime(viewerId, employees, employeePermissions);
}
export const canEditClientsProjects = permissionHelper('edit-clients-projects');
export const canEditTasks = permissionHelper('edit-tasks');
export const canAssignTasks = permissionHelper('assign-tasks');
export const canManageStatuses = permissionHelper('manage-statuses');

function isColumnAdminCategory(employee: Employee | undefined): boolean {
  if (!employee) return false;
  const category = inferOrgCategory(employee);
  return category === 'owner' || category === 'bim-manager' || category === 'operations-manager';
}

/** Top-tier titles (Owner, BIM Manager, Operations Manager) can always add columns / materials. */
export function canAddColumns(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  if (hasPermission(userId, 'add-columns', employees, employeePermissions)) return true;
  const employee = userId ? employees.find((entry) => entry.id === userId) : undefined;
  return isColumnAdminCategory(employee);
}

export function canManageColumns(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  if (hasPermission(userId, 'manage-columns', employees, employeePermissions)) return true;
  const employee = userId ? employees.find((entry) => entry.id === userId) : undefined;
  return isColumnAdminCategory(employee);
}

/** Top-tier titles can edit Material dropdown options (and other column settings). */
export function canManageMaterialOptions(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  return (
    canManageColumns(userId, employees, employeePermissions) ||
    canAddColumns(userId, employees, employeePermissions)
  );
}

export function canViewActivityLog(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  if (hasPermission(userId, 'view-activity-log', employees, employeePermissions)) return true;
  const employee = userId ? employees.find((entry) => entry.id === userId) : undefined;
  return isColumnAdminCategory(employee);
}

export function canEditBudgetHours(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  return hasPermission(userId, 'edit-budget-hours', employees, employeePermissions);
}

export function canAccessOrgChart(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  return (
    hasPermission(userId, 'view-org-chart', employees, employeePermissions) ||
    hasPermission(userId, 'manage-org', employees, employeePermissions)
  );
}

/** @deprecated Use canAccessOrgChart */
export function canAccessPermissionsTab(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  return canAccessOrgChart(userId, employees, employeePermissions);
}

export function canManageOrg(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  return hasPermission(userId, 'manage-org', employees, employeePermissions);
}

export function canViewOwnerDashboard(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  return hasPermission(userId, 'view-owner-dashboard', employees, employeePermissions);
}

export function canViewTimeTracking(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  return hasPermission(userId, 'view-time-tracking', employees, employeePermissions);
}

export function canViewVisibilityDashboard(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {},
  visibilityDashboardJobLevels: OrgCategory[] = DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS
): boolean {
  if (hasPermission(userId, 'view-visibility-dashboard', employees, employeePermissions)) {
    return true;
  }
  if (!userId) return false;
  const employee = employees.find((entry) => entry.id === userId);
  if (!employee) return false;
  return visibilityDashboardJobLevels.includes(inferOrgCategory(employee));
}

const DASHBOARD_PERMISSION: Record<DashboardType, AppPermission> = {
  pm: 'view-pm-dashboard',
  field: 'view-field-dashboard',
  fab: 'view-fab-dashboard',
  shipping: 'view-shipping-dashboard',
};

export function canViewDashboard(
  dashboard: DashboardType,
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  return hasPermission(userId, DASHBOARD_PERMISSION[dashboard], employees, employeePermissions);
}

export function visibleDashboards(
  userId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): DashboardType[] {
  return (['pm', 'fab', 'shipping', 'field'] as DashboardType[]).filter((dashboard) =>
    canViewDashboard(dashboard, userId, employees, employeePermissions)
  );
}

export function getBudgetHoursEditors(
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): Employee[] {
  return employees.filter((employee) =>
    canEditBudgetHours(employee.id, employees, employeePermissions)
  );
}

/** Owners, an employee's managers, and the employee themselves can view logged hours. */
export function canViewEmployeeTime(
  viewerId: string | null,
  targetEmployeeId: string,
  employees: Employee[],
  employeeReportsTo: EmployeeReportsToMap
): boolean {
  if (!viewerId) return false;
  if (viewerId === targetEmployeeId) return true;

  const viewer = employees.find((employee) => employee.id === viewerId);
  if (isOrgOwner(viewer)) return true;

  const memberIds = employees.map((employee) => employee.id);
  return isUpstreamManagerOf(viewerId, targetEmployeeId, memberIds, employeeReportsTo);
}

export function getVisibleTimeEmployeeIds(
  viewerId: string | null,
  employees: Employee[],
  employeeReportsTo: EmployeeReportsToMap
): string[] {
  if (!viewerId) return [];

  const viewer = employees.find((employee) => employee.id === viewerId);
  if (isOrgOwner(viewer)) return employees.map((employee) => employee.id);

  const memberIds = employees.map((employee) => employee.id);
  return memberIds.filter(
    (employeeId) =>
      employeeId === viewerId ||
      isUpstreamManagerOf(viewerId, employeeId, memberIds, employeeReportsTo)
  );
}
