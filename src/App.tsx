import { useEffect, useState } from 'react';

import { useStore } from './store/useStore';

import { MainNav } from './components/MainNav';

import { HeaderUserBadge } from './components/HeaderUserBadge';

import { LoginScreen } from './components/LoginScreen';

import { ClientView } from './components/ClientView';

import { EmployeeView } from './components/EmployeeView';

import { OrgChartView } from './components/OrgChartView';

import { TimeTrackingView } from './components/TimeTrackingView';

import { DepartmentDashboardView } from './components/DepartmentDashboardView';

import { OwnerDashboardView } from './components/OwnerDashboardView';
import { ActivityLogView } from './components/ActivityLogView';

import { PMDashboardView } from './components/PMDashboardView';

import { EmployeeDashboardView } from './components/EmployeeDashboardView';

import { ReportsDialog } from './components/ReportsDialog';

import { ViewAsPicker } from './components/ViewAsPicker';

import { canAccessOrgChart } from './utils/permissions';

import styles from './App.module.css';



function syncPermissionsMenu() {

  const { currentUserId, employees, employeePermissions } = useStore.getState();

  window.electronAPI?.setPermissionsMenuVisible(

    canAccessOrgChart(currentUserId, employees, employeePermissions)

  );

}



function App() {

  const activeMainTab = useStore((s) => s.activeMainTab);

  const clientsView = useStore((s) => s.clientsView);

  const currentUserId = useStore((s) => s.currentUserId);

  const employees = useStore((s) => s.employees);

  const employeePermissions = useStore((s) => s.employeePermissions);

  const setActiveMainTab = useStore((s) => s.setActiveMainTab);

  const goToMainScreen = useStore((s) => s.goToMainScreen);

  const isMainScreen = activeMainTab === 'clients' && clientsView === 'dashboard';

  const isLoggedIn = currentUserId !== null;



  const [storeReady, setStoreReady] = useState(() => useStore.persist.hasHydrated());
  const [hydrationTimedOut, setHydrationTimedOut] = useState(false);
  const [showReports, setShowReports] = useState(false);



  useEffect(() => {

    if (storeReady) return;

    const unsub = useStore.persist.onFinishHydration(() => setStoreReady(true));
    const timeoutId = window.setTimeout(() => setHydrationTimedOut(true), 12000);

    return () => {
      unsub();
      window.clearTimeout(timeoutId);
    };

  }, [storeReady]);



  useEffect(() => {

    const refresh = () => useStore.getState().refreshActiveView();



    const onKeyDown = (e: KeyboardEvent) => {

      if (e.key === 'F5') {

        e.preventDefault();

        refresh();

        if (document.activeElement instanceof HTMLElement) {
          document.activeElement.blur();
        }

        return;

      }

      const target = e.target as HTMLElement;
      if (['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName)) return;

      const mod = e.ctrlKey || e.metaKey;
      if (!mod) return;

      const { undo, redo, historyPast, historyFuture } = useStore.getState();

      if (e.key === 'z' || e.key === 'Z') {
        if (e.shiftKey) {
          if (historyFuture.length === 0) return;
          e.preventDefault();
          redo();
        } else {
          if (historyPast.length === 0) return;
          e.preventDefault();
          undo();
        }
        return;
      }

      if (e.key === 'y' || e.key === 'Y') {
        if (historyFuture.length === 0) return;
        e.preventDefault();
        redo();
      }

    };

    window.addEventListener('keydown', onKeyDown, true);



    const unsubscribeElectron = window.electronAPI?.onRefreshView(refresh);

    const unsubscribeNavigate = window.electronAPI?.onNavigateTo((tab) => {

      if (tab === 'org-chart' || tab === 'permissions') setActiveMainTab('org-chart');

    });

    const unsubscribeMenuSync = window.electronAPI?.onRequestPermissionsMenuSync(syncPermissionsMenu);



    return () => {

      window.removeEventListener('keydown', onKeyDown, true);

      unsubscribeElectron?.();

      unsubscribeNavigate?.();

      unsubscribeMenuSync?.();

    };

  }, [setActiveMainTab]);



  useEffect(() => {

    syncPermissionsMenu();

    const unsubHydrate = useStore.persist.onFinishHydration(syncPermissionsMenu);

    return () => unsubHydrate();

  }, [currentUserId, employees, employeePermissions]);



  useEffect(() => {
    if (!storeReady || !import.meta.env.DEV) return;
    const { currentUserId: userId, ensureDevSession } = useStore.getState();
    if (!userId) ensureDevSession();
  }, [storeReady]);



  if (!storeReady) {
    return (
      <div className={styles.app}>
        <div className={styles.loadingScreen}>
          <span className={styles.logoIcon}>◈</span>
          <p className={styles.loadingText}>
            {hydrationTimedOut ? 'Still loading your workspace…' : 'Loading BIM Boardroom…'}
          </p>
          {hydrationTimedOut && (
            <p className={styles.loadingHint}>
              If this screen stays blank, close the app completely and reopen it. Your data is stored
              locally.
            </p>
          )}
        </div>
      </div>
    );
  }



  if (!isLoggedIn) {

    return (

      <div className={styles.app}>

        <LoginScreen />

      </div>

    );

  }



  return (

    <div className={styles.app}>

      <header className={styles.header}>

        <button

          type="button"

          className={`${styles.logo} ${isMainScreen ? styles.logoActive : ''}`}

          onClick={goToMainScreen}

          title="Main Dashboard"

        >

          <span className={styles.logoIcon}>◈</span>

          <span className={styles.logoText}>BIM Boardroom</span>

        </button>

        <div className={styles.headerNav}>

          <MainNav />

        </div>

        <div className={styles.headerSpacer} />

        <div className={styles.headerActions}>
          <button
            type="button"
            className={styles.reportsBtn}
            onClick={() => setShowReports(true)}
          >
            Reports
          </button>

          <ViewAsPicker />
        </div>

        <HeaderUserBadge />

      </header>

      {showReports && (
        <ReportsDialog activeTab={activeMainTab} onClose={() => setShowReports(false)} />
      )}



      <main className={styles.main}>

        {activeMainTab === 'clients' && <ClientView />}

        {activeMainTab === 'task-board' && <EmployeeView />}

        {activeMainTab === 'time-tracking' && <TimeTrackingView />}

        {activeMainTab === 'employees' && <EmployeeDashboardView />}

        {activeMainTab === 'org-chart' && <OrgChartView />}

        {activeMainTab === 'activity-log' && <ActivityLogView />}

        {activeMainTab === 'owner-dashboard' && <OwnerDashboardView />}

        {activeMainTab === 'pm-dashboard' && <PMDashboardView />}

        {activeMainTab === 'field-dashboard' && <DepartmentDashboardView dashboard="field" />}

        {activeMainTab === 'fab-dashboard' && <DepartmentDashboardView dashboard="fab" />}

        {activeMainTab === 'shipping-dashboard' && <DepartmentDashboardView dashboard="shipping" />}

      </main>

    </div>

  );

}



export default App;


