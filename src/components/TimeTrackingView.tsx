import { useEffect, useMemo, useRef, useState } from 'react';
import { useStore } from '../store/useStore';
import { canViewEmployeeTime, getVisibleTimeEmployeeIds } from '../utils/permissions';
import { computeHoursFromTimes, snapTimeToIncrement } from '../utils/timeEntry';
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
  const addTimeEntry = useStore((s) => s.addTimeEntry);
  const updateTimeEntry = useStore((s) => s.updateTimeEntry);
  const removeTimeEntry = useStore((s) => s.removeTimeEntry);

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
  const canLogTime = visibleEmployeeIds.length > 0;
  const selectedEmployee = employees.find((employee) => employee.id === employeeId);

  const resetForm = () => {
    setStartTime('');
    setEndTime('');
    setNote('');
    setSelectedTaskId(null);
    setEditingEntryId(null);
  };

  const handleSubmit = () => {
    const snappedStart = startTime ? snapTimeToIncrement(startTime) : null;
    const snappedEnd = endTime ? snapTimeToIncrement(endTime) : null;
    const computedHours =
      snappedStart && snappedEnd ? computeHoursFromTimes(snappedStart, snappedEnd) : null;
    if (
      !employeeId ||
      !date ||
      computedHours === null ||
      computedHours <= 0 ||
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
      startTime: snappedStart,
      endTime: snappedEnd,
      hours: computedHours,
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
    removeTimeEntry(editingEntryId);
    resetForm();
  };

  const handleDeleteEntry = (id: string) => {
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
        <p className={styles.subtitle}>Log hours and review them by day, week, or month.</p>
      </div>

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
              You can only log time for yourself and people who report to you.
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
