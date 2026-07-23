import { useMemo, useState, useCallback, useEffect } from 'react';
import {
  DndContext,
  PointerSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core';
import {
  SortableContext,
  arrayMove,
  horizontalListSortingStrategy,
  useSortable,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useStore } from '../store/useStore';
import { canEditClientsProjects } from '../utils/permissions';
import {
  PROJECT_BOARD_TYPES,
  getBoardLabel,
  getProjectSubBoardOrder,
  type ProjectBoardType,
} from '../types';
import { TaskSpreadsheet } from './TaskSpreadsheet';
import { MainDashboard } from './MainDashboard';
import { ProjectMetaPanel } from './ProjectMetaPanel';
import { AddProjectDialog, type AddProjectDialogResult } from './AddProjectDialog';
import { ImportMondayDialog, type ImportMondayDialogResult } from './ImportMondayDialog';
import { ContextMenuPanel } from './ContextMenuPanel';
import styles from './ClientView.module.css';
import { registerBoardTabElement } from '../utils/boardTabDropRegistry';

interface SortableBoardTabProps {
  id: ProjectBoardType;
  label: string;
  isActive: boolean;
  onSelect: () => void;
}

function SortableBoardTab({ id, label, isActive, onSelect }: SortableBoardTabProps) {
  const sheetDragActive = useStore((s) => s.sheetDragActive);
  const sheetDragHoverBoard = useStore((s) => s.sheetDragHoverBoard);
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id,
    disabled: sheetDragActive,
  });

  const setRef = useCallback(
    (node: HTMLButtonElement | null) => {
      setNodeRef(node);
      registerBoardTabElement(id, node);
    },
    [id, setNodeRef]
  );

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
  };

  return (
    <button
      ref={setRef}
      style={style}
      className={`${styles.boardTab} ${isActive ? styles.active : ''} ${isDragging ? styles.boardTabDragging : ''} ${sheetDragHoverBoard === id ? styles.boardTabDropTarget : ''}`}
      onClick={onSelect}
      {...attributes}
      {...(sheetDragActive ? {} : listeners)}
      title={sheetDragActive ? `Drop here to move to ${label}` : 'Drag to reorder boards'}
    >
      <span className={styles.boardTabGrip} aria-hidden>
        ⠿
      </span>
      {label}
    </button>
  );
}

export function ClientView() {
  const clients = useStore((s) => s.clients);
  const projects = useStore((s) => s.projects);
  const activeClientId = useStore((s) => s.activeClientId);
  const activeProjectId = useStore((s) => s.activeProjectId);
  const activeBoardType = useStore((s) => s.activeBoardType);
  const sheetDragHoverBoard = useStore((s) => s.sheetDragHoverBoard);
  const sheetDragActive = useStore((s) => s.sheetDragActive);
  const clientsView = useStore((s) => s.clientsView);
  const subBoardTabOrder = useStore((s) => s.subBoardTabOrder);
  const customBoards = useStore((s) => s.customBoards);
  const setActiveClientId = useStore((s) => s.setActiveClientId);
  const setActiveProjectId = useStore((s) => s.setActiveProjectId);
  const setActiveBoardType = useStore((s) => s.setActiveBoardType);
  const reorderProjectBoardTabs = useStore((s) => s.reorderProjectBoardTabs);
  const addCustomBoard = useStore((s) => s.addCustomBoard);
  const addClient = useStore((s) => s.addClient);
  const updateClient = useStore((s) => s.updateClient);
  const addProject = useStore((s) => s.addProject);
  const addProjectFromTemplate = useStore((s) => s.addProjectFromTemplate);
  const importMondayBoard = useStore((s) => s.importMondayBoard);
  const removeProject = useStore((s) => s.removeProject);
  const updateProjectSettings = useStore((s) => s.updateProjectSettings);
  const employees = useStore((s) => s.employees);
  const currentUserId = useStore((s) => s.currentUserId);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const canEditClients = canEditClientsProjects(currentUserId, employees, employeePermissions);

  const [newClientName, setNewClientName] = useState('');
  const [newBoardName, setNewBoardName] = useState('');
  const [showAddClient, setShowAddClient] = useState(false);
  const [showAddProject, setShowAddProject] = useState(false);
  const [showImportMonday, setShowImportMonday] = useState(false);
  const [showAddBoard, setShowAddBoard] = useState(false);
  const [renamingClientId, setRenamingClientId] = useState<string | null>(null);
  const [clientNameDraft, setClientNameDraft] = useState('');
  const [renamingProjectId, setRenamingProjectId] = useState<string | null>(null);
  const [projectNameDraft, setProjectNameDraft] = useState('');
  const [projectContextMenu, setProjectContextMenu] = useState<{
    projectId: string;
    x: number;
    y: number;
  } | null>(null);

  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
    [clients]
  );

  const clientProjects = useMemo(
    () => projects.filter((p) => p.clientId === activeClientId),
    [projects, activeClientId]
  );

  const sortedClientProjects = useMemo(
    () =>
      [...clientProjects].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
    [clientProjects]
  );
  const activeClient = clients.find((c) => c.id === activeClientId);
  const activeProject = projects.find((p) => p.id === activeProjectId);

  const mainBoardTab = PROJECT_BOARD_TYPES.find((b) => b.id === 'main')!;

  const projectBoardOrder = useMemo(() => {
    if (!activeProjectId) return [];
    return getProjectSubBoardOrder(activeProjectId, subBoardTabOrder, customBoards);
  }, [activeProjectId, subBoardTabOrder, customBoards]);

  const sortableBoardTabs = useMemo(
    () =>
      projectBoardOrder.map((id) => ({
        id,
        label: getBoardLabel(id, customBoards),
      })),
    [projectBoardOrder, customBoards]
  );

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 6 } })
  );

  const handleBoardDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id || !activeProjectId) return;
    const oldIndex = projectBoardOrder.indexOf(active.id as ProjectBoardType);
    const newIndex = projectBoardOrder.indexOf(over.id as ProjectBoardType);
    if (oldIndex === -1 || newIndex === -1) return;
    reorderProjectBoardTabs(
      activeProjectId,
      arrayMove(projectBoardOrder, oldIndex, newIndex)
    );
  };

  const handleAddBoard = () => {
    if (!canEditClients) return;
    if (newBoardName.trim() && activeClientId && activeProjectId) {
      addCustomBoard(activeClientId, activeProjectId, newBoardName.trim());
      setNewBoardName('');
      setShowAddBoard(false);
    }
  };

  const handleAddClient = () => {
    if (!canEditClients) return;
    if (newClientName.trim()) {
      addClient(newClientName.trim());
      setNewClientName('');
      setShowAddClient(false);
    }
  };

  const handleAddProject = (result: AddProjectDialogResult) => {
    if (!canEditClients || !activeClientId) return;
    if (result.useTemplate) {
      addProjectFromTemplate(activeClientId, result.name, result);
    } else {
      addProject(activeClientId, result.name, result);
    }
    setShowAddProject(false);
  };

  const handleImportMonday = (result: ImportMondayDialogResult) => {
    if (!canEditClients || !activeClientId) return;
    const summary = importMondayBoard({
      clientId: activeClientId,
      projectName: result.projectName,
      items: result.items,
      ensuredGroups: result.ensuredGroups,
      warnings: result.warnings,
    });
    setShowImportMonday(false);
    if (summary && summary.warnings.length > 0) {
      window.alert(
        `Imported "${summary.projectName}" with ${summary.taskCount} tasks.\n\nNotes:\n${summary.warnings.join('\n')}`
      );
    }
  };

  const startRenamingClient = (clientId: string, name: string) => {
    if (!canEditClients) return;
    setRenamingClientId(clientId);
    setClientNameDraft(name);
  };

  const finishRenamingClient = (clientId: string) => {
    const trimmed = clientNameDraft.trim();
    if (trimmed && canEditClients) updateClient(clientId, { name: trimmed });
    setRenamingClientId(null);
    setClientNameDraft('');
  };

  const startRenamingProject = (projectId: string, name: string) => {
    if (!canEditClients) return;
    setProjectContextMenu(null);
    setRenamingProjectId(projectId);
    setProjectNameDraft(name);
  };

  const finishRenamingProject = (projectId: string) => {
    const trimmed = projectNameDraft.trim();
    if (trimmed && canEditClients) {
      updateProjectSettings(projectId, { name: trimmed });
    }
    setRenamingProjectId(null);
    setProjectNameDraft('');
  };

  const handleDeleteProject = (projectId: string) => {
    setProjectContextMenu(null);
    if (!canEditClients) return;
    const project = projects.find((p) => p.id === projectId);
    if (!project || project.isTemplate) return;
    const ok = window.confirm(
      `Delete project "${project.name}"?\n\nTasks will be moved to the Activity Log where they can be restored.`
    );
    if (!ok) return;
    removeProject(projectId);
  };

  useEffect(() => {
    if (!projectContextMenu) return;
    const close = () => setProjectContextMenu(null);
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    document.addEventListener('click', close);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('click', close);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [projectContextMenu]);

  const contextMenuProject = projectContextMenu
    ? projects.find((p) => p.id === projectContextMenu.projectId)
    : null;

  return (
    <div className={styles.container}>
      {clientsView === 'board' && (
        <div className={styles.navArea}>
          <div className={styles.tabRows}>
            <div className={styles.tabRow}>
              <span className={styles.tabLabel}>Clients</span>
              <div className={styles.tabs}>
                {sortedClients.map((client) =>
                  renamingClientId === client.id ? (
                    <input
                      key={client.id}
                      autoFocus
                      className={styles.clientTabInput}
                      value={clientNameDraft}
                      onChange={(e) => setClientNameDraft(e.target.value)}
                      onBlur={() => finishRenamingClient(client.id)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') {
                          (e.target as HTMLInputElement).blur();
                        }
                        if (e.key === 'Escape') {
                          setRenamingClientId(null);
                          setClientNameDraft('');
                        }
                      }}
                      onClick={(e) => e.stopPropagation()}
                    />
                  ) : (
                    <button
                      key={client.id}
                      className={`${styles.clientTab} ${activeClientId === client.id ? styles.active : ''}`}
                      onClick={() => setActiveClientId(client.id)}
                      onDoubleClick={(e) => {
                        e.preventDefault();
                        startRenamingClient(client.id, client.name);
                      }}
                      title="Double-click to rename"
                    >
                      {client.name}
                    </button>
                  )
                )}
                {showAddClient ? (
                  <div className={styles.inlineForm}>
                    <input
                      autoFocus
                      placeholder="Client name"
                      value={newClientName}
                      onChange={(e) => setNewClientName(e.target.value)}
                      onKeyDown={(e) => e.key === 'Enter' && handleAddClient()}
                    />
                    <button onClick={handleAddClient}>Add</button>
                    <button onClick={() => setShowAddClient(false)}>Cancel</button>
                  </div>
                ) : canEditClients ? (
                  <button className={styles.addBtn} onClick={() => setShowAddClient(true)}>
                    + Client
                  </button>
                ) : null}
              </div>
            </div>

            {activeClient && (
              <>
                <div className={styles.tabRow}>
                  <span className={styles.tabLabel}>Projects</span>
                  <div className={styles.tabs}>
                    {sortedClientProjects.map((project) =>
                      renamingProjectId === project.id ? (
                        <input
                          key={project.id}
                          autoFocus
                          className={styles.projectTabInput}
                          value={projectNameDraft}
                          onChange={(e) => setProjectNameDraft(e.target.value)}
                          onBlur={() => finishRenamingProject(project.id)}
                          onKeyDown={(e) => {
                            if (e.key === 'Enter') {
                              (e.target as HTMLInputElement).blur();
                            }
                            if (e.key === 'Escape') {
                              setRenamingProjectId(null);
                              setProjectNameDraft('');
                            }
                          }}
                          onClick={(e) => e.stopPropagation()}
                        />
                      ) : (
                        <button
                          key={project.id}
                          className={`${styles.projectTab} ${activeProjectId === project.id ? styles.active : ''} ${project.isTemplate ? styles.templateTab : ''}`}
                          onClick={() => setActiveProjectId(project.id)}
                          onContextMenu={(e) => {
                            if (!canEditClients) return;
                            e.preventDefault();
                            setActiveProjectId(project.id);
                            setProjectContextMenu({
                              projectId: project.id,
                              x: e.clientX,
                              y: e.clientY,
                            });
                          }}
                          title={
                            project.isTemplate
                              ? 'Master template — right-click to rename'
                              : canEditClients
                                ? 'Right-click to rename or delete'
                                : undefined
                          }
                        >
                          {project.name}
                          {project.isTemplate && (
                            <span className={styles.templateBadge}>Template</span>
                          )}
                        </button>
                      )
                    )}
                    {showAddProject ? (
                      <AddProjectDialog
                        onSubmit={handleAddProject}
                        onClose={() => setShowAddProject(false)}
                      />
                    ) : null}
                    {showImportMonday ? (
                      <ImportMondayDialog
                        onSubmit={handleImportMonday}
                        onClose={() => setShowImportMonday(false)}
                      />
                    ) : null}
                    {canEditClients && !showAddProject && !showImportMonday ? (
                      <>
                        <button className={styles.addBtn} onClick={() => setShowAddProject(true)}>
                          + Project
                        </button>
                        <button
                          className={styles.addBtn}
                          onClick={() => setShowImportMonday(true)}
                          title="Create a project from an Excel/CSV export"
                        >
                          Import Project from Excel
                        </button>
                      </>
                    ) : null}
                  </div>
                </div>

                {activeProject && (
                  <div className={styles.tabRow}>
                    <span className={styles.tabLabel}>Boards</span>
                    <div className={styles.tabs}>
                      <button
                        ref={(node) => registerBoardTabElement('main', node)}
                        className={`${styles.boardTab} ${styles.boardTabFixed} ${activeBoardType === 'main' ? styles.active : ''} ${sheetDragHoverBoard === 'main' ? styles.boardTabDropTarget : ''}`}
                        onClick={() => setActiveBoardType('main')}
                        title={sheetDragActive ? 'Drop here to move to Main Overview' : undefined}
                      >
                        {mainBoardTab.label}
                      </button>
                      <DndContext
                        sensors={sensors}
                        collisionDetection={closestCenter}
                        onDragEnd={handleBoardDragEnd}
                      >
                        <SortableContext
                          items={projectBoardOrder}
                          strategy={horizontalListSortingStrategy}
                        >
                          {sortableBoardTabs.map((board) => (
                            <SortableBoardTab
                              key={board.id}
                              id={board.id}
                              label={board.label}
                              isActive={activeBoardType === board.id}
                              onSelect={() => setActiveBoardType(board.id)}
                            />
                          ))}
                        </SortableContext>
                      </DndContext>
                      {showAddBoard ? (
                        <div className={styles.inlineForm}>
                          <input
                            autoFocus
                            placeholder="Board name"
                            value={newBoardName}
                            onChange={(e) => setNewBoardName(e.target.value)}
                            onKeyDown={(e) => e.key === 'Enter' && handleAddBoard()}
                          />
                          <button onClick={handleAddBoard}>Add</button>
                          <button onClick={() => setShowAddBoard(false)}>Cancel</button>
                        </div>
                      ) : canEditClients ? (
                        <button className={styles.addBtn} onClick={() => setShowAddBoard(true)}>
                          + Board
                        </button>
                      ) : null}
                    </div>
                  </div>
                )}
              </>
            )}
          </div>

          {activeProject && (
            <ProjectMetaPanel
              project={activeProject}
              employees={employees}
              onUpdate={(updates) => updateProjectSettings(activeProject.id, updates)}
            />
          )}
        </div>
      )}

      {clientsView === 'dashboard' && <MainDashboard />}

      {clientsView === 'board' && activeClient && activeProject && (
        <div className={styles.boardArea}>
          <TaskSpreadsheet
            key={activeBoardType}
            clientId={activeClientId!}
            projectId={activeProjectId!}
            boardType={activeBoardType}
          />
        </div>
      )}

      {clientsView === 'board' && activeClient && !activeProject && clientProjects.length === 0 && (
        <div className={styles.empty}>
          <p>No projects yet for {activeClient.name}.</p>
          <p className={styles.emptyHint}>
            Click "+ Project" or "Import Project from Excel" to create one.
          </p>
        </div>
      )}

      {clientsView === 'board' && clients.length === 0 && (
        <div className={styles.empty}>
          <p>No clients yet.</p>
          <p className={styles.emptyHint}>Click "+ Client" to get started.</p>
        </div>
      )}

      {projectContextMenu && contextMenuProject ? (
        <ContextMenuPanel
          x={projectContextMenu.x}
          y={projectContextMenu.y}
          className={styles.contextMenu}
          onClick={(e) => e.stopPropagation()}
        >
          <button
            type="button"
            onClick={() =>
              startRenamingProject(contextMenuProject.id, contextMenuProject.name)
            }
          >
            Rename
          </button>
          {!contextMenuProject.isTemplate ? (
            <button
              type="button"
              className={styles.contextMenuDelete}
              onClick={() => handleDeleteProject(contextMenuProject.id)}
            >
              Delete
            </button>
          ) : null}
        </ContextMenuPanel>
      ) : null}
    </div>
  );
}
