import { test as setup } from '@playwright/test';
import { platformLogin, PLATFORM_STATE } from './helpers';

/**
 * Authenticate ONCE and persist the session for every spec that needs platform admin.
 *
 * WHY THIS EXISTS. The suite previously called `platformLogin` in `beforeEach`, so a 17-route
 * sanity spec performed 17 logins in a few seconds. The API rate-limits platform login to
 * `RateLimiting:PlatformLoginPermitLimit` (default 5) per window, so most of those logins were
 * correctly rejected and the tests failed in the login helper — not on the routes they were
 * meant to check. The rate limiter is right; re-authenticating per test is what was wrong.
 *
 * Logging in once and reusing the storage state keeps the suite honest (it still exercises a
 * real login) while staying inside the production rate limit, which is what makes it viable
 * as a CI gate.
 */
setup('authenticate as platform admin', async ({ page }) => {
  await platformLogin(page);
  await page.context().storageState({ path: PLATFORM_STATE });
});
