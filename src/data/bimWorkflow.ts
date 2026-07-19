import type { ProjectBoardType, Task, TaskGroup } from '../types';
import {
  getBoardTaskStatuses,
  isCompleteStatus,
  type BoardTaskStatusesMap,
  type ProjectBoardTaskStatusesMap,
} from '../utils/taskStatuses';
import { taskBranchBoardType } from '../utils/groupRows';

/** End-to-end BIM lifecycle stages for mechanical contractors */
export type WorkflowStageId =
  | 'project-setup'
  | 'detailing'
  | 'coordination'
  | 'support'
  | 'spooling'
  | 'fab'
  | 'shipping'
  | 'field';

export interface WorkflowStageDefinition {
  id: WorkflowStageId;
  label: string;
  description: string;
  boardTypes: ProjectBoardType[];
}

export const BIM_WORKFLOW_STAGES: WorkflowStageDefinition[] = [
  {
    id: 'project-setup',
    label: 'Project Setup',
    description: 'Contract review, kickoff, BEP, and model environment setup',
    boardTypes: ['project-managers'],
  },
  {
    id: 'detailing',
    label: 'Detailing',
    description: 'Modeling and coordinating before spooling handoff',
    boardTypes: ['detailers'],
  },
  {
    id: 'coordination',
    label: 'Coordination',
    description: 'Clash detection, RFIs, backgrounds, and trade coordination',
    boardTypes: ['project-managers', 'rfi', 'documents'],
  },
  {
    id: 'support',
    label: 'Support Specialists',
    description: 'Pre-planning, dimensions, notes, and deliverable prep',
    boardTypes: ['deliverables'],
  },
  {
    id: 'spooling',
    label: 'Spooling',
    description: 'Spool sheet creation, QA, and fab-ready approval',
    boardTypes: ['spooling'],
  },
  {
    id: 'fab',
    label: 'Fab Shop',
    description: 'Shop fabrication, QA, and ready-to-ship',
    boardTypes: ['fab'],
  },
  {
    id: 'shipping',
    label: 'Shipping',
    description: 'Staging, loading, transit, and site delivery',
    boardTypes: ['shipping'],
  },
  {
    id: 'field',
    label: 'Field Installation',
    description: 'Mobilization, install, punch, and as-built updates',
    boardTypes: ['field'],
  },
];

export interface WorkflowStageProgress {
  stage: WorkflowStageDefinition;
  completed: number;
  total: number;
  percent: number;
}

export function workflowStageForBoard(boardType: ProjectBoardType): WorkflowStageId | null {
  for (const stage of BIM_WORKFLOW_STAGES) {
    if (stage.boardTypes.includes(boardType)) return stage.id;
  }
  return null;
}

export function computeWorkflowStageProgress(
  projectId: string,
  tasks: Task[],
  groups: TaskGroup[],
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): WorkflowStageProgress[] {
  const projectTasks = tasks.filter((task) => task.projectId === projectId);

  return BIM_WORKFLOW_STAGES.map((stage) => {
    const scoped = projectTasks.filter((task) => {
      const branch = taskBranchBoardType(task, groups);
      return stage.boardTypes.includes(branch);
    });
    const total = scoped.length;
    const completed = scoped.filter((task) => {
      const branch = taskBranchBoardType(task, groups);
      const statuses = getBoardTaskStatuses(
        branch,
        boardTaskStatuses,
        projectId,
        projectBoardTaskStatuses
      );
      return isCompleteStatus(task.status, statuses);
    }).length;
    return {
      stage,
      completed,
      total,
      percent: total === 0 ? 0 : Math.round((completed / total) * 100),
    };
  });
}

export function overallWorkflowPercent(stages: WorkflowStageProgress[]): number {
  const withTasks = stages.filter((entry) => entry.total > 0);
  if (withTasks.length === 0) return 0;
  const sum = withTasks.reduce((acc, entry) => acc + entry.percent, 0);
  return Math.round(sum / withTasks.length);
}

/** Deliverables statuses that belong to the Support Specialists phase */
export const SUPPORT_PHASE_DELIVERABLE_STATUSES = new Set([
  'not-started',
  'ready-for-pre-planning',
  'pre-planning-complete',
  'support-in-progress',
  'on-hold',
]);

/** Deliverables statuses that belong to the Spooling phase */
export const SPOOLING_PHASE_DELIVERABLE_STATUSES = new Set([
  'ready-for-spooling',
  'spool-in-progress',
  'spool-qa-review',
  'spool-approved',
  'ready-for-fab',
  'detailer-review',
  'fix-mark-ups',
]);

export function deliverablesPhaseLabel(statusId: string): 'Support' | 'Spooling' | 'Complete' | 'Other' {
  if (statusId === 'complete') return 'Complete';
  if (SUPPORT_PHASE_DELIVERABLE_STATUSES.has(statusId)) return 'Support';
  if (SPOOLING_PHASE_DELIVERABLE_STATUSES.has(statusId)) return 'Spooling';
  return 'Other';
}
