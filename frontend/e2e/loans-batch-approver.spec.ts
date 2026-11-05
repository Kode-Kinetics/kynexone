import { expect, test, type Page, type Route } from '@playwright/test';

// The client-configurable batch approver and the 502 guard on the Loan Payments tab, against the real UI with a
// mocked API. Backend rules (Admin-only relaxing, borrower never approves, the cap) are proven in the .NET suites;
// this proves the screens read and send the same wire names, and that a gateway page is never shown as markup.

const loanType = { id: 'type-1', nameEn: 'Personal', code: 'PERSONAL', isInterestFree: true, isActive: true, maxAmount: 10000, maxInstallments: 24, repaymentFrequency: 'Monthly', minServiceMonths: 0 };
const approvedLoan = { id: 'loan-1', employeeId: 'employee-1', employeeName: 'Amira Mansour', loanTypeId: 'type-1', loanTypeName: 'Personal', loanNumber: 'LN-001', requestedAmount: 1000, approvedAmount: 1000, requestedInstallments: 2, approvedInstallments: 2, installmentAmount: 500, repaymentFrequency: 'Monthly', repaymentMethod: 'BankTransfer', currency: 'SAR', totalRepaid: 0, outstandingBalance: 0, status: 'Approved', notes: '', isLockedByPayroll: false, createdAtUtc: '2026-10-01T00:00:00Z' };
const RENDER_502 = '<!DOCTYPE html> <html lang="en"> <head><title>502</title></head><body><h1>Bad Gateway</h1><div class="request-id">Request ID: abc</div></body></html>';

type Handler = (path: string, method: string, body: any, route: Route) => unknown;

async function boot(page: Page, user: { id: string; role: string }, handler: Handler, start = '/loans') {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone.theme', 'light'); });
  await page.route('**/api/**', async route => {
    const request = route.request(); const url = new URL(request.url()); const path = url.pathname;
    const response = await handler(path, request.method(), request.postData() ? request.postDataJSON() : null, route);
    if (response === 'handled') return;
    if (response !== undefined) return route.fulfill({ json: response });
    if (path === '/api/auth/me') return route.fulfill({ json: { id: user.id, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: `${user.role} User`, roles: [user.role], permissions: ['loans.read', 'loans.write', 'loans.policy_manage'], companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] } });
    if (path === '/api/tenant-admin/localization') return route.fulfill({ json: { currencyCode: 'SAR' } });
    if (path === '/api/finance/loans/types') return route.fulfill({ json: [loanType] });
    if (path === '/api/finance/loans/types/offered') return route.fulfill({ json: [{ loanTypeId: loanType.id, code: loanType.code, nameEn: loanType.nameEn, nameAr: '', gradeLimited: false, offered: true, reasonCode: null, reasonText: null }] });
    if (path === '/api/finance/loans/offerings') return route.fulfill({ json: [{ loanTypeId: loanType.id, code: loanType.code, nameEn: loanType.nameEn, nameAr: '', gradeLimited: false, companyId: 'company-1', offered: true, source: 'LoanTypeBaseline', policyId: null, policyVersion: null, detachedFromGroupPolicy: false }] });
    if (path === '/api/finance/loans/grade-limits') return route.fulfill({ json: [] });
    if (path === '/api/finance/loans') return route.fulfill({ json: { items: [approvedLoan], total: 1 } });
    if (path.endsWith('/audit')) return route.fulfill({ json: {} });
    if (path.endsWith('/changes') || path.endsWith('/corrections') || path.includes('/features/') || path === '/api/notifications' || path.endsWith('/bonuses/types')) return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto(start);
  return errors;
}

const draft = (createdBy: string, selfApprovalAllowed: boolean) => ({
  id: 'batch-1', batchNumber: 'LP-001', currency: 'SAR', status: 'Draft', totalAmount: 1000, createdBy, createdAtUtc: '2026-11-01T00:00:00Z', selfApprovalAllowed,
  lines: [{ id: 'line-1', loanId: 'loan-1', loanNumber: 'LN-001', employeeName: 'Amira Mansour', bankName: 'Bank', iban: 'SA4420000001234567891234', amount: 1000, status: 'Pending' }],
});

test('a 502 gateway page reads as one plain sentence on Loan Payments, and Retry recovers', async ({ page }) => {
  // Down until the test lowers it: dev StrictMode loads twice, so a one-shot failure would be retried away unseen.
  let gatewayDown = true;
  const errors = await boot(page, { id: 'maker', role: 'Finance' }, (path, _method, _body, route) => {
    if (path === '/api/finance/loans/payment-batches') {
      if (gatewayDown) { void route.fulfill({ status: 502, contentType: 'text/html', body: RENDER_502 }); return 'handled'; }
      return [];
    }
  });
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  const alert = page.getByRole('alert').filter({ hasText: 'temporarily unavailable' });
  await expect(alert).toBeVisible();
  await expect(page.getByText('DOCTYPE', { exact: false })).toHaveCount(0);
  await expect(page.getByText('Bad Gateway', { exact: false })).toHaveCount(0);
  gatewayDown = false;
  await alert.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(alert).toHaveCount(0);
  await expect(page.getByRole('checkbox', { name: 'Select LN-001' })).toBeVisible();
  await expect(page.getByText('No loan payment batches yet.')).toBeVisible();
  expect(errors).toEqual([]);
});

test('the creator self-approves only when the company policy allows it, and must give a reason', async ({ page }) => {
  const batch: any = draft('maker', true);
  const approvals: any[] = [];
  const errors = await boot(page, { id: 'maker', role: 'Finance' }, (path, method, body) => {
    if (path === '/api/finance/loans/payment-batches') return [batch];
    if (path.endsWith('/batch-1/approve')) { approvals.push(body); Object.assign(batch, { status: 'Approved', selfApprovalAllowed: false }); return batch; }
    if (path.endsWith('/batch-1')) return batch;
  });
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  await page.getByRole('button', { name: 'View batch LP-001' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('lets you approve it yourself');
  await dialog.getByRole('button', { name: 'Approve Batch', exact: true }).click();
  const reason = dialog.getByLabel('Why are you approving your own batch?');
  await expect(reason).toHaveAttribute('maxlength', '500');
  await reason.fill('short');
  await dialog.getByRole('button', { name: 'Confirm Approval', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('at least 10 characters');
  expect(approvals).toHaveLength(0);
  await reason.fill('Sole finance officer this month');
  await dialog.getByRole('button', { name: 'Confirm Approval', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Download Payment Instructions' })).toBeVisible();
  expect(approvals).toEqual([{ reason: 'Sole finance officer this month' }]);
  expect(errors).toEqual([]);
});

test('without the policy the creator gets no Approve button, and another user approves with no reason', async ({ page }) => {
  let user = { id: 'maker', role: 'Finance' };
  const batch: any = draft('maker', false);
  const approvals: any[] = [];
  const errors = await boot(page, user, (path, method, body) => {
    if (path === '/api/auth/me') return { id: user.id, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'User', roles: [user.role], permissions: ['loans.read', 'loans.write'], companies: [] };
    if (path === '/api/finance/loans/payment-batches') return [batch];
    if (path.endsWith('/batch-1/approve')) { approvals.push(body); batch.status = 'Approved'; return batch; }
    if (path.endsWith('/batch-1')) return batch;
  });
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  await page.getByRole('button', { name: 'View batch LP-001' }).click();
  await expect(page.getByRole('dialog')).toContainText('Another Finance user must approve it.');
  await expect(page.getByRole('dialog').getByRole('button', { name: 'Approve Batch', exact: true })).toHaveCount(0);

  user = { id: 'checker', role: 'Finance Approver' };
  await page.reload();
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  await page.getByRole('button', { name: 'View batch LP-001' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Approve Batch', exact: true }).click();
  await expect(dialog.getByLabel('Why are you approving your own batch?')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Confirm Approval', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Download Payment Instructions' })).toBeVisible();
  expect(approvals).toEqual([{}]);
  expect(errors).toEqual([]);
});

test('an Admin sets single-operator with a cap, and the policy form sends both', async ({ page }) => {
  let posted: any;
  const errors = await boot(page, { id: 'admin', role: 'Admin' }, (path, method, body) => {
    if (path.endsWith('/policies') && method === 'GET') return posted ? [{ ...posted, id: 'policy-1', version: 1, createdAtUtc: '2026-11-01T00:00:00Z' }] : [];
    if (path.endsWith('/policies') && method === 'POST') { posted = body; return { ...body, id: 'policy-1', version: 1, createdAtUtc: '2026-11-01T00:00:00Z' }; }
  }, '/setup?tab=loanPolicies');
  await page.getByRole('button', { name: 'New Policy Version', exact: true }).click();
  const setting = page.getByRole('group', { name: 'Who approves loan payment batches' });
  await expect(setting).toBeEnabled();
  await expect(setting.getByLabel('Approval of payment batches')).toHaveValue('SeparateApprover');
  await expect(setting.getByLabel('Largest batch one person may approve alone')).toHaveCount(0);
  await page.getByLabel('Policy name', { exact: true }).fill('Single finance officer');
  await setting.getByLabel('Approval of payment batches').selectOption('SingleOperator');
  await setting.getByLabel('Largest batch one person may approve alone').fill('2500');
  await page.getByRole('button', { name: 'Create Policy Version', exact: true }).click();
  await expect(page.getByText(/Policy version 1 created/)).toBeVisible();
  expect(posted.batchApproverMode).toBe('SingleOperator');
  expect(posted.selfApprovalMaxAmount).toBe(2500);
  expect(errors).toEqual([]);
});

test('HR sees the setting read-only, and a new version keeps the active one instead of resetting it', async ({ page }) => {
  let posted: any;
  const active = { companyId: 'company-1', loanTypeId: 'type-1', policyName: 'Current', maxAmount: 5000, maxTotalOutstanding: 0, maxMultiplierOfSalary: 0, maxInstallmentPercentOfSalary: 0, minServiceMonths: 0, maxInstallments: 12, minInstallments: 1, requireProbationCompleted: false, blockDuringNotice: true, blockOnOverdue: true, allowedEmploymentStatuses: ['Active'], allowedContractTypes: [], allowedRepaymentMethods: ['BankTransfer'], allowedRepaymentFrequencies: ['Monthly'], maxConcurrentLoans: 1, cooldownMonthsAfterRepayment: 0, additionalApprovalThreshold: 0, additionalApproverRole: 'HR Director', allowExceptions: false, allowEarlySettlement: true, allowRescheduling: false, isActive: true, isOffered: true, batchApproverMode: 'SingleOperator', selfApprovalMaxAmount: 2500, id: 'policy-1', version: 1, createdAtUtc: '2026-11-01T00:00:00Z' };
  const errors = await boot(page, { id: 'hr', role: 'HR Manager' }, (path, method, body) => {
    if (path.endsWith('/policies') && method === 'GET') return [active];
    if (path.endsWith('/policies') && method === 'POST') { posted = body; return { ...body, id: 'policy-2', version: 2, createdAtUtc: '2026-11-02T00:00:00Z' }; }
  }, '/setup?tab=loanPolicies');
  await page.getByRole('button', { name: 'New Policy Version', exact: true }).click();
  const setting = page.getByRole('group', { name: 'Who approves loan payment batches' });
  await expect(setting).toHaveAttribute('disabled', '');
  await expect(setting.getByLabel('Approval of payment batches')).toBeDisabled();
  await expect(setting).toContainText('Only an Admin can change this setting');
  await expect(setting.getByLabel('Approval of payment batches')).toHaveValue('SingleOperator');
  await page.getByLabel('Policy name', { exact: true }).fill('Raised maximum');
  await page.getByLabel('Maximum loan amount', { exact: true }).fill('9000');
  await page.getByRole('button', { name: 'Create Policy Version', exact: true }).click();
  await expect(page.getByText(/Policy version 2 created/)).toBeVisible();
  expect(posted.batchApproverMode).toBe('SingleOperator');
  expect(posted.selfApprovalMaxAmount).toBe(2500);
  expect(posted.maxAmount).toBe(9000);
  expect(errors).toEqual([]);
});

test('a new version reads the chosen type\'s own active setting, whatever the table is filtered to or which row it starts from', async ({ page }) => {
  // The table only holds the filtered type's rows, so the form must ask the server for the type chosen in the modal;
  // an old inactive row must not carry its stale setting into the new version either. Either slip silently rewrites
  // the setting, and HR cannot see or correct it because the fieldset is read-only for them.
  const housing = { ...loanType, id: 'type-2', nameEn: 'Housing', code: 'HOUSING' };
  const base = { companyId: 'company-1', policyName: 'Current', maxAmount: 5000, maxTotalOutstanding: 0, maxMultiplierOfSalary: 0, maxInstallmentPercentOfSalary: 0, minServiceMonths: 0, maxInstallments: 12, minInstallments: 1, requireProbationCompleted: false, blockDuringNotice: true, blockOnOverdue: true, allowedEmploymentStatuses: ['Active'], allowedContractTypes: [], allowedRepaymentMethods: ['BankTransfer'], allowedRepaymentFrequencies: ['Monthly'], maxConcurrentLoans: 1, cooldownMonthsAfterRepayment: 0, additionalApprovalThreshold: 0, additionalApproverRole: 'HR Director', allowExceptions: false, allowEarlySettlement: true, allowRescheduling: false, isOffered: true, createdAtUtc: '2026-11-01T00:00:00Z' };
  const all = [
    { ...base, id: 'p1-old', loanTypeId: 'type-1', version: 1, isActive: false, batchApproverMode: 'SeparateApprover', selfApprovalMaxAmount: 0 },
    { ...base, id: 'p1', loanTypeId: 'type-1', version: 2, isActive: true, batchApproverMode: 'SingleOperator', selfApprovalMaxAmount: 900 },
    { ...base, id: 'p2', loanTypeId: 'type-2', version: 1, isActive: true, batchApproverMode: 'SingleOperator', selfApprovalMaxAmount: 2500 },
  ];
  const posted: any[] = [];
  const errors = await boot(page, { id: 'hr', role: 'HR Manager' }, (path, method, body, route) => {
    if (path === '/api/finance/loans/types') return [loanType, housing];
    if (path.endsWith('/policies') && method === 'GET') {
      const typeFilter = new URL(route.request().url()).searchParams.get('loanTypeId');
      return all.filter(p => !typeFilter || p.loanTypeId === typeFilter);
    }
    if (path.endsWith('/policies') && method === 'POST') { posted.push(body); return { ...body, id: `new-${posted.length}`, version: 9, createdAtUtc: '2026-11-02T00:00:00Z' }; }
  }, '/setup?tab=loanPolicies');
  const mode = page.getByRole('group', { name: 'Who approves loan payment batches' }).getByLabel('Approval of payment batches');

  await page.getByRole('combobox', { name: 'Loan type' }).filter({ has: page.getByRole('option', { name: 'All types' }) }).selectOption('type-1');
  await page.getByRole('button', { name: 'New Policy Version', exact: true }).click();
  await expect(mode).toHaveValue('SingleOperator');
  await page.getByRole('dialog').getByLabel('Loan type').selectOption('type-2');
  await page.getByLabel('Policy name', { exact: true }).fill('Housing v2');
  await page.getByRole('button', { name: 'Create Policy Version', exact: true }).click();
  await expect(page.getByText(/Policy version 9 created/)).toBeVisible();
  expect(posted[0]).toMatchObject({ loanTypeId: 'type-2', batchApproverMode: 'SingleOperator', selfApprovalMaxAmount: 2500 });

  await page.getByRole('row').filter({ hasText: 'v1' }).filter({ hasText: 'Inactive' }).getByRole('button', { name: 'Review / New Version' }).click();
  await expect(mode).toHaveValue('SingleOperator');
  await page.getByRole('button', { name: 'Create Policy Version', exact: true }).click();
  await expect.poll(() => posted.length).toBe(2);
  expect(posted[1]).toMatchObject({ loanTypeId: 'type-1', batchApproverMode: 'SingleOperator', selfApprovalMaxAmount: 900 });
  expect(errors).toEqual([]);
});
