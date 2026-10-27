import { expect, test } from '@playwright/test';

/**
 * Fixture lane (e2e/playwright.fixture.config.ts): the Employee Salary tab warns, before anyone proposes a salary change, that nobody
 * besides the signed-in user could approve it. The sentence is the server's own, in the reader's language (H2: no_eligible_approver).
 * Written without a run — the sandbox cannot launch a browser; CI's Browser Pilot runs it.
 */
const USER = {
  id: 'u-hr', tenantId: 't1', tenantSlug: 'solo', email: 'hr@solo.test', fullName: 'Hana HR',
  roles: ['Admin'], accountType: 'Tenant', isGroupScope: true, companies: [],
  permissions: ['dashboard.read', 'payroll.read', 'payroll.write', 'payroll.approve', 'employees.read'],
};
const NO_APPROVER = {
  hasEligibleApprover: false,
  message: 'Add a second person with Finance approval rights (payroll.approve) before proposing salary changes. Nobody else can approve it, so it would wait forever.',
  messageAr: 'أضف شخصاً ثانياً يملك صلاحية الموافقة المالية (payroll.approve) قبل اقتراح تغييرات الرواتب. لا يوجد من يستطيع الموافقة عليه غيرك، وسيبقى معلقاً دون قرار.',
};

async function open(page: import('@playwright/test').Page, status: unknown) {
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
  });
  await page.route('**/api/**', (route) => {
    const p = new URL(route.request().url()).pathname;
    const json = (b: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(USER);
    if (p === '/api/payroll/salary-changes/approver-status') return json(status);
    // The payroll dashboard reads the overview as an object (companies, totals); the catch-all [] would crash it before any tab opens.
    if (p === '/api/payroll/overview') return json({ year: 2026, month: 10, totalCompanies: 0, totalActiveEmployees: 0, totalGrossPayroll: 0, totalNetPayroll: 0, totalValidationErrors: 0, totalPendingApprovals: 0, companies: [] });
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p.startsWith('/api/features') || p === '/api/notifications') return json([]);
    return json([]);
  });
  await page.goto('/payroll');
  await page.getByRole('tab', { name: 'Employee Salary' }).click();
}

test('with nobody else able to approve, the salary tab says so in advance', async ({ page }) => {
  await open(page, NO_APPROVER);
  const notice = page.getByTestId('second-approver-notice');
  await expect(notice).toBeVisible({ timeout: 30_000 });
  await expect(notice).toContainText('Add a second person with Finance approval rights (payroll.approve)');
});

test('with a second approver in place there is no notice', async ({ page }) => {
  await open(page, { hasEligibleApprover: true });
  await expect(page.getByRole('button', { name: 'Assign Salary' })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('second-approver-notice')).toHaveCount(0);
});
