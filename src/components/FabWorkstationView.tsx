import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent } from 'react';
import { employeeInitials } from '../data/employees';
import { useStore } from '../store/useStore';
import type { Task } from '../types';
import {
  isSsv3AssemblyTask,
  isSsv3PackageTask,
  parseSsv3Files,
  SSV3_FIELD,
  type BoardroomPackageFileRef,
} from '../utils/boardroomPackageImport';
import {
  extractPdfPageBlobUrl,
  isSpoolsCombinedPdf,
  resolveAssemblyPdfPageIndex,
} from '../utils/extractPdfPage';
import {
  assembliesVisibleToUser,
  canAssignDeptLead,
  canAssignWorkers,
  findBomFileName,
  getPackageDeptLeadId,
  getPackageWorkerId,
  getPrimaryFabRole,
  isWarehouseStatusOption,
  isQueueActiveStatus,
  listFabDeptLeads,
  listFabWorkers,
  listWorkersForDeptLead,
  packageVisibleToUser,
  workstationTitle,
} from '../utils/fabWorkstationAccess';
import {
  canAssignFabLeadsPermission,
  canAssignFabWorkersPermission,
  canEditFabCollab,
  canEditFabStatus,
  canEditWeldLog,
  canFabClock,
} from '../utils/permissions';
import { getBoardTaskStatuses, isAssemblyCompleteStatus, isFabInFabStatus, isFabShippedStatus } from '../utils/taskStatuses';
import {
  formatTimeLabel,
  isOpenTimeEntry,
  localNowTimeString,
  localTodayIsoDate,
  prepareCompletedClockTimes,
} from '../utils/timeEntry';
import {
  arrayBufferToBase64,
  base64ToArrayBuffer,
  filterWeldLogRowsForAssembly,
  findWeldLogFileName,
  parseWeldLogWorkbook,
  serializeWeldLogWorkbook,
  todayLocalIsoDate,
  toggleWeldLogFill,
  type WeldLogFillField,
  type WeldLogRow,
} from '../utils/weldLogWorkbook';
import { FabWeldLogGrid } from './FabWeldLogGrid';
import { CleanPdfViewer } from './CleanPdfViewer';
import { PackageCollabBar } from './PackageCollabBar';
import { FabDashboardNav, type ShopDashboardSection } from './FabDashboardNav';
import styles from './FabWorkstationView.module.css';

type ViewerSelection =
  | { kind: 'file'; fileName: string; label: string }
  | { kind: 'assembly'; taskId: string; label: string; preferredFileName: string | null };

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

  for (const needle of candidates) {
    const match = pdfs.find((file) => file.fileName.toLowerCase().includes(needle));
    if (match) return match.fileName;
  }

  const combined = pdfs.find((file) => isSpoolsCombinedPdf(file.fileName));
  return combined?.fileName ?? null;
}

function base64ToObjectUrl(base64: string, mimeType: string): string {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return URL.createObjectURL(new Blob([bytes], { type: mimeType }));
}

function isPreviewableMime(mimeType: string): boolean {
  return (
    mimeType === 'application/pdf' ||
    mimeType.startsWith('image/') ||
    mimeType.startsWith('text/') ||
    mimeType === 'application/json'
  );
}

function isWeldLogFile(fileName: string): boolean {
  return /weld\s*log/i.test(fileName) && /\.xlsx$/i.test(fileName);
}

function clampZoom(value: number): number {
  return Math.min(5, Math.max(0.25, value));
}

const FAB_PANE_STORAGE_KEY = 'bim-fab-pane-widths-v1';
const DEFAULT_FAB_PANE_WIDTHS = { packages: 280, detail: 520 };
const MIN_FAB_PANE_WIDTH = 200;
const MAX_FAB_PANE_WIDTH = 900;

function loadFabPaneWidths(): { packages: number; detail: number } {
  try {
    const raw = localStorage.getItem(FAB_PANE_STORAGE_KEY);
    if (!raw) return { ...DEFAULT_FAB_PANE_WIDTHS };
    const parsed = JSON.parse(raw) as { packages?: unknown; detail?: unknown };
    const packages =
      typeof parsed.packages === 'number' && Number.isFinite(parsed.packages)
        ? parsed.packages
        : DEFAULT_FAB_PANE_WIDTHS.packages;
    const detail =
      typeof parsed.detail === 'number' && Number.isFinite(parsed.detail)
        ? parsed.detail
        : DEFAULT_FAB_PANE_WIDTHS.detail;
    return {
      packages: Math.min(MAX_FAB_PANE_WIDTH, Math.max(MIN_FAB_PANE_WIDTH, packages)),
      detail: Math.min(MAX_FAB_PANE_WIDTH, Math.max(MIN_FAB_PANE_WIDTH, detail)),
    };
  } catch {
    return { ...DEFAULT_FAB_PANE_WIDTHS };
  }
}

function saveFabPaneWidths(widths: { packages: number; detail: number }) {
  try {
    localStorage.setItem(FAB_PANE_STORAGE_KEY, JSON.stringify(widths));
  } catch {
    /* ignore quota / private mode */
  }
}

export function FabWorkstationView() {
  const tasks = useStore((s) => s.tasks);
  const projects = useStore((s) => s.projects);
  const clients = useStore((s) => s.clients);
  const employees = useStore((s) => s.employees);
  const currentUserId = useStore((s) => s.currentUserId);
  const dashboardAssignments = useStore((s) => s.dashboardAssignments);
  const employeeReportsTo = useStore((s) => s.employeeReportsTo);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const updateTask = useStore((s) => s.updateTask);
  const timeEntries = useStore((s) => s.timeEntries);
  const addTimeEntry = useStore((s) => s.addTimeEntry);
  const updateTimeEntry = useStore((s) => s.updateTimeEntry);
  const employeePermissions = useStore((s) => s.employeePermissions);

  const [selectedPackageId, setSelectedPackageId] = useState<string | null>(null);
  const [shopSection, setShopSection] = useState<ShopDashboardSection>('queue');
  const [selectedWorkstationId, setSelectedWorkstationId] = useState<string | null>(null);
  /** Shop uses its own filter — not the Clients tab project — so packages don’t disappear. */
  const [shopProjectFilterId, setShopProjectFilterId] = useState<string | null>(null);
  const [paneWidths, setPaneWidths] = useState(loadFabPaneWidths);
  const [viewer, setViewer] = useState<ViewerSelection | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [previewText, setPreviewText] = useState<string | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [previewMode, setPreviewMode] = useState<'native-pdf' | 'image-pan' | 'text' | 'none'>(
    'none'
  );
  const [viewerZoom, setViewerZoom] = useState(1);
  const [viewerPan, setViewerPan] = useState({ x: 0, y: 0 });
  const [weldRows, setWeldRows] = useState<WeldLogRow[]>([]);
  const [weldBusy, setWeldBusy] = useState(false);
  const [weldError, setWeldError] = useState<string | null>(null);
  const [weldSaveMessage, setWeldSaveMessage] = useState<string | null>(null);
  const [statusMessage, setStatusMessage] = useState<string>('Waiting for exports…');
  const previewUrlRef = useRef<string | null>(null);
  const previewBlobUrlRef = useRef<string | null>(null);
  const weldFilePathRef = useRef<string | null>(null);
  const panDragRef = useRef<{
    pointerId: number;
    startX: number;
    startY: number;
    originX: number;
    originY: number;
  } | null>(null);

  const currentUser = useMemo(
    () => employees.find((employee) => employee.id === currentUserId) ?? null,
    [employees, currentUserId]
  );

  const fabMode: 'queue' | 'warehouse' | 'personal' =
    shopSection === 'queue' ? 'queue' : shopSection === 'warehouse' ? 'warehouse' : 'personal';

  const focusUserId =
    shopSection === 'fabrication' ? selectedWorkstationId : currentUserId;

  const focusUser = useMemo(
    () =>
      focusUserId
        ? (employees.find((employee) => employee.id === focusUserId) ?? null)
        : null,
    [employees, focusUserId]
  );

  const awaitingFabricationWorkstation =
    shopSection === 'fabrication' && !selectedWorkstationId;

  const handleShopSectionChange = useCallback((section: ShopDashboardSection) => {
    setShopSection(section);
    if (section !== 'fabrication') {
      setSelectedWorkstationId(null);
    }
    setSelectedPackageId(null);
    setViewer(null);
  }, []);

  const handleWorkstationSelect = useCallback((employeeId: string) => {
    setShopSection('fabrication');
    setSelectedWorkstationId(employeeId);
    setSelectedPackageId(null);
    setViewer(null);
  }, []);

  const beginPaneResize = useCallback(
    (pane: 'packages' | 'detail', event: ReactPointerEvent<HTMLButtonElement>) => {
      event.preventDefault();
      const handle = event.currentTarget;
      const pointerId = event.pointerId;
      handle.setPointerCapture(pointerId);
      const startX = event.clientX;
      const startWidth = paneWidths[pane];

      const onMove = (moveEvent: PointerEvent) => {
        const delta = moveEvent.clientX - startX;
        const nextWidth = Math.min(
          MAX_FAB_PANE_WIDTH,
          Math.max(MIN_FAB_PANE_WIDTH, startWidth + delta)
        );
        setPaneWidths((current) => {
          const updated = { ...current, [pane]: nextWidth };
          saveFabPaneWidths(updated);
          return updated;
        });
      };

      const onUp = () => {
        handle.releasePointerCapture(pointerId);
        handle.removeEventListener('pointermove', onMove);
        handle.removeEventListener('pointerup', onUp);
        handle.removeEventListener('pointercancel', onUp);
      };

      handle.addEventListener('pointermove', onMove);
      handle.addEventListener('pointerup', onUp);
      handle.addEventListener('pointercancel', onUp);
    },
    [paneWidths]
  );

  const resetPaneWidths = useCallback(() => {
    const next = { ...DEFAULT_FAB_PANE_WIDTHS };
    setPaneWidths(next);
    saveFabPaneWidths(next);
  }, []);

  const primaryFabRole = useMemo(
    () => getPrimaryFabRole(currentUserId, dashboardAssignments, employees),
    [currentUserId, dashboardAssignments, employees]
  );

  const focusPrimaryFabRole = useMemo(
    () => getPrimaryFabRole(focusUserId, dashboardAssignments, employees),
    [focusUserId, dashboardAssignments, employees]
  );

  const deptLeadOptions = useMemo(
    () => listFabDeptLeads(employees, dashboardAssignments),
    [employees, dashboardAssignments]
  );

  const workerOptions = useMemo(() => {
    const team = listWorkersForDeptLead(
      focusUserId,
      employees,
      dashboardAssignments,
      employeeReportsTo
    );
    if (team.length > 0) return team;
    return listFabWorkers(employees, dashboardAssignments);
  }, [focusUserId, employees, dashboardAssignments, employeeReportsTo]);

  const allowDeptLeadAssign =
    canAssignDeptLead(fabMode, primaryFabRole) &&
    canAssignFabLeadsPermission(currentUserId, employees, employeePermissions);

  const allPackageTasks = useMemo(() => {
    return tasks
      .filter(isSsv3PackageTask)
      .filter((task) => (shopProjectFilterId ? task.projectId === shopProjectFilterId : true))
      .sort((a, b) => {
        const aAt = a.customFields?.[SSV3_FIELD.exportedAt] ?? a.createdAt;
        const bAt = b.customFields?.[SSV3_FIELD.exportedAt] ?? b.createdAt;
        return bAt.localeCompare(aAt);
      });
  }, [tasks, shopProjectFilterId]);

  const packageTasks = useMemo(() => {
    if (fabMode === 'queue') {
      return allPackageTasks.filter((pkg) => isQueueActiveStatus(pkg.status));
    }
    if (!focusUserId) return [];
    return allPackageTasks.filter((pkg) => {
      if (isFabShippedStatus(pkg.status)) return false;
      const children = tasks.filter(
        (task) => isSsv3AssemblyTask(task) && task.parentTaskId === pkg.id
      );
      return packageVisibleToUser(pkg, children, focusUserId, fabMode);
    });
  }, [allPackageTasks, tasks, focusUserId, fabMode]);

  const assemblyCompletion = useCallback(
    (packageId: string) => {
      const children = tasks.filter(
        (task) => isSsv3AssemblyTask(task) && task.parentTaskId === packageId
      );
      const total = children.length;
      if (total === 0) return { completed: 0, total: 0 };
      const pkg = tasks.find((task) => task.id === packageId);
      const statuses = getBoardTaskStatuses(
        'fab',
        boardTaskStatuses,
        pkg?.projectId ?? null,
        projectBoardTaskStatuses
      );
      const completed = children.filter((child) =>
        isAssemblyCompleteStatus(child.status, statuses)
      ).length;
      return { completed, total };
    },
    [tasks, boardTaskStatuses, projectBoardTaskStatuses]
  );

  useEffect(() => {
    if (!selectedPackageId && packageTasks[0]) {
      setSelectedPackageId(packageTasks[0].id);
      return;
    }
    if (selectedPackageId && !packageTasks.some((task) => task.id === selectedPackageId)) {
      setSelectedPackageId(packageTasks[0]?.id ?? null);
    }
  }, [packageTasks, selectedPackageId]);

  useEffect(() => {
    setViewer(null);
  }, [selectedPackageId]);

  const selectedPackage = packageTasks.find((task) => task.id === selectedPackageId) ?? null;

  const allowWorkerAssign =
    canAssignWorkers(
      fabMode,
      selectedPackage,
      focusUserId,
      dashboardAssignments,
      employees
    ) && canAssignFabWorkersPermission(currentUserId, employees, employeePermissions);

  const canSubOutPackage = useCallback(
    (pkg: Task) =>
      canAssignWorkers(fabMode, pkg, focusUserId, dashboardAssignments, employees) &&
      canAssignFabWorkersPermission(currentUserId, employees, employeePermissions),
    [fabMode, focusUserId, dashboardAssignments, employees, currentUserId, employeePermissions]
  );

  const allAssemblies = useMemo(() => {
    if (!selectedPackage) return [];
    return tasks
      .filter((task) => isSsv3AssemblyTask(task) && task.parentTaskId === selectedPackage.id)
      .sort((a, b) => (a.title ?? '').localeCompare(b.title ?? ''));
  }, [tasks, selectedPackage]);

  const assemblies = useMemo(() => {
    if (!selectedPackage) return allAssemblies;
    if (fabMode === 'queue' || fabMode === 'warehouse') return allAssemblies;
    if (!focusUserId) return [];
    return assembliesVisibleToUser(
      selectedPackage,
      allAssemblies,
      focusUserId,
      fabMode,
      focusPrimaryFabRole
    );
  }, [selectedPackage, allAssemblies, focusUserId, fabMode, focusPrimaryFabRole]);

  const exportFiles = useMemo(() => {
    if (!selectedPackage) return [];
    return parseSsv3Files(selectedPackage);
  }, [selectedPackage]);

  const bomFileName = useMemo(
    () => findBomFileName(exportFiles.map((file) => file.fileName)),
    [exportFiles]
  );

  useEffect(() => {
    if (fabMode !== 'warehouse' || !selectedPackage || !bomFileName) return;
    setViewer({
      kind: 'file',
      fileName: bomFileName,
      label: bomFileName,
    });
  }, [fabMode, selectedPackage?.id, bomFileName]);

  const canClockPackage =
    (fabMode === 'warehouse' || fabMode === 'personal') &&
    canFabClock(currentUserId, employees, employeePermissions);

  const openClockEntry = useMemo(() => {
    if (!selectedPackage || !currentUserId) return null;
    return (
      timeEntries.find(
        (entry) =>
          entry.taskId === selectedPackage.id &&
          entry.employeeId === currentUserId &&
          isOpenTimeEntry(entry)
      ) ?? null
    );
  }, [timeEntries, selectedPackage, currentUserId]);

  const handleClockIn = useCallback(() => {
    if (!canClockPackage || !selectedPackage || !currentUserId || openClockEntry) return;
    const now = new Date();
    addTimeEntry({
      employeeId: currentUserId,
      clientId: selectedPackage.clientId,
      projectId: selectedPackage.projectId,
      taskId: selectedPackage.id,
      date: localTodayIsoDate(now),
      startTime: localNowTimeString(now),
      endTime: null,
      hours: 0,
      note:
        fabMode === 'warehouse'
          ? `Warehouse · ${selectedPackage.title}`
          : `Fab · ${selectedPackage.title}`,
    });
  }, [
    canClockPackage,
    selectedPackage,
    currentUserId,
    openClockEntry,
    addTimeEntry,
    fabMode,
  ]);

  const handleClockOut = useCallback(() => {
    if (!canClockPackage || !openClockEntry) return;
    const completed = prepareCompletedClockTimes(
      openClockEntry.startTime,
      localNowTimeString()
    );
    if (!completed) return;
    updateTimeEntry(openClockEntry.id, {
      employeeId: openClockEntry.employeeId,
      clientId: openClockEntry.clientId,
      projectId: openClockEntry.projectId,
      taskId: openClockEntry.taskId,
      date: openClockEntry.date,
      startTime: completed.startTime,
      endTime: completed.endTime,
      hours: completed.hours,
      note: openClockEntry.note,
    });
  }, [canClockPackage, openClockEntry, updateTimeEntry]);

  const fabStatusesFor = useCallback(
    (task: Task) =>
      getBoardTaskStatuses('fab', boardTaskStatuses, task.projectId, projectBoardTaskStatuses),
    [boardTaskStatuses, projectBoardTaskStatuses]
  );

  const statusMeta = useCallback(
    (task: Task) => {
      const statuses = fabStatusesFor(task);
      const def = statuses.find((status) => status.id === task.status);
      return {
        label: def?.label ?? task.status,
        color: def?.color ?? '#94a3b8',
      };
    },
    [fabStatusesFor]
  );

  const projectLabel = useCallback(
    (projectId: string | null) => {
      if (!projectId) return '—';
      const project = projects.find((entry) => entry.id === projectId);
      if (!project) return 'Unknown project';
      const client = clients.find((entry) => entry.id === project.clientId);
      const code = project.jobCode ? `${project.jobCode} · ` : '';
      return `${code}${client?.name ?? 'Client'} / ${project.name}`;
    },
    [projects, clients]
  );

  const assignDeptLead = useCallback(
    (packageId: string, deptLeadId: string) => {
      const pkg = tasks.find((task) => task.id === packageId);
      if (!pkg) return;
      updateTask(packageId, {
        customFields: {
          ...(pkg.customFields ?? {}),
          [SSV3_FIELD.deptLeadId]: deptLeadId,
        },
      });
    },
    [tasks, updateTask]
  );

  /** Package-level owner only — does not overwrite per-assembly assignments. */
  const assignPackageWorker = useCallback(
    (packageId: string, workerId: string) => {
      const pkg = tasks.find((task) => task.id === packageId);
      if (!pkg) return;
      updateTask(packageId, {
        customFields: {
          ...(pkg.customFields ?? {}),
          [SSV3_FIELD.workerId]: workerId,
        },
        assigneeIds: workerId ? [workerId] : [],
      });
    },
    [tasks, updateTask]
  );

  /** Per-assembly assignee — independent of package owner. */
  const assignAssemblyWorker = useCallback(
    (assemblyId: string, _packageId: string, workerId: string) => {
      updateTask(assemblyId, { assigneeIds: workerId ? [workerId] : [] });
    },
    [updateTask]
  );

  /** If any assembly is In Fab, parent package must be In Progress. */
  const setAssemblyStatus = useCallback(
    (assemblyId: string, packageId: string, status: string) => {
      updateTask(assemblyId, { status });
      if (!isFabInFabStatus(status)) return;
      const pkg = tasks.find((task) => task.id === packageId);
      if (!pkg || pkg.status === 'in-progress') return;
      updateTask(packageId, { status: 'in-progress' });
    },
    [tasks, updateTask]
  );

  const clearPreview = useCallback(() => {
    if (previewBlobUrlRef.current) {
      URL.revokeObjectURL(previewBlobUrlRef.current);
      previewBlobUrlRef.current = null;
    }
    previewUrlRef.current = null;
    setPreviewUrl(null);
    setPreviewText(null);
    setPreviewError(null);
    setPreviewMode('none');

    setViewerZoom(1);
    setViewerPan({ x: 0, y: 0 });
    panDragRef.current = null;
  }, []);

  const clearWeldLog = useCallback(() => {
    setWeldRows([]);
    setWeldError(null);
    setWeldBusy(false);
    setWeldSaveMessage(null);
    weldFilePathRef.current = null;
  }, []);

  const loadWeldLogFile = useCallback(
    async (fileName: string) => {
      const folder = selectedPackage?.customFields?.[SSV3_FIELD.exportFolder];
      if (!folder || folder.startsWith('(browser')) {
        setWeldError('Weld log requires Electron with the original export folder path.');
        return;
      }
      const api = window.electronAPI;
      if (!api?.readFilePreview) {
        setWeldError('Weld log requires the BIM Boardroom desktop app (updated preload).');
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
        const rows = parseWeldLogWorkbook(base64ToArrayBuffer(result.base64));
        setWeldRows(rows);
      } catch (err) {
        setWeldError(err instanceof Error ? err.message : String(err));
        setWeldRows([]);
      } finally {
        setWeldBusy(false);
      }
    },
    [selectedPackage]
  );

  const loadNativePdfUrl = useCallback(async (fullPath: string) => {
    const api = window.electronAPI!;
    if (!api.readFilePreview) {
      throw new Error('PDF preview requires an updated Electron preload (readFilePreview).');
    }
    const result = await api.readFilePreview(fullPath);
    if (!result.ok) throw new Error(result.error);
    const url = base64ToObjectUrl(result.base64, 'application/pdf');
    previewBlobUrlRef.current = url;
    previewUrlRef.current = url;
    setPreviewUrl(url);
    setPreviewMode('native-pdf');
  }, []);

  const loadAssemblySheetPdf = useCallback(
    async (fileName: string, assembly: Task) => {
      clearPreview();
      const folder = selectedPackage?.customFields?.[SSV3_FIELD.exportFolder];
      if (!folder || folder.startsWith('(browser')) {
        setPreviewError('Preview requires Electron with the original export folder path.');
        return;
      }
      const api = window.electronAPI;
      if (!api?.readFilePreview && !api?.getFilePreviewUrl) {
        setPreviewError('Sheet preview requires the BIM Boardroom desktop app.');
        return;
      }

      const fullPath = joinPath(folder, fileName);
      setPreviewBusy(true);
      try {
        if (isSpoolsCombinedPdf(fileName) && api.readFilePreview) {
          const pageIndex = resolveAssemblyPdfPageIndex(allAssemblies, assembly);
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
          previewUrlRef.current = extracted.url;
          setPreviewUrl(extracted.url);

          setPreviewMode('native-pdf');

          return;
        }

        await loadNativePdfUrl(fullPath);

      } catch (err) {
        setPreviewError(err instanceof Error ? err.message : String(err));
      } finally {
        setPreviewBusy(false);
      }
    },
    [allAssemblies, clearPreview, loadNativePdfUrl, selectedPackage]
  );

  const loadPreviewFile = useCallback(
    async (fileName: string) => {
      clearPreview();
      if (isWeldLogFile(fileName)) {
        setPreviewMode('none');
        return;
      }

      const folder = selectedPackage?.customFields?.[SSV3_FIELD.exportFolder];
      if (!folder || folder.startsWith('(browser')) {
        setPreviewError('Preview requires Electron with the original export folder path.');
        return;
      }
      const api = window.electronAPI;
      if (!api) {
        setPreviewError('Sheet preview requires the BIM Boardroom desktop app.');
        return;
      }
      if (!api.getFilePreviewUrl && !api.readFilePreview) {
        setPreviewError(
          'Electron preload is outdated. Stop the app and run npm run electron:dev again so preview APIs compile.'
        );
        return;
      }

      const fullPath = joinPath(folder, fileName);
      setPreviewBusy(true);
      try {
        if (api.getFilePreviewUrl) {
          const result = await api.getFilePreviewUrl(fullPath);
          if (!result.ok) {
            setPreviewError(result.error);
            return;
          }
          if (!isPreviewableMime(result.mimeType)) {
            setPreviewError(
              `${result.fileName} can’t be previewed here (${result.mimeType}).`
            );
            return;
          }
          if (result.mimeType.startsWith('text/') || result.mimeType === 'application/json') {
            if (api.readFilePreview) {
              const textResult = await api.readFilePreview(fullPath);
              if (textResult.ok) {
                setPreviewText(atob(textResult.base64));
                setPreviewMode('text');
                return;
              }
            }
            setPreviewError('Text preview unavailable.');
            return;
          }
          if (result.mimeType === 'application/pdf') {
            await loadNativePdfUrl(fullPath);
            return;
          }
          previewUrlRef.current = result.previewUrl;
          setPreviewUrl(result.previewUrl);
          setPreviewMode('image-pan');
          return;
        }

        const result = await api.readFilePreview!(fullPath);
        if (!result.ok) {
          setPreviewError(result.error);
          return;
        }
        if (!isPreviewableMime(result.mimeType)) {
          setPreviewError(
            `${result.fileName} can’t be previewed here (${result.mimeType}).`
          );
          return;
        }
        if (result.mimeType.startsWith('text/') || result.mimeType === 'application/json') {
          setPreviewText(atob(result.base64));
          setPreviewMode('text');
          return;
        }
        if (result.mimeType === 'application/pdf') {
          await loadNativePdfUrl(fullPath);
          return;
        }
        const url = base64ToObjectUrl(result.base64, result.mimeType);
        previewBlobUrlRef.current = url;
        previewUrlRef.current = url;
        setPreviewUrl(url);
        setPreviewMode('image-pan');
      } catch (err) {
        setPreviewError(err instanceof Error ? err.message : String(err));
      } finally {
        setPreviewBusy(false);
      }
    },
    [clearPreview, loadNativePdfUrl, selectedPackage]
  );

  useEffect(() => {
    if (!viewer) {
      clearPreview();
      clearWeldLog();
      return;
    }

    const weldFile =
      findWeldLogFileName(exportFiles.map((file) => file.fileName)) ??
      (viewer.kind === 'file' && isWeldLogFile(viewer.fileName) ? viewer.fileName : null);

    if (viewer.kind === 'assembly') {
      const assembly = assemblies.find((task) => task.id === viewer.taskId);
      if (viewer.preferredFileName && assembly) {
        void loadAssemblySheetPdf(viewer.preferredFileName, assembly);
      } else {
        clearPreview();
        setPreviewError('No matching sheet PDF found for this assembly in the export folder.');
      }
      if (weldFile) void loadWeldLogFile(weldFile);
      else clearWeldLog();
      return;
    }

    if (isWeldLogFile(viewer.fileName)) {
      clearPreview();
      void loadWeldLogFile(viewer.fileName);
      return;
    }

    clearWeldLog();
    void loadPreviewFile(viewer.fileName);
  }, [
    viewer,
    assemblies,
    exportFiles,
    loadAssemblySheetPdf,
    loadPreviewFile,
    loadWeldLogFile,
    clearPreview,
    clearWeldLog,
  ]);

  useEffect(() => () => clearPreview(), [clearPreview]);

  useEffect(() => {
    const api = window.electronAPI;
    if (!api?.getDefaultBoardroomExportsDir) {
      setStatusMessage('Exports sync requires the BIM Boardroom desktop app.');
      return;
    }
    void api.getDefaultBoardroomExportsDir().then((dir) => {
      setStatusMessage(
        `Exports sync active (${dir}). New package exports attach on the Spooling task automatically.`
      );
    });
  }, []);

  const selectedAssembly =
    viewer?.kind === 'assembly'
      ? (assemblies.find((task) => task.id === viewer.taskId) ?? null)
      : null;

  const viewerFileName =
    viewer?.kind === 'file'
      ? viewer.fileName
      : viewer?.kind === 'assembly'
        ? viewer.preferredFileName
        : null;

  const showWeldLog =
    Boolean(viewer) &&
    (viewer?.kind === 'assembly' ||
      (viewer?.kind === 'file' && isWeldLogFile(viewer.fileName)));

  const visibleWeldRows = useMemo(() => {
    if (!showWeldLog) return [];
    if (selectedAssembly) {
      return filterWeldLogRowsForAssembly(weldRows, selectedAssembly.title);
    }
    return weldRows;
  }, [showWeldLog, selectedAssembly, weldRows]);

  const canTapFillWeldLog =
    Boolean(currentUser) && canEditWeldLog(currentUserId, employees, employeePermissions);
  const allowFabStatusEdit = canEditFabStatus(currentUserId, employees, employeePermissions);
  const allowFabCollab = canEditFabCollab(currentUserId, employees, employeePermissions);

  useEffect(() => {
    if (!allowFabStatusEdit) return;
    for (const pkg of allPackageTasks) {
      const children = tasks.filter(
        (task) => isSsv3AssemblyTask(task) && task.parentTaskId === pkg.id
      );
      if (!children.some((child) => isFabInFabStatus(child.status))) continue;
      if (pkg.status === 'in-progress') continue;
      updateTask(pkg.id, { status: 'in-progress' });
    }
  }, [allPackageTasks, tasks, updateTask, allowFabStatusEdit]);

  const packageDisplayStatus = useCallback(
    (pkg: Task) => {
      const children = tasks.filter(
        (task) => isSsv3AssemblyTask(task) && task.parentTaskId === pkg.id
      );
      if (children.some((child) => isFabInFabStatus(child.status))) return 'in-progress';
      return pkg.status;
    },
    [tasks]
  );

  const welderFillValues = useMemo(() => {
    if (!currentUser) {
      return { date: todayLocalIsoDate(), welderId: '', initials: '' };
    }
    const initials = employeeInitials(currentUser.name);
    const welderId = (currentUser.welderId ?? '').trim() || initials;
    return {
      date: todayLocalIsoDate(),
      welderId,
      initials,
    };
  }, [currentUser]);

  const persistWeldRows = useCallback(async (nextRows: WeldLogRow[]) => {
    const path = weldFilePathRef.current;
    const api = window.electronAPI;
    if (!path || !api?.writeFileBytes) {
      setWeldSaveMessage(
        'Filled in this session only — restart Electron to save weld log edits to disk.'
      );
      return;
    }
    try {
      const bytes = serializeWeldLogWorkbook(nextRows);
      const result = await api.writeFileBytes(path, arrayBufferToBase64(bytes));
      if (!result.ok) {
        setWeldSaveMessage(`Could not save weld log: ${result.error}`);
        return;
      }
      setWeldSaveMessage('Weld log saved.');
    } catch (err) {
      setWeldSaveMessage(err instanceof Error ? err.message : String(err));
    }
  }, []);

  const handleWeldTapFill = useCallback(
    (visibleIndex: number, field: WeldLogFillField) => {
      if (!canTapFillWeldLog || !currentUser) return;
      const visible = visibleWeldRows[visibleIndex];
      if (!visible) return;

      const nextAll = weldRows.map((row) =>
        row.weldNumber === visible.weldNumber
          ? toggleWeldLogFill(row, field, welderFillValues)
          : row
      );
      setWeldRows(nextAll);
      void persistWeldRows(nextAll);
    },
    [
      canTapFillWeldLog,
      currentUser,
      visibleWeldRows,
      weldRows,
      welderFillValues,
      persistWeldRows,
    ]
  );

  const projectOptions = useMemo(() => {
    return projects
      .filter((project) => !project.isTemplate)
      .map((project) => {
        const client = clients.find((entry) => entry.id === project.clientId);
        const code = project.jobCode ? `${project.jobCode} · ` : '';
        return {
          id: project.id,
          label: `${code}${client?.name ?? 'Client'} / ${project.name}`,
        };
      })
      .sort((a, b) => a.label.localeCompare(b.label));
  }, [projects, clients]);

  const pageTitle = awaitingFabricationWorkstation
    ? 'Fabrication Dashboard'
    : workstationTitle(fabMode, focusUser?.name ?? currentUser?.name);

  const pageSubtitle = awaitingFabricationWorkstation
    ? 'Select a workstation below to open that person’s assigned packages'
    : fabMode === 'queue'
      ? 'Shop Super queue — review packages and assign Dept Leads'
      : fabMode === 'warehouse'
        ? 'Pull material from the package Bill of Materials — clock in, attach photos, comment, then mark Material Pulled'
        : 'Fabrication Dashboard — assigned packages and assemblies for this workstation';

  return (
    <div className={styles.wrapper}>
      <FabDashboardNav
        shopSection={shopSection}
        selectedWorkstationId={selectedWorkstationId}
        onShopSectionChange={handleShopSectionChange}
        onWorkstationSelect={handleWorkstationSelect}
      />
      <header className={styles.header}>
        <div className={styles.headerText}>
          <h2 className={styles.title}>{pageTitle}</h2>
          <p className={styles.subtitle}>
            {pageSubtitle}
            {shopProjectFilterId
              ? ` · filtered to ${projectLabel(shopProjectFilterId)}`
              : ' · all projects'}
          </p>
        </div>
        <div className={styles.headerActions}>
          <label className={styles.projectFilterField}>
            <span className={styles.projectFilterLabel}>Project</span>
            <select
              className={styles.projectFilterSelect}
              value={shopProjectFilterId ?? ''}
              onChange={(event) => {
                const next = event.target.value;
                setShopProjectFilterId(next || null);
                setSelectedPackageId(null);
                setViewer(null);
              }}
              aria-label="Filter shop packages by project"
            >
              <option value="">All projects</option>
              {projectOptions.map((option) => (
                <option key={option.id} value={option.id}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
        </div>
      </header>

      <p className={styles.scanStatus} role="status">
        {statusMessage}
      </p>

      {awaitingFabricationWorkstation ? (
        <div className={styles.fabricationPicker}>
          <p className={styles.empty}>
            Choose a workstation from the Workstations row above to view fabrication packages.
          </p>
        </div>
      ) : (
      <div
        className={styles.layout}
        style={
          {
            '--fab-packages-width': `${paneWidths.packages}px`,
            '--fab-detail-width': `${paneWidths.detail}px`,
          } as CSSProperties
        }
      >
        <aside className={styles.packageList} aria-label="Packages">
          <h3 className={styles.paneTitle}>Packages</h3>
          {packageTasks.length === 0 ? (
            <p className={styles.empty}>
              {fabMode === 'queue'
                ? 'No packages waiting in queue. New SSv3 packages land here until they move In Fab (or later).'
                : fabMode === 'warehouse'
                  ? 'No packages waiting for material. Packages appear here while Queued or Pulling Material, and leave when marked Material Pulled.'
                  : 'Nothing assigned to you yet. Ask Shop Super to assign a package to your Dept Lead, or ask your Lead to assign work to you.'}
            </p>
          ) : (
            <ul className={styles.list}>
              {packageTasks.map((task) => {
                const sPackage = task.customFields?.[SSV3_FIELD.package] ?? '';
                const displayStatus = packageDisplayStatus(task);
                const meta = statusMeta({ ...task, status: displayStatus });
                const selected = task.id === selectedPackageId;
                const statuses = fabStatusesFor(task);
                const deptLeadId = getPackageDeptLeadId(task) ?? '';
                return (
                  <li key={task.id}>
                    <div className={selected ? styles.packageItemActive : styles.packageItem}>
                      <button
                        type="button"
                        className={styles.packageSelectBtn}
                        onClick={() => setSelectedPackageId(task.id)}
                      >
                        <span className={styles.packageName}>{task.title}</span>
                        <span className={styles.packageMeta}>
                          {sPackage} · {projectLabel(task.projectId)}
                        </span>
                        {(() => {
                          const { completed, total } = assemblyCompletion(task.id);
                          if (total === 0) return null;
                          return (
                            <span className={styles.assemblyProgress}>
                              {completed}/{total} assemblies completed
                            </span>
                          );
                        })()}
                      </button>
                      {allowDeptLeadAssign ? (
                        <label className={styles.assignField}>
                          <span className={styles.srOnly}>Dept Lead</span>
                          <select
                            className={styles.assignSelect}
                            value={deptLeadId}
                            onChange={(e) => assignDeptLead(task.id, e.target.value)}
                            onClick={(e) => e.stopPropagation()}
                            title="Assign Dept Lead"
                          >
                            <option value="">Dept Lead…</option>
                            {deptLeadOptions.map((option) => (
                              <option key={option.id} value={option.id}>
                                {option.name}
                              </option>
                            ))}
                          </select>
                        </label>
                      ) : deptLeadId ? (
                        <span className={styles.assignReadout}>
                          Lead:{' '}
                          {employees.find((employee) => employee.id === deptLeadId)?.name ??
                            'Assigned'}
                        </span>
                      ) : null}
                      {canSubOutPackage(task) ? (
                        <label className={styles.assignField}>
                          <span className={styles.assignLabel}>Package owner</span>
                          <select
                            className={styles.assignSelect}
                            value={getPackageWorkerId(task) ?? ''}
                            onChange={(e) => assignPackageWorker(task.id, e.target.value)}
                            onClick={(e) => e.stopPropagation()}
                            title="Who owns this package — does not change assembly assignments"
                          >
                            <option value="">Select owner…</option>
                            {workerOptions.map((option) => (
                              <option key={option.id} value={option.id}>
                                {option.name}
                              </option>
                            ))}
                          </select>
                        </label>
                      ) : fabMode === 'personal' && getPackageWorkerId(task) ? (
                        <span className={styles.assignReadout}>
                          Owner:{' '}
                          {employees.find(
                            (employee) => employee.id === getPackageWorkerId(task)
                          )?.name ?? 'Assigned'}
                        </span>
                      ) : null}
                      <label className={styles.statusField}>
                        <span className={styles.srOnly}>Package status</span>
                        <select
                          className={styles.statusSelect}
                          value={displayStatus}
                          style={{ borderColor: meta.color, color: meta.color }}
                          disabled={!allowFabStatusEdit}
                          onChange={(e) => updateTask(task.id, { status: e.target.value })}
                          onClick={(e) => e.stopPropagation()}
                          title="Change package status. Spooling returns it to the Spooling board. In Progress when any assembly is In Fab."
                        >
                          {(fabMode === 'warehouse'
                            ? statuses.filter(
                                (status) =>
                                  isWarehouseStatusOption(status.id) ||
                                  status.id === displayStatus ||
                                  status.id === task.status
                              )
                            : statuses
                          ).map((status) => (
                            <option key={status.id} value={status.id}>
                              {status.label}
                            </option>
                          ))}
                        </select>
                      </label>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </aside>

        <button
          type="button"
          className={styles.paneSplitter}
          aria-label="Resize packages panel"
          title="Drag to resize · Double-click to reset widths"
          onPointerDown={(event) => beginPaneResize('packages', event)}
          onDoubleClick={resetPaneWidths}
        />

        <section className={styles.detail} aria-label="Package detail">
          {!selectedPackage ? (
            <p className={styles.empty}>Select a package to see assemblies and export files.</p>
          ) : (
            <>
              <div className={styles.detailHeader}>
                <h3 className={styles.detailTitle}>{selectedPackage.title}</h3>
                <p className={styles.detailMeta}>
                  {projectLabel(selectedPackage.projectId)}
                  {selectedPackage.customFields?.[SSV3_FIELD.exportedAt]
                    ? ` · exported ${formatScanTime(selectedPackage.customFields[SSV3_FIELD.exportedAt])}`
                    : null}
                </p>
                {allowWorkerAssign ? (
                  <label className={styles.packageWorkerField}>
                    <span className={styles.packageWorkerLabel}>Package owner</span>
                    <select
                      className={styles.assignSelect}
                      value={getPackageWorkerId(selectedPackage) ?? ''}
                      onChange={(e) =>
                        assignPackageWorker(selectedPackage.id, e.target.value)
                      }
                      title="Who owns this package — assembly workers stay as assigned"
                    >
                      <option value="">Select owner…</option>
                      {workerOptions.map((option) => (
                        <option key={option.id} value={option.id}>
                          {option.name}
                        </option>
                      ))}
                    </select>
                  </label>
                ) : fabMode === 'personal' && getPackageWorkerId(selectedPackage) ? (
                  <p className={styles.detailMeta}>
                    Owner:{' '}
                    {employees.find(
                      (employee) => employee.id === getPackageWorkerId(selectedPackage)
                    )?.name ?? 'Assigned'}
                  </p>
                ) : null}
                <PackageCollabBar packageTask={selectedPackage} allowEdit={allowFabCollab} />
              </div>

              {fabMode === 'warehouse' ? (
                <div className={styles.detailSection}>
                  <h4 className={styles.sectionHeading}>Bill of Materials</h4>
                  {bomFileName ? (
                    <ul className={styles.fileList}>
                      <li className={styles.fileRowActive}>
                        <button
                          type="button"
                          className={styles.rowSelectBtn}
                          onClick={() =>
                            setViewer({
                              kind: 'file',
                              fileName: bomFileName,
                              label: bomFileName,
                            })
                          }
                        >
                          <span className={styles.fileName}>{bomFileName}</span>
                          <span className={styles.fileType}>pdf</span>
                        </button>
                      </li>
                    </ul>
                  ) : (
                    <p className={styles.empty}>
                      No Bill of Materials PDF found in this package export.
                    </p>
                  )}
                </div>
              ) : (
                <>
              <div className={styles.detailSection}>
                <h4 className={styles.sectionHeading}>Assemblies</h4>
                {assemblies.length === 0 ? (
                  <p className={styles.empty}>No assemblies in this package.</p>
                ) : (
                  <ul className={styles.assemblyList}>
                    {assemblies.map((task) => {
                      const meta = statusMeta(task);
                      const statuses = fabStatusesFor(task);
                      const active =
                        viewer?.kind === 'assembly' && viewer.taskId === task.id;
                      const sheetLabel = [
                        task.customFields?.[SSV3_FIELD.sheetName],
                        task.customFields?.[SSV3_FIELD.sheetNumber],
                      ]
                        .map((v) => (v ?? '').trim())
                        .filter(Boolean)
                        .join(' · ');
                      const assemblyWorkerId = task.assigneeIds?.[0] ?? '';
                      return (
                        <li key={task.id}>
                          <div
                            className={
                              active ? styles.assemblyRowActive : styles.assemblyRow
                            }
                          >
                            <button
                              type="button"
                              className={styles.rowSelectBtn}
                              onClick={() =>
                                setViewer({
                                  kind: 'assembly',
                                  taskId: task.id,
                                  label: task.title,
                                  preferredFileName: findSheetFileForAssembly(
                                    exportFiles,
                                    task
                                  ),
                                })
                              }
                            >
                              <span className={styles.assemblyTitle}>{task.title}</span>
                              {sheetLabel ? (
                                <span className={styles.rowHint}>{sheetLabel}</span>
                              ) : null}
                            </button>
                            {allowWorkerAssign ? (
                              <select
                                className={styles.assignSelect}
                                value={assemblyWorkerId}
                                onChange={(e) =>
                                  assignAssemblyWorker(
                                    task.id,
                                    selectedPackage.id,
                                    e.target.value
                                  )
                                }
                                onClick={(e) => e.stopPropagation()}
                                title="Assign this assembly only — independent of package owner"
                              >
                                <option value="">Worker…</option>
                                {workerOptions.map((option) => (
                                  <option key={option.id} value={option.id}>
                                    {option.name}
                                  </option>
                                ))}
                              </select>
                            ) : fabMode === 'personal' && assemblyWorkerId ? (
                              <span className={styles.assignReadoutInline}>
                                {employees.find((employee) => employee.id === assemblyWorkerId)
                                  ?.name ?? 'Assigned'}
                              </span>
                            ) : (
                              <span className={styles.assignSelectPlaceholder} aria-hidden />
                            )}
                            {fabMode === 'personal' ? (
                              <select
                                className={styles.statusSelect}
                                value={task.status}
                                style={{ borderColor: meta.color, color: meta.color }}
                                disabled={!allowFabStatusEdit}
                                onChange={(e) =>
                                  setAssemblyStatus(task.id, selectedPackage.id, e.target.value)
                                }
                                onClick={(e) => e.stopPropagation()}
                                title="Change assembly status"
                              >
                                {statuses
                                  .filter((status) => {
                                    // Assemblies use fab shop statuses; omit package-only handoffs.
                                    if (status.id === 'spooling' && status.id !== task.status) {
                                      return false;
                                    }
                                    if (
                                      status.id === 'ready-to-ship' &&
                                      status.id !== task.status
                                    ) {
                                      return false;
                                    }
                                    return true;
                                  })
                                  .map((status) => (
                                    <option key={status.id} value={status.id}>
                                      {status.label}
                                    </option>
                                  ))}
                              </select>
                            ) : null}
                          </div>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>

              <div className={styles.detailSection}>
                <h4 className={styles.sectionHeading}>Export files</h4>
                {exportFiles.length === 0 ? (
                  <p className={styles.empty}>No report files listed in the manifest.</p>
                ) : (
                  <ul className={styles.fileList}>
                    {exportFiles.map((file) => {
                      const active =
                        viewer?.kind === 'file' && viewer.fileName === file.fileName;
                      return (
                        <li key={file.fileName}>
                          <div className={active ? styles.fileRowActive : styles.fileRow}>
                            <button
                              type="button"
                              className={styles.rowSelectBtn}
                              onClick={() =>
                                setViewer({
                                  kind: 'file',
                                  fileName: file.fileName,
                                  label: file.fileName,
                                })
                              }
                            >
                              <span className={styles.fileName}>{file.fileName}</span>
                              {file.type ? (
                                <span className={styles.fileType}>{file.type}</span>
                              ) : null}
                            </button>
                          </div>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>
                </>
              )}

              {canClockPackage ? (
                <div className={`${styles.clockStrip} ${styles.clockStripActive}`}>
                  <span className={styles.clockLabel}>Time on package</span>
                  <div className={styles.clockActions}>
                    <button
                      type="button"
                      className={styles.clockBtnActive}
                      disabled={Boolean(openClockEntry)}
                      onClick={handleClockIn}
                    >
                      Clock in
                    </button>
                    <button
                      type="button"
                      className={styles.clockBtnActive}
                      disabled={!openClockEntry}
                      onClick={handleClockOut}
                    >
                      Clock out
                    </button>
                  </div>
                  <span className={styles.clockHint}>
                    {openClockEntry?.startTime
                      ? `Clocked in · ${formatTimeLabel(openClockEntry.startTime)} · logs to Time Tracking`
                      : 'Not clocked in · logs to Time Tracking'}
                  </span>
                </div>
              ) : null}
            </>
          )}
        </section>

        <button
          type="button"
          className={styles.paneSplitter}
          aria-label="Resize detail panel"
          title="Drag to resize · Double-click to reset widths"
          onPointerDown={(event) => beginPaneResize('detail', event)}
          onDoubleClick={resetPaneWidths}
        />

        <section className={styles.viewer} aria-label="Sheet viewer">
          <div className={styles.viewerHeader}>
            <h3 className={styles.paneTitle}>Sheet viewer</h3>
          </div>

          {!viewer ? (
            <p className={styles.viewerEmpty}>
              Click an assembly or export file to preview it here.
            </p>
          ) : (
            <div className={styles.viewerBody}>
              {previewMode !== 'native-pdf' ? (
                <div className={styles.viewerMeta}>
                  <p className={styles.viewerTitle}>{viewer.label}</p>
                  {selectedAssembly ? (
                    <p className={styles.viewerSub}>
                      {[
                        selectedAssembly.customFields?.[SSV3_FIELD.sheetName],
                        selectedAssembly.customFields?.[SSV3_FIELD.sheetNumber],
                      ]
                        .map((v) => (v ?? '').trim())
                        .filter(Boolean)
                        .join(' · ') || selectedAssembly.description || 'Assembly'}
                    </p>
                  ) : null}
                  {viewerFileName && !isWeldLogFile(viewerFileName) ? (
                    <p className={styles.viewerSub}>Previewing {viewerFileName}</p>
                  ) : null}
                </div>
              ) : null}

              {previewBusy ? <p className={styles.empty}>Loading preview…</p> : null}
              {previewError ? (
                <p className={styles.viewerError} role="alert">
                  {previewError}
                </p>
              ) : null}

              {previewMode === 'native-pdf' && previewUrl ? (
                <CleanPdfViewer src={previewUrl} title={viewer.label} />
              ) : null}

              {previewMode === 'image-pan' && previewUrl ? (
                <>
                  <div className={styles.zoomBar}>
                    <button
                      type="button"
                      className={styles.zoomBtn}
                      onClick={() => setViewerZoom((z) => clampZoom(z / 1.15))}
                      title="Zoom out"
                    >
                      âˆ’
                    </button>
                    <span className={styles.zoomLabel}>{Math.round(viewerZoom * 100)}%</span>
                    <button
                      type="button"
                      className={styles.zoomBtn}
                      onClick={() => setViewerZoom((z) => clampZoom(z * 1.15))}
                      title="Zoom in"
                    >
                      +
                    </button>
                    <button
                      type="button"
                      className={styles.zoomBtn}
                      onClick={() => {
                        setViewerZoom(1);
                        setViewerPan({ x: 0, y: 0 });
                      }}
                      title="Reset view"
                    >
                      Reset
                    </button>
                    <span className={styles.zoomHint}>Scroll to zoom · drag to pan</span>
                  </div>
                  <div
                    className={styles.viewerStage}
                    onWheel={(event) => {
                      event.preventDefault();
                      const factor = event.deltaY < 0 ? 1.1 : 1 / 1.1;
                      setViewerZoom((z) => clampZoom(z * factor));
                    }}
                    onPointerDown={(event) => {
                      if (event.button !== 0) return;
                      (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
                      panDragRef.current = {
                        pointerId: event.pointerId,
                        startX: event.clientX,
                        startY: event.clientY,
                        originX: viewerPan.x,
                        originY: viewerPan.y,
                      };
                    }}
                    onPointerMove={(event) => {
                      const drag = panDragRef.current;
                      if (!drag || drag.pointerId !== event.pointerId) return;
                      setViewerPan({
                        x: drag.originX + (event.clientX - drag.startX),
                        y: drag.originY + (event.clientY - drag.startY),
                      });
                    }}
                    onPointerUp={(event) => {
                      if (panDragRef.current?.pointerId === event.pointerId) {
                        panDragRef.current = null;
                      }
                    }}
                    onPointerCancel={() => {
                      panDragRef.current = null;
                    }}
                  >
                    <div
                      className={styles.viewerTransform}
                      style={{
                        transform: `translate(${viewerPan.x}px, ${viewerPan.y}px) scale(${viewerZoom})`,
                      }}
                    >
                      <img
                        className={styles.previewImage}
                        src={previewUrl}
                        alt={viewer.label}
                        draggable={false}
                      />
                    </div>
                  </div>
                </>
              ) : null}

              {previewMode === 'text' && previewText != null ? (
                <div className={styles.viewerStage}>
                  <pre className={styles.previewText}>{previewText}</pre>
                </div>
              ) : null}

              {showWeldLog ? (
                <FabWeldLogGrid
                  rows={visibleWeldRows}
                  title={
                    selectedAssembly
                      ? `Weld log · ${selectedAssembly.title}`
                      : 'Weld log'
                  }
                  canTapFill={canTapFillWeldLog}
                  signedInLabel={currentUser?.name ?? null}
                  busy={weldBusy}
                  error={weldError}
                  saveMessage={weldSaveMessage}
                  onTapFill={handleWeldTapFill}
                />
              ) : null}
            </div>
          )}
        </section>
      </div>
      )}
    </div>
  );
}
