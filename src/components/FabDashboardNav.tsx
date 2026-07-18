import { useMemo } from 'react';
import { useStore } from '../store/useStore';
import {
  listFabFabricationWorkstationGroups,
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
 * Styled like Clients → Projects → Boards. Never changes the signed-in user.
 *
 * Workstations row = supervisors only.
 * Workers row = only the selected supervisor's reports.
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

  const { groups, unassignedWorkers } = useMemo(
    () =>
      listFabFabricationWorkstationGroups(employees, dashboardAssignments, employeeReportsTo),
    [employees, dashboardAssignments, employeeReportsTo]
  );

  const activeGroup = useMemo(
    () => findActiveGroup(groups, selectedWorkstationId),
    [groups, selectedWorkstationId]
  );

  const teamWorkers = activeGroup?.workers ?? [];
  const showWorkersRow = Boolean(activeGroup && teamWorkers.length > 0);

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

  const hasWorkstations = groups.length > 0 || unassignedWorkers.length > 0;

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
                      activeGroup?.manager.id === group.manager.id
                    )
                  )}
                  {unassignedWorkers.map((worker) =>
                    renderWorkerTab(worker, selectedWorkstationId === worker.id)
                  )}
                </div>
              )}
            </div>

            {showWorkersRow && (
              <div className={styles.tabRow}>
                <span className={styles.tabLabel}>Workers</span>
                <div className={styles.tabs}>
                  {teamWorkers.map((worker) =>
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
