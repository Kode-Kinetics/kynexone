import { expect, test, type Page } from '@playwright/test';

const base = '/api/compensation/benefits';
const policy = { delivery: 'Reimbursement', amount: 2000, frequency: 'Monthly', paymentMonth: null, prorate: false, salaryComponentId: 'earning', receiptRequired: true, receiptLabel: 'School invoice', claimWindowDays: 365, instructions: 'Upload the school invoice for eligible tuition.' };
const plan = { id: 'education', companyId: 'company-benefits', code: 'EDU', name: 'Child Education', planType: 'Education', classification: 'Discretionary', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null, requiresEnrollment: true, isActive: true, paymentPolicy: policy, policyVersion: 1 };
const balance = { canClaim: true, maximumAmount: 2000, reservedAmount: 200, approvedAmount: 300, remainingAmount: 1500, periodFrom: '2026-01-01', periodTo: '2026-12-31' };
const enrollment = { id: 'enrolled', benefitPlanId: plan.id, employeeId: 42, employeeName: 'Alex Morgan', companyId: plan.companyId, coverageTier: 'Employee', entitlementTier: 'Education', maximumBenefitAmount: 5000, requestedBenefitAmount: null, limitPeriod: 'Annual', effectiveFrom: '2026-01-01', effectiveTo: null, status: 'Active', assignmentSource: 'GradeDefault', hasException: false, exceptionReason: null, updatedAtUtc: '2026-10-09T12:00:00Z', planName: plan.name, planType: plan.planType, currency: 'SAR', effectiveStatus: 'Current', paymentPolicy: policy, claimBalance: balance, deductions: [], currentEmployeeAmount: null, currentEmployerAmount: null };

async function boot(page: Page, options: { readonly?: boolean; noReceipt?: boolean; missingRoute?: boolean; emptyMappings?: boolean } = {}) {
  const saves: Record<string, unknown>[] = [], submits: Record<string, unknown>[] = [], mappings: Record<string, unknown>[] = [], workflows: Record<string, unknown>[] = [];
  const balances: string[] = []; const uploads: string[] = []; const errors: string[] = [];
  let claim: Record<string, unknown> | null = null; let withdrawals = 0;
  page.on('pageerror', err => errors.push(err.message));
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'benefit-policy-fixture'));
  await page.route('**/api/**', async route => {
    const req = route.request(), url = new URL(req.url()), path = url.pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (path === '/api/auth/me') return reply({ id: 'hr-user', tenantId: 'benefit-tenant', fullName: 'Alex Morgan', roles: ['HR Manager'], permissions: ['employees.read', 'employees.write', 'employees.approve', 'payroll.read', 'payroll.write', 'approvals.manage', 'ess.read', ...(options.readonly ? [] : ['ess.write'])], companies: [{ id: plan.companyId, name: 'Benefits Company', countryCode: 'SA', isActive: true }], isGroupScope: false, accountType: 'SingleCompany' });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === `${base}/plans` && req.method() === 'POST') { saves.push(req.postDataJSON()); return reply({ id: 'new-plan', ...req.postDataJSON(), policyVersion: 1 }); }
    if (path === `${base}/plans`) return reply([plan]);
    if (path === `${base}/plans/${plan.id}/eligibility`) return reply([]);
    if (path === `${base}/enrollments`) return reply([]);
    if (path === `${base}/payment-components` && req.method() === 'POST') { mappings.push(req.postDataJSON()); return reply({ id: 'new-component', ...req.postDataJSON(), isActive: true, salaryStructureId: null }); }
    if (path === `${base}/payment-components`) return reply(options.emptyMappings ? [] : [{ id: 'earning', code: 'BEN_EARN', name: 'Benefit earning', componentType: 'Earning', isActive: true, salaryStructureId: null, isTaxable: false }, { id: 'deduction', code: 'BEN_DED', name: 'Benefit deduction', componentType: 'Deduction', isActive: true, salaryStructureId: null, isTaxable: false }, { id: 'bound', code: 'BOUND', name: 'Bound salary component', componentType: 'Earning', isActive: true, salaryStructureId: 'salary', isTaxable: false }]);
    if (path === '/api/ess/benefits') return reply({ employeeId: 42, enrollments: [{ ...enrollment, paymentPolicy: { ...policy, receiptRequired: !options.noReceipt }, claimBalance: claim && claim.status === 'Pending' ? { ...balance, reservedAmount: 450, remainingAmount: 1250 } : balance }] });
    if (path.endsWith('/claim-balance')) { balances.push(url.searchParams.get('expenseDate')!); return reply({ ...balance, remainingAmount: url.searchParams.get('expenseDate') === '2026-02-01' ? 100 : 1500 }); }
    if (path === '/api/ess/benefits/receipts') { uploads.push(req.postDataBuffer()?.toString() ?? ''); return reply({ id: 'receipt-1', fileName: 'school.pdf', contentType: 'application/pdf', versionNumber: 1 }); }
    if (path === '/api/ess/benefits/claims' && req.method() === 'POST') {
      submits.push(req.postDataJSON());
      if (options.missingRoute) return reply({ code: 'approval_route_not_configured', message: 'No approval route configured.' }, 422);
      claim = { id: 'claim-1', status: 'Pending', employeeId: 42, employeeName: 'Alex Morgan', benefitPlanId: plan.id, planName: plan.name, currency: 'SAR', ...req.postDataJSON(), receipts: options.noReceipt ? [] : [{ id: 'receipt-1', fileName: 'school.pdf', contentType: 'application/pdf', versionNumber: 1 }], createdAtUtc: '2026-10-09T12:00:00Z', approvalRequestId: 'claim-1', payrollRunId: null, settlementStatus: 'AwaitingApproval', reservedAmount: 450, remainingAmount: 1250, canWithdraw: true };
      return reply(claim, 201);
    }
    if (path === '/api/ess/benefits/claims/claim-1/withdraw') { withdrawals++; claim = { ...claim, status: 'Cancelled', settlementStatus: 'Withdrawn', canWithdraw: false }; return reply(claim); }
    if (path.endsWith('/receipts/receipt-1/download')) return route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.4 receipt' });
    if (path === '/api/ess/benefits/claims') return reply(claim ? [claim] : []);
    if (path === '/api/approval-workflows' && req.method() === 'POST') { workflows.push(req.postDataJSON()); return reply({ id: 'claim-workflow', ...req.postDataJSON() }); }
    if (path.includes('/features/') || path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  return { saves, submits, mappings, workflows, balances, uploads, errors, withdrawals: () => withdrawals };
}

async function openClaim(page: Page) {
  await page.goto('/ess/benefits');
  await expect(page.getByTestId('benefit-claims')).toContainText('1,500.00');
  await page.getByRole('button', { name: 'Submit a claim', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: `Claim ${plan.name}`, exact: true });
  await expect(dialog.getByRole('button', { name: 'Review claim' })).toBeEnabled();
  await dialog.getByLabel('Claim amount (SAR)', { exact: true }).fill('250');
  await dialog.getByLabel('Invoice reference', { exact: true }).fill('INV-SCHOOL-42');
  await dialog.getByLabel('What was this expense for?', { exact: true }).fill('Tuition for the current school term.');
  return dialog;
}

test('custom benefit policy shows only applicable payment controls and saves a dedicated deduction mapping', async ({ page }) => {
  const { saves, mappings, errors } = await boot(page, { emptyMappings: true });
  await page.goto('/benefits');
  await page.getByRole('button', { name: 'Add benefit plan', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New benefit plan' });
  await dialog.getByLabel('Plan code').fill('GYM'); await dialog.getByLabel('Plan name').fill('Gym membership');
  await dialog.getByLabel('How this benefit works', { exact: true }).selectOption('PayrollDeduction');
  await dialog.getByLabel('Payment amount (SAR)', { exact: true }).fill('125');
  await dialog.getByLabel('Payment frequency', { exact: true }).selectOption('Annual');
  await dialog.getByLabel('Payment month', { exact: true }).selectOption('6');
  await expect(dialog.getByLabel('Require receipt or invoice')).toHaveCount(0);
  await expect(dialog.getByLabel('Prorate for covered days')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Add payroll mapping', exact: true }).click();
  await expect(dialog.getByLabel('Taxable', { exact: true })).toHaveCount(0);
  await dialog.getByLabel('Mapping name').fill('Gym deduction'); await dialog.getByLabel('Mapping code').fill('GYM_DED');
  await dialog.getByRole('button', { name: 'Create payroll mapping', exact: true }).click();
  await expect(dialog.getByLabel('Payroll mapping', { exact: true })).toHaveValue('new-component');
  await dialog.getByRole('button', { name: 'Create plan', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(mappings[0]).toMatchObject({ componentType: 'Deduction', isTaxable: false });
  expect(saves[0]).toMatchObject({ name: 'Gym membership', paymentPolicy: { delivery: 'PayrollDeduction', amount: 125, frequency: 'Annual', paymentMonth: 6, prorate: false, salaryComponentId: 'new-component', receiptRequired: false } });
  expect(errors).toEqual([]);
});

test('reimbursement policy filters bound mappings and clears money when changed to coverage', async ({ page }) => {
  const { saves } = await boot(page);
  await page.goto('/benefits'); await page.getByRole('button', { name: 'Add benefit plan', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New benefit plan' });
  await dialog.getByLabel('Plan code').fill('COVER'); await dialog.getByLabel('Plan name').fill('Wellbeing cover');
  await dialog.getByLabel('How this benefit works', { exact: true }).selectOption('Reimbursement');
  await expect(dialog.getByLabel('Require receipt or invoice')).toBeChecked();
  await expect(dialog.getByLabel('Payroll mapping', { exact: true }).getByRole('option', { name: /Bound salary component/ })).toHaveCount(0);
  await dialog.getByLabel('Claim limit (SAR)').fill('500'); await dialog.getByLabel('Payroll mapping', { exact: true }).selectOption('earning');
  await dialog.getByLabel('How this benefit works', { exact: true }).selectOption('Coverage');
  await expect(dialog.getByLabel('Payroll mapping', { exact: true })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Create plan', exact: true }).click();
  expect(saves[0]).toMatchObject({ paymentPolicy: { delivery: 'Coverage', amount: null, salaryComponentId: null, receiptRequired: false } });
});

test('employee uploads a receipt, reviews and submits a capped claim, downloads evidence and withdraws the pending request', async ({ page }, info) => {
  const { submits, uploads, withdrawals, errors } = await boot(page);
  const dialog = await openClaim(page);
  await dialog.getByRole('button', { name: 'Review claim', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Attach the required receipt');
  await dialog.locator('input[type=file]').setInputFiles({ name: 'school.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4 synthetic school invoice') });
  await expect(dialog).toContainText('school.pdf');
  await dialog.getByRole('button', { name: 'Review claim', exact: true }).click();
  await expect(dialog).toContainText('Payment follows the payroll process after approval.');
  await dialog.getByRole('button', { name: 'Submit claim', exact: true }).click();
  const claims = page.getByTestId('benefit-claims');
  await expect(claims).toContainText('Awaiting approval'); await expect(claims).toContainText('1,250.00');
  await claims.locator('summary').click();
  const download = page.waitForEvent('download'); await claims.getByRole('button', { name: 'school.pdf', exact: true }).click(); await download;
  await page.screenshot({ path: `/tmp/benefit-claims-${info.project.name}.png`, fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await claims.getByRole('button', { name: 'Withdraw claim', exact: true }).click();
  await expect(claims).toContainText('Withdrawn');
  expect(uploads).toHaveLength(1); expect(submits).toHaveLength(1); expect(withdrawals()).toBe(1);
  expect(submits[0]).toMatchObject({ enrollmentId: 'enrolled', amount: 250, documentIds: ['receipt-1'] });
  expect(submits[0]).not.toHaveProperty('employeeId'); expect(errors).toEqual([]);
});

test('selected expense date refreshes the actual balance and over-limit submission is blocked', async ({ page }) => {
  const { submits, balances } = await boot(page, { noReceipt: true });
  const dialog = await openClaim(page);
  await dialog.getByLabel('Expense date', { exact: true }).fill('2026-02-01');
  await expect(dialog.getByLabel('Claim amount (SAR)', { exact: true })).toHaveAttribute('max', '100');
  await dialog.getByRole('button', { name: 'Review claim', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Submit claim', exact: true })).toHaveCount(0);
  expect(balances).toContain('2026-02-01'); expect(submits).toEqual([]);
});

test('read-only employee sees benefit policy and balance without claim mutation controls', async ({ page }) => {
  await boot(page, { readonly: true }); await page.goto('/ess/benefits');
  await expect(page.getByTestId('benefit-claims')).toContainText('Available to claim');
  await expect(page.getByTestId('benefit-payment-summary')).toContainText('Reimburse approved claims');
  await expect(page.getByRole('button', { name: 'Submit a claim', exact: true })).toHaveCount(0);
});

test('missing claim approval route preserves submission and offers employee contact guidance', async ({ page }) => {
  const { submits } = await boot(page, { noReceipt: true, missingRoute: true });
  const dialog = await openClaim(page); await dialog.getByRole('button', { name: 'Review claim', exact: true }).click();
  await dialog.getByRole('button', { name: 'Submit claim', exact: true }).click();
  await expect(dialog).toContainText('Ask an administrator to configure benefit claim approvals');
  await expect(dialog.getByRole('link', { name: 'Configure claim approvals' })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(dialog.getByLabel('Invoice reference', { exact: true })).toHaveValue('INV-SCHOOL-42'); expect(submits).toHaveLength(1);
});

test('claim approval setup uses an independent BenefitClaim workflow', async ({ page }) => {
  const { workflows } = await boot(page); await page.goto('/benefits#benefit-claim-approval');
  const dialog = page.getByRole('dialog', { name: 'Benefit claim approvals', exact: true });
  await expect(dialog.getByLabel('Approver role for step 1')).toHaveValue('HR Manager');
  await dialog.getByRole('button', { name: 'Save approval route', exact: true }).click();
  await expect(dialog.getByRole('status')).toHaveText('Approval route saved.'); expect(workflows[0]).toMatchObject({ entityName: 'BenefitClaim', code: 'BENEFIT_CLAIM_DEFAULT' });
});


test('approved, payroll-included and paid claims remain distinct', async ({ page }) => {
  await boot(page);
  await page.route('**/api/ess/benefits/claims', route => route.fulfill({ json: ['AwaitingPayroll', 'IncludedInPayroll', 'Paid'].map((settlementStatus, index) => ({ id: `settled-${index}`, status: 'Approved', employeeId: 42, employeeName: 'Alex Morgan', enrollmentId: enrollment.id, benefitPlanId: plan.id, planName: plan.name, currency: 'SAR', amount: 100, expenseDate: '2026-10-01', invoiceReference: `STATUS-${index}`, description: 'Approved tuition claim.', receipts: [], approvalRequestId: `settled-${index}`, payrollRunId: index ? 'run' : null, settlementStatus, reservedAmount: 0, remainingAmount: 1500, canWithdraw: false })) }));
  await page.goto('/ess/benefits');
  const claims = page.getByTestId('benefit-claims');
  await expect(claims.getByText('Approved · awaiting payroll', { exact: true })).toBeVisible();
  await expect(claims.getByText('Included in payroll', { exact: true })).toBeVisible();
  await expect(claims.getByText('Paid', { exact: true })).toHaveCount(1);
  await expect(claims.getByRole('button', { name: 'Withdraw claim', exact: true })).toHaveCount(0);
});


test('saved benefit policy keeps its payment method while future assignment terms remain editable', async ({ page }) => {
  await boot(page); await page.goto('/benefits');
  await page.getByText(plan.name, { exact: true }).first().click();
  await page.getByRole('button', { name: 'Edit plan', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: `Edit plan ${plan.code}`, exact: true });
  await expect(dialog.getByLabel('How this benefit works', { exact: true })).toHaveValue('Reimbursement');
  await expect(dialog.getByLabel('How this benefit works', { exact: true })).toBeDisabled();
  await expect(dialog.getByLabel('Claim limit (SAR)', { exact: true })).toBeEnabled();
  await expect(dialog).toContainText('Existing employee benefits retain their agreed policy.');
});


test('configured salary deduction does not claim an empty legacy deduction list means unpaid', async ({ page }) => {
  await boot(page);
  await page.route('**/api/ess/benefits', route => route.fulfill({ json: { employeeId: 42, enrollments: [{ ...enrollment, planName: 'Salary benefit deduction', paymentPolicy: { ...policy, delivery: 'PayrollDeduction', amount: 100, receiptRequired: false }, deductions: [] }] } }));
  await page.goto('/ess/benefits');
  await expect(page.getByTestId('benefit-payment-summary')).toContainText('Deduct from salary');
  await expect(page.getByTestId('benefit-payment-summary')).toContainText('100.00');
  await expect(page.getByText('No benefit deduction has been recorded on payroll yet.', { exact: true })).toHaveCount(0);
});
