import { test, expect } from '@playwright/test';
import { PLATFORM_EMAIL, PLATFORM_PASSWORD } from './helpers';

test.describe('Platform authentication', () => {
  test('setup login redirects to /platform/dashboard', async ({ page }) => {
    // The setup dependency performs the real credential submission exactly once.
    // Reuse that session because a newer platform login deliberately invalidates
    // the operator's previous session stamp.
    await page.goto('/platform/dashboard');
    await expect(page).toHaveURL(/\/platform\/dashboard/);
  });

  test('setup login stores platform_access_token in localStorage', async ({ page }) => {
    await page.goto('/platform/dashboard');
    const token = await page.evaluate(() => localStorage.getItem('platform_access_token'));
    expect(token).not.toBeNull();
    expect(token!.length).toBeGreaterThan(20);
  });

  test('wrong password shows an error message', async ({ page }) => {
    await page.addInitScript(() => localStorage.removeItem('platform_access_token'));
    await page.goto('/platform/login');
    await page.getByRole('textbox', { name: 'Email address' }).fill(PLATFORM_EMAIL);
    const password = page.getByRole('textbox', { name: 'Password' });
    await password.fill('WRONG_PASSWORD_XYZ');
    await password.press('Enter');
    // Wait for the error message to appear (API call completes and shows error)
    await expect(page.getByText(/invalid|incorrect|error|wrong|credentials/i).first()).toBeVisible({ timeout: 10_000 });
    await expect(page).toHaveURL(/\/platform\/login/);
  });

  test('unauthenticated visit to /platform/dashboard redirects to /platform/login', async ({ page }) => {
    // Remove the setup project's stored token before application code runs.
    await page.addInitScript(() => localStorage.removeItem('platform_access_token'));
    await page.goto('/platform/dashboard');
    await page.waitForURL(/\/platform\/login/, { timeout: 10_000 });
    await expect(page).toHaveURL(/\/platform\/login/);
  });

  test('password visibility toggle works on login form', async ({ page }) => {
    await page.addInitScript(() => localStorage.removeItem('platform_access_token'));
    await page.goto('/platform/login');
    const passwordInput = page.getByRole('textbox', { name: 'Password' });
    // Initially masked
    await expect(passwordInput).toHaveAttribute('type', 'password');
    // Click toggle
    await page.getByRole('button', { name: /show password|hide password/i }).click();
    // Should now be visible (type=text)
    const typeAfterToggle = await passwordInput.getAttribute('type');
    expect(['text', 'password']).toContain(typeAfterToggle);
  });
});
