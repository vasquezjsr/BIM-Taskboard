import type { AppPermission } from '../types';
import type { DashboardAssignments, Employee } from '../types';

/** Project Management coordinators */
export const PM_STAFF_IDS = ['emp-pm-1', 'emp-pm-2'] as const;

/** Field dashboard roster */
export const FIELD_STAFF_IDS = ['emp-field-1', 'emp-field-2', 'emp-field-3'] as const;

/** Fab shop roster */
export const FAB_STAFF_IDS = [
  'emp-fab-1',
  'emp-fab-2',
  'emp-fab-3',
  'emp-fab-4',
  'emp-fab-5',
  'emp-fab-6',
  'emp-fab-7',
  'emp-fab-8',
  'emp-fab-9',
  'emp-fab-10',
] as const;

/** Shipping roster */
export const SHIPPING_STAFF_IDS = ['emp-ship-1', 'emp-ship-2'] as const;

export const DEPARTMENT_STAFF: Employee[] = [
  { id: 'emp-pm-1', name: 'Kendra Walsh', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-pm-2', name: 'Damon Pierce', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-field-1', name: 'Marcus Reed', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-field-2', name: 'Elena Vargas', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-field-3', name: 'Tyler Nash', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-1', name: 'Gina Ortega', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-2', name: 'Liam Porter', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-3', name: 'Sophia Knox', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-4', name: 'Noah Griffin', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-5', name: 'Ethan Walsh', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-6', name: 'Olivia Marsh', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-7', name: 'Jaden Cole', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-8', name: 'Ava Brooks', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-9', name: 'Miles Chen', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-10', name: 'Priya Nair', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-ship-1', name: 'Harper Sloan', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-ship-2', name: 'Mason Price', role: 'operations', orgCategory: 'operations-staff' },
];

const DEPARTMENT_STAFF_IDS = new Set(DEPARTMENT_STAFF.map((employee) => employee.id));

export function isDepartmentStaff(employeeId: string): boolean {
  return DEPARTMENT_STAFF_IDS.has(employeeId);
}

export function createSeededDashboardAssignments(): DashboardAssignments {
  return {
    pm: {
      'project-manager': ['emp-pm-1'],
      'assistant-pm': ['emp-pm-2'],
    },
    field: {
      'site-superintendent': ['emp-field-1'],
      foreman: ['emp-field-2'],
      'crew-lead': ['emp-field-3'],
    },
    fab: {
      'shop-super': ['emp-fab-1'],
      'warehouse-lead': ['emp-fab-8'],
      'warehouse-worker': ['emp-fab-9', 'emp-fab-10'],
      'dept-manager-mech': ['emp-fab-2'],
      'dept-manager-plmb': ['emp-fab-3'],
      'dept-manager-hvac': ['emp-fab-4'],
      worker: ['emp-fab-5', 'emp-fab-6', 'emp-fab-7'],
    },
    shipping: {
      'shipping-manager': ['emp-ship-1'],
      worker: ['emp-ship-2'],
    },
  };
}

function mergeRoleIds(current: string[], seeded: string[]): string[] {
  return [...new Set([...current, ...seeded])];
}

export function mergeDashboardAssignments(
  current: DashboardAssignments,
  seeded: DashboardAssignments
): DashboardAssignments {
  const legacyField = current.field as Record<string, string[] | undefined>;
  return {
    pm: {
      'project-manager': mergeRoleIds(current.pm?.['project-manager'] ?? [], seeded.pm['project-manager']),
      'assistant-pm': mergeRoleIds(current.pm?.['assistant-pm'] ?? [], seeded.pm['assistant-pm']),
    },
    field: {
      'site-superintendent': mergeRoleIds(
        current.field['site-superintendent'] ?? legacyField.pm ?? [],
        seeded.field['site-superintendent']
      ),
      foreman: mergeRoleIds(current.field.foreman ?? legacyField['assistant-pm'] ?? [], seeded.field.foreman),
      'crew-lead': mergeRoleIds(
        current.field['crew-lead'] ?? legacyField.worker ?? [],
        seeded.field['crew-lead']
      ),
    },
    fab: {
      'shop-super': mergeRoleIds(current.fab?.['shop-super'] ?? [], seeded.fab['shop-super']),
      'warehouse-lead': mergeRoleIds(
        [
          ...(current.fab?.['warehouse-lead'] ?? []),
          // Migrate legacy single `warehouse` role → warehouse-lead
          ...((current.fab as { warehouse?: string[] } | undefined)?.warehouse ?? []),
        ],
        seeded.fab['warehouse-lead']
      ),
      'warehouse-worker': mergeRoleIds(
        current.fab?.['warehouse-worker'] ?? [],
        seeded.fab['warehouse-worker']
      ),
      'dept-manager-mech': mergeRoleIds(
        current.fab?.['dept-manager-mech'] ?? [],
        seeded.fab['dept-manager-mech']
      ),
      'dept-manager-plmb': mergeRoleIds(
        current.fab?.['dept-manager-plmb'] ?? [],
        seeded.fab['dept-manager-plmb']
      ),
      'dept-manager-hvac': mergeRoleIds(
        current.fab?.['dept-manager-hvac'] ?? [],
        seeded.fab['dept-manager-hvac']
      ),
      worker: mergeRoleIds(current.fab?.worker ?? [], seeded.fab.worker),
    },
    shipping: {
      'shipping-manager': mergeRoleIds(
        current.shipping['shipping-manager'],
        seeded.shipping['shipping-manager']
      ),
      worker: mergeRoleIds(current.shipping.worker, seeded.shipping.worker),
    },
  };
}

export function operationsDashboardPermissions(employeeId: string): AppPermission[] {
  const permissions: AppPermission[] = [];
  if ((PM_STAFF_IDS as readonly string[]).includes(employeeId)) {
    permissions.push(
      'view-pm-dashboard',
      'view-field-dashboard',
      'view-fab-dashboard',
      'view-shipping-dashboard',
      'view-weld-log-dashboard'
    );
  }
  if ((FIELD_STAFF_IDS as readonly string[]).includes(employeeId)) {
    permissions.push('view-field-dashboard', 'view-weld-log-dashboard');
  }
  if ((FAB_STAFF_IDS as readonly string[]).includes(employeeId)) {
    permissions.push('view-fab-dashboard', 'view-weld-log-dashboard');
  }
  if ((SHIPPING_STAFF_IDS as readonly string[]).includes(employeeId)) {
    permissions.push('view-shipping-dashboard');
  }
  return permissions;
}

export function mergeDepartmentStaff(employees: Employee[]): Employee[] {
  const byId = new Map(employees.map((employee) => [employee.id, employee]));
  const merged = [...employees];

  for (const staff of DEPARTMENT_STAFF) {
    if (!byId.has(staff.id)) {
      merged.push({ ...staff });
    }
  }

  return merged;
}

/** Legacy field role keys from before site-superintendent / foreman / crew-lead rename */
export type LegacyFieldDashboardRole = 'pm' | 'assistant-pm' | 'worker';
