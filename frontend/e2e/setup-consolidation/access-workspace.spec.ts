import { movedSectionSpecs } from './fixture';

// Users & access, the workspace, and Opening balances: User Management and Tenant Admin are whole pages that moved.

const SECURITY = {
  id: 'security-1', tenantId: 'tenant', passwordMinLength: 12, passwordRequireUppercase: true, passwordRequireLowercase: true,
  passwordRequireDigit: true, passwordRequireSpecial: true, passwordExpiryDays: 0, passwordHistoryCount: 5, maxFailedLoginAttempts: 5,
  lockoutDurationMinutes: 15, sessionTimeoutMinutes: 60, sessionMaxHours: 12, refreshTokenExpiryDays: 14, allowMultipleSessions: true,
  mfaRequired: false, mfaTrustedDeviceDays: 30, updatedAtUtc: '2026-10-01T09:00:00Z',
};
const ROLES = [{ id: 'role-admin', name: 'Admin', description: 'Everything', isSystemRole: true, isActive: true, authorityLevel: 1, permissions: ['security.manage'] }];
const USERS = { items: [], total: 0, page: 1, pageSize: 20 };
const SUBSCRIPTION = {
  plan: 'Professional', status: 'Active', billingCycle: 'Monthly', monthlyAmount: 1200, currencyCode: 'SAR', billingEmail: 'billing@fixture.test',
  maxEmployees: 250, startedAtUtc: '2026-01-01T00:00:00Z', expiresAtUtc: null,
};

movedSectionSpecs([
  { id: 'users', label: 'Users', group: 'Users & access', old: '/user-management', ready: page => page.getByRole('button', { name: /New user|Add user|Invite/i }).first(),
    mocks: { '/api/access/users': USERS, '/api/access/roles': ROLES, '/api/tenant-admin/usage': { activeUsers: 1, maxUsers: 20, activeEmployees: 1, maxEmployees: 100, storageUsedMb: 1 } } },
  { id: 'roles', label: 'Roles', group: 'Users & access', old: '/user-management?tab=roles', ready: page => page.getByRole('button', { name: /role/i }).first(),
    mocks: { '/api/access/roles': ROLES } },
  { id: 'permissionMatrix', label: 'Permission matrix', group: 'Users & access', old: '/user-management?tab=permissionMatrix', ready: page => page.getByRole('table').first(),
    mocks: { '/api/access/permission-matrix': { roles: ROLES, matrix: [{ permissionKey: 'security.manage', module: 'Security', description: 'Manage security settings', roles: { Admin: true } }] } } },
  { id: 'permissions', label: 'Permissions', group: 'Users & access', old: '/user-management?tab=permissions', ready: page => page.getByRole('searchbox').or(page.getByRole('textbox')).first() },
  { id: 'permissionGrantors', label: 'Permission grantors', group: 'Users & access', old: '/user-management?tab=grantors', ready: page => page.getByRole('button', { name: /grantor|Add/i }).first() },
  { id: 'approvalAuthorities', label: 'Approval authorities', group: 'Users & access', old: '/user-management?tab=authorities', ready: page => page.getByRole('button', { name: /authority|Add/i }).first() },
  { id: 'approvalDelegations', label: 'Approval delegations', group: 'Users & access', old: '/user-management?tab=delegations', ready: page => page.getByRole('button', { name: /delegation|Add/i }).first() },
  { id: 'signInSecurity', label: 'Sign-in security', group: 'Users & access', old: '/tenant-admin?tab=security', ready: page => page.getByRole('button', { name: /Save/i }).first(),
    mocks: { '/api/access/security-settings': SECURITY } },
  { id: 'accessLog', label: 'Sign-in and access log', group: 'Users & access', old: '/user-management?tab=auditLogs', ready: page => page.getByRole('table').or(page.getByText(/No .*log|No entries/i)).first() },
  { id: 'localization', label: 'Language and region', group: 'Workspace', old: '/tenant-admin?tab=localization', ready: page => page.getByRole('button', { name: /Save/i }).first(),
    mocks: { '/api/tenant-admin/localization': {
      defaultLanguage: 'en', rtlEnabled: false, calendarSystem: 'Gregorian', defaultTimezone: 'Asia/Riyadh', dateFormat: 'dd/MM/yyyy',
      currencyCode: 'SAR', countryCode: 'SA', weekStartDay: 'Sunday', workWeek: 'Sun-Thu', hijriDatesEnabled: false,
    } } },
  { id: 'branding', label: 'Branding', group: 'Workspace', old: '/tenant-admin?tab=branding', ready: page => page.getByRole('button', { name: /Save/i }).first(),
    mocks: { '/api/tenant-admin/branding': {
      logoUrl: '', primaryColor: '#2563EB', accentColor: '#7C3AED', companyNameEn: 'Workspace Fixture LLC', companyNameAr: '', portalTitle: 'HR Portal', faviconUrl: '',
    } } },
  { id: 'helpText', label: 'Field help text', group: 'Workspace', old: '/tenant-admin?tab=help-text', ready: page => page.getByRole('button', { name: /Add|New/i }).first() },
  { id: 'modules', label: 'Modules', group: 'Workspace', old: '/tenant-admin?tab=modules', ready: page => page.getByRole('switch').or(page.getByRole('checkbox')).first(),
    mocks: { '/api/tenant-modules': { countryCode: 'SA', modules: [
      { key: 'leave', labelEn: 'Leave', labelAr: 'الإجازات', description: 'Leave requests and balances.', enabled: true, canDisable: true, lockClass: 'Optional', lockReason: null },
    ] } } },
  { id: 'planBilling', label: 'Plan and billing', group: 'Workspace', old: '/tenant-admin', ready: page => page.getByText(/Subscription|Plan/i).first(),
    mocks: {
      '/api/tenant-admin/subscription': SUBSCRIPTION,
      '/api/tenant-admin/usage': { activeUsers: 3, maxUsers: 20, activeEmployees: 40, maxEmployees: 250, storageUsedMb: 12 },
      '/api/tenant-admin/subscription/usage': {
        plan: 'Professional', status: 'Active', billingCycle: 'Monthly', monthlyAmount: 1200, currencyCode: 'SAR',
        limits: { maxEmployees: 250, maxUsers: 20, maxCompanies: 3, maxAdminUsers: 5 },
        usage: { activeEmployees: 40, totalUsers: 3, totalCompanies: 1, aiTokensThisMonth: 0 }, featureFlags: { payroll: true },
      },
      '/api/tenant-admin/ai-usage': { tokensUsed: 10, monthlyTokenLimit: 1000, isUnlimited: false, usagePct: 1, requestCount: 2, blockedCount: 0, plan: 'Professional' },
    } },
  // Opening balances opens on its first step ("Choose sections"), whose next step is "Attach files".
  { id: 'openingBalances', label: 'Opening balances', group: 'Get started', old: '/opening-balances', ready: page => page.getByRole('button', { name: 'Attach files' }) },
]);
