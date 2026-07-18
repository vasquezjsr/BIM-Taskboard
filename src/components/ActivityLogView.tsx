import { useMemo, useState } from 'react';
import { useStore } from '../store/useStore';
import {
  activityActionLabel,
  activityEntityLabel,
  formatActivityTimestamp,
  type ActivityAction,
  type ActivityLogEntry,
} from '../utils/activityLog';
import { employeeNameById } from '../utils/orgChart';
import { canManageColumns, canManageOrg, canViewActivityLog } from '../utils/permissions';
import styles from './ActivityLogView.module.css';

const ACTION_FILTERS: Array<{ id: 'all' | ActivityAction; label: string }> = [
  { id: 'all', label: 'All activity' },
  { id: 'created', label: 'Created' },
  { id: 'updated', label: 'Updated' },
  { id: 'deleted', label: 'Deleted' },
  { id: 'restored', label: 'Restored' },
  { id: 'status_changed', label: 'Status changes' },
];

function actorName(actorId: string | null, employees: ReturnType<typeof useStore.getState>['employees']) {
  if (!actorId) return 'System';
  return employeeNameById(employees, actorId);
}

export function ActivityLogView() {
  const currentUserId = useStore((s) => s.currentUserId);
  const viewAsOriginalUserId = useStore((s) => s.viewAsOriginalUserId);
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const activityLog = useStore((s) => s.activityLog ?? []);
  const deletedColumnArchive = useStore((s) => s.deletedColumnArchive ?? []);
  const deletedEmployeeArchive = useStore((s) => s.deletedEmployeeArchive ?? []);
  const restoreDeletedColumn = useStore((s) => s.restoreDeletedColumn);
  const restoreDeletedEmployee = useStore((s) => s.restoreDeletedEmployee);

  // View As: page content follows the person being previewed (same as MainNav).
  // Restore actions stay on the real signed-in user so preview can't escalate privileges.
  const perspectiveUserId = currentUserId;
  const realUserId = viewAsOriginalUserId ?? currentUserId;
  const canView = canViewActivityLog(perspectiveUserId, employees, employeePermissions);
  const canRestoreColumns = canManageColumns(realUserId, employees, employeePermissions);
  const canRestoreEmployees = canManageOrg(realUserId, employees, employeePermissions);

  const [actionFilter, setActionFilter] = useState<'all' | ActivityAction>('all');
  const [search, setSearch] = useState('');

  const filteredEntries = useMemo(() => {
    const query = search.trim().toLowerCase();
    return activityLog.filter((entry) => {
      if (actionFilter !== 'all' && entry.action !== actionFilter) return false;
      if (!query) return true;
      const haystack = [
        entry.summary,
        activityActionLabel(entry.action),
        activityEntityLabel(entry.entityType),
        actorName(entry.actorId, employees),
        entry.entityId,
      ]
        .join(' ')
        .toLowerCase();
      return haystack.includes(query);
    });
  }, [actionFilter, activityLog, employees, search]);

  const restorableColumnArchiveIds = useMemo(() => {
    const map = new Map<string, boolean>();
    for (const archive of deletedColumnArchive) {
      map.set(archive.id, !archive.restoredAt);
    }
    return map;
  }, [deletedColumnArchive]);

  const restorableEmployeeArchiveIds = useMemo(() => {
    const map = new Map<string, boolean>();
    for (const archive of deletedEmployeeArchive) {
      const alreadyOnRoster = employees.some((employee) => employee.id === archive.employee.id);
      map.set(archive.id, !archive.restoredAt && !alreadyOnRoster);
    }
    return map;
  }, [deletedEmployeeArchive, employees]);

  const restorableEmployeeCount = deletedEmployeeArchive.filter((entry) => {
    if (entry.restoredAt) return false;
    return !employees.some((employee) => employee.id === entry.employee.id);
  }).length;

  if (!canView) {
    return (
      <div className={styles.page}>
        <div className={styles.emptyState}>
          <h2>Activity Log</h2>
          <p>
            Only the Owner, BIM Manager, Operations Manager, and employees granted Activity Log
            access can view this page.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <div>
          <h2 className={styles.title}>Activity Log</h2>
          <p className={styles.subtitle}>
            Track creates, updates, deletes, and restores across BIM Boardroom. Deleted columns and
            employees can be restored here by authorized users.
          </p>
        </div>
        <div className={styles.stats}>
          <span>{activityLog.length} events</span>
          <span>
            {deletedColumnArchive.filter((entry) => !entry.restoredAt).length} restorable columns
          </span>
          <span>{restorableEmployeeCount} restorable employees</span>
        </div>
      </header>

      <div className={styles.toolbar}>
        <input
          type="search"
          className={styles.search}
          placeholder="Search activity…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <div className={styles.filters}>
          {ACTION_FILTERS.map((filter) => (
            <button
              key={filter.id}
              type="button"
              className={`${styles.filterBtn}${actionFilter === filter.id ? ` ${styles.filterBtnActive}` : ''}`}
              onClick={() => setActionFilter(filter.id)}
            >
              {filter.label}
            </button>
          ))}
        </div>
      </div>

      <div className={styles.list}>
        {filteredEntries.length === 0 ? (
          <div className={styles.emptyList}>No activity matches your filters.</div>
        ) : (
          filteredEntries.map((entry) => {
            const isEmployee = entry.entityType === 'employee';
            const isColumn = entry.entityType === 'column';
            const canRestoreArchive = Boolean(
              entry.archiveId &&
                ((isEmployee && restorableEmployeeArchiveIds.get(entry.archiveId)) ||
                  (isColumn && restorableColumnArchiveIds.get(entry.archiveId)))
            );
            const canRestore =
              (isEmployee && canRestoreEmployees) || (isColumn && canRestoreColumns);

            return (
              <ActivityRow
                key={entry.id}
                entry={entry}
                actor={actorName(entry.actorId, employees)}
                canRestore={canRestore}
                canRestoreArchive={canRestoreArchive}
                restoreLabel={isEmployee ? 'Restore employee' : 'Restore column'}
                onRestore={() => {
                  if (!entry.archiveId) return;
                  if (isEmployee) restoreDeletedEmployee(entry.archiveId);
                  else if (isColumn) restoreDeletedColumn(entry.archiveId);
                }}
              />
            );
          })
        )}
      </div>
    </div>
  );
}

function ActivityRow({
  entry,
  actor,
  canRestore,
  canRestoreArchive,
  restoreLabel,
  onRestore,
}: {
  entry: ActivityLogEntry;
  actor: string;
  canRestore: boolean;
  canRestoreArchive: boolean;
  restoreLabel: string;
  onRestore: () => void;
}) {
  return (
    <article className={styles.row}>
      <div className={styles.rowMeta}>
        <span className={`${styles.actionBadge} ${styles[`action_${entry.action}`]}`}>
          {activityActionLabel(entry.action)}
        </span>
        <span className={styles.entityBadge}>{activityEntityLabel(entry.entityType)}</span>
        <time className={styles.timestamp}>{formatActivityTimestamp(entry.timestamp)}</time>
      </div>
      <div className={styles.rowBody}>
        <p className={styles.summary}>{entry.summary}</p>
        <p className={styles.actor}>By {actor}</p>
        {entry.details && Object.keys(entry.details).length > 0 && (
          <dl className={styles.details}>
            {Object.entries(entry.details).map(([key, value]) => (
              <div key={key} className={styles.detailItem}>
                <dt>{key}</dt>
                <dd>{value == null ? '—' : String(value)}</dd>
              </div>
            ))}
          </dl>
        )}
      </div>
      {canRestore && canRestoreArchive && (
        <button type="button" className={styles.restoreBtn} onClick={onRestore}>
          {restoreLabel}
        </button>
      )}
      {entry.restoredAt && <span className={styles.restoredTag}>Restored</span>}
    </article>
  );
}
