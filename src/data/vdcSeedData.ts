import { v4 as uuid } from 'uuid';
import type { Client, Project, ProjectBoardType, Task, TaskGroup, TaskStatus } from '../types';
import { MAIN_SECTION_BOARDS, isCustomBoardId } from '../types';
import { defaultProjectFields, normalizeProject } from '../types';
import { defaultSectionName, isGroupUnderSection, looksLikeLevelGroupName, looksLikeTradeGroupName, collectDescendantGroupIds, parseLevelNameFromTaskTitle } from '../utils/groupRows';
import { defaultStatusForBoard } from '../utils/taskStatus';
import type { ProjectBoardTaskStatusesMap } from '../utils/taskStatuses';
import { applyDefaultProjectTeams, ensureDefaultProjectTeams } from './projectTeams';
import {
  createTemplateProjectMetadata,
  buildEmptyProjectBoards,
  resetTemplateToEmptyBoards,
  templateStatusForBoard,
  isTemplateProject,
} from '../utils/projectTemplate';

export const SEED_KICKOFF_TITLE = 'Project Kickoff Meeting — GC & Design Team';

/** Parent group for project-wide PM deliverables (BEP, clash runs, IFC, handoff, etc.) */
export const PROJECT_COORDINATION_GROUP_NAME = 'Project Coordination';

export const PROJECT_COORDINATION_GROUP_KEY = 'main::project-managers::project-coordination';

const PROJECT_COORDINATION_TASK_TITLES = new Set([
  SEED_KICKOFF_TITLE,
  'BIM Execution Plan (BEP) Review & Sign-Off',
  'ACC / Revit Central Model Setup',
  'Issue LOD 300 Modeling Matrix',
  'Coordination Schedule — Weekly VDC Meetings',
  'Multi-Level Navisworks Clash Detection — Cycle 1',
  'Multi-Level Navisworks Clash Detection — Cycle 2',
  'Final Support BOM Package — All Levels',
  'O&M Model & Asset Data Handoff',
  'Project Closeout — Lessons Learned & Archive',
]);

export function isProjectCoordinationTask(title: string): boolean {
  if (PROJECT_COORDINATION_TASK_TITLES.has(title)) return true;
  return title.startsWith('Issue IFC Coordination Set —');
}

export type SystemType =
  | 'Sanitary'
  | 'Domestic Water'
  | 'Storm'
  | 'Mechanical Piping'
  | 'Duct';

export interface LevelConfig {
  name: string;
  /** e.g. Nebius Level 1 → Building & Yard */
  zones?: string[];
}

export interface ProjectSeedConfig {
  projectName: string;
  clientName: string;
  levels: LevelConfig[];
  systems: SystemType[];
}

export const PROJECT_CONFIGS: ProjectSeedConfig[] = [
  {
    projectName: 'Project Template',
    clientName: 'Client Template',
    levels: [
      { name: 'Underground' },
      { name: 'Level 1' },
      { name: 'Level 2' },
      { name: 'Level 3' },
      { name: 'Roof' },
    ],
    systems: ['Sanitary', 'Domestic Water', 'Storm', 'Mechanical Piping', 'Duct'],
  },
];

export const TEMPLATE_CLIENT_NAME = 'Client Template';
export const TEMPLATE_PROJECT_NAME = 'Project Template';

/** Previous template labels — matched when migrating persisted data. */
const LEGACY_TEMPLATE_CLIENT_NAMES = [
  'Precision HVAC & Plumbing',
  'ABMEP',
  'Allied Mechanical Contractors',
  'Summit Mechanical Services',
];
const LEGACY_TEMPLATE_PROJECT_NAMES = ['Eastgate School Renovation'];

function findTemplateProject(projects: Project[]): Project | undefined {
  return (
    projects.find((project) => project.name === TEMPLATE_PROJECT_NAME) ??
    projects.find((project) => project.isTemplate) ??
    projects.find((project) => LEGACY_TEMPLATE_PROJECT_NAMES.includes(project.name))
  );
}

function findTemplateClient(clients: Client[], templateProject?: Project): Client | undefined {
  return (
    clients.find((client) => client.name === TEMPLATE_CLIENT_NAME) ??
    (templateProject
      ? clients.find((client) => client.id === templateProject.clientId)
      : undefined) ??
    clients.find((client) => LEGACY_TEMPLATE_CLIENT_NAMES.includes(client.name))
  );
}

function ensureTemplateProjectRecord(
  templateClientId: string,
  templateProject: Project | undefined
): {
  templateProject: Project;
  seedGroups: TaskGroup[];
  seedTasks: Task[];
} {
  if (!templateProject) {
    templateProject = normalizeProject({
      id: uuid(),
      name: TEMPLATE_PROJECT_NAME,
      clientId: templateClientId,
      ...defaultProjectFields(),
      ...createTemplateProjectMetadata(),
      buildingLevels: [],
      activeLevels: [],
    });
    return {
      templateProject,
      seedGroups: buildEmptyProjectBoards(templateClientId, templateProject.id),
      seedTasks: [],
    };
  }

  templateProject = normalizeProject({
    ...templateProject,
    name: TEMPLATE_PROJECT_NAME,
    clientId: templateClientId,
    isTemplate: true,
  });

  return { templateProject, seedGroups: [], seedTasks: [] };
}

const BRANCH_BOARD_TYPES = ['detailers', 'deliverables', 'documents', 'rfi'] as const;

/** @deprecated Template is user-built; only used to detect legacy auto-seeded data. */
export function isTemplateProjectUnderseeded(
  templateProjectId: string,
  taskGroups: TaskGroup[],
  tasks: Task[]
): boolean {
  void templateProjectId;
  void taskGroups;
  void tasks;
  return false;
}

/** @deprecated Use resetTemplateToEmptyBoards from projectTemplate instead. */
export function reseedTemplateProject(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  _employeeIds: { detailers: string[]; support: string[] }
): { projects: Project[]; taskGroups: TaskGroup[]; tasks: Task[] } {
  return resetTemplateToEmptyBoards(projects, taskGroups, tasks);
}

/** Drop every client except Client Template; keep all projects under that client. */
export function pruneToClientTemplateOnly(
  clients: Client[],
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  customBoards: import('../types').CustomBoard[],
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  timeEntries: import('../types').TimeEntry[],
  _employeeIds: { detailers: string[]; support: string[] }
): {
  clients: Client[];
  projects: Project[];
  taskGroups: TaskGroup[];
  tasks: Task[];
  customBoards: import('../types').CustomBoard[];
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap;
  timeEntries: import('../types').TimeEntry[];
  activeClientId: string;
  activeProjectId: string;
} {
  const foundTemplateProject = findTemplateProject(projects);
  let templateClient = findTemplateClient(clients, foundTemplateProject);
  if (templateClient) {
    templateClient = { ...templateClient, name: TEMPLATE_CLIENT_NAME };
  } else {
    templateClient = { id: uuid(), name: TEMPLATE_CLIENT_NAME };
  }

  const templateClientId = templateClient.id;
  const { templateProject, seedGroups, seedTasks } = ensureTemplateProjectRecord(
    templateClientId,
    foundTemplateProject
  );
  const templateProjectId = templateProject.id;

  const keptProjects = ensureDefaultProjectTeams(
    Array.from(
      [
        ...projects
          .filter(
            (project) =>
              project.clientId === templateClientId ||
              project.id === templateProjectId ||
              project.isTemplate
          )
          .filter((project) => project.id !== templateProjectId)
          .map((project) =>
            normalizeProject({
              ...project,
              clientId: templateClientId,
              isTemplate: false,
            })
          ),
        { ...templateProject, id: templateProjectId, clientId: templateClientId, isTemplate: true },
      ].reduce<Map<string, Project>>((byId, project) => {
        byId.set(project.id, project);
        return byId;
      }, new Map()).values()
    )
  );

  const keptProjectIds = new Set(keptProjects.map((project) => project.id));

  let nextGroups = taskGroups
    .filter((group) => keptProjectIds.has(group.projectId))
    .map((group) => ({ ...group, clientId: templateClientId }));
  let nextTasks: Task[] = tasks
    .filter((task) => task.projectId && keptProjectIds.has(task.projectId))
    .map((task) => ({ ...task, clientId: templateClientId }));

  if (
    seedGroups.length > 0 &&
    (!nextGroups.some((group) => group.projectId === templateProjectId) ||
      !nextTasks.some((task) => task.projectId === templateProjectId))
  ) {
    nextGroups = [...nextGroups, ...seedGroups];
    nextTasks = [...nextTasks, ...seedTasks];
  }

  return {
    clients: [{ ...templateClient, id: templateClientId, name: TEMPLATE_CLIENT_NAME }],
    projects: keptProjects,
    taskGroups: nextGroups,
    tasks: nextTasks,
    customBoards: customBoards.filter((board) => keptProjectIds.has(board.projectId)),
    projectBoardTaskStatuses: Object.fromEntries(
      Object.entries(projectBoardTaskStatuses).filter(([projectId]) => keptProjectIds.has(projectId))
    ),
    timeEntries: timeEntries.filter(
      (entry) =>
        entry.projectId === null ||
        entry.clientId === null ||
        (entry.projectId && keptProjectIds.has(entry.projectId))
    ),
    activeClientId: templateClientId,
    activeProjectId: templateProjectId,
  };
}

export const SEED_CLIENT_NAMES = [TEMPLATE_CLIENT_NAME] as const;

export { createTemplateProjectMetadata, templateStatusForBoard };

type GroupKey = string;

interface TaskSpec {
  title: string;
  description: string;
  status: TaskStatus;
  boardType: ProjectBoardType;
  groupKey?: GroupKey;
  dueDate: string;
  assigneeRole?: 'detailer' | 'support-specialist';
}

function slug(value: string): string {
  return value.toLowerCase().replace(/\s+/g, '-');
}

const sectionLevelKey = (section: ProjectBoardType, levelName: string) =>
  `main::${section}::${slug(levelName)}`;

const tradeGroupKey = (section: ProjectBoardType, system: string) =>
  `main::${section}::${slug(system)}`;

const levelUnderTradeKey = (
  section: ProjectBoardType,
  system: string,
  levelName: string
) => `main::${section}::${slug(system)}::${slug(levelName)}`;

const zoneUnderLevelKey = (
  section: ProjectBoardType,
  system: string,
  levelName: string,
  zone: string
) => `main::${section}::${slug(system)}::${slug(levelName)}::${slug(zone)}`;

const branchGroupKey = (
  section: ProjectBoardType,
  levelName: string,
  system: string,
  zone?: string
) =>
  zone
    ? zoneUnderLevelKey(section, system, levelName, zone)
    : levelUnderTradeKey(section, system, levelName);

const DOCUMENTS_BOARD_EXCLUDED_TITLE =
  /support shop drawings|spool sheet|isometric detail|equipment schedule submittal/i;

export function resolveProjectSeedInput(project: Project): ProjectSeedConfig | null {
  const config = getProjectConfig(project.name);
  if (config) return config;
  if (project.activeLevels.length === 0) return null;
  return {
    projectName: project.name,
    clientName: '',
    levels: project.activeLevels.map((name) => ({ name })),
    systems: ['Mechanical Piping', 'Duct'],
  };
}

export function projectHasMisclassifiedDocumentTasks(
  projectId: string,
  tasks: Task[]
): boolean {
  return tasks.some(
    (task) =>
      task.projectId === projectId &&
      task.boardType === 'documents' &&
      DOCUMENTS_BOARD_EXCLUDED_TITLE.test(task.title)
  );
}

function parseZoneFromTitle(
  title: string,
  levelName: string,
  zones: string[]
): string | undefined {
  for (const zone of zones) {
    if (title.startsWith(`${levelName} ${zone} —`)) return zone;
  }
  return undefined;
}

function findZoneGroupId(
  groups: TaskGroup[],
  projectId: string,
  sectionBoardType: ProjectBoardType,
  levelName: string,
  zoneName: string,
  systemName?: string
): string | null {
  const section = groups.find(
    (g) =>
      g.projectId === projectId &&
      g.tier === 'section' &&
      g.sectionBoardType === sectionBoardType
  );
  if (!section) return null;

  const trades = groups.filter(
    (g) =>
      g.projectId === projectId &&
      g.parentId === section.id &&
      g.tier === 'parent'
  );

  for (const trade of trades) {
    if (systemName && trade.name !== systemName) continue;
    const levelGroup = groups.find(
      (g) => g.projectId === projectId && g.parentId === trade.id && g.name === levelName
    );
    if (!levelGroup) continue;
    const zoneGroup = groups.find(
      (g) => g.projectId === projectId && g.parentId === levelGroup.id && g.name === zoneName
    );
    if (zoneGroup) return zoneGroup.id;
  }

  return null;
}

function parseSystemFromTitle(title: string, systems: string[]): string | undefined {
  for (const system of systems) {
    if (title.includes(system)) return system;
  }
  return systems[0];
}

export function projectHasMisplacedZoneTasks(
  project: Project,
  tasks: Task[],
  groups: TaskGroup[]
): boolean {
  const config = getProjectConfig(project.name);
  if (!config?.levels.some((level) => level.zones?.length)) return false;

  for (const task of tasks.filter((t) => t.projectId === project.id)) {
    if (task.boardType !== 'detailers' && task.boardType !== 'deliverables') continue;

    for (const level of config.levels) {
      if (!level.zones) continue;
      const zone = parseZoneFromTitle(task.title, level.name, level.zones);
      if (!zone) continue;

      const zoneGroupId = findZoneGroupId(
        groups,
        project.id,
        task.boardType as ProjectBoardType,
        level.name,
        zone,
        parseSystemFromTitle(task.title, config.systems)
      );
      if (zoneGroupId && task.groupId !== zoneGroupId) return true;
    }
  }

  return false;
}

export function dedupeTasksByProjectTitle(tasks: Task[]): Task[] {
  // Never drop tasks by title — duplicate titles are valid user data.
  return tasks;
}

/** Move Building/Yard (etc.) tasks from the level parent group into the correct zone child group. */
export function assignZoneTasksToChildGroups(
  tasks: Task[],
  groups: TaskGroup[],
  projects: Project[]
): Task[] {
  return tasks.map((task) => {
    const project = projects.find((p) => p.id === task.projectId);
    if (!project) return task;

    const config = getProjectConfig(project.name);
    if (!config) return task;
    if (task.boardType !== 'detailers' && task.boardType !== 'deliverables') return task;

    for (const level of config.levels) {
      if (!level.zones) continue;
      const zone = parseZoneFromTitle(task.title, level.name, level.zones);
      if (!zone) continue;

      const zoneGroupId = findZoneGroupId(
        groups,
        project.id,
        task.boardType as ProjectBoardType,
        level.name,
        zone,
        parseSystemFromTitle(task.title, config.systems)
      );
      if (zoneGroupId && task.groupId !== zoneGroupId) {
        return { ...task, groupId: zoneGroupId };
      }
    }

    return task;
  });
}

export function projectsNeedBranchBoardReconcile(projects: Project[], tasks: Task[]): boolean {
  return projects.some(
    (project) =>
      !project.isTemplate &&
      (resolveProjectSeedInput(project) !== null ||
        projectHasMisclassifiedDocumentTasks(project.id, tasks))
  );
}

export function getProjectConfig(projectName: string): ProjectSeedConfig | undefined {
  return PROJECT_CONFIGS.find((c) => c.projectName === projectName);
}

export function createSeedClients(): Client[] {
  return SEED_CLIENT_NAMES.map((name) => ({ id: uuid(), name }));
}

export function createSeedProjects(clients: Client[]): Project[] {
  const byName = Object.fromEntries(clients.map((c) => [c.name, c.id]));
  const projects = PROJECT_CONFIGS.map((cfg) => {
    const isTemplate = cfg.projectName === TEMPLATE_PROJECT_NAME;
    return normalizeProject({
      id: uuid(),
      name: cfg.projectName,
      clientId: byName[cfg.clientName],
      ...defaultProjectFields(),
      ...(isTemplate ? createTemplateProjectMetadata() : {}),
      isTemplate,
      buildingLevels: [],
      activeLevels: isTemplate ? [] : cfg.levels.map((level) => level.name),
    });
  });
  return applyDefaultProjectTeams(projects);
}

export function ensureProjectTemplate(
  clients: Client[],
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  employeeIds: { detailers: string[]; support: string[] }
): { clients: Client[]; projects: Project[]; taskGroups: TaskGroup[]; tasks: Task[] } {
  const pruned = pruneToClientTemplateOnly(
    clients,
    projects,
    taskGroups,
    tasks,
    [],
    {},
    [],
    employeeIds
  );

  return {
    clients: pruned.clients,
    projects: pruned.projects,
    taskGroups: pruned.taskGroups,
    tasks: pruned.tasks,
  };
}

/** Keep only the template project and remove all other clients/projects. */
export function resetPortfolioToTemplate(
  clients: Client[],
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  customBoards: import('../types').CustomBoard[],
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap,
  timeEntries: import('../types').TimeEntry[],
  employeeIds: { detailers: string[]; support: string[] }
): {
  clients: Client[];
  projects: Project[];
  taskGroups: TaskGroup[];
  tasks: Task[];
  customBoards: import('../types').CustomBoard[];
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap;
  timeEntries: import('../types').TimeEntry[];
  activeClientId: string;
  activeProjectId: string;
} {
  const pruned = pruneToClientTemplateOnly(
    clients,
    projects,
    taskGroups,
    tasks,
    customBoards,
    projectBoardTaskStatuses,
    timeEntries,
    employeeIds
  );

  const templateProject =
    pruned.projects.find((project) => project.name === TEMPLATE_PROJECT_NAME || project.isTemplate) ??
    pruned.projects[0];
  if (!templateProject) {
    return pruned;
  }

  const templateProjectId = templateProject.id;

  return {
    clients: pruned.clients,
    projects: [{ ...templateProject, isTemplate: true, name: TEMPLATE_PROJECT_NAME }],
    taskGroups: pruned.taskGroups.filter((group) => group.projectId === templateProjectId),
    tasks: pruned.tasks.filter((task) => task.projectId === templateProjectId),
    customBoards: pruned.customBoards.filter((board) => board.projectId === templateProjectId),
    projectBoardTaskStatuses: Object.fromEntries(
      Object.entries(pruned.projectBoardTaskStatuses).filter(
        ([projectId]) => projectId === templateProjectId
      )
    ),
    timeEntries: pruned.timeEntries.filter(
      (entry) => entry.projectId === null || entry.projectId === templateProjectId
    ),
    activeClientId: pruned.activeClientId,
    activeProjectId: templateProjectId,
  };
}

export function buildProjectSeed(
  config: ProjectSeedConfig,
  clientId: string,
  projectId: string,
  employeeIds: { detailers: string[]; support: string[] },
  options?: { isTemplate?: boolean }
): { groups: TaskGroup[]; tasks: Task[] } {
  const isTemplate = options?.isTemplate ?? config.projectName === TEMPLATE_PROJECT_NAME;
  const groups: TaskGroup[] = [];
  const groupIdMap = new Map<GroupKey, string>();

  const addGroup = (key: GroupKey, group: Omit<TaskGroup, 'id'>): string => {
    const id = uuid();
    groups.push({ ...group, id });
    groupIdMap.set(key, id);
    return id;
  };

  const sectionIds: Partial<Record<ProjectBoardType, string>> = {};
  MAIN_SECTION_BOARDS.forEach((sectionBoardType, index) => {
    const id = uuid();
    sectionIds[sectionBoardType] = id;
    groups.push({
      id,
      name: defaultSectionName(sectionBoardType),
      clientId,
      projectId,
      boardType: 'main',
      tier: 'section',
      parentId: null,
      sectionBoardType,
      sortOrder: index,
    });
    groupIdMap.set(`main-section::${sectionBoardType}`, id);
  });

  for (const sectionBoardType of MAIN_SECTION_BOARDS) {
    const sectionId = sectionIds[sectionBoardType]!;

    if (sectionBoardType === 'project-managers') {
      addGroup(PROJECT_COORDINATION_GROUP_KEY, {
        name: PROJECT_COORDINATION_GROUP_NAME,
        clientId,
        projectId,
        boardType: 'main',
        tier: 'parent',
        parentId: sectionId,
        sectionBoardType: null,
        sortOrder: 0,
      });
      continue;
    }

    if (sectionBoardType === 'documents' || sectionBoardType === 'rfi') {
      config.levels.forEach((level, li) => {
        addGroup(sectionLevelKey(sectionBoardType, level.name), {
          name: level.name,
          clientId,
          projectId,
          boardType: 'main',
          tier: 'parent',
          parentId: sectionId,
          sectionBoardType: null,
          sortOrder: li,
        });
      });
      continue;
    }

    config.systems.forEach((system, si) => {
      const tradeKey = tradeGroupKey(sectionBoardType, system);
      const tradeId = addGroup(tradeKey, {
        name: system,
        clientId,
        projectId,
        boardType: 'main',
        tier: 'parent',
        parentId: sectionId,
        sectionBoardType: null,
        sortOrder: si,
      });

      config.levels.forEach((level, li) => {
        const levelKey = levelUnderTradeKey(sectionBoardType, system, level.name);
        const levelId = addGroup(levelKey, {
          name: level.name,
          clientId,
          projectId,
          boardType: 'main',
          tier: 'child',
          parentId: tradeId,
          sectionBoardType: null,
          sortOrder: li,
        });

        if (level.zones) {
          level.zones.forEach((zone, zi) => {
            addGroup(`${levelKey}::${slug(zone)}`, {
              name: zone,
              clientId,
              projectId,
              boardType: 'main',
              tier: 'child',
              parentId: levelId,
              sectionBoardType: null,
              sortOrder: zi,
            });
          });
        }
      });
    });
  }

  let detailerIdx = 0;
  let supportIdx = 0;
  const assigneeFor = (role?: 'detailer' | 'support-specialist'): string | null => {
    if (!role) return null;
    if (role === 'detailer') {
      const id = employeeIds.detailers[detailerIdx % employeeIds.detailers.length];
      detailerIdx += 1;
      return id ?? null;
    }
    const id = employeeIds.support[supportIdx % employeeIds.support.length];
    supportIdx += 1;
    return id ?? null;
  };

  const mk = (spec: TaskSpec): Task => ({
    id: uuid(),
    title: spec.title,
    description: spec.description,
    status: isTemplate
      ? templateStatusForBoard(spec.boardType)
      : spec.boardType === 'detailers' ||
          spec.boardType === 'deliverables' ||
          spec.boardType === 'project-managers'
        ? defaultStatusForBoard(spec.boardType)
        : spec.status,
    assigneeIds:
      isTemplate || !spec.assigneeRole
        ? []
        : [assigneeFor(spec.assigneeRole)!].filter(Boolean),
    clientId,
    projectId,
    boardType: spec.boardType,
    groupId: spec.groupKey ? groupIdMap.get(spec.groupKey) ?? null : null,
    parentTaskId: null,
    priority: 0,
    dueDate: isTemplate ? null : spec.dueDate,
    createdAt: new Date().toISOString(),
  });

  const gk = (section: ProjectBoardType, levelName: string, system: string) =>
    levelUnderTradeKey(section, system, levelName);

  const locationLabel = (levelName: string, zone?: string) =>
    zone ? `${levelName} ${zone}` : levelName;

  const documentUploadTasksForLevel = (levelName: string, idx: number): TaskSpec[] => {
    const docDate = `2026-0${4 + Math.floor(idx / 2)}-${String(12 + idx).padStart(2, '0')}`;
    const specDate = `2026-0${4 + Math.floor(idx / 2)}-${String(18 + idx).padStart(2, '0')}`;
    const equipDate = `2026-0${5 + Math.floor(idx / 3)}-${String(8 + idx).padStart(2, '0')}`;

    return [
      {
        title: `${levelName} — Upload Architectural Background`,
        description: `Receive and link the architectural background model for ${levelName}. Upload to the project folder for detailers.`,
        status: 'not-ready',
        boardType: 'documents',
        groupKey: sectionLevelKey('documents', levelName),
        dueDate: docDate,
      },
      {
        title: `${levelName} — Upload Structural Background`,
        description: `Receive and link the structural background model for ${levelName}. Upload to the project folder for detailers.`,
        status: 'not-ready',
        boardType: 'documents',
        groupKey: sectionLevelKey('documents', levelName),
        dueDate: specDate,
      },
      {
        title: `${levelName} — Upload Equipment Cut Sheets`,
        description: `Collect manufacturer cut sheets and submittals needed for modeling at ${levelName}. Upload for detailer reference.`,
        status: 'not-ready',
        boardType: 'documents',
        groupKey: sectionLevelKey('documents', levelName),
        dueDate: equipDate,
      },
    ];
  };

  const detailerDeliverableTasksForLevel = (
    displayLabel: string,
    levelName: string,
    system: SystemType,
    idx: number,
    zone?: string
  ): TaskSpec[] => {
    const baseDate = `2026-0${4 + Math.floor(idx / 2)}-${String(10 + (idx % 2) * 8).padStart(2, '0')}`;
    const hangerDate = `2026-0${5 + Math.floor(idx / 3)}-${String(5 + idx * 2).padStart(2, '0')}`;
    const shopDate = `2026-0${6 + Math.floor(idx / 2)}-${String(1 + idx * 3).padStart(2, '0')}`;
    const bomDate = `2026-0${7 + Math.floor(idx / 3)}-${String(8 + idx).padStart(2, '0')}`;

    const isPlumbing =
      system === 'Sanitary' || system === 'Domestic Water' || system === 'Storm';
    const isDuct = system === 'Duct';

    const modelDesc = isPlumbing
      ? {
          Sanitary: `Model sanitary waste, vent, and branch piping at ${displayLabel}.`,
          'Domestic Water': `Model cold and hot domestic water mains, branches, and fixture connections at ${displayLabel}.`,
          Storm: `Model storm drain leaders, area drains, and roof drainage at ${displayLabel}.`,
        }[system]
      : isDuct
        ? `Model supply, return, and exhaust ductwork with access panels and clearances at ${displayLabel}.`
        : `Model CHW, HHW, condenser water, and specialty piping at ${displayLabel}. Insulation zones and access clearances per spec.`;

    const hangerDesc = isDuct
      ? `Place duct support rods, trapeze hangers, and seismic bracing at ${displayLabel}.`
      : isPlumbing
        ? `Place clevis, trapeze, and riser clamps for ${system.toLowerCase()} piping at ${displayLabel}.`
        : `Place pipe shoes, spring hangers, and guided cantilever supports at ${displayLabel}.`;

    return [
      {
        title: `${displayLabel} — ${system} Base Model (LOD 300)`,
        description: modelDesc,
        status: 'not-started',
        boardType: 'detailers',
        groupKey: branchGroupKey('detailers', levelName, system, zone),
        dueDate: baseDate,
      },
      {
        title: `${displayLabel} — ${system} Hanger & Support Placement`,
        description: hangerDesc,
        status: 'not-started',
        boardType: 'detailers',
        groupKey: branchGroupKey('detailers', levelName, system, zone),
        dueDate: hangerDate,
      },
      {
        title: `${displayLabel} — ${system} Support Shop Drawings`,
        description: `Support steel, unistrut assemblies, and seismic bracing shop drawings for ${system.toLowerCase()} at ${displayLabel}.`,
        status: 'in-progress',
        boardType: 'deliverables',
        groupKey: branchGroupKey('deliverables', levelName, system, zone),
        dueDate: shopDate,
      },
      {
        title: `${displayLabel} — ${system} Support BOM`,
        description: `Bill of materials for supports, rods, anchors, and hardware — ${system.toLowerCase()} at ${displayLabel}.`,
        status: 'not-ready',
        boardType: 'deliverables',
        groupKey: branchGroupKey('deliverables', levelName, system, zone),
        dueDate: bomDate,
        assigneeRole: 'support-specialist',
      },
    ];
  };

  const primarySystem = config.systems[0] ?? 'Mechanical Piping';

  const sharedLevelTasks = (levelName: string, idx: number): TaskSpec[] => [
    {
      title: `${levelName} — Apply View Templates & Filters`,
      description: `Apply project view templates, filters, and workset standards for ${levelName}.`,
      status: 'not-started',
      boardType: 'detailers',
      groupKey: levelUnderTradeKey('detailers', primarySystem, levelName),
      dueDate: `2026-03-${String(14 + idx).padStart(2, '0')}`,
    },
    {
      title: `${levelName} — Spool Sheet Package`,
      description: `Fabrication-ready spool drawings for ${levelName} mains, branches, and risers.`,
      status: 'not-ready',
      boardType: 'deliverables',
      groupKey: levelUnderTradeKey('deliverables', primarySystem, levelName),
      dueDate: `2026-0${8 + Math.floor(idx / 2)}-${String(10 + idx).padStart(2, '0')}`,
    },
    {
      title: `${levelName} — As-Built Model Update`,
      description: `Incorporate RFIs, change orders, and field redlines into the ${levelName} production model.`,
      status: 'not-started',
      boardType: 'detailers',
      groupKey: levelUnderTradeKey('detailers', primarySystem, levelName),
      dueDate: `2026-11-${String(10 + idx).padStart(2, '0')}`,
    },
  ];

  const pmGroupKey = PROJECT_COORDINATION_GROUP_KEY;

  const taskSpecs: TaskSpec[] = [
    {
      title: SEED_KICKOFF_TITLE,
      description: `Introduce VDC scope, milestones, and coordination protocol for ${config.projectName}.`,
      status: 'complete',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-03-03',
    },
    {
      title: 'BIM Execution Plan (BEP) Review & Sign-Off',
      description: `Review LOD matrix, model delivery schedule, and clash detection workflow for ${config.projectName}.`,
      status: 'complete',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-03-07',
    },
    {
      title: 'ACC / Revit Central Model Setup',
      description: 'Create shared ACC project, worksets, and discipline-linked models.',
      status: 'complete',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-03-10',
    },
    {
      title: 'Issue LOD 300 Modeling Matrix',
      description: 'Publish element-by-element LOD expectations for all project levels.',
      status: 'not-ready',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-03-21',
    },
    {
      title: 'Coordination Schedule — Weekly VDC Meetings',
      description: 'Standing Tuesday coordination with trade leads.',
      status: 'ready',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-03-24',
    },
  ];

  config.levels.forEach((level, idx) => {
    taskSpecs.push(...documentUploadTasksForLevel(level.name, idx));
    taskSpecs.push(...sharedLevelTasks(level.name, idx));

    if (level.zones) {
      level.zones.forEach((zone) => {
        const loc = locationLabel(level.name, zone);
        config.systems.forEach((system) => {
          taskSpecs.push(...detailerDeliverableTasksForLevel(loc, level.name, system, idx, zone));
        });
      });
    } else {
      config.systems.forEach((system) => {
        taskSpecs.push(...detailerDeliverableTasksForLevel(level.name, level.name, system, idx));
      });
    }

    if (idx === 0) {
      taskSpecs.push({
        title: `${level.name} — RFI: Ceiling Plenum Conflict`,
        description:
          'Submit RFI for reduced elevation at corridor crossing. Attach clash screenshot and proposed offset.',
        status: 'waiting-for-response',
        boardType: 'rfi',
        groupKey: sectionLevelKey('rfi', level.name),
        dueDate: '2026-05-15',
      });
    }

    const isUG = level.name === 'Underground' || level.name === 'UG';
    if (isUG && config.systems.includes('Sanitary')) {
      taskSpecs.push({
        title: `${level.name} — Sanitary Utility Trench Coordination`,
        description: 'Review sanitary waste mains in utility trench with civil and structural.',
        status: 'complete',
        boardType: 'main',
        groupKey: gk('detailers', level.name, 'Sanitary'),
        dueDate: '2026-04-16',
      });
    }
    if (isUG && config.systems.includes('Storm')) {
      taskSpecs.push({
        title: `${level.name} — Storm Utility Trench Coordination`,
        description: 'Review storm drain mains in utility trench with civil and structural.',
        status: 'complete',
        boardType: 'main',
        groupKey: gk('detailers', level.name, 'Storm'),
        dueDate: '2026-04-18',
      });
    }
    if (level.name === 'Roof') {
      const roofLeaf = level.zones ? level.zones[0] : config.systems[0];
      taskSpecs.push({
        title: `${level.name} — Equipment Curbs & Penetration Coordination`,
        description: 'Coordinate RTU, exhaust, and penetration layouts with roofing trade.',
        status: 'ready',
        boardType: 'main',
        groupKey: gk('detailers', level.name, roofLeaf),
        dueDate: '2026-06-10',
      });
    }
  });

  taskSpecs.push(
    {
      title: 'Multi-Level Navisworks Clash Detection — Cycle 1',
      description: 'Run hard + clearance clashes. Issue report to trades within 48 hrs.',
      status: 'in-progress',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-05-22',
    },
    {
      title: 'Multi-Level Navisworks Clash Detection — Cycle 2',
      description: 'Full project clash run. Target zero critical clashes.',
      status: 'not-ready',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-07-15',
    },
    {
      title: `Issue IFC Coordination Set — ${config.projectName}`,
      description: 'Publish federated coordination model and 2D sheets for GC review.',
      status: 'not-ready',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-08-01',
    },
    {
      title: 'Final Support BOM Package — All Levels',
      description: 'Consolidated BOM submittal for all supports.',
      status: 'not-ready',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-09-30',
    },
    {
      title: 'O&M Model & Asset Data Handoff',
      description: 'Deliver COBie / asset data for valves, equipment, and access panels to owner.',
      status: 'not-ready',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-12-01',
    },
    {
      title: 'Project Closeout — Lessons Learned & Archive',
      description: 'Final coordination meeting, archive models to ACC, and internal VDC debrief.',
      status: 'not-ready',
      boardType: 'project-managers',
      groupKey: pmGroupKey,
      dueDate: '2026-12-15',
    },
    {
      title: `RFI #101 — Structural Beam Conflict — ${config.projectName}`,
      description:
        'Submit RFI for structural beam encroachment at corridor B. Attach Navisworks clash view and proposed reroute.',
      status: 'waiting-for-response',
      boardType: 'rfi',
      dueDate: '2026-05-10',
    },
    {
      title: `RFI #102 — Ceiling Height — Restroom Core — ${config.projectName}`,
      description:
        'Confirm finished ceiling elevation at restroom core. GC to verify architectural reflected ceiling plan.',
      status: 'waiting-for-response',
      boardType: 'rfi',
      dueDate: '2026-05-18',
    }
  );

  return { groups, tasks: taskSpecs.map(mk) };
}

const SAMPLE_RFI_SPECS: Omit<TaskSpec, 'groupKey' | 'assigneeRole'>[] = [
  {
    title: 'RFI #101 — Structural Beam Conflict',
    description:
      'Submit RFI for structural beam encroachment at corridor B. Attach Navisworks clash view and proposed reroute.',
    status: 'waiting-for-response',
    boardType: 'rfi',
    dueDate: '2026-05-10',
  },
  {
    title: 'RFI #102 — Ceiling Height — Restroom Core',
    description:
      'Confirm finished ceiling elevation at restroom core. GC to verify architectural reflected ceiling plan.',
    status: 'waiting-for-response',
    boardType: 'rfi',
    dueDate: '2026-05-18',
  },
];

const SAMPLE_DOCUMENT_UPLOAD_SUFFIXES = [
  {
    suffix: 'Upload Architectural Background',
    description: (level: string) =>
      `Receive and link the architectural background model for ${level}. Upload to the project folder for detailers.`,
    dueDate: '2026-04-12',
  },
  {
    suffix: 'Upload Structural Background',
    description: (level: string) =>
      `Receive and link the structural background model for ${level}. Upload to the project folder for detailers.`,
    dueDate: '2026-04-18',
  },
] as const;

function findLevelGroupId(
  taskGroups: TaskGroup[],
  projectId: string,
  sectionBoardType: ProjectBoardType,
  levelName: string
): string | null {
  const section = taskGroups.find(
    (g) =>
      g.projectId === projectId &&
      g.tier === 'section' &&
      g.sectionBoardType === sectionBoardType
  );
  if (!section) return null;
  return (
    taskGroups.find(
      (g) =>
        g.projectId === projectId &&
        g.parentId === section.id &&
        g.tier === 'parent' &&
        g.name === levelName
    )?.id ?? null
  );
}

/** Add sample RFI and per-level document upload tasks to existing projects that are missing them. */
export function injectSampleRfiAndDocumentTasks(
  tasks: Task[],
  projects: Project[],
  taskGroups: TaskGroup[] = []
): Task[] {
  const existingTitles = new Set(tasks.map((task) => task.title));
  const additions: Task[] = [];

  for (const project of projects) {
    if (project.isTemplate) continue;

    for (const spec of SAMPLE_RFI_SPECS) {
      const title = `${spec.title} — ${project.name}`;
      if (existingTitles.has(title)) continue;

      additions.push({
        id: uuid(),
        title,
        description: spec.description,
        status: spec.status,
        assigneeIds: [],
        clientId: project.clientId,
        projectId: project.id,
        boardType: spec.boardType,
        groupId: null,
        parentTaskId: null,
        priority: 0,
        dueDate: spec.dueDate,
        createdAt: new Date().toISOString(),
      });
      existingTitles.add(title);
    }

    for (const levelName of project.activeLevels) {
      for (const upload of SAMPLE_DOCUMENT_UPLOAD_SUFFIXES) {
        const title = `${levelName} — ${upload.suffix}`;
        if (existingTitles.has(title)) continue;

        additions.push({
          id: uuid(),
          title,
          description: upload.description(levelName),
          status: 'not-ready',
          assigneeIds: [],
          clientId: project.clientId,
          projectId: project.id,
          boardType: 'documents',
          groupId: findLevelGroupId(taskGroups, project.id, 'documents', levelName),
          parentTaskId: null,
          priority: 0,
          dueDate: upload.dueDate,
          createdAt: new Date().toISOString(),
        });
        existingTitles.add(title);
      }
    }
  }

  return additions.length > 0 ? [...tasks, ...additions] : tasks;
}

function isBranchBoardTask(task: Task): boolean {
  return (
    task.boardType !== 'main' &&
    task.boardType !== 'employee' &&
    !isCustomBoardId(task.boardType)
  );
}

function projectHasLevelGroups(projectId: string, groups: TaskGroup[]): boolean {
  const detailersSection = groups.find(
    (g) =>
      g.projectId === projectId &&
      g.tier === 'section' &&
      g.sectionBoardType === 'detailers'
  );
  if (!detailersSection) return false;
  return groups.some(
    (g) => g.projectId === projectId && g.parentId === detailersSection.id && g.tier === 'parent'
  );
}

/** Detect broken Level→Trade or duplicate trade rows under a board section. */
export function isTradeLevelSectionBroken(
  groups: TaskGroup[],
  projectId: string,
  sectionId: string,
  systems: string[],
  levels: { name: string }[]
): boolean {
  const underSection = groups.filter(
    (g) => g.projectId === projectId && g.parentId === sectionId
  );

  if (underSection.some((g) => looksLikeLevelGroupName(g.name))) return true;

  const tradeNameCounts = new Map<string, number>();
  for (const g of underSection) {
    if (looksLikeTradeGroupName(g.name)) {
      tradeNameCounts.set(g.name, (tradeNameCounts.get(g.name) ?? 0) + 1);
    }
  }
  if ([...tradeNameCounts.values()].some((count) => count > 1)) return true;

  for (const system of systems) {
    const trades = underSection.filter((g) => g.name === system);
    if (trades.length !== 1) return true;
    const trade = trades[0]!;
    for (const level of levels) {
      const levelGroup = groups.find(
        (g) => g.projectId === projectId && g.parentId === trade.id && g.name === level.name
      );
      if (!levelGroup || levelGroup.tier !== 'child') return true;
    }
  }

  for (const group of groups) {
    if (group.projectId !== projectId) continue;
    const parent = group.parentId ? groups.find((g) => g.id === group.parentId) : undefined;
    if (
      parent &&
      looksLikeLevelGroupName(parent.name) &&
      looksLikeTradeGroupName(group.name) &&
      isGroupUnderSection(groups, parent.id, sectionId)
    ) {
      return true;
    }
  }

  return false;
}

function parseSystemFromTaskTitle(title: string, systems: string[]): string {
  for (const system of systems) {
    if (title.includes(system)) return system;
  }
  return systems[0] ?? 'Mechanical Piping';
}

function resolveTaskLevelGroupId(
  task: Task,
  sectionType: ProjectBoardType,
  groupKeyToId: Map<string, string>,
  systems: string[],
  levels: { name: string; zones?: string[] }[]
): string | null {
  const levelName = parseLevelNameFromTaskTitle(task.title);
  if (!levelName) return null;

  const system = parseSystemFromTaskTitle(task.title, systems);
  const levelConfig = levels.find((l) => l.name === levelName);

  if (levelConfig?.zones) {
    for (const zone of levelConfig.zones) {
      if (task.title.startsWith(`${levelName} ${zone} —`)) {
        const zoneKey = zoneUnderLevelKey(sectionType, system, levelName, zone);
        const zoneId = groupKeyToId.get(zoneKey);
        if (zoneId) return zoneId;
      }
    }
  }

  return groupKeyToId.get(levelUnderTradeKey(sectionType, system, levelName)) ?? null;
}

/**
 * Wipe and rebuild Trade → Level groups under Detailers/Deliverables, then re-link every task.
 * Idempotent when structure is already correct (caller should check isTradeLevelSectionBroken first).
 */
export function rebuildTradeLevelSectionHierarchy(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  options?: { force?: boolean }
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  let nextGroups = [...taskGroups];
  let nextTasks = [...tasks];

  for (const project of projects) {
    if (project.isTemplate) continue;
    const seedInput = resolveProjectSeedInput(project);
    if (!seedInput) continue;

    for (const sectionType of ['detailers', 'deliverables'] as const) {
      const section = nextGroups.find(
        (g) =>
          g.projectId === project.id &&
          g.tier === 'section' &&
          g.sectionBoardType === sectionType
      );
      if (!section) continue;

      if (
        !options?.force &&
        !isTradeLevelSectionBroken(
          nextGroups,
          project.id,
          section.id,
          seedInput.systems,
          seedInput.levels
        )
      ) {
        continue;
      }

      const removeIds = new Set(collectDescendantGroupIds(nextGroups, section.id));
      nextGroups = nextGroups.filter((g) => !removeIds.has(g.id));

      const groupKeyToId = new Map<string, string>();

      for (const system of seedInput.systems) {
        const tradeId = uuid();
        groupKeyToId.set(tradeGroupKey(sectionType, system), tradeId);
        nextGroups.push({
          id: tradeId,
          name: system,
          clientId: project.clientId,
          projectId: project.id,
          boardType: 'main',
          tier: 'parent',
          parentId: section.id,
          sectionBoardType: null,
          sortOrder: seedInput.systems.indexOf(system),
        });

        for (const level of seedInput.levels) {
          const levelKey = levelUnderTradeKey(sectionType, system, level.name);
          const levelId = uuid();
          groupKeyToId.set(levelKey, levelId);
          nextGroups.push({
            id: levelId,
            name: level.name,
            clientId: project.clientId,
            projectId: project.id,
            boardType: 'main',
            tier: 'child',
            parentId: tradeId,
            sectionBoardType: null,
            sortOrder: seedInput.levels.indexOf(level),
          });

          if (level.zones) {
            for (const zone of level.zones) {
              const zoneKey = zoneUnderLevelKey(sectionType, system, level.name, zone);
              const zoneId = uuid();
              groupKeyToId.set(zoneKey, zoneId);
              nextGroups.push({
                id: zoneId,
                name: zone,
                clientId: project.clientId,
                projectId: project.id,
                boardType: 'main',
                tier: 'child',
                parentId: levelId,
                sectionBoardType: null,
                sortOrder: level.zones.indexOf(zone),
              });
            }
          }
        }
      }

      const primarySystem = seedInput.systems[0] ?? 'Mechanical Piping';

      nextTasks = nextTasks.map((task) => {
        if (task.projectId !== project.id || task.parentTaskId) return task;

        const hadGroupInSection = Boolean(task.groupId && removeIds.has(task.groupId));
        const explicitSection = task.boardType === sectionType;
        const mainBoardOrphan =
          task.boardType === 'main' &&
          hadGroupInSection &&
          parseLevelNameFromTaskTitle(task.title) !== null;

        if (!explicitSection && !hadGroupInSection && !mainBoardOrphan) return task;

        const groupId =
          resolveTaskLevelGroupId(task, sectionType, groupKeyToId, seedInput.systems, seedInput.levels) ??
          groupKeyToId.get(
            levelUnderTradeKey(sectionType, primarySystem, seedInput.levels[0]?.name ?? 'Level 1')
          );

        if (!groupId) return task;
        return {
          ...task,
          groupId,
          boardType: task.boardType === 'main' ? sectionType : task.boardType,
        };
      });
    }
  }

  return { taskGroups: nextGroups, tasks: nextTasks };
}

/** @deprecated Use rebuildTradeLevelSectionHierarchy */
export function migrateTradeBeforeLevelGroupStructure(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  return rebuildTradeLevelSectionHierarchy(projects, taskGroups, tasks);
}

/** Seed level groups and branch tasks when a project has levels but no hierarchy yet. */
export function ensureProjectHierarchy(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  employeeIds: { detailers: string[]; support: string[] }
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  let nextGroups = taskGroups;
  let nextTasks = tasks;

  for (const project of projects) {
    if (project.isTemplate) continue;

    const seedInput = resolveProjectSeedInput(project);
    if (!seedInput) continue;

    const projectTasks = nextTasks.filter((task) => task.projectId === project.id);
    const hasLevels = projectHasLevelGroups(project.id, nextGroups);
    const branchTaskCount = projectTasks.filter(isBranchBoardTask).length;

    if (hasLevels && branchTaskCount > 0) continue;

    const seed = buildProjectSeed(seedInput, project.clientId, project.id, employeeIds);

    if (!hasLevels) {
      nextGroups = [...nextGroups.filter((g) => g.projectId !== project.id), ...seed.groups];
      // Additive only — never wipe existing project tasks when seeding hierarchy.
      nextTasks = [...nextTasks, ...seed.tasks];
      continue;
    }

    if (branchTaskCount === 0) {
      nextTasks = [...nextTasks, ...seed.tasks];
    }
  }

  return { taskGroups: nextGroups, tasks: nextTasks };
}

/** Replace detailer/deliverable/documents/rfi tasks with the current level-grouped seed structure. */
export function migrateBranchBoardTaskStructure(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  employeeIds: { detailers: string[]; support: string[] },
  options?: { onlyIfMisclassified?: boolean }
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  let nextGroups = taskGroups;
  let nextTasks = tasks;

  for (const project of projects) {
    if (project.isTemplate) continue;

    const seedInput = resolveProjectSeedInput(project);
    const misclassified = projectHasMisclassifiedDocumentTasks(project.id, nextTasks);
    if (!seedInput) continue;
    if (options?.onlyIfMisclassified && !misclassified) continue;

    const branchSectionIds = new Set(
      nextGroups
        .filter(
          (g) =>
            g.projectId === project.id &&
            g.tier === 'section' &&
            g.sectionBoardType &&
            (BRANCH_BOARD_TYPES as readonly string[]).includes(g.sectionBoardType)
        )
        .map((g) => g.id)
    );

    const removeGroupIds = new Set<string>();
    for (const group of nextGroups) {
      if (group.projectId !== project.id) continue;
      for (const sectionId of branchSectionIds) {
        if (isGroupUnderSection(nextGroups, group.id, sectionId)) {
          removeGroupIds.add(group.id);
        }
      }
    }

    nextGroups = nextGroups.filter(
      (g) => g.projectId !== project.id || !removeGroupIds.has(g.id)
    );
    nextTasks = nextTasks.filter(
      (t) =>
        t.projectId !== project.id ||
        !(BRANCH_BOARD_TYPES as readonly string[]).includes(t.boardType)
    );

    const seed = buildProjectSeed(
      seedInput,
      project.clientId,
      project.id,
      employeeIds
    );

    nextGroups = [...nextGroups, ...seed.groups.filter((g) => g.tier !== 'section')];
    nextTasks = [...nextTasks, ...seed.tasks.filter(isBranchBoardTask)];
  }

  return { taskGroups: nextGroups, tasks: nextTasks };
}

/** Refresh branch-board tasks when shop drawings or other deliverables are still on Documents. */
export function reconcileBranchBoardTasksIfNeeded(
  projects: Project[],
  taskGroups: TaskGroup[],
  tasks: Task[],
  employeeIds: { detailers: string[]; support: string[] }
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const needsDocumentFix = projects.some((project) =>
    projectHasMisclassifiedDocumentTasks(project.id, tasks)
  );
  const needsZoneFix = projects.some((project) =>
    projectHasMisplacedZoneTasks(project, tasks, taskGroups)
  );

  if (needsDocumentFix) {
    return migrateBranchBoardTaskStructure(projects, taskGroups, tasks, employeeIds, {
      onlyIfMisclassified: true,
    });
  }

  if (needsZoneFix) {
    let nextTasks = assignZoneTasksToChildGroups(tasks, taskGroups, projects);
    nextTasks = dedupeTasksByProjectTitle(nextTasks);
    if (
      projects.some((project) => projectHasMisplacedZoneTasks(project, nextTasks, taskGroups))
    ) {
      return migrateBranchBoardTaskStructure(projects, taskGroups, nextTasks, employeeIds);
    }
    return { taskGroups, tasks: nextTasks };
  }

  return { taskGroups, tasks };
}

export function buildAllSeedData(
  projects: Project[],
  employeeIds: { detailers: string[]; support: string[] }
): { groups: TaskGroup[]; tasks: Task[] } {
  const groups: TaskGroup[] = [];
  const tasks: Task[] = [];

  for (const project of projects) {
    if (isTemplateProject(project)) continue;
    const config = getProjectConfig(project.name);
    if (!config) continue;
    const seed = buildProjectSeed(config, project.clientId, project.id, employeeIds);
    groups.push(...seed.groups);
    tasks.push(...seed.tasks);
  }

  return { groups, tasks };
}
