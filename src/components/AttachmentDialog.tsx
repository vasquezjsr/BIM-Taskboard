import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { v4 as uuid } from 'uuid';
import { useStore } from '../store/useStore';
import type { Task, TaskAttachment } from '../types';
import { deleteFileBlob, getFileBlob, saveFileBlob } from '../utils/fileStorage';
import styles from './AttachmentDialog.module.css';

interface AttachmentDialogProps {
  task: Task;
  onClose: () => void;
}

type PendingUpload = {
  file: File;
  action: 'new' | 'replace' | 'newVersion';
};

export function AttachmentDialog({ task, onClose }: AttachmentDialogProps) {
  const employees = useStore((s) => s.employees);
  const taskAttachments = useStore((s) => s.taskAttachments);
  const upsertTaskAttachment = useStore((s) => s.upsertTaskAttachment);
  const removeTaskAttachment = useStore((s) => s.removeTaskAttachment);

  const fileInputRef = useRef<HTMLInputElement>(null);
  const [pending, setPending] = useState<PendingUpload | null>(null);
  const [duplicateName, setDuplicateName] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const attachments = useMemo(
    () => taskAttachments.filter((a) => a.taskId === task.id),
    [taskAttachments, task.id]
  );

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !pending) onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose, pending]);

  const findByName = (fileName: string): TaskAttachment | undefined =>
    attachments.find((a) => a.fileName.toLowerCase() === fileName.toLowerCase());

  const commitUpload = async (file: File, action: 'new' | 'replace' | 'newVersion') => {
    setBusy(true);
    setError(null);
    try {
      const storageId = uuid();
      await saveFileBlob(storageId, file);
      upsertTaskAttachment({
        taskId: task.id,
        fileName: file.name,
        mimeType: file.type || 'application/octet-stream',
        sizeBytes: file.size,
        storageId,
        uploadedById: null,
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

  const handleDownload = async (attachment: TaskAttachment) => {
    const version = attachment.versions.find((v) => v.id === attachment.currentVersionId);
    if (!version) return;
    const blob = await getFileBlob(version.storageId);
    if (!blob) return;
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
    const storageIds = removeTaskAttachment(attachmentId);
    await Promise.all(storageIds.map((id) => deleteFileBlob(id)));
  };

  const formatSize = (bytes: number) => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  const authorName = (id: string | null) =>
    id ? employees.find((e) => e.id === id)?.name ?? 'Unknown' : 'Unknown';

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>Attachments</h2>
            <p className={styles.subtitle}>{task.title}</p>
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

          <input
            ref={fileInputRef}
            type="file"
            className={styles.hiddenInput}
            onChange={(e) => {
              void handleFilePick(e.target.files?.[0] ?? null);
              e.target.value = '';
            }}
          />

          <button
            type="button"
            className={styles.uploadBtn}
            disabled={busy || Boolean(pending)}
            onClick={() => fileInputRef.current?.click()}
          >
            + Upload file
          </button>

          {attachments.length === 0 ? (
            <p className={styles.empty}>No attachments yet. PDFs and other file types are supported.</p>
          ) : (
            <ul className={styles.list}>
              {attachments.map((attachment) => {
                const current = attachment.versions.find((v) => v.id === attachment.currentVersionId);
                const versionCount = attachment.versions.length;
                return (
                  <li key={attachment.id} className={styles.item}>
                    <div className={styles.itemMain}>
                      <span className={styles.fileName}>{attachment.fileName}</span>
                      {current && (
                        <span className={styles.meta}>
                          v{current.version}
                          {versionCount > 1 ? ` · ${versionCount} versions` : ''}
                          {' · '}
                          {formatSize(current.sizeBytes)}
                          {' · '}
                          {authorName(current.uploadedById)}
                        </span>
                      )}
                    </div>
                    <div className={styles.itemActions}>
                      <button
                        type="button"
                        className={styles.linkBtn}
                        onClick={() => void handleDownload(attachment)}
                      >
                        Download
                      </button>
                      <button
                        type="button"
                        className={styles.removeBtn}
                        onClick={() => void handleRemove(attachment.id)}
                      >
                        Remove
                      </button>
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
