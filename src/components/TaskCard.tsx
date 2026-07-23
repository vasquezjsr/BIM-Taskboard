import { useMemo, useRef, type CSSProperties, type ReactNode } from 'react';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useStore } from '../store/useStore';
import { employeeAssigneeStyle, employeeInitials } from '../data/employees';
import { getBoardTaskStatuses, getStatusColor, statusBoardForTask } from '../utils/taskStatuses';
import { getContrastingTextColor } from '../utils/colorContrast';
import { taskMyWorkDueDate, workflowDueColumnIdForTask } from '../utils/myWorkTasks';
import { canLogTime } from '../utils/permissions';
import {
  isOpenTimeEntry,
  localNowTimeString,
  localTodayIsoDate,
  prepareCompletedClockTimes,
} from '../utils/timeEntry';
import { getBoardLabel, type ProjectBoardType, type Task } from '../types';
import styles from './TaskCard.module.css';

interface TaskCardProps {
  task: Task;
  statusColor?: string;
  /** Drag overlay preview — must not register a sortable id. */
  isDragging?: boolean;
  hideAssignee?: boolean;
  hideDescription?: boolean;
  /** When false, card is display-only (no drag). */
  draggable?: boolean;
}

export function TaskCard({
  task,
  statusColor,
  isDragging = false,
  hideAssignee = false,
  hideDescription = false,
  draggable = true,
}: TaskCardProps) {
  const employees = useStore((s) => s.employees);
  const employeeAssigneeStyles = useStore((s) => s.employeeAssigneeStyles);
  const clients = useStore((s) => s.clients);
  const projects = useStore((s) => s.projects);
  const taskGroups = useStore((s) => s.taskGroups);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const timeEntries = useStore((s) => s.timeEntries);
  const currentUserId = useStore((s) => s.currentUserId);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const moveTask = useStore((s) => s.moveTask);
  const removeTask = useStore((s) => s.removeTask);
  const addTimeEntry = useStore((s) => s.addTimeEntry);
  const updateTimeEntry = useStore((s) => s.updateTimeEntry);

  const dateInputRef = useRef<HTMLInputElement>(null);
  const enableSortable = draggable && !isDragging;
  const allowClock = canLogTime(currentUserId, employees, employeePermissions);

  const statuses = getBoardTaskStatuses(
    statusBoardForTask(task, taskGroups),
    boardTaskStatuses,
    task.projectId,
    projectBoardTaskStatuses
  );
  const barColor = statusColor ?? getStatusColor(task.status, statuses);
  const statusTextColor = getContrastingTextColor(barColor);

  // Overlay must use a distinct id so it never collides with the live card's sortable.
  const { attributes, listeners, setNodeRef, transform, transition, isDragging: isSortDragging } =
    useSortable({
      id: isDragging ? `__overlay__${task.id}` : task.id,
      disabled: !enableSortable,
    });

  const assignees = employees.filter((e) => task.assigneeIds.includes(e.id));
  const client = clients.find((c) => c.id === task.clientId);
  const project = projects.find((p) => p.id === task.projectId);

  const openClockOnTask = useMemo(() => {
    if (!currentUserId) return null;
    return (
      timeEntries.find(
        (entry) =>
          entry.employeeId === currentUserId &&
          entry.taskId === task.id &&
          isOpenTimeEntry(entry)
      ) ?? null
    );
  }, [timeEntries, currentUserId, task.id]);

  const anyOpenClock = useMemo(() => {
    if (!currentUserId) return null;
    return (
      timeEntries.find(
        (entry) => entry.employeeId === currentUserId && isOpenTimeEntry(entry)
      ) ?? null
    );
  }, [timeEntries, currentUserId]);

  const clockBlockedByOther = Boolean(anyOpenClock) && anyOpenClock?.taskId !== task.id;

  const dueDate = taskMyWorkDueDate(task);
  const dueColumnId = workflowDueColumnIdForTask(task);
  const dueLabel = dueDate
    ? new Date(dueDate + 'T00:00:00').toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric',
      })
    : 'Due date';

  const handleDueDateChange = (value: string) => {
    const next = value || null;
    if (dueColumnId) {
      moveTask(task.id, { customFields: { [dueColumnId]: next } });
      return;
    }
    moveTask(task.id, { dueDate: next });
  };

  const handleClockIn = () => {
    if (!currentUserId || !allowClock || anyOpenClock) return;
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
      note: `Task Board · ${getBoardLabel(task.boardType as ProjectBoardType)} · ${task.title}`,
    });
  };

  const handleClockOut = () => {
    if (!openClockOnTask) return;
    const completed = prepareCompletedClockTimes(
      openClockOnTask.startTime,
      localNowTimeString()
    );
    if (!completed) return;
    updateTimeEntry(openClockOnTask.id, {
      employeeId: openClockOnTask.employeeId,
      clientId: openClockOnTask.clientId,
      projectId: openClockOnTask.projectId,
      taskId: openClockOnTask.taskId,
      date: openClockOnTask.date,
      startTime: completed.startTime,
      endTime: completed.endTime,
      hours: completed.hours,
      note: openClockOnTask.note,
    });
  };

  const style: CSSProperties = isDragging
    ? {}
    : {
        transform: CSS.Transform.toString(transform),
        transition,
        opacity: isSortDragging ? 0.4 : 1,
      };

  const body: ReactNode = (
    <>
      <div className={styles.statusBar} style={{ background: barColor }} />
      <div className={styles.content}>
        <div className={styles.titleRow}>
          <h4 className={styles.title}>{task.title}</h4>
          {!isDragging && (
            <button
              className={styles.deleteBtn}
              onPointerDown={(e) => e.stopPropagation()}
              onClick={() => removeTask(task.id)}
              title="Delete task"
            >
              ×
            </button>
          )}
        </div>
        {task.description && !hideDescription && (
          <p className={styles.description}>{task.description}</p>
        )}
        {((assignees.length > 0 && !hideAssignee) || (client && project)) && (
          <div className={styles.meta}>
            {assignees.length > 0 && !hideAssignee && (
              <span className={`${styles.badge} ${styles.assignee}`}>
                {assignees.map((emp) => {
                  const badgeStyle = employeeAssigneeStyle(emp.id, employeeAssigneeStyles);
                  return (
                    <span
                      key={emp.id}
                      className={styles.assigneeInitial}
                      style={{
                        borderColor: badgeStyle.border,
                        background: badgeStyle.background,
                        color: badgeStyle.text,
                      }}
                      title={emp.name}
                    >
                      {employeeInitials(emp.name)}
                    </span>
                  );
                })}
              </span>
            )}
            {client && project && (
              <span className={styles.badge}>
                {client.name} / {project.name}
              </span>
            )}
          </div>
        )}
        <div className={styles.controls}>
          <label className={styles.dueField}>
            <button
              type="button"
              className={`${styles.dueButton} ${!dueDate ? styles.dueButtonEmpty : ''}`}
              onPointerDown={(e) => e.stopPropagation()}
              onClick={(e) => {
                e.stopPropagation();
                if (isDragging) return;
                dateInputRef.current?.showPicker?.();
              }}
              title="Change due date"
            >
              {dueLabel}
            </button>
            {!isDragging && (
              <input
                ref={dateInputRef}
                type="date"
                className={styles.dueInputHidden}
                value={dueDate ?? ''}
                onPointerDown={(e) => e.stopPropagation()}
                onClick={(e) => e.stopPropagation()}
                onChange={(e) => handleDueDateChange(e.target.value)}
                tabIndex={-1}
                aria-label="Due date"
              />
            )}
          </label>
          <select
            className={styles.statusSelect}
            value={task.status}
            style={{
              backgroundColor: barColor,
              color: statusTextColor,
              borderColor: barColor,
            }}
            disabled={isDragging}
            onPointerDown={(e) => e.stopPropagation()}
            onClick={(e) => e.stopPropagation()}
            onChange={(e) => moveTask(task.id, { status: e.target.value })}
            title="Change status"
          >
            {statuses.map((status) => (
              <option key={status.id} value={status.id}>
                {status.label}
              </option>
            ))}
          </select>
        </div>
        {!isDragging && allowClock && (
          <button
            type="button"
            className={`${styles.clockBtn} ${openClockOnTask ? styles.clockBtnOut : styles.clockBtnIn}`}
            onPointerDown={(e) => e.stopPropagation()}
            onClick={(e) => {
              e.stopPropagation();
              if (openClockOnTask) handleClockOut();
              else handleClockIn();
            }}
            disabled={!openClockOnTask && clockBlockedByOther}
            title={
              openClockOnTask
                ? 'Clock out of this task'
                : clockBlockedByOther
                  ? 'Clock out of your other task first'
                  : 'Clock in to this task'
            }
          >
            {openClockOnTask ? 'Clock Out' : 'Clock In'}
          </button>
        )}
      </div>
    </>
  );

  // Overlay preview — plain node, no sortable registration (duplicate ids break DnD).
  if (isDragging) {
    return <div className={`${styles.card} ${styles.dragOverlay}`}>{body}</div>;
  }

  return (
    <div
      ref={setNodeRef}
      style={style}
      className={`${styles.card} ${openClockOnTask ? styles.cardClockedIn : ''}`}
      {...(enableSortable ? attributes : {})}
      {...(enableSortable ? listeners : {})}
    >
      {body}
    </div>
  );
}
