import type { Modifier } from '@dnd-kit/core';

/** Keep the drag overlay anchored to where the pointer grabbed the card. */
export const snapGrabPointToCursor: Modifier = ({
  activatorEvent,
  draggingNodeRect,
  transform,
}) => {
  if (!draggingNodeRect || !activatorEvent) return transform;
  if (!('clientX' in activatorEvent) || !('clientY' in activatorEvent)) return transform;

  const event = activatorEvent as MouseEvent;
  const grabOffsetX = event.clientX - draggingNodeRect.left;
  const grabOffsetY = event.clientY - draggingNodeRect.top;

  return {
    ...transform,
    x: transform.x + grabOffsetX,
    y: transform.y + grabOffsetY,
  };
};
