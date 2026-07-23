import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import styles from './AddProjectDialog.module.css';

export interface ConfirmDialogProps {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  onConfirm: () => void;
  onCancel: () => void;
  /** Optional third path (e.g. dismiss without either action). Defaults to onCancel. */
  onDismiss?: () => void;
}

/** App-styled confirm modal (replaces window.confirm). */
export function ConfirmDialog({
  title,
  message,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  onConfirm,
  onCancel,
  onDismiss,
}: ConfirmDialogProps) {
  const handleDismiss = onDismiss ?? onCancel;

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') handleDismiss();
      if (event.key === 'Enter') {
        event.preventDefault();
        onConfirm();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [handleDismiss, onConfirm]);

  return createPortal(
    <div className={styles.overlay} onClick={handleDismiss}>
      <div
        className={styles.modal}
        style={{ width: 440 }}
        onClick={(event) => event.stopPropagation()}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        aria-describedby="confirm-dialog-message"
      >
        <div className={styles.header}>
          <h2 id="confirm-dialog-title">{title}</h2>
          <button
            type="button"
            className={styles.closeBtn}
            onClick={handleDismiss}
            title="Close"
          >
            ×
          </button>
        </div>
        <div className={styles.form}>
          <p id="confirm-dialog-message" className={styles.hint} style={{ margin: 0 }}>
            {message}
          </p>
          <div className={styles.footer}>
            <button type="button" className={styles.cancelBtn} onClick={onCancel}>
              {cancelLabel}
            </button>
            <button type="button" className={styles.submitBtn} onClick={onConfirm}>
              {confirmLabel}
            </button>
          </div>
        </div>
      </div>
    </div>,
    document.body
  );
}
