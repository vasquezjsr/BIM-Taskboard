import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ProjectBoardType } from '../types';
import {
  applyImportColumnMapping,
  guessImportColumnMapping,
  IMPORT_DEFAULT_BOARD_OPTIONS,
  IMPORT_MAP_COLUMN_ROLES,
  readImportSheetPreview,
  type ImportColumnMapping,
  type ImportMapColumnRole,
  type ImportSheetPreview,
} from '../utils/excelImportMapping';
import {
  parseMondayBoardFile,
  type MondayImportEnsuredGroup,
  type MondayImportItem,
  type MondayImportParseResult,
} from '../utils/mondayBoardImport';
import dialogStyles from './AddProjectDialog.module.css';
import styles from './ImportMondayDialog.module.css';

export interface ImportMondayDialogResult {
  projectName: string;
  items: MondayImportItem[];
  ensuredGroups: MondayImportEnsuredGroup[];
  warnings: string[];
}

interface ImportMondayDialogProps {
  onSubmit: (result: ImportMondayDialogResult) => void;
  onClose: () => void;
}

type ImportMode = 'auto' | 'map';

export function ImportMondayDialog({ onSubmit, onClose }: ImportMondayDialogProps) {
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [projectName, setProjectName] = useState('');
  const [fileName, setFileName] = useState('');
  const [mode, setMode] = useState<ImportMode>('auto');
  const [autoParsed, setAutoParsed] = useState<MondayImportParseResult | null>(null);
  const [preview, setPreview] = useState<ImportSheetPreview | null>(null);
  const [mapping, setMapping] = useState<ImportColumnMapping | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  const mappedParsed = useMemo(() => {
    if (!preview || !mapping) return null;
    return applyImportColumnMapping(preview, mapping);
  }, [preview, mapping]);

  const activeParsed = mode === 'map' ? mappedParsed : autoParsed;

  const handleFile = async (file: File | null) => {
    if (!file) return;
    setLoading(true);
    setError(null);
    setAutoParsed(null);
    setPreview(null);
    setMapping(null);
    setFileName(file.name);
    try {
      const buffer = await file.arrayBuffer();
      const auto = parseMondayBoardFile(buffer, file.name);
      const sheetPreview = readImportSheetPreview(buffer, file.name);
      const guessed = guessImportColumnMapping(sheetPreview);

      setAutoParsed(auto);
      setPreview(sheetPreview);
      setMapping(guessed);

      if (!projectName.trim()) {
        setProjectName(auto.boardNameHint || sheetPreview.boardNameHint);
      }

      const autoOk = auto.items.length > 0 || auto.ensuredGroups.length > 0;
      if (autoOk) {
        setMode('auto');
        setError(null);
      } else {
        setMode('map');
        setError(
          auto.warnings[0] ??
            'Auto-detect found nothing — map Board / Group / Task columns below.'
        );
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not read that file.');
      setAutoParsed(null);
      setPreview(null);
      setMapping(null);
    } finally {
      setLoading(false);
    }
  };

  const setRoleAt = (index: number, role: ImportMapColumnRole) => {
    setMapping((prev) => {
      if (!prev) return prev;
      const roles = [...prev.roles];
      // Only one column per structural role (except skip)
      if (role !== 'skip') {
        for (let i = 0; i < roles.length; i++) {
          if (i !== index && roles[i] === role) roles[i] = 'skip';
        }
      }
      roles[index] = role;
      return { ...prev, roles };
    });
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (
      !projectName.trim() ||
      !activeParsed ||
      (activeParsed.items.length === 0 && activeParsed.ensuredGroups.length === 0)
    ) {
      return;
    }
    onSubmit({
      projectName: projectName.trim(),
      items: activeParsed.items,
      ensuredGroups: activeParsed.ensuredGroups,
      warnings: activeParsed.warnings,
    });
  };

  const canSubmit = Boolean(
    projectName.trim() &&
      activeParsed &&
      (activeParsed.items.length > 0 || activeParsed.ensuredGroups.length > 0) &&
      !loading
  );

  const wide = Boolean(preview && (mode === 'map' || preview.headers.length > 0));

  return createPortal(
    <div className={dialogStyles.overlay} onClick={onClose}>
      <div
        className={`${dialogStyles.modal} ${wide ? styles.wideModal : ''}`}
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="import-project-excel-title"
      >
        <div className={dialogStyles.header}>
          <h2 id="import-project-excel-title">Import Project from Excel</h2>
          <button type="button" className={dialogStyles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>

        <form className={dialogStyles.form} onSubmit={handleSubmit}>
          <p className={dialogStyles.hint}>
            Choose a Monday.com or Smartsheet Excel/CSV export. Auto-detect runs first; if the layout
            is unusual, use Map columns to assign Board, Group, Sub-group, Main task, and Sub-task.
          </p>

          <label className={dialogStyles.field}>
            <span className={dialogStyles.label}>Excel / CSV file</span>
            <input
              ref={fileInputRef}
              type="file"
              accept=".xlsx,.xls,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv"
              style={{ display: 'none' }}
              onChange={(e) => void handleFile(e.target.files?.[0] ?? null)}
            />
            <button
              type="button"
              className={dialogStyles.cancelBtn}
              onClick={() => fileInputRef.current?.click()}
              disabled={loading}
            >
              {loading ? 'Reading…' : fileName ? 'Choose another file…' : 'Choose file…'}
            </button>
            {fileName ? <span className={dialogStyles.summary}>{fileName}</span> : null}
          </label>

          <label className={dialogStyles.field}>
            <span className={dialogStyles.label}>Project name</span>
            <input
              autoFocus
              className={dialogStyles.input}
              placeholder="e.g. Office Tower — Mechanical"
              value={projectName}
              onChange={(e) => setProjectName(e.target.value)}
            />
          </label>

          {preview ? (
            <div className={styles.modeRow}>
              <button
                type="button"
                className={mode === 'auto' ? styles.modeBtnActive : styles.modeBtn}
                onClick={() => setMode('auto')}
                disabled={!autoParsed}
              >
                Auto-detect
              </button>
              <button
                type="button"
                className={mode === 'map' ? styles.modeBtnActive : styles.modeBtn}
                onClick={() => setMode('map')}
              >
                Map columns
              </button>
            </div>
          ) : null}

          {mode === 'auto' && autoParsed && (autoParsed.items.length > 0 || autoParsed.ensuredGroups.length > 0) ? (
            <p className={dialogStyles.summary}>
              {autoParsed.items.length} item{autoParsed.items.length === 1 ? '' : 's'}
              {autoParsed.importedBoardNames.length
                ? ` · from ${autoParsed.importedBoardNames.join(', ')}`
                : ''}
              {autoParsed.skippedBoardNames.length
                ? ` · skipped ${autoParsed.skippedBoardNames.length} board${
                    autoParsed.skippedBoardNames.length === 1 ? '' : 's'
                  }`
                : ''}
            </p>
          ) : null}

          {mode === 'map' && preview && mapping ? (
            <div className={styles.mapPanel}>
              <p className={dialogStyles.hint}>
                Assign each column. Sub-groups become parent tasks under a Group (Boardroom has one
                group level). Blank Board/Group cells can carry forward from the row above.
              </p>

              <div className={styles.mapControls}>
                <label className={styles.inlineField}>
                  <span className={dialogStyles.label}>Default board</span>
                  <select
                    className={dialogStyles.select}
                    value={mapping.defaultBoardType ?? ''}
                    onChange={(e) =>
                      setMapping((prev) =>
                        prev
                          ? {
                              ...prev,
                              defaultBoardType: (e.target.value || null) as ProjectBoardType | null,
                            }
                          : prev
                      )
                    }
                  >
                    <option value="">None (require Board column)</option>
                    {IMPORT_DEFAULT_BOARD_OPTIONS.map((b) => (
                      <option key={b.id} value={b.id}>
                        {b.label}
                      </option>
                    ))}
                  </select>
                </label>
                <label className={styles.carryCheck}>
                  <input
                    type="checkbox"
                    checked={mapping.carryForward}
                    onChange={(e) =>
                      setMapping((prev) =>
                        prev ? { ...prev, carryForward: e.target.checked } : prev
                      )
                    }
                  />
                  Carry forward blank Board / Group / Sub-group / Task cells
                </label>
              </div>

              <div className={styles.tableScroll}>
                <table className={styles.previewTable}>
                  <thead>
                    <tr>
                      {preview.headers.map((header, i) => (
                        <th key={`h-${i}`}>
                          <div className={styles.colHead}>{header || `Column ${i + 1}`}</div>
                          <select
                            className={styles.roleSelect}
                            value={mapping.roles[i] ?? 'skip'}
                            onChange={(e) =>
                              setRoleAt(i, e.target.value as ImportMapColumnRole)
                            }
                            aria-label={`Role for ${header || `column ${i + 1}`}`}
                          >
                            {IMPORT_MAP_COLUMN_ROLES.map((role) => (
                              <option key={role.id} value={role.id}>
                                {role.label}
                              </option>
                            ))}
                          </select>
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {preview.previewRows.map((row, ri) => (
                      <tr key={`r-${ri}`}>
                        {preview.headers.map((_, ci) => (
                          <td key={`c-${ri}-${ci}`}>{row[ci] || '—'}</td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {preview.rows.length > preview.previewRows.length ? (
                <p className={dialogStyles.summary}>
                  Showing {preview.previewRows.length} of {preview.rows.length} rows
                </p>
              ) : null}

              {mappedParsed ? (
                <div className={styles.resultPreview}>
                  <p className={dialogStyles.summary}>
                    Mapped result: {mappedParsed.items.length} item
                    {mappedParsed.items.length === 1 ? '' : 's'}
                    {mappedParsed.groupNames.length
                      ? ` · ${mappedParsed.groupNames.length} group${
                          mappedParsed.groupNames.length === 1 ? '' : 's'
                        }`
                      : ''}
                    {mappedParsed.importedBoardNames.length
                      ? ` · ${mappedParsed.importedBoardNames.join(', ')}`
                      : ''}
                  </p>
                  {mappedParsed.items.length > 0 ? (
                    <ul className={styles.treePreview}>
                      {mappedParsed.items.slice(0, 12).map((item, idx) => (
                        <li
                          key={`${item.boardType}-${item.groupName}-${item.title}-${idx}`}
                          className={item.parentTitle ? styles.treeChild : styles.treeRoot}
                        >
                          <span className={styles.treeMeta}>
                            [{item.boardType}] {item.groupName}
                          </span>
                          {item.parentTitle ? (
                            <span className={styles.treeParent}>↳ {item.parentTitle} / </span>
                          ) : null}
                          {item.title}
                        </li>
                      ))}
                      {mappedParsed.items.length > 12 ? (
                        <li className={styles.treeMore}>
                          +{mappedParsed.items.length - 12} more…
                        </li>
                      ) : null}
                    </ul>
                  ) : null}
                </div>
              ) : null}
            </div>
          ) : null}

          {activeParsed?.warnings.length ? (
            <ul className={dialogStyles.hint} style={{ margin: 0, paddingLeft: 18 }}>
              {activeParsed.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          ) : null}

          {error ? (
            <p className={dialogStyles.hint} style={{ color: 'var(--danger, #e57373)' }}>
              {error}
            </p>
          ) : null}

          <div className={dialogStyles.footer}>
            <button type="button" className={dialogStyles.cancelBtn} onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className={dialogStyles.submitBtn} disabled={!canSubmit}>
              Import project
            </button>
          </div>
        </form>
      </div>
    </div>,
    document.body
  );
}
