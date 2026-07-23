import { useEffect, useState } from 'react';
import {
  APP_ZOOM_CHANGED_EVENT,
  getCurrentAppZoom,
} from '../utils/appZoom';
import styles from './ZoomIndicator.module.css';

/** Brief on-screen zoom % when Ctrl+scroll (or Ctrl+/-) changes scale. */
export function ZoomIndicator() {
  const [percent, setPercent] = useState<number | null>(null);

  useEffect(() => {
    let hideTimer: number | undefined;
    const onZoom = (event: Event) => {
      const detail = (event as CustomEvent<{ factor: number }>).detail;
      const factor = detail?.factor ?? getCurrentAppZoom();
      setPercent(Math.round(factor * 100));
      window.clearTimeout(hideTimer);
      hideTimer = window.setTimeout(() => setPercent(null), 1200);
    };
    window.addEventListener(APP_ZOOM_CHANGED_EVENT, onZoom);
    return () => {
      window.removeEventListener(APP_ZOOM_CHANGED_EVENT, onZoom);
      window.clearTimeout(hideTimer);
    };
  }, []);

  if (percent == null) return null;

  return (
    <div className={styles.badge} role="status" aria-live="polite">
      Zoom {percent}%
    </div>
  );
}
