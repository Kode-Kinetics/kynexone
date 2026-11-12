import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * Saudi Compliance -> Saudization, fixture lane (e2e/playwright.fixture.config.ts).
 *
 * A band is only worth showing when the thresholds behind it have been checked against MHRSD. When the
 * thresholds are not verified for the activity the tab keeps the Saudization percentage (a fact computed from the
 * workforce) and shows NO band, colour, floor or distance-to-band, with one honest line saying why. When they are
 * verified the band, floor and distances are shown exactly as before.
 */
const COMPANY = { id: 'c1', legalNameEn: 'IntelliFlow KSA', tradeName: 'IntelliFlow KSA', countryCode: 'SA', defaultCurrency: 'SAR', isActive: true };
const USER = {
  id: 'u1', tenantId: 't1', tenantSlug: 'intelliflow', email: 'admin@intelliflow.test', fullName: 'Yaser Al-Ghamdi',
  roles: ['Admin'], accountType: 'Tenant', isGroupScope: true, companies: [],
  permissions: ['dashboard.read', 'compliance.read', 'qiwa.read', 'employees.read'],
};

const standing = (over: Record<string, unknown> = {}) => ({
  companyId: 'c1', companyName: 'IntelliFlow KSA', asOf: '2026-11-12', activityCode: 'GENERAL_UNVERIFIED',
  activityNameEn: 'General (illustrative)', activityNameAr: 'عام (توضيحي)', sizeTierCode: 'SmallA', sizeTierNameEn: 'Small A', sizeTierRank: 2,
  saudiWeighted: 6, totalWeighted: 9, achievedPercent: 66.67, rawSaudiHeadcount: 6, rawTotalHeadcount: 9,
  band: 'Platinum', bandRank: 4, restrictsServices: false, consequenceSummary: 'No Nitaqat service restrictions apply at this band.',
  currentBandFloorPercent: 45.15,
  nextBandUp: null,
  bandBelow: { band: 'HighGreen', requiredPercent: 30, percentGap: 15.15, saudiHiresRequired: null, infeasible: null },
  scenario: { saudiHiresToNextBand: null, nextBand: null, expatHiresBeforeDowngrade: 2, bandBelow: 'HighGreen', saudiLeaversBeforeDowngrade: 1 },
  breakdown: [],
  allInputsVerified: false, unverifiedInputs: ['Platinum threshold (45.15%)'],
  qiwaReportedBand: null, qiwaReportedOn: null, disagreesWithQiwa: false,
  bandingMethod: 'nitaqat_mutawar_curve', bandingMethodNote: 'Banded by the MHRSD Nitaqat Mutawar curve.',
  thresholdsVerified: false,
  ...over,
});

async function open(page: Page, s: Record<string, unknown>) {
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
  });
  await page.route('**/api/**', (route: Route) => {
    const p = new URL(route.request().url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(USER);
    if (p === '/api/companies') return json({ items: [COMPANY], total: 1, page: 1, pageSize: 100 });
    if (p === '/api/saudi-compliance/nitaqat') return json({ ok: true, standing: s, refusal: null });
    if (p === '/api/saudi-compliance/nitaqat/trend') return json({ ok: true, companyId: 'c1', points: [], changePercentagePoints: null, direction: null, projectedBandWarning: null, refusal: null });
    if (p === '/api/saudi-compliance/nitaqat/grid') return json({ activitiesTotal: 40, activitiesWithAnyGrid: 0, activitiesWithCompleteGrid: 0, activitiesFullyVerified: 0, configurationRequiredNotice: null, activities: [], sizeTierCodes: [] });
    if (p === '/api/saudi-compliance/dashboard') return json({ error: 'not_mocked' }, 500);
    if (p.startsWith('/api/features') || p === '/api/notifications') return json([]);
    return json([]);
  });
  await page.goto('/saudi-compliance');
  await page.getByRole('button', { name: 'Saudization', exact: true }).click();
  await expect(page.getByText('66.67%').first()).toBeVisible({ timeout: 30_000 });
}

test.describe('Saudization tab when the thresholds are not verified', () => {
  test('shows the percentage and the working, but no band, colour, floor or distance-to-band', async ({ page }) => {
    await open(page, standing());
    const main = page.locator('main');
    await expect(main).toContainText('6 / 9 weighted units');
    await expect(main).not.toContainText('Platinum');
    await expect(main).not.toContainText('45.15');
    await expect(main).not.toContainText('Current band floor');
    await expect(main).not.toContainText('Next band up');
    await expect(main).not.toContainText('Headroom above the floor');
    await expect(main).not.toContainText('Saudi hires to next band');
    await expect(page.getByTestId('nitaqat-band-withheld')).toBeVisible();
    if (process.env.SHOT_DIR) await page.screenshot({ path: `${process.env.SHOT_DIR}/saudization-band-withheld.png`, fullPage: true });
  });

  test('the note says why, in one line', async ({ page }) => {
    await open(page, standing());
    await expect(page.getByTestId('nitaqat-band-withheld'))
      .toContainText('No band is shown: the MHRSD thresholds for this activity are not loaded or not verified.');
  });

  test('the note is in Arabic when the language is Arabic', async ({ page }) => {
    await page.addInitScript(() => { localStorage.setItem('kynexone-locale-choice-v2', 'ar'); });
    await open(page, standing());
    await expect(page.getByTestId('nitaqat-band-withheld')).toContainText('لا يُعرض نطاق');
    if (process.env.SHOT_DIR) await page.screenshot({ path: `${process.env.SHOT_DIR}/saudization-band-withheld-ar.png`, fullPage: true });
  });
});

test.describe('Saudization tab when the thresholds are verified', () => {
  test('still shows the band, floor and distances', async ({ page }) => {
    await open(page, standing({
      thresholdsVerified: true, allInputsVerified: true, unverifiedInputs: [], activityCode: 'RETAIL', activityNameEn: 'Retail',
    }));
    const main = page.locator('main');
    await expect(main).toContainText('Platinum');
    await expect(main).toContainText('Current band floor');
    await expect(main).toContainText('45.15%');
    await expect(page.getByTestId('nitaqat-band-withheld')).toHaveCount(0);
  });

  test('a server that does not say (older API) is treated as verified, as before', async ({ page }) => {
    const { thresholdsVerified: _v, ...old } = standing({ allInputsVerified: true, unverifiedInputs: [] });
    await open(page, old);
    await expect(page.locator('main')).toContainText('Platinum');
    await expect(page.getByTestId('nitaqat-band-withheld')).toHaveCount(0);
  });
});
