import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { translate } from '../../src/i18n/translations';
import { ALL_PERMISSIONS, boot, type ApiMocks, type Persona } from '../setup-consolidation/fixture';

/**
 * Space pass, batch 1 (Screen Standard 1 and 2): the main task of a screen sits in the first screenful at 1366x768 and 1280x720,
 * in English and Arabic, and the page never scrolls sideways. Every API call is intercepted; nothing is read or written.
 *
 *   /pay-approvals       the queue shows at least six packages without scrolling (it showed three)
 *   /loans               the loans table starts above y=340 (it started at 464 under the totals band and the flow banner) and shows four rows (three before)
 *   /saudi-compliance    the Action Center starts above y=330 and the module cards are in view (Refresh had a row of its own)
 *   /gosi-filing         the tie-out verdict starts above y=400
 *   /payroll             the Dashboard's KPI band starts above y=430 (it started at 670 under the run list and the readiness panel)
 *
 * SPACE_SHOTS_DIR=<dir> also saves a screenshot per screen, viewport and language (the before/after evidence).
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

const persona = (roles: string[], extra: string[] = []): Persona => ({ roles, permissions: [...ALL_PERMISSIONS, 'payroll.approve', 'payroll.export', ...extra] });

// ── /pay-approvals ─────────────────────────────────────────────────────────────────────────────────────────────────────
const payItem = (n: number) => ({
  employeeId: 100 + n, employeeCode: `EVT2-${String(n).padStart(4, '0')}`, name: `Candidate ${n}`, companyId: 'c1', companyName: 'Workspace Fixture LLC', designation: 'Accountant',
  currency: 'SAR', gross: 5400, ibanLast4: String(1000 + n), enteredBy: { name: 'John Smith', atUtc: '2026-10-31T09:15:00Z' }, lastChangedAtUtc: '2026-10-31T09:15:00Z', status: 'pending',
});

// ── /loans ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
const loan = (n: number) => ({
  id: `loan-${n}`, employeeId: `e-${n}`, employeeName: `Employee ${n}`, loanTypeId: 'type-1', loanTypeName: 'Personal Loan Policy', loanNumber: `LN-2026-${1000 + n}`,
  requestedAmount: 4000, approvedAmount: 4000, requestedInstallments: 6, approvedInstallments: 6, installmentAmount: 666.66, repaymentFrequency: 'Monthly', repaymentMethod: 'BankTransfer',
  currency: 'SAR', disbursementDate: '2026-09-01', totalRepaid: 0, outstandingBalance: 4000, status: 'Active', notes: '', isLockedByPayroll: false, createdAtUtc: '2026-09-01T00:00:00Z',
  policyVersion: 1, collectionStatus: 'Normal', reviewRequired: n % 2 === 0,
});

// ── /saudi-compliance ─────────────────────────────────────────────────────────────────────────────────────────────────
const action = (id: string, severity: string, module: string, title: string, affectedCount: number) => ({
  id, severity, module, title, description: `${title}.`, affectedCount, recommendedAction: 'Fix the records.', route: '/people', permissionRequired: 'compliance.read', canAct: true, evaluatedAt: '2026-11-18T21:32:00Z',
});
const SAUDI = {
  overall: { complianceScore: 77, urgentActionCount: 4, lastEvaluatedAt: '2026-11-18T21:32:00Z', enabledModules: ['QIWA', 'WPS', 'GOSI'], scoreBreakdown: [] },
  qiwa: { featureEnabled: false, credentialConfigured: false, connectionStatus: 'NotConfigured', lastConnectedAt: null, totalEmployees: 9, readyForSync: 0, blockedFromSync: 9, readinessPercent: 0, failedSyncCount: 0, lastSuccessfulSync: null, blockedEmployees: [] },
  wps: { lastRunStatus: 'Locked', lastRunPeriod: '2026-09', pendingApprovals: 0, missingIbanCount: 0, exportHistoryCount: 0, lastExportDate: null, lastExportStatus: null, blockingIssues: [] },
  gosi: { employeesMissingGosiRef: 6, employeesMissingGosiEmployerId: 0, gosiEmployerIdConfigured: false, readyCount: 3, blockedCount: 6, warningCount: 0, readinessPercent: 33.3, gccEmployeeCount: 0, varianceCount: 0, warnings: [], blockedEmployees: [] },
  actionItems: [
    action('a1', 'Critical', 'GOSI', '6 employees are blocked from GOSI calculation', 6),
    action('a2', 'High', 'QIWA', '9 employees are blocked from QIWA sync', 9),
    action('a3', 'High', 'GOSI', 'GOSI employer ID is not set', 0),
    action('a4', 'Medium', 'QIWA', 'QIWA integration is not enabled', 0),
  ],
};

// ── /gosi-filing ───────────────────────────────────────────────────────────────────────────────────────────────────────
const gosiRun = { id: '11111111-1111-4111-8111-111111111111', companyId: 'c1', year: 2026, month: 11, status: 'Locked', runType: 'Regular', totalGrossSalary: 144856.25, totalDeductions: 11000, totalNetSalary: 133588.9, totalEmployerStatutoryCost: 10206.25, employeeCount: 9, createdAtUtc: '2026-11-01T08:00:00Z', processedAtUtc: '2026-11-02T08:00:00Z', lockedAtUtc: '2026-11-03T08:00:00Z' };
const GOSI = {
  hasStatutoryData: true, packResolved: true, packStatusNote: null, totalEmployeeContrib: 7556.25, totalEmployerContrib: 10206.25, totalGosi: 17762.5, expectedEmployeeContrib: 7556.25, expectedEmployerContrib: 10206.25,
  expectedVsActualEmployeeDelta: 0, expectedVsActualEmployerDelta: 0, glPosted: true, glEmployeeLiability: 7556.25, glEmployerLiability: 10206.25, glEmployeeDelta: 0, glEmployerDelta: 0,
  branchBreakdown: [
    { componentCode: 'GOSI-ANN-EE', componentName: 'GOSI Annuities (Employee)', isEmployerContribution: false, totalAmount: 6975, employeeCount: 6 },
    { componentCode: 'GOSI-ANN-ER', componentName: 'GOSI Annuities (Employer)', isEmployerContribution: true, totalAmount: 7000, employeeCount: 6 },
  ],
  period: '2026-11', companyId: null, runCount: 1, runIds: [gosiRun.id], varianceCount: 0,
  runs: [{ runId: gosiRun.id, runType: 'Regular', status: 'Locked', includesRecurringPay: true, employeeTotal: 7556.25, employerTotal: 10206.25 }],
  employees: Array.from({ length: 9 }, (_, i) => ({ employeeId: i + 1, employeeCode: `E-${i + 1}`, employeeName: `Employee ${i + 1}`, classification: 'Saudi', coveredWageBase: 10000, expectedEmployee: 900, expectedEmployer: 1100, actualEmployee: 900, actualEmployer: 1100, runCount: 1, employeeVariance: 0, employerVariance: 0, hasVariance: false })),
};

// ── /payroll (Dashboard) ───────────────────────────────────────────────────────────────────────────────────────────────
const PAYROLL_RUN = { ...gosiRun, id: '22222222-2222-4222-8222-222222222222', month: 9 };
const PREREQ = {
  evaluatedEmployees: 9, blockedEmployees: 2, employeesWithRecommendations: 0, attendanceChecked: true, companyBlocking: [], companyRecommended: [], recommended: [],
  blocking: [{ code: 'STATUTORY_INCOMPLETE', label: 'Required statutory details are missing or expired', nextAction: 'Open the employee and complete their readiness checklist', count: 2 }],
  employees: [{ employeeId: 1, employeeCode: 'E-1', employeeName: 'Employee 1', companyId: 'c1', blocking: ['STATUTORY_INCOMPLETE'], recommended: [] }, { employeeId: 2, employeeCode: 'E-2', employeeName: 'Employee 2', companyId: 'c1', blocking: ['STATUTORY_INCOMPLETE'], recommended: [] }],
  employeesTruncated: false,
};
const PAYROLL_MOCKS: ApiMocks = {
  '/api/payroll/companies': [{ id: 'c1', name: 'Workspace Fixture LLC', tradeName: '', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' }],
  '/api/payroll/runs': { items: [PAYROLL_RUN], total: 1, page: 1, pageSize: 100 },
  '/api/payroll/payment-batches': [],
  '/api/payroll/overview': {
    year: 2026, month: 11, totalCompanies: 1, totalActiveEmployees: 9, totalGrossPayroll: 144856.25, totalNetPayroll: 133588.9, totalValidationErrors: 0, totalPendingApprovals: 0,
    companies: [{ companyId: 'c1', companyName: 'Workspace Fixture LLC', tradeName: '', currency: 'SAR', activeEmployees: 9, employeesWithSalary: 9, employeesMissingSalary: 0, salaryCoveragePercent: 100, payrollRunStatus: 'Locked', grossPayroll: 144856.25, totalDeductions: 11267.35, netPayroll: 133588.9, validationErrors: 0, validationWarnings: 0, pendingApprovals: 0, wpsEmployerId: '', gosiEmployerId: '', hasPayrollRun: true }],
  },
  '/api/payroll/readiness': { year: 2026, month: 11, companyId: null, completionPercent: 100, isReadyForProcessing: true, totalActiveEmployees: 9, employeesWithSalary: 9, salaryCoveragePercent: 100, validationErrors: 0, payrollRunStatus: 'Locked', steps: [], paymentPrerequisites: PREREQ },
  '/api/payroll/reports/summary': { totalRuns: 3, lockedRuns: 3, totalEmployeesPaid: 9, totalGrossYtd: 0, totalNetYtd: 0 },
};

for (const locale of LOCALES) {
  for (const viewport of VIEWPORTS) {
    test.describe(`space batch 1 (${locale} ${viewport.width}x${viewport.height})`, () => {
      test.use({ viewport });

      test('/pay-approvals: at least six packages are in view and nothing scrolls sideways', async ({ page }) => {
        await boot(page, {
          persona: persona(['Finance Approver']), locale, url: '/pay-approvals',
          mocks: { '/api/pay-approvals': { items: Array.from({ length: 14 }, (_, i) => payItem(i + 1)), requiresOtherReviewerCount: 0, hasMore: false } },
        });
        const list = page.getByTestId('pay-approvals-list');
        await expect(list).toBeVisible({ timeout: 60_000 });
        await expect(list.locator('> li')).toHaveCount(14);
        const inView = await list.locator('> li').evaluateAll((items, h) => items.filter(el => el.getBoundingClientRect().bottom <= h).length, viewport.height);
        await shot(page, 'pay-approvals', locale);
        expect(inView, 'packages fully in view').toBeGreaterThanOrEqual(6);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/loans: the loans table starts in the first screenful', async ({ page }) => {
        await boot(page, {
          persona: persona(['HR Manager']), locale, url: '/loans',
          mocks: { '/api/finance/loans': { items: Array.from({ length: 12 }, (_, i) => loan(i + 1)), total: 12 } },
        });
        await expect(page.locator('main table tbody tr').first()).toBeVisible({ timeout: 60_000 });
        await shot(page, 'loans', locale);
        expect(await topOf(page, 'main table'), 'loans table top').toBeLessThanOrEqual(340);
        const rowsInView = await page.locator('main table tbody tr').evaluateAll((rows, h) => rows.filter(r => r.getBoundingClientRect().bottom <= h).length, viewport.height);
        expect(rowsInView, 'loan rows fully in view').toBeGreaterThanOrEqual(4);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
        const scroller = await page.locator('main table').first().evaluate(t => { const p = t.parentElement!; return p.scrollWidth - p.clientWidth; });
        expect(scroller, 'the loans table scrolls sideways by (px)').toBeLessThanOrEqual(0);
      });

      test('/saudi-compliance: the Action Center starts in the first screenful and the module cards are in view', async ({ page }) => {
        await boot(page, { persona: persona(['HR Manager']), locale, url: '/saudi-compliance', mocks: { '/api/saudi-compliance/dashboard': SAUDI } });
        await expect(page.getByTestId('action-center-count')).toBeVisible({ timeout: 60_000 });
        await shot(page, 'saudi-compliance', locale);
        expect(await topOf(page, '[data-testid="action-center-count"]'), 'Action Center header top').toBeLessThanOrEqual(330);
        const cards = await page.locator('main section, main .surface').evaluateAll((els, h) => els.filter(el => el.getBoundingClientRect().top < h - 60).length, viewport.height);
        expect(cards, 'sections starting above the last 60 px of the screen').toBeGreaterThanOrEqual(3);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/gosi-filing: the tie-out verdict starts in the first screenful', async ({ page }) => {
        await boot(page, {
          persona: persona(['HR Manager']), locale, url: '/gosi-filing',
          mocks: {
            '/api/payroll/runs': { items: [gosiRun], total: 1, page: 1, pageSize: 50 },
            '/api/gosi/periods/2026/11/contribution-summary': GOSI,
          },
        });
        await expect(page.getByTestId('gosi-tieout')).toBeVisible({ timeout: 60_000 });
        await shot(page, 'gosi-filing', locale);
        expect(await topOf(page, '[data-testid="gosi-tieout"]'), 'tie-out top').toBeLessThanOrEqual(400);
        expect(await page.getByTestId('gosi-not-a-filing').isVisible(), 'the not-a-filing statement stays on screen').toBe(true);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/payroll: the Dashboard KPI band starts in the first screenful', async ({ page }) => {
        await boot(page, { persona: persona(['Admin']), locale, url: '/payroll', mocks: PAYROLL_MOCKS });
        await expect(page.getByTestId('payroll-section-panel')).toBeVisible({ timeout: 60_000 });
        const kpi = page.getByText(translate(locale, 'Gross Payroll'), { exact: true }).first();
        await expect(kpi).toBeVisible({ timeout: 30_000 });
        await shot(page, 'payroll', locale);
        const top = await kpi.evaluate(el => Math.round((el.closest('.surface, [class*="rounded"]') ?? el).getBoundingClientRect().top));
        expect(top, 'KPI band top').toBeLessThanOrEqual(430);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });
    });
  }
}
