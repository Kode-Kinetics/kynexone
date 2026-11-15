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

  test.describe(`Salary Advances tab (${locale})`, () => {
    const ADV = (id: string, n: number, status: string, over: Record<string, unknown> = {}) => ({
      id, employeeId: `00000000-0000-4000-8000-00000000000${n}`, employeeName: `Employee ${n}`, advanceNumber: `ADV-2026-0000${n}`, requestedAmount: 1000, approvedAmount: status === 'Pending' ? 0 : 1000,
      repaymentType: 'OneTime', installments: 1, installmentAmount: 1000, repaymentStartDate: '2026-12-01', totalRepaid: 0, outstandingBalance: status === 'Pending' ? 0 : 1000, reason: 'Medical', status,
      isLockedByPayroll: false, createdAtUtc: '2026-11-01T09:00:00Z', ...over,
    });
    const LIST = [ADV('a1', 1, 'Pending'), ADV('a2', 2, 'Approved'), ADV('a3', 3, 'Active', { paidAtUtc: '2026-11-05T10:00:00Z', paymentReference: 'BANK-REF-77', repaymentType: 'Installments', installments: 2 }), ADV('a4', 4, 'Closed'), ADV('a5', 5, 'Cancelled', { cancelledAtUtc: '2026-11-06T08:00:00Z', cancelReason: 'Employee withdrew the request' })];
    const DETAIL = {
      advance: LIST[2],
      installments: [
        { id: 'i1', advanceId: 'a3', installmentNumber: 1, dueDate: '2026-12-01', amountDue: 500, amountPaid: 500, status: 'Paid', paidDate: '2026-12-01' },
        { id: 'i2', advanceId: 'a3', installmentNumber: 2, dueDate: '2027-01-01', amountDue: 500, amountPaid: 0, status: 'Pending' },
      ],
      approvals: [{ id: 'ap1', advanceId: 'a3', stepOrder: 1, approverRole: 'Finance', approvedByName: 'Fatima Finance', status: 'Approved', comments: '', decidedAtUtc: '2026-11-04T10:00:00Z' }],
      auditLogs: [], glEntries: [],
    };
    const advRoutes: Handler = (path, search) => {
      if (path === '/api/auth/me') return { ...USER, permissions: [...USER.permissions, 'loans.read', 'loans.write', 'loans.self', 'finance.read'] };
      if (path === '/api/finance/advances') return { items: search.get('status') ? LIST.filter(a => a.status === search.get('status')) : LIST, total: LIST.length };
      if (path === '/api/finance/advances/a3') return DETAIL;
      if (path === '/api/finance/advances/a5') return { ...DETAIL, advance: LIST[4], installments: [], approvals: [] };
      if (path === '/api/finance/loans/types' || path === '/api/finance/bonuses/types') return [];
      return undefined;
    };
    const openAdvances = async (page: import('@playwright/test').Page, routes: Handler = advRoutes, beforeTab?: () => Promise<void>) => {
      await boot(page, { locale, url: '/loans', routes, waitMs: 2500 });
      await expect(page.getByText('LN-0001').or(page.getByText('No loans found')).or(page.getByText('لا توجد قروض')).first()).toBeVisible({ timeout: 60_000 });
      if (beforeTab) await beforeTab();
      await page.getByRole('button', { name: locale === 'ar' ? 'السلف على الراتب' : 'Salary Advances' }).first().click();
    };

    test('the status filter offers exactly the statuses an advance can have, in the viewer\'s language', async ({ page }) => {
      await openAdvances(page);
      await expect(page.getByText('ADV-2026-00001')).toBeVisible({ timeout: 30_000 });
      const options = await optionTexts(page);
      expect(options, options.join(' | ')).toHaveLength(7);
      expect(options.join(' | ')).not.toMatch(RAW_STATUS);
      expect(options.join(' | ')).not.toMatch(/Settled|مسوّى/);
      expect(options.join(' | ')).toContain(locale === 'ar' ? 'معتمدة — بانتظار الصرف' : 'Approved — awaiting payout');
      if (locale === 'ar') expect(latinWords(options.join(' ')), options.join(' | ')).toEqual([]);
      await shot(page, 'loans-advances-list', locale);
    });

    test('a row opens the detail: schedule, approval route and the payout reference', async ({ page }) => {
      await openAdvances(page);
      await page.getByRole('button', { name: new RegExp(locale === 'ar' ? 'عرض السلفة ADV-2026-00003' : 'View advance ADV-2026-00003') }).click();
      const detail = page.getByTestId('advance-detail');
      await expect(detail.getByTestId('advance-payout')).toContainText('BANK-REF-77', { timeout: 30_000 });
      await expect(detail.getByTestId('advance-approvals')).toContainText('Fatima Finance');
      await expect(detail.getByRole('row')).toHaveCount(3);
      const text = await detail.innerText();
      expect(text).not.toMatch(RAW_STATUS);
      if (locale === 'ar') expect(latinWords(text, /^(Employee|BANK|Fatima|Finance|Medical)$/), text).toEqual([]);
      await shot(page, 'loans-advance-detail', locale);
    });

    test('a failed list says so and offers Retry, never an empty register', async ({ page }) => {
      await openAdvances(page, advRoutes, async () => {
        await page.route('**/api/finance/advances?*', route => route.fulfill({ status: 500, json: { message: 'boom' } }));
      });
      await expect(page.getByRole('button', { name: locale === 'ar' ? 'إعادة المحاولة' : 'Retry' }).first()).toBeVisible({ timeout: 30_000 });
      await expect(page.getByText(locale === 'ar' ? 'لا توجد سلف' : 'No advances found')).toHaveCount(0);
      await shot(page, 'loans-advances-load-failed', locale);
    });

    test('a refused request shows the server\'s reason, not the raw body', async ({ page }) => {
      await openAdvances(page, (path, search, method, route) => {
        if (path === '/api/auth/me') return { ...USER, roles: ['Employee'], permissions: ['ess.read', 'ess.write', 'loans.self'] };
        return advRoutes(path, search, method, route);
      }, async () => {
        await page.route('**/api/finance/advances', async route => {
          if (route.request().method() !== 'POST') return route.fallback();
          return route.fulfill({ status: 400, json: { code: 'bad_request', message: 'The requested amount is above the policy limit.' } });
        });
      });
      await page.getByRole('button', { name: locale === 'ar' ? 'طلب سلفة جديد' : 'New Advance Request' }).click();
      await page.getByRole('spinbutton').first().fill('5000');
      await page.getByRole('button', { name: locale === 'ar' ? 'إرسال' : 'Submit' }).last().click();
      await expect(page.getByText('The requested amount is above the policy limit.')).toBeVisible({ timeout: 30_000 });
      expect(await page.locator('body').innerText()).not.toMatch(/\{"code"/);
    });

    test('approving sends the start date shown, never an empty one', async ({ page }) => {
      const bodies: Array<Record<string, unknown>> = [];
      await openAdvances(page, advRoutes, async () => {
        await page.route('**/api/finance/advances/a1/approve', async route => { bodies.push(route.request().postDataJSON()); return route.fulfill({ json: LIST[0] }); });
      });
      await page.getByText('ADV-2026-00001').waitFor({ timeout: 30_000 });
      await page.getByRole('row').filter({ hasText: 'ADV-2026-00001' }).getByRole('button', { name: locale === 'ar' ? 'موافقة' : 'Approve', exact: true }).click();
      await page.getByRole('dialog').getByRole('button', { name: locale === 'ar' ? 'موافقة' : 'Approve', exact: true }).click();
      await expect.poll(() => bodies.length, { timeout: 30_000 }).toBe(1);
      expect(bodies[0].repaymentStartDate).toBe('2026-12-01');
    });

    const selfRoutes = (extra?: Handler): Handler => (path, search, method, route) => {
      if (path === '/api/auth/me') return { ...USER, roles: ['Employee'], permissions: ['ess.read', 'ess.write', 'loans.self'] };
      const custom = extra?.(path, search, method, route);
      if (custom !== undefined) return custom;
      if (path === '/api/finance/advances' && method === 'GET') return search.get('mine') === 'true' ? { items: [LIST[0], LIST[1]], total: 2 } : undefined;
      return advRoutes(path, search, method, route);
    };

    test('an employee with only the self-service permission sees their own advances (mine=true), can request and cancel a pending one, and cannot approve or pay', async ({ page }) => {
      const posted: Array<Record<string, unknown>> = [];
      const cancels: Array<Record<string, unknown>> = [];
      await openAdvances(page, selfRoutes((path, _s, method, route) => {
        if (path === '/api/finance/advances' && method === 'POST') { posted.push(route.request().postDataJSON()); return { id: 'new' }; }
        if (path === '/api/finance/advances/a1/cancel') { cancels.push(route.request().postDataJSON()); return { ...LIST[0], status: 'Cancelled' }; }
        return undefined;
      }));
      await expect(page.getByText('ADV-2026-00001')).toBeVisible({ timeout: 30_000 });
      await expect(page.getByText('ADV-2026-00003')).toHaveCount(0);
      await expect(page.getByRole('button', { name: locale === 'ar' ? 'موافقة' : 'Approve', exact: true })).toHaveCount(0);
      await expect(page.getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid' })).toHaveCount(0);
      await shot(page, 'loans-advances-self-service', locale);
      // Cancel is offered on the pending one only (the approved one is HR's or Finance's to cancel).
      await expect(page.getByRole('button', { name: /Cancel advance ADV-2026-00002|إلغاء السلفة ADV-2026-00002/ })).toHaveCount(0);
      await page.getByRole('button', { name: new RegExp(locale === 'ar' ? 'إلغاء السلفة ADV-2026-00001' : 'Cancel advance ADV-2026-00001') }).click();
      const dlg = page.getByRole('dialog');
      await dlg.locator('textarea').fill('Not needed any more');
      await dlg.getByRole('button', { name: locale === 'ar' ? 'إلغاء السلفة' : 'Cancel advance', exact: true }).click();
      await expect.poll(() => cancels.length, { timeout: 30_000 }).toBe(1);
      expect(cancels[0]).toEqual({ reason: 'Not needed any more' });
      await page.getByRole('button', { name: locale === 'ar' ? 'طلب سلفة جديد' : 'New Advance Request' }).click();
      await expect(page.getByRole('dialog').getByText(locale === 'ar' ? 'الموظف' : 'Employee', { exact: true })).toHaveCount(0);
      await page.getByRole('spinbutton').first().fill('800');
      await page.getByRole('button', { name: locale === 'ar' ? 'إرسال' : 'Submit' }).last().click();
      await expect.poll(() => posted.length, { timeout: 30_000 }).toBe(1);
      expect(posted[0].employeeIntId).toBeUndefined();
      expect(posted[0].repaymentType).toBe('OneTime');
    });

    test('a self-service list the server refuses shows the note instead of a failed register', async ({ page }) => {
      await openAdvances(page, selfRoutes(), async () => {
        await page.route('**/api/finance/advances?*', route => route.fulfill({ status: 403, json: { error: 'loans_read_forbidden', message: 'Not allowed', messageAr: 'غير مسموح' } }));
      });
      await expect(page.getByRole('note').filter({ hasText: locale === 'ar' ? 'يعتمد طلبك' : 'Your request is approved' })).toBeVisible({ timeout: 30_000 });
      await expect(page.getByRole('button', { name: locale === 'ar' ? 'إعادة المحاولة' : 'Retry' })).toHaveCount(0);
    });

    test('Finance records the payout of an approved advance: date, reference, notes, after a confirmation', async ({ page }) => {
      const bodies: Array<Record<string, unknown>> = [];
      await openAdvances(page, (path, search, method, route) => {
        if (path === '/api/finance/advances/a2/payout') { bodies.push(route.request().postDataJSON()); return { ...LIST[1], status: 'Active' }; }
        return advRoutes(path, search, method, route);
      });
      const row = page.getByRole('row').filter({ hasText: 'ADV-2026-00002' });
      await expect(row).toBeVisible({ timeout: 30_000 });
      await expect(page.getByRole('row').filter({ hasText: 'ADV-2026-00001' }).getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid' })).toHaveCount(0);
      await row.getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid' }).click();
      const modal = page.getByRole('dialog').filter({ hasText: 'ADV-2026-00002' });
      await modal.getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid' }).click();
      await expect(modal.getByText(locale === 'ar' ? 'أدخل مرجع الدفع' : 'Enter the bank or cash reference')).toBeVisible();
      expect(bodies).toHaveLength(0);
      await modal.getByTitle(locale === 'ar' ? 'مرجع الدفع' : 'Payment reference').fill('TRF-2026-1180');
      await modal.locator('textarea').fill('Paid by bank transfer');
      await shot(page, 'loans-advances-payout', locale);
      await modal.getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid' }).click();
      // The confirmation names the amount, the employee and the reference; nothing is sent until it is accepted.
      const confirm = page.getByRole('dialog').filter({ hasText: 'TRF-2026-1180' }).last();
      await expect(confirm).toBeVisible();
      expect(bodies).toHaveLength(0);
      await shot(page, 'loans-advances-payout-confirm', locale);
      await confirm.getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid', exact: true }).click();
      await expect.poll(() => bodies.length, { timeout: 30_000 }).toBe(1);
      expect(bodies[0].reference).toBe('TRF-2026-1180');
      expect(bodies[0].notes).toBe('Paid by bank transfer');
      expect(String(bodies[0].paidDate)).toMatch(/^\d{4}-\d{2}-\d{2}$/);
      await expect(page.getByTestId('advance-notice')).toContainText(locale === 'ar' ? 'تم تسجيل الصرف' : 'Payout recorded');
    });

    test('a refused payout shows the server\'s sentence in the viewer\'s language and stays open', async ({ page }) => {
      await openAdvances(page, advRoutes, async () => {
        await page.route('**/api/finance/advances/a2/payout', route => route.fulfill({ status: 400, json: { error: 'advance_payer_is_approver', message: 'Maker-checker control: the person who approved this advance cannot also record its payout.', messageAr: 'ضابط الفصل بين المهام: لا يمكن لمن اعتمد السلفة أن يسجل صرفها أيضًا.' } }));
      });
      await page.getByRole('row').filter({ hasText: 'ADV-2026-00002' }).getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid' }).click();
      const modal = page.getByRole('dialog').filter({ hasText: 'ADV-2026-00002' });
      await modal.getByTitle(locale === 'ar' ? 'مرجع الدفع' : 'Payment reference').fill('TRF-1');
      await modal.getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid' }).click();
      await page.getByRole('dialog').filter({ hasText: 'TRF-1' }).last().getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid', exact: true }).click();
      await expect(modal.getByText(locale === 'ar' ? 'ضابط الفصل بين المهام' : 'Maker-checker control', { exact: false })).toBeVisible({ timeout: 30_000 });
      await shot(page, 'loans-advances-payout-refused', locale);
    });

    test('HR can cancel but not pay out; cancelling asks for a reason and a refusal is shown', async ({ page }) => {
      const cancels: Array<Record<string, unknown>> = [];
      await openAdvances(page, (path, search, method, route) => {
        if (path === '/api/auth/me') return { ...USER, roles: ['HR Manager'], permissions: [...USER.permissions, 'loans.read', 'loans.write', 'loans.self', 'finance.read'] };
        return advRoutes(path, search, method, route);
      }, async () => {
        await page.route('**/api/finance/advances/a2/cancel', route => {
          cancels.push(route.request().postDataJSON());
          return route.fulfill({ status: 409, json: { error: 'invalid_advance_state', message: 'Only a pending or approved advance that has not been paid can be cancelled (current: Active).', messageAr: 'لا يمكن إلغاء إلا سلفة معلقة أو معتمدة لم تُصرف بعد (الحالة الحالية: Active).' } });
        });
      });
      const row = page.getByRole('row').filter({ hasText: 'ADV-2026-00002' });
      await expect(row).toBeVisible({ timeout: 30_000 });
      await expect(page.getByRole('button', { name: locale === 'ar' ? 'تسجيل الصرف' : 'Mark paid' })).toHaveCount(0);
      await row.getByRole('button', { name: /Cancel advance ADV-2026-00002|إلغاء السلفة ADV-2026-00002/ }).click();
      const dlg = page.getByRole('dialog');
      const confirmButton = dlg.getByRole('button', { name: locale === 'ar' ? 'إلغاء السلفة' : 'Cancel advance', exact: true });
      await expect(confirmButton).toBeDisabled();
      await dlg.locator('textarea').fill('Duplicate request');
      await shot(page, 'loans-advances-cancel-confirm', locale);
      await confirmButton.click();
      await expect.poll(() => cancels.length, { timeout: 30_000 }).toBe(1);
      expect(cancels[0]).toEqual({ reason: 'Duplicate request' });
      await expect(page.getByTestId('advance-action-error')).toContainText(locale === 'ar' ? 'لا يمكن إلغاء إلا سلفة' : 'Only a pending or approved advance');
    });

    test('a cancelled advance shows when and why in its detail', async ({ page }) => {
      await openAdvances(page);
      await page.getByRole('button', { name: new RegExp(locale === 'ar' ? 'عرض السلفة ADV-2026-00005' : 'View advance ADV-2026-00005') }).click();
      await expect(page.getByTestId('advance-cancelled')).toContainText('Employee withdrew the request', { timeout: 30_000 });
      await shot(page, 'loans-advance-detail-cancelled', locale);
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
