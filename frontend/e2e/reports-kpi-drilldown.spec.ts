import { expect, test, type Page } from '@playwright/test';

/**
 * Reports → Analytics Dashboard, fixture lane (e2e/playwright.fixture.config.ts): every KPI tile says what it counts,
 * and a tile whose records sit in an existing list or report links there. A tile with no such list shows its
 * definition only — a link to an unfiltered list would not be "the records behind" the number.
 */

const COMPANY = { id: 'company-1', name: 'Masar Facility Services', code: 'MFS', countryCode: 'SA', isActive: true };
const HR = {
  id: 'u-hr', tenantId: 't1', tenantSlug: 'masar', email: 'hr@masar.test', fullName: 'Huda Reports', employeeId: 900,
  roles: ['HR Manager'], accountType: 'Tenant', isGroupScope: true, companies: [COMPANY],
  permissions: ['reports.read', 'employees.read', 'leave.read', 'loans.read', 'payroll.read'],
};
const KPIS = {
  headcount: { totalActive: 42, newThisMonth: 3, exitsThisMonth: 1 },
  leave: { pendingLeave: 4, onLeaveToday: 2 },
  attendance: { presentToday: 37, lateToday: 5 },
  overtime: { pendingOT: 6 },
  payroll: { lastRunYear: 2026, lastRunMonth: 9, lastRunStatus: 'Approved', totalNetSalary: 512000 },
  compliance: { visasExpiring: null, passportsExpiring: null },
  recruitment: { openPositions: 7, pendingApplications: 11 },
  financial: { activeLoans: 9, outstandingLoanBalance: 81500 },
  withheld: [{ section: 'compliance', fields: ['visasExpiring', 'passportsExpiring'], requiredPermissions: ['compliance.read'], acceptedRoles: [] }],
  generatedAt: '2026-10-20T07:00:00Z',
};
const CATALOG = [
  { key: 'leave.pending', name: 'Pending Leave Approvals', category: 'Leave', description: 'Leave requests awaiting approval' },
  { key: 'finance.loan-balance', name: 'Loan Balance', category: 'Finance', description: 'Outstanding loan balances by employee' },
];

async function boot(page: Page, lang: 'en' | 'ar', path = '/reports') {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript((l) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
    localStorage.setItem('kynexone-locale-choice-v2', l);
  }, lang);
  await page.route('**/api/**', (route) => {
    const p = new URL(route.request().url()).pathname;
    const json = (b: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(HR);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false, currencyCode: 'SAR' });
    if (p === '/api/analytics/kpis') return json(KPIS);
    if (p.startsWith('/api/analytics/trends/')) return json([]);
    if (p === '/api/reports/catalog') return json(CATALOG);
    return json({ items: [], total: 0 });
  });
  await page.goto(path);
  return errors;
}

const tile = (page: Page, label: string) => page.locator('.surface').filter({ has: page.getByText(label, { exact: true }) });

test('every KPI tile says what it counts, and links only to a list that holds exactly its records', async ({ page }) => {
  const errors = await boot(page, 'en');

  const active = tile(page, 'Active Employees');
  await expect(active).toContainText('42');
  await expect(active).toContainText('Employees whose status is Active.');
  await expect(active.getByRole('link', { name: 'Show these employees' })).toHaveAttribute('href', '/people?status=Active');

  await expect(tile(page, 'Pending Leave').getByRole('link', { name: 'Open the Pending Leave Approvals report' }))
    .toHaveAttribute('href', '/reports?report=leave.pending');
  const loans = tile(page, 'Active Loans');
  await expect(loans).toContainText('Active employee loans. Outstanding is what is still to be repaid on them.');
  await expect(loans.getByRole('link', { name: 'Open the Loan Balance report' })).toHaveAttribute('href', '/reports?report=finance.loan-balance');
  await expect(tile(page, 'Total Net Salary').getByRole('link', { name: 'Open the payroll runs' })).toHaveAttribute('href', '/payroll');

  // No list filters to exactly these records yet: the definition is shown, and no link pretends otherwise.
  for (const [label, definition] of [
    ['New This Month', 'Employees whose joining date falls in this calendar month.'],
    ['Exits This Month', 'Resigned or terminated employees whose contract ends in this calendar month.'],
    ['Open Positions', 'Job openings whose status is Open.'],
    ['Present Today', "Today's attendance records marked present, late or half day."],
    ['Late Today', "Today's attendance records with late minutes."],
    ['On Leave', 'Approved leave requests that cover today.'],
    ['Pending OT', 'Overtime requests waiting for a decision.'],
  ] as const) {
    await expect(tile(page, label)).toContainText(definition);
    await expect(tile(page, label).getByRole('link')).toHaveCount(0);
  }
  // A withheld figure keeps its definition but offers no way into records the viewer may not read.
  const visas = tile(page, 'Visas Expiring (30d)');
  await expect(visas).toContainText('Restricted');
  await expect(visas).toContainText('Active visas that expire in the next 30 days.');
  await expect(visas.getByRole('link')).toHaveCount(0);

  // The report link opens the library on that report.
  await tile(page, 'Pending Leave').getByRole('link', { name: 'Open the Pending Leave Approvals report' }).click();
  await expect(page).toHaveURL(/\/reports\?report=leave\.pending$/);
  await expect(page.getByRole('tab', { name: 'Report Library' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('button', { name: /Run Report/ })).toBeVisible();
  expect(errors).toEqual([]);
});

test('the KPI definitions and links read in Arabic', async ({ page }) => {
  const errors = await boot(page, 'ar');
  await expect(page.getByText('الموظفون الذين حالتهم نشط.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'عرض هؤلاء الموظفين' })).toHaveAttribute('href', '/people?status=Active');
  await expect(page.getByRole('link', { name: 'فتح تقرير أرصدة القروض' })).toBeVisible();
  expect(errors).toEqual([]);
});
