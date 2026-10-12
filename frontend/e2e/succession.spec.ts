import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Succession screens (SUC) against synthetic intercepted API responses — no backend, no real tenant.
 *   npx next dev -p 5317 &
 *   SUCCESSION_BASE_URL=http://127.0.0.1:5317 npx playwright test -c e2e/playwright.succession.config.ts
 * Proves the UI contract (gaps first, every figure drills to exactly its rows, 9-box inputs and RTL mirroring, the slate
 * editor and the return-for-revision reason, permission-shaped views, the feature flag, Arabic, a 390px phone, axe),
 * not server authorization — that is the backend succession tests and the real-backend journey.
 */

const SUC_ALL = ['succession.read', 'succession.positions.manage', 'succession.pools.manage', 'succession.nominate', 'succession.assess',
  'succession.calibrate', 'succession.slates.read', 'succession.slates.manage', 'succession.devplans.read', 'succession.devplans.manage',
  'succession.reports.read', 'succession.self'];
const HR_PERMS = ['dashboard.read', 'employees.read', 'employees.write', 'approvals.read', 'approvals.decide', 'ess.read', ...SUC_ALL];
const MANAGER_PERMS = ['dashboard.read', 'employees.read', 'manager.read', 'ess.read', 'succession.nominate', 'succession.devplans.read',
  'succession.devplans.manage', 'succession.self'];
const EMP_PERMS = ['dashboard.read', 'profile.read', 'ess.read', 'ess.write', 'succession.self'];

const person = (id: number, nameEn: string, nameAr: string, over: Record<string, unknown> = {}) => ({
  employeeId: id, employeeCode: `E-${id}`, nameEn, nameAr, status: 'Active', hasLeft: false, ...over,
});
const LIU = person(7, 'Liu Wei', 'ليو وي');
const SARA = person(9, 'Sara Ahmadi', 'سارة أحمدي');
const OMAR = person(8, 'Omar Siddiqui', 'عمر صديقي');
const ACCOUNTABLE = person(2, 'Noura Al-Saud', 'نورة آل سعود');

const slateRef = (id: string, versionNo: number, status: string) => ({ id, versionNo, status, submittedAtUtc: '2026-09-01T08:00:00Z', approvedAtUtc: status === 'Approved' ? '2026-09-02T08:00:00Z' : null, effectiveFrom: null });

const position = (id: string, title: string, over: Record<string, unknown> = {}) => ({
  id, positionId: `p-${id}`, positionCode: `POS-${id}`, positionTitle: title, companyId: 'co-1', companyName: 'IntelliFlow', departmentName: 'Finance',
  riskOfLoss: 'High', impactOfVacancy: 'Critical', rationale: 'Signs off the group accounts every quarter.', accountable: ACCOUNTABLE, incumbent: OMAR,
  vacant: false, status: 'Active', effectiveFrom: '2026-01-01', retiredAtUtc: null, retiredReason: null, liveNominations: 2, readyNow: 0,
  oneToTwoYears: 1, threePlusYears: 0, approvedSlate: slateRef('s-1', 1, 'Approved'), openSlate: null, gaps: ['NoReadyNow', 'HighRisk'],
  needsReviewCount: 0, version: 1, ...over,
});
const CFO = position('cp-1', 'Chief Financial Officer');
const COO = position('cp-2', 'Chief Operating Officer', { gaps: ['VacantNoReadyNow', 'NoApprovedSlate', 'NoReadyNow'], vacant: true, incumbent: null, approvedSlate: null, riskOfLoss: 'Medium', impactOfVacancy: 'High' });
const HRD = position('cp-3', 'HR Director', { gaps: [], readyNow: 1, riskOfLoss: 'Low', impactOfVacancy: 'Medium' });

const metric = (key: string, value: number, drill: string, numerator: number | null = null, denominator: number | null = null) =>
  ({ key, value, numerator, denominator, definition: 'server words', drill });
const SUMMARY = { asOf: '2026-10-11', activePositions: 3, metrics: [
  metric('positions', 3, 'positions'),
  metric('coverage', 33.3, 'coverage', 1, 3),
  metric('extendedCoverage', 66.7, 'extendedCoverage', 2, 3),
  metric('benchReadyNow', 1, 'bench:ReadyNow'),
  metric('benchOneToTwoYears', 1, 'bench:OneToTwoYears'),
  metric('benchThreePlusYears', 0, 'bench:ThreePlusYears'),
  metric('gapNoApprovedSlate', 1, 'gap:NoApprovedSlate'),
  metric('gapNoReadyNow', 2, 'gap:NoReadyNow'),
  metric('gapStaleSlate', 0, 'gap:StaleSlate'),
  metric('gapVacantNoReadyNow', 1, 'gap:VacantNoReadyNow'),
  metric('gapHighRisk', 1, 'gap:HighRisk'),
  metric('plansActive', 1, 'plans:current'),
  metric('plansOnTrack', 0, 'plans:onTrack', 0, 1),
  metric('plansOffTrack', 1, 'plans:offTrack'),
  metric('actionsOverdue', 1, 'plans:overdue'),
  metric('actionsCompletedRate', 50, 'plans:current', 1, 2),
] };

const assessment = (n: number, emp: ReturnType<typeof person>, over: Record<string, unknown> = {}) => ({
  id: `as-${n}`, employee: emp, reviewPeriod: '2026-H2', status: 'Submitted', assessor: ACCOUNTABLE, proposedPotential: 3,
  potentialEvidence: 'Led the Riyadh ERP cut-over and the audit remediation.', appraisalReviewId: `rv-${n}`, appraisalCycleName: '2026 annual',
  performanceScore: 82, performanceBand: 'High', bandRuleVersion: 'perf-band-75-60-v1', calibratedPotential: null, calibrationReason: null,
  calibratedBy: null, calibratedAtUtc: null, nineBoxCell: 'P3-High', cellLabelEn: 'Future leader', cellLabelAr: 'قائد المستقبل', supersedesId: null,
  createdAtUtc: '2026-10-01T08:00:00Z', anonymised: false, version: 1, ...over,
});

const nomination = (id: string, emp: ReturnType<typeof person>, status: string, over: Record<string, unknown> = {}) => ({
  id, target: { kind: 'position', id: 'cp-1', title: 'Chief Financial Officer', titleAr: null }, candidate: emp, status, readiness: 'OneToTwoYears',
  evidence: 'Runs month-end close for three entities.', competencyEvidence: [], nominatedBy: ACCOUNTABLE, nominatedAtUtc: '2026-09-01T08:00:00Z',
  readinessPrevious: null, readinessChangedAtUtc: null, readinessReason: null, needsReview: false, reviewReason: null, exitReason: null, exitNote: null,
  exitedAtUtc: null, currentAssessment: { id: 'as-1', reviewPeriod: '2026-H2', status: 'Calibrated', proposedPotential: 3, calibratedPotential: 3,
    performanceBand: 'High', performanceScore: 82, nineBoxCell: 'P3-High' }, readyNowOnApprovedSlates: 0, overCommitted: false, version: 3, ...over,
});

const NOMINATIONS = [
  nomination('n-1', LIU, 'Calibrated'),
  nomination('n-2', SARA, 'InReview', { readiness: 'ReadyNow', needsReview: true, reviewReason: 'Transferred to Operations', overCommitted: true, readyNowOnApprovedSlates: 3 }),
  nomination('n-3', OMAR, 'Withdrawn', { exitNote: 'Appointed elsewhere' }),
];

const placement = (emp: ReturnType<typeof person>, cell: string, band: string, over: Record<string, unknown> = {}) => ({
  assessmentId: `pl-${emp.employeeId}`, employee: emp, status: 'Calibrated', provisional: false, nineBoxCell: cell, potential: 3, proposedPotential: 2,
  calibratedPotential: 3, calibrationReason: 'Calibrated up after the cross-company review panel.', performanceBand: band, performanceScore: 82,
  appraisalReviewId: 'rv-777', bandRuleVersion: 'perf-band-75-60-v1', assessor: ACCOUNTABLE, reviewPeriod: '2026-H2', ...over,
});
const cell = (code: string, people: unknown[] = []) => {
  const [p, band] = code.replace('P', '').split('-');
  return { code, potential: Number(p), band, labelEn: code, labelAr: code, people };
};
const NINE_BOX = {
  reviewPeriod: '2026-H2', ruleVersion: 'suc-9box-v1', bandRule: { highFrom: 75, moderateFrom: 60, version: 'perf-band-75-60-v1', source: 'default' },
  cells: ['P3-Low', 'P3-Moderate', 'P3-High', 'P2-Low', 'P2-Moderate', 'P2-High', 'P1-Low', 'P1-Moderate', 'P1-High']
    .map((c) => cell(c, c === 'P3-High' ? [placement(LIU, 'P3-High', 'High')] : [])),
  unrated: [placement(SARA, '', 'Unrated', { performanceScore: null, appraisalReviewId: null, provisional: true, calibratedPotential: null, calibrationReason: null })],
  notAssessed: 0,
};

const snapshot = {
  schema: 1, criticalPositionId: 'cp-1', positionId: 'p-cp-1', positionCode: 'POS-cp-1', positionTitle: 'Chief Financial Officer', companyId: 'co-1',
  riskOfLoss: 'High', impactOfVacancy: 'Critical', incumbent: { ...OMAR },
  candidates: [{ order: 1, rationale: 'Ran the region close for two years.', nominationId: 'n-1', employeeId: 7, employeeCode: 'E-7', nameEn: 'Liu Wei',
    nameAr: 'ليو وي', readiness: 'OneToTwoYears', assessmentId: 'as-1', reviewPeriod: '2026-H2', nineBoxCell: 'P3-High', performanceBand: 'High',
    performanceScore: 82, appraisalReviewId: 'rv-777', bandRuleVersion: 'perf-band-75-60-v1', proposedPotential: 2, calibratedPotential: 3,
    calibrationReason: 'Calibrated up after the cross-company review panel.', assessorEmployeeId: 2, calibratedAtUtc: '2026-10-02T08:00:00Z' }],
  rules: { nineBox: 'suc-9box-v1', readinessScale: 'suc-readiness-v1', ordering: 'manual-order-with-rationale-v1' }, builtAtUtc: '2026-09-01T08:00:00Z',
};
const slate = (id: string, versionNo: number, status: string, over: Record<string, unknown> = {}) => ({
  id, criticalPositionId: 'cp-1', versionNo, status, snapshot, snapshotSha256: 'ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56abcd',
  hashVerified: true, approvalRequestId: status === 'Submitted' ? 'ar-1' : null, approvalStatus: null, submittedAtUtc: '2026-09-01T08:00:00Z',
  approvedAtUtc: status === 'Approved' ? '2026-09-02T08:00:00Z' : null, effectiveFrom: null, supersededAtUtc: null, returnedAtUtc: null,
  returnReason: null, cancelledAtUtc: null, cancelReason: null, revisedFromId: null, closedSinceApproval: [], anonymised: false, readOnly: false,
  version: 1, ...over,
});

const action = (id: string, over: Record<string, unknown> = {}) => ({
  id, actionType: 'Experience', title: 'Lead the Q4 budget round', description: null, status: 'InProgress', dueOn: '2026-09-30', isOverdue: true,
  owner: LIU, mentor: null, learningAssignmentId: null, learningPathName: null, learningPathNameAr: null, learningStatus: null, employeeGoalId: null,
  goalTitle: null, goalStatus: null, progressNote: null, completedAtUtc: null, completionMethod: null, sortOrder: 1, version: 2, ...over,
});
const PLAN = {
  id: 'dp-1', employee: LIU, owner: ACCOUNTABLE, purpose: 'Prepare for the CFO role within two years.', status: 'Active', nextReviewOn: '2026-12-01',
  lastReviewedAtUtc: null, lastReviewNote: null, activatedAtUtc: '2026-09-01T08:00:00Z', completedAtUtc: null, cancelledAtUtc: null, cancelReason: null,
  nominationId: 'n-1', progress: { total: 2, completed: 1, cancelled: 0, overdue: 1, percentComplete: 50, onTrack: false, reviewOverdue: false },
  actions: [
    action('ac-1'),
    action('ac-2', { actionType: 'Learning', title: 'Finance leadership path', status: 'Completed', isOverdue: false, learningAssignmentId: 'la-1',
      learningPathName: 'Finance leadership', learningPathNameAr: 'قيادة المالية', learningStatus: 'Completed', completedAtUtc: '2026-10-01T08:00:00Z',
      completionMethod: 'LinkedLearningCompleted', sortOrder: 2 }),
  ],
  createdAtUtc: '2026-09-01T08:00:00Z', version: 4,
};

interface Options { perms?: string[]; locale?: 'en' | 'ar'; successionOff?: boolean; employeeId?: number; visibility?: boolean; noPlan?: boolean }

async function boot(page: Page, options: Options = {}) {
  const errors: string[] = [];
  const writes: Array<{ method: string; path: string; body: unknown }> = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'succession-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });

  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const p = new URL(request.url()).pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    let body: unknown = null;
    try { body = request.postDataJSON(); } catch { body = request.postData(); }
    if (method !== 'GET' && (p.includes('/succession') || p.includes('/development-plan') || p.includes('/approval-requests'))) writes.push({ method, path: p, body });

    if (p === '/api/auth/me') {
      return reply({
        id: 'u-1', tenantId: 't-1', tenantSlug: 'intelliflow', fullName: 'Fixture User', roles: ['HR Director'], permissions: options.perms ?? HR_PERMS,
        accountType: 'SingleCompany', isGroupScope: true, employeeId: options.employeeId ?? null,
        companies: [{ id: 'co-1', name: 'IntelliFlow', code: 'IFL', countryCode: 'SA', isActive: true }],
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(options.successionOff ? ['release_a', 'succession'] : ['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);

    // ── Workspace ──
    if (p === '/api/succession/reports/summary') return reply(SUMMARY);
    if (p === '/api/succession/reports/drill') {
      const m = new URL(request.url()).searchParams.get('metric');
      if (m === 'gap:NoReadyNow') return reply({ metric: m, kind: 'positions', positions: [CFO, COO], candidates: [], plans: [] });
      if (m === 'coverage') return reply({ metric: m, kind: 'positions', positions: [HRD], candidates: [], plans: [] });
      return reply({ metric: m, kind: 'positions', positions: [], candidates: [], plans: [] });
    }
    if (p === '/api/succession/reports/trend') {
      return reply([
        { monthEnd: '2026-08-31', activePositions: 2, withReadyNow: 0, withReadyNowOrOneToTwo: 1, coverage: 0, extendedCoverage: 50 },
        { monthEnd: '2026-09-30', activePositions: 3, withReadyNow: 1, withReadyNowOrOneToTwo: 2, coverage: 33.3, extendedCoverage: 66.7 },
        { monthEnd: '2026-10-11', activePositions: 3, withReadyNow: 1, withReadyNowOrOneToTwo: 2, coverage: 33.3, extendedCoverage: 66.7 },
      ]);
    }
    if (p === '/api/succession/positions/mine') return reply([]);
    if (p === '/api/succession/positions/available') return reply([{ id: 'p-9', code: 'CTO', title: 'Chief Technology Officer', companyId: 'co-1', companyName: 'IntelliFlow', incumbent: null }]);
    if (p === '/api/succession/positions' && method === 'GET') return reply([HRD, CFO, COO]);
    if (p === '/api/succession/positions' && method === 'POST') return reply(position('cp-9', 'Chief Technology Officer'));
    if (p === '/api/succession/people') return reply([LIU, SARA]);
    if (p === '/api/succession/positions/cp-1') return reply({ position: CFO, viaRelationship: false });
    if (p === '/api/succession/positions/cp-1/slates' && method === 'GET') return reply([slate('s-1', 1, 'Approved')]);
    if (p === '/api/succession/positions/cp-1/slates' && method === 'POST') return reply(slate('s-2', 2, 'Draft'));
    if (p === '/api/succession/nominations' && method === 'GET') {
      const status = new URL(request.url()).searchParams.get('status');
      return reply(status === 'InReview' ? NOMINATIONS.filter((n) => n.status === 'InReview') : NOMINATIONS);
    }
    if (p === '/api/succession/nominations/n-2/readiness') return reply(NOMINATIONS[1]);
    if (p === '/api/succession/nominations/n-1/history') {
      return reply([{ action: 'succession.nomination.created', atUtc: '2026-09-01T08:00:00Z', userId: null, metadata: null },
        { action: 'succession.nomination.calibrated', atUtc: '2026-10-02T08:00:00Z', userId: null, metadata: '{"from":null,"to":"OneToTwoYears"}' }]);
    }
    if (p === '/api/succession/nominations/mine') {
      return reply([{ id: 'n-1', targetKind: 'position', targetTitle: 'Chief Financial Officer', candidate: LIU, stage: 'InReview',
        nominatedAtUtc: '2026-09-01T08:00:00Z', closedNote: null, canWithdraw: true }]);
    }
    if (p === '/api/succession/nine-box') return reply(NINE_BOX);
    if (p === '/api/succession/assessments/periods') return reply(['2026-H2']);
    if (p === '/api/succession/assessments' && method === 'GET') return reply([assessment(1, LIU)]);
    if (p === '/api/succession/assessments/as-1/calibrate') {
      return reply({ error: 'reviewer_conflict', message: 'The candidate is in your management chain.', details: { rule: 'manager_chain' } }, 409);
    }
    if (p === '/api/succession/pools') return reply([{ id: 'tp-1', companyId: 'co-1', companyName: 'IntelliFlow', code: 'FIN-LEAD', nameEn: 'Finance leaders',
      nameAr: 'قادة المالية', purpose: 'Grow the next finance leadership bench.', owner: ACCOUNTABLE, status: 'Active', liveMembers: 2, version: 1 }]);
    if (p === '/api/succession/slates/s-5') return reply(slate('s-5', 2, 'Submitted'));
    if (p === '/api/approval-requests/ar-1' && method === 'GET') return reply({ id: 'ar-1', status: 'Pending', canDecide: true, decisionBlockedReason: null });
    if (p === '/api/approval-requests/ar-1/decisions') return reply({ id: 'ar-1', status: 'Rejected' });
    if (p === '/api/succession/development-plans' && method === 'GET') return reply([PLAN]);

    // ── Employee ──
    if (p === '/api/ess/development-plan') {
      return reply(options.noPlan
        ? { current: null, history: [], candidateVisibility: false, memberships: [] }
        : { current: { ...PLAN, nominationId: null }, history: [], candidateVisibility: !!options.visibility,
          memberships: options.visibility ? [{ kind: 'pool', title: 'Finance leaders', titleAr: 'قادة المالية', stage: 'Member', readiness: null }] : [] });
    }
    if (p.startsWith('/api/ess/development-plan/actions/')) return reply({ current: { ...PLAN, nominationId: null }, history: [], candidateVisibility: false, memberships: [] });
    return reply(paged([]));
  });
  return { errors, writes };
}

/** Full-page evidence screenshots, only when SUCCESSION_SHOTS_DIR is set. */
async function shot(page: Page, name: string) {
  const dir = process.env.SUCCESSION_SHOTS_DIR;
  if (!dir) return;
  await page.screenshot({ path: `${dir}/${name}.png`, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
}

async function noSideways(page: Page, label: string) {
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth), label).toBeLessThanOrEqual(1);
}

async function axeSerious(page: Page, selector: string) {
  const results = await new AxeBuilder({ page }).include(selector).analyze();
  return results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`);
}

test.describe('Succession workspace', () => {
  test('gaps first: every figure says what it counts, and drills to exactly its rows', async ({ page }) => {
    const { errors } = await boot(page);
    await page.goto('/performance/succession');
    await expect(page.getByTestId('suc-workspace')).toBeVisible();
    const tile = page.getByTestId('suc-kpi-gapNoReadyNow');
    await tile.focus();
    await expect(tile.getByRole('tooltip')).toBeVisible();
    await expect(tile.getByRole('tooltip')).toHaveText('Active critical positions with no Ready-now candidate on the approved slate.');
    await tile.click();
    await expect(page.getByTestId('suc-drill')).toBeVisible();
    await expect(page.getByTestId('suc-drill-row')).toHaveCount(2);
    await expect(page.getByTestId('suc-drill-count')).toHaveAttribute('data-count', '2');
    // A rate drills to its numerator: 33.3% coverage = 1 of 3 = 1 row.
    await expect(page.getByTestId('suc-kpi-coverage')).toContainText('1 of 3');
    await page.getByTestId('suc-kpi-coverage').click();
    await expect(page.getByTestId('suc-drill-row')).toHaveCount(1);
    await shot(page, 'workspace-drill');
    // The positions list leads with the worst gaps, in words.
    const rows = page.getByTestId('suc-position-row');
    await expect(rows).toHaveCount(3);
    await expect(rows.first()).toHaveAttribute('data-position-id', 'cp-2');
    await expect(rows.first()).toContainText('Vacant, no one ready');
    await expect(rows.last()).toContainText('Covered');
    expect(errors).toEqual([]);
    expect(await axeSerious(page, '[data-testid="suc-workspace"]')).toEqual([]);
  });

  test('designate: the form explains what is missing before sending, then sends exactly what was chosen', async ({ page }) => {
    const { writes } = await boot(page);
    await page.goto('/performance/succession');
    await page.getByTestId('suc-designate-open').click();
    await page.getByTestId('suc-designate-rationale').fill('short');
    await page.getByTestId('suc-designate-submit').click();
    await expect(page.getByText('Choose the position.')).toBeVisible();
    await expect(page.getByText('Write at least 10 characters.')).toBeVisible();
    expect(writes).toEqual([]);
    await page.getByTestId('suc-designate-position').selectOption('p-9');
    await page.getByTestId('suc-designate-risk').selectOption('High');
    await page.getByTestId('suc-designate-impact').selectOption('Critical');
    await page.getByTestId('suc-designate-rationale').fill('Owns the platform roadmap and vendor contracts.');
    await page.getByTestId('suc-designate-accountable').fill('Liu');
    await page.getByTestId('suc-person-option').filter({ hasText: 'Liu Wei' }).click();
    await page.getByTestId('suc-designate-submit').click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/succession/positions',
      body: { positionId: 'p-9', riskOfLoss: 'High', impactOfVacancy: 'Critical', accountableEmployeeId: 7 } });
  });

  test('talent review: moving the potential needs a 20-character reason; a reviewer conflict is shown as the server said it', async ({ page }) => {
    const { writes } = await boot(page);
    await page.goto('/performance/succession?tab=calibration');
    const row = page.getByTestId('suc-calibration-row').first();
    await expect(row).toHaveAttribute('data-assessment-id', 'as-1');
    await expect(row).toContainText('High');
    await row.getByTestId('suc-calibrate-open').click();
    await page.getByTestId('suc-calibrate-potential').selectOption('2');
    await page.getByTestId('suc-calibrate-reason').fill('too short');
    await page.getByTestId('suc-calibrate-submit').click();
    await expect(page.getByText('Moving the potential needs a reason of at least 20 characters.')).toBeVisible();
    expect(writes).toEqual([]);
    await page.getByTestId('suc-calibrate-reason').fill('Two quarters below target after the transfer.');
    await page.getByTestId('suc-calibrate-submit').click();
    await expect(page.getByRole('alert').filter({ hasText: 'The candidate is in your management chain.' })).toBeVisible();
    // The nominations waiting for a readiness decision.
    await expect(page.getByTestId('suc-confirm-readiness-open')).toHaveAttribute('data-nomination-id', 'n-2');
  });

  test('a manager sees their nominations (stage masked) and plans — never positions, the 9-box or a slate', async ({ page }) => {
    await boot(page, { perms: MANAGER_PERMS, employeeId: 2 });
    await page.goto('/performance/succession');
    await expect(page.getByTestId('suc-tab-nominations')).toBeVisible();
    await expect(page.getByTestId('suc-tab-plans')).toBeVisible();
    for (const id of ['suc-tab-positions', 'suc-tab-calibration', 'suc-tab-pools', 'suc-tab-reports']) await expect(page.getByTestId(id)).toHaveCount(0);
    await expect(page.getByTestId('suc-kpis')).toHaveCount(0);
    await expect(page.getByTestId('suc-my-nomination')).toContainText('Being considered');
  });

  test('development plans: the row opens the plan, linked learning says it completed itself, overdue is explained', async ({ page }) => {
    await boot(page);
    await page.goto('/performance/succession?tab=plans');
    await page.getByTestId('suc-plan-row').click();
    await expect(page.getByTestId('suc-plan-status')).toHaveText('Active');
    await expect(page.getByTestId('suc-plan-track')).toContainText('An action is past its due date.');
    const learning = page.getByTestId('suc-action-row').filter({ hasText: 'Finance leadership path' });
    await expect(learning.getByTestId('suc-action-status')).toHaveText('Completed');
    await expect(learning.getByTestId('suc-action-method')).toHaveText('Completed automatically when the linked learning was completed');
    await expect(page.getByTestId('suc-plan-progress')).toHaveText('1 of 2 actions completed (cancelled actions not counted)');
    await expect(page.getByTestId('suc-plan-nomination')).toBeVisible();
    await shot(page, 'plan');
  });

  test('with the succession flag off, the screen says so instead of half-working', async ({ page }) => {
    await boot(page, { successionOff: true });
    await page.goto('/performance/succession');
    await expect(page.getByTestId('suc-disabled')).toContainText('Succession planning is not switched on for your organisation yet.');
  });

  test('an employee is turned away from the workspace', async ({ page }) => {
    await boot(page, { perms: EMP_PERMS, employeeId: 7 });
    await page.goto('/performance/succession');
    await page.waitForURL(/\/dashboard/);
  });
});

test.describe('Position page and slates', () => {
  test('candidates, the 9-box with every input, and a manual slate draft', async ({ page }) => {
    const { errors, writes } = await boot(page);
    await page.goto('/performance/succession/positions/cp-1');
    await expect(page.getByTestId('suc-position-detail')).toBeVisible();
    const sara = page.locator('[data-testid="suc-candidate-row"][data-nomination-id="n-2"]');
    await expect(sara.getByTestId('suc-candidate-stage')).toHaveText('InReview');
    await expect(sara.getByTestId('suc-candidate-needs-review')).toBeVisible();
    await expect(sara.getByTestId('suc-candidate-overcommitted')).toContainText('Ready now on 3 approved slates');
    // Readiness change needs a reason.
    await sara.getByTestId('suc-candidate-readiness').click();
    await page.getByTestId('suc-readiness-select').selectOption('OneToTwoYears');
    await page.getByTestId('suc-readiness-submit').click();
    await expect(page.getByText('Write at least 10 characters.')).toBeVisible();
    await page.getByTestId('suc-readiness-reason').fill('Moved to Operations; needs a year in the new role.');
    await page.getByTestId('suc-readiness-submit').click();
    await expect.poll(() => writes.find((w) => w.path.endsWith('/readiness'))?.body).toMatchObject({ readiness: 'OneToTwoYears', version: 3 });

    // The 9-box: every placement opens its inputs; the unrated are apart.
    await expect(page.getByTestId('suc-ninebox-cell-P3-High')).toHaveAttribute('data-count', '1');
    await page.getByTestId('suc-ninebox-person').click();
    const inputs = page.getByTestId('suc-ninebox-inputs');
    await expect(inputs).toContainText('rv-777');
    await expect(inputs).toContainText('perf-band-75-60-v1');
    await expect(inputs).toContainText('Calibrated up after the cross-company review panel.');
    await expect(page.getByTestId('suc-ninebox-unrated')).toContainText('Sara Ahmadi');

    // Slate versions and a new draft in the planner's own order.
    await expect(page.getByTestId('suc-slate-version')).toHaveAttribute('data-status', 'Approved');
    await expect(page.getByTestId('suc-slate-version').getByTestId('suc-slate-hash')).toContainText('Hash verified');
    await page.getByTestId('suc-slate-new').click();
    await expect(page.getByTestId('suc-slate-editor')).toContainText('never computed');
    await page.getByTestId('suc-slate-pick-n-1').check();
    await expect(page.getByTestId('suc-slate-order-n-1')).toHaveValue('1');
    await page.getByTestId('suc-slate-save').click();
    await expect(page.getByText('Give each chosen candidate’s place a reason of at least 10 characters.')).toBeVisible();
    await page.getByTestId('suc-slate-rationale-n-1').fill('Ran the region close for two years.');
    await page.getByTestId('suc-slate-save').click();
    await expect.poll(() => writes.find((w) => w.path === '/api/succession/positions/cp-1/slates')?.body)
      .toEqual({ candidates: [{ nominationId: 'n-1', order: 1, rationale: 'Ran the region close for two years.' }] });
    expect(errors).toEqual([]);
    await shot(page, 'position-detail');
    expect(await axeSerious(page, '[data-testid="suc-position-detail"]')).toEqual([]);
  });

  test('the history drawer lists what happened to a nomination', async ({ page }) => {
    await boot(page);
    await page.goto('/performance/succession/positions/cp-1');
    await page.locator('[data-testid="suc-candidate-row"][data-nomination-id="n-1"]').getByTestId('suc-candidate-history').click();
    await expect(page.getByTestId('suc-history-entry')).toHaveCount(2);
    await expect(page.getByTestId('suc-history')).toContainText('Readiness confirmed');
  });

  test('the slate review page: returning for revision needs a reason, and goes through the approvals API', async ({ page }) => {
    const { writes } = await boot(page);
    await page.goto('/performance/succession/slates/s-5');
    await expect(page.getByTestId('suc-slate-review')).toBeVisible();
    await expect(page.getByTestId('suc-slate-status')).toHaveText('Submitted');
    await expect(page.getByTestId('suc-snapshot-candidate')).toHaveCount(1);
    await expect(page.getByTestId('suc-snapshot-candidate')).toContainText('rv-777');
    await page.getByTestId('suc-slate-return').click();
    await page.getByTestId('suc-slate-return-confirm').click();
    await expect(page.getByText('Write at least 5 characters.')).toBeVisible();
    expect(writes).toEqual([]);
    await page.getByTestId('suc-slate-return-reason').fill('Add the Jeddah controller as a candidate.');
    await page.getByTestId('suc-slate-return-confirm').click();
    await expect.poll(() => writes[0]).toMatchObject({ method: 'POST', path: '/api/approval-requests/ar-1/decisions',
      body: { decision: 'Reject', comments: 'Add the Jeddah controller as a candidate.' } });
  });

  test('Arabic: right-to-left, Arabic words, and the 9-box mirrored (High performance on the left)', async ({ page }) => {
    const { errors } = await boot(page, { locale: 'ar' });
    await page.goto('/performance/succession/positions/cp-1');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    const grid = page.getByTestId('suc-ninebox');
    await expect(grid).toHaveAttribute('dir', 'rtl');
    await expect(page.getByTestId('suc-ninebox-cell-P3-High')).toContainText('قائد المستقبل');
    const low = await page.getByTestId('suc-ninebox-cell-P3-Low').boundingBox();
    const high = await page.getByTestId('suc-ninebox-cell-P3-High').boundingBox();
    expect(low!.x).toBeGreaterThan(high!.x);
    await expect(page.getByTestId('suc-candidates')).toContainText('يحتاج إلى مراجعة');
    await shot(page, 'position-detail-ar');
    expect(errors).toEqual([]);
    expect(await axeSerious(page, '[data-testid="suc-position-detail"]')).toEqual([]);
  });

  test('English: the 9-box reads Low → High performance from left to right', async ({ page }) => {
    await boot(page);
    await page.goto('/performance/succession/positions/cp-1');
    const low = await page.getByTestId('suc-ninebox-cell-P3-Low').boundingBox();
    const high = await page.getByTestId('suc-ninebox-cell-P3-High').boundingBox();
    expect(low!.x).toBeLessThan(high!.x);
  });
});

test.describe('My development plan', () => {
  test('my plan, my actions, and no memberships unless the organisation shares them', async ({ page }) => {
    const { errors } = await boot(page, { perms: EMP_PERMS, employeeId: 7 });
    await page.goto('/ess/development-plan');
    await expect(page.getByTestId('suc-my-plan')).toBeVisible();
    await expect(page.getByTestId('suc-my-action-row')).toHaveCount(2);
    await expect(page.getByTestId('suc-my-memberships')).toHaveCount(0);
    await expect(page.getByTestId('suc-my-plan')).not.toContainText('nomination');
    expect(errors).toEqual([]);
    expect(await axeSerious(page, '[data-testid="suc-my-plan"]')).toEqual([]);
  });

  test('memberships appear when candidate visibility is on; Arabic RTL', async ({ page }) => {
    await boot(page, { perms: EMP_PERMS, employeeId: 7, visibility: true, locale: 'ar' });
    await page.goto('/ess/development-plan');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { name: 'خطة تطويري' })).toBeVisible();
    await expect(page.getByTestId('suc-my-memberships')).toContainText('قادة المالية');
  });

  test('no plan yet: one plain sentence', async ({ page }) => {
    await boot(page, { perms: EMP_PERMS, employeeId: 7, noPlan: true });
    await page.goto('/ess/development-plan');
    await expect(page.getByTestId('suc-my-empty')).toContainText('You have no active development plan');
  });

  test('a 390px phone: no sideways scrolling on the workspace, a position, or my plan', async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    const page = await context.newPage();
    await boot(page);
    for (const url of ['/performance/succession', '/performance/succession/positions/cp-1', '/ess/development-plan']) {
      await page.goto(url);
      await expect(page.locator('main')).toBeVisible();
      await noSideways(page, url);
      await shot(page, `phone-${url.split('/').filter(Boolean).join('-')}`);
    }
    await context.close();
  });
});
