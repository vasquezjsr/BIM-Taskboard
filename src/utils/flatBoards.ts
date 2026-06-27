import type { ProjectBoardType } from '../types';

export const FLAT_BOARD_TYPES = ['rfi', 'documents'] as const;

export type FlatBoardType = (typeof FLAT_BOARD_TYPES)[number];

export function isFlatBoard(boardType: ProjectBoardType): boardType is FlatBoardType {
  return (FLAT_BOARD_TYPES as readonly string[]).includes(boardType);
}
