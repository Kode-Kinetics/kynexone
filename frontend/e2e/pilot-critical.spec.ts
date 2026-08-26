import { expect, test } from '@playwright/test';

const TENANT_SLUG = process.env.E2E_DEFAULT_TENANT_SLUG ?? 'intelliflow';
const ADMIN_EMAIL = process.env.E2E_DEFAULT_ADMIN_EMAIL ?? 'admin@intelliflow.com';
const ADMIN_PASSWORD = process.env.E2E_DEFAULT_ADMIN_PASSWORD ?? 'IntelliFlow@2026!';
const EXPECTED_MIN_EMPLOYEES = Number(process.env.E2E_MIN_EMPLOYEES ?? '1');

const CRITICAL_ROUTES = [
  '/people',
  '/attendance',
  '/leave',
  '/shifts',
  '/overtime',
  '/payroll',
  '/loans',
  '/recruitment',
  '/offboarding',
  '/performance',
  '/compliance',
  '/reports',
  '/approvals',
] as const;

test.describe('client-pilot critical tenant lane', () => {
  test('real seeded tenant loads dashboard and every core module without API failures', async ({ page }) => {
    // This is intentionally a broad, serial pilot witness across every critical route.
    // Keep each route's 20s fail-fast assertion below, but do not let Playwright's 30s
    // default whole-test budget turn a healthy cold-start run into a flaky retry.
    test.setTimeout(90_000);
    const serverFailures: string[] = [];
    page.on('response', (response) => {
      if (response.url().includes('/api/') && response.status() >= 500) {
        serverFailures.push(`${response.status()} ${response.request().method()} ${response.url()}`);
      }
    });

    await page.goto('/login');
    await page.locator('#li-em, input[type="email"]').first().fill(ADMIN_EMAIL);
    await page.locator('#li-pw, input[type="password"]').first().fill(ADMIN_PASSWORD);
    await page.locator('#li-ws, input[autocomplete="organization"]').first().fill(TENANT_SLUG);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/dashboard/, { timeout: 20_000 });

    const dashboardResponse = await page.waitForResponse(
      (response) => response.url().includes('/api/dashboard/full') && response.request().method() === 'GET',
      { timeout: 20_000 },
    );
    expect(dashboardResponse.status(), await dashboardResponse.text()).toBe(200);
    const dashboard = await dashboardResponse.json();
    expect(dashboard.summary?.totalEmployees).toBeGreaterThanOrEqual(EXPECTED_MIN_EMPLOYEES);
    await expect(page.getByText(/unable to load dashboard/i)).toHaveCount(0);

    for (const route of CRITICAL_ROUTES) {
      await page.goto(route, { waitUntil: 'domcontentloaded' });
      await expect(page, `${route} redirected out of the authenticated application`).not.toHaveURL(/\/login/);
      await expect.poll(
        async () => (await page.locator('body').innerText()).trim().length,
        { message: `${route} never left its loading/blank state`, timeout: 20_000 },
      ).toBeGreaterThan(50);
      const body = (await page.locator('body').innerText()).trim();
      expect(body.toLowerCase(), `${route} rendered a fatal error`).not.toMatch(
        /something went wrong|unexpected error|cannot read properties of undefined|typeerror/,
      );
    }

    expect(serverFailures, `Core-module navigation produced server errors:\n${serverFailures.join('\n')}`).toEqual([]);
  });
});
