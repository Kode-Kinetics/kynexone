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
  // A salary deduction that is really a loan belongs in Loans, which tracks the balance; the plan form says so and links there.
  await expect(dialog.getByTestId('benefit-loans-pointer')).toContainText('Set it up under Loans');
  await expect(dialog.getByTestId('benefit-loans-pointer').getByRole('link', { name: 'Go to Loans', exact: true })).toHaveAttribute('href', '/loans');
  await dialog.getByRole('button', { name: 'Add payslip line', exact: true }).click();
  await expect(dialog.getByLabel('Taxable', { exact: true })).toHaveCount(0);
  await dialog.getByLabel('Payslip line name').fill('Gym deduction'); await dialog.getByLabel('Payslip line code').fill('GYM_DED');
  await dialog.getByRole('button', { name: 'Create payslip line', exact: true }).click();
  await expect(dialog.getByLabel('Payslip line', { exact: true })).toHaveValue('new-component');
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
  await expect(dialog.getByLabel('Payslip line', { exact: true }).getByRole('option', { name: /Bound salary component/ })).toHaveCount(0);
  await dialog.getByLabel('Claim limit (SAR)').fill('500'); await dialog.getByLabel('Payslip line', { exact: true }).selectOption('earning');
  await dialog.getByLabel('How this benefit works', { exact: true }).selectOption('Coverage');
  await expect(dialog.getByLabel('Payslip line', { exact: true })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Create plan', exact: true }).click();
  expect(saves[0]).toMatchObject({ paymentPolicy: { delivery: 'Coverage', amount: null, salaryComponentId: null, receiptRequired: false } });
  for (const key of ['claimFor', 'limitPerChild', 'maxChildren', 'extraChildByException', 'coveredCosts', 'allowedCountries', 'claimTerms']) expect(saves[0].paymentPolicy).not.toHaveProperty(key);
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
  // A plan without education rules keeps the original form and payload.
  for (const key of ['dependantId', 'schoolName', 'schoolCountry', 'claimTerm', 'lines']) expect(submits[0]).not.toHaveProperty(key);
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


// ── Education claims: child, school, country, term and invoice lines (contract sections A–E) ──

const GCC = ['SA', 'AE', 'KW', 'BH', 'OM', 'QA'];
const eduPolicy = { ...policy, amount: 20000, receiptRequired: false, claimFor: 'Child', limitPerChild: true, maxChildren: 3, extraChildByException: true, coveredCosts: ['Tuition'], allowedCountries: GCC, claimTerms: ['Semester', 'HalfYear', 'FullYear'] };
const eduRules = { claimFor: 'Child', limitPerChild: true, maxChildren: 3, extraChildByException: true, coveredCosts: ['Tuition'], allowedCountries: GCC, claimTerms: ['Semester', 'HalfYear', 'FullYear'], limitPeriod: 'ContractYear' };
const kid = (n: number, remainingAmount: number, withinChildLimit = true) => ({ dependantId: `child-${n}`, name: ['Sara', 'Omar', 'Lina', 'Yusuf'][n - 1], maximumAmount: 20000, reservedAmount: 0, approvedAmount: 20000 - remainingAmount, remainingAmount, withinChildLimit });
const eduBalance = (rules = eduRules, children = [kid(1, 15000), kid(2, 20000), kid(3, 0)]) => ({ ...balance, maximumAmount: 20000, remainingAmount: 20000, approvedAmount: 0, reservedAmount: 0, periodFrom: '2026-03-15', periodTo: '2027-03-14', children, rules });

async function bootEducation(page: Page, options: { exception?: boolean; children?: ReturnType<typeof kid>[] } = {}) {
  const fixture = await boot(page);
  const rules = { ...eduRules, extraChildByException: options.exception ?? true };
  const children = options.children ?? [kid(1, 15000), kid(2, 20000), kid(3, 0), kid(4, 20000, false)];
  const enrolment = { ...enrollment, planName: 'Child education', paymentPolicy: { ...eduPolicy, extraChildByException: rules.extraChildByException }, claimBalance: eduBalance(rules, children) };
  await page.route('**/api/ess/benefits', route => route.fulfill({ json: { employeeId: 42, enrollments: [enrolment] } }));
  await page.route('**/api/ess/benefits/enrollments/*/claim-balance**', route => { fixture.balances.push(new URL(route.request().url()).searchParams.get('expenseDate')!); return route.fulfill({ json: eduBalance(rules, children) }); });
  await page.goto('/ess/benefits');
  await expect(page.getByTestId('benefit-claims')).toContainText('Child education');
  if (children.length) await expect(page.getByTestId('claim-child-balances')).toContainText(children[0].name);
  await page.getByRole('button', { name: 'Submit a claim', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Claim Child education', exact: true });
  if (children.length) await expect(dialog.getByLabel('Child', { exact: true })).toBeVisible();
  return { ...fixture, dialog };
}

test('HR sets education rules on a reimbursement plan and the payslip line is filled from the plan', async ({ page }) => {
  const { saves, mappings, errors } = await boot(page, { emptyMappings: true });
  await page.goto('/benefits');
  await page.getByRole('button', { name: 'Add benefit plan', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New benefit plan' });
  await dialog.getByLabel('Plan code').fill('edu-allow'); await dialog.getByLabel('Plan name').fill('Child education allowance');
  await dialog.getByLabel('How this benefit works', { exact: true }).selectOption('Reimbursement');
  await expect(dialog.getByText('The line this benefit appears as on the payslip.', { exact: true })).toBeVisible();
  await expect(dialog.getByTestId('benefit-loans-pointer')).toHaveCount(0);

  // Payslip line: name and code follow the plan until edited; the code is made safe for payroll.
  await dialog.getByRole('button', { name: 'Add payslip line', exact: true }).click();
  await expect(dialog.getByLabel('Payslip line name')).toHaveValue('Child education allowance');
  await expect(dialog.getByLabel('Payslip line code')).toHaveValue('EDU_ALLOW');
  await dialog.getByLabel('Plan name').fill('Child education');
  await expect(dialog.getByLabel('Payslip line name')).toHaveValue('Child education');
  await dialog.getByLabel('Payslip line code').fill('edu-tuition');
  await expect(dialog.getByLabel('Payslip line code')).toHaveValue('EDU_TUITION');
  await dialog.getByRole('button', { name: 'Create payslip line', exact: true }).click();
  await expect(dialog.getByLabel('Payslip line', { exact: true })).toHaveValue('new-component');
  expect(mappings[0]).toMatchObject({ code: 'EDU_TUITION', name: 'Child education', componentType: 'Earning' });

  // Education rules.
  await dialog.getByText('Education rules', { exact: true }).click();
  await dialog.getByLabel('Each claim is for one of the employee’s children').check();
  await dialog.getByLabel('The claim limit applies to each child separately').check();
  await expect(dialog.getByLabel('Claim limit per child (SAR)', { exact: true })).toBeVisible();
  await dialog.getByLabel('Claim limit per child (SAR)', { exact: true }).fill('20000');
  await dialog.getByLabel('Most children covered (optional)').fill('3');
  await dialog.getByLabel('Allow more children as an HR exception').check();
  await expect(dialog).toContainText('A claim for child 4 or later can still be submitted.');
  await dialog.getByLabel('Only some invoice costs are covered').check();
  await expect(dialog.getByLabel('Tuition', { exact: true })).toBeChecked();
  await expect(dialog.getByLabel('Transport', { exact: true })).not.toBeChecked();
  await dialog.getByLabel('Where the school must be', { exact: true }).selectOption('Gcc');
  await dialog.getByLabel('Ask which term the claim covers').check();
  await dialog.getByRole('button', { name: 'Create plan', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(saves[0]).toMatchObject({ code: 'EDU-ALLOW', paymentPolicy: { delivery: 'Reimbursement', amount: 20000, salaryComponentId: 'new-component', claimFor: 'Child', limitPerChild: true, maxChildren: 3, extraChildByException: true, coveredCosts: ['Tuition'], claimTerms: ['Semester', 'HalfYear', 'FullYear'] } });
  expect([...(saves[0].paymentPolicy as { allowedCountries: string[] }).allowedCountries].sort()).toEqual([...GCC].sort());
  expect(errors).toEqual([]);
});

test('the eligibility rule editor offers the contract-year limit period', async ({ page }) => {
  await boot(page); await page.goto('/benefits');
  await page.getByText(plan.name, { exact: true }).first().click();
  await page.getByRole('button', { name: /Add rule|Add eligibility rule/ }).first().click();
  const form = page.getByRole('form', { name: 'Add eligibility rule' });
  await form.getByLabel('Limit period').selectOption('ContractYear');
  await expect(form).toContainText('follows renewals, not January to December');
});

test('employee claims for a child with invoice lines: only covered costs are claimed', async ({ page }, info) => {
  const { dialog, submits, errors } = await bootEducation(page);
  await expect(dialog.getByLabel('Child', { exact: true }).locator('option', { hasText: 'Sara' })).toContainText('15,000.00');
  await dialog.getByLabel('Child', { exact: true }).selectOption('child-1');
  await dialog.getByLabel('School name').fill('Riyadh International School');
  await expect(dialog.getByLabel('School country', { exact: true }).locator('option')).toHaveCount(7);
  await dialog.getByLabel('School country', { exact: true }).selectOption('AE');
  await dialog.getByLabel('Claim term', { exact: true }).selectOption('Semester');
  await expect(dialog.getByLabel('Claim amount (SAR)', { exact: true })).toHaveCount(0);
  await dialog.getByLabel('Line 1 amount (SAR)').fill('1000');
  await dialog.getByRole('button', { name: 'Add invoice line', exact: true }).click();
  await expect(dialog.getByLabel('Line 2 cost', { exact: true })).toHaveValue('Transport');
  await dialog.getByLabel('Line 2 amount (SAR)').fill('200');
  await dialog.getByRole('button', { name: 'Add invoice line', exact: true }).click();
  await dialog.getByLabel('Line 3 cost', { exact: true }).selectOption('Vat');
  await dialog.getByLabel('Line 3 amount (SAR)').fill('150');
  await expect(dialog.getByTestId('claim-covered-summary')).toContainText('1,000.00');
  await expect(dialog.getByTestId('claim-covered-summary')).toContainText('350.00');
  await expect(dialog.getByTestId('claim-covered-summary')).toContainText('(Transport, VAT)');
  await expect(dialog.getByTestId('claim-amount-covered')).toContainText('1,000.00');
  await dialog.getByTestId('claim-lines').screenshot({ path: `/tmp/benefit-education-lines-${info.project.name}.png` });
  await dialog.getByLabel('Invoice reference', { exact: true }).fill('INV-SCHOOL-77');
  await dialog.getByLabel('What was this expense for?', { exact: true }).fill('First semester tuition.');
  await dialog.getByRole('button', { name: 'Review claim', exact: true }).click();
  await expect(dialog).toContainText('Sara'); await expect(dialog).toContainText('Riyadh International School');
  await page.screenshot({ path: `/tmp/benefit-education-review-${info.project.name}.png`, fullPage: true });
  await dialog.getByRole('button', { name: 'Submit claim', exact: true }).click();
  await expect(page.getByTestId('benefit-claims')).toContainText('Awaiting approval');
  expect(submits[0]).toMatchObject({ enrollmentId: 'enrolled', amount: 1000, dependantId: 'child-1', schoolName: 'Riyadh International School', schoolCountry: 'AE', claimTerm: 'Semester',
    lines: [{ type: 'Tuition', amount: 1000 }, { type: 'Transport', amount: 200 }, { type: 'Vat', amount: 150 }] });
  expect(page.url()).toContain('/ess/benefits');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(errors).toEqual([]);
});

test('an invoice with no covered cost cannot be claimed, and the reason is named', async ({ page }) => {
  const { dialog, submits } = await bootEducation(page);
  await dialog.getByLabel('Child', { exact: true }).selectOption('child-2');
  await dialog.getByLabel('School name').fill('Dubai School');
  await dialog.getByLabel('School country', { exact: true }).selectOption('SA');
  await dialog.getByLabel('Claim term', { exact: true }).selectOption('FullYear');
  await dialog.getByLabel('Line 1 cost', { exact: true }).selectOption('Transport');
  await dialog.getByLabel('Line 1 amount (SAR)').fill('300');
  await dialog.getByLabel('Invoice reference', { exact: true }).fill('INV-BUS');
  await dialog.getByLabel('What was this expense for?', { exact: true }).fill('School bus.');
  await dialog.getByRole('button', { name: 'Review claim', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('No covered cost is on this invoice');
  await expect(dialog.getByRole('button', { name: 'Submit claim', exact: true })).toHaveCount(0);
  expect(submits).toEqual([]);
});

test('a 4th child is flagged as an HR exception when the policy allows one', async ({ page }) => {
  const { dialog } = await bootEducation(page);
  await expect(dialog.getByLabel('Child', { exact: true }).locator('option', { hasText: 'Yusuf' })).toContainText('beyond the 3-child limit');
  await dialog.getByLabel('Child', { exact: true }).selectOption('child-4');
  await expect(dialog.getByTestId('claim-child-exception')).toContainText('HR reviews this claim as an exception');
  await expect(dialog.getByRole('button', { name: 'Review claim', exact: true })).toBeEnabled();
});

test('a 4th child is refused before submission when the policy allows no exception', async ({ page }) => {
  const { dialog, submits } = await bootEducation(page, { exception: false });
  await dialog.getByLabel('Child', { exact: true }).selectOption('child-4');
  await expect(dialog.getByTestId('claim-child-blocked')).toContainText('A claim for another child cannot be submitted.');
  await expect(dialog.getByRole('button', { name: 'Review claim', exact: true })).toBeDisabled();
  expect(submits).toEqual([]);
});

test('an employee with no child on file is told why a child claim cannot be made', async ({ page }) => {
  const { dialog } = await bootEducation(page, { children: [] });
  await expect(dialog.getByTestId('claim-no-children')).toContainText('HR needs to add the child under dependants');
  await expect(dialog.getByLabel('Child', { exact: true })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Review claim', exact: true })).toBeDisabled();
});

test('the approver sees child, school, country, term, covered and not-covered lines and the exception first', async ({ page }) => {
  await boot(page);
  const eduClaim = { id: 'claim-edu', status: 'Pending', employeeId: 42, employeeName: 'Alex Morgan', enrollmentId: 'enrolled', benefitPlanId: plan.id, planName: 'Child education', currency: 'SAR', amount: 1000, expenseDate: '2026-10-01', invoiceReference: 'INV-SCHOOL-77', description: 'First semester tuition.', receipts: [], createdAtUtc: '2026-10-09T12:00:00Z', approvalRequestId: 'approval-edu', payrollRunId: null, settlementStatus: 'AwaitingApproval', reservedAmount: 1000, remainingAmount: 19000, canWithdraw: false,
    dependantId: 'child-4', dependantName: 'Yusuf', schoolName: 'Riyadh International School', schoolCountry: 'AE', claimTerm: 'Semester', lines: [{ type: 'Tuition', amount: 1000 }, { type: 'Transport', amount: 200 }, { type: 'Vat', amount: 150 }], invoiceTotal: 1350,
    exceptionReason: 'Child 4 of 3: beyond the policy’s child limit — HR exception' };
  const request = { id: 'approval-edu', workflowId: 'w1', entityName: 'BenefitClaim', entityId: 'claim-edu', title: 'Exception · Benefit claim - Child education - Alex Morgan', status: 'Pending', currentStepOrder: 1, requestedByUserId: 'employee-user', requestedForEmployeeId: 42, companyId: plan.companyId,
    currentApproverEmployeeId: null, currentApproverUserId: null, currentApproverName: '', currentApproverRole: 'HR Manager', currentApproverType: 'Role', currentQueue: 'Role:HR Manager', slaHours: 48, dueAtUtc: '2026-10-11T12:00:00Z', isOverdue: false, ageHours: 3, lastRoutedAtUtc: '2026-10-09T12:00:00Z',
    escalatedAtUtc: null, escalatedToRole: '', priority: 'Normal', createdAtUtc: '2026-10-09T12:00:00Z', completedAtUtc: null, decisions: [], canDecide: true, decisionBlockedReason: null, canWithdraw: false, changeSummary: null };
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: 'hr-user', tenantId: 'benefit-tenant', fullName: 'Hana HR', roles: ['HR Manager'], permissions: ['dashboard.read', 'employees.read', 'approvals.read', 'approvals.decide', 'payroll.read'], companies: [{ id: plan.companyId, name: 'Benefits Company', countryCode: 'SA', isActive: true }], isGroupScope: false, accountType: 'SingleCompany' } }));
  await page.route('**/api/approval-requests**', route => new URL(route.request().url()).pathname === '/api/approval-requests' ? route.fulfill({ json: { items: [request], total: 1, page: 1, pageSize: 25 } }) : route.fallback());
  await page.route(`**${base}/claims/claim-edu`, route => route.fulfill({ json: eduClaim }));
  await page.goto('/approvals');
  await page.getByRole('row').filter({ hasText: 'Child education' }).getByRole('button', { name: 'Review' }).click();
  const detail = page.getByRole('dialog').getByTestId('claim-education-details');
  await expect(detail.getByTestId('claim-exception-banner')).toContainText('Child 4 of 3');
  await expect(detail).toContainText('Yusuf'); await expect(detail).toContainText('Riyadh International School');
  await expect(detail).toContainText('United Arab Emirates'); await expect(detail).toContainText('Semester');
  const lines = detail.locator('li');
  await expect(lines.filter({ hasText: 'Tuition' })).toContainText('Covered');
  await expect(lines.filter({ hasText: 'Transport' })).toContainText('Not covered');
  await expect(lines.filter({ hasText: 'VAT' })).toContainText('Not covered');
  await expect(detail).toContainText('1,350.00');
});
