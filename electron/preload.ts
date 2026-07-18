import { contextBridge, ipcRenderer } from 'electron';

export type BoardroomPackageReadResult = {
  manifest: unknown;
  exportFolder: string;
  jsonPath: string;
  siblingFiles: string[];
};

contextBridge.exposeInMainWorld('electronAPI', {
  platform: process.platform,
  onRefreshView: (callback: () => void) => {
    const handler = () => callback();
    ipcRenderer.on('refresh-active-view', handler);
    return () => ipcRenderer.removeListener('refresh-active-view', handler);
  },
  setPermissionsMenuVisible: (visible: boolean) => {
    ipcRenderer.send('set-permissions-menu-visible', visible);
  },
  onNavigateTo: (callback: (tab: string) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, tab: string) => callback(tab);
    ipcRenderer.on('navigate-to', handler);
    return () => ipcRenderer.removeListener('navigate-to', handler);
  },
  onRequestPermissionsMenuSync: (callback: () => void) => {
    const handler = () => callback();
    ipcRenderer.on('request-permissions-menu-sync', handler);
    return () => ipcRenderer.removeListener('request-permissions-menu-sync', handler);
  },
  getDefaultBoardroomExportsDir: (): Promise<string> =>
    ipcRenderer.invoke('boardroom:get-default-exports-dir'),
  readBoardroomPackage: (dirOrJsonPath: string): Promise<BoardroomPackageReadResult> =>
    ipcRenderer.invoke('boardroom:read-package', dirOrJsonPath),
  openPath: (filePath: string): Promise<{ ok: true } | { ok: false; error: string }> =>
    ipcRenderer.invoke('boardroom:open-path', filePath),
  getFilePreviewUrl: (
    filePath: string
  ): Promise<
    | { ok: true; fileName: string; mimeType: string; previewUrl: string }
    | { ok: false; error: string }
  > => ipcRenderer.invoke('boardroom:get-file-preview-url', filePath),
  readFilePreview: (
    filePath: string
  ): Promise<
    | {
        ok: true;
        fileName: string;
        mimeType: string;
        sizeBytes: number;
        base64: string;
        previewUrl?: string;
      }
    | { ok: false; error: string }
  > => ipcRenderer.invoke('boardroom:read-file-preview', filePath),
  writeFileBytes: (
    filePath: string,
    base64: string
  ): Promise<{ ok: true; fileName: string } | { ok: false; error: string }> =>
    ipcRenderer.invoke('boardroom:write-file-bytes', filePath, base64),
  watchBoardroomExports: (): Promise<
    { ok: true; dir: string } | { ok: false; dir: string; error: string }
  > => ipcRenderer.invoke('boardroom:watch-exports'),
  unwatchBoardroomExports: (): Promise<{ ok: true }> =>
    ipcRenderer.invoke('boardroom:unwatch-exports'),
  onBoardroomExportsChanged: (
    callback: (payload: { dir: string; filename: string | null }) => void
  ) => {
    const handler = (
      _event: Electron.IpcRendererEvent,
      payload: { dir: string; filename: string | null }
    ) => callback(payload);
    ipcRenderer.on('boardroom:exports-changed', handler);
    return () => ipcRenderer.removeListener('boardroom:exports-changed', handler);
  },
  publishBoardroomApiSnapshot: (snapshot: unknown): Promise<{ ok: true }> =>
    ipcRenderer.invoke('boardroom-api:publish-snapshot', snapshot),
  getBoardroomApiStatus: (): Promise<{ port: number; host: string }> =>
    ipcRenderer.invoke('boardroom-api:get-status'),
  savePersistedStore: (payload: {
    state: string;
    updatedAt: number;
  }): Promise<{ ok: true; path: string } | { ok: false; error: string }> =>
    ipcRenderer.invoke('boardroom:save-persisted-store', payload),
  loadPersistedStore: (): Promise<
    | { ok: true; state: string; updatedAt: number; path: string }
    | { ok: false; error?: string }
  > => ipcRenderer.invoke('boardroom:load-persisted-store'),
  clearPersistedStore: (): Promise<{ ok: true } | { ok: false; error: string }> =>
    ipcRenderer.invoke('boardroom:clear-persisted-store'),
});
