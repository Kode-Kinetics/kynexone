import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { boot, shot, type Handler, type Locale } from './demo-polish.fixture';
import { LOCKED_RUN_FINDINGS } from './demo-polish.data';
import { bootEmployeeWorkspaceFixture } from './employee-workspace.fixture';

/**
 * Demo-polish screens against a mocked API (nothing is read or written). EN and AR for every screen.
 * Run: npx playwright test -c e2e/playwright.demo-polish.config.ts   (DEMO_POLISH_SHOTS=<dir> saves screenshots)
 */

const LOCALES: Locale[] = ['en', 'ar'];
const latinWords = (text: string, allowed: RegExp = /^$/) => (text.match(/[A-Za-z]{5,}/g) ?? []).filter(w => !allowed.test(w));

// ── Saudi Compliance dashboard ───────────────────────────────────────────────

const ACTION = (id: string, severity: string, module: string, title: string, affectedCount: number, over: Record<string, unknown> = {}) => ({
  id, severity, module, title, description: 'Description', affectedCount, recommendedAction: 'Do the thing', route: '/people', permissionRequired: 'compliance.read', canAct: true, evaluatedAt: '2026-11-14T02:03:00Z', ...over,
});
const COMPLIANCE_DASHBOARD = {
  overall: { complianceScore: 77, urgentActionCount: 3, lastEvaluatedAt: '2026-11-14T02:03:00Z', enabledModules: ['WPS', 'GOSI'], scoreBreakdown: [
    { module: 'QIWA', weightPercent: 30, score: 100, pointsContributed: 30, measurable: true, basis: 'Not switched on.' },
    { module: 'WPS', weightPercent: 35, score: 100, pointsContributed: 35, measurable: true, basis: 'No blocking issues.' },
    { module: 'GOSI', weightPercent: 35, score: 33, pointsContributed: 12, measurable: true, basis: '3 of 9 ready.' }] },
  qiwa: { featureEnabled: false, credentialConfigured: false, connectionStatus: 'NotConfigured', lastConnectedAt: null, totalEmployees: 9, readyForSync: 0, blockedFromSync: 9, readinessPercent: 0, failedSyncCount: 0, lastSuccessfulSync: null, isLiveIntegration: false, lastSimulatedSync: null, blockedEmployees: [] },
  wps: { lastRunStatus: 'Locked', lastRunPeriod: '2026-11', pendingApprovals: 0, missingIbanCount: 0, exportHistoryCount: 1, lastExportDate: '2026-11-14T01:05:00Z', lastExportStatus: 'Generated', blockingIssues: [] },
  gosi: { employeesMissingGosiRef: 0, employeesMissingGosiEmployerId: 1, gosiEmployerIdConfigured: false, readyCount: 3, blockedCount: 6, warningCount: 6, readinessPercent: 33.3, gccEmployeeCount: 0, varianceCount: 0,
    warnings: ['Company GOSI employer ID is not set.'],
    blockedEmployees: [{ employeeId: 3, employeeCode: 'EVT2-0003', fullName: 'Khalid Alotaibi', blockingIssueCodes: ['GOSI_COHORT_NOT_RECORDED'] }] },
  actionItems: [
    ACTION('gosi_blocked_employees', 'Critical', 'GOSI', '6 employees are blocked from GOSI calculation', 6),
    ACTION('qiwa_blocked_employees', 'High', 'QIWA', '9 employees are blocked from QIWA sync', 9),
    ACTION('gosi_missing_employer_id', 'High', 'GOSI', 'GOSI employer ID is not set', 0),
    ACTION('qiwa_feature_disabled', 'Medium', 'QIWA', 'QIWA integration is not enabled', 0),
    ACTION('wps_pending_approvals', 'Medium', 'WPS', '2 payroll runs are awaiting approval', 2),
  ],
};
const complianceRoutes: Handler = path => path === '/api/saudi-compliance/dashboard' ? COMPLIANCE_DASHBOARD : undefined;

for (const locale of LOCALES) {
  test.describe(`Saudi Compliance dashboard (${locale})`, () => {
    test('the headline and the Action Center badge give one count', async ({ page }) => {
      await boot(page, { locale, url: '/saudi-compliance', routes: complianceRoutes, waitMs: 2500 });
      const headline = page.getByTestId('urgent-action-count');
      await expect(headline).toBeVisible({ timeout: 60_000 });
      const badge = page.getByTestId('action-center-count');
      await expect(badge).toHaveText('5');
      expect(await headline.innerText()).toMatch(/5|٥/);
      await shot(page, 'compliance-dashboard', locale);
    });

    test('action titles, module names, GOSI codes and warnings read in the viewer\'s language', async ({ page }) => {
      await boot(page, { locale, url: '/saudi-compliance', routes: complianceRoutes, waitMs: 2500 });
      const center = page.getByTestId('action-center-count').locator('xpath=ancestor::div[contains(@class,"surface")][1]');
      await expect(center).toBeVisible({ timeout: 60_000 });
      const centerText = await center.innerText();
      const cards = await page.locator('section, div.surface').filter({ hasText: /GOSI_COHORT|تاريخ التسجيل الأول|first-registration/ }).last().innerText();
      if (locale === 'ar') {
        expect(latinWords(centerText), centerText).toEqual([]);
        expect(cards).not.toMatch(/GOSI_COHORT_NOT_RECORDED/);
        expect(latinWords(cards.replace(/EVT2-\d+|Khalid|Alotaibi|Generated/g, '')), cards).toEqual([]);
      } else {
        expect(centerText).toContain('6 employees are blocked from GOSI calculation');
        expect(cards).not.toMatch(/GOSI_COHORT_NOT_RECORDED/);
      }
    });
  });
}

// ── Payroll (shared mocks) ───────────────────────────────────────────────────

const RUN_ID = '11111111-1111-4111-8111-111111111111';
const VOIDED_A = '22222222-2222-4222-8222-222222222222';
const VOIDED_B = '33333333-3333-4333-8333-333333333333';
const RUN = (id: string, status: string, over: Record<string, unknown> = {}) => ({
  id, companyId: 'c1', year: 2026, month: 11, status, runType: 'Regular', totalGrossSalary: 144856.25, totalDeductions: 11267.35, totalNetSalary: 133588.9, totalEmployerStatutoryCost: 10206.25, employeeCount: 9,
  createdAtUtc: '2026-11-14T00:50:00Z', processedAtUtc: '2026-11-14T00:51:00Z', lockedAtUtc: status === 'Locked' ? '2026-11-14T00:53:00Z' : null, ...over,
});
const RUNS = [RUN(VOIDED_A, 'Voided'), RUN(VOIDED_B, 'Voided'), RUN(RUN_ID, 'Locked')];
const RECONCILIATION = {
  runId: RUN_ID, period: '2026-11', priorPeriod: '2026-10', currentHeadcount: 9, priorHeadcount: 8, joinerCount: 1, leaverCount: 0, flaggedVariances: 0,
  currentTotalGross: 144856.25, priorTotalGross: 120000, currentTotalNet: 133588.9, priorTotalNet: 111000, variances: [],
};
const BATCH = {
  id: 'b1', payrollRunId: RUN_ID, batchNumber: 'PAY-202611-220353', paymentMethod: 'WPS', totalAmount: 133588.9, currency: 'SAR', status: 'Generated', wpsStatus: 'Generated',
  wpsStatusChangedAtUtc: null, wpsSubmissionReference: null, wpsRejectionReason: null, createdAtUtc: '2026-11-14T01:05:00Z', acceptanceEvidenceCount: 0, isSaudi: true,
  generatedFileLabel: 'ANB Connect payroll instruction', effectiveWpsStatus: 'Generated', wpsStatusLabel: 'Generated (file ready for the bank)',
};
/** Two of nine active employees have a gap that would have blocked payment: the run is already Locked and exported. */
const READINESS = {
  year: 2026, month: 11, companyId: null, completionPercent: 100, isReadyForProcessing: true, totalActiveEmployees: 9, employeesWithSalary: 9, salaryCoveragePercent: 100,
  validationErrors: 0, payrollRunStatus: 'Locked', steps: [],
  isReadyToPay: false,
  paymentPrerequisites: {
    evaluatedEmployees: 9, blockedEmployees: 2, employeesWithRecommendations: 0, attendanceChecked: true, companyBlocking: [], companyRecommended: [],
    blocking: [{ code: 'READINESS_PAY_BLOCKED', label: 'Required statutory details are missing or expired', nextAction: 'Open the employee and complete their readiness checklist', count: 2 }],
    recommended: [], employees: [], ['employees' + 'Truncated']: false,
  },
};
const payroll = (over: Handler = () => undefined): Handler => (path, search, method, route) => {
  const custom = over(path, search, method, route);
  if (custom !== undefined) return custom;
  if (path === '/api/payroll/payment-batches') return [BATCH];
  if (path === '/api/payroll/readiness') return READINESS;
  if (path === '/api/payroll/overview') return { year: 2026, month: 11, totalCompanies: 1, totalActiveEmployees: 9, totalGrossPayroll: 144856.25, totalNetPayroll: 133588.9, totalValidationErrors: 0, totalPendingApprovals: 0, companies: [] };
  if (/^\/api\/payroll\/runs\/[^/]+\/processing$/.test(path)) return { runId: RUN_ID, jobId: null, status: 'Idle', processedEmployees: null, totalEmployees: null, startedAtUtc: null, finishedAtUtc: null, errorCode: null };
  if (/^\/api\/payroll\/runs\/[^/]+\/(validate|approvals)$/.test(path)) return [];
  if (/^\/api\/payroll\/runs\/[^/]+\/validation-overrides$/.test(path)) return { runId: RUN_ID, overrides: [], overridableCodes: [], nonOverridableCodes: [] };
  if (path === '/api/payroll/companies') return [COMPANY_ROW];
  if (path === '/api/payroll/runs') return { items: RUNS, total: RUNS.length, page: 1, pageSize: 100 };
  if (path === '/api/payroll/reports/reconciliation') return RECONCILIATION;
  return undefined;
};
const COMPANY_ROW = { id: 'c1', name: 'Acme Arabia', legalNameEn: 'Acme Arabia LLC', tradeName: 'Acme Arabia', defaultCurrency: 'SAR', countryCode: 'SA', isActive: true };

for (const locale of LOCALES) {
  test.describe(`Payroll reconciliation tab (${locale})`, () => {
    test('the gross card and the net card carry different labels', async ({ page }) => {
      await boot(page, { locale, url: `/payroll?tab=reconciliation&run=${RUN_ID}`, routes: payroll(), waitMs: 2500 });
      await page.getByRole('button', { name: locale === 'ar' ? 'المطابقة' : 'Reconcile', exact: true }).click({ timeout: 60_000 });
      const cards = page.locator('p.text-xs.text-slate-400').filter({ hasText: locale === 'ar' ? /إجمالي/ : /^Total/ });
      await expect(cards.first()).toBeVisible({ timeout: 60_000 });
      const texts = await cards.allInnerTexts();
      expect(new Set(texts).size, `labels: ${texts.join(' | ')}`).toBe(2);
      if (locale === 'ar') expect(texts.join(' ')).toContain('صافي');
      await shot(page, 'payroll-reconciliation', locale);
    });
  });
}

for (const locale of LOCALES) {
  test.describe(`Payroll dashboard on a Locked, exported run (${locale})`, () => {
    test('readiness drift is a note for next month, never a payment block, and the pipeline is translated', async ({ page }) => {
      await boot(page, { locale, url: `/payroll?tab=dashboard&run=${RUN_ID}`, routes: payroll(), waitMs: 2500 });
      const panel = page.getByRole('region', { name: locale === 'ar' ? 'جاهزية الدفع' : 'Payment readiness' });
      await expect(panel).toBeVisible({ timeout: 90_000 });
      const text = await panel.innerText();
      expect(text).not.toMatch(/BLOCKS PAYMENT|Blocks payment|يمنع الدفع|cannot be paid yet/i);
      expect(text).toMatch(locale === 'ar' ? /قبل الشهر القادم/ : /before next month/);
      if (locale === 'ar') expect(latinWords(text), text).toEqual([]);
      // The pipeline's bank-file line and its dates read in the viewer's language.
      const pipeline = page.getByRole('list', { name: /Stages of payroll run|مراحل مسير/ });
      const pipelineText = await pipeline.innerText();
      if (locale === 'ar') {
        expect(latinWords(pipelineText.replace(/PAY-\d+-\d+/, '')), pipelineText).toEqual([]);
        expect(pipelineText).not.toMatch(/ ,/);
      } else {
        expect(pipelineText).toContain('Generated (file ready for the bank)');
      }
      await shot(page, 'payroll-dashboard-locked', locale);
    });
  });
}

// ── The Review stage card on a Locked run ────────────────────────────────────

const RECON_SIGNED = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'recon-signed.json'), 'utf8'));
const reviewRoutes = payroll((p) => {
  if (/^\/api\/payroll\/runs\/[^/]+\/recon$/.test(p)) return RECON_SIGNED;
  if (/^\/api\/payroll\/runs\/[^/]+\/validate$/.test(p)) return LOCKED_RUN_FINDINGS;
  return undefined;
});

for (const locale of LOCALES) {
  test.describe(`Review stage on a Locked run (${locale})`, () => {
    test('shows the sign-off summary; the findings sit under Details, translated, with no raw setting key', async ({ page }) => {
      await boot(page, { locale, url: `/payroll?tab=dashboard&run=${RUN_ID}`, routes: reviewRoutes, waitMs: 2500 });
      await page.getByRole('button', { name: locale === 'ar' ? 'مراجعة' : 'Review', exact: true }).first().click({ timeout: 90_000 });
      const card = page.getByTestId('run-review');
      await expect(card).toBeVisible();
      await expect(page.getByTestId('review-complete')).toBeVisible();
      // Collapsed: nothing of the findings is visible until the reader opens Details.
      await expect(page.getByTestId('review-issue-row').first()).toBeHidden();
      await card.locator('summary').first().click();
      const rows = page.getByTestId('review-issue-row');
      await expect(rows.first()).toBeVisible();
      const text = await rows.first().innerText();
      expect(text).not.toMatch(/payparameter|arrears_max|before this run is locked|2022-0\d/);
      if (locale === 'ar') expect(latinWords(text.replace(/Employee \d+/, '')), text).toEqual([]);
      await shot(page, 'payroll-review-locked', locale);
    });
  });
}

// ── New-hire readiness overview (/people/{id}) ───────────────────────────────

const READINESS_ITEMS = {
  blocking: [
    { key: 'BankIban', label: 'IBAN (WPS)', category: 'payroll', reason: 'statutory' },
    { key: 'SalaryPackageApproval', label: 'Salary package waiting for Finance approval — it must be approved by a different person before this employee can be activated', category: 'payroll', reason: 'approval' },
  ],
  payBlocking: [{ key: 'GosiReference', label: 'GOSI reference', category: 'identity', reason: 'statutory' }, { key: 'QiwaContractNumber', label: 'Qiwa contract number', category: 'identity', reason: 'statutory' }],
  recommended: [{ key: 'SalaryStructure', label: 'Salary', category: 'payroll', reason: 'recommended' }],
  expiringSoon: [], present: [], progress: { present: 2, requiredTotal: 9 }, disclaimer: null,
};

for (const locale of LOCALES) {
  test.describe(`New-hire readiness overview (${locale})`, () => {
    test('the body is translated, and the API reason tokens read as sentences', async ({ page }) => {
      const control = await bootEmployeeWorkspaceFixture(page, { locale });
      control.readinessBody.set(901, READINESS_ITEMS);
      await page.goto('/people/901');
      const next = page.getByRole('heading', { name: locale === 'ar' ? 'الإجراء التالي الموصى به' : 'Recommended next action' });
      await expect(next).toBeVisible({ timeout: 90_000 });
      const text = await page.locator('main').first().innerText();
      expect(text).not.toMatch(/^statutory$|^approval$|^recommended$/m);
      if (locale === 'ar') {
        // The page chrome and the requirement cards: no English sentence is left (the employee's own data and codes are not chrome).
        const cards = await page.locator('section').filter({ hasText: /المتطلبات المفتوحة|Open requirements/ }).last().innerText();
        expect(latinWords(cards.replace(/WPS|GOSI|IBAN/g, '')), cards).toEqual([]);
        const heading = await page.locator('section').filter({ has: next }).first().innerText();
        // The employee's own record values (department, designation, place) are data, not chrome.
        expect(latinWords(heading.replace(/WPS|GOSI|IBAN/g, ''), /^(Finance|Manager|Jeddah|Master|Test)$/), heading).toEqual([]);
        await expect(page.getByText('Record summary')).toHaveCount(0);
      }
      await shot(page, 'new-hire-readiness-overview', locale);
    });
  });
}

// ── Overtime calculation preview ─────────────────────────────────────────────

const OT_REQUESTS = [
  { id: 'r1', employeeId: 226, employeeName: 'Fahad Alharbi', status: 'Approved', workDate: '2026-11-12', requestedMinutes: 240, approvedMinutes: 240 },
  { id: 'r2', employeeId: 226, employeeName: 'Fahad Alharbi', status: 'Approved', workDate: '2026-11-12', requestedMinutes: 60, approvedMinutes: 60 },
];
const OT_CALCULATIONS = [
  { id: 'c1', overtimeRequestId: 'r1', employeeId: 226, approvedHours: 4, hourlyRate: 168.75, multiplier: 2, amount: 1175, currency: 'SAR', calculationJson: '{}', createdAtUtc: '2026-11-12T10:00:00Z' },
  { id: 'c2', overtimeRequestId: 'r2', employeeId: 226, approvedHours: 1, hourlyRate: 168.75, multiplier: 1.5, amount: 231.25, currency: 'SAR', calculationJson: '{}', createdAtUtc: '2026-11-12T10:00:00Z' },
  { id: 'c3', overtimeRequestId: 'r3', employeeId: 226, approvedHours: 100, hourlyRate: 1234.5, multiplier: 1.5, amount: 123456.5, currency: 'SAR', calculationJson: '{}', createdAtUtc: '2026-11-12T10:00:00Z' },
];
const overtimeRoutes: Handler = path => {
  if (path === '/api/overtime/calculations') return OT_CALCULATIONS;
  if (path === '/api/overtime/requests') return { items: OT_REQUESTS, total: OT_REQUESTS.length, page: 1, pageSize: 100 };
  return undefined;
};

for (const locale of LOCALES) {
  test.describe(`Overtime calculation preview (${locale})`, () => {
    test('shows the employee name, not #226, and amounts with thousands separators', async ({ page }) => {
      await boot(page, { locale, url: '/overtime', routes: overtimeRoutes, waitMs: 2500 });
      await page.getByRole('button', { name: /Calculation Preview|معاينة الاحتساب/ }).first().click({ timeout: 90_000 });
      const table = page.locator('table').first();
      await expect(table).toBeVisible();
      const text = await table.innerText();
      expect(text).not.toMatch(/#226|Emp #/);
      expect(text).toContain('Fahad Alharbi');
      expect(text).toContain('1,175.00');
      expect(text).toContain('123,456.50');
      expect(text).toContain('1,234.50');
      await shot(page, 'overtime-calculation-preview', locale);
    });
  });
}

// ── Run selectors ────────────────────────────────────────────────────────────

for (const locale of LOCALES) {
  test.describe(`Run selectors (${locale})`, () => {
    test('the live run is listed first and voided runs sit in their own group', async ({ page }) => {
      await boot(page, { locale, url: `/payroll?tab=reconciliation&run=${RUN_ID}`, routes: payroll(), waitMs: 2500 });
      const select = page.getByRole('tabpanel').getByRole('combobox').first();
      await expect(select).toBeVisible({ timeout: 90_000 });
      await expect(select.locator('option')).toHaveCount(4);
      const texts = (loc: ReturnType<typeof select.locator>) => loc.evaluateAll(els => els.map(e => (e.textContent ?? '').trim()));
      const options = await texts(select.locator('option'));
      const firstRun = options.find(o => /November|نوفمبر/.test(o));
      expect(firstRun, options.join(' | ')).toMatch(locale === 'ar' ? /مقفل/ : /Locked/);
      const groups = await select.locator('optgroup').evaluateAll(els => els.map(e => (e as HTMLOptGroupElement).label));
      expect(groups).toEqual([locale === 'ar' ? 'المسيرات المُبطَلة' : 'Voided runs']);
      const voided = await texts(select.locator('optgroup option'));
      expect(voided).toHaveLength(2);
      for (const v of voided) expect(v).toMatch(locale === 'ar' ? /مُبطَل/ : /Voided/);
      await shot(page, 'payroll-run-selector', locale);
    });
  });
}

// ── Offer workspace (a Sent offer) ───────────────────────────────────────────

const OFFER_ID = 'o-1';
const OFFER_DEADLINE = '2026-11-18T23:43:00.000Z'; // 02:43 on 19 Nov in Riyadh
const OFFER_COMPONENTS = [
  { code: 'BASIC', labelEn: 'Basic salary', labelAr: null, kind: 'Cash', amount: 4400, description: null, frequency: 'Monthly', included: true, exclusionReason: null, payComponentId: null, order: 0, required: true },
  { code: 'HOUSING', labelEn: 'Housing allowance', labelAr: null, kind: 'Cash', amount: 1100, description: null, frequency: 'Monthly', included: true, exclusionReason: null, payComponentId: null, order: 1, required: false },
];
const OFFER_WORKSPACE = {
  offer: { version: 10, id: OFFER_ID, applicationId: 'app-1', candidateName: 'Nora Alharbi', offeredJobTitle: 'Sales Executive', offeredDepartment: 'Sales', startDate: '2027-01-15', basicSalary: 4400, housingAllowance: 1100, transportAllowance: 440, otherAllowances: 0,
    probationMonths: 6, responseDeadline: OFFER_DEADLINE, status: 'Sent', seriesId: 's-1', revisionNumber: 1, recipientEmail: 'nora@example.invalid', frozenAtUtc: '2026-11-12T04:43:00Z', companyId: 'c1', generatedAtUtc: '2026-11-12T04:43:00Z' },
  artifact: null, originalProvenance: 'Frozen', approvals: [],
  capabilities: { editDraft: { allowed: false, reason: 'Create a new revision to change submitted terms.' }, recordResponse: { allowed: true }, issue: { allowed: false } },
  revisions: [{ id: OFFER_ID, applicationId: 'app-1', companyId: 'c1', seriesId: 's-1', revisionNumber: 1, version: 10, candidateName: 'Nora Alharbi', position: 'Sales Executive', status: 'Sent', communicationOutcome: 'AcceptedByRelay', originalProvenance: 'Frozen', generatedAtUtc: '2026-11-12T04:43:00Z', responseDeadline: OFFER_DEADLINE }],
  changedFields: [], communicationOutcome: 'AcceptedByRelay',
  compensationSnapshot: { schemaVersion: 1, currency: 'SAR', precision: 2, components: OFFER_COMPONENTS, monthlyCash: 5940, annualRecurringCash: 71280, oneTimeCash: 0, basicSalary: 4400, housingAllowance: 1100, transportAllowance: 440, otherAllowances: 0, policyVersion: 1, policySource: 'Company' },
  designSnapshot: null,
};
const offerRoutes: Handler = path => {
  if (path === `/api/recruitment/offers/${OFFER_ID}/workspace`) return OFFER_WORKSPACE;
  if (path === '/api/recruitment/offer-templates/defaults') return { design: { schemaVersion: 2 }, tokens: [] };
  return undefined;
};

for (const locale of LOCALES) {
  test.describe(`Offer workspace (${locale})`, () => {
    test.use({ timezoneId: 'America/New_York' });
    test('money has separators, the deadline is in the tenant zone, and the page is translated', async ({ page }) => {
      await boot(page, { locale, url: `/recruitment/offers/${OFFER_ID}`, routes: offerRoutes, waitMs: 2500 });
      const totals = page.locator('dl').filter({ hasText: locale === 'ar' ? /النقد الشهري/ : /Monthly cash/ }).first();
      await expect(totals).toBeVisible({ timeout: 90_000 });
      const money = await totals.innerText();
      expect(money).toMatch(locale === 'ar' ? /5,940\.00/ : /SAR\s5,940\.00/);
      expect(money).toMatch(/71,280\.00/);
      expect(money).not.toMatch(/5940\.00/);
      // The browser is in New York; the deadline (02:43 on 19 Nov in Riyadh) must not read as 18 Nov 9:43 PM.
      const deadline = page.getByRole('group', { name: locale === 'ar' ? 'الموعد النهائي للرد' : 'Response deadline' });
      await expect(deadline).toBeVisible();
      await expect(deadline.getByLabel(locale === 'ar' ? 'اليوم' : 'Day')).toHaveValue('19');
      await expect(deadline.getByLabel(locale === 'ar' ? 'الشهر' : 'Month')).toHaveValue('11');
      await expect(deadline.getByLabel(locale === 'ar' ? 'الساعة' : 'Hour')).toHaveValue('2');
      await expect(deadline.getByLabel(locale === 'ar' ? 'الدقيقة' : 'Minute')).toHaveValue('43');
      if (locale === 'ar') {
        const body = await page.locator('main').first().innerText();
        expect(body).not.toContain('Create a new revision to change submitted terms');
        expect(body).not.toMatch(/^\d+\. Basic salary/m);
      }
      await shot(page, 'offer-terms', locale);
    });
  });
}
