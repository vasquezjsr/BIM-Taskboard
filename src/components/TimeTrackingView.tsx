import { useEffect, useMemo, useRef, useState } from 'react';
import { useStore } from '../store/useStore';
import {
  canDeleteTimeEntry,
  canLogTime as hasLogTimePermission,
  canViewEmployeeTime,
  getVisibleTimeEmployeeIds,
} from '../utils/permissions';
import {
  formatTimeLabel,
  getEntryTaskLabel,
  isOpenTimeEntry,
  localNowTimeString,
  prepareCompletedClockTimes,
  snapTimeToIncrement,
} from '../utils/timeEntry';
import { TimeIncrementSelect } from './TimeIncrementSelect';
import {
  type CalendarView,
  shiftFocusDate,
  todayIsoDate,
} from '../utils/timeCalendar';
import { TimeTrackingCalendar } from './TimeTrackingCalendar';
import {
  resolveTaskClientProject,
  TimeTrackingQuickTasks,
} from './TimeTrackingQuickTasks';
import type { Task, TimeEntry } from '../types';
import styles from './TimeTrackingView.module.css';

export function TimeTrackingView() {
  const employees = useStore((s) => s.employees);
  const clients = useStore((s) => s.clients);
  const projects = useStore((s) => s.projects);
  const timeEntries = useStore((s) => s.timeEntries);
  const tasks = useStore((s) => s.tasks);
  const currentUserId = useStore((s) => s.currentUserId);
  const employeeReportsTo = useStore((s) => s.employeeReportsTo);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const addTimeEntry = useStore((s) => s.addTimeEntry);
  const updateTimeEntry = useStore((s) => s.updateTimeEntry);
  const removeTimeEntry = useStore((s) => s.removeTimeEntry);

  const allowLogTime = hasLogTimePermission(currentUserId, employees, employeePermissions);

  const canDeleteEntry = (entryEmployeeId: string) =>
    canDeleteTimeEntry(
      currentUserId,
      entryEmployeeId,
      employees,
      employeePermissions,
      employeeReportsTo
    );

  const visibleEmployeeIds = useMemo(
    () => getVisibleTimeEmployeeIds(currentUserId, employees, employeeReportsTo),
    [currentUserId, employees, employeeReportsTo]
  );

  const visibleEmployees = useMemo(
    () =>
      employees
        .filter((employee) => visibleEmployeeIds.includes(employee.id))
        .sort((a, b) => a.name.localeCompare(b.name)),
    [employees, visibleEmployeeIds]
  );

  const [employeeId, setEmployeeId] = useState(currentUserId ?? visibleEmployees[0]?.id ?? '');
  const [clientId, setClientId] = useState('');
  const [projectId, setProjectId] = useState('');
  const [date, setDate] = useState(todayIsoDate());
  const [startTime, setStartTime] = useState('');
  const [endTime, setEndTime] = useState('');
  const [note, setNote] = useState('');
  const [calendarView, setCalendarView] = useState<CalendarView>('week');
  const [focusDate, setFocusDate] = useState(todayIsoDate());
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  const [editingEntryId, setEditingEntryId] = useState<string | null>(null);
  const startTimeInputRef = useRef<HTMLSelectElement>(null);
  const formCardRef = useRef<HTMLElement>(null);

  useEffect(() => {
    if (visibleEmployeeIds.length === 0) {
      setEmployeeId('');
      return;
    }
    if (!visibleEmployeeIds.includes(employeeId)) {
      setEmployeeId(
        currentUserId && visibleEmployeeIds.includes(currentUserId)
          ? currentUserId
          : visibleEmployeeIds[0]!
      );
    }
  }, [currentUserId, employeeId, visibleEmployeeIds]);

  const visibleTimeEntries = useMemo(
    () =>
      timeEntries.filter((entry) =>
        canViewEmployeeTime(currentUserId, entry.employeeId, employees, employeeReportsTo)
      ),
    [timeEntries, currentUserId, employees, employeeReportsTo]
  );

  const openClockEntries = useMemo(
    () =>
      visibleTimeEntries
        .filter(isOpenTimeEntry)
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt)),
    [visibleTimeEntries]
  );

  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name)),
    [clients]
  );

  const clientProjects = useMemo(
    () =>
      projects
        .filter((project) => project.clientId === clientId)
        .sort((a, b) => a.name.localeCompare(b.name)),
    [projects, clientId]
  );

  const hiddenEntryCount = timeEntries.length - visibleTimeEntries.length;
  const canLogTime = allowLogTime && visibleEmployeeIds.length > 0;
  const selectedEmployee = employees.find((employee) => employee.id === employeeId);

  const resetForm = () => {
    setStartTime('');
    setEndTime('');
    setNote('');
    setSelectedTaskId(null);
    setEditingEntryId(null);
  };

  const handleClockOutEntry = (entry: TimeEntry) => {
    if (!allowLogTime) return;
    const completed = prepareCompletedClockTimes(entry.startTime, localNowTimeString());
    if (!completed) return;
    updateTimeEntry(entry.id, {
      employeeId: entry.employeeId,
      clientId: entry.clientId,
      projectId: entry.projectId,
      taskId: entry.taskId,
      date: entry.date,
      startTime: completed.startTime,
      endTime: completed.endTime,
      hours: completed.hours,
      note: entry.note,
    });
    if (editingEntryId === entry.id) resetForm();
  };

  const handleSubmit = () => {
    if (!allowLogTime) return;
    const snappedStart = startTime ? snapTimeToIncrement(startTime) : null;
    const snappedEnd = endTime ? snapTimeToIncrement(endTime) : null;
    const completed =
      snappedStart && snappedEnd
        ? prepareCompletedClockTimes(snappedStart, snappedEnd)
        : null;
    if (
      !employeeId ||
      !date ||
      !completed ||
      !canViewEmployeeTime(currentUserId, employeeId, employees, employeeReportsTo)
    ) {
      return;
    }

    const payload = {
      employeeId,
      clientId: clientId || null,
      projectId: projectId || null,
      taskId: selectedTaskId,
      date,
      startTime: completed.startTime,
      endTime: completed.endTime,
      hours: completed.hours,
      note: note.trim(),
    };

    if (editingEntryId) {
      updateTimeEntry(editingEntryId, payload);
    } else {
      addTimeEntry(payload);
    }

    resetForm();
    setFocusDate(date);
  };

  const handleEditEntry = (entry: TimeEntry) => {
    setEditingEntryId(entry.id);
    setEmployeeId(entry.employeeId);
    setClientId(entry.clientId ?? '');
    setProjectId(entry.projectId ?? '');
    setDate(entry.date);
    setStartTime(entry.startTime ?? '');
    setEndTime(entry.endTime ?? '');
    setNote(entry.note);
    setSelectedTaskId(entry.taskId);
    setFocusDate(entry.date);
    formCardRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    startTimeInputRef.current?.focus();
  };

  const handleCancelEdit = () => {
    resetForm();
  };

  const handleDeleteEditingEntry = () => {
    if (!editingEntryId) return;
    const entry = timeEntries.find((item) => item.id === editingEntryId);
    if (!entry || !canDeleteEntry(entry.employeeId)) return;
    removeTimeEntry(editingEntryId);
    resetForm();
  };

  const handleDeleteEntry = (id: string) => {
    const entry = timeEntries.find((item) => item.id === id);
    if (!entry || !canDeleteEntry(entry.employeeId)) return;
    removeTimeEntry(id);
    if (editingEntryId === id) resetForm();
  };

  const handleSelectTask = (task: Task) => {
    const resolved = resolveTaskClientProject(task, projects);
    if (!resolved) return;

    setSelectedTaskId(task.id);
    setClientId(resolved.clientId);
    setProjectId(resolved.projectId);
    if (!note.trim()) setNote(task.title);
    startTimeInputRef.current?.focus();
  };

  const handleClientChange = (nextClientId: string) => {
    setClientId(nextClientId);
    setProjectId('');
    setSelectedTaskId(null);
  };

  const handleProjectChange = (nextProjectId: string) => {
    setProjectId(nextProjectId);
    setSelectedTaskId(null);
  };

  const handleFocusDateChange = (iso: string) => {
    setFocusDate(iso);
    setDate(iso);
  };

  const handleNavigate = (delta: -1 | 1) => {
    setFocusDate((current) => shiftFocusDate(current, calendarView, delta));
  };

  return (
    <div className={styles.wrapper}>
      <div className={styles.header}>
        <h2 className={styles.title}>Time Tracking</h2>
        <p className={styles.subtitle}>
          Log hours and review them by day, week, or month. Fab Warehouse and workstation
          clocks write here automatically.
        </p>
      </div>

      {openClockEntries.length > 0 ? (
        <section className={styles.openClocks} aria-label="Open clocks">
          <span className={styles.sectionLabel}>Clocked in</span>
          <ul className={styles.openClockList}>
            {openClockEntries.map((entry) => {
              const employee = employees.find((item) => item.id === entry.employeeId);
              return (
                <li key={entry.id} className={styles.openClockItem}>
                  <div className={styles.openClockText}>
                    <span className={styles.openClockTask}>
                      {getEntryTaskLabel(entry, tasks)}
                    </span>
                    <span className={styles.openClockMeta}>
                      {employee?.name ?? 'Employee'}
                      {entry.startTime
                        ? ` · started ${formatTimeLabel(entry.startTime)}`
                        : ''}
                      {entry.note ? ` · ${entry.note}` : ''}
                    </span>
                  </div>
                  <div className={styles.openClockActions}>
                    {allowLogTime ? (
                      <>
                        <button
                          type="button"
                          className={styles.openClockEdit}
                          onClick={() => handleEditEntry(entry)}
                        >
                          Edit
                        </button>
                        <button
                          type="button"
                          className={styles.openClockOut}
                          onClick={() => handleClockOutEntry(entry)}
                        >
                          Clock out
                        </button>
                      </>
                    ) : null}
                  </div>
                </li>
              );
            })}
          </ul>
        </section>
      ) : null}

      <div className={styles.layout}>
        <section
          ref={formCardRef}
          className={`${styles.formCard} ${editingEntryId ? styles.formCardEditing : ''}`}
        >
          <span className={styles.sectionLabel}>
            {editingEntryId ? 'Edit entry' : 'Log time'}
          </span>

          {!canLogTime ? (
            <p className={styles.restrictedNote}>
              {!allowLogTime
                ? 'You do not have permission to log time. Ask an admin to grant Log time on Access Control.'
                : 'You can only log time for yourself and people who report to you.'}
            </p>
          ) : (
            <>
              <label className={styles.field}>
                <span className={styles.sectionLabel}>Employee</span>
                <select
                  className={styles.select}
                  value={employeeId}
                  onChange={(e) => {
                    setEmployeeId(e.target.value);
                    setSelectedTaskId(null);
                  }}
                >
                  {visibleEmployees.map((employee) => (
                    <option key={employee.id} value={employee.id}>
                      {employee.name}
                    </option>
                  ))}
                </select>
              </label>

              <TimeTrackingQuickTasks
                tasks={tasks}
                projects={projects}
                employeeId={employeeId}
                employeeName={selectedEmployee?.name}
                isSelf={employeeId === currentUserId}
                selectedTaskId={selectedTaskId}
                onSelectTask={handleSelectTask}
              />

              <label className={styles.field}>
                <span className={styles.sectionLabel}>Client</span>
                <select
                  className={styles.select}
                  value={clientId}
                  onChange={(e) => handleClientChange(e.target.value)}
                >
                  <option value="">Select client</option>
                  {sortedClients.map((client) => (
                    <option key={client.id} value={client.id}>
                      {client.name}
                    </option>
                  ))}
                </select>
              </label>

              <label className={styles.field}>
                <span className={styles.sectionLabel}>Project</span>
                <select
                  className={styles.select}
                  value={projectId}
                  onChange={(e) => handleProjectChange(e.target.value)}
                  disabled={!clientId}
                >
                  <option value="">Select project</option>
                  {clientProjects.map((project) => (
                    <option key={project.id} value={project.id}>
                      {project.name}
                    </option>
                  ))}
                </select>
              </label>

              <label className={styles.field}>
                <span className={styles.sectionLabel}>Date</span>
                <input
                  type="date"
                  className={styles.input}
                  value={date}
                  onChange={(e) => {
                    setDate(e.target.value);
                    setFocusDate(e.target.value);
                  }}
                />
              </label>

              <div className={styles.timeRow}>
                <label className={styles.field}>
                  <span className={styles.sectionLabel}>Start time</span>
                  <TimeIncrementSelect
                    inputRef={startTimeInputRef}
                    className={styles.select}
                    value={startTime}
                    onChange={setStartTime}
                    placeholder="Start"
                  />
                </label>

                <label className={styles.field}>
                  <span className={styles.sectionLabel}>End time</span>
                  <TimeIncrementSelect
                    className={styles.select}
                    value={endTime}
                    onChange={setEndTime}
                    placeholder="End"
                  />
                </label>
              </div>

              <label className={styles.field}>
                <span className={styles.sectionLabel}>Note</span>
                <textarea
                  className={styles.textarea}
                  value={note}
                  onChange={(e) => setNote(e.target.value)}
                  placeholder="What did you work on?"
                />
              </label>

              <div className={styles.formActions}>
                <button
                  type="button"
                  className={styles.submitBtn}
                  onClick={handleSubmit}
                  disabled={!canLogTime}
                >
                  {editingEntryId ? 'Save changes' : 'Add entry'}
                </button>
                {editingEntryId && (
                  <>
                    <button type="button" className={styles.cancelBtn} onClick={handleCancelEdit}>
                      Cancel
                    </button>
                    <button
                      type="button"
                      className={styles.deleteBtn}
                      onClick={handleDeleteEditingEntry}
                      disabled={
                        !canDeleteEntry(
                          timeEntries.find((item) => item.id === editingEntryId)?.employeeId ??
                            employeeId
                        )
                      }
                      title={
                        canDeleteEntry(
                          timeEntries.find((item) => item.id === editingEntryId)?.employeeId ??
                            employeeId
                        )
                          ? 'Delete entry'
                          : 'You do not have permission to delete this time entry'
                      }
                    >
                      Delete
                    </button>
                  </>
                )}
              </div>
            </>
          )}
        </section>

        <TimeTrackingCalendar
          entries={visibleTimeEntries}
          employees={employees}
          clients={clients}
          projects={projects}
          tasks={tasks}
          focusDate={focusDate}
          view={calendarView}
          hiddenEntryCount={hiddenEntryCount}
          editingEntryId={editingEntryId}
          onFocusDateChange={handleFocusDateChange}
          onViewChange={setCalendarView}
          onNavigate={handleNavigate}
          onEditEntry={handleEditEntry}
          onDeleteEntry={handleDeleteEntry}
        />
      </div>
    </div>
  );
}
