import type { Client, Project } from '../types';

const CSV_HEADERS = [
  'ProjectId',
  'ProjectName',
  'JobCode',
  'ClientId',
  'ClientName',
  'IsTemplate',
  'BillingType',
  'RevitYear',
] as const;

function escapeCsv(value: string): string {
  if (/[",\r\n]/.test(value)) {
    return `"${value.replace(/"/g, '""')}"`;
  }
  return value;
}

/** Build a CSV of Boardroom projects for Spooling Savant project pickers. */
export function buildBoardroomProjectsCsv(
  clients: Client[],
  projects: Project[],
  options?: { includeTemplates?: boolean }
): string {
  const includeTemplates = options?.includeTemplates ?? true;
  const clientNameById = new Map(clients.map((client) => [client.id, client.name]));

  const rows = projects
    .filter((project) => includeTemplates || !project.isTemplate)
    .slice()
    .sort((a, b) => {
      const clientA = clientNameById.get(a.clientId) ?? '';
      const clientB = clientNameById.get(b.clientId) ?? '';
      const byClient = clientA.localeCompare(clientB, undefined, { sensitivity: 'base' });
      if (byClient !== 0) return byClient;
      return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
    });

  const lines = [CSV_HEADERS.join(',')];
  for (const project of rows) {
    lines.push(
      [
        escapeCsv(project.id),
        escapeCsv(project.name),
        escapeCsv(project.jobCode ?? ''),
        escapeCsv(project.clientId),
        escapeCsv(clientNameById.get(project.clientId) ?? ''),
        project.isTemplate ? 'true' : 'false',
        escapeCsv(project.billingType),
        escapeCsv(project.revitYear ?? ''),
      ].join(',')
    );
  }

  return `${lines.join('\r\n')}\r\n`;
}

export function downloadBoardroomProjectsCsv(
  clients: Client[],
  projects: Project[],
  options?: { includeTemplates?: boolean; filename?: string }
): void {
  const csv = buildBoardroomProjectsCsv(clients, projects, options);
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = options?.filename ?? 'Boardroom-Projects.csv';
  anchor.click();
  URL.revokeObjectURL(url);
}
