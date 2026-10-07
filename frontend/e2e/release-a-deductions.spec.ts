import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';

/**
 * Release A slice R3 — the deductions statement, fixture lane (e2e/playwright.fixture.config.ts).
 *
 * The API responses are NOT hand-written: e2e/fixtures/release-a-deductions.json is the read model's own output, written
 * by DeductionStatementPostgresTests.FrontendFixture_IsTheReadModelsOwnOutput from a real payroll run on PostgreSQL (two
 * loans in one LOAN_EMI line, GOSI, and a colleague over the 50% limit by an unrecognised deduction). That test fails if
 * the API's shape drifts from this file. Every screen runs in English and Arabic.
 *
 * Screenshots: set R3_EVIDENCE_DIR.
 */

const FIXTURE = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/release-a-deductions.json'), 'utf8'));
const EVIDENCE = process.env.R3_EVIDENCE_DIR;
const NOW = new Date('2026-10-06T07:00:00Z');
const COMPANY = { id: 'company-1', name: 'Masar Facility Services', code: 'MFS', countryCode: 'SA', isActive: true };

type Lang = 'en' | 'ar';
/** A handler answer that makes the route 404 (an optional panel the screen degrades without). */
const NOT_FOUND = Symbol('404');
type Handler = (p: string, method: string, query: URLSearchParams, body: unknown) => unknown;

const HR = {
  id: 'u-hr', tenantId: 't1', tenantSlug: 'masar', email: 'hr@masar.test', fullName: 'Huda Payroll', employeeId: 900,
  roles: ['HR Manager'], accountType: 'Tenant', isGroupScope: false, companies: [COMPANY],
  permissions: ['dashboard.read', 'employees.read', 'employees.write', 'employees.documents', 'payroll.read', 'loans.read', 'loans.write'],
};
const EMPLOYEE = {
  id: 'u-emp', tenantId: 't1', tenantSlug: 'masar', email: 'mohammed@masar.test', fullName: FIXTURE.essMine.statements[0].employeeName,
  employeeId: FIXTURE.essMine.statements[0].employeeId, roles: ['Employee'], accountType: 'Tenant', isGroupScope: false, companies: [COMPANY],
  permissions: ['ess.read', 'ess.write', 'loans.self'],
};

async function boot(page: Page, lang: Lang, user: typeof HR, handler: Handler) {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.clock.setFixedTime(NOW);
  await page.addInitScript((l) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
    localStorage.setItem('kynexone-locale-choice-v2', l);
  }, lang);
  await page.route('**/api/**', (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const p = url.pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    let body: unknown = null;
    try { body = req.postData() ? req.postDataJSON() : null; } catch { body = req.postData(); } // multipart uploads are not JSON
    const answer = handler(p, req.method(), url.searchParams, body);
    if (answer === NOT_FOUND) return json({ message: 'Not found' }, 404);
    if (answer !== undefined) return json(answer);
    if (p === '/api/auth/me') return json(user);
    // release_a is opt-in: an absent key in disabled-keys means the platform switched it on for this tenant.
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false, currencyCode: 'SAR' });
    if (p === '/api/payroll/companies') return json([{ id: COMPANY.id, name: COMPANY.name, tradeName: COMPANY.name, defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' }]);
    // The payroll dashboard's own panels are not under test here: they answer "unavailable", which the page handles.
    if (p === '/api/payroll/overview' || p === '/api/payroll/readiness' || p === '/api/payroll/reports/summary') return json({ message: 'Not found' }, 404);
    return json({ items: [], total: 0 });
  });
  return errors;
}

async function snap(page: Page, name: string, lang: Lang, project: string) {
  if (!EVIDENCE) return;
  fs.mkdirSync(EVIDENCE, { recursive: true });
  await page.screenshot({ path: `${EVIDENCE}/${name}-${lang}-${project}.png`, fullPage: false });
}

const RUN_ID = FIXTURE.runRows[0] ? FIXTURE.slipWithLoans.runId : 'run-1';
const RUN = {
  id: RUN_ID, companyId: COMPANY.id, year: FIXTURE.slipWithLoans.year, month: FIXTURE.slipWithLoans.month, status: 'Processed',
  runType: 'Regular', employeeCount: FIXTURE.runRows.length, totalGrossSalary: 22000, totalDeductions: 8575, totalNetSalary: 13425,
  totalEmployerStatutoryCost: 0, createdAtUtc: '2026-06-01T00:00:00Z', erpPostingStatus: 'NotReady', includesRecurringPay: true,
};
const SLIPS = FIXTURE.runRows.map((r: { slipId: string; employeeId: number; employeeCode: string; employeeName: string; wageDue: number }, i: number) => ({
  id: r.slipId, runId: RUN_ID, employeeId: r.employeeId, employeeCode: r.employeeCode, employeeName: r.employeeName, department: 'Operations',
  basicSalary: 8000, housingAllowance: 2000, transportAllowance: 800, otherAllowances: 200, grossSalary: r.wageDue,
  deductions: i === 0 ? FIXTURE.slipWithLoans.slipDeductionTotal : FIXTURE.slipOverLimit.slipDeductionTotal,
  netSalary: r.wageDue - (i === 0 ? FIXTURE.slipWithLoans.slipDeductionTotal : FIXTURE.slipOverLimit.slipDeductionTotal),
  status: 'Draft', employeeStatutoryTotal: i === 0 ? 975 : 0, employerStatutoryTotal: 0, ytdGross: 0, ytdDeductions: 0, ytdNet: 0,
  loanDeductions: i === 0 ? 1600 : 0, deductionLines: [],
}));

for (const lang of ['en', 'ar'] as const) {
  test.describe(`deductions (${lang})`, () => {
    test(`HR: the run's deductions check opens on the over-limit employee, and the drawer explains every line`, async ({ page }, info) => {
      const errors = await boot(page, lang, HR, (p) => {
        if (p === '/api/payroll/runs') return { items: [RUN], total: 1, page: 1, pageSize: 100 };
        if (p === `/api/payroll/runs/${RUN_ID}/slips`) return { items: SLIPS, total: SLIPS.length, page: 1, pageSize: 100 };
        if (p === `/api/payroll/runs/${RUN_ID}/deduction-statements`) return FIXTURE.runRows;
        if (p === `/api/payroll/slips/${FIXTURE.slipOverLimit.slipId}/deduction-statement`) return FIXTURE.slipOverLimit;
        if (p === `/api/payroll/slips/${FIXTURE.slipWithLoans.slipId}/deduction-statement`) return FIXTURE.slipWithLoans;
        return undefined;
      });
      await page.goto('/payroll');
      await page.getByRole('tab', { name: lang === 'ar' ? /مسيرات|Payroll Runs/ : 'Payroll Runs' }).click();
      await page.locator('[role="button"]').filter({ hasText: String(RUN.year) }).first().click();
      const review = page.getByTestId('run-deductions-review');
      await expect(review).toBeVisible({ timeout: 30_000 });
      // Exception-first: only the employee over the limit is listed until "Everyone" is chosen.
      await expect(review.getByTestId('run-deduction-row')).toHaveCount(1);
      await expect(review.getByTestId('run-deduction-row')).toContainText(FIXTURE.slipOverLimit.employeeName);
      await review.getByRole('button', { name: lang === 'ar' ? 'الجميع' : 'Everyone' }).click();
      await expect(review.getByTestId('run-deduction-row')).toHaveCount(2);
      await review.getByRole('button', { name: lang === 'ar' ? 'قريب من الحد أو يتجاوزه' : 'Near or over the limit' }).click();

      await review.getByTestId('run-deduction-row').getByRole('button').click();
      const drawer = page.getByTestId('deductions-drawer');
      await expect(drawer).toBeVisible();
      await expect(drawer.getByTestId('cap-status')).toHaveText(lang === 'ar' ? 'يتجاوز الحد' : 'Over the limit');
      await expect(drawer.getByRole('alert').first()).toContainText(lang === 'ar' ? 'الاستقطاعات تتجاوز نصف الأجر' : 'Deductions above half the wage');
      await expect(drawer).toContainText(lang === 'ar' ? 'استقطاع من نوع غير معروف' : 'A deduction of an unrecognised type');
      await expect(drawer.getByTestId('statement-reconciles')).not.toHaveClass(/amber/);
      await snap(page, 'hr-drawer-over-limit', lang, info.project.name);
      await page.keyboard.press('Escape');
      await expect(drawer).toHaveCount(0);

      // The loan employee: two loans split from one LOAN_EMI line, each with what is left.
      await review.getByRole('button', { name: lang === 'ar' ? 'الجميع' : 'Everyone' }).click();
      await review.getByTestId('run-deduction-row').filter({ hasText: FIXTURE.slipWithLoans.employeeName }).getByRole('button').click();
      const loans = page.getByTestId('deductions-drawer');
      await expect(loans.getByTestId('lines-counted').getByTestId('deduction-line')).toHaveCount(2);
      await expect(loans.getByTestId('lines-not-counted').getByTestId('deduction-line')).toHaveCount(2); // GOSI annuities + SANED
      await expect(loans.getByTestId('deduction-sentence').first()).toContainText(lang === 'ar' ? 'تبقّى' : 'instalments left');
      await snap(page, 'hr-drawer-loans', lang, info.project.name);
      expect(errors).toEqual([]);
    });

    test('HR: the Deductions tab on the employee profile', async ({ page }, info) => {
      const employeeId = FIXTURE.slipWithLoans.employeeId;
      const errors = await boot(page, lang, HR, (p) => {
        if (p === `/api/employees/${employeeId}`) return employeeDetail(employeeId);
        if (p === '/api/employees') return { items: [employeeRow(employeeId)], total: 1, page: 1, pageSize: 25 };
        if (p === `/api/payroll/employees/${employeeId}/deduction-statements`) return FIXTURE.employeeHr;
        // The profile's own readiness and field catalogue are not under test here.
        if (p === `/api/employees/${employeeId}/readiness` || p === '/api/employees/field-catalog') return NOT_FOUND;
        return undefined;
      });
      await page.goto(`/people?employeeId=${employeeId}`);
      // The tab strip scrolls sideways; on a phone in RTL Playwright cannot scroll the tab into its viewport, so it is
      // clicked by its DOM event (a person swipes the strip).
      await page.getByRole('button', { name: lang === 'ar' ? 'الاستقطاعات' : 'Deductions', exact: true }).dispatchEvent('click');
      const panel = page.getByTestId('employee-deductions-panel');
      await expect(panel.getByTestId('balance')).toHaveCount(2);
      await expect(panel.getByTestId('deduction-statement')).toBeVisible();
      await expect(panel).toContainText(lang === 'ar' ? 'موافقة الموظف الكتابية محفوظة في ملفه.' : "The employee's written consent is on file.");
      await panel.scrollIntoViewIfNeeded();
      await snap(page, 'hr-profile-panel', lang, info.project.name);
      expect(errors).toEqual([]);
    });

    test('Employee: My deductions explains each deduction in plain sentences', async ({ page }, info) => {
      const errors = await boot(page, lang, EMPLOYEE, (p) => {
        if (p === '/api/ess/deductions') return FIXTURE.essMine;
        return undefined;
      });
      await page.goto('/ess/deductions');
      const mine = page.getByTestId('my-deductions');
      await expect(mine.getByRole('heading', { name: lang === 'ar' ? 'خصوماتي' : 'My deductions' })).toBeVisible({ timeout: 30_000 });
      await expect(mine.getByTestId('balance')).toHaveCount(2);
      const sentence = mine.getByTestId('deduction-sentence').first();
      await expect(sentence).toBeVisible();
      if (lang === 'en') await expect(sentence).toHaveText(/Personal loan LN-R3-\S+ instalment of .+: \d+ of \d+ instalments left, .+ still owed\./);
      await expect(mine).toContainText(lang === 'ar' ? 'الحد دون موافقتك الكتابية: 10% من أجرك' : 'Limit without your written consent: 10% of your wage');
      // GOSI is shown but not counted (#177); the counted total drills to the two loan lines.
      await expect(mine.getByTestId('lines-not-counted').getByTestId('deduction-line')).toHaveCount(2);
      await expect(mine.getByTestId('statement-reconciles')).toContainText(lang === 'ar' ? 'يطابق' : 'add up');
      await snap(page, 'ess-my-deductions', lang, info.project.name);
      expect(errors).toEqual([]);
    });

    test('Loan form: an instalment above 10% asks HR for the signed consent before submitting', async ({ page }, info) => {
      let created: Record<string, unknown> | null = null;
      const errors = await boot(page, lang, HR, (p, method, _q, body) => {
        if (p === '/api/finance/loans/types') return [LOAN_TYPE];
        if (p === '/api/finance/loans/types/offered') return [{ loanTypeId: LOAN_TYPE.id, code: LOAN_TYPE.code, nameEn: LOAN_TYPE.nameEn, nameAr: 'قرض شخصي', gradeLimited: false, offered: true, reasonCode: null, reasonText: null }];
        if (p === '/api/finance/loans' && method === 'GET') return { items: [], total: 0 };
        if (p === '/api/finance/loans/eligibility') return ELIGIBILITY;
        if (p === '/api/employees') return { items: [employeeRow(EMPLOYEE.employeeId)], total: 1, page: 1, pageSize: 8 };
        if (p === `/api/employees/${EMPLOYEE.employeeId}/documents` && method === 'POST')
          return { id: 'consent-doc-1', employeeId: EMPLOYEE.employeeId, documentType: 'LoanDeductionConsent', fileName: 'consent.pdf', contentType: 'application/pdf', storageUrl: '', isRequired: false };
        if (p === '/api/finance/loans' && method === 'POST') { created = body as Record<string, unknown>; return { id: 'loan-9', status: 'Pending' }; }
        if (p.endsWith('/audit')) return {};
        if (p.endsWith('/bonuses/types')) return [];
        return undefined;
      });
      await page.goto('/loans');
      await page.getByRole('button', { name: lang === 'ar' ? /طلب قرض جديد|New Loan Request/ : 'New Loan Request' }).click();
      const dialog = page.getByRole('dialog');
      await dialog.locator('input:not([type]), input[type="text"], input[type="search"]').first().fill('Moh');
      await dialog.getByRole('button', { name: new RegExp(FIXTURE.slipWithLoans.employeeName) }).click();
      // Amount, instalments, repayment method — by position, so the same steps run in both languages.
      await dialog.locator('input[type=number]').nth(0).fill('12000');
      await dialog.locator('input[type=number]').nth(1).fill('6');
      await dialog.locator('select').filter({ has: page.locator('option[value="PayrollDeduction"]') }).selectOption('PayrollDeduction');
      await dialog.getByRole('button', { name: lang === 'ar' ? /التحقق من الأهلية|Check Eligibility/ : 'Check Eligibility' }).click();
      const step = dialog.getByTestId('loan-consent-step');
      await expect(step).toBeVisible();
      await expect(step).toContainText('20%');
      const submit = page.getByRole('button', { name: lang === 'ar' ? /إرسال الطلب|Submit Request/ : 'Submit Request', exact: true });
      await expect(submit).toBeDisabled();
      await snap(page, 'loan-consent-step', lang, info.project.name);
      await step.locator('input[type=file]').setInputFiles({ name: 'consent.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4 consent') });
      await expect(step).toContainText(lang === 'ar' ? 'تم إرفاق الموافقة الموقّعة' : 'Signed consent attached');
      await expect(submit).toBeEnabled();
      await snap(page, 'loan-consent-attached', lang, info.project.name);
      await submit.click();
      await expect.poll(() => created?.consentDocumentId).toBe('consent-doc-1');
      expect(errors).toEqual([]);
    });

    test('Pending loan: the signed consent is attached so the approver can approve it', async ({ page }, info) => {
      let attached = false;
      const pending = { ...PENDING_LOAN };
      const errors = await boot(page, lang, HR, (p, method) => {
        if (p === '/api/finance/loans/types') return [LOAN_TYPE];
        if (p === '/api/finance/loans' && method === 'GET') return { items: [{ ...pending, consentOnFile: attached }], total: 1 };
        if (p === `/api/finance/loans/${pending.id}`) return {
          loan: { ...pending, consentOnFile: attached }, installments: [], repayments: [], glEntries: [], auditLogs: [],
          approvals: [{ id: 'a-1', loanId: pending.id, stepOrder: 1, approverRole: 'HR Manager', status: 'Pending', approvedByName: '', comments: '' }],
        };
        if (p === `/api/finance/loans/${pending.id}/consent` && method === 'POST') { attached = true; return { ...pending, consentOnFile: true }; }
        if (p.endsWith('/changes') || p.endsWith('/corrections') || p.endsWith('/bonuses/types')) return [];
        if (p.endsWith('/audit')) return {};
        return undefined;
      });
      await page.goto('/loans');
      await page.getByRole('button', { name: `View loan ${pending.loanNumber}` }).click();
      const attach = page.getByRole('dialog').getByTestId('loan-consent-attach');
      await expect(attach).toBeVisible();
      await snap(page, 'loan-pending-consent-attach', lang, info.project.name);
      await attach.locator('input[type=file]').setInputFiles({ name: 'consent.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4 consent') });
      await expect(page.getByRole('dialog').getByTestId('loan-consent-on-file')).toBeVisible();
      expect(attached).toBe(true);
      await snap(page, 'loan-pending-consent-on-file', lang, info.project.name);
      expect(errors).toEqual([]);
    });
  });
}

const PENDING_LOAN = {
  id: 'loan-7', employeeId: 'emp-7', employeeName: FIXTURE.slipWithLoans.employeeName, loanTypeId: 'type-1', loanTypeName: 'Personal',
  loanNumber: 'LN-2026-PENDING', requestedAmount: 12000, approvedAmount: 0, requestedInstallments: 6, approvedInstallments: 0,
  installmentAmount: 0, repaymentFrequency: 'Monthly', repaymentMethod: 'PayrollDeduction', currency: 'SAR', totalRepaid: 0,
  outstandingBalance: 0, status: 'Pending', notes: '', isLockedByPayroll: false, createdAtUtc: '2026-10-01T00:00:00Z',
  policyVersion: 1, collectionStatus: 'Normal', reviewRequired: false, consentOnFile: false,
};

const LOAN_TYPE = { id: 'type-1', nameEn: 'Personal', code: 'PERSONAL', isInterestFree: true, interestRate: 0, isActive: true, maxAmount: 50000, maxInstallments: 24, repaymentFrequency: 'Monthly', minServiceMonths: 0 };
const ELIGIBILITY = {
  eligible: true, reasons: [], codes: [], maxAvailableAmount: 50000, policyVersion: 1, monthlySalary: 10000, committedAmount: 0, currency: 'SAR',
  canRequestException: false, preview: false, available: 50000, bindingLimit: 'PolicyMaxAmount', limitBreakdowns: [], gradeLimit: null,
  art92: { instalment: 2000, wageDue: 10000, pct: 20, requiresConsent: true, deductedFromPay: true, thresholdPercent: 10 },
};

function employeeRow(id: number) {
  return {
    id, publicId: '00000000-0000-0000-0000-000000000007', employeeCode: 'MS-1', fullName: FIXTURE.slipWithLoans.employeeName,
    arabicName: 'محمد القحطاني', department: 'Operations', designation: 'Supervisor', branch: 'Riyadh', status: 'Active', companyId: COMPANY.id,
    profileCompletenessScore: 100, iqamaNumber: '', readinessState: 'Ready', activationBlockersCount: 0,
  };
}

function employeeDetail(id: number) {
  return {
    ...employeeRow(id), publicId: '00000000-0000-0000-0000-000000000007', firstName: 'Mohammed', lastName: 'Al-Qahtani', countryCode: 'SA',
    nationality: 'Saudi', joiningDate: '2022-01-01', workEmail: 'mohammed@masar.test', personalEmail: '', phone: '', gender: 'Male',
    complianceRecords: [], documents: [], history: [], transfers: [], dependents: [], emergencyContacts: [], customFields: {},
  };
}
