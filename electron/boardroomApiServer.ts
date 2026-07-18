import http from 'http';
import type { IncomingMessage, ServerResponse } from 'http';

export const BOARDROOM_API_PORT = 17321;
export const BOARDROOM_API_HOST = '127.0.0.1';

export type BoardroomApiProject = {
  id: string;
  name: string;
  jobCode: string;
  clientId: string;
  clientName: string;
  isTemplate: boolean;
  billingType: string;
  revitYear: string;
};

export type BoardroomApiTask = {
  id: string;
  taskNumber: string | null;
  title: string;
  status: string;
  projectId: string | null;
  boardType: string | null;
  groupId: string | null;
  parentTaskId: string | null;
  assigneeIds: string[];
  dueDate: string | null;
  customFields: Record<string, string>;
  /** True when this Spooling task already has an attached SSv3 export. */
  hasSsv3Export: boolean;
  createdAt: string;
};

export type BoardroomApiSnapshot = {
  clients: { id: string; name: string }[];
  projects: BoardroomApiProject[];
  tasks: BoardroomApiTask[];
  publishedAt: string;
};

let snapshot: BoardroomApiSnapshot | null = null;
let server: http.Server | null = null;

function sendJson(res: ServerResponse, status: number, body: unknown) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(payload),
    'Access-Control-Allow-Origin': '*',
  });
  res.end(payload);
}

function parseUrl(req: IncomingMessage) {
  try {
    return new URL(req.url ?? '/', `http://${BOARDROOM_API_HOST}:${BOARDROOM_API_PORT}`);
  } catch {
    return null;
  }
}

function handleRequest(req: IncomingMessage, res: ServerResponse) {
  const remote = req.socket.remoteAddress ?? '';
  if (remote !== '127.0.0.1' && remote !== '::1' && remote !== '::ffff:127.0.0.1') {
    sendJson(res, 403, { error: 'Loopback only' });
    return;
  }

  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET, OPTIONS',
      'Access-Control-Allow-Headers': 'Content-Type',
    });
    res.end();
    return;
  }

  if (req.method !== 'GET') {
    sendJson(res, 405, { error: 'Method not allowed' });
    return;
  }

  const url = parseUrl(req);
  if (!url) {
    sendJson(res, 400, { error: 'Bad request' });
    return;
  }

  if (url.pathname === '/health') {
    sendJson(res, 200, {
      ok: true,
      app: 'BIM Boardroom',
      port: BOARDROOM_API_PORT,
      snapshotReady: Boolean(snapshot),
      publishedAt: snapshot?.publishedAt ?? null,
    });
    return;
  }

  if (!snapshot) {
    sendJson(res, 503, {
      error: 'Boardroom snapshot not ready. Open BIM Boardroom and wait for data to load.',
    });
    return;
  }

  if (url.pathname === '/v1/projects') {
    const includeTemplates = url.searchParams.get('includeTemplates') === 'true';
    const projects = snapshot.projects.filter((p) => includeTemplates || !p.isTemplate);
    sendJson(res, 200, projects);
    return;
  }

  const tasksMatch = url.pathname.match(/^\/v1\/projects\/([^/]+)\/tasks$/);
  if (tasksMatch) {
    const projectId = decodeURIComponent(tasksMatch[1]!);
    const boardType = url.searchParams.get('boardType') ?? 'spooling';
    const tasks = snapshot.tasks.filter(
      (task) =>
        task.projectId === projectId &&
        task.boardType === boardType &&
        !task.parentTaskId
    );
    sendJson(res, 200, tasks);
    return;
  }

  const projectMatch = url.pathname.match(/^\/v1\/projects\/([^/]+)$/);
  if (projectMatch) {
    const projectId = decodeURIComponent(projectMatch[1]!);
    const project = snapshot.projects.find((p) => p.id === projectId);
    if (!project) {
      sendJson(res, 404, { error: 'Project not found' });
      return;
    }
    sendJson(res, 200, project);
    return;
  }

  sendJson(res, 404, { error: 'Not found' });
}

export function setBoardroomApiSnapshot(next: BoardroomApiSnapshot | null) {
  snapshot = next;
}

export function startBoardroomApiServer(): Promise<{ ok: true; port: number } | { ok: false; error: string }> {
  if (server) {
    return Promise.resolve({ ok: true, port: BOARDROOM_API_PORT });
  }

  return new Promise((resolve) => {
    const next = http.createServer(handleRequest);
    next.on('error', (error) => {
      resolve({
        ok: false,
        error: error instanceof Error ? error.message : String(error),
      });
    });
    next.listen(BOARDROOM_API_PORT, BOARDROOM_API_HOST, () => {
      server = next;
      resolve({ ok: true, port: BOARDROOM_API_PORT });
    });
  });
}

export function stopBoardroomApiServer() {
  if (!server) return;
  try {
    server.close();
  } catch {
    /* ignore */
  }
  server = null;
}
