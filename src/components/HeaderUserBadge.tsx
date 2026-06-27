import { useEmployeeAssigneeStyle } from '../hooks/useEmployeeAssigneeStyle';
import { employeeInitials } from '../data/employees';
import { useStore } from '../store/useStore';
import styles from './HeaderUserBadge.module.css';

export function HeaderUserBadge() {
  const currentUserId = useStore((s) => s.currentUserId);
  const viewAsOriginalUserId = useStore((s) => s.viewAsOriginalUserId);
  const employees = useStore((s) => s.employees);
  const logout = useStore((s) => s.logout);

  const user = employees.find((employee) => employee.id === currentUserId);
  if (!user) return null;

  const badgeStyle = useEmployeeAssigneeStyle(user.id);
  const isPreviewing = viewAsOriginalUserId !== null;

  return (
    <div className={styles.wrap}>
      {isPreviewing && <span className={styles.previewBadge}>Preview</span>}
      <div className={styles.identity}>
        <span
          className={styles.avatar}
          style={{
            borderColor: badgeStyle.border,
            background: badgeStyle.background,
            color: badgeStyle.text,
          }}
          aria-hidden
        >
          {employeeInitials(user.name)}
        </span>
        <span className={styles.name}>{user.name}</span>
      </div>
      <button type="button" className={styles.signOutBtn} onClick={logout}>
        Sign out
      </button>
    </div>
  );
}
