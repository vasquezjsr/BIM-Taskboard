import type { DashboardAssignments, DashboardType } from '../types';
import { createSeededDashboardAssignments } from './departmentStaff';

export const PM_DASHBOARD_ROLES = [
  { id: 'project-manager' as const, label: 'Project Managers' },
  { id: 'assistant-pm' as const, label: 'Assistant PMs' },
];

export const FIELD_DASHBOARD_ROLES = [
  { id: 'site-superintendent' as const, label: 'Site Superintendents' },
  { id: 'foreman' as const, label: 'Foremen' },
  { id: 'crew-lead' as const, label: 'Crew Leads' },
];

export const FAB_DASHBOARD_ROLES = [
  { id: 'shop-super' as const, label: 'Shop Super' },
  { id: 'dept-manager-mech' as const, label: 'Shop Dept Manager (Mech)' },
  { id: 'dept-manager-plmb' as const, label: 'Shop Dept Manager (Plmb)' },
  { id: 'dept-manager-hvac' as const, label: 'Shop Dept Manager (HVAC)' },
  { id: 'worker' as const, label: 'Workers' },
];

export const SHIPPING_DASHBOARD_ROLES = [
  { id: 'shipping-manager' as const, label: 'Shipping Manager' },
  { id: 'worker' as const, label: 'Workers' },
];

export const DASHBOARD_META: Record<
  DashboardType,
  { label: string; permission: import('../types').AppPermission; boardType: import('../types').ProjectBoardType }
> = {
  pm: { label: 'PM Dashboard', permission: 'view-pm-dashboard', boardType: 'project-managers' },
  field: { label: 'Field Dashboard', permission: 'view-field-dashboard', boardType: 'field' },
  fab: { label: 'Fab Dashboard', permission: 'view-fab-dashboard', boardType: 'fab' },
  shipping: { label: 'Shipping Dashboard', permission: 'view-shipping-dashboard', boardType: 'shipping' },
};

export function createDefaultDashboardAssignments(): DashboardAssignments {
  return createSeededDashboardAssignments();
}

export function dashboardRolesFor(type: DashboardType) {
  if (type === 'pm') return PM_DASHBOARD_ROLES;
  if (type === 'field') return FIELD_DASHBOARD_ROLES;
  if (type === 'fab') return FAB_DASHBOARD_ROLES;
  return SHIPPING_DASHBOARD_ROLES;
}

export function boardTypeForDashboard(dashboard: DashboardType) {
  return DASHBOARD_META[dashboard].boardType;
}
