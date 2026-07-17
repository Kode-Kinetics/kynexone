import { test, expect } from '@playwright/test';
import {
  tenantLogin, tenantLogout,
  INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1,
  EVOSTEL_SLUG, EVOSTEL_ADMIN,
  apiLogin,
} from './helpers';

test.describe('Tenant authentication', () => {

  // ── Happy-path login ────────────────────────────────────────────────────────

  test('IntelliFlow admin can log in and reaches dashboard', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await expect(page).not.toHaveURL(/\/login/, { timeout: 5_000 });
    const body = (await page.locator('body').innerText()) ?? '';
    expect(body.toLowerCase()).not.toContain('something went wrong');
  });

  test('Evostel admin can log in despite PastDue status', async ({ page }) => {
    await tenantLogin(page, EVOSTEL_ADMIN.email, EVOSTEL_ADMIN.password, EVOSTEL_SLUG);
    await expect(page).not.toHaveURL(/\/login/, { timeout: 5_000 });
  });

  test('IntelliFlow employee1 can log in', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
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
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await tenantLogout(page);
    await page.goto('/dashboard');
    await page.waitForURL(/\/login/, { timeout: 10_000 });
    await expect(page).toHaveURL(/\/login/);
  });

  // ── API-level token validation ──────────────────────────────────────────────

  test('API login returns a valid JWT for IntelliFlow admin', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
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

  test('IntelliFlow user cannot log in using Evostel slug', async ({ request }) => {
    const resp = await request.post('/api/auth/login', {
      data: { email: INTELLIFLOW_ADMIN.email, password: INTELLIFLOW_ADMIN.password, tenantSlug: EVOSTEL_SLUG },
    });
    expect([401, 403, 404]).toContain(resp.status());
  });

  test('Evostel user cannot log in using IntelliFlow slug', async ({ request }) => {
    const resp = await request.post('/api/auth/login', {
      data: { email: EVOSTEL_ADMIN.email, password: EVOSTEL_ADMIN.password, tenantSlug: INTELLIFLOW_SLUG },
    });
    expect([401, 403, 404]).toContain(resp.status());
  });
});
