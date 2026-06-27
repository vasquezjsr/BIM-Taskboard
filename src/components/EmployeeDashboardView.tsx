import { useMemo, useState } from 'react';
import { useStore } from '../store/useStore';
import {
  canAssignDetailerTrade,
  getEmployeeManagers,
  inferOrgCategory,
} from '../utils/orgChart';
import { canManageOrg } from '../utils/permissions';
import {
  DETAILER_ORG_CATEGORIES,
  EMPLOYEE_ROLES,
  type Employee,
  type EmployeeRole,
  type OrgCategory,
  type DashboardType,
} from '../types';
import {
  EMPLOYEE_STAGES,
  detailerTradeGroups,
  employeesForStage,
  stageCounts,
  type EmployeeStageId,
} from '../utils/employeeDashboardStages';
import {
  createDefaultDashboardAssignments,
  dashboardRolesFor,
  DASHBOARD_META,
} from '../data/dashboards';
import { employeeAssigneeStyle, employeeInitials, isOwnerEmployee, isProtectedRosterEmployee } from '../data/employees';
import { isValidEmail } from '../utils/auth';
import { EmployeeInviteModal, type EmployeeInviteDetails } from './EmployeeInviteModal';
import { PermissionToggles, WorksUnderPicker } from './EmployeeFormControls';
import formStyles from './EmployeeManagementDialog.module.css';
import styles from './EmployeeDashboardView.module.css';

export function EmployeeDashboardView() {
  const employees = useStore((s) => s.employees);
  const employeeReportsTo = useStore((s) => s.employeeReportsTo);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const employeeCredentials = useStore((s) => s.employeeCredentials);
  const dashboardAssignments = useStore(
    (s) => s.dashboardAssignments ?? createDefaultDashboardAssignments()
  );
  const currentUserId = useStore((s) => s.currentUserId);
  const viewAsOriginalUserId = useStore((s) => s.viewAsOriginalUserId);
  const assigneeStyles = useStore((s) => s.employeeAssigneeStyles);
  const addEmployee = useStore((s) => s.addEmployee);
  const removeEmployee = useStore((s) => s.removeEmployee);
  const updateEmployee = useStore((s) => s.updateEmployee);
  const updateEmployeeEmail = useStore((s) => s.updateEmployeeEmail);
  const toggleEmployeeManager = useStore((s) => s.toggleEmployeeManager);
  const setEmployeePermission = useStore((s) => s.setEmployeePermission);
  const assignDashboardMember = useStore((s) => s.assignDashboardMember);
  const unassignDashboardMember = useStore((s) => s.unassignDashboardMember);

  const [activeStage, setActiveStage] = useState<EmployeeStageId>('leadership');
  const [pickerRole, setPickerRole] = useState<{ dashboard: DashboardType; roleId: string } | null>(
    null
  );
  const [newName, setNewName] = useState('');
  const [newEmail, setNewEmail] = useState('');
  const [newRole, setNewRole] = useState<EmployeeRole>('detailer');
  const [newDetailerType, setNewDetailerType] = useState<OrgCategory>('plumbing-detailer');
  const [pendingInvite, setPendingInvite] = useState<EmployeeInviteDetails | null>(null);

  const memberIds = useMemo(() => employees.map((employee) => employee.id), [employees]);
  const editorUserId = viewAsOriginalUserId ?? currentUserId;
  const canManage = canManageOrg(editorUserId, employees, employeePermissions);
  const counts = useMemo(() => stageCounts(employees, dashboardAssignments), [employees, dashboardAssignments]);
  const stageMeta = EMPLOYEE_STAGES.find((stage) => stage.id === activeStage)!;
  const stageEmployees = useMemo(
    () => employeesForStage(activeStage, employees, dashboardAssignments),
    [activeStage, employees, dashboardAssignments]
  );

  const trimmedName = newName.trim();
  const trimmedEmail = newEmail.trim();
  const canAdd = trimmedName.length > 0 && isValidEmail(trimmedEmail);

  const handleAdd = () => {
    if (!canAdd) return;
    const invite = addEmployee(
      trimmedName,
      newRole,
      newRole === 'detailer' ? newDetailerType : undefined,
      trimmedEmail
    );
    setNewName('');
    setNewEmail('');
    setPendingInvite(invite);
    if (newRole === 'detailer') setActiveStage('detailers');
    else if (newRole === 'operations') setActiveStage('pm-ops');
    else if (newRole === 'support-specialist') setActiveStage('support');
  };

  const renderRoleLabel = (emp: Employee) => {
    if (isOwnerEmployee(emp)) return 'Owner';
    if (emp.role === 'operations') return 'Operations';
    if (emp.role === 'support-specialist') {
      return inferOrgCategory(emp) === 'support-manager'
        ? 'Support Manager'
        : 'Support Specialist';
    }
    const category = DETAILER_ORG_CATEGORIES.find((entry) => entry.id === inferOrgCategory(emp));
    return category?.label ?? 'Detailer';
  };

  const renderRoleField = (emp: Employee) => {
    if (isOwnerEmployee(emp)) {
      return <span className={formStyles.roleBadge}>Owner</span>;
    }
    if (emp.role === 'operations') {
      return <span className={formStyles.roleBadge}>Operations</span>;
    }
    if (emp.role === 'support-specialist') {
      if (!canManage) return <span className={formStyles.cellText}>Support Specialist</span>;
      return (
        <select
          className={formStyles.cellSelect}
          value={emp.role}
          onChange={(e) => updateEmployee(emp.id, { role: e.target.value as EmployeeRole })}
        >
          {EMPLOYEE_ROLES.map((role) => (
            <option key={role.id} value={role.id}>
              {role.label}
            </option>
          ))}
        </select>
      );
    }
    if (canAssignDetailerTrade(emp)) {
      if (!canManage) {
        return <span className={formStyles.cellText}>{renderRoleLabel(emp)}</span>;
      }
      return (
        <select
          className={formStyles.cellSelect}
          value={inferOrgCategory(emp)}
          onChange={(e) =>
            updateEmployee(emp.id, { orgCategory: e.target.value as OrgCategory })
          }
        >
          {DETAILER_ORG_CATEGORIES.map((category) => (
            <option key={category.id} value={category.id}>
              {category.label}
            </option>
          ))}
        </select>
      );
    }
    return <span className={formStyles.roleBadge}>{renderRoleLabel(emp)}</span>;
  };

  const renderEmployeeCard = (emp: Employee) => {
    const managerIds = getEmployeeManagers(emp.id, memberIds, employeeReportsTo);
    const managerOptions = employees
      .filter((entry) => entry.id !== emp.id)
      .sort((a, b) => a.name.localeCompare(b.name));
    const permissions = employeePermissions[emp.id] ?? [];
    const email = employeeCredentials[emp.id]?.email ?? '';
    const badge = employeeAssigneeStyle(emp.id, assigneeStyles);

    return (
      <article key={emp.id} className={styles.employeeCard}>
        <div className={styles.cardTop}>
          <span
            className={styles.avatar}
            style={{
              borderColor: badge.border,
              background: badge.background,
              color: badge.text,
            }}
          >
            {employeeInitials(emp.name)}
          </span>
          <div className={styles.cardIdentity}>
            {canManage ? (
              <input
                className={formStyles.cellInput}
                defaultValue={emp.name}
                key={`${emp.id}-${emp.name}`}
                aria-label={`Name for ${emp.name}`}
                onBlur={(e) => {
                  const next = e.target.value.trim();
                  if (next && next !== emp.name) updateEmployee(emp.id, { name: next });
                  else e.target.value = emp.name;
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') e.currentTarget.blur();
                }}
              />
            ) : (
              <div className={styles.cardName}>{emp.name}</div>
            )}
            <div className={styles.cardRole}>{renderRoleLabel(emp)}</div>
          </div>
          {canManage && !isProtectedRosterEmployee(emp.id) && (
            <button
              type="button"
              className={styles.removeBtn}
              onClick={() => removeEmployee(emp.id)}
              title={`Remove ${emp.name}`}
              aria-label={`Remove ${emp.name}`}
            >
              ×
            </button>
          )}
        </div>

        <div className={styles.fieldGrid}>
          <span className={styles.fieldLabel}>Email</span>
          <div>
            {canManage ? (
              <input
                className={formStyles.cellInput}
                type="email"
                defaultValue={email}
                key={`${emp.id}-${email}`}
                placeholder="Add email"
                onBlur={(e) => {
                  const next = e.target.value.trim();
                  if (!next) {
                    e.target.value = email;
                    return;
                  }
                  if (isValidEmail(next) && next !== email) updateEmployeeEmail(emp.id, next);
                  else e.target.value = email;
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') e.currentTarget.blur();
                }}
              />
            ) : (
              <span className={formStyles.cellText}>{email || '—'}</span>
            )}
          </div>

          <span className={styles.fieldLabel}>Role</span>
          <div>{renderRoleField(emp)}</div>

          <span className={styles.fieldLabel}>Reports to</span>
          <div>
            <WorksUnderPicker
              employeeId={emp.id}
              managerIds={managerIds}
              managerOptions={managerOptions}
              onToggleManager={(managerId, enabled) =>
                toggleEmployeeManager(emp.id, managerId, enabled)
              }
              disabled={!canManage}
            />
          </div>
        </div>

        <div className={styles.permissionsBlock}>
          <span className={styles.fieldLabel}>Permissions</span>
          <PermissionToggles
            permissions={permissions}
            onToggle={(permission, enabled) =>
              setEmployeePermission(emp.id, permission, enabled)
            }
            disabled={!canManage}
            compact
          />
        </div>
      </article>
    );
  };

  const renderDashboardRoles = (dashboard: DashboardType) => {
    const roles = dashboardRolesFor(dashboard);
    const assignedIds = new Set(Object.values(dashboardAssignments[dashboard]).flatMap((ids) => ids));
    const availableEmployees = employees.filter((employee) => !assignedIds.has(employee.id));

    return (
      <section className={styles.rolePanel}>
        <h3 className={styles.rolePanelTitle}>{DASHBOARD_META[dashboard].label} roles</h3>
        <div className={styles.roleGrid}>
          {roles.map((role) => {
            const memberIdsForRole =
              (dashboardAssignments[dashboard] as Record<string, string[]>)[role.id] ?? [];
            return (
              <div key={role.id} className={styles.roleSlot}>
                <div className={styles.roleSlotHeader}>
                  <h4>{role.label}</h4>
                  {canManage && (
                    <button
                      type="button"
                      className={styles.addBtn}
                      onClick={() => setPickerRole({ dashboard, roleId: role.id })}
                    >
                      + Add
                    </button>
                  )}
                </div>
                <div className={styles.memberList}>
                  {memberIdsForRole.length === 0 ? (
                    <p className={styles.emptyRole}>Unassigned</p>
                  ) : (
                    memberIdsForRole.map((memberId) => {
                      const employee = employees.find((entry) => entry.id === memberId);
                      if (!employee) return null;
                      const badge = employeeAssigneeStyle(memberId, assigneeStyles);
                      return (
                        <div key={memberId} className={styles.memberChip}>
                          <span
                            className={styles.avatar}
                            style={{
                              borderColor: badge.border,
                              background: badge.background,
                              color: badge.text,
                            }}
                          >
                            {employeeInitials(employee.name)}
                          </span>
                          <span className={styles.memberName}>{employee.name}</span>
                          {canManage && (
                            <button
                              type="button"
                              className={styles.removeBtn}
                              onClick={() =>
                                unassignDashboardMember(dashboard, role.id, memberId)
                              }
                              aria-label={`Remove ${employee.name}`}
                            >
                              ×
                            </button>
                          )}
                        </div>
                      );
                    })
                  )}
                </div>
              </div>
            );
          })}
        </div>
        {pickerRole?.dashboard === dashboard && canManage && (
          <div className={styles.pickerOverlay} onClick={() => setPickerRole(null)}>
            <div className={styles.pickerModal} onClick={(e) => e.stopPropagation()}>
              <h4>
                Add to{' '}
                {roles.find((role) => role.id === pickerRole.roleId)?.label ?? 'role'}
              </h4>
              {availableEmployees.length === 0 ? (
                <p className={styles.emptyRole}>
                  Everyone is already assigned somewhere on this dashboard.
                </p>
              ) : (
                <ul className={styles.pickerList}>
                  {availableEmployees.map((employee) => (
                    <li key={employee.id}>
                      <button
                        type="button"
                        className={styles.pickerItem}
                        onClick={() => {
                          assignDashboardMember(dashboard, pickerRole.roleId, employee.id);
                          setPickerRole(null);
                        }}
                      >
                        {employee.name}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              <button type="button" className={styles.cancelBtn} onClick={() => setPickerRole(null)}>
                Cancel
              </button>
            </div>
          </div>
        )}
      </section>
    );
  };

  const renderStageContent = () => {
    if (stageEmployees.length === 0 && !stageMeta.dashboard) {
      return <p className={styles.emptyStage}>No employees in this stage yet.</p>;
    }

    if (activeStage === 'detailers') {
      const groups = detailerTradeGroups(stageEmployees);
      return (
        <div className={styles.tradeGrid}>
          {groups.map((group) => (
            <div key={group.category} className={styles.tradeColumn}>
              <h3 className={styles.tradeHeading}>{group.label}</h3>
              {group.employees.map(renderEmployeeCard)}
            </div>
          ))}
        </div>
      );
    }

    return (
      <>
        {stageMeta.dashboard && renderDashboardRoles(stageMeta.dashboard)}
        {stageEmployees.length > 0 ? (
          <div className={styles.cardGrid}>{stageEmployees.map(renderEmployeeCard)}</div>
        ) : (
          <p className={styles.emptyStage}>Assign operations staff using the roles above.</p>
        )}
      </>
    );
  };

  const officeStages = EMPLOYEE_STAGES.filter((stage) => stage.lane === 'office');
  const opsStages = EMPLOYEE_STAGES.filter((stage) => stage.lane === 'operations');
  const totalEmployees = employees.length;

  return (
    <div className={styles.wrapper}>
      <header className={styles.topBar}>
        <div className={styles.titleBlock}>
          <h1>Employees</h1>
          <p>
            Roster, reporting lines, permissions, and operations assignments — organized by
            workforce stage so office and field teams stay separate.
          </p>
        </div>
        <div className={styles.stats}>
          <span className={styles.statChip}>
            Total<strong>{totalEmployees}</strong>
          </span>
          <span className={styles.statChip}>
            Office
            <strong>
              {officeStages.reduce((sum, stage) => sum + counts[stage.id], 0)}
            </strong>
          </span>
          <span className={styles.statChip}>
            Operations
            <strong>{opsStages.reduce((sum, stage) => sum + counts[stage.id], 0)}</strong>
          </span>
        </div>
      </header>

      {!canManage && (
        <p className={styles.readOnlyBanner}>
          You can browse the roster and permissions. Only owners and org managers can edit
          employees.
        </p>
      )}

      <div className={styles.body}>
        <nav className={styles.rail} aria-label="Employee stages">
          <div className={styles.laneGroup}>
            <span className={styles.laneLabel}>Office</span>
            {officeStages.map((stage) => (
              <button
                key={stage.id}
                type="button"
                className={`${styles.stageBtn} ${
                  activeStage === stage.id ? styles.stageBtnActiveOffice : ''
                }`}
                onClick={() => setActiveStage(stage.id)}
              >
                <span>{stage.label}</span>
                <span className={styles.stageCount}>{counts[stage.id]}</span>
              </button>
            ))}
          </div>
          <div className={styles.laneGroup}>
            <span className={styles.laneLabel}>Operations</span>
            {opsStages.map((stage) => (
              <button
                key={stage.id}
                type="button"
                className={`${styles.stageBtn} ${
                  activeStage === stage.id ? styles.stageBtnActiveOps : ''
                }`}
                onClick={() => setActiveStage(stage.id)}
              >
                <span>{stage.label}</span>
                <span className={styles.stageCount}>{counts[stage.id]}</span>
              </button>
            ))}
          </div>
        </nav>

        <section className={styles.main}>
          <header
            className={`${styles.stageHeader} ${
              stageMeta.lane === 'office' ? styles.stageHeaderOffice : styles.stageHeaderOps
            }`}
          >
            <div className={styles.stageEyebrow}>{stageMeta.laneLabel}</div>
            <h2 className={styles.stageTitle}>{stageMeta.label}</h2>
            <p className={styles.stageDescription}>{stageMeta.description}</p>
          </header>

          <div className={styles.scrollArea}>
            {renderStageContent()}

            {canManage && stageMeta.lane === 'office' && (
              <div className={styles.addBar}>
                <span className={styles.addBarLabel}>Add to roster</span>
                <input
                  className={formStyles.addInput}
                  placeholder="Name"
                  value={newName}
                  onChange={(e) => setNewName(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && handleAdd()}
                />
                <input
                  className={formStyles.addEmailInput}
                  type="email"
                  placeholder="Email"
                  value={newEmail}
                  onChange={(e) => setNewEmail(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && canAdd && handleAdd()}
                />
                <select
                  className={formStyles.footerSelect}
                  value={newRole}
                  onChange={(e) => setNewRole(e.target.value as EmployeeRole)}
                >
                  {EMPLOYEE_ROLES.map((role) => (
                    <option key={role.id} value={role.id}>
                      {role.label}
                    </option>
                  ))}
                </select>
                {newRole === 'detailer' && (
                  <select
                    className={formStyles.footerSelect}
                    value={newDetailerType}
                    onChange={(e) => setNewDetailerType(e.target.value as OrgCategory)}
                  >
                    {DETAILER_ORG_CATEGORIES.map((category) => (
                      <option key={category.id} value={category.id}>
                        {category.label}
                      </option>
                    ))}
                  </select>
                )}
                <button
                  type="button"
                  className={formStyles.addBtn}
                  onClick={handleAdd}
                  disabled={!canAdd}
                >
                  Add
                </button>
              </div>
            )}
          </div>
        </section>
      </div>

      {pendingInvite && (
        <EmployeeInviteModal invite={pendingInvite} onClose={() => setPendingInvite(null)} />
      )}
    </div>
  );
}
