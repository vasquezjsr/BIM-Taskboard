import type { Project, Task } from '../types';

export function formatTaskNumber(project: Project, sequence: number): string {
  const code = project.jobCode?.trim() || 'TASK';
  return `${code}-${String(sequence).padStart(4, '0')}`;
}

export function nextTaskNumberForProject(project: Project): { taskNumber: string; nextTaskNumber: number } {
  const sequence = project.nextTaskNumber ?? 1;
  return {
    taskNumber: formatTaskNumber(project, sequence),
    nextTaskNumber: sequence + 1,
  };
}

export function backfillTaskNumbers(
  tasks: Task[],
  projects: Project[]
): { tasks: Task[]; projects: Project[] } {
  const projectById = new Map(projects.map((project) => [project.id, { ...project }]));
  const counters = new Map<string, number>();

  for (const project of projectById.values()) {
    counters.set(project.id, project.nextTaskNumber ?? 1);
  }

  const nextTasks = tasks.map((task) => {
    if (task.taskNumber || !task.projectId) return task;
    const project = projectById.get(task.projectId);
    if (!project) return task;

    const sequence = counters.get(task.projectId) ?? 1;
    counters.set(task.projectId, sequence + 1);
    project.nextTaskNumber = sequence + 1;
    return { ...task, taskNumber: formatTaskNumber(project, sequence) };
  });

  return {
    tasks: nextTasks,
    projects: [...projectById.values()],
  };
}
