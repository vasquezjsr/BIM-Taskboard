import { useMemo } from 'react';
import { useStore } from '../store/useStore';
import type { DashboardType } from '../types';
import { DASHBOARD_META, boardTypeForDashboard } from '../data/dashboards';
import { BIM_WORKFLOW_STAGES, workflowStageForBoard } from '../data/bimWorkflow';
import { getBoardLabel } from '../types';
import { getBoardTaskStatuses } from '../utils/taskStatuses';
import styles from './DepartmentDashboardView.module.css';

interface DepartmentDashboardViewProps {
  dashboard: DashboardType;
}

export function DepartmentDashboardView({ dashboard }: DepartmentDashboardViewProps) {
  const tasks = useStore((s) => s.tasks);
  const projects = useStore((s) => s.projects);
  const clients = useStore((s) => s.clients);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);

  const meta = DASHBOARD_META[dashboard];
  const boardType = boardTypeForDashboard(dashboard);
  const workflowStage = workflowStageForBoard(boardType);
  const stageInfo = BIM_WORKFLOW_STAGES.find((stage) => stage.id === workflowStage);

  const openTasks = useMemo(
    () =>
      tasks
        .filter((task) => task.boardType === boardType && task.projectId)
        .sort((a, b) => (a.taskNumber ?? '').localeCompare(b.taskNumber ?? '')),
    [tasks, boardType]
  );

  const projectLabel = (projectId: string | null) => {
    if (!projectId) return '—';
    const project = projects.find((entry) => entry.id === projectId);
    if (!project) return '—';
    const client = clients.find((entry) => entry.id === project.clientId);
    const code = project.jobCode ? `${project.jobCode} · ` : '';
    return `${code}${client?.name ?? 'Client'} / ${project.name}`;
  };

  const statusLabel = (task: (typeof tasks)[number]) => {
    const statuses = getBoardTaskStatuses(
      boardType,
      boardTaskStatuses,
      task.projectId,
      projectBoardTaskStatuses
    );
    return statuses.find((status) => status.id === task.status)?.label ?? task.status;
  };

  return (
    <div className={styles.wrapper}>
      <header className={styles.header}>
        <h2 className={styles.title}>{meta.label}</h2>
        <p className={styles.subtitle}>
          {stageInfo
            ? `${stageInfo.label} — ${stageInfo.description} Assign team members from the Employees dashboard; tasks on the ${getBoardLabel(boardType)} board appear below.`
            : `Tasks on the ${getBoardLabel(boardType)} board. Assign team members from the Employees dashboard.`}
        </p>
      </header>

      <section className={styles.tasksSection}>
        <h3 className={styles.tasksHeading}>{getBoardLabel(boardType)} tasks</h3>
        {openTasks.length === 0 ? (
          <p className={styles.emptyTasks}>
            No tasks on this board yet. Add them from a project&apos;s {getBoardLabel(boardType)} tab.
          </p>
        ) : (
          <table className={styles.tasksTable}>
            <thead>
              <tr>
                <th>Task #</th>
                <th>Title</th>
                <th>Project</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {openTasks.map((task) => (
                <tr key={task.id}>
                  <td className={styles.taskNumberCell}>{task.taskNumber ?? '—'}</td>
                  <td>{task.title}</td>
                  <td>{projectLabel(task.projectId)}</td>
                  <td>{statusLabel(task)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
