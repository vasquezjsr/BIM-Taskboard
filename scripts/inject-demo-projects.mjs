/**
 * Injects Demo Mechanical + 3 projects into the live Boardroom Zustand store via CDP.
 * Requires Electron/Chromium with --remote-debugging-port=9222
 */
import http from 'node:http';
import fs from 'node:fs';

const DEBUG_PORT = 9222;

function getJson(url) {
  return new Promise((resolve, reject) => {
    http
      .get(url, (res) => {
        let data = '';
        res.on('data', (c) => (data += c));
        res.on('end', () => {
          try {
            resolve(JSON.parse(data));
          } catch (e) {
            reject(e);
          }
        });
      })
      .on('error', reject);
  });
}

async function waitForDebugger(timeoutMs = 60000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const list = await getJson(`http://127.0.0.1:${DEBUG_PORT}/json/list`);
      const page = list.find(
        (t) =>
          t.type === 'page' &&
          (t.url.includes('localhost:5173') || t.url.includes('5173'))
      );
      if (page?.webSocketDebuggerUrl) return page;
    } catch {
      /* retry */
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error('Timed out waiting for Electron debugger on port 9222');
}

async function cdpEvaluate(wsUrl, expression) {
  const { default: WebSocket } = await import('ws');
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    let id = 0;
    const pending = new Map();

    ws.on('open', () => {
      const send = (method, params) => {
        const msgId = ++id;
        return new Promise((res, rej) => {
          pending.set(msgId, { res, rej });
          ws.send(JSON.stringify({ id: msgId, method, params }));
        });
      };

      (async () => {
        try {
          await send('Runtime.enable', {});
          const result = await send('Runtime.evaluate', {
            expression,
            awaitPromise: true,
            returnByValue: true,
          });
          ws.close();
          if (result.exceptionDetails) {
            reject(new Error(JSON.stringify(result.exceptionDetails)));
          } else {
            resolve(result.result?.value);
          }
        } catch (e) {
          ws.close();
          reject(e);
        }
      })();
    });

    ws.on('message', (raw) => {
      const msg = JSON.parse(String(raw));
      if (msg.id && pending.has(msg.id)) {
        const { res } = pending.get(msg.id);
        pending.delete(msg.id);
        res(msg.result);
      }
    });

    ws.on('error', reject);
  });
}

const expression = `
(async () => {
  const store = window.__BIM_STORE__;
  if (!store) throw new Error('__BIM_STORE__ not found — wait for app load');
  let state = store.getState();
  let client = state.clients.find(c => c.name === 'Demo Mechanical');
  if (!client) {
    state.addClient('Demo Mechanical');
    client = store.getState().clients.find(c => c.name === 'Demo Mechanical');
  }
  if (!client) throw new Error('Failed to add client');

  const names = [
    { name: 'Office Tower Phase 1', jobCode: '25-1001' },
    { name: 'Hospital Wing B', jobCode: '25-1002' },
    { name: 'Campus Utility Upgrade', jobCode: '25-1003' },
  ];
  for (const def of names) {
    state = store.getState();
    let project = state.projects.find(p => p.clientId === client.id && p.name === def.name);
    if (!project) {
      store.getState().addProject(client.id, def.name, {});
      project = store.getState().projects.find(p => p.clientId === client.id && p.name === def.name);
    }
    if (project && project.jobCode !== def.jobCode) {
      store.getState().updateProjectSettings(project.id, { jobCode: def.jobCode });
    }
  }

  state = store.getState();
  const campus = state.projects.find(p => p.clientId === client.id && p.name === 'Campus Utility Upgrade');
  if (campus) {
    const hasSpoolTask = state.tasks.some(
      t => t.projectId === campus.id && t.boardType === 'spooling' && (t.title || '').includes('TP007')
    );
    if (!hasSpoolTask) {
      store.getState().addTask({
        title: 'TP007 - Mechanical Pipe',
        description: 'SSv3 export target for package TP007',
        status: 'not-started',
        assigneeIds: [],
        clientId: client.id,
        projectId: campus.id,
        boardType: 'spooling',
        groupId: null,
        parentTaskId: null,
        priority: 0,
        dueDate: null,
        customFields: {},
      });
    }
    store.getState().setActiveClientId?.(client.id);
    if (typeof store.getState().setActiveProjectId === 'function') {
      store.getState().setActiveProjectId(campus.id);
    } else if (typeof store.getState().setNavigation === 'function') {
      store.getState().setNavigation({ activeClientId: client.id, activeProjectId: campus.id });
    }
  }

  state = store.getState();
  const projects = state.projects
    .filter(p => p.clientId === client.id)
    .map(p => p.name + (p.jobCode ? ' [' + p.jobCode + ']' : ''));
  const spoolTasks = state.tasks
    .filter(t => t.clientId === client.id && t.boardType === 'spooling')
    .map(t => t.title);
  return { ok: true, clientId: client.id, projects, spoolTasks };
})()
`;

const page = await waitForDebugger();
console.log('Attached to', page.url);
const result = await cdpEvaluate(page.webSocketDebuggerUrl, expression);
console.log(JSON.stringify(result, null, 2));

// Refresh CSV from sync file after store persists
await new Promise((r) => setTimeout(r, 1500));
try {
  const { spawnSync } = await import('node:child_process');
  spawnSync(process.execPath, ['scripts/export-projects-csv-from-sync.mjs'], {
    cwd: process.cwd(),
    stdio: 'inherit',
  });
} catch {
  /* optional */
}
