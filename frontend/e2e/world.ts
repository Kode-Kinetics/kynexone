/**
 * THE FIXTURE WORLD — the single source of truth for every identity the e2e suites log in as.
 *
 * ── One organisation ──────────────────────────────────────────────────────────────────────────
 * KynexOne is installed by each client for ONE organisation (implementation prompt WP0/WP2). The
 * fixture world is therefore one organisation with several companies. What used to be five
 * separate tenants are now CLUSTERS of companies inside it:
 *
 *   almarai-test  the group cluster — five Saudi/UAE companies; its group identities hold the whole
 *                 organisation (the organisation administrator, ALMARAI_OWNER, is one of them)
 *   intelliflow   the single-company workhorse (employees, salary structures, a locked payroll run);
 *                 its identities are confined to INTELLIFLOW-KSA
 *   rasalmanar    a second single company; identities confined to RASALMANAR-KSA
 *   tata-test     an Indian group of five companies; identities confined to those five
 *
 * A cluster's `slug` is only a NAME: it keys personas and sessions and appears in reports. Nobody
 * signs in "to" it — a client installation has no workspace, so logins send no tenant slug.
 * Scenarios that needed two different organisations (cross-tenant isolation, per-tenant plans) were
 * SaaS-only and are retired; company-scope isolation inside the organisation is what remains.
 *
 * ── How it comes to exist ─────────────────────────────────────────────────────────────────────
 * Databases start EMPTY (docs/DATA_ENTRY_PATHS.md). The organisation, its first company and its
 * administrator are created by `--setup-organisation` (the only door), with ALMARAI_OWNER's address
 * and E2E_GROUP_PASSWORD; a licence is imported (CI issues one per run: scripts/ci/ci-licence.sh).
 * Everything else — companies, users, grants, employees, payroll — is created by
 * e2e/bootstrap/provision.ts through the organisation's own APIs, as that administrator.
 *
 * ── Passwords ─────────────────────────────────────────────────────────────────────────────────
 * Every password comes from the environment. In CI they are GENERATED PER RUN (see ci.yml) and a
 * missing variable is a hard failure — CI must never fall back to a value committed to the repo.
 * Outside CI the historical local-stack defaults are kept so `docker compose up && npx playwright
 * test` still works on a developer machine, and the preflight refuses any target that is not a
 * disposable stack.
 *
 * ── Personas ──────────────────────────────────────────────────────────────────────────────────
 * `PERSONAS` at the bottom is the registry every consumer resolves identities through: the
 * bootstrap provisions `TENANTS` (the clusters), the preflight logs every persona in and checks its
 * role, scope and permissions, the security gate's roles.ts references these objects instead of
 * re-spelling addresses, and e2e/identity/actor.ts stamps the persona behind every login into the
 * test report.
 */

import { isCi } from './identity/env';

const inCi = isCi();

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

/**
 * A CLUSTER of companies inside the one organisation (see the header). Named `FixtureTenant` because
 * that is what every spec already imports; it no longer describes a tenant of its own.
 */
export interface FixtureTenant {
  /** The cluster's name: keys personas and sessions. Never sent to the API. */
  slug: string;
  name: string;
  /** The cluster's administrator. ALMARAI_OWNER, the organisation administrator, is created by setup. */
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
   * A signed-in user is linked to an employee ONLY by the explicit login link the invitation flow
   * writes (the token's employee_id claim; `CallerEmployeeResolver`). An email match does not link
   * anyone. Without that link the
   * account is a login with no person behind it: every employee-self-service surface resolves to
   * "Own" scope over an EMPTY employee set, so My Benefits, ESS document requests and the employee
   * letter journey render nothing at all while the API returns 200.
   */
  employeePortalLogins?: string[];
  /**
   * Modules and opt-in features to switch ON. The organisation has one set; the bootstrap switches on
   * the union of every cluster's list (the licence grants them all).
   */
  features?: string[];
}

// ── The organisation ──────────────────────────────────────────────────────────────────────────
/**
 * What `--setup-organisation` is run with (CI: ci.yml; locally: e2e/bootstrap/setup-organisation.sh).
 * The administrator is ALMARAI_OWNER and the first company is ALM-DAIRY-KSA, so setup creates exactly
 * what the group cluster expects and the bootstrap adds the rest.
 */
export const ORGANISATION = {
  /** The slug setup stores the one organisation under (OrganisationProvisioningService.OrganisationSlug). */
  slug: 'main',
  name: 'KynexOne E2E Organisation',
  homeCountryCode: 'SA',
  firstCompanyCode: 'ALM-DAIRY-KSA',
} as const;

// ── Passwords, one per cluster ────────────────────────────────────────────────────────────────
const INTELLIFLOW_PASSWORD = fixturePassword('E2E_INTELLIFLOW_PASSWORD', 'IntelliFlow@2026!');
const RASALMANAR_PASSWORD = fixturePassword('E2E_RASALMANAR_PASSWORD', 'RasAlManar@2026!');
export const GROUP_PASSWORD = fixturePassword('E2E_GROUP_PASSWORD', 'GroupDemo123!x');

// ── Slugs ─────────────────────────────────────────────────────────────────────────────────────
export const INTELLIFLOW_SLUG = 'intelliflow';
export const RASALMANAR_SLUG = 'rasalmanar';
export const ALMARAI_SLUG = 'almarai-test';
export const TATA_SLUG = 'tata-test';

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

// ── IntelliFlow: the single-company workhorse cluster ─────────────────────────────────────────
// Carries the employees, salary structures and the locked payroll run that the payroll, GOSI,
// dashboard and demo-surface specs read. Its identities are confined to its one company, so they see
// what a single-company organisation's people would see.
export const INTELLIFLOW_COMPANY = 'INTELLIFLOW-KSA';
const intelliflowUser = (local: string, role: string, fullName: string): FixtureUser => ({
  email: `${local}@intelliflow.com`, password: INTELLIFLOW_PASSWORD, role, fullName, companyCode: INTELLIFLOW_COMPANY,
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

export const RASALMANAR_COMPANY = 'RASALMANAR-KSA';
export const RASALMANAR_ADMIN: FixtureUser = {
  email: 'admin@rasalmanar.com', password: RASALMANAR_PASSWORD, role: 'Admin',
  fullName: 'Ras Al-Manar Administrator', companyCode: RASALMANAR_COMPANY,
};
// A second person who can decide leave. Approvals never route to the requester, so a tenant whose
// only approver is the admin filing on employees' behalf has nobody who may decide; the leave
// service now refuses those requests up front (approval_no_eligible_decider) instead of parking
// them in a queue nobody can act on.
export const RASALMANAR_HR: FixtureUser = {
  email: 'hr@rasalmanar.com', password: RASALMANAR_PASSWORD, role: 'HR Manager',
  fullName: 'Ras Al-Manar HR Manager', companyCode: RASALMANAR_COMPANY,
};

// ── Almarai: the group cluster ────────────────────────────────────────────────────────────────
// Its identities are what the Chrome security gate and the group-company suite authorize against,
// so each one's SCOPE is declared here and granted by the bootstrap through /api/access/entity-grants.
// Its group identities hold the whole organisation; ALMARAI_OWNER is the organisation administrator.
const groupUser = (role: string, roleName: string, fullName: string, slug = ALMARAI_SLUG): FixtureUser => ({
  email: groupEmail(role, slug), password: GROUP_PASSWORD, role: roleName, fullName,
});
const companyUser = (
  role: string, companyCode: string, roleName: string, fullName: string, slug = ALMARAI_SLUG,
): FixtureUser => ({
  email: companyEmail(role, companyCode, slug), password: GROUP_PASSWORD, role: roleName, fullName, companyCode,
});

export const ALMARAI_OWNER = groupUser('owner', 'Admin', 'Almarai Group Owner');
export const ALMARAI_HR = groupUser('hr', 'HR Director', 'Almarai Group HR Director');
export const ALMARAI_FINANCE = groupUser('finance', 'Finance Approver', 'Almarai Group Finance Approver');
export const ALMARAI_LOAN_FINANCE = groupUser('loan.finance', 'Finance', 'Almarai Loan Finance Operator');
// 'Compliance Officer', not 'HR Manager': the role is what
// CompanyComplianceProfilesController authorizes on, and it is the only non-Admin role permitted
// to AUTHOR a company compliance profile. Given HR Manager, this persona got a 403 from
// /api/company-compliance-profiles and the compliance suite saw a page with no profile on it.
export const ALMARAI_COMPLIANCE = groupUser('compliance', 'Compliance Officer', 'Almarai Group Compliance Officer');
export const ALMARAI_AUDITOR = groupUser('auditor', 'Auditor', 'Almarai Group Auditor');
// Role-catalog certification personas: one group-scoped holder for each system role the personas
// above do not already cover, so the security gate's full role matrix (security-gate/
// full-role-matrix.spec.ts) signs in as a REAL account of every role AuthSeeder installs. Created
// through the organisation's own APIs like every other identity here; no side door.
export const ALMARAI_PAYROLL_MANAGER = groupUser('payroll.manager', 'Payroll Manager', 'Almarai Payroll Manager');
export const ALMARAI_HR_OFFICER = groupUser('hr.officer', 'HR Officer', 'Almarai HR Officer');
export const ALMARAI_RECRUITER = groupUser('recruiter', 'Recruiter', 'Almarai Recruiter');
export const ALMARAI_HR_ASSISTANT = groupUser('hr.assistant', 'HR Assistant', 'Almarai HR Assistant');
export const ALMARAI_KIOSK = groupUser('kiosk', 'Kiosk Operator', 'Almarai Kiosk Operator');
export const ALMARAI_SCOPED_ADMIN: FixtureUser = {
  ...groupUser('scoped.admin', 'HR Manager', 'Almarai Selected-Companies Admin'),
  // "2 of 5" — the grant the scoped-user specs prove cannot see the other three.
  companyCodes: ['ALM-DAIRY-KSA', 'ALM-POULTRY-KSA'],
};
export const ALMARAI_DAIRY_ADMIN = companyUser('admin', 'ALM-DAIRY-KSA', 'HR Manager', 'Almarai Dairy Company Admin');
export const ALMARAI_DAIRY_HR = companyUser('hr', 'ALM-DAIRY-KSA', 'HR Manager', 'Almarai Dairy Company HR');
export const ALMARAI_BAKERY_HR = companyUser('hr', 'ALM-BAKERY-KSA', 'HR Manager', 'Almarai Bakery Company HR');
export const ALMARAI_DAIRY_PAYROLL = companyUser('payroll', 'ALM-DAIRY-KSA', 'Payroll Officer', 'Almarai Dairy Payroll Officer');

export const ALMARAI_USERS: FixtureUser[] = [
  ALMARAI_HR,
  ALMARAI_FINANCE,
  ALMARAI_LOAN_FINANCE,
  ALMARAI_COMPLIANCE,
  ALMARAI_AUDITOR,
  ALMARAI_PAYROLL_MANAGER,
  ALMARAI_HR_OFFICER,
  ALMARAI_RECRUITER,
  ALMARAI_HR_ASSISTANT,
  ALMARAI_KIOSK,
  ALMARAI_SCOPED_ADMIN,
  ALMARAI_DAIRY_ADMIN,
  ALMARAI_DAIRY_HR,
  ALMARAI_BAKERY_HR,
  ALMARAI_DAIRY_PAYROLL,
];

// Tata's identities are confined to its five companies: inside one organisation, "the Tata group" is
// the set of companies they hold, not a tenant of its own.
const tataUser = (role: string, roleName: string, fullName: string): FixtureUser => ({
  ...groupUser(role, roleName, fullName, TATA_SLUG), companyCodes: [...TATA_COMPANY_CODES],
});
export const TATA_OWNER = tataUser('owner', 'Admin', 'Tata Group Owner');
export const TATA_COMPLIANCE = tataUser('compliance', 'Compliance Officer', 'Tata Group Compliance Officer');
// Same reason as RASALMANAR_HR: someone other than the filing admin who can decide leave.
export const TATA_HR = tataUser('hr', 'HR Manager', 'Tata Group HR Manager');

export const TENANTS: FixtureTenant[] = [
  {
    slug: INTELLIFLOW_SLUG,
    name: 'IntelliFlow Systems',
    admin: INTELLIFLOW_ADMIN,
    companies: [saCompany('INTELLIFLOW-KSA')],
    users: [
      INTELLIFLOW_HR_DIR, INTELLIFLOW_HR_MGR, INTELLIFLOW_FINANCE, INTELLIFLOW_MANAGER,
      INTELLIFLOW_SUPERVISOR, INTELLIFLOW_EMP1, INTELLIFLOW_EMP2, INTELLIFLOW_AUDITOR,
    ],
    employeesPerCompany: 14,
    // pilot-critical.spec.ts asserts the dashboard counts at least this many employees. It reads the
    // number from here; the E2E_MIN_EMPLOYEES override that CI used to pass separately is retired.
    minActiveEmployees: 12,
    payroll: true,
    employeePortalLogins: [INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP2.email],
    features: ['ai_assistant', 'recruitment', 'performance', 'shifts', 'overtime', 'qiwa_integration'],
  },
  {
    slug: RASALMANAR_SLUG,
    name: 'Ras Al-Manar Trading',
    admin: RASALMANAR_ADMIN,
    companies: [saCompany('RASALMANAR-KSA')],
    users: [RASALMANAR_HR],
    // Company-scope isolation specs compare real employee rows against IntelliFlow's.
    employeesPerCompany: 4,
    minActiveEmployees: 4,
    payroll: false,
  },
  {
    slug: ALMARAI_SLUG,
    name: 'Almarai Group (E2E)',
    admin: ALMARAI_OWNER,
    companies: ALMARAI_COMPANY_CODES.map(saCompany),
    users: ALMARAI_USERS,
    employeesPerCompany: 3,
    // All 15 activate. Every third is deliberately left without an Iqama so the compliance
    // profile's "Missing" column is a real number, and readiness counts only ACTIVE employees —
    // so the gap has to be on an activated one, not on a Draft.
    minActiveEmployees: 15,
    payroll: true,
    // The modules are switched ON so the role matrix and navigation assertions measure RBAC, not an
    // unrelated module denial.
    features: ['recruitment', 'performance', 'shifts', 'overtime', 'qiwa_integration'],
  },
  {
    slug: TATA_SLUG,
    name: 'Tata Group (E2E)',
    admin: TATA_OWNER,
    companies: TATA_COMPANY_CODES.map((code) => ({ code, countryCode: 'IN', currency: 'INR' })),
    users: [TATA_COMPLIANCE, TATA_HR],
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
  if (!found) throw new Error(`[world] No fixture cluster '${slug}' is declared.`);
  return found;
};

// ── The persona registry ──────────────────────────────────────────────────────────────────────

export type PersonaScope = 'group' | 'companies';

/**
 * One identity the suites act as, with everything the preflight needs to verify it and everything a
 * report needs to say who acted. Derived from `TENANTS`, never declared a second time.
 */
export interface Persona {
  /** `<cluster-slug>|<email>`, lower-case. Unique. */
  key: string;
  label: string;
  email: string;
  password: string;
  /** The cluster the persona belongs to (a name, not a login workspace). */
  tenantSlug: string;
  /** The role the account holds. */
  role: string;
  scope: PersonaScope;
  /** The companies a 'companies'-scoped persona is confined to. */
  companyCodes: string[];
  /** True when the bootstrap links the login to a real employee record (invitation flow). */
  employeeLinked: boolean;
  /** Created by --setup-organisation (the organisation administrator) or by the bootstrap. */
  provisionedBy: 'setup' | 'bootstrap';
}

export const personaKey = (email: string, tenantSlug: string): string =>
  `${tenantSlug.toLowerCase()}|${email.toLowerCase()}`;

function toPersona(
  user: FixtureUser,
  tenant: { slug: string; employeePortalLogins?: string[]; companies?: FixtureCompany[] },
  provisionedBy: Persona['provisionedBy'],
): Persona {
  const employeeLinked = (tenant.employeePortalLogins ?? []).some((e) => e.toLowerCase() === user.email.toLowerCase());
  // An employee login is confined to its employee's company: the invitation flow
  // (AccessManagementService.InviteEmployeeLoginAsync) issues a SelectedCompanies grant for exactly
  // that company, and the bootstrap links portal logins to the FIRST company's employees.
  const codes = employeeLinked && tenant.companies?.length
    ? [tenant.companies[0].code]
    : user.companyCodes ?? (user.companyCode ? [user.companyCode] : []);
  return {
    key: personaKey(user.email, tenant.slug),
    label: `${user.fullName} (${user.role})`,
    email: user.email,
    password: user.password,
    tenantSlug: tenant.slug,
    role: user.role,
    scope: codes.length ? 'companies' : 'group',
    companyCodes: codes,
    employeeLinked,
    provisionedBy,
  };
}

/** Every identity the suites may act as. */
export const PERSONAS: Persona[] = TENANTS.flatMap((tenant) => [tenant.admin, ...tenant.users].map((u) =>
  toPersona(u, tenant, u.email.toLowerCase() === ALMARAI_OWNER.email.toLowerCase() ? 'setup' : 'bootstrap')));

/** The organisation administrator: created by --setup-organisation, it provisions everything else. */
export const ORGANISATION_ADMIN = ALMARAI_OWNER;

/** Every company code of the organisation, in cluster order. */
export const ALL_COMPANY_CODES: string[] = TENANTS.flatMap((tenant) => tenant.companies.map((c) => c.code));

export function personaForLogin(email: string, tenantSlug: string): Persona | undefined {
  const key = personaKey(email, tenantSlug);
  return PERSONAS.find((p) => p.key === key);
}

export function personaFor(user: FixtureUser, tenantSlug: string): Persona {
  const found = personaForLogin(user.email, tenantSlug);
  if (!found) throw new Error(`[world] ${user.email} is not a persona of cluster '${tenantSlug}'.`);
  return found;
}

/**
 * Where the bootstrap records what it provisioned. Its presence is how every spec distinguishes
 * "the fixture world is missing" (a hard failure with instructions) from "this assertion failed".
 */
export const WORLD_MANIFEST = 'e2e/.auth/world.json';

export interface WorldManifest {
  baseUrl: string;
  provisionedAtUtc: string;
  /** One entry per cluster; `tenantId` is the organisation's id, the same for every cluster. */
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
  + 'Databases start EMPTY (docs/DATA_ENTRY_PATHS.md): there is no organisation, user or employee\n'
  + 'until they are created. Once, after migrations and before any spec lane:\n'
  + '  1. create the organisation and import a licence:  e2e/bootstrap/setup-organisation.sh\n'
  + '  2. provision the world through its own APIs:\n'
  + '       cd frontend && npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts\n'
  + 'The fixture passwords the bootstrap used (E2E_*_PASSWORD) must still be exported.\n'
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
