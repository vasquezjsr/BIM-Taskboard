import { createPortal } from 'react-dom';
import { useEffect, useState } from 'react';
import { useDroppable } from '@dnd-kit/core';
import { useStore } from '../store/useStore';
import type { ProjectBoardType } from '../types';
import { boardDropId } from '../utils/sheetDrag';
import {
  getBoardTabRect,
  getRegisteredBoardTabTypes,
} from '../utils/boardTabDropRegistry';

function BoardTabDropZone({
  boardType,
  layoutTick,
}: {
  boardType: ProjectBoardType;
  layoutTick: number;
}) {
  const { setNodeRef } = useDroppable({ id: boardDropId(boardType) });
  void layoutTick;
  const rect = getBoardTabRect(boardType);
  if (!rect || rect.width <= 0 || rect.height <= 0) return null;

  return (
    <div
      ref={setNodeRef}
      style={{
        position: 'fixed',
        left: rect.left,
        top: rect.top,
        width: rect.width,
        height: rect.height,
        zIndex: 10000,
        pointerEvents: 'auto',
      }}
      aria-hidden
    />
  );
}

/** Invisible droppable zones aligned to board tabs while dragging sheet rows. */
export function BoardTabDropOverlays() {
  const sheetDragActive = useStore((state) => state.sheetDragActive);
  const [layoutTick, setLayoutTick] = useState(0);

  useEffect(() => {
    if (!sheetDragActive) return;
    const bump = () => setLayoutTick((tick) => tick + 1);
    bump();
    window.addEventListener('scroll', bump, true);
    window.addEventListener('resize', bump);
    return () => {
      window.removeEventListener('scroll', bump, true);
      window.removeEventListener('resize', bump);
    };
  }, [sheetDragActive]);

  if (!sheetDragActive) return null;

  const boardTypes = getRegisteredBoardTabTypes();
  if (boardTypes.length === 0) return null;

  return createPortal(
    <>
      {boardTypes.map((boardType) => (
        <BoardTabDropZone
          key={boardType}
          boardType={boardType}
          layoutTick={layoutTick}
        />
      ))}
    </>,
    document.body
  );
}
