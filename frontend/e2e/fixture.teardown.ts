import { test as teardown, expect } from '@playwright/test';
import { purgeLimitedTenantFixture } from './limited-tenant-fixture';
import { apiPlatformFreshLogin } from './helpers';

teardown('purge isolated limited tenant and verify platform logout', async ({ page, request }) => {
  teardown.setTimeout(120_000);
  // Acquire a fresh final-session token. Playwright may construct this project's
  // context before setup rewrites its storage-state file, and platform sessions
  // deliberately invalidate older operator tokens on a newer login.
  let token: string;
  try {
    token = await apiPlatformFreshLogin(request);
  } catch {
    // If setup could not authenticate, it could not provision the fixture. Do not
    // mask the original setup failure with a secondary cleanup error.
    return;
  }
  await purgeLimitedTenantFixture(request, token);

  // Platform logout deliberately invalidates every outstanding token for this
  // operator. Verify it only after all specs and fixture cleanup have finished,
  // otherwise the shared authenticated setup state would be invalidated halfway
  // through the suite.
  await page.goto('/platform/login');
  await page.evaluate((freshToken) => localStorage.setItem('platform_access_token', freshToken), token);
  await page.goto('/platform/dashboard');
  const logout = page.locator('button[title="Sign out"], button[aria-label*="ign out"]').first();
  await expect(logout).toBeVisible({ timeout: 10_000 });
  await logout.click();
  await page.waitForURL(/\/platform\/login/, { timeout: 10_000 });
  await expect.poll(() => page.evaluate(() => localStorage.getItem('platform_access_token')))
    .toBeNull();
});
