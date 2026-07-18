import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { useStore } from '../store/useStore';
import type { Employee, EmployeeRole, Project, ProjectSettingsUpdate } from '../types';
import { REVIT_YEAR_OPTIONS } from '../types';
import { createDefaultDashboardAssignments } from '../data/dashboards';
import { canEditBudgetHours, canEditClientsProjects } from '../utils/permissions';
import { canAssignDetailerTrade } from '../utils/orgChart';
import styles from './ProjectSettings.module.css';

interface ProjectSettingsProps {
  project: Project;
  employees: Employee[];
  onUpdate: (updates: ProjectSettingsUpdate) => void;
  onClose: () => void;
}

function toggleMember(ids: string[], memberId: string): string[] {
  return ids.includes(memberId) ? ids.filter((id) => id !== memberId) : [...ids, memberId];
}

function MemberPicker({
  label,
  role,
  chipClass,
  memberIds,
  employees,
  onToggle,
  disabled,
  poolEmployees,
}: {
  label: string;
  role?: EmployeeRole;
  chipClass: string;
  memberIds: string[];
  employees: Employee[];
  onToggle: (ids: string[]) => void;
  disabled?: boolean;
  /** When set, replaces role-based pool filtering. */
  poolEmployees?: Employee[];
}) {
  const pool =
    poolEmployees ??
    employees.filter((e) =>
      role === 'detailer' ? canAssignDetailerTrade(e) : e.role === role
    );

  return (
    <div className={styles.section}>
      <span className={styles.sectionLabel}>{label}</span>
      <p className={styles.sectionHint}>
        {disabled
          ? 'View only — you need Edit clients & projects to change the team.'
          : 'Click names to assign or remove from this project.'}
      </p>
      {pool.length > 0 ? (
        <div className={styles.chipRow}>
          {pool.map((emp) => {
            const selected = memberIds.includes(emp.id);
            return (
              <button
                key={emp.id}
                type="button"
                className={`${styles.chip} ${chipClass} ${selected ? styles.chipSelected : ''}`}
                disabled={disabled}
                onClick={() => onToggle(toggleMember(memberIds, emp.id))}
              >
                {selected && <span className={styles.checkMark}>✓</span>}
                {emp.name}
              </button>
            );
          })}
        </div>
      ) : (
        <p className={styles.emptyPool}>No {label.toLowerCase()} on file — add them under Employees.</p>
      )}
    </div>
  );
}

export function ProjectSettings({ project, employees, onUpdate, onClose }: ProjectSettingsProps) {
  const currentUserId = useStore((s) => s.currentUserId);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const dashboardAssignments = useStore(
    (s) => s.dashboardAssignments ?? createDefaultDashboardAssignments()
  );
  const canEditBudget = canEditBudgetHours(currentUserId, employees, employeePermissions);
  const canEditProject = canEditClientsProjects(currentUserId, employees, employeePermissions);
  const spent = project.totalHoursSpent ?? 0;
  const budget = project.budgetHours;
  const [nameDraft, setNameDraft] = useState(project.name);

  const fieldCrewPool = useMemo(() => {
    const fieldIds = new Set(Object.values(dashboardAssignments.field).flatMap((ids) => ids));
    return employees
      .filter((employee) => fieldIds.has(employee.id))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [employees, dashboardAssignments]);

  const fieldLeadPool = useMemo(() => {
    const ids = new Set([
      ...(dashboardAssignments.field['site-superintendent'] ?? []),
      ...(dashboardAssignments.field.foreman ?? []),
      ...(dashboardAssignments.field['crew-lead'] ?? []),
    ]);
    return employees
      .filter((employee) => ids.has(employee.id))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [employees, dashboardAssignments]);

  const pmPool = useMemo(() => {
    const ids = new Set(dashboardAssignments.pm['project-manager'] ?? []);
    return employees
      .filter((employee) => ids.has(employee.id))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [employees, dashboardAssignments]);

  const assistantPmPool = useMemo(() => {
    const ids = new Set(dashboardAssignments.pm['assistant-pm'] ?? []);
    return employees
      .filter((employee) => ids.has(employee.id))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [employees, dashboardAssignments]);

  useEffect(() => {
    setNameDraft(project.name);
  }, [project.id, project.name]);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div
        className={styles.modal}
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="project-settings-title"
      >
        <div className={styles.header}>
          <div>
            <h2 id="project-settings-title">Project settings</h2>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>

        <div className={styles.body}>
          <label className={styles.field}>
            <span className={styles.fieldLabel}>Project name</span>
            <input
              type="text"
              className={styles.input}
              value={nameDraft}
              disabled={!canEditProject}
              onChange={(e) => setNameDraft(e.target.value)}
              onBlur={() => {
                if (!canEditProject) return;
                const trimmed = nameDraft.trim();
                if (trimmed && trimmed !== project.name) {
                  onUpdate({ name: trimmed });
                } else {
                  setNameDraft(project.name);
                }
              }}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  (e.target as HTMLInputElement).blur();
                }
              }}
              placeholder="Project name"
            />
          </label>

          <label className={styles.field}>
            <span className={styles.fieldLabel}>Job code</span>
            <input
              type="text"
              className={styles.input}
              defaultValue={project.jobCode ?? ''}
              key={`${project.id}-jobCode-${project.jobCode ?? ''}`}
              placeholder="e.g. TMPL or 24-1847"
              disabled={!canEditProject}
              onBlur={(e) => {
                if (!canEditProject) return;
                const trimmed = e.target.value.trim();
                const next = trimmed || null;
                if (next !== (project.jobCode ?? null)) {
                  onUpdate({ jobCode: next });
                }
              }}
            />
            <span className={styles.sectionHint}>Used in task numbers for this project.</span>
          </label>

          <MemberPicker
            label="Detailers"
            role="detailer"
            chipClass={styles.detailerChip}
            memberIds={project.detailerIds}
            employees={employees}
            disabled={!canEditProject}
            onToggle={(detailerIds) => onUpdate({ detailerIds })}
          />

          <MemberPicker
            label="Support"
            role="support-specialist"
            chipClass={styles.supportChip}
            memberIds={project.supportIds}
            employees={employees}
            disabled={!canEditProject}
            onToggle={(supportIds) => onUpdate({ supportIds })}
          />

          <MemberPicker
            label="Project Managers"
            chipClass={styles.fieldChip}
            memberIds={project.pmIds ?? []}
            employees={employees}
            poolEmployees={pmPool}
            disabled={!canEditProject}
            onToggle={(pmIds) => onUpdate({ pmIds })}
          />

          <MemberPicker
            label="Assistant PMs"
            chipClass={styles.fieldChip}
            memberIds={project.assistantPmIds ?? []}
            employees={employees}
            poolEmployees={assistantPmPool}
            disabled={!canEditProject}
            onToggle={(assistantPmIds) => onUpdate({ assistantPmIds })}
          />

          <MemberPicker
            label="Field Super / lead"
            chipClass={styles.fieldChip}
            memberIds={project.fieldIds ?? []}
            employees={employees}
            poolEmployees={fieldLeadPool}
            disabled={!canEditProject}
            onToggle={(fieldIds) => onUpdate({ fieldIds })}
          />

          <MemberPicker
            label="Field crew"
            chipClass={styles.fieldChip}
            memberIds={project.fieldCrewIds ?? []}
            employees={employees}
            poolEmployees={fieldCrewPool}
            disabled={!canEditProject}
            onToggle={(fieldCrewIds) => onUpdate({ fieldCrewIds })}
          />

          <div className={styles.metaSection}>
            <span className={styles.sectionLabel}>Contract & schedule</span>
            <fieldset className={styles.modelFieldset}>
              <legend className={styles.fieldLabel}>Billing type</legend>
              <div className={styles.modelOptions}>
                <label className={styles.modelOption}>
                  <input
                    type="radio"
                    name={`billing-${project.id}`}
                    checked={project.billingType === 'lump-sum'}
                    disabled={!canEditProject}
                    onChange={() => onUpdate({ billingType: 'lump-sum' })}
                  />
                  <span>Lump Sum</span>
                </label>
                <label className={styles.modelOption}>
                  <input
                    type="radio"
                    name={`billing-${project.id}`}
                    checked={project.billingType === 'time-and-material'}
                    disabled={!canEditProject}
                    onChange={() => onUpdate({ billingType: 'time-and-material' })}
                  />
                  <span>Time & Material</span>
                </label>
              </div>
            </fieldset>

            {canEditBudget ? (
              <div className={styles.field}>
                <span className={styles.fieldLabel}>Budget hours</span>
                <div className={styles.budgetHoursEdit}>
                  <label className={styles.budgetField}>
                    <span className={styles.budgetFieldLabel}>Spent</span>
                    <input
                      type="number"
                      min={0}
                      step={0.5}
                      className={styles.input}
                      value={project.totalHoursSpent ?? ''}
                      onChange={(e) =>
                        onUpdate({
                          totalHoursSpent: e.target.value === '' ? null : Number(e.target.value),
                        })
                      }
                      placeholder="0"
                    />
                  </label>
                  <label className={styles.budgetField}>
                    <span className={styles.budgetFieldLabel}>Budget</span>
                    <input
                      type="number"
                      min={0}
                      step={0.5}
                      className={styles.input}
                      value={project.budgetHours ?? ''}
                      onChange={(e) =>
                        onUpdate({
                          budgetHours: e.target.value === '' ? null : Number(e.target.value),
                        })
                      }
                      placeholder="e.g. 230"
                    />
                  </label>
                </div>
              </div>
            ) : (
              <div className={styles.field}>
                <span className={styles.fieldLabel}>Budget hours</span>
                <p className={styles.readOnlyValue}>
                  {spent}/{budget ?? '—'} Hours
                </p>
                <p className={styles.sectionHint}>Only Detailers and Joe Vasquez can edit budget hours.</p>
              </div>
            )}

            <div className={styles.dateRow}>
              <label className={styles.field}>
                <span className={styles.fieldLabel}>Project start</span>
                <input
                  type="date"
                  className={styles.input}
                  value={project.projectStartDate ?? ''}
                  onChange={(e) => onUpdate({ projectStartDate: e.target.value || null })}
                />
              </label>
              <label className={styles.field}>
                <span className={styles.fieldLabel}>Project end</span>
                <input
                  type="date"
                  className={styles.input}
                  value={project.projectEndDate ?? ''}
                  onChange={(e) => onUpdate({ projectEndDate: e.target.value || null })}
                />
              </label>
            </div>
          </div>

          <div className={styles.metaSection}>
            <label className={styles.field}>
              <span className={styles.fieldLabel}>Revit year</span>
              <select
                className={styles.select}
                value={project.revitYear ?? ''}
                onChange={(e) => onUpdate({ revitYear: e.target.value || null })}
              >
                <option value="">Select year</option>
                {REVIT_YEAR_OPTIONS.map((year) => (
                  <option key={year} value={year}>
                    {year}
                  </option>
                ))}
              </select>
            </label>

            <fieldset className={styles.modelFieldset}>
              <legend className={styles.fieldLabel}>Model type</legend>
              <div className={styles.modelOptions}>
                <label className={styles.modelOption}>
                  <input
                    type="radio"
                    name={`model-settings-${project.id}`}
                    checked={project.modelType === 'cloud'}
                    onChange={() => onUpdate({ modelType: 'cloud' })}
                  />
                  <span>Cloud model</span>
                </label>
                <label className={styles.modelOption}>
                  <input
                    type="radio"
                    name={`model-settings-${project.id}`}
                    checked={project.modelType === 'local'}
                    onChange={() => onUpdate({ modelType: 'local' })}
                  />
                  <span>Local model</span>
                </label>
              </div>
            </fieldset>
          </div>
        </div>

        <div className={styles.footer}>
          <button type="button" className={styles.doneBtn} onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
