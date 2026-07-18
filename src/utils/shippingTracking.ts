import type { Task } from '../types';
import { SSV3_FIELD, isSsv3AssemblyTask, isSsv3ShippingPackageTask } from './boardroomPackageImport';
import { localTodayIsoDate } from './timeEntry';

/** Statuses at/after truck leaves — count as “shipped” for date stamps. */
export const SHIPPED_LANE_STATUSES = [
  'in-transit',
  'delivered',
  'received-field',
  'complete',
] as const;

export function isShippedLaneStatus(status: string): boolean {
  return (SHIPPED_LANE_STATUSES as readonly string[]).includes(status);
}

export function getTaskQr(task: Task): string {
  return (task.customFields?.[SSV3_FIELD.qr] ?? '').trim();
}

/**
 * Human-facing QR label. Sheet payloads often look like `SSV2|P=pkg|A=mark` —
 * strip vendor tags and show package / assembly marks only.
 */
export function formatQrDisplayLabel(qr: string): string {
  const raw = qr.trim();
  if (!raw) return '';
  const parts = raw
    .split('|')
    .map((part) => part.trim())
    .filter(Boolean)
    .filter((part) => !/^ssv\d*$/i.test(part));
  if (parts.length === 0) return raw;

  const assemblyPart = parts.find((part) => /^a=/i.test(part));
  const packagePart = parts.find((part) => /^p=/i.test(part));
  if (assemblyPart) {
    const mark = assemblyPart.slice(2).trim();
    const pkg = packagePart?.slice(2).trim();
    return pkg ? `${pkg} · ${mark}` : mark;
  }
  return parts.join(' · ');
}

export function getEstimatedArrival(task: Task): string {
  return (task.customFields?.[SSV3_FIELD.estimatedArrival] ?? '').trim();
}

export function getShippedAt(task: Task): string {
  return (task.customFields?.[SSV3_FIELD.shippedAt] ?? '').trim();
}

/** Normalize stored date/datetime to YYYY-MM-DD for `<input type="date">`. */
export function toDateInputValue(iso: string | null | undefined): string {
  if (!iso) return '';
  const trimmed = iso.trim();
  if (/^\d{4}-\d{2}-\d{2}$/.test(trimmed)) return trimmed;
  try {
    const d = new Date(trimmed);
    if (Number.isNaN(d.getTime())) return '';
    return localTodayIsoDate(d);
  } catch {
    return '';
  }
}

export function formatShipDateLabel(iso: string | null | undefined): string {
  if (!iso) return '—';
  try {
    // Date-only YYYY-MM-DD
    if (/^\d{4}-\d{2}-\d{2}$/.test(iso)) {
      const [y, m, d] = iso.split('-').map(Number);
      return new Date(y, m - 1, d).toLocaleDateString();
    }
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

export function formatShipDateShort(iso: string | null | undefined): string {
  if (!iso) return '—';
  try {
    if (/^\d{4}-\d{2}-\d{2}$/.test(iso)) {
      const [y, m, d] = iso.split('-').map(Number);
      return new Date(y, m - 1, d).toLocaleDateString();
    }
    return new Date(iso).toLocaleDateString();
  } catch {
    return iso;
  }
}

/** Stamp shippedAt when first entering a shipped lane; keep existing stamp. */
export function withShippingStatusCustomFields(
  task: Task,
  nextStatus: string
): Record<string, string | null> {
  const customFields: Record<string, string | null> = {
    ...(task.customFields ?? {}),
    [SSV3_FIELD.shippingStatus]: nextStatus,
  };
  if (isShippedLaneStatus(nextStatus) && !getShippedAt(task)) {
    customFields[SSV3_FIELD.shippedAt] = localTodayIsoDate();
  }
  return customFields;
}

export function withPackageStatusShippedStamp(
  task: Task,
  nextStatus: string
): Partial<Task> {
  if (!isShippedLaneStatus(nextStatus) || getShippedAt(task)) {
    return { status: nextStatus };
  }
  return {
    status: nextStatus,
    customFields: {
      ...(task.customFields ?? {}),
      [SSV3_FIELD.shippedAt]: localTodayIsoDate(),
    },
  };
}

export function findAssemblyByQr(
  tasks: Task[],
  qrQuery: string
): { packageTask: Task; assembly: Task } | null {
  const needle = qrQuery.trim().toLowerCase();
  if (!needle) return null;

  const assemblies = tasks.filter(isSsv3AssemblyTask);
  const exact = assemblies.find((task) => getTaskQr(task).toLowerCase() === needle);
  const assembly =
    exact ??
    assemblies.find((task) => {
      const qr = getTaskQr(task).toLowerCase();
      return qr.length > 0 && (qr.includes(needle) || needle.includes(qr));
    });
  if (!assembly?.parentTaskId) return null;

  const packageTask = tasks.find(
    (task) =>
      task.id === assembly.parentTaskId &&
      (isSsv3ShippingPackageTask(task) || task.boardType === 'field' || task.boardType === 'fab')
  );
  if (!packageTask) return null;
  return { packageTask, assembly };
}

export function defaultEtaSuggestion(): string {
  // Suggest +3 calendar days for ETA planning default
  const d = new Date();
  d.setDate(d.getDate() + 3);
  return localTodayIsoDate(d);
}
