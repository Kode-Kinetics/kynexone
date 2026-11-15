import { expect, test } from '@playwright/test';
import { boot, shot, USER, type Handler, type Locale } from './demo-polish.fixture';

/**
 * Status tail: raw status words and untranslated chrome on the Loans and Compliance pages, against a mocked API.
 * Run: npx playwright test -c e2e/playwright.demo-polish.config.ts e2e/status-tail.spec.ts
 */

const LOCALES: Locale[] = ['en', 'ar'];
const RAW_STATUS = /PendingApproval|InProgress|PaidInPayroll|WrittenOff|PartiallyPaid|UnderRenewal/;
/** Latin words of four or more letters: in Arabic a screen should have none (names, codes and currency codes are shorter or mixed). */
const latinWords = (text: string, allowed: RegExp = /^$/) => (text.match(/[A-Za-z]{4,}/g) ?? []).filter(w => !allowed.test(w));

const loan = (id: string, n: number, status: string) => ({
  id, employeeId: String(n), employeeName: `Employee ${n}`, loanTypeId: 'lt-1', loanTypeName: 'Personal Loan', loanNumber: `LN-000${n}`,
  requestedAmount: 4800, approvedAmount: 4800, requestedInstallments: 12, approvedInstallments: 12, installmentAmount: 400, repaymentFrequency: 'Monthly',
  repaymentMethod: 'BankTransfer', currency: 'SAR', totalRepaid: 0, outstandingBalance: 4800, status, notes: '', isLockedByPayroll: false, createdAtUtc: '2026-10-01T00:00:00Z',
});
const LOANS = [loan('l1', 1, 'PendingApproval'), loan('l2', 2, 'Active'), loan('l3', 3, 'Settled'), loan('l4', 4, 'WrittenOff'), loan('l5', 5, 'Disbursed')];
const ADVANCES = [{ id: 'a1', advanceNumber: 'ADV-1', employeeName: 'Employee 1', requestedAmount: 1000, approvedAmount: 1000, outstandingBalance: 1000, repaymentType: 'FullDeduction', status: 'Pending', currency: 'SAR' }];
const BATCHES = [{ id: 'b1', batchNumber: 'BB-1', batchName: 'Eid bonus', bonusTypeId: 'bt1', bonusTypeName: 'Eid', paymentPeriod: '2026-11', paymentDate: '2026-11-20', employeeCount: 3, totalAmount: 3000, status: 'PendingApproval' }];
const loansRoutes: Handler = path => {
  if (path === '/api/auth/me') return { ...USER, permissions: [...USER.permissions, 'loans.read', 'loans.write', 'loans.self', 'finance.read'] };
  if (path === '/api/finance/loans') return { items: LOANS, total: LOANS.length, page: 1, pageSize: 20 };
  if (path === '/api/finance/advances') return { items: ADVANCES, total: 1 };
  if (path === '/api/finance/bonuses/types' || path === '/api/finance/loans/types') return [];
  if (path === '/api/finance/bonuses/batches') return { items: BATCHES, total: 1 };
  return undefined;
};

const CONTRACTS = ['Draft', 'PendingApproval', 'Active', 'Expired', 'Terminated', 'Superseded'].map((status, i) => ({
  id: `c${i}`, employeeId: String(i), employeeName: `Employee ${i}`, templateId: null, contractNumber: `CT-00${i}`, contractType: 'Employment', status,
  startDate: '2026-01-01', endDate: '2027-01-01', basicSalary: 9000, currencyCode: 'SAR', language: 'en', version: 1, previousVersionId: null,
  signedByEmployeeName: '', signedByEmployeeAtUtc: null, signedByHrName: '', signedByHrAtUtc: null, fileUrl: '', createdAtUtc: '2026-01-01T00:00:00Z', updatedAtUtc: null,
}));
const RENEWALS = ['Pending', 'InProgress', 'Renewed', 'Overdue', 'Exempted'].map((status, i) => ({
  id: `r${i}`, employeeId: String(i), employeeName: `Employee ${i}`, documentType: 'Iqama', documentNumber: `2${i}00`, expiryDate: '2026-12-01', renewalDate: null, status, assignedToName: '', notes: '', createdAtUtc: '2026-10-01T00:00:00Z',
}));
const complianceRoutes: Handler = (path, search) => {
  if (path === '/api/compliance/contracts') return { items: CONTRACTS, total: CONTRACTS.length, page: Number(search.get('page') ?? 1) };
  if (path === '/api/compliance/renewals') return { items: RENEWALS, total: RENEWALS.length, page: Number(search.get('page') ?? 1) };
  if (path === '/api/compliance/reports/dashboard') return { activeContracts: 4, visasExpiring30: 1, visasExpiring60: 0, passportsExpiring90: 2, expiredVisas: 0, pendingRenewals: 1, passportsHeldByCompany: 0 };
  return undefined;
};

const optionTexts = (page: import('@playwright/test').Page) => page.locator('select option').allInnerTexts();

for (const locale of LOCALES) {
  test.describe(`Loans and Advances (${locale})`, () => {
    test('the status filter and the status chips use the shared labels', async ({ page }) => {
      await boot(page, { locale, url: '/loans', routes: loansRoutes, waitMs: 2500 });
      await expect(page.getByText('LN-0001').first()).toBeVisible({ timeout: 60_000 });
      const options = (await optionTexts(page)).join(' | ');
      const table = await page.locator('table').first().innerText();
      expect(options, options).not.toMatch(RAW_STATUS);
      expect(table, table).not.toMatch(RAW_STATUS);
      if (locale === 'ar') {
        expect(latinWords(options), options).toEqual([]);
        expect(latinWords(table, /^(Employee|Personal|Loan)$/), table).toEqual([]);
        expect(table).toContain('مشطوب');
      } else {
        expect(options).toContain('Pending');
        expect(table).toContain('Pending approval');
        expect(table).toContain('Written off');
      }
      await shot(page, 'loans-list', locale);
    });

    test('the page chrome is in the viewer\'s language', async ({ page }) => {
      await boot(page, { locale, url: '/loans', routes: loansRoutes, waitMs: 2500 });
      await expect(page.getByText('LN-0001').first()).toBeVisible({ timeout: 60_000 });
      if (locale === 'ar') {
        const chrome = await page.locator('main').first().innerText();
        expect(latinWords(chrome, /^(Employee|Personal|Loan|ACME|Acme|Arabia)$/), chrome).toEqual([]);
      }
    });

    test('the Salary Advances and Bonus Batches tabs read in the viewer\'s language', async ({ page }) => {
      await boot(page, { locale, url: '/loans', routes: loansRoutes, waitMs: 2500 });
      await expect(page.getByText('LN-0001').first()).toBeVisible({ timeout: 60_000 });
      const tabNames = locale === 'ar' ? ['السلف على الراتب', 'دفعات المكافآت'] : ['Salary Advances', 'Bonus Batches'];
      for (const [i, name] of tabNames.entries()) {
        await page.getByRole('button', { name }).first().click();
        await expect(page.locator(i === 0 ? 'text=ADV-1' : 'text=BB-1').first()).toBeVisible({ timeout: 30_000 });
        const options = (await optionTexts(page)).join(' | ');
        const body = await page.locator('main').first().innerText();
        expect(options, options).not.toMatch(RAW_STATUS);
        expect(body, body).not.toMatch(RAW_STATUS);
        if (locale === 'ar') expect(latinWords(options + ' ' + body, /^(Employee|Eid|bonus|Acme|Arabia|ACME)$/), options + body).toEqual([]);
        await shot(page, i === 0 ? 'loans-advances' : 'loans-bonus-batches', locale);
      }
    });
  });

  test.describe(`Compliance (${locale})`, () => {
    test('contracts: the status filter, chips and chrome use the shared labels', async ({ page }) => {
      await boot(page, { locale, url: '/compliance?tab=contracts', routes: complianceRoutes, waitMs: 2500 });
      await expect(page.getByText('CT-001').first()).toBeVisible({ timeout: 60_000 });
      const options = (await optionTexts(page)).join(' | ');
      const body = await page.locator('main').first().innerText();
      expect(options, options).not.toMatch(RAW_STATUS);
      expect(body, body).not.toMatch(RAW_STATUS);
      if (locale === 'ar') {
        expect(latinWords(options), options).toEqual([]);
        expect(latinWords(body, /^(Employee|Employment)$/), body).toEqual([]);
      } else {
        expect(options).toContain('Pending approval');
      }
      await shot(page, 'compliance-contracts', locale);
    });

    test('renewals: the status filter and chips use the shared labels', async ({ page }) => {
      await boot(page, { locale, url: '/compliance?tab=renewals', routes: complianceRoutes, waitMs: 2500 });
      await expect(page.getByText('Employee 1').first()).toBeVisible({ timeout: 60_000 });
      const options = (await optionTexts(page)).join(' | ');
      const body = await page.locator('main').first().innerText();
      expect(options, options).not.toMatch(RAW_STATUS);
      expect(body, body).not.toMatch(RAW_STATUS);
      if (locale === 'ar') {
        expect(latinWords(options), options).toEqual([]);
        expect(latinWords(body, /^(Employee|Iqama)$/), body).toEqual([]);
      } else {
        expect(options).toContain('In progress');
      }
      await shot(page, 'compliance-renewals', locale);
    });

    test('the dashboard tab and tab bar are in the viewer\'s language', async ({ page }) => {
      await boot(page, { locale, url: '/compliance', routes: complianceRoutes, waitMs: 2500 });
      await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible({ timeout: 60_000 });
      await page.waitForTimeout(1500);
      const body = await page.locator('main').first().innerText();
      if (locale === 'ar') expect(latinWords(body), body).toEqual([]);
      await shot(page, 'compliance-dashboard-tabs', locale);
    });
  });
}
