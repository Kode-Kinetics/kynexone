import type { RoleCatalog } from './role-catalog';

/**
 * Separation-of-duties POLICY for the tenant system roles.
 *
 * This is deliberately not a copy of the role matrix — that is generated from AuthSeeder.cs by
 * role-catalog.ts. These are the few statements about the matrix that must stay true whatever else
 * changes, each with the reason it matters. They are checked twice:
 *   • browserless, against the catalog parsed from AuthSeeder.cs (e2e/identity/identity-contract.spec.ts),
 *     so a seeding change that breaks one fails on the PR without a stack;
 *   • in the Chrome security gate, against the permissions each signed-in persona actually holds.
 *
 * Every key a rule names must exist in the catalog (checked by `unknownPolicyKeys`), so a renamed
 * permission cannot turn a "must lack" into a check that passes because the key no longer exists.
 */

export interface PolicyRule {
  id: string;
  /** Why the rule exists, in the product's terms. Printed on failure. */
  why: string;
  /** Permission keys the rule names (for the unknown-key check). */
  keys: string[];
  /** Violations for a role → permissions map; empty when the rule holds. */
  violations(roles: Map<string, Set<string>>, catalog: string[]): string[];
}

const holders = (roles: Map<string, Set<string>>, key: string): string[] =>
  [...roles.entries()].filter(([, perms]) => perms.has(key)).map(([name]) => name).sort();

/** Only the listed roles may hold `key`. */
const onlyHeldBy = (id: string, key: string, allowed: string[], why: string): PolicyRule => ({
  id, why, keys: [key],
  violations: (roles) => holders(roles, key)
    .filter((name) => !allowed.includes(name))
    .map((name) => `${name} holds ${key} (only ${allowed.join(', ')} may)`),
});

/** `role` must not hold any of `keys`. */
const mustLack = (id: string, role: string, keys: string[], why: string): PolicyRule => ({
  id, why, keys,
  violations: (roles) => {
    const perms = roles.get(role);
    if (!perms) return [];
    return keys.filter((key) => perms.has(key)).map((key) => `${role} holds ${key}`);
  },
});

/** Every permission `role` holds must satisfy `allowed`. */
const confinedTo = (
  id: string, role: string, describe: string, allowed: (key: string) => boolean, why: string, keys: string[] = [],
): PolicyRule => ({
  id, why, keys,
  violations: (roles) => {
    const perms = roles.get(role);
    if (!perms) return [];
    return [...perms].filter((key) => !allowed(key)).sort().map((key) => `${role} holds ${key}, outside ${describe}`);
  },
});

export const ROLE_POLICY: PolicyRule[] = [
  {
    id: 'admin-holds-everything',
    why: 'The tenant Admin is the break-glass role; AuthSeeder backfills it with every permission on boot.',
    keys: [],
    violations: (roles, catalog) => {
      const admin = roles.get('Admin');
      if (!admin) return [];
      return catalog.filter((key) => !admin.has(key)).map((key) => `Admin lacks ${key}`);
    },
  },
  onlyHeldBy('security-manage-is-admin-only', 'security.manage', ['Admin'],
    'security.manage opens RBAC, user and override management (AccessController); a second holder can grant itself anything.'),
  onlyHeldBy('payroll-lock-is-controller-tier', 'payroll.lock', ['Admin', 'Finance Approver'],
    'Locking, voiding or sending back a run is the financial-controller tier; HR Manager and Payroll Manager are excluded on purpose.'),
  onlyHeldBy('payroll-run-delete-is-narrow', 'payroll.run_delete', ['Admin', 'Payroll Manager'],
    'Hard-deleting a payroll run destroys the audit trail of a pay period.'),
  mustLack('payroll-officer-is-a-maker', 'Payroll Officer', ['payroll.approve', 'payroll.lock', 'payroll.run_delete', 'employees.write'],
    'Maker-checker: the person who prepares a run must not be able to approve, lock or delete it.'),
  mustLack('finance-approver-is-a-checker', 'Finance Approver', ['payroll.write', 'payroll.run_delete', 'employees.write'],
    'Maker-checker: the approver must not be able to prepare the run or change the people on it.'),
  mustLack('hr-director-reads-payroll-only', 'HR Director', ['payroll.write', 'payroll.approve', 'payroll.lock', 'payroll.run_delete'],
    'HR Director has strategic payroll VISIBILITY, not payroll authority.'),
  mustLack('supervisor-does-not-approve-leave', 'Supervisor', ['leave.approve', 'employees.write', 'payroll.read'],
    'Leave approval sits with the Manager; a front-line supervisor records attendance.'),
  mustLack('recruiter-does-not-approve-hires', 'Recruiter', ['recruitment.approve', 'employees.write', 'payroll.read'],
    'A recruiter runs the pipeline; requisitions and offers are approved by someone else.'),
  mustLack('hr-assistant-cannot-mutate', 'HR Assistant', ['employees.write', 'payroll.read', 'payroll.write'],
    'Junior HR support has read access only.'),
  mustLack('compliance-officer-has-no-payroll', 'Compliance Officer', ['payroll.read', 'payroll.write', 'employees.write'],
    'Compliance reviews records; it has no reason to see or move pay.'),
  confinedTo('auditor-is-read-only', 'Auditor', '*.read', (key) => key.endsWith('.read'),
    'An auditor is read-only by definition (mirrors Zayra.Api.Tests/Security/AuditorReadOnlyTests).'),
  confinedTo('kiosk-operator-is-kiosk-only', 'Kiosk Operator', 'attendance.kiosk', (key) => key === 'attendance.kiosk',
    'A kiosk device account captures punches and nothing else.', ['attendance.kiosk']),
  confinedTo('employee-is-self-service-only', 'Employee', 'dashboard/profile/ess, loans.self and performance.read',
    (key) => key === 'dashboard.read' || key.startsWith('profile.') || key.startsWith('ess.') || key === 'loans.self' || key === 'performance.read',
    'An employee sees their own record through self-service, never other people\'s records or pay. '
    + 'loans.self and performance.read open only their OWN records (object/data scope); goals are set by others, so no performance.write.',
    ['loans.self', 'performance.read']),
  onlyHeldBy('erp-confirm-is-the-checker', 'finance.erp.confirm', ['Admin', 'Finance Approver'],
    'Maker-checker on the GL hand-off: Payroll Manager produces the journal export, so it must not also attest '
    + 'that the client ERP imported it.'),
];

/** Every key a policy rule names that the catalog does not define. */
export function unknownPolicyKeys(catalog: RoleCatalog, rules: PolicyRule[] = ROLE_POLICY): string[] {
  const known = new Set(catalog.permissions);
  return [...new Set(rules.flatMap((r) => r.keys))].filter((key) => !known.has(key)).sort();
}

/** Every role a policy rule is ABOUT that the catalog does not install (a renamed role voids the rule). */
export function unknownPolicyRoles(catalog: RoleCatalog): string[] {
  const installed = new Set(catalog.roles.map((r) => r.name));
  const named = ['Admin', 'Finance Approver', 'Payroll Manager', 'Payroll Officer', 'HR Director', 'Supervisor',
    'Recruiter', 'HR Assistant', 'Compliance Officer', 'Auditor', 'Kiosk Operator', 'Employee'];
  return named.filter((name) => !installed.has(name));
}

/** Evaluate every rule against a role → permissions map. Returns `rule-id: violation` lines. */
export function evaluatePolicy(
  roles: Map<string, Set<string>>, catalogKeys: string[], rules: PolicyRule[] = ROLE_POLICY,
): string[] {
  return rules.flatMap((rule) => rule.violations(roles, catalogKeys).map((v) => `${rule.id}: ${v} — ${rule.why}`));
}

export const catalogRoleMap = (catalog: RoleCatalog): Map<string, Set<string>> =>
  new Map(catalog.roles.map((r) => [r.name, new Set(r.permissions)]));
