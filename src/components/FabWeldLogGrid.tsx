import { useState } from 'react';
import styles from './FabWorkstationView.module.css';
import type { WeldLogFillField, WeldLogRow } from '../utils/weldLogWorkbook';

interface FabWeldLogGridProps {
  rows: WeldLogRow[];
  title: string;
  /** Global gate — combined with canTapFillRow when provided. */
  canTapFill: boolean;
  /** Per-row gate (e.g. Field users may only fill Field Weld rows). */
  canTapFillRow?: (row: WeldLogRow) => boolean;
  signedInLabel: string | null;
  busy?: boolean;
  error?: string | null;
  saveMessage?: string | null;
  hint?: string | null;
  onTapFill: (rowIndex: number, field: WeldLogFillField) => void;
}

function fillableClass(canTapFill: boolean, hasValue: boolean): string {
  if (!canTapFill) return styles.weldCellReadonly;
  return hasValue ? styles.weldCellFilled : styles.weldCellTap;
}

function cellTitle(canTapFill: boolean, hasValue: boolean, emptyLabel: string): string | undefined {
  if (!canTapFill) return undefined;
  return hasValue ? 'Tap again to clear' : emptyLabel;
}

export function FabWeldLogGrid({
  rows,
  title,
  canTapFill,
  canTapFillRow,
  signedInLabel,
  busy,
  error,
  saveMessage,
  hint,
  onTapFill,
}: FabWeldLogGridProps) {
  const [expanded, setExpanded] = useState(true);

  const rowCanFill = (row: WeldLogRow) =>
    canTapFill && (canTapFillRow ? canTapFillRow(row) : true);

  if (!expanded) {
    return (
      <div className={styles.weldLogCollapsed}>
        <span className={styles.weldLogCollapsedTitle}>{title}</span>
        <button
          type="button"
          className={styles.weldLogToggle}
          onClick={() => setExpanded(true)}
        >
          Expand
        </button>
      </div>
    );
  }

  const defaultHint = canTapFill
    ? `Signed in as ${signedInLabel}. Tap to fill · tap again to clear.`
    : 'Sign in to tap-fill Date, Welder ID, and Initials.';

  return (
    <div className={styles.weldLogPanel}>
      <div className={styles.weldLogHeader}>
        <div className={styles.weldLogHeaderText}>
          <h4 className={styles.weldLogTitle}>{title}</h4>
          <p className={styles.weldLogHint}>{hint ?? defaultHint}</p>
        </div>
        <button
          type="button"
          className={styles.weldLogToggle}
          onClick={() => setExpanded(false)}
          title="Collapse weld log"
        >
          Collapse
        </button>
      </div>
      {busy ? <p className={styles.empty}>Loading weld log…</p> : null}
      {error ? (
        <p className={styles.viewerError} role="alert">
          {error}
        </p>
      ) : null}
      {saveMessage ? <p className={styles.weldSaveMsg}>{saveMessage}</p> : null}
      {!busy && !error && rows.length === 0 ? (
        <p className={styles.empty}>No weld log rows for this selection.</p>
      ) : null}
      {!busy && rows.length > 0 ? (
        <div className={styles.weldTableWrap}>
          <table className={styles.weldTable}>
            <thead>
              <tr>
                <th>Weld Number</th>
                <th>Date</th>
                <th>Welder ID</th>
                <th>Initials</th>
                <th>Material</th>
                <th>Weld Type</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, index) => {
                const tap = rowCanFill(row);
                return (
                  <tr key={`${row.weldNumber}-${index}`}>
                    <td className={styles.weldCellReadonly}>{row.weldNumber}</td>
                    <td>
                      <button
                        type="button"
                        className={fillableClass(tap, Boolean(row.date))}
                        disabled={!tap}
                        onClick={() => onTapFill(index, 'date')}
                        title={cellTitle(tap, Boolean(row.date), 'Tap to set today’s date')}
                      >
                        {row.date || (tap ? 'Tap date' : '—')}
                      </button>
                    </td>
                    <td>
                      <button
                        type="button"
                        className={fillableClass(tap, Boolean(row.welderId))}
                        disabled={!tap}
                        onClick={() => onTapFill(index, 'welderId')}
                        title={cellTitle(tap, Boolean(row.welderId), 'Tap to set your welder ID')}
                      >
                        {row.welderId || (tap ? 'Tap ID' : '—')}
                      </button>
                    </td>
                    <td>
                      <button
                        type="button"
                        className={fillableClass(tap, Boolean(row.initials))}
                        disabled={!tap}
                        onClick={() => onTapFill(index, 'initials')}
                        title={cellTitle(tap, Boolean(row.initials), 'Tap to set your initials')}
                      >
                        {row.initials || (tap ? 'Tap initials' : '—')}
                      </button>
                    </td>
                    <td className={styles.weldCellReadonly}>{row.material || '—'}</td>
                    <td className={styles.weldCellReadonly}>{row.weldType || '—'}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      ) : null}
    </div>
  );
}
