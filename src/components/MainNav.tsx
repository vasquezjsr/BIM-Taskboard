import { useCallback, useEffect, useRef, useState } from 'react';
import { useStore } from '../store/useStore';
import type { MainTab, Employee, DashboardType, OrgCategory } from '../types';
import { canAccessOrgChart, canViewActivityLog, canViewOwnerDashboard, canViewSpoolingDashboard, canViewTimeTracking, canViewVisibilityDashboard, canViewWeldLogDashboard, visibleDashboards } from '../utils/permissions';
import { DASHBOARD_META } from '../data/dashboards';
import type { EmployeePermissionsMap } from '../utils/orgChart';
import styles from './MainNav.module.css';

const dashboardTabId = (dashboard: DashboardType): MainTab => `${dashboard}-dashboard`;

/** Production-phase dashboards in BIM lifecycle order (after PM). */
const PHASE_DASHBOARDS: DashboardType[] = ['fab', 'shipping', 'field'];

const SCROLL_STEP_PX = 220;

function buildNavTabs(
  currentUserId: string | null,
  employees: Employee[],
  employeePermissions: EmployeePermissionsMap,
  visibilityDashboardJobLevels: OrgCategory[]
): { id: MainTab; label: string }[] {
  const tabs: { id: MainTab; label: string }[] = [];
  const dashboards = visibleDashboards(currentUserId, employees, employeePermissions);

  if (canViewOwnerDashboard(currentUserId, employees, employeePermissions)) {
    tabs.push({ id: 'owner-dashboard', label: 'Owner Dashboard' });
  }

  if (dashboards.includes('pm')) {
    tabs.push({ id: 'pm-dashboard', label: DASHBOARD_META.pm.label });
  }

  tabs.push({ id: 'clients', label: 'Clients' });
  tabs.push({ id: 'task-board', label: 'Task Board' });

  if (canViewSpoolingDashboard(currentUserId, employees, employeePermissions)) {
    tabs.push({ id: 'spooling-dashboard', label: 'Spooling Dashboard' });
  }

  for (const dashboard of PHASE_DASHBOARDS) {
    if (dashboards.includes(dashboard)) {
      tabs.push({ id: dashboardTabId(dashboard), label: DASHBOARD_META[dashboard].label });
    }
  }

  if (canViewWeldLogDashboard(currentUserId, employees, employeePermissions)) {
    tabs.push({ id: 'weld-log-dashboard', label: 'Weld Log Dashboard' });
  }

  if (canViewTimeTracking(currentUserId, employees, employeePermissions)) {
    tabs.push({ id: 'time-tracking', label: 'Time Tracking' });
  }

  tabs.push({ id: 'employees', label: 'Employees' });

  if (canAccessOrgChart(currentUserId, employees, employeePermissions)) {
    tabs.push({ id: 'org-chart', label: 'Organizational Chart' });
  }

  if (canViewActivityLog(currentUserId, employees, employeePermissions)) {
    tabs.push({ id: 'activity-log', label: 'Activity Log' });
  }

  if (
    canViewVisibilityDashboard(
      currentUserId,
      employees,
      employeePermissions,
      visibilityDashboardJobLevels
    )
  ) {
    tabs.push({ id: 'visibility-dashboard', label: 'Access Control' });
  }

  return tabs;
}

export function MainNav() {
  const activeMainTab = useStore((s) => s.activeMainTab);
  const setActiveMainTab = useStore((s) => s.setActiveMainTab);
  const currentUserId = useStore((s) => s.currentUserId);
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const visibilityDashboardJobLevels = useStore((s) => s.visibilityDashboardJobLevels);

  const tabs = buildNavTabs(
    currentUserId,
    employees,
    employeePermissions,
    visibilityDashboardJobLevels
  );

  const scrollerRef = useRef<HTMLDivElement>(null);
  const [canScrollLeft, setCanScrollLeft] = useState(false);
  const [canScrollRight, setCanScrollRight] = useState(false);

  const updateScrollState = useCallback(() => {
    const el = scrollerRef.current;
    if (!el) return;
    const { scrollLeft, clientWidth, scrollWidth } = el;
    setCanScrollLeft(scrollLeft > 1);
    setCanScrollRight(scrollLeft + clientWidth < scrollWidth - 1);
  }, []);

  useEffect(() => {
    const el = scrollerRef.current;
    if (!el) return;
    updateScrollState();
    const observer = new ResizeObserver(() => updateScrollState());
    observer.observe(el);
    if (el.firstElementChild) observer.observe(el.firstElementChild);
    el.addEventListener('scroll', updateScrollState, { passive: true });
    window.addEventListener('resize', updateScrollState);
    const raf = window.requestAnimationFrame(updateScrollState);
    const timeoutId = window.setTimeout(updateScrollState, 100);
    return () => {
      observer.disconnect();
      el.removeEventListener('scroll', updateScrollState);
      window.removeEventListener('resize', updateScrollState);
      window.cancelAnimationFrame(raf);
      window.clearTimeout(timeoutId);
    };
  }, [updateScrollState, tabs.length]);

  useEffect(() => {
    const el = scrollerRef.current;
    if (!el) return;
    const active = el.querySelector<HTMLElement>(`[data-tab-id="${activeMainTab}"]`);
    active?.scrollIntoView({ inline: 'nearest', block: 'nearest', behavior: 'smooth' });
    window.requestAnimationFrame(updateScrollState);
  }, [activeMainTab, updateScrollState]);

  const scrollByDir = (dir: -1 | 1) => {
    scrollerRef.current?.scrollBy({ left: dir * SCROLL_STEP_PX, behavior: 'smooth' });
  };

  return (
    <div
      className={`${styles.navShell} ${canScrollLeft ? styles.fadeLeft : ''} ${
        canScrollRight ? styles.fadeRight : ''
      }`}
    >
      <button
        type="button"
        className={styles.scrollBtn}
        disabled={!canScrollLeft}
        onClick={() => scrollByDir(-1)}
        aria-label="Scroll tabs left"
        title="Scroll left"
      >
        ‹
      </button>
      <div className={styles.scroller} ref={scrollerRef}>
        <nav className={styles.nav} aria-label="Main">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              data-tab-id={tab.id}
              className={`${styles.tab} ${activeMainTab === tab.id ? styles.active : ''}`}
              onClick={() => setActiveMainTab(tab.id)}
            >
              {tab.label}
            </button>
          ))}
        </nav>
      </div>
      <button
        type="button"
        className={styles.scrollBtn}
        disabled={!canScrollRight}
        onClick={() => scrollByDir(1)}
        aria-label="Scroll tabs right"
        title="Scroll right"
      >
        ›
      </button>
    </div>
  );
}
