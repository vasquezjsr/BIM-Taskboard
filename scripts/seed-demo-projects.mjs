import fs from 'node:fs';
import crypto from 'node:crypto';

const syncPath = '.dev-store-sync.json';
const outer = JSON.parse(fs.readFileSync(syncPath, 'utf8'));
const persist = JSON.parse(outer.state);
const state = persist.state;

const MAIN_SECTION_BOARDS = [
  'project-managers',
  'rfi',
  'documents',
  'detailers',
  'deliverables',
  'spooling',
];
const SECTION_LABELS = {
  'project-managers': 'Project Management',
  rfi: 'RFI',
  documents: 'Documents',
  detailers: 'Detailers',
  deliverables: 'Deliverables',
  spooling: 'Spooling',
};

function uuid() {
  return crypto.randomUUID();
}

function defaultProjectFields() {
  return {
    detailerIds: [],
    supportIds: [],
    pmIds: [],
    revitYear: null,
    modelType: null,
    buildingLevels: [],
    activeLevels: [],
    isTemplate: false,
    billingType: 'lump-sum',
    budgetHours: null,
    totalHoursSpent: null,
    projectStartDate: null,
    projectEndDate: null,
    jobCode: null,
    nextTaskNumber: 1,
  };
}

function emptyBoards(clientId, projectId) {
  return MAIN_SECTION_BOARDS.map((sectionBoardType, index) => ({
    id: uuid(),
    name: SECTION_LABELS[sectionBoardType],
    clientId,
    projectId,
    boardType: 'main',
    tier: 'section',
    parentId: null,
    sectionBoardType,
    sortOrder: index,
  }));
}

function escapeCsv(value) {
  const s = String(value ?? '');
  if (/[",\r\n]/.test(s)) return `"${s.replace(/"/g, '""')}"`;
  return s;
}

const DEMO_CLIENT_NAME = 'Demo Mechanical';

const oldDemo = (state.clients || []).find((c) => c.name === DEMO_CLIENT_NAME);
if (oldDemo) {
  const oldId = oldDemo.id;
  state.clients = state.clients.filter((c) => c.id !== oldId);
  state.projects = (state.projects || []).filter((p) => p.clientId !== oldId);
  state.taskGroups = (state.taskGroups || []).filter((g) => g.clientId !== oldId);
  state.tasks = (state.tasks || []).filter((t) => t.clientId !== oldId);
  state.customBoards = (state.customBoards || []).filter((b) => b.clientId !== oldId);
}

const clientId = uuid();
const client = { id: clientId, name: DEMO_CLIENT_NAME };
state.clients = [...(state.clients || []), client];

const projectDefs = [
  { name: 'Office Tower Phase 1', jobCode: '25-1001' },
  { name: 'Hospital Wing B', jobCode: '25-1002' },
  { name: 'Campus Utility Upgrade', jobCode: '25-1003' },
];

const newProjects = [];
const newGroups = [];
for (const def of projectDefs) {
  const projectId = uuid();
  newProjects.push({
    id: projectId,
    name: def.name,
    clientId,
    ...defaultProjectFields(),
    jobCode: def.jobCode,
  });
  newGroups.push(...emptyBoards(clientId, projectId));
}

state.projects = [...(state.projects || []), ...newProjects];
state.taskGroups = [...(state.taskGroups || []), ...newGroups];
state.activeClientId = clientId;
state.activeProjectId = newProjects[0].id;
state.activeBoardType = 'main';
state.activeMainTab = 'clients';

persist.state = state;
const updatedAt = Date.now();
fs.writeFileSync(syncPath, JSON.stringify({ updatedAt, state: JSON.stringify(persist) }));

const csvRows = [
  'ProjectId,ProjectName,JobCode,ClientId,ClientName,IsTemplate,BillingType,RevitYear',
];
const clientsById = Object.fromEntries(state.clients.map((c) => [c.id, c.name]));
for (const p of state.projects) {
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

console.log(`Client: ${client.name}`);
for (const p of newProjects) {
  console.log(`  - ${p.name} (${p.jobCode})`);
}
console.log('Store + CSV updated.');
