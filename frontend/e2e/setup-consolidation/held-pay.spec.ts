import { expect, test, type Page } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type Persona } from './fixture';

/**
 * Payroll → Approvals: pay held for suspended employees, with Finance's two decisions; and the read-only multi-month
 * leave deductions report. Every API call is intercepted: nothing real is read or written.
 */
const FINANCE: Persona = { roles: ['Finance Controller'], permissions: [...ALL_PERMISSIONS, 'payroll.approve'] };
const RUN = {
  id: '3f2a6c1e-9b7d-4c1a-8e2f-5d6b7a8c9e01', companyId: 'fixture-company', year: 2026, month: 3, status: 'Processed', runType: 'Regular',
  totalGrossSalary: 7500, totalDeductions: 731.25, totalNetSalary: 6768.75, totalEmployerStatutoryCost: 731.25,
  employeeCount: 1, createdAtUtc: '2026-03-28T08:00:00Z', processedAtUtc: '2026-03-28T09:00:00Z', lockedAtUtc: null,
};
const HELD = {
  runId: RUN.id, period: '2026-03', heldTotal: 7500, awaitingDecision: 1,
  items: [
    { holdId: 'hold-1', employeeId: 7, employeeCode: 'EMP-007', employeeName: 'Sara Alqahtani', employeeStatus: 'Suspended', amount: 7500,
      reason: 'suspended', status: 'Held', heldRunId: RUN.id, heldPeriod: '2026-03', heldByThisRun: true, paidInRunId: null, paidInPeriod: null },
    { holdId: 'hold-0', employeeId: 9, employeeCode: 'EMP-009', employeeName: 'Omar Alharbi', employeeStatus: 'Active', amount: 6200,
      reason: 'suspended', status: 'Released', heldRunId: 'run-feb', heldPeriod: '2026-02', heldByThisRun: false, paidInRunId: RUN.id, paidInPeriod: '2026-03' },
  ],
};
const POPULATION = {
  runId: RUN.id, runType: 'Regular', includesRecurringPay: true, populationMode: 'AllEligible',
  eligibleCount: 1, includedCount: 1, excludedCount: 0, notEligibleCount: 0, included: [], excluded: [], notEligible: [],
  leftOut: [], leftOutCounts: {}, payWarnings: [], payWarningCount: 0,
};
const REPORT = {
  readOnly: true,
  wouldSplit: [{
    impactId: 'i1', leaveRequestId: 'l1', employeeId: 7, employeeCode: 'EMP-007', employeeName: 'Sara Alqahtani',
    startDate: '2026-01-25', endDate: '2026-02-05', requestStatus: 'Approved', payPeriod: '2026-01', status: 'Pending',
    impactType: 'Leave Deduction (Unpaid)', days: 10, amount: 2000, note: '',
    parts: [{ payPeriod: '2026-01', from: '2026-01-25', to: '2026-01-31', days: 5, amount: 1000 }, { payPeriod: '2026-02', from: '2026-02-01', to: '2026-02-05', days: 5, amount: 1000 }],
  }],
  doNotReconcile: [], alreadyDeducted: [], sickScaleRows: [],
};
const mocks = {
  '/api/payroll/runs': { items: [RUN], total: 1, page: 1, pageSize: 100 },
  [`/api/payroll/runs/${RUN.id}/population`]: POPULATION,
  [`/api/payroll/runs/${RUN.id}/validation-overrides`]: { runId: RUN.id, overrides: [], overridableCodes: [], nonOverridableCodes: [], paidOutsideBankFile: [] },
  [`/api/payroll/runs/${RUN.id}/held-pay`]: HELD,
  '/api/payroll/leave-slice-split-report': REPORT,
};
const openRun = (page: Page, locale: 'en' | 'ar' = 'en') =>
  boot(page, { persona: FINANCE, url: `/payroll?tab=approvals&run=${RUN.id}`, mocks, locale });

for (const locale of ['en', 'ar'] as const) {
  test(`the approvals view lists held pay with Finance's decisions (${locale})`, async ({ page }) => {
    const { errors } = await openRun(page, locale);
    const panel = page.getByTestId('payroll-held-pay');
    await expect(panel).toBeVisible({ timeout: 30_000 });
    await expect(panel.getByTestId('held-pay-row')).toHaveCount(2);
    await expect(panel.getByTestId('held-pay-release')).toHaveCount(1);
    await panel.getByTestId('held-pay-forfeit').click();
    await expect(panel.getByTestId('held-pay-forfeit-confirm')).toBeDisabled();
    await panel.scrollIntoViewIfNeeded();
    await page.screenshot({ path: `/tmp/claude/paynext/held-pay-${locale}.png`, fullPage: false });
    if (locale === 'ar') await expect(panel).toContainText('محتجز');
    expect(errors).toEqual([]);
  });

  test(`the multi-month leave deductions report is read-only and lists the split (${locale})`, async ({ page }) => {
    const { errors, writes } = await boot(page, { persona: FINANCE, url: '/payroll/leave-split-report', mocks, locale });
    const report = page.getByTestId('leave-split-report');
    await expect(report).toBeVisible({ timeout: 30_000 });
    await expect(report).toContainText('2026-02');
    await page.screenshot({ path: `/tmp/claude/paynext/leave-split-report-${locale}.png`, fullPage: true });
    if (locale === 'ar') await expect(report).toContainText('سيُقسَّم');
    expect(writes).toEqual([]);
    expect(errors).toEqual([]);
  });
}

test('paying a held amount sends one decision and says where it is paid', async ({ page }) => {
  const sent: unknown[] = [];
  await openRun(page);
  await page.route('**/api/payroll/held-pay/hold-1/decision', async route => {
    sent.push(route.request().postDataJSON());
    await route.fulfill({ json: { status: 'Released', message: 'ok' } });
  });
  await page.getByTestId('held-pay-release').click();
  await expect(page.getByRole('status').filter({ hasText: 'added to the next open run' })).toBeVisible();
  expect(sent).toEqual([{ action: 'pay', reason: '' }]);
});
