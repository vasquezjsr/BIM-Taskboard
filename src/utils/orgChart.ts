import type { AppPermission, Employee, OrgCategory, OrgTeam } from '../types';
import { ORG_CATEGORIES, DEFAULT_ORG_TEAM_IDS } from '../types';
import { operationsDashboardPermissions } from '../data/departmentStaff';
import { JOE_VASQUEZ_ID } from './permissions';

export type EmployeePermissionsMap = Record<string, AppPermission[]>;

/** Employee id → manager employee ids (may be multiple) */
export type EmployeeReportsToMap = Record<string, string[]>;

export const REPORTS_UNDER_PREFIX = 'reports-under-';

export const ORG_SLOT_PREFIX = 'org-slot-';

/** Half of a card column (110px). Cards span 2 half-slots and may start on any half index. */
export const ORG_CARD_HALF_SPAN = 2;
export const ORG_HALF_SLOT_WIDTH = 110;
export const ORG_CARD_WIDTH = 220;

/** Depth (string) → employee id → body half-slot start index (no padding) */
export type OrgChartLevelSlotsMap = Record<string, Record<string, number>>;

export const DEFAULT_LEADING_HALF_PHANTOMS = 8;
export const DEFAULT_TRAILING_HALF_PHANTOMS = 12;
export const MIN_ORG_CHART_ROWS = 2;
export const MIN_TRAILING_SNAP_CARD_SLOTS = 2;

export const PERMISSION_LABELS: Record<AppPermission, string> = {
  'edit-budget-hours': 'Edit budget hours',
  'manage-org': 'Manage org & permissions',
  'manage-columns': 'Manage & restore columns',
  'edit-pm-assigns': 'Edit PM assigns',
  'assign-fab-leads': 'Assign fab leads',
  'assign-fab-workers': 'Assign fab workers',
  'edit-fab-status': 'Edit fab status',
  'fab-clock': 'Fab clock',
  'edit-weld-log': 'Edit weld log',
  'edit-fab-collab': 'Fab package notes',
  'log-time': 'Log time',
  'delete-time': 'Delete time',
  'edit-clients-projects': 'Edit clients & projects',
  'edit-tasks': 'Edit tasks',
  'assign-tasks': 'Assign tasks',
  'manage-statuses': 'Manage statuses',
  'add-columns': 'Add columns',
  'view-activity-log': 'View activity log',
  'view-org-chart': 'View org chart',
  'view-owner-dashboard': 'View Owner dashboard',
  'view-pm-dashboard': 'View PM dashboard',
  'view-field-dashboard': 'View Field dashboard',
  'view-fab-dashboard': 'View Shop dashboard',
  'view-shipping-dashboard': 'View Shipping dashboard',
  'view-weld-log-dashboard': 'View Weld Log Dashboard',
  'view-spooling-dashboard': 'View Spooling Dashboard',
  'view-visibility-dashboard': 'View Access Control',
  'view-time-tracking': 'View Time Tracking',
};

export const ALL_PERMISSIONS: AppPermission[] = [
  'edit-budget-hours',
  'manage-org',
  'manage-columns',
  'edit-pm-assigns',
  'assign-fab-leads',
  'assign-fab-workers',
  'edit-fab-status',
  'fab-clock',
  'edit-weld-log',
  'edit-fab-collab',
  'log-time',
  'delete-time',
  'edit-clients-projects',
  'edit-tasks',
  'assign-tasks',
  'manage-statuses',
  'add-columns',
  'view-activity-log',
  'view-org-chart',
  'view-owner-dashboard',
  'view-pm-dashboard',
  'view-field-dashboard',
  'view-fab-dashboard',
  'view-shipping-dashboard',
  'view-weld-log-dashboard',
  'view-spooling-dashboard',
  'view-visibility-dashboard',
  'view-time-tracking',
];

/** Live-roster edit capabilities (not tab visibility). */
export const DASHBOARD_EDIT_PERMISSIONS: AppPermission[] = [
  'edit-pm-assigns',
  'assign-fab-leads',
  'assign-fab-workers',
  'edit-fab-status',
  'fab-clock',
  'edit-weld-log',
  'edit-fab-collab',
  'log-time',
  'delete-time',
  'edit-clients-projects',
  'edit-tasks',
  'assign-tasks',
  'manage-statuses',
  'add-columns',
];

/** Default edit chips by org category (additive; callers merge into existing sets). */
export function defaultDashboardEditPermissionsForCategory(
  category: OrgCategory
): AppPermission[] {
  if (category === 'owner' || category === 'bim-manager') {
    return [...DASHBOARD_EDIT_PERMISSIONS];
  }
  if (category === 'operations-manager') {
    return [
      'edit-pm-assigns',
      'assign-fab-leads',
      'assign-fab-workers',
      'edit-fab-status',
      'fab-clock',
      'edit-weld-log',
      'edit-fab-collab',
      'log-time',
      'delete-time',
      'edit-clients-projects',
      'edit-tasks',
      'assign-tasks',
      'manage-statuses',
      'add-columns',
    ];
  }
  if (
    category === 'plumbing-detailer' ||
    category === 'mechanical-detailer' ||
    category === 'sheet-metal-detailer' ||
    category === 'jr-detailer'
  ) {
    return ['log-time', 'edit-tasks', 'assign-tasks'];
  }
  if (category === 'operations-staff') {
    return [
      'assign-fab-workers',
      'edit-fab-status',
      'fab-clock',
      'edit-weld-log',
      'edit-fab-collab',
      'log-time',
    ];
  }
  if (category === 'support-manager' || category === 'support-specialist') {
    return ['log-time', 'edit-clients-projects', 'edit-tasks', 'assign-tasks'];
  }
  return ['log-time'];
}

/** Job levels that get Visibility Dashboard access by default (editable in-app). */
export const DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS: OrgCategory[] = [
  'owner',
  'bim-manager',
  'operations-manager',
];

const ORG_CATEGORY_LABELS = Object.fromEntries(
  ORG_CATEGORIES.map((category) => [category.id, category.label])
) as Record<OrgCategory, string>;

const ORG_CATEGORY_TO_TEAM_ID = Object.fromEntries(
  ORG_CATEGORIES.map((category) => [category.id, category.teamId])
) as Record<OrgCategory, string>;

const TEAM_ID_TO_ORG_CATEGORY = Object.fromEntries(
  ORG_CATEGORIES.map((category) => [category.teamId, category.id])
) as Record<string, OrgCategory>;

export function orgCategoryToTeamId(category: OrgCategory): string {
  return ORG_CATEGORY_TO_TEAM_ID[category];
}

export function teamIdToOrgCategory(teamId: string): OrgCategory | null {
  return TEAM_ID_TO_ORG_CATEGORY[teamId] ?? null;
}

export function inferOrgCategory(employee: Employee): OrgCategory {
  if (employee.orgCategory) return employee.orgCategory;
  if (employee.role === 'operations') return 'operations-staff';
  if (employee.role === 'support-specialist') return 'support-specialist';
  return 'plumbing-detailer';
}

export function orgCategoryLabel(category: OrgCategory): string {
  return ORG_CATEGORY_LABELS[category];
}

export function employeeOrgLabel(employee: Employee): string {
  return orgCategoryLabel(inferOrgCategory(employee));
}

export function isProtectedOrgTeamId(teamId: string): boolean {
  return DEFAULT_ORG_TEAM_IDS.has(teamId);
}

function categoryTeamDisplayName(category: OrgCategory): string {
  switch (category) {
    case 'owner':
      return 'Owners';
    case 'bim-manager':
      return 'BIM Management';
    case 'operations-manager':
      return 'Operations Management';
    case 'operations-staff':
      return 'Operations';
    case 'plumbing-detailer':
      return 'Plumbing Detailers';
    case 'mechanical-detailer':
      return 'Mechanical Detailers';
    case 'sheet-metal-detailer':
      return 'Sheet Metal Detailers';
    case 'jr-detailer':
      return 'Junior Detailers';
    case 'support-manager':
    case 'support-specialist':
      return 'Support Specialists';
    default:
      return 'Team';
  }
}

export function createEmptyOrgTeams(): OrgTeam[] {
  return ORG_CATEGORIES.map((category, index) => ({
    id: category.teamId,
    name: categoryTeamDisplayName(category.id),
    memberIds: [],
    sortOrder: index,
  }));
}

export function createDefaultOrgTeams(employees: Employee[]): OrgTeam[] {
  const teams = createEmptyOrgTeams();
  const teamById = new Map(teams.map((team) => [team.id, team]));

  for (const employee of employees) {
    const teamId = orgCategoryToTeamId(inferOrgCategory(employee));
    teamById.get(teamId)?.memberIds.push(employee.id);
  }

  return teams;
}

export function migrateOrgTeamsToCategories(
  existingTeams: OrgTeam[] | undefined,
  employees: Employee[]
): OrgTeam[] {
  const teams = createEmptyOrgTeams();
  const teamById = new Map(teams.map((team) => [team.id, team]));
  const assigned = new Set<string>();

  const assignToTeam = (employeeId: string, teamId: string) => {
    if (assigned.has(employeeId)) return;
    teamById.get(teamId)?.memberIds.push(employeeId);
    assigned.add(employeeId);
  };

  for (const team of existingTeams ?? []) {
    if (!DEFAULT_ORG_TEAM_IDS.has(team.id)) continue;
    for (const employeeId of team.memberIds) {
      assignToTeam(employeeId, team.id);
    }
  }

  const legacyDetailers =
    existingTeams?.find((team) => team.id === 'team-detailers')?.memberIds ?? [];
  const legacySupport =
    existingTeams?.find((team) => team.id === 'team-support')?.memberIds ?? [];

  for (const employeeId of legacySupport) {
    assignToTeam(employeeId, orgCategoryToTeamId('support-specialist'));
  }

  for (const employeeId of legacyDetailers) {
    const employee = employees.find((entry) => entry.id === employeeId);
    assignToTeam(
      employeeId,
      orgCategoryToTeamId(employee ? inferOrgCategory(employee) : 'plumbing-detailer')
    );
  }

  for (const employee of employees) {
    if (assigned.has(employee.id)) continue;
    assignToTeam(employee.id, orgCategoryToTeamId(inferOrgCategory(employee)));
  }

  return teams;
}

export function createDefaultEmployeePermissions(employees: Employee[]): EmployeePermissionsMap {
  const map: EmployeePermissionsMap = {};

  for (const employee of employees) {
    const permissions: AppPermission[] = ['view-org-chart', 'view-time-tracking'];

    if (employee.role === 'detailer') {
      permissions.push('edit-budget-hours');
    }

    if (inferOrgCategory(employee) === 'owner') {
      permissions.push(
        'edit-budget-hours',
        'manage-org',
        'manage-columns',
        'view-activity-log',
        'view-owner-dashboard',
        'view-pm-dashboard',
        'view-field-dashboard',
        'view-fab-dashboard',
        'view-shipping-dashboard',
        'view-weld-log-dashboard',
        'view-spooling-dashboard',
        'view-visibility-dashboard',
        ...defaultDashboardEditPermissionsForCategory('owner')
      );
    }

    if (inferOrgCategory(employee) === 'support-manager') {
      permissions.push(
        'view-pm-dashboard',
        'view-field-dashboard',
        'view-fab-dashboard',
        'view-shipping-dashboard',
        'view-weld-log-dashboard',
        'view-spooling-dashboard',
        ...defaultDashboardEditPermissionsForCategory('support-manager')
      );
    }

    if (inferOrgCategory(employee) === 'bim-manager') {
      permissions.push(
        'edit-budget-hours',
        'manage-org',
        'manage-columns',
        'view-activity-log',
        'view-org-chart',
        'view-pm-dashboard',
        'view-field-dashboard',
        'view-fab-dashboard',
        'view-shipping-dashboard',
        'view-weld-log-dashboard',
        'view-spooling-dashboard',
        'view-visibility-dashboard',
        ...defaultDashboardEditPermissionsForCategory('bim-manager')
      );
    }

    if (inferOrgCategory(employee) === 'operations-manager') {
      permissions.push(
        'manage-org',
        'manage-columns',
        'view-activity-log',
        'view-pm-dashboard',
        'view-field-dashboard',
        'view-fab-dashboard',
        'view-shipping-dashboard',
        'view-weld-log-dashboard',
        'view-spooling-dashboard',
        'view-visibility-dashboard',
        ...defaultDashboardEditPermissionsForCategory('operations-manager')
      );
    }

    if (employee.role === 'detailer') {
      permissions.push(...defaultDashboardEditPermissionsForCategory(inferOrgCategory(employee)));
    }

    if (employee.role === 'operations') {
      permissions.push(...operationsDashboardPermissions(employee.id));
      permissions.push(...defaultDashboardEditPermissionsForCategory('operations-staff'));
    }

    if (employee.role === 'support-specialist' && inferOrgCategory(employee) === 'support-specialist') {
      permissions.push(
        'view-spooling-dashboard',
        ...defaultDashboardEditPermissionsForCategory('support-specialist')
      );
    }

    if (employee.id === JOE_VASQUEZ_ID) {
      permissions.push('edit-budget-hours', 'manage-org', ...DASHBOARD_EDIT_PERMISSIONS);
    }

    map[employee.id] = [...new Set(permissions)];
  }

  return map;
}

export function getUnassignedEmployeeIds(employees: Employee[], orgTeams: OrgTeam[]): string[] {
  const assigned = new Set(orgTeams.flatMap((team) => team.memberIds));
  return employees.filter((employee) => !assigned.has(employee.id)).map((employee) => employee.id);
}

export function employeeNameById(employees: Employee[], id: string): string {
  return employees.find((employee) => employee.id === id)?.name ?? 'Unknown';
}

export function getEmployeeManagers(
  employeeId: string,
  memberIds: string[],
  reportsTo: EmployeeReportsToMap
): string[] {
  return (reportsTo[employeeId] ?? []).filter((managerId) => memberIds.includes(managerId));
}

export function getTeamRoots(memberIds: string[], reportsTo: EmployeeReportsToMap): string[] {
  return memberIds.filter((id) => getEmployeeManagers(id, memberIds, reportsTo).length === 0);
}

export function getDirectReports(
  managerId: string,
  memberIds: string[],
  reportsTo: EmployeeReportsToMap
): string[] {
  return memberIds.filter((id) => (reportsTo[id] ?? []).includes(managerId));
}

/** True when managerId appears anywhere in employeeId's reporting chain (direct or indirect). */
export function isUpstreamManagerOf(
  managerId: string,
  employeeId: string,
  memberIds: string[],
  reportsTo: EmployeeReportsToMap
): boolean {
  if (managerId === employeeId) return false;

  const visited = new Set<string>();
  const queue = [...getEmployeeManagers(employeeId, memberIds, reportsTo)];

  while (queue.length > 0) {
    const current = queue.shift()!;
    if (current === managerId) return true;
    if (visited.has(current)) continue;
    visited.add(current);
    queue.push(...getEmployeeManagers(current, memberIds, reportsTo));
  }

  return false;
}

export function isOrgOwner(employee: Employee | undefined): boolean {
  return employee ? inferOrgCategory(employee) === 'owner' : false;
}

export function isValidReportingManager(
  employeeId: string,
  managerId: string,
  employees: Employee[]
): boolean {
  if (managerId === employeeId) return false;
  return employees.some((entry) => entry.id === managerId);
}

export interface OrgChartLevel {
  depth: number;
  employeeIds: string[];
}

export function computeEmployeeDepth(
  employeeId: string,
  memberIds: string[],
  reportsTo: EmployeeReportsToMap,
  memo: Map<string, number> = new Map()
): number {
  if (memo.has(employeeId)) return memo.get(employeeId)!;

  const managers = getEmployeeManagers(employeeId, memberIds, reportsTo);
  if (managers.length === 0) {
    memo.set(employeeId, 0);
    return 0;
  }

  const depth = 1 + Math.max(...managers.map((m) => computeEmployeeDepth(m, memberIds, reportsTo, memo)));
  memo.set(employeeId, depth);
  return depth;
}

function averageManagerIndex(
  employeeId: string,
  memberIds: string[],
  reportsTo: EmployeeReportsToMap,
  previousLevelOrder: string[]
): number {
  const managers = getEmployeeManagers(employeeId, memberIds, reportsTo);
  if (managers.length === 0) return previousLevelOrder.indexOf(employeeId);
  const indices = managers
    .map((id) => previousLevelOrder.indexOf(id))
    .filter((index) => index >= 0);
  if (indices.length === 0) return 0;
  return indices.reduce((sum, index) => sum + index, 0) / indices.length;
}

export function buildOrgChartLevels(
  memberIds: string[],
  reportsTo: EmployeeReportsToMap
): OrgChartLevel[] {
  if (memberIds.length === 0) return [];

  const depthMemo = new Map<string, number>();
  const byDepth = new Map<number, string[]>();

  for (const id of memberIds) {
    const depth = computeEmployeeDepth(id, memberIds, reportsTo, depthMemo);
    if (!byDepth.has(depth)) byDepth.set(depth, []);
    byDepth.get(depth)!.push(id);
  }

  const levels: OrgChartLevel[] = [];
  let previousOrder: string[] = [];

  for (const depth of [...byDepth.keys()].sort((a, b) => a - b)) {
    const ids = byDepth.get(depth)!;
    const sorted =
      depth === 0
        ? ids
        : [...ids].sort(
            (a, b) =>
              averageManagerIndex(a, memberIds, reportsTo, previousOrder) -
              averageManagerIndex(b, memberIds, reportsTo, previousOrder)
          );
    levels.push({ depth, employeeIds: sorted });
    previousOrder = sorted;
  }

  return levels;
}

export function wouldCreateReportingCycle(
  employeeId: string,
  managerId: string,
  reportsTo: EmployeeReportsToMap
): boolean {
  if (managerId === employeeId) return true;

  const visited = new Set<string>();
  const queue = [managerId];

  while (queue.length > 0) {
    const current = queue.shift()!;
    if (current === employeeId) return true;
    if (visited.has(current)) continue;
    visited.add(current);

    for (const upstream of reportsTo[current] ?? []) {
      queue.push(upstream);
    }
  }

  return false;
}

export function normalizeEmployeeReportsTo(
  raw: EmployeeReportsToMap | Record<string, string | null | string[] | undefined> | undefined
): EmployeeReportsToMap {
  if (!raw) return {};
  const normalized: EmployeeReportsToMap = {};

  for (const [employeeId, value] of Object.entries(raw)) {
    if (Array.isArray(value)) {
      normalized[employeeId] = [...new Set(value.filter(Boolean))];
    } else if (typeof value === 'string' && value) {
      normalized[employeeId] = [value];
    } else {
      normalized[employeeId] = [];
    }
  }

  return normalized;
}

export function reportsUnderDropId(employeeId: string): string {
  return `${REPORTS_UNDER_PREFIX}${employeeId}`;
}

export function parseReportsUnderDropId(dropId: string): string | null {
  return dropId.startsWith(REPORTS_UNDER_PREFIX) ? dropId.slice(REPORTS_UNDER_PREFIX.length) : null;
}

export interface RectLike {
  top: number;
  left: number;
  right: number;
  bottom: number;
  width: number;
  height: number;
}

/** When a dragged card substantially overlaps another card, treat it as a "reports to" drop. */
export function findReportsUnderDropFromCardOverlap(
  activeId: string,
  collisionRect: RectLike | null,
  droppableEntries: { id: string; rect: RectLike | null | undefined }[],
  minOverlapRatio = 0.3
): string | null {
  if (!collisionRect) return null;

  const activeArea = collisionRect.width * collisionRect.height;
  if (activeArea <= 0) return null;

  let bestDropId: string | null = null;
  let bestRatio = minOverlapRatio;

  for (const entry of droppableEntries) {
    const managerId = parseReportsUnderDropId(entry.id);
    if (!managerId || managerId === activeId || !entry.rect) continue;

    const overlapLeft = Math.max(collisionRect.left, entry.rect.left);
    const overlapRight = Math.min(collisionRect.right, entry.rect.right);
    const overlapTop = Math.max(collisionRect.top, entry.rect.top);
    const overlapBottom = Math.min(collisionRect.bottom, entry.rect.bottom);
    if (overlapLeft >= overlapRight || overlapTop >= overlapBottom) continue;

    const overlapArea = (overlapRight - overlapLeft) * (overlapBottom - overlapTop);
    const ratio = overlapArea / activeArea;
    if (ratio > bestRatio) {
      bestRatio = ratio;
      bestDropId = entry.id;
    }
  }

  return bestDropId;
}

export function orgSlotDropId(depth: number, halfSlotIndex: number): string {
  return `${ORG_SLOT_PREFIX}${depth}:${halfSlotIndex}`;
}

export function parseOrgSlotDropId(dropId: string): { depth: number; halfSlotIndex: number } | null {
  if (!dropId.startsWith(ORG_SLOT_PREFIX)) return null;
  const rest = dropId.slice(ORG_SLOT_PREFIX.length);

  const colonIndex = rest.indexOf(':');
  if (colonIndex !== -1) {
    const depth = Number(rest.slice(0, colonIndex));
    const halfSlotIndex = Number(rest.slice(colonIndex + 1));
    if (!Number.isFinite(depth) || !Number.isFinite(halfSlotIndex)) return null;
    return { depth, halfSlotIndex };
  }

  // Legacy ids: org-slot-{depth}-{index} (non-negative index only)
  const legacyMatch = /^(\d+)-(\d+)$/.exec(rest);
  if (!legacyMatch) return null;
  const depth = Number(legacyMatch[1]);
  const halfSlotIndex = Number(legacyMatch[2]);
  if (!Number.isFinite(depth) || !Number.isFinite(halfSlotIndex)) return null;
  return { depth, halfSlotIndex };
}

export function halfSlotLeftPx(halfSlotIndex: number): number {
  if (halfSlotIndex >= 0) {
    return halfSlotIndex * 110 + Math.floor(halfSlotIndex / 2) * 20;
  }
  const steps = Math.abs(halfSlotIndex);
  return -(steps * 110 + Math.floor(steps / 2) * 20);
}

export function canPlaceCardAtBodyIndex(
  positions: Record<string, number>,
  bodyIndex: number,
  employeeId: string
): boolean {
  for (let half = bodyIndex; half < bodyIndex + ORG_CARD_HALF_SPAN; half += 1) {
    for (const [id, start] of Object.entries(positions)) {
      if (id === employeeId) continue;
      if (half >= start && half < start + ORG_CARD_HALF_SPAN) return false;
    }
  }
  return true;
}

export function collectPhantomBodyStartsInRange(
  rangeStart: number,
  rangeEnd: number,
  positions: Record<string, number>,
  activeDragId: string | null
): number[] {
  const starts: number[] = [];
  const excludeId = activeDragId ?? '';

  for (let bodyIndex = rangeStart; bodyIndex <= rangeEnd; bodyIndex += 1) {
    if (canPlaceCardAtBodyIndex(positions, bodyIndex, excludeId)) {
      starts.push(bodyIndex);
    }
  }

  return starts;
}

export function collectPhantomBodyStarts(
  positions: Record<string, number>,
  activeDragId: string | null,
  options: { minTrailingCardSlots?: number } = {}
): number[] {
  const minTrailingCardSlots = options.minTrailingCardSlots ?? 0;
  let minIndex = 0;
  let maxIndex = 0;
  for (const start of Object.values(positions)) {
    minIndex = Math.min(minIndex, start);
    maxIndex = Math.max(maxIndex, start);
  }

  const rangeStart = minIndex - DEFAULT_LEADING_HALF_PHANTOMS;
  const rangeEnd = Math.max(
    maxIndex + DEFAULT_TRAILING_HALF_PHANTOMS,
    maxIndex + minTrailingCardSlots * ORG_CARD_HALF_SPAN
  );

  return collectPhantomBodyStartsInRange(rangeStart, rangeEnd, positions, activeDragId);
}

export function rowHasAvailableSnapSlot(
  positions: Record<string, number>,
  activeDragId: string | null
): boolean {
  return (
    collectPhantomBodyStarts(positions, activeDragId, { minTrailingCardSlots: 1 }).length > 0
  );
}

export function expandOrgChartLevelsForCapacity(
  levels: OrgChartLevel[],
  orgChartLevelSlots: OrgChartLevelSlotsMap,
  activeDragId: string | null,
  minRows: number = MIN_ORG_CHART_ROWS
): OrgChartLevel[] {
  const levelMap = new Map<number, OrgChartLevel>();
  for (const level of levels) {
    levelMap.set(level.depth, level);
  }

  const positionsForDepth = (depth: number): Record<string, number> => {
    const level = levelMap.get(depth);
    if (!level || level.employeeIds.length === 0) return {};
    return resolveOrgChartCardPositions(level.employeeIds, orgChartLevelSlots[String(depth)]);
  };

  let maxDepth = Math.max(minRows - 1, ...Array.from(levelMap.keys()), -1);

  const deepestPopulatedDepth = Math.max(
    -1,
    ...Array.from(levelMap.values())
      .filter((level) => level.employeeIds.length > 0)
      .map((level) => level.depth)
  );

  // Always show minRows; keep one empty snap-in row below the deepest populated row.
  maxDepth = Math.max(maxDepth, minRows - 1, deepestPopulatedDepth + 1);

  for (let depth = 0; depth <= maxDepth; depth += 1) {
    if (!levelMap.has(depth)) {
      levelMap.set(depth, { depth, employeeIds: [] });
    }
  }

  let expanded = true;
  while (expanded) {
    expanded = false;
    for (let depth = 0; depth <= maxDepth; depth += 1) {
      const level = levelMap.get(depth);
      const employeeCount = level?.employeeIds.length ?? 0;
      if (employeeCount === 0) continue;

      const positions = positionsForDepth(depth);
      if (!rowHasAvailableSnapSlot(positions, activeDragId)) {
        const nextDepth = depth + 1;
        if (!levelMap.has(nextDepth)) {
          levelMap.set(nextDepth, { depth: nextDepth, employeeIds: [] });
          maxDepth = nextDepth;
          expanded = true;
        }
      }
    }
  }

  return [...levelMap.values()].sort((a, b) => a.depth - b.depth);
}

export function cardLeftPx(bodyStart: number, chartPaddingLeft: number): number {
  return chartPaddingLeft + halfSlotLeftPx(bodyStart);
}

export function cardCenterX(bodyStart: number, chartPaddingLeft: number): number {
  return cardLeftPx(bodyStart, chartPaddingLeft) + ORG_CARD_WIDTH / 2;
}

export interface OrgChartGridLayout {
  positionsByDepth: Map<number, Record<string, number>>;
  paddingLeft: number;
  width: number;
  phantomStartsByDepth: Map<number, number[]>;
}

export function computeOrgChartGridLayout(
  levels: { depth: number; employeeIds: string[] }[],
  orgChartLevelSlots: OrgChartLevelSlotsMap,
  activeDragId: string | null,
  showPhantoms: boolean
): OrgChartGridLayout {
  const positionsByDepth = new Map<number, Record<string, number>>();
  const phantomStartsByDepth = new Map<number, number[]>();

  for (const level of levels) {
    positionsByDepth.set(
      level.depth,
      level.employeeIds.length > 0
        ? resolveOrgChartCardPositions(
            level.employeeIds,
            orgChartLevelSlots[String(level.depth)]
          )
        : {}
    );
  }

  let minLeft = 0;
  let maxRight = 0;

  const considerStart = (start: number) => {
    minLeft = Math.min(minLeft, halfSlotLeftPx(start));
    maxRight = Math.max(maxRight, halfSlotLeftPx(start) + ORG_CARD_WIDTH);
  };

  for (const level of levels) {
    const depth = level.depth;
    const positions = positionsByDepth.get(depth) ?? {};

    for (const start of Object.values(positions)) considerStart(start);

    if (showPhantoms && level.employeeIds.length > 0) {
      const phantoms = collectPhantomBodyStarts(positions, activeDragId, {
        minTrailingCardSlots: MIN_TRAILING_SNAP_CARD_SLOTS,
      });
      phantomStartsByDepth.set(depth, phantoms);
    }
  }

  const paddingLeft = minLeft < 0 ? -minLeft : 0;

  return {
    positionsByDepth,
    paddingLeft,
    width: maxRight - minLeft,
    phantomStartsByDepth,
  };
}

export function levelRowWidthFromHalfSlots(slots: (string | null)[]): number {
  let maxEnd = 0;
  for (let index = 0; index < slots.length; index += 1) {
    const entry = slots[index];
    if (typeof entry === 'string' && entry) {
      maxEnd = Math.max(maxEnd, halfSlotLeftPx(index) + 220);
      index += 1;
    }
  }
  return maxEnd;
}

function halfSlotGridFromPositions(
  positions: Record<string, number>,
  trailingHalfPhantoms = DEFAULT_TRAILING_HALF_PHANTOMS
): (string | null)[] {
  let maxEnd = -1;
  for (const start of Object.values(positions)) {
    maxEnd = Math.max(maxEnd, start + ORG_CARD_HALF_SPAN - 1);
  }
  const length = Math.max(maxEnd + 1, 0) + trailingHalfPhantoms;
  const slots: (string | null)[] = Array.from({ length }, () => null);

  for (const [employeeId, start] of Object.entries(positions)) {
    if (start < 0 || start >= slots.length) continue;
    slots[start] = employeeId;
    if (start + 1 < slots.length) slots[start + 1] = null;
  }

  return slots;
}

function positionsFromHalfSlotGrid(slots: (string | null)[]): Record<string, number> {
  const positions: Record<string, number> = {};
  for (let index = 0; index < slots.length; index += 1) {
    const entry = slots[index];
    if (typeof entry === 'string' && entry) {
      positions[entry] = index;
      index += 1;
    }
  }
  return positions;
}

export function resolveOrgChartCardPositions(
  employeeIds: string[],
  saved: Record<string, number> | (string | null)[] | string[] | undefined
): Record<string, number> {
  const positions: Record<string, number> = {};

  const applySaved = (employeeId: string, start: unknown) => {
    const index = typeof start === 'number' ? start : Number(start);
    if (employeeIds.includes(employeeId) && Number.isFinite(index)) {
      positions[employeeId] = index;
    }
  };

  if (saved && !Array.isArray(saved)) {
    for (const [employeeId, start] of Object.entries(saved)) {
      applySaved(employeeId, start);
    }
  } else if (Array.isArray(saved)) {
    for (const [employeeId, start] of Object.entries(
      positionsFromHalfSlotGrid(saved as (string | null)[])
    )) {
      applySaved(employeeId, start);
    }
  }

  employeeIds.forEach((employeeId, index) => {
    if (!(employeeId in positions)) {
      positions[employeeId] = index * ORG_CARD_HALF_SPAN;
    }
  });

  return positions;
}

export function resolveOrgChartHalfSlots(
  employeeIds: string[],
  saved: Record<string, number> | (string | null)[] | string[] | undefined,
  trailingHalfPhantoms = DEFAULT_TRAILING_HALF_PHANTOMS
): (string | null)[] {
  if (employeeIds.length === 0) return [];
  const positions = resolveOrgChartCardPositions(employeeIds, saved);
  return halfSlotGridFromPositions(positions, trailingHalfPhantoms);
}

export function moveEmployeeToOrgHalfSlot(
  positions: Record<string, number>,
  employeeId: string,
  targetHalfIndex: number
): Record<string, number> {
  if (!canPlaceCardAtBodyIndex(positions, targetHalfIndex, employeeId)) return positions;
  return {
    ...positions,
    [employeeId]: targetHalfIndex,
  };
}

export function resolveOrgChartLevelOrder(
  employeeIds: string[],
  saved: string[] | (string | null)[] | undefined
): string[] {
  const idSet = new Set(employeeIds);
  const ordered: string[] = [];

  if (saved) {
    for (const entry of saved) {
      if (entry && idSet.has(entry) && !ordered.includes(entry)) {
        ordered.push(entry);
      }
    }
  }

  for (const id of employeeIds) {
    if (!ordered.includes(id)) ordered.push(id);
  }

  return ordered;
}

export function removeEmployeeFromOrgChartSlots(
  slotsMap: OrgChartLevelSlotsMap,
  employeeId: string
): OrgChartLevelSlotsMap {
  const next: OrgChartLevelSlotsMap = {};
  for (const [depth, positions] of Object.entries(slotsMap)) {
    const row = { ...positions };
    delete row[employeeId];
    next[depth] = row;
  }
  return next;
}

export function normalizeOrgChartLevelSlots(
  raw: Record<string, Record<string, number> | string[] | (string | null)[]> | undefined
): OrgChartLevelSlotsMap {
  if (!raw) return {};
  const next: OrgChartLevelSlotsMap = {};
  for (const [depth, saved] of Object.entries(raw)) {
    if (Array.isArray(saved)) {
      next[depth] = positionsFromHalfSlotGrid(saved as (string | null)[]);
      continue;
    }
    const positions: Record<string, number> = {};
    for (const [employeeId, start] of Object.entries(saved)) {
      const index = typeof start === 'number' ? start : Number(start);
      if (Number.isFinite(index)) {
        positions[employeeId] = index;
      }
    }
    next[depth] = positions;
  }
  return next;
}

export function getManagerEdges(
  memberIds: string[],
  reportsTo: EmployeeReportsToMap
): { managerId: string; employeeId: string }[] {
  const edges: { managerId: string; employeeId: string }[] = [];
  for (const employeeId of memberIds) {
    for (const managerId of getEmployeeManagers(employeeId, memberIds, reportsTo)) {
      edges.push({ managerId, employeeId });
    }
  }
  return edges;
}

export function defaultOrgCategoryForRole(role: Employee['role']): OrgCategory {
  return role === 'support-specialist' ? 'support-specialist' : 'plumbing-detailer';
}

export function roleForOrgCategory(category: OrgCategory): Employee['role'] {
  if (
    category === 'operations-manager' ||
    category === 'operations-staff'
  ) {
    return 'operations';
  }
  if (
    category === 'support-specialist' ||
    category === 'support-manager' ||
    category === 'bim-manager'
  ) {
    return 'support-specialist';
  }
  return 'detailer';
}

export function canAssignDetailerTrade(employee: Employee): boolean {
  return employee.role === 'detailer' && inferOrgCategory(employee) !== 'owner';
}

export function createDefaultEmployeeReportsTo(): EmployeeReportsToMap {
  return {};
}

/** Extra half-slot spacing between separate root trees at depth 0. */
const ORG_ROOT_TREE_GAP = ORG_CARD_HALF_SPAN * 4;

function primaryManagerForLayout(
  employeeId: string,
  memberIds: string[],
  reportsTo: EmployeeReportsToMap
): string | null {
  const managers = getEmployeeManagers(employeeId, memberIds, reportsTo);
  if (managers.length === 0) return null;
  if (managers.length === 1) return managers[0]!;

  return managers.reduce((deepest, managerId) => {
    const deepestDepth = computeEmployeeDepth(deepest, memberIds, reportsTo);
    const managerDepth = computeEmployeeDepth(managerId, memberIds, reportsTo);
    return managerDepth >= deepestDepth ? managerId : deepest;
  });
}

function buildLayoutChildrenMap(
  memberIds: string[],
  reportsTo: EmployeeReportsToMap
): Map<string, string[]> {
  const childrenOf = new Map<string, string[]>();

  for (const employeeId of memberIds) {
    const managerId = primaryManagerForLayout(employeeId, memberIds, reportsTo);
    if (!managerId) continue;
    if (!childrenOf.has(managerId)) childrenOf.set(managerId, []);
    childrenOf.get(managerId)!.push(employeeId);
  }

  for (const children of childrenOf.values()) {
    children.sort((a, b) => memberIds.indexOf(a) - memberIds.indexOf(b));
  }

  return childrenOf;
}

interface SubtreeLayout {
  positions: Map<string, number>;
  width: number;
}

function layoutSubtree(
  rootId: string,
  childrenOf: Map<string, string[]>
): SubtreeLayout {
  const children = childrenOf.get(rootId) ?? [];

  if (children.length === 0) {
    return {
      positions: new Map([[rootId, 0]]),
      width: ORG_CARD_HALF_SPAN,
    };
  }

  const positions = new Map<string, number>();
  let cursor = 0;

  for (const childId of children) {
    const subtree = layoutSubtree(childId, childrenOf);
    for (const [employeeId, start] of subtree.positions) {
      positions.set(employeeId, start + cursor);
    }
    cursor += subtree.width;
  }

  const totalWidth = cursor;
  const firstChildStart = positions.get(children[0]!) ?? 0;
  const lastChildStart = positions.get(children[children.length - 1]!) ?? 0;
  const rootCenter = (firstChildStart + lastChildStart + ORG_CARD_HALF_SPAN) / 2;
  const rootStart = Math.round(rootCenter - ORG_CARD_HALF_SPAN / 2);
  positions.set(rootId, rootStart);

  return {
    positions,
    width: Math.max(totalWidth, ORG_CARD_HALF_SPAN),
  };
}

function mergeSubtreeLayout(
  target: Map<string, number>,
  layout: SubtreeLayout,
  offset: number
): number {
  for (const [employeeId, start] of layout.positions) {
    target.set(employeeId, start + offset);
  }
  return layout.width;
}

/** Positions each card under its manager(s) on the half-slot grid. */
export function findNearestEmployeeAtDepth(
  depth: number,
  halfSlotIndex: number,
  memberIds: string[],
  reportsTo: EmployeeReportsToMap,
  orgChartLevelSlots: OrgChartLevelSlotsMap
): string | null {
  const level = buildOrgChartLevels(memberIds, reportsTo).find((entry) => entry.depth === depth);
  if (!level || level.employeeIds.length === 0) return null;

  const positions = resolveOrgChartCardPositions(
    level.employeeIds,
    orgChartLevelSlots[String(depth)]
  );

  let bestId: string | null = null;
  let bestDistance = Infinity;

  for (const [employeeId, start] of Object.entries(positions)) {
    const center = start + ORG_CARD_HALF_SPAN / 2;
    const distance = Math.abs(center - halfSlotIndex);
    if (distance < bestDistance) {
      bestDistance = distance;
      bestId = employeeId;
    }
  }

  return bestId;
}

export function managersForOrgChartDepth(
  targetDepth: number,
  halfSlotIndex: number,
  memberIds: string[],
  reportsTo: EmployeeReportsToMap,
  orgChartLevelSlots: OrgChartLevelSlotsMap
): string[] {
  if (targetDepth <= 0) return [];

  const managerId = findNearestEmployeeAtDepth(
    targetDepth - 1,
    halfSlotIndex,
    memberIds,
    reportsTo,
    orgChartLevelSlots
  );

  return managerId ? [managerId] : [];
}

export function createDefaultOrgChartLevelSlots(
  memberIds: string[],
  reportsTo: EmployeeReportsToMap
): OrgChartLevelSlotsMap {
  if (memberIds.length === 0) return {};

  const childrenOf = buildLayoutChildrenMap(memberIds, reportsTo);
  const roots = getTeamRoots(memberIds, reportsTo).sort(
    (a, b) => memberIds.indexOf(a) - memberIds.indexOf(b)
  );

  const allPositions = new Map<string, number>();
  let offset = 0;

  for (const rootId of roots) {
    const layout = layoutSubtree(rootId, childrenOf);
    const width = mergeSubtreeLayout(allPositions, layout, offset);
    offset += width + ORG_ROOT_TREE_GAP;
  }

  const result: OrgChartLevelSlotsMap = {};
  for (const employeeId of memberIds) {
    const start = allPositions.get(employeeId);
    if (start === undefined) continue;
    const depth = computeEmployeeDepth(employeeId, memberIds, reportsTo);
    const key = String(depth);
    if (!result[key]) result[key] = {};
    result[key]![employeeId] = start;
  }

  return result;
}
