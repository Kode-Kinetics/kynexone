import { expect, test, type Page } from '@playwright/test';
import type { GlPeriod, JournalExportFormat, JournalExportManifest, JournalExportSummary, JournalExportJournal } from '../src/api/financeErp';

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

const periods: GlPeriod[] = [
  { id: 'p1', companyId: 'co-a', period: '2026-09', status: 'Open', closedAtUtc: '2026-10-01T08:00:00Z', closedByName: 'Finance Lead', closedReason: null, reopenedAtUtc: '2026-10-02T08:00:00Z', reopenedByName: 'Controller', reopenReason: 'Late correction' },
  { id: 'p2', companyId: 'co-b', period: '2026-08', status: 'Closed', closedAtUtc: '2026-09-02T08:00:00Z', closedByName: 'Finance Lead', closedReason: null, reopenedAtUtc: null, reopenedByName: null, reopenReason: null },
  { id: 'p3', companyId: null, period: '2026-07', status: 'Closed', closedAtUtc: '2026-08-05T08:00:00Z', closedByName: 'Group CFO', closedReason: null, reopenedAtUtc: null, reopenedByName: null, reopenReason: null },
];

interface State {
  doublePost: boolean;
  periodCalls: { method: string; path: string; search: string; body: unknown }[];
  listCalls: string[];
  history: JournalExportSummary[];
  journals: JournalExportJournal[];
  confirms: unknown[];
  rejects: unknown[];
  creates: string[];
  errors: string[];
}

interface BootOptions {
  doublePost?: boolean;
  locale?: 'en' | 'ar';
  /** false = a finance user whose access is limited to company A (the API forbids them "all companies"). */
  groupScope?: boolean;
  /** 'periods' opens Setup → GL & rates → Accounting periods instead of the payroll GL Journal tab. */
  screen?: 'journal' | 'periods';
}

async function boot(page: Page, options: BootOptions = {}): Promise<State> {
  const groupScope = options.groupScope ?? true;
  const visibleCompanies = groupScope ? [companyA, companyB] : [companyA];
  const state: State = {
    doublePost: options.doublePost ?? false, history: [], confirms: [], rejects: [], creates: [], errors: [],
    periodCalls: [], listCalls: [],
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
      roles: ['Admin'],
      permissions: ['payroll.read', 'finance.gl.read', 'finance.gl.manage', 'finance.erp.confirm', 'finance.gl.period.reopen', 'dashboard.read',
        ...(options.screen === 'periods' ? ['organization.write', 'organization.read'] : [])],
      accountType: 'Group', isGroupScope: groupScope,
      companies: visibleCompanies.map(c => ({ id: c.id, name: c.name, code: c.id, countryCode: 'SA', isActive: true })),
    });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/features/modules' || path === '/api/notifications') return reply([]);
    if (path === '/api/companies') return reply(paged(visibleCompanies.map(c => ({ ...c, legalNameEn: c.name, legalNameAr: '', countryCode: 'SA', isActive: true }))));
    if (path === '/api/payroll/companies') return reply(visibleCompanies);
    if (path === '/api/finance/gl/periods') return reply(periods);
    if (path.startsWith('/api/finance/gl/periods/')) {
      state.periodCalls.push({ method: request.method(), path, search: url.search, body: request.postDataJSON() });
      return reply(periods[0]);
    }

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
    if (path === gl) { state.listCalls.push(url.search); return reply(state.history); }
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

  if (options.screen === 'periods') {
    await page.goto('/setup?tab=glMapping');
    await page.getByRole('tab', { name: 'Accounting periods' }).click();
    await expect(page.getByRole('heading', { name: 'Accounting periods' })).toBeVisible({ timeout: 60_000 });
    return state;
  }
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

test('a company-scoped finance user starts on their own company and is never offered "all companies"', async ({ page }) => {
  const state = await boot(page, { groupScope: false });
  const company = page.getByLabel('Company');
  await expect(company).toHaveValue('co-a');
  await expect(company.locator('option')).toHaveText(['Alpha KSA']);
  await expect(page.getByText('No export files for this month yet.')).toBeVisible();
  expect(state.listCalls.length).toBeGreaterThan(0);
  expect(state.listCalls.every(q => q.includes('companyId=co-a')), 'history is never asked for the whole group').toBe(true);

  await chooseJuly(page);
  await page.getByRole('button', { name: 'Create export file' }).click();
  await expect(page.getByTestId('journal-export-manifest').getByText(/File created:/)).toBeVisible();
  expect(state.creates[0]).toContain('companyId=co-a');
  expect(state.errors).toEqual([]);
});

test('group view: each period row is closed or reopened in its own company, and a group-wide close is explicit', async ({ page }, info) => {
  const state = await boot(page, { screen: 'periods' });
  const rows = page.getByTestId('gl-periods');
  await expect(rows.getByTestId('gl-period-company')).toHaveText(['Alpha KSA', 'Beta Services', 'All companies']);

  // Company A's reopened September: "Close month" closes company A only — never the whole group.
  await page.getByTestId('gl-period-2026-09-co-a').getByRole('button', { name: 'Close month' }).click();
  await expect.poll(() => state.periodCalls.length).toBe(1);
  expect(state.periodCalls[0]).toMatchObject({ method: 'POST', path: '/api/finance/gl/periods/2026-09/close', search: '?companyId=co-a' });

  // Company B's closed August reopens in company B's scope.
  const august = page.getByTestId('gl-period-2026-08-co-b');
  await august.getByRole('button', { name: 'Reopen month' }).click();
  await august.getByLabel('Why reopen this month? (required)').fill('Late loan posting');
  await august.getByRole('button', { name: 'Confirm reopen' }).click();
  await expect.poll(() => state.periodCalls.length).toBe(2);
  expect(state.periodCalls[1]).toMatchObject({ path: '/api/finance/gl/periods/2026-08/reopen', search: '?companyId=co-b', body: { reason: 'Late loan posting' } });

  // A new close for every company names the companies it blocks, and is sent only after confirmation.
  await page.getByLabel('Month to close').fill('2026-10');
  await expect(page.getByTestId('gl-period-close-target')).toHaveValue('');
  await page.getByRole('button', { name: 'Close for all companies' }).first().click();
  const confirm = page.getByTestId('gl-period-group-close-confirm');
  await expect(confirm).toContainText('Close 2026-10 for all companies?');
  await expect(confirm).toContainText('Alpha KSA, Beta Services');
  expect(state.periodCalls).toHaveLength(2);
  const shot = info.outputPath('gl-periods-group-close-confirm.png');
  await page.screenshot({ path: shot, fullPage: true });
  await info.attach('gl-periods-group-close-confirm', { path: shot, contentType: 'image/png' });
  await confirm.getByRole('button', { name: 'Close for all companies' }).click();
  await expect.poll(() => state.periodCalls.length).toBe(3);
  expect(state.periodCalls[2]).toMatchObject({ path: '/api/finance/gl/periods/2026-10/close', search: '' });

  // Choosing one company closes just that company, with no group confirmation.
  await page.getByLabel('Month to close').fill('2026-11');
  await page.getByTestId('gl-period-close-target').selectOption('co-b');
  await page.getByRole('button', { name: 'Close month', exact: true }).first().click();
  await expect.poll(() => state.periodCalls.length).toBe(4);
  expect(state.periodCalls[3]).toMatchObject({ path: '/api/finance/gl/periods/2026-11/close', search: '?companyId=co-b' });
  expect(state.errors).toEqual([]);
});
