import type { ProjectBoardType, SheetColumnDefinition, TaskDurationRange } from '../types';

export const MAX_ACTIVITY_LOG_ENTRIES = 2000;

export type ActivityAction =
  | 'created'
  | 'updated'
  | 'deleted'
  | 'restored'
  | 'status_changed';

export type ActivityEntityType =
  | 'task'
  | 'group'
  | 'column'
  | 'employee'
  | 'status'
  | 'project'
  | 'comment'
  | 'time-entry'
  | 'permission';

export interface ActivityLogEntry {
  id: string;
  timestamp: string;
  actorId: string | null;
  action: ActivityAction;
  entityType: ActivityEntityType;
  entityId: string;
  summary: string;
  details?: Record<string, string | number | boolean | null>;
  archiveId?: string;
  restoredAt?: string;
  restoredById?: string | null;
}

export interface DeletedColumnArchive {
  id: string;
  deletedAt: string;
  deletedById: string | null;
  activityLogId: string;
  boardType: ProjectBoardType;
  /** Main Overview section column — null for board-level columns */
  sectionBoardType: ProjectBoardType | null;
  column: SheetColumnDefinition;
  columnOrderBefore: string[];
  wasMainOverviewShared: boolean;
  taskFieldValues: Record<
    string,
    {
      customFields?: Record<string, string | null>;
      durationFields?: Record<string, TaskDurationRange>;
    }
  >;
  groupFieldValues: Record<string, Record<string, TaskDurationRange>>;
  restoredAt?: string;
  restoredById?: string | null;
}

export function appendActivityLogEntry(
  log: ActivityLogEntry[],
  entry: Omit<ActivityLogEntry, 'id' | 'timestamp'>,
  createId: () => string
): ActivityLogEntry[] {
  const next: ActivityLogEntry = {
    ...entry,
    id: createId(),
    timestamp: new Date().toISOString(),
  };
  return [next, ...log].slice(0, MAX_ACTIVITY_LOG_ENTRIES);
}

export function activityActionLabel(action: ActivityAction): string {
  switch (action) {
    case 'created':
      return 'Created';
    case 'updated':
      return 'Updated';
    case 'deleted':
      return 'Deleted';
    case 'restored':
      return 'Restored';
    case 'status_changed':
      return 'Status changed';
    default:
      return action;
  }
}

export function activityEntityLabel(entityType: ActivityEntityType): string {
  switch (entityType) {
    case 'task':
      return 'Task';
    case 'group':
      return 'Group';
    case 'column':
      return 'Column';
    case 'employee':
      return 'Employee';
    case 'status':
      return 'Status';
    case 'project':
      return 'Project';
    case 'comment':
      return 'Comment';
    case 'time-entry':
      return 'Time entry';
    case 'permission':
      return 'Permission';
    default:
      return entityType;
  }
}

export function formatActivityTimestamp(iso: string): string {
  const date = new Date(iso);
  return date.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}
