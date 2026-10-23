import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * Two-step sign-in, administered (mfa2-contract): an administrator resets another user's two-step
 * sign-in from User Management, and sets the company's policy in Tenant Admin → Security.
 *
 * Over mocked API routes shaped as the contract: the user list's `mfaEnabled`, POST
 * /api/access/users/{id}/mfa/reset { reason } → 204 with its refusals (400 ERR_MFA_RESET_REASON,
 * 403 ERR_MFA_RESET_SELF or a privilege-ceiling code, 404, 409 ERR_MFA_NOT_ENABLED), and the
 * security settings' `mfaRequired` / `mfaTrustedDeviceDays`. Who may reset whom is the server's
 * rule (backend tests); these prove the screens: who is offered the action, what is sent, and what
 * each refusal says.
 *
 *   npx playwright test -c e2e/playwright.fixture.config.ts e2e/two-step-admin.spec.ts
 *
 * Screenshots go to SIGN_IN_SECURITY_SHOTS when it is set (a review folder), else the test output.
 */

const SHOTS = process.env.SIGN_IN_SECURITY_SHOTS;
async function shot(page: Page, name: string, outputPath: (n: string) => string) {
  const file = SHOTS ? path.join(SHOTS, `${name}.png`) : outputPath(`${name}.png`);
  if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: file, fullPage: true });
}

const ME = 'aaaaaaaa-0000-0000-0000-000000000001';
const base = {
  phoneNumber: '', status: 'Active', isActive: true, isLocked: false, mustChangePassword: false,
  roles: ['Employee'], accessMode: 'FullPortal', employeeId: 42, employeeName: 'x', employeeCode: 'EMP-1', createdAtUtc: '2026-10-01T09:00:00Z',
};
const noah = { ...base, id: '11111111-1111-1111-1111-111111111111', email: 'noah.williams@kkdemo.com', fullName: 'Noah Williams', employeeName: 'Noah Williams', mfaEnabled: true };
const layla = { ...base, id: '44444444-4444-4444-4444-444444444444', email: 'l.haddad@kkdemo.com', fullName: 'Layla Haddad', employeeName: 'Layla Haddad', mfaEnabled: false };
// The administrator's own row: two-step on, and still never offered a reset of their own.
const self = { ...base, id: ME, email: 'admin@kkdemo.com', fullName: 'Tenant Admin', employeeName: 'Tenant Admin', roles: ['Admin'], mfaEnabled: true };

type Reply = { status: number; body?: unknown };
interface Call { method: string; path: string; body: any }

interface World {
  permissions?: string[];
  reset?: Reply[];
  locale?: 'en' | 'ar';
  theme?: 'light' | 'dark';
  security?: Record<string, unknown>;
}

const SECURITY = {
  passwordMinLength: 12, passwordRequireUppercase: true, passwordRequireLowercase: true, passwordRequireDigit: true,
  passwordRequireSpecial: true, sessionTimeoutMinutes: 60, maxFailedLoginAttempts: 5, lockoutDurationMinutes: 15,
  mfaRequired: false, mfaTrustedDeviceDays: 30,
};

async function mockApi(page: Page, world: World = {}) {
  const calls: Call[] = [];
  const errors: string[] = [];
  let users = [noah, layla, self];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(({ locale, theme }) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', theme ?? 'light');
    if (locale) localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: world.locale, theme: world.theme });
  const next = (list: Reply[] | undefined, fallback: Reply) => (!list?.length ? fallback : list.length > 1 ? list.shift()! : list[0]);
  const permissions = world.permissions ?? ['users.manage', 'roles.manage', 'security.manage'];
  let security = { ...SECURITY, ...(world.security ?? {}) };

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    let body: any = null;
    try { body = method === 'GET' ? null : request.postDataJSON(); } catch { body = null; }
    calls.push({ method, path: p, body });

    const reset = p.match(/^\/api\/access\/users\/([^/]+)\/mfa\/reset$/);
    if (reset && method === 'POST') {
      const r = next(world.reset, { status: 204 });
      if (r.status === 204) {
        users = users.map((u) => (u.id === decodeURIComponent(reset[1]) ? { ...u, mfaEnabled: false } : u));
        return route.fulfill({ status: 204, body: '' });
      }
      return json(r.body ?? {}, r.status);
    }
    if (p === '/api/access/security-settings' && method === 'PUT') { security = { ...security, ...body }; return json(security); }
    if (method !== 'GET') return json({}); // Never touch a real account.

    if (p === '/api/auth/me') return json({ id: ME, tenantId: 't1', tenantSlug: 'kkdemo', email: 'admin@kkdemo.com', fullName: 'Tenant Admin', roles: ['Admin'], accountType: 'SingleCompany', isGroupScope: true, companies: [], permissions });
    if (p === '/api/access/users') return json({ items: users, total: users.length, page: 1, pageSize: 20 });
    if (p === '/api/access/roles') return json(['Admin', 'HR Manager', 'Employee'].map((name, index) => ({ id: String(index), name, permissions: [] })));
    if (p === '/api/access/ceiling') return json({ userId: ME, isAdmin: true, heldPermissions: permissions, roles: [] });
    if (p === '/api/access/security-settings') return json(security);
    if (p === '/api/tenant-admin/usage') return json({ activeUsers: 3, maxUsers: 20 });
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', currencyCode: 'SAR', countryCode: 'SA' });
    // Tenant Admin's other tabs (subscription, invoices, AI usage…): none on file. The page catches these.
    if (p.startsWith('/api/tenant-admin/')) return json({ message: 'Not found' }, 404);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications'
      || p === '/api/access/permissions' || p.includes('help-text')) return json([]);
    return json({ items: [], total: 0 });
  });
  return { calls, errors };
}

const rowOf = (page: Page, name: string) => page.locator('tbody tr').filter({ hasText: name });
const resetButton = (page: Page, name: string) => rowOf(page, name).getByRole('button', { name: 'Reset two-step sign-in' });
const dialog = (page: Page) => page.getByRole('dialog', { name: 'Reset two-step sign-in?' });

async function openUsers(page: Page) {
  await page.goto('/user-management');
  await expect(rowOf(page, 'Noah Williams')).toBeVisible({ timeout: 30_000 });
}

async function expectNoHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
}

test.use({ storageState: { cookies: [], origins: [] }, timezoneId: 'Asia/Riyadh' });

test.describe('User Management: reset another user\'s two-step sign-in', () => {
  test('offered for someone with two-step on; never for yourself or someone without it', async ({ page }, info) => {
    await mockApi(page);
    await openUsers(page);
    await expect(resetButton(page, 'Noah Williams')).toBeVisible();
    await expect(rowOf(page, 'Noah Williams').getByTestId('user-two-step-on')).toHaveText('Two-step sign-in is on');
    await expect(resetButton(page, 'Layla Haddad')).toHaveCount(0);
    await expect(rowOf(page, 'Layla Haddad').getByTestId('user-two-step-on')).toHaveCount(0);
    // Your own row says two-step is on, but has no reset: the subject never acts on themselves.
    await expect(rowOf(page, 'admin@kkdemo.com').getByTestId('user-two-step-on')).toBeVisible();
    await expect(rowOf(page, 'admin@kkdemo.com').getByRole('button', { name: 'Reset two-step sign-in' })).toHaveCount(0);
    await shot(page, 'admin-users-list-en', info.outputPath.bind(info));
  });

  test('not offered to a viewer who cannot manage user accounts', async ({ page }) => {
    await mockApi(page, { permissions: ['users.manage', 'roles.manage'] });
    await openUsers(page);
    await expect(page.getByRole('button', { name: 'Reset two-step sign-in' })).toHaveCount(0);
  });

  test('the dialog: plain words, a required reason, focus kept inside, Escape closes', async ({ page }, info) => {
    const api = await mockApi(page);
    await openUsers(page);
    await resetButton(page, 'Noah Williams').click();
    const d = dialog(page);
    await expect(d).toBeVisible();
    await expect(d.getByTestId('reset-two-step-explainer')).toHaveText('Noah Williams will be signed out everywhere and set up two-step sign-in again the next time they sign in. Their trusted browsers and backup codes stop working.');
    const reason = d.getByRole('textbox', { name: /Reason/ });
    await expect(reason).toBeFocused();
    await expect(reason).toHaveAttribute('aria-required', 'true');
    const confirm = d.getByRole('button', { name: 'Reset two-step sign-in' });
    // The shared destructive button: white on rose-700.
    await expect(confirm).toHaveClass(/btn-danger/);
    await expect(confirm).toHaveCSS('background-color', 'rgb(190, 18, 60)');
    await shot(page, 'admin-reset-dialog-en', info.outputPath.bind(info));

    // Required: nothing is sent without a reason of 3 or more characters.
    await confirm.click();
    await expect(d.getByRole('alert')).toHaveText('Enter a reason of 3 to 500 characters.');
    await expect(reason).toHaveAttribute('aria-invalid', 'true');
    await expect(reason).toBeFocused();
    await reason.fill('  ab ');
    await confirm.click();
    await expect(d.getByRole('alert')).toHaveText('Enter a reason of 3 to 500 characters.');
    expect(api.calls.some((c) => c.path.endsWith('/mfa/reset'))).toBe(false);

    // Focus is trapped: Tab and Shift+Tab never leave the dialog.
    for (let i = 0; i < 6; i++) {
      await page.keyboard.press('Tab');
      expect(await d.evaluate((el) => el.contains(document.activeElement))).toBe(true);
    }
    for (let i = 0; i < 6; i++) {
      await page.keyboard.press('Shift+Tab');
      expect(await d.evaluate((el) => el.contains(document.activeElement))).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(d).toHaveCount(0);
    await expect(resetButton(page, 'Noah Williams')).toBeFocused();
    expect(api.calls.some((c) => c.path.endsWith('/mfa/reset'))).toBe(false);
    expect(api.errors).toEqual([]);
  });

  test('a reason, then the reset: the exact request, a toast with the name, and the row updates', async ({ page }) => {
    const api = await mockApi(page);
    await openUsers(page);
    await resetButton(page, 'Noah Williams').click();
    await dialog(page).getByRole('textbox', { name: /Reason/ }).fill('  Lost his phone on a site visit ');
    await dialog(page).getByRole('button', { name: 'Reset two-step sign-in' }).click();
    await expect(dialog(page)).toHaveCount(0);
    await expect(page.getByText('Two-step sign-in reset for Noah Williams')).toBeVisible();
    const sent = api.calls.filter((c) => c.path.endsWith('/mfa/reset'));
    expect(sent).toEqual([{ method: 'POST', path: `/api/access/users/${noah.id}/mfa/reset`, body: { reason: 'Lost his phone on a site visit' } }]);
    // The list is read again: Noah no longer has two-step, so the action is gone.
    await expect(resetButton(page, 'Noah Williams')).toHaveCount(0);
    await expect(rowOf(page, 'Noah Williams').getByTestId('user-two-step-on')).toHaveCount(0);
  });

  test('each refusal has its own plain message: reason, self, ceiling, not found, already off', async ({ page }) => {
    await mockApi(page, {
      reset: [
        { status: 400, body: { code: 'ERR_MFA_RESET_REASON', message: 'Reason must be 3-500 characters.' } },
        { status: 403, body: { code: 'ERR_MFA_RESET_SELF', message: 'You cannot reset your own two-step sign-in.' } },
        { status: 403, body: { code: 'access_admin_target', error: 'access_admin_target', message: 'Only an Admin can change another Admin.', messageAr: 'لا يغيّر حساب مسؤول إلا مسؤول آخر.' } },
        { status: 403, body: { message: 'Forbidden' } },
        { status: 500, body: {} },
        { status: 404, body: {} },
      ],
    });
    await openUsers(page);
    await resetButton(page, 'Noah Williams').click();
    const d = dialog(page);
    const reason = d.getByRole('textbox', { name: /Reason/ });
    const confirm = d.getByRole('button', { name: 'Reset two-step sign-in' });
    await reason.fill('Lost phone');
    await confirm.click();
    await expect(d.getByRole('alert')).toHaveText('Enter a reason of 3 to 500 characters.');
    await confirm.click();
    await expect(d.getByTestId('reset-two-step-error')).toHaveText("You can't reset your own two-step sign-in. Ask another administrator to do it.");
    await confirm.click();
    await expect(d.getByTestId('reset-two-step-error')).toHaveText('Only an Admin can change another Admin.');
    await confirm.click();
    await expect(d.getByTestId('reset-two-step-error')).toHaveText("You don't have permission to reset two-step sign-in for Noah Williams.");
    await confirm.click();
    await expect(d.getByTestId('reset-two-step-error')).toHaveText('Two-step sign-in could not be reset. Try again in a moment.');
    // 404: nothing left to reset here; only Close remains, and the list is read again.
    await confirm.click();
    await expect(d.getByTestId('reset-two-step-error')).toHaveText("Noah Williams is no longer one of your company's users. The list has been refreshed.");
    await expect(d.getByRole('button', { name: 'Reset two-step sign-in' })).toHaveCount(0);
    await d.getByTestId('reset-two-step-cancel').click();
    await expect(d).toHaveCount(0);
  });

  test('409: two-step was already off, nothing changed', async ({ page }) => {
    await mockApi(page, { reset: [{ status: 409, body: { code: 'ERR_MFA_NOT_ENABLED', message: 'Not enabled.' } }] });
    await openUsers(page);
    await resetButton(page, 'Noah Williams').click();
    await dialog(page).getByRole('textbox', { name: /Reason/ }).fill('Lost phone');
    await dialog(page).getByRole('button', { name: 'Reset two-step sign-in' }).click();
    await expect(dialog(page).getByTestId('reset-two-step-error')).toHaveText('Two-step sign-in is already off for Noah Williams. Nothing was changed.');
    await expect(dialog(page).getByRole('button', { name: 'Close', exact: true }).last()).toBeVisible();
  });

  test('a network failure says so', async ({ page }) => {
    await mockApi(page);
    await openUsers(page);
    await page.route('**/api/access/users/*/mfa/reset', (route) => route.abort('internetdisconnected'));
    await resetButton(page, 'Noah Williams').click();
    await dialog(page).getByRole('textbox', { name: /Reason/ }).fill('Lost phone');
    await dialog(page).getByRole('button', { name: 'Reset two-step sign-in' }).click();
    await expect(dialog(page).getByTestId('reset-two-step-error')).toHaveText('Cannot reach the server. Check your connection and try again.');
  });

  test('Arabic: the dialog reads right to left in Arabic', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar' });
    await page.goto('/user-management');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    const button = rowOf(page, 'Noah Williams').getByRole('button', { name: 'إعادة تعيين التحقق بخطوتين' });
    await button.click({ timeout: 30_000 });
    const d = page.getByRole('dialog', { name: 'إعادة تعيين التحقق بخطوتين؟' });
    await expect(d.getByTestId('reset-two-step-explainer')).toHaveText('سيُسجَّل خروج Noah Williams من كل الأجهزة، وسيُعدّ التحقق بخطوتين من جديد عند تسجيل الدخول التالي. وستتوقف متصفحاته الموثوقة ورموزه الاحتياطية عن العمل.');
    await expect(d.getByRole('textbox', { name: /السبب/ })).toBeFocused();
    await d.getByRole('button', { name: 'إعادة تعيين التحقق بخطوتين' }).click();
    await expect(d.getByRole('alert')).toHaveText('أدخل سببًا يتراوح بين 3 و500 حرف.');
    await shot(page, 'admin-reset-dialog-ar', info.outputPath.bind(info));
  });

  test('dark and 390px: the dialog fits', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { theme: 'dark' });
    await openUsers(page);
    await resetButton(page, 'Noah Williams').click();
    await expect(dialog(page)).toBeVisible();
    await expect(page.locator('html')).toHaveClass(/dark/);
    const box = (await dialog(page).boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(390);
    await expectNoHorizontalScroll(page);
    await shot(page, 'admin-reset-dialog-dark-390', info.outputPath.bind(info));
  });
});

test.describe('Tenant Admin → Security: the two-step sign-in group', () => {
  async function openSecurity(page: Page) {
    await page.goto('/tenant-admin');
    await page.getByRole('button', { name: 'Security', exact: true }).click({ timeout: 30_000 });
    await expect(page.getByTestId('two-step-policy')).toBeVisible();
  }

  test('saves mfaRequired and mfaTrustedDeviceDays with the rest of the form', async ({ page }, info) => {
    const api = await mockApi(page);
    await openSecurity(page);
    const group = page.getByTestId('two-step-policy');
    await expect(group.getByRole('heading', { name: 'Two-step sign-in' })).toBeVisible();
    // A fact, not a switch: privileged roles always need it.
    await expect(group.getByTestId('two-step-always')).toHaveText(/People who can see or change payroll, banking or access\s*Always required/);
    await expect(group.getByTestId('two-step-always').getByRole('switch')).toHaveCount(0);
    const everyone = group.getByRole('switch', { name: 'Also require it for everyone else' });
    await expect(everyone).toHaveAttribute('aria-checked', 'false');
    const days = group.getByLabel('Trusted browsers');
    await expect(days).toHaveValue('30');
    await expect(days.locator('option')).toHaveText(['Off', '7 days', '14 days', '30 days (default)', '90 days']);
    await expect(group.getByText('Shorter is safer. Shortening it also shortens browsers already trusted.')).toBeVisible();
    await shot(page, 'tenant-admin-two-step-en', info.outputPath.bind(info));

    await everyone.click();
    await expect(everyone).toHaveAttribute('aria-checked', 'true');
    await days.selectOption('14');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.find((c) => c.method === 'PUT' && c.path === '/api/access/security-settings')?.body)
      .toMatchObject({ mfaRequired: true, mfaTrustedDeviceDays: 14 });

    // Off is 0 days, and turning the company rule off again is sent as false.
    await everyone.click();
    await days.selectOption('0');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.filter((c) => c.method === 'PUT' && c.path === '/api/access/security-settings').at(-1)?.body)
      .toMatchObject({ mfaRequired: false, mfaTrustedDeviceDays: 0 });
    expect(api.errors).toEqual([]);
  });

  test('the special-character rule is read from and saved to the field the API uses', async ({ page }) => {
    // The form used to send passwordRequireSpecialChar, which the server ignores, so this rule never saved from here.
    const api = await mockApi(page, { security: { passwordRequireSpecial: false } });
    await openSecurity(page);
    const special = page.locator('#sec-special');
    await expect(special).not.toBeChecked();
    await special.click();
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.find((c) => c.method === 'PUT' && c.path === '/api/access/security-settings')?.body)
      .toMatchObject({ passwordRequireSpecial: true });
    const sent = api.calls.find((c) => c.method === 'PUT' && c.path === '/api/access/security-settings')?.body as Record<string, unknown>;
    expect(sent).not.toHaveProperty('passwordRequireSpecialChar');
  });

  test('a stored lifetime outside the choices is shown, not silently changed', async ({ page }) => {
    const api = await mockApi(page, { security: { mfaTrustedDeviceDays: 45, mfaRequired: true } });
    await openSecurity(page);
    const days = page.getByTestId('two-step-policy').getByLabel('Trusted browsers');
    await expect(days).toHaveValue('45');
    await expect(days.locator('option:checked')).toHaveText('45 days (current)');
    await expect(page.getByRole('switch', { name: 'Also require it for everyone else' })).toHaveAttribute('aria-checked', 'true');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ mfaRequired: true, mfaTrustedDeviceDays: 45 });
  });

  test('Arabic: the group is Arabic, with no capitals or tracking', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar' });
    await page.goto('/tenant-admin');
    await page.getByRole('button', { name: 'Security', exact: true }).click({ timeout: 30_000 });
    const group = page.getByTestId('two-step-policy');
    await expect(group.getByRole('heading', { name: 'التحقق بخطوتين' })).toBeVisible();
    await expect(group.getByTestId('two-step-always')).toContainText('مطلوب دائمًا');
    await expect(group.getByRole('switch', { name: 'طلبه أيضًا من جميع الآخرين' })).toBeVisible();
    await expect(group.getByLabel('المتصفحات الموثوقة').locator('option')).toHaveText(['متوقف', '7 أيام', '14 يومًا', '30 يومًا (الافتراضي)', '90 يومًا']);
    await expect(group.getByText('المدة الأقصر أكثر أمانًا. وتقصيرها يُقصّر أيضًا مدة المتصفحات الموثوقة حاليًا.')).toBeVisible();
    await expect(group).not.toContainText('الثنائي');
    await expect(group.getByRole('heading', { name: 'التحقق بخطوتين' })).toHaveCSS('text-transform', 'none');
    await shot(page, 'tenant-admin-two-step-ar', info.outputPath.bind(info));
  });

  test('dark and 390px: the group fits', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { theme: 'dark' });
    await openSecurity(page);
    await page.getByTestId('two-step-policy').scrollIntoViewIfNeeded();
    await expectNoHorizontalScroll(page);
    await shot(page, 'tenant-admin-two-step-dark-390', info.outputPath.bind(info));
  });
});
