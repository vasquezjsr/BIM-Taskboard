import type { BoardSheetColumnOrderMap, BoardSheetColumnsMap } from '../utils/sheetColumns';
import {
  ensurePremadeSheetColumns,
  PREMADE_MATERIAL_COLUMN_ID,
} from './premadeSheetColumns';
import { getBoardLocalSheetColumns } from '../utils/sheetColumns';

export {
  PREMADE_MATERIAL_COLUMN_ID as TEMPLATE_MATERIAL_COLUMN_ID,
  PREMADE_MATERIAL_OPTIONS as TEMPLATE_MATERIAL_OPTIONS,
} from './premadeSheetColumns';

export function applyTemplateMaterialColumn(
  boardSheetColumns: BoardSheetColumnsMap,
  boardSheetColumnOrder: BoardSheetColumnOrderMap
): {
  boardSheetColumns: BoardSheetColumnsMap;
  boardSheetColumnOrder: BoardSheetColumnOrderMap;
} {
  return ensurePremadeSheetColumns(boardSheetColumns, boardSheetColumnOrder);
}

export function templateHasCanonicalMaterialColumn(
  boardSheetColumns: BoardSheetColumnsMap
): boolean {
  return getBoardLocalSheetColumns('main', boardSheetColumns).some(
    (column) => column.id === PREMADE_MATERIAL_COLUMN_ID
  );
}
