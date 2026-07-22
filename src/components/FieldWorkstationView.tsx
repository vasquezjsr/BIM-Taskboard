import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { employeeInitials } from '../data/employees';
import { useStore } from '../store/useStore';
import type { Task } from '../types';
import {
  isSsv3AssemblyTask,
  parseSsv3Files,
  SSV3_FIELD,
  type BoardroomPackageFileRef,
} from '../utils/boardroomPackageImport';
import {
  assembliesForFieldDashboardView,
  canEmployeeSeeFieldProject,
  getAssemblyShippingLane,
  inboundShippingLabel,
  isPackageInboundFromShipping,
  isPackageVisibleOnFieldDashboard,
  isShippingStatusVisibleToField,
} from '../utils/fieldWorkstationAccess';
import {
  formatQrDisplayLabel,
  formatShipDateShort,
  getEstimatedArrival,
  getShippedAt,
  getTaskQr,
  isAssemblyReleasedForShipView,
  PARTIAL_STILL_IN_FAB_COLOR,
  PARTIAL_STILL_IN_FAB_LABEL,
  PARTIAL_STILL_IN_FAB_STATUS_ID,
} from '../utils/shippingTracking';
import {
  extractPdfPageBlobUrl,
  isSpoolsCombinedPdf,
  resolveAssemblyPdfPageIndex,
} from '../utils/extractPdfPage';
import {
  canEditWeldLog,
  canViewDashboard,
  canViewWeldLogDashboard,
} from '../utils/permissions';
import {
  FIELD_WORKFLOW_LANES,
  getBoardTaskStatuses,
} from '../utils/taskStatuses';
import {
  arrayBufferToBase64,
  base64ToArrayBuffer,
  filterWeldLogRowsForAssembly,
  findWeldLogFileName,
  isFieldWeldRow,
  parseWeldLogWorkbook,
  serializeWeldLogWorkbook,
  todayLocalIsoDate,
  toggleWeldLogFill,
  type WeldLogFillField,
  type WeldLogRow,
} from '../utils/weldLogWorkbook';
import { CleanPdfViewer } from './CleanPdfViewer';
import { FabWeldLogGrid } from './FabWeldLogGrid';
import { PackageCollabBar } from './PackageCollabBar';
import fabStyles from './FabWorkstationView.module.css';
import styles from './FieldWorkstationView.module.css';

type FieldLaneId = (typeof FIELD_WORKFLOW_LANES)[number];

function isFieldLaneId(value: string | null | undefined): value is FieldLaneId {
  return (FIELD_WORKFLOW_LANES as readonly string[]).includes(value ?? '');
}

function nextFieldLaneId(status: string): FieldLaneId | null {
  const idx = FIELD_WORKFLOW_LANES.findIndex((lane) => lane === status);
  if (idx < 0 || idx >= FIELD_WORKFLOW_LANES.length - 1) return null;
  return FIELD_WORKFLOW_LANES[idx + 1];
}

function getAssemblyFieldStatus(task: Task): FieldLaneId | 'not-started' {
  const raw = task.customFields?.[SSV3_FIELD.fieldStatus];
  if (isFieldLaneId(raw)) return raw;
  if (raw === 'not-started') return 'not-started';
  return 'not-started';
}

function joinPath(folder: string, fileName: string): string {
  const base = folder.replace(/[/\\]+$/, '');
  const sep = folder.includes('\\') ? '\\' : '/';
  return `${base}${sep}${fileName}`;
}

function formatScanTime(iso: string | null): string {
  if (!iso) return 'Never';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

function findSheetFileForAssembly(
  files: BoardroomPackageFileRef[],
  assembly: Task
): string | null {
  const candidates = [
    assembly.customFields?.[SSV3_FIELD.sheetNumber],
    assembly.customFields?.[SSV3_FIELD.sheetName],
    assembly.title,
  ]
    .map((value) => (value ?? '').trim().toLowerCase())
    .filter(Boolean);

  const pdfs = files.filter(
    (file) => file.type === 'pdf' || file.fileName.toLowerCase().endsWith('.pdf')
  );
  for (const candidate of candidates) {
    const match = pdfs.find((file) => file.fileName.toLowerCase().includes(candidate));
    if (match) return match.fileName;
  }
  const combined = pdfs.find((file) => isSpoolsCombinedPdf(file.fileName));
  return combined?.fileName ?? pdfs[0]?.fileName ?? null;
}

function base64ToObjectUrl(base64: string, mimeType: string): string {
  const bytes = base64ToArrayBuffer(base64);
  const blob = new Blob([bytes], { type: mimeType });
  return URL.createObjectURL(blob);
}

function isAssemblyInstallDone(status: string): boolean {
  return status === 'complete' || status === 'final-inspection';
}

function assemblyInstallProgress(
  assemblies: Task[]
): { done: number; total: number } {
  const total = assemblies.length;
  const done = assemblies.filter((task) =>
    isAssemblyInstallDone(getAssemblyFieldStatus(task))
  ).length;
  return { done, total };
}

export function FieldWorkstationView() {
  const tasks = useStore((s) => s.tasks);
  const projects = useStore((s) => s.projects);
  const clients = useStore((s) => s.clients);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const updateTask = useStore((s) => s.updateTask);
  const fieldFocusProjectId = useStore((s) => s.fieldFocusProjectId);
  const setFieldFocusProjectId = useStore((s) => s.setFieldFocusProjectId);
  const currentUserId = useStore((s) => s.currentUserId);
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);

  const currentUser = employees.find((e) => e.id === currentUserId) ?? null;
  const canViewField = canViewDashboard(
    'field',
    currentUserId,
    employees,
    employeePermissions
  );
  const canEditAllWelds = canEditWeldLog(currentUserId, employees, employeePermissions);
  const canViewWeldLog = canViewWeldLogDashboard(
    currentUserId,
    employees,
    employeePermissions
  );
  const canTapFillFieldWelds =
    Boolean(currentUser) && (canEditAllWelds || canViewField || canViewWeldLog);

  const [laneFilter, setLaneFilter] = useState<
    FieldLaneId | 'active' | 'inbound' | 'all'
  >('active');
  const [selectedPackageId, setSelectedPackageId] = useState<string | null>(null);
  const [selectedAssemblyId, setSelectedAssemblyId] = useState<string | null>(null);
  const [projectFilterId, setProjectFilterId] = useState<string | null>(
    () => fieldFocusProjectId
  );

  useEffect(() => {
    if (fieldFocusProjectId) setProjectFilterId(fieldFocusProjectId);
  }, [fieldFocusProjectId]);

  const setProjectFilter = useCallback(
    (next: string | null) => {
      setProjectFilterId(next);
      setFieldFocusProjectId(next);
    },
    [setFieldFocusProjectId]
  );
  const [showFieldWelds, setShowFieldWelds] = useState(false);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [weldRows, setWeldRows] = useState<WeldLogRow[]>([]);
  const [weldBusy, setWeldBusy] = useState(false);
  const [weldError, setWeldError] = useState<string | null>(null);
  const [weldSaveMessage, setWeldSaveMessage] = useState<string | null>(null);
  const previewBlobUrlRef = useRef<string | null>(null);
  const weldFilePathRef = useRef<string | null>(null);

  const fieldStatusesFor = useCallback(
    (task: Task) =>
      getBoardTaskStatuses(
        'field',
        boardTaskStatuses,
        task.projectId,
        projectBoardTaskStatuses
      ),
    [boardTaskStatuses, projectBoardTaskStatuses]
  );

  const allPackageTasks = useMemo(() => {
    let list = tasks.filter((pkg) => isPackageVisibleOnFieldDashboard(pkg, tasks));
    list = list.filter((pkg) => {
      const project = projects.find((entry) => entry.id === pkg.projectId);
      return canEmployeeSeeFieldProject(
        currentUserId,
        project,
        employees,
        employeePermissions
      );
    });
    const filterId = projectFilterId;
    if (filterId) {
      list = list.filter((task) => task.projectId === filterId);
    }
    return list.sort((a, b) => (a.title ?? '').localeCompare(b.title ?? ''));
  }, [
    tasks,
    projects,
    currentUserId,
    employees,
    employeePermissions,
    projectFilterId,
  ]);

  const inboundCount = useMemo(
    () => allPackageTasks.filter(isPackageInboundFromShipping).length,
    [allPackageTasks]
  );

  const laneCounts = useMemo(() => {
    const counts: Record<string, number> = Object.fromEntries(
      FIELD_WORKFLOW_LANES.map((lane) => [lane, 0])
    );
    for (const pkg of allPackageTasks) {
      if (isPackageInboundFromShipping(pkg)) continue;
      const key = isFieldLaneId(pkg.status) ? pkg.status : 'material-on-site';
      counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
  }, [allPackageTasks]);

  const packageTasks = useMemo(() => {
    if (laneFilter === 'all') return allPackageTasks;
    if (laneFilter === 'inbound') {
      return allPackageTasks.filter(isPackageInboundFromShipping);
    }
    if (laneFilter === 'active') {
      return allPackageTasks.filter((pkg) => {
        if (isPackageInboundFromShipping(pkg)) return true;
        return pkg.status !== 'complete';
      });
    }
    return allPackageTasks.filter(
      (pkg) => !isPackageInboundFromShipping(pkg) && pkg.status === laneFilter
    );
  }, [allPackageTasks, laneFilter]);

  useEffect(() => {
    if (!selectedPackageId) return;
    if (!packageTasks.some((pkg) => pkg.id === selectedPackageId)) {
      setSelectedPackageId(packageTasks[0]?.id ?? null);
    }
  }, [packageTasks, selectedPackageId]);

  useEffect(() => {
    if (selectedPackageId) return;
    if (packageTasks[0]) setSelectedPackageId(packageTasks[0].id);
  }, [packageTasks, selectedPackageId]);

  const selectedPackage =
    packageTasks.find((pkg) => pkg.id === selectedPackageId) ??
    allPackageTasks.find((pkg) => pkg.id === selectedPackageId) ??
    null;

  const assemblies = useMemo(() => {
    if (!selectedPackage) return [];
    // Show every assembly (still-in-Fab rows are grayed); Field install stays on In Transit+.
    return tasks
      .filter((task) => isSsv3AssemblyTask(task) && task.parentTaskId === selectedPackage.id)
      .sort((a, b) => (a.title ?? '').localeCompare(b.title ?? ''));
  }, [tasks, selectedPackage]);

  const selectedInbound = selectedPackage
    ? isPackageInboundFromShipping(selectedPackage)
    : false;

  const selectedInboundAssemblies = useMemo(() => {
    if (!selectedPackage) return [];
    return assembliesForFieldDashboardView(selectedPackage, assemblies);
  }, [selectedPackage, assemblies]);

  const selectedInboundLane = selectedPackage
    ? inboundShippingLabel(selectedPackage, selectedInboundAssemblies)
    : null;

  const selectedPartialFab = Boolean(
    selectedPackage && selectedInbound && selectedPackage.boardType === 'fab'
  );

  useEffect(() => {
    if (!selectedPackage) {
      setSelectedAssemblyId(null);
      return;
    }
    if (assemblies.some((assembly) => assembly.id === selectedAssemblyId)) {
      const current = assemblies.find((assembly) => assembly.id === selectedAssemblyId);
      if (
        current &&
        selectedInbound &&
        !isAssemblyReleasedForShipView(current, selectedPackage)
      ) {
        // Prefer a released / inbound assembly when current pick is still in Fab.
        const preferred =
          selectedInboundAssemblies[0] ??
          assemblies.find((assembly) =>
            isAssemblyReleasedForShipView(assembly, selectedPackage)
          ) ??
          null;
        setSelectedAssemblyId(preferred?.id ?? null);
      }
      return;
    }
    const preferred =
      selectedInboundAssemblies[0] ??
      assemblies.find((assembly) =>
        !selectedInbound || isAssemblyReleasedForShipView(assembly, selectedPackage)
      ) ??
      assemblies[0] ??
      null;
    setSelectedAssemblyId(preferred?.id ?? null);
  }, [
    selectedPackage,
    assemblies,
    selectedAssemblyId,
    selectedInbound,
    selectedInboundAssemblies,
  ]);

  const selectedAssembly =
    assemblies.find((assembly) => assembly.id === selectedAssemblyId) ?? null;

  const exportFiles = useMemo(() => {
    if (!selectedPackage) return [];
    return parseSsv3Files(selectedPackage);
  }, [selectedPackage]);

  const spoolPdfFiles = useMemo(
    () =>
      exportFiles.filter((file) => file.fileName.toLowerCase().endsWith('.pdf')),
    [exportFiles]
  );

  const projectOptions = useMemo(() => {
    const visible = tasks.filter((pkg) => isPackageVisibleOnFieldDashboard(pkg, tasks));
    const ids = new Set(
      visible
        .filter((task) => {
          const project = projects.find((entry) => entry.id === task.projectId);
          return canEmployeeSeeFieldProject(
            currentUserId,
            project,
            employees,
            employeePermissions
          );
        })
        .map((task) => task.projectId)
        .filter(Boolean)
    );
    return projects
      .filter((project) => ids.has(project.id))
      .sort((a, b) => (a.jobCode ?? a.name).localeCompare(b.jobCode ?? b.name));
  }, [tasks, projects, currentUserId, employees, employeePermissions]);

  const shippingStatusesFor = useCallback(
    (task: Task) =>
      getBoardTaskStatuses(
        'shipping',
        boardTaskStatuses,
        task.projectId,
        projectBoardTaskStatuses
      ),
    [boardTaskStatuses, projectBoardTaskStatuses]
  );

  const shippingLaneLabel = useCallback(
    (statusId: string, task: Task) =>
      shippingStatusesFor(task).find((status) => status.id === statusId)?.label ?? statusId,
    [shippingStatusesFor]
  );

  const projectLabel = useCallback(
    (projectId: string | null) => {
      if (!projectId) return '—';
      const project = projects.find((entry) => entry.id === projectId);
      if (!project) return '—';
      const client = clients.find((entry) => entry.id === project.clientId);
      const code = project.jobCode ? `${project.jobCode} · ` : '';
      return `${code}${client?.name ?? 'Client'} / ${project.name}`;
    },
    [projects, clients]
  );

  const statusMeta = useCallback(
    (statusId: string, task: Task) => {
      const statuses = fieldStatusesFor(task);
      const match = statuses.find((status) => status.id === statusId);
      return {
        label: match?.label ?? statusId,
        color: match?.color ?? '#94a3b8',
      };
    },
    [fieldStatusesFor]
  );

  const defaultFieldStatuses = useMemo(
    () => getBoardTaskStatuses('field', boardTaskStatuses, null, projectBoardTaskStatuses),
    [boardTaskStatuses, projectBoardTaskStatuses]
  );

  const laneChipLabel = useCallback(
    (laneId: string) =>
      defaultFieldStatuses.find((status) => status.id === laneId)?.label ?? laneId,
    [defaultFieldStatuses]
  );

  const clearPreview = useCallback(() => {
    if (previewBlobUrlRef.current) {
      URL.revokeObjectURL(previewBlobUrlRef.current);
      previewBlobUrlRef.current = null;
    }
    setPreviewUrl(null);
    setPreviewError(null);
  }, []);

  const clearWeldLog = useCallback(() => {
    setWeldRows([]);
    setWeldError(null);
    setWeldBusy(false);
    setWeldSaveMessage(null);
    weldFilePathRef.current = null;
  }, []);

  const loadWeldLog = useCallback(async () => {
    if (!selectedPackage) {
      clearWeldLog();
      return;
    }
    const folder = selectedPackage.customFields?.[SSV3_FIELD.exportFolder];
    const fileName = findWeldLogFileName(exportFiles.map((file) => file.fileName));
    if (!folder || folder.startsWith('(browser') || !fileName) {
      clearWeldLog();
      if (!fileName) setWeldError('No Weld Log.xlsx found in this package export.');
      else setWeldError('Weld log requires Electron with the original export folder path.');
      return;
    }
    const api = window.electronAPI;
    if (!api?.readFilePreview) {
      setWeldError('Weld log requires the BIM Boardroom desktop app.');
      return;
    }
    const fullPath = joinPath(folder, fileName);
    weldFilePathRef.current = fullPath;
    setWeldBusy(true);
    setWeldError(null);
    setWeldSaveMessage(null);
    try {
      const result = await api.readFilePreview(fullPath);
      if (!result.ok) {
        setWeldError(result.error);
        setWeldRows([]);
        return;
      }
      setWeldRows(parseWeldLogWorkbook(base64ToArrayBuffer(result.base64)));
    } catch (err) {
      setWeldError(err instanceof Error ? err.message : String(err));
      setWeldRows([]);
    } finally {
      setWeldBusy(false);
    }
  }, [selectedPackage, exportFiles, clearWeldLog]);

  useEffect(() => {
    void loadWeldLog();
  }, [loadWeldLog]);

  const loadAssemblySheet = useCallback(
    async (assembly: Task) => {
      clearPreview();
      const folder = selectedPackage?.customFields?.[SSV3_FIELD.exportFolder];
      if (!folder || folder.startsWith('(browser')) {
        setPreviewError('Spool sheet preview requires Electron with the export folder path.');
        return;
      }
      const api = window.electronAPI;
      if (!api?.readFilePreview) {
        setPreviewError('Sheet preview requires the BIM Boardroom desktop app.');
        return;
      }
      const fileName = findSheetFileForAssembly(exportFiles, assembly);
      if (!fileName) {
        setPreviewError('No spool sheet PDF found for this assembly.');
        return;
      }
      const fullPath = joinPath(folder, fileName);
      setPreviewBusy(true);
      try {
        if (isSpoolsCombinedPdf(fileName)) {
          const pageIndex = resolveAssemblyPdfPageIndex(assemblies, assembly);
          if (pageIndex == null) {
            setPreviewError('Could not map this assembly to a sheet page.');
            return;
          }
          const result = await api.readFilePreview(fullPath);
          if (!result.ok) {
            setPreviewError(result.error);
            return;
          }
          const extracted = await extractPdfPageBlobUrl(
            base64ToArrayBuffer(result.base64),
            pageIndex
          );
          previewBlobUrlRef.current = extracted.url;
          setPreviewUrl(extracted.url);
          return;
        }
        const result = await api.readFilePreview(fullPath);
        if (!result.ok) {
          setPreviewError(result.error);
          return;
        }
        const url = base64ToObjectUrl(result.base64, 'application/pdf');
        previewBlobUrlRef.current = url;
        setPreviewUrl(url);
      } catch (err) {
        setPreviewError(err instanceof Error ? err.message : String(err));
      } finally {
        setPreviewBusy(false);
      }
    },
    [assemblies, clearPreview, exportFiles, selectedPackage]
  );

  useEffect(() => {
    if (!selectedAssembly) {
      clearPreview();
      return;
    }
    void loadAssemblySheet(selectedAssembly);
  }, [selectedAssembly, loadAssemblySheet, clearPreview]);

  useEffect(() => () => clearPreview(), [clearPreview]);

  const visibleFieldWelds = useMemo(() => {
    if (!selectedAssembly) return [];
    return filterWeldLogRowsForAssembly(weldRows, selectedAssembly.title).filter(isFieldWeldRow);
  }, [weldRows, selectedAssembly]);

  useEffect(() => {
    if (visibleFieldWelds.length === 0) setShowFieldWelds(false);
  }, [visibleFieldWelds.length]);

  const setAssemblyFieldStatus = useCallback(
    (assembly: Task, status: string) => {
      updateTask(assembly.id, {
        customFields: {
          ...(assembly.customFields ?? {}),
          [SSV3_FIELD.fieldStatus]: status,
        },
      });
    },
    [updateTask]
  );

  const advancePackage = useCallback(
    (pkg: Task) => {
      const next = nextFieldLaneId(pkg.status);
      if (!next) return;
      updateTask(pkg.id, { status: next });
    },
    [updateTask]
  );

  const welderFillValues = useMemo(() => {
    if (!currentUser) return { date: todayLocalIsoDate(), welderId: '', initials: '' };
    return {
      date: todayLocalIsoDate(),
      welderId: currentUser.name,
      initials: employeeInitials(currentUser.name),
    };
  }, [currentUser]);

  const persistWeldRows = useCallback(async (nextRows: WeldLogRow[]) => {
    const path = weldFilePathRef.current;
    const api = window.electronAPI;
    if (!path || !api?.writeFileBytes) {
      setWeldError('Cannot save weld log (missing export path or write API).');
      return;
    }
    setWeldBusy(true);
    setWeldSaveMessage(null);
    try {
      const bytes = serializeWeldLogWorkbook(nextRows);
      const result = await api.writeFileBytes(path, arrayBufferToBase64(bytes));
      if (!result.ok) {
        setWeldError(result.error);
        return;
      }
      setWeldRows(nextRows);
      setWeldSaveMessage('Field weld log saved.');
    } catch (err) {
      setWeldError(err instanceof Error ? err.message : String(err));
    } finally {
      setWeldBusy(false);
    }
  }, []);

  const handleTapFill = useCallback(
    (visibleIndex: number, field: WeldLogFillField) => {
      if (!canTapFillFieldWelds || !currentUser || !selectedAssembly) return;
      const target = visibleFieldWelds[visibleIndex];
      if (!target || (!canEditAllWelds && !isFieldWeldRow(target))) return;
      const fullIndex = weldRows.findIndex(
        (row) =>
          row.weldNumber === target.weldNumber &&
          row.assembly === target.assembly &&
          row.weldType === target.weldType
      );
      if (fullIndex < 0) return;
      const updated = [...weldRows];
      updated[fullIndex] = toggleWeldLogFill(updated[fullIndex], field, welderFillValues);
      void persistWeldRows(updated);
    },
    [
      canTapFillFieldWelds,
      canEditAllWelds,
      currentUser,
      selectedAssembly,
      visibleFieldWelds,
      weldRows,
      welderFillValues,
      persistWeldRows,
    ]
  );

  const selectedAdvance = selectedPackage ? nextFieldLaneId(selectedPackage.status) : null;
  const selectedAdvanceLabel = selectedAdvance ? laneChipLabel(selectedAdvance) : null;
  const selectedInstallProgress = assemblyInstallProgress(
    selectedInbound ? selectedInboundAssemblies : assemblies
  );

  return (
    <div className={`${fabStyles.wrapper} ${styles.fieldRoot}`}>
      <header className={fabStyles.header}>
        <div className={fabStyles.headerText}>
          <h2 className={fabStyles.title}>Field Dashboard</h2>
          <p className={fabStyles.subtitle}>
            Generic job view for Field Super and crew assigned to a project. Inbound packages
            appear at In Transit+; install after Received by Field. Assign people from PM Dashboard
            → Assign Employees (or Project Settings).
          </p>
        </div>
        <div className={fabStyles.headerActions}>
          <label className={fabStyles.projectFilterField}>
            <span className={fabStyles.projectFilterLabel}>Project</span>
            <select
              className={fabStyles.projectFilterSelect}
              value={projectFilterId ?? ''}
              onChange={(e) => setProjectFilter(e.target.value || null)}
            >
              <option value="">All projects</option>
              {projectOptions.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.jobCode ? `${project.jobCode} · ` : ''}
                  {project.name}
                </option>
              ))}
            </select>
          </label>
        </div>
      </header>

      <div className={styles.laneStrip} role="tablist" aria-label="Field stages">
        <button
          type="button"
          role="tab"
          aria-selected={laneFilter === 'active'}
          className={laneFilter === 'active' ? styles.laneChipActive : styles.laneChip}
          onClick={() => setLaneFilter('active')}
        >
          Active
          <span className={styles.laneCount}>
            {
              allPackageTasks.filter((pkg) => {
                if (isPackageInboundFromShipping(pkg)) return true;
                return pkg.status !== 'complete';
              }).length
            }
          </span>
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={laneFilter === 'inbound'}
          className={laneFilter === 'inbound' ? styles.laneChipActive : styles.laneChip}
          onClick={() => setLaneFilter('inbound')}
        >
          Inbound
          <span className={styles.laneCount}>{inboundCount}</span>
        </button>
        {FIELD_WORKFLOW_LANES.map((lane) => (
          <button
            key={lane}
            type="button"
            role="tab"
            aria-selected={laneFilter === lane}
            className={laneFilter === lane ? styles.laneChipActive : styles.laneChip}
            onClick={() => setLaneFilter(lane)}
          >
            {laneChipLabel(lane)}
            <span className={styles.laneCount}>{laneCounts[lane] ?? 0}</span>
          </button>
        ))}
        <button
          type="button"
          role="tab"
          aria-selected={laneFilter === 'all'}
          className={laneFilter === 'all' ? styles.laneChipActive : styles.laneChip}
          onClick={() => setLaneFilter('all')}
        >
          All
          <span className={styles.laneCount}>{allPackageTasks.length}</span>
        </button>
      </div>

      <div className={fabStyles.layout}>
        <aside className={fabStyles.packageList} aria-label="Field packages">
          <h3 className={fabStyles.paneTitle}>Packages</h3>
          {packageTasks.length === 0 ? (
            <p className={fabStyles.empty}>
              No packages visible. Shipping In Transit+ packages appear here for Field staff
              assigned to the project. Assign Field on Project Settings if the list is empty.
            </p>
          ) : (
            <ul className={fabStyles.list}>
              {packageTasks.map((task) => {
                const sPackage = task.customFields?.[SSV3_FIELD.package] ?? '';
                const inbound = isPackageInboundFromShipping(task);
                const allChildren = tasks.filter(
                  (entry) => isSsv3AssemblyTask(entry) && entry.parentTaskId === task.id
                );
                const children = assembliesForFieldDashboardView(task, allChildren);
                const inboundLane = inboundShippingLabel(task, children);
                const partialFab = inbound && task.boardType === 'fab';
                const meta = inbound
                  ? partialFab
                    ? {
                        label: PARTIAL_STILL_IN_FAB_LABEL,
                        color: PARTIAL_STILL_IN_FAB_COLOR,
                      }
                    : {
                        label: inboundLane
                          ? shippingLaneLabel(inboundLane, task)
                          : 'Inbound',
                        color:
                          shippingStatusesFor(task).find((status) => status.id === inboundLane)
                            ?.color ?? '#fdba74',
                      }
                  : statusMeta(task.status, task);
                const selected = task.id === selectedPackageId;
                const statuses = inbound
                  ? shippingStatusesFor(task)
                  : fieldStatusesFor(task);
                const progress = assemblyInstallProgress(children);
                return (
                  <li key={task.id}>
                    <div
                      className={
                        selected ? fabStyles.packageItemActive : fabStyles.packageItem
                      }
                    >
                      <button
                        type="button"
                        className={fabStyles.packageSelectBtn}
                        onClick={() => setSelectedPackageId(task.id)}
                      >
                        <span className={fabStyles.packageName}>{task.title}</span>
                        <span className={fabStyles.packageMeta}>
                          {sPackage} · {projectLabel(task.projectId)}
                          {partialFab ? ' · partial release' : ''}
                        </span>
                        {inbound ? (
                          <span className={styles.inboundBadge}>
                            {partialFab
                              ? PARTIAL_STILL_IN_FAB_LABEL
                              : `Inbound · ${meta.label}`}
                          </span>
                        ) : progress.total > 0 ? (
                          <span className={fabStyles.assemblyProgress}>
                            {progress.done}/{progress.total} assemblies installed
                          </span>
                        ) : null}
                      </button>
                      {inbound ? null : (
                      <label className={fabStyles.statusField}>
                        <span className={fabStyles.srOnly}>Package status</span>
                        <select
                          className={fabStyles.statusSelect}
                          value={task.status}
                          style={{ borderColor: meta.color, color: meta.color }}
                          onChange={(e) => updateTask(task.id, { status: e.target.value })}
                          onClick={(e) => e.stopPropagation()}
                          title="Field install status"
                        >
                          {statuses.map((status) => (
                            <option key={status.id} value={status.id}>
                              {status.label}
                            </option>
                          ))}
                        </select>
                      </label>
                      )}
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </aside>

        <section className={`${fabStyles.detail} ${styles.midPane}`} aria-label="Assemblies">
          {!selectedPackage ? (
            <p className={fabStyles.empty}>Select a package to install.</p>
          ) : (
            <>
              <div className={fabStyles.detailHeader}>
                <h3 className={fabStyles.detailTitle}>{selectedPackage.title}</h3>
                <p className={fabStyles.detailMeta}>
                  {projectLabel(selectedPackage.projectId)}
                  {selectedPackage.customFields?.[SSV3_FIELD.exportedAt]
                    ? ` · exported ${formatScanTime(selectedPackage.customFields[SSV3_FIELD.exportedAt])}`
                    : null}
                </p>
                {selectedInbound && (selectedPartialFab || selectedInboundLane) ? (
                  <p className={styles.inboundCallout}>
                    {selectedPartialFab
                      ? `${PARTIAL_STILL_IN_FAB_LABEL} — some assemblies are still on Fabrication.`
                      : `Inbound from Shipping · ${shippingLaneLabel(selectedInboundLane!, selectedPackage)}`}
                    {getEstimatedArrival(selectedPackage)
                      ? ` · Expected ${formatShipDateShort(getEstimatedArrival(selectedPackage))}`
                      : ''}
                    {getShippedAt(selectedPackage)
                      ? ` · Shipped ${formatShipDateShort(getShippedAt(selectedPackage))}`
                      : ''}
                    . Package install lanes unlock after Received by Field.
                  </p>
                ) : selectedInstallProgress.total > 0 ? (
                  <p className={styles.installProgress}>
                    {selectedInstallProgress.done}/{selectedInstallProgress.total} assemblies
                    installed
                  </p>
                ) : null}
                <div className={styles.advanceRow}>
                  {selectedInbound ? (
                    <p className={styles.sectionHint}>
                      Shipping still owns package status. Open spool sheets and prep install;
                      Field stages apply after handoff.
                    </p>
                  ) : (
                    <>
                      <label className={styles.statusInline}>
                        <span className={styles.statusInlineLabel}>Package status</span>
                        <select
                          className={fabStyles.statusSelect}
                          value={selectedPackage.status}
                          style={{
                            borderColor: statusMeta(selectedPackage.status, selectedPackage)
                              .color,
                            color: statusMeta(selectedPackage.status, selectedPackage).color,
                          }}
                          onChange={(e) =>
                            updateTask(selectedPackage.id, { status: e.target.value })
                          }
                        >
                          {fieldStatusesFor(selectedPackage).map((status) => (
                            <option key={status.id} value={status.id}>
                              {status.label}
                            </option>
                          ))}
                        </select>
                      </label>
                      {selectedAdvance && selectedAdvanceLabel ? (
                        <button
                          type="button"
                          className={styles.advanceBtn}
                          onClick={() => advancePackage(selectedPackage)}
                        >
                          Advance to {selectedAdvanceLabel}
                        </button>
                      ) : (
                        <span className={styles.advanceDone}>Package complete</span>
                      )}
                    </>
                  )}
                </div>
                <PackageCollabBar packageTask={selectedPackage} allowEdit />
              </div>

              <div className={fabStyles.detailSection}>
                <h4 className={fabStyles.sectionHeading}>Assemblies</h4>
                <p className={styles.sectionHint}>
                  {selectedInbound
                    ? 'Assemblies still in Fab are grayed out. Prefabricated In Transit+ assemblies can open spool sheets; Field stages unlock after handoff.'
                    : 'Select an assembly to open its spool sheet and update install status.'}
                </p>
                {assemblies.length === 0 ? (
                  <p className={fabStyles.empty}>No assemblies in this package.</p>
                ) : (
                  <ul className={fabStyles.assemblyList}>
                    {assemblies.map((task) => {
                      const active = task.id === selectedAssemblyId;
                      const stillInFab =
                        selectedInbound &&
                        !isAssemblyReleasedForShipView(task, selectedPackage);
                      const fieldReady =
                        !selectedInbound ||
                        isShippingStatusVisibleToField(getAssemblyShippingLane(task));
                      const fieldStatus = stillInFab
                        ? PARTIAL_STILL_IN_FAB_STATUS_ID
                        : getAssemblyFieldStatus(task);
                      const fieldMeta = stillInFab
                        ? {
                            label: PARTIAL_STILL_IN_FAB_LABEL,
                            color: PARTIAL_STILL_IN_FAB_COLOR,
                          }
                        : statusMeta(fieldStatus, selectedPackage);
                      const sheetLabel = [
                        task.customFields?.[SSV3_FIELD.sheetName],
                        task.customFields?.[SSV3_FIELD.sheetNumber],
                      ]
                        .map((v) => (v ?? '').trim())
                        .filter(Boolean)
                        .join(' · ');
                      const shipLane = getAssemblyShippingLane(task);
                      const shipMeta =
                        selectedInbound &&
                        !stillInFab &&
                        isShippingStatusVisibleToField(shipLane)
                          ? shippingStatusesFor(selectedPackage).find(
                              (status) => status.id === shipLane
                            )
                          : null;
                      const qrLabel = formatQrDisplayLabel(getTaskQr(task));
                      return (
                        <li key={task.id}>
                          <div
                            className={
                              stillInFab
                                ? fabStyles.assemblyRowReleased
                                : active
                                  ? `${fabStyles.assemblyRow} ${styles.assemblyRowActive}`
                                  : fabStyles.assemblyRow
                            }
                          >
                            <button
                              type="button"
                              className={styles.assemblySelectBtn}
                              onClick={() => {
                                if (stillInFab) return;
                                setSelectedAssemblyId(task.id);
                              }}
                              disabled={stillInFab}
                            >
                              <span className={fabStyles.assemblyTitle}>{task.title}</span>
                              {stillInFab ? (
                                <span className={styles.assemblyMeta}>
                                  {PARTIAL_STILL_IN_FAB_LABEL}
                                </span>
                              ) : shipMeta ? (
                                <span className={styles.assemblyMeta}>
                                  Shipping: {shipMeta.label}
                                  {qrLabel ? ` · QR ${qrLabel}` : ''}
                                  {sheetLabel ? ` · ${sheetLabel}` : ''}
                                </span>
                              ) : sheetLabel ? (
                                <span className={styles.assemblyMeta}>
                                  {qrLabel ? `QR ${qrLabel} · ` : ''}
                                  {sheetLabel}
                                </span>
                              ) : (
                                <span className={styles.assemblyMeta}>
                                  {qrLabel ? `QR ${qrLabel}` : 'Open spool sheet'}
                                </span>
                              )}
                            </button>
                            <select
                              className={fabStyles.statusSelect}
                              value={fieldStatus}
                              disabled={stillInFab || (selectedInbound && !fieldReady)}
                              style={{
                                borderColor: fieldMeta.color,
                                color: fieldMeta.color,
                              }}
                              onChange={(e) => setAssemblyFieldStatus(task, e.target.value)}
                              onClick={(e) => e.stopPropagation()}
                              title={
                                stillInFab
                                  ? 'Still in Fab — mark Ready for Shipping first'
                                  : selectedInbound && !fieldReady
                                    ? 'Waiting for In Transit from Shipping'
                                    : 'Assembly install status'
                              }
                            >
                              {stillInFab ? (
                                <option value={PARTIAL_STILL_IN_FAB_STATUS_ID}>
                                  {PARTIAL_STILL_IN_FAB_LABEL}
                                </option>
                              ) : (
                                <>
                                  <option value="not-started">Not Started</option>
                                  {FIELD_WORKFLOW_LANES.map((lane) => (
                                    <option key={lane} value={lane}>
                                      {statusMeta(lane, selectedPackage).label}
                                    </option>
                                  ))}
                                </>
                              )}
                            </select>
                          </div>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>
            </>
          )}
        </section>

        <section className={`${fabStyles.viewer} ${styles.viewerPane}`} aria-label="Spool sheet">
          {!selectedAssembly ? (
            <p className={fabStyles.viewerEmpty}>
              Select an assembly to view its spool sheet for install.
            </p>
          ) : (
            <div
              className={
                showFieldWelds ? styles.viewerStackWithWelds : styles.viewerStack
              }
            >
              <div className={styles.sheetBlock}>
                <div className={styles.sheetHeader}>
                  <div className={styles.sheetHeaderRow}>
                    <div>
                      <h4 className={fabStyles.sectionHeading}>Spool sheet</h4>
                      <p className={fabStyles.detailMeta}>
                        {[
                          selectedAssembly.customFields?.[SSV3_FIELD.sheetName],
                          selectedAssembly.customFields?.[SSV3_FIELD.sheetNumber],
                        ]
                          .map((v) => (v ?? '').trim())
                          .filter(Boolean)
                          .join(' · ') || selectedAssembly.title}
                      </p>
                    </div>
                    {visibleFieldWelds.length > 0 ? (
                      <button
                        type="button"
                        className={
                          showFieldWelds ? styles.weldToggleActive : styles.weldToggle
                        }
                        onClick={() => setShowFieldWelds((open) => !open)}
                      >
                        Field Welds ({visibleFieldWelds.length})
                      </button>
                    ) : null}
                  </div>
                </div>
                {previewBusy ? <p className={fabStyles.empty}>Loading spool sheet…</p> : null}
                {previewError ? (
                  <p className={fabStyles.viewerError} role="alert">
                    {previewError}
                  </p>
                ) : null}
                {previewUrl ? (
                  <CleanPdfViewer src={previewUrl} title={selectedAssembly.title} />
                ) : !previewBusy && !previewError ? (
                  <p className={fabStyles.empty}>
                    {spoolPdfFiles.length === 0
                      ? 'No spool PDFs in this package export.'
                      : 'Opening sheet…'}
                  </p>
                ) : null}
              </div>

              {showFieldWelds && visibleFieldWelds.length > 0 ? (
                <div className={styles.weldBlock}>
                  <FabWeldLogGrid
                    rows={visibleFieldWelds}
                    title="Field Welds (optional)"
                    canTapFill={canTapFillFieldWelds}
                    canTapFillRow={
                      canEditAllWelds ? undefined : (row) => isFieldWeldRow(row)
                    }
                    signedInLabel={currentUser?.name ?? null}
                    busy={weldBusy}
                    error={weldError}
                    saveMessage={weldSaveMessage}
                    hint="Only when this assembly has Field Welds — tap Date / Welder / Initials to fill."
                    onTapFill={handleTapFill}
                  />
                </div>
              ) : null}
            </div>
          )}
        </section>
      </div>
    </div>
  );
}
