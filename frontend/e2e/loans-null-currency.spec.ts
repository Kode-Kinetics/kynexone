import { expect, test } from '@playwright/test';

/**
 * A loan carried in through the opening-balance import before #188 has currency = null in production.
 * The Loans page formatted it with toLocaleString({ style: 'currency', currency: null }), which throws, and
 * one such loan blanked the whole page. Mocked API, no stack: npx playwright test -c e2e/playwright.ess-workspace.config.ts
 */
const loan = {
  id: 'loan-1', employeeId: '17', employeeName: 'Asif Mahmood', loanTypeId: 'lt-1', loanTypeName: 'Personal Loan',
  loanNumber: 'LN-0001', requestedAmount: 4800, approvedAmount: 4800, requestedInstallments: 12, approvedInstallments: 12,
  installmentAmount: 400, repaymentFrequency: 'Monthly', repaymentMethod: 'PayrollDeduction', currency: null,
  disbursementDate: '2025-11-01', repaymentStartDate: '2025-12-01', totalRepaid: 2000, outstandingBalance: 2800,
  status: 'Active', notes: '', isLockedByPayroll: false, createdAtUtc: '2025-11-01T00:00:00Z',
};

test('a loan with no currency renders instead of blanking the Loans page', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'fixture'));
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/me') {
      return route.fulfill({ json: {
        id: 'user-1', employeeId: 17, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Asif Mahmood', roles: ['Employee'],
        permissions: ['ess.read', 'ess.write', 'loans.self'],
        companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
      } });
    }
    if (path === '/api/features/disabled-keys' || path === '/api/features/modules' || path === '/api/notifications') return route.fulfill({ json: [] });
    if (path === '/api/finance/loans') return route.fulfill({ json: { items: [loan], total: 1, page: 1, pageSize: 100 } });
    if (path === '/api/tenant-admin/localization') return route.fulfill({ json: { currencyCode: 'SAR' } });
    return route.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto('/loans?mine=true');
  await expect(page.getByText('LN-0001').first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText(/2,800/).first()).toBeVisible();
  expect(errors.filter((e) => /currency/i.test(e)), errors.join('\n')).toEqual([]);
});
