import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { payrollUx } from '../src/i18n/payrollUx';
import { boot, HANDLED, PERMS, USER, type Handler, type Locale } from './demo-polish.fixture';

/**
 * Payroll → Bank / WPS Files and Payment Tracking, the UX waves (W2 reasons, W4 page size, W5 bank-file steps, W6 confirm before a
 * GL post, W7 names, W8 lock wording). Every API request is intercepted: this proves the UI contract (states, wiring, wording, EN and
 * AR), not authorisation or the real payloads.
 * Run (dev server on its own port and dist dir):  DEMO_POLISH_PORT=5174 npx playwright test -c e2e/playwright.demo-polish.config.ts payroll-ux-waves
 * SHOT_DIR=<dir> SHOT_PREFIX=before|after also saves the review screenshots of the Bank / WPS tab.
 */

const LOCALES: Locale[] = ['en', 'ar'];
const RUN_ID = '11111111-1111-4111-8111-111111111111';
const BATCH_ID = 'b1';
const COMPANY_ROW = { id: 'c1', name: 'Acme Arabia', legalNameEn: 'Acme Arabia LLC', tradeName: 'Acme Arabia', defaultCurrency: 'SAR', countryCode: 'SA', isActive: true };
const RUN = {
  id: RUN_ID, companyId: 'c1', year: 2026, month: 11, status: 'Locked', runType: 'Regular', totalGrossSalary: 144856.25, totalDeductions: 11267.35, totalNetSalary: 133588.9,
  totalEmployerStatutoryCost: 10206.25, employeeCount: 9, createdAtUtc: '2026-11-14T00:50:00Z', processedAtUtc: '2026-11-14T00:51:00Z', lockedAtUtc: '2026-11-14T00:53:00Z',
};
const BATCH = {
  id: BATCH_ID, payrollRunId: RUN_ID, batchNumber: 'PAY-202611-220353', paymentMethod: 'WPS', totalAmount: 133588.9, currency: 'SAR', status: 'Created', wpsStatus: 'Draft',
  effectiveWpsStatus: 'Draft', wpsStatusLabel: 'Draft', wpsStatusChangedAtUtc: null, wpsSubmissionReference: null, wpsRejectionReason: null, createdAtUtc: '2026-11-14T01:05:00Z',
  acceptanceEvidenceCount: 0, isSaudi: true, generatedFileLabel: null,
};
const NAMES = ['Maha Almutairi', 'Layla Al-Harbi', 'Fahad Alharbi', 'Nora Alharbi', 'Khalid Alotaibi', 'Sara Alzahrani', 'Omar Aldosari', 'Huda Alqahtani', 'Yousef Alshehri'];
const RECORDS = NAMES.map((name, i) => ({
  id: `r${i + 1}`, paymentBatchId: BATCH_ID, employeeId: 200 + i, employeeName: name, employeeCode: `EVT2-${String(i + 1).padStart(4, '0')}`, amount: 12000 + i * 350,
  iban: `SA03800000006080101675${10 + i}`, wpsReference: `REF-${i + 1}`, status: 'Pending',
}));
const SETTINGS = {
  formatId: 'anb-connect-csv-v1', molEstablishmentId: '7-1234567', mainAccountNumber: '0108061198800026', organizationName: 'Acme Arabia', organizationAddress1: 'Riyadh',
  organizationAddress2: 'Olaya', organizationAddress3: 'Building 10', companyName: 'Acme Arabia LLC', narrative: 'November payroll', batchType: 'PAYROLL', autoWpsUpload: false,
  nationalUnifiedNo: '', debitAccountIban: '',
};
const FORMATS = [{ id: 'anb-connect-csv-v1', name: 'ANB Connect payroll payment — header.csv + body.csv', bank: 'Arab National Bank (ANB)', channel: 'ANB Connect API channel; not corporate portal WPY',
  sourceUrl: 'https://connect.anb.com.sa/apis/api/payroll-payment', reviewedOn: '2026-09-26', acceptanceStatus: 'specification-implemented; bank-acceptance-not-verified' }];

type Opts = {
  perms?: string[];
  roles?: string[];
  availability?: { enabled: boolean } | 'fail' | (() => { enabled: boolean } | 'fail');
  batches?: unknown[] | 'fail' | 'forbidden';
  records?: unknown[] | 'fail' | 'forbidden';
  /** Delay (ms) before the batch list answers, to look at the loading state. */
  slowBatches?: number;
  posts?: string[];
  extra?: Handler;
};

const bankHandler = (o: Opts = {}): Handler => (path, search, method, route) => {
  if (o.extra) { const x = o.extra(path, search, method, route); if (x !== undefined) return x; }
  if (path === '/api/auth/me') return { ...USER, roles: o.roles ?? USER.roles, permissions: o.perms ?? [...PERMS, 'payroll.export', 'payroll.structure_manage'] };
  if (method !== 'GET') o.posts?.push(`${method} ${path}`);
  if (path === '/api/payroll/runs') return { items: [RUN], total: 1, page: 1, pageSize: 100 };
  if (path === '/api/payroll/companies') return [COMPANY_ROW];
  if (path === '/api/payroll/readiness') return { year: 2026, month: 11, companyId: null, completionPercent: 100, isReadyForProcessing: true, totalActiveEmployees: 9, employeesWithSalary: 9, salaryCoveragePercent: 100, validationErrors: 0, payrollRunStatus: 'Locked', steps: [] };
  if (path === '/api/payroll/overview') return { year: 2026, month: 11, totalCompanies: 1, totalActiveEmployees: 9, totalGrossPayroll: 144856.25, totalNetPayroll: 133588.9, totalValidationErrors: 0, totalPendingApprovals: 0, companies: [] };
  if (/^\/api\/payroll\/runs\/[^/]+\/processing$/.test(path)) return { runId: RUN_ID, jobId: null, status: 'Idle', processedEmployees: null, totalEmployees: null, startedAtUtc: null, finishedAtUtc: null, errorCode: null };
  if (/^\/api\/payroll\/runs\/[^/]+\/(validate|approvals)$/.test(path)) return [];
  if (/^\/api\/payroll\/runs\/[^/]+\/validation-overrides$/.test(path)) return { runId: RUN_ID, overrides: [], overridableCodes: [], nonOverridableCodes: [] };
  if (path === '/api/payroll/payment-batches') {
    if (o.batches === 'fail') return (void route.fulfill({ status: 500, json: { message: 'boom' } }), HANDLED);
    if (o.batches === 'forbidden') return (void route.fulfill({ status: 403, json: {} }), HANDLED);
    const all = (o.batches ?? [BATCH]) as { payrollRunId: string }[];
    const run = search.get('runId');
    const body = run ? all.filter(b => b.payrollRunId === run) : all;
    if (o.slowBatches) { setTimeout(() => void route.fulfill({ json: body }).catch(() => undefined), o.slowBatches); return HANDLED; }
    return body;
  }
  if (path === `/api/payroll/payment-batches/${BATCH_ID}/records`) {
    if (o.records === 'fail') return (void route.fulfill({ status: 500, json: { message: 'boom' } }), HANDLED);
    if (o.records === 'forbidden') return (void route.fulfill({ status: 403, json: {} }), HANDLED);
    return o.records ?? RECORDS;
  }
  const bp = '/api/payroll/bank-exports';
  if (path === `${bp}/batches/${BATCH_ID}/availability`) {
    const a = typeof o.availability === 'function' ? o.availability() : o.availability;
    if (a === 'fail') return (void route.fulfill({ status: 500, json: { message: 'boom' } }), HANDLED);
    return a ?? { enabled: true };
  }
  if (path === `${bp}/formats`) return FORMATS;
  if (path === `${bp}/companies/c1/settings`) return SETTINGS;
  if (path === `${bp}/companies/c1/templates`) return [];
  if (path === `${bp}/batches/${BATCH_ID}/context`) return { companyId: 'c1', companyName: 'Acme Arabia', countryCode: 'SA', runStatus: 'Locked', existingExport: null };
  if (path === `${bp}/batches/${BATCH_ID}/validate`) return { canExport: true, errors: [], warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' };
  return undefined;
};

const say = (locale: Locale, key: string) => (locale === 'ar' ? payrollUx.ar[key] ?? `MISSING ${key}` : key);
const url = (tab: string) => `/payroll?tab=${tab}&run=${RUN_ID}`;
const BANK_WORDS: Record<Locale, { tab: string }> = { en: { tab: 'Bank / WPS Files' }, ar: { tab: 'ملفات البنك' } };
void BANK_WORDS;

async function shot(page: Page, name: string, locale: Locale, form: string) {
  const dir = process.env.SHOT_DIR;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/${process.env.SHOT_PREFIX ?? 'after'}-${name}-${locale}-${form}.png`, fullPage: true });
}

// ── Review screenshots: the Bank / WPS tab as a finance user meets it ──────────────────────────────

for (const locale of LOCALES) {
  for (const [form, viewport] of [['desktop', { width: 1366, height: 768 }], ['phone', { width: 390, height: 844 }]] as const) {
    test.describe(`shots: Bank / WPS tab (${locale}, ${form})`, () => {
      test.use({ viewport });
      test('the tab with a Saudi batch open', async ({ page }) => {
        await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500 });
        await expect(page.getByRole('cell', { name: /Maha Almutairi/ })).toBeVisible({ timeout: 90_000 });
        await page.waitForTimeout(2500);
        await shot(page, 'bank-wps', locale, form);
      });
    });
  }
}

// ── W2: say why something is missing ───────────────────────────────────────────────────────────────

const FINANCE_PERMS = PERMS.filter(p => !p.startsWith('payroll.export'));

for (const locale of LOCALES) {
  test.describe(`W2: reasons instead of nothing (${locale})`, () => {
    test('without the export permission the bank file panel says so and what to do', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ perms: FINANCE_PERMS, roles: ['Finance Approver'], records: 'forbidden' }), waitMs: 2500 });
      const note = page.getByTestId('bank-file-no-permission');
      await expect(note).toBeVisible({ timeout: 90_000 });
      await expect(note).toContainText(say(locale, 'You cannot prepare the bank file'));
      await expect(note).toContainText(say(locale, 'Preparing the Saudi bank file needs the Payroll export permission. Ask an administrator to give it to your role, then come back to this batch.'));
      await expect(page.getByRole('heading', { name: /ANB Connect/ })).toHaveCount(0);
      // The records the user may not read are not "No records."
      await expect(page.getByTestId('bank-records-forbidden')).toContainText(say(locale, 'The payment records need the Payroll export permission. Ask an administrator to give it to your role.'));
      await expect(page.getByText(/^(No records\.|لا توجد سجلات.?)$/)).toHaveCount(0);
    });

    test('with the switch off the panel names the switch and who turns it on', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ availability: { enabled: false } }), waitMs: 2500 });
      const note = page.getByTestId('bank-file-off');
      await expect(note).toBeVisible({ timeout: 90_000 });
      await expect(note).toContainText(say(locale, 'Bank file export is switched off for this company'));
      await expect(note).toContainText(say(locale, 'The platform team switches bank file export on for each company. Ask them to turn it on, then reload this batch.'));
    });

    test('a failed availability check says so, and Retry brings the panel', async ({ page }) => {
      let calls = 0;
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ availability: () => (++calls === 1 ? 'fail' : { enabled: true }) }), waitMs: 2500 });
      const note = page.getByTestId('bank-file-check-failed');
      await expect(note).toBeVisible({ timeout: 90_000 });
      await expect(note).toContainText(say(locale, 'Could not check whether the bank file can be prepared'));
      await note.getByRole('button').click();
      await expect(page.getByRole('heading', { name: /ANB Connect/ }).first()).toBeVisible({ timeout: 30_000 });
      await expect(note).toHaveCount(0);
    });

    test('while the batches load there is a skeleton, not "No batches yet."', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ slowBatches: 4000 }), waitMs: 1500 });
      const panel = page.getByRole('tabpanel');
      await expect(panel.locator('.kx-skeleton').first()).toBeVisible({ timeout: 90_000 });
      await expect(panel.getByText(/^(No batches yet\.|لا توجد دفعات بعد.?)$/)).toHaveCount(0);
      await expect(page.getByRole('button', { name: /PAY-202611-220353/ })).toBeVisible({ timeout: 30_000 });
    });

    test('a failed batch list says it failed, never "No batches yet."', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ batches: 'fail' }), waitMs: 2500 });
      await expect(page.getByRole('tabpanel').getByTestId('list-load-failed')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByText(/^(No batches yet\.|لا توجد دفعات بعد.?)$/)).toHaveCount(0);
    });

    test('a failed records read says it failed, never "No records."', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ records: 'fail' }), waitMs: 2500 });
      await expect(page.getByRole('tabpanel').getByTestId('list-load-failed')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByText(/^(No records\.|لا توجد سجلات.?)$/)).toHaveCount(0);
    });

    test('Payment Tracking: a failed list is not "No payment batches yet"', async ({ page }) => {
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler({ batches: 'fail' }), waitMs: 2500 });
      await expect(page.getByRole('tabpanel').getByTestId('list-load-failed')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByText(/^(No payment batches yet|لا توجد دفعات دفع بعد)$/)).toHaveCount(0);
    });
  });
}
