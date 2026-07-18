import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  catalogOptionsForDropdownColumn,
  DEFAULT_COLUMN_SETTINGS_DROPDOWN_IDS,
  normalizeColumnSettingsDropdownIds,
  PREMADE_MATERIAL_COLUMN_ID,
  PREMADE_TRADE_COLUMN_ID,
  tabLabelForDropdownColumn,
} from '../data/premadeSheetColumns';
import { useStore } from '../store/useStore';
import { getBoardLabel, type ProjectBoardType, type SheetColumnDefinition } from '../types';
import {
  canAddColumns,
  canManageColumns,
  canManageMaterialOptions,
  canManageStatuses,
} from '../utils/permissions';
import { getBoardSheetColumns } from '../utils/sheetColumns';
import { BUILT_IN_BOARD_TYPES } from '../utils/taskStatuses';
import { ColumnSettings } from './ColumnSettings';
import { StatusSettings } from './StatusSettings';
import styles from './ColumnSettings.module.css';
import hubStyles from './BoardColumnSettingsHub.module.css';

export const OPEN_COLUMN_SETTINGS_EVENT = 'bim-open-column-settings';

export type ColumnSettingsHubTab = 'columns' | 'statuses' | `dropdown:${string}`;

export interface OpenColumnSettingsDetail {
  tab?: ColumnSettingsHubTab;
  dropdownColumnId?: string;
}

export function openColumnSettingsHub(detail?: OpenColumnSettingsDetail): void {
  window.dispatchEvent(
    new CustomEvent<OpenColumnSettingsDetail>(OPEN_COLUMN_SETTINGS_EVENT, {
      detail: detail ?? {},
    })
  );
}

type HubTab = ColumnSettingsHubTab;

interface BoardColumnSettingsHubProps {
  onClose: () => void;
  initialTab?: HubTab;
  initialDropdownColumnId?: string;
}

function findManagedColumn(
  columnId: string,
  boardSheetColumns: ReturnType<typeof useStore.getState>['boardSheetColumns']
): SheetColumnDefinition | undefined {
  return (
    getBoardSheetColumns('main', boardSheetColumns).find((column) => column.id === columnId) ??
    getBoardSheetColumns('detailers', boardSheetColumns).find((column) => column.id === columnId) ??
    Object.values(boardSheetColumns)
      .flatMap((columns) => columns ?? [])
      .find((column) => column.id === columnId)
  );
}

function DropdownOptionsPanel({ columnId }: { columnId: string }) {
  const updateBoardSheetColumn = useStore((s) => s.updateBoardSheetColumn);
  const updateTasksWith = useStore((s) => s.updateTasksWith);
  const tasks = useStore((s) => s.tasks);
  const boardSheetColumns = useStore((s) => s.boardSheetColumns);
  const removeColumnSettingsDropdown = useStore((s) => s.removeColumnSettingsDropdown);
  const column = useMemo(
    () => findManagedColumn(columnId, boardSheetColumns),
    [boardSheetColumns, columnId]
  );

  const catalog = catalogOptionsForDropdownColumn(columnId);
  const tabLabel = tabLabelForDropdownColumn(columnId, column?.label);
  const isBuiltIn = (DEFAULT_COLUMN_SETTINGS_DROPDOWN_IDS as readonly string[]).includes(
    columnId
  );

  // Prefer the saved column list so edits/removes stick. Fall back to catalog when empty.
  const options = useMemo(() => {
    const fromColumn = (column?.options ?? []).map((option) => option.trim()).filter(Boolean);
    if (fromColumn.length > 0) return fromColumn;
    return catalog.map((option) => option.trim()).filter(Boolean);
  }, [catalog, column]);

  const [draft, setDraft] = useState('');
  const [message, setMessage] = useState<string | null>(null);
  const [editingOption, setEditingOption] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState('');

  const canEditList = Boolean(column && column.type === 'dropdown');

  const persistOptions = (next: string[], successMessage?: string) => {
    if (!column) {
      setMessage(`Add the ${tabLabel} column first (Columns tab → Premade or Create column).`);
      return false;
    }
    updateBoardSheetColumn('main', columnId, {
      options: next,
    });
    setMessage(successMessage ?? `${tabLabel} list saved.`);
    return true;
  };

  const handleAdd = () => {
    const trimmed = draft.trim();
    if (!trimmed) return;
    const exists = options.some((option) => option.trim().toLowerCase() === trimmed.toLowerCase());
    if (exists) {
      setMessage(`“${trimmed}” is already in the list.`);
      return;
    }
    if (persistOptions([...options, trimmed], `Added “${trimmed}”.`)) {
      setDraft('');
    }
  };

  const handleRemove = (option: string) => {
    if (editingOption === option) {
      setEditingOption(null);
      setEditDraft('');
    }
    persistOptions(
      options.filter((entry) => entry !== option),
      `Removed “${option}”.`
    );
  };

  const startEdit = (option: string) => {
    setEditingOption(option);
    setEditDraft(option);
    setMessage(null);
  };

  const cancelEdit = () => {
    setEditingOption(null);
    setEditDraft('');
  };

  const commitEdit = () => {
    if (!editingOption) return;
    const trimmed = editDraft.trim();
    if (!trimmed) {
      setMessage('Option name cannot be empty.');
      return;
    }
    if (
      trimmed.toLowerCase() !== editingOption.toLowerCase() &&
      options.some((option) => option.trim().toLowerCase() === trimmed.toLowerCase())
    ) {
      setMessage(`“${trimmed}” is already in the list.`);
      return;
    }
    if (trimmed === editingOption) {
      cancelEdit();
      return;
    }

    const next = options.map((entry) => (entry === editingOption ? trimmed : entry));
    const previous = editingOption;
    if (!persistOptions(next, `Renamed “${previous}” to “${trimmed}”.`)) return;

    const affectedIds = tasks
      .filter((task) => (task.customFields?.[columnId] ?? null) === previous)
      .map((task) => task.id);
    if (affectedIds.length > 0) {
      updateTasksWith(affectedIds, () => ({
        customFields: { [columnId]: trimmed },
      }));
    }

    setEditingOption(null);
    setEditDraft('');
  };

  return (
    <div className={styles.body}>
      <p className={styles.intro}>
        Manage {tabLabel} dropdown choices used across boards. New values appear the next time you
        open a {column?.label ?? tabLabel} cell.
      </p>
      {!column && (
        <p className={hubStyles.warning}>
          The {tabLabel} column is not on your boards yet. Use the Columns tab to add it, then come
          back here.
        </p>
      )}
      {column && column.type !== 'dropdown' && (
        <p className={hubStyles.warning}>
          “{column.label}” is not a dropdown column. Change its cell type to Dropdown to manage
          choices here.
        </p>
      )}
      <label className={styles.field}>
        <span className={styles.fieldLabel}>Add option</span>
        <div className={hubStyles.addRow}>
          <input
            className={styles.textInput}
            value={draft}
            placeholder={
              columnId === PREMADE_TRADE_COLUMN_ID
                ? 'e.g. FP, MEC, PLMB'
                : columnId === PREMADE_MATERIAL_COLUMN_ID
                  ? 'e.g. Hangers, Sleeves, CS SCH40 WELD'
                  : 'e.g. Option name'
            }
            onChange={(event) => {
              setDraft(event.target.value);
              setMessage(null);
            }}
            onKeyDown={(event) => {
              if (event.key === 'Enter') handleAdd();
            }}
          />
          <button
            type="button"
            className={styles.addBtn}
            onClick={handleAdd}
            disabled={!draft.trim() || !canEditList}
          >
            Add
          </button>
        </div>
      </label>
      {message && <p className={hubStyles.message}>{message}</p>}
      <div className={styles.field}>
        <span className={styles.fieldLabel}>
          Current options ({options.length})
        </span>
        <ul className={hubStyles.optionList}>
          {options.map((option) => (
            <li key={option} className={hubStyles.optionRow}>
              {editingOption === option ? (
                <>
                  <input
                    className={`${styles.textInput} ${hubStyles.optionEditInput}`}
                    value={editDraft}
                    autoFocus
                    onChange={(event) => {
                      setEditDraft(event.target.value);
                      setMessage(null);
                    }}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter') {
                        event.preventDefault();
                        commitEdit();
                      }
                      if (event.key === 'Escape') {
                        event.preventDefault();
                        cancelEdit();
                      }
                    }}
                  />
                  <div className={hubStyles.optionActions}>
                    <button
                      type="button"
                      className={styles.addBtn}
                      onClick={commitEdit}
                      disabled={!editDraft.trim()}
                    >
                      Save
                    </button>
                    <button type="button" className={styles.linkBtn} onClick={cancelEdit}>
                      Cancel
                    </button>
                  </div>
                </>
              ) : (
                <>
                  <span>{option}</span>
                  <div className={hubStyles.optionActions}>
                    <button
                      type="button"
                      className={styles.linkBtn}
                      onClick={() => startEdit(option)}
                      disabled={!canEditList}
                      title={`Edit ${tabLabel} option`}
                    >
                      Edit
                    </button>
                    <button
                      type="button"
                      className={styles.linkBtn}
                      onClick={() => handleRemove(option)}
                      disabled={!canEditList}
                      title={`Remove from ${tabLabel} dropdown`}
                    >
                      Remove
                    </button>
                  </div>
                </>
              )}
            </li>
          ))}
        </ul>
      </div>
      {!isBuiltIn && (
        <div className={hubStyles.footerActions}>
          <button
            type="button"
            className={styles.linkBtn}
            onClick={() => removeColumnSettingsDropdown(columnId)}
          >
            Remove from Column Settings
          </button>
        </div>
      )}
    </div>
  );
}

export function BoardColumnSettingsHub({
  onClose,
  initialTab,
  initialDropdownColumnId,
}: BoardColumnSettingsHubProps) {
  const currentUserId = useStore((s) => s.currentUserId);
  const viewAsOriginalUserId = useStore((s) => s.viewAsOriginalUserId);
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const activeBoardType = useStore((s) => s.activeBoardType);
  const activeProjectId = useStore((s) => s.activeProjectId);
  const customBoards = useStore((s) => s.customBoards);
  const boardSheetColumns = useStore((s) => s.boardSheetColumns);
  const columnSettingsDropdownIds = useStore((s) => s.columnSettingsDropdownIds);

  const actorId = viewAsOriginalUserId ?? currentUserId;
  const allowColumns = canAddColumns(actorId, employees, employeePermissions);
  const allowManageColumns = canManageColumns(actorId, employees, employeePermissions);
  const allowDropdowns = canManageMaterialOptions(actorId, employees, employeePermissions);
  const allowStatuses = canManageStatuses(actorId, employees, employeePermissions);

  const managedDropdownIds = useMemo(
    () => normalizeColumnSettingsDropdownIds(columnSettingsDropdownIds),
    [columnSettingsDropdownIds]
  );

  const dropdownTabs = useMemo(
    () =>
      managedDropdownIds.map((columnId) => {
        const column = findManagedColumn(columnId, boardSheetColumns);
        return {
          columnId,
          label: tabLabelForDropdownColumn(columnId, column?.label),
          tab: `dropdown:${columnId}` as HubTab,
        };
      }),
    [boardSheetColumns, managedDropdownIds]
  );

  const defaultTab: HubTab = (() => {
    if (initialDropdownColumnId) return `dropdown:${initialDropdownColumnId}`;
    if (initialTab) return initialTab;
    if (allowColumns || allowManageColumns) return 'columns';
    if (allowDropdowns && dropdownTabs[0]) return dropdownTabs[0].tab;
    return 'statuses';
  })();

  const [tab, setTab] = useState<HubTab>(defaultTab);

  useEffect(() => {
    if (initialDropdownColumnId) {
      setTab(`dropdown:${initialDropdownColumnId}`);
    } else if (initialTab) {
      setTab(initialTab);
    }
  }, [initialDropdownColumnId, initialTab]);

  useEffect(() => {
    if (!tab.startsWith('dropdown:')) return;
    const columnId = tab.slice('dropdown:'.length);
    if (managedDropdownIds.includes(columnId)) return;
    if (allowColumns || allowManageColumns) setTab('columns');
    else if (managedDropdownIds[0]) setTab(`dropdown:${managedDropdownIds[0]}`);
    else if (allowStatuses) setTab('statuses');
  }, [tab, managedDropdownIds, allowColumns, allowManageColumns, allowStatuses]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [onClose]);

  const boards = useMemo(() => {
    const builtIn = BUILT_IN_BOARD_TYPES.filter((id) => id !== 'main').map((id) => ({
      id,
      label: getBoardLabel(id, customBoards),
    }));
    const custom = customBoards.map((board) => ({
      id: board.id as ProjectBoardType,
      label: board.name,
    }));
    return [
      { id: 'main' as ProjectBoardType, label: getBoardLabel('main', customBoards) },
      ...builtIn,
      ...custom,
    ];
  }, [customBoards]);

  const canOpen =
    allowColumns || allowManageColumns || allowDropdowns || allowStatuses;

  const activeDropdownColumnId =
    tab.startsWith('dropdown:') ? tab.slice('dropdown:'.length) : null;

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div
        className={`${styles.modal} ${hubStyles.hubModal}`}
        onClick={(event) => event.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="column-settings-hub-title"
      >
        <div className={styles.header}>
          <div>
            <h2 id="column-settings-hub-title">Column Settings</h2>
            <p className={styles.intro}>
              Add columns, manage Trade / Material choices, statuses, and other dropdown lists for
              your boards.
            </p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>

        {!canOpen ? (
          <div className={styles.body}>
            <p className={hubStyles.warning}>
              You do not have permission to manage columns, dropdown options, or statuses. Ask an
              Owner, BIM Manager, or Operations Manager.
            </p>
          </div>
        ) : (
          <>
            <div className={hubStyles.tabs} role="tablist" aria-label="Column settings sections">
              {(allowColumns || allowManageColumns) && (
                <button
                  type="button"
                  role="tab"
                  aria-selected={tab === 'columns'}
                  className={`${hubStyles.tab} ${tab === 'columns' ? hubStyles.tabActive : ''}`}
                  onClick={() => setTab('columns')}
                >
                  Columns
                </button>
              )}
              {allowDropdowns &&
                dropdownTabs.map((entry) => (
                  <button
                    key={entry.columnId}
                    type="button"
                    role="tab"
                    aria-selected={tab === entry.tab}
                    className={`${hubStyles.tab} ${tab === entry.tab ? hubStyles.tabActive : ''}`}
                    onClick={() => setTab(entry.tab)}
                  >
                    {entry.label}
                  </button>
                ))}
              {allowStatuses && (
                <button
                  type="button"
                  role="tab"
                  aria-selected={tab === 'statuses'}
                  className={`${hubStyles.tab} ${tab === 'statuses' ? hubStyles.tabActive : ''}`}
                  onClick={() => setTab('statuses')}
                >
                  Statuses
                </button>
              )}
            </div>

            <div className={hubStyles.panel}>
              {tab === 'columns' && (allowColumns || allowManageColumns) && (
                <ColumnSettings
                  embedded
                  initialBoardType={activeBoardType || 'detailers'}
                  boards={boards}
                  onClose={onClose}
                />
              )}
              {allowDropdowns && activeDropdownColumnId && (
                <DropdownOptionsPanel columnId={activeDropdownColumnId} />
              )}
              {tab === 'statuses' && allowStatuses && (
                <StatusSettings
                  embedded
                  initialBoardType={
                    activeBoardType && activeBoardType !== 'main' ? activeBoardType : 'detailers'
                  }
                  boards={boards.filter((board) => board.id !== 'main')}
                  projectId={activeProjectId}
                  onClose={onClose}
                />
              )}
            </div>
          </>
        )}
      </div>
    </div>,
    document.body
  );
}
