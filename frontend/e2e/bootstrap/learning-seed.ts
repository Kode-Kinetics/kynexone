/**
 * LRN module seed — the learning scenario on top of the fixture world (run e2e/bootstrap/provision.ts first).
 *
 * API-only and idempotent, like provision.ts: every step reads before it writes, nothing is deleted, and it refuses
 * any target that is not a disposable stack. It deliberately creates NO learning content: the HR journey creates its
 * courses, path and assignments through the UI, which is what is being proven. What it adds is the world those
 * screens need that the general fixture does not have:
 *
 *   • the `learning` opt-in flag ON for IntelliFlow (tenant A) and Ras Al-Manar (tenant B, for denial checks);
 *   • a second department (Quality, LRN-QA) in IntelliFlow with three active employees, so assignments span two
 *     departments and a manager's report has people outside their team to NOT see;
 *   • a manager login linked to IntelliFlow's head of Operations (INTELLIFLOW-KSA-E1), whom every other Operations
 *     employee reports to — so the manager report is scoped by a real reporting line.
 *
 * Deterministic: fixed codes, names and e-mail; the same run twice yields the same world.
 */
import { assertDisposableHost } from '../disposable-host.guard';
import { collectAllPages, pageItems, pageTotal } from '../paging';
import {
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_EMP2, INTELLIFLOW_HR_MGR, INTELLIFLOW_SLUG, PLATFORM_EMAIL, PLATFORM_PASSWORD,
  RASALMANAR_ADMIN, RASALMANAR_SLUG,
} from '../world';

const API = (process.env.E2E_API_BASE_URL ?? 'http://localhost:5117').replace(/\/$/, '');
const COMPANY_CODE = 'INTELLIFLOW-KSA';
export const LEARNING_MANAGER = {
  email: 'lrn.manager@intelliflow.com',
  password: process.env.E2E_LEARNING_MANAGER_PASSWORD ?? 'LrnManager@2026!x',
  role: 'Manager',
};
const QUALITY = { code: 'LRN-QA', nameEn: 'Quality', nameAr: 'الجودة' };
const QUALITY_PEOPLE = ['Huda Quality Lead', 'Yusuf Quality Analyst', 'Reem Quality Analyst'];

export interface LearningWorld {
  apiBase: string;
  tenantA: { id: string; slug: string; companyId: string };
  tenantB: { id: string; slug: string };
  departments: { operations: string; quality: string };
  employees: {
    ops1: { id: number; name: string; code: string; login: string };
    ops2: { id: number; name: string; code: string; login: string };
    quality: Array<{ id: number; name: string; code: string }>;
    managerHead: { id: number; name: string; code: string };
  };
  logins: { hr: { email: string; password: string }; manager: typeof LEARNING_MANAGER; ops1: { email: string; password: string };
    ops2: { email: string; password: string }; tenantBAdmin: { email: string; password: string } };
}

interface Res { status: number; body: any; text: string }

async function call(method: string, path: string, opts: { token?: string; body?: unknown; companyId?: string } = {}): Promise<Res> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  if (opts.companyId) headers['X-Company-Id'] = opts.companyId;
  const response = await fetch(`${API}${path}`, { method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body) });
  const text = await response.text();
  let body: any = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* kept in text */ }
  return { status: response.status, body, text };
}

function ok(res: Res, what: string, accept = [200, 201, 204]): Res {
  if (!accept.includes(res.status)) throw new Error(`[learning-seed] ${what} failed: HTTP ${res.status}\n${res.text.slice(0, 500)}`);
  return res;
}

export async function login(email: string, password: string, tenantSlug: string): Promise<string> {
  const res = ok(await call('POST', '/api/auth/login', { body: { email, password, tenantSlug } }), `sign in as ${email}`);
  return (res.body?.accessToken ?? res.body?.token) as string;
}

async function employees(token: string, companyId: string, search?: string): Promise<any[]> {
  return collectAllPages(async (page, pageSize) => {
    const qs = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (search) qs.set('search', search);
    const res = ok(await call('GET', `/api/employees?${qs}`, { token, companyId }), 'list employees');
    return { items: pageItems(res.body), total: pageTotal(res.body) };
  });
}

let seq = 9000;
const idRecord = (fieldKey: string, fieldLabel: string, fieldValue: string) => {
  seq++;
  return { countryCode: 'SA', fieldKey, fieldLabel, fieldValue, isSensitive: true, isRequired: true };
};

function saudiIban(seed: number): string {
  const bban = `80${String(seed).padStart(18, '0')}`.slice(-20);
  let remainder = 0;
  for (const ch of `${bban}281000`) remainder = (remainder * 10 + Number(ch)) % 97;
  return `SA${String(98 - remainder).padStart(2, '0')}${bban}`;
}

export async function seedLearningWorld(): Promise<LearningWorld> {
  assertDisposableHost(API, 'seed the learning scenario');

  // 1. The opt-in flag, for tenant A and tenant B — switched on through the platform API only when a tenant's own
  //    feature list still reports it off (the platform login is rate limited per account; re-runs need not spend it).
  const admin = await login(INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
  const adminB = await login(RASALMANAR_ADMIN.email, RASALMANAR_ADMIN.password, RASALMANAR_SLUG);
  const learningOff = async (tok: string) =>
    (ok(await call('GET', '/api/features/disabled-keys', { token: tok }), 'read feature flags').body as string[]).includes('learning');
  const meA = ok(await call('GET', '/api/auth/me', { token: admin }), 'who am I (A)').body;
  const meB = ok(await call('GET', '/api/auth/me', { token: adminB }), 'who am I (B)').body;
  const tenantA = { id: String(meA.tenantId), slug: INTELLIFLOW_SLUG };
  const tenantB = { id: String(meB.tenantId), slug: RASALMANAR_SLUG };
  const needsFlag = [...(await learningOff(admin) ? [tenantA] : []), ...(await learningOff(adminB) ? [tenantB] : [])];
  if (needsFlag.length > 0) {
    let platformRes = await call('POST', '/api/platform/auth/login', { body: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD } });
    for (let tries = 0; platformRes.status === 429 && tries < 3; tries++) {
      await new Promise((r) => setTimeout(r, 61_000));
      platformRes = await call('POST', '/api/platform/auth/login', { body: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD } });
    }
    const platformToken = ok(platformRes, 'platform login').body?.token ?? platformRes.body?.accessToken;
    for (const t of needsFlag) {
      ok(await call('PUT', `/api/platform/tenants/${t.id}/features/learning`, { token: platformToken, body: { isEnabled: true } }), `enable learning on ${t.slug}`);
    }
  }

  // 2. IntelliFlow: its company and Operations department (from provision.ts).
  const companies = pageItems(ok(await call('GET', '/api/companies?page=1&pageSize=100', { token: admin }), 'list companies').body);
  const company = companies.find((c: any) => c.legalNameEn === COMPANY_CODE) ?? companies[0];
  const departments = pageItems(ok(await call('GET', '/api/departments?page=1&pageSize=100', { token: admin, companyId: company.id }), 'list departments').body);
  const operations = departments.find((d: any) => d.code === `${COMPANY_CODE}-OPS`);
  if (!operations) throw new Error('[learning-seed] The Operations department from provision.ts is missing.');

  // 3. The Quality department and its three people.
  let quality = departments.find((d: any) => d.code === QUALITY.code);
  if (!quality) {
    quality = ok(await call('POST', '/api/departments', {
      token: admin, companyId: company.id, body: { code: QUALITY.code, nameEn: QUALITY.nameEn, nameAr: QUALITY.nameAr, isActive: true },
    }), 'create the Quality department', [200, 201]).body;
  }
  const designations = pageItems(ok(await call('GET', '/api/designations?page=1&pageSize=100', { token: admin, companyId: company.id }), 'list designations').body);
  let analyst = designations.find((d: any) => d.code === `${QUALITY.code}-AN`);
  if (!analyst) {
    analyst = ok(await call('POST', '/api/designations', {
      token: admin, companyId: company.id,
      body: { departmentId: quality.id, code: `${QUALITY.code}-AN`, titleEn: 'Quality Analyst', titleAr: 'محلل جودة', isActive: true },
    }), 'create the Quality Analyst designation', [200, 201]).body;
  }

  const existing = await employees(admin, company.id, `${QUALITY.code}-E`);
  let lead: number | null = Number(existing.find((e: any) => e.employeeCode === `${QUALITY.code}-E1`)?.id) || null;
  for (const [index, name] of QUALITY_PEOPLE.entries()) {
    const code = `${QUALITY.code}-E${index + 1}`;
    if (existing.some((e: any) => e.employeeCode === code)) continue;
    const created = ok(await call('POST', '/api/employees', {
      token: admin, companyId: company.id,
      body: {
        employeeCode: code, manualEmployeeCode: true, englishName: name, gender: index % 2 ? 'Male' : 'Female', dateOfBirth: '1991-03-10',
        nationality: 'Saudi', personalEmail: `${code.toLowerCase()}@intelliflow.local`, companyId: company.id, departmentId: quality.id,
        designationId: analyst.id, reportingManagerEmployeeId: index === 0 ? null : lead, jobTitle: index === 0 ? 'Quality Lead' : 'Quality Analyst',
        employmentType: 'FullTime', contractType: 'Unlimited', joiningDate: '2025-01-01T00:00:00Z',
        payrollProfile: {
          bankName: 'Al Rajhi Bank', iban: saudiIban(7080101675 + index), accountNumber: String(7080101675 + index), paymentMethod: 'BankTransfer',
          salaryCurrency: 'SAR', wpsEligible: true, eosbEligible: true, socialInsuranceReference: `SI-${code}`, molId: `MOL-${code}`,
        },
        salaryBreakdown: { basicSalary: 7000, housingAllowance: 1750, transportAllowance: 500, effectiveDate: '2025-01-01', currency: 'SAR' },
        complianceRecords: [
          idRecord('civil_id', 'Civil ID', `1${String(210000000 + seq).slice(0, 9)}`),
          idRecord('id_number', 'Government ID number', `1${String(310000000 + seq).slice(0, 9)}`),
          idRecord('gosi_reference', 'GOSI reference', `GOSI-${code}`),
          idRecord('iqama_number', 'Iqama Number', `2${String(410000000 + seq).slice(0, 9)}`),
          idRecord('emirates_id', 'Emirates ID', `784-1991-${String(1100000 + seq).slice(0, 7)}-1`),
          idRecord('work_permit', 'Work permit number', `WP-${code}`),
          idRecord('passport_number', 'Passport number', `P${String(11000000 + seq).slice(0, 8)}`),
          idRecord('qid', 'Qatar ID', `2${String(8100000 + seq).slice(0, 10)}`),
          idRecord('residency_number', 'Residency number', `RES-${code}`),
          idRecord('labor_card_number', 'Labour card number', `LC-${code}`),
          idRecord('muqeem_reference', 'Muqeem reference', `MQ-${code}`),
          idRecord('qiwa_contract_reference', 'Qiwa contract reference', `QC-${code}`),
        ],
        acknowledgeDuplicate: true,
      },
    }), `create employee ${code}`, [200, 201]);
    if (index === 0) lead = Number(created.body?.id ?? created.body?.Id);
  }
  for (const e of await employees(admin, company.id, `${QUALITY.code}-E`)) {
    if (e.status === 'Active') continue;
    ok(await call('POST', `/api/employees/${e.id}/activate`, { token: admin, body: { status: 'Active', reason: 'learning e2e scenario' } }), `activate ${e.employeeCode}`);
  }

  // 4. A manager login for the head of Operations (everyone else in Operations reports to them).
  const all = await employees(admin, company.id);
  const byCode = (code: string) => {
    const e = all.find((x: any) => x.employeeCode === code);
    if (!e) throw new Error(`[learning-seed] Employee ${code} is missing.`);
    return e;
  };
  const head = byCode(`${COMPANY_CODE}-E1`);
  const managerSignsIn = await call('POST', '/api/auth/login', { body: { email: LEARNING_MANAGER.email, password: LEARNING_MANAGER.password, tenantSlug: INTELLIFLOW_SLUG } });
  if (managerSignsIn.status !== 200) {
    // An invitation goes only to the employee's work email, so the head gets one first (the product's backfill path).
    ok(await call('POST', '/api/employee-access/work-emails', {
      token: admin, companyId: company.id, body: { rows: [{ employeeCode: head.employeeCode, workEmail: LEARNING_MANAGER.email }], dryRun: false },
    }), 'set the Operations head\'s work email');
    const invite = ok(await call('POST', '/api/access/employee-logins/invite', {
      token: admin, companyId: company.id,
      body: { employeeId: head.id, email: LEARNING_MANAGER.email, accessMode: 'FullPortal', roles: [LEARNING_MANAGER.role], confirmedWorkEmail: true },
    }), 'invite the Operations head as a manager', [200, 201]);
    const invitationToken = decodeURIComponent(String(invite.body.invitationUrl ?? '').split('#token=')[1] ?? '');
    if (!invitationToken) throw new Error('[learning-seed] The manager invitation came back without a link.');
    ok(await call('POST', '/api/auth/accept-invitation', {
      body: { invitationToken, newPassword: LEARNING_MANAGER.password, tenantSlug: INTELLIFLOW_SLUG },
    }), 'accept the manager invitation', [200, 204]);
  }

  const ops1 = byCode(`${COMPANY_CODE}-E2`);
  const ops2 = byCode(`${COMPANY_CODE}-E3`);
  const qualityPeople = (await employees(admin, company.id, `${QUALITY.code}-E`))
    .sort((a: any, b: any) => String(a.employeeCode).localeCompare(String(b.employeeCode)));
  return {
    apiBase: API,
    tenantA: { id: tenantA.id, slug: INTELLIFLOW_SLUG, companyId: company.id },
    tenantB: { id: tenantB.id, slug: RASALMANAR_SLUG },
    departments: { operations: operations.id, quality: quality.id },
    employees: {
      ops1: { id: Number(ops1.id), name: ops1.fullName ?? ops1.englishName, code: ops1.employeeCode, login: INTELLIFLOW_EMP1.email },
      ops2: { id: Number(ops2.id), name: ops2.fullName ?? ops2.englishName, code: ops2.employeeCode, login: INTELLIFLOW_EMP2.email },
      quality: qualityPeople.map((e: any) => ({ id: Number(e.id), name: e.fullName ?? e.englishName, code: e.employeeCode })),
      managerHead: { id: Number(head.id), name: head.fullName ?? head.englishName, code: head.employeeCode },
    },
    logins: {
      hr: { email: INTELLIFLOW_HR_MGR.email, password: INTELLIFLOW_HR_MGR.password },
      manager: LEARNING_MANAGER,
      ops1: { email: INTELLIFLOW_EMP1.email, password: INTELLIFLOW_EMP1.password },
      ops2: { email: INTELLIFLOW_EMP2.email, password: INTELLIFLOW_EMP2.password },
      tenantBAdmin: { email: RASALMANAR_ADMIN.email, password: RASALMANAR_ADMIN.password },
    },
  };
}
