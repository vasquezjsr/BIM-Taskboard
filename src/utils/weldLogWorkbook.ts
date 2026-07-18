import * as XLSX from 'xlsx';

export const WELD_LOG_HEADERS = [
  'Weld Number',
  'Date',
  'Welder ID',
  'Initials',
  'Material',
  'Weld Type',
  'Assembly',
] as const;

export type WeldLogColumn = (typeof WELD_LOG_HEADERS)[number];

export interface WeldLogRow {
  weldNumber: string;
  date: string;
  welderId: string;
  initials: string;
  material: string;
  weldType: string;
  /** Assembly / spool name from SSv3 export (groups Field Welds under the right assembly). */
  assembly: string;
}

export type WeldLogFillField = 'date' | 'welderId' | 'initials';

function cellText(value: unknown): string {
  if (value == null) return '';
  if (value instanceof Date) {
    const y = value.getFullYear();
    const m = String(value.getMonth() + 1).padStart(2, '0');
    const d = String(value.getDate()).padStart(2, '0');
    return `${y}-${m}-${d}`;
  }
  return String(value).trim();
}

function normalizeHeader(value: string): string {
  return value.trim().toLowerCase().replace(/\s+/g, ' ');
}

function headerIndexMap(headerRow: unknown[]): Partial<Record<WeldLogColumn, number>> {
  const map: Partial<Record<WeldLogColumn, number>> = {};
  headerRow.forEach((cell, index) => {
    const key = normalizeHeader(cellText(cell));
    for (const header of WELD_LOG_HEADERS) {
      if (normalizeHeader(header) === key) {
        map[header] = index;
      }
    }
  });
  return map;
}

export function isFieldWeldRow(row: Pick<WeldLogRow, 'weldType' | 'weldNumber'>): boolean {
  if (/field\s*weld/i.test(row.weldType ?? '')) return true;
  return /(?:^|[\-_/])FW-\d+/i.test(row.weldNumber ?? '');
}

export function parseWeldLogWorkbook(buffer: ArrayBuffer): WeldLogRow[] {
  const workbook = XLSX.read(buffer, { type: 'array', cellDates: true });
  const sheetName =
    workbook.SheetNames.find((name) => /weld\s*log/i.test(name)) ?? workbook.SheetNames[0];
  if (!sheetName) return [];
  const sheet = workbook.Sheets[sheetName];
  if (!sheet) return [];
  const rows = XLSX.utils.sheet_to_json<(string | number | Date | null)[]>(sheet, {
    header: 1,
    defval: '',
    raw: false,
  });
  if (rows.length === 0) return [];

  const indexes = headerIndexMap(rows[0] ?? []);
  const weldNumberIdx = indexes['Weld Number'] ?? 0;
  const dateIdx = indexes.Date ?? 1;
  const welderIdIdx = indexes['Welder ID'] ?? 2;
  const initialsIdx = indexes.Initials ?? 3;
  const materialIdx = indexes.Material ?? 4;
  const weldTypeIdx = indexes['Weld Type'] ?? 5;
  const assemblyIdx = indexes.Assembly;

  const out: WeldLogRow[] = [];
  for (let i = 1; i < rows.length; i += 1) {
    const row = rows[i] ?? [];
    const weldNumber = cellText(row[weldNumberIdx]);
    if (!weldNumber) continue;
    out.push({
      weldNumber,
      date: cellText(row[dateIdx]),
      welderId: cellText(row[welderIdIdx]),
      initials: cellText(row[initialsIdx]),
      material: cellText(row[materialIdx]),
      weldType: cellText(row[weldTypeIdx]),
      assembly: assemblyIdx != null ? cellText(row[assemblyIdx]) : '',
    });
  }
  return out;
}

export function serializeWeldLogWorkbook(rows: WeldLogRow[]): ArrayBuffer {
  const aoa: string[][] = [
    [...WELD_LOG_HEADERS],
    ...rows.map((row) => [
      row.weldNumber,
      row.date,
      row.welderId,
      row.initials,
      row.material,
      row.weldType,
      row.assembly ?? '',
    ]),
  ];
  const sheet = XLSX.utils.aoa_to_sheet(aoa);
  const workbook = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(workbook, sheet, 'Weld Log');
  const out = XLSX.write(workbook, { type: 'array', bookType: 'xlsx' }) as Uint8Array;
  const copy = new Uint8Array(out);
  return copy.buffer;
}

export function filterWeldLogRowsForAssembly(
  rows: WeldLogRow[],
  assemblyName: string
): WeldLogRow[] {
  const needle = assemblyName.trim().toLowerCase();
  if (!needle) return rows;
  return rows.filter((row) => {
    const assembly = (row.assembly ?? '').trim().toLowerCase();
    if (assembly && assembly === needle) return true;
    const weld = row.weldNumber.toLowerCase();
    return weld === needle || weld.startsWith(`${needle}-`) || weld.includes(needle);
  });
}

export function findWeldLogFileName(fileNames: string[]): string | null {
  const match = fileNames.find((name) => /weld\s*log/i.test(name) && /\.xlsx$/i.test(name));
  return match ?? null;
}

export function todayLocalIsoDate(now = new Date()): string {
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

export function applyWeldLogFill(
  row: WeldLogRow,
  field: WeldLogFillField,
  values: { date: string; welderId: string; initials: string }
): WeldLogRow {
  if (field === 'date') return { ...row, date: values.date };
  if (field === 'welderId') return { ...row, welderId: values.welderId };
  return { ...row, initials: values.initials };
}

/** Tap fills an empty cell; tap again clears it. */
export function toggleWeldLogFill(
  row: WeldLogRow,
  field: WeldLogFillField,
  values: { date: string; welderId: string; initials: string }
): WeldLogRow {
  const current =
    field === 'date' ? row.date : field === 'welderId' ? row.welderId : row.initials;
  if (current.trim()) {
    if (field === 'date') return { ...row, date: '' };
    if (field === 'welderId') return { ...row, welderId: '' };
    return { ...row, initials: '' };
  }
  return applyWeldLogFill(row, field, values);
}

export function base64ToArrayBuffer(base64: string): ArrayBuffer {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes.buffer;
}

export function arrayBufferToBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}
