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

/**
 * One labelled row of the profile card's definition list (Country / Jurisdiction
 * / …). Scoping to the card is the point: the nav shell contains "Saudi
 * Compliance", so any assertion made against the whole page body is satisfied
 * by the chrome rather than by the profile.
 */
function profileRow(page: Page, label: string) {
  return page
    .locator('dl > div')
    .filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}$`) }) })
    .first();
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
    // A mid-test skip on the surface under test is one more way to prove nothing:
    // a /compliance-profiles route that 404s, or a company selector that stopped
    // rendering, silently turned this compliance check into a no-op. Assert both.
    expect(await looksLikeNotFound(page), '/compliance-profiles must render, not 404').toBe(false);

    const selected = await selectCompanyOnPage(page, 'ALM-DAIRY-KSA');
    expect(selected, 'the company selector on /compliance-profiles must offer ALM-DAIRY-KSA').toBe(true);

    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);

    // Country: assert the PROFILE's own Country row. Matching /\bSA\b|Saudi/i
    // against the whole page was satisfied by the "Saudi Compliance" nav item,
    // so the profile card could show any country — or not render at all.
    const countryRow = profileRow(page, 'Country');
    await expect(countryRow, 'the compliance profile must render a Country row').toBeVisible({ timeout: 30_000 });
    await expect(countryRow.locator('dd'), 'ALM-DAIRY-KSA must resolve to the KSA (SA) profile').toHaveText('SA');

    // Required-field row for IqamaNumber must be present…
    const row = page.locator('table tbody tr').filter({ hasText: /iqama/i }).first();
    await expect(row, 'the required-fields table must contain an IqamaNumber row').toBeVisible({ timeout: 30_000 });

    // …with a missing count > 0 (seed data intentionally has gaps).
    // The old code matched a broad container (`tr, [role=row], li, [class*=row],
    // [class*=card]`), fell back to the WHOLE PAGE text when nothing matched,
    // and then accepted ANY digit found in that text — so it never actually
    // read the Missing column. Read that one cell instead.
    const missingCell = row.locator('td').last();
    const missingText = (await missingCell.innerText()).trim();
    expect(missingText, `IqamaNumber "Missing" cell must be a number, got "${missingText}"`).toMatch(/^\d+$/);
    expect(
      Number(missingText),
      'the seed leaves Iqama gaps, so the IqamaNumber missing count must be > 0',
    ).toBeGreaterThan(0);

    // Advisory disclaimer — the profile is guidance, not legal certification.
    await expect(page.getByText(/not legal certification/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test('contrast: TATA-TCS-IN profile shows country IN', async ({ page }) => {
    await uiLogin(page, TATA_COMPLIANCE, TATA.slug);
    await openComplianceProfiles(page);
    expect(await looksLikeNotFound(page), '/compliance-profiles must render, not 404').toBe(false);

    const selected = await selectCompanyOnPage(page, 'TATA-TCS-IN');
    expect(selected, 'the company selector on /compliance-profiles must offer TATA-TCS-IN').toBe(true);

    const text = await bodyText(page);
    expect(crashIndicators(text)).toEqual([]);
    // Same scoping as the KSA case: assert the profile card's Country row, not
    // a two-letter match anywhere in the page chrome.
    const countryRow = profileRow(page, 'Country');
    await expect(countryRow, 'the compliance profile must render a Country row').toBeVisible({ timeout: 30_000 });
    await expect(countryRow.locator('dd'), 'TATA-TCS-IN must resolve to the India (IN) profile').toHaveText('IN');
    // And it must NOT present the KSA-only requirement.
    expect(text.toLowerCase(), 'an Indian company must not require Iqama').not.toContain('iqama');
  });
});
