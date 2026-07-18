import type { Employee } from '../types';
import { ORG_CATEGORIES } from '../types';
import { FAB_DASHBOARD_ROLES } from '../data/dashboards';
import { createSeededDashboardAssignments } from '../data/departmentStaff';
import { inferOrgCategory } from './orgChart';

export interface ViewAsOption {
  employeeId: string;
  label: string;
}

export interface ViewAsGroup {
  label: string;
  options: ViewAsOption[];
}

const OPERATIONS_PERSPECTIVES: { label: string; employeeId: string }[] = [
  { label: 'PM Lead', employeeId: 'emp-pm-1' },
  { label: 'Assistant PM', employeeId: 'emp-pm-2' },
  { label: 'Site Superintendent', employeeId: 'emp-field-1' },
  { label: 'Field Foreman', employeeId: 'emp-field-2' },
  { label: 'Shipping Manager', employeeId: 'emp-ship-1' },
  { label: 'Shipping Worker', employeeId: 'emp-ship-2' },
];

export function buildViewAsGroups(employees: Employee[]): ViewAsGroup[] {
  const byId = new Map(employees.map((employee) => [employee.id, employee]));
  const groups: ViewAsGroup[] = [];
  const usedIds = new Set<string>();

  const orgOptions: ViewAsOption[] = [];
  for (const category of ORG_CATEGORIES) {
    const match = employees.find(
      (employee) => inferOrgCategory(employee) === category.id && !usedIds.has(employee.id)
    );
    if (!match) continue;
    usedIds.add(match.id);
    orgOptions.push({
      employeeId: match.id,
      label:
        category.id === 'owner'
          ? `${match.name} (Owner)`
          : `${category.label} — ${match.name}`,
    });
  }

  if (orgOptions.length > 0) {
    groups.push({ label: 'Office & Detailers', options: orgOptions });
  }

  const opsOptions = OPERATIONS_PERSPECTIVES.flatMap((perspective) => {
    const employee = byId.get(perspective.employeeId);
    if (!employee) return [];
    usedIds.add(employee.id);
    return [
      {
        employeeId: employee.id,
        label: `${perspective.label} — ${employee.name}`,
      },
    ];
  });

  if (opsOptions.length > 0) {
    groups.push({ label: 'PM, Field & Shipping', options: opsOptions });
  }

  const fabAssignments = createSeededDashboardAssignments().fab;
  const fabOptions: ViewAsOption[] = [];
  const fabSeen = new Set<string>();
  for (const role of FAB_DASHBOARD_ROLES) {
    for (const id of fabAssignments[role.id] ?? []) {
      if (fabSeen.has(id)) continue;
      const employee = byId.get(id);
      if (!employee) continue;
      fabSeen.add(id);
      fabOptions.push({
        employeeId: employee.id,
        label: `${role.label} — ${employee.name}`,
      });
    }
  }

  if (fabOptions.length > 0) {
    groups.push({ label: 'Fab Shop', options: fabOptions });
  }

  return groups;
}
