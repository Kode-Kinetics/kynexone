/**
 * ENGAGEMENT SEED — the deterministic, API-only world the engagement journey runs on. Same rules as provision.ts:
 * no SQL, idempotent (reads before it writes), fail-loud, disposable-host guarded. Nothing here is real customer data.
 *
 * Tenant `eng-acme` (Saudi, one company): departments Operations / Finance / Legal / Management; designations Engineer /
 * Analyst / Supervisor / Manager; 36 employees in Operations (Engineer 12, Analyst 5, Supervisor 6), Finance (Analyst 8,
 * Engineer 2) and Legal (Analyst 3) reporting to four managers in Management; every one with a real employee login made
 * the product's way (HR invites, the employee accepts and sets a password). Tenant `eng-other` is the isolation check.
 * The `engagement` opt-in flag is switched on for both by the platform owner.
 */
import { assertDisposableHost } from '../disposable-host.guard';
import { collectAllPages, pageItems, pageTotal } from '../paging';

const API = (process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5212').replace(/\/$/, '');
const PLATFORM_EMAIL = process.env.PLATFORM_ADMIN_EMAIL ?? '';
const PLATFORM_PASSWORD = process.env.PLATFORM_ADMIN_PASSWORD ?? '';
/** Local-only password for every seeded login; overridable. Never a production credential. */
export const SEED_PASSWORD = process.env.ENGAGEMENT_SEED_PASSWORD ?? 'Eng-Seed-Local-2026!';

export const ACME = { slug: 'eng-acme', name: 'Engagement Acme', domain: 'eng-acme.test', admin: 'hr.director@eng-acme.test' };
export const OTHER = { slug: 'eng-other', name: 'Engagement Other Co', domain: 'eng-other.test', admin: 'hr@eng-other.test' };

export interface SeedPerson { code: string; name: string; dept: 'OPS' | 'FIN' | 'LEG' | 'MGT'; desig: 'ENG' | 'ANA' | 'SUP' | 'MGR'; manager: string | null; email: string; role: 'Employee' | 'Manager' }

/** The deterministic population. Managers first (they are everyone else's reporting line). */
export const PEOPLE: SeedPerson[] = (() => {
  const people: SeedPerson[] = [];
  const add = (code: string, name: string, dept: SeedPerson['dept'], desig: SeedPerson['desig'], manager: string | null, role: SeedPerson['role'] = 'Employee') =>
    people.push({ code, name, dept, desig, manager, role, email: `${code.toLowerCase()}@${ACME.domain}` });
  add('MGR-ENG', 'Mona Al-Harbi', 'MGT', 'MGR', null, 'Manager');
  add('MGR-OPS', 'Omar Siddiqui', 'MGT', 'MGR', null, 'Manager');
  add('MGR-FIN', 'Fatima Zahra', 'MGT', 'MGR', null, 'Manager');
  add('MGR-LEG', 'Layla Al-Mutairi', 'MGT', 'MGR', null, 'Manager');
  const first = ['Noura', 'Rashid', 'Daniel', 'Priya', 'Hassan', 'Mariam', 'Victor', 'Sara', 'Bilal', 'Kenji', 'Grace', 'Tomas'];
  let n = 0;
  const batch = (count: number, dept: SeedPerson['dept'], desig: SeedPerson['desig'], manager: string) => {
    for (let i = 0; i < count; i++) {
      n++;
      add(`ACM-${String(n).padStart(3, '0')}`, `${first[n % first.length]} ${dept}-${desig}-${i + 1}`, dept, desig, manager);
    }
  };
  batch(12, 'OPS', 'ENG', 'MGR-ENG');
  batch(5, 'OPS', 'ANA', 'MGR-OPS');
  batch(6, 'OPS', 'SUP', 'MGR-OPS');
  batch(8, 'FIN', 'ANA', 'MGR-FIN');
  batch(2, 'FIN', 'ENG', 'MGR-FIN');
  batch(3, 'LEG', 'ANA', 'MGR-LEG');
  return people;
})();

interface Res<T = any> { status: number; body: T; text: string }

async function call<T = any>(method: string, path: string, opts: { token?: string; body?: unknown; companyId?: string } = {}): Promise<Res<T>> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  if (opts.companyId) headers['X-Company-Id'] = opts.companyId;
  const response = await fetch(`${API}${path}`, { method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body) });
  const text = await response.text();
  let body: any = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  return { status: response.status, body, text };
}

function ok<T>(res: Res<T>, what: string, accept = [200, 201, 204]): Res<T> {
  if (!accept.includes(res.status)) throw new Error(`[engagement-seed] ${what} failed: HTTP ${res.status}\n${res.text.slice(0, 600)}`);
  return res;
}

/** API sessions are reused for a few minutes: the login abuse guard rightly locks an account signed in dozens of times. */
const sessions = new Map<string, { token: string; at: number }>();

export async function login(email: string, slug: string, password = SEED_PASSWORD): Promise<string> {
  const key = `${slug}/${email}`;
  const cached = sessions.get(key);
  if (cached && Date.now() - cached.at < 8 * 60_000) return cached.token;
  const res = ok(await call('POST', '/api/auth/login', { body: { email, password, tenantSlug: slug } }), `login ${email}`);
  const token = res.body?.accessToken ?? res.body?.token;
  if (!token) throw new Error(`[engagement-seed] ${email} logged in but got no token (MFA prompt?): ${res.text.slice(0, 200)}`);
  sessions.set(key, { token, at: Date.now() });
  return token;
}

async function platformLogin(): Promise<string> {
  if (!PLATFORM_EMAIL || !PLATFORM_PASSWORD) throw new Error('[engagement-seed] Set PLATFORM_ADMIN_EMAIL and PLATFORM_ADMIN_PASSWORD to the values the API was started with.');
  const res = ok(await call('POST', '/api/platform/auth/login', { body: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD } }), 'platform login');
  return res.body.token ?? res.body.accessToken;
}

async function ensureTenant(platform: string, t: typeof ACME): Promise<string> {
  const list = ok(await call('GET', '/api/platform/tenants', { token: platform }), 'list tenants');
  const existing = pageItems(list.body).find((x: any) => x.slug === t.slug);
  const id = existing?.id ?? ok(await call('POST', '/api/platform/tenants', {
    token: platform,
    body: {
      name: t.name, slug: t.slug, adminEmail: t.admin, adminFullName: 'Hana HR Director', adminPassword: SEED_PASSWORD,
      accountType: 'SingleCompany', plan: 'Growth', maxUsers: 100, maxEmployees: 200, maxCompanies: 1,
      billingEmail: `billing@${t.domain}`, billingCycle: 'Monthly', monthlyAmount: 0, currencyCode: 'SAR', homeCountryCode: 'SA',
    },
  }), `create tenant ${t.slug}`, [201]).body.tenantId;
  ok(await call('PUT', `/api/platform/tenants/${id}/localization`, { token: platform, body: { currencyCode: 'SAR', countryCode: 'SA', defaultLanguage: 'en' } }), 'localization');
  ok(await call('PUT', `/api/platform/tenants/${id}/features/engagement`, { token: platform, body: { isEnabled: true } }), `enable engagement on ${t.slug}`);
  return id;
}

async function company(admin: string, t: typeof ACME): Promise<string> {
  const list = pageItems(ok(await call('GET', '/api/companies?page=1&pageSize=100', { token: admin }), 'companies').body);
  const c = list[0];
  // The company's email domain is what work emails derive into; it must be the tenant's.
  ok(await call('PUT', `/api/companies/${c.id}`, {
    token: admin,
    body: {
      legalNameEn: `${t.name} KSA`, legalNameAr: 'أكمي السعودية', tradeName: t.name, countryCode: 'SA',
      registrationNumber: `CR-${t.slug}`, taxNumber: `VAT-${t.slug}`, defaultCurrency: 'SAR', emailDomain: t.domain, isActive: true,
    },
  }), 'company details');
  return c.id;
}

async function ensureUnits(admin: string, companyId: string): Promise<{ dept: Record<string, string>; desig: Record<string, string> }> {
  const depts = pageItems(ok(await call('GET', '/api/departments?page=1&pageSize=100', { token: admin, companyId }), 'departments').body);
  const desigs = pageItems(ok(await call('GET', '/api/designations?page=1&pageSize=100', { token: admin, companyId }), 'designations').body);
  const dept: Record<string, string> = {};
  const desig: Record<string, string> = {};
  const deptNames: Record<string, [string, string]> = { OPS: ['Operations', 'العمليات'], FIN: ['Finance', 'المالية'], LEG: ['Legal', 'الشؤون القانونية'], MGT: ['Management', 'الإدارة'] };
  for (const [code, [en, ar]] of Object.entries(deptNames)) {
    const found = depts.find((d: any) => d.code === `ENG-${code}`);
    dept[code] = found?.id ?? ok(await call('POST', '/api/departments', { token: admin, companyId, body: { code: `ENG-${code}`, nameEn: en, nameAr: ar, isActive: true } }), `department ${code}`, [200, 201]).body.id;
  }
  const desigNames: Record<string, [string, string]> = { ENG: ['Engineer', 'مهندس'], ANA: ['Analyst', 'محلل'], SUP: ['Supervisor', 'مشرف'], MGR: ['Manager', 'مدير'] };
  for (const [code, [en, ar]] of Object.entries(desigNames)) {
    const found = desigs.find((d: any) => d.code === `ENG-${code}`);
    desig[code] = found?.id ?? ok(await call('POST', '/api/designations', { token: admin, companyId, body: { code: `ENG-${code}`, titleEn: en, titleAr: ar, isActive: true } }), `designation ${code}`, [200, 201]).body.id;
  }
  return { dept, desig };
}

let seq = 0;
const idRecord = (fieldKey: string, fieldLabel: string, fieldValue: string) => ({ countryCode: 'SA', fieldKey, fieldLabel, fieldValue, isSensitive: true, isRequired: true });

function saudiIban(seed: number): string {
  const bban = `80${String(seed).padStart(18, '0')}`.slice(-20);
  let remainder = 0;
  for (const ch of `${bban}281000`) remainder = (remainder * 10 + Number(ch)) % 97;
  return `SA${String(98 - remainder).padStart(2, '0')}${bban}`;
}

async function allEmployees(token: string): Promise<any[]> {
  return collectAllPages(async (page, pageSize) => {
    const res = ok(await call('GET', `/api/employees?page=${page}&pageSize=${pageSize}`, { token }), 'list employees');
    return { items: pageItems(res.body), total: pageTotal(res.body) };
  });
}

async function ensureEmployees(admin: string, companyId: string, units: Awaited<ReturnType<typeof ensureUnits>>): Promise<Map<string, number>> {
  const ids = new Map<string, number>();
  for (const e of await allEmployees(admin)) ids.set(String(e.employeeCode), Number(e.id));
  for (const p of PEOPLE) {
    if (ids.has(p.code)) continue;
    seq++;
    const res = ok(await call('POST', '/api/employees', {
      token: admin, companyId,
      body: {
        employeeCode: p.code, manualEmployeeCode: true, englishName: p.name, gender: seq % 2 ? 'Male' : 'Female',
        dateOfBirth: '1990-01-15', nationality: 'Saudi', personalEmail: `${p.code.toLowerCase()}@personal.eng-acme.test`, workEmail: p.email,
        companyId, departmentId: units.dept[p.dept], designationId: units.desig[p.desig],
        reportingManagerEmployeeId: p.manager ? ids.get(p.manager) ?? null : null,
        jobTitle: p.desig === 'MGR' ? 'Manager' : 'Specialist', employmentType: 'FullTime', contractType: 'Unlimited',
        // Tenure bands vary deterministically (some under a year) so the tenure breakdown has more than one group.
        joiningDate: seq % 4 === 0 ? '2026-03-01T00:00:00Z' : '2023-01-01T00:00:00Z',
        payrollProfile: {
          bankName: 'Al Rajhi Bank', iban: saudiIban(7080101675 + seq), accountNumber: String(7080101675 + seq),
          paymentMethod: 'BankTransfer', salaryCurrency: 'SAR', wpsEligible: true, eosbEligible: true,
          socialInsuranceReference: `SI-${p.code}`, molId: `MOL-${p.code}`,
        },
        salaryBreakdown: { basicSalary: 7000, housingAllowance: 1500, transportAllowance: 500, effectiveDate: '2024-01-01', currency: 'SAR' },
        complianceRecords: [
          idRecord('civil_id', 'Civil ID', `1${String(210000000 + seq)}`),
          idRecord('id_number', 'Government ID number', `1${String(310000000 + seq)}`),
          idRecord('gosi_reference', 'GOSI reference', `GOSI-${p.code}`),
          idRecord('iqama_number', 'Iqama Number', `2${String(410000000 + seq)}`),
          idRecord('emirates_id', 'Emirates ID', `784-1990-${String(2000000 + seq)}-1`),
          idRecord('work_permit', 'Work permit number', `WP-${p.code}`),
          idRecord('passport_number', 'Passport number', `P${String(20000000 + seq)}`),
          idRecord('qid', 'Qatar ID', `2${String(81000000 + seq)}`),
          idRecord('residency_number', 'Residency number', `RES-${p.code}`),
          idRecord('labor_card_number', 'Labour card number', `LC-${p.code}`),
          idRecord('muqeem_reference', 'Muqeem reference', `MQ-${p.code}`),
          idRecord('qiwa_contract_reference', 'Qiwa contract reference', `QC-${p.code}`),
        ],
        acknowledgeDuplicate: true,
      },
    }), `create employee ${p.code}`, [200, 201]);
    ids.set(p.code, Number(res.body.id ?? res.body.Id));
  }
  for (const e of await allEmployees(admin)) {
    if (String(e.status) === 'Active') continue;
    ok(await call('POST', `/api/employees/${e.id}/activate`, { token: admin, body: { status: 'Active', reason: 'engagement seed' } }), `activate ${e.employeeCode}`);
  }
  return ids;
}

async function ensureLogins(admin: string, companyId: string, ids: Map<string, number>, slug: string): Promise<void> {
  for (const p of PEOPLE) {
    const probe = await call('POST', '/api/auth/login', { body: { email: p.email, password: SEED_PASSWORD, tenantSlug: slug } });
    if (probe.status === 200) continue;
    const invite = ok(await call('POST', '/api/access/employee-logins/invite', {
      token: admin, companyId,
      body: { employeeId: ids.get(p.code), email: p.email, accessMode: p.role === 'Manager' ? 'ManagerPortal' : 'ESSOnly', roles: [p.role] },
    }), `invite ${p.code}`, [200, 201]);
    const token = decodeURIComponent(String(invite.body.invitationUrl ?? '').split('#token=')[1] ?? '');
    if (!token) throw new Error(`[engagement-seed] The invitation for ${p.email} came back without a link.`);
    ok(await call('POST', '/api/auth/accept-invitation', { body: { invitationToken: token, newPassword: SEED_PASSWORD, tenantSlug: slug } }), `accept ${p.code}`, [200, 204]);
  }
}

export interface EngagementWorld { acmeTenantId: string; otherTenantId: string; companyId: string; departments: Record<string, string>; designations: Record<string, string>; employees: Record<string, number> }

export async function seedEngagementWorld(baseUrl: string): Promise<EngagementWorld> {
  assertDisposableHost(baseUrl, 'seed the engagement world');
  assertDisposableHost(API, 'seed the engagement world');
  const platform = await platformLogin();
  const acmeTenantId = await ensureTenant(platform, ACME);
  const otherTenantId = await ensureTenant(platform, OTHER);
  const admin = await login(ACME.admin, ACME.slug);
  const companyId = await company(admin, ACME);
  const units = await ensureUnits(admin, companyId);
  const ids = await ensureEmployees(admin, companyId, units);
  await ensureLogins(admin, companyId, ids, ACME.slug);
  const otherAdmin = await login(OTHER.admin, OTHER.slug);
  await company(otherAdmin, OTHER);
  return { acmeTenantId, otherTenantId, companyId, departments: units.dept, designations: units.desig, employees: Object.fromEntries(ids) };
}
