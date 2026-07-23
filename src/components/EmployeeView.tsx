import { useMemo, useState } from 'react';
import {
  DndContext,
  DragOverlay,
  PointerSensor,
  useSensor,
  useSensors,
  pointerWithin,
  rectIntersection,
  closestCorners,
  type CollisionDetection,
  type DragEndEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import { useStore, sortEmployeeTasks } from '../store/useStore';
import { canAssignTasks } from '../utils/permissions';
import { hasTaskAssignees, taskHasAssignee } from '../utils/taskAssignees';
import {
  isTaskVisibleOnTaskBoard,
  taskBoardVisibleStatusSet,
} from '../utils/taskBoardVisibility';
import type { Employee, EmployeeBoardTab, EmployeeRole, Task } from '../types';
import { EmployeeColumn } from './EmployeeColumn';
import { TaskBoardSettings } from './TaskBoardSettings';
import { TaskCard } from './TaskCard';
import styles from './EmployeeView.module.css';

const BOARD_TABS: { id: EmployeeBoardTab; label: string }[] = [
  { id: 'detailers', label: 'Detailers' },
  { id: 'support-specialists', label: 'Support Specialists' },
];

/** Prefer the column under the pointer so empty person columns are easy drop targets. */
const taskBoardCollision: CollisionDetection = (args) => {
  const pointerHits = pointerWithin(args);
  if (pointerHits.length > 0) return pointerHits;
  const rectHits = rectIntersection(args);
  if (rectHits.length > 0) return rectHits;
  return closestCorners(args);
};

function hasAssignee(task: Task): boolean {
  return hasTaskAssignees(task);
}

function isUnassignedDropTarget(overId: string, poolTasks: Task[]): boolean {
  if (overId === 'unassigned') return true;
  return poolTasks.some((task) => task.id === overId);
}

function taskBelongsToEmployeeColumn(
  task: Task,
  employee: Employee,
  activeRole: EmployeeRole
): boolean {
  if (!hasAssignee(task)) return false;
  if (!taskHasAssignee(task, employee.id)) return false;
  return employee.role === activeRole;
}

function tasksForEmployee(
  tasks: Task[],
  employee: Employee,
  activeRole: EmployeeRole
): Task[] {
  return tasks.filter((task) => taskBelongsToEmployeeColumn(task, employee, activeRole));
}

export function EmployeeView() {
  const employees = useStore((s) => s.employees);
  const tasks = useStore((s) => s.tasks);
  const boardTaskStatuses = useStore((s) => s.boardTaskStatuses);
  const projectBoardTaskStatuses = useStore((s) => s.projectBoardTaskStatuses);
  const taskBoardVisibleStatuses = useStore((s) => s.taskBoardVisibleStatuses);
  const activeEmployeeBoard = useStore((s) => s.activeEmployeeBoard);
  const setActiveEmployeeBoard = useStore((s) => s.setActiveEmployeeBoard);
  const moveTask = useStore((s) => s.moveTask);
  const reorderEmployeeTasks = useStore((s) => s.reorderEmployeeTasks);
  const currentUserId = useStore((s) => s.currentUserId);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const allowAssignTasks = canAssignTasks(currentUserId, employees, employeePermissions);

  const [activeTask, setActiveTask] = useState<Task | null>(null);
  const [showSettings, setShowSettings] = useState(false);

  const visibleStatusIds = useMemo(
    () => taskBoardVisibleStatusSet(taskBoardVisibleStatuses),
    [taskBoardVisibleStatuses]
  );

  const isDetailers = activeEmployeeBoard === 'detailers';
  const activeRole: EmployeeRole = isDetailers ? 'detailer' : 'support-specialist';
  const roleEmployees = useMemo(
    () => employees.filter((e) => e.role === activeRole),
    [employees, activeRole]
  );
  const roleEmployeeIds = useMemo(
    () => new Set(roleEmployees.map((employee) => employee.id)),
    [roleEmployees]
  );

  /** Unassigned work waiting to be claimed */
  const poolTasks = useMemo(
    () =>
      tasks.filter(
        (task) =>
          !hasAssignee(task) &&
          isTaskVisibleOnTaskBoard(
            task,
            visibleStatusIds,
            boardTaskStatuses,
            projectBoardTaskStatuses,
            tasks
          )
      ),
    [tasks, visibleStatusIds, boardTaskStatuses, projectBoardTaskStatuses]
  );

  /** Only tasks with a valid assignee on this team board and a visible status */
  const teamAssignedTasks = useMemo(
    () =>
      tasks.filter(
        (task) =>
          hasAssignee(task) &&
          task.assigneeIds.some((id) => roleEmployeeIds.has(id)) &&
          isTaskVisibleOnTaskBoard(
            task,
            visibleStatusIds,
            boardTaskStatuses,
            projectBoardTaskStatuses,
            tasks
          )
      ),
    [tasks, roleEmployeeIds, visibleStatusIds, boardTaskStatuses, projectBoardTaskStatuses]
  );

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 8 } })
  );

  const handleDragStart = (event: DragStartEvent) => {
    if (!allowAssignTasks) return;
    const task = tasks.find((t) => t.id === event.active.id);
    if (task) setActiveTask(task);
  };

  const assignToEmployee = (taskId: string, employee: Employee, overTaskId?: string) => {
    const columnTasks = sortEmployeeTasks(
      tasksForEmployee(teamAssignedTasks, employee, activeRole).filter((task) => task.id !== taskId)
    );
    const dragged = tasks.find((task) => task.id === taskId);
    if (dragged && overTaskId) {
      const overIndex = columnTasks.findIndex((task) => task.id === overTaskId);
      if (overIndex >= 0) columnTasks.splice(overIndex, 0, dragged);
      else columnTasks.push(dragged);
    } else if (dragged) {
      columnTasks.push(dragged);
    }

    moveTask(taskId, {
      assigneeIds: [employee.id],
      assigneesLocked: true,
      priority: Math.max(
        0,
        columnTasks.findIndex((task) => task.id === taskId)
      ),
    });
    reorderEmployeeTasks(
      employee.id,
      columnTasks.map((task) => task.id)
    );
  };

  const handleDragEnd = (event: DragEndEvent) => {
    setActiveTask(null);
    if (!allowAssignTasks) return;
    const { active, over } = event;
    if (!over) return;

    const taskId = String(active.id);
    const overId = String(over.id);
    if (taskId === overId) return;

    if (isUnassignedDropTarget(overId, poolTasks)) {
      moveTask(taskId, { assigneeIds: [], assigneesLocked: true });
      return;
    }

    const targetEmployee = roleEmployees.find((employee) => employee.id === overId);
    if (targetEmployee) {
      assignToEmployee(taskId, targetEmployee);
      return;
    }

    const overTask = tasks.find((task) => task.id === overId);
    if (!overTask || !hasAssignee(overTask)) return;

    const assignee = roleEmployees.find((employee) => taskHasAssignee(overTask, employee.id));
    if (!assignee) return;
    assignToEmployee(taskId, assignee, overId);
  };

  const variant = isDetailers ? 'detailer' : 'support';
  const boardClass = isDetailers ? styles.detailersBoard : styles.supportBoard;

  return (
    <div className={styles.container}>
      {/* Level 2 — Employee board tabs */}
      <div className={`${styles.tabRow} ${isDetailers ? styles.detailersTabRow : styles.supportTabRow}`}>
        <span className={styles.tabLabel}>Team</span>
        <div className={styles.tabs}>
          {BOARD_TABS.map((tab) => (
            <button
              key={tab.id}
              className={`${styles.boardTab} ${activeEmployeeBoard === tab.id ? styles.active : ''} ${
                tab.id === 'detailers' ? styles.detailerTab : styles.supportTab
              }`}
              onClick={() => setActiveEmployeeBoard(tab.id)}
            >
              {tab.label}
            </button>
          ))}
        </div>
        <button
          type="button"
          className={`${styles.settingsBtn} ${isDetailers ? styles.detailerSettingsBtn : styles.supportSettingsBtn}`}
          onClick={() => setShowSettings(true)}
          title="Task board settings"
        >
          Settings
        </button>
      </div>

      {showSettings && <TaskBoardSettings onClose={() => setShowSettings(false)} />}

      <DndContext
        sensors={sensors}
        collisionDetection={taskBoardCollision}
        onDragStart={handleDragStart}
        onDragEnd={handleDragEnd}
        onDragCancel={() => setActiveTask(null)}
      >
        <div className={`${styles.board} ${boardClass}`}>
          <EmployeeColumn
            id="unassigned"
            title="Unassigned"
            subtitle="Visible statuses — drag to assign"
            tasks={poolTasks}
            isPool
            variant={variant}
            draggable={allowAssignTasks}
          />

          {roleEmployees.map((emp) => (
            <EmployeeColumn
              key={emp.id}
              id={emp.id}
              title={emp.name}
              subtitle={isDetailers ? 'Detailer' : 'Support Specialist'}
              tasks={sortEmployeeTasks(tasksForEmployee(teamAssignedTasks, emp, activeRole))}
              variant={variant}
              draggable={allowAssignTasks}
            />
          ))}

          {roleEmployees.length === 0 && (
            <div className={styles.empty}>
              <p>No {isDetailers ? 'detailers' : 'support specialists'} yet.</p>
              <p className={styles.emptyHint}>
                Click "+ {isDetailers ? 'Detailer' : 'Support Specialist'}" to add one.
              </p>
            </div>
          )}
        </div>
        <DragOverlay dropAnimation={null}>
          {activeTask ? <TaskCard task={activeTask} isDragging hideDescription /> : null}
        </DragOverlay>
      </DndContext>
    </div>
  );
}
