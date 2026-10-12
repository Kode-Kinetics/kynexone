import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Merit planning (/compensation) against intercepted API responses — synthetic values only, no backend, no tenant.
 *   npx next dev -p 5313 &
 *   COMPENSATION_BASE_URL=http://127.0.0.1:5313 npx playwright test -c e2e/playwright.compensation.config.ts
 * Proves the UI contract (what each audience sees, what is sent, refusals shown verbatim, Arabic/RTL, 390 px).
 * Server rules, money and SoD are proven by Zayra.Api.Tests/Compensation on real PostgreSQL.
 */

const company = { id: 'co-sa', legalNameEn: 'Synthetic Trading KSA', legalNameAr: '', tradeName: 'Synthetic', countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };

const bench = (state: string, extra: object = {}) => ({ state, setId: null, setName: null, isSynthetic: false, surveyDate: null, payBasis: null, p50Cycle: null, position: null, detail: null, ...extra });
const line = (o: Record<string, unknown>): Record<string, any> => ({
  id: `l-${o.employeeId}`, employeeCode: `E${o.employeeId}`, departmentName: 'Engineering', designationName: 'Software Engineer', gradeName: 'Grade 1',
  payCurrency: 'SAR', fxRate: 1, currentBasic: 6000, currentGross: 8000, currentBasicCycle: 6000, currentGrossCycle: 8000, gradeMin: 8000, gradeMid: 10000, gradeMax: 12000,
  gradeCurrency: 'SAR', compaRatio: 0.8, rangePosition: 0, rating: 'Exceeds', eligibleDays: 365, prorationFactor: 1, guidelineMinPercent: 4, guidelineTargetPercent: 6,
  guidelineMaxPercent: 8, proposedIncrease: 0, proposedPercent: 0, proratedIncrease: 0, proratedIncreasePay: 0, newBasic: 6000, newGross: 8000, newCompaRatio: 0.8,
  outOfGuideline: false, justification: null, flags: [], blockingMessages: [], benchmark: bench('missing'), changeRequestId: null, changeStatus: null, applyBlockedReason: null, ...o,
});

function worksheet(overrides: object = {}) {
  return {
    id: 'ws-a', cycleId: 'cy-1', cycleName: 'FY2026 merit', cycleStatus: 'Planning', currency: 'SAR', effectiveDate: '2026-11-01', managerEmployeeId: 10, managerName: 'Manager Alpha',
    scenarioName: 'Main', isPrimary: true, status: 'Draft', revision: 1, version: 3, overspendJustification: null, approvalRequestId: null, decisionComments: null, submittedAtUtc: null,
    lines: [
      line({ employeeId: 1, employeeName: 'Aisha Synthetic', proposedIncrease: 360, proposedPercent: 6, proratedIncrease: 360, proratedIncreasePay: 360, newBasic: 6360, newGross: 8360, newCompaRatio: 0.836,
        benchmark: bench('stale', { setName: 'SYNTHETIC survey', isSynthetic: true, surveyDate: '2024-01-01', payBasis: 'MonthlyBasic', p50Cycle: 6500, position: 0.9231, detail: 'Survey dated 2024-01-01 is more than 18 months before the effective date.' }), flags: ['stale_benchmark'] }),
      line({ employeeId: 2, employeeName: 'Badr Synthetic', currentBasic: 8000, currentGross: 10500, currentBasicCycle: 8000, currentGrossCycle: 10500, compaRatio: 1.05, rangePosition: 0.625, rating: 'Meets',
        guidelineMinPercent: 1, guidelineTargetPercent: 2, guidelineMaxPercent: 4, proposedIncrease: 400, proposedPercent: 5, proratedIncrease: 400, proratedIncreasePay: 400, newBasic: 8400, newGross: 10900,
        newCompaRatio: 1.09, outOfGuideline: true, flags: ['out_of_guideline', 'missing_benchmark'] }),
      line({ employeeId: 3, employeeName: 'Chen Synthetic', payCurrency: 'AED', fxRate: 1.02, currentBasicCycle: 6120, currentGrossCycle: 8160, compaRatio: 0.816, rangePosition: 0.04, rating: 'Meets',
        guidelineMinPercent: 2, guidelineTargetPercent: 3, guidelineMaxPercent: 5, proposedIncrease: 183.6, proposedPercent: 3, proratedIncrease: 183.6, proratedIncreasePay: 180, newBasic: 6180, newGross: 8180, newCompaRatio: 0.8344, flags: ['missing_benchmark'] }),
    ],
    excluded: [{ employeeId: 4, employeeCode: 'E4', name: 'Dana Synthetic', reason: 'Joined 2026-10-01, after the hire-date cutoff 2026-08-31.' }],
    budgetImpact: [{ departmentId: 'd-eng', departmentName: 'Engineering', hasBudget: true, poolType: 'PercentOfEligibleBasic', poolPercent: 4, fixedAmount: null, eligibleCount: 6, eligibleBasicCycle: 23600, pool: 944, proposed: 943.6, remaining: 0.4, percentUsed: 99.96, overspent: false }],
    proposedTotal: 943.6, overspend: false, canEdit: true, canSubmit: false,
    blockers: [{ code: 'justification_required', message: 'Badr Synthetic: outside the guideline — add a justification.' }],
    scenarios: [{ id: 'ws-a', scenarioName: 'Main', isPrimary: true, status: 'Draft', proposedTotal: 943.6 }],
    ...overrides,
  };
}

function cycle(overrides: object = {}) {
  return {
    id: 'cy-1', companyId: 'co-sa', companyName: 'Synthetic Trading KSA', name: 'FY2026 merit', status: 'Planning', performancePeriodStart: '2025-11-01', performancePeriodEnd: '2026-10-31',
    effectiveDate: '2026-11-01', currency: 'SAR', payComponent: 'Basic', hireDateCutoff: '2026-08-31', eligibleStatuses: ['Active'], includeGradeIds: [], excludeGradeIds: [], includeDepartmentIds: [],
    excludeDepartmentIds: [], minimumEligibleDays: 30, prorationRule: 'EligibleDays', equityIncludesNationality: false, version: 7,
    budgets: [
      { departmentId: 'd-eng', departmentName: 'Engineering', hasBudget: true, poolType: 'PercentOfEligibleBasic', poolPercent: 4, fixedAmount: null, eligibleCount: 6, eligibleBasicCycle: 39620, pool: 1584.8, proposed: 1604.09, remaining: -19.29, percentUsed: 101.22, overspent: true },
      { departmentId: 'd-ops', departmentName: 'Operations', hasBudget: true, poolType: 'FixedAmount', poolPercent: null, fixedAmount: 1500, eligibleCount: 6, eligibleBasicCycle: 46500, pool: 1500, proposed: 1319.5, remaining: 180.5, percentUsed: 87.97, overspent: false },
    ],
    guidelines: [{ id: 'g1', rating: 'Exceeds', compaRatioMin: 0, compaRatioMax: 0.9, minPercent: 4, targetPercent: 6, maxPercent: 8 }],
    fxRates: [{ id: 'fx1', fromCurrency: 'AED', rate: 1.02, source: 'Synthetic reference rate', rateDate: '2026-10-01', enteredByName: 'HR One', enteredAtUtc: '2026-10-02T08:00:00Z' }],
    payCurrencies: ['AED', 'SAR'],
    worksheets: [
      { id: 'ws-a', managerEmployeeId: 10, managerName: 'Manager Alpha', scenarioName: 'Main', isPrimary: true, status: 'Approved', revision: 2, lineCount: 6, proposedTotal: 1604.09, overspend: true, approvalRequestId: 'ap-1', decisionComments: null, version: 9, cycleId: 'cy-1' },
      { id: 'ws-b', managerEmployeeId: 11, managerName: 'Manager Beta', scenarioName: 'Main', isPrimary: true, status: 'Submitted', revision: 1, lineCount: 6, proposedTotal: 1319.5, overspend: false, approvalRequestId: 'ap-2', decisionComments: null, version: 4, cycleId: 'cy-1' },
    ],
    uncovered: [], ineligible: [{ employeeId: 4, employeeCode: 'E4', name: 'Dana Synthetic', reason: 'Joined 2026-10-01, after the hire-date cutoff 2026-08-31.' }],
    blockers: [], totals: { eligibleCount: 12, eligibleBasicCycle: 86120, pool: 3084.8, proposed: 2923.59, remaining: 161.21, percentUsed: 94.77, awardCount: 11 },
    allowedActions: ['budgets', 'fx', 'start_review', 'cancel'], apply: null, approvedAtUtc: null, appliedAtUtc: null, cancelReason: null,
    ...overrides,
  };
}

const benchmarkSet = {
  id: 'bs-1', companyId: 'co-sa', name: 'SYNTHETIC survey', provider: 'Fixture Provider', licenceStatement: 'Synthetic fixture for tests only.', surveyDate: '2024-01-01', geography: 'SA',
  currency: 'SAR', payBasis: 'MonthlyBasic', isSynthetic: true, isStale: true, sourceFileName: 'synthetic-benchmarks.csv', sourceFileSha256: 'a'.repeat(64), rowCount: 2, isActive: true,
  importedByName: 'HR One', importedAtUtc: '2026-10-02T08:00:00Z', mappedRows: 1,
};

type Persona = 'manager' | 'hr';
interface Recorded { method: string; path: string; body: unknown }

async function boot(page: Page, persona: Persona, opts: { locale?: 'en' | 'ar'; submitRefusal?: boolean; cycle?: object } = {}) {
  const writes: Recorded[] = [];
  const errors: string[] = [];
  let ws = worksheet();
  let cy = cycle(opts.cycle ?? {});
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'compensation-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: opts.locale ?? 'en' });
  const perms = persona === 'manager'
    ? ['dashboard.read', 'compensation.plan', 'approvals.read']
    : ['dashboard.read', 'compensation.read', 'compensation.manage', 'compensation.approve', 'compensation.benchmarks.manage', 'compensation.equity.read', 'organization.read'];

  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const p = new URL(request.url()).pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => { try { return request.postDataJSON(); } catch { return null; } })();
    if (method !== 'GET' && p.startsWith('/api/compensation')) writes.push({ method, path: p, body });

    if (p === '/api/auth/me') {
      return reply({
        id: `user-${persona}`, tenantId: 'cmp-tenant', tenantSlug: 'cmp-fixture', fullName: persona === 'manager' ? 'Manager Alpha' : 'HR One', roles: [persona === 'manager' ? 'Manager' : 'HR Manager'],
        permissions: perms, accountType: 'SingleCompany', isGroupScope: false, companies: [{ id: company.id, name: company.legalNameEn, code: 'SA1', countryCode: 'SA', isActive: true }],
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/companies') return reply(paged([company]));
    if (p === '/api/grades') return reply(paged([{ id: 'g-1', code: 'G1', name: 'Grade 1', band: '', level: 1, minSalary: 8000, midSalary: 10000, maxSalary: 12000, currency: 'SAR', isActive: true }]));
    if (p === '/api/departments') return reply(paged([{ id: 'd-eng', code: 'ENG', nameEn: 'Engineering', nameAr: 'الهندسة' }, { id: 'd-ops', code: 'OPS', nameEn: 'Operations', nameAr: 'العمليات' }]));
    if (p === '/api/designations') return reply(paged([{ id: 'ds-1', code: 'SWE', titleEn: 'Software Engineer', titleAr: '' }]));

    const base = '/api/compensation/merit';
    if (p === `${base}/my-worksheets`) return reply({ cycles: [{ id: 'cy-1', companyId: 'co-sa', companyName: company.legalNameEn, name: 'FY2026 merit', status: 'Planning', effectiveDate: '2026-11-01', currency: 'SAR', performancePeriodStart: '2025-11-01', performancePeriodEnd: '2026-10-31', worksheetCount: 2, approvedWorksheetCount: 0, version: 7 }],
      worksheets: [{ id: 'ws-a', managerEmployeeId: 10, managerName: 'Manager Alpha', scenarioName: 'Main', isPrimary: true, status: ws.status, revision: ws.revision, lineCount: 3, proposedTotal: ws.proposedTotal, overspend: false, approvalRequestId: null, decisionComments: null, version: ws.version, cycleId: 'cy-1' }] });
    if (p === `${base}/worksheets/ws-a` && method === 'GET') return reply(ws);
    if (p === `${base}/worksheets/ws-a` && method === 'PUT') {
      const b = body as { version: number; lines: { employeeId: number; justification: string | null }[] };
      if (b.version !== ws.version) return reply({ error: 'conflict', message: 'This worksheet was changed elsewhere. Reload it; your edits were not saved.' }, 409);
      const justified = b.lines.find((l) => l.employeeId === 2)?.justification;
      ws = worksheet({ version: ws.version + 1, blockers: justified ? [] : ws.blockers, canSubmit: !!justified,
        lines: ws.lines.map((l) => (l.employeeId === 2 ? { ...l, justification: justified ?? null } : l)) });
      return reply(ws);
    }
    if (p === `${base}/worksheets/ws-a/submit`) {
      if (opts.submitRefusal) return reply({ error: 'submit_blocked', message: 'The worksheet cannot be submitted yet (1 issue(s)).', blockers: [{ code: 'overspend_justification_required', message: 'This worksheet takes Engineering to 1,604.09 of 1,584.80 SAR. Explain the overspend; it goes to the approvers flagged.' }] }, 422);
      ws = worksheet({ status: 'Submitted', canEdit: false, canSubmit: false, blockers: [], approvalRequestId: 'ap-1', version: ws.version + 1 });
      return reply(ws);
    }
    if (p === `${base}/cycles`) return reply([{ id: 'cy-1', companyId: 'co-sa', companyName: company.legalNameEn, name: 'FY2026 merit', status: 'Planning', effectiveDate: '2026-11-01', currency: 'SAR', performancePeriodStart: '2025-11-01', performancePeriodEnd: '2026-10-31', worksheetCount: 2, approvedWorksheetCount: 1, version: 7 }]);
    if (p === `${base}/cycles/cy-1`) return reply(cy);
    if (p === `${base}/cycles/cy-1/worksheets` && method === 'POST') {
      cy = cycle({ ...(opts.cycle ?? {}), uncovered: [] });
      return reply(worksheet({ id: 'ws-g', managerEmployeeId: 12, managerName: 'Manager Gamma', lines: [] }));
    }
    if (p === `${base}/worksheets/ws-a/assign`) return reply(worksheet());
    if (p === `${base}/awards/al-1/withdraw`) return reply({ lineId: 'al-1', employeeId: 1, employeeName: 'Aisha Synthetic', changeRequestId: 'cr-1', changeStatus: 'Cancelled', blockedReason: 'Withdrawn by HR: x', newBasic: 6360, payCurrency: 'SAR', reversalChangeRequestId: null, reversalStatus: null });
    if (p === `${base}/cycles/cy-1/reopen`) {
      cy = cycle({ ...(opts.cycle ?? {}), version: 8, worksheets: (cy.worksheets as { status: string }[]).map((w) => ({ ...w, status: 'Returned' })) });
      return reply(cy);
    }
    if (p === `${base}/cycles/cy-1/start-review`) return reply({ error: 'worksheets_open', message: '1 worksheet(s) are not approved yet. Every manager’s worksheet must be approved before the cycle goes to review.', blockers: [] }, 422);
    if (p === `${base}/benchmarks`) return reply([benchmarkSet]);
    if (p === `${base}/benchmarks/bs-1`) return reply({ set: benchmarkSet, rows: [
      { id: 'br-1', jobFamily: 'Engineering', level: 'L1', sampleSize: 40, p25: 5800, p50: 6500, p75: 7200, p90: 8000, designationId: 'ds-1', gradeId: null },
      { id: 'br-2', jobFamily: 'Operations', level: 'L1', sampleSize: 25, p25: 6900, p50: 7400, p75: 8100, p90: 8800, designationId: null, gradeId: null },
    ] });
    if (p === `${base}/cycles/cy-1/equity`) return reply({ cycleId: 'cy-1', currency: 'SAR', minimumCohort: 5, excludedNoGrade: 0, excludedNoSalary: 0, notice: '', nationalityCohorts: [], cohorts: [
      { gradeName: 'Grade 1', designationName: 'Software Engineer', suppressed: false, countLabel: '5 or more', gapPercent: null, projectedGapPercent: null, dimension: 'gender',
        groups: [{ group: 'Female', suppressed: true, count: null, meanCompaRatio: null, medianCompaRatio: null, meanGrossCycle: null, projectedMeanCompaRatio: null }, { group: 'Male', suppressed: true, count: null, meanCompaRatio: null, medianCompaRatio: null, meanGrossCycle: null, projectedMeanCompaRatio: null }] },
      { gradeName: 'Grade 2', designationName: 'Operations Analyst', suppressed: false, countLabel: '12', gapPercent: 0, projectedGapPercent: -0.5, dimension: 'gender',
        groups: [{ group: 'Female', suppressed: false, count: 6, meanCompaRatio: 0.6667, medianCompaRatio: 0.63, meanGrossCycle: 10000, projectedMeanCompaRatio: 0.67 }, { group: 'Male', suppressed: false, count: 6, meanCompaRatio: 0.6667, medianCompaRatio: 0.65, meanGrossCycle: 10000, projectedMeanCompaRatio: 0.6667 }] },
    ] });
    return reply(paged([]));
  });
  await page.goto('/compensation');
  const ar = opts.locale === 'ar';
  await expect(page.getByRole('heading', { name: ar ? 'تخطيط زيادات الأداء' : 'Merit planning', exact: true }).first()).toBeVisible({ timeout: 90_000 });
  return { writes, errors };
}

async function shot(page: Page, name: string) {
  const dir = process.env.COMPENSATION_SHOTS_DIR;
  if (!dir) return;
  await page.screenshot({ path: `${dir}/${name}.png`, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
}

test('a manager sees only their worksheet, with guideline, range, market and proration, and must justify an out-of-guideline line', async ({ page }) => {
  const { writes, errors } = await boot(page, 'manager');
  await expect(page.getByRole('tab', { name: 'My worksheets' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Merit cycles' })).toHaveCount(0);
  const sheet = page.getByTestId('worksheet');
  await expect(sheet.getByRole('heading', { name: 'Merit worksheet: Manager Alpha' })).toBeVisible();
  const aisha = sheet.locator('tr', { hasText: 'Aisha Synthetic' });
  await expect(aisha).toContainText('Guideline 4.00% – 8.00%, target 6.00%');
  await expect(aisha).toContainText('Stale market data');
  await expect(aisha).toContainText('Synthetic — not market data');
  await expect(sheet.locator('tr', { hasText: 'Chen Synthetic' })).toContainText('AED');
  await expect(sheet.getByTestId('blockers')).toContainText('Badr Synthetic: outside the guideline');
  await shot(page, 'cmp-manager-worksheet');

  // Justify, save, submit — the sent payload carries every line and the justification.
  await sheet.getByLabel('Justification for Badr Synthetic').fill('Retention risk: competing offer on file.');
  await sheet.getByRole('button', { name: 'Save worksheet' }).click();
  await expect(sheet.getByRole('status')).toHaveText('Worksheet saved.');
  const put = writes.find((w) => w.method === 'PUT')!.body as { version: number; lines: { employeeId: number; proposedIncrease: number | null; justification: string | null }[] };
  expect(put.version).toBe(3);
  expect(put.lines).toHaveLength(3);
  expect(put.lines.find((l) => l.employeeId === 2)!.justification).toBe('Retention risk: competing offer on file.');
  expect(put.lines.find((l) => l.employeeId === 1)!.proposedIncrease).toBe(360);
  await sheet.getByRole('button', { name: 'Submit for approval' }).click();
  await expect(sheet.getByText('This worksheet is locked while it waits for approval in Approvals.', { exact: false })).toBeVisible();
  await expect(sheet.getByRole('button', { name: 'Save worksheet' })).toHaveCount(0);
  await shot(page, 'cmp-manager-submitted');
  expect(errors).toEqual([]);
});

test('a refused submission shows the server’s blockers verbatim', async ({ page }) => {
  await boot(page, 'manager', { submitRefusal: true });
  const sheet = page.getByTestId('worksheet');
  await sheet.getByLabel('Justification for Badr Synthetic').fill('Retention.');
  await sheet.getByRole('button', { name: 'Submit for approval' }).click();
  await expect(sheet.getByRole('alert').filter({ hasText: 'The worksheet cannot be submitted yet' })).toBeVisible();
  await expect(sheet.getByTestId('blockers')).toContainText('This worksheet takes Engineering to 1,604.09 of 1,584.80 SAR.');
});

test('HR sees the cycle: one next action, budgets with definitions, worksheets, FX provenance; a refusal is shown', async ({ page }) => {
  const { errors } = await boot(page, 'hr');
  await page.getByRole('tab', { name: 'Merit cycles' }).click();
  const detail = page.getByTestId('cycle-detail');
  await expect(detail.getByRole('heading', { name: 'FY2026 merit' })).toBeVisible();
  await expect(detail.getByTestId('cycle-totals')).toContainText('Sum of prorated monthly increases on the submitted scenarios.');
  const eng = detail.getByTestId('budget-table').locator('tr[data-department="Engineering"]');
  await expect(eng).toContainText('Over budget');
  await expect(eng).toContainText('101.22%');
  await expect(detail.getByTestId('fx-table').getByLabel('Source for AED')).toHaveValue('Synthetic reference rate');
  await expect(detail.getByTestId('fx-table')).toContainText('HR One');
  await expect(detail.getByTestId('cycle-worksheets')).toContainText('Manager Beta');
  await shot(page, 'cmp-hr-cycle');
  await detail.getByTestId('cycle-next-action').click();
  await expect(detail.getByRole('alert').filter({ hasText: 'not approved yet' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('benchmarks carry provenance and a synthetic set is bannered; equity hides small groups and explains its formulas', async ({ page }) => {
  await boot(page, 'hr');
  await page.getByRole('tab', { name: 'Pay benchmarks' }).click();
  await expect(page.getByTestId('benchmark-sets')).toContainText('Fixture Provider');
  await expect(page.getByTestId('benchmark-sets')).toContainText('Older than 18 months');
  await page.getByRole('button', { name: 'Open set SYNTHETIC survey' }).click();
  await expect(page.getByTestId('synthetic-banner')).toContainText('Synthetic sample — not market data');
  await shot(page, 'cmp-benchmarks-synthetic');
  await page.getByRole('tab', { name: 'Pay equity' }).click();
  await expect(page.getByTestId('equity-notice')).toContainText('Authorised use only.');
  const g1 = page.getByTestId('equity-gender').locator('tr[data-cohort="Grade 1-Software Engineer"]');
  await expect(g1.first()).toContainText('Hidden: fewer than 5 people.');
  await expect(page.getByTestId('equity-gender')).toContainText('0.6667');
  await shot(page, 'cmp-equity');
});

test('Arabic: the page is right-to-left and translated', async ({ page }) => {
  await boot(page, 'manager', { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByRole('tab', { name: 'أوراق عملي' })).toBeVisible();
  await expect(page.getByTestId('worksheet').getByRole('heading', { name: 'ورقة زيادات الأداء: Manager Alpha' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'إرسال للموافقة' })).toBeVisible();
  await shot(page, 'cmp-manager-ar-rtl');
});

test('390 px: no horizontal page scroll; wide tables scroll inside their own box', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await boot(page, 'manager');
  await expect(page.getByTestId('worksheet')).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await shot(page, 'cmp-manager-390');
});

test('no serious accessibility violations on the worksheet', async ({ page }) => {
  await boot(page, 'manager');
  await expect(page.getByTestId('worksheet')).toBeVisible();
  const result = await new AxeBuilder({ page }).include('[data-testid="worksheet"]').withTags(['wcag2a', 'wcag2aa']).analyze();
  expect(result.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious').map((v) => `${v.id}: ${v.nodes.length}`)).toEqual([]);
});

test('HR closes coverage gaps, sees the retro cost of a past date, and reopens planning with a reason', async ({ page }) => {
  const { writes, errors } = await boot(page, 'hr', { cycle: {
    worksheets: [
      { id: 'ws-a', managerEmployeeId: 10, managerName: 'Manager Alpha', scenarioName: 'Main', isPrimary: true, status: 'Returned', revision: 2, lineCount: 6, proposedTotal: 1604.09, overspend: false, approvalRequestId: null, decisionComments: 'The approval was closed without a decision.', version: 9, cycleId: 'cy-1' },
      { id: 'ws-b', managerEmployeeId: 11, managerName: 'Manager Beta', scenarioName: 'Main', isPrimary: true, status: 'Submitted', revision: 1, lineCount: 6, proposedTotal: 1319.5, overspend: false, approvalRequestId: 'ap-2', decisionComments: null, version: 4, cycleId: 'cy-1' },
    ],
    uncovered: [{ employeeId: 21, employeeCode: 'E21', name: 'Eman Synthetic', reason: 'Manager Gamma has no worksheet in this cycle (they were assigned after planning opened).', ownerEmployeeId: 12, ownerName: 'Manager Gamma', ownerCanBeOpened: true }],
    retro: { monthsInPast: 2, paidPeriods: ['2026-08', '2026-09'], monthlyDeltaCycle: 943.6, retroCostCycle: 1887.2, acknowledgementRequired: true },
    allowedActions: ['start_review', 'reopen', 'cancel'],
  } });
  await page.getByRole('tab', { name: 'Merit cycles' }).click();
  const detail = page.getByTestId('cycle-detail');
  await expect(detail.getByTestId('cycle-retro')).toContainText('2026-08, 2026-09');
  await expect(detail.getByTestId('cycle-retro')).toContainText('The approver must acknowledge this cost');
  const eman = detail.getByTestId('cycle-uncovered').locator('li[data-employee="Eman Synthetic"]');
  await expect(eman).toContainText('Manager Gamma has no worksheet');
  // Assign to the one editable primary worksheet (Manager Alpha's, Returned); Beta's is submitted and not offered.
  await eman.getByLabel('Assign Eman Synthetic to a worksheet').selectOption({ label: 'Manager Alpha' });
  await expect(eman.getByRole('option', { name: 'Manager Beta' })).toHaveCount(0);
  await eman.getByRole('button', { name: 'Assign Eman Synthetic' }).click();
  await expect.poll(() => writes.find((w) => w.path.endsWith('/worksheets/ws-a/assign'))?.body).toEqual({ employeeId: 21 });
  await eman.getByRole('button', { name: 'Open a worksheet for Manager Gamma' }).click();
  await expect.poll(() => writes.find((w) => w.path.endsWith('/cycles/cy-1/worksheets'))?.body).toEqual({ managerEmployeeId: 12 });
  await expect(detail.getByTestId('cycle-uncovered')).toHaveCount(0);
  await shot(page, 'cmp-coverage-retro');
  await detail.getByTestId('cycle-reopen').click();
  const dialog = page.getByRole('dialog', { name: 'Reopen planning?' });
  await expect(dialog).toContainText('Budgets and exchange rates can then be changed.');
  await expect(dialog.getByRole('button', { name: 'Reopen planning' })).toBeDisabled();
  await dialog.getByLabel('Reason').fill('Budget for Engineering corrected');
  await dialog.getByRole('button', { name: 'Reopen planning' }).click();
  await expect.poll(() => writes.find((w) => w.path.endsWith('/cycles/cy-1/reopen'))?.body).toEqual({ version: 7, reason: 'Budget for Engineering corrected', acknowledgeRetroCost: false });
  await expect(detail.getByTestId('cycle-worksheets').locator('tr[data-manager="Manager Beta"]')).toContainText('Returned');
  expect(errors).toEqual([]);
});

test('HR withdraws one scheduled award with a reason; an award in pay offers a reversal instead', async ({ page }) => {
  const line = (id: string, employeeId: number, name: string, changeStatus: string) => ({ lineId: id, employeeId, employeeName: name, changeRequestId: `cr-${id}`, changeStatus, blockedReason: null, newBasic: 6360, payCurrency: 'SAR', reversalChangeRequestId: null, reversalStatus: null });
  const { writes, errors } = await boot(page, 'hr', { cycle: {
    status: 'Applied', allowedActions: ['cancel'], approvedAtUtc: '2026-10-05T08:00:00Z', appliedAtUtc: '2026-10-06T08:00:00Z',
    apply: { awards: 2, scheduled: 1, applied: 1, returnedForReview: 0, blocked: 0, cancelled: 0,
      lines: [line('al-1', 1, 'Aisha Synthetic', 'ApprovedPendingEffectiveDate'), line('al-2', 2, 'Badr Synthetic', 'ApprovedApplied')] },
  } });
  await page.getByRole('tab', { name: 'Merit cycles' }).click();
  const results = page.getByTestId('apply-results');
  await expect(results.locator('tr[data-employee="Badr Synthetic"]').getByRole('button', { name: 'Withdraw award of Badr Synthetic' })).toHaveCount(0);
  await expect(results.getByRole('button', { name: 'Reverse award of Badr Synthetic' })).toBeVisible();
  await results.getByRole('button', { name: 'Withdraw award of Aisha Synthetic' }).click();
  const dialog = page.getByRole('dialog', { name: 'Withdraw this award?' });
  await expect(dialog).toContainText('the rest of the cycle stays scheduled');
  await expect(dialog.getByRole('button', { name: 'Withdraw the award' })).toBeDisabled();
  await dialog.getByLabel('Reason').fill('Performance concern raised after approval');
  await dialog.getByRole('button', { name: 'Withdraw the award' }).click();
  await expect.poll(() => writes.find((w) => w.path.endsWith('/awards/al-1/withdraw'))?.body).toEqual({ reason: 'Performance concern raised after approval' });
  expect(errors).toEqual([]);
});
