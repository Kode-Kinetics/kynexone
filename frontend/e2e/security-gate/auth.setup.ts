import { test as setup, expect, request as pwRequest } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { ROLES, BASE_URL, storageStatePath, tokenPath, type RoleFixture } from './roles';

/**
 * WAVE 1 B3 — authenticate every role ONCE, then never again.
 *
 * <b>This setup FAILS. It does not skip.</b> The suite it replaces probed the stack in `beforeAll` and
 * called `test.skip()` when the probe failed, so a completely dead backend produced a green run. A gate
 * that cannot fail is not a gate — and this one is intended to become a required PR check, where a
 * silent pass is worse than no check at all.
 *
 * <b>Rate limiting is respected, not raised.</b> The API permits 10 login attempts per 60s window. Nine
 * roles authenticate here, paced apart, and every spec then reuses the stored session. Raising
 * `RateLimit:LoginPermitLimit` to make tests pass would weaken a production brute-force control for the
 * convenience of the test suite.
 */

const PACING_MS = Number(process.env.E2E_LOGIN_PACING_MS ?? 7_000);

setup('stack is genuinely reachable — fail, never skip', async () => {
  const api = await pwRequest.newContext({ baseURL: BASE_URL, timeout: 30_000 });
  try {
    // 401 is the healthy answer: the frontend is serving AND proxying to a live backend that
    // rejects an unauthenticated caller. A connection error or a 5xx means the stack is not up.
    const resp = await api.get('/api/auth/me');
    expect(
      resp.status(),
      `GET ${BASE_URL}/api/auth/me returned ${resp.status()}. The security gate requires a REAL stack: `
      + `Postgres + backend + production frontend build. See docs/CHROME_SECURITY_GATE.md. `
      + `This FAILS rather than skipping, deliberately — a gate that skips when the stack is down `
      + `reports green for a completely dead system.`,
    ).toBe(401);
  } finally {
    await api.dispose();
  }
});

for (const [index, role] of ROLES.entries()) {
  setup(`authenticate ${role.key} (${role.label})`, async ({ browser }) => {
    setup.setTimeout(120_000);

    // Pace logins under the 10-per-60s window instead of raising the limit.
    if (index > 0) await new Promise(r => setTimeout(r, PACING_MS));

    const api = await pwRequest.newContext({ baseURL: BASE_URL, timeout: 30_000 });
    try {
      const { token, refreshToken } = await login(api, role);

      // The browser session. Tokens live in localStorage for this app, so the storage state is
      // seeded through an origin script rather than by driving the login form nine times.
      const ctx = await browser.newContext({ baseURL: BASE_URL });
      const page = await ctx.newPage();
      await page.goto('/');
      // The EXACT keys the app reads. Getting these wrong is not a cosmetic bug: the browser context
      // silently stays anonymous, every UI assertion then runs against a logged-out page, and a suite
      // of "cross-company data is not visible" tests passes because NO data is visible. That is how
      // the first version of this file produced 22 green tests while proving nothing in the browser.
      await page.evaluate(
        ([t, r, isPlatform]) => {
          if (isPlatform) {
            localStorage.setItem('platform_access_token', t as string);
          } else {
            localStorage.setItem('zayra_access_token', t as string);
            if (r) localStorage.setItem('zayra_refresh_token', r as string);
          }
        },
        [token, refreshToken ?? '', role.tenantSlug === null] as [string, string, boolean],
      );

      fs.mkdirSync(path.dirname(storageStatePath(role.key)), { recursive: true });
      await ctx.storageState({ path: storageStatePath(role.key) });
      await ctx.close();

      // The raw token, for the direct-API half of every boundary. Rule 15: a security boundary must be
      // proven through the API as well as the browser, because a hidden menu item is not authorization.
      fs.writeFileSync(
        tokenPath(role.key),
        JSON.stringify({ key: role.key, email: role.email, token }, null, 2),
      );
    } finally {
      await api.dispose();
    }
  });
}

async function login(api: import('@playwright/test').APIRequestContext, role: RoleFixture) {
  const isPlatform = role.tenantSlug === null;
  const url = isPlatform ? '/api/platform/auth/login' : '/api/auth/login';
  const data = isPlatform
    ? { email: role.email, password: role.password }
    : { email: role.email, password: role.password, tenantSlug: role.tenantSlug };

  const resp = await api.post(url, { data });

  expect(
    resp.status(),
    `Login failed for ${role.key} <${role.email}>: ${resp.status()} ${await resp.text().catch(() => '')}\n`
    + (resp.status() === 429
      ? 'This is the login rate limiter (10 per 60s). Increase E2E_LOGIN_PACING_MS — do NOT raise '
        + 'RateLimit:LoginPermitLimit, which would weaken a production brute-force control.'
      : 'Check that the enterprise-group seed ran (SEED_ENTERPRISE_TEST_DATA=true) and, for the '
        + 'platform operator, that PLATFORM_ADMIN_PASSWORD was supplied.'),
  ).toBe(200);

  const body = await resp.json();
  const token = body.accessToken ?? body.token ?? body.access_token;
  expect(token, `Login for ${role.key} returned no access token`).toBeTruthy();
  return { token: token as string, refreshToken: body.refreshToken ?? body.refresh_token };
}
