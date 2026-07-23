import { useDroppable } from '@dnd-kit/core';
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable';
import type { Task } from '../types';
import { TaskCard } from './TaskCard';
import styles from './EmployeeColumn.module.css';

interface EmployeeColumnProps {
  id: string;
  title: string;
  subtitle: string;
  tasks: Task[];
  isPool?: boolean;
  variant: 'detailer' | 'support';
  draggable?: boolean;
}

export function EmployeeColumn({
  id,
  title,
  subtitle,
  tasks,
  isPool = false,
  variant,
  draggable = true,
}: EmployeeColumnProps) {
  const { setNodeRef, isOver } = useDroppable({ id });

  const variantClass = variant === 'detailer' ? styles.detailer : styles.support;

  return (
    <div className={`${styles.column} ${variantClass} ${isPool ? styles.pool : ''}`}>
      <div className={styles.header}>
        <div>
          <h3 className={styles.title}>{title}</h3>
          <span className={styles.subtitle}>{subtitle}</span>
        </div>
        <span className={styles.count}>{tasks.length}</span>
      </div>
      <div
        ref={setNodeRef}
        className={`${styles.body} ${isOver ? styles.over : ''}`}
      >
        <SortableContext items={tasks.map((t) => t.id)} strategy={verticalListSortingStrategy}>
          {tasks.map((task) => (
            <div key={task.id} className={styles.taskWrapper}>
              <TaskCard
                task={task}
                hideAssignee={!isPool}
                hideDescription
                draggable={draggable}
              />
            </div>
          ))}
        </SortableContext>
        {tasks.length === 0 && (
          <div className={styles.placeholder}>
            {isPool ? 'All tasks assigned' : 'Drop tasks here'}
          </div>
        )}
      </div>
    </div>
  );
}
