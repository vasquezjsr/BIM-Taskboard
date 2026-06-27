export interface EmployeeAssigneeStyle {
  border: string;
  background: string;
  text: string;
}

export type EmployeeAssigneeStylesMap = Record<string, EmployeeAssigneeStyle>;

/** Distinct assignee badge colors — border hex must be unique across the roster. */
export const ASSIGNEE_COLOR_PALETTE: EmployeeAssigneeStyle[] = [
  { border: '#818cf8', background: 'rgba(129, 140, 248, 0.2)', text: '#c7d2fe' },
  { border: '#a78bfa', background: 'rgba(167, 139, 250, 0.2)', text: '#ddd6fe' },
  { border: '#38bdf8', background: 'rgba(56, 189, 248, 0.18)', text: '#bae6fd' },
  { border: '#2dd4bf', background: 'rgba(45, 212, 191, 0.18)', text: '#99f6e4' },
  { border: '#22c55e', background: 'rgba(34, 197, 94, 0.18)', text: '#bbf7d0' },
  { border: '#84cc16', background: 'rgba(132, 204, 22, 0.18)', text: '#d9f99d' },
  { border: '#fbbf24', background: 'rgba(251, 191, 36, 0.18)', text: '#fde68a' },
  { border: '#fb923c', background: 'rgba(251, 146, 60, 0.18)', text: '#fed7aa' },
  { border: '#f472b6', background: 'rgba(244, 114, 182, 0.18)', text: '#fbcfe8' },
  { border: '#4ade80', background: 'rgba(74, 222, 128, 0.18)', text: '#bbf7d0' },
  { border: '#60a5fa', background: 'rgba(96, 165, 250, 0.18)', text: '#bfdbfe' },
  { border: '#c084fc', background: 'rgba(192, 132, 252, 0.18)', text: '#e9d5ff' },
  { border: '#f87171', background: 'rgba(248, 113, 113, 0.18)', text: '#fecaca' },
  { border: '#e879f9', background: 'rgba(232, 121, 249, 0.18)', text: '#f5d0fe' },
  { border: '#facc15', background: 'rgba(250, 204, 21, 0.18)', text: '#fef08a' },
  { border: '#94a3b8', background: 'rgba(148, 163, 184, 0.18)', text: '#cbd5e1' },
  { border: '#14b8a6', background: 'rgba(20, 184, 166, 0.18)', text: '#99f6e4' },
  { border: '#a3e635', background: 'rgba(163, 230, 53, 0.18)', text: '#ecfccb' },
  { border: '#fb7185', background: 'rgba(251, 113, 133, 0.18)', text: '#fecdd3' },
  { border: '#6366f1', background: 'rgba(99, 102, 241, 0.18)', text: '#c7d2fe' },
  { border: '#0ea5e9', background: 'rgba(14, 165, 233, 0.18)', text: '#bae6fd' },
  { border: '#d946ef', background: 'rgba(217, 70, 239, 0.18)', text: '#f5d0fe' },
  { border: '#ea580c', background: 'rgba(234, 88, 12, 0.18)', text: '#fed7aa' },
];

/** Preferred seed colors keyed by default employee id. */
export const SEED_EMPLOYEE_ASSIGNEE_STYLES: Record<string, EmployeeAssigneeStyle> = {
  'emp-owner-1': ASSIGNEE_COLOR_PALETTE[15]!,
  'emp-detailer-1': ASSIGNEE_COLOR_PALETTE[0]!,
  'emp-detailer-2': ASSIGNEE_COLOR_PALETTE[1]!,
  'emp-detailer-3': ASSIGNEE_COLOR_PALETTE[2]!,
  'emp-detailer-4': ASSIGNEE_COLOR_PALETTE[3]!,
  'emp-detailer-5': ASSIGNEE_COLOR_PALETTE[4]!,
  'emp-detailer-6': ASSIGNEE_COLOR_PALETTE[5]!,
  'emp-detailer-7': ASSIGNEE_COLOR_PALETTE[10]!,
  'emp-detailer-8': ASSIGNEE_COLOR_PALETTE[11]!,
  'emp-support-1': ASSIGNEE_COLOR_PALETTE[6]!,
  'emp-support-2': ASSIGNEE_COLOR_PALETTE[7]!,
  'emp-support-3': ASSIGNEE_COLOR_PALETTE[8]!,
  'emp-support-4': ASSIGNEE_COLOR_PALETTE[9]!,
  'emp-support-5': ASSIGNEE_COLOR_PALETTE[12]!,
};

export function assigneeStyleKey(style: EmployeeAssigneeStyle): string {
  return style.border.toLowerCase();
}

export function pickNextUniqueAssigneeStyle(usedKeys: Set<string>): EmployeeAssigneeStyle {
  for (const style of ASSIGNEE_COLOR_PALETTE) {
    const key = assigneeStyleKey(style);
    if (!usedKeys.has(key)) return style;
  }

  const index = usedKeys.size % ASSIGNEE_COLOR_PALETTE.length;
  const base = ASSIGNEE_COLOR_PALETTE[index]!;
  const shift = Math.floor(usedKeys.size / ASSIGNEE_COLOR_PALETTE.length);
  if (shift === 0) return base;

  return {
    ...base,
    border: adjustHexLightness(base.border, shift * 4),
    background: base.background,
    text: base.text,
  };
}

function adjustHexLightness(hex: string, amount: number): string {
  const normalized = hex.replace('#', '');
  if (normalized.length !== 6) return hex;
  const channels = [0, 2, 4].map((start) => Number.parseInt(normalized.slice(start, start + 2), 16));
  const adjusted = channels.map((channel) => Math.max(0, Math.min(255, channel + amount)));
  return `#${adjusted.map((channel) => channel.toString(16).padStart(2, '0')).join('')}`;
}

export function buildUniqueAssigneeStyles(
  employeeIds: string[],
  existing: EmployeeAssigneeStylesMap = {},
  preferred: Record<string, EmployeeAssigneeStyle> = SEED_EMPLOYEE_ASSIGNEE_STYLES
): EmployeeAssigneeStylesMap {
  const usedKeys = new Set<string>();
  const result: EmployeeAssigneeStylesMap = {};

  for (const employeeId of employeeIds) {
    const candidate = existing[employeeId] ?? preferred[employeeId];
    if (!candidate) continue;
    const key = assigneeStyleKey(candidate);
    if (usedKeys.has(key)) continue;
    result[employeeId] = candidate;
    usedKeys.add(key);
  }

  for (const employeeId of employeeIds) {
    if (result[employeeId]) continue;
    const style = pickNextUniqueAssigneeStyle(usedKeys);
    result[employeeId] = style;
    usedKeys.add(assigneeStyleKey(style));
  }

  return result;
}

export function createDefaultEmployeeAssigneeStyles(
  employeeIds: string[]
): EmployeeAssigneeStylesMap {
  return buildUniqueAssigneeStyles(employeeIds);
}

export function employeeAssigneeStyleFromMap(
  employeeId: string,
  styleMap: EmployeeAssigneeStylesMap
): EmployeeAssigneeStyle {
  if (styleMap[employeeId]) return styleMap[employeeId];
  return pickNextUniqueAssigneeStyle(new Set(Object.values(styleMap).map(assigneeStyleKey)));
}
