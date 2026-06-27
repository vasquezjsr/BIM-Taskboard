import type { TaskComment } from '../types';

export type TaskCommentReadState = 'none' | 'unread' | 'read';

export function getTaskCommentReadState(
  taskId: string,
  taskComments: TaskComment[],
  taskCommentReadAt: Record<string, string>
): TaskCommentReadState {
  const comments = taskComments.filter((comment) => comment.taskId === taskId);
  if (comments.length === 0) return 'none';

  const readAt = taskCommentReadAt[taskId];
  if (!readAt) return 'unread';

  return comments.some((comment) => comment.createdAt > readAt) ? 'unread' : 'read';
}

export function buildTaskCommentReadStateMap(
  taskComments: TaskComment[],
  taskCommentReadAt: Record<string, string>
): Map<string, TaskCommentReadState> {
  const taskIds = new Set(taskComments.map((comment) => comment.taskId));
  const map = new Map<string, TaskCommentReadState>();

  for (const taskId of taskIds) {
    map.set(taskId, getTaskCommentReadState(taskId, taskComments, taskCommentReadAt));
  }

  return map;
}
