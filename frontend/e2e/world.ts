/**
 * THE FIXTURE WORLD — the single source of truth for every identity the e2e suites log in as.
 *
 * ── Why this file exists ──────────────────────────────────────────────────────────────────────
 * Every demo/fixture seeder was deleted (docs/DATA_ENTRY_PATHS.md). A migrated database now starts
 * EMPTY: there is no `intelliflow`, no `almarai-test`, no `admin@platform.local`. Everything the
 * suites authenticate as has to be CREATED, and the only sanctioned way to create it is the
 * platform-admin API.
 *
 * Before this file, the same identities were declared independently in three places —
 * `e2e/helpers.ts`, `e2e/security-gate/roles.ts` and `e2e/group-company/helpers.ts` — and they had
 * already drifted: `helpers.ts` defaulted the platform operator to `platform@kynexone.com` while
 * `roles.ts` defaulted it to `admin@platform.local`, so the two lanes were provably authenticating
 * as different accounts. A bootstrap can only provision one world, so there is now one declaration
 * of it and the three modules re-export from here.
 *
 * ── Passwords ─────────────────────────────────────────────────────────────────────────────────
 * Every password comes from the environment. In CI they are GENERATED PER RUN (see ci.yml) and a
 * missing variable is a hard failure — CI must never fall back to a value committed to the repo.
 * Outside CI the historical local-stack defaults are kept so `docker compose up && npx playwright
 * test` still works on a developer machine. These defaults are not secrets: they are only ever
 * valid against a disposable loopback database that this same bootstrap just populated.
 */

const inCi = !!process.env.CI;

/**
 * A fixture password. Required in CI; falls back to the documented local-stack value elsewhere.
 * Fails LOUDLY rather than silently provisioning a world the specs cannot then log in to.
 */
function fixturePassword(envVar: string, localDefault: string): string {
  const supplied = process.env[envVar];
  if (supplied && supplied.trim()) return supplied;
  if (inCi) {
    throw new Error(
      `[world] ${envVar} is not set.\n`
      + 'CI must generate this password per run and export it to every step that provisions or\n'
      + 'uses the fixture world (the bootstrap and the Playwright jobs). Falling back to a value\n'
      + 'committed to the repository would put a working credential in git, so this fails instead.',
    );
  }
  return localDefault;
}

export interface FixtureUser {
  email: string;
  password: string;
  /** Tenant role name; must exist in AuthSeeder's standard tenant role set. */
  role: string;
  fullName: string;
  /** Group-wide when omitted; otherwise the company code this identity is confined to. */
  companyCode?: string;
  /** Explicit multi-company grant (the "selected companies" access mode). */
  companyCodes?: string[];
}

export interface FixtureCompany {
  /** Also the company's LegalNameEn — the group specs match companies by this code. */
  code: string;
  countryCode: string;
  currency: string;
}

export interface FixtureTenant {
  slug: string;
  name: string;
  accountType: 'SingleCompany' | 'Group';
  plan: string;
  maxUsers: number;
  maxEmployees: number;
  maxCompanies: number;
  admin: FixtureUser;
  companies: FixtureCompany[];
  users: FixtureUser[];
  /** Employees created per company, with `<COMPANY-CODE>-E<n>` codes. */
  employeesPerCompany: number;
  /**
   * The floor of ACTIVE employees the bootstrap must reach for this tenant, after the product's own
   * activation guard has had its say. Below it the bootstrap fails: a tenant whose employees are all
   * still Draft renders as an empty product, and every row-count assertion downstream would blame
   * the UI for it.
   */
  minActiveEmployees: number;
  /** Provision salary structures + one locked payroll run for the first company. */
  payroll: boolean;
  /**
   * Tenant-user emails to attach to real EMPLOYEE records, in order, starting at the first
   * company's second employee.
   *
   * `DataScopeService.ResolveCallerEmployeeIdAsync` links a signed-in user to an employee by
   * matching the token's email against the employee's work or personal email. Without that link the
   * account is a login with no person behind it: every employee-self-service surface resolves to
   * "Own" scope over an EMPTY employee set, so My Benefits, ESS document requests and the employee
   * letter journey render nothing at all while the API returns 200.
   */
  employeePortalLogins?: string[];
  /**
   * Feature flags to switch ON. A tenant is born with none enabled, so the platform tenant-detail
   * screen shows a feature list where every switch is off — and `platform-tenants.spec.ts` requires
   * at least one `aria-checked="true"`, which is the honest assertion: a feature panel that can only
   * ever render "off" proves nothing about the toggle.
   */
  features?: string[];
}

// ── Platform operator ─────────────────────────────────────────────────────────────────────────
// Created by PlatformOwnerBootstrap at API boot from PLATFORM_ADMIN_EMAIL/PLATFORM_ADMIN_PASSWORD,
// then used by the bootstrap to create everything else.
export const PLATFORM_EMAIL = process.env.PLATFORM_ADMIN_EMAIL ?? 'admin@platform.local';
export const PLATFORM_PASSWORD = fixturePassword('PLATFORM_ADMIN_PASSWORD', 'YourPassword123!');

// ── Passwords, one per tenant ─────────────────────────────────────────────────────────────────
const INTELLIFLOW_PASSWORD = fixturePassword('E2E_INTELLIFLOW_PASSWORD', 'IntelliFlow@2026!');
const RASALMANAR_PASSWORD = fixturePassword('E2E_RASALMANAR_PASSWORD', 'RasAlManar@2026!');
export const GROUP_PASSWORD = fixturePassword('E2E_GROUP_PASSWORD', 'GroupDemo123!x');
export const EVOSTEL_PASSWORD = fixturePassword('E2E_EVOSTEL_PASSWORD', 'E2E-Demo@1234');

// ── Slugs ─────────────────────────────────────────────────────────────────────────────────────
export const INTELLIFLOW_SLUG = 'intelliflow';
export const RASALMANAR_SLUG = 'rasalmanar';
export const ALMARAI_SLUG = 'almarai-test';
export const TATA_SLUG = 'tata-test';
/** Provisioned and purged per run by e2e/limited-tenant-fixture.ts, not by the bootstrap. */
export const EVOSTEL_SLUG = 'evostel';

export const ALMARAI_COMPANY_CODES = [
  'ALM-DAIRY-KSA', 'ALM-POULTRY-KSA', 'ALM-BAKERY-KSA', 'ALM-DIST-KSA', 'ALM-UAE-TRD',
];
export const TATA_COMPANY_CODES = [
  'TATA-TCS-IN', 'TATA-MOTORS-IN', 'TATA-STEEL-IN', 'TATA-HOTELS-IN', 'TATA-JLR-UK',
];

const saCompany = (code: string): FixtureCompany => ({ code, countryCode: 'SA', currency: 'SAR' });

/** Group-scope address shape the suites already use: `<role>@<slug>.local`. */
export const groupEmail = (role: string, slug = ALMARAI_SLUG): string => `${role}@${slug}.local`;
/** Company-scoped address shape: `<role>@<company-code-lowercased>.<slug>.local`. */
export const companyEmail = (role: string, companyCode: string, slug = ALMARAI_SLUG): string =>
  `${role}@${companyCode.toLowerCase()}.${slug}.local`;

// ── IntelliFlow: the single-company workhorse tenant ──────────────────────────────────────────
// Carries the employees, salary structures and the locked payroll run that the payroll, GOSI,
// dashboard and demo-surface specs read.
const intelliflowUser = (local: string, role: string, fullName: string): FixtureUser => ({
  email: `${local}@intelliflow.com`, password: INTELLIFLOW_PASSWORD, role, fullName,
});

export const INTELLIFLOW_ADMIN = intelliflowUser('admin', 'Admin', 'IntelliFlow Administrator');
export const INTELLIFLOW_HR_DIR = intelliflowUser('hrdirector', 'HR Director', 'IntelliFlow HR Director');
export const INTELLIFLOW_HR_MGR = intelliflowUser('hrmanager', 'HR Manager', 'IntelliFlow HR Manager');
export const INTELLIFLOW_FINANCE = intelliflowUser('finance', 'Finance Approver', 'IntelliFlow Finance Approver');
export const INTELLIFLOW_MANAGER = intelliflowUser('manager', 'Manager', 'IntelliFlow Manager');
export const INTELLIFLOW_SUPERVISOR = intelliflowUser('supervisor', 'Supervisor', 'IntelliFlow Supervisor');
export const INTELLIFLOW_EMP1 = intelliflowUser('employee1', 'Employee', 'IntelliFlow Employee One');
export const INTELLIFLOW_EMP2 = intelliflowUser('employee2', 'Employee', 'IntelliFlow Employee Two');
export const INTELLIFLOW_AUDITOR = intelliflowUser('auditor', 'Auditor', 'IntelliFlow Auditor');

export const RASALMANAR_ADMIN: FixtureUser = {
  email: 'admin@rasalmanar.com', password: RASALMANAR_PASSWORD, role: 'Admin',
  fullName: 'Ras Al-Manar Administrator',
};

export const EVOSTEL_ADMIN: FixtureUser = {
  email: 'admin@evostel.com', password: EVOSTEL_PASSWORD, role: 'Admin',
  fullName: 'E2E Tenant Administrator',
};
export const EVOSTEL_EMP1: FixtureUser = {
  email: 'employee1@evostel.com', password: EVOSTEL_PASSWORD, role: 'Employee',
  fullName: 'E2E Tenant Employee',
};

// ── Almarai: the multi-company group tenant ───────────────────────────────────────────────────
// Its identities are what the Chrome security gate and the group-company suite authorize against,
// so each one's SCOPE is declared here and granted by the bootstrap through /api/access/entity-grants.
const groupUser = (role: string, roleName: string, fullName: string, slug = ALMARAI_SLUG): FixtureUser => ({
  email: groupEmail(role, slug), password: GROUP_PASSWORD, role: roleName, fullName,
});
const companyUser = (
  role: string, companyCode: string, roleName: string, fullName: string, slug = ALMARAI_SLUG,
): FixtureUser => ({
  email: companyEmail(role, companyCode, slug), password: GROUP_PASSWORD, role: roleName, fullName, companyCode,
});

export const ALMARAI_USERS: FixtureUser[] = [
  groupUser('hr', 'HR Director', 'Almarai Group HR Director'),
  groupUser('finance', 'Finance Approver', 'Almarai Group Finance Approver'),
  // 'Compliance Officer', not 'HR Manager': the role is what
  // CompanyComplianceProfilesController authorizes on, and it is the only non-Admin role permitted
  // to AUTHOR a company compliance profile. Given HR Manager, this persona got a 403 from
  // /api/company-compliance-profiles and the compliance suite saw a page with no profile on it.
  groupUser('compliance', 'Compliance Officer', 'Almarai Group Compliance Officer'),
  groupUser('auditor', 'Auditor', 'Almarai Group Auditor'),
  {
    ...groupUser('scoped.admin', 'HR Manager', 'Almarai Selected-Companies Admin'),
    // "2 of 5" — the grant the scoped-user specs prove cannot see the other three.
    companyCodes: ['ALM-DAIRY-KSA', 'ALM-POULTRY-KSA'],
  },
  companyUser('admin', 'ALM-DAIRY-KSA', 'HR Manager', 'Almarai Dairy Company Admin'),
  companyUser('hr', 'ALM-DAIRY-KSA', 'HR Manager', 'Almarai Dairy Company HR'),
  companyUser('hr', 'ALM-BAKERY-KSA', 'HR Manager', 'Almarai Bakery Company HR'),
  companyUser('payroll', 'ALM-DAIRY-KSA', 'Payroll Officer', 'Almarai Dairy Payroll Officer'),
];

export const TENANTS: FixtureTenant[] = [
  {
    slug: INTELLIFLOW_SLUG,
    name: 'IntelliFlow Systems',
    accountType: 'SingleCompany',
    plan: 'Enterprise',
    maxUsers: 100,
    maxEmployees: 500,
    maxCompanies: 1,
    admin: INTELLIFLOW_ADMIN,
    companies: [saCompany('INTELLIFLOW-KSA')],
    users: [
      INTELLIFLOW_HR_DIR, INTELLIFLOW_HR_MGR, INTELLIFLOW_FINANCE, INTELLIFLOW_MANAGER,
      INTELLIFLOW_SUPERVISOR, INTELLIFLOW_EMP1, INTELLIFLOW_EMP2, INTELLIFLOW_AUDITOR,
    ],
    // pilot-critical.spec.ts asserts E2E_MIN_EMPLOYEES (12 in CI) rendered rows.
    employeesPerCompany: 14,
    // pilot-critical.spec.ts is run in CI with E2E_MIN_EMPLOYEES=12.
    minActiveEmployees: 12,
    payroll: true,
    employeePortalLogins: [INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP2.email],
    // The Enterprise tenant that tenant-feature-flags.spec.ts contrasts against the limited Evostel
    // fixture: these must be ON here and OFF there for either half to mean anything.
    features: ['ai_assistant', 'recruitment', 'performance', 'shifts', 'overtime', 'qiwa_integration'],
  },
  {
    slug: RASALMANAR_SLUG,
    name: 'Ras Al-Manar Trading',
    accountType: 'SingleCompany',
    plan: 'Growth',
    maxUsers: 25,
    maxEmployees: 100,
    maxCompanies: 1,
    admin: RASALMANAR_ADMIN,
    companies: [saCompany('RASALMANAR-KSA')],
    users: [],
    // Cross-tenant isolation specs compare real employee rows against IntelliFlow's.
    employeesPerCompany: 4,
    minActiveEmployees: 4,
    payroll: false,
  },
  {
    slug: ALMARAI_SLUG,
    name: 'Almarai Group (E2E)',
    accountType: 'Group',
    plan: 'Enterprise',
    maxUsers: 100,
    maxEmployees: 500,
    maxCompanies: 0,
    admin: { ...groupUser('owner', 'Admin', 'Almarai Group Owner') },
    companies: ALMARAI_COMPANY_CODES.map(saCompany),
    users: ALMARAI_USERS,
    employeesPerCompany: 3,
    // All 15 activate. Every third is deliberately left without an Iqama so the compliance
    // profile's "Missing" column is a real number, and readiness counts only ACTIVE employees —
    // so the gap has to be on an activated one, not on a Draft.
    minActiveEmployees: 15,
    payroll: true,
  },
  {
    slug: TATA_SLUG,
    name: 'Tata Group (E2E)',
    accountType: 'Group',
    plan: 'Enterprise',
    maxUsers: 100,
    maxEmployees: 500,
    maxCompanies: 0,
    admin: { ...groupUser('owner', 'Admin', 'Tata Group Owner', TATA_SLUG) },
    companies: TATA_COMPANY_CODES.map((code) => ({ code, countryCode: 'IN', currency: 'INR' })),
    users: [groupUser('compliance', 'Compliance Officer', 'Tata Group Compliance Officer', TATA_SLUG)],
    employeesPerCompany: 2,
    // India pack. The group-company compliance spec reads this tenant's PROFILE for the country
    // contrast rather than its headcount, so the floor is modest — but it is not zero, because
    // "every employee failed to activate" must not pass quietly.
    minActiveEmployees: 8,
    payroll: false,
  },
];

export const tenantBySlug = (slug: string): FixtureTenant => {
  const found = TENANTS.find((t) => t.slug === slug);
  if (!found) throw new Error(`[world] No fixture tenant '${slug}' is declared.`);
  return found;
};

/**
 * Where the bootstrap records what it provisioned. Its presence is how every spec distinguishes
 * "the fixture world is missing" (a hard failure with instructions) from "this assertion failed".
 */
export const WORLD_MANIFEST = 'e2e/.auth/world.json';

export interface WorldManifest {
  baseUrl: string;
  provisionedAtUtc: string;
  tenants: Array<{ slug: string; tenantId: string; companies: Array<{ code: string; id: string }> }>;
}

/**
 * The one message every lane prints when the fixture world is absent.
 *
 * It exists because "missing fixture data" and "the product is broken" produce identical symptoms —
 * empty lists, failed logins, 404s — and the suites used to answer that ambiguity by SKIPPING. A
 * skipped group-company suite reported green against a database that had never been provisioned.
 * Now every such path throws, and it throws this, so the next person reads the fix instead of
 * debugging the UI.
 */
export const MISSING_WORLD =
  'THE E2E FIXTURE WORLD IS NOT PROVISIONED.\n'
  + 'Databases start EMPTY — every demo/fixture seeder was deleted (docs/DATA_ENTRY_PATHS.md), so\n'
  + 'there is no tenant, user or employee until the bootstrap creates them through the platform-admin\n'
  + 'API. Run it once, after migrations and before any spec lane:\n'
  + '    cd frontend && npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts\n'
  + 'The backend must have been started with PLATFORM_ADMIN_EMAIL/PLATFORM_ADMIN_PASSWORD set so the\n'
  + 'platform owner exists, and the fixture passwords the bootstrap used must still be exported.\n'
  + 'This is a FAILURE, not a skip: an unprovisioned stack must never produce a green run.';

/**
 * Hard gate for any spec that needs the fixture world. Reads the manifest the bootstrap wrote and
 * throws MISSING_WORLD when it is not there, so "nobody provisioned the database" can never be
 * mistaken for "this assertion happens to fail".
 */
export async function requireWorld(): Promise<WorldManifest> {
  const { readFile } = await import('node:fs/promises');
  let raw: string;
  try {
    raw = await readFile(WORLD_MANIFEST, 'utf8');
  } catch {
    throw new Error(`${WORLD_MANIFEST} does not exist.\n${MISSING_WORLD}`);
  }
  const manifest = JSON.parse(raw) as WorldManifest;
  if (!manifest.tenants?.length) throw new Error(`${WORLD_MANIFEST} lists no tenants.\n${MISSING_WORLD}`);
  return manifest;
}
