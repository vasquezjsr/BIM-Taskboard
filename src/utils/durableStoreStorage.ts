import type { StateStorage } from 'zustand/middleware';
import { DEV_STORE_SYNC_STORAGE_KEY, devStoreSyncStorage } from './devStoreSyncStorage';

const STORE_NAME = DEV_STORE_SYNC_STORAGE_KEY;

function updatedAtKey(name: string): string {
  return `${name}-updatedAt`;
}

type StoreCandidate = { updatedAt: number; state: string; source: string };

async function readElectronBackup(): Promise<StoreCandidate | null> {
  try {
    const api = window.electronAPI;
    if (!api?.loadPersistedStore) return null;
    const result = await api.loadPersistedStore();
    if (!result?.ok || !result.state) return null;
    return {
      updatedAt: result.updatedAt ?? 0,
      state: result.state,
      source: 'electron',
    };
  } catch {
    return null;
  }
}

async function writeElectronBackup(state: string, updatedAt: number): Promise<void> {
  try {
    const api = window.electronAPI;
    if (!api?.savePersistedStore) return;
    await api.savePersistedStore({ state, updatedAt });
  } catch {
    /* best effort */
  }
}

let backupTimer: ReturnType<typeof setTimeout> | null = null;
let pendingBackup: { state: string; updatedAt: number } | null = null;

function scheduleElectronBackup(state: string, updatedAt: number) {
  pendingBackup = { state, updatedAt };
  if (backupTimer) clearTimeout(backupTimer);
  backupTimer = setTimeout(() => {
    backupTimer = null;
    const payload = pendingBackup;
    pendingBackup = null;
    if (payload) void writeElectronBackup(payload.state, payload.updatedAt);
  }, 400);
}

/** Flush any queued Electron backup immediately (blur / beforeunload). */
export function flushDurableStoreBackup(): void {
  if (backupTimer) {
    clearTimeout(backupTimer);
    backupTimer = null;
  }
  const payload = pendingBackup;
  pendingBackup = null;
  if (payload) void writeElectronBackup(payload.state, payload.updatedAt);
}

function readLocalCandidate(name: string): StoreCandidate | null {
  try {
    const state = localStorage.getItem(name);
    if (!state) return null;
    const updatedAt = Number(localStorage.getItem(updatedAtKey(name)) || 0);
    return { updatedAt: Number.isFinite(updatedAt) ? updatedAt : 0, state, source: 'local' };
  } catch {
    return null;
  }
}

/**
 * Persistence that never drops writes during HMR/hydrate races, and mirrors
 * Electron sessions to a durable file under userData so template/detailer work survives.
 *
 * Load uses only localStorage + Electron IPC (no same-origin fetch) so hydrate cannot
 * deadlock against the Vite page load on :5173.
 */
export const durableStoreStorage: StateStorage = {
  getItem: async (name) => {
    try {
      const local = readLocalCandidate(name);
      const backup = await readElectronBackup();

      const candidates = [local, backup].filter(Boolean) as StoreCandidate[];
      if (candidates.length === 0) {
        // Last resort: shared file via sync storage (may fetch). Keep a timeout.
        const shared = await Promise.race([
          Promise.resolve(devStoreSyncStorage.getItem(name)),
          new Promise<null>((resolve) => setTimeout(() => resolve(null), 1500)),
        ]);
        return shared;
      }

      candidates.sort((a, b) => b.updatedAt - a.updatedAt);
      const winner = candidates[0]!;

      try {
        localStorage.setItem(name, winner.state);
        localStorage.setItem(updatedAtKey(name), String(winner.updatedAt));
      } catch {
        /* quota / private mode */
      }

      // Refresh Electron if local won and is newer — do not block hydrate.
      if (backup && winner.source !== 'electron' && winner.updatedAt > backup.updatedAt) {
        scheduleElectronBackup(winner.state, winner.updatedAt);
      }
      // Mirror to shared sync without awaiting (avoids :5173 connection deadlock).
      if (import.meta.env.DEV) {
        void Promise.resolve(devStoreSyncStorage.setItem(name, winner.state)).catch(() => undefined);
      }

      return winner.state;
    } catch (error) {
      console.error('durableStoreStorage.getItem failed', error);
      return readLocalCandidate(name)?.state ?? null;
    }
  },

  setItem: async (name, value) => {
    const updatedAt = Date.now();
    localStorage.setItem(name, value);
    localStorage.setItem(updatedAtKey(name), String(updatedAt));
    scheduleElectronBackup(value, updatedAt);
    if (import.meta.env.DEV) {
      // Fire-and-forget shared sync — never block UI/persist on Vite fetch.
      void Promise.resolve(devStoreSyncStorage.setItem(name, value)).catch(() => undefined);
    }
  },

  removeItem: async (name) => {
    localStorage.removeItem(name);
    localStorage.removeItem(updatedAtKey(name));
    try {
      await window.electronAPI?.clearPersistedStore?.();
    } catch {
      /* ignore */
    }
    if (import.meta.env.DEV) {
      void Promise.resolve(devStoreSyncStorage.removeItem(name)).catch(() => undefined);
    }
  },
};

export function installDurableStoreFlushHooks(): void {
  if (typeof window === 'undefined') return;
  const flush = () => flushDurableStoreBackup();
  window.addEventListener('beforeunload', flush);
  window.addEventListener('pagehide', flush);
  window.addEventListener('blur', flush);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') flush();
  });
}

export { STORE_NAME as DURABLE_STORE_NAME };
