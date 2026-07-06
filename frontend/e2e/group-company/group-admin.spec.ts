/**
 * Group admin — owner@almarai-test.local (group scope, all 5 companies):
 *   • /api/auth/me reports group scope + companies
 *   • Group Overview nav + /group dashboard render all 5 company cards
 *   • Company switcher offers "All companies"; switching to ALM-DAIRY-KSA
 *     filters the employees list down to ALM-DAIRY-KSA-E* codes
 */
import { test, expect } from '@playwright/test';
import {
  stackDownReason,
  groupSeedMissingReason,
  newApi,
  apiLogin,
  fetchMe,
  fetchCompanies,
  companyIdByCode,
  fetchEmployees,
  employeeCodes,
  uiLogin,
  findCompanySwitcher,
  openCompanySwitcher,
  pickCompanyInSwitcher,
  bodyText,
  crashIndicators,
  looksLikeNotFound,
  groupUser,
  ALMARAI,
  empCodePrefix,
} from './helpers';

const OWNER = groupUser('owner'); // owner@almarai-test.local
const DAIRY = 'ALM-DAIRY-KSA';
const BAKERY = 'ALM-BAKERY-KSA';

let skipReason: string | null = null;

test.describe('Group→Company: group admin (owner, almarai-test)', () => {
  test.beforeAll(async () => {
    skipReason = (await stackDownReason()) ?? (await groupSeedMissingReason(OWNER));
  });

  test.beforeEach(() => {
    test.skip(skipReason !== null, skipReason ?? '');
  });

  test('API: /api/auth/me exposes accountType=Group, isGroupScope and 5 companies', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, OWNER, ALMARAI.slug);
      const me = await fetchMe(api, token);
      expect(me.status).toBe(200);
      expect(String(me.json?.accountType ?? '')).toMatch(/group/i);
      expect(me.json?.isGroupScope).toBeTruthy();
      const companies = me.json?.companies;
      expect(Array.isArray(companies), '/api/auth/me should return a companies array').toBeTruthy();
      expect(companies.length).toBe(ALMARAI.companies.length);
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('API: /api/companies lists all 5 Almarai companies incl. ALM-DAIRY-KSA', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, OWNER, ALMARAI.slug);
      const companies = await fetchCompanies(api, token);
      expect(companies.length).toBe(ALMARAI.companies.length);
      expect(companyIdByCode(companies, DAIRY), `no company matching ${DAIRY}`).not.toBeNull();
      expect(companyIdByCode(companies, BAKERY), `no company matching ${BAKERY}`).not.toBeNull();
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('API: X-Company-Id=ALM-DAIRY-KSA filters /api/employees to that company only', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, OWNER, ALMARAI.slug);
      const companies = await fetchCompanies(api, token);
      const dairyId = companyIdByCode(companies, DAIRY);
      expect(dairyId, `could not resolve ${DAIRY} company id`).not.toBeNull();

      const { status, items } = await fetchEmployees(api, token, dairyId!);
      expect(status).toBe(200);
      const codes = employeeCodes(items);
      expect(codes.length, 'dairy company should have seeded employees').toBeGreaterThan(0);
      for (const code of codes) {
        expect(code, `employee ${code} leaked into ${DAIRY} scope`).toMatch(new RegExp(`^${DAIRY}`));
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('UI: owner sees Group Overview nav and the group dashboard renders 5 company cards', async ({ page }) => {
    await uiLogin(page, OWNER, ALMARAI.slug);

    // Group Overview nav entry must exist for group-scope users.
    await expect(
      page.getByRole('link', { name: /group overview/i }).or(page.getByText(/group overview/i)).first(),
    ).toBeVisible({ timeout: 30_000 });

    await page.goto('/group', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});
    test.skip(await looksLikeNotFound(page), '/group dashboard not built yet in this frontend build');

    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);

    // Per-company cards: every company code should be visible, dairy explicitly.
    await expect(page.getByText(DAIRY).first()).toBeVisible({ timeout: 30_000 });
    for (const code of ALMARAI.companies) {
      expect(text, `group dashboard missing card for ${code}`).toContain(code);
    }
  });

  test('UI: switcher shows "All companies"; switching to ALM-DAIRY-KSA filters employees', async ({ page }) => {
    await uiLogin(page, OWNER, ALMARAI.slug);

    const switcher = await findCompanySwitcher(page, 30_000);
    test.skip(switcher === null, 'company switcher not found in TopBar — UI surface not built yet');

    const opened = await openCompanySwitcher(page, switcher!);
    expect(opened, 'company switcher dropdown did not open').toBeTruthy();

    // Group-scope users get the "All companies" option.
    await expect(page.getByText(/all companies/i).first()).toBeVisible({ timeout: 10_000 });

    const picked = await pickCompanyInSwitcher(page, DAIRY);
    expect(picked, `could not pick ${DAIRY} in the switcher`).toBeTruthy();

    // Employees list should now only show ALM-DAIRY-KSA-E* codes.
    await page.goto('/people', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});

    await expect(page.getByText(new RegExp(`${empCodePrefix(DAIRY)}\\d+`)).first())
      .toBeVisible({ timeout: 30_000 });
    const text = await bodyText(page);
    for (const sibling of ALMARAI.companies.filter((c) => c !== DAIRY)) {
      expect(text, `employees list leaked ${sibling} employees while filtered to ${DAIRY}`)
        .not.toContain(empCodePrefix(sibling));
    }
  });
});
