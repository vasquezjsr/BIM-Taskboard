import { useMemo } from 'react';
import { createPortal } from 'react-dom';
import { useStore } from '../store/useStore';
import {
  buildDefaultTaskBoardVisibleStatuses,
  collectTaskBoardStatusOptions,
} from '../utils/taskBoardVisibility';
import { findAllStatusIdsForLabel } from '../utils/statusConsolidation';
import modalStyles from './ColumnSettings.module.css';
import styles from './TaskBoardSettings.module.css';

interface TaskBoardSettingsProps {
  onClose: () => void;
}

export function TaskBoardSettings({ onClose }: TaskBoardSettingsProps) {
  const tasks = useStore((s) => s.tasks);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const taskBoardVisibleStatuses = useStore((s) => s.taskBoardVisibleStatuses);
  const setTaskBoardVisibleStatuses = useStore((s) => s.setTaskBoardVisibleStatuses);

  const statusOptions = useMemo(
    () => collectTaskBoardStatusOptions(boardTaskStatuses, projectBoardTaskStatuses, tasks),
    [boardTaskStatuses, projectBoardTaskStatuses, tasks]
  );

  const visibleSet = useMemo(
    () => new Set(taskBoardVisibleStatuses),
    [taskBoardVisibleStatuses]
  );

  const toggleStatus = (statusId: string) => {
    const label = statusOptions.find((status) => status.id === statusId)?.label;
    const equivalentIds = label
      ? findAllStatusIdsForLabel(label, boardTaskStatuses, projectBoardTaskStatuses, tasks)
      : [statusId];
    const isVisible = equivalentIds.some((id) => visibleSet.has(id));

    if (isVisible) {
      setTaskBoardVisibleStatuses(
        taskBoardVisibleStatuses.filter((id) => !equivalentIds.includes(id))
      );
      return;
    }

    setTaskBoardVisibleStatuses([...new Set([...taskBoardVisibleStatuses, ...equivalentIds])]);
  };

  const showAll = () => {
    setTaskBoardVisibleStatuses(statusOptions.map((status) => status.id));
  };

  const showDefault = () => {
    setTaskBoardVisibleStatuses(buildDefaultTaskBoardVisibleStatuses(statusOptions));
  };

  return createPortal(
    <div className={modalStyles.overlay} onClick={onClose}>
      <div
        className={modalStyles.modal}
        style={{ width: 480 }}
        onClick={(event) => event.stopPropagation()}
      >
        <div className={modalStyles.header}>
          <div>
            <h2>Task Board Settings</h2>
            <p className={modalStyles.intro}>
              Choose which statuses appear on the Detailers and Support Specialist boards.
              Unchecked statuses stay on project spreadsheets but won&apos;t show here.
            </p>
          </div>
          <button type="button" className={modalStyles.closeBtn} onClick={onClose} aria-label="Close">
            ×
          </button>
        </div>

        <div className={styles.toolbar}>
          <button type="button" className={styles.toolbarBtn} onClick={showAll}>
            Show all
          </button>
          <button type="button" className={styles.toolbarBtn} onClick={showDefault}>
            Default
          </button>
          <span className={styles.count}>
            {visibleSet.size} of {statusOptions.length} visible
          </span>
        </div>

        <ul className={styles.list}>
          {statusOptions.map((status) => {
            const equivalentIds = findAllStatusIdsForLabel(
              status.label,
              boardTaskStatuses,
              projectBoardTaskStatuses,
              tasks
            );
            const checked = equivalentIds.some((id) => visibleSet.has(id));
            return (
              <li key={status.id}>
                <label className={styles.item}>
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => toggleStatus(status.id)}
                  />
                  <span className={styles.swatch} style={{ backgroundColor: status.color }} />
                  <span className={styles.label}>{status.label}</span>
                </label>
              </li>
            );
          })}
        </ul>

        <div className={styles.footer}>
          <button type="button" className={styles.primaryBtn} onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
