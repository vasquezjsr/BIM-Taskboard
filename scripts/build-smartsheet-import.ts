import fs from 'fs';
import * as XLSX from 'xlsx';

const data = JSON.parse(
  fs.readFileSync('C:/Apps/BIM-Taskboard/scripts/_databank_tasks_export.json', 'utf8')
);

const boardLabel: Record<string, string> = {
  'project-managers': 'Project Setup',
  detailers: 'MP - Modeling/Coordination',
  deliverables: 'MP Deliverables',
  rfi: 'RFI Tracker',
  spooling: 'Spooling',
};

const aoa: (string | null)[][] = [
  ['Task Name', 'Board', 'Group', 'Parent Task', 'Status', 'Task Number', 'Due Date'],
];

for (const r of data.rows) {
  aoa.push([
    r.title,
    boardLabel[r.board] || r.board,
    r.group || '',
    r.parent || '',
    r.status || '',
    r.taskNumber || '',
    r.dueDate || '',
  ]);
}

const wb = XLSX.utils.book_new();
const ws = XLSX.utils.aoa_to_sheet(aoa);
XLSX.utils.book_append_sheet(wb, ws, 'Databank Tasks');

const outDir = 'C:/Apps/BIM-Taskboard/scripts';
const outXlsx = `${outDir}/BKI-26-006-Databank-Smartsheet-Import.xlsx`;
const outCsv = `${outDir}/BKI-26-006-Databank-Smartsheet-Import.csv`;
const dlXlsx = 'C:/Users/vasqu/Downloads/BKI-26-006-Databank-Smartsheet-Import.xlsx';
const dlCsv = 'C:/Users/vasqu/Downloads/BKI-26-006-Databank-Smartsheet-Import.csv';

XLSX.writeFile(wb, outXlsx);
fs.writeFileSync(outCsv, XLSX.utils.sheet_to_csv(ws));
fs.copyFileSync(outXlsx, dlXlsx);
fs.copyFileSync(outCsv, dlCsv);

console.log(`Wrote ${aoa.length - 1} rows`);
console.log(outXlsx);
console.log(dlXlsx);
