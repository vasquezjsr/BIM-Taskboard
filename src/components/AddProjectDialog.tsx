import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  defaultActiveLevels,
  DEFAULT_BUILDING_LEVEL_COUNT,
  formatBuildingLevelOptionLabel,
  generateBuildingLevels,
  toggleLevelSelection,
} from '../utils/buildingLevels';
import styles from './AddProjectDialog.module.css';

export interface AddProjectDialogResult {
  name: string;
  useTemplate: boolean;
  buildingLevels: string[];
  activeLevels: string[];
}

interface AddProjectDialogProps {
  onSubmit: (result: AddProjectDialogResult) => void;
  onClose: () => void;
}

export function AddProjectDialog({ onSubmit, onClose }: AddProjectDialogProps) {
  const [name, setName] = useState('');
  const [useTemplate, setUseTemplate] = useState(true);
  const [totalLevels, setTotalLevels] = useState(DEFAULT_BUILDING_LEVEL_COUNT);

  const buildingLevels = useMemo(() => generateBuildingLevels(totalLevels), [totalLevels]);
  const [activeLevels, setActiveLevels] = useState<string[]>(() =>
    defaultActiveLevels(generateBuildingLevels(DEFAULT_BUILDING_LEVEL_COUNT))
  );

  useEffect(() => {
    setActiveLevels((prev) => {
      const kept = prev.filter((l) => buildingLevels.includes(l));
      return kept.length ? kept : defaultActiveLevels(buildingLevels);
    });
  }, [buildingLevels]);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim() || activeLevels.length === 0) return;
    onSubmit({
      name: name.trim(),
      useTemplate,
      buildingLevels,
      activeLevels,
    });
  };

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div
        className={styles.modal}
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="add-project-title"
      >
        <div className={styles.header}>
          <h2 id="add-project-title">New project</h2>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>

        <form className={styles.form} onSubmit={handleSubmit}>
          <label className={styles.field}>
            <span className={styles.label}>Project name</span>
            <input
              autoFocus
              className={styles.input}
              placeholder="e.g. Westside Medical Center"
              value={name}
              onChange={(e) => setName(e.target.value)}
            />
          </label>

          <label className={styles.templateCheck}>
            <input
              type="checkbox"
              checked={useTemplate}
              onChange={(e) => setUseTemplate(e.target.checked)}
            />
            <span>Use Project Template</span>
          </label>

          <div className={styles.levelSection}>
            <label className={styles.field}>
              <span className={styles.label}>Building levels (UG → Roof)</span>
              <select
                className={styles.select}
                value={totalLevels}
                onChange={(e) => setTotalLevels(Number(e.target.value))}
              >
                {Array.from({ length: 29 }, (_, i) => i + 2).map((n) => (
                  <option key={n} value={n}>
                    {formatBuildingLevelOptionLabel(n)}
                  </option>
                ))}
              </select>
            </label>

            <div className={styles.field}>
              <span className={styles.label}>Levels in scope for this project</span>
              <p className={styles.hint}>
                Select every floor the team is working on. Unselected levels are omitted from the
                project boards. Default is UG + 8 levels + Roof.
              </p>
              <div className={styles.levelGrid}>
                {buildingLevels.map((level) => {
                  const selected = activeLevels.includes(level);
                  return (
                    <button
                      key={level}
                      type="button"
                      className={`${styles.levelChip} ${selected ? styles.levelChipSelected : ''}`}
                      onClick={() =>
                        setActiveLevels((prev) =>
                          toggleLevelSelection(prev, level, buildingLevels)
                        )
                      }
                    >
                      {selected && <span className={styles.check}>✓</span>}
                      {level}
                    </button>
                  );
                })}
              </div>
              <p className={styles.summary}>
                {activeLevels.length} of {buildingLevels.length} levels selected
              </p>
            </div>
          </div>

          <div className={styles.footer}>
            <button type="button" className={styles.cancelBtn} onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className={styles.submitBtn} disabled={!name.trim()}>
              Create project
            </button>
          </div>
        </form>
      </div>
    </div>,
    document.body
  );
}
