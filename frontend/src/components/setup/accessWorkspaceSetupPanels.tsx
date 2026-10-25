'use client';

import dynamic from 'next/dynamic';
import type { ComponentType } from 'react';
import type { ModuleSetupPanelProps } from './ModuleSetupSection';

/**
 * The Setup sections of Users & access, the Workspace, and Opening balances (lib/setupModuleSections.ts): each is the
 * screen's own editor, loaded only when its section is opened. Users & access are the tabs of User Management; the
 * Workspace panels are what Tenant Administration held; Field help text is components/HelpTextManager.tsx.
 */
type Panel = ComponentType<ModuleSetupPanelProps>;

// Opening balances
export const OpeningBalances: Panel = dynamic(() => import('../../views/MigrationImportPage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.MigrationImportPage embedded onDirtyChange={onDirtyChange} />;
  return Panel;
}));

// Users & access. UsersTab reads `search`, `openCreate`, `returnTo`, `companyId` and `purpose` from the address, so
// /setup?tab=users&companyId=…&openCreate=1&purpose=companyAdmin (the readiness link) opens the guided create dialog.
export const Users: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = () => <module.UsersTab />;
  return Panel;
}));
export const Roles: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = () => <module.RolesTab />;
  return Panel;
}));
export const PermissionMatrix: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = () => <module.PermissionMatrixTab />;
  return Panel;
}));
export const Permissions: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = () => <module.PermissionsTab />;
  return Panel;
}));
export const PermissionGrantors: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = () => <module.PermissionGrantorsTab />;
  return Panel;
}));
export const ApprovalAuthorities: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = () => <module.AuthoritiesTab />;
  return Panel;
}));
export const ApprovalDelegations: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = () => <module.DelegationsTab />;
  return Panel;
}));
export const SignInSecurity: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.SignInSecurityTab onDirtyChange={onDirtyChange} />;
  return Panel;
}));
export const AccessLog: Panel = dynamic(() => import('../../views/UserManagementPage').then(module => {
  const Panel = () => <module.AuditLogsTab />;
  return Panel;
}));

// Workspace
export const Localization: Panel = dynamic(() => import('../../views/TenantAdminPage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.LocalizationPanel onDirtyChange={onDirtyChange} />;
  return Panel;
}));
export const Branding: Panel = dynamic(() => import('../../views/TenantAdminPage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.BrandingPanel onDirtyChange={onDirtyChange} />;
  return Panel;
}));
export const HelpText: Panel = dynamic(() => import('../HelpTextManager').then(module => {
  const Panel = () => <module.HelpTextManager />;
  return Panel;
}));
export const Modules: Panel = dynamic(() => import('../../views/TenantAdminPage').then(module => {
  const Panel = () => <module.ModulesPanel />;
  return Panel;
}));
export const PlanBilling: Panel = dynamic(() => import('../../views/TenantAdminPage').then(module => {
  const Panel = () => <module.PlanBillingPanel />;
  return Panel;
}));
export const CountryPayrollKeys: Panel = dynamic(() => import('../../views/TenantAdminPage').then(module => {
  const Panel = () => <module.CountryPayrollKeysPanel />;
  return Panel;
}));
