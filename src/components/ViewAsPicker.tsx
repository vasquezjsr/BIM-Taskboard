import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useStore } from '../store/useStore';
import { buildViewAsGroups } from '../utils/viewAsPerspectives';
import styles from './ViewAsPicker.module.css';

export function ViewAsPicker() {
  const employees = useStore((s) => s.employees);
  const currentUserId = useStore((s) => s.currentUserId);
  const viewAsOriginalUserId = useStore((s) => s.viewAsOriginalUserId);
  const setViewAsEmployee = useStore((s) => s.setViewAsEmployee);

  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const [panelStyle, setPanelStyle] = useState<{ top: number; left: number }>({ top: 0, left: 0 });

  const groups = useMemo(() => buildViewAsGroups(employees), [employees]);
  const isPreviewing = viewAsOriginalUserId !== null;
  const currentUser = employees.find((employee) => employee.id === currentUserId);
  const originalUser = employees.find((employee) => employee.id === viewAsOriginalUserId);

  useLayoutEffect(() => {
    if (!open || !buttonRef.current) return;

    const updatePosition = () => {
      const button = buttonRef.current;
      if (!button) return;

      const rect = button.getBoundingClientRect();
      const panelWidth = panelRef.current?.offsetWidth ?? 300;
      const panelHeight = panelRef.current?.offsetHeight ?? 320;
      const left = Math.min(Math.max(8, rect.right - panelWidth), window.innerWidth - panelWidth - 8);
      const spaceBelow = window.innerHeight - rect.bottom - 8;
      const top =
        spaceBelow >= panelHeight || rect.top < panelHeight
          ? rect.bottom + 6
          : rect.top - panelHeight - 6;

      setPanelStyle({ top, left });
    };

    updatePosition();
    window.addEventListener('resize', updatePosition);
    window.addEventListener('scroll', updatePosition, true);
    return () => {
      window.removeEventListener('resize', updatePosition);
      window.removeEventListener('scroll', updatePosition, true);
    };
  }, [open, groups]);

  useEffect(() => {
    if (!open) return;

    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (rootRef.current?.contains(target) || panelRef.current?.contains(target)) return;
      setOpen(false);
    };

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };

    window.addEventListener('mousedown', onPointerDown);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('mousedown', onPointerDown);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  const triggerLabel = isPreviewing
    ? `Viewing: ${currentUser?.name ?? 'Employee'}`
    : 'View as…';

  const panel =
    open &&
    createPortal(
      <div ref={panelRef} className={styles.panel} style={panelStyle} role="menu">
        {groups.map((group) => (
          <div key={group.label}>
            <div className={styles.groupLabel}>{group.label}</div>
            {group.options.map((option) => (
              <button
                key={option.employeeId}
                type="button"
                role="menuitem"
                className={`${styles.option} ${
                  option.employeeId === currentUserId ? styles.optionActive : ''
                }`}
                onClick={() => {
                  setViewAsEmployee(option.employeeId);
                  setOpen(false);
                }}
              >
                {option.label}
              </button>
            ))}
          </div>
        ))}
        {isPreviewing && originalUser && (
          <div className={styles.resetOption}>
            <button
              type="button"
              role="menuitem"
              className={styles.option}
              onClick={() => {
                setViewAsEmployee(null);
                setOpen(false);
              }}
            >
              Back to {originalUser.name}
            </button>
          </div>
        )}
      </div>,
      document.body
    );

  return (
    <div className={styles.root} ref={rootRef}>
      <button
        ref={buttonRef}
        type="button"
        className={`${styles.trigger} ${open ? styles.triggerOpen : ''} ${
          isPreviewing ? styles.triggerActive : ''
        }`}
        onClick={() => setOpen((value) => !value)}
        title="Preview the app as another employee type"
        aria-haspopup="menu"
        aria-expanded={open}
      >
        {triggerLabel}
        <span className={styles.chevron} aria-hidden>
          ▾
        </span>
      </button>
      {panel}
    </div>
  );
}
