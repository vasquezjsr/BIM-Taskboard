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
import type { ProjectBoardType, TaskStatusDefinition } from '../types';
import {
  STATUS_AUTO_ASSIGN_OPTIONS,
  autoAssignChoiceFromStatus,
  autoAssignTeamToStoreValue,
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
}

interface SortableStatusItemProps {
  status: TaskStatusDefinition;
  canRemove: boolean;
  applyAll: boolean;
  projectId: string | null;
  selectedBoard: ProjectBoardType;
  onUpdate: (
    boardType: ProjectBoardType,
    id: string,
    updates: Partial<Pick<TaskStatusDefinition, 'label' | 'color' | 'countsAsComplete' | 'autoAssignTeam'>>,
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

function SortableStatusItem({
  status,
  canRemove,
  applyAll,
  projectId,
  selectedBoard,
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
        <select
          className={styles.autoAssignSelect}
          value={autoAssignChoiceFromStatus(status.autoAssignTeam)}
          onChange={(e) =>
            onUpdate(
              selectedBoard,
              status.id,
              {
                autoAssignTeam: autoAssignTeamToStoreValue(e.target.value as StatusAutoAssignChoice),
              },
              projectId,
              applyAll
            )
          }
        >
          {STATUS_AUTO_ASSIGN_OPTIONS.map((option) => (
            <option key={option.id} value={option.id}>
              {option.label}
            </option>
          ))}
        </select>
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

export function StatusSettings({ initialBoardType, boards, projectId, onClose }: StatusSettingsProps) {
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
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  const handleAdd = () => {
    if (!newLabel.trim()) return;
    addBoardTaskStatus(
      selectedBoard,
      newLabel.trim(),
      autoAssignTeamToStoreValue(newAutoAssign),
      projectId,
      effectiveApplyAll
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

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>Status options</h2>
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
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>
        <div className={styles.body}>
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
              <select
                className={styles.addAutoAssignSelect}
                value={newAutoAssign}
                onChange={(e) => setNewAutoAssign(e.target.value as StatusAutoAssignChoice)}
              >
                {STATUS_AUTO_ASSIGN_OPTIONS.map((option) => (
                  <option key={option.id} value={option.id}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>
          </div>
          )}
        </div>
      </div>
    </div>,
    document.body
  );
}
