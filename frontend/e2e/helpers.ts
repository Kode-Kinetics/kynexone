import { Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';

// ── Platform admin credentials ─────────────────────────────────────────────────
export const PLATFORM_EMAIL    = process.env.PLATFORM_ADMIN_EMAIL    ?? 'platform@kynexone.com';
export const PLATFORM_PASSWORD = process.env.PLATFORM_ADMIN_PASSWORD ?? 'PlatformAdmin123!';
export const BASE_URL          = process.env.PLAYWRIGHT_BASE_URL     ?? 'http://localhost:5173';

// Where auth.setup.ts persists the platform-admin session. Reused by every platform spec so the
// suite authenticates ONCE rather than once per test — see auth.setup.ts for why that matters.
export const PLATFORM_STATE = 'e2e/.auth/platform.json';
export const TENANT_STATE = 'e2e/.auth/tenants.json';

type TenantSession = { accessToken: string; refreshToken: string };

export const tenantSessionKey = (email: string, slug: string): string =>
  `${slug.toLowerCase()}|${email.toLowerCase()}`;

export async function tenantSetupSession(email: string, slug: string): Promise<TenantSession> {
  const sessions = JSON.parse(await readFile(TENANT_STATE, 'utf8')) as Record<string, TenantSession>;
  const session = sessions[tenantSessionKey(email, slug)];
  if (!session?.accessToken)
    throw new Error(`No setup tenant session exists for ${email} / ${slug}.`);
  return session;
}

// ── Demo tenant credentials ────────────────────────────────────────────────────
// IntelliFlow Systems — Enterprise, all features enabled, Active
export const INTELLIFLOW_SLUG     = 'intelliflow';
const INTELLIFLOW_PASSWORD = process.env.E2E_INTELLIFLOW_PASSWORD ?? 'IntelliFlow@2026!';
export const INTELLIFLOW_ADMIN    = { email: 'admin@intelliflow.com',      password: INTELLIFLOW_PASSWORD, role: 'Admin' };
export const INTELLIFLOW_HR_DIR   = { email: 'hrdirector@intelliflow.com', password: INTELLIFLOW_PASSWORD, role: 'HR Director' };
export const INTELLIFLOW_HR_MGR   = { email: 'hrmanager@intelliflow.com',  password: INTELLIFLOW_PASSWORD, role: 'HR Manager' };
export const INTELLIFLOW_FINANCE  = { email: 'finance@intelliflow.com',    password: INTELLIFLOW_PASSWORD, role: 'Finance Approver' };
export const INTELLIFLOW_MANAGER  = { email: 'manager@intelliflow.com',    password: INTELLIFLOW_PASSWORD, role: 'Manager' };
export const INTELLIFLOW_SUPERVISOR = { email: 'supervisor@intelliflow.com', password: INTELLIFLOW_PASSWORD, role: 'Supervisor' };
export const INTELLIFLOW_EMP1     = { email: 'employee1@intelliflow.com',  password: INTELLIFLOW_PASSWORD, role: 'Employee' };
export const INTELLIFLOW_EMP2     = { email: 'employee2@intelliflow.com',  password: INTELLIFLOW_PASSWORD, role: 'Employee' };
export const INTELLIFLOW_AUDITOR  = { email: 'auditor@intelliflow.com',    password: INTELLIFLOW_PASSWORD, role: 'Auditor' };

// Ras Al-Manar — second permanent clean-demo tenant used for meaningful
// cross-tenant isolation checks against real seeded employee/HR data.
export const RASALMANAR_SLUG  = 'rasalmanar';
export const RASALMANAR_ADMIN = { email: 'admin@rasalmanar.com', password: 'RasAlManar@2026!', role: 'Admin' };

// Evostel is NOT application demo data. The Playwright setup project provisions
// this isolated, limited/PastDue tenant and its teardown project purges it. Keeping
// subscription/feature tests self-contained prevents test fixtures from polluting
// production-shaped startup data.
export const EVOSTEL_SLUG    = 'evostel';
export const EVOSTEL_ADMIN   = { email: 'admin@evostel.com',    password: 'E2E-Demo@1234', role: 'Admin' };
export const EVOSTEL_EMP1    = { email: 'employee1@evostel.com', password: 'E2E-Demo@1234', role: 'Employee' };

// ── Platform admin helpers ────────────────────────────────────────────────────

export async function platformLogin(page: Page): Promise<void> {
  await page.goto('/platform/login');
  await page.locator('#pl-em, input[type="email"]').first().fill(PLATFORM_EMAIL);
  const passwordInput = page.locator('#pl-pw, input[type="password"]').first();
  await passwordInput.fill(PLATFORM_PASSWORD);
  // Submit from the input instead of clicking the animated button. The login
  // surface intentionally replaces the button during its entrance transition,
  // which makes a pointer click race DOM replacement even though the form is ready.
  await passwordInput.press('Enter');
  await page.waitForURL(/\/platform\/dashboard/, { timeout: 15_000 });
}

export async function platformLogout(page: Page): Promise<void> {
  await page.evaluate(async () => {
    const token = localStorage.getItem('platform_access_token');
    if (token) {
      await fetch('/api/platform/auth/logout', {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}` },
      });
    }
    localStorage.removeItem('platform_access_token');
  });
}

// ── Tenant login helpers ──────────────────────────────────────────────────────

export async function tenantLoginLive(
  page: Page,
  email: string,
  password: string,
  slug: string
): Promise<void> {
  await page.goto('/login');
  await page.locator('#li-em, input[type="email"]').first().fill(email);
  await page.locator('#li-pw, input[type="password"]').first().fill(password);
  await page.locator('#li-ws, input[autocomplete="organization"]').first().fill(slug);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.waitForURL(/\/(dashboard|app)/, { timeout: 15_000 });
  // Wait for the dashboard's initial API calls to settle before each test navigates away.
  // Without this, background fetches can fire 403s that trigger window.location redirects,
  // which abort subsequent page.goto() calls with ERR_ABORTED.
  await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {/* ignore timeout */});
}

/** Reuse a setup-time persona session for feature/workflow UI assertions. */
export async function tenantLogin(
  page: Page,
  email: string,
  _password: string,
  slug: string
): Promise<void> {
  const session = await tenantSetupSession(email, slug);
  await page.goto('/login');
  await page.evaluate(({ accessToken, refreshToken }) => {
    localStorage.setItem('zayra_access_token', accessToken);
    localStorage.setItem('zayra_refresh_token', refreshToken);
  }, session);
  await page.goto('/dashboard');
  await page.waitForURL(/\/(dashboard|app|group)/, { timeout: 15_000 });
}

export async function tenantLogout(page: Page): Promise<void> {
  await page.evaluate(() => {
    localStorage.removeItem('zayra_access_token');
    localStorage.removeItem('zayra_refresh_token');
    localStorage.removeItem('access_token');
    localStorage.removeItem('refresh_token');
    localStorage.removeItem('user');
  });
}

// ── API helpers (direct HTTP, bypasses UI) ───────────────────────────────────

/** Login via API and return the access token. */
export async function apiLoginLive(
  request: import('@playwright/test').APIRequestContext,
  email: string,
  password: string,
  slug: string
): Promise<string> {
  const resp = await request.post('/api/auth/login', {
    data: { email, password, tenantSlug: slug },
  });
  if (!resp.ok()) throw new Error(`Login failed: ${resp.status()} ${await resp.text()}`);
  const data = await resp.json();
  return data.accessToken ?? data.token ?? data.access_token;
}

/** Reuse a setup-time persona token for API feature/workflow assertions. */
export async function apiLogin(
  _request: import('@playwright/test').APIRequestContext,
  email: string,
  _password: string,
  slug: string
): Promise<string> {
  return (await tenantSetupSession(email, slug)).accessToken;
}

/** Read the one platform session created by the setup project. */
export async function platformSetupToken(): Promise<string> {
  const state = JSON.parse(await readFile(PLATFORM_STATE, 'utf8')) as {
    origins?: Array<{ localStorage?: Array<{ name: string; value: string }> }>;
  };
  const token = state.origins
    ?.flatMap((origin) => origin.localStorage ?? [])
    .find((entry) => entry.name === 'platform_access_token')
    ?.value;
  if (!token) throw new Error('Platform setup storage state does not contain an access token.');
  return token;
}

/** Reuse the setup project's platform session for authenticated API assertions. */
export async function apiPlatformLogin(
  _request: import('@playwright/test').APIRequestContext
): Promise<string> {
  return platformSetupToken();
}

/** Create a new platform session only when the prior shared session is no longer needed. */
export async function apiPlatformFreshLogin(
  request: import('@playwright/test').APIRequestContext
): Promise<string> {
  const resp = await request.post('/api/platform/auth/login', {
    data: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD },
  });
  if (!resp.ok()) throw new Error(`Platform login failed: ${resp.status()} ${await resp.text()}`);
  const data = await resp.json();
  return data.token ?? data.accessToken;
}
