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
/**
 * A structurally valid Saudi IBAN (SA + 2 check digits + 22 BBAN digits), check digits computed by
 * ISO 13616 mod-97. The payroll validation engine runs the real mod-97 check and raises a BLOCKING
 * `INVALID_IBAN` error, so a made-up 24-character string stops every run at approval — and a
 * missing one raises `MISSING_IBAN`, which is what held the fixture run in Processed.
 */
function saudiIban(seed: number): string {
  // A Saudi IBAN is exactly 24 characters: 'SA' + 2 check digits + a 20-digit BBAN.
  const bban = `80${String(seed).padStart(18, '0')}`.slice(-20);
  // Rearrange to BBAN + country code + '00', mapping S→28 and A→10, then take the remainder mod 97.
  let remainder = 0;
  for (const ch of `${bban}281000`) remainder = (remainder * 10 + Number(ch)) % 97;
  return `SA${String(98 - remainder).padStart(2, '0')}${bban}`;
}

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
): Promise<Array<{ code: string; id: string; countryCode: string }>> {
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

  return fixture.companies.map((c) => ({ code: c.code, id: byCode.get(c.code)!, countryCode: c.countryCode }));
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
 * Give every provisioned user its entity scope. Two failure modes make this mandatory, not optional:
 *
 *  • WITHOUT A GRANT, A NON-ADMIN USER SEES NOTHING. `POST /api/platform/tenants/{id}/users` sets
 *    `IsGroupScope` only for the Admin role, and it has no scope parameter at all, so an HR Manager
 *    or Finance Approver it creates resolves to ZERO accessible companies. Every company-owned row
 *    is then filtered out of their queries — the payroll maker/checker got a flat 404 from
 *    `/api/payroll/runs/{id}/approve` for a run that plainly existed. A 404 reads as "no such run",
 *    not as "this account has no company access", which is what cost the diagnosis.
 *  • WITHOUT A *CONFINED* GRANT, THE SECURITY GATE PROVES NOTHING. If the company-scoped identities
 *    were group-scope instead, every "bakery data is not visible to the dairy user" assertion would
 *    pass because there is nothing to confine.
 *
 * So group identities get `AllCurrentAndFutureCompanies` and company identities get one
 * `SelectedCompanies` grant per company they are entitled to — both real product grant modes,
 * created through the product's own endpoint.
 */
async function ensureEntityGrants(
  adminToken: string, fixture: FixtureTenant, userIds: Map<string, string>,
  companies: Array<{ code: string; id: string; countryCode: string }>,
): Promise<void> {
  if (fixture.users.length === 0) return;

  const current = items(expectOk(
    await call('GET', '/api/access/entity-grants', { token: adminToken }), 'list entity grants',
  ).body);
  const held = new Set(current.map((g: any) => `${g.userId ?? g.UserId}|${g.companyId ?? g.CompanyId ?? 'all'}`));

  for (const user of fixture.users) {
    const userId = userIds.get(user.email.toLowerCase());
    if (!userId) throw new Error(`[bootstrap] No user id for '${user.email}' — cannot scope it.`);
    const codes = user.companyCodes ?? (user.companyCode ? [user.companyCode] : null);

    if (codes === null) {
      if (held.has(`${userId}|all`)) continue;
      const res = await call('POST', '/api/access/entity-grants', {
        token: adminToken,
        body: { userId, role: user.role, grantMode: 'AllCurrentAndFutureCompanies' },
      });
      // 409 is "this grant already exists" — idempotent, not an error.
      expectOk(res, `grant ${user.email} → all companies`, [200, 201, 409]);
      continue;
    }

    for (const code of codes) {
      const company = companies.find((c) => c.code === code);
      if (!company) throw new Error(`[bootstrap] '${user.email}' is scoped to unknown company '${code}'.`);
      if (held.has(`${userId}|${company.id}`)) continue;
      const res = await call('POST', '/api/access/entity-grants', {
        token: adminToken,
        body: { userId, companyId: company.id, role: user.role, grantMode: 'SelectedCompanies' },
      });
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
  adminToken: string, fixture: FixtureTenant,
  companies: Array<{ code: string; id: string; countryCode: string }>,
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
      // Every third employee outside IntelliFlow is a SAUDI NATIONAL with no Iqama on file — which
      // is not a data defect, it is what a Saudi national's record looks like. That is precisely what
      // gives group-company/compliance.spec.ts a non-zero "Missing" count for the Iqama requirement.
      //
      // The gap has to sit on an ACTIVE employee: ComplianceReadinessCalculator counts only
      // `Status == "Active"` rows, so a Draft employee with a missing Iqama is invisible to readiness
      // and the "Missing" cell reads 0. It also has to sit on a national rather than an expatriate,
      // because the activation guard's required set IS nationality-aware: it demands an Iqama of a
      // non-Saudi and not of a Saudi. An expatriate with the gap simply never activates.
      const iqamaGap = fixture.slug !== 'intelliflow' && n % 3 === 0;
      const res = await call('POST', '/api/employees', {
        token: adminToken,
        companyId: company.id,
        body: {
          employeeCode: code,
          manualEmployeeCode: true,
          englishName: name,
          gender: n % 2 === 0 ? 'Female' : 'Male',
          dateOfBirth: '1990-01-15',
          nationality: isSaudi || iqamaGap ? 'Saudi' : 'Indian',
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
            bankName: 'Al Rajhi Bank',
            // Required, and really validated: see saudiIban() above.
            iban: saudiIban(6080101675 + seq),
            accountNumber: String(6080101675 + seq),
            paymentMethod: 'BankTransfer',
            salaryCurrency: company.code.endsWith('-IN') ? 'INR' : 'SAR',
            wpsEligible: true,
            eosbEligible: true,
            socialInsuranceReference: isSaudi ? `GOSI-${code}` : null,
            // KSA regulatory reporting blocks approval without it.
            molId: `MOL-${code}`,
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
          // across EVERY Gulf pack a tenant is provisioned with, not just the company's own country:
          // a Saudi national in a Saudi company is currently refused activation until it also carries
          // an Emirates ID and a Qatar ID. That looks like a product bug rather than a fixture
          // concern, but the bootstrap's job is to build the world the product will accept, so it
          // supplies the whole set and the oddity is recorded here rather than hidden.
          complianceRecords: [
            idRecord('civil_id', 'Civil ID', `1${String(200000000 + seq).slice(0, 9)}`),
            idRecord('id_number', 'Government ID number', `1${String(300000000 + seq).slice(0, 9)}`),
            idRecord('gosi_reference', 'GOSI reference', `GOSI-${code}`),
            ...(iqamaGap ? [] : [idRecord('iqama_number', 'Iqama Number', `2${String(400000000 + seq).slice(0, 9)}`)]),
            idRecord('emirates_id', 'Emirates ID', `784-1990-${String(1000000 + seq).slice(0, 7)}-1`),
            idRecord('work_permit', 'Work permit number', `WP-${code}`),
            idRecord('passport_number', 'Passport number', `P${String(10000000 + seq).slice(0, 8)}`),
            idRecord('qid', 'Qatar ID', `2${String(8000000 + seq).slice(0, 10)}`),
            idRecord('residency_number', 'Residency number', `RES-${code}`),
            idRecord('labor_card_number', 'Labour card number', `LC-${code}`),
            idRecord('muqeem_reference', 'Muqeem reference', `MQ-${code}`),
            idRecord('qiwa_contract_reference', 'Qiwa contract reference', `QC-${code}`),
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

// ── Compliance profiles ───────────────────────────────────────────────────────────────────────

/**
 * One active compliance profile per company, with the statutory fields the readiness view counts.
 *
 * `CompanyComplianceProfilesController.Readiness` returns an EMPTY required-fields list when a
 * company has no profile, so /compliance-profiles renders a page with no Country row and no table —
 * and `group-company/compliance.spec.ts` times out waiting for a row that nothing will ever draw.
 * Nothing creates these by default; they are a tenant-configuration step, so the bootstrap performs
 * it as the tenant's own Compliance Officer, which is the only non-Admin role permitted to.
 */
async function ensureComplianceProfiles(
  adminToken: string, companies: Array<{ code: string; id: string; countryCode: string }>,
): Promise<number> {
  const existing = items((await call(
    'GET', '/api/company-compliance-profiles', { token: adminToken },
  )).body);
  const have = new Set(existing.map((p: any) => String(p.companyId ?? p.CompanyId)));

  // IqamaNumber is deliberately first: the KSA suite reads its "Missing" cell, and the expatriate
  // employees the bootstrap leaves without one are what make that number non-zero.
  const required: Record<string, string[]> = {
    SA: ['IqamaNumber', 'GosiReference', 'IdNumber'],
    IN: ['IdNumber', 'PassportNumber'],
  };

  let created = 0;
  for (const company of companies) {
    if (have.has(company.id)) continue;
    const fields = required[company.countryCode] ?? ['IdNumber'];
    const res = await call('POST', '/api/company-compliance-profiles', {
      token: adminToken, companyId: company.id,
      body: {
        companyId: company.id,
        countryCode: company.countryCode,
        jurisdiction: company.countryCode,
        compliancePack: `${company.countryCode}-BASE`,
        effectiveFrom: '2024-01-01',
        status: 'Active',
        requiredFieldsJson: JSON.stringify(fields.map((field) => ({ field, failClosed: false }))),
        notes: 'Provisioned by the e2e fixture-world bootstrap.',
      },
    });
    expectOk(res, `create the compliance profile for '${company.code}'`, [200, 201]);
    created++;
  }
  return created;
}

// ── Leave, attendance and the approval queue ──────────────────────────────────────────────────

/**
 * The three module lists `pilot-critical.spec.ts` asserts are NOT empty — /attendance, /leave and
 * /approvals — plus the leave and attendance rows `tenant-isolation.spec.ts` counts per tenant.
 *
 * `expectNonEmptyList` calls an empty Attendance or Leave screen "the blank-module failure the pilot
 * feared" and fails, correctly: an HR product showing no leave and no attendance is indistinguishable
 * to a viewer from one whose API is returning 500s. So the world has to contain some.
 *
 * Every row goes in through the product: a leave BALANCE adjustment (HR's own tool) followed by a
 * real leave submission, which is what puts an item in the approval queue, then a CSV punch import
 * followed by the attendance processor. Nothing is inserted behind the modules' backs.
 */
async function ensureLeaveAndAttendance(
  adminToken: string, slug: string, companies: Array<{ code: string; id: string; countryCode: string }>,
): Promise<string> {
  const leaveTypes = items((await call('GET', '/api/leave/types', { token: adminToken })).body);
  const annual = leaveTypes.find((t: any) => (t.code ?? t.Code) === 'ANNUAL') ?? leaveTypes[0];
  if (!annual) return 'no leave types provisioned';

  const company = companies[0];
  const employees = items((await call(
    'GET', '/api/employees?page=1&pageSize=200', { token: adminToken, companyId: company.id },
  )).body).filter((e: any) => String(e.status ?? e.Status) === 'Active').slice(0, 3);
  if (employees.length === 0) return 'no active employees to give leave or attendance to';

  const existingLeave = (await call('GET', '/api/leave/requests?page=1&pageSize=5', { token: adminToken })).body;
  let leaveCreated = 0;
  if (items(existingLeave).length === 0) {
    for (const [index, employee] of employees.entries()) {
      const id = employee.id ?? employee.Id;
      // Entitlement first: LeaveRequestsController refuses a request with "Insufficient leave
      // balance", and rightly so — the balance is the product's control, not a formality.
      expectOk(await call('POST', '/api/leave/balances/adjust', {
        token: adminToken, companyId: company.id,
        body: { employeeId: id, leaveTypeId: annual.id, amount: 21, reason: 'e2e fixture world opening balance' },
      }), `grant leave balance to employee ${id}`, [200, 201, 204]);

      const start = new Date(Date.now() + (14 + index * 7) * 86_400_000);
      const end = new Date(start.getTime() + 2 * 86_400_000);
      const res = await call('POST', '/api/leave/requests', {
        token: adminToken, companyId: company.id,
        body: {
          employeeId: id, leaveTypeId: annual.id,
          startDate: start.toISOString().slice(0, 10), endDate: end.toISOString().slice(0, 10),
          reason: 'e2e fixture world', isEmergency: false,
        },
      });
      if (res.status === 200 || res.status === 201) leaveCreated++;
      else console.log(
        `[bootstrap] ${slug}: leave request for employee ${id} refused `
        + `(HTTP ${res.status}: ${(res.body?.message ?? res.text).slice(0, 140)}).`,
      );
    }
  }

  // Attendance: two punches a day for the last five days, then the processor turns the raw events
  // into the daily rows the /attendance screen actually lists.
  const from = new Date(Date.now() - 6 * 86_400_000).toISOString().slice(0, 10);
  const to = new Date(Date.now() - 1 * 86_400_000).toISOString().slice(0, 10);
  const existingAttendance = (await call(
    'GET', `/api/attendance/daily?from=${from}&to=${to}&page=1&pageSize=5`, { token: adminToken },
  )).body;
  let punches = 0;
  if (items(existingAttendance).length === 0) {
    const rows = ['employeeCode,punchTimestamp,punchDirection'];
    for (let day = 5; day >= 1; day--) {
      const date = new Date(Date.now() - day * 86_400_000).toISOString().slice(0, 10);
      for (const employee of employees) {
        rows.push(`${employee.employeeCode},${date}T05:00:00Z,In`);
        rows.push(`${employee.employeeCode},${date}T14:00:00Z,Out`);
        punches += 2;
      }
    }
    expectOk(await call('POST', '/api/attendance/events/import', {
      token: adminToken, companyId: company.id,
      body: { fileName: 'e2e-fixture-world.csv', csvContent: rows.join('\n') },
    }), 'import attendance punches', [200, 201]);
    // No X-Company-Id here, deliberately. AttendanceController.Process refuses a tenant-wide
    // reprocess from a caller whose scope is not unrestricted, and pinning a company IS a narrowing —
    // so passing the header the other calls use turns the group admin into a 403.
    expectOk(await call('POST', '/api/attendance/process', {
      token: adminToken,
      body: { fromDate: from, toDate: to, employeeId: null },
    }), 'process attendance punches', [200, 201]);
  }
  return `${leaveCreated} leave request(s), ${punches} punch(es)`;
}

// ── Salaries ──────────────────────────────────────────────────────────────────────────────────

/**
 * A salary structure per company, assigned to every one of its employees.
 *
 * `EmployeeCreateRequest.salaryBreakdown` alone is NOT enough, and the way it fails is silent: the
 * service only materialises an assignment when the employee resolves to a structure (via a grade or
 * an explicit code), so a 14-employee tenant produced a payroll run with `employeeCount: 14` and a
 * gross total of one person's package. Every downstream money assertion would then be comparing
 * numbers that are internally consistent and completely wrong.
 */
async function ensureSalaries(
  adminToken: string, companies: Array<{ code: string; id: string; countryCode: string }>,
): Promise<number> {
  const structures = items(expectOk(
    await call('GET', '/api/payroll/salary-structures', { token: adminToken }), 'list salary structures',
  ).body);
  const assigned = new Set(items((await call(
    'GET', '/api/payroll/employee-salary-structures?page=1&pageSize=500', { token: adminToken },
  )).body).map((a: any) => String(a.employeeId ?? a.EmployeeId)));

  let count = 0;
  for (const company of companies) {
    const currency = company.countryCode === 'IN' ? 'INR' : 'SAR';
    const structureCode = `E2E-STD-${company.code}`;
    let structureId = structures.find((s: any) => (s.code ?? s.Code) === structureCode)?.id;
    if (!structureId) {
      const created = await call('POST', '/api/payroll/salary-structures', {
        token: adminToken, companyId: company.id,
        body: {
          code: structureCode, name: `${company.code} standard structure`, currency,
          effectiveDate: '2024-01-01', companyId: company.id, components: [], isActive: true,
        },
      });
      expectOk(created, `create salary structure '${structureCode}'`, [200, 201]);
      structureId = created.body.id ?? created.body.Id;
    }

    const employees = items((await call(
      'GET', '/api/employees?page=1&pageSize=200', { token: adminToken, companyId: company.id },
    )).body);
    for (const employee of employees) {
      const id = employee.id ?? employee.Id;
      if (assigned.has(String(id))) continue;
      const n = Number(/-E(\d+)$/.exec(String(employee.employeeCode ?? ''))?.[1] ?? 1);
      const res = await call('POST', '/api/payroll/employee-salary-structures', {
        token: adminToken, companyId: company.id,
        body: {
          employeeId: id, salaryStructureId: structureId,
          basicSalary: 6000 + n * 250, housingAllowance: 1500, transportAllowance: 500,
          foodAllowance: 0, mobileAllowance: 0, otherAllowance: 0, fixedDeduction: 0,
          effectiveDate: '2024-01-01', currency,
        },
      });
      expectOk(res, `assign salary to '${employee.employeeCode}'`, [200, 201]);
      count++;
    }
  }
  return count;
}

// ── Payroll ───────────────────────────────────────────────────────────────────────────────────

/**
 * One payroll run per company, and — where the maker/checker personas exist — one of them carried
 * all the way to `Locked`.
 *
 * TWO specs need this and neither could get it from the old seeders either:
 *   • group-company/security.spec.ts proves a scoped user cannot reach a SIBLING company's payroll
 *     run. It used to `test.skip(!runId, 'seed has no runs for that company; skipping gracefully')`,
 *     so the cross-company payroll boundary was never once exercised. A run per company fixes that.
 *   • gosi-filing.spec.ts reconciles deducted vs recomputed vs ledger over the latest period, which
 *     requires a run that reached Lock (that is when the GL is posted).
 *
 * Maker/checker is respected, not bypassed: PayrollController.Approve refuses the user who processed
 * the run, and Lock needs `payroll.lock`, which the HR Manager deliberately does not hold. So three
 * real identities drive it, exactly as the product requires of a human.
 */
async function ensurePayrollRuns(
  fixture: FixtureTenant, adminToken: string,
  companies: Array<{ code: string; id: string; countryCode: string }>,
): Promise<string> {
  const now = new Date();
  const period = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1));
  const year = period.getUTCFullYear();
  const month = period.getUTCMonth() + 1;

  const existing = items(expectOk(
    await call('GET', '/api/payroll/runs?page=1&pageSize=50', { token: adminToken }), 'list payroll runs',
  ).body);
  const runByCompany = new Map<string, any>();
  for (const run of existing) {
    if (run.year === year && run.month === month) runByCompany.set(String(run.companyId ?? run.CompanyId), run);
  }

  const created: string[] = [];
  for (const company of companies) {
    if (runByCompany.has(company.id)) continue;
    const res = await call('POST', '/api/payroll/runs', {
      token: adminToken, companyId: company.id,
      body: { year, month, companyId: company.id, runType: 'Regular' },
    });
    // A duplicate period for the company is a 409 — idempotent, not a failure.
    if (res.status === 409) continue;
    expectOk(res, `create ${year}-${month} payroll run for '${company.code}'`, [200, 201]);
    created.push(company.code);
  }

  // Re-read rather than trusting the create response's shape. Taking the id from the POST body was
  // how the whole approval chain silently became `/api/payroll/runs/undefined/approve` — a clean
  // 404 from the route constraint, which looks exactly like "the run does not exist".
  runByCompany.clear();
  for (const run of items(expectOk(
    await call('GET', '/api/payroll/runs?page=1&pageSize=50', { token: adminToken }), 'list payroll runs',
  ).body)) {
    if (run.year === year && run.month === month) runByCompany.set(String(run.companyId ?? run.CompanyId), run);
  }

  // Carry the FIRST company's run through the whole approval chain, when the fixture declares the
  // three personas it takes. A half-approved run is worse than none, so every step is asserted.
  const lead = runByCompany.get(companies[0].id);
  const leadId = lead?.id ?? lead?.Id;
  const leadStatus = String(lead?.status ?? lead?.Status ?? 'Draft');
  if (!leadId || leadStatus === 'Locked' || leadStatus === 'Paid') {
    return `${created.length} run(s) created; lead run already ${leadStatus}`;
  }

  const maker = fixture.users.find((u) => u.role === 'HR Manager' && !u.companyCode);
  const checker = fixture.users.find((u) => u.role === 'Finance Approver');
  if (!maker || !checker) return `${created.length} run(s) created; no maker/checker personas to approve them`;

  expectOk(await call('POST', `/api/payroll/runs/${leadId}/process`, { token: adminToken }),
    `process the ${year}-${month} run`, [200, 201]);

  const makerToken = await requireTenantLogin(maker, fixture.slug);
  const checkerToken = await requireTenantLogin(checker, fixture.slug);
  const decision = { comments: 'e2e fixture world bootstrap', expectedExcludedCount: 0 };

  const makerApproval = await call('POST', `/api/payroll/runs/${leadId}/approve`, { token: makerToken, body: decision });
  const checkerApproval = makerApproval.status === 200
    ? await call('POST', `/api/payroll/runs/${leadId}/approve`, { token: checkerToken, body: decision })
    : makerApproval;
  const locked = checkerApproval.status === 200
    ? await call('POST', `/api/payroll/runs/${leadId}/lock`, { token: checkerToken })
    : checkerApproval;

  if (locked.status !== 200) {
    // Not fatal for the lanes that only need a run to EXIST, but never silent: a spec that reads
    // locked-period GOSI totals must be able to see, in this log, that no run reached Lock.
    console.log(
      `[bootstrap] ${fixture.slug}: the ${year}-${month} run did NOT reach Locked `
      + `(HTTP ${locked.status}: ${(locked.body?.message ?? locked.text).slice(0, 200)}). `
      + 'Specs that reconcile a locked period will fail against this world.',
    );
    return `${created.length} run(s) created; lead run stopped before Lock`;
  }
  return `${created.length} run(s) created; ${year}-${String(month).padStart(2, '0')} locked`;
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
    const salaries = await ensureSalaries(adminToken, companies);
    const hrData = await ensureLeaveAndAttendance(adminToken, fixture.slug, companies);
    const profiles = await ensureComplianceProfiles(adminToken, companies);
    const payroll = fixture.payroll ? await ensurePayrollRuns(fixture, adminToken, companies) : 'no payroll';
    manifest.tenants.push({ slug: fixture.slug, tenantId, companies });
    console.log(
      `[bootstrap] ${fixture.slug}: ${companies.length} compan${companies.length === 1 ? 'y' : 'ies'}, `
      + `${fixture.users.length + 1} users, ${active} active employees, `
      + `${salaries} salary assignment(s), ${profiles} compliance profile(s), ${hrData}, ${payroll}.`,
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
