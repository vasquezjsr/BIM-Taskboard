import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  DndContext,
  DragOverlay,
  PointerSensor,
  closestCenter,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
  type CollisionDetection,
  type DragEndEvent,
  type DragOverEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import { SortableContext, arrayMove, horizontalListSortingStrategy, useSortable } from '@dnd-kit/sortable';
import { CSS, add, getEventCoordinates } from '@dnd-kit/utilities';
import { useStore } from '../store/useStore';
import { AssigneeCell } from './AssigneeCell';
import { ContextMenuPanel } from './ContextMenuPanel';
import { employeeInitials } from '../data/employees';
import {
  getBoardLabel,
  type ProjectBoardType,
  type SheetColumnAlign,
  type SheetColumnDefinition,
  type Task,
  type TaskDurationRange,
  type TaskGroup,
  type TaskStatusDefinition,
} from '../types';
import {
  buildSheetRows,
  computeGroupProgress,
  getSectionForBoard,
  taskBranchBoardType,
  isSectionUngroupedGroupId,
  isUngroupedBucketGroupId,
  sectionBoardTypeFromUngroupedBucketId,
  shouldShowGroupProgressBar,
  taskCountsAsUngroupedInSection,
  taskShowsUngroupedTitleSuffix,
  isSubBoard,
  sheetRowPaddingLeft,
  resolveGroupVisualRole,
  type GroupProgress,
  type GroupVisualRole,
  type SheetRow,
} from '../utils/groupRows';
import {
  getProjectSubBoardOrder,
  getAssignableBoards,
  PROJECT_BOARD_TYPES,
} from '../types';
import { getBoardTaskStatuses, getStatusColor, statusBoardForTask, type BoardTaskStatusesMap, type ProjectBoardTaskStatusesMap } from '../utils/taskStatuses';
import {
  canAddColumns,
  canAssignTasks,
  canEditTasks,
  canManageColumns,
  canManageMaterialOptions,
  canManageStatuses,
} from '../utils/permissions';
import { getContrastingTextColor } from '../utils/colorContrast';
import {
  normalizeColumnSettingsDropdownIds,
} from '../data/premadeSheetColumns';
import {
  openColumnSettingsHub,
} from './BoardColumnSettingsHub';
import {
  buildSheetColumnSlots,
  customColumnWidthKey,
  defaultCustomColumnWidth,
  DEFAULT_SHEET_COLUMN_ALIGNMENT,
  FIXED_SHEET_COLUMN_LABELS,
  normalizeSheetColumnAlignments,
  getBoardSheetColumns,
  isFixedSheetColumnId,
  isProtectedBoardColumnId,
  getBoardSheetColumnOrder,
  parseSheetColDragId,
  sheetColDragId,
  type SheetColumnSlot,
  type FixedSheetColumnId,
} from '../utils/sheetColumns';
import {
  autoFitColumnWidth,
  loadLockedColumns,
  loadOverviewSectionSizing,
  loadSheetColumnWidths,
  loadUserResizedColumns,
  saveLockedColumns,
  saveOverviewSectionSizing,
  saveSheetColumnWidths,
  saveUserResizedColumns,
  type OverviewSectionSizingState,
  SHEET_DEFAULT_WIDTHS,
  SHEET_MIN_WIDTHS,
  sheetColStyle,
  sheetColumnHeaderStyle,
  type SheetFixedColumnKey,
} from '../utils/sheetColumnSizing';
import { StatusSettings } from './StatusSettings';
import { ColumnSettings } from './ColumnSettings';
import { EditCustomColumnDialog } from './EditCustomColumnDialog';
import { defaultStatusForBoard } from '../utils/taskStatus';
import {
  computeSheetGroupsDrop,
  computeSheetTasksDrop,
  dropIntentLabel,
  groupDropId,
  GROUP_DROP_PREFIX,
  BOARD_DROP_PREFIX,
  parseBoardDropId,
  parseGroupDropId,
  parseTaskDragId,
  placementFromPointer,
  resolveGroupDropAction,
  resolveTaskDropTarget,
  isSheetGroupContainer,
  taskDragId,
  TRASH_DROP_ID,
  type DropPlacement,
  type GroupDropAction,
  type SheetDropHint,
  type TaskDropTarget,
} from '../utils/sheetDrag';
import styles from './TaskSpreadsheet.module.css';
import { TaskModal } from './TaskModal';
import { boardTypeForGroup } from '../utils/history';
import {
  getMainOverviewSectionBoardTypes,
  getMainOverviewSectionColumnOrder,
  getMainOverviewSectionSheetLayout,
  overviewSectionColDragId,
  parseOverviewSectionColDragId,
  splitMainOverviewSheetRows,
  type MainOverviewSectionSheetLayout,
} from '../utils/mainOverviewColumns';
import { isFlatBoard } from '../utils/flatBoards';
import { applyFindReplaceToTask, applyFindReplaceToGroup, type FindReplaceOptions } from '../utils/findReplace';
import { buildTaskCommentReadStateMap, type TaskCommentReadState } from '../utils/taskComments';
import { AttachmentDialog } from './AttachmentDialog';
import { CommentDialog } from './CommentDialog';
import { FindReplaceDialog } from './FindReplaceDialog';
import { BoardTabDropOverlays } from './BoardTabDropOverlays';

type SheetContextMenu =
  | { kind: 'task'; taskId: string; x: number; y: number }
  | { kind: 'group'; group: TaskGroup; x: number; y: number }
  | { kind: 'workspace'; x: number; y: number };

/** Pixels near a row edge that count as before/after insert (not center/inside). */
const SHEET_ROW_EDGE_HIT_PX = 10;

/** Live pointer while a sheet drag is active — drop lines track the cursor, not the ghost. */
const sheetLivePointer: { current: { x: number; y: number } | null } = { current: null };

function setSheetLivePointer(point: { x: number; y: number } | null) {
  sheetLivePointer.current = point;
}

function dragPointerCoordinates(
  event: DragOverEvent | DragEndEvent
): { x: number; y: number } | null {
  const start = getEventCoordinates(event.activatorEvent);
  if (start) return add(start, event.delta);
  const translated = event.active.rect.current.translated;
  if (translated) {
    return {
      x: translated.left + translated.width / 2,
      y: translated.top + translated.height / 2,
    };
  }
  const initial = event.active.rect.current.initial;
  if (initial) {
    return {
      x: initial.left + initial.width / 2 + event.delta.x,
      y: initial.top + initial.height / 2 + event.delta.y,
    };
  }
  return null;
}

function dragFeedbackPoint(
  event: DragOverEvent | DragEndEvent
): { x: number; y: number } | null {
  if (sheetLivePointer.current) return sheetLivePointer.current;
  const pointer = dragPointerCoordinates(event);
  if (pointer) return pointer;
  const translated = event.active.rect.current.translated;
  if (translated) {
    return {
      x: translated.left + translated.width / 2,
      y: translated.top + translated.height / 2,
    };
  }
  return null;
}

function resolveSheetDropAtPoint(
  root: HTMLElement,
  x: number,
  y: number,
  excludeDropId?: string | null
): { droppableId: string; rect: DOMRect; placement?: DropPlacement } | null {
  const measured: { droppableId: string; rect: DOMRect }[] = [];
  for (const row of root.querySelectorAll<HTMLTableRowElement>('tr[data-sheet-drop-id]')) {
    const droppableId = row.dataset.sheetDropId;
    if (!droppableId || droppableId === excludeDropId) continue;
    measured.push({ droppableId, rect: row.getBoundingClientRect() });
  }
  measured.sort((a, b) => a.rect.top - b.rect.top || a.rect.left - b.rect.left);

  let containing: { droppableId: string; rect: DOMRect; area: number } | null = null;
  for (const entry of measured) {
    const { droppableId, rect } = entry;
    if (y < rect.top || y > rect.bottom || x < rect.left || x > rect.right) continue;
    const area = rect.width * rect.height;
    if (!containing || area < containing.area) {
      containing = { droppableId, rect, area };
    }
  }

  if (containing) {
    const { droppableId, rect } = containing;
    const fromTop = y - rect.top;
    const fromBottom = rect.bottom - y;
    if (fromTop <= SHEET_ROW_EDGE_HIT_PX && fromTop <= fromBottom) {
      return { droppableId, rect, placement: 'before' };
    }
    if (fromBottom <= SHEET_ROW_EDGE_HIT_PX) {
      return { droppableId, rect, placement: 'after' };
    }
    return { droppableId, rect };
  }

  // True gap between consecutive rows (cursor not inside either).
  for (let i = 0; i < measured.length - 1; i++) {
    const upper = measured[i]!;
    const lower = measured[i + 1]!;
    if (
      x < Math.min(upper.rect.left, lower.rect.left) ||
      x > Math.max(upper.rect.right, lower.rect.right)
    ) {
      continue;
    }
    const gapTop = Math.min(upper.rect.bottom, lower.rect.top);
    const gapBottom = Math.max(upper.rect.bottom, lower.rect.top);
    if (y < gapTop - 1 || y > gapBottom + 1) continue;
    return { droppableId: upper.droppableId, rect: upper.rect, placement: 'after' };
  }

  let nearest: { droppableId: string; rect: DOMRect; dist: number } | null = null;
  for (const entry of measured) {
    const { droppableId, rect } = entry;
    const dist =
      y < rect.top ? rect.top - y : y > rect.bottom ? y - rect.bottom : 0;
    if (dist > 20) continue;
    if (!nearest || dist < nearest.dist) {
      nearest = { droppableId, rect, dist };
    }
  }
  if (!nearest) return null;
  const midY = nearest.rect.top + nearest.rect.height / 2;
  return {
    droppableId: nearest.droppableId,
    rect: nearest.rect,
    placement: y < midY ? 'before' : 'after',
  };
}

function dropPlacementFromEvent(
  event: DragOverEvent | DragEndEvent,
  rowRect?: DOMRect,
  edgeRatio = 0.35
): DropPlacement {
  const point = dragFeedbackPoint(event);
  const overRect = rowRect ?? event.over?.rect;
  if (point != null && overRect) {
    return placementFromPointer(point.y, overRect, edgeRatio);
  }
  return 'after';
}

function groupOnGroupEdgeRatio(activeId: string, overId: string): number {
  // Slightly larger edges so insert lines are easier to hit than "move inside".
  return parseGroupDropId(activeId) && parseGroupDropId(overId) ? 0.28 : 0.35;
}

function resolveDragOverTarget(
  event: DragOverEvent | DragEndEvent,
  scrollRoot: HTMLElement | null,
  fallback?: { overId?: string | null; placement?: DropPlacement }
): { overId: string; placement: DropPlacement; rowRect?: DOMRect } | null {
  const { over, active } = event;
  const activeId = String(active.id);

  const point = dragFeedbackPoint(event);
  const rowHit =
    scrollRoot && point
      ? resolveSheetDropAtPoint(scrollRoot, point.x, point.y, activeId)
      : null;

  if (rowHit) {
    const edgeRatio = groupOnGroupEdgeRatio(activeId, rowHit.droppableId);
    return {
      overId: rowHit.droppableId,
      placement:
        rowHit.placement ?? dropPlacementFromEvent(event, rowHit.rect, edgeRatio),
      rowRect: rowHit.rect,
    };
  }

  if (over && String(over.id) !== activeId) {
    const overId = String(over.id);
    const edgeRatio = groupOnGroupEdgeRatio(activeId, overId);
    return {
      overId,
      placement: dropPlacementFromEvent(event, undefined, edgeRatio),
    };
  }

  if (fallback?.overId && fallback.overId !== activeId) {
    return {
      overId: fallback.overId,
      placement: fallback.placement ?? 'after',
    };
  }

  return null;
}

function groupDropHintPlacement(
  _taskGroups: TaskGroup[],
  _hoveredGroup: TaskGroup | undefined,
  action: GroupDropAction,
  pointerPlacement: DropPlacement
): DropPlacement {
  if (action.hintPlacement) return action.hintPlacement;
  if (action.mode === 'nest') return 'inside';
  return pointerPlacement === 'before' ? 'before' : 'after';
}

const sheetCollisionDetection: CollisionDetection = (args) => {
  const activeId = String(args.active.id);
  const containers = args.droppableContainers.filter(
    (container) => String(container.id) !== activeId
  );
  if (containers.length === 0) return [];

  const pickAtPointer = (candidates: typeof containers) => {
    if (!args.pointerCoordinates) return [];

    const { x, y } = args.pointerCoordinates;
    const measured = candidates
      .map((container) => {
        const rect = container.rect.current;
        if (!rect) return null;
        const contains =
          y >= rect.top && y <= rect.bottom && x >= rect.left && x <= rect.right;
        const edgeDistance =
          y < rect.top
            ? rect.top - y
            : y > rect.bottom
              ? y - rect.bottom
              : x < rect.left
                ? rect.left - x
                : x > rect.right
                  ? x - rect.right
                  : 0;
        return { container, rect, contains, edgeDistance };
      })
      .filter((entry): entry is NonNullable<typeof entry> => entry != null);

    const containing = measured.filter((entry) => entry.contains);
    if (containing.length > 0) {
      containing.sort(
        (a, b) => a.rect.height - b.rect.height || b.rect.top - a.rect.top
      );
      return [
        {
          id: containing[0]!.container.id,
          data: { droppableContainer: containing[0]!.container, value: 0 },
        },
      ];
    }

    measured.sort((a, b) => a.edgeDistance - b.edgeDistance);
    const nearest = measured[0];
    if (nearest && nearest.edgeDistance <= 20) {
      return [
        {
          id: nearest.container.id,
          data: { droppableContainer: nearest.container, value: nearest.edgeDistance },
        },
      ];
    }

    return [];
  };

  const groupContainers = containers.filter((container) =>
    String(container.id).startsWith(GROUP_DROP_PREFIX)
  );

  const boardContainers = containers.filter((container) =>
    String(container.id).startsWith(BOARD_DROP_PREFIX)
  );
  const boardHit = pickAtPointer(boardContainers);
  if (boardHit.length > 0) return boardHit;

  const groupHit = pickAtPointer(groupContainers);
  if (groupHit.length > 0) return groupHit;

  const anyHit = pickAtPointer(containers);
  if (anyHit.length > 0) return anyHit;

  return closestCenter({ ...args, droppableContainers: containers });
};

function ghostBoardFromGroupId(groupId: string): ProjectBoardType | null {
  const match = groupId.match(/^__ghost-ungrouped-(.+)__$/);
  if (!match) return null;
  return match[1] as ProjectBoardType;
}

function canRemoveSheetGroup(group: TaskGroup): boolean {
  if (group.id.startsWith('__')) return false;
  if (isUngroupedBucketGroupId(group.id)) return false;
  if (group.tier === 'section') return false;
  return true;
}

function canDragSheetGroup(group: TaskGroup): boolean {
  return canRemoveSheetGroup(group);
}

function SheetRowDragHandle({
  title,
  className,
  ...props
}: {
  title: string;
  className?: string;
} & React.ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      type="button"
      className={[styles.dragHandleInline, className].filter(Boolean).join(' ')}
      title={title}
      aria-label={title}
      {...props}
    >
      <svg className={styles.dragGripSvg} width="8" height="12" viewBox="0 0 8 12" aria-hidden>
        <circle cx="2" cy="2" r="1.2" fill="currentColor" />
        <circle cx="6" cy="2" r="1.2" fill="currentColor" />
        <circle cx="2" cy="6" r="1.2" fill="currentColor" />
        <circle cx="6" cy="6" r="1.2" fill="currentColor" />
        <circle cx="2" cy="10" r="1.2" fill="currentColor" />
        <circle cx="6" cy="10" r="1.2" fill="currentColor" />
      </svg>
      <span className={styles.dragGripFallback} aria-hidden>
        ⠿
      </span>
    </button>
  );
}

function isSpreadsheetTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  if (target.tagName === 'TEXTAREA') return true;
  if (target.tagName === 'SELECT') return true;
  if (target.tagName === 'INPUT') {
    const type = (target as HTMLInputElement).type.toLowerCase();
    return type !== 'radio' && type !== 'button' && type !== 'submit' && type !== 'file';
  }
  return false;
}

type TaskUpdateOptions = { bulk?: boolean };

type TaskUpdateHandler = (id: string, updates: Partial<Task>, options?: TaskUpdateOptions) => void;

function isTypingOnlyUpdate(updates: Partial<Task>, options?: TaskUpdateOptions): boolean {
  if (options?.bulk === true) return false;
  if (options?.bulk === false) return true;
  const keys = Object.keys(updates);
  return keys.length === 1 && (keys[0] === 'title' || keys[0] === 'description');
}

function mergeBulkTaskUpdates(
  task: Task,
  updates: Partial<Task>,
  boardTaskStatuses: BoardTaskStatusesMap,
  projectBoardTaskStatuses: ProjectBoardTaskStatusesMap
): Partial<Task> {
  const merged: Partial<Task> = {};

  if (updates.title !== undefined) merged.title = updates.title;
  if (updates.description !== undefined) merged.description = updates.description;
  if (updates.dueDate !== undefined) merged.dueDate = updates.dueDate;

  if (updates.assigneeIds !== undefined) {
    merged.assigneeIds = updates.assigneeIds;
  } else if (updates.boardType !== undefined) {
    merged.boardType = updates.boardType;
    if (updates.status !== undefined) {
      merged.status = updates.status;
    } else if (
      updates.boardType === 'detailers' ||
      updates.boardType === 'deliverables' ||
      updates.boardType === 'project-managers'
    ) {
      merged.status = defaultStatusForBoard(
        updates.boardType,
        getBoardTaskStatuses(updates.boardType, boardTaskStatuses, task.projectId, projectBoardTaskStatuses)
      );
    }
  }

  if (updates.status !== undefined && updates.assigneeIds === undefined && updates.boardType === undefined) {
    merged.status = updates.status;
  }

  if (updates.customFields) {
    merged.customFields = { ...(task.customFields ?? {}), ...updates.customFields };
  }

  if (updates.durationFields) {
    const next = { ...(task.durationFields ?? {}) };
    for (const [colId, range] of Object.entries(updates.durationFields)) {
      next[colId] = {
        ...(task.durationFields?.[colId] ?? { start: null, end: null }),
        ...range,
      };
    }
    merged.durationFields = next;
  }

  return merged;
}

function TrashDropZone() {
  const { setNodeRef, isOver } = useDroppable({ id: TRASH_DROP_ID });

  return (
    <div
      ref={setNodeRef}
      className={`${styles.trashDrop} ${isOver ? styles.trashDropActive : ''}`}
      title="Drag tasks or groups here to delete"
    >
      🗑 Delete
    </div>
  );
}

interface TaskSpreadsheetProps {
  clientId: string;
  projectId: string;
  boardType: ProjectBoardType;
}

type FixedColumnKey = SheetFixedColumnKey;

const DEFAULT_WIDTHS = SHEET_DEFAULT_WIDTHS;
const MIN_WIDTHS = SHEET_MIN_WIDTHS;

const FLAT_COLLAPSE_COL_WIDTH = 24;
const DRAG_COL_WIDTH = 28;
const DRAG_COL_STYLE = {
  width: DRAG_COL_WIDTH,
  minWidth: DRAG_COL_WIDTH,
  maxWidth: DRAG_COL_WIDTH,
} as const;

const COLLAPSE_STORAGE_KEY = 'bim-spreadsheet-collapsed';
const OVERVIEW_SECTION_COLLAPSE_KEY = 'bim-overview-section-collapsed';

function loadColumnWidths(boardType: ProjectBoardType): Record<string, number> {
  return loadSheetColumnWidths(boardType);
}

function loadCollapsed(): Set<string> {
  try {
    const saved = localStorage.getItem(COLLAPSE_STORAGE_KEY);
    if (saved) return new Set(JSON.parse(saved));
  } catch {
    /* ignore */
  }
  return new Set();
}

function loadOverviewSectionCollapsed(): Set<string> {
  try {
    const saved = localStorage.getItem(OVERVIEW_SECTION_COLLAPSE_KEY);
    if (saved) return new Set(JSON.parse(saved));
  } catch {
    /* ignore */
  }
  return new Set();
}

interface SortableResizableHeaderProps {
  columnKey: string;
  sortableId?: string;
  label: string;
  width: number;
  onResize: (key: string, width: number) => void;
  resizable?: boolean;
  draggable?: boolean;
  headerAction?: React.ReactNode;
  editableLabel?: boolean;
  onLabelChange?: (label: string) => void;
  onHeaderContextMenu?: (e: React.MouseEvent) => void;
  forceEditLabel?: boolean;
  onForceEditEnd?: () => void;
  labelAlign?: SheetColumnAlign;
  headerClassName?: string;
  userResized?: boolean;
  locked?: boolean;
  onAutoFit?: (key: string) => void;
}

function durationCellJustify(align: SheetColumnAlign): React.CSSProperties['justifyContent'] {
  if (align === 'left') return 'flex-start';
  if (align === 'right') return 'flex-end';
  return 'center';
}

function SortableResizableHeader({
  columnKey,
  sortableId,
  label,
  width,
  onResize,
  resizable = true,
  draggable = false,
  headerAction,
  editableLabel = false,
  onLabelChange,
  onHeaderContextMenu,
  forceEditLabel = false,
  onForceEditEnd,
  labelAlign = DEFAULT_SHEET_COLUMN_ALIGNMENT,
  headerClassName,
  userResized = false,
  locked = false,
  onAutoFit,
}: SortableResizableHeaderProps) {
  const [editingLabel, setEditingLabel] = useState(false);
  const [draftLabel, setDraftLabel] = useState(label);
  const labelInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!editingLabel) setDraftLabel(label);
  }, [label, editingLabel]);

  useEffect(() => {
    if (forceEditLabel) setEditingLabel(true);
  }, [forceEditLabel]);

  useEffect(() => {
    if (editingLabel) labelInputRef.current?.select();
  }, [editingLabel]);

  const commitLabel = () => {
    const trimmed = draftLabel.trim();
    if (trimmed && trimmed !== label) {
      onLabelChange?.(trimmed);
    } else {
      setDraftLabel(label);
    }
    setEditingLabel(false);
    onForceEditEnd?.();
  };
  const startXRef = useRef(0);
  const startWidthRef = useRef(0);
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: sortableId ?? sheetColDragId(columnKey),
    disabled: !draggable,
  });

  const handleMouseDown = useCallback(
    (e: React.MouseEvent) => {
      if (locked || e.detail > 1) return;
      e.preventDefault();
      e.stopPropagation();
      startXRef.current = e.clientX;
      startWidthRef.current = width;

      const handleMouseMove = (moveEvent: MouseEvent) => {
        onResize(columnKey, startWidthRef.current + (moveEvent.clientX - startXRef.current));
      };

      const handleMouseUp = () => {
        document.removeEventListener('mousemove', handleMouseMove);
        document.removeEventListener('mouseup', handleMouseUp);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';
      };

      document.body.style.cursor = 'col-resize';
      document.body.style.userSelect = 'none';
      document.addEventListener('mousemove', handleMouseMove);
      document.addEventListener('mouseup', handleMouseUp);
    },
    [columnKey, width, onResize, locked]
  );

  const handleHeaderContextMenu = useCallback(
    (e: React.MouseEvent) => {
      if (!onHeaderContextMenu) return;
      e.preventDefault();
      e.stopPropagation();
      onHeaderContextMenu(e);
    },
    [onHeaderContextMenu]
  );

  const fixedWidth = locked || userResized;
  const style = {
    ...sheetColumnHeaderStyle(columnKey, width, fixedWidth),
    transform: draggable ? CSS.Transform.toString(transform) : undefined,
    transition: draggable ? transition : undefined,
    opacity: isDragging ? 0.55 : 1,
    zIndex: isDragging ? 20 : undefined,
  };

  return (
    <th
      ref={draggable ? setNodeRef : undefined}
      data-column-key={columnKey}
      style={style}
      className={[isDragging ? styles.headerDragging : undefined, locked ? styles.headerLocked : undefined, headerClassName]
        .filter(Boolean)
        .join(' ') || undefined}
      onContextMenu={handleHeaderContextMenu}
    >
      <div className={styles.thContent}>
        {draggable && (
          <button
            type="button"
            className={styles.headerDragHandle}
            title="Drag to reorder column"
            {...attributes}
            {...listeners}
          >
            ⠿
          </button>
        )}
        {label &&
          (editingLabel ? (
            <input
              ref={labelInputRef}
              className={styles.thLabelInput}
              style={{ textAlign: labelAlign }}
              value={draftLabel}
              onChange={(e) => setDraftLabel(e.target.value)}
              onBlur={commitLabel}
              onKeyDown={(e) => {
                if (e.key === 'Enter') commitLabel();
                if (e.key === 'Escape') {
                  setDraftLabel(label);
                  setEditingLabel(false);
                  onForceEditEnd?.();
                }
              }}
              onClick={(e) => e.stopPropagation()}
            />
          ) : (
            <span
              className={`${styles.thLabel} ${editableLabel ? styles.thLabelEditable : ''}`}
              style={{ textAlign: labelAlign }}
              title={editableLabel ? 'Click to rename' : undefined}
              onClick={
                editableLabel
                  ? (e) => {
                      e.stopPropagation();
                      setEditingLabel(true);
                    }
                  : undefined
              }
            >
              {label}
            </span>
          ))}
        {locked && (
          <span className={styles.columnLockIcon} title="Column width locked">
            🔒
          </span>
        )}
        {headerAction}
        {resizable && !locked && (
          <span
            className={styles.resizeHandle}
            onMouseDown={handleMouseDown}
            onDoubleClick={(e) => {
              e.stopPropagation();
              onAutoFit?.(columnKey);
            }}
            title="Drag to resize · Double-click to fit content"
          />
        )}
      </div>
    </th>
  );
}

function GroupProgressBar({ progress }: { progress: GroupProgress }) {
  const { completed, total, percent } = progress;
  const label = total === 0 ? '0%' : `${percent}%`;

  return (
    <div
      className={styles.groupProgress}
      data-col-measure
      title={
        total === 0
          ? 'No tasks yet'
          : `${completed} of ${total} complete (${percent}%)`
      }
    >
      <div className={styles.groupProgressTrack} aria-hidden>
        <div className={styles.groupProgressFill} style={{ width: `${percent}%` }} />
      </div>
      <span className={styles.groupProgressLabel}>{label}</span>
    </div>
  );
}

function GroupDurationCell({
  column,
  group,
  readOnly,
  onUpdate,
}: {
  column: SheetColumnDefinition;
  group: TaskGroup;
  readOnly: boolean;
  onUpdate: (id: string, updates: Partial<TaskGroup>) => void;
}) {
  const align = column.cellAlignment ?? DEFAULT_SHEET_COLUMN_ALIGNMENT;
  const value: TaskDurationRange = group.durationFields?.[column.id] ?? { start: null, end: null };

  return (
    <div
      className={styles.customColumnCell}
      style={{ justifyContent: durationCellJustify(align) }}
      data-col-measure
    >
      <div className={styles.durationCell}>
        <input
          type="date"
          className={styles.cellDate}
          style={{ textAlign: align as React.CSSProperties['textAlign'] }}
          value={value.start ?? ''}
          readOnly={readOnly}
          onClick={(e) => e.stopPropagation()}
          onMouseDown={(e) => e.stopPropagation()}
          onChange={(e) =>
            !readOnly &&
            onUpdate(group.id, {
              durationFields: {
                ...(group.durationFields ?? {}),
                [column.id]: { ...value, start: e.target.value || null },
              },
            })
          }
          title="Start date"
        />
        <span className={styles.durationSep}>–</span>
        <input
          type="date"
          className={styles.cellDate}
          style={{ textAlign: align as React.CSSProperties['textAlign'] }}
          value={value.end ?? ''}
          readOnly={readOnly}
          onClick={(e) => e.stopPropagation()}
          onMouseDown={(e) => e.stopPropagation()}
          onChange={(e) =>
            !readOnly &&
            onUpdate(group.id, {
              durationFields: {
                ...(group.durationFields ?? {}),
                [column.id]: { ...value, end: e.target.value || null },
              },
            })
          }
          title="End date"
        />
      </div>
    </div>
  );
}

function groupVisualClass(role: GroupVisualRole): string {
  if (role === 'board-section') return styles.groupBoardSection;
  if (role === 'trade-group') return styles.groupTradeGroup;
  if (role === 'level-group') return styles.groupLevelGroup;
  return styles.groupSubLevelGroup;
}

interface SortableGroupRowProps {
  row: Extract<SheetRow, { type: 'group' }>;
  columnSlots: SheetColumnSlot[];
  getCustomColWidth: (column: SheetColumnDefinition) => number;
  progress: GroupProgress;
  collapsedIds: Set<string>;
  dropHint: SheetDropHint | null;
  taskGroups: TaskGroup[];
  onToggleCollapse: (id: string) => void;
  onRename: (id: string, name: string) => void;
  onAddChild: (group: TaskGroup) => void;
  onContextMenu: (group: TaskGroup, x: number, y: number) => void;
  onNewTask: (group: TaskGroup) => void;
  onAddGroup: (group: TaskGroup) => void;
  onUpdateGroup: (id: string, updates: Partial<TaskGroup>) => void;
  onPromoteUngrouped: (group: TaskGroup, name: string) => void;
  isSelected: boolean;
  selectable: boolean;
  onSelect: (groupId: string, e: React.MouseEvent) => void;
  focusName?: boolean;
  onNameFocusConsumed?: () => void;
  isColumnVisible?: (columnId: string) => boolean;
}

function SortableGroupRow({
  row,
  columnSlots,
  getCustomColWidth,
  progress,
  collapsedIds,
  dropHint,
  taskGroups,
  onToggleCollapse,
  onRename,
  onAddChild,
  onContextMenu,
  onNewTask,
  onAddGroup,
  onUpdateGroup,
  onPromoteUngrouped,
  isSelected,
  selectable,
  onSelect,
  focusName = false,
  onNameFocusConsumed,
  isColumnVisible,
}: SortableGroupRowProps) {
  const { group, depth, isGhost = false } = row;
  const isVirtual = group.id.startsWith('__');
  const isUngroupedBucket = isUngroupedBucketGroupId(group.id);
  const isCollapsibleBucket = isSectionUngroupedGroupId(group.id) || isUngroupedBucket;
  const isCollapsed = collapsedIds.has(group.id);
  const groupNameReadOnly = isVirtual && !isCollapsibleBucket;
  const [ungroupedDraftName, setUngroupedDraftName] = useState(group.name);
  const [newMenuOpen, setNewMenuOpen] = useState(false);
  const newMenuRef = useRef<HTMLDivElement>(null);
  const groupNameInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    setUngroupedDraftName(group.name);
  }, [group.id, group.name]);

  useEffect(() => {
    if (!focusName || groupNameReadOnly) return;
    let cancelled = false;
    const frame = window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        if (cancelled) return;
        const input = groupNameInputRef.current;
        if (input) {
          input.focus();
          input.select();
        }
        onNameFocusConsumed?.();
      });
    });
    return () => {
      cancelled = true;
      window.cancelAnimationFrame(frame);
    };
  }, [focusName, group.id, groupNameReadOnly, onNameFocusConsumed]);

  useEffect(() => {
    if (!newMenuOpen) return;
    const handlePointerDown = (event: MouseEvent) => {
      if (newMenuRef.current?.contains(event.target as Node)) return;
      setNewMenuOpen(false);
    };
    document.addEventListener('mousedown', handlePointerDown);
    return () => document.removeEventListener('mousedown', handlePointerDown);
  }, [newMenuOpen]);

  const draggable = canDragSheetGroup(group);
  const dropId = groupDropId(group.id);
  const { setNodeRef: setDropRef } = useDroppable({ id: dropId });
  const {
    attributes,
    listeners,
    setNodeRef: setDragRef,
    isDragging,
  } = useDraggable({
    id: dropId,
    disabled: !draggable,
  });

  const setNodeRef = useCallback(
    (node: HTMLTableRowElement | null) => {
      setDropRef(node);
      setDragRef(node);
    },
    [setDropRef, setDragRef]
  );

  const style = {
    opacity: isDragging ? 0.35 : 1,
  };

  const tierClass = groupVisualClass(resolveGroupVisualRole(group, taskGroups));
  const isLevelGroup = resolveGroupVisualRole(group, taskGroups) === 'level-group';
  const canEditDuration = isLevelGroup && !(isVirtual && !isCollapsibleBucket);

  const showNewBtn = !isVirtual || isCollapsibleBucket;
  const showsNewChoice =
    isUngroupedBucket || group.tier === 'parent' || group.tier === 'child';
  const hasStatusColumn = columnSlots.some(
    (slot) => slot.kind === 'fixed' && slot.id === 'status'
  );
  const showProgress = shouldShowGroupProgressBar(group, taskGroups);

  const handleNew = () => {
    if (isUngroupedBucket) {
      onNewTask(group);
      return;
    }
    if (group.tier === 'section') {
      onAddChild(group);
    } else {
      onNewTask(group);
    }
  };

  const newBtnTitle = isUngroupedBucket
    ? 'New ungrouped task or new group'
    : group.tier === 'section'
      ? 'Add group'
      : showsNewChoice
        ? 'New task or new group'
        : `New task in ${group.name}`;

  const isDropTarget = dropHint?.targetKind === 'group' && dropHint.targetId === group.id;
  const dropClass =
    isDropTarget && dropHint.intent === 'regroup'
      ? styles.dropTargetRegroup
      : isDropTarget && dropHint.intent === 'reorder' && dropHint.placement === 'before'
        ? styles.dropTargetBefore
        : isDropTarget && dropHint.intent === 'reorder'
          ? styles.dropTargetAfter
          : '';

  const handleRowMouseDown = (e: React.MouseEvent<HTMLTableRowElement>) => {
    if (!selectable) return;
    if (e.shiftKey || e.ctrlKey || e.metaKey) {
      e.preventDefault();
    }
  };

  const handleRowClick = (e: React.MouseEvent<HTMLTableRowElement>) => {
    if (!selectable) return;
    const target = e.target as HTMLElement;
    const tag = target.tagName;
    const mod = e.shiftKey || e.ctrlKey || e.metaKey;
    if (tag === 'INPUT' && (target as HTMLInputElement).type === 'checkbox') return;
    if (['INPUT', 'SELECT', 'BUTTON', 'TEXTAREA', 'OPTION'].includes(tag) && !mod) return;
    onSelect(group.id, e);
    if (mod) {
      window.getSelection()?.removeAllRanges();
    }
  };

  return (
    <tr
      ref={setNodeRef}
      style={style}
      data-sheet-drop-id={dropId}
      className={`${styles.groupRow} ${tierClass} ${isGhost ? styles.ghostRow : ''} ${isDragging ? styles.groupRowDragging : ''} ${isSelected ? styles.groupRowSelected : ''} ${dropClass}`}
      title={
        draggable
          ? 'Same-level groups: blue line reorders · center of a container row moves inside'
          : canRemoveSheetGroup(group)
            ? 'Right-click for options'
            : undefined
      }
      onContextMenu={(e) => {
        e.preventDefault();
        onContextMenu(group, e.clientX, e.clientY);
      }}
      onMouseDown={handleRowMouseDown}
      onClick={handleRowClick}
    >
      <td className={styles.colRow} aria-hidden />
      <td className={styles.colCollapse}>
        {!isVirtual || isCollapsibleBucket ? (
          <button
            type="button"
            className={styles.collapseBtn}
            onClick={() => onToggleCollapse(group.id)}
            title={isCollapsed ? 'Expand' : 'Collapse'}
          >
            {isCollapsed ? '▶' : '▼'}
          </button>
        ) : null}
      </td>
      <td className={styles.colDrag} style={DRAG_COL_STYLE}>
        {draggable ? (
          <div className={styles.colDragInner}>
            <SheetRowDragHandle
              title="Drag to reorder or move into another group"
              {...attributes}
              {...listeners}
            />
          </div>
        ) : null}
      </td>
      {columnSlots.map((slot) => {
        const key = slot.kind === 'fixed' ? slot.id : slot.column.id;
        if (isColumnVisible && !isColumnVisible(key)) {
          return <td key={key} className={styles.groupRowEmpty} aria-hidden />;
        }
        const isTitle = slot.kind === 'fixed' && slot.id === 'title';
        const isStatus = slot.kind === 'fixed' && slot.id === 'status';
        const isDurationCol = slot.kind === 'custom' && slot.column.type === 'duration';
        const showDuration = isDurationCol && isLevelGroup;
        return (
          <td
            key={key}
            className={isTitle || isStatus || showDuration ? undefined : styles.groupRowEmpty}
            style={
              slot.kind === 'custom'
                ? { width: getCustomColWidth(slot.column), minWidth: getCustomColWidth(slot.column) }
                : undefined
            }
          >
            {isTitle ? (
              <div className={styles.sheetRowLead}>
                <div
                  className={styles.groupRowContent}
                  style={{ paddingLeft: sheetRowPaddingLeft(depth) }}
                >
                  {!groupNameReadOnly ? (
                    <input
                      ref={groupNameInputRef}
                      className={styles.groupNameInput}
                      value={isUngroupedBucket ? ungroupedDraftName : group.name}
                      onChange={(e) => {
                        if (isUngroupedBucket) {
                          setUngroupedDraftName(e.target.value);
                        } else {
                          onRename(group.id, e.target.value);
                        }
                      }}
                      onBlur={() => {
                        if (!isUngroupedBucket) return;
                        const trimmed = ungroupedDraftName.trim();
                        if (trimmed && trimmed !== 'Ungrouped') {
                          onPromoteUngrouped(group, trimmed);
                        } else {
                          setUngroupedDraftName(group.name);
                        }
                      }}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') {
                          (e.target as HTMLInputElement).blur();
                          return;
                        }
                        if (!isUngroupedBucket) return;
                        if (e.key === 'Escape') {
                          setUngroupedDraftName(group.name);
                          (e.target as HTMLInputElement).blur();
                        }
                      }}
                      title={isUngroupedBucket ? 'Rename to create a group and move these tasks into it' : undefined}
                    />
                  ) : (
                    <span className={styles.groupName}>{group.name}</span>
                  )}
                </div>
              </div>
            ) : isStatus ? (
              <div className={styles.groupRowStatusCell}>
                {showProgress && <GroupProgressBar progress={progress} />}
              </div>
            ) : showDuration ? (
              <div className={styles.groupRowDurationCell}>
                <GroupDurationCell
                  column={slot.column}
                  group={group}
                  readOnly={!canEditDuration}
                  onUpdate={onUpdateGroup}
                />
              </div>
            ) : null}
          </td>
        );
      })}
      <td className={styles.colActions}>
        <div className={styles.colActionsCenter} data-col-measure>
          {!hasStatusColumn && showProgress && <GroupProgressBar progress={progress} />}
          {showNewBtn && (
            showsNewChoice ? (
              <div className={styles.groupNewWrap} ref={newMenuRef}>
                <button
                  type="button"
                  className={styles.groupNewBtn}
                  onClick={(e) => {
                    e.stopPropagation();
                    setNewMenuOpen((open) => !open);
                  }}
                  title={newBtnTitle}
                  aria-expanded={newMenuOpen}
                  aria-haspopup="menu"
                >
                  + New ▾
                </button>
                {newMenuOpen && (
                  <div className={styles.groupNewMenu} role="menu">
                    <button
                      type="button"
                      role="menuitem"
                      onClick={(e) => {
                        e.stopPropagation();
                        onNewTask(group);
                        setNewMenuOpen(false);
                      }}
                    >
                      New task
                    </button>
                    <button
                      type="button"
                      role="menuitem"
                      onClick={(e) => {
                        e.stopPropagation();
                        if (isUngroupedBucket && group.parentId) {
                          const section = taskGroups.find((g) => g.id === group.parentId);
                          if (section) onAddChild(section);
                        } else {
                          onAddGroup(group);
                        }
                        setNewMenuOpen(false);
                      }}
                    >
                      New group
                    </button>
                  </div>
                )}
              </div>
            ) : (
              <button
                type="button"
                className={styles.groupNewBtn}
                onClick={(e) => {
                  e.stopPropagation();
                  handleNew();
                }}
                title={newBtnTitle}
              >
                + New
              </button>
            )
          )}
        </div>
      </td>
    </tr>
  );
}

function CustomColumnCell({
  column,
  task,
  readOnly,
  onUpdate,
}: {
  column: SheetColumnDefinition;
  task: Task;
  readOnly: boolean;
  onUpdate: TaskUpdateHandler;
}) {
  const align = (column.cellAlignment ?? DEFAULT_SHEET_COLUMN_ALIGNMENT);
  const textStyle = { textAlign: align as React.CSSProperties['textAlign'] };

  if (column.type === 'duration') {
    return <div className={styles.customColumnCell} aria-hidden />;
  }

  if (column.type === 'date') {
    return (
      <div className={styles.customColumnCell}>
        <input
          type="date"
          className={styles.cellDate}
          style={textStyle}
          value={task.customFields?.[column.id] ?? ''}
          readOnly={readOnly}
          onChange={(e) =>
            !readOnly &&
            onUpdate(task.id, {
              // Patch only this column — bulk apply must not copy sibling fields.
              customFields: {
                [column.id]: e.target.value || null,
              },
            })
          }
        />
      </div>
    );
  }

  if (column.type === 'dropdown') {
    const options = column.options ?? [];
    const value = task.customFields?.[column.id] ?? '';
    return (
      <div className={styles.customColumnCell}>
        <select
          className={styles.cellSelect}
          style={textStyle}
          value={value}
          disabled={readOnly || options.length === 0}
          onChange={(e) =>
            onUpdate(task.id, {
              // Patch only this column — bulk apply must not copy sibling fields.
              customFields: {
                [column.id]: e.target.value || null,
              },
            })
          }
        >
          <option value="">—</option>
          {options.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </select>
      </div>
    );
  }

  return (
    <div className={styles.customColumnCell}>
      <input
        className={styles.cellInput}
        style={textStyle}
        value={task.customFields?.[column.id] ?? ''}
        readOnly={readOnly}
        onChange={(e) =>
          !readOnly &&
          onUpdate(
            task.id,
            {
              customFields: {
                [column.id]: e.target.value || null,
              },
            },
            { bulk: false }
          )
        }
        placeholder="—"
      />
    </div>
  );
}

interface SortableTaskRowProps {
  row: Extract<SheetRow, { type: 'task' }>;
  isOverview: boolean;
  isFlatBoardView: boolean;
  isSelected: boolean;
  dropHint: SheetDropHint | null;
  attachmentCount: number;
  commentCount: number;
  commentReadState: TaskCommentReadState;
  employees: { id: string; name: string }[];
  taskStatuses: TaskStatusDefinition[];
  boardTaskStatuses: import('../utils/taskStatuses').BoardTaskStatusesMap;
  projectBoardTaskStatuses: import('../utils/taskStatuses').ProjectBoardTaskStatusesMap;
  branchBoards: { id: import('../types').ProjectBoardType; label: string }[];
  columnSlots: SheetColumnSlot[];
  getCustomColWidth: (column: SheetColumnDefinition) => number;
  isColumnVisible?: (columnId: string) => boolean;
  taskGroups: TaskGroup[];
  onSelect: (taskId: string, e: React.MouseEvent) => void;
  onContextMenu: (taskId: string, x: number, y: number) => void;
  onOpenAttachments: (task: Task) => void;
  onOpenComments: (task: Task) => void;
  onUpdate: TaskUpdateHandler;
  onRemove: (id: string) => void;
  onDuplicate: (id: string) => void;
  allowEditTasks: boolean;
  allowAssignTasks: boolean;
}

function renderFixedColumnCell(
  slotId: FixedSheetColumnId,
  task: Task,
  depth: number,
  readOnly: boolean,
  allowAssignTasks: boolean,
  isOverview: boolean,
  employees: { id: string; name: string }[],
  taskStatuses: TaskStatusDefinition[],
  boardTaskStatuses: import('../utils/taskStatuses').BoardTaskStatusesMap,
  projectBoardTaskStatuses: import('../utils/taskStatuses').ProjectBoardTaskStatusesMap,
  taskGroups: TaskGroup[],
  branchBoards: { id: import('../types').ProjectBoardType; label: string }[],
  onUpdate: TaskUpdateHandler
): React.ReactNode {
  switch (slotId) {
    case 'title':
      return (
        <div className={styles.sheetRowLead}>
          <div
            className={styles.taskTitleContent}
            style={{ paddingLeft: sheetRowPaddingLeft(depth) }}
          >
            {task.parentTaskId && <span className={styles.subtaskBadge}>Sub</span>}
            <input
              className={styles.cellInput}
              value={task.title}
              readOnly={readOnly}
              onChange={(e) => !readOnly && onUpdate(task.id, { title: e.target.value }, { bulk: false })}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  (e.target as HTMLInputElement).blur();
                }
              }}
            />
            {taskShowsUngroupedTitleSuffix(task, taskGroups) && (
              <span className={styles.ungroupedSuffix} title="Not in a group">
                (Ungrouped)
              </span>
            )}
          </div>
        </div>
      );
    case 'description':
      return (
        <input
          className={styles.cellInput}
          value={task.description}
          readOnly={readOnly}
          onChange={(e) => !readOnly && onUpdate(task.id, { description: e.target.value }, { bulk: false })}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              (e.target as HTMLInputElement).blur();
            }
          }}
          placeholder="—"
        />
      );
    case 'status': {
      const statusColor = getStatusColor(task.status, taskStatuses);
      const statusTextColor = getContrastingTextColor(statusColor);
      return (
        <div className={styles.statusCellWrap}>
          <select
            className={`${styles.cellSelect} ${styles.statusSelect}`}
            value={task.status}
            disabled={readOnly}
            style={{
              backgroundColor: statusColor,
              color: statusTextColor,
              borderColor: statusColor,
            }}
            onChange={(e) => onUpdate(task.id, { status: e.target.value })}
          >
            {taskStatuses.map((s) => (
              <option key={s.id} value={s.id}>
                {s.label}
              </option>
            ))}
          </select>
        </div>
      );
    }
    case 'assignee':
      return (
        <div
          style={
            !allowAssignTasks
              ? { pointerEvents: 'none', opacity: 0.65 }
              : undefined
          }
          title={!allowAssignTasks ? 'You do not have permission to assign tasks' : undefined}
        >
          <AssigneeCell
            assigneeIds={task.assigneeIds ?? []}
            employees={employees}
            assigneesLocked={task.assigneesLocked}
            onChange={(assigneeIds) => {
              if (!allowAssignTasks) return;
              onUpdate(task.id, { assigneeIds });
            }}
          />
        </div>
      );
    case 'due':
      return (
        <input
          type="date"
          className={styles.cellDate}
          value={task.dueDate ?? ''}
          readOnly={readOnly}
          onChange={(e) => onUpdate(task.id, { dueDate: e.target.value || null })}
        />
      );
    case 'board':
      if (!isOverview) return null;
      return (
        <select
          className={styles.cellSelect}
          value={taskBranchBoardType(task, taskGroups)}
          disabled={readOnly}
          title="Which board this task appears on"
          onChange={(e) => {
            const newBoard = e.target.value as Task['boardType'];
            const updates: Partial<Task> = { boardType: newBoard };
            if (
              newBoard === 'detailers' ||
              newBoard === 'deliverables' ||
              newBoard === 'project-managers'
            ) {
              updates.status = defaultStatusForBoard(
                newBoard,
                getBoardTaskStatuses(newBoard, boardTaskStatuses, task.projectId, projectBoardTaskStatuses)
              );
            }
            onUpdate(task.id, updates);
          }}
        >
          {branchBoards.map((b) => (
            <option key={b.id} value={b.id}>
              {b.label}
            </option>
          ))}
        </select>
      );
    default:
      return null;
  }
}

function SortableTaskRow({
  row,
  isOverview,
  isFlatBoardView,
  isSelected,
  dropHint,
  attachmentCount,
  commentCount,
  commentReadState,
  employees,
  taskStatuses,
  boardTaskStatuses,
  projectBoardTaskStatuses,
  columnSlots,
  getCustomColWidth,
  isColumnVisible,
  taskGroups,
  branchBoards,
  onSelect,
  onContextMenu,
  onOpenAttachments,
  onOpenComments,
  onUpdate,
  onRemove,
  onDuplicate,
  allowEditTasks,
  allowAssignTasks,
}: SortableTaskRowProps) {
  const { task, depth } = row;
  const readOnly = !allowEditTasks;
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: taskDragId(task.id),
  });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.35 : 1,
  };

  const handleRowMouseDown = (e: React.MouseEvent<HTMLTableRowElement>) => {
    if (e.shiftKey || e.ctrlKey || e.metaKey) {
      e.preventDefault();
    }
  };

  const handleRowClick = (e: React.MouseEvent<HTMLTableRowElement>) => {
    const target = e.target as HTMLElement;
    const tag = target.tagName;
    const mod = e.shiftKey || e.ctrlKey || e.metaKey;
    if (['INPUT', 'SELECT', 'BUTTON', 'TEXTAREA', 'OPTION'].includes(tag) && !mod) return;
    onSelect(task.id, e);
    if (mod) {
      window.getSelection()?.removeAllRanges();
    }
  };

  const handleContextMenu = (e: React.MouseEvent<HTMLTableRowElement>) => {
    e.preventDefault();
    onContextMenu(task.id, e.clientX, e.clientY);
  };

  const rowMetaCell = (
    <div className={styles.rowIconGroup}>
      <button
        type="button"
        className={`${styles.rowIconBtn} ${attachmentCount > 0 ? styles.rowIconActive : ''}`}
        title={attachmentCount > 0 ? `${attachmentCount} attachment(s)` : 'Add attachment'}
        onClick={(e) => {
          e.stopPropagation();
          onOpenAttachments(task);
        }}
      >
        📎
      </button>
      <button
        type="button"
        className={`${styles.rowIconBtn} ${
          commentReadState === 'unread'
            ? styles.rowIconCommentUnread
            : commentReadState === 'read'
              ? styles.rowIconCommentRead
              : ''
        }`}
        title={
          commentReadState === 'unread'
            ? `${commentCount} unread comment${commentCount === 1 ? '' : 's'}`
            : commentCount > 0
              ? `${commentCount} comment${commentCount === 1 ? '' : 's'}`
              : 'Add comment'
        }
        onClick={(e) => {
          e.stopPropagation();
          onOpenComments(task);
        }}
      >
        💬
      </button>
    </div>
  );

  const titleDragHandle = (
    <SheetRowDragHandle
      title="Drag onto another task to reorder, or onto a group header to move into that group"
      {...attributes}
      {...listeners}
    />
  );

  const isDropTarget = dropHint?.targetKind === 'task' && dropHint.targetId === task.id;
  const dropClass =
    isDropTarget && dropHint.intent === 'reorder' && dropHint.placement === 'before'
      ? styles.dropLineBefore
      : isDropTarget && dropHint.intent === 'reorder'
        ? styles.dropLineAfter
        : '';

  return (
    <tr
      ref={setNodeRef}
      style={style}
      data-sheet-drop-id={taskDragId(task.id)}
      className={`${styles.taskRow} ${task.parentTaskId ? styles.taskRowSubtask : ''} ${isDragging ? styles.taskRowDragging : ''} ${isSelected ? styles.taskRowSelected : ''} ${dropClass}`}
      onMouseDown={handleRowMouseDown}
      onClick={handleRowClick}
      onContextMenu={handleContextMenu}
    >
      <td className={styles.colRow}>{rowMetaCell}</td>
      <td className={isFlatBoardView ? styles.colCollapseFlat : styles.colCollapse} />
      <td className={styles.colDrag} style={DRAG_COL_STYLE}>
        <div className={styles.colDragInner}>{titleDragHandle}</div>
      </td>
      {columnSlots.map((slot) => {
        const key = slot.kind === 'fixed' ? slot.id : slot.column.id;
        if (isColumnVisible && !isColumnVisible(key)) {
          return <td key={key} className={styles.groupRowEmpty} aria-hidden />;
        }
        return (
        <td
          key={slot.kind === 'fixed' ? slot.id : slot.column.id}
          style={
            slot.kind === 'custom'
              ? { width: getCustomColWidth(slot.column), minWidth: getCustomColWidth(slot.column) }
              : undefined
          }
        >
          {slot.kind === 'custom' ? (
            <CustomColumnCell
              column={slot.column}
              task={task}
              readOnly={readOnly}
              onUpdate={onUpdate}
            />
          ) : (
            renderFixedColumnCell(
              slot.id,
              task,
              depth,
              readOnly,
              allowAssignTasks,
              isOverview,
              employees,
              taskStatuses,
              boardTaskStatuses,
              projectBoardTaskStatuses,
              taskGroups,
              branchBoards,
              onUpdate,
            )
          )}
        </td>
        );
      })}
      <td className={styles.colActions}>
        <div className={styles.colActionsCenter} data-col-measure>
          <button
            className={styles.duplicateBtn}
            onClick={() => onDuplicate(task.id)}
            title="Duplicate task"
          >
            ⧉
          </button>
          <button
            className={styles.deleteBtn}
            onClick={() => onRemove(task.id)}
            title="Delete task"
          >
            ×
          </button>
        </div>
      </td>
    </tr>
  );
}

export function TaskSpreadsheet({ clientId, projectId, boardType }: TaskSpreadsheetProps) {
  const tasks = useStore((s) => s.tasks);
  const taskGroups = useStore((s) => s.taskGroups);
  const employees = useStore((s) => s.employees);
  const currentUserId = useStore((s) => s.currentUserId);
  const viewAsOriginalUserId = useStore((s) => s.viewAsOriginalUserId);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const subBoardTabOrder = useStore((s) => s.subBoardTabOrder);
  const customBoards = useStore((s) => s.customBoards);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const boardSheetColumns = useStore((s) => s.boardSheetColumns);
  const updateBoardSheetColumn = useStore((s) => s.updateBoardSheetColumn);
  const removeBoardSheetColumn = useStore((s) => s.removeBoardSheetColumn);
  const updateMainOverviewSectionColumn = useStore((s) => s.updateMainOverviewSectionColumn);
  const removeMainOverviewSectionColumn = useStore((s) => s.removeMainOverviewSectionColumn);
  const boardSheetColumnOrder = useStore((s) => s.boardSheetColumnOrder);
  const mainOverviewSectionColumnOrder = useStore((s) => s.mainOverviewSectionColumnOrder);
  const mainOverviewSectionSheetColumns = useStore((s) => s.mainOverviewSectionSheetColumns);
  const reorderBoardSheetColumns = useStore((s) => s.reorderBoardSheetColumns);
  const reorderMainOverviewSectionColumns = useStore((s) => s.reorderMainOverviewSectionColumns);
  const isOverview = boardType === 'main';
  const columnAdminId = viewAsOriginalUserId ?? currentUserId;
  const canDeleteColumns = canManageColumns(columnAdminId, employees, employeePermissions);
  const canEditMaterialOptions = canManageMaterialOptions(
    columnAdminId,
    employees,
    employeePermissions
  );
  const columnSettingsDropdownIds = useStore((s) => s.columnSettingsDropdownIds);
  const addColumnSettingsDropdown = useStore((s) => s.addColumnSettingsDropdown);
  const managedDropdownIds = useMemo(
    () => new Set(normalizeColumnSettingsDropdownIds(columnSettingsDropdownIds)),
    [columnSettingsDropdownIds]
  );
  const allowEditTasks = canEditTasks(columnAdminId, employees, employeePermissions);
  const allowAssignTasks = canAssignTasks(columnAdminId, employees, employeePermissions);
  const allowManageStatuses = canManageStatuses(columnAdminId, employees, employeePermissions);
  const allowAddColumns = canAddColumns(columnAdminId, employees, employeePermissions);
  const overviewSectionBoardTypes = useMemo(
    () =>
      isOverview
        ? getMainOverviewSectionBoardTypes(
            taskGroups,
            clientId,
            projectId,
            subBoardTabOrder,
            customBoards
          )
        : [],
    [isOverview, taskGroups, clientId, projectId, subBoardTabOrder, customBoards]
  );
  const [overviewColumnSection, setOverviewColumnSection] = useState<ProjectBoardType | null>(
    null
  );
  const overviewSectionLayouts = useMemo(() => {
    if (!isOverview) return new Map<ProjectBoardType, MainOverviewSectionSheetLayout>();
    const layouts = new Map<ProjectBoardType, MainOverviewSectionSheetLayout>();
    for (const sectionBoardType of overviewSectionBoardTypes) {
      layouts.set(
        sectionBoardType,
        getMainOverviewSectionSheetLayout(
          sectionBoardType,
          mainOverviewSectionColumnOrder,
          mainOverviewSectionSheetColumns,
          boardSheetColumnOrder,
          boardSheetColumns
        )
      );
    }
    return layouts;
  }, [
    isOverview,
    overviewSectionBoardTypes,
    mainOverviewSectionColumnOrder,
    mainOverviewSectionSheetColumns,
    boardSheetColumnOrder,
    boardSheetColumns,
  ]);
  const sheetColumns = useMemo(
    () => getBoardSheetColumns(boardType, boardSheetColumns),
    [boardType, boardSheetColumns]
  );
  const columnOrder = useMemo(
    () => getBoardSheetColumnOrder(boardType, boardSheetColumnOrder, boardSheetColumns, isOverview),
    [boardType, boardSheetColumnOrder, boardSheetColumns, isOverview]
  );
  const columnSlots = useMemo(
    () => buildSheetColumnSlots(columnOrder, sheetColumns, isOverview),
    [columnOrder, sheetColumns, isOverview]
  );
  const hasStatusColumn = useMemo(() => {
    if (isOverview) {
      return [...overviewSectionLayouts.values()].some((layout) =>
        layout.columnSlots.some((slot) => slot.kind === 'fixed' && slot.id === 'status')
      );
    }
    return columnSlots.some((slot) => slot.kind === 'fixed' && slot.id === 'status');
  }, [isOverview, overviewSectionLayouts, columnSlots]);
  const hasAssigneeColumn = useMemo(() => {
    if (isOverview) {
      return [...overviewSectionLayouts.values()].some((layout) =>
        layout.columnSlots.some((slot) => slot.kind === 'fixed' && slot.id === 'assignee')
      );
    }
    return columnSlots.some((slot) => slot.kind === 'fixed' && slot.id === 'assignee');
  }, [isOverview, overviewSectionLayouts, columnSlots]);
  const headerSortableIds = useMemo(
    () => columnOrder.map((id) => sheetColDragId(id)),
    [columnOrder]
  );
  const overviewSectionOptions = useMemo(
    () =>
      overviewSectionBoardTypes.map((sectionBoardType) => ({
        id: sectionBoardType,
        label: getBoardLabel(sectionBoardType, customBoards),
      })),
    [overviewSectionBoardTypes, customBoards]
  );
  const resolveTaskStatuses = useCallback(
    (task: Task) =>
      getBoardTaskStatuses(
        statusBoardForTask(task, taskGroups),
        boardTaskStatuses,
        projectId,
        projectBoardTaskStatuses
      ),
    [boardTaskStatuses, projectBoardTaskStatuses, projectId, taskGroups]
  );
  const statusBoardOptions = useMemo(() => {
    const builtIn = PROJECT_BOARD_TYPES.map((b) => ({ id: b.id, label: b.label }));
    const customs = customBoards
      .filter((b) => b.projectId === projectId)
      .sort((a, b) => a.sortOrder - b.sortOrder)
      .map((b) => ({ id: b.id, label: b.name }));
    return [...builtIn, ...customs];
  }, [customBoards, projectId]);
  const taskClipboard = useStore((s) => s.taskClipboard);
  const updateTask = useStore((s) => s.updateTask);
  const updateTasksWith = useStore((s) => s.updateTasksWith);
  const refreshTasksAutoAssign = useStore((s) => s.refreshTasksAutoAssign);
  const removeTask = useStore((s) => s.removeTask);
  const removeTasks = useStore((s) => s.removeTasks);
  const duplicateTask = useStore((s) => s.duplicateTask);
  const duplicateTasks = useStore((s) => s.duplicateTasks);
  const duplicateGroup = useStore((s) => s.duplicateGroup);
  const duplicateGroups = useStore((s) => s.duplicateGroups);
  const copyTask = useStore((s) => s.copyTask);
  const pasteTask = useStore((s) => s.pasteTask);
  const createTaskInGroup = useStore((s) => s.createTaskInGroup);
  const createSubtask = useStore((s) => s.createSubtask);
  const applySheetTaskUpdates = useStore((s) => s.applySheetTaskUpdates);
  const applySheetGroupUpdates = useStore((s) => s.applySheetGroupUpdates);
  const moveSheetItemsToBoard = useStore((s) => s.moveSheetItemsToBoard);
  const setSheetDragActive = useStore((s) => s.setSheetDragActive);
  const setSheetDragHoverBoard = useStore((s) => s.setSheetDragHoverBoard);
  const addGroup = useStore((s) => s.addGroup);
  const updateGroup = useStore((s) => s.updateGroup);
  const removeGroup = useStore((s) => s.removeGroup);
  const removeGroups = useStore((s) => s.removeGroups);
  const addFlatBoardHeader = useStore((s) => s.addFlatBoardHeader);
  const taskAttachments = useStore((s) => s.taskAttachments);
  const taskComments = useStore((s) => s.taskComments);
  const taskCommentReadAt = useStore((s) => s.taskCommentReadAt);
  const ensureProjectGroups = useStore((s) => s.ensureProjectGroups);

  const wrapperRef = useRef<HTMLDivElement>(null);
  const scrollAreaRef = useRef<HTMLDivElement>(null);
  const tableRef = useRef<HTMLTableElement>(null);
  const [columnWidths, setColumnWidths] = useState(() => loadColumnWidths(boardType));
  const [userResizedColumns, setUserResizedColumns] = useState(() =>
    loadUserResizedColumns(boardType)
  );
  const [lockedColumns, setLockedColumns] = useState(() => loadLockedColumns(boardType));
  const [overviewSectionSizing, setOverviewSectionSizing] = useState<
    Record<string, OverviewSectionSizingState>
  >({});
  const [collapsedIds, setCollapsedIds] = useState<Set<string>>(loadCollapsed);
  const [collapsedOverviewSections, setCollapsedOverviewSections] = useState<Set<string>>(
    loadOverviewSectionCollapsed
  );
  const [activeDragTask, setActiveDragTask] = useState<Task | null>(null);
  const [activeDragTaskIds, setActiveDragTaskIds] = useState<string[]>([]);
  const [activeDragGroup, setActiveDragGroup] = useState<TaskGroup | null>(null);
  const [activeDragGroupIds, setActiveDragGroupIds] = useState<string[]>([]);
  const lastDragOverIdRef = useRef<string | null>(null);
  const lastValidGroupActionRef = useRef<GroupDropAction | null>(null);
  const lastValidTaskDropRef = useRef<TaskDropTarget | null>(null);
  const lastDropPlacementRef = useRef<DropPlacement>('before');
  const [selectedTaskIds, setSelectedTaskIds] = useState<Set<string>>(() => new Set());
  const [selectedGroupIds, setSelectedGroupIds] = useState<Set<string>>(() => new Set());
  const [selectionAnchorId, setSelectionAnchorId] = useState<string | null>(null);
  const [groupSelectionAnchorId, setGroupSelectionAnchorId] = useState<string | null>(null);
  const [contextMenu, setContextMenu] = useState<SheetContextMenu | null>(null);
  const [focusGroupNameId, setFocusGroupNameId] = useState<string | null>(null);
  const [showTaskModal, setShowTaskModal] = useState(false);
  const [showStatusSettings, setShowStatusSettings] = useState(false);
  const [showColumnSettings, setShowColumnSettings] = useState(false);
  const [columnHeaderMenu, setColumnHeaderMenu] = useState<{
    columnKey: string;
    customColumn?: SheetColumnDefinition;
    sectionBoardType?: ProjectBoardType;
    x: number;
    y: number;
  } | null>(null);
  const [editColumn, setEditColumn] = useState<SheetColumnDefinition | null>(null);
  const [inlineRenameColumnId, setInlineRenameColumnId] = useState<string | null>(null);
  const [taskModalSeed, setTaskModalSeed] = useState<{
    groupId?: string | null;
    parentTaskId?: string | null;
  } | null>(null);
  const [showFindReplace, setShowFindReplace] = useState(false);
  const [attachmentTask, setAttachmentTask] = useState<Task | null>(null);
  const [commentTask, setCommentTask] = useState<Task | null>(null);
  const [groupDropFeedback, setGroupDropFeedback] = useState<string | null>(null);
  const [sheetDropHint, setSheetDropHint] = useState<SheetDropHint | null>(null);

  const isFlatBoardView = isFlatBoard(boardType) && isSubBoard(boardType);
  const isGhostBoard = isSubBoard(boardType) && !isFlatBoardView;
  const rowColWidth = Math.max(columnWidths.row ?? DEFAULT_WIDTHS.row, MIN_WIDTHS.row);
  const collapseColWidth = Math.max(
    isFlatBoardView
      ? (columnWidths.collapse ?? FLAT_COLLAPSE_COL_WIDTH)
      : (columnWidths.collapse ?? DEFAULT_WIDTHS.collapse),
    MIN_WIDTHS.collapse
  );
  const dragColWidth = DRAG_COL_WIDTH;
  const colSpan = 3 + columnSlots.length + 1;

  const primaryTaskId = selectionAnchorId ?? (selectedTaskIds.size > 0 ? [...selectedTaskIds][0] : null);

  const clearSelection = useCallback(() => {
    setSelectedTaskIds(new Set());
    setSelectedGroupIds(new Set());
    setSelectionAnchorId(null);
    setGroupSelectionAnchorId(null);
  }, []);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 6 } })
  );

  useEffect(() => {
    ensureProjectGroups(clientId, projectId);
  }, [clientId, projectId, ensureProjectGroups]);

  useEffect(() => {
    setColumnWidths(loadColumnWidths(boardType));
    setUserResizedColumns(loadUserResizedColumns(boardType));
    setLockedColumns(loadLockedColumns(boardType));
  }, [boardType]);

  useEffect(() => {
    if (!isOverview) return;
    setOverviewSectionSizing((prev) => {
      const next = { ...prev };
      let changed = false;
      for (const sectionBoardType of overviewSectionBoardTypes) {
        if (!next[sectionBoardType]) {
          next[sectionBoardType] = loadOverviewSectionSizing(sectionBoardType);
          changed = true;
        }
      }
      return changed ? next : prev;
    });
  }, [isOverview, overviewSectionBoardTypes]);

  useEffect(() => {
    if (isOverview) return;
    saveSheetColumnWidths(boardType, columnWidths);
  }, [boardType, columnWidths, isOverview]);

  useEffect(() => {
    if (isOverview) return;
    saveUserResizedColumns(boardType, userResizedColumns);
  }, [boardType, userResizedColumns, isOverview]);

  useEffect(() => {
    if (isOverview) return;
    saveLockedColumns(boardType, lockedColumns);
  }, [boardType, lockedColumns, isOverview]);

  useEffect(() => {
    const dragging = Boolean(activeDragTask || activeDragGroup);
    if (!dragging) {
      setSheetLivePointer(null);
      return;
    }
    const onMove = (event: PointerEvent) => {
      setSheetLivePointer({ x: event.clientX, y: event.clientY });
    };
    window.addEventListener('pointermove', onMove, true);
    return () => {
      window.removeEventListener('pointermove', onMove, true);
      setSheetLivePointer(null);
    };
  }, [activeDragTask, activeDragGroup]);

  const resolveSizing = useCallback(
    (sectionBoardType?: ProjectBoardType): OverviewSectionSizingState => {
      if (isOverview && sectionBoardType) {
        return (
          overviewSectionSizing[sectionBoardType] ??
          loadOverviewSectionSizing(sectionBoardType)
        );
      }
      return {
        columnWidths,
        userResizedColumns,
        lockedColumns,
      };
    },
    [isOverview, overviewSectionSizing, columnWidths, userResizedColumns, lockedColumns]
  );

  const patchOverviewSectionSizing = useCallback(
    (
      sectionBoardType: ProjectBoardType,
      patch: (current: OverviewSectionSizingState) => OverviewSectionSizingState
    ) => {
      setOverviewSectionSizing((prev) => {
        const current =
          prev[sectionBoardType] ?? loadOverviewSectionSizing(sectionBoardType);
        const nextState = patch(current);
        saveOverviewSectionSizing(sectionBoardType, nextState);
        return { ...prev, [sectionBoardType]: nextState };
      });
    },
    []
  );

  useEffect(() => {
    localStorage.setItem(COLLAPSE_STORAGE_KEY, JSON.stringify([...collapsedIds]));
  }, [collapsedIds]);

  useEffect(() => {
    localStorage.setItem(
      OVERVIEW_SECTION_COLLAPSE_KEY,
      JSON.stringify([...collapsedOverviewSections])
    );
  }, [collapsedOverviewSections]);

  useEffect(() => {
    clearSelection();
    setContextMenu(null);
    setColumnHeaderMenu(null);
  }, [clientId, projectId, boardType, clearSelection]);

  useEffect(() => {
    if (!contextMenu && !columnHeaderMenu) return;
    const close = () => {
      setContextMenu(null);
      setColumnHeaderMenu(null);
    };
    window.addEventListener('click', close);
    window.addEventListener('scroll', close, true);
    return () => {
      window.removeEventListener('click', close);
      window.removeEventListener('scroll', close, true);
    };
  }, [contextMenu, columnHeaderMenu]);

  const handleCopy = useCallback(
    (taskId?: string | null) => {
      const id = taskId ?? primaryTaskId;
      if (id) copyTask(id);
    },
    [copyTask, primaryTaskId]
  );

  const handlePaste = useCallback(
    (insertAfterTaskId?: string | null) => {
      const newId = pasteTask({
        clientId,
        projectId,
        insertAfterTaskId: insertAfterTaskId ?? primaryTaskId,
      });
      if (newId) {
        setSelectedTaskIds(new Set([newId]));
        setSelectionAnchorId(newId);
      }
    },
    [pasteTask, clientId, projectId, primaryTaskId]
  );

  const handleDuplicate = useCallback(
    (taskId: string) => {
      const newId = duplicateTask(taskId);
      if (newId) {
        setSelectedTaskIds(new Set([newId]));
        setSelectionAnchorId(newId);
      }
    },
    [duplicateTask]
  );

  const handleDeleteTask = useCallback(
    (taskId: string) => {
      removeTask(taskId);
      setSelectedTaskIds((current) => {
        const next = new Set(current);
        next.delete(taskId);
        return next;
      });
      setSelectionAnchorId((current) => (current === taskId ? null : current));
      setContextMenu(null);
    },
    [removeTask]
  );

  const handleDeleteSelected = useCallback(() => {
    if (selectedTaskIds.size === 0) return;
    removeTasks([...selectedTaskIds]);
    clearSelection();
    setContextMenu(null);
  }, [selectedTaskIds, removeTasks, clearSelection]);

  const handleDeleteGroup = useCallback(
    (groupId: string) => {
      removeGroup(groupId);
      setSelectedGroupIds((current) => {
        const next = new Set(current);
        next.delete(groupId);
        return next;
      });
      setGroupSelectionAnchorId((current) => (current === groupId ? null : current));
      setContextMenu(null);
    },
    [removeGroup]
  );

  const handleDeleteSelectedGroups = useCallback(() => {
    if (selectedGroupIds.size === 0) return;
    removeGroups([...selectedGroupIds]);
    clearSelection();
    setContextMenu(null);
  }, [selectedGroupIds, removeGroups, clearSelection]);

  const expandGroupAncestors = useCallback(
    (groupId: string) => {
      const ancestorsToExpand = new Set<string>();
      let current = taskGroups.find((g) => g.id === groupId);
      while (current) {
        ancestorsToExpand.add(current.id);
        current = current.parentId
          ? taskGroups.find((g) => g.id === current!.parentId)
          : undefined;
      }
      if (ancestorsToExpand.size > 0) {
        setCollapsedIds((prev) => {
          const next = new Set(prev);
          for (const id of ancestorsToExpand) next.delete(id);
          return next;
        });
      }
    },
    [taskGroups]
  );

  const handleDuplicateGroup = useCallback(
    (groupId: string) => {
      expandGroupAncestors(groupId);
      const newId = duplicateGroup(groupId);
      if (newId) {
        setSelectedGroupIds(new Set([newId]));
        setGroupSelectionAnchorId(newId);
        setFocusGroupNameId(newId);
      }
      setContextMenu(null);
    },
    [duplicateGroup, expandGroupAncestors]
  );

  const handleAutoAssignFromStatus = useCallback(
    (taskIds: string[]) => {
      if (taskIds.length === 0) return;
      refreshTasksAutoAssign(taskIds);
      setContextMenu(null);
    },
    [refreshTasksAutoAssign]
  );

  useEffect(() => {
    const openFindReplace = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || (e.key !== 'f' && e.key !== 'F')) return;
      if (showFindReplace) return;
      if (isSpreadsheetTypingTarget(e.target)) return;
      e.preventDefault();
      e.stopPropagation();
      setShowFindReplace(true);
      setContextMenu(null);
    };

    const onKeyDown = (e: KeyboardEvent) => {
      if (showFindReplace) {
        if (e.key === 'Escape') {
          setShowFindReplace(false);
          e.preventDefault();
        }
        return;
      }

      const target = e.target as HTMLElement;
      if (isSpreadsheetTypingTarget(target)) return;

      if (e.key === 'Escape') {
        let handled = false;
        if (contextMenu) {
          setContextMenu(null);
          handled = true;
        }
        if (columnHeaderMenu) {
          setColumnHeaderMenu(null);
          handled = true;
        }
        if (selectedTaskIds.size > 0 || selectedGroupIds.size > 0) {
          clearSelection();
          handled = true;
        }

        const active = document.activeElement;
        const wrapper = wrapperRef.current;
        if (active instanceof HTMLElement && wrapper?.contains(active)) {
          active.blur();
          handled = true;
        }

        if (handled) {
          e.preventDefault();
        }
        return;
      }

      if (e.key === 'Delete' || e.key === 'Backspace') {
        if (selectedGroupIds.size > 0) {
          e.preventDefault();
          if (selectedGroupIds.size > 1) handleDeleteSelectedGroups();
          else handleDeleteGroup([...selectedGroupIds][0]!);
          return;
        }
        if (selectedTaskIds.size === 0) return;
        e.preventDefault();
        if (selectedTaskIds.size > 1) handleDeleteSelected();
        else handleDeleteTask(primaryTaskId!);
        return;
      }

      const mod = e.ctrlKey || e.metaKey;
      if (!mod) return;

      if (e.key === 'c' || e.key === 'C') {
        if (!primaryTaskId) return;
        e.preventDefault();
        handleCopy(primaryTaskId);
      }
      if (e.key === 'v' || e.key === 'V') {
        if (!taskClipboard) return;
        e.preventDefault();
        handlePaste();
      }
    };

    window.addEventListener('keydown', openFindReplace, true);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', openFindReplace, true);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [
    selectedTaskIds,
    selectedGroupIds,
    primaryTaskId,
    taskClipboard,
    contextMenu,
    columnHeaderMenu,
    showFindReplace,
    handleCopy,
    handlePaste,
    handleDeleteTask,
    handleDeleteSelected,
    handleDeleteGroup,
    handleDeleteSelectedGroups,
    clearSelection,
  ]);

  const handleResize = useCallback(
    (key: string, rawWidth: number, sectionBoardType?: ProjectBoardType) => {
      const min = key.startsWith('custom:')
        ? 100
        : MIN_WIDTHS[key as FixedColumnKey] ?? 72;
      const width = Math.max(min, rawWidth);

      if (isOverview && sectionBoardType) {
        patchOverviewSectionSizing(sectionBoardType, (current) => {
          if (current.lockedColumns.has(key)) return current;
          return {
            ...current,
            columnWidths: { ...current.columnWidths, [key]: width },
            userResizedColumns: new Set(current.userResizedColumns).add(key),
          };
        });
        return;
      }

      if (lockedColumns.has(key)) return;
      setUserResizedColumns((prev) => new Set(prev).add(key));
      setColumnWidths((prev) => ({ ...prev, [key]: width }));
    },
    [isOverview, lockedColumns, patchOverviewSectionSizing]
  );

  const openColumnHeaderMenu = useCallback(
    (
      columnKey: string,
      customColumn: SheetColumnDefinition | undefined,
      e: React.MouseEvent,
      sectionBoardType?: ProjectBoardType
    ) => {
      setContextMenu(null);
      if (sectionBoardType) {
        setOverviewColumnSection(sectionBoardType);
      }
      setColumnHeaderMenu({
        columnKey,
        customColumn,
        sectionBoardType,
        x: e.clientX,
        y: e.clientY,
      });
    },
    []
  );

  const toggleColumnWidthLock = useCallback(
    (columnKey: string, sectionBoardType?: ProjectBoardType) => {
      if (isOverview && sectionBoardType) {
        patchOverviewSectionSizing(sectionBoardType, (current) => {
          const nextLocked = new Set(current.lockedColumns);
          if (nextLocked.has(columnKey)) {
            nextLocked.delete(columnKey);
            const nextResized = new Set(current.userResizedColumns);
            nextResized.delete(columnKey);
            return {
              ...current,
              lockedColumns: nextLocked,
              userResizedColumns: nextResized,
            };
          }

          const nextWidths = { ...current.columnWidths };
          const nextResized = new Set(current.userResizedColumns).add(columnKey);
          nextLocked.add(columnKey);
          return {
            ...current,
            columnWidths: nextWidths,
            userResizedColumns: nextResized,
            lockedColumns: nextLocked,
          };
        });
        return;
      }

      setLockedColumns((prev) => {
        const next = new Set(prev);
        if (next.has(columnKey)) {
          next.delete(columnKey);
          setUserResizedColumns((resized) => {
            const updated = new Set(resized);
            updated.delete(columnKey);
            return updated;
          });
          return next;
        }

        const header = Array.from(
          tableRef.current?.querySelectorAll('th[data-column-key]') ?? []
        ).find((el) => el.getAttribute('data-column-key') === columnKey);
        const measuredWidth = header?.getBoundingClientRect().width;
        if (measuredWidth && measuredWidth > 0) {
          const min = columnKey.startsWith('custom:')
            ? 100
            : MIN_WIDTHS[columnKey as FixedColumnKey] ?? 72;
          setColumnWidths((widths) => ({
            ...widths,
            [columnKey]: Math.max(min, Math.round(measuredWidth)),
          }));
        }
        setUserResizedColumns((resized) => new Set(resized).add(columnKey));
        next.add(columnKey);
        return next;
      });
    },
    [isOverview, patchOverviewSectionSizing]
  );

  const getCustomColWidth = useCallback(
    (column: SheetColumnDefinition, sectionBoardType?: ProjectBoardType) => {
      const key = customColumnWidthKey(column.id);
      const sizing = resolveSizing(sectionBoardType);
      return sizing.columnWidths[key] ?? defaultCustomColumnWidth(column.type);
    },
    [resolveSizing]
  );

  const projectBoardOrder = useMemo(
    () => getProjectSubBoardOrder(projectId, subBoardTabOrder, customBoards),
    [projectId, subBoardTabOrder, customBoards]
  );

  const branchBoards = useMemo(
    () => getAssignableBoards(customBoards, projectId),
    [customBoards, projectId]
  );

  const sheetRows = useMemo(
    () =>
      buildSheetRows(
        taskGroups,
        tasks,
        clientId,
        projectId,
        boardType,
        collapsedIds,
        projectBoardOrder,
        customBoards
      ),
    [
      taskGroups,
      tasks,
      clientId,
      projectId,
      boardType,
      collapsedIds,
      projectBoardOrder,
      customBoards,
    ]
  );

  const overviewSplitRows = useMemo(
    () =>
      isOverview
        ? splitMainOverviewSheetRows(sheetRows, overviewSectionBoardTypes)
        : { preludeRows: [], sectionRows: new Map<ProjectBoardType, SheetRow[]>() },
    [isOverview, sheetRows, overviewSectionBoardTypes]
  );

  const activeBoardSection = useMemo(
    () => getSectionForBoard(taskGroups, clientId, projectId, boardType),
    [taskGroups, clientId, projectId, boardType]
  );

  const showTradeGroupWorkspace =
    Boolean(activeBoardSection) && !isFlatBoardView && boardType !== 'main';

  const taskDragIds = useMemo(
    () =>
      sheetRows
        .filter((row): row is Extract<SheetRow, { type: 'task' }> => row.type === 'task')
        .map((row) => taskDragId(row.task.id)),
    [sheetRows]
  );

  const visibleTaskIds = useMemo(
    () =>
      sheetRows
        .filter((row): row is Extract<SheetRow, { type: 'task' }> => row.type === 'task')
        .map((row) => row.task.id),
    [sheetRows]
  );

  const handleDuplicateSelected = useCallback(() => {
    if (selectedTaskIds.size === 0) return;
    const orderedIds = [...selectedTaskIds].sort(
      (a, b) => visibleTaskIds.indexOf(a) - visibleTaskIds.indexOf(b)
    );
    const newIds = duplicateTasks(orderedIds);
    if (newIds.length > 0) {
      setSelectedTaskIds(new Set(newIds));
      setSelectionAnchorId(newIds[0]!);
    }
    setContextMenu(null);
  }, [selectedTaskIds, visibleTaskIds, duplicateTasks]);

  const visibleSelectableGroupIds = useMemo(
    () =>
      sheetRows
        .filter((row): row is Extract<SheetRow, { type: 'group' }> => row.type === 'group')
        .map((row) => row.group)
        .filter((group) => canRemoveSheetGroup(group))
        .map((group) => group.id),
    [sheetRows]
  );

  const handleDuplicateSelectedGroups = useCallback(() => {
    if (selectedGroupIds.size === 0) return;
    const orderedIds = [...selectedGroupIds].sort(
      (a, b) => visibleSelectableGroupIds.indexOf(a) - visibleSelectableGroupIds.indexOf(b)
    );
    for (const id of orderedIds) expandGroupAncestors(id);
    const newIds = duplicateGroups(orderedIds);
    if (newIds.length > 0) {
      setSelectedGroupIds(new Set(newIds));
      setGroupSelectionAnchorId(newIds[0]!);
      if (newIds.length === 1) setFocusGroupNameId(newIds[0]!);
    }
    setContextMenu(null);
  }, [
    selectedGroupIds,
    visibleSelectableGroupIds,
    duplicateGroups,
    expandGroupAncestors,
  ]);

  const handleGroupSelect = useCallback(
    (groupId: string, e: React.MouseEvent) => {
      const mod = e.ctrlKey || e.metaKey;
      const shift = e.shiftKey;
      let clearedSelection = false;

      setSelectedTaskIds(new Set());
      setSelectionAnchorId(null);

      setSelectedGroupIds((prev) => {
        if (shift && groupSelectionAnchorId) {
          const anchorIdx = visibleSelectableGroupIds.indexOf(groupSelectionAnchorId);
          const clickIdx = visibleSelectableGroupIds.indexOf(groupId);
          if (anchorIdx === -1 || clickIdx === -1) {
            return mod ? new Set([...prev, groupId]) : new Set([groupId]);
          }
          const [lo, hi] =
            anchorIdx < clickIdx ? [anchorIdx, clickIdx] : [clickIdx, anchorIdx];
          const rangeIds = visibleSelectableGroupIds.slice(lo, hi + 1);
          if (mod) {
            const next = new Set(prev);
            rangeIds.forEach((id) => next.add(id));
            return next;
          }
          return new Set(rangeIds);
        }
        if (mod) {
          const next = new Set(prev);
          if (next.has(groupId)) next.delete(groupId);
          else next.add(groupId);
          return next;
        }
        if (prev.size === 1 && prev.has(groupId)) {
          clearedSelection = true;
          return new Set();
        }
        return new Set([groupId]);
      });

      if (clearedSelection) {
        setGroupSelectionAnchorId(null);
        return;
      }

      if (!shift || !groupSelectionAnchorId) {
        setGroupSelectionAnchorId(groupId);
      }
    },
    [groupSelectionAnchorId, visibleSelectableGroupIds]
  );

  const selectedTasks = useMemo(
    () => tasks.filter((task) => selectedTaskIds.has(task.id)),
    [tasks, selectedTaskIds]
  );

  const bulkStatusOptions = useMemo(() => {
    const byId = new Map<string, TaskStatusDefinition>();
    for (const task of selectedTasks) {
      for (const status of resolveTaskStatuses(task)) {
        byId.set(status.id, status);
      }
    }
    return [...byId.values()];
  }, [selectedTasks, resolveTaskStatuses]);

  const allBoardStatusLabels = useMemo(
    () =>
      getBoardTaskStatuses(boardType, boardTaskStatuses, projectId, projectBoardTaskStatuses).map(
        (status) => status.label
      ),
    [boardType, boardTaskStatuses, projectId, projectBoardTaskStatuses]
  );

  const autoFitColumn = useCallback(
    (columnKey: string, sectionBoardType?: ProjectBoardType) => {
      const sizing = resolveSizing(sectionBoardType);
      if (sizing.lockedColumns.has(columnKey)) return;

      const slots =
        sectionBoardType && isOverview
          ? (overviewSectionLayouts.get(sectionBoardType)?.columnSlots ?? columnSlots)
          : columnSlots;
      const table = tableRef.current;
      if (!table) return;

      let columnIndex = 1;
      let headerLabel = '';
      if (columnKey === 'row') {
        columnIndex = 1;
      } else if (columnKey === 'collapse') {
        columnIndex = 2;
      } else if (columnKey === 'drag') {
        columnIndex = 3;
      } else if (columnKey === 'actions') {
        columnIndex = 4 + slots.length;
      } else if (columnKey.startsWith('custom:')) {
        const customId = columnKey.slice('custom:'.length);
        const slotIndex = slots.findIndex(
          (slot) => slot.kind === 'custom' && slot.column.id === customId
        );
        if (slotIndex < 0) return;
        columnIndex = 4 + slotIndex;
        const customSlot = slots[slotIndex];
        if (customSlot.kind === 'custom') headerLabel = customSlot.column.label;
      } else {
        const slotIndex = slots.findIndex(
          (slot) => slot.kind === 'fixed' && slot.id === columnKey
        );
        if (slotIndex < 0) return;
        columnIndex = 4 + slotIndex;
        headerLabel = FIXED_SHEET_COLUMN_LABELS[columnKey as FixedSheetColumnId] ?? '';
      }

      const min = columnKey.startsWith('custom:')
        ? 100
        : MIN_WIDTHS[columnKey as FixedColumnKey] ?? 72;
      const measured = autoFitColumnWidth(columnKey, table, columnIndex, {
        sheetRows,
        statusLabels: allBoardStatusLabels,
        branchLabels: branchBoards.map((board) => board.label),
        branchLabelByBoard: Object.fromEntries(branchBoards.map((board) => [board.id, board.label])),
        tasks: tasks.filter((task) => visibleTaskIds.includes(task.id)),
        headerLabel,
      });
      handleResize(columnKey, Math.max(min, measured), sectionBoardType);
    },
    [
      allBoardStatusLabels,
      branchBoards,
      columnSlots,
      handleResize,
      isOverview,
      overviewSectionLayouts,
      resolveSizing,
      sheetRows,
      tasks,
      visibleTaskIds,
    ]
  );

  const handleTaskSelect = useCallback(
    (taskId: string, e: React.MouseEvent) => {
      const mod = e.ctrlKey || e.metaKey;
      const shift = e.shiftKey;
      let clearedSelection = false;

      setSelectedGroupIds(new Set());
      setGroupSelectionAnchorId(null);

      setSelectedTaskIds((prev) => {
        if (shift && selectionAnchorId) {
          const anchorIdx = visibleTaskIds.indexOf(selectionAnchorId);
          const clickIdx = visibleTaskIds.indexOf(taskId);
          if (anchorIdx === -1 || clickIdx === -1) {
            return mod ? new Set([...prev, taskId]) : new Set([taskId]);
          }
          const [lo, hi] =
            anchorIdx < clickIdx ? [anchorIdx, clickIdx] : [clickIdx, anchorIdx];
          const rangeIds = visibleTaskIds.slice(lo, hi + 1);
          if (mod) {
            const next = new Set(prev);
            rangeIds.forEach((id) => next.add(id));
            return next;
          }
          return new Set(rangeIds);
        }
        if (mod) {
          const next = new Set(prev);
          if (next.has(taskId)) next.delete(taskId);
          else next.add(taskId);
          return next;
        }
        if (prev.size === 1 && prev.has(taskId)) {
          clearedSelection = true;
          return new Set();
        }
        return new Set([taskId]);
      });

      if (clearedSelection) {
        setSelectionAnchorId(null);
        return;
      }

      if (!shift || !selectionAnchorId) {
        setSelectionAnchorId(taskId);
      }
    },
    [selectionAnchorId, visibleTaskIds]
  );

  const handleBulkStatusChange = useCallback(
    (statusId: string) => {
      if (selectedTaskIds.size === 0) return;
      updateTasksWith([...selectedTaskIds], (task) =>
        mergeBulkTaskUpdates(task, { status: statusId }, boardTaskStatuses, projectBoardTaskStatuses)
      );
      setContextMenu(null);
    },
    [selectedTaskIds, updateTasksWith, boardTaskStatuses, projectBoardTaskStatuses]
  );

  const handleBulkAssigneeChange = useCallback(
    (assigneeIds: string[]) => {
      if (selectedTaskIds.size === 0) return;
      updateTasksWith([...selectedTaskIds], (task) =>
        mergeBulkTaskUpdates(task, { assigneeIds }, boardTaskStatuses, projectBoardTaskStatuses)
      );
    },
    [selectedTaskIds, updateTasksWith, boardTaskStatuses, projectBoardTaskStatuses]
  );

  const findReplaceTextColumns = useMemo(
    () =>
      columnSlots
        .filter(
          (slot): slot is Extract<SheetColumnSlot, { kind: 'custom' }> =>
            slot.kind === 'custom' &&
            (slot.column.type === 'text' || slot.column.type === 'dropdown')
        )
        .map((slot) => ({ id: slot.column.id, label: slot.column.label })),
    [columnSlots]
  );

  const findReplaceTaskIds = useMemo(() => {
    if (selectedTaskIds.size > 0) return [...selectedTaskIds];
    if (selectedGroupIds.size > 0) return [];
    return visibleTaskIds;
  }, [selectedTaskIds, selectedGroupIds, visibleTaskIds]);

  const findReplaceGroupIds = useMemo(() => {
    if (selectedGroupIds.size === 0) return [];
    return [...selectedGroupIds].filter((id) => {
      const group = taskGroups.find((entry) => entry.id === id);
      return group ? canRemoveSheetGroup(group) : false;
    });
  }, [selectedGroupIds, taskGroups]);

  const handleFindReplaceApply = useCallback(
    (options: FindReplaceOptions) => {
      if (findReplaceTaskIds.length > 0) {
        updateTasksWith(findReplaceTaskIds, (task) => {
          const updates = applyFindReplaceToTask(task, options);
          return updates ?? {};
        });
      }

      if (findReplaceGroupIds.length > 0 && options.fields.groupName) {
        for (const groupId of findReplaceGroupIds) {
          const group = taskGroups.find((entry) => entry.id === groupId);
          if (!group) continue;
          const updates = applyFindReplaceToGroup(group, options);
          if (updates) updateGroup(groupId, updates);
        }
      }

      setContextMenu(null);
    },
    [findReplaceTaskIds, findReplaceGroupIds, taskGroups, updateGroup, updateTasksWith]
  );

  const handleTaskUpdate = useCallback<TaskUpdateHandler>(
    (id, updates, options) => {
      const updateKeys = Object.keys(updates) as (keyof Task)[];
      const hasAssigneeUpdate = updates.assigneeIds !== undefined;
      const hasNonAssigneeUpdate = updateKeys.some((key) => key !== 'assigneeIds');
      if (hasAssigneeUpdate && !allowAssignTasks) return;
      if (hasNonAssigneeUpdate && !allowEditTasks) return;

      const shouldBulk =
        !isTypingOnlyUpdate(updates, options) &&
        selectedTaskIds.size >= 2 &&
        selectedTaskIds.has(id);

      if (!shouldBulk) {
        updateTask(id, updates);
        return;
      }

      updateTasksWith([...selectedTaskIds], (task) =>
        mergeBulkTaskUpdates(task, updates, boardTaskStatuses, projectBoardTaskStatuses)
      );
    },
    [
      allowAssignTasks,
      allowEditTasks,
      selectedTaskIds,
      updateTask,
      updateTasksWith,
      boardTaskStatuses,
      projectBoardTaskStatuses,
    ]
  );

  const handleTaskContextMenu = useCallback(
    (taskId: string, x: number, y: number) => {
      setSelectedTaskIds((prev) => {
        if (prev.has(taskId) && prev.size > 1) return prev;
        return new Set([taskId]);
      });
      setSelectionAnchorId(taskId);
      setContextMenu({ kind: 'task', taskId, x, y });
    },
    []
  );

  const groupProgressById = useMemo(() => {
    const map = new Map<string, GroupProgress>();
    for (const g of taskGroups) {
      if (g.clientId !== clientId || g.projectId !== projectId) continue;
      map.set(
        g.id,
        computeGroupProgress(g, taskGroups, tasks, clientId, projectId, boardTaskStatuses, projectBoardTaskStatuses)
      );
    }
    return map;
  }, [taskGroups, tasks, clientId, projectId, boardTaskStatuses, projectBoardTaskStatuses]);

  const assignableEmployees = useMemo(
    () => [...employees].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
    [employees]
  );

  const attachmentCountByTask = useMemo(() => {
    const map = new Map<string, number>();
    for (const attachment of taskAttachments) {
      map.set(attachment.taskId, (map.get(attachment.taskId) ?? 0) + 1);
    }
    return map;
  }, [taskAttachments]);

  const commentCountByTask = useMemo(() => {
    const map = new Map<string, number>();
    for (const comment of taskComments) {
      map.set(comment.taskId, (map.get(comment.taskId) ?? 0) + 1);
    }
    return map;
  }, [taskComments]);

  const commentReadStateByTask = useMemo(
    () => buildTaskCommentReadStateMap(taskComments, taskCommentReadAt),
    [taskComments, taskCommentReadAt]
  );

  const getSlotWidth = useCallback(
    (slot: SheetColumnSlot, sectionBoardType?: ProjectBoardType) => {
      const sizing = resolveSizing(sectionBoardType);
      if (slot.kind === 'custom') return getCustomColWidth(slot.column, sectionBoardType);
      return sizing.columnWidths[slot.id] ?? DEFAULT_WIDTHS[slot.id as FixedColumnKey];
    },
    [resolveSizing, getCustomColWidth]
  );

  const tableMinWidth = useMemo(() => {
    const actionsWidth = columnWidths.actions ?? DEFAULT_WIDTHS.actions;
    const slotsTotal = columnSlots.reduce((sum, slot) => sum + getSlotWidth(slot), 0);
    return rowColWidth + collapseColWidth + dragColWidth + slotsTotal + actionsWidth;
  }, [columnWidths, columnSlots, getSlotWidth, rowColWidth, collapseColWidth, dragColWidth]);

  const col = useCallback(
    (key: string, fallback = 120, sectionBoardType?: ProjectBoardType) => {
      if (key === 'drag') {
        return DRAG_COL_STYLE;
      }
      const sizing = resolveSizing(sectionBoardType);
      const width = sizing.columnWidths[key] ?? fallback;
      return sheetColStyle(key, width, sizing.userResizedColumns.has(key));
    },
    [resolveSizing]
  );

  const isColumnLocked = useCallback(
    (key: string, sectionBoardType?: ProjectBoardType) =>
      resolveSizing(sectionBoardType).lockedColumns.has(key),
    [resolveSizing]
  );

  const isColumnUserResized = useCallback(
    (key: string, sectionBoardType?: ProjectBoardType) => {
      const sizing = resolveSizing(sectionBoardType);
      return sizing.userResizedColumns.has(key) || sizing.lockedColumns.has(key);
    },
    [resolveSizing]
  );

  const canRemoveColumn = useCallback(
    (columnId: string, sectionBoardType?: ProjectBoardType) => {
      if (!canDeleteColumns) return false;
      if (isProtectedBoardColumnId(columnId)) return false;
      const slots =
        sectionBoardType && isOverview
          ? (overviewSectionLayouts.get(sectionBoardType)?.columnSlots ?? columnSlots)
          : columnSlots;
      return slots.some(
        (slot) =>
          (slot.kind === 'custom' && slot.column.id === columnId) ||
          (slot.kind === 'fixed' && slot.id === columnId)
      );
    },
    [canDeleteColumns, columnSlots, isOverview, overviewSectionLayouts]
  );

  const handleRemoveOverviewSectionColumn = useCallback(
    (sectionBoardType: ProjectBoardType, columnId: string) => {
      const extras = mainOverviewSectionSheetColumns[sectionBoardType] ?? [];
      if (extras.some((column) => column.id === columnId)) {
        removeMainOverviewSectionColumn(sectionBoardType, columnId);
        return;
      }
      const order = getMainOverviewSectionColumnOrder(
        sectionBoardType,
        mainOverviewSectionColumnOrder,
        mainOverviewSectionSheetColumns,
        boardSheetColumnOrder,
        boardSheetColumns
      ).filter((id) => id !== columnId);
      reorderMainOverviewSectionColumns(sectionBoardType, order);
    },
    [
      mainOverviewSectionSheetColumns,
      mainOverviewSectionColumnOrder,
      boardSheetColumnOrder,
      boardSheetColumns,
      removeMainOverviewSectionColumn,
      reorderMainOverviewSectionColumns,
    ]
  );

  const updateColumnLabel = useCallback(
    (columnId: string, newLabel: string, sectionBoardType?: ProjectBoardType) => {
      if (sectionBoardType) {
        const extras = mainOverviewSectionSheetColumns[sectionBoardType] ?? [];
        if (extras.some((column) => column.id === columnId)) {
          updateMainOverviewSectionColumn(sectionBoardType, columnId, { label: newLabel });
          return;
        }
        updateBoardSheetColumn(sectionBoardType, columnId, { label: newLabel });
        return;
      }
      updateBoardSheetColumn(boardType, columnId, { label: newLabel });
    },
    [
      boardType,
      boardSheetColumns,
      mainOverviewSectionSheetColumns,
      updateBoardSheetColumn,
      updateMainOverviewSectionColumn,
    ]
  );

  const getLayoutTableMinWidth = useCallback(
    (layout: MainOverviewSectionSheetLayout, sectionBoardType: ProjectBoardType) => {
      const sizing = resolveSizing(sectionBoardType);
      const sectionRowColWidth = Math.max(
        sizing.columnWidths.row ?? DEFAULT_WIDTHS.row,
        MIN_WIDTHS.row
      );
      const sectionCollapseColWidth = Math.max(
        sizing.columnWidths.collapse ?? DEFAULT_WIDTHS.collapse,
        MIN_WIDTHS.collapse
      );
      const actionsWidth = sizing.columnWidths.actions ?? DEFAULT_WIDTHS.actions;
      const slotsTotal = layout.columnSlots.reduce(
        (sum, slot) => sum + getSlotWidth(slot, sectionBoardType),
        0
      );
      return (
        sectionRowColWidth + sectionCollapseColWidth + dragColWidth + slotsTotal + actionsWidth
      );
    },
    [resolveSizing, getSlotWidth, dragColWidth]
  );

  const overviewScrollMinWidth = useMemo(() => {
    if (!isOverview || overviewSectionBoardTypes.length === 0) return tableMinWidth;
    let max = tableMinWidth;
    for (const sectionBoardType of overviewSectionBoardTypes) {
      const layout = overviewSectionLayouts.get(sectionBoardType);
      if (!layout) continue;
      max = Math.max(max, getLayoutTableMinWidth(layout, sectionBoardType));
    }
    return max;
  }, [
    isOverview,
    overviewSectionBoardTypes,
    overviewSectionLayouts,
    tableMinWidth,
    getLayoutTableMinWidth,
  ]);

  const renderColumnHeader = (slot: SheetColumnSlot, sectionBoardType?: ProjectBoardType) => {
    const colDragId = (columnId: string) =>
      sectionBoardType
        ? overviewSectionColDragId(sectionBoardType, columnId)
        : sheetColDragId(columnId);
    const sizing = resolveSizing(sectionBoardType);
    const onResize = (key: string, width: number) =>
      handleResize(key, width, sectionBoardType);
    const onFit = (key: string) => autoFitColumn(key, sectionBoardType);

    if (slot.kind === 'custom') {
      const widthKey = customColumnWidthKey(slot.column.id);
      const width = getCustomColWidth(slot.column, sectionBoardType);
      return (
        <SortableResizableHeader
          key={slot.column.id}
          sortableId={colDragId(slot.column.id)}
          columnKey={widthKey}
          label={slot.column.label}
          width={width}
          onResize={onResize}
          draggable
          editableLabel
          labelAlign={
            normalizeSheetColumnAlignments(slot.column).headerAlignment
          }
          forceEditLabel={inlineRenameColumnId === slot.column.id}
          onForceEditEnd={() => setInlineRenameColumnId(null)}
          onLabelChange={(newLabel) =>
            updateColumnLabel(slot.column.id, newLabel, sectionBoardType)
          }
          onHeaderContextMenu={(e) =>
            openColumnHeaderMenu(widthKey, slot.column, e, sectionBoardType)
          }
          userResized={isColumnUserResized(widthKey, sectionBoardType)}
          locked={isColumnLocked(widthKey, sectionBoardType)}
          onAutoFit={onFit}
        />
      );
    }

    const width = sizing.columnWidths[slot.id] ?? DEFAULT_WIDTHS[slot.id as FixedColumnKey];
    return (
      <SortableResizableHeader
        key={slot.id}
        sortableId={colDragId(slot.id)}
        columnKey={slot.id}
        label={FIXED_SHEET_COLUMN_LABELS[slot.id]}
        width={width}
        onResize={onResize}
        draggable
        labelAlign={DEFAULT_SHEET_COLUMN_ALIGNMENT}
        headerAction={
          slot.id === 'status' && allowManageStatuses ? (
            <div className={styles.statusHeaderAction}>
              <button
                type="button"
                className={styles.statusManageBtn}
                onClick={(e) => {
                  e.stopPropagation();
                  setShowStatusSettings(true);
                  setContextMenu(null);
                }}
                title="Add or remove statuses"
              >
                ⚙
              </button>
            </div>
          ) : undefined
        }
        userResized={isColumnUserResized(slot.id, sectionBoardType)}
        locked={isColumnLocked(slot.id, sectionBoardType)}
        onHeaderContextMenu={(e) => openColumnHeaderMenu(slot.id, undefined, e, sectionBoardType)}
        onAutoFit={onFit}
      />
    );
  };

  const renderSheetRowList = (
    rows: SheetRow[],
    slots: SheetColumnSlot[],
    sectionBoardType?: ProjectBoardType
  ) => {
    const getRowCustomColWidth = (column: SheetColumnDefinition) =>
      getCustomColWidth(column, sectionBoardType);

    return rows.length === 0 ? (
      <tr>
        <td colSpan={3 + slots.length + 1} className={styles.empty}>
          No tasks yet. Use "+ New" on a group, add a group, or create an ungrouped task to get started.
        </td>
      </tr>
    ) : (
      rows.map((row) => {
        if (row.type === 'group') {
          return (
            <SortableGroupRow
              key={row.group.id}
              row={row}
              columnSlots={slots}
              getCustomColWidth={getRowCustomColWidth}
              progress={
                groupProgressById.get(row.group.id) ?? {
                  completed: 0,
                  total: 0,
                  percent: 0,
                }
              }
              collapsedIds={collapsedIds}
              dropHint={
                sheetDropHint?.targetKind === 'group' && sheetDropHint.targetId === row.group.id
                  ? sheetDropHint
                  : null
              }
              taskGroups={taskGroups}
              onToggleCollapse={toggleCollapse}
              onRename={(id, name) => updateGroup(id, { name })}
              onUpdateGroup={updateGroup}
              onPromoteUngrouped={handlePromoteUngroupedBucket}
              onAddChild={handleSectionAddChild}
              onContextMenu={(group, x, y) => {
                if (group.tier === 'section' && group.sectionBoardType) {
                  setOverviewColumnSection(group.sectionBoardType);
                }
                if (canRemoveSheetGroup(group) && !selectedGroupIds.has(group.id)) {
                  setSelectedTaskIds(new Set());
                  setSelectionAnchorId(null);
                  setSelectedGroupIds(new Set([group.id]));
                  setGroupSelectionAnchorId(group.id);
                }
                setContextMenu({ kind: 'group', group, x, y });
              }}
              onNewTask={handleQuickTaskInGroup}
              onAddGroup={handleAddGroupUnder}
              focusName={focusGroupNameId === row.group.id}
              onNameFocusConsumed={() => setFocusGroupNameId(null)}
              isSelected={selectedGroupIds.has(row.group.id)}
              selectable={canRemoveSheetGroup(row.group)}
              onSelect={handleGroupSelect}
            />
          );
        }
        return (
          <SortableTaskRow
            key={row.task.id}
            row={row}
            isOverview={isOverview}
            isFlatBoardView={isFlatBoardView}
            isSelected={selectedTaskIds.has(row.task.id)}
            dropHint={
              sheetDropHint?.targetKind === 'task' && sheetDropHint.targetId === row.task.id
                ? sheetDropHint
                : null
            }
            attachmentCount={attachmentCountByTask.get(row.task.id) ?? 0}
            commentCount={commentCountByTask.get(row.task.id) ?? 0}
            commentReadState={commentReadStateByTask.get(row.task.id) ?? 'none'}
            employees={assignableEmployees}
            taskStatuses={resolveTaskStatuses(row.task)}
            boardTaskStatuses={boardTaskStatuses}
            projectBoardTaskStatuses={projectBoardTaskStatuses}
            columnSlots={slots}
            getCustomColWidth={getRowCustomColWidth}
            taskGroups={taskGroups}
            branchBoards={branchBoards}
            onSelect={handleTaskSelect}
            onContextMenu={handleTaskContextMenu}
            onOpenAttachments={setAttachmentTask}
            onOpenComments={setCommentTask}
            onUpdate={handleTaskUpdate}
            onRemove={handleDeleteTask}
            onDuplicate={handleDuplicate}
            allowEditTasks={allowEditTasks}
            allowAssignTasks={allowAssignTasks}
          />
        );
      })
    );
  };

  const renderOverviewSectionTable = (sectionBoardType: ProjectBoardType) => {
    const layout = overviewSectionLayouts.get(sectionBoardType);
    if (!layout) return null;
    const sectionRows = overviewSplitRows.sectionRows.get(sectionBoardType) ?? [];
    const sectionSizing = resolveSizing(sectionBoardType);
    const sectionRowColWidth = Math.max(
      sectionSizing.columnWidths.row ?? DEFAULT_WIDTHS.row,
      MIN_WIDTHS.row
    );
    const sectionCollapseColWidth = Math.max(
      sectionSizing.columnWidths.collapse ?? DEFAULT_WIDTHS.collapse,
      MIN_WIDTHS.collapse
    );
    const sectionMinWidth = getLayoutTableMinWidth(layout, sectionBoardType);
    const sectionHeaderSortableIds = layout.columnOrder.map((id) =>
      overviewSectionColDragId(sectionBoardType, id)
    );
    const onSectionResize = (key: string, width: number) =>
      handleResize(key, width, sectionBoardType);
    const onSectionFit = (key: string) => autoFitColumn(key, sectionBoardType);
    const isSectionCollapsed = collapsedOverviewSections.has(sectionBoardType);

    return (
      <div
        key={sectionBoardType}
        className={`${styles.overviewSection}${isSectionCollapsed ? ` ${styles.overviewSectionCollapsed}` : ''}`}
        onMouseDown={() => setOverviewColumnSection(sectionBoardType)}
      >
        {renderOverviewSectionHeader(
          getBoardLabel(sectionBoardType, customBoards),
          sectionBoardType
        )}
        {!isSectionCollapsed && (
        <table
          className={styles.table}
          style={{ width: sectionMinWidth, minWidth: sectionMinWidth }}
        >
          <colgroup>
            <col style={col('row', sectionRowColWidth, sectionBoardType)} />
            <col style={col('collapse', sectionCollapseColWidth, sectionBoardType)} />
            <col style={col('drag', dragColWidth, sectionBoardType)} />
            {layout.columnSlots.map((slot) => (
              <col
                key={slot.kind === 'fixed' ? slot.id : slot.column.id}
                style={
                  slot.kind === 'custom'
                    ? col(
                        customColumnWidthKey(slot.column.id),
                        defaultCustomColumnWidth(slot.column.type),
                        sectionBoardType
                      )
                    : col(slot.id, DEFAULT_WIDTHS[slot.id as FixedColumnKey], sectionBoardType)
                }
              />
            ))}
            <col
              style={col(
                'actions',
                sectionSizing.columnWidths.actions ?? DEFAULT_WIDTHS.actions,
                sectionBoardType
              )}
            />
          </colgroup>
          <SortableContext
            key={`sheet-col-headers-${sectionBoardType}`}
            items={sectionHeaderSortableIds}
            strategy={horizontalListSortingStrategy}
          >
            <thead>
              <tr>
                <SortableResizableHeader
                  columnKey="row"
                  label=""
                  width={sectionRowColWidth}
                  onResize={onSectionResize}
                  resizable
                  draggable={false}
                  userResized={isColumnUserResized('row', sectionBoardType)}
                  locked={isColumnLocked('row', sectionBoardType)}
                  onHeaderContextMenu={(e) => openColumnHeaderMenu('row', undefined, e, sectionBoardType)}
                  onAutoFit={onSectionFit}
                />
                <SortableResizableHeader
                  columnKey="collapse"
                  label=""
                  width={sectionCollapseColWidth}
                  onResize={onSectionResize}
                  resizable
                  draggable={false}
                  userResized={isColumnUserResized('collapse', sectionBoardType)}
                  locked={isColumnLocked('collapse', sectionBoardType)}
                  onHeaderContextMenu={(e) =>
                    openColumnHeaderMenu('collapse', undefined, e, sectionBoardType)
                  }
                  onAutoFit={onSectionFit}
                />
                <SortableResizableHeader
                  columnKey="drag"
                  label=""
                  width={dragColWidth}
                  onResize={onSectionResize}
                  resizable={false}
                  draggable={false}
                  userResized={isColumnUserResized('drag', sectionBoardType)}
                  locked={isColumnLocked('drag', sectionBoardType)}
                  onHeaderContextMenu={(e) => openColumnHeaderMenu('drag', undefined, e, sectionBoardType)}
                  onAutoFit={onSectionFit}
                  headerClassName={styles.colDragHeader}
                />
                {layout.columnSlots.map((slot) => renderColumnHeader(slot, sectionBoardType))}
                <SortableResizableHeader
                  columnKey="actions"
                  label=""
                  width={sectionSizing.columnWidths.actions ?? DEFAULT_WIDTHS.actions}
                  onResize={onSectionResize}
                  resizable
                  draggable={false}
                  headerClassName={styles.colActionsHeader}
                  userResized={isColumnUserResized('actions', sectionBoardType)}
                  locked={isColumnLocked('actions', sectionBoardType)}
                  onHeaderContextMenu={(e) =>
                    openColumnHeaderMenu('actions', undefined, e, sectionBoardType)
                  }
                  onAutoFit={onSectionFit}
                  headerAction={
                    allowAddColumns ? (
                    <button
                      type="button"
                      className={styles.columnManageBtn}
                      onClick={(e) => {
                        e.stopPropagation();
                        setOverviewColumnSection(sectionBoardType);
                        setShowColumnSettings(true);
                        setContextMenu(null);
                      }}
                      title="Add column"
                    >
                      +
                    </button>
                    ) : undefined
                  }
                />
              </tr>
            </thead>
          </SortableContext>
          <tbody>
            {renderSheetRowList(sectionRows, layout.columnSlots, sectionBoardType)}
          </tbody>
        </table>
        )}
      </div>
    );
  };

  const toggleCollapse = (id: string) => {
    setCollapsedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const toggleOverviewSectionCollapse = (sectionKey: string) => {
    setCollapsedOverviewSections((prev) => {
      const next = new Set(prev);
      if (next.has(sectionKey)) next.delete(sectionKey);
      else next.add(sectionKey);
      return next;
    });
  };

  const renderOverviewSectionHeader = (title: string, sectionKey: string) => {
    const isSectionCollapsed = collapsedOverviewSections.has(sectionKey);
    return (
      <div className={styles.overviewSectionHeader}>
        <h3 className={styles.overviewSectionHeading}>{title}</h3>
        <button
          type="button"
          className={styles.overviewSectionCollapseBtn}
          onClick={() => toggleOverviewSectionCollapse(sectionKey)}
          title={isSectionCollapsed ? 'Expand section' : 'Collapse section'}
          aria-expanded={!isSectionCollapsed}
          aria-label={isSectionCollapsed ? `Expand ${title}` : `Collapse ${title}`}
        >
          {isSectionCollapsed ? '▶' : '▼'}
        </button>
      </div>
    );
  };

  const handleAddGroupUnder = useCallback(
    (parent: TaskGroup) => {
      let newGroupId: string;
      if (parent.tier === 'section') {
        newGroupId = addGroup({
          name: 'New Trade Group',
          clientId,
          projectId,
          boardType: 'main',
          tier: 'parent',
          parentId: parent.id,
          sectionBoardType: null,
        });
      } else {
        const tier: TaskGroup['tier'] =
          parent.tier === 'parent' ? 'child' : parent.tier === 'child' ? 'parent' : 'child';
        const name = tier === 'child' ? 'New Sub-Level' : 'New Group';
        newGroupId = addGroup({
          name,
          clientId,
          projectId,
          boardType: parent.boardType,
          tier,
          parentId: parent.id,
          sectionBoardType: null,
        });
      }

      const ancestorsToExpand = new Set<string>();
      let current: TaskGroup | undefined = parent;
      while (current) {
        ancestorsToExpand.add(current.id);
        current = current.parentId
          ? taskGroups.find((g) => g.id === current!.parentId)
          : undefined;
      }
      if (ancestorsToExpand.size > 0) {
        setCollapsedIds((prev) => {
          const next = new Set(prev);
          for (const id of ancestorsToExpand) next.delete(id);
          return next;
        });
      }

      setFocusGroupNameId(newGroupId);
      return newGroupId;
    },
    [addGroup, clientId, projectId, taskGroups]
  );

  const handleSectionAddChild = (group: TaskGroup) => {
    handleAddGroupUnder(group);
  };

  const handleAddTradeGroup = () => {
    if (!activeBoardSection) return;
    handleAddGroupUnder(activeBoardSection);
    setContextMenu(null);
  };

  const handlePromoteUngroupedBucket = useCallback(
    (ungroupedGroup: TaskGroup, name: string) => {
      const trimmed = name.trim();
      if (!trimmed || trimmed === 'Ungrouped') return;

      const section =
        (ungroupedGroup.parentId
          ? taskGroups.find((g) => g.id === ungroupedGroup.parentId)
          : undefined) ??
        taskGroups.find(
          (g) =>
            g.projectId === projectId &&
            g.clientId === clientId &&
            g.tier === 'section' &&
            g.sectionBoardType === sectionBoardTypeFromUngroupedBucketId(ungroupedGroup.id)
        );
      if (!section?.sectionBoardType) return;

      const taskIds = tasks
        .filter(
          (t) =>
            t.clientId === clientId &&
            t.projectId === projectId &&
            !t.parentTaskId &&
            taskCountsAsUngroupedInSection(t, section, taskGroups)
        )
        .map((t) => t.id);

      const newGroupId = addGroup({
        name: trimmed,
        clientId,
        projectId,
        boardType: 'main',
        tier: 'parent',
        parentId: section.id,
        sectionBoardType: null,
      });

      if (taskIds.length > 0) {
        updateTasksWith(taskIds, (task) => ({ ...task, groupId: newGroupId }));
      }
    },
    [addGroup, clientId, projectId, taskGroups, tasks, updateTasksWith]
  );

  const handleDragStart = (event: DragStartEvent) => {
    const activeId = String(event.active.id);
    if (parseSheetColDragId(activeId) || parseOverviewSectionColDragId(activeId)) return;

    lastDragOverIdRef.current = null;
    lastValidGroupActionRef.current = null;
    lastValidTaskDropRef.current = null;
    lastDropPlacementRef.current = 'before';
    setSheetDropHint(null);
    setSheetDragHoverBoard(null);

    const groupId = parseGroupDropId(String(event.active.id));
    if (groupId) {
      const group = taskGroups.find((g) => g.id === groupId);
      if (!group || !canDragSheetGroup(group)) return;
      const rowOrder = new Map(
        sheetRows
          .filter((row): row is Extract<SheetRow, { type: 'group' }> => row.type === 'group')
          .map((row, index) => [row.group.id, index])
      );
      const movingIds =
        selectedGroupIds.has(groupId) && selectedGroupIds.size > 1
          ? [...selectedGroupIds]
              .filter((id) => {
                const entry = taskGroups.find((g) => g.id === id);
                return entry && canDragSheetGroup(entry);
              })
              .sort((a, b) => (rowOrder.get(a) ?? 0) - (rowOrder.get(b) ?? 0))
          : [groupId];
      setActiveDragGroup(group);
      setActiveDragGroupIds(movingIds);
      setActiveDragTask(null);
      setActiveDragTaskIds([]);
      setSheetDragActive(true);
      return;
    }

    const taskId = parseTaskDragId(String(event.active.id));
    if (!taskId) return;
    const task = tasks.find((t) => t.id === taskId);
    if (!task) return;

    const rowOrder = new Map(
      sheetRows
        .filter((row): row is Extract<SheetRow, { type: 'task' }> => row.type === 'task')
        .map((row, index) => [row.task.id, index])
    );
    const movingIds =
      selectedTaskIds.has(taskId) && selectedTaskIds.size > 1
        ? [...selectedTaskIds].sort(
            (a, b) => (rowOrder.get(a) ?? 0) - (rowOrder.get(b) ?? 0)
          )
        : [taskId];

    setActiveDragGroup(null);
    setActiveDragGroupIds([]);
    setActiveDragTask(task);
    setActiveDragTaskIds(movingIds);
    setSheetDragActive(true);
  };

  const handleDragOver = (event: DragOverEvent) => {
    const target = resolveDragOverTarget(event, scrollAreaRef.current);
    if (!target) {
      setSheetDropHint(null);
      setSheetDragHoverBoard(null);
      return;
    }

    const { overId, placement } = target;
    lastDragOverIdRef.current = overId;
    lastDropPlacementRef.current = placement;

    const hoverBoard = parseBoardDropId(overId);
    if (hoverBoard) {
      setSheetDragHoverBoard(hoverBoard);
      setSheetDropHint(null);
      return;
    }
    setSheetDragHoverBoard(null);

    const activeGroupId = parseGroupDropId(String(event.active.id));
    if (activeGroupId) {
      const action = resolveGroupDropAction(
        overId,
        activeGroupId,
        placement,
        taskGroups,
        tasks,
        sheetRows
      );
      if (action && 'mode' in action) {
        lastValidGroupActionRef.current = action;
        const targetGroupId = parseGroupDropId(action.targetOverId);
        if (targetGroupId) {
          const hoveredGroupId = parseGroupDropId(overId);
          const hoveredGroup = hoveredGroupId
            ? taskGroups.find((group) => group.id === hoveredGroupId)
            : undefined;
          setSheetDropHint({
            targetId: action.hintTargetGroupId ?? hoveredGroupId ?? targetGroupId,
            targetKind: 'group',
            intent: action.mode === 'nest' ? 'regroup' : 'reorder',
            placement: groupDropHintPlacement(
              taskGroups,
              hoveredGroup,
              action,
              placement
            ),
          });
          return;
        }
      }
      setSheetDropHint(null);
      return;
    }

    const activeTaskId = parseTaskDragId(String(event.active.id));
    if (!activeTaskId) return;

    const movingIds =
      selectedTaskIds.has(activeTaskId) && selectedTaskIds.size > 1
        ? [...selectedTaskIds]
        : [activeTaskId];

    const dropTarget = resolveTaskDropTarget(
      overId,
      movingIds,
      tasks,
      sheetRows,
      placement
    );
    if (dropTarget) {
      lastValidTaskDropRef.current = dropTarget;
      const overGroupId = parseGroupDropId(overId);
      const overTaskId = parseTaskDragId(overId);
      const linePlacement: DropPlacement = dropTarget.insertBeforeTaskId
        ? 'before'
        : dropTarget.insertAfterTaskId
          ? 'after'
          : overGroupId &&
              isSheetGroupContainer(
                taskGroups,
                taskGroups.find((group) => group.id === overGroupId)
              ) &&
              placement !== 'after'
            ? 'after'
            : 'inside';
      setSheetDropHint({
        targetId: overGroupId ?? overTaskId ?? '',
        targetKind: overGroupId ? 'group' : 'task',
        intent: dropTarget.intent,
        placement: linePlacement,
      });
      return;
    }
    setSheetDropHint(null);
  };

  const expandGroupChain = (groupId: string) => {
    setCollapsedIds((prev) => {
      const next = new Set(prev);
      next.delete(groupId);
      let current = taskGroups.find((group) => group.id === groupId);
      while (current?.parentId) {
        next.delete(current.parentId);
        current = taskGroups.find((group) => group.id === current!.parentId);
      }
      return next;
    });
  };

  const clearSheetDragState = () => {
    setSheetLivePointer(null);
    setActiveDragTask(null);
    setActiveDragTaskIds([]);
    setActiveDragGroup(null);
    setActiveDragGroupIds([]);
    setSheetDropHint(null);
    setSheetDragActive(false);
    setSheetDragHoverBoard(null);
    lastDragOverIdRef.current = null;
    lastValidGroupActionRef.current = null;
    lastValidTaskDropRef.current = null;
    lastDropPlacementRef.current = 'before';
  };

  const handleDragEnd = (event: DragEndEvent) => {
    const movingTaskIds = activeDragTaskIds;
    const movingGroupIds = activeDragGroupIds;
    const { active } = event;
    const savedValidGroupAction = lastValidGroupActionRef.current;
    const savedValidTaskDrop = lastValidTaskDropRef.current;
    const savedOverId = lastDragOverIdRef.current;
    const savedPlacement = lastDropPlacementRef.current;
    const dragTarget = resolveDragOverTarget(event, scrollAreaRef.current, {
      overId: savedOverId,
      placement: savedPlacement,
    });
    const placement = dragTarget?.placement ?? savedPlacement;
    let overId = dragTarget?.overId ?? savedOverId;
    if (!overId || overId === String(active.id)) {
      overId = savedOverId;
    }

    const targetBoard = overId ? parseBoardDropId(overId) : null;
    const activeGroupId = parseGroupDropId(String(active.id));
    const activeTaskId = parseTaskDragId(String(active.id));

    if (targetBoard && (activeGroupId || activeTaskId)) {
      if (activeGroupId) {
        const groupIds = movingGroupIds.length > 0 ? movingGroupIds : [activeGroupId];
        moveSheetItemsToBoard({
          clientId,
          projectId,
          groupIds,
          taskIds: [],
          targetBoardType: targetBoard,
        });
      } else if (activeTaskId) {
        const taskIds = movingTaskIds.length > 0 ? movingTaskIds : [activeTaskId];
        moveSheetItemsToBoard({
          clientId,
          projectId,
          groupIds: [],
          taskIds,
          targetBoardType: targetBoard,
        });
      }
      clearSheetDragState();
      return;
    }

    clearSheetDragState();
    if (!overId || overId === String(active.id)) return;

    const overviewActiveCol = parseOverviewSectionColDragId(String(active.id));
    if (overviewActiveCol) {
      const overParsed = parseOverviewSectionColDragId(overId);
      if (
        !overParsed ||
        overParsed.sectionBoardType !== overviewActiveCol.sectionBoardType ||
        overviewActiveCol.columnId === overParsed.columnId
      ) {
        return;
      }
      const sectionOrder = getMainOverviewSectionColumnOrder(
        overviewActiveCol.sectionBoardType,
        mainOverviewSectionColumnOrder,
        mainOverviewSectionSheetColumns,
        boardSheetColumnOrder,
        boardSheetColumns
      );
      const oldIndex = sectionOrder.indexOf(overviewActiveCol.columnId);
      const newIndex = sectionOrder.indexOf(overParsed.columnId);
      if (oldIndex === -1 || newIndex === -1) return;
      reorderMainOverviewSectionColumns(
        overviewActiveCol.sectionBoardType,
        arrayMove(sectionOrder, oldIndex, newIndex)
      );
      return;
    }

    const activeColId = parseSheetColDragId(String(active.id));
    if (activeColId) {
      if (isOverview) return;
      const overCol = parseSheetColDragId(overId);
      if (!overCol || activeColId === overCol) return;
      const oldIndex = columnOrder.indexOf(activeColId);
      const newIndex = columnOrder.indexOf(overCol);
      if (oldIndex === -1 || newIndex === -1) return;
      reorderBoardSheetColumns(boardType, arrayMove(columnOrder, oldIndex, newIndex));
      return;
    }

    if (activeGroupId) {
      const groupIds = movingGroupIds.length > 0 ? movingGroupIds : [activeGroupId];

      if (overId === TRASH_DROP_ID) {
        const removable = groupIds.filter((id) => {
          const group = taskGroups.find((g) => g.id === id);
          return group && canRemoveSheetGroup(group);
        });
        if (removable.length > 0) {
          if (removable.length > 1) removeGroups(removable);
          else removeGroup(removable[0]!);
          if (removable.length > 1) clearSelection();
        }
        return;
      }

      if (String(active.id) === overId) return;

      let groupAction: GroupDropAction | null = savedValidGroupAction;
      if (!groupAction) {
        const resolved = resolveGroupDropAction(
          overId,
          activeGroupId,
          placement,
          taskGroups,
          tasks,
          sheetRows
        );
        if (resolved && 'blockedReason' in resolved) {
          setGroupDropFeedback(resolved.blockedReason);
          return;
        }
        if (resolved && 'mode' in resolved) {
          groupAction = resolved;
        }
      }
      if (!groupAction) return;

      const updates = computeSheetGroupsDrop(
        taskGroups,
        projectId,
        groupIds,
        groupAction.targetOverId,
        groupAction.mode,
        groupAction.reorderPlacement
      );
      if (updates) {
        applySheetGroupUpdates(updates);
        setGroupDropFeedback(null);
        if (groupAction.mode === 'nest') {
          const targetGroupId = parseGroupDropId(groupAction.targetOverId);
          if (targetGroupId) expandGroupChain(targetGroupId);
        }
      } else {
        setGroupDropFeedback(
          groupAction.mode === 'reorder'
            ? 'Cannot reorder here — drop onto the group you want to move into.'
            : 'Could not move this group to that location.'
        );
      }
      return;
    }

    if (!activeTaskId) return;

    if (overId === TRASH_DROP_ID) {
      if (movingTaskIds.length > 1) {
        removeTasks(movingTaskIds);
        clearSelection();
      } else {
        handleDeleteTask(activeTaskId);
      }
      return;
    }

    if (String(active.id) === overId) return;

    let taskDropTarget = savedValidTaskDrop;
    if (!taskDropTarget) {
      taskDropTarget = resolveTaskDropTarget(
        overId,
        movingTaskIds.length > 0 ? movingTaskIds : [activeTaskId],
        tasks,
        sheetRows,
        placement
      );
    }
    if (!taskDropTarget) return;

    const updates = computeSheetTasksDrop(
      tasks,
      taskGroups,
      projectId,
      boardType,
      movingTaskIds.length > 0 ? movingTaskIds : [activeTaskId],
      taskDropTarget
    );
    if (updates) {
      applySheetTaskUpdates(updates);
      const expandId = taskDropTarget.targetGroupId;
      if (expandId) expandGroupChain(expandId);
    }
  };

  const handleQuickTaskInGroup = (group: TaskGroup) => {
    const ghostBoard = ghostBoardFromGroupId(group.id);
    const sectionBoard = sectionBoardTypeFromUngroupedBucketId(group.id);
    const flatBoard = isFlatBoard(group.boardType) ? group.boardType : undefined;
    const newId = createTaskInGroup({
      clientId,
      projectId,
      groupId: ghostBoard || group.id.startsWith('__') ? null : group.id,
      boardType:
        ghostBoard ?? sectionBoard ?? flatBoard ?? (isFlatBoardView ? boardType : undefined),
    });
    if (newId) {
      setSelectedTaskIds(new Set([newId]));
      setSelectionAnchorId(newId);
    }
  };

  const handleCreateUngroupedTask = useCallback(
    (targetBoardType: ProjectBoardType = boardType) => {
      const newId = createTaskInGroup({
        clientId,
        projectId,
        groupId: null,
        boardType: targetBoardType,
      });
      if (newId) {
        setSelectedTaskIds(new Set([newId]));
        setSelectionAnchorId(newId);
      }
    },
    [clientId, projectId, boardType, createTaskInGroup]
  );

  const handleAddFlatRow = () => {
    const newId = createTaskInGroup({
      clientId,
      projectId,
      groupId: null,
      boardType,
    });
    if (newId) {
      setSelectedTaskIds(new Set([newId]));
      setSelectionAnchorId(newId);
    }
  };

  const handleAddFlatHeader = () => {
    addFlatBoardHeader(clientId, projectId, boardType);
  };

  const handleQuickSubtask = (taskId: string) => {
    const newId = createSubtask(taskId);
    if (newId) {
      setSelectedTaskIds(new Set([newId]));
      setSelectionAnchorId(newId);
    }
  };

  const openNewTaskModal = (seed?: {
    groupId?: string | null;
    parentTaskId?: string | null;
    boardType?: import('../types').ProjectBoardType;
  }) => {
    setTaskModalSeed(seed ?? null);
    setShowTaskModal(true);
  };

  return (
    <div
      ref={wrapperRef}
      className={styles.wrapper}
      onClick={() => {
        setContextMenu(null);
        setColumnHeaderMenu(null);
      }}
    >
      {(selectedTaskIds.size >= 2 ||
        (selectedGroupIds.size >= 2 && !isGhostBoard)) && (
        <div className={styles.selectionBarHost} aria-live="polite">
          {selectedTaskIds.size >= 2 && (
            <div className={styles.selectionBar} onClick={(e) => e.stopPropagation()}>
            <span className={styles.selectionBarLabel}>
              {selectedTaskIds.size} tasks selected
            </span>
            {hasStatusColumn && (
              <>
                <label className={styles.selectionBarField}>
                  <span className={styles.selectionBarFieldLabel}>Status</span>
                  <select
                    className={`${styles.cellSelect} ${styles.bulkStatusSelect}`}
                    value=""
                    onChange={(e) => {
                      if (e.target.value) handleBulkStatusChange(e.target.value);
                    }}
                  >
                    <option value="">Change status…</option>
                    {bulkStatusOptions.map((status) => (
                      <option key={status.id} value={status.id}>
                        {status.label}
                      </option>
                    ))}
                  </select>
                </label>
                <button
                  type="button"
                  className={styles.selectionBarAction}
                  onClick={() => handleAutoAssignFromStatus([...selectedTaskIds])}
                >
                  Auto-assign from status
                </button>
              </>
            )}
            <button
              type="button"
              className={styles.selectionBarAction}
              onClick={handleDuplicateSelected}
            >
              Duplicate
            </button>
            <button
              type="button"
              className={styles.selectionBarAction}
              onClick={() => setShowFindReplace(true)}
            >
              Find &amp; replace
              <span className={styles.contextShortcut}>Ctrl+F</span>
            </button>
            {hasAssigneeColumn && (
            <label className={styles.selectionBarField}>
              <span className={styles.selectionBarFieldLabel}>Assignee</span>
              <select
                className={`${styles.cellSelect} ${styles.bulkStatusSelect}`}
                value=""
                onChange={(e) => {
                  const value = e.target.value;
                  if (!value) return;
                  handleBulkAssigneeChange(value === '__unassigned__' ? [] : [value]);
                }}
              >
                <option value="">Change assignee…</option>
                <option value="__unassigned__">Unassigned</option>
                {assignableEmployees.map((emp) => (
                  <option key={emp.id} value={emp.id} title={emp.name}>
                    {employeeInitials(emp.name)}
                  </option>
                ))}
              </select>
            </label>
            )}
            <button type="button" className={styles.selectionBarClear} onClick={clearSelection}>
              Clear selection
            </button>
          </div>
        )}
        {selectedGroupIds.size >= 2 && !isGhostBoard && (
          <div className={styles.selectionBar} onClick={(e) => e.stopPropagation()}>
            <span className={styles.selectionBarLabel}>
              {selectedGroupIds.size} groups selected
            </span>
            <button
              type="button"
              className={styles.selectionBarAction}
              onClick={handleDuplicateSelectedGroups}
            >
              Duplicate
            </button>
            <button
              type="button"
              className={styles.selectionBarAction}
              onClick={() => setShowFindReplace(true)}
            >
              Find &amp; replace
              <span className={styles.contextShortcut}>Ctrl+F</span>
            </button>
            <button
              type="button"
              className={`${styles.selectionBarAction} ${styles.selectionBarDelete}`}
              onClick={handleDeleteSelectedGroups}
            >
              Remove groups
            </button>
            <button type="button" className={styles.selectionBarClear} onClick={clearSelection}>
              Clear selection
            </button>
          </div>
        )}
        </div>
      )}
      <DndContext
        sensors={sensors}
        collisionDetection={sheetCollisionDetection}
        onDragStart={handleDragStart}
        onDragOver={handleDragOver}
        onDragEnd={handleDragEnd}
        onDragCancel={clearSheetDragState}
      >
        <BoardTabDropOverlays />
        <div className={styles.scrollArea} ref={scrollAreaRef}>
          {groupDropFeedback && (
            <div className={styles.groupDropFeedback} role="status">
              {groupDropFeedback}
              <button type="button" onClick={() => setGroupDropFeedback(null)} aria-label="Dismiss">
                ×
              </button>
            </div>
          )}
          {(activeDragTask || activeDragGroup) && sheetDropHint && (
            <div className={styles.dragIntentBar} role="status">
              {dropIntentLabel(sheetDropHint.intent)}
              {sheetDropHint.intent === 'regroup'
                ? ' — whole row highlights: group will move inside'
                : sheetDropHint.placement === 'before'
                  ? ' — blue line at top: insert before this row'
                  : ' — blue line at bottom: insert after this row'}
            </div>
          )}
          {isFlatBoardView && (
            <div className={styles.flatToolbar}>
              <button type="button" className={styles.flatToolbarBtn} onClick={handleAddFlatRow}>
                + New row
              </button>
              <button type="button" className={styles.flatToolbarBtn} onClick={handleAddFlatHeader}>
                + Add header
              </button>
              <span className={styles.flatToolbarHint}>
                Rename headers and columns using the + button in the table header.
              </span>
            </div>
          )}
          <div
            className={styles.scrollInner}
            style={{
              minWidth:
                isOverview && overviewSectionBoardTypes.length > 0
                  ? overviewScrollMinWidth
                  : tableMinWidth,
            }}
          >
            {isOverview && overviewSectionBoardTypes.length > 0 ? (
              <SortableContext items={taskDragIds}>
                {overviewSplitRows.preludeRows.length > 0 && (
                  <div
                    className={`${styles.overviewSection}${collapsedOverviewSections.has('general') ? ` ${styles.overviewSectionCollapsed}` : ''}`}
                  >
                    {renderOverviewSectionHeader('General', 'general')}
                    {!collapsedOverviewSections.has('general') && (
                    <table
                      ref={tableRef}
                      className={styles.table}
                      style={{ width: tableMinWidth, minWidth: tableMinWidth }}
                    >
                      <colgroup>
                        <col style={col('row', rowColWidth)} />
                        <col style={col('collapse', collapseColWidth)} />
                        <col style={col('drag', dragColWidth)} />
                        {columnSlots.map((slot) => (
                          <col
                            key={slot.kind === 'fixed' ? slot.id : slot.column.id}
                            style={
                              slot.kind === 'custom'
                                ? col(
                                    customColumnWidthKey(slot.column.id),
                                    defaultCustomColumnWidth(slot.column.type)
                                  )
                                : col(slot.id, DEFAULT_WIDTHS[slot.id as FixedColumnKey])
                            }
                          />
                        ))}
                        <col style={col('actions', columnWidths.actions ?? DEFAULT_WIDTHS.actions)} />
                      </colgroup>
                      <thead>
                        <tr>
                          <th className={styles.colRow} style={{ width: rowColWidth }} />
                          <th className={styles.colCollapse} style={{ width: collapseColWidth }} />
                          <th className={styles.colDragHeader} style={DRAG_COL_STYLE} />
                          {columnSlots.map((slot) =>
                            slot.kind === 'custom' ? (
                              <th key={slot.column.id}>{slot.column.label}</th>
                            ) : (
                              <th key={slot.id}>{FIXED_SHEET_COLUMN_LABELS[slot.id]}</th>
                            )
                          )}
                          <th className={styles.colActionsHeader} />
                        </tr>
                      </thead>
                      <tbody>{renderSheetRowList(overviewSplitRows.preludeRows, columnSlots)}</tbody>
                    </table>
                    )}
                  </div>
                )}
                {overviewSectionBoardTypes.map((sectionBoardType) =>
                  renderOverviewSectionTable(sectionBoardType)
                )}
              </SortableContext>
            ) : (
            <table
              ref={tableRef}
              className={styles.table}
              style={{ width: tableMinWidth, minWidth: tableMinWidth }}
            >
            <colgroup>
              <col style={col('row', rowColWidth)} />
              <col style={col('collapse', collapseColWidth)} />
              <col style={col('drag', dragColWidth)} />
              {columnSlots.map((slot) => (
                <col
                  key={slot.kind === 'fixed' ? slot.id : slot.column.id}
                  style={
                    slot.kind === 'custom'
                      ? col(
                          customColumnWidthKey(slot.column.id),
                          defaultCustomColumnWidth(slot.column.type)
                        )
                      : col(slot.id, DEFAULT_WIDTHS[slot.id as FixedColumnKey])
                  }
                />
              ))}
              <col style={col('actions', columnWidths.actions ?? DEFAULT_WIDTHS.actions)} />
            </colgroup>
            <SortableContext
              key={`sheet-col-headers-${boardType}`}
              items={headerSortableIds}
              strategy={horizontalListSortingStrategy}
            >
            <thead>
              <tr>
                <SortableResizableHeader
                  columnKey="row"
                  label=""
                  width={rowColWidth}
                  onResize={handleResize}
                  resizable
                  draggable={false}
                  userResized={isColumnUserResized('row')}
                  locked={isColumnLocked('row')}
                  onHeaderContextMenu={(e) => openColumnHeaderMenu('row', undefined, e)}
                  onAutoFit={autoFitColumn}
                />
                <SortableResizableHeader
                  columnKey="collapse"
                  label=""
                  width={collapseColWidth}
                  onResize={handleResize}
                  resizable
                  draggable={false}
                  userResized={isColumnUserResized('collapse')}
                  locked={isColumnLocked('collapse')}
                  onHeaderContextMenu={(e) => openColumnHeaderMenu('collapse', undefined, e)}
                  onAutoFit={autoFitColumn}
                />
                <SortableResizableHeader
                  columnKey="drag"
                  label=""
                  width={dragColWidth}
                  onResize={handleResize}
                  resizable={false}
                  draggable={false}
                  userResized={isColumnUserResized('drag')}
                  locked={isColumnLocked('drag')}
                  onHeaderContextMenu={(e) => openColumnHeaderMenu('drag', undefined, e)}
                  onAutoFit={autoFitColumn}
                  headerClassName={styles.colDragHeader}
                />
                {columnSlots.map((slot) => renderColumnHeader(slot))}
                <SortableResizableHeader
                  columnKey="actions"
                  label=""
                  width={columnWidths.actions ?? DEFAULT_WIDTHS.actions}
                  onResize={handleResize}
                  resizable
                  draggable={false}
                  headerClassName={styles.colActionsHeader}
                  userResized={isColumnUserResized('actions')}
                  locked={isColumnLocked('actions')}
                  onHeaderContextMenu={(e) => openColumnHeaderMenu('actions', undefined, e)}
                  onAutoFit={autoFitColumn}
                  headerAction={
                    allowAddColumns ? (
                    <button
                      type="button"
                      className={styles.columnManageBtn}
                      onClick={(e) => {
                        e.stopPropagation();
                        setShowColumnSettings(true);
                        setContextMenu(null);
                      }}
                      title="Add column"
                    >
                      +
                    </button>
                    ) : undefined
                  }
                />
              </tr>
            </thead>
            </SortableContext>
            <SortableContext items={taskDragIds}>
              <tbody>
                {sheetRows.length === 0 ? (
                  <tr>
                    <td colSpan={colSpan} className={styles.empty}>
                      {isFlatBoardView
                        ? 'No rows yet. Click "+ New row" to get started.'
                        : isGhostBoard
                          ? 'No tasks for this board yet. Assign tasks on Main Overview using the Board column or by placing them under this board’s section.'
                          : 'No tasks yet. Use "+ New" on a group, add a group, or create an ungrouped task to get started.'}
                    </td>
                  </tr>
                ) : (
                  renderSheetRowList(sheetRows, columnSlots)
                )}
              </tbody>
            </SortableContext>
          </table>
            )}
          <DragOverlay>
            {activeDragGroup ? (
              <div className={styles.dragOverlay} data-drag-overlay>
                <span className={styles.dragOverlayGrip}>⠿</span>
                {activeDragGroupIds.length > 1
                  ? `${activeDragGroupIds.length} groups`
                  : activeDragGroup.name}
              </div>
            ) : activeDragTask ? (
              <div className={styles.dragOverlay} data-drag-overlay>
                <span className={styles.dragOverlayGrip}>⠿</span>
                {activeDragTaskIds.length > 1
                  ? `${activeDragTaskIds.length} tasks`
                  : activeDragTask.title}
              </div>
            ) : null}
          </DragOverlay>
          </div>
          {showTradeGroupWorkspace && (
            <div
              className={styles.sheetWorkspacePad}
              onContextMenu={(e) => {
                e.preventDefault();
                e.stopPropagation();
                setContextMenu({ kind: 'workspace', x: e.clientX, y: e.clientY });
              }}
            />
          )}
          {(activeDragTask || activeDragGroup) && (
            <div className={styles.trashOverlay}>
              <TrashDropZone />
            </div>
          )}
        </div>
      </DndContext>
      {contextMenu?.kind === 'task' && (() => {
        const menuTask = tasks.find((task) => task.id === contextMenu.taskId);
        const assigneesLocked = menuTask?.assigneesLocked ?? false;

        return (
        <ContextMenuPanel
          key={`task-${contextMenu.x}-${contextMenu.y}`}
          x={contextMenu.x}
          y={contextMenu.y}
          className={styles.contextMenu}
          onClick={(e) => e.stopPropagation()}
        >
          {selectedTaskIds.size >= 2 && hasStatusColumn && (
            <>
              <div className={styles.contextMenuLabel}>
                Set status ({selectedTaskIds.size} tasks)
              </div>
              {bulkStatusOptions.map((status) => (
                <button
                  key={status.id}
                  type="button"
                  onClick={() => handleBulkStatusChange(status.id)}
                >
                  <span style={{ color: status.color }}>●</span>
                  {status.label}
                </button>
              ))}
              <div className={styles.contextMenuDivider} />
              <button
                type="button"
                onClick={() => {
                  setShowFindReplace(true);
                  setContextMenu(null);
                }}
              >
                Find &amp; replace ({selectedTaskIds.size} tasks)
                <span className={styles.contextShortcut}>Ctrl+F</span>
              </button>
              <div className={styles.contextMenuDivider} />
            </>
          )}
          {selectedTaskIds.size >= 2 && !hasStatusColumn && (
            <>
              <button
                type="button"
                onClick={() => {
                  setShowFindReplace(true);
                  setContextMenu(null);
                }}
              >
                Find &amp; replace ({selectedTaskIds.size} tasks)
                <span className={styles.contextShortcut}>Ctrl+F</span>
              </button>
              <div className={styles.contextMenuDivider} />
            </>
          )}
          {selectedTaskIds.size === 1 && (
            <>
              <button
                type="button"
                onClick={() => {
                  setShowFindReplace(true);
                  setContextMenu(null);
                }}
              >
                Find &amp; replace
                <span className={styles.contextShortcut}>Ctrl+F</span>
              </button>
              <div className={styles.contextMenuDivider} />
            </>
          )}
          {hasStatusColumn && (
          <button
            type="button"
            onClick={() => {
              handleAutoAssignFromStatus(
                selectedTaskIds.size >= 2 ? [...selectedTaskIds] : [contextMenu.taskId]
              );
            }}
          >
            {assigneesLocked ? 'Resume auto-assign from status' : 'Auto-assign from status'}
            {selectedTaskIds.size >= 2 ? ` (${selectedTaskIds.size} tasks)` : ''}
          </button>
          )}
          <button
            type="button"
            onClick={() => {
              handleQuickSubtask(contextMenu.taskId);
              setContextMenu(null);
            }}
          >
            Create subtask
          </button>
          <button
            type="button"
            onClick={() => {
              handleCopy(contextMenu.taskId);
              setContextMenu(null);
            }}
          >
            Copy
            <span className={styles.contextShortcut}>Ctrl+C</span>
          </button>
          <button
            type="button"
            disabled={!taskClipboard}
            onClick={() => {
              handlePaste(contextMenu.taskId);
              setContextMenu(null);
            }}
          >
            Paste below
            <span className={styles.contextShortcut}>Ctrl+V</span>
          </button>
          <button
            type="button"
            onClick={() => {
              if (selectedTaskIds.size > 1) handleDuplicateSelected();
              else handleDuplicate(contextMenu.taskId);
              setContextMenu(null);
            }}
          >
            Duplicate
            {selectedTaskIds.size > 1 ? ` (${selectedTaskIds.size})` : ''}
          </button>
          <button
            type="button"
            className={styles.contextMenuDelete}
            onClick={() => {
              if (selectedTaskIds.size > 1) handleDeleteSelected();
              else handleDeleteTask(contextMenu.taskId);
            }}
          >
            Delete
            {selectedTaskIds.size > 1 ? ` (${selectedTaskIds.size})` : ''}
            <span className={styles.contextShortcut}>Del</span>
          </button>
        </ContextMenuPanel>
        );
      })()}
      {contextMenu?.kind === 'group' && (
        <ContextMenuPanel
          key={`group-${contextMenu.x}-${contextMenu.y}`}
          x={contextMenu.x}
          y={contextMenu.y}
          className={styles.contextMenu}
          onClick={(e) => e.stopPropagation()}
        >
          {isUngroupedBucketGroupId(contextMenu.group.id) && (
            <>
              <button
                type="button"
                onClick={() => {
                  handleQuickTaskInGroup(contextMenu.group);
                  setContextMenu(null);
                }}
              >
                New ungrouped task
              </button>
              <button
                type="button"
                onClick={() => {
                  const section =
                    (contextMenu.group.parentId
                      ? taskGroups.find((g) => g.id === contextMenu.group.parentId)
                      : undefined) ??
                    taskGroups.find(
                      (g) =>
                        g.projectId === projectId &&
                        g.clientId === clientId &&
                        g.tier === 'section' &&
                        g.sectionBoardType ===
                          sectionBoardTypeFromUngroupedBucketId(contextMenu.group.id)
                    );
                  if (section) handleSectionAddChild(section);
                  setContextMenu(null);
                }}
              >
                Add group
              </button>
            </>
          )}
          {contextMenu.group.tier === 'section' && !isGhostBoard && (
            <>
              <button
                type="button"
                onClick={() => {
                  if (contextMenu.group.sectionBoardType) {
                    handleCreateUngroupedTask(contextMenu.group.sectionBoardType);
                  }
                  setContextMenu(null);
                }}
              >
                New ungrouped task
              </button>
              <button
                type="button"
                onClick={() => {
                  handleSectionAddChild(contextMenu.group);
                  setContextMenu(null);
                }}
              >
                Add group
              </button>
            </>
          )}
          {(contextMenu.group.tier === 'parent' || contextMenu.group.tier === 'child') &&
            !isUngroupedBucketGroupId(contextMenu.group.id) && (
            <>
              <button
                type="button"
                onClick={() => {
                  handleQuickTaskInGroup(contextMenu.group);
                  setContextMenu(null);
                }}
              >
                New task
              </button>
              <button
                type="button"
                onClick={() => {
                  handleAddGroupUnder(contextMenu.group);
                  setContextMenu(null);
                }}
              >
                New group
              </button>
              <button
                type="button"
                onClick={() => {
                  const ghostBoard = ghostBoardFromGroupId(contextMenu.group.id);
                  openNewTaskModal({
                    groupId:
                      ghostBoard || contextMenu.group.id.startsWith('__')
                        ? null
                        : contextMenu.group.id,
                    boardType:
                      ghostBoard ?? boardTypeForGroup(taskGroups, contextMenu.group.id),
                  });
                  setContextMenu(null);
                }}
              >
                New task (full form)…
              </button>
            </>
          )}
          {(contextMenu.group.tier === 'parent' || contextMenu.group.tier === 'child') &&
            !isUngroupedBucketGroupId(contextMenu.group.id) &&
            !contextMenu.group.id.startsWith('__') && (
            <>
              <div className={styles.contextMenuDivider} />
              <button
                type="button"
                onClick={() => {
                  const visible = shouldShowGroupProgressBar(contextMenu.group, taskGroups);
                  updateGroup(contextMenu.group.id, { showProgressBar: !visible });
                  setContextMenu(null);
                }}
              >
                {shouldShowGroupProgressBar(contextMenu.group, taskGroups)
                  ? 'Hide progress bar'
                  : 'Show progress bar'}
              </button>
            </>
          )}
          {canRemoveSheetGroup(contextMenu.group) && (
            <>
              {selectedGroupIds.size >= 2 && selectedGroupIds.has(contextMenu.group.id) && (
                <>
                  <button
                    type="button"
                    onClick={() => {
                      setShowFindReplace(true);
                      setContextMenu(null);
                    }}
                  >
                    Find &amp; replace ({selectedGroupIds.size} groups)
                    <span className={styles.contextShortcut}>Ctrl+F</span>
                  </button>
                  <div className={styles.contextMenuDivider} />
                </>
              )}
              <button
                type="button"
                onClick={() => {
                  const bulk =
                    selectedGroupIds.size > 1 && selectedGroupIds.has(contextMenu.group.id);
                  if (bulk) handleDuplicateSelectedGroups();
                  else handleDuplicateGroup(contextMenu.group.id);
                }}
              >
                Duplicate
                {selectedGroupIds.size > 1 && selectedGroupIds.has(contextMenu.group.id)
                  ? ` (${selectedGroupIds.size})`
                  : ''}
              </button>
              <button
                type="button"
                className={styles.contextMenuDelete}
                onClick={() => {
                  const bulk =
                    selectedGroupIds.size > 1 && selectedGroupIds.has(contextMenu.group.id);
                  if (bulk) handleDeleteSelectedGroups();
                  else handleDeleteGroup(contextMenu.group.id);
                }}
              >
                Remove group
                {selectedGroupIds.size > 1 && selectedGroupIds.has(contextMenu.group.id)
                  ? `s (${selectedGroupIds.size})`
                  : ''}
              </button>
            </>
          )}
        </ContextMenuPanel>
      )}
      {contextMenu?.kind === 'workspace' && (
        <ContextMenuPanel
          key={`workspace-${contextMenu.x}-${contextMenu.y}`}
          x={contextMenu.x}
          y={contextMenu.y}
          className={styles.contextMenu}
          onClick={(e) => e.stopPropagation()}
        >
          <button
            type="button"
            onClick={() => {
              handleCreateUngroupedTask();
              setContextMenu(null);
            }}
          >
            New ungrouped task
          </button>
          <button
            type="button"
            onClick={() => {
              handleAddTradeGroup();
              setContextMenu(null);
            }}
          >
            New trade group
          </button>
        </ContextMenuPanel>
      )}
      {showColumnSettings && (allowAddColumns || canDeleteColumns) && (
        <ColumnSettings
          initialBoardType={boardType}
          boards={statusBoardOptions}
          overviewSectionBoards={isOverview ? overviewSectionOptions : undefined}
          overviewSectionBoardType={overviewColumnSection ?? overviewSectionOptions[0]?.id}
          onClose={() => setShowColumnSettings(false)}
        />
      )}
      {editColumn && (
        <EditCustomColumnDialog
          column={editColumn}
          onSave={(updates) => {
            const sectionBoardType = overviewColumnSection ?? columnHeaderMenu?.sectionBoardType;
            if (isOverview && sectionBoardType) {
              const extras = mainOverviewSectionSheetColumns[sectionBoardType] ?? [];
              if (extras.some((column) => column.id === editColumn.id)) {
                updateMainOverviewSectionColumn(sectionBoardType, editColumn.id, updates);
              } else {
                updateBoardSheetColumn(sectionBoardType, editColumn.id, updates);
              }
              return;
            }
            updateBoardSheetColumn(boardType, editColumn.id, updates);
          }}
          onClose={() => setEditColumn(null)}
        />
      )}
      {columnHeaderMenu && (
        <ContextMenuPanel
          key={`col-${columnHeaderMenu.x}-${columnHeaderMenu.y}`}
          x={columnHeaderMenu.x}
          y={columnHeaderMenu.y}
          className={styles.contextMenu}
          onClick={(e) => e.stopPropagation()}
        >
          {columnHeaderMenu.columnKey === 'title' &&
            !isFlatBoardView &&
            ((columnHeaderMenu.sectionBoardType
              ? getSectionForBoard(
                  taskGroups,
                  clientId,
                  projectId,
                  columnHeaderMenu.sectionBoardType
                )
              : null) ??
              activeBoardSection) && (
              <>
                <button
                  type="button"
                  onClick={() => {
                    const section =
                      (columnHeaderMenu.sectionBoardType
                        ? getSectionForBoard(
                            taskGroups,
                            clientId,
                            projectId,
                            columnHeaderMenu.sectionBoardType
                          )
                        : null) ?? activeBoardSection;
                    if (section) handleAddGroupUnder(section);
                    setColumnHeaderMenu(null);
                  }}
                >
                  New group
                </button>
                <button
                  type="button"
                  onClick={() => {
                    const section =
                      (columnHeaderMenu.sectionBoardType
                        ? getSectionForBoard(
                            taskGroups,
                            clientId,
                            projectId,
                            columnHeaderMenu.sectionBoardType
                          )
                        : null) ?? activeBoardSection;
                    if (section?.sectionBoardType) {
                      handleCreateUngroupedTask(section.sectionBoardType);
                    } else {
                      handleCreateUngroupedTask();
                    }
                    setColumnHeaderMenu(null);
                  }}
                >
                  New ungrouped task
                </button>
                <div className={styles.contextMenuDivider} />
              </>
            )}
          <button
            type="button"
            onClick={() => {
              toggleColumnWidthLock(
                columnHeaderMenu.columnKey,
                columnHeaderMenu.sectionBoardType
              );
              setColumnHeaderMenu(null);
            }}
          >
            {isColumnLocked(
              columnHeaderMenu.columnKey,
              columnHeaderMenu.sectionBoardType
            )
              ? 'Unlock column width'
              : 'Lock column width'}
          </button>
          {columnHeaderMenu.customColumn &&
            (canDeleteColumns || canEditMaterialOptions) && (
            <>
              <div className={styles.contextMenuDivider} />
              <button
                type="button"
                onClick={() => {
                  setInlineRenameColumnId(columnHeaderMenu.customColumn!.id);
                  setColumnHeaderMenu(null);
                }}
              >
                Rename column
              </button>
              <button
                type="button"
                onClick={() => {
                  if (columnHeaderMenu.sectionBoardType) {
                    setOverviewColumnSection(columnHeaderMenu.sectionBoardType);
                  }
                  setEditColumn(columnHeaderMenu.customColumn!);
                  setColumnHeaderMenu(null);
                }}
              >
                Edit column
              </button>
              {canEditMaterialOptions &&
                columnHeaderMenu.customColumn.type === 'dropdown' && (
                  <button
                    type="button"
                    onClick={() => {
                      const column = columnHeaderMenu.customColumn!;
                      const alreadyManaged = managedDropdownIds.has(column.id);
                      if (!alreadyManaged) {
                        addColumnSettingsDropdown(column.id);
                      }
                      openColumnSettingsHub({ dropdownColumnId: column.id });
                      setColumnHeaderMenu(null);
                    }}
                  >
                    {managedDropdownIds.has(columnHeaderMenu.customColumn.id)
                      ? 'Manage options in Column Settings…'
                      : 'Add to Column Settings…'}
                  </button>
                )}
            </>
          )}
          {canDeleteColumns &&
            (() => {
              const columnId =
                columnHeaderMenu.customColumn?.id ??
                (isFixedSheetColumnId(columnHeaderMenu.columnKey)
                  ? columnHeaderMenu.columnKey
                  : null);
              if (!columnId || !canRemoveColumn(columnId, columnHeaderMenu.sectionBoardType)) {
                return null;
              }
              const needsDivider = !columnHeaderMenu.customColumn;
              return (
                <>
                  {needsDivider && <div className={styles.contextMenuDivider} />}
                  <button
                    type="button"
                    className={styles.contextMenuDelete}
                    onClick={() => {
                      const sectionBoardType = columnHeaderMenu.sectionBoardType;
                      if (isOverview && sectionBoardType) {
                        handleRemoveOverviewSectionColumn(sectionBoardType, columnId);
                      } else {
                        removeBoardSheetColumn(boardType, columnId);
                      }
                      setColumnHeaderMenu(null);
                    }}
                  >
                    Remove column
                  </button>
                </>
              );
            })()}
        </ContextMenuPanel>
      )}
      {showStatusSettings && allowManageStatuses && (
        <StatusSettings
          initialBoardType={boardType}
          boards={statusBoardOptions}
          projectId={projectId}
          onClose={() => setShowStatusSettings(false)}
        />
      )}
      {showTaskModal && (
        <TaskModal
          onClose={() => {
            setShowTaskModal(false);
            setTaskModalSeed(null);
          }}
          initial={{
            clientId,
            projectId,
            groupId: taskModalSeed?.groupId,
            parentTaskId: taskModalSeed?.parentTaskId,
          }}
        />
      )}
      {attachmentTask && (
        <AttachmentDialog task={attachmentTask} onClose={() => setAttachmentTask(null)} />
      )}
      {commentTask && <CommentDialog task={commentTask} onClose={() => setCommentTask(null)} />}
      {showFindReplace && (
        <FindReplaceDialog
          tasks={tasks}
          taskIds={findReplaceTaskIds}
          groups={taskGroups}
          groupIds={findReplaceGroupIds}
          customTextColumns={findReplaceTextColumns}
          onApply={handleFindReplaceApply}
          onClose={() => setShowFindReplace(false)}
        />
      )}
    </div>
  );
}
