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

// ── Base URL / stack constants ────────────────────────────────────────────────

export const BASE_URL = process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173';

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

export const DEFAULT_TENANT_SLUG = process.env.E2E_DEFAULT_TENANT_SLUG ?? 'zayra';
export const DEFAULT_ADMIN_EMAIL = process.env.E2E_DEFAULT_ADMIN_EMAIL ?? 'admin@zayra.local';
export const DEFAULT_ADMIN_PASSWORD = process.env.E2E_DEFAULT_ADMIN_PASSWORD ?? 'ChangeMe123!';

// ── Platform admin (same envs the legacy e2e/helpers.ts uses) ────────────────

export const PLATFORM_EMAIL = process.env.PLATFORM_ADMIN_EMAIL ?? 'admin@platform.local';
export const PLATFORM_PASSWORD = process.env.PLATFORM_ADMIN_PASSWORD ?? 'YourPassword123!';

// ── Stack probing / suite skipping ────────────────────────────────────────────

/** Fresh API context bound to the frontend baseURL (goes through the /api proxy). */
export async function newApi(): Promise<APIRequestContext> {
  return pwRequest.newContext({ baseURL: BASE_URL, timeout: 30_000 });
}

/**
 * Returns null when the stack (frontend proxy + backend) is reachable,
 * otherwise a human-readable skip reason. A 401 from /api/auth/me proves
 * the whole chain is alive; 5xx/network errors mean it is not.
 */
export async function stackDownReason(): Promise<string | null> {
  let api: APIRequestContext | null = null;
  try {
    api = await pwRequest.newContext({ baseURL: BASE_URL, timeout: 15_000 });
    const resp = await api.get('/api/auth/me');
    if (resp.status() >= 500) {
      return `Stack unhealthy: GET ${BASE_URL}/api/auth/me returned ${resp.status()} ` +
        `(frontend is up but the backend API is not). See e2e/group-company/README.md.`;
    }
    return null;
  } catch {
    return `Stack not reachable at ${BASE_URL}. Start the backend + frontend first ` +
      `(see e2e/group-company/README.md) or set PLAYWRIGHT_BASE_URL.`;
  } finally {
    await api?.dispose().catch(() => {});
  }
}

/** Login via API; throws with details on failure. Returns the raw auth payload. */
export async function apiLogin(
  api: APIRequestContext,
  email: string,
  slug: string,
  password: string = GROUP_PASSWORD,
): Promise<{ token: string; body: any }> {
  const resp = await api.post('/api/auth/login', {
    data: { email, password, tenantSlug: slug },
  });
  if (!resp.ok()) {
    throw new Error(`Login failed for ${email} (tenant ${slug}): ${resp.status()} ${await resp.text()}`);
  }
  const body = await resp.json();
  const token = body.accessToken ?? body.token ?? body.access_token;
  if (!token) throw new Error(`Login for ${email} returned no access token: ${JSON.stringify(body).slice(0, 400)}`);
  return { token, body };
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
  api: APIRequestContext,
): Promise<{ token: string } | null> {
  try {
    const resp = await api.post('/api/platform/auth/login', {
      data: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD },
    });
    if (!resp.ok()) return null;
    const body = await resp.json();
    const token = body.token ?? body.accessToken;
    return token ? { token } : null;
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
  password: string = GROUP_PASSWORD,
): Promise<void> {
  await page.goto('/login', { waitUntil: 'domcontentloaded', timeout: 60_000 });
  // The login form uses stable ids (#li-em / #li-pw / #li-ws — src/views/LoginPage.tsx);
  // its labels are not programmatically associated, so accessible-name lookups miss.
  // The page renders the form TWICE (mobile + desktop variants share ids); target the
  // visible instance to satisfy strict mode.
  const visible = (selector: string) => page.locator(selector).filter({ visible: true }).first();
  await visible('#li-em').fill(email, { timeout: 30_000 });
  await visible('#li-pw').fill(password);
  await visible('#li-ws').fill(slug);
  await page.getByRole('button', { name: 'Sign in', exact: true }).filter({ visible: true }).first().click();
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
export async function bodyText(page: Page): Promise<string> {
  return (await page.locator('body').innerText().catch(() => '')) ?? '';
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
