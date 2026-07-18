import type { Employee, Project, Task } from '../types';
import {
  isSsv3AssemblyTask,
  isSsv3FieldPackageTask,
  isSsv3ShippingPackageTask,
  SSV3_FIELD,
} from './boardroomPackageImport';
import { canManageOrg } from './permissions';
import type { EmployeePermissionsMap } from './orgChart';
import { isOrgOwner } from './orgChart';

/** Shipping lanes at or past In Transit — visible to Field (inbound). */
export const FIELD_INBOUND_SHIPPING_STATUSES = [
  'in-transit',
  'delivered',
  'received-field',
  'complete',
] as const;

export type FieldInboundShippingStatus = (typeof FIELD_INBOUND_SHIPPING_STATUSES)[number];

export function isShippingStatusVisibleToField(status: string): boolean {
  return (FIELD_INBOUND_SHIPPING_STATUSES as readonly string[]).includes(status);
}

export function getAssemblyShippingLane(task: Task): string {
  return task.customFields?.[SSV3_FIELD.shippingStatus] ?? 'staging';
}

/** Whether this employee may see Field Dashboard packages for a project. */
export function canEmployeeSeeFieldProject(
  employeeId: string | null,
  project: Project | null | undefined,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap = {}
): boolean {
  if (!employeeId || !project) return false;
  if (canManageOrg(employeeId, employees, employeePermissions)) return true;
  const employee = employees.find((entry) => entry.id === employeeId);
  if (isOrgOwner(employee)) return true;
  const fieldLead = (project.fieldIds ?? []).includes(employeeId);
  const fieldCrew = (project.fieldCrewIds ?? []).includes(employeeId);
  return fieldLead || fieldCrew;
}

export function isPackageInboundFromShipping(pkg: Task): boolean {
  return isSsv3ShippingPackageTask(pkg);
}

/**
 * Packages Field should list:
 * - Already on Field board, or
 * - Still on Shipping with package/assembly lane In Transit or later.
 */
export function isPackageVisibleOnFieldDashboard(
  pkg: Task,
  allTasks: Task[]
): boolean {
  if (isSsv3FieldPackageTask(pkg)) return true;
  if (!isSsv3ShippingPackageTask(pkg)) return false;
  if (isShippingStatusVisibleToField(pkg.status)) return true;
  const assemblies = allTasks.filter(
    (task) => isSsv3AssemblyTask(task) && task.parentTaskId === pkg.id
  );
  return assemblies.some((assembly) =>
    isShippingStatusVisibleToField(getAssemblyShippingLane(assembly))
  );
}

/**
 * Assemblies shown for a Field-visible package.
 * Inbound shipping: only assemblies already In Transit+, unless the whole package is.
 */
export function assembliesForFieldDashboardView(
  pkg: Task,
  assemblies: Task[]
): Task[] {
  if (isSsv3FieldPackageTask(pkg)) return assemblies;
  if (!isSsv3ShippingPackageTask(pkg)) return assemblies;
  if (isShippingStatusVisibleToField(pkg.status)) return assemblies;
  return assemblies.filter((assembly) =>
    isShippingStatusVisibleToField(getAssemblyShippingLane(assembly))
  );
}

export function inboundShippingLabel(pkg: Task, assemblies: Task[]): string | null {
  if (!isSsv3ShippingPackageTask(pkg)) return null;
  if (isShippingStatusVisibleToField(pkg.status)) {
    return pkg.status;
  }
  const lanes = assemblies
    .map(getAssemblyShippingLane)
    .filter(isShippingStatusVisibleToField);
  if (lanes.length === 0) return null;
  // Prefer the “least advanced” inbound lane so Field sees earliest state clearly.
  for (const lane of FIELD_INBOUND_SHIPPING_STATUSES) {
    if (lanes.includes(lane)) return lane;
  }
  return lanes[0] ?? null;
}
