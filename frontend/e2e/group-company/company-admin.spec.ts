/**
 * Company admin — admin@alm-dairy-ksa.almarai-test.local sees exactly one
 * company (ALM-DAIRY-KSA) and cannot widen their scope:
 *   • /api/companies and /api/auth/me expose only ALM-DAIRY-KSA
 *   • employees (API + UI) contain only ALM-DAIRY-KSA-E* codes
 *   • API tamper: sending X-Company-Id for a sibling company (ALM-BAKERY-KSA)
 *     must FAIL CLOSED — empty data, never sibling employee codes
 */
import { test, expect } from '@playwright/test';
import {
  stackDownReason,
  groupSeedMissingReason,
  newApi,
  apiLogin,
  tryApiLogin,
  fetchMe,
  fetchCompanies,
  companyIdByCode,
  fetchEmployees,
  employeeCodes,
  uiLogin,
  bodyText,
  crashIndicators,
  groupUser,
  companyUser,
  ALMARAI,
  ALMARAI_SIBLING_CODES,
  empCodePrefix,
} from './helpers';

const DAIRY = 'ALM-DAIRY-KSA';
const BAKERY = 'ALM-BAKERY-KSA';
const COMPANY_ADMIN = companyUser('admin', DAIRY); // admin@alm-dairy-ksa.almarai-test.local
const OWNER = groupUser('owner');

let skipReason: string | null = null;
let bakeryId: string | null = null;

test.describe('Group→Company: company admin (ALM-DAIRY-KSA)', () => {
  test.beforeAll(async () => {
    skipReason = (await stackDownReason()) ?? (await groupSeedMissingReason(COMPANY_ADMIN));
    if (skipReason) return;

    // Resolve the sibling (ALM-BAKERY-KSA) company id via a group-scope user —
    // the company admin cannot see it, which is exactly the point.
    const api = await newApi();
    try {
      const ownerLogin = await tryApiLogin(api, OWNER, ALMARAI.slug);
      if (ownerLogin) {
        const companies = await fetchCompanies(api, ownerLogin.token).catch(() => []);
        bakeryId = companyIdByCode(companies, BAKERY);
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test.beforeEach(() => {
    test.skip(skipReason !== null, skipReason ?? '');
  });

  test('API: company admin sees only ALM-DAIRY-KSA in /api/companies (and me.companies)', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, COMPANY_ADMIN, ALMARAI.slug);

      const companies = await fetchCompanies(api, token);
      expect(companies.length).toBe(1);
      expect(JSON.stringify(companies)).toContain(DAIRY);

      const me = await fetchMe(api, token);
      expect(me.status).toBe(200);
      // Both checks were `if (field present) { expect(...) }`. A response that
      // dropped `companies` or `isGroupScope` — which is how a scope regression
      // would surface — made this test assert nothing about scope at all.
      expect(Array.isArray(me.json?.companies), '/api/auth/me must report a companies array').toBe(true);
      expect(me.json.companies.length).toBe(1);
      expect(JSON.stringify(me.json.companies)).toContain(DAIRY);

      expect(me.json?.isGroupScope, '/api/auth/me must report isGroupScope').toBeDefined();
      expect(me.json.isGroupScope, 'a company-scoped admin must not be group scope').toBeFalsy();
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('API: /api/employees returns only ALM-DAIRY-KSA-E* employees', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, COMPANY_ADMIN, ALMARAI.slug);
      const { status, items } = await fetchEmployees(api, token);
      expect(status).toBe(200);
      const codes = employeeCodes(items);
      expect(codes.length, 'dairy company should have seeded employees').toBeGreaterThan(0);
      for (const code of codes) {
        expect(code, `employee ${code} does not belong to ${DAIRY}`).toMatch(new RegExp(`^${DAIRY}`));
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('API tamper: X-Company-Id = ALM-BAKERY-KSA id fails closed (empty data, no sibling codes)', async () => {
    test.skip(bakeryId === null, `could not resolve ${BAKERY} company id via group owner — cannot run tamper check`);

    const api = await newApi();
    try {
      const { token } = await apiLogin(api, COMPANY_ADMIN, ALMARAI.slug);
      const resp = await api.get('/api/employees?page=1&pageSize=200', {
        headers: { Authorization: `Bearer ${token}`, 'X-Company-Id': bakeryId! },
      });

      // Never a server error; either an explicit denial or an EMPTY result.
      expect(resp.status(), 'tampered X-Company-Id must not cause a 500').toBeLessThan(500);

      const raw = await resp.text();
      for (const sibling of ALMARAI_SIBLING_CODES) {
        expect(raw, `tampered request leaked ${sibling} employee data`).not.toContain(empCodePrefix(sibling));
      }
      if (resp.ok()) {
        let json: any = null;
        try { json = JSON.parse(raw); } catch { /* ignore */ }
        const items = Array.isArray(json) ? json : (json?.items ?? []);
        expect(items.length, 'tampered request must return an empty list (fail closed)').toBe(0);
      } else {
        expect([400, 401, 403, 404]).toContain(resp.status());
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('UI: company admin sees only ALM-DAIRY-KSA; employees page has no sibling codes', async ({ page }) => {
    await uiLogin(page, COMPANY_ADMIN, ALMARAI.slug);

    const dashText = await bodyText(page);
    expect(crashIndicators(dashText)).toEqual([]);
    for (const sibling of ALMARAI_SIBLING_CODES) {
      expect(dashText, `dashboard leaked sibling company ${sibling}`).not.toContain(sibling);
    }

    await page.goto('/people', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});
    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);
    for (const sibling of ALMARAI.companies.filter((c) => c !== DAIRY)) {
      expect(text, `employees page leaked ${sibling} employees`).not.toContain(empCodePrefix(sibling));
    }
  });
});
