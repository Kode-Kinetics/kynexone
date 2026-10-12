import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Engagement surveys against synthetic intercepted API responses (no backend, no tenant). Proves the UI contract:
 * hidden values render the suppression sentence and never a number, the respondent sees the server's privacy notice
 * verbatim, drafts and one submission, refusals in plain words, Arabic RTL and a 390px phone. The real backend journey
 * is engagement-journey.spec.ts.
 */

const SURVEY = 'sv-1';
const K = 5;
const SUPPRESSED = 'Not enough responses to protect confidentiality (minimum 5).';

const content = {
  drivers: [{ key: 'recognition', labelEn: 'Recognition', labelAr: 'التقدير' }],
  questions: [
    { key: 'q1', type: 'likert5', textEn: 'I feel recognised.', textAr: 'أشعر بالتقدير.', required: true, driver: 'recognition' },
    { key: 'q2', type: 'enps', textEn: 'Would you recommend us?', textAr: 'هل توصي بنا؟', required: true },
    { key: 'q3', type: 'text', textEn: 'One thing to change?', textAr: 'شيء واحد للتغيير؟', required: false },
  ],
};

const survey = {
  id: SURVEY, templateId: 'tp-1', titleEn: 'Q4 pulse', titleAr: 'نبض الربع الرابع', mode: 'Anonymous', status: 'Open', companyId: 'co-1',
  opensAtUtc: '2026-10-01T06:00:00Z', closesAtUtc: '2026-10-20T20:00:00Z', recurrence: 'None', seriesId: SURVEY, cycleNumber: 1,
  minCohortSize: K, reminderOffsetsDays: [3], invitationCount: 36, submitted: 34, launchedAtUtc: '2026-10-01T06:00:00Z', closedAtUtc: null, version: 3,
  commentDimension: 'department',
};

const dist = (counts: number[]) => counts.map((c, i) => ({
  key: String(i + 1), labelEn: ['Strongly disagree', 'Disagree', 'Neutral', 'Agree', 'Strongly agree'][i], labelAr: '', count: c,
  pct: Math.round((1000 * c) / counts.reduce((a, b) => a + b, 0)) / 10,
}));

function results(filtered: 'none' | 'blocked' | 'open') {
  if (filtered === 'open') {
    return {
      surveyId: SURVEY, titleEn: 'Q4 pulse', titleAr: 'نبض الربع الرابع', mode: 'Anonymous', status: 'Open', cycleNumber: 1, minCohortSize: K,
      participation: { visible: true, invited: 36, submitted: 20, responseRatePct: 55.6, definition: '' }, filters: [], visible: false,
      suppressionReason: 'results_after_close', respondents: null, favourablePct: null, enps: null, drivers: [], items: [], breakdown: null,
      suppressedMessage: SUPPRESSED, generatedAtUtc: '2026-10-11T08:00:00Z', resultsAvailable: false, resultsAvailableFromUtc: '2026-10-20T20:00:00Z', commentDimension: 'department',
    };
  }
  const hidden = filtered === 'blocked';
  return {
    resultsAvailable: true, resultsAvailableFromUtc: null, commentDimension: 'department',
    surveyId: SURVEY, titleEn: 'Q4 pulse', titleAr: 'نبض الربع الرابع', mode: 'Anonymous', status: 'Open', cycleNumber: 1, minCohortSize: K,
    participation: { visible: true, invited: 36, submitted: 34, responseRatePct: 94.4, definition: '' },
    filters: [], visible: !hidden, suppressionReason: hidden ? 'suppressed' : null,
    respondents: hidden ? null : 34, favourablePct: hidden ? null : 64.7,
    enps: hidden ? null : { score: 17.6, promotersPct: 44.1, passivesPct: 29.4, detractorsPct: 26.5, answered: 34 },
    drivers: [{ key: 'recognition', labelEn: 'Recognition', labelAr: 'التقدير', visible: !hidden, suppressionReason: null, respondents: 34, mean: 3.82, favourablePct: 70.6 }],
    items: [
      { key: 'q1', type: 'likert5', textEn: 'I feel recognised.', textAr: 'أشعر بالتقدير.', driver: 'recognition', visible: !hidden, suppressionReason: null, answered: 34, mean: 3.82, favourablePct: 70.6, enps: null, distribution: dist([1, 3, 6, 14, 10]), commentCount: null },
      { key: 'q2', type: 'enps', textEn: 'Would you recommend us?', textAr: 'هل توصي بنا؟', driver: null, visible: !hidden, suppressionReason: null, answered: 34, mean: 7.6, favourablePct: null, enps: { score: 17.6, promotersPct: 44.1, passivesPct: 29.4, detractorsPct: 26.5, answered: 34 }, distribution: [], commentCount: null },
      { key: 'q3', type: 'text', textEn: 'One thing to change?', textAr: 'شيء واحد للتغيير؟', driver: null, visible: !hidden, suppressionReason: null, answered: 12, mean: null, favourablePct: null, enps: null, distribution: [], commentCount: 12 },
    ],
    breakdown: hidden ? null : {
      dimension: 'department',
      cells: [
        { value: 'd-fin', label: 'Finance', visible: false, suppressionReason: 'suppressed', respondents: null, favourablePct: null, enps: null, driverFavourablePct: { recognition: null } },
        { value: 'd-ops', label: 'Operations', visible: true, suppressionReason: null, respondents: 23, favourablePct: 67.4, enps: 21.7, driverFavourablePct: { recognition: 73.9 } },
        { value: 'd-leg', label: 'Legal', visible: false, suppressionReason: 'suppressed', respondents: null, favourablePct: null, enps: null, driverFavourablePct: { recognition: null } },
      ],
    },
    suppressedMessage: SUPPRESSED, generatedAtUtc: '2026-10-11T08:00:00Z',
  };
}

interface Options { locale?: 'en' | 'ar'; permissions?: string[]; flagOff?: boolean; alreadySubmitted?: boolean; open?: boolean; scope?: 'team' | 'company' }

async function boot(page: Page, options: Options = {}) {
  const writes: { method: string; path: string; body: any }[] = [];
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript((locale) => {
    localStorage.setItem('zayra_access_token', 'engagement-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, options.locale ?? 'en');
  let draft: unknown[] = [];
  let submitted = false;
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    let body: any = null;
    try { body = request.postDataJSON(); } catch { body = null; }
    if (method !== 'GET' && (p.startsWith('/api/engagement') || p.startsWith('/api/ess/surveys'))) writes.push({ method, path: p, body });

    if (p === '/api/auth/me') {
      return reply({
        id: 'u-1', tenantId: 't-1', tenantSlug: 'eng', fullName: 'Hana HR', roles: ['HR Director'], employeeId: 7,
        permissions: options.permissions ?? ['dashboard.read', 'ess.read', 'ess.write', 'engagement.read', 'engagement.manage', 'engagement.comments.read', 'engagement.actions.manage'],
        accountType: 'SingleCompany', isGroupScope: true, companies: [{ id: 'co-1', name: 'Acme KSA', code: 'co-1', countryCode: 'SA', isActive: true }],
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(options.flagOff ? ['engagement'] : []);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);

    if (p === '/api/engagement/settings') return reply({ minCohortSize: K, minCohortFloor: 3, freeTextRetentionMonths: 24 });
    if (p === '/api/engagement/options') {
      return reply({
        companies: [{ value: 'co-1', labelEn: 'Acme KSA', labelAr: 'أكمي' }],
        departments: [{ value: 'd-ops', labelEn: 'Operations', labelAr: 'العمليات' }, { value: 'd-fin', labelEn: 'Finance', labelAr: 'المالية' }, { value: 'd-leg', labelEn: 'Legal', labelAr: 'الشؤون القانونية' }],
        designations: [{ value: 'g-eng', labelEn: 'Engineer', labelAr: 'مهندس' }],
        grades: [], gradeBands: [], tenureBands: [],
        modes: [{ value: 'Anonymous', noticeEn: 'This survey is anonymous.', noticeAr: 'هذا الاستبيان مجهول الهوية.' }],
      });
    }
    if (p === '/api/engagement/surveys') return reply([survey]);
    if (p === '/api/engagement/templates') return reply([]);
    if (p === `/api/engagement/surveys/${SURVEY}/results`) {
      return reply({ ...results(options.open ? 'open' : url.searchParams.get('designation') ? 'blocked' : 'none'), scopeDimension: options.scope ?? null });
    }
    if (p === `/api/engagement/surveys/${SURVEY}/trend`) return reply([]);
    if (p === `/api/engagement/surveys/${SURVEY}/comments`) {
      return reply({ visible: false, suppressionReason: 'suppressed', minCohortSize: K, message: SUPPRESSED, comments: [] });
    }
    if (p === '/api/engagement/actions') return reply([]);

    if (p === '/api/ess/surveys' && method === 'GET') {
      return reply([{ surveyId: SURVEY, titleEn: 'Q4 pulse', titleAr: 'نبض الربع الرابع', mode: 'Anonymous', closesAtUtc: '2026-10-20T20:00:00Z',
        status: submitted ? 'Submitted' : draft.length ? 'Draft' : 'NotStarted', submittedOn: submitted ? '2026-10-11' : null, hasDraft: draft.length > 0 }]);
    }
    if (p === '/api/ess/surveys/actions') return reply([]);
    if (p === '/api/ess/surveys/open') {
      if (options.alreadySubmitted || submitted) return reply({ error: 'survey_already_submitted', code: 'survey_already_submitted', message: 'You have already submitted this survey. Thank you.' }, 409);
      return reply({
        surveyId: SURVEY, token: 'tok', titleEn: 'Q4 pulse', titleAr: 'نبض الربع الرابع', mode: 'Anonymous', minCohortSize: K,
        privacyNoticeEn: 'This survey is anonymous. Your answers are stored without your name or employee number.',
        privacyNoticeAr: 'هذا الاستبيان مجهول الهوية. تُحفظ إجاباتك بدون اسمك أو رقمك الوظيفي.',
        closesAtUtc: '2026-10-20T20:00:00Z', content, draft,
      });
    }
    if (p === '/api/ess/surveys/draft') { draft = body.answers; return reply({ savedOn: '2026-10-11' }); }
    if (p === '/api/ess/surveys/submit') {
      if (submitted) return reply({ error: 'survey_already_submitted', code: 'survey_already_submitted', message: 'Already submitted.' }, 409);
      submitted = true;
      return reply({ submittedOn: '2026-10-11' });
    }
    return reply(paged([]));
  });
  return { writes, errors };
}

test('results: hidden groups show the suppression sentence, never a number', async ({ page }) => {
  const { errors } = await boot(page);
  await page.goto('/engagement');
  await page.getByRole('tab', { name: 'Results' }).click();
  await expect(page.getByTestId('kpi-response-rate')).toContainText('94.4%');
  await expect(page.getByTestId('kpi-response-rate')).toContainText('34 of 36 invited responded');
  await page.getByTestId('filter-breakdown').selectOption('department');
  const table = page.getByTestId('breakdown-table');
  await expect(table.getByTestId('cell-d-leg')).toContainText(SUPPRESSED);
  await expect(table.getByTestId('cell-d-fin')).toContainText(SUPPRESSED);
  await expect(table.getByTestId('cell-d-ops')).toContainText('67.4%');
  await expect(table.getByTestId('cell-d-leg')).not.toContainText(/\d+(\.\d)?%/);
  expect(errors).toEqual([]);
});

test('results: a blocked intersecting filter explains itself and shows no metric', async ({ page }) => {
  await boot(page);
  await page.goto('/engagement');
  await page.getByRole('tab', { name: 'Results' }).click();
  await page.getByTestId('filter-department').selectOption('d-ops');
  await page.getByTestId('filter-designation').selectOption('g-eng');
  await expect(page.getByTestId('results-suppressed')).toContainText(SUPPRESSED);
  await expect(page.getByTestId('results-suppressed')).toContainText('can be worked out from this or any other result');
  await expect(page.getByTestId('kpi-favourable')).toContainText(SUPPRESSED);
  await expect(page.getByTestId('kpi-favourable')).not.toContainText('64.7');
});

test('comments: a suppressed comment view says why; managers without the permission see no button', async ({ page }) => {
  await boot(page);
  await page.goto('/engagement');
  await page.getByRole('tab', { name: 'Results' }).click();
  await page.getByRole('button', { name: 'Read comments' }).click();
  await expect(page.getByTestId('comments-suppressed')).toContainText(SUPPRESSED);
});

test('a reader without manage or comment rights sees results and actions only', async ({ page }) => {
  await boot(page, { permissions: ['dashboard.read', 'engagement.read'] });
  await page.goto('/engagement');
  await expect(page.getByRole('tab', { name: 'Results' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Surveys' })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'Templates' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Read comments' })).toHaveCount(0);
  await expect(page.getByText('Reading comments needs the engagement comments permission.')).toBeVisible();
});

test('the opt-in flag off: one plain sentence, no half-working screen', async ({ page }) => {
  await boot(page, { flagOff: true });
  await page.goto('/engagement');
  await expect(page.getByText('This feature is not enabled for your account yet.')).toBeVisible();
  await expect(page.getByTestId('engagement-page')).toHaveCount(0);
});

test('respondent: privacy notice verbatim, draft, submit once', async ({ page }) => {
  const { writes } = await boot(page);
  await page.goto('/ess/surveys');
  await page.getByTestId(`my-survey-open-${SURVEY}`).click();
  await expect(page.getByTestId('privacy-notice')).toContainText('Your answers are stored without your name or employee number.');
  await expect(page.getByTestId('respond-submit')).toBeDisabled();
  await page.getByTestId('question-q1').getByText('Agree', { exact: true }).click();
  await page.getByTestId('respond-save-draft').click();
  await expect(page.getByTestId('respond-draft-state')).toContainText('Only you can see it');
  await page.getByTestId('question-q2').getByText('9', { exact: true }).click();
  await page.getByTestId('respond-submit').click();
  await expect(page.getByTestId('survey-thanks')).toBeVisible();
  const submit = writes.find((w) => w.path === '/api/ess/surveys/submit')!;
  expect(submit.body).toEqual({ surveyId: SURVEY, token: 'tok', answers: [
    { questionKey: 'q1', value: 4, choices: null, text: null },
    { questionKey: 'q2', value: 9, choices: null, text: null },
  ] });
  // No identifier of a person anywhere in what the respondent sends.
  expect(JSON.stringify(writes)).not.toMatch(/employee|userId|invitation/i);
});

test('respondent: an already-submitted survey is refused in plain words', async ({ page }) => {
  await boot(page, { alreadySubmitted: true });
  await page.goto('/ess/surveys');
  await page.getByTestId(`my-survey-open-${SURVEY}`).click();
  await expect(page.getByText('You have already submitted this survey. Thank you.')).toBeVisible();
  await expect(page.getByTestId('survey-respond')).toHaveCount(0);
});

test('Arabic RTL: results and the suppression sentence in Arabic', async ({ page }) => {
  await boot(page, { locale: 'ar' });
  await page.goto('/engagement');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByRole('tab', { name: 'النتائج' }).click();
  await page.getByTestId('filter-breakdown').selectOption('department');
  await expect(page.getByTestId('cell-d-leg')).toContainText('لا توجد إجابات كافية لحماية السرية (الحد الأدنى 5).');
});

test('phone 390px Arabic: the respond flow fits and is accessible', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await boot(page, { locale: 'ar' });
  await page.goto('/ess/surveys');
  await page.getByTestId(`my-survey-open-${SURVEY}`).click();
  await expect(page.getByTestId('privacy-notice')).toContainText('مجهول الهوية');
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  const box = await page.getByTestId('question-q1').getByText('أوافق', { exact: true }).boundingBox();
  expect(box!.height).toBeGreaterThanOrEqual(20);
  const result = await new AxeBuilder({ page }).include('[data-testid="survey-respond"]').analyze();
  const serious = result.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')
    .map((v) => `${v.id}: ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}`);
  expect(serious).toEqual([]);
});

test('an open anonymous survey shows only the response rate and when results will appear', async ({ page }) => {
  await boot(page, { open: true });
  await page.goto('/engagement');
  await page.getByRole('tab', { name: 'Results' }).click();
  await expect(page.getByTestId('results-after-close')).toContainText('Results appear when the survey closes on');
  await expect(page.getByTestId('kpi-response-rate')).toContainText('20 of 36 invited responded');
  await expect(page.getByTestId('kpi-favourable')).toHaveCount(0);
  await expect(page.getByTestId('breakdown-table')).toHaveCount(0);
  await expect(page.getByTestId('filter-department')).toBeDisabled();
});

test('at most two dimensions: a third filter is disabled; comments refuse other filters', async ({ page }) => {
  await boot(page);
  await page.goto('/engagement');
  await page.getByRole('tab', { name: 'Results' }).click();
  await page.getByTestId('filter-department').selectOption('d-ops');
  await page.getByTestId('filter-breakdown').selectOption('designation');
  await expect(page.getByTestId('filter-designation')).toBeDisabled();
  await expect(page.getByTestId('filter-tenure')).toBeDisabled();
  await expect(page.getByTestId('filter-limit-hint')).toContainText('Combine at most two');
  await expect(page.getByTestId('comments-scope-note')).toContainText('by Department only');
});

test('a team-scoped reader: the team is one of the two, so one filter OR one breakdown', async ({ page }) => {
  await boot(page, { scope: 'team' });
  await page.goto('/engagement');
  await page.getByRole('tab', { name: 'Results' }).click();
  await expect(page.getByTestId('filter-limit-hint')).toContainText('Your view is limited to your team');
  await page.getByTestId('filter-department').selectOption('d-ops');
  await expect(page.getByTestId('filter-breakdown')).toBeDisabled();
  await expect(page.getByTestId('filter-designation')).toBeDisabled();
});

for (const locale of ['en', 'ar'] as const) {
  test(`results view has no serious accessibility violations (${locale})`, async ({ page }) => {
    await boot(page, { locale });
    await page.goto('/engagement');
    await page.getByRole('tab', { name: locale === 'ar' ? 'النتائج' : 'Results' }).click();
    await page.getByTestId('filter-breakdown').selectOption('department');
    await expect(page.getByTestId('breakdown-table')).toBeVisible();
    const result = await new AxeBuilder({ page }).include('[data-testid="engagement-results"]').analyze();
    const serious = result.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious')
      .map((v) => `${v.id}: ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}`);
    expect(serious).toEqual([]);
  });
}
