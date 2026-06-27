import { contextBridge, ipcRenderer } from 'electron';

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
});
