import { expect, test, type Page } from '@playwright/test';

const housingType = { id: 'type-0', nameEn: 'Housing', code: 'HOUSING', isInterestFree: true, isActive: true, maxAmount: 50000, maxInstallments: 60, repaymentFrequency: 'Monthly', minServiceMonths: 12 };
const loanType = { id: 'type-1', nameEn: 'Personal', code: 'PERSONAL', isInterestFree: true, isActive: true, maxAmount: 10000, maxInstallments: 24, repaymentFrequency: 'Monthly', minServiceMonths: 0 };
const originalLoan = { id: 'loan-1', employeeId: 'employee-1', employeeName: 'Amira Mansour', loanTypeId: 'type-1', loanTypeName: 'Personal', loanNumber: 'LN-001', requestedAmount: 1000, approvedAmount: 1000, requestedInstallments: 2, approvedInstallments: 2, installmentAmount: 500, repaymentFrequency: 'Monthly', repaymentMethod: 'BankTransfer', currency: 'SAR', disbursementDate: '2026-09-01', totalRepaid: 0, outstandingBalance: 1000, status: 'Overdue', notes: '', isLockedByPayroll: false, createdAtUtc: '2026-09-01T00:00:00Z', policyVersion: 1, collectionStatus: 'Normal', reviewRequired: false };

test('legacy manual collection exposes reconciliation evidence and independent review', async ({ page }) => {
  let reviewer = false;
  const loan = { ...originalLoan, repaymentMethod: 'PayrollDeduction', policyVersion: null, collectionStatus: 'OnHold', createdBy: 'employee-user' };
  const changes: any[] = [];
  const errors = await boot(page, 'Finance', (path, method, body) => {
    if (path === '/api/auth/me' && reviewer) return { id: 'reviewer', tenantId: 'tenant-1', fullName: 'Finance Checker', roles: ['Finance'], permissions: ['loans.read', 'loans.write'], companies: [] };
    if (path === '/api/finance/loans') return { items: [loan], total: 1 };
    if (path.endsWith('/loan-1')) return { loan, installments: [], approvals: [], repayments: [], glEntries: [], auditLogs: [] };
    if (path.endsWith('/changes')) {
      if (method === 'POST') { expect(body.confirmNoPayrollCollection).toBe(true); changes.push({ ...body, id: 'conversion', status: 'Pending', createdBy: 'user-1', requestedRepaymentMethod: body.repaymentMethod, reference: body.reconciliationReference }); }
      return method === 'POST' ? changes[0] : changes;
    }
    if (path.endsWith('/conversion/decide')) { changes[0].status = body.decision; loan.repaymentMethod = 'BankTransfer'; return { loan, change: changes[0] }; }
  });
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await page.getByRole('button', { name: 'Reviews & Changes' }).click();
  await page.getByRole('button', { name: 'Review Legacy Collection Method' }).click();
  await page.getByLabel('Reconciliation evidence reference').fill('BANK-RECON-42');
  await page.getByLabel('Reason', { exact: true }).fill('Verified historical manual bank receipts');
  await page.getByRole('button', { name: 'Submit Request', exact: true }).click();
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Confirm the reconciled');
  await page.getByRole('checkbox', { name: 'I verified the history is manual-only, with no payroll collections.' }).check();
  await page.getByRole('button', { name: 'Submit Request', exact: true }).click();
  await expect(page.getByText('Target collection: BankTransfer · Reconciliation evidence: BANK-RECON-42')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review Change', exact: true })).toHaveCount(0);
  reviewer = true; await page.reload();
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await page.getByRole('button', { name: 'Reviews & Changes' }).click();
  await page.getByRole('button', { name: 'Review Change', exact: true }).click();
  await page.getByLabel('Reason', { exact: true }).fill('Evidence independently reconciled');
  await page.getByRole('button', { name: 'Submit Review Decision', exact: true }).click();
  await expect(page.getByText('CollectionMethod · Approved')).toBeVisible();
  await expect(page.getByText('Collection status: OnHold', { exact: false })).toBeVisible();
  expect(errors).toEqual([]);
});

async function boot(page: Page, role: string, handler: (path: string, method: string, body: any, query: URLSearchParams) => unknown) {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'fixture'));
  await page.route('**/api/**', route => {
    const request = route.request(); const url = new URL(request.url()); const path = url.pathname;
    const response = handler(path, request.method(), request.postData() ? request.postDataJSON() : null, url.searchParams);
    if (response !== undefined) return route.fulfill({ json: response });
    if (path === '/api/auth/me') return route.fulfill({ json: { id: 'user-1', employeeId: 17, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Amira Mansour', roles: [role], permissions: ['loans.read', 'loans.write'], companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] } });
    if (path === '/api/tenant-admin/localization') return route.fulfill({ json: { currencyCode: 'USD' } });
    if (path === '/api/finance/loans/types') return route.fulfill({ json: [housingType, loanType] });
    // Slice L1 endpoints, in their real (array) shapes: the request form lists only offered types, and Loan
    // Policies shows the per-company offerings and the limits-by-grade grid.
    if (path === '/api/finance/loans/types/offered') return route.fulfill({ json: [
      { loanTypeId: housingType.id, code: housingType.code, nameEn: housingType.nameEn, nameAr: 'قرض سكن', gradeLimited: true, offered: false, reasonCode: 'LoanTypeNotOffered', reasonText: "Loans of this type aren't offered by your company." },
      { loanTypeId: loanType.id, code: loanType.code, nameEn: loanType.nameEn, nameAr: 'قرض شخصي', gradeLimited: false, offered: true, reasonCode: null, reasonText: null },
    ] });
    if (path === '/api/finance/loans/offerings') return route.fulfill({ json: [{ loanTypeId: loanType.id, code: loanType.code, nameEn: loanType.nameEn, nameAr: '', gradeLimited: false, companyId: 'company-1', offered: true, source: 'LoanTypeBaseline', policyId: null, policyVersion: null, detachedFromGroupPolicy: false }] });
    if (path === '/api/finance/loans/grade-limits') return route.fulfill({ json: [] });
    if (path === '/api/finance/loans') return route.fulfill({ json: { items: [originalLoan], total: 1 } });
    if (path.endsWith('/audit')) return route.fulfill({ json: {} });
    if (path.endsWith('/changes') || path.endsWith('/corrections') || path.includes('/features/') || path === '/api/notifications' || path.endsWith('/bonuses/types')) return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto('/loans');
  await expect(page).toHaveURL(/\/loans$/);
  await expect(page).toHaveTitle(/Kynex/i);
  return errors;
}

test('employee sees own loan statement and can submit an eligible application', async ({ page }, info) => {
  let created: any;
  const queries: string[] = [];
  const errors = await boot(page, 'Employee', (path, method, body, query) => {
    if (path === '/api/finance/loans' && method === 'GET') { queries.push(query.get('mine') ?? ''); return { items: [originalLoan], total: 1 }; }
    if (path.endsWith('/eligibility')) { expect(query.get('employeeIntId')).toBe('17'); return { eligible: true, reasons: [], codes: [], maxAvailableAmount: null, policyVersion: 2, monthlySalary: null, committedAmount: 1000 }; }
    if (path === '/api/finance/loans' && method === 'POST') { created = body; return { ...originalLoan, status: 'Pending' }; }
    if (path.endsWith('/loan-1')) return { loan: originalLoan, installments: [{ id: 'ins-1', installmentNumber: 1, dueDate: '2020-01-01', amountDue: 500, amountPaid: 0, status: 'Overdue' }], approvals: [{ id: 'a-1', stepOrder: 1, approverRole: 'HR Manager', status: 'Approved', approvedByName: 'HR Reviewer' }], repayments: [], auditLogs: [], glEntries: [] };
  });
  await expect(page.getByRole('heading', { name: 'My Loans', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Loan Payments', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Loan Policies', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await expect(page.getByRole('region', { name: 'Loan statement' })).toContainText('Overdue');
  await expect(page.getByRole('dialog').getByText('Step 1 — HR Manager · Approved · HR Reviewer')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Record Repayment', exact: true })).toHaveCount(0);
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
  await page.getByRole('button', { name: 'New Loan Request' }).click();
  await expect(page.getByPlaceholder('Search by name or code…')).toHaveCount(0);
  // Two types exist; /types/offered offers only the second, so only it is listed — and it is preselected.
  const typeSelect = page.getByRole('dialog').getByTitle('Loan Type');
  await expect(typeSelect.locator('option')).toHaveText(['Select type', 'Personal (Interest-free)']);
  await expect(typeSelect).toHaveValue('type-1');
  await expect(page.getByText("We couldn't confirm which loan types", { exact: false })).toHaveCount(0);
  await page.getByTitle('Requested Amount').fill('1200');
  await expect(page.getByRole('button', { name: 'Submit Request', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Check Eligibility', exact: true }).click();
  await expect(page.getByText('Eligible to apply', { exact: true })).toBeVisible();
  await expect(page.getByText('No fixed amount limit', { exact: false })).toBeVisible();
  if (process.env.LOAN_EVIDENCE_DIR) await page.screenshot({ path: `${process.env.LOAN_EVIDENCE_DIR}/loan-eligibility-${info.project.name}.png` });
  await page.getByRole('button', { name: 'Submit Request', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(created.employeeIntId).toBe(17); expect(created.repaymentMethod).toBe('BankTransfer');
  expect(queries.every(value => value === 'true')).toBe(true); expect(errors).toEqual([]);
});

test('HR creates an immutable company policy version with its approval route', async ({ page }) => {
  let policy: any;
  const errors = await boot(page, 'HR Manager', (path, method, body) => {
    if (path.endsWith('/policies') && method === 'GET') return policy ? [policy] : [];
    if (path.endsWith('/policies') && method === 'POST') { policy = { ...body, id: 'policy-1', version: 1, createdAtUtc: '2026-10-04T00:00:00Z' }; return policy; }
  });
  await page.getByRole('button', { name: 'Loan Policies', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Loan types offered' })).toContainText('Personal');
  await page.getByRole('button', { name: 'New Policy Version', exact: true }).click();
  await page.getByLabel('Policy name', { exact: true }).fill('Employee welfare');
  await page.getByLabel('Maximum loan amount', { exact: true }).fill('10000');
  await page.getByLabel('Additional approval threshold', { exact: true }).fill('5000');
  await page.getByRole('button', { name: 'Create Policy Version', exact: true }).click();
  await expect(page.getByText(/Policy version 1 created/)).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: 'Employee welfare' })).toContainText('HR Manager → HR Director above 5,000');
  expect(policy.companyId).toBe('company-1'); expect(policy.allowedRepaymentMethods).toEqual(['BankTransfer']); expect(policy.allowedRepaymentFrequencies).toContain('Quarterly'); expect(policy.isOffered).not.toBe(false); expect(errors).toEqual([]);
});

// Owner decision: HR Director manages loan policies. Its seeded bundle has loans.read and employees.approve (the
// API's second key on the four policy gates) but no loans.write or loans.policy_manage, so boot it with exactly that.
const hrDirectorMe = { id: 'hr-director', employeeId: 17, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Huda Director', roles: ['HR Director'], permissions: ['loans.read', 'employees.read', 'employees.approve', 'organization.read'], companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] };
const gradeRow = { gradeId: 'grade-1', gradeCode: 'G1', gradeName: 'Grade One', gradeNameAr: null, level: 1, cellId: null, eligible: false, valueType: null, amount: null, rate: null, maxOutstandingAmount: null, effectiveFrom: null, isCompanyOverride: false };

test('HR Director sees every loan policy edit control, enabled', async ({ page }) => {
  const errors = await boot(page, 'HR Director', path => {
    if (path === '/api/auth/me') return hrDirectorMe;
    if (path.endsWith('/policies')) return [];
    if (path === '/api/finance/loans/grade-limits') return [gradeRow];
  });
  await page.getByRole('button', { name: 'Loan Policies', exact: true }).click();
  await expect(page.getByRole('button', { name: 'New Policy Version', exact: true })).toBeEnabled();
  const offerings = page.getByRole('region', { name: 'Loan types offered' });
  await expect(offerings.getByRole('checkbox', { name: 'Offered' })).toBeEnabled();
  const grid = page.getByRole('region', { name: 'Limits by grade' });
  await expect(grid.getByRole('checkbox', { name: 'Limit this loan type by grade' })).toBeEnabled();
  await expect(grid.getByRole('combobox', { name: 'Eligible — Grade One' })).toBeEnabled();
  expect(errors).toEqual([]);
});

for (const role of ['Finance', 'Finance Approver']) {
  test(`${role} gets no loan policy edit controls`, async ({ page }) => {
    const errors = await boot(page, role, () => undefined);
    await expect(page.getByRole('button', { name: 'Loans', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Loan Policies', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'New Policy Version', exact: true })).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Loan types offered' })).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Limits by grade' })).toHaveCount(0);
    expect(errors).toEqual([]);
  });
}

test('Finance records a failed line, retries it, and requests audited corrections', async ({ page }, info) => {
  let correctionReviewer = false;
  const loan = { ...originalLoan, status: 'Approved', disbursementDate: undefined as string | undefined, outstandingBalance: 0, totalRepaid: 0 };
  const batch = { id: 'batch-1', batchNumber: 'LP-001', status: 'Approved', currency: 'SAR', totalAmount: 2000, createdBy: 'other-maker', lines: [{ id: 'line-1', loanId: loan.id, loanNumber: loan.loanNumber, employeeName: loan.employeeName, amount: 1000, status: 'Pending' }, { id: 'line-2', loanId: 'loan-2', loanNumber: 'LN-002', employeeName: 'Omar Hassan', amount: 1000, status: 'Pending' }] };
  const corrections: any[] = []; const changes: any[] = [];
  const receipt = { id: 'receipt-1', amount: 100, paidDate: '2026-10-01', reference: 'REC-1', paymentMethod: 'BankTransfer' };
  const errors = await boot(page, 'Finance', (path, method, body, query) => {
    if (path === '/api/auth/me' && correctionReviewer) return { id: 'user-2', tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Independent Finance Reviewer', roles: ['Finance'], permissions: ['loans.read', 'loans.write'], companies: [] };
    if (path === '/api/finance/loans') return { total: query.get('status') && query.get('status') !== loan.status ? 0 : 1, items: query.get('status') && query.get('status') !== loan.status ? [] : [loan] };
    if (path.endsWith('/payment-batches')) return [batch];
    if (path.endsWith('/batch-1')) return batch;
    if (path.endsWith('/line-1/outcome')) { batch.lines[0].status = body.outcome; if (body.outcome === 'Paid') { batch.status = 'PartiallyPaid'; loan.status = 'Active'; loan.disbursementDate = body.paidDate; loan.outstandingBalance = 900; loan.totalRepaid = 100; } return batch; }
    if (path.endsWith('/loan-1')) return { loan, installments: [], approvals: [], repayments: [receipt], auditLogs: [], glEntries: [] };
    if (path.endsWith('/changes')) { if (method === 'POST') changes.push({ ...body, id: 'change-1', status: 'Pending', createdBy: 'user-1' }); return method === 'POST' ? changes[0] : changes; }
    if (path.endsWith('/corrections')) { if (method === 'POST') corrections.push({ ...body, id: 'correction-1', status: 'Pending', createdBy: 'user-1' }); return method === 'POST' ? corrections[0] : corrections; }
    if (path.endsWith('/correction-1/decide')) { corrections[0].status = body.decision; return { loan, change: corrections[0] }; }
  });
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  await page.getByRole('button', { name: 'View batch LP-001' }).click();
  await page.getByRole('button', { name: 'Record Outcome — LN-001' }).click();
  await page.getByRole('combobox', { name: 'Payment outcome', exact: true }).selectOption('Failed');
  await page.getByLabel('Outcome reason', { exact: true }).fill('Bank rejected the transfer');
  await page.getByRole('button', { name: 'Confirm Loan Payment Outcome' }).click();
  await expect(page.getByText('Payment: Failed', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Record Outcome — LN-001' }).click();
  await page.getByLabel('Bank reference', { exact: true }).fill('RETRY-100');
  await page.getByRole('button', { name: 'Confirm Loan Payment Outcome' }).click();
  await expect(page.getByRole('dialog').getByText('PartiallyPaid', { exact: true })).toBeVisible();
  await expect(page.getByRole('dialog').getByText('Instruction total:', { exact: false })).toContainText('SAR');
  await expect(page.getByText('Paid amount', { exact: true }).locator('..')).toContainText('1,000.00');
  await expect(page.getByText('Remaining to pay', { exact: true }).locator('..')).toContainText('1,000.00');
  await expect(page.getByRole('button', { name: 'Record Completed Payment', exact: true })).toHaveCount(0);
  if (process.env.LOAN_EVIDENCE_DIR) await page.screenshot({ path: `${process.env.LOAN_EVIDENCE_DIR}/loan-partial-payment-${info.project.name}.png` });
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
  await page.getByRole('button', { name: 'Loans', exact: true }).click();
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await page.getByRole('button', { name: 'Reviews & Changes' }).click();
  await page.getByRole('button', { name: 'Request Receipt Reversal' }).click();
  await page.getByLabel('Correction reference', { exact: true }).fill('CORR-1');
  await page.getByLabel('Reason', { exact: true }).fill('Duplicate receipt entered in error');
  await page.getByRole('button', { name: 'Submit Request', exact: true }).click();
  await expect(page.getByText('ReceiptReversal · Pending', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review Correction', exact: true })).toHaveCount(0);
  expect(corrections[0].repaymentId).toBe('receipt-1');
  correctionReviewer = true;
  await page.reload();
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await page.getByRole('button', { name: 'Reviews & Changes' }).click();
  await page.getByRole('button', { name: 'Review Correction', exact: true }).click();
  await page.getByLabel('Reason', { exact: true }).fill('Confirmed duplicate against bank statement');
  await page.getByRole('button', { name: 'Submit Review Decision', exact: true }).click();
  await expect(page.getByText('ReceiptReversal · Approved', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('HR reviews employment changes and Finance receives a reschedule request', async ({ page }) => {
  const loan = { ...originalLoan, reviewRequired: true, reviewReason: 'Employment contract changed', collectionStatus: 'OnHold', createdBy: 'employee-user' };
  const changes: any[] = [];
  const errors = await boot(page, 'HR Manager', (path, method, body) => {
    if (path === '/api/finance/loans') return { items: [loan], total: 1 };
    if (path.endsWith('/loan-1')) return { loan, installments: [], approvals: [], repayments: [], glEntries: [], auditLogs: [] };
    if (path.endsWith('/lifecycle/review')) { expect(body.decision).toBe('Continue'); loan.reviewRequired = false; loan.collectionStatus = 'Normal'; return loan; }
    if (path.endsWith('/changes')) { if (method === 'POST') changes.push({ ...body, id: 'reschedule-1', status: 'Pending', requestedInstallments: body.installments, requestedStartDate: body.startDate, createdBy: 'user-1' }); return method === 'POST' ? changes[0] : changes; }
  });
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  await expect(page.getByText(/HR review required: Employment contract changed/)).toBeVisible();
  await page.getByRole('button', { name: 'Reviews & Changes' }).click();
  await page.getByRole('button', { name: 'Record HR Review', exact: true }).click();
  await page.getByLabel('Reason', { exact: true }).fill('Contract change reviewed and collection can continue');
  await page.getByRole('button', { name: 'Submit Request', exact: true }).click();
  await expect(page.getByText('Lifecycle review recorded.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Request Reschedule', exact: true }).click();
  await page.getByLabel('Remaining installments', { exact: true }).fill('4');
  await page.getByLabel('First due date', { exact: true }).fill('2026-12-01');
  await page.getByLabel('Reason', { exact: true }).fill('Employee requested a longer repayment schedule');
  await page.getByRole('button', { name: 'Submit Request', exact: true }).click();
  await expect(page.getByText('Reschedule · Pending', { exact: true })).toBeVisible();
  await expect(page.getByText('4 installments starting 2026-12-01', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review Change', exact: true })).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('policy exception application requires an explicit employee request', async ({ page }) => {
  let submitted: any;
  const errors = await boot(page, 'Employee', (path, method, body) => {
    if (path.endsWith('/eligibility')) return { eligible: false, reasons: ['Amount exceeds the salary limit.'], codes: ['SalaryMultiple'], canRequestException: true, maxAvailableAmount: 1000, policyVersion: 3 };
    if (path === '/api/finance/loans' && method === 'POST') { submitted = body; return { ...originalLoan, status: 'Pending' }; }
  });
  await page.getByRole('button', { name: 'New Loan Request' }).click();
  await page.getByTitle('Requested Amount').fill('1500');
  await page.getByRole('button', { name: 'Check Eligibility', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Submit Request', exact: true })).toBeDisabled();
  await page.getByRole('checkbox', { name: 'Request an HR Director policy exception' }).check();
  await page.getByRole('button', { name: 'Submit Request', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(submitted.requestPolicyException).toBe(true); expect(errors).toEqual([]);
});

test('when the offered-types list is unusable, every interest-free type is listed with a plain note', async ({ page }) => {
  const legacyInterest = { ...loanType, id: 'type-9', nameEn: 'Legacy interest', code: 'LEGACY', isInterestFree: false, interestRate: 3 };
  const errors = await boot(page, 'Employee', path => {
    if (path === '/api/finance/loans/types/offered') return { items: [] };            // malformed: not a list
    if (path === '/api/finance/loans/types') return [housingType, loanType, legacyInterest];
  });
  await page.getByRole('button', { name: 'New Loan Request' }).click();
  await expect(page.getByText("We couldn't confirm which loan types your company offers; we'll check when you choose one.")).toBeVisible();
  await expect(page.getByRole('dialog').getByTitle('Loan Type').locator('option'))
    .toHaveText(['Select type', 'Housing (Interest-free)', 'Personal (Interest-free)']);   // never the interest-bearing one
  await expect(page.getByRole('button', { name: 'Check Eligibility', exact: true })).toBeVisible();
  await expect(page.getByText('Something went wrong')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('a malformed offerings response shows the load error, not a false empty list', async ({ page }) => {
  const errors = await boot(page, 'HR Manager', path => {
    if (path === '/api/finance/loans/offerings') return { items: [] };                 // malformed: not a list
    if (path.endsWith('/policies')) return [];
  });
  await page.getByRole('button', { name: 'Loan Policies', exact: true }).click();
  const panel = page.getByRole('region', { name: 'Loan types offered' });
  await expect(panel.getByRole('alert')).toHaveText('Unable to load which loan types this company offers.');
  await expect(panel.getByText('No loan types yet', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'New Policy Version', exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('cancelled or waived schedule history does not create a collectible balance', async ({ page }) => {
  const cancelled = { ...originalLoan, status: 'Cancelled', outstandingBalance: 0 };
  const errors = await boot(page, 'Employee', path => {
    if (path === '/api/finance/loans') return { items: [cancelled], total: 1 };
    if (path.endsWith('/loan-1')) return { loan: cancelled, installments: [
      { id: 'cancelled-1', installmentNumber: 1, dueDate: '2020-01-01', amountDue: 500, amountPaid: 0, status: 'Cancelled' },
      { id: 'waived-1', installmentNumber: 2, dueDate: '2020-02-01', amountDue: 250, amountPaid: 0, status: 'Waived' },
      { id: 'cancelled-2', installmentNumber: 3, dueDate: '2030-01-01', amountDue: 250, amountPaid: 0, status: 'Cancelled' },
    ], approvals: [], repayments: [], auditLogs: [], glEntries: [] };
  });
  await page.getByRole('button', { name: 'View loan LN-001' }).click();
  const statement = page.getByRole('region', { name: 'Loan statement' });
  for (const label of ['Outstanding', 'Due to date', 'Overdue']) await expect(statement.getByText(label, { exact: true }).locator('..')).toContainText('0.00');
  await expect(page.getByRole('row').filter({ hasText: '2020-01-01' })).toContainText('Cancelled');
  await expect(page.getByRole('row').filter({ hasText: '2020-02-01' })).toContainText('Waived');
  expect(errors).toEqual([]);
});

test('batch totals distinguish paid, cancelled and reversed instructions', async ({ page }) => {
  const batch = { id: 'batch-1', batchNumber: 'LP-001', status: 'Completed', currency: 'SAR', totalAmount: 3000, paidAmount: 1000, remainingAmount: 0, cancelledAmount: 500, reversedAmount: 1500, lines: [
    { id: 'line-1', loanId: 'loan-1', loanNumber: 'LN-001', employeeName: 'Amira', amount: 1000, status: 'Paid' },
    { id: 'line-2', loanId: 'loan-2', loanNumber: 'LN-002', employeeName: 'Omar', amount: 500, status: 'Cancelled' },
    { id: 'line-3', loanId: 'loan-3', loanNumber: 'LN-003', employeeName: 'Layla', amount: 1500, status: 'Reversed' },
  ] };
  const errors = await boot(page, 'Finance', path => path.endsWith('/payment-batches') ? [batch] : undefined);
  await page.getByRole('button', { name: 'Loan Payments', exact: true }).click();
  await page.getByRole('button', { name: 'View batch LP-001' }).click();
  for (const [label, amount] of [['Paid amount', '1,000.00'], ['Remaining to pay', '0.00'], ['Cancelled amount', '500.00'], ['Reversed amount', '1,500.00']]) await expect(page.getByText(label, { exact: true }).locator('..')).toContainText(amount);
  await expect(page.getByText('Payment: Reversed', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /Record Outcome/ })).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('a malformed loan-policies reply shows a failed load with Retry, never "No policy versions"', async ({ page }) => {
  let malformed = true;
  const errors = await boot(page, 'HR Manager', path => {
    if (path === '/api/finance/loans/policies') return malformed ? { items: [] } : [];   // malformed: not a list
  });
  await page.getByRole('button', { name: 'Loan Policies', exact: true }).click();
  const failed = page.getByTestId('list-load-failed');
  await expect(failed).toContainText('This list could not be loaded');
  await expect(failed).toContainText('The server sent an unexpected reply.');
  await expect(page.getByText('No policy versions for this selection.', { exact: true })).toHaveCount(0);
  malformed = false;
  await failed.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByText('No policy versions for this selection.', { exact: true })).toBeVisible();
  await expect(page.getByTestId('list-load-failed')).toHaveCount(0);
  expect(errors).toEqual([]);
});

// The session check (/api/auth/me) failing for a reason other than 401 must not sign the user out:
// the shell shows "Can't reach the server right now" with Retry and keeps the tokens.
test('a network failure of the session check keeps the user signed in and offers Retry', async ({ page }) => {
  let reachable = false;
  await page.addInitScript(() => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('zayra_refresh_token', 'fixture-refresh'); });
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/me') {
      if (!reachable) return route.abort('internetdisconnected');
      return route.fulfill({ json: { id: 'user-1', employeeId: 17, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Amira Mansour', roles: ['HR Manager'], permissions: ['loans.read', 'loans.write'], companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] } });
    }
    if (path === '/api/finance/loans/types') return route.fulfill({ json: [loanType] });
    if (path === '/api/finance/loans') return route.fulfill({ json: { items: [originalLoan], total: 1 } });
    if (path.includes('/features/') || path === '/api/notifications' || path.endsWith('/bonuses/types') || path.endsWith('/offerings') || path.endsWith('/grade-limits')) return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto('/loans');
  const offline = page.getByTestId('server-unreachable');
  await expect(offline).toContainText("Can't reach the server right now.", { timeout: 15_000 });
  await expect(offline).toContainText('You are still signed in.');
  await expect(page).toHaveURL(/\/loans$/);
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBe('fixture');

  reachable = true;
  await offline.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByTestId('server-unreachable')).toHaveCount(0);
  await expect(page.getByText('LN-001').first()).toBeVisible();
  await expect(page).toHaveURL(/\/loans$/);
});

test('a 502 from the session check is shown as a server problem, not a sign-out', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'fixture'));
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/me') return route.fulfill({ status: 502, body: '<html>Bad Gateway</html>', contentType: 'text/html' });
    return route.fulfill({ json: [] });
  });
  await page.goto('/loans');
  await expect(page.getByTestId('server-unreachable')).toContainText('is not responding normally', { timeout: 15_000 });
  await expect(page).toHaveURL(/\/loans$/);
});

test('a 401 from the session check still ends the session and goes to sign-in', async ({ page }) => {
  await page.addInitScript(() => { if (!sessionStorage.getItem('seeded')) { localStorage.setItem('zayra_access_token', 'expired'); sessionStorage.setItem('seeded', '1'); } });
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/me') return route.fulfill({ status: 401, json: { code: 'unauthorized' } });
    if (path === '/api/auth/refresh') return route.fulfill({ status: 401, json: { code: 'invalid_refresh_token' } });
    return route.fulfill({ json: [] });
  });
  await page.goto('/loans');
  await expect(page).toHaveURL(/\/login/, { timeout: 15_000 });
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBeNull();
});

test('the offline screen offers Sign out, which ends the session and goes to sign-in', async ({ page }) => {
  await page.addInitScript(() => { if (!sessionStorage.getItem('seeded')) { localStorage.setItem('zayra_access_token', 'fixture'); sessionStorage.setItem('seeded', '1'); } });
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/me' || path === '/api/auth/logout') return route.abort('internetdisconnected');
    return route.fulfill({ json: [] });
  });
  await page.goto('/loans');
  const offline = page.getByTestId('server-unreachable');
  await expect(offline).toContainText('If this keeps happening, you can sign out', { timeout: 15_000 });
  await offline.getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page).toHaveURL(/\/login/, { timeout: 15_000 });
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBeNull();
});

test('a 403 from the session check ends the session instead of trapping the user offline', async ({ page }) => {
  await page.addInitScript(() => { if (!sessionStorage.getItem('seeded')) { localStorage.setItem('zayra_access_token', 'fixture'); sessionStorage.setItem('seeded', '1'); } });
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/me') return route.fulfill({ status: 403, json: { code: 'forbidden', message: 'This account cannot use the tenant app.' } });
    return route.fulfill({ json: [] });
  });
  await page.goto('/loans');
  await expect(page).toHaveURL(/\/login/, { timeout: 15_000 });
  await expect(page.getByTestId('server-unreachable')).toHaveCount(0);
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBeNull();
});
