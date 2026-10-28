import { expect, type Page } from '@playwright/test';
import realEmployeeDraft from '../../unit/fixtures/hire-real/employee-draft.json';
import nationalitiesFixture from '../../unit/fixtures/reference-nationalities.json';

/**
 * GET /api/employees/{id} for a draft, exactly as the API sends it (captured by the backend test HireDraftResumeContractTests;
 * `joiningDate` is `2026-11-01T00:00:00Z`, `nationalityCode` is present). Tests override only what they are about.
 */
export function realDraft(over: Record<string, any> = {}): Record<string, any> {
  return { ...realEmployeeDraft, companyId: 'company-hire', ...over };
}

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
  duplicate?: { status: string; matchType?: 'strong' | 'probable' | 'possible'; canView?: boolean; signals?: string[] };
  /** The first create answers 409 warnings_require_acknowledgement. */
  warnFirstCreate?: boolean;
  /** The warnings that 409 carries (default: joining_backdated, the code the API sends). */
  warnings?: Array<{ code: string; field: string; requiresPermission?: boolean }>;
  /** The first create answers 400 validation_failed with these field codes. */
  createErrors?: Record<string, string[]>;
  /** Company country (default SA). */
  companyCountry?: string;
  /** What GET /api/employees/:id answers for a resumed draft: an object, or a status code. */
  draftDetail?: Record<string, any> | 404 | 403;
  /** What hire-review answers. */
  review?: { blockers?: any[]; warnings?: any[]; notProvided?: any[] };
  /** Every hire-draft PUT answers 409 draft_changed_elsewhere. */
  conflictOnPut?: boolean;
  /** The first hire-draft PUT answers 409 warnings_require_acknowledgement (a client that still receives one must not treat it as a conflict). */
  warnOnFirstPut?: boolean;
  /** Hold the create response until released (double-click test). */
  holdCreate?: Promise<void>;
  permissions?: string[];
  /** Arabic name suggestions the transliterate endpoint knows, by the English text sent. Any other name answers requiresManualEntry. */
  transliterations?: Record<string, string>;
  /** P3: the company's hire templates. Omitted = the picker is not shown (the endpoint answers an empty list). */
  hireTemplates?: Array<Record<string, any>>;
  /** The template the server suggests for the chosen designation. */
  suggestedTemplateId?: string;
}

export const hireTemplateFixture = {
  id: 'tpl-cashier', companyId: 'company-hire', name: 'Branch cashier', nameAr: 'أمين صندوق فرع', isActive: true, isDeleted: false, essCompletionSections: ['id', 'bank'],
  matchDesignationId: 'desig-1', checklistId: 'cl-1', checklistTaskCount: 4, checklistBlockingCount: 1,
  defaults: { departmentId: 'dept-1', employmentType: 'Full-time', contractType: 'Permanent', probationDays: 90, workLocation: 'Riyadh branch' },
};
/** What checklist-preview answers: an Iqama task only for a hire who is not Saudi. */
export function checklistPreviewFor(nationality: string | null) {
  const tasks = [
    { templateTaskId: 't1', title: 'Create the company mailbox', titleAr: 'إنشاء البريد الإلكتروني للشركة', ownerRole: 'IT', dueDate: null, blocksActivation: false },
    { templateTaskId: 't2', title: 'Register with GOSI', titleAr: 'التسجيل في التأمينات', ownerRole: 'Gov', dueDate: null, blocksActivation: true },
    ...(nationality && nationality !== 'SA' ? [{ templateTaskId: 't3', title: 'Issue the Iqama through Muqeem', titleAr: 'إصدار الإقامة عبر مقيم', ownerRole: 'Gov', dueDate: null, blocksActivation: false }] : []),
  ];
  return { tasks, blockingCount: tasks.filter(x => x.blocksActivation).length };
}

export async function bootHire(page: Page, options: HireMockOptions = {}, path = '/people/new') {
  const calls: string[] = [];
  const creates: Array<{ body: Record<string, any>; key: string | undefined }> = [];
  const puts: Array<Record<string, any>> = [];
  const dupChecks: Array<Record<string, any>> = [];
  const catalogCalls: string[] = [];
  const rehires: Array<Record<string, any>> = [];
  const discards: string[] = [];
  const gets: string[] = [];
  const errors: string[] = [];
  const translits: Array<Record<string, any>> = [];
  let seq = 0;
  const co = options.companyCountry ? { ...company, countryCode: options.companyCountry, jurisdiction: options.companyCountry } : company;
  const companies = options.multipleCompanies ? [co, { ...co, id: 'company-other', legalNameEn: 'Other Test Company' }] : [co];
  const saBanks = [{ code: '80', nameEn: 'Al Rajhi Bank', nameAr: 'مصرف الراجحي' }, { code: '10', nameEn: 'The National Commercial Bank', nameAr: 'البنك الأهلي التجاري' }];
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
    if (p === '/api/departments') return reply(paged([{ id: 'dept-1', branchId: 'branch-1', code: 'OPS', nameEn: 'Operations', isActive: true }, { id: 'dept-2', branchId: 'branch-1', code: 'FIN', nameEn: 'Finance', isActive: true }]));
    if (p === '/api/designations') return reply(paged([
      { id: 'desig-1', code: 'SPEC', titleEn: 'Specialist', gradeId: grade.id, isActive: true, companyId: company.id },
      { id: 'desig-2', code: 'FREE', titleEn: 'Associate', isActive: true, companyId: company.id },
    ]));
    if (p === '/api/grades') return reply(paged([grade, grade2]));
    if (p === '/api/organization/cost-centers') return reply(paged([]));
    if (p === '/api/admin/master-data/values') return reply([]);
    // The real endpoint's rows ({ iso2, nameEn, nameAr, class }), not a shape invented for the mock.
    if (p === '/api/reference/nationalities') return reply(nationalitiesFixture);
    if (p === '/api/reference/sa-banks') return reply(saBanks);
    if (p === '/api/employees/field-catalog') { catalogCalls.push(url.search); return reply({ countryCode: 'SA', fields: [] }); }
    if (p === '/api/employees/derive-work-email') return reply({ domain: 'example.test', pattern: 'first.last', localPart: 'alex.morgan', workEmail: 'alex.morgan@example.test', unique: true, suggestion: 'alex.morgan', status: 'derived' });
    if (p === '/api/employees/duplicate-check') {
      dupChecks.push(body);
      const d = options.duplicate;
      return reply({ hasStrong: d?.matchType === 'strong', hasProbable: !!d, matches: d ? [d.canView === false ? { employeeId: 45, employeeCode: '', fullName: '', status: '', matchType: 'probable', signals: [], canView: false } : { employeeId: 44, employeeCode: 'TEST-044', fullName: 'Alex Morgan', status: d.status, matchType: d.matchType ?? 'probable', signals: d.signals ?? ['Same name'], canView: true, joinedYear: 2021, companyId: company.id, branch: 'Riyadh' }] : [] });
    }
    if (p === '/api/employees' && method === 'POST') {
      const key = req.headers()['idempotency-key'];
      creates.push({ body, key });
      await options.holdCreate;
      if (options.createErrors && creates.length === 1) return reply({ title: 'Validation failed', status: 400, code: 'validation_failed', traceId: 't3', errors: options.createErrors }, 400);
      if (options.warnFirstCreate && !body.acknowledgedWarnings) {
        return reply({ title: 'Warnings', status: 409, code: 'warnings_require_acknowledgement', traceId: 't1', warnings: (options.warnings ?? [{ code: 'joining_backdated', field: 'joiningDate' }]).map(w => ({ message: 'x', requiresPermission: false, ...w })) }, 409);
      }
      const created = realDraft({ id: 901, employeeCode: 'TEST-901', fullName: body.englishName, englishName: body.englishName, updatedAtUtc: '2026-10-27T08:00:00Z' });
      if (creates.filter(c => c.key === key).length > 1) return reply(created, 200);
      return reply(created, 201);
    }
    if (/^\/api\/employees\/\d+$/.test(p) && method === 'GET') {
      gets.push(p);
      if (typeof options.draftDetail === 'number') return reply({ title: 'No', status: options.draftDetail }, options.draftDetail);
      if (options.draftDetail) return reply(options.draftDetail);
      return reply({ title: 'Not found', status: 404 }, 404);
    }
    if (p === '/api/employees' && method === 'GET') return reply(paged(url.searchParams.get('status') === 'Active' ? [{ id: 17, employeeCode: 'TEST-017', fullName: 'Morgan Reviewer', status: 'Active', companyId: company.id }] : []));
    if (/^\/api\/employees\/\d+\/hire-draft$/.test(p) && method === 'PUT') {
      puts.push(body);
      if (options.conflictOnPut) return reply({ title: 'Draft changed elsewhere.', status: 409, code: 'draft_changed_elsewhere', traceId: 't2' }, 409);
      if (options.warnOnFirstPut && puts.length === 1) return reply({ title: 'Acknowledgement required.', status: 409, code: 'warnings_require_acknowledgement', traceId: 't4', warnings: [{ code: 'joining_backdated', field: 'joiningDate', message: 'Joining date is more than 30 days in the past.', requiresPermission: false }] }, 409);
      seq++;
      return reply({ updatedAtUtc: `2026-10-27T08:00:0${seq}Z`, readiness: { state: 'Draft', blockers: 0 } });
    }
    if (/^\/api\/employees\/\d+\/hire-review$/.test(p)) return reply({ blockers: options.review?.blockers ?? [{ key: 'IqamaNumber', label: 'Identity number missing' }, { key: 'pay:compensationUnapproved', label: 'Pay package awaiting Finance approval' }], warnings: options.review?.warnings ?? [], notProvided: options.review?.notProvided ?? [{ key: 'arabicName', label: 'Arabic name' }], essSections: [] });
    if (/^\/api\/employees\/\d+\/rehire$/.test(p)) { rehires.push(body); return reply({ id: 44, status: 'Draft', fullName: 'Alex Morgan', updatedAtUtc: '2026-10-27T09:00:00Z' }); }
    if (/^\/api\/employees\/\d+\/draft$/.test(p) && method === 'DELETE') { discards.push(p); return route.fulfill({ status: 204 }); }
    if (p.endsWith('/pay-scale')) return reply([]);
    if (p === '/api/onboarding/hire-templates') return reply(options.hireTemplates ?? []);
    if (p === '/api/onboarding/hire-templates/resolve') return reply({ candidates: options.hireTemplates ?? [], suggestedId: options.suggestedTemplateId ?? null });
    if (/^\/api\/onboarding\/hire-templates\/[^/]+\/checklist-preview$/.test(p)) return reply(checklistPreviewFor(url.searchParams.get('nationality')));
    if (p === '/api/compensation/benefits/grade-defaults') return reply([]);
    if (p === '/api/localization/transliterate') {
      translits.push(body);
      const known = options.transliterations?.[String(body?.text)];
      if (known) return reply({ suggestion: known, requiresManualEntry: false });
      if (options.transliterations) return reply({ suggestion: '', requiresManualEntry: true });
      return reply({ suggestion: 'أليكس مورغان', requiresManualEntry: false });
    }
    return reply(p.includes('/features/') || p === '/api/notifications' ? [] : paged([]));
  });
  await page.goto(path);
  return { calls, gets, translits, creates, puts, dupChecks, catalogCalls, rehires, discards, errors, apiCount: () => calls.filter(c => !/auth|features|localization|notifications/.test(c)).length };
}

export async function expectNoHorizontalScroll(page: Page) {
  const ok = await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1);
  expect(ok).toBe(true);
}
