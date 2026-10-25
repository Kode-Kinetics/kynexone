import { expect, test } from '@playwright/test';
import { SETUP_MODULE_SECTIONS, moduleSectionAvailable, type SetupModuleAccess } from '../src/lib/setupModuleSections';

/**
 * Who may open each Users & access, Workspace and Opening balances section in Setup. A section opens for what its old
 * page opened for, narrowed to what the API serves, so nobody is shown a screen that answers 403 on load and nobody is
 * given a screen they never had. Each case cites the backend rule (paths under backend-dotnet/Zayra.Api).
 */

const persona = (roles: string[], permissions: string[]): SetupModuleAccess => ({
  hasPermission: key => permissions.includes(key),
  hasRole: role => roles.includes(role),
  isFeatureEnabled: () => true,
  verdictForPath: () => ({ allowed: true }),
  flagStatus: 'ready',
});
const canOpen = (id: string, access: SetupModuleAccess) => {
  const found = SETUP_MODULE_SECTIONS.find(section => section.id === id);
  expect(found, `Missing Setup section ${id}`).toBeDefined();
  return moduleSectionAvailable(found!, access);
};

// The seeded Admin holds every permission but the withheld data-access keys (Infrastructure/Seed/AuthSeeder.cs:38,65-80).
const admin = persona(['Admin'], ['security.manage', 'users.manage', 'roles.manage', 'audit.read', 'employees.write', 'payroll.write', 'employees.bulk_import']);
// HR Director holds roles.manage, users.manage and audit.read but NOT security.manage (AuthSeeder.cs:181-190).
const hrDirector = persona(['HR Director'], ['users.manage', 'roles.manage', 'audit.read', 'employees.read', 'employees.write', 'payroll.read']);
// HR Manager: employees.*, audit.read, payroll.read/write; no security.manage (AuthSeeder.cs:217-223).
const hrManager = persona(['HR Manager'], ['employees.write', 'employees.bulk_import', 'payroll.read', 'payroll.write', 'audit.read']);
const hrOfficer = persona(['HR Officer'], ['employees.read', 'employees.write', 'employees.bulk_import']);
// A custom "Console Admin": the one-key way to mint an administrator (AccessController.cs:19-23), without audit.read.
const consoleAdmin = persona(['Console Admin'], ['security.manage']);
const employee = persona(['Employee'], ['ess.read', 'leave.read', 'loans.self']);

test('the_access_sections_open_for_security_manage_only_never_for_users_manage_or_roles_manage_alone', () => {
  // AccessController.cs:18-24: the whole controller is [HasPermission("security.manage")], and no action loosens it except
  // the two grant-permission endpoints (:926,:946), which this screen does not call. So Users, Roles, the matrix,
  // Permissions, Grantors, Authorities, Delegations and Sign-in security (:963,:973) all answer 403 to users.manage or
  // roles.manage alone. The old route opened for those two as well and then failed on load; the sections do not.
  for (const id of ['users', 'roles', 'permissionMatrix', 'permissions', 'permissionGrantors', 'approvalAuthorities', 'approvalDelegations', 'signInSecurity']) {
    expect(canOpen(id, admin), `${id} for Admin`).toBe(true);
    expect(canOpen(id, consoleAdmin), `${id} for a custom role holding security.manage`).toBe(true);
    expect(canOpen(id, hrDirector), `${id} for HR Director (users.manage, roles.manage)`).toBe(false);
    expect(canOpen(id, persona(['Custom'], ['roles.manage'])), `${id} for roles.manage alone`).toBe(false);
    expect(canOpen(id, hrManager), `${id} for HR Manager`).toBe(false);
    expect(canOpen(id, employee), `${id} for Employee`).toBe(false);
  }
});

test('the_access_log_opens_for_an_Admin_who_holds_security_manage_and_audit_read', () => {
  // AuditLogsController.cs:10-11,42-45: GET /api/audit-logs and /integrity are [Authorize(Roles = "Admin")], and the role
  // gate is enforced as audit.read (LegacyRolePermissionAuthorization.cs:57,178-190). The old tab needed one of
  // users/roles/security.manage on top, and the seeded Admin holds all three. Offering it on audit.read alone would open it
  // for HR Manager and Auditor, who never had it (and raw detail is security.manage only, AuditLogsController.cs:29), and
  // offering it on security.manage alone would show a 403 to a custom role without audit.read. So: both, in effect.
  expect(canOpen('accessLog', admin)).toBe(true);
  expect(canOpen('accessLog', hrDirector)).toBe(false);
  expect(canOpen('accessLog', hrManager)).toBe(false);
  expect(canOpen('accessLog', consoleAdmin)).toBe(false);
  expect(canOpen('accessLog', employee)).toBe(false);
});

test('the_workspace_sections_open_for_security_manage_only', () => {
  // TenantAdminController.cs:13-17: [HasPermission("security.manage")] on the controller, covering subscription, usage,
  // AI usage, invoices (:26-:464), localization (:152,:240; only the anonymous GET is open), branding (:274,:286) and
  // country rules (:317-:417). Modules: TenantModulesController.cs:39-40 is the same permission. Field help text: the
  // list is any signed-in user (HelpTextController.cs:17,26) but upsert and delete are [Authorize(Roles = "Admin")]
  // (:40,:63), enforced as security.manage (LegacyRolePermissionAuthorization.cs:HelpText).
  for (const id of ['planBilling', 'modules', 'localization', 'branding', 'helpText', 'countryPayrollKeys']) {
    expect(canOpen(id, admin), `${id} for Admin`).toBe(true);
    expect(canOpen(id, consoleAdmin), `${id} for a custom role holding security.manage`).toBe(true);
    expect(canOpen(id, hrDirector), `${id} for HR Director`).toBe(false);
    expect(canOpen(id, hrManager), `${id} for HR Manager`).toBe(false);
    expect(canOpen(id, employee), `${id} for Employee`).toBe(false);
  }
});

test('opening_balances_open_for_Admin_and_HR_Manager_only', () => {
  // MigrationImportController.cs:24-25: [Authorize(Roles = "Admin,HR Manager")] on the controller (opening balances are a
  // section of the same /api/migrations engine, MigrationImportController.OpeningBalances.cs), enforced as
  // employees.bulk_import (LegacyRolePermissionAuthorization.cs:MigrationImport). The old route gate was employees.write or
  // payroll.write, and the screen stays for the two roles it was written for; an HR Officer holds the permissions but not
  // the role, and an HR Director holds the permission but was never offered the screen.
  expect(canOpen('openingBalances', admin)).toBe(true);
  expect(canOpen('openingBalances', hrManager)).toBe(true);
  expect(canOpen('openingBalances', hrOfficer)).toBe(false);
  expect(canOpen('openingBalances', hrDirector)).toBe(false);
  expect(canOpen('openingBalances', employee)).toBe(false);
});
