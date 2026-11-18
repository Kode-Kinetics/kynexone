import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { translate } from '../../src/i18n/translations';
import { ALL_PERMISSIONS, boot, type ApiMocks, type Persona } from '../setup-consolidation/fixture';

/**
 * Space pass, batch 2: the Payroll shell and its sub-sections (Screen Standard 1 and 2). The main task of each section sits in the
 * first screenful at 1366x768 and 1280x720, in English and Arabic, and the page never scrolls sideways. Every API call is intercepted.
 *
 *   shell (no run chosen)   the run picker and the "runs that need attention" list share ONE slim bar: the section heading starts
 *                           above y=200 (it started at 256 under a picker card, an attention heading and an attention list)
 *   Validation              Run Validation and the issue counts share one row: the issue list starts above y=340 (it started at 404)
 *   Final Settlement        the lifecycle note is one band and the four inputs share one row: the register starts above y=580 (it started at 710; measured 563 at 1366x768)
 *   GL Journal              the intro line shares the row with the run picker: the journal's first block starts above y=430
 *   Employee Salary         "Assign Salary" is a one-line button, the filter is a normal input, and at least 8 salary rows are in view
 *
 * SPACE_SHOTS_DIR=<dir> also saves a screenshot per section, viewport and language (SPACE_PHASE=before|after names the file).
 */
const SHOTS = process.env.SPACE_SHOTS_DIR;
const VIEWPORTS = [{ width: 1366, height: 768 }, { width: 1280, height: 720 }] as const;
const LOCALES = ['en', 'ar'] as const;
type Locale = (typeof LOCALES)[number];

async function shot(page: Page, screen: string, locale: Locale) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  const vp = page.viewportSize();
  await page.screenshot({ path: `${SHOTS}/${process.env.SPACE_PHASE ?? 'run'}-${screen}-${locale}-${vp?.width}x${vp?.height}.png` });
}
const noSideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
const topOf = async (page: Page, selector: string) => page.locator(selector).first().evaluate(el => Math.round(el.getBoundingClientRect().top));

const persona: Persona = { roles: ['Admin'], permissions: [...ALL_PERMISSIONS, 'payroll.approve', 'payroll.export', 'payroll.lock', 'payroll.structure_manage'] };

const RUN = {
  id: '22222222-2222-4222-8222-222222222222', companyId: 'c1', year: 2026, month: 9, status: 'Locked', runType: 'Regular', totalGrossSalary: 144856.25, totalDeductions: 11000, totalNetSalary: 133588.9,
  totalEmployerStatutoryCost: 10206.25, employeeCount: 9, createdAtUtc: '2026-11-01T08:00:00Z', processedAtUtc: '2026-11-02T08:00:00Z', lockedAtUtc: '2026-11-03T08:00:00Z',
};
const BASE_MOCKS: ApiMocks = {
  '/api/payroll/companies': [{ id: 'c1', name: 'Workspace Fixture LLC', tradeName: '', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' }],
  '/api/payroll/runs': { items: [RUN], total: 1, page: 1, pageSize: 100 },
  '/api/payroll/payment-batches': [],
};

for (const locale of LOCALES) {
  for (const viewport of VIEWPORTS) {
    test.describe(`space batch 2, payroll (${locale} ${viewport.width}x${viewport.height})`, () => {
      test.use({ viewport });

      test('shell with no run chosen: the picker and the runs needing attention are one slim bar', async ({ page }) => {
        await boot(page, { persona, locale, url: '/payroll?tab=runs', mocks: BASE_MOCKS });
        const attention = page.getByTestId('pipeline-attention');
        await expect(attention).toBeVisible({ timeout: 60_000 });
        await expect(page.getByTestId('payroll-section-panel')).toBeVisible();
        await shot(page, 'payroll-shell', locale);
        const bar = page.getByTestId('run-pipeline');
        expect(await bar.locator('[data-testid="pipeline-attention"]').count(), 'the attention list is inside the run bar').toBe(1);
        const barHeight = await bar.evaluate(el => Math.round(el.getBoundingClientRect().height));
        expect(barHeight, 'run bar height with one run needing attention').toBeLessThanOrEqual(72);
        expect(await topOf(page, '#payroll-section-heading'), 'section heading top').toBeLessThanOrEqual(200);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('Validation: Run Validation and the counts share a row, the issue list starts in the first screenful', async ({ page }) => {
        const results = Array.from({ length: 12 }, (_, i) => ({
          id: `v${i}`, employeeId: 100 + i, severity: 'Warning', code: 'IBAN_MISSING', isResolved: false,
          message: `Employee ${i}: no IBAN is recorded, so the payment cannot be included in the bank file. Add the IBAN on the employee's payroll tab.`,
        }));
        await boot(page, {
          persona, locale, url: `/payroll?tab=validation&run=${RUN.id}`,
          mocks: { ...BASE_MOCKS, [`/api/payroll/runs/${RUN.id}/validate`]: results, [`/api/payroll/runs/${RUN.id}/validation-overrides`]: { overridableCodes: [] } },
        });
        const list = page.getByTestId('validation-results');
        await expect(list).toBeVisible({ timeout: 60_000 });
        await shot(page, 'validation', locale);
        expect(await topOf(page, '[data-testid="validation-results"]'), 'issue list top').toBeLessThanOrEqual(340);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('Final Settlement: the register starts in the first screenful', async ({ page }) => {
        const row = (n: number) => ({
          id: `s${n}`, employeeId: n, employeeCode: `E-${n}`, employeeName: `Employee ${n}`, companyId: 'c1', offboardingId: `o${n}`, lastWorkingDay: '2026-10-31', settlementDueDate: '2026-11-30',
          terminationReason: 'Resignation', status: 'Draft', currency: 'SAR', netPayable: 12000 + n, payrollRunId: null, paymentBatchId: null, paidAtUtc: null, isOverdue: false, overdueDays: 0,
        });
        await boot(page, {
          persona, locale, url: '/payroll?tab=final-settlement',
          mocks: { ...BASE_MOCKS, '/api/payroll/final-settlements': { rows: [row(1), row(2), row(3)], outstandingTotal: 0 } },
        });
        const title = page.getByText(translate(locale, 'Settlement Register'), { exact: true });
        await expect(title).toBeVisible({ timeout: 60_000 });
        await expect(page.getByText('Employee 1', { exact: true })).toBeVisible();
        await shot(page, 'final-settlement', locale);
        expect(await title.evaluate(el => Math.round(el.getBoundingClientRect().top)), 'Settlement Register title top').toBeLessThanOrEqual(580);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('GL Journal: the journal starts in the first screenful', async ({ page }) => {
        const entry = (glAccount: string, name: string, entryType: 'DR' | 'CR', amount: number) => ({ componentCode: 'BASIC', componentName: name, glAccount, glAccountName: name, entryType, amount, costCentreCode: 'CC_HR', costCentreName: 'Human Resources' });
        const entries = [entry('5001', 'Basic Salary Expense', 'DR', 50000), entry('5002', 'Housing Allowance Expense', 'DR', 3000), entry('2100', 'Payroll Payable', 'CR', 53000)];
        const journal = { runId: RUN.id, period: '2026-09', currency: 'SAR', entries, totalDebits: 53000, totalCredits: 53000, isBalanced: true, isPosted: true,
          journals: [{ kind: 'accrual', group: null, eventType: 'PayrollAccrual', period: '2026-09', entryDate: '2026-09-13', postedAtUtc: '2026-09-13T08:00:00Z', postedByName: 'Test Approver', isReversed: false, hasCostCentres: true, entries, totalDebits: 53000, totalCredits: 53000, isBalanced: true }] };
        await boot(page, {
          persona, locale, url: `/payroll?tab=gl-journal&run=${RUN.id}`,
          mocks: { ...BASE_MOCKS, [`/api/payroll/runs/${RUN.id}/gl-journal`]: journal },
        });
        const block = page.getByTestId('gl-journal-accrual');
        await expect(block).toBeVisible({ timeout: 60_000 });
        await shot(page, 'gl-journal', locale);
        expect(await topOf(page, '[data-testid="gl-journal-accrual"]'), 'accrual journal top').toBeLessThanOrEqual(430);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('Employee Salary: Assign Salary is one line, the filter is a normal input and seven rows are in view', async ({ page }) => {
        const assignment = (n: number) => ({
          id: `a${n}`, employeeId: 200 + n, salaryStructureId: 's1', basicSalary: 4000, housingAllowance: 1000, transportAllowance: 400, foodAllowance: 0, mobileAllowance: 0, otherAllowance: 0,
          fixedDeduction: 0, effectiveDate: '2026-10-31', currency: 'SAR', isActive: true, createdAtUtc: '2026-10-31T00:00:00Z',
        });
        await boot(page, {
          persona, locale, url: '/payroll?tab=employee-salary',
          mocks: {
            ...BASE_MOCKS,
            '/api/payroll/employee-salary-structures': Array.from({ length: 14 }, (_, i) => assignment(i + 1)),
            '/api/payroll/salary-structures': [{ id: 's1', companyId: 'c1', companyName: 'Workspace Fixture LLC', code: 'G3', name: 'Grade 3 salary structure', currency: 'SAR', effectiveDate: '2026-01-01', minGrossSalary: 0, maxGrossSalary: 0 }],
            '/api/payroll/salary-changes': [{
              id: 'c1', source: 'manual', employeeId: 300, employeeCode: 'E-300', employeeName: 'Candidate', basicSalary: 4500, housingAllowance: 0, transportAllowance: 0, foodAllowance: 0, mobileAllowance: 0, otherAllowance: 0,
              fixedDeduction: 0, effectiveDate: '2026-11-01', currency: 'SAR', previousBasicSalary: 4000, status: 'Pending', requestedByName: 'Test Account', requestedAtUtc: '2026-10-31T00:00:00Z', decidedByName: '', decisionNote: '',
            }],
          },
        });
        const rows = page.locator('main table tbody tr');
        await expect(rows.first()).toBeVisible({ timeout: 60_000 });
        await shot(page, 'employee-salary', locale);
        const assign = page.getByRole('button', { name: translate(locale, 'Assign Salary') });
        expect(await assign.evaluate(el => Math.round(el.getBoundingClientRect().height)), 'Assign Salary button height').toBeLessThanOrEqual(44);
        const filter = page.getByLabel(translate(locale, 'Employee ID filter'));
        expect(await filter.evaluate(el => Math.round(el.getBoundingClientRect().width)), 'filter input width').toBeLessThanOrEqual(300);
        const inView = await rows.evaluateAll((trs, h) => trs.filter(r => r.getBoundingClientRect().bottom <= h).length, viewport.height);
        expect(inView, 'salary rows fully in view').toBeGreaterThanOrEqual(7);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });
    });
  }
}
