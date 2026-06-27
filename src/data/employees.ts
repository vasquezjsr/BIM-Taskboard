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
/** Taylor Morgan — support specialist manager roster id (legacy owner id) */
export const TAYLOR_MORGAN_ID = 'emp-owner-1';

/** @deprecated Legacy roster ids kept for migrations only */
export const LEE_CRESON_ID = 'emp-detailer-4';
/** @deprecated Legacy roster ids kept for migrations only */
export const JESSE_VASQUEZ_ID = 'emp-support-4';

export const DEFAULT_EMPLOYEES: Employee[] = [
  {
    id: JOE_VASQUEZ_ID,
    name: 'Joe Vasquez',
    role: 'support-specialist',
    orgCategory: 'owner',
  },
  { id: 'emp-detailer-1', name: 'Ryan Cooper', role: 'detailer' },
  { id: 'emp-detailer-2', name: 'Casey Brooks', role: 'detailer' },
  { id: 'emp-detailer-3', name: 'Avery Lane', role: 'detailer' },
  { id: 'emp-detailer-4', name: 'Quinn Parker', role: 'detailer' },
  { id: 'emp-detailer-5', name: 'Blake Turner', role: 'detailer' },
  { id: 'emp-detailer-6', name: 'Drew Hayes', role: 'detailer' },
  { id: 'emp-detailer-7', name: 'Riley Bennett', role: 'detailer' },
  { id: 'emp-detailer-8', name: 'Cameron Shaw', role: 'detailer' },
  {
    id: TAYLOR_MORGAN_ID,
    name: 'Taylor Morgan',
    role: 'support-specialist',
    orgCategory: 'support-manager',
  },
  { id: 'emp-support-2', name: 'Jamie Foster', role: 'support-specialist' },
  { id: 'emp-support-3', name: 'Chris Dalton', role: 'support-specialist' },
  { id: 'emp-support-4', name: 'Morgan Ellis', role: 'support-specialist' },
  { id: 'emp-support-5', name: 'Jordan Hayes', role: 'support-specialist' },
];

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
  return [...DEFAULT_EMPLOYEES.map((defaults) => byId.get(defaults.id) ?? defaults), ...customs];
}

export function isProtectedRosterEmployee(employeeId: string): boolean {
  return employeeId === JOE_VASQUEZ_ID || employeeId === TAYLOR_MORGAN_ID;
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
    (employee) =>
      !DEFAULT_EMPLOYEE_IDS.has(employee.id) &&
      !LEGACY_NAME_TO_ID[employee.name] &&
      !idMap.has(employee.id)
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

  return { employees: mergedEmployees, tasks: updatedTasks };
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
