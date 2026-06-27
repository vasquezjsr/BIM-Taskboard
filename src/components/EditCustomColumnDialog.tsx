import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { SheetColumnAlign, SheetColumnDefinition, SheetColumnType } from '../types';
import {
  normalizeSheetColumnAlignments,
  SHEET_COLUMN_TYPES,
} from '../utils/sheetColumns';
import { ColumnAlignmentPicker } from './ColumnAlignmentPicker';
import styles from './ColumnSettings.module.css';

interface EditCustomColumnDialogProps {
  column: SheetColumnDefinition;
  onSave: (
    updates: Partial<
      Pick<
        SheetColumnDefinition,
        'label' | 'type' | 'options' | 'headerAlignment' | 'cellAlignment'
      >
    >
  ) => void;
  onClose: () => void;
}

function textToOptions(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean);
}

export function EditCustomColumnDialog({ column, onSave, onClose }: EditCustomColumnDialogProps) {
  const initialAlignments = normalizeSheetColumnAlignments(column);
  const [label, setLabel] = useState(column.label);
  const [cellType, setCellType] = useState<SheetColumnType>(column.type);
  const [optionsText, setOptionsText] = useState((column.options ?? []).join('\n'));
  const [headerAlignment, setHeaderAlignment] = useState<SheetColumnAlign>(
    initialAlignments.headerAlignment
  );
  const [cellAlignment, setCellAlignment] = useState<SheetColumnAlign>(
    initialAlignments.cellAlignment
  );

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  const handleSave = () => {
    const trimmedLabel = label.trim();
    if (!trimmedLabel) return;

    const updates: Partial<
      Pick<
        SheetColumnDefinition,
        'label' | 'type' | 'options' | 'headerAlignment' | 'cellAlignment'
      >
    > = {
      label: trimmedLabel,
      type: cellType,
      headerAlignment,
      cellAlignment,
    };
    if (cellType === 'dropdown') {
      updates.options = textToOptions(optionsText);
    }
    onSave(updates);
    onClose();
  };

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>Edit column</h2>
            <p className={styles.intro}>Update the header name, cell type, and alignment.</p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>
        <div className={styles.body}>
          <label className={styles.field}>
            <span className={styles.fieldLabel}>Column name</span>
            <input
              className={styles.textInput}
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && cellType !== 'dropdown' && handleSave()}
              autoFocus
            />
          </label>
          <label className={styles.field}>
            <span className={styles.fieldLabel}>Cell type</span>
            <select
              className={styles.typeSelect}
              value={cellType}
              onChange={(e) => setCellType(e.target.value as SheetColumnType)}
            >
              {SHEET_COLUMN_TYPES.map((type) => (
                <option key={type.id} value={type.id}>
                  {type.label}
                </option>
              ))}
            </select>
          </label>
          <ColumnAlignmentPicker
            label="Header alignment"
            hint="Aligns the column title in the table header"
            value={headerAlignment}
            onChange={setHeaderAlignment}
            groupName="Header alignment"
          />
          <ColumnAlignmentPicker
            label="Cell alignment"
            hint="Aligns text and values in each row"
            value={cellAlignment}
            onChange={setCellAlignment}
            groupName="Cell alignment"
          />
          {cellType === 'dropdown' && (
            <label className={styles.field}>
              <span className={styles.fieldLabel}>Dropdown choices</span>
              <span className={styles.fieldHint}>One option per line</span>
              <textarea
                className={styles.optionsInput}
                value={optionsText}
                onChange={(e) => setOptionsText(e.target.value)}
                rows={6}
                placeholder={'Yes\nNo\nPending review'}
              />
            </label>
          )}
          <button
            type="button"
            className={styles.addBtn}
            onClick={handleSave}
            disabled={!label.trim()}
          >
            Save changes
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
