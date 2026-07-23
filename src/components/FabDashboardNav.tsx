import { useMemo } from 'react';
import { useStore } from '../store/useStore';
import {
  canBrowseFabWorkstations,
  isFabShopFloorWorker,
  listFabFabricationWorkstationGroups,
  listFabWorkers,
  type FabPersonOption,
  type FabWorkstationGroup,
} from '../utils/fabWorkstationAccess';
import styles from './FabDashboardNav.module.css';

export type ShopDashboardSection = 'queue' | 'warehouse' | 'fabrication';

interface FabDashboardNavProps {
  shopSection: ShopDashboardSection;
  selectedWorkstationId: string | null;
  onShopSectionChange: (section: ShopDashboardSection) => void;
  onWorkstationSelect: (employeeId: string) => void;
}

function findActiveGroup(
  groups: FabWorkstationGroup[],
  selectedWorkstationId: string | null
): FabWorkstationGroup | null {
  if (!selectedWorkstationId) return null;
  return (
    groups.find(
      (group) =>
        group.manager.id === selectedWorkstationId ||
        group.workers.some((worker) => worker.id === selectedWorkstationId)
    ) ?? null
  );
}

/**
 * Shop dashboard strip — Queued / Warehouse / Fabrication (+ workstations).
 * Owners/managers see all leads + all workers.
 * Floor workers get Queued (view-only) + their own station.
 */
export function FabDashboardNav({
  shopSection,
  selectedWorkstationId,
  onShopSectionChange,
  onWorkstationSelect,
}: FabDashboardNavProps) {
  const employees = useStore((s) => s.employees);
  const dashboardAssignments = useStore((s) => s.dashboardAssignments);
  const employeeReportsTo = useStore((s) => s.employeeReportsTo);
  const currentUserId = useStore((s) => s.currentUserId);

  const floorWorker = isFabShopFloorWorker(
    currentUserId,
    dashboardAssignments,
    employees
  );
  const canBrowse = canBrowseFabWorkstations(
    currentUserId,
    dashboardAssignments,
    employees
  );

  const { groups, unassignedWorkers } = useMemo(
    () =>
      listFabFabricationWorkstationGroups(employees, dashboardAssignments, employeeReportsTo),
    [employees, dashboardAssignments, employeeReportsTo]
  );

  const allWorkers = useMemo(
    () => listFabWorkers(employees, dashboardAssignments),
    [employees, dashboardAssignments]
  );

  const activeGroup = useMemo(
    () => findActiveGroup(groups, selectedWorkstationId),
    [groups, selectedWorkstationId]
  );

  const renderManagerTab = (option: FabPersonOption, active: boolean) => (
    <button
      key={option.id}
      type="button"
      className={`${styles.workstationTab} ${styles.workstationTabManager} ${
        active ? styles.workstationTabActive : ''
      }`}
      onClick={() => onWorkstationSelect(option.id)}
      title={option.roleLabel}
    >
      {option.name}
      <span className={styles.workstationRole}>{option.roleLabel}</span>
    </button>
  );

  const renderWorkerTab = (option: FabPersonOption, active: boolean) => (
    <button
      key={option.id}
      type="button"
      className={`${styles.workerTab} ${active ? styles.workerTabActive : ''}`}
      onClick={() => onWorkstationSelect(option.id)}
      title={option.roleLabel}
    >
      {option.name}
      <span className={styles.workstationRole}>{option.roleLabel}</span>
    </button>
  );

  const hasWorkstations = groups.length > 0 || unassignedWorkers.length > 0 || allWorkers.length > 0;

  // Floor workers: Queued (view-only) + their own fabrication station. No warehouse / other people.
  if (floorWorker && currentUserId) {
    const selfWorker =
      allWorkers.find((worker) => worker.id === currentUserId) ??
      ({
        id: currentUserId,
        name: employees.find((employee) => employee.id === currentUserId)?.name ?? 'My workstation',
        roleId: 'worker' as const,
        roleLabel: 'Worker',
      } satisfies FabPersonOption);

    return (
      <div className={styles.navArea} aria-label="Shop dashboards">
        <div className={styles.tabRows}>
          <div className={styles.tabRow}>
            <span className={styles.tabLabel}>Dashboards</span>
            <div className={styles.tabs}>
              <button
                type="button"
                className={`${styles.dashTab} ${shopSection === 'queue' ? styles.dashTabActive : ''}`}
                onClick={() => onShopSectionChange('queue')}
              >
                Queued Dashboard
              </button>
              <button
                type="button"
                className={`${styles.dashTab} ${
                  shopSection === 'fabrication' ? styles.dashTabActive : ''
                }`}
                onClick={() => onWorkstationSelect(selfWorker.id)}
              >
                Fabrication Dashboard
              </button>
            </div>
          </div>
          {shopSection === 'fabrication' && (
            <div className={styles.tabRow}>
              <span className={styles.tabLabel}>Workstation</span>
              <div className={styles.tabs}>
                {renderWorkerTab(selfWorker, selectedWorkstationId === selfWorker.id)}
              </div>
            </div>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className={styles.navArea} aria-label="Shop dashboards">
      <div className={styles.tabRows}>
        <div className={styles.tabRow}>
          <span className={styles.tabLabel}>Dashboards</span>
          <div className={styles.tabs}>
            <button
              type="button"
              className={`${styles.dashTab} ${shopSection === 'queue' ? styles.dashTabActive : ''}`}
              onClick={() => onShopSectionChange('queue')}
            >
              Queued Dashboard
            </button>
            <button
              type="button"
              className={`${styles.dashTab} ${
                shopSection === 'warehouse' ? styles.dashTabActive : ''
              }`}
              onClick={() => onShopSectionChange('warehouse')}
            >
              Warehouse Dashboard
            </button>
            <button
              type="button"
              className={`${styles.dashTab} ${
                shopSection === 'fabrication' ? styles.dashTabActive : ''
              }`}
              onClick={() => onShopSectionChange('fabrication')}
            >
              Fabrication Dashboard
            </button>
          </div>
        </div>

        {shopSection === 'fabrication' && (
          <>
            <div className={styles.tabRow}>
              <span className={styles.tabLabel}>Workstations</span>
              {!hasWorkstations ? (
                <span className={styles.emptyWorkstations}>
                  No fabrication workstations assigned.
                </span>
              ) : (
                <div className={styles.tabs}>
                  {groups.map((group) =>
                    renderManagerTab(
                      group.manager,
                      selectedWorkstationId === group.manager.id
                    )
                  )}
                  {/* Unassigned workers only here when we aren't showing the full Workers row */}
                  {!canBrowse &&
                    unassignedWorkers.map((worker) =>
                      renderWorkerTab(worker, selectedWorkstationId === worker.id)
                    )}
                </div>
              )}
            </div>

            {canBrowse && allWorkers.length > 0 && (
              <div className={styles.tabRow}>
                <span className={styles.tabLabel}>Workers</span>
                <div className={styles.tabs}>
                  {allWorkers.map((worker) =>
                    renderWorkerTab(worker, selectedWorkstationId === worker.id)
                  )}
                </div>
              </div>
            )}

            {!canBrowse && activeGroup && activeGroup.workers.length > 0 && (
              <div className={styles.tabRow}>
                <span className={styles.tabLabel}>Workers</span>
                <div className={styles.tabs}>
                  {activeGroup.workers.map((worker) =>
                    renderWorkerTab(worker, selectedWorkstationId === worker.id)
                  )}
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
