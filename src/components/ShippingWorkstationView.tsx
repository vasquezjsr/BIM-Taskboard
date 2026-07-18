import { useCallback, useEffect, useMemo, useState } from 'react';
import { useStore } from '../store/useStore';
import type { Task } from '../types';
import {
  isSsv3AssemblyTask,
  isSsv3ShippingPackageTask,
  SSV3_FIELD,
} from '../utils/boardroomPackageImport';
import {
  findAssemblyByQr,
  formatQrDisplayLabel,
  formatShipDateShort,
  getEstimatedArrival,
  getShippedAt,
  getTaskQr,
  toDateInputValue,
  withPackageStatusShippedStamp,
  withShippingStatusCustomFields,
} from '../utils/shippingTracking';
import {
  getBoardTaskStatuses,
  isShippingHandedToFieldStatus,
} from '../utils/taskStatuses';
import { canLogTime } from '../utils/permissions';
import {
  formatTimeLabel,
  isOpenTimeEntry,
  localNowTimeString,
  localTodayIsoDate,
  prepareCompletedClockTimes,
} from '../utils/timeEntry';
import { PackageCollabBar } from './PackageCollabBar';
import fabStyles from './FabWorkstationView.module.css';
import styles from './ShippingWorkstationView.module.css';

/** Primary shipping lane order (packages from Fab start at Staging). */
const SHIPPING_LANES = [
  { id: 'staging', label: 'Staging' },
  { id: 'loading', label: 'Loading' },
  { id: 'in-transit', label: 'In Transit' },
  { id: 'delivered', label: 'Delivered' },
  { id: 'received-field', label: 'Received by Field' },
  { id: 'complete', label: 'Complete' },
] as const;

/** Package can leave Shipping for rework upstream. */
const RETURN_PACKAGE_OPTIONS = [
  { id: 'return-to-fab', label: 'Return to Fab' },
  { id: 'spooling', label: 'Return to Spooling' },
] as const;

type ShippingLaneId = (typeof SHIPPING_LANES)[number]['id'];

function nextLaneId(status: string): ShippingLaneId | null {
  const idx = SHIPPING_LANES.findIndex((lane) => lane.id === status);
  if (idx < 0 || idx >= SHIPPING_LANES.length - 1) return null;
  return SHIPPING_LANES[idx + 1].id;
}

function isShippingLaneId(value: string | null | undefined): value is ShippingLaneId {
  return SHIPPING_LANES.some((lane) => lane.id === value);
}

/** Assembly shipping lane — independent of fab Complete status. */
function getAssemblyShippingStatus(task: Task): ShippingLaneId {
  const raw = task.customFields?.[SSV3_FIELD.shippingStatus];
  return isShippingLaneId(raw) ? raw : 'staging';
}

/** Left staging count as “shipped” (on a load / en route / delivered). */
function isAssemblyShippedLane(status: ShippingLaneId): boolean {
  return status !== 'staging';
}

function formatScanTime(iso: string | null): string {
  if (!iso) return 'Never';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

export function ShippingWorkstationView() {
  const tasks = useStore((s) => s.tasks);
  const projects = useStore((s) => s.projects);
  const clients = useStore((s) => s.clients);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const updateTask = useStore((s) => s.updateTask);
  const updateTasksWith = useStore((s) => s.updateTasksWith);
  const selectedProjectId = useStore((s) => s.selectedProjectId);
  const currentUserId = useStore((s) => s.currentUserId);
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const timeEntries = useStore((s) => s.timeEntries);
  const addTimeEntry = useStore((s) => s.addTimeEntry);
  const updateTimeEntry = useStore((s) => s.updateTimeEntry);

  const [laneFilter, setLaneFilter] = useState<ShippingLaneId | 'active' | 'all'>('active');
  const [selectedPackageId, setSelectedPackageId] = useState<string | null>(null);
  const [projectFilterId, setProjectFilterId] = useState<string | null>(null);
  const [trackedAssemblyId, setTrackedAssemblyId] = useState<string | null>(null);
  const [qrQuery, setQrQuery] = useState('');
  const [qrMessage, setQrMessage] = useState<string | null>(null);

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

  const allPackageTasks = useMemo(() => {
    let list = tasks
      .filter(isSsv3ShippingPackageTask)
      .filter((task) => !isShippingHandedToFieldStatus(task.status));
    const filterId = projectFilterId ?? selectedProjectId;
    if (filterId) {
      list = list.filter((task) => task.projectId === filterId);
    }
    return list.sort((a, b) => (a.title ?? '').localeCompare(b.title ?? ''));
  }, [tasks, projectFilterId, selectedProjectId]);

  const laneCounts = useMemo(() => {
    const counts: Record<string, number> = Object.fromEntries(
      SHIPPING_LANES.map((lane) => [lane.id, 0])
    );
    for (const pkg of allPackageTasks) {
      const key = SHIPPING_LANES.some((lane) => lane.id === pkg.status)
        ? pkg.status
        : 'staging';
      counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
  }, [allPackageTasks]);

  const packageTasks = useMemo(() => {
    if (laneFilter === 'all') return allPackageTasks;
    if (laneFilter === 'active') {
      return allPackageTasks.filter((pkg) => pkg.status !== 'complete');
    }
    return allPackageTasks.filter((pkg) => pkg.status === laneFilter);
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
    return tasks
      .filter((task) => isSsv3AssemblyTask(task) && task.parentTaskId === selectedPackage.id)
      .sort((a, b) => (a.title ?? '').localeCompare(b.title ?? ''));
  }, [tasks, selectedPackage]);

  const assemblyProgress = useCallback(
    (packageId: string) => {
      const children = tasks.filter(
        (task) => isSsv3AssemblyTask(task) && task.parentTaskId === packageId
      );
      const total = children.length;
      const shipped = children.filter((child) =>
        isAssemblyShippedLane(getAssemblyShippingStatus(child))
      ).length;
      return { shipped, total };
    },
    [tasks]
  );

  const projectOptions = useMemo(() => {
    const ids = new Set(
      tasks.filter(isSsv3ShippingPackageTask).map((task) => task.projectId).filter(Boolean)
    );
    return projects
      .filter((project) => ids.has(project.id))
      .sort((a, b) => (a.jobCode ?? a.name).localeCompare(b.jobCode ?? b.name));
  }, [tasks, projects]);

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
    (task: Task) => {
      const statuses = shippingStatusesFor(task);
      const match = statuses.find((status) => status.id === task.status);
      return {
        label: match?.label ?? task.status,
        color: match?.color ?? '#94a3b8',
      };
    },
    [shippingStatusesFor]
  );

  const shippingLaneMeta = useCallback(
    (laneId: ShippingLaneId, task: Task) => {
      const statuses = shippingStatusesFor(task);
      const match = statuses.find((status) => status.id === laneId);
      return {
        label: match?.label ?? SHIPPING_LANES.find((lane) => lane.id === laneId)?.label ?? laneId,
        color: match?.color ?? '#94a3b8',
      };
    },
    [shippingStatusesFor]
  );

  const setAssemblyShippingStatus = useCallback(
    (assembly: Task, status: string) => {
      updateTask(assembly.id, {
        customFields: withShippingStatusCustomFields(assembly, status),
      });
    },
    [updateTask]
  );

  const setPackageShippingStatus = useCallback(
    (pkg: Task, status: string) => {
      updateTask(pkg.id, withPackageStatusShippedStamp(pkg, status));
    },
    [updateTask]
  );

  const returnPackageUpstream = useCallback(
    (pkg: Task, destination: 'return-to-fab' | 'spooling') => {
      const label = destination === 'return-to-fab' ? 'Fab' : 'Spooling';
      const ok = window.confirm(
        `Return package "${pkg.title}" to ${label}?\n\nThe whole package and its assemblies will leave Shipping.`
      );
      if (!ok) return;
      updateTask(pkg.id, { status: destination });
      if (selectedPackageId === pkg.id) setSelectedPackageId(null);
    },
    [updateTask, selectedPackageId]
  );

  const handlePackageStatusChange = useCallback(
    (pkg: Task, status: string) => {
      if (status === 'return-to-fab' || status === 'spooling') {
        returnPackageUpstream(pkg, status);
        return;
      }
      setPackageShippingStatus(pkg, status);
    },
    [returnPackageUpstream, setPackageShippingStatus]
  );

  const handleAssemblyStatusChange = useCallback(
    (assembly: Task, status: string, packageTask: Task) => {
      if (status === 'return-to-fab' || status === 'spooling') {
        returnPackageUpstream(packageTask, status);
        return;
      }
      setAssemblyShippingStatus(assembly, status);
    },
    [returnPackageUpstream, setAssemblyShippingStatus]
  );

  const setPackageEstimatedArrival = useCallback(
    (pkg: Task, value: string) => {
      updateTask(pkg.id, {
        customFields: {
          ...(pkg.customFields ?? {}),
          [SSV3_FIELD.estimatedArrival]: value || null,
        },
      });
      // Empty assembly Expected fields follow the package; leave filled ones alone.
      const eligible = tasks.filter(
        (task) =>
          isSsv3AssemblyTask(task) &&
          task.parentTaskId === pkg.id &&
          !getEstimatedArrival(task)
      );
      if (eligible.length === 0) return;
      updateTasksWith(
        eligible.map((task) => task.id),
        (task) => ({
          customFields: {
            ...(task.customFields ?? {}),
            [SSV3_FIELD.estimatedArrival]: value || null,
          },
        })
      );
    },
    [tasks, updateTask, updateTasksWith]
  );

  const setPackageShippedAt = useCallback(
    (pkg: Task, value: string) => {
      updateTask(pkg.id, {
        customFields: {
          ...(pkg.customFields ?? {}),
          [SSV3_FIELD.shippedAt]: value || null,
        },
      });
      // Empty assembly Shipped fields follow the package; leave filled ones alone.
      const eligible = tasks.filter(
        (task) =>
          isSsv3AssemblyTask(task) &&
          task.parentTaskId === pkg.id &&
          !getShippedAt(task)
      );
      if (eligible.length === 0) return;
      updateTasksWith(
        eligible.map((task) => task.id),
        (task) => ({
          customFields: {
            ...(task.customFields ?? {}),
            [SSV3_FIELD.shippedAt]: value || null,
          },
        })
      );
    },
    [tasks, updateTask, updateTasksWith]
  );

  const setAssemblyEstimatedArrival = useCallback(
    (assembly: Task, value: string) => {
      updateTask(assembly.id, {
        customFields: {
          ...(assembly.customFields ?? {}),
          [SSV3_FIELD.estimatedArrival]: value || null,
        },
      });
    },
    [updateTask]
  );

  const setAssemblyShippedAt = useCallback(
    (assembly: Task, value: string) => {
      updateTask(assembly.id, {
        customFields: {
          ...(assembly.customFields ?? {}),
          [SSV3_FIELD.shippedAt]: value || null,
        },
      });
    },
    [updateTask]
  );

  const advancePackage = useCallback(
    (pkg: Task) => {
      const next = nextLaneId(pkg.status);
      if (!next) return;
      setPackageShippingStatus(pkg, next);
    },
    [setPackageShippingStatus]
  );

  const advanceAssembly = useCallback(
    (assembly: Task) => {
      const next = nextLaneId(getAssemblyShippingStatus(assembly));
      if (!next) return;
      setAssemblyShippingStatus(assembly, next);
    },
    [setAssemblyShippingStatus]
  );

  const lookupQr = useCallback(() => {
    const hit = findAssemblyByQr(tasks, qrQuery);
    if (!hit) {
      setQrMessage('No assembly matched that QR. Check the label and try again.');
      setTrackedAssemblyId(null);
      return;
    }
    if (hit.packageTask.boardType !== 'shipping') {
      setQrMessage(
        `Found ${hit.assembly.title} on ${hit.packageTask.boardType} — open that board to track it.`
      );
    } else {
      const qrLabel = formatQrDisplayLabel(getTaskQr(hit.assembly)) || '—';
      setQrMessage(`Matched ${hit.assembly.title} · QR ${qrLabel}`);
    }
    setSelectedPackageId(hit.packageTask.id);
    setTrackedAssemblyId(hit.assembly.id);
  }, [tasks, qrQuery]);

  const selectedAdvance = selectedPackage ? nextLaneId(selectedPackage.status) : null;
  const selectedAdvanceLabel = selectedAdvance
    ? SHIPPING_LANES.find((lane) => lane.id === selectedAdvance)?.label
    : null;

  const canClockPackage = canLogTime(currentUserId, employees, employeePermissions);

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
      note: `Shipping · ${selectedPackage.title}`,
    });
  }, [
    canClockPackage,
    selectedPackage,
    currentUserId,
    openClockEntry,
    addTimeEntry,
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

  return (
    <div className={`${fabStyles.wrapper} ${styles.shippingRoot}`}>
      <header className={fabStyles.header}>
        <div className={fabStyles.headerText}>
          <h2 className={fabStyles.title}>Shipping Dashboard</h2>
          <p className={fabStyles.subtitle}>
            Packages marked Ready for Shipping leave Fabrication and move here — stage, load,
            deliver, then hand off to Field. Ship the whole package or individual assemblies.
            Advance to Received by Field to send the package to the Field Dashboard.
          </p>
        </div>
        <div className={fabStyles.headerActions}>
          <label className={styles.qrLookupField}>
            <span className={fabStyles.projectFilterLabel}>Scan / enter QR</span>
            <div className={styles.qrLookupRow}>
              <input
                className={styles.qrInput}
                value={qrQuery}
                onChange={(e) => {
                  setQrQuery(e.target.value);
                  setQrMessage(null);
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    lookupQr();
                  }
                }}
                placeholder="Assembly label QR"
                title="Match QR codes from assembly labels / spool sheets"
              />
              <button type="button" className={styles.qrFindBtn} onClick={lookupQr}>
                Find
              </button>
            </div>
            {qrMessage ? <span className={styles.qrMessage}>{qrMessage}</span> : null}
          </label>
          <label className={fabStyles.projectFilterField}>
            <span className={fabStyles.projectFilterLabel}>Project</span>
            <select
              className={fabStyles.projectFilterSelect}
              value={projectFilterId ?? ''}
              onChange={(e) => setProjectFilterId(e.target.value || null)}
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

      <div className={styles.laneStrip} role="tablist" aria-label="Shipping stages">
        <button
          type="button"
          role="tab"
          aria-selected={laneFilter === 'active'}
          className={laneFilter === 'active' ? styles.laneChipActive : styles.laneChip}
          onClick={() => setLaneFilter('active')}
        >
          Active
          <span className={styles.laneCount}>
            {allPackageTasks.filter((pkg) => pkg.status !== 'complete').length}
          </span>
        </button>
        {SHIPPING_LANES.map((lane) => (
          <button
            key={lane.id}
            type="button"
            role="tab"
            aria-selected={laneFilter === lane.id}
            className={laneFilter === lane.id ? styles.laneChipActive : styles.laneChip}
            onClick={() => setLaneFilter(lane.id)}
          >
            {lane.label}
            <span className={styles.laneCount}>{laneCounts[lane.id] ?? 0}</span>
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
        <aside className={fabStyles.packageList} aria-label="Shipping packages">
          <h3 className={fabStyles.paneTitle}>Packages</h3>
          {packageTasks.length === 0 ? (
            <p className={fabStyles.empty}>
              No packages in this stage. Mark a Fab package Ready for Shipping to send it here.
            </p>
          ) : (
            <ul className={fabStyles.list}>
              {packageTasks.map((task) => {
                const sPackage = task.customFields?.[SSV3_FIELD.package] ?? '';
                const meta = statusMeta(task);
                const selected = task.id === selectedPackageId;
                const statuses = shippingStatusesFor(task);
                const { shipped, total } = assemblyProgress(task.id);
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
                        </span>
                        {total > 0 ? (
                          <>
                            <span className={fabStyles.assemblyProgress}>
                              {shipped}/{total} assemblies shipped
                            </span>
                            {getShippedAt(task) ? (
                              <span className={styles.dateMeta}>
                                Shipped {formatShipDateShort(getShippedAt(task))}
                              </span>
                            ) : null}
                            {getEstimatedArrival(task) ? (
                              <span className={styles.dateMeta}>
                                Expected {formatShipDateShort(getEstimatedArrival(task))}
                              </span>
                            ) : null}
                          </>
                        ) : null}
                      </button>
                      <label className={fabStyles.statusField}>
                        <span className={fabStyles.srOnly}>Package status</span>
                        <select
                          className={fabStyles.statusSelect}
                          value={task.status}
                          style={{ borderColor: meta.color, color: meta.color }}
                          onChange={(e) => handlePackageStatusChange(task, e.target.value)}
                          onClick={(e) => e.stopPropagation()}
                          title="Package shipping status"
                        >
                          {statuses.map((status) => (
                            <option key={status.id} value={status.id}>
                              {status.label}
                            </option>
                          ))}
                          <optgroup label="Return package">
                            {RETURN_PACKAGE_OPTIONS.map((option) => (
                              <option key={option.id} value={option.id}>
                                {option.label}
                              </option>
                            ))}
                          </optgroup>
                        </select>
                      </label>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </aside>

        <section
          className={`${fabStyles.detail} ${styles.detailFill}`}
          aria-label="Package detail"
        >
          {!selectedPackage ? (
            <p className={fabStyles.empty}>Select a package to stage and ship.</p>
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
                <div className={styles.workflowBar}>
                  {SHIPPING_LANES.map((lane, index) => {
                    const currentIdx = SHIPPING_LANES.findIndex(
                      (entry) => entry.id === selectedPackage.status
                    );
                    const done = currentIdx >= index;
                    const current = selectedPackage.status === lane.id;
                    return (
                      <div
                        key={lane.id}
                        className={
                          current
                            ? styles.workflowStepCurrent
                            : done
                              ? styles.workflowStepDone
                              : styles.workflowStep
                        }
                      >
                        <span className={styles.workflowIndex}>{index + 1}</span>
                        <span className={styles.workflowLabel}>{lane.label}</span>
                      </div>
                    );
                  })}
                </div>
                <div className={styles.trackingRow}>
                  <label className={styles.dateField}>
                    <span className={styles.statusInlineLabel}>Shipped date</span>
                    <input
                      type="date"
                      className={styles.dateInput}
                      value={toDateInputValue(getShippedAt(selectedPackage))}
                      onChange={(e) => setPackageShippedAt(selectedPackage, e.target.value)}
                    />
                  </label>
                  <label className={styles.dateField}>
                    <span className={styles.statusInlineLabel}>Expected delivery</span>
                    <input
                      type="date"
                      className={styles.dateInput}
                      value={toDateInputValue(getEstimatedArrival(selectedPackage))}
                      onChange={(e) =>
                        setPackageEstimatedArrival(selectedPackage, e.target.value)
                      }
                    />
                  </label>
                </div>
                <div className={styles.advanceRow}>
                  <label className={styles.statusInline}>
                    <span className={styles.statusInlineLabel}>Package status</span>
                    <select
                      className={fabStyles.statusSelect}
                      value={selectedPackage.status}
                      style={{
                        borderColor: statusMeta(selectedPackage).color,
                        color: statusMeta(selectedPackage).color,
                      }}
                      onChange={(e) =>
                        handlePackageStatusChange(selectedPackage, e.target.value)
                      }
                    >
                      {shippingStatusesFor(selectedPackage).map((status) => (
                        <option key={status.id} value={status.id}>
                          {status.label}
                        </option>
                      ))}
                      <optgroup label="Return package">
                        {RETURN_PACKAGE_OPTIONS.map((option) => (
                          <option key={option.id} value={option.id}>
                            {option.label}
                          </option>
                        ))}
                      </optgroup>
                    </select>
                  </label>
                  {selectedAdvance && selectedAdvanceLabel ? (
                    <button
                      type="button"
                      className={styles.advanceBtn}
                      onClick={() => advancePackage(selectedPackage)}
                    >
                      Advance package to {selectedAdvanceLabel}
                    </button>
                  ) : (
                    <span className={styles.advanceDone}>Package shipping complete</span>
                  )}
                </div>
                <PackageCollabBar packageTask={selectedPackage} allowEdit />
                {canClockPackage ? (
                  <div className={`${fabStyles.clockStrip} ${fabStyles.clockStripActive}`}>
                    <span className={fabStyles.clockLabel}>Time on package</span>
                    <div className={fabStyles.clockActions}>
                      <button
                        type="button"
                        className={fabStyles.clockBtnActive}
                        disabled={Boolean(openClockEntry)}
                        onClick={handleClockIn}
                      >
                        Clock in
                      </button>
                      <button
                        type="button"
                        className={fabStyles.clockBtnActive}
                        disabled={!openClockEntry}
                        onClick={handleClockOut}
                      >
                        Clock out
                      </button>
                    </div>
                    <span className={fabStyles.clockHint}>
                      {openClockEntry?.startTime
                        ? `Clocked in · ${formatTimeLabel(openClockEntry.startTime)} · logs to Time Tracking`
                        : 'Not clocked in · logs to Time Tracking'}
                    </span>
                  </div>
                ) : null}
              </div>

              <div className={fabStyles.detailSection}>
                <h4 className={fabStyles.sectionHeading}>Assemblies</h4>
                {assemblies.length === 0 ? (
                  <p className={fabStyles.empty}>No assemblies in this package.</p>
                ) : (
                  <ul className={fabStyles.assemblyList}>
                    {assemblies.map((task) => {
                      const shipStatus = getAssemblyShippingStatus(task);
                      const shipMeta = shippingLaneMeta(shipStatus, selectedPackage);
                      const nextShip = nextLaneId(shipStatus);
                      const nextShipLabel = nextShip
                        ? SHIPPING_LANES.find((lane) => lane.id === nextShip)?.label
                        : null;
                      const tracked = trackedAssemblyId === task.id;
                      return (
                        <li key={task.id}>
                          <div
                            className={
                              tracked
                                ? `${fabStyles.assemblyRow} ${styles.shipAssemblyRow} ${styles.assemblyRowTracked}`
                                : `${fabStyles.assemblyRow} ${styles.shipAssemblyRow}`
                            }
                          >
                            <div className={styles.assemblyMain}>
                              <span className={fabStyles.assemblyTitle}>{task.title}</span>
                            </div>
                            <label className={styles.dateFieldCompact}>
                              <span className={styles.dateFieldCompactLabel}>Shipped</span>
                              <input
                                type="date"
                                className={styles.dateInputCompact}
                                value={toDateInputValue(
                                  getShippedAt(task) || getShippedAt(selectedPackage)
                                )}
                                onChange={(e) => setAssemblyShippedAt(task, e.target.value)}
                                title={
                                  getShippedAt(task)
                                    ? 'Assembly shipped date'
                                    : getShippedAt(selectedPackage)
                                      ? 'Following package shipped date — change to set a different date'
                                      : 'Date this assembly shipped'
                                }
                              />
                            </label>
                            <label className={styles.dateFieldCompact}>
                              <span className={styles.dateFieldCompactLabel}>Expected</span>
                              <input
                                type="date"
                                className={styles.dateInputCompact}
                                value={toDateInputValue(
                                  getEstimatedArrival(task) ||
                                    getEstimatedArrival(selectedPackage)
                                )}
                                onChange={(e) =>
                                  setAssemblyEstimatedArrival(task, e.target.value)
                                }
                                title={
                                  getEstimatedArrival(task)
                                    ? 'Assembly expected delivery'
                                    : getEstimatedArrival(selectedPackage)
                                      ? 'Following package expected delivery — change to set a different date'
                                      : 'Expected delivery date for this assembly'
                                }
                              />
                            </label>
                            <label className={styles.shipStatusField}>
                              <span className={fabStyles.srOnly}>Ship status</span>
                              <select
                                className={fabStyles.statusSelect}
                                value={shipStatus}
                                style={{
                                  borderColor: shipMeta.color,
                                  color: shipMeta.color,
                                }}
                                onChange={(e) =>
                                  handleAssemblyStatusChange(
                                    task,
                                    e.target.value,
                                    selectedPackage
                                  )
                                }
                                title="Assembly shipping status — or return the whole package upstream"
                              >
                                {SHIPPING_LANES.map((lane) => (
                                  <option key={lane.id} value={lane.id}>
                                    {lane.label}
                                  </option>
                                ))}
                                <optgroup label="Return package">
                                  {RETURN_PACKAGE_OPTIONS.map((option) => (
                                    <option key={option.id} value={option.id}>
                                      {option.label}
                                    </option>
                                  ))}
                                </optgroup>
                              </select>
                            </label>
                            {nextShip && nextShipLabel ? (
                              <button
                                type="button"
                                className={styles.shipOneBtn}
                                onClick={() => advanceAssembly(task)}
                                title={`Advance ${task.title} to ${nextShipLabel}`}
                              >
                                Ship → {nextShipLabel}
                              </button>
                            ) : (
                              <span className={styles.shipOneDone}>Shipped</span>
                            )}
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
      </div>
    </div>
  );
}
