import { useStore } from '../store/useStore';
import { canViewSpoolingDashboard } from '../utils/permissions';
import { TaskSpreadsheet } from './TaskSpreadsheet';
import clientStyles from './ClientView.module.css';
import styles from './SpoolingDashboardView.module.css';

export function SpoolingDashboardView() {
  const currentUserId = useStore((s) => s.currentUserId);
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);

  const canView = canViewSpoolingDashboard(currentUserId, employees, employeePermissions);

  if (!canView) {
    return (
      <div className={clientStyles.empty}>
        <p>You do not have access to the Spooling Dashboard.</p>
      </div>
    );
  }

  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <h1 className={styles.title}>Spooling Dashboard</h1>
        <p className={styles.subtitle}>
          All projects’ Spooling work in one spreadsheet. Create new rows on each project’s Spooling
          board under Clients.
        </p>
      </header>
      <div className={styles.sheetArea}>
        <TaskSpreadsheet
          clientId=""
          projectId=""
          boardType="spooling"
          scope="all-projects"
        />
      </div>
    </div>
  );
}
