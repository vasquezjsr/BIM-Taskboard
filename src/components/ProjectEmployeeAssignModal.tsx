import { useMemo } from 'react';
import { useStore } from '../store/useStore';
import type { Project } from '../types';
import { createDefaultDashboardAssignments } from '../data/dashboards';
import deptStyles from './DepartmentDashboardView.module.css';
import styles from './ProjectEmployeeAssignModal.module.css';

function toggleId(ids: string[], id: string): string[] {
  return ids.includes(id) ? ids.filter((entry) => entry !== id) : [...ids, id];
}

interface RoleSectionProps {
  title: string;
  hint: string;
  pool: { id: string; name: string }[];
  selectedIds: string[];
  onToggle: (ids: string[]) => void;
}

function RoleSection({ title, hint, pool, selectedIds, onToggle }: RoleSectionProps) {
  return (
    <section className={styles.section}>
      <h5 className={styles.sectionTitle}>{title}</h5>
      <p className={styles.sectionHint}>{hint}</p>
      {pool.length === 0 ? (
        <p className={styles.emptyPool}>No people in this role yet — add them under Employees.</p>
      ) : (
        <div className={styles.chipRow}>
          {pool.map((person) => {
            const selected = selectedIds.includes(person.id);
            return (
              <button
                key={person.id}
                type="button"
                className={selected ? styles.chipSelected : styles.chip}
                onClick={() => onToggle(toggleId(selectedIds, person.id))}
              >
                {selected ? '✓ ' : ''}
                {person.name}
              </button>
            );
          })}
        </div>
      )}
    </section>
  );
}

interface ProjectEmployeeAssignModalProps {
  project: Project;
  onClose: () => void;
}

export function ProjectEmployeeAssignModal({ project, onClose }: ProjectEmployeeAssignModalProps) {
  const employees = useStore((s) => s.employees);
  const dashboardAssignments = useStore(
    (s) => s.dashboardAssignments ?? createDefaultDashboardAssignments()
  );
  const updateProjectSettings = useStore((s) => s.updateProjectSettings);

  const pmPool = useMemo(() => {
    const ids = new Set(dashboardAssignments.pm['project-manager'] ?? []);
    return employees.filter((e) => ids.has(e.id)).sort((a, b) => a.name.localeCompare(b.name));
  }, [employees, dashboardAssignments]);

  const assistantPool = useMemo(() => {
    const ids = new Set(dashboardAssignments.pm['assistant-pm'] ?? []);
    return employees.filter((e) => ids.has(e.id)).sort((a, b) => a.name.localeCompare(b.name));
  }, [employees, dashboardAssignments]);

  const fieldLeadPool = useMemo(() => {
    const ids = new Set([
      ...(dashboardAssignments.field['site-superintendent'] ?? []),
      ...(dashboardAssignments.field.foreman ?? []),
      ...(dashboardAssignments.field['crew-lead'] ?? []),
    ]);
    return employees.filter((e) => ids.has(e.id)).sort((a, b) => a.name.localeCompare(b.name));
  }, [employees, dashboardAssignments]);

  /** Field staff not already used as leads — crew / journeyman visibility pool */
  const fieldCrewPool = useMemo(() => {
    const allField = new Set(Object.values(dashboardAssignments.field).flatMap((ids) => ids));
    return employees
      .filter((e) => allField.has(e.id))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [employees, dashboardAssignments]);

  return (
    <div className={deptStyles.pickerOverlay} onClick={onClose}>
      <div
        className={`${deptStyles.pickerModal} ${styles.modalWide}`}
        onClick={(e) => e.stopPropagation()}
      >
        <h4>Assign employees — {project.name}</h4>
        <p className={styles.intro}>
          Choose who runs this job: PM / Assistant PM for the office, and Field Super (or lead)
          plus crew for site visibility on the Field Dashboard.
        </p>

        <RoleSection
          title="Project Manager"
          hint="Primary PM(s) for this job."
          pool={pmPool}
          selectedIds={project.pmIds ?? []}
          onToggle={(pmIds) => updateProjectSettings(project.id, { pmIds })}
        />

        <RoleSection
          title="Assistant PM"
          hint="Supports the PM on contract milestones and coordination."
          pool={assistantPool}
          selectedIds={project.assistantPmIds ?? []}
          onToggle={(assistantPmIds) => updateProjectSettings(project.id, { assistantPmIds })}
        />

        <RoleSection
          title="Field Super / lead"
          hint="Site superintendent, foreman, or journeyman in charge in the field."
          pool={fieldLeadPool}
          selectedIds={project.fieldIds ?? []}
          onToggle={(fieldIds) => updateProjectSettings(project.id, { fieldIds })}
        />

        <RoleSection
          title="Field crew"
          hint="Workers under the Field Super who need this job’s generic Field Dashboard view."
          pool={fieldCrewPool}
          selectedIds={project.fieldCrewIds ?? []}
          onToggle={(fieldCrewIds) => updateProjectSettings(project.id, { fieldCrewIds })}
        />

        <div className={styles.footer}>
          <button type="button" className={deptStyles.cancelBtn} onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </div>
  );
}
