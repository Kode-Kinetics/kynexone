import { expect, test, type Page } from '@playwright/test';

/**
 * Every loan and advance a leaver still owes is visible at exit, whatever its repayment method:
 *   Offboarding card: "Loans and advances still owed" lists each open loan and advance, flags a held loan, and names the
 *   employee and the amount still owed.
 *   Payroll → Final Settlement: the calculated draft lists the same debts with what the final pay recovers from each, and
 *   names what it cannot recover.
 *   Reports: the loan KPI shows one figure per currency instead of adding currencies together.
 * Every API call is intercepted; nothing is written. Run: npx playwright test -c e2e/playwright.loans.config.ts
 */

const DEBTS = [
  { kind: 'Loan', id: 'l-1', reference: 'LN-2026-BANK', repaymentMethod: 'BankTransfer', held: false, outstandingBalance: 1500, currency: 'SAR', plannedRecovery: 1000 },
  { kind: 'Loan', id: 'l-2', reference: 'LN-2026-HELD', repaymentMethod: 'PayrollDeduction', held: true, outstandingBalance: 800, currency: 'SAR', plannedRecovery: 0 },
  { kind: 'Advance', id: 'a-1', reference: 'ADV-2026-1', repaymentMethod: null, held: false, outstandingBalance: 600, currency: 'SAR', plannedRecovery: 0 },
];

const OFFBOARDING = {
  id: 'o-1', employeeId: 31, employeeName: 'Layla Nasser', employeeCode: 'EMP-0031', department: 'Finance', designation: 'Accountant', separationType: 'Termination',
  reason: '', noticeDate: '2026-10-01', noticePeriodDays: 30, lastWorkingDay: '2026-11-01', rehireEligible: true, status: 'InProgress', exitInterviewStatus: 'Pending',
  exitInterviewDate: null, exitReasonCategory: '', exitInterviewRating: 0, exitInterviewNotes: '', assetsReturned: false, accessRevoked: false, accessRevokedAtUtc: null,
  knowledgeHandover: false, finalSettlementDone: false, backfillRequisitionId: null, completedAtUtc: null, cancelledAtUtc: null, cancelReason: null,
};

async function boot(page: Page, url: string, locale: 'en' | 'ar', opts: { debtsStatus?: number } = {}) {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript((lang) => {
    localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone.theme', 'light');
    localStorage.setItem('kynexone-locale-choice-v2', lang);
  }, locale);
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (path === '/api/auth/me') return json({
      id: 'hr-1', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Hana HR', roles: ['HR Manager'],
      permissions: ['employees.read', 'employees.write', 'employees.approve', 'payroll.read', 'payroll.approve', 'loans.read', 'reports.read', 'dashboard.read'],
      companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (['/api/features/modules', '/api/notifications', '/api/payroll/companies'].includes(path)) return json([]);
    if (path === '/api/tenant-admin/localization') return json({ currencyCode: 'SAR' });
    if (path === '/api/payroll/overview' || path === '/api/payroll/readiness' || path === '/api/payroll/reports/summary') return json({ message: 'not under test' }, 503);
    if (path === '/api/offboarding') return json([OFFBOARDING]);
    if (path === '/api/offboarding/summary') return json({ inNotice: 1, completed: 0, exitInterviewsPending: 1, avgExitRating: 0, reasons: [] });
    if (path === '/api/offboarding/o-1/open-debts') return opts.debtsStatus
      ? json({ message: 'forbidden' }, opts.debtsStatus)
      : json({ items: DEBTS, currencyCode: 'SAR', totalOutstanding: 2900, heldOutstanding: 800 });
    if (path === '/api/payroll/final-settlements') return json({ outstandingTotal: 0, note: '', rows: [] });
    if (path === '/api/payroll/final-settlement' && request.method() === 'POST') return json({
      settlementId: 's-1', status: 'Draft', employeeId: 31, employeeName: 'Layla Nasser', lastWorkingDay: '2026-11-01', currency: 'SAR',
      basicSalary: 8000, grossSalary: 10000, proRataSalary: 0, daysWorkedInMonth: 1, daysInMonth: 30, eosbAmount: 1000, totalYears: 2,
      terminationReason: 'Termination', leaveBalanceDays: 0, leaveEncashment: 0, noticePeriodDaysShort: 0, noticePeriodDeduction: 0,
      totalPayable: 1000, netPayable: 1000, settlementDueDate: '2026-11-08', wageBaseDelta: 0, wagesPaidByRunId: null,
      warnings: [], breakdown: [{ component: 'End-of-service gratuity', amount: 1000 }],
      openDebts: DEBTS.map(d => d.kind === 'Advance' ? { ...d, plannedRecovery: 0 } : d), heldDebt: 800, unrecoveredDebt: 1100,
    });
    if (path === '/api/analytics/kpis') return json({
      headcount: { totalActive: 10, newThisMonth: 0, exitsThisMonth: 1 }, leave: { pendingLeave: 0, onLeaveToday: 0 }, attendance: { presentToday: 0, lateToday: 0 },
      overtime: { pendingOT: 0 }, payroll: { lastRunYear: null, lastRunMonth: null, lastRunStatus: null, totalNetSalary: null },
      compliance: { visasExpiring: 0, passportsExpiring: 0 }, recruitment: { openPositions: 0, pendingApplications: 0 },
      financial: { activeLoans: 3, outstandingLoanBalance: null, loanCurrencyCode: null, outstandingLoanBalanceByCurrency: [{ currency: 'AED', amount: 1500, count: 2 }, { currency: 'SAR', amount: 2000, count: 1 }] },
      withheld: [], generatedAt: '2026-11-17T10:00:00Z',
    });
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(url);
  return { errors };
}

for (const locale of ['en', 'ar'] as const) {
  test(`offboarding card lists every loan and advance still owed (${locale})`, async ({ page }, info) => {
    const { errors } = await boot(page, '/offboarding', locale);
    const panel = page.getByTestId('offboarding-open-debts');
    await expect(panel).toBeVisible({ timeout: 60_000 });
    await expect(panel).toContainText('LN-2026-BANK');
    await expect(panel).toContainText('LN-2026-HELD');
    await expect(panel).toContainText('ADV-2026-1');
    await expect(panel).toContainText(locale === 'ar' ? 'تحويل بنكي' : 'Bank transfer');
    await expect(panel).toContainText(locale === 'ar' ? 'موقوف، ولا يُسترد من المستحقات النهائية' : 'On hold, not recovered from the final pay');
    await expect(panel).toContainText('Layla Nasser');
    await expect(panel).toContainText(locale === 'ar' ? 'القروض والسلف المستحقة' : 'Loans and advances still owed');
    await page.screenshot({ path: info.outputPath(`offboarding-open-debts-${locale}.png`), fullPage: true });
    expect(errors).toEqual([]);
  });

  test(`final settlement draft shows the debts and what it cannot recover (${locale})`, async ({ page }, info) => {
    const { errors } = await boot(page, '/payroll?tab=final-settlement', locale);
    await page.locator('input[type="number"]').first().fill('31', { timeout: 60_000 });
    await page.locator('input[type="date"]').first().fill('2026-11-01');
    await page.getByRole('button', { name: /Calculate & Save Draft|احتساب وحفظ مسودة/ }).click();
    const panel = page.getByTestId('settlement-open-debts');
    await expect(panel).toBeVisible();
    await expect(panel).toContainText('LN-2026-BANK');
    await expect(panel).toContainText(locale === 'ar' ? 'المخطط استرداده من المستحقات النهائية' : 'Planned from final pay');
    await expect(panel).toContainText(locale === 'ar' ? 'لا يمكن استرداد' : 'cannot be recovered from this settlement');
    await page.screenshot({ path: info.outputPath(`settlement-open-debts-${locale}.png`), fullPage: true });
    expect(errors).toEqual([]);
  });
}

test('a reader without loan rights sees no debts section instead of "nothing owed"', async ({ page }) => {
  const { errors } = await boot(page, '/offboarding', 'en', { debtsStatus: 403 });
  await expect(page.getByText('Layla Nasser').first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('offboarding-open-debts')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('the loan KPI shows each currency, never one total across currencies', async ({ page }, info) => {
  const { errors } = await boot(page, '/reports', 'en');
  await expect(page.getByText(/Outstanding by currency: AED .*1,500\.00 \+ SAR .*2,000\.00/)).toBeVisible({ timeout: 60_000 });
  await page.screenshot({ path: info.outputPath('reports-loan-kpi-by-currency.png'), fullPage: true });
  expect(errors).toEqual([]);
});
