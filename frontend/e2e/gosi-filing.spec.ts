import { test, expect } from '@playwright/test';
import {
  apiLogin, crashIndicators, mainText, tenantLogin,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_SLUG,
} from './helpers';

// W2-F — GOSI filing & variance view. IntelliFlow's seed has a Locked 2026-08 run with statutory
// PayrollDeductions and matching GL 2101/2106 postings, so that period must tie out to the halala.
// A period with no run must render the explicit "no statutory lines" state, never a grid of zeros.

test.describe('GOSI filing — UI', () => {
  test('a period with statutory data shows tiles, a halala tie-out, components and per-employee rows', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/gosi-filing');
    await expect(page.getByRole('heading', { name: 'GOSI Filing & Variance' })).toBeVisible({ timeout: 15_000 });
    await page.getByLabel('Month').selectOption({ label: 'August' });
    await page.getByLabel('Year').selectOption('2026');

    await expect(page.getByTestId('tile-employee')).toContainText('10,603.13');
    await expect(page.getByTestId('tile-employer')).toContainText('14,403.13');
    await expect(page.getByTestId('tile-expected-delta')).toContainText('Recomputation matches to the halala');
    await expect(page.getByTestId('tile-gl-delta')).toContainText('GL matches deductions to the halala');
    await expect(page.getByTestId('gosi-tieout-verdict')).toHaveText(/Ties out to the halala/);
    await expect(page.getByRole('cell', { name: /GOSI Annuities \(Employee\)/ })).toBeVisible();
    await expect(page.getByTestId('gosi-variance-count')).toHaveText(/All 12 employees reconcile/);
    await expect(page.getByTestId('gosi-employee-table').locator('tbody tr')).toHaveCount(12);
    await expect(page.getByRole('link', { name: 'Saudi Compliance' }).first()).toHaveAttribute('href', '/saudi-compliance');

    // Drill into the single Locked run: slip witnesses + GL accounts join the chain.
    await page.getByRole('tab', { name: /Regular run · Locked/ }).click();
    const tie = page.getByTestId('gosi-tieout');
    await expect(tie).toContainText('Payslips');
    await expect(tie).toContainText('GL 2101');
    await expect(tie).toContainText('GL 2106');
    await expect(page.getByTestId('gosi-variance-count')).toHaveText(/All 12 employees reconcile/);
    expect(crashIndicators(await mainText(page))).toEqual([]);
  });

  test('a period with no payroll shows the explicit no-statutory-lines state, not zeros', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/gosi-filing');
    await expect(page.getByRole('heading', { name: 'GOSI Filing & Variance' })).toBeVisible({ timeout: 15_000 });
    await page.getByLabel('Month').selectOption({ label: 'January' });
    await page.getByLabel('Year').selectOption('2025');
    const empty = page.getByTestId('gosi-no-statutory');
    await expect(empty).toContainText('No statutory lines recorded for January 2025');
    await expect(page.getByTestId('tile-employee')).toHaveCount(0);
    await expect(page.getByTestId('gosi-employee-table')).toHaveCount(0);
  });
});

test.describe('GOSI filing — API authorization', () => {
  test('an employee cannot read GOSI period filings', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/gosi/periods/2026/8/contribution-summary', { headers: { Authorization: `Bearer ${token}` } });
    expect(resp.status()).toBe(403);
  });
});
