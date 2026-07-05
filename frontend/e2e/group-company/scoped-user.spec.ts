/**
 * Selected-companies user — scoped.admin@almarai-test.local has grants ONLY
 * to ALM-DAIRY-KSA and ALM-POULTRY-KSA:
 *   • switcher lists exactly those two, with NO "All companies" option
 *   • group dashboard (if accessible) shows only those two companies
 *   • employees list/API never contains ALM-BAKERY-KSA-E* (or other sibling) codes
 */
import { test, expect } from '@playwright/test';
import {
  stackDownReason,
  groupSeedMissingReason,
  newApi,
  apiLogin,
  fetchMe,
  fetchCompanies,
  fetchEmployees,
  employeeCodes,
  uiLogin,
  findCompanySwitcher,
  openCompanySwitcher,
  bodyText,
  looksLikeNotFound,
  ALMARAI,
  ALMARAI_SIBLING_CODES,
  empCodePrefix,
} from './helpers';

const SCOPED = 'scoped.admin@almarai-test.local';
const ALLOWED = ['ALM-DAIRY-KSA', 'ALM-POULTRY-KSA'];

let skipReason: string | null = null;

test.describe('Group→Company: selected-companies user (scoped.admin, almarai-test)', () => {
  test.beforeAll(async () => {
    skipReason = (await stackDownReason()) ?? (await groupSeedMissingReason(SCOPED));
  });

  test.beforeEach(() => {
    test.skip(skipReason !== null, skipReason ?? '');
  });

  test('API: /api/auth/me and /api/companies expose exactly the two granted companies', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, SCOPED, ALMARAI.slug);

      const me = await fetchMe(api, token);
      expect(me.status).toBe(200);
      if (Array.isArray(me.json?.companies)) {
        expect(me.json.companies.length).toBe(ALLOWED.length);
      }
      // A selected-companies grant is NOT full group scope.
      if (me.json?.isGroupScope !== undefined) {
        expect(me.json.isGroupScope).toBeFalsy();
      }

      const companies = await fetchCompanies(api, token);
      expect(companies.length).toBe(ALLOWED.length);
      const serialized = JSON.stringify(companies);
      for (const code of ALLOWED) {
        expect(serialized, `granted company ${code} missing from /api/companies`).toContain(code);
      }
      for (const sibling of ALMARAI_SIBLING_CODES) {
        expect(serialized, `sibling company ${sibling} leaked into /api/companies`).not.toContain(sibling);
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('API: /api/employees never returns sibling-company employee codes', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, SCOPED, ALMARAI.slug);
      const { status, items } = await fetchEmployees(api, token);
      expect(status).toBe(200);
      const codes = employeeCodes(items);
      expect(codes.length, 'scoped admin should see employees of granted companies').toBeGreaterThan(0);
      for (const code of codes) {
        expect(
          ALLOWED.some((c) => code.startsWith(c)),
          `employee ${code} is outside the granted companies ${ALLOWED.join(', ')}`,
        ).toBeTruthy();
        for (const sibling of ALMARAI_SIBLING_CODES) {
          expect(code.startsWith(sibling), `sibling employee ${code} leaked`).toBeFalsy();
        }
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('UI: switcher lists ONLY the two granted companies and has NO "All companies"', async ({ page }) => {
    await uiLogin(page, SCOPED, ALMARAI.slug);

    const switcher = await findCompanySwitcher(page, 30_000);
    test.skip(switcher === null, 'company switcher not found in TopBar — UI surface not built yet');

    const opened = await openCompanySwitcher(page, switcher!);
    expect(opened, 'company switcher dropdown did not open').toBeTruthy();

    for (const code of ALLOWED) {
      await expect(page.getByText(code).first(), `switcher missing granted company ${code}`)
        .toBeVisible({ timeout: 10_000 });
    }
    // No "All companies" for a selected-companies grant.
    await expect(page.getByText(/all companies/i)).toHaveCount(0);
    // No sibling companies offered.
    const text = await bodyText(page);
    for (const sibling of ALMARAI_SIBLING_CODES) {
      expect(text, `switcher offered inaccessible company ${sibling}`).not.toContain(sibling);
    }
  });

  test('UI: group dashboard (if visible) shows only the two granted companies', async ({ page }) => {
    await uiLogin(page, SCOPED, ALMARAI.slug);
    await page.goto('/group', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});

    // The product may legitimately hide /group from a scoped user — that is fine.
    const redirectedAway = !/\/group/.test(page.url());
    test.skip(
      redirectedAway || (await looksLikeNotFound(page)),
      'group dashboard not visible to scoped user (redirect/404) — acceptable, nothing to assert',
    );

    const text = await bodyText(page);
    for (const sibling of ALMARAI_SIBLING_CODES) {
      expect(text, `group dashboard leaked inaccessible company ${sibling}`).not.toContain(sibling);
    }
  });

  test('UI: employees page never shows ALM-BAKERY-KSA-E* codes', async ({ page }) => {
    await uiLogin(page, SCOPED, ALMARAI.slug);
    await page.goto('/people', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});

    const text = await bodyText(page);
    for (const sibling of ALMARAI_SIBLING_CODES) {
      expect(text, `employees page leaked ${sibling} employees`).not.toContain(empCodePrefix(sibling));
    }
  });
});
