import { test, expect } from '@playwright/test';
import {
  tenantLogin, tenantLoginLive, tenantLogout,
  INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1,
  RASALMANAR_SLUG, RASALMANAR_ADMIN,
  apiLoginLive,
} from './helpers';

test.describe('Tenant authentication', () => {

  // ── Happy-path login ────────────────────────────────────────────────────────

  test('IntelliFlow admin can log in and reaches dashboard', async ({ page }) => {
    await tenantLoginLive(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await expect(page).not.toHaveURL(/\/login/, { timeout: 5_000 });
    const body = (await page.locator('body').innerText()) ?? '';
    expect(body.toLowerCase()).not.toContain('something went wrong');
  });

  test('Ras Al-Manar admin can log in', async ({ page }) => {
    await tenantLoginLive(page, RASALMANAR_ADMIN.email, RASALMANAR_ADMIN.password, RASALMANAR_SLUG);
    await expect(page).not.toHaveURL(/\/login/, { timeout: 5_000 });
  });

  test('IntelliFlow employee1 can log in', async ({ page }) => {
    await tenantLoginLive(page, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    await expect(page).not.toHaveURL(/\/login/, { timeout: 5_000 });
  });

  // ── Wrong credentials ───────────────────────────────────────────────────────

  test('wrong password shows error and stays on login', async ({ page }) => {
    await page.goto('/login');
    await page.locator('#li-em, input[type="email"]').first().fill(INTELLIFLOW_ADMIN.email);
    await page.locator('#li-pw, input[type="password"]').first().fill('WRONG_PASSWORD_XYZ!');
    await page.locator('#li-ws, input[autocomplete="organization"]').first().fill(INTELLIFLOW_SLUG);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    // Wait for error message to appear after API responds
    await expect(page.getByText(/invalid|incorrect|error|wrong|credentials/i).first()).toBeVisible({ timeout: 10_000 });
    await expect(page).toHaveURL(/\/login/);
  });

  test('wrong slug shows error and stays on login', async ({ page }) => {
    await page.goto('/login');
    await page.locator('#li-em, input[type="email"]').first().fill(INTELLIFLOW_ADMIN.email);
    await page.locator('#li-pw, input[type="password"]').first().fill(INTELLIFLOW_ADMIN.password);
    await page.locator('#li-ws, input[autocomplete="organization"]').first().fill('nonexistent-tenant-xyz');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page).toHaveURL(/\/login/, { timeout: 8_000 });
  });

  // ── Unauthenticated guard ───────────────────────────────────────────────────

  test('unauthenticated visit to protected route redirects to login', async ({ page }) => {
    await page.goto('/login');
    await tenantLogout(page);
    await page.goto('/dashboard');
    await page.waitForURL(/\/login/, { timeout: 10_000 });
    await expect(page).toHaveURL(/\/login/);
  });

  // ── Logout ──────────────────────────────────────────────────────────────────

  test('after token cleared, dashboard redirects to login', async ({ page }) => {
    // The login journey is covered above. Reuse the setup session here so this logout/route-guard
    // assertion does not consume the final permit in the 10-request authentication test window.
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await tenantLogout(page);
    await page.goto('/dashboard');
    await page.waitForURL(/\/login/, { timeout: 10_000 });
    await expect(page).toHaveURL(/\/login/);
  });

  // ── API-level token validation ──────────────────────────────────────────────

  test('API login returns a valid JWT for IntelliFlow admin', async ({ request }) => {
    const token = await apiLoginLive(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    expect(typeof token).toBe('string');
    expect(token.split('.').length).toBe(3); // valid JWT format
  });

  test('API login with bad password returns 401', async ({ request }) => {
    const resp = await request.post('/api/auth/login', {
      data: { email: INTELLIFLOW_ADMIN.email, password: 'WRONG!', tenantSlug: INTELLIFLOW_SLUG },
    });
    expect(resp.status()).toBe(401);
  });

  test('API login with bad slug returns 401 or 404', async ({ request }) => {
    const resp = await request.post('/api/auth/login', {
      data: { email: INTELLIFLOW_ADMIN.email, password: INTELLIFLOW_ADMIN.password, tenantSlug: 'ghost-tenant-xyz' },
    });
    expect([401, 404]).toContain(resp.status());
  });

  // ── Cross-tenant login guard ─────────────────────────────────────────────────

  test('IntelliFlow user cannot log in using Ras Al-Manar slug', async ({ request }) => {
    const resp = await request.post('/api/auth/login', {
      data: { email: INTELLIFLOW_ADMIN.email, password: INTELLIFLOW_ADMIN.password, tenantSlug: RASALMANAR_SLUG },
    });
    expect([401, 403, 404]).toContain(resp.status());
  });

  test('Ras Al-Manar user cannot log in using IntelliFlow slug', async ({ request }) => {
    const resp = await request.post('/api/auth/login', {
      data: { email: RASALMANAR_ADMIN.email, password: RASALMANAR_ADMIN.password, tenantSlug: INTELLIFLOW_SLUG },
    });
    expect([401, 403, 404]).toContain(resp.status());
  });
});
