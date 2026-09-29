import { Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';
// Imported (not merely re-exported) because this module's own helpers use them below: a bare
// `export … from` re-export does not bring the names into local scope.
import { MISSING_WORLD, PLATFORM_EMAIL, PLATFORM_PASSWORD } from './world';

// ── Identities ────────────────────────────────────────────────────────────────
// Re-exported from e2e/world.ts, which is the ONE declaration of the fixture world and the same
// module e2e/bootstrap/provision.ts creates it from. Declaring them here as well is how this file
// and e2e/security-gate/roles.ts ended up pointing at two different platform operators
// (`platform@kynexone.com` vs `admin@platform.local`) — a drift no test could catch, because each
// lane provisioned nothing and simply failed to log in.
export {
  PLATFORM_EMAIL, PLATFORM_PASSWORD,
  INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, INTELLIFLOW_HR_DIR, INTELLIFLOW_HR_MGR,
  INTELLIFLOW_FINANCE, INTELLIFLOW_MANAGER, INTELLIFLOW_SUPERVISOR,
  INTELLIFLOW_EMP1, INTELLIFLOW_EMP2, INTELLIFLOW_AUDITOR,
  RASALMANAR_SLUG, RASALMANAR_ADMIN,
  EVOSTEL_SLUG, EVOSTEL_ADMIN, EVOSTEL_EMP1,
  ALMARAI_SLUG, TATA_SLUG, GROUP_PASSWORD, groupEmail, companyEmail,
} from './world';

export const BASE_URL = process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL ?? 'http://localhost:5173';

// Where auth.setup.ts persists the platform-admin session. Reused by every platform spec so the
// suite authenticates ONCE rather than once per test — see auth.setup.ts for why that matters.
export const PLATFORM_STATE = 'e2e/.auth/platform.json';
export const TENANT_STATE = 'e2e/.auth/tenants.json';

type TenantSession = { accessToken: string; refreshToken: string };

export const tenantSessionKey = (email: string, slug: string): string =>
  `${slug.toLowerCase()}|${email.toLowerCase()}`;

export async function tenantSetupSession(email: string, slug: string): Promise<TenantSession> {
  let sessions: Record<string, TenantSession>;
  try {
    sessions = JSON.parse(await readFile(TENANT_STATE, 'utf8')) as Record<string, TenantSession>;
  } catch {
    throw new Error(
      `${TENANT_STATE} does not exist, so no persona sessions were ever minted.\n`
      + `${MISSING_WORLD}`,
    );
  }
  const session = sessions[tenantSessionKey(email, slug)];
  if (!session?.accessToken)
    throw new Error(
      `No setup tenant session exists for ${email} / ${slug}.\n`
      + 'Either auth.setup.ts does not mint this persona, or the login failed because the fixture '
      + `world is missing that account.\n${MISSING_WORLD}`,
    );
  return session;
}

// ── Platform admin helpers ────────────────────────────────────────────────────

export async function platformLogin(page: Page): Promise<void> {
  await page.goto('/platform/login');
  await page.locator('#pl-em, input[type="email"]').first().fill(PLATFORM_EMAIL);
  const passwordInput = page.locator('#pl-pw, input[type="password"]').first();
  await passwordInput.fill(PLATFORM_PASSWORD);
  // Submit from the input instead of clicking the animated button. The login
  // surface intentionally replaces the button during its entrance transition,
  // which makes a pointer click race DOM replacement even though the form is ready.
  await passwordInput.press('Enter');
  await page.waitForURL(/\/platform\/dashboard/, { timeout: 15_000 });
}

export async function platformLogout(page: Page): Promise<void> {
  await page.evaluate(async () => {
    const token = localStorage.getItem('platform_access_token');
    if (token) {
      await fetch('/api/platform/auth/logout', {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}` },
      });
    }
    localStorage.removeItem('platform_access_token');
  });
}

// ── Tenant login helpers ──────────────────────────────────────────────────────

export async function tenantLoginLive(
  page: Page,
  email: string,
  password: string,
  slug: string
): Promise<void> {
  await page.goto('/login');
  await page.locator('#li-em, input[type="email"]').first().fill(email);
  await page.locator('#li-pw, input[type="password"]').first().fill(password);
  await page.locator('#li-ws, input[autocomplete="organization"]').first().fill(slug);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.waitForURL(/\/(dashboard|app)/, { timeout: 15_000 });
  // Wait for the dashboard's initial API calls to settle before each test navigates away.
  // Without this, background fetches can fire 403s that trigger window.location redirects,
  // which abort subsequent page.goto() calls with ERR_ABORTED.
  await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {/* ignore timeout */});
}

/** Reuse a setup-time persona session for feature/workflow UI assertions. */
export async function tenantLogin(
  page: Page,
  email: string,
  _password: string,
  slug: string
): Promise<void> {
  const session = await tenantSetupSession(email, slug);
  await page.goto('/login');
  await page.evaluate(({ accessToken, refreshToken }) => {
    localStorage.setItem('zayra_access_token', accessToken);
    localStorage.setItem('zayra_refresh_token', refreshToken);
  }, session);
  await page.goto('/dashboard');
  await page.waitForURL(/\/(dashboard|app|group)/, { timeout: 15_000 });
}

export async function tenantLogout(page: Page): Promise<void> {
  await page.evaluate(() => {
    localStorage.removeItem('zayra_access_token');
    localStorage.removeItem('zayra_refresh_token');
    localStorage.removeItem('access_token');
    localStorage.removeItem('refresh_token');
    localStorage.removeItem('user');
  });
}

// ── API helpers (direct HTTP, bypasses UI) ───────────────────────────────────

/** Login via API and return the access token. */
export async function apiLoginLive(
  request: import('@playwright/test').APIRequestContext,
  email: string,
  password: string,
  slug: string
): Promise<string> {
  const resp = await request.post('/api/auth/login', {
    data: { email, password, tenantSlug: slug },
  });
  if (!resp.ok()) throw new Error(`Login failed: ${resp.status()} ${await resp.text()}`);
  const data = await resp.json();
  return data.accessToken ?? data.token ?? data.access_token;
}

/** Reuse a setup-time persona token for API feature/workflow assertions. */
export async function apiLogin(
  _request: import('@playwright/test').APIRequestContext,
  email: string,
  _password: string,
  slug: string
): Promise<string> {
  return (await tenantSetupSession(email, slug)).accessToken;
}

/** Read the one platform session created by the setup project. */
export async function platformSetupToken(): Promise<string> {
  const state = JSON.parse(await readFile(PLATFORM_STATE, 'utf8')) as {
    origins?: Array<{ localStorage?: Array<{ name: string; value: string }> }>;
  };
  const token = state.origins
    ?.flatMap((origin) => origin.localStorage ?? [])
    .find((entry) => entry.name === 'platform_access_token')
    ?.value;
  if (!token) throw new Error('Platform setup storage state does not contain an access token.');
  return token;
}

/** Reuse the setup project's platform session for authenticated API assertions. */
export async function apiPlatformLogin(
  _request: import('@playwright/test').APIRequestContext
): Promise<string> {
  return platformSetupToken();
}

/** Create a new platform session only when the prior shared session is no longer needed. */
export async function apiPlatformFreshLogin(
  request: import('@playwright/test').APIRequestContext
): Promise<string> {
  const resp = await request.post('/api/platform/auth/login', {
    data: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD },
  });
  if (!resp.ok()) throw new Error(`Platform login failed: ${resp.status()} ${await resp.text()}`);
  const data = await resp.json();
  return data.token ?? data.accessToken;
}

// ── Honest render assertions ──────────────────────────────────────────────────
//
// WHY THESE EXIST — `(await page.locator('body').innerText()).length > 50` was this suite's
// standard "the page loaded" proxy. It is not one. The persistent application shell (sidebar +
// nav + header) renders before any data arrives and is ~950 characters on its own, so the
// threshold is cleared by:
//   • a page whose every /api/** call returned 500,
//   • a page showing an empty-state or a spinner,
//   • in some layouts, a redirect that still paints chrome.
// Proven in e2e/group-company/helpers.ts (nav 382 + aside 479 + header 90). Commit 199cfd5 put it
// plainly: "130 tests passing in 90 seconds against an HR/payroll product was the tell."
//
// Replace the proxy with two things that can actually fail: measure only the ROUTE's own output,
// and assert on specific, semantically meaningful content.

/**
 * Length of the main region's text, excluding the static navigation shell.
 * Deliberately does NOT swallow locator errors — a thrown read must fail the test, not return 0
 * and let a `> 50` check decide the outcome on a page that never rendered.
 */
export async function mainContentLength(page: Page): Promise<number> {
  const main = page.locator('main, [role="main"]').first();
  if ((await main.count()) === 0) return 0;
  return (await main.innerText()).trim().length;
}

/** Visible text of the route's own main region (never the shell). Throws if there is no main. */
export async function mainText(page: Page): Promise<string> {
  const main = page.locator('main, [role="main"]').first();
  if ((await main.count()) === 0)
    throw new Error(`No <main> region at ${page.url()} — the route rendered only the shell.`);
  return await main.innerText();
}

/** Fatal-crash strings. Kept in one place so every suite sniffs for the same set. */
export function crashIndicators(text: string): string[] {
  const lower = text.toLowerCase();
  return ['something went wrong', 'unexpected error', 'cannot read properties of undefined', 'typeerror']
    .filter((s) => lower.includes(s));
}

/**
 * Count the data rows a list screen actually rendered.
 *
 * Tries real table rows first, then the common card/list-item shapes. Returns 0 when nothing
 * matched — callers assert `> 0`, so "I could not find the rows" and "there are no rows" both go
 * red, which is the correct bias for a demo-readiness check.
 */
export async function renderedRowCount(page: Page): Promise<number> {
  const candidates = [
    page.locator('main tbody tr, [role="main"] tbody tr'),
    page.locator('main [role="row"], [role="main"] [role="row"]'),
    page.locator('main [data-testid$="-row"], [role="main"] [data-testid$="-row"]'),
    page.locator('main li[data-id], [role="main"] li[data-id]'),
  ];
  let best = 0;
  for (const c of candidates) best = Math.max(best, await c.count());
  return best;
}

/** Text that a genuinely empty list screen shows. Used to distinguish "empty" from "not loaded". */
const EMPTY_STATE = /no (records|results|data|requests|approvals|entries)|nothing to show|0 results/i;

/**
 * Assert a list route rendered REAL rows — the check the demo actually depends on.
 *
 * Fails loudly on an empty state rather than treating it as "the page loaded fine", because an
 * empty Attendance / Leave / Approvals screen IS the demo failure mode this suite exists to catch.
 *
 * <b>It WAITS for the rows.</b> This used to take a single snapshot the instant the caller asked,
 * which made it a race rather than an assertion: every one of these screens fetches its rows over
 * XHR after the route's shell has painted, and the caller's own `expect.poll(mainContentLength)`
 * is satisfied by the shell alone (the Leave tab strip is ~200 characters on its own). So the
 * verdict came down to whether the API answered inside the few milliseconds between those two
 * lines. It did on a warm machine and did not on a cold CI runner — the pilot lane reported
 * "/leave rendered 0 data row(s)" on main and on every integration branch alike, then retried
 * green, which is the signature of a racing test and not of a blank module.
 *
 * Polling closes that. A screen that genuinely has no rows still fails, one second later, with the
 * same message; a screen whose rows are merely still in flight now passes for the reason it always
 * should have. Deliberately NOT short-circuited on {@link EMPTY_STATE}: these modules render their
 * empty copy while the request is still outstanding, so treating that text as a verdict would
 * reinstate the same race with extra steps.
 */
export async function expectNonEmptyList(
  page: Page, route: string, minRows = 1, timeoutMs = 15_000,
): Promise<number> {
  const deadline = Date.now() + timeoutMs;
  let rows = 0;
  for (;;) {
    rows = await renderedRowCount(page);
    if (rows >= minRows) return rows;
    if (Date.now() >= deadline) break;
    await page.waitForTimeout(200);
  }

  const text = await mainText(page);
  const emptyState = EMPTY_STATE.test(text) ? ' The screen is showing its EMPTY STATE.' : '';
  throw new Error(
    `${route} rendered ${rows} data row(s); at least ${minRows} was required ` +
    `(waited ${timeoutMs}ms).${emptyState}\n` +
    `This is the blank-module failure the pilot feared. Main-region text (first 400 chars):\n` +
    text.slice(0, 400),
  );
}

/**
 * Hard pre-flight: the stack must be up. Throws — never skips.
 *
 * A 401 from /api/auth/me through the frontend proxy proves frontend AND backend are alive and
 * talking. Accepting any sub-500 response would let an unrelated dev server on the same port
 * masquerade as a healthy HRM API.
 */
export async function assertStackReachable(baseUrl: string = BASE_URL): Promise<void> {
  const { request: pwRequest } = await import('@playwright/test');
  const api = await pwRequest.newContext({ baseURL: baseUrl, timeout: 15_000 });
  try {
    const resp = await api.get('/api/auth/me');
    if (resp.status() === 401) return;
    const preview = (await resp.text()).replace(/\s+/g, ' ').slice(0, 160);
    throw new Error(
      `STACK UNHEALTHY: GET ${baseUrl}/api/auth/me must return 401, got ${resp.status()}. ${preview}`,
    );
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('STACK UNHEALTHY')) throw error;
    throw new Error(
      `STACK UNREACHABLE at ${baseUrl}: ${error instanceof Error ? error.message : String(error)}\n` +
      `Start the backend + frontend before running e2e. This is a FAILURE, not a skip: a dead ` +
      `backend must never produce a green run.`,
    );
  } finally {
    await api.dispose();
  }
}
