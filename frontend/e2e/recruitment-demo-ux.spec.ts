import { expect, test, type Page, type Request } from '@playwright/test';
import fs from 'node:fs';

/**
 * Recruitment demo rehearsal fixes, against a mocked API (nothing is read or written):
 *   1 the application drawer's header is below the top bar and its tabs are labelled words
 *   2 creating an offer does not ask whether to discard what was just saved
 *   3 offers show dates, not stored timestamps; a legacy revision is "Original"
 *   4 interview rows name the candidate and job, show the feedback notes, and use the app's date format
 *   5 placeholders are Saudi-appropriate
 *   6 the timeline says which stage an application left and reached
 *   7 onboarding picks a person instead of asking for ids
 * Run: npx playwright test -c e2e/playwright.recruitment-demo.config.ts
 */

const OPENING = { id: 'op-1', jobCode: 'JOB-1', requisitionId: null, title: 'Sales Executive', departmentName: 'Sales', designationTitle: 'Sales Executive', employmentType: 'Full-Time', headCount: 1, filledCount: 0, description: '', requirements: '', responsibilities: '', salaryFrom: null, salaryTo: null, location: 'Riyadh', status: 'Open', assignedHrName: '', createdAtUtc: '2026-11-01T08:00:00Z', activeApplications: 1, remaining: 1 };
const app = (over: Record<string, unknown> = {}) => ({ id: 'app-1', version: 3, jobOpeningId: 'op-1', jobTitle: 'Sales Executive', candidateId: 'c-1', candidateName: 'Nora Alharbi', candidateEmail: 'nora@example.invalid', stage: 'Interview', stageOrder: 3, status: 'Active', rejectionReason: '', offeredSalary: null, appliedAtUtc: '2026-11-01T08:00:00Z', stageChangedAtUtc: null, hiredAtUtc: null, onboardingDraftId: null, ...over });
const EVENTS = [
  { id: 'e1', eventType: 'ApplicationCreated', stage: 'Applied', notes: 'Application created.', performedByName: 'HR', createdAtUtc: '2026-11-01T08:00:00Z' },
  { id: 'e2', eventType: 'StageAdvanced', stage: 'Screening', notes: 'Advanced application stage.', performedByName: 'HR', createdAtUtc: '2026-11-02T08:00:00Z' },
  { id: 'e3', eventType: 'StageAdvanced', stage: 'Assessment', notes: 'Advanced application stage.', performedByName: 'HR', createdAtUtc: '2026-11-03T08:00:00Z' },
  { id: 'e4', eventType: 'StageAdvanced', stage: 'Interview', notes: 'Advanced application stage.', performedByName: 'HR', createdAtUtc: '2026-11-04T08:00:00Z' },
];
const ISO = '2026-11-19T04:43:06.1954Z';
const offerRow = { id: 'offer-1', applicationId: 'app-1', companyId: null, seriesId: null, revisionNumber: null, version: 1, candidateName: 'Nora Alharbi', position: 'Sales Executive', status: 'Draft', communicationOutcome: 'NotIssued', originalProvenance: 'NotFrozen', generatedAtUtc: '2026-11-12T04:43:06.717683Z', responseDeadline: ISO };
const offer = { ...offerRow, offeredJobTitle: 'Sales Executive', offeredDepartment: 'Sales', startDate: '2027-01-15', basicSalary: 4500, housingAllowance: 1125, transportAllowance: 450, otherAllowances: 0, grossSalary: 6075, probationMonths: 3, sentAtUtc: null, acceptedAtUtc: null, recipientEmail: 'nora@example.invalid', frozenAtUtc: null };
const workspace = { offer, artifact: null, originalProvenance: 'NotFrozen', approvals: [], capabilities: {}, revisions: [offerRow], changedFields: [], communicationOutcome: 'NotIssued', compensationSnapshot: null, designSnapshot: null };

interface Boot { locale?: 'en' | 'ar'; url?: string; interviews?: unknown[]; hired?: unknown[] }

async function boot(page: Page, opts: Boot = {}) {
  const posts: { path: string; body: unknown }[] = [];
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(locale => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', locale); }, opts.locale ?? 'en');
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
    if (path === '/api/recruitment/openings') return json({ items: [OPENING], total: 1, page: 1, pageSize: 50 });
    if (path === '/api/recruitment/openings/op-1') return json({ opening: OPENING, stageCounts: [] });
    if (path === '/api/recruitment/applications/kanban/op-1') return json({ stages: ['Applied', 'Screening', 'Assessment', 'Interview', 'Offer', 'Hired'].map((stage, order) => ({ stage, order, applications: stage === 'Interview' ? [app()] : [] })), rejected: [] });
    if (path === '/api/recruitment/applications/app-1') return json({ application: app({ stage: 'Offer' }), candidate: { id: 'c-1', email: 'nora@example.invalid', phone: '+966 50 123 4567', totalExperienceYears: 4, source: 'Direct' }, events: EVENTS, interviews: [], offer: null });
    if (path === '/api/recruitment/applications/app-1/offer' && request.method() === 'POST') return json({ ...offer, id: 'offer-9' });
    if (path === '/api/recruitment/applications') {
      const status = new URL(request.url()).searchParams.get('status');
      const items = status === 'Hired' ? (opts.hired ?? []) : [app()];
      return json({ items, total: items.length, page: 1, pageSize: 100 });
    }
    if (path === '/api/recruitment/interviews') return json({ items: opts.interviews ?? [], total: (opts.interviews ?? []).length, page: 1, pageSize: 20 });
    if (path === '/api/recruitment/offers/history') return json({ items: [offerRow], total: 1, page: 1, pageSize: 20 });
    if (path.endsWith('/workspace')) return json(workspace);
    if (path.endsWith('/events')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    if (path === '/api/recruitment/offers/placement-options') return json({ departments: [], designations: [] });
    if (path === '/api/recruitment/offer-templates/defaults') return json({ design: { schemaVersion: 2 }, tokens: [] });
    if (path === '/api/recruitment/offer-templates') return json([]);
    if (path === '/api/recruitment/onboarding/checklists') return json([{ id: 'ck-1', code: 'STD', name: 'Standard onboarding', description: '', applicableTo: 'All', departmentName: '', isActive: true }]);
    if (path === '/api/recruitment/onboarding/checklists/ck-1/template-tasks') return json([]);
    if (path === '/api/recruitment/onboarding/tasks/bulk') return json({ count: 3, tasks: [] });
    if (path === '/api/recruitment/onboarding/tasks') return json({ total: 0, items: [] });
    if (path === '/api/recruitment/onboarding/summary') return json({ total: 0, pending: 0, inProgress: 0, completed: 0, blocked: 0, mandatory: 0, mandatoryCompleted: 0, completionPct: 0 });
    if (path === '/api/employees') return json({ items: [{ id: 7, publicId: 'emp-public-7', fullName: 'Khalid Alotaibi', employeeCode: 'EMP-0007', department: 'Sales', designation: 'Sales Executive', status: 'Active' }], total: 1, page: 1, pageSize: 8 });
    if (path.startsWith('/api/recruitment/')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(opts.url ?? '/recruitment');
  return { posts, errors };
}

async function shot(page: Page, name: string, locale: string) {
  const dir = process.env.RECRUITMENT_DEMO_SHOTS;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/${name}-${locale}-${page.viewportSize()!.width >= 1000 ? 'desktop' : 'phone'}.png` });
}

const openTab = (page: Page, name: RegExp) => page.getByRole('button', { name }).first().click();
async function openDrawer(page: Page) {
  await openTab(page, /^Job Openings|^الوظائف الشاغرة/);
  await page.getByText('Sales Executive').first().click();
  await page.getByText('Nora Alharbi').first().click();
}

for (const locale of ['en', 'ar'] as const) {
  const L = locale === 'en'
    ? { timeline: 'Timeline', interviews: 'Interviews', offer: 'Offer', screening: 'Screening', assessment: 'Assessment', original: 'Original' }
    : { timeline: 'الجدول الزمني', interviews: 'المقابلات', offer: 'العرض', screening: 'الفرز', assessment: 'التقييم', original: 'الأصلي' };

  test.describe(`recruitment demo fixes (${locale})`, () => {
    test('1+6 the drawer header sits below the top bar, its tabs are words, and the timeline names each stage move', async ({ page }) => {
      const { errors } = await boot(page, { locale });
      await openDrawer(page);
      const close = page.getByRole('button', { name: /^Close$|^إغلاق$/ }).last();
      await expect(close).toBeVisible();
      // The drawer is an overlay like every dialog: its header is what a click at its centre reaches, not the sticky top bar painted over it.
      expect(await close.evaluate(el => { const r = el.getBoundingClientRect(); const hit = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2); return el.contains(hit); })).toBe(true);
      const name = page.getByText('Nora Alharbi').last();
      expect(await name.evaluate(el => { const r = el.getBoundingClientRect(); const hit = document.elementFromPoint(r.x + 4, r.y + r.height / 2); return el.contains(hit); })).toBe(true);
      for (const label of [L.timeline, L.interviews, L.offer]) await expect(page.getByRole('button', { name: label, exact: true }).last()).toBeVisible();
      await expect(page.getByText('timeline', { exact: true })).toHaveCount(0);
      await expect(page.getByText(/→/)).toHaveCount(3);
      await expect(page.getByText(new RegExp(`${L.screening}\\s*→\\s*${L.assessment}`))).toBeVisible();
      await expect(page.getByText('Advanced application stage.')).toHaveCount(0);
      await shot(page, 'drawer-timeline', locale);
      expect(errors).toEqual([]);
    });

    test('2 generating an offer from the drawer does not ask to discard the saved form', async ({ page }) => {
      const { posts } = await boot(page, { locale });
      await openDrawer(page);
      await page.getByRole('button', { name: L.offer, exact: true }).click();
      await page.getByLabel(/^Start date|تاريخ البدء|تاريخ المباشرة/).fill('2027-01-15');
      await page.getByLabel(/^Basic salary|الراتب الأساسي/).fill('4500');
      await page.getByRole('button', { name: /Generate Offer Letter|إنشاء خطاب العرض/ }).click();
      await expect(page).toHaveURL(/\/recruitment\/offers\/offer-9/);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      expect(posts.some(p => p.path === '/api/recruitment/applications/app-1/offer')).toBe(true);
    });

    test('3 offers show dates, not stored timestamps; a legacy revision is "Original"', async ({ page }) => {
      await boot(page, { locale, url: '/recruitment?tab=offers' });
      await expect(page.getByText('Nora Alharbi').first()).toBeVisible();
      await expect(page.getByText(/\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/)).toHaveCount(0);
      await expect(page.getByText(L.original).first()).toBeVisible();
      await shot(page, 'offers-list', locale);
      await page.goto('/recruitment/offers/offer-1');
      await expect(page.getByRole('heading', { name: 'Nora Alharbi' })).toBeVisible();
      await expect(page.locator('body')).not.toContainText(/\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/);
      await expect(page.locator('body')).not.toContainText('Legacy');
      await expect(page.locator('body')).not.toContainText(/Next action: History/);
      await shot(page, 'offer-workspace', locale);
    });

    test('4 interview rows name the candidate and job, show the notes, and use the app date format', async ({ page }) => {
      const interviews = [
        { id: 'i1', applicationId: 'app-1', interviewType: 'Technical', interviewerNames: 'Sara Ahmed', scheduledAt: '2026-11-19T07:30:00Z', durationMinutes: 60, mode: 'Video', meetingLink: '', location: '', status: 'Completed', overallRating: 4, recommendation: 'Hire', feedbackNotes: 'Clear communicator, strong account history.', completedAt: null, createdAtUtc: '2026-11-10T08:00:00Z' },
      ];
      await boot(page, { locale, interviews });
      await openTab(page, /^Interviews|^المقابلات/);
      await expect(page.getByText('Nora Alharbi')).toBeVisible();
      await expect(page.getByText('Sales Executive').first()).toBeVisible();
      await expect(page.getByText('Clear communicator, strong account history.')).toBeVisible();
      const body = await page.locator('main').innerText();
      expect(body).not.toMatch(/\b11\/19\/2026\b/);
      expect(body).toMatch(locale === 'en' ? /19 Nov 2026/ : /2026/);
      await shot(page, 'interviews', locale);
    });

    test('5 placeholders are Saudi-appropriate', async ({ page }) => {
      await boot(page, { locale });
      await openTab(page, /^Talent Pool|^قاعدة المواهب|^مجمع المواهب/);
      await page.getByRole('button', { name: /Add (to )?(Talent|Candidate)|إضافة/ }).first().click();
      await expect(page.getByLabel(/^Phone|الهاتف|الجوال/)).toHaveAttribute('placeholder', '+966 50 000 0000');
      await expect(page.getByLabel(/^Nationality|الجنسية/)).toHaveAttribute('placeholder', locale === 'en' ? 'For example: Saudi' : 'مثال: سعودي');
      await expect(page.locator('body')).not.toContainText(/UAE|\+971/);
      await shot(page, 'candidate-form', locale);
    });

    test('7 onboarding picks a hired candidate or an employee instead of asking for ids', async ({ page }) => {
      const { posts } = await boot(page, { locale, hired: [app({ id: 'app-h', stage: 'Hired', status: 'Hired', candidateName: 'Nora Alharbi' })] });
      await openTab(page, /^Onboarding|^التهيئة|^الانضمام|^الاستقبال/);
      await expect(page.getByPlaceholder('Employee ID')).toHaveCount(0);
      await expect(page.getByPlaceholder('Application ID')).toHaveCount(0);
      await expect(page.locator('select').filter({ has: page.locator('option', { hasText: 'Nora Alharbi' }) })).toHaveCount(1);
      await shot(page, 'onboarding', locale);
      if (locale === 'en') {
        await page.locator('select').filter({ has: page.locator('option', { hasText: 'Nora Alharbi' }) }).selectOption({ label: 'Nora Alharbi — Sales Executive' });
        await page.getByRole('button', { name: 'Generate Tasks From Checklist' }).click();
        await expect.poll(() => posts.find(p => p.path === '/api/recruitment/onboarding/tasks/bulk')?.body).toMatchObject({ checklistId: 'ck-1', applicationId: 'app-h' });
      }
    });
  });
}
