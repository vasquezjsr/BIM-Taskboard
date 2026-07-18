import type { DashboardAssignments, DashboardType, Employee, OrgCategory } from '../types';
import { DETAILER_ORG_CATEGORIES } from '../types';
import {
  FAB_STAFF_IDS,
  FIELD_STAFF_IDS,
  PM_STAFF_IDS,
  SHIPPING_STAFF_IDS,
} from '../data/departmentStaff';
import { isOwnerEmployee } from '../data/employees';
import { inferOrgCategory } from './orgChart';
import type { EmployeeJobTitleDef } from './employeeJobs';

export type EmployeeStageId =
  | 'leadership'
  | 'detailers'
  | 'support'
  | 'pm-ops'
  | 'field-ops'
  | 'fab-ops'
  | 'shipping-ops';

export type EmployeeStageLane = 'office' | 'operations';

export interface EmployeeStageMeta {
  id: EmployeeStageId;
  label: string;
  lane: EmployeeStageLane;
  laneLabel: string;
  description: string;
  dashboard?: DashboardType;
}

export const EMPLOYEE_STAGES: EmployeeStageMeta[] = [
  {
    id: 'leadership',
    label: 'Leadership',
    lane: 'office',
    laneLabel: 'Office',
    description: 'Owner, BIM management, and executive oversight of the VDC department.',
  },
  {
    id: 'detailers',
    label: 'Detailers',
    lane: 'office',
    laneLabel: 'Office',
    description: 'BIM detailers by trade — plumbing, mechanical, and sheet metal.',
  },
  {
    id: 'support',
    label: 'Support',
    lane: 'office',
    laneLabel: 'Office',
    description: 'Support specialists and managers who coordinate deliverables and documents.',
  },
  {
    id: 'pm-ops',
    label: 'Project Management',
    lane: 'operations',
    laneLabel: 'Operations',
    description: 'PM coordinators assigned to the PM dashboard and project oversight.',
    dashboard: 'pm',
  },
  {
    id: 'field-ops',
    label: 'Field',
    lane: 'operations',
    laneLabel: 'Operations',
    description: 'Site superintendents, foremen, and crew leads on active jobsites.',
    dashboard: 'field',
  },
  {
    id: 'fab-ops',
    label: 'Fab Shop',
    lane: 'operations',
    laneLabel: 'Operations',
    description: 'Shop leadership, department managers, and fabrication workers.',
    dashboard: 'fab',
  },
  {
    id: 'shipping-ops',
    label: 'Shipping',
    lane: 'operations',
    laneLabel: 'Operations',
    description: 'Shipping managers and warehouse staff preparing deliveries.',
    dashboard: 'shipping',
  },
];

const OPS_STAGE_STAFF: Record<
  Exclude<EmployeeStageId, 'leadership' | 'detailers' | 'support'>,
  readonly string[]
> = {
  'pm-ops': PM_STAFF_IDS,
  'field-ops': FIELD_STAFF_IDS,
  'fab-ops': FAB_STAFF_IDS,
  'shipping-ops': SHIPPING_STAFF_IDS,
};

function legacyEmployeesForStage(
  stageId: EmployeeStageId,
  employees: Employee[],
  dashboardAssignments: DashboardAssignments
): Employee[] {
  switch (stageId) {
    case 'leadership':
      return employees.filter((employee) => {
        const category = inferOrgCategory(employee);
        return (
          isOwnerEmployee(employee) ||
          category === 'owner' ||
          category === 'bim-manager'
        );
      });
    case 'detailers':
      return employees.filter((employee) => employee.role === 'detailer');
    case 'support':
      return employees.filter((employee) => {
        if (isOwnerEmployee(employee)) return false;
        const category = inferOrgCategory(employee);
        return category === 'support-manager' || category === 'support-specialist';
      });
    case 'pm-ops':
    case 'field-ops':
    case 'fab-ops':
    case 'shipping-ops': {
      const rosterIds = new Set<string>(OPS_STAGE_STAFF[stageId]);
      if (stageId === 'pm-ops') {
        for (const employee of employees) {
          if (inferOrgCategory(employee) === 'operations-manager') {
            rosterIds.add(employee.id);
          }
        }
      }
      const dashboard = EMPLOYEE_STAGES.find((stage) => stage.id === stageId)?.dashboard;
      if (dashboard) {
        for (const memberIds of Object.values(dashboardAssignments[dashboard])) {
          for (const memberId of memberIds) rosterIds.add(memberId);
        }
      }
      return employees.filter((employee) => rosterIds.has(employee.id));
    }
    default:
      return [];
  }
}

export function employeesForStage(
  stageId: EmployeeStageId,
  employees: Employee[],
  dashboardAssignments: DashboardAssignments,
  jobTitles?: EmployeeJobTitleDef[]
): Employee[] {
  if (!jobTitles || jobTitles.length === 0) {
    return legacyEmployeesForStage(stageId, employees, dashboardAssignments);
  }

  const byTitle = employees.filter((employee) => {
    if (isOwnerEmployee(employee) || inferOrgCategory(employee) === 'owner') {
      return stageId === 'leadership';
    }
    if (!employee.jobTitleId) return false;
    const title = jobTitles.find((entry) => entry.id === employee.jobTitleId);
    return title?.stageId === stageId;
  });

  // Include people still missing jobTitleId via legacy rules (without double-counting).
  const titledIds = new Set(byTitle.map((employee) => employee.id));
  const legacy = legacyEmployeesForStage(stageId, employees, dashboardAssignments).filter(
    (employee) => !employee.jobTitleId && !titledIds.has(employee.id)
  );

  return [...byTitle, ...legacy];
}

export function detailerTradeGroups(employees: Employee[]): { category: OrgCategory; label: string; employees: Employee[] }[] {
  return DETAILER_ORG_CATEGORIES.map((category) => ({
    category: category.id,
    label: category.label,
    employees: employees.filter((employee) => inferOrgCategory(employee) === category.id),
  })).filter((group) => group.employees.length > 0);
}

export function stageCounts(
  employees: Employee[],
  dashboardAssignments: DashboardAssignments,
  jobTitles?: EmployeeJobTitleDef[]
): Record<EmployeeStageId, number> {
  return Object.fromEntries(
    EMPLOYEE_STAGES.map((stage) => [
      stage.id,
      employeesForStage(stage.id, employees, dashboardAssignments, jobTitles).length,
    ])
  ) as Record<EmployeeStageId, number>;
}
