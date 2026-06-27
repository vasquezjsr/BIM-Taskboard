import type { ProjectBoardType, SheetColumnDefinition } from '../types';
import { DEFAULT_SHEET_COLUMN_ALIGNMENT } from '../utils/sheetColumnConstants';

export const WORKFLOW_DUE_DATE_MARKER_COLUMN_ID = 'col-detailing-due-date';

export const WORKFLOW_DUE_DATE_COLUMNS: SheetColumnDefinition[] = [
  {
    id: WORKFLOW_DUE_DATE_MARKER_COLUMN_ID,
    label: 'Detailing Due Date',
    type: 'date',
    headerAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
    cellAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
  },
  {
    id: 'col-spooling-due-date',
    label: 'Spooling Due Date',
    type: 'date',
    headerAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
    cellAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
  },
  {
    id: 'col-fabrication-due-date',
    label: 'Fabrication Due Date',
    type: 'date',
    headerAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
    cellAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
  },
  {
    id: 'col-shipping-due-date',
    label: 'Shipping Due Date',
    type: 'date',
    headerAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
    cellAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
  },
  {
    id: 'col-received-date',
    label: 'Received Date',
    type: 'date',
    headerAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
    cellAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
  },
  {
    id: 'col-install-date',
    label: 'Install Date',
    type: 'date',
    headerAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
    cellAlignment: DEFAULT_SHEET_COLUMN_ALIGNMENT,
  },
];

export const WORKFLOW_DUE_DATE_COLUMN_IDS = WORKFLOW_DUE_DATE_COLUMNS.map((column) => column.id);

/** Boards that use workflow due date columns instead of the fixed Due Date column. */
export const WORKFLOW_DUE_DATE_BOARDS: ProjectBoardType[] = [
  'main',
  'detailers',
  'deliverables',
  'spooling',
  'fab',
  'shipping',
  'field',
];

/** Sub-boards that inherit custom columns created on Main Overview. */
export const MAIN_OVERVIEW_SHARED_COLUMN_BOARDS: ProjectBoardType[] = [
  'detailers',
  'deliverables',
  'spooling',
  'fab',
  'shipping',
  'field',
];

const WORKFLOW_DUE_DATE_BOARD_SET = new Set<ProjectBoardType>(WORKFLOW_DUE_DATE_BOARDS);
const WORKFLOW_DUE_DATE_ID_SET = new Set<string>(WORKFLOW_DUE_DATE_COLUMN_IDS);

export function boardUsesWorkflowDueDates(boardType: ProjectBoardType): boolean {
  return WORKFLOW_DUE_DATE_BOARD_SET.has(boardType);
}

export function isWorkflowDueDateColumn(columnId: string): boolean {
  return WORKFLOW_DUE_DATE_ID_SET.has(columnId);
}
