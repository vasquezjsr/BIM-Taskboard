import type { StateStorage } from 'zustand/middleware';

const SYNC_ROUTE = '/__dev/store-sync';

interface DevStoreSyncPayload {
  updatedAt: number;
  state: string;
}

function updatedAtKey(name: string): string {
  return `${name}-updatedAt`;
}

async function readSharedPayload(): Promise<DevStoreSyncPayload | null> {
  try {
    const res = await fetch(SYNC_ROUTE);
    if (!res.ok) return null;
    const payload = (await res.json()) as DevStoreSyncPayload;
    if (!payload?.state || typeof payload.updatedAt !== 'number') return null;
    return payload;
  } catch {
    return null;
  }
}

async function writeSharedPayload(payload: DevStoreSyncPayload): Promise<void> {
  try {
    await fetch(SYNC_ROUTE, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
  } catch {
    /* dev-only best effort */
  }
}

export const DEV_STORE_SYNC_STORAGE_KEY = 'bim-task-board-storage';

/**
 * Dev storage for 5173/5174.
 * CRITICAL: If this browser profile already has localStorage, that wins — never replace it
 * with the shared disk file on refresh (that was wiping Demo Mechanical on Ctrl+Shift+R).
 * Shared file is only used when local is empty (first boot / new profile).
 */
export const devStoreSyncStorage: StateStorage = {
  getItem: async (name) => {
    const local = localStorage.getItem(name);
    if (local) {
      return local;
    }

    const shared = await readSharedPayload();
    if (!shared) return null;

    localStorage.setItem(name, shared.state);
    localStorage.setItem(updatedAtKey(name), String(shared.updatedAt));
    return shared.state;
  },

  setItem: async (name, value) => {
    const updatedAt = Date.now();
    localStorage.setItem(name, value);
    localStorage.setItem(updatedAtKey(name), String(updatedAt));
    await writeSharedPayload({ updatedAt, state: value });
  },

  removeItem: async (name) => {
    localStorage.removeItem(name);
    localStorage.removeItem(updatedAtKey(name));
    try {
      await fetch(SYNC_ROUTE, { method: 'DELETE' });
    } catch {
      /* ignore */
    }
  },
};

/** @deprecated Pulling shared over local caused data loss — always no-op now. */
export async function pullDevStoreSync(): Promise<boolean> {
  return false;
}

/** Push this tab's localStorage into the shared dev file (backup / seed other ports). */
export async function pushDevStoreSyncIfNewer(): Promise<void> {
  if (!import.meta.env.DEV) return;

  const name = DEV_STORE_SYNC_STORAGE_KEY;
  const local = localStorage.getItem(name);
  if (!local) return;

  const updatedAt = Number(localStorage.getItem(updatedAtKey(name)) || Date.now());
  await writeSharedPayload({ updatedAt: Math.max(updatedAt, Date.now()), state: local });
}
