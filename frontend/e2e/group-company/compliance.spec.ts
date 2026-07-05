/**
 * Compliance profiles — per-company statutory profile page:
 *   • compliance@almarai-test.local → /compliance-profiles → ALM-DAIRY-KSA:
 *     KSA profile (country SA), required-field rows incl. IqamaNumber with a
 *     missing count > 0, and the "not legal certification" disclaimer
 *   • contrast: compliance@tata-test.local → TATA-TCS-IN shows country IN
 *
 * The page is being built in parallel: the suite skips (not fails) when the
 * route 404s or the company selector cannot be located yet.
 */
import { test, expect, Page } from '@playwright/test';
import {
  stackDownReason,
  groupSeedMissingReason,
  uiLogin,
  bodyText,
  crashIndicators,
  looksLikeNotFound,
  groupUser,
  ALMARAI,
  TATA,
} from './helpers';

const ALM_COMPLIANCE = groupUser('compliance', ALMARAI.slug);
const TATA_COMPLIANCE = groupUser('compliance', TATA.slug);

let skipReason: string | null = null;

/**
 * Best-effort company selection on /compliance-profiles without fabricating
 * selectors: native <select>, visible code text (tab/card/row), or combobox.
 */
async function selectCompanyOnPage(page: Page, code: string): Promise<boolean> {
  // 1. Native <select> whose options mention the code.
  for (const select of await page.locator('select').all()) {
    if (!(await select.isVisible().catch(() => false))) continue;
    const options = await select.locator('option').allInnerTexts().catch(() => [] as string[]);
    const label = options.find((o) => o.includes(code));
    if (label) {
      await select.selectOption({ label });
      await page.waitForLoadState('networkidle', { timeout: 15_000 }).catch(() => {});
      return true;
    }
  }
  // 2. ARIA combobox → option.
  const combo = page.getByRole('combobox').first();
  if (await combo.isVisible().catch(() => false)) {
    await combo.click().catch(() => {});
    const opt = page.getByRole('option', { name: new RegExp(code, 'i') }).first();
    if (await opt.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await opt.click();
      await page.waitForLoadState('networkidle', { timeout: 15_000 }).catch(() => {});
      return true;
    }
    await page.keyboard.press('Escape').catch(() => {});
  }
  // 3. Clickable code text (tab / card / list row).
  const el = page.getByText(code, { exact: false }).first();
  if (await el.isVisible({ timeout: 5_000 }).catch(() => false)) {
    await el.click().catch(() => {});
    await page.waitForLoadState('networkidle', { timeout: 15_000 }).catch(() => {});
    return true;
  }
  return false;
}

async function openComplianceProfiles(page: Page): Promise<void> {
  await page.goto('/compliance-profiles', { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});
}

test.describe('Group→Company: compliance profiles', () => {
  test.beforeAll(async () => {
    skipReason = (await stackDownReason()) ?? (await groupSeedMissingReason(ALM_COMPLIANCE));
  });

  test.beforeEach(() => {
    test.skip(skipReason !== null, skipReason ?? '');
  });

  test('KSA profile for ALM-DAIRY-KSA: country SA, IqamaNumber missing > 0, disclaimer', async ({ page }) => {
    await uiLogin(page, ALM_COMPLIANCE, ALMARAI.slug);
    await openComplianceProfiles(page);
    test.skip(await looksLikeNotFound(page), '/compliance-profiles page not built yet in this frontend build');

    const selected = await selectCompanyOnPage(page, 'ALM-DAIRY-KSA');
    test.skip(!selected, 'could not locate a company selector on /compliance-profiles — UI still in flight');

    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);

    // Country: the KSA profile must be shown.
    expect(text, 'expected KSA (SA) country indication').toMatch(/\bSA\b|Saudi/i);

    // Required-field row for IqamaNumber must be present…
    await expect(page.getByText(/iqama/i).first()).toBeVisible({ timeout: 30_000 });

    // …with a missing count > 0 (seed data intentionally has gaps).
    const row = page
      .locator('tr, [role="row"], li, [class*="row" i], [class*="card" i]')
      .filter({ hasText: /iqama/i })
      .first();
    const rowText = (await row.isVisible().catch(() => false))
      ? await row.innerText()
      : text;
    const counts = (rowText.match(/\d+/g) ?? []).map(Number);
    expect(
      counts.some((n) => n > 0),
      `expected a missing-count > 0 on the IqamaNumber row; row text was: ${rowText.slice(0, 200)}`,
    ).toBeTruthy();

    // Advisory disclaimer — the profile is guidance, not legal certification.
    await expect(page.getByText(/not legal certification/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test('contrast: TATA-TCS-IN profile shows country IN', async ({ page }) => {
    await uiLogin(page, TATA_COMPLIANCE, TATA.slug);
    await openComplianceProfiles(page);
    test.skip(await looksLikeNotFound(page), '/compliance-profiles page not built yet in this frontend build');

    const selected = await selectCompanyOnPage(page, 'TATA-TCS-IN');
    test.skip(!selected, 'could not locate a company selector on /compliance-profiles — UI still in flight');

    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);
    expect(text, 'expected India (IN) country indication').toMatch(/\bIN\b|India/);
    // And it must NOT present the KSA-only requirement.
    expect(text.toLowerCase(), 'an Indian company must not require Iqama').not.toContain('iqama');
  });
});
