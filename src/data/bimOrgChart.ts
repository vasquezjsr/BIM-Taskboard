import type { Employee, OrgCategory } from '../types';
import { JOE_VASQUEZ_ID, TAYLOR_MORGAN_ID } from './employees';
import { DEPARTMENT_STAFF } from './departmentStaff';
import type { EmployeeReportsToMap } from '../utils/orgChart';

/** Executive & department leadership */
export const BIM_LEADERSHIP_EMPLOYEES: Employee[] = [
  {
    id: 'emp-bim-mgr-1',
    name: 'Priya Shah',
    role: 'support-specialist',
    orgCategory: 'bim-manager',
  },
  {
    id: 'emp-ops-mgr-1',
    name: 'Derek Coleman',
    role: 'operations',
    orgCategory: 'operations-manager',
  },
];

/** Additional operations roster */
export const BIM_EXTRA_OPERATIONS: Employee[] = [
  { id: 'emp-fab-6', name: 'Olivia Marsh', role: 'operations', orgCategory: 'operations-staff' },
  { id: 'emp-fab-7', name: 'Jaden Cole', role: 'operations', orgCategory: 'operations-staff' },
];

const DETAILER_TRADE_BY_ID: Record<string, OrgCategory> = {
  'emp-detailer-1': 'plumbing-detailer',
  'emp-detailer-2': 'mechanical-detailer',
  'emp-detailer-3': 'sheet-metal-detailer',
  'emp-detailer-4': 'jr-detailer',
  'emp-detailer-5': 'jr-detailer',
  'emp-detailer-6': 'jr-detailer',
  'emp-detailer-7': 'jr-detailer',
  'emp-detailer-8': 'jr-detailer',
};

export function mergeBimOrgChartEmployees(employees: Employee[]): Employee[] {
  const byId = new Map(employees.map((employee) => [employee.id, employee]));
  const extras = [...BIM_LEADERSHIP_EMPLOYEES, ...BIM_EXTRA_OPERATIONS];

  for (const extra of extras) {
    if (!byId.has(extra.id)) {
      byId.set(extra.id, { ...extra });
    }
  }

  return [...byId.values()].map((employee) => {
    const trade = DETAILER_TRADE_BY_ID[employee.id];
    if (!trade) return employee;
    return { ...employee, role: 'detailer' as const, orgCategory: trade };
  });
}

export function applyBimDetailerOrgCategories(employees: Employee[]): Employee[] {
  return employees.map((employee) => {
    const trade = DETAILER_TRADE_BY_ID[employee.id];
    if (!trade) return employee;
    return { ...employee, role: 'detailer', orgCategory: trade };
  });
}

/**
 * Typical BIM / MEP contractor org chart:
 * Owner → BIM Manager, Operations Manager, Support Manager
 * Detailers & juniors under BIM; PM/Field/Fab/Ship under Operations; support under Taylor.
 */
export function createBimOrgChartReportsTo(): EmployeeReportsToMap {
  return {
    [JOE_VASQUEZ_ID]: [],
    [TAYLOR_MORGAN_ID]: [JOE_VASQUEZ_ID],
    'emp-bim-mgr-1': [JOE_VASQUEZ_ID],
    'emp-ops-mgr-1': [JOE_VASQUEZ_ID],

    'emp-detailer-1': ['emp-bim-mgr-1'],
    'emp-detailer-2': ['emp-bim-mgr-1'],
    'emp-detailer-3': ['emp-bim-mgr-1'],
    'emp-detailer-4': ['emp-detailer-1'],
    'emp-detailer-5': ['emp-detailer-1'],
    'emp-detailer-6': ['emp-detailer-2'],
    'emp-detailer-7': ['emp-detailer-2'],
    'emp-detailer-8': ['emp-detailer-3'],

    'emp-support-2': [TAYLOR_MORGAN_ID],
    'emp-support-3': [TAYLOR_MORGAN_ID],
    'emp-support-4': [TAYLOR_MORGAN_ID],
    'emp-support-5': [TAYLOR_MORGAN_ID],

    'emp-pm-1': ['emp-ops-mgr-1'],
    'emp-pm-2': ['emp-pm-1'],
    'emp-field-1': ['emp-ops-mgr-1'],
    'emp-field-2': ['emp-field-1'],
    'emp-field-3': ['emp-field-1'],
    'emp-fab-1': ['emp-ops-mgr-1'],
    'emp-fab-2': ['emp-fab-1'],
    'emp-fab-3': ['emp-fab-1'],
    'emp-fab-4': ['emp-fab-1'],
    'emp-fab-5': ['emp-fab-1'],
    'emp-fab-6': ['emp-fab-1'],
    'emp-fab-7': ['emp-fab-1'],
    'emp-ship-1': ['emp-ops-mgr-1'],
    'emp-ship-2': ['emp-ship-1'],
  };
}

export function buildBimOrgRoster(baseEmployees: Employee[]): Employee[] {
  const withDepartment = [...baseEmployees];
  const byId = new Set(withDepartment.map((employee) => employee.id));
  for (const staff of DEPARTMENT_STAFF) {
    if (!byId.has(staff.id)) {
      withDepartment.push({
        ...staff,
        orgCategory: staff.orgCategory ?? 'operations-staff',
      });
    }
  }
  return applyBimDetailerOrgCategories(mergeBimOrgChartEmployees(withDepartment));
}
