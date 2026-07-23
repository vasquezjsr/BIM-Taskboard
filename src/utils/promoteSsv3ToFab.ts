import { v4 as uuid } from 'uuid';
import type { Project, Task, TaskAttachment, TaskGroup } from '../types';
import {
  PREMADE_MATERIAL_COLUMN_ID,
  PREMADE_TRADE_COLUMN_ID,
} from '../data/premadeSheetColumns';
import type { BoardTaskStatusesMap, ProjectBoardTaskStatusesMap } from './taskStatuses';
import { applyAutoAssigneesToTask } from './taskAssigneesAuto';
import { nextTaskNumberForProject } from './taskNumbers';
import {
  formatAssemblySheetDescription,
  packageTitle,
  parseSsv3Packages,
  spoolingTaskHasSsv3Export,
  stripSsv3ExportCustomFields,
  SSV3_FIELD,
  SSV3_KIND_ASSEMBLY,
  SSV3_KIND_PACKAGE,
  type BoardroomPackageAssembly,
  type BoardroomPackageBatch,
  type BoardroomPackageManifest,
} from './boardroomPackageImport';
import {
  DETAILERS_MIRROR_FIELD,
  DETAILERS_MIRROR_GROUP_FIELD,
  detailersMirrorGroupId,
  stampDetailersSpoolingMirror,
  stickyDetailersGroupId,
} from './detailersSpoolingHandoff';

/** Copy Trade / Material from the main Spooling package onto nested assemblies. */
export function tradeMaterialFieldsFromPackage(pkg: Task): Record<string, string | null> {
  return {
    [PREMADE_TRADE_COLUMN_ID]: pkg.customFields?.[PREMADE_TRADE_COLUMN_ID] ?? null,
    [PREMADE_MATERIAL_COLUMN_ID]: pkg.customFields?.[PREMADE_MATERIAL_COLUMN_ID] ?? null,
  };
}

/** Walk to the top-level package task for an assembly. */
function packageRootForTask(tasks: Task[], task: Task): Task {
  const byId = new Map(tasks.map((entry) => [entry.id, entry]));
  let current = task;
  const seen = new Set<string>();
  while (current.parentTaskId && !seen.has(current.id)) {
    seen.add(current.id);
    const parent = byId.get(current.parentTaskId);
    if (!parent) break;
    current = parent;
  }
  return current;
}

/**
 * Ensure every SSv3 assembly inherits Trade / Material from its main package task.
 * Safe to run repeatedly (idempotent).
 */
export function syncAssemblyTradeMaterialFromPackageRoots(tasks: Task[]): Task[] {
  return tasks.map((task) => {
    if (task.customFields?.[SSV3_FIELD.kind] !== SSV3_KIND_ASSEMBLY) return task;
    if (!task.parentTaskId) return task;
    const root = packageRootForTask(tasks, task);
    if (root.id === task.id) return task;
    const tradeMaterial = tradeMaterialFieldsFromPackage(root);
    const currentTrade = task.customFields?.[PREMADE_TRADE_COLUMN_ID] ?? null;
    const currentMaterial = task.customFields?.[PREMADE_MATERIAL_COLUMN_ID] ?? null;
    if (
      currentTrade === tradeMaterial[PREMADE_TRADE_COLUMN_ID] &&
      currentMaterial === tradeMaterial[PREMADE_MATERIAL_COLUMN_ID]
    ) {
      return task;
    }
    return {
      ...task,
      customFields: {
        ...task.customFields,
        ...tradeMaterial,
      },
    };
  });
}
type TaskTreeContext = {
  projects: Project[];
  tasks: Task[];
  taskGroups: TaskGroup[];
  boardTaskStatuses: BoardTaskStatusesMap;
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap;
};

export type AttachSsv3Result = {
  projects: Project[];
  tasks: Task[];
  packagesUpserted: number;
  assembliesUpserted: number;
};

export type PromoteSsv3Result = AttachSsv3Result & {
  fabPackageTaskIds: string[];
  moved: boolean;
};

export type ClearSsv3ExportResult = {
  tasks: Task[];
  taskAttachments: TaskAttachment[];
  cleared: boolean;
  removedTaskCount: number;
  removedAttachmentCount: number;
};

function isBoardroomAbsAttachment(attachment: TaskAttachment): boolean {
  const current = attachment.versions.find((v) => v.id === attachment.currentVersionId);
  return Boolean(current?.storageId?.startsWith('boardroom-abs:'));
}

/**
 * Remove SSv3 package/assembly children and boardroom-abs report attachments from a
 * Spooling (or pre-promote) export root. Keeps the root task and manual uploads.
 */
export function clearSsv3ExportFromSpoolingTask(
  rootTask: Task,
  tasks: Task[],
  taskAttachments: TaskAttachment[]
): ClearSsv3ExportResult {
  const treeIds = collectDescendantIds(tasks, rootTask.id);
  const removeIds = new Set<string>();

  for (const task of tasks) {
    if (task.id === rootTask.id || !treeIds.has(task.id)) continue;
    const kind = task.customFields?.[SSV3_FIELD.kind];
    if (kind === SSV3_KIND_PACKAGE || kind === SSV3_KIND_ASSEMBLY) {
      removeIds.add(task.id);
    }
  }

  // Drop any nested rows under removed package nodes (defensive).
  let grew = true;
  while (grew) {
    grew = false;
    for (const task of tasks) {
      if (task.parentTaskId && removeIds.has(task.parentTaskId) && !removeIds.has(task.id)) {
        removeIds.add(task.id);
        grew = true;
      }
    }
  }

  const hadExportMeta = spoolingTaskHasSsv3Export(rootTask);
  const boardroomOnRoot = taskAttachments.filter(
    (attachment) => attachment.taskId === rootTask.id && isBoardroomAbsAttachment(attachment)
  );
  const attachmentsOnRemoved = taskAttachments.filter((attachment) =>
    removeIds.has(attachment.taskId)
  );
  const removedAttachmentCount = boardroomOnRoot.length + attachmentsOnRemoved.length;
  const cleared = hadExportMeta || removeIds.size > 0 || boardroomOnRoot.length > 0;

  if (!cleared) {
    return {
      tasks,
      taskAttachments,
      cleared: false,
      removedTaskCount: 0,
      removedAttachmentCount: 0,
    };
  }

  const nextTasks = tasks
    .filter((task) => !removeIds.has(task.id))
    .map((task) =>
      task.id === rootTask.id
        ? { ...task, customFields: stripSsv3ExportCustomFields(task.customFields) }
        : task
    );

  const nextAttachments = taskAttachments.filter((attachment) => {
    if (removeIds.has(attachment.taskId)) return false;
    if (attachment.taskId === rootTask.id && isBoardroomAbsAttachment(attachment)) return false;
    return true;
  });

  return {
    tasks: nextTasks,
    taskAttachments: nextAttachments,
    cleared: true,
    removedTaskCount: removeIds.size,
    removedAttachmentCount,
  };
}

function allocateNumber(
  projects: Project[],
  projectId: string
): { projects: Project[]; taskNumber: string | null } {
  const projectIndex = projects.findIndex((entry) => entry.id === projectId);
  if (projectIndex < 0) return { projects, taskNumber: null };
  const current = projects[projectIndex]!;
  const next = nextTaskNumberForProject(current);
  const updated = projects.map((entry, index) =>
    index === projectIndex ? { ...entry, nextTaskNumber: next.nextTaskNumber } : entry
  );
  return { projects: updated, taskNumber: next.taskNumber };
}

function makeTask(
  partial: Omit<Task, 'customFields' | 'durationFields' | 'assigneesLocked'> & {
    customFields?: Record<string, string | null>;
  }
): Task {
  return {
    ...partial,
    customFields: partial.customFields ?? {},
    durationFields: {},
    assigneesLocked: false,
  };
}

function collectDescendantIds(tasks: Task[], rootId: string): Set<string> {
  const ids = new Set<string>([rootId]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const task of tasks) {
      if (task.parentTaskId && ids.has(task.parentTaskId) && !ids.has(task.id)) {
        ids.add(task.id);
        grew = true;
      }
    }
  }
  return ids;
}

function upsertAssemblyUnderParent(
  tasks: Task[],
  projects: Project[],
  ctx: TaskTreeContext,
  parentId: string,
  project: Project,
  boardType: Task['boardType'],
  sPackage: string,
  assembly: BoardroomPackageAssembly,
  status: string,
  packageRoot: Task
): { tasks: Task[]; projects: Project[]; created: boolean } {
  const revitId = String(assembly.revitElementId ?? '');
  const tradeMaterial = tradeMaterialFieldsFromPackage(packageRoot);
  const existing = tasks.find(
    (task) =>
      task.parentTaskId === parentId &&
      task.customFields?.[SSV3_FIELD.kind] === SSV3_KIND_ASSEMBLY &&
      task.customFields?.[SSV3_FIELD.revitElementId] === revitId
  );

  if (existing) {
    const description = formatAssemblySheetDescription(
      assembly.sheetName,
      assembly.sheetNumber,
      assembly.name
    );
    return {
      projects,
      created: false,
      tasks: tasks.map((task) =>
        task.id === existing.id
          ? {
              ...task,
              title: assembly.name,
              description: description || task.description,
              boardType,
              customFields: {
                ...task.customFields,
                [SSV3_FIELD.kind]: SSV3_KIND_ASSEMBLY,
                [SSV3_FIELD.package]: sPackage,
                [SSV3_FIELD.revitElementId]: revitId,
                [SSV3_FIELD.qr]: assembly.qr ?? '',
                [SSV3_FIELD.sheetName]: assembly.sheetName ?? '',
                [SSV3_FIELD.sheetNumber]: assembly.sheetNumber ?? '',
                ...tradeMaterial,
              },
            }
          : task
      ),
    };
  }

  const allocated = allocateNumber(projects, project.id);
  const description = formatAssemblySheetDescription(
    assembly.sheetName,
    assembly.sheetNumber,
    assembly.name
  );
  const created = makeTask({
    id: uuid(),
    taskNumber: allocated.taskNumber,
    title: assembly.name,
    description,
    status,
    assigneeIds: [],
    clientId: project.clientId,
    projectId: project.id,
    boardType,
    groupId: null,
    parentTaskId: parentId,
    priority: 0,
    dueDate: null,
    customFields: {
      [SSV3_FIELD.kind]: SSV3_KIND_ASSEMBLY,
      [SSV3_FIELD.package]: sPackage,
      [SSV3_FIELD.revitElementId]: revitId,
      [SSV3_FIELD.qr]: assembly.qr ?? '',
      [SSV3_FIELD.sheetName]: assembly.sheetName ?? '',
      [SSV3_FIELD.sheetNumber]: assembly.sheetNumber ?? '',
      ...tradeMaterial,
    },
    createdAt: new Date().toISOString(),
  });

  return {
    projects: allocated.projects,
    created: true,
    tasks: [
      ...tasks,
      applyAutoAssigneesToTask(
        created,
        allocated.projects,
        ctx.taskGroups,
        ctx.boardTaskStatuses,
        ctx.projectBoardTaskStatuses
      ),
    ],
  };
}

/** Nest package/assembly children under the chosen Spooling (or Fab) export task. */
export function attachSsv3HierarchyToTask(
  rootTask: Task,
  batches: BoardroomPackageBatch[],
  ctx: TaskTreeContext
): AttachSsv3Result {
  const project = ctx.projects.find((p) => p.id === rootTask.projectId);
  if (!project) {
    return { projects: ctx.projects, tasks: ctx.tasks, packagesUpserted: 0, assembliesUpserted: 0 };
  }

  let projects = ctx.projects;
  let tasks = [...ctx.tasks];
  let packagesUpserted = 0;
  let assembliesUpserted = 0;
  const boardType = rootTask.boardType;
  const childStatus = rootTask.status === 'ready-for-fab' ? 'ready-for-fab' : rootTask.status;

  const useIntermediatePackages = batches.length > 1;

  for (const batch of batches) {
    const sPackage = batch.sPackage;
    let packageParentId = rootTask.id;

    if (useIntermediatePackages) {
      const title = packageTitle(sPackage);
      const existingPackage = tasks.find(
        (task) =>
          task.parentTaskId === rootTask.id &&
          task.customFields?.[SSV3_FIELD.kind] === SSV3_KIND_PACKAGE &&
          task.customFields?.[SSV3_FIELD.package] === sPackage
      );

      if (existingPackage) {
        packageParentId = existingPackage.id;
        tasks = tasks.map((task) =>
          task.id === existingPackage.id
            ? {
                ...task,
                title,
                description: `SSv3 package ${sPackage}`,
                boardType,
                customFields: {
                  ...task.customFields,
                  [SSV3_FIELD.kind]: SSV3_KIND_PACKAGE,
                  [SSV3_FIELD.package]: sPackage,
                },
              }
            : task
        );
      } else {
        const allocated = allocateNumber(projects, project.id);
        projects = allocated.projects;
        packageParentId = uuid();
        const created = makeTask({
          id: packageParentId,
          taskNumber: allocated.taskNumber,
          title,
          description: `SSv3 package ${sPackage}`,
          status: childStatus,
          assigneeIds: [],
          clientId: project.clientId,
          projectId: project.id,
          boardType,
          groupId: null,
          parentTaskId: rootTask.id,
          priority: 0,
          dueDate: null,
          customFields: {
            [SSV3_FIELD.kind]: SSV3_KIND_PACKAGE,
            [SSV3_FIELD.package]: sPackage,
          },
          createdAt: new Date().toISOString(),
        });
        tasks.push(
          applyAutoAssigneesToTask(
            created,
            projects,
            ctx.taskGroups,
            ctx.boardTaskStatuses,
            ctx.projectBoardTaskStatuses
          )
        );
      }
      packagesUpserted += 1;
    } else {
      packagesUpserted += 1;
    }

    for (const assembly of batch.assemblies ?? []) {
      const result = upsertAssemblyUnderParent(
        tasks,
        projects,
        ctx,
        packageParentId,
        project,
        boardType,
        sPackage,
        assembly,
        childStatus,
        rootTask
      );
      tasks = result.tasks;
      projects = result.projects;
      assembliesUpserted += 1;
    }
  }

  return {
    projects,
    tasks: syncAssemblyTradeMaterialFromPackageRoots(tasks),
    packagesUpserted,
    assembliesUpserted,
  };
}

export function attachSsv3HierarchyFromManifest(
  rootTask: Task,
  manifest: BoardroomPackageManifest,
  ctx: TaskTreeContext
): AttachSsv3Result {
  return attachSsv3HierarchyToTask(rootTask, manifest.packages, ctx);
}

/** Remove duplicate Fab package roots (old promote model) that match this export. */
function removeOrphanFabPackageTrees(
  tasks: Task[],
  rootTask: Task,
  packages: string[]
): Task[] {
  const packageSet = new Set(packages);
  const orphanRoots = tasks.filter(
    (task) =>
      task.id !== rootTask.id &&
      task.boardType === 'fab' &&
      !task.parentTaskId &&
      task.projectId === rootTask.projectId &&
      task.customFields?.[SSV3_FIELD.kind] === SSV3_KIND_PACKAGE &&
      packageSet.has(task.customFields?.[SSV3_FIELD.package] ?? '')
  );
  if (orphanRoots.length === 0) return tasks;

  const removeIds = new Set<string>();
  for (const orphan of orphanRoots) {
    for (const id of collectDescendantIds(tasks, orphan.id)) {
      removeIds.add(id);
    }
  }
  return tasks.filter((task) => !removeIds.has(task.id));
}

/**
 * Move the Spooling export task + descendants onto the Fab board (status → queued).
 * Does not create a parallel package tree.
 */
export function promoteSsv3SpoolingTaskToFab(
  spoolingTask: Task,
  ctx: TaskTreeContext
): PromoteSsv3Result {
  if (!spoolingTaskHasSsv3Export(spoolingTask) || !spoolingTask.projectId) {
    return {
      projects: ctx.projects,
      tasks: ctx.tasks,
      packagesUpserted: 0,
      assembliesUpserted: 0,
      fabPackageTaskIds: [],
      moved: false,
    };
  }

  const batches = parseSsv3Packages(spoolingTask);
  const attached = attachSsv3HierarchyToTask(spoolingTask, batches, ctx);
  let tasks = removeOrphanFabPackageTrees(
    attached.tasks,
    spoolingTask,
    batches.map((b) => b.sPackage)
  );
  const treeIds = collectDescendantIds(tasks, spoolingTask.id);

  tasks = tasks.map((task) => {
    if (!treeIds.has(task.id)) return task;
    const isRoot = task.id === spoolingTask.id;
    // Keep Detailers origin group + mirror so the package remains on Detailers
    // and demote can remirror cleanly.
    const sticky =
      isRoot ? stickyDetailersGroupId(task) : detailersMirrorGroupId(task);
    const fields: Record<string, string | null> = { ...(task.customFields ?? {}) };
    if (isRoot) {
      if (sticky) {
        fields[DETAILERS_MIRROR_FIELD] = '1';
        fields[DETAILERS_MIRROR_GROUP_FIELD] = sticky;
      }
      fields[SSV3_FIELD.kind] = SSV3_KIND_PACKAGE;
      fields[SSV3_FIELD.fabPackageTaskId] = spoolingTask.id;
    }
    return {
      ...task,
      boardType: 'fab',
      // Root keeps Detailers level placement for the Detailers original copy.
      groupId: isRoot ? (sticky ?? task.groupId) : null,
      status: 'queued',
      customFields: fields,
    };
  });

  return {
    projects: attached.projects,
    tasks,
    packagesUpserted: attached.packagesUpserted,
    assembliesUpserted: attached.assembliesUpserted,
    fabPackageTaskIds: [spoolingTask.id],
    moved: true,
  };
}

/**
 * Move a Fab package tree back to the Spooling board (status → Spool In Progress).
 * Unlocks SSv3 export replace/clear so the package can be reworked and re-exported.
 */
export function demoteSsv3FabTaskToSpooling(
  fabTask: Task,
  ctx: TaskTreeContext
): PromoteSsv3Result {
  if (
    fabTask.boardType !== 'fab' ||
    fabTask.parentTaskId ||
    !spoolingTaskHasSsv3Export(fabTask)
  ) {
    return {
      projects: ctx.projects,
      tasks: ctx.tasks,
      packagesUpserted: 0,
      assembliesUpserted: 0,
      fabPackageTaskIds: [],
      moved: false,
    };
  }

  const treeIds = collectDescendantIds(ctx.tasks, fabTask.id);
  const stickyGroup = stickyDetailersGroupId(fabTask);
  const tasks = ctx.tasks.map((task) => {
    if (!treeIds.has(task.id)) return task;
    const customFields = { ...(task.customFields ?? {}) };
    if (task.id === fabTask.id) {
      delete customFields[SSV3_FIELD.fabPackageTaskId];
      const stamped = stampDetailersSpoolingMirror(
        { ...task, customFields },
        stickyGroup ?? detailersMirrorGroupId(task)
      );
      return {
        ...task,
        boardType: 'spooling' as const,
        groupId: stamped.groupId,
        status: 'spool-in-progress',
        customFields: stamped.customFields,
      };
    }
    return {
      ...task,
      boardType: 'spooling' as const,
      groupId: null,
      status: 'spool-in-progress',
      customFields,
    };
  });

  return {
    projects: ctx.projects,
    tasks,
    packagesUpserted: 0,
    assembliesUpserted: 0,
    fabPackageTaskIds: [],
    moved: true,
  };
}

/**
 * Move a Fab package tree onto the Shipping board (status → Staging).
 * Removes it from the Fabrication / Shop workstation.
 */
export function promoteSsv3FabTaskToShipping(
  fabTask: Task,
  ctx: TaskTreeContext
): PromoteSsv3Result {
  if (
    fabTask.boardType !== 'fab' ||
    fabTask.parentTaskId ||
    !spoolingTaskHasSsv3Export(fabTask)
  ) {
    return {
      projects: ctx.projects,
      tasks: ctx.tasks,
      packagesUpserted: 0,
      assembliesUpserted: 0,
      fabPackageTaskIds: [],
      moved: false,
    };
  }

  const treeIds = collectDescendantIds(ctx.tasks, fabTask.id);
  const tasks = ctx.tasks.map((task) => {
    if (!treeIds.has(task.id)) return task;
    if (task.id === fabTask.id) {
      return {
        ...task,
        boardType: 'shipping' as const,
        groupId: null,
        status: 'staging',
      };
    }
    // Assemblies keep fab Complete/In Fab marks; track shipping lane separately.
    const customFields = {
      ...(task.customFields ?? {}),
      [SSV3_FIELD.shippingStatus]:
        task.customFields?.[SSV3_FIELD.shippingStatus] ?? 'staging',
    };
    return {
      ...task,
      boardType: 'shipping' as const,
      groupId: null,
      customFields,
    };
  });

  return {
    projects: ctx.projects,
    tasks,
    packagesUpserted: 0,
    assembliesUpserted: 0,
    fabPackageTaskIds: [],
    moved: true,
  };
}

/**
 * Move a Shipping package tree onto the Field board (status → Material On Site).
 * Removes it from the Shipping Dashboard.
 */
export function promoteSsv3ShippingTaskToField(
  shippingTask: Task,
  ctx: TaskTreeContext
): PromoteSsv3Result {
  if (
    shippingTask.boardType !== 'shipping' ||
    shippingTask.parentTaskId ||
    !spoolingTaskHasSsv3Export(shippingTask)
  ) {
    return {
      projects: ctx.projects,
      tasks: ctx.tasks,
      packagesUpserted: 0,
      assembliesUpserted: 0,
      fabPackageTaskIds: [],
      moved: false,
    };
  }

  const treeIds = collectDescendantIds(ctx.tasks, shippingTask.id);
  const tasks = ctx.tasks.map((task) => {
    if (!treeIds.has(task.id)) return task;
    if (task.id === shippingTask.id) {
      return {
        ...task,
        boardType: 'field' as const,
        groupId: null,
        status: 'material-on-site',
      };
    }
    const customFields = {
      ...(task.customFields ?? {}),
      [SSV3_FIELD.fieldStatus]:
        task.customFields?.[SSV3_FIELD.fieldStatus] ?? 'not-started',
      [SSV3_FIELD.shippingStatus]:
        task.customFields?.[SSV3_FIELD.shippingStatus] ?? 'received-field',
    };
    return {
      ...task,
      boardType: 'field' as const,
      groupId: null,
      customFields,
    };
  });

  return {
    projects: ctx.projects,
    tasks,
    packagesUpserted: 0,
    assembliesUpserted: 0,
    fabPackageTaskIds: [],
    moved: true,
  };
}

/**
 * Move a Shipping package tree back to Fabrication (status → Rework).
 * Use when shop/fab changes are needed after the package already left Fab.
 */
export function demoteSsv3ShippingTaskToFab(
  shippingTask: Task,
  ctx: TaskTreeContext
): PromoteSsv3Result {
  if (
    shippingTask.boardType !== 'shipping' ||
    shippingTask.parentTaskId ||
    !spoolingTaskHasSsv3Export(shippingTask)
  ) {
    return {
      projects: ctx.projects,
      tasks: ctx.tasks,
      packagesUpserted: 0,
      assembliesUpserted: 0,
      fabPackageTaskIds: [],
      moved: false,
    };
  }

  const treeIds = collectDescendantIds(ctx.tasks, shippingTask.id);
  const tasks = ctx.tasks.map((task) => {
    if (!treeIds.has(task.id)) return task;
    if (task.id === shippingTask.id) {
      return {
        ...task,
        boardType: 'fab' as const,
        groupId: null,
        status: 'rework',
      };
    }
    return {
      ...task,
      boardType: 'fab' as const,
      groupId: null,
    };
  });

  return {
    projects: ctx.projects,
    tasks,
    packagesUpserted: 0,
    assembliesUpserted: 0,
    fabPackageTaskIds: [],
    moved: true,
  };
}

/**
 * Move a Shipping package tree back to the Spooling board (status → Spool In Progress).
 * Unlocks export replace/clear so sheets can be reworked and re-exported.
 */
export function demoteSsv3ShippingTaskToSpooling(
  shippingTask: Task,
  ctx: TaskTreeContext
): PromoteSsv3Result {
  if (
    shippingTask.boardType !== 'shipping' ||
    shippingTask.parentTaskId ||
    !spoolingTaskHasSsv3Export(shippingTask)
  ) {
    return {
      projects: ctx.projects,
      tasks: ctx.tasks,
      packagesUpserted: 0,
      assembliesUpserted: 0,
      fabPackageTaskIds: [],
      moved: false,
    };
  }

  const treeIds = collectDescendantIds(ctx.tasks, shippingTask.id);
  const stickyGroup = stickyDetailersGroupId(shippingTask);
  const tasks = ctx.tasks.map((task) => {
    if (!treeIds.has(task.id)) return task;
    const customFields = { ...(task.customFields ?? {}) };
    if (task.id === shippingTask.id) {
      delete customFields[SSV3_FIELD.fabPackageTaskId];
      const stamped = stampDetailersSpoolingMirror(
        { ...task, customFields },
        stickyGroup ?? detailersMirrorGroupId(task)
      );
      return {
        ...task,
        boardType: 'spooling' as const,
        groupId: stamped.groupId,
        status: 'spool-in-progress',
        customFields: stamped.customFields,
      };
    }
    return {
      ...task,
      boardType: 'spooling' as const,
      groupId: null,
      status: 'spool-in-progress',
      customFields,
    };
  });

  return {
    projects: ctx.projects,
    tasks,
    packagesUpserted: 0,
    assembliesUpserted: 0,
    fabPackageTaskIds: [],
    moved: true,
  };
}
