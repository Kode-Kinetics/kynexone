import { expect, test, type Page } from '@playwright/test';
import type { PasswordPolicy } from '../src/lib/passwordRequirements';

const policy: PasswordPolicy = {
  passwordMinLength: 12,
  passwordRequireUppercase: true,
  passwordRequireLowercase: true,
  passwordRequireDigit: true,
  passwordRequireSpecial: true,
};

async function openCreateUser(page: Page, options: { policy?: PasswordPolicy; failPolicy?: boolean; dark?: boolean } = {}) {
  let writes = 0;
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(dark => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', dark ? 'dark' : 'light');
  }, options.dark ?? false);
  await page.route('**/api/**', route => {
    const pathname = new URL(route.request().url()).pathname;
    const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (route.request().method() !== 'GET') {
      writes++;
      return json({}); // Never touch a real account, even if client validation regresses.
    }
    if (pathname === '/api/auth/me') return json({ id: 'u1', tenantId: 't1', email: 'admin@example.test', fullName: 'Test Admin', roles: ['Admin'], accountType: 'Group', isGroupScope: true, companies: [], permissions: ['users.manage', 'roles.manage', 'security.manage'] });
    if (pathname === '/api/access/security-settings') return options.failPolicy
      ? route.fulfill({ status: 500, contentType: 'application/json', body: '{}' })
      : json(options.policy ?? policy);
    if (pathname === '/api/access/roles') return json(['Admin', 'HR Manager', 'Payroll Manager', 'HR Officer', 'Payroll Officer', 'Finance Approver', 'Compliance Officer', 'Manager', 'Supervisor', 'Recruiter', 'HR Assistant', 'Auditor', 'Kiosk Operator', 'Employee'].map((name, index) => ({ id: String(index), name, permissions: [] })));
    if (pathname === '/api/features/disabled-keys' || pathname === '/api/features/modules' || pathname === '/api/notifications' || pathname === '/api/access/permissions' || pathname.includes('help-text')) return json([]);
    if (pathname === '/api/access/ceiling') return json({ userId: 'u1', isAdmin: true, heldPermissions: ['users.manage', 'roles.manage', 'security.manage'], roles: [] });
    if (pathname === '/api/tenant-admin/usage') return json({ activeUsers: 1, maxUsers: 20 });
    if (pathname === '/api/tenant-admin/localization') return json({ defaultTimezone: 'America/New_York', currencyCode: 'USD', countryCode: 'US' });
    return json({ items: [], total: 0 });
  });
  await page.goto('/user-management');
  await expect(page.getByRole('heading', { name: 'User Management & Access Control' })).toBeVisible();
  await page.getByRole('button', { name: 'Create User', exact: true }).click();
  const password = page.getByLabel('Password', { exact: true });
  const panel = page.locator(`[id="${await password.getAttribute('aria-describedby')}"]`);
  return { password, panel, getWrites: () => writes, errors };
}

test('password checklist updates live, preserves show/hide, and fits the viewport', async ({ page }, testInfo) => {
  const { password, panel, getWrites, errors } = await openCreateUser(page);
  await expect(panel.getByRole('listitem')).toHaveCount(5);
  await expect(panel.getByText('At least 12 characters', { exact: false })).toBeVisible();
  await expect(panel.getByRole('status')).toHaveText('0 of 5 password requirements met.');
  await password.fill('Ab1!');
  await expect(panel.getByRole('status')).toHaveText('4 of 5 password requirements met.');
  await expect(panel.getByRole('listitem').filter({ hasText: 'One uppercase letter' })).toHaveClass(/text-emerald-700/);
  await expect(panel.getByRole('listitem').filter({ hasText: 'At least 12' })).not.toHaveClass(/text-emerald-700/);
  await password.fill('Ab1!abcdefgh');
  await expect(panel.getByRole('status')).toHaveText('All password requirements met.');
  await expect(panel.locator('li.text-emerald-700')).toHaveCount(5);
  await page.getByRole('button', { name: 'Show password', exact: true }).click();
  await expect(password).toHaveAttribute('type', 'text');
  await expect(password).toHaveValue('Ab1!abcdefgh');
  await page.getByRole('button', { name: 'Hide password', exact: true }).press('Space');
  await expect(password).toHaveAttribute('type', 'password');
  await password.fill('Ab1abcdefgh');
  await expect(panel.getByRole('status')).toHaveText('3 of 5 password requirements met.');
  await expect(panel.getByRole('listitem').filter({ hasText: 'One special character' })).not.toHaveClass(/text-emerald-700/);
  // Client-side failure must not send the password or create an account.
  const modal = page.getByRole('heading', { name: 'Create User', exact: true }).locator('..');
  await modal.locator('input').nth(0).fill('Test Person');
  await modal.locator('input[type="email"]').fill('test@example.test');
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(modal.getByText('Please meet all password requirements below.')).toBeVisible();
  expect(getWrites()).toBe(0);
  await password.fill('Ab1!abcdefgh');
  await modal.evaluate(element => { element.scrollTop = 0; });
  const box = await modal.boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.y).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(page.viewportSize()!.width);
  expect(box!.y + box!.height).toBeLessThanOrEqual(page.viewportSize()!.height);
  // The backdrop must cover mobile navigation/header, not sit in main's stacking context.
  expect(await modal.locator('..').evaluate(element => element === document.elementFromPoint(1, 1))).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('password-checklist.png') });
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('button', { name: 'Create User', exact: true }).click();
  await expect(password).toHaveValue('');
  await expect(password).toHaveAttribute('type', 'password');
  const reopenedPanel = page.locator(`[id="${await password.getAttribute('aria-describedby')}"]`);
  await expect(reopenedPanel.getByRole('status')).toHaveText('0 of 5 password requirements met.');
  expect(errors).toEqual([]);
});

test('checklist follows optional workspace rules in dark mode', async ({ page }, testInfo) => {
  const { password, panel, errors } = await openCreateUser(page, { dark: true, policy: { ...policy, passwordMinLength: 14, passwordRequireDigit: false, passwordRequireSpecial: false } });
  await expect(panel.getByRole('listitem')).toHaveCount(3);
  await expect(panel.getByText('One number', { exact: false })).toHaveCount(0);
  await password.fill('Abcdefghijklmn');
  await expect(panel.getByRole('status')).toHaveText('All password requirements met.');
  await page.screenshot({ path: testInfo.outputPath('password-checklist-dark.png') });
  expect(errors).toEqual([]);
});

test('unavailable rules are explicit and can be retried without losing the password', async ({ page }) => {
  const { password, panel, getWrites } = await openCreateUser(page, { failPolicy: true });
  await expect(panel.getByText(/Workspace rules couldn’t be loaded/)).toBeVisible();
  await password.fill('Ab1!abcdefgh');
  await page.route('**/api/access/security-settings', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(policy) }));
  await panel.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(panel.getByRole('status')).toHaveText('All password requirements met.');
  await expect(password).toHaveValue('Ab1!abcdefgh');
  expect(getWrites()).toBe(0);
});
