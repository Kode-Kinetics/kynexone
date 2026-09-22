/**
 * THE E2E BOOTSTRAP — builds the fixture world on a freshly migrated, EMPTY database.
 *
 * ── Why ───────────────────────────────────────────────────────────────────────────────────────
 * Every demo/fixture seeder is gone (docs/DATA_ENTRY_PATHS.md). `SEED_DEMO_DATA` and
 * `SEED_ENTERPRISE_TEST_DATA` no longer exist — `Zayra.Api.Tests/Security/NoSideDoorDataTests.cs`
 * fails the build if they come back. A migrated database now contains the permission catalogue,
 * statutory rules and pricing config, and NOTHING else: no tenant, no company, no user, no
 * employee. Every e2e lane that logged in as `admin@intelliflow.com` or `owner@almarai-test.local`
 * was authenticating against rows a seeder used to write.
 *
 * This module recreates that world the only sanctioned way: as the platform owner, through
 * `/api/platform/**`, and then as each tenant's own administrator through the tenant API. There is
 * no direct SQL anywhere in it, which is the point — if a path here is missing from the product,
 * that is a product gap, and the bootstrap surfaces it instead of papering over it with an INSERT.
 *
 * ── Properties ────────────────────────────────────────────────────────────────────────────────
 * IDEMPOTENT — every step reads before it writes and skips what already exists, so it is safe to
 *   run on an already-provisioned stack and safe to re-run after a partial failure.
 * FAIL-LOUD — a step that cannot complete throws with the HTTP status and body. It never returns a
 *   half-built world for the specs to discover as a mystery assertion failure twenty minutes later.
 * NON-DESTRUCTIVE — it creates and updates; it never deletes a tenant. (The per-run Evostel fixture
 *   in e2e/limited-tenant-fixture.ts still owns its own purge, guarded by disposable-host.guard.ts.)
 * DISPOSABLE-HOST GUARDED — it writes tenants and users, so it refuses to run against anything but
 *   loopback or an explicitly allowlisted throwaway stack.
 */
import { writeFile, mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';
import { assertDisposableHost } from '../disposable-host.guard';
import {
  PLATFORM_EMAIL, PLATFORM_PASSWORD, TENANTS, WORLD_MANIFEST,
  type FixtureCompany, type FixtureTenant, type FixtureUser, type WorldManifest,
} from '../world';

const API_BASE = (process.env.E2E_API_BASE_URL ?? 'http://localhost:5117').replace(/\/$/, '');

/** Named employees the benefits-admin spec searches for by name, in creation order. */
const NAMED_EMPLOYEES = ['Liu Wei', 'Carlos Mendez', 'Aisha Al-Harbi', 'Omar Siddiqui'];
const FILLER_NAMES = [
  'Noura Al-Qahtani', 'Rashid Al-Otaibi', 'Fatima Zahra', 'Daniel Okonkwo', 'Priya Nair',
  'Hassan Al-Dosari', 'Mariam Youssef', 'Victor Almeida', 'Sara Ahmadi', 'Bilal Rahman',
  'Layla Al-Mutairi', 'Kenji Tanaka', 'Grace Mwangi', 'Tomas Novak',
];
/** The grade benefits-admin.spec.ts requires, and which only some employees hold. */
const GRADE = { code: 'IFL-STD', name: 'IFL Standard', level: 5, min: 3000, mid: 9000, max: 60000 };

// ── HTTP ──────────────────────────────────────────────────────────────────────────────────────

interface Res<T> { status: number; body: T; text: string }

async function call<T = any>(
  method: string, path: string, opts: { token?: string; body?: unknown; companyId?: string } = {},
): Promise<Res<T>> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  if (opts.companyId) headers['X-Company-Id'] = opts.companyId;
  const response = await fetch(`${API_BASE}${path}`, {
    method,
    headers,
    body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
  });
  const text = await response.text();
  let body: any = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* non-JSON body kept in `text` */ }
  return { status: response.status, body, text };
}

/** Throws with the status AND the body — the two things needed to fix a failing bootstrap. */
function expectOk<T>(res: Res<T>, what: string, accept: number[] = [200, 201, 204]): Res<T> {
  if (!accept.includes(res.status)) {
    throw new Error(
      `[bootstrap] ${what} failed: HTTP ${res.status}\n${res.text.slice(0, 600)}\n`
      + `(expected one of ${accept.join(', ')} from ${API_BASE})`,
    );
  }
  return res;
}

const items = (body: any): any[] =>
  Array.isArray(body) ? body
    : Array.isArray(body?.items) ? body.items
      : Array.isArray(body?.Items) ? body.Items : [];

// ── Authentication ────────────────────────────────────────────────────────────────────────────

async function platformLogin(): Promise<string> {
  const res = await call('POST', '/api/platform/auth/login', {
    body: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD },
  });
  if (res.status !== 200) {
    throw new Error(
      `[bootstrap] Platform-owner login failed for ${PLATFORM_EMAIL}: HTTP ${res.status}.\n`
      + `${res.text.slice(0, 300)}\n\n`
      + 'The platform owner is created ONCE, at API boot, by PlatformOwnerBootstrap — and only when\n'
      + 'PLATFORM_ADMIN_PASSWORD is set in the API process (plus PLATFORM_ADMIN_BOOTSTRAP=true on\n'
      + 'Production/dedicated deployments). Start the backend with:\n'
      + `  PLATFORM_ADMIN_EMAIL=${PLATFORM_EMAIL} PLATFORM_ADMIN_PASSWORD=<the same value this run uses>\n`
      + 'See docs/DATA_ENTRY_PATHS.md §1.',
    );
  }
  const token = res.body?.token ?? res.body?.accessToken;
  if (!token) throw new Error('[bootstrap] Platform login returned no token.');
  return token as string;
}

async function tenantLogin(email: string, password: string, slug: string): Promise<string | null> {
  const res = await call('POST', '/api/auth/login', { body: { email, password, tenantSlug: slug } });
  if (res.status !== 200) return null;
  return (res.body?.accessToken ?? res.body?.token) as string;
}

/**
 * Log in, or say precisely why not. A 429 here is the login limiter, not a bad password, and
 * conflating the two has cost this repo entire wasted CI runs before.
 */
async function requireTenantLogin(user: FixtureUser, slug: string): Promise<string> {
  const token = await tenantLogin(user.email, user.password, slug);
  if (token) return token;
  const probe = await call('POST', '/api/auth/login', {
    body: { email: user.email, password: user.password, tenantSlug: slug },
  });
  throw new Error(
    `[bootstrap] ${user.email} cannot log in to '${slug}' (HTTP ${probe.status}).\n`
    + (probe.status === 429
      ? 'That is the login rate limiter, not a wrong password. Raise RateLimit__LoginPermitLimit on '
        + 'the DISPOSABLE bootstrap stack only, or pace the run.'
      : 'The account exists but the password does not match. The fixture passwords are generated per '
        + 'CI run: a database left over from an earlier run holds the OLD password. Recreate the '
        + 'database (it is disposable) or re-export the same passwords.'),
  );
}

// ── Tenants and companies ─────────────────────────────────────────────────────────────────────

async function ensureTenant(platformToken: string, fixture: FixtureTenant): Promise<string> {
  const list = expectOk(await call('GET', '/api/platform/tenants', { token: platformToken }), 'list tenants');
  const existing = items(list.body).find((t: any) => t.slug === fixture.slug);
  if (existing) return existing.id;

  const created = await call('POST', '/api/platform/tenants', {
    token: platformToken,
    body: {
      name: fixture.name,
      slug: fixture.slug,
      adminEmail: fixture.admin.email,
      adminFullName: fixture.admin.fullName,
      adminPassword: fixture.admin.password,
      accountType: fixture.accountType,
      plan: fixture.plan,
      maxUsers: fixture.maxUsers,
      maxEmployees: fixture.maxEmployees,
      maxCompanies: fixture.maxCompanies,
      billingEmail: `billing@${fixture.slug}.test`,
      billingCycle: 'Monthly',
      monthlyAmount: 0,
      currencyCode: 'SAR',
    },
  });
  expectOk(created, `create tenant '${fixture.slug}'`, [201]);
  return created.body.tenantId as string;
}

/**
 * Give the tenant exactly the companies the fixture declares, identified by CODE.
 *
 * CreateTenant now bears the tenant's first company itself, named after the TENANT. The group
 * suites resolve companies by code (`ALM-DAIRY-KSA`), so that first company is RENAMED to the first
 * declared code rather than left beside it — otherwise `almarai-test` would hold six companies and
 * `group-admin.spec.ts`'s `companies.length === 5` would be wrong for a reason that has nothing to
 * do with the product.
 */
async function ensureCompanies(
  platformToken: string, tenantId: string, adminToken: string, fixture: FixtureTenant,
): Promise<Array<{ code: string; id: string }>> {
  const existing = items(expectOk(
    await call('GET', '/api/companies?page=1&pageSize=100', { token: adminToken }), 'list companies',
  ).body);

  const byCode = new Map<string, string>();
  for (const company of existing) {
    const name = String(company.legalNameEn ?? company.LegalNameEn ?? '');
    if (fixture.companies.some((c) => c.code === name)) byCode.set(name, company.id ?? company.Id);
  }

  // Rename the tenant-named starter company onto the first declared code.
  const starter = existing.find((c: any) => {
    const name = String(c.legalNameEn ?? c.LegalNameEn ?? '');
    return !fixture.companies.some((f) => f.code === name);
  });
  const first = fixture.companies[0];
  if (starter && !byCode.has(first.code)) {
    expectOk(await call('PUT', `/api/companies/${starter.id ?? starter.Id}`, {
      token: adminToken, body: companyBody(first, fixture),
    }), `rename starter company to '${first.code}'`);
    byCode.set(first.code, starter.id ?? starter.Id);
  }

  for (const company of fixture.companies) {
    if (byCode.has(company.code)) continue;
    const created = await call('POST', `/api/platform/tenants/${tenantId}/companies`, {
      token: platformToken, body: companyBody(company, fixture),
    });
    expectOk(created, `create company '${company.code}' in '${fixture.slug}'`);
    byCode.set(company.code, created.body.companyId as string);
  }

  return fixture.companies.map((c) => ({ code: c.code, id: byCode.get(c.code)! }));
}

function companyBody(company: FixtureCompany, fixture: FixtureTenant) {
  return {
    legalNameEn: company.code,
    legalNameAr: company.code,
    tradeName: company.code,
    countryCode: company.countryCode,
    // Required by CompanyRequest; deterministic so a re-run does not churn the row.
    registrationNumber: `CR-${company.code}`,
    taxNumber: `VAT-${company.code}`,
    defaultCurrency: company.currency,
    emailDomain: `${fixture.slug}.local`,
    isActive: true,
  };
}

// ── Users and their company scope ─────────────────────────────────────────────────────────────

async function ensureUsers(
  platformToken: string, tenantId: string, fixture: FixtureTenant,
): Promise<Map<string, string>> {
  const existing = items(expectOk(
    await call('GET', `/api/platform/tenants/${tenantId}/users`, { token: platformToken }), 'list tenant users',
  ).body);
  const byEmail = new Map<string, string>(
    existing.map((u: any) => [String(u.email ?? u.Email).toLowerCase(), u.id ?? u.Id]),
  );

  for (const user of fixture.users) {
    if (byEmail.has(user.email.toLowerCase())) continue;
    const created = await call('POST', `/api/platform/tenants/${tenantId}/users`, {
      token: platformToken,
      body: { email: user.email, password: user.password, fullName: user.fullName, roleName: user.role },
    });
    expectOk(created, `create user '${user.email}' in '${fixture.slug}'`);
    byEmail.set(user.email.toLowerCase(), created.body.Id ?? created.body.id);
  }
  return byEmail;
}

/**
 * Confine the company-scoped identities. Without this every user created above is group-scope (the
 * platform endpoint has no scope parameter), and the entire Chrome security gate — which exists to
 * prove company confinement — would pass vacuously because nobody is confined.
 */
async function ensureEntityGrants(
  adminToken: string, fixture: FixtureTenant, userIds: Map<string, string>,
  companies: Array<{ code: string; id: string }>,
): Promise<void> {
  const scoped = fixture.users.filter((u) => u.companyCode || u.companyCodes);
  if (scoped.length === 0) return;

  const current = items(expectOk(
    await call('GET', '/api/access/entity-grants', { token: adminToken }), 'list entity grants',
  ).body);
  const held = new Set(current.map((g: any) => `${g.userId ?? g.UserId}|${g.companyId ?? g.CompanyId}`));

  for (const user of scoped) {
    const userId = userIds.get(user.email.toLowerCase());
    if (!userId) throw new Error(`[bootstrap] No user id for '${user.email}' — cannot scope it.`);
    const codes = user.companyCodes ?? [user.companyCode!];
    for (const code of codes) {
      const company = companies.find((c) => c.code === code);
      if (!company) throw new Error(`[bootstrap] '${user.email}' is scoped to unknown company '${code}'.`);
      if (held.has(`${userId}|${company.id}`)) continue;
      const res = await call('POST', '/api/access/entity-grants', {
        token: adminToken,
        body: { userId, companyId: company.id, role: user.role, grantMode: 'SelectedCompanies' },
      });
      // 409 is "this grant already exists" — idempotent, not an error.
      expectOk(res, `grant ${user.email} → ${code}`, [200, 201, 409]);
    }
  }
}

// ── Employees ─────────────────────────────────────────────────────────────────────────────────

async function ensureGrade(adminToken: string): Promise<string | null> {
  const list = await call('GET', '/api/grades?page=1&pageSize=100', { token: adminToken });
  if (list.status === 200) {
    const found = items(list.body).find((g: any) => (g.code ?? g.Code) === GRADE.code);
    if (found) return found.id ?? found.Id;
  }
  const created = await call('POST', '/api/grades', {
    token: adminToken,
    body: {
      code: GRADE.code, name: GRADE.name, band: 'Standard', level: GRADE.level,
      minSalary: GRADE.min, midSalary: GRADE.mid, maxSalary: GRADE.max, currency: 'SAR', isActive: true,
    },
  });
  expectOk(created, `create grade '${GRADE.code}'`);
  return created.body.id ?? created.body.Id;
}

/**
 * Employees, with deterministic `<COMPANY-CODE>-E<n>` codes — the exact shape the group suites
 * assert company confinement with (`ALM-DAIRY-KSA-E1` must never appear for a bakery user).
 *
 * The first two carry the names `benefits-admin.spec.ts` searches for, and only the FIRST holds the
 * IFL-STD grade, because that spec's eligibility rule needs one employee in the grade and one out.
 * Every third employee is deliberately left without an Iqama compliance record, which is what
 * `group-company/compliance.spec.ts` counts as the "Missing" column for IqamaNumber.
 */
async function ensureEmployees(
  adminToken: string, fixture: FixtureTenant, companies: Array<{ code: string; id: string }>,
  gradeId: string | null,
): Promise<number> {
  let created = 0;
  for (const company of companies) {
    const existing = await call(
      `GET`, `/api/employees?page=1&pageSize=200&search=${encodeURIComponent(`${company.code}-E`)}`,
      { token: adminToken, companyId: company.id },
    );
    const have = new Set(items(existing.body).map((e: any) => String(e.employeeCode ?? e.EmployeeCode ?? '')));

    for (let n = 1; n <= fixture.employeesPerCompany; n++) {
      const code = `${company.code}-E${n}`;
      if (have.has(code)) continue;
      // The first names are literal, not suffixed: benefits-admin.spec.ts searches for the exact
      // strings "Liu Wei" and "Carlos Mendez".
      const name = NAMED_EMPLOYEES[n - 1] ?? `${FILLER_NAMES[(n - 1) % FILLER_NAMES.length]} ${n}`;
      const isSaudi = company.countryCode === 'SA';
      // An expatriate with no Iqama on file. Deliberate, and only outside IntelliFlow: it is what
      // gives group-company/compliance.spec.ts a non-zero "Missing" count for the Iqama requirement.
      // Their activation is correctly blocked by the statutory guard, so they stay in Draft.
      const expatWithGap = isSaudi && fixture.slug !== 'intelliflow' && n % 3 === 0;
      const res = await call('POST', '/api/employees', {
        token: adminToken,
        companyId: company.id,
        body: {
          employeeCode: code,
          manualEmployeeCode: true,
          englishName: name,
          gender: n % 2 === 0 ? 'Female' : 'Male',
          dateOfBirth: '1990-01-15',
          nationality: expatWithGap ? 'Indian' : isSaudi ? 'Saudi' : 'Indian',
          personalEmail: `${code.toLowerCase()}@${fixture.slug}.local`,
          companyId: company.id,
          // Only the first employee holds the grade — the benefits eligibility rule needs a
          // population it INCLUDES and a population it EXCLUDES to prove anything.
          gradeId: n === 1 ? gradeId : null,
          jobTitle: 'Specialist',
          employmentType: 'FullTime',
          contractType: 'Unlimited',
          joiningDate: '2024-01-01T00:00:00Z',
          payrollProfile: {
            paymentMethod: 'BankTransfer',
            salaryCurrency: company.code.endsWith('-IN') ? 'INR' : 'SAR',
            wpsEligible: true,
            eosbEligible: true,
            socialInsuranceReference: isSaudi ? `GOSI-${code}` : null,
          },
          salaryBreakdown: {
            basicSalary: 6000 + n * 250,
            housingAllowance: 1500,
            transportAllowance: 500,
            effectiveDate: '2024-01-01',
            currency: company.code.endsWith('-IN') ? 'INR' : 'SAR',
          },
          // The statutory identity set the activation guard blocks on. The keys are the SNAKE_CASE
          // vocabulary EmployeeManagementService.ApplyComplianceRecord understands — `IqamaNumber`
          // and friends fall through its switch untouched, so the employee would be created and
          // then refuse to activate, with nothing in the response saying why.
          // The set is the union of what EmployeeReadinessEvaluator's statutory floor asks for
          // across the Gulf packs a tenant is provisioned with, not just the company's own country.
          complianceRecords: expatWithGap || !isSaudi ? [] : [
            idRecord('civil_id', 'Civil ID', `1${String(200000000 + seq).slice(0, 9)}`),
            idRecord('id_number', 'Government ID number', `1${String(300000000 + seq).slice(0, 9)}`),
            idRecord('gosi_reference', 'GOSI reference', `GOSI-${code}`),
            idRecord('iqama_number', 'Iqama Number', `2${String(400000000 + seq).slice(0, 9)}`),
            idRecord('emirates_id', 'Emirates ID', `784-1990-${String(1000000 + seq).slice(0, 7)}-1`),
            idRecord('work_permit', 'Work permit number', `WP-${code}`),
            idRecord('passport_number', 'Passport number', `P${String(10000000 + seq).slice(0, 8)}`),
          ],
          acknowledgeDuplicate: true,
        },
      });
      expectOk(res, `create employee '${code}'`, [200, 201]);
      created++;
    }
  }
  return created;
}

let seq = 0;
const idRecord = (fieldKey: string, fieldLabel: string, fieldValue: string) => {
  seq++;
  return { countryCode: 'SA', fieldKey, fieldLabel, fieldValue, isSensitive: true, isRequired: true };
};

/**
 * Move employees out of Draft, because Draft employees are invisible to the dashboard's
 * `activeEmployees`, to payroll population and to most module lists — so a tenant full of Drafts
 * looks exactly like an unprovisioned one to every spec that counts rows.
 *
 * Activation is the product's own statutory guard (`EmployeeActivationGuard`), not a flag flip. An
 * employee it refuses is reported, never forced, and the caller asserts a floor afterwards: silently
 * ending up with zero Active employees is the failure this whole bootstrap exists to prevent.
 */
async function activateEmployees(adminToken: string, slug: string, floor: number): Promise<number> {
  const list = expectOk(
    await call('GET', '/api/employees?page=1&pageSize=200', { token: adminToken }), 'list employees',
  );
  const rows = items(list.body);
  const blocked: string[] = [];
  let active = 0;

  for (const row of rows) {
    const status = String(row.status ?? row.Status ?? '');
    if (status === 'Active') { active++; continue; }
    const res = await call('POST', `/api/employees/${row.id ?? row.Id}/activate`, {
      token: adminToken, body: { status: 'Active', reason: 'e2e fixture world bootstrap' },
    });
    if (res.status === 200) { active++; continue; }
    // Name the missing keys, not just the count. "2 required detail(s) missing" is unactionable;
    // "EmiratesId, WorkPermitNumber" tells the next person exactly what to add here.
    const keys = (res.body?.blocking ?? []).map((b: any) => b.key).join(', ');
    blocked.push(
      `${row.employeeCode ?? row.Id}: HTTP ${res.status} ${(res.body?.message ?? res.text).slice(0, 100)}`
      + (keys ? ` [${keys}]` : ''),
    );
  }

  if (active < floor) {
    throw new Error(
      `[bootstrap] '${slug}' has ${active} ACTIVE employees; at least ${floor} are required.\n`
      + 'Draft employees are excluded from the dashboard counts, payroll population and the module\n'
      + 'lists, so the specs would see an empty product and report it as a rendering bug.\n'
      + `Refused activations:\n  ${blocked.slice(0, 8).join('\n  ')}`,
    );
  }
  if (blocked.length) {
    console.log(`[bootstrap] ${slug}: ${active} active, ${blocked.length} left in Draft by the activation guard (expected).`);
  }
  return active;
}

// ── Orchestration ─────────────────────────────────────────────────────────────────────────────

export interface ProvisionResult extends WorldManifest { employeesCreated: number }

export async function provisionWorld(baseUrl: string): Promise<ProvisionResult> {
  // Creating tenants and users is a write. Refuse anything that is not a throwaway stack, for the
  // same reason limited-tenant-fixture.ts refuses: a stale PLAYWRIGHT_BASE_URL plus real platform
  // credentials would otherwise write fixture accounts into a live environment.
  assertDisposableHost(baseUrl, 'provision the e2e fixture world');
  assertDisposableHost(API_BASE, 'provision the e2e fixture world');

  const started = Date.now();
  const platformToken = await platformLogin();
  const manifest: ProvisionResult = {
    baseUrl, provisionedAtUtc: new Date().toISOString(), tenants: [], employeesCreated: 0,
  };

  for (const fixture of TENANTS) {
    const tenantId = await ensureTenant(platformToken, fixture);
    const adminToken = await requireTenantLogin(fixture.admin, fixture.slug);
    const companies = await ensureCompanies(platformToken, tenantId, adminToken, fixture);
    const userIds = await ensureUsers(platformToken, tenantId, fixture);
    await ensureEntityGrants(adminToken, fixture, userIds, companies);
    const gradeId = fixture.slug === 'intelliflow' ? await ensureGrade(adminToken) : null;
    manifest.employeesCreated += await ensureEmployees(adminToken, fixture, companies, gradeId);
    const active = await activateEmployees(adminToken, fixture.slug, fixture.minActiveEmployees);
    manifest.tenants.push({ slug: fixture.slug, tenantId, companies });
    console.log(
      `[bootstrap] ${fixture.slug}: ${companies.length} compan${companies.length === 1 ? 'y' : 'ies'}, `
      + `${fixture.users.length + 1} users, ${active} active employees.`,
    );
  }

  await mkdir(dirname(WORLD_MANIFEST), { recursive: true });
  await writeFile(WORLD_MANIFEST, JSON.stringify(manifest, null, 2), { mode: 0o600 });
  console.log(
    `[bootstrap] Fixture world ready in ${((Date.now() - started) / 1000).toFixed(1)}s — `
    + `${manifest.tenants.length} tenants, ${manifest.employeesCreated} new employees. `
    + `Manifest: ${WORLD_MANIFEST}`,
  );
  return manifest;
}
