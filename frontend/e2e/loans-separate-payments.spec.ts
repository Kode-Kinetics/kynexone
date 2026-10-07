import { expect, test } from '@playwright/test';
import type { EmployeeLoan, LoanApproval, LoanPaymentBatch, LoanRepayment } from '../src/api/loans';

test('approved loan moves through a separate payment batch and repayment receipt', async ({ page }, info) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  let userId = 'hr-reviewer';
  let role = 'HR Manager';
  const loan: EmployeeLoan = { id: 'loan-1', employeeId: 'employee-1', employeeName: 'Amira Mansour', loanTypeId: 'type-1', loanTypeName: 'Personal', loanNumber: 'LN-001', requestedAmount: 1000, approvedAmount: 0, requestedInstallments: 2, approvedInstallments: 0, installmentAmount: 0, repaymentFrequency: 'Monthly', repaymentMethod: 'BankTransfer', currency: 'SAR', totalRepaid: 0, outstandingBalance: 0, status: 'Pending', notes: '', isLockedByPayroll: false, createdAtUtc: '2026-10-01T00:00:00Z' };
  const approval: LoanApproval = { id: 'approval-1', loanId: loan.id, stepOrder: 1, approverRole: 'HR Manager', approvedByName: '', status: 'Pending', comments: '' };
  let batch: LoanPaymentBatch | null = null;
  const repayments: LoanRepayment[] = [];
  await page.addInitScript(() => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone.theme', 'light'); });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const json = (body: unknown) => route.fulfill({ json: body });
    if (path === '/api/auth/me') return json({ id: userId, tenantId: 'tenant-1', tenantSlug: 'fixture', email: 'reviewer@example.test', fullName: `${role} Reviewer`, roles: [role], permissions: ['loans.read', 'loans.write'], companies: [] });
    if (['/api/features/disabled-keys', '/api/features/modules', '/api/notifications'].includes(path)) return json([]);
    if (path === '/api/tenant-admin/localization') return json({ currencyCode: 'USD' });
    if (path === '/api/finance/loans/types') return json([{ id: 'type-1', code: 'PERSONAL', nameEn: 'Personal', isInterestFree: true, maxAmount: 10000, maxInstallments: 12, repaymentFrequency: 'Monthly', interestRate: 0, minServiceMonths: 0, requiresApproval: true, isActive: true }]);
    if (path === '/api/finance/bonuses/types') return json([]);
    if (path.endsWith('/audit')) return json({});
    if (path === '/api/finance/loans') return json({ total: !url.searchParams.get('status') || url.searchParams.get('status') === loan.status ? 1 : 0, items: !url.searchParams.get('status') || url.searchParams.get('status') === loan.status ? [loan] : [] });
    if (path.endsWith('/approvals/approval-1/decide')) { expect(role).toBe('HR Manager'); approval.status = 'Approved'; Object.assign(loan, { status: 'Approved', approvedAmount: 1000, approvedInstallments: 2, installmentAmount: 500 }); return json({ loan }); }
    if (path === '/api/finance/loans/loan-1/repayments') {
      const body = request.postDataJSON();
      repayments.push({ id: 'receipt-1', ...body }); loan.totalRepaid += body.amount; loan.outstandingBalance -= body.amount;
      if (!loan.outstandingBalance) loan.status = 'Settled';
      return json({ loan, repayment: repayments[0] });
    }
    if (path === '/api/finance/loans/loan-1') return json({ loan, approvals: [approval], installments: [], auditLogs: [], glEntries: [], repayments });
    if (path === '/api/finance/loans/payment-batches') {
      if (request.method() === 'POST') { expect(request.postDataJSON().loanIds).toEqual(['loan-1']); batch = { id: 'batch-1', batchNumber: 'LP-001', currency: 'SAR', status: 'Draft', totalAmount: 1000, createdBy: userId, createdAtUtc: '2026-10-04T00:00:00Z', lines: [{ id: 'line-1', loanId: loan.id, loanNumber: loan.loanNumber, employeeName: loan.employeeName, amount: 1000 }] }; return json(batch); }
      return json(batch ? [batch] : []);
    }
    if (path.endsWith('/batch-1/approve')) { batch!.status = 'Approved'; return json(batch); }
    if (path.endsWith('/batch-1/confirm-paid')) { Object.assign(batch!, { status: 'Paid', paidDate: request.postDataJSON().paidDate, paymentReference: request.postDataJSON().reference }); Object.assign(loan, { status: 'Active', outstandingBalance: 1000 }); return json(batch); }
    if (path.endsWith('/batch-1/export')) return route.fulfill({ contentType: 'text/csv', body: 'Loan,Amount\nLN-001,1000' });
    if (path.endsWith('/batch-1')) return json(batch);
    return json({ items: [], total: 0 });
  });
  await page.goto('/loans');
  await expect(page).toHaveURL(/\/loans$/);
  await expect(page).toHaveTitle(/Kynex/i);
  await expect(page.getByRole('heading', { name: 'Loans, Advances & Bonuses' })).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: 'LN-001' })).toContainText('SAR');
  await page.getByRole('button', { name: 'New Loan Request', exact: true }).click();
  await expect(page.getByTitle('Repayment method')).toHaveValue('BankTransfer');
  await expect(page.getByText('Amount (employee company currency)', { exact: false })).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await page.getByRole('button', { name: 'Decide', exact: true }).click();
  await expect(page.getByText('Approved Amount (SAR)', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Submit Decision', exact: true }).click();
  await expect(page.getByText('Approved and awaiting disbursement.', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open Loan Payments' })).toHaveCount(0);
  userId = 'maker'; role = 'Finance';
  await page.reload();
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await page.getByRole('button', { name: 'Open Loan Payments' }).click();
  await page.getByRole('checkbox', { name: 'Select LN-001' }).check();
  await page.getByRole('button', { name: 'Create Payment Batch (1)' }).click();
  await expect(page.getByText('You created this batch.', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Approve Batch', exact: true })).toHaveCount(0);
  userId = 'checker';
  role = 'Finance Approver';
  await page.reload();
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  await page.getByRole('button', { name: 'View batch LP-001' }).click();
  await page.getByRole('button', { name: 'Approve Batch', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm Approval', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Record Completed Payment', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Download Payment Instructions' })).toBeVisible();
  userId = 'maker'; role = 'Finance';
  await page.reload();
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  await page.getByRole('button', { name: 'View batch LP-001' }).click();
  await expect(page.getByRole('button', { name: 'Record Completed Payment', exact: true })).toBeVisible();
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Download Payment Instructions' }).click();
  expect((await download).suggestedFilename()).toBe('LP-001-payment-instructions.csv');
  await page.getByRole('button', { name: 'Record Completed Payment', exact: true }).click();
  await expect(page.getByLabel('Bank reference', { exact: true })).toHaveAttribute('maxlength', '160');
  await page.getByLabel('Bank reference', { exact: true }).fill('BANK-100');
  await page.getByRole('button', { name: 'Confirm Payment Received by Employees' }).click();
  await expect(page.getByText(/Paid on .*Reference: BANK-100/)).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
  loan.status = 'Overdue';
  await page.getByRole('button', { name: 'Loans', exact: true }).click();
  await page.getByTitle('Filter by status').selectOption('Overdue');
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await expect(page.getByRole('dialog').getByText('Overdue', { exact: true }).last()).toBeVisible();
  await expect(page.getByRole('dialog')).toContainText('SAR');
  await page.getByRole('button', { name: 'Record Repayment', exact: true }).click();
  await expect(page.getByText('Received amount (SAR)', { exact: false })).toBeVisible();
  await expect(page.getByTitle('Receipt / bank reference')).toHaveAttribute('maxlength', '160');
  await page.getByTitle('Received amount').fill('1000');
  await page.getByTitle('Receipt / bank reference').fill('RECEIPT-100');
  await page.getByRole('button', { name: 'Record Received Payment', exact: true }).click();
  await expect(page.getByText(/RECEIPT-100/)).toBeVisible();
  await expect(page.getByRole('dialog').getByText('Settled', { exact: true })).toBeVisible();
  await expect(page.getByRole('dialog').getByText(/SAR.*1,000\.00/).last()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Record Repayment', exact: true })).toHaveCount(0);
  expect(errors).toEqual([]);
  if (process.env.LOAN_EVIDENCE_DIR) await page.screenshot({ path: `${process.env.LOAN_EVIDENCE_DIR}/loan-receipt-${info.project.name}.png` });
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
  await page.getByRole('button', { name: 'Loan Types', exact: true }).click();
  await page.getByRole('button', { name: 'New Loan Type', exact: true }).click();
  await expect(page.getByTitle('Repayment Frequency').locator('option')).toHaveText(['Monthly', 'Weekly', 'BiWeekly', 'Quarterly']);
  expect(errors).toEqual([]);
});
