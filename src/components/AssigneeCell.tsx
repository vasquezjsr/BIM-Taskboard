import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { employeeAssigneeStyle, employeeInitials } from '../data/employees';
import { useStore } from '../store/useStore';
import styles from './AssigneeCell.module.css';

interface AssigneeCellProps {
  assigneeIds: string[];
  employees: { id: string; name: string }[];
  assigneesLocked?: boolean;
  onChange: (assigneeIds: string[]) => void;
}

export function AssigneeCell({ assigneeIds, employees, assigneesLocked = false, onChange }: AssigneeCellProps) {
  const employeeAssigneeStyles = useStore((state) => state.employeeAssigneeStyles);
  const ids = assigneeIds ?? [];
  const [open, setOpen] = useState(false);
  const [pickerPos, setPickerPos] = useState<{ top: number; left: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const pickerRef = useRef<HTMLDivElement>(null);

  const selected = employees.filter((emp) => ids.includes(emp.id));

  const updatePickerPosition = useCallback(() => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;
    setPickerPos({
      top: rect.bottom + 4,
      left: rect.left + rect.width / 2,
    });
  }, []);

  useEffect(() => {
    if (!open) return;
    updatePickerPosition();
    window.addEventListener('resize', updatePickerPosition);
    window.addEventListener('scroll', updatePickerPosition, true);
    return () => {
      window.removeEventListener('resize', updatePickerPosition);
      window.removeEventListener('scroll', updatePickerPosition, true);
    };
  }, [open, updatePickerPosition]);

  useEffect(() => {
    if (!open) return;
    const handlePointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (triggerRef.current?.contains(target)) return;
      if (pickerRef.current?.contains(target)) return;
      setOpen(false);
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
        triggerRef.current?.blur();
      }
    };
    const timerId = window.setTimeout(() => {
      document.addEventListener('mousedown', handlePointerDown);
    }, 0);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      window.clearTimeout(timerId);
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [open]);

  const toggleEmployee = (employeeId: string) => {
    if (ids.includes(employeeId)) {
      onChange(ids.filter((id) => id !== employeeId));
      return;
    }
    onChange([...ids, employeeId]);
  };

  const handleToggle = (event: React.MouseEvent<HTMLButtonElement>) => {
    event.stopPropagation();
    if (open) {
      setOpen(false);
      return;
    }
    updatePickerPosition();
    setOpen(true);
  };

  const picker =
    open && pickerPos
      ? createPortal(
          <div
            ref={pickerRef}
            className={styles.picker}
            style={{ top: pickerPos.top, left: pickerPos.left }}
            role="listbox"
            aria-multiselectable
            onMouseDown={(event) => event.stopPropagation()}
            onClick={(event) => event.stopPropagation()}
          >
            {employees.length === 0 ? (
              <div className={styles.pickerEmpty}>No employees available</div>
            ) : (
              employees.map((emp) => {
                const checked = ids.includes(emp.id);
                const badgeStyle = employeeAssigneeStyle(emp.id, employeeAssigneeStyles);
                return (
                  <label key={emp.id} className={styles.pickerOption}>
                    <input
                      type="checkbox"
                      checked={checked}
                      onChange={() => toggleEmployee(emp.id)}
                    />
                    <span
                      className={styles.badge}
                      style={{
                        borderColor: badgeStyle.border,
                        background: badgeStyle.background,
                        color: badgeStyle.text,
                      }}
                    >
                      {employeeInitials(emp.name)}
                    </span>
                    <span className={styles.pickerName}>{emp.name}</span>
                  </label>
                );
              })
            )}
          </div>,
          document.body
        )
      : null;

  return (
    <div className={`${styles.cell} assigneeCell`}>
      <button
        ref={triggerRef}
        type="button"
        className={`${styles.trigger} ${assigneesLocked ? styles.triggerLocked : ''}`}
        onMouseDown={(event) => event.stopPropagation()}
        onClick={handleToggle}
        aria-label={
          selected.length
            ? `Assignees: ${selected.map((emp) => emp.name).join(', ')}${assigneesLocked ? ' (manual, auto-assign paused)' : ''}`
            : assigneesLocked
              ? 'Assign people (manual, auto-assign paused)'
              : 'Assign people'
        }
        aria-expanded={open}
        aria-haspopup="listbox"
      >
        {selected.length > 0 ? (
          <span className={styles.triggerContent}>
            <span className={styles.badgeRow}>
              {selected.map((emp) => {
                const badgeStyle = employeeAssigneeStyle(emp.id, employeeAssigneeStyles);
                return (
                  <span
                    key={emp.id}
                    className={styles.badge}
                    style={{
                      borderColor: badgeStyle.border,
                      background: badgeStyle.background,
                      color: badgeStyle.text,
                    }}
                    title={emp.name}
                  >
                    {employeeInitials(emp.name)}
                  </span>
                );
              })}
            </span>
          </span>
        ) : (
          <span className={styles.triggerContent}>
            <span className={styles.empty} title="Unassigned">
              —
            </span>
          </span>
        )}
        <span className={styles.chevron} aria-hidden>
          ▾
        </span>
      </button>
      {picker}
    </div>
  );
}
