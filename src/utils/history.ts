import type { ProjectBoardType, Task, TaskGroup, TimeEntry } from '../types';

export const MAX_HISTORY = 50;

export interface HistorySnapshot {
  tasks: Task[];
  taskGroups: TaskGroup[];
  timeEntries: TimeEntry[];
}

export function cloneSnapshot(
  tasks: Task[],
  taskGroups: TaskGroup[],
  timeEntries: TimeEntry[]
): HistorySnapshot {
  return {
    tasks: tasks.map((t) => ({ ...t })),
    taskGroups: taskGroups.map((g) => ({ ...g })),
    timeEntries: timeEntries.map((entry) => ({ ...entry })),
  };
}

export function pushHistory(state: {
  tasks: Task[];
  taskGroups: TaskGroup[];
  timeEntries: TimeEntry[];
  historyPast: HistorySnapshot[];
}): { historyPast: HistorySnapshot[]; historyFuture: HistorySnapshot[] } {
  const snap = cloneSnapshot(state.tasks, state.taskGroups, state.timeEntries);
  return {
    historyPast: [...state.historyPast, snap].slice(-MAX_HISTORY),
    historyFuture: [],
  };
}

export function boardTypeForGroup(
  groups: TaskGroup[],
  groupId: string
): ProjectBoardType | 'main' {
  let current = groups.find((g) => g.id === groupId);
  while (current) {
    if (current.tier === 'section' && current.sectionBoardType) {
      return current.sectionBoardType;
    }
    current = current.parentId ? groups.find((g) => g.id === current!.parentId) : undefined;
  }
  return 'main';
}
