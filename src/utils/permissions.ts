import type { AppPermission, Employee } from '../types';
import type { DashboardType } from '../types';
import type { EmployeePermissionsMap, EmployeeReportsToMap } from './orgChart';
import { inferOrgCategory, isOrgOwner, isUpstreamManagerOf } from './orgChart';

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

  if (permission === 'manage-org' && (userId === JOE_VASQUEZ_ID || isOrgOwner(employee))) {
    return true;
  }

  if (permission === 'view-owner-dashboard' && isOrgOwner(employee)) {
    return true;
  }

  if (
    (permission === 'view-pm-dashboard' ||
      permission === 'view-field-dashboard' ||
      permission === 'view-fab-dashboard' ||
      permission === 'view-shipping-dashboard') &&
    isOrgOwner(employee)
  ) {
    return true;
  }

  return false;
}

function isColumnAdminCategory(employee: Employee | undefined): boolean {
  if (!employee) return false;
  const category = inferOrgCategory(employee);
  return category === 'owner' || category === 'bim-manager' || category === 'operations-manager';
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
