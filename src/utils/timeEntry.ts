import type { Task, TimeEntry } from '../types';

export const TIME_INCREMENT_MINUTES = 15;
export const TIME_INCREMENT_SECONDS = TIME_INCREMENT_MINUTES * 60;

export function parseTimeToMinutes(time: string): number | null {
  const match = /^(\d{1,2}):(\d{2})$/.exec(time.trim());
  if (!match) return null;
  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  if (!Number.isFinite(hours) || !Number.isFinite(minutes)) return null;
  if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59) return null;
  return hours * 60 + minutes;
}

export function minutesToTimeString(totalMinutes: number): string {
  const clamped = Math.max(0, Math.min(totalMinutes, 23 * 60 + 45));
  const hours = Math.floor(clamped / 60);
  const minutes = clamped % 60;
  return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}`;
}

export function snapTimeToIncrement(
  time: string,
  incrementMinutes = TIME_INCREMENT_MINUTES
): string | null {
  const minutes = parseTimeToMinutes(time);
  if (minutes === null) return null;
  const snapped = Math.round(minutes / incrementMinutes) * incrementMinutes;
  return minutesToTimeString(snapped);
}

export function isTimeOnIncrement(
  time: string,
  incrementMinutes = TIME_INCREMENT_MINUTES
): boolean {
  const minutes = parseTimeToMinutes(time);
  if (minutes === null) return false;
  return minutes % incrementMinutes === 0;
}

export function computeHoursFromTimes(startTime: string, endTime: string): number | null {
  const startMinutes = parseTimeToMinutes(startTime);
  const endMinutes = parseTimeToMinutes(endTime);
  if (startMinutes === null || endMinutes === null) return null;
  if (!isTimeOnIncrement(startTime) || !isTimeOnIncrement(endTime)) return null;
  if (endMinutes <= startMinutes) return null;
  return Math.round(((endMinutes - startMinutes) / 60) * 100) / 100;
}

export function formatTimeLabel(time: string | null): string {
  if (!time) return '';
  const minutes = parseTimeToMinutes(time);
  if (minutes === null) return time;
  const hours24 = Math.floor(minutes / 60);
  const mins = minutes % 60;
  const period = hours24 >= 12 ? 'PM' : 'AM';
  const hours12 = hours24 % 12 || 12;
  return `${hours12}:${String(mins).padStart(2, '0')} ${period}`;
}

export function buildTimeIncrementOptions(): { value: string; label: string }[] {
  const options: { value: string; label: string }[] = [];
  for (let minutes = 0; minutes < 24 * 60; minutes += TIME_INCREMENT_MINUTES) {
    const value = minutesToTimeString(minutes);
    options.push({ value, label: formatTimeLabel(value) });
  }
  return options;
}

export function formatEntryTimeRange(entry: Pick<TimeEntry, 'startTime' | 'endTime' | 'hours'>): string {
  if (entry.startTime && entry.endTime) {
    return `${formatTimeLabel(entry.startTime)} – ${formatTimeLabel(entry.endTime)}`;
  }
  return `${entry.hours.toLocaleString()} hrs`;
}

export function getEntryTaskLabel(
  entry: Pick<TimeEntry, 'taskId' | 'note'>,
  tasks: Task[]
): string {
  if (entry.taskId) {
    const task = tasks.find((item) => item.id === entry.taskId);
    if (task) return task.title;
  }
  if (entry.note.trim()) return entry.note.trim();
  return 'Time entry';
}

export function normalizeTimeEntry(entry: TimeEntry): TimeEntry {
  return {
    ...entry,
    taskId: entry.taskId ?? null,
    startTime: entry.startTime ?? null,
    endTime: entry.endTime ?? null,
  };
}

export function prepareTimeEntryPayload(
  entry: Omit<TimeEntry, 'id' | 'createdAt'>
): Omit<TimeEntry, 'id' | 'createdAt'> | null {
  const taskId = entry.taskId ?? null;
  const startTime = entry.startTime ? snapTimeToIncrement(entry.startTime.trim()) : null;
  const endTime = entry.endTime ? snapTimeToIncrement(entry.endTime.trim()) : null;
  let hours = entry.hours;

  if (startTime && endTime) {
    const computed = computeHoursFromTimes(startTime, endTime);
    if (computed === null || computed <= 0) return null;
    hours = computed;
  }

  if (!Number.isFinite(hours) || hours <= 0) return null;

  return {
    ...entry,
    taskId,
    startTime,
    endTime,
    hours,
    note: entry.note.trim(),
  };
}
