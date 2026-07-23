import { normalizeProject, type Client, type Project, type Task } from '../types';
import { formatTaskNumber } from './taskNumbers';

/** e.g. BKI-26-006 or DEMO-26-003 — client code must start with a letter */
const JOB_CODE_RE = /^([A-Za-z][A-Za-z0-9]*)-(\d{2})-(\d{1,4})$/;

/**
 * Client abbreviation for job codes.
 * "BKI" → BKI, "Demo Mechanical" → DEMO (first token, letters/digits, uppercased).
 */
export function deriveClientCode(clientName: string): string {
  const first = clientName.trim().split(/\s+/)[0] ?? '';
  const cleaned = first.replace(/[^A-Za-z0-9]/g, '').toUpperCase();
  return cleaned || 'CLIENT';
}

export function resolveClientCode(client: Pick<Client, 'name' | 'code'>): string {
  const explicit = client.code?.trim();
  if (explicit) {
    return explicit.replace(/[^A-Za-z0-9]/g, '').toUpperCase() || deriveClientCode(client.name);
  }
  return deriveClientCode(client.name);
}

export function jobCodeYear(date: Date = new Date()): string {
  return String(date.getFullYear()).slice(-2);
}

export function formatJobCode(clientCode: string, year: string, sequence: number): string {
  const code = clientCode.replace(/[^A-Za-z0-9]/g, '').toUpperCase() || 'CLIENT';
  const yy = year.replace(/\D/g, '').slice(-2).padStart(2, '0');
  const seq = Math.max(1, Math.floor(sequence));
  return `${code}-${yy}-${String(seq).padStart(3, '0')}`;
}

export function parseJobCode(
  value: string | null | undefined
): { clientCode: string; year: string; sequence: number } | null {
  const text = value?.trim() ?? '';
  const match = text.match(JOB_CODE_RE);
  if (!match) return null;
  return {
    clientCode: match[1]!.toUpperCase(),
    year: match[2]!,
    sequence: Number(match[3]),
  };
}

/** Pull a job code out of a project title like "(BKI-26-006) Databank ATL GT DCR". */
export function extractJobCodeFromText(text: string): string | null {
  const match = text.match(/\b([A-Za-z][A-Za-z0-9]*-\d{2}-\d{1,4})\b/);
  if (!match) return null;
  const parsed = parseJobCode(match[1]);
  return parsed
    ? formatJobCode(parsed.clientCode, parsed.year, parsed.sequence)
    : null;
}

/**
 * Next sequence for this client: max of (1) existing job-code sequences for the
 * same client code and (2) non-template project count, then +1.
 * Sequences are not reused when a project is deleted.
 */
export function nextJobSequenceForClient(
  client: Pick<Client, 'id' | 'name' | 'code'>,
  projects: Project[],
  clientCode = resolveClientCode(client)
): number {
  const codeUpper = clientCode.toUpperCase();
  let maxSeq = 0;

  for (const project of projects) {
    if (project.clientId !== client.id || project.isTemplate) continue;
    const parsed = parseJobCode(project.jobCode);
    if (parsed && parsed.clientCode === codeUpper) {
      maxSeq = Math.max(maxSeq, parsed.sequence);
    }
  }

  const projectCount = projects.filter(
    (project) => project.clientId === client.id && !project.isTemplate
  ).length;

  return Math.max(maxSeq, projectCount) + 1;
}

/** Allocate `{CLIENT}-{YY}-{NNN}` for a new project under this client. */
export function allocateJobCode(
  client: Pick<Client, 'id' | 'name' | 'code'>,
  projects: Project[],
  options?: { year?: string | number; date?: Date }
): string {
  const clientCode = resolveClientCode(client);
  const year =
    options?.year != null
      ? String(options.year).replace(/\D/g, '').slice(-2).padStart(2, '0')
      : jobCodeYear(options?.date ?? new Date());
  const sequence = nextJobSequenceForClient(client, projects, clientCode);
  return formatJobCode(clientCode, year, sequence);
}

export function normalizeClientRecord(client: Client): Client {
  const name = client.name?.trim() || client.name;
  const code = client.code?.trim()
    ? client.code.replace(/[^A-Za-z0-9]/g, '').toUpperCase()
    : deriveClientCode(name);
  return { ...client, name, code };
}

function taskSequenceHint(task: Task): number {
  const fromNumber = task.taskNumber?.match(/(\d+)\s*$/)?.[1];
  if (fromNumber) return Number(fromNumber);
  return task.priority ?? 0;
}

function legacyJobSortKey(jobCode: string | null | undefined): number {
  const digits = String(jobCode ?? '').replace(/\D/g, '');
  return digits ? Number(digits) : Number.MAX_SAFE_INTEGER;
}

/**
 * Backfill client codes, set job numbers (CLIENT-YY-NNN), and rewrite task numbers
 * to `{jobNumber}-0001`, … for existing portfolio data.
 */
export function migrateJobCodesAndTaskNumbers(
  clients: Client[],
  projects: Project[],
  tasks: Task[],
  options?: { date?: Date }
): { clients: Client[]; projects: Project[]; tasks: Task[] } {
  const date = options?.date ?? new Date();
  const year = jobCodeYear(date);

  const nextClients = clients.map(normalizeClientRecord);
  const clientById = new Map(nextClients.map((client) => [client.id, { ...client }]));

  const projectById = new Map<string, Project>();

  for (const project of projects) {
    if (project.isTemplate) {
      projectById.set(project.id, normalizeProject({ ...project, jobCode: project.jobCode ?? null }));
      continue;
    }

    const fromName = extractJobCodeFromText(project.name);
    if (fromName) {
      const parsed = parseJobCode(fromName)!;
      const client = clientById.get(project.clientId);
      if (client) {
        const updatedClient = { ...client, code: parsed.clientCode };
        clientById.set(client.id, updatedClient);
      }
      projectById.set(project.id, normalizeProject({ ...project, jobCode: fromName }));
      continue;
    }

    const existing = parseJobCode(project.jobCode);
    if (existing) {
      projectById.set(
        project.id,
        normalizeProject({
          ...project,
          jobCode: formatJobCode(existing.clientCode, existing.year, existing.sequence),
        })
      );
      continue;
    }

    // Needs allocation later
    projectById.set(project.id, normalizeProject({ ...project, jobCode: null }));
  }

  // Allocate missing job numbers per client (Demo Mechanical 25-1001 → DEMO-26-001, etc.)
  const needsAllocByClient = new Map<string, Project[]>();
  for (const project of projectById.values()) {
    if (project.isTemplate || project.jobCode) continue;
    const list = needsAllocByClient.get(project.clientId) ?? [];
    list.push(project);
    needsAllocByClient.set(project.clientId, list);
  }

  for (const [clientId, list] of needsAllocByClient) {
    const client = clientById.get(clientId);
    if (!client) continue;

    const sorted = [...list].sort((a, b) => {
      const aKey = legacyJobSortKey(projects.find((p) => p.id === a.id)?.jobCode);
      const bKey = legacyJobSortKey(projects.find((p) => p.id === b.id)?.jobCode);
      if (aKey !== bKey) return aKey - bKey;
      return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
    });

    let working = [...projectById.values()].filter(
      (p) => p.clientId === clientId && Boolean(p.jobCode)
    );

    for (const project of sorted) {
      const jobCode = allocateJobCode(client, working, { year, date });
      const updated = normalizeProject({ ...project, jobCode });
      projectById.set(project.id, updated);
      working = [...working, updated];
    }
  }

  let nextProjects = [...projectById.values()];

  // Rewrite task numbers under each job number
  const nextTasks = tasks.map((task) => ({ ...task }));
  const tasksByProject = new Map<string, number[]>();
  nextTasks.forEach((task, index) => {
    if (!task.projectId) return;
    const list = tasksByProject.get(task.projectId) ?? [];
    list.push(index);
    tasksByProject.set(task.projectId, list);
  });

  nextProjects = nextProjects.map((project) => {
    if (project.isTemplate || !project.jobCode) return project;
    const indexes = tasksByProject.get(project.id) ?? [];
    if (indexes.length === 0) {
      return normalizeProject({ ...project, nextTaskNumber: 1 });
    }

    const orderedIndexes = [...indexes].sort((ia, ib) => {
      const a = nextTasks[ia]!;
      const b = nextTasks[ib]!;
      const seq = taskSequenceHint(a) - taskSequenceHint(b);
      if (seq !== 0) return seq;
      return (a.createdAt || '').localeCompare(b.createdAt || '');
    });

    let seq = 1;
    for (const index of orderedIndexes) {
      nextTasks[index] = {
        ...nextTasks[index]!,
        taskNumber: formatTaskNumber(project, seq),
      };
      seq += 1;
    }
    return normalizeProject({ ...project, nextTaskNumber: seq });
  });

  return {
    clients: [...clientById.values()],
    projects: nextProjects,
    tasks: nextTasks,
  };
}
