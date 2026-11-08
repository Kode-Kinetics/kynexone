import { expect, test, type Page, type Request } from '@playwright/test';

/**
 * G-006: the Payroll Manager owns the off-cycle run that pays an approved final settlement.
 *   Payroll → Final Settlement: "Off-cycle runs to create" lists every approved settlement with no run, with the legal due date
 *   and a one-step "Create off-cycle run"; a settlement whose run exists links to it.
 *   Offboarding: the settlement block shows who is to create the run, or links the run once it exists.
 * Every API call is intercepted; nothing is written. Run: npx playwright test -c e2e/playwright.hotfix-dead-ends.config.ts
 */

const RUN_ID = '5f0c7e3a-1111-4222-8333-444455556666';

const row = (over: Record<string, unknown>) => ({
  id: 's-1', employeeId: 31, employeeCode: 'EMP-0031', employeeName: 'Layla Nasser', companyId: 'c1', offboardingId: 'o-1',
  lastWorkingDay: '2026-11-01', settlementDueDate: '2026-11-15', terminationReason: 'Termination', status: 'Approved', currency: 'SAR', netPayable: 18250.5,
  payrollRunId: null, paymentBatchId: null, paidAtUtc: null, isOverdue: false, overdueDays: 0, linkedRunId: null, linkedRunStatus: null, runDutyOpen: true, ...over,
});

interface Boot { permissions: string[]; rows: Array<Record<string, unknown>>; locale?: 'en' | 'ar'; createFails?: boolean }

async function boot(page: Page, url: string, opts: Boot) {
  const errors: string[] = [];
  const posts: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript((locale) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    if (locale === 'ar') localStorage.setItem('kynexone-locale-choice-v2', 'ar');
  }, opts.locale ?? 'en');
  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const path = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (path === '/api/auth/me') return json({
      id: 'pm-1', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Parveen Payroll', roles: ['Payroll Manager'], permissions: opts.permissions,
      companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/payroll/companies') return json([]);
    // The dashboard's own figures are not under test: a failed read is a state it already handles.
    if (path === '/api/payroll/overview' || path === '/api/payroll/readiness' || path === '/api/payroll/reports/summary') return json({ message: 'not under test' }, 503);
    if (path === '/api/payroll/final-settlements') return json({ outstandingTotal: 18250.5, note: '', rows: opts.rows });
    if (path === `/api/payroll/final-settlements/s-1/offcycle-run` && request.method() === 'POST') {
      posts.push(path);
      return opts.createFails ? json({ error: 'regular_run_exists', message: 'A payroll run already exists for this period.' }, 409) : json({ runId: RUN_ID, created: true });
    }
    if (path === '/api/offboarding') return json([{
      id: 'o-1', employeeId: 31, employeeName: 'Layla Nasser', employeeCode: 'EMP-0031', department: 'Finance', designation: 'Accountant', separationType: 'Termination',
      reason: '', noticeDate: '2026-10-01', noticePeriodDays: 30, lastWorkingDay: '2026-11-01', rehireEligible: true, status: 'InProgress', exitInterviewStatus: 'Pending',
      exitInterviewDate: null, exitReasonCategory: '', exitInterviewRating: 0, exitInterviewNotes: '', assetsReturned: false, accessRevoked: false, accessRevokedAtUtc: null,
      knowledgeHandover: false, finalSettlementDone: false, backfillRequisitionId: null, completedAtUtc: null, cancelledAtUtc: null, cancelReason: null,
    }]);
    if (path === '/api/offboarding/summary') return json({ inNotice: 1, completed: 0, exitInterviewsPending: 1, avgExitRating: 0, reasons: [] });
    if (path === '/api/tenant-admin/localization') return json({ currencyCode: 'SAR' });
    if (path === '/api/notifications') return json([]);
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(url);
  return { errors, posts };
}

const PAYROLL = ['payroll.read', 'payroll.write', 'dashboard.read'];

test.describe('Off-cycle run for an approved final settlement', () => {
  test('the Final Settlement tab lists the settlement with its due date and creates the run in one confirmed step', async ({ page }) => {
    const { errors, posts } = await boot(page, '/payroll?tab=final-settlement', { permissions: PAYROLL, rows: [row({})] });
    const panel = page.getByTestId('settlement-run-duty');
    await expect(panel).toContainText('Off-cycle runs to create', { timeout: 60_000 });
    await expect(panel.getByTestId('run-duty-s-1')).toContainText('Layla Nasser');
    await expect(panel.getByTestId('run-duty-s-1')).toContainText('Due by');
    await panel.getByRole('button', { name: 'Create off-cycle run' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Create the off-cycle run?');
    await expect(dialog).toContainText('pays only Layla Nasser’s final settlement');
    expect(posts).toHaveLength(0);
    await dialog.getByRole('button', { name: 'Create off-cycle run' }).click();
    await expect(page).toHaveURL(new RegExp(`/payroll\\?.*tab=runs.*run=${RUN_ID}|/payroll\\?.*run=${RUN_ID}.*tab=runs`));
    expect(posts).toEqual(['/api/payroll/final-settlements/s-1/offcycle-run']);
    expect(errors).toEqual([]);
  });

  test('an overdue settlement says so, and a refusal from the server is shown in words', async ({ page }) => {
    await boot(page, '/payroll?tab=final-settlement', { permissions: PAYROLL, rows: [row({ isOverdue: true, overdueDays: 3 })], createFails: true });
    const panel = page.getByTestId('settlement-run-duty');
    await expect(panel.getByTestId('run-duty-s-1')).toContainText('3d overdue', { timeout: 60_000 });
    await panel.getByRole('button', { name: 'Create off-cycle run' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Create off-cycle run' }).click();
    await expect(panel.getByRole('alert')).toContainText('A payroll run already exists');
  });

  test('a settlement whose run exists links to it instead of offering to create another', async ({ page }) => {
    await boot(page, '/payroll?tab=final-settlement', { permissions: PAYROLL, rows: [row({ runDutyOpen: false, linkedRunId: RUN_ID, linkedRunStatus: 'Draft' })] });
    const panel = page.getByTestId('settlement-run-duty');
    await expect(panel.getByTestId('run-linked-s-1').getByRole('link', { name: /Open the off-cycle run/ })).toHaveAttribute('href', `/payroll?tab=runs&run=${RUN_ID}`, { timeout: 60_000 });
    await expect(panel.getByRole('button', { name: 'Create off-cycle run' })).toHaveCount(0);
  });

  test('a reader without payroll.write sees the job but is not offered the button', async ({ page }) => {
    await boot(page, '/payroll?tab=final-settlement', { permissions: ['payroll.read', 'dashboard.read'], rows: [row({})] });
    const panel = page.getByTestId('settlement-run-duty');
    await expect(panel).toContainText('A Payroll Manager creates this run.', { timeout: 60_000 });
    await expect(panel.getByRole('button', { name: 'Create off-cycle run' })).toHaveCount(0);
  });

  test('in Arabic the panel and its confirmation are Arabic', async ({ page }) => {
    await boot(page, '/payroll?tab=final-settlement', { permissions: PAYROLL, rows: [row({})], locale: 'ar' });
    const panel = page.getByTestId('settlement-run-duty');
    await expect(panel).toContainText('دورات رواتب خارج الدورة بانتظار الإنشاء', { timeout: 60_000 });
    await panel.getByRole('button', { name: 'إنشاء دورة خارج الدورة' }).click();
    await expect(page.getByRole('dialog')).toContainText('هل تنشئ الدورة خارج الدورة؟');
  });

  test('the offboarding card says who is to create the run, then links the run once it exists', async ({ page }) => {
    await boot(page, '/offboarding', { permissions: ['employees.read', 'employees.write', 'payroll.read', 'dashboard.read'], rows: [row({})] });
    await expect(page.getByTestId('offboarding-settlement-run-waiting')).toContainText('Payroll has to create the off-cycle run, due by', { timeout: 60_000 });

    await boot(page, '/offboarding', { permissions: ['employees.read', 'employees.write', 'payroll.read', 'dashboard.read'], rows: [row({ runDutyOpen: false, linkedRunId: RUN_ID })] });
    await expect(page.getByTestId('offboarding-settlement-run')).toHaveAttribute('href', `/payroll?tab=runs&run=${RUN_ID}`, { timeout: 60_000 });
  });

  test('the Payroll dashboard shows how many settlements need a run and the first due date, linking to the tab', async ({ page }) => {
    await boot(page, '/payroll', { permissions: PAYROLL, rows: [row({}), row({ id: 's-2', employeeName: 'Omar Haddad', settlementDueDate: '2026-11-09' }), row({ id: 's-3', runDutyOpen: false, linkedRunId: RUN_ID })] });
    const card = page.getByTestId('settlement-run-duty-card');
    await expect(card).toContainText('2 settlements need an off-cycle run', { timeout: 60_000 });
    await expect(card).toContainText('Next due');
    await card.getByRole('button', { name: 'Open Final Settlement' }).click();
    await expect(page.getByTestId('settlement-run-duty')).toBeVisible();
  });

  test('the dashboard card is silent when nothing waits, and for a reader without payroll.write', async ({ page }) => {
    await boot(page, '/payroll', { permissions: PAYROLL, rows: [row({ runDutyOpen: false, linkedRunId: RUN_ID })] });
    await expect(page.getByRole('tab', { name: /dashboard/i }).or(page.getByText('Company scope', { exact: false })).first()).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('settlement-run-duty-card')).toHaveCount(0);
    await boot(page, '/payroll', { permissions: ['payroll.read', 'dashboard.read'], rows: [row({})] });
    await page.waitForTimeout(1500);
    await expect(page.getByTestId('settlement-run-duty-card')).toHaveCount(0);
  });

  test('the dashboard card in Arabic', async ({ page }) => {
    await boot(page, '/payroll', { permissions: PAYROLL, rows: [row({})], locale: 'ar' });
    await expect(page.getByTestId('settlement-run-duty-card')).toContainText('تسوية واحدة تحتاج دورة خارج الدورة', { timeout: 60_000 });
  });
});
