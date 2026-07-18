import type { Project, Task } from '../types';
import { isSsv3PackageTask } from '../utils/boardroomPackageImport';
import {
  getPackageDeptLeadId,
  getPackageWorkerId,
} from '../utils/fabWorkstationAccess';
import { taskHasAssignee } from '../utils/taskAssignees';
import styles from './TimeTrackingQuickTasks.module.css';

const COMPLETE_STATUSES = new Set(['complete', 'material-pulled', 'shipped']);

export interface TaskClientProject {
  clientId: string;
  projectId: string;
}

export function resolveTaskClientProject(
  task: Task,
  projects: Project[]
): TaskClientProject | null {
  if (!task.projectId) return null;

  const project = projects.find((entry) => entry.id === task.projectId);
  if (!project) return null;

  return {
    clientId: task.clientId ?? project.clientId,
    projectId: project.id,
  };
}

function isEmployeeOnProjectTeam(project: Project, employeeId: string): boolean {
  return project.detailerIds.includes(employeeId) || project.supportIds.includes(employeeId);
}

function isFabPackageAssignedToEmployee(task: Task, employeeId: string): boolean {
  if (!isSsv3PackageTask(task)) return false;
  if (getPackageDeptLeadId(task) === employeeId) return true;
  if (getPackageWorkerId(task) === employeeId) return true;
  if (taskHasAssignee(task, employeeId)) return true;
  return false;
}

/** Tasks an employee can log time against on a project team. */
export function taskMatchesEmployeeWork(
  task: Task,
  project: Project,
  employeeId: string
): boolean {
  if (isFabPackageAssignedToEmployee(task, employeeId)) return true;
  if (taskHasAssignee(task, employeeId)) return true;
  if (!isEmployeeOnProjectTeam(project, employeeId)) return false;

  if (project.detailerIds.includes(employeeId) && task.boardType === 'detailers') {
    return true;
  }

  if (
    project.supportIds.includes(employeeId) &&
    (task.boardType === 'deliverables' || task.boardType === 'project-managers')
  ) {
    return true;
  }

  return false;
}

export function getQuickLogTasksForEmployee(
  tasks: Task[],
  projects: Project[],
  employeeId: string
): Task[] {
  if (!employeeId) return [];

  return tasks
    .filter((task) => {
      if (COMPLETE_STATUSES.has(task.status)) return false;
      const resolved = resolveTaskClientProject(task, projects);
      if (!resolved) return false;
      const project = projects.find((entry) => entry.id === resolved.projectId);
      if (!project) return false;
      return taskMatchesEmployeeWork(task, project, employeeId);
    })
    .sort((a, b) => {
      const aPackage = isSsv3PackageTask(a) ? 0 : 1;
      const bPackage = isSsv3PackageTask(b) ? 0 : 1;
      if (aPackage !== bPackage) return aPackage - bPackage;
      const aInProgress = a.status === 'in-progress' || a.status === 'pulling-material' ? 0 : 1;
      const bInProgress = b.status === 'in-progress' || b.status === 'pulling-material' ? 0 : 1;
      if (aInProgress !== bInProgress) return aInProgress - bInProgress;
      return a.title.localeCompare(b.title, undefined, { sensitivity: 'base' });
    });
}

interface TimeTrackingQuickTasksProps {
  tasks: Task[];
  projects: Project[];
  employeeId: string;
  employeeName?: string;
  isSelf: boolean;
  selectedTaskId: string | null;
  onSelectTask: (task: Task) => void;
}

export function TimeTrackingQuickTasks({
  tasks,
  projects,
  employeeId,
  employeeName,
  isSelf,
  selectedTaskId,
  onSelectTask,
}: TimeTrackingQuickTasksProps) {
  const quickTasks = getQuickLogTasksForEmployee(tasks, projects, employeeId);
  const sectionLabel = isSelf ? 'My tasks' : `${employeeName ?? 'Employee'}'s tasks`;

  if (quickTasks.length === 0) {
    return (
      <div className={styles.wrapper}>
        <span className={styles.label}>{sectionLabel}</span>
        <div className={styles.taskWindow}>
          <p className={styles.empty}>
            {isSelf
              ? 'Assigned project tasks you can log time against will show up here.'
              : 'No open assigned project tasks for this employee.'}
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.wrapper}>
      <span className={styles.label}>{sectionLabel}</span>
      <div className={styles.taskWindow}>
        <div className={styles.taskWindowHeader}>
          <span className={styles.taskWindowTitle}>Assigned tasks</span>
          <span className={styles.taskWindowCount}>{quickTasks.length}</span>
        </div>
        <div className={styles.taskList}>
          {quickTasks.map((task) => {
            const project = projects.find((entry) => entry.id === task.projectId);
            const isSelected = selectedTaskId === task.id;

            return (
              <button
                key={task.id}
                type="button"
                className={`${styles.taskBtn} ${isSelected ? styles.taskBtnSelected : ''}`}
                onClick={() => onSelectTask(task)}
              >
                <span className={styles.taskTitle}>{task.title}</span>
                {project && <span className={styles.taskMeta}>{project.name}</span>}
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
