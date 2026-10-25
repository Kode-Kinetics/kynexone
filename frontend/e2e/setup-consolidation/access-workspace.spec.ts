import { movedSectionSpecs } from './fixture';

// Users & access, the workspace, and Opening balances: User Management and Tenant Admin are whole pages that moved.
movedSectionSpecs([
  { id: 'users', label: 'Users', group: 'Users & access', old: '/user-management', ready: page => page.getByRole('button', { name: /New user|Add user|Invite/i }).first() },
  { id: 'roles', label: 'Roles', group: 'Users & access', old: '/user-management?tab=roles', ready: page => page.getByRole('button', { name: /role/i }).first() },
  { id: 'permissionMatrix', label: 'Permission matrix', group: 'Users & access', old: '/user-management?tab=permissionMatrix', ready: page => page.getByRole('table').first() },
  { id: 'permissions', label: 'Permissions', group: 'Users & access', old: '/user-management?tab=permissions', ready: page => page.getByRole('searchbox').or(page.getByRole('textbox')).first() },
  { id: 'permissionGrantors', label: 'Permission grantors', group: 'Users & access', old: '/user-management?tab=grantors', ready: page => page.getByRole('button', { name: /grantor|Add/i }).first() },
  { id: 'approvalAuthorities', label: 'Approval authorities', group: 'Users & access', old: '/user-management?tab=authorities', ready: page => page.getByRole('button', { name: /authority|Add/i }).first() },
  { id: 'approvalDelegations', label: 'Approval delegations', group: 'Users & access', old: '/user-management?tab=delegations', ready: page => page.getByRole('button', { name: /delegation|Add/i }).first() },
  { id: 'signInSecurity', label: 'Sign-in security', group: 'Users & access', old: '/tenant-admin?tab=security', ready: page => page.getByRole('button', { name: /Save/i }).first() },
  { id: 'accessLog', label: 'Sign-in and access log', group: 'Users & access', old: '/user-management?tab=auditLogs', ready: page => page.getByRole('table').or(page.getByText(/No .*log|No entries/i)).first() },
  { id: 'localization', label: 'Language and region', group: 'Workspace', old: '/tenant-admin?tab=localization', ready: page => page.getByRole('button', { name: /Save/i }).first() },
  { id: 'branding', label: 'Branding', group: 'Workspace', old: '/tenant-admin?tab=branding', ready: page => page.getByRole('button', { name: /Save/i }).first() },
  { id: 'helpText', label: 'Field help text', group: 'Workspace', old: '/tenant-admin?tab=help-text', ready: page => page.getByRole('button', { name: /Add|New/i }).first() },
  { id: 'modules', label: 'Modules', group: 'Workspace', old: '/tenant-admin?tab=modules', ready: page => page.getByRole('switch').or(page.getByRole('checkbox')).first() },
  { id: 'planBilling', label: 'Plan and billing', group: 'Workspace', old: '/tenant-admin', ready: page => page.getByText(/Subscription|Plan/i).first() },
  { id: 'openingBalances', label: 'Opening balances', group: 'Get started', old: '/opening-balances', ready: page => page.getByRole('button', { name: /Upload|Import|Validate/i }).first() },
]);
