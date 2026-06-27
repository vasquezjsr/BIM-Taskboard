import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
} from 'react';
import styles from './OrgChartViewport.module.css';

const MIN_ZOOM = 0.35;
const MAX_ZOOM = 2.5;
const ZOOM_STEP = 1.08;

export interface OrgChartViewportTransform {
  pan: { x: number; y: number };
  scale: number;
}

const OrgChartViewportContext = createContext<OrgChartViewportTransform>({
  pan: { x: 0, y: 0 },
  scale: 1,
});

export function useOrgChartViewportTransform() {
  return useContext(OrgChartViewportContext);
}

interface OrgChartViewportProps {
  children: ReactNode;
}

export function OrgChartViewport({ children }: OrgChartViewportProps) {
  const viewportRef = useRef<HTMLDivElement>(null);
  const [{ pan, scale }, setTransform] = useState<OrgChartViewportTransform>({
    pan: { x: 0, y: 0 },
    scale: 1,
  });
  const [isPanning, setIsPanning] = useState(false);
  const panAnchorRef = useRef({ x: 0, y: 0 });

  const stopPanning = useCallback(() => setIsPanning(false), []);

  useEffect(() => {
    if (!isPanning) return;

    const onMouseMove = (event: MouseEvent) => {
      setTransform((current) => ({
        ...current,
        pan: {
          x: event.clientX - panAnchorRef.current.x,
          y: event.clientY - panAnchorRef.current.y,
        },
      }));
    };

    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', stopPanning);
    return () => {
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', stopPanning);
    };
  }, [isPanning, stopPanning]);

  useEffect(() => {
    const viewport = viewportRef.current;
    if (!viewport) return;

    const onWheel = (event: WheelEvent) => {
      event.preventDefault();

      const rect = viewport.getBoundingClientRect();
      const pointerX = event.clientX - rect.left;
      const pointerY = event.clientY - rect.top;
      const direction = event.deltaY < 0 ? 1 : -1;
      const factor = direction > 0 ? ZOOM_STEP : 1 / ZOOM_STEP;

      setTransform((current) => {
        const nextScale = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, current.scale * factor));
        const contentX = (pointerX - current.pan.x) / current.scale;
        const contentY = (pointerY - current.pan.y) / current.scale;

        return {
          scale: nextScale,
          pan: {
            x: pointerX - contentX * nextScale,
            y: pointerY - contentY * nextScale,
          },
        };
      });
    };

    viewport.addEventListener('wheel', onWheel, { passive: false });
    return () => viewport.removeEventListener('wheel', onWheel);
  }, []);

  const handleMouseDown = (event: React.MouseEvent<HTMLDivElement>) => {
    if (event.button !== 1) return;
    event.preventDefault();
    panAnchorRef.current = {
      x: event.clientX - pan.x,
      y: event.clientY - pan.y,
    };
    setIsPanning(true);
  };

  const handleAuxClick = (event: React.MouseEvent<HTMLDivElement>) => {
    if (event.button === 1) event.preventDefault();
  };

  const zoomPercent = Math.round(scale * 100);

  const canvasStyle: CSSProperties = {
    left: pan.x,
    top: pan.y,
  };

  return (
    <OrgChartViewportContext.Provider value={{ pan, scale }}>
      <div className={styles.shell}>
        <div
          ref={viewportRef}
          className={`${styles.viewport} ${isPanning ? styles.viewportPanning : ''}`}
          onMouseDown={handleMouseDown}
          onAuxClick={handleAuxClick}
          role="presentation"
        >
          <div className={styles.canvas} style={canvasStyle}>
            {children}
          </div>
        </div>

        <div className={styles.controls} aria-hidden>
          <span className={styles.zoomLabel}>{zoomPercent}%</span>
          <span className={styles.hint}>Middle-click drag to pan · Scroll to zoom</span>
        </div>
      </div>
    </OrgChartViewportContext.Provider>
  );
}
