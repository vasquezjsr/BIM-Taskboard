import { useLayoutEffect, useRef, useState, type MouseEvent, type ReactNode } from 'react';

const VIEWPORT_PADDING = 8;

interface ContextMenuPanelProps {
  x: number;
  y: number;
  className?: string;
  children: ReactNode;
  onClick?: (e: MouseEvent<HTMLDivElement>) => void;
  onMouseDown?: (e: MouseEvent<HTMLDivElement>) => void;
}

export function ContextMenuPanel({
  x,
  y,
  className,
  children,
  onClick,
  onMouseDown,
}: ContextMenuPanelProps) {
  const ref = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState({ top: y, left: x });

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;

    const menuHeight = el.offsetHeight;
    const menuWidth = el.offsetWidth;

    let top = y;
    let left = x;

    if (top + menuHeight > window.innerHeight - VIEWPORT_PADDING) {
      top = Math.max(VIEWPORT_PADDING, y - menuHeight);
    }

    if (left + menuWidth > window.innerWidth - VIEWPORT_PADDING) {
      left = Math.max(VIEWPORT_PADDING, window.innerWidth - menuWidth - VIEWPORT_PADDING);
    }

    setPosition((prev) =>
      prev.top === top && prev.left === left ? prev : { top, left }
    );
  }, [x, y, children]);

  return (
    <div
      ref={ref}
      className={className}
      style={{ top: position.top, left: position.left }}
      onClick={onClick}
      onMouseDown={onMouseDown}
    >
      {children}
    </div>
  );
}
