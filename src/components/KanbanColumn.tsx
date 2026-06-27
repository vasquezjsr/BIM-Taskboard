import { useDroppable } from '@dnd-kit/core';

import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable';

import type { Task, TaskStatus } from '../types';

import { TaskCard } from './TaskCard';

import styles from './KanbanColumn.module.css';



interface KanbanColumnProps {

  status: TaskStatus;

  label: string;

  color: string;

  tasks: Task[];

}



export function KanbanColumn({ status, label, color, tasks }: KanbanColumnProps) {

  const { setNodeRef, isOver } = useDroppable({ id: status });



  return (

    <div className={styles.column}>

      <div className={styles.header} style={{ borderTopColor: color }}>

        <span className={styles.label}>{label}</span>

        <span className={styles.count}>{tasks.length}</span>

      </div>

      <div

        ref={setNodeRef}

        className={`${styles.body} ${isOver ? styles.over : ''}`}

      >

        <SortableContext items={tasks.map((t) => t.id)} strategy={verticalListSortingStrategy}>

          {tasks.map((task) => (

            <TaskCard key={task.id} task={task} statusColor={color} />

          ))}

        </SortableContext>

        {tasks.length === 0 && (

          <div className={styles.placeholder}>Drop tasks here</div>

        )}

      </div>

    </div>

  );

}


