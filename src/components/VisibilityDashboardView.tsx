import { useEffect, useMemo, useState } from 'react';
import { useStore } from '../store/useStore';
import { ORG_CATEGORIES, type OrgCategory } from '../types';
import {
  DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS,
  orgCategoryLabel,
} from '../utils/orgChart';
import { canManageOrg, canViewVisibilityDashboard } from '../utils/permissions';
import {
  ALWAYS_VISIBLE_NAV_TABS,
  DASHBOARD_VISIBILITY_COLUMNS,
  PERMISSION_COLUMNS,
  buildJobLevelVisibilityRows,
  buildLiveRosterVisibilityRows,
  groupJobLevelVisibilityByDepartment,
  navColumnLockReason,
  normalizeJobLevelNavVisibility,
  DEFAULT_JOB_LEVEL_VISIBILITY_ROWS,
  type JobLevelNavVisibilityMap,
  type VisibilityMatrixRow,
  type VisibilityNavColumn,
} from '../utils/visibilityMatrix';
import styles from './VisibilityDashboardView.module.css';

type MatrixColumn = {
  id: VisibilityNavColumn;
  label: string;
  tooltip: string;
  kind: 'permission' | 'dashboard';
};

type EditSection = 'visibility' | 'roster' | 'access' | null;

type RosterDraft = Record<string, Record<VisibilityNavColumn, boolean>>;

function PencilIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" aria-hidden>
      <path
        d="M12 20h9"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <path
        d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

function cloneJobLevelDraft(map: JobLevelNavVisibilityMap): JobLevelNavVisibilityMap {
  const normalized = normalizeJobLevelNavVisibility(map);
  return Object.fromEntries(
    Object.entries(normalized).map(([rowId, cells]) => [rowId, { ...cells }])
  );
}

function buildRosterDraft(rows: VisibilityMatrixRow[]): RosterDraft {
  return Object.fromEntries(
    rows.map((row) => [
      row.id,
      Object.fromEntries(
        PERMISSION_COLUMNS.map((column) => [column.id, Boolean(row.cells[column.id])])
      ) as Record<VisibilityNavColumn, boolean>,
    ])
  );
}

function applyRosterDraft(
  rows: VisibilityMatrixRow[],
  draft: RosterDraft | null
): VisibilityMatrixRow[] {
  if (!draft) return rows;
  return rows.map((row) => {
    const overrides = draft[row.id];
    if (!overrides) return row;
    return {
      ...row,
      cells: { ...row.cells, ...overrides },
    };
  });
}

function SectionEditControls({
  canEdit,
  editing,
  onEdit,
  onSave,
  onCancel,
}: {
  canEdit: boolean;
  editing: boolean;
  onEdit: () => void;
  onSave: () => void;
  onCancel: () => void;
}) {
  if (!canEdit) return null;
  if (!editing) {
    return (
      <button type="button" className={styles.editButton} onClick={onEdit} title="Edit section">
        <PencilIcon />
        <span>Edit</span>
      </button>
    );
  }
  return (
    <div className={styles.sectionActions}>
      <button type="button" className={styles.cancelButton} onClick={onCancel}>
        Cancel
      </button>
      <button type="button" className={styles.saveButton} onClick={onSave}>
        Save
      </button>
    </div>
  );
}

function MatrixTable({
  rows,
  columns,
  rowHeaderLabel = 'Job level / person',
  editable,
  onToggle,
  lockReasonForCell,
}: {
  rows: VisibilityMatrixRow[];
  columns: readonly MatrixColumn[];
  rowHeaderLabel?: string;
  editable: boolean;
  onToggle?: (rowId: string, column: VisibilityNavColumn, enabled: boolean) => void;
  lockReasonForCell?: (rowId: string, column: VisibilityNavColumn) => string;
}) {
  return (
    <div className={styles.tableWrap}>
      <table className={styles.matrix}>
        <thead>
          <tr>
            <th
              scope="col"
              className={styles.rowHeader}
              title="Person or job level this row applies to"
            >
              {rowHeaderLabel}
            </th>
            {columns.map((column) => (
              <th key={column.id} scope="col" title={column.tooltip} className={styles.colHeader}>
                <span className={styles.colHeaderLabel}>{column.label}</span>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}>
              <td>{row.label}</td>
              {columns.map((column) => {
                const checked = row.cells[column.id];
                const lockReason = lockReasonForCell?.(row.id, column.id) ?? '';
                const locked = Boolean(lockReason) && checked;
                if (!editable || !onToggle) {
                  return (
                    <td key={column.id}>
                      {checked ? (
                        <span className={styles.check} aria-label="Enabled" title={lockReason || undefined}>
                          ✓
                        </span>
                      ) : (
                        <span className={styles.dash} aria-hidden>
                          ·
                        </span>
                      )}
                    </td>
                  );
                }
                return (
                  <td key={column.id}>
                    <label
                      className={`${styles.cellCheck} ${locked ? styles.cellCheckLocked : ''}`}
                      title={
                        locked
                          ? lockReason
                          : `${checked ? 'Revoke' : 'Grant'} ${column.label} for ${row.label}`
                      }
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        disabled={locked}
                        onChange={(event) => onToggle(row.id, column.id, event.target.checked)}
                        aria-label={`${column.label} for ${row.label}`}
                      />
                    </label>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Access Control — tab visibility by role, then per-person edit permissions. */
export function VisibilityDashboardView() {
  const currentUserId = useStore((s) => s.currentUserId);
  const viewAsOriginalUserId = useStore((s) => s.viewAsOriginalUserId);
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const visibilityDashboardJobLevels = useStore((s) => s.visibilityDashboardJobLevels);
  const jobLevelNavVisibility = useStore((s) => s.jobLevelNavVisibility);
  const setVisibilityDashboardJobLevel = useStore((s) => s.setVisibilityDashboardJobLevel);
  const setEmployeeNavVisibility = useStore((s) => s.setEmployeeNavVisibility);
  const setJobLevelNavVisibility = useStore((s) => s.setJobLevelNavVisibility);

  const [editingSection, setEditingSection] = useState<EditSection>(null);
  const [draftJobLevel, setDraftJobLevel] = useState<JobLevelNavVisibilityMap | null>(null);
  const [draftRoster, setDraftRoster] = useState<RosterDraft | null>(null);
  const [draftAccessLevels, setDraftAccessLevels] = useState<OrgCategory[] | null>(null);

  const perspectiveUserId = currentUserId;
  const realUserId = viewAsOriginalUserId ?? currentUserId;
  const canView = canViewVisibilityDashboard(
    perspectiveUserId,
    employees,
    employeePermissions,
    visibilityDashboardJobLevels
  );
  const canEditAccess = canManageOrg(realUserId, employees, employeePermissions);
  const isViewAsPreview = viewAsOriginalUserId != null;
  const readOnlyHint = isViewAsPreview
    ? 'Read-only while View As is on — exit View As to edit Access Control.'
    : 'Read-only — you need Manage org & permissions to edit.';

  useEffect(() => {
    if (!canEditAccess && editingSection) {
      setEditingSection(null);
      setDraftJobLevel(null);
      setDraftRoster(null);
      setDraftAccessLevels(null);
    }
  }, [canEditAccess, editingSection]);

  const committedJobLevel = useMemo(
    () => normalizeJobLevelNavVisibility(jobLevelNavVisibility),
    [jobLevelNavVisibility]
  );

  const defaultRows = useMemo(
    () => buildJobLevelVisibilityRows(draftJobLevel ?? committedJobLevel),
    [draftJobLevel, committedJobLevel]
  );

  const departmentGroups = useMemo(
    () => groupJobLevelVisibilityByDepartment(defaultRows),
    [defaultRows]
  );

  const liveRowsCommitted = useMemo(
    () =>
      buildLiveRosterVisibilityRows(employees, employeePermissions, visibilityDashboardJobLevels),
    [employees, employeePermissions, visibilityDashboardJobLevels]
  );

  const liveRows = useMemo(
    () => applyRosterDraft(liveRowsCommitted, draftRoster),
    [liveRowsCommitted, draftRoster]
  );

  const accessLevels = draftAccessLevels ?? visibilityDashboardJobLevels;

  const employeeById = useMemo(() => {
    const map = new Map(employees.map((employee) => [employee.id, employee]));
    return map;
  }, [employees]);

  const cancelEditing = () => {
    setEditingSection(null);
    setDraftJobLevel(null);
    setDraftRoster(null);
    setDraftAccessLevels(null);
  };

  const beginEdit = (section: Exclude<EditSection, null>) => {
    setEditingSection(section);
    setDraftJobLevel(section === 'visibility' ? cloneJobLevelDraft(committedJobLevel) : null);
    setDraftRoster(section === 'roster' ? buildRosterDraft(liveRowsCommitted) : null);
    setDraftAccessLevels(
      section === 'access' ? [...visibilityDashboardJobLevels] : null
    );
  };

  const saveVisibility = () => {
    if (!draftJobLevel) {
      cancelEditing();
      return;
    }
    for (const row of DEFAULT_JOB_LEVEL_VISIBILITY_ROWS) {
      for (const column of DASHBOARD_VISIBILITY_COLUMNS) {
        const next = Boolean(draftJobLevel[row.id]?.[column.id]);
        const prev = Boolean(committedJobLevel[row.id]?.[column.id]);
        if (next !== prev) {
          setJobLevelNavVisibility(row.id, column.id, next);
        }
      }
    }
    cancelEditing();
  };

  const saveRoster = () => {
    if (!draftRoster) {
      cancelEditing();
      return;
    }
    for (const row of liveRowsCommitted) {
      const nextCells = draftRoster[row.id];
      if (!nextCells) continue;
      const employee = employeeById.get(row.id);
      for (const column of PERMISSION_COLUMNS) {
        if (employee) {
          const lock = navColumnLockReason(column.id, employee, visibilityDashboardJobLevels);
          if (lock && row.cells[column.id]) continue;
        }
        const next = Boolean(nextCells[column.id]);
        const prev = Boolean(row.cells[column.id]);
        if (next !== prev) {
          setEmployeeNavVisibility(row.id, column.id, next);
        }
      }
    }
    cancelEditing();
  };

  const saveAccess = () => {
    if (!draftAccessLevels) {
      cancelEditing();
      return;
    }
    const next = new Set(draftAccessLevels);
    const prev = new Set(visibilityDashboardJobLevels);
    for (const category of ORG_CATEGORIES) {
      const enabled = next.has(category.id);
      const was = prev.has(category.id);
      if (enabled !== was) {
        setVisibilityDashboardJobLevel(category.id, enabled);
      }
    }
    cancelEditing();
  };

  if (!canView) {
    return (
      <div className={styles.page}>
        <div className={styles.emptyState}>
          <h2>Access Control</h2>
          <p>
            Only people with Access Control permission can open this page. Ask an Owner, BIM
            Manager, or Operations Manager to grant access.
          </p>
        </div>
      </div>
    );
  }

  const editingVisibility = editingSection === 'visibility';
  const editingRoster = editingSection === 'roster';
  const editingAccess = editingSection === 'access';

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <h1 className={styles.title}>Access Control</h1>
        <p className={styles.subtitle}>
          Choose which dashboards each role can <strong>see</strong>, then grant who can{' '}
          <strong>edit</strong> on the live roster below. Use <strong>Edit</strong> on a section to
          make changes, then <strong>Save</strong> or <strong>Cancel</strong>.
        </p>
      </header>

      <p className={styles.alwaysOn}>
        <strong>Always visible to everyone:</strong> {ALWAYS_VISIBLE_NAV_TABS.join(', ')}. Tab
        visibility is by role above; edit rights are per person below. Changing either needs{' '}
        <strong>Manage org & permissions</strong>.
      </p>

      <section className={styles.section}>
        <div className={styles.sectionHeader}>
          <div>
            <h2 className={styles.sectionTitle}>Dashboard visibility by department</h2>
            <p className={styles.sectionHint}>
              {!canEditAccess
                ? readOnlyHint
                : editingVisibility
                  ? 'Editing — checkboxes are writable. Save to apply to everyone in each role, or Cancel to discard.'
                  : 'Visibility only — whether this role can open each tab (not edit inside it). Click Edit to change.'}
            </p>
          </div>
          <SectionEditControls
            canEdit={canEditAccess}
            editing={editingVisibility}
            onEdit={() => beginEdit('visibility')}
            onSave={saveVisibility}
            onCancel={cancelEditing}
          />
        </div>
        <div className={styles.departmentStack}>
          {departmentGroups.map(({ department, rows }) => (
            <div key={department.id} className={styles.departmentCard}>
              <div className={styles.departmentHeader}>
                <h3 className={styles.departmentTitle}>{department.label}</h3>
                <p className={styles.departmentHint}>{department.description}</p>
              </div>
              <MatrixTable
                rows={rows}
                columns={DASHBOARD_VISIBILITY_COLUMNS}
                rowHeaderLabel="Role"
                editable={editingVisibility}
                onToggle={(rowId, column, enabled) => {
                  setDraftJobLevel((current) => {
                    const base = cloneJobLevelDraft(current ?? committedJobLevel);
                    base[rowId] = { ...(base[rowId] ?? {}), [column]: enabled };
                    return base;
                  });
                }}
              />
            </div>
          ))}
        </div>
      </section>

      <section className={styles.section}>
        <div className={styles.sectionHeader}>
          <div>
            <h2 className={styles.sectionTitle}>Live roster — permission to edit</h2>
            <p className={styles.sectionHint}>
              {!canEditAccess
                ? readOnlyHint
                : editingRoster
                  ? 'Editing — greyed boxes are locked by role (Owner / BIM Manager keep those rights). Hover a locked box for why.'
                  : 'Edit permissions only — budget, org, columns, and each dashboard/board action (PM assigns, Shop, time, clients, tasks, statuses, columns). Tab visibility is set by role above. Click Edit to change.'}
            </p>
          </div>
          <SectionEditControls
            canEdit={canEditAccess}
            editing={editingRoster}
            onEdit={() => beginEdit('roster')}
            onSave={saveRoster}
            onCancel={cancelEditing}
          />
        </div>
        <MatrixTable
          rows={liveRows}
          columns={PERMISSION_COLUMNS}
          rowHeaderLabel="Person"
          editable={editingRoster}
          onToggle={(employeeId, column, enabled) => {
            setDraftRoster((current) => {
              const base = current ? { ...current } : buildRosterDraft(liveRowsCommitted);
              base[employeeId] = { ...(base[employeeId] ?? {}), [column]: enabled };
              return { ...base };
            });
          }}
          lockReasonForCell={(employeeId, column) => {
            const employee = employeeById.get(employeeId);
            if (!employee) return '';
            return navColumnLockReason(column, employee, visibilityDashboardJobLevels);
          }}
        />
      </section>

      <section className={styles.section}>
        <div className={styles.accessPanel}>
          <div className={styles.sectionHeader}>
            <div>
              <h2 className={styles.accessTitle}>Who can open Access Control</h2>
              <p className={styles.accessHint}>
                Job levels below get Access Control automatically (also mirrored when you toggle the
                Access Control column on matching department rows above). Default:{' '}
                {DEFAULT_VISIBILITY_DASHBOARD_JOB_LEVELS.map((id) => orgCategoryLabel(id)).join(', ')}.
              </p>
            </div>
            <SectionEditControls
              canEdit={canEditAccess}
              editing={editingAccess}
              onEdit={() => beginEdit('access')}
              onSave={saveAccess}
              onCancel={cancelEditing}
            />
          </div>

          {!canEditAccess ? (
            <p className={styles.lockedNote}>
              You can view Access Control, but changing who can open it requires Manage org &
              permissions.
            </p>
          ) : editingAccess ? (
            <div className={styles.jobLevelGrid}>
              {ORG_CATEGORIES.map((category) => {
                const checked = accessLevels.includes(category.id);
                return (
                  <label key={category.id} className={styles.jobLevelOption}>
                    <input
                      type="checkbox"
                      checked={checked}
                      onChange={(event) => {
                        const enabled = event.target.checked;
                        setDraftAccessLevels((current) => {
                          const base = new Set(current ?? visibilityDashboardJobLevels);
                          if (enabled) base.add(category.id);
                          else base.delete(category.id);
                          return [...base];
                        });
                      }}
                    />
                    <span>{category.label}</span>
                  </label>
                );
              })}
            </div>
          ) : (
            <div className={styles.jobLevelGrid}>
              {ORG_CATEGORIES.map((category) => {
                const checked = accessLevels.includes(category.id);
                return (
                  <div key={category.id} className={styles.jobLevelReadonly}>
                    <span className={checked ? styles.check : styles.dash} aria-hidden>
                      {checked ? '✓' : '·'}
                    </span>
                    <span>{category.label}</span>
                  </div>
                );
              })}
            </div>
          )}

          <p className={styles.footerNote}>
            Tip: Keep Owner selected so owners never lose this page. Use the Access Control column in
            Dashboard visibility by department to open the tab for a whole role.
          </p>
        </div>
      </section>
    </div>
  );
}
