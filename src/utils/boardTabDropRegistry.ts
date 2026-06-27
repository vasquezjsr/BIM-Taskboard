import type { ProjectBoardType } from '../types';

const tabElements = new Map<ProjectBoardType, HTMLElement>();

export function registerBoardTabElement(
  boardType: ProjectBoardType,
  element: HTMLElement | null
): void {
  if (element) {
    tabElements.set(boardType, element);
  } else {
    tabElements.delete(boardType);
  }
}

export function getRegisteredBoardTabTypes(): ProjectBoardType[] {
  return [...tabElements.keys()];
}

export function getBoardTabRect(boardType: ProjectBoardType): DOMRect | null {
  const element = tabElements.get(boardType);
  if (!element) return null;
  return element.getBoundingClientRect();
}
