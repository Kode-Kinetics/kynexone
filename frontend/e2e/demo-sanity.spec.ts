import { test, expect, type Page } from '@playwright/test';
import {
  tenantLogin, INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, RASALMANAR_SLUG, RASALMANAR_ADMIN,
  mainContentLength, mainText, crashIndicators,
} from './helpers';

/**
 * Demo sanity — ensures no demo-critical route crashes or shows blank screens.
 *
 * ── Why every length check below measures <main>, not <body> ──────────────────
 * This file used `(await page.locator('body').innerText()).length > 50` (and > 100) as its "the
 * page rendered" proxy. That is a FALSE GREEN. The persistent application shell — sidebar, nav,
 * header — paints before any data arrives and is ~950 characters on its own, so the threshold was
 * already met by:
 *   • a route whose every /api/** call returned 500,
 *   • a route that rendered nothing but a spinner,
 *   • a redirect that still painted chrome.
 * Proven in e2e/group-company/helpers.ts: nav 382 + aside 479 + header 90 characters, with every
 * /api/** request fulfilled as a 500, cleared the bar on /payroll, /leave, /attendance, /people,
 * /offboarding and /saudi-compliance.
 *
 * Rules now:
 * - The ROUTE's own <main> region must render content (the shell does not count)
 * - The route must not have bounced to /login
 * - No route may show a crash string
 * - Demo-critical tenant routes must additionally show named, route-specific content
 */

/** Route-specific proof that the RIGHT screen rendered — not merely "a" screen. */
const TENANT_ROUTE_CONTENT: Record<string, RegExp> = {
  '/people':            /employee|staff|headcount/i,
  '/attendance':        /attendance|check.?in|present|absent/i,
  '/leave':             /leave|balance|request/i,
  '/payroll':           /payroll|gross|net|salary|run/i,
  '/approvals':         /approval|pending|request/i,
  '/reports':           /report/i,
  '/saudi-compliance':  /gosi|saudi|nitaqat|compliance|wps/i,
  '/org-chart':         /org|report|hierarch/i,
  '/dashboard':         /dashboard|overview|employee|headcount/i,
};

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

      // Measure the route's OWN region. `body` length here was satisfied by the shell alone.
      await expect.poll(
        async () => mainContentLength(page),
        { timeout: 10_000, message: `${route} rendered only the navigation shell — no content of its own` },
      ).toBeGreaterThan(80);

      await expect(page, `${route} bounced to the login screen`).not.toHaveURL(/\/login/);

      const main = await mainText(page);
      expect(crashIndicators(main), `${route} rendered a fatal error`).toEqual([]);
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

      await expect(page, `${route} bounced to the login screen`).not.toHaveURL(/\/login/);
      await expect.poll(
        async () => mainContentLength(page),
        { timeout: 10_000, message: `${route} rendered only the navigation shell — no content of its own` },
      ).toBeGreaterThan(80);

      const main = await mainText(page);
      expect(crashIndicators(main), `${route} rendered a fatal error`).toEqual([]);

      // Named content: proves the RIGHT module rendered, which a length check never could.
      const expected = TENANT_ROUTE_CONTENT[route];
      if (expected)
        expect(main, `${route} rendered content, but nothing matching ${expected} — wrong screen or an error surface`)
          .toMatch(expected);
    }
  });
});

test.describe('Ras Al-Manar tenant — demo sanity', () => {
  test('dashboard and people load without crash', async ({ page }) => {
    await tenantLogin(page, RASALMANAR_ADMIN.email, RASALMANAR_ADMIN.password, RASALMANAR_SLUG);
    for (const route of ['/dashboard', '/people']) {
      await gotoRoute(page, route);
      await expect(page, `${route} bounced to the login screen`).not.toHaveURL(/\/login/);
      await expect.poll(
        async () => mainContentLength(page),
        { timeout: 10_000, message: `${route} rendered only the navigation shell — no content of its own` },
      ).toBeGreaterThan(80);
      const main = await mainText(page);
      expect(crashIndicators(main), `${route} rendered a fatal error`).toEqual([]);
      const expected = TENANT_ROUTE_CONTENT[route];
      if (expected) expect(main, `${route} did not render ${expected}`).toMatch(expected);
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
    const main = await mainText(page);
    expect(crashIndicators(main), 'tenant detail rendered a fatal error').toEqual([]);
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
