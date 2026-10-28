import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * Two-step sign-in, administered (mfa2-contract): an administrator resets another user's two-step
 * sign-in from Setup → Users, and sets the company's policy in Setup → Sign-in security (both were pages of their own:
 * User Management, and Tenant Admin → Security).
 *
 * Over mocked API routes shaped as the contract: the user list's `mfaEnabled`, POST
 * /api/access/users/{id}/mfa/reset { reason } → 204 with its refusals (400 ERR_MFA_RESET_REASON,
 * 403 ERR_MFA_RESET_SELF or a privilege-ceiling code, 404, 409 ERR_MFA_NOT_ENABLED), and the
 * security settings' `mfaRequired` / `mfaTrustedDeviceDays`. Who may reset whom is the server's
 * rule (backend tests); these prove the screens: who is offered the action, what is sent, and what
 * each refusal says.
 *
 * Also the session limits (session-contract C): the relabelled idle timeout and the maximum session
 * length (`sessionMaxHours`) in the same form, and the sign-in notice after a refresh refused with
 * 401 ERR_SESSION_EXPIRED.
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
  /** Replies to PUT /api/access/security-settings, in order (the last one repeats); default: saved. */
  securityPut?: Reply[];
  /** /api/auth/me answers 401, so the API client tries POST /api/auth/refresh, which answers this. */
  refreshRefusal?: Reply;
}

const SECURITY = {
  passwordMinLength: 12, passwordRequireUppercase: true, passwordRequireLowercase: true, passwordRequireDigit: true,
  passwordRequireSpecial: true, sessionTimeoutMinutes: 60, sessionMaxHours: 12, maxFailedLoginAttempts: 5, lockoutDurationMinutes: 15,
  mfaRequired: false, mfaTrustedDeviceDays: 30,
};

async function mockApi(page: Page, world: World = {}) {
  const calls: Call[] = [];
  const errors: string[] = [];
  let users = [noah, layla, self];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(({ locale, theme, seedOnce }) => {
    // A refused refresh clears the tokens and reloads /login: seed them once per tab, so the reload
    // shows what the app left behind instead of a fresh sign-in.
    if (!seedOnce || !sessionStorage.getItem('fixture-seeded')) {
      localStorage.setItem('zayra_access_token', 'fixture-token');
      localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
      if (seedOnce) sessionStorage.setItem('fixture-seeded', '1');
    }
    localStorage.setItem('kynexone.theme', theme ?? 'light');
    if (locale) localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: world.locale, theme: world.theme, seedOnce: Boolean(world.refreshRefusal) });
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
    if (p === '/api/access/security-settings' && method === 'PUT') {
      const r = next(world.securityPut, { status: 200 });
      if (r.status !== 200) return json(r.body ?? {}, r.status);
      security = { ...security, ...body };
      return json(security);
    }
    if (p === '/api/auth/refresh' && method === 'POST' && world.refreshRefusal) return json(world.refreshRefusal.body ?? {}, world.refreshRefusal.status);
    if (method !== 'GET') return json({}); // Never touch a real account.

    if (p === '/api/auth/me' && world.refreshRefusal) return json({ message: 'Unauthorized' }, 401);
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
/** Opens a row's "More actions" menu (portalled to <body>) and returns its "Reset two-step sign-in" item. */
async function resetItem(page: Page, name: string) {
  await rowOf(page, name).getByTestId('user-more-actions').click();
  return page.getByTestId('user-actions-menu').getByTestId('user-reset-two-step');
}
const dialog = (page: Page) => page.getByRole('dialog', { name: 'Reset two-step sign-in?' });

async function openUsers(page: Page) {
  await page.goto('/setup?tab=users');
  await expect(rowOf(page, 'Noah Williams')).toBeVisible({ timeout: 30_000 });
}

async function expectNoHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
}

test.use({ storageState: { cookies: [], origins: [] }, timezoneId: 'Asia/Riyadh' });

test.describe('Setup → Users: reset another user\'s two-step sign-in', () => {
  test('offered for someone with two-step on; never for yourself or someone without it', async ({ page }, info) => {
    await mockApi(page);
    await openUsers(page);
    await expect(await resetItem(page, 'Noah Williams')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(rowOf(page, 'Noah Williams').getByTestId('user-two-step-on')).toHaveText('Two-step sign-in is on');
    await expect(await resetItem(page, 'Layla Haddad')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await expect(rowOf(page, 'Layla Haddad').getByTestId('user-two-step-on')).toHaveCount(0);
    // Your own row says two-step is on, but has no reset: the subject never acts on themselves.
    await expect(rowOf(page, 'admin@kkdemo.com').getByTestId('user-two-step-on')).toBeVisible();
    await expect(await resetItem(page, 'admin@kkdemo.com')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await shot(page, 'admin-users-list-en', info.outputPath.bind(info));
  });

  test('not offered to a viewer who cannot manage user accounts', async ({ page }) => {
    // users.manage and roles.manage do not make the accounts API answer (every /api/access action needs security.manage,
    // AccessController.cs:23), so Setup does not offer Users to them at all, let alone a reset.
    await mockApi(page, { permissions: ['users.manage', 'roles.manage'] });
    await page.goto('/setup?tab=users');
    await expect(page.getByTestId('setup-no-sections').or(page.getByText('Access Denied')).first()).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-module-section="users"]')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Reset two-step sign-in' })).toHaveCount(0);
  });

  test('the dialog: plain words, a required reason, focus kept inside, Escape closes', async ({ page }, info) => {
    const api = await mockApi(page);
    await openUsers(page);
    await (await resetItem(page, 'Noah Williams')).click();
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
    // Focus goes back to the row's "More actions" button, which opened the dialog.
    await expect(rowOf(page, 'Noah Williams').getByTestId('user-more-actions')).toBeFocused();
    expect(api.calls.some((c) => c.path.endsWith('/mfa/reset'))).toBe(false);
    expect(api.errors).toEqual([]);
  });

  test('a reason, then the reset: the exact request, a toast with the name, and the row updates', async ({ page }) => {
    const api = await mockApi(page);
    await openUsers(page);
    await (await resetItem(page, 'Noah Williams')).click();
    await dialog(page).getByRole('textbox', { name: /Reason/ }).fill('  Lost his phone on a site visit ');
    await dialog(page).getByRole('button', { name: 'Reset two-step sign-in' }).click();
    await expect(dialog(page)).toHaveCount(0);
    await expect(page.getByText('Two-step sign-in reset for Noah Williams')).toBeVisible();
    const sent = api.calls.filter((c) => c.path.endsWith('/mfa/reset'));
    expect(sent).toEqual([{ method: 'POST', path: `/api/access/users/${noah.id}/mfa/reset`, body: { reason: 'Lost his phone on a site visit' } }]);
    // The list is read again: Noah no longer has two-step, so the action is gone.
    await expect(await resetItem(page, 'Noah Williams')).toHaveCount(0);
    await page.keyboard.press('Escape');
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
    await (await resetItem(page, 'Noah Williams')).click();
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
    await (await resetItem(page, 'Noah Williams')).click();
    await dialog(page).getByRole('textbox', { name: /Reason/ }).fill('Lost phone');
    await dialog(page).getByRole('button', { name: 'Reset two-step sign-in' }).click();
    await expect(dialog(page).getByTestId('reset-two-step-error')).toHaveText('Two-step sign-in is already off for Noah Williams. Nothing was changed.');
    await expect(dialog(page).getByRole('button', { name: 'Close', exact: true }).last()).toBeVisible();
  });

  test('a network failure says so', async ({ page }) => {
    await mockApi(page);
    await openUsers(page);
    await page.route('**/api/access/users/*/mfa/reset', (route) => route.abort('internetdisconnected'));
    await (await resetItem(page, 'Noah Williams')).click();
    await dialog(page).getByRole('textbox', { name: /Reason/ }).fill('Lost phone');
    await dialog(page).getByRole('button', { name: 'Reset two-step sign-in' }).click();
    await expect(dialog(page).getByTestId('reset-two-step-error')).toHaveText('Cannot reach the server. Check your connection and try again.');
  });

  test('Arabic: the dialog reads right to left in Arabic', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar' });
    await page.goto('/setup?tab=users');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    // The row's "More actions" menu, in Arabic too: the item reads «إعادة تعيين التحقق بخطوتين».
    await rowOf(page, 'Noah Williams').getByTestId('user-more-actions').click({ timeout: 30_000 });
    const item = page.getByTestId('user-actions-menu').getByRole('menuitem', { name: 'إعادة تعيين التحقق بخطوتين' });
    await item.click();
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
    await (await resetItem(page, 'Noah Williams')).click();
    await expect(dialog(page)).toBeVisible();
    await expect(page.locator('html')).toHaveClass(/dark/);
    const box = (await dialog(page).boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(390);
    await expectNoHorizontalScroll(page);
    await shot(page, 'admin-reset-dialog-dark-390', info.outputPath.bind(info));
  });
});

test.describe('Setup → Sign-in security: the two-step sign-in group', () => {
  async function openSecurity(page: Page) {
    await page.goto('/setup?tab=signInSecurity');
    await expect(page.getByTestId('two-step-policy')).toBeVisible();
  }

  test('saves the company policy and mfaTrustedDeviceDays with the rest of the form', async ({ page }, info) => {
    const api = await mockApi(page);
    await openSecurity(page);
    const group = page.getByTestId('two-step-policy');
    await expect(group.getByRole('heading', { name: 'Two-step sign-in' })).toBeVisible();
    // Three options, one-line explanations; the default (no stored policy, mfaRequired false) is the privileged-roles rule.
    const optional = group.getByRole('radio', { name: /Each person chooses/ });
    const privileged = group.getByRole('radio', { name: /Required for people who can see or change payroll, banking or access/ });
    const everyone = group.getByRole('radio', { name: /Required for everyone/ });
    await expect(group.getByRole('radio')).toHaveCount(3);
    await expect(privileged).toBeChecked();
    await expect(group.getByTestId('two-step-optional-note')).toHaveCount(0);
    const days = group.getByLabel('Trusted browsers');
    await expect(days).toHaveValue('30');
    await expect(days.locator('option')).toHaveText(['Off', '7 days', '14 days', '30 days (default)', '90 days']);
    await shot(page, 'tenant-admin-two-step-en', info.outputPath.bind(info));

    await everyone.check();
    await days.selectOption('14');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.find((c) => c.method === 'PUT' && c.path === '/api/access/security-settings')?.body)
      .toMatchObject({ mfaPolicy: 'Everyone', mfaRequired: true, mfaTrustedDeviceDays: 14 });

    await privileged.check();
    await days.selectOption('0');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.filter((c) => c.method === 'PUT' && c.path === '/api/access/security-settings').at(-1)?.body)
      .toMatchObject({ mfaPolicy: 'PrivilegedRoles', mfaRequired: false, mfaTrustedDeviceDays: 0 });

    // "Each person chooses": the amber go-live warning and a required reason, sent with the policy.
    await optional.check();
    const note = group.getByTestId('two-step-optional-note');
    await expect(note).toContainText('Recommended only while testing. A customer going live should require it at least for payroll, banking and access.');
    await group.getByLabel('Reason (required)').fill('Pilot with test data only');
    await shot(page, 'tenant-admin-two-step-optional-en', info.outputPath.bind(info));
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.filter((c) => c.method === 'PUT' && c.path === '/api/access/security-settings').at(-1)?.body)
      .toMatchObject({ mfaPolicy: 'Optional', mfaRequired: false, mfaPolicyReason: 'Pilot with test data only' });
    expect(api.errors).toEqual([]);
  });

  test('a refused reason is explained; a company already on "Each person chooses" is not asked again', async ({ page }) => {
    await mockApi(page, {
      security: { mfaPolicy: 'Optional' },
      securityPut: [{ status: 400, body: { code: 'ERR_MFA_POLICY_REASON', message: 'x' } }],
    });
    await openSecurity(page);
    const group = page.getByTestId('two-step-policy');
    await expect(group.getByRole('radio', { name: /Each person chooses/ })).toBeChecked();
    await expect(group.getByTestId('two-step-optional-note')).toBeVisible();
    await expect(group.getByLabel('Reason (required)')).toHaveCount(0);
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect(page.getByText('Give a reason for letting each person choose two-step sign-in.')).toBeVisible();
  });

  test('starting on "Each person chooses": tightening, saving, then choosing it again asks for a fresh reason', async ({ page }) => {
    const api = await mockApi(page, { security: { mfaPolicy: 'Optional' } });
    await openSecurity(page);
    const group = page.getByTestId('two-step-policy');
    const optional = group.getByRole('radio', { name: /Each person chooses/ });
    const privileged = group.getByRole('radio', { name: /Required for people who can see or change payroll, banking or access/ });
    await expect(group.getByLabel('Reason (required)')).toHaveCount(0);

    await privileged.check();
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.filter((c) => c.method === 'PUT' && c.path === '/api/access/security-settings').at(-1)?.body)
      .toMatchObject({ mfaPolicy: 'PrivilegedRoles' });

    // The saved policy is now the baseline, so going back to "Each person chooses" is a relaxation again.
    await optional.check();
    await group.getByLabel('Reason (required)').fill('Second test round');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.filter((c) => c.method === 'PUT' && c.path === '/api/access/security-settings').at(-1)?.body)
      .toMatchObject({ mfaPolicy: 'Optional', mfaPolicyReason: 'Second test round' });
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
    const api = await mockApi(page, { security: { mfaTrustedDeviceDays: 45, mfaRequired: true, mfaPolicy: 'Everyone' } });
    await openSecurity(page);
    const days = page.getByTestId('two-step-policy').getByLabel('Trusted browsers');
    await expect(days).toHaveValue('45');
    await expect(days.locator('option:checked')).toHaveText('45 days (current)');
    await expect(page.getByRole('radio', { name: /Required for everyone/ })).toBeChecked();
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => api.calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ mfaPolicy: 'Everyone', mfaRequired: true, mfaTrustedDeviceDays: 45 });
  });

  test('Arabic: the group is Arabic, with no capitals or tracking', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar' });
    await page.goto('/setup?tab=signInSecurity');
    const group = page.getByTestId('two-step-policy');
    await expect(group.getByRole('heading', { name: 'التحقق بخطوتين' })).toBeVisible();
    await expect(group.getByRole('radio')).toHaveCount(3);
    await expect(group.getByRole('radio', { name: /كل شخص يختار بنفسه/ })).toBeVisible();
    await expect(group.getByRole('radio', { name: /مطلوب من الجميع/ })).toBeVisible();
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

/*
 * Session limits (session-contract C): the idle timeout, relabelled, and the maximum session length
 * (`sessionMaxHours`, 1–168, 12 by default) in Setup → Sign-in security; and the sign-in page's
 * notice when a refresh is refused with 401 ERR_SESSION_EXPIRED. That the server enforces the limit
 * is the backend tests' job; these prove what the screens show and send.
 */
test.describe('Setup → Sign-in security: sessions and lockout', () => {
  async function openSecurity(page: Page) {
    await page.goto('/setup?tab=signInSecurity');
    await expect(page.getByTestId('session-limits')).toBeVisible();
  }
  const putBodies = (calls: Call[]) => calls.filter((c) => c.method === 'PUT' && c.path === '/api/access/security-settings').map((c) => c.body);

  test('the idle timeout reads "Sign out after inactivity", with what it does', async ({ page }) => {
    const api = await mockApi(page);
    await openSecurity(page);
    const group = page.getByTestId('session-limits');
    await expect(group.getByRole('heading', { name: 'Sessions and lockout' })).toBeVisible();
    const idle = group.getByLabel('Sign out after inactivity (minutes)');
    await expect(idle).toHaveValue('60');
    await expect(idle).toHaveAccessibleDescription('Ends a session nobody has used for this long.');
    await expect(page.getByText(/Session Timeout/i)).toHaveCount(0);
    await idle.fill('45');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => putBodies(api.calls).at(-1)).toMatchObject({ sessionTimeoutMinutes: 45, sessionMaxHours: 12 });
    expect(api.errors).toEqual([]);
  });

  test('"Maximum session length": its choices, 12 by default, and it saves sessionMaxHours', async ({ page }, info) => {
    const api = await mockApi(page);
    await openSecurity(page);
    const group = page.getByTestId('session-limits');
    const max = group.getByLabel('Maximum session length');
    await expect(max).toHaveValue('12');
    await expect(max.locator('option')).toHaveText(['4 hours', '8 hours', '12 hours (default)', '24 hours', '72 hours (3 days)', '168 hours (7 days)']);
    expect(await max.locator('option').evaluateAll((os) => os.map((o) => (o as HTMLOptionElement).value))).toEqual(['4', '8', '12', '24', '72', '168']);
    await expect(max).toHaveAccessibleDescription('Everyone signs in again after this long, even if they are active. Shorter is safer.');
    await shot(page, `session-limits-en-${info.project.name}`, info.outputPath.bind(info));

    await max.selectOption('4');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => putBodies(api.calls).at(-1)).toMatchObject({ sessionMaxHours: 4, sessionTimeoutMinutes: 60 });
    await expect(page.getByText('Saved!')).toBeVisible();
    await max.selectOption('168');
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => putBodies(api.calls).length).toBe(2);
    expect(putBodies(api.calls)[1]).toMatchObject({ sessionMaxHours: 168 });
    expect(api.errors).toEqual([]);
  });

  test('a stored length outside the choices is shown as "(current)" and kept on save', async ({ page }) => {
    const api = await mockApi(page, { security: { sessionMaxHours: 6 } });
    await openSecurity(page);
    const max = page.getByTestId('session-limits').getByLabel('Maximum session length');
    await expect(max).toHaveValue('6');
    await expect(max.locator('option:checked')).toHaveText('6 hours (current)');
    await expect(max.locator('option')).toHaveText(['4 hours', '6 hours (current)', '8 hours', '12 hours (default)', '24 hours', '72 hours (3 days)', '168 hours (7 days)']);
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect.poll(() => putBodies(api.calls).at(-1)).toMatchObject({ sessionMaxHours: 6 });
  });

  test('one hour, stored, reads "1 hour (current)"', async ({ page }) => {
    await mockApi(page, { security: { sessionMaxHours: 1 } });
    await openSecurity(page);
    await expect(page.getByTestId('session-limits').getByLabel('Maximum session length').locator('option:checked')).toHaveText('1 hour (current)');
  });

  test('the server\'s range refusal is said in plain words', async ({ page }) => {
    await mockApi(page, { securityPut: [{ status: 400, body: { code: 'ERR_SESSION_MAX_HOURS_RANGE', message: 'Session max hours must be 1-168.' } }] });
    await openSecurity(page);
    await page.getByRole('button', { name: 'Save Settings' }).click();
    await expect(page.getByText('Maximum session length must be between 1 and 168 hours.')).toBeVisible();
    await expect(page.getByText('Saved!')).toHaveCount(0);
  });

  test('Arabic: the group is Arabic and right to left, with no capitals or tracking', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar', security: { sessionMaxHours: 6 } });
    await page.goto('/setup?tab=signInSecurity');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    const group = page.getByTestId('session-limits');
    const heading = group.getByRole('heading', { name: 'الجلسات وقفل الحساب' });
    await expect(heading).toBeVisible();
    await expect(heading).toHaveCSS('text-transform', 'none');
    await expect(heading).toHaveCSS('letter-spacing', 'normal');
    const idle = group.getByLabel('تسجيل الخروج بعد عدم النشاط (بالدقائق)');
    await expect(idle).toHaveAccessibleDescription('تنتهي الجلسة التي لم يستخدمها أحد طوال هذه المدة.');
    const max = group.getByLabel('الحد الأقصى لمدة الجلسة');
    await expect(max.locator('option')).toHaveText(['4 ساعات', '6 ساعات (الحالي)', '8 ساعات', '12 ساعة (الافتراضي)', '24 ساعة', '72 ساعة (3 أيام)', '168 ساعة (7 أيام)']);
    await expect(max).toHaveAccessibleDescription('يسجّل الجميع الدخول من جديد بعد هذه المدة، حتى لو كانوا نشطين. المدة الأقصر أكثر أمانًا.');
    await expect(group.getByLabel('الحد الأقصى للمحاولات الفاشلة (1–20)')).toBeVisible();
    await expect(group.getByLabel('مدة قفل الحساب (بالدقائق)')).toBeVisible();
    // Right to left: the first field of the row sits on the right.
    const idleBox = (await idle.boundingBox())!;
    const maxBox = (await max.boundingBox())!;
    if (idleBox.y === maxBox.y) expect(idleBox.x).toBeGreaterThan(maxBox.x);
    await expectNoHorizontalScroll(page);
    await group.scrollIntoViewIfNeeded();
    await shot(page, `session-limits-ar-${info.project.name}`, info.outputPath.bind(info));
  });

  test('dark and 390px: the fields stack and fit', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { theme: 'dark' });
    await openSecurity(page);
    await expect(page.locator('html')).toHaveClass(/dark/);
    const group = page.getByTestId('session-limits');
    await group.scrollIntoViewIfNeeded();
    const idle = (await group.getByLabel('Sign out after inactivity (minutes)').boundingBox())!;
    const max = (await group.getByLabel('Maximum session length').boundingBox())!;
    // One column: the maximum length sits under the idle timeout, and both fit the screen.
    expect(max.y).toBeGreaterThan(idle.y);
    for (const box of [idle, max]) {
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(390);
    }
    await expectNoHorizontalScroll(page);
    await shot(page, 'session-limits-dark-390', info.outputPath.bind(info));
  });
});

test.describe('Sign-in page: a session that reached its maximum length', () => {
  const ended = (page: Page) => page.getByRole('status').filter({ hasText: 'Your session has ended. Sign in again.' });

  test('a refresh refused with 401 ERR_SESSION_EXPIRED lands on sign-in with "Your session has ended. Sign in again."', async ({ page }, info) => {
    const api = await mockApi(page, { refreshRefusal: { status: 401, body: { code: 'ERR_SESSION_EXPIRED', message: 'Your session has ended. Sign in again.' } } });
    await page.goto('/dashboard');
    await page.waitForURL('**/login', { timeout: 30_000 });
    await expect(ended(page)).toBeVisible({ timeout: 30_000 });
    // A notice, not an error (Next's route announcer is the page's only role=alert).
    await expect(page.locator('.lx-fault')).toHaveCount(0);
    expect(api.calls.filter((c) => c.path === '/api/auth/refresh')).toEqual([{ method: 'POST', path: '/api/auth/refresh', body: { refreshToken: 'fixture-refresh' } }]);
    // Signed out for real: the tokens are gone, and the stored sentence was taken by the page now shown.
    expect(await page.evaluate(() => [localStorage.getItem('zayra_access_token'), localStorage.getItem('zayra_refresh_token')])).toEqual([null, null]);
    await expect.poll(() => page.evaluate(() => sessionStorage.getItem('kynexone.sign-in-notice'))).toBeNull();
    await shot(page, `login-session-ended-en-${info.project.name}`, info.outputPath.bind(info));
    // Said once: coming back to sign-in later does not repeat it.
    await page.reload();
    await expect(page.getByLabel(/email/i).first()).toBeVisible({ timeout: 30_000 });
    await expect(ended(page)).toHaveCount(0);
    expect(api.errors).toEqual([]);
  });

  test('any other refused refresh keeps today\'s behaviour: sign-in, signed out, no sentence', async ({ page }) => {
    const api = await mockApi(page, { refreshRefusal: { status: 401, body: { message: 'Invalid or expired refresh token.' } } });
    await page.goto('/dashboard');
    await page.waitForURL('**/login', { timeout: 30_000 });
    await expect(page.getByLabel(/email/i).first()).toBeVisible({ timeout: 30_000 });
    expect(api.calls.filter((c) => c.path === '/api/auth/refresh')).toHaveLength(1);
    expect(await page.evaluate(() => localStorage.getItem('zayra_refresh_token'))).toBeNull();
    expect(await page.evaluate(() => sessionStorage.getItem('kynexone.sign-in-notice'))).toBeNull();
    await expect(page.getByText('Your session has ended. Sign in again.')).toHaveCount(0);
    await expect(page.getByRole('status').filter({ hasText: /signed out everywhere/ })).toHaveCount(0);
  });

  test('Arabic: the notice is Arabic, right to left', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar', refreshRefusal: { status: 401, body: { code: 'ERR_SESSION_EXPIRED', message: 'Your session has ended. Sign in again.' } } });
    await page.goto('/dashboard');
    await page.waitForURL('**/login', { timeout: 30_000 });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('status').filter({ hasText: 'انتهت جلستك. سجّل الدخول مجددًا.' })).toBeVisible({ timeout: 30_000 });
    await expectNoHorizontalScroll(page);
    await shot(page, `login-session-ended-ar-${info.project.name}`, info.outputPath.bind(info));
  });

  test('dark and 390px: the notice fits', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { theme: 'dark', refreshRefusal: { status: 401, body: { code: 'ERR_SESSION_EXPIRED' } } });
    await page.goto('/dashboard');
    await page.waitForURL('**/login', { timeout: 30_000 });
    await expect(ended(page)).toBeVisible({ timeout: 30_000 });
    const box = (await ended(page).boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(390);
    await expectNoHorizontalScroll(page);
    await shot(page, 'login-session-ended-dark-390', info.outputPath.bind(info));
  });
});
