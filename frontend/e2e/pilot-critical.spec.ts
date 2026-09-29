import { expect, test } from '@playwright/test';
import { mainContentLength, mainText, crashIndicators, expectNonEmptyList } from './helpers';
import { INTELLIFLOW_ADMIN, INTELLIFLOW_SLUG } from './world';

// Defaults come from e2e/world.ts — the declaration e2e/bootstrap/provision.ts builds the tenant
// from — rather than literals, so this lane cannot be pointed at an account nothing created.
const TENANT_SLUG = process.env.E2E_DEFAULT_TENANT_SLUG ?? INTELLIFLOW_SLUG;
const ADMIN_EMAIL = process.env.E2E_DEFAULT_ADMIN_EMAIL ?? INTELLIFLOW_ADMIN.email;
const ADMIN_PASSWORD = process.env.E2E_DEFAULT_ADMIN_PASSWORD ?? INTELLIFLOW_ADMIN.password;
const EXPECTED_MIN_EMPLOYEES = Number(process.env.E2E_MIN_EMPLOYEES ?? '1');

/**
 * The three screens the pilot feared would be empty. A route that loads but shows zero rows is the
 * demo failure — so these are asserted on RENDERED ROW COUNT, not on page length.
 *
 * Live demo data at the time of writing: attendance_daily_records 4,234 · leave_requests 54 (zero
 * null company_id) · approval_requests 13. The floors below are deliberately 1, not those numbers:
 * the test must catch "empty", not police the seed's exact volume, which would make it brittle.
 */
const MUST_HAVE_ROWS = ['/attendance', '/leave', '/approvals'] as const;

/** Route-specific proof that the RIGHT module rendered, not merely "a" page. */
const ROUTE_CONTENT: Record<string, RegExp> = {
  '/people':      /employee|staff|headcount/i,
  '/attendance':  /attendance|check.?in|present|absent/i,
  '/leave':       /leave|balance|request/i,
  '/payroll':     /payroll|gross|net|salary|run/i,
  '/approvals':   /approval|pending|request/i,
  '/compliance':  /compliance|gosi|saudi|wps/i,
  '/reports':     /report/i,
};

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
      // Measure the ROUTE's own <main>, not <body>. The persistent shell (sidebar + nav + header)
      // is ~950 characters on its own, so `body.length > 50` was satisfied with every /api/** call
      // returning 500 — it could not fail for the reason it claimed to check.
      await expect.poll(
        async () => mainContentLength(page),
        { message: `${route} rendered only the navigation shell — no content of its own`, timeout: 20_000 },
      ).toBeGreaterThan(80);

      const main = await mainText(page);
      expect(crashIndicators(main), `${route} rendered a fatal error`).toEqual([]);

      const expected = ROUTE_CONTENT[route];
      if (expected)
        expect(main, `${route} rendered content but nothing matching ${expected} — wrong screen or an error surface`)
          .toMatch(expected);

      // The demo-critical part: these three screens must show actual rows, not an empty state.
      if ((MUST_HAVE_ROWS as readonly string[]).includes(route)) {
        const rows = await expectNonEmptyList(page, route, 1);
        console.log(`[pilot] ${route} rendered ${rows} data row(s)`);
      }
    }

    expect(serverFailures, `Core-module navigation produced server errors:\n${serverFailures.join('\n')}`).toEqual([]);
  });

  /**
   * The regression this lane kept reporting as a product bug.
   *
   * `/leave rendered 0 data row(s)` failed attempt 1 and passed the retry on MAIN (run
   * 35950845579) and on the integration branch (run 35953783874) with byte-identical output. No
   * module was blank: `expectNonEmptyList` took ONE snapshot the moment the route's shell had
   * painted, before the Leave dashboard's three XHRs had answered, so the gate was measuring CI
   * runner latency and calling it a demo failure. A gate that goes red on a slow answer cannot be
   * trusted when it goes red on a real one.
   *
   * Holding every leave call for two seconds makes that race DETERMINISTIC, in the direction CI
   * hit by accident. With the snapshot helper this fails every time; with the polling helper it
   * passes, and a genuinely empty /leave still fails — `expectNonEmptyList` only ever stops early
   * on rows it has actually seen.
   */
  test('a slow leave API is waited for, not reported as a blank module', async ({ page }) => {
    test.setTimeout(90_000);

    await page.goto('/login');
    await page.locator('#li-em, input[type="email"]').first().fill(ADMIN_EMAIL);
    await page.locator('#li-pw, input[type="password"]').first().fill(ADMIN_PASSWORD);
    await page.locator('#li-ws, input[autocomplete="organization"]').first().fill(TENANT_SLUG);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/dashboard/, { timeout: 20_000 });

    // Delay, never stub. The rows this asserts on are the seeded tenant's real ones; only their
    // arrival is slowed, so the test still fails if the leave API stops returning them.
    await page.route('**/api/leave/**', async (route) => {
      await new Promise((resolve) => setTimeout(resolve, 2_000));
      await route.continue();
    });

    await page.goto('/leave', { waitUntil: 'domcontentloaded' });
    // The same two steps the walk above performs, in the same order. The poll is satisfied by the
    // Leave shell (heading + tab strip) while the rows are still in flight — which is exactly how
    // the snapshot helper came to be asked for a verdict it could not yet have.
    await expect.poll(
      async () => mainContentLength(page),
      { message: '/leave rendered only the navigation shell', timeout: 20_000 },
    ).toBeGreaterThan(80);

    const rows = await expectNonEmptyList(page, '/leave', 1);
    console.log(`[pilot] /leave under a 2s API delay rendered ${rows} data row(s)`);
  });
});
