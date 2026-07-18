import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { useStore } from '../store/useStore';
import type { Task } from '../types';
import styles from './CommentDialog.module.css';

interface CommentDialogProps {
  task: Task;
  onClose: () => void;
  allowMutate?: boolean;
}

export function CommentDialog({ task, onClose, allowMutate = true }: CommentDialogProps) {
  const employees = useStore((s) => s.employees);
  const currentUserId = useStore((s) => s.currentUserId);
  const taskComments = useStore((s) => s.taskComments);
  const addTaskComment = useStore((s) => s.addTaskComment);
  const removeTaskComment = useStore((s) => s.removeTaskComment);
  const markTaskCommentsRead = useStore((s) => s.markTaskCommentsRead);

  const [body, setBody] = useState('');
  const [authorId, setAuthorId] = useState(currentUserId ?? '');

  const comments = useMemo(
    () =>
      taskComments
        .filter((c) => c.taskId === task.id)
        .sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
    [taskComments, task.id]
  );

  useEffect(() => {
    markTaskCommentsRead(task.id);
  }, [markTaskCommentsRead, task.id]);

  useEffect(() => {
    if (currentUserId) setAuthorId(currentUserId);
  }, [currentUserId]);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  const authorName = (id: string | null) =>
    id ? employees.find((e) => e.id === id)?.name ?? 'Unknown' : 'Unknown';

  const formatWhen = (iso: string) => {
    try {
      return new Date(iso).toLocaleString(undefined, {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
      });
    } catch {
      return iso;
    }
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!allowMutate) return;
    const trimmed = body.trim();
    if (!trimmed) return;
    addTaskComment(task.id, authorId || null, trimmed);
    markTaskCommentsRead(task.id);
    setBody('');
  };

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <div>
            <h2>Comments</h2>
            <p className={styles.subtitle}>{task.title}</p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>

        <div className={styles.body}>
          <div className={styles.thread}>
            {comments.length === 0 ? (
              <p className={styles.empty}>No comments yet. Be the first to add one.</p>
            ) : (
              comments.map((comment) => (
                <div key={comment.id} className={styles.comment}>
                  <div className={styles.commentHeader}>
                    <div className={styles.commentMeta}>
                      <span className={styles.author}>{authorName(comment.authorId)}</span>
                      <span className={styles.when}>{formatWhen(comment.createdAt)}</span>
                    </div>
                    {allowMutate ? (
                      <button
                        type="button"
                        className={styles.deleteBtn}
                        onClick={() => removeTaskComment(comment.id)}
                        title="Delete comment"
                      >
                        Delete
                      </button>
                    ) : null}
                  </div>
                  <p className={styles.commentBody}>{comment.body}</p>
                </div>
              ))
            )}
          </div>

          {allowMutate ? (
            <form className={styles.compose} onSubmit={handleSubmit}>
              <label className={styles.authorField}>
                <span>Your name</span>
                <select value={authorId} onChange={(e) => setAuthorId(e.target.value)}>
                  <option value="">Select employee…</option>
                  {employees.map((emp) => (
                    <option key={emp.id} value={emp.id}>
                      {emp.name}
                    </option>
                  ))}
                </select>
              </label>
              <label className={styles.commentField}>
                <span>Add a comment</span>
                <textarea
                  value={body}
                  onChange={(e) => setBody(e.target.value)}
                  rows={4}
                  placeholder="Type your comment…"
                />
              </label>
              <button type="submit" className={styles.submitBtn} disabled={!body.trim()}>
                Post comment
              </button>
            </form>
          ) : null}
        </div>
      </div>
    </div>,
    document.body
  );
}
