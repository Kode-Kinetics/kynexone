import { saudiNationalId } from './saudiIds';
import { test as setup } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { ensurePayApprover } from './payApprover';
import { assertDisposableHost } from '../disposable-host.guard';
import { CMP_WORLD, type CmpWorld } from './compensationWorld';

/**
 * CMP real-backend seed — deterministic, API-only, idempotent, for the merit-planning journey
 * (e2e/compensation-journey.spec.ts). Everything is created the sanctioned way the fixture bootstrap uses
 * (e2e/bootstrap/provision.ts): as the platform owner through /api/platform/**, then as the tenant administrator
 * through the tenant API, and the line manager's login through the employee-invitation flow. No SQL.
 *
 * World: tenant `cmp-alpha` (SA, SAR) with the compensation opt-in flag ON, company CMP-KSA, departments
 * Engineering and Operations, grade CMP-G1 (8,000 / 10,000 / 12,000 SAR), a line manager (Mona) with four eligible
 * reports in Engineering — one paid in AED — and one report who joined after the hire-date cutoff; HR Manager,
 * HR Director and Finance Approver users. A second tenant `cmp-beta` exists only to prove isolation.
 * Every figure is invented test data. No market data is created here: the benchmark file the journey imports is
 * e2e/fixtures/compensation/synthetic-benchmarks.csv, labelled SYNTHETIC — NOT MARKET DATA.
 *
 * Run (disposable stack only):
 *   E2E_API_BASE_URL=http://127.0.0.1:5213 PLATFORM_ADMIN_EMAIL=… PLATFORM_ADMIN_PASSWORD=… \
 *   npx playwright test -c e2e/playwright.compensation-real.config.ts --project=seed
 */

const API = (process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5213').replace(/\/$/, '');
const PLATFORM_EMAIL = process.env.PLATFORM_ADMIN_EMAIL ?? '';
const PLATFORM_PASSWORD = process.env.PLATFORM_ADMIN_PASSWORD ?? '';
const PASSWORD = process.env.E2E_CMP_PASSWORD ?? 'CmpLocal-Only-2026!x';

interface Res { status: number; body: any; text: string }
async function call(method: string, path: string, opts: { token?: string; body?: unknown; companyId?: string } = {}): Promise<Res> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  if (opts.companyId) headers['X-Company-Id'] = opts.companyId;
  const r = await fetch(`${API}${path}`, { method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body) });
  const text = await r.text();
  let body: any = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* keep text */ }
  return { status: r.status, body, text };
}
function ok(res: Res, what: string, accept = [200, 201, 204]): Res {
  if (!accept.includes(res.status)) throw new Error(`[cmp-seed] ${what}: HTTP ${res.status}\n${res.text.slice(0, 800)}`);
  return res;
}
const items = (b: any): any[] => (Array.isArray(b) ? b : b?.items ?? b?.Items ?? []);

async function login(email: string, password: string, slug: string): Promise<string | null> {
  const r = await call('POST', '/api/auth/login', { body: { email, password, tenantSlug: slug } });
  return r.status === 200 ? (r.body?.accessToken ?? r.body?.token) : null;
}

function iban(seed: number): string {
  const bban = `80${String(seed).padStart(18, '0')}`.slice(-20);
  let rem = 0;
  for (const ch of `${bban}281000`) rem = (rem * 10 + Number(ch)) % 97;
  return `SA${String(98 - rem).padStart(2, '0')}${bban}`;
}

let seq = 900;
const rec = (fieldKey: string, fieldLabel: string, fieldValue: string) => ({ countryCode: 'SA', fieldKey, fieldLabel, fieldValue, isSensitive: true, isRequired: true });

async function ensureTenant(platform: string, slug: string, name: string, admin: string): Promise<string> {
  const list = ok(await call('GET', '/api/platform/tenants', { token: platform }), 'list tenants');
  const found = items(list.body).find((t: any) => t.slug === slug);
  if (found) return found.id;
  const created = ok(await call('POST', '/api/platform/tenants', {
    token: platform,
    body: {
      name, slug, adminEmail: admin, adminFullName: `${name} Administrator`, adminPassword: PASSWORD, accountType: 'SingleCompany', plan: 'Enterprise',
      maxUsers: 50, maxEmployees: 200, maxCompanies: 2, billingEmail: `billing@${slug}.test`, billingCycle: 'Monthly', monthlyAmount: 0, currencyCode: 'SAR', homeCountryCode: 'SA',
    },
  }), `create tenant ${slug}`, [201]);
  return created.body.tenantId;
}

setup('seed the CMP merit-planning world (synthetic, API-only)', async () => {
  setup.setTimeout(300_000);
  assertDisposableHost(API, 'seed the CMP merit-planning world');
  if (!PLATFORM_EMAIL || !PLATFORM_PASSWORD) throw new Error('[cmp-seed] PLATFORM_ADMIN_EMAIL / PLATFORM_ADMIN_PASSWORD must match the API process.');
  const p = ok(await call('POST', '/api/platform/auth/login', { body: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD } }), 'platform login');
  const platform = p.body?.token ?? p.body?.accessToken;

  const slug = 'cmp-alpha';
  const domain = 'cmp-alpha.test';
  const users = {
    admin: `admin@${domain}`, hr: `hr@${domain}`, hrDirector: `hrdirector@${domain}`, finance: `finance@${domain}`, manager: `mona@${domain}`, betaAdmin: 'admin@cmp-beta.test',
  };
  const tenantId = await ensureTenant(platform, slug, 'CMP Alpha (synthetic)', users.admin);
  ok(await call('PUT', `/api/platform/tenants/${tenantId}/localization`, { token: platform, body: { currencyCode: 'SAR', countryCode: 'SA', defaultLanguage: 'en' } }), 'localization');
  ok(await call('PUT', `/api/platform/tenants/${tenantId}/features/compensation`, { token: platform, body: { isEnabled: true } }), 'enable compensation');
  const admin = await login(users.admin, PASSWORD, slug);
  if (!admin) throw new Error('[cmp-seed] tenant admin cannot log in (a database from an earlier run? it is disposable — recreate it).');

  // Company: the starter company, renamed and given its SAR currency and email domain.
  const companies = items(ok(await call('GET', '/api/companies?page=1&pageSize=100', { token: admin }), 'companies').body);
  const company = companies[0];
  const companyId = company.id ?? company.Id;
  if ((company.legalNameEn ?? '') !== 'CMP-KSA') {
    ok(await call('PUT', `/api/companies/${companyId}`, {
      token: admin,
      body: { legalNameEn: 'CMP-KSA', legalNameAr: 'CMP-KSA', tradeName: 'CMP-KSA', countryCode: 'SA', registrationNumber: 'CR-CMP-KSA', taxNumber: 'VAT-CMP-KSA', defaultCurrency: 'SAR', emailDomain: domain, isActive: true },
    }), 'rename company');
  }

  // Staff users (not employees): HR Manager, HR Director, Finance Approver — group scope.
  const existingUsers = items(ok(await call('GET', `/api/platform/tenants/${tenantId}/users`, { token: platform }), 'users').body).map((u: any) => String(u.email ?? u.Email).toLowerCase());
  for (const [email, role, name] of [[users.hr, 'HR Manager', 'Hana HR Manager'], [users.hrDirector, 'HR Director', 'Hadi HR Director'], [users.finance, 'Finance Approver', 'Farah Finance Approver']] as const) {
    if (existingUsers.includes(email)) continue;
    ok(await call('POST', `/api/platform/tenants/${tenantId}/users`, { token: platform, body: { email, password: PASSWORD, fullName: name, roleName: role, entityScope: 'group' } }), `user ${email}`);
  }

  // A hire's pay package needs a second person (payroll.approve + employees.sensitive) before activation.
  const approver = await ensurePayApprover({ apiBase: API, platformToken: platform, tenantId, slug, password: PASSWORD });

  // Org units and grade.
  const ensureDept = async (code: string, en: string, ar: string) => {
    const list = items((await call('GET', '/api/departments?page=1&pageSize=100', { token: admin, companyId })).body);
    const f = list.find((d: any) => (d.code ?? d.Code) === code);
    if (f) return f.id ?? f.Id;
    return ok(await call('POST', '/api/departments', { token: admin, companyId, body: { code, nameEn: en, nameAr: ar, isActive: true } }), `department ${code}`, [200, 201]).body.id;
  };
  const eng = await ensureDept('CMP-ENG', 'Engineering', 'الهندسة');
  const ops = await ensureDept('CMP-OPS', 'Operations', 'العمليات');
  const designations = items((await call('GET', '/api/designations?page=1&pageSize=100', { token: admin, companyId })).body);
  const designationId = designations.find((d: any) => (d.code ?? d.Code) === 'CMP-SWE')?.id
    ?? ok(await call('POST', '/api/designations', { token: admin, companyId, body: { departmentId: eng, code: 'CMP-SWE', titleEn: 'Software Engineer', titleAr: 'مهندس برمجيات', isActive: true } }), 'designation', [200, 201]).body.id;
  const grades = items((await call('GET', '/api/grades?page=1&pageSize=100', { token: admin })).body);
  const gradeId = grades.find((g: any) => (g.code ?? g.Code) === 'CMP-G1')?.id
    ?? ok(await call('POST', '/api/grades', { token: admin, body: { code: 'CMP-G1', name: 'CMP Grade 1', band: 'Standard', level: 1, minSalary: 8000, midSalary: 10000, maxSalary: 12000, currency: 'SAR', isActive: true } }), 'grade').body.id;

  // Employees: code, name, gender, basic, currency, joined, reports to manager?
  const people: Array<[string, string, string, number, string, string, boolean]> = [
    ['CMP-E1', 'Mona Manager', 'Female', 20000, 'SAR', '2019-01-01', false],
    ['CMP-E2', 'Ali Engineer', 'Male', 6000, 'SAR', '2020-01-01', true],
    ['CMP-E3', 'Sara Engineer', 'Female', 7000, 'SAR', '2020-01-01', true],
    ['CMP-E4', 'Omar Engineer', 'Male', 8000, 'SAR', '2020-01-01', true],
    ['CMP-E5', 'Noor Engineer', 'Female', 6000, 'AED', '2020-01-01', true],
    ['CMP-E6', 'Late Joiner', 'Male', 6000, 'SAR', '2026-08-01', true],
  ];
  const listed = items((await call('GET', '/api/employees?page=1&pageSize=100&search=CMP-E', { token: admin, companyId })).body);
  const ids: Record<string, number> = Object.fromEntries(listed.map((e: any) => [String(e.employeeCode ?? e.EmployeeCode), Number(e.id ?? e.Id)]));
  for (const [code, name, gender, basic, currency, joined, reports] of people) {
    if (ids[code]) continue;
    const n = ++seq;
    const res = ok(await call('POST', '/api/employees', {
      token: admin, companyId,
      body: {
        employeeCode: code, manualEmployeeCode: true, englishName: name, gender, dateOfBirth: '1990-01-15', nationality: 'Saudi',
        personalEmail: `${code.toLowerCase()}@personal.${domain}`, workEmail: code === 'CMP-E1' ? users.manager : undefined, companyId,
        gradeId: code === 'CMP-E1' ? null : gradeId, departmentId: code === 'CMP-E1' ? ops : eng, designationId,
        reportingManagerEmployeeId: reports ? ids['CMP-E1'] : null, jobTitle: code === 'CMP-E1' ? 'Engineering Manager' : 'Software Engineer',
        employmentType: 'FullTime', contractType: 'Unlimited', joiningDate: `${joined}T00:00:00Z`,
        payrollProfile: {
          bankName: 'Al Rajhi Bank', iban: iban(7080101675 + n), accountNumber: String(7080101675 + n), paymentMethod: 'BankTransfer', salaryCurrency: currency,
          wpsEligible: true, eosbEligible: true, socialInsuranceReference: `SI-${code}`, molId: `MOL-${code}`,
        },
        salaryBreakdown: { basicSalary: basic, housingAllowance: basic / 4, transportAllowance: 500, effectiveDate: joined < '2024-01-01' ? '2024-01-01' : joined, currency },
        complianceRecords: [
          rec('civil_id', 'Civil ID', `1${String(200000000 + n).slice(0, 9)}`), rec('id_number', 'Government ID number', saudiNationalId(300000000 + n)),
          rec('gosi_reference', 'GOSI reference', `GOSI-${code}`),
          rec('emirates_id', 'Emirates ID', `784-1990-${String(1000000 + n).slice(0, 7)}-1`), rec('work_permit', 'Work permit number', `WP-${code}`),
          rec('passport_number', 'Passport number', `P${String(10000000 + n).slice(0, 8)}`), rec('qid', 'Qatar ID', `2${String(8000000 + n).slice(0, 10)}`),
          rec('residency_number', 'Residency number', `RES-${code}`), rec('labor_card_number', 'Labour card number', `LC-${code}`),
          rec('muqeem_reference', 'Muqeem reference', `MQ-${code}`), rec('qiwa_contract_reference', 'Qiwa contract reference', `QC-${code}`),
        ],
        acknowledgeDuplicate: true,
        duplicateOverrideReason: 'E2E fixture world: deterministic synthetic people re-created per run',
        acknowledgedWarnings: [{ code: 'joining_backdated', reason: 'E2E fixture: historical joining date needed by the specs' }],
      },
    }), `employee ${code}`, [200, 201]);
    ids[code] = Number(res.body?.id ?? res.body?.Id);
    // A line manager must not be a Draft, so the manager is activated as soon as it exists.
    await approver.approve(ids[code]);
    if (code === 'CMP-E1') ok(await call('POST', `/api/employees/${ids[code]}/activate`, { token: admin, body: { status: 'Active', reason: 'CMP synthetic seed' } }), `activate ${code}`);
  }
  for (const code of Object.keys(ids)) {
    const r = await call('POST', `/api/employees/${ids[code]}/activate`, { token: admin, body: { status: 'Active', reason: 'CMP synthetic seed' } });
    if (![200, 409, 422].includes(r.status)) throw new Error(`[cmp-seed] activate ${code}: HTTP ${r.status} ${r.text.slice(0, 300)}`);
  }

  // The line manager's login, through the invitation flow (the only link between a login and a person).
  if (!(await login(users.manager, PASSWORD, slug))) {
    const invite = ok(await call('POST', '/api/access/employee-logins/invite', {
      token: admin, companyId, body: { employeeId: ids['CMP-E1'], email: users.manager, accessMode: 'FullPortal', roles: ['Manager'] },
    }), 'invite manager', [200, 201]);
    const token = decodeURIComponent(String(invite.body.invitationUrl ?? '').split('#token=')[1] ?? '');
    if (!token) throw new Error('[cmp-seed] the invitation came back without a link.');
    ok(await call('POST', '/api/auth/accept-invitation', { body: { invitationToken: token, newPassword: PASSWORD, tenantSlug: slug } }), 'accept invitation', [200, 204]);
  }

  // Tenant B: exists only to prove it cannot see tenant A's cycle.
  const betaId = await ensureTenant(platform, 'cmp-beta', 'CMP Beta (synthetic)', users.betaAdmin);
  // The flag is ON for tenant B too, so the isolation probe meets the tenant filter (404), not the feature gate (403).
  ok(await call('PUT', `/api/platform/tenants/${betaId}/features/compensation`, { token: platform, body: { isEnabled: true } }), 'enable compensation (beta)');

  const world: CmpWorld = { slug, betaSlug: 'cmp-beta', password: PASSWORD, companyId, engDepartmentId: eng, opsDepartmentId: ops, gradeId, designationId, users, employees: ids };
  await mkdir(dirname(CMP_WORLD), { recursive: true });
  await writeFile(CMP_WORLD, JSON.stringify(world, null, 2));
  console.log(`[cmp-seed] ready: ${JSON.stringify({ slug, companyId, employees: ids })}`);
});
