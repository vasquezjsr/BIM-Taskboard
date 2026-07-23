const STORAGE_KEY = 'bim-boardroom-zoom-factor';
export const APP_ZOOM_CHANGED_EVENT = 'bim-app-zoom-changed';

/** Matches Electron DEFAULT_ZOOM_FACTOR. */
export const DEFAULT_APP_ZOOM = 0.8;
export const MIN_APP_ZOOM = 0.5;
export const MAX_APP_ZOOM = 2.5;
/** ~5% steps; wheel deltas accumulate smoothly. */
export const APP_ZOOM_STEP = 0.05;

export function clampAppZoom(factor: number): number {
  if (!Number.isFinite(factor)) return DEFAULT_APP_ZOOM;
  return Math.min(MAX_APP_ZOOM, Math.max(MIN_APP_ZOOM, Math.round(factor * 100) / 100));
}

export function loadSavedAppZoom(): number {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw == null || raw === '') return DEFAULT_APP_ZOOM;
    return clampAppZoom(Number(raw));
  } catch {
    return DEFAULT_APP_ZOOM;
  }
}

function saveAppZoom(factor: number): void {
  try {
    localStorage.setItem(STORAGE_KEY, String(factor));
  } catch {
    // ignore quota / private mode
  }
}

function notifyZoomChanged(factor: number): void {
  window.dispatchEvent(
    new CustomEvent(APP_ZOOM_CHANGED_EVENT, { detail: { factor } })
  );
}

export function getCurrentAppZoom(): number {
  if (window.electronAPI?.getZoomFactor) {
    return clampAppZoom(window.electronAPI.getZoomFactor());
  }
  const cssZoom = document.documentElement.style.zoom;
  if (cssZoom) {
    const n = Number.parseFloat(cssZoom);
    if (Number.isFinite(n) && n > 0) return clampAppZoom(n);
  }
  return loadSavedAppZoom();
}

/** Apply zoom in Electron (webFrame) or browser (CSS zoom). */
export function applyAppZoom(factor: number, options?: { persist?: boolean }): number {
  const next = clampAppZoom(factor);
  if (window.electronAPI?.setZoomFactor) {
    window.electronAPI.setZoomFactor(next);
  } else {
    document.documentElement.style.zoom = String(next);
  }
  if (options?.persist !== false) saveAppZoom(next);
  notifyZoomChanged(next);
  return next;
}

/** Wheel deltaY > 0 → zoom out (browser convention). */
export function adjustAppZoomByWheel(deltaY: number): number {
  const current = getCurrentAppZoom();
  const direction = deltaY > 0 ? -1 : deltaY < 0 ? 1 : 0;
  if (direction === 0) return current;
  return applyAppZoom(current + direction * APP_ZOOM_STEP);
}

export function resetAppZoom(): number {
  return applyAppZoom(DEFAULT_APP_ZOOM);
}

/**
 * Ctrl/Cmd + mouse wheel zooms the whole app.
 * Returns cleanup. Call once at startup.
 */
export function installCtrlScrollZoom(): () => void {
  applyAppZoom(loadSavedAppZoom(), { persist: false });

  const onWheel = (event: WheelEvent) => {
    if (!(event.ctrlKey || event.metaKey)) return;
    // Don't steal zoom from editable text when not needed — still zoom app (accessibility).
    event.preventDefault();
    adjustAppZoomByWheel(event.deltaY);
  };

  const onKeyDown = (event: KeyboardEvent) => {
    if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
    const key = event.key;
    if (key === '=' || key === '+') {
      event.preventDefault();
      applyAppZoom(getCurrentAppZoom() + APP_ZOOM_STEP);
    } else if (key === '-' || key === '_') {
      event.preventDefault();
      applyAppZoom(getCurrentAppZoom() - APP_ZOOM_STEP);
    } else if (key === '0') {
      event.preventDefault();
      resetAppZoom();
    }
  };

  window.addEventListener('wheel', onWheel, { passive: false });
  window.addEventListener('keydown', onKeyDown);
  return () => {
    window.removeEventListener('wheel', onWheel);
    window.removeEventListener('keydown', onKeyDown);
  };
}
