import type { ProjectBoardType, Task } from '../types';
import { MAIN_SECTION_BOARDS } from '../types';
import { WORKFLOW_DUE_DATE_MARKER_COLUMN_ID } from '../data/workflowDueDateColumns';
import {
  FABRICATION_DUE_DATE_COLUMN_ID,
  SHIPPING_DUE_DATE_COLUMN_ID,
  SPOOLING_DUE_DATE_COLUMN_ID,
} from './workflowDueDateCascade';
import { taskHasAssignee } from './taskAssignees';
import {
  getBoardTaskStatuses,
  isCompleteStatus,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from './taskStatuses';

/** Office boards through Spooling (excludes Fab / Shipping / Field). */
export const MY_WORK_BOARD_TYPES: readonly ProjectBoardType[] = MAIN_SECTION_BOARDS;

export function isMyWorkBoardType(boardType: Task['boardType']): boardType is ProjectBoardType {
  return MY_WORK_BOARD_TYPES.includes(boardType as ProjectBoardType);
}

function customFieldDate(task: Task, columnId: string): string | null {
  const raw = task.customFields?.[columnId];
  if (raw == null) return null;
  const text = String(raw).trim().slice(0, 10);
  return text || null;
}

type WorkflowDueStage = 'detailing' | 'spooling' | 'fab' | 'shipping' | 'legacy';

function stageForBoard(board: Task['boardType']): WorkflowDueStage {
  switch (board) {
    case 'spooling':
      return 'spooling';
    case 'fab':
      return 'fab';
    case 'shipping':
    case 'field':
      return 'shipping';
    case 'detailers':
    case 'deliverables':
      return 'detailing';
    default:
      return 'legacy';
  }
}

/**
 * Which workflow due-date column applies for My Work.
 * Board is the base; status can advance to the next stage’s date
 * (e.g. Detailers + Ready for Spooling → Spooling Due Date).
 */
export function workflowDueStageForTask(task: Task): WorkflowDueStage {
  const status = String(task.status ?? '').toLowerCase();
  const board = task.boardType;

  // Status handoff cues — prefer the stage the work is moving toward.
  if (/(ready.?to.?ship|ready.?for.?ship)/.test(status)) return 'shipping';
  if (/(ready.?for.?fab)/.test(status)) return 'fab';
  if (/(ready.?for.?spool)/.test(status)) return 'spooling';

  // Otherwise: the board the task currently sits on.
  return stageForBoard(board);
}

function dueDateForStage(task: Task, stage: WorkflowDueStage): string | null {
  const detailing = customFieldDate(task, WORKFLOW_DUE_DATE_MARKER_COLUMN_ID);
  const spooling = customFieldDate(task, SPOOLING_DUE_DATE_COLUMN_ID);
  const fab = customFieldDate(task, FABRICATION_DUE_DATE_COLUMN_ID);
  const shipping = customFieldDate(task, SHIPPING_DUE_DATE_COLUMN_ID);
  const legacy = task.dueDate ?? null;

  switch (stage) {
    case 'detailing':
      return detailing ?? legacy;
    case 'spooling':
      // Prefer stage date; fall back so Team cards aren't blank when only Detailing is set.
      return spooling ?? detailing ?? legacy;
    case 'fab':
      return fab ?? spooling ?? detailing ?? legacy;
    case 'shipping':
      return shipping ?? fab ?? spooling ?? detailing ?? legacy;
    default:
      return legacy ?? detailing ?? spooling ?? fab ?? shipping;
  }
}

export function workflowDueColumnIdForStage(stage: WorkflowDueStage): string | null {
  switch (stage) {
    case 'detailing':
      return WORKFLOW_DUE_DATE_MARKER_COLUMN_ID;
    case 'spooling':
      return SPOOLING_DUE_DATE_COLUMN_ID;
    case 'fab':
      return FABRICATION_DUE_DATE_COLUMN_ID;
    case 'shipping':
      return SHIPPING_DUE_DATE_COLUMN_ID;
    default:
      return null;
  }
}

/** Column to edit for this task’s active workflow due date (null → task.dueDate). */
export function workflowDueColumnIdForTask(task: Task): string | null {
  return workflowDueColumnIdForStage(workflowDueStageForTask(task));
}

function labelForStage(stage: WorkflowDueStage): string {
  switch (stage) {
    case 'detailing':
      return 'Detailing Due Date';
    case 'spooling':
      return 'Spooling Due Date';
    case 'fab':
      return 'Fabrication Due Date';
    case 'shipping':
      return 'Shipping Due Date';
    default:
      return 'Due';
  }
}

/**
 * Due date shown on My Work — from the workflow column for this task’s
 * current board / status stage (not a single shared dueDate).
 */
export function taskMyWorkDueDate(task: Task): string | null {
  return dueDateForStage(task, workflowDueStageForTask(task));
}

/** Header label for the Due column — adapts to board filter / task mix. */
export function myWorkDueColumnLabel(
  boardFilter: 'all' | ProjectBoardType,
  tasks: Task[]
): string {
  if (boardFilter !== 'all') return labelForStage(stageForBoard(boardFilter));

  const stages = [...new Set(tasks.map((task) => workflowDueStageForTask(task)))];
  if (stages.length === 1) return labelForStage(stages[0]!);
  return 'Due';
}

export function getMyOfficeWorkTasks(
  tasks: Task[],
  employeeId: string | null,
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses?: ProjectBoardTaskStatusesMap,
  options?: { includeCompleted?: boolean }
): Task[] {
  if (!employeeId) return [];
  const includeCompleted = options?.includeCompleted ?? false;

  return tasks
    .filter((task) => {
      if (!task.projectId || !isMyWorkBoardType(task.boardType)) return false;
      if (!taskHasAssignee(task, employeeId)) return false;
      if (includeCompleted) return true;
      const statuses = getBoardTaskStatuses(
        task.boardType,
        boardTaskStatuses,
        task.projectId,
        projectBoardTaskStatuses
      );
      return !isCompleteStatus(task.status, statuses);
    })
    .sort((a, b) => {
      const projectCmp = (a.projectId ?? '').localeCompare(b.projectId ?? '');
      if (projectCmp !== 0) return projectCmp;
      const boardCmp = String(a.boardType).localeCompare(String(b.boardType));
      if (boardCmp !== 0) return boardCmp;
      return (a.taskNumber ?? a.title).localeCompare(b.taskNumber ?? b.title, undefined, {
        sensitivity: 'base',
        numeric: true,
      });
    });
}
