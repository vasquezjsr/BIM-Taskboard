import { useRef } from 'react';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useStore } from '../store/useStore';
import { employeeAssigneeStyle, employeeInitials } from '../data/employees';
import { getBoardTaskStatuses, getStatusColor, statusBoardForTask } from '../utils/taskStatuses';
import { getContrastingTextColor } from '../utils/colorContrast';
import type { Task } from '../types';
import styles from './TaskCard.module.css';

interface TaskCardProps {
  task: Task;
  statusColor?: string;
  isDragging?: boolean;
  hideAssignee?: boolean;
  hideDescription?: boolean;
}

export function TaskCard({
  task,
  statusColor,
  isDragging = false,
  hideAssignee = false,
  hideDescription = false,
}: TaskCardProps) {
  const employees = useStore((s) => s.employees);
  const employeeAssigneeStyles = useStore((s) => s.employeeAssigneeStyles);
  const clients = useStore((s) => s.clients);
  const projects = useStore((s) => s.projects);
  const taskGroups = useStore((s) => s.taskGroups);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const moveTask = useStore((s) => s.moveTask);
  const removeTask = useStore((s) => s.removeTask);

  const dateInputRef = useRef<HTMLInputElement>(null);

  const statuses = getBoardTaskStatuses(
    statusBoardForTask(task, taskGroups),
    boardTaskStatuses,
    task.projectId,
    projectBoardTaskStatuses
  );
  const barColor = statusColor ?? getStatusColor(task.status, statuses);
  const statusTextColor = getContrastingTextColor(barColor);

  const { attributes, listeners, setNodeRef, transform, transition, isDragging: isSortDragging } =
    useSortable({ id: task.id });

  const assignees = employees.filter((e) => task.assigneeIds.includes(e.id));
  const client = clients.find((c) => c.id === task.clientId);
  const project = projects.find((p) => p.id === task.projectId);

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isSortDragging ? 0.4 : 1,
  };

  const dueLabel = task.dueDate
    ? new Date(task.dueDate + 'T00:00:00').toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric',
      })
    : 'Due date';

  return (
    <div
      ref={setNodeRef}
      style={style}
      className={`${styles.card} ${isDragging ? styles.dragOverlay : ''}`}
      {...attributes}
      {...listeners}
    >
      <div className={styles.statusBar} style={{ background: barColor }} />
      <div className={styles.content}>
        <div className={styles.titleRow}>
          <h4 className={styles.title}>{task.title}</h4>
          <button
            className={styles.deleteBtn}
            onPointerDown={(e) => e.stopPropagation()}
            onClick={() => removeTask(task.id)}
            title="Delete task"
          >
            ×
          </button>
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
              className={`${styles.dueButton} ${!task.dueDate ? styles.dueButtonEmpty : ''}`}
              onPointerDown={(e) => e.stopPropagation()}
              onClick={(e) => {
                e.stopPropagation();
                dateInputRef.current?.showPicker?.();
              }}
              title="Change due date"
            >
              {dueLabel}
            </button>
            <input
              ref={dateInputRef}
              type="date"
              className={styles.dueInputHidden}
              value={task.dueDate ?? ''}
              onPointerDown={(e) => e.stopPropagation()}
              onClick={(e) => e.stopPropagation()}
              onChange={(e) => moveTask(task.id, { dueDate: e.target.value || null })}
              tabIndex={-1}
              aria-label="Due date"
            />
          </label>
          <select
            className={styles.statusSelect}
            value={task.status}
            style={{
              backgroundColor: barColor,
              color: statusTextColor,
              borderColor: barColor,
            }}
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
      </div>
    </div>
  );
}
