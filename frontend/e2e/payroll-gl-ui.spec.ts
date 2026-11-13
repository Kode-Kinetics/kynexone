import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Request, type TestInfo } from '@playwright/test';

/**
 * Payroll screens a finance audience reads after a run has posted, settled and remitted: the GL Journal tab (one journal per event, cost
 * centres from what was posted), the Void dialog (the cash elections the server demands), the permission message on payment actions,
 * a Voided run's card, and one payment status. Every API request is intercepted (no backend, no real tenant), so this proves the UI
 * contract (states, wiring, wording, EN and AR), not authorisation.
 * Run it with the cost-centre journal config (it matches this spec too), against a dev server started with a private NEXT_DIST_DIR:
 *   PAYROLL_CC_BASE_URL=http://127.0.0.1:<port> npx playwright test -c e2e/playwright.payroll-cost-centre-journal.config.ts payroll-gl-ui
 * GLUI_SHOTS_DIR, when set, also receives the review screenshots.
 */

const company = { id: 'co-sa', name: 'Masar Trading KSA', tradeName: 'Masar KSA', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' };

const run = (over: Record<string, unknown>) => ({
  id: 'run-nov', companyId: 'co-sa', year: 2026, month: 11, status: 'Locked', runType: 'Regular', totalGrossSalary: 38110.83, totalDeductions: 7435.41,
  totalNetSalary: 30675.42, totalEmployerStatutoryCost: 0, employeeCount: 3, createdAtUtc: '2026-11-12T08:00:00Z', processedAtUtc: '2026-11-12T08:10:00Z',
  lockedAtUtc: '2026-11-12T08:30:00Z', ...over,
});

interface Options {
  locale?: 'en' | 'ar';
  /** payroll.export (Admin only). Default true. */
  canExport?: boolean;
  /** The posted lines carry cost centres. Default true. */
  costCentres?: boolean;
  /** The server predates the posted-journals list. */
  legacyJournal?: boolean;
  batches?: unknown[];
  /** The next create-batch POST is refused with an empty 403. */
  createRefused?: boolean;
  /** Void is refused with the elections 422 until they are sent. */
  voidNeedsElections?: boolean;
}

interface Recorded { method: string; path: string; search: string; body: unknown }

const line = (entryType: 'DR' | 'CR', glAccount: string, glAccountName: string, amount: number, cc?: [string, string, string | null]) => ({
  componentCode: 'BASIC', componentName: glAccountName, glAccount, glAccountName, entryType, amount,
  costCentreCode: cc?.[0] ?? null, costCentreName: cc?.[1] ?? null, costCentreErpCode: cc?.[2] ?? null,
});

const journalBlock = (kind: string, group: string | null, entries: ReturnType<typeof line>[], by: string, date: string) => {
  const dr = entries.filter(e => e.entryType === 'DR').reduce((s, e) => s + e.amount, 0);
  const cr = entries.filter(e => e.entryType === 'CR').reduce((s, e) => s + e.amount, 0);
  return {
    kind, group, eventType: kind, period: '2026-11', entryDate: date, postedAtUtc: `${date}T09:00:00Z`, postedByName: by, isReversed: false,
    hasCostCentres: entries.some(e => e.costCentreCode), entries, totalDebits: Math.round(dr * 100) / 100, totalCredits: Math.round(cr * 100) / 100,
    isBalanced: Math.abs(dr - cr) < 0.01,
  };
};

function postedJournal(options: Options) {
  const cc = options.costCentres !== false;
  const accrual = journalBlock('accrual', null, [
    line('DR', '5001', 'Basic Salary Expense', 26000, cc ? ['CC-ENG', 'Engineering', 'ENG-01'] : undefined),
    line('DR', '5002', 'Housing Allowance Expense', 12110.83, cc ? ['CC-OPS', 'Operations', null] : undefined),
    line('CR', '2100', 'Salaries Payable', 30675.42),
    line('CR', '2101', 'Social Insurance Payable (Employee)', 7435.41),
  ], 'Layla Hamdan', '2026-11-12');
  const settlement = journalBlock('settlement', null, [
    line('DR', '2100', 'Salaries Payable', 30675.42), line('CR', '1000', 'Cash/Bank', 30675.42),
  ], 'Omar Finance', '2026-11-12');
  const gosi = journalBlock('remittance', 'GOSI', [
    line('DR', '2101', 'Social Insurance Payable (Employee)', 7435.41), line('CR', '1000', 'Cash/Bank', 7435.41),
  ], 'Omar Finance', '2026-11-13');
  const loan = journalBlock('remittance', 'LOAN', [
    line('DR', '2107', 'Loan & Advance Deductions Payable', 666.66), line('CR', '1000', 'Cash/Bank', 666.66),
  ], 'Omar Finance', '2026-11-13');
  return {
    runId: 'run-nov', period: '2026-11', currency: 'SAR', isPosted: true, entries: accrual.entries,
    costing: { enabled: false, postedWithCostCentres: cc, blockers: [], groups: [], roundingBlocked: false, fallbackEmployees: 0, fallbackAmount: 0, uncostedEmployees: 0, uncostedAmount: 0 },
    totalDebits: accrual.totalDebits, totalCredits: accrual.totalCredits, isBalanced: true,
    ...(options.legacyJournal ? {} : { journals: [accrual, settlement, gosi, loan] }),
    catchAllComponents: [], blockingProblems: [], roundingAdjustment: 0,
  };
}

const elections = [
  { code: 'remittance_disposition_required', field: 'remittanceDisposition', eventType: 'PayrollRemit:GOSI', amount: 7435.41, cashAccount: '1000 - Cash/Bank', period: '2026-11', referenceField: 'remittanceReference', referenceRequiredIfRecalled: true, message: 'This run has already DISBURSED the GOSI remittance.' },
  { code: 'remittance_disposition_required', field: 'remittanceDisposition', eventType: 'PayrollRemit:LOAN', amount: 666.66, cashAccount: '1000 - Cash/Bank', period: '2026-11', referenceField: 'remittanceReference', referenceRequiredIfRecalled: true, message: 'This run has already DISBURSED the LOAN remittance.' },
  { code: 'settlement_disposition_required', field: 'settlementDisposition', eventType: 'PayrollNetSettlement', amount: 30675.42, cashAccount: '1000 - Cash/Bank', period: '2026-11', referenceField: 'settlementReference', referenceRequiredIfRecalled: true, message: 'This run has already DISBURSED net pay.' },
];

const batch = (over: Record<string, unknown>) => ({
  id: 'b1', payrollRunId: 'run-nov', batchNumber: 'PAY-202611-193005', paymentMethod: 'WPS', totalAmount: 30675.42, currency: 'SAR',
  status: 'FileGenerated', wpsStatus: 'Paid', effectiveWpsStatus: 'Paid', wpsStatusLabel: 'Paid', allowedNextStatuses: [], isSaudi: true,
  createdAtUtc: '2026-11-12T08:35:00Z', wpsStatusChangedAtUtc: null, wpsSubmissionReference: null, wpsRejectionReason: null, ...over,
});

async function boot(page: Page, options: Options = {}, startTab = 'runs') {
  const writes: Recorded[] = [];
  const errors: string[] = [];
  const state = { voided: false, voidAttempts: 0 };
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'payroll-gl-ui-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });
  const canExport = options.canExport !== false;

  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => { try { return request.postDataJSON(); } catch { return request.postData(); } })();
    if (method !== 'GET') writes.push({ method, path: p, search: url.search, body });

    if (p === '/api/auth/me') {
      return reply({
        id: 'user-1', tenantId: 'glui-tenant', tenantSlug: 'glui-fixture', fullName: canExport ? 'Fatima Admin' : 'Pavel Payroll', roles: [canExport ? 'Admin' : 'Payroll Manager'],
        permissions: ['payroll.read', 'payroll.lock', 'payroll.approve', 'dashboard.read', 'finance.gl.read', ...(canExport ? ['payroll.export'] : [])],
        accountType: 'SingleCompany', isGroupScope: false,
        companies: [{ id: company.id, name: company.name, code: company.id, countryCode: 'SA', isActive: true }],
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/payroll/companies') return reply([company]);
    if (p === '/api/companies') return reply(paged([]));
    if (p === '/api/payroll/runs' && method === 'GET') {
      return reply(paged([
        run({ status: state.voided ? 'Voided' : 'Locked' }),
        run({ id: 'run-oct', month: 10, status: 'Voided', lockedAtUtc: null }),
      ]));
    }
    if (p === '/api/payroll/reports/summary') return reply({ totalRuns: 2, lockedRuns: 1, totalGrossYtd: 0, totalNetYtd: 0, ytdByCompany: [] });
    if (/^\/api\/payroll\/runs\/run-nov\/gl-journal$/.test(p)) return reply(postedJournal(options));
    if (p === '/api/payroll/runs/run-nov/void' && method === 'POST') {
      state.voidAttempts += 1;
      const q = url.searchParams;
      if (options.voidNeedsElections && !q.get('settlementDisposition')) {
        return reply({ error: 'remittance_disposition_required', message: 'This run has already DISBURSED the GOSI remittance (7,435.41 credited to 1000 - Cash/Bank).', detail: { requiredElections: elections, outstandingElectionCount: 3 } }, 422);
      }
      state.voided = true;
      return reply({ runId: 'run-nov', status: 'Voided' });
    }
    if (p === '/api/payroll/payment-batches' && method === 'GET') return reply(options.batches ?? []);
    if (/^\/api\/payroll\/payment-batches\/[^/]+\/records$/.test(p)) return reply([]);
    if (/^\/api\/payroll\/runs\/run-nov\/payment-batches$/.test(p) && method === 'POST') {
      if (options.createRefused) return route.fulfill({ status: 403, body: '' });
      return reply(batch({ status: 'Pending', wpsStatus: 'Draft', effectiveWpsStatus: 'Draft' }));
    }
    if (/^\/api\/payroll\/runs\/[^/]+\/(approvals|validation-overrides|population)$/.test(p)) return reply([]);
    if (p.startsWith('/api/finance/gl/journal-exports') || p.startsWith('/api/finance/gl/periods')) return reply([]);
    if (p.startsWith('/api/payroll/')) return reply({ message: 'Not part of this fixture.' }, 404);
    if (p.startsWith('/api/admin/') || p.includes('country-packs')) return reply([]);
    return reply(paged([]));
  });

  await page.goto('/payroll');
  await page.locator(`#payroll-tab-${startTab}`).click();
  await expect(page.locator(`#payroll-tab-${startTab}`)).toHaveAttribute('aria-selected', 'true', { timeout: 60_000 });
  return { writes, errors, state };
}

async function shot(page: Page, info: TestInfo, base: string) {
  const name = `${base}-${info.project.name}`;
  const file = info.outputPath(`${name}.png`);
  await page.screenshot({ path: file, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path: file, contentType: 'image/png' });
  const dir = process.env.GLUI_SHOTS_DIR;
  if (dir) {
    fs.mkdirSync(dir, { recursive: true });
    fs.copyFileSync(file, path.join(dir, `${name}.png`));
  }
}

const tid = (page: Page, id: string) => page.getByTestId(id);
const openJournal = async (page: Page) => {
  await page.getByLabel('Select payroll run for GL journal').selectOption('run-nov');
  await page.getByRole('button', { name: /Generate Journal|إنشاء/ }).click();
};
// Run cards show the month as the viewer's locale spells it ("November 2026", "نوفمبر 2026"), so match any of them.
const MONTHS: Record<string, string> = { Oct: 'Oct(?:ober)?|أكتوبر', Nov: 'Nov(?:ember)?|نوفمبر' };
const card = (page: Page, text: string) => {
  const [mon, year] = text.split(' ');
  return page.locator('[role="button"]', { hasText: new RegExp(`(?:${MONTHS[mon] ?? mon}) ${year}`) }).first();
};
// The void button's label is translated; the selects are found by the run they offer, not by their (translated) label.
const voidButton = 'button[aria-label="Void run"], button[aria-label="إلغاء المسير"]';
const runSelect = (page: Page) => page.locator('select:has(option[value="run-nov"])').first();

// ═════════════════════════════ M3: the GL Journal tab for a posted run ═════════════════════════════

test('a posted run: no "cost accounting is off" banner, the headline is the accrual, each journal is shown on its own', async ({ page }, info) => {
  await boot(page, {}, 'gl-journal');
  await openJournal(page);
  await expect(tid(page, 'gl-posted-journals')).toBeVisible();
  await expect(page.getByText('Cost accounting is off for this company')).toHaveCount(0);
  // The headline is the accrual journal alone.
  await expect(tid(page, 'gl-headline-debits')).toContainText('38,110.83');
  await expect(tid(page, 'gl-headline-credits')).toContainText('38,110.83');
  await expect(tid(page, 'gl-headline-debits')).not.toContainText('76,221.66');
  // One block per event, titled by it, each balanced, dated and attributed.
  for (const [id, title] of [
    ['accrual', 'Payroll accrual'], ['settlement', 'Net pay settlement'], ['remittance-GOSI', 'GOSI remittance'], ['remittance-LOAN', 'Loan remittance'],
  ] as const) {
    const block = tid(page, `gl-journal-${id}`);
    await expect(block).toContainText(title);
    await expect(block).toContainText('Balanced');
    await expect(block).toContainText('Entry date');
    await expect(block).toContainText('Posted by');
  }
  await expect(tid(page, 'gl-journal-settlement')).toContainText('Omar Finance');
  // 2100 is a debit in the settlement journal and a credit in the accrual: never both inside one journal.
  await expect(tid(page, 'gl-journal-accrual').locator('tr', { hasText: '2100' })).toHaveCount(1);
  await expect(tid(page, 'gl-journal-settlement').locator('tr', { hasText: '2100' })).toHaveCount(1);
  await expect(tid(page, 'gl-journal-settlement')).toContainText('30,675.42');
  await shot(page, info, 'glui-en-journal-posted');
});

test('the cost-centre column appears where lines carry one, with the ERP code when there is one', async ({ page }) => {
  await boot(page, {}, 'gl-journal');
  await openJournal(page);
  const accrual = tid(page, 'gl-journal-accrual');
  await expect(accrual.getByRole('columnheader', { name: 'Cost centre' })).toBeVisible();
  await expect(accrual).toContainText('CC-ENG');
  await expect(accrual).toContainText('Engineering');
  await expect(accrual).toContainText('ERP code: ENG-01');
  await expect(accrual).toContainText('CC-OPS');
  // The settlement journal has no cost centre on any line: no column.
  await expect(tid(page, 'gl-journal-settlement').getByRole('columnheader', { name: 'Cost centre' })).toHaveCount(0);
});

test('a run posted without cost centres says so, and shows no cost-centre column', async ({ page }) => {
  await boot(page, { costCentres: false }, 'gl-journal');
  await openJournal(page);
  await expect(tid(page, 'gl-posted-without-cost-centres')).toContainText('This run was posted without cost centres.');
  await expect(tid(page, 'gl-journal-accrual').getByRole('columnheader', { name: 'Cost centre' })).toHaveCount(0);
});

test('a server that predates the journals list still shows the journal it always did, with no false banner', async ({ page }) => {
  await boot(page, { legacyJournal: true }, 'gl-journal');
  await openJournal(page);
  await expect(page.getByText('Journal is balanced — Debits = Credits')).toBeVisible();
  await expect(page.getByText('Cost accounting is off for this company')).toHaveCount(0);
});

test('Arabic: journal titles, cost-centre and debit/credit labels, right to left', async ({ page }, info) => {
  await boot(page, { locale: 'ar' }, 'gl-journal');
  await page.getByTestId('cc-lock-run-select').selectOption('run-nov');
  await page.locator('button.bg-sapphire').first().click();
  await expect(tid(page, 'gl-posted-journals')).toBeVisible();
  await expect(tid(page, 'gl-journal-accrual')).toContainText('قيد استحقاق الرواتب');
  await expect(tid(page, 'gl-journal-settlement')).toContainText('تسوية صافي الرواتب');
  await expect(tid(page, 'gl-journal-remittance-GOSI')).toContainText('سداد التأمينات');
  await expect(tid(page, 'gl-journal-accrual')).toContainText('مركز التكلفة');
  await expect(tid(page, 'gl-journal-accrual')).toContainText('مدين');
  await expect(tid(page, 'gl-journal-accrual')).toContainText('دائن');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await shot(page, info, 'glui-ar-journal-posted');
});

// ═════════════════════════════ M1: void with the cash elections ═════════════════════════════

test('void of a paid and remitted run: the server\'s elections become choices with reference fields, then the void is resent', async ({ page }, info) => {
  const { writes, state } = await boot(page, { voidNeedsElections: true });
  await card(page, 'Nov 2026').locator(voidButton).click();
  await page.getByLabel('Reason', { exact: true }).fill('Posted against the wrong month');
  await tid(page, 'void-run-submit').click();

  // The first attempt carried no election; the refusal is turned into choices.
  await expect(tid(page, 'void-elections')).toBeVisible();
  expect(writes.filter(w => w.path.endsWith('/void'))[0].search).not.toContain('Disposition');
  await expect(tid(page, 'void-election-settlement')).toContainText('30,675.42');
  await expect(tid(page, 'void-election-remittance')).toContainText('GOSI remittance');
  await expect(tid(page, 'void-election-remittance')).toContainText('Loan remittance');
  await expect(tid(page, 'void-run-submit')).toBeDisabled();
  await shot(page, info, 'glui-en-void-elections');

  // Recall net pay (needs a reference), keep the remittance as disbursed.
  await tid(page, 'void-election-settlement').getByLabel('Funds recalled').check();
  await expect(tid(page, 'void-run-submit')).toBeDisabled();
  await tid(page, 'void-election-settlement').getByLabel('Recall reference').fill('RCL-NET-001');
  await tid(page, 'void-election-remittance').getByLabel('Funds already disbursed').check();
  await expect(tid(page, 'void-run-submit')).toBeEnabled();
  await tid(page, 'void-run-submit').click();

  await expect.poll(() => state.voided).toBe(true);
  const sent = writes.filter(w => w.path.endsWith('/void')).at(-1)!;
  const q = new URLSearchParams(sent.search);
  expect(q.get('settlementDisposition')).toBe('FundsRecalled');
  expect(q.get('settlementReference')).toBe('RCL-NET-001');
  expect(q.get('remittanceDisposition')).toBe('FundsDisbursed');
  expect(q.get('remittanceReference')).toBeNull();
  expect(sent.body).toEqual({ notes: 'Posted against the wrong month' });
  await expect(tid(page, 'void-elections')).toHaveCount(0);
});

test('a void that needs no election goes straight through', async ({ page }) => {
  const { writes, state } = await boot(page, {});
  await card(page, 'Nov 2026').locator(voidButton).click();
  await page.getByLabel('Reason', { exact: true }).fill('Duplicate run');
  await tid(page, 'void-run-submit').click();
  await expect.poll(() => state.voided).toBe(true);
  expect(writes.filter(w => w.path.endsWith('/void'))).toHaveLength(1);
});

test('Arabic: the elections, choices and reference field', async ({ page }, info) => {
  await boot(page, { locale: 'ar', voidNeedsElections: true });
  await card(page, 'Nov 2026').locator(voidButton).click();
  await page.locator('textarea').fill('خطأ في الشهر');
  await tid(page, 'void-run-submit').click();
  await expect(tid(page, 'void-elections')).toBeVisible();
  await expect(tid(page, 'void-elections')).toContainText('خرجت أموال هذا المسير من الحساب البنكي بالفعل');
  await expect(tid(page, 'void-election-settlement')).toContainText('استُردّت الأموال');
  await expect(tid(page, 'void-election-settlement')).toContainText('صُرفت الأموال ولم تُسترد');
  await tid(page, 'void-election-settlement').getByLabel('استُردّت الأموال').check();
  await expect(tid(page, 'void-election-settlement')).toContainText('مرجع الاسترداد');
  await shot(page, info, 'glui-ar-void-elections');
});

// ═════════════════════════════ M2: payment actions without payroll.export ═════════════════════════════

test('without payroll.export the batch cannot be created, and the screen says why instead of "lock the run first"', async ({ page }, info) => {
  await boot(page, { canExport: false }, 'bank-wps');
  await page.getByLabel('Select payroll run', { exact: true }).selectOption('run-nov');
  const create = page.getByRole('button', { name: 'Create Payment Batch' });
  await expect(create).toBeDisabled();
  await expect(page.getByText("You don't have permission to create payment batches — ask an administrator.").first()).toBeVisible();
  await expect(page.getByText('must be Locked first')).toHaveCount(0);
  await shot(page, info, 'glui-en-bank-no-export');
});

test('a 403 on creating a batch (permission held in the UI, refused by the server) shows the permission reason, not the lock hint', async ({ page }) => {
  await boot(page, { canExport: true, createRefused: true }, 'bank-wps');
  await page.getByLabel('Select payroll run', { exact: true }).selectOption('run-nov');
  await page.getByRole('button', { name: 'Create Payment Batch' }).click();
  await expect(page.getByText("You don't have permission to create payment batches — ask an administrator.").first()).toBeVisible();
  await expect(page.getByText('must be Locked first')).toHaveCount(0);
});

test('payment tracking without payroll.export: the row actions are replaced by the reason', async ({ page }) => {
  await boot(page, { canExport: false, batches: [batch({ wpsStatus: 'Accepted', effectiveWpsStatus: 'Accepted', status: 'FileGenerated' })] }, 'payment-tracking');
  await expect(page.getByText('Needs an administrator')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Settle net pay to GL' })).toHaveCount(0);
});

test('payment tracking with payroll.export keeps the settle action', async ({ page }) => {
  await boot(page, { canExport: true, batches: [batch({ wpsStatus: 'Accepted', effectiveWpsStatus: 'Accepted', status: 'FileGenerated' })] }, 'payment-tracking');
  await expect(page.getByRole('button', { name: 'Settle net pay to GL' })).toBeVisible();
});

test('Arabic: the permission message', async ({ page }, info) => {
  await boot(page, { locale: 'ar', canExport: false }, 'bank-wps');
  await runSelect(page).selectOption('run-nov');
  await expect(page.getByText('ليس لديك صلاحية إنشاء دفعات الرواتب — اطلب ذلك من مسؤول النظام.').first()).toBeVisible();
  await shot(page, info, 'glui-ar-bank-no-export');
});

// ═════════════════════════════ C2: a Voided run offers nothing to process ═════════════════════════════

test('a Voided run offers neither Process nor Validate', async ({ page }, info) => {
  await boot(page, {});
  const voided = card(page, 'Oct 2026');
  await expect(voided).toContainText('Voided');
  await expect(voided.getByRole('button', { name: /Process/ })).toHaveCount(0);
  await expect(voided.getByRole('button', { name: /Validate/ })).toHaveCount(0);
  // A locked run keeps Validate and Void.
  await expect(card(page, 'Nov 2026').getByRole('button', { name: /Validate/ })).toBeVisible();
  await shot(page, info, 'glui-en-runs-voided');
});

// ═════════════════════════════ C3: one payment status ═════════════════════════════

test('payment tracking shows the lifecycle status only: Paid, never "File generated" beside it', async ({ page }, info) => {
  await boot(page, { batches: [batch({})] }, 'payment-tracking');
  const row = page.locator('tbody tr', { hasText: 'PAY-202611-193005' });
  await expect(row).toContainText('Paid');
  await expect(row).not.toContainText('File generated');
  await shot(page, info, 'glui-en-tracking-status');
});
