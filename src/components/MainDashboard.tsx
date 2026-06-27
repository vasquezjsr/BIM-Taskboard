import { useMemo } from 'react';
import { useStore } from '../store/useStore';
import type { ProjectBoardType } from '../types';
import { buildClientDashboardSummaries } from '../utils/projectDashboard';
import styles from './MainDashboard.module.css';

function ProgressBar({ percent, compact = false }: { percent: number; compact?: boolean }) {
  return (
    <div className={`${styles.progressTrack} ${compact ? styles.progressTrackCompact : ''}`}>
      <div className={styles.progressFill} style={{ width: `${percent}%` }} />
      <span className={styles.progressText}>{percent}%</span>
    </div>
  );
}

export function MainDashboard() {
  const clients = useStore((s) => s.clients);
  const projects = useStore((s) => s.projects);
  const tasks = useStore((s) => s.tasks);
  const taskGroups = useStore((s) => s.taskGroups);
  const subBoardTabOrder = useStore((s) => s.subBoardTabOrder);
  const customBoards = useStore((s) => s.customBoards);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const openProjectBoard = useStore((s) => s.openProjectBoard);

  const sections = useMemo(
    () =>
      buildClientDashboardSummaries(
        clients,
        projects,
        tasks,
        taskGroups,
        subBoardTabOrder,
        customBoards,
        boardTaskStatuses,
        projectBoardTaskStatuses
      ),
    [
      clients,
      projects,
      tasks,
      taskGroups,
      subBoardTabOrder,
      customBoards,
      boardTaskStatuses,
      projectBoardTaskStatuses,
    ]
  );

  if (sections.length === 0) {
    return (
      <div className={styles.empty}>
        <h2>Main Dashboard</h2>
        <p>No active projects yet. Add a client and project to get started.</p>
      </div>
    );
  }

  return (
    <div className={styles.wrapper}>
      <div className={styles.header}>
        <h2 className={styles.title}>Main Dashboard</h2>
      </div>

      <div className={styles.sections}>
        {sections.map(({ client, projects: clientProjects }) => (
          <section key={client.id} className={styles.clientSection}>
            <h3 className={styles.clientHeading}>{client.name}</h3>
            <div className={styles.projectGrid}>
              {clientProjects.map((summary) => (
                <article key={summary.project.id} className={styles.projectCard}>
                  <div className={styles.projectHeader}>
                    <div>
                      <button
                        type="button"
                        className={styles.projectTitle}
                        onClick={() =>
                          openProjectBoard(
                            summary.project.clientId,
                            summary.project.id,
                            'main'
                          )
                        }
                      >
                        {summary.project.name}
                      </button>
                      <div className={styles.projectMeta}>
                        <span
                          className={`${styles.billingBadge} ${
                            summary.project.billingType === 'time-and-material'
                              ? styles.billingTm
                              : styles.billingLump
                          }`}
                        >
                          {summary.project.billingType === 'time-and-material'
                            ? 'T&M'
                            : 'Lump Sum'}
                        </span>
                        <span className={styles.metaItem}>{summary.durationLabel}</span>
                        <div className={styles.budgetHoursMeta} aria-label="Budget hours">
                          <span className={styles.budgetHoursLabel}>Budget Hours</span>
                          <span className={styles.budgetHoursValue}>{summary.budgetHoursLabel}</span>
                        </div>
                      </div>
                    </div>
                    <div className={styles.overallProgress}>
                      <span className={styles.overallLabel}>Overall</span>
                      <ProgressBar percent={summary.overall.percent} />
                      <span className={styles.taskCount}>
                        {summary.overall.completed}/{summary.overall.total} tasks
                      </span>
                    </div>
                  </div>

                  <div className={styles.boardTable}>
                    <div className={styles.boardHeaderRow}>
                      <span>Board</span>
                      <span>Progress</span>
                      <span>Tasks</span>
                    </div>
                    {summary.boards.map((board) => (
                      <button
                        key={board.boardType}
                        type="button"
                        className={styles.boardRow}
                        onClick={() =>
                          openProjectBoard(
                            summary.project.clientId,
                            summary.project.id,
                            board.boardType as ProjectBoardType
                          )
                        }
                      >
                        <span className={styles.boardName}>{board.label}</span>
                        <ProgressBar percent={board.percent} compact />
                        <span className={styles.boardTasks}>
                          {board.completed}/{board.total}
                        </span>
                      </button>
                    ))}
                  </div>
                </article>
              ))}
            </div>
          </section>
        ))}
      </div>
    </div>
  );
}
