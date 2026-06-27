import { useMemo, useState } from 'react';
import type { Employee, Project, ProjectSettingsUpdate } from '../types';
import { ProjectSettings } from './ProjectSettings';
import styles from './ProjectMetaPanel.module.css';

interface ProjectMetaPanelProps {
  project: Project;
  employees: Employee[];
  onUpdate: (updates: ProjectSettingsUpdate) => void;
}

function ReadOnlyTags({
  label,
  names,
  tagClass,
  emptyLabel,
}: {
  label: string;
  names: string[];
  tagClass: string;
  emptyLabel: string;
}) {
  return (
    <div className={styles.section}>
      <span className={styles.sectionLabel}>{label}</span>
      {names.length > 0 ? (
        <div className={styles.tagRow}>
          {names.map((name) => (
            <span key={name} className={`${styles.tag} ${tagClass}`}>
              {name}
            </span>
          ))}
        </div>
      ) : (
        <span className={styles.emptyValue}>{emptyLabel}</span>
      )}
    </div>
  );
}

function formatProjectDate(value: string | null): string {
  if (!value) return '—';
  const [year, month, day] = value.split('-');
  if (!year || !month || !day) return value;
  return `${month}/${day}/${year}`;
}

function billingTypeLabel(billingType: Project['billingType']): string {
  return billingType === 'time-and-material' ? 'Time & Material' : 'Lump Sum';
}

function MetaItem({
  label,
  value,
  centered = false,
  wrap = false,
}: {
  label: string;
  value: string;
  centered?: boolean;
  wrap?: boolean;
}) {
  return (
    <div
      className={`${styles.metaItem} ${centered ? styles.metaItemCentered : ''} ${wrap ? styles.metaItemWrap : ''}`}
    >
      <span className={styles.metaItemLabel}>{label}</span>
      <div className={styles.metaValueSlot}>
        <span className={styles.metaValue}>{value}</span>
      </div>
    </div>
  );
}

function BudgetHoursDisplay({ project }: { project: Project }) {
  const spent = project.totalHoursSpent ?? 0;
  const budget = project.budgetHours;

  return (
    <div className={`${styles.metaItem} ${styles.metaItemBudget}`}>
      <span className={styles.metaItemLabel}>Budget Hours</span>
      <div className={styles.metaValueSlot}>
        <span className={styles.metaValue}>
          {spent}/{budget ?? '—'}
        </span>
      </div>
    </div>
  );
}

export function ProjectMetaPanel({ project, employees, onUpdate }: ProjectMetaPanelProps) {
  const [showSettings, setShowSettings] = useState(false);

  const assignedDetailers = useMemo(
    () =>
      project.detailerIds
        .map((id) => employees.find((e) => e.id === id)?.name)
        .filter((name): name is string => Boolean(name)),
    [project.detailerIds, employees]
  );

  const assignedSupport = useMemo(
    () =>
      project.supportIds
        .map((id) => employees.find((e) => e.id === id)?.name)
        .filter((name): name is string => Boolean(name)),
    [project.supportIds, employees]
  );

  const modelLabel =
    project.modelType === 'cloud' ? 'Cloud' : project.modelType === 'local' ? 'Local' : null;

  return (
    <aside className={styles.panel} aria-label="Project info">
      <div className={styles.panelHeader}>
        <span className={styles.panelTitle}>{project.name}</span>
        <button
          type="button"
          className={styles.settingsBtn}
          onClick={() => setShowSettings(true)}
          title="Project settings"
        >
          ⚙
        </button>
      </div>

      {showSettings && (
        <ProjectSettings
          project={project}
          employees={employees}
          onUpdate={onUpdate}
          onClose={() => setShowSettings(false)}
        />
      )}

      <ReadOnlyTags
        label="Detailers"
        names={assignedDetailers}
        tagClass={styles.detailerTag}
        emptyLabel="None assigned"
      />

      <ReadOnlyTags
        label="Support"
        names={assignedSupport}
        tagClass={styles.supportTag}
        emptyLabel="None assigned"
      />

      <div className={styles.metaRow}>
        <MetaItem label="Billing" value={billingTypeLabel(project.billingType)} wrap />
        <MetaItem label="Start" value={formatProjectDate(project.projectStartDate)} centered />
        <MetaItem label="End" value={formatProjectDate(project.projectEndDate)} centered />
      </div>

      <div className={styles.metaRow}>
        <div className={`${styles.metaItem} ${styles.metaItemCompact}`}>
          <span className={styles.metaItemLabel}>Revit</span>
          <div className={styles.metaValueSlot}>
            <span className={styles.metaValue}>{project.revitYear ?? '—'}</span>
          </div>
        </div>
        <div className={`${styles.metaItem} ${styles.metaItemCompact}`}>
          <span className={styles.metaItemLabel}>Model</span>
          <div className={styles.metaValueSlot}>
            <span className={styles.metaValue}>{modelLabel ?? '—'}</span>
          </div>
        </div>
        <BudgetHoursDisplay project={project} />
      </div>
    </aside>
  );
}
