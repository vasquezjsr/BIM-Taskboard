import type { DashboardAssignments, DashboardType, Employee, OrgCategory } from '../types';
import { DETAILER_ORG_CATEGORIES } from '../types';
import { dashboardRolesFor } from '../data/dashboards';
import { inferOrgCategory } from './orgChart';
import { EMPLOYEE_STAGES, type EmployeeStageId } from './employeeDashboardStages';

/** @deprecated Prefer EmployeeJobTitleDef — kept for permission helpers. */
export type EmployeeJobOption = {
  id: string;
  label: string;
  group: 'Office' | 'Detailers' | 'Operations';
  orgCategory: OrgCategory;
  opsPlacement?: { dashboard: DashboardType; roleId: string };
};

export type EmployeeJobTitleDef = {
  id: string;
  label: string;
  stageId: EmployeeStageId;
  /** Office / leadership / support / ops-manager placement. */
  orgCategory?: OrgCategory;
  /** Ops dashboard role slot (PM / Field / Fab / Shipping). */
  opsPlacement?: { dashboard: DashboardType; roleId: string };
};

export type JobTitleGroup = 'Office' | 'Detailers' | 'Operations';

const OPS_LEAD_ROLE_IDS: Record<DashboardType, ReadonlySet<string>> = {
  pm: new Set(['project-manager']),
  field: new Set(['site-superintendent']),
  fab: new Set(['shop-super', 'warehouse-lead', 'dept-manager-mech', 'dept-manager-plmb', 'dept-manager-hvac']),
  shipping: new Set(['shipping-manager']),
};

/** Seed catalog — mirrors the original hardcoded promote options. */
export const DEFAULT_EMPLOYEE_JOB_TITLES: EmployeeJobTitleDef[] = [
  {
    id: 'bim-manager',
    label: 'BIM Manager',
    stageId: 'leadership',
    orgCategory: 'bim-manager',
  },
  {
    id: 'operations-manager',
    label: 'Operations Manager',
    stageId: 'pm-ops',
    orgCategory: 'operations-manager',
  },
  {
    id: 'support-manager',
    label: 'Support Manager',
    stageId: 'support',
    orgCategory: 'support-manager',
  },
  {
    id: 'support-specialist',
    label: 'Support Specialist',
    stageId: 'support',
    orgCategory: 'support-specialist',
  },
  ...DETAILER_ORG_CATEGORIES.map((category) => ({
    id: category.id,
    label: category.label,
    stageId: 'detailers' as const,
    orgCategory: category.id,
  })),
  {
    id: 'ops-pm-lead',
    label: 'Project Manager',
    stageId: 'pm-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'pm', roleId: 'project-manager' },
  },
  {
    id: 'ops-pm-staff',
    label: 'Assistant PM',
    stageId: 'pm-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'pm', roleId: 'assistant-pm' },
  },
  {
    id: 'ops-field-lead',
    label: 'Site Superintendent',
    stageId: 'field-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'field', roleId: 'site-superintendent' },
  },
  {
    id: 'ops-field-foreman',
    label: 'Foreman',
    stageId: 'field-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'field', roleId: 'foreman' },
  },
  {
    id: 'ops-field-crew',
    label: 'Crew Lead',
    stageId: 'field-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'field', roleId: 'crew-lead' },
  },
  {
    id: 'ops-fab-super',
    label: 'Shop Super',
    stageId: 'fab-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'fab', roleId: 'shop-super' },
  },
  {
    id: 'ops-fab-wh-lead',
    label: 'Warehouse Lead',
    stageId: 'fab-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'fab', roleId: 'warehouse-lead' },
  },
  {
    id: 'ops-fab-dept-mech',
    label: 'Shop Dept Manager (Mech)',
    stageId: 'fab-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'fab', roleId: 'dept-manager-mech' },
  },
  {
    id: 'ops-fab-dept-plmb',
    label: 'Shop Dept Manager (Plmb)',
    stageId: 'fab-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'fab', roleId: 'dept-manager-plmb' },
  },
  {
    id: 'ops-fab-dept-hvac',
    label: 'Shop Dept Manager (HVAC)',
    stageId: 'fab-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'fab', roleId: 'dept-manager-hvac' },
  },
  {
    id: 'ops-fab-wh-worker',
    label: 'Warehouse Worker',
    stageId: 'fab-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'fab', roleId: 'warehouse-worker' },
  },
  {
    id: 'ops-fab-worker',
    label: 'Fab Worker',
    stageId: 'fab-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'fab', roleId: 'worker' },
  },
  {
    id: 'ops-ship-lead',
    label: 'Shipping Manager',
    stageId: 'shipping-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'shipping', roleId: 'shipping-manager' },
  },
  {
    id: 'ops-ship-worker',
    label: 'Shipping Worker',
    stageId: 'shipping-ops',
    orgCategory: 'operations-staff',
    opsPlacement: { dashboard: 'shipping', roleId: 'worker' },
  },
];

export function jobTitleGroup(stageId: EmployeeStageId): JobTitleGroup {
  if (stageId === 'detailers') return 'Detailers';
  if (stageId === 'leadership' || stageId === 'support') return 'Office';
  return 'Operations';
}

export function resolveOrgCategoryForTitle(title: EmployeeJobTitleDef): OrgCategory {
  if (title.orgCategory) return title.orgCategory;
  if (title.opsPlacement) return 'operations-staff';
  return 'jr-detailer';
}

export function jobTitleToOption(title: EmployeeJobTitleDef): EmployeeJobOption {
  return {
    id: title.id,
    label: title.label,
    group: jobTitleGroup(title.stageId),
    orgCategory: resolveOrgCategoryForTitle(title),
    opsPlacement: title.opsPlacement,
  };
}

/** @deprecated Use DEFAULT_EMPLOYEE_JOB_TITLES / employeeJobTitles from store. */
export const EMPLOYEE_JOB_OPTIONS: EmployeeJobOption[] = DEFAULT_EMPLOYEE_JOB_TITLES.map(
  jobTitleToOption
);

export function createDefaultEmployeeJobTitles(): EmployeeJobTitleDef[] {
  return DEFAULT_EMPLOYEE_JOB_TITLES.map((title) => ({
    ...title,
    opsPlacement: title.opsPlacement ? { ...title.opsPlacement } : undefined,
  }));
}

export function normalizeEmployeeJobTitles(
  value: EmployeeJobTitleDef[] | null | undefined
): EmployeeJobTitleDef[] {
  if (!Array.isArray(value) || value.length === 0) {
    return createDefaultEmployeeJobTitles();
  }

  const normalized: EmployeeJobTitleDef[] = [];
  const seen = new Set<string>();

  for (const entry of value) {
    if (!entry || typeof entry.id !== 'string' || !entry.id.trim()) continue;
    if (seen.has(entry.id)) continue;
    const label = typeof entry.label === 'string' ? entry.label.trim() : '';
    if (!label) continue;
    const stageId = EMPLOYEE_STAGES.some((stage) => stage.id === entry.stageId)
      ? entry.stageId
      : inferStageFromLegacy(entry);
    const repaired = repairJobTitlePlacement({
      id: entry.id,
      label,
      stageId,
      orgCategory: entry.orgCategory,
      opsPlacement: entry.opsPlacement
        ? { dashboard: entry.opsPlacement.dashboard, roleId: entry.opsPlacement.roleId }
        : undefined,
    });
    seen.add(repaired.id);
    normalized.push(repaired);
  }

  return normalized.length > 0 ? normalized : createDefaultEmployeeJobTitles();
}

function inferStageFromLegacy(entry: Partial<EmployeeJobTitleDef>): EmployeeStageId {
  if (entry.orgCategory === 'bim-manager' || entry.orgCategory === 'owner') return 'leadership';
  if (entry.orgCategory === 'support-manager' || entry.orgCategory === 'support-specialist') {
    return 'support';
  }
  if (entry.orgCategory === 'operations-manager') return 'pm-ops';
  if (entry.opsPlacement?.dashboard === 'pm') return 'pm-ops';
  if (entry.opsPlacement?.dashboard === 'field') return 'field-ops';
  if (entry.opsPlacement?.dashboard === 'fab') return 'fab-ops';
  if (entry.opsPlacement?.dashboard === 'shipping') return 'shipping-ops';
  if (
    entry.orgCategory === 'plumbing-detailer' ||
    entry.orgCategory === 'mechanical-detailer' ||
    entry.orgCategory === 'sheet-metal-detailer' ||
    entry.orgCategory === 'jr-detailer'
  ) {
    return 'detailers';
  }
  return 'detailers';
}

/** Ensure stage, orgCategory, and opsPlacement agree. */
export function repairJobTitlePlacement(title: EmployeeJobTitleDef): EmployeeJobTitleDef {
  const stage = EMPLOYEE_STAGES.find((entry) => entry.id === title.stageId);
  if (!stage) {
    return { ...title, stageId: 'detailers', orgCategory: 'jr-detailer', opsPlacement: undefined };
  }

  if (stage.id === 'leadership') {
    return {
      ...title,
      orgCategory: title.orgCategory === 'operations-manager' ? 'operations-manager' : 'bim-manager',
      opsPlacement: undefined,
    };
  }

  if (stage.id === 'detailers') {
    const valid = DETAILER_ORG_CATEGORIES.some((category) => category.id === title.orgCategory);
    return {
      ...title,
      orgCategory: valid ? title.orgCategory : 'jr-detailer',
      opsPlacement: undefined,
    };
  }

  if (stage.id === 'support') {
    return {
      ...title,
      orgCategory:
        title.orgCategory === 'support-manager' ? 'support-manager' : 'support-specialist',
      opsPlacement: undefined,
    };
  }

  // Operations stages
  if (title.orgCategory === 'operations-manager' && stage.id === 'pm-ops') {
    return { ...title, orgCategory: 'operations-manager', opsPlacement: undefined };
  }

  const dashboard = stage.dashboard!;
  const roles = dashboardRolesFor(dashboard);
  const roleId =
    title.opsPlacement?.dashboard === dashboard &&
    roles.some((role) => role.id === title.opsPlacement?.roleId)
      ? title.opsPlacement!.roleId
      : roles[0]?.id;

  return {
    ...title,
    orgCategory: 'operations-staff',
    opsPlacement: roleId ? { dashboard, roleId } : undefined,
  };
}

export function findEmployeeJobTitle(
  titles: EmployeeJobTitleDef[],
  id: string
): EmployeeJobTitleDef | undefined {
  return titles.find((title) => title.id === id);
}

/** @deprecated */
export function findEmployeeJobOption(id: string): EmployeeJobOption | undefined {
  return EMPLOYEE_JOB_OPTIONS.find((option) => option.id === id);
}

export function officeSubtypeOptions(stageId: EmployeeStageId): { id: OrgCategory; label: string }[] {
  if (stageId === 'leadership') {
    return [
      { id: 'bim-manager', label: 'BIM Manager' },
      { id: 'operations-manager', label: 'Operations Manager' },
    ];
  }
  if (stageId === 'detailers') {
    return DETAILER_ORG_CATEGORIES.map((category) => ({
      id: category.id,
      label: category.label,
    }));
  }
  if (stageId === 'support') {
    return [
      { id: 'support-manager', label: 'Support Manager' },
      { id: 'support-specialist', label: 'Support Specialist' },
    ];
  }
  if (stageId === 'pm-ops') {
    return [{ id: 'operations-manager', label: 'Operations Manager' }];
  }
  return [];
}

export function opsRoleSlotOptions(
  stageId: EmployeeStageId
): { id: string; label: string; dashboard: DashboardType }[] {
  const stage = EMPLOYEE_STAGES.find((entry) => entry.id === stageId);
  if (!stage?.dashboard) return [];
  return dashboardRolesFor(stage.dashboard).map((role) => ({
    id: role.id,
    label: role.label,
    dashboard: stage.dashboard!,
  }));
}

export function stageNeedsOpsSlot(stageId: EmployeeStageId): boolean {
  return (
    stageId === 'pm-ops' ||
    stageId === 'field-ops' ||
    stageId === 'fab-ops' ||
    stageId === 'shipping-ops'
  );
}

export function stageNeedsOfficeSubtype(stageId: EmployeeStageId): boolean {
  return stageId === 'leadership' || stageId === 'detailers' || stageId === 'support';
}

/** Access Control row for permissions defaults. */
export function accessControlRowIdForJobTitle(title: EmployeeJobTitleDef): string {
  const category = resolveOrgCategoryForTitle(title);
  if (
    category === 'plumbing-detailer' ||
    category === 'mechanical-detailer' ||
    category === 'sheet-metal-detailer' ||
    category === 'jr-detailer'
  ) {
    return 'detailer';
  }
  if (category !== 'operations-staff') return category;

  const placement = title.opsPlacement;
  if (!placement) return 'ops-unassigned';

  const isLead = OPS_LEAD_ROLE_IDS[placement.dashboard]?.has(placement.roleId);
  if (placement.dashboard === 'pm') return isLead ? 'ops-pm-lead' : 'ops-pm-staff';
  if (placement.dashboard === 'field') return isLead ? 'ops-field-lead' : 'ops-field-staff';
  if (placement.dashboard === 'fab') return isLead ? 'ops-fab-lead' : 'ops-fab-staff';
  if (placement.dashboard === 'shipping') return isLead ? 'ops-shipping-lead' : 'ops-shipping-staff';
  return 'ops-unassigned';
}

/** Resolve which job-title id matches this employee’s current placement. */
export function jobTitleIdForEmployee(
  employee: Employee,
  assignments: DashboardAssignments | null | undefined,
  titles: EmployeeJobTitleDef[] = DEFAULT_EMPLOYEE_JOB_TITLES
): string {
  if (employee.jobTitleId) {
    const existing = findEmployeeJobTitle(titles, employee.jobTitleId);
    if (existing) return existing.id;
  }

  const category = inferOrgCategory(employee);

  if (category !== 'operations-staff') {
    const match = titles.find(
      (title) => resolveOrgCategoryForTitle(title) === category && !title.opsPlacement
    );
    return match?.id ?? category;
  }

  if (assignments) {
    for (const title of titles) {
      if (!title.opsPlacement) continue;
      const { dashboard, roleId } = title.opsPlacement;
      const ids = (assignments[dashboard] as Record<string, string[] | undefined>)[roleId] ?? [];
      if (ids.includes(employee.id)) return title.id;
    }
  }

  const fallback = titles.find((title) => title.id === 'ops-fab-worker');
  return fallback?.id ?? titles[0]?.id ?? 'ops-fab-worker';
}

/** @deprecated */
export function jobOptionIdForEmployee(
  employee: Employee,
  assignments: DashboardAssignments | null | undefined
): string {
  return jobTitleIdForEmployee(employee, assignments, DEFAULT_EMPLOYEE_JOB_TITLES);
}

export function stageForJobTitle(title: EmployeeJobTitleDef): EmployeeStageId {
  return title.stageId;
}

/** @deprecated */
export function stageForJobOption(option: EmployeeJobOption): EmployeeStageId {
  return stageForJobTitle({
    id: option.id,
    label: option.label,
    stageId: inferStageFromLegacy(option),
    orgCategory: option.orgCategory,
    opsPlacement: option.opsPlacement,
  });
}

export function backfillEmployeeJobTitleIds(
  employees: Employee[],
  assignments: DashboardAssignments | null | undefined,
  titles: EmployeeJobTitleDef[]
): Employee[] {
  return employees.map((employee) => {
    if (employee.jobTitleId && findEmployeeJobTitle(titles, employee.jobTitleId)) {
      return employee;
    }
    return {
      ...employee,
      jobTitleId: jobTitleIdForEmployee(employee, assignments, titles),
    };
  });
}

export function removeEmployeeFromAllDashboardRoles(
  assignments: DashboardAssignments,
  employeeId: string
): DashboardAssignments {
  const next: DashboardAssignments = {
    pm: { ...assignments.pm },
    field: { ...assignments.field },
    fab: { ...assignments.fab },
    shipping: { ...assignments.shipping },
  };

  for (const dashboard of ['pm', 'field', 'fab', 'shipping'] as const) {
    const board = { ...(next[dashboard] as Record<string, string[]>) };
    for (const roleId of Object.keys(board)) {
      board[roleId] = (board[roleId] ?? []).filter((id) => id !== employeeId);
    }
    (next as unknown as Record<string, Record<string, string[]>>)[dashboard] = board;
  }

  return next;
}
