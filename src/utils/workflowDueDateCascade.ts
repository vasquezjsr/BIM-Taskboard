import { WORKFLOW_DUE_DATE_MARKER_COLUMN_ID } from '../data/workflowDueDateColumns';

export const SPOOLING_DUE_DATE_COLUMN_ID = 'col-spooling-due-date';
export const FABRICATION_DUE_DATE_COLUMN_ID = 'col-fabrication-due-date';
export const SHIPPING_DUE_DATE_COLUMN_ID = 'col-shipping-due-date';

/** Ordered workflow due-date chain used for cascade prompts. */
export const WORKFLOW_DUE_DATE_CHAIN = [
  WORKFLOW_DUE_DATE_MARKER_COLUMN_ID,
  SPOOLING_DUE_DATE_COLUMN_ID,
  FABRICATION_DUE_DATE_COLUMN_ID,
  SHIPPING_DUE_DATE_COLUMN_ID,
] as const;

export type WorkflowDueDateChainColumnId = (typeof WORKFLOW_DUE_DATE_CHAIN)[number];

const WORKFLOW_DUE_DATE_CHAIN_LABELS: Record<WorkflowDueDateChainColumnId, string> = {
  [WORKFLOW_DUE_DATE_MARKER_COLUMN_ID]: 'Detailing',
  [SPOOLING_DUE_DATE_COLUMN_ID]: 'Spooling',
  [FABRICATION_DUE_DATE_COLUMN_ID]: 'Fabrication',
  [SHIPPING_DUE_DATE_COLUMN_ID]: 'Shipping',
};

/** Days after the previous stage when Detailing Due Date is set. */
export interface WorkflowDueDateOffsets {
  /** Spooling due = detailing due + this many days. */
  spoolingDaysAfterDetailing: number;
  /** Fabrication due = spooling due + this many days. */
  fabricationDaysAfterSpooling: number;
  /** Shipping due = fabrication due + this many days. */
  shippingDaysAfterFabrication: number;
}

export const DEFAULT_WORKFLOW_DUE_DATE_OFFSETS: WorkflowDueDateOffsets = {
  spoolingDaysAfterDetailing: 7,
  fabricationDaysAfterSpooling: 7,
  shippingDaysAfterFabrication: 7,
};

function clampDayOffset(value: unknown, fallback: number): number {
  const n = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.max(0, Math.min(3650, Math.round(n)));
}

export function normalizeWorkflowDueDateOffsets(
  raw?: Partial<WorkflowDueDateOffsets> | null
): WorkflowDueDateOffsets {
  return {
    spoolingDaysAfterDetailing: clampDayOffset(
      raw?.spoolingDaysAfterDetailing,
      DEFAULT_WORKFLOW_DUE_DATE_OFFSETS.spoolingDaysAfterDetailing
    ),
    fabricationDaysAfterSpooling: clampDayOffset(
      raw?.fabricationDaysAfterSpooling,
      DEFAULT_WORKFLOW_DUE_DATE_OFFSETS.fabricationDaysAfterSpooling
    ),
    shippingDaysAfterFabrication: clampDayOffset(
      raw?.shippingDaysAfterFabrication,
      DEFAULT_WORKFLOW_DUE_DATE_OFFSETS.shippingDaysAfterFabrication
    ),
  };
}

/** Add calendar days to an ISO date (YYYY-MM-DD). Returns null if invalid. */
export function addCalendarDaysIso(isoDate: string, days: number): string | null {
  const text = isoDate.trim().slice(0, 10);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) return null;
  const [y, m, d] = text.split('-').map(Number);
  const date = new Date(Date.UTC(y!, m! - 1, d!));
  if (Number.isNaN(date.getTime())) return null;
  date.setUTCDate(date.getUTCDate() + days);
  const yy = date.getUTCFullYear();
  const mm = String(date.getUTCMonth() + 1).padStart(2, '0');
  const dd = String(date.getUTCDate()).padStart(2, '0');
  return `${yy}-${mm}-${dd}`;
}

export function isWorkflowDueDateChainColumn(
  columnId: string
): columnId is WorkflowDueDateChainColumnId {
  return (WORKFLOW_DUE_DATE_CHAIN as readonly string[]).includes(columnId);
}

export function followingWorkflowDueDateColumnIds(
  columnId: string
): WorkflowDueDateChainColumnId[] {
  const index = WORKFLOW_DUE_DATE_CHAIN.indexOf(columnId as WorkflowDueDateChainColumnId);
  if (index < 0 || index >= WORKFLOW_DUE_DATE_CHAIN.length - 1) return [];
  return [...WORKFLOW_DUE_DATE_CHAIN.slice(index + 1)];
}

export function labelsForWorkflowDueDateColumns(columnIds: string[]): string {
  return columnIds
    .map((id) => WORKFLOW_DUE_DATE_CHAIN_LABELS[id as WorkflowDueDateChainColumnId] ?? id)
    .join(', ');
}

function fieldValue(
  fields: Record<string, string | null | undefined> | undefined,
  columnId: string
): string {
  const raw = fields?.[columnId];
  if (raw == null) return '';
  return String(raw).trim().slice(0, 10);
}

/** True when any later stage in the chain already has a date. */
export function hasFilledFollowingWorkflowDueDates(
  customFields: Record<string, string | null | undefined> | undefined,
  columnId: string
): boolean {
  return followingWorkflowDueDateColumnIds(columnId).some((id) =>
    Boolean(fieldValue(customFields, id))
  );
}

export function computeWorkflowDueDatesFromDetailing(
  detailingDueDate: string,
  offsets: WorkflowDueDateOffsets
): {
  [SPOOLING_DUE_DATE_COLUMN_ID]: string;
  [FABRICATION_DUE_DATE_COLUMN_ID]: string;
  [SHIPPING_DUE_DATE_COLUMN_ID]: string;
} | null {
  return computeFollowingWorkflowDueDates(
    WORKFLOW_DUE_DATE_MARKER_COLUMN_ID,
    detailingDueDate,
    offsets
  ) as {
    [SPOOLING_DUE_DATE_COLUMN_ID]: string;
    [FABRICATION_DUE_DATE_COLUMN_ID]: string;
    [SHIPPING_DUE_DATE_COLUMN_ID]: string;
  } | null;
}

/**
 * Compute later-stage due dates from the stage that changed.
 * Offsets always apply between adjacent stages in the chain.
 */
export function computeFollowingWorkflowDueDates(
  columnId: string,
  newDate: string,
  offsets: WorkflowDueDateOffsets
): Record<string, string> | null {
  const following = followingWorkflowDueDateColumnIds(columnId);
  if (following.length === 0) return null;

  const normalized = normalizeWorkflowDueDateOffsets(offsets);
  let cursor = newDate.trim().slice(0, 10);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(cursor)) return null;

  const out: Record<string, string> = {};
  for (const nextId of following) {
    let days = 0;
    if (nextId === SPOOLING_DUE_DATE_COLUMN_ID) {
      days = normalized.spoolingDaysAfterDetailing;
    } else if (nextId === FABRICATION_DUE_DATE_COLUMN_ID) {
      days = normalized.fabricationDaysAfterSpooling;
    } else if (nextId === SHIPPING_DUE_DATE_COLUMN_ID) {
      days = normalized.shippingDaysAfterFabrication;
    }
    const nextDate = addCalendarDaysIso(cursor, days);
    if (!nextDate) return null;
    out[nextId] = nextDate;
    cursor = nextDate;
  }
  return out;
}

/**
 * When a chain due-date column is patched, fill later stages that are still empty.
 * Existing later dates are left alone (UI asks before overwriting).
 */
export function cascadeWorkflowDueDatesFromDetailingPatch(
  customFieldsPatch: Record<string, string | null | undefined>,
  offsets: WorkflowDueDateOffsets,
  existingCustomFields?: Record<string, string | null | undefined>
): Record<string, string | null> | null {
  const changedColumn = WORKFLOW_DUE_DATE_CHAIN.find((id) =>
    Object.prototype.hasOwnProperty.call(customFieldsPatch, id)
  );
  if (!changedColumn) return null;

  const raw = customFieldsPatch[changedColumn];
  const value = raw == null ? '' : String(raw).trim().slice(0, 10);
  if (!value) return null;

  const computed = computeFollowingWorkflowDueDates(changedColumn, value, offsets);
  if (!computed) return null;

  const out: Record<string, string | null> = {};
  for (const [columnId, date] of Object.entries(computed)) {
    if (Object.prototype.hasOwnProperty.call(customFieldsPatch, columnId)) continue;
    // Only auto-fill empty following dates; never overwrite without an explicit patch.
    if (fieldValue(existingCustomFields, columnId)) continue;
    out[columnId] = date;
  }
  return Object.keys(out).length > 0 ? out : null;
}
