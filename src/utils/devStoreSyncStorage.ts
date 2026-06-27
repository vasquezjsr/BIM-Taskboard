import type { StateStorage } from 'zustand/middleware';

const SYNC_ROUTE = '/__dev/store-sync';

interface DevStoreSyncPayload {
  updatedAt: number;
  state: string;
}

function updatedAtKey(name: string): string {
  return `${name}-updatedAt`;
}

function parsePersistVersion(stateJson: string | null): number {
  if (!stateJson) return 0;
  try {
    const parsed = JSON.parse(stateJson) as { version?: number };
    return typeof parsed.version === 'number' ? parsed.version : 0;
  } catch {
    return 0;
  }
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

/** Dev-only storage: shares zustand persist JSON between localhost:5173 and :5174 via disk. */
export const devStoreSyncStorage: StateStorage = {
  getItem: async (name) => {
    const local = localStorage.getItem(name);
    const localAt = Number(localStorage.getItem(updatedAtKey(name)) || 0);
    const shared = await readSharedPayload();

    if (shared) {
      const sharedVersion = parsePersistVersion(shared.state);
      const localVersion = parsePersistVersion(local);
      const preferShared =
        sharedVersion > localVersion ||
        (sharedVersion === localVersion && shared.updatedAt > localAt);
      if (preferShared) {
        localStorage.setItem(name, shared.state);
        localStorage.setItem(updatedAtKey(name), String(shared.updatedAt));
        return shared.state;
      }
    }

    return local;
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

export async function pullDevStoreSync(): Promise<boolean> {
  if (!import.meta.env.DEV) return false;

  const name = DEV_STORE_SYNC_STORAGE_KEY;
  const local = localStorage.getItem(name);
  const localAt = Number(localStorage.getItem(updatedAtKey(name)) || 0);
  const shared = await readSharedPayload();
  if (!shared) return false;

  const localVersion = parsePersistVersion(local);
  const sharedVersion = parsePersistVersion(shared.state);
  const preferShared =
    sharedVersion > localVersion ||
    (sharedVersion === localVersion && shared.updatedAt > localAt);
  if (!preferShared) return false;

  localStorage.setItem(name, shared.state);
  localStorage.setItem(updatedAtKey(name), String(shared.updatedAt));
  return true;
}

/** Push this tab's localStorage into the shared dev file if it is newer (seeds 5174 → file). */
export async function pushDevStoreSyncIfNewer(): Promise<void> {
  if (!import.meta.env.DEV) return;

  const name = DEV_STORE_SYNC_STORAGE_KEY;
  const local = localStorage.getItem(name);
  if (!local) return;

  const localAt = Number(localStorage.getItem(updatedAtKey(name)) || Date.now());
  const shared = await readSharedPayload();
  const localVersion = parsePersistVersion(local);
  const sharedVersion = shared ? parsePersistVersion(shared.state) : 0;
  if (shared && sharedVersion > localVersion) return;
  if (shared && shared.updatedAt >= localAt && sharedVersion >= localVersion) return;

  await writeSharedPayload({ updatedAt: localAt, state: local });
}
