import { expect, test, type Page, type Request } from '@playwright/test';
import fs from 'node:fs';

/**
 * Launch-hiring forms against a mocked API (nothing is read or written):
 *   - the requisition and opening forms pick department, designation and grade from the catalogue (ids go to the API, not typed text)
 *   - an opening made from a requisition starts with its budget range and skills
 *   - the offer form leaves the basic salary for HR to enter and warns, never blocks, when it is outside the grade's band
 *   - moving to Offer with no interview or assessment asks for a reason
 * Run: npx playwright test -c e2e/playwright.launch-hiring.config.ts   (LAUNCH_HIRING_SHOTS=/tmp/claude/demo/launch-hiring to save screenshots)
 */

const PLACEMENT = {
  departments: [{ id: 'd-fin', name: 'Finance', code: 'FIN' }, { id: 'd-sales', name: 'Sales', code: 'SAL' }],
  designations: [{ id: 'g-acc', name: 'Accountant', code: 'ACC' }, { id: 'g-se', name: 'Sales Executive', code: 'SE' }],
  grades: [{ id: 'gr-4', name: 'Grade 4', code: 'G4', minSalary: 8000, midSalary: 10000, maxSalary: 12000, currency: 'SAR' }],
};
const OPENING = { id: 'op-1', jobCode: 'JOB-1', requisitionId: null, title: 'Accountant', departmentId: 'd-fin', departmentName: 'Finance', designationId: 'g-acc', designationTitle: 'Accountant', gradeId: 'gr-4', employmentType: 'Full-Time', headCount: 1, filledCount: 0, description: '', requirements: '', responsibilities: '', salaryFrom: 9000, salaryTo: 11000, location: 'Riyadh', status: 'Open', assignedHrName: '', createdAtUtc: '2026-11-01T08:00:00Z', activeApplications: 1, remaining: 1 };
const REQUISITION = { id: 'rq-1', requisitionNumber: 'MRQ-2026-0001', departmentId: 'd-fin', departmentName: 'Finance', designationId: 'g-acc', designationTitle: 'Accountant', gradeId: 'gr-4', headCount: 2, employmentType: 'Full-Time', priority: 'High', justification: 'Growth', requiredSkills: 'IFRS\nExcel', minExperienceYears: 3, maxExperienceYears: null, budgetFrom: 9000, budgetTo: 11000, targetJoiningDate: null, status: 'Approved', requestedByName: 'HR', requestedByUserId: 'u9', rejectionReason: '', createdAtUtc: '2026-11-01T08:00:00Z', submittedAtUtc: null, approvedAtUtc: null };
const app = (over: Record<string, unknown> = {}) => ({ id: 'app-1', version: 3, jobOpeningId: 'op-1', jobTitle: 'Accountant', candidateId: 'c-1', candidateName: 'Nora Alharbi', candidateEmail: 'nora@example.invalid', stage: 'Offer', stageOrder: 5, status: 'Active', rejectionReason: '', offeredSalary: null, appliedAtUtc: '2026-11-01T08:00:00Z', stageChangedAtUtc: null, hiredAtUtc: null, onboardingDraftId: null, ...over });

async function boot(page: Page, opts: { locale: 'en' | 'ar'; appStage?: string; advanceFails?: boolean }) {
  const posts: { path: string; body: any }[] = [];
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(locale => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', locale); }, opts.locale);
  await page.route('**/api/**', async route => {
    const request: Request = route.request();
    const path = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (request.method() !== 'GET') posts.push({ path, body: request.postDataJSON() });
    if (path === '/api/auth/me') return json({ id: 'u1', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Recruiter Test', roles: ['Admin'], permissions: ['recruitment.read', 'recruitment.write', 'recruitment.approve', 'employees.read', 'dashboard.read'], companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] });
    if (path === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/notifications') return json([]);
    if (path === '/api/recruitment/offers/placement-options') return json(PLACEMENT);
    if (path === '/api/recruitment/requisitions' && request.method() === 'GET') return json({ items: [REQUISITION], total: 1, page: 1, pageSize: 25 });
    if (path === '/api/recruitment/requisitions' && request.method() === 'POST') return json({ ...REQUISITION, id: 'rq-9' }, 201);
    if (path === '/api/recruitment/openings' && request.method() === 'POST') return json({ ...OPENING, id: 'op-9' }, 201);
    if (path === '/api/recruitment/openings') return json({ items: [OPENING], total: 1, page: 1, pageSize: 50 });
    if (path === '/api/recruitment/openings/op-1') return json({ opening: OPENING, stageCounts: [] });
    if (path === '/api/recruitment/applications/kanban/op-1') return json({ stages: ['Applied', 'Screening', 'Assessment', 'Interview', 'Offer', 'Hired'].map((stage, order) => ({ stage, order, applications: stage === (opts.appStage ?? 'Offer') ? [app({ stage: opts.appStage ?? 'Offer' })] : [] })), rejected: [] });
    if (path === '/api/recruitment/applications/app-1/advance') {
      return opts.advanceFails && !request.postDataJSON()?.overrideReason
        ? json({ error: 'offer_stage_needs_evaluation', message: 'x' }, 422)
        : json(app({ stage: 'Offer' }));
    }
    if (path === '/api/recruitment/applications/app-1') return json({ application: app({ stage: opts.appStage ?? 'Offer' }), candidate: { id: 'c-1', email: 'nora@example.invalid', phone: '+966 50 123 4567', totalExperienceYears: 4, source: 'Direct' }, events: [], interviews: [], offer: null });
    if (path === '/api/recruitment/applications') return json({ items: [app()], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/recruitment/offer-templates/defaults') return json({ design: { schemaVersion: 2 }, tokens: [] });
    if (path === '/api/recruitment/offer-templates') return json([]);
    if (path.startsWith('/api/recruitment/')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto('/recruitment');
  return { posts, errors };
}

async function shot(page: Page, name: string, locale: string) {
  const dir = process.env.LAUNCH_HIRING_SHOTS;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/${name}-${locale}.png` });
}

const openTab = (page: Page, name: RegExp) => page.getByRole('button', { name }).first().click();

for (const locale of ['en', 'ar'] as const) {
  const L = locale === 'en'
    ? { requisitions: /^Requisitions/, openings: /^Job Openings/, newReq: /New Requisition/, dept: 'Department *', desig: 'Designation *', grade: 'Grade' }
    : { requisitions: /^طلبات/, openings: /^الوظائف الشاغرة/, newReq: /طلب توظيف جديد/, dept: 'القسم *', desig: 'المسمى الوظيفي *', grade: 'الدرجة' };

  test.describe(`launch hiring forms (${locale})`, () => {
    test('requisition form picks department, designation and grade from the catalogue and sends ids', async ({ page }) => {
      const { posts, errors } = await boot(page, { locale });
      await openTab(page, L.requisitions);
      await page.getByRole('button', { name: L.newReq }).first().click();
      const dept = page.getByLabel(L.dept);
      await expect(dept).toBeVisible();
      await expect(dept.locator('option')).toHaveCount(3); // placeholder + 2 catalogue records
      await expect(page.getByLabel(L.grade, { exact: true })).toBeVisible();
      await dept.selectOption('d-fin');
      await page.getByLabel(L.desig).selectOption('g-acc');
      await page.getByLabel(L.grade, { exact: true }).selectOption('gr-4');
      await page.locator('textarea').first().fill('Growth');
      await shot(page, 'requisition-form', locale);
      await page.locator('.btn-primary').last().click();
      await expect.poll(() => posts.find(p => p.path === '/api/recruitment/requisitions')?.body).toMatchObject({ departmentId: 'd-fin', designationId: 'g-acc', gradeId: 'gr-4', departmentName: 'Finance', designationTitle: 'Accountant' });
      expect(errors).toEqual([]);
    });

    test('an opening started from a requisition carries its budget range and skills, and sends ids', async ({ page }) => {
      const { posts } = await boot(page, { locale });
      await openTab(page, L.requisitions);
      // The approved requisition's row action opens the opening form.
      await page.locator('table tbody tr button.btn-primary, table tbody tr button').filter({ hasText: /Open Job|فتح وظيفة|فتح الوظيفة/ }).first().click();
      await expect(page.getByLabel(/^Salary from|^الراتب من/)).toHaveValue('9000');
      await expect(page.getByLabel(/^Salary to|^الراتب إلى/)).toHaveValue('11000');
      await expect(page.locator('textarea').filter({ hasText: 'IFRS' })).toHaveCount(1);
      await shot(page, 'opening-from-requisition', locale);
      await page.locator('.btn-primary').last().click();
      await expect.poll(() => posts.find(p => p.path === '/api/recruitment/openings')?.body).toMatchObject({ requisitionId: 'rq-1', departmentId: 'd-fin', designationId: 'g-acc', gradeId: 'gr-4', salaryFrom: 9000, salaryTo: 11000 });
    });

    test('the offer form leaves the basic salary empty and warns, never blocks, outside the grade band', async ({ page }) => {
      await boot(page, { locale });
      await openTab(page, L.openings);
      await page.getByText('Accountant').first().click();
      await page.getByText('Nora Alharbi').first().click();
      await page.getByRole('button', { name: /^Offer$|^العرض$/ }).last().click();
      const basic = page.getByLabel(/^Basic salary|^الراتب الأساسي/);
      await expect(basic).toHaveValue(''); // HR enters the amount: nothing is derived from the opening or the grade
      await basic.fill('10000');
      await expect(page.getByRole('status')).toHaveCount(0); // inside the 8000-12000 band
      await basic.fill('7000');
      await expect(page.getByRole('status')).toContainText(/8,?000|٨٬?٠٠٠/);
      await shot(page, 'offer-band-warning', locale);
      await basic.fill('13000');
      await expect(page.getByRole('status')).toContainText(/12,?000|١٢٬?٠٠٠/);
    });

    test('moving to Offer without an interview or assessment asks for a reason, then sends it', async ({ page }) => {
      const { posts } = await boot(page, { locale, appStage: 'Interview', advanceFails: true });
      await openTab(page, L.openings);
      await page.getByText('Accountant').first().click();
      await page.getByText('Nora Alharbi').first().click();
      await page.getByRole('button', { name: /Move to Offer|نقل إلى/ }).first().click();
      await expect(page.getByText(/Nobody has interviewed or assessed|لم تُجرَ مقابلة/)).toBeVisible();
      const reason = page.getByPlaceholder(/Reason for moving to Offer|سبب الانتقال/);
      await expect(reason).toBeVisible();
      await shot(page, 'stage-gate-reason', locale);
      await reason.fill('Senior referral, interviewed outside the system');
      await page.getByRole('button', { name: /Move to Offer with this reason|الانتقال إلى مرحلة العرض بهذا السبب/ }).click();
      await expect.poll(() => posts.filter(p => p.path.endsWith('/advance')).pop()?.body).toMatchObject({ overrideReason: 'Senior referral, interviewed outside the system' });
    });
  });
}
