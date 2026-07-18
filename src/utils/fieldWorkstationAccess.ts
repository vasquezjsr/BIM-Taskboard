import type { Employee, Project, Task } from '../types';
import {
  isSsv3AssemblyTask,
  isSsv3FieldPackageTask,
  isSsv3PackageTask,
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

/**
 * Package is still traveling (Fab partial release or Shipping board) — not yet
 * handed off onto the Field board.
 */
export function isPackageInboundFromShipping(pkg: Task): boolean {
  return !isSsv3FieldPackageTask(pkg);
}

function packageHasFieldInboundAssembly(pkg: Task, allTasks: Task[]): boolean {
  return allTasks.some(
    (task) =>
      isSsv3AssemblyTask(task) &&
      task.parentTaskId === pkg.id &&
      isShippingStatusVisibleToField(getAssemblyShippingLane(task))
  );
}

/**
 * Packages Field should list:
 * - Already on Field board, or
 * - Still on Shipping / Fab with package or assembly lane In Transit or later
 *   (supports partial release while the package remains on Fab).
 */
export function isPackageVisibleOnFieldDashboard(
  pkg: Task,
  allTasks: Task[]
): boolean {
  if (isSsv3FieldPackageTask(pkg)) return true;

  if (isSsv3ShippingPackageTask(pkg)) {
    if (isShippingStatusVisibleToField(pkg.status)) return true;
    return packageHasFieldInboundAssembly(pkg, allTasks);
  }

  // Partial Fab release: package stays on Fab while individual assemblies ship.
  if (isSsv3PackageTask(pkg)) {
    return packageHasFieldInboundAssembly(pkg, allTasks);
  }

  return false;
}

/**
 * Assemblies shown for a Field-visible package.
 * Inbound (Fab/Shipping): only assemblies already In Transit+, unless the whole
 * Shipping package lane is already In Transit+.
 */
export function assembliesForFieldDashboardView(
  pkg: Task,
  assemblies: Task[]
): Task[] {
  if (isSsv3FieldPackageTask(pkg)) return assemblies;
  if (isSsv3ShippingPackageTask(pkg) && isShippingStatusVisibleToField(pkg.status)) {
    return assemblies;
  }
  return assemblies.filter((assembly) =>
    isShippingStatusVisibleToField(getAssemblyShippingLane(assembly))
  );
}

export function inboundShippingLabel(pkg: Task, assemblies: Task[]): string | null {
  if (isSsv3FieldPackageTask(pkg)) return null;
  if (
    isSsv3ShippingPackageTask(pkg) &&
    isShippingStatusVisibleToField(pkg.status)
  ) {
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
