import type { Project, Task } from '../types';

export const SSV3_KIND_PACKAGE = 'package';
export const SSV3_KIND_ASSEMBLY = 'assembly';

export const SSV3_FIELD = {
  kind: 'ssv3Kind',
  package: 'ssPackage',
  exportFolder: 'ssv3ExportFolder',
  exportedAt: 'ssv3ExportedAt',
  files: 'ssv3Files',
  packages: 'ssv3Packages',
  revitElementId: 'revitElementId',
  qr: 'ssv3Qr',
  sheetName: 'ssv3SheetName',
  sheetNumber: 'ssv3SheetNumber',
  fabPackageTaskId: 'ssv3FabPackageTaskId',
  /** Fab Shop Super assigns whole package to this Dept Lead (employee id). */
  deptLeadId: 'ssv3DeptLeadId',
  /** Dept Lead assigns whole package to this worker (employee id). */
  workerId: 'ssv3WorkerId',
  /** Assembly shipping lane (staging → complete) independent of fab Complete. */
  shippingStatus: 'ssv3ShippingStatus',
  /** Assembly field install lane (independent of package field status). */
  fieldStatus: 'ssv3FieldStatus',
  /** Estimated arrival date (YYYY-MM-DD) for package or assembly. */
  estimatedArrival: 'ssv3EstimatedArrival',
  /** When the package/assembly left Staging for transit (ISO datetime). */
  shippedAt: 'ssv3ShippedAt',
} as const;

export interface BoardroomPackageFileRef {
  fileName: string;
  type: string;
}

export interface BoardroomPackageAssembly {
  revitElementId: number | string;
  name: string;
  sheetName?: string;
  sheetNumber?: string;
  qr?: string;
}

export interface BoardroomPackageBatch {
  sPackage: string;
  assemblies: BoardroomPackageAssembly[];
}

export interface BoardroomPackageManifest {
  schema: string;
  exportedAt?: string;
  targets?: string[];
  boardroomProject: {
    id: string;
    name?: string;
    clientName?: string;
    jobCode?: string;
  };
  boardroomTask: {
    id: string;
    taskNumber?: string;
    title?: string;
  };
  packages: BoardroomPackageBatch[];
  files?: BoardroomPackageFileRef[];
}

export interface BoardroomPackageImportResult {
  projectId: string;
  spoolingTaskId: string;
  packagesAttached: number;
  promotedToFab: boolean;
  packagesUpserted: number;
  assembliesUpserted: number;
}

export function parseBoardroomPackageManifest(raw: unknown): BoardroomPackageManifest {
  if (!raw || typeof raw !== 'object') {
    throw new Error('Invalid Boardroom package: not an object.');
  }
  const data = raw as Record<string, unknown>;
  if (data.schema !== 'bim-boardroom-package-v1') {
    throw new Error(
      `Unsupported package schema "${String(data.schema ?? '')}". Expected bim-boardroom-package-v1.`
    );
  }
  const project = data.boardroomProject as BoardroomPackageManifest['boardroomProject'] | undefined;
  if (!project?.id || typeof project.id !== 'string') {
    throw new Error('Package is missing boardroomProject.id.');
  }
  const task = data.boardroomTask as BoardroomPackageManifest['boardroomTask'] | undefined;
  if (!task?.id || typeof task.id !== 'string') {
    throw new Error(
      'Package is missing boardroomTask.id. Re-export from SSv3 and pick a Spooling board task.'
    );
  }
  if (!Array.isArray(data.packages) || data.packages.length === 0) {
    throw new Error('Package contains no S-Package batches.');
  }

  const packages: BoardroomPackageBatch[] = data.packages.map((entry, index) => {
    const batch = entry as Record<string, unknown>;
    const sPackage = String(batch.sPackage ?? '').trim() || `(Package ${index + 1})`;
    const assembliesRaw = Array.isArray(batch.assemblies) ? batch.assemblies : [];
    const assemblies: BoardroomPackageAssembly[] = assembliesRaw.map((a) => {
      const row = a as Record<string, unknown>;
      return {
        revitElementId: row.revitElementId as number | string,
        name: String(row.name ?? '').trim() || `Assembly ${String(row.revitElementId ?? '')}`,
        sheetName: row.sheetName != null ? String(row.sheetName) : undefined,
        sheetNumber: row.sheetNumber != null ? String(row.sheetNumber) : undefined,
        qr: row.qr != null ? String(row.qr) : undefined,
      };
    });
    return { sPackage, assemblies };
  });

  const files: BoardroomPackageFileRef[] = Array.isArray(data.files)
    ? data.files
        .map((f) => {
          const row = f as Record<string, unknown>;
          return {
            fileName: String(row.fileName ?? ''),
            type: String(row.type ?? ''),
          };
        })
        .filter((f) => f.fileName)
    : [];

  return {
    schema: 'bim-boardroom-package-v1',
    exportedAt: data.exportedAt != null ? String(data.exportedAt) : undefined,
    targets: Array.isArray(data.targets) ? data.targets.map(String) : undefined,
    boardroomProject: {
      id: project.id,
      name: project.name,
      clientName: project.clientName,
      jobCode: project.jobCode,
    },
    boardroomTask: {
      id: task.id,
      taskNumber: task.taskNumber != null ? String(task.taskNumber) : undefined,
      title: task.title != null ? String(task.title) : undefined,
    },
    packages,
    files,
  };
}

export function findProjectForManifest(
  projects: Project[],
  manifest: BoardroomPackageManifest
): Project | null {
  return projects.find((project) => project.id === manifest.boardroomProject.id) ?? null;
}

export function findSpoolingTaskForManifest(
  tasks: Task[],
  manifest: BoardroomPackageManifest
): Task | null {
  const task = tasks.find((entry) => entry.id === manifest.boardroomTask.id) ?? null;
  if (!task) return null;
  // Allow re-attach after Ready for Fab moved the task onto Fab (or later Shipping).
  if (task.boardType !== 'spooling' && task.boardType !== 'fab' && task.boardType !== 'shipping' && task.boardType !== 'field') {
    return null;
  }
  if (task.projectId !== manifest.boardroomProject.id) return null;
  return task;
}

export function isSsv3PackageTask(task: Task): boolean {
  return (
    task.boardType === 'fab' &&
    !task.parentTaskId &&
    task.customFields?.[SSV3_FIELD.kind] === SSV3_KIND_PACKAGE &&
    Boolean(task.customFields?.[SSV3_FIELD.package])
  );
}

/** SSv3 package root on the Shipping board (after Ready for Shipping). */
export function isSsv3ShippingPackageTask(task: Task): boolean {
  return (
    task.boardType === 'shipping' &&
    !task.parentTaskId &&
    task.customFields?.[SSV3_FIELD.kind] === SSV3_KIND_PACKAGE &&
    Boolean(task.customFields?.[SSV3_FIELD.package])
  );
}

/** SSv3 package root on the Field board (after Received by Field). */
export function isSsv3FieldPackageTask(task: Task): boolean {
  return (
    task.boardType === 'field' &&
    !task.parentTaskId &&
    task.customFields?.[SSV3_FIELD.kind] === SSV3_KIND_PACKAGE &&
    Boolean(task.customFields?.[SSV3_FIELD.package])
  );
}

/** Package roots on Fab, Shipping, or Field (weld log / export consumers). */
export function isSsv3TrackedPackageTask(task: Task): boolean {
  return (
    isSsv3PackageTask(task) ||
    isSsv3ShippingPackageTask(task) ||
    isSsv3FieldPackageTask(task)
  );
}

export function isSsv3AssemblyTask(task: Task): boolean {
  return task.customFields?.[SSV3_FIELD.kind] === SSV3_KIND_ASSEMBLY;
}

export function spoolingTaskHasSsv3Export(task: Task): boolean {
  return Boolean(
    task.customFields?.[SSV3_FIELD.package] || task.customFields?.[SSV3_FIELD.packages]
  );
}

/** Ready for Fab / already on Fab, Shipping, or Field — wipe and replace are blocked. */
export function isSsv3ExportLocked(task: Task): boolean {
  return (
    task.boardType === 'fab' ||
    task.boardType === 'shipping' ||
    task.boardType === 'field' ||
    task.status === 'ready-for-fab'
  );
}

export function stripSsv3ExportCustomFields(
  existing: Record<string, string | null> | undefined
): Record<string, string | null> {
  const next: Record<string, string | null> = { ...(existing ?? {}) };
  delete next[SSV3_FIELD.kind];
  delete next[SSV3_FIELD.package];
  delete next[SSV3_FIELD.packages];
  delete next[SSV3_FIELD.files];
  delete next[SSV3_FIELD.exportFolder];
  delete next[SSV3_FIELD.exportedAt];
  delete next[SSV3_FIELD.fabPackageTaskId];
  return next;
}

export function getSsv3PackageKey(task: Task): string | null {
  if (!isSsv3PackageTask(task) || !task.projectId) return null;
  return `${task.projectId}::${task.customFields?.[SSV3_FIELD.package] ?? ''}`;
}

export function parseSsv3Files(task: Task): BoardroomPackageFileRef[] {
  const raw = task.customFields?.[SSV3_FIELD.files];
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as BoardroomPackageFileRef[];
    return Array.isArray(parsed)
      ? parsed.filter((f) => f?.fileName && isBoardroomPackageAttachmentFile(f.fileName))
      : [];
  } catch {
    return [];
  }
}

export function parseSsv3Packages(task: Task): BoardroomPackageBatch[] {
  const raw = task.customFields?.[SSV3_FIELD.packages];
  if (!raw) {
    const single = task.customFields?.[SSV3_FIELD.package];
    if (!single) return [];
    return [{ sPackage: single, assemblies: [] }];
  }
  try {
    const parsed = JSON.parse(raw) as BoardroomPackageBatch[];
    return Array.isArray(parsed) ? parsed.filter((p) => p?.sPackage) : [];
  } catch {
    return [];
  }
}

export function packageTitle(sPackage: string): string {
  return `Package ${sPackage}`;
}

/** Boardroom description for an assembly: Sheet Name-Sheet Number. */
export function formatAssemblySheetDescription(
  sheetName?: string | null,
  sheetNumber?: string | null,
  fallbackName?: string | null
): string {
  const name = (sheetName ?? '').trim();
  const number = (sheetNumber ?? '').trim();
  if (name && number) return `${name}-${number}`;
  if (name) return name;
  if (number) return number;
  return (fallbackName ?? '').trim();
}

/** Report files that belong on the task; excludes catalog / manifest / backup junk. */
export function isBoardroomPackageAttachmentFile(fileName: string): boolean {
  const name = fileName.trim().toLowerCase();
  if (!name) return false;
  if (name === 'boardroom-package.json') return false;
  if (name.endsWith('.bak') || name.includes('.bak-')) return false;
  if (name.startsWith('piping specification catalog')) return false;
  return true;
}

export function filterBoardroomPackageAttachmentFiles(
  files: BoardroomPackageFileRef[] | undefined
): BoardroomPackageFileRef[] {
  return (files ?? []).filter((file) => isBoardroomPackageAttachmentFile(file.fileName));
}

export function buildSpoolingExportCustomFields(
  existing: Record<string, string | null> | undefined,
  manifest: BoardroomPackageManifest,
  exportFolder: string
): Record<string, string | null> {
  const primary = manifest.packages[0]?.sPackage ?? '';
  return {
    ...existing,
    [SSV3_FIELD.kind]: SSV3_KIND_PACKAGE,
    [SSV3_FIELD.package]: primary,
    [SSV3_FIELD.packages]: JSON.stringify(manifest.packages),
    [SSV3_FIELD.exportFolder]: exportFolder,
    [SSV3_FIELD.exportedAt]: manifest.exportedAt ?? new Date().toISOString(),
    [SSV3_FIELD.files]: JSON.stringify(filterBoardroomPackageAttachmentFiles(manifest.files)),
  };
}
