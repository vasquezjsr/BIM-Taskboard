import { app, BrowserWindow, ipcMain, Menu, net, protocol, shell } from 'electron';
import fs from 'fs';
import path from 'path';
import { fileURLToPath, pathToFileURL } from 'url';
import {
  setBoardroomApiSnapshot,
  startBoardroomApiServer,
  stopBoardroomApiServer,
  type BoardroomApiSnapshot,
} from './boardroomApiServer';

/** Parent terminal/pipe may close; don't crash the main process on console writes. */
function ignoreBrokenPipe(stream: NodeJS.WriteStream | null | undefined) {
  stream?.on('error', () => {
    /* stdout/stderr write failures (EPIPE/EIO) must not take down Electron */
  });
}
ignoreBrokenPipe(process.stdout);
ignoreBrokenPipe(process.stderr);

process.on('uncaughtException', (error: NodeJS.ErrnoException) => {
  if (error.code === 'EPIPE' || error.code === 'EIO') return;
  try {
    if (app.isReady()) {
      fs.writeFileSync(
        path.join(app.getPath('userData'), 'main-uncaught-exception.log'),
        `${new Date().toISOString()}\n${error.stack ?? error.message}\n`
      );
    }
  } catch {
    /* ignore */
  }
  process.exit(1);
});

const isPackaged = app.isPackaged;
const useDevServer = !isPackaged && process.argv.includes('--dev');
const devServerUrl = process.env.VITE_DEV_SERVER_URL ?? 'http://localhost:5173';
/** Comfortable default density — equivalent to pressing Ctrl+- twice in Chromium. */
const DEFAULT_ZOOM_FACTOR = 0.8;

protocol.registerSchemesAsPrivileged([
  {
    scheme: 'boardroom-file',
    privileges: {
      standard: true,
      secure: true,
      supportFetchAPI: true,
      stream: true,
      bypassCSP: true,
      corsEnabled: true,
    },
  },
]);

let mainWindow: BrowserWindow | null = null;
let boardroomExportsWatcher: fs.FSWatcher | null = null;

function getRepoRoot() {
  // dist-electron/main.js → repo root
  return path.resolve(__dirname, '..');
}

function getDefaultBoardroomExportsDir() {
  return path.join(
    getRepoRoot(),
    'Spooling Savant Version 3 (Exports)',
    'Boardroom',
    'Exports'
  );
}

function mimeForPath(filePath: string): string {
  const ext = path.extname(filePath).toLowerCase();
  const mimeByExt: Record<string, string> = {
    '.pdf': 'application/pdf',
    '.pcf': 'text/plain',
    '.txt': 'text/plain',
    '.csv': 'text/plain',
    '.json': 'application/json',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.jpeg': 'image/jpeg',
    '.webp': 'image/webp',
    '.xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    '.xls': 'application/vnd.ms-excel',
  };
  return mimeByExt[ext] ?? 'application/octet-stream';
}

/** Convert an absolute disk path into a boardroom-file:// URL for in-app preview. */
function toBoardroomFileUrl(filePath: string): string {
  // Use a query param so Chromium does not treat "C:" as a URL host (which became \\c\...).
  return `boardroom-file://preview/?path=${encodeURIComponent(path.resolve(filePath))}`;
}

function filePathFromBoardroomUrl(requestUrl: string): string {
  try {
    const url = new URL(requestUrl);
    const fromQuery = url.searchParams.get('path');
    if (fromQuery) {
      return path.normalize(fromQuery);
    }
  } catch {
    /* fall through */
  }

  const asFileUrl = requestUrl.replace(/^boardroom-file:/i, 'file:');
  try {
    return fileURLToPath(asFileUrl);
  } catch {
    let filePath = decodeURIComponent(requestUrl.replace(/^boardroom-file:\/\//i, ''));
    if (filePath.startsWith('/') && /^\/[A-Za-z]:/.test(filePath)) {
      filePath = filePath.slice(1);
    }
    // Repair Chromium host-as-drive parsing: \\c\Apps\... → C:\Apps\...
    const uncDrive = filePath.match(/^[/\\]{2}([A-Za-z])[/\\](.*)$/);
    if (uncDrive) {
      return path.normalize(`${uncDrive[1]}:\\${uncDrive[2]}`);
    }
    return path.normalize(filePath);
  }
}

function resolveBoardroomPackageJson(dirOrJsonPath: string) {
  const trimmed = (dirOrJsonPath ?? '').trim();
  if (!trimmed) {
    throw new Error('No Boardroom export path provided.');
  }
  const resolved = path.resolve(trimmed);
  if (resolved.toLowerCase().endsWith('.json')) {
    return resolved;
  }
  return path.join(resolved, 'boardroom-package.json');
}

function readBoardroomPackagePayload(dirOrJsonPath: string) {
  const jsonPath = resolveBoardroomPackageJson(dirOrJsonPath);
  if (!fs.existsSync(jsonPath)) {
    throw new Error(`boardroom-package.json not found:\n${jsonPath}`);
  }
  const exportFolder = path.dirname(jsonPath);
  const raw = fs.readFileSync(jsonPath, 'utf8');
  const manifest = JSON.parse(raw) as unknown;
  const siblingFiles = fs
    .readdirSync(exportFolder)
    .filter((name) => name.toLowerCase() !== 'boardroom-package.json')
    .sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
  return {
    manifest,
    exportFolder,
    jsonPath,
    siblingFiles,
  };
}

function sendNavigate(tab: string) {
  mainWindow?.webContents.send('navigate-to', tab);
}

function buildApplicationMenu() {
  const viewSubmenu: Electron.MenuItemConstructorOptions[] = [
    { role: 'reload' },
    { role: 'forceReload' },
    { role: 'toggleDevTools' },
    { type: 'separator' },
    { role: 'resetZoom' },
    { role: 'zoomIn' },
    { role: 'zoomOut' },
    { type: 'separator' },
    { role: 'togglefullscreen' },
  ];

  const editSubmenu: Electron.MenuItemConstructorOptions[] = [
    { role: 'undo' },
    { role: 'redo' },
    { type: 'separator' },
    { role: 'cut' },
    { role: 'copy' },
    { role: 'paste' },
    ...(process.platform === 'darwin'
      ? ([{ role: 'pasteAndMatchStyle' }, { role: 'delete' }, { role: 'selectAll' }] as const)
      : ([{ role: 'delete' }, { type: 'separator' }, { role: 'selectAll' }] as const)),
    { type: 'separator' },
    {
      label: 'Column Settings...',
      accelerator: process.platform === 'darwin' ? 'Cmd+Shift+C' : 'Ctrl+Shift+C',
      click: () => sendNavigate('column-settings'),
    },
  ];

  const template: Electron.MenuItemConstructorOptions[] = [
    ...(process.platform === 'darwin'
      ? [
          {
            label: app.name,
            submenu: [
              { role: 'about' },
              { type: 'separator' },
              { role: 'services' },
              { type: 'separator' },
              { role: 'hide' },
              { role: 'hideOthers' },
              { role: 'unhide' },
              { type: 'separator' },
              { role: 'quit' },
            ],
          } satisfies Electron.MenuItemConstructorOptions,
        ]
      : []),
    {
      label: 'File',
      submenu: [process.platform === 'darwin' ? { role: 'close' } : { role: 'quit' }],
    },
    {
      label: 'Edit',
      submenu: editSubmenu,
    },
    {
      label: 'View',
      submenu: viewSubmenu,
    },
    ...(process.platform === 'darwin' ? [{ role: 'windowMenu' as const }] : []),
  ];

  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

function getAppIconPath() {
  if (isPackaged) {
    return path.join(__dirname, '../dist/icon.png');
  }
  return path.join(__dirname, '../build/icon.png');
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1600,
    height: 900,
    minWidth: 1000,
    minHeight: 700,
    title: 'BIM Boardroom',
    backgroundColor: '#12141c',
    icon: getAppIconPath(),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });

  if (useDevServer) {
    mainWindow.loadURL(devServerUrl);
    mainWindow.webContents.openDevTools({ mode: 'detach' });
  } else {
    mainWindow.loadFile(path.join(__dirname, '../dist/index.html'));
  }

  mainWindow.webContents.on('did-finish-load', () => {
    mainWindow?.webContents.setZoomFactor(DEFAULT_ZOOM_FACTOR);
  });

  mainWindow.webContents.on('before-input-event', (event, input) => {
    if (input.type === 'keyDown' && input.key === 'F5') {
      event.preventDefault();
      mainWindow?.webContents.send('refresh-active-view');
    }
  });

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

ipcMain.on('set-permissions-menu-visible', () => {
  // Organization & Permissions menu removed — keep handler for older renderers.
});

ipcMain.handle('boardroom:get-default-exports-dir', () => getDefaultBoardroomExportsDir());

ipcMain.handle('boardroom:read-package', (_event, dirOrJsonPath: string) => {
  return readBoardroomPackagePayload(dirOrJsonPath);
});

ipcMain.handle('boardroom:open-path', async (_event, filePath: string) => {
  const target = path.resolve(String(filePath ?? ''));
  if (!target || !fs.existsSync(target)) {
    return { ok: false as const, error: `File not found:\n${target}` };
  }
  const error = await shell.openPath(target);
  if (error) {
    return { ok: false as const, error };
  }
  return { ok: true as const };
});

ipcMain.handle('boardroom:read-file-preview', (_event, filePath: string) => {
  const target = path.resolve(String(filePath ?? ''));
  if (!target || !fs.existsSync(target)) {
    return { ok: false as const, error: `File not found:\n${target}` };
  }
  const stat = fs.statSync(target);
  const maxBytes = 40 * 1024 * 1024;
  if (stat.size > maxBytes) {
    return {
      ok: false as const,
      error: `File is too large to preview (${Math.round(stat.size / (1024 * 1024))} MB). Use Open externally.`,
    };
  }
  const mimeType = mimeForPath(target);
  const base64 = fs.readFileSync(target).toString('base64');
  return {
    ok: true as const,
    fileName: path.basename(target),
    mimeType,
    sizeBytes: stat.size,
    base64,
    previewUrl: toBoardroomFileUrl(target),
  };
});

ipcMain.handle('boardroom:get-file-preview-url', (_event, filePath: string) => {
  const target = path.resolve(String(filePath ?? ''));
  if (!target || !fs.existsSync(target)) {
    return { ok: false as const, error: `File not found:\n${target}` };
  }
  return {
    ok: true as const,
    fileName: path.basename(target),
    mimeType: mimeForPath(target),
    previewUrl: toBoardroomFileUrl(target),
  };
});

ipcMain.handle(
  'boardroom:write-file-bytes',
  (_event, filePath: string, base64: string) => {
    const target = path.resolve(String(filePath ?? ''));
    if (!target) {
      return { ok: false as const, error: 'Missing file path.' };
    }
    if (typeof base64 !== 'string' || base64.length === 0) {
      return { ok: false as const, error: 'Missing file contents.' };
    }
    try {
      const dir = path.dirname(target);
      if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true });
      }
      fs.writeFileSync(target, Buffer.from(base64, 'base64'));
      return { ok: true as const, fileName: path.basename(target) };
    } catch (err) {
      return {
        ok: false as const,
        error: err instanceof Error ? err.message : String(err),
      };
    }
  }
);

ipcMain.handle('boardroom:watch-exports', (event) => {
  const dir = getDefaultBoardroomExportsDir();
  try {
    fs.mkdirSync(dir, { recursive: true });
  } catch {
    /* ignore */
  }

  if (boardroomExportsWatcher) {
    boardroomExportsWatcher.close();
    boardroomExportsWatcher = null;
  }

  try {
    boardroomExportsWatcher = fs.watch(dir, { persistent: true }, (_eventType, filename) => {
      const name = filename?.toString() ?? '';
      if (name && !name.toLowerCase().includes('boardroom-package')) {
        return;
      }
      if (!event.sender.isDestroyed()) {
        event.sender.send('boardroom:exports-changed', { dir, filename: name || null });
      }
    });
  } catch (error) {
    return {
      ok: false as const,
      dir,
      error: error instanceof Error ? error.message : String(error),
    };
  }

  return { ok: true as const, dir };
});

function getPersistedStorePath() {
  return path.join(app.getPath('userData'), 'bim-boardroom-store.json');
}

ipcMain.handle('boardroom:save-persisted-store', (_event, payload: { state: string; updatedAt: number }) => {
  try {
    if (!payload?.state || typeof payload.state !== 'string') {
      return { ok: false as const, error: 'missing state' };
    }
    const updatedAt =
      typeof payload.updatedAt === 'number' && Number.isFinite(payload.updatedAt)
        ? payload.updatedAt
        : Date.now();
    const filePath = getPersistedStorePath();
    const bakPath = `${filePath}.bak`;
    try {
      if (fs.existsSync(filePath)) {
        fs.copyFileSync(filePath, bakPath);
      }
    } catch {
      /* ignore bak copy failures */
    }
    fs.writeFileSync(
      filePath,
      JSON.stringify({ updatedAt, state: payload.state }),
      'utf8'
    );
    return { ok: true as const, path: filePath };
  } catch (error) {
    return {
      ok: false as const,
      error: error instanceof Error ? error.message : String(error),
    };
  }
});

ipcMain.handle('boardroom:load-persisted-store', () => {
  try {
    const filePath = getPersistedStorePath();
    const tryRead = (p: string) => {
      if (!fs.existsSync(p)) return null;
      const raw = fs.readFileSync(p, 'utf8');
      const parsed = JSON.parse(raw) as { updatedAt?: number; state?: string };
      if (!parsed?.state || typeof parsed.state !== 'string') return null;
      return {
        ok: true as const,
        state: parsed.state,
        updatedAt: typeof parsed.updatedAt === 'number' ? parsed.updatedAt : 0,
        path: p,
      };
    };
    return tryRead(filePath) ?? tryRead(`${filePath}.bak`) ?? { ok: false as const };
  } catch (error) {
    return {
      ok: false as const,
      error: error instanceof Error ? error.message : String(error),
    };
  }
});

ipcMain.handle('boardroom:clear-persisted-store', () => {
  try {
    const filePath = getPersistedStorePath();
    for (const p of [filePath, `${filePath}.bak`]) {
      if (fs.existsSync(p)) fs.unlinkSync(p);
    }
    return { ok: true as const };
  } catch (error) {
    return {
      ok: false as const,
      error: error instanceof Error ? error.message : String(error),
    };
  }
});

ipcMain.handle('boardroom:unwatch-exports', () => {
  if (boardroomExportsWatcher) {
    boardroomExportsWatcher.close();
    boardroomExportsWatcher = null;
  }
  return { ok: true as const };
});

ipcMain.handle('boardroom-api:publish-snapshot', (_event, payload: BoardroomApiSnapshot) => {
  setBoardroomApiSnapshot(payload ?? null);
  return { ok: true as const };
});

ipcMain.handle('boardroom-api:get-status', () => ({
  port: 17321,
  host: '127.0.0.1',
}));

app.whenReady().then(async () => {
  protocol.handle('boardroom-file', async (request) => {
    try {
      const filePath = filePathFromBoardroomUrl(request.url);
      if (!filePath || !fs.existsSync(filePath)) {
        return new Response(`Not found: ${filePath || request.url}`, {
          status: 404,
          headers: { 'Content-Type': 'text/plain; charset=utf-8' },
        });
      }
      return net.fetch(pathToFileURL(filePath).href);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      return new Response(message, { status: 500 });
    }
  });

  const api = await startBoardroomApiServer();
  // Avoid console.* here — broken stdout/stderr pipes crash Electron with EPIPE.
  if (!api.ok) {
    try {
      process.stderr.write(`Boardroom API failed to start: ${api.error}\n`);
    } catch {
      /* ignore */
    }
  }
  buildApplicationMenu();
  createWindow();
});

app.on('before-quit', () => {
  stopBoardroomApiServer();
  if (boardroomExportsWatcher) {
    boardroomExportsWatcher.close();
    boardroomExportsWatcher = null;
  }
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
});
