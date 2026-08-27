/**
 * Shared helpers for the Group→Company E2E suite.
 *
 * Design notes
 * ────────────
 * • Every suite probes the stack in `beforeAll` and records a skip reason;
 *   a `beforeEach` then calls `test.skip(reason !== null, reason)` so CI
 *   without a running stack goes YELLOW (skipped), never RED.
 * • The group/company UI is being built in parallel, so UI helpers use
 *   resilient role/text locators and return `null` instead of throwing when
 *   a surface is not present yet — callers skip with a clear message.
 * • Where the DOM is uncertain we prefer API assertions via APIRequestContext.
 *
 * Test data: seeded by EnterpriseGroupSeeder when the backend runs with
 * SEED_ENTERPRISE_TEST_DATA=true. Password for ALL enterprise-group users
 * is GroupDemo123!x. See README.md in this directory.
 */
import {
  request as pwRequest,
  APIRequestContext,
  Page,
  Locator,
} from '@playwright/test';
import { platformSetupToken, tenantSetupSession } from '../helpers';

// ── Base URL / stack constants ────────────────────────────────────────────────

export const BASE_URL = process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL ?? 'http://localhost:5173';

// ── Enterprise group seed data (EnterpriseGroupSeeder) ───────────────────────

export const GROUP_PASSWORD = process.env.E2E_GROUP_PASSWORD ?? 'GroupDemo123!x';

export const ALMARAI = {
  slug: 'almarai-test',
  companies: ['ALM-DAIRY-KSA', 'ALM-POULTRY-KSA', 'ALM-BAKERY-KSA', 'ALM-DIST-KSA', 'ALM-UAE-TRD'],
};
export const TATA = {
  slug: 'tata-test',
  companies: ['TATA-TCS-IN', 'TATA-MOTORS-IN', 'TATA-STEEL-IN', 'TATA-HOTELS-IN', 'TATA-JLR-UK'],
};
export const EMAAR = {
  slug: 'emaar-test',
  companies: ['EMAAR-PROP-UAE', 'EMAAR-MALLS-UAE', 'EMAAR-HOSP-UAE', 'EMAAR-LEISURE-UAE', 'EMAAR-KSA-PROP'],
};

/** Group-scope users: owner@ / admin@ / hr@ / finance@ / compliance@ / auditor@ <slug>.local */
export const groupUser = (role: string, slug: string = ALMARAI.slug): string =>
  `${role}@${slug}.local`;

/** Company-scoped users: e.g. admin@alm-dairy-ksa.almarai-test.local */
export const companyUser = (role: string, companyCode: string, slug: string = ALMARAI.slug): string =>
  `${role}@${companyCode.toLowerCase()}.${slug}.local`;

/** Employee codes are `<COMPANY-CODE>-E<number>`, e.g. ALM-DAIRY-KSA-E1. */
export const empCodePrefix = (companyCode: string): string => `${companyCode}-E`;

/** Sibling (inaccessible) Almarai company codes for the scoped/company users. */
export const ALMARAI_SIBLING_CODES = ['ALM-BAKERY-KSA', 'ALM-DIST-KSA', 'ALM-UAE-TRD'];

// ── Default single-company tenant (backend appsettings.json → SeedAdmin) ─────
// Override via env if your local SeedAdmin config differs.

// The full E2E setup already authenticates this production-shaped, single-company tenant. Using
// it as the default keeps the regression deterministic and avoids an extra login outside the
// production 10/minute budget. Deployments may still override all three values.
export const DEFAULT_TENANT_SLUG = process.env.E2E_DEFAULT_TENANT_SLUG ?? 'intelliflow';
export const DEFAULT_ADMIN_EMAIL = process.env.E2E_DEFAULT_ADMIN_EMAIL ?? 'admin@intelliflow.com';
export const DEFAULT_ADMIN_PASSWORD = process.env.E2E_DEFAULT_ADMIN_PASSWORD ?? 'IntelliFlow@2026!';

// ── Platform admin (same envs the legacy e2e/helpers.ts uses) ────────────────

export const PLATFORM_EMAIL = process.env.PLATFORM_ADMIN_EMAIL ?? 'platform@kynexone.com';
export const PLATFORM_PASSWORD = process.env.PLATFORM_ADMIN_PASSWORD ?? 'PlatformAdmin123!';

// ── Stack probing / suite skipping ────────────────────────────────────────────

/** Fresh API context bound to the frontend baseURL (goes through the /api proxy). */
export async function newApi(): Promise<APIRequestContext> {
  return pwRequest.newContext({ baseURL: BASE_URL, timeout: 30_000 });
}

/**
 * Returns null only when the frontend proxy reaches the real backend auth endpoint.
 * A 401 response from /api/auth/me proves that chain; accepting any sub-500
 * response previously let an unrelated Vite HTML page masquerade as a healthy HRM API.
 */
export async function stackDownReason(): Promise<string | null> {
  let api: APIRequestContext | null = null;
  try {
    api = await pwRequest.newContext({ baseURL: BASE_URL, timeout: 15_000 });
    const resp = await api.get('/api/auth/me');
    const contentType = resp.headers()['content-type'] ?? '';
    if (resp.status() === 401) return null;

    const preview = (await resp.text()).replace(/\s+/g, ' ').slice(0, 160);
    return `Stack unhealthy: GET ${BASE_URL}/api/auth/me must return 401, but returned ` +
      `${resp.status()} ${contentType || '(no content-type)'}: ${preview}. ` +
      `The configured URL may point to an unrelated frontend or a broken API proxy.`;
  } catch {
    return `Stack not reachable at ${BASE_URL}. Start the backend + frontend first ` +
      `(see e2e/group-company/README.md) or set PLAYWRIGHT_BASE_URL.`;
  } finally {
    await api?.dispose().catch(() => {});
  }
}

/** Login via API; throws with details on failure. Returns the raw auth payload. */
export async function apiLogin(
  _api: APIRequestContext,
  email: string,
  slug: string,
  _password: string = GROUP_PASSWORD,
): Promise<{ token: string; body: any }> {
  const session = await tenantSetupSession(email, slug);
  return { token: session.accessToken, body: {} };
}

/** Like apiLogin but returns null instead of throwing (used for seed probes). */
export async function tryApiLogin(
  api: APIRequestContext,
  email: string,
  slug: string,
  password: string = GROUP_PASSWORD,
): Promise<{ token: string; body: any } | null> {
  try {
    return await apiLogin(api, email, slug, password);
  } catch {
    return null;
  }
}

/**
 * Returns null when the enterprise-group seed data is present (probe user can
 * log in), otherwise a skip reason instructing how to seed it.
 */
export async function groupSeedMissingReason(
  probeEmail: string = groupUser('owner'),
  slug: string = ALMARAI.slug,
): Promise<string | null> {
  const api = await newApi();
  try {
    const login = await tryApiLogin(api, probeEmail, slug);
    if (login) return null;
    return `Enterprise group test data not seeded (login failed for ${probeEmail} / tenant ${slug}). ` +
      `Run the backend with SEED_ENTERPRISE_TEST_DATA=true. See e2e/group-company/README.md.`;
  } finally {
    await api.dispose().catch(() => {});
  }
}

/** Platform admin API login; returns null (with reason) when unavailable. */
export async function tryPlatformApiLogin(
  _api: APIRequestContext,
): Promise<{ token: string } | null> {
  try {
    return { token: await platformSetupToken() };
  } catch {
    return null;
  }
}

// ── Authenticated API helpers ─────────────────────────────────────────────────

export function authHeaders(token: string, companyId?: string): Record<string, string> {
  const h: Record<string, string> = { Authorization: `Bearer ${token}` };
  if (companyId) h['X-Company-Id'] = companyId;
  return h;
}

export async function getJson(
  api: APIRequestContext,
  token: string,
  path: string,
  extraHeaders?: Record<string, string>,
): Promise<{ status: number; json: any }> {
  const resp = await api.get(path, { headers: { ...authHeaders(token), ...(extraHeaders ?? {}) } });
  let json: any = null;
  try { json = await resp.json(); } catch { /* non-JSON body */ }
  return { status: resp.status(), json };
}

/** Normalize a PagedResult ({ items, total, ... }) or bare array into an array. */
export function listItems(json: any): any[] {
  if (Array.isArray(json)) return json;
  if (json && Array.isArray(json.items)) return json.items;
  if (json && Array.isArray(json.Items)) return json.Items;
  return [];
}

/** GET /api/companies for the given token, normalized to an array. */
export async function fetchCompanies(api: APIRequestContext, token: string): Promise<any[]> {
  const { status, json } = await getJson(api, token, '/api/companies?page=1&pageSize=100');
  if (status !== 200) throw new Error(`GET /api/companies returned ${status}: ${JSON.stringify(json).slice(0, 300)}`);
  return listItems(json);
}

/**
 * Find a company id by its seed code (e.g. ALM-DAIRY-KSA). The DTO's exact
 * field carrying the code may vary while the feature is built, so we match
 * the code anywhere in the serialized row, falling back to the distinctive
 * middle segment (DAIRY / POULTRY / ...).
 */
export function companyIdByCode(companies: any[], code: string): string | null {
  const byCode = companies.find((c) => JSON.stringify(c).includes(code));
  if (byCode) return byCode.id ?? byCode.Id ?? null;
  const segment = code.split('-')[1]; // ALM-DAIRY-KSA → DAIRY
  if (segment) {
    const bySegment = companies.find((c) => JSON.stringify(c).toUpperCase().includes(segment.toUpperCase()));
    if (bySegment) return bySegment.id ?? bySegment.Id ?? null;
  }
  return null;
}

/** GET /api/employees (large page), normalized. Optionally scoped via X-Company-Id. */
export async function fetchEmployees(
  api: APIRequestContext,
  token: string,
  companyId?: string,
): Promise<{ status: number; items: any[] }> {
  const resp = await api.get('/api/employees?page=1&pageSize=200', {
    headers: authHeaders(token, companyId),
  });
  let json: any = null;
  try { json = await resp.json(); } catch { /* ignore */ }
  return { status: resp.status(), items: listItems(json) };
}

export function employeeCodes(items: any[]): string[] {
  return items.map((i) => String(i.employeeCode ?? i.EmployeeCode ?? '')).filter(Boolean);
}

/** GET /api/auth/me for a token. */
export async function fetchMe(api: APIRequestContext, token: string): Promise<{ status: number; json: any }> {
  return getJson(api, token, '/api/auth/me');
}

// ── UI helpers ────────────────────────────────────────────────────────────────

/**
 * UI login. Generous timeouts on the first load — Next dev servers compile
 * routes lazily and the first navigation can take a while.
 */
export async function uiLogin(
  page: Page,
  email: string,
  slug: string,
  _password: string = GROUP_PASSWORD,
): Promise<void> {
  const session = await tenantSetupSession(email, slug);
  await page.goto('/login', { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await page.evaluate(({ accessToken, refreshToken }) => {
    localStorage.setItem('zayra_access_token', accessToken);
    localStorage.setItem('zayra_refresh_token', refreshToken);
  }, session);
  await page.goto('/dashboard', { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await page.waitForURL(/\/(dashboard|app|group)/, { timeout: 30_000 });
  // Let the dashboard's initial API calls settle so background 403 redirects
  // cannot abort the next page.goto() (see e2e/helpers.ts for the rationale).
  await page.waitForLoadState('networkidle', { timeout: 15_000 }).catch(() => {});
}

/**
 * Locate the TopBar company switcher without fabricating selectors:
 * tries data-testid, aria-label and accessible-name candidates in turn.
 * Returns null when no candidate becomes visible in time (surface not built
 * yet, or the user genuinely has no switcher) — callers decide what that means.
 */
export async function findCompanySwitcher(page: Page, timeoutMs = 15_000): Promise<Locator | null> {
  const candidates: Locator[] = [
    page.getByTestId('company-switcher'),
    page.getByRole('button', { name: /all companies/i }),
    page.getByRole('button', { name: /switch (compan|entit)/i }),
    page.locator('header button[aria-label*="ompan"]'),
    page.locator('[role="banner"] button[aria-label*="ompan"]'),
    page.locator('button[aria-label*="company switcher" i]'),
    page.getByRole('combobox', { name: /compan/i }),
  ];
  const deadline = Date.now() + timeoutMs;
  // Poll: the topbar may hydrate after networkidle.
  for (;;) {
    for (const c of candidates) {
      const loc = c.first();
      if (await loc.isVisible().catch(() => false)) return loc;
    }
    if (Date.now() > deadline) return null;
    await page.waitForTimeout(500);
  }
}

/**
 * Open the switcher dropdown. Returns true when a dropdown-ish surface
 * appeared (listbox/menu/dialog or any new option text).
 */
export async function openCompanySwitcher(page: Page, switcher: Locator): Promise<boolean> {
  await switcher.click().catch(() => {});
  const dropdown = page.locator('[role="listbox"], [role="menu"], [role="dialog"], [data-radix-popper-content-wrapper]').first();
  return dropdown.isVisible({ timeout: 5_000 }).catch(() => false);
}

/**
 * Pick a company (by its code text) inside the opened switcher dropdown.
 * Returns true on success.
 */
export async function pickCompanyInSwitcher(page: Page, code: string): Promise<boolean> {
  const candidates: Locator[] = [
    page.getByRole('option', { name: new RegExp(code, 'i') }),
    page.getByRole('menuitem', { name: new RegExp(code, 'i') }),
    page.getByText(code, { exact: false }),
  ];
  for (const c of candidates) {
    const loc = c.first();
    if (await loc.isVisible().catch(() => false)) {
      await loc.click().catch(() => {});
      await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});
      return true;
    }
  }
  return false;
}

/** Full visible page text (lowercased comparisons are up to the caller). */
/**
 * Reads the page body. Deliberately does NOT swallow errors.
 *
 * This used to be `.catch(() => '')`. Its output feeds ~14 leak assertions of the form
 * `expect(text).not.toContain('<sibling company code>')` — and an empty string contains no
 * sibling codes. So a detached frame, a redirect to /login, an unrendered page, or any thrown
 * locator error all produced PASS. Proved: the verbatim leak assertion from scoped-user.spec.ts
 * passed against a completely logged-out browser.
 *
 * A negative assertion is only meaningful if the thing it reads actually loaded, so this now
 * throws on failure and asserts the page is authenticated and has rendered real content — not
 * just the navigation shell, which is 479-685 characters on its own.
 */
export async function bodyText(page: Page): Promise<string> {
  if (/\/login/.test(page.url())) {
    throw new Error(
      `bodyText() called on ${page.url()} — the page is the login screen, not authenticated `
      + 'content. Any "does not contain sibling data" assertion here would pass vacuously.',
    );
  }
  const text = await page.locator('body').innerText();
  if (!text || text.trim().length === 0) {
    throw new Error(`bodyText() read an empty body at ${page.url()} — nothing rendered.`);
  }
  return text;
}

/**
 * Content length excluding the static navigation shell.
 *
 * `innerText().length > 50` was the suite's standard "the page loaded" proxy, and the shell alone
 * satisfies it: nav 382 + aside 479 + header 90 characters. Proved by fulfilling every /api/**
 * request with a 500 — /payroll, /leave, /attendance, /people, /offboarding and
 * /saudi-compliance all still cleared the threshold. Measure the main region instead.
 */
export async function mainContentLength(page: Page): Promise<number> {
  const main = page.locator('main, [role="main"]').first();
  if (await main.count() === 0) return 0;
  const text = await main.innerText().catch(() => '');
  return (text ?? '').trim().length;
}

/** True when the current page looks like a Next.js 404 / not-found. */
export async function looksLikeNotFound(page: Page): Promise<boolean> {
  const text = (await bodyText(page)).toLowerCase();
  return /(^|\s)404(\s|$)/.test(text) || text.includes('page could not be found') || text.includes('page not found');
}

/** Assert-free crash sniff, reused across suites. */
export function crashIndicators(text: string): string[] {
  const lower = text.toLowerCase();
  return ['something went wrong', 'unexpected error', 'cannot read properties of undefined', 'typeerror']
    .filter((s) => lower.includes(s));
}
