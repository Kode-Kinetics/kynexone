/**
 * Platform admin — group tenant lifecycle:
 *   • create a Group tenant (API contract: POST /api/platform/tenants with
 *     accountType "Group"; UI attempt for the companyCreationMode selection
 *     skips gracefully while that surface is in flight)
 *   • open an existing group tenant's detail page
 *   • account-type downgrade on almarai-test is BLOCKED (409
 *     multiple_active_companies) while it has 5 active companies
 *
 * Credentials: PLATFORM_ADMIN_EMAIL / PLATFORM_ADMIN_PASSWORD envs
 * (defaults admin@platform.local / YourPassword123!, matching e2e/helpers.ts).
 */
import { test, expect } from '@playwright/test';
import {
  stackDownReason,
  newApi,
  tryPlatformApiLogin,
  tryApiLogin,
  fetchCompanies,
  listItems,
  bodyText,
  crashIndicators,
  groupUser,
  ALMARAI,
  PLATFORM_EMAIL,
  PLATFORM_PASSWORD,
} from './helpers';

let skipReason: string | null = null;
let platformToken: string | null = null;

const platformHeaders = () => ({ Authorization: `Bearer ${platformToken}` });

/** Find a tenant row by slug in GET /api/platform/tenants (array or paged). */
async function findTenantBySlug(slug: string): Promise<any | null> {
  const api = await newApi();
  try {
    const resp = await api.get('/api/platform/tenants', { headers: platformHeaders() });
    if (!resp.ok()) return null;
    const json = await resp.json().catch(() => null);
    const tenants = listItems(json);
    return tenants.find((t) => (t.slug ?? t.Slug) === slug) ?? null;
  } finally {
    await api.dispose().catch(() => {});
  }
}

test.describe('Group→Company: platform admin', () => {
  test.beforeAll(async () => {
    skipReason = await stackDownReason();
    if (skipReason) return;
    const api = await newApi();
    try {
      const login = await tryPlatformApiLogin(api);
      if (!login) {
        skipReason =
          `Platform admin login failed for ${PLATFORM_EMAIL}. Set PLATFORM_ADMIN_EMAIL / ` +
          `PLATFORM_ADMIN_PASSWORD to match your platform seed. See e2e/group-company/README.md.`;
        return;
      }
      platformToken = login.token;
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test.beforeEach(() => {
    test.skip(skipReason !== null, skipReason ?? '');
  });

  test('API: platform admin can create a Group tenant (accountType=Group)', async () => {
    test.setTimeout(120_000);
    const api = await newApi();
    const stamp = Date.now();
    const slug = `e2e-group-${stamp}`;
    let createdId: string | undefined;
    try {
      const create = await api.post('/api/platform/tenants', {
        headers: platformHeaders(),
        data: {
          name: `E2E Group ${stamp}`,
          slug,
          adminEmail: `admin@${slug}.local`,
          adminPassword: 'E2eGroupAdmin123!x',
          accountType: 'Group',
        },
      });
      expect(create.status(), await create.text()).toBeLessThan(300);

      const created = await findTenantBySlug(slug);
      expect(created, `created tenant ${slug} not found in /api/platform/tenants`).not.toBeNull();
      // `if (accountType !== undefined)` meant a creation that silently
      // downgraded the account type asserted nothing — and the accountType IS
      // the claim of this test. GET /api/platform/tenants (the listing) does
      // not carry accountType at all, so the guard could never have fired;
      // read it from the tenant DETAIL endpoint, which does report it.
      const id = created.id ?? created.Id;
      expect(id, `created tenant ${slug} has no id: ${JSON.stringify(created).slice(0, 300)}`).toBeTruthy();
      createdId = String(id);

      const detail = await api.get(`/api/platform/tenants/${createdId}`, { headers: platformHeaders() });
      expect(detail.status(), await detail.text()).toBe(200);
      const detailJson = await detail.json();
      const accountType = detailJson.accountType ?? detailJson.AccountType;
      expect(accountType, `created tenant ${slug} must report an accountType: ${JSON.stringify(detailJson).slice(0, 300)}`)
        .toBeDefined();
      expect(String(accountType)).toMatch(/group/i);
      // `createdId` is set above, before the assertions, so the finally block
      // still purges the throwaway tenant when one of them fails.
    } finally {
      if (createdId) {
        const deleted = await api.delete(`/api/platform/tenants/${createdId}?confirm=DELETE`, {
          headers: platformHeaders(),
        });
        expect(deleted.ok(), await deleted.text()).toBe(true);
        const purged = await api.delete(`/api/platform/tenants/${createdId}/purge?confirm=PURGE`, {
          headers: platformHeaders(),
        });
        expect(purged.ok(), await purged.text()).toBe(true);
      }
      await api.dispose().catch(() => {});
    }
  });

  test('UI: tenant creation offers a Group / company-creation-mode choice', async ({ page }) => {
    await page.goto('/platform/tenants', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});

    const createBtn = page.getByRole('button', { name: /(new|add|create).*tenant|tenant.*(new|add|create)/i }).first();
    await expect(createBtn).toBeVisible({ timeout: 10_000 });

    await createBtn.click();
    await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});

    // The Group→Company release adds an account-type / company-creation-mode choice.
    const groupChoice = page
      .getByText(/account type|company creation|group account|multi-?company/i)
      .first();
    await expect(groupChoice).toBeVisible({ timeout: 10_000 });

    // Do not actually submit from the UI (the API test covers creation end-to-end);
    // select the native option and assert its value. Native <option> nodes are not independently
    // visible in Chromium, so getByText(...).toBeVisible() is the wrong semantic assertion here.
    const accountType = page.locator('select').filter({
      has: page.locator('option[value="Group"]'),
    }).first();
    await expect(accountType).toBeVisible({ timeout: 10_000 });
    await expect(accountType.locator('option[value="Group"]')).toHaveCount(1);
    await accountType.selectOption('Group');
    await expect(accountType).toHaveValue('Group');
  });

  test('UI: existing group tenant (almarai-test) detail page opens', async ({ page }) => {
    const almarai = await findTenantBySlug(ALMARAI.slug);
    test.skip(!almarai, `tenant ${ALMARAI.slug} not found — enterprise group seed missing (SEED_ENTERPRISE_TEST_DATA=true)`);
    const id = almarai.id ?? almarai.Id;

    await page.goto(`/platform/tenants/${id}`, { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});

    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);
    expect(text.toLowerCase()).toContain(ALMARAI.slug.split('-')[0]); // "almarai" appears in name/slug
  });

  test('API: account-type downgrade on almarai-test is blocked (multiple active companies)', async () => {
    const almarai = await findTenantBySlug(ALMARAI.slug);
    test.skip(!almarai, `tenant ${ALMARAI.slug} not found — enterprise group seed missing (SEED_ENTERPRISE_TEST_DATA=true)`);
    const id = almarai.id ?? almarai.Id;

    // Safety: only attempt the downgrade when the tenant really has >1 active
    // company, otherwise the PUT would SUCCEED and mutate the seed data.
    const api = await newApi();
    try {
      const owner = await tryApiLogin(api, groupUser('owner'), ALMARAI.slug);
      test.skip(!owner, 'group owner login unavailable — cannot verify active company count before downgrade attempt');
      const companies = await fetchCompanies(api, owner!.token).catch(() => []);
      const active = companies.filter((c) => (c.isActive ?? c.IsActive) !== false);
      test.skip(active.length <= 1, `almarai-test has ${active.length} active companies — downgrade would not be blocked; seed incomplete`);

      const resp = await api.put(`/api/platform/tenants/${id}/account-type`, {
        headers: platformHeaders(),
        data: { accountType: 'SingleCompany' },
      });

      try {
        expect(resp.status(), 'downgrade must be blocked with 409 Conflict').toBe(409);
        const body = await resp.json().catch(() => ({} as any));
        const message = JSON.stringify(body);
        expect(message).toMatch(/multiple[_ ]active[_ ]companies|multiple active companies/i);
      } finally {
        // Paranoia: if the downgrade unexpectedly succeeded, restore Group so
        // the seed tenant is not left corrupted for the rest of the suite.
        if (resp.ok()) {
          await api.put(`/api/platform/tenants/${id}/account-type`, {
            headers: platformHeaders(),
            data: { accountType: 'Group' },
          }).catch(() => {});
        }
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });
});
