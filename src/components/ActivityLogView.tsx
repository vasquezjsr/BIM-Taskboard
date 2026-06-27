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
import { canManageColumns, canViewActivityLog } from '../utils/permissions';
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
  const restoreDeletedColumn = useStore((s) => s.restoreDeletedColumn);

  const viewerId = viewAsOriginalUserId ?? currentUserId;
  const canView = canViewActivityLog(viewerId, employees, employeePermissions);
  const canRestore = canManageColumns(viewerId, employees, employeePermissions);

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

  const restorableArchiveIds = useMemo(() => {
    const map = new Map<string, boolean>();
    for (const archive of deletedColumnArchive) {
      map.set(archive.id, !archive.restoredAt);
    }
    return map;
  }, [deletedColumnArchive]);

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
            Track creates, updates, deletes, status changes, and column changes across BIM
            Boardroom. Column deletions can be restored here by authorized users.
          </p>
        </div>
        <div className={styles.stats}>
          <span>{activityLog.length} events</span>
          <span>{deletedColumnArchive.filter((entry) => !entry.restoredAt).length} restorable columns</span>
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
          filteredEntries.map((entry) => (
            <ActivityRow
              key={entry.id}
              entry={entry}
              actor={actorName(entry.actorId, employees)}
              canRestore={canRestore}
              canRestoreArchive={Boolean(entry.archiveId && restorableArchiveIds.get(entry.archiveId))}
              onRestore={() => entry.archiveId && restoreDeletedColumn(entry.archiveId)}
            />
          ))
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
  onRestore,
}: {
  entry: ActivityLogEntry;
  actor: string;
  canRestore: boolean;
  canRestoreArchive: boolean;
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
          Restore column
        </button>
      )}
      {entry.restoredAt && (
        <span className={styles.restoredTag}>Restored</span>
      )}
    </article>
  );
}
