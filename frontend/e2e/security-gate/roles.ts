/**
 * WAVE 1 B3 — the role registry behind the Chrome security gate.
 *
 * Every identity here is a REAL account in a REAL database, reached through a REAL production
 * frontend build talking to a REAL backend. No mocked business API, no fixture, no hidden demo data.
 *
 * WHY ONE LOGIN PER ROLE, EVER. The API's login limiter permits 10 attempts per 60-second window
 * (`RateLimit:LoginPermitLimit`). The previous suite logged in per spec file and hit `429`, and the
 * tempting "fix" is to raise the limit for tests — which weakens a production control to make a test
 * pass. Instead each role authenticates exactly ONCE in the setup project, paced under the window, and
 * every spec reuses the stored session. The limiter is left exactly as production runs it.
 *
 * NO ADDRESS IS SPELLED HERE. Every entry points at a persona object from e2e/world.ts — the same
 * declaration the bootstrap provisions and the preflight verifies — so the gate cannot sign in as an
 * account the world does not contain, or as a different one from the lane next to it (register F07).
 */

import { resolveTarget } from '../identity/env';
import {
  ALMARAI_AUDITOR, ALMARAI_BAKERY_HR, ALMARAI_COMPLIANCE, ALMARAI_DAIRY_HR, ALMARAI_DAIRY_PAYROLL,
  ALMARAI_FINANCE, ALMARAI_HR, ALMARAI_HR_ASSISTANT, ALMARAI_HR_OFFICER, ALMARAI_KIOSK, ALMARAI_OWNER,
  ALMARAI_PAYROLL_MANAGER, ALMARAI_RECRUITER, ALMARAI_SCOPED_ADMIN, ALMARAI_SLUG, GROUP_PASSWORD,
  INTELLIFLOW_EMP1, INTELLIFLOW_MANAGER, INTELLIFLOW_SLUG, INTELLIFLOW_SUPERVISOR, PLATFORM_EMAIL,
  PLATFORM_PASSWORD, type FixtureUser,
} from '../world';

export const BASE_URL = resolveTarget().baseUrl;

/**
 * The multi-company group tenant. It is no longer seeded by anything — e2e/bootstrap/provision.ts
 * creates it, its five companies and every identity below through the platform-admin API, and the
 * declaration lives in e2e/world.ts so this file and e2e/helpers.ts cannot drift apart again.
 */
export const GROUP_SLUG = ALMARAI_SLUG;
export { GROUP_PASSWORD, PLATFORM_EMAIL, PLATFORM_PASSWORD };

export type Scope = 'group' | 'company' | 'companies' | 'platform';

export interface RoleFixture {
  /** Stable key — also the storage-state filename. */
  key: string;
  label: string;
  email: string;
  password: string;
  /** null for the platform operator, which authenticates against the platform audience. */
  tenantSlug: string | null;
  /** The tenant role this identity holds (AuthSeeder name); null for the platform operator. */
  role: string | null;
  scope: Scope;
  /** Company code this identity is confined to, when scope === 'company'. */
  companyCode?: string;
  /** What this role must NOT be able to do — asserted by the isolation specs. */
  mustNotReach: string[];
}

const tenantRole = (
  key: string, label: string, user: FixtureUser, tenantSlug: string, scope: Scope, mustNotReach: string[],
): RoleFixture => ({
  key, label, email: user.email, password: user.password, tenantSlug, role: user.role, scope,
  ...(scope === 'company' && user.companyCode ? { companyCode: user.companyCode } : {}),
  mustNotReach,
});

/**
 * The gate spans the WHOLE tenant system-role catalog AuthSeeder installs — e2e/identity/
 * identity-contract.spec.ts fails the PR if a role is added there without a persona here.
 *
 * Every company-scoped identity below is confined by a real `SelectedCompanies` entity grant that
 * the bootstrap creates through /api/access/entity-grants. Without that grant each of them would be
 * group-scope, and the negative assertions in isolation.spec.ts ("bakery data is not visible") would
 * pass for the wrong reason.
 *
 * Manager, Supervisor and Employee are IntelliFlow accounts, not Almarai ones. Employee is the right
 * choice: employee1@intelliflow.com is the one persona the bootstrap links to a real employee record
 * through the invitation flow, so self-service has a person behind it. Manager and Supervisor are NOT
 * linked to any employee and have no direct reports — they prove what the ROLE may do (permissions,
 * API authority, navigation), not a manager journey over a real team. (The earlier port claimed
 * "real reporting linkage" for them; the bootstrap has never created one.)
 */
export const ROLES: RoleFixture[] = [
  {
    key: 'platform-admin',
    label: 'Platform Admin',
    email: PLATFORM_EMAIL,
    password: PLATFORM_PASSWORD,
    tenantSlug: null,
    role: null,
    scope: 'platform',
    mustNotReach: ['tenant HR administration outside controlled impersonation'],
  },
  tenantRole('tenant-owner', 'Tenant Owner / Admin (group)', ALMARAI_OWNER, GROUP_SLUG, 'group',
    ['platform administration']),
  tenantRole('group-hr', 'Group HR Director', ALMARAI_HR, GROUP_SLUG, 'group', ['platform administration']),
  tenantRole('company-hr-dairy', 'Company HR — ALM-DAIRY-KSA', ALMARAI_DAIRY_HR, GROUP_SLUG, 'company',
    ['ALM-BAKERY-KSA data', 'platform administration']),
  tenantRole('company-hr-bakery', 'Company HR — ALM-BAKERY-KSA', ALMARAI_BAKERY_HR, GROUP_SLUG, 'company',
    ['ALM-DAIRY-KSA data', 'platform administration']),
  tenantRole('payroll-maker', 'Payroll Maker (Payroll Officer, ALM-DAIRY-KSA)', ALMARAI_DAIRY_PAYROLL, GROUP_SLUG,
    'company', ['approver/checker actions', 'sibling-company payroll']),
  tenantRole('payroll-checker', 'Payroll Checker / Finance Approver', ALMARAI_FINANCE, GROUP_SLUG, 'group',
    ['platform administration']),
  tenantRole('auditor', 'Auditor (read-only)', ALMARAI_AUDITOR, GROUP_SLUG, 'group',
    ['any write', 'platform administration']),
  tenantRole('scoped-admin', 'Selected-companies admin (2 of 5)', ALMARAI_SCOPED_ADMIN, GROUP_SLUG, 'companies',
    ['the 3 companies not granted', 'platform administration']),
  // ── The rest of the system-role catalog (full-role-matrix.spec.ts) ──────────────────────────
  tenantRole('payroll-manager', 'Payroll Manager', ALMARAI_PAYROLL_MANAGER, GROUP_SLUG, 'group',
    ['platform administration', 'HR-only employee mutation']),
  tenantRole('hr-officer', 'HR Officer', ALMARAI_HR_OFFICER, GROUP_SLUG, 'group',
    ['payroll approval', 'platform administration']),
  tenantRole('compliance-officer', 'Compliance Officer', ALMARAI_COMPLIANCE, GROUP_SLUG, 'group',
    ['payroll mutation', 'platform administration']),
  tenantRole('recruiter', 'Recruiter', ALMARAI_RECRUITER, GROUP_SLUG, 'group',
    ['payroll administration', 'platform administration']),
  tenantRole('hr-assistant', 'HR Assistant', ALMARAI_HR_ASSISTANT, GROUP_SLUG, 'group',
    ['employee mutation', 'payroll administration']),
  tenantRole('kiosk-operator', 'Kiosk Operator', ALMARAI_KIOSK, GROUP_SLUG, 'group',
    ['general HR administration', 'payroll administration']),
  tenantRole('manager', 'Manager (IntelliFlow, not employee-linked)', INTELLIFLOW_MANAGER, INTELLIFLOW_SLUG, 'group',
    ['payroll administration', 'tenant administration']),
  tenantRole('supervisor', 'Supervisor (IntelliFlow, not employee-linked)', INTELLIFLOW_SUPERVISOR, INTELLIFLOW_SLUG,
    'group', ['payroll administration', 'tenant administration']),
  tenantRole('employee', 'Employee self-service (IntelliFlow, employee-linked)', INTELLIFLOW_EMP1, INTELLIFLOW_SLUG,
    'company', ['other employee records', 'payroll administration', 'tenant administration']),
];

export const roleByKey = (key: string): RoleFixture => {
  const found = ROLES.find(r => r.key === key);
  if (!found) throw new Error(`Unknown role fixture '${key}'`);
  return found;
};

/** Where a role's session is persisted. Gitignored (frontend/.gitignore: e2e/.auth/). */
export const storageStatePath = (key: string) => `e2e/.auth/${key}.json`;
export const tokenPath = (key: string) => `e2e/.auth/${key}.token.json`;
