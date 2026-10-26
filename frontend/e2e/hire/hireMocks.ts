import { expect, type Page } from '@playwright/test';

/**
 * Mocked API for the hire wizard (hire_wizard_v2). Every /api/ request is intercepted; nothing mutates a real tenant.
 * The error shapes are the ones in unit/fixtures/hire-errors (40-ARCHITECTURE section 3.1).
 */
export const company = {
  id: 'company-hire', legalNameEn: 'Hire Test Company', legalNameAr: 'شركة اختبار', tradeName: 'Hire Test Company',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', emailDomain: 'example.test', workEmailPattern: 'first.last', isActive: true, approvalStatus: 'Active',
};
export const grade = { id: 'grade-g5', code: 'G5', name: 'Professional', band: 'P', level: 5, minSalary: 5000, midSalary: 10000, maxSalary: 15000, currency: 'SAR', isActive: true };
export const grade2 = { ...grade, id: 'grade-g6', code: 'G6', name: 'Senior' };

export interface HireMockOptions {
  locale?: 'en' | 'ar';
  multipleCompanies?: boolean;
  /** Put hire_wizard_v2 in the disabled keys, as the server does for a tenant that has not been switched on. */
  flagOff?: boolean;
  /** Existing person the duplicate-check returns. */
  duplicate?: { status: string; matchType?: 'strong' | 'probable' };
  /** The first create answers 409 warnings_require_acknowledgement. */
  warnFirstCreate?: boolean;
  /** Every hire-draft PUT answers 409 draft_changed_elsewhere. */
  conflictOnPut?: boolean;
  /** Hold the create response until released (double-click test). */
  holdCreate?: Promise<void>;
  permissions?: string[];
}

export async function bootHire(page: Page, options: HireMockOptions = {}, path = '/people/new') {
  const calls: string[] = [];
  const creates: Array<{ body: Record<string, any>; key: string | undefined }> = [];
  const puts: Array<Record<string, any>> = [];
  const dupChecks: Array<Record<string, any>> = [];
  const catalogCalls: string[] = [];
  const rehires: Array<Record<string, any>> = [];
  const discards: string[] = [];
  const errors: string[] = [];
  let seq = 0;
  const companies = options.multipleCompanies ? [company, { ...company, id: 'company-other', legalNameEn: 'Other Test Company' }] : [company];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(([locale]) => {
    localStorage.setItem('zayra_access_token', 'hire-browser-fixture');
    if (locale === 'ar') localStorage.setItem('kynexone-locale-choice-v2', 'ar');
  }, [options.locale ?? 'en']);
  // The Next dev indicator is a dev-only overlay that can sit on top of the sticky action bar.
  await page.addInitScript(() => { document.addEventListener('DOMContentLoaded', () => { const st = document.createElement('style'); st.textContent = 'nextjs-portal{display:none!important}'; document.head.appendChild(st); }); });
  await page.route('**/api/**', async route => {
    const req = route.request();
    const url = new URL(req.url());
    const p = url.pathname;
    const method = req.method();
    const body = req.postData() ? req.postDataJSON() : undefined;
    const reply = (json: unknown, status = 200, headers?: Record<string, string>) => route.fulfill({ status, json, headers });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    calls.push(`${method} ${p}`);
    if (p === '/api/auth/me') return reply({
      id: 'hire-user', tenantId: 'hire-tenant', tenantSlug: 'hire-fixture', fullName: 'HR Officer', roles: ['HR Manager'],
      permissions: options.permissions ?? ['employees.read', 'employees.write', 'employees.override_validation'],
      isGroupScope: !!options.multipleCompanies, accountType: options.multipleCompanies ? 'Group' : 'SingleCompany',
      companies: companies.map(c => ({ id: c.id, name: c.legalNameEn, code: 'T', countryCode: c.countryCode, isActive: true })),
    });
    if (p === '/api/features/disabled-keys') return reply(options.flagOff ? ['release_a', 'hire_wizard_v2'] : ['release_a']);
    if (p === '/api/features/modules') return reply([]);
    if (p === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/companies') return reply(paged(companies));
    if (p === '/api/branches') return reply(paged([{ id: 'branch-1', companyId: company.id, code: 'RYD', nameEn: 'Riyadh', isActive: true }]));
    if (p === '/api/departments') return reply(paged([{ id: 'dept-1', branchId: 'branch-1', code: 'OPS', nameEn: 'Operations', isActive: true }]));
    if (p === '/api/designations') return reply(paged([
      { id: 'desig-1', code: 'SPEC', titleEn: 'Specialist', gradeId: grade.id, isActive: true, companyId: company.id },
      { id: 'desig-2', code: 'FREE', titleEn: 'Associate', isActive: true, companyId: company.id },
    ]));
    if (p === '/api/grades') return reply(paged([grade, grade2]));
    if (p === '/api/organization/cost-centers') return reply(paged([]));
    if (p === '/api/admin/master-data/values') return reply([]);
    if (p === '/api/reference/nationalities') return reply({}, 404);
    if (p === '/api/employees/field-catalog') { catalogCalls.push(url.search); return reply({ countryCode: 'SA', fields: [] }); }
    if (p === '/api/employees/derive-work-email') return reply({ domain: 'example.test', pattern: 'first.last', localPart: 'alex.morgan', workEmail: 'alex.morgan@example.test', unique: true, suggestion: 'alex.morgan', status: 'derived' });
    if (p === '/api/employees/duplicate-check') {
      dupChecks.push(body);
      const d = options.duplicate;
      return reply({ hasStrong: d?.matchType === 'strong', hasProbable: !!d, matches: d ? [{ employeeId: 44, employeeCode: 'TEST-044', fullName: 'Alex Morgan', status: d.status, matchType: d.matchType ?? 'probable', signals: ['Same name'], canView: true, joinedYear: 2021, companyId: company.id }] : [] });
    }
    if (p === '/api/employees' && method === 'POST') {
      const key = req.headers()['idempotency-key'];
      creates.push({ body, key });
      await options.holdCreate;
      if (options.warnFirstCreate && !body.acknowledgedWarnings) {
        return reply({ title: 'Warnings', status: 409, code: 'warnings_require_acknowledgement', traceId: 't1', warnings: [{ code: 'joining_backdated_over_30d', field: 'joiningDate', message: 'x', requiresPermission: false }] }, 409);
      }
      if (creates.filter(c => c.key === key).length > 1) return reply({ id: 901, fullName: body.englishName, status: 'Draft', updatedAtUtc: '2026-10-27T08:00:00Z' }, 200);
      return reply({ id: 901, employeeCode: 'TEST-901', fullName: body.englishName, englishName: body.englishName, status: 'Draft', updatedAtUtc: '2026-10-27T08:00:00Z' }, 201);
    }
    if (p === '/api/employees' && method === 'GET') return reply(paged(url.searchParams.get('status') === 'Active' ? [{ id: 17, employeeCode: 'TEST-017', fullName: 'Morgan Reviewer', status: 'Active', companyId: company.id }] : []));
    if (/^\/api\/employees\/\d+\/hire-draft$/.test(p) && method === 'PUT') {
      puts.push(body);
      if (options.conflictOnPut) return reply({ title: 'Draft changed elsewhere.', status: 409, code: 'draft_changed_elsewhere', traceId: 't2' }, 409);
      seq++;
      return reply({ updatedAtUtc: `2026-10-27T08:00:0${seq}Z`, readiness: { blockers: [], warnings: [] } });
    }
    if (/^\/api\/employees\/\d+\/hire-review$/.test(p)) return reply({ blockers: ['Identity number missing'], warnings: [], notProvided: ['arabicName'], essSections: [] });
    if (/^\/api\/employees\/\d+\/rehire$/.test(p)) { rehires.push(body); return reply({ id: 44, status: 'Draft', fullName: 'Alex Morgan', updatedAtUtc: '2026-10-27T09:00:00Z' }); }
    if (/^\/api\/employees\/\d+\/draft$/.test(p) && method === 'DELETE') { discards.push(p); return route.fulfill({ status: 204 }); }
    if (p.endsWith('/pay-scale')) return reply([]);
    if (p === '/api/compensation/benefits/grade-defaults') return reply([]);
    if (p === '/api/localization/transliterate') return reply({ suggestion: 'أليكس مورغان', requiresManualEntry: false });
    return reply(p.includes('/features/') || p === '/api/notifications' ? [] : paged([]));
  });
  await page.goto(path);
  return { calls, creates, puts, dupChecks, catalogCalls, rehires, discards, errors, apiCount: () => calls.filter(c => !/auth|features|localization|notifications/.test(c)).length };
}

export async function expectNoHorizontalScroll(page: Page) {
  const ok = await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1);
  expect(ok).toBe(true);
}
