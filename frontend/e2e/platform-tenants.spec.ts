import { test, expect } from '@playwright/test';

test.describe('Platform tenants', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/platform/tenants');
    // Wait for the tenant list to settle
    await page.waitForLoadState('networkidle');
  });

  test('tenants page loads without error', async ({ page }) => {
    await expect(page).toHaveURL(/\/platform\/tenants/);
    const bodyText = (await page.locator('body').innerText()) ?? '';
    expect(bodyText.toLowerCase()).not.toContain('something went wrong');
  });

  test('IntelliFlow Systems is shown in the tenant table', async ({ page }) => {
    await expect(page.getByText(/intelliflow systems/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test('Evostel LLC is shown in the tenant table', async ({ page }) => {
    await expect(page.getByText(/evostel/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test('Evostel LLC row indicates PastDue status', async ({ page }) => {
    // Find the Evostel row and check its status text
    const evostelRow = page.locator('tr, [role="row"]').filter({ hasText: /evostel/i });
    await expect(evostelRow.first()).toBeVisible({ timeout: 15_000 });
    await expect(evostelRow.getByText(/past.?due/i).first()).toBeVisible({ timeout: 5_000 });
  });

  test('clicking a tenant row navigates to the tenant detail page', async ({ page }) => {
    // Click the first tenant row or a "View" / name link
    const tenantLink = page.getByText(/intelliflow systems/i).first();
    await tenantLink.click();
    // Should navigate to /platform/tenants/<guid>
    await expect(page).toHaveURL(/\/platform\/tenants\/[0-9a-f-]{36}/, { timeout: 10_000 });
  });

  test('tenant detail page shows name and plan badge', async ({ page }) => {
    const tenantLink = page.getByText(/intelliflow systems/i).first();
    await tenantLink.click();
    await page.waitForURL(/\/platform\/tenants\/[0-9a-f-]{36}/, { timeout: 10_000 });
    await page.waitForLoadState('networkidle');
    // Name should appear
    await expect(page.getByText(/intelliflow systems/i).first()).toBeVisible({ timeout: 10_000 });
    // Plan badge (Enterprise) should be visible
    await expect(page.getByText(/enterprise/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('feature flag section is visible on tenant detail', async ({ page }) => {
    const tenantLink = page.getByText(/intelliflow systems/i).first();
    await tenantLink.click();
    await page.waitForURL(/\/platform\/tenants\/[0-9a-f-]{36}/, { timeout: 10_000 });
    await page.waitForLoadState('networkidle');

    // `getByText(/feature/i).first()` matched the word "feature" ANYWHERE on the
    // page — including the tab label itself — so the section could be entirely
    // absent and the test still passed. Open the Features tab and assert the
    // section's own contents.
    const featuresTab = page.getByRole('button', { name: 'Features', exact: true });
    await expect(featuresTab).toBeVisible({ timeout: 10_000 });
    await featuresTab.click();

    // The feature-flag section renders one aria switch per flag.
    const flagSwitches = page.getByRole('switch');
    await expect(flagSwitches.first()).toBeVisible({ timeout: 10_000 });
    expect(await flagSwitches.count(), 'tenant detail must list the tenant feature flags').toBeGreaterThan(0);

    // IntelliFlow is the all-features-enabled Enterprise tenant: at least one
    // flag must read as enabled, which also proves the tenant's own state is
    // rendered rather than a static catalogue.
    const enabled = page.locator('[role="switch"][aria-checked="true"]');
    expect(await enabled.count(), 'IntelliFlow (Enterprise) must show enabled feature flags').toBeGreaterThan(0);
  });

  test('Provision New Tenant link or button is visible on tenants list', async ({ page }) => {
    await expect(
      page.getByRole('link', { name: /provision|new tenant/i }).or(
        page.getByRole('button', { name: /provision|new tenant/i })
      ).first()
    ).toBeVisible({ timeout: 10_000 });
  });
});
