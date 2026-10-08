/**
 * RELEASE A DEMO SEED — Masar Holding (مجموعة مسار), phase 1 (slice R7, skeleton).
 *
 * Builds the demo company for the 10-minute storyline (plan of record §5) through the PUBLIC API
 * only, the way e2e/bootstrap/provision.ts builds the fixture world: as the platform owner for the
 * tenant, its companies and its users, then as the tenant's own personas for everything else. No SQL.
 * Every row passes the product's real validation, approval and maker-checker rules.
 *
 * Phase 1 seeds the HR, payroll, loan, leave, overtime and attendance base. The Release A parts
 * (entitlement matrix, packages, renewal cases) are TODO hooks at the bottom, marked TODO(R1)…TODO(R6):
 * their APIs are on main behind the release_a flag, which phase 1 leaves off (see the hooks for why).
 *
 * ── Properties ────────────────────────────────────────────────────────────────────────────────
 * IDEMPOTENT  every step reads before it writes; a second run changes nothing and says so.
 * FAIL-LOUD   a step that cannot complete throws with the HTTP status and body.
 * NO SECRETS  passwords are generated per environment (or taken from DEMO_MASAR_PASSWORD) and written
 *             only to the git-ignored e2e/.auth/demo-masar.credentials.json (mode 0600).
 * GUARDED     loopback/private hosts only, unless DEMO_SEED_CONFIRM_HOST names the target host
 *             exactly; the live pilot hosts are refused unconditionally.
 *
 * ── Environment ───────────────────────────────────────────────────────────────────────────────
 * DEMO_API_BASE_URL         API base (falls back to E2E_API_BASE_URL, then http://localhost:5117)
 * PLATFORM_ADMIN_EMAIL      platform owner, to create the tenant, companies and users (required)
 * PLATFORM_ADMIN_PASSWORD   (required)
 * DEMO_MASAR_PASSWORD       optional: one password for every persona instead of generated ones
 * DEMO_SEED_CONFIRM_HOST    required for any non-private host: the exact hostname being seeded
 */
import { randomBytes } from 'node:crypto';
import { chmod, mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { collectAllPages, pageItems, pageTotal } from '../paging';
import { isPrivateHost } from '../preflight/rules';
import {
  ASIF_ABSENCE_DATE, ASIF_LOAN, ASIF_TRAFFIC_FINE, COMPANIES, EMPLOYEES, ENTITLEMENT_MATRIX, GO_LIVE_CUTOVER,
  GRADES, GRADE_LOAN_LIMITS, LOAN_TYPES, MOHAMMED_DEPENDANTS, OVERTIME_POLICY, PAYROLL_MONTHS, PERSONAS, TENANT,
  allowancesFor, loanCatalogueProblems, type CompanyKey, type DemoEmployee, type DemoPersona, type GradeCode,
} from './demo-seed.data';

const API_BASE = (process.env.DEMO_API_BASE_URL ?? process.env.E2E_API_BASE_URL ?? 'http://localhost:5117')
  .replace(/\/$/, '');
export const CREDENTIALS_FILE = 'e2e/.auth/demo-masar.credentials.json';
export const MANIFEST_FILE = 'e2e/.auth/demo-masar.json';

/** The live pilot. Never seeded, whatever the confirmation variable says. */
const NEVER_SEED = ['zayra-ai-workforce.onrender.com', 'kynexone.vercel.app', 'kynexone.com'];

const log = (msg: string) => console.log(`[demo-masar] ${msg}`);

// ── HTTP ──────────────────────────────────────────────────────────────────────────────────────

interface Res<T = any> { status: number; body: T; text: string }

async function call<T = any>(
  method: string, path: string, opts: { token?: string; body?: unknown; companyId?: string } = {},
): Promise<Res<T>> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  if (opts.companyId) headers['X-Company-Id'] = opts.companyId;
  const response = await fetch(`${API_BASE}${path}`, {
    method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
  });
  const text = await response.text();
  let body: any = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* keep text */ }
  return { status: response.status, body, text };
}

function ok<T>(res: Res<T>, what: string, accept: number[] = [200, 201, 204]): Res<T> {
  if (!accept.includes(res.status)) {
    throw new Error(`[demo-masar] ${what} failed: HTTP ${res.status}\n${res.text.slice(0, 900)}`);
  }
  return res;
}

const items = pageItems;
const idOf = (x: any): string => x?.id ?? x?.Id;

async function allPages(token: string, path: string, companyId?: string): Promise<any[]> {
  const sep = path.includes('?') ? '&' : '?';
  return collectAllPages(async (page, pageSize) => {
    const res = ok(await call('GET', `${path}${sep}page=${page}&pageSize=${pageSize}`, { token, companyId }), `list ${path}`);
    return { items: pageItems(res.body), total: pageTotal(res.body) };
  });
}

// ── Safety ────────────────────────────────────────────────────────────────────────────────────

function assertSeedableHost(): void {
  let host: string;
  try { host = new URL(API_BASE).hostname.toLowerCase(); } catch {
    throw new Error(`[demo-masar] DEMO_API_BASE_URL '${API_BASE}' is not a URL.`);
  }
  if (NEVER_SEED.some((h) => host === h || host.endsWith(`.${h}`))) {
    throw new Error(`[demo-masar] REFUSING to seed '${host}': that is the live pilot. Never seed demo data there.`);
  }
  if (isPrivateHost(host)) return;
  if ((process.env.DEMO_SEED_CONFIRM_HOST ?? '').trim().toLowerCase() === host) return;
  throw new Error(
    `[demo-masar] REFUSING to seed '${host}' without confirmation. The seed creates a tenant, users and\n`
    + `payroll. If '${host}' really is a demo environment, re-run with DEMO_SEED_CONFIRM_HOST=${host}.`,
  );
}

// ── Credentials (never committed) ───────────────────────────────────────────────────────────

type Credentials = Record<string, string>; // email -> password

function generatePassword(): string {
  // 18 random base64url characters plus one of each class the password policy asks for.
  return `${randomBytes(14).toString('base64url')}-Mq7`;
}

async function loadCredentials(): Promise<Credentials> {
  const shared = process.env.DEMO_MASAR_PASSWORD?.trim();
  let stored: Credentials = {};
  try {
    stored = JSON.parse(await readFile(CREDENTIALS_FILE, 'utf8')).passwords ?? {};
  } catch { /* first run on this machine */ }
  const creds: Credentials = {};
  for (const p of PERSONAS) creds[p.email] = shared || stored[p.email] || generatePassword();
  await mkdir(dirname(CREDENTIALS_FILE), { recursive: true });
  await writeFile(CREDENTIALS_FILE, JSON.stringify({
    note: 'Generated by e2e/release-a/demo-seed.ts. Git-ignored. Never commit or paste these.',
    apiBaseUrl: API_BASE, tenantSlug: TENANT.slug,
    personas: PERSONAS.map((p) => ({ email: p.email, role: p.role, storylineRole: p.storylineRole })),
    passwords: creds,
  }, null, 2), { mode: 0o600 });
  await chmod(CREDENTIALS_FILE, 0o600);
  return creds;
}

// ── Authentication ────────────────────────────────────────────────────────────────────────────

/**
 * A 200 with no token is a second-factor or set-up challenge, not a session. Privileged MFA (#182) is enforced
 * from a date (platform-wide: migration time + 14 days); from then on the platform owner and the privileged
 * personas must sign in with TOTP, which this seed does not do. It says so instead of guessing.
 */
const MFA_HINT = 'The response is a second-factor challenge, not a session: privileged MFA is enforced on this '
  + 'environment (docs/MFA_ENFORCEMENT.md). This seed signs in with a password only; seed a stack whose '
  + 'enforcement date has not passed.';

async function platformLogin(): Promise<string> {
  const email = process.env.PLATFORM_ADMIN_EMAIL;
  const password = process.env.PLATFORM_ADMIN_PASSWORD;
  if (!email || !password) {
    throw new Error('[demo-masar] PLATFORM_ADMIN_EMAIL and PLATFORM_ADMIN_PASSWORD must be set (the platform owner of the target).');
  }
  const res = ok(await call('POST', '/api/platform/auth/login', { body: { email, password } }), 'platform-owner login');
  const token = res.body?.token ?? res.body?.accessToken;
  if (!token) throw new Error(`[demo-masar] platform-owner login returned no token. ${MFA_HINT}`);
  return token;
}

async function tenantLoginRes(email: string, password: string): Promise<Res> {
  return call('POST', '/api/auth/login', { body: { email, password, tenantSlug: TENANT.slug } });
}

async function tenantLogin(email: string, password: string): Promise<string | null> {
  const res = await tenantLoginRes(email, password);
  return res.status === 200 ? (res.body?.accessToken ?? res.body?.token ?? null) : null;
}

async function requireLogin(persona: DemoPersona, creds: Credentials): Promise<string> {
  const res = await tenantLoginRes(persona.email, creds[persona.email]);
  const token = res.status === 200 ? (res.body?.accessToken ?? res.body?.token) : null;
  if (token) return token;
  throw new Error(
    `[demo-masar] ${persona.email} cannot log in (HTTP ${res.status}). `
    + (res.status === 200 ? MFA_HINT
      : res.status === 429 ? 'That is the login rate limiter.'
        : `The account exists with a different password than ${CREDENTIALS_FILE} holds. Restore that file `
          + 'from the machine that first seeded this environment, or set DEMO_MASAR_PASSWORD to the password used then.'),
  );
}

const persona = (key: string): DemoPersona => {
  const p = PERSONAS.find((x) => x.key === key);
  if (!p) throw new Error(`no persona '${key}'`);
  return p;
};

// ── Deterministic, format-valid identifiers ───────────────────────────────────────────────────

function hashOf(text: string): number {
  let h = 2166136261;
  for (const ch of text) { h ^= ch.charCodeAt(0); h = Math.imul(h, 16777619); }
  return h >>> 0;
}

function digits(seed: string, count: number): string {
  let out = '';
  let h = hashOf(seed);
  while (out.length < count) { h = Math.imul(h ^ (h >>> 15), 2246822519) >>> 0; out += String(h % 10); }
  return out;
}

/** Luhn check digit over the first nine digits — the scheme Saudi national IDs and Iqamas use. */
function luhnComplete(nine: string): string {
  let sum = 0;
  for (let i = 0; i < 9; i++) {
    let d = Number(nine[i]);
    if (i % 2 === 0) { d *= 2; if (d > 9) d -= 9; }
    sum += d;
  }
  return `${nine}${(10 - (sum % 10)) % 10}`;
}

/** 10 digits: '1…' for a Saudi national ID, '2…' for an Iqama. */
const saudiId = (e: DemoEmployee): string =>
  luhnComplete(`${e.nationality === 'Saudi' ? '1' : '2'}${digits(`id:${e.code}`, 8)}`);

const BANKS = [
  { name: 'Al Rajhi Bank', code: '80' }, { name: 'Saudi National Bank', code: '10' },
  { name: 'Riyad Bank', code: '20' }, { name: 'Alinma Bank', code: '05' }, { name: 'SAB', code: '45' },
];

/** ISO 13616 Saudi IBAN: 'SA' + 2 check digits + 2-digit bank code + 18-digit account. */
function saudiIban(bankCode: string, account18: string): string {
  const bban = `${bankCode}${account18}`;
  let remainder = 0;
  for (const ch of `${bban}281000`) remainder = (remainder * 10 + Number(ch)) % 97; // S=28, A=10
  return `SA${String(98 - remainder).padStart(2, '0')}${bban}`;
}

function bankFor(e: DemoEmployee) {
  const bank = BANKS[hashOf(`bank:${e.code}`) % BANKS.length];
  const account = digits(`acct:${e.code}`, 18);
  return { bankName: bank.name, iban: saudiIban(bank.code, account), accountNumber: account.slice(-12) };
}

function passportFor(e: DemoEmployee): string {
  const d = digits(`pp:${e.code}`, 8);
  switch (e.nationality) {
    case 'Egyptian': return `A${d.slice(0, 8)}`;
    case 'Pakistani': return `AB${d.slice(0, 7)}`;
    case 'Filipino': return `P${d.slice(0, 7)}A`;
    case 'Indian': return `N${d.slice(0, 7)}`;
    default: return `S${d.slice(0, 8)}`;
  }
}

function dateOfBirth(e: DemoEmployee): string {
  const base: Record<GradeCode, number> = { G1: 1992, G2: 1989, G3: 1986, G4: 1984, G5: 1977 };
  const year = base[e.grade] - (hashOf(`dob:${e.code}`) % 6);
  const month = 1 + (hashOf(`m:${e.code}`) % 12);
  const day = 1 + (hashOf(`d:${e.code}`) % 27);
  return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

const workEmail = (e: DemoEmployee): string =>
  PERSONAS.find((p) => p.employeeCode === e.code)?.email
  ?? `${e.en.toLowerCase().replace(/[^a-z ]/g, '').trim().replace(/\s+/g, '.')}@${TENANT.emailDomain}`;

const joiningDate = (e: DemoEmployee): string => e.terms[0].start;
const currentTerm = (e: DemoEmployee) => e.terms[e.terms.length - 1];

// ── Tenant, companies, branches, users ────────────────────────────────────────────────────────

async function ensureTenant(platformToken: string, creds: Credentials): Promise<string> {
  const list = ok(await call('GET', '/api/platform/tenants', { token: platformToken }), 'list tenants');
  const existing = items(list.body).find((t: any) => t.slug === TENANT.slug);
  if (existing) return existing.id;
  const admin = persona('hrDirector');
  const created = ok(await call('POST', '/api/platform/tenants', {
    token: platformToken,
    body: {
      name: TENANT.name, slug: TENANT.slug, adminEmail: admin.email, adminFullName: admin.fullName,
      adminPassword: creds[admin.email], accountType: 'Group', plan: 'Enterprise',
      maxUsers: 100, maxEmployees: 500, maxCompanies: 0,
      billingEmail: `billing@${TENANT.emailDomain}`, billingCycle: 'Monthly', monthlyAmount: 0,
      currencyCode: 'SAR', homeCountryCode: 'SA',
    },
  }), `create tenant '${TENANT.slug}'`, [201]);
  log(`created tenant ${TENANT.name} (${TENANT.slug})`);
  return created.body.tenantId;
}

function companyBody(c: (typeof COMPANIES)[number]) {
  return {
    legalNameEn: c.legalNameEn, legalNameAr: c.legalNameAr, tradeName: c.tradeName,
    countryCode: 'SA', jurisdiction: 'SA', registrationNumber: c.registrationNumber, taxNumber: c.taxNumber,
    gosiEmployerId: c.gosiEmployerId, qiwaEstablishmentId: c.qiwaEstablishmentId, wpsEmployerId: c.wpsEmployerId,
    defaultCurrency: 'SAR',
    emailDomain: TENANT.emailDomain, workEmailPattern: 'first.last', isActive: true,
  };
}

async function ensureCompanies(platformToken: string, tenantId: string, adminToken: string): Promise<Record<CompanyKey, string>> {
  const existing = items(ok(await call('GET', '/api/companies?page=1&pageSize=100', { token: adminToken }), 'list companies').body);
  const ids = {} as Record<CompanyKey, string>;
  for (const c of COMPANIES) {
    const found = existing.find((x: any) => (x.legalNameEn ?? x.LegalNameEn) === c.legalNameEn);
    if (!found) continue;
    ids[c.key] = idOf(found);
    // Bring an existing company up to the declared statutory identifiers (an earlier seed may predate one).
    if ((found.wpsEmployerId ?? '') !== c.wpsEmployerId || (found.gosiEmployerId ?? '') !== c.gosiEmployerId
      || (found.qiwaEstablishmentId ?? '') !== c.qiwaEstablishmentId || (found.legalNameAr ?? '') !== c.legalNameAr) {
      ok(await call('PUT', `/api/companies/${ids[c.key]}`, { token: adminToken, body: companyBody(c) }), `update company ${c.key}`);
      log(`updated company ${c.legalNameEn}`);
    }
  }
  // The tenant is born with one company named after the tenant; it BECOMES the first declared one.
  const starter = existing.find((x: any) => !COMPANIES.some((c) => c.legalNameEn === (x.legalNameEn ?? x.LegalNameEn)));
  if (starter && !ids.MFS) {
    ok(await call('PUT', `/api/companies/${idOf(starter)}`, { token: adminToken, body: companyBody(COMPANIES[0]) }), 'rename starter company');
    ids.MFS = idOf(starter);
  }
  for (const c of COMPANIES) {
    if (ids[c.key]) continue;
    const created = ok(await call('POST', `/api/platform/tenants/${tenantId}/companies`, { token: platformToken, body: companyBody(c) }),
      `create company ${c.legalNameEn}`);
    ids[c.key] = created.body.companyId;
    // The platform create takes names and registration only; the GOSI, Qiwa and WPS establishment
    // identifiers are the tenant's own company settings.
    ok(await call('PUT', `/api/companies/${ids[c.key]}`, { token: adminToken, body: companyBody(c) }), `complete company ${c.key}`);
  }
  return ids;
}

async function ensureBranches(adminToken: string, companyIds: Record<CompanyKey, string>): Promise<Record<CompanyKey, string>> {
  const existing = items(ok(await call('GET', '/api/branches?page=1&pageSize=100', { token: adminToken }), 'list branches').body);
  const ids = {} as Record<CompanyKey, string>;
  for (const c of COMPANIES) {
    const code = `${c.key}-HQ`;
    const found = existing.find((b: any) => (b.code ?? b.Code) === code);
    if (found) { ids[c.key] = idOf(found); continue; }
    const created = ok(await call('POST', '/api/branches', {
      token: adminToken, companyId: companyIds[c.key],
      body: {
        companyId: companyIds[c.key], code, nameEn: `${c.tradeName} — ${c.city}`, nameAr: `${c.legalNameAr} — ${c.cityAr}`,
        countryCode: 'SA', city: c.city, timeZoneId: 'Asia/Riyadh', isHeadOffice: true, isActive: true,
      },
    }), `create branch ${code}`);
    ids[c.key] = idOf(created.body);
  }
  return ids;
}

/** Staff personas (not the employee logins, which the invitation flow creates). Group scope. */
async function ensureStaffUsers(platformToken: string, tenantId: string, adminToken: string, creds: Credentials): Promise<void> {
  const existing = items(ok(await call('GET', `/api/platform/tenants/${tenantId}/users`, { token: platformToken }), 'list users').body);
  const byEmail = new Map<string, string>(existing.map((u: any) => [String(u.email ?? u.Email).toLowerCase(), u.id ?? u.Id]));
  const grants = items(ok(await call('GET', '/api/access/entity-grants', { token: adminToken }), 'list entity grants').body);
  const held = new Set(grants.map((g: any) => `${g.userId ?? g.UserId}|${g.companyId ?? g.CompanyId ?? 'all'}`));

  for (const p of PERSONAS) {
    if (p.employeeCode || p.key === 'hrDirector') continue;
    let userId = byEmail.get(p.email.toLowerCase());
    if (!userId) {
      const created = ok(await call('POST', `/api/platform/tenants/${tenantId}/users`, {
        token: platformToken,
        body: { email: p.email, password: creds[p.email], fullName: p.fullName, roleName: p.role, entityScope: 'group' },
      }), `create user ${p.email}`);
      userId = created.body.Id ?? created.body.id;
      log(`created ${p.role} ${p.fullName}`);
    }
    // Backstop: a group identity without a grant resolves to zero companies (see provision.ts).
    if (!held.has(`${userId}|all`)) {
      ok(await call('POST', '/api/access/entity-grants', {
        token: adminToken, body: { userId, role: p.role, grantMode: 'AllCurrentAndFutureCompanies' },
      }), `grant ${p.email} group scope`, [200, 201, 409]);
    }
  }
}

// ── Organisation: grades, departments, designations ──────────────────────────────────────────

async function ensureGrades(adminToken: string): Promise<Record<GradeCode, string>> {
  const existing = items(ok(await call('GET', '/api/grades?page=1&pageSize=100', { token: adminToken }), 'list grades').body);
  const ids = {} as Record<GradeCode, string>;
  for (const g of GRADES) {
    const found = existing.find((x: any) => (x.code ?? x.Code) === g.code);
    if (found) { ids[g.code] = idOf(found); continue; }
    const created = ok(await call('POST', '/api/grades', {
      token: adminToken,
      body: {
        code: g.code, name: g.name, nameAr: g.nameAr, band: g.name, level: g.level,
        minSalary: g.min, midSalary: g.mid, maxSalary: g.max, currency: 'SAR', isActive: true,
      },
    }), `create grade ${g.code}`);
    ids[g.code] = idOf(created.body);
  }
  return ids;
}

const slug = (s: string) => s.toUpperCase().replace(/[^A-Z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 24);

async function ensureOrg(
  adminToken: string, companyIds: Record<CompanyKey, string>, branchIds: Record<CompanyKey, string>,
): Promise<{ dept: Map<string, string>; desig: Map<string, string> }> {
  const dept = new Map<string, string>();
  const desig = new Map<string, string>();
  for (const c of COMPANIES) {
    const companyId = companyIds[c.key];
    const departments = await allPages(adminToken, '/api/departments', companyId);
    for (const d of c.departments) {
      const code = `${c.key}-${d.code}`;
      let id = idOf(departments.find((x: any) => (x.code ?? x.Code) === code));
      if (!id) {
        id = idOf(ok(await call('POST', '/api/departments', {
          token: adminToken, companyId,
          body: { branchId: branchIds[c.key], code, nameEn: d.en, nameAr: d.ar, isActive: true },
        }), `create department ${code}`).body);
      }
      dept.set(code, id);
    }
    const designations = await allPages(adminToken, '/api/designations', companyId);
    for (const e of EMPLOYEES.filter((x) => x.company === c.key)) {
      const code = `${c.key}-${e.dept}-${slug(e.title)}`;
      if (desig.has(code)) continue;
      let id = idOf(designations.find((x: any) => (x.code ?? x.Code) === code));
      if (!id) {
        id = idOf(ok(await call('POST', '/api/designations', {
          token: adminToken, companyId,
          body: {
            departmentId: dept.get(`${c.key}-${e.dept}`), code, titleEn: e.title, titleAr: e.titleAr,
            isManagerRole: /Manager|Supervisor|Leader|Lead/.test(e.title), isActive: true,
          },
        }), `create designation ${code}`).body);
      }
      desig.set(code, id);
    }
  }
  return { dept, desig };
}

// ── Employees ─────────────────────────────────────────────────────────────────────────────────

const record = (fieldKey: string, fieldLabel: string, fieldValue: string, expiryDate?: string) => ({
  countryCode: 'SA', fieldKey, fieldLabel, fieldValue, expiryDate, isSensitive: true, isRequired: true,
});

function complianceRecords(e: DemoEmployee) {
  const id = saudiId(e);
  const gosi = digits(`gosi:${e.code}`, 9);
  const expiry = `2027-${String(1 + (hashOf(`exp:${e.code}`) % 12)).padStart(2, '0')}-15`;
  const recs = [
    record('gosi_reference', 'GOSI number', gosi),
    record('qiwa_contract_reference', 'Qiwa contract reference', `QC-${digits(`qc:${e.code}`, 8)}`),
  ];
  if (e.nationality === 'Saudi') {
    recs.push(record('id_number', 'National ID', id));
  } else {
    recs.push(record('iqama_number', 'Iqama number', id, expiry));
    recs.push(record('passport_number', 'Passport number', passportFor(e), '2030-06-30'));
    recs.push(record('muqeem_reference', 'Muqeem reference', `MQ-${digits(`mq:${e.code}`, 8)}`));
  }
  return recs;
}

async function ensureEmployees(
  adminToken: string, companyIds: Record<CompanyKey, string>, branchIds: Record<CompanyKey, string>,
  gradeIds: Record<GradeCode, string>, org: { dept: Map<string, string>; desig: Map<string, string> },
): Promise<Map<string, any>> {
  const byCode = new Map<string, any>();
  for (const row of await allPages(adminToken, '/api/employees')) byCode.set(String(row.employeeCode ?? row.EmployeeCode), row);

  let created = 0;
  for (const e of EMPLOYEES) {
    if (byCode.has(e.code)) continue;
    const companyId = companyIds[e.company];
    const manager = e.manager ? byCode.get(e.manager) : null;
    if (e.manager && !manager) throw new Error(`[demo-masar] ${e.code}'s manager ${e.manager} must be created first.`);
    const { housing, transport } = allowancesFor(e.grade, e.basic);
    const bank = bankFor(e);
    const res = ok(await call('POST', '/api/employees', {
      token: adminToken, companyId,
      body: {
        employeeCode: e.code, manualEmployeeCode: true,
        englishName: e.en, arabicName: e.ar, gender: e.gender, dateOfBirth: dateOfBirth(e),
        nationality: e.nationality, maritalStatus: e.maritalStatus,
        workEmail: workEmail(e), personalEmail: `${e.code.toLowerCase()}.personal@${TENANT.emailDomain}`,
        mobileNumber: `+9665${digits(`mob:${e.code}`, 8)}`,
        companyId, branchId: branchIds[e.company], gradeId: gradeIds[e.grade],
        departmentId: org.dept.get(`${e.company}-${e.dept}`),
        designationId: org.desig.get(`${e.company}-${e.dept}-${slug(e.title)}`),
        reportingManagerEmployeeId: manager ? Number(manager.id ?? manager.Id) : null,
        jobTitle: e.title, employmentType: 'FullTime',
        contractType: currentTerm(e).end ? 'Fixed-Term' : 'Unlimited',
        joiningDate: `${joiningDate(e)}T00:00:00Z`,
        payrollProfile: {
          bankName: bank.bankName, iban: bank.iban, accountNumber: bank.accountNumber,
          paymentMethod: 'BankTransfer', salaryCurrency: 'SAR', wpsEligible: true, eosbEligible: true,
          socialInsuranceReference: digits(`gosi:${e.code}`, 9), molId: `MOL-${digits(`mol:${e.code}`, 7)}`,
        },
        salaryBreakdown: {
          basicSalary: e.basic, housingAllowance: housing, transportAllowance: transport,
          effectiveDate: joiningDate(e), currency: 'SAR',
        },
        complianceRecords: complianceRecords(e),
        acknowledgeDuplicate: true,
      },
    }), `create employee ${e.code} ${e.en}`, [200, 201]);
    byCode.set(e.code, { ...res.body, employeeCode: e.code });
    created++;
  }
  if (created) log(`created ${created} employee(s)`);

  // Re-read so every row carries the list shape (id, publicId, status).
  byCode.clear();
  for (const row of await allPages(adminToken, '/api/employees')) byCode.set(String(row.employeeCode ?? row.EmployeeCode), row);
  return byCode;
}

async function activateEmployees(adminToken: string, byCode: Map<string, any>): Promise<void> {
  const blocked: string[] = [];
  let activated = 0;
  for (const e of EMPLOYEES) {
    const row = byCode.get(e.code);
    if (String(row.status ?? row.Status) === 'Active') continue;
    const res = await call('POST', `/api/employees/${row.id ?? row.Id}/activate`, {
      token: adminToken, body: { status: 'Active', reason: 'Masar Holding onboarding' },
    });
    if (res.status === 200) { activated++; row.status = 'Active'; continue; }
    const keys = (res.body?.blocking ?? []).map((b: any) => b.key).join(', ');
    blocked.push(`${e.code}: HTTP ${res.status} ${(res.body?.message ?? res.text).slice(0, 160)}${keys ? ` [${keys}]` : ''}`);
  }
  if (blocked.length) throw new Error(`[demo-masar] the activation guard refused:\n  ${blocked.join('\n  ')}`);
  if (activated) log(`activated ${activated} employee(s)`);
}

// ── Salaries ─────────────────────────────────────────────────────────────────────────────────

/** A salary assignment per employee, effective from 1 Jan 2026 or the joining date if later. */
async function ensureSalaries(adminToken: string, companyIds: Record<CompanyKey, string>, byCode: Map<string, any>): Promise<void> {
  const structures = items(ok(await call('GET', '/api/payroll/salary-structures', { token: adminToken }), 'list salary structures').body);
  const assigned = new Set((await allPages(adminToken, '/api/payroll/employee-salary-structures'))
    .map((a: any) => String(a.employeeId ?? a.EmployeeId)));
  let count = 0;
  for (const c of COMPANIES) {
    const code = `MASAR-${c.key}-STD`;
    let structureId = idOf(structures.find((s: any) => (s.code ?? s.Code) === code));
    if (!structureId) {
      structureId = idOf(ok(await call('POST', '/api/payroll/salary-structures', {
        token: adminToken, companyId: companyIds[c.key],
        body: {
          code, name: `${c.tradeName} standard structure`, currency: 'SAR', effectiveDate: '2018-01-01',
          companyId: companyIds[c.key], components: [], isActive: true,
        },
      }), `create salary structure ${code}`).body);
    }
    for (const e of EMPLOYEES.filter((x) => x.company === c.key)) {
      const row = byCode.get(e.code);
      const id = row.id ?? row.Id;
      if (assigned.has(String(id))) continue;
      const { housing, transport } = allowancesFor(e.grade, e.basic);
      ok(await call('POST', '/api/payroll/employee-salary-structures', {
        token: adminToken, companyId: companyIds[c.key],
        body: {
          employeeId: id, salaryStructureId: structureId, basicSalary: e.basic,
          housingAllowance: housing, transportAllowance: transport, foodAllowance: 0, mobileAllowance: 0,
          otherAllowance: 0, fixedDeduction: 0,
          effectiveDate: joiningDate(e) > '2026-01-01' ? joiningDate(e) : '2026-01-01', currency: 'SAR',
        },
      }), `assign salary to ${e.code}`);
      count++;
    }
  }
  if (count) log(`assigned ${count} salar${count === 1 ? 'y' : 'ies'}`);
}

// ── Employment contracts (the live employee_contracts register) ──────────────────────────────

/**
 * Every term of every employee, oldest first, as a superseded chain: term 1 is created and
 * activated, each later term SUPERSEDES the one before (version+1, previousVersionId), and only the
 * last stays Active. Faisal therefore carries 3 terms (2 renewals), Mohammed 2.
 */
async function ensureContracts(adminToken: string, byCode: Map<string, any>): Promise<void> {
  const signer = persona('hrDirector').fullName;
  let made = 0;
  for (const e of EMPLOYEES) {
    const publicId = byCode.get(e.code).publicId ?? byCode.get(e.code).PublicId;
    const existing = items(ok(await call('GET', `/api/compliance/contracts?employeeId=${publicId}&pageSize=100`, { token: adminToken }),
      `list contracts for ${e.code}`).body);
    let previousId: string | null = null;
    for (const term of e.terms) {
      let contract = existing.find((c: any) => String(c.startDate).slice(0, 10) === term.start);
      if (!contract) {
        const body = {
          employeeId: publicId, employeeName: e.en, contractType: term.end ? 'Fixed-Term' : 'Unlimited',
          startDate: term.start, endDate: term.end, basicSalary: e.basic, currencyCode: 'SAR', language: 'en',
        };
        contract = ok(previousId
          ? await call('POST', `/api/compliance/contracts/${previousId}/supersede`, { token: adminToken, body })
          : await call('POST', '/api/compliance/contracts', { token: adminToken, body }),
        `${previousId ? 'supersede into' : 'create'} contract ${e.code} ${term.start}`, [200, 201]).body;
        made++;
      }
      for (const next of ['PendingApproval', 'Active']) {
        if (contract.status === 'Draft' && next === 'PendingApproval'
          || contract.status === 'PendingApproval' && next === 'Active') {
          contract = ok(await call('PATCH', `/api/compliance/contracts/${contract.id}/status`, {
            token: adminToken, body: { status: next, signedByHrName: signer },
          }), `contract ${e.code} ${term.start} -> ${next}`).body ?? { ...contract, status: next };
          contract.status = contract.status ?? next;
        }
      }
      previousId = contract.id;
    }
  }
  if (made) log(`created ${made} contract term(s)`);
}

// ── Employee logins (invitation flow, so Employee.UserAccountId links the person) ─────────────

async function ensureEmployeeLogins(
  adminToken: string, companyIds: Record<CompanyKey, string>, byCode: Map<string, any>, creds: Credentials,
): Promise<void> {
  for (const p of PERSONAS.filter((x) => x.employeeCode)) {
    if (await tenantLogin(p.email, creds[p.email])) continue;
    const e = EMPLOYEES.find((x) => x.code === p.employeeCode)!;
    const row = byCode.get(e.code);
    const invite = ok(await call('POST', '/api/access/employee-logins/invite', {
      token: adminToken, companyId: companyIds[e.company],
      body: { employeeId: Number(row.id ?? row.Id), email: p.email, accessMode: 'FullPortal', roles: [p.role] },
    }), `invite ${p.email}`, [200, 201]);
    ok(await call('POST', '/api/auth/accept-invitation', {
      body: { invitationToken: invite.body.invitationToken, newPassword: creds[p.email], tenantSlug: TENANT.slug },
    }), `accept invitation ${p.email}`, [200, 204]);
    await requireLogin(p, creds);
    log(`linked login ${p.email} to ${e.code} (${p.role})`);
  }
}

// ── Leave: the 2025 Saudi statutory minimums ──────────────────────────────────────────────────

/**
 * ANNUAL (21/30) and SICK (120) arrive with the tenant. These add the special leaves at exactly the
 * amended Labour Law floors (in force 19 Feb 2025), which the API itself enforces
 * (KsaStatutorySpecialLeave): maternity 12 weeks (84 calendar days), paternity 3, marriage 5,
 * bereavement 5, sibling bereavement 3, Hajj 10 (after 2 years' service), iddah 130 / 15.
 */
const SPECIAL_LEAVES = [
  { code: 'MATERNITY', en: 'Maternity Leave', ar: 'إجازة الوضع', days: 84, gender: 'Female', calendar: true, category: 'Maternity' },
  { code: 'PATERNITY', en: 'Paternity Leave (birth)', ar: 'إجازة المولود', days: 3, gender: null, calendar: false, category: 'Paternity' },
  { code: 'MARRIAGE', en: 'Marriage Leave', ar: 'إجازة الزواج', days: 5, gender: null, calendar: false, category: 'Marriage' },
  { code: 'BEREAVEMENT', en: 'Bereavement Leave', ar: 'إجازة الوفاة', days: 5, gender: null, calendar: false, category: 'Bereavement' },
  { code: 'BEREAVEMENT_SIB', en: 'Bereavement Leave (sibling)', ar: 'إجازة وفاة الأخ أو الأخت', days: 3, gender: null, calendar: false, category: 'Bereavement' },
  { code: 'HAJJ', en: 'Hajj Leave', ar: 'إجازة الحج', days: 10, gender: null, calendar: false, category: 'Hajj' },
  { code: 'IDDAH', en: 'Iddah Leave', ar: 'إجازة العدة', days: 130, gender: 'Female', calendar: true, category: 'Iddah' },
  { code: 'IDDAH_NM', en: 'Iddah Leave (non-Muslim)', ar: 'إجازة العدة (لغير المسلمة)', days: 15, gender: 'Female', calendar: true, category: 'Iddah' },
] as const;

async function ensureLeave(adminToken: string): Promise<void> {
  const types = items(ok(await call('GET', '/api/leave/types', { token: adminToken }), 'list leave types').body);
  const policies = await allPages(adminToken, '/api/leave/policies');
  let made = 0;
  for (const [i, l] of SPECIAL_LEAVES.entries()) {
    let typeId = idOf(types.find((t: any) => (t.code ?? t.Code) === l.code));
    if (!typeId) {
      typeId = idOf(ok(await call('POST', '/api/leave/types', {
        token: adminToken,
        body: {
          code: l.code, nameEn: l.en, nameAr: l.ar, category: l.category, isPaid: true,
          isHalfDayAllowed: false, isHourlyAllowed: false, requiresAttachment: true, requiresReason: true,
          maxConsecutiveDays: l.days, colorCode: null, sortOrder: 10 + i,
        },
      }), `create leave type ${l.code}`).body);
      made++;
    }
    if (policies.some((p: any) => String(p.leaveTypeId ?? p.LeaveTypeId) === typeId)) continue;
    ok(await call('POST', '/api/leave/policies', {
      token: adminToken,
      body: {
        name: `${l.en} — KSA statutory`, leaveTypeId: typeId, countryCode: 'SA', companyId: null, branchId: null,
        departmentName: null, grade: null, employmentType: null, contractType: null, gender: l.gender,
        appliesOnProbation: true, annualEntitlementDays: l.days, accrualMethod: 'Upfront',
        carryForwardMax: 0, carryForwardExpiry: 0, encashmentAllowed: false, encashmentMaxDays: 0,
        minimumDaysPerRequest: 1, maximumDaysPerRequest: l.days, noticeRequiredDays: 0,
        weekendsIncluded: l.calendar, publicHolidaysIncluded: l.calendar, payrollImpact: 'Paid',
        approvalWorkflowId: null, status: 'Active',
      },
    }), `create leave policy ${l.code}`);
    made++;
  }
  if (made) log(`created ${made} statutory leave type/policy row(s)`);
}

// ── Loans: types, per-company policies, grade loan limits ─────────────────────────────────────

async function ensureLoans(
  adminToken: string, companyIds: Record<CompanyKey, string>, gradeIds: Record<GradeCode, string>,
): Promise<Record<string, string>> {
  const types = items(ok(await call('GET', '/api/finance/loans/types', { token: adminToken }), 'list loan types').body);
  // An earlier draft of this seed created the housing advance as code HOUSING (facility LOAN_HOUSING, a generic
  // loan). Nothing here removes it; say so, because it is a second "Housing Advance" on that stack.
  const legacy = types.find((x: any) => (x.code ?? x.Code) === 'HOUSING' && /housing advance/i.test(String(x.nameEn ?? x.NameEn ?? '')));
  if (legacy) {
    log('WARNING: loan type HOUSING ("Housing Advance") from an earlier seed draft exists; it is a generic loan '
      + '(LOAN_HOUSING), not the housing advance. Re-seed a fresh stack, or retire it in Loans.');
  }
  const typeIds: Record<string, string> = {};
  for (const t of LOAN_TYPES) {
    let id = idOf(types.find((x: any) => (x.code ?? x.Code) === t.code));
    if (!id) {
      id = idOf(ok(await call('POST', '/api/finance/loans/types', {
        token: adminToken,
        body: {
          code: t.code, nameEn: t.nameEn, nameAr: t.nameAr, maxAmount: t.maxAmount, maxInstallments: t.maxInstallments,
          repaymentFrequency: 'Monthly', isInterestFree: true, interestRate: 0, minServiceMonths: 0, requiresApproval: true,
        },
      }), `create loan type ${t.code}`).body);
      log(`created loan type ${t.code}`);
    }
    typeIds[t.code] = id;
  }

  for (const c of COMPANIES) {
    const policies = items(ok(await call('GET', `/api/finance/loans/policies?companyId=${companyIds[c.key]}`, { token: adminToken }),
      `list loan policies ${c.key}`).body);
    for (const t of LOAN_TYPES) {
      if (policies.some((p: any) => String(p.loanTypeId ?? p.LoanTypeId) === typeIds[t.code] && (p.isActive ?? p.IsActive ?? true))) continue;
      ok(await call('POST', '/api/finance/loans/policies', {
        token: adminToken,
        body: {
          companyId: companyIds[c.key], loanTypeId: typeIds[t.code], policyName: `${c.tradeName} — ${t.nameEn}`,
          maxAmount: t.maxAmount, maxTotalOutstanding: 0, maxMultiplierOfSalary: 0, maxInstallmentPercentOfSalary: 0,
          minServiceMonths: t.code === 'PERSONAL' ? 6 : 3, maxInstallments: t.maxInstallments, maxConcurrentLoans: 1,
          cooldownMonthsAfterRepayment: 0, requireProbationCompleted: true, blockDuringNotice: true, blockOnOverdue: true,
          allowExceptions: true, allowEarlySettlement: true, allowRescheduling: false, isOffered: true,
          allowedEmploymentStatuses: ['Active'], allowedRepaymentMethods: ['PayrollDeduction'],
          allowedRepaymentFrequencies: ['Monthly'], additionalApprovalThreshold: 0,
        },
      }), `create loan policy ${c.key} ${t.code}`);
      log(`published loan policy ${c.key} ${t.code}`);
    }
  }

  // Group-wide grade grid, effective today (the API refuses a past date), then switch grade limits on.
  const today = new Date().toISOString().slice(0, 10);
  for (const t of LOAN_TYPES) {
    const grid = GRADE_LOAN_LIMITS[t.code];
    const res = ok(await call('PUT', '/api/finance/loans/grade-limits', {
      token: adminToken,
      body: {
        loanTypeId: typeIds[t.code], companyId: null, effectiveFrom: today,
        rows: GRADES.map((g) => ({
          gradeId: gradeIds[g.code], eligible: grid[g.code].eligible, valueType: grid[g.code].valueType,
          rate: grid[g.code].rate ?? null, amount: null, maxOutstandingAmount: null, note: grid[g.code].note,
        })),
      },
    }), `publish grade loan limits ${t.code}`);
    if (res.body?.changed) log(`grade loan limits ${t.code}: ${res.body.changed} cell(s) published`);
    ok(await call('PATCH', `/api/finance/loans/types/${typeIds[t.code]}/grade-limited`, {
      token: adminToken, body: { gradeLimited: true },
    }), `grade-limit loan type ${t.code}`);
  }
  await assertServerLoanFacilities(adminToken);
  return typeIds;
}

/**
 * Seed-time check that the API agreed: every grade-limited loan type carries the facility code the data declares
 * (the API stamps it on the first grade-limit publish), and every catalogued facility is in the live entitlement
 * catalogue. The catalogue endpoint is part of Release A, so it is read only when the tenant has release_a on;
 * otherwise the pre-flight check against the pinned catalogue (loanCatalogueProblems) is what holds.
 */
async function assertServerLoanFacilities(adminToken: string): Promise<void> {
  const live = items(ok(await call('GET', '/api/finance/loans/types', { token: adminToken }), 'list loan types').body);
  const problems: string[] = [];
  for (const t of LOAN_TYPES.filter((x) => GRADE_LOAN_LIMITS[x.code])) {
    const row = live.find((x: any) => (x.code ?? x.Code) === t.code);
    const code = row?.entitlementComponentCode ?? row?.EntitlementComponentCode;
    if (!row) problems.push(`loan type ${t.code} is missing`);
    else if (code !== t.facility) problems.push(`loan type ${t.code} has facility ${code ?? 'none'}, expected ${t.facility}`);
    else if (!(row.gradeLimited ?? row.GradeLimited)) problems.push(`loan type ${t.code} is not grade-limited`);
  }
  const catalogue = await call('GET', '/api/entitlements/components', { token: adminToken });
  if (catalogue.status === 200) {
    const codes = new Set(items(catalogue.body).map((c: any) => String(c.code ?? c.Code)));
    for (const t of LOAN_TYPES.filter((x) => x.catalogued && !codes.has(x.facility))) {
      problems.push(`loan type ${t.code}: ${t.facility} is not in the live entitlement catalogue`);
    }
  }
  if (problems.length) throw new Error(`[demo-masar] loan facilities do not match the catalogue:\n  ${problems.join('\n  ')}`);
}

// ── Overtime policy ──────────────────────────────────────────────────────────────────────────

/** One active tenant overtime policy, so /ess/overtime (POST /api/overtime/requests) can be used at all. */
async function ensureOvertimePolicy(adminToken: string): Promise<void> {
  const policies = items(ok(await call('GET', '/api/overtime/policies', { token: adminToken }), 'list overtime policies').body);
  const mine = policies.find((p: any) => (p.code ?? p.Code) === OVERTIME_POLICY.code);
  if (mine) {
    if (!(mine.isActive ?? mine.IsActive)) {
      throw new Error(`[demo-masar] overtime policy ${OVERTIME_POLICY.code} exists but is inactive; reactivate it in Overtime.`);
    }
    return;
  }
  ok(await call('POST', '/api/overtime/policies', {
    token: adminToken,
    body: {
      code: OVERTIME_POLICY.code, name: OVERTIME_POLICY.name, hourlyRateBasis: OVERTIME_POLICY.hourlyRateBasis,
      fixedHourlyRate: 0, standardMonthlyHours: OVERTIME_POLICY.standardMonthlyHours,
      minimumMinutes: OVERTIME_POLICY.minimumMinutes, maximumMinutesPerDay: OVERTIME_POLICY.maximumMinutesPerDay,
      monthlyCapMinutes: OVERTIME_POLICY.monthlyCapMinutes, roundingRule: OVERTIME_POLICY.roundingRule,
      requiresApproval: OVERTIME_POLICY.requiresApproval, allowCompOffConversion: OVERTIME_POLICY.allowCompOffConversion,
      regularDayMultiplier: OVERTIME_POLICY.regularDayMultiplier, weekendMultiplier: OVERTIME_POLICY.weekendMultiplier,
      holidayMultiplier: OVERTIME_POLICY.holidayMultiplier,
    },
  }), `create overtime policy ${OVERTIME_POLICY.code}`, [200, 201]);
  log(`created overtime policy ${OVERTIME_POLICY.code}`);
}

// ── Go-live cutover and Asif's carried-in loan ────────────────────────────────────────────────

/**
 * Masar went live on 1 Aug 2026. Asif's loan was disbursed before that, so it comes in through the
 * product's opening-balance import (the only sanctioned path for a loan with history: the live
 * approval flow refuses a back-dated repayment start, by design).
 */
async function ensureCutoverAndAsifLoan(adminToken: string, byCode: Map<string, any>): Promise<void> {
  const asif = byCode.get(ASIF_LOAN.employeeCode);
  const loans = items(ok(await call('GET', `/api/finance/loans?employeeId=${asif.publicId ?? asif.PublicId}&pageSize=50`, { token: adminToken }),
    'list Asif loans').body);
  if (loans.some((l: any) => (l.loanNumber ?? l.LoanNumber) === ASIF_LOAN.loanNumber)) return;

  const cutover = ['CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes',
    ...COMPANIES.map((c) => `${c.registrationNumber},${c.legalNameEn},${GO_LIVE_CUTOVER},${ASIF_LOAN.sourceSystem},Active,Masar Holding go-live`),
  ].join('\n');
  const l = ASIF_LOAN;
  const loanCsv = [
    'EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId',
    `${l.employeeCode},${l.loanNumber},${l.loanTypeCode},Personal Loan,${l.original},${l.instalment},${l.total},${l.paidBeforeCutover},${l.outstandingAtCutover},${l.firstUnpaidDue},${l.disbursed},SAR,${l.sourceSystem},${l.sourceRecordId}`,
  ].join('\n');
  const res = ok(await call('POST', '/api/migrations/commit', {
    token: adminToken,
    body: { externalBatchId: 'masar-go-live-2026-08', sections: { companyCutover: cutover, loans: loanCsv } },
  }), 'import go-live cutover and Asif\'s loan');
  const failed = JSON.stringify(res.body ?? {}).match(/"(errors|failed)"\s*:\s*\[[^\]]+\]/);
  if (failed) throw new Error(`[demo-masar] opening-balance import reported row errors: ${res.text.slice(0, 900)}`);
  log(`imported cutover ${GO_LIVE_CUTOVER} and Asif's loan (${l.paidBeforeCutover}/${l.total} repaid before go-live)`);
}

/**
 * Annual-leave opening balances at the go-live cutover (as at 31 Jul 2026), through the same
 * opening-balance import: 21 days a year, 30 from five years' service (Art 109), seven months
 * accrued, and a few days already taken in the previous system. Imported once — the import is an
 * upsert, and a re-run must not overwrite leave taken since.
 */
async function ensureLeaveOpeningBalances(adminToken: string, byCode: Map<string, any>): Promise<void> {
  const probe = byCode.get(EMPLOYEES[0].code);
  const held = items(ok(await call('GET', `/api/leave/balances/employee/${probe.id ?? probe.Id}?year=2026`, { token: adminToken }),
    'read leave balances').body);
  if (held.some((b: any) => /ANNUAL/i.test(String(b.leaveTypeCode ?? b.leaveTypeName ?? b.leaveType ?? '')))) return;
  const cutover = new Date(`${GO_LIVE_CUTOVER}T00:00:00Z`);
  const rows = EMPLOYEES.map((e) => {
    const years = (cutover.getTime() - new Date(`${joiningDate(e)}T00:00:00Z`).getTime()) / (365.25 * 86_400_000);
    const entitled = years >= 5 ? 30 : 21;
    const accrued = Math.round(entitled * 7 / 12 * 100) / 100;
    const used = Math.min(hashOf(`leave:${e.code}`) % 6, Math.floor(accrued));
    return `${e.code},ANNUAL,2026,${entitled},${accrued},${used},0,0,0,0,0,false`;
  });
  ok(await call('POST', '/api/migrations/commit', {
    token: adminToken,
    body: {
      externalBatchId: 'masar-go-live-leave-2026-08',
      sections: { leaveBalances: ['EmployeeCode,LeaveTypeCode,Year,Entitled,Accrued,Used,Pending,CarriedForward,Encashed,Expired,ManualAdjustment,NegativeAllowed', ...rows].join('\n') },
    },
  }), 'import annual-leave opening balances');
  log(`imported ${rows.length} annual-leave opening balance(s) as at the ${GO_LIVE_CUTOVER} go-live`);
}

// ── Payroll: August and September 2026, both companies, to Locked ────────────────────────────

/**
 * Maker-checker as the product requires of humans: Payroll creates and processes, the HR Manager
 * approves (→ PendingFinanceReview), Finance approves and locks. Months in order, so payslip YTD
 * figures build up. Lock is what makes slips Final and visible in employee self-service.
 */
async function ensurePayroll(companyIds: Record<CompanyKey, string>, creds: Credentials): Promise<string[]> {
  const payroll = await requireLogin(persona('payroll'), creds);
  const hr = await requireLogin(persona('hrManager'), creds);
  const finance = await requireLogin(persona('finance'), creds);
  const summary: string[] = [];

  for (const { year, month } of PAYROLL_MONTHS) {
    for (const c of COMPANIES) {
      const companyId = companyIds[c.key];
      const find = async () => (await allPages(payroll, '/api/payroll/runs'))
        .find((r: any) => r.year === year && r.month === month && String(r.companyId ?? r.CompanyId) === companyId
          && String(r.status ?? r.Status) !== 'Voided');
      let run = await find();
      if (!run) {
        ok(await call('POST', '/api/payroll/runs', {
          token: payroll, companyId, body: { year, month, companyId, runType: 'Regular' },
        }), `create ${year}-${month} run ${c.key}`, [200, 201]);
        run = await find();
      }
      const id = idOf(run);
      const label = `${c.key} ${year}-${String(month).padStart(2, '0')}`;
      for (let step = 0; step < 6; step++) {
        const status = String(run.status ?? run.Status);
        let res: Res | null = null;
        if (status === 'Draft') res = await call('POST', `/api/payroll/runs/${id}/process`, { token: payroll });
        else if (status === 'Processed') res = await call('POST', `/api/payroll/runs/${id}/approve`, { token: hr, body: { notes: 'Reviewed by HR', expectedExcludedCount: 0 } });
        else if (status === 'PendingFinanceReview') res = await call('POST', `/api/payroll/runs/${id}/approve`, { token: finance, body: { notes: 'Finance approval', expectedExcludedCount: 0 } });
        else if (status === 'Approved') res = await call('POST', `/api/payroll/runs/${id}/lock`, { token: finance });
        else break;
        ok(res, `${label}: ${status} -> next`);
        run = await find();
      }
      const final = String(run.status ?? run.Status);
      if (final !== 'Locked' && final !== 'Paid') throw new Error(`[demo-masar] payroll ${label} stopped at ${final}.`);
      // Issue the payslip documents (published to self-service because the run is Locked), once.
      const issued = items(ok(await call('GET', `/api/payroll/runs/${id}/payslips`, { token: payroll }), `${label}: list payslips`).body);
      if (issued.length === 0) {
        ok(await call('POST', `/api/payroll/runs/${id}/payslips/generate`, { token: payroll }), `${label}: generate payslips`);
        log(`${label}: payslips issued`);
      }
      summary.push(`${label} ${final}`);
    }
  }
  return summary;
}

// ── October attendance so far ─────────────────────────────────────────────────────────────

/**
 * Clock-ins for every October working day before today (Sun–Thu; Fri/Sat is the KSA weekend), 08:00–
 * 17:00 Riyadh, for everyone EXCEPT Asif on his absence day. Without them, processing October
 * attendance later would mark the whole company absent on those days, and the October payslip in
 * the storyline would carry far more than Asif's one day.
 */
async function ensureOctoberAttendance(adminToken: string, companyIds: Record<CompanyKey, string>): Promise<string> {
  const today = new Date().toISOString().slice(0, 10);
  const days: string[] = [];
  for (let d = new Date('2026-10-01T00:00:00Z'); d.toISOString().slice(0, 10) < today && d.getUTCMonth() === 9;
    d = new Date(d.getTime() + 86_400_000)) {
    const dow = d.getUTCDay(); // 5 = Friday, 6 = Saturday
    if (dow !== 5 && dow !== 6) days.push(d.toISOString().slice(0, 10));
  }
  if (days.length === 0) return 'no October working days before today';
  let punches = 0;
  for (const c of COMPANIES) {
    const existing = items(ok(await call('GET', `/api/attendance/daily?from=${days[0]}&to=${days[0]}&page=1&pageSize=100`,
      { token: adminToken, companyId: companyIds[c.key] }), 'read attendance').body)
      .filter((r: any) => r.firstInUtc ?? r.FirstInUtc);
    if (existing.length > 0) continue;
    const rows = ['employeeCode,punchTimestamp,punchDirection'];
    for (const day of days) {
      for (const e of EMPLOYEES.filter((x) => x.company === c.key)) {
        if (e.code === ASIF_LOAN.employeeCode && day === ASIF_ABSENCE_DATE) continue;
        rows.push(`${e.code},${day}T05:00:00Z,In`, `${e.code},${day}T14:00:00Z,Out`);
        punches += 2;
      }
    }
    ok(await call('POST', '/api/attendance/events/import', {
      token: adminToken, companyId: companyIds[c.key],
      body: { fileName: `masar-${c.key.toLowerCase()}-2026-10.csv`, csvContent: rows.join('\n') },
    }), `import October punches ${c.key}`, [200, 201]);
  }
  if (punches) {
    // No X-Company-Id: a tenant-wide reprocess from a pinned company is refused (see provision.ts).
    ok(await call('POST', '/api/attendance/process', {
      token: adminToken, body: { fromDate: days[0], toDate: days[days.length - 1], employeeId: null },
    }), 'process October attendance');
    log(`imported ${punches} October punch(es) for ${days.join(', ')}`);
  }
  return `${days.length} working day(s)`;
}

// ── Asif's absence day ───────────────────────────────────────────────────────────────────────

/**
 * One working day with no punches and no leave, processed by attendance → Absent → an
 * "Absence deduction" payroll impact of basic/30 = SAR 160 on the October payslip. Processed for Asif
 * ALONE: processing a range for everyone with no punches would mark the whole company absent.
 */
async function ensureAsifAbsence(adminToken: string, byCode: Map<string, any>): Promise<string> {
  const asif = byCode.get(ASIF_LOAN.employeeCode);
  const intId = Number(asif.id ?? asif.Id);
  const daily = items(ok(await call('GET', `/api/attendance/daily?from=${ASIF_ABSENCE_DATE}&to=${ASIF_ABSENCE_DATE}&page=1&pageSize=100`, { token: adminToken }),
    'read attendance').body);
  const mine = daily.find((d: any) => Number(d.employeeId ?? d.EmployeeId) === intId
    || String(d.employeeCode ?? d.EmployeeCode) === ASIF_LOAN.employeeCode);
  if (!mine) {
    ok(await call('POST', '/api/attendance/process', {
      token: adminToken, body: { fromDate: ASIF_ABSENCE_DATE, toDate: ASIF_ABSENCE_DATE, employeeId: intId },
    }), 'process Asif\'s absence day');
    const again = items(ok(await call('GET', `/api/attendance/daily?from=${ASIF_ABSENCE_DATE}&to=${ASIF_ABSENCE_DATE}&page=1&pageSize=100`, { token: adminToken }),
      'read attendance').body).find((d: any) => Number(d.employeeId ?? d.EmployeeId) === intId
      || String(d.employeeCode ?? d.EmployeeCode) === ASIF_LOAN.employeeCode);
    return String(again?.status ?? again?.Status ?? 'no record');
  }
  return String(mine.status ?? mine.Status);
}

// ── Tenant defaults ───────────────────────────────────────────────────────────────────────────

async function ensureTenantDefaults(adminToken: string, platformToken: string, tenantId: string): Promise<void> {
  ok(await call('PUT', `/api/platform/tenants/${tenantId}/localization`, {
    token: platformToken, body: { currencyCode: 'SAR', countryCode: 'SA', defaultLanguage: 'en' },
  }), 'set localization');
  for (const path of ['/api/hr-letters/templates/seed-defaults', '/api/finance/gl/seed-defaults']) {
    const res = await call('POST', path, { token: adminToken, body: {} });
    if (res.status >= 300) log(`${path} returned ${res.status} (${(res.body?.message ?? res.text).slice(0, 140)})`);
  }
}

// ── Release A TODO hooks (phase 2) ───────────────────────────────────────────────────────────
//
// R0–R4 are on main now, every surface behind the per-tenant release_a flag
// (PUT /api/platform/tenants/{id}/features/release_a). Phase 1 deliberately leaves the flag OFF: with it on,
// activating a contract term freezes that term's package (PackageFreezeOnActivation), so switching it on before
// the matrix is published would freeze packages with nothing in them. Phase 2 does, in order: flag on → matrix →
// dependants → package freeze → open-now.

/** TODO(R1): publish ENTITLEMENT_MATRIX group-wide from 1 Nov 2026 (PUT /api/entitlements/matrix), then the MLG
 *  overrides (Education → Skip, per diem G1 → SAR 200 Tailored) via PUT /api/entitlements/offerings and the matrix. */
function todoR1EntitlementMatrix(): string {
  return `TODO(R1) entitlement matrix: ${Object.keys(ENTITLEMENT_MATRIX).length - 2} benefit rows (PUT /api/entitlements/matrix, needs release_a)`;
}

/** TODO(R2): Mohammed's dependants (wife + 2 children) through POST /api/entitlements/employees/{id}/dependants,
 *  then each employee's contract-year package (POST /api/entitlements/package/freeze-bulk). */
function todoR2DependantsAndPackages(): string {
  return `TODO(R2) ${MOHAMMED_DEPENDANTS.length} dependants for MFS-0003 + contract-year packages (needs release_a)`;
}

/** TODO(R3): Asif's SAR 150 traffic fine for Oct 2026. R3 shipped the deductions STATEMENT; there is still no
 *  public API that creates a PayrollAdjustment for a fine (only leave encashment writes one). */
function todoR3TrafficFine(): string {
  return `TODO(R3) traffic fine SAR ${ASIF_TRAFFIC_FINE.amount} for ${ASIF_TRAFFIC_FINE.employeeCode} (${ASIF_TRAFFIC_FINE.period}): no adjustment API`;
}

/** TODO(R4–R6): R4's chain stamper sets renewed_from / renewal_number / chain_started_on when a term is activated
 *  on a release_a tenant; then POST /api/contracts/renewals/open-now creates the renewal cases (Faisal Art 55,
 *  Mohammed, the 12-person Ramon batch). TODO(R7 phase 2): process the October run once the fine exists. */
function todoR4toR6RenewalCases(): string {
  const ramon = EMPLOYEES.filter((e) => e.cast === 'ramon-group').length;
  return `TODO(R4-R6) renewal cases: Faisal (Art 55), Mohammed, Ramon group of ${ramon} (open-now, needs release_a)`;
}

// ── Orchestration ─────────────────────────────────────────────────────────────────────────────

/** Refuses to start when a grade-limited loan type would not land on its catalogue facility (no writes yet). */
export function assertLoanCatalogue(): void {
  const problems = loanCatalogueProblems();
  if (problems.length) throw new Error(`[demo-masar] loan types disagree with the entitlement catalogue:\n  ${problems.join('\n  ')}`);
}

export async function seedMasarDemo(): Promise<void> {
  assertSeedableHost();
  assertLoanCatalogue();
  const started = Date.now();
  const creds = await loadCredentials();
  const platformToken = await platformLogin();
  const tenantId = await ensureTenant(platformToken, creds);
  const adminToken = await requireLogin(persona('hrDirector'), creds);
  await ensureTenantDefaults(adminToken, platformToken, tenantId);
  const companyIds = await ensureCompanies(platformToken, tenantId, adminToken);
  const branchIds = await ensureBranches(adminToken, companyIds);
  await ensureStaffUsers(platformToken, tenantId, adminToken, creds);
  const gradeIds = await ensureGrades(adminToken);
  const org = await ensureOrg(adminToken, companyIds, branchIds);
  const byCode = await ensureEmployees(adminToken, companyIds, branchIds, gradeIds, org);
  await activateEmployees(adminToken, byCode);
  await ensureSalaries(adminToken, companyIds, byCode);
  await ensureContracts(adminToken, byCode);
  await ensureEmployeeLogins(adminToken, companyIds, byCode, creds);
  await ensureLeave(adminToken);
  await ensureLoans(adminToken, companyIds, gradeIds);
  await ensureOvertimePolicy(adminToken);
  await ensureCutoverAndAsifLoan(adminToken, byCode);
  await ensureLeaveOpeningBalances(adminToken, byCode);
  const payroll = await ensurePayroll(companyIds, creds);
  await ensureOctoberAttendance(adminToken, companyIds);
  const absence = await ensureAsifAbsence(adminToken, byCode);
  const todos = [todoR1EntitlementMatrix(), todoR2DependantsAndPackages(), todoR3TrafficFine(), todoR4toR6RenewalCases()];

  const manifest = {
    apiBaseUrl: API_BASE, seededAtUtc: new Date().toISOString(), tenantSlug: TENANT.slug, tenantId, companyIds,
    employees: EMPLOYEES.length, payroll, asifAbsence: `${ASIF_ABSENCE_DATE}: ${absence}`, todos,
    personas: PERSONAS.map((p) => ({ email: p.email, role: p.role, storylineRole: p.storylineRole })),
  };
  await mkdir(dirname(MANIFEST_FILE), { recursive: true });
  await writeFile(MANIFEST_FILE, JSON.stringify(manifest, null, 2), { mode: 0o600 });
  log(`payroll: ${payroll.join(', ')}; Asif ${ASIF_ABSENCE_DATE}: ${absence}`);
  for (const t of todos) log(t);
  log(`done in ${((Date.now() - started) / 1000).toFixed(1)}s — ${EMPLOYEES.length} employees, `
    + `${PERSONAS.length} personas. Passwords: ${CREDENTIALS_FILE}`);
}
