import type { Task } from '../types';
import { SSV3_FIELD, isSsv3AssemblyTask, isSsv3ShippingPackageTask } from './boardroomPackageImport';
import { isAssemblyReleasedFromFabStatus } from './taskStatuses';
import { localTodayIsoDate } from './timeEntry';

/** Display-only — assemblies/packages not yet released out of Fab. */
export const PARTIAL_STILL_IN_FAB_STATUS_ID = 'partial-still-in-fab';
export const PARTIAL_STILL_IN_FAB_LABEL = 'Partial · still in Fab';
export const PARTIAL_STILL_IN_FAB_COLOR = '#fdba74';

/** Shipping Dashboard workflow lanes (excluding Not Started). */
export const SHIPPING_WORKFLOW_LANES = [
  'staging',
  'loading',
  'in-transit',
  'delivered',
  'received-field',
  'complete',
] as const;

export type ShippingWorkflowLaneId = (typeof SHIPPING_WORKFLOW_LANES)[number];

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

export function isShippingWorkflowLane(status: string | null | undefined): status is ShippingWorkflowLaneId {
  return Boolean(status && (SHIPPING_WORKFLOW_LANES as readonly string[]).includes(status));
}

/** Assembly shipping lane — independent of fab Complete / Ready for Shipping. */
export function getAssemblyShippingStatus(task: Task): ShippingWorkflowLaneId {
  const raw = task.customFields?.[SSV3_FIELD.shippingStatus];
  return isShippingWorkflowLane(raw) ? raw : 'staging';
}

/**
 * Effective package lane for Shipping views — Fab partial packages count as Staging
 * (same as Shipping Dashboard filters / workflow bar).
 */
export function getPackageShippingStatus(pkg: Task): ShippingWorkflowLaneId {
  if (pkg.boardType !== 'shipping') return 'staging';
  return isShippingWorkflowLane(pkg.status) ? pkg.status : 'staging';
}

/** True when Shipping may set lanes on this assembly (released from Fab or package already moved). */
export function isAssemblyReleasedForShipView(
  assembly: Task,
  packageTask: Task | null | undefined
): boolean {
  if (packageTask?.boardType === 'shipping' || packageTask?.boardType === 'field') {
    return true;
  }
  return isAssemblyReleasedFromFabStatus(assembly.status);
}

/** Status value shown for an assembly on Shipping / Field inbound views. */
export function assemblyShippingDisplayStatus(
  assembly: Task,
  packageTask: Task | null | undefined
): string {
  if (!isAssemblyReleasedForShipView(assembly, packageTask)) {
    return PARTIAL_STILL_IN_FAB_STATUS_ID;
  }
  return getAssemblyShippingStatus(assembly);
}

/** Status value shown for a package root on Shipping / Field inbound views. */
export function packageShippingDisplayStatus(pkg: Task): string {
  if (pkg.boardType === 'fab') return PARTIAL_STILL_IN_FAB_STATUS_ID;
  return getPackageShippingStatus(pkg);
}

/** Value shown in Status on Shipping board / Field inbound spreadsheet rows. */
export function shippingStatusValueForSpreadsheet(
  task: Task,
  packageTask?: Task | null
): string {
  if (task.parentTaskId || isSsv3AssemblyTask(task)) {
    return assemblyShippingDisplayStatus(task, packageTask);
  }
  return packageShippingDisplayStatus(task);
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

/**
 * Persist a Shipping Dashboard–compatible status change from the spreadsheet.
 * Assemblies write `ssv3ShippingStatus`; packages on Fab promote via Ready for Shipping;
 * packages already on Shipping update `status` (+ shipped date stamp).
 */
export function updatesForShippingSpreadsheetStatus(
  task: Task,
  nextStatus: string,
  packageTask?: Task | null
): Partial<Task> | null {
  const isAssembly = Boolean(task.parentTaskId) || isSsv3AssemblyTask(task);
  if (isAssembly) {
    const pkg = packageTask;
    if (pkg?.boardType === 'fab' && !isAssemblyReleasedFromFabStatus(task.status)) {
      return null;
    }
    if (!isShippingWorkflowLane(nextStatus) && nextStatus !== 'not-started') {
      return null;
    }
    return { customFields: withShippingStatusCustomFields(task, nextStatus) };
  }
  if (task.boardType === 'fab') {
    // Mirror dashboard: lane edit on a partial Fab package promotes / keeps Ready for Shipping.
    return { status: 'ready-to-ship' };
  }
  return withPackageStatusShippedStamp(task, nextStatus);
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
