import { test, expect, type APIRequestContext } from '@playwright/test';
import {
  apiLogin, crashIndicators, mainText, tenantLogin,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_SLUG,
} from './helpers';

// W2-F — GOSI filing & variance view.
//
// NOTHING in this file is a hard-coded seed figure, deliberately. An earlier draft asserted
// "10,603.13 / 14,403.13 / August 2026 / 12 employees" literally and was already wrong twice over:
//   * the provisioned period is `now - 1 month` (e2e/bootstrap), so every literal month goes red
//     on the 1st of a month, for no product reason;
//   * a long-lived demo tenant can hold MORE than one locked run, and the period endpoint unions
//     runs and applies the statutory ceiling ONCE — so the period total is deliberately not the sum
//     of the per-run totals. A literal that matches a single-run database is wrong on a two-run one.
// So the period under test is discovered from /api/payroll/runs, the expected amounts are read from
// the API, and what is actually asserted is the reconciliation itself: deducted == recomputed == the
// ledger, across three independently-derived witnesses (payroll_deductions, the recompute, the GL,
// plus the payslips at run scope). Those hold for one run or for five.

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const HALALA = 0.01;

/** Mirrors the page's `money()` so a tile assertion compares like for like. */
const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const near = (actual: number, expected: number) => Math.abs(actual - expected) < HALALA;

interface RunRow { id: string; year: number; month: number; status: string }
interface PeriodRef { year: number; month: number; label: string }

interface GosiRun { runId: string; runType: string; status: string; employeeTotal: number; employerTotal: number }
interface GosiPeriod {
  period: string; hasStatutoryData: boolean; packResolved: boolean; runCount: number; runs: GosiRun[];
  totalEmployeeContrib: number; totalEmployerContrib: number; totalGosi: number;
  expectedVsActualEmployeeDelta: number; expectedVsActualEmployerDelta: number;
  glPosted: boolean; glEmployeeLiability: number | null; glEmployerLiability: number | null;
  glEmployeeDelta: number | null; glEmployerDelta: number | null;
  varianceCount: number;
  employees: { employeeName: string }[];
  branchBreakdown: { componentName: string; totalAmount: number }[];
}
interface GosiRunSummary {
  period: string; hasStatutoryData: boolean; siblingRunCount: number; expectedIsPeriodPartial: boolean;
  totalEmployeeContrib: number; totalEmployerContrib: number;
  slipEmployeeStatutoryTotal: number; slipEmployerStatutoryTotal: number;
  glEmployeeAccount: string | null; glEmployerAccount: string | null;
  glEmployeeDelta: number | null; glEmployerDelta: number | null;
}

async function json<T>(request: APIRequestContext, token: string, url: string): Promise<T> {
  const resp = await request.get(url, { headers: { Authorization: `Bearer ${token}` } });
  expect(resp.status(), `GET ${url}`).toBe(200);
  return (await resp.json()) as T;
}

async function nonVoidedRuns(request: APIRequestContext, token: string): Promise<RunRow[]> {
  const page = await json<{ items: RunRow[] }>(request, token, '/api/payroll/runs?page=1&pageSize=50');
  return (page.items ?? []).filter((r) => r.status !== 'Voided');
}

/** The newest period the seed actually ran payroll for — the same rule the page itself opens on. */
function latestPeriod(runs: RunRow[]): PeriodRef {
  const latest = [...runs].sort((a, b) => b.year - a.year || b.month - a.month)[0];
  expect(latest, 'the demo tenant must seed at least one non-voided payroll run').toBeTruthy();
  return { year: latest.year, month: latest.month, label: `${MONTHS[latest.month - 1]} ${latest.year}` };
}

/**
 * A period the picker can reach that provably has no run. The Year select offers the current year
 * and the two before it (plus any year holding a run), so the candidate is drawn from that range
 * rather than an arbitrary literal that the dropdown might not contain.
 */
function emptyPeriod(runs: RunRow[]): PeriodRef {
  const taken = new Set(runs.map((r) => `${r.year}-${r.month}`));
  const thisYear = new Date().getFullYear();
  for (const year of [thisYear - 2, thisYear - 1, thisYear]) {
    for (let month = 1; month <= 12; month++) {
      if (!taken.has(`${year}-${month}`)) return { year, month, label: `${MONTHS[month - 1]} ${year}` };
    }
  }
  throw new Error('No run-free period exists in the picker range; the fixture cannot test the empty state.');
}

async function openPeriod(page: import('@playwright/test').Page, p: PeriodRef) {
  await page.goto('/gosi-filing');
  await expect(page.getByRole('heading', { name: 'GOSI Filing & Variance' })).toBeVisible({ timeout: 15_000 });
  // Year first: the page resets scope on either change, and the Year list is populated from the runs.
  await page.getByLabel('Year').selectOption(String(p.year));
  await page.getByLabel('Month').selectOption({ label: MONTHS[p.month - 1] });
}

test.describe('GOSI filing — UI', () => {
  test('the seeded period ties out across deductions, recomputation and the ledger', async ({ page, request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const runs = await nonVoidedRuns(request, token);
    const ref = latestPeriod(runs);
    const p = await json<GosiPeriod>(request, token, `/api/gosi/periods/${ref.year}/${ref.month}/contribution-summary`);

    // ── Fixture gate: fail loudly if the seed stopped providing what this test is about, rather
    // than passing vacuously against an empty period.
    expect(p.hasStatutoryData, `seeded period ${ref.label} must carry statutory data`).toBe(true);
    expect(p.packResolved).toBe(true);
    expect(p.runCount).toBeGreaterThan(0);
    expect(p.employees.length).toBeGreaterThan(0);
    expect(p.branchBreakdown.length).toBeGreaterThan(0);

    // ── The reconciliation itself. These are the assertions with content, and they are invariant
    // under any reseed: what payroll deducted, what the rules recompute, and what hit the GL must
    // agree to the halala, and no employee may carry a variance.
    expect(near(p.expectedVsActualEmployeeDelta, 0), 'employee: deducted vs recomputed').toBe(true);
    expect(near(p.expectedVsActualEmployerDelta, 0), 'employer: deducted vs recomputed').toBe(true);
    expect(p.glPosted).toBe(true);
    expect(near(p.glEmployeeDelta ?? NaN, 0), 'employee: deductions vs GL').toBe(true);
    expect(near(p.glEmployerDelta ?? NaN, 0), 'employer: deductions vs GL').toBe(true);
    // GL liability is read from finance_gl_entries, the contrib totals from payroll_deductions:
    // two different tables, so this is a real cross-check and not a restatement.
    expect(near(p.glEmployeeLiability ?? NaN, p.totalEmployeeContrib)).toBe(true);
    expect(near(p.glEmployerLiability ?? NaN, p.totalEmployerContrib)).toBe(true);
    expect(near(p.totalGosi, p.totalEmployeeContrib + p.totalEmployerContrib)).toBe(true);
    expect(p.varianceCount).toBe(0);

    // ── The UI must render exactly those figures.
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await openPeriod(page, ref);

    await expect(page.getByTestId('tile-employee')).toContainText(money(p.totalEmployeeContrib));
    await expect(page.getByTestId('tile-employer')).toContainText(money(p.totalEmployerContrib));
    await expect(page.getByTestId('tile-expected-delta')).toContainText('Recomputation matches to the halala');
    await expect(page.getByTestId('tile-gl-delta')).toContainText('GL matches deductions to the halala');
    await expect(page.getByTestId('gosi-tieout-verdict')).toHaveText(/Ties out to the halala/);
    // The cell's accessible name is "<name> <code>", so this is a substring match by design.
    for (const component of p.branchBreakdown) {
      await expect(page.getByRole('cell', { name: component.componentName })).toBeVisible();
    }
    await expect(page.getByTestId('gosi-variance-count')).toHaveText(`All ${p.employees.length} employees reconcile`);
    await expect(page.getByTestId('gosi-employee-table').locator('tbody tr')).toHaveCount(p.employees.length);
    await expect(page.getByRole('link', { name: 'Saudi Compliance' }).first()).toHaveAttribute('href', '/saudi-compliance');

    // ── Multi-run periods are a supported shape, not a failure: the page must say so rather than
    // silently presenting a partial figure as the filing figure.
    if (p.runCount > 1) {
      await expect(page.getByTestId('gosi-multi-run-note')).toContainText(`${p.runCount} runs are unioned`);
    }
    expect(crashIndicators(await mainText(page))).toEqual([]);
  });

  test('drilling into one named run adds the payslip and GL witnesses', async ({ page, request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const runs = await nonVoidedRuns(request, token);
    const ref = latestPeriod(runs);
    const p = await json<GosiPeriod>(request, token, `/api/gosi/periods/${ref.year}/${ref.month}/contribution-summary`);
    expect(p.hasStatutoryData).toBe(true);

    // Scope to ONE run, named by the tab the page renders for it, so this half of the proof is
    // independent of how many siblings share the period.
    const run = p.runs[0];
    expect(run, 'the period must expose at least one run tab').toBeTruthy();
    const r = await json<GosiRunSummary>(request, token, `/api/gosi/payroll-runs/${run.runId}/contribution-summary`);

    // A fourth witness at run scope: payslip statutory lines, stored separately from the deductions.
    expect(near(r.slipEmployeeStatutoryTotal, r.totalEmployeeContrib), 'employee: payslips vs deductions').toBe(true);
    expect(near(r.slipEmployerStatutoryTotal, r.totalEmployerContrib), 'employer: payslips vs deductions').toBe(true);
    expect(near(r.totalEmployeeContrib, run.employeeTotal)).toBe(true);
    expect(r.siblingRunCount).toBe(p.runCount - 1);

    // The documented period-vs-run relationship, asserted instead of tripped over: with one run the
    // two views agree exactly; with siblings the period unions them and applies the ceiling once, so
    // a single run can only be a part of it.
    if (p.runCount === 1) {
      expect(near(p.totalEmployeeContrib, r.totalEmployeeContrib)).toBe(true);
      expect(r.expectedIsPeriodPartial).toBe(false);
    } else {
      expect(r.totalEmployeeContrib).toBeLessThanOrEqual(p.totalEmployeeContrib + HALALA);
    }

    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await openPeriod(page, ref);

    const tabName = `${run.runType} run · ${run.status} · EE ${money(run.employeeTotal)} / ER ${money(run.employerTotal)}`;
    await page.getByRole('tab', { name: tabName, exact: true }).click();

    const tie = page.getByTestId('gosi-tieout');
    await expect(tie).toContainText('Payslips');
    // The GL account codes come from the run payload, so a chart-of-accounts change surfaces here
    // as a real failure rather than being masked by a literal '2101'.
    await expect(tie).toContainText((r.glEmployeeAccount ?? '').split(' ')[0]);
    await expect(tie).toContainText((r.glEmployerAccount ?? '').split(' ')[0]);
    await expect(page.getByTestId('gosi-tieout-verdict')).toHaveText(/Ties out to the halala/);
    if (r.expectedIsPeriodPartial) await expect(page.getByTestId('gosi-period-scope-note')).toBeVisible();
    expect(crashIndicators(await mainText(page))).toEqual([]);
  });

  test('a period with no payroll shows the explicit no-statutory-lines state, not zeros', async ({ page, request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const runs = await nonVoidedRuns(request, token);
    const ref = emptyPeriod(runs);

    // Confirm the fixture really is empty before asserting the empty state, so this can never pass
    // by accidentally landing on a period that does have data.
    const p = await json<GosiPeriod>(request, token, `/api/gosi/periods/${ref.year}/${ref.month}/contribution-summary`);
    expect(p.hasStatutoryData, `${ref.label} must have no statutory data`).toBe(false);
    expect(p.runCount).toBe(0);

    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await openPeriod(page, ref);

    const empty = page.getByTestId('gosi-no-statutory');
    await expect(empty).toContainText(`No statutory lines recorded for ${ref.label}`);
    // The regression this guards: rendering the tiles and the grid anyway, filled with 0.00.
    await expect(page.getByTestId('tile-employee')).toHaveCount(0);
    await expect(page.getByTestId('tile-employer')).toHaveCount(0);
    await expect(page.getByTestId('gosi-employee-table')).toHaveCount(0);
    await expect(page.getByTestId('gosi-tieout')).toHaveCount(0);
    expect(await mainText(page)).not.toContain('0.00');
  });
});

test.describe('GOSI filing — API authorization', () => {
  test('an employee cannot read GOSI period filings', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    const adminToken = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const ref = latestPeriod(await nonVoidedRuns(request, adminToken));
    const resp = await request.get(`/api/gosi/periods/${ref.year}/${ref.month}/contribution-summary`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(403);
  });
});
