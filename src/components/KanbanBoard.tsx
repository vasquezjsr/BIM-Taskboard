import {

  DndContext,

  DragOverlay,

  PointerSensor,

  useSensor,

  useSensors,

  closestCorners,

  type DragEndEvent,

  type DragStartEvent,

} from '@dnd-kit/core';

import { useMemo, useState } from 'react';

import { useStore } from '../store/useStore';

import { type ProjectBoardType, type Task, type TaskStatus } from '../types';

import { getBoardTaskStatuses } from '../utils/taskStatuses';
import { taskBelongsToGhostBoard } from '../utils/groupRows';

import { KanbanColumn } from './KanbanColumn';

import { TaskCard } from './TaskCard';

import styles from './KanbanBoard.module.css';



interface KanbanBoardProps {

  clientId: string;

  projectId: string;

  boardType: ProjectBoardType;

}



export function KanbanBoard({ clientId, projectId, boardType }: KanbanBoardProps) {

  const tasks = useStore((s) => s.tasks);
  const taskGroups = useStore((s) => s.taskGroups);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);

  const moveTask = useStore((s) => s.moveTask);

  const [activeTask, setActiveTask] = useState<Task | null>(null);

  const boardStatuses = useMemo(
    () => getBoardTaskStatuses(boardType, boardTaskStatuses, projectId, projectBoardTaskStatuses),
    [boardType, boardTaskStatuses, projectId, projectBoardTaskStatuses]
  );



  const boardTasks = useMemo(() => {
    const projectTasks = tasks.filter(
      (t) => t.clientId === clientId && t.projectId === projectId
    );
    return projectTasks.filter((t) =>
      taskBelongsToGhostBoard(t, boardType, taskGroups, clientId, projectId, projectTasks)
    );
  }, [tasks, clientId, projectId, boardType, taskGroups]);



  const sensors = useSensors(

    useSensor(PointerSensor, { activationConstraint: { distance: 5 } })

  );



  const handleDragStart = (event: DragStartEvent) => {

    const task = tasks.find((t) => t.id === event.active.id);

    if (task) setActiveTask(task);

  };



  const handleDragEnd = (event: DragEndEvent) => {

    setActiveTask(null);

    const { active, over } = event;

    if (!over) return;



    const taskId = active.id as string;

    const overId = over.id as string;



    if (boardStatuses.some((s) => s.id === overId)) {

      moveTask(taskId, { status: overId as TaskStatus });

    } else {

      const overTask = tasks.find((t) => t.id === overId);

      if (overTask) {

        moveTask(taskId, { status: overTask.status });

      }

    }

  };



  return (

    <DndContext

      sensors={sensors}

      collisionDetection={closestCorners}

      onDragStart={handleDragStart}

      onDragEnd={handleDragEnd}

    >

      <div className={styles.board}>

        {boardStatuses.map((status) => (

          <KanbanColumn

            key={status.id}

            status={status.id}

            label={status.label}

            color={status.color}

            tasks={boardTasks.filter((t) => t.status === status.id)}

          />

        ))}

      </div>

      <DragOverlay>

        {activeTask ? (

          <TaskCard task={activeTask} statusColor={boardStatuses.find((s) => s.id === activeTask.status)?.color} isDragging />

        ) : null}

      </DragOverlay>

    </DndContext>

  );

}


