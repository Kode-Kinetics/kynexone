import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';

/**
 * Loan limits in the Benefits by grade grid, in a real browser against route mocks. A loan facility row (Car Loan) is edited
 * in the same drawer as any benefit, but it saves through its own Save into the existing grade-loan-limits API
 * (GET/PUT /api/finance/loans/grade-limits), never through the grid's Publish bar. Driven in English and Arabic, on a desktop
 * and on a phone; the mocks keep the rows the way the API does (a publish starts a new cell and the matrix then shows it).
 *
 * Run: npx playwright test -c e2e/playwright.matrix.config.ts e2e/benefits-by-grade-loan-limits.spec.ts
 * LOAN_GRID_SCREENSHOT_DIR keeps the screenshots outside the test output folder.
 */

const fixtures = path.join(__dirname, 'fixtures', 'benefits-by-grade');
const load = (name: string) => JSON.parse(fs.readFileSync(path.join(fixtures, `${name}.json`), 'utf8'));
const groupMatrix = load('matrix-group');
const logisticsMatrix = load('matrix-logistics');
const companies: { id: string; name: string }[] = load('companies');
const logistics = companies.find(c => c.id === logisticsMatrix.companyId)!;

type Locale = 'en' | 'ar';
const say = (locale: Locale, key: string, values: Record<string, string | number> = {}) =>
  Object.entries(values).reduce((text, [k, v]) => text.replaceAll(`{${k}}`, String(v)), (LOCALE_DICTS[locale] as Record<string, string>)[key] ?? key);

const LOAN_TYPE = {
  id: 'loan-type-car', code: 'CAR', nameEn: 'Car Loan', nameAr: 'قرض سيارة', maxAmount: 80000, maxInstallments: 48, repaymentFrequency: 'Monthly',
  isInterestFree: true, interestRate: 0, minServiceMonths: 0, requiresApproval: true, isActive: true, gradeLimited: true, entitlementComponentCode: 'LOAN_CAR',
};
const loanComponent = {
  code: 'LOAN_CAR', nameEn: LOAN_TYPE.nameEn, nameAr: LOAN_TYPE.nameAr, class: 'Facility', floor: 'None', group: 'Facility',
  allowedValueTypes: ['Amount', 'MultipleOfBasic', 'MultipleOfGross', 'EligibilityOnly'], allowedCoverageTiers: [], allowedDependantScopes: ['None'],
  allowedLimitPeriods: [], defaultLimitPeriod: null, isFloor: false, canBeSkipped: false, isLoanFacility: true, floorReason: null,
};
const grade = (code: string) => groupMatrix.grades.find((g: { code: string }) => g.code === code);
const nameOf = (locale: Locale) => (locale === 'ar' ? LOAN_TYPE.nameAr : LOAN_TYPE.nameEn);

type LoanRow = {
  gradeId: string; gradeCode: string; gradeName: string; gradeNameAr: string | null; level: number; cellId: string | null; eligible: boolean;
  valueType: string | null; amount: number | null; rate: number | null; maxOutstandingAmount: number | null; effectiveFrom: string | null; isCompanyOverride: boolean;
};
/** G1 a fixed amount with an outstanding cap, G2 a salary multiple, G3 not eligible, G4 eligible with no figure, G5 nothing set yet. */
function startingRows(): LoanRow[] {
  const base = (code: string) => { const g = grade(code); return { gradeId: g.id, gradeCode: g.code, gradeName: g.name, gradeNameAr: g.nameAr, level: g.level, isCompanyOverride: false }; };
  return [
    { ...base('G1'), cellId: 'lc-1', eligible: true, valueType: 'Amount', amount: 10000, rate: null, maxOutstandingAmount: 20000, effectiveFrom: '2026-01-01' },
    { ...base('G2'), cellId: 'lc-2', eligible: true, valueType: 'MultipleOfBasic', amount: null, rate: 3, maxOutstandingAmount: null, effectiveFrom: '2026-01-01' },
    { ...base('G3'), cellId: 'lc-3', eligible: false, valueType: 'EligibilityOnly', amount: null, rate: null, maxOutstandingAmount: null, effectiveFrom: '2026-01-01' },
    { ...base('G4'), cellId: 'lc-4', eligible: true, valueType: 'EligibilityOnly', amount: null, rate: null, maxOutstandingAmount: null, effectiveFrom: '2026-01-01' },
    { ...base('G5'), cellId: null, eligible: false, valueType: null, amount: null, rate: null, maxOutstandingAmount: null, effectiveFrom: null },
  ];
}
const matrixCell = (r: LoanRow) => ({
  gradeId: r.gradeId, componentCode: 'LOAN_CAR', cellId: r.cellId, isCompanyOverride: r.isCompanyOverride, inherited: false, eligible: r.eligible, valueType: r.valueType,
  amount: r.amount, rate: r.rate, maxOutstandingAmount: r.maxOutstandingAmount, coverageTier: null, quantity: null, dependantScope: 'None', maxDependants: null,
  limitPeriod: null, minServiceMonths: null, afterProbation: false, nationalityScope: 'Any', nationalityBasis: null, sourceRule: 'LoanGradeLimitsGrid', note: null,
  effectiveFrom: r.effectiveFrom, effectiveTo: null, nextChangeOn: null,
});
const withLoan = (base: any, rows: LoanRow[]) => ({
  ...base,
  components: [...base.components, loanComponent],
  cells: [...base.cells, ...rows.filter(r => r.cellId).map(matrixCell)],
  offerings: [...base.offerings, { componentCode: 'LOAN_CAR', mode: 'Group', offered: true, skippedFrom: null, changesOn: null, canBeSkipped: false }],
});

const HR_PERMISSIONS = ['entitlements.read', 'entitlements.manage', 'employees.approve', 'loans.read', 'loans.policy_manage'];

async function boot(page: Page, locale: Locale, options: {
  roles?: string[]; permissions?: string[]; isGroupScope?: boolean; myCompanies?: { id: string; name: string }[]; companyView?: boolean;
  /** Someone else publishes a new G2 limit between the editor opening and the Save. */
  changeG2BeforeSave?: boolean; gradeLimited?: boolean; loanTypesFail?: boolean;
} = {}) {
  const errors: string[] = [];
  const state = {
    rows: startingRows(), armed: false, loanPublishes: [] as any[], loanLists: [] as URLSearchParams[], matrixLoads: 0, matrixPublishes: 0, loanTypeLoads: 0, listCalls: 0,
  };
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(([lang]) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', lang);
  }, [locale]);
  await page.route('**/api/**', route => {
    const request = route.request(); const url = new URL(request.url()); const p = url.pathname;
    if (p === '/api/auth/me') return route.fulfill({ json: {
      id: 'user-1', tenantId: 'tenant-1', tenantSlug: 'masar', fullName: 'Hala Al-Qahtani', roles: options.roles ?? ['HR Director'], isGroupScope: options.isGroupScope ?? true,
      permissions: options.permissions ?? HR_PERMISSIONS,
      companies: (options.myCompanies ?? companies).map((c, i) => ({ ...c, code: `M${i + 1}`, countryCode: 'SA', isActive: true })),
    } });
    if (p === '/api/entitlements/matrix' && request.method() === 'GET') {
      state.matrixLoads++;
      const forCompany = !!url.searchParams.get('companyId');
      return route.fulfill({ json: withLoan(forCompany ? logisticsMatrix : groupMatrix, state.rows) });
    }
    if (p === '/api/entitlements/matrix' && request.method() === 'PUT') { state.matrixPublishes++; return route.fulfill({ status: 500, json: { error: 'unexpected' } }); }
    if (p === '/api/finance/loans/types') {
      state.loanTypeLoads++;
      if (options.loanTypesFail) return route.fulfill({ status: 500, json: { message: 'down' } });
      return route.fulfill({ json: [{ ...LOAN_TYPE, gradeLimited: options.gradeLimited ?? true }] });
    }
    if (p === '/api/finance/loans/grade-limits' && request.method() === 'GET') {
      state.loanLists.push(url.searchParams);
      state.listCalls++;
      // Armed by the test once the editor is filled in: the next read is the one made just before the write, and somebody else
      // has published G2 by then.
      if (options.changeG2BeforeSave && state.armed) {
        state.armed = false;
        state.rows = state.rows.map(r => (r.gradeCode === 'G2' ? { ...r, cellId: 'lc-2b', rate: 4 } : r));
      }
      return route.fulfill({ json: state.rows });
    }
    if (p === '/api/finance/loans/grade-limits' && request.method() === 'PUT') {
      const body = request.postDataJSON();
      state.loanPublishes.push(body);
      state.rows = state.rows.map(r => {
        const sent = body.rows.find((x: any) => x.gradeId === r.gradeId);
        return sent ? { ...r, cellId: `new-${r.gradeCode}`, eligible: sent.eligible, valueType: sent.valueType, amount: sent.amount, rate: sent.rate, maxOutstandingAmount: sent.maxOutstandingAmount,
          effectiveFrom: body.effectiveFrom, isCompanyOverride: !!body.companyId } : r;
      });
      return route.fulfill({ json: { changed: body.rows.length, unchanged: 0, rows: state.rows } });
    }
    if (p === '/api/compensation/benefits/plans') return route.fulfill({ json: [] });
    if (p === '/api/tenant-admin/localization') return route.fulfill({ json: { countryCode: 'SA', currencyCode: 'SAR', defaultLanguage: locale } });
    if (p.includes('/features/') || p === '/api/notifications') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto('/benefits/by-grade');
  await expect(page.getByRole('heading', { name: say(locale, 'Benefits by grade'), level: 1 })).toBeVisible();
  if (options.companyView) await page.getByRole('combobox', { name: say(locale, 'Applies to') }).selectOption(logistics.id);
  return { errors, state };
}

const loanCell = (page: Page, locale: Locale, gradeCode: string) =>
  page.getByRole('button', { name: new RegExp(`^${say(locale, '{benefit} for {grade}: {value}', { benefit: nameOf(locale), grade: gradeCode, value: '' })}`) });
const drawer = (page: Page) => page.getByRole('dialog');
const field = (page: Page, locale: Locale, label: string) => drawer(page).getByLabel(say(locale, label), { exact: true });
const saveButton = (page: Page, locale: Locale) => drawer(page).getByRole('button', { name: say(locale, 'Save loan limit'), exact: true });

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
  const dir = process.env.LOAN_GRID_SCREENSHOT_DIR ?? test.info().outputDir;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: path.join(dir, `${name}.png`), fullPage: true });
}

for (const locale of ['en', 'ar'] as const) {
  test.describe(`loan limits in the benefits grid (${locale})`, () => {
    test('a loan limit opens the same drawer as any benefit, saves through its own Save, and the grid shows the new limit', async ({ page }) => {
      const { errors, state } = await boot(page, locale);
      await expect(page.locator('html')).toHaveAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');
      // The cell is a control now, not read-only text; the old read-only wording is gone.
      await expect(loanCell(page, locale, 'G1')).toContainText('10');
      await loanCell(page, locale, 'G1').click();
      await expect(drawer(page).getByRole('heading', { name: new RegExp(nameOf(locale)) })).toBeVisible();
      // The editor reads the limit in force from the grade-limits API, for this loan type and the group grid.
      await expect(field(page, locale, 'Per-loan maximum')).toHaveValue('10000');
      await expect(field(page, locale, 'Total outstanding maximum')).toHaveValue('20000');
      expect(state.loanLists[0].get('loanTypeId')).toBe(LOAN_TYPE.id);
      expect(state.loanLists[0].get('companyId')).toBeNull();
      await shot(page, `01-editor-open-${locale}`);

      await field(page, locale, 'Per-loan maximum').fill('15000');
      await field(page, locale, 'Effective from').fill('2026-10-20');
      await saveButton(page, locale).click();
      await expect(page.getByText(say(locale, 'Published limits for {count} grade(s), starting {date}. Loans already requested keep the limit they were assessed against.', { count: 1, date: '2026-10-20' }))).toBeVisible();

      // Exactly Limits by grade's request: this loan type, the group grid, the chosen date, and only the edited grade.
      expect(state.loanPublishes).toEqual([{
        loanTypeId: LOAN_TYPE.id, companyId: null, effectiveFrom: '2026-10-20',
        rows: [{ gradeId: grade('G1').id, eligible: true, valueType: 'Amount', amount: 15000, rate: null, maxOutstandingAmount: 20000 }],
      }]);
      // The grid refreshed from the API and shows the new figure. Nothing went through the grid's own Publish.
      await expect(loanCell(page, locale, 'G1')).toContainText('15');
      expect(state.matrixLoads).toBeGreaterThanOrEqual(2);
      expect(state.matrixPublishes).toBe(0);
      await expect(page.getByRole('button', { name: say(locale, 'Publish {count} change(s)', { count: 0 }) })).toBeDisabled();
      await shot(page, `02-after-save-${locale}`);
      expect(errors).toEqual([]);
    });

    test('a salary multiple, not eligible and "Not set" are all editable; the same checks as Limits by grade refuse bad figures before any request', async ({ page }) => {
      const { errors, state } = await boot(page, locale);
      // G5 has no limit yet: it reads as not set and can be set.
      await loanCell(page, locale, 'G5').click();
      await expect(field(page, locale, 'Eligible')).toHaveValue('unset');
      await expect(saveButton(page, locale)).toBeDisabled();
      await field(page, locale, 'Eligible').selectOption('yes');
      await field(page, locale, 'Per-loan maximum').fill('0');
      await expect(drawer(page).getByRole('alert')).toContainText(say(locale, 'Per-loan maximum must be more than zero, or left empty for no per-loan limit.'));
      await expect(saveButton(page, locale)).toBeDisabled();
      await field(page, locale, 'Per-loan maximum').fill('1.234');
      await field(page, locale, 'Limit basis').selectOption('MultipleOfGross');
      await field(page, locale, 'Per-loan maximum').fill('2.5');
      await field(page, locale, 'Total outstanding maximum').fill('1.005');
      await expect(drawer(page).getByRole('alert')).toContainText(say(locale, 'Amounts can have at most two decimal places.'));
      await field(page, locale, 'Total outstanding maximum').fill('');
      await expect(saveButton(page, locale)).toBeEnabled();
      expect(state.loanPublishes).toEqual([]);
      await saveButton(page, locale).click();
      await expect(page.getByRole('status').filter({ hasText: '2026-10-06' })).toBeVisible();
      expect(state.loanPublishes[0].rows).toEqual([{ gradeId: grade('G5').id, eligible: true, valueType: 'MultipleOfGross', amount: null, rate: 2.5, maxOutstandingAmount: null }]);
      expect(state.loanPublishes[0].effectiveFrom).toBe(groupMatrix.today);
      expect(errors).toEqual([]);
    });

    test('"Same for all grades" and "From a grade upward" publish every grade they change, in one request', async ({ page }) => {
      const { errors, state } = await boot(page, locale);
      await loanCell(page, locale, 'G2').click();
      await drawer(page).getByRole('button', { name: say(locale, 'From {grade} upward', { grade: 'G2' }) }).click();
      await saveButton(page, locale).click();
      await expect(page.getByRole('status').filter({ hasText: '2026-10-06' })).toBeVisible();
      expect(state.loanPublishes).toHaveLength(1);
      expect(state.loanPublishes[0].rows.map((r: any) => r.gradeId).sort()).toEqual(['G3', 'G4', 'G5'].map(c => grade(c).id).sort());
      expect(state.loanPublishes[0].rows.every((r: any) => r.valueType === 'MultipleOfBasic' && r.rate === 3)).toBe(true);
      expect(errors).toEqual([]);
    });

    test('if somebody else changed the limit while the editor was open, nothing is saved and the current limit is shown', async ({ page }) => {
      const { errors, state } = await boot(page, locale, { changeG2BeforeSave: true });
      await loanCell(page, locale, 'G2').click();
      await expect(field(page, locale, 'Per-loan maximum')).toHaveValue('3');
      await field(page, locale, 'Per-loan maximum').fill('5');
      state.armed = true;
      await saveButton(page, locale).click();
      await expect(drawer(page).getByRole('alert')).toContainText(say(locale, 'Someone else changed {grades} while you were editing, so nothing was saved. The current limits are shown; check them, then save again.', { grades: (locale === 'ar' && grade('G2').nameAr) || grade('G2').name }));
      expect(state.loanPublishes).toEqual([]);
      // What is on screen now is the other person's limit, and the edit is not carried over silently.
      await expect(field(page, locale, 'Per-loan maximum')).toHaveValue('4');
      await shot(page, `03-changed-by-someone-else-${locale}`);
      await field(page, locale, 'Per-loan maximum').fill('5');
      await saveButton(page, locale).click();
      await expect.poll(() => state.loanPublishes.length).toBe(1);
      expect(state.loanPublishes[0].rows[0]).toMatchObject({ gradeId: grade('G2').id, rate: 5 });
      expect(errors).toEqual([]);
    });

    test('a viewer who cannot publish loan limits sees them read-only, with no editor and no link they cannot use', async ({ page }) => {
      const { errors, state } = await boot(page, locale, { roles: ['Finance'], permissions: ['entitlements.read', 'loans.read', 'loans.write'] });
      await expect(page.getByTestId('matrix-toolbar')).toContainText(say(locale, 'View only'));
      // The limit is still shown, as text in a cell that is not a control.
      await expect(loanCell(page, locale, 'G1')).toBeDisabled();
      await expect(loanCell(page, locale, 'G1')).toContainText('10');
      await expect(page.locator('a[href*="loanPolicies"], a[href="/loans"]')).toHaveCount(0);
      expect(state.loanTypeLoads, 'a viewer who cannot edit does not ask for loan types').toBe(0);
      expect(state.loanLists).toEqual([]);
      await shot(page, `04-view-only-${locale}`);
      expect(errors).toEqual([]);
    });

    test('the loan row links to Loan policies on this loan type, not to the Loans list', async ({ page }) => {
      const { errors } = await boot(page, locale);
      const link = page.getByRole('link', { name: say(locale, 'Edit in Loans → Loan policies → Limits by grade') });
      await expect(link).toHaveAttribute('href', `/setup?tab=loanPolicies&loanType=${LOAN_TYPE.id}`);
      await page.getByRole('combobox', { name: say(locale, 'Applies to') }).selectOption(logistics.id);
      await expect(link).toHaveAttribute('href', `/setup?tab=loanPolicies&loanType=${LOAN_TYPE.id}&companyId=${logistics.id}`);
      expect(errors).toEqual([]);
    });

    test('a company administrator edits that company\'s own limit: the company grid is read and written, never the group one', async ({ page }) => {
      const { errors, state } = await boot(page, locale, { isGroupScope: false, myCompanies: [logistics] });
      await loanCell(page, locale, 'G1').click();
      await expect(field(page, locale, 'Per-loan maximum')).toHaveValue('10000');
      expect(state.loanLists[0].get('companyId')).toBe(logistics.id);
      await field(page, locale, 'Per-loan maximum').fill('12000');
      await saveButton(page, locale).click();
      await expect.poll(() => state.loanPublishes.length).toBe(1);
      expect(state.loanPublishes[0]).toMatchObject({ loanTypeId: LOAN_TYPE.id, companyId: logistics.id });
      expect(errors).toEqual([]);
    });

    test('a loan type with grade limits switched off says so in the editor, and links to where it is switched on', async ({ page }) => {
      const { errors } = await boot(page, locale, { gradeLimited: false });
      await loanCell(page, locale, 'G1').click();
      await expect(drawer(page).getByText(say(locale, 'Grade limits are switched off for this loan type, so these limits are not applied to requests yet.'))).toBeVisible();
      await expect(drawer(page).getByRole('link', { name: say(locale, 'Open Loan policies for this loan type') }))
        .toHaveAttribute('href', `/setup?tab=loanPolicies&loanType=${LOAN_TYPE.id}`);
      expect(errors).toEqual([]);
    });

    test.describe('phone', () => {
      test.use({ viewport: { width: 390, height: 844 } });
      test('a loan limit is edited on a phone card: the drawer fits and nothing scrolls sideways', async ({ page }) => {
        const { errors, state } = await boot(page, locale);
        await page.getByTestId('grade-cards').getByRole('combobox', { name: say(locale, 'Grade') }).selectOption(grade('G1').id);
        await loanCell(page, locale, 'G1').click();
        await expect(field(page, locale, 'Per-loan maximum')).toHaveValue('10000');
        const box = (await settledBox(drawer(page)))!;
        expect(box.x).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width).toBeLessThanOrEqual(390 + 1);
        expect(box.y + box.height).toBeLessThanOrEqual(844 + 1);
        expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);
        await shot(page, `05-phone-editor-${locale}`);
        await field(page, locale, 'Per-loan maximum').fill('11000');
        await saveButton(page, locale).scrollIntoViewIfNeeded();
        await saveButton(page, locale).click();
        await expect.poll(() => state.loanPublishes.length).toBe(1);
        await expect(loanCell(page, locale, 'G1')).toContainText('11');
        await shot(page, `06-phone-after-save-${locale}`);
        expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);
        expect(errors).toEqual([]);
      });
    });
  });
}
