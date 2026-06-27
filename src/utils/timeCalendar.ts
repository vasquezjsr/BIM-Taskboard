import type { TimeEntry } from '../types';

export type CalendarView = 'day' | 'week' | 'month';

const WEEKDAY_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

export function todayIsoDate(): string {
  return toIsoDate(new Date());
}

export function parseIsoDate(iso: string): Date {
  const [year, month, day] = iso.split('-').map(Number);
  return new Date(year!, month! - 1, day);
}

export function toIsoDate(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

export function addDays(date: Date, days: number): Date {
  const next = new Date(date);
  next.setDate(next.getDate() + days);
  return next;
}

export function addMonths(date: Date, months: number): Date {
  const next = new Date(date);
  next.setMonth(next.getMonth() + months);
  return next;
}

export function startOfWeek(date: Date): Date {
  const start = new Date(date);
  start.setDate(start.getDate() - start.getDay());
  return start;
}

export function endOfWeek(date: Date): Date {
  return addDays(startOfWeek(date), 6);
}

export function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

export function endOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth() + 1, 0);
}

export function isSameDay(a: Date, b: Date): boolean {
  return (
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate()
  );
}

export function isSameMonth(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth();
}

export function shiftFocusDate(focusDate: string, view: CalendarView, delta: -1 | 1): string {
  const date = parseIsoDate(focusDate);
  if (view === 'day') return toIsoDate(addDays(date, delta));
  if (view === 'week') return toIsoDate(addDays(date, delta * 7));
  return toIsoDate(addMonths(date, delta));
}

export function getWeekDates(focusDate: string): string[] {
  const start = startOfWeek(parseIsoDate(focusDate));
  return Array.from({ length: 7 }, (_, index) => toIsoDate(addDays(start, index)));
}

export interface MonthGridCell {
  iso: string;
  inMonth: boolean;
}

export function getMonthGrid(focusDate: string): MonthGridCell[] {
  const anchor = parseIsoDate(focusDate);
  const monthStart = startOfMonth(anchor);
  const gridStart = startOfWeek(monthStart);
  const cells: MonthGridCell[] = [];

  for (let index = 0; index < 42; index += 1) {
    const date = addDays(gridStart, index);
    cells.push({
      iso: toIsoDate(date),
      inMonth: date.getMonth() === anchor.getMonth(),
    });
  }

  return cells;
}

export function formatCalendarPeriodLabel(focusDate: string, view: CalendarView): string {
  const date = parseIsoDate(focusDate);

  if (view === 'day') {
    return date.toLocaleDateString(undefined, {
      weekday: 'long',
      month: 'long',
      day: 'numeric',
      year: 'numeric',
    });
  }

  if (view === 'week') {
    const start = startOfWeek(date);
    const end = endOfWeek(date);
    const sameYear = start.getFullYear() === end.getFullYear();
    const sameMonth = sameYear && start.getMonth() === end.getMonth();

    if (sameMonth) {
      const month = start.toLocaleDateString(undefined, { month: 'short' });
      return `${month} ${start.getDate()} – ${end.getDate()}, ${start.getFullYear()}`;
    }

    if (sameYear) {
      const startPart = start.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
      const endPart = end.toLocaleDateString(undefined, {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
      });
      return `${startPart} – ${endPart}`;
    }

    const startPart = start.toLocaleDateString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    });
    const endPart = end.toLocaleDateString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    });
    return `${startPart} – ${endPart}`;
  }

  return date.toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
}

export function formatDayHeading(iso: string): string {
  return parseIsoDate(iso).toLocaleDateString(undefined, {
    weekday: 'short',
    month: 'short',
    day: 'numeric',
  });
}

export function formatDayNumber(iso: string): string {
  return String(parseIsoDate(iso).getDate());
}

export function weekdayLabels(): string[] {
  return [...WEEKDAY_LABELS];
}

export function entriesInRange(entries: TimeEntry[], startIso: string, endIso: string): TimeEntry[] {
  return entries.filter((entry) => entry.date >= startIso && entry.date <= endIso);
}

export function groupEntriesByDate(entries: TimeEntry[]): Map<string, TimeEntry[]> {
  const map = new Map<string, TimeEntry[]>();
  for (const entry of entries) {
    const bucket = map.get(entry.date);
    if (bucket) bucket.push(entry);
    else map.set(entry.date, [entry]);
  }
  for (const bucket of map.values()) {
    bucket.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  }
  return map;
}

export function sumHours(entries: TimeEntry[]): number {
  return entries.reduce((total, entry) => total + entry.hours, 0);
}

export function hoursForDate(entriesByDate: Map<string, TimeEntry[]>, iso: string): number {
  return sumHours(entriesByDate.get(iso) ?? []);
}

export function getViewRange(focusDate: string, view: CalendarView): { start: string; end: string } {
  const date = parseIsoDate(focusDate);
  if (view === 'day') {
    const iso = toIsoDate(date);
    return { start: iso, end: iso };
  }
  if (view === 'week') {
    return { start: toIsoDate(startOfWeek(date)), end: toIsoDate(endOfWeek(date)) };
  }
  return { start: toIsoDate(startOfMonth(date)), end: toIsoDate(endOfMonth(date)) };
}
