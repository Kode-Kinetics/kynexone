import { expect, test, type Page } from '@playwright/test';
import type { JournalExportFormat, JournalExportManifest, JournalExportSummary, JournalExportJournal } from '../src/api/financeErp';

/**
 * ERP journal hand-off on the Payroll → GL Journal tab, against synthetic, intercepted API responses.
 * Run from frontend after starting a dev server:
 *   npx next dev -p 5191 &
 *   GL_EXPORTS_BASE_URL=http://127.0.0.1:5191 npx playwright test -c e2e/playwright.gl-journal-exports.config.ts
 * This proves the UI contract (create → download → confirm / reject, and the double-post refusal shown in
 * plain words). Server rules are proven by JournalExportDoublePostTests / JournalExportConcurrencyPostgresTests.
 */

const companyA = { id: 'co-a', name: 'Alpha KSA', tradeName: 'Alpha', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' };
const companyB = { id: 'co-b', name: 'Beta Services', tradeName: 'Beta', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' };

const formats: JournalExportFormat[] = [
  { key: 'generic-csv', displayName: 'Generic journal CSV (line-per-row)', extension: 'csv', contentType: 'text/csv', disclaimer: 'Neutral line-level export.', status: 'Verified', options: [] },
  {
    key: 'oracle-gl-interface-csv', displayName: 'Oracle EBS GL_INTERFACE staging CSV (template – validate in your ERP test instance)',
    extension: 'csv', contentType: 'text/csv', disclaimer: 'Oracle staging rows.', status: 'Template',
    options: [{ key: 'ledgerId', label: 'Ledger ID', defaultValue: '', description: 'Your ledger id.', maxLength: 15 }],
  },
];

const byCompany = [
  { companyCode: 'SA01', companyId: 'co-a', debits: 10000, credits: 10000, balanced: true, lines: 2 },
  { companyCode: '1010-555', companyId: 'co-b', debits: 2500, credits: 2500, balanced: true, lines: 2 },
];

function manifest(exportId: string | null, kind: JournalExportManifest['kind'] = 'Full'): JournalExportManifest {
  return {
    exportId, status: exportId ? 'Exported' : null, kind, replacesExportId: null,
    format: { key: 'generic-csv', displayName: formats[0].displayName, disclaimer: formats[0].disclaimer, extension: 'csv', status: 'Verified' },
    fileName: 'journal-GROUP-2026-07-SAR-generic-csv-5F0C2A9E.csv', fileHash: 'abc', currency: 'SAR', companyCode: 'GROUP',
    entryCount: 3, lineCount: 4, totalDebits: 12500, totalCredits: 12500, byCompany, journalCount: 2,
    previouslyConfirmedExcluded: kind === 'Supplementary' ? { count: 3, total: 12500, exportIds: ['old'] } : { count: 0, total: 0, exportIds: [] },
    warnings: [], outOfPeriodEntryDateCount: 0, downloadUrl: exportId ? `/api/finance/gl/journal-exports/${exportId}/download` : null,
  };
}

interface State {
  doublePost: boolean;
  history: JournalExportSummary[];
  journals: JournalExportJournal[];
  confirms: unknown[];
  rejects: unknown[];
  creates: string[];
  errors: string[];
}

async function boot(page: Page, options: { doublePost?: boolean; locale?: 'en' | 'ar' } = {}): Promise<State> {
  const state: State = {
    doublePost: options.doublePost ?? false, history: [], confirms: [], rejects: [], creates: [], errors: [],
    journals: [
      { journalRef: '2026-07-Payroll-11111111-Accrual', companyCode: 'SA01', composite: false, lineCount: 2, debits: 10000, credits: 10000, accountingDate: '2026-07-31', description: 'July salaries', status: 'Pending', erpDocumentNumber: null, erpPostedAtUtc: null, reason: null, decidedByName: null },
      { journalRef: '2026-07-Loan-66666666-Disbursement', companyCode: '1010-555', composite: false, lineCount: 2, debits: 2500, credits: 2500, accountingDate: '2026-07-31', description: 'Loan LN-1', status: 'Pending', erpDocumentNumber: null, erpPostedAtUtc: null, reason: null, decidedByName: null },
    ],
  };
  page.on('pageerror', error => state.errors.push(error.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'gl-export-browser-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });

  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });

    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/auth/me') return reply({
      id: 'gl-reviewer', tenantId: 'gl-tenant', tenantSlug: 'gl-fixture', fullName: 'Finance Reviewer',
      roles: ['Admin'], permissions: ['payroll.read', 'finance.gl.read', 'finance.gl.manage', 'finance.erp.confirm', 'dashboard.read'],
      accountType: 'Group', isGroupScope: true,
      companies: [companyA, companyB].map(c => ({ id: c.id, name: c.name, code: c.id, countryCode: 'SA', isActive: true })),
    });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/features/modules' || path === '/api/notifications') return reply([]);
    if (path === '/api/companies') return reply(paged([companyA, companyB]));
    if (path === '/api/payroll/companies') return reply([companyA, companyB]);

    const gl = '/api/finance/gl/journal-exports';
    if (path === `${gl}/formats`) return reply(formats);
    if (path === `${gl}/preview`) return reply(manifest(null, 'Supplementary'));
    if (path === gl && request.method() === 'POST') {
      state.creates.push(url.search);
      if (state.doublePost) return reply({
        error: 'already_posted_to_erp',
        message: 'Nothing new to export: all 3 ledger row(s) in this scope are already confirmed as posted in your ERP. Exporting them again would post them twice. If the ERP reversed that journal, create a Replacement export for the confirmed export instead.',
      }, 422);
      const created = manifest('exp-1', 'Supplementary');
      state.history = [{
        id: 'exp-1', companyId: null, companyCode: 'GROUP', period: '2026-07', payrollRunId: null, formatKey: 'generic-csv', currency: 'SAR',
        fileName: created.fileName, fileHash: 'abc', lineCount: 4, entryCount: 3, totalDebits: 12500, totalCredits: 12500,
        includeUnattributed: false, status: 'Exported', exportedByName: 'Payroll Maker', exportedAtUtc: '2026-08-03T09:30:00Z',
        erpDocumentNumber: null, erpPostedAtUtc: null, confirmedByName: null, confirmedAtUtc: null, rejectionReason: null, notes: null,
        kind: 'Supplementary', replacesExportId: null, formatVersion: 2, confirmedJournalCount: 0,
      }];
      return reply(created, 201);
    }
    if (path === gl) return reply(state.history);
    if (path === `${gl}/exp-1/download`) return route.fulfill({
      status: 200, contentType: 'text/csv',
      headers: { 'content-disposition': 'attachment; filename="journal-GROUP-2026-07-SAR-generic-csv-5F0C2A9E.csv"' },
      body: 'AccountingDate,SystemPostedDate\n2026-07-31,2026-08-03\n',
    });
    if (path === `${gl}/exp-1/journals/confirm`) {
      const body = request.postDataJSON() as { journals: { journalRef: string; erpDocumentNumber: string }[] };
      state.confirms.push(body);
      for (const j of body.journals) {
        const row = state.journals.find(x => x.journalRef === j.journalRef)!;
        Object.assign(row, { status: 'Confirmed', erpDocumentNumber: j.erpDocumentNumber });
      }
      return reply({ exportId: 'exp-1', status: 'Exported' });
    }
    if (path === `${gl}/exp-1/journals/reject`) {
      const body = request.postDataJSON() as { journals: { journalRef: string; reason: string }[] };
      state.rejects.push(body);
      for (const j of body.journals) Object.assign(state.journals.find(x => x.journalRef === j.journalRef)!, { status: 'Rejected', reason: j.reason });
      return reply({ exportId: 'exp-1', status: 'PartiallyConfirmed' });
    }
    if (path === `${gl}/exp-1`) return reply({
      export: state.history[0], formatter: null,
      newRowsInScopeSinceExport: { count: 0, total: 0, message: null }, journals: state.journals,
    });
    // Other payroll reads are outside this contract: answer 404 so each screen takes its own empty/error path.
    if (path.startsWith('/api/payroll/')) return reply({ message: 'Not part of this fixture.' }, 404);
    if (path.startsWith('/api/admin/') || path.includes('country-packs')) return reply([]);
    return reply(paged([]));
  });

  await page.goto('/payroll');
  await page.getByRole('tab', { name: /GL Journal/ }).click();
  const heading = options.locale === 'ar' ? 'إرسال القيد إلى نظام تخطيط الموارد' : 'Send journal to your ERP';
  await expect(page.getByRole('heading', { name: heading })).toBeVisible({ timeout: 60_000 });
  return state;
}

async function chooseJuly(page: Page) {
  await page.getByLabel('Accounting month').fill('2026-07');
}

test('checks balances, creates, downloads and confirms one journal with its ERP document number', async ({ page }, info) => {
  const state = await boot(page);
  await chooseJuly(page);

  await page.getByRole('button', { name: 'Check balances' }).click();
  const manifestCard = page.getByTestId('journal-export-manifest');
  await expect(manifestCard.getByText('Supplementary export')).toBeVisible();
  await expect(manifestCard.getByText('3 ledger rows already posted in your ERP are left out, so nothing is posted twice.')).toBeVisible();
  await expect(manifestCard.getByText('Balanced')).toHaveCount(2);
  expect(state.creates).toHaveLength(0);

  await page.getByRole('button', { name: 'Create export file' }).click();
  await expect(manifestCard.getByText(/File created:/)).toBeVisible();
  expect(state.creates[0]).toContain('period=2026-07');
  expect(state.creates[0]).toContain('format=generic-csv');

  const downloadPromise = page.waitForEvent('download');
  await manifestCard.getByRole('button', { name: 'Download file' }).click();
  expect((await downloadPromise).suggestedFilename()).toBe('journal-GROUP-2026-07-SAR-generic-csv-5F0C2A9E.csv');

  const outcome = page.getByTestId('journal-export-outcome-exp-1');
  await expect(outcome.getByText('2026-07-Payroll-11111111-Accrual')).toBeVisible();
  const payroll = outcome.locator('div.rounded-md').filter({ hasText: '2026-07-Payroll-11111111-Accrual' });
  await payroll.getByLabel('ERP document number').fill('SAP-1900001');
  await payroll.getByLabel('Posted on').fill('2026-08-04');
  await payroll.getByRole('button', { name: 'Confirm posted' }).click();

  await expect(payroll.getByText('Posted in ERP')).toBeVisible();
  await expect(payroll.getByText('ERP document SAP-1900001')).toBeVisible();
  expect(state.confirms).toEqual([{
    journals: [{ journalRef: '2026-07-Payroll-11111111-Accrual', erpDocumentNumber: 'SAP-1900001', postedAtUtc: '2026-08-04T00:00:00Z' }],
    notes: null,
  }]);
  expect(state.errors).toEqual([]);
  const shot = info.outputPath('journal-exports-confirmed.png');
  await page.getByRole('heading', { name: 'Send journal to your ERP' }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: shot, fullPage: true });
  await info.attach('journal-exports-confirmed', { path: shot, contentType: 'image/png' });
});

test('records an ERP rejection for one journal with its reason', async ({ page }) => {
  const state = await boot(page);
  await chooseJuly(page);
  await page.getByRole('button', { name: 'Create export file' }).click();

  const outcome = page.getByTestId('journal-export-outcome-exp-1');
  const loan = outcome.locator('div.rounded-md').filter({ hasText: '2026-07-Loan-66666666-Disbursement' });
  await expect(loan.getByRole('button', { name: 'Record rejection' })).toBeDisabled();
  await loan.getByLabel('Reason the ERP refused it').fill('Cost centre missing in company 1010');
  await loan.getByRole('button', { name: 'Record rejection' }).click();

  await expect(loan.getByText('Rejected by ERP')).toBeVisible();
  await expect(loan.getByText('Cost centre missing in company 1010')).toBeVisible();
  expect(state.rejects).toEqual([{ journals: [{ journalRef: '2026-07-Loan-66666666-Disbursement', reason: 'Cost centre missing in company 1010' }] }]);
  expect(state.confirms).toEqual([]);
  expect(state.errors).toEqual([]);
});

test('shows the double-post refusal in plain words and creates nothing', async ({ page }) => {
  const state = await boot(page, { doublePost: true });
  await chooseJuly(page);
  await page.getByRole('button', { name: 'Create export file' }).click();

  const refusal = page.getByTestId('journal-export-refusal');
  await expect(refusal).toContainText('Exporting them again would post them twice.');
  await expect(page.getByTestId('journal-export-manifest')).toHaveCount(0);
  await expect(page.getByText('No export files for this month yet.')).toBeVisible();
  expect(state.creates).toHaveLength(1);
  expect(state.errors).toEqual([]);
});

test('renders the panel in Arabic', async ({ page }) => {
  await boot(page, { locale: 'ar' });
  await expect(page.getByRole('button', { name: 'إنشاء ملف التصدير' })).toBeVisible();
});

test('a double-clicked Lock sends one request, shows no error, and each run names its company', async ({ page }) => {
  // Found live: double-clicking Lock fired two requests. The server posted the journal once and refused the
  // second (409), and that refusal then showed as an error under a run that had just locked. At group scope the
  // run cards also gave no company, so four "Sep 2026 Draft" cards were indistinguishable.
  const run = {
    id: 'run-oct', companyId: 'co-b', year: 2026, month: 10, status: 'Approved', runType: 'Regular', totalGrossSalary: 25730.21,
    totalDeductions: 3119.43, totalNetSalary: 22610.78, totalEmployerStatutoryCost: 2820, employeeCount: 3,
    createdAtUtc: '2026-10-10T00:00:00Z', processedAtUtc: '2026-10-10T00:00:00Z', lockedAtUtc: null,
  };
  const lockCalls: string[] = [];
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'gl-export-browser-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
  });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/auth/me') return reply({
      id: 'gl-reviewer', tenantId: 'gl-tenant', tenantSlug: 'gl-fixture', fullName: 'Finance Reviewer',
      roles: ['Admin'], permissions: ['payroll.read', 'payroll.lock', 'dashboard.read'], accountType: 'Group', isGroupScope: true,
      companies: [companyA, companyB].map(c => ({ id: c.id, name: c.name, code: c.id, countryCode: 'SA', isActive: true })),
    });
    if (path === '/api/payroll/companies') return reply([companyA, companyB]);
    if (path === '/api/payroll/runs') return reply({ items: [run], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/payroll/runs/run-oct/lock') {
      lockCalls.push(request.method());
      await new Promise(r => setTimeout(r, 400));
      if (lockCalls.length > 1) return reply({ error: 'run_already_locked', message: 'This payroll run is already locked and its journal is already posted. Nothing was posted again.' }, 409);
      return reply({ ...run, status: 'Locked', lockedAtUtc: '2026-10-10T01:00:00Z' });
    }
    if (path.startsWith('/api/payroll/')) return reply({ message: 'Not part of this fixture.' }, 404);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/payroll');
  await page.getByRole('tab', { name: 'Payroll Runs', exact: true }).click();
  const card = page.locator('main [role="button"]').filter({ hasText: 'Oct 2026' }).first();
  await expect(card).toContainText('Beta Services');
  await card.getByRole('button', { name: 'Lock' }).dblclick();
  await expect(card).toContainText('Locked');
  expect(lockCalls).toEqual(['POST']);
  await expect(page.getByText(/already locked/)).toHaveCount(0);
});
