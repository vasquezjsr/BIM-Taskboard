import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useStore } from '../store/useStore';
import { getBoardLabel, type ProjectBoardType, type Task } from '../types';
import { getMyOfficeWorkTasks, MY_WORK_BOARD_TYPES, myWorkDueColumnLabel, taskMyWorkDueDate } from '../utils/myWorkTasks';
import { buildTaskCommentReadStateMap } from '../utils/taskComments';
import {
  getBoardTaskStatuses,
  isCompleteStatus,
} from '../utils/taskStatuses';
import {
  formatEntryTimeRange,
  isOpenTimeEntry,
  localNowTimeString,
  localTodayIsoDate,
  prepareCompletedClockTimes,
} from '../utils/timeEntry';
import { AttachmentDialog } from './AttachmentDialog';
import { CommentDialog } from './CommentDialog';
import styles from './MyWorkDashboardView.module.css';

type BoardFilter = 'all' | ProjectBoardType;

type MyWorkColumnId =
  | 'meta'
  | 'taskNumber'
  | 'title'
  | 'board'
  | 'project'
  | 'status'
  | 'due'
  | 'time';

const MY_WORK_COLUMNS: {
  id: MyWorkColumnId;
  label: string;
  minWidth: number;
  defaultWidth: number;
}[] = [
  { id: 'meta', label: '', minWidth: 72, defaultWidth: 84 },
  { id: 'title', label: 'Title', minWidth: 140, defaultWidth: 240 },
  { id: 'project', label: 'Project', minWidth: 140, defaultWidth: 220 },
  { id: 'board', label: 'Board', minWidth: 90, defaultWidth: 120 },
  { id: 'taskNumber', label: 'Task #', minWidth: 100, defaultWidth: 140 },
  { id: 'status', label: 'Status', minWidth: 110, defaultWidth: 160 },
  { id: 'due', label: 'Due', minWidth: 110, defaultWidth: 150 },
  { id: 'time', label: 'Time', minWidth: 110, defaultWidth: 140 },
];

/** Legacy unscoped key — migrated once into per-user keys. */
const COLUMN_WIDTHS_STORAGE_KEY_LEGACY = 'bim-my-work-column-widths-v2';
/** Per-user localStorage only — never shared store / never other users. */
const COLUMN_WIDTHS_STORAGE_PREFIX = 'bim-my-work-column-widths-v3:';

type ColumnWidths = Record<MyWorkColumnId, number>;

function columnWidthsStorageKey(userId: string | null): string {
  return `${COLUMN_WIDTHS_STORAGE_PREFIX}${userId ?? '_guest'}`;
}

function defaultColumnWidths(): ColumnWidths {
  return Object.fromEntries(
    MY_WORK_COLUMNS.map((column) => [column.id, column.defaultWidth])
  ) as ColumnWidths;
}

function parseColumnWidths(raw: string | null): ColumnWidths | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<Record<string, number>>;
    const next = defaultColumnWidths();
    let any = false;
    for (const column of MY_WORK_COLUMNS) {
      const value = parsed[column.id];
      if (typeof value === 'number' && Number.isFinite(value)) {
        next[column.id] = Math.max(column.minWidth, Math.round(value));
        any = true;
      }
    }
    return any ? next : null;
  } catch {
    return null;
  }
}

/**
 * My Work column layout is personal: localStorage keyed by employee, not Zustand.
 * Resizing here must never affect another user's view.
 */
function loadColumnWidths(userId: string | null): ColumnWidths {
  const defaults = defaultColumnWidths();
  try {
    const scoped = parseColumnWidths(localStorage.getItem(columnWidthsStorageKey(userId)));
    if (scoped) return scoped;

    // One-time migrate legacy machine-wide widths into this user's key.
    const legacy = parseColumnWidths(localStorage.getItem(COLUMN_WIDTHS_STORAGE_KEY_LEGACY));
    if (legacy && userId) {
      localStorage.setItem(columnWidthsStorageKey(userId), JSON.stringify(legacy));
      localStorage.removeItem(COLUMN_WIDTHS_STORAGE_KEY_LEGACY);
      return legacy;
    }
    return defaults;
  } catch {
    return defaults;
  }
}

function saveColumnWidths(userId: string | null, widths: ColumnWidths): void {
  try {
    localStorage.setItem(columnWidthsStorageKey(userId), JSON.stringify(widths));
  } catch {
    // ignore quota / private mode
  }
}

function MyWorkResizableHeader({
  columnId,
  label,
  width,
  minWidth,
  onResize,
}: {
  columnId: MyWorkColumnId;
  label: string;
  width: number;
  minWidth: number;
  onResize: (columnId: MyWorkColumnId, width: number) => void;
}) {
  const startXRef = useRef(0);
  const startWidthRef = useRef(0);

  const handleMouseDown = useCallback(
    (event: React.MouseEvent) => {
      if (event.detail > 1) return;
      event.preventDefault();
      event.stopPropagation();
      startXRef.current = event.clientX;
      startWidthRef.current = width;

      const onMove = (moveEvent: MouseEvent) => {
        const next = Math.max(
          minWidth,
          Math.round(startWidthRef.current + (moveEvent.clientX - startXRef.current))
        );
        onResize(columnId, next);
      };
      const onUp = () => {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';
      };
      document.body.style.cursor = 'col-resize';
      document.body.style.userSelect = 'none';
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    },
    [columnId, minWidth, onResize, width]
  );

  return (
    <th style={{ width, minWidth: width, maxWidth: width }}>
      <div className={styles.thInner}>
        <span className={styles.thLabel}>{label}</span>
        <button
          type="button"
          className={styles.resizeHandle}
          aria-label={`Resize ${label} column`}
          title="Drag to resize"
          onMouseDown={handleMouseDown}
        />
      </div>
    </th>
  );
}

export function MyWorkDashboardView() {
  const currentUserId = useStore((s) => s.currentUserId);
  const tasks = useStore((s) => s.tasks);
  const projects = useStore((s) => s.projects);
  const clients = useStore((s) => s.clients);
  const timeEntries = useStore((s) => s.timeEntries);
  const taskAttachments = useStore((s) => s.taskAttachments);
  const taskComments = useStore((s) => s.taskComments);
  const taskCommentReadAt = useStore((s) => s.taskCommentReadAt);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const addTimeEntry = useStore((s) => s.addTimeEntry);
  const updateTimeEntry = useStore((s) => s.updateTimeEntry);
  const openProjectBoard = useStore((s) => s.openProjectBoard);

  const [boardFilter, setBoardFilter] = useState<BoardFilter>('all');
  const [showCompleted, setShowCompleted] = useState(false);
  const [attachmentTask, setAttachmentTask] = useState<Task | null>(null);
  const [commentTask, setCommentTask] = useState<Task | null>(null);
  const [columnWidths, setColumnWidths] = useState<ColumnWidths>(() =>
    loadColumnWidths(currentUserId)
  );
  const skipNextColumnWidthsSaveRef = useRef(false);

  // Switch user → load that user's private My Work column layout only.
  useEffect(() => {
    skipNextColumnWidthsSaveRef.current = true;
    setColumnWidths(loadColumnWidths(currentUserId));
  }, [currentUserId]);

  useEffect(() => {
    if (skipNextColumnWidthsSaveRef.current) {
      skipNextColumnWidthsSaveRef.current = false;
      return;
    }
    saveColumnWidths(currentUserId, columnWidths);
  }, [currentUserId, columnWidths]);

  const handleColumnResize = useCallback((columnId: MyWorkColumnId, width: number) => {
    setColumnWidths((prev) => ({ ...prev, [columnId]: width }));
  }, []);

  const myTasks = useMemo(
    () =>
      getMyOfficeWorkTasks(
        tasks,
        currentUserId,
        boardTaskStatuses,
        projectBoardTaskStatuses,
        { includeCompleted: showCompleted }
      ),
    [tasks, currentUserId, boardTaskStatuses, projectBoardTaskStatuses, showCompleted]
  );

  const visibleTasks = useMemo(
    () =>
      boardFilter === 'all'
        ? myTasks
        : myTasks.filter((task) => task.boardType === boardFilter),
    [myTasks, boardFilter]
  );

  /** Hide Board column when filtered to one board (Spooling Dashboard style — only show when needed). */
  const showBoardColumn = boardFilter === 'all';

  const activeColumns = useMemo(
    () => MY_WORK_COLUMNS.filter((column) => column.id !== 'board' || showBoardColumn),
    [showBoardColumn]
  );

  const dueColumnLabel = useMemo(
    () => myWorkDueColumnLabel(boardFilter, visibleTasks),
    [boardFilter, visibleTasks]
  );

  const tableWidth = useMemo(
    () => activeColumns.reduce((sum, column) => sum + columnWidths[column.id], 0),
    [activeColumns, columnWidths]
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

  const openClockByTaskId = useMemo(() => {
    const map = new Map<string, (typeof timeEntries)[number]>();
    if (!currentUserId) return map;
    for (const entry of timeEntries) {
      if (
        entry.employeeId === currentUserId &&
        entry.taskId &&
        isOpenTimeEntry(entry)
      ) {
        map.set(entry.taskId, entry);
      }
    }
    return map;
  }, [timeEntries, currentUserId]);

  const anyOpenClock = useMemo(() => {
    if (!currentUserId) return null;
    return (
      timeEntries.find(
        (entry) => entry.employeeId === currentUserId && isOpenTimeEntry(entry)
      ) ?? null
    );
  }, [timeEntries, currentUserId]);

  const projectLabel = useCallback(
    (projectId: string | null) => {
      if (!projectId) return '—';
      const project = projects.find((p) => p.id === projectId);
      if (!project) return 'Unknown project';
      const client = clients.find((c) => c.id === project.clientId);
      const code = project.jobCode ? `${project.jobCode} · ` : '';
      return `${code}${client?.name ?? 'Client'} / ${project.name}`;
    },
    [projects, clients]
  );

  const statusMeta = useCallback(
    (task: Task) => {
      const statuses = getBoardTaskStatuses(
        task.boardType as ProjectBoardType,
        boardTaskStatuses,
        task.projectId,
        projectBoardTaskStatuses
      );
      const def = statuses.find((s) => s.id === task.status);
      return {
        label: def?.label ?? task.status,
        color: def?.color ?? '#94a3b8',
        complete: isCompleteStatus(task.status, statuses),
      };
    },
    [boardTaskStatuses, projectBoardTaskStatuses]
  );

  const handleClockIn = useCallback(
    (task: Task) => {
      if (!currentUserId || anyOpenClock) return;
      const now = new Date();
      addTimeEntry({
        employeeId: currentUserId,
        clientId: task.clientId,
        projectId: task.projectId,
        taskId: task.id,
        date: localTodayIsoDate(now),
        startTime: localNowTimeString(now),
        endTime: null,
        hours: 0,
        note: `My Work · ${getBoardLabel(task.boardType as ProjectBoardType)} · ${task.title}`,
      });
    },
    [currentUserId, anyOpenClock, addTimeEntry]
  );

  const handleClockOut = useCallback(
    (taskId: string) => {
      const open = openClockByTaskId.get(taskId);
      if (!open) return;
      const completed = prepareCompletedClockTimes(open.startTime, localNowTimeString());
      if (!completed) return;
      updateTimeEntry(open.id, {
        employeeId: open.employeeId,
        clientId: open.clientId,
        projectId: open.projectId,
        taskId: open.taskId,
        date: open.date,
        startTime: completed.startTime,
        endTime: completed.endTime,
        hours: completed.hours,
        note: open.note,
      });
    },
    [openClockByTaskId, updateTimeEntry]
  );

  const openTaskOnBoard = useCallback(
    (task: Task) => {
      if (!task.clientId || !task.projectId) return;
      openProjectBoard(task.clientId, task.projectId, task.boardType as ProjectBoardType);
    },
    [openProjectBoard]
  );

  if (!currentUserId) {
    return (
      <div className={styles.root}>
        <header className={styles.header}>
          <div>
            <h1 className={styles.title}>My Work</h1>
            <p className={styles.subtitle}>Sign in to see your assigned office work.</p>
          </div>
        </header>
      </div>
    );
  }

  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <div>
          <h1 className={styles.title}>My Work</h1>
          <p className={styles.subtitle}>
            Your assigned tasks across office boards through Spooling — same spreadsheet feel as the
            Spooling Dashboard. Columns adapt to the boards you filter. Drag column edges to resize.
          </p>
        </div>
        {anyOpenClock ? (
          <div className={styles.activeClockBanner}>
            <span className={styles.activeClockDot} aria-hidden />
            Clocked in
            {anyOpenClock.taskId
              ? ` · ${tasks.find((t) => t.id === anyOpenClock.taskId)?.title ?? 'task'}`
              : ''}
            <span className={styles.activeClockTime}>
              {formatEntryTimeRange(anyOpenClock)}
            </span>
          </div>
        ) : null}
      </header>

      <div className={styles.sheetArea}>
        <div className={styles.sheetToolbar}>
          <div className={styles.filters}>
            <button
              type="button"
              className={`${styles.filterChip} ${boardFilter === 'all' ? styles.filterChipActive : ''}`}
              onClick={() => setBoardFilter('all')}
            >
              All
            </button>
            {MY_WORK_BOARD_TYPES.map((board) => (
              <button
                key={board}
                type="button"
                className={`${styles.filterChip} ${boardFilter === board ? styles.filterChipActive : ''}`}
                onClick={() => setBoardFilter(board)}
              >
                {getBoardLabel(board)}
              </button>
            ))}
          </div>
          <label className={styles.completedToggle}>
            <input
              type="checkbox"
              checked={showCompleted}
              onChange={(e) => setShowCompleted(e.target.checked)}
            />
            Show completed
          </label>
        </div>

        {visibleTasks.length === 0 ? (
          <p className={styles.empty}>
            {myTasks.length === 0
              ? 'No open office tasks are assigned to you right now.'
              : 'No tasks match this board filter.'}
          </p>
        ) : (
          <div className={styles.scrollArea}>
            <table className={styles.table} style={{ width: tableWidth, minWidth: tableWidth }}>
              <colgroup>
                {activeColumns.map((column) => (
                  <col
                    key={column.id}
                    style={{
                      width: columnWidths[column.id],
                      minWidth: columnWidths[column.id],
                    }}
                  />
                ))}
              </colgroup>
              <thead>
                <tr>
                  {activeColumns.map((column) =>
                    column.id === 'meta' ? (
                      <th
                        key={column.id}
                        className={styles.metaHeader}
                        style={{
                          width: columnWidths.meta,
                          minWidth: columnWidths.meta,
                          maxWidth: columnWidths.meta,
                        }}
                        aria-label="Attachments, comments, and open on board"
                      />
                    ) : (
                      <MyWorkResizableHeader
                        key={column.id}
                        columnId={column.id}
                        label={column.id === 'due' ? dueColumnLabel : column.label}
                        width={columnWidths[column.id]}
                        minWidth={column.minWidth}
                        onResize={handleColumnResize}
                      />
                    )
                  )}
                </tr>
              </thead>
              <tbody>
                {visibleTasks.map((task) => {
                  const status = statusMeta(task);
                  const openClock = openClockByTaskId.get(task.id) ?? null;
                  const clockBlockedByOther =
                    Boolean(anyOpenClock) && anyOpenClock?.taskId !== task.id;
                  const attachmentCount = attachmentCountByTask.get(task.id) ?? 0;
                  const commentCount = commentCountByTask.get(task.id) ?? 0;
                  const commentReadState = commentReadStateByTask.get(task.id) ?? 'none';
                  const cellStyle = (id: MyWorkColumnId) => ({
                    width: columnWidths[id],
                    minWidth: columnWidths[id],
                    maxWidth: columnWidths[id],
                  });

                  return (
                    <tr
                      key={task.id}
                      className={openClock ? styles.rowClockedIn : undefined}
                    >
                      <td className={styles.metaCell} style={cellStyle('meta')}>
                        <div className={styles.rowIcons}>
                          <button
                            type="button"
                            className={`${styles.rowIconBtn} ${
                              commentReadState === 'unread'
                                ? styles.rowIconUnread
                                : commentCount > 0
                                  ? styles.rowIconActive
                                  : ''
                            }`}
                            title={
                              commentReadState === 'unread'
                                ? `${commentCount} unread comment${commentCount === 1 ? '' : 's'}`
                                : commentCount > 0
                                  ? `${commentCount} comment${commentCount === 1 ? '' : 's'}`
                                  : 'Comments'
                            }
                            onClick={() => setCommentTask(task)}
                          >
                            💬
                          </button>
                          <button
                            type="button"
                            className={`${styles.rowIconBtn} ${
                              attachmentCount > 0 ? styles.rowIconActive : ''
                            }`}
                            title={
                              attachmentCount > 0
                                ? `${attachmentCount} attachment${attachmentCount === 1 ? '' : 's'}`
                                : 'Attachments'
                            }
                            onClick={() => setAttachmentTask(task)}
                          >
                            📎
                          </button>
                          <button
                            type="button"
                            className={styles.rowIconBtn}
                            title="Open on project board"
                            onClick={() => openTaskOnBoard(task)}
                          >
                            👁
                          </button>
                        </div>
                      </td>
                      <td style={cellStyle('title')}>
                        <button
                          type="button"
                          className={styles.taskLink}
                          onClick={() => openTaskOnBoard(task)}
                          title="Open on project board"
                        >
                          {task.title}
                          {task.parentTaskId ? (
                            <span className={styles.subBadge}>Sub</span>
                          ) : null}
                        </button>
                      </td>
                      <td className={styles.projectCell} style={cellStyle('project')}>
                        {projectLabel(task.projectId)}
                      </td>
                      {showBoardColumn ? (
                        <td style={cellStyle('board')}>
                          {getBoardLabel(task.boardType as ProjectBoardType)}
                        </td>
                      ) : null}
                      <td className={styles.mono} style={cellStyle('taskNumber')}>
                        {task.taskNumber ?? '—'}
                      </td>
                      <td style={cellStyle('status')}>
                        <span
                          className={styles.statusPill}
                          style={{
                            borderColor: status.color,
                            color: status.color,
                          }}
                        >
                          {status.label}
                        </span>
                      </td>
                      <td className={styles.mono} style={cellStyle('due')}>
                        {taskMyWorkDueDate(task) ?? '—'}
                      </td>
                      <td style={cellStyle('time')}>
                        <div className={styles.clockCell}>
                          {openClock ? (
                            <>
                              <span className={styles.clockHint}>
                                {formatEntryTimeRange(openClock)}
                              </span>
                              <button
                                type="button"
                                className={`${styles.clockBtn} ${styles.clockBtnOut}`}
                                onClick={() => handleClockOut(task.id)}
                              >
                                Clock out
                              </button>
                            </>
                          ) : (
                            <button
                              type="button"
                              className={`${styles.clockBtn} ${styles.clockBtnIn}`}
                              disabled={clockBlockedByOther || status.complete}
                              title={
                                clockBlockedByOther
                                  ? 'Clock out of your other task first'
                                  : status.complete
                                    ? 'Completed tasks cannot be clocked'
                                    : 'Clock in'
                              }
                              onClick={() => handleClockIn(task)}
                            >
                              Clock in
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {attachmentTask ? (
        <AttachmentDialog task={attachmentTask} onClose={() => setAttachmentTask(null)} />
      ) : null}
      {commentTask ? (
        <CommentDialog task={commentTask} onClose={() => setCommentTask(null)} />
      ) : null}
    </div>
  );
}
