import type { Employee, Project, ProjectBoardType } from '../types';

const SUPPORT_BOARDS: ProjectBoardType[] = ['deliverables', 'documents', 'rfi'];

export function getAssignableEmployees(
  employees: Employee[],
  project: Project | null | undefined,
  boardType: ProjectBoardType
): Employee[] {
  if (!project) return employees;

  const detailers = employees.filter((e) => e.role === 'detailer');
  const support = employees.filter((e) => e.role === 'support-specialist');

  if (boardType === 'detailers') {
    return project.detailerIds.length
      ? detailers.filter((e) => project.detailerIds.includes(e.id))
      : detailers;
  }

  if (SUPPORT_BOARDS.includes(boardType)) {
    return project.supportIds.length
      ? support.filter((e) => project.supportIds.includes(e.id))
      : support;
  }

  const teamIds = new Set([...project.detailerIds, ...project.supportIds]);
  if (teamIds.size > 0) {
    return employees.filter((e) => teamIds.has(e.id));
  }

  return employees;
}
