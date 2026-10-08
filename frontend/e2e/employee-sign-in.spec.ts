import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * Employee sign-in: /login without a workspace, and /welcome (first sign-in with HR's welcome code).
 *
 * Over mocked API routes shaped exactly as the employee-access contract (§4, Amendments 2-3): login
 * with an optional tenantSlug and `workspace_required`; `POST /api/auth/welcome/redeem` answering
 * 200 `{ tenantSlug }` or 400 `{ code }`; `GET /api/auth/password-policy`. The server's rules are the
 * backend suite's job; these prove the screens: what is asked, what is sent, where the code goes
 * (never into a URL that survives), and what each refusal says.
 */

const EMAIL = 'noah.williams@evostel.com';
const CODE = '48217730';
const NEW_PASSWORD = 'Desert-Falcon-2026';

interface Call { path: string; body: any }

interface MockOptions {
  /** Redeem answers, in order; the last repeats. Default: 200 { tenantSlug: 'evostel' }. */
  redeem?: Array<{ status: number; body?: unknown }>;
  /** Login answers, in order; the last repeats. Default: an authenticated session. */
  login?: Array<{ status: number; body?: unknown }>;
  minLength?: number;
  locale?: 'en' | 'ar';
}

const session = (extra: Record<string, unknown> = {}) => ({
  accessToken: 'fixture-access', refreshToken: 'fixture-refresh', expiresAtUtc: '2026-10-08T00:00:00Z',
  user: {
    id: 'u-42', tenantId: 't-1', tenantSlug: 'evostel', email: EMAIL, fullName: 'Noah Williams',
    roles: ['Employee'], permissions: ['dashboard.read', 'profile.read', 'ess.read', 'ess.write', 'performance.read', 'loans.self'],
    employeeId: 42, accessMode: 'FullPortal', ...extra,
  },
});

async function mockApi(page: Page, opts: MockOptions = {}) {
  const calls: Call[] = [];
  const errors: string[] = [];
  const urls: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('framenavigated', (frame) => { if (frame === page.mainFrame()) urls.push(frame.url()); });
  if (opts.locale) {
    await page.addInitScript((locale) => localStorage.setItem('kynexone-locale-choice-v2', locale), opts.locale);
  }
  const redeem = [...(opts.redeem ?? [{ status: 200, body: { tenantSlug: 'evostel' } }])];
  const login = [...(opts.login ?? [{ status: 200, body: session() }])];
  const next = (list: Array<{ status: number; body?: unknown }>) => (list.length > 1 ? list.shift()! : list[0]);

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const json = (body: unknown, status = 200) =>
      route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body ?? {}) });
    const body = request.method() === 'GET' ? Object.fromEntries(url.searchParams) : request.postDataJSON();
    calls.push({ path: url.pathname, body });

    if (url.pathname === '/api/auth/welcome/redeem') { const r = next(redeem); return json(r.body, r.status); }
    if (url.pathname === '/api/auth/login') { const r = next(login); return json(r.body, r.status); }
    if (url.pathname === '/api/auth/password-policy') return json({ minLength: opts.minLength ?? 10 });
    if (url.pathname === '/api/auth/me') return json(session().user);
    if (url.pathname === '/api/auth/logout') return route.fulfill({ status: 204, body: '' });
    // Whatever the employee home asks for once signed in: empty, never a real record.
    return json(request.method() === 'GET' ? [] : {});
  });
  return { calls, errors, urls };
}

const paths = (calls: Call[]) => calls.map((c) => c.path).filter((p) => p.startsWith('/api/auth/') && p !== '/api/auth/password-policy');
const passwordBox = (page: Page) => page.getByLabel('Choose a password');
/** The card's error line (Next's dev route announcer is also role=alert, so not by role). */
const fault = (page: Page) => page.locator('.lx-fault');

test.use({ storageState: { cookies: [], origins: [] } });

test.describe('/welcome from the slip QR', () => {
  test('reads the fragment, scrubs it, asks only for a password, then redeems, signs in and lands on /ess', async ({ page }) => {
    const api = await mockApi(page);
    await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=4821%207730&w=evostel`);

    await expect(page.getByTestId('welcome-email')).toHaveText(EMAIL);
    await expect(page, 'the code must leave the address bar at once').toHaveURL(/\/welcome$/);
    expect(await page.evaluate(() => window.location.hash)).toBe('');
    // Only one thing is asked: no email box, no code box, no confirm box.
    await expect(page.locator('#wc-email')).toHaveCount(0);
    await expect(page.locator('#wc-code')).toHaveCount(0);
    await expect(page.locator('input[autocomplete="new-password"]')).toHaveCount(1);
    await expect(page.getByText(CODE)).toHaveCount(0);

    // Shown by default, with an eye toggle to hide it.
    await expect(passwordBox(page)).toHaveAttribute('type', 'text');
    await page.getByTestId('welcome-password-toggle').click();
    await expect(passwordBox(page)).toHaveAttribute('type', 'password');
    await page.getByTestId('welcome-password-toggle').click();

    const save = page.getByRole('button', { name: 'Save password and sign in' });
    await passwordBox(page).fill('Noah2026xyz');
    await expect(page.getByTestId('tick-length')).toHaveAttribute('data-ok', 'true');
    await expect(page.getByTestId('tick-personal')).toHaveAttribute('data-ok', 'false');
    await expect(save).toBeDisabled();
    await passwordBox(page).fill('short');
    await expect(page.getByTestId('tick-length')).toHaveAttribute('data-ok', 'false');
    await passwordBox(page).fill(NEW_PASSWORD);
    await expect(page.getByTestId('tick-length')).toHaveAttribute('data-ok', 'true');
    await expect(page.getByTestId('tick-personal')).toHaveAttribute('data-ok', 'true');

    await save.click();
    await page.waitForURL(/\/ess/);
    expect(paths(api.calls).slice(0, 2)).toEqual(['/api/auth/welcome/redeem', '/api/auth/login']);
    const redeemed = api.calls.find((c) => c.path === '/api/auth/welcome/redeem')!.body;
    expect(redeemed).toEqual({ email: EMAIL, code: CODE, newPassword: NEW_PASSWORD, tenantSlug: 'evostel' });
    const login = api.calls.find((c) => c.path === '/api/auth/login')!.body;
    expect(login).toEqual({ email: EMAIL, password: NEW_PASSWORD, tenantSlug: 'evostel' });
    // The code never sat in any URL the page navigated to after the first load, nor in storage.
    expect(api.urls.slice(1).some((u) => u.includes(CODE))).toBe(false);
    const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }));
    expect(stored).not.toContain(CODE);
  });

  test('"Not your email?" starts over with empty fields', async ({ page }) => {
    await mockApi(page);
    await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}`);
    await page.getByTestId('welcome-not-me').click();
    await expect(page.locator('#wc-email')).toHaveValue('');
    await expect(page.locator('#wc-code')).toHaveValue('');
  });

  test('the company password policy sets the length tick', async ({ page }) => {
    await mockApi(page, { minLength: 12 });
    await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}&w=evostel`);
    await expect(page.getByTestId('tick-length')).toHaveText('At least 12 characters');
    await passwordBox(page).fill('Eleven-char');
    await expect(page.getByTestId('tick-length')).toHaveAttribute('data-ok', 'false');
  });
});

test.describe('/welcome typed by hand', () => {
  test('email and a code typed with Arabic-Indic digits and a space are sent as eight ASCII digits', async ({ page }) => {
    const api = await mockApi(page);
    await page.goto('/welcome');
    const code = page.getByLabel('Welcome code');
    await expect(code).toHaveAttribute('inputmode', 'numeric');
    await expect(code).toHaveAttribute('autocomplete', 'one-time-code');
    await expect(code).toHaveAttribute('dir', 'ltr');
    await expect(page.getByLabel('Work email')).toHaveAttribute('dir', 'ltr');

    await page.getByLabel('Work email').fill(EMAIL);
    await code.fill('٤٨٢١ ٧٧٣٠');
    await page.getByRole('button', { name: 'Continue' }).click();
    await passwordBox(page).fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Save password and sign in' }).click();
    await page.waitForURL(/\/ess/);
    const redeemed = api.calls.find((c) => c.path === '/api/auth/welcome/redeem')!.body;
    expect(redeemed).toEqual({ email: EMAIL, code: CODE, newPassword: NEW_PASSWORD });
    // The server named the company; the follow-up sign-in uses it.
    expect(api.calls.find((c) => c.path === '/api/auth/login')!.body.tenantSlug).toBe('evostel');
  });

  test('a code that is not eight digits is caught before anything is sent', async ({ page }) => {
    const api = await mockApi(page);
    await page.goto('/welcome');
    await page.getByLabel('Work email').fill(EMAIL);
    await page.getByLabel('Welcome code').fill('4821 773');
    await page.getByRole('button', { name: 'Continue' }).click();
    await expect(fault(page)).toHaveText('The welcome code is 8 digits. Check your sign-in slip.');
    expect(paths(api.calls)).toEqual([]);
  });
});

const REFUSALS: Array<{ name: string; status: number; body?: unknown; message: string }> = [
  { name: 'code_expired', status: 400, body: { code: 'code_expired' }, message: 'This code no longer works. It may have expired or already been used. Ask HR for a new one.' },
  { name: 'code_used', status: 400, body: { code: 'code_used' }, message: 'This code no longer works. It may have expired or already been used. Ask HR for a new one.' },
  { name: 'password_policy (server text never shown)', status: 400, body: { code: 'password_policy', message: 'SERVER TEXT: needs a symbol' }, message: "Your password doesn't meet the rules above." },
  { name: 'try_later', status: 400, body: { code: 'try_later' }, message: 'Too many tries. Wait a few minutes and try again.' },
  { name: 'HTTP 429', status: 429, body: { error: 'rate_limited' }, message: 'Too many tries. Wait a few minutes and try again.' },
  { name: 'seat_limit', status: 400, body: { code: 'seat_limit' }, message: "Your company's KynexOne plan is full. Ask HR." },
  { name: 'sign_out_first', status: 400, body: { code: 'sign_out_first' }, message: 'Sign out of KynexOne on this device first.' },
  { name: 'an unknown refusal', status: 400, body: { code: 'something_new', message: 'SERVER TEXT' }, message: 'Something went wrong. Please try again.' },
];

test.describe('/welcome refusals, each in plain words', () => {
  for (const refusal of REFUSALS) {
    test(refusal.name, async ({ page }) => {
      const api = await mockApi(page, { redeem: [{ status: refusal.status, body: refusal.body }] });
      await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}`);
      await passwordBox(page).fill(NEW_PASSWORD);
      await page.getByRole('button', { name: 'Save password and sign in' }).click();
      await expect(fault(page)).toHaveText(refusal.message);
      await expect(page.getByText('SERVER TEXT')).toHaveCount(0);
      expect(paths(api.calls), 'a refused code never leads to a sign-in').toEqual(['/api/auth/welcome/redeem']);
      if (refusal.name === 'sign_out_first') await expect(page.getByTestId('welcome-sign-out')).toBeVisible();
    });
  }

  test('code_invalid shows the email and code again to check against the slip', async ({ page }) => {
    await mockApi(page, { redeem: [{ status: 400, body: { code: 'code_invalid' } }] });
    await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}`);
    await passwordBox(page).fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Save password and sign in' }).click();
    await expect(fault(page)).toHaveText("Email and code don't match. Check both on your sign-in slip.");
    await expect(page.getByLabel('Work email')).toHaveValue(EMAIL);
    await expect(page.getByLabel('Welcome code')).toHaveValue('4821 7730');
  });

  test('workspace_required asks for the company ID and sends it on the retry', async ({ page }) => {
    const api = await mockApi(page, { redeem: [{ status: 400, body: { code: 'workspace_required' } }, { status: 200, body: { tenantSlug: 'evostel-ksa' } }] });
    await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}`);
    await passwordBox(page).fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Save password and sign in' }).click();
    await expect(fault(page)).toHaveText("We couldn't find your company from your email. Enter your company ID. HR can tell you what it is.");
    await page.getByLabel('Company ID').fill('Evostel-KSA');
    await page.getByRole('button', { name: 'Save password and sign in' }).click();
    await page.waitForURL(/\/ess/);
    const redeems = api.calls.filter((c) => c.path === '/api/auth/welcome/redeem');
    expect(redeems[1].body.tenantSlug).toBe('evostel-ksa');
  });

  test('a lost reply is settled by signing in, not by replaying the code', async ({ page }) => {
    const api = await mockApi(page, { redeem: [{ status: 502, body: {} }] });
    await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}`);
    await passwordBox(page).fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Save password and sign in' }).click();
    await page.waitForURL(/\/ess/);
    expect(paths(api.calls).slice(0, 2)).toEqual(['/api/auth/welcome/redeem', '/api/auth/login']);
  });
});

test.describe('/welcome in Arabic', () => {
  test('right-to-left page, left-to-right email and code, copy-deck wording', async ({ page }) => {
    await mockApi(page, { locale: 'ar' });
    await page.goto('/welcome');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('مرحباً');
    await expect(page.getByLabel('رمز التفعيل')).toHaveAttribute('dir', 'ltr');
    await expect(page.getByLabel('البريد الإلكتروني للعمل').or(page.getByLabel('البريد الإلكتروني الوظيفي'))).toHaveAttribute('dir', 'ltr');
    await expect(page.getByText('نسيت كلمة المرور؟ اطلب رمز تفعيل جديد من الموارد البشرية.')).toBeVisible();

    await page.getByTestId('signin-lang-en').click();
    await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Welcome');
  });

  test('the email is an isolated LTR run, and Arabic letters in the password get a hint', async ({ page }) => {
    await mockApi(page, { locale: 'ar' });
    await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}`);
    await expect(page.getByTestId('welcome-email')).toHaveAttribute('dir', 'ltr');
    await page.getByLabel('اختر كلمة المرور').fill('كلمةسرطويلة2026');
    await expect(page.getByTestId('welcome-arabic-hint')).toHaveText('استخدم أحرفاً إنجليزية ليسهل الدخول من أي لوحة مفاتيح.');
  });
});

test.describe('/login for employees', () => {
  test('asks for email and password only, sends no workspace, and lands an employee on Self-Service', async ({ page }) => {
    const api = await mockApi(page);
    await page.goto('/login');
    await expect(page.locator('#li-ws')).toHaveCount(0);
    await expect(page.getByText(/tenant isolation|audit trail/i)).toHaveCount(0);
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/ess/);
    expect(api.calls.find((c) => c.path === '/api/auth/login')!.body).toEqual({ email: EMAIL, password: NEW_PASSWORD });
  });

  test('shows Company ID only after the server asks for it', async ({ page }) => {
    const api = await mockApi(page, { login: [{ status: 400, body: { code: 'workspace_required' } }, { status: 200, body: session() }] });
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.locator('#li-ws')).toBeVisible();
    await expect(page.locator('#li-ws')).toBeFocused();
    await expect(fault(page)).toHaveText("We couldn't find your company from your email. Enter your company ID. HR can tell you what it is.");
    await page.locator('#li-ws').fill('Evostel');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/ess/);
    const logins = api.calls.filter((c) => c.path === '/api/auth/login');
    expect(logins.map((c) => c.body.tenantSlug)).toEqual([undefined, 'evostel']);
  });

  test('a ?workspace= link still shows and sends the company ID', async ({ page }) => {
    const api = await mockApi(page);
    await page.goto('/login?workspace=Evostel');
    await expect(page.locator('#li-ws')).toHaveValue('evostel');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/ess/);
    expect(api.calls.find((c) => c.path === '/api/auth/login')!.body.tenantSlug).toBe('evostel');
  });

  test('a welcome code typed as the password goes to /welcome after the sign-in fails, in memory only', async ({ page }) => {
    const api = await mockApi(page, { login: [{ status: 401, body: { message: 'Invalid credentials' } }, { status: 200, body: session() }] });
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill('٤٨٢١-٧٧٣٠');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/welcome$/);
    await expect(page.locator('.lx-ok')).toHaveText("That looks like a welcome code. Let's set your password.");
    await expect(page.getByTestId('welcome-email')).toHaveText(EMAIL);
    // It tried to sign in first (an 8-digit password is still a password).
    expect(paths(api.calls)).toEqual(['/api/auth/login']);
    expect(api.urls.some((u) => u.includes(CODE) || u.includes('#'))).toBe(false);

    await passwordBox(page).fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Save password and sign in' }).click();
    await page.waitForURL(/\/ess/);
    expect(api.calls.find((c) => c.path === '/api/auth/welcome/redeem')!.body.code).toBe(CODE);
  });

  test('"First time? Use your welcome code" opens /welcome with the email filled in', async ({ page }) => {
    await mockApi(page);
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.getByRole('button', { name: 'First time? Use your welcome code' }).click();
    await page.waitForURL(/\/welcome$/);
    await expect(page.getByLabel('Work email')).toHaveValue(EMAIL);
    await expect(page.getByLabel('Welcome code')).toHaveValue('');
  });

  test('a live reset code is reported after sign-in, before going on', async ({ page }) => {
    await mockApi(page, { login: [{ status: 200, body: session({ pendingResetNotice: { date: '2026-10-05T09:00:00Z' } }) }] });
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByTestId('login-reset-notice')).toContainText(
      "HR gave you a new sign-in code on 5 October 2026. If you didn't ask for it, tell HR.");
    await page.getByRole('button', { name: 'Continue' }).click();
    await page.waitForURL(/\/ess/);
  });

  test('Arabic sign-in card with the language switch', async ({ page }) => {
    await mockApi(page, { locale: 'ar' });
    await page.goto('/login');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('تسجيل الدخول');
    await expect(page.getByRole('button', { name: 'أول مرة؟ استخدم رمز التفعيل' })).toBeVisible();
    await expect(page.locator('#li-em')).toHaveAttribute('dir', 'ltr');
  });
});

test.describe('where sign-in lands', () => {
  test('HR keeps landing on the dashboard', async ({ page }) => {
    await mockApi(page, { login: [{ status: 200, body: session({ roles: ['HR Manager'], permissions: ['dashboard.read', 'ess.read', 'employees.read', 'employees.write'] }) }] });
    await page.goto('/login');
    await page.locator('#li-em').fill('sara.ali@evostel.com');
    await page.locator('#li-pw').fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/dashboard/);
  });

  test('a manager with approvals is not treated as employee-only', async ({ page }) => {
    await mockApi(page, { login: [{ status: 200, body: session({ permissions: ['dashboard.read', 'ess.read', 'ess.write', 'manager.read', 'leave.approve'] }) }] });
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/dashboard/);
  });

  test('an explicit ?from= still wins', async ({ page }) => {
    await mockApi(page);
    await page.goto('/login?from=%2Fess%2Fleave');
    await page.locator('#li-em').fill(EMAIL);
    await page.locator('#li-pw').fill(NEW_PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/ess\/leave/);
  });
});

test.describe('device language on an Arabic phone', () => {
  test.use({ locale: 'ar-SA' });

  test('the slip QR opens /welcome in Arabic with no choice made', async ({ page }) => {
    await mockApi(page);
    await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}`);
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('مرحباً');
    await expect(page.getByTestId('welcome-email')).toHaveText(EMAIL);
  });

  test('an explicit English choice wins over the device', async ({ page }) => {
    await mockApi(page, { locale: 'en' });
    await page.goto('/welcome');
    await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Welcome');
  });
});

test.describe('forgot password with no email delivery', () => {
  test('points to HR for a new welcome code instead of promising an email', async ({ page }) => {
    await mockApi(page);
    await page.route('**/api/auth/forgot-password', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ message: 'SERVER TEXT', emailDeliveryConfigured: false }),
    }));
    await page.goto('/login');
    await page.locator('#li-em').fill(EMAIL);
    await page.getByTestId('login-forgot').click();
    await page.getByRole('button', { name: 'Send reset link' }).click();
    await expect(page.getByTestId('forgot-ask-hr')).toHaveText('Forgot your password? Ask HR for a new welcome code.');
    await expect(page.getByText('a reset link is on its way')).toHaveCount(0);
    await expect(page.getByText('SERVER TEXT')).toHaveCount(0);
  });
});

test.describe('app shell: a reset code issued while already signed in', () => {
  async function signedIn(page: Page, notice: { date: string } | null) {
    await page.addInitScript(() => {
      localStorage.setItem('zayra_access_token', 'fixture-access');
      localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    });
    await page.route('**/api/**', async (route: Route) => {
      const url = new URL(route.request().url());
      const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
      // A user the shell renders without Self-Service data (this mock answers every list with []),
      // so the page under the banner is the shell's own access screen, not a half-mocked /ess.
      if (url.pathname === '/api/auth/me') return json({ ...session().user, permissions: ['ess.self'], pendingResetNotice: notice });
      if (url.pathname === '/api/auth/mfa/status') return json({ enabled: false, required: false, promptToEnroll: false });
      return json(route.request().method() === 'GET' ? [] : {});
    });
  }

  test('shows the notice from /api/auth/me and hides it for this session when dismissed', async ({ page }) => {
    await signedIn(page, { date: '2026-10-05T09:00:00Z' });
    await page.goto('/ess');
    const notice = page.getByTestId('reset-code-notice');
    await expect(notice).toContainText("HR gave you a new sign-in code on");
    await expect(notice).toContainText("If you didn't ask for it, tell HR.");
    await expect(notice).toContainText('2026');
    await notice.getByRole('button', { name: 'Hide this message' }).click();
    await expect(notice).toHaveCount(0);
    await page.reload();
    await expect(page.locator('main')).toBeVisible();
    await expect(page.getByTestId('reset-code-notice')).toHaveCount(0);
  });

  test('shows nothing when there is no live reset code', async ({ page }) => {
    await signedIn(page, null);
    await page.goto('/ess');
    await expect(page.locator('main')).toBeVisible();
    await expect(page.getByTestId('reset-code-notice')).toHaveCount(0);
  });
});

/** Screenshots for review: only when SIGNIN_SHOTS names a directory. */
test('screenshots', async ({ page }, info) => {
  const dir = process.env.SIGNIN_SHOTS;
  test.skip(!dir, 'set SIGNIN_SHOTS=<dir> to capture');
  fs.mkdirSync(dir!, { recursive: true });
  const shot = (name: string) => page.screenshot({ path: path.join(dir!, `${info.project.name}-${name}.png`), fullPage: true });
  await mockApi(page);
  await page.goto('/login');
  await expect(page.locator('#li-em')).toBeVisible();
  await shot('login-en');
  await page.goto(`/welcome#e=${encodeURIComponent(EMAIL)}&c=${CODE}`);
  await passwordBox(page).fill('Noah2026xyz');
  await shot('welcome-qr-en');
  await page.getByTestId('signin-lang-ar').click();
  await page.getByLabel('اختر كلمة المرور').fill('كلمةسر');
  await shot('welcome-qr-ar');
  await page.goto('/welcome');
  await shot('welcome-manual-ar');
  await page.goto('/login');
  await shot('login-ar');
});
