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

  test('at-risk section lists the past-due tenant, and the page loads without JS errors', async ({ page }) => {
    // Two defects fixed here.
    //
    // 1. The listener was attached AFTER `beforeEach` had already navigated, so
    //    a load-time exception could never reach it — `errors` could only be
    //    empty. Attach first, then navigate (see platform-billing.spec.ts).
    // 2. The old title claimed something the fixture contradicts: the E2E setup
    //    provisions a PastDue tenant, so the at-risk panel MUST be populated.
    //    Assert that, rather than asserting nothing.
    const errors: string[] = [];
    page.on('pageerror', e => errors.push(e.message));

    await page.goto('/platform/dashboard');
    await page.waitForLoadState('networkidle');

    // Scope to the At-Risk CARD (the nearest rounded-xl ancestor of its
    // heading), not to the whole page and not to the heading's own row.
    const atRiskHeading = page.getByText('At Risk', { exact: true });
    await expect(atRiskHeading).toBeVisible({ timeout: 15_000 });
    const atRisk = atRiskHeading.locator('xpath=ancestor::div[contains(@class,"rounded-xl")][1]');

    // A PastDue tenant exists, so the healthy empty-state must NOT be shown…
    await expect(atRisk.getByText(/all tenants healthy/i)).toHaveCount(0);
    // …and the panel must list the past-due tenant as a row of its own.
    await expect(
      atRisk.getByRole('link').filter({ hasText: /past due/i }).first(),
      'the at-risk panel must list the PastDue fixture tenant',
    ).toBeVisible({ timeout: 15_000 });

    expect(errors, `page errors during load: ${errors.join(' | ')}`).toHaveLength(0);
  });

  test('command bar search input is rendered', async ({ page }) => {
    await expect(page.getByPlaceholder(/search tenants/i)).toBeVisible();
  });
});
