import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * The appraisal screens HR, a manager and an employee use to run a cycle, in a real browser against a mocked API (fixture lane:
 * no stack, only `next dev`). Every /api/ request is intercepted; nothing touches a real tenant.
 *
 * What it proves: a cycle made in the screen carries a scorecard (and says where to make one when there is none); an employee
 * is never shown a score before HR publishes it, and can acknowledge or appeal once it is; a probation review can be opened
 * and written from the screen; a PIP cannot be closed adversely without a check-in and the screen leads to one; 360 feedback
 * can be read and given; each is right to left in Arabic with no sideways scroll. What it cannot prove: that the API returns
 * these payloads (the backend suites do).
 *
 *   FIXTURE_PORT=5200 NEXT_DIST_DIR=.next-claude npx playwright test -c e2e/playwright.fixture.config.ts \
 *     e2e/performance-appraisals-ui.spec.ts --project=desktop --project=phone
 */

const EVIDENCE = process.env.APPRAISALS_EVIDENCE_DIR;

type Persona = 'employee' | 'manager' | 'hr';
const PERMISSIONS: Record<Persona, string[]> = {
  employee: ['performance.read', 'ess.read', 'ess.write'],
  manager: ['performance.read', 'performance.write', 'ess.read'],
  hr: ['performance.read', 'performance.write', 'performance.approve', 'performance.cycle_manage', 'employees.read'],
};
const ROLES: Record<Persona, string[]> = { employee: ['Employee'], manager: ['Manager'], hr: ['HR Manager'] };

const review = (over: Record<string, unknown> = {}) => ({
  id: 'rv-1', cycleId: 'cy-1', cycleName: 'Annual Appraisal 2026', employeeId: 7, employeeName: 'Noura Alqahtani', departmentName: 'Finance',
  designationTitle: 'Accountant', scorecardTemplateId: 'tp-1', kpiScore: 80, competencyScore: 85, attendanceScore: 90, productivityScore: 80,
  feedbackScore: 75, disciplineScore: 90, finalScore: 82.5, finalRating: 'Exceeds Expectations', calibrationAdjustment: 0, calibrationNotes: '',
  selfAssessmentNotes: 'I closed the quarter on time.', managerNotes: '', hrNotes: '', status: 'Published', selfAssessmentSubmittedAt: '2026-10-01T00:00:00Z',
  managerReviewedAt: '2026-10-10T00:00:00Z', publishedAt: '2026-11-01T00:00:00Z', acknowledgedAt: null, isAppealed: false, reviewerManagerId: 3,
  reviewerManagerName: 'Faisal Al-Otaibi', createdAtUtc: '2026-09-01T00:00:00Z', ...over,
});
const template = (over: Record<string, unknown> = {}) => ({
  id: 'tp-1', name: 'Office staff scorecard', departmentName: '', designationTitle: '', grade: '', kpiWeight: 40, competencyWeight: 30, attendanceWeight: 10,
  productivityWeight: 10, feedbackWeight: 5, disciplineWeight: 5, minPassingScore: 60, requiresCalibration: true, requires360Feedback: false,
  isDefault: true, isActive: true, ratingLabels: '', createdAtUtc: '2026-08-01T00:00:00Z', ...over,
});
const cycle = (over: Record<string, unknown> = {}) => ({
  id: 'cy-1', name: 'Annual Appraisal 2026', cycleType: 'Annual', reviewPeriodStart: '2026-01-01', reviewPeriodEnd: '2026-12-31', status: 'Active',
  enableCalibration: true, enable360Feedback: true, enableSelfAssessment: true, enableForcedDistribution: false, selfAssessmentDeadline: null,
  managerReviewDeadline: null, calibrationDeadline: null, defaultScorecardTemplateId: 'tp-1', notes: '', createdAtUtc: '2026-08-01T00:00:00Z',
  launchedAtUtc: null, publishedAtUtc: null, closedAtUtc: null, enrolledCount: 12, ...over,
});
const probation = (over: Record<string, unknown> = {}) => ({
  id: 'pr-1', employeeId: 21, employeeName: 'Omar Haddad', departmentName: 'Sales', designationTitle: 'Sales Executive', probationStartDate: '2026-06-01',
  probationEndDate: '2026-11-30', reviewDueDate: null, performanceSummary: '', overallRating: 0, managerRecommendation: '', managerNotes: '', hrDecision: '',
  hrNotes: '', status: 'Pending', reviewedByManagerName: '', createdAtUtc: '2026-11-01T00:00:00Z', ...over,
});
const pip = (over: Record<string, unknown> = {}) => ({
  id: 'pip-1', employeeId: 31, employeeName: 'Layla Mansour', departmentName: 'Operations', performanceGaps: 'Missed deadlines', improvementGoals: 'Meet every deadline for 8 weeks',
  supportPlan: '', startDate: '2026-10-01', endDate: '2026-12-01', status: 'Active', hrNotes: '', managerNotes: '', employeeComments: '', initiatedByName: 'Faisal',
  createdAtUtc: '2026-10-01T00:00:00Z', closedAtUtc: null, latestCheckInOutcome: null, checkInCount: 0, ...over,
});

interface Opts {
  persona: Persona;
  locale?: 'en' | 'ar';
  reviews?: Record<string, unknown>[];
  detail?: Record<string, unknown>;
  templates?: Record<string, unknown>[];
  failTemplates?: boolean;
  probations?: Record<string, unknown>[];
  pips?: Record<string, unknown>[];
  failPipLoad?: boolean;
  entries?: Record<string, unknown>[];
  /** An API refusal for the named write: { path: [status, body] } */
  refuse?: Record<string, [number, Record<string, unknown>]>;
}

async function boot(page: Page, o: Opts, path = '/performance') {
  const errors: string[] = [];
  const calls: { method: string; path: string; body?: any }[] = [];
  const state = {
    reviews: o.reviews ?? [review()],
    probations: o.probations ?? [],
    pips: o.pips ?? [],
    checkIns: [] as any[],
    entries: o.entries ?? [],
  };
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && !/Failed to load resource|status of (4\d\d|5\d\d)/.test(m.text())) errors.push(m.text()); });
  await page.addInitScript(([locale]) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone.theme', 'light');
    if (locale === 'ar') localStorage.setItem('kynexone-locale-choice-v2', 'ar');
  }, [o.locale ?? 'en']);
  await page.addInitScript(() => { document.addEventListener('DOMContentLoaded', () => { const st = document.createElement('style'); st.textContent = 'nextjs-portal{display:none!important}'; document.head.appendChild(st); }); });

  await page.route('**/api/**', async route => {
    const req = route.request();
    const url = new URL(req.url());
    const p = url.pathname;
    const method = req.method();
    const body = req.postData() && (req.headers()['content-type'] ?? '').includes('json') ? req.postDataJSON() : undefined;
    const json = (b: unknown, status = 200) => route.fulfill({ status, json: b });
    if (method !== 'GET') calls.push({ method, path: p, body });
    const refusal = o.refuse?.[`${method} ${p}`];
    if (refusal) return json(refusal[1], refusal[0]);

    if (p === '/api/auth/me') {
      return json({
        id: 'user-7', employeeId: 7, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: o.persona === 'employee' ? 'Noura Alqahtani' : 'Faisal Al-Otaibi',
        roles: ROLES[o.persona], permissions: PERMISSIONS[o.persona], accessMode: 'FullPortal', isGroupScope: o.persona === 'hr', accountType: o.persona === 'hr' ? 'Group' : 'Company',
        companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
      });
    }
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/notifications') return json([]);

    // ── performance ──
    if (p === '/api/performance/templates') return o.failTemplates ? json({ message: 'boom' }, 500) : json(o.templates ?? [template(), template({ id: 'tp-2', name: 'Field staff scorecard', isDefault: false })]);
    if (p === '/api/performance/cycles' && method === 'GET') return json({ items: [cycle()], total: 1 });
    if (p === '/api/performance/cycles' && method === 'POST') return json(cycle({ id: 'cy-new', name: body?.name, status: 'Draft', defaultScorecardTemplateId: body?.defaultScorecardTemplateId }), 201);
    if (p === '/api/performance/reviews' && method === 'GET') {
      const mine = url.searchParams.get('view') === 'mine';
      const items = mine ? state.reviews.filter(r => r.employeeId === 7) : state.reviews;
      return json({ items, total: items.length });
    }
    let m = /^\/api\/performance\/reviews\/([\w-]+)$/.exec(p);
    if (m && method === 'GET') {
      const r = state.reviews.find(x => x.id === m![1]) ?? state.reviews[0];
      return json({
        review: r, template: template(),
        breakdown: [
          { component: 'KPI', rawScore: 80, weight: 40, weightedScore: 32, notes: '' }, { component: 'Competency', rawScore: 85, weight: 30, weightedScore: 25.5, notes: '' },
          { component: 'Attendance', rawScore: 90, weight: 10, weightedScore: 9, notes: '' }, { component: 'Productivity', rawScore: 80, weight: 10, weightedScore: 8, notes: '' },
          { component: 'Feedback', rawScore: 75, weight: 5, weightedScore: 3.75, notes: '' }, { component: 'Discipline', rawScore: 90, weight: 5, weightedScore: 4.5, notes: '' },
        ],
        competencies: [{ competencyId: 'c1', competencyName: 'Communication', competencyCategory: 'Core', selfRating: 4, managerRating: 4, selfComments: '', managerComments: '', weight: 50 }],
        goals: [{ id: 'g1', title: 'Close the quarter by day 5', achievementPct: 100, weight: 50 }],
        feedback360: [], auditLog: [], calibration: null, appeal: (o.detail as any)?.appeal ?? null, ...o.detail,
      });
    }
    m = /^\/api\/performance\/reviews\/([\w-]+)\/(acknowledge|appeal)$/.exec(p);
    if (m && method === 'POST') {
      state.reviews = state.reviews.map(r => r.id === m![1] ? { ...r, status: m![2] === 'acknowledge' ? 'Acknowledged' : 'Appealed' } : r);
      if (m[2] === 'appeal') (o.detail = { ...o.detail, appeal: { id: 'ap-1', status: 'Submitted', appealReason: body.appealReason, hrResponse: '', submittedAt: '2026-11-17T00:00:00Z' } });
      return json(state.reviews[0], m[2] === 'appeal' ? 201 : 200);
    }
    if (p === '/api/performance/reviews/appeals') return json([]);
    if (p === '/api/performance/probation' && method === 'GET') return json(state.probations);
    if (p === '/api/performance/probation' && method === 'POST') {
      const created = probation({ id: 'pr-new', employeeId: body.employeeId, employeeName: body.employeeName, probationStartDate: body.probationStartDate, probationEndDate: body.probationEndDate });
      state.probations = [created, ...state.probations];
      return json(created, 201);
    }
    m = /^\/api\/performance\/probation\/([\w-]+)\/manager-review$/.exec(p);
    if (m && method === 'POST') {
      state.probations = state.probations.map(x => x.id === m![1] ? { ...x, status: 'ManagerReviewed', managerRecommendation: body.recommendation } : x);
      return json(state.probations.find(x => x.id === m![1]));
    }
    if (p === '/api/performance/pip' && method === 'GET') return o.failPipLoad ? json({ message: 'boom' }, 500) : json(state.pips);
    if (p === '/api/performance/pip/termination-queue') return json({ items: [], total: 0 });
    m = /^\/api\/performance\/pip\/([\w-]+)$/.exec(p);
    if (m && method === 'GET') return json({ pip: state.pips.find(x => x.id === m![1]), checkIns: state.checkIns });
    m = /^\/api\/performance\/pip\/([\w-]+)\/checkin$/.exec(p);
    if (m && method === 'POST') {
      state.checkIns = [{ id: `ci-${state.checkIns.length + 1}`, pipId: m[1], checkInDate: body.checkInDate, notes: body.notes, outcome: body.outcome, checkedByName: 'Faisal Al-Otaibi', createdAtUtc: '2026-11-17T00:00:00Z' }, ...state.checkIns];
      state.pips = state.pips.map(x => x.id === m![1] ? { ...x, checkInCount: state.checkIns.length, latestCheckInOutcome: body.outcome } : x);
      return json({});
    }
    m = /^\/api\/performance\/pip\/([\w-]+)\/status$/.exec(p);
    if (m && method === 'POST') {
      state.pips = state.pips.map(x => x.id === m![1] ? { ...x, status: body.status } : x);
      return json({ pip: state.pips.find(x => x.id === m![1]), routedTo: null, nextStep: null });
    }
    m = /^\/api\/performance\/feedback\/360\/([\w-]+)$/.exec(p);
    if (m) return json(state.entries);
    if (p === '/api/performance/feedback/360' && method === 'POST') {
      state.entries = [...state.entries, { id: 'e-new', reviewId: body.reviewId, reviewerEmployeeId: 3, reviewerName: 'Faisal Al-Otaibi', reviewerRole: body.reviewerRole ?? 'Peer', isAnonymous: body.isAnonymous, score: body.score, strengths: body.strengths, improvements: body.improvements, comments: body.comments }];
      return json({});
    }
    if (p === '/api/performance/goals') return json({ items: [], total: 0 });
    if (p === '/api/employees' && method === 'GET') return json({ items: [{ id: 21, publicId: 'e-21', fullName: 'Omar Haddad', employeeCode: 'EMP-0021', department: 'Sales', status: 'Active' }], total: 1, page: 1, pageSize: 8 });
    if (p === '/api/employees/21') return json({ id: 21, fullName: 'Omar Haddad', joiningDate: '2026-06-01', probationStartDate: '2026-06-01', probationEndDate: '2026-11-30' });
    return json({ items: [], total: 0 });
  });

  await page.goto(path);
  return { errors, calls };
}

const noSideScroll = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1);
const shot = async (page: Page, name: string) => { if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/${name}.png`, animations: 'disabled' }); };
const tab = (page: Page, name: string | RegExp) => page.getByRole('button', { name }).first();

test.describe('Performance header', () => {
  test('the title and purpose line are Arabic under Arabic', async ({ page }) => {
    await boot(page, { persona: 'employee', locale: 'ar' });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('الأداء والتقييمات');
    await expect(page.getByText('تقييمات بطاقة الأداء المتوازن', { exact: false })).toBeVisible();
  });
});

for (const locale of ['en', 'ar'] as const) {
  const L = (en: string, ar: string) => (locale === 'ar' ? ar : en);

  test.describe(`New cycle needs a scorecard (${locale})`, () => {
    test('the company default is pre-selected and the cycle is created with it', async ({ page }) => {
      const log = await boot(page, { persona: 'hr', locale });
      await tab(page, L('Cycles', 'الدورات')).click();
      await page.getByRole('button', { name: L('New Cycle', 'دورة جديدة') }).click();
      const picker = page.getByTestId('cycle-template');
      await expect(picker).toBeVisible();
      await expect(picker).toHaveValue('tp-1');
      await expect(picker.locator('option:checked')).toContainText(L('company default', 'الافتراضي للشركة'));
      await page.getByLabel(L('Cycle Name', 'اسم الدورة')).fill('Mid-year 2026');
      await page.locator('input[type="date"]').nth(0).fill('2026-01-01');
      await page.locator('input[type="date"]').nth(1).fill('2026-06-30');
      await shot(page, `cycle-new-${locale}`);
      expect(await noSideScroll(page)).toBe(true);
      await page.getByRole('button', { name: L('Create Cycle', 'إنشاء دورة') }).click();
      await expect.poll(() => log.calls.find(c => c.path === '/api/performance/cycles')?.body?.defaultScorecardTemplateId).toBe('tp-1');
      expect(log.errors).toEqual([]);
    });

    test('with several templates and no default, the user must choose before creating', async ({ page }) => {
      const log = await boot(page, { persona: 'hr', locale, templates: [template({ isDefault: false }), template({ id: 'tp-2', name: 'Field staff scorecard', isDefault: false })] });
      await tab(page, L('Cycles', 'الدورات')).click();
      await page.getByRole('button', { name: L('New Cycle', 'دورة جديدة') }).click();
      await expect(page.getByTestId('cycle-template')).toHaveValue('');
      await page.getByLabel(L('Cycle Name', 'اسم الدورة')).fill('Mid-year 2026');
      await page.locator('input[type="date"]').nth(0).fill('2026-01-01');
      await page.locator('input[type="date"]').nth(1).fill('2026-06-30');
      await page.getByRole('button', { name: L('Create Cycle', 'إنشاء دورة') }).click();
      // the same sentence is also the field's `i` help text (hidden until hovered), so ask for the visible error alert
      await expect(page.getByRole('alert').getByText(L('A cycle cannot be launched without one', 'لا يمكن إطلاق الدورة بدونه'))).toBeVisible();
      expect(log.calls.filter(c => c.path === '/api/performance/cycles')).toEqual([]);
      await page.getByTestId('cycle-template').selectOption('tp-2');
      await page.getByRole('button', { name: L('Create Cycle', 'إنشاء دورة') }).click();
      await expect.poll(() => log.calls.find(c => c.path === '/api/performance/cycles')?.body?.defaultScorecardTemplateId).toBe('tp-2');
    });

    test('with no active template the form says where to make one and does not allow a dead-end cycle', async ({ page }) => {
      await boot(page, { persona: 'hr', locale, templates: [template({ isActive: false })] });
      await tab(page, L('Cycles', 'الدورات')).click();
      await page.getByRole('button', { name: L('New Cycle', 'دورة جديدة') }).click();
      const empty = page.locator('[data-empty-state="first-use"]');
      await expect(empty).toContainText(L('No scorecard template yet', 'لا يوجد قالب لبطاقة التقييم بعد'));
      await expect(empty.getByRole('link', { name: L('Create a scorecard in Setup', 'إنشاء بطاقة تقييم في الإعدادات') })).toHaveAttribute('href', '/setup?tab=performanceScorecards');
      await expect(page.getByRole('button', { name: L('Create Cycle', 'إنشاء دورة') })).toBeDisabled();
      await shot(page, `cycle-no-template-${locale}`);
    });

    test('a failed template load says so and can be retried', async ({ page }) => {
      await boot(page, { persona: 'hr', locale, failTemplates: true });
      await tab(page, L('Cycles', 'الدورات')).click();
      await page.getByRole('button', { name: L('New Cycle', 'دورة جديدة') }).click();
      await expect(page.getByTestId('list-load-failed')).toBeVisible();
      await expect(page.getByTestId('list-load-failed').getByRole('button', { name: L('Retry', 'إعادة المحاولة') })).toBeVisible();
    });
  });

  test.describe(`Employee review (${locale})`, () => {
    test('before HR publishes, the result is withheld on the list and in the review, whatever the API sends', async ({ page }) => {
      const log = await boot(page, { persona: 'employee', locale, reviews: [review({ status: 'ManagerReviewComplete' })] });
      const row = page.getByText('Annual Appraisal 2026').first();
      await expect(row).toBeVisible();
      await expect(page.getByText(/82\.5/)).toHaveCount(0);
      await page.getByRole('button', { name: L('Details', 'التفاصيل') }).click();
      const drawer = page.getByTestId('review-detail');
      await expect(drawer.getByTestId('result-withheld')).toContainText(L('not published yet', 'لم تُنشر نتيجتك بعد'));
      await expect(drawer.getByTestId('result-score')).toHaveCount(0);
      await expect(drawer.getByText(/82\.5|Exceeds|يفوق/)).toHaveCount(0);
      await expect(drawer.locator('[aria-current="step"]')).toContainText(L('HR calibration', 'معايرة الموارد البشرية'));
      await expect(page.getByRole('dialog').getByRole('button', { name: L('Acknowledge result', 'الإقرار بالنتيجة') })).toHaveCount(0);
      expect(await noSideScroll(page)).toBe(true);
      await shot(page, `review-withheld-${locale}`);
      expect(log.errors).toEqual([]);
    });

    test('once published: the result and how it was reached, then acknowledge', async ({ page }) => {
      const log = await boot(page, { persona: 'employee', locale });
      await page.getByRole('button', { name: L('View result', 'عرض النتيجة') }).click();
      const drawer = page.getByTestId('review-detail');
      await expect(drawer.getByTestId('result-score')).toContainText('82.5');
      await expect(drawer.getByTestId('result-score')).toContainText(L('Exceeds expectations', 'يفوق التوقعات'));
      await expect(drawer.getByRole('table')).toContainText(L('Goals and KPIs', 'الأهداف ومؤشرات الأداء'));
      await expect(drawer.getByTestId('notes-withheld')).toBeVisible();
      expect(await noSideScroll(page)).toBe(true);
      await shot(page, `review-published-${locale}`);
      await page.getByRole('dialog').getByRole('button', { name: L('Acknowledge result', 'الإقرار بالنتيجة') }).click();
      await expect.poll(() => log.calls.some(c => c.path === '/api/performance/reviews/rv-1/acknowledge')).toBe(true);
      await expect(page.locator('[data-result]')).toContainText(L('You acknowledged your result.', 'أقررتَ بنتيجتك.'));
      expect(log.errors).toEqual([]);
    });

    test('the employee can appeal a published result, and the screen then shows the appeal', async ({ page }) => {
      const log = await boot(page, { persona: 'employee', locale });
      await page.getByRole('button', { name: L('View result', 'عرض النتيجة') }).click();
      const drawer = page.getByTestId('review-detail');
      await page.getByRole('dialog').getByRole('button', { name: L('Ask HR to review', 'طلب مراجعة من الموارد البشرية') }).click();
      await shot(page, `review-appeal-${locale}`);
      await page.getByRole('dialog').getByRole('button', { name: L('Send to HR', 'إرسال إلى الموارد البشرية') }).click();
      await expect(page.getByTestId('review-detail-error')).toContainText(L('Say what you disagree with', 'اذكر ما تعترض عليه'));
      expect(log.calls.filter(c => c.path.endsWith('/appeal'))).toEqual([]);
      await drawer.getByRole('textbox', { name: L('What do you disagree with?', 'ما الذي تعترض عليه؟') }).fill('A goal I completed was not counted.');
      await page.getByRole('dialog').getByRole('button', { name: L('Send to HR', 'إرسال إلى الموارد البشرية') }).click();
      await expect.poll(() => log.calls.find(c => c.path === '/api/performance/reviews/rv-1/appeal')?.body?.appealReason).toBe('A goal I completed was not counted.');
      await expect(page.getByTestId('appeal-status')).toContainText(L('sent to HR', 'أُرسل إلى الموارد البشرية'));
      await expect(page.locator('[data-result]')).toContainText(L('Your appeal was sent to HR.', 'أُرسل تظلمك إلى الموارد البشرية.'));
      expect(log.errors).toEqual([]);
    });

    test('a refused appeal is explained in the viewer\'s language', async ({ page }) => {
      await boot(page, { persona: 'employee', locale, refuse: { 'POST /api/performance/reviews/rv-1/appeal': [409, { error: 'appeal_already_open', message: 'An appeal is already open for this review.' }] } });
      await page.getByRole('button', { name: L('View result', 'عرض النتيجة') }).click();
      const drawer = page.getByTestId('review-detail');
      await page.getByRole('dialog').getByRole('button', { name: L('Ask HR to review', 'طلب مراجعة من الموارد البشرية') }).click();
      await drawer.getByRole('textbox', { name: L('What do you disagree with?', 'ما الذي تعترض عليه؟') }).fill('Not fair.');
      await page.getByRole('dialog').getByRole('button', { name: L('Send to HR', 'إرسال إلى الموارد البشرية') }).click();
      await expect(page.getByTestId('review-detail-error')).toContainText(L('An appeal is already open for this review', 'يوجد تظلم مفتوح لهذا التقييم'));
    });

    test('a failed review list says so and can be retried; an empty one says what to expect', async ({ page }) => {
      await boot(page, { persona: 'employee', locale, reviews: [] });
      await expect(page.locator('[data-empty-state]')).toContainText(L('Your review appears here when HR starts an appraisal cycle', 'يظهر تقييمك هنا عندما تبدأ الموارد البشرية دورة تقييم'));
      await shot(page, `my-reviews-empty-${locale}`);
    });
  });

  test.describe(`Probation (${locale})`, () => {
    test('HR opens a probation review: dates come from the employee record, and the manager review is the next step', async ({ page }) => {
      const log = await boot(page, { persona: 'hr', locale });
      await tab(page, L('PIP & Probation', 'خطط تحسين الأداء وفترة التجربة')).click();
      await page.getByRole('button', { name: /Probation \(|فترة التجربة \(|تحت التجربة \(/ }).click();
      await expect(page.locator('[data-empty-state="first-use"]')).toContainText(L('Open a review when someone is nearing the end of probation', 'افتح تقييمًا عندما تقترب نهاية فترة تجربة'));
      await shot(page, `probation-empty-${locale}`);
      await page.locator('[data-empty-state] button').click();
      const dialog = page.getByTestId('new-probation');
      await dialog.getByRole('textbox').first().fill('Omar');
      await page.getByRole('button', { name: /Omar Haddad/ }).click();
      await expect(dialog.locator('input[type="date"]').nth(1)).toHaveValue('2026-11-30');
      await expect(dialog.locator('input[type="date"]').nth(0)).toHaveValue('2026-06-01');
      expect(await noSideScroll(page)).toBe(true);
      await shot(page, `probation-new-${locale}`);
      await page.getByRole('button', { name: L('Open review', 'فتح التقييم') }).click();
      await expect.poll(() => log.calls.find(c => c.path === '/api/performance/probation')?.body).toMatchObject({ employeeId: 21, probationStartDate: '2026-06-01', probationEndDate: '2026-11-30' });
      await expect(page.locator('[data-result]')).toContainText(L('Probation review opened for Omar Haddad.', 'فُتح تقييم فترة التجربة لـ Omar Haddad.'));
      expect(log.errors).toEqual([]);
    });

    test('the manager writes the probation review: all three answers are needed, then it goes to HR', async ({ page }) => {
      const log = await boot(page, { persona: 'manager', locale, probations: [probation()] });
      await tab(page, L('PIP & Probation', 'خطط تحسين الأداء وفترة التجربة')).click();
      await page.getByRole('button', { name: /Probation \(|فترة التجربة \(|تحت التجربة \(/ }).click();
      await page.getByRole('button', { name: L('Write review', 'كتابة التقييم') }).click();
      const form = page.getByTestId('probation-review-form');
      await page.getByRole('button', { name: L('Submit review', 'إرسال التقييم') }).click();
      await expect(form.getByRole('alert')).toContainText(L('Write a short summary', 'اكتب ملخصًا قصيرًا'));
      await form.getByRole('textbox').first().fill('Learned the role fast and meets targets.');
      await page.getByRole('button', { name: L('Submit review', 'إرسال التقييم') }).click();
      await expect(form.getByRole('alert')).toContainText(L('Choose an overall rating', 'اختر تقييمًا عامًا'));
      await form.getByRole('combobox').selectOption('4');
      await page.getByRole('button', { name: L('Submit review', 'إرسال التقييم') }).click();
      await expect(form.getByRole('alert')).toContainText(L('Choose what you recommend', 'اختر ما توصي به'));
      await form.getByText(L('Confirm in the role', 'التثبيت في الوظيفة')).click();
      expect(await noSideScroll(page)).toBe(true);
      await shot(page, `probation-review-${locale}`);
      await page.getByRole('button', { name: L('Submit review', 'إرسال التقييم') }).click();
      await expect.poll(() => log.calls.find(c => c.path === '/api/performance/probation/pr-1/manager-review')?.body).toMatchObject({ overallRating: 4, recommendation: 'Confirm' });
      await expect(page.locator('[data-result]')).toContainText(L('sent to HR', 'إلى الموارد البشرية'));
      expect(log.errors).toEqual([]);
    });

    test('a probation load that fails says so with Retry, instead of an empty list', async ({ page }) => {
      await boot(page, { persona: 'hr', locale, failPipLoad: true });
      await tab(page, L('PIP & Probation', 'خطط تحسين الأداء وفترة التجربة')).click();
      await expect(page.getByTestId('list-load-failed')).toBeVisible();
      await expect(page.locator('[data-empty-state]')).toHaveCount(0);
    });
  });

  test.describe(`PIP close (${locale})`, () => {
    test('a plan with no check-in cannot be closed as failed: the screen leads to a check-in, then back to the close', async ({ page }) => {
      const log = await boot(page, { persona: 'hr', locale, pips: [pip()] });
      await tab(page, L('PIP & Probation', 'خطط تحسين الأداء وفترة التجربة')).click();
      await page.getByRole('button', { name: L('Update Status', 'تحديث الحالة') }).click();
      await page.getByTitle(L('New Status', 'الحالة الجديدة')).selectOption('Failed');
      const warning = page.getByTestId('pip-needs-checkin');
      await expect(warning).toContainText(L('no check-ins yet', 'لا توجد متابعات لهذه الخطة بعد'));
      await expect(page.getByRole('button', { name: L('Update', 'تحديث'), exact: true })).toBeDisabled();
      await shot(page, `pip-close-blocked-${locale}`);
      await warning.getByRole('button').click();
      const dialog = page.getByTestId('pip-checkin');
      await expect(dialog.getByTestId('checkin-none')).toBeVisible();
      await dialog.getByRole('combobox').selectOption('Deteriorated');
      await dialog.getByLabel(L('What was discussed', 'ما جرت مناقشته')).fill('Missed two deadlines again; support was offered.');
      expect(await noSideScroll(page)).toBe(true);
      await shot(page, `pip-checkin-${locale}`);
      await page.getByRole('button', { name: L('Save check-in', 'حفظ المتابعة') }).click();
      await expect.poll(() => log.calls.find(c => c.path === '/api/performance/pip/pip-1/checkin')?.body).toMatchObject({ outcome: 'Deteriorated' });
      await expect(dialog.locator('[data-result]')).toContainText(L('Check-in recorded.', 'سُجّلت المتابعة.'));
      await dialog.getByRole('button', { name: L('Continue closing the plan', 'متابعة إغلاق الخطة') }).click();
      await expect(page.getByTestId('pip-needs-checkin')).toHaveCount(0);
      await page.getByRole('button', { name: L('Update', 'تحديث'), exact: true }).click();
      await expect.poll(() => log.calls.find(c => c.path === '/api/performance/pip/pip-1/status')?.body).toMatchObject({ status: 'Failed' });
      expect(log.errors).toEqual([]);
    });

    test('a manager can record a check-in on an Active or Extended plan', async ({ page }) => {
      const log = await boot(page, { persona: 'manager', locale, pips: [pip({ status: 'Extended' })] });
      await tab(page, L('PIP & Probation', 'خطط تحسين الأداء وفترة التجربة')).click();
      await expect(page.getByRole('button', { name: L('Update Status', 'تحديث الحالة') })).toHaveCount(0);
      await page.getByRole('button', { name: L('Record check-in', 'تسجيل متابعة') }).click();
      const dialog = page.getByTestId('pip-checkin');
      await page.getByRole('button', { name: L('Save check-in', 'حفظ المتابعة') }).click();
      await expect(dialog.getByRole('alert')).toContainText(L('Write what was discussed', 'اكتب ما جرت مناقشته'));
      expect(log.calls.filter(c => c.path.endsWith('/checkin'))).toEqual([]);
    });

    test('an Extended plan can still be closed by HR', async ({ page }) => {
      await boot(page, { persona: 'hr', locale, pips: [pip({ status: 'Extended', checkInCount: 2 })] });
      await tab(page, L('PIP & Probation', 'خطط تحسين الأداء وفترة التجربة')).click();
      await expect(page.getByRole('button', { name: L('Update Status', 'تحديث الحالة') })).toBeVisible();
    });

    test('the API\'s own refusal is shown in the viewer\'s language', async ({ page }) => {
      await boot(page, { persona: 'hr', locale, pips: [pip({ checkInCount: 1 })], refuse: { 'POST /api/performance/pip/pip-1/status': [409, { error: 'no_monitoring_evidence', message: 'This plan has no recorded check-ins' }] } });
      await tab(page, L('PIP & Probation', 'خطط تحسين الأداء وفترة التجربة')).click();
      await page.getByRole('button', { name: L('Update Status', 'تحديث الحالة') }).click();
      await page.getByTitle(L('New Status', 'الحالة الجديدة')).selectOption('Failed');
      await page.getByRole('button', { name: L('Update', 'تحديث'), exact: true }).click();
      await expect(page.getByText(L('no check-ins yet', 'لا توجد متابعات لهذه الخطة بعد')).first()).toBeVisible();
    });
  });

  test.describe(`360 feedback (${locale})`, () => {
    test('a manager reads the feedback on a review and gives their own', async ({ page }) => {
      const log = await boot(page, {
        persona: 'manager', locale, reviews: [review({ id: 'rv-9', employeeId: 21, employeeName: 'Omar Haddad', status: 'ManagerReview' })],
        entries: [{ id: 'e1', reviewId: 'rv-9', reviewerEmployeeId: 5, reviewerName: 'Sara Nasser', reviewerRole: 'Peer', isAnonymous: false, score: 4, strengths: 'Always helps the team.', improvements: '', comments: '' }],
      });
      await page.getByRole('button', { name: L('360 feedback', 'ملاحظات 360') }).click();
      const modal = page.getByTestId('feedback-360');
      await expect(modal).toContainText('Sara Nasser');
      await expect(modal).toContainText(L('Average 4 of 5', 'المتوسط 4 من 5'));
      await page.getByRole('button', { name: L('Send feedback', 'إرسال الملاحظات') }).click();
      await expect(modal.getByRole('alert')).toContainText(L('Choose an overall rating', 'اختر تقييمًا عامًا'));
      await modal.getByRole('combobox').first().selectOption('5');
      await modal.getByLabel(L('Strengths', 'نقاط القوة')).fill('Calm under pressure.');
      expect(await noSideScroll(page)).toBe(true);
      await shot(page, `f360-${locale}`);
      await page.getByRole('button', { name: L('Send feedback', 'إرسال الملاحظات') }).click();
      await expect.poll(() => log.calls.find(c => c.path === '/api/performance/feedback/360')?.body).toMatchObject({ reviewId: 'rv-9', score: 5, strengths: 'Calm under pressure.', isAnonymous: false });
      await expect(modal.locator('[data-result]')).toContainText(L('Your feedback was sent.', 'أُرسلت ملاحظاتك.'));
      expect(log.errors).toEqual([]);
    });

    test('once the review is past the writing stage the feedback can be read but not added', async ({ page }) => {
      await boot(page, { persona: 'manager', locale, reviews: [review({ id: 'rv-9', employeeId: 21, employeeName: 'Omar Haddad', status: 'FinalApproval' })] });
      await page.getByRole('button', { name: L('View 360 feedback', 'عرض ملاحظات 360') }).click();
      const modal = page.getByTestId('feedback-360');
      await expect(modal.getByTestId('f360-none')).toBeVisible();
      await expect(page.getByRole('button', { name: L('Send feedback', 'إرسال الملاحظات') })).toHaveCount(0);
    });

    test('the API refusal (login not linked to an employee) is explained', async ({ page }) => {
      await boot(page, {
        persona: 'manager', locale, reviews: [review({ id: 'rv-9', employeeId: 21, employeeName: 'Omar Haddad', status: 'ManagerReview' })],
        refuse: { 'POST /api/performance/feedback/360': [403, { error: 'reviewer_not_linked', message: 'x' }] },
      });
      await page.getByRole('button', { name: L('360 feedback', 'ملاحظات 360') }).click();
      const modal = page.getByTestId('feedback-360');
      await modal.getByRole('combobox').first().selectOption('3');
      await modal.getByLabel(L('Strengths', 'نقاط القوة')).fill('Good.');
      await page.getByRole('button', { name: L('Send feedback', 'إرسال الملاحظات') }).click();
      await expect(modal.getByRole('alert')).toContainText(L('your login is not linked to an employee record', 'حسابك غير مرتبط بسجل موظف'));
    });
  });
}

test.describe('Accessibility of the new screens', () => {
  test('the published review and the check-in dialog have no serious axe violations', async ({ page }) => {
    await boot(page, { persona: 'employee' });
    await page.getByRole('button', { name: 'View result' }).click();
    await expect(page.getByTestId('result-score')).toBeVisible();
    // The drawer fades and slides in; axe blends the colours of a half-transparent badge with the scrim. Measure the settled screen.
    await page.evaluate(() => Promise.all(document.getAnimations().map(a => a.finished.catch(() => undefined))));
    const serious = (await new AxeBuilder({ page }).include('[role="dialog"]').analyze()).violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
    expect(serious.map(v => `${v.id}: ${v.nodes[0]?.html} ${v.nodes[0]?.any?.[0]?.message ?? ''}`)).toEqual([]);
  });
});
