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
  stages?: 'open' | 'folded' | 'default';
  /** The check's answer (default: passes). */
  validation?: { canExport: boolean; errors: { code: string; message: string; employeeId?: number; field?: string }[] };
  runStatus?: string;
};

const bankHandler = (o: Opts = {}): Handler => (path, search, method, route) => {
  if (method !== 'GET') o.posts?.push(`${method} ${path}`);
  if (o.extra) { const x = o.extra(path, search, method, route); if (x !== undefined) return x; }
  if (path === '/api/auth/me') return { ...USER, roles: o.roles ?? USER.roles, permissions: o.perms ?? [...PERMS, 'payroll.export', 'payroll.structure_manage'] };
  if (path === '/api/payroll/runs') return { items: [{ ...RUN, status: o.runStatus ?? RUN.status }], total: 1, page: 1, pageSize: 100 };
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
  if (path === `${bp}/batches/${BATCH_ID}/validate`) return { canExport: true, errors: [], warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1', ...(o.validation ?? {}) };
  if (path === `${bp}/batches/${BATCH_ID}/generate`) {
    const body = JSON.parse(route.request().postData() ?? '{}') as { batchReference: string; paymentDate: string };
    return { id: '44444444-4444-4444-8444-444444444444', formatId: 'anb-connect-csv-v1', files: [{ name: 'header.csv', sha256: 'a'.repeat(64) }, { name: 'body.csv', sha256: 'b'.repeat(64) }],
      batchReference: body.batchReference, paymentDate: body.paymentDate, employeeCount: 9, totalAmount: 133588.9, downloadUrl: `${bp}/batches/${BATCH_ID}/download` };
  }
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

// ── W4: shrink the page ───────────────────────────────────────────────────────────────────────────

const MANY = Array.from({ length: 40 }, (_, i) => ({
  id: `m${i}`, paymentBatchId: BATCH_ID, employeeId: 300 + i, employeeName: `Employee Number ${i + 1}`, employeeCode: `EVT2-${String(100 + i)}`, amount: 9000 + i,
  iban: `SA03800000006080101675${i}`, wpsReference: `REF-M${i}`, status: 'Pending',
}));

for (const locale of LOCALES) {
  test.describe(`W4: a smaller page (${locale})`, () => {
    test.use({ viewport: { width: 1366, height: 768 } });

    test('the stage cards start folded once a run is chosen, the next step stays, and the choice is remembered', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500, stages: 'default' });
      const toggle = page.getByTestId('pipeline-stages-toggle');
      await expect(toggle).toBeVisible({ timeout: 90_000 });
      await expect(toggle).toHaveAttribute('aria-expanded', 'false');
      await expect(page.getByTestId('pipeline-stages')).toBeHidden();
      await expect(page.getByTestId('pipeline-next-action')).toBeVisible();
      await expect(page.getByTestId('pipeline-folded-stage')).toBeVisible();
      await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await expect(page.getByTestId('pipeline-stages')).toBeVisible();
      await page.reload();
      await expect(page.getByTestId('pipeline-stages-toggle')).toHaveAttribute('aria-expanded', 'true', { timeout: 90_000 });
      await page.getByTestId('pipeline-stages-toggle').click();
      await page.reload();
      await expect(page.getByTestId('pipeline-stages-toggle')).toHaveAttribute('aria-expanded', 'false', { timeout: 90_000 });
    });

    test('with no run chosen the pipeline stays open (runs needing attention are the content)', async ({ page }) => {
      await boot(page, { locale, url: '/payroll?tab=dashboard', routes: bankHandler(), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('pipeline-attention')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByTestId('pipeline-stages-toggle')).toHaveCount(0);
    });

    test('the sixteen tabs are five labelled groups and every tab is reachable without scrolling the strip', async ({ page }) => {
      await boot(page, { locale, url: url('dashboard'), routes: bankHandler(), waitMs: 2500 });
      const tabs = page.getByRole('tab');
      await expect(tabs).toHaveCount(16, { timeout: 90_000 });
      await expect(page.locator('[data-tab-group]')).toHaveCount(5);
      const boxes = await tabs.evaluateAll(els => els.map(e => { const r = e.getBoundingClientRect(); return { l: r.left, r: r.right, w: r.width }; }));
      for (const b of boxes) { expect(b.w).toBeGreaterThan(0); expect(b.l).toBeGreaterThanOrEqual(0); expect(b.r).toBeLessThanOrEqual(1366); }
      const strip = page.locator('[data-roving-tabs="payroll"]');
      expect(await strip.evaluate(el => el.scrollWidth - el.clientWidth)).toBeLessThanOrEqual(1);
      // Arrow keys still walk the tabs in order.
      // (From Bank / WPS to Payment Tracking: the next tab in reading order, which in Arabic is the one on the left.)
      await page.locator('#payroll-tab-bank-wps').focus();
      await page.keyboard.press(locale === 'ar' ? 'ArrowLeft' : 'ArrowRight');
      await expect(page.locator('#payroll-tab-payment-tracking')).toHaveAttribute('aria-selected', 'true');
    });

    test('changing tab does not move the page', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500 });
      await expect(page.locator('#payroll-tab-payment-tracking')).toBeVisible({ timeout: 90_000 });
      expect(await page.evaluate(() => window.scrollY)).toBe(0);
      await page.evaluate(() => window.scrollTo(0, 120));
      const before = await page.evaluate(() => window.scrollY);
      await page.locator('#payroll-tab-payment-tracking').click();
      await page.waitForTimeout(400);
      expect(Math.abs((await page.evaluate(() => window.scrollY)) - before)).toBeLessThanOrEqual(2);
    });

    test('the top bar does not cover an anchor, the assistant is the compact circle, and a long batch scrolls inside its own region', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ records: MANY }), waitMs: 2500 });
      const region = page.getByTestId('bank-records-scroll');
      await expect(region).toBeVisible({ timeout: 90_000 });
      await expect(region).toHaveAttribute('role', 'region');
      const dims = await region.evaluate(el => ({ sh: el.scrollHeight, ch: el.clientHeight }));
      expect(dims.sh).toBeGreaterThan(dims.ch);
      expect(await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).scrollPaddingTop))).toBeGreaterThanOrEqual(60);
      await expect(page.getByRole('button', { name: /Kody/ })).toHaveAttribute('data-compact', 'true');
    });
  });

  test.describe(`W4: on a phone (${locale})`, () => {
    test.use({ viewport: { width: 390, height: 844 } });

    test('a deep link does not scroll the page, and a wide table scrolls inside its card', async ({ page }) => {
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler(), waitMs: 2500 });
      await expect(page.locator('#payroll-tab-payment-tracking')).toBeVisible({ timeout: 90_000 });
      await page.waitForTimeout(500);
      expect(await page.evaluate(() => window.scrollY)).toBe(0);
      const card = page.getByRole('tabpanel').locator('.surface').filter({ has: page.locator('table') }).first();
      await expect(card).toBeVisible();
      const m = await card.evaluate(el => ({ ox: getComputedStyle(el).overflowX, sw: el.scrollWidth, cw: el.clientWidth }));
      expect(m.ox).toBe('auto');
      expect(m.sw).toBeGreaterThan(m.cw);
      // The page itself has no sideways scroll.
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1);
    });
  });
}

// ── W5: the bank file as four steps ───────────────────────────────────────────────────────────────

const step = (page: Page, key: string) => page.locator(`[data-testid="bank-file-steps"] [data-step="${key}"]`);
async function pickDate(page: Page, locale: Locale) {
  const group = page.getByRole('group', { name: locale === 'ar' ? 'تاريخ قيمة الإيداع' : 'Credit value date' });
  const selects = group.getByRole('combobox');
  await expect(selects).toHaveCount(3);
  for (let i = 0; i < 3; i++) {
    const sel = selects.nth(i);
    const values = await sel.locator('option').evaluateAll(els => els.map(e => (e as HTMLOptionElement).value));
    await sel.selectOption(values.includes('2026') ? '2026' : values.includes('31') ? '30' : '11');
  }
}

for (const locale of LOCALES) {
  test.describe(`W5: bank file steps (${locale})`, () => {
    test.use({ viewport: { width: 1366, height: 768 } });

    test('the steps follow the batch: check is current, then done once validated, then the file is made and the download step offers the next move', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500 });
      await expect(step(page, 'batch')).toHaveAttribute('data-state', 'done', { timeout: 90_000 });
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'current');
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'upcoming');
      await expect(step(page, 'create').getByTestId('bank-step-reason-create')).toContainText(say(locale, 'Waits for a successful check.'));
      await expect(step(page, 'download')).toHaveAttribute('data-state', 'upcoming');
      await expect(page.getByTestId('bank-file-steps').locator('[aria-current="step"]')).toHaveCount(1);

      await pickDate(page, locale);
      await page.getByRole('button', { name: locale === 'ar' ? 'تحقق' : 'Validate', exact: true }).click();
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'done');
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'current');
      await page.getByRole('button', { name: locale === 'ar' ? 'إنشاء الملفات' : 'Generate files', exact: true }).click();
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'done');
      await expect(step(page, 'download')).toHaveAttribute('data-state', 'current');
      await step(page, 'download').getByRole('button', { name: say(locale, 'Open Payment Tracking') }).click();
      await expect(page.locator('#payroll-tab-payment-tracking')).toHaveAttribute('aria-selected', 'true');
    });

    test('validation blockers block the check step and say where to fix them', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ validation: { canExport: false, errors: [{ code: 'iban_missing', message: 'IBAN is missing', employeeId: 203 }] } }), waitMs: 2500 });
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'current', { timeout: 90_000 });
      await pickDate(page, locale);
      await page.getByRole('button', { name: locale === 'ar' ? 'تحقق' : 'Validate', exact: true }).click();
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'blocked');
      await expect(step(page, 'check').getByTestId('bank-step-reason-check')).toContainText(say(locale, 'The check found blockers. Fix them in the bank file section, then check again.'));
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'upcoming');
    });

    test('a closed gate blocks step 2 with its reason (switch off, no permission)', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ availability: { enabled: false } }), waitMs: 2500 });
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'blocked', { timeout: 90_000 });
      await expect(step(page, 'check').getByTestId('bank-step-reason-check')).toContainText(say(locale, 'Bank file export is switched off for this company. Ask the platform team.'));
    });

    test('a run that is not locked and has no batch: step 1 is blocked, with the reason', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ batches: [], runStatus: 'Approved' }), waitMs: 2500 });
      await expect(step(page, 'batch')).toHaveAttribute('data-state', 'blocked', { timeout: 90_000 });
      await expect(step(page, 'batch').getByTestId('bank-step-reason-batch')).toContainText(say(locale, 'Lock the run first: a payment batch can only be created once the run is Locked.'));
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'upcoming');
    });

    test('the bank batch number is suggested from the payment batch number, stays editable, and says where it came from', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500 });
      const input = page.getByLabel(locale === 'ar' ? 'رقم الدفعة' : 'Batch number', { exact: true });
      await expect(input).toHaveValue('202611220353', { timeout: 90_000 });
      await expect(page.getByText(locale === 'ar' ? 'مقترح من رقم دفعة الدفع' : 'Suggested from the payment batch number', { exact: false })).toBeVisible();
      await input.fill('202611999');
      await expect(input).toHaveValue('202611999');
      await expect(page.getByText(locale === 'ar' ? 'مقترح من رقم دفعة الدفع' : 'Suggested from the payment batch number', { exact: false })).toHaveCount(0);
      // The suggestion is only a starting value: the request carries what is in the box.
      await pickDate(page, locale);
      const validate = page.waitForRequest(r => r.url().endsWith('/validate'));
      await page.getByRole('button', { name: locale === 'ar' ? 'تحقق' : 'Validate', exact: true }).click();
      expect((await validate).postDataJSON()).toMatchObject({ batchReference: '202611999' });
    });

    test('the hedging sentence "if it does not appear" is gone: the reason is named where the panel would be', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ perms: FINANCE_PERMS, roles: ['Finance Approver'] }), waitMs: 2500 });
      await expect(page.getByTestId('bank-file-no-permission')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByText(/If it does not, you need|وإن لم يظهر/)).toHaveCount(0);
    });
  });
}

// ── W6: say what will post to the GL before it posts ──────────────────────────────────────────────

const ACCEPTED = { ...BATCH, status: 'FileGenerated', wpsStatus: 'Accepted', effectiveWpsStatus: 'Accepted', wpsStatusLabel: 'Accepted', allowedNextStatuses: [] };
const PAID = { ...ACCEPTED, wpsStatus: 'Paid', effectiveWpsStatus: 'Paid', wpsStatusLabel: 'Paid' };

for (const locale of LOCALES) {
  test.describe(`W6: confirm before a GL posting (${locale})`, () => {
    test.use({ viewport: { width: 1440, height: 900 } });
    const dialog = (page: Page) => page.getByRole('dialog');

    test('Settle net pay names the batch, journal, amount and date; Cancel posts nothing; confirming posts once', async ({ page }) => {
      const posts: string[] = [];
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler({ batches: [ACCEPTED], posts, extra: (p, _s, m) => (m === 'POST' && p.endsWith('/settle') ? { batchId: BATCH_ID, runId: RUN_ID, wpsStatus: 'Paid', settled: 9 } : undefined) }), waitMs: 2500 });
      const settle = page.getByRole('button', { name: locale === 'ar' ? /تسوية صافي الرواتب في دفتر الأستاذ/ : 'Settle net pay to GL' }).first();
      await expect(settle).toBeVisible({ timeout: 90_000 });
      await settle.click();
      const d = dialog(page);
      await expect(d).toBeVisible();
      await expect(d).toContainText(say(locale, 'Post the net pay settlement to the general ledger?'));
      const facts = d.getByTestId('gl-post-confirm');
      await expect(facts).toContainText('PAY-202611-220353');
      await expect(facts).toContainText('133,588.90');
      await expect(facts).toContainText(say(locale, 'Payment batch'));
      await expect(facts).toContainText(say(locale, 'Journal'));
      expect(posts.filter(x => x.endsWith('/settle'))).toHaveLength(0);
      await d.getByRole('button', { name: locale === 'ar' ? 'إلغاء' : 'Cancel' }).click();
      await expect(d).toHaveCount(0);
      expect(posts.filter(x => x.endsWith('/settle'))).toHaveLength(0);
      await settle.click();
      await d.getByRole('button', { name: say(locale, 'Post settlement') }).click();
      await expect.poll(() => posts.filter(x => x.endsWith('/settle')).length).toBe(1);
    });

    test('Remit names the group and says the amounts appear on the GL Journal tab', async ({ page }) => {
      const posts: string[] = [];
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler({ batches: [PAID], posts }), waitMs: 2500 });
      const remit = page.getByRole('button', { name: locale === 'ar' ? 'تحويل' : 'Remit', exact: true }).first();
      await expect(remit).toBeVisible({ timeout: 90_000 });
      await remit.click();
      const d = dialog(page);
      await expect(d).toContainText(say(locale, 'Post the statutory remittance to the general ledger?'));
      await expect(d.getByTestId('gl-post-confirm')).toContainText(say(locale, 'GOSI, tax and loan remittances'));
      expect(posts.filter(x => x.endsWith('/remit'))).toHaveLength(0);
      await d.getByRole('button', { name: locale === 'ar' ? 'إلغاء' : 'Cancel' }).click();
      expect(posts.filter(x => x.endsWith('/remit'))).toHaveLength(0);
    });
  });
}
