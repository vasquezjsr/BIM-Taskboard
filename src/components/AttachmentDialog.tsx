import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { v4 as uuid } from 'uuid';
import { useStore } from '../store/useStore';
import type { Task, TaskAttachment } from '../types';
import { deleteFileBlob, getFileBlob, saveFileBlob } from '../utils/fileStorage';
import {
  isSsv3ExportLocked,
  spoolingTaskHasSsv3Export,
} from '../utils/boardroomPackageImport';
import { canEditFabStatus } from '../utils/permissions';
import styles from './AttachmentDialog.module.css';

interface AttachmentDialogProps {
  task: Task;
  onClose: () => void;
  /** When false, hide upload/remove (view attachments only). Defaults to true. */
  allowMutate?: boolean;
}

type PendingUpload = {
  file: File;
  action: 'new' | 'replace' | 'newVersion';
};

export function AttachmentDialog({ task, onClose, allowMutate = true }: AttachmentDialogProps) {
  const employees = useStore((s) => s.employees);
  const liveTask = useStore((s) => s.tasks.find((entry) => entry.id === task.id) ?? task);
  const taskAttachments = useStore((s) => s.taskAttachments);
  const upsertTaskAttachment = useStore((s) => s.upsertTaskAttachment);
  const removeTaskAttachment = useStore((s) => s.removeTaskAttachment);
  const clearSsv3ExportFromTask = useStore((s) => s.clearSsv3ExportFromTask);
  const ensureBoardroomAttachmentsForTask = useStore((s) => s.ensureBoardroomAttachmentsForTask);
  const currentUserId = useStore((s) => s.currentUserId);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [pending, setPending] = useState<PendingUpload | null>(null);
  const [duplicateName, setDuplicateName] = useState<string | null>(null);
  const [confirmClearExport, setConfirmClearExport] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const attachments = useMemo(
    () => taskAttachments.filter((a) => a.taskId === liveTask.id),
    [taskAttachments, liveTask.id]
  );

  // Package Main Task: keep paperclip in sync with SSv3 export files.
  useEffect(() => {
    if (!liveTask.parentTaskId && spoolingTaskHasSsv3Export(liveTask)) {
      ensureBoardroomAttachmentsForTask(liveTask.id);
    }
  }, [liveTask.id, liveTask.parentTaskId, ensureBoardroomAttachmentsForTask]);

  const canClearSsv3Export =
    allowMutate &&
    canEditFabStatus(currentUserId, employees, employeePermissions) &&
    liveTask.boardType === 'spooling' &&
    spoolingTaskHasSsv3Export(liveTask) &&
    !isSsv3ExportLocked(liveTask);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !pending && !confirmClearExport) onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose, pending, confirmClearExport]);

  const findByName = (fileName: string): TaskAttachment | undefined =>
    attachments.find((a) => a.fileName.toLowerCase() === fileName.toLowerCase());

  const commitUpload = async (file: File, action: 'new' | 'replace' | 'newVersion') => {
    setBusy(true);
    setError(null);
    try {
      const storageId = uuid();
      await saveFileBlob(storageId, file);
      upsertTaskAttachment({
        taskId: liveTask.id,
        fileName: file.name,
        mimeType: file.type || 'application/octet-stream',
        sizeBytes: file.size,
        storageId,
        uploadedById: currentUserId,
        mode: action,
      });
      setPending(null);
      setDuplicateName(null);
    } catch {
      setError('Could not save the file. Try a smaller file or check storage permissions.');
    } finally {
      setBusy(false);
    }
  };

  const handleFilePick = async (file: File | null) => {
    if (!file) return;
    const existing = findByName(file.name);
    if (existing) {
      setDuplicateName(file.name);
      setPending({ file, action: 'newVersion' });
      return;
    }
    await commitUpload(file, 'new');
  };

  const isBoardroomFile = (storageId: string | undefined) =>
    Boolean(storageId?.startsWith('boardroom-abs:'));

  const openOrDownload = async (attachment: TaskAttachment) => {
    const version = attachment.versions.find((v) => v.id === attachment.currentVersionId);
    if (!version) return;

    if (isBoardroomFile(version.storageId)) {
      const filePath = version.storageId.slice('boardroom-abs:'.length);
      const open = window.electronAPI?.openPath;
      if (!open) {
        setError('Open file requires the BIM Boardroom desktop app.');
        return;
      }
      const result = await open(filePath);
      if (!result.ok) {
        setError(result.error);
      }
      return;
    }

    const blob = await getFileBlob(version.storageId);
    if (!blob) {
      setError('File data is missing from local storage.');
      return;
    }
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = version.fileName;
    link.click();
    URL.revokeObjectURL(url);
  };

  const handleRemove = async (attachmentId: string) => {
    const attachment = attachments.find((a) => a.id === attachmentId);
    if (!attachment) return;
    const storageIds = removeTaskAttachment(attachmentId).filter(
      (id) => !id.startsWith('boardroom-abs:')
    );
    await Promise.all(storageIds.map((id) => deleteFileBlob(id)));
  };

  const handleClearSsv3Export = () => {
    setConfirmClearExport(false);
    setError(null);
    try {
      clearSsv3ExportFromTask(liveTask.id);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not clear the SSv3 export.');
    }
  };

  const formatMeta = (attachment: TaskAttachment) => {
    const current = attachment.versions.find((v) => v.id === attachment.currentVersionId);
    if (!current) return '';
    if (isBoardroomFile(current.storageId)) return '';
    const versionCount = attachment.versions.length;
    const size =
      current.sizeBytes < 1024
        ? `${current.sizeBytes} B`
        : current.sizeBytes < 1024 * 1024
          ? `${(current.sizeBytes / 1024).toFixed(1)} KB`
          : `${(current.sizeBytes / (1024 * 1024)).toFixed(1)} MB`;
    const author = current.uploadedById
      ? employees.find((e) => e.id === current.uploadedById)?.name ?? 'Unknown'
      : 'Unknown';
    return `v${current.version}${versionCount > 1 ? ` · ${versionCount} versions` : ''} · ${size} · ${author}`;
  };

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>Attachments</h2>
            <p className={styles.subtitle}>{liveTask.title}</p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>

        <div className={styles.body}>
          {error && <p className={styles.error}>{error}</p>}

          {duplicateName && pending && (
            <div className={styles.duplicateBox}>
              <p>
                <strong>{duplicateName}</strong> already exists on this row.
              </p>
              <p className={styles.duplicateHint}>Save as a new version or replace the current file?</p>
              <div className={styles.duplicateActions}>
                <button
                  type="button"
                  className={styles.primaryBtn}
                  disabled={busy}
                  onClick={() => commitUpload(pending.file, 'newVersion')}
                >
                  New version
                </button>
                <button
                  type="button"
                  className={styles.secondaryBtn}
                  disabled={busy}
                  onClick={() => commitUpload(pending.file, 'replace')}
                >
                  Replace file
                </button>
                <button
                  type="button"
                  className={styles.ghostBtn}
                  disabled={busy}
                  onClick={() => {
                    setPending(null);
                    setDuplicateName(null);
                  }}
                >
                  Cancel
                </button>
              </div>
            </div>
          )}

          {confirmClearExport && (
            <div className={styles.confirmClearBox}>
              <p>
                <strong>Are you sure you want to clear exports?</strong>
              </p>
              <p className={styles.duplicateHint}>
                This removes nested assemblies and package report attachments from this Spooling task.
                Manual uploads are kept.
              </p>
              <div className={styles.duplicateActions}>
                <button
                  type="button"
                  className={styles.dangerBtn}
                  disabled={busy}
                  onClick={handleClearSsv3Export}
                >
                  Yes, clear exports
                </button>
                <button
                  type="button"
                  className={styles.ghostBtn}
                  disabled={busy}
                  onClick={() => setConfirmClearExport(false)}
                >
                  Cancel
                </button>
              </div>
            </div>
          )}

          <input
            ref={fileInputRef}
            type="file"
            className={styles.hiddenInput}
            onChange={(e) => {
              void handleFilePick(e.target.files?.[0] ?? null);
              e.target.value = '';
            }}
          />

          {allowMutate ? (
            <button
              type="button"
              className={styles.uploadBtn}
              disabled={busy || Boolean(pending) || confirmClearExport}
              onClick={() => fileInputRef.current?.click()}
            >
              + Upload file
            </button>
          ) : null}

          {canClearSsv3Export && !confirmClearExport && (
            <button
              type="button"
              className={styles.clearSsv3Btn}
              disabled={busy || Boolean(pending)}
              onClick={() => {
                setError(null);
                setConfirmClearExport(true);
              }}
            >
              Clear SSv3 export
            </button>
          )}

          {attachments.length === 0 ? (
            <p className={styles.empty}>
              {allowMutate
                ? 'No attachments yet. PDFs and other file types are supported.'
                : 'No attachments on this task.'}
            </p>
          ) : (
            <ul className={styles.list}>
              {attachments.map((attachment) => {
                const current = attachment.versions.find((v) => v.id === attachment.currentVersionId);
                const boardroom = isBoardroomFile(current?.storageId);
                return (
                  <li key={attachment.id} className={styles.item}>
                    <div className={styles.itemMain}>
                      <button
                        type="button"
                        className={styles.fileNameBtn}
                        title={boardroom ? 'Open file' : attachment.fileName}
                        onClick={() => void openOrDownload(attachment)}
                      >
                        {attachment.fileName}
                      </button>
                      {formatMeta(attachment) ? (
                        <span className={styles.meta}>{formatMeta(attachment)}</span>
                      ) : null}
                    </div>
                    <div className={styles.itemActions}>
                      <button
                        type="button"
                        className={styles.linkBtn}
                        onClick={() => void openOrDownload(attachment)}
                      >
                        {boardroom ? 'Open' : 'Download'}
                      </button>
                      {allowMutate ? (
                        <button
                          type="button"
                          className={styles.removeBtn}
                          onClick={() => void handleRemove(attachment.id)}
                        >
                          Remove
                        </button>
                      ) : null}
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      </div>
    </div>,
    document.body
  );
}
