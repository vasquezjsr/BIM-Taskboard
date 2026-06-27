import { app, BrowserWindow, ipcMain, Menu } from 'electron';
import path from 'path';

const isPackaged = app.isPackaged;
const useDevServer = !isPackaged && process.argv.includes('--dev');
const devServerUrl = process.env.VITE_DEV_SERVER_URL ?? 'http://localhost:5173';
/** Comfortable default density — equivalent to pressing Ctrl+- twice in Chromium. */
const DEFAULT_ZOOM_FACTOR = 0.8;

let mainWindow: BrowserWindow | null = null;
let permissionsMenuVisible = false;

function sendNavigate(tab: string) {
  mainWindow?.webContents.send('navigate-to', tab);
}

function requestPermissionsMenuSync() {
  mainWindow?.webContents.send('request-permissions-menu-sync');
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
    { role: 'editMenu' },
    ...(permissionsMenuVisible
      ? [
          {
            label: 'Organization & Permissions',
            submenu: [
              {
                label: 'Open Org Chart',
                accelerator: process.platform === 'darwin' ? 'Cmd+Shift+P' : 'Ctrl+Shift+P',
                click: () => sendNavigate('org-chart'),
              },
            ],
          } satisfies Electron.MenuItemConstructorOptions,
        ]
      : []),
    {
      label: 'View',
      submenu: viewSubmenu,
    },
    ...(process.platform === 'darwin' ? [{ role: 'windowMenu' as const }] : []),
  ];

  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1600,
    height: 900,
    minWidth: 1000,
    minHeight: 700,
    title: 'BIM Boardroom',
    backgroundColor: '#12141c',
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
    requestPermissionsMenuSync();
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

ipcMain.on('set-permissions-menu-visible', (_event, visible: boolean) => {
  permissionsMenuVisible = visible;
  buildApplicationMenu();
});

app.whenReady().then(() => {
  buildApplicationMenu();
  createWindow();
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
});
