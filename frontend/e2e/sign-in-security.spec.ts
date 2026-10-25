import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * Two-step sign-in choice and trusted browsers.
 *
 * Over mocked API routes shaped exactly as that contract: the challenge's `rememberDeviceDays`,
 * `rememberDevice` on challenge/verify, GET /api/auth/mfa/status with `canDisable`,
 * `rememberDeviceDays` and `trustedDeviceCount`, setup / verify-setup / disable with their
 * `{ code }` refusals, and GET / DELETE /api/auth/mfa/trusted-devices. Wave 2 (mfa2-contract):
 * verify-setup and enrollment/verify-setup answer 200 `{ backupCodes }` (or the older 204),
 * POST /api/auth/mfa/backup-codes regenerates, status gains `backupCodesRemaining`, and
 * challenge/verify takes `backupCode` instead of `totpCode`. The server's rules (who may
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
  backupCodesRemaining?: number;
}

// Ten codes in the contract's shape: 10 Crockford base32 characters, shown as XXXXX-XXXXX.
const CODES = ['K7Q2M-9XH4P', '3TBWN-6RZ8D', 'HV5JC-2YQ7A', 'M8E4R-XK3TG', '9PDZW-5NC2H', 'B6TXQ-7JM4V', 'R2HKE-8WP9Z', '5YGCN-3DT6Q', 'WQ7MA-4BX2K', 'D3ZVR-9HE5N'];
const NEW_CODES = ['XN4QA-7TB2R', 'GK9WD-3MZ5E', 'P2VHC-8QJ6T', 'Z5RMB-4XW9K', 'C7TEN-2HD3Q', 'J3KZV-6PA8M', 'T8QXW-5BG2D', 'E4MDR-9CN7Z', 'A6HPK-3VT5X', 'Y9BCG-7RE4W'];

interface Device {
  id: string; createdAtUtc: string; lastUsedAtUtc: string | null; expiresAtUtc: string;
  browser: string | null; ipAddress: string | null; current: boolean;
}

const DEVICES: Device[] = [
  { id: 'd-1', createdAtUtc: '2026-10-01T08:00:00Z', lastUsedAtUtc: '2026-10-21T07:30:00Z', expiresAtUtc: '2026-10-31T08:00:00Z', browser: 'Chrome on Windows', ipAddress: '203.0.113.7', current: true },
  // The server's own wording (MfaTrustedDevices.DescribeBrowser): browser on OS, never the hardware.
  { id: 'd-2', createdAtUtc: '2026-09-28T18:00:00Z', lastUsedAtUtc: null, expiresAtUtc: '2026-10-28T18:00:00Z', browser: 'Safari on iOS', ipAddress: '198.51.100.24', current: false },
];
const THIRD: Device = { id: 'd-3', createdAtUtc: '2026-09-20T09:00:00Z', lastUsedAtUtc: '2026-10-19T12:00:00Z', expiresAtUtc: '2026-10-20T09:00:00Z', browser: 'Firefox on macOS', ipAddress: null, current: false };

const ROLE_REASON = 'Required for your role, because you can see or change payroll, banking or access.';
const WORKSPACE_REASON = 'Required by your company for everyone.';

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
  /** verify-setup re-issues this session in the login shape alongside the codes (the current server). */
  reissueSession?: boolean;
  verifySetup?: Reply[];
  disable?: Reply[];
  forgetOne?: Reply[];
  locale?: 'en' | 'ar';
  theme?: 'light' | 'dark';
  signedIn?: boolean;
  login?: Reply[];
  /** verify-setup and enrollment/verify-setup answer 200 with these; absent = the older 204 (no codes). */
  backupCodes?: string[];
  /** POST /api/auth/mfa/backup-codes replies; default 200 with NEW_CODES. */
  regenerate?: Reply[];
  /** POST /api/auth/mfa/challenge/verify replies; default a session. */
  challengeVerify?: Reply[];
  /** Stub navigator.clipboard so Copy all can be read back in every project. */
  clipboard?: boolean;
}

async function mockApi(page: Page, world: World) {
  const calls: Call[] = [];
  const errors: string[] = [];
  /** Everything the page writes to the console: backup codes must never appear in it. */
  const logs: string[] = [];
  let sessionEnded = false;
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => logs.push(message.text()));
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
  if (world.clipboard) {
    await page.addInitScript(() => {
      Object.defineProperty(navigator, 'clipboard', {
        configurable: true,
        value: {
          writeText: async (text: string) => { (window as any).__copied = text; },
          readText: async () => (window as any).__copied ?? '',
        },
      });
    });
  }

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
    if (p === '/api/auth/mfa/enrollment/setup') return json({ provisioningUri: URI });
    // An older API answered 200 {} here; a wave-2 API answers 200 { backupCodes }.
    if (p === '/api/auth/mfa/enrollment/verify-setup') return json(world.backupCodes ? { backupCodes: world.backupCodes } : {});
    if (p === '/api/auth/mfa/challenge/verify') {
      const r = next(world.challengeVerify, { status: 200 });
      if (r.status !== 200) return json(r.body, r.status);
      return json({ accessToken: 'fixture-access', refreshToken: 'fixture-refresh', expiresAtUtc: '2026-10-23T00:00:00Z', user: user(!!world.privileged) });
    }
    if (p === '/api/auth/mfa/backup-codes' && method === 'POST') {
      const r = next(world.regenerate, { status: 200, body: { backupCodes: NEW_CODES } });
      if (r.status === 200) world.status = { ...world.status, backupCodesRemaining: (r.body as any)?.backupCodes?.length ?? 0 };
      return json(r.body, r.status);
    }
    if (p === '/api/auth/me') return sessionEnded ? json({ message: 'Session ended.' }, 401) : json(user(!!world.privileged));
    if (p === '/api/auth/refresh') return sessionEnded ? json({ message: 'Session ended.' }, 401) : json({ accessToken: 'fixture-access', refreshToken: 'fixture-refresh' });
    if (p === '/api/auth/logout') return noContent();
    if (p === '/api/auth/mfa/status') return json(world.status);
    if (p === '/api/auth/mfa/setup' && method === 'POST') return json({ provisioningUri: URI });
    if (p === '/api/auth/mfa/verify-setup' && method === 'POST') {
      const r = next(world.verifySetup, { status: 204 });
      if (r.status === 204) {
        world.status = status({ ...world.status, enabled: true, promptToEnroll: false, canDisable: !world.status.required,
          ...(world.backupCodes ? { backupCodesRemaining: world.backupCodes.length } : {}) });
        if (world.changeEndsSession) sessionEnded = true;
        if (world.reissueSession) return json({ backupCodes: world.backupCodes ?? [], accessToken: 'reissued-access', refreshToken: 'reissued-refresh', expiresAtUtc: '2030-01-01T00:00:00Z' });
        return world.backupCodes ? json({ backupCodes: world.backupCodes }) : noContent();
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
  return { calls, errors, logs, world };
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
    // One name for the field in both login modes, in plain words.
    await expect(page.getByLabel('6-digit code', { exact: true })).toHaveAttribute('id', 'mfa-code');
    await expect(page.getByRole('heading', { name: 'Two-step sign-in' })).toBeVisible();
    await expect(page.getByText(/two-factor/i)).toHaveCount(0);
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
    // The code (and its 000000 placeholder) is centred in Arabic too, not pushed to the left edge.
    await expect(page.locator('#mfa-code')).toHaveCSS('text-align', 'center');
    await expect(page.getByLabel('الرمز المكوّن من 6 أرقام', { exact: true })).toHaveAttribute('id', 'mfa-code');
    await page.getByRole('button', { name: 'فقدت هاتفك؟' }).click();
    await expect(page.getByTestId('mfa-lost-phone-help')).toHaveText('اطلب من مسؤول النظام في شركتك إعادة تعيين التحقق بخطوتين، أو استخدم أحد رموزك الاحتياطية. بعد إعادة التعيين ستُعدّه من جديد عند تسجيل الدخول التالي.');
    await shot(page, 'login-code-step-ar', info.outputPath.bind(info));
  });

  test('"Lost your phone?" says who can help, and toggles from the keyboard', async ({ page }) => {
    await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challenge(30)] });
    await reachCodeStep(page);
    const lost = page.getByRole('button', { name: 'Lost your phone?' });
    await expect(lost).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('mfa-lost-phone-help')).toHaveCount(0);
    await lost.focus();
    await page.keyboard.press('Enter');
    await expect(lost).toHaveAttribute('aria-expanded', 'true');
    await expect(lost).toHaveAttribute('aria-controls', 'mfa-lost-phone-help');
    // The existing way out, plus the backup codes that need nobody.
    await expect(page.getByTestId('mfa-lost-phone-help')).toHaveText("Ask your company's administrator to reset two-step sign-in, or use one of your backup codes. After a reset, you'll set it up again the next time you sign in.");
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('mfa-lost-phone-help')).toHaveCount(0);
  });

  test('the trust checkbox: a real, labelled, described checkbox painted to the page', async ({ page }) => {
    await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challenge(30)] });
    await reachCodeStep(page);
    const box = page.getByTestId('mfa-trust-browser');
    // Still the native control: its role, its <label for> and its hint survive the restyle.
    await expect(box).toHaveAttribute('type', 'checkbox');
    await expect(page.getByRole('checkbox', { name: 'Trust this browser for 30 days' })).toHaveAttribute('aria-describedby', 'mfa-trust-hint');
    await expect(box).toHaveAccessibleDescription("You won't be asked for a code on this browser until then. Don't use on a shared computer.");
    await expect(box).toHaveCSS('appearance', 'none');
    const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
    const size = (await box.boundingBox())!;
    expect(Math.round(size.width)).toBe(coarse ? 24 : 20);
    if (coarse) expect((await page.locator('label[for="mfa-trust"]').boundingBox())!.height).toBeGreaterThanOrEqual(44);
    // Cyan with a drawn tick when checked; clicking the LABEL checks it.
    await page.locator('label[for="mfa-trust"]').click();
    await expect(box).toBeChecked();
    await expect(box).toHaveCSS('background-color', 'rgb(94, 235, 255)');
    expect(await box.evaluate((el) => getComputedStyle(el).backgroundImage)).toContain('data:image/svg+xml');
    // Keyboard: Space toggles it, and focus shows a visible ring.
    await page.locator('#mfa-code').focus();
    await page.keyboard.press('Tab');
    await expect(box).toBeFocused();
    await expect(box).toHaveCSS('outline-style', 'solid');
    await expect(box).toHaveCSS('outline-color', 'rgb(94, 235, 255)');
    await page.keyboard.press('Space');
    await expect(box).not.toBeChecked();
    // Forced colours: the system draws the control again, so the tick cannot vanish.
    await page.emulateMedia({ forcedColors: 'active' });
    await expect(box).toHaveCSS('appearance', 'auto');
    await page.keyboard.press('Space');
    await expect(box).toBeChecked();
  });

  test('a required first-time setup at sign-in says two-step sign-in, and who resets a lost phone', async ({ page }) => {
    const api = await mockApi(page, {
      status: status(), devices: [], signedIn: false,
      login: [{ status: 200, body: { mfaEnrollmentRequired: true, enrollmentToken: 'enr-1', expiresInSeconds: 600, message: 'Set up two-step sign-in.' } }],
    });
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill('Desert-Falcon-2026');
    await page.locator('#li-pw').press('Enter');
    await expect(page.getByRole('heading', { name: 'Set up two-step sign-in' })).toBeVisible();
    await expect(page.getByLabel('6-digit code', { exact: true })).toHaveAttribute('id', 'mfa-enroll-code');
    await expect(page.getByText('If you lose this phone, your administrator can reset it, or you can use one of your backup codes.')).toBeVisible();
    await expect(page.getByText(/two-factor/i)).toHaveCount(0);
    await page.getByLabel('6-digit code', { exact: true }).fill('123456');
    await page.getByRole('button', { name: 'Turn on two-step sign-in' }).click();
    await expect.poll(() => api.calls.find((c) => c.path === '/api/auth/mfa/enrollment/verify-setup')?.body)
      .toEqual({ enrollmentToken: 'enr-1', tempSecret: 'JBSWY3DPEHPK3PXP', totpCode: '123456' });
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
    // Phones hide the top-bar user menu; there the entry lives in the navigation drawer.
    const userMenu = page.getByRole('button', { name: 'Open user menu' });
    await page.getByRole('button', { name: /^Open (user menu|navigation)$/ }).first().waitFor();
    if (await userMenu.isVisible()) {
      await userMenu.click();
      await page.getByTestId('user-menu-sign-in-security').click();
    } else {
      await page.getByRole('button', { name: 'Open navigation' }).click();
      await page.getByTestId('sidebar-sign-in-security').click();
    }
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
    await expect(page.getByTestId('two-step-lost-phone-note')).toHaveText('If you lose this phone, your administrator can reset it, or you can use one of your backup codes.');
    // Nothing typed yet: "Turn on" is visibly disabled (grey), not a faded blue.
    await expect(page.getByRole('button', { name: 'Turn on', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Turn on', exact: true })).toHaveCSS('background-color', 'rgb(241, 245, 249)');
    await expect(page.getByRole('button', { name: 'Turn on', exact: true })).toHaveCSS('opacity', '1');
    await shot(page, 'card-setup-en', info.outputPath.bind(info));
    await code.fill('654321');
    await page.getByRole('button', { name: 'Turn on', exact: true }).click();
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
    await expect(card(page).getByText('Two-step sign-in is on. You were signed out everywhere else.')).toBeVisible();
    expect(api.calls.find((c) => c.path === '/api/auth/mfa/verify-setup')?.body).toEqual({ tempSecret: 'JBSWY3DPEHPK3PXP', totpCode: '654321' });
    // An API from before backup codes answers 204: no codes step, and no count promised.
    await expect(page.getByTestId('backup-codes-step')).toHaveCount(0);
    await expect(page.getByTestId('backup-codes-remaining')).toHaveCount(0);
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
    await expect(submit).toHaveCSS('background-color', 'rgb(241, 245, 249)');
    await page.getByRole('checkbox', { name: /only my password will protect this account/ }).check();
    // Enabled, it is the shared danger button: white on rose-700.
    await expect(submit).toHaveClass(/btn-danger/);
    await expect(submit).toHaveCSS('background-color', 'rgb(190, 18, 60)');
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
    await expect(page.getByTestId('two-step-required-reason')).toHaveText(WORKSPACE_REASON);
    await expect(card(page).getByRole('button', { name: 'Turn off two-step sign-in' })).toHaveCount(0);
  });
});

test.describe('Sign-in security: focus follows the person', () => {
  test('opening a panel focuses its heading; Cancel returns to the button that opened it', async ({ page }) => {
    const api = await mockApi(page, { status: status(), devices: [] });
    await openSecurity(page);
    await card(page).getByRole('button', { name: 'Turn on two-step sign-in' }).click();
    await expect(page.getByTestId('two-step-setup-heading')).toBeFocused();
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(card(page).getByRole('button', { name: 'Turn on two-step sign-in' })).toBeFocused();

    api.world.status = status({ enabled: true });
    await page.reload();
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on', { timeout: 30_000 });
    await card(page).getByRole('button', { name: 'Turn off two-step sign-in' }).click();
    await expect(page.getByTestId('two-step-disable-heading')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Current 6-digit code from your authenticator app')).toBeFocused();
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(card(page).getByRole('button', { name: 'Turn off two-step sign-in' })).toBeFocused();
  });

  test('after a change in place, focus lands on the new status', async ({ page }) => {
    await mockApi(page, { status: status(), devices: [] });
    await openSecurity(page);
    await card(page).getByRole('button', { name: 'Turn on two-step sign-in' }).click();
    await page.getByLabel('Enter the 6-digit code the app shows').fill('654321');
    await page.getByRole('button', { name: 'Turn on', exact: true }).click();
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
    await expect(card(page).getByTestId('two-step-status')).toBeFocused();
  });

  test('after Forget, focus moves to the next Forget, and to the heading once the list is empty', async ({ page }) => {
    await mockApi(page, { status: status({ enabled: true, trustedDeviceCount: 3 }), devices: [...DEVICES, THIRD] });
    await openSecurity(page);
    const rows = page.getByTestId('trusted-browser-row');
    await expect(rows).toHaveCount(3);
    // The middle row goes: the row that slides into its place is focused.
    await page.getByRole('button', { name: 'Forget Safari on iOS' }).click();
    await expect(rows).toHaveCount(2);
    await expect(page.getByRole('button', { name: 'Forget Firefox on macOS' })).toBeFocused();
    // The last row goes: the new last row is focused.
    await page.keyboard.press('Enter');
    await expect(rows).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Forget Chrome on Windows' })).toBeFocused();
    // One browser left: no "Forget all" sweep for a single row.
    await expect(page.getByTestId('forget-all-browsers')).toHaveCount(0);
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('trusted-browsers-empty')).toBeVisible();
    await expect(page.getByTestId('trusted-browsers-heading')).toBeFocused();
  });

  test('after Forget all, focus lands on the "Trusted browsers" heading, not the page', async ({ page }) => {
    await mockApi(page, { status: status({ enabled: true, trustedDeviceCount: 2 }), devices: [...DEVICES] });
    await openSecurity(page);
    await page.getByTestId('forget-all-browsers').click();
    await page.getByRole('dialog', { name: 'Forget all browsers?' }).getByRole('button', { name: 'Forget all browsers' }).click();
    await expect(page.getByTestId('trusted-browsers-empty')).toBeVisible();
    await expect(page.getByTestId('trusted-browsers-heading')).toBeFocused();
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
    await expect(page.getByTestId('two-step-required-reason')).toHaveText(ROLE_REASON);
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
    await expect(page.getByTestId('two-step-required-reason')).toHaveText(ROLE_REASON);
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
  });
});

test.describe('trusted browsers', () => {
  test('list, forget one, forget all behind a confirmation, then the empty state', async ({ page }, info) => {
    const api = await mockApi(page, { status: status({ enabled: true, trustedDeviceCount: 3 }), devices: [...DEVICES, THIRD] });
    await openSecurity(page);
    await expect(trusted(page).getByText('A trusted browser skips the code for 30 days. Your company sets this period.')).toBeVisible();
    const rows = page.getByTestId('trusted-browser-row');
    await expect(rows).toHaveCount(3);
    await expect(rows.nth(0)).toContainText('Chrome on Windows');
    await expect(rows.nth(0).getByTestId('this-browser-badge')).toHaveText('This browser');
    await expect(rows.nth(1).getByTestId('this-browser-badge')).toHaveCount(0);
    // The device picture follows the server's words: a monitor for Windows/macOS, a phone for iOS.
    await expect(rows.nth(0).getByTestId('device-icon')).toHaveAttribute('data-kind', 'computer');
    await expect(rows.nth(1).getByTestId('device-icon')).toHaveAttribute('data-kind', 'phone');
    await expect(rows.nth(2).getByTestId('device-icon')).toHaveAttribute('data-kind', 'computer');
    // Each fact is a labelled pair; a browser never used since it was trusted says so in words.
    await expect(rows.nth(1).locator('dt')).toHaveText(['Trusted since', 'Last used', 'Expires', 'IP address']);
    await expect(rows.nth(1).locator('dd').nth(1)).toHaveText('Not used since you trusted it');
    await expect(rows.nth(1)).toContainText('198.51.100.24');
    // No address recorded: no empty "IP address" line.
    await expect(rows.nth(2).locator('dt')).toHaveText(['Trusted since', 'Last used', 'Expires']);
    // Row Forget is the shared secondary button; Forget all is a quiet text button, never full width.
    await expect(rows.nth(0).getByTestId('forget-browser')).toHaveClass(/btn-secondary/);
    const sweep = (await page.getByTestId('forget-all-browsers').boundingBox())!;
    expect(sweep.width).toBeLessThan((await trusted(page).boundingBox())!.width / 2);
    await shot(page, 'card-trusted-en', info.outputPath.bind(info));

    await page.getByRole('button', { name: 'Forget Safari on iOS' }).click();
    await expect(rows).toHaveCount(2);
    await expect(trusted(page).getByText('Safari on iOS is forgotten. It will ask for a code the next time you sign in.')).toBeVisible();
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
    await page.getByRole('button', { name: 'Forget Safari on iOS' }).click();
    await expect(trusted(page).getByText('That browser was already forgotten. The list is up to date.')).toBeVisible();
    await expect(page.getByTestId('trusted-browser-row')).toHaveCount(1);
  });

  test('switched off by the company: no lifetime promised', async ({ page }) => {
    await mockApi(page, { status: status({ enabled: true, rememberDeviceDays: 0 }), devices: [] });
    await openSecurity(page);
    await expect(trusted(page).getByText('Your company has switched off trusted browsers, so every sign-in asks for a code.')).toBeVisible();
  });
});

const codesStep = (page: Page) => page.getByTestId('backup-codes-step');
const ENROLMENT = { status: 200, body: { mfaEnrollmentRequired: true, enrollmentToken: 'enr-1', expiresInSeconds: 600, message: 'Set up two-step sign-in.' } };
const challengeReply = (days: number) => ({ status: 200, body: { mfaRequired: true, challengeToken: 'chal-1', expiresInSeconds: 300, rememberDeviceDays: days } });

/** After Done, no code is left anywhere in the page: not in the DOM, not in an attribute. */
async function expectCodesGone(page: Page, codes: string[]) {
  const html = await page.content();
  for (const c of codes) {
    expect(html).not.toContain(c);
    expect(html).not.toContain(c.replace('-', ''));
  }
}

/** No code was ever written to the console. */
function expectNotLogged(logs: string[], codes: string[]) {
  expect(logs.filter((line) => codes.some((c) => line.includes(c) || line.includes(c.replace('-', ''))))).toEqual([]);
}

/** Copy all puts every code on the clipboard, and Download saves kynexone-backup-codes.txt with every code. */
async function expectCopyAndDownload(page: Page, codes: string[]) {
  const step = codesStep(page);
  await step.getByTestId('backup-codes-copy').click();
  await expect(step.getByTestId('backup-codes-copy')).toHaveText('Copied');
  await expect(step.getByRole('status')).toHaveText('The codes are copied. Paste them somewhere safe.');
  const copied = await page.evaluate(() => (window as any).__copied as string);
  for (const c of codes) expect(copied).toContain(c);
  expect(copied).toContain('Each code works once. Keep them somewhere safe, away from your phone.');
  const [download] = await Promise.all([page.waitForEvent('download'), step.getByRole('button', { name: 'Download' }).click()]);
  expect(download.suggestedFilename()).toBe('kynexone-backup-codes.txt');
  const text = fs.readFileSync((await download.path())!, 'utf8');
  for (const c of codes) expect(text).toContain(c);
  expect(text).toContain(EMAIL);
}

async function turnOnFromPage(page: Page) {
  await openSecurity(page);
  await card(page).getByRole('button', { name: 'Turn on two-step sign-in' }).click();
  await page.getByLabel('Enter the 6-digit code the app shows').fill('654321');
  await page.getByRole('button', { name: 'Turn on', exact: true }).click();
  await expect(codesStep(page)).toBeVisible();
}

async function enrolAtSignIn(page: Page, code = '123456') {
  await page.goto('/login');
  await page.locator('#li-em').fill(EMAIL);
  await page.locator('#li-pw').fill('Desert-Falcon-2026');
  await page.locator('#li-pw').press('Enter');
  await page.locator('#mfa-enroll-code').fill(code);
  await page.locator('#mfa-enroll-code').press('Enter');
  await expect(codesStep(page)).toBeVisible();
}

test.describe('backup codes: saved once, after turning on', () => {
  test('on the page: two columns, Copy all and Download work, "I\'ve saved" gates Done, and Done leaves nothing behind', async ({ page }, info) => {
    const api = await mockApi(page, { status: status(), devices: [], backupCodes: CODES, clipboard: true });
    await turnOnFromPage(page);
    const step = codesStep(page);
    await expect(step.getByRole('heading', { name: 'Save your backup codes' })).toBeFocused();
    // Turned on, so the heading says so; but nothing is re-read under the codes (turning on can end the session).
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
    const verifiedAt = api.calls.findIndex((c) => c.path === '/api/auth/mfa/verify-setup');
    const list = step.getByTestId('backup-codes-list');
    await expect(list.locator('li')).toHaveText(CODES);
    expect(await list.evaluate((el) => getComputedStyle(el).gridTemplateColumns.split(' ').length)).toBe(2);
    expect(await list.evaluate((el) => getComputedStyle(el).fontFamily)).toMatch(/mono/i);
    await expect(list.locator('li').first()).toHaveAttribute('dir', 'ltr');
    await expect(step.getByTestId('backup-codes-keep-safe')).toHaveText('Each code works once. Keep them somewhere safe, away from your phone.');
    const done = step.getByRole('button', { name: 'Done' });
    await expect(done).toBeDisabled();
    await shot(page, 'codes-step-page-en', info.outputPath.bind(info));

    await expectCopyAndDownload(page, CODES);
    await expect(done).toBeDisabled();
    await step.getByRole('checkbox', { name: "I've saved these codes" }).check();
    await expect(done).toBeEnabled();
    expect(api.calls.slice(verifiedAt + 1).filter((c) => c.path.startsWith('/api/auth/mfa/'))).toEqual([]);
    await done.click();

    await expect(codesStep(page)).toHaveCount(0);
    await expect(card(page).getByText('Two-step sign-in is on. You were signed out everywhere else.')).toBeVisible();
    await expect(card(page).getByTestId('backup-codes-remaining')).toHaveText('Backup codes: 10 left');
    await expect(card(page).getByTestId('two-step-status')).toBeFocused();
    await expectCodesGone(page, CODES);
    expectNotLogged(api.logs, CODES);
    expect(api.errors).toEqual([]);
  });

  test('the session the server re-issues on turning on is kept, so the codes are not lost to a sign-out', async ({ page }) => {
    await mockApi(page, { status: status(), devices: [], backupCodes: CODES, reissueSession: true });
    await turnOnFromPage(page);
    await expect(codesStep(page).getByTestId('backup-codes-list').locator('li')).toHaveCount(10);
    expect(await page.evaluate(() => [localStorage.getItem('zayra_access_token'), localStorage.getItem('zayra_refresh_token')]))
      .toEqual(['reissued-access', 'reissued-refresh']);
    await codesStep(page).getByRole('checkbox', { name: "I've saved these codes" }).check();
    await codesStep(page).getByRole('button', { name: 'Done' }).click();
    await expect(page).not.toHaveURL(/\/login/);
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is on');
  });

  test('when turning on ended this session, the codes still come first, then sign-in says why', async ({ page }) => {
    await mockApi(page, { status: status(), devices: [], backupCodes: CODES, changeEndsSession: true });
    await turnOnFromPage(page);
    await expect(codesStep(page).getByTestId('backup-codes-list').locator('li')).toHaveCount(10);
    await codesStep(page).getByRole('checkbox', { name: "I've saved these codes" }).check();
    await codesStep(page).getByRole('button', { name: 'Done' }).click();
    await page.waitForURL(/\/login/);
    await expect(page.locator('.lx-ok')).toHaveText(/Two-step sign-in is on, and you were signed out everywhere for your security\./);
    await expectCodesGone(page, CODES);
  });

  test('forced setup at sign-in: the codes come before signing in again', async ({ page }, info) => {
    const api = await mockApi(page, { status: status(), devices: [], signedIn: false, backupCodes: CODES, clipboard: true, login: [ENROLMENT] });
    await enrolAtSignIn(page);
    const step = codesStep(page);
    await expect(step.getByRole('heading', { name: 'Save your backup codes' })).toBeFocused();
    await expect(step.getByTestId('backup-codes-list').locator('li')).toHaveText(CODES);
    expect(await step.getByTestId('backup-codes-list').evaluate((el) => getComputedStyle(el).gridTemplateColumns.split(' ').length)).toBe(2);
    await expect(step.getByTestId('backup-codes-keep-safe')).toHaveText('Each code works once. Keep them somewhere safe, away from your phone.');
    const done = step.getByRole('button', { name: 'Done' });
    await expect(done).toBeDisabled();
    await shot(page, 'codes-step-login-en', info.outputPath.bind(info));
    await expectCopyAndDownload(page, CODES);
    await page.locator('label', { hasText: "I've saved these codes" }).click();
    await expect(step.getByRole('checkbox', { name: "I've saved these codes" })).toBeChecked();
    await done.click();
    await expect(page.locator('.lx-ok')).toHaveText('Two-step sign-in is on. Sign in again to continue.');
    await expect(page.locator('#li-em')).toBeVisible();
    await expectCodesGone(page, CODES);
    expectNotLogged(api.logs, CODES);
    expect(api.errors).toEqual([]);
  });

  test('an older API (200 without codes) at sign-in goes straight back to sign in', async ({ page }) => {
    await mockApi(page, { status: status(), devices: [], signedIn: false, login: [ENROLMENT] });
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill('Desert-Falcon-2026');
    await page.locator('#li-pw').press('Enter');
    await page.locator('#mfa-enroll-code').fill('123456');
    await page.locator('#mfa-enroll-code').press('Enter');
    await expect(page.locator('.lx-ok')).toHaveText('Two-step sign-in is on. Sign in again to continue.');
    await expect(codesStep(page)).toHaveCount(0);
  });
});

test.describe('backup codes: on the Sign-in security card', () => {
  test('Get new backup codes needs a current code, warns the old ones stop working, and the count updates', async ({ page }, info) => {
    const api = await mockApi(page, {
      status: status({ enabled: true, backupCodesRemaining: 3 }), devices: [],
      regenerate: [{ status: 400, body: { code: 'ERR_MFA_CODE_INVALID', message: 'Invalid code.' } }, { status: 200, body: { backupCodes: NEW_CODES } }],
    });
    await openSecurity(page);
    await expect(card(page).getByTestId('backup-codes-remaining')).toHaveText('Backup codes: 3 left');
    // 3 or fewer: a quiet note, not an alarm.
    await expect(card(page).getByTestId('backup-codes-low')).toHaveText('Only a few backup codes are left. Get new ones before they run out.');
    await expect(card(page).getByTestId('backup-codes-low').getByRole('alert')).toHaveCount(0);
    await shot(page, 'card-low-codes-en', info.outputPath.bind(info));

    await card(page).getByRole('button', { name: 'Get new backup codes' }).click();
    await expect(page.getByTestId('backup-codes-regenerate-heading')).toBeFocused();
    await expect(page.getByTestId('backup-codes-regenerate-warning')).toHaveText('Your old codes stop working. Only the new ones will sign you in.');
    const submit = page.getByRole('button', { name: 'Get new codes' });
    await expect(submit).toBeDisabled();
    const code = page.getByLabel('Current 6-digit code from your authenticator app');
    await expect(code).toHaveAttribute('autocomplete', 'one-time-code');
    await shot(page, 'card-regenerate-en', info.outputPath.bind(info));
    await code.fill('111111');
    await submit.click();
    await expect(page.getByRole('alert').filter({ hasText: "That code didn't match. Enter the newest code from your authenticator app. Each code works only once." })).toBeVisible();
    await expect(codesStep(page)).toHaveCount(0);
    await code.fill('222222');
    await submit.click();

    const step = codesStep(page);
    await expect(step.getByRole('heading', { name: 'Save your backup codes' })).toBeFocused();
    await expect(step).toContainText('These replace your old backup codes, which no longer work.');
    await expect(step.getByTestId('backup-codes-list').locator('li')).toHaveText(NEW_CODES);
    await expect(step.getByRole('button', { name: 'Done' })).toBeDisabled();
    await step.getByRole('checkbox', { name: "I've saved these codes" }).check();
    await step.getByRole('button', { name: 'Done' }).click();

    await expect(card(page).getByTestId('backup-codes-remaining')).toHaveText('Backup codes: 10 left');
    await expect(card(page).getByTestId('backup-codes-low')).toHaveCount(0);
    await expect(card(page).getByText('Your new backup codes are ready. The old ones no longer work.')).toBeVisible();
    await expect(card(page).getByTestId('two-step-status')).toBeFocused();
    expect(api.calls.filter((c) => c.path === '/api/auth/mfa/backup-codes').map((c) => c.body)).toEqual([{ totpCode: '111111' }, { totpCode: '222222' }]);
    await expectCodesGone(page, NEW_CODES);
    expectNotLogged(api.logs, NEW_CODES);
  });

  test('the note shows at 3 or fewer, not at 4, and says so when none are left', async ({ page }) => {
    const api = await mockApi(page, { status: status({ enabled: true, backupCodesRemaining: 4 }), devices: [] });
    await openSecurity(page);
    await expect(card(page).getByTestId('backup-codes-remaining')).toHaveText('Backup codes: 4 left');
    await expect(card(page).getByTestId('backup-codes-low')).toHaveCount(0);
    api.world.status = status({ enabled: true, backupCodesRemaining: 0 });
    await page.reload();
    await expect(card(page).getByTestId('backup-codes-remaining')).toHaveText('Backup codes: 0 left', { timeout: 30_000 });
    await expect(card(page).getByTestId('backup-codes-low')).toHaveText('You have no backup codes left. Get new ones so you can still sign in without your phone.');
  });

  test('Cancel returns focus to Get new backup codes; a 409 says two-step is off', async ({ page }) => {
    await mockApi(page, {
      status: status({ enabled: true, backupCodesRemaining: 8 }), devices: [],
      regenerate: [{ status: 409, body: { code: 'ERR_MFA_NOT_ENABLED', message: 'Not enabled.' } }],
    });
    await openSecurity(page);
    await card(page).getByRole('button', { name: 'Get new backup codes' }).click();
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(card(page).getByRole('button', { name: 'Get new backup codes' })).toBeFocused();
    await card(page).getByRole('button', { name: 'Get new backup codes' }).click();
    await page.getByLabel('Current 6-digit code from your authenticator app').fill('123456');
    await page.getByRole('button', { name: 'Get new codes' }).click();
    await expect(card(page).getByText('Two-step sign-in is off for your account, so there are no backup codes.')).toBeVisible();
    await expect(codesStep(page)).toHaveCount(0);
  });

  test('a status without a count (an older API) promises nothing', async ({ page }) => {
    await mockApi(page, { status: status({ enabled: true }), devices: [] });
    await openSecurity(page);
    await expect(card(page).getByTestId('backup-codes-remaining')).toHaveCount(0);
    await expect(card(page).getByRole('button', { name: 'Get new backup codes' })).toHaveCount(0);
  });
});

test.describe('sign-in code step: a backup code instead', () => {
  async function reachCodeStep(page: Page) {
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill('Desert-Falcon-2026');
    await page.locator('#li-pw').press('Enter');
    await expect(page.locator('#mfa-code')).toBeVisible();
  }

  test('switching keeps focus on the field, hides the trust choice, and sends backupCode without rememberDevice', async ({ page }, info) => {
    const api = await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challengeReply(30)] });
    await reachCodeStep(page);
    // Ticked before switching: a backup code still never trusts this browser.
    await page.getByTestId('mfa-trust-browser').check();
    await page.getByRole('button', { name: 'Use a backup code instead' }).click();
    const field = page.getByLabel('Backup code', { exact: true });
    await expect(field).toBeFocused();
    await expect(field).toHaveAttribute('type', 'text');
    await expect(field).toHaveAttribute('autocomplete', 'one-time-code');
    await expect(page.getByTestId('mfa-trust-browser')).toHaveCount(0);
    await expect(page.locator('#mfa-code')).toHaveCount(0);
    await expect(page.getByText('Enter one of the backup codes you saved when you turned on two-step sign-in.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Verify' })).toBeDisabled();
    await shot(page, 'login-backup-code-en', info.outputPath.bind(info));

    // And back, from the keyboard: the cursor goes to the field now shown, and the trust choice returns.
    await page.getByRole('button', { name: 'Use the app code instead' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('#mfa-code')).toBeFocused();
    await expect(page.getByTestId('mfa-trust-browser')).toBeVisible();
    await page.getByRole('button', { name: 'Use a backup code instead' }).focus();
    await page.keyboard.press('Enter');
    await expect(field).toBeFocused();

    // As read off paper: lower case, a space instead of the hyphen.
    await field.fill('k7q2m 9xh4p');
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect.poll(() => api.calls.find((c) => c.path === '/api/auth/mfa/challenge/verify')?.body)
      .toEqual({ challengeToken: 'chal-1', backupCode: 'K7Q2M9XH4P' });
    await page.waitForURL(/\/(ess|dashboard)/);
    expect(api.errors).toEqual([]);
  });

  test('a wrong or used backup code, a wrong app code, and a missing code each say so', async ({ page }) => {
    const api = await mockApi(page, {
      status: status({ enabled: true }), devices: [], signedIn: false, login: [challengeReply(0)],
      challengeVerify: [
        { status: 400, body: { code: 'ERR_MFA_CODE_INVALID', message: 'Invalid code.' } },
        { status: 400, body: { code: 'ERR_MFA_CODE_INVALID', message: 'Invalid code.' } },
        { status: 400, body: { code: 'ERR_MFA_CODE_REQUIRED', message: 'Exactly one code.' } },
      ],
    });
    await reachCodeStep(page);
    await page.getByRole('button', { name: 'Use a backup code instead' }).click();
    await page.getByLabel('Backup code', { exact: true }).fill('K7Q2M-9XH4P');
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect(page.locator('.lx-fault')).toHaveText("That backup code didn't work. Check it, or use another one. Each code works once.");
    await page.getByRole('button', { name: 'Use the app code instead' }).click();
    await expect(page.locator('.lx-fault')).toHaveCount(0);
    await page.locator('#mfa-code').fill('123456');
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect(page.locator('.lx-fault')).toHaveText("That code didn't match. Enter the newest code from your authenticator app. Each code works only once.");
    await page.getByRole('button', { name: 'Use a backup code instead' }).click();
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect(page.locator('.lx-fault')).toHaveText('Enter one of your backup codes.');
    const sent = api.calls.filter((c) => c.path === '/api/auth/mfa/challenge/verify').map((c) => c.body);
    expect(sent).toEqual([
      { challengeToken: 'chal-1', backupCode: 'K7Q2M9XH4P' },
      { challengeToken: 'chal-1', totpCode: '123456' },
      { challengeToken: 'chal-1', backupCode: 'K7Q2M9XH4P' },
    ]);
  });

  test('a backup code that is too short is not sent', async ({ page }) => {
    const api = await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, login: [challengeReply(30)] });
    await reachCodeStep(page);
    await page.getByRole('button', { name: 'Use a backup code instead' }).click();
    await page.getByLabel('Backup code', { exact: true }).fill('K7Q2M-9XH');
    await expect(page.getByRole('button', { name: 'Verify' })).toBeDisabled();
    await page.getByLabel('Backup code', { exact: true }).press('Enter');
    expect(api.calls.some((c) => c.path === '/api/auth/mfa/challenge/verify')).toBe(false);
  });
});

test.describe('backup codes: Arabic, dark and phone', () => {
  test('Arabic: the switch, the backup field and the forced-setup codes read right to left', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar', status: status({ enabled: true }), devices: [], signedIn: false, login: [challengeReply(30)] });
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill('Desert-Falcon-2026');
    await page.locator('#li-pw').press('Enter');
    await page.getByRole('button', { name: 'استخدم رمزًا احتياطيًا بدلًا من ذلك' }).click();
    const field = page.getByLabel('الرمز الاحتياطي', { exact: true });
    await expect(field).toBeFocused();
    await expect(field).toHaveAttribute('dir', 'ltr');
    await expect(field).toHaveCSS('text-align', 'center');
    await expect(page.getByRole('button', { name: 'استخدم رمز التطبيق بدلًا من ذلك' })).toBeVisible();
    await shot(page, 'login-backup-code-ar', info.outputPath.bind(info));
  });

  test('Arabic: the codes step on the page', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar', status: status(), devices: [], backupCodes: CODES });
    await page.goto('/account/security');
    await card(page).getByRole('button', { name: 'تفعيل التحقق بخطوتين' }).click({ timeout: 30_000 });
    await page.getByLabel('أدخل الرمز المكوّن من 6 أرقام الذي يعرضه التطبيق').fill('654321');
    await page.getByRole('button', { name: 'تفعيل', exact: true }).click();
    const step = codesStep(page);
    const heading = step.getByRole('heading', { name: 'احفظ رموزك الاحتياطية' });
    await expect(heading).toBeFocused();
    await expect(heading).toHaveCSS('text-transform', 'none');
    await expect(heading).toHaveCSS('letter-spacing', 'normal');
    await expect(step.getByTestId('backup-codes-keep-safe')).toHaveText('يعمل كل رمز مرة واحدة فقط. احتفظ بها في مكان آمن بعيدًا عن هاتفك.');
    await expect(step.getByTestId('backup-codes-list').locator('li').first()).toHaveAttribute('dir', 'ltr');
    await expect(step.getByTestId('backup-codes-copy')).toHaveText('نسخ الكل');
    await expect(step.getByRole('checkbox', { name: 'حفظتُ هذه الرموز' })).toBeVisible();
    await expect(page.locator('main')).not.toContainText('الثنائي');
    await shot(page, 'codes-step-page-ar', info.outputPath.bind(info));
  });

  test('Arabic: forced setup codes on the sign-in card', async ({ page }, info) => {
    await mockApi(page, { locale: 'ar', status: status(), devices: [], signedIn: false, backupCodes: CODES, login: [ENROLMENT] });
    await enrolAtSignIn(page);
    await expect(page.locator('.lx-card')).toHaveAttribute('dir', 'rtl');
    await expect(codesStep(page).getByRole('heading', { name: 'احفظ رموزك الاحتياطية' })).toBeFocused();
    await shot(page, 'codes-step-login-ar', info.outputPath.bind(info));
  });

  test('dark: the codes step and Get new backup codes', async ({ page }, info) => {
    await mockApi(page, { theme: 'dark', status: status({ enabled: true, backupCodesRemaining: 2 }), devices: [] });
    await openSecurity(page);
    await expect(page.locator('html')).toHaveClass(/dark/);
    await shot(page, 'card-low-codes-dark', info.outputPath.bind(info));
    await card(page).getByRole('button', { name: 'Get new backup codes' }).click();
    await page.getByLabel('Current 6-digit code from your authenticator app').fill('123456');
    await page.getByRole('button', { name: 'Get new codes' }).click();
    await expect(codesStep(page).getByTestId('backup-codes-list').locator('li')).toHaveText(NEW_CODES);
    await shot(page, 'codes-step-page-dark', info.outputPath.bind(info));
  });

  test('390px: the codes step fits on the page and on the sign-in card', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { status: status(), devices: [], backupCodes: CODES });
    await turnOnFromPage(page);
    await expect(codesStep(page).getByTestId('backup-codes-list').locator('li')).toHaveCount(10);
    expect(await codesStep(page).getByTestId('backup-codes-list').evaluate((el) => getComputedStyle(el).gridTemplateColumns.split(' ').length)).toBe(2);
    await expectNoHorizontalScroll(page);
    await shot(page, 'codes-step-page-390', info.outputPath.bind(info));
  });

  test('390px: the forced-setup codes fit on the sign-in card', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page, { status: status(), devices: [], signedIn: false, backupCodes: CODES, login: [ENROLMENT] });
    await enrolAtSignIn(page);
    await expectNoHorizontalScroll(page);
    // Every code is fully inside the card: none clipped at phone width.
    const cardBox = (await page.locator('.lx-card').boundingBox())!;
    for (const box of await Promise.all((await codesStep(page).getByTestId('backup-codes-list').locator('li').all()).map((li) => li.boundingBox()))) {
      expect(box!.x).toBeGreaterThanOrEqual(cardBox.x);
      expect(box!.x + box!.width).toBeLessThanOrEqual(cardBox.x + cardBox.width + 0.5);
    }
    await shot(page, 'codes-step-login-390', info.outputPath.bind(info));
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
    await expect(page.getByTestId('two-step-required-reason')).toHaveText('مطلوب لدورك، لأنك تستطيع الاطلاع على الرواتب أو البيانات المصرفية أو صلاحيات الوصول أو تغييرها.');
    await expect(page.getByRole('heading', { name: 'المتصفحات الموثوقة' })).toBeVisible();
    await expect(page.getByTestId('this-browser-badge')).toHaveText('هذا المتصفح');
    // "Forget" is «إلغاء الوثوق» (stop trusting), never «نسيان».
    await expect(page.getByTestId('forget-browser').first()).toHaveText('إلغاء الوثوق');
    await expect(page.getByTestId('forget-all-browsers')).toHaveText('إلغاء الوثوق بكل المتصفحات');
    await expect(page.locator('main')).not.toContainText('نسيان');
    await expect(page.locator('main')).not.toContainText('الثنائي');
    // The Arabic eyebrow: no capitals or tracking, and at least 13px.
    const eyebrow = page.getByText('حسابي', { exact: true });
    await expect(eyebrow).toHaveCSS('letter-spacing', 'normal');
    await expect(eyebrow).toHaveCSS('text-transform', 'none');
    expect(parseFloat(await eyebrow.evaluate((el) => getComputedStyle(el).fontSize))).toBeGreaterThanOrEqual(13);
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
    // A full 44px touch row in the drawer, in readable 13px type.
    const entry = page.getByTestId('sidebar-sign-in-security');
    expect((await entry.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await expect(entry).toHaveCSS('font-size', '13px');
    await entry.click();
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

/**
 * The review set: every state of the page and the code step, in English, Arabic, dark and at 390px.
 * Runs only when SIGN_IN_SECURITY_SHOTS names a folder, once (desktop project), so CI pays nothing.
 */
test.describe('review screenshots', () => {
  type Variant = { name: string; locale?: 'ar'; theme?: 'dark'; phone?: boolean };
  const variants: Variant[] = [
    { name: 'en' }, { name: 'ar', locale: 'ar' }, { name: 'dark', theme: 'dark' }, { name: '390', phone: true },
  ];
  const states: { name: string; world: () => Omit<World, 'locale' | 'theme'>; act?: (page: Page) => Promise<void> }[] = [
    { name: 'off', world: () => ({ status: status(), devices: [] }) },
    { name: 'setup', world: () => ({ status: status(), devices: [] }), act: async (page) => {
      await page.getByTestId('two-step-turn-on').click();
      await expect(page.getByTestId('two-step-qr')).toBeVisible();
    } },
    { name: 'on-trusted', world: () => ({ status: status({ enabled: true, trustedDeviceCount: 3 }), devices: [...DEVICES, THIRD] }) },
    { name: 'disable', world: () => ({ status: status({ enabled: true, trustedDeviceCount: 1 }), devices: [DEVICES[0]] }), act: async (page) => {
      await page.getByTestId('two-step-turn-off').click();
      await expect(page.getByTestId('two-step-disable-heading')).toBeFocused();
    } },
    { name: 'required-role', world: () => ({ privileged: true, status: status({ enabled: true, required: true, requiredBecause: 'privileged_role', trustedDeviceCount: 2 }), devices: [...DEVICES] }) },
    { name: 'required-workspace', world: () => ({ status: status({ enabled: false, required: true, requiredBecause: 'workspace_policy' }), devices: [] }) },
    { name: 'empty-trusted', world: () => ({ status: status({ enabled: true }), devices: [] }) },
    { name: 'trust-off-by-company', world: () => ({ status: status({ enabled: true, rememberDeviceDays: 0 }), devices: [] }) },
  ];

  for (const v of variants) {
    test(`code step, ${v.name}`, async ({ page }, info) => {
      test.skip(!SHOTS || info.project.name !== 'desktop', 'review shots only');
      if (v.phone) await page.setViewportSize({ width: 390, height: 844 });
      await mockApi(page, { status: status({ enabled: true }), devices: [], signedIn: false, locale: v.locale, theme: v.theme,
        login: [{ status: 200, body: { mfaRequired: true, challengeToken: 'chal-1', expiresInSeconds: 300, rememberDeviceDays: 30 } }] });
      await page.goto('/login');
      await page.locator('#li-em').fill(EMAIL);
      await page.locator('#li-pw').fill('Desert-Falcon-2026');
      await page.locator('#li-pw').press('Enter');
      await expect(page.locator('#mfa-code')).toBeVisible();
      await page.getByTestId('mfa-trust-browser').check();
      await page.getByTestId('mfa-lost-phone').click();
      await expect(page.getByTestId('mfa-lost-phone-help')).toBeVisible();
      await page.locator('#mfa-code').fill('12');
      await shot(page, `login-code-${v.name}`, info.outputPath.bind(info));
    });

    for (const st of states) {
      test(`page ${st.name}, ${v.name}`, async ({ page }, info) => {
        test.skip(!SHOTS || info.project.name !== 'desktop', 'review shots only');
        if (v.phone) await page.setViewportSize({ width: 390, height: 844 });
        await mockApi(page, { ...st.world(), locale: v.locale, theme: v.theme });
        await page.goto('/account/security');
        await expect(card(page).getByTestId('two-step-status')).toBeVisible({ timeout: 30_000 });
        if (st.world().devices.length) await expect(page.getByTestId('trusted-browser-row').first()).toBeVisible();
        if (st.act) await st.act(page);
        await expectNoHorizontalScroll(page);
        await shot(page, `page-${st.name}-${v.name}`, info.outputPath.bind(info));
      });
    }
  }
});

test.describe('Sign-in security: the company lets each person choose', () => {
  // Policy "Optional": the API reports required=false even for a payroll login, so the buttons appear.
  const optionalStatus = (over: Partial<Status> = {}) => ({ ...status(over), policy: 'Optional' }) as Status;

  test('a privileged person who is enrolled can turn it off with a backup code, after confirming', async ({ page }, info) => {
    const api = await mockApi(page, { status: optionalStatus({ enabled: true, backupCodesRemaining: 8 }), devices: [], privileged: true });
    await openSecurity(page);
    await expect(card(page).getByTestId('two-step-optional-note')).toHaveText('Your company lets each person choose whether to use two-step sign-in.');
    await expect(card(page).getByTestId('two-step-required-reason')).toHaveCount(0);
    await shot(page, 'account-security-optional-on-en', info.outputPath.bind(info));
    await card(page).getByTestId('two-step-turn-off').click();
    const submit = page.getByRole('button', { name: 'Turn off', exact: true });
    await page.getByTestId('two-step-disable-switch').click();
    await expect(page.getByRole('button', { name: 'Use the app code instead' })).toBeVisible();
    await page.getByTestId('two-step-disable-backup').fill(CODES[0]);
    await expect(submit).toBeDisabled();                       // not until it is confirmed
    await page.getByRole('checkbox').check();
    await shot(page, 'account-security-optional-disable-backup-en', info.outputPath.bind(info));
    await submit.click();
    await expect.poll(() => api.calls.find((c) => c.path === '/api/auth/mfa/disable')?.body).toEqual({ backupCode: CODES[0] });
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is off');
    await expect(card(page).getByTestId('two-step-turn-on')).toBeVisible();
    expect(api.errors).toEqual([]);
  });

  test('with the app code the body is totpCode; a wrong backup code is explained', async ({ page }) => {
    const api = await mockApi(page, {
      status: optionalStatus({ enabled: true, backupCodesRemaining: 10 }), devices: [], privileged: true,
      disable: [{ status: 400, body: { code: 'ERR_MFA_CODE_INVALID', message: 'no' } }, { status: 204 }],
    });
    await openSecurity(page);
    await card(page).getByTestId('two-step-turn-off').click();
    await page.getByTestId('two-step-disable-switch').click();
    await page.getByTestId('two-step-disable-backup').fill('AAAAA-BBBBB');
    await page.getByRole('checkbox').check();
    await page.getByRole('button', { name: 'Turn off', exact: true }).click();
    await expect(page.getByText("That backup code didn't work. Check it, or use another one. Each code works once.")).toBeVisible();
    await page.getByTestId('two-step-disable-switch').click();
    await page.getByLabel('Current 6-digit code from your authenticator app').fill('123456');
    await page.getByRole('button', { name: 'Turn off', exact: true }).click();
    await expect.poll(() => api.calls.filter((c) => c.path === '/api/auth/mfa/disable').at(-1)?.body).toEqual({ totpCode: '123456' });
  });

  test('a person who is not enrolled can turn it on', async ({ page }) => {
    await mockApi(page, { status: optionalStatus(), devices: [], privileged: true });
    await openSecurity(page);
    await expect(card(page).getByTestId('two-step-status')).toHaveText('Two-step sign-in is off');
    await expect(card(page).getByTestId('two-step-turn-on')).toBeVisible();
    await expect(card(page).getByTestId('two-step-turn-off')).toHaveCount(0);
  });

  test('Arabic: the choice note and the backup-code switch are translated', async ({ page }) => {
    await mockApi(page, { status: optionalStatus({ enabled: true, backupCodesRemaining: 5 }), devices: [], locale: 'ar' });
    await openSecurity(page);
    await expect(card(page).getByTestId('two-step-optional-note')).toHaveText('تتيح شركتك لكل شخص أن يختار استخدام التحقق بخطوتين أو عدم استخدامه.');
    await card(page).getByTestId('two-step-turn-off').click();
    await expect(page.getByTestId('two-step-disable-switch')).toHaveText('استخدم رمزًا احتياطيًا بدلًا من ذلك');
  });

  test('when it is required, today\'s text stays and there is no off button', async ({ page }) => {
    await mockApi(page, {
      status: status({ enabled: true, required: true, requiredBecause: 'privileged_role', backupCodesRemaining: 10 }),
      devices: [], privileged: true,
    });
    await openSecurity(page);
    await expect(card(page).getByTestId('two-step-required-reason')).toContainText(ROLE_REASON);
    await expect(card(page).getByTestId('two-step-turn-off')).toHaveCount(0);
    await expect(card(page).getByTestId('two-step-optional-note')).toHaveCount(0);
  });
});
