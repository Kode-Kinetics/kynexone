import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * One appraisal cycle end to end from the screen, fixture lane (e2e/playwright.fixture.config.ts). Every API reply is
 * route-intercepted by a small in-memory server that applies the API's own rules (the controllers named beside each
 * branch), so the journey proves the SCREEN carries an HR user, a line manager and an employee through the whole
 * cycle with no step missing:
 *
 *   HR creates the cycle (with its scorecard template) -> launches it -> the employee self-assesses -> HR advances
 *   -> the manager reviews -> HR advances to calibration and calibrates with a reason -> HR advances to final
 *   approval and publishes -> HR advances to published -> the employee sees the result and acknowledges it.
 *
 * What it cannot prove: that the real API returns these payloads. The backend tests (AppraisalCycleCompletionTests,
 * AppraisalSubjectVisibilityTests, AppraisalScoringIntegrityTests) prove the rules; the seeded-stack lane proves
 * the wiring.
 */

type Persona = 'hr' | 'manager' | 'employee';
const EMPLOYEE_ID = 501;
const TEMPLATE = { id: 'tpl-1', name: 'Balanced scorecard', isDefault: true, isActive: true, departmentName: '', designationTitle: '', grade: '',
  kpiWeight: 40, competencyWeight: 20, attendanceWeight: 10, productivityWeight: 15, feedbackWeight: 10, disciplineWeight: 5,
  minPassingScore: 60, requiresCalibration: true, requires360Feedback: false, ratingLabels: '' };

const USERS: Record<Persona, Record<string, unknown>> = {
  hr: { id: 'u-hr', tenantId: 't1', tenantSlug: 'acme', email: 'hr@acme.test', fullName: 'Huda HR', roles: ['HR Manager'], accountType: 'Tenant',
    isGroupScope: true, companies: [], permissions: ['dashboard.read', 'employees.read', 'performance.read', 'performance.write', 'performance.approve', 'performance.cycle_manage'] },
  manager: { id: 'u-mgr', tenantId: 't1', tenantSlug: 'acme', email: 'mgr@acme.test', fullName: 'Majid Manager', roles: ['Manager'], accountType: 'Tenant',
    isGroupScope: false, companies: [], permissions: ['dashboard.read', 'performance.read', 'performance.write'] },
  employee: { id: 'u-emp', tenantId: 't1', tenantSlug: 'acme', email: 'sara@acme.test', fullName: 'Sara Employee', roles: ['Employee'], accountType: 'Tenant',
    isGroupScope: false, companies: [], permissions: ['dashboard.read', 'performance.read'] },
};

interface Cycle { id: string; name: string; cycleType: string; reviewPeriodStart: string; reviewPeriodEnd: string; status: string;
  enableCalibration: boolean; enableSelfAssessment: boolean; enable360Feedback: boolean; defaultScorecardTemplateId: string | null; enrolledCount: number }
interface Review { id: string; cycleId: string; cycleName: string; employeeId: number; employeeName: string; departmentName: string; designationTitle: string;
  status: string; kpiScore: number; competencyScore: number; attendanceScore: number; productivityScore: number; feedbackScore: number; disciplineScore: number;
  finalScore: number; finalRating: string; calibrationAdjustment: number; calibrationNotes: string; selfAssessmentNotes: string; managerNotes: string; hrNotes: string;
  publishedAt: string | null; acknowledgedAt: string | null; isAppealed: boolean; reviewerManagerName: string; scorecardTemplateId: string; createdAtUtc: string }

const rating = (s: number) => s >= 90 ? 'Outstanding' : s >= 75 ? 'Exceeds Expectations' : s >= 60 ? 'Meets Expectations' : s >= 45 ? 'Developing' : 'Unsatisfactory';
const score = (r: Review) => Math.max(0, Math.min(100, Math.round((r.kpiScore * 0.4 + r.competencyScore * 0.2 + r.attendanceScore * 0.1
  + r.productivityScore * 0.15 + r.feedbackScore * 0.1 + r.disciplineScore * 0.05 + r.calibrationAdjustment) * 100) / 100));

async function boot(page: Page) {
  const s = { persona: 'hr' as Persona, cycles: [] as Cycle[], reviews: [] as Review[], audit: [] as { action: string; reason: string }[] };

  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
  });
  await page.route('**/api/**', (route: Route) => {
    const req = route.request();
    const url = new URL(req.url());
    const p = url.pathname;
    const m = req.method();
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    const pageOf = <T,>(items: T[]) => json({ items, total: items.length, page: Number(url.searchParams.get('page') ?? 1), pageSize: Number(url.searchParams.get('pageSize') ?? 50) });
    const body = () => (req.postDataJSON() ?? {}) as Record<string, any>;
    // The employee reads their own review without any unpublished result (ReviewsController.WithholdFromSubject).
    const asSeenBy = (r: Review): Review => s.persona !== 'employee' || ['Published', 'Acknowledged', 'Appealed', 'Closed'].includes(r.status)
      || ['SelfAssessmentDue', 'SelfAssessmentSubmitted'].includes(r.status)
      ? { ...r, managerNotes: s.persona === 'hr' ? r.managerNotes : '', calibrationNotes: s.persona === 'employee' ? '' : r.calibrationNotes }
      : { ...r, kpiScore: 0, competencyScore: 0, attendanceScore: 0, productivityScore: 0, feedbackScore: 0, disciplineScore: 0, finalScore: 0, finalRating: '', calibrationAdjustment: 0, calibrationNotes: '', managerNotes: '' };

    if (p === '/api/auth/me') return json(USERS[s.persona]);
    if (p.startsWith('/api/features') || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });

    if (p === '/api/performance/analytics/dashboard') return json({
      activeCycles: s.cycles.filter(c => c.status === 'Active'),
      pendingActions: { selfAssessmentDue: s.reviews.filter(r => r.status === 'SelfAssessmentDue').length, managerReviewPending: s.reviews.filter(r => ['ManagerReview', 'SelfAssessmentSubmitted'].includes(r.status)).length, calibrationPending: 0, appealsPending: 0 },
      recentActivity: [], recommendations: { incrementPending: 0, promotionPending: 0, bonusPending: 0 }, activePips: 0, probationDue: 0 });
    if (p === '/api/performance/analytics/goals-completion') return json({ total: 0, completed: 0, onTrack: 0, atRisk: 0, avgAchievement: 0 });
    if (p === '/api/performance/templates') return json([TEMPLATE]);
    if (p === '/api/performance/goals') return pageOf([]);
    if (p === '/api/performance/reviews/appeals') return json([]);
    if (p.startsWith('/api/performance/feedback')) return json([]);

    // ── Cycles (CyclesController) ──
    if (p === '/api/performance/cycles' && m === 'GET') {
      const status = url.searchParams.get('status');
      return pageOf(s.cycles.filter(c => !status || c.status === status));
    }
    if (p === '/api/performance/cycles' && m === 'POST') {
      const b = body();
      if (!b.name) return json({ message: 'Cycle name is required.' }, 400);
      const c: Cycle = { id: `cyc-${s.cycles.length + 1}`, name: b.name, cycleType: b.cycleType, reviewPeriodStart: b.reviewPeriodStart, reviewPeriodEnd: b.reviewPeriodEnd,
        status: 'Draft', enableCalibration: b.enableCalibration, enableSelfAssessment: b.enableSelfAssessment, enable360Feedback: b.enable360Feedback,
        defaultScorecardTemplateId: b.defaultScorecardTemplateId ?? null, enrolledCount: 0 };
      s.cycles.push(c);
      return json(c, 201);
    }
    const cyc = /^\/api\/performance\/cycles\/([^/]+)\/(launch|advance|close)$/.exec(p);
    if (cyc && m === 'POST') {
      const c = s.cycles.find(x => x.id === cyc[1]);
      if (!c) return json({}, 404);
      const mine = s.reviews.filter(r => r.cycleId === c.id && r.status !== 'Excluded');
      if (cyc[2] === 'launch') {
        if (c.status !== 'Draft') return json({ message: 'Only Draft cycles can be launched.' }, 400);
        if (!c.defaultScorecardTemplateId) return json({ message: 'A default scorecard template is required before launching.' }, 400);
        c.status = 'Active';
        s.reviews.push({ id: 'rev-1', cycleId: c.id, cycleName: c.name, employeeId: EMPLOYEE_ID, employeeName: 'Sara Employee', departmentName: 'Operations',
          designationTitle: 'Analyst', status: c.enableSelfAssessment ? 'SelfAssessmentDue' : 'ManagerReview', kpiScore: 0, competencyScore: 0, attendanceScore: 0,
          productivityScore: 0, feedbackScore: 0, disciplineScore: 0, finalScore: 0, finalRating: '', calibrationAdjustment: 0, calibrationNotes: '',
          selfAssessmentNotes: '', managerNotes: '', hrNotes: '', publishedAt: null, acknowledgedAt: null, isAppealed: false, reviewerManagerName: '',
          scorecardTemplateId: c.defaultScorecardTemplateId, createdAtUtc: '2026-11-17T08:00:00Z' });
        c.enrolledCount = 1;
        return json({ cycle: c, enrolledCount: 1 });
      }
      if (cyc[2] === 'advance') {
        const next = ({ Active: 'InReview', InReview: c.enableCalibration ? 'Calibration' : 'FinalApproval', Calibration: 'FinalApproval', FinalApproval: 'Published' } as Record<string, string>)[c.status];
        if (!next) return json({ message: `Cannot advance from status ${c.status}.` }, 400);
        if (['InReview', 'Calibration'].includes(c.status) && mine.some(r => r.status !== 'ManagerReviewComplete'))
          return json({ error: 'manager_reviews_incomplete', message: 'Every enrolled review must complete manager review before calibration/final approval.' }, 409);
        if (c.status === 'FinalApproval' && mine.some(r => !['Published', 'Acknowledged', 'Appealed'].includes(r.status)))
          return json({ error: 'reviews_not_published', message: 'Every review must be published before the cycle can be published.' }, 409);
        c.status = next;
        if (next === 'FinalApproval') mine.filter(r => r.status === 'ManagerReviewComplete').forEach(r => { r.status = 'FinalApproval'; });
        return json(c);
      }
      if (c.status !== 'Published') return json({ error: 'cycle_not_published', message: 'Only a Published cycle can be closed.' }, 409);
      c.status = 'Closed';
      return json(c);
    }

    // ── Reviews (ReviewsController) ──
    if (p === '/api/performance/reviews' && m === 'GET') {
      const mineOnly = url.searchParams.get('view') === 'mine';
      const status = url.searchParams.get('status');
      const visible = s.reviews.filter(r => (!mineOnly || (s.persona === 'employee' && r.employeeId === EMPLOYEE_ID)) && (!status || r.status === status));
      return pageOf(visible.map(asSeenBy));
    }
    const rev = /^\/api\/performance\/reviews\/([^/]+)(?:\/([a-z-]+))?$/.exec(p);
    if (rev) {
      const r = s.reviews.find(x => x.id === rev[1]);
      if (!r) return json({}, 404);
      const action = rev[2];
      const inRange = (...v: number[]) => v.every(x => x >= 0 && x <= 100);
      if (!action && m === 'GET') return json({ review: asSeenBy(r), template: TEMPLATE, breakdown: [], competencies: [], goals: [], feedback360: [], auditLog: [], calibration: null, appeal: null, goalKpi: null });
      if (action === 'self-assessment') {
        const b = body();
        if (s.persona !== 'employee') return json({ error: 'self_assessment_by_employee_only' }, 403);
        if (r.status !== 'SelfAssessmentDue') return json({ message: 'Review is not in Self-Assessment stage.' }, 400);
        if (!inRange(b.kpiScore, b.competencyScore, b.productivityScore)) return json({ error: 'score_out_of_range' }, 400);
        Object.assign(r, { kpiScore: b.kpiScore, competencyScore: b.competencyScore, productivityScore: b.productivityScore, selfAssessmentNotes: b.notes, status: 'SelfAssessmentSubmitted' });
        return json(r);
      }
      if (action === 'manager-review') {
        const b = body();
        if (!['SelfAssessmentSubmitted', 'ManagerReview', 'SelfAssessmentDue'].includes(r.status)) return json({ message: 'Review is not reviewable.' }, 400);
        if (!inRange(b.kpiScore, b.competencyScore, b.attendanceScore, b.productivityScore, b.feedbackScore, b.disciplineScore)) return json({ error: 'score_out_of_range' }, 400);
        Object.assign(r, { kpiScore: b.kpiScore, competencyScore: b.competencyScore, attendanceScore: b.attendanceScore, productivityScore: b.productivityScore,
          feedbackScore: b.feedbackScore, disciplineScore: b.disciplineScore, managerNotes: b.managerNotes ?? '', reviewerManagerName: 'Majid Manager', status: 'ManagerReviewComplete' });
        r.finalScore = score(r); r.finalRating = rating(r.finalScore);
        s.audit.push({ action: 'ManagerReviewSubmitted', reason: r.managerNotes });
        return json(r);
      }
      if (action === 'publish') {
        if (r.status !== 'FinalApproval') return json({ error: 'review_not_final_approval' }, 409);
        Object.assign(r, { status: 'Published', publishedAt: '2026-11-17T09:00:00Z' });
        return json(r);
      }
      if (action === 'acknowledge') {
        if (s.persona !== 'employee') return json({ error: 'acknowledge_by_employee_only' }, 403);
        if (r.status !== 'Published') return json({ message: 'Review must be Published before acknowledgement.' }, 400);
        Object.assign(r, { status: 'Acknowledged', acknowledgedAt: '2026-11-17T10:00:00Z' });
        return json(r);
      }
    }

    // ── Calibration (CalibrationController) ──
    const cal = /^\/api\/performance\/calibration\/([^/]+)(\/adjust)?$/.exec(p);
    if (cal) {
      const c = s.cycles.find(x => x.id === cal[1]);
      if (!cal[2]) {
        const rows = s.reviews.filter(r => r.cycleId === cal[1]);
        return json({ reviews: rows, distribution: {}, managerStats: [], totalReviews: rows.length });
      }
      const b = body();
      if (c?.status !== 'Calibration') return json({ error: 'cycle_not_in_calibration' }, 409);
      if (!String(b.reason ?? '').trim()) return json({ message: 'A reason is mandatory for all calibration adjustments.' }, 400);
      const r = s.reviews.find(x => x.id === b.reviewId)!;
      if (r.status !== 'ManagerReviewComplete') return json({ error: 'review_not_calibratable' }, 409);
      const original = r.finalScore;
      r.calibrationAdjustment = Number(b.adjustment); r.calibrationNotes = b.reason;
      r.finalScore = score(r); r.finalRating = rating(r.finalScore);
      s.audit.push({ action: 'CalibrationAdjustment', reason: b.reason });
      return json({ originalScore: original, newScore: r.finalScore, newRating: r.finalRating });
    }
    return json([]);
  });
  return s;
}

const tab = (page: Page, name: string) => page.getByRole('button', { name, exact: true }).click();
const as = async (page: Page, s: { persona: Persona }, persona: Persona) => {
  s.persona = persona;
  await page.goto('/performance');
  await expect(page.getByRole('heading', { name: 'Performance & Appraisals' })).toBeVisible({ timeout: 30_000 });
};
const cycleRow = (page: Page, name: string) => page.locator('div.px-5.py-4').filter({ hasText: name }).first();

test('an HR user runs one appraisal cycle from the screen, with the manager and the employee doing their steps', async ({ page }) => {
  test.setTimeout(180_000);
  const s = await boot(page);

  // ── HR creates the cycle with its scorecard template, and launches it ──
  await as(page, s, 'hr');
  await tab(page, 'Cycles');
  await page.getByRole('button', { name: 'New Cycle' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Cycle Name *').fill('FY2026 Annual Appraisal');
  await dialog.getByLabel('Period Start *').fill('2026-01-01');
  await dialog.getByLabel('Period End *').fill('2026-12-31');
  await dialog.getByRole('combobox', { name: 'Scorecard template' }).selectOption(TEMPLATE.id);
  await dialog.getByRole('button', { name: 'Create Cycle' }).click();
  await expect(dialog).toBeHidden();
  expect(s.cycles[0].defaultScorecardTemplateId, 'the form sends the chosen template, or the cycle cannot launch').toBe(TEMPLATE.id);
  await cycleRow(page, 'FY2026 Annual Appraisal').getByRole('button', { name: 'Launch' }).click();
  await expect(cycleRow(page, 'FY2026 Annual Appraisal')).toContainText('1 enrolled');
  expect(s.cycles[0].status).toBe('Active');

  // ── The employee self-assesses ──
  await as(page, s, 'employee');
  await page.getByRole('button', { name: 'Start Assessment' }).click();
  const self = page.getByRole('dialog');
  await self.getByLabel('KPI Score (0–100)').fill('80');
  await self.getByLabel('Competency Score').fill('75');
  await self.getByLabel('Productivity Score').fill('70');
  await self.getByLabel('Self-Assessment Notes').fill('Delivered the warehouse migration two weeks early.');
  await self.getByRole('button', { name: 'Submit Assessment' }).click();
  await expect(self).toBeHidden();
  expect(s.reviews[0].status).toBe('SelfAssessmentSubmitted');

  // ── HR moves the cycle into review ──
  await as(page, s, 'hr');
  await tab(page, 'Cycles');
  await cycleRow(page, 'FY2026 Annual Appraisal').getByRole('button', { name: 'Advance' }).click();
  await expect.poll(() => s.cycles[0].status).toBe('InReview');

  // ── The manager reviews ──
  await as(page, s, 'manager');
  await tab(page, 'Team Reviews');
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  const mgr = page.getByRole('dialog');
  for (const [label, value] of [['KPI Score (0–100)', '85'], ['Competency Score (0–100)', '80'], ['Attendance Score (0–100)', '90'],
    ['Productivity Score (0–100)', '75'], ['Feedback Score (0–100)', '70'], ['Discipline Score (0–100)', '95']] as const) {
    await mgr.getByLabel(label).fill(value);
  }
  await mgr.getByLabel('Manager Notes').fill('Strong delivery; coach on stakeholder updates.');
  await mgr.getByRole('button', { name: 'Submit Review' }).click();
  await expect(mgr).toBeHidden();
  expect(s.reviews[0].status).toBe('ManagerReviewComplete');
  const managerScore = s.reviews[0].finalScore;

  // Before publication the employee sees the stage, never the score.
  await as(page, s, 'employee');
  await expect(page.getByText(/Score: /)).toHaveCount(0);

  // ── HR advances to calibration and calibrates with a reason ──
  await as(page, s, 'hr');
  await tab(page, 'Cycles');
  await cycleRow(page, 'FY2026 Annual Appraisal').getByRole('button', { name: 'Advance' }).click();
  await expect.poll(() => s.cycles[0].status).toBe('Calibration');
  await tab(page, 'Calibration');
  await page.getByRole('button', { name: 'Adjust' }).click();
  const calib = page.getByRole('dialog');
  await calib.getByLabel('Score Adjustment (e.g. +5 or -3)').fill('2');
  await calib.getByLabel('Reason (required) *').fill('Panel moderation: delivery outweighs the team average.');
  await calib.getByRole('button', { name: 'Apply Calibration' }).click();
  await expect(calib).toBeHidden();
  expect(s.reviews[0].finalScore).toBe(managerScore + 2);
  expect(s.audit.at(-1)).toEqual({ action: 'CalibrationAdjustment', reason: 'Panel moderation: delivery outweighs the team average.' });

  // ── HR advances to final approval, publishes the review, and publishes the cycle ──
  await tab(page, 'Cycles');
  await cycleRow(page, 'FY2026 Annual Appraisal').getByRole('button', { name: 'Advance' }).click();
  await expect.poll(() => s.cycles[0].status).toBe('FinalApproval');
  await tab(page, 'Team Reviews');
  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect.poll(() => s.reviews[0].status).toBe('Published');
  await tab(page, 'Cycles');
  await cycleRow(page, 'FY2026 Annual Appraisal').getByRole('button', { name: 'Advance' }).click();
  await expect.poll(() => s.cycles[0].status).toBe('Published');

  // ── The employee sees the published result and acknowledges it ──
  await as(page, s, 'employee');
  const finalScore = s.reviews[0].finalScore;
  await expect(page.getByText(`Score: ${finalScore} — ${s.reviews[0].finalRating}`)).toBeVisible();
  // The row opens the review; acknowledging is done in the review drawer (the row's own button was replaced by "View result").
  await page.getByRole('button', { name: 'View result' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Acknowledge result' }).click();
  await expect.poll(() => s.reviews[0].status).toBe('Acknowledged');
  await expect(page.getByText('Acknowledged').first()).toBeVisible();

  // ── HR closes the cycle ──
  await as(page, s, 'hr');
  await tab(page, 'Cycles');
  await cycleRow(page, 'FY2026 Annual Appraisal').getByRole('button', { name: 'Close cycle' }).click();
  await expect.poll(() => s.cycles[0].status).toBe('Closed');
});
