import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Payroll → the run pipeline, fixture lane (e2e/playwright.fixture.config.ts). Written without a run: the
 * sandbox cannot launch a browser; CI's Browser Pilot / the fixture lane runs it.
 *
 * Proves, against route-intercepted payloads:
 *   - the deep link ?tab=&run= (select, replace-not-push, graceful fallbacks)
 *   - the stage list (ordered list, one aria-current step, blocked stages say why)
 *   - the Review stop point (Approve is not the next action before the server holds an Active reconciliation sign-off)
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
  /** The reconciliation the server returns for the run (e2e/fixtures/recon-*.json). */
  recon?: unknown;
  /** Payment batches the server lists (all runs), and the records of any batch. */
  batches?: unknown[];
  records?: unknown[];
  /** The signed-in user (default: an admin without the payment permissions). */
  user?: unknown;
};

async function open(page: Page, url: string, opts: Mock & { theme?: 'light' | 'dark'; locale?: 'en' | 'ar'; stages?: 'open' | 'folded' | 'default' } = {}) {
  let processingCalls = 0;
  await page.addInitScript(([th, locale, stages]) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', th);
    if (locale) localStorage.setItem('kynexone-locale-choice-v2', locale);
    // The stage cards start folded once a run is chosen; the stage assertions below read them, so they open them (the folded default has its own tests).
    if (stages !== 'default') localStorage.setItem('kynexone-payroll-pipeline-stages', stages);
  }, [opts.theme ?? 'light', opts.locale ?? '', opts.stages ?? 'open'] as const);
  await page.route('**/api/**', (route: Route) => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    const runs = opts.runs ?? RUNS;
    if (p === '/api/auth/me') return json(opts.user ?? USER);
    if (p === '/api/payroll/companies') return json([COMPANY]);
    if (p === '/api/payroll/runs' && req.method() === 'GET') return json({ items: runs, total: runs.length, page: Number(new URL(req.url()).searchParams.get('page') ?? 1), pageSize: 100 });
    if (p === '/api/payroll/payment-batches') {
      const runFilter = new URL(req.url()).searchParams.get('runId');
      const all = (opts.batches ?? []) as { payrollRunId: string }[];
      return json(runFilter ? all.filter((b) => b.payrollRunId === runFilter) : all);
    }
    if (/^\/api\/payroll\/payment-batches\/[^/]+\/records$/.test(p)) return json(opts.records ?? []);
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
      if (tail === 'recon' && req.method() === 'GET') return opts.recon ? json(opts.recon) : json({ error: 'not_mocked' }, 404);
    }
    if (p === '/api/payroll/reports/reconciliation') return json({ runId: RUN_PROCESSED, period: '2026-11', priorPeriod: '2026-10', currentHeadcount: 40, priorHeadcount: 38, joinerCount: 3, leaverCount: 1, currentTotalGross: 100000, priorTotalGross: 95000, currentTotalNet: 90000, priorTotalNet: 86000, flaggedVariances: 0, variances: [] });
    return json([]);
  });
  await page.goto(url);
  await expect(page.getByTestId('payroll-section-panel')).toBeVisible({ timeout: 30_000 });
}

/** A Payroll section's entry in the section nav (a link; the open one is aria-current="page"). */
/** Found by its stable id at every width (folded below 1280px; on a phone the list lives in a bottom sheet). */
const section = (page: Page, name: string) => page.locator('a[id^="payroll-tab-"]').filter({ hasText: new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')}$`) });
/** Opens a Payroll section like a user would: below 1280px the list is folded behind the "{group} › {section}" button. */
async function openSection(page: Page, name: string) {
  const link = section(page, name);
  if (!(await link.isVisible())) await page.getByTestId('payroll-section-toggle').click();
  await link.click();
}
const recon = (name: string) => JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', name), 'utf8'));
const stages = (page: Page) => page.getByTestId('pipeline-stages').locator('> li');

test.describe('payroll deep link', () => {
  test('?tab=&run= selects the tab and the run', async ({ page }) => {
    await open(page, `/payroll?tab=validation&run=${RUN_APPROVED}`);
    await expect(section(page, 'Validation')).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('#pipeline-run-picker')).toHaveValue(RUN_APPROVED);
    await expect(stages(page)).toHaveCount(8);
  });

  test('selecting a tab updates the URL with replace, not push', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_APPROVED}`);
    const before = await page.evaluate(() => history.length);
    await openSection(page, 'Approvals');
    await expect(page).toHaveURL(new RegExp(`tab=approvals.*run=${RUN_APPROVED}|run=${RUN_APPROVED}.*tab=approvals`));
    await page.locator('#pipeline-run-picker').selectOption(RUN_PREV);
    await expect(page).toHaveURL(new RegExp(RUN_PREV));
    expect(await page.evaluate(() => history.length)).toBe(before);
  });

  test('an unknown tab falls back to the dashboard and the address is cleaned', async ({ page }) => {
    await open(page, '/payroll?tab=nope&run=not-a-guid');
    await expect(section(page, 'Dashboard')).toHaveAttribute('aria-current', 'page');
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
    await expect(section(page, 'Validation')).toHaveAttribute('aria-current', 'page');
  });

  test('Review is a stop point: Approve is only the next step once the server holds an Active reconciliation sign-off', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_PROCESSED}`, { recon: recon('recon-full.json') });
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
    await expect(page.getByTestId('stage-reason-approved')).toContainText('sign off');
    await expect(page.getByTestId('run-review')).toBeVisible();
    await expect(page.getByTestId('recon-headline-gross')).toHaveCount(0); // Volume is the first tab
    await page.getByRole('tab', { name: 'Value', exact: true }).click();
    await expect(page.getByTestId('recon-headline-gross')).toContainText('SAR');
    // The old device-only "mark complete" button is gone.
    await expect(page.getByTestId('review-complete-button')).toHaveCount(0);
  });

  test('a review "marked" in this browser by an older version no longer counts', async ({ page }) => {
    await page.addInitScript((id) => { localStorage.setItem(`kynexone.payrollReview.${id}.2026-10-02T08:00:00Z`, '2026-10-02T09:00:00Z'); }, RUN_PROCESSED);
    await open(page, `/payroll?tab=runs&run=${RUN_PROCESSED}`, { recon: recon('recon-full.json') });
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Review the results');
  });

  test('with an Active sign-off the review stage is done, names the signer, and Approve is next', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_PROCESSED}`, { recon: recon('recon-signed.json') });
    await expect(page.getByTestId('pipeline-next-title')).toHaveText('Approve the run');
    await expect(page.getByTestId('pipeline-stages').locator('[data-stage="review"]')).toContainText('Signed off by Layla Al-Harbi');
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
    // Locking publishes the payslips to the employees, and the dialog says so before it happens.
    await expect(summary.getByTestId('lock-summary-payslips')).toContainText('Locking also publishes the payslips to the employees');

    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toHaveCount(0);
    expect(locked).toBe(0);

    await page.getByTestId('pipeline-next-action').click();
    await page.getByRole('dialog').getByRole('button', { name: 'Lock run' }).click();
    await expect.poll(() => locked).toBe(1);
  });

  test('[ar] the Lock dialog says in Arabic that locking publishes the payslips to the employees', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_APPROVED}`, { locale: 'ar' });
    await page.getByTestId('pipeline-next-action').click();
    const note = page.getByRole('dialog').getByTestId('lock-summary-payslips');
    await expect(note).toBeVisible();
    await expect(note).toContainText('قسائم الرواتب للموظفين');
    await expect(note).not.toContainText(/Locking|Self-Service/);
  });

  test('Lock is withheld, with a reason, when the company currency cannot be confirmed', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_APPROVED}`, { runs: RUNS.map((r) => ({ ...r, companyId: 'c-unknown' })) });
    await expect(page.getByTestId('pipeline-next-action')).toBeDisabled();
    await expect(page.getByTestId('pipeline-next-reason')).toContainText('currency');
  });
});

test.describe('Bank / WPS tab opened from the pipeline', () => {
  // Someone who prepares bank files (without a payment permission the tab says so instead; bank-guided.spec.ts).
  const PAYER = { ...USER, permissions: [...USER.permissions, 'payroll.export'] };
  const BATCH = {
    id: 'b1111111-1111-4111-8111-111111111111', payrollRunId: RUN_PREV, batchNumber: 'PB-2026-09-0001', paymentMethod: 'WPS',
    totalAmount: 90000, currency: 'SAR', status: 'Created', wpsStatus: 'NotGenerated', wpsStatusChangedAtUtc: null,
    wpsSubmissionReference: null, wpsRejectionReason: null, createdAtUtc: '2026-09-29T08:00:00Z', isSaudi: true,
  };
  // The run's only batch opens by itself: its number heads the facts strip of the Bank / WPS flow.
  const batchFacts = (page: Page) => page.getByTestId('bank-file-facts');
  const RECORD = {
    id: 'r1', paymentBatchId: BATCH.id, employeeId: 226, employeeName: 'Layla Al-Harbi', employeeCode: 'EMP-0226',
    amount: 9000, iban: 'SA03****0000', status: 'Pending', wpsReference: 'WPS-EMP-0226-202609',
  };

  test('"Open Bank / WPS" lands on the run the pipeline was showing, with its batch listed', async ({ page }) => {
    await open(page, `/payroll?tab=runs&run=${RUN_PREV}`, { batches: [BATCH], records: [RECORD], user: PAYER });
    // One button carries the next action; where it goes is its tooltip.
    await expect(page.getByTestId('pipeline-next-action')).toHaveText('Create the bank file');
    await expect(page.getByTestId('pipeline-next-action')).toHaveAttribute('title', /Open Bank \/ WPS/);
    await page.getByTestId('pipeline-next-action').click();
    await expect(section(page, 'Bank / WPS Files')).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('#pipeline-run-picker')).toHaveValue(RUN_PREV);
    await expect(page.getByText('No batches yet.')).toHaveCount(0);
    await expect(batchFacts(page)).toContainText(BATCH.batchNumber);
  });

  test('a deep link to the Bank / WPS tab preselects the run too', async ({ page }) => {
    await open(page, `/payroll?tab=bank-wps&run=${RUN_PREV}`, { batches: [BATCH], records: [RECORD], user: PAYER });
    await expect(page.locator('#pipeline-run-picker')).toHaveValue(RUN_PREV);
    await expect(batchFacts(page)).toContainText(BATCH.batchNumber);
  });

  test('payment records show the employee name, not a bare id', async ({ page }) => {
    await open(page, `/payroll?tab=bank-wps&run=${RUN_PREV}`, { batches: [BATCH], records: [RECORD], user: PAYER });
    // The payment records open in a drawer from the section's More menu.
    await page.getByTestId('bank-more').click();
    await page.getByRole('menuitem', { name: 'Payment records' }).click();
    const row = page.getByRole('row', { name: /WPS-EMP-0226-202609/ });
    await expect(row).toContainText('Layla Al-Harbi');
    await expect(row).not.toContainText('Emp #226');
    if (process.env.SHOT_DIR) await page.screenshot({ path: `${process.env.SHOT_DIR}/bank-wps-names.png`, fullPage: true });
  });

  test('a record from an older server with no name still shows the id', async ({ page }) => {
    const { employeeName: _n, employeeCode: _c, ...bare } = RECORD;
    await open(page, `/payroll?tab=bank-wps&run=${RUN_PREV}`, { batches: [BATCH], records: [bare], user: PAYER });
    // The payment records open in a drawer from the section's More menu.
    await page.getByTestId('bank-more').click();
    await page.getByRole('menuitem', { name: 'Payment records' }).click();
    await expect(page.getByRole('row', { name: /WPS-EMP-0226-202609/ })).toContainText('Emp #226');
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
    await expect(page.getByTestId('pipeline-next-action')).toHaveText('Retry processing');
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

test.describe('Pay out names the file that exists (a company whose bank file is the Saudi bank export)', () => {
  // RUN_PREV is Locked. Generating the INTERNAL payroll register sets its batch to Generated; it is not a bank file.
  const BATCH = {
    id: 'b2222222-2222-4222-8222-222222222222', payrollRunId: RUN_PREV, batchNumber: 'PB-2026-09-0002', paymentMethod: 'WPS',
    totalAmount: 90000, currency: 'SAR', status: 'Created', wpsStatus: 'Generated', effectiveWpsStatus: 'Generated',
    wpsStatusLabel: 'Generated (file ready for the bank)', wpsStatusChangedAtUtc: '2026-09-30T08:00:00Z',
    wpsSubmissionReference: null, wpsRejectionReason: null, createdAtUtc: '2026-09-29T08:00:00Z', isSaudi: true,
    bankFileAvailable: true, bankFileGenerated: false,
  };
  // The screen isolates the batch number (a left-to-right code) so an Arabic sentence does not reorder it.
  const LTR = (code: string) => `\u2066${code}\u2069`;
  const WORDS = {
    en: { stage: 'Pay out', noFile: 'No bank file yet', created: 'File created — not sent', bankFile: `Batch ${LTR(BATCH.batchNumber)}: Bank file generated`, done: 'Done' },
    ar: { stage: 'الدفع', noFile: 'لا يوجد ملف بنكي بعد', created: 'أُنشئ الملف — لم يُرسل', bankFile: `الدفعة ${LTR(BATCH.batchNumber)}: أُنشئ الملف البنكي`, done: 'مكتمل' },
  } as const;
  const paidStage = (page: Page) => page.getByTestId('pipeline-stages').locator('> li[data-stage="paid"]');
  const shot = async (page: Page, name: string) => {
    if (process.env.SHOT_DIR) await page.getByTestId('run-pipeline').screenshot({ path: `${process.env.SHOT_DIR}/${name}.png` });
  };

  for (const locale of ['en', 'ar'] as const) {
    test(`[${locale}] the internal register alone is "No bank file yet", in the stage, the chip and the run option`, async ({ page }) => {
      await open(page, `/payroll?tab=runs&run=${RUN_PREV}`, { batches: [BATCH], locale });
      if (locale === 'ar') await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
      await expect(paidStage(page)).toHaveAttribute('data-state', 'current');
      await expect(paidStage(page)).toHaveAttribute('aria-current', 'step');
      await expect(paidStage(page)).toContainText(WORDS[locale].stage);
      await expect(page.getByTestId('stage-reason-paid')).toHaveText(WORDS[locale].noFile);
      // The chip says it once; the run bar does not repeat it under the row.
      await expect(page.getByTestId('pipeline-next-reason')).toHaveCount(0);
      await expect(page.getByTestId('bank-guide-chip')).toHaveText(WORDS[locale].noFile);
      // The status is the chip's; the run picker shows the period only (its tooltip carries the status).
      await expect(page.locator('#pipeline-run-picker option:checked')).not.toContainText(WORDS[locale].noFile);
      await expect(page.locator('#pipeline-run-picker')).toHaveAttribute('title', new RegExp(WORDS[locale].noFile));
      await expect(paidStage(page)).not.toContainText(WORDS[locale].done);
      await shot(page, `pipeline-register-only-${locale}`);
    });

    test(`[${locale}] a bank file that is not yet with the bank is not Done: "File created — not sent", and the detail names the file`, async ({ page }) => {
      await open(page, `/payroll?tab=runs&run=${RUN_PREV}`, { batches: [{ ...BATCH, bankFileGenerated: true }], locale });
      await expect(paidStage(page)).toHaveAttribute('data-state', 'current');
      await expect(page.getByTestId('stage-reason-paid')).toHaveText(WORDS[locale].created);
      await expect(paidStage(page)).toContainText(WORDS[locale].bankFile);
      await expect(paidStage(page)).not.toContainText(WORDS[locale].done);
      await shot(page, `pipeline-bank-file-${locale}`);
    });
  }
});

test.describe('Pay out waits for the bank to confirm the payments', () => {
  // RUN_PREV is Locked, its bank file was recorded as submitted, and the bank has spoken about 2 of the 5 payments.
  const BATCH = {
    id: 'b3333333-3333-4333-8333-333333333333', payrollRunId: RUN_PREV, batchNumber: 'PB-2026-09-0003', paymentMethod: 'WPS',
    totalAmount: 90000, currency: 'SAR', status: 'FileGenerated', wpsStatus: 'Submitted', effectiveWpsStatus: 'Submitted',
    wpsStatusLabel: 'Submitted', wpsStatusChangedAtUtc: '2026-09-30T08:00:00Z',
    wpsSubmissionReference: 'ANB-1', wpsRejectionReason: null, createdAtUtc: '2026-09-29T08:00:00Z', isSaudi: true,
    bankFileAvailable: true, bankFileGenerated: true,
    unconfirmedPayments: { count: 3, total: 54000 }, confirmationCoverage: 0.4,
  };
  const WORDS = {
    en: { waiting: 'Submitted — waiting for the bank (3 of 5 waiting)', title: 'Confirm payments with the bank', action: 'Open Payment Tracking', done: 'Done', complete: 'All stages are complete.', confirmed: 'Bank confirmed every payment' },
    ar: { waiting: 'أُرسل — بانتظار البنك (3 من 5 بانتظار الرد)', title: 'تأكيد المدفوعات مع البنك', action: 'فتح تتبع المدفوعات', done: 'مكتمل', complete: 'اكتملت كل المراحل.', confirmed: 'أكّد البنك جميع المدفوعات' },
  } as const;
  const paidStage = (page: Page) => page.getByTestId('pipeline-stages').locator('> li[data-stage="paid"]');

  for (const locale of ['en', 'ar'] as const) {
    test(`[${locale}] a file the bank has not fully confirmed is Current, not Done, and points at Payment Tracking`, async ({ page }) => {
      await open(page, `/payroll?tab=runs&run=${RUN_PREV}`, { batches: [BATCH], locale });
      await expect(paidStage(page)).toHaveAttribute('data-state', 'current');
      await expect(page.getByTestId('stage-reason-paid')).toHaveText(WORDS[locale].waiting);
      await expect(paidStage(page)).not.toContainText(WORDS[locale].done);
      await expect(page.getByTestId('pipeline-next-title')).toHaveText(WORDS[locale].title);
      await expect(page.getByTestId('pipeline-next-title')).not.toHaveText(WORDS[locale].complete);
      await expect(page.getByTestId('pipeline-next-reason')).toHaveCount(0);
      await expect(page.getByTestId('bank-guide-chip')).toHaveText(WORDS[locale].waiting);
      await expect(page.getByTestId('pipeline-next-action')).toHaveText(WORDS[locale].title);
      await expect(page.getByTestId('pipeline-next-action')).toHaveAttribute('title', new RegExp(WORDS[locale].action));
      await page.getByTestId('pipeline-next-action').click();
      await expect(page).toHaveURL(/tab=payment-tracking/);
    });

    test(`[${locale}] the stage is Done once every payment is confirmed`, async ({ page }) => {
      await open(page, `/payroll?tab=runs&run=${RUN_PREV}`, { batches: [{ ...BATCH, unconfirmedPayments: { count: 0, total: 0 }, confirmationCoverage: 1, unresolvedReturns: { count: 0, total: 0 } }], locale });
      await expect(paidStage(page)).toHaveAttribute('data-state', 'done');
      await expect(paidStage(page)).toContainText(WORDS[locale].done);
      await expect(page.getByTestId('stage-reason-paid')).toHaveCount(0);
      await expect(page.getByTestId('bank-guide-chip')).toHaveText(WORDS[locale].confirmed);
      await expect(page.getByTestId('pipeline-next-title')).toHaveCount(0);
    });
  }
});
