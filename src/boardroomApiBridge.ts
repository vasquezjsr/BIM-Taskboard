import { useStore } from './store/useStore';
import { spoolingTaskHasSsv3Export } from './utils/boardroomPackageImport';

export type BoardroomApiSnapshot = {
  clients: { id: string; name: string }[];
  projects: {
    id: string;
    name: string;
    jobCode: string;
    clientId: string;
    clientName: string;
    isTemplate: boolean;
    billingType: string;
    revitYear: string;
  }[];
  tasks: {
    id: string;
    taskNumber: string | null;
    title: string;
    status: string;
    projectId: string | null;
    boardType: string | null;
    groupId: string | null;
    parentTaskId: string | null;
    assigneeIds: string[];
    dueDate: string | null;
    customFields: Record<string, string>;
    hasSsv3Export: boolean;
    createdAt: string;
  }[];
  publishedAt: string;
};

const DEBOUNCE_MS = 400;

function buildSnapshot(): BoardroomApiSnapshot {
  const { clients, projects, tasks } = useStore.getState();
  const clientNameById = new Map(clients.map((c) => [c.id, c.name]));

  return {
    publishedAt: new Date().toISOString(),
    clients: clients.map((c) => ({ id: c.id, name: c.name })),
    projects: projects.map((project) => ({
      id: project.id,
      name: project.name,
      jobCode: project.jobCode ?? '',
      clientId: project.clientId,
      clientName: clientNameById.get(project.clientId) ?? '',
      isTemplate: Boolean(project.isTemplate),
      billingType: project.billingType ?? '',
      revitYear: project.revitYear ?? '',
    })),
    tasks: tasks.map((task) => ({
      id: task.id,
      taskNumber: task.taskNumber ?? null,
      title: task.title,
      status: task.status,
      projectId: task.projectId,
      boardType: task.boardType,
      groupId: task.groupId,
      parentTaskId: task.parentTaskId,
      assigneeIds: task.assigneeIds ?? [],
      dueDate: task.dueDate,
      customFields: Object.fromEntries(
        Object.entries(task.customFields ?? {}).map(([key, value]) => [key, value ?? ''])
      ),
      hasSsv3Export: spoolingTaskHasSsv3Export(task),
      createdAt: task.createdAt,
    })),
  };
}

/** Keep Electron main's loopback API in sync with zustand store state. */
export function startBoardroomApiBridge(): () => void {
  const publish = window.electronAPI?.publishBoardroomApiSnapshot;
  if (!publish) {
    return () => undefined;
  }

  let timer: number | null = null;

  const flush = () => {
    timer = null;
    void publish(buildSnapshot());
  };

  const schedule = () => {
    if (timer != null) window.clearTimeout(timer);
    timer = window.setTimeout(flush, DEBOUNCE_MS);
  };

  flush();

  const unsub = useStore.subscribe((state, prev) => {
    if (
      state.clients !== prev.clients ||
      state.projects !== prev.projects ||
      state.tasks !== prev.tasks
    ) {
      schedule();
    }
  });

  const onHydrated = () => {
    flush();
  };
  const persistApi = useStore.persist;
  persistApi.onFinishHydration(onHydrated);
  if (persistApi.hasHydrated()) {
    flush();
  }

  return () => {
    unsub();
    if (timer != null) window.clearTimeout(timer);
  };
}
