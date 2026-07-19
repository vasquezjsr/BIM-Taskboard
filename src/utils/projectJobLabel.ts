import type { Client, Project } from '../types';

/** Spooling Dashboard project column / sort key — project name only. */
export function projectJobLabel(
  projectId: string | null | undefined,
  projects: Project[],
  _clients?: Client[]
): string {
  if (!projectId) return '—';
  const project = projects.find((entry) => entry.id === projectId);
  if (!project) return 'Unknown project';
  return project.name;
}
