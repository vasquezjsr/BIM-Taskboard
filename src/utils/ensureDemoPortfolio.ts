import type { Project, Task } from '../types';
import type { NewProjectOptions } from './projectTemplate';

const DEMO_CLIENT_NAME = 'Demo Mechanical';

const DEMO_PROJECTS = [
  { name: 'Office Tower Phase 1', jobCode: '25-1001' },
  { name: 'Hospital Wing B', jobCode: '25-1002' },
  { name: 'Campus Utility Upgrade', jobCode: '25-1003' },
] as const;

type DemoStoreSlice = {
  clients: { id: string; name: string }[];
  projects: Project[];
  tasks: Task[];
  addClient: (name: string) => void;
  addProject: (clientId: string, name: string, options?: NewProjectOptions) => void;
  updateProjectSettings: (projectId: string, updates: Partial<Project>) => void;
  addTask: (task: Omit<Task, 'id' | 'createdAt'>) => void;
  setActiveClientId: (id: string | null) => void;
};

/** If the portfolio has no real (non-template) projects, seed Demo Mechanical once. */
export function ensureDemoPortfolio(store: {
  getState: () => DemoStoreSlice;
}): { seeded: boolean; reason: string } {
  const state = store.getState();
  const realProjects = state.projects.filter((project) => !project.isTemplate);
  const demoClient = state.clients.find((client) => client.name === DEMO_CLIENT_NAME);

  // Real work already exists — never wipe or reseed over it.
  if (realProjects.length > 0) {
    return { seeded: false, reason: demoClient ? 'demo-already-present' : 'other-real-projects-present' };
  }

  // No real projects — restore Demo Mechanical via store actions (persists normally).
  if (!demoClient) {
    state.addClient(DEMO_CLIENT_NAME);
  }
  const client = store.getState().clients.find((entry) => entry.name === DEMO_CLIENT_NAME);
  if (!client) {
    return { seeded: false, reason: 'add-client-failed' };
  }

  for (const def of DEMO_PROJECTS) {
    const existing = store
      .getState()
      .projects.find((project) => project.clientId === client.id && project.name === def.name);
    if (!existing) {
      store.getState().addProject(client.id, def.name);
    }
    const project = store
      .getState()
      .projects.find((entry) => entry.clientId === client.id && entry.name === def.name);
    if (project && project.jobCode !== def.jobCode) {
      store.getState().updateProjectSettings(project.id, { jobCode: def.jobCode });
    }
  }

  const campus = store
    .getState()
    .projects.find((project) => project.clientId === client.id && project.name === 'Campus Utility Upgrade');
  if (campus) {
    const hasTp007 = store
      .getState()
      .tasks.some(
        (task) =>
          task.projectId === campus.id &&
          (task.boardType === 'spooling' || task.boardType === 'fab') &&
          (task.title || '').includes('TP007') &&
          !task.parentTaskId
      );
    if (!hasTp007) {
      store.getState().addTask({
        title: 'TP007 - Mechanical Pipe',
        description: 'SSv3 export target for package TP007',
        status: 'not-started',
        assigneeIds: [],
        clientId: client.id,
        projectId: campus.id,
        boardType: 'spooling',
        groupId: null,
        parentTaskId: null,
        priority: 0,
        dueDate: null,
        customFields: {},
      });
    }
    store.getState().setActiveClientId(client.id);
  }

  return { seeded: true, reason: 'seeded-empty-portfolio' };
}
