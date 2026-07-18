import { useMemo, useState } from 'react';
import { useStore } from '../store/useStore';
import {
  EMPLOYEE_STAGES,
  type EmployeeStageId,
} from '../utils/employeeDashboardStages';
import type { EmployeeJobTitleDef } from '../utils/employeeJobs';
import styles from './JobTitlesSettingsModal.module.css';

type Props = {
  onClose: () => void;
};

export function JobTitlesSettingsModal({ onClose }: Props) {
  const employeeJobTitles = useStore((s) => s.employeeJobTitles);
  const employees = useStore((s) => s.employees);
  const addJobTitle = useStore((s) => s.addJobTitle);
  const updateJobTitle = useStore((s) => s.updateJobTitle);
  const removeJobTitle = useStore((s) => s.removeJobTitle);

  const [newLabel, setNewLabel] = useState('');
  const [newStage, setNewStage] = useState<EmployeeStageId>('detailers');
  const [error, setError] = useState<string | null>(null);

  const holderCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const employee of employees) {
      if (!employee.jobTitleId) continue;
      counts.set(employee.jobTitleId, (counts.get(employee.jobTitleId) ?? 0) + 1);
    }
    return counts;
  }, [employees]);

  const sortedTitles = useMemo(() => {
    const stageOrder = new Map(EMPLOYEE_STAGES.map((stage, index) => [stage.id, index]));
    return [...employeeJobTitles].sort((a, b) => {
      const stageDiff = (stageOrder.get(a.stageId) ?? 99) - (stageOrder.get(b.stageId) ?? 99);
      if (stageDiff !== 0) return stageDiff;
      return a.label.localeCompare(b.label);
    });
  }, [employeeJobTitles]);

  const handleAdd = () => {
    setError(null);
    const id = addJobTitle(newLabel, newStage);
    if (!id) {
      setError('Enter a job title name.');
      return;
    }
    setNewLabel('');
  };

  const handleDelete = (title: EmployeeJobTitleDef) => {
    setError(null);
    const holders = holderCounts.get(title.id) ?? 0;
    if (holders > 0) {
      setError(
        `“${title.label}” is assigned to ${holders} ${holders === 1 ? 'person' : 'people'}. Reassign them first.`
      );
      return;
    }
    const confirmed = window.confirm(`Delete job title “${title.label}”?`);
    if (!confirmed) return;
    if (!removeJobTitle(title.id)) {
      setError('Could not delete that job title.');
    }
  };

  return (
    <div className={styles.overlay} onClick={onClose} role="presentation">
      <div
        className={styles.modal}
        role="dialog"
        aria-modal="true"
        aria-labelledby="job-titles-settings-title"
        onClick={(event) => event.stopPropagation()}
      >
        <header className={styles.header}>
          <div>
            <h2 id="job-titles-settings-title">Job titles</h2>
            <p className={styles.subtitle}>
              Create titles and choose which Employees stage they land in.
            </p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} aria-label="Close">
            ×
          </button>
        </header>

        <div className={styles.body}>
          <div className={styles.addRow}>
            <input
              className={styles.input}
              value={newLabel}
              placeholder="New job title"
              aria-label="New job title name"
              onChange={(event) => setNewLabel(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') handleAdd();
              }}
            />
            <select
              className={styles.select}
              value={newStage}
              aria-label="Stage for new job title"
              onChange={(event) => setNewStage(event.target.value as EmployeeStageId)}
            >
              {EMPLOYEE_STAGES.map((stage) => (
                <option key={stage.id} value={stage.id}>
                  {stage.laneLabel} / {stage.label}
                </option>
              ))}
            </select>
            <button type="button" className={styles.primaryBtn} onClick={handleAdd}>
              Add
            </button>
          </div>

          {error && <p className={styles.error}>{error}</p>}

          <div className={styles.list}>
            <div className={styles.columnHeaders} aria-hidden={sortedTitles.length === 0}>
              <span className={styles.columnHeader}>Job title</span>
              <span className={styles.columnHeader}>Employees stage</span>
              <span className={`${styles.columnHeader} ${styles.columnHeaderPeople}`}>People</span>
              <span className={styles.columnHeader} />
            </div>
            {sortedTitles.map((title) => {
              const holders = holderCounts.get(title.id) ?? 0;

              return (
                <article key={title.id} className={styles.row}>
                  <input
                    className={styles.input}
                    value={title.label}
                    aria-label={`Name for ${title.label}`}
                    onChange={(event) => updateJobTitle(title.id, { label: event.target.value })}
                  />
                  <select
                    className={styles.select}
                    value={title.stageId}
                    aria-label={`Stage for ${title.label}`}
                    onChange={(event) => {
                      const stageId = event.target.value as EmployeeStageId;
                      updateJobTitle(title.id, { stageId });
                    }}
                  >
                    {EMPLOYEE_STAGES.map((stage) => (
                      <option key={stage.id} value={stage.id}>
                        {stage.laneLabel} / {stage.label}
                      </option>
                    ))}
                  </select>

                  <span className={styles.meta} title="People with this title">
                    {holders}
                  </span>
                  <button
                    type="button"
                    className={styles.deleteBtn}
                    onClick={() => handleDelete(title)}
                    title={
                      holders > 0
                        ? 'Reassign people before deleting'
                        : `Delete ${title.label}`
                    }
                    aria-label={`Delete ${title.label}`}
                  >
                    ×
                  </button>
                </article>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
}
