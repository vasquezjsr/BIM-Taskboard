import { useStore } from '../store/useStore';
import {
  DEFAULT_WORKFLOW_DUE_DATE_OFFSETS,
  type WorkflowDueDateOffsets,
} from '../utils/workflowDueDateCascade';
import styles from './ColumnSettings.module.css';
import hubStyles from './BoardColumnSettingsHub.module.css';

type OffsetKey = keyof WorkflowDueDateOffsets;

const OFFSET_FIELDS: {
  key: OffsetKey;
  label: string;
  hint: string;
}[] = [
  {
    key: 'spoolingDaysAfterDetailing',
    label: 'Spooling due date',
    hint: 'Days after Detailing Due Date',
  },
  {
    key: 'fabricationDaysAfterSpooling',
    label: 'Fabrication due date',
    hint: 'Days after Spooling Due Date',
  },
  {
    key: 'shippingDaysAfterFabrication',
    label: 'Shipping due date',
    hint: 'Days after Fabrication Due Date',
  },
];

interface WorkflowDueDateOffsetsPanelProps {
  canEdit: boolean;
}

export function WorkflowDueDateOffsetsPanel({ canEdit }: WorkflowDueDateOffsetsPanelProps) {
  const offsets = useStore((s) => s.workflowDueDateOffsets);
  const setWorkflowDueDateOffsets = useStore((s) => s.setWorkflowDueDateOffsets);

  const exampleDetailing = '2026-07-01';
  const spoolingDays = offsets?.spoolingDaysAfterDetailing ?? 7;
  const fabDays = offsets?.fabricationDaysAfterSpooling ?? 7;
  const shipDays = offsets?.shippingDaysAfterFabrication ?? 7;

  return (
    <div className={styles.body}>
      <p className={styles.intro}>
        When someone sets Detailing Due Date, Spooling, Fabrication, and Shipping due dates fill in
        automatically from these offsets. Anyone with task-edit permission can still change those
        dates by hand afterward.
      </p>

      <div className={hubStyles.offsetList}>
        {OFFSET_FIELDS.map((field) => (
          <label key={field.key} className={styles.field}>
            <span className={styles.fieldLabel}>{field.label}</span>
            <span className={styles.fieldHint}>{field.hint}</span>
            <div className={hubStyles.offsetInputRow}>
              <input
                type="number"
                className={styles.textInput}
                min={0}
                max={3650}
                step={1}
                disabled={!canEdit}
                value={offsets?.[field.key] ?? DEFAULT_WORKFLOW_DUE_DATE_OFFSETS[field.key]}
                onChange={(event) => {
                  const next = Number(event.target.value);
                  if (!Number.isFinite(next)) return;
                  setWorkflowDueDateOffsets({ [field.key]: next });
                }}
              />
              <span className={hubStyles.offsetUnit}>days later</span>
            </div>
          </label>
        ))}
      </div>

      <p className={hubStyles.message}>
        Example: Detailing {exampleDetailing} → Spooling +{spoolingDays}d → Fabrication +{fabDays}d
        → Shipping +{shipDays}d.
      </p>

      {canEdit ? (
        <div className={hubStyles.footerActions}>
          <button
            type="button"
            className={styles.linkBtn}
            onClick={() => setWorkflowDueDateOffsets({ ...DEFAULT_WORKFLOW_DUE_DATE_OFFSETS })}
          >
            Reset to 7 / 7 / 7
          </button>
        </div>
      ) : (
        <p className={hubStyles.warning}>
          You can view these offsets, but need column-manage permission to change them.
        </p>
      )}
    </div>
  );
}
