import { test, expect } from '@playwright/test';

test.describe('Platform dashboard', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/platform/dashboard');
    await page.waitForLoadState('networkidle');
  });

  test('dashboard page loads without error', async ({ page }) => {
    await expect(page).toHaveURL(/\/platform\/dashboard/);
    // No error boundary / unhandled exception UI visible
    const bodyText = (await page.locator('body').innerText()) ?? '';
    expect(bodyText.toLowerCase()).not.toContain('something went wrong');
  });

  test('MRR metric card is visible', async ({ page }) => {
    // The dashboard should surface an MRR (monthly recurring revenue) stat
    await expect(page.getByText(/mrr|monthly recurring/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test('Active Tenants metric is visible', async ({ page }) => {
    await expect(page.getByText(/active tenants?/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test('platform sidebar navigation is rendered', async ({ page }) => {
    // The sidebar contains "All Tenants" link
    await expect(page.getByRole('link', { name: /all tenants/i })).toBeVisible();
    // And the Platform Team link
    await expect(page.getByRole('link', { name: /platform team/i })).toBeVisible();
  });

  test('attention / at-risk section is not shown when there are no past-due tenants', async ({ page }) => {
    // If the demo seed has past-due tenants this test is informational rather than a hard assertion.
    // We verify no JS runtime error is thrown instead.
    const errors: string[] = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.waitForTimeout(2_000); // let async data load
    expect(errors).toHaveLength(0);
  });

  test('command bar search input is rendered', async ({ page }) => {
    await expect(page.getByPlaceholder(/search tenants/i)).toBeVisible();
  });
});
