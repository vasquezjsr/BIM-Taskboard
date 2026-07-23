import type { DashboardAssignments, Task } from '../types';
import {
  isSsv3PackageTask,
  SSV3_FIELD,
} from './boardroomPackageImport';
import { collectDescendantTaskIds } from './detailersSpoolingHandoff';
import { isAssemblyReleasedFromFabStatus, isFabInFabStatus } from './taskStatuses';

/** Map package/assembly In Fab statuses to Shop Dept Manager dashboard roles. */
export const IN_FAB_STATUS_TO_DEPT_ROLE = {
  'in-fab-mech': 'dept-manager-mech',
  'in-fab-plmb': 'dept-manager-plmb',
  'in-fab-hvac': 'dept-manager-hvac',
} as const;

export type InFabStatusId = keyof typeof IN_FAB_STATUS_TO_DEPT_ROLE;

export function deptLeadRoleForInFabStatus(
  status: string
): (typeof IN_FAB_STATUS_TO_DEPT_ROLE)[InFabStatusId] | null {
  if (status in IN_FAB_STATUS_TO_DEPT_ROLE) {
    return IN_FAB_STATUS_TO_DEPT_ROLE[status as InFabStatusId];
  }
  return null;
}

/** Statuses that should copy from Fab package → nested assemblies. */
export function shouldCascadeFabPackageStatusToAssemblies(status: string): boolean {
  if (isFabInFabStatus(status)) return true;
  return (
    status === 'queued' ||
    status === 'not-started' ||
    status === 'pulling-material' ||
    status === 'material-pulled' ||
    status === 'spooling'
  );
}

/**
 * When a Fab package status changes, push it to assemblies.
 * In Fab (Mech/Plmb/HVAC) on the package → assemblies get that status, package becomes
 * In Progress, and the matching Dept Lead is stamped when missing (so it appears on
 * the Fabrication Dashboard workstation list).
 */
export function cascadeFabPackageStatusToAssemblies(
  tasks: Task[],
  parentIds: Iterable<string>,
  newStatus: string,
  assignments?: DashboardAssignments | null
): Task[] {
  if (!shouldCascadeFabPackageStatusToAssemblies(newStatus)) return tasks;

  const roots = new Set<string>();
  for (const id of parentIds) {
    const parent = tasks.find((task) => task.id === id);
    if (!parent) continue;
    if (parent.boardType !== 'fab') continue;
    if (parent.parentTaskId) continue;
    // Prefer SSv3 packages; also allow any fab root with assembly children.
    if (isSsv3PackageTask(parent) || tasks.some((task) => task.parentTaskId === parent.id)) {
      roots.add(id);
    }
  }
  if (roots.size === 0) return tasks;

  const childIds = new Set<string>();
  for (const rootId of roots) {
    for (const childId of collectDescendantTaskIds(tasks, rootId)) {
      if (!roots.has(childId)) childIds.add(childId);
    }
  }

  const packageStatus = isFabInFabStatus(newStatus) ? 'in-progress' : newStatus;
  const assemblyStatus = newStatus;
  const deptRole = deptLeadRoleForInFabStatus(newStatus);
  const autoLeadId =
    deptRole && assignments?.fab?.[deptRole]?.length
      ? assignments.fab[deptRole]![0]!
      : null;

  return tasks.map((task) => {
    if (roots.has(task.id)) {
      const customFields = { ...(task.customFields ?? {}) };
      if (autoLeadId && !customFields[SSV3_FIELD.deptLeadId]?.trim()) {
        customFields[SSV3_FIELD.deptLeadId] = autoLeadId;
      }
      return {
        ...task,
        status: packageStatus,
        customFields,
      };
    }

    if (!childIds.has(task.id)) return task;
    if (isAssemblyReleasedFromFabStatus(task.status)) return task;

    return {
      ...task,
      boardType: 'fab' as const,
      status: assemblyStatus,
    };
  });
}
