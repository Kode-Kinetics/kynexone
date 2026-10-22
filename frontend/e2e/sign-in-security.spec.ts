import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * Two-step sign-in choice and trusted browsers (scratchpad mfa-contract.md + Amendment 1).
 *
 * Over mocked API routes shaped exactly as that contract: the challenge's `rememberDeviceDays`,
 * `rememberDevice` on challenge/verify, GET /api/auth/mfa/status with `canDisable`,
 * `rememberDeviceDays` and `trustedDeviceCount`, setup / verify-setup / disable with their
 * `{ code }` refusals, and GET / DELETE /api/auth/mfa/trusted-devices. The server's rules (who may
 * turn it off, what a trusted browser skips, revocation) are the backend suite's job; these prove
 * the screens: what is offered, what is sent, and what each refusal says.
 *
 *   npx playwright test -c e2e/playwright.fixture.config.ts e2e/sign-in-security.spec.ts
 *
 * Screenshots go to SIGN_IN_SECURITY_SHOTS when it is set (a review folder), else the test output.
 */

const SHOTS = process.env.SIGN_IN_SECURITY_SHOTS;
async function shot(page: Page, name: string, outputPath: (n: string) => string) {
  const file = SHOTS ? path.join(SHOTS, `${name}.png`) : outputPath(`${name}.png`);
  if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: file, fullPage: true });
}

const EMAIL = 'layla.haddad@evostel.com';
// A real base32 secret shape; the QR is drawn from it in the browser.
const URI = 'otpauth://totp/KynexOne:layla.haddad%40evostel.com?secret=JBSWY3DPEHPK3PXP&issuer=KynexOne';

interface Call { method: string; path: string; body: any }
type Reply = { status: number; body?: unknown };

interface Status {
  enabled: boolean; required: boolean; requiredBecause: string | null; enforceFromUtc: string | null;
  enforced: boolean; promptToEnroll: boolean; canDisable: boolean; rememberDeviceDays: number; trustedDeviceCount: number;
}

interface Device {
  id: string; createdAtUtc: string; lastUsedAtUtc: string | null; expiresAtUtc: string;
  browser: string | null; ipAddress: string | null; current: boolean;
}

const DEVICES: Device[] = [
  { id: 'd-1', createdAtUtc: '2026-10-01T08:00:00Z', lastUsedAtUtc: '2026-10-21T07:30:00Z', expiresAtUtc: '2026-10-31T08:00:00Z', browser: 'Chrome on Windows', ipAddress: '203.0.113.7', current: true },
  { id: 'd-2', createdAtUtc: '2026-09-28T18:00:00Z', lastUsedAtUtc: null, expiresAtUtc: '2026-10-28T18:00:00Z', browser: 'Safari on iPhone', ipAddress: '198.51.100.24', current: false },
];

const status = (over: Partial<Status> = {}): Status => {
  const s = { enabled: false, required: false, requiredBecause: null, enforceFromUtc: null, enforced: false, promptToEnroll: false,
    rememberDeviceDays: 30, trustedDeviceCount: 0, ...over } as Status;
  return { ...s, canDisable: over.canDisable ?? (s.enabled && !s.required) };
};

const user = (privileged: boolean) => ({
  id: 'u-7', tenantId: 't-1', tenantSlug: 'evostel', email: EMAIL, fullName: 'Layla Haddad',
  roles: privileged ? ['Payroll Manager'] : ['Employee'],
  // A payroll login need not hold ess.read: the page must still open for it.
  permissions: privileged ? ['dashboard.read', 'payroll.read', 'payroll.write'] : ['dashboard.read', 'profile.read', 'ess.read', 'ess.write'],
  employeeId: 7, accessMode: 'FullPortal', companies: [{ id: 'c-1', name: 'Evostel', code: 'EVO', countryCode: 'SA', isActive: true }],
});

interface World {
  status: Status;
  devices: Device[];
  privileged?: boolean;
  /** The change (turn on / off) ends this session too: /me answers 401 afterwards. */
  changeEndsSession?: boolean;
  verifySetup?: Reply[];
  disable?: Reply[];
  forgetOne?: Reply[];
  locale?: 'en' | 'ar';
  theme?: 'light' | 'dark';
  signedIn?: boolean;
  login?: Reply[];
}

async function mockApi(page: Page, world: World) {
  const calls: Call[] = [];
  const errors: string[] = [];
  let sessionEnded = false;
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(({ locale, theme, signedIn }) => {
    if (locale) localStorage.setItem('kynexone-locale-choice-v2', locale);
    localStorage.setItem('kynexone.theme', theme ?? 'light');
    // Once per test: a later navigation must not resurrect a session the test signed out of.
    if (signedIn && !sessionStorage.getItem('fixture-booted')) {
      localStorage.setItem('zayra_access_token', 'fixture-access');
      localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
      sessionStorage.setItem('fixture-booted', '1');
    }
  }, { locale: world.locale, theme: world.theme, signedIn: world.signedIn ?? true });

  const next = (list: Reply[] | undefined, fallback: Reply) => (!list?.length ? fallback : list.length > 1 ? list.shift()! : list[0]);

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const json = (body: unknown, s = 200) => route.fulfill({ status: s, contentType: 'application/json', body: JSON.stringify(body ?? {}) });
    const noContent = () => route.fulfill({ status: 204, body: '' });
    let body: any = null;
    try { body = method === 'GET' || method === 'DELETE' ? null : request.postDataJSON(); } catch { body = null; }
    calls.push({ method, path: p, body });

    if (p === '/api/auth/login') { const r = next(world.login, { status: 200, body: {} }); return json(r.body, r.status); }
    if (p === '/api/auth/mfa/challenge/verify') {
      return json({ accessToken: 'fixture-access', refreshToken: 'fixture-refresh', expiresAtUtc: '2026-10-23T00:00:00Z', user: user(!!world.privileged) });
    }
    if (p === '/api/auth/me') return sessionEnded ? json({ message: 'Session ended.' }, 401) : json(user(!!world.privileged));
    if (p === '/api/auth/refresh') return sessionEnded ? json({ message: 'Session ended.' }, 401) : json({ accessToken: 'fixture-access', refreshToken: 'fixture-refresh' });
    if (p === '/api/auth/logout') return noContent();
    if (p === '/api/auth/mfa/status') return json(world.status);
    if (p === '/api/auth/mfa/setup' && method === 'POST') return json({ provisioningUri: URI });
    if (p === '/api/auth/mfa/verify-setup' && method === 'POST') {
      const r = next(world.verifySetup, { status: 204 });
      if (r.status === 204) {
        world.status = status({ ...world.status, enabled: true, promptToEnroll: false, canDisable: !world.status.required });
        if (world.changeEndsSession) sessionEnded = true;
        return noContent();
      }
      return json(r.body, r.status);
    }
    if (p === '/api/auth/mfa/disable' && method === 'POST') {
      const r = next(world.disable, { status: 204 });
      if (r.status === 204) {
        world.status = status({ ...world.status, enabled: false, canDisable: false, trustedDeviceCount: 0 });
        world.devices = [];
        if (world.changeEndsSession) sessionEnded = true;
        return noContent();
      }
      return json(r.body, r.status);
    }
    if (p === '/api/auth/mfa/trusted-devices' && method === 'GET') return json(world.devices);
    if (p === '/api/auth/mfa/trusted-devices' && method === 'DELETE') { world.devices = []; return noContent(); }
    if (p.startsWith('/api/auth/mfa/trusted-devices/') && method === 'DELETE') {
      const id = decodeURIComponent(p.split('/').pop()!);
      const r = next(world.forgetOne, { status: 204 });
      if (r.status !== 204) return json(r.body, r.status);
      if (!world.devices.some((d) => d.id === id)) return json({}, 404);
      world.devices = world.devices.filter((d) => d.id !== id);
      return noContent();
    }
    if (p === '/api/auth/password-policy') return json({ minLength: 10 });
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    // Anything else the shell asks for: empty, never a real record.
    return json(method === 'GET' ? [] : {});
  });
  return { calls, errors, world };
}

const card = (page: Page) => page.getByTestId('sign-in-security-card');
const trusted = (page: Page) => page.getByTestId('trusted-browsers');

async function openSecurity(page: Page) {
  await page.goto('/account/security');
  await expect(card(page).getByTestId('two-step-status')).toBeVisible({ timeout: 30_000 });
}

/** The page fits its viewport: no horizontal scroll at phone width. */
async function expectNoHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
}

test.use({ storageState: { cookies: [], origins: [] }, timezoneId: 'Asia/Riyadh' });

test.describe('sign-in code step: trust this browser', () => {
  const challenge = (days: number) => ({ status: 200, body: { mfaRequired: true, challengeToken: 'chal-1', expiresInSeconds: 300, rememberDeviceDays: days } });

  async function reachCodeStep(page: Page) {
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill('Desert-Falcon-2026');
    await page.locator('#li-pw').press('Enter');
    await expect(page.locator('#mfa-code')).toBeVisible();
  }

  test('offered unchecked, and a tick sends rememberDevice:true', async ({ page }, info) => {
    const api = await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challenge(30)] });
    await reachCodeStep(page);
    const box = page.getByRole('checkbox', { name: 'Trust this browser for 30 days' });
    await expect(box).toBeVisible();
    await expect(box).not.toBeChecked();
    await expect(page.getByText("You won't be asked for a code on this browser until then. Don't use on a shared computer.")).toBeVisible();
    await expect(page.locator('#mfa-code')).toHaveAttribute('autocomplete', 'one-time-code');
    await expect(page.locator('#mfa-code')).toHaveAttribute('inputmode', 'numeric');
    await shot(page, 'login-code-step-en', info.outputPath.bind(info));
    await box.check();
    await page.locator('#mfa-code').fill('123456');
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect.poll(() => api.calls.find((c) => c.path === '/api/auth/mfa/challenge/verify')?.body).toEqual({ challengeToken: 'chal-1', totpCode: '123456', rememberDevice: true });
    await page.waitForURL(/\/(ess|dashboard)/);
    expect(api.errors).toEqual([]);
  });

  test('left unticked, it sends rememberDevice:false', async ({ page }) => {
    const api = await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challenge(14)] });
    await reachCodeStep(page);
    await expect(page.getByRole('checkbox', { name: 'Trust this browser for 14 days' })).not.toBeChecked();
    // Arabic-Indic digits from a phone keyboard are accepted as the code.
    await page.locator('#mfa-code').fill('١٢٣٤٥٦');
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect.poll(() => api.calls.find((c) => c.path === '/api/auth/mfa/challenge/verify')?.body).toEqual({ challengeToken: 'chal-1', totpCode: '123456', rememberDevice: false });
  });

  test('not offered when the company allows 0 days, and nothing extra is sent', async ({ page }) => {
    const api = await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challenge(0)] });
    await reachCodeStep(page);
    await expect(page.getByTestId('mfa-trust-browser')).toHaveCount(0);
    await page.locator('#mfa-code').fill('123456');
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect.poll(() => api.calls.find((c) => c.path === '/api/auth/mfa/challenge/verify')?.body).toEqual({ challengeToken: 'chal-1', totpCode: '123456' });
  });

  test('Arabic: the choice reads right to left with an Arabic plural', async ({ page }, info) => {
    await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challenge(30)], locale: 'ar' });
    await reachCodeStep(page);
    await expect(page.locator('.lx-card')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('checkbox', { name: 'الوثوق بهذا المتصفح لمدة 30 يومًا' })).toBeVisible();
    // The checkbox sits at the inline START, which is the right-hand side in Arabic.
    const boxX = (await page.getByTestId('mfa-trust-browser').boundingBox())!.x;
    const labelX = (await page.locator('label[for="mfa-trust"]').boundingBox())!.x;
    expect(boxX).toBeGreaterThan(labelX);
    await shot(page, 'login-code-step-ar', info.outputPath.bind(info));
  });

  test('phone width: the code step fits', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challenge(30)] });
    await reachCodeStep(page);
    await expect(page.getByRole('checkbox', { name: 'Trust this browser for 30 days' })).toBeVisible();
    await expectNoHorizontalScroll(page);
    await shot(page, 'login-code-step-390', info.outputPath.bind(info));
  });
});

test.describe('Sign-in security: an everyday user turns it on and off', () => {
  test('reachable from the user menu, then turn on with the QR and the key', async ({ page }, info) => {
    const api = await mockApi(page, { status: status(), devices: [] });
    // Any signed-in page; the Self-Service overview needs richer fixtures than this spec's.
    await page.goto('/ess/documents');
    await page.getByRole('button', { name: 'Open user menu' }).click();
    await page.getByTestId('user-menu-sign-in-security').click();
    await page.waitForURL(/\/account\/security$/);
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is off');
    await expect(trusted(page)).toHaveCount(0);
    await shot(page, 'card-off-en', info.outputPath.bind(info));

    await card(page).getByRole('button', { name: 'Turn on two-step sign-in' }).click();
    await expect(page.getByTestId('two-step-qr')).toBeVisible();
    await expect(page.getByTestId('two-step-key')).toHaveText('JBSW Y3DP EHPK 3PXP');
    const code = page.getByLabel('Enter the 6-digit code the app shows');
    await expect(code).toHaveAttribute('autocomplete', 'one-time-code');
    await expect(code).toHaveAttribute('inputmode', 'numeric');
    await expect(page.getByText('Turning it on signs you out everywhere else, and you may need to sign in again here.')).toBeVisible();
    await shot(page, 'card-setup-en', info.outputPath.bind(info));
    await code.fill('654321');
    await page.getByRole('button', { name: 'Turn on', exact: true }).click();
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
    await expect(card(page).getByText('Two-step sign-in is on. You were signed out everywhere else.')).toBeVisible();
    expect(api.calls.find((c) => c.path === '/api/auth/mfa/verify-setup')?.body).toEqual({ tempSecret: 'JBSWY3DPEHPK3PXP', totpCode: '654321' });
    // On now: the trusted browsers appear, empty.
    await expect(page.getByTestId('trusted-browsers-empty')).toHaveText('No trusted browsers. Every sign-in asks for a code.');
    expect(api.errors).toEqual([]);
  });

  test('a wrong code, then an expired setup, then a network failure each say so', async ({ page }) => {
    await mockApi(page, {
      status: status(), devices: [],
      verifySetup: [
        { status: 400, body: { code: 'ERR_MFA_CODE_INVALID', message: 'Invalid code.' } },
        { status: 400, body: { code: 'ERR_MFA_SETUP_INVALID', message: 'Setup expired.' } },
        { status: 204 },
      ],
    });
    await openSecurity(page);
    await card(page).getByRole('button', { name: 'Turn on two-step sign-in' }).click();
    const code = page.getByLabel('Enter the 6-digit code the app shows');
    await code.fill('111111');
    await page.getByRole('button', { name: 'Turn on', exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: "That code didn't match." })).toBeVisible();
    await expect(code).toHaveAttribute('aria-invalid', 'true');
    await code.fill('222222');
    await page.getByRole('button', { name: 'Turn on', exact: true }).click();
    await expect(page.getByText('This setup has expired. Start again to get a new QR code.')).toBeVisible();
    await page.getByRole('button', { name: 'Start again' }).click();
    await expect(page.getByTestId('two-step-qr')).toBeVisible();
    // Network failure: the request never gets an answer.
    await page.route('**/api/auth/mfa/verify-setup', (route) => route.abort('internetdisconnected'));
    await code.fill('333333');
    await page.getByRole('button', { name: 'Turn on', exact: true }).click();
    await expect(page.getByText('Cannot reach the server. Check your connection and try again.')).toBeVisible();
  });

  test('turn off needs the current code and a confirmation', async ({ page }, info) => {
    const api = await mockApi(page, {
      status: status({ enabled: true, trustedDeviceCount: 2 }), devices: [...DEVICES],
      disable: [{ status: 400, body: { code: 'ERR_MFA_CODE_INVALID' } }, { status: 204 }],
    });
    await openSecurity(page);
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
    await card(page).getByRole('button', { name: 'Turn off two-step sign-in' }).click();
    const submit = page.getByRole('button', { name: 'Turn off', exact: true });
    await expect(submit).toBeDisabled();
    await page.getByLabel('Current 6-digit code from your authenticator app').fill('123456');
    await expect(submit).toBeDisabled();
    await page.getByRole('checkbox', { name: /only my password will protect this account/ }).check();
    await shot(page, 'card-disable-en', info.outputPath.bind(info));
    await submit.click();
    await expect(page.getByText("That code didn't match. Enter the newest code from your authenticator app. Each code works only once.")).toBeVisible();
    await page.getByLabel('Current 6-digit code from your authenticator app').fill('234567');
    await submit.click();
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is off');
    expect(api.calls.filter((c) => c.path === '/api/auth/mfa/disable').map((c) => c.body)).toEqual([{ totpCode: '123456' }, { totpCode: '234567' }]);
  });

  test('when the change ends this session too, it says why on the sign-in page', async ({ page }) => {
    await mockApi(page, { status: status(), devices: [], changeEndsSession: true });
    await openSecurity(page);
    await card(page).getByRole('button', { name: 'Turn on two-step sign-in' }).click();
    await page.getByLabel('Enter the 6-digit code the app shows').fill('654321');
    await page.getByRole('button', { name: 'Turn on', exact: true }).click();
    await page.waitForURL(/\/login/);
    await expect(page.locator('.lx-ok')).toHaveText(/Two-step sign-in is on, and you were signed out everywhere for your security\./);
    await expect(page.locator('#li-em')).toBeVisible();
    expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBeNull();
  });

  test('turning off that ends this session also returns to sign-in with a reason', async ({ page }) => {
    await mockApi(page, { status: status({ enabled: true }), devices: [], changeEndsSession: true });
    await openSecurity(page);
    await card(page).getByRole('button', { name: 'Turn off two-step sign-in' }).click();
    await page.getByLabel('Current 6-digit code from your authenticator app').fill('123456');
    await page.getByRole('checkbox', { name: /only my password will protect this account/ }).check();
    await page.getByRole('button', { name: 'Turn off', exact: true }).click();
    await page.waitForURL(/\/login/);
    await expect(page.locator('.lx-ok')).toHaveText(/Two-step sign-in is off, and you were signed out everywhere for your security\./);
  });

  test('a 409 saying it is already off, or that the whole company requires it, is explained', async ({ page }) => {
    await mockApi(page, {
      status: status({ enabled: true }), devices: [],
      disable: [{ status: 409, body: { code: 'ERR_MFA_REQUIRED_BY_WORKSPACE', message: 'Your company requires two-step sign-in for everyone.' } }],
    });
    await openSecurity(page);
    await card(page).getByRole('button', { name: 'Turn off two-step sign-in' }).click();
    await page.getByLabel('Current 6-digit code from your authenticator app').fill('123456');
    await page.getByRole('checkbox', { name: /only my password will protect this account/ }).check();
    await page.getByRole('button', { name: 'Turn off', exact: true }).click();
    await expect(page.getByTestId('two-step-required-reason')).toHaveText('Your company requires two-step sign-in for everyone.');
    await expect(card(page).getByRole('button', { name: 'Turn off two-step sign-in' })).toHaveCount(0);
  });
});

test.describe('Sign-in security: a privileged user', () => {
  test('cannot turn it off, and is told why instead of seeing a disabled button', async ({ page }, info) => {
    await mockApi(page, {
      privileged: true,
      status: status({ enabled: true, required: true, requiredBecause: 'privileged_role', trustedDeviceCount: 2 }),
      devices: [...DEVICES],
    });
    await openSecurity(page);
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
    await expect(page.getByTestId('two-step-required-reason')).toHaveText('Your role can see or change payroll, banking or access, so your company requires two-step sign-in.');
    await expect(card(page).getByRole('button', { name: /Turn off/ })).toHaveCount(0);
    await shot(page, 'card-privileged-en', info.outputPath.bind(info));
  });

  test('a 409 ERR_MFA_REQUIRED_FOR_ROLE from a stale status shows the same reason', async ({ page }) => {
    // The status still said "may turn off" (the role changed since the page loaded).
    await mockApi(page, {
      privileged: true,
      status: status({ enabled: true }), devices: [],
      disable: [{ status: 409, body: { code: 'ERR_MFA_REQUIRED_FOR_ROLE', message: 'Your role can see or change payroll, banking or access, so your company requires two-step sign-in.' } }],
    });
    await openSecurity(page);
    await card(page).getByRole('button', { name: 'Turn off two-step sign-in' }).click();
    await page.getByLabel('Current 6-digit code from your authenticator app').fill('123456');
    await page.getByRole('checkbox', { name: /only my password will protect this account/ }).check();
    await page.getByRole('button', { name: 'Turn off', exact: true }).click();
    await expect(page.getByTestId('two-step-required-reason')).toHaveText('Your role can see or change payroll, banking or access, so your company requires two-step sign-in.');
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
  });
});

test.describe('trusted browsers', () => {
  test('list, forget one, forget all behind a confirmation, then the empty state', async ({ page }, info) => {
    const api = await mockApi(page, { status: status({ enabled: true, trustedDeviceCount: 2 }), devices: [...DEVICES] });
    await openSecurity(page);
    await expect(trusted(page).getByText("A trusted browser doesn't ask for a code for 30 days after you trust it.", { exact: false })).toBeVisible();
    const rows = page.getByTestId('trusted-browser-row');
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText('Chrome on Windows');
    await expect(rows.nth(0).getByTestId('this-browser-badge')).toHaveText('This browser');
    await expect(rows.nth(1).getByTestId('this-browser-badge')).toHaveCount(0);
    await expect(rows.nth(1)).toContainText('Not used yet');
    await expect(rows.nth(1)).toContainText('198.51.100.24');
    await shot(page, 'card-trusted-en', info.outputPath.bind(info));

    await page.getByRole('button', { name: 'Forget Safari on iPhone' }).click();
    await expect(rows).toHaveCount(1);
    await expect(trusted(page).getByText('Safari on iPhone is forgotten. It will ask for a code the next time you sign in.')).toBeVisible();
    expect(api.calls.some((c) => c.method === 'DELETE' && c.path === '/api/auth/mfa/trusted-devices/d-2')).toBe(true);

    await page.getByTestId('forget-all-browsers').click();
    const dialog = page.getByRole('dialog', { name: 'Forget all browsers?' });
    await expect(dialog).toContainText('Every browser, this one included, will ask for a code the next time you sign in.');
    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toHaveCount(0);
    expect(api.calls.some((c) => c.method === 'DELETE' && c.path === '/api/auth/mfa/trusted-devices')).toBe(false);
    await page.getByTestId('forget-all-browsers').click();
    await page.getByRole('dialog', { name: 'Forget all browsers?' }).getByRole('button', { name: 'Forget all browsers' }).click();
    await expect(page.getByTestId('trusted-browsers-empty')).toHaveText('No trusted browsers. Every sign-in asks for a code.');
    expect(api.calls.some((c) => c.method === 'DELETE' && c.path === '/api/auth/mfa/trusted-devices')).toBe(true);
    await expect(page.getByTestId('forget-all-browsers')).toHaveCount(0);
  });

  test('a browser already forgotten elsewhere refreshes the list', async ({ page }) => {
    const api = await mockApi(page, { status: status({ enabled: true, trustedDeviceCount: 2 }), devices: [...DEVICES] });
    await openSecurity(page);
    await expect(page.getByTestId('trusted-browser-row')).toHaveCount(2);
    api.world.devices = [DEVICES[0]];
    await page.getByRole('button', { name: 'Forget Safari on iPhone' }).click();
    await expect(trusted(page).getByText('That browser was already forgotten. The list is up to date.')).toBeVisible();
    await expect(page.getByTestId('trusted-browser-row')).toHaveCount(1);
  });

  test('switched off by the company: no lifetime promised', async ({ page }) => {
    await mockApi(page, { status: status({ enabled: true, rememberDeviceDays: 0 }), devices: [] });
    await openSecurity(page);
    await expect(trusted(page).getByText('Your company has switched off trusted browsers, so every sign-in asks for a code.')).toBeVisible();
  });
});

test.describe('Arabic, dark and phone', () => {
  test('Arabic: the card mirrors and every word is Arabic', async ({ page }, info) => {
    await mockApi(page, {
      locale: 'ar', privileged: true,
      status: status({ enabled: true, required: true, requiredBecause: 'privileged_role', trustedDeviceCount: 2 }), devices: [...DEVICES],
    });
    await page.goto('/account/security');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(card(page).getByTestId('two-step-status')).toHaveText('التحقق بخطوتين مُفعّل', { timeout: 30_000 });
    await expect(page.getByTestId('two-step-required-reason')).toContainText('تشترط شركتك التحقق بخطوتين');
    await expect(page.getByRole('heading', { name: 'المتصفحات الموثوقة' })).toBeVisible();
    await expect(page.getByTestId('this-browser-badge')).toHaveText('هذا المتصفح');
    // The status icon is at the inline start: the right edge in Arabic.
    const cardBox = (await card(page).boundingBox())!;
    const headingBox = (await card(page).getByTestId('two-step-status').boundingBox())!;
    expect(headingBox.x + headingBox.width).toBeLessThan(cardBox.x + cardBox.width - 40);
    await shot(page, 'card-ar', info.outputPath.bind(info));
    // Arabic setup (a non-privileged user with it off).
  });

  test('Arabic setup form', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar', status: status(), devices: [] });
    await page.goto('/account/security');
    await card(page).getByRole('button', { name: 'تفعيل التحقق بخطوتين' }).click({ timeout: 30_000 });
    await expect(page.getByTestId('two-step-qr')).toBeVisible();
    // The key stays left to right inside the Arabic page.
    await expect(page.getByTestId('two-step-key')).toHaveAttribute('dir', 'ltr');
    await shot(page, 'card-setup-ar', info.outputPath.bind(info));
  });

  test('dark theme', async ({ page }, info) => {
    await mockApi(page, { theme: 'dark', status: status({ enabled: true, trustedDeviceCount: 2 }), devices: [...DEVICES] });
    await openSecurity(page);
    await expect(page.locator('html')).toHaveClass(/dark/);
    await shot(page, 'card-dark', info.outputPath.bind(info));
    await card(page).getByRole('button', { name: 'Turn off two-step sign-in' }).click();
    await shot(page, 'card-dark-disable', info.outputPath.bind(info));
  });

  test('390px: reachable from the drawer, and nothing scrolls sideways', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { status: status({ enabled: true, trustedDeviceCount: 2 }), devices: [...DEVICES] });
    // Any signed-in page; the Self-Service overview needs richer fixtures than this spec's.
    await page.goto('/ess/documents');
    await page.getByRole('button', { name: 'Open navigation' }).click();
    await page.getByTestId('sidebar-sign-in-security').click();
    await page.waitForURL(/\/account\/security$/);
    await expect(card(page).getByTestId('two-step-status')).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('trusted-browser-row')).toHaveCount(2);
    await expectNoHorizontalScroll(page);
    await shot(page, 'card-390', info.outputPath.bind(info));
    await card(page).getByRole('button', { name: 'Turn off two-step sign-in' }).click();
    await expectNoHorizontalScroll(page);
    await shot(page, 'card-390-disable', info.outputPath.bind(info));
  });

  test('390px Arabic setup fits', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { locale: 'ar', status: status(), devices: [] });
    await page.goto('/account/security');
    await card(page).getByRole('button', { name: 'تفعيل التحقق بخطوتين' }).click({ timeout: 30_000 });
    await expect(page.getByTestId('two-step-qr')).toBeVisible();
    await expectNoHorizontalScroll(page);
    await shot(page, 'card-390-ar-setup', info.outputPath.bind(info));
  });
});
