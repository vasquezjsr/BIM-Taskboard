import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { useStore } from '../store/useStore';
import type { Employee, EmployeeRole, Project, ProjectSettingsUpdate } from '../types';
import { REVIT_YEAR_OPTIONS } from '../types';
import { canEditBudgetHours } from '../utils/permissions';
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
}: {
  label: string;
  role: EmployeeRole;
  chipClass: string;
  memberIds: string[];
  employees: Employee[];
  onToggle: (ids: string[]) => void;
}) {
  const pool = employees.filter((e) =>
    role === 'detailer' ? canAssignDetailerTrade(e) : e.role === role
  );

  return (
    <div className={styles.section}>
      <span className={styles.sectionLabel}>{label}</span>
      <p className={styles.sectionHint}>Click names to assign or remove from this project.</p>
      {pool.length > 0 ? (
        <div className={styles.chipRow}>
          {pool.map((emp) => {
            const selected = memberIds.includes(emp.id);
            return (
              <button
                key={emp.id}
                type="button"
                className={`${styles.chip} ${chipClass} ${selected ? styles.chipSelected : ''}`}
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
  const canEditBudget = canEditBudgetHours(currentUserId, employees, employeePermissions);
  const spent = project.totalHoursSpent ?? 0;
  const budget = project.budgetHours;
  const [nameDraft, setNameDraft] = useState(project.name);

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
              onChange={(e) => setNameDraft(e.target.value)}
              onBlur={() => {
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
              onBlur={(e) => {
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
            onToggle={(detailerIds) => onUpdate({ detailerIds })}
          />

          <MemberPicker
            label="Support"
            role="support-specialist"
            chipClass={styles.supportChip}
            memberIds={project.supportIds}
            employees={employees}
            onToggle={(supportIds) => onUpdate({ supportIds })}
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
                    onChange={() => onUpdate({ billingType: 'lump-sum' })}
                  />
                  <span>Lump Sum</span>
                </label>
                <label className={styles.modelOption}>
                  <input
                    type="radio"
                    name={`billing-${project.id}`}
                    checked={project.billingType === 'time-and-material'}
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
