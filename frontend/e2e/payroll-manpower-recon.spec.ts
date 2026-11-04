import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Payroll → manpower + value reconciliation before approval, fixture lane (e2e/playwright.fixture.config.ts).
 * Every API reply is route-intercepted; the reconciliation payloads are the shared contract fixtures in
 * e2e/fixtures/recon-*.json (figures that genuinely tie).
 *
 * Proves:
 *   - the volume bridge renders and says it ties; the value bridge keeps employer cost out of net pay
 *   - every figure opens a drill-down that asks the server for exactly that cell, and Escape gives focus back
 *   - sign-off stays blocked until each flagged item has a written reason and the flagged count is confirmed;
 *     after it, Approve is the next step and the review stage names the signer
 *   - the 409 reconciliation_not_reviewed refusal at Approve returns the run to Review
 *   - a department-scoped (partial) read can never sign; NoPrior still needs a sign-off; a failed load is not an empty bridge
 *   - exports download with the server's filename; light and dark pass axe; Arabic renders right-to-left
 */
const RUN = '33333333-3333-4333-8333-333333333333';
const RUN_PRIOR = '11111111-1111-4111-8111-111111111111';
const RUN_OFFCYCLE = '55555555-5555-4555-8555-555555555555';
const COMPANY = { id: 'c1', name: 'IntelliFlow KSA', tradeName: '', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' };
const USER = (permissions: string[]) => ({
  id: 'u1', tenantId: 't1', tenantSlug: 'intelliflow', email: 'admin@intelliflow.test', fullName: 'Yaser Al-Ghamdi',
  roles: ['Admin'], accountType: 'Tenant', isGroupScope: true, companies: [], permissions,
});
const ALL = ['dashboard.read', 'payroll.read', 'payroll.write', 'payroll.approve', 'payroll.lock', 'employees.read'];
const run = (id: string, month: number, status: string, over: Record<string, unknown> = {}) => ({
  id, companyId: 'c1', year: 2026, month, status, runType: 'Regular',
  totalGrossSalary: 100000, totalDeductions: 10000, totalNetSalary: 90000, totalEmployerStatutoryCost: 9000, employeeCount: 40,
  createdAtUtc: '2026-10-01T08:00:00Z', processedAtUtc: status === 'Draft' ? null : '2026-10-02T08:00:00Z', lockedAtUtc: null, ...over,
});
const RUNS = [run(RUN, 11, 'Processed'), run(RUN_PRIOR, 10, 'Locked', { lockedAtUtc: '2026-10-28T08:00:00Z' }), run(RUN_OFFCYCLE, 11, 'Processed', { runType: 'OffCycle' })];

const fixture = (name: string) => JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', name), 'utf8'));

interface Employees { items: unknown[]; total: number; page: number; pageSize: number }
const employeesPage = (n: number, category = 'NEW_JOINER'): Employees => ({
  items: Array.from({ length: n }, (_, i) => ({ employeeId: 200 + i, employeeCode: `E-${200 + i}`, employeeName: `Employee Number ${i + 1}`, category, note: i === 0 ? 'Joined 3 Nov 2026' : null, prior: null, current: 12000, delta: 12000 })),
  total: n, page: 1, pageSize: 50,
});

interface State {
  recon: any;
  reconStatus: number;
  reconBody?: unknown;
  reconCalls: number;
  failFirstRecon?: boolean;
  employeeQueries: URLSearchParams[];
  flaggedTotal: number;
  signoffBodies: any[];
  signoffError?: { status: number; body: unknown };
  approveError?: { status: number; body: unknown };
  approveCalls: number;
  exports: string[];
}

async function boot(page: Page, opts: { recon?: string; permissions?: string[]; theme?: 'light' | 'dark'; locale?: 'en' | 'ar'; url?: string; failFirstRecon?: boolean; mutate?: (dto: any) => void } = {}) {
  const s: State = {
    recon: fixture(opts.recon ?? 'recon-full.json'), reconStatus: 200, reconCalls: 0, failFirstRecon: opts.failFirstRecon,
    employeeQueries: [], flaggedTotal: 3, signoffBodies: [], approveCalls: 0, exports: [],
  };
  opts.mutate?.(s.recon);
  await page.addInitScript(([th, lang]) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', th);
    localStorage.setItem('kynexone-locale-choice-v2', lang);
  }, [opts.theme ?? 'light', opts.locale ?? 'en']);
  await page.route('**/api/**', (route: Route) => {
    const req = route.request();
    const url = new URL(req.url());
    const p = url.pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(USER(opts.permissions ?? ALL));
    if (p === '/api/payroll/companies') return json([COMPANY]);
    if (p === '/api/payroll/runs' && req.method() === 'GET') return json({ items: RUNS, total: RUNS.length, page: 1, pageSize: 100 });
    if (p === '/api/payroll/payment-batches') return json([]);
    if (p === '/api/payroll/overview') return json({ year: 2026, month: 11, totalCompanies: 0, totalActiveEmployees: 0, totalGrossPayroll: 0, totalNetPayroll: 0, totalValidationErrors: 0, totalPendingApprovals: 0, companies: [] });
    if (p === '/api/payroll/readiness') return json({ year: 2026, month: 11, companyId: null, completionPercent: 100, isReadyForProcessing: true, totalActiveEmployees: 40, employeesWithSalary: 40, salaryCoveragePercent: 100, validationErrors: 0, payrollRunStatus: null, steps: [] });
    if (p === '/api/payroll/reports/summary') return json({ totalRuns: 1, lockedRuns: 1, totalEmployeesPaid: 40, totalGrossYtd: 0, totalNetYtd: 0 });
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p.startsWith('/api/features') || p === '/api/notifications') return json([]);

    const m = /^\/api\/payroll\/runs\/([^/]+)\/(.+)$/.exec(p);
    if (m) {
      const [, id, tail] = m;
      if (tail === 'recon' && req.method() === 'GET') {
        s.reconCalls++;
        if (s.failFirstRecon && s.reconCalls === 1) return json({ error: 'boom', message: 'The reconciliation service is busy.' }, 500);
        return json(s.reconBody ?? s.recon, s.reconStatus);
      }
      if (tail === 'recon/employees') {
        s.employeeQueries.push(url.searchParams);
        if (url.searchParams.get('bucket') === 'flagged') return json(employeesPage(s.flaggedTotal, 'FLAGGED'));
        if (url.searchParams.get('category') === 'NOT_PAID') return json(employeesPage(1, 'NOT_PAID'));
        return json(employeesPage(url.searchParams.get('category') === 'NEW_JOINER' ? 3 : 2, url.searchParams.get('category') ?? 'X'));
      }
      if (tail === 'recon/export') {
        s.exports.push(url.search);
        const ext = url.searchParams.get('format') === 'csv' ? 'csv' : 'xlsx';
        const suffix = s.recon.scope?.partial ? 'PARTIAL' : s.recon.signoff?.status === 'Active' ? 'SIGNED' : 'UNSIGNED-DRAFT';
        return route.fulfill({ status: 200, contentType: 'application/octet-stream', headers: { 'content-disposition': `attachment; filename="payroll-reconciliation-2026-11-${suffix}.${ext}"` }, body: 'fixture' });
      }
      if (tail === 'recon/signoff' && req.method() === 'POST') {
        s.signoffBodies.push(req.postDataJSON());
        if (s.signoffError) return json(s.signoffError.body, s.signoffError.status);
        s.recon = { ...s.recon, signoff: fixture('recon-signed.json').signoff };
        return json(s.recon.signoff, 201);
      }
      if (tail === 'approve' && req.method() === 'POST') {
        s.approveCalls++;
        if (s.approveError) return json(s.approveError.body, s.approveError.status);
        return json({ ...RUNS[0], status: 'Approved' });
      }
      if (tail === 'validate' && req.method() === 'GET') return json([]);
      if (tail === 'validation-overrides') return json({ runId: id, overrides: [], overridableCodes: [], nonOverridableCodes: [] });
      if (tail === 'approvals') return json([]);
      if (tail === 'population') return json({ runId: id, includedCount: 42, excludedCount: 0, notEligibleCount: 0, included: [], excluded: [], notEligible: [] });
      if (tail === 'attendance-reconciliation') return json({ runId: id, from: '', to: '', consumesAttendance: true, stale: { code: 'X', employeeCount: 0, dayCount: 0, days: [] }, held: { code: 'Y', employeeCount: 0, dayCount: 0, lateMinutes: 0, earlyExitMinutes: 0, days: [] } });
      if (tail === 'processing') return json({ runId: id, jobId: null, status: 'Idle', processedEmployees: null, totalEmployees: null, startedAtUtc: null, finishedAtUtc: null, errorCode: null, errorMessage: null });
      if (tail === 'slips') return json({ items: [], total: 0, page: 1, pageSize: 100 });
      if (tail === 'gl-journal') return json({});
    }
    return json([]);
  });
  await page.goto(opts.url ?? `/payroll?tab=runs&run=${RUN}`);
  await expect(page.getByRole('heading', { name: 'Payroll Management' })).toBeVisible({ timeout: 30_000 });
  return s;
}

const panel = (page: Page) => page.getByTestId('manpower-recon');
const openTab = async (page: Page, name: string | RegExp) => {
  await panel(page).getByRole('tab', { name, exact: typeof name === 'string' }).click();
};
const serious = async (page: Page, selector: string) => {
  const results = await new AxeBuilder({ page }).include(selector).analyze();
  return results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
};

test.describe('volume bridge', () => {
  test('the bridge renders with its caption, opening, every movement and a closing that ties', async ({ page }) => {
    await boot(page);
    await expect(panel(page)).toBeVisible();
    await expect(panel(page).getByRole('tab', { name: 'Volume', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(panel(page).getByRole('table', { name: /Headcount bridge/ })).toBeVisible();
    await expect(page.getByTestId('recon-volume-opening')).toContainText('40');
    await expect(page.getByTestId('recon-volume-row-NEW_JOINER')).toContainText('+3');
    await expect(page.getByTestId('recon-volume-row-REHIRE')).toContainText('+1');
    await expect(page.getByTestId('recon-volume-row-LEAVER')).toContainText('−2');
    await expect(page.getByTestId('recon-volume-row-LEAVER')).toContainText('1 accrued');
    await expect(page.getByTestId('recon-volume-row-LEAVER')).toContainText('1 missing');
    await expect(page.getByTestId('recon-volume-closing')).toContainText('42');
    await expect(page.getByTestId('recon-volume-ties')).toContainText('The bridge ties');
    await expect(page.getByTestId('recon-volume-ties')).toContainText('40');
    await expect(page.getByTestId('recon-volume-ties')).toContainText('42');
  });

  test('the memo says what sits outside the bridge', async ({ page }) => {
    await boot(page);
    await expect(page.getByTestId('recon-volume-memo')).toContainText('final wage month');
    await expect(page.getByTestId('recon-volume-memo')).toContainText('Off-cycle');
  });

  test('a bridge whose server tie is false is not presented as a bridge', async ({ page }) => {
    await boot(page, {
      mutate: (d) => {
        d.volume.ties = false;
        d.integrity = { ok: false, issues: [{ code: 'duplicateSlip', message: 'Employee 12 has two slips in this run.' }] };
      },
    });
    await expect(page.getByTestId('recon-integrity')).toContainText('Employee 12 has two slips');
    await expect(page.getByTestId('recon-volume-ties')).toContainText('does not tie');
  });
});

test.describe('value bridge', () => {
  test('earnings, deductions and net pay tie, and employer cost sits apart from net pay', async ({ page }) => {
    await boot(page);
    await openTab(page, 'Value');
    const table = panel(page).getByRole('table', { name: /Value bridge/ });
    await expect(table).toBeVisible();
    await expect(page.getByTestId('recon-headline-gross')).toContainText('SAR');
    await expect(page.getByTestId('recon-headline-gross')).toContainText('572,445.00');
    await expect(page.getByTestId('recon-headline-deductions')).toContainText('47,220.00');
    await expect(page.getByTestId('recon-headline-net')).toContainText('525,225.00');
    await expect(page.getByTestId('recon-headline-employer')).toContainText('69,390.00');
    // Employer cost is its own section, labelled as outside net pay.
    await expect(panel(page).getByText('Employer cost (not part of net pay)')).toBeVisible();
    await expect(page.getByTestId('recon-item-Employer-GOSI_ER')).toBeVisible();
    // Exactly one item moved more than the threshold: it carries a Review flag, the others do not.
    await expect(page.getByTestId('recon-flag-Earning-OVERTIME')).toBeVisible();
    await expect(page.getByTestId('recon-flag-Earning-BASIC')).toHaveCount(0);
    await expect(page.getByTestId('recon-register-difference')).toContainText('0.00');
  });

  test('a row opens its breakdown into joiners, leavers and what moved among continuing staff', async ({ page }) => {
    await boot(page);
    await openTab(page, 'Value');
    const toggle = panel(page).getByRole('button', { name: 'Show breakdown for Basic salary' });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
    await expect(panel(page).getByRole('button', { name: 'Hide breakdown for Basic salary' })).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('recon-breakdown-Earning-BASIC-newJoiner')).toContainText('+36,000.00');
    await expect(page.getByTestId('recon-breakdown-Earning-BASIC-leaver')).toContainText('−21,500.00');
    await expect(page.getByTestId('recon-breakdown-Earning-BASIC-package')).toContainText('+6,200.00');
    await expect(page.getByTestId('recon-breakdown-Earning-BASIC-proration')).toContainText('−1,100.00');
    // Empty cells are not listed: there is nothing to explain.
    await expect(page.getByTestId('recon-breakdown-Earning-BASIC-lateJoiner')).toHaveCount(0);
  });
});

test.describe('drill-down', () => {
  test('a headcount opens the employees behind it, asks for exactly that cell, and Escape returns focus', async ({ page }) => {
    const s = await boot(page);
    const opener = page.getByTestId('recon-volume-row-NEW_JOINER').getByRole('button');
    await opener.click();
    const dialog = page.getByRole('dialog', { name: /New joiners/ });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId('recon-drilldown-row')).toHaveCount(3);
    await expect(dialog).toContainText('Employee Number 1');
    expect(s.employeeQueries.at(-1)?.get('section')).toBe('volume');
    expect(s.employeeQueries.at(-1)?.get('category')).toBe('NEW_JOINER');
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(opener).toBeFocused();
  });

  test('a value cell asks for its kind, component and category', async ({ page }) => {
    const s = await boot(page);
    await openTab(page, 'Value');
    await panel(page).getByRole('button', { name: 'Show breakdown for Basic salary' }).click();
    await page.getByTestId('recon-breakdown-Earning-BASIC-leaver').getByRole('button').click();
    await expect(page.getByRole('dialog')).toBeVisible();
    const q = s.employeeQueries.at(-1)!;
    expect(q.get('section')).toBe('value');
    expect(q.get('kind')).toBe('Earning');
    expect(q.get('itemCode')).toBe('BASIC');
    expect(q.get('category')).toBe('LEAVER');
    await page.getByRole('dialog').getByRole('button', { name: 'Close' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  test('a drill-down that fails says so and can be retried, instead of listing nobody', async ({ page }) => {
    await boot(page);
    let calls = 0;
    await page.route('**/recon/employees*', (route) => {
      calls++;
      if (calls === 1) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'Down for maintenance.' }) });
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(employeesPage(2)) });
    });
    await page.getByTestId('recon-volume-row-NEW_JOINER').getByRole('button').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('alert')).toContainText('could not be loaded');
    await expect(dialog.getByTestId('recon-drilldown-row')).toHaveCount(0);
    await dialog.getByRole('button', { name: 'Retry' }).click();
    await expect(dialog.getByTestId('recon-drilldown-row')).toHaveCount(2);
  });
});

test.describe('final settlement', () => {
  test('settlements are placed in exactly one cell, per-component change is shown, and leavers without one are listed', async ({ page }) => {
    await boot(page);
    await openTab(page, 'Final settlement');
    await expect(panel(page).getByText(/accrual basis/i)).toBeVisible();
    await expect(page.getByTestId('recon-fs-cell-LEAVER_IN_BRIDGE')).toContainText('1');
    await expect(page.getByTestId('recon-fs-cell-FINAL_WAGE_MONTH')).toContainText('1');
    await expect(page.getByTestId('recon-fs-total')).toContainText('2');
    await expect(page.getByTestId('recon-fs-component-EOSB_GRATUITY')).toContainText('82,400.00');
    await expect(page.getByTestId('recon-fs-component-EOSB_GRATUITY')).toContainText('+27,400.00');
    await expect(page.getByTestId('recon-fs-component-NOTICE_SHORTFALL')).toContainText('−1,500.00');
    await expect(page.getByTestId('recon-fs-missing-1042')).toContainText('Hassan Al-Qahtani');
  });
});

test.describe('sign-off', () => {
  test('is blocked until each flagged item has a reason and the flagged count is confirmed; then Approve is the next step', async ({ page }) => {
    const s = await boot(page);
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
    await openTab(page, 'Sign-off');
    const button = page.getByTestId('recon-signoff-button');
    await expect(button).toBeDisabled();
    await expect(page.getByTestId('recon-signoff-blocker')).toContainText('written reason');

    const reason = page.getByTestId('recon-explanation-Item-Earning-OVERTIME');
    await expect(reason).toBeVisible();
    await reason.fill('too short');
    await expect(button).toBeDisabled();
    await reason.fill('Seasonal overtime for the Q4 stock count');
    await expect(button).toBeDisabled();
    await expect(page.getByTestId('recon-signoff-blocker')).toContainText('3 employees');

    await page.getByTestId('recon-ack-flagged').check();
    await expect(button).toBeEnabled();
    await button.click();

    await expect(page.getByTestId('recon-signoff-status')).toContainText('Signed off by Layla Al-Harbi');
    expect(s.signoffBodies).toHaveLength(1);
    expect(s.signoffBodies[0]).toMatchObject({
      expectedProcessVersion: 3,
      expectedSnapshotHash: 'sha256:7c1f0d8e5b2a49e3a6d4f1b0c9e8a7d6',
      acknowledgedFlaggedEmployeeCount: 3,
      explanations: [{ scope: 'Item', key: 'Earning:OVERTIME', text: 'Seasonal overtime for the Q4 stock count' }],
    });
    // The server holds the review now, so the pipeline moves on and names the signer.
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Approve the run');
    await expect(page.getByTestId('pipeline-stages').locator('[data-stage="review"]')).toContainText('Signed off by Layla Al-Harbi');
    await expect(page.getByTestId('review-complete')).toContainText('Layla Al-Harbi');
    await expect(page.getByTestId('review-complete-button')).toHaveCount(0);
    await expect(page.getByTestId('pipeline-live')).not.toBeEmpty();
  });

  test('a NOT_PAID employee needs a reason too, found by name in the list', async ({ page }) => {
    const s = await boot(page, { recon: 'recon-flagged.json' });
    await openTab(page, 'Sign-off');
    await expect(page.getByTestId('recon-explanation-Employee-200')).toBeVisible();
    await expect(page.getByTestId('recon-employee-reason-200')).toContainText('Employee Number 1');
    await page.getByTestId('recon-explanation-Item-Earning-OVERTIME').fill('Seasonal overtime for the Q4 stock count');
    await page.getByTestId('recon-explanation-Item-Earning-BONUS').fill('Board-approved Q3 performance bonus');
    await page.getByTestId('recon-ack-flagged').check();
    await expect(page.getByTestId('recon-signoff-button')).toBeDisabled();
    await expect(page.getByTestId('recon-signoff-blocker')).toContainText('written reason');
    await page.getByTestId('recon-explanation-Employee-200').fill('On unpaid leave until the 20th, paid next month');
    await expect(page.getByTestId('recon-signoff-button')).toBeEnabled();
    await page.getByTestId('recon-signoff-button').click();
    await expect(page.getByTestId('recon-signoff-status')).toContainText('Signed off');
    expect(s.signoffBodies[0].explanations.map((e: any) => `${e.scope}:${e.key}`)).toEqual(['Item:Earning:OVERTIME', 'Item:Earning:BONUS', 'Employee:200']);
  });

  test('a stale refusal reloads the reconciliation and says the figures moved', async ({ page }) => {
    const s = await boot(page);
    s.signoffError = { status: 409, body: { error: 'signoff_stale', message: 'The run was processed again.' } };
    await openTab(page, 'Sign-off');
    await page.getByTestId('recon-explanation-Item-Earning-OVERTIME').fill('Seasonal overtime for the Q4 stock count');
    await page.getByTestId('recon-ack-flagged').check();
    const before = s.reconCalls;
    await page.getByTestId('recon-signoff-button').click();
    await expect(page.getByTestId('recon-signoff-error')).toContainText('changed since you opened it');
    await expect.poll(() => s.reconCalls).toBeGreaterThan(before);
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
  });

  test('without payroll.approve the sign-off is shown but withheld, with the reason', async ({ page }) => {
    await boot(page, { permissions: ['dashboard.read', 'payroll.read', 'employees.read'] });
    await openTab(page, 'Sign-off');
    await expect(page.getByTestId('recon-signoff-button')).toBeDisabled();
    await expect(page.getByTestId('recon-signoff-blocker')).toContainText('permission');
  });

  test('a Superseded sign-off says why it no longer counts and the review is open again', async ({ page }) => {
    const d = fixture('recon-full.json');
    d.signoff = { id: 'so-0', status: 'Superseded', signedOffByName: 'Layla Al-Harbi', signedOffAtUtc: '2026-11-01T09:00:00Z', snapshotHash: 'sha256:old', supersededReason: 'Reprocessed' };
    const s = await boot(page);
    s.recon = d;
    await page.reload();
    await openTab(page, 'Sign-off');
    await expect(page.getByTestId('recon-signoff-superseded')).toContainText('processed again');
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
  });

  test('an Active sign-off can be withdrawn only with a reason', async ({ page }) => {
    const s = await boot(page, { recon: 'recon-signed.json' });
    let withdrawn: unknown = null;
    await page.route('**/recon/signoff', (route) => {
      if (route.request().method() !== 'DELETE') return route.fallback();
      withdrawn = route.request().postDataJSON();
      s.recon = { ...s.recon, signoff: { ...s.recon.signoff, status: 'Superseded', supersededReason: 'Withdrawn' } };
      return route.fulfill({ status: 204 });
    });
    // A signed run moves on to Approve; open the Review stage to see the sign-off again.
    await page.getByTestId('pipeline-stages').getByRole('button', { name: 'Review', exact: true }).click();
    await openTab(page, 'Sign-off');
    await page.getByRole('button', { name: 'Withdraw sign-off' }).click();
    const confirm = page.getByTestId('recon-withdraw-confirm');
    await expect(confirm).toBeDisabled();
    await page.getByLabel('Reason for withdrawing').fill('Payroll was re-run after a late joiner');
    await confirm.click();
    await expect.poll(() => withdrawn).toEqual({ reason: 'Payroll was re-run after a late joiner' });
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
  });
});

test.describe('Approve is refused without a current sign-off', () => {
  test('a 409 reconciliation_not_reviewed says why and takes the run back to Review', async ({ page }) => {
    const s = await boot(page, { recon: 'recon-signed.json', url: `/payroll?tab=approvals&run=${RUN}` });
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Approve the run');
    // The run is processed again elsewhere: the sign-off no longer counts, and the server says so at Approve.
    s.approveError = { status: 409, body: { error: 'reconciliation_not_reviewed', reason: 'Stale', processVersion: 4, signedVersion: 3, message: 'The reconciliation was signed off on an earlier processing of this run.' } };
    s.recon = { ...fixture('recon-full.json'), processVersion: 4, signoff: { id: 'so-1', status: 'Superseded', signedOffByName: 'Layla Al-Harbi', signedOffAtUtc: '2026-11-02T09:15:00Z', snapshotHash: 'x', supersededReason: 'Reprocessed' } };
    await page.getByRole('button', { name: 'Approve — Final' }).click();
    await expect.poll(() => s.approveCalls).toBe(1);
    await expect(page.getByTestId('approve-reconciliation-notice')).toContainText('signed off');
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
    await expect(page.getByTestId('manpower-recon')).toBeVisible();
    await expect(page.getByTestId('pipeline-stages').locator('[aria-current="step"]')).toHaveAttribute('data-stage', 'review');
  });
});

test.describe('scope, prior month and failures', () => {
  test('a department-scoped read is labelled partial and can never be signed', async ({ page }) => {
    await boot(page, { recon: 'recon-partial.json' });
    await expect(page.getByTestId('recon-partial-banner')).toContainText('part of the company');
    await expect(page.getByTestId('recon-export-xlsx')).toContainText('partial');
    await openTab(page, 'Sign-off');
    await expect(page.getByTestId('recon-signoff-button')).toBeDisabled();
    await expect(page.getByTestId('recon-signoff-blocker')).toContainText('whole company');
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
  });

  test('with no prior month the closing and its composition are shown, no movement is invented, and sign-off is still required', async ({ page }) => {
    await boot(page, { recon: 'recon-noprior.json' });
    await expect(page.getByTestId('recon-noprior')).toContainText('2026-08');
    await expect(page.getByTestId('recon-volume-closing')).toContainText('42');
    await expect(page.getByTestId('recon-volume-row-NEW_JOINER')).toHaveCount(0);
    await openTab(page, 'Value');
    await expect(page.getByTestId('recon-headline-net')).toContainText('525,225.00');
    await openTab(page, 'Sign-off');
    await expect(page.getByTestId('recon-explanation-Item-Earning-OVERTIME')).toHaveCount(0);
    await expect(page.getByTestId('recon-signoff-button')).toBeEnabled();
  });

  test('an off-cycle run is not gated: no reconciliation is asked for and Approve is the next step', async ({ page }) => {
    const s = await boot(page, { url: `/payroll?tab=runs&run=${RUN_OFFCYCLE}` });
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Approve the run');
    expect(s.reconCalls).toBe(0);
    await expect(page.getByTestId('pipeline-stages').locator('[data-stage="review"]')).toContainText('No reconciliation sign-off is needed');
  });

  test('a failed load is an error with Retry, never an empty bridge', async ({ page }) => {
    const s = await boot(page, { failFirstRecon: true });
    await expect(page.getByTestId('recon-load-failed')).toContainText('could not be loaded');
    await expect(page.getByTestId('recon-load-failed')).toContainText('busy');
    await expect(page.getByTestId('recon-volume-opening')).toHaveCount(0);
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
    await page.getByTestId('recon-load-failed').getByRole('button', { name: 'Retry' }).click();
    await expect(page.getByTestId('recon-volume-opening')).toContainText('40');
    expect(s.reconCalls).toBe(2);
  });

  test('a reply of the wrong shape is a failed load, not a blank screen', async ({ page }) => {
    const s = await boot(page);
    s.reconBody = [];
    await page.reload();
    await expect(page.getByTestId('recon-load-failed')).toBeVisible();
  });
});

test.describe('export', () => {
  test('XLSX and CSV download under the server filename and ask for their format', async ({ page }) => {
    const s = await boot(page);
    const xlsx = page.waitForEvent('download');
    await page.getByTestId('recon-export-xlsx').click();
    expect((await xlsx).suggestedFilename()).toBe('payroll-reconciliation-2026-11-UNSIGNED-DRAFT.xlsx');
    const csv = page.waitForEvent('download');
    await page.getByTestId('recon-export-csv').click();
    expect((await csv).suggestedFilename()).toBe('payroll-reconciliation-2026-11-UNSIGNED-DRAFT.csv');
    expect(s.exports.map((q) => new URLSearchParams(q).get('format'))).toEqual(['xlsx', 'csv']);
  });
});

test.describe('accessibility', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`every tab and the drill-down pass axe in ${theme} mode`, async ({ page }) => {
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await boot(page, { theme });
      await expect(page.getByTestId('recon-volume-opening')).toBeVisible();
      // Tab pattern: one tablist, the selected tab is the only tab stop, panels are labelled by their tab.
      await expect(panel(page).getByRole('tablist')).toHaveCount(1);
      await expect(panel(page).getByRole('tab', { selected: true })).toHaveCount(1);
      await expect(panel(page).getByRole('tabpanel')).toHaveAttribute('aria-labelledby', 'recon-tab-volume');
      await expect(page.getByTestId('recon-live')).toHaveAttribute('aria-live', 'polite');
      expect(await serious(page, '[data-testid="manpower-recon"]'), `${theme} volume`).toEqual([]);

      await openTab(page, 'Value');
      await panel(page).getByRole('button', { name: 'Show breakdown for Basic salary' }).click();
      expect(await serious(page, '[data-testid="manpower-recon"]'), `${theme} value`).toEqual([]);

      await openTab(page, 'Final settlement');
      expect(await serious(page, '[data-testid="manpower-recon"]'), `${theme} final settlement`).toEqual([]);

      await openTab(page, 'Sign-off');
      expect(await serious(page, '[data-testid="manpower-recon"]'), `${theme} sign-off`).toEqual([]);

      await openTab(page, 'Volume');
      await page.getByTestId('recon-volume-row-NEW_JOINER').getByRole('button').click();
      await expect(page.getByRole('dialog')).toBeVisible();
      expect(await serious(page, '[role="dialog"]'), `${theme} drawer`).toEqual([]);
    });
  }

  test('the tabs move with the arrow keys', async ({ page }) => {
    await boot(page);
    await panel(page).getByRole('tab', { name: 'Volume', exact: true }).focus();
    await page.keyboard.press('ArrowRight');
    await expect(panel(page).getByRole('tab', { name: 'Value', exact: true })).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('End');
    await expect(panel(page).getByRole('tab', { name: 'Sign-off', exact: true })).toHaveAttribute('aria-selected', 'true');
  });
});

test.describe('Arabic', () => {
  test('renders right to left, in Arabic, with every figure isolated so digits keep their order', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await boot(page, { locale: 'ar' });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(panel(page)).toContainText('مطابقة');
    await expect(panel(page).getByRole('tab').first()).not.toHaveText('Volume');
    await expect(panel(page).locator('table td bdi').first()).toBeVisible();
    // No English leaks into the bridge chrome.
    const text = await panel(page).innerText();
    for (const english of ['Headcount bridge', 'Final settlement', 'Sign-off', 'Opening']) expect(text, english).not.toContain(english);
    expect(await serious(page, '[data-testid="manpower-recon"]')).toEqual([]);
  });
});

// Evidence screenshots, taken on demand: RECON_SCREENSHOTS=<dir> npx playwright test ... -g screenshots
test.describe('screenshots', () => {
  test.skip(!process.env.RECON_SCREENSHOTS, 'set RECON_SCREENSHOTS to a folder to capture the evidence screenshots');
  for (const locale of ['en', 'ar'] as const) {
    test(`screenshots ${locale}`, async ({ page }, info) => {
      test.skip(info.project.name !== 'desktop', 'one viewport is enough');
      const dir = process.env.RECON_SCREENSHOTS!;
      fs.mkdirSync(dir, { recursive: true });
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await boot(page, { locale, recon: 'recon-flagged.json' });
      const shot = async (name: string) => panel(page).screenshot({ path: path.join(dir, `${name}-${locale}.png`) });
      await expect(page.getByTestId('recon-volume-opening')).toBeVisible();
      await shot('volume');
      await openTab(page, locale === 'en' ? 'Value' : /^(القيمة|الأرقام|القيم)$/);
      await panel(page).getByRole('button', { name: /breakdown|تفصيل/ }).first().click();
      await shot('value');
      await panel(page).getByRole('tab').nth(2).click();
      await shot('final-settlement');
      await panel(page).getByRole('tab').nth(3).click();
      await shot('sign-off');
    });
  }
});
