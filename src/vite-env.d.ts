/// <reference types="vite/client" />

interface BoardroomPackageReadResult {
  manifest: unknown;
  exportFolder: string;
  jsonPath: string;
  siblingFiles: string[];
}

interface ElectronAPI {
  platform: string;
  onRefreshView: (callback: () => void) => () => void;
  setPermissionsMenuVisible: (visible: boolean) => void;
  onNavigateTo: (callback: (tab: string) => void) => () => void;
  onRequestPermissionsMenuSync: (callback: () => void) => () => void;
  getDefaultBoardroomExportsDir?: () => Promise<string>;
  readBoardroomPackage?: (dirOrJsonPath: string) => Promise<BoardroomPackageReadResult>;
  openPath?: (filePath: string) => Promise<{ ok: true } | { ok: false; error: string }>;
  getFilePreviewUrl?: (
    filePath: string
  ) => Promise<
    | { ok: true; fileName: string; mimeType: string; previewUrl: string }
    | { ok: false; error: string }
  >;
  readFilePreview?: (
    filePath: string
  ) => Promise<
    | {
        ok: true;
        fileName: string;
        mimeType: string;
        sizeBytes: number;
        base64: string;
        previewUrl?: string;
      }
    | { ok: false; error: string }
  >;
  writeFileBytes?: (
    filePath: string,
    base64: string
  ) => Promise<{ ok: true; fileName: string } | { ok: false; error: string }>;
  watchBoardroomExports?: () => Promise<
    { ok: true; dir: string } | { ok: false; dir: string; error: string }
  >;
  unwatchBoardroomExports?: () => Promise<{ ok: true }>;
  onBoardroomExportsChanged?: (
    callback: (payload: { dir: string; filename: string | null }) => void
  ) => () => void;
  publishBoardroomApiSnapshot?: (snapshot: unknown) => Promise<{ ok: true }>;
  getBoardroomApiStatus?: () => Promise<{ port: number; host: string }>;
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
