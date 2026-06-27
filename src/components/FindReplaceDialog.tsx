import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { Task, TaskGroup } from '../types';
import {
  previewFindReplace,
  previewFindReplaceGroups,
  type FindReplaceOptions,
  type FindReplacePreview,
} from '../utils/findReplace';
import styles from './ColumnSettings.module.css';

interface FindReplaceDialogProps {
  tasks: Task[];
  taskIds: string[];
  groups: TaskGroup[];
  groupIds: string[];
  customTextColumns: { id: string; label: string }[];
  onApply: (options: FindReplaceOptions) => void;
  onClose: () => void;
}

export function FindReplaceDialog({
  tasks,
  taskIds,
  groups,
  groupIds,
  customTextColumns,
  onApply,
  onClose,
}: FindReplaceDialogProps) {
  const defaultGroupScope = groupIds.length > 0 && taskIds.length === 0;
  const [find, setFind] = useState('');
  const [replace, setReplace] = useState('');
  const [caseSensitive, setCaseSensitive] = useState(false);
  const [includeTitle, setIncludeTitle] = useState(!defaultGroupScope);
  const [includeDescription, setIncludeDescription] = useState(!defaultGroupScope);
  const [includeGroupName, setIncludeGroupName] = useState(defaultGroupScope || groupIds.length > 0);
  const [selectedCustomColumnIds, setSelectedCustomColumnIds] = useState<Set<string>>(
    () => new Set()
  );

  const findInputRef = useRef<HTMLInputElement>(null);
  const [lastApplied, setLastApplied] = useState<FindReplacePreview | null>(null);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
        return;
      }
      if ((e.ctrlKey || e.metaKey) && (e.key === 'f' || e.key === 'F')) {
        e.preventDefault();
        findInputRef.current?.focus();
        findInputRef.current?.select();
      }
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  const options = useMemo<FindReplaceOptions>(
    () => ({
      find,
      replace,
      caseSensitive,
      fields: {
        title: includeTitle,
        description: includeDescription,
        groupName: includeGroupName,
        customColumnIds: [...selectedCustomColumnIds],
      },
    }),
    [
      find,
      replace,
      caseSensitive,
      includeTitle,
      includeDescription,
      includeGroupName,
      selectedCustomColumnIds,
    ]
  );

  const taskPreview = useMemo(
    () => previewFindReplace(tasks, taskIds, options),
    [tasks, taskIds, options]
  );

  const groupPreview = useMemo(
    () => previewFindReplaceGroups(groups, groupIds, options),
    [groups, groupIds, options]
  );

  const preview = useMemo<FindReplacePreview>(
    () => ({
      tasksAffected: taskPreview.tasksAffected + groupPreview.tasksAffected,
      replacementCount: taskPreview.replacementCount + groupPreview.replacementCount,
    }),
    [taskPreview, groupPreview]
  );

  const hasSelectedField =
    includeTitle ||
    includeDescription ||
    includeGroupName ||
    selectedCustomColumnIds.size > 0;

  const canApply = find.length > 0 && hasSelectedField && preview.replacementCount > 0;

  const scopeLabel = useMemo(() => {
    const parts: string[] = [];
    if (taskIds.length > 0) {
      parts.push(`${taskIds.length} task${taskIds.length === 1 ? '' : 's'}`);
    }
    if (groupIds.length > 0) {
      parts.push(`${groupIds.length} group${groupIds.length === 1 ? '' : 's'}`);
    }
    return parts.join(' and ') || 'this board';
  }, [taskIds.length, groupIds.length]);

  const toggleCustomColumn = (columnId: string, checked: boolean) => {
    setSelectedCustomColumnIds((prev) => {
      const next = new Set(prev);
      if (checked) next.add(columnId);
      else next.delete(columnId);
      return next;
    });
  };

  const handleApply = () => {
    if (!canApply) return;
    setLastApplied({
      tasksAffected: preview.tasksAffected,
      replacementCount: preview.replacementCount,
    });
    onApply(options);
  };

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>Find and replace</h2>
            <p className={styles.intro}>
              Replace text across {scopeLabel}. Press Enter to replace and keep editing.
            </p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>
        <div className={styles.body}>
          <label className={styles.field}>
            <span className={styles.fieldLabel}>Find</span>
            <input
              ref={findInputRef}
              className={styles.textInput}
              value={find}
              onChange={(e) => {
                setFind(e.target.value);
                setLastApplied(null);
              }}
              placeholder="Text to find"
              autoFocus
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  if (canApply) handleApply();
                }
              }}
            />
          </label>
          <label className={styles.field}>
            <span className={styles.fieldLabel}>Replace with</span>
            <input
              className={styles.textInput}
              value={replace}
              onChange={(e) => {
                setReplace(e.target.value);
                setLastApplied(null);
              }}
              placeholder="Replacement text"
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  if (canApply) handleApply();
                }
              }}
            />
          </label>
          <label className={styles.checkboxField}>
            <input
              type="checkbox"
              checked={caseSensitive}
              onChange={(e) => setCaseSensitive(e.target.checked)}
            />
            <span>Match case</span>
          </label>

          <div className={styles.field}>
            <span className={styles.fieldLabel}>Search in</span>
            <div className={styles.checkboxGroup}>
              {taskIds.length > 0 && (
                <>
                  <label className={styles.checkboxField}>
                    <input
                      type="checkbox"
                      checked={includeTitle}
                      onChange={(e) => setIncludeTitle(e.target.checked)}
                    />
                    <span>Title</span>
                  </label>
                  <label className={styles.checkboxField}>
                    <input
                      type="checkbox"
                      checked={includeDescription}
                      onChange={(e) => setIncludeDescription(e.target.checked)}
                    />
                    <span>Description</span>
                  </label>
                </>
              )}
              {groupIds.length > 0 && (
                <label className={styles.checkboxField}>
                  <input
                    type="checkbox"
                    checked={includeGroupName}
                    onChange={(e) => setIncludeGroupName(e.target.checked)}
                  />
                  <span>Group name</span>
                </label>
              )}
              {customTextColumns.map((column) => (
                <label key={column.id} className={styles.checkboxField}>
                  <input
                    type="checkbox"
                    checked={selectedCustomColumnIds.has(column.id)}
                    onChange={(e) => toggleCustomColumn(column.id, e.target.checked)}
                  />
                  <span>{column.label}</span>
                </label>
              ))}
            </div>
          </div>

          <p className={styles.previewText} role="status">
            {lastApplied
              ? `Replaced ${lastApplied.replacementCount} occurrence${
                  lastApplied.replacementCount === 1 ? '' : 's'
                } across ${lastApplied.tasksAffected} row${
                  lastApplied.tasksAffected === 1 ? '' : 's'
                }.`
              : !find
                ? 'Enter text to find.'
                : !hasSelectedField
                  ? 'Select at least one field to search.'
                  : preview.replacementCount === 0
                    ? 'No matches in the current scope.'
                    : `Will replace ${preview.replacementCount} occurrence${
                        preview.replacementCount === 1 ? '' : 's'
                      } across ${preview.tasksAffected} row${
                        preview.tasksAffected === 1 ? '' : 's'
                      }.`}
          </p>

          <button
            type="button"
            className={styles.addBtn}
            onClick={handleApply}
            disabled={!canApply}
          >
            Replace all
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
