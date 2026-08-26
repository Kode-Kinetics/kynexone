import { test, expect, type Page } from '@playwright/test';
import { tenantLogin, INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, RASALMANAR_SLUG, RASALMANAR_ADMIN } from './helpers';

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

async function gotoRoute(page: Page, route: string): Promise<void> {
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      await page.goto(route, { waitUntil: 'domcontentloaded', timeout: 15_000 });
      return;
    } catch (error) {
      const transientReset = error instanceof Error
        && /ERR_CONNECTION_RESET|ERR_CONNECTION_CLOSED/.test(error.message);
      if (!transientReset || attempt === 1) throw error;
    }
  }
}

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
    '/platform/compliance',
    '/platform/leads',
    '/platform/pricing',
    '/platform/roles',
    '/platform/tenants/new',
  ];

  for (const route of PLATFORM_ROUTES) {
    test(`${route} loads without crash`, async ({ page }) => {
      await gotoRoute(page, route);

      await expect.poll(
        async () => ((await page.locator('body').innerText()) ?? '').trim().length,
        { timeout: 10_000, message: `${route} did not render meaningful content` },
      ).toBeGreaterThan(100);

      const body = (await page.locator('body').innerText()) ?? '';

      // Check for crashes
      expect(body.toLowerCase()).not.toContain('something went wrong');
      expect(body.toLowerCase()).not.toContain('unexpected error');
      expect(body.toLowerCase()).not.toContain('cannot read properties of undefined');
      expect(body.toLowerCase()).not.toContain('typeerror');
    });
  }
});

test.describe('IntelliFlow tenant — demo sanity', () => {
  const TENANT_ROUTES = [
    '/dashboard',
    '/people',
    '/attendance',
    '/leave',
    '/payroll',
    '/payroll/templates',
    '/recruitment',
    '/recruitment/onboarding',
    '/performance',
    '/performance/calibration',
    '/performance/pip',
    '/reports',
    '/approvals',
    '/companies',
    '/compliance',
    '/compliance-profiles',
    '/ess',
    '/hr-requests',
    '/loans',
    '/offboarding',
    '/org-chart',
    '/overtime',
    '/saudi-compliance',
    '/setup',
    '/shifts',
    '/tax-policies',
    '/tenant-admin',
    '/user-management',
    '/ai-assistant',
  ];

  test('every IntelliFlow module route loads without crash', async ({ page }) => {
    test.setTimeout(180_000);
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    for (const route of TENANT_ROUTES) {
      await gotoRoute(page, route);

      await expect.poll(
        async () => ((await page.locator('body').innerText()) ?? '').trim().length,
        { timeout: 10_000, message: `${route} did not render meaningful content` },
      ).toBeGreaterThan(50);

      const body = (await page.locator('body').innerText()) ?? '';
      expect(body.toLowerCase(), `${route} rendered a fatal error`).not.toContain('something went wrong');
      expect(body.toLowerCase(), `${route} rendered an unexpected error`).not.toContain('unexpected error');
      expect(body.trim().length, `${route} remained blank`).toBeGreaterThan(50);
    }
  });
});

test.describe('Ras Al-Manar tenant — demo sanity', () => {
  test('dashboard and people load without crash', async ({ page }) => {
    await tenantLogin(page, RASALMANAR_ADMIN.email, RASALMANAR_ADMIN.password, RASALMANAR_SLUG);
    for (const route of ['/dashboard', '/people']) {
      await gotoRoute(page, route);
      await expect.poll(
        async () => ((await page.locator('body').innerText()) ?? '').trim().length,
        { timeout: 10_000, message: `${route} did not render meaningful content` },
      ).toBeGreaterThan(50);
      const body = (await page.locator('body').innerText()) ?? '';
      expect(body.toLowerCase(), `${route} rendered a fatal error`).not.toContain('something went wrong');
      expect(body.trim().length, `${route} remained blank`).toBeGreaterThan(50);
    }
  });
});

test.describe('Platform tenant detail — demo sanity', () => {
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
