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
const cellNamed = (page: Page, locale: Locale, benefit: string, grade: string) =>
  page.getByRole('button', { name: new RegExp(`^${say(locale, '{benefit} for {grade}: {value}', { benefit, grade, value: '' })}`) });
const cellButton = (page: Page, locale: Locale, code: string, grade: string) => cellNamed(page, locale, benefitName(locale, code), grade);

// The toolbar button, which opens the Add a benefit dialog. The dialog's own submit has the same words, so tests reach it through the dialog.
const addBenefitButton = (page: Page, locale: Locale) => page.getByRole('button', { name: say(locale, 'Add benefit'), exact: true }).first();
const addDialog = (page: Page, locale: Locale) => page.getByRole('dialog', { name: say(locale, 'Add a benefit'), exact: true });
const submitAdd = (page: Page, locale: Locale) => addDialog(page, locale).getByRole('button', { name: say(locale, 'Add benefit'), exact: true });
/** The row's actions menu (Skip for this company, Correct GL, Remove benefit...). */
const rowMenu = (page: Page, locale: Locale, benefit: string) => page.getByRole('button', { name: say(locale, 'Actions for {benefit}', { benefit }), exact: true });

/** The box of something that slides in: read once it has stopped moving. */
async function settledBox(locator: import('@playwright/test').Locator) {
  let last = await locator.boundingBox();
  for (let i = 0; i < 40; i++) {
    await locator.page().waitForTimeout(50);
    const next = await locator.boundingBox();
    if (next && last && next.x === last.x && next.y === last.y && next.width === last.width) return next;
    last = next;
  }
  return last;
}

async function shot(page: Page, name: string) {
  const dir = process.env.R1_SCREENSHOT_DIR ?? test.info().outputDir;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: path.join(dir, `${name}.png`), fullPage: true });
}

async function boot(page: Page, locale: Locale, options: { matrix?: unknown; companyMatrix?: unknown; permissions?: string[]; roles?: string[]; plans?: unknown[]; publishRefusal?: unknown } = {}) {
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
      id: 'user-1', tenantId: 'tenant-1', tenantSlug: 'masar', fullName: 'Hala Al-Qahtani', roles: options.roles ?? ['HR Director'], isGroupScope: true,
      permissions: options.permissions ?? ['entitlements.read', 'entitlements.manage'],
      companies: companies.map((c, i) => ({ ...c, code: `M${i + 1}`, countryCode: 'SA', isActive: true })),
    } });
    if (p === '/api/entitlements/matrix' && request.method() === 'GET')
      return route.fulfill({ json: url.searchParams.get('companyId') ? (options.companyMatrix ?? logisticsMatrix) : (options.matrix ?? groupMatrix) });
    if (p === '/api/compensation/benefits/plans') return route.fulfill({ json: options.plans ?? [] });
    if (p === '/api/entitlements/matrix' && request.method() === 'PUT') {
      const isDry = url.searchParams.get('dryRun') === 'true';
      publishes.push({ dryRun: isDry, body: request.postDataJSON() });
      if (options.publishRefusal && !isDry) return route.fulfill({ status: 400, json: options.publishRefusal });
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


/** The published matrix with five more grades (G6-G10, valued like G5) and a second fixed monthly allowance, so the grid is wider than the screen and has a total. */
function wideMatrix() {
  const last = groupMatrix.grades.at(-1);
  const extra = [6, 7, 8, 9, 10].map(n => ({ ...last, id: `00000000-0000-4000-8000-0000000000${String(n).padStart(2, '0')}`, code: `G${n}`, name: `Grade ${n}`, nameAr: `الدرجة ${n}`, level: last.level + n }));
  const grades = [...groupMatrix.grades, ...extra];
  const cells = [...groupMatrix.cells, ...extra.flatMap(g => groupMatrix.cells.filter((c: any) => c.gradeId === last.id).map((c: any) => ({ ...c, gradeId: g.id, cellId: `${c.cellId}-${g.code}` })))];
  const phone = {
    code: 'ALW_PHONE', nameEn: 'Phone allowance', nameAr: 'بدل هاتف', class: 'QiwaWage', floor: 'None', group: 'Wage',
    allowedValueTypes: ['Amount', 'PercentOfBasic'], allowedCoverageTiers: [], allowedDependantScopes: ['None'], allowedLimitPeriods: ['Monthly'],
    defaultLimitPeriod: 'Monthly', isFloor: false, canBeSkipped: false, isLoanFacility: false, floorReason: null, isCustom: true,
  };
  const phoneCells = grades.map(g => ({ ...cells.find((c: any) => c.componentCode === 'TRANSPORT' && c.valueType === 'Amount'), gradeId: g.id, componentCode: 'ALW_PHONE', cellId: `phone-${g.code}`, amount: 150 }));
  return { ...groupMatrix, grades, cells: [...cells, ...phoneCells], components: [...groupMatrix.components, phone],
    offerings: [...groupMatrix.offerings, { componentCode: 'ALW_PHONE', mode: 'Group', offered: true, skippedFrom: null, changesOn: null, canBeSkipped: false }] };
}

for (const locale of ['en', 'ar'] as const) {
  test.describe(`benefits by grade (${locale})`, () => {
    test('matrix editor: the published grid, its gaps and the floors that cannot be skipped', async ({ page }) => {
      const { errors } = await boot(page, locale);
      await expect(page.locator('html')).toHaveAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');
      // The status band says how many values are set and nothing it cannot back: no Finance, no unpublished changes, no later start.
      const total = groupMatrix.grades.length * (groupMatrix.components.length);
      await expect(page.getByTestId('matrix-status')).toContainText(say(locale, '{set} of {total} values set', { set: total - groupMatrix.gaps.length, total }));
      // The G3 housing value comes from the read model: 25% of basic, with a scheduled change on the date it carries.
      await expect(cellButton(page, locale, 'HOUSING', 'G3')).toBeVisible();
      // "Required by law" is in the benefit's info, not printed in the column.
      await expect(page.getByText(say(locale, 'Required by law'), { exact: true })).toBeHidden();
      await page.getByRole('button', { name: say(locale, 'About {field}', { field: benefitName(locale, 'HOUSING') }) }).focus();
      await expect(page.getByRole('tooltip').filter({ hasText: say(locale, 'Required by law') })).toBeVisible();
      await shot(page, `matrix-editor-${locale}`);
      expect(errors).toEqual([]);
    });

    test('publish dry run: what changes, who it reaches, and the not-yet-started value it replaces', async ({ page }) => {
      const { errors, publishes } = await boot(page, locale);
      await cellButton(page, locale, 'HOUSING', 'G3').click();
      await page.getByLabel(say(locale, 'Percent of basic salary')).fill('35');
      await page.getByRole('button', { name: say(locale, 'Done editing'), exact: true }).click();
      await cellButton(page, locale, 'PER_DIEM', 'G5').click();
      await page.getByRole('radio', { name: say(locale, 'Offered'), exact: true }).check();
      await page.getByLabel(say(locale, 'Amount ({currency})', { currency: groupMatrix.currency })).fill('500');
      await page.getByRole('button', { name: say(locale, 'Done editing'), exact: true }).click();
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

    test('publish without touching the date: it starts today, and a binder refusal never reaches the screen', async ({ page }) => {
      const { errors, publishes } = await boot(page, locale, { publishRefusal: {
        title: 'One or more validation errors occurred.',
        errors: { req: ['The req field is required.'], '$.effectiveFrom': ['The JSON value could not be converted to Zayra.Api.Controllers.PublishMatrixRequest. Path: $.effectiveFrom | LineNumber: 0 | BytePositionInLine: 120.'] },
      } });
      await cellButton(page, locale, 'HOUSING', 'G3').click();
      await page.getByLabel(say(locale, 'Percent of basic salary')).fill('35');
      await page.getByRole('button', { name: say(locale, 'Done editing'), exact: true }).click();
      // The date box already holds a day (today), so a publish is never sent blank.
      await expect(page.getByLabel(say(locale, 'Effective from'))).toHaveValue(groupMatrix.today);
      await shot(page, `publish-default-date-${locale}`);
      await page.getByRole('button', { name: say(locale, 'Publish {count} change(s)', { count: 1 }) }).click();
      await expect(page.getByText(say(locale, 'Unable to publish benefits by grade.'))).toBeVisible();
      await expect(page.getByText(/field is required|could not be converted/)).toHaveCount(0);
      const real = publishes.filter(x => !x.dryRun).at(-1)!;
      expect(real.body.effectiveFrom).toBe(groupMatrix.today);
      await shot(page, `publish-refused-human-${locale}`);
      // Clearing the date is caught before anything is sent.
      await page.getByLabel(say(locale, 'Effective from')).fill('');
      const before = publishes.length;
      await page.getByRole('button', { name: say(locale, 'Publish {count} change(s)', { count: 1 }) }).click();
      await expect(page.getByText(say(locale, 'Choose the date these changes start from.'))).toBeVisible();
      expect(publishes.length).toBe(before);
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

      // The old plan dialog's look: a dialog titled "Add a benefit" with a purpose line, opened from the toolbar, focus returns to it.
      await addBenefitButton(page, locale).click();
      await expect(addDialog(page, locale)).toBeVisible();
      await expect(addDialog(page, locale).getByText(say(locale, 'Add a benefit that every grade can receive. You set its value for each grade next.'))).toBeVisible();
      await page.getByLabel(say(locale, 'Benefit name (English)')).fill('Parking');
      await page.getByLabel(say(locale, 'Benefit name (Arabic)')).fill('موقف');
      await submitAdd(page, locale).click();

      // The result says what changed, where, and offers the one next step; the new row is marked New.
      const banner = page.locator('[data-result]');
      await expect(banner).toContainText(say(locale, '{benefit} was added.', { benefit: locale === 'ar' ? 'موقف' : 'Parking' }));
      await expect(banner).toContainText(say(locale, 'Benefits by grade, group default. Not live until you publish values.'));
      await expect(banner.getByRole('button', { name: say(locale, 'Set values for {benefit}', { benefit: locale === 'ar' ? 'موقف' : 'Parking' }), exact: true })).toBeVisible();
      expect(calls[0]).toMatchObject({ path: '/api/entitlements/components', body: { nameEn: 'Parking', nameAr: 'موقف' } });
      await expect(page.getByRole('row').filter({ hasText: benefit }).getByTestId('row-chip')).toHaveText(say(locale, 'New'));
      // Every grade is a gap until it has a value, so the new benefit can't be silently missing from a package.
      await expect(page.getByRole('button', { name: new RegExp(`^${say(locale, '{benefit} for {grade}: {value}', { benefit, grade: 'G1', value: say(locale, 'Not set') })}`) })).toBeVisible();
      await expect(page.getByTestId('matrix-status')).toContainText(say(locale, '{set} of {total} values set',
        { set: groupMatrix.grades.length * groupMatrix.components.length - groupMatrix.gaps.length, total: groupMatrix.grades.length * withParking.components.length }));

      // The banner's next step opens the first grade's value for the new benefit.
      await banner.getByRole('button', { name: say(locale, 'Set values for {benefit}', { benefit }), exact: true }).click();
      await expect(page.getByRole('dialog').getByRole('heading', { level: 2 })).toContainText(benefit);
      await page.getByRole('button', { name: say(locale, 'Done editing'), exact: true }).click();

      // Its cell offers what a contract benefit allows, and a count reads "number of items", not tickets.
      // The added benefit is not in the published fixture, so its cell is found by the name just created.
      await cellNamed(page, locale, benefit, 'G1').click();
      await page.getByRole('radio', { name: say(locale, 'Offered'), exact: true }).check(); // the value type is chosen once the grade gets it
      await expect(page.getByRole('option', { name: say(locale, 'Number of items') })).toHaveCount(1);
      await expect(page.getByRole('option', { name: say(locale, 'Number of tickets') })).toHaveCount(0);
      await page.getByRole('button', { name: say(locale, 'Done editing'), exact: true }).click();
      await shot(page, `add-benefit-${locale}`);

      // Unused, so it can be removed again (from the row's actions menu, with the existing confirmation).
      await rowMenu(page, locale, benefit).click();
      await page.getByRole('menuitem', { name: say(locale, 'Remove benefit'), exact: true }).click();
      await page.getByRole('button', { name: say(locale, 'Yes, remove it'), exact: true }).click();
      await expect(page.getByText(say(locale, '{benefit} was removed.', { benefit }))).toBeVisible();
      expect(calls[1]).toMatchObject({ path: '/api/entitlements/components/BEN_PARKING/remove' });
      await expect(page.getByRole('row').filter({ hasText: benefit })).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('add a monthly cash allowance: asks for the GL account and end of service reason, says each refusal at its field, then shows it in the Wage group', async ({ page }) => {
      const { errors } = await boot(page, locale);
      const phone = {
        code: 'ALW_PHONE', nameEn: 'Phone allowance', nameAr: 'بدل هاتف', class: 'QiwaWage', floor: 'None', group: 'Wage',
        allowedValueTypes: ['Amount', 'PercentOfBasic'], allowedCoverageTiers: [], allowedDependantScopes: ['None'],
        allowedLimitPeriods: ['Monthly'], defaultLimitPeriod: 'Monthly', isFloor: false, canBeSkipped: false, isLoanFacility: false,
        floorReason: null, isCustom: true,
      };
      const withPhone = {
        ...groupMatrix,
        components: [...groupMatrix.components, phone],
        // A cash allowance is wage: the server never lets a company skip it.
        offerings: [...groupMatrix.offerings, { componentCode: 'ALW_PHONE', mode: 'Group', offered: true, skippedFrom: null, changesOn: null, canBeSkipped: false }],
        gaps: [...groupMatrix.gaps, ...groupMatrix.grades.map((g: { id: string }) => ({ gradeId: g.id, componentCode: 'ALW_PHONE', scheduledFrom: null }))],
      };
      let exists = false; let attempts = 0;
      const calls: { method: string; path: string; body: any }[] = [];
      await page.route('**/api/entitlements/**', route => {
        const request = route.request(); const p = new URL(request.url()).pathname;
        if (p === '/api/entitlements/components' && request.method() === 'POST') {
          calls.push({ method: 'POST', path: p, body: request.postDataJSON() });
          // The first send is refused the way the server refuses an unmapped GL driver; the second goes through.
          if (++attempts === 1) return route.fulfill({ status: 400, json: { error: 'gl_driver_invalid', message: "GL driver 'EARN:PHONE' resolves to no account in company Logistics (Unmapped)." } });
          exists = true;
          return route.fulfill({ json: phone });
        }
        if (p === '/api/entitlements/components/ALW_PHONE/remove' && request.method() === 'POST') {
          calls.push({ method: 'POST', path: p, body: null });
          exists = false;
          return route.fulfill({ json: { removed: 'ALW_PHONE' } });
        }
        if (p === '/api/entitlements/matrix' && request.method() === 'GET') return route.fulfill({ json: exists ? withPhone : groupMatrix });
        // The HR-safe list: the server's verdict on each earning account, never the chart itself.
        if (p === '/api/entitlements/components/cash-gl-drivers' && request.method() === 'GET') return route.fulfill({ json: [
          { key: 'EARN:PHONE', label: 'Phone allowances', labelAr: null, usable: true, reasonCode: null, reason: null },
          { key: 'EARN:BASIC', label: 'Basic salary', labelAr: null, usable: false, reasonCode: 'reserved', reason: 'Reserved for payroll lines.' }] });
        return route.fallback();
      });
      const benefit = (locale === 'ar' ? phone.nameAr : phone.nameEn) as string;

      await addBenefitButton(page, locale).click();
      await page.getByLabel(say(locale, 'Benefit name (English)')).fill('Phone allowance');
      await page.getByLabel(say(locale, 'Benefit name (Arabic)')).fill('بدل هاتف');

      // The default is a contract benefit: no pay questions are asked.
      await expect(page.getByRole('radio', { name: say(locale, 'No, it’s a contract benefit (recorded on the employee’s package, not paid in salary)') })).toBeChecked();
      await expect(page.getByTestId('cash-allowance-fields')).toHaveCount(0);

      await page.getByRole('radio', { name: say(locale, 'Yes, a monthly cash allowance paid in the salary') }).check();
      const glSelect = page.getByLabel(say(locale, 'GL account for this allowance (required)'));
      await expect(glSelect).toBeVisible();
      // Only earning accounts are offered; GOSI and WPS are stated, not editable.
      await expect(glSelect.getByRole('option')).toHaveCount(3); // Choose an account + the server's two earning accounts (one is listed but cannot be chosen)
      await expect(page.getByText(say(locale, 'No — GOSI is calculated on basic + housing only'))).toBeVisible();
      await expect(page.getByText(say(locale, 'Yes — paid in the salary transfer'))).toBeVisible();
      await expect(page.getByText(say(locale, 'Each employee’s amount is set on their salary and approved by Finance before it’s paid.'))).toBeVisible();

      // Nothing is sent without the GL account, and the message sits at the field.
      await submitAdd(page, locale).click();
      await expect(page.getByText(say(locale, 'Choose the GL account this allowance posts to.'))).toBeVisible();
      await expect(glSelect).toBeFocused();
      expect(calls).toHaveLength(0);

      // Leaving it out of end of service needs a category; a note is required only for Other.
      // An account the server refuses is listed with its reason but cannot be chosen.
      await expect(glSelect.locator('option[value="EARN:BASIC"]')).toBeDisabled();
      await expect(glSelect.locator('option[value="EARN:BASIC"]')).toContainText('Basic salary');
      await glSelect.selectOption('EARN:PHONE');
      await page.getByRole('radio', { name: say(locale, 'No, leave it out') }).check();
      await submitAdd(page, locale).click();
      await expect(page.getByText(say(locale, 'Choose why this allowance is left out of end of service pay.'))).toBeVisible();
      expect(calls).toHaveLength(0);
      const category = page.getByLabel(say(locale, 'Why is it left out? (required)'));
      await expect(category.getByRole('option')).toHaveCount(5); // Choose a reason + Reimbursement, Discretionary, Non-recurring, Other
      await category.selectOption('Other');
      await submitAdd(page, locale).click();
      await expect(page.getByText(say(locale, 'Say why this allowance is left out of end of service pay.'))).toBeVisible();
      expect(calls).toHaveLength(0);
      await category.selectOption('Reimbursement');
      await page.getByLabel(say(locale, 'Note (optional)')).fill('A reimbursement, not wage');

      // The server's refusal of the GL account is shown at the GL field, and the form stays open with what was typed.
      await submitAdd(page, locale).click();
      await expect(page.locator('#add-benefit-gl-error')).toContainText('EARN:PHONE');
      await expect(page.getByLabel(say(locale, 'Benefit name (English)'))).toHaveValue('Phone allowance');
      expect(calls[0]).toMatchObject({ path: '/api/entitlements/components', body: {
        nameEn: 'Phone allowance', nameAr: 'بدل هاتف', kind: 'Cash', glDriverKey: 'EARN:PHONE', eosbIncluded: false, eosbExclusionCategory: 'Reimbursement', eosbExclusionReason: 'A reimbursement, not wage' } });

      await submitAdd(page, locale).click();
      await expect(page.locator('[data-result]')).toContainText(say(locale, '{benefit} was added.', { benefit }));
      expect(calls).toHaveLength(2);

      // It is a Wage row, added by the customer and paid in salary (a banknote icon with that name), with no company skip control.
      const phoneRow = page.getByRole('row').filter({ hasText: benefit });
      await expect(phoneRow.getByTestId('row-chip')).toHaveText(say(locale, 'New'));
      await expect(phoneRow.getByRole('img', { name: say(locale, 'Paid in salary'), exact: true })).toBeVisible();
      await rowMenu(page, locale, benefit).click();
      await expect(page.getByRole('menuitem', { name: say(locale, 'Skip for this company') })).toHaveCount(0);
      await page.keyboard.press('Escape');
      await cellNamed(page, locale, benefit, 'G1').click();
      await page.getByRole('radio', { name: say(locale, 'Offered'), exact: true }).check();
      // The server's rule decides what is on offer: an amount or a percentage of basic, monthly, nothing else.
      const editor = page.getByRole('dialog');
      await expect(editor.getByRole('option', { name: say(locale, 'Fixed amount') })).toHaveCount(1);
      await expect(editor.getByRole('option', { name: say(locale, '% of basic salary') })).toHaveCount(1);
      await expect(editor.getByLabel(say(locale, 'How it is given')).getByRole('option')).toHaveCount(2); // the Conditions selects below have options of their own
      await expect(editor.getByLabel(say(locale, 'Resets'))).toHaveCount(0);
      await page.getByLabel(say(locale, 'Amount ({currency})', { currency: groupMatrix.currency })).fill('350');
      await expect(page.getByRole('button', { name: say(locale, 'Save & next') })).toBeEnabled();
      await page.getByRole('button', { name: say(locale, 'Done editing'), exact: true }).click();
      await shot(page, `add-cash-allowance-${locale}`);

      // Remove works as for a contract benefit.
      await rowMenu(page, locale, benefit).click();
      await page.getByRole('menuitem', { name: say(locale, 'Remove benefit'), exact: true }).click();
      await page.getByRole('button', { name: say(locale, 'Yes, remove it'), exact: true }).click();
      await expect(page.getByText(say(locale, '{benefit} was removed.', { benefit }))).toBeVisible();
      expect(calls[2]).toMatchObject({ path: '/api/entitlements/components/ALW_PHONE/remove' });
      expect(errors).toEqual([]);
    });

    test('add a cash allowance with no GL account set up: a calm message and a link, no empty list', async ({ page }) => {
      const { errors } = await boot(page, locale);
      await page.route('**/api/entitlements/components/cash-gl-drivers', route => route.fulfill({ json: [] }));
      await addBenefitButton(page, locale).click();
      await page.getByRole('radio', { name: say(locale, 'Yes, a monthly cash allowance paid in the salary') }).check();
      await expect(page.getByText(say(locale, 'No GL account is set up for earnings yet. Set one up in Payroll accounting, then come back to add this allowance.'))).toBeVisible();
      await expect(page.getByRole('link', { name: say(locale, 'Open Payroll accounting') })).toHaveAttribute('href', '/setup?tab=glMapping');
      await expect(page.getByLabel(say(locale, 'GL account for this allowance (required)'))).toHaveCount(0);
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

    test('layout at 1366x768: one purpose line, the grid starts in the top half, and the toolbar is one row', async ({ page }) => {
      await page.setViewportSize({ width: 1366, height: 768 });
      const { errors } = await boot(page, locale);
      // One purpose line (the screen's own), not the old paragraph, the scope sentence, the gap banner or the "outlined in amber" wording.
      await expect(page.locator('[data-purpose]')).toHaveCount(1);
      await expect(page.locator('[data-purpose]')).toHaveText(say(locale, 'Set what each grade receives, per company. Changes start on the date you choose when you publish.'));
      await expect(page.getByText(say(locale, 'Set every benefit by grade in one place: housing, transport, tickets, medical cover, education and per diem.'))).toHaveCount(0);
      await expect(page.getByText(/amber|العنبري|كهرمان/)).toHaveCount(0);
      await expect(page.getByText(say(locale, 'Every grade has a value.'))).toHaveCount(0);
      // Applies to, the filter, Add benefit and More share one row, and the step strip sits above it.
      const toolbar = page.getByTestId('matrix-toolbar');
      const strip = page.getByRole('navigation', { name: say(locale, 'Steps to set benefits by grade') });
      const [t, a, add, more, st] = await Promise.all([toolbar, toolbar.getByRole('combobox', { name: say(locale, 'Applies to') }), toolbar.getByRole('button', { name: say(locale, 'Add benefit'), exact: true }),
        toolbar.getByRole('button', { name: say(locale, 'More'), exact: true }), strip].map(l => l.boundingBox()));
      expect(Math.abs(a!.y - add!.y), 'Applies to and Add benefit are on one row').toBeLessThan(40);
      expect(Math.abs(add!.y - more!.y)).toBeLessThan(20);
      expect(st!.y + st!.height, 'the step tracker is above the toolbar').toBeLessThanOrEqual(t!.y + 1);
      // The grid starts in the top half of a 768 px screen and shows at least five benefit rows without scrolling.
      const table = await page.getByRole('table').boundingBox();
      expect(table!.y, 'grid top').toBeLessThan(768 * 0.5);
      const rows = page.locator('tbody tr').filter({ has: page.locator('th[scope="row"]') });
      let visible = 0;
      for (let i = 0; i < await rows.count(); i++) { const b = await rows.nth(i).boundingBox(); if (b && b.y + b.height <= 768) visible++; }
      expect(visible, 'benefit rows visible without scrolling').toBeGreaterThanOrEqual(5);
      expect(errors).toEqual([]);
    });

    test('every value cell shows it can be edited: a pencil on a value, "+ Set value" on an empty one, no per-cell provenance text', async ({ page }) => {
      const { errors } = await boot(page, locale);
      const cells = page.locator('button[data-cell]');
      const count = await cells.count();
      expect(count).toBe(groupMatrix.grades.length * groupMatrix.components.length);
      for (let i = 0; i < count; i++) {
        const cell = cells.nth(i);
        const cue = await cell.locator('[data-testid="cell-pencil"], [data-testid="cell-set-value"]').count();
        expect(cue, `cell ${i} has a pencil or "+ Set value"`).toBe(1);
      }
      await expect(page.getByTestId('cell-set-value')).toHaveCount(groupMatrix.gaps.length);
      await expect(page.getByTestId('cell-set-value').first()).toHaveText(say(locale, 'Set value'));
      // The company view: the mode is on the row, never repeated under 30 cells.
      await page.getByRole('combobox', { name: say(locale, 'Applies to') }).selectOption(companies[1].id);
      await expect(page.getByRole('row').filter({ hasText: benefitName(locale, 'PER_DIEM') }).getByTestId('row-chip')).toHaveText(say(locale, 'Tailored'));
      await expect(page.getByText(say(locale, 'This company'), { exact: true })).toHaveCount(0);
      await expect(page.getByText(say(locale, 'Group default'), { exact: true })).toHaveCount(0);
      // The override is marked by a shape with a name, only in a tailored row.
      await expect(page.getByTestId('cell-override')).toHaveCount(1);
      await expect(page.getByTestId('cell-override').first()).toHaveText(say(locale, 'Set for this company'));
      expect(errors).toEqual([]);
    });

    test('a row has at most one chip, and its actions are in a menu with targets of at least 24 px', async ({ page }) => {
      const { errors } = await boot(page, locale, { matrix: undefined });
      const rows = page.locator('tbody tr').filter({ has: page.locator('th[scope="row"]') });
      for (let i = 0; i < await rows.count(); i++) expect(await rows.nth(i).getByTestId('row-chip').count()).toBeLessThanOrEqual(1);
      const trigger = rowMenu(page, locale, benefitName(locale, 'PER_DIEM'));
      const box = await trigger.boundingBox();
      expect(box!.width).toBeGreaterThanOrEqual(24);
      expect(box!.height).toBeGreaterThanOrEqual(24);
      await trigger.click();
      await expect(page.getByRole('menu')).toBeVisible();
      await expect(page.getByRole('menuitem', { name: say(locale, 'Mark the rest Not offered') })).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByRole('menu')).toHaveCount(0);
      await expect(trigger).toBeFocused();
      expect(errors).toEqual([]);
    });

    test('the "Needs a value" filter keeps only benefits with an empty grade, and says it is on', async ({ page }) => {
      const { errors } = await boot(page, locale);
      const chip = page.getByRole('button', { name: say(locale, 'Needs a value ({count})', { count: new Set(groupMatrix.gaps.map((g: any) => g.componentCode)).size }), exact: true });
      await expect(chip).toHaveAttribute('aria-pressed', 'false');
      const rows = page.locator('tbody tr').filter({ has: page.locator('th[scope="row"]') });
      const before = await rows.count();
      await chip.click();
      await expect(chip).toHaveAttribute('aria-pressed', 'true');
      expect(await rows.count()).toBeLessThan(before);
      await expect(rows).toHaveCount(new Set(groupMatrix.gaps.map((g: any) => g.componentCode)).size);
      await chip.click();
      await expect(rows).toHaveCount(before);
      expect(errors).toEqual([]);
    });

    test('a grid wider than the screen has a labelled scroller, says which grades are in view, and scrolls by a grade', async ({ page }) => {
      await page.setViewportSize({ width: 1366, height: 768 });
      const wide = wideMatrix();
      const { errors } = await boot(page, locale, { matrix: wide });
      const scroller = page.getByRole('region', { name: say(locale, 'Benefits by grade, scrolls sideways') });
      await expect(scroller).toBeVisible();
      await expect(scroller).toHaveAttribute('tabindex', '0');
      const status = page.getByTestId('grades-status');
      const re = (from: string) => new RegExp(say(locale, 'Grades {from}-{to} of {total}', { from: '@F', to: '@T', total: '10' }).replace('@F', from).replace('@T', '\\d+'));
      await expect(status).toHaveText(re('1'));
      const first = Number((await status.innerText()).match(/\d+/g)![1]);
      expect(first, 'at least five grades are in view with both rails open').toBeGreaterThanOrEqual(5);
      const next = page.getByRole('button', { name: say(locale, 'Show later grades'), exact: true });
      const prev = page.getByRole('button', { name: say(locale, 'Show earlier grades'), exact: true });
      await expect(prev).toBeDisabled();
      await next.click();
      await expect(status).not.toHaveText(re('1'));
      await expect(prev).toBeEnabled();
      // The fade at the end of the scroller says there is more; it is gone once the last grade is reached.
      await expect(page.getByTestId('scroll-fade-end')).toBeVisible();
      for (let i = 0; i < 10; i++) { if (await next.isEnabled()) await next.click({ timeout: 1_500 }).catch(() => undefined); } // it may turn off mid-click, on reaching the end
      await expect(next).toBeDisabled();
      await expect(page.getByTestId('scroll-fade-end')).toBeHidden();
      // The monthly total row exists because two wage rows carry fixed amounts, and says what it adds.
      const total = page.getByTestId('monthly-total-row');
      await expect(total).toContainText(say(locale, 'Fixed monthly allowances (amounts only)'));
      await shot(page, `wide-grid-${locale}`);
      expect(errors).toEqual([]);
    });

    test('the monthly total is left out when it would add up a single row', async ({ page }) => {
      const { errors } = await boot(page, locale);
      await expect(page.getByTestId('monthly-total-row')).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('the cell editor is a drawer on the end edge that is fully on screen, with the grid still behind it', async ({ page }) => {
      await page.setViewportSize({ width: 1366, height: 768 });
      const { errors } = await boot(page, locale);
      await cellButton(page, locale, 'PER_DIEM', 'G5').click();
      const drawer = page.getByRole('dialog');
      await expect(drawer).toBeVisible();
      const box = (await settledBox(drawer))!;
      expect(box.y).toBeGreaterThanOrEqual(0);
      expect(box.y + box.height).toBeLessThanOrEqual(768 + 1);
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(1366 + 1);
      if (locale === 'ar') expect(box.x, 'docks at the left (inline-end in Arabic)').toBeLessThan(2);
      else expect(box.x + box.width, 'docks at the right').toBeGreaterThan(1366 - 2);
      expect(box.width).toBeLessThan(700);
      await expect(drawer.getByRole('heading', { level: 2 })).toContainText(benefitName(locale, 'PER_DIEM'));
      await shot(page, `cell-drawer-${locale}`);
      // Esc closes it and focus returns to the cell that opened it.
      await page.keyboard.press('Escape');
      await expect(drawer).toHaveCount(0);
      await expect(cellButton(page, locale, 'PER_DIEM', 'G5')).toBeFocused();
      expect(errors).toEqual([]);
    });

    test('a viewer who cannot change values sees no pencils, no + Set value, no Add benefit, and the cell still opens read-only', async ({ page }) => {
      const { errors } = await boot(page, locale, { permissions: ['entitlements.read'] });
      await expect(page.getByTestId('cell-pencil')).toHaveCount(0);
      await expect(page.getByTestId('cell-set-value')).toHaveCount(0);
      await expect(page.getByRole('button', { name: say(locale, 'Add benefit'), exact: true })).toHaveCount(0);
      await cellButton(page, locale, 'HOUSING', 'G3').click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await expect(page.getByRole('dialog').getByRole('radio', { name: say(locale, 'Offered'), exact: true })).toBeDisabled();
      expect(errors).toEqual([]);
    });

    test('a tenant with benefit plans from before finds them from the toolbar menu, read-only; one with none is not offered the link', async ({ page }) => {
      const hr = { permissions: ['entitlements.read', 'entitlements.manage', 'employees.write'], roles: ['HR Manager'] };
      const { errors } = await boot(page, locale, { ...hr, plans: [{ id: 'p1', code: 'MED', name: 'Medical', planType: 'Medical', classification: 'Discretionary', currency: 'SAR', effectiveFrom: '2026-01-01', isActive: true }] });
      await page.getByRole('button', { name: say(locale, 'More'), exact: true }).click();
      await expect(page.getByRole('menuitem', { name: say(locale, 'Open in Benefit plans') })).toHaveAttribute('href', '/setup?tab=benefitPlans');
      expect(errors).toEqual([]);
    });

    test('with no plans from before there is no Benefit plans link', async ({ page }) => {
      const { errors } = await boot(page, locale, { permissions: ['entitlements.read', 'entitlements.manage', 'employees.write'], roles: ['HR Manager'], plans: [] });
      await page.getByRole('button', { name: say(locale, 'More'), exact: true }).click();
      await expect(page.getByRole('menuitem', { name: say(locale, 'Export to Excel (CSV)') })).toBeVisible();
      await expect(page.getByRole('menuitem', { name: say(locale, 'Open in Benefit plans') })).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test.describe('phone', () => {
      test.use({ viewport: { width: 390, height: 844 } });
      test('one grade at a time as cards: no sideways page scroll, a grade picker, and the drawer fits the screen', async ({ page }) => {
        const { errors } = await boot(page, locale);
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
        expect(overflow, 'the page does not scroll sideways').toBeLessThanOrEqual(0);
        const cards = page.getByTestId('grade-cards');
        await expect(cards).toBeVisible();
        await expect(page.getByRole('table')).toHaveCount(0);
        const picker = cards.getByRole('combobox', { name: say(locale, 'Grade') });
        await picker.selectOption(gradeId('G3'));
        await expect(cellButton(page, locale, 'HOUSING', 'G3')).toBeVisible();
        await expect(cellButton(page, locale, 'HOUSING', 'G1')).toHaveCount(0);
        // The toolbar's main control is not below a long preamble.
        const add = await page.getByRole('button', { name: say(locale, 'Add benefit'), exact: true }).boundingBox();
        expect(add!.y).toBeLessThan(844);
        await cellButton(page, locale, 'HOUSING', 'G3').click();
        const box = (await settledBox(page.getByRole('dialog')))!;
        expect(box.x).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width).toBeLessThanOrEqual(390 + 1);
        expect(box.y + box.height).toBeLessThanOrEqual(844 + 1);
        await shot(page, `phone-drawer-${locale}`);
        await page.keyboard.press('Escape');
        await shot(page, `phone-cards-${locale}`);
        expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);
        expect(errors).toEqual([]);
      });
    });
  });
}
