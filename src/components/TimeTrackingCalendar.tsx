import { useEffect, useMemo, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { ContextMenuPanel } from './ContextMenuPanel';
import type { Client, Employee, Project, Task, TimeEntry } from '../types';
import { employeeNameById } from '../utils/orgChart';
import {
  formatEntryTimeRange,
  getEntryTaskLabel,
  isOpenTimeEntry,
} from '../utils/timeEntry';
import {
  type CalendarView,
  entriesInRange,
  formatCalendarPeriodLabel,
  formatDayNumber,
  getMonthGrid,
  getViewRange,
  getWeekDates,
  groupEntriesByDate,
  hoursForDate,
  sumHours,
  todayIsoDate,
  weekdayLabels,
} from '../utils/timeCalendar';
import styles from './TimeTrackingCalendar.module.css';

interface TimeTrackingCalendarProps {
  entries: TimeEntry[];
  employees: Employee[];
  clients: Client[];
  projects: Project[];
  tasks: Task[];
  focusDate: string;
  view: CalendarView;
  hiddenEntryCount: number;
  editingEntryId: string | null;
  onFocusDateChange: (iso: string) => void;
  onViewChange: (view: CalendarView) => void;
  onNavigate: (delta: -1 | 1) => void;
  onEditEntry: (entry: TimeEntry) => void;
  onDeleteEntry: (id: string) => void;
}

export function TimeTrackingCalendar({
  entries,
  employees,
  clients,
  projects,
  tasks,
  focusDate,
  view,
  hiddenEntryCount,
  editingEntryId,
  onFocusDateChange,
  onViewChange,
  onNavigate,
  onEditEntry,
  onDeleteEntry,
}: TimeTrackingCalendarProps) {
  const today = todayIsoDate();
  const range = getViewRange(focusDate, view);

  const rangeEntries = useMemo(
    () => entriesInRange(entries, range.start, range.end),
    [entries, range.end, range.start]
  );

  const entriesByDate = useMemo(() => groupEntriesByDate(entries), [entries]);
  const rangeHours = sumHours(rangeEntries);

  const clientName = (id: string | null) => clients.find((client) => client.id === id)?.name ?? '—';
  const projectName = (id: string | null) => projects.find((project) => project.id === id)?.name ?? '—';

  const handleSelectDay = (iso: string) => {
    onFocusDateChange(iso);
    if (view !== 'day') onViewChange('day');
  };

  const [entryContextMenu, setEntryContextMenu] = useState<{
    entry: TimeEntry;
    x: number;
    y: number;
  } | null>(null);

  useEffect(() => {
    if (!entryContextMenu) return;
    const close = () => setEntryContextMenu(null);
    // Defer listeners so the click that opens the menu (and the Delete click) aren't swallowed.
    const timer = window.setTimeout(() => {
      window.addEventListener('click', close);
      window.addEventListener('contextmenu', close);
      window.addEventListener('scroll', close, true);
    }, 0);
    return () => {
      window.clearTimeout(timer);
      window.removeEventListener('click', close);
      window.removeEventListener('contextmenu', close);
      window.removeEventListener('scroll', close, true);
    };
  }, [entryContextMenu]);

  const handleEntryContextMenu = (entry: TimeEntry, e: ReactMouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setEntryContextMenu({ entry, x: e.clientX, y: e.clientY });
  };

  const handleDeleteFromMenu = (e: ReactMouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (!entryContextMenu) return;
    const entryId = entryContextMenu.entry.id;
    setEntryContextMenu(null);
    onDeleteEntry(entryId);
  };

  const renderDayCell = (iso: string, inMonth = true) => {
    const dayEntries = entriesByDate.get(iso) ?? [];
    const dayHours = sumHours(dayEntries);
    const hasOpenEntry = dayEntries.some(isOpenTimeEntry);
    const isToday = iso === today;
    const isSelected = iso === focusDate;
    const visibleEntries = dayEntries.slice(0, 3);
    const hiddenCount = dayEntries.length - visibleEntries.length;

    return (
      <div
        key={iso}
        className={`${styles.dayCell} ${!inMonth ? styles.dayCellOutside : ''} ${
          isToday ? styles.dayCellToday : ''
        } ${isSelected ? styles.dayCellSelected : ''} ${
          dayHours > 0 || hasOpenEntry ? styles.dayCellHasHours : ''
        }`}
      >
        <button
          type="button"
          className={styles.dayNumberBtn}
          onClick={() => handleSelectDay(iso)}
        >
          {formatDayNumber(iso)}
        </button>
        {dayEntries.length > 0 && (
          <div className={styles.dayEntryList}>
            {visibleEntries.map((entry) => (
              <button
                key={entry.id}
                type="button"
                className={`${styles.dayEntryChip} ${
                  editingEntryId === entry.id ? styles.dayEntryChipActive : ''
                } ${isOpenTimeEntry(entry) ? styles.dayEntryChipOpen : ''}`}
                onClick={() => onEditEntry(entry)}
                onContextMenu={(e) => handleEntryContextMenu(entry, e)}
              >
                <span className={styles.dayEntryTask}>{getEntryTaskLabel(entry, tasks)}</span>
                <span className={styles.dayEntryTime}>{formatEntryTimeRange(entry)}</span>
              </button>
            ))}
            {hiddenCount > 0 && (
              <button
                type="button"
                className={styles.dayEntryMore}
                onClick={() => handleSelectDay(iso)}
              >
                +{hiddenCount} more
              </button>
            )}
          </div>
        )}
        {dayHours > 0 && dayEntries.length > 1 && (
          <span className={styles.dayHoursTotal}>{dayHours.toLocaleString()}h total</span>
        )}
      </div>
    );
  };

  const renderDailyEntries = () => {
    const dayEntries = entriesByDate.get(focusDate) ?? [];

    if (dayEntries.length === 0) {
      return <div className={styles.emptyDay}>No time logged for this day.</div>;
    }

    return (
      <div className={styles.tableWrap}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Employee</th>
              <th>Task</th>
              <th>Time</th>
              <th>Hours</th>
              <th>Client</th>
              <th>Project</th>
              <th>Note</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {dayEntries.map((entry) => (
              <tr
                key={entry.id}
                className={`${styles.entryRow} ${
                  editingEntryId === entry.id ? styles.entryRowActive : ''
                }`}
                onClick={() => onEditEntry(entry)}
                onContextMenu={(e) => handleEntryContextMenu(entry, e)}
              >
                <td>{employeeNameById(employees, entry.employeeId)}</td>
                <td>{getEntryTaskLabel(entry, tasks)}</td>
                <td>{formatEntryTimeRange(entry)}</td>
                <td className={styles.hoursCell}>
                  {isOpenTimeEntry(entry) ? '—' : entry.hours.toLocaleString()}
                </td>
                <td>{clientName(entry.clientId)}</td>
                <td>{projectName(entry.projectId)}</td>
                <td>{entry.note || '—'}</td>
                <td>
                  <button
                    type="button"
                    className={styles.deleteBtn}
                    onClick={(e) => {
                      e.stopPropagation();
                      onDeleteEntry(entry.id);
                    }}
                  >
                    Delete
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  };

  return (
    <section className={styles.calendarCard}>
      <div className={styles.toolbar}>
        <div className={styles.viewTabs}>
          {(['day', 'week', 'month'] as const).map((option) => (
            <button
              key={option}
              type="button"
              className={`${styles.viewTab} ${view === option ? styles.viewTabActive : ''}`}
              onClick={() => onViewChange(option)}
            >
              {option === 'day' ? 'Daily' : option === 'week' ? 'Weekly' : 'Monthly'}
            </button>
          ))}
        </div>

        <div className={styles.nav}>
          <button type="button" className={styles.navBtn} onClick={() => onNavigate(-1)} aria-label="Previous">
            ‹
          </button>
          <button
            type="button"
            className={styles.todayBtn}
            onClick={() => onFocusDateChange(todayIsoDate())}
          >
            Today
          </button>
          <button type="button" className={styles.navBtn} onClick={() => onNavigate(1)} aria-label="Next">
            ›
          </button>
        </div>
      </div>

      <div className={styles.periodHeader}>
        <h3 className={styles.periodTitle}>{formatCalendarPeriodLabel(focusDate, view)}</h3>
        <span className={styles.periodSummary}>{rangeHours.toLocaleString()} hrs in view</span>
      </div>

      {hiddenEntryCount > 0 && (
        <p className={styles.restrictedNote}>
          {hiddenEntryCount.toLocaleString()} entr{hiddenEntryCount === 1 ? 'y' : 'ies'} hidden based on
          reporting lines.
        </p>
      )}

      {view === 'day' && (
        <div className={styles.dayView}>
          <div className={styles.daySummary}>
            <span className={styles.daySummaryLabel}>Total</span>
            <span className={styles.daySummaryHours}>
              {hoursForDate(entriesByDate, focusDate).toLocaleString()} hrs
            </span>
          </div>
          {renderDailyEntries()}
        </div>
      )}

      {view === 'week' && (
        <div className={styles.weekView}>
          <div className={styles.weekdayRow}>
            {weekdayLabels().map((label) => (
              <span key={label} className={styles.weekdayLabel}>
                {label}
              </span>
            ))}
          </div>
          <div className={styles.weekGrid}>
            {getWeekDates(focusDate).map((iso) => renderDayCell(iso))}
          </div>
        </div>
      )}

      {view === 'month' && (
        <div className={styles.monthView}>
          <div className={styles.weekdayRow}>
            {weekdayLabels().map((label) => (
              <span key={label} className={styles.weekdayLabel}>
                {label}
              </span>
            ))}
          </div>
          <div className={styles.monthGrid}>
            {getMonthGrid(focusDate).map((cell) => renderDayCell(cell.iso, cell.inMonth))}
          </div>
        </div>
      )}

      {entryContextMenu && (
        <ContextMenuPanel
          key={`entry-${entryContextMenu.x}-${entryContextMenu.y}`}
          x={entryContextMenu.x}
          y={entryContextMenu.y}
          className={styles.contextMenu}
          onClick={(e) => e.stopPropagation()}
          onMouseDown={(e) => e.stopPropagation()}
        >
          <button
            type="button"
            className={styles.contextMenuDelete}
            onMouseDown={(e) => e.stopPropagation()}
            onClick={handleDeleteFromMenu}
          >
            Delete entry
          </button>
        </ContextMenuPanel>
      )}
    </section>
  );
}
