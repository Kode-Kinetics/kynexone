import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * /login while the frontend runs AHEAD of the backend (Vercel deploys main at once; the API waits
 * for approval), against a mocked API.
 *
 * This build leaves the company ID out of sign-in and password reset and lets the server find the
 * company from the email. The API before employee access still declares `TenantSlug` required, so a
 * request without it is a standard ASP.NET validation problem (400, `errors.TenantSlug`, no `code`).
 * The page must read that as "the company ID is needed", show the field, and sign in with it.
 * Also: the "Sign in with Company ID" link for someone whose email domain belongs to another company,
 * and the device language applying on /login but not once signed in.
 *
 * Run: npx playwright test -c e2e/playwright.ess-workspace.config.ts login-workspace-skew
 */

const EMAIL = 'noah.williams@evostel.com';
const PASSWORD = 'Desert-Falcon-2026';
const WORKSPACE = 'evostel';
const NEEDS_WORKSPACE = "We couldn't find your company from your email. Enter your company ID. HR can tell you what it is.";

/** Exactly what the pre-employee-access API answers when `[param: RequiredWorkspace] TenantSlug` is missing. */
const oldValidationProblem = (key = 'TenantSlug') => ({
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
  title: 'One or more validation errors occurred.',
  status: 400,
  errors: { [key]: ['Workspace is required.'] },
  traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00',
});

const user = {
  id: 'u-42', tenantId: 't-1', tenantSlug: WORKSPACE, email: EMAIL, fullName: 'Noah Williams',
  roles: ['Employee'], permissions: ['dashboard.read', 'profile.read', 'ess.read', 'ess.write', 'performance.read', 'loans.self'],
  employeeId: 42, accessMode: 'FullPortal',
};
/** The smallest Self-Service home that renders: where an employee lands once signed in. */
const essDashboard = {
  profile: { employeeId: 42, employeeCode: 'EMP-42', fullName: 'Noah Williams', jobTitle: 'Analyst', department: 'Finance', profilePhotoUrl: '', profileCompletenessScore: 80 },
  attendanceToday: null, leaveBalances: [], pendingRequests: 0, documentAlerts: [], announcements: [], notifications: [],
  actionItems: [], payrollSnapshot: null, loansSummary: null, loanSummaries: [], performanceSnapshot: null,
  overtimeHoursThisMonth: 0, nextApprovedLeave: null, tenureMonths: 12,
};
const session = { accessToken: 'fixture-access', refreshToken: 'fixture-refresh', expiresAtUtc: '2026-10-09T00:00:00Z', user };

interface Call { path: string; body: any }

interface MockOptions {
  /** How the API answers a sign-in or reset WITHOUT a company ID. Default: the old API's validation problem. */
  withoutWorkspace?: { status: number; body: unknown };
  /** The tenant's stated default language, once signed in. */
  tenantLanguage?: 'en' | 'ar';
}

async function mockApi(page: Page, opts: MockOptions = {}) {
  const calls: Call[] = [];
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const withoutWorkspace = opts.withoutWorkspace ?? { status: 400, body: oldValidationProblem() };

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const json = (body: unknown, status = 200) =>
      route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body ?? {}) });
    const body = request.method() === 'GET' ? Object.fromEntries(url.searchParams) : request.postDataJSON();
    calls.push({ path: url.pathname, body });

    if (url.pathname === '/api/auth/login') {
      if (!body?.tenantSlug) return json(withoutWorkspace.body, withoutWorkspace.status);
      return body.tenantSlug === WORKSPACE && body.password === PASSWORD
        ? json(session)
        : json({ error: 'invalid_credentials' }, 401);
    }
    if (url.pathname === '/api/auth/forgot-password') {
      if (!body?.tenantSlug) return json(withoutWorkspace.body, withoutWorkspace.status);
      return json({ message: 'If an account exists, a reset link has been sent.' });
    }
    if (url.pathname === '/api/auth/me') return json(user);
    if (url.pathname === '/api/ess/dashboard') return json(essDashboard);
    if (url.pathname === '/api/features/disabled-keys') return json(['release_a']);
    if (url.pathname === '/api/features/modules') return json([]);
    // Lists the Self-Service home reads (the same endpoints e2e/ess-workspace.spec.ts mocks).
    const lists = ['/api/ess/hr-requests/my', '/api/ess/my-roster', '/api/leave/types', '/api/ess/document-requests/types',
      '/api/ess/document-requests', '/api/ess/payslips', '/api/notifications', '/api/hr-requests/categories'];
    if (lists.includes(url.pathname)) return json([]);
    if (url.pathname === '/api/tenant-admin/localization') {
      return json(opts.tenantLanguage ? { stated: true, defaultLanguage: opts.tenantLanguage, currencyCode: 'SAR' } : { stated: false });
    }
    // Whatever else the employee home asks for once signed in: empty, never a real record.
    return json(request.method() === 'GET' ? { items: [], total: 0 } : {});
  });
  return { calls, errors };
}

const sent = (calls: Call[], path: string) => calls.filter((c) => c.path === path).map((c) => c.body);
/** The card's error line (Next's dev route announcer is also role=alert, so not by role). */
const fault = (page: Page) => page.locator('.lx-fault');

test.use({ storageState: { cookies: [], origins: [] } });

test.describe('the frontend deployed before the backend', () => {
  test('sign-in: the old API\'s "TenantSlug is required" asks for the company ID, and signing in with it lands', async ({ page }) => {
    const api = await mockApi(page);
    await page.goto('/login');
    await expect(page.locator('#li-ws')).toHaveCount(0);

    await page.getByLabel('Email').fill(EMAIL);
    await page.getByLabel('Password', { exact: true }).fill(PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();

    // Not "Please check the details you entered": the company ID field, with the plain sentence.
    const companyId = page.getByLabel('Company ID');
    await expect(companyId).toBeVisible();
    await expect(companyId).toBeFocused();
    await expect(companyId).toBeEditable();
    await expect(fault(page)).toHaveText(NEEDS_WORKSPACE);

    await companyId.fill('  Evostel ');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/ess/);

    expect(sent(api.calls, '/api/auth/login')).toEqual([
      { email: EMAIL, password: PASSWORD },
      { email: EMAIL, password: PASSWORD, tenantSlug: WORKSPACE },
    ]);
    expect(api.errors).toEqual([]);
  });

  test('sign-in: a camelCase "tenantSlug" validation key is read the same way', async ({ page }) => {
    const api = await mockApi(page, { withoutWorkspace: { status: 400, body: oldValidationProblem('tenantSlug') } });
    await page.goto('/login');
    await page.getByLabel('Email').fill(EMAIL);
    await page.getByLabel('Password', { exact: true }).fill(PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByLabel('Company ID')).toBeVisible();
    await expect(fault(page)).toHaveText(NEEDS_WORKSPACE);
    expect(sent(api.calls, '/api/auth/login')).toHaveLength(1);
  });

  test('password reset: the old API\'s validation problem asks for the company ID, then the link is sent', async ({ page }) => {
    const api = await mockApi(page);
    await page.goto('/login');
    await page.getByLabel('Email').fill(EMAIL);
    await page.getByTestId('login-forgot').click();
    await expect(page.locator('#fg-ws')).toHaveCount(0);
    await page.getByRole('button', { name: 'Send reset link' }).click();

    const companyId = page.getByLabel('Company ID');
    await expect(companyId).toBeVisible();
    await expect(fault(page)).toHaveText(NEEDS_WORKSPACE);

    await companyId.fill(WORKSPACE);
    await page.getByRole('button', { name: 'Send reset link' }).click();
    await expect(page.getByText('If this email has an account, a reset link is on its way to it.')).toBeVisible();
    expect(sent(api.calls, '/api/auth/forgot-password')).toEqual([
      { email: EMAIL },
      { email: EMAIL, tenantSlug: WORKSPACE },
    ]);
  });
});

test.describe('Sign in with Company ID', () => {
  test('an email whose domain belongs to another company: the link reveals the field and the sign-in carries it', async ({ page }) => {
    // The current API: without a company ID, this email finds the wrong company, so the password fails.
    const api = await mockApi(page, { withoutWorkspace: { status: 401, body: { error: 'invalid_credentials' } } });
    await page.goto('/login');
    await page.getByLabel('Email').fill(EMAIL);
    await page.getByLabel('Password', { exact: true }).fill(PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(fault(page)).toHaveText('Email or password is incorrect. Check both and try again.');

    const link = page.getByRole('button', { name: 'Sign in with Company ID' });
    await expect(link).toBeVisible();
    await link.click();
    const companyId = page.getByLabel('Company ID');
    await expect(companyId).toBeVisible();
    await expect(companyId).toBeFocused();
    await expect(companyId).toBeEditable();
    // The link has done its job: it goes once the field is there.
    await expect(link).toHaveCount(0);

    await companyId.fill(WORKSPACE);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.waitForURL(/\/ess/);
    expect(sent(api.calls, '/api/auth/login').at(-1)).toEqual({ email: EMAIL, password: PASSWORD, tenantSlug: WORKSPACE });
  });

  test('not offered when the company ID is already shown (a ?workspace= link)', async ({ page }) => {
    await mockApi(page);
    await page.goto(`/login?workspace=${WORKSPACE}`);
    await expect(page.getByLabel('Company ID')).toHaveValue(WORKSPACE);
    await expect(page.getByRole('button', { name: 'Sign in with Company ID' })).toHaveCount(0);
  });
});

test.describe('device language', () => {
  test.use({ locale: 'ar-SA' });

  test('an Arabic device opens /login in Arabic, link included, over a cached English tenant', async ({ page }) => {
    await mockApi(page);
    await page.addInitScript(() => localStorage.setItem('kynexone-tenant-locale', 'en'));
    await page.goto('/login');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('button', { name: 'تسجيل الدخول باستخدام معرّف الشركة' })).toBeVisible();
  });

  test('signed in, an English tenant stays English on an Arabic device', async ({ page }) => {
    await mockApi(page, { tenantLanguage: 'en' });
    await page.addInitScript(() => {
      localStorage.setItem('zayra_access_token', 'fixture-access');
      localStorage.setItem('kynexone-tenant-locale', 'en');
    });
    await page.goto('/ess');
    await expect(page.getByRole('heading', { level: 1, name: /Noah/ })).toBeVisible({ timeout: 60_000 });
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
    await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
  });
});
