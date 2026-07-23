import type { Employee } from '../types';
import {
  employeeAssigneeStyleFromMap,
  type EmployeeAssigneeStyle,
  type EmployeeAssigneeStylesMap,
} from './assigneeColors';
import { remapTaskAssigneeIds, type TaskWithLegacyAssignee } from '../utils/taskAssignees';
import { inferOrgCategory } from '../utils/orgChart';

export type { EmployeeAssigneeStyle, EmployeeAssigneeStylesMap };

export const JOE_VASQUEZ_ID = 'emp-support-1';
export const OWNER_EMPLOYEE_ID = JOE_VASQUEZ_ID;
/** Support Manager roster id (legacy owner / Taylor Morgan id) */
export const TAYLOR_MORGAN_ID = 'emp-owner-1';

/** @deprecated Legacy roster ids kept for migrations only */
export const LEE_CRESON_ID = 'emp-detailer-4';
/** @deprecated Legacy roster ids kept for migrations only */
export const JESSE_VASQUEZ_ID = 'emp-support-4';

/**
 * Demo / template display names by roster id.
 * Real person names are replaced with role titles — except Joe Vasquez (owner/builder).
 */
export const SEED_ROLE_DISPLAY_NAMES: Readonly<Record<string, string>> = {
  [JOE_VASQUEZ_ID]: 'Joe Vasquez',
  [TAYLOR_MORGAN_ID]: 'Support Manager',
  'emp-support-2': 'Support Specialist 1',
  'emp-support-3': 'Support Specialist 2',
  'emp-support-4': 'Support Specialist 3',
  'emp-support-5': 'Support Specialist 4',

  'emp-bim-mgr-1': 'BIM Manager',
  'emp-ops-mgr-1': 'Operations Manager',

  'emp-detailer-1': 'Plmb Lead Detailer',
  'emp-detailer-2': 'Mech Lead Detailer',
  'emp-detailer-3': 'SM Lead Detailer',
  'emp-detailer-4': 'Plmb Detailer 1',
  'emp-detailer-5': 'Plmb Detailer 2',
  'emp-detailer-6': 'Mech Detailer 1',
  'emp-detailer-7': 'Mech Detailer 2',
  'emp-detailer-8': 'SM Detailer 1',

  'emp-pm-1': 'Project Manager',
  'emp-pm-2': 'Assistant PM',
  'emp-field-1': 'Field Worker 1',
  'emp-field-2': 'Field Worker 2',
  'emp-field-3': 'Field Worker 3',
  'emp-fab-1': 'Fab Shop Super',
  'emp-fab-2': 'Fab Mech Lead',
  'emp-fab-3': 'Fab Plmb Lead',
  'emp-fab-4': 'Fab HVAC Lead',
  'emp-fab-5': 'Fab Shop Worker 1',
  'emp-fab-6': 'Fab Shop Worker 2',
  'emp-fab-7': 'Fab Shop Worker 3',
  'emp-fab-8': 'Warehouse Lead',
  'emp-fab-9': 'Warehouse Worker 1',
  'emp-fab-10': 'Warehouse Worker 2',
  'emp-ship-1': 'Shipping Manager',
  'emp-ship-2': 'Shipping Specialist 1',
};

export const DEFAULT_EMPLOYEES: Employee[] = [
  {
    id: JOE_VASQUEZ_ID,
    name: SEED_ROLE_DISPLAY_NAMES[JOE_VASQUEZ_ID]!,
    role: 'support-specialist',
    orgCategory: 'owner',
  },
  { id: 'emp-detailer-1', name: SEED_ROLE_DISPLAY_NAMES['emp-detailer-1']!, role: 'detailer' },
  { id: 'emp-detailer-2', name: SEED_ROLE_DISPLAY_NAMES['emp-detailer-2']!, role: 'detailer' },
  { id: 'emp-detailer-3', name: SEED_ROLE_DISPLAY_NAMES['emp-detailer-3']!, role: 'detailer' },
  { id: 'emp-detailer-4', name: SEED_ROLE_DISPLAY_NAMES['emp-detailer-4']!, role: 'detailer' },
  { id: 'emp-detailer-5', name: SEED_ROLE_DISPLAY_NAMES['emp-detailer-5']!, role: 'detailer' },
  { id: 'emp-detailer-6', name: SEED_ROLE_DISPLAY_NAMES['emp-detailer-6']!, role: 'detailer' },
  { id: 'emp-detailer-7', name: SEED_ROLE_DISPLAY_NAMES['emp-detailer-7']!, role: 'detailer' },
  { id: 'emp-detailer-8', name: SEED_ROLE_DISPLAY_NAMES['emp-detailer-8']!, role: 'detailer' },
  {
    id: TAYLOR_MORGAN_ID,
    name: SEED_ROLE_DISPLAY_NAMES[TAYLOR_MORGAN_ID]!,
    role: 'support-specialist',
    orgCategory: 'support-manager',
  },
  { id: 'emp-support-2', name: SEED_ROLE_DISPLAY_NAMES['emp-support-2']!, role: 'support-specialist' },
  { id: 'emp-support-3', name: SEED_ROLE_DISPLAY_NAMES['emp-support-3']!, role: 'support-specialist' },
  { id: 'emp-support-4', name: SEED_ROLE_DISPLAY_NAMES['emp-support-4']!, role: 'support-specialist' },
  { id: 'emp-support-5', name: SEED_ROLE_DISPLAY_NAMES['emp-support-5']!, role: 'support-specialist' },
];

/**
 * Apply role-title display names for seeded roster IDs.
 * Joe Vasquez keeps whatever real name is stored (defaults to Joe Vasquez).
 */
export function applySeedRoleDisplayNames(employees: Employee[]): Employee[] {
  return employees.map((employee) => {
    if (employee.id === JOE_VASQUEZ_ID) {
      return {
        ...employee,
        name: employee.name?.trim() || SEED_ROLE_DISPLAY_NAMES[JOE_VASQUEZ_ID]!,
      };
    }
    const roleName = SEED_ROLE_DISPLAY_NAMES[employee.id];
    if (!roleName) return employee;
    return { ...employee, name: roleName };
  });
}

export const SUPPORT_ASSIGNEE_IDS = [
  TAYLOR_MORGAN_ID,
  'emp-support-2',
  'emp-support-3',
  'emp-support-4',
  'emp-support-5',
] as const;

const DEFAULT_EMPLOYEE_IDS = new Set(DEFAULT_EMPLOYEES.map((employee) => employee.id));

/** Ensures protected roster members (owner, support manager) always exist after edits. */
export function reconcileCoreRoster(employees: Employee[]): Employee[] {
  const byId = new Map(employees.map((employee) => [employee.id, employee]));

  for (const defaults of DEFAULT_EMPLOYEES) {
    const existing = byId.get(defaults.id);
    if (!existing) {
      byId.set(defaults.id, { ...defaults });
      continue;
    }

    if (defaults.id === JOE_VASQUEZ_ID) {
      byId.set(defaults.id, {
        ...existing,
        id: JOE_VASQUEZ_ID,
        name: existing.name?.trim() || defaults.name,
        role: defaults.role,
        orgCategory: 'owner',
      });
      continue;
    }

    if (defaults.id === TAYLOR_MORGAN_ID) {
      byId.set(defaults.id, {
        ...existing,
        id: TAYLOR_MORGAN_ID,
        name: defaults.name,
        role: 'support-specialist',
        orgCategory: 'support-manager',
      });
    }
  }

  const defaultIds = new Set(DEFAULT_EMPLOYEES.map((employee) => employee.id));
  const customs = employees.filter((employee) => !defaultIds.has(employee.id));
  return applySeedRoleDisplayNames([
    ...DEFAULT_EMPLOYEES.map((defaults) => byId.get(defaults.id) ?? defaults),
    ...customs,
  ]);
}

export function isProtectedRosterEmployee(employeeId: string): boolean {
  return (
    employeeId === JOE_VASQUEZ_ID ||
    employeeId === TAYLOR_MORGAN_ID ||
    employeeId === 'emp-bim-mgr-1' ||
    employeeId === 'emp-ops-mgr-1'
  );
}

export function isOwnerEmployee(employee: Employee | undefined): boolean {
  return employee ? inferOrgCategory(employee) === 'owner' : false;
}

export function employeeInitials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .map((part) => part[0]!)
    .join('')
    .toUpperCase();
}

export function employeeAssigneeStyle(
  employeeId: string,
  styleMap: EmployeeAssigneeStylesMap = {}
): EmployeeAssigneeStyle {
  return employeeAssigneeStyleFromMap(employeeId, styleMap);
}

export function assignDeliverableTasksToSupport<
  T extends { boardType: string; assigneeIds?: string[]; assigneeId?: string | null; groupId?: string | null },
>(
  tasks: T[],
  groups: { id: string; parentId: string | null; tier: string; sectionBoardType: import('../types').ProjectBoardType | null }[] = []
): T[] {
  return tasks.map((task) => {
    const onDeliverables =
      task.boardType === 'deliverables' ||
      (groups.length > 0 && inferTaskBranchFromGroups(task, groups) === 'deliverables');
    if (!onDeliverables) return task;
    return { ...task, assigneeIds: [...SUPPORT_ASSIGNEE_IDS] };
  });
}

function inferTaskBranchFromGroups(
  task: { groupId?: string | null },
  groups: { id: string; parentId: string | null; tier: string; sectionBoardType: import('../types').ProjectBoardType | null }[]
): import('../types').ProjectBoardType {
  if (!task.groupId) return 'main';
  let current = groups.find((g) => g.id === task.groupId);
  while (current) {
    if (current.tier === 'section' && current.sectionBoardType) {
      return current.sectionBoardType;
    }
    current = current.parentId ? groups.find((g) => g.id === current!.parentId) : undefined;
  }
  return 'main';
}

export const LEGACY_NAME_TO_ID: Record<string, string> = {
  'Alex Rivera': 'emp-detailer-1',
  'Jordan Kim': 'emp-detailer-2',
  'Sam Chen': JOE_VASQUEZ_ID,
  'Taylor Brooks': 'emp-support-2',
  'Tanner Scholin': 'emp-detailer-1',
  'Arturo Cantu': 'emp-detailer-2',
  'AJ Healy': 'emp-detailer-3',
  'Lee Creson': 'emp-detailer-4',
  Eldridge: 'emp-detailer-5',
  'Miguel Marin': 'emp-support-2',
  'Sky Creson': 'emp-support-3',
  'Jesse Vasquez': JESSE_VASQUEZ_ID,
  'Ryan Cooper': 'emp-detailer-1',
  'Casey Brooks': 'emp-detailer-2',
  'Avery Lane': 'emp-detailer-3',
  'Quinn Parker': 'emp-detailer-4',
  'Blake Turner': 'emp-detailer-5',
  'Drew Hayes': 'emp-detailer-6',
  'Riley Bennett': 'emp-detailer-7',
  'Cameron Shaw': 'emp-detailer-8',
  'Taylor Morgan': TAYLOR_MORGAN_ID,
  'Jamie Foster': 'emp-support-2',
  'Chris Dalton': 'emp-support-3',
  'Morgan Ellis': 'emp-support-4',
  'Jordan Hayes': 'emp-support-5',
  'Priya Shah': 'emp-bim-mgr-1',
  'Derek Coleman': 'emp-ops-mgr-1',
  'Kendra Walsh': 'emp-pm-1',
  'Damon Pierce': 'emp-pm-2',
  'Marcus Reed': 'emp-field-1',
  'Elena Vargas': 'emp-field-2',
  'Tyler Nash': 'emp-field-3',
  'Gina Ortega': 'emp-fab-1',
  'Liam Porter': 'emp-fab-2',
  'Sophia Knox': 'emp-fab-3',
  'Noah Griffin': 'emp-fab-4',
  'Ethan Walsh': 'emp-fab-5',
  'Olivia Marsh': 'emp-fab-6',
  'Jaden Cole': 'emp-fab-7',
  'Ava Brooks': 'emp-fab-8',
  'Miles Chen': 'emp-fab-9',
  'Priya Nair': 'emp-fab-10',
  'Harper Sloan': 'emp-ship-1',
  'Mason Price': 'emp-ship-2',
  'Detailer 1': 'emp-detailer-1',
  'Detailer 2': 'emp-detailer-2',
  'Detailer 3': 'emp-detailer-3',
  'Detailer 4': 'emp-detailer-4',
  'Detailer 5': 'emp-detailer-5',
  'Support Specialist 1': 'emp-support-2',
  'Support Specialist 2': 'emp-support-3',
  'Support Specialist 3': 'emp-support-4',
};

export function normalizeEmployeesWithRemap<T extends TaskWithLegacyAssignee>(
  employees: Employee[],
  tasks: T[] = []
): { employees: Employee[]; tasks: T[] } {
  const idMap = new Map<string, string>();
  const persistedById = new Map(employees.map((employee) => [employee.id, employee]));

  for (const employee of employees) {
    const mappedId = LEGACY_NAME_TO_ID[employee.name];
    if (mappedId && mappedId !== employee.id) {
      idMap.set(employee.id, mappedId);
    }
  }

  const customEmployees = employees.filter(
    (employee) => !DEFAULT_EMPLOYEE_IDS.has(employee.id) && !idMap.has(employee.id)
  );

  const mergedEmployees = reconcileCoreRoster(
    DEFAULT_EMPLOYEES.map((defaults) => {
      const persisted = persistedById.get(defaults.id);
      if (!persisted) return defaults;
      const name =
        defaults.id === JOE_VASQUEZ_ID
          ? persisted.name?.trim() || defaults.name
          : defaults.name;
      return {
        ...defaults,
        ...persisted,
        id: defaults.id,
        name,
        role: defaults.role,
        orgCategory: defaults.orgCategory ?? persisted.orgCategory,
      };
    }).concat(customEmployees)
  );

  const updatedTasks =
    idMap.size > 0 ? (tasks.map((task) => remapTaskAssigneeIds(task, idMap)) as T[]) : tasks;

  return { employees: applySeedRoleDisplayNames(mergedEmployees), tasks: updatedTasks };
}

/** @deprecated Use normalizeEmployeesWithRemap */
export function migrateEmployeesToRoster(
  employees: Employee[],
  tasks: TaskWithLegacyAssignee[]
): { employees: Employee[]; tasks: typeof tasks } {
  return normalizeEmployeesWithRemap(employees, tasks);
}

export function remapProjectMemberIds(
  project: { detailerIds: string[]; supportIds: string[] },
  idMap: Map<string, string>
): { detailerIds: string[]; supportIds: string[] } {
  const remap = (ids: string[]) =>
    [...new Set(ids.map((id) => (idMap.has(id) ? idMap.get(id)! : id)))];
  return {
    detailerIds: remap(project.detailerIds),
    supportIds: remap(project.supportIds),
  };
}
