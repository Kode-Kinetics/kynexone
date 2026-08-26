/**
 * WAVE 1 B3 — the role registry behind the Chrome security gate.
 *
 * Every identity here is a REAL seeded user in a REAL database, reached through a REAL production
 * frontend build talking to a REAL backend. No mocked business API, no fixture, no hidden demo data.
 *
 * WHY ONE LOGIN PER ROLE, EVER. The API's login limiter permits 10 attempts per 60-second window
 * (`RateLimit:LoginPermitLimit`). The previous suite logged in per spec file and hit `429`, and the
 * tempting "fix" is to raise the limit for tests — which weakens a production control to make a test
 * pass. Instead each role authenticates exactly ONCE in the setup project, paced under the window, and
 * every spec reuses the stored session. The limiter is left exactly as production runs it.
 */

export const BASE_URL = process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173';

/** Seeded enterprise-group tenant (EnterpriseGroupSeeder, SEED_ENTERPRISE_TEST_DATA=true). */
export const GROUP_SLUG = 'almarai-test';
export const GROUP_PASSWORD = process.env.E2E_GROUP_PASSWORD ?? 'GroupDemo123!x';

/** Platform operator — bootstrapped independently of demo data (Wave 1 B3). */
export const PLATFORM_EMAIL = process.env.PLATFORM_ADMIN_EMAIL ?? 'admin@platform.local';
export const PLATFORM_PASSWORD = process.env.PLATFORM_ADMIN_PASSWORD ?? 'YourPassword123!';

export type Scope = 'group' | 'company' | 'companies' | 'platform';

export interface RoleFixture {
  /** Stable key — also the storage-state filename. */
  key: string;
  label: string;
  email: string;
  password: string;
  /** null for the platform operator, which authenticates against the platform audience. */
  tenantSlug: string | null;
  scope: Scope;
  /** Company code this identity is confined to, when scope === 'company'. */
  companyCode?: string;
  /** What this role must NOT be able to do — asserted by the isolation specs. */
  mustNotReach: string[];
}

/**
 * Roles present in the enterprise-group seed. `Manager` and `Employee` exist as roles in the product
 * but are NOT seeded into this tenant, so they are deliberately absent rather than faked — see
 * GAP-B3-1 in docs/CHROME_SECURITY_GATE.md.
 */
export const ROLES: RoleFixture[] = [
  {
    key: 'platform-admin',
    label: 'Platform Admin',
    email: PLATFORM_EMAIL,
    password: PLATFORM_PASSWORD,
    tenantSlug: null,
    scope: 'platform',
    mustNotReach: ['tenant HR administration outside controlled impersonation'],
  },
  {
    key: 'tenant-owner',
    label: 'Tenant Owner / Admin (group)',
    email: `owner@${GROUP_SLUG}.local`,
    password: GROUP_PASSWORD,
    tenantSlug: GROUP_SLUG,
    scope: 'group',
    mustNotReach: ['platform administration'],
  },
  {
    key: 'group-hr',
    label: 'Group HR Director',
    email: `hr@${GROUP_SLUG}.local`,
    password: GROUP_PASSWORD,
    tenantSlug: GROUP_SLUG,
    scope: 'group',
    mustNotReach: ['platform administration'],
  },
  {
    key: 'company-hr-dairy',
    label: 'Company HR — ALM-DAIRY-KSA',
    email: `hr@alm-dairy-ksa.${GROUP_SLUG}.local`,
    password: GROUP_PASSWORD,
    tenantSlug: GROUP_SLUG,
    scope: 'company',
    companyCode: 'ALM-DAIRY-KSA',
    mustNotReach: ['ALM-BAKERY-KSA data', 'platform administration'],
  },
  {
    key: 'company-hr-bakery',
    label: 'Company HR — ALM-BAKERY-KSA',
    email: `hr@alm-bakery-ksa.${GROUP_SLUG}.local`,
    password: GROUP_PASSWORD,
    tenantSlug: GROUP_SLUG,
    scope: 'company',
    companyCode: 'ALM-BAKERY-KSA',
    mustNotReach: ['ALM-DAIRY-KSA data', 'platform administration'],
  },
  {
    key: 'payroll-maker',
    label: 'Payroll Maker (Payroll Officer, ALM-DAIRY-KSA)',
    email: `payroll@alm-dairy-ksa.${GROUP_SLUG}.local`,
    password: GROUP_PASSWORD,
    tenantSlug: GROUP_SLUG,
    scope: 'company',
    companyCode: 'ALM-DAIRY-KSA',
    mustNotReach: ['approver/checker actions', 'sibling-company payroll'],
  },
  {
    key: 'payroll-checker',
    label: 'Payroll Checker / Finance Approver',
    email: `finance@${GROUP_SLUG}.local`,
    password: GROUP_PASSWORD,
    tenantSlug: GROUP_SLUG,
    scope: 'group',
    mustNotReach: ['platform administration'],
  },
  {
    key: 'auditor',
    label: 'Auditor (read-only)',
    email: `auditor@${GROUP_SLUG}.local`,
    password: GROUP_PASSWORD,
    tenantSlug: GROUP_SLUG,
    scope: 'group',
    mustNotReach: ['any write', 'platform administration'],
  },
  {
    key: 'scoped-admin',
    label: 'Selected-companies admin (2 of 5)',
    email: `scoped.admin@${GROUP_SLUG}.local`,
    password: GROUP_PASSWORD,
    tenantSlug: GROUP_SLUG,
    scope: 'companies',
    mustNotReach: ['the 3 companies not granted', 'platform administration'],
  },
];

export const roleByKey = (key: string): RoleFixture => {
  const found = ROLES.find(r => r.key === key);
  if (!found) throw new Error(`Unknown role fixture '${key}'`);
  return found;
};

/** Where a role's session is persisted. Gitignored (frontend/.gitignore: e2e/.auth/). */
export const storageStatePath = (key: string) => `e2e/.auth/${key}.json`;
export const tokenPath = (key: string) => `e2e/.auth/${key}.token.json`;
