/**
 * Single-company regression — the Group→Company release must not change the
 * experience of an ordinary single-company tenant:
 *   • admin logs in and reaches the dashboard
 *   • NO company switcher in the TopBar, NO "Group Overview" nav entry
 *   • employees page still loads
 *
 * Uses the production-shaped IntelliFlow demo tenant, which the full E2E setup already
 * authenticates inside the production login budget. Override via
 * E2E_DEFAULT_TENANT_SLUG / E2E_DEFAULT_ADMIN_EMAIL / E2E_DEFAULT_ADMIN_PASSWORD.
 */
import { test, expect } from '@playwright/test';
import {
  stackDownReason,
  newApi,
  tryApiLogin,
  fetchMe,
  uiLogin,
  findCompanySwitcher,
  bodyText,
  mainContentLength,
  crashIndicators,
  DEFAULT_TENANT_SLUG,
  DEFAULT_ADMIN_EMAIL,
  DEFAULT_ADMIN_PASSWORD,
} from './helpers';

let skipReason: string | null = null;
let token: string | null = null;

test.describe('Group→Company: single-company regression', () => {
  test.beforeAll(async () => {
    skipReason = await stackDownReason();
    if (skipReason) return;
    const api = await newApi();
    try {
      const login = await tryApiLogin(api, DEFAULT_ADMIN_EMAIL, DEFAULT_TENANT_SLUG, DEFAULT_ADMIN_PASSWORD);
      if (!login) {
        skipReason =
          `Default single-company tenant login failed (${DEFAULT_ADMIN_EMAIL} / tenant ${DEFAULT_TENANT_SLUG}). ` +
          `Set E2E_DEFAULT_TENANT_SLUG / E2E_DEFAULT_ADMIN_EMAIL / E2E_DEFAULT_ADMIN_PASSWORD to match your ` +
          `backend SeedAdmin config. See e2e/group-company/README.md.`;
        return;
      }
      token = login.token;
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test.beforeEach(() => {
    test.skip(skipReason !== null, skipReason ?? '');
  });

  test('API: /api/auth/me marks the default tenant as SingleCompany with ≤1 company', async () => {
    const api = await newApi();
    try {
      const me = await fetchMe(api, token!);
      expect(me.status).toBe(200);
      // The single-company signal is the ACCOUNT TYPE and the accessible-company count.
      // isGroupScope is deliberately true for grant-less users (the documented explicit
      // tenant default — docs/GROUP_COMPANY_ACCESS_MODEL.md); with one company that
      // still renders no switcher and no group UI.
      //
      // These were `if (field !== undefined) { expect(...) }`. Losing either field
      // from /api/auth/me — the exact regression this test is named for — asserted
      // nothing at all. Require the fields, THEN assert their values.
      expect(me.json?.accountType, '/api/auth/me must report accountType').toBeDefined();
      expect(me.json.accountType, '/api/auth/me must report accountType').not.toBeNull();
      expect(String(me.json.accountType)).not.toMatch(/^group$/i);

      expect(Array.isArray(me.json?.companies), '/api/auth/me must report a companies array').toBe(true);
      expect(me.json.companies.length).toBeLessThanOrEqual(1);
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('admin logs in, sees dashboard, and sees NO company switcher / Group Overview nav', async ({ page }) => {
    await uiLogin(page, DEFAULT_ADMIN_EMAIL, DEFAULT_TENANT_SLUG, DEFAULT_ADMIN_PASSWORD);
    await expect(page).not.toHaveURL(/\/login/, { timeout: 15_000 });

    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);
    // Count only the ROUTE's own output: the persistent sidebar alone clears
    // 50 characters, so `bodyText().length > 50` passed on a blank dashboard.
    expect(await mainContentLength(page), 'the dashboard route rendered no content of its own')
      .toBeGreaterThan(50);

    // No "Group Overview" navigation for a single-company tenant.
    expect(text.toLowerCase()).not.toContain('group overview');
    await expect(page.getByRole('link', { name: /group overview/i })).toHaveCount(0);

    // No company switcher in the TopBar (short timeout — absence is expected).
    const switcher = await findCompanySwitcher(page, 3_000);
    expect(switcher, 'single-company tenants must not see the company switcher').toBeNull();
  });

  test('employees page loads for the default tenant', async ({ page }) => {
    await uiLogin(page, DEFAULT_ADMIN_EMAIL, DEFAULT_TENANT_SLUG, DEFAULT_ADMIN_PASSWORD);
    await page.goto('/people', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 15_000 }).catch(() => {});

    await expect(page).not.toHaveURL(/\/login/);
    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);
    expect(await mainContentLength(page), 'the employees route rendered no content of its own')
      .toBeGreaterThan(50);
  });
});
