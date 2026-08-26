import { Page } from '@playwright/test';

// ── Platform admin credentials ─────────────────────────────────────────────────
export const PLATFORM_EMAIL    = process.env.PLATFORM_ADMIN_EMAIL    ?? 'admin@platform.local';
export const PLATFORM_PASSWORD = process.env.PLATFORM_ADMIN_PASSWORD ?? 'YourPassword123!';
export const BASE_URL          = process.env.PLAYWRIGHT_BASE_URL     ?? 'http://localhost:5173';

// Where auth.setup.ts persists the platform-admin session. Reused by every platform spec so the
// suite authenticates ONCE rather than once per test — see auth.setup.ts for why that matters.
export const PLATFORM_STATE = 'e2e/.auth/platform.json';

// ── Demo tenant credentials ────────────────────────────────────────────────────
// IntelliFlow Systems — Enterprise, all features enabled, Active
export const INTELLIFLOW_SLUG     = 'intelliflow';
export const INTELLIFLOW_ADMIN    = { email: 'admin@intelliflow.com',      password: 'Demo@1234', role: 'Admin' };
export const INTELLIFLOW_HR_DIR   = { email: 'hrdirector@intelliflow.com', password: 'Demo@1234', role: 'HR Director' };
export const INTELLIFLOW_HR_MGR   = { email: 'hrmanager@intelliflow.com',  password: 'Demo@1234', role: 'HR Manager' };
export const INTELLIFLOW_FINANCE  = { email: 'finance@intelliflow.com',    password: 'Demo@1234', role: 'Finance Approver' };
export const INTELLIFLOW_MANAGER  = { email: 'manager@intelliflow.com',    password: 'Demo@1234', role: 'Manager' };
export const INTELLIFLOW_SUPERVISOR = { email: 'supervisor@intelliflow.com', password: 'Demo@1234', role: 'Supervisor' };
export const INTELLIFLOW_EMP1     = { email: 'employee1@intelliflow.com',  password: 'Demo@1234', role: 'Employee' };
export const INTELLIFLOW_EMP2     = { email: 'employee2@intelliflow.com',  password: 'Demo@1234', role: 'Employee' };
export const INTELLIFLOW_AUDITOR  = { email: 'auditor@intelliflow.com',    password: 'Demo@1234', role: 'Auditor' };

// Evostel LLC — Starter, PastDue, limited features
export const EVOSTEL_SLUG    = 'evostel';
export const EVOSTEL_ADMIN   = { email: 'admin@evostel.com',    password: 'Demo@1234', role: 'Admin' };
export const EVOSTEL_EMP1    = { email: 'employee1@evostel.com', password: 'Demo@1234', role: 'Employee' };

// ── Platform admin helpers ────────────────────────────────────────────────────

export async function platformLogin(page: Page): Promise<void> {
  await page.goto('/platform/login');
  await page.locator('#pl-em, input[type="email"]').first().fill(PLATFORM_EMAIL);
  await page.locator('#pl-pw, input[type="password"]').first().fill(PLATFORM_PASSWORD);
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.waitForURL(/\/platform\/dashboard/, { timeout: 15_000 });
}

export async function platformLogout(page: Page): Promise<void> {
  await page.evaluate(() => localStorage.removeItem('platform_access_token'));
}

// ── Tenant login helpers ──────────────────────────────────────────────────────

export async function tenantLogin(
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
export async function apiLogin(
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

/** Platform admin login via API. */
export async function apiPlatformLogin(
  request: import('@playwright/test').APIRequestContext
): Promise<string> {
  const resp = await request.post('/api/platform/auth/login', {
    data: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD },
  });
  if (!resp.ok()) throw new Error(`Platform login failed: ${resp.status()} ${await resp.text()}`);
  const data = await resp.json();
  return data.token ?? data.accessToken;
}
