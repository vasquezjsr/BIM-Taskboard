import type { ProjectBoardType, TaskStatus, TaskStatusDefinition } from '../types';
import { normalizeTaskStatuses } from './taskStatuses';

/** Default status when a task is assigned to a sub-board on Main Overview */
export function defaultStatusForBoard(
  boardType: ProjectBoardType | 'employee' | 'main',
  statuses?: TaskStatusDefinition[]
): TaskStatus {
  const list = normalizeTaskStatuses(statuses);
  const pick = (id: string) => (list.some((s) => s.id === id) ? id : list[0]!.id);
  if (boardType === 'detailers') return pick('not-started');
  if (boardType === 'deliverables') return pick('not-started');
  if (boardType === 'spooling') return pick('not-started');
  if (boardType === 'rfi') return pick('waiting-for-response');
  if (boardType === 'project-managers') return pick('not-started');
  if (boardType === 'documents') return pick('not-started');
  if (boardType === 'field' || boardType === 'fab' || boardType === 'shipping') return pick('not-started');
  return pick('not-started');
}

export function applyBoardDefaultStatus(
  boardType: ProjectBoardType | 'employee' | 'main',
  currentStatus?: TaskStatus,
  statuses?: TaskStatusDefinition[]
): TaskStatus | undefined {
  const list = normalizeTaskStatuses(statuses);
  const pick = (id: string) => (list.some((s) => s.id === id) ? id : list[0]!.id);
  if (boardType === 'detailers') return pick('not-started');
  if (boardType === 'deliverables') return pick('not-started');
  if (boardType === 'spooling') return pick('not-started');
  if (boardType === 'rfi') return pick('waiting-for-response');
  if (boardType === 'project-managers') return pick('not-started');
  if (boardType === 'documents') return pick('not-started');
  if (boardType === 'field' || boardType === 'fab' || boardType === 'shipping') return pick('not-started');
  return currentStatus;
}
