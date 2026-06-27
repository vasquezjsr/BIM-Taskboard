/// <reference types="vite/client" />

interface ElectronAPI {
  platform: string;
  onRefreshView: (callback: () => void) => () => void;
  setPermissionsMenuVisible: (visible: boolean) => void;
  onNavigateTo: (callback: (tab: string) => void) => () => void;
  onRequestPermissionsMenuSync: (callback: () => void) => () => void;
}

declare global {
  interface Window {
    electronAPI?: ElectronAPI;
  }
}

export {};

declare module '*.module.css' {
  const classes: { readonly [key: string]: string };
  export default classes;
}
