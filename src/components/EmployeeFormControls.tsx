import { useLayoutEffect, useRef, useState, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { ALL_PERMISSIONS, PERMISSION_LABELS } from '../utils/orgChart';
import type { AppPermission, Employee } from '../types';
import formStyles from './EmployeeManagementDialog.module.css';

export const PERMISSION_SHORT: Record<AppPermission, string> = {
  'edit-budget-hours': 'Budget',
  'manage-org': 'Org',
  'manage-columns': 'Columns',
  'edit-pm-assigns': 'PM assign',
  'assign-fab-leads': 'Fab lead',
  'assign-fab-workers': 'Fab worker',
  'edit-fab-status': 'Fab status',
  'fab-clock': 'Fab clock',
  'edit-weld-log': 'Weld log',
  'edit-fab-collab': 'Fab notes',
  'log-time': 'Log time',
  'delete-time': 'Del time',
  'edit-clients-projects': 'Clients',
  'edit-tasks': 'Tasks',
  'assign-tasks': 'Assign',
  'manage-statuses': 'Statuses',
  'add-columns': 'Add cols',
  'view-activity-log': 'Activity',
  'view-org-chart': 'Chart',
  'view-owner-dashboard': 'Owner',
  'view-pm-dashboard': 'PM',
  'view-field-dashboard': 'Field',
  'view-fab-dashboard': 'Shop',
  'view-shipping-dashboard': 'Ship',
  'view-weld-log-dashboard': 'Weld Log Dash',
  'view-visibility-dashboard': 'Access',
  'view-time-tracking': 'Time',
};

export function WorksUnderPicker({
  employeeId,
  managerIds,
  managerOptions,
  onToggleManager,
  disabled,
}: {
  employeeId: string;
  managerIds: string[];
  managerOptions: Employee[];
  onToggleManager: (managerId: string, enabled: boolean) => void;
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const [panelStyle, setPanelStyle] = useState<CSSProperties>({});

  const selectedNames = managerOptions
    .filter((option) => managerIds.includes(option.id))
    .map((option) => option.name);

  const summary =
    selectedNames.length === 0
      ? 'Top level'
      : selectedNames.length === 1
        ? selectedNames[0]!
        : `${selectedNames.length} managers`;

  useLayoutEffect(() => {
    if (!open || !buttonRef.current) return;

    const updatePosition = () => {
      const button = buttonRef.current;
      if (!button) return;

      const rect = button.getBoundingClientRect();
      const panelHeight = panelRef.current?.offsetHeight ?? 200;
      const spaceBelow = window.innerHeight - rect.bottom - 8;
      const spaceAbove = rect.top - 8;
      const openUpward = spaceBelow < panelHeight && spaceAbove > spaceBelow;

      setPanelStyle({
        top: openUpward ? undefined : rect.bottom + 4,
        bottom: openUpward ? window.innerHeight - rect.top + 4 : undefined,
        left: Math.min(rect.left, window.innerWidth - 240),
        minWidth: Math.max(rect.width, 220),
        maxHeight: Math.min(240, openUpward ? spaceAbove : spaceBelow),
      });
    };

    updatePosition();
    const raf = requestAnimationFrame(updatePosition);
    window.addEventListener('scroll', updatePosition, true);
    window.addEventListener('resize', updatePosition);
    return () => {
      cancelAnimationFrame(raf);
      window.removeEventListener('scroll', updatePosition, true);
      window.removeEventListener('resize', updatePosition);
    };
  }, [open, managerOptions.length]);

  useLayoutEffect(() => {
    if (!open) return;
    const close = (event: MouseEvent) => {
      const target = event.target as Node;
      if (rootRef.current?.contains(target) || panelRef.current?.contains(target)) return;
      setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  if (disabled) {
    return <span className={formStyles.cellText}>{summary}</span>;
  }

  const panel =
    open &&
    createPortal(
      <div
        ref={panelRef}
        className={formStyles.pickerPanel}
        style={panelStyle}
        onMouseDown={(event) => event.stopPropagation()}
      >
        {managerOptions.length === 0 ? (
          <span className={formStyles.pickerEmpty}>No other teammates</span>
        ) : (
          managerOptions.map((manager) => (
            <label key={manager.id} className={formStyles.pickerOption}>
              <input
                type="checkbox"
                checked={managerIds.includes(manager.id)}
                onChange={(e) => onToggleManager(manager.id, e.target.checked)}
              />
              <span>{manager.name}</span>
            </label>
          ))
        )}
      </div>,
      document.body
    );

  return (
    <div className={formStyles.pickerRoot} ref={rootRef}>
      <button
        ref={buttonRef}
        type="button"
        className={formStyles.pickerBtn}
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        aria-controls={`works-under-${employeeId}`}
      >
        <span className={formStyles.pickerBtnLabel}>{summary}</span>
        <span className={formStyles.pickerChevron} aria-hidden>
          ▾
        </span>
      </button>
      {panel}
    </div>
  );
}

export function PermissionToggles({
  permissions,
  onToggle,
  disabled,
  compact,
}: {
  permissions: AppPermission[];
  onToggle: (permission: AppPermission, enabled: boolean) => void;
  disabled?: boolean;
  compact?: boolean;
}) {
  if (disabled) {
    const label =
      permissions.length === 0
        ? 'None'
        : permissions.map((permission) => PERMISSION_SHORT[permission]).join(', ');
    return <span className={formStyles.cellText}>{label}</span>;
  }

  return (
    <div className={formStyles.permissionRow}>
      {ALL_PERMISSIONS.map((permission) => {
        const active = permissions.includes(permission);
        return (
          <button
            key={permission}
            type="button"
            className={`${formStyles.permissionPill} ${active ? formStyles.permissionPillActive : ''}`}
            aria-pressed={active}
            title={PERMISSION_LABELS[permission]}
            onClick={() => onToggle(permission, !active)}
            style={compact ? { fontSize: 10, padding: '3px 6px' } : undefined}
          >
            {PERMISSION_SHORT[permission]}
          </button>
        );
      })}
    </div>
  );
}
