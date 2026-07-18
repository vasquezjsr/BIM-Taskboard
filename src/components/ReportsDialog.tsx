import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { useStore } from '../store/useStore';
import type { MainTab } from '../types';
import { todayIsoDate } from '../utils/timeCalendar';
import {
  REPORT_CATEGORIES,
  REPORT_DEFINITIONS,
  type ReportDefinition,
  type ReportId,
  reportsForTab,
  selectionNeedsProjects,
} from '../utils/reports/definitions';
import {
  buildReportContextFromStore,
  downloadReportFiles,
  generateReports,
} from '../utils/reports/generateReports';
import { buildReportProjectGroups } from '../utils/reports/reportData';
import { downloadBoardroomProjectsCsv } from '../utils/exportBoardroomProjectsCsv';
import styles from './ReportsDialog.module.css';

interface ReportsDialogProps {
  activeTab: MainTab;
  onClose: () => void;
}

export function ReportsDialog({ activeTab, onClose }: ReportsDialogProps) {
  const clients = useStore((s) => s.clients);
  const projects = useStore((s) => s.projects);
  const activeProjectId = useStore((s) => s.activeProjectId);

  const [scope, setScope] = useState<'tab' | 'all'>('tab');
  const [selected, setSelected] = useState<Set<ReportId>>(new Set());
  const [selectedProjectIds, setSelectedProjectIds] = useState<Set<string>>(() => {
    const initial = new Set<string>();
    if (activeProjectId) {
      const project = projects.find((entry) => entry.id === activeProjectId && !entry.isTemplate);
      if (project) initial.add(activeProjectId);
    }
    return initial;
  });
  const [periodDate, setPeriodDate] = useState(todayIsoDate());
  const [generating, setGenerating] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const visibleReports = useMemo(
    () => (scope === 'tab' ? reportsForTab(activeTab) : REPORT_DEFINITIONS),
    [activeTab, scope]
  );

  const reportsByCategory = useMemo(() => {
    const map = new Map<string, ReportDefinition[]>();
    for (const category of REPORT_CATEGORIES) {
      const items = visibleReports.filter((report) => report.category === category);
      if (items.length > 0) map.set(category, items);
    }
    return map;
  }, [visibleReports]);

  const needsPeriod = useMemo(
    () =>
      [...selected].some((id) => {
        const report = REPORT_DEFINITIONS.find((item) => item.id === id);
        return report && report.periodKind !== 'none';
      }),
    [selected]
  );

  const needsProjects = useMemo(() => selectionNeedsProjects([...selected]), [selected]);

  const projectGroups = useMemo(
    () => buildReportProjectGroups(clients, projects),
    [clients, projects]
  );

  const totalSelectableProjects = useMemo(
    () => projectGroups.reduce((count, group) => count + group.projects.length, 0),
    [projectGroups]
  );

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  useEffect(() => {
    setSelected(new Set());
  }, [scope, activeTab]);

  const toggleReport = (id: ReportId) => {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
    setMessage(null);
  };

  const toggleCategory = (category: string, checked: boolean) => {
    const items = reportsByCategory.get(category) ?? [];
    setSelected((current) => {
      const next = new Set(current);
      for (const item of items) {
        if (checked) next.add(item.id);
        else next.delete(item.id);
      }
      return next;
    });
    setMessage(null);
  };

  const toggleProject = (projectId: string) => {
    setSelectedProjectIds((current) => {
      const next = new Set(current);
      if (next.has(projectId)) next.delete(projectId);
      else next.add(projectId);
      return next;
    });
    setMessage(null);
  };

  const toggleClientProjects = (clientId: string, checked: boolean) => {
    const clientProjectIds =
      projectGroups.find((group) => group.clientId === clientId)?.projects.map((project) => project.id) ??
      [];
    setSelectedProjectIds((current) => {
      const next = new Set(current);
      for (const projectId of clientProjectIds) {
        if (checked) next.add(projectId);
        else next.delete(projectId);
      }
      return next;
    });
    setMessage(null);
  };

  const selectAllProjects = () => {
    setSelectedProjectIds(
      new Set(projectGroups.flatMap((group) => group.projects.map((project) => project.id)))
    );
    setMessage(null);
  };

  const clearProjects = () => {
    setSelectedProjectIds(new Set());
    setMessage(null);
  };

  const handleGenerate = async () => {
    if (selected.size === 0) {
      setMessage('Select at least one report.');
      return;
    }

    if (needsProjects && selectedProjectIds.size === 0) {
      setMessage('Select at least one project.');
      return;
    }

    setGenerating(true);
    setMessage(null);

    try {
      const state = useStore.getState();
      const ctx = buildReportContextFromStore({
        clients: state.clients,
        projects: state.projects,
        employees: state.employees,
        tasks: state.tasks,
        taskGroups: state.taskGroups,
        timeEntries: state.timeEntries,
        orgTeams: state.orgTeams,
        employeeReportsTo: state.employeeReportsTo,
        boardTaskStatuses: state.boardTaskStatuses,
        projectBoardTaskStatuses: state.projectBoardTaskStatuses,
        customBoards: state.customBoards,
        subBoardTabOrder: state.subBoardTabOrder,
        currentUserId: state.currentUserId,
      });
      const files = generateReports(ctx, {
        reportIds: [...selected],
        periodDate,
        projectIds: [...selectedProjectIds],
      });

      if (files.length === 0) {
        setMessage('No PDF files were generated for the selected reports.');
        return;
      }

      downloadReportFiles(files);
      setMessage(
        files.length === 1
          ? 'Downloaded 1 PDF report.'
          : `Downloaded ${files.length} PDF reports.`
      );
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Failed to generate reports.');
    } finally {
      setGenerating(false);
    }
  };

  const tabLabel =
    activeTab === 'clients'
      ? 'Clients'
      : activeTab === 'task-board'
        ? 'Task Board'
        : activeTab === 'time-tracking'
          ? 'Time Tracking'
          : 'Organizational Chart';

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>PDF Reports</h2>
            <p className={styles.subtitle}>Generate and download reports for your workspace.</p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>

        <div className={styles.body}>
          <div className={styles.scopeTabs}>
            <button
              type="button"
              className={`${styles.scopeTab} ${scope === 'tab' ? styles.scopeTabActive : ''}`}
              onClick={() => setScope('tab')}
            >
              {tabLabel} reports
            </button>
            <button
              type="button"
              className={`${styles.scopeTab} ${scope === 'all' ? styles.scopeTabActive : ''}`}
              onClick={() => setScope('all')}
            >
              All reports
            </button>
          </div>

          {needsPeriod && (
            <label className={styles.periodField}>
              <span className={styles.periodLabel}>Report period anchor date</span>
              <input
                type="date"
                className={styles.periodInput}
                value={periodDate}
                onChange={(e) => setPeriodDate(e.target.value)}
              />
              <span className={styles.periodHint}>
                Weekly reports use the week containing this date. Monthly reports use this month.
              </span>
            </label>
          )}

          <section className={styles.projectPicker}>
            <div className={styles.projectPickerHeader}>
              <div>
                <span className={styles.projectPickerTitle}>
                  Projects to include ({totalSelectableProjects})
                </span>
                <span className={styles.projectPickerHint}>
                  {needsProjects
                    ? 'Required for the selected reports. Organization reports ignore this filter.'
                    : 'Optional — organization reports use the full workspace.'}
                </span>
              </div>
              <div className={styles.projectPickerActions}>
                <button type="button" className={styles.linkBtn} onClick={selectAllProjects}>
                  Select all
                </button>
                <button type="button" className={styles.linkBtn} onClick={clearProjects}>
                  Clear
                </button>
              </div>
            </div>
            <div className={styles.projectPickerList}>
              {projectGroups.map(({ clientId, clientName, projects: clientProjects }) => {
                const allChecked = clientProjects.every((project) =>
                  selectedProjectIds.has(project.id)
                );
                const someChecked = clientProjects.some((project) =>
                  selectedProjectIds.has(project.id)
                );
                return (
                  <div key={clientId} className={styles.clientProjectGroup}>
                    <label className={styles.clientProjectHeader}>
                      <input
                        type="checkbox"
                        checked={allChecked}
                        ref={(input) => {
                          if (input) input.indeterminate = someChecked && !allChecked;
                        }}
                        onChange={(e) => toggleClientProjects(clientId, e.target.checked)}
                      />
                      <span>{clientName}</span>
                      <span className={styles.clientProjectCount}>{clientProjects.length}</span>
                    </label>
                    <ul className={styles.clientProjectItems}>
                      {clientProjects.map((project) => (
                        <li key={project.id}>
                          <label className={styles.projectItem}>
                            <input
                              type="checkbox"
                              checked={selectedProjectIds.has(project.id)}
                              onChange={() => toggleProject(project.id)}
                            />
                            <span>{project.name}</span>
                          </label>
                        </li>
                      ))}
                    </ul>
                  </div>
                );
              })}
            </div>
          </section>

          <div className={styles.reportList}>
            {[...reportsByCategory.entries()].map(([category, items]) => {
              const allChecked = items.every((item) => selected.has(item.id));
              const someChecked = items.some((item) => selected.has(item.id));
              return (
                <section key={category} className={styles.categoryBlock}>
                  <label className={styles.categoryHeader}>
                    <input
                      type="checkbox"
                      checked={allChecked}
                      ref={(input) => {
                        if (input) input.indeterminate = someChecked && !allChecked;
                      }}
                      onChange={(e) => toggleCategory(category, e.target.checked)}
                    />
                    <span>{category}</span>
                  </label>
                  <ul className={styles.categoryItems}>
                    {items.map((report) => (
                      <li key={report.id}>
                        <label className={styles.reportItem}>
                          <input
                            type="checkbox"
                            checked={selected.has(report.id)}
                            onChange={() => toggleReport(report.id)}
                          />
                          <span className={styles.reportText}>
                            <span className={styles.reportTitle}>{report.label}</span>
                            <span className={styles.reportDescription}>{report.description}</span>
                            {report.perProject && (
                              <span className={styles.reportBadge}>One PDF per project</span>
                            )}
                          </span>
                        </label>
                      </li>
                    ))}
                  </ul>
                </section>
              );
            })}
          </div>

          {message && <p className={styles.message}>{message}</p>}
        </div>

        <div className={styles.footer}>
          <button
            type="button"
            className={styles.cancelBtn}
            onClick={() => {
              downloadBoardroomProjectsCsv(clients, projects, { includeTemplates: true });
              setMessage('Downloaded projects CSV.');
            }}
            title="Download a CSV of all clients and projects"
          >
            Export projects CSV
          </button>
          <button type="button" className={styles.cancelBtn} onClick={onClose}>
            Close
          </button>
          <button
            type="button"
            className={styles.generateBtn}
            onClick={handleGenerate}
            disabled={
              generating ||
              selected.size === 0 ||
              (needsProjects && selectedProjectIds.size === 0)
            }
          >
            {generating ? 'Generating…' : 'Download PDFs'}
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
