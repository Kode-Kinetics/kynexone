import { expect, test, type APIRequestContext } from '@playwright/test';
import { apiLoginLive, tenantLoginLive } from '../helpers';
import { RASALMANAR_SLUG, tenantBySlug } from '../world';

// LIVE: browser -> Next frontend -> .NET API -> PostgreSQL, on a disposable stack provisioned by the e2e bootstrap.
// No mocks. An Admin sets the company's loan policy to single-operator in Setup, HR approves a loan over the API, and the
// Admin then creates the payment batch and self-approves it with a reason on the Loan Payments tab. HR then publishes
// a new version and the server keeps the Admin's setting.

const tenant = tenantBySlug(RASALMANAR_SLUG);
const admin = tenant.admin;
const hr = tenant.users.find(u => u.role === 'HR Manager')!;

const auth = (token: string) => ({ headers: { Authorization: `Bearer ${token}` } });
async function ok(resp: Awaited<ReturnType<APIRequestContext['get']>>, what: string) {
  expect(resp.ok(), `${what}: ${resp.status()} ${await resp.text()}`).toBeTruthy();
  return resp.json();
}

test('single-operator loan batch: policy in Setup, HR approval, self-approval with reason, all persisted', async ({ page, request }) => {
  test.setTimeout(180_000);
  const adminToken = await apiLoginLive(request, admin.email, admin.password, tenant.slug);
  const hrToken = await apiLoginLive(request, hr.email, hr.password, tenant.slug);

  const companies = await ok(await request.get('/api/companies?page=1&pageSize=50', auth(adminToken)), 'companies');
  const company = (companies.items ?? companies)[0];
  const employees = await ok(await request.get('/api/employees?page=1&pageSize=100', auth(adminToken)), 'employees');
  const employee = (employees.items ?? employees).find((e: { status: string }) => e.status === 'Active');
  expect(employee, 'an active employee in the fixture tenant').toBeTruthy();

  const code = `LIVE${Date.now().toString(36).toUpperCase()}`.slice(0, 12);
  const type = await ok(await request.post('/api/finance/loans/types', { ...auth(adminToken), data: {
    code, nameEn: `Live ${code}`, nameAr: null, maxAmount: 10000, maxInstallments: 12, repaymentFrequency: 'Monthly',
    isInterestFree: true, interestRate: 0, minServiceMonths: 0, requiresApproval: true } }), 'loan type');

  // ── Admin publishes the policy through the real Setup screen.
  await tenantLoginLive(page, admin.email, admin.password, tenant.slug);
  await page.goto('/setup?tab=loanPolicies');
  await page.getByRole('button', { name: 'New Policy Version', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('combobox', { name: 'Loan type', exact: true }).selectOption(type.id);
  await dialog.getByLabel('Policy name', { exact: true }).fill(`Single operator ${code}`);
  await dialog.getByLabel('Maximum loan amount', { exact: true }).fill('5000');
  await dialog.getByRole('checkbox', { name: 'Require completed probation' }).uncheck();
  const setting = dialog.getByRole('group', { name: 'Who approves loan payment batches' });
  await setting.getByLabel('Approval of payment batches').selectOption('SingleOperator');
  await setting.getByLabel('Largest batch one person may approve alone').fill('3000');
  await dialog.getByRole('button', { name: 'Create Policy Version', exact: true }).click();
  await expect(page.getByText(/Policy version \d+ created/)).toBeVisible();

  const policies = await ok(await request.get(`/api/finance/loans/policies?companyId=${company.id}&loanTypeId=${type.id}`, auth(adminToken)), 'policies');
  const active = policies.find((p: { isActive: boolean }) => p.isActive);
  expect(active.batchApproverMode).toBe('SingleOperator');
  expect(active.selfApprovalMaxAmount).toBe(3000);

  // ── A loan for the employee, approved by HR (a different person from the Admin who will pay it).
  const loan = await ok(await request.post('/api/finance/loans', { ...auth(adminToken), data: {
    employeeId: '00000000-0000-0000-0000-000000000000', employeeName: employee.fullName, loanTypeId: type.id,
    requestedAmount: 1200, requestedInstallments: 4, notes: 'live batch-approver test', employeeIntId: employee.id, repaymentMethod: 'BankTransfer' } }), 'loan');
  const detail = await ok(await request.get(`/api/finance/loans/${loan.id}`, auth(hrToken)), 'loan detail');
  for (const step of detail.approvals.sort((a: { stepOrder: number }, b: { stepOrder: number }) => a.stepOrder - b.stepOrder))
    await ok(await request.patch(`/api/finance/loans/${loan.id}/approvals/${step.id}/decide`, { ...auth(hrToken), data: { decision: 'Approved', comments: 'live' } }), 'HR approval');

  // ── The Admin creates the batch and approves it alone on the real Loan Payments tab.
  await page.goto('/loans');
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  await page.getByRole('checkbox', { name: `Select ${loan.loanNumber}` }).check();
  await page.getByRole('button', { name: 'Create Payment Batch (1)' }).click();
  const batchDialog = page.getByRole('dialog');
  await expect(batchDialog).toContainText('lets you approve it yourself');
  await batchDialog.getByRole('button', { name: 'Approve Batch', exact: true }).click();
  await batchDialog.getByLabel('Why are you approving your own batch?').fill('Only finance officer at Ras Al-Manar this month');
  await batchDialog.getByRole('button', { name: 'Confirm Approval', exact: true }).click();
  await expect(batchDialog.getByRole('button', { name: 'Download Payment Instructions' })).toBeVisible();
  await expect(batchDialog).toContainText('Status: Approved');

  // ── What the database now holds, read back through the API.
  const batches = await ok(await request.get('/api/finance/loans/payment-batches', auth(adminToken)), 'batches');
  const batch = batches.find((b: { lines: { loanId: string }[] }) => b.lines.some(l => l.loanId === loan.id));
  expect(batch.status).toBe('Approved');
  expect(batch.approvedBy).toBe(batch.createdBy);
  const after = await ok(await request.get(`/api/finance/loans/${loan.id}`, auth(adminToken)), 'loan after');
  const self = after.auditLogs.find((a: { action: string }) => a.action === 'PaymentBatchSelfApproved');
  expect(self, 'the self-approval is in the loan audit trail').toBeTruthy();
  expect(JSON.stringify(self)).toContain('Only finance officer at Ras Al-Manar this month');

  // ── HR may publish other terms but cannot reopen or widen the setting; the server says so.
  const hrWiden = await request.post('/api/finance/loans/policies', { ...auth(hrToken), data: {
    companyId: company.id, loanTypeId: type.id, policyName: 'HR widening', maxAmount: 5000, maxInstallments: 12,
    allowedEmploymentStatuses: ['Active'], allowedRepaymentMethods: ['BankTransfer'], batchApproverMode: 'SingleOperator', selfApprovalMaxAmount: 0 } });
  expect(hrWiden.status()).toBe(403);
  const hrKeep = await request.post('/api/finance/loans/policies', { ...auth(hrToken), data: {
    companyId: company.id, loanTypeId: type.id, policyName: 'HR raises maximum', maxAmount: 6000, maxInstallments: 12,
    allowedEmploymentStatuses: ['Active'], allowedRepaymentMethods: ['BankTransfer'], batchApproverMode: 'SingleOperator', selfApprovalMaxAmount: 3000 } });
  const kept = await ok(hrKeep, 'HR keeps the setting');
  expect(kept.batchApproverMode).toBe('SingleOperator');
  expect(kept.maxAmount).toBe(6000);
});
