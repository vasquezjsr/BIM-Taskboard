import type { Project } from '../types';
import { TEMPLATE_PROJECT_NAME } from './vdcSeedData';
import { isTemplateProject } from '../utils/projectTemplate';
import { SUPPORT_ASSIGNEE_IDS } from './employees';

export const DETAILER_EMPLOYEE_IDS = {
  d1: 'emp-detailer-1',
  d2: 'emp-detailer-2',
  d3: 'emp-detailer-3',
  d4: 'emp-detailer-4',
  d5: 'emp-detailer-5',
  d6: 'emp-detailer-6',
  d7: 'emp-detailer-7',
  d8: 'emp-detailer-8',
} as const;

const DETAILERS_BY_PROJECT_NAME: Record<string, string[]> = {};

export function defaultDetailerIdsForProject(projectName: string): string[] {
  return DETAILERS_BY_PROJECT_NAME[projectName] ?? [];
}

export function defaultSupportIdsForProject(): string[] {
  return [...SUPPORT_ASSIGNEE_IDS];
}

const REVIT_YEAR_BY_PROJECT_NAME: Record<string, string> = {};

export function defaultRevitYearForProject(projectName: string): string | null {
  return REVIT_YEAR_BY_PROJECT_NAME[projectName] ?? null;
}

export function defaultModelTypeForProject(projectName: string): Project['modelType'] {
  return defaultRevitYearForProject(projectName) ? 'cloud' : null;
}

function applyProjectDefaults(project: Project): Project {
  if (isTemplateProject(project)) {
    return { ...project, isTemplate: true, name: TEMPLATE_PROJECT_NAME };
  }

  const revitYear = defaultRevitYearForProject(project.name);
  return {
    ...project,
    detailerIds: defaultDetailerIdsForProject(project.name),
    supportIds: defaultSupportIdsForProject(),
    ...(revitYear ? { revitYear, modelType: 'cloud' as const } : {}),
  };
}

/** Apply roster detailers, support, Revit year, and cloud model per project. */
export function applyDefaultProjectTeams(projects: Project[]): Project[] {
  return projects.map(applyProjectDefaults);
}

/** Fill in defaults when a project has none assigned (safe on every load). */
export function ensureDefaultProjectTeams(projects: Project[]): Project[] {
  return projects.map((project) => {
    if (isTemplateProject(project)) {
      return { ...project, isTemplate: true, name: TEMPLATE_PROJECT_NAME };
    }

    const defaultDetailers = defaultDetailerIdsForProject(project.name);
    const defaultRevitYear = defaultRevitYearForProject(project.name);
    const defaultModelType = defaultModelTypeForProject(project.name);
    return {
      ...project,
      detailerIds:
        project.detailerIds.length === 0 && defaultDetailers.length > 0
          ? defaultDetailers
          : project.detailerIds,
      supportIds:
        project.supportIds.length === 0 ? defaultSupportIdsForProject() : project.supportIds,
      revitYear: project.revitYear ?? defaultRevitYear,
      modelType: project.modelType ?? defaultModelType,
    };
  });
}
