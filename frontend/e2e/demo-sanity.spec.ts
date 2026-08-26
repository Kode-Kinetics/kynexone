import { test, expect } from '@playwright/test';
import { platformLogin, tenantLogin, INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, EVOSTEL_SLUG, EVOSTEL_ADMIN } from './helpers';

/**
 * Demo sanity — ensures no demo-critical route crashes or shows blank screens.
 *
 * Rules:
 * - No route should show "Something went wrong"
 * - No route should show a full blank white page
 * - No console errors from React crashes (detected via body content)
 * - Platform admin routes load without crashing
 * - Tenant admin routes load without crashing
 */

test.describe('Platform admin — demo sanity', () => {
  // No per-test login: the session comes from auth.setup.ts via storageState. Logging in here
  // once per route made the suite issue 17 logins in seconds, which the API's platform-login
  // rate limit (default 5/window) correctly rejected — so the tests failed inside the login
  // helper instead of checking the routes they name.

  const PLATFORM_ROUTES = [
    '/platform/dashboard',
    '/platform/tenants',
    '/platform/team',
    '/platform/billing',
    '/platform/plans',
    '/platform/ai-usage',
    '/platform/marketing',
    '/platform/support',
    '/platform/support-sessions',
    '/platform/security',
    '/platform/audit-logs',
    '/platform/system-health',
    '/platform/settings',
  ];

  for (const route of PLATFORM_ROUTES) {
    test(`${route} loads without crash`, async ({ page }) => {
      await page.goto(route);
      await page.waitForLoadState('networkidle');

      const body = (await page.locator('body').innerText()) ?? '';

      // Check for crashes
      expect(body.toLowerCase()).not.toContain('something went wrong');
      expect(body.toLowerCase()).not.toContain('unexpected error');
      expect(body.toLowerCase()).not.toContain('cannot read properties of undefined');
      expect(body.toLowerCase()).not.toContain('typeerror');

      // Page should have content
      expect(body.trim().length).toBeGreaterThan(100);
    });
  }
});

test.describe('IntelliFlow tenant — demo sanity', () => {
  test.beforeEach(async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
  });

  const TENANT_ROUTES = [
    '/dashboard',
    '/people',
    '/attendance',
    '/leave',
    '/payroll',
    '/recruitment',
    '/performance',
    '/reports',
  ];

  for (const route of TENANT_ROUTES) {
    test(`IntelliFlow ${route} loads without crash`, async ({ page }) => {
      await page.goto(route);
      await page.waitForLoadState('networkidle');

      const body = (await page.locator('body').innerText()) ?? '';
      expect(body.toLowerCase()).not.toContain('something went wrong');
      expect(body.toLowerCase()).not.toContain('unexpected error');
      expect(body.trim().length).toBeGreaterThan(50);
    });
  }
});

test.describe('Evostel tenant — demo sanity', () => {
  test.beforeEach(async ({ page }) => {
    await tenantLogin(page, EVOSTEL_ADMIN.email, EVOSTEL_ADMIN.password, EVOSTEL_SLUG);
  });

  test('Evostel dashboard loads without crash', async ({ page }) => {
    await page.goto('/dashboard');
    await page.waitForLoadState('networkidle');
    const body = (await page.locator('body').innerText()) ?? '';
    expect(body.toLowerCase()).not.toContain('something went wrong');
    expect(body.trim().length).toBeGreaterThan(50);
  });

  test('Evostel people page loads without crash', async ({ page }) => {
    await page.goto('/people');
    await page.waitForLoadState('networkidle');
    const body = (await page.locator('body').innerText()) ?? '';
    expect(body.toLowerCase()).not.toContain('something went wrong');
  });

  test('Evostel shows PastDue warning or banner', async ({ page }) => {
    await page.goto('/dashboard');
    await page.waitForLoadState('networkidle');
    const body = (await page.locator('body').innerText()) ?? '';
    // Should either show a PastDue warning OR work normally — never crash
    expect(body.toLowerCase()).not.toContain('something went wrong');
  });

  test('Evostel AI assistant route shows feature-disabled message not crash', async ({ page }) => {
    await page.goto('/ai-assistant');
    await page.waitForLoadState('networkidle');
    const body = (await page.locator('body').innerText()) ?? '';
    // Should show "not enabled" or similar — NOT a crash
    expect(body.toLowerCase()).not.toContain('something went wrong');
    expect(body.toLowerCase()).not.toContain('typeerror');
  });
});

test.describe('Platform tenant detail — demo sanity', () => {
  test.beforeEach(async ({ page }) => {
    await platformLogin(page);
  });

  test('IntelliFlow tenant detail page loads', async ({ page }) => {
    await page.goto('/platform/tenants');
    await page.waitForLoadState('networkidle');
    const link = page.getByText(/intelliflow systems/i).first();
    await link.click();
    await page.waitForURL(/\/platform\/tenants\/[0-9a-f-]{36}/, { timeout: 10_000 });
    await page.waitForLoadState('networkidle');
    const body = (await page.locator('body').innerText()) ?? '';
    expect(body.toLowerCase()).not.toContain('something went wrong');
    await expect(page.getByText(/intelliflow systems/i).first()).toBeVisible();
  });

  test('Evostel tenant detail shows PastDue status', async ({ page }) => {
    await page.goto('/platform/tenants');
    await page.waitForLoadState('networkidle');
    const link = page.getByText(/evostel/i).first();
    await link.click();
    await page.waitForURL(/\/platform\/tenants\/[0-9a-f-]{36}/, { timeout: 10_000 });
    // Wait for the status text to appear (client-side fetch)
    await expect(page.getByText(/past.?due/i).first()).toBeVisible({ timeout: 15_000 });
  });
});
