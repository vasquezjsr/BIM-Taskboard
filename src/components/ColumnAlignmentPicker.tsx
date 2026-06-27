import type { SheetColumnAlign } from '../types';
import { SHEET_COLUMN_ALIGNMENTS } from '../utils/sheetColumns';
import styles from './ColumnSettings.module.css';

interface ColumnAlignmentPickerProps {
  label: string;
  hint?: string;
  value: SheetColumnAlign;
  onChange: (value: SheetColumnAlign) => void;
  groupName: string;
}

export function ColumnAlignmentPicker({
  label,
  hint,
  value,
  onChange,
  groupName,
}: ColumnAlignmentPickerProps) {
  return (
    <div className={styles.field}>
      <span className={styles.fieldLabel}>{label}</span>
      {hint ? <span className={styles.fieldHint}>{hint}</span> : null}
      <div className={styles.alignBtnGroup} role="group" aria-label={groupName}>
        {SHEET_COLUMN_ALIGNMENTS.map((option) => (
          <button
            key={option.id}
            type="button"
            className={`${styles.alignBtn} ${value === option.id ? styles.alignBtnActive : ''}`}
            onClick={() => onChange(option.id)}
          >
            {option.label}
          </button>
        ))}
      </div>
    </div>
  );
}
