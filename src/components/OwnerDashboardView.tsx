import { useMemo } from 'react';
import { useStore } from '../store/useStore';
import {
  BIM_WORKFLOW_STAGES,
  computeWorkflowStageProgress,
  overallWorkflowPercent,
} from '../data/bimWorkflow';
import { formatBudgetHoursDisplay } from '../utils/projectDashboard';
import { isTemplateProject } from '../utils/projectTemplate';
import styles from './ExecutiveDashboard.module.css';

interface PortfolioMetrics {
  activeProjects: number;
  avgProgress: number;
  totalBudget: number;
  totalSpent: number;
  atRisk: number;
}

function computeMetrics(
  projects: ReturnType<typeof useStore.getState>['projects'],
  tasks: ReturnType<typeof useStore.getState>['tasks'],
  taskGroups: ReturnType<typeof useStore.getState>['taskGroups'],
  boardTaskStatuses: ReturnType<typeof useStore.getState>['boardTaskStatuses'],
  projectBoardTaskStatuses: ReturnType<typeof useStore.getState>['projectBoardTaskStatuses']
): PortfolioMetrics {
  const active = projects.filter((project) => !isTemplateProject(project));
  let progressSum = 0;
  let atRisk = 0;
  let totalBudget = 0;
  let totalSpent = 0;

  for (const project of active) {
    const stages = computeWorkflowStageProgress(
      project.id,
      tasks,
      taskGroups,
      boardTaskStatuses,
      projectBoardTaskStatuses
    );
    const overall = overallWorkflowPercent(stages);
    progressSum += overall;
    const spent = project.totalHoursSpent ?? 0;
    const budget = project.budgetHours;
    totalSpent += spent;
    if (budget != null) totalBudget += budget;
    const overBudget = budget != null && spent > budget;
    const stalled = overall < 25 && tasks.some((task) => task.projectId === project.id);
    if (overBudget || stalled) atRisk += 1;
  }

  return {
    activeProjects: active.length,
    avgProgress: active.length ? Math.round(progressSum / active.length) : 0,
    totalBudget,
    totalSpent,
    atRisk,
  };
}

export function OwnerDashboardView() {
  const projects = useStore((s) => s.projects);
  const clients = useStore((s) => s.clients);
  const tasks = useStore((s) => s.tasks);
  const taskGroups = useStore((s) => s.taskGroups);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const employees = useStore((s) => s.employees);
  const openProjectBoard = useStore((s) => s.openProjectBoard);

  const activeProjects = useMemo(
    () => projects.filter((project) => !isTemplateProject(project)),
    [projects]
  );

  const metrics = useMemo(
    () =>
      computeMetrics(projects, tasks, taskGroups, boardTaskStatuses, projectBoardTaskStatuses),
    [projects, tasks, taskGroups, boardTaskStatuses, projectBoardTaskStatuses]
  );

  const stageRollup = useMemo(() => {
    return BIM_WORKFLOW_STAGES.map((stage) => {
      let completed = 0;
      let total = 0;
      for (const project of activeProjects) {
        const progress = computeWorkflowStageProgress(
          project.id,
          tasks,
          taskGroups,
          boardTaskStatuses,
          projectBoardTaskStatuses
        ).find((entry) => entry.stage.id === stage.id);
        if (progress) {
          completed += progress.completed;
          total += progress.total;
        }
      }
      return {
        stage,
        percent: total === 0 ? 0 : Math.round((completed / total) * 100),
        total,
      };
    });
  }, [activeProjects, tasks, taskGroups, boardTaskStatuses, projectBoardTaskStatuses]);

  return (
    <div className={styles.wrapper}>
      <header className={styles.header}>
        <h2 className={styles.title}>Owner Dashboard</h2>
        <p className={styles.subtitle}>
          Portfolio health across every active job — financials, lifecycle progress, and risk flags
          from Project Setup through Field Installation.
        </p>
      </header>

      <div className={styles.metricsRow}>
        <div className={styles.metricCard}>
          <span className={styles.metricLabel}>Active projects</span>
          <strong className={styles.metricValue}>{metrics.activeProjects}</strong>
        </div>
        <div className={styles.metricCard}>
          <span className={styles.metricLabel}>Avg. lifecycle progress</span>
          <strong className={styles.metricValue}>{metrics.avgProgress}%</strong>
        </div>
        <div className={styles.metricCard}>
          <span className={styles.metricLabel}>Hours spent / budget</span>
          <strong className={styles.metricValue}>
            {metrics.totalSpent.toLocaleString()} /{' '}
            {metrics.totalBudget ? metrics.totalBudget.toLocaleString() : '—'}
          </strong>
        </div>
        <div className={styles.metricCard}>
          <span className={styles.metricLabel}>Projects at risk</span>
          <strong className={`${styles.metricValue} ${metrics.atRisk > 0 ? styles.risk : ''}`}>
            {metrics.atRisk}
          </strong>
        </div>
      </div>

      <section className={styles.stageRollup}>
        <h3>Workflow stage rollup</h3>
        <div className={styles.stageGrid}>
          {stageRollup.map(({ stage, percent, total }) => (
            <div key={stage.id} className={styles.stageChip} title={stage.description}>
              <span className={styles.stageLabel}>{stage.label}</span>
              <span className={styles.stagePercent}>{total === 0 ? 'No tasks' : `${percent}%`}</span>
            </div>
          ))}
        </div>
      </section>

      <section className={styles.projectGrid}>
        <h3>All projects</h3>
        {activeProjects.length === 0 ? (
          <p className={styles.empty}>No active projects.</p>
        ) : (
          activeProjects.map((project) => {
            const client = clients.find((entry) => entry.id === project.clientId);
            const stages = computeWorkflowStageProgress(
              project.id,
              tasks,
              taskGroups,
              boardTaskStatuses,
              projectBoardTaskStatuses
            );
            const overall = overallWorkflowPercent(stages);
            const pmNames = project.pmIds
              .map((id) => employees.find((entry) => entry.id === id)?.name)
              .filter(Boolean)
              .join(', ');
            const spent = project.totalHoursSpent ?? 0;
            const budget = project.budgetHours;
            const overBudget = budget != null && spent > budget;

            return (
              <article key={project.id} className={styles.projectCard}>
                <div className={styles.projectCardHeader}>
                  <div>
                    <h3>{project.name}</h3>
                    <p className={styles.clientName}>{client?.name ?? 'Client'}</p>
                  </div>
                  <button
                    type="button"
                    className={styles.openBtn}
                    onClick={() => openProjectBoard(project.clientId, project.id, 'main')}
                  >
                    Open project
                  </button>
                </div>
                <dl className={styles.metaGrid}>
                  <div>
                    <dt>PMs</dt>
                    <dd>{pmNames || 'Unassigned'}</dd>
                  </div>
                  <div>
                    <dt>Budget</dt>
                    <dd className={overBudget ? styles.risk : undefined}>
                      {formatBudgetHoursDisplay(project)}
                    </dd>
                  </div>
                  <div>
                    <dt>Progress</dt>
                    <dd>{overall}%</dd>
                  </div>
                </dl>
                <div className={styles.progressBarTrack}>
                  <div className={styles.progressBarFill} style={{ width: `${overall}%` }} />
                </div>
              </article>
            );
          })
        )}
      </section>
    </div>
  );
}
