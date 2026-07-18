import { useMemo, useState } from 'react';
import { useStore } from '../store/useStore';
import type { Task } from '../types';
import { AttachmentDialog } from './AttachmentDialog';
import { CommentDialog } from './CommentDialog';
import styles from './FabWorkstationView.module.css';

interface PackageCollabBarProps {
  packageTask: Task;
  /** When false, photos/comments open read-only (no add/remove). */
  allowEdit?: boolean;
}

/** Photos + comments on a Fab package — available throughout the workflow. */
export function PackageCollabBar({ packageTask, allowEdit = true }: PackageCollabBarProps) {
  const taskAttachments = useStore((s) => s.taskAttachments);
  const taskComments = useStore((s) => s.taskComments);
  const [showPhotos, setShowPhotos] = useState(false);
  const [showComments, setShowComments] = useState(false);

  const photoCount = useMemo(
    () =>
      taskAttachments.filter(
        (attachment) =>
          attachment.taskId === packageTask.id &&
          attachment.versions.some((version) =>
            (version.mimeType || '').startsWith('image/')
          )
      ).length,
    [taskAttachments, packageTask.id]
  );

  const commentCount = useMemo(
    () => taskComments.filter((comment) => comment.taskId === packageTask.id).length,
    [taskComments, packageTask.id]
  );

  return (
    <>
      <div className={styles.collabBar}>
        <button
          type="button"
          className={styles.collabBtn}
          onClick={() => setShowPhotos(true)}
        >
          Photos{photoCount > 0 ? ` (${photoCount})` : ''}
        </button>
        <button
          type="button"
          className={styles.collabBtn}
          onClick={() => setShowComments(true)}
        >
          Comments{commentCount > 0 ? ` (${commentCount})` : ''}
        </button>
      </div>
      {showPhotos ? (
        <AttachmentDialog
          task={packageTask}
          onClose={() => setShowPhotos(false)}
          allowMutate={allowEdit}
        />
      ) : null}
      {showComments ? (
        <CommentDialog
          task={packageTask}
          onClose={() => setShowComments(false)}
          allowMutate={allowEdit}
        />
      ) : null}
    </>
  );
}
