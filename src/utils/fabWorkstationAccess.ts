import { FAB_DASHBOARD_ROLES } from '../data/dashboards';
import type {
  DashboardAssignments,
  Employee,
  FabDashboardRole,
  Task,
} from '../types';
import { isOwnerEmployee } from '../data/employees';
import { SSV3_FIELD } from './boardroomPackageImport';
import type { EmployeeReportsToMap } from './orgChart';
import { isOrgOwner, isUpstreamManagerOf, getEmployeeManagers } from './orgChart';
import {
  FAB_WAREHOUSE_ACTIVE_STATUSES,
  FAB_WAREHOUSE_STATUS_OPTIONS,
  FAB_QUEUE_ACTIVE_STATUSES,
  isFabInFabStatus,
  isFabShippedStatus,
} from './taskStatuses';

export const FAB_DEPT_MANAGER_ROLES: FabDashboardRole[] = [
  'dept-manager-mech',
  'dept-manager-plmb',
  'dept-manager-hvac',
];

export type FabWorkstationMode = 'queue' | 'warehouse' | 'personal';

export interface FabPersonOption {
  id: string;
  name: string;
  roleId: FabDashboardRole;
  roleLabel: string;
}

function roleLabel(roleId: FabDashboardRole): string {
  return FAB_DASHBOARD_ROLES.find((role) => role.id === roleId)?.label ?? roleId;
}

export function getFabRolesForEmployee(
  employeeId: string | null | undefined,
  assignments: DashboardAssignments | null | undefined
): FabDashboardRole[] {
  if (!employeeId || !assignments?.fab) return [];
  const roles: FabDashboardRole[] = [];
  for (const role of FAB_DASHBOARD_ROLES) {
    const ids = assignments.fab[role.id] ?? [];
    if (ids.includes(employeeId)) roles.push(role.id);
  }
  return roles;
}

export function getPrimaryFabRole(
  employeeId: string | null | undefined,
  assignments: DashboardAssignments | null | undefined,
  employees: Employee[]
): FabDashboardRole | 'owner-queue' | null {
  if (!employeeId) return null;
  const employee = employees.find((entry) => entry.id === employeeId);
  if (isOwnerEmployee(employee)) return 'owner-queue';

  const roles = getFabRolesForEmployee(employeeId, assignments);
  if (roles.includes('shop-super')) return 'shop-super';
  if (roles.includes('warehouse-lead')) return 'warehouse-lead';
  if (roles.includes('warehouse-worker')) return 'warehouse-worker';
  for (const role of FAB_DEPT_MANAGER_ROLES) {
    if (roles.includes(role)) return role;
  }
  if (roles.includes('worker')) return 'worker';
  return 'owner-queue';
}

export function isWarehouseFabRole(
  role: FabDashboardRole | 'owner-queue' | null | undefined
): boolean {
  return role === 'warehouse-lead' || role === 'warehouse-worker';
}

/**
 * Warehouse Dashboard access: Warehouse Lead, Shop Super, owners,
 * and anyone upstream of the Warehouse Lead or Shop Super in the org chart.
 */
export function canAccessWarehouseDashboard(
  employeeId: string | null | undefined,
  assignments: DashboardAssignments | null | undefined,
  employees: Employee[],
  employeeReportsTo: EmployeeReportsToMap = {}
): boolean {
  if (!employeeId) return false;

  const employee = employees.find((entry) => entry.id === employeeId);
  if (isOwnerEmployee(employee) || isOrgOwner(employee)) return true;

  const roles = getFabRolesForEmployee(employeeId, assignments);
  if (roles.includes('shop-super') || roles.includes('warehouse-lead')) return true;

  const memberIds = employees.map((entry) => entry.id);
  const shopSuperIds = assignments?.fab?.['shop-super'] ?? [];
  const warehouseLeadIds = assignments?.fab?.['warehouse-lead'] ?? [];
  for (const targetId of [...shopSuperIds, ...warehouseLeadIds]) {
    if (isUpstreamManagerOf(employeeId, targetId, memberIds, employeeReportsTo)) {
      return true;
    }
  }

  return false;
}

/** Dashboards strip: Shop Super / owner / Warehouse Lead / org-chart managers above them. */
export function canBrowseFabShopDashboards(
  employeeId: string | null | undefined,
  assignments: DashboardAssignments | null | undefined,
  employees: Employee[],
  employeeReportsTo: EmployeeReportsToMap = {}
): boolean {
  if (!employeeId) return false;

  const employee = employees.find((entry) => entry.id === employeeId);
  if (isOwnerEmployee(employee) || isOrgOwner(employee)) return true;

  const role = getPrimaryFabRole(employeeId, assignments, employees);
  if (role === 'shop-super' || role === 'owner-queue' || role === 'warehouse-lead') {
    return true;
  }

  return canAccessWarehouseDashboard(employeeId, assignments, employees, employeeReportsTo);
}

export function resolveFabWorkstationMode(
  employeeId: string | null | undefined,
  assignments: DashboardAssignments | null | undefined,
  employees: Employee[]
): FabWorkstationMode {
  const primary = getPrimaryFabRole(employeeId, assignments, employees);
  if (isWarehouseFabRole(primary)) return 'warehouse';
  if (primary === 'shop-super' || primary === 'owner-queue' || primary === null) {
    return 'queue';
  }
  return 'personal';
}

export function isWarehouseActiveStatus(status: string): boolean {
  return (FAB_WAREHOUSE_ACTIVE_STATUSES as readonly string[]).includes(status);
}

export function isWarehouseStatusOption(status: string): boolean {
  return (FAB_WAREHOUSE_STATUS_OPTIONS as readonly string[]).includes(status);
}

/** True while package still belongs on the Queued Dashboard (not yet In Fab). */
export function isQueueActiveStatus(status: string): boolean {
  return (FAB_QUEUE_ACTIVE_STATUSES as readonly string[]).includes(status);
}

export function findBomFileName(fileNames: string[]): string | null {
  const match = fileNames.find(
    (name) => /bill\s*of\s*materials/i.test(name) || /\bbom\b/i.test(name)
  );
  return match ?? null;
}

export function listFabDeptLeads(
  employees: Employee[],
  assignments: DashboardAssignments | null | undefined
): FabPersonOption[] {
  const options: FabPersonOption[] = [];
  const seen = new Set<string>();
  for (const roleId of FAB_DEPT_MANAGER_ROLES) {
    const ids = assignments?.fab?.[roleId] ?? [];
    for (const id of ids) {
      if (seen.has(id)) continue;
      const employee = employees.find((entry) => entry.id === id);
      if (!employee) continue;
      seen.add(id);
      options.push({
        id: employee.id,
        name: employee.name,
        roleId,
        roleLabel: roleLabel(roleId),
      });
    }
  }
  return options.sort((a, b) => a.name.localeCompare(b.name));
}

export function listFabWorkers(
  employees: Employee[],
  assignments: DashboardAssignments | null | undefined
): FabPersonOption[] {
  const ids = assignments?.fab?.worker ?? [];
  const options: FabPersonOption[] = [];
  for (const id of ids) {
    const employee = employees.find((entry) => entry.id === id);
    if (!employee) continue;
    options.push({
      id: employee.id,
      name: employee.name,
      roleId: 'worker',
      roleLabel: roleLabel('worker'),
    });
  }
  return options.sort((a, b) => a.name.localeCompare(b.name));
}

/** True when this person is a fab floor worker (not Shop Super / warehouse / dept lead). */
export function isFabShopFloorWorker(
  employeeId: string | null | undefined,
  assignments: DashboardAssignments | null | undefined,
  employees: Employee[]
): boolean {
  if (!employeeId) return false;
  return getPrimaryFabRole(employeeId, assignments, employees) === 'worker';
}

/** Owner, Shop Super, or Shop Dept Manager — can browse other workstations. */
export function canBrowseFabWorkstations(
  employeeId: string | null | undefined,
  assignments: DashboardAssignments | null | undefined,
  employees: Employee[]
): boolean {
  if (!employeeId) return false;
  const role = getPrimaryFabRole(employeeId, assignments, employees);
  if (role === 'owner-queue' || role === 'shop-super') return true;
  if (role && FAB_DEPT_MANAGER_ROLES.includes(role as FabDashboardRole)) return true;
  const employee = employees.find((entry) => entry.id === employeeId);
  return isOwnerEmployee(employee) || isOrgOwner(employee);
}

export type FabWorkstationGroup = {
  manager: FabPersonOption;
  workers: FabPersonOption[];
};

/**
 * Fabrication workstations: dept managers with fab workers who report to them.
 * Warehouse roles are excluded (they belong on Warehouse Dashboard).
 */
export function listFabFabricationWorkstationGroups(
  employees: Employee[],
  assignments: DashboardAssignments | null | undefined,
  reportsTo: EmployeeReportsToMap = {}
): { groups: FabWorkstationGroup[]; unassignedWorkers: FabPersonOption[] } {
  const managers = listFabDeptLeads(employees, assignments);
  const workers = listFabWorkers(employees, assignments);
  const memberIds = employees.map((employee) => employee.id);
  const assigned = new Set<string>();

  const groups: FabWorkstationGroup[] = managers.map((manager) => {
    const reports = workers.filter((worker) => {
      const managerIds = getEmployeeManagers(worker.id, memberIds, reportsTo);
      return managerIds.includes(manager.id);
    });
    for (const worker of reports) assigned.add(worker.id);
    return { manager, workers: reports };
  });

  const unassignedWorkers = workers.filter((worker) => !assigned.has(worker.id));
  return { groups, unassignedWorkers };
}

/** All fab roster people for Acting-as switcher. */
export function listFabActingAsOptions(
  employees: Employee[],
  assignments: DashboardAssignments | null | undefined
): FabPersonOption[] {
  const options: FabPersonOption[] = [];
  const seen = new Set<string>();
  for (const role of FAB_DASHBOARD_ROLES) {
    const ids = assignments?.fab?.[role.id] ?? [];
    for (const id of ids) {
      if (seen.has(id)) continue;
      const employee = employees.find((entry) => entry.id === id);
      if (!employee) continue;
      seen.add(id);
      options.push({
        id: employee.id,
        name: employee.name,
        roleId: role.id,
        roleLabel: role.label,
      });
    }
  }
  return options;
}

export function getPackageDeptLeadId(pkg: Task): string | null {
  const value = pkg.customFields?.[SSV3_FIELD.deptLeadId];
  return value && value.trim() ? value.trim() : null;
}

export function getPackageWorkerId(pkg: Task): string | null {
  const value = pkg.customFields?.[SSV3_FIELD.workerId];
  return value && value.trim() ? value.trim() : null;
}

export function packageVisibleToUser(
  pkg: Task,
  assemblies: Task[],
  userId: string,
  mode: FabWorkstationMode,
  assignments: DashboardAssignments | null | undefined = null,
  employees: Employee[] = []
): boolean {
  if (mode === 'queue') return isQueueActiveStatus(pkg.status);
  if (mode === 'warehouse') return isWarehouseActiveStatus(pkg.status);
  if (getPackageDeptLeadId(pkg) === userId) return true;
  if (getPackageWorkerId(pkg) === userId) return true;
  if (assemblies.some((assembly) => (assembly.assigneeIds ?? []).includes(userId))) {
    return true;
  }

  // Active fab work: matching Shop Dept Manager sees packages even before lead is stamped.
  if (mode === 'personal' && !isFabShippedStatus(pkg.status)) {
    const role = getPrimaryFabRole(userId, assignments, employees);
    if (role === 'shop-super' || role === 'owner-queue') {
      return pkg.status === 'in-progress' || isFabInFabStatus(pkg.status);
    }
    if (role && FAB_DEPT_MANAGER_ROLES.includes(role as FabDashboardRole)) {
      const tradeStatus =
        role === 'dept-manager-mech'
          ? 'in-fab-mech'
          : role === 'dept-manager-plmb'
            ? 'in-fab-plmb'
            : role === 'dept-manager-hvac'
              ? 'in-fab-hvac'
              : null;
      if (
        tradeStatus &&
        (pkg.status === tradeStatus ||
          assemblies.some((assembly) => assembly.status === tradeStatus) ||
          (pkg.status === 'in-progress' &&
            assemblies.some((assembly) => isFabInFabStatus(assembly.status))))
      ) {
        return true;
      }
    }
  }

  return false;
}

export function assembliesVisibleToUser(
  pkg: Task,
  assemblies: Task[],
  userId: string,
  mode: FabWorkstationMode,
  primaryRole: FabDashboardRole | 'owner-queue' | null
): Task[] {
  if (mode === 'queue' || mode === 'warehouse') return assemblies;
  if (getPackageDeptLeadId(pkg) === userId) return assemblies;
  if (getPackageWorkerId(pkg) === userId) return assemblies;
  if (primaryRole === 'worker' || FAB_DEPT_MANAGER_ROLES.includes(primaryRole as FabDashboardRole)) {
    return assemblies.filter((assembly) => (assembly.assigneeIds ?? []).includes(userId));
  }
  return assemblies;
}

export function canAssignDeptLead(
  mode: FabWorkstationMode,
  primaryRole: FabDashboardRole | 'owner-queue' | null
): boolean {
  return mode === 'queue' && (primaryRole === 'shop-super' || primaryRole === 'owner-queue');
}

export function canAssignWorkers(
  mode: FabWorkstationMode,
  pkg: Task | null,
  /** Workstation person in view (Dept Lead) — not necessarily the signed-in user. */
  workstationUserId: string | null,
  assignments: DashboardAssignments | null | undefined = null,
  employees: Employee[] = []
): boolean {
  if (mode !== 'personal' || !pkg || !workstationUserId) return false;

  // Any Shop Dept Manager's fabrication station can sub out packages on that list.
  const role = getPrimaryFabRole(workstationUserId, assignments, employees);
  if (role && FAB_DEPT_MANAGER_ROLES.includes(role as FabDashboardRole)) {
    return true;
  }

  // Fallback: package explicitly assigned to this person as Dept Lead
  return getPackageDeptLeadId(pkg) === workstationUserId;
}

/**
 * Sub-out picker options for a Dept Lead: the lead themselves (leads often fab),
 * then workers who report to them.
 */
export function listWorkersForDeptLead(
  leadId: string | null | undefined,
  employees: Employee[],
  assignments: DashboardAssignments | null | undefined,
  reportsTo: EmployeeReportsToMap = {}
): FabPersonOption[] {
  if (!leadId) return [];

  const options: FabPersonOption[] = [];
  const seen = new Set<string>();

  const leadEmployee = employees.find((employee) => employee.id === leadId);
  if (leadEmployee) {
    const leadRole =
      FAB_DEPT_MANAGER_ROLES.find((roleId) =>
        (assignments?.fab?.[roleId] ?? []).includes(leadId)
      ) ?? getPrimaryFabRole(leadId, assignments, employees);
    const roleId =
      leadRole && leadRole !== 'owner-queue' ? (leadRole as FabDashboardRole) : 'worker';
    options.push({
      id: leadEmployee.id,
      name: leadEmployee.name,
      roleId,
      roleLabel: roleLabel(roleId),
    });
    seen.add(leadEmployee.id);
  }

  const memberIds = employees.map((employee) => employee.id);
  for (const worker of listFabWorkers(employees, assignments)) {
    if (seen.has(worker.id)) continue;
    if (!getEmployeeManagers(worker.id, memberIds, reportsTo).includes(leadId)) continue;
    options.push(worker);
    seen.add(worker.id);
  }

  return options;
}

export function workstationTitle(
  mode: FabWorkstationMode,
  userName: string | null | undefined
): string {
  if (mode === 'queue') return 'Queued Dashboard';
  if (mode === 'warehouse') return 'Warehouse Dashboard';
  return `${userName?.trim() || 'User'}'s Workstation`;
}
