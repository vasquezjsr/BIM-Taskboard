import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { employeeInitials } from '../data/employees';
import { FabWeldLogGrid } from './FabWeldLogGrid';
import { useStore } from '../store/useStore';
import type { Task } from '../types';
import {
  isSsv3AssemblyTask,
  isSsv3TrackedPackageTask,
  parseSsv3Files,
  SSV3_FIELD,
} from '../utils/boardroomPackageImport';
import {
  canEditWeldLog,
  canViewWeldLogDashboard,
} from '../utils/permissions';
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
import clientStyles from './ClientView.module.css';
import styles from './WeldLogDashboardView.module.css';
import { TEMPLATE_CLIENT_NAME } from '../data/vdcSeedData';

function joinPath(folder: string, fileName: string): string {
  const base = folder.replace(/[/\\]+$/, '');
  const sep = folder.includes('\\') ? '\\' : '/';
  return `${base}${sep}${fileName}`;
}

type PackageWeldCache = {
  packageId: string;
  filePath: string | null;
  rows: WeldLogRow[];
  error: string | null;
};

export function WeldLogDashboardView() {
  const clients = useStore((s) => s.clients);
  const projects = useStore((s) => s.projects);
  const tasks = useStore((s) => s.tasks);
  const currentUserId = useStore((s) => s.currentUserId);
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);

  const currentUser = employees.find((e) => e.id === currentUserId) ?? null;
  const canView = canViewWeldLogDashboard(currentUserId, employees, employeePermissions);
  const canEditAll = canEditWeldLog(currentUserId, employees, employeePermissions);
  const canTapFill = Boolean(currentUser) && (canEditAll || canView);

  const sortedClients = useMemo(
    () =>
      [...clients]
        .filter((client) => client.name !== TEMPLATE_CLIENT_NAME)
        .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
    [clients]
  );

  const [activeClientId, setActiveClientId] = useState<string | null>(
    () => sortedClients[0]?.id ?? null
  );
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
  const [selectedPackageId, setSelectedPackageId] = useState<string | null>(null);
  const [selectedAssemblyId, setSelectedAssemblyId] = useState<string | null>(null);
  const [cache, setCache] = useState<PackageWeldCache | null>(null);
  const [busy, setBusy] = useState(false);
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const filePathRef = useRef<string | null>(null);

  useEffect(() => {
    if (activeClientId && sortedClients.some((c) => c.id === activeClientId)) return;
    setActiveClientId(sortedClients[0]?.id ?? null);
  }, [sortedClients, activeClientId]);

  const clientProjects = useMemo(
    () =>
      projects
        .filter((p) => p.clientId === activeClientId && !p.isTemplate)
        .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
    [projects, activeClientId]
  );

  useEffect(() => {
    if (selectedProjectId && clientProjects.some((p) => p.id === selectedProjectId)) return;
    setSelectedProjectId(clientProjects[0]?.id ?? null);
    setSelectedPackageId(null);
    setSelectedAssemblyId(null);
    setCache(null);
    setSaveMessage(null);
  }, [clientProjects, selectedProjectId]);

  const activeClient = clients.find((c) => c.id === activeClientId) ?? null;
  const activeProject = projects.find((p) => p.id === selectedProjectId) ?? null;

  const packageTasks = useMemo(() => {
    return tasks
      .filter(isSsv3TrackedPackageTask)
      .filter((task) => (selectedProjectId ? task.projectId === selectedProjectId : false))
      .sort((a, b) => a.title.localeCompare(b.title));
  }, [tasks, selectedProjectId]);

  const selectedPackage =
    packageTasks.find((task) => task.id === selectedPackageId) ?? packageTasks[0] ?? null;

  const assemblies = useMemo(() => {
    if (!selectedPackage) return [];
    return tasks
      .filter((task) => isSsv3AssemblyTask(task) && task.parentTaskId === selectedPackage.id)
      .sort((a, b) => a.title.localeCompare(b.title));
  }, [tasks, selectedPackage]);

  const selectedAssembly =
    assemblies.find((task) => task.id === selectedAssemblyId) ?? assemblies[0] ?? null;

  const loadPackageWeldLog = useCallback(async (pkg: Task) => {
    const folder = pkg.customFields?.[SSV3_FIELD.exportFolder];
    const files = parseSsv3Files(pkg);
    const fileName = findWeldLogFileName(files.map((f) => f.fileName));
    if (!folder || folder.startsWith('(browser') || !fileName) {
      filePathRef.current = null;
      setCache({
        packageId: pkg.id,
        filePath: null,
        rows: [],
        error: fileName
          ? 'Weld log requires Electron with the original export folder path.'
          : 'No weld log workbook on this package export.',
      });
      return;
    }
    const api = window.electronAPI;
    if (!api?.readFilePreview) {
      filePathRef.current = null;
      setCache({
        packageId: pkg.id,
        filePath: null,
        rows: [],
        error: 'Weld log requires the BIM Boardroom desktop app.',
      });
      return;
    }
    const fullPath = joinPath(folder, fileName);
    filePathRef.current = fullPath;
    setBusy(true);
    setSaveMessage(null);
    try {
      const result = await api.readFilePreview(fullPath);
      if (!result.ok) {
        setCache({
          packageId: pkg.id,
          filePath: fullPath,
          rows: [],
          error: result.error,
        });
        return;
      }
      const rows = parseWeldLogWorkbook(base64ToArrayBuffer(result.base64));
      setCache({ packageId: pkg.id, filePath: fullPath, rows, error: null });
    } catch (err) {
      setCache({
        packageId: pkg.id,
        filePath: fullPath,
        rows: [],
        error: err instanceof Error ? err.message : String(err),
      });
    } finally {
      setBusy(false);
    }
  }, []);

  useEffect(() => {
    if (!selectedPackage) {
      setCache(null);
      filePathRef.current = null;
      return;
    }
    void loadPackageWeldLog(selectedPackage);
  }, [selectedPackage, loadPackageWeldLog]);

  useEffect(() => {
    if (selectedPackage && !packageTasks.some((task) => task.id === selectedPackage.id)) {
      setSelectedPackageId(packageTasks[0]?.id ?? null);
    }
  }, [packageTasks, selectedPackage]);

  useEffect(() => {
    if (selectedAssembly && !assemblies.some((task) => task.id === selectedAssembly.id)) {
      setSelectedAssemblyId(assemblies[0]?.id ?? null);
    } else if (!selectedAssemblyId && assemblies[0]) {
      setSelectedAssemblyId(assemblies[0].id);
    }
  }, [assemblies, selectedAssembly, selectedAssemblyId]);

  const weldRows =
    cache != null && selectedPackage != null && cache.packageId === selectedPackage.id
      ? cache.rows
      : [];
  const visibleRows = useMemo(() => {
    if (!selectedAssembly) return weldRows;
    return filterWeldLogRowsForAssembly(weldRows, selectedAssembly.title);
  }, [weldRows, selectedAssembly]);

  const welderFillValues = useMemo(() => {
    if (!currentUser) {
      return { date: todayLocalIsoDate(), welderId: '', initials: '' };
    }
    const initials = employeeInitials(currentUser.name);
    const welderId = (currentUser.welderId ?? '').trim() || initials;
    return { date: todayLocalIsoDate(), welderId, initials };
  }, [currentUser]);

  const canTapFillRow = useCallback(
    (row: WeldLogRow) => {
      if (!canTapFill) return false;
      if (canEditAll) return true;
      return isFieldWeldRow(row);
    },
    [canTapFill, canEditAll]
  );

  const persistRows = useCallback(async (nextRows: WeldLogRow[]) => {
    const path = filePathRef.current;
    const api = window.electronAPI;
    if (!path || !api?.writeFileBytes) {
      setSaveMessage(
        'Filled in this session only — restart Electron to save weld log edits to disk.'
      );
      return;
    }
    try {
      const bytes = serializeWeldLogWorkbook(nextRows);
      const result = await api.writeFileBytes(path, arrayBufferToBase64(bytes));
      if (!result.ok) {
        setSaveMessage(`Could not save weld log: ${result.error}`);
        return;
      }
      setSaveMessage('Weld log saved.');
    } catch (err) {
      setSaveMessage(err instanceof Error ? err.message : String(err));
    }
  }, []);

  const handleTapFill = useCallback(
    (visibleIndex: number, field: WeldLogFillField) => {
      if (!selectedPackage || !canTapFill || !currentUser) return;
      const visible = visibleRows[visibleIndex];
      if (!visible || !canTapFillRow(visible)) return;

      const nextAll = weldRows.map((row) =>
        row.weldNumber === visible.weldNumber
          ? toggleWeldLogFill(row, field, welderFillValues)
          : row
      );
      setCache((prev) =>
        prev && prev.packageId === selectedPackage.id
          ? { ...prev, rows: nextAll, error: null }
          : prev
      );
      void persistRows(nextAll);
    },
    [
      selectedPackage,
      canTapFill,
      currentUser,
      visibleRows,
      canTapFillRow,
      weldRows,
      welderFillValues,
      persistRows,
    ]
  );

  const selectClient = (clientId: string) => {
    setActiveClientId(clientId);
    setSelectedProjectId(null);
    setSelectedPackageId(null);
    setSelectedAssemblyId(null);
    setCache(null);
    setSaveMessage(null);
  };

  const selectProject = (projectId: string) => {
    setSelectedProjectId(projectId);
    setSelectedPackageId(null);
    setSelectedAssemblyId(null);
    setCache(null);
    setSaveMessage(null);
  };

  if (!canView && !canEditAll) {
    return (
      <div className={clientStyles.empty}>
        <p>You do not have permission to view the Weld Log Dashboard.</p>
      </div>
    );
  }

  const fillHint = canEditAll
    ? `Signed in as ${currentUser?.name ?? '—'}. Tap any empty Date / Welder ID / Initials cell.`
    : `Signed in as ${currentUser?.name ?? '—'}. Field Welds only — tap Date, Welder ID, or Initials.`;

  return (
    <div className={clientStyles.container}>
      <div className={clientStyles.navArea}>
        <div className={clientStyles.tabRows}>
          <div className={clientStyles.tabRow}>
            <span className={clientStyles.tabLabel}>Clients</span>
            <div className={clientStyles.tabs}>
              {sortedClients.map((client) => (
                <button
                  key={client.id}
                  type="button"
                  className={`${clientStyles.clientTab} ${
                    activeClientId === client.id ? clientStyles.active : ''
                  }`}
                  onClick={() => selectClient(client.id)}
                >
                  {client.name}
                </button>
              ))}
            </div>
          </div>

          {activeClient ? (
            <div className={clientStyles.tabRow}>
              <span className={clientStyles.tabLabel}>Projects</span>
              <div className={clientStyles.tabs}>
                {clientProjects.map((project) => (
                  <button
                    key={project.id}
                    type="button"
                    className={`${clientStyles.projectTab} ${
                      selectedProjectId === project.id ? clientStyles.active : ''
                    }`}
                    onClick={() => selectProject(project.id)}
                    title={project.jobCode ? `${project.jobCode} · ${project.name}` : project.name}
                  >
                    {project.jobCode ? `${project.jobCode} · ${project.name}` : project.name}
                  </button>
                ))}
              </div>
            </div>
          ) : null}
        </div>
      </div>

      {sortedClients.length === 0 ? (
        <div className={clientStyles.empty}>
          <p>No clients yet.</p>
          <p className={clientStyles.emptyHint}>Add clients on the Clients tab first.</p>
        </div>
      ) : null}

      {activeClient && clientProjects.length === 0 ? (
        <div className={clientStyles.empty}>
          <p>No projects yet for {activeClient.name}.</p>
          <p className={clientStyles.emptyHint}>Add projects on the Clients tab first.</p>
        </div>
      ) : null}

      {activeClient && activeProject ? (
        <div className={styles.body}>
          {packageTasks.length === 0 ? (
            <p className={styles.empty}>No fab packages with SSv3 exports on this job yet.</p>
          ) : (
            <div className={styles.layout}>
              <aside className={styles.sidebar} aria-label="Packages and assemblies">
                <label className={styles.packageField}>
                  <span className={styles.fieldLabel}>Package</span>
                  <select
                    className={styles.packageSelect}
                    value={selectedPackage?.id ?? ''}
                    onChange={(event) => {
                      setSelectedPackageId(event.target.value || null);
                      setSelectedAssemblyId(null);
                      setSaveMessage(null);
                    }}
                  >
                    {packageTasks.map((pkg) => (
                      <option key={pkg.id} value={pkg.id}>
                        {pkg.title}
                      </option>
                    ))}
                  </select>
                </label>

                <h3 className={styles.assembliesHeading}>Assemblies</h3>
                {assemblies.length === 0 ? (
                  <p className={styles.emptyCompact}>No assemblies on this package.</p>
                ) : (
                  <ul className={styles.assemblyList}>
                    {assemblies.map((assembly) => {
                      const count = filterWeldLogRowsForAssembly(weldRows, assembly.title).length;
                      const active = selectedAssembly?.id === assembly.id;
                      return (
                        <li key={assembly.id}>
                          <button
                            type="button"
                            className={`${styles.assemblyBtn} ${
                              active ? styles.assemblyBtnActive : ''
                            }`}
                            onClick={() => setSelectedAssemblyId(assembly.id)}
                          >
                            <span className={styles.assemblyName}>{assembly.title}</span>
                            <span className={styles.assemblyCount}>{count}</span>
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </aside>

              <section className={styles.detail} aria-label="Weld log">
                {selectedAssembly ? (
                  <FabWeldLogGrid
                    rows={visibleRows}
                    title={`${selectedAssembly.title} — welds`}
                    canTapFill={canTapFill}
                    canTapFillRow={canTapFillRow}
                    signedInLabel={currentUser?.name ?? null}
                    busy={busy}
                    error={cache?.error ?? null}
                    saveMessage={saveMessage}
                    hint={fillHint}
                    onTapFill={handleTapFill}
                  />
                ) : (
                  <p className={styles.empty}>Select an assembly to review its weld log.</p>
                )}
              </section>
            </div>
          )}
        </div>
      ) : null}
    </div>
  );
}
