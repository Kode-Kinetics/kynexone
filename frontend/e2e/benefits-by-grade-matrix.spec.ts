import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';

/**
 * Benefits by grade (Release A R1) in a real browser, against route mocks. The mocks are not hand-written: they are the
 * backend's own read models — EntitlementMatrixService's matrix, publish dry run and legacy-import preview — serialized
 * by EntitlementMatrixTests.BrowserLaneFixtures_AreTheRealReadModels (regenerate with KYNEX_R1_FIXTURES_DIR). Each
 * screen is driven in English and Arabic: the matrix editor, the publish dry run and the import review list.
 *
 * Run: npx playwright test -c e2e/playwright.matrix.config.ts (starts `next dev` on MATRIX_FIXTURE_PORT, default 5197).
 * R1_SCREENSHOT_DIR keeps the screenshots outside the test output folder.
 */

const fixtures = path.join(__dirname, 'fixtures', 'benefits-by-grade');
const load = (name: string) => JSON.parse(fs.readFileSync(path.join(fixtures, `${name}.json`), 'utf8'));
const groupMatrix = load('matrix-group');
const logisticsMatrix = load('matrix-logistics');
const dryRun = load('publish-dry-run');
const legacyPreview = load('legacy-preview');
const companies: { id: string; name: string }[] = load('companies');

type Locale = 'en' | 'ar';
const say = (locale: Locale, key: string, values: Record<string, string | number> = {}) =>
  Object.entries(values).reduce((text, [k, v]) => text.replaceAll(`{${k}}`, String(v)), (LOCALE_DICTS[locale] as Record<string, string>)[key] ?? key);

const gradeId = (code: string) => groupMatrix.grades.find((g: { code: string }) => g.code === code).id as string;
const component = (code: string) => groupMatrix.components.find((c: { code: string }) => c.code === code);
const benefitName = (locale: Locale, code: string) => (locale === 'ar' ? component(code).nameAr : component(code).nameEn) as string;
const cellButton = (page: Page, locale: Locale, code: string, grade: string) =>
  page.getByRole('button', { name: new RegExp(`^${say(locale, '{benefit} for {grade}: {value}', { benefit: benefitName(locale, code), grade, value: '' })}`) });

async function shot(page: Page, name: string) {
  const dir = process.env.R1_SCREENSHOT_DIR ?? test.info().outputDir;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: path.join(dir, `${name}.png`), fullPage: true });
}

async function boot(page: Page, locale: Locale) {
  const errors: string[] = [];
  const publishes: { dryRun: boolean; body: any }[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(([lang]) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', lang);
  }, [locale]);
  await page.route('**/api/**', route => {
    const request = route.request(); const url = new URL(request.url()); const p = url.pathname;
    if (p === '/api/auth/me') return route.fulfill({ json: {
      id: 'user-1', tenantId: 'tenant-1', tenantSlug: 'masar', fullName: 'Hala Al-Qahtani', roles: ['HR Director'], isGroupScope: true,
      permissions: ['entitlements.read', 'entitlements.manage'],
      companies: companies.map((c, i) => ({ ...c, code: `M${i + 1}`, countryCode: 'SA', isActive: true })),
    } });
    if (p === '/api/entitlements/matrix' && request.method() === 'GET')
      return route.fulfill({ json: url.searchParams.get('companyId') ? logisticsMatrix : groupMatrix });
    if (p === '/api/entitlements/matrix' && request.method() === 'PUT') {
      const isDry = url.searchParams.get('dryRun') === 'true';
      publishes.push({ dryRun: isDry, body: request.postDataJSON() });
      return route.fulfill({ json: isDry ? dryRun : { ...dryRun, dryRun: false, matrix: groupMatrix } });
    }
    if (p === '/api/entitlements/matrix/import-legacy') return route.fulfill({ json: legacyPreview });
    if (p === '/api/tenant-admin/localization') return route.fulfill({ json: { countryCode: 'SA', currencyCode: 'SAR', defaultLanguage: locale } });
    if (p.includes('/features/') || p === '/api/notifications') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto('/benefits/by-grade');
  await expect(page.getByRole('heading', { name: say(locale, 'Benefits by grade'), level: 1 })).toBeVisible();
  return { errors, publishes };
}

for (const locale of ['en', 'ar'] as const) {
  test.describe(`benefits by grade (${locale})`, () => {
    test('matrix editor: the published grid, its gaps and the floors that cannot be skipped', async ({ page }) => {
      const { errors } = await boot(page, locale);
      await expect(page.locator('html')).toHaveAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');
      await expect(page.getByText(say(locale, '{count} grade value(s) still need setting.', { count: groupMatrix.gaps.length }))).toBeVisible();
      // The G3 housing value comes from the read model: 25% of basic, with a scheduled change on the date it carries.
      await expect(cellButton(page, locale, 'HOUSING', 'G3')).toBeVisible();
      await expect(page.getByText(say(locale, 'Required by law')).first()).toBeVisible();
      await shot(page, `matrix-editor-${locale}`);
      expect(errors).toEqual([]);
    });

    test('publish dry run: what changes, who it reaches, and the not-yet-started value it replaces', async ({ page }) => {
      const { errors, publishes } = await boot(page, locale);
      await cellButton(page, locale, 'HOUSING', 'G3').click();
      await page.getByLabel(say(locale, 'Percent of basic salary')).fill('35');
      await page.getByRole('button', { name: say(locale, 'Done editing') }).click();
      await cellButton(page, locale, 'PER_DIEM', 'G5').click();
      await page.getByRole('radio', { name: say(locale, 'Offered'), exact: true }).check();
      await page.getByLabel(say(locale, 'Amount ({currency})', { currency: groupMatrix.currency })).fill('500');
      await page.getByRole('button', { name: say(locale, 'Done editing') }).click();
      await page.getByLabel(say(locale, 'Effective from')).fill('2026-11-01');

      await expect(page.getByText(say(locale, 'Reaches {now} employee(s) on that date and {later} at their next contract year.',
        { now: dryRun.affectedNow, later: dryRun.affectedAtRenewal }))).toBeVisible();
      await expect(page.getByText(say(locale, '{count} value(s) that have not started yet will be replaced on their start date.', { count: dryRun.superseded }))).toBeVisible();
      const last = publishes.filter(x => x.dryRun).at(-1)!;
      expect(last.body.effectiveFrom).toBe('2026-11-01');
      expect(last.body.cells.map((c: any) => [c.gradeId, c.componentCode]).sort()).toEqual([[gradeId('G3'), 'HOUSING'], [gradeId('G5'), 'PER_DIEM']].sort());
      expect(last.body.cells.find((c: any) => c.componentCode === 'HOUSING').rate).toBeCloseTo(0.35);
      expect(publishes.some(x => !x.dryRun)).toBe(false);
      await shot(page, `publish-dry-run-${locale}`);
      expect(errors).toEqual([]);
    });

    test('add a benefit: it appears for every grade as a gap, is set like any cell, and can be removed while unused', async ({ page }) => {
      const { errors } = await boot(page, locale);
      const parking = {
        code: 'BEN_PARKING', nameEn: 'Parking', nameAr: 'موقف', class: 'Contractual', floor: 'None', group: 'Contract',
        allowedValueTypes: ['Amount', 'InKind', 'Quantity'], allowedCoverageTiers: [], allowedDependantScopes: ['None'],
        allowedLimitPeriods: ['Annual'], defaultLimitPeriod: 'Annual', isFloor: false, canBeSkipped: true, isLoanFacility: false,
        floorReason: null, isCustom: true,
      };
      const withParking = {
        ...groupMatrix,
        components: [...groupMatrix.components, parking],
        offerings: [...groupMatrix.offerings, { componentCode: 'BEN_PARKING', mode: 'Group', offered: true, skippedFrom: null, changesOn: null, canBeSkipped: true }],
        gaps: [...groupMatrix.gaps, ...groupMatrix.grades.map((g: { id: string }) => ({ gradeId: g.id, componentCode: 'BEN_PARKING', scheduledFrom: null }))],
      };
      let exists = false;
      const calls: { method: string; path: string; body: any }[] = [];
      await page.route('**/api/entitlements/**', route => {
        const request = route.request(); const p = new URL(request.url()).pathname;
        if (p === '/api/entitlements/components' && request.method() === 'POST') {
          calls.push({ method: 'POST', path: p, body: request.postDataJSON() });
          exists = true;
          return route.fulfill({ json: parking });
        }
        if (p === '/api/entitlements/components/BEN_PARKING/remove' && request.method() === 'POST') {
          calls.push({ method: 'POST', path: p, body: null });
          exists = false;
          return route.fulfill({ json: { removed: 'BEN_PARKING' } });
        }
        if (p === '/api/entitlements/matrix' && request.method() === 'GET') return route.fulfill({ json: exists ? withParking : groupMatrix });
        return route.fallback();
      });
      const benefit = (locale === 'ar' ? parking.nameAr : parking.nameEn) as string;

      await page.getByRole('button', { name: say(locale, 'Add a benefit'), exact: true }).click();
      await page.getByLabel(say(locale, 'Benefit name (English)')).fill('Parking');
      await page.getByLabel(say(locale, 'Benefit name (Arabic)')).fill('موقف');
      await page.getByRole('button', { name: say(locale, 'Add benefit'), exact: true }).click();

      await expect(page.getByText(say(locale, '{benefit} was added. Set its value for each grade, then publish.', { benefit: locale === 'ar' ? 'موقف' : 'Parking' }))).toBeVisible();
      expect(calls[0]).toMatchObject({ path: '/api/entitlements/components', body: { nameEn: 'Parking', nameAr: 'موقف' } });
      await expect(page.getByText(say(locale, 'Added by you'))).toBeVisible();
      // Every grade is a gap until it has a value, so the new benefit can't be silently missing from a package.
      await expect(page.getByRole('button', { name: new RegExp(`^${say(locale, '{benefit} for {grade}: {value}', { benefit, grade: 'G1', value: say(locale, 'Not set') })}`) })).toBeVisible();
      await expect(page.getByText(say(locale, '{count} grade value(s) still need setting.', { count: withParking.gaps.length }))).toBeVisible();

      // Its cell offers what a contract benefit allows, and a count reads "number of items", not tickets.
      await cellButton(page, locale, 'BEN_PARKING', 'G1').click();
      await expect(page.getByRole('option', { name: say(locale, 'Number of items') })).toHaveCount(1);
      await expect(page.getByRole('option', { name: say(locale, 'Number of tickets') })).toHaveCount(0);
      await page.getByRole('button', { name: say(locale, 'Done editing') }).click();
      await shot(page, `add-benefit-${locale}`);

      // Unused, so it can be removed again.
      await page.getByRole('button', { name: say(locale, 'Remove benefit'), exact: true }).click();
      await page.getByRole('button', { name: say(locale, 'Yes, remove it'), exact: true }).click();
      await expect(page.getByText(say(locale, '{benefit} was removed.', { benefit }))).toBeVisible();
      expect(calls[1]).toMatchObject({ path: '/api/entitlements/components/BEN_PARKING/remove' });
      await expect(page.getByText(say(locale, 'Added by you'))).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('import review: every old line says what happens to it, and nothing is guessed', async ({ page }) => {
      const { errors } = await boot(page, locale);
      await page.getByText(say(locale, 'Bring in the old grade pay scales')).click();
      await page.getByRole('button', { name: say(locale, 'Preview'), exact: true }).click();
      await expect(page.getByText(say(locale, '{import} can be imported · {skip} need setting by hand',
        { import: legacyPreview.toImport, skip: legacyPreview.skipped }))).toBeVisible();
      await expect(page.getByText(say(locale, 'This line is not an allowance paid to the employee. Set the value in the grid if one applies.'))).toBeVisible();
      await expect(page.getByText(say(locale, 'Education is a yearly amount per child, up to a number of children. The old line says neither, so enter both in the grid.'))).toBeVisible();
      await expect(page.getByRole('button', { name: say(locale, 'Import {count} value(s)', { count: legacyPreview.toImport }) })).toBeVisible();
      await page.getByText(say(locale, 'Bring in the old grade pay scales')).scrollIntoViewIfNeeded();
      await shot(page, `import-review-${locale}`);
      expect(errors).toEqual([]);
    });
  });
}
