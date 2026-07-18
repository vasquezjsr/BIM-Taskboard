import type { CustomBoard, Project, ProjectBoardType, Task, TaskDurationRange, TaskGroup } from '../types';
import { getBoardTaskStatuses, isCompleteStatus, type BoardTaskStatusesMap, type ProjectBoardTaskStatusesMap } from './taskStatuses';
import {
  MAIN_SECTION_BOARDS,
  getBoardLabel,
  getProjectSubBoardOrder,
  isCustomBoardId,
  isSubBoardType,
  normalizeSubBoardTabOrder,
} from '../types';

export type SheetRow =
  | { type: 'group'; group: TaskGroup; depth: number; isGhost?: boolean }
  | { type: 'task'; task: Task; depth: number; isGhost?: boolean };

/** Match drag-handle / collapse column width in TaskSpreadsheet */
export const SHEET_ROW_COL_WIDTH = 44;
/** Extra horizontal offset per hierarchy level (section → parent → child → task) */
export const SHEET_INDENT_PX = 24;

/** Content indent after the shared row gutter column — same for groups and tasks */
export function sheetRowPaddingLeft(depth: number): number {
  return depth * SHEET_INDENT_PX;
}

import { isFlatBoard } from './flatBoards';

function taskDepthForGroup(parentDepth: number): number {
  return parentDepth + 1;
}

function subtaskDepthForGroup(parentDepth: number): number {
  return parentDepth + 2;
}

function sectionLabel(boardType: ProjectBoardType, customBoards: CustomBoard[] = []): string {
  return getBoardLabel(boardType, customBoards);
}

export function getFlatBoardHeaders(
  groups: TaskGroup[],
  clientId: string,
  projectId: string,
  boardType: ProjectBoardType
): TaskGroup[] {
  return groups
    .filter(
      (g) =>
        g.clientId === clientId &&
        g.projectId === projectId &&
        g.boardType === boardType &&
        g.tier === 'parent' &&
        g.parentId === null
    )
    .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
}

export function sectionUngroupedGroupId(sectionBoardType: ProjectBoardType): string {
  return `__section-ungrouped-${sectionBoardType}__`;
}

export function isSectionUngroupedGroupId(groupId: string): boolean {
  return groupId.startsWith('__section-ungrouped-');
}

export function isUngroupedBucketGroupId(groupId: string): boolean {
  return isSectionUngroupedGroupId(groupId) || /^__ghost-ungrouped-.+__$/.test(groupId);
}

export function sectionBoardTypeFromUngroupedBucketId(groupId: string): ProjectBoardType | null {
  const sectionMatch = groupId.match(/^__section-ungrouped-(.+)__$/);
  if (sectionMatch) return sectionMatch[1] as ProjectBoardType;
  const ghostMatch = groupId.match(/^__ghost-ungrouped-(.+)__$/);
  if (ghostMatch) return ghostMatch[1] as ProjectBoardType;
  return null;
}

function resolveGroupSectionBoardType(
  group: TaskGroup,
  taskGroups: TaskGroup[]
): ProjectBoardType | null {
  if (group.tier === 'section' && group.sectionBoardType) return group.sectionBoardType;
  let current: TaskGroup | undefined = group;
  while (current) {
    if (current.tier === 'section' && current.sectionBoardType) {
      return current.sectionBoardType;
    }
    current = current.parentId
      ? taskGroups.find((entry) => entry.id === current!.parentId)
      : undefined;
  }
  return null;
}

/** Default: progress on trade/level groups; off for Ungrouped, sections, and Project Management. */
export function shouldShowGroupProgressBar(
  group: TaskGroup,
  taskGroups: TaskGroup[]
): boolean {
  if (isUngroupedBucketGroupId(group.id)) return false;
  if (group.tier === 'section') return false;
  if (group.id.startsWith('__')) return false;

  if (typeof group.showProgressBar === 'boolean') {
    return group.showProgressBar;
  }

  const sectionBoardType = resolveGroupSectionBoardType(group, taskGroups);
  const effectiveBoard = sectionBoardType ?? group.boardType;
  if (effectiveBoard === 'project-managers') return false;

  return true;
}

export function isSubBoard(boardType: ProjectBoardType): boolean {
  return isSubBoardType(boardType);
}

/** All groups live on the main board — sub-boards mirror this hierarchy. */
export function getMainGroups(
  groups: TaskGroup[],
  clientId: string,
  projectId: string
): TaskGroup[] {
  return groups
    .filter(
      (g) =>
        g.clientId === clientId &&
        g.projectId === projectId &&
        g.boardType === 'main'
    )
    .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
}

/** Keep one section row per board tab; merge duplicate empty sections into the one that has content. */
export function dedupeProjectSections(groups: TaskGroup[], projectId: string): TaskGroup[] {
  let updated = [...groups];

  for (const sectionType of MAIN_SECTION_BOARDS) {
    const sections = updated.filter(
      (g) =>
        g.projectId === projectId &&
        g.tier === 'section' &&
        g.sectionBoardType === sectionType
    );
    if (sections.length <= 1) continue;

    const descendantCount = (sectionId: string) =>
      updated.filter(
        (g) => g.projectId === projectId && isGroupUnderSection(updated, g.id, sectionId)
      ).length;

    const sorted = [...sections].sort(
      (a, b) => descendantCount(b.id) - descendantCount(a.id)
    );
    const canonical = sorted[0]!;
    for (const duplicate of sorted.slice(1)) {
      updated = updated.map((g) =>
        g.projectId === projectId && g.parentId === duplicate.id
          ? { ...g, parentId: canonical.id }
          : g
      );
      updated = updated.filter((g) => g.id !== duplicate.id);
    }
  }

  return updated;
}

export function isGroupUnderSection(
  groups: TaskGroup[],
  groupId: string,
  sectionId: string
): boolean {
  let current = groups.find((g) => g.id === groupId);
  while (current) {
    if (current.id === sectionId) return true;
    current = current.parentId ? groups.find((g) => g.id === current!.parentId) : undefined;
  }
  return false;
}

export function inferTaskBranchBoardType(
  task: Pick<Task, 'groupId'>,
  groups: TaskGroup[]
): ProjectBoardType {
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

export function taskBranchBoardType(task: Task, groups: TaskGroup[]): ProjectBoardType {
  if (
    task.boardType !== 'main' &&
    task.boardType !== 'employee'
  ) {
    return task.boardType as ProjectBoardType;
  }
  const fromGroup = inferTaskBranchBoardType(task, groups);
  return fromGroup === 'main' ? 'main' : fromGroup;
}

export function defaultParentGroupForSection(
  groups: TaskGroup[],
  projectId: string,
  sectionBoardType: ProjectBoardType,
  projectCoordinationGroupName = 'Project Coordination'
): TaskGroup | undefined {
  const section = groups.find(
    (g) =>
      g.projectId === projectId &&
      g.tier === 'section' &&
      g.sectionBoardType === sectionBoardType &&
      g.boardType === 'main'
  );
  if (!section) return undefined;

  if (sectionBoardType === 'project-managers') {
    return groups.find(
      (g) =>
        g.projectId === projectId &&
        g.parentId === section.id &&
        g.tier === 'parent' &&
        g.name === projectCoordinationGroupName
    );
  }

  return groups
    .filter(
      (g) =>
        g.projectId === projectId &&
        g.parentId === section.id &&
        g.tier === 'parent' &&
        g.boardType === 'main'
    )
    .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))[0];
}

/** Place branch-assigned tasks with no group into the section's default parent group. */
export function repairOrphanedTaskGroups(groups: TaskGroup[], tasks: Task[]): Task[] {
  const groupIdsByProject = new Map<string, Set<string>>();
  for (const group of groups) {
    let ids = groupIdsByProject.get(group.projectId);
    if (!ids) {
      ids = new Set();
      groupIdsByProject.set(group.projectId, ids);
    }
    ids.add(group.id);
  }

  return tasks.map((task) => {
    if (!task.groupId || !task.projectId) return task;
    const ids = groupIdsByProject.get(task.projectId);
    if (ids?.has(task.groupId)) return task;
    return { ...task, groupId: null };
  });
}

const TASK_TITLE_LEVEL =
  /^((?:Level\s*\d+\s*m?|Roof|UG|Underground|Penthouse))\s*[—-]\s*/i;

export function parseLevelNameFromTaskTitle(title: string): string | null {
  const match = title.match(TASK_TITLE_LEVEL);
  if (!match) return null;
  const raw = match[1].trim();
  if (/^ug$/i.test(raw)) return 'UG';
  return raw.replace(/\s+/g, ' ');
}

function inferBranchSectionFromTaskTitle(title: string): 'detailers' | 'deliverables' | null {
  if (/spool sheet|shop drawing|\bbom\b|support shop/i.test(title)) return 'deliverables';
  if (
    /base model|hanger|as-built|view template|piping|ductwork|plumbing|support placement/i.test(
      title
    )
  ) {
    return 'detailers';
  }
  return parseLevelNameFromTaskTitle(title) ? 'detailers' : null;
}

/** Re-link branch-board tasks to level groups when groupId was orphaned after hierarchy repair. */
export function reassignOrphanedBranchTasksToLevels(
  groups: TaskGroup[],
  tasks: Task[],
  _projects: Project[]
): Task[] {
  return tasks.map((task) => {
    if (!task.projectId || task.parentTaskId) return task;
    if (
      task.boardType !== 'detailers' &&
      task.boardType !== 'deliverables' &&
      task.boardType !== 'main'
    ) {
      return task;
    }

    const validIds = new Set(
      groups.filter((g) => g.projectId === task.projectId).map((g) => g.id)
    );
    if (task.groupId && validIds.has(task.groupId)) return task;

    const levelName = parseLevelNameFromTaskTitle(task.title);
    if (!levelName) return task;

    const sectionType: ProjectBoardType | null =
      task.boardType === 'main'
        ? inferBranchSectionFromTaskTitle(task.title)
        : (task.boardType as ProjectBoardType);
    if (sectionType !== 'detailers' && sectionType !== 'deliverables') return task;

    const section = groups.find(
      (g) =>
        g.projectId === task.projectId &&
        g.tier === 'section' &&
        g.sectionBoardType === sectionType
    );
    if (!section) return task;

    const trades = groups.filter(
      (g) =>
        g.projectId === task.projectId &&
        g.parentId === section.id &&
        g.tier === 'parent' &&
        looksLikeTradeGroupName(g.name)
    );
    if (trades.length === 0) return task;

    const systemFromTitle = trades.find((g) => task.title.includes(g.name));
    const primaryTrade =
      systemFromTitle ?? trades.find((g) => g.name === 'Mechanical Piping') ?? trades[0]!;
    const levelGroup = groups.find(
      (g) => g.projectId === task.projectId && g.parentId === primaryTrade.id && g.name === levelName
    );
    if (!levelGroup) return task;

    return {
      ...task,
      groupId: levelGroup.id,
      boardType: task.boardType === 'main' ? sectionType : task.boardType,
    };
  });
}

export function taskCountsAsUngroupedInSection(
  task: Task,
  section: TaskGroup,
  groups: TaskGroup[]
): boolean {
  if (task.parentTaskId) return false;
  if (taskBranchBoardType(task, groups) !== section.sectionBoardType) return false;
  if (!task.groupId) return true;
  const group = groups.find((g) => g.id === task.groupId);
  if (!group) return true;
  return !isGroupUnderSection(groups, task.groupId, section.id);
}

/** Top-level task with no valid group — show "(Ungrouped)" on the title instead of an Ungrouped row. */
export function taskShowsUngroupedTitleSuffix(task: Task, groups: TaskGroup[]): boolean {
  if (task.parentTaskId) return false;
  if (!task.groupId) return true;
  return !groups.some((g) => g.id === task.groupId);
}

/** Place branch-assigned tasks with no group into the section's default parent group. */
export function assignUngroupedSectionTasks(
  groups: TaskGroup[],
  tasks: Task[],
  projectCoordinationGroupName = 'Project Coordination'
): Task[] {
  return tasks.map((task) => {
    const hasValidGroup =
      task.groupId &&
      groups.some((g) => g.id === task.groupId && g.projectId === task.projectId);

    if (
      hasValidGroup ||
      task.parentTaskId ||
      !task.projectId ||
      task.boardType === 'employee' ||
      task.boardType === 'main' ||
      isCustomBoardId(task.boardType) ||
      !MAIN_SECTION_BOARDS.includes(task.boardType as (typeof MAIN_SECTION_BOARDS)[number])
    ) {
      return task;
    }

    const parent = defaultParentGroupForSection(
      groups,
      task.projectId,
      task.boardType as ProjectBoardType,
      projectCoordinationGroupName
    );
    if (!parent) return task;

    return { ...task, groupId: parent.id };
  });
}

export function enrichTaskUpdatesWithBranchGroup(
  task: Task,
  updates: Partial<Task>,
  taskGroups: TaskGroup[],
  projectCoordinationGroupName = 'Project Coordination'
): Partial<Task> {
  if (updates.groupId !== undefined) return updates;
  if (updates.boardType === undefined || updates.boardType === task.boardType) return updates;

  const nextBoard = updates.boardType;
  const merged = { ...updates };

  if (nextBoard === 'main' || nextBoard === 'employee') {
    merged.groupId = null;
    return merged;
  }

  if (!task.projectId) {
    merged.groupId = null;
    return merged;
  }

  merged.groupId = resolveGroupForBoardPlacement(
    task,
    nextBoard,
    taskGroups,
    projectCoordinationGroupName
  );

  return merged;
}

/** Pick the group row a task belongs in on Main Overview for its board assignment. */
export function resolveGroupForBoardPlacement(
  task: Task,
  boardType: ProjectBoardType,
  taskGroups: TaskGroup[],
  projectCoordinationGroupName = 'Project Coordination'
): string | null {
  if (boardType === 'main' || isCustomBoardId(boardType)) {
    return null;
  }

  if (!task.projectId) return null;

  if (!MAIN_SECTION_BOARDS.includes(boardType as (typeof MAIN_SECTION_BOARDS)[number])) {
    return null;
  }

  if (boardType === 'detailers' || boardType === 'deliverables') {
    const levelName = parseLevelNameFromTaskTitle(task.title);
    if (levelName) {
      const section = taskGroups.find(
        (g) =>
          g.projectId === task.projectId &&
          g.tier === 'section' &&
          g.sectionBoardType === boardType
      );
      if (section) {
        const trades = taskGroups.filter(
          (g) =>
            g.projectId === task.projectId &&
            g.parentId === section.id &&
            g.tier === 'parent' &&
            looksLikeTradeGroupName(g.name)
        );
        const trade =
          trades.find((g) => task.title.includes(g.name)) ??
          trades.find((g) => g.name === 'Mechanical Piping') ??
          trades[0];
        const levelGroup = trade
          ? taskGroups.find(
              (g) =>
                g.projectId === task.projectId && g.parentId === trade.id && g.name === levelName
            )
          : undefined;
        if (levelGroup) return levelGroup.id;
      }
    }
  }

  const parent = defaultParentGroupForSection(
    taskGroups,
    task.projectId,
    boardType,
    projectCoordinationGroupName
  );
  return parent?.id ?? null;
}

/** Clear group placement when boardType and group hierarchy disagree (e.g. after branch change to a custom board). */
export function repairTasksOnWrongBoardSection(tasks: Task[], groups: TaskGroup[]): Task[] {
  return tasks.map((task) => {
    if (!task.groupId || !task.projectId || task.parentTaskId) return task;
    if (task.boardType === 'main' || task.boardType === 'employee') return task;

    let current = groups.find((g) => g.id === task.groupId);
    let sectionBoard: ProjectBoardType | null = null;
    while (current) {
      if (current.tier === 'section' && current.sectionBoardType) {
        sectionBoard = current.sectionBoardType;
        break;
      }
      current = current.parentId ? groups.find((g) => g.id === current!.parentId) : undefined;
    }

    if (!sectionBoard || taskBranchBoardType(task, groups) === sectionBoard) return task;
    return {
      ...task,
      groupId: resolveGroupForBoardPlacement(
        task,
        task.boardType as ProjectBoardType,
        groups
      ),
    };
  });
}

function taskRootForBoard(task: Task, tasks: Task[]): Task {
  let current = task;
  const seen = new Set<string>();
  while (current.parentTaskId && !seen.has(current.id)) {
    seen.add(current.id);
    const parent = tasks.find((t) => t.id === current.parentTaskId);
    if (!parent) break;
    current = parent;
  }
  return current;
}

export function taskBelongsToGhostBoard(
  task: Task,
  boardType: ProjectBoardType,
  groups: TaskGroup[],
  clientId: string,
  projectId: string,
  projectTasks: Task[] = []
): boolean {
  if (task.clientId !== clientId || task.projectId !== projectId) return false;
  const root = taskRootForBoard(task, projectTasks.length > 0 ? projectTasks : [task]);
  return taskBranchBoardType(root, groups) === boardType;
}

export function getSectionForBoard(
  groups: TaskGroup[],
  clientId: string,
  projectId: string,
  boardType: ProjectBoardType
): TaskGroup | undefined {
  return getMainGroups(groups, clientId, projectId).find(
    (g) => g.tier === 'section' && g.sectionBoardType === boardType
  );
}

export function inferTaskProjectBoardType(
  task: Task,
  groups: TaskGroup[]
): Task['boardType'] {
  if (task.groupId) {
    let group = groups.find((g) => g.id === task.groupId);
    while (group) {
      if (group.sectionBoardType) return group.sectionBoardType;
      if (group.boardType !== 'main') {
        return group.boardType;
      }
      group = group.parentId ? groups.find((g) => g.id === group!.parentId) : undefined;
    }
  }

  if (
    task.boardType !== 'employee' &&
    task.boardType !== 'main' &&
    !isCustomBoardId(task.boardType)
  ) {
    return task.boardType;
  }

  return 'main';
}

export function restoreAssignedTaskBoardTypes(tasks: Task[], groups: TaskGroup[]): Task[] {
  return tasks.map((task) => {
    if (task.boardType !== 'employee' || !task.projectId) return task;
    return { ...task, boardType: inferTaskProjectBoardType(task, groups) };
  });
}

export function sortTasksByPriority(tasks: Task[]): Task[] {
  return [...tasks].sort(
    (a, b) => a.priority - b.priority || a.createdAt.localeCompare(b.createdAt)
  );
}

export function buildSheetRows(
  groups: TaskGroup[],
  tasks: Task[],
  clientId: string,
  projectId: string,
  boardType: ProjectBoardType,
  collapsedIds: Set<string>,
  subBoardOrder?: ProjectBoardType[],
  customBoards: CustomBoard[] = []
): SheetRow[] {
  const rows: SheetRow[] = [];
  const isOverview = boardType === 'main';
  const isGhostBoard = isSubBoard(boardType);
  const mainGroups = getMainGroups(groups, clientId, projectId);
  const sectionOrder = subBoardOrder
    ? subBoardOrder
    : getProjectSubBoardOrder(projectId, normalizeSubBoardTabOrder([]), customBoards);

  const projectTasks = tasks.filter(
    (t) => t.clientId === clientId && t.projectId === projectId
  );

  /** Sub-board ghost view: tasks assigned to this board (by boardType or group placement) */
  const ghostTasks = isGhostBoard
    ? projectTasks.filter((t) =>
        taskBelongsToGhostBoard(t, boardType, groups, clientId, projectId, projectTasks)
      )
    : [];

  /** Main board: every project task */
  const boardTasks = isOverview ? projectTasks : ghostTasks;

  const childrenOf = (parentId: string) =>
    mainGroups
      .filter((g) => g.parentId === parentId)
      .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));

  const tasksInGroup = (groupId: string) => {
    return sortTasksByPriority(
      boardTasks.filter((t) => t.groupId === groupId && !t.parentTaskId)
    );
  };

  const pushTaskTree = (groupTasks: Task[], isGhost: boolean, parentDepth: number) => {
    const taskDepth = taskDepthForGroup(parentDepth);
    const subtaskDepth = subtaskDepthForGroup(parentDepth);
    for (const task of groupTasks) {
      rows.push({ type: 'task', task, depth: taskDepth, isGhost });
      const subtasks = sortTasksByPriority(
        boardTasks.filter((t) => t.parentTaskId === task.id)
      );
      for (const sub of subtasks) {
        rows.push({ type: 'task', task: sub, depth: subtaskDepth, isGhost });
      }
    }
  };

  const pushGroup = (group: TaskGroup, depth: number, isGhost = false) => {
    rows.push({ type: 'group', group, depth, isGhost });
    // Board sections on Main Overview always stay expanded so sub-board tabs stay in sync.
    if (collapsedIds.has(group.id) && !(isOverview && group.tier === 'section')) return;

    const childZones = childrenOf(group.id).filter((g) => g.tier === 'child');
    const isZonedLevel =
      group.tier === 'parent' &&
      childZones.length > 0 &&
      childZones.every((g) => !looksLikeLevelGroupName(g.name));

    // All children share one sortOrder list — do not split by tier
    if (isZonedLevel) {
      for (const task of tasksInGroup(group.id)) {
        pushTaskTree([task], isGhost, depth);
      }
    }

    for (const child of childrenOf(group.id)) {
      pushGroup(child, depth + 1, isGhost);
    }

    if (!isZonedLevel) {
      for (const task of tasksInGroup(group.id)) {
        pushTaskTree([task], isGhost, depth);
      }
    }
  };

  if (isGhostBoard) {
    if (isFlatBoard(boardType)) {
      const flatHeaders = getFlatBoardHeaders(groups, clientId, projectId, boardType);
      const flatTasks = sortTasksByPriority(
        ghostTasks.filter((t) => !t.parentTaskId)
      );

      for (const header of flatHeaders) {
        rows.push({ type: 'group', group: header, depth: 0, isGhost: false });
        if (!collapsedIds.has(header.id)) {
          for (const task of sortTasksByPriority(flatTasks.filter((t) => t.groupId === header.id))) {
            pushTaskTree([task], false, 0);
          }
        }
      }

      const headerIds = new Set(flatHeaders.map((h) => h.id));
      const ungroupedFlat = sortTasksByPriority(
        flatTasks.filter((t) => !t.groupId || !headerIds.has(t.groupId))
      );
      for (const task of ungroupedFlat) {
        pushTaskTree([task], false, 0);
      }
      return rows;
    }

    const section = getSectionForBoard(groups, clientId, projectId, boardType);
    if (section) {
      // Mirror the section hierarchy from Main Overview (all child groups, any tier).
      for (const child of childrenOf(section.id)) {
        pushGroup(child, 0, true);
      }
      const ungroupedInSection = sortTasksByPriority(
        ghostTasks.filter((t) => !t.groupId && !t.parentTaskId)
      );
      // No dedicated Ungrouped group row — list ungrouped tasks at the end with a title suffix.
      for (const task of ungroupedInSection) {
        pushTaskTree([task], true, 0);
      }
    } else if (ghostTasks.length > 0) {
      for (const task of sortTasksByPriority(ghostTasks.filter((t) => !t.parentTaskId))) {
        pushTaskTree([task], true, 0);
      }
    }
    return rows;
  }

  // ── Main Overview — sub-board sections in tab order (no General section; not a board tab) ──
  const sections = mainGroups.filter((g) => g.tier === 'section');
  const sectionByBoard = new Map(
    sections.map((section) => [section.sectionBoardType!, section] as const)
  );

  const pushSectionUngroupedTasks = (ungroupedTasks: Task[]) => {
    if (ungroupedTasks.length === 0) return;
    // No Ungrouped group header — tasks render with an "(Ungrouped)" title suffix.
    for (const task of ungroupedTasks) {
      pushTaskTree([task], false, 1);
    }
  };

  const generalTasks = sortTasksByPriority(
    boardTasks.filter(
      (t) =>
        !t.groupId &&
        !t.parentTaskId &&
        taskBranchBoardType(t, groups) === 'main'
    )
  );
  for (const task of generalTasks) {
    pushTaskTree([task], false, 0);
  }

  for (const boardId of sectionOrder) {
    const section = sectionByBoard.get(boardId);
    if (!section) continue;

    const rowIndexBeforeSection = rows.length;

    pushGroup(section, 0, false);

    const includedTaskIds = new Set(
      rows
        .slice(rowIndexBeforeSection)
        .filter((row): row is Extract<SheetRow, { type: 'task' }> => row.type === 'task')
        .map((row) => row.task.id)
    );

    const ungroupedInSection = sortTasksByPriority(
      boardTasks.filter((t) => taskCountsAsUngroupedInSection(t, section, groups))
    );
    // Tasks under a collapsed trade/level still "belong" to this section — do NOT spill them
    // out as flat ungrouped rows when their parent group is collapsed.
    const supplementalBoardTasks = sortTasksByPriority(
      projectTasks.filter((t) => {
        if (t.parentTaskId || includedTaskIds.has(t.id)) return false;
        if (t.groupId && isGroupUnderSection(groups, t.groupId, section.id)) {
          return false;
        }
        return taskBelongsToGhostBoard(
          t,
          section.sectionBoardType!,
          groups,
          clientId,
          projectId,
          projectTasks
        );
      })
    );
    const mergedUngrouped = sortTasksByPriority([
      ...ungroupedInSection,
      ...supplementalBoardTasks.filter(
        (task) => !ungroupedInSection.some((existing) => existing.id === task.id)
      ),
    ]);
    pushSectionUngroupedTasks(mergedUngrouped);
  }

  return rows;
}

export function getSectionBoardTypes(): ProjectBoardType[] {
  return MAIN_SECTION_BOARDS;
}

export function defaultSectionName(
  sectionBoardType: ProjectBoardType,
  customBoards: CustomBoard[] = []
): string {
  return sectionLabel(sectionBoardType, customBoards);
}

export function getGroupTierLabel(tier: TaskGroup['tier']): string {
  if (tier === 'section') return 'Board Section';
  if (tier === 'parent') return 'Level Group';
  return 'Trade Group';
}

export type GroupVisualRole = 'board-section' | 'trade-group' | 'level-group' | 'sub-level-group';

const LEVEL_GROUP_NAME =
  /^(level\s*\d+\s*m?|roof|ug|yard|building|basement|penthouse)\b/i;

export function looksLikeLevelGroupName(name: string): boolean {
  return LEVEL_GROUP_NAME.test(name.trim());
}

export function looksLikeTradeGroupName(name: string): boolean {
  const lower = name.trim().toLowerCase();
  return (
    lower.includes('piping') ||
    lower.includes('mechanical') ||
    lower.includes('plumbing') ||
    lower.includes('sheet metal') ||
    lower.includes('spooling') ||
    lower.includes('electrical') ||
    lower.includes('fire protection') ||
    lower.includes('coordination')
  );
}

function hopsFromSection(group: TaskGroup, taskGroups: TaskGroup[]): number {
  let hops = 0;
  let current: TaskGroup | undefined = group;
  while (current && current.tier !== 'section') {
    const parentId: string | null = current.parentId;
    if (!parentId) return Math.max(hops, 1);
    const parent: TaskGroup | undefined = taskGroups.find((entry) => entry.id === parentId);
    if (!parent) return Math.max(hops, 1);
    hops++;
    current = parent;
  }
  return hops;
}

/** Sheet row color role from hierarchy depth under a board section. */
export function resolveGroupVisualRole(
  group: TaskGroup,
  taskGroups: TaskGroup[]
): GroupVisualRole {
  if (group.tier === 'section') return 'board-section';

  const depth = hopsFromSection(group, taskGroups);
  if (depth <= 1) return 'trade-group';
  if (depth === 2) return 'level-group';
  return 'sub-level-group';
}

export function visualRoleToTier(role: GroupVisualRole): TaskGroup['tier'] {
  if (role === 'board-section') return 'section';
  if (role === 'trade-group') return 'parent';
  if (role === 'level-group') return 'child';
  return 'child';
}

/** Tier from tree depth: section → parent (trade) → child (level/zone). Colors use resolveGroupVisualRole. */
export function structuralGroupTier(
  group: TaskGroup,
  taskGroups: TaskGroup[]
): TaskGroup['tier'] {
  if (group.tier === 'section') return 'section';
  const parent = group.parentId
    ? taskGroups.find((entry) => entry.id === group.parentId)
    : undefined;
  if (!parent || parent.tier === 'section') return 'parent';
  return 'child';
}

/** Normalize stored tiers to structural depth — do not derive tier from display color role. */
export function repairGroupTiers(taskGroups: TaskGroup[]): TaskGroup[] {
  return taskGroups.map((group) => {
    const tier = structuralGroupTier(group, taskGroups);
    return group.tier === tier ? group : { ...group, tier };
  });
}

export function collectDescendantGroupIds(groups: TaskGroup[], rootId: string): string[] {
  const ids: string[] = [];
  const walk = (parentId: string) => {
    for (const g of groups) {
      if (g.parentId === parentId) {
        ids.push(g.id);
        walk(g.id);
      }
    }
  };
  walk(rootId);
  return ids;
}

export interface GroupProgress {
  completed: number;
  total: number;
  percent: number;
}

/** Completion % from tasks + subtasks under a group (and nested child groups). */
export function computeGroupProgress(
  group: TaskGroup,
  groups: TaskGroup[],
  tasks: Task[],
  clientId: string,
  projectId: string,
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses?: ProjectBoardTaskStatusesMap
): GroupProgress {
  if (group.id.startsWith('__')) {
    return { completed: 0, total: 0, percent: 0 };
  }

  const projectTasks = tasks.filter(
    (t) => t.clientId === clientId && t.projectId === projectId
  );

  const scopeGroupIds = new Set([
    group.id,
    ...collectDescendantGroupIds(groups, group.id),
  ]);

  const childrenByParent = new Map<string, Task[]>();
  for (const t of projectTasks) {
    if (!t.parentTaskId) continue;
    const list = childrenByParent.get(t.parentTaskId) ?? [];
    list.push(t);
    childrenByParent.set(t.parentTaskId, list);
  }

  const inScope = new Set<string>();
  const addTree = (task: Task) => {
    if (inScope.has(task.id)) return;
    inScope.add(task.id);
    for (const child of childrenByParent.get(task.id) ?? []) {
      addTree(child);
    }
  };

  for (const t of projectTasks) {
    if (t.parentTaskId) continue;

    let rootInScope = Boolean(t.groupId && scopeGroupIds.has(t.groupId));
    if (
      !rootInScope &&
      group.tier === 'section' &&
      group.sectionBoardType &&
      !t.groupId &&
      inferTaskBranchBoardType(t, groups) === group.sectionBoardType
    ) {
      rootInScope = true;
    }

    if (rootInScope) addTree(t);
  }

  for (const t of projectTasks) {
    if (t.groupId && scopeGroupIds.has(t.groupId) && !inScope.has(t.id)) {
      addTree(t);
    }
  }

  const scoped = projectTasks.filter((t) => inScope.has(t.id));
  const total = scoped.length;
  const completed = scoped.filter((t) => {
    const statuses = getBoardTaskStatuses(
      inferTaskBranchBoardType(t, groups),
      boardTaskStatuses,
      projectId,
      projectBoardTaskStatuses
    );
    return isCompleteStatus(t.status, statuses);
  }).length;
  const percent = total === 0 ? 0 : Math.round((completed / total) * 100);

  return { completed, total, percent };
}

function mergeDurationRange(
  a: TaskDurationRange,
  b: TaskDurationRange
): TaskDurationRange {
  const starts = [a.start, b.start].filter((d): d is string => Boolean(d));
  const ends = [a.end, b.end].filter((d): d is string => Boolean(d));
  return {
    start: starts.length > 0 ? starts.sort()[0]! : null,
    end: ends.length > 0 ? ends.sort().at(-1)! : null,
  };
}

export function resolveLevelGroupForTask(
  groupId: string | null | undefined,
  groups: TaskGroup[]
): TaskGroup | undefined {
  if (!groupId) return undefined;
  let current = groups.find((g) => g.id === groupId);
  while (current) {
    if (resolveGroupVisualRole(current, groups) === 'level-group') return current;
    current = current.parentId ? groups.find((g) => g.id === current!.parentId) : undefined;
  }
  return undefined;
}

export function resolveTradeGroupForTask(
  groupId: string | null | undefined,
  groups: TaskGroup[]
): TaskGroup | undefined {
  if (!groupId) return undefined;
  let current = groups.find((g) => g.id === groupId);
  while (current) {
    if (resolveGroupVisualRole(current, groups) === 'trade-group') return current;
    current = current.parentId ? groups.find((g) => g.id === current!.parentId) : undefined;
  }
  return undefined;
}

/** Move per-task duration ranges onto level groups (min start / max end), then clear tasks. */
export function migrateTaskDurationsToLevelGroups(
  taskGroups: TaskGroup[],
  tasks: Task[]
): { taskGroups: TaskGroup[]; tasks: Task[] } {
  const groupDurationPatches = new Map<string, Record<string, TaskDurationRange>>();

  for (const task of tasks) {
    const durations = task.durationFields;
    if (!durations || Object.keys(durations).length === 0) continue;

    const levelGroup = resolveLevelGroupForTask(task.groupId, taskGroups);
    if (!levelGroup) continue;

    const existing = groupDurationPatches.get(levelGroup.id) ?? {
      ...(levelGroup.durationFields ?? {}),
    };

    for (const [colId, range] of Object.entries(durations)) {
      const prev = existing[colId] ?? { start: null, end: null };
      existing[colId] = mergeDurationRange(prev, range);
    }

    groupDurationPatches.set(levelGroup.id, existing);
  }

  if (groupDurationPatches.size === 0) {
    return { taskGroups, tasks };
  }

  const nextGroups = taskGroups.map((group) => {
    const patch = groupDurationPatches.get(group.id);
    return patch ? { ...group, durationFields: patch } : group;
  });

  const nextTasks = tasks.map((task) => {
    if (!task.durationFields || Object.keys(task.durationFields).length === 0) return task;
    return { ...task, durationFields: {} };
  });

  return { taskGroups: nextGroups, tasks: nextTasks };
}
