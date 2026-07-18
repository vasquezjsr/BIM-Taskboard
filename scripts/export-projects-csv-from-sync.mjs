import fs from 'node:fs';

const syncPath = '.dev-store-sync.json';
const outer = JSON.parse(fs.readFileSync(syncPath, 'utf8'));
const persist = JSON.parse(outer.state);
const state = persist.state;

function escapeCsv(value) {
  const s = String(value ?? '');
  if (/[",\r\n]/.test(s)) return `"${s.replace(/"/g, '""')}"`;
  return s;
}

const csvRows = [
  'ProjectId,ProjectName,JobCode,ClientId,ClientName,IsTemplate,BillingType,RevitYear',
];
const clientsById = Object.fromEntries((state.clients || []).map((c) => [c.id, c.name]));
for (const p of state.projects || []) {
  csvRows.push(
    [
      p.id,
      p.name,
      p.jobCode || '',
      p.clientId,
      clientsById[p.clientId] || '',
      p.isTemplate ? 'true' : 'false',
      p.billingType || 'lump-sum',
      p.revitYear || '',
    ]
      .map(escapeCsv)
      .join(',')
  );
}
const csvDir = 'Spooling Savant Version 3 (Exports)/Boardroom';
fs.mkdirSync(csvDir, { recursive: true });
fs.writeFileSync(`${csvDir}/Boardroom-Projects.csv`, `${csvRows.join('\r\n')}\r\n`);
console.log(`Wrote CSV with ${(state.projects || []).length} projects`);
