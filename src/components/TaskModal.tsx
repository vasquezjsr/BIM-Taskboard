import { useMemo, useState } from 'react';
import { useStore } from '../store/useStore';
import { AssigneeCell } from './AssigneeCell';
import { getAssignableBoards, type ProjectBoardType, type TaskStatus } from '../types';
import { defaultStatusForBoard } from '../utils/taskStatus';
import { findSectionBoardType } from '../utils/sheetDrag';
import { getBoardTaskStatuses } from '../utils/taskStatuses';
import { taskHasAssignee } from '../utils/taskAssignees';
import styles from './TaskModal.module.css';

export interface TaskModalInitialValues {
  clientId?: string;
  projectId?: string;
  groupId?: string | null;
  parentTaskId?: string | null;
}

interface TaskModalProps {
  onClose: () => void;
  initial?: TaskModalInitialValues;
}

export function TaskModal({ onClose, initial }: TaskModalProps) {
  const addTask = useStore((s) => s.addTask);
  const clients = useStore((s) => s.clients);
  const projects = useStore((s) => s.projects);
  const employees = useStore((s) => s.employees);
  const taskGroups = useStore((s) => s.taskGroups);
  const activeClientId = useStore((s) => s.activeClientId);
  const activeProjectId = useStore((s) => s.activeProjectId);
  const customBoards = useStore((s) => s.customBoards);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);

  const defaultBranch = useMemo(
    () => findSectionBoardType(taskGroups, initial?.groupId ?? null) ?? 'main',
    [taskGroups, initial?.groupId]
  );
  const [clientId, setClientId] = useState(initial?.clientId ?? activeClientId ?? '');
  const [projectId, setProjectId] = useState(initial?.projectId ?? activeProjectId ?? '');
  const branchBoards = useMemo(
    () => getAssignableBoards(customBoards, projectId || activeProjectId || ''),
    [customBoards, projectId, activeProjectId]
  );
  const [boardType, setBoardType] = useState<ProjectBoardType>(defaultBranch);
  const branchStatuses = getBoardTaskStatuses(
    boardType,
    boardTaskStatuses,
    projectId || null,
    projectBoardTaskStatuses
  );
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [status, setStatus] = useState<TaskStatus>(
    defaultStatusForBoard(
      defaultBranch,
      getBoardTaskStatuses(defaultBranch, boardTaskStatuses, projectId || null, projectBoardTaskStatuses)
    )
  );
  const [assigneeIds, setAssigneeIds] = useState<string[]>([]);
  const [dueDate, setDueDate] = useState('');
  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
    [clients]
  );
  const sortedClientProjects = useMemo(
    () =>
      [...projects.filter((p) => p.clientId === clientId)].sort((a, b) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
      ),
    [projects, clientId]
  );

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim()) return;

    addTask({
      title: title.trim(),
      description: description.trim(),
      status,
      assigneeIds,
      clientId: clientId || null,
      projectId: projectId || null,
      boardType,
      groupId: initial?.groupId ?? null,
      parentTaskId: initial?.parentTaskId ?? null,
      priority: assigneeIds.length
        ? Math.max(
            ...assigneeIds.map(
              (id) =>
                useStore.getState().tasks.filter((t) => taskHasAssignee(t, id)).length
            ),
            0
          )
        : useStore.getState().tasks.filter(
            (t) =>
              t.projectId === projectId &&
              t.groupId === (initial?.groupId ?? null) &&
              t.parentTaskId === (initial?.parentTaskId ?? null)
          ).length,
      dueDate: dueDate || null,
    });
    onClose();
  };

  return (
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <h2>Create Task</h2>
          <button className={styles.closeBtn} onClick={onClose}>×</button>
        </div>

        <form onSubmit={handleSubmit} className={styles.form}>
          <label>
            Title
            <input
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="Task title"
              autoFocus
            />
          </label>

          <label>
            Description
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Optional details"
              rows={3}
            />
          </label>

          <div className={styles.row}>
            <label>
              Status
              <select value={status} onChange={(e) => setStatus(e.target.value as TaskStatus)}>
                {branchStatuses.map((s) => (
                  <option key={s.id} value={s.id}>{s.label}</option>
                ))}
              </select>
            </label>

            <label>
              Due Date
              <input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} />
            </label>
          </div>

          <div className={styles.row}>
            <label>
              Client
              <select
                value={clientId}
                onChange={(e) => {
                  setClientId(e.target.value);
                  const first = projects.find((p) => p.clientId === e.target.value);
                  setProjectId(first?.id ?? '');
                }}
              >
                <option value="">Select client</option>
                {sortedClients.map((c) => (
                  <option key={c.id} value={c.id}>{c.name}</option>
                ))}
              </select>
            </label>

            <label>
              Project
              <select value={projectId} onChange={(e) => setProjectId(e.target.value)}>
                <option value="">Select project</option>
                {sortedClientProjects.map((p) => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </select>
            </label>
          </div>

          <div className={styles.row}>
            <label>
              Board
              <select
                value={boardType}
                onChange={(e) => {
                  const next = e.target.value as ProjectBoardType;
                  setBoardType(next);
                  if (
                    next === 'detailers' ||
                    next === 'deliverables' ||
                    next === 'project-managers'
                  ) {
                    setStatus(
                      defaultStatusForBoard(
                        next,
                        getBoardTaskStatuses(next, boardTaskStatuses, projectId || null, projectBoardTaskStatuses)
                      )
                    );
                  }
                }}
              >
                {branchBoards.map((b) => (
                  <option key={b.id} value={b.id}>{b.label}</option>
                ))}
              </select>
            </label>

            <label className={styles.assigneeField}>
              Assign To
              <AssigneeCell
                assigneeIds={assigneeIds}
                employees={employees}
                onChange={setAssigneeIds}
              />
            </label>
          </div>

          <div className={styles.actions}>
            <button type="button" className={styles.cancelBtn} onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className={styles.submitBtn} disabled={!title.trim()}>
              Create Task
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
