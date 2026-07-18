import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  DndContext,
  PointerSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core';
import {
  SortableContext,
  arrayMove,
  useSortable,
  verticalListSortingStrategy,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useStore } from '../store/useStore';
import type { TaskStatusDefinition } from '../types';
import type { ProjectBoardType } from '../types';
import {
  autoAssignChoiceFromStatus,
  autoAssignEmployeeIdToStoreValue,
  autoAssignTeamToStoreValue,
  buildStatusAutoAssignOptions,
  type StatusAutoAssignChoice,
} from '../utils/taskAssigneesAuto';
import { getBoardTaskStatuses, isRfiBoardStatusListLocked } from '../utils/taskStatuses';
import { findStatusColorConflicts } from '../utils/statusColorSync';
import { findDuplicateStatusLabels } from '../utils/statusConsolidation';
import styles from './StatusSettings.module.css';

interface StatusSettingsProps {
  initialBoardType: ProjectBoardType;
  boards: { id: ProjectBoardType; label: string }[];
  projectId: string | null;
  onClose: () => void;
  /** Render inside another dialog (no portal / outer chrome). */
  embedded?: boolean;
}

type StatusAssignUpdates = Partial<
  Pick<
    TaskStatusDefinition,
    'label' | 'color' | 'countsAsComplete' | 'autoAssignTeam' | 'autoAssignEmployeeId'
  >
>;

interface SortableStatusItemProps {
  status: TaskStatusDefinition;
  canRemove: boolean;
  applyAll: boolean;
  projectId: string | null;
  selectedBoard: ProjectBoardType;
  assignOptions: ReturnType<typeof buildStatusAutoAssignOptions>;
  onUpdate: (
    boardType: ProjectBoardType,
    id: string,
    updates: StatusAssignUpdates,
    projectId: string | null,
    applyToAllDeliverables: boolean
  ) => void;
  onRemove: (
    boardType: ProjectBoardType,
    id: string,
    projectId: string | null,
    applyToAllDeliverables: boolean
  ) => void;
}

function assignUpdatesFromChoice(choice: string): StatusAssignUpdates {
  return {
    autoAssignTeam: autoAssignTeamToStoreValue(choice),
    autoAssignEmployeeId: autoAssignEmployeeIdToStoreValue(choice),
  };
}

function StatusAssignSelect({
  value,
  options,
  onChange,
  className,
  title,
}: {
  value: StatusAutoAssignChoice;
  options: ReturnType<typeof buildStatusAutoAssignOptions>;
  onChange: (choice: string) => void;
  className: string;
  title?: string;
}) {
  const teamOptions = options.filter((option) => option.group === 'team');
  const peopleOptions = options.filter((option) => option.group === 'people');
  const knownIds = new Set(options.map((option) => option.id));
  const orphanPerson =
    value.startsWith('person:') && !knownIds.has(value) ? value.slice('person:'.length) : null;

  return (
    <select
      className={className}
      value={value}
      title={title}
      onChange={(e) => onChange(e.target.value)}
    >
      <optgroup label="Teams">
        {teamOptions.map((option) => (
          <option key={option.id} value={option.id}>
            {option.label}
          </option>
        ))}
      </optgroup>
      <optgroup label="People">
        {orphanPerson ? (
          <option value={value}>{orphanPerson} (missing)</option>
        ) : null}
        {peopleOptions.length === 0 ? (
          <option value="" disabled>
            No employees yet
          </option>
        ) : (
          peopleOptions.map((option) => (
            <option key={option.id} value={option.id}>
              {option.label}
            </option>
          ))
        )}
      </optgroup>
    </select>
  );
}

function SortableStatusItem({
  status,
  canRemove,
  applyAll,
  projectId,
  selectedBoard,
  assignOptions,
  onUpdate,
  onRemove,
}: SortableStatusItemProps) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: status.id,
  });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
  };

  return (
    <li
      ref={setNodeRef}
      style={style}
      className={`${styles.item} ${isDragging ? styles.itemDragging : ''}`}
    >
      <button
        type="button"
        className={styles.dragHandle}
        title="Drag to reorder"
        {...attributes}
        {...listeners}
      >
        ⠿
      </button>
      <label className={styles.swatchField} title="Status color">
        <input
          type="color"
          className={styles.swatchInput}
          value={status.color}
          onChange={(e) =>
            onUpdate(
              selectedBoard,
              status.id,
              { color: e.target.value },
              projectId,
              applyAll
            )
          }
        />
        <span className={styles.swatch} style={{ backgroundColor: status.color }} />
      </label>
      <span className={styles.label}>{status.label}</span>
      <label className={styles.autoAssignField} title="Auto-assign when a task enters this status">
        <span className={styles.autoAssignLabel}>Assign</span>
        <StatusAssignSelect
          className={styles.autoAssignSelect}
          value={autoAssignChoiceFromStatus(status.autoAssignTeam, status.autoAssignEmployeeId)}
          options={assignOptions}
          onChange={(choice) =>
            onUpdate(selectedBoard, status.id, assignUpdatesFromChoice(choice), projectId, applyAll)
          }
        />
      </label>
      <label className={styles.completeToggle} title="Counts toward progress">
        <input
          type="checkbox"
          checked={Boolean(status.countsAsComplete)}
          onChange={(e) =>
            onUpdate(
              selectedBoard,
              status.id,
              { countsAsComplete: e.target.checked },
              projectId,
              applyAll
            )
          }
        />
        <span>Done</span>
      </label>
      <button
        type="button"
        className={styles.removeBtn}
        disabled={!canRemove}
        onClick={() => onRemove(selectedBoard, status.id, projectId, applyAll)}
        title={canRemove ? 'Remove status' : 'Keep at least one status'}
      >
        Remove
      </button>
    </li>
  );
}

export function StatusSettings({
  initialBoardType,
  boards,
  projectId,
  onClose,
  embedded = false,
}: StatusSettingsProps) {
  const employees = useStore((s) => s.employees);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const addBoardTaskStatus = useStore((s) => s.addBoardTaskStatus);
  const removeBoardTaskStatus = useStore((s) => s.removeBoardTaskStatus);
  const updateBoardTaskStatus = useStore((s) => s.updateBoardTaskStatus);
  const reorderBoardTaskStatuses = useStore((s) => s.reorderBoardTaskStatuses);
  const syncStatusColorsAcrossProjects = useStore((s) => s.syncStatusColorsAcrossProjects);

  const [selectedBoard, setSelectedBoard] = useState(initialBoardType);
  const [newLabel, setNewLabel] = useState('');
  const [newAutoAssign, setNewAutoAssign] = useState<StatusAutoAssignChoice>('none');
  const [applyToAllDeliverables, setApplyToAllDeliverables] = useState(false);
  const [syncMessage, setSyncMessage] = useState<string | null>(null);

  const showApplyAllOption = Boolean(projectId && selectedBoard === 'deliverables');
  const effectiveApplyAll = showApplyAllOption && applyToAllDeliverables;
  const isRfiBoard = isRfiBoardStatusListLocked(selectedBoard);

  const assignOptions = useMemo(
    () => buildStatusAutoAssignOptions(employees.map((employee) => ({ id: employee.id, name: employee.name }))),
    [employees]
  );

  const statuses = useMemo(
    () => getBoardTaskStatuses(selectedBoard, boardTaskStatuses, projectId, projectBoardTaskStatuses),
    [selectedBoard, boardTaskStatuses, projectId, projectBoardTaskStatuses]
  );

  const colorConflicts = useMemo(
    () => findStatusColorConflicts(boardTaskStatuses, projectBoardTaskStatuses),
    [boardTaskStatuses, projectBoardTaskStatuses]
  );

  const duplicateLabels = useMemo(
    () => findDuplicateStatusLabels(boardTaskStatuses, projectBoardTaskStatuses),
    [boardTaskStatuses, projectBoardTaskStatuses]
  );

  const hasStatusSyncIssues = colorConflicts.length > 0 || duplicateLabels.length > 0;

  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }));

  useEffect(() => {
    setSelectedBoard(initialBoardType);
  }, [initialBoardType]);

  useEffect(() => {
    setSyncMessage(null);
  }, [boardTaskStatuses, projectBoardTaskStatuses]);

  useEffect(() => {
    if (embedded) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [embedded, onClose]);

  const handleAdd = () => {
    if (!newLabel.trim()) return;
    addBoardTaskStatus(
      selectedBoard,
      newLabel.trim(),
      autoAssignTeamToStoreValue(newAutoAssign),
      projectId,
      effectiveApplyAll,
      autoAssignEmployeeIdToStoreValue(newAutoAssign)
    );
    setNewLabel('');
    setNewAutoAssign('none');
  };

  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const oldIndex = statuses.findIndex((status) => status.id === active.id);
    const newIndex = statuses.findIndex((status) => status.id === over.id);
    if (oldIndex === -1 || newIndex === -1) return;
    const reordered = arrayMove(statuses, oldIndex, newIndex);
    reorderBoardTaskStatuses(
      selectedBoard,
      reordered.map((status) => status.id),
      projectId,
      effectiveApplyAll
    );
  };

  const handleSyncColors = () => {
    const syncedCount = syncStatusColorsAcrossProjects();
    if (syncedCount > 0) {
      setSyncMessage(`Synced ${syncedCount} status issue${syncedCount === 1 ? '' : 's'} across all jobs.`);
    } else {
      setSyncMessage('All statuses are already consistent across jobs.');
    }
  };

  const boardControls = (
    <>
      <label className={styles.boardField}>
        <span className={styles.boardLabel}>Board</span>
        <select
          className={styles.boardSelect}
          value={selectedBoard}
          onChange={(e) => setSelectedBoard(e.target.value as ProjectBoardType)}
        >
          {boards.map((board) => (
            <option key={board.id} value={board.id}>
              {board.label}
            </option>
          ))}
        </select>
      </label>
      {isRfiBoard && (
        <p className={styles.rfiHint}>
          RFI uses <strong>Waiting for Response</strong> and <strong>Complete</strong> only.
        </p>
      )}
      {showApplyAllOption && (
        <label className={styles.applyAllField}>
          <input
            type="checkbox"
            checked={applyToAllDeliverables}
            onChange={(e) => setApplyToAllDeliverables(e.target.checked)}
          />
          <span>Apply changes to all Deliverables boards (all projects)</span>
        </label>
      )}
    </>
  );

  const body = (
        <div className={styles.body}>
          {embedded && <div className={styles.header}>{boardControls}</div>}
          {hasStatusSyncIssues && (
            <div className={styles.syncBanner}>
              <p className={styles.syncBannerText}>
                {duplicateLabels.length > 0 && colorConflicts.length > 0
                  ? `${duplicateLabels.length} duplicate status name${duplicateLabels.length === 1 ? '' : 's'} and ${colorConflicts.length} color mismatch${colorConflicts.length === 1 ? '' : 'es'} found across jobs.`
                  : duplicateLabels.length > 0
                    ? `${duplicateLabels.length} status name${duplicateLabels.length === 1 ? '' : 's'} appear${duplicateLabels.length === 1 ? 's' : ''} more than once across jobs (e.g. "${duplicateLabels[0]?.label}").`
                    : colorConflicts.length === 1
                      ? `"${colorConflicts[0].label}" uses ${colorConflicts[0].colors.length} different colors across jobs.`
                      : `${colorConflicts.length} statuses use different colors on different jobs.`}
              </p>
              <button type="button" className={styles.syncBtn} onClick={handleSyncColors}>
                Sync statuses across all jobs
              </button>
            </div>
          )}
          {syncMessage && <p className={styles.syncSuccess}>{syncMessage}</p>}
          <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
            <SortableContext items={statuses.map((status) => status.id)} strategy={verticalListSortingStrategy}>
              <ul className={styles.list}>
                {statuses.map((status) => (
                  <SortableStatusItem
                    key={status.id}
                    status={status}
                    canRemove={!isRfiBoard && statuses.length > 1}
                    applyAll={effectiveApplyAll}
                    projectId={projectId}
                    selectedBoard={selectedBoard}
                    assignOptions={assignOptions}
                    onUpdate={updateBoardTaskStatus}
                    onRemove={removeBoardTaskStatus}
                  />
                ))}
              </ul>
            </SortableContext>
          </DndContext>
          {!isRfiBoard && (
          <div className={styles.addSection}>
            <div className={styles.addRow}>
              <input
                className={styles.addInput}
                placeholder="New status name"
                value={newLabel}
                onChange={(e) => setNewLabel(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && handleAdd()}
              />
              <button type="button" className={styles.addBtn} onClick={handleAdd}>
                Add
              </button>
            </div>
            <label className={styles.addAutoAssignField}>
              <span className={styles.addAutoAssignLabel}>Auto assign to</span>
              <StatusAssignSelect
                className={styles.addAutoAssignSelect}
                value={newAutoAssign}
                options={assignOptions}
                onChange={(choice) => setNewAutoAssign(choice as StatusAutoAssignChoice)}
              />
            </label>
          </div>
          )}
        </div>
  );

  if (embedded) return body;

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>Status options</h2>
            {boardControls}
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>
        {body}
      </div>
    </div>,
    document.body
  );
}
