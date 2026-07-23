import { create } from 'zustand';

import { persist, createJSONStorage } from 'zustand/middleware';

import { v4 as uuid } from 'uuid';

import type {

  Client,

  Employee,

  EmployeeBoardTab,

  GroupTier,

  MainTab,

  Project,

  ProjectBoardType,

  Task,

  TaskAttachment,

  TaskComment,

  TaskClipboardData,

  TaskGroup,

  TaskStatus,

  TaskStatusDefinition,

  AppPermission,

  DashboardAssignments,

  DashboardType,

  OrgCategory,

  OrgTeam,

  TimeEntry,

} from '../types';

import {
  MAIN_SECTION_BOARDS,
  DEFAULT_SUB_BOARD_TAB_ORDER,
  normalizeSubBoardTabOrder,
  isCustomBoardId,
  type BuiltInProjectBoardType,
  type CustomBoard,
  getProjectSubBoardOrder,
} from '../types';

import { defaultSectionName, collectDescendantGroupIds, restoreAssignedTaskBoardTypes, assignUngroupedSectionTasks, dedupeProjectSections, enrichTaskUpdatesWithBranchGroup, repairOrphanedTaskGroups, repairGroupTiers, reassignOrphanedBranchTasksToLevels, migrateTaskDurationsToLevelGroups, repairTasksOnWrongBoardSection } from '../utils/groupRows';
import { DEFAULT_EMPLOYEES, JOE_VASQUEZ_ID, LEGACY_NAME_TO_ID, normalizeEmployeesWithRemap, TAYLOR_MORGAN_ID, isOwnerEmployee, isProtectedRosterEmployee } from '../data/employees';
import { createDefaultDashboardAssignments } from '../data/dashboards';
import {
  createSeededDashboardAssignments,
  mergeDashboardAssignments,
  mergeDepartmentStaff,
  operationsDashboardPermissions,
} from '../data/departmentStaff';
import { buildBimOrgRoster, createBimOrgChartReportsTo } from '../data/bimOrgChart';
import { applyDefaultProjectTeams, ensureDefaultProjectTeams } from '../data/projectTeams';
import { defaultProjectFields, normalizeProject, type ProjectSettingsUpdate } from '../types';
import { cloneProjectFromTemplate, type NewProjectOptions } from '../utils/projectTemplate';
import { canAccessOrgChart, canEditBudgetHours, canManageColumns, canViewActivityLog, canViewDashboard, canViewEmployeeTime, canViewOwnerDashboard, canViewSpoolingDashboard, canViewTimeTracking, canViewVisibilityDashboard, canViewWeldLogDashboard } from '../utils/permissions';
import {
  DEFAULT_JOB_LEVEL_NAV_VISIBILITY,
  NAV_COLUMN_PERMISSION,
  employeesForJobLevelRow,
  normalizeJobLevelNavVisibility,
  syncOpsDashboardPermissionsFromDefaults,
  type JobLevelNavVisibilityMap,
  type VisibilityNavColumn,
} from '../utils/visibilityMatrix';
import type {
  ActivityLogEntry,
  DeletedColumnArchive,
  DeletedEmployeeArchive,
  DeletedTaskArchive,
  TaskRevisionArchive,
} from '../utils/activityLog';
import {
  applyColumnArchiveRestore,
  applyRestoredTaskArchive,
  buildColumnDeleteArchive,
  buildTaskRevisionArchive,
  columnActivitySummary,
  findColumnDefinition,
  logActivity,
  prependTaskRevisionArchive,
  resolveActivityActorId,
  softDeleteTaskTrees,
  stripColumnFromState,
} from './activityLogHelpers';
import { backfillTaskNumbers, nextTaskNumberForProject } from '../utils/taskNumbers';
import {
  buildSpoolingExportCustomFields,
  filterFilesForSPackages,
  findProjectForManifest,
  findSpoolingTaskForManifest,
  isSsv3ExportLocked,
  parseBoardroomPackageManifest,
  parseSsv3Files,
  spoolingTaskHasSsv3Export,
  upsertBoardroomAbsAttachments,
  SSV3_FIELD,
  SSV3_KIND_ASSEMBLY,
  type BoardroomPackageImportResult,
} from '../utils/boardroomPackageImport';
import {
  promoteSsv3SpoolingTaskToFab,
  demoteSsv3FabTaskToSpooling,
  promoteSsv3FabTaskToShipping,
  promoteSsv3ShippingTaskToField,
  demoteSsv3ShippingTaskToFab,
  demoteSsv3ShippingTaskToSpooling,
  attachSsv3HierarchyFromManifest,
  clearSsv3ExportFromSpoolingTask,
  syncAssemblyTradeMaterialFromPackageRoots,
} from '../utils/promoteSsv3ToFab';
import {
  applyDetailersReadyForSpoolingHandoff,
  applyDetailersSpoolingMirrorCleanup,
  applySpoolingReturnToDetailingHandoff,
  collectDescendantTaskIds,
  repairDetailersSpoolingMirror,
} from '../utils/detailersSpoolingHandoff';
import { normalizeTimeEntry, prepareTimeEntryPayload } from '../utils/timeEntry';
import {
  createDefaultEmployeeCredentials,
  createJoeVasquezCredential,
  DEFAULT_LOGIN_PASSWORD,
  generateInvitePassword,
  isValidEmail,
  lookupEmployeeLogin,
  syncEmployeeAuthAndColors,
  verifyEmployeeLogin,
  type EmployeeCredentialsMap,
} from '../utils/auth';
import {
  assigneeStyleKey,
  buildUniqueAssigneeStyles,
  createDefaultEmployeeAssigneeStyles,
  pickNextUniqueAssigneeStyle,
  type EmployeeAssigneeStylesMap,
} from '../data/assigneeColors';
import {
  buildOrgChartLevels,
  computeEmployeeDepth,
  createDefaultEmployeePermissions,
  createDefaultEmployeeReportsTo,
  createDefaultOrgChartLevelSlots,
  createDefaultOrgTeams,
  defaultOrgCategoryForRole,
  DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS,
  migrateOrgTeamsToCategories,
  managersForOrgChartDepth,
  orgCategoryToTeamId,
  resolveOrgChartCardPositions,
  canPlaceCardAtBodyIndex,
  roleForOrgCategory,
  teamIdToOrgCategory,
  inferOrgCategory,
  isOrgOwner,
  isValidReportingManager,
  defaultDashboardEditPermissionsForCategory,
  type EmployeePermissionsMap,
  type EmployeeReportsToMap,
  normalizeEmployeeReportsTo,
  normalizeOrgChartLevelSlots,
  removeEmployeeFromOrgChartSlots,
  wouldCreateReportingCycle,
  type OrgChartLevelSlotsMap,
} from '../utils/orgChart';
import {
  accessControlRowIdForJobTitle,
  backfillEmployeeJobTitleIds,
  createDefaultEmployeeJobTitles,
  findEmployeeJobTitle,
  normalizeEmployeeJobTitles,
  removeEmployeeFromAllDashboardRoles,
  repairJobTitlePlacement,
  resolveOrgCategoryForTitle,
  type EmployeeJobTitleDef,
} from '../utils/employeeJobs';

function permissionsForJobTitle(title: EmployeeJobTitleDef): AppPermission[] {
  const rowId = accessControlRowIdForJobTitle(title);
  const cells =
    DEFAULT_JOB_LEVEL_NAV_VISIBILITY[rowId] ?? DEFAULT_JOB_LEVEL_NAV_VISIBILITY['ops-unassigned']!;
  const granted = new Set<AppPermission>(['view-org-chart', 'view-time-tracking']);
  for (const [column, enabled] of Object.entries(cells) as [VisibilityNavColumn, boolean][]) {
    if (enabled) granted.add(NAV_COLUMN_PERMISSION[column]);
  }
  const category = resolveOrgCategoryForTitle(title);
  if (category === 'bim-manager' || category === 'operations-manager') {
    granted.add('manage-org');
    granted.add('manage-columns');
    granted.add('view-activity-log');
  }
  if (category === 'bim-manager') {
    granted.add('edit-budget-hours');
  }
  if (
    category === 'plumbing-detailer' ||
    category === 'mechanical-detailer' ||
    category === 'sheet-metal-detailer' ||
    category === 'jr-detailer'
  ) {
    granted.add('edit-budget-hours');
  }
  for (const permission of defaultDashboardEditPermissionsForCategory(category)) {
    granted.add(permission);
  }
  return [...granted];
}

import {

  buildAllSeedData,

  createSeedClients,

  createSeedProjects,

  buildProjectSeed,

  ensureProjectTemplate,

  resetPortfolioToTemplate,

  pruneToClientTemplateOnly,

  ensureProjectHierarchy,

  assignZoneTasksToChildGroups,

  dedupeTasksByProjectTitle,

  injectSampleRfiAndDocumentTasks,

  migrateBranchBoardTaskStructure,

  migrateTradeBeforeLevelGroupStructure,

  rebuildTradeLevelSectionHierarchy,

  isTradeLevelSectionBroken,

  resolveProjectSeedInput,

  reconcileBranchBoardTasksIfNeeded,

  isProjectCoordinationTask,

  PROJECT_COORDINATION_GROUP_NAME,

  TEMPLATE_CLIENT_NAME,

  TEMPLATE_PROJECT_NAME,

} from '../data/vdcSeedData';

import { buildEmptyProjectBoards, resetTemplateToEmptyBoards, isTemplateProject } from '../utils/projectTemplate';
import { applyTemplatePmBoardChecklist } from '../utils/applyPmBoardChecklist';
import { applyTemplateBoardSamples } from '../utils/applyTemplateBoardSamples';
import { applyWorkflowDueDateColumns } from '../utils/applyWorkflowDueDateColumns';
import { applyTemplateMaterialColumn } from '../data/templateMaterialColumn';
import {
  appendPremadeColumnToBoardState,
  appendPremadeColumnToOverviewSectionState,
  ensurePremadeInMainOverviewSectionOrders,
  ensurePremadeSheetColumns,
  normalizeColumnSettingsDropdownIds,
  repairWorkflowTradeMaterialColumnVisibility,
} from '../data/premadeSheetColumns';
import {
  applyCustomColumnToTargets,
  applyPremadeColumnsToTargets,
  buildSheetColumnDefinition,
  savedTemplateFromColumn,
} from '../utils/columnBatchHelpers';
import {
  getMainOverviewSectionColumnOrder,
  normalizeMainOverviewSectionColumnOrder,
  resolveStoredMainOverviewSectionColumnOrder,
} from '../utils/mainOverviewColumns';
import { revertTemplateExpandedLevels } from '../utils/tradeFirstLevelConfig';

import {
  collectTaskIdsInGroupSubtrees,
  computeMoveGroupsToBoard,
  type SheetGroupMergeUpdate,
  type SheetGroupUpdate,
  type SheetTaskUpdate,
} from '../utils/sheetDrag';
import { findSectionBoardType, syncTaskBoardFromGroupPlacement } from '../utils/sheetDrag';
import { inferTaskBranchBoardType } from '../utils/groupRows';
import {
  cloneSnapshot,
  pushHistory,
  type HistorySnapshot,
} from '../utils/history';
import { defaultStatusForBoard } from '../utils/taskStatus';
import { durableStoreStorage, installDurableStoreFlushHooks } from '../utils/durableStoreStorage';
import { ensureDemoPortfolio } from '../utils/ensureDemoPortfolio';
import { normalizeTaskAssignees, taskHasAssignee } from '../utils/taskAssignees';
import {
  applyAutoAssigneesToTask,
  applyDeliverablesAutoAssignTeams,
  enrichTaskUpdatesWithAutoAssignees,
  migrateDeliverablesTaskStatus,
  reconcileTaskAssigneeLock,
} from '../utils/taskAssigneesAuto';
import {
  DEFAULT_DELIVERABLES_TASK_STATUSES,
  DEFAULT_SPOOLING_TASK_STATUSES,
  DEFAULT_TASK_STATUSES,
  createDefaultBoardTaskStatuses,
  ensureDetailersBoardStatuses,
  getBoardTaskStatuses,
  migrateDetailersTaskStatus,
  migrateRfiBoardTaskStatuses,
  migrateTasksToBoardStatuses,
  normalizeProjectBoardTaskStatuses,
  normalizeRfiBoardTaskStatuses,
  isRfiBoardStatusListLocked,
  isBoardStatusListLocked,
  isDashboardDrivenStatusBoard,
  normalizeBoardTaskStatuses,
  normalizeTaskStatuses,
  pickNewStatusColor,
  statusBoardForTask,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from '../utils/taskStatuses';
import {
  buildDefaultTaskBoardVisibleStatuses,
  collectTaskBoardStatusOptions,
  isTaskVisibleOnTaskBoard,
  normalizeTaskBoardVisibleStatuses,
  taskBoardVisibleStatusSet,
} from '../utils/taskBoardVisibility';
import { syncStatusColorMaps, applyStatusColorGlobally } from '../utils/statusColorSync';
import { consolidateDuplicateStatuses } from '../utils/statusConsolidation';
import { duplicateGroupSubtrees } from '../utils/duplicateGroups';
import {
  createDefaultBoardSheetColumns,
  createDefaultBoardSheetColumnOrder,
  defaultBoardColumnOrder,
  appendSheetColumnDefinition,
  getAllConfiguredBoardTypes,
  getBoardLocalSheetColumns,
  getBoardSheetColumnOrder,
  getBoardSheetColumns,
  isFixedSheetColumnId,
  isMainOverviewSharedColumn,
  isProtectedBoardColumnId,
  fixedColumnAsDefinition,
  normalizeBoardSheetColumnOrder,
  normalizeBoardSheetColumns,
  propagateMainSheetColumnToAllBoards,
  stripBoardColumnFromSubBoardOrders,
  stripFlatBoardHiddenColumns,
  syncMainOverviewColumnsToAllBoards,
  syncMainSheetColumnUpdateToAllBoards,
  filterBoardCustomColumns,
  type BoardSheetColumnOrderMap,
  type BoardSheetColumnsMap,
} from '../utils/sheetColumns';



interface AppState {

  clients: Client[];

  projects: Project[];

  employees: Employee[];

  tasks: Task[];

  taskGroups: TaskGroup[];

  /** User-created boards scoped to a project */
  customBoards: CustomBoard[];

  /** Per-board task status options */
  boardTaskStatuses: BoardTaskStatusesMap;

  /** Per-project overrides for board status lists */
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap;

  /** @deprecated Legacy global statuses — migrated to boardTaskStatuses */
  taskStatuses?: TaskStatusDefinition[];

  /** Per-board spreadsheet column definitions */
  boardSheetColumns: BoardSheetColumnsMap;

  /** Per-board spreadsheet column display order */
  boardSheetColumnOrder: BoardSheetColumnOrderMap;

  /** Main Overview only — per-section column order (keys are section board types) */
  mainOverviewSectionColumnOrder: BoardSheetColumnOrderMap;

  /** Main Overview only — section-specific column definitions not on the sub-board */
  mainOverviewSectionSheetColumns: BoardSheetColumnsMap;

  /** File attachments keyed per task row */
  taskAttachments: TaskAttachment[];

  /** Comment threads keyed per task row */
  taskComments: TaskComment[];

  /** Last time comments were viewed per task (ISO timestamp) */
  taskCommentReadAt: Record<string, string>;

  /** Tab order for built-in sub-boards (Detailers, Deliverables, etc.) — Main Overview stays first */
  subBoardTabOrder: BuiltInProjectBoardType[];

  /** Copied task fields for Ctrl+V paste (session only, not persisted) */
  taskClipboard: TaskClipboardData | null;

  /** Sheet row drag in progress — disables board tab reorder (session only) */
  sheetDragActive: boolean;

  /** Board tab highlighted while dragging sheet rows (session only) */
  sheetDragHoverBoard: ProjectBoardType | null;

  historyPast: HistorySnapshot[];
  historyFuture: HistorySnapshot[];

  activityLog: ActivityLogEntry[];
  deletedColumnArchive: DeletedColumnArchive[];
  deletedEmployeeArchive: DeletedEmployeeArchive[];
  deletedTaskArchive: DeletedTaskArchive[];
  taskRevisionArchive: TaskRevisionArchive[];

  /** User-saved column layouts for reuse when adding columns */
  savedSheetColumnTemplates: import('../types').SavedSheetColumnTemplate[];

  /** Dropdown column ids managed as tabs in Column Settings (Materials, Trade, …) */
  columnSettingsDropdownIds: string[];

  activeMainTab: MainTab;

  activeClientId: string | null;

  activeProjectId: string | null;

  activeBoardType: ProjectBoardType;

  activeEmployeeBoard: EmployeeBoardTab;

  /** Status IDs shown on the employee task board */
  taskBoardVisibleStatuses: string[];

  clientsView: 'dashboard' | 'board';

  /**
   * When set, Field Dashboard focuses this project (job view for assigned field crew).
   * Cleared when the user leaves Field or picks All projects.
   */
  fieldFocusProjectId: string | null;

  /** Signed-in employee — controls budget-hours editing and org chart access */
  currentUserId: string | null;

  /** Real signed-in user while previewing another employee perspective (session only) */
  viewAsOriginalUserId: string | null;

  orgTeams: OrgTeam[];

  employeePermissions: EmployeePermissionsMap;

  /** Job levels that automatically get Visibility Dashboard access (editable). */
  visibilityDashboardJobLevels: OrgCategory[];

  /** Editable defaults matrix on Visibility Dashboard (by job-level row id). */
  jobLevelNavVisibility: JobLevelNavVisibilityMap;

  /** Maps employee id → manager employee id within their team */
  employeeReportsTo: EmployeeReportsToMap;

  /** Per-level horizontal slot layout for the org chart (null = phantom slot) */
  orgChartLevelSlots: OrgChartLevelSlotsMap;

  timeEntries: TimeEntry[];

  employeeAssigneeStyles: EmployeeAssigneeStylesMap;

  employeeCredentials: EmployeeCredentialsMap;

  dashboardAssignments?: DashboardAssignments;

  /** Editable job title catalog (Employees promote / stage placement). */
  employeeJobTitles: EmployeeJobTitleDef[];

  setActiveMainTab: (tab: MainTab) => void;

  setActiveClientId: (id: string | null) => void;

  setActiveProjectId: (id: string | null) => void;

  setActiveBoardType: (type: ProjectBoardType) => void;

  setActiveEmployeeBoard: (board: EmployeeBoardTab) => void;

  setTaskBoardVisibleStatuses: (statusIds: string[]) => void;

  setClientsView: (view: 'dashboard' | 'board') => void;

  login: (
    loginId: string,
    password: string
  ) => 'success' | 'not-found' | 'ambiguous' | 'invalid-password';

  ensureDevSession: () => void;

  setViewAsEmployee: (employeeId: string | null) => void;

  logout: () => void;

  goToMainScreen: () => void;

  openProjectBoard: (clientId: string, projectId: string, boardType: ProjectBoardType) => void;

  /** Jump to Field Dashboard focused on a specific job (for assigned field crew / PMs). */
  openFieldJobDashboard: (projectId: string) => void;

  setFieldFocusProjectId: (projectId: string | null) => void;

  addClient: (name: string) => void;

  updateClient: (id: string, updates: Partial<Pick<Client, 'name'>>) => void;

  removeClient: (id: string) => void;

  addProject: (clientId: string, name: string, options?: NewProjectOptions) => void;

  addProjectFromTemplate: (clientId: string, name: string, options: NewProjectOptions) => void;

  removeProject: (id: string) => void;

  updateProjectSettings: (projectId: string, updates: ProjectSettingsUpdate) => void;

  assignProjectPm: (projectId: string, employeeId: string) => void;

  unassignProjectPm: (projectId: string, employeeId: string) => void;

  addEmployee: (
    name: string,
    role: Employee['role'],
    orgCategory: Employee['orgCategory'] | undefined,
    email: string
  ) => {
    employeeId: string;
    employeeName: string;
    invitePassword: string;
    email: string;
  };

  updateEmployee: (id: string, updates: Partial<Pick<Employee, 'name' | 'role' | 'orgCategory'>>) => void;

  /** Promote / change job title (org category + ops dashboard role). */
  changeEmployeeJob: (employeeId: string, jobTitleId: string) => void;

  addJobTitle: (label: string, stageId: EmployeeJobTitleDef['stageId']) => string | null;

  updateJobTitle: (
    id: string,
    updates: Partial<Pick<EmployeeJobTitleDef, 'label' | 'stageId' | 'orgCategory' | 'opsPlacement'>>
  ) => void;

  removeJobTitle: (id: string) => boolean;

  updateEmployeeEmail: (id: string, email: string) => void;

  removeEmployee: (id: string) => void;

  restoreDeletedEmployee: (archiveId: string) => boolean;

  restoreDeletedTask: (archiveId: string) => boolean;

  /** Restore a task from any Activity Log row (delete, status change, or update). */
  restoreTaskActivity: (activityLogId: string) => boolean;

  addOrgTeam: (name: string) => void;

  renameOrgTeam: (teamId: string, name: string) => void;

  removeOrgTeam: (teamId: string) => void;

  moveEmployeeToTeam: (employeeId: string, teamId: string | null) => void;

  setEmployeeManagers: (employeeId: string, managerIds: string[]) => void;

  addEmployeeManager: (employeeId: string, managerId: string) => void;

  toggleEmployeeManager: (employeeId: string, managerId: string, enabled: boolean) => void;

  moveOrgChartEmployeeToSlot: (depth: number, employeeId: string, halfSlotIndex: number) => void;
  placeEmployeeOnOrgChartSlot: (depth: number, employeeId: string, halfSlotIndex: number) => void;
  organizeOrgChartLayout: () => void;

  setEmployeePermission: (employeeId: string, permission: AppPermission, enabled: boolean) => void;

  setVisibilityDashboardJobLevel: (category: OrgCategory, enabled: boolean) => void;

  setEmployeeNavVisibility: (employeeId: string, column: VisibilityNavColumn, enabled: boolean) => void;

  setJobLevelNavVisibility: (rowId: string, column: VisibilityNavColumn, enabled: boolean) => void;

  assignDashboardMember: (dashboard: DashboardType, roleId: string, employeeId: string) => void;

  unassignDashboardMember: (dashboard: DashboardType, roleId: string, employeeId: string) => void;

  addTimeEntry: (entry: Omit<TimeEntry, 'id' | 'createdAt'>) => void;

  updateTimeEntry: (id: string, entry: Omit<TimeEntry, 'id' | 'createdAt'>) => void;

  removeTimeEntry: (id: string) => void;

  addTask: (task: Omit<Task, 'id' | 'createdAt'>) => void;

  updateTask: (id: string, updates: Partial<Task>) => void;

  /** Upsert Fab package/assembly tasks from an SSv3 boardroom-package.json export. */
  importBoardroomPackageManifest: (
    manifest: unknown,
    exportFolder: string
  ) => BoardroomPackageImportResult;

  /**
   * Ensure package Main Task paperclip attachments match ssv3Files / export folder
   * (repairs older imports that only populated Export files metadata).
   */
  ensureBoardroomAttachmentsForTask: (taskId: string) => number;

  /** Remove nested SSv3 assemblies and export report attachments from a Spooling task. */
  clearSsv3ExportFromTask: (taskId: string) => void;

  updateTasks: (ids: string[], updates: Partial<Task>) => void;

  updateTasksWith: (ids: string[], updater: (task: Task) => Partial<Task>) => void;

  refreshTasksAutoAssign: (ids: string[]) => void;

  refreshActiveView: () => void;

  removeTask: (id: string) => void;

  removeTasks: (ids: string[]) => void;

  moveTask: (

    taskId: string,

    updates: {

      status?: TaskStatus;

      assigneeIds?: string[];

      assigneesLocked?: boolean;

      clientId?: string | null;

      projectId?: string | null;

      boardType?: Task['boardType'];

      groupId?: string | null;

      priority?: number;

      dueDate?: string | null;

    }

  ) => void;

  reorderEmployeeTasks: (assigneeId: string, taskIds: string[]) => void;

  reorderSubBoardTabs: (order: BuiltInProjectBoardType[]) => void;

  reorderProjectBoardTabs: (projectId: string, order: ProjectBoardType[]) => void;

  addCustomBoard: (clientId: string, projectId: string, name: string) => ProjectBoardType | null;

  addBoardTaskStatus: (
    boardType: ProjectBoardType,
    label: string,
    autoAssignTeam: 'detailers' | 'support' | null | undefined,
    projectId: string | null,
    applyToAllDeliverables: boolean,
    autoAssignEmployeeId?: string | null
  ) => string | null;

  removeBoardTaskStatus: (
    boardType: ProjectBoardType,
    id: string,
    projectId: string | null,
    applyToAllDeliverables: boolean
  ) => void;

  updateBoardTaskStatus: (
    boardType: ProjectBoardType,
    id: string,
    updates: Partial<
      Pick<
        TaskStatusDefinition,
        'label' | 'color' | 'countsAsComplete' | 'autoAssignTeam' | 'autoAssignEmployeeId'
      >
    >,
    projectId: string | null,
    applyToAllDeliverables: boolean
  ) => void;

  reorderBoardTaskStatuses: (
    boardType: ProjectBoardType,
    statusIds: string[],
    projectId: string | null,
    applyToAllDeliverables: boolean
  ) => void;

  syncStatusColorsAcrossProjects: () => number;

  addBoardSheetColumn: (
    boardType: ProjectBoardType,
    label: string,
    type: import('../types').SheetColumnType,
    options?: string[],
    headerAlignment?: import('../types').SheetColumnAlign,
    cellAlignment?: import('../types').SheetColumnAlign
  ) => string | null;

  removeBoardSheetColumn: (boardType: ProjectBoardType, id: string) => void;

  updateBoardSheetColumn: (
    boardType: ProjectBoardType,
    id: string,
    updates: Partial<
      Pick<
        import('../types').SheetColumnDefinition,
        'label' | 'type' | 'options' | 'headerAlignment' | 'cellAlignment'
      >
    >
  ) => void;

  reorderBoardSheetColumns: (boardType: ProjectBoardType, columnOrder: string[]) => void;

  reorderMainOverviewSectionColumns: (
    sectionBoardType: ProjectBoardType,
    columnOrder: string[]
  ) => void;

  addMainOverviewSectionColumn: (
    sectionBoardType: ProjectBoardType,
    label: string,
    type: import('../types').SheetColumnType,
    options?: string[],
    headerAlignment?: import('../types').SheetColumnAlign,
    cellAlignment?: import('../types').SheetColumnAlign
  ) => string | null;

  addPremadeBoardColumn: (boardType: ProjectBoardType, premadeId: string) => boolean;

  addPremadeOverviewSectionColumn: (
    sectionBoardType: ProjectBoardType,
    premadeId: string
  ) => boolean;

  addPremadeColumnsToTargets: (
    targets: ProjectBoardType[],
    premadeIds: string[],
    mode: 'board' | 'overview'
  ) => number;

  addCustomColumnToTargets: (
    targets: ProjectBoardType[],
    mode: 'board' | 'overview',
    label: string,
    type: import('../types').SheetColumnType,
    options?: string[],
    headerAlignment?: import('../types').SheetColumnAlign,
    cellAlignment?: import('../types').SheetColumnAlign,
    saveToLibrary?: boolean
  ) => string | null;

  saveSheetColumnTemplate: (
    label: string,
    type: import('../types').SheetColumnType,
    options?: string[],
    headerAlignment?: import('../types').SheetColumnAlign,
    cellAlignment?: import('../types').SheetColumnAlign
  ) => string;

  removeSavedSheetColumnTemplate: (id: string) => void;

  addColumnSettingsDropdown: (columnId: string) => void;

  removeColumnSettingsDropdown: (columnId: string) => void;

  removeMainOverviewSectionColumn: (sectionBoardType: ProjectBoardType, id: string) => void;

  restoreDeletedColumn: (archiveId: string) => boolean;

  updateMainOverviewSectionColumn: (
    sectionBoardType: ProjectBoardType,
    id: string,
    updates: Partial<
      Pick<
        import('../types').SheetColumnDefinition,
        'label' | 'type' | 'options' | 'headerAlignment' | 'cellAlignment'
      >
    >
  ) => void;

  duplicateTask: (taskId: string) => string | null;

  duplicateTasks: (taskIds: string[]) => string[];

  duplicateGroup: (groupId: string) => string | null;

  duplicateGroups: (groupIds: string[]) => string[];

  copyTask: (taskId: string) => void;

  pasteTask: (options: {
    clientId: string;
    projectId: string;
    insertAfterTaskId?: string | null;
  }) => string | null;

  undo: () => void;
  redo: () => void;

  createTaskInGroup: (options: {
    clientId: string;
    projectId: string;
    groupId: string | null;
    boardType?: ProjectBoardType | 'main';
  }) => string | null;

  createSubtask: (parentTaskId: string) => string | null;

  applySheetTaskUpdates: (updates: SheetTaskUpdate[]) => void;

  applySheetGroupUpdates: (updates: SheetGroupUpdate[]) => void;

  moveSheetItemsToBoard: (options: {
    clientId: string;
    projectId: string;
    groupIds: string[];
    taskIds: string[];
    targetBoardType: ProjectBoardType;
  }) => void;

  setSheetDragActive: (active: boolean) => void;

  setSheetDragHoverBoard: (board: ProjectBoardType | null) => void;

  applySheetGroupMerge: (merge: SheetGroupMergeUpdate) => void;

  addGroup: (

    group: Omit<TaskGroup, 'id' | 'sortOrder'> & { sortOrder?: number }

  ) => string;

  updateGroup: (id: string, updates: Partial<TaskGroup>) => void;

  removeGroup: (id: string) => void;

  removeGroups: (ids: string[]) => void;

  addFlatBoardHeader: (
    clientId: string,
    projectId: string,
    boardType: ProjectBoardType,
    name?: string
  ) => string;

  upsertTaskAttachment: (params: {
    taskId: string;
    fileName: string;
    mimeType: string;
    sizeBytes: number;
    storageId: string;
    uploadedById: string | null;
    mode: 'new' | 'replace' | 'newVersion';
  }) => void;

  removeTaskAttachment: (attachmentId: string) => string[];

  addTaskComment: (taskId: string, authorId: string | null, body: string) => void;

  removeTaskComment: (commentId: string) => void;

  markTaskCommentsRead: (taskId: string) => void;

  ensureProjectGroups: (clientId: string, projectId: string) => void;

}



const seedEmployees: Employee[] = buildBimOrgRoster([...DEFAULT_EMPLOYEES]);



function getEmployeeIds(employees: Employee[]) {

  return {

    detailers: employees.filter((e) => e.role === 'detailer').map((e) => e.id),

    support: employees.filter((e) => e.role === 'support-specialist').map((e) => e.id),

  };

}



function createInitialPortfolio() {

  const clients = createSeedClients();

  const projects = createSeedProjects(clients);

  const templateClient = clients.find((c) => c.name === TEMPLATE_CLIENT_NAME)!;

  const templateProject = projects.find((p) => p.name === TEMPLATE_PROJECT_NAME)!;

  const groups = buildEmptyProjectBoards(templateClient.id, templateProject.id);

  return { clients, projects, groups, tasks: [] as Task[], templateClient, templateProject };

}



function mergePersistedState(p: Partial<AppState>, current: AppState): AppState {
  const { employees, tasks } = normalizeEmployeesWithRemap(
    p.employees ?? current.employees,
    p.tasks ?? current.tasks
  );
  // Keep every persisted client/project/task. Never run pruneToClientTemplateOnly here —
  // that wiped Demo Mechanical (and all shop progress) on every refresh/rehydrate.
  let clients = p.clients?.length ? p.clients : current.clients;
  let projects = ensureDefaultProjectTeams(
    (p.projects?.length ? p.projects : current.projects).map(normalizeProject)
  );
  let taskGroups = p.taskGroups ?? current.taskGroups;
  let tasksOut = tasks.map(normalizeTaskFields);
  let customBoards = p.customBoards ?? current.customBoards ?? [];
  let timeEntries = p.timeEntries ?? current.timeEntries ?? [];

  const templateProject =
    projects.find((project) => project.name === TEMPLATE_PROJECT_NAME || project.isTemplate) ??
    projects[0];
  if (templateProject) {
    taskGroups = ensureMainSections(taskGroups, templateProject.clientId, templateProject.id);
    const pmChecklist = applyTemplatePmBoardChecklist(projects, taskGroups, tasksOut);
    taskGroups = pmChecklist.taskGroups;
    tasksOut = pmChecklist.tasks;
    const boardSamples = applyTemplateBoardSamples(projects, taskGroups, tasksOut);
    taskGroups = boardSamples.taskGroups;
    tasksOut = boardSamples.tasks;

    const templateHasExpandedLevels = taskGroups.some(
      (group) =>
        group.projectId === templateProject.id &&
        /^Level [3-8]$/.test(group.name)
    );
    // Never delete template tasks on rehydrate — only normalize level names / clear
    // buildingLevels. Task rows stay put (orphan groupId if a level group is removed).
    if (templateHasExpandedLevels || templateProject.buildingLevels.length > 0) {
      const reverted = revertTemplateExpandedLevels(
        templateProject.id,
        taskGroups,
        tasksOut
      );
      taskGroups = reverted.taskGroups;
      tasksOut = reverted.tasks;
      projects = projects.map((project) =>
        project.id === templateProject.id
          ? normalizeProject({
              ...project,
              buildingLevels: [],
              activeLevels: [],
            })
          : project
      );
    }
  }

  const taskIds = new Set(tasksOut.map((task) => task.id));
  const taskAttachments = (p.taskAttachments ?? current.taskAttachments ?? []).filter((attachment) =>
    taskIds.has(attachment.taskId)
  );
  const taskComments = (p.taskComments ?? current.taskComments ?? []).filter((comment) =>
    taskIds.has(comment.taskId)
  );

  tasksOut = migrateRfiBoardTaskStatuses(tasksOut);

  const subBoardTabOrder = normalizeSubBoardTabOrder(p.subBoardTabOrder ?? current.subBoardTabOrder);
  const navigation = resolvePersistedNavigation(
    { ...p, ...readReloadNavigation() },
    current,
    clients,
    projects,
    customBoards,
    subBoardTabOrder
  );

  if (!clients.some((client) => client.id === navigation.activeClientId)) {
    navigation.activeClientId = clients[0]?.id ?? null;
  }
  if (!projects.some((project) => project.id === navigation.activeProjectId)) {
    const forClient = projects.find((project) => project.clientId === navigation.activeClientId);
    navigation.activeProjectId = forClient?.id ?? projects[0]?.id ?? null;
  }

  const currentUserId = p.currentUserId ?? current.currentUserId ?? null;
  const resolvedUserId =
    currentUserId && employees.some((employee) => employee.id === currentUserId)
      ? currentUserId
      : null;
  const employeePermissions =
    p.employeePermissions ??
    current.employeePermissions ??
    createDefaultEmployeePermissions(employees);
  const syncedAuth = syncEmployeeAuthAndColors(
    employees.map((employee) => employee.id),
    p.employeeAssigneeStyles ?? current.employeeAssigneeStyles ?? {},
    p.employeeCredentials ?? current.employeeCredentials ?? {},
    buildUniqueAssigneeStyles,
    employees
  );
  if (
    navigation.activeMainTab === 'org-chart' &&
    !canAccessOrgChart(resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'owner-dashboard' &&
    !canViewOwnerDashboard(resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'pm-dashboard' &&
    !canViewDashboard('pm', resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'field-dashboard' &&
    !canViewDashboard('field', resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'fab-dashboard' &&
    !canViewDashboard('fab', resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'shipping-dashboard' &&
    !canViewDashboard('shipping', resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'weld-log-dashboard' &&
    !canViewWeldLogDashboard(resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'spooling-dashboard' &&
    !canViewSpoolingDashboard(resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'activity-log' &&
    !canViewActivityLog(resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  if (
    navigation.activeMainTab === 'time-tracking' &&
    !canViewTimeTracking(resolvedUserId, employees, employeePermissions)
  ) {
    navigation.activeMainTab = 'clients';
  }

  const visibilityDashboardJobLevels =
    (p.visibilityDashboardJobLevels as OrgCategory[] | undefined) ??
    (current.visibilityDashboardJobLevels as OrgCategory[] | undefined) ??
    [...DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS];

  const jobLevelNavVisibility = normalizeJobLevelNavVisibility(
    (p.jobLevelNavVisibility as JobLevelNavVisibilityMap | undefined) ??
      (current.jobLevelNavVisibility as JobLevelNavVisibilityMap | undefined)
  );

  if (
    navigation.activeMainTab === 'visibility-dashboard' &&
    !canViewVisibilityDashboard(
      resolvedUserId,
      employees,
      employeePermissions,
      visibilityDashboardJobLevels
    )
  ) {
    navigation.activeMainTab = 'clients';
  }

  const normalizedBoardTaskStatuses = normalizeBoardTaskStatuses(
    p.boardTaskStatuses ?? current.boardTaskStatuses,
    p.taskStatuses
  );
  const normalizedProjectBoardTaskStatuses = normalizeProjectBoardTaskStatuses(
    p.projectBoardTaskStatuses ?? current.projectBoardTaskStatuses
  );
  const consolidatedStatuses = consolidateDuplicateStatuses(
    normalizedBoardTaskStatuses,
    normalizedProjectBoardTaskStatuses,
    tasksOut,
    p.taskBoardVisibleStatuses ?? current.taskBoardVisibleStatuses ?? []
  );
  tasksOut = consolidatedStatuses.tasks;
  const syncedStatusColors = syncStatusColorMaps(
    consolidatedStatuses.boardTaskStatuses,
    consolidatedStatuses.projectBoardTaskStatuses
  );

  let boardSheetColumns = normalizeBoardSheetColumns(p.boardSheetColumns ?? current.boardSheetColumns);
  let boardSheetColumnOrder = normalizeBoardSheetColumnOrder(
    p.boardSheetColumnOrder ?? current.boardSheetColumnOrder,
    p.boardSheetColumns ?? current.boardSheetColumns
  );
  const premadeEnsured = ensurePremadeSheetColumns(boardSheetColumns, boardSheetColumnOrder);
  boardSheetColumns = premadeEnsured.boardSheetColumns;
  boardSheetColumnOrder = premadeEnsured.boardSheetColumnOrder;
  const mainOverviewSectionSheetColumns = normalizeBoardSheetColumns(
    p.mainOverviewSectionSheetColumns ?? current.mainOverviewSectionSheetColumns
  );
  let mainOverviewSectionColumnOrder = ensurePremadeInMainOverviewSectionOrders(
    normalizeMainOverviewSectionColumnOrder(
      p.mainOverviewSectionColumnOrder ?? current.mainOverviewSectionColumnOrder ?? {},
      mainOverviewSectionSheetColumns,
      boardSheetColumns
    )
  );
  {
    const repaired = repairWorkflowTradeMaterialColumnVisibility(
      boardSheetColumns,
      boardSheetColumnOrder,
      mainOverviewSectionColumnOrder,
      mainOverviewSectionSheetColumns
    );
    boardSheetColumns = repaired.boardSheetColumns;
    boardSheetColumnOrder = repaired.boardSheetColumnOrder;
    mainOverviewSectionColumnOrder = repaired.mainOverviewSectionColumnOrder;
  }

  return {
    ...current,
    ...p,
    ...navigation,
    clients,
    employees,
    tasks: tasksOut,
    taskGroups,
    projects,
    customBoards,
    subBoardTabOrder,
    currentUserId: resolvedUserId,
    orgTeams: p.orgTeams ?? current.orgTeams ?? createDefaultOrgTeams(employees),
    employeePermissions,
    visibilityDashboardJobLevels,
    jobLevelNavVisibility,
    timeEntries,
    employeeReportsTo: normalizeEmployeeReportsTo(p.employeeReportsTo ?? current.employeeReportsTo),
    orgChartLevelSlots: normalizeOrgChartLevelSlots(p.orgChartLevelSlots ?? current.orgChartLevelSlots),
    employeeAssigneeStyles: syncedAuth.employeeAssigneeStyles,
    employeeCredentials: syncedAuth.employeeCredentials,
    boardTaskStatuses: syncedStatusColors.boardTaskStatuses,
    projectBoardTaskStatuses: syncedStatusColors.projectBoardTaskStatuses,
    boardSheetColumns,
    boardSheetColumnOrder: boardSheetColumnOrder,
    mainOverviewSectionColumnOrder,
    mainOverviewSectionSheetColumns,
    taskAttachments,
    taskComments,
    taskCommentReadAt: p.taskCommentReadAt ?? current.taskCommentReadAt ?? {},
    taskBoardVisibleStatuses: normalizeTaskBoardVisibleStatuses(
      consolidatedStatuses.taskBoardVisibleStatuses,
      collectTaskBoardStatusOptions(
        syncedStatusColors.boardTaskStatuses,
        syncedStatusColors.projectBoardTaskStatuses,
        tasksOut
      )
    ),
    dashboardAssignments:
      p.dashboardAssignments ?? current.dashboardAssignments ?? createDefaultDashboardAssignments(),
    employeeJobTitles: normalizeEmployeeJobTitles(
      (p.employeeJobTitles as EmployeeJobTitleDef[] | undefined) ?? current.employeeJobTitles
    ),
    activityLog: Array.isArray(p.activityLog) ? p.activityLog : (current.activityLog ?? []),
    deletedColumnArchive: Array.isArray(p.deletedColumnArchive)
      ? p.deletedColumnArchive
      : (current.deletedColumnArchive ?? []),
    deletedEmployeeArchive: Array.isArray(p.deletedEmployeeArchive)
      ? p.deletedEmployeeArchive
      : (current.deletedEmployeeArchive ?? []),
    deletedTaskArchive: Array.isArray(p.deletedTaskArchive)
      ? p.deletedTaskArchive
      : (current.deletedTaskArchive ?? []),
    taskRevisionArchive: Array.isArray(p.taskRevisionArchive)
      ? p.taskRevisionArchive
      : (current.taskRevisionArchive ?? []),
    savedSheetColumnTemplates: Array.isArray(p.savedSheetColumnTemplates)
      ? p.savedSheetColumnTemplates
      : (current.savedSheetColumnTemplates ?? []),
    columnSettingsDropdownIds: normalizeColumnSettingsDropdownIds(
      p.columnSettingsDropdownIds ?? current.columnSettingsDropdownIds
    ),
  };
}



function applyProjectStructureRepairs(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  employees: Employee[],
  options?: { preserveGroupHierarchy?: boolean }
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  let nextGroups = taskGroups;
  for (const project of projects) {
    nextGroups = dedupeProjectSections(nextGroups, project.id);
  }

  const employeeIds = getEmployeeIds(employees);
  let nextTasks = tasks;

  if (options?.preserveGroupHierarchy) {
    nextTasks = assignZoneTasksToChildGroups(nextTasks, nextGroups, projects);
    nextTasks = dedupeTasksByProjectTitle(nextTasks);
  } else {
    const reconciled = reconcileBranchBoardTasksIfNeeded(
      projects,
      nextGroups,
      tasks,
      employeeIds
    );
    nextGroups = reconciled.taskGroups;
    nextTasks = reconciled.tasks;
  }

  const hierarchy = ensureProjectHierarchy(projects, nextGroups, nextTasks, employeeIds);
  nextGroups = hierarchy.taskGroups;
  nextTasks = repairOrphanedTaskGroups(nextGroups, hierarchy.tasks);
  if (!options?.preserveGroupHierarchy) {
    nextTasks = assignZoneTasksToChildGroups(nextTasks, nextGroups, projects);
    nextTasks = dedupeTasksByProjectTitle(nextTasks);
  }
  nextTasks = assignProjectCoordinationTasks(nextGroups, nextTasks);
  nextTasks = assignUngroupedSectionTasks(nextGroups, nextTasks);
  return { taskGroups: nextGroups, tasks: nextTasks };
}

function createMainSections(clientId: string, projectId: string): TaskGroup[] {
  return buildEmptyProjectBoards(clientId, projectId);
}



function ensureMainSections(

  groups: TaskGroup[],

  clientId: string,

  projectId: string

): TaskGroup[] {

  const existing = groups.filter(

    (g) =>

      g.clientId === clientId &&

      g.projectId === projectId &&

      g.boardType === 'main' &&

      g.tier === 'section'

  );

  const existingTypes = new Set(existing.map((g) => g.sectionBoardType));

  const missing = MAIN_SECTION_BOARDS.filter((t) => !existingTypes.has(t));

  if (missing.length === 0) return groups;



  const newSections = missing.map((sectionBoardType, i) => ({

    id: uuid(),

    name: defaultSectionName(sectionBoardType),

    clientId,

    projectId,

    boardType: 'main' as ProjectBoardType,

    tier: 'section' as GroupTier,

    parentId: null,

    sectionBoardType,

    sortOrder: existing.length + i,

  }));

  return [...groups, ...newSections];

}

function ensureCustomBoardSections(
  groups: TaskGroup[],
  clientId: string,
  projectId: string,
  customBoards: CustomBoard[],
  subBoardTabOrder: ProjectBoardType[]
): TaskGroup[] {
  const projectBoards = customBoards.filter(
    (b) => b.clientId === clientId && b.projectId === projectId
  );
  if (projectBoards.length === 0) return groups;

  const existingTypes = new Set(
    groups
      .filter(
        (g) =>
          g.clientId === clientId &&
          g.projectId === projectId &&
          g.boardType === 'main' &&
          g.tier === 'section'
      )
      .map((g) => g.sectionBoardType)
  );

  const missing = projectBoards.filter((b) => !existingTypes.has(b.id));
  let next = missing.length
    ? [
        ...groups,
        ...missing.map((board) => ({
          id: uuid(),
          name: defaultSectionName(board.id, customBoards),
          clientId,
          projectId,
          boardType: 'main' as ProjectBoardType,
          tier: 'section' as const,
          parentId: null,
          sectionBoardType: board.id,
          sortOrder: board.sortOrder,
        })),
      ]
    : groups;

  const order = getProjectSubBoardOrder(projectId, subBoardTabOrder, customBoards);
  next = syncSectionDisplayNames(next, customBoards);
  return syncSectionSortOrder(next, order, projectId);
}



function migrateBoardDefaultStatuses(tasks: Task[]): Task[] {
  return tasks.map((t) => {
    if (t.boardType === 'detailers' && t.status !== 'not-started') {
      return { ...t, status: 'not-started' as TaskStatus };
    }
    if (t.boardType === 'deliverables') {
      const status = migrateDeliverablesTaskStatus(t.status);
      if (status !== t.status) return { ...t, status: status as TaskStatus };
    }
    if (t.boardType === 'project-managers' && t.status !== 'not-ready') {
      return { ...t, status: 'not-ready' as TaskStatus };
    }
    return t;
  });
}

/** Move assigned detailer/deliverable tasks off hidden statuses so they stay on the task board. */
function restoreAssignedTasksForTaskBoard(tasks: Task[]): Task[] {
  return tasks.map((task) => {
    if (task.assigneeIds.length === 0) return task;
    if (task.boardType !== 'detailers' && task.boardType !== 'deliverables') return task;
    if (task.status !== 'not-started' && task.status !== 'not-ready') return task;
    return { ...task, status: 'ready' as TaskStatus };
  });
}

function migrateDeliverablesBoardStatuses(
  tasks: Task[],
  taskGroups: TaskGroup[]
): Task[] {
  return tasks.map((task) => {
    const board = statusBoardForTask(task, taskGroups);
    if (board !== 'deliverables') return task;
    const status = migrateDeliverablesTaskStatus(task.status);
    return status === task.status ? task : { ...task, status: status as TaskStatus };
  });
}

function ensureProjectCoordinationGroups(
  groups: TaskGroup[],
  projects: Project[]
): TaskGroup[] {
  let next = [...groups];
  for (const project of projects) {
    if (isTemplateProject(project)) continue;

    const pmSection = next.find(
      (g) =>
        g.projectId === project.id &&
        g.tier === 'section' &&
        g.sectionBoardType === 'project-managers'
    );
    if (!pmSection) continue;

    const hasGroup = next.some(
      (g) =>
        g.projectId === project.id &&
        g.parentId === pmSection.id &&
        g.name === PROJECT_COORDINATION_GROUP_NAME
    );
    if (hasGroup) continue;

    next.push({
      id: uuid(),
      name: PROJECT_COORDINATION_GROUP_NAME,
      clientId: project.clientId,
      projectId: project.id,
      boardType: 'main',
      tier: 'parent',
      parentId: pmSection.id,
      sectionBoardType: null,
      sortOrder: 0,
    });
  }
  return next;
}

function assignProjectCoordinationTasks(groups: TaskGroup[], tasks: Task[]): Task[] {
  return tasks.map((task) => {
    if (!isProjectCoordinationTask(task.title)) return task;

    const pmGroup = groups.find(
      (g) =>
        g.projectId === task.projectId &&
        g.name === PROJECT_COORDINATION_GROUP_NAME &&
        g.tier === 'parent'
    );
    if (!pmGroup) return task;

    return {
      ...task,
      boardType: 'project-managers',
      groupId: pmGroup.id,
    };
  });
}

function commitBoardTaskStatusList(
  state: Pick<AppState, 'boardTaskStatuses' | 'projectBoardTaskStatuses'>,
  boardType: ProjectBoardType,
  statuses: TaskStatusDefinition[],
  projectId: string | null | undefined,
  applyToAllDeliverables: boolean
): Pick<AppState, 'boardTaskStatuses' | 'projectBoardTaskStatuses'> {
  const copied = isRfiBoardStatusListLocked(boardType)
    ? normalizeRfiBoardTaskStatuses(statuses)
    : statuses.map((status) => ({ ...status }));
  let boardTaskStatuses = state.boardTaskStatuses;
  let projectBoardTaskStatuses = state.projectBoardTaskStatuses;

  if (projectId) {
    projectBoardTaskStatuses = {
      ...projectBoardTaskStatuses,
      [projectId]: {
        ...projectBoardTaskStatuses[projectId],
        [boardType]: copied,
      },
    };
  } else {
    boardTaskStatuses = {
      ...boardTaskStatuses,
      [boardType]: copied,
    };
  }

  if (applyToAllDeliverables && boardType === 'deliverables') {
    boardTaskStatuses = {
      ...boardTaskStatuses,
      deliverables: copied.map((status) => ({ ...status })),
    };
  }

  return { boardTaskStatuses, projectBoardTaskStatuses };
}

function enrichTaskUpdates(
  task: Task,
  updates: Partial<Task>,
  projects: Project[],
  taskGroups: TaskGroup[],
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  employees: Employee[] = [],
  employeeJobTitles: import('../utils/employeeJobs').EmployeeJobTitleDef[] = []
): Partial<Task> {
  // Treat customFields / durationFields as patches so one-column edits never wipe siblings.
  let patched: Partial<Task> = { ...updates };
  if (updates.customFields) {
    patched.customFields = {
      ...(task.customFields ?? {}),
      ...updates.customFields,
    };
  }
  if (updates.durationFields) {
    const next = { ...(task.durationFields ?? {}) };
    for (const [colId, range] of Object.entries(updates.durationFields)) {
      next[colId] = {
        ...(task.durationFields?.[colId] ?? { start: null, end: null }),
        ...range,
      };
    }
    patched.durationFields = next;
  }

  patched = applyDetailersReadyForSpoolingHandoff(task, patched);
  patched = applySpoolingReturnToDetailingHandoff(task, patched);
  patched = applyDetailersSpoolingMirrorCleanup(task, patched);

  const withBranch = enrichTaskUpdatesWithBranchGroup(
    task,
    patched,
    taskGroups,
    PROJECT_COORDINATION_GROUP_NAME
  );
  return enrichTaskUpdatesWithAutoAssignees(
    task,
    withBranch,
    projects,
    taskGroups,
    boardTaskStatuses,
    projectBoardTaskStatuses,
    employees,
    employeeJobTitles
  );
}

/**
 * When a parent package is set to Ready for Spooling, push the same status
 * (and Detailers→Spooling handoff) onto all nested assembly subtasks.
 */
function cascadeReadyForSpoolingToAssemblies(
  tasks: Task[],
  parentIds: Iterable<string>,
  projects: Project[],
  taskGroups: TaskGroup[],
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  employees: Employee[] = [],
  employeeJobTitles: import('../utils/employeeJobs').EmployeeJobTitleDef[] = []
): Task[] {
  const rootIds = new Set<string>();
  for (const id of parentIds) {
    const parent = tasks.find((task) => task.id === id);
    if (parent?.status === 'ready-for-spooling') rootIds.add(id);
  }
  if (rootIds.size === 0) return tasks;

  const childIds = new Set<string>();
  for (const rootId of rootIds) {
    for (const childId of collectDescendantTaskIds(tasks, rootId)) {
      if (!rootIds.has(childId)) childIds.add(childId);
    }
  }
  if (childIds.size === 0) return tasks;

  return tasks.map((task) => {
    if (!childIds.has(task.id)) return task;
    const enriched = enrichTaskUpdates(
      task,
      { status: 'ready-for-spooling' },
      projects,
      taskGroups,
      boardTaskStatuses,
      projectBoardTaskStatuses,
      employees,
      employeeJobTitles
    );
    return { ...task, ...enriched };
  });
}

const RELOAD_NAV_SESSION_KEY = 'bim-task-board-reload-nav';

function resolveTaskBoardVisibleStatusIds(
  state: Pick<
    AppState,
    'taskBoardVisibleStatuses' | 'boardTaskStatuses' | 'projectBoardTaskStatuses' | 'tasks'
  >
): string[] {
  const options = collectTaskBoardStatusOptions(
    state.boardTaskStatuses,
    state.projectBoardTaskStatuses,
    state.tasks
  );
  return normalizeTaskBoardVisibleStatuses(state.taskBoardVisibleStatuses, options);
}

function taskIdsForActiveView(
  state: Pick<
    AppState,
    | 'activeMainTab'
    | 'activeClientId'
    | 'activeProjectId'
    | 'tasks'
    | 'taskBoardVisibleStatuses'
    | 'boardTaskStatuses'
    | 'projectBoardTaskStatuses'
  >
): string[] {
  if (state.activeMainTab === 'clients') {
    if (!state.activeClientId || !state.activeProjectId) return [];
    return state.tasks
      .filter(
        (task) =>
          task.clientId === state.activeClientId && task.projectId === state.activeProjectId
      )
      .map((task) => task.id);
  }

  const visibleStatuses = taskBoardVisibleStatusSet(resolveTaskBoardVisibleStatusIds(state));
  return state.tasks
    .filter((task) =>
      isTaskVisibleOnTaskBoard(
        task,
        visibleStatuses,
        state.boardTaskStatuses,
        state.projectBoardTaskStatuses,
        state.tasks
      )
    )
    .map((task) => task.id);
}

function readReloadNavigation(): Partial<AppState> | null {
  try {
    const raw = sessionStorage.getItem(RELOAD_NAV_SESSION_KEY);
    if (!raw) return null;
    sessionStorage.removeItem(RELOAD_NAV_SESSION_KEY);
    return JSON.parse(raw) as Partial<AppState>;
  } catch {
    return null;
  }
}

function resolvePersistedNavigation(
  source: Partial<AppState>,
  current: Pick<
    AppState,
    | 'activeMainTab'
    | 'activeClientId'
    | 'activeProjectId'
    | 'activeBoardType'
    | 'activeEmployeeBoard'
    | 'clientsView'
  >,
  clients: Client[],
  projects: Project[],
  customBoards: CustomBoard[],
  subBoardTabOrder: BuiltInProjectBoardType[]
): Pick<
  AppState,
  | 'activeMainTab'
  | 'activeClientId'
  | 'activeProjectId'
  | 'activeBoardType'
  | 'activeEmployeeBoard'
  | 'clientsView'
> {
  const rawMainTab = source.activeMainTab as MainTab | 'permissions' | undefined;
  const activeMainTab: MainTab =
    rawMainTab === 'task-board'
      ? 'task-board'
      : rawMainTab === 'time-tracking'
        ? 'time-tracking'
        : rawMainTab === 'employees'
          ? 'employees'
          : rawMainTab === 'owner-dashboard'
          ? 'owner-dashboard'
          : rawMainTab === 'pm-dashboard'
            ? 'pm-dashboard'
            : rawMainTab === 'field-dashboard'
          ? 'field-dashboard'
          : rawMainTab === 'fab-dashboard'
            ? 'fab-dashboard'
            : rawMainTab === 'shipping-dashboard'
              ? 'shipping-dashboard'
              : rawMainTab === 'spooling-dashboard'
                ? 'spooling-dashboard'
                : rawMainTab === 'weld-log-dashboard'
                  ? 'weld-log-dashboard'
                  : rawMainTab === 'activity-log'
                    ? 'activity-log'
                    : rawMainTab === 'visibility-dashboard'
                      ? 'visibility-dashboard'
                      : rawMainTab === 'org-chart' || rawMainTab === 'permissions'
                        ? 'org-chart'
                        : 'clients';

  const clientsView: AppState['clientsView'] =
    source.clientsView === 'dashboard' ? 'dashboard' : 'board';

  let activeClientId = source.activeClientId ?? current.activeClientId;
  if (clientsView === 'dashboard') {
    if (activeClientId && !clients.some((client) => client.id === activeClientId)) {
      activeClientId = null;
    }
  } else if (!activeClientId || !clients.some((client) => client.id === activeClientId)) {
    activeClientId = clients[0]?.id ?? null;
  }

  const clientProjects = projects.filter((project) => project.clientId === activeClientId);
  let activeProjectId = source.activeProjectId ?? current.activeProjectId;
  if (clientsView === 'dashboard') {
    activeProjectId = null;
  } else if (!activeProjectId || !clientProjects.some((project) => project.id === activeProjectId)) {
    activeProjectId = clientProjects[0]?.id ?? null;
  }

  let activeBoardType: ProjectBoardType = source.activeBoardType ?? current.activeBoardType ?? 'main';
  if (activeProjectId) {
    const validBoards = new Set<ProjectBoardType>([
      'main',
      ...getProjectSubBoardOrder(activeProjectId, subBoardTabOrder, customBoards),
    ]);
    if (!validBoards.has(activeBoardType)) {
      activeBoardType = 'main';
    }
  } else {
    activeBoardType = 'main';
  }

  const activeEmployeeBoard: EmployeeBoardTab =
    source.activeEmployeeBoard === 'support-specialists' ? 'support-specialists' : 'detailers';

  return { activeMainTab, activeClientId, activeProjectId, activeBoardType, activeEmployeeBoard, clientsView };
}

function normalizeTaskFields(task: Task & { assigneeId?: string | null }): Task {
  return normalizeTaskAssignees({
    ...task,
    customFields: task.customFields ?? {},
    durationFields: task.durationFields ?? {},
    assigneesLocked: task.assigneesLocked ?? false,
  });
}

function taskToClipboard(task: Task): TaskClipboardData {
  return {
    title: task.title,
    description: task.description,
    status: task.status,
    assigneeIds: [...task.assigneeIds],
    assigneesLocked: task.assigneesLocked ?? false,
    boardType: task.boardType,
    groupId: task.groupId,
    parentTaskId: task.parentTaskId,
    dueDate: task.dueDate,
    customFields: { ...(task.customFields ?? {}) },
    durationFields: { ...(task.durationFields ?? {}) },
  };
}

function resolveGroupForProject(
  groups: TaskGroup[],
  projectId: string,
  groupId: string | null
): string | null {
  if (!groupId) return null;
  return groups.some((g) => g.id === groupId && g.projectId === projectId) ? groupId : null;
}

function insertTaskCopy(
  tasks: Task[],
  clip: TaskClipboardData,
  clientId: string,
  projectId: string,
  groupId: string | null,
  boardType: Task['boardType'],
  insertAfterTaskId: string | null
): { tasks: Task[]; newTaskId: string } {
  const newId = uuid();
  const bucket = tasks.filter(
    (t) =>
      t.projectId === projectId &&
      t.groupId === groupId &&
      t.boardType === boardType
  );

  let priority: number;
  let updated = tasks;

  if (insertAfterTaskId) {
    const anchor = tasks.find((t) => t.id === insertAfterTaskId);
    priority = anchor ? anchor.priority + 1 : bucket.reduce((max, t) => Math.max(max, t.priority), -1) + 1;
    updated = updated.map((t) => {
      if (
        t.projectId === projectId &&
        t.groupId === groupId &&
        t.boardType === boardType &&
        t.priority >= priority
      ) {
        return { ...t, priority: t.priority + 1 };
      }
      return t;
    });
  } else {
    priority = bucket.reduce((max, t) => Math.max(max, t.priority), -1) + 1;
  }

  const copyTitle = clip.title.endsWith(' (Copy)') ? clip.title : `${clip.title} (Copy)`;
  const copy: Task = {
    ...clip,
    id: newId,
    clientId,
    projectId,
    groupId,
    boardType,
    parentTaskId: clip.parentTaskId ?? null,
    title: copyTitle,
    priority,
    createdAt: new Date().toISOString(),
  };

  return { tasks: [...updated, copy], newTaskId: newId };
}

function syncSectionDisplayNames(
  groups: TaskGroup[],
  customBoards: CustomBoard[]
): TaskGroup[] {
  return groups.map((group) => {
    if (group.tier !== 'section' || !group.sectionBoardType) return group;
    return { ...group, name: defaultSectionName(group.sectionBoardType, customBoards) };
  });
}

function removeSpoolingBoardSections(groups: TaskGroup[]): TaskGroup[] {
  const spoolingSections = groups.filter(
    (g) => g.tier === 'section' && (g.sectionBoardType as string) === 'spooling'
  );
  if (spoolingSections.length === 0) return groups;

  const spoolingSectionIds = new Set(spoolingSections.map((g) => g.id));
  let next = groups.filter((g) => !spoolingSectionIds.has(g.id));

  for (const spoolSection of spoolingSections) {
    const deliverablesSection = next.find(
      (g) =>
        g.projectId === spoolSection.projectId &&
        g.clientId === spoolSection.clientId &&
        g.tier === 'section' &&
        g.sectionBoardType === 'deliverables'
    );
    if (!deliverablesSection) continue;
    next = next.map((g) =>
      g.parentId === spoolSection.id ? { ...g, parentId: deliverablesSection.id } : g
    );
  }

  return next;
}

function syncSectionSortOrder(
  groups: TaskGroup[],
  order: ProjectBoardType[],
  projectId?: string
): TaskGroup[] {
  return groups.map((group) => {
    if (group.tier !== 'section' || !group.sectionBoardType) return group;
    if (projectId && group.projectId !== projectId) return group;
    const idx = order.indexOf(group.sectionBoardType);
    return idx === -1 ? group : { ...group, sortOrder: idx };
  });
}

function renameDocumentsSectionGroups(groups: TaskGroup[]): TaskGroup[] {
  const documentsSectionName = defaultSectionName('documents');
  return groups.map((g) => {
    if (
      g.tier === 'section' &&
      g.sectionBoardType === 'documents' &&
      g.name !== documentsSectionName
    ) {
      return { ...g, name: documentsSectionName };
    }
    return g;
  });
}

function renameProjectManagementSectionGroups(groups: TaskGroup[]): TaskGroup[] {
  const sectionName = defaultSectionName('project-managers');
  return groups.map((g) => {
    if (
      g.tier === 'section' &&
      g.sectionBoardType === 'project-managers' &&
      g.name !== sectionName
    ) {
      return { ...g, name: sectionName };
    }
    return g;
  });
}



function buildPortfolioSeed(employees: Employee[]) {

  const clients = createSeedClients();

  const projects = createSeedProjects(clients);

  const employeeIds = getEmployeeIds(employees);

  const { groups, tasks } = buildAllSeedData(projects, employeeIds);

  const templateClient = clients.find((c) => c.name === TEMPLATE_CLIENT_NAME)!;

  const templateProject = projects.find((p) => p.name === TEMPLATE_PROJECT_NAME)!;

  return {

    clients,

    projects,

    taskGroups: groups,

    tasks,

    activeClientId: templateClient.id,

    activeProjectId: templateProject.id,

    subBoardTabOrder: [...DEFAULT_SUB_BOARD_TAB_ORDER],

    historyPast: [],
    historyFuture: [],
  };

}



function createRecoveryPersistedState(persisted: unknown, employees: Employee[]) {
  const portfolio = buildPortfolioSeed(employees);
  return {
    ...(persisted as Record<string, unknown>),
    ...portfolio,
    employees,
    currentUserId: null,
    activeMainTab: 'clients' as MainTab,
    activeBoardType: 'main' as ProjectBoardType,
    activeEmployeeBoard: 'detailers' as EmployeeBoardTab,
    clientsView: 'board' as const,
    customBoards: [],
    boardTaskStatuses: createDefaultBoardTaskStatuses(),
    projectBoardTaskStatuses: {},
    boardSheetColumns: createDefaultBoardSheetColumns(),
    boardSheetColumnOrder: createDefaultBoardSheetColumnOrder(),
    mainOverviewSectionColumnOrder: {},
    mainOverviewSectionSheetColumns: {},
    taskAttachments: [],
    taskComments: [],
    taskCommentReadAt: {},
    taskBoardVisibleStatuses: buildDefaultTaskBoardVisibleStatuses([
      ...DEFAULT_TASK_STATUSES,
      ...DEFAULT_DELIVERABLES_TASK_STATUSES,
    ]),
    orgTeams: createDefaultOrgTeams(employees),
    employeePermissions: createDefaultEmployeePermissions(employees),
    visibilityDashboardJobLevels: [...DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS],
    jobLevelNavVisibility: normalizeJobLevelNavVisibility(undefined),
    activityLog: [],
    deletedColumnArchive: [],
    deletedEmployeeArchive: [],
    deletedTaskArchive: [],
    taskRevisionArchive: [],
    savedSheetColumnTemplates: [],
    columnSettingsDropdownIds: normalizeColumnSettingsDropdownIds(),
    timeEntries: [],
    employeeReportsTo: createBimOrgChartReportsTo(),
    orgChartLevelSlots: createDefaultOrgChartLevelSlots(
      employees.map((employee) => employee.id),
      createBimOrgChartReportsTo()
    ),
    employeeAssigneeStyles: createDefaultEmployeeAssigneeStyles(employees.map((employee) => employee.id)),
    employeeCredentials: createDefaultEmployeeCredentials(employees.map((employee) => employee.id)),
    dashboardAssignments: createDefaultDashboardAssignments(),
    employeeJobTitles: createDefaultEmployeeJobTitles(),
  };
}



function runStoreMigration(persisted: unknown, version: number) {
  const state = persisted as Record<string, unknown> & {
    clients?: Client[];
    projects?: Project[];
    employees?: Employee[];
    tasks?: Task[];
    taskGroups?: TaskGroup[];
    subBoardTabOrder?: BuiltInProjectBoardType[];
    customBoards?: CustomBoard[];
    taskStatuses?: TaskStatusDefinition[];
    boardTaskStatuses?: BoardTaskStatusesMap;
    projectBoardTaskStatuses?: ProjectBoardTaskStatusesMap;
    boardSheetColumns?: BoardSheetColumnsMap;
    boardSheetColumnOrder?: BoardSheetColumnOrderMap;
    taskAttachments?: TaskAttachment[];
    taskComments?: TaskComment[];
    taskCommentReadAt?: Record<string, string>;
  };

  let employees = state.employees ?? seedEmployees;

        if (version < 7) {

          const portfolio = buildPortfolioSeed(employees);

          return {

            ...state,

            ...portfolio,

            employees,

            activeMainTab: (state.activeMainTab as MainTab | undefined) ?? 'clients',

            activeClientId: (state.activeClientId as string | null | undefined) ?? null,

            activeProjectId: (state.activeProjectId as string | null | undefined) ?? null,

            activeBoardType: (state.activeBoardType as ProjectBoardType | undefined) ?? 'main',

            activeEmployeeBoard: (state.activeEmployeeBoard as EmployeeBoardTab | undefined) ?? 'detailers',

            taskBoardVisibleStatuses: buildDefaultTaskBoardVisibleStatuses([
              ...DEFAULT_TASK_STATUSES,
              ...DEFAULT_DELIVERABLES_TASK_STATUSES,
            ]),

            clientsView: 'board' as const,

            customBoards: [],

            boardTaskStatuses: createDefaultBoardTaskStatuses(),

            projectBoardTaskStatuses: {},

            boardSheetColumns: createDefaultBoardSheetColumns(),

            boardSheetColumnOrder: createDefaultBoardSheetColumnOrder(),

            taskAttachments: [],

            taskComments: [],

            taskCommentReadAt: {},

            currentUserId: null,

            orgTeams: createDefaultOrgTeams(employees),

            employeePermissions: createDefaultEmployeePermissions(employees),

            timeEntries: [],

            employeeReportsTo: {},

            orgChartLevelSlots: {},

            employeeAssigneeStyles: createDefaultEmployeeAssigneeStyles(
              employees.map((employee) => employee.id)
            ),

            employeeCredentials: createDefaultEmployeeCredentials(
              employees.map((employee) => employee.id)
            ),

            dashboardAssignments: createDefaultDashboardAssignments(),

          };

        }



        let clients = state.clients ?? createSeedClients();

        let projects = (state.projects ?? createSeedProjects(clients)).map(normalizeProject);

        let taskGroups = state.taskGroups ?? [];
        if (version < 8) {
          taskGroups = renameDocumentsSectionGroups(taskGroups);
        }
        if (version < 19) {
          taskGroups = renameProjectManagementSectionGroups(taskGroups);
        }
        for (const project of projects) {

          taskGroups = ensureMainSections(taskGroups, project.clientId, project.id);

        }
        if (version < 9) {
          taskGroups = ensureProjectCoordinationGroups(taskGroups, projects);
        }

        let tasks = (state.tasks ?? []).map((t) => ({
          ...t,
          groupId: t.groupId ?? null,
          parentTaskId: (t as Task).parentTaskId ?? null,
        }));

        if (version < 17) {
          const ensured = ensureProjectTemplate(
            clients,
            projects,
            taskGroups,
            tasks,
            getEmployeeIds(employees)
          );
          clients = ensured.clients;
          projects = ensured.projects.map(normalizeProject);
          taskGroups = ensured.taskGroups;
          tasks = ensured.tasks;
        }

        if (version < 9) {
          tasks = assignProjectCoordinationTasks(taskGroups, tasks);
        }
        for (const project of projects) {
          taskGroups = ensureProjectCoordinationGroups(taskGroups, [project]);
        }
        tasks = assignProjectCoordinationTasks(taskGroups, tasks);
        if (version < 26) {
          tasks = restoreAssignedTaskBoardTypes(tasks, taskGroups);
        }
        if (version < 28) {
          tasks = assignUngroupedSectionTasks(taskGroups, tasks);
        }
        if (version < 29) {
          tasks = syncTaskBoardFromGroupPlacement(taskGroups, tasks);
          tasks = tasks.map((task) => {
            if (task.boardType === 'employee' || task.boardType !== 'main') return task;
            if (!task.groupId) return task;
            const branch = inferTaskBranchBoardType(task, taskGroups);
            return branch !== 'main' ? { ...task, boardType: branch } : task;
          });
        }
        if (version < 30) {
          projects = applyDefaultProjectTeams(projects.map(normalizeProject));
        }
        if (version < 31) {
          projects = applyDefaultProjectTeams(projects.map(normalizeProject));
        }
        if (version < 32) {
          projects = applyDefaultProjectTeams(projects.map(normalizeProject));
        }
        if (version < 33) {
          tasks = tasks.map((t) =>
            normalizeTaskAssignees(t as Task & { assigneeId?: string | null })
          );
        }
        projects = ensureDefaultProjectTeams(projects.map(normalizeProject));
        tasks = assignUngroupedSectionTasks(taskGroups, tasks);
        tasks = syncTaskBoardFromGroupPlacement(taskGroups, tasks);
        tasks = repairDetailersSpoolingMirror(tasks, taskGroups);
        if (version < 56) {
          tasks = migrateDeliverablesBoardStatuses(tasks, taskGroups);
          tasks = migrateBoardDefaultStatuses(tasks);
        }
        tasks = tasks.map(normalizeTaskFields);

        if (version < 15) {
          const normalized = normalizeEmployeesWithRemap(employees, tasks);
          employees = normalized.employees;
          tasks = normalized.tasks as Task[];
          projects = projects.map((p) => {
            const idMap = new Map<string, string>();
            for (const emp of state.employees ?? []) {
              const legacy = LEGACY_NAME_TO_ID[emp.name];
              if (legacy) idMap.set(emp.id, legacy);
            }
            if (idMap.size === 0) return p;
            return {
              ...p,
              detailerIds: p.detailerIds.map((id) => idMap.get(id) ?? id),
              supportIds: p.supportIds.map((id) => idMap.get(id) ?? id),
            };
          });
        }

        let subBoardTabOrder = normalizeSubBoardTabOrder(
          (state.subBoardTabOrder ?? []).filter(
            (id): id is BuiltInProjectBoardType => !isCustomBoardId(id)
          )
        );
        taskGroups = syncSectionSortOrder(taskGroups, subBoardTabOrder);

        for (const project of projects) {
          taskGroups = ensureCustomBoardSections(
            taskGroups,
            project.clientId,
            project.id,
            state.customBoards ?? [],
            subBoardTabOrder
          );
        }

        if (version < 35) {
          taskGroups = syncSectionDisplayNames(taskGroups, state.customBoards ?? []);
        }

        const taskStatuses = normalizeTaskStatuses(state.taskStatuses);

        let boardTaskStatuses = normalizeBoardTaskStatuses(
          state.boardTaskStatuses,
          taskStatuses
        );

        let projectBoardTaskStatuses = normalizeProjectBoardTaskStatuses(
          state.projectBoardTaskStatuses as ProjectBoardTaskStatusesMap | undefined
        );

        if (version < 18) {
          boardTaskStatuses = normalizeBoardTaskStatuses(undefined, taskStatuses);
        }

        for (const cb of state.customBoards ?? []) {
          if (!boardTaskStatuses[cb.id]?.length) {
            boardTaskStatuses = {
              ...boardTaskStatuses,
              [cb.id]: getBoardTaskStatuses('main', boardTaskStatuses).map((st) => ({ ...st })),
            };
          }
        }

        let boardSheetColumns = normalizeBoardSheetColumns(state.boardSheetColumns);

        if (version < 20) {
          boardSheetColumns = createDefaultBoardSheetColumns();
        }

        let boardSheetColumnOrder = normalizeBoardSheetColumnOrder(
          state.boardSheetColumnOrder,
          boardSheetColumns
        );

        if (version < 21) {
          boardSheetColumnOrder = createDefaultBoardSheetColumnOrder();
          for (const cb of state.customBoards ?? []) {
            boardSheetColumnOrder = {
              ...boardSheetColumnOrder,
              [cb.id]: defaultBoardColumnOrder(
                getBoardSheetColumns(cb.id, boardSheetColumns),
                false
              ),
            };
          }
        }

        for (const cb of state.customBoards ?? []) {
          if (!boardSheetColumns[cb.id]?.length) {
            boardSheetColumns = {
              ...boardSheetColumns,
              [cb.id]: getBoardSheetColumns('main', boardSheetColumns).map((c) => ({ ...c })),
            };
          }
          if (!boardSheetColumnOrder[cb.id]?.length) {
            boardSheetColumnOrder = {
              ...boardSheetColumnOrder,
              [cb.id]: defaultBoardColumnOrder(
                getBoardSheetColumns(cb.id, boardSheetColumns),
                false
              ),
            };
          }
        }

        if (version < 34) {
          tasks = tasks.map((task) =>
            applyAutoAssigneesToTask(task, projects, taskGroups, boardTaskStatuses)
          );
          boardTaskStatuses = {
            ...boardTaskStatuses,
            deliverables: DEFAULT_DELIVERABLES_TASK_STATUSES.map((s) => ({ ...s })),
          };
        }

        if (version < 36) {
          boardTaskStatuses = {
            ...boardTaskStatuses,
            deliverables: applyDeliverablesAutoAssignTeams(
              getBoardTaskStatuses('deliverables', boardTaskStatuses)
            ),
          };
        }

        if (version < 38) {
          projects = projects.map((project) => normalizeProject(project as Project));
        }

        let orgTeams = (state.orgTeams as OrgTeam[] | undefined) ?? createDefaultOrgTeams(employees);
        let employeePermissions =
          (state.employeePermissions as EmployeePermissionsMap | undefined) ??
          createDefaultEmployeePermissions(employees);
        let timeEntries = (state.timeEntries as TimeEntry[] | undefined) ?? [];
        let employeeReportsTo = normalizeEmployeeReportsTo(
          state.employeeReportsTo as EmployeeReportsToMap | Record<string, string | null> | undefined
        );

        if (version < 40) {
          orgTeams = createDefaultOrgTeams(employees);
          employeePermissions = createDefaultEmployeePermissions(employees);
          timeEntries = [];
          employeeReportsTo = {};
        }

        if (version < 41) {
          employeeReportsTo = employeeReportsTo ?? {};
        }

        if (version < 42) {
          employeeReportsTo = normalizeEmployeeReportsTo(
            state.employeeReportsTo as Record<string, string | null | string[]>
          );
        }

        if (version < 43) {
          const defaultById = new Map(seedEmployees.map((employee) => [employee.id, employee]));
          employees = employees.map((employee) => ({
            ...employee,
            orgCategory:
              employee.orgCategory ??
              defaultById.get(employee.id)?.orgCategory ??
              inferOrgCategory(employee),
          }));
          orgTeams = migrateOrgTeamsToCategories(orgTeams, employees);
        }

        const JESSE_VASQUEZ_ID = 'emp-support-4';

        if (version < 44) {
          employees = employees.map((employee) =>
            employee.id === JESSE_VASQUEZ_ID
              ? { ...employee, role: 'detailer', orgCategory: 'mechanical-detailer' }
              : employee
          );

          orgTeams = orgTeams.map((team) => {
            if (team.id === 'team-support-specialists') {
              return {
                ...team,
                memberIds: team.memberIds.filter((memberId) => memberId !== JESSE_VASQUEZ_ID),
              };
            }
            if (team.id === 'team-mechanical-detailers') {
              return team.memberIds.includes(JESSE_VASQUEZ_ID)
                ? team
                : { ...team, memberIds: [...team.memberIds, JESSE_VASQUEZ_ID] };
            }
            return team;
          });

          employeePermissions = {
            ...employeePermissions,
            [JESSE_VASQUEZ_ID]: [
              ...new Set<AppPermission>([
                ...(employeePermissions[JESSE_VASQUEZ_ID] ?? []),
                'view-org-chart',
                'edit-budget-hours',
              ]),
            ],
          };
        }

        if (version < 45) {
          const defaultById = new Map(seedEmployees.map((employee) => [employee.id, employee]));
          employees = employees.map((employee) => {
            const defaults = defaultById.get(employee.id);
            if (!defaults) return employee;
            return {
              ...employee,
              role: defaults.role,
              orgCategory: defaults.orgCategory,
            };
          });
          employeeReportsTo = createDefaultEmployeeReportsTo();
          orgTeams = createDefaultOrgTeams(employees);
        }

        let orgChartLevelSlots = normalizeOrgChartLevelSlots(
          state.orgChartLevelSlots as Record<
            string,
            Record<string, number> | string[] | (string | null)[]
          > | undefined
        );
        if (version < 46) {
          orgChartLevelSlots = {};
        }
        if (version < 49) {
          orgChartLevelSlots = normalizeOrgChartLevelSlots(orgChartLevelSlots);
        }
        if (version < 50) {
          orgChartLevelSlots = createDefaultOrgChartLevelSlots(
            employees.map((employee) => employee.id),
            employeeReportsTo
          );
        }
        if (version < 51) {
          orgChartLevelSlots = createDefaultOrgChartLevelSlots(
            employees.map((employee) => employee.id),
            employeeReportsTo
          );
        }

        const employeeIds = employees.map((employee) => employee.id);
        let employeeAssigneeStyles =
          (state.employeeAssigneeStyles as EmployeeAssigneeStylesMap | undefined) ??
          createDefaultEmployeeAssigneeStyles(employeeIds);
        let employeeCredentials =
          (state.employeeCredentials as EmployeeCredentialsMap | undefined) ??
          createDefaultEmployeeCredentials(employeeIds);

        if (version < 53) {
          const synced = syncEmployeeAuthAndColors(
            employeeIds,
            employeeAssigneeStyles,
            employeeCredentials,
            buildUniqueAssigneeStyles
          );
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
        }

        if (version < 55) {
          tasks = tasks.map((task) => {
            const { assigneesLocked: _locked, ...rest } = task as Task & { assigneesLocked?: boolean };
            return normalizeTaskFields(rest as Task);
          });
        }

        if (version < 56) {
          timeEntries = timeEntries.map((entry) => normalizeTimeEntry(entry as TimeEntry));
        }

        if (version < 57) {
          tasks = restoreAssignedTasksForTaskBoard(tasks);
        }

        if (version < 58) {
          tasks = injectSampleRfiAndDocumentTasks(tasks, projects, taskGroups);
        }

        for (const project of projects) {
          taskGroups = dedupeProjectSections(taskGroups, project.id);
        }
        if (version < 59) {
          const repaired = applyProjectStructureRepairs(projects, taskGroups, tasks, employees);
          taskGroups = repaired.taskGroups;
          tasks = repaired.tasks;
        } else if (version >= 59) {
          tasks = repairOrphanedTaskGroups(taskGroups, tasks);
          tasks = assignUngroupedSectionTasks(taskGroups, tasks);
        }

        if (version < 60) {
          const migrated = migrateBranchBoardTaskStructure(
            projects,
            taskGroups,
            tasks,
            getEmployeeIds(employees)
          );
          taskGroups = migrated.taskGroups;
          tasks = repairOrphanedTaskGroups(taskGroups, migrated.tasks);
          tasks = assignUngroupedSectionTasks(taskGroups, tasks);
        }

        if (version < 61) {
          const migrated = migrateBranchBoardTaskStructure(
            projects,
            taskGroups,
            tasks,
            getEmployeeIds(employees)
          );
          taskGroups = migrated.taskGroups;
          tasks = repairOrphanedTaskGroups(taskGroups, migrated.tasks);
          tasks = assignUngroupedSectionTasks(taskGroups, tasks);
        }

        if (version < 62) {
          const migrated = migrateBranchBoardTaskStructure(
            projects,
            taskGroups,
            tasks,
            getEmployeeIds(employees)
          );
          taskGroups = migrated.taskGroups;
          tasks = repairOrphanedTaskGroups(taskGroups, migrated.tasks);
          tasks = assignZoneTasksToChildGroups(tasks, taskGroups, projects);
          tasks = dedupeTasksByProjectTitle(tasks);
          tasks = assignUngroupedSectionTasks(taskGroups, tasks);
        }

        if (version < 63) {
          taskGroups = repairGroupTiers(taskGroups);
        }

        if (version < 64) {
          const tradeMigrated = migrateTradeBeforeLevelGroupStructure(
            projects,
            taskGroups,
            tasks
          );
          taskGroups = repairGroupTiers(tradeMigrated.taskGroups);
          tasks = assignZoneTasksToChildGroups(tradeMigrated.tasks, taskGroups, projects);
        }

        if (version < 65) {
          for (const project of projects) {
            taskGroups = dedupeProjectSections(taskGroups, project.id);
          }
          const tradeMigrated = migrateTradeBeforeLevelGroupStructure(
            projects,
            taskGroups,
            tasks
          );
          taskGroups = repairGroupTiers(tradeMigrated.taskGroups);
          tasks = repairOrphanedTaskGroups(taskGroups, tradeMigrated.tasks);
          tasks = reassignOrphanedBranchTasksToLevels(taskGroups, tasks, projects);
          tasks = assignZoneTasksToChildGroups(tasks, taskGroups, projects);
        }

        if (version < 66) {
          const rebuilt = rebuildTradeLevelSectionHierarchy(projects, taskGroups, tasks, {
            force: true,
          });
          taskGroups = repairGroupTiers(rebuilt.taskGroups);
          tasks = repairOrphanedTaskGroups(taskGroups, rebuilt.tasks);
          tasks = reassignOrphanedBranchTasksToLevels(taskGroups, tasks, projects);
          tasks = assignZoneTasksToChildGroups(tasks, taskGroups, projects);
        }

        if (version < 67) {
          const migrated = migrateTaskDurationsToLevelGroups(taskGroups, tasks);
          taskGroups = migrated.taskGroups;
          tasks = migrated.tasks;
        }

        if (version < 69) {
          tasks = repairTasksOnWrongBoardSection(tasks, taskGroups);
        }

        if (version < 70) {
          boardSheetColumnOrder = stripBoardColumnFromSubBoardOrders(boardSheetColumnOrder);
          tasks = repairTasksOnWrongBoardSection(tasks, taskGroups);
        }

        let taskCommentReadAt =
          (state.taskCommentReadAt as Record<string, string> | undefined) ?? {};

        if (version < 71) {
          taskCommentReadAt = {};
        }

        let taskBoardVisibleStatuses =
          (state.taskBoardVisibleStatuses as string[] | undefined) ?? [];

        if (version < 72 || taskBoardVisibleStatuses.length === 0) {
          const options = collectTaskBoardStatusOptions(
            boardTaskStatuses,
            state.projectBoardTaskStatuses ?? {},
            tasks
          );
          taskBoardVisibleStatuses = normalizeTaskBoardVisibleStatuses(
            taskBoardVisibleStatuses,
            options
          );
        }

        if (version < 75) {
          boardSheetColumnOrder = stripFlatBoardHiddenColumns(boardSheetColumnOrder);
          boardSheetColumns = {
            ...boardSheetColumns,
            documents: filterBoardCustomColumns('documents', boardSheetColumns.documents ?? []),
            rfi: filterBoardCustomColumns('rfi', boardSheetColumns.rfi ?? []),
          };
        }

        if (version < 77) {
          boardTaskStatuses = {
            ...boardTaskStatuses,
            rfi: normalizeRfiBoardTaskStatuses(boardTaskStatuses.rfi),
          };
          projectBoardTaskStatuses = normalizeProjectBoardTaskStatuses(
            state.projectBoardTaskStatuses as ProjectBoardTaskStatusesMap | undefined
          );
          tasks = migrateRfiBoardTaskStatuses(tasks);
        }

        if (version < 78) {
          const consolidated = consolidateDuplicateStatuses(
            boardTaskStatuses,
            projectBoardTaskStatuses,
            tasks,
            taskBoardVisibleStatuses
          );
          boardTaskStatuses = consolidated.boardTaskStatuses;
          projectBoardTaskStatuses = consolidated.projectBoardTaskStatuses;
          tasks = consolidated.tasks;
          taskBoardVisibleStatuses = consolidated.taskBoardVisibleStatuses;
          const synced = syncStatusColorMaps(boardTaskStatuses, projectBoardTaskStatuses);
          boardTaskStatuses = synced.boardTaskStatuses;
          projectBoardTaskStatuses = synced.projectBoardTaskStatuses;
        }

        let customBoards = (state.customBoards as CustomBoard[] | undefined) ?? [];
        let taskAttachments = (state.taskAttachments as TaskAttachment[] | undefined) ?? [];
        let taskComments = (state.taskComments as TaskComment[] | undefined) ?? [];
        let portfolioResetNavigation: { activeClientId: string; activeProjectId: string } | null = null;

        if (version < 79) {
          const reset = resetPortfolioToTemplate(
            clients,
            projects,
            taskGroups,
            tasks,
            customBoards,
            projectBoardTaskStatuses,
            timeEntries,
            getEmployeeIds(employees)
          );
          clients = reset.clients;
          projects = reset.projects.map(normalizeProject);
          taskGroups = reset.taskGroups;
          tasks = reset.tasks;
          customBoards = reset.customBoards;
          projectBoardTaskStatuses = reset.projectBoardTaskStatuses;
          timeEntries = reset.timeEntries;
          portfolioResetNavigation = {
            activeClientId: reset.activeClientId,
            activeProjectId: reset.activeProjectId,
          };
          const taskIds = new Set(tasks.map((task) => task.id));
          taskAttachments = taskAttachments.filter((attachment) => taskIds.has(attachment.taskId));
          taskComments = taskComments.filter((comment) => taskIds.has(comment.taskId));
        }

        if (version < 80) {
          const reset = resetPortfolioToTemplate(
            clients,
            projects,
            taskGroups,
            tasks,
            customBoards,
            projectBoardTaskStatuses,
            timeEntries,
            getEmployeeIds(employees)
          );
          clients = reset.clients;
          projects = reset.projects.map(normalizeProject);
          taskGroups = reset.taskGroups;
          tasks = reset.tasks;
          customBoards = reset.customBoards;
          projectBoardTaskStatuses = reset.projectBoardTaskStatuses;
          timeEntries = reset.timeEntries;
          portfolioResetNavigation = {
            activeClientId: reset.activeClientId,
            activeProjectId: reset.activeProjectId,
          };
          const taskIds = new Set(tasks.map((task) => task.id));
          taskAttachments = taskAttachments.filter((attachment) => taskIds.has(attachment.taskId));
          taskComments = taskComments.filter((comment) => taskIds.has(comment.taskId));
        }

        if (version < 81) {
          const pruned = pruneToClientTemplateOnly(
            clients,
            projects,
            taskGroups,
            tasks,
            customBoards,
            projectBoardTaskStatuses,
            timeEntries,
            getEmployeeIds(employees)
          );
          clients = pruned.clients;
          projects = pruned.projects.map(normalizeProject);
          taskGroups = pruned.taskGroups;
          tasks = pruned.tasks;
          customBoards = pruned.customBoards;
          projectBoardTaskStatuses = pruned.projectBoardTaskStatuses;
          timeEntries = pruned.timeEntries;
          portfolioResetNavigation = {
            activeClientId: pruned.activeClientId,
            activeProjectId: pruned.activeProjectId,
          };
          const taskIds = new Set(tasks.map((task) => task.id));
          taskAttachments = taskAttachments.filter((attachment) => taskIds.has(attachment.taskId));
          taskComments = taskComments.filter((comment) => taskIds.has(comment.taskId));
        }

        let dashboardAssignments =
          (state.dashboardAssignments as DashboardAssignments | undefined) ??
          createDefaultDashboardAssignments();

        if (version < 96) {
          employees = buildBimOrgRoster(employees);
          employeeReportsTo = createBimOrgChartReportsTo();
          orgTeams = createDefaultOrgTeams(employees);
          employeePermissions = createDefaultEmployeePermissions(employees);
          dashboardAssignments = mergeDashboardAssignments(
            dashboardAssignments,
            createSeededDashboardAssignments()
          );
          orgChartLevelSlots = createDefaultOrgChartLevelSlots(
            employees.map((employee) => employee.id),
            employeeReportsTo
          );
          const synced = syncEmployeeAuthAndColors(
            employees.map((employee) => employee.id),
            employeeAssigneeStyles,
            employeeCredentials,
            buildUniqueAssigneeStyles,
            employees
          );
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
        }

        if (version < 97) {
          subBoardTabOrder = [...DEFAULT_SUB_BOARD_TAB_ORDER];
          for (const project of projects) {
            taskGroups = ensureMainSections(taskGroups, project.clientId, project.id);
          }
          taskGroups = syncSectionSortOrder(taskGroups, subBoardTabOrder);
        }

        if (version < 98) {
          subBoardTabOrder = [...DEFAULT_SUB_BOARD_TAB_ORDER];

          tasks = tasks.map((task) =>
            (task.boardType as string) === 'spooling'
              ? { ...task, boardType: 'deliverables' as ProjectBoardType }
              : task
          );

          taskGroups = removeSpoolingBoardSections(taskGroups);

          boardTaskStatuses = {
            ...normalizeBoardTaskStatuses(boardTaskStatuses),
            deliverables: applyDeliverablesAutoAssignTeams(
              DEFAULT_DELIVERABLES_TASK_STATUSES.map((status) => ({ ...status }))
            ),
          };
          if ('spooling' in boardTaskStatuses) {
            const { spooling: _removed, ...rest } = boardTaskStatuses as BoardTaskStatusesMap & {
              spooling?: unknown;
            };
            boardTaskStatuses = rest;
          }

          taskGroups = syncSectionSortOrder(taskGroups, subBoardTabOrder);

          tasks = migrateTasksToBoardStatuses(
            tasks,
            boardTaskStatuses,
            taskGroups,
            projectBoardTaskStatuses
          );
          tasks = tasks.map((task) =>
            applyAutoAssigneesToTask(task, projects, taskGroups, boardTaskStatuses, projectBoardTaskStatuses)
          );

          taskBoardVisibleStatuses = buildDefaultTaskBoardVisibleStatuses([
            ...DEFAULT_TASK_STATUSES,
            ...DEFAULT_DELIVERABLES_TASK_STATUSES,
          ]);
        }

        if (version < 100) {
          subBoardTabOrder = [...DEFAULT_SUB_BOARD_TAB_ORDER];
          for (const project of projects) {
            taskGroups = ensureMainSections(taskGroups, project.clientId, project.id);
          }
          taskGroups = syncSectionSortOrder(taskGroups, subBoardTabOrder);
          boardTaskStatuses = {
            ...boardTaskStatuses,
            spooling: DEFAULT_SPOOLING_TASK_STATUSES.map((status) => ({ ...status })),
          };
        }

        if (version < 101) {
          const pmChecklist = applyTemplatePmBoardChecklist(projects, taskGroups, tasks);
          taskGroups = pmChecklist.taskGroups;
          tasks = pmChecklist.tasks;
        }

        if (version < 102) {
          const pmChecklist = applyTemplatePmBoardChecklist(projects, taskGroups, tasks);
          taskGroups = pmChecklist.taskGroups;
          tasks = pmChecklist.tasks;
        }

        if (version < 103) {
          const boardSamples = applyTemplateBoardSamples(projects, taskGroups, tasks);
          taskGroups = boardSamples.taskGroups;
          tasks = boardSamples.tasks;
        }

        if (version < 105) {
          const template = projects.find(
            (project) => project.name === TEMPLATE_PROJECT_NAME || project.isTemplate
          );
          if (template) {
            const reverted = revertTemplateExpandedLevels(template.id, taskGroups, tasks);
            taskGroups = reverted.taskGroups;
            tasks = reverted.tasks;
            projects = projects.map((project) =>
              project.id === template.id
                ? normalizeProject({
                    ...project,
                    buildingLevels: [],
                    activeLevels: [],
                  })
                : project
            );
          }
        }

        if (version < 106) {
          boardSheetColumns = syncMainOverviewColumnsToAllBoards(
            boardSheetColumns,
            getAllConfiguredBoardTypes(state.customBoards ?? [])
          );
        }

        if (version < 107) {
          subBoardTabOrder = normalizeSubBoardTabOrder(DEFAULT_SUB_BOARD_TAB_ORDER);
          const workflowDueDates = applyWorkflowDueDateColumns(
            boardSheetColumns,
            boardSheetColumnOrder
          );
          boardSheetColumns = workflowDueDates.boardSheetColumns;
          boardSheetColumnOrder = workflowDueDates.boardSheetColumnOrder;
        }

        if (version < 111) {
          const premade = ensurePremadeSheetColumns(boardSheetColumns, boardSheetColumnOrder);
          boardSheetColumns = premade.boardSheetColumns;
          boardSheetColumnOrder = premade.boardSheetColumnOrder;
        }

        if (version < 113) {
          employees = mergeDepartmentStaff(employees);
          dashboardAssignments = mergeDashboardAssignments(
            dashboardAssignments,
            createSeededDashboardAssignments()
          );
          employeeReportsTo = {
            ...employeeReportsTo,
            'emp-fab-8': employeeReportsTo['emp-fab-8']?.length
              ? employeeReportsTo['emp-fab-8']
              : ['emp-fab-1'],
            'emp-fab-9': ['emp-fab-8'],
            'emp-fab-10': ['emp-fab-8'],
          };
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          for (const staffId of ['emp-fab-8', 'emp-fab-9', 'emp-fab-10'] as const) {
            const granted = new Set(nextPermissions[staffId] ?? []);
            for (const permission of operationsDashboardPermissions(staffId)) {
              granted.add(permission);
            }
            nextPermissions[staffId] = [...granted];
          }
          employeePermissions = nextPermissions;
          const synced = syncEmployeeAuthAndColors(
            employees.map((employee) => employee.id),
            employeeAssigneeStyles,
            employeeCredentials,
            buildUniqueAssigneeStyles,
            employees
          );
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
        }

        let visibilityDashboardJobLevels =
          (state.visibilityDashboardJobLevels as OrgCategory[] | undefined) ??
          [...DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS];

        if (version < 114) {
          visibilityDashboardJobLevels = [...DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS];
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          for (const employee of employees) {
            const category = inferOrgCategory(employee);
            if (
              category === 'owner' ||
              category === 'bim-manager' ||
              category === 'operations-manager'
            ) {
              const granted = new Set(nextPermissions[employee.id] ?? []);
              granted.add('view-visibility-dashboard');
              nextPermissions[employee.id] = [...granted];
            }
          }
          employeePermissions = nextPermissions;
        }

        let jobLevelNavVisibility = normalizeJobLevelNavVisibility(
          state.jobLevelNavVisibility as JobLevelNavVisibilityMap | undefined
        );

        if (version < 115) {
          jobLevelNavVisibility = normalizeJobLevelNavVisibility(undefined);
        }

        if (version < 116) {
          jobLevelNavVisibility = normalizeJobLevelNavVisibility(undefined);
          employeePermissions = syncOpsDashboardPermissionsFromDefaults(
            employees,
            employeePermissions,
            dashboardAssignments,
            jobLevelNavVisibility
          );
        }

        if (version < 117) {
          jobLevelNavVisibility = normalizeJobLevelNavVisibility(jobLevelNavVisibility);
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          for (const employee of employees) {
            const granted = new Set(nextPermissions[employee.id] ?? []);
            granted.add('view-time-tracking');
            nextPermissions[employee.id] = [...granted];
          }
          employeePermissions = nextPermissions;
        }

        let deletedEmployeeArchive =
          (state.deletedEmployeeArchive as DeletedEmployeeArchive[] | undefined) ?? [];
        let activityLog =
          (state.activityLog as ActivityLogEntry[] | undefined) ?? [];

        if (version < 118) {
          // Re-seed missing BIM leadership (e.g. Priya Shah) after accidental roster deletes.
          employees = buildBimOrgRoster(employees);
          const seedReports = createBimOrgChartReportsTo();
          employeeReportsTo = {
            ...seedReports,
            ...employeeReportsTo,
            'emp-bim-mgr-1': employeeReportsTo['emp-bim-mgr-1']?.length
              ? employeeReportsTo['emp-bim-mgr-1']
              : seedReports['emp-bim-mgr-1'] ?? [JOE_VASQUEZ_ID],
            'emp-ops-mgr-1': employeeReportsTo['emp-ops-mgr-1']?.length
              ? employeeReportsTo['emp-ops-mgr-1']
              : seedReports['emp-ops-mgr-1'] ?? [JOE_VASQUEZ_ID],
          };
          const defaults = createDefaultEmployeePermissions(employees);
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          for (const employeeId of ['emp-bim-mgr-1', 'emp-ops-mgr-1'] as const) {
            nextPermissions[employeeId] = [
              ...new Set([...(nextPermissions[employeeId] ?? []), ...(defaults[employeeId] ?? [])]),
            ];
          }
          employeePermissions = nextPermissions;
          const synced = syncEmployeeAuthAndColors(
            employees.map((employee) => employee.id),
            employeeAssigneeStyles,
            employeeCredentials,
            buildUniqueAssigneeStyles,
            employees
          );
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
        }

        if (version < 119) {
          // Force-restore seed leadership if deleted after v118 (Priya Shah / Derek Coleman).
          const seedIds = ['emp-bim-mgr-1', 'emp-ops-mgr-1'] as const;
          let nextArchive = [...deletedEmployeeArchive];
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          let nextReports = { ...employeeReportsTo };
          let nextStyles = { ...employeeAssigneeStyles };
          let nextCredentials = { ...employeeCredentials };
          const restoredNames: string[] = [];

          for (const seedId of seedIds) {
            if (employees.some((employee) => employee.id === seedId)) continue;

            const archive = nextArchive.find(
              (entry) => entry.employee.id === seedId && !entry.restoredAt
            );
            if (archive) {
              employees = [...employees, { ...archive.employee }];
              nextPermissions[seedId] = [...archive.permissions];
              nextReports[seedId] = [...archive.reportsTo];
              if (archive.assigneeStyle) nextStyles[seedId] = archive.assigneeStyle;
              if (archive.credentials) nextCredentials[seedId] = archive.credentials;
              nextArchive = nextArchive.map((entry) =>
                entry.id === archive.id
                  ? {
                      ...entry,
                      restoredAt: new Date().toISOString(),
                      restoredById: null,
                    }
                  : entry
              );
              restoredNames.push(archive.employee.name);
            }
          }

          const beforeIds = new Set(employees.map((employee) => employee.id));
          employees = buildBimOrgRoster(employees);
          for (const employee of employees) {
            if (
              (seedIds as readonly string[]).includes(employee.id) &&
              !beforeIds.has(employee.id)
            ) {
              restoredNames.push(employee.name);
            }
          }

          const seedReports = createBimOrgChartReportsTo();
          const defaults = createDefaultEmployeePermissions(employees);
          for (const seedId of seedIds) {
            nextReports[seedId] = nextReports[seedId]?.length
              ? nextReports[seedId]!
              : seedReports[seedId] ?? [JOE_VASQUEZ_ID];
            nextPermissions[seedId] = [
              ...new Set([...(nextPermissions[seedId] ?? []), ...(defaults[seedId] ?? [])]),
            ];
          }

          const synced = syncEmployeeAuthAndColors(
            employees.map((employee) => employee.id),
            nextStyles,
            nextCredentials,
            buildUniqueAssigneeStyles,
            employees
          );
          employeePermissions = nextPermissions;
          employeeReportsTo = nextReports;
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
          deletedEmployeeArchive = nextArchive;

          for (const name of [...new Set(restoredNames)]) {
            const employee = employees.find((entry) => entry.name === name);
            activityLog = logActivity(
              activityLog,
              {
                actorId: null,
                action: 'restored',
                entityType: 'employee',
                entityId: employee?.id ?? name,
                summary: `Restored employee "${name}" to the roster`,
                details: { name, reason: 'seed-leadership-repair' },
              },
              uuid
            );
          }
        }

        let employeeJobTitles = normalizeEmployeeJobTitles(
          (state as { employeeJobTitles?: EmployeeJobTitleDef[] }).employeeJobTitles
        );

        if (version < 120) {
          employeeJobTitles = createDefaultEmployeeJobTitles();
          employees = backfillEmployeeJobTitleIds(employees, dashboardAssignments, employeeJobTitles);
        } else {
          employees = backfillEmployeeJobTitleIds(employees, dashboardAssignments, employeeJobTitles);
        }

        if (version < 121) {
          // Nest fab workers under their dept managers for Fabrication workstations.
          const fabWorkerReports: Record<string, string[]> = {
            'emp-fab-5': ['emp-fab-2'],
            'emp-fab-6': ['emp-fab-3'],
            'emp-fab-7': ['emp-fab-4'],
          };
          employeeReportsTo = { ...employeeReportsTo };
          for (const [workerId, managers] of Object.entries(fabWorkerReports)) {
            const current = employeeReportsTo[workerId] ?? [];
            if (current.length === 1 && current[0] === 'emp-fab-1') {
              employeeReportsTo[workerId] = managers;
            }
          }
        }

        if (version < 122) {
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          for (const employee of employees) {
            const granted = new Set(nextPermissions[employee.id] ?? []);
            for (const permission of defaultDashboardEditPermissionsForCategory(
              inferOrgCategory(employee)
            )) {
              granted.add(permission);
            }
            nextPermissions[employee.id] = [...granted];
          }
          employeePermissions = nextPermissions;
          jobLevelNavVisibility = normalizeJobLevelNavVisibility(undefined);
        }

        if (version < 123) {
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          for (const employee of employees) {
            const granted = new Set(nextPermissions[employee.id] ?? []);
            for (const permission of operationsDashboardPermissions(employee.id)) {
              granted.add(permission);
            }
            if (granted.has('view-fab-dashboard') || granted.has('edit-weld-log')) {
              granted.add('view-weld-log-dashboard');
            }
            if (granted.has('view-field-dashboard')) {
              granted.add('view-weld-log-dashboard');
            }
            const category = inferOrgCategory(employee);
            if (
              category === 'owner' ||
              category === 'bim-manager' ||
              category === 'operations-manager'
            ) {
              granted.add('view-weld-log-dashboard');
            }
            nextPermissions[employee.id] = [...granted];
          }
          employeePermissions = nextPermissions;
          jobLevelNavVisibility = normalizeJobLevelNavVisibility(jobLevelNavVisibility);
        }

        if (version < 124) {
          const premade = ensurePremadeSheetColumns(boardSheetColumns, boardSheetColumnOrder);
          boardSheetColumns = premade.boardSheetColumns;
          boardSheetColumnOrder = premade.boardSheetColumnOrder;
        }

        if (version < 125) {
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          for (const employee of employees) {
            const granted = new Set(nextPermissions[employee.id] ?? []);
            const category = inferOrgCategory(employee);
            if (
              category === 'owner' ||
              category === 'bim-manager' ||
              category === 'operations-manager' ||
              category === 'support-manager' ||
              category === 'support-specialist' ||
              employee.role === 'support-specialist'
            ) {
              granted.add('view-spooling-dashboard');
            }
            nextPermissions[employee.id] = [...granted];
          }
          employeePermissions = nextPermissions;
        }

        if (version < 127) {
          boardTaskStatuses = {
            ...boardTaskStatuses,
            detailers: ensureDetailersBoardStatuses(
              boardTaskStatuses.detailers?.length
                ? boardTaskStatuses.detailers
                : createDefaultBoardTaskStatuses().detailers!
            ),
          };
          const nextProjectStatuses: ProjectBoardTaskStatusesMap = { ...projectBoardTaskStatuses };
          for (const [projectId, boards] of Object.entries(nextProjectStatuses)) {
            if (!boards?.detailers?.length) continue;
            nextProjectStatuses[projectId] = {
              ...boards,
              detailers: ensureDetailersBoardStatuses(boards.detailers),
            };
          }
          projectBoardTaskStatuses = nextProjectStatuses;
          tasks = tasks.map((task) => {
            if (task.boardType !== 'detailers') return task;
            const nextStatus = migrateDetailersTaskStatus(task.status);
            return nextStatus === task.status ? task : { ...task, status: nextStatus };
          });
        }

        if (version < 128) {
          tasks = tasks.map((task) => {
            if (task.status !== 'ready-for-spooling') return task;
            return applyAutoAssigneesToTask(
              task,
              projects,
              taskGroups,
              boardTaskStatuses,
              projectBoardTaskStatuses,
              { force: true, employees, employeeJobTitles }
            );
          });
        }

        if (version < 129) {
          // Fab demote / export loops used to wipe bbMirrorDetailers — restore dual visibility.
          tasks = repairDetailersSpoolingMirror(tasks, taskGroups);
        }

        if (version < 130) {
          // Detailers boardType yanked by group sync while status stayed Ready for Spooling.
          tasks = repairDetailersSpoolingMirror(tasks, taskGroups);
        }

        if (version < 131) {
          // SSv3 assemblies should inherit Trade / Material from the main package task.
          tasks = syncAssemblyTradeMaterialFromPackageRoots(tasks);
        }

        if (version < 110) {
          const nextPermissions: EmployeePermissionsMap = { ...employeePermissions };
          for (const employee of employees) {
            const category = inferOrgCategory(employee);
            if (
              category === 'owner' ||
              category === 'bim-manager' ||
              category === 'operations-manager'
            ) {
              const granted = new Set(nextPermissions[employee.id] ?? []);
              granted.add('manage-columns');
              granted.add('view-activity-log');
              nextPermissions[employee.id] = [...granted];
            }
          }
          employeePermissions = nextPermissions;
        }

        if (version < 109) {
          const materialColumn = applyTemplateMaterialColumn(
            boardSheetColumns,
            boardSheetColumnOrder
          );
          boardSheetColumns = materialColumn.boardSheetColumns;
          boardSheetColumnOrder = materialColumn.boardSheetColumnOrder;
        }

        if (version < 95) {
          boardTaskStatuses = createDefaultBoardTaskStatuses();
          tasks = migrateTasksToBoardStatuses(
            tasks,
            boardTaskStatuses,
            taskGroups,
            projectBoardTaskStatuses
          );
          tasks = migrateRfiBoardTaskStatuses(tasks);
          projects = projects.map((project) => normalizeProject({ ...project, pmIds: project.pmIds ?? [] }));
          dashboardAssignments = mergeDashboardAssignments(
            dashboardAssignments,
            createSeededDashboardAssignments()
          );
          employeePermissions = createDefaultEmployeePermissions(employees);
        }

        if (version < 94) {
          let customBoardsForReset = customBoards;
          const reset = resetTemplateToEmptyBoards(projects, taskGroups, tasks);
          projects = reset.projects.map(normalizeProject);
          taskGroups = reset.taskGroups;
          tasks = reset.tasks as Task[];

          const templateProject = projects.find(
            (project) => project.name === TEMPLATE_PROJECT_NAME || project.isTemplate
          );
          if (templateProject) {
            customBoardsForReset = customBoards.filter(
              (board) => board.projectId !== templateProject.id
            );
            const { [templateProject.id]: _removed, ...restStatuses } = projectBoardTaskStatuses;
            projectBoardTaskStatuses = restStatuses;
          }
          customBoards = customBoardsForReset;

          const taskIds = new Set(tasks.map((task) => task.id));
          taskAttachments = taskAttachments.filter((attachment) => taskIds.has(attachment.taskId));
          taskComments = taskComments.filter((comment) => taskIds.has(comment.taskId));
        }

        if (version < 92) {
          employees = mergeDepartmentStaff(employees);
          orgTeams = createDefaultOrgTeams(employees);
          employeePermissions = createDefaultEmployeePermissions(employees);

          dashboardAssignments = mergeDashboardAssignments(
            dashboardAssignments,
            createSeededDashboardAssignments()
          );

          const synced = syncEmployeeAuthAndColors(
            employees.map((employee) => employee.id),
            employeeAssigneeStyles,
            employeeCredentials,
            buildUniqueAssigneeStyles,
            employees
          );
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
        }

        if (version < 91) {
          const normalized = normalizeEmployeesWithRemap(employees, tasks);
          employees = normalized.employees;
          tasks = normalized.tasks as Task[];
          orgTeams = createDefaultOrgTeams(employees);
          employeePermissions = createDefaultEmployeePermissions(employees);

          employeeReportsTo = {
            ...employeeReportsTo,
            [JOE_VASQUEZ_ID]: [],
            [TAYLOR_MORGAN_ID]: [JOE_VASQUEZ_ID],
            'emp-support-2': [TAYLOR_MORGAN_ID],
            'emp-support-3': [TAYLOR_MORGAN_ID],
            'emp-support-4': [TAYLOR_MORGAN_ID],
            'emp-support-5': [TAYLOR_MORGAN_ID],
          };

          const synced = syncEmployeeAuthAndColors(
            employees.map((employee) => employee.id),
            employeeAssigneeStyles,
            employeeCredentials,
            buildUniqueAssigneeStyles,
            employees
          );
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
        }

        if (version < 90) {
          const normalized = normalizeEmployeesWithRemap(employees, tasks);
          employees = normalized.employees;
          tasks = normalized.tasks as Task[];
          orgTeams = createDefaultOrgTeams(employees);
          employeePermissions = createDefaultEmployeePermissions(employees);

          employeeReportsTo = {
            ...employeeReportsTo,
            [JOE_VASQUEZ_ID]: [],
            [TAYLOR_MORGAN_ID]: [JOE_VASQUEZ_ID],
            'emp-support-2': [TAYLOR_MORGAN_ID],
            'emp-support-3': [TAYLOR_MORGAN_ID],
            'emp-support-4': [TAYLOR_MORGAN_ID],
            'emp-support-5': [TAYLOR_MORGAN_ID],
          };

          projects = projects.map((project) =>
            normalizeProject({
              ...project,
              jobCode: project.isTemplate ? null : project.jobCode ?? null,
            })
          );

          const numbered = backfillTaskNumbers(tasks, projects);
          tasks = numbered.tasks;
          projects = numbered.projects;

          boardTaskStatuses = normalizeBoardTaskStatuses(boardTaskStatuses);
          subBoardTabOrder = normalizeSubBoardTabOrder([
            ...subBoardTabOrder,
            'field',
            'fab',
            'shipping',
          ]);

          dashboardAssignments = createDefaultDashboardAssignments();
        }

        if (version < 88) {
          const normalized = normalizeEmployeesWithRemap(employees, tasks);
          employees = normalized.employees;
          tasks = normalized.tasks as Task[];
          orgTeams = createDefaultOrgTeams(employees);
          employeePermissions = createDefaultEmployeePermissions(employees);
          const synced = syncEmployeeAuthAndColors(
            employees.map((employee) => employee.id),
            employeeAssigneeStyles,
            employeeCredentials,
            buildUniqueAssigneeStyles
          );
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
          projects = ensureDefaultProjectTeams(projects.map(normalizeProject));
        }

        if (version < 87) {
          employeeCredentials = {
            ...employeeCredentials,
            [JOE_VASQUEZ_ID]: createJoeVasquezCredential(employeeCredentials[JOE_VASQUEZ_ID]),
          };
        }

        if (version < 86) {
          const normalized = normalizeEmployeesWithRemap(employees, tasks);
          employees = normalized.employees;
          tasks = normalized.tasks as Task[];
        }

        let migrationCurrentUserId = (state.currentUserId as string | undefined) ?? null;

        if (version < 85) {
          const rosterSource =
            employees.length <= 1 ? [...DEFAULT_EMPLOYEES] : employees;
          const normalized = normalizeEmployeesWithRemap(rosterSource, tasks);
          employees = normalized.employees;
          tasks = normalized.tasks as Task[];
          orgTeams = createDefaultOrgTeams(employees);
          employeePermissions = createDefaultEmployeePermissions(employees);
          const synced = syncEmployeeAuthAndColors(
            employees.map((employee) => employee.id),
            employeeAssigneeStyles,
            employeeCredentials,
            buildUniqueAssigneeStyles
          );
          employeeAssigneeStyles = synced.employeeAssigneeStyles;
          employeeCredentials = synced.employeeCredentials;
          projects = ensureDefaultProjectTeams(projects.map(normalizeProject));
          if (
            migrationCurrentUserId &&
            !employees.some((employee) => employee.id === migrationCurrentUserId)
          ) {
            migrationCurrentUserId = JOE_VASQUEZ_ID;
          }
        }

        const currentUserId = migrationCurrentUserId;

        const resolvedNavigation = resolvePersistedNavigation(
          state as Partial<AppState>,
          {
            activeMainTab: 'clients',
            activeClientId: clients[0]?.id ?? null,
            activeProjectId: projects.find((project) => project.clientId === clients[0]?.id)?.id ?? null,
            activeBoardType: 'main',
            activeEmployeeBoard: 'detailers',
            clientsView: 'board' as const,
          },
          clients,
          projects,
          customBoards,
          subBoardTabOrder
        );

        if (
          resolvedNavigation.activeMainTab === 'org-chart' &&
          !canAccessOrgChart(currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'owner-dashboard' &&
          !canViewOwnerDashboard(currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'pm-dashboard' &&
          !canViewDashboard('pm', currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'field-dashboard' &&
          !canViewDashboard('field', currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'fab-dashboard' &&
          !canViewDashboard('fab', currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'shipping-dashboard' &&
          !canViewDashboard('shipping', currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'weld-log-dashboard' &&
          !canViewWeldLogDashboard(currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'spooling-dashboard' &&
          !canViewSpoolingDashboard(currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'activity-log' &&
          !canViewActivityLog(currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'time-tracking' &&
          !canViewTimeTracking(currentUserId, employees, employeePermissions)
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (
          resolvedNavigation.activeMainTab === 'visibility-dashboard' &&
          !canViewVisibilityDashboard(
            currentUserId,
            employees,
            employeePermissions,
            visibilityDashboardJobLevels
          )
        ) {
          resolvedNavigation.activeMainTab = 'clients';
        }

        if (portfolioResetNavigation) {
          resolvedNavigation.activeClientId = portfolioResetNavigation.activeClientId;
          resolvedNavigation.activeProjectId = portfolioResetNavigation.activeProjectId;
        }

        const sectionSheetColumns = normalizeBoardSheetColumns(
          (state.mainOverviewSectionSheetColumns as BoardSheetColumnsMap | undefined) ?? {}
        );
        let sectionColumnOrder = ensurePremadeInMainOverviewSectionOrders(
          normalizeMainOverviewSectionColumnOrder(
            (state.mainOverviewSectionColumnOrder as BoardSheetColumnOrderMap | undefined) ?? {},
            sectionSheetColumns,
            boardSheetColumns
          )
        );

        // Ghost boards (Field/Shipping/Fab/Spooling/…) display section column order. Repair
        // Trade/Material if they were only stuck on boardSheetColumnOrder (Column Settings bug).
        {
          const repaired = repairWorkflowTradeMaterialColumnVisibility(
            boardSheetColumns,
            boardSheetColumnOrder,
            sectionColumnOrder,
            sectionSheetColumns
          );
          boardSheetColumns = repaired.boardSheetColumns;
          boardSheetColumnOrder = repaired.boardSheetColumnOrder;
          sectionColumnOrder = repaired.mainOverviewSectionColumnOrder;
        }

        return {
          ...state,
          clients,
          projects,
          employees,
          tasks,
          taskGroups,
          subBoardTabOrder,
          customBoards,
          boardTaskStatuses,
          projectBoardTaskStatuses,
          boardSheetColumns,
          boardSheetColumnOrder,
          mainOverviewSectionSheetColumns: sectionSheetColumns,
          mainOverviewSectionColumnOrder: sectionColumnOrder,
          taskAttachments,
          taskComments,
          taskCommentReadAt,
          taskBoardVisibleStatuses,
          historyPast: [],
          historyFuture: [],
          currentUserId,
          orgTeams,
          employeePermissions,
          visibilityDashboardJobLevels,
          jobLevelNavVisibility,
          timeEntries,
          employeeReportsTo,
          orgChartLevelSlots,
          employeeAssigneeStyles,
          employeeCredentials,
          dashboardAssignments,
          employeeJobTitles,
          activityLog,
          deletedColumnArchive:
            (state.deletedColumnArchive as DeletedColumnArchive[] | undefined) ?? [],
          deletedEmployeeArchive,
          deletedTaskArchive:
            (state.deletedTaskArchive as DeletedTaskArchive[] | undefined) ?? [],
          taskRevisionArchive:
            (state.taskRevisionArchive as TaskRevisionArchive[] | undefined) ?? [],
          ...resolvedNavigation,
        };
}

/** Blocks first-boot writes until rehydration so empty defaults cannot wipe disk.
 * Session flag survives Vite HMR so in-progress edits keep saving after hot reload. */
let storePersistHydrated =
  typeof window !== 'undefined' && Boolean((window as Window & { __BIM_PERSIST_READY__?: boolean }).__BIM_PERSIST_READY__);

let pendingPersistWrite: { name: string; value: string } | null = null;

function markStorePersistHydrated() {
  storePersistHydrated = true;
  if (typeof window !== 'undefined') {
    (window as Window & { __BIM_PERSIST_READY__?: boolean }).__BIM_PERSIST_READY__ = true;
  }
  try {
    installDurableStoreFlushHooks();
  } catch {
    /* ignore */
  }
  if (pendingPersistWrite) {
    const pending = pendingPersistWrite;
    pendingPersistWrite = null;
    void durableStoreStorage.setItem(pending.name, pending.value);
  }
  queueMicrotask(() => {
    try {
      ensureDemoPortfolio(useStore);
    } catch (ensureError) {
      console.error('ensureDemoPortfolio failed', ensureError);
    }
  });
}

export const useStore = create<AppState>()(

  persist(

    (set, get) => {

      const { clients, projects, groups, tasks, templateClient, templateProject } = createInitialPortfolio();



      return {

        clients,

        projects,

        employees: seedEmployees,

        tasks,

        taskGroups: groups,

        customBoards: [],

        boardTaskStatuses: createDefaultBoardTaskStatuses(),

        projectBoardTaskStatuses: {},

        boardSheetColumns: createDefaultBoardSheetColumns(),

        boardSheetColumnOrder: createDefaultBoardSheetColumnOrder(),

        mainOverviewSectionColumnOrder: {},

        mainOverviewSectionSheetColumns: {},

        taskAttachments: [],

        taskComments: [],

        taskCommentReadAt: {},

        subBoardTabOrder: [...DEFAULT_SUB_BOARD_TAB_ORDER],

        taskClipboard: null,

        sheetDragActive: false,

        sheetDragHoverBoard: null,

        historyPast: [],
        historyFuture: [],

        activityLog: [],
        deletedColumnArchive: [],
        deletedEmployeeArchive: [],
        deletedTaskArchive: [],
        taskRevisionArchive: [],

        savedSheetColumnTemplates: [],

        columnSettingsDropdownIds: normalizeColumnSettingsDropdownIds(),

        activeMainTab: 'clients',

        activeClientId: templateClient.id,

        activeProjectId: templateProject.id,

        activeBoardType: 'main',

        activeEmployeeBoard: 'detailers',

        taskBoardVisibleStatuses: buildDefaultTaskBoardVisibleStatuses([
          ...DEFAULT_TASK_STATUSES,
          ...DEFAULT_DELIVERABLES_TASK_STATUSES,
        ]),

        clientsView: 'board' as const,

        fieldFocusProjectId: null as string | null,

        currentUserId: null,

        viewAsOriginalUserId: null,

        orgTeams: createDefaultOrgTeams(seedEmployees),

        employeePermissions: createDefaultEmployeePermissions(seedEmployees),

        visibilityDashboardJobLevels: [...DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS],

        jobLevelNavVisibility: normalizeJobLevelNavVisibility(undefined),

        employeeReportsTo: createBimOrgChartReportsTo(),

        orgChartLevelSlots: createDefaultOrgChartLevelSlots(
          seedEmployees.map((employee) => employee.id),
          createBimOrgChartReportsTo()
        ),

        timeEntries: [],

        employeeAssigneeStyles: createDefaultEmployeeAssigneeStyles(
          seedEmployees.map((employee) => employee.id)
        ),

        employeeCredentials: createDefaultEmployeeCredentials(
          seedEmployees.map((employee) => employee.id)
        ),

        dashboardAssignments: createDefaultDashboardAssignments(),

        employeeJobTitles: createDefaultEmployeeJobTitles(),

        setActiveMainTab: (tab) => {
          const state = get();
          if (
            tab === 'owner-dashboard' &&
            !canViewOwnerDashboard(state.currentUserId, state.employees, state.employeePermissions)
          ) {
            return;
          }
          if (
            tab === 'pm-dashboard' &&
            !canViewDashboard('pm', state.currentUserId, state.employees, state.employeePermissions)
          ) {
            return;
          }
          if (
            tab === 'field-dashboard' &&
            !canViewDashboard('field', state.currentUserId, state.employees, state.employeePermissions)
          ) {
            return;
          }
          if (
            tab === 'fab-dashboard' &&
            !canViewDashboard('fab', state.currentUserId, state.employees, state.employeePermissions)
          ) {
            return;
          }
          if (
            tab === 'shipping-dashboard' &&
            !canViewDashboard('shipping', state.currentUserId, state.employees, state.employeePermissions)
          ) {
            return;
          }
          if (
            tab === 'weld-log-dashboard' &&
            !canViewWeldLogDashboard(
              state.currentUserId,
              state.employees,
              state.employeePermissions
            )
          ) {
            return;
          }
          if (
            tab === 'spooling-dashboard' &&
            !canViewSpoolingDashboard(
              state.currentUserId,
              state.employees,
              state.employeePermissions
            )
          ) {
            return;
          }
          if (
            tab === 'visibility-dashboard' &&
            !canViewVisibilityDashboard(
              state.currentUserId,
              state.employees,
              state.employeePermissions,
              state.visibilityDashboardJobLevels
            )
          ) {
            return;
          }
          if (
            tab === 'time-tracking' &&
            !canViewTimeTracking(state.currentUserId, state.employees, state.employeePermissions)
          ) {
            return;
          }
          if (
            tab === 'org-chart' &&
            !canAccessOrgChart(state.currentUserId, state.employees, state.employeePermissions)
          ) {
            return;
          }
          set({
            activeMainTab: tab,
            ...(tab === 'clients' ? { clientsView: 'board' as const } : {}),
          });
        },

        setActiveClientId: (id) => {

          const projectsForClient = get().projects.filter((p) => p.clientId === id);

          set({

            activeClientId: id,

            activeProjectId: projectsForClient[0]?.id ?? null,

            activeBoardType: 'main',

            clientsView: 'board',

          });

        },

        setActiveProjectId: (id) =>
          set({ activeProjectId: id, activeBoardType: 'main', clientsView: 'board' }),

        setActiveBoardType: (type) => set({ activeBoardType: type, clientsView: 'board' }),

        setActiveEmployeeBoard: (board) => set({ activeEmployeeBoard: board }),

        setTaskBoardVisibleStatuses: (statusIds) => set({ taskBoardVisibleStatuses: statusIds }),

        setClientsView: (view) => set({ clientsView: view }),

        login: (loginId, password) => {
          const { employees, employeePermissions, employeeCredentials } = get();
          const lookup = lookupEmployeeLogin(loginId, employees, employeeCredentials);
          if (lookup.status === 'not-found') return 'not-found';
          if (lookup.status === 'ambiguous') return 'ambiguous';

          const employeeId = lookup.employee.id;
          if (!verifyEmployeeLogin(employeeId, password, employees, employeeCredentials)) {
            return 'invalid-password';
          }

          const tab = get().activeMainTab;
          const restrictedTab =
            (tab === 'org-chart' && !canAccessOrgChart(employeeId, employees, employeePermissions)) ||
            (tab === 'owner-dashboard' &&
              !canViewOwnerDashboard(employeeId, employees, employeePermissions)) ||
            (tab === 'pm-dashboard' &&
              !canViewDashboard('pm', employeeId, employees, employeePermissions)) ||
            (tab === 'field-dashboard' &&
              !canViewDashboard('field', employeeId, employees, employeePermissions)) ||
            (tab === 'fab-dashboard' &&
              !canViewDashboard('fab', employeeId, employees, employeePermissions)) ||
            (tab === 'shipping-dashboard' &&
              !canViewDashboard('shipping', employeeId, employees, employeePermissions)) ||
            (tab === 'weld-log-dashboard' &&
              !canViewWeldLogDashboard(employeeId, employees, employeePermissions)) ||
            (tab === 'spooling-dashboard' &&
              !canViewSpoolingDashboard(employeeId, employees, employeePermissions)) ||
            (tab === 'activity-log' &&
              !canViewActivityLog(employeeId, employees, employeePermissions)) ||
            (tab === 'time-tracking' &&
              !canViewTimeTracking(employeeId, employees, employeePermissions)) ||
            (tab === 'visibility-dashboard' &&
              !canViewVisibilityDashboard(
                employeeId,
                employees,
                employeePermissions,
                get().visibilityDashboardJobLevels
              ));
          const nextTab = restrictedTab ? 'clients' : tab;

          const credential = employeeCredentials[employeeId];
          const nextCredentials =
            credential?.invitePending
              ? {
                  ...employeeCredentials,
                  [employeeId]: { ...credential, invitePending: false },
                }
              : employeeCredentials;

          set({
            currentUserId: employeeId,
            viewAsOriginalUserId: null,
            activeMainTab: nextTab,
            employeeCredentials: nextCredentials,
          });
          return 'success';
        },

        ensureDevSession: () => {
          if (!import.meta.env.DEV) return;
          set((state) => ({
            currentUserId: JOE_VASQUEZ_ID,
            viewAsOriginalUserId: null,
            employeeCredentials: {
              ...state.employeeCredentials,
              [JOE_VASQUEZ_ID]: createJoeVasquezCredential(),
            },
          }));
        },

        setViewAsEmployee: (employeeId) => {
          const state = get();
          const { employees, employeePermissions, activeMainTab, currentUserId, viewAsOriginalUserId } =
            state;

          if (employeeId === null) {
            if (!viewAsOriginalUserId) return;
            set({
              currentUserId: viewAsOriginalUserId,
              viewAsOriginalUserId: null,
            });
            return;
          }

          if (!employees.some((employee) => employee.id === employeeId)) return;

          const originalUserId = viewAsOriginalUserId ?? currentUserId;
          if (!originalUserId) return;

          if (employeeId === originalUserId) {
            set({
              currentUserId: originalUserId,
              viewAsOriginalUserId: null,
            });
            return;
          }

          const restrictedTab =
            (activeMainTab === 'org-chart' &&
              !canAccessOrgChart(employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'owner-dashboard' &&
              !canViewOwnerDashboard(employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'pm-dashboard' &&
              !canViewDashboard('pm', employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'field-dashboard' &&
              !canViewDashboard('field', employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'fab-dashboard' &&
              !canViewDashboard('fab', employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'shipping-dashboard' &&
              !canViewDashboard('shipping', employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'weld-log-dashboard' &&
              !canViewWeldLogDashboard(employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'spooling-dashboard' &&
              !canViewSpoolingDashboard(employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'activity-log' &&
              !canViewActivityLog(employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'time-tracking' &&
              !canViewTimeTracking(employeeId, employees, employeePermissions)) ||
            (activeMainTab === 'visibility-dashboard' &&
              !canViewVisibilityDashboard(
                employeeId,
                employees,
                employeePermissions,
                state.visibilityDashboardJobLevels
              ));

          set({
            viewAsOriginalUserId: originalUserId,
            currentUserId: employeeId,
            activeMainTab: restrictedTab ? 'clients' : activeMainTab,
          });
        },

        logout: () =>
          set({
            currentUserId: null,
            viewAsOriginalUserId: null,
            activeMainTab: 'clients',
            clientsView: 'dashboard',
            activeClientId: null,
            activeProjectId: null,
          }),

        goToMainScreen: () =>
          set({
            activeMainTab: 'clients',
            clientsView: 'dashboard',
            activeClientId: null,
            activeProjectId: null,
          }),

        openProjectBoard: (clientId, projectId, boardType) => {
          set({
            activeClientId: clientId,
            activeProjectId: projectId,
            activeBoardType: boardType,
            clientsView: 'board',
            activeMainTab: 'clients',
          });
        },

        openFieldJobDashboard: (projectId) => {
          set({
            activeMainTab: 'field-dashboard',
            fieldFocusProjectId: projectId,
          });
        },

        setFieldFocusProjectId: (projectId) => set({ fieldFocusProjectId: projectId }),

        addClient: (name) => {

          const client: Client = { id: uuid(), name };

          set((s) => ({

            clients: [...s.clients, client],

            activeClientId: client.id,

            activeProjectId: null,

          }));

        },



        updateClient: (id, updates) => {
          set((s) => ({
            clients: s.clients.map((client) => {
              if (client.id !== id) return client;
              const next = { ...client, ...updates };
              if (updates.name !== undefined) {
                const trimmed = updates.name.trim();
                if (!trimmed) return client;
                next.name = trimmed;
              }
              return next;
            }),
          }));
        },



        removeClient: (id) => {

          set((s) => {

            const clients = s.clients.filter((c) => c.id !== id);

            const projects = s.projects.filter((p) => p.clientId !== id);

            const rootTaskIds = s.tasks
              .filter((t) => t.clientId === id && !t.parentTaskId)
              .map((t) => t.id);
            const soft = softDeleteTaskTrees({
              tasks: s.tasks,
              taskAttachments: s.taskAttachments,
              taskComments: s.taskComments,
              deletedTaskArchive: s.deletedTaskArchive ?? [],
              activityLog: s.activityLog,
              rootTaskIds,
              actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
              reason: 'client-removed',
              createId: uuid,
              summaryForRoot: (task, descendantCount) =>
                descendantCount > 0
                  ? `Deleted task "${task.title}" with client (+${descendantCount} subtasks)`
                  : `Deleted task "${task.title}" with client`,
            });

            const taskGroups = s.taskGroups.filter((g) => g.clientId !== id);

            const customBoards = s.customBoards.filter((b) => b.clientId !== id);

            return {

              clients,

              projects,

              tasks: soft.tasks,

              taskGroups,

              customBoards,

              taskAttachments: soft.taskAttachments,

              taskComments: soft.taskComments,

              deletedTaskArchive: soft.deletedTaskArchive,

              activityLog: soft.activityLog,

              activeClientId: clients[0]?.id ?? null,

              activeProjectId: projects.find((p) => p.clientId === clients[0]?.id)?.id ?? null,

            };

          });

        },



        addProject: (clientId, name, options) => {
          const trimmed = name.trim();
          if (!trimmed) return;

          const buildingLevels = options?.buildingLevels ?? [];
          const activeLevels = options?.activeLevels ?? [];

          set((s) => {
            const projectId = uuid();
            const project: Project = normalizeProject({
              id: projectId,
              name: trimmed,
              clientId,
              ...defaultProjectFields(),
              buildingLevels: [...buildingLevels],
              activeLevels: [...activeLevels],
            });

            let taskGroups = [...s.taskGroups];
            let tasks = [...s.tasks];

            if (activeLevels.length > 0) {
              const seed = buildProjectSeed(
                {
                  projectName: trimmed,
                  clientName: '',
                  levels: activeLevels.map((levelName) => ({ name: levelName })),
                  systems: ['Mechanical Piping', 'Duct'],
                },
                clientId,
                projectId,
                getEmployeeIds(s.employees)
              );
              taskGroups = [...taskGroups, ...seed.groups];
              tasks = [...tasks, ...seed.tasks];
            } else {
              taskGroups = [...taskGroups, ...createMainSections(clientId, projectId)];
            }

            return {
              projects: [...s.projects, project],
              taskGroups,
              tasks,
              activeProjectId: projectId,
              activeBoardType: 'main' as ProjectBoardType,
            };
          });
        },

        addProjectFromTemplate: (clientId, name, options) => {
          const trimmed = name.trim();
          if (!trimmed) return;

          if (!options.useTemplate) {
            get().addProject(clientId, trimmed, options);
            return;
          }

          set((s) => {
            let projects = s.projects;
            let taskGroups = s.taskGroups;
            let tasks = s.tasks;
            let clients = s.clients;

            if (!projects.some((p) => p.isTemplate)) {
              const ensured = ensureProjectTemplate(
                clients,
                projects,
                taskGroups,
                tasks,
                getEmployeeIds(s.employees)
              );
              clients = ensured.clients;
              projects = ensured.projects;
              taskGroups = ensured.taskGroups;
              tasks = ensured.tasks;
            }

            const template = projects.find((p) => p.isTemplate);
            if (!template) {
              const projectId = uuid();
              const project: Project = normalizeProject({
                id: projectId,
                name: trimmed,
                clientId,
                ...defaultProjectFields(),
                buildingLevels: [...options.buildingLevels],
                activeLevels: [...options.activeLevels],
              });
              let nextGroups = [...taskGroups];
              let nextTasks = [...tasks];
              if (options.activeLevels.length > 0) {
                const seed = buildProjectSeed(
                  {
                    projectName: trimmed,
                    clientName: '',
                    levels: options.activeLevels.map((levelName) => ({ name: levelName })),
                    systems: ['Mechanical Piping', 'Duct'],
                  },
                  clientId,
                  projectId,
                  getEmployeeIds(s.employees)
                );
                nextGroups = [...nextGroups, ...seed.groups];
                nextTasks = [...nextTasks, ...seed.tasks];
              } else {
                nextGroups = [...nextGroups, ...createMainSections(clientId, projectId)];
              }
              return {
                ...s,
                clients,
                projects: [...projects, project],
                taskGroups: nextGroups,
                tasks: nextTasks,
                activeProjectId: projectId,
                activeBoardType: 'main' as ProjectBoardType,
              };
            }

            const newProjectId = uuid();
            const cloned = cloneProjectFromTemplate(
              template,
              clientId,
              newProjectId,
              trimmed,
              taskGroups,
              tasks,
              s.customBoards,
              s.boardTaskStatuses,
              options.buildingLevels,
              options.activeLevels
            );

            return {
              ...s,
              clients,
              projects: [...projects, cloned.project],
              taskGroups: [...taskGroups, ...cloned.taskGroups],
              tasks: [...tasks, ...cloned.tasks],
              customBoards: [...s.customBoards, ...cloned.customBoards],
              activeProjectId: newProjectId,
              activeBoardType: 'main' as ProjectBoardType,
            };
          });
        },

        removeProject: (id) => {

          const project = get().projects.find((p) => p.id === id);
          if (project?.isTemplate) return;

          set((s) => {

            const projects = s.projects.filter((p) => p.id !== id);

            const clientProjects = projects.filter((p) => p.clientId === s.activeClientId);

            const rootTaskIds = s.tasks
              .filter((t) => t.projectId === id && !t.parentTaskId)
              .map((t) => t.id);
            const soft = softDeleteTaskTrees({
              tasks: s.tasks,
              taskAttachments: s.taskAttachments,
              taskComments: s.taskComments,
              deletedTaskArchive: s.deletedTaskArchive ?? [],
              activityLog: s.activityLog,
              rootTaskIds,
              actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
              reason: 'project-removed',
              createId: uuid,
              summaryForRoot: (task, descendantCount) =>
                descendantCount > 0
                  ? `Deleted task "${task.title}" with project (+${descendantCount} subtasks)`
                  : `Deleted task "${task.title}" with project`,
            });

            return {

              projects,

              tasks: soft.tasks,

              taskGroups: s.taskGroups.filter((g) => g.projectId !== id),

              customBoards: s.customBoards.filter((b) => b.projectId !== id),

              taskAttachments: soft.taskAttachments,

              taskComments: soft.taskComments,

              deletedTaskArchive: soft.deletedTaskArchive,

              activityLog: soft.activityLog,

              activeProjectId: clientProjects[0]?.id ?? null,

            };

          });

        },



        updateProjectSettings: (projectId, updates) => {
          const state = get();
          const touchesBudget =
            updates.budgetHours !== undefined || updates.totalHoursSpent !== undefined;
          if (touchesBudget && !canEditBudgetHours(state.currentUserId, state.employees, state.employeePermissions)) {
            return;
          }
          set((s) => ({
            projects: s.projects.map((p) =>
              p.id === projectId ? { ...p, ...updates } : p
            ),
          }));
        },

        assignProjectPm: (projectId, employeeId) => {
          set((s) => ({
            projects: s.projects.map((project) => {
              if (project.id !== projectId) return project;
              if (project.pmIds.includes(employeeId)) return project;
              return { ...project, pmIds: [...project.pmIds, employeeId] };
            }),
          }));
        },

        unassignProjectPm: (projectId, employeeId) => {
          set((s) => ({
            projects: s.projects.map((project) =>
              project.id === projectId
                ? { ...project, pmIds: project.pmIds.filter((id) => id !== employeeId) }
                : project
            ),
          }));
        },

        addEmployee: (name, role, orgCategory, email) => {
          const trimmedEmail = email.trim();
          if (!isValidEmail(trimmedEmail)) {
            throw new Error('A valid email is required to send a login invite.');
          }
          const category = orgCategory ?? defaultOrgCategoryForRole(role);
          const matchingTitle = get().employeeJobTitles.find(
            (title) =>
              resolveOrgCategoryForTitle(title) === category && !title.opsPlacement
          );
          const employee: Employee = {
            id: uuid(),
            name,
            role,
            orgCategory: category,
            jobTitleId: matchingTitle?.id,
          };
          const invitePassword = generateInvitePassword();
          const usedStyleKeys = new Set(
            Object.values(get().employeeAssigneeStyles).map(assigneeStyleKey)
          );
          const badgeStyle = pickNextUniqueAssigneeStyle(usedStyleKeys);

          set((s) => ({
            employees: [...s.employees, employee],
            employeePermissions: {
              ...s.employeePermissions,
              [employee.id]: ['view-org-chart'],
            },
            employeeAssigneeStyles: {
              ...s.employeeAssigneeStyles,
              [employee.id]: badgeStyle,
            },
            employeeCredentials: {
              ...s.employeeCredentials,
              [employee.id]: {
                password: invitePassword,
                invitePending: true,
                invitedAt: new Date().toISOString(),
                email: trimmedEmail,
              },
            },
            orgTeams: s.orgTeams.map((team) =>
              team.id === orgCategoryToTeamId(category)
                ? { ...team, memberIds: [...team.memberIds, employee.id] }
                : team
            ),
          }));

          return {
            employeeId: employee.id,
            employeeName: name,
            invitePassword,
            email: trimmedEmail,
          };
        },

        updateEmployee: (id, updates) => {
          const existing = get().employees.find((employee) => employee.id === id);
          if (!existing) return;

          const normalized: Partial<Pick<Employee, 'name' | 'role' | 'orgCategory'>> = { ...updates };
          if (isOwnerEmployee(existing)) {
            delete normalized.orgCategory;
            delete normalized.role;
          } else if (isProtectedRosterEmployee(id)) {
            delete normalized.orgCategory;
          }

          if (normalized.name !== undefined) {
            const trimmedName = normalized.name.trim();
            if (!trimmedName) return;
            normalized.name = trimmedName;
          }

          set((s) => {
            let orgTeams = s.orgTeams;

            if (normalized.orgCategory) {
              const teamId = orgCategoryToTeamId(normalized.orgCategory);
              orgTeams = s.orgTeams.map((team) => {
                const withoutEmployee = team.memberIds.filter((memberId) => memberId !== id);
                if (team.id === teamId) {
                  return { ...team, memberIds: [...withoutEmployee, id] };
                }
                return { ...team, memberIds: withoutEmployee };
              });
            }

            return {
              employees: s.employees.map((e) => (e.id === id ? { ...e, ...normalized } : e)),
              orgTeams,
              projects:
                normalized.role === undefined
                  ? s.projects
                  : s.projects.map((p) => {
                      if (normalized.role === 'detailer') {
                        return {
                          ...p,
                          supportIds: p.supportIds.filter((sid) => sid !== id),
                        };
                      }
                      if (normalized.role === 'support-specialist') {
                        return {
                          ...p,
                          detailerIds: p.detailerIds.filter((did) => did !== id),
                        };
                      }
                      return p;
                    }),
            };
          });
        },

        changeEmployeeJob: (employeeId, jobTitleId) => {
          const state = get();
          const title = findEmployeeJobTitle(state.employeeJobTitles, jobTitleId);
          if (!title) return;

          const existing = state.employees.find((employee) => employee.id === employeeId);
          if (!existing || isOwnerEmployee(existing)) return;
          if (isProtectedRosterEmployee(employeeId)) return;

          const orgCategory = resolveOrgCategoryForTitle(title);
          const nextRole = roleForOrgCategory(orgCategory);
          const nextPermissions = permissionsForJobTitle(title);

          set((s) => {
            let assignments = s.dashboardAssignments ?? createDefaultDashboardAssignments();
            assignments = removeEmployeeFromAllDashboardRoles(assignments, employeeId);
            if (title.opsPlacement) {
              const { dashboard, roleId } = title.opsPlacement;
              const board = { ...(assignments[dashboard] as Record<string, string[]>) };
              const current = board[roleId] ?? [];
              if (!current.includes(employeeId)) {
                board[roleId] = [...current, employeeId];
              }
              assignments = {
                ...assignments,
                [dashboard]: board,
              } as DashboardAssignments;
            }

            const teamId = orgCategoryToTeamId(orgCategory);
            const orgTeams = s.orgTeams.map((team) => {
              const withoutEmployee = team.memberIds.filter((memberId) => memberId !== employeeId);
              if (team.id === teamId) {
                return { ...team, memberIds: [...withoutEmployee, employeeId] };
              }
              return { ...team, memberIds: withoutEmployee };
            });

            const activityLog = logActivity(
              s.activityLog,
              {
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                action: 'updated',
                entityType: 'employee',
                entityId: employeeId,
                summary: `Changed job for ${existing.name} to ${title.label}`,
                details: {
                  name: existing.name,
                  job: title.label,
                  orgCategory,
                  stageId: title.stageId,
                },
              },
              uuid
            );

            return {
              employees: s.employees.map((employee) =>
                employee.id === employeeId
                  ? {
                      ...employee,
                      role: nextRole,
                      orgCategory,
                      jobTitleId: title.id,
                    }
                  : employee
              ),
              orgTeams,
              dashboardAssignments: assignments,
              employeePermissions: {
                ...s.employeePermissions,
                [employeeId]: nextPermissions,
              },
              activityLog,
              projects: s.projects.map((project) => {
                if (nextRole === 'detailer') {
                  return {
                    ...project,
                    supportIds: project.supportIds.filter((sid) => sid !== employeeId),
                  };
                }
                if (nextRole === 'support-specialist') {
                  return {
                    ...project,
                    detailerIds: project.detailerIds.filter((did) => did !== employeeId),
                  };
                }
                return {
                  ...project,
                  detailerIds: project.detailerIds.filter((did) => did !== employeeId),
                  supportIds: project.supportIds.filter((sid) => sid !== employeeId),
                };
              }),
            };
          });
        },

        addJobTitle: (label, stageId) => {
          const trimmed = label.trim();
          if (!trimmed) return null;
          const id = `job-${uuid()}`;
          const draft = repairJobTitlePlacement({
            id,
            label: trimmed,
            stageId,
          });
          set((s) => ({
            employeeJobTitles: [...s.employeeJobTitles, draft],
          }));
          return id;
        },

        updateJobTitle: (id, updates) => {
          set((s) => {
            const existing = findEmployeeJobTitle(s.employeeJobTitles, id);
            if (!existing) return s;

            const next = repairJobTitlePlacement({
              ...existing,
              ...updates,
              label:
                updates.label !== undefined
                  ? updates.label.trim() || existing.label
                  : existing.label,
              id: existing.id,
            });

            const orgCategory = resolveOrgCategoryForTitle(next);
            const nextRole = roleForOrgCategory(orgCategory);
            const nextPermissions = permissionsForJobTitle(next);

            let assignments = s.dashboardAssignments ?? createDefaultDashboardAssignments();
            const holders = s.employees.filter((employee) => employee.jobTitleId === id);

            for (const holder of holders) {
              assignments = removeEmployeeFromAllDashboardRoles(assignments, holder.id);
              if (next.opsPlacement) {
                const { dashboard, roleId } = next.opsPlacement;
                const board = { ...(assignments[dashboard] as Record<string, string[]>) };
                const current = board[roleId] ?? [];
                if (!current.includes(holder.id)) {
                  board[roleId] = [...current, holder.id];
                }
                assignments = {
                  ...assignments,
                  [dashboard]: board,
                } as DashboardAssignments;
              }
            }

            const holderIds = new Set(holders.map((employee) => employee.id));
            const teamId = orgCategoryToTeamId(orgCategory);
            const orgTeams = s.orgTeams.map((team) => {
              let memberIds = team.memberIds.filter((memberId) => !holderIds.has(memberId));
              if (team.id === teamId) {
                memberIds = [...memberIds, ...holders.map((employee) => employee.id)];
              }
              return { ...team, memberIds };
            });

            const employeePermissions = { ...s.employeePermissions };
            for (const holder of holders) {
              employeePermissions[holder.id] = nextPermissions;
            }

            return {
              employeeJobTitles: s.employeeJobTitles.map((title) =>
                title.id === id ? next : title
              ),
              employees: s.employees.map((employee) =>
                employee.jobTitleId === id
                  ? { ...employee, role: nextRole, orgCategory, jobTitleId: id }
                  : employee
              ),
              orgTeams,
              dashboardAssignments: assignments,
              employeePermissions,
            };
          });
        },

        removeJobTitle: (id) => {
          const state = get();
          if (state.employees.some((employee) => employee.jobTitleId === id)) {
            return false;
          }
          if (!findEmployeeJobTitle(state.employeeJobTitles, id)) return false;
          set((s) => ({
            employeeJobTitles: s.employeeJobTitles.filter((title) => title.id !== id),
          }));
          return true;
        },

        updateEmployeeEmail: (id, email) => {
          const trimmedEmail = email.trim();
          if (!isValidEmail(trimmedEmail)) return;
          if (!get().employees.some((employee) => employee.id === id)) return;

          set((s) => ({
            employeeCredentials: {
              ...s.employeeCredentials,
              [id]: {
                ...(s.employeeCredentials[id] ?? {
                  password: DEFAULT_LOGIN_PASSWORD,
                  invitePending: false,
                }),
                email: trimmedEmail,
              },
            },
          }));
        },

        removeEmployee: (id) => {
          if (isProtectedRosterEmployee(id)) return;

          set((s) => {
            const employee = s.employees.find((entry) => entry.id === id);
            if (!employee) return s;

            const actorId = resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId);
            const archiveId = uuid();
            const activityLogId = uuid();
            const archive: DeletedEmployeeArchive = {
              id: archiveId,
              deletedAt: new Date().toISOString(),
              deletedById: actorId,
              activityLogId,
              employee: { ...employee },
              permissions: [...(s.employeePermissions[id] ?? [])],
              reportsTo: [...(s.employeeReportsTo[id] ?? [])],
              assigneeStyle: s.employeeAssigneeStyles[id] ?? null,
              credentials: s.employeeCredentials[id] ?? null,
            };

            const activityLog = logActivity(
              s.activityLog,
              {
                actorId,
                action: 'deleted',
                entityType: 'employee',
                entityId: id,
                summary: `Removed employee "${employee.name}" from the roster`,
                details: {
                  name: employee.name,
                  role: employee.role,
                  orgCategory: employee.orgCategory ?? null,
                },
                archiveId,
              },
              () => activityLogId
            );

            return {
              employees: s.employees.filter((e) => e.id !== id),
              tasks: s.tasks.map((t) =>
                t.assigneeIds.includes(id)
                  ? { ...t, assigneeIds: t.assigneeIds.filter((aid) => aid !== id) }
                  : t
              ),
              projects: s.projects.map((p) => ({
                ...p,
                detailerIds: p.detailerIds.filter((did) => did !== id),
                supportIds: p.supportIds.filter((sid) => sid !== id),
                pmIds: p.pmIds.filter((pmId) => pmId !== id),
                assistantPmIds: (p.assistantPmIds ?? []).filter((pmId) => pmId !== id),
                fieldIds: (p.fieldIds ?? []).filter((fieldId) => fieldId !== id),
                fieldCrewIds: (p.fieldCrewIds ?? []).filter((fieldId) => fieldId !== id),
              })),
              orgTeams: s.orgTeams.map((team) => ({
                ...team,
                memberIds: team.memberIds.filter((memberId) => memberId !== id),
              })),
              employeePermissions: Object.fromEntries(
                Object.entries(s.employeePermissions).filter(([employeeId]) => employeeId !== id)
              ),
              employeeReportsTo: Object.fromEntries(
                Object.entries(s.employeeReportsTo)
                  .filter(([employeeId]) => employeeId !== id)
                  .map(([employeeId, managerIds]) => [
                    employeeId,
                    managerIds.filter((managerId) => managerId !== id),
                  ])
              ),
              orgChartLevelSlots: removeEmployeeFromOrgChartSlots(s.orgChartLevelSlots, id),
              timeEntries: s.timeEntries.filter((entry) => entry.employeeId !== id),
              employeeAssigneeStyles: Object.fromEntries(
                Object.entries(s.employeeAssigneeStyles).filter(([employeeId]) => employeeId !== id)
              ),
              employeeCredentials: Object.fromEntries(
                Object.entries(s.employeeCredentials).filter(([employeeId]) => employeeId !== id)
              ),
              currentUserId: s.currentUserId === id ? null : s.currentUserId,
              deletedEmployeeArchive: [archive, ...(s.deletedEmployeeArchive ?? [])],
              activityLog,
            };
          });
        },

        restoreDeletedEmployee: (archiveId) => {
          const state = get();
          const archive = (state.deletedEmployeeArchive ?? []).find((entry) => entry.id === archiveId);
          if (!archive || archive.restoredAt) return false;
          if (state.employees.some((employee) => employee.id === archive.employee.id)) return false;

          const actorId = resolveActivityActorId(state.currentUserId, state.viewAsOriginalUserId);
          const employee = archive.employee;
          const employeeIds = [...state.employees.map((entry) => entry.id), employee.id];

          set((s) => {
            const nextCredentials = { ...s.employeeCredentials };
            if (archive.credentials) {
              nextCredentials[employee.id] = archive.credentials;
            }
            const nextStyles = { ...s.employeeAssigneeStyles };
            if (archive.assigneeStyle) {
              nextStyles[employee.id] = archive.assigneeStyle;
            }
            const synced = syncEmployeeAuthAndColors(
              employeeIds,
              nextStyles,
              nextCredentials,
              buildUniqueAssigneeStyles,
              [...s.employees, employee]
            );

            const activityLog = logActivity(
              s.activityLog,
              {
                actorId,
                action: 'restored',
                entityType: 'employee',
                entityId: employee.id,
                summary: `Restored employee "${employee.name}" to the roster`,
                details: { name: employee.name },
                archiveId,
              },
              uuid
            );

            return {
              employees: [...s.employees, employee],
              employeePermissions: {
                ...s.employeePermissions,
                [employee.id]: [...archive.permissions],
              },
              employeeReportsTo: {
                ...s.employeeReportsTo,
                [employee.id]: [...archive.reportsTo],
              },
              employeeAssigneeStyles: synced.employeeAssigneeStyles,
              employeeCredentials: synced.employeeCredentials,
              deletedEmployeeArchive: (s.deletedEmployeeArchive ?? []).map((entry) =>
                entry.id === archiveId
                  ? { ...entry, restoredAt: new Date().toISOString(), restoredById: actorId }
                  : entry
              ),
              activityLog,
            };
          });
          return true;
        },

        addOrgTeam: (name) => {
          const team: OrgTeam = {
            id: `team-${uuid()}`,
            name,
            memberIds: [],
            sortOrder: get().orgTeams.length,
          };
          set((s) => ({ orgTeams: [...s.orgTeams, team] }));
        },

        renameOrgTeam: (teamId, name) => {
          set((s) => ({
            orgTeams: s.orgTeams.map((team) => (team.id === teamId ? { ...team, name } : team)),
          }));
        },

        removeOrgTeam: (teamId) => {
          set((s) => ({ orgTeams: s.orgTeams.filter((team) => team.id !== teamId) }));
        },

        moveEmployeeToTeam: (employeeId, teamId) => {
          set((s) => {
            const orgTeams = s.orgTeams.map((team) => {
              const withoutEmployee = team.memberIds.filter((id) => id !== employeeId);
              if (teamId && team.id === teamId) {
                return { ...team, memberIds: [...withoutEmployee, employeeId] };
              }
              return { ...team, memberIds: withoutEmployee };
            });

            const targetTeam = teamId ? orgTeams.find((team) => team.id === teamId) : null;
            const nextManagers = (s.employeeReportsTo[employeeId] ?? []).filter((managerId) => {
              const manager = s.employees.find((entry) => entry.id === managerId);
              if (isOrgOwner(manager)) return true;
              return targetTeam ? targetTeam.memberIds.includes(managerId) : false;
            });

            const category = teamId ? teamIdToOrgCategory(teamId) : null;
            const employees =
              category === null
                ? s.employees
                : s.employees.map((employee) => {
                    if (employee.id !== employeeId) return employee;
                    if (category === 'owner') {
                      return { ...employee, orgCategory: category };
                    }
                    return {
                      ...employee,
                      orgCategory: category,
                      role: roleForOrgCategory(category),
                    };
                  });

            return {
              orgTeams,
              employees,
              employeeReportsTo: {
                ...s.employeeReportsTo,
                [employeeId]: teamId ? nextManagers : [],
              },
            };
          });
        },

        setEmployeeManagers: (employeeId, managerIds) => {
          set((s) => {
            const unique = [...new Set(managerIds.filter(Boolean))].filter((managerId) => {
              if (wouldCreateReportingCycle(employeeId, managerId, s.employeeReportsTo)) return false;
              return isValidReportingManager(employeeId, managerId, s.employees);
            });

            return {
              employeeReportsTo: {
                ...s.employeeReportsTo,
                [employeeId]: unique,
              },
            };
          });
        },

        addEmployeeManager: (employeeId, managerId) => {
          set((s) => {
            if (wouldCreateReportingCycle(employeeId, managerId, s.employeeReportsTo)) return s;
            if (!isValidReportingManager(employeeId, managerId, s.employees)) return s;

            const current = s.employeeReportsTo[employeeId] ?? [];
            if (current.includes(managerId)) return s;

            return {
              employeeReportsTo: {
                ...s.employeeReportsTo,
                [employeeId]: [...current, managerId],
              },
            };
          });
        },

        toggleEmployeeManager: (employeeId, managerId, enabled) => {
          const state = get();
          const current = state.employeeReportsTo[employeeId] ?? [];
          if (enabled) {
            get().addEmployeeManager(employeeId, managerId);
          } else {
            get().setEmployeeManagers(
              employeeId,
              current.filter((id) => id !== managerId)
            );
          }
        },

        moveOrgChartEmployeeToSlot: (depth, employeeId, halfSlotIndex) => {
          get().placeEmployeeOnOrgChartSlot(depth, employeeId, halfSlotIndex);
        },

        placeEmployeeOnOrgChartSlot: (targetDepth, employeeId, halfSlotIndex) => {
          set((s) => {
            const memberIds = s.employees.map((employee) => employee.id);
            const currentDepth = computeEmployeeDepth(employeeId, memberIds, s.employeeReportsTo);

            let nextManagers = s.employeeReportsTo[employeeId] ?? [];
            if (targetDepth !== currentDepth) {
              nextManagers = managersForOrgChartDepth(
                targetDepth,
                halfSlotIndex,
                memberIds,
                s.employeeReportsTo,
                s.orgChartLevelSlots
              );
              if (targetDepth > 0 && nextManagers.length === 0) return s;

              for (const managerId of nextManagers) {
                if (wouldCreateReportingCycle(employeeId, managerId, s.employeeReportsTo)) return s;
                if (!isValidReportingManager(employeeId, managerId, s.employees)) {
                  return s;
                }
              }
            }

            const nextReportsTo = {
              ...s.employeeReportsTo,
              [employeeId]: nextManagers,
            };

            const level = buildOrgChartLevels(memberIds, nextReportsTo).find(
              (entry) => entry.depth === targetDepth
            );
            if (!level?.employeeIds.includes(employeeId)) return s;

            const key = String(targetDepth);
            const existing = s.orgChartLevelSlots[key] ?? {};
            const merged = resolveOrgChartCardPositions(level.employeeIds, existing);
            if (!canPlaceCardAtBodyIndex(merged, halfSlotIndex, employeeId)) return s;

            return {
              employeeReportsTo: nextReportsTo,
              orgChartLevelSlots: {
                ...s.orgChartLevelSlots,
                [key]: {
                  ...existing,
                  [employeeId]: halfSlotIndex,
                },
              },
            };
          });
        },

        organizeOrgChartLayout: () => {
          set((s) => ({
            orgChartLevelSlots: createDefaultOrgChartLevelSlots(
              s.employees.map((employee) => employee.id),
              s.employeeReportsTo
            ),
          }));
        },

        setEmployeePermission: (employeeId, permission, enabled) => {
          set((s) => {
            const current = new Set(s.employeePermissions[employeeId] ?? []);
            if (enabled) current.add(permission);
            else current.delete(permission);
            const employee = s.employees.find((entry) => entry.id === employeeId);
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                action: 'updated',
                entityType: 'permission',
                entityId: employeeId,
                summary: `${enabled ? 'Granted' : 'Revoked'} ${permission} for ${employee?.name ?? 'employee'}`,
                details: { permission, enabled },
              },
              uuid
            );
            return {
              employeePermissions: {
                ...s.employeePermissions,
                [employeeId]: [...current],
              },
              activityLog,
            };
          });
        },

        setVisibilityDashboardJobLevel: (category, enabled) => {
          set((s) => {
            const levels = new Set(s.visibilityDashboardJobLevels);
            if (enabled) levels.add(category);
            else levels.delete(category);
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                action: 'updated',
                entityType: 'permission',
                entityId: 'visibility-dashboard',
                summary: `${enabled ? 'Enabled' : 'Disabled'} Access Control for job level ${category}`,
                details: { category, enabled },
              },
              uuid
            );
            return {
              visibilityDashboardJobLevels: [...levels],
              activityLog,
            };
          });
        },

        setEmployeeNavVisibility: (employeeId, column, enabled) => {
          const permission = NAV_COLUMN_PERMISSION[column];
          set((s) => {
            const current = new Set(s.employeePermissions[employeeId] ?? []);
            if (enabled) current.add(permission);
            else current.delete(permission);
            const employee = s.employees.find((entry) => entry.id === employeeId);
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                action: 'updated',
                entityType: 'permission',
                entityId: employeeId,
                summary: `${enabled ? 'Granted' : 'Revoked'} ${permission} for ${employee?.name ?? 'employee'}`,
                details: { permission, enabled, column },
              },
              uuid
            );
            return {
              employeePermissions: {
                ...s.employeePermissions,
                [employeeId]: [...current],
              },
              activityLog,
            };
          });
        },

        setJobLevelNavVisibility: (rowId, column, enabled) => {
          set((s) => {
            const permission = NAV_COLUMN_PERMISSION[column];
            const nextDefaults = normalizeJobLevelNavVisibility(s.jobLevelNavVisibility);
            nextDefaults[rowId] = {
              ...(nextDefaults[rowId] ?? DEFAULT_JOB_LEVEL_NAV_VISIBILITY[rowId]!),
              [column]: enabled,
            };

            const nextPermissions: EmployeePermissionsMap = { ...s.employeePermissions };
            for (const employee of employeesForJobLevelRow(
              rowId,
              s.employees,
              s.dashboardAssignments
            )) {
              const granted = new Set(nextPermissions[employee.id] ?? []);
              if (enabled) granted.add(permission);
              else granted.delete(permission);
              nextPermissions[employee.id] = [...granted];
            }

            let visibilityDashboardJobLevels = s.visibilityDashboardJobLevels;
            const categoryRows: OrgCategory[] = [
              'owner',
              'bim-manager',
              'operations-manager',
              'support-manager',
              'support-specialist',
            ];
            if (column === 'visibility' && (categoryRows as string[]).includes(rowId)) {
              const levels = new Set(visibilityDashboardJobLevels);
              if (enabled) levels.add(rowId as OrgCategory);
              else levels.delete(rowId as OrgCategory);
              visibilityDashboardJobLevels = [...levels];
            }

            const activityLog = logActivity(
              s.activityLog,
              {
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                action: 'updated',
                entityType: 'permission',
                entityId: rowId,
                summary: `${enabled ? 'Enabled' : 'Disabled'} ${column} default for ${rowId}`,
                details: { rowId, column, enabled },
              },
              uuid
            );

            return {
              jobLevelNavVisibility: nextDefaults,
              employeePermissions: nextPermissions,
              visibilityDashboardJobLevels,
              activityLog,
            };
          });
        },

        assignDashboardMember: (dashboard, roleId, employeeId) => {
          set((s) => {
            const assignments = s.dashboardAssignments ?? createDefaultDashboardAssignments();
            const board = { ...assignments[dashboard] } as Record<string, string[]>;
            const current = [...(board[roleId] ?? [])];
            if (current.includes(employeeId)) return s;
            board[roleId] = [...current, employeeId];
            return {
              dashboardAssignments: {
                ...assignments,
                [dashboard]: board,
              } as DashboardAssignments,
            };
          });
        },

        unassignDashboardMember: (dashboard, roleId, employeeId) => {
          set((s) => {
            const assignments = s.dashboardAssignments ?? createDefaultDashboardAssignments();
            const board = { ...assignments[dashboard] } as Record<string, string[]>;
            board[roleId] = (board[roleId] ?? []).filter((id) => id !== employeeId);
            return {
              dashboardAssignments: {
                ...assignments,
                [dashboard]: board,
              } as DashboardAssignments,
            };
          });
        },

        addTimeEntry: (entry) => {
          const state = get();
          const prepared = prepareTimeEntryPayload(entry);
          if (
            !prepared ||
            !canViewEmployeeTime(
              state.currentUserId,
              prepared.employeeId,
              state.employees,
              state.employeeReportsTo
            )
          ) {
            return;
          }
          const timeEntry: TimeEntry = {
            ...prepared,
            id: uuid(),
            createdAt: new Date().toISOString(),
          };
          set((s) => {
            const history = pushHistory(s);
            return { ...history, timeEntries: [timeEntry, ...s.timeEntries] };
          });
        },

        updateTimeEntry: (id, entry) => {
          const state = get();
          const existing = state.timeEntries.find((item) => item.id === id);
          if (
            !existing ||
            !canViewEmployeeTime(
              state.currentUserId,
              existing.employeeId,
              state.employees,
              state.employeeReportsTo
            )
          ) {
            return;
          }
          const prepared = prepareTimeEntryPayload(entry);
          if (
            !prepared ||
            !canViewEmployeeTime(
              state.currentUserId,
              prepared.employeeId,
              state.employees,
              state.employeeReportsTo
            )
          ) {
            return;
          }
          set((s) => {
            const history = pushHistory(s);
            return {
              ...history,
              timeEntries: s.timeEntries.map((item) =>
                item.id === id ? { ...item, ...prepared } : item
              ),
            };
          });
        },

        removeTimeEntry: (id) => {
          const state = get();
          const entry = state.timeEntries.find((item) => item.id === id);
          if (
            !entry ||
            !canViewEmployeeTime(
              state.currentUserId,
              entry.employeeId,
              state.employees,
              state.employeeReportsTo
            )
          ) {
            return;
          }
          set((s) => {
            const history = pushHistory(s);
            return {
              ...history,
              timeEntries: s.timeEntries.filter((item) => item.id !== id),
            };
          });
        },



        addTask: (task) => {
          set((s) => {
            const history = pushHistory(s);
            let projects = s.projects;
            let taskNumber = task.taskNumber ?? null;

            if (task.projectId && !taskNumber) {
              const projectIndex = projects.findIndex((project) => project.id === task.projectId);
              if (projectIndex >= 0) {
                const project = projects[projectIndex]!;
                const next = nextTaskNumberForProject(project);
                taskNumber = next.taskNumber;
                projects = projects.map((entry, index) =>
                  index === projectIndex ? { ...entry, nextTaskNumber: next.nextTaskNumber } : entry
                );
              }
            }

            const normalized = normalizeTaskFields({
              ...task,
              taskNumber,
              parentTaskId: task.parentTaskId ?? null,
              id: uuid(),
              createdAt: new Date().toISOString(),
            });
            const newTask = applyAutoAssigneesToTask(
              normalized,
              projects,
              s.taskGroups,
              s.boardTaskStatuses,
              s.projectBoardTaskStatuses,
              { employees: s.employees, employeeJobTitles: s.employeeJobTitles }
            );
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                action: 'created',
                entityType: 'task',
                entityId: newTask.id,
                summary: `Created task "${newTask.title}"`,
                details: { board: newTask.boardType ?? 'main' },
              },
              uuid
            );
            return { ...history, projects, tasks: [...s.tasks, newTask], activityLog };
          });
        },

        importBoardroomPackageManifest: (rawManifest, exportFolder) => {
          const manifest = parseBoardroomPackageManifest(rawManifest);
          const folder = exportFolder.trim();
          if (!folder) {
            throw new Error('Export folder path is required.');
          }

          const state = get();
          const project = findProjectForManifest(state.projects, manifest);
          if (!project) {
            throw new Error(
              `Boardroom project id "${manifest.boardroomProject.id}" was not found. Start BIM Boardroom and pick a live project in SSv3.`
            );
          }

          const spoolingTask = findSpoolingTaskForManifest(state.tasks, manifest);
          if (!spoolingTask) {
            throw new Error(
              `Spooling task "${manifest.boardroomTask.id}" was not found on the Spooling board for this project. Pick an existing Spooling task in SSv3.`
            );
          }

          if (isSsv3ExportLocked(spoolingTask) && spoolingTaskHasSsv3Export(spoolingTask)) {
            throw new Error(
              `Cannot replace the SSv3 export on "${spoolingTask.title}" while it is Ready for Fab or already on the Fab board. Move the status back first, or clear after moving it off Fab.`
            );
          }

          const incomingExportedAt = manifest.exportedAt ?? '';
          const existingExportedAt = spoolingTask.customFields?.[SSV3_FIELD.exportedAt] ?? '';
          const existingFolder = spoolingTask.customFields?.[SSV3_FIELD.exportFolder] ?? '';
          if (
            spoolingTaskHasSsv3Export(spoolingTask) &&
            incomingExportedAt &&
            existingExportedAt === incomingExportedAt &&
            existingFolder === folder
          ) {
            // Same export already attached — do not wipe shop progress on watcher/startup.
            // Still repair Main Task paperclip attachments if they were never written.
            const exportFiles = filterFilesForSPackages(
              manifest.files,
              manifest.packages.map((batch) => batch.sPackage)
            );
            set((s) => {
              const actorId = resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId);
              const nextAttachments = upsertBoardroomAbsAttachments({
                taskId: spoolingTask.id,
                exportFolder: folder,
                files: exportFiles,
                taskAttachments: s.taskAttachments,
                actorId,
                createId: uuid,
                sPackage:
                  spoolingTask.customFields?.[SSV3_FIELD.package] ??
                  manifest.packages[0]?.sPackage ??
                  null,
              });
              if (nextAttachments === s.taskAttachments) return s;
              return { ...s, taskAttachments: nextAttachments };
            });
            return {
              projectId: project.id,
              spoolingTaskId: spoolingTask.id,
              packagesAttached: manifest.packages.length,
              promotedToFab: false,
              packagesUpserted: 0,
              assembliesUpserted: 0,
            };
          }

          let result: BoardroomPackageImportResult = {
            projectId: project.id,
            spoolingTaskId: spoolingTask.id,
            packagesAttached: manifest.packages.length,
            promotedToFab: false,
            packagesUpserted: 0,
            assembliesUpserted: 0,
          };

          set((s) => {
            const history = pushHistory(s);
            const actorId = resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId);
            let activityLog = s.activityLog;

            const priorRoot = s.tasks.find((task) => task.id === spoolingTask.id) ?? spoolingTask;
            const priorStatusByRevitId = new Map<string, { status: string; assigneeIds: string[] }>();
            const treeIds = new Set<string>([priorRoot.id]);
            let grew = true;
            while (grew) {
              grew = false;
              for (const task of s.tasks) {
                if (task.parentTaskId && treeIds.has(task.parentTaskId) && !treeIds.has(task.id)) {
                  treeIds.add(task.id);
                  grew = true;
                }
              }
            }
            for (const task of s.tasks) {
              if (!treeIds.has(task.id)) continue;
              if (task.customFields?.[SSV3_FIELD.kind] !== SSV3_KIND_ASSEMBLY) continue;
              const revitId = task.customFields?.[SSV3_FIELD.revitElementId];
              if (!revitId) continue;
              priorStatusByRevitId.set(revitId, {
                status: task.status,
                assigneeIds: [...(task.assigneeIds ?? [])],
              });
            }

            const wipedPreview = clearSsv3ExportFromSpoolingTask(
              spoolingTask,
              s.tasks,
              s.taskAttachments
            );
            const importRemovedIds = new Set(
              s.tasks
                .filter((task) => !wipedPreview.tasks.some((kept) => kept.id === task.id))
                .map((task) => task.id)
            );
            const importArchiveRoots = [...importRemovedIds].filter((id) => {
              const entry = s.tasks.find((task) => task.id === id);
              if (!entry?.parentTaskId) return true;
              return !importRemovedIds.has(entry.parentTaskId);
            });

            let deletedTaskArchive = s.deletedTaskArchive ?? [];
            let tasksForImport = s.tasks;
            let attachmentsForImport = s.taskAttachments;
            let commentsForImport = s.taskComments;

            if (importArchiveRoots.length > 0) {
              const soft = softDeleteTaskTrees({
                tasks: tasksForImport,
                taskAttachments: attachmentsForImport,
                taskComments: commentsForImport,
                deletedTaskArchive,
                activityLog,
                rootTaskIds: importArchiveRoots,
                actorId,
                reason: 'ssv3-reimport',
                createId: uuid,
                summaryForRoot: (task) =>
                  `Replaced SSv3 child "${task.title}" on re-import to "${spoolingTask.title}"`,
              });
              tasksForImport = soft.tasks;
              attachmentsForImport = soft.taskAttachments;
              commentsForImport = soft.taskComments;
              deletedTaskArchive = soft.deletedTaskArchive;
              activityLog = soft.activityLog;
            }

            const wiped = clearSsv3ExportFromSpoolingTask(
              tasksForImport.find((task) => task.id === spoolingTask.id) ?? spoolingTask,
              tasksForImport,
              attachmentsForImport
            );
            let tasks = wiped.tasks;
            let taskAttachments = wiped.taskAttachments;

            tasks = tasks.map((task) =>
              task.id === spoolingTask.id
                ? {
                    ...task,
                    customFields: buildSpoolingExportCustomFields(
                      task.customFields,
                      manifest,
                      folder
                    ),
                  }
                : task
            );

            let attached = tasks.find((task) => task.id === spoolingTask.id)!;
            let projects = s.projects;
            let packagesUpserted = 0;
            let assembliesUpserted = 0;
            let promotedToFab = false;

            const nested = attachSsv3HierarchyFromManifest(attached, manifest, {
              projects,
              tasks,
              taskGroups: s.taskGroups,
              boardTaskStatuses: s.boardTaskStatuses,
              projectBoardTaskStatuses: s.projectBoardTaskStatuses,
            });
            projects = nested.projects;
            tasks = nested.tasks;
            packagesUpserted = nested.packagesUpserted;
            assembliesUpserted = nested.assembliesUpserted;

            if (priorStatusByRevitId.size > 0) {
              tasks = tasks.map((task) => {
                if (task.customFields?.[SSV3_FIELD.kind] !== SSV3_KIND_ASSEMBLY) return task;
                const revitId = task.customFields?.[SSV3_FIELD.revitElementId];
                if (!revitId) return task;
                const prior = priorStatusByRevitId.get(revitId);
                if (!prior) return task;
                return {
                  ...task,
                  status: prior.status,
                  assigneeIds: prior.assigneeIds.length ? prior.assigneeIds : task.assigneeIds,
                };
              });
            }

            attached = tasks.find((task) => task.id === spoolingTask.id)!;

            if (
              attached.boardType === 'spooling' &&
              attached.status === 'ready-for-fab' &&
              spoolingTaskHasSsv3Export(attached)
            ) {
              const promoted = promoteSsv3SpoolingTaskToFab(attached, {
                projects,
                tasks,
                taskGroups: s.taskGroups,
                boardTaskStatuses: s.boardTaskStatuses,
                projectBoardTaskStatuses: s.projectBoardTaskStatuses,
              });
              projects = promoted.projects;
              tasks = promoted.tasks;
              packagesUpserted = promoted.packagesUpserted;
              assembliesUpserted = promoted.assembliesUpserted;
              promotedToFab = promoted.moved;
            }

            activityLog = logActivity(
              activityLog,
              {
                actorId,
                action: 'updated',
                entityType: 'task',
                entityId: spoolingTask.id,
                summary: wiped.cleared
                  ? `Replaced SSv3 export under Spooling task "${attached.title}"`
                  : `Attached SSv3 export under Spooling task "${attached.title}"`,
                details: {
                  board: attached.boardType,
                  packages: manifest.packages.map((p) => p.sPackage).join(', '),
                  promotedToFab,
                  replacedPriorExport: wiped.cleared,
                },
              },
              uuid
            );

            const exportFiles = filterFilesForSPackages(
              manifest.files,
              manifest.packages.map((batch) => batch.sPackage)
            );

            taskAttachments = upsertBoardroomAbsAttachments({
              taskId: spoolingTask.id,
              exportFolder: folder,
              files: exportFiles,
              taskAttachments,
              actorId,
              createId: uuid,
              sPackage:
                attached.customFields?.[SSV3_FIELD.package] ??
                manifest.packages[0]?.sPackage ??
                null,
            });

            result = {
              projectId: project.id,
              spoolingTaskId: spoolingTask.id,
              packagesAttached: manifest.packages.length,
              promotedToFab,
              packagesUpserted,
              assembliesUpserted,
            };

            return {
              ...history,
              projects,
              tasks,
              activityLog,
              taskAttachments,
              taskComments: commentsForImport,
              deletedTaskArchive,
            };
          });

          return result;
        },

        ensureBoardroomAttachmentsForTask: (taskId) => {
          const state = get();
          const task = state.tasks.find((entry) => entry.id === taskId);
          if (!task || !spoolingTaskHasSsv3Export(task)) return 0;

          const folder = (task.customFields?.[SSV3_FIELD.exportFolder] ?? '').trim();
          const sPackage = (task.customFields?.[SSV3_FIELD.package] ?? '').trim();
          if (!folder && !sPackage) return 0;

          const beforeCount = state.taskAttachments.filter((a) => a.taskId === taskId).length;
          set((s) => {
            const live = s.tasks.find((entry) => entry.id === taskId);
            if (!live) return s;
            const liveFolder = (live.customFields?.[SSV3_FIELD.exportFolder] ?? '').trim();
            const livePackage = (live.customFields?.[SSV3_FIELD.package] ?? '').trim();
            const liveFiles = parseSsv3Files(live, s.tasks);
            if (!liveFolder && !livePackage) return s;

            const actorId = resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId);
            const nextAttachments = upsertBoardroomAbsAttachments({
              taskId: live.id,
              exportFolder: liveFolder,
              files: liveFiles,
              taskAttachments: s.taskAttachments,
              actorId,
              createId: uuid,
              sPackage: livePackage || null,
            });
            if (nextAttachments === s.taskAttachments) return s;
            return { ...s, taskAttachments: nextAttachments };
          });

          return get().taskAttachments.filter((a) => a.taskId === taskId).length - beforeCount;
        },

        clearSsv3ExportFromTask: (taskId) => {
          const state = get();
          const task = state.tasks.find((entry) => entry.id === taskId);
          if (!task) {
            throw new Error('Task not found.');
          }
          if (task.boardType !== 'spooling') {
            throw new Error('Clear SSv3 export is only available on Spooling board tasks.');
          }
          if (!spoolingTaskHasSsv3Export(task)) {
            throw new Error('This task has no SSv3 export to clear.');
          }
          if (isSsv3ExportLocked(task)) {
            throw new Error(
              `Cannot clear the SSv3 export on "${task.title}" while it is Ready for Fab or already on the Fab board. Move the status back first.`
            );
          }

          set((s) => {
            const history = pushHistory(s);
            const root = s.tasks.find((entry) => entry.id === taskId);
            if (!root) return s;

            const preview = clearSsv3ExportFromSpoolingTask(root, s.tasks, s.taskAttachments);
            const removedIds = new Set(
              s.tasks
                .filter((entry) => !preview.tasks.some((kept) => kept.id === entry.id))
                .map((entry) => entry.id)
            );
            const archiveRoots = [...removedIds].filter((id) => {
              const entry = s.tasks.find((task) => task.id === id);
              if (!entry?.parentTaskId) return true;
              // Archive only top-most removed nodes so trees aren't double-archived.
              return !removedIds.has(entry.parentTaskId);
            });

            let tasks = s.tasks;
            let taskAttachments = s.taskAttachments;
            let taskComments = s.taskComments;
            let deletedTaskArchive = s.deletedTaskArchive ?? [];
            let activityLog = s.activityLog;

            if (archiveRoots.length > 0) {
              const soft = softDeleteTaskTrees({
                tasks,
                taskAttachments,
                taskComments,
                deletedTaskArchive,
                activityLog,
                rootTaskIds: archiveRoots,
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                reason: 'ssv3-clear',
                createId: uuid,
                summaryForRoot: (task) =>
                  `Cleared SSv3 child "${task.title}" from Spooling task "${root.title}"`,
              });
              tasks = soft.tasks;
              taskAttachments = soft.taskAttachments;
              taskComments = soft.taskComments;
              deletedTaskArchive = soft.deletedTaskArchive;
              activityLog = soft.activityLog;
            }

            const liveRoot = tasks.find((entry) => entry.id === taskId) ?? root;
            const wiped = clearSsv3ExportFromSpoolingTask(liveRoot, tasks, taskAttachments);
            if (!wiped.cleared && archiveRoots.length === 0) return s;

            activityLog = logActivity(
              activityLog,
              {
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                action: 'updated',
                entityType: 'task',
                entityId: taskId,
                summary: `Cleared SSv3 export from Spooling task "${root.title}"`,
                details: {
                  removedAssemblies: wiped.removedTaskCount || removedIds.size,
                  removedAttachments: wiped.removedAttachmentCount,
                },
              },
              uuid
            );
            return {
              ...history,
              tasks: wiped.tasks,
              taskAttachments: wiped.taskAttachments,
              taskComments,
              deletedTaskArchive,
              activityLog,
            };
          });
        },

        updateTask: (id, updates) => {
          set((s) => {
            const actorId = resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId);
            let activityLog = s.activityLog;
            let taskRevisionArchive = s.taskRevisionArchive ?? [];
            let projects = s.projects;
            let tasks = s.tasks.map((t) => {
              if (t.id !== id) return t;
              const enriched = enrichTaskUpdates(
                t,
                updates,
                s.projects,
                s.taskGroups,
                s.boardTaskStatuses,
                s.projectBoardTaskStatuses,
                s.employees,
                s.employeeJobTitles
              );
              const next = { ...t, ...enriched };
              if (updates.status && updates.status !== t.status) {
                const revision = buildTaskRevisionArchive({
                  task: t,
                  actorId,
                  createId: uuid,
                });
                taskRevisionArchive = prependTaskRevisionArchive(
                  taskRevisionArchive,
                  revision.archive
                );
                activityLog = logActivity(
                  activityLog,
                  {
                    actorId,
                    action: 'status_changed',
                    entityType: 'task',
                    entityId: id,
                    summary: `Changed status on "${next.title}"`,
                    details: { from: t.status, to: updates.status },
                    archiveId: revision.archiveId,
                  },
                  () => revision.activityLogId
                );
              } else if (
                updates.title !== undefined ||
                updates.groupId !== undefined ||
                updates.boardType !== undefined ||
                updates.assigneeIds !== undefined ||
                updates.description !== undefined ||
                updates.dueDate !== undefined ||
                updates.customFields !== undefined ||
                updates.durationFields !== undefined
              ) {
                const revision = buildTaskRevisionArchive({
                  task: t,
                  actorId,
                  createId: uuid,
                });
                taskRevisionArchive = prependTaskRevisionArchive(
                  taskRevisionArchive,
                  revision.archive
                );
                activityLog = logActivity(
                  activityLog,
                  {
                    actorId,
                    action: 'updated',
                    entityType: 'task',
                    entityId: id,
                    summary: `Updated task "${next.title}"`,
                    archiveId: revision.archiveId,
                  },
                  () => revision.activityLogId
                );
              }
              return next;
            });

            if (updates.status === 'ready-for-spooling') {
              tasks = cascadeReadyForSpoolingToAssemblies(
                tasks,
                [id],
                s.projects,
                s.taskGroups,
                s.boardTaskStatuses,
                s.projectBoardTaskStatuses,
                s.employees,
                s.employeeJobTitles
              );
            }

            const updated = tasks.find((t) => t.id === id);
            if (
              updated &&
              updates.status === 'ready-for-fab' &&
              updated.boardType === 'spooling' &&
              spoolingTaskHasSsv3Export(updated)
            ) {
              const promoted = promoteSsv3SpoolingTaskToFab(updated, {
                projects,
                tasks,
                taskGroups: s.taskGroups,
                boardTaskStatuses: s.boardTaskStatuses,
                projectBoardTaskStatuses: s.projectBoardTaskStatuses,
              });
              projects = promoted.projects;
              tasks = promoted.tasks;
            } else if (
              updated &&
              updates.status === 'spooling' &&
              updated.boardType === 'fab' &&
              !updated.parentTaskId &&
              spoolingTaskHasSsv3Export(updated)
            ) {
              const demoted = demoteSsv3FabTaskToSpooling(updated, {
                projects,
                tasks,
                taskGroups: s.taskGroups,
                boardTaskStatuses: s.boardTaskStatuses,
                projectBoardTaskStatuses: s.projectBoardTaskStatuses,
              });
              projects = demoted.projects;
              tasks = demoted.tasks;
              if (demoted.moved) {
                activityLog = logActivity(
                  activityLog,
                  {
                    actorId,
                    action: 'status_changed',
                    entityType: 'task',
                    entityId: id,
                    summary: `Moved "${updated.title}" back to Spooling`,
                    details: { from: 'fab', to: 'spooling' },
                  },
                  uuid
                );
              }
            } else if (
              updated &&
              updates.status === 'ready-to-ship' &&
              updated.boardType === 'fab' &&
              !updated.parentTaskId &&
              spoolingTaskHasSsv3Export(updated)
            ) {
              const shipped = promoteSsv3FabTaskToShipping(updated, {
                projects,
                tasks,
                taskGroups: s.taskGroups,
                boardTaskStatuses: s.boardTaskStatuses,
                projectBoardTaskStatuses: s.projectBoardTaskStatuses,
              });
              projects = shipped.projects;
              tasks = shipped.tasks;
              if (shipped.moved) {
                activityLog = logActivity(
                  activityLog,
                  {
                    actorId,
                    action: 'status_changed',
                    entityType: 'task',
                    entityId: id,
                    summary: `Moved "${updated.title}" to Shipping`,
                    details: { from: 'fab', to: 'shipping' },
                  },
                  uuid
                );
              }
            } else if (
              updated &&
              updates.status === 'received-field' &&
              updated.boardType === 'shipping' &&
              !updated.parentTaskId &&
              spoolingTaskHasSsv3Export(updated)
            ) {
              const handed = promoteSsv3ShippingTaskToField(updated, {
                projects,
                tasks,
                taskGroups: s.taskGroups,
                boardTaskStatuses: s.boardTaskStatuses,
                projectBoardTaskStatuses: s.projectBoardTaskStatuses,
              });
              projects = handed.projects;
              tasks = handed.tasks;
              if (handed.moved) {
                activityLog = logActivity(
                  activityLog,
                  {
                    actorId,
                    action: 'status_changed',
                    entityType: 'task',
                    entityId: id,
                    summary: `Moved "${updated.title}" to Field`,
                    details: { from: 'shipping', to: 'field' },
                  },
                  uuid
                );
              }
            } else if (
              updated &&
              updates.status === 'return-to-fab' &&
              updated.boardType === 'shipping' &&
              !updated.parentTaskId &&
              spoolingTaskHasSsv3Export(updated)
            ) {
              const demoted = demoteSsv3ShippingTaskToFab(updated, {
                projects,
                tasks,
                taskGroups: s.taskGroups,
                boardTaskStatuses: s.boardTaskStatuses,
                projectBoardTaskStatuses: s.projectBoardTaskStatuses,
              });
              projects = demoted.projects;
              tasks = demoted.tasks;
              if (demoted.moved) {
                activityLog = logActivity(
                  activityLog,
                  {
                    actorId,
                    action: 'status_changed',
                    entityType: 'task',
                    entityId: id,
                    summary: `Returned "${updated.title}" to Fab`,
                    details: { from: 'shipping', to: 'fab' },
                  },
                  uuid
                );
              }
            } else if (
              updated &&
              updates.status === 'spooling' &&
              updated.boardType === 'shipping' &&
              !updated.parentTaskId &&
              spoolingTaskHasSsv3Export(updated)
            ) {
              const demoted = demoteSsv3ShippingTaskToSpooling(updated, {
                projects,
                tasks,
                taskGroups: s.taskGroups,
                boardTaskStatuses: s.boardTaskStatuses,
                projectBoardTaskStatuses: s.projectBoardTaskStatuses,
              });
              projects = demoted.projects;
              tasks = demoted.tasks;
              if (demoted.moved) {
                activityLog = logActivity(
                  activityLog,
                  {
                    actorId,
                    action: 'status_changed',
                    entityType: 'task',
                    entityId: id,
                    summary: `Returned "${updated.title}" to Spooling`,
                    details: { from: 'shipping', to: 'spooling' },
                  },
                  uuid
                );
              }
            }

            const projectsChanged = projects !== s.projects;
            const revisionsChanged =
              taskRevisionArchive !== (s.taskRevisionArchive ?? []);
            if (
              activityLog === s.activityLog &&
              !projectsChanged &&
              !revisionsChanged
            ) {
              return { tasks };
            }
            return {
              tasks,
              ...(projectsChanged ? { projects } : {}),
              ...(activityLog !== s.activityLog ? { activityLog } : {}),
              ...(revisionsChanged ? { taskRevisionArchive } : {}),
            };
          });
        },

        updateTasks: (ids, updates) => {
          const idSet = new Set(ids);
          if (idSet.size === 0) return;
          set((s) => {
            let projects = s.projects;
            let tasks = s.tasks.map((t) => {
              if (!idSet.has(t.id)) return t;
              const enriched = enrichTaskUpdates(
                t,
                updates,
                s.projects,
                s.taskGroups,
                s.boardTaskStatuses,
                s.projectBoardTaskStatuses,
                s.employees,
                s.employeeJobTitles
              );
              return { ...t, ...enriched };
            });

            if (updates.status === 'ready-for-spooling') {
              tasks = cascadeReadyForSpoolingToAssemblies(
                tasks,
                idSet,
                s.projects,
                s.taskGroups,
                s.boardTaskStatuses,
                s.projectBoardTaskStatuses,
                s.employees,
                s.employeeJobTitles
              );
            }

            if (updates.status === 'ready-for-fab') {
              for (const id of idSet) {
                const updated = tasks.find((t) => t.id === id);
                if (
                  updated &&
                  updated.boardType === 'spooling' &&
                  spoolingTaskHasSsv3Export(updated)
                ) {
                  const promoted = promoteSsv3SpoolingTaskToFab(updated, {
                    projects,
                    tasks,
                    taskGroups: s.taskGroups,
                    boardTaskStatuses: s.boardTaskStatuses,
                    projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                  });
                  projects = promoted.projects;
                  tasks = promoted.tasks;
                }
              }
            } else if (updates.status === 'spooling') {
              for (const id of idSet) {
                const updated = tasks.find((t) => t.id === id);
                if (
                  updated &&
                  updated.boardType === 'fab' &&
                  !updated.parentTaskId &&
                  spoolingTaskHasSsv3Export(updated)
                ) {
                  const demoted = demoteSsv3FabTaskToSpooling(updated, {
                    projects,
                    tasks,
                    taskGroups: s.taskGroups,
                    boardTaskStatuses: s.boardTaskStatuses,
                    projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                  });
                  projects = demoted.projects;
                  tasks = demoted.tasks;
                } else if (
                  updated &&
                  updated.boardType === 'shipping' &&
                  !updated.parentTaskId &&
                  spoolingTaskHasSsv3Export(updated)
                ) {
                  const demoted = demoteSsv3ShippingTaskToSpooling(updated, {
                    projects,
                    tasks,
                    taskGroups: s.taskGroups,
                    boardTaskStatuses: s.boardTaskStatuses,
                    projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                  });
                  projects = demoted.projects;
                  tasks = demoted.tasks;
                }
              }
            } else if (updates.status === 'ready-to-ship') {
              for (const id of idSet) {
                const updated = tasks.find((t) => t.id === id);
                if (
                  updated &&
                  updated.boardType === 'fab' &&
                  !updated.parentTaskId &&
                  spoolingTaskHasSsv3Export(updated)
                ) {
                  const shipped = promoteSsv3FabTaskToShipping(updated, {
                    projects,
                    tasks,
                    taskGroups: s.taskGroups,
                    boardTaskStatuses: s.boardTaskStatuses,
                    projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                  });
                  projects = shipped.projects;
                  tasks = shipped.tasks;
                }
              }
            } else if (updates.status === 'received-field') {
              for (const id of idSet) {
                const updated = tasks.find((t) => t.id === id);
                if (
                  updated &&
                  updated.boardType === 'shipping' &&
                  !updated.parentTaskId &&
                  spoolingTaskHasSsv3Export(updated)
                ) {
                  const handed = promoteSsv3ShippingTaskToField(updated, {
                    projects,
                    tasks,
                    taskGroups: s.taskGroups,
                    boardTaskStatuses: s.boardTaskStatuses,
                    projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                  });
                  projects = handed.projects;
                  tasks = handed.tasks;
                }
              }
            } else if (updates.status === 'return-to-fab') {
              for (const id of idSet) {
                const updated = tasks.find((t) => t.id === id);
                if (
                  updated &&
                  updated.boardType === 'shipping' &&
                  !updated.parentTaskId &&
                  spoolingTaskHasSsv3Export(updated)
                ) {
                  const demoted = demoteSsv3ShippingTaskToFab(updated, {
                    projects,
                    tasks,
                    taskGroups: s.taskGroups,
                    boardTaskStatuses: s.boardTaskStatuses,
                    projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                  });
                  projects = demoted.projects;
                  tasks = demoted.tasks;
                }
              }
            }

            return projects !== s.projects ? { tasks, projects } : { tasks };
          });
        },

        updateTasksWith: (ids, updater) => {
          const idSet = new Set(ids);
          if (idSet.size === 0) return;
          set((s) => {
            let projects = s.projects;
            const previousById = new Map(s.tasks.map((t) => [t.id, t]));
            let tasks = s.tasks.map((t) => {
              if (!idSet.has(t.id)) return t;
              const rawUpdates = updater(t);
              const enriched = enrichTaskUpdates(
                t,
                rawUpdates,
                s.projects,
                s.taskGroups,
                s.boardTaskStatuses,
                s.projectBoardTaskStatuses,
                s.employees,
                s.employeeJobTitles
              );
              return { ...t, ...enriched };
            });

            const readyForSpoolingRoots = [...idSet].filter(
              (id) => tasks.find((t) => t.id === id)?.status === 'ready-for-spooling'
            );
            if (readyForSpoolingRoots.length > 0) {
              tasks = cascadeReadyForSpoolingToAssemblies(
                tasks,
                readyForSpoolingRoots,
                s.projects,
                s.taskGroups,
                s.boardTaskStatuses,
                s.projectBoardTaskStatuses,
                s.employees,
                s.employeeJobTitles
              );
            }

            for (const id of idSet) {
              const previous = previousById.get(id);
              const updated = tasks.find((t) => t.id === id);
              if (
                previous &&
                updated &&
                previous.status !== 'ready-for-fab' &&
                updated.status === 'ready-for-fab' &&
                updated.boardType === 'spooling' &&
                spoolingTaskHasSsv3Export(updated)
              ) {
                const promoted = promoteSsv3SpoolingTaskToFab(updated, {
                  projects,
                  tasks,
                  taskGroups: s.taskGroups,
                  boardTaskStatuses: s.boardTaskStatuses,
                  projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                });
                projects = promoted.projects;
                tasks = promoted.tasks;
              } else if (
                previous &&
                updated &&
                previous.status !== 'spooling' &&
                updated.status === 'spooling' &&
                updated.boardType === 'fab' &&
                !updated.parentTaskId &&
                spoolingTaskHasSsv3Export(updated)
              ) {
                const demoted = demoteSsv3FabTaskToSpooling(updated, {
                  projects,
                  tasks,
                  taskGroups: s.taskGroups,
                  boardTaskStatuses: s.boardTaskStatuses,
                  projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                });
                projects = demoted.projects;
                tasks = demoted.tasks;
              } else if (
                previous &&
                updated &&
                previous.status !== 'spooling' &&
                updated.status === 'spooling' &&
                updated.boardType === 'shipping' &&
                !updated.parentTaskId &&
                spoolingTaskHasSsv3Export(updated)
              ) {
                const demoted = demoteSsv3ShippingTaskToSpooling(updated, {
                  projects,
                  tasks,
                  taskGroups: s.taskGroups,
                  boardTaskStatuses: s.boardTaskStatuses,
                  projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                });
                projects = demoted.projects;
                tasks = demoted.tasks;
              } else if (
                previous &&
                updated &&
                previous.status !== 'ready-to-ship' &&
                updated.status === 'ready-to-ship' &&
                updated.boardType === 'fab' &&
                !updated.parentTaskId &&
                spoolingTaskHasSsv3Export(updated)
              ) {
                const shipped = promoteSsv3FabTaskToShipping(updated, {
                  projects,
                  tasks,
                  taskGroups: s.taskGroups,
                  boardTaskStatuses: s.boardTaskStatuses,
                  projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                });
                projects = shipped.projects;
                tasks = shipped.tasks;
              } else if (
                previous &&
                updated &&
                previous.status !== 'received-field' &&
                updated.status === 'received-field' &&
                updated.boardType === 'shipping' &&
                !updated.parentTaskId &&
                spoolingTaskHasSsv3Export(updated)
              ) {
                const handed = promoteSsv3ShippingTaskToField(updated, {
                  projects,
                  tasks,
                  taskGroups: s.taskGroups,
                  boardTaskStatuses: s.boardTaskStatuses,
                  projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                });
                projects = handed.projects;
                tasks = handed.tasks;
              } else if (
                previous &&
                updated &&
                previous.status !== 'return-to-fab' &&
                updated.status === 'return-to-fab' &&
                updated.boardType === 'shipping' &&
                !updated.parentTaskId &&
                spoolingTaskHasSsv3Export(updated)
              ) {
                const demoted = demoteSsv3ShippingTaskToFab(updated, {
                  projects,
                  tasks,
                  taskGroups: s.taskGroups,
                  boardTaskStatuses: s.boardTaskStatuses,
                  projectBoardTaskStatuses: s.projectBoardTaskStatuses,
                });
                projects = demoted.projects;
                tasks = demoted.tasks;
              }
            }

            return projects !== s.projects ? { tasks, projects } : { tasks };
          });
        },

        refreshTasksAutoAssign: (ids) => {
          const idSet = new Set(ids);
          if (idSet.size === 0) return;
          set((s) => ({
            tasks: s.tasks.map((t) => {
              if (!idSet.has(t.id)) return t;
              return applyAutoAssigneesToTask(
                t,
                s.projects,
                s.taskGroups,
                s.boardTaskStatuses,
                s.projectBoardTaskStatuses,
                { force: true, employees: s.employees, employeeJobTitles: s.employeeJobTitles }
              );
            }),
          }));
        },

        refreshActiveView: () => {
          set((s) => {
            const ids = new Set(taskIdsForActiveView(s));
            if (ids.size === 0) return s;
            return {
              tasks: s.tasks.map((t) => {
                if (!ids.has(t.id)) return t;
                const reconciled = reconcileTaskAssigneeLock(
                  t,
                  s.projects,
                  s.taskGroups,
                  s.boardTaskStatuses,
                  s.projectBoardTaskStatuses,
                  s.employees,
                  s.employeeJobTitles
                );
                if (reconciled.assigneesLocked) return reconciled;
                return applyAutoAssigneesToTask(
                  reconciled,
                  s.projects,
                  s.taskGroups,
                  s.boardTaskStatuses,
                  s.projectBoardTaskStatuses,
                  { employees: s.employees, employeeJobTitles: s.employeeJobTitles }
                );
              }),
            };
          });
        },

        removeTask: (id) => {
          set((s) => {
            const history = pushHistory(s);
            const soft = softDeleteTaskTrees({
              tasks: s.tasks,
              taskAttachments: s.taskAttachments,
              taskComments: s.taskComments,
              deletedTaskArchive: s.deletedTaskArchive ?? [],
              activityLog: s.activityLog,
              rootTaskIds: [id],
              actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
              reason: 'user',
              createId: uuid,
            });
            if (soft.removedIds.size === 0) return s;
            return {
              ...history,
              tasks: soft.tasks,
              taskAttachments: soft.taskAttachments,
              taskComments: soft.taskComments,
              deletedTaskArchive: soft.deletedTaskArchive,
              activityLog: soft.activityLog,
            };
          });
        },

        removeTasks: (ids) => {
          if (ids.length === 0) return;
          set((s) => {
            const history = pushHistory(s);
            const soft = softDeleteTaskTrees({
              tasks: s.tasks,
              taskAttachments: s.taskAttachments,
              taskComments: s.taskComments,
              deletedTaskArchive: s.deletedTaskArchive ?? [],
              activityLog: s.activityLog,
              rootTaskIds: ids,
              actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
              reason: 'user',
              createId: uuid,
            });
            if (soft.removedIds.size === 0) return s;
            return {
              ...history,
              tasks: soft.tasks,
              taskAttachments: soft.taskAttachments,
              taskComments: soft.taskComments,
              deletedTaskArchive: soft.deletedTaskArchive,
              activityLog: soft.activityLog,
            };
          });
        },

        restoreDeletedTask: (archiveId) => {
          const state = get();
          const archive = (state.deletedTaskArchive ?? []).find((entry) => entry.id === archiveId);
          if (!archive || archive.restoredAt) return false;
          const restored = applyRestoredTaskArchive(archive, state);
          if (!restored) return false;

          const actorId = resolveActivityActorId(state.currentUserId, state.viewAsOriginalUserId);
          const rootTitle = archive.tasks.find((task) => task.id === archive.rootTaskId)?.title
            ?? archive.tasks[0]?.title
            ?? 'task';

          set((s) => {
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId,
                action: 'restored',
                entityType: 'task',
                entityId: archive.rootTaskId,
                summary: `Restored task "${rootTitle}"`,
                details: {
                  title: rootTitle,
                  taskCount: archive.tasks.length,
                  archiveId: archive.id,
                },
                archiveId: archive.id,
              },
              uuid
            );
            return {
              ...pushHistory(s),
              tasks: restored.tasks,
              taskAttachments: restored.taskAttachments,
              taskComments: restored.taskComments,
              deletedTaskArchive: (s.deletedTaskArchive ?? []).map((entry) =>
                entry.id === archiveId
                  ? { ...entry, restoredAt: new Date().toISOString(), restoredById: actorId }
                  : entry
              ),
              activityLog: activityLog.map((entry) =>
                entry.id === archive.activityLogId
                  ? { ...entry, restoredAt: new Date().toISOString(), restoredById: actorId }
                  : entry
              ),
            };
          });
          return true;
        },

        restoreTaskActivity: (activityLogId) => {
          const state = get();
          const entry = (state.activityLog ?? []).find((item) => item.id === activityLogId);
          if (!entry || entry.entityType !== 'task' || entry.restoredAt) return false;
          if (entry.action === 'restored' || entry.action === 'created') return false;

          const actorId = resolveActivityActorId(state.currentUserId, state.viewAsOriginalUserId);
          const now = new Date().toISOString();

          // Soft-deleted task tree
          if (entry.action === 'deleted' && entry.archiveId) {
            return get().restoreDeletedTask(entry.archiveId);
          }

          const revision =
            (entry.archiveId
              ? (state.taskRevisionArchive ?? []).find((item) => item.id === entry.archiveId)
              : undefined) ??
            (state.taskRevisionArchive ?? []).find((item) => item.activityLogId === entry.id);

          if (revision && !revision.restoredAt) {
            const live = state.tasks.find((task) => task.id === revision.taskId);
            const before = revision.before;
            set((s) => {
              const groupIds = new Set(s.taskGroups.map((group) => group.id));
              const restoredTask = {
                ...before,
                groupId: before.groupId && groupIds.has(before.groupId) ? before.groupId : null,
              };
              const tasks = live
                ? s.tasks.map((task) => (task.id === revision.taskId ? restoredTask : task))
                : [...s.tasks, restoredTask];
              const activityLog = logActivity(
                s.activityLog.map((item) =>
                  item.id === entry.id
                    ? { ...item, restoredAt: now, restoredById: actorId }
                    : item
                ),
                {
                  actorId,
                  action: 'restored',
                  entityType: 'task',
                  entityId: revision.taskId,
                  summary: `Restored task "${restoredTask.title}" to earlier state`,
                  details: {
                    title: restoredTask.title,
                    fromActivity: entry.action,
                    archiveId: revision.id,
                  },
                  archiveId: revision.id,
                },
                uuid
              );
              return {
                ...pushHistory(s),
                tasks,
                taskRevisionArchive: (s.taskRevisionArchive ?? []).map((item) =>
                  item.id === revision.id
                    ? { ...item, restoredAt: now, restoredById: actorId }
                    : item
                ),
                activityLog,
              };
            });
            return true;
          }

          // Legacy status_changed rows (no revision archive) — revert using details.from
          if (
            entry.action === 'status_changed' &&
            typeof entry.details?.from === 'string' &&
            entry.details.from
          ) {
            const live = state.tasks.find((task) => task.id === entry.entityId);
            if (!live) return false;
            const fromStatus = entry.details.from;
            set((s) => {
              const tasks = s.tasks.map((task) =>
                task.id === entry.entityId ? { ...task, status: fromStatus } : task
              );
              const activityLog = logActivity(
                s.activityLog.map((item) =>
                  item.id === entry.id
                    ? { ...item, restoredAt: now, restoredById: actorId }
                    : item
                ),
                {
                  actorId,
                  action: 'restored',
                  entityType: 'task',
                  entityId: entry.entityId,
                  summary: `Restored status on "${live.title}" to "${fromStatus}"`,
                  details: {
                    title: live.title,
                    status: fromStatus,
                  },
                },
                uuid
              );
              return {
                ...pushHistory(s),
                tasks,
                activityLog,
              };
            });
            return true;
          }

          return false;
        },

        moveTask: (taskId, updates) => {
          set((s) => {
            const history = pushHistory(s);
            return {
              ...history,
              tasks: s.tasks.map((t) => {
                if (t.id !== taskId) return t;
                const enriched = enrichTaskUpdates(
                  t,
                  updates,
                  s.projects,
                  s.taskGroups,
                  s.boardTaskStatuses,
                  s.projectBoardTaskStatuses,
                  s.employees,
                  s.employeeJobTitles
                );
                return { ...t, ...enriched };
              }),
            };
          });
        },

        reorderEmployeeTasks: (assigneeId, taskIds) => {
          set((s) => {
            const history = pushHistory(s);
            const updated = [...s.tasks];
            taskIds.forEach((taskId, index) => {
              const idx = updated.findIndex((t) => t.id === taskId);
              if (idx !== -1 && taskHasAssignee(updated[idx], assigneeId)) {
                updated[idx] = { ...updated[idx], priority: index };
              }
            });
            return { ...history, tasks: updated };
          });
        },

        reorderSubBoardTabs: (order) => {
          const normalized = normalizeSubBoardTabOrder(order);
          set((s) => {
            const history = pushHistory(s);
            return {
              ...history,
              subBoardTabOrder: normalized,
              taskGroups: syncSectionSortOrder(s.taskGroups, normalized),
            };
          });
        },

        reorderProjectBoardTabs: (projectId, order) => {
          set((s) => {
            const history = pushHistory(s);
            const builtInOrder = order.filter(
              (id): id is BuiltInProjectBoardType => !isCustomBoardId(id)
            );
            const normalized = normalizeSubBoardTabOrder(builtInOrder);
            const customBoards = s.customBoards.map((board) => {
              if (board.projectId !== projectId) return board;
              const idx = order.indexOf(board.id);
              if (idx === -1) return board;
              return { ...board, sortOrder: idx };
            });
            return {
              ...history,
              subBoardTabOrder: normalized,
              customBoards,
              taskGroups: syncSectionSortOrder(s.taskGroups, order, projectId),
            };
          });
        },

        addCustomBoard: (clientId, projectId, name) => {
          const trimmed = name.trim();
          if (!trimmed) return null;
          const boardId: CustomBoard['id'] = `cb-${uuid()}`;
          set((s) => {
            const history = pushHistory(s);
            const projectCustom = s.customBoards.filter((b) => b.projectId === projectId);
            const sortOrder = s.subBoardTabOrder.length + projectCustom.length;
            const board: CustomBoard = {
              id: boardId,
              name: trimmed,
              clientId,
              projectId,
              sortOrder,
            };
            const section: TaskGroup = {
              id: uuid(),
              name: defaultSectionName(boardId, [...s.customBoards, board]),
              clientId,
              projectId,
              boardType: 'main',
              tier: 'section',
              parentId: null,
              sectionBoardType: boardId,
              sortOrder,
            };
            const fullOrder = getProjectSubBoardOrder(projectId, s.subBoardTabOrder, [
              ...s.customBoards,
              board,
            ]);
            return {
              ...history,
              customBoards: [...s.customBoards, board],
              boardTaskStatuses: {
                ...s.boardTaskStatuses,
                [boardId]: getBoardTaskStatuses('main', s.boardTaskStatuses).map((st) => ({ ...st })),
              },
              boardSheetColumns: {
                ...s.boardSheetColumns,
                [boardId]: getBoardSheetColumns('main', s.boardSheetColumns).map((c) => ({ ...c })),
              },
              boardSheetColumnOrder: {
                ...s.boardSheetColumnOrder,
                [boardId]: defaultBoardColumnOrder(
                  getBoardSheetColumns('main', s.boardSheetColumns),
                  false,
                  boardId
                ),
              },
              taskGroups: syncSectionSortOrder([...s.taskGroups, section], fullOrder, projectId),
              activeBoardType: boardId,
            };
          });
          return boardId;
        },

        addBoardTaskStatus: (
          boardType,
          label,
          autoAssignTeam,
          projectId,
          applyToAllDeliverables,
          autoAssignEmployeeId
        ) => {
          if (isBoardStatusListLocked(boardType)) return null;
          const trimmed = label.trim();
          if (!trimmed) return null;
          const id = `status-${uuid()}`;
          set((s) => {
            const current = getBoardTaskStatuses(
              boardType,
              s.boardTaskStatuses,
              projectId,
              s.projectBoardTaskStatuses
            );
            const next = [
              ...current,
              {
                id,
                label: trimmed,
                color: pickNewStatusColor(current.length),
                countsAsComplete: false,
                ...(autoAssignTeam !== undefined ? { autoAssignTeam } : {}),
                ...(autoAssignEmployeeId !== undefined
                  ? { autoAssignEmployeeId }
                  : {}),
              },
            ];
            return commitBoardTaskStatusList(
              s,
              boardType,
              next,
              projectId,
              applyToAllDeliverables
            );
          });
          return id;
        },

        removeBoardTaskStatus: (boardType, id, projectId, applyToAllDeliverables) => {
          if (isBoardStatusListLocked(boardType)) return;
          set((s) => {
            const current = getBoardTaskStatuses(
              boardType,
              s.boardTaskStatuses,
              projectId,
              s.projectBoardTaskStatuses
            );
            if (current.length <= 1) return s;
            const remaining = current.filter((st) => st.id !== id);
            const fallback = remaining[0]?.id;
            if (!fallback) return s;
            const history = pushHistory(s);
            return {
              ...history,
              ...commitBoardTaskStatusList(
                s,
                boardType,
                remaining,
                projectId,
                applyToAllDeliverables
              ),
              tasks: s.tasks.map((t) =>
                (!projectId || t.projectId === projectId) &&
                statusBoardForTask(t, s.taskGroups) === boardType &&
                t.status === id
                  ? { ...t, status: fallback }
                  : t
              ),
            };
          });
        },

        updateBoardTaskStatus: (boardType, id, updates, projectId, applyToAllDeliverables) => {
          if (isDashboardDrivenStatusBoard(boardType)) return;
          set((s) => {
            const current = getBoardTaskStatuses(
              boardType,
              s.boardTaskStatuses,
              projectId,
              s.projectBoardTaskStatuses
            );
            const next = current.map((st) => (st.id === id ? { ...st, ...updates } : st));
            const committed = commitBoardTaskStatusList(
              s,
              boardType,
              next,
              projectId,
              applyToAllDeliverables
            );
            if (!updates.color) return committed;
            const globalColors = applyStatusColorGlobally(
              id,
              updates.color,
              committed.boardTaskStatuses,
              committed.projectBoardTaskStatuses
            );
            return {
              ...committed,
              boardTaskStatuses: globalColors.boardTaskStatuses,
              projectBoardTaskStatuses: globalColors.projectBoardTaskStatuses,
            };
          });
        },

        reorderBoardTaskStatuses: (boardType, statusIds, projectId, applyToAllDeliverables) => {
          if (isBoardStatusListLocked(boardType)) return;
          set((s) => {
            const current = getBoardTaskStatuses(
              boardType,
              s.boardTaskStatuses,
              projectId,
              s.projectBoardTaskStatuses
            );
            const byId = new Map(current.map((status) => [status.id, status]));
            const reordered = statusIds
              .map((statusId) => byId.get(statusId))
              .filter((status): status is TaskStatusDefinition => Boolean(status));
            if (reordered.length !== current.length) return s;
            return commitBoardTaskStatusList(
              s,
              boardType,
              reordered,
              projectId,
              applyToAllDeliverables
            );
          });
        },

        syncStatusColorsAcrossProjects: () => {
          let syncedCount = 0;
          set((s) => {
            const consolidated = consolidateDuplicateStatuses(
              s.boardTaskStatuses,
              s.projectBoardTaskStatuses,
              s.tasks,
              s.taskBoardVisibleStatuses
            );
            const synced = syncStatusColorMaps(
              consolidated.boardTaskStatuses,
              consolidated.projectBoardTaskStatuses
            );
            if (consolidated.consolidatedCount === 0 && synced.syncedCount === 0) return s;
            syncedCount = consolidated.consolidatedCount + synced.syncedCount;
            const history = pushHistory(s);
            return {
              ...history,
              boardTaskStatuses: synced.boardTaskStatuses,
              projectBoardTaskStatuses: synced.projectBoardTaskStatuses,
              tasks: consolidated.tasks,
              taskBoardVisibleStatuses: consolidated.taskBoardVisibleStatuses,
            };
          });
          return syncedCount;
        },

        addBoardSheetColumn: (boardType, label, type, options, headerAlignment, cellAlignment) => {
          const trimmed = label.trim();
          if (!trimmed) return null;
          const id = `col-${uuid()}`;
          const column: import('../types').SheetColumnDefinition = {
            id,
            label: trimmed,
            type,
            headerAlignment: headerAlignment ?? 'center',
            cellAlignment: cellAlignment ?? 'center',
          };
          if (type === 'dropdown') {
            column.options = (options ?? ['Option 1', 'Option 2'])
              .map((o) => o.trim())
              .filter(Boolean);
          }
          set((s) => {
            const isOverview = boardType === 'main';
            const order = getBoardSheetColumnOrder(
              boardType,
              s.boardSheetColumnOrder,
              s.boardSheetColumns,
              isOverview
            );
            const boardIdx = order.indexOf('board');
            const nextOrder =
              boardIdx >= 0
                ? [...order.slice(0, boardIdx), id, ...order.slice(boardIdx)]
                : [...order, id];
            const allBoardTypes = getAllConfiguredBoardTypes(s.customBoards);
            const nextBoardSheetColumns =
              boardType === 'main'
                ? propagateMainSheetColumnToAllBoards(
                    appendSheetColumnDefinition(s.boardSheetColumns, 'main', column),
                    column,
                    allBoardTypes
                  )
                : {
                    ...s.boardSheetColumns,
                    [boardType]: [...getBoardLocalSheetColumns(boardType, s.boardSheetColumns), column],
                  };
            return {
              boardSheetColumns: nextBoardSheetColumns,
              boardSheetColumnOrder: {
                ...s.boardSheetColumnOrder,
                [boardType]: nextOrder,
              },
            };
          });
          return id;
        },

        removeBoardSheetColumn: (boardType, id) => {
          set((s) => {
            const actorId = resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId);
            if (!canManageColumns(actorId, s.employees, s.employeePermissions)) return s;
            if (isProtectedBoardColumnId(id)) return s;

            const sharedFromMain = isMainOverviewSharedColumn(id, s.boardSheetColumns);
            const column =
              findColumnDefinition(
                boardType,
                id,
                s.boardSheetColumns,
                s.mainOverviewSectionSheetColumns
              ) ?? (isFixedSheetColumnId(id) ? fixedColumnAsDefinition(id) : null);
            if (!column) return s;

            const isOverview = boardType === 'main';
            const columnOrderBefore = getBoardSheetColumnOrder(
              boardType,
              s.boardSheetColumnOrder,
              s.boardSheetColumns,
              isOverview
            );
            if (!columnOrderBefore.includes(id)) return s;

            const activityLogId = uuid();
            const archiveId = uuid();
            // Fixed columns and Main-shared columns on sub-boards: remove from this board's
            // order only so definitions (and other boards) stay intact.
            const orderOnly =
              isFixedSheetColumnId(id) || (sharedFromMain && boardType !== 'main');

            const archive = buildColumnDeleteArchive({
              archiveId,
              activityLogId,
              actorId,
              boardType,
              sectionBoardType: null,
              column,
              columnOrderBefore,
              wasMainOverviewShared: sharedFromMain && boardType === 'main',
              tasks: s.tasks,
              taskGroups: s.taskGroups,
            });
            const history = pushHistory(s);
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId,
                action: 'deleted',
                entityType: 'column',
                entityId: id,
                summary: columnActivitySummary(
                  'deleted',
                  column,
                  boardType,
                  null,
                  s.customBoards
                ),
                archiveId,
                details: { column: column.label, board: boardType },
              },
              uuid
            );

            if (orderOnly) {
              return {
                ...history,
                boardSheetColumnOrder: {
                  ...s.boardSheetColumnOrder,
                  [boardType]: columnOrderBefore.filter((columnId) => columnId !== id),
                },
                activityLog,
                deletedColumnArchive: [archive, ...s.deletedColumnArchive],
              };
            }

            const stripped = stripColumnFromState(boardType, id, s);
            return {
              ...history,
              ...stripped,
              activityLog,
              deletedColumnArchive: [archive, ...s.deletedColumnArchive],
            };
          });
        },

        updateBoardSheetColumn: (boardType, id, updates) => {
          set((s) => {
            const sharedFromMain = isMainOverviewSharedColumn(id, s.boardSheetColumns);
            const allBoardTypes = getAllConfiguredBoardTypes(s.customBoards);
            const nextBoardSheetColumns = sharedFromMain
              ? syncMainSheetColumnUpdateToAllBoards(
                  s.boardSheetColumns,
                  id,
                  updates,
                  allBoardTypes
                )
              : {
                  ...s.boardSheetColumns,
                  [boardType]: getBoardLocalSheetColumns(boardType, s.boardSheetColumns).map((col) => {
                    if (col.id !== id) return col;
                    const next = { ...col, ...updates };
                    if (next.type === 'dropdown') {
                      next.options = (next.options ?? [])
                        .map((o) => o.trim())
                        .filter(Boolean);
                    } else {
                      delete next.options;
                    }
                    return next;
                  }),
                };
            return {
              boardSheetColumns: nextBoardSheetColumns,
            };
          });
        },

        reorderBoardSheetColumns: (boardType, columnOrder) => {
          set((s) => {
            const isOverview = boardType === 'main';
            const normalized = getBoardSheetColumnOrder(
              boardType,
              { [boardType]: columnOrder },
              s.boardSheetColumns,
              isOverview
            );
            // Sub-board tabs and Main Overview sections share one column layout.
            if (!isOverview) {
              const overviewNormalized = resolveStoredMainOverviewSectionColumnOrder(
                boardType,
                normalized,
                s.mainOverviewSectionSheetColumns,
                s.boardSheetColumns
              );
              return {
                boardSheetColumnOrder: {
                  ...s.boardSheetColumnOrder,
                  [boardType]: normalized,
                },
                mainOverviewSectionColumnOrder: {
                  ...s.mainOverviewSectionColumnOrder,
                  [boardType]: overviewNormalized,
                },
              };
            }
            return {
              boardSheetColumnOrder: {
                ...s.boardSheetColumnOrder,
                [boardType]: normalized,
              },
            };
          });
        },

        reorderMainOverviewSectionColumns: (sectionBoardType, columnOrder) => {
          set((s) => {
            const normalized = resolveStoredMainOverviewSectionColumnOrder(
              sectionBoardType,
              columnOrder,
              s.mainOverviewSectionSheetColumns,
              s.boardSheetColumns
            );
            const boardOrder = normalized.filter((id) => id !== 'board');
            return {
              mainOverviewSectionColumnOrder: {
                ...s.mainOverviewSectionColumnOrder,
                [sectionBoardType]: normalized,
              },
              boardSheetColumnOrder: {
                ...s.boardSheetColumnOrder,
                [sectionBoardType]: boardOrder,
              },
            };
          });
        },

        addMainOverviewSectionColumn: (
          sectionBoardType,
          label,
          type,
          options,
          headerAlignment,
          cellAlignment
        ) => {
          const trimmed = label.trim();
          if (!trimmed) return null;
          const id = `col-${uuid()}`;
          const column: import('../types').SheetColumnDefinition = {
            id,
            label: trimmed,
            type,
            headerAlignment: headerAlignment ?? 'center',
            cellAlignment: cellAlignment ?? 'center',
          };
          if (type === 'dropdown') {
            column.options = (options ?? ['Option 1', 'Option 2'])
              .map((option) => option.trim())
              .filter(Boolean);
          }
          set((s) => {
            const currentExtras =
              s.mainOverviewSectionSheetColumns[sectionBoardType] ?? [];
            const nextSectionColumns = [...currentExtras, column];
            const nextSectionSheetColumns = {
              ...s.mainOverviewSectionSheetColumns,
              [sectionBoardType]: nextSectionColumns,
            };
            const order = getMainOverviewSectionColumnOrder(
              sectionBoardType,
              s.mainOverviewSectionColumnOrder,
              nextSectionSheetColumns,
              s.boardSheetColumnOrder,
              s.boardSheetColumns
            );
            const boardIdx = order.indexOf('board');
            const nextOrder =
              boardIdx >= 0
                ? [...order.slice(0, boardIdx), id, ...order.slice(boardIdx)]
                : [...order, id];
            return {
              mainOverviewSectionSheetColumns: nextSectionSheetColumns,
              mainOverviewSectionColumnOrder: {
                ...s.mainOverviewSectionColumnOrder,
                [sectionBoardType]: nextOrder,
              },
            };
          });
          return id;
        },

        addPremadeBoardColumn: (boardType, premadeId) => {
          let added = false;
          set((s) => {
            const next = appendPremadeColumnToBoardState(
              boardType,
              premadeId,
              s.boardSheetColumns,
              s.boardSheetColumnOrder
            );
            if (!next) return s;
            added = true;
            return {
              boardSheetColumns: next.boardSheetColumns,
              boardSheetColumnOrder: next.boardSheetColumnOrder,
            };
          });
          return added;
        },

        addPremadeOverviewSectionColumn: (sectionBoardType, premadeId) => {
          let added = false;
          set((s) => {
            const next = appendPremadeColumnToOverviewSectionState(
              sectionBoardType,
              premadeId,
              s.boardSheetColumns,
              s.boardSheetColumnOrder,
              s.mainOverviewSectionColumnOrder,
              s.mainOverviewSectionSheetColumns
            );
            if (!next) return s;
            added = true;
            return {
              boardSheetColumns: next.boardSheetColumns,
              boardSheetColumnOrder: next.boardSheetColumnOrder,
              mainOverviewSectionColumnOrder: next.mainOverviewSectionColumnOrder,
            };
          });
          return added;
        },

        addPremadeColumnsToTargets: (targets, premadeIds, mode) => {
          if (!targets.length || !premadeIds.length) return 0;
          let addedCount = 0;
          set((s) => {
            const result = applyPremadeColumnsToTargets(
              targets,
              premadeIds,
              mode,
              s.boardSheetColumns,
              s.boardSheetColumnOrder,
              s.mainOverviewSectionColumnOrder,
              s.mainOverviewSectionSheetColumns
            );
            addedCount = result.addedCount;
            if (addedCount === 0) return s;
            return {
              boardSheetColumns: result.boardSheetColumns,
              boardSheetColumnOrder: result.boardSheetColumnOrder,
              mainOverviewSectionColumnOrder: result.mainOverviewSectionColumnOrder,
            };
          });
          return addedCount;
        },

        addCustomColumnToTargets: (
          targets,
          mode,
          label,
          type,
          options,
          headerAlignment,
          cellAlignment,
          saveToLibrary
        ) => {
          const trimmed = label.trim();
          if (!trimmed || !targets.length) return null;
          const columnId = `col-${uuid()}`;
          const column = buildSheetColumnDefinition(
            columnId,
            trimmed,
            type,
            options,
            headerAlignment,
            cellAlignment
          );
          set((s) => {
            const applied = applyCustomColumnToTargets(
              targets,
              column,
              mode,
              s.boardSheetColumns,
              s.boardSheetColumnOrder,
              s.mainOverviewSectionColumnOrder,
              s.mainOverviewSectionSheetColumns,
              s.customBoards
            );
            let savedSheetColumnTemplates = s.savedSheetColumnTemplates;
            if (saveToLibrary) {
              const templateId = `saved-col-${uuid()}`;
              savedSheetColumnTemplates = [
                ...savedSheetColumnTemplates,
                savedTemplateFromColumn(column, templateId, new Date().toISOString()),
              ];
            }
            return {
              ...applied,
              savedSheetColumnTemplates,
            };
          });
          return columnId;
        },

        saveSheetColumnTemplate: (label, type, options, headerAlignment, cellAlignment) => {
          const trimmed = label.trim();
          if (!trimmed) return '';
          const templateId = `saved-col-${uuid()}`;
          const column = buildSheetColumnDefinition(
            templateId,
            trimmed,
            type,
            options,
            headerAlignment,
            cellAlignment
          );
          set((s) => ({
            savedSheetColumnTemplates: [
              ...s.savedSheetColumnTemplates,
              savedTemplateFromColumn(column, templateId, new Date().toISOString()),
            ],
          }));
          return templateId;
        },

        removeSavedSheetColumnTemplate: (id) => {
          set((s) => ({
            savedSheetColumnTemplates: s.savedSheetColumnTemplates.filter(
              (template) => template.id !== id
            ),
          }));
        },

        addColumnSettingsDropdown: (columnId) => {
          const trimmed = columnId.trim();
          if (!trimmed) return;
          set((s) => ({
            columnSettingsDropdownIds: normalizeColumnSettingsDropdownIds([
              ...s.columnSettingsDropdownIds,
              trimmed,
            ]),
          }));
        },

        removeColumnSettingsDropdown: (columnId) => {
          set((s) => ({
            columnSettingsDropdownIds: normalizeColumnSettingsDropdownIds(
              s.columnSettingsDropdownIds.filter((id) => id !== columnId)
            ),
          }));
        },

        removeMainOverviewSectionColumn: (sectionBoardType, id) => {
          set((s) => {
            const actorId = resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId);
            if (!canManageColumns(actorId, s.employees, s.employeePermissions)) return s;

            const extras = s.mainOverviewSectionSheetColumns[sectionBoardType] ?? [];
            const sectionColumn = extras.find((column) => column.id === id);
            const boardColumn = getBoardSheetColumns(sectionBoardType, s.boardSheetColumns).find(
              (column) => column.id === id
            );
            const column = sectionColumn ?? boardColumn;
            if (!column) return s;

            const columnOrderBefore = getBoardSheetColumnOrder(
              sectionBoardType,
              s.mainOverviewSectionColumnOrder,
              {
                ...s.boardSheetColumns,
                ...s.mainOverviewSectionSheetColumns,
              },
              false
            );
            const activityLogId = uuid();
            const archiveId = uuid();
            const archive = buildColumnDeleteArchive({
              archiveId,
              activityLogId,
              actorId,
              boardType: 'main',
              sectionBoardType,
              column,
              columnOrderBefore,
              wasMainOverviewShared: false,
              tasks: s.tasks,
              taskGroups: s.taskGroups,
            });
            const stripped = stripColumnFromState('main', id, s, sectionBoardType);
            const history = pushHistory(s);
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId,
                action: 'deleted',
                entityType: 'column',
                entityId: id,
                summary: columnActivitySummary(
                  'deleted',
                  column,
                  'main',
                  sectionBoardType,
                  s.customBoards
                ),
                archiveId,
                details: { column: column.label, section: sectionBoardType },
              },
              uuid
            );

            return {
              ...history,
              ...stripped,
              activityLog,
              deletedColumnArchive: [archive, ...s.deletedColumnArchive],
            };
          });
        },

        restoreDeletedColumn: (archiveId) => {
          let restored = false;
          set((s) => {
            const actorId = resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId);
            if (!canManageColumns(actorId, s.employees, s.employeePermissions)) return s;

            const archive = s.deletedColumnArchive.find((entry) => entry.id === archiveId);
            if (!archive || archive.restoredAt) return s;

            const restoredState = applyColumnArchiveRestore(archive, s);
            const history = pushHistory(s);
            const restoredAt = new Date().toISOString();
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId,
                action: 'restored',
                entityType: 'column',
                entityId: archive.column.id,
                summary: columnActivitySummary(
                  'restored',
                  archive.column,
                  archive.boardType,
                  archive.sectionBoardType,
                  s.customBoards
                ),
                archiveId,
              },
              uuid
            );
            restored = true;
            return {
              ...history,
              ...restoredState,
              activityLog,
              deletedColumnArchive: s.deletedColumnArchive.map((entry) =>
                entry.id === archiveId
                  ? { ...entry, restoredAt, restoredById: actorId }
                  : entry
              ),
            };
          });
          return restored;
        },

        updateMainOverviewSectionColumn: (sectionBoardType, id, updates) => {
          set((s) => {
            const extras = s.mainOverviewSectionSheetColumns[sectionBoardType];
            if (!extras?.some((column) => column.id === id)) return s;
            return {
              mainOverviewSectionSheetColumns: {
                ...s.mainOverviewSectionSheetColumns,
                [sectionBoardType]: extras.map((column) => {
                  if (column.id !== id) return column;
                  const next = { ...column, ...updates };
                  if (next.type === 'dropdown') {
                    next.options = (next.options ?? [])
                      .map((option) => option.trim())
                      .filter(Boolean);
                  } else {
                    delete next.options;
                  }
                  return next;
                }),
              },
            };
          });
        },

        duplicateTask: (taskId) => {
          const newIds = get().duplicateTasks([taskId]);
          return newIds[0] ?? null;
        },

        duplicateTasks: (taskIds) => {
          const newTaskIds: string[] = [];
          set((s) => {
            const sources = taskIds
              .map((id) => s.tasks.find((task) => task.id === id))
              .filter((task): task is Task => task != null);
            if (sources.length === 0) return s;

            const history = pushHistory(s);
            let tasks = s.tasks;
            for (const source of sources) {
              const { tasks: nextTasks, newTaskId } = insertTaskCopy(
                tasks,
                taskToClipboard({ ...source, parentTaskId: null }),
                source.clientId!,
                source.projectId!,
                source.groupId,
                source.boardType,
                source.id
              );
              tasks = nextTasks;
              newTaskIds.push(newTaskId);
            }
            return { ...history, tasks };
          });
          return newTaskIds;
        },

        duplicateGroup: (groupId) => {
          const newIds = get().duplicateGroups([groupId]);
          return newIds[0] ?? null;
        },

        duplicateGroups: (groupIds) => {
          const newGroupIds: string[] = [];
          set((s) => {
            const result = duplicateGroupSubtrees(groupIds, s.taskGroups, s.tasks);
            if (result.newRootGroupIds.length === 0) return s;
            const history = pushHistory(s);
            newGroupIds.push(...result.newRootGroupIds);
            return {
              ...history,
              taskGroups: result.taskGroups,
              tasks: result.tasks,
            };
          });
          return newGroupIds;
        },

        copyTask: (taskId) => {
          const source = get().tasks.find((t) => t.id === taskId);
          if (source) set({ taskClipboard: taskToClipboard(source) });
        },

        pasteTask: ({ clientId, projectId, insertAfterTaskId }) => {
          const clip = get().taskClipboard;
          if (!clip) return null;

          let newTaskId: string | null = null;
          set((s) => {
            const history = pushHistory(s);
            const anchor = insertAfterTaskId
              ? s.tasks.find((t) => t.id === insertAfterTaskId)
              : null;

            const groupId = anchor
              ? anchor.groupId
              : resolveGroupForProject(s.taskGroups, projectId, clip.groupId);
            const boardType = anchor?.boardType ?? clip.boardType;

            const { tasks, newTaskId: id } = insertTaskCopy(
              s.tasks,
              clip,
              clientId,
              projectId,
              groupId,
              boardType,
              insertAfterTaskId ?? null
            );
            newTaskId = id;
            return { ...history, tasks };
          });
          return newTaskId;
        },

        undo: () => {
          set((s) => {
            if (s.historyPast.length === 0) return s;
            const previous = s.historyPast[s.historyPast.length - 1];
            const current = cloneSnapshot(s.tasks, s.taskGroups, s.timeEntries);
            return {
              tasks: previous.tasks,
              taskGroups: previous.taskGroups,
              timeEntries: previous.timeEntries ?? s.timeEntries,
              historyPast: s.historyPast.slice(0, -1),
              historyFuture: [current, ...s.historyFuture].slice(0, 50),
            };
          });
        },

        redo: () => {
          set((s) => {
            if (s.historyFuture.length === 0) return s;
            const next = s.historyFuture[0];
            const current = cloneSnapshot(s.tasks, s.taskGroups, s.timeEntries);
            return {
              tasks: next.tasks,
              taskGroups: next.taskGroups,
              timeEntries: next.timeEntries ?? s.timeEntries,
              historyPast: [...s.historyPast, current].slice(-50),
              historyFuture: s.historyFuture.slice(1),
            };
          });
        },

        createTaskInGroup: ({ clientId, projectId, groupId, boardType: boardOverride }) => {
          let newTaskId: string | null = null;
          set((s) => {
            const history = pushHistory(s);
            const branchBoard =
              boardOverride ??
              (groupId ? findSectionBoardType(s.taskGroups, groupId) ?? 'main' : 'main');
            const siblings = s.tasks.filter(
              (t) =>
                t.projectId === projectId &&
                t.groupId === groupId &&
                !t.parentTaskId &&
                t.boardType === branchBoard
            );
            newTaskId = uuid();
            const task: Task = {
              id: newTaskId,
              title: 'New task',
              description: '',
              status: defaultStatusForBoard(
                branchBoard,
                getBoardTaskStatuses(branchBoard, s.boardTaskStatuses)
              ),
              assigneeIds: [],
              clientId,
              projectId,
              boardType: branchBoard,
              groupId,
              parentTaskId: null,
              priority: siblings.reduce((max, t) => Math.max(max, t.priority), -1) + 1,
              dueDate: null,
              customFields: {},
              durationFields: {},
              createdAt: new Date().toISOString(),
            };
            const newTask = applyAutoAssigneesToTask(
              normalizeTaskFields(task),
              s.projects,
              s.taskGroups,
              s.boardTaskStatuses,
              s.projectBoardTaskStatuses,
              { employees: s.employees, employeeJobTitles: s.employeeJobTitles }
            );
            return { ...history, tasks: [...s.tasks, newTask] };
          });
          return newTaskId;
        },

        createSubtask: (parentTaskId) => {
          let newTaskId: string | null = null;
          set((s) => {
            const parent = s.tasks.find((t) => t.id === parentTaskId);
            if (!parent) return s;
            const history = pushHistory(s);
            const siblings = s.tasks.filter((t) => t.parentTaskId === parentTaskId);
            newTaskId = uuid();
            const task: Task = {
              id: newTaskId,
              title: 'New subtask',
              description: '',
              status: parent.status,
              assigneeIds: [],
              clientId: parent.clientId,
              projectId: parent.projectId,
              boardType: parent.boardType,
              groupId: parent.groupId,
              parentTaskId,
              priority: siblings.reduce((max, t) => Math.max(max, t.priority), -1) + 1,
              dueDate: null,
              createdAt: new Date().toISOString(),
            };
            const newTask = applyAutoAssigneesToTask(
              normalizeTaskFields(task),
              s.projects,
              s.taskGroups,
              s.boardTaskStatuses,
              s.projectBoardTaskStatuses,
              { employees: s.employees, employeeJobTitles: s.employeeJobTitles }
            );
            return { ...history, tasks: [...s.tasks, newTask] };
          });
          return newTaskId;
        },

        applySheetTaskUpdates: (updates) => {
          if (updates.length === 0) return;
          set((s) => {
            const history = pushHistory(s);
            const byId = new Map(updates.map((u) => [u.id, u]));
            return {
              ...history,
              tasks: s.tasks.map((task) => {
                const update = byId.get(task.id);
                if (!update) return task;
                return {
                  ...task,
                  groupId: update.groupId,
                  priority: update.priority,
                  ...(update.boardType !== undefined ? { boardType: update.boardType } : {}),
                };
              }),
            };
          });
        },

        applySheetGroupUpdates: (updates) => {
          if (updates.length === 0) return;
          set((s) => {
            const history = pushHistory(s);
            const byId = new Map(updates.map((u) => [u.id, u]));
            const taskGroups = repairGroupTiers(
              s.taskGroups.map((group) => {
                const update = byId.get(group.id);
                if (!update) return group;
                return {
                  ...group,
                  parentId: update.parentId,
                  sortOrder: update.sortOrder,
                  ...(update.tier !== undefined ? { tier: update.tier } : {}),
                };
              })
            );
            return {
              ...history,
              taskGroups,
            };
          });
        },

        moveSheetItemsToBoard: ({
          clientId,
          projectId,
          groupIds,
          taskIds,
          targetBoardType,
        }) => {
          if (groupIds.length === 0 && taskIds.length === 0) return;

          set((s) => {
            const groupUpdates =
              groupIds.length > 0
                ? computeMoveGroupsToBoard(
                    s.taskGroups,
                    projectId,
                    clientId,
                    groupIds,
                    targetBoardType
                  )
                : null;
            if (groupIds.length > 0 && (!groupUpdates || groupUpdates.length === 0)) {
              return s;
            }

            const history = pushHistory(s);
            const groupUpdateById = new Map(
              (groupUpdates ?? []).map((update) => [update.id, update])
            );

            let taskGroups = s.taskGroups;
            if (groupUpdateById.size > 0) {
              taskGroups = repairGroupTiers(
                taskGroups.map((group) => {
                  const update = groupUpdateById.get(group.id);
                  if (!update) return group;
                  return {
                    ...group,
                    parentId: update.parentId,
                    sortOrder: update.sortOrder,
                    ...(update.tier !== undefined ? { tier: update.tier } : {}),
                  };
                })
              );
            }

            const subtreeTaskIds =
              groupIds.length > 0
                ? collectTaskIdsInGroupSubtrees(s.taskGroups, groupIds, s.tasks)
                : [];
            const directTaskIds = new Set(taskIds);
            const affectedTaskIds = new Set([...subtreeTaskIds, ...taskIds]);
            if (affectedTaskIds.size === 0) {
              return { ...history, taskGroups };
            }

            const tasks = s.tasks.map((task) => {
              if (!affectedTaskIds.has(task.id)) return task;
              return {
                ...task,
                boardType: targetBoardType,
                ...(directTaskIds.has(task.id) && !subtreeTaskIds.includes(task.id)
                  ? { groupId: null }
                  : {}),
              };
            });

            return { ...history, taskGroups, tasks };
          });
        },

        setSheetDragActive: (active) => set({ sheetDragActive: active }),

        setSheetDragHoverBoard: (board) => set({ sheetDragHoverBoard: board }),

        applySheetGroupMerge: (merge) => {
          set((s) => {
            const history = pushHistory(s);
            const taskById = new Map(merge.taskUpdates.map((update) => [update.id, update]));
            const siblingById = new Map(
              merge.siblingGroupUpdates.map((update) => [update.id, update])
            );
            return {
              ...history,
              taskGroups: s.taskGroups
                .filter((group) => group.id !== merge.removeGroupId)
                .map((group) => {
                  const siblingUpdate = siblingById.get(group.id);
                  if (!siblingUpdate) return group;
                  return {
                    ...group,
                    parentId: siblingUpdate.parentId,
                    sortOrder: siblingUpdate.sortOrder,
                  };
                }),
              tasks: s.tasks.map((task) => {
                const update = taskById.get(task.id);
                if (!update) return task;
                return {
                  ...task,
                  groupId: update.groupId,
                  priority: update.priority,
                };
              }),
            };
          });
        },



        addGroup: (group) => {
          const id = uuid();
          set((s) => {
            const history = pushHistory(s);
            const siblings = s.taskGroups.filter(
              (g) =>
                g.clientId === group.clientId &&
                g.projectId === group.projectId &&
                g.parentId === group.parentId
            );
            const sortOrder = group.sortOrder ?? siblings.length;
            const created = { ...group, id, sortOrder };
            const activityLog = logActivity(
              s.activityLog,
              {
                actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                action: 'created',
                entityType: 'group',
                entityId: id,
                summary: `Created group "${created.name}"`,
                details: { board: created.boardType },
              },
              uuid
            );
            return {
              ...history,
              taskGroups: [...s.taskGroups, created],
              activityLog,
            };
          });
          return id;
        },

        updateGroup: (id, updates) => {
          set((s) => {
            const history = pushHistory(s);
            return {
              ...history,
              taskGroups: s.taskGroups.map((g) => (g.id === id ? { ...g, ...updates } : g)),
            };
          });
        },

        removeGroup: (id) => {
          set((s) => {
            const history = pushHistory(s);
            const removedGroup = s.taskGroups.find((group) => group.id === id);
            const descendantIds = collectDescendantGroupIds(s.taskGroups, id);
            const removeIds = new Set([id, ...descendantIds]);
            const activityLog = removedGroup
              ? logActivity(
                  s.activityLog,
                  {
                    actorId: resolveActivityActorId(s.currentUserId, s.viewAsOriginalUserId),
                    action: 'deleted',
                    entityType: 'group',
                    entityId: id,
                    summary: `Deleted group "${removedGroup.name}"`,
                  },
                  uuid
                )
              : s.activityLog;
            return {
              ...history,
              taskGroups: s.taskGroups.filter((g) => !removeIds.has(g.id)),
              tasks: s.tasks.map((t) =>
                t.groupId && removeIds.has(t.groupId) ? { ...t, groupId: null } : t
              ),
              activityLog,
            };
          });
        },

        removeGroups: (ids) => {
          if (ids.length === 0) return;
          set((s) => {
            const history = pushHistory(s);
            const removeIds = new Set<string>();
            for (const id of ids) {
              removeIds.add(id);
              for (const descendantId of collectDescendantGroupIds(s.taskGroups, id)) {
                removeIds.add(descendantId);
              }
            }
            return {
              ...history,
              taskGroups: s.taskGroups.filter((g) => !removeIds.has(g.id)),
              tasks: s.tasks.map((t) =>
                t.groupId && removeIds.has(t.groupId) ? { ...t, groupId: null } : t
              ),
            };
          });
        },



        addFlatBoardHeader: (clientId, projectId, boardType, name = 'New Header') => {
          const id = uuid();
          set((s) => {
            const history = pushHistory(s);
            const siblings = s.taskGroups.filter(
              (g) =>
                g.clientId === clientId &&
                g.projectId === projectId &&
                g.boardType === boardType &&
                g.parentId === null
            );
            return {
              ...history,
              taskGroups: [
                ...s.taskGroups,
                {
                  id,
                  name,
                  clientId,
                  projectId,
                  boardType,
                  tier: 'parent',
                  parentId: null,
                  sectionBoardType: null,
                  sortOrder: siblings.length,
                },
              ],
            };
          });
          return id;
        },

        upsertTaskAttachment: ({
          taskId,
          fileName,
          mimeType,
          sizeBytes,
          storageId,
          uploadedById,
          mode,
        }) => {
          set((s) => {
            const now = new Date().toISOString();
            const existing = s.taskAttachments.find(
              (a) => a.taskId === taskId && a.fileName.toLowerCase() === fileName.toLowerCase()
            );

            if (!existing || mode === 'new') {
              const versionId = uuid();
              const attachmentId = uuid();
              const attachment: TaskAttachment = {
                id: attachmentId,
                taskId,
                fileName,
                currentVersionId: versionId,
                versions: [
                  {
                    id: versionId,
                    version: 1,
                    fileName,
                    mimeType,
                    sizeBytes,
                    storageId,
                    uploadedAt: now,
                    uploadedById,
                  },
                ],
              };
              return { taskAttachments: [...s.taskAttachments, attachment] };
            }

            const nextVersionNumber =
              mode === 'replace'
                ? existing.versions.find((v) => v.id === existing.currentVersionId)?.version ??
                  existing.versions.length
                : existing.versions.reduce((max, v) => Math.max(max, v.version), 0) + 1;

            const versionId = uuid();
            const version = {
              id: versionId,
              version: nextVersionNumber,
              fileName,
              mimeType,
              sizeBytes,
              storageId,
              uploadedAt: now,
              uploadedById,
            };

            const nextAttachment: TaskAttachment = {
              ...existing,
              fileName,
              currentVersionId: versionId,
              versions:
                mode === 'replace'
                  ? existing.versions.map((v) =>
                      v.id === existing.currentVersionId ? { ...version, version: v.version } : v
                    )
                  : [...existing.versions, version],
            };

            return {
              taskAttachments: s.taskAttachments.map((a) =>
                a.id === existing.id ? nextAttachment : a
              ),
            };
          });
        },

        removeTaskAttachment: (attachmentId) => {
          let storageIds: string[] = [];
          set((s) => {
            const attachment = s.taskAttachments.find((a) => a.id === attachmentId);
            if (!attachment) return s;
            storageIds = attachment.versions.map((v) => v.storageId);
            return {
              taskAttachments: s.taskAttachments.filter((a) => a.id !== attachmentId),
            };
          });
          return storageIds;
        },

        addTaskComment: (taskId, authorId, body) => {
          set((s) => ({
            taskComments: [
              ...s.taskComments,
              {
                id: uuid(),
                taskId,
                authorId,
                body: body.trim(),
                createdAt: new Date().toISOString(),
              },
            ],
          }));
        },

        removeTaskComment: (commentId) => {
          set((s) => ({
            taskComments: s.taskComments.filter((comment) => comment.id !== commentId),
          }));
        },

        markTaskCommentsRead: (taskId) => {
          set((s) => ({
            taskCommentReadAt: {
              ...s.taskCommentReadAt,
              [taskId]: new Date().toISOString(),
            },
          }));
        },

        ensureProjectGroups: (clientId, projectId) => {
          set((s) => {
            let taskGroups = ensureMainSections(s.taskGroups, clientId, projectId);
            taskGroups = ensureCustomBoardSections(
              taskGroups,
              clientId,
              projectId,
              s.customBoards,
              s.subBoardTabOrder
            );
            let tasks = repairTasksOnWrongBoardSection(s.tasks, taskGroups);
            tasks = repairDetailersSpoolingMirror(tasks, taskGroups);
            const project = s.projects.find((p) => p.id === projectId);
            if (!project || project.isTemplate) {
              return { taskGroups, tasks };
            }
            const seedInput = resolveProjectSeedInput(project);
            if (!seedInput) {
              return { taskGroups, tasks };
            }

            const needsRepair = (['detailers', 'deliverables'] as const).some((sectionType) => {
              const section = taskGroups.find(
                (g) =>
                  g.projectId === projectId &&
                  g.tier === 'section' &&
                  g.sectionBoardType === sectionType
              );
              if (!section) return false;
              return isTradeLevelSectionBroken(
                taskGroups,
                projectId,
                section.id,
                seedInput.systems,
                seedInput.levels
              );
            });

            if (!needsRepair) {
              return { taskGroups, tasks };
            }

            const fixed = rebuildTradeLevelSectionHierarchy([project], taskGroups, tasks);
            taskGroups = repairGroupTiers(fixed.taskGroups);
            tasks = repairOrphanedTaskGroups(taskGroups, fixed.tasks);
            tasks = reassignOrphanedBranchTasksToLevels(taskGroups, tasks, s.projects);
            tasks = assignZoneTasksToChildGroups(tasks, taskGroups, s.projects);
            tasks = repairTasksOnWrongBoardSection(tasks, taskGroups);

            return { taskGroups, tasks };
          });
        },

      };

    },

    {

      name: 'bim-task-board-storage',

      version: 132,

      migrate: (persisted, version) => {
        try {
          return runStoreMigration(persisted, version);
        } catch (error) {
          console.error('Store migration failed', error);
          return createRecoveryPersistedState(persisted, seedEmployees);
        }
      },

      onRehydrateStorage: () => (_state, error) => {
        if (error) {
          console.error('Store rehydration failed', error);
        }
        markStorePersistHydrated();
      },

      partialize: (state) => ({

        clients: state.clients,

        projects: state.projects,

        employees: state.employees,

        tasks: state.tasks.map(({ assigneesLocked: _locked, ...task }) => task),

        taskGroups: state.taskGroups,

        customBoards: state.customBoards,

        subBoardTabOrder: state.subBoardTabOrder,

        boardTaskStatuses: state.boardTaskStatuses,

        projectBoardTaskStatuses: state.projectBoardTaskStatuses,

        boardSheetColumns: state.boardSheetColumns,

        boardSheetColumnOrder: state.boardSheetColumnOrder,

        mainOverviewSectionColumnOrder: state.mainOverviewSectionColumnOrder,

        mainOverviewSectionSheetColumns: state.mainOverviewSectionSheetColumns,

        taskAttachments: state.taskAttachments,

        taskComments: state.taskComments,

        taskCommentReadAt: state.taskCommentReadAt,

        activeMainTab: state.activeMainTab,

        activeClientId: state.activeClientId,

        activeProjectId: state.activeProjectId,

        activeBoardType: state.activeBoardType,

        activeEmployeeBoard: state.activeEmployeeBoard,

        taskBoardVisibleStatuses: state.taskBoardVisibleStatuses,

        clientsView: state.clientsView,

        currentUserId: state.currentUserId,

        orgTeams: state.orgTeams,

        employeePermissions: state.employeePermissions,

        visibilityDashboardJobLevels: state.visibilityDashboardJobLevels,

        jobLevelNavVisibility: state.jobLevelNavVisibility,

        timeEntries: state.timeEntries,

        employeeReportsTo: state.employeeReportsTo,

        orgChartLevelSlots: state.orgChartLevelSlots,

        employeeAssigneeStyles: state.employeeAssigneeStyles,

        employeeCredentials: state.employeeCredentials,

        dashboardAssignments: state.dashboardAssignments,

        employeeJobTitles: state.employeeJobTitles,

        activityLog: state.activityLog,

        deletedColumnArchive: state.deletedColumnArchive,

        deletedEmployeeArchive: state.deletedEmployeeArchive,

        deletedTaskArchive: state.deletedTaskArchive,

        taskRevisionArchive: state.taskRevisionArchive,

        savedSheetColumnTemplates: state.savedSheetColumnTemplates,

        columnSettingsDropdownIds: state.columnSettingsDropdownIds,

      }),

      merge: (persisted, current) => {
        try {
          return mergePersistedState(persisted as Partial<AppState>, current);
        } catch (error) {
          console.error('Failed to merge persisted store state', error);
          return {
            ...current,
            ...createRecoveryPersistedState(persisted, current.employees),
          } as AppState;
        }
      },

      storage: createJSONStorage(() => ({
        getItem: (name) => durableStoreStorage.getItem(name),
        setItem: (name, value) => {
          // First cold start: wait for hydrate. After that (incl. HMR), never drop edits.
          if (!storePersistHydrated) {
            pendingPersistWrite = { name, value };
            return undefined as unknown as void;
          }
          return durableStoreStorage.setItem(name, value);
        },
        removeItem: (name) => durableStoreStorage.removeItem(name),
      })),

    }

  )

  );

if (useStore.persist.hasHydrated()) {
  markStorePersistHydrated();
} else {
  useStore.persist.onFinishHydration(() => {
    markStorePersistHydrated();
  });
  // Safety for HMR / slow hydrate — never leave writes permanently blocked.
  setTimeout(() => {
    if (!storePersistHydrated) markStorePersistHydrated();
  }, 3000);
}

export function saveNavigationForReload(): void {
  const state = useStore.getState();
  sessionStorage.setItem(
    RELOAD_NAV_SESSION_KEY,
    JSON.stringify({
      activeMainTab: state.activeMainTab,
      activeClientId: state.activeClientId,
      activeProjectId: state.activeProjectId,
      activeBoardType: state.activeBoardType,
      activeEmployeeBoard: state.activeEmployeeBoard,
      clientsView: state.clientsView,
    })
  );
}

export function sortEmployeeTasks(tasks: Task[]): Task[] {

  return [...tasks].sort((a, b) => {

    if (a.priority !== b.priority) return a.priority - b.priority;

    if (a.dueDate && b.dueDate) return a.dueDate.localeCompare(b.dueDate);

    if (a.dueDate) return -1;

    if (b.dueDate) return 1;

    return a.createdAt.localeCompare(b.createdAt);

  });

}


