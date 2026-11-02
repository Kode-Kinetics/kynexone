import { expect, test, type Page, type Route } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Payroll → the run pipeline, fixture lane (e2e/playwright.fixture.config.ts). Written without a run: the
 * sandbox cannot launch a browser; CI's Browser Pilot / the fixture lane runs it.
 *
 * Proves, against route-intercepted payloads:
 *   - the deep link ?tab=&run= (select, replace-not-push, graceful fallbacks)
 *   - the stage list (ordered list, one aria-current step, blocked stages say why)
 *   - the Review stop point (Approve is not the next action before it)
 *   - the lock summary in the app's confirm dialog (figures, change against the previous run, currency)
 *   - processing as a background job (progress, success reload, failure with retry)
 *   - dark-mode tables (header and totals bands are dark, axe finds nothing serious)
 */
const RUN_APPROVED = '11111111-1111-4111-8111-111111111111';
const RUN_PREV = '22222222-2222-4222-8222-222222222222';
const RUN_PROCESSED = '33333333-3333-4333-8333-333333333333';
const RUN_DRAFT = '44444444-4444-4444-8444-444444444444';
const COMPANY = { id: 'c1', name: 'IntelliFlow KSA', tradeName: '', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' };
const USER = {
  id: 'u1', tenantId: 't1', tenantSlug: 'intelliflow', email: 'admin@intelliflow.test', fullName: 'Yaser Al-Ghamdi',
  roles: ['Admin'], accountType: 'Tenant', isGroupScope: true, companies: [],
  permissions: ['dashboard.read', 'payroll.read', 'payroll.write', 'payroll.approve', 'payroll.lock', 'employees.read'],
};
const run = (id: string, month: number, status: string, over: Record<string, unknown> = {}) => ({
  id, companyId: 'c1', year: 2026, month, status, runType: 'Regular',
  totalGrossSalary: 100000, totalDeductions: 10000, totalNetSalary: 90000, totalEmployerStatutoryCost: 9000, employeeCount: 40,
  createdAtUtc: '2026-10-01T08:00:00Z', processedAtUtc: status === 'Draft' ? null : '2026-10-02T08:00:00Z', lockedAtUtc: null, ...over,
});
const RUNS = [
  run(RUN_APPROVED, 10, 'Approved', { totalGrossSalary: 103000, totalDeductions: 10300, totalNetSalary: 92700, employeeCount: 42 }),
  run(RUN_PREV, 9, 'Locked', { lockedAtUtc: '2026-09-28T08:00:00Z' }),
  run(RUN_PROCESSED, 11, 'Processed'),
  run(RUN_DRAFT, 12, 'Draft'),
];

type Mock = {
  runs?: unknown[];
  openErrors?: number;
  processing?: (n: number) => unknown;
  onLock?: () => void;
  onProcess?: () => void;
  overview?: unknown;
};

async function open(page: Page, url: string, opts: Mock & { theme?: 'light' | 'dark' } = {}) {
  let processingCalls = 0;
  await page.addInitScript((th) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', th);
  }, opts.theme ?? 'light');
  await page.route('**/api/**', (route: Route) => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    const runs = opts.runs ?? RUNS;
    if (p === '/api/auth/me') return json(USER);
    if (p === '/api/payroll/companies') return json([COMPANY]);
    if (p === '/api/payroll/runs' && req.method() === 'GET') return json({ items: runs, total: runs.length, page: Number(new URL(req.url()).searchParams.get('page') ?? 1), pageSize: 100 });
    if (p === '/api/payroll/payment-batches') return json([]);
    if (p === '/api/payroll/overview') return json(opts.overview ?? { year: 2026, month: 10, totalCompanies: 0, totalActiveEmployees: 0, totalGrossPayroll: 0, totalNetPayroll: 0, totalValidationErrors: 0, totalPendingApprovals: 0, companies: [] });
    if (p === '/api/payroll/readiness') return json({ year: 2026, month: 10, companyId: null, completionPercent: 100, isReadyForProcessing: true, totalActiveEmployees: 40, employeesWithSalary: 40, salaryCoveragePercent: 100, validationErrors: 0, payrollRunStatus: null, steps: [] });
    if (p === '/api/payroll/reports/summary') return json({ totalRuns: 1, lockedRuns: 1, totalEmployeesPaid: 40, totalGrossYtd: 0, totalNetYtd: 0 });
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p.startsWith('/api/features') || p === '/api/notifications') return json([]);
    const m = /^\/api\/payroll\/runs\/([^/]+)\/(.+)$/.exec(p);
    if (m) {
      const [, , tail] = m;
      if (tail === 'validate' && req.method() === 'GET') {
        const errors = Array.from({ length: opts.openErrors ?? 0 }, (_, i) => ({ id: `e${i}`, employeeId: 100 + i, severity: 'Error', code: 'MISSING_IBAN', message: 'Missing IBAN', isResolved: false }));
        return json([...errors, { id: 'w1', employeeId: 7, severity: 'Warning', code: 'W', message: 'Late arrivals held', isResolved: false }]);
      }
      if (tail === 'validation-overrides') return json({ runId: m[1], overrides: [{ id: 'o1' }], overridableCodes: [], nonOverridableCodes: [] });
      if (tail === 'approvals') return json([]);
      if (tail === 'population') return json({ runId: m[1], includedCount: 42, excludedCount: 2, notEligibleCount: 0, included: [], excluded: [], notEligible: [] });
      if (tail === 'attendance-reconciliation') return json({ runId: m[1], from: '', to: '', consumesAttendance: true, stale: { code: 'X', employeeCount: 0, dayCount: 0, days: [] }, held: { code: 'Y', employeeCount: 0, dayCount: 0, lateMinutes: 0, earlyExitMinutes: 0, days: [] } });
      if (tail === 'processing') return json(opts.processing ? opts.processing(++processingCalls) : { runId: m[1], jobId: null, status: 'Idle', processedEmployees: null, totalEmployees: null, startedAtUtc: null, finishedAtUtc: null, errorCode: null, errorMessage: null });
      if (tail === 'process' && req.method() === 'POST') { opts.onProcess?.(); return json({ runId: m[1], jobId: 'job-1', status: 'Queued' }, 202); }
      if (tail === 'lock' && req.method() === 'POST') { opts.onLock?.(); return json({ ...RUNS[0], status: 'Locked', lockedAtUtc: '2026-10-27T08:00:00Z' }); }
      if (tail === 'slips') return json({ items: [], total: 0, page: 1, pageSize: 100 });
    }
    if (p === '/api/payroll/reports/reconciliation') return json({ runId: RUN_PROCESSED, period: '2026-11', priorPeriod: '2026-10', currentHeadcount: 40, priorHeadcount: 38, joinerCount: 3, leaverCount: 1, currentTotalGross: 100000, priorTotalGross: 95000, currentTotalNet: 90000, priorTotalNet: 86000, flaggedVariances: 0, variances: [] });
    return json([]);
  });
  await page.goto(url);
  await expect(page.getByRole('heading', { name: 'Payroll Management' })).toBeVisible({ timeout: 30_000 });
}

const stages = (page: Page) => page.getByTestId('pipeline-stages').locator('> li');

test.describe('payroll deep link', () => {
  test('?tab=&run= selects the tab and the run', async ({ page }) => {
    await open(page, `/payroll?tab=validation&run=${RUN_APPROVED}`);
    await expect(page.getByRole('tab', { name: 'Validation', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('#pipeline-run-picker')).toHaveValue(RUN_APPROVED);
    await expect(stages(page)).toHaveCount(8);
  });

  test('selecting a tab updates the URL with replace, not push', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_APPROVED}`);
    const before = await page.evaluate(() => history.length);
    await page.getByRole('tab', { name: 'Approvals', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`tab=approvals.*run=${RUN_APPROVED}|run=${RUN_APPROVED}.*tab=approvals`));
    await page.locator('#pipeline-run-picker').selectOption(RUN_PREV);
    await expect(page).toHaveURL(new RegExp(RUN_PREV));
    expect(await page.evaluate(() => history.length)).toBe(before);
  });

  test('an unknown tab falls back to the dashboard and the address is cleaned', async ({ page }) => {
    await open(page, '/payroll?tab=nope&run=not-a-guid');
    await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(page).toHaveURL(/\/payroll$/);
  });

  test('a run that does not exist selects nothing and says so', async ({ page }) => {
    await open(page, '/payroll?tab=runs&run=99999999-9999-4999-8999-999999999999');
    await expect(page.getByTestId('pipeline-run-missing')).toBeVisible();
    await expect(page.locator('#pipeline-run-picker')).toHaveValue('');
    await expect(page).not.toHaveURL(/run=9999/);
  });
});

test.describe('payroll run pipeline', () => {
  test('stages are an ordered list with exactly one current step, and runs needing attention are grouped', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_APPROVED}`);
    const list = page.getByTestId('pipeline-stages');
    expect(await list.evaluate((el) => el.tagName)).toBe('OL');
    await expect(list.locator('[aria-current="step"]')).toHaveCount(1);
    await expect(list.locator('[aria-current="step"]')).toHaveAttribute('data-stage', 'locked');
    await expect(list.locator('[data-stage="approved"]')).toHaveAttribute('data-state', 'done');
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Lock the run');
    const attention = page.locator('#pipeline-run-picker optgroup[label="Needs attention"] option');
    // Approved, Processed and Draft wait on someone; so does the Locked run, which has no bank file yet.
    await expect(attention).toHaveCount(4);
  });

  test('open validation errors block the stage and say why in words', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_PROCESSED}`, { openErrors: 2 });
    await expect(page.getByTestId('stage-reason-validated')).toContainText('2 validation errors are open');
    await expect(page.getByTestId('pipeline-next-reason')).toContainText('Resolve or override');
    await page.getByTestId('pipeline-next-action').click();
    await expect(page.getByRole('tab', { name: 'Validation', exact: true })).toHaveAttribute('aria-selected', 'true');
  });

  test('Review is a stop point: Approve is only the next step after the review is marked complete', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_PROCESSED}`);
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
    await expect(page.getByTestId('stage-reason-approved')).toContainText('mark it complete');
    await expect(page.getByTestId('run-review')).toBeVisible();
    await expect(page.getByTestId('review-row-gross')).toContainText('SAR');
    await page.getByTestId('review-complete-button').click();
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Approve the run');
    await expect(page.getByTestId('pipeline-stages').locator('[aria-current="step"]')).toHaveAttribute('data-stage', 'approved');
  });

  test('Lock shows the summary in the confirm dialog; cancelling locks nothing, confirming locks', async ({ page }) => {
    let locked = 0;
    await open(page, `/payroll?tab=runs&run=${RUN_APPROVED}`, { onLock: () => { locked++; } });
    await page.getByTestId('pipeline-next-action').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    const summary = dialog.getByTestId('lock-summary');
    await expect(summary.getByTestId('lock-summary-headcount')).toContainText('42');
    await expect(summary.getByTestId('lock-summary-headcount')).toContainText('+2');
    await expect(summary.getByTestId('lock-summary-gross')).toContainText('SAR');
    await expect(summary.getByTestId('lock-summary-gross')).toContainText(/\+SAR\s3,000\.00/);
    await expect(summary.getByTestId('lock-summary-gross')).toContainText('September 2026');
    await expect(summary.getByTestId('lock-summary-deductions')).toBeVisible();
    await expect(summary.getByTestId('lock-summary-net')).toContainText('92,700.00');
    await expect(summary.getByTestId('lock-summary-excluded')).toContainText('2');
    await expect(summary.getByTestId('lock-summary-overrides')).toContainText('1');
    await expect(summary.getByTestId('lock-summary-currency')).toContainText('SAR');

    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toHaveCount(0);
    expect(locked).toBe(0);

    await page.getByTestId('pipeline-next-action').click();
    await page.getByRole('dialog').getByRole('button', { name: 'Lock run' }).click();
    await expect.poll(() => locked).toBe(1);
  });

  test('Lock is withheld, with a reason, when the company currency cannot be confirmed', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_APPROVED}`, { runs: RUNS.map((r) => ({ ...r, companyId: 'c-unknown' })) });
    await expect(page.getByTestId('pipeline-next-action')).toBeDisabled();
    await expect(page.getByTestId('pipeline-next-reason')).toContainText('currency');
  });
});

test.describe('processing as a background job', () => {
  test('progress is shown while Running, announced politely, and the run is reloaded when it succeeds', async ({ page }) => {
    let processed = false;
    let afterPost = 0;
    const draft = run(RUN_DRAFT, 12, 'Draft');
    await open(page, `/payroll?tab=runs&run=${RUN_DRAFT}`, {
      runs: [draft],
      onProcess: () => { processed = true; },
      // Queueing re-reads the status twice at once (refresh + the page's version bump), so the job stays Running
      // for the first three reads after the POST: long enough to see one 2 s poll tick with progress on screen.
      processing: () => (!processed ? { runId: RUN_DRAFT, jobId: null, status: 'Idle' }
        : ++afterPost <= 3 ? { runId: RUN_DRAFT, jobId: 'job-1', status: 'Running', processedEmployees: 312, totalEmployees: 1200, startedAtUtc: '2026-10-27T08:00:00Z' }
        : { runId: RUN_DRAFT, jobId: 'job-1', status: 'Succeeded', processedEmployees: 1200, totalEmployees: 1200, startedAtUtc: '2026-10-27T08:00:00Z', finishedAtUtc: '2026-10-27T08:01:00Z' }),
    });
    await expect(page.getByTestId('pipeline-live')).toHaveAttribute('aria-live', 'polite');
    await page.getByTestId('pipeline-next-action').click();
    const bar = page.getByRole('progressbar', { name: 'Processing progress' });
    await expect(bar).toBeVisible({ timeout: 10_000 });
    await expect(bar).toHaveAttribute('aria-valuenow', '312');
    await expect(page.getByTestId('pipeline-progress')).toContainText('312 of 1,200 employees');
    await expect(page.getByTestId('pipeline-live')).not.toBeEmpty();
    await expect(bar).toHaveCount(0, { timeout: 15_000 });
  });

  test('a failed job shows the server message and offers Retry', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_DRAFT}`, {
      processing: () => ({ runId: RUN_DRAFT, jobId: 'job-9', status: 'Failed', processedEmployees: 10, totalEmployees: 40, errorCode: 'NO_PACK', errorMessage: 'The company has no statutory pack.' }),
    });
    await expect(page.getByTestId('pipeline-processing-failed')).toContainText('The company has no statutory pack.');
    await expect(page.getByTestId('pipeline-next-action')).toHaveText('Retry');
  });
});

test.describe('payroll tables in dark mode', () => {
  const OVERVIEW = {
    year: 2026, month: 10, totalCompanies: 2, totalActiveEmployees: 80, totalGrossPayroll: 200000, totalNetPayroll: 180000, totalValidationErrors: 0, totalPendingApprovals: 0,
    companies: ['a', 'b'].map((x, i) => ({ companyId: `c${x}`, companyName: `Company ${x}`, tradeName: '', currency: 'SAR', activeEmployees: 40, employeesWithSalary: 40, employeesMissingSalary: 0, salaryCoveragePercent: 100, payrollRunStatus: 'Locked', grossPayroll: 100000 + i, totalDeductions: 10000, netPayroll: 90000, validationErrors: 0, validationWarnings: 0, pendingApprovals: 0, wpsEmployerId: '', gosiEmployerId: '', hasPayrollRun: true })),
  };
  const luminance = (rgb: string) => {
    const [r, g, b] = (rgb.match(/[\d.]+/g) ?? ['255', '255', '255']).slice(0, 3).map(Number);
    return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
  };

  test('the header and totals bands are dark, and axe finds nothing serious', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await open(page, '/payroll', { theme: 'dark', overview: OVERVIEW });
    const header = page.locator('table thead tr').first();
    await expect(header).toBeVisible();
    const headerBg = await header.evaluate((el) => getComputedStyle(el).backgroundColor);
    expect(luminance(headerBg), `header band ${headerBg} must be dark`).toBeLessThan(0.35);
    const totals = page.getByText('Group totals:').locator('xpath=ancestor::div[contains(@class,"border-t")][1]');
    const totalsBg = await totals.evaluate((el) => getComputedStyle(el).backgroundColor);
    expect(luminance(totalsBg), `totals band ${totalsBg} must be dark`).toBeLessThan(0.35);

    const results = await new AxeBuilder({ page }).include('main').analyze();
    const serious = results.violations
      .filter((v) => v.impact === 'serious' || v.impact === 'critical')
      .map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) }));
    expect(serious).toEqual([]);
  });

  test('the pipeline passes axe in dark and light', async ({ page }) => {
    for (const theme of ['dark', 'light'] as const) {
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await open(page, `/payroll?tab=runs&run=${RUN_APPROVED}`, { theme });
      await expect(page.getByTestId('pipeline-stages')).toBeVisible();
      const results = await new AxeBuilder({ page }).include('[data-testid="run-pipeline"]').analyze();
      const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id);
      expect(serious, `${theme} theme`).toEqual([]);
      await page.unroute('**/api/**');
    }
  });
});
