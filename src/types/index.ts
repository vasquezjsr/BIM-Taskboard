export type TaskStatus = string;

export type SheetColumnType = 'text' | 'date' | 'duration' | 'dropdown';

export type SheetColumnAlign = 'left' | 'center' | 'right';

export interface SheetColumnDefinition {
  id: string;
  label: string;
  type: SheetColumnType;
  /** Choices shown when type is dropdown */
  options?: string[];
  /** Header label alignment */
  headerAlignment?: SheetColumnAlign;
  /** Cell content alignment */
  cellAlignment?: SheetColumnAlign;
}

/** User-saved column layout for reuse across projects/boards */
export interface SavedSheetColumnTemplate {
  id: string;
  label: string;
  type: SheetColumnType;
  options?: string[];
  headerAlignment?: SheetColumnAlign;
  cellAlignment?: SheetColumnAlign;
  createdAt: string;
}

export interface TaskDurationRange {
  start: string | null;
  end: string | null;
}

export interface TaskStatusDefinition {
  id: string;
  label: string;
  color: string;
  /** When true, tasks with this status count toward group progress */
  countsAsComplete?: boolean;
  /** Auto-assign project detailers or support when a task enters this status */
  autoAssignTeam?: 'detailers' | 'support' | null;
}

export type EmployeeRole = 'detailer' | 'support-specialist' | 'operations';

export type OrgCategory =
  | 'owner'
  | 'bim-manager'
  | 'operations-manager'
  | 'operations-staff'
  | 'plumbing-detailer'
  | 'mechanical-detailer'
  | 'sheet-metal-detailer'
  | 'jr-detailer'
  | 'support-manager'
  | 'support-specialist';

export const ORG_CATEGORIES: { id: OrgCategory; label: string; teamId: string }[] = [
  { id: 'owner', label: 'Owner', teamId: 'team-owners' },
  { id: 'bim-manager', label: 'BIM Manager', teamId: 'team-bim-managers' },
  { id: 'operations-manager', label: 'Operations Manager', teamId: 'team-operations-managers' },
  { id: 'operations-staff', label: 'Operations', teamId: 'team-operations' },
  { id: 'plumbing-detailer', label: 'Lead Plumbing Detailer', teamId: 'team-plumbing-detailers' },
  { id: 'mechanical-detailer', label: 'Lead Mechanical Detailer', teamId: 'team-mechanical-detailers' },
  { id: 'sheet-metal-detailer', label: 'Lead Sheet Metal Detailer', teamId: 'team-sheet-metal-detailers' },
  { id: 'jr-detailer', label: 'Junior Detailer', teamId: 'team-jr-detailers' },
  { id: 'support-manager', label: 'Support Specialist Manager', teamId: 'team-support-specialists' },
  { id: 'support-specialist', label: 'Support Specialist', teamId: 'team-support-specialists' },
];

export const DEFAULT_ORG_TEAM_IDS = new Set(ORG_CATEGORIES.map((category) => category.teamId));

export const DETAILER_ORG_CATEGORIES = ORG_CATEGORIES.filter((category) =>
  (
    ['plumbing-detailer', 'mechanical-detailer', 'sheet-metal-detailer', 'jr-detailer'] as OrgCategory[]
  ).includes(category.id)
);

export type ProjectBoardType =
  | 'main'
  | 'detailers'
  | 'deliverables'
  | 'spooling'
  | 'rfi'
  | 'documents'
  | 'project-managers'
  | 'field'
  | 'fab'
  | 'shipping'
  | `cb-${string}`;

export type BuiltInProjectBoardType = Exclude<ProjectBoardType, `cb-${string}`>;

export type DashboardType = 'pm' | 'field' | 'fab' | 'shipping';

export type PmDashboardRole = 'project-manager' | 'assistant-pm';
export type FieldDashboardRole = 'site-superintendent' | 'foreman' | 'crew-lead';
export type FabDashboardRole =
  | 'shop-super'
  | 'dept-manager-mech'
  | 'dept-manager-plmb'
  | 'dept-manager-hvac'
  | 'worker';
export type ShippingDashboardRole = 'shipping-manager' | 'worker';

export interface DashboardAssignments {
  pm: Record<PmDashboardRole, string[]>;
  field: Record<FieldDashboardRole, string[]>;
  fab: Record<FabDashboardRole, string[]>;
  shipping: Record<ShippingDashboardRole, string[]>;
}

export interface CustomBoard {
  id: `cb-${string}`;
  name: string;
  clientId: string;
  projectId: string;
  sortOrder: number;
}

export type MainTab =
  | 'clients'
  | 'task-board'
  | 'time-tracking'
  | 'employees'
  | 'org-chart'
  | 'owner-dashboard'
  | 'pm-dashboard'
  | 'field-dashboard'
  | 'fab-dashboard'
  | 'shipping-dashboard'
  | 'activity-log';

export type AppPermission =
  | 'edit-budget-hours'
  | 'manage-org'
  | 'manage-columns'
  | 'view-activity-log'
  | 'view-org-chart'
  | 'view-owner-dashboard'
  | 'view-pm-dashboard'
  | 'view-field-dashboard'
  | 'view-fab-dashboard'
  | 'view-shipping-dashboard';

export interface OrgTeam {
  id: string;
  name: string;
  memberIds: string[];
  sortOrder: number;
}

export interface TimeEntry {
  id: string;
  employeeId: string;
  clientId: string | null;
  projectId: string | null;
  taskId: string | null;
  date: string;
  startTime: string | null;
  endTime: string | null;
  hours: number;
  note: string;
  createdAt: string;
}

export type EmployeeBoardTab = 'detailers' | 'support-specialists';

export interface Employee {
  id: string;
  name: string;
  role: EmployeeRole;
  /** Org chart category — determines default team placement */
  orgCategory?: OrgCategory;
}

export type GroupTier = 'section' | 'parent' | 'child';

export interface TaskGroup {
  id: string;
  name: string;
  clientId: string;
  projectId: string;
  boardType: ProjectBoardType;
  tier: GroupTier;
  parentId: string | null;
  /** Main-board sections only — which sub-board this section represents */
  sectionBoardType: ProjectBoardType | null;
  sortOrder: number;
  /** When set, overrides default rules for showing the group progress bar */
  showProgressBar?: boolean;
  /** Start/end date ranges keyed by duration column id (level groups only) */
  durationFields?: Record<string, TaskDurationRange>;
}

export interface Task {
  id: string;
  /** Project-scoped task number, e.g. TMPL-0001 */
  taskNumber?: string | null;
  title: string;
  description: string;
  status: TaskStatus;
  assigneeIds: string[];
  clientId: string | null;
  projectId: string | null;
  boardType: ProjectBoardType | 'employee';
  groupId: string | null;
  parentTaskId: string | null;
  priority: number;
  dueDate: string | null;
  /** User-defined text/date column values keyed by column id */
  customFields?: Record<string, string | null>;
  /** Start/end date ranges keyed by duration column id */
  durationFields?: Record<string, TaskDurationRange>;
  /** When true, status-based auto-assign will not replace assigneeIds */
  assigneesLocked?: boolean;
  createdAt: string;
}

export interface TaskAttachmentVersion {
  id: string;
  version: number;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  storageId: string;
  uploadedAt: string;
  uploadedById: string | null;
}

export interface TaskAttachment {
  id: string;
  taskId: string;
  fileName: string;
  currentVersionId: string;
  versions: TaskAttachmentVersion[];
}

export interface TaskComment {
  id: string;
  taskId: string;
  authorId: string | null;
  body: string;
  createdAt: string;
}

/** In-memory clipboard payload for copy / paste (not persisted) */
export type TaskClipboardData = Pick<
  Task,
  | 'title'
  | 'description'
  | 'status'
  | 'assigneeIds'
  | 'assigneesLocked'
  | 'boardType'
  | 'groupId'
  | 'parentTaskId'
  | 'dueDate'
  | 'customFields'
  | 'durationFields'
>;

export type ProjectBillingType = 'lump-sum' | 'time-and-material';

export interface Project {
  id: string;
  name: string;
  clientId: string;
  detailerIds: string[];
  supportIds: string[];
  /** Project managers assigned to this job (PM Dashboard) */
  pmIds: string[];
  revitYear: string | null;
  modelType: 'cloud' | 'local' | null;
  /** Full building level list (UG → Roof) */
  buildingLevels: string[];
  /** Levels in scope for this project */
  activeLevels: string[];
  /** Master template — duplicated when creating new projects from template */
  isTemplate?: boolean;
  billingType: ProjectBillingType;
  /** Lump sum projects — contracted hour budget */
  budgetHours: number | null;
  /** Time & material projects — hours logged to date */
  totalHoursSpent: number | null;
  projectStartDate: string | null;
  projectEndDate: string | null;
  /** Short code used in task numbers, e.g. TMPL or 24-1847 */
  jobCode?: string | null;
  /** Next sequence number when creating tasks on this project */
  nextTaskNumber?: number;
}

export interface Client {
  id: string;
  name: string;
}

/** @deprecated Use taskStatuses from store — kept for seed/migration reference */
export const TASK_STATUSES: { id: string; label: string }[] = [
  { id: 'not-started', label: 'Not Started' },
  { id: 'not-ready', label: 'Not Ready' },
  { id: 'ready', label: 'Ready' },
  { id: 'in-progress', label: 'In Progress' },
  { id: 'on-hold', label: 'On Hold' },
  { id: 'complete', label: 'Complete' },
];

export const PROJECT_BOARD_TYPES: { id: ProjectBoardType; label: string }[] = [
  { id: 'main', label: 'Main Overview' },
  { id: 'project-managers', label: 'Project Management' },
  { id: 'rfi', label: 'RFI' },
  { id: 'documents', label: 'Documents' },
  { id: 'detailers', label: 'Detailers' },
  { id: 'deliverables', label: 'Deliverables' },
  { id: 'spooling', label: 'Spooling' },
  { id: 'fab', label: 'Fabrication' },
  { id: 'shipping', label: 'Shipping' },
  { id: 'field', label: 'Field' },
];

export function isCustomBoardId(type: ProjectBoardType): type is `cb-${string}` {
  return typeof type === 'string' && type.startsWith('cb-');
}

export function isSubBoardType(type: ProjectBoardType): boolean {
  return type !== 'main';
}

export function getBoardLabel(type: ProjectBoardType, customBoards: CustomBoard[] = []): string {
  const builtIn = PROJECT_BOARD_TYPES.find((b) => b.id === type);
  if (builtIn) return builtIn.label;
  return customBoards.find((b) => b.id === type)?.name ?? type;
}

/** Merge built-in tab order with custom boards placed at their saved sortOrder slots. */
export function mergeProjectSubBoardTabOrder(
  builtIns: BuiltInProjectBoardType[],
  customs: CustomBoard[]
): ProjectBoardType[] {
  if (customs.length === 0) return [...builtIns];

  const sortedCustoms = [...customs].sort((a, b) => a.sortOrder - b.sortOrder);
  const total = builtIns.length + sortedCustoms.length;
  const result: ProjectBoardType[] = new Array(total);
  const occupied = new Set<number>();
  const unplacedCustoms: ProjectBoardType[] = [];

  for (const board of sortedCustoms) {
    let slot = Math.min(Math.max(board.sortOrder, 0), total - 1);
    while (slot < total && occupied.has(slot)) slot += 1;
    if (slot >= total) {
      unplacedCustoms.push(board.id);
      continue;
    }
    result[slot] = board.id;
    occupied.add(slot);
  }

  let builtInIndex = 0;
  for (let slot = 0; slot < total; slot++) {
    if (occupied.has(slot)) continue;
    if (builtInIndex >= builtIns.length) break;
    result[slot] = builtIns[builtInIndex++]!;
  }

  const placed = new Set(result.filter(Boolean));
  const trailing = [
    ...unplacedCustoms,
    ...builtIns.filter((id) => !placed.has(id)),
  ];
  return [...result.filter(Boolean), ...trailing];
}

export function getProjectSubBoardOrder(
  projectId: string,
  subBoardTabOrder: ProjectBoardType[],
  customBoards: CustomBoard[]
): ProjectBoardType[] {
  const builtIns = normalizeSubBoardTabOrder(
    subBoardTabOrder.filter((id): id is BuiltInProjectBoardType => !isCustomBoardId(id))
  );
  const customs = customBoards.filter((b) => b.projectId === projectId);
  return mergeProjectSubBoardTabOrder(builtIns, customs);
}

export function getAssignableBoards(
  customBoards: CustomBoard[],
  projectId: string
): { id: ProjectBoardType; label: string }[] {
  const customs = customBoards
    .filter((b) => b.projectId === projectId)
    .sort((a, b) => a.sortOrder - b.sortOrder)
    .map((b) => ({ id: b.id as ProjectBoardType, label: b.name }));
  return [
    { id: 'main', label: 'General' },
    ...PROJECT_BOARD_TYPES.filter((b) => b.id !== 'main').map((b) => ({
      id: b.id as ProjectBoardType,
      label: b.label,
    })),
    ...customs,
  ];
}

export const MAIN_SECTION_BOARDS: BuiltInProjectBoardType[] = [
  'project-managers',
  'rfi',
  'documents',
  'detailers',
  'deliverables',
  'spooling',
];

/** Built-in sub-board tabs that do not get a Main Overview section group by default. */
export const EXTENDED_SUB_BOARD_TAB_TYPES: BuiltInProjectBoardType[] = [
  'fab',
  'shipping',
  'field',
];

export const DEFAULT_SUB_BOARD_TAB_ORDER: BuiltInProjectBoardType[] = [
  ...MAIN_SECTION_BOARDS,
  ...EXTENDED_SUB_BOARD_TAB_TYPES,
];

export function normalizeSubBoardTabOrder(
  order?: BuiltInProjectBoardType[] | null
): BuiltInProjectBoardType[] {
  const canonical = DEFAULT_SUB_BOARD_TAB_ORDER;
  const seen = new Set<BuiltInProjectBoardType>();
  const result: BuiltInProjectBoardType[] = [];
  for (const id of order ?? []) {
    if (canonical.includes(id) && !seen.has(id)) {
      seen.add(id);
      result.push(id);
    }
  }
  for (const id of canonical) {
    if (!seen.has(id)) result.push(id);
  }
  return result;
}

export const REVIT_YEAR_OPTIONS = ['2022', '2023', '2024', '2025', '2026'] as const;

export type ProjectSettingsUpdate = Partial<
  Pick<
    Project,
    | 'name'
    | 'detailerIds'
    | 'supportIds'
    | 'pmIds'
    | 'revitYear'
    | 'modelType'
    | 'billingType'
    | 'budgetHours'
    | 'totalHoursSpent'
    | 'projectStartDate'
    | 'projectEndDate'
    | 'jobCode'
  >
>;

export function defaultProjectFields(): Pick<
  Project,
  | 'detailerIds'
  | 'supportIds'
  | 'pmIds'
  | 'revitYear'
  | 'modelType'
  | 'buildingLevels'
  | 'activeLevels'
  | 'isTemplate'
  | 'billingType'
  | 'budgetHours'
  | 'totalHoursSpent'
  | 'projectStartDate'
  | 'projectEndDate'
  | 'jobCode'
  | 'nextTaskNumber'
> {
  return {
    detailerIds: [],
    supportIds: [],
    pmIds: [],
    revitYear: null,
    modelType: null,
    buildingLevels: [],
    activeLevels: [],
    isTemplate: false,
    billingType: 'lump-sum',
    budgetHours: null,
    totalHoursSpent: null,
    projectStartDate: null,
    projectEndDate: null,
    jobCode: null,
    nextTaskNumber: 1,
  };
}

export function normalizeProject(project: Project): Project {
  return {
    ...project,
    detailerIds: project.detailerIds ?? [],
    supportIds: project.supportIds ?? [],
    pmIds: project.pmIds ?? [],
    revitYear: project.revitYear ?? null,
    modelType: project.modelType ?? null,
    buildingLevels: project.buildingLevels ?? [],
    activeLevels: project.activeLevels ?? [],
    isTemplate: project.isTemplate ?? false,
    billingType: project.billingType ?? 'lump-sum',
    budgetHours: project.budgetHours ?? null,
    totalHoursSpent: project.totalHoursSpent ?? null,
    projectStartDate: project.projectStartDate ?? null,
    projectEndDate: project.projectEndDate ?? null,
    jobCode: project.jobCode ?? null,
    nextTaskNumber: project.nextTaskNumber ?? 1,
  };
}

export const EMPLOYEE_ROLES: { id: EmployeeRole; label: string }[] = [
  { id: 'detailer', label: 'Detailer' },
  { id: 'support-specialist', label: 'Support Specialist' },
  { id: 'operations', label: 'Operations' },
];
