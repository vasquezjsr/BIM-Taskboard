import { useMemo, useState } from 'react';
import { useStore } from '../store/useStore';
import { BIM_WORKFLOW_STAGES, computeWorkflowStageProgress, overallWorkflowPercent } from '../data/bimWorkflow';
import { PM_STAFF_IDS } from '../data/departmentStaff';
import { DASHBOARD_META } from '../data/dashboards';
import { formatBudgetHoursDisplay, formatProjectDuration } from '../utils/projectDashboard';
import { canEditPmAssigns, canManageOrg, canViewDashboard } from '../utils/permissions';
import { getBoardTaskStatuses } from '../utils/taskStatuses';
import { isTemplateProject } from '../utils/projectTemplate';
import { ProjectEmployeeAssignModal } from './ProjectEmployeeAssignModal';
import styles from './ExecutiveDashboard.module.css';
import deptStyles from './DepartmentDashboardView.module.css';

function namesForIds(
  ids: string[],
  employees: { id: string; name: string }[]
): string {
  return ids
    .map((id) => employees.find((entry) => entry.id === id)?.name)
    .filter(Boolean)
    .join(', ');
}

export function PMDashboardView() {
  const employees = useStore((s) => s.employees);
  const projects = useStore((s) => s.projects);
  const clients = useStore((s) => s.clients);
  const tasks = useStore((s) => s.tasks);
  const taskGroups = useStore((s) => s.taskGroups);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const openProjectBoard = useStore((s) => s.openProjectBoard);
  const openFieldJobDashboard = useStore((s) => s.openFieldJobDashboard);
  const currentUserId = useStore((s) => s.currentUserId);
  const employeePermissions = useStore((s) => s.employeePermissions);

  const [pickerProjectId, setPickerProjectId] = useState<string | null>(null);

  const canManage = canManageOrg(currentUserId, employees, employeePermissions);
  const canAssignEmployees = canEditPmAssigns(currentUserId, employees, employeePermissions);
  const isPmStaff = currentUserId
    ? (PM_STAFF_IDS as readonly string[]).includes(currentUserId)
    : false;
  const canOpenField = canViewDashboard(
    'field',
    currentUserId,
    employees,
    employeePermissions
  );

  const visibleProjects = useMemo(() => {
    return projects.filter((project) => {
      if (isTemplateProject(project)) return false;
      if (canManage) return true;
      if (!currentUserId) return false;
      if (project.pmIds.includes(currentUserId)) return true;
      if ((project.assistantPmIds ?? []).includes(currentUserId)) return true;
      if (isPmStaff) return true;
      return false;
    });
  }, [projects, canManage, currentUserId, isPmStaff]);

  const pickerProject = pickerProjectId
    ? projects.find((project) => project.id === pickerProjectId) ?? null
    : null;

  const pmBoardTasks = useMemo(
    () =>
      tasks
        .filter(
          (task) =>
            task.boardType === 'project-managers' &&
            task.projectId &&
            visibleProjects.some((project) => project.id === task.projectId)
        )
        .sort((a, b) => (a.taskNumber ?? '').localeCompare(b.taskNumber ?? '')),
    [tasks, visibleProjects]
  );

  const projectLabel = (projectId: string | null) => {
    if (!projectId) return '—';
    const project = projects.find((entry) => entry.id === projectId);
    if (!project) return '—';
    const client = clients.find((entry) => entry.id === project.clientId);
    const code = project.jobCode ? `${project.jobCode} · ` : '';
    return `${code}${client?.name ?? 'Client'} / ${project.name}`;
  };

  return (
    <div className={styles.wrapper}>
      <header className={styles.header}>
        <h2 className={styles.title}>{DASHBOARD_META.pm.label}</h2>
        <p className={styles.subtitle}>
          Projects assigned to you with contract milestones, budget, and lifecycle progress from
          Project Setup through Field. Assign PMs, Assistant PMs, and Field staff to each job.
        </p>
      </header>

      <div className={styles.projectGrid}>
        {visibleProjects.length === 0 ? (
          <p className={styles.empty}>No projects assigned yet.</p>
        ) : (
          visibleProjects.map((project) => {
            const client = clients.find((entry) => entry.id === project.clientId);
            const stages = computeWorkflowStageProgress(
              project.id,
              tasks,
              taskGroups,
              boardTaskStatuses,
              projectBoardTaskStatuses
            );
            const overall = overallWorkflowPercent(stages);
            const pmNames = namesForIds(project.pmIds ?? [], employees);
            const assistantNames = namesForIds(project.assistantPmIds ?? [], employees);
            const fieldLeadNames = namesForIds(project.fieldIds ?? [], employees);
            const fieldCrewNames = namesForIds(project.fieldCrewIds ?? [], employees);

            return (
              <article key={project.id} className={styles.projectCard}>
                <div className={styles.projectCardHeader}>
                  <div>
                    <h3>{project.name}</h3>
                    <p className={styles.clientName}>{client?.name ?? 'Client'}</p>
                  </div>
                  <div className={styles.cardActions}>
                    <button
                      type="button"
                      className={styles.openBtn}
                      onClick={() =>
                        openProjectBoard(project.clientId, project.id, 'project-managers')
                      }
                    >
                      Open PM Board
                    </button>
                    {canOpenField ? (
                      <button
                        type="button"
                        className={styles.openBtnSecondary}
                        onClick={() => openFieldJobDashboard(project.id)}
                        title="Open this job on the Field Dashboard for crew visibility"
                      >
                        Open job Field view
                      </button>
                    ) : null}
                  </div>
                </div>

                <dl className={styles.metaGrid}>
                  <div>
                    <dt>Contract / budget</dt>
                    <dd>{formatBudgetHoursDisplay(project)}</dd>
                  </div>
                  <div>
                    <dt>Schedule</dt>
                    <dd>
                      {formatProjectDuration(
                        project,
                        tasks.filter((task) => task.projectId === project.id),
                        taskGroups.filter((group) => group.projectId === project.id)
                      )}
                    </dd>
                  </div>
                  <div>
                    <dt>Overall progress</dt>
                    <dd>{overall}%</dd>
                  </div>
                </dl>

                <dl className={styles.teamGrid}>
                  <div>
                    <dt>Project Manager</dt>
                    <dd>{pmNames || '—'}</dd>
                  </div>
                  <div>
                    <dt>Assistant PM</dt>
                    <dd>{assistantNames || '—'}</dd>
                  </div>
                  <div>
                    <dt>Field Super / lead</dt>
                    <dd>{fieldLeadNames || '—'}</dd>
                  </div>
                  <div>
                    <dt>Field crew</dt>
                    <dd>{fieldCrewNames || '—'}</dd>
                  </div>
                </dl>

                <div className={styles.progressBarTrack}>
                  <div className={styles.progressBarFill} style={{ width: `${overall}%` }} />
                </div>

                <div className={styles.stageGrid}>
                  {stages.map(({ stage, percent, total }) => (
                    <div key={stage.id} className={styles.stageChip} title={stage.description}>
                      <span className={styles.stageLabel}>{stage.label}</span>
                      <span className={styles.stagePercent}>
                        {total === 0 ? '—' : `${percent}%`}
                      </span>
                    </div>
                  ))}
                </div>

                {canAssignEmployees ? (
                  <button
                    type="button"
                    className={styles.openBtn}
                    onClick={() => setPickerProjectId(project.id)}
                  >
                    Assign Employees
                  </button>
                ) : null}
              </article>
            );
          })
        )}
      </div>

      <section className={deptStyles.tasksSection}>
        <h3 className={deptStyles.tasksHeading}>PM board tasks</h3>
        {pmBoardTasks.length === 0 ? (
          <p className={deptStyles.emptyTasks}>
            No tasks on this board yet. Add them from a project&apos;s PM tab.
          </p>
        ) : (
          <table className={deptStyles.tasksTable}>
            <thead>
              <tr>
                <th>Task #</th>
                <th>Title</th>
                <th>Project</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {pmBoardTasks.map((task) => {
                const statuses = getBoardTaskStatuses(
                  'project-managers',
                  boardTaskStatuses,
                  task.projectId,
                  projectBoardTaskStatuses
                );
                const label =
                  statuses.find((status) => status.id === task.status)?.label ?? task.status;
                return (
                  <tr key={task.id}>
                    <td className={deptStyles.taskNumberCell}>{task.taskNumber ?? '—'}</td>
                    <td>{task.title}</td>
                    <td>{projectLabel(task.projectId)}</td>
                    <td>{label}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </section>

      <section className={styles.workflowLegend}>
        <h3>BIM lifecycle stages</h3>
        <ul>
          {BIM_WORKFLOW_STAGES.map((stage) => (
            <li key={stage.id}>
              <strong>{stage.label}</strong> — {stage.description}
            </li>
          ))}
        </ul>
      </section>

      {pickerProject && canAssignEmployees ? (
        <ProjectEmployeeAssignModal
          project={pickerProject}
          onClose={() => setPickerProjectId(null)}
        />
      ) : null}
    </div>
  );
}
