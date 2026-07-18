import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';

import { listAvailablePremadeForTargets } from '../data/premadeSheetColumns';
import { useStore } from '../store/useStore';
import type { ProjectBoardType, SheetColumnAlign, SheetColumnType } from '../types';
import { SHEET_COLUMN_TYPES } from '../utils/sheetColumns';

import { ColumnAlignmentPicker } from './ColumnAlignmentPicker';

import styles from './ColumnSettings.module.css';

interface ColumnSettingsProps {
  initialBoardType: ProjectBoardType;
  boards: { id: ProjectBoardType; label: string }[];
  overviewSectionBoards?: { id: ProjectBoardType; label: string }[];
  overviewSectionBoardType?: ProjectBoardType | null;
  onClose: () => void;
  /** Render inside another dialog (no portal / chrome). */
  embedded?: boolean;
}

function textToOptions(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean);
}

function toggleBoardSelection(
  selected: ProjectBoardType[],
  boardId: ProjectBoardType
): ProjectBoardType[] {
  return selected.includes(boardId)
    ? selected.filter((id) => id !== boardId)
    : [...selected, boardId];
}

export function ColumnSettings({
  initialBoardType,
  boards,
  overviewSectionBoards,
  overviewSectionBoardType,
  onClose,
  embedded = false,
}: ColumnSettingsProps) {
  const addPremadeColumnsToTargets = useStore((s) => s.addPremadeColumnsToTargets);
  const addCustomColumnToTargets = useStore((s) => s.addCustomColumnToTargets);
  const removeSavedSheetColumnTemplate = useStore((s) => s.removeSavedSheetColumnTemplate);
  const boardSheetColumns = useStore((s) => s.boardSheetColumns);
  const boardSheetColumnOrder = useStore((s) => s.boardSheetColumnOrder);
  const mainOverviewSectionColumnOrder = useStore((s) => s.mainOverviewSectionColumnOrder);
  const mainOverviewSectionSheetColumns = useStore((s) => s.mainOverviewSectionSheetColumns);
  const savedSheetColumnTemplates = useStore((s) => s.savedSheetColumnTemplates);

  const isOverviewMode = initialBoardType === 'main' && Boolean(overviewSectionBoards?.length);
  const boardOptions = isOverviewMode ? overviewSectionBoards! : boards;
  const initialTarget =
    isOverviewMode
      ? overviewSectionBoardType ?? overviewSectionBoards![0]!.id
      : initialBoardType;

  const [selectedBoards, setSelectedBoards] = useState<ProjectBoardType[]>([initialTarget]);
  const [selectedPremadeIds, setSelectedPremadeIds] = useState<string[]>([]);
  const [selectedSavedTemplateId, setSelectedSavedTemplateId] = useState('');
  const [newLabel, setNewLabel] = useState('');
  const [newType, setNewType] = useState<SheetColumnType>('text');
  const [headerAlignment, setHeaderAlignment] = useState<SheetColumnAlign>('center');
  const [cellAlignment, setCellAlignment] = useState<SheetColumnAlign>('center');
  const [newOptionsText, setNewOptionsText] = useState('Option 1\nOption 2');
  const [saveToLibrary, setSaveToLibrary] = useState(false);

  useEffect(() => {
    setSelectedBoards([initialTarget]);
    setSelectedPremadeIds([]);
  }, [initialTarget]);

  useEffect(() => {
    if (embedded) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [embedded, onClose]);

  const availablePremade = useMemo(
    () =>
      listAvailablePremadeForTargets(
        selectedBoards,
        isOverviewMode,
        boardSheetColumns,
        boardSheetColumnOrder,
        mainOverviewSectionColumnOrder,
        mainOverviewSectionSheetColumns
      ),
    [
      selectedBoards,
      isOverviewMode,
      boardSheetColumns,
      boardSheetColumnOrder,
      mainOverviewSectionColumnOrder,
      mainOverviewSectionSheetColumns,
    ]
  );

  useEffect(() => {
    setSelectedPremadeIds((current) =>
      current.filter((id) => availablePremade.some((premade) => premade.id === id))
    );
  }, [availablePremade]);

  const applySavedTemplate = (templateId: string) => {
    setSelectedSavedTemplateId(templateId);
    if (!templateId) return;
    const template = savedSheetColumnTemplates.find((entry) => entry.id === templateId);
    if (!template) return;
    setNewLabel(template.label);
    setNewType(template.type);
    setHeaderAlignment(template.headerAlignment ?? 'center');
    setCellAlignment(template.cellAlignment ?? 'center');
    setNewOptionsText(
      template.type === 'dropdown'
        ? (template.options ?? []).join('\n') || 'Option 1\nOption 2'
        : 'Option 1\nOption 2'
    );
  };

  const handlePremadeSelectionChange = (event: React.ChangeEvent<HTMLSelectElement>) => {
    setSelectedPremadeIds(Array.from(event.target.selectedOptions, (option) => option.value));
  };

  const handleAddPremade = () => {
    if (!selectedBoards.length || !selectedPremadeIds.length) return;
    addPremadeColumnsToTargets(
      selectedBoards,
      selectedPremadeIds,
      isOverviewMode ? 'overview' : 'board'
    );
    if (!embedded) onClose();
  };

  const handleAddCustom = () => {
    if (!newLabel.trim() || !selectedBoards.length) return;
    const options = newType === 'dropdown' ? textToOptions(newOptionsText) : undefined;
    addCustomColumnToTargets(
      selectedBoards,
      isOverviewMode ? 'overview' : 'board',
      newLabel.trim(),
      newType,
      options,
      headerAlignment,
      cellAlignment,
      saveToLibrary
    );
    if (!embedded) onClose();
  };

  const selectAllBoards = () => {
    setSelectedBoards(boardOptions.map((board) => board.id));
  };

  const clearBoardSelection = () => {
    setSelectedBoards([]);
  };

  const body = (
        <div className={styles.body}>
          <div className={styles.field}>
            <div className={styles.boardPickerHeader}>
              <span className={styles.fieldLabel}>
                {isOverviewMode ? 'Apply to sections' : 'Apply to boards'}
              </span>
              <div className={styles.boardPickerActions}>
                <button type="button" className={styles.linkBtn} onClick={selectAllBoards}>
                  All
                </button>
                <button type="button" className={styles.linkBtn} onClick={clearBoardSelection}>
                  None
                </button>
              </div>
            </div>
            <div className={styles.boardChipGrid}>
              {boardOptions.map((board) => {
                const selected = selectedBoards.includes(board.id);
                return (
                  <button
                    key={board.id}
                    type="button"
                    className={`${styles.boardChip} ${selected ? styles.boardChipSelected : ''}`}
                    onClick={() =>
                      setSelectedBoards((current) => toggleBoardSelection(current, board.id))
                    }
                  >
                    {selected && <span className={styles.check}>✓</span>}
                    {board.label}
                  </button>
                );
              })}
            </div>
            <p className={styles.fieldHint}>
              {selectedBoards.length} of {boardOptions.length} selected
            </p>
          </div>

          {availablePremade.length > 0 && (
            <div className={styles.field}>
              <span className={styles.fieldLabel}>Premade columns</span>
              <span className={styles.fieldHint}>
                Only columns missing from the selected {isOverviewMode ? 'sections' : 'boards'}
              </span>
              <select
                className={styles.multiSelect}
                multiple
                size={Math.min(6, Math.max(3, availablePremade.length))}
                value={selectedPremadeIds}
                onChange={handlePremadeSelectionChange}
              >
                {availablePremade.map((premade) => (
                  <option key={premade.id} value={premade.id}>
                    {premade.label}
                    {premade.type === 'dropdown' && premade.options?.length
                      ? ` (${premade.options.length} options)`
                      : ''}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className={styles.secondaryBtn}
                onClick={handleAddPremade}
                disabled={!selectedBoards.length || !selectedPremadeIds.length}
              >
                Add selected premade
              </button>
            </div>
          )}

          <div className={styles.customDivider}>
            <span>Custom column</span>
          </div>

          {savedSheetColumnTemplates.length > 0 && (
            <label className={styles.field}>
              <span className={styles.fieldLabel}>Load from saved library</span>
              <div className={styles.savedTemplateRow}>
                <select
                  className={styles.typeSelect}
                  value={selectedSavedTemplateId}
                  onChange={(e) => applySavedTemplate(e.target.value)}
                >
                  <option value="">Choose a saved column…</option>
                  {savedSheetColumnTemplates.map((template) => (
                    <option key={template.id} value={template.id}>
                      {template.label} ({SHEET_COLUMN_TYPES.find((t) => t.id === template.type)?.label ?? template.type})
                    </option>
                  ))}
                </select>
                {selectedSavedTemplateId && (
                  <button
                    type="button"
                    className={styles.linkBtn}
                    onClick={() => {
                      removeSavedSheetColumnTemplate(selectedSavedTemplateId);
                      setSelectedSavedTemplateId('');
                    }}
                    title="Remove from saved library"
                  >
                    Remove
                  </button>
                )}
              </div>
            </label>
          )}

          <div className={styles.addGrid}>
            <label className={styles.field}>
              <span className={styles.fieldLabel}>Header name</span>
              <input
                className={styles.textInput}
                placeholder="e.g. Priority, RFI #, Review status"
                value={newLabel}
                onChange={(e) => setNewLabel(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && newType !== 'dropdown' && handleAddCustom()}
              />
            </label>
            <label className={styles.field}>
              <span className={styles.fieldLabel}>Cell type</span>
              <select
                className={styles.typeSelect}
                value={newType}
                onChange={(e) => setNewType(e.target.value as SheetColumnType)}
              >
                {SHEET_COLUMN_TYPES.map((type) => (
                  <option key={type.id} value={type.id}>
                    {type.label}
                  </option>
                ))}
              </select>
            </label>
          </div>

          <ColumnAlignmentPicker
            label="Header alignment"
            value={headerAlignment}
            onChange={setHeaderAlignment}
            groupName="Header alignment"
          />
          <ColumnAlignmentPicker
            label="Cell alignment"
            value={cellAlignment}
            onChange={setCellAlignment}
            groupName="Cell alignment"
          />

          {newType === 'dropdown' && (
            <label className={styles.field}>
              <span className={styles.fieldLabel}>Dropdown choices</span>
              <span className={styles.fieldHint}>One option per line</span>
              <textarea
                className={styles.optionsInput}
                value={newOptionsText}
                onChange={(e) => setNewOptionsText(e.target.value)}
                rows={4}
                placeholder={'Yes\nNo\nPending review'}
              />
            </label>
          )}

          <label className={styles.checkboxField}>
            <input
              type="checkbox"
              checked={saveToLibrary}
              onChange={(e) => setSaveToLibrary(e.target.checked)}
            />
            Save to my library for later use
          </label>

          <button
            type="button"
            className={styles.addBtn}
            onClick={handleAddCustom}
            disabled={!newLabel.trim() || !selectedBoards.length}
          >
            Create column
          </button>
        </div>
  );

  if (embedded) return body;

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>Add column</h2>
            <p className={styles.intro}>
              {isOverviewMode
                ? 'Choose one or more Main Overview sections, then add premade or custom columns.'
                : 'Choose one or more boards, then add premade or custom columns.'}
            </p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>
        {body}
      </div>
    </div>,
    document.body
  );
}
