import type { Page, Route } from '@playwright/test';

/**
 * Route-mocked People module for the list-scroll and link-crawl specs. Every /api/** call is answered here; nothing
 * reaches a backend. The mock RECORDS what it saw so a spec can assert on it:
 *   - `unmocked`  GET paths that fell through to the generic empty-list reply (a screen asked for data nobody mocked)
 *   - `failed`    requests answered with a 4xx/5xx
 *   - `writes`    every non-GET
 *   - `errors`    page errors and console errors (browser noise from the mocked API's own 4xx is filtered)
 */

export type Locale = 'en' | 'ar';
export type Theme = 'light' | 'dark';

export interface PeopleFixtureOptions {
  /** Number of employees (default 25: exactly one 25-row page). */
  count?: number;
  locale?: Locale;
  theme?: Theme;
  permissions?: string[];
  /** Force a status for GET /api/employees (network/5xx unhappy path). */
  listFailure?: 'network' | 500;
}

export const ALL_PERMISSIONS = [
  'employees.read', 'employees.write', 'employees.sensitive', 'employees.documents', 'employees.approve', 'employees.delete',
  'employees.access.issue', 'employees.access.reset', 'employees.bulk_import', 'dashboard.read', 'manager.approve',
];

const DEPARTMENTS = ['Sales', 'Finance', 'Operations', 'Human Resources', 'Information Technology', 'Legal'];
const BRANCHES = ['Riyadh HQ', 'Jeddah', 'Dammam'];
const DESIGNATIONS = ['Senior Account Executive', 'Finance Analyst', 'Operations Lead', 'HR Business Partner', 'Platform Engineer', 'Legal Counsel'];

/** Access states in rotation, so every row shape (3 chips, "Reset sign-in" button, "Needs work email first") appears. */
const ACCESS = ['active', 'code_given', 'not_started', 'waiting_for_work_email', 'active', 'stopped'] as const;

export function employeeFixture(index: number) {
  const id = 1000 + index;
  const state = ACCESS[index % ACCESS.length];
  const status = state === 'stopped' ? 'Resigned' : index % 7 === 3 ? 'Draft' : 'Active';
  const readinessState = status === 'Draft' ? 'Blocked' : index % 5 === 2 ? 'NeedsAttention' : 'Ready';
  const nameEn = `Employee ${String(index + 1).padStart(2, '0')} Al-Harbi`;
  return {
    id, publicId: `p-${id}`, employeeCode: `EMP-${String(id).padStart(4, '0')}`, fullName: nameEn, englishName: nameEn,
    arabicName: `موظف ${index + 1} الحربي`,
    department: DEPARTMENTS[index % DEPARTMENTS.length], designation: DESIGNATIONS[index % DESIGNATIONS.length],
    branch: BRANCHES[index % BRANCHES.length], status, profileCompletenessScore: 60 + (index % 5) * 8, iqamaNumber: '',
    readinessState, activationBlockersCount: readinessState === 'Ready' ? 0 : 2,
    workEmail: state === 'waiting_for_work_email' ? null : `employee${index + 1}@evostel.test`,
    accessState: state, loginEnabled: state === 'active', passwordConfigured: state === 'active',
  };
}

export function detailFixture(index: number) {
  const base = employeeFixture(index);
  return {
    ...base, preferredName: '', profilePhotoUrl: '', personalEmail: '', phone: '+966500000000', gender: 'Male', maritalStatus: 'Single',
    nationality: 'Saudi Arabia', countryCode: 'SAU', companyId: 'c1', branchId: 'b1', departmentId: 'd1', designationId: 'g1', gradeId: 'gr1',
    costCenterId: 'cc1', workLocation: 'Riyadh', managerEmployeeId: null, joiningDate: '2026-01-01', contractType: 'Unlimited', employmentType: 'Full Time',
    jobTitle: base.designation, grade: 'M1', costCenter: 'CC-1', complianceRecords: [], documents: [], history: [], transfers: [],
    createdAtUtc: '2026-01-01T00:00:00Z', effectiveDate: '2026-01-01', reason: '',
  };
}

export interface PeopleFixture {
  employeeCount: number;
  requested: string[];
  unmocked: string[];
  failed: string[];
  writes: Array<{ method: string; path: string; body: unknown }>;
  errors: string[];
  listQueries: string[];
  /** Flip at runtime to exercise the unhappy path and back (the list's Refresh must recover). */
  listFailure: 'network' | 500 | null;
}

function ids(count: number): number[] {
  return Array.from({ length: count }, (_, i) => i);
}

export async function bootPeopleFixture(page: Page, options: PeopleFixtureOptions = {}): Promise<PeopleFixture> {
  const count = options.count ?? 25;
  const permissions = options.permissions ?? ALL_PERMISSIONS;
  const everyone = ids(count).map(employeeFixture);
  const fixture: PeopleFixture = { employeeCount: count, requested: [], unmocked: [], failed: [], writes: [], errors: [], listQueries: [], listFailure: options.listFailure ?? null };

  page.on('pageerror', error => fixture.errors.push(`pageerror: ${error.message}`));
  page.on('console', message => {
    if (message.type() !== 'error') return;
    const text = message.text();
    // The browser logs every mocked 4xx/5xx as a console error; those are reported through `failed`, not twice.
    if (/Failed to load resource/.test(text)) return;
    fixture.errors.push(`console.error: ${text}`);
  });

  await page.addInitScript(({ locale, theme }) => {
    localStorage.setItem('zayra_access_token', 'people-fixture-token');
    localStorage.setItem('zayra_refresh_token', 'people-fixture-refresh');
    localStorage.setItem('kynexone.theme', theme);
    localStorage.setItem('kynexone-locale-choice-v2', locale);
    localStorage.setItem('kynexone-company:people-reviewer', 'c1');
  }, { locale: options.locale ?? 'en', theme: options.theme ?? 'light' });

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    const reply = async (body: unknown, status = 200) => {
      if (status >= 400) fixture.failed.push(`${status} ${method} ${path}`);
      await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    };
    const text = (body: string) => route.fulfill({ status: 200, contentType: 'text/csv', body });
    fixture.requested.push(`${method} ${path}`);

    if (method !== 'GET') {
      let body: unknown = null;
      try { body = request.postDataJSON(); } catch { body = request.postData(); }
      if (path === '/api/employees/derive-work-email') return reply({ domain: 'evostel.test', pattern: 'first.last', localPart: 'x', workEmail: 'x@evostel.test', unique: true, suggestion: 'x', status: 'derived', suggestedWorkEmail: 'x@evostel.test' });
      if (path === '/api/employees/duplicate-check') return reply({ matches: [] });
      fixture.writes.push({ method, path, body });
      if (path === '/api/employees/import-preview') return reply({ rows: [], summary: { total: 0 } });
      if (path === '/api/employee-access/codes') {
        const targets = (body as { employeeIds?: number[] })?.employeeIds ?? [];
        return reply({ issued: targets.map(id => ({ employeeId: id, employeeName: 'x', employeeCode: 'x', username: 'x@evostel.test', code: '12345678', delivery: 'print', printReason: 'no_email', expiresAtUtc: '2030-01-01T00:00:00Z', tenantSlug: 'evostel' })), skipped: [], emailed: false, deliveryMessage: null });
      }
      return reply({});
    }

    if (path === '/api/auth/me') return reply({
      id: 'people-reviewer', tenantId: 't1', tenantSlug: 'fixture', email: 'reviewer@evostel.test', fullName: 'People Reviewer', roles: ['Admin'],
      permissions, accountType: 'SingleCompany', isGroupScope: false,
      companies: [{ id: 'c1', name: 'Evostel', code: 'c1', countryCode: 'SAU', isActive: true }],
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: options.locale ?? 'en', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/tenant-admin/usage') return reply({ activeEmployees: count, maxEmployees: 0 });
    if (path === '/api/features/disabled-keys') return reply(['hire_wizard_v2', 'release_a']);
    if (path.startsWith('/api/features/')) return reply([]);
    if (path === '/api/notifications' || path.includes('help-text') || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/admin/system-settings' || path === '/api/country-packs/available' || path === '/api/admin/master-data/values') return reply([]);
    if (path === '/api/companies') return reply({ items: [{ id: 'c1', legalNameEn: 'Evostel', tradeName: 'Evostel', countryCode: 'SAU', emailDomain: 'evostel.test', workEmailPattern: 'first.last', isActive: true, approvalStatus: 'Active', defaultCurrency: 'SAR' }], total: 1, page: 1, pageSize: 100 });
    if (['/api/departments', '/api/branches', '/api/designations', '/api/grades', '/api/organization/cost-centers', '/api/cost-centers'].includes(path)) return reply({ items: [], total: 0, page: 1, pageSize: 100 });

    if (path === '/api/employees/export') return text('Code,Name\nEMP-1000,Employee 01\n');
    if (path === '/api/employees/import-template') return text('Code,Name\n');
    if (path === '/api/employees/field-catalog') return reply({ fields: [] });
    if (path === '/api/employee-access/summary') {
      const counts: Record<string, number> = {};
      for (const p of everyone) counts[p.accessState] = (counts[p.accessState] ?? 0) + 1;
      return reply(counts);
    }

    if (path === '/api/employees') {
      fixture.listQueries.push(url.search);
      if (fixture.listFailure === 'network') return route.abort('failed');
      if (fixture.listFailure === 500) return reply({ message: 'Fixture failure.' }, 500);
      const search = (url.searchParams.get('search') ?? '').toLowerCase();
      const access = url.searchParams.get('access');
      const status = url.searchParams.get('status');
      const readiness = url.searchParams.get('readiness');
      const pageNumber = Number(url.searchParams.get('page') ?? 1);
      const pageSize = Number(url.searchParams.get('pageSize') ?? 25);
      const matches = everyone
        .filter(e => !search || `${e.fullName} ${e.employeeCode}`.toLowerCase().includes(search))
        .filter(e => !access || access.split(',').includes(e.accessState))
        .filter(e => !status || e.status === status)
        .filter(e => !readiness || e.readinessState === readiness);
      return reply({ items: matches.slice((pageNumber - 1) * pageSize, pageNumber * pageSize), total: matches.length, page: pageNumber, pageSize });
    }

    const readinessMatch = /^\/api\/employees\/(\d+)\/readiness$/.exec(path);
    if (readinessMatch) {
      return reply({
        employeeId: Number(readinessMatch[1]), state: 'Ready', score: 100, progress: { present: 5, requiredTotal: 5 },
        policy: { countryCode: 'SAU', tier: 'Activation', sources: ['Fixture policy'] }, blocking: [], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: 'Fixture.',
      });
    }
    const accessMatch = /^\/api\/employee-access\/(\d+)$/.exec(path);
    if (accessMatch) {
      const index = Number(accessMatch[1]) - 1000;
      const e = everyone[index];
      if (!e) return reply({ message: 'Not found' }, 404);
      return reply({ employeeId: e.id, employeeName: e.fullName, employeeCode: e.employeeCode, workEmail: e.workEmail, state: e.accessState, canIssue: true, emailDelivery: false, lastCodeExpiredAtUtc: null, lastSignInAtUtc: null, codeExpiresAtUtc: null });
    }
    if (/^\/api\/employee-completion\/\d+$/.test(path)) return reply({ requested: false, status: 'NotRequested', selfServiceAvailable: true });
    const detailMatch = /^\/api\/employees\/(\d+)$/.exec(path);
    if (detailMatch) {
      const index = Number(detailMatch[1]) - 1000;
      if (index < 0 || index >= count) return reply({ message: 'Employee not found.' }, 404);
      return reply(detailFixture(index));
    }

    if (path === '/api/employees/ex-employees') {
      const items = [1, 2].map(n => ({ id: 2000 + n, employeeCode: `EMP-200${n}`, fullName: `Former Employee ${n}`, arabicName: '', department: 'Sales', designation: 'Officer', branch: 'Riyadh HQ', lastStatus: 'Resigned', isDeleted: false, exitDate: '2026-06-30', retentionUntilUtc: '2033-06-30T00:00:00Z', privacyStatus: 'Retained' }));
      return reply({ items, total: 2, page: 1, pageSize: 25 });
    }
    if (['/api/positions', '/api/hr-letters/requests'].includes(path) || /^\/api\/employees\/\d+\/signatures$/.test(path)) return reply({ items: [], total: 0, page: 1, pageSize: 100 });
    if (path === '/api/employees/org-chart' || path === '/api/hr-letters/types') return reply([]);
    if (/^\/api\/employees\/\d+\/cost-centre\/preview$/.test(path)) return reply({ costCentreId: null, code: null, nameEn: null, nameAr: null, source: 'None', sourceName: null, sourceNameAr: null, level: null, isOverride: false, invalidReason: null });
    if (path === '/api/employees/drafts') {
      const draft = (id: string, status: string, name: string, activatedEmployeeId: number | null) => ({
        id, status, currentStep: 'HR approval', name, arabicName: '', department: 'Sales', designation: 'Officer', branch: 'Riyadh HQ', joiningDate: '2026-12-01', source: 'Manual',
        applicationId: null, jobTitle: null, createdByUserId: 'hr-2', createdByName: 'Sara Ali', isMine: false, canApprove: true, approveBlockedReason: null,
        profileCompletenessScore: 90, createdAtUtc: '2026-11-01T08:00:00Z', submittedAtUtc: '2026-11-01T08:05:00Z', decidedAtUtc: null, decidedByName: null,
        decisionReason: null, activatedEmployeeCode: activatedEmployeeId ? 'EMP-1000' : null, activatedEmployeeId,
      });
      const all = [draft('d-1', 'PendingHrApproval', 'Rana Saeed', null), draft('d-2', 'Activated', 'Employee 01 Al-Harbi', 1000)];
      const wanted = url.searchParams.get('status');
      const items = wanted ? all.filter(d => d.status === wanted) : all;
      return reply({ items, total: items.length, page: 1, pageSize: 25, counts: { awaitingApproval: 1, draft: 0, activated: 1, rejected: 0, cancelled: 0 } });
    }
    if (path === '/api/hr/profile-completion/inbox') return reply([{
      requestId: 'r-1', employee: { id: 1000, code: 'EMP-1000', name: 'Employee 01 Al-Harbi' }, section: 'contact', status: 'PendingHR', submittedAtUtc: '2026-11-02T08:00:00Z',
      goesToFinance: false, diff: [{ field: 'phone', labelKey: 'Mobile number', before: '', after: '+966500000001', changed: true }], documents: [],
    }]);
    if (path === '/api/onboarding/worklist') return reply({ total: 2, items: [
      { taskId: 't-1', employee: { id: 1000, code: 'EMP-1000', name: 'Employee 01 Al-Harbi', startDate: '2026-12-01' }, title: 'Collect signed contract', titleAr: null, ownerRole: 'HR', dueDate: '2026-11-20', overdue: false, blocksActivation: true, status: 'Pending', linkSection: null, evidenceRequired: false, evidenceDocumentId: null },
      { taskId: 't-2', employee: { id: 1001, code: 'EMP-1001', name: 'Employee 02 Al-Harbi', startDate: '2026-12-01' }, title: 'Order laptop', titleAr: null, ownerRole: 'IT', dueDate: '2026-10-01', overdue: true, blocksActivation: false, status: 'Pending', linkSection: null, evidenceRequired: false, evidenceDocumentId: null },
    ] });

    // The employee Benefits panel (Pay & Benefits, panel=benefits): no plans, nothing enrolled, nothing requested.
    if (path === '/api/compensation/benefits/plans') return reply([]);
    const benefitPackage = /^\/api\/compensation\/benefits\/employees\/(\d+)\/package$/.exec(path);
    if (benefitPackage) return reply({ employeeId: Number(benefitPackage[1]), employeeName: `Employee ${Number(benefitPackage[1]) - 999}`, gradeId: null, companyId: null, asOf: '2026-11-20', enrollments: [], additionalRequests: [] });

    // Anything else: answer with an empty list (so the screen renders) but record it, so a spec can name the gap.
    fixture.unmocked.push(`${method} ${path}`);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });

  return fixture;
}
