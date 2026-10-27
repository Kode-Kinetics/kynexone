import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * Resetting another user's password from Setup → Users, both ways: a reset link (POST
 * /api/access/users/{id}/password-reset-link) or a temporary password (POST .../admin-reset-password
 * { newPassword, reason }), and the sign-in step that follows a temporary password (/api/auth/login answers
 * { passwordChangeRequired, resetToken, tenantSlug }; the page sends POST /api/auth/reset-password, then signs in
 * again with the new password). Who may reset whom is the server's rule (AdminSetTemporaryPasswordTests); these
 * prove the screens: what is offered, what is sent, and what is said.
 *
 *   npx playwright test -c e2e/playwright.fixture.config.ts e2e/user-password-reset.spec.ts
 *
 * Screenshots go to USER_PASSWORD_RESET_SHOTS when it is set (a review folder), else the test output.
 */

const SHOTS = process.env.USER_PASSWORD_RESET_SHOTS;
async function shot(page: Page, name: string, outputPath: (n: string) => string) {
  const file = SHOTS ? path.join(SHOTS, `${name}.png`) : outputPath(`${name}.png`);
  if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: file, fullPage: true });
}

const ME = 'aaaaaaaa-0000-0000-0000-000000000001';
const base = {
  phoneNumber: '', status: 'Active', isActive: true, isLocked: false, mustChangePassword: false, mfaEnabled: false,
  roles: ['Employee'], accessMode: 'FullPortal', employeeId: 42, employeeName: 'x', employeeCode: 'EMP-1',
  createdAtUtc: '2026-10-01T09:00:00Z', lastLoginAtUtc: null,
};
const amy = { ...base, id: '22222222-2222-2222-2222-222222222222', email: 'amy.baker@kkdemo.com', fullName: 'Amy Baker', employeeName: 'Amy Baker' };
const pending = { ...base, id: '33333333-3333-3333-3333-333333333333', email: 'ahmed@kkdemo.com', fullName: 'Muhammad Ahmed', employeeName: 'Muhammad Ahmed', status: 'PendingPasswordSetup', accessMode: 'NoLogin' };
const self = {
  ...base, id: ME, email: 'admin@kkdemo.com', fullName: 'Tenant Admin', employeeName: 'Tenant Admin', lastLoginAtUtc: '2026-10-26T16:08:00Z',
  roles: ['Admin', 'Auditor', 'Compliance Officer', 'Employee', 'Finance Approver', 'HR Director', 'Payroll Manager'],
};

type Reply = { status: number; body?: unknown };
interface Call { method: string; path: string; body: any }

async function mockAdmin(page: Page, opts: { link?: Reply; temporary?: Reply } = {}) {
  const calls: Call[] = [];
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
  });
  const permissions = ['users.manage', 'roles.manage', 'security.manage'];
  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    let body: any = null;
    try { body = method === 'GET' ? null : request.postDataJSON(); } catch { body = null; }
    calls.push({ method, path: p, body });

    if (/^\/api\/access\/users\/[^/]+\/password-reset-link$/.test(p) && method === 'POST') {
      const r = opts.link ?? { status: 200, body: {
        userId: amy.id, email: amy.email, expiresAtUtc: '2026-10-26T17:00:00Z', emailDeliveryConfigured: false, emailSent: false,
        resetUrl: 'https://app.kynexone.test/reset-password?workspace=kkdemo#token=abc',
        message: 'No email delivery is configured for this workspace, so nothing was sent. Copy the link below and give it to the user directly — it can be used once and expires in 1 hour.',
      } };
      return json(r.body ?? {}, r.status);
    }
    if (/^\/api\/access\/users\/[^/]+\/admin-reset-password$/.test(p) && method === 'POST') {
      const r = opts.temporary ?? { status: 200, body: { userId: amy.id, mustChangePassword: true, sessionsRevoked: true, message: 'Temporary password set.' } };
      return json(r.body ?? {}, r.status);
    }
    if (method !== 'GET') return json({}); // Never touch a real account.
    if (p === '/api/auth/me') return json({ id: ME, tenantId: 't1', tenantSlug: 'kkdemo', email: 'admin@kkdemo.com', fullName: 'Tenant Admin', roles: ['Admin'], accountType: 'SingleCompany', isGroupScope: true, companies: [], permissions });
    if (p === '/api/access/users') return json({ items: [pending, amy, self], total: 3, page: 1, pageSize: 20 });
    if (p === '/api/access/roles') return json(['Admin', 'HR Manager', 'Employee'].map((name, index) => ({ id: String(index), name, permissions: [] })));
    if (p === '/api/access/ceiling') return json({ userId: ME, isAdmin: true, heldPermissions: permissions, roles: [] });
    if (p === '/api/tenant-admin/usage') return json({ activeUsers: 3, maxUsers: 20 });
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', currencyCode: 'SAR', countryCode: 'SA' });
    if (p.startsWith('/api/tenant-admin/')) return json({ message: 'Not found' }, 404);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications'
      || p === '/api/access/permissions' || p.includes('help-text')) return json([]);
    return json({ items: [], total: 0 });
  });
  return { calls, errors };
}

const rowOf = (page: Page, name: string) => page.locator('tbody tr').filter({ hasText: name });
const dialog = (page: Page) => page.getByTestId('reset-password-dialog');

async function openUsers(page: Page) {
  await page.goto('/setup?tab=users');
  await expect(rowOf(page, 'Amy Baker')).toBeVisible({ timeout: 30_000 });
}

test.use({ storageState: { cookies: [], origins: [] }, timezoneId: 'Asia/Riyadh' });

test.describe('Setup → Users: the list reads in words', () => {
  test('statuses and access modes in words, two roles then "+N", "Never" for no sign-in', async ({ page }, info) => {
    const api = await mockAdmin(page);
    await openUsers(page);
    await expect(rowOf(page, 'Muhammad Ahmed')).toContainText('Password not set yet');
    await expect(rowOf(page, 'Muhammad Ahmed')).toContainText('No login');
    await expect(rowOf(page, 'Amy Baker')).toContainText('Full portal');
    await expect(rowOf(page, 'Amy Baker')).toContainText('Never');
    await expect(rowOf(page, 'Tenant Admin')).toContainText('+5');
    await expect(rowOf(page, 'Tenant Admin')).not.toContainText('Payroll Manager');
    // Your own password changes through Change password, never from this list.
    await expect(rowOf(page, 'Tenant Admin').getByTestId('user-reset-password')).toHaveCount(0);
    await expect(rowOf(page, 'Amy Baker').getByTestId('user-reset-password')).toHaveCount(1);
    await shot(page, 'users-list-en', info.outputPath.bind(info));
    expect(api.errors).toEqual([]);
  });
});

test.describe('Setup → Users: reset another user\'s password', () => {
  test('a reset link is the default, and a link that was not emailed is shown once to copy', async ({ page }, info) => {
    const api = await mockAdmin(page);
    await openUsers(page);
    await rowOf(page, 'Amy Baker').getByTestId('user-reset-password').click();
    const d = dialog(page);
    await expect(d).toBeVisible();
    await expect(d.getByTestId('reset-password-method-link')).toBeChecked();
    await shot(page, 'reset-dialog-link-en', info.outputPath.bind(info));
    await page.getByTestId('reset-password-confirm').click();
    await expect(d.getByTestId('reset-password-link-result')).toContainText('No email delivery is configured');
    await expect(d.getByTestId('reset-password-link-result').getByRole('textbox')).toHaveValue(/reset-password/);
    expect(api.calls.filter((c) => c.path.endsWith('/password-reset-link'))).toHaveLength(1);
    expect(api.calls.some((c) => c.path.endsWith('/admin-reset-password'))).toBe(false);
  });

  test('a temporary password: generated, needs a reason, sent with it, then shown once', async ({ page }, info) => {
    const api = await mockAdmin(page);
    await openUsers(page);
    await rowOf(page, 'Amy Baker').getByTestId('user-reset-password').click();
    const d = dialog(page);
    await d.getByTestId('reset-password-method-temporary').check();
    const box = d.getByTestId('reset-password-temporary');
    const generated = await box.inputValue();
    expect(generated).toHaveLength(16);
    expect(generated).toMatch(/[A-Z]/);
    expect(generated).toMatch(/[a-z]/);
    expect(generated).toMatch(/[0-9]/);
    expect(generated).toMatch(/[^A-Za-z0-9]/);
    await shot(page, 'reset-dialog-temporary-en', info.outputPath.bind(info));

    // No reason, nothing sent.
    await page.getByTestId('reset-password-confirm').click();
    await expect(d.getByTestId('reset-password-error')).toHaveText('Enter a reason of 3 to 500 characters.');
    expect(api.calls.some((c) => c.path.endsWith('/admin-reset-password'))).toBe(false);

    await d.getByTestId('reset-password-reason').fill('Locked out, confirmed in person');
    await page.getByTestId('reset-password-confirm').click();
    await expect(d.getByTestId('reset-password-temporary-result')).toContainText('Temporary password set for Amy Baker');
    await expect(d.getByTestId('reset-password-temporary-result').getByRole('textbox')).toHaveValue(generated);
    const sent = api.calls.find((c) => c.path.endsWith('/admin-reset-password'))!;
    expect(sent.path).toBe(`/api/access/users/${amy.id}/admin-reset-password`);
    expect(sent.body).toEqual({ newPassword: generated, reason: 'Locked out, confirmed in person' });
    await shot(page, 'reset-dialog-temporary-done-en', info.outputPath.bind(info));
  });

  test('the server\'s refusal is said as it is', async ({ page }) => {
    await mockAdmin(page, { temporary: { status: 400, body: { message: 'This person has not finished setting up their login yet. Resend the invitation instead.' } } });
    await openUsers(page);
    await rowOf(page, 'Amy Baker').getByTestId('user-reset-password').click();
    const d = dialog(page);
    await d.getByTestId('reset-password-method-temporary').check();
    await d.getByTestId('reset-password-reason').fill('Asked in person');
    await page.getByTestId('reset-password-confirm').click();
    await expect(d.getByTestId('reset-password-error')).toHaveText('This person has not finished setting up their login yet. Resend the invitation instead.');
  });
});

test.describe('/login after a temporary password', () => {
  const EMAIL = 'amy.baker@kkdemo.com';
  const session = {
    accessToken: 'fixture-access', refreshToken: 'fixture-refresh', expiresAtUtc: '2026-10-28T00:00:00Z',
    user: { id: amy.id, tenantId: 't1', tenantSlug: 'kkdemo', email: EMAIL, fullName: 'Amy Baker', roles: ['Employee'],
      permissions: ['dashboard.read', 'profile.read', 'ess.read'], employeeId: 42, accessMode: 'FullPortal' },
  };

  test('asks for their own password, saves it with the token, then signs in with it', async ({ page }, info) => {
    const calls: Call[] = [];
    let logins = 0;
    await page.route('**/api/**', async (route: Route) => {
      const request = route.request();
      const url = new URL(request.url());
      const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
      const body = request.method() === 'GET' ? null : request.postDataJSON();
      calls.push({ method: request.method(), path: url.pathname, body });
      if (url.pathname === '/api/auth/login') {
        logins += 1;
        return json(logins === 1
          ? { passwordChangeRequired: true, resetToken: 'change-token', expiresInSeconds: 900, tenantSlug: 'kkdemo', message: 'x' }
          : session);
      }
      if (url.pathname === '/api/auth/reset-password') return route.fulfill({ status: 204, body: '' });
      if (url.pathname === '/api/auth/me') return json(session.user);
      return json(request.method() === 'GET' ? [] : {});
    });

    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill('Temporary-Pass1!');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    const step = page.getByTestId('login-password-change');
    await expect(step).toBeVisible();
    await expect(step).toContainText('Choose your own password');
    await shot(page, 'login-choose-own-password-en', info.outputPath.bind(info));

    await page.getByTestId('login-new-password').fill('MyOwnPassword9!');
    await page.getByTestId('login-confirm-password').fill('MyOwnPassword9?');
    await page.getByRole('button', { name: 'Save and sign in' }).click();
    await expect(page.locator('.lx-fault')).toContainText('The two passwords do not match.');
    expect(calls.some((c) => c.path === '/api/auth/reset-password')).toBe(false);

    await page.getByTestId('login-confirm-password').fill('MyOwnPassword9!');
    await page.getByRole('button', { name: 'Save and sign in' }).click();
    await page.waitForURL((u) => !u.pathname.startsWith('/login'));
    expect(calls.find((c) => c.path === '/api/auth/reset-password')!.body)
      .toEqual({ resetToken: 'change-token', newPassword: 'MyOwnPassword9!', tenantSlug: 'kkdemo' });
    const loginBodies = calls.filter((c) => c.path === '/api/auth/login').map((c) => c.body.password);
    expect(loginBodies).toEqual(['Temporary-Pass1!', 'MyOwnPassword9!']);
  });
});
