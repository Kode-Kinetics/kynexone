import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';

const company = {
  id: 'edit-company', legalNameEn: 'Wizard Test Company', legalNameAr: '', tradeName: 'Wizard Test Company',
  countryCode: 'SA', defaultCurrency: 'SAR', emailDomain: 'example.test', workEmailPattern: 'first.last',
  isActive: true, approvalStatus: 'Active',
};
const initialEmployee = {
  id: 901, publicId: 'employee-edit-fixture', employeeCode: 'TEST-901', fullName: 'Alex Morgan', englishName: 'Alex Morgan',
  arabicName: '', preferredName: 'Alex', profilePhotoUrl: '', gender: 'Male', nationality: 'GB', maritalStatus: 'Single',
  dateOfBirth: '1990-04-18', personalEmail: 'alex.personal@example.test', workEmail: 'alex.it-issued@example.test', phone: '+966500000001',
  countryCode: 'SA', companyId: company.id, branchId: 'edit-branch', branch: 'Riyadh', department: 'Operations',
  designation: 'Specialist', jobTitle: 'Operations specialist', grade: 'Professional', costCenter: 'Operations cost center',
  employmentType: 'Full-Time', contractType: 'Unlimited', joiningDate: '2024-10-15', workLocation: 'Riyadh office',
  status: 'Active', salary: 9000, bankName: 'Test Bank', bankIban: 'SA0380000000608010167519', wpsBankDetails: '',
  payrollProfile: { bankName: 'Test Bank', iban: 'SA0380000000608010167519', salaryCurrency: 'SAR', payrollGroup: '', paymentMethod: 'BankTransfer', wpsEligible: true, eosbEligible: true },
  passportNumber: 'TEST-PASSPORT-001', passportExpiryDate: '2030-01-31', iqamaNumber: '', gosiReference: '',
  readinessState: 'Ready', activationBlockersCount: 0, profileCompletenessScore: 90, accessState: 'active',
  complianceRecords: [], documents: [] as Array<{ id: string; documentType: string; fileName: string; approvalStatus: string }>, history: [], transfers: [],
};
const stepNames = ['Profile', 'Employment', 'Payroll', 'Salary', 'Identity', 'Review'];
const salaryBreakdown = { basicSalary: 7000, housingAllowance: 2000, transportAllowance: 300, foodAllowance: 100, mobileAllowance: 50, otherAllowance: 25, fixedDeduction: 10, currency: 'SAR', effectiveDate: '2026-10-01', salaryStructureCode: 'PACKAGE-TEST' };
type EditBody = { effectiveDate: string; changes: Record<string, unknown> };
interface BootOptions { readOnly?: boolean; noSensitive?: boolean; customRole?: boolean; approval?: boolean; failFirstSave?: boolean; catalogResponse?: Promise<unknown>; structuredSalary?: boolean; salaryStructureCode?: string; legacySalaryWithNullPackage?: boolean; documentUploadStatus?: number; contractGap?: boolean }

async function boot(page: Page, options: BootOptions = {}) {
  let employee = { ...initialEmployee, ...(options.structuredSalary ? { salaryBreakdown: { ...salaryBreakdown, ...(options.salaryStructureCode ? { salaryStructureCode: options.salaryStructureCode } : {}) } } : options.legacySalaryWithNullPackage ? { salary: 7500, salaryBreakdown: null } : {}) };
  const writes: Array<{ method: string; body: EditBody }> = [];
  const documentUploads: string[] = [];
  let detailReads = 0;
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() !== 'error') return;
    // The retry fixture deliberately returns one 503; all other browser errors fail the case.
    if ((options.failFirstSave || options.documentUploadStatus === 503) && /Failed to load resource.*503/.test(message.text())) return;
    errors.push(`console: ${message.text()}`);
  });
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'employee-edit-fixture');
    localStorage.setItem('kynexone.theme', 'light');
  });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const postData = request.postData();
    const body = postData && (request.headers()['content-type'] ?? '').includes('application/json') ? request.postDataJSON() : undefined;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (path === '/api/auth/me') return reply({
      id: 'edit-reviewer', tenantId: 'wizard-tenant', tenantSlug: 'wizard-fixture', fullName: 'HR Reviewer',
      roles: [options.readOnly ? 'Auditor' : options.customRole ? 'Custom HR' : 'HR Manager'],
      permissions: ['employees.read', ...(options.readOnly ? [] : ['employees.write', 'employees.approve', 'employees.documents']), ...(options.noSensitive || options.readOnly ? [] : ['employees.sensitive'])],
      companies: [{ id: company.id, name: company.legalNameEn, code: 'TEST', countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/admin/master-data/values') return reply([]);
    if (path === '/api/companies') return reply(paged([company]));
    if (path === '/api/branches') return reply(paged([{ id: 'edit-branch', companyId: company.id, nameEn: 'Riyadh', code: 'RYD', isActive: true }]));
    if (path === '/api/departments') return reply(paged([{ id: 'edit-department', nameEn: 'Operations', code: 'OPS', isActive: true }]));
    if (path === '/api/designations') return reply(paged([{ id: 'edit-designation', titleEn: 'Specialist', code: 'SPEC', isActive: true }]));
    if (path === '/api/grades') return reply(paged([{ id: 'edit-grade', name: 'Professional', code: 'G5', minSalary: 5000, maxSalary: 15000, currency: 'SAR', isActive: true }]));
    if (path === '/api/organization/cost-centers') return reply(paged([]));
    if (path === '/api/payroll/salary-structures') return reply([
      { id: 'structure-company', companyId: company.id, code: 'OPS-BAND', name: 'Operations band', isActive: true },
      { id: 'structure-package', companyId: company.id, code: 'PACKAGE-TEST', name: 'Package test', isActive: true },
      { id: 'structure-group', companyId: null, code: 'GROUP-STD', name: 'Group standard', isActive: true },
      { id: 'structure-other', companyId: 'another-company', code: 'OTHER-CO', name: 'Another company band', isActive: true },
      { id: 'structure-retired', companyId: company.id, code: 'OPS-OLD', name: 'Retired band', isActive: false },
    ]);
    if (path === '/api/employees/field-catalog') {
      if (options.catalogResponse && new URL(request.url()).searchParams.get('nationality') === 'GB') return reply(await options.catalogResponse);
      return reply({ countryCode: 'SA', fields: [] });
    }
    if (path === '/api/employees/derive-work-email') {
      const localPart = body.localPart ?? 'alex.morgan';
      return reply({ domain: company.emailDomain, pattern: 'first.last', localPart, workEmail: `${localPart}@${company.emailDomain}`, unique: true, suggestion: localPart, status: 'derived' });
    }
    if (path === '/api/employees/901' && request.method() !== 'GET') {
      writes.push({ method: request.method(), body });
      if (options.readOnly) return reply({ message: 'Read-only fixture must never receive an employee mutation.' }, 403);
      if (options.failFirstSave && writes.length === 1) return reply({ message: 'Temporary save failure. Please try again.' }, 503);
      if (options.approval) {
        const sensitiveFields = Object.keys(body.changes).filter(key => ['salary', 'salaryBreakdown'].includes(key));
        const immediate = Object.fromEntries(Object.entries(body.changes).filter(([key]) => !sensitiveFields.includes(key)));
        employee = { ...employee, ...immediate };
        return reply({ sensitiveFields, appliedFields: Object.keys(immediate), approvalRequestId: 'approval-fixture-001', alreadyPending: false }, 202);
      }
      employee = { ...employee, ...body.changes, ...(body.changes.payrollGroup !== undefined ? { payrollProfile: { ...employee.payrollProfile, payrollGroup: body.changes.payrollGroup } } : {}) };
      return reply(employee);
    }
    if (path === '/api/employees/901') { detailReads++; return reply(employee); }
    if (path === '/api/employees') return reply(paged([employee]));
    if (path === '/api/employee-access/summary') return reply({ active: 1 });
    if (path === '/api/employee-access/901') return reply({
      employeeId: 901, employeeName: employee.fullName, employeeCode: employee.employeeCode, workEmail: employee.workEmail,
      state: 'active', canIssue: false, codeExpiresAtUtc: null, codeIssuedByName: null, lastCodeExpiredAtUtc: null,
      lastSignInAtUtc: '2026-10-01T10:00:00Z', stoppedReason: null, blockedCode: null, blockedReason: null, reasonCode: null,
    });
    if (path.endsWith('/readiness')) return reply({ employeeId: 901, state: options.contractGap ? 'NeedsAttention' : 'Ready', score: options.contractGap ? 80 : 100, progress: { present: options.contractGap ? 4 : 5, requiredTotal: 5 }, policy: { countryCode: 'SA', tier: 'Standard', sources: [] }, blocking: [], payBlocking: [], recommended: options.contractGap ? [{ key: 'doc:Contract', label: 'Contract document', category: 'document', reason: 'recommended', jurisdiction: 'SA', gate: 'activate', fix: { kind: 'document', documentType: 'Contract' } }] : [], present: [], expiringSoon: [], disclaimer: 'Synthetic browser fixture.' });
    if (path === '/api/employees/901/documents') {
      if (request.method() === 'GET') return reply(employee.documents);
      documentUploads.push(postData ?? '');
      if (options.documentUploadStatus && options.documentUploadStatus !== 201) return reply({ message: 'Document storage unavailable. Try again.' }, options.documentUploadStatus);
      const uploaded = { id: 'document-upload-fixture', documentType: postData?.includes('Offer letter') ? 'Offer letter' : 'Contract', fileName: 'evostel-demo.docx', approvalStatus: 'Pending' };
      employee = { ...employee, documents: [uploaded, ...employee.documents] };
      return reply(uploaded, 201);
    }
    if (path.includes('/features/') || path === '/api/notifications' || path.endsWith('/documents') || path.endsWith('/history') || path.endsWith('/pay-scale')) return reply([]);
    return reply(paged([]));
  });
  await page.goto(options.readOnly ? '/people/901?section=personal' : '/people/901/edit?mode=guided&section=personal');
  await expect(page).toHaveURL(options.readOnly ? /\/people\/901\?section=personal/ : /\/people\/901\/edit/);
  await expect(page).not.toHaveTitle('');
  const dialog = options.readOnly ? page.locator('main.employee-workspace') : page.getByTestId('employee-edit-flow');
  await expect(dialog).toBeVisible();
  if (!options.readOnly) {
    await expect(dialog.getByRole('navigation', { name: 'Employee setup progress' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Step 1: Profile', exact: true })).toHaveAttribute('aria-current', 'step');
  }
  return { dialog, writes, errors, documentUploads, detailReadCount: () => detailReads };
}

async function next(dialog: Locator, index: number) {
  await dialog.getByRole('button', { name: `Next: ${stepNames[index]}`, exact: true }).click();
  await expect(dialog.getByRole('button', { name: `Step ${index + 1}: ${stepNames[index]}`, exact: true })).toHaveAttribute('aria-current', 'step');
}

async function toReview(dialog: Locator, start = 1) {
  for (let index = start; index < stepNames.length; index++) await next(dialog, index);
}

async function evidence(page: Page, info: TestInfo, name: string) {
  const path = info.outputPath(`${name}-${info.project.name}.png`);
  const step = page.locator('[data-employee-step]:visible');
  if (await step.count()) await expect(step).toHaveCSS('opacity', '1');
  await page.screenshot({ path, fullPage: false, animations: 'disabled', style: 'nextjs-portal { visibility: hidden; }' });
  await info.attach(name, { path, contentType: 'image/png' });
}

async function reopenEdit(page: Page) {
  await page.getByRole('link', { name: 'Edit full record', exact: true }).click();
  const flow = page.getByTestId('employee-edit-flow');
  await expect(flow).toBeVisible();
  return flow;
}

test('email regression: edit preserves the full address across employment review and save', async ({ page }, info) => {
  const { dialog, writes, errors } = await boot(page);
  await next(dialog, 1);
  const input = dialog.getByRole('textbox', { name: 'Work email', exact: true });
  await expect(input).toHaveValue('alex.it-issued@example.test');
  await input.fill('alex.updated@external.test');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(dialog.locator('[data-employee-step="5"]')).toContainText('alex.updated@external.test');
  await evidence(page, info, 'full-email-edit-review');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes[0].body.changes.workEmail).toBe('alex.updated@external.test');
  expect(errors).toEqual([]);
});

test('employee name opens a prefilled wizard and review saves only edited fields', async ({ page }, info) => {
  const { dialog, writes, errors } = await boot(page);
  await expect(dialog.getByRole('textbox', { name: /^English full name/ })).toHaveValue('Alex Morgan');
  await expect(dialog.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Alex');
  await expect(dialog.getByRole('button', { name: 'Save changes', exact: true })).toHaveCount(0);
  await evidence(page, info, 'edit-profile');
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Alex M');
  await next(dialog, 1);
  await expect(dialog.getByRole('textbox', { name: /^Job title/ })).toHaveValue('Operations specialist');
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('alex.it-issued@example.test');
  await dialog.getByRole('textbox', { name: /^Job title/ }).fill('Senior operations specialist');
  await dialog.getByRole('textbox', { name: 'Work email', exact: true }).fill('alex.updated-by-it@example.test');
  await next(dialog, 2);
  await expect(dialog.getByRole('textbox', { name: /^Bank name/ })).toHaveValue('Test Bank');
  await expect(dialog.getByRole('textbox', { name: /^IBAN/ })).toHaveValue('SA0380000000608010167519');
  await next(dialog, 3);
  await expect(dialog.getByRole('spinbutton', { name: /^Salary/ })).toHaveValue('9000');
  await next(dialog, 4);
  await expect(dialog.locator('#edit-field-passportNumber')).toHaveValue('TEST-PASSPORT-001');
  await next(dialog, 5);
  await evidence(page, info, 'edit-review');
  expect(writes).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Edit Employment', exact: true }).click();
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('alex.updated-by-it@example.test');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(dialog.getByRole('button', { name: /^Step 6: Review/ })).toHaveAttribute('aria-current', 'step');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText('Employee details saved.', { exact: true })).toBeVisible();
  expect(writes).toHaveLength(1);
  expect(writes[0]).toEqual({ method: 'PUT', body: { effectiveDate: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/), changes: { preferredName: 'Alex M', jobTitle: 'Senior operations specialist', workEmail: 'alex.updated-by-it@example.test' } } });
  expect(errors).toEqual([]);
});

test('existing employees can jump directly to a section and return to review before saving', async ({ page }, info) => {
  const { dialog, writes, errors } = await boot(page);
  const progress = dialog.getByRole('navigation', { name: 'Employee setup progress' });
  for (const step of await progress.getByRole('button').all()) await expect(step).toBeEnabled();
  await progress.getByRole('button', { name: /^Step 5: Identity/ }).click();
  await expect(dialog.locator('#edit-field-passportNumber')).toHaveValue('TEST-PASSPORT-001');
  await expect(dialog.getByRole('button', { name: 'Save changes', exact: true })).toHaveCount(0);
  await progress.getByRole('button', { name: /^Step 2: Employment/ }).click();
  await dialog.getByRole('textbox', { name: /^Job title/ }).fill('Directly updated role');
  expect(writes).toHaveLength(0);
  await progress.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(dialog.locator('[data-employee-step="5"]')).toContainText('Directly updated role');
  await evidence(page, info, 'direct-edit-review');
  expect(writes).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body.changes).toEqual({ jobTitle: 'Directly updated role' });
  expect(errors).toEqual([]);
});

test('structured salary edits preserve the complete package and persist resumed payroll details', async ({ page }, info) => {
  const { dialog, writes, errors } = await boot(page, { structuredSalary: true });
  await dialog.getByRole('button', { name: /^Step 3: Payroll/ }).click();
  await dialog.getByRole('textbox', { name: /^Payroll group/ }).fill('Monthly payroll');
  await dialog.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await expect(dialog.getByRole('spinbutton', { name: /^Basic salary/ })).toHaveValue('7000');
  await expect(dialog.getByRole('spinbutton', { name: /^Housing allowance/ })).toHaveValue('2000');
  // The structure is a choice of the company's active (or group-wide) structures, never free text; retired ones and
  // other companies' are not offered.
  const structure = dialog.getByRole('combobox', { name: /^Salary structure/ });
  await expect(structure).toHaveValue('PACKAGE-TEST');
  await expect(structure.locator('option')).toHaveText(['Default for the grade', 'Group standard (GROUP-STD) — all companies', 'Operations band (OPS-BAND)', 'Package test (PACKAGE-TEST)']);
  await expect(dialog.getByRole('textbox', { name: /Salary structure/ })).toHaveCount(0);
  await dialog.getByRole('spinbutton', { name: /^Basic salary/ }).fill('8000');
  await dialog.getByRole('spinbutton', { name: /^Housing allowance/ }).fill('2500');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await evidence(page, info, 'structured-salary-review');
  expect(writes).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body.changes).toEqual({ payrollGroup: 'Monthly payroll', salaryBreakdown: { ...salaryBreakdown, basicSalary: 8000, housingAllowance: 2500 } });
  expect(writes[0].body.changes).not.toHaveProperty('salary');
  const reopened = await reopenEdit(page);
  await reopened.getByRole('button', { name: /^Step 3: Payroll/ }).click();
  await expect(reopened.getByRole('textbox', { name: /^Payroll group/ })).toHaveValue('Monthly payroll');
  await reopened.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await expect(reopened.getByRole('spinbutton', { name: /^Basic salary/ })).toHaveValue('8000');
  await expect(reopened.getByRole('spinbutton', { name: /^Transport allowance/ })).toHaveValue('300');
  expect(errors).toEqual([]);
});

test('choosing another salary structure sends its code with the complete package, and no free-text reference exists', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { structuredSalary: true, approval: true });
  await dialog.getByRole('button', { name: /^Step 3: Payroll/ }).click();
  await expect(dialog.getByRole('textbox', { name: /^Salary structure reference/ })).toHaveCount(0);
  await dialog.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await dialog.getByRole('combobox', { name: /^Salary structure/ }).selectOption('OPS-BAND');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body.changes).toEqual({ salaryBreakdown: { ...salaryBreakdown, salaryStructureCode: 'OPS-BAND' } });
  expect(errors).toEqual([]);
});

test('a retired current salary structure is flagged, blocks a salary change until another is chosen, and never blocks other edits', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { structuredSalary: true, salaryStructureCode: 'OPS-OLD', approval: true });
  await dialog.getByRole('button', { name: /^Step 4: Salary/ }).click();
  const structure = dialog.getByRole('combobox', { name: /^Salary structure/ });
  await expect(structure).toHaveValue('OPS-OLD');
  await expect(structure.locator('option:checked')).toHaveText('Retired band (OPS-OLD) — not valid any more, choose another');
  await dialog.getByRole('spinbutton', { name: /^Basic salary/ }).fill('8000');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  const save = dialog.getByRole('button', { name: 'Save changes', exact: true });
  await expect(dialog.getByRole('alert').filter({ hasText: 'no longer valid for this employee' })).toBeVisible();
  await expect(save).toBeDisabled();
  expect(writes).toHaveLength(0);
  // Choosing a valid structure clears the block and sends it with the package.
  await dialog.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await structure.selectOption('OPS-BAND');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(save).toBeEnabled();
  await save.click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body.changes).toEqual({ salaryBreakdown: { ...salaryBreakdown, basicSalary: 8000, salaryStructureCode: 'OPS-BAND' } });
  expect(errors).toEqual([]);
});

test('an edit that does not touch the salary saves even when the current structure is retired', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { structuredSalary: true, salaryStructureCode: 'OPS-OLD' });
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Alex M');
  await dialog.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await expect(dialog.getByRole('combobox', { name: /^Salary structure/ })).toHaveValue('OPS-OLD');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body.changes).toEqual({ preferredName: 'Alex M' });
  expect(errors).toEqual([]);
});

test('the grade dropdown follows Setup changes while the edit form is open', async ({ page }) => {
  const { dialog, errors } = await boot(page);
  await dialog.getByRole('button', { name: /^Step 2: Employment/ }).click();
  const gradeSelect = dialog.getByRole('combobox', { name: 'Grade', exact: true });
  await expect(gradeSelect.locator('option')).toHaveText(['Select', 'Professional', 'G5']);
  const grade = { id: 'edit-grade', name: 'Professional', code: 'G5', minSalary: 5000, maxSalary: 15000, currency: 'SAR', isActive: true };
  const rows = [{ ...grade, isActive: false }, { ...grade, id: 'edit-grade-7', code: 'G7', name: 'Principal' }];
  await page.route('**/api/grades?**', route => route.fulfill({ json: { items: rows, total: rows.length, page: 1, pageSize: 100 } }));
  await page.evaluate(() => window.dispatchEvent(new Event('kynexone:grades-changed')));
  // G5 was retired, G7 added; the employee's current value stays selectable.
  await expect(gradeSelect.locator('option')).toHaveText(['Select', 'Professional', 'G7']);
  expect(errors).toEqual([]);
});

test('sensitive edits retain the existing 202 approval behavior without claiming immediate application', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { approval: true });
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Alex M');
  await next(dialog, 1);
  await next(dialog, 2);
  await next(dialog, 3);
  await dialog.getByRole('spinbutton', { name: /^Salary/ }).fill('11000');
  await toReview(dialog, 4);
  expect(writes).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText(/Sensitive changes submitted to Approval Center/)).toBeVisible();
  expect(writes).toHaveLength(1);
  expect(writes[0].method).toBe('PUT');
  expect(writes[0].body.changes).toEqual({ preferredName: 'Alex M', salary: 11000 });
  const reopened = await reopenEdit(page);
  await expect(reopened.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Alex M');
  await next(reopened, 1);
  await next(reopened, 2);
  await next(reopened, 3);
  await expect(reopened.getByRole('spinbutton', { name: /^Salary/ })).toHaveValue('9000');
  expect(errors).toEqual([]);
});

test('a legacy salary with no package remains a scalar salary edit requiring approval', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { legacySalaryWithNullPackage: true, approval: true });
  await dialog.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await expect(dialog.getByRole('spinbutton', { name: /^Salary\b/ })).toHaveValue('7500');
  await expect(dialog.getByRole('spinbutton', { name: /^Basic salary/ })).toHaveCount(0);
  await dialog.getByRole('spinbutton', { name: /^Salary\b/ }).fill('8000');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body.changes).toEqual({ salary: 8000 });
  await expect(page.getByText(/Sensitive changes submitted to Approval Center/)).toBeVisible();
  const reopened = await reopenEdit(page);
  await reopened.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await expect(reopened.getByRole('spinbutton', { name: /^Salary\b/ })).toHaveValue('7500');
  expect(errors).toEqual([]);
});

test('future salary changes retain their effective date and remain pending approval', async ({ page }, info) => {
  const { dialog, writes, errors } = await boot(page, { structuredSalary: true, approval: true });
  const futureDate = `${new Date().getUTCFullYear() + 1}-07-15`;
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Alex M');
  await dialog.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await dialog.getByRole('spinbutton', { name: /^Basic salary/ }).fill('8000');
  await dialog.getByLabel(/^Effective date/).fill(futureDate);
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(dialog).toContainText(`Salary changes take effect on ${futureDate}, after approval.`);
  await evidence(page, info, 'future-salary-review');
  expect(writes).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body).toEqual({ effectiveDate: futureDate, changes: { preferredName: 'Alex M', salaryBreakdown: { ...salaryBreakdown, basicSalary: 8000, effectiveDate: futureDate } } });
  await expect(page.getByText(/Sensitive changes submitted to Approval Center/)).toBeVisible();
  const reopened = await reopenEdit(page);
  await expect(reopened.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Alex M');
  await reopened.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await expect(reopened.getByRole('spinbutton', { name: /^Basic salary/ })).toHaveValue('7000');
  await expect(reopened.getByLabel(/^Effective date/)).toHaveValue(salaryBreakdown.effectiveDate);
  expect(errors).toEqual([]);
});

test('future salary changes cannot include other approval-required changes', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { structuredSalary: true });
  const futureDate = `${new Date().getUTCFullYear() + 1}-07-15`;
  const message = 'Save future salary changes separately from other approval-required details.';
  await dialog.getByRole('button', { name: /^Step 3: Payroll/ }).click();
  await dialog.getByRole('textbox', { name: /^Bank name/ }).fill('Changed Bank');
  await dialog.getByRole('button', { name: /^Step 4: Salary/ }).click();
  await dialog.getByRole('spinbutton', { name: /^Basic salary/ }).fill('8000');
  await dialog.getByLabel(/^Effective date/).fill(futureDate);
  await dialog.getByRole('button', { name: 'Next: Identity', exact: true }).click();
  await expect(dialog).toContainText(message);
  await expect(dialog.getByRole('button', { name: /^Step 4: Salary/ })).toHaveAttribute('aria-current', 'step');
  expect(writes).toHaveLength(0);
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(dialog.getByRole('button', { name: 'Save changes', exact: true })).toHaveCount(0);
  await expect(dialog).toContainText(message);
  await dialog.getByRole('button', { name: /^Step 3: Payroll/ }).click();
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toContainText(message);
  expect(writes).toHaveLength(0);
  await dialog.getByRole('button', { name: /^Step 3: Payroll/ }).click();
  await dialog.getByRole('textbox', { name: /^Bank name/ }).fill('Test Bank');
  await dialog.getByRole('button', { name: /^Step 6: Review/ }).click();
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body).toEqual({ effectiveDate: futureDate, changes: { salaryBreakdown: { ...salaryBreakdown, basicSalary: 8000, effectiveDate: futureDate } } });
  expect(errors).toEqual([]);
});

test('read-only users see the record without edit or sensitive controls', async ({ page }, info) => {
  const { dialog, writes, errors } = await boot(page, { readOnly: true });
  await expect(dialog).toContainText('Alex Morgan');
  await expect(dialog.getByRole('link', { name: /Edit/ })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: /^Save/ })).toHaveCount(0);
  await expect(page.getByText('TEST-PASSPORT-001', { exact: true })).toHaveCount(0);
  const bounds = await dialog.boundingBox();
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(page.viewportSize()!.width + 1);
  expect(await dialog.evaluate(node => node.scrollWidth <= node.clientWidth + 1)).toBe(true);
  await evidence(page, info, 'employee-readonly-review');
  expect(writes).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('Open full profile returns to the route-owned employee record', async ({ page }) => {
  const { dialog, detailReadCount, errors } = await boot(page);
  const before = detailReadCount();
  await dialog.getByRole('link', { name: 'Open full profile', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Alex Morgan', exact: true })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Employee record sections' })).toBeVisible();
  await expect.poll(detailReadCount).toBeGreaterThan(before);
  expect(errors).toEqual([]);
});

test('document upload shows success and explains when an offer letter does not close the contract gap', async ({ page }) => {
  const { dialog, documentUploads, errors } = await boot(page, { contractGap: true });
  await dialog.getByRole('link', { name: 'Open full profile', exact: true }).click();
  await page.getByRole('link', { name: 'Compliance & Documents', exact: true }).click();
  await page.getByRole('link', { name: 'Upload document', exact: true }).click();
  await page.getByLabel('Document type', { exact: true }).selectOption('Offer letter');
  await page.getByLabel('Upload document file', { exact: true }).setInputFiles({
    name: 'evostel-demo.docx',
    mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    buffer: Buffer.from('synthetic offer letter'),
  });
  await page.getByRole('button', { name: 'Upload Offer letter', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Offer letter uploaded. The Contract document checklist still requires a Contract file.');
  await expect(page.getByText('evostel-demo.docx', { exact: false })).toBeVisible();
  expect(documentUploads).toHaveLength(1);
  expect(errors).toEqual([]);
});

test('document upload keeps the selected file and shows the API error so the user can retry', async ({ page }) => {
  const { dialog, documentUploads, errors } = await boot(page, { documentUploadStatus: 503, contractGap: true });
  await dialog.getByRole('link', { name: 'Open full profile', exact: true }).click();
  await page.getByRole('link', { name: 'Compliance & Documents', exact: true }).click();
  await page.getByRole('link', { name: 'Upload document', exact: true }).click();
  await page.getByLabel('Document type', { exact: true }).selectOption('Contract');
  const file = page.getByLabel('Upload document file', { exact: true });
  await file.setInputFiles({ name: 'evostel-contract.docx', mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', buffer: Buffer.from('synthetic contract') });
  await page.getByRole('button', { name: 'Upload Contract', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'The service is temporarily unavailable. Retry in a minute.' })).toBeVisible();
  await expect(file).toHaveValue(/evostel-contract\.docx$/);
  await expect(page.getByRole('button', { name: 'Upload Contract', exact: true })).toBeEnabled();
  expect(documentUploads).toHaveLength(1);
  expect(errors).toEqual([]);
});

test('failed save keeps reviewed values available and retries the same changed-field payload', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { failFirstSave: true });
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Alex retained');
  await toReview(dialog);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog.getByText(/Temporary save failure/)).toBeVisible();
  expect(writes).toHaveLength(1);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(2);
  expect(writes[1]).toEqual(writes[0]);
  expect(writes[1].body.changes).toEqual({ preferredName: 'Alex retained' });
  expect(errors).toEqual([]);
});


test('writers without sensitive access can edit ordinary fields while payroll and identity stay locked', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { noSensitive: true });
  await expect(dialog.getByRole('textbox', { name: /^Preferred name/ })).toBeEditable();
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Alex ordinary edit');
  await next(dialog, 1);
  await next(dialog, 2);
  for (const field of ['bankName', 'bankIban']) {
    const control = dialog.locator(`#edit-field-${field}`);
    if (await control.count()) await expect(control).not.toBeEditable();
  }
  await next(dialog, 3);
  const salary = dialog.locator('#edit-field-salary');
  if (await salary.count()) await expect(salary).not.toBeEditable();
  await next(dialog, 4);
  const passport = dialog.locator('#edit-field-passportNumber');
  if (await passport.count()) await expect(passport).not.toBeEditable();
  await next(dialog, 5);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body.changes).toEqual({ preferredName: 'Alex ordinary edit' });
  expect(errors).toEqual([]);
});

test('a custom role with employees.write can review and save ordinary changes', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { customRole: true });
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Custom-role edit');
  await toReview(dialog);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toHaveLength(1);
  expect(writes[0].body.changes).toEqual({ preferredName: 'Custom-role edit' });
  expect(errors).toEqual([]);
});

test('invalid work-email changes stop at Employment until corrected', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page);
  await next(dialog, 1);
  const email = dialog.getByRole('textbox', { name: 'Work email', exact: true });
  await email.fill('alex+alias@example.test');
  await dialog.getByRole('button', { name: 'Next: Payroll', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Step 2: Employment', exact: true })).toHaveAttribute('aria-current', 'step');
  await expect(dialog.getByRole('alert')).toContainText(/work email/i);
  expect(writes).toHaveLength(0);
  await email.fill('alex.it-issued@example.test');
  await next(dialog, 2);
  expect(writes).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('cancel protects unsaved changes and discarding restores the saved record on reopen', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page);
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Unsaved preferred name');
  await dialog.getByRole('link', { name: 'Cancel', exact: true }).click();
  const confirmation = page.getByRole('dialog', { name: 'Discard changes?' });
  await expect(confirmation).toBeVisible();
  await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Unsaved preferred name');
  await dialog.getByRole('link', { name: 'Cancel', exact: true }).click();
  await confirmation.getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  const reopened = await reopenEdit(page);
  await expect(reopened.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Alex');
  expect(writes).toHaveLength(0);
  expect(errors).toEqual([]);
});


test('a late response for the first row cannot replace or save over the latest employee', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page);
  await dialog.getByRole('link', { name: 'Cancel', exact: true }).click();
  let secondEmployee = { ...initialEmployee, id: 902, employeeCode: 'TEST-902', fullName: 'Blake Carter', englishName: 'Blake Carter', preferredName: 'Blake', workEmail: 'blake.it-issued@example.test' };
  const secondWrites: Array<{ method: string; body: EditBody }> = [];
  let firstRequested = false;
  let firstReleased = false;
  let releaseFirst = () => {};
  const pendingFirst = new Promise<void>(resolve => { releaseFirst = resolve; });
  await page.route('**/api/employees**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/employees') return route.fulfill({ json: { items: [initialEmployee, secondEmployee], total: 2, page: 1, pageSize: 25 } });
    if (path === '/api/employees/901' && request.method() === 'GET' && !firstReleased) {
      firstRequested = true;
      await pendingFirst;
      return route.fulfill({ json: initialEmployee });
    }
    if (path === '/api/employees/902') {
      if (request.method() !== 'GET') {
        const body = request.postDataJSON();
        secondWrites.push({ method: request.method(), body });
        secondEmployee = { ...secondEmployee, ...body.changes };
      }
      return route.fulfill({ json: secondEmployee });
    }
    return route.fallback();
  });
  await page.route('**/api/employee-access/902', route => route.fulfill({ json: {
    employeeId: 902, employeeName: 'Blake Carter', employeeCode: 'TEST-902', workEmail: secondEmployee.workEmail,
    state: 'active', canIssue: false, codeExpiresAtUtc: null, codeIssuedByName: null, lastCodeExpiredAtUtc: null,
    lastSignInAtUtc: '2026-10-01T10:00:00Z', stoppedReason: null, blockedCode: null, blockedReason: null, reasonCode: null,
  } }));
  await page.goto('/people/901/edit?mode=guided&section=personal');
  await expect.poll(() => firstRequested).toBe(true);
  await page.goto('/people/902/edit?mode=guided&section=personal');
  const latest = page.getByTestId('employee-edit-flow');
  await expect(latest.getByRole('textbox', { name: /^English full name/ })).toHaveValue('Blake Carter');
  firstReleased = true;
  releaseFirst();
  await page.waitForTimeout(100);
  await expect(latest.getByRole('textbox', { name: /^English full name/ })).toHaveValue('Blake Carter');
  await latest.getByRole('textbox', { name: /^Preferred name/ }).fill('Blake reviewed');
  await toReview(latest);
  await latest.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(latest).toHaveCount(0);
  expect(writes).toHaveLength(0);
  expect(secondWrites).toHaveLength(1);
  expect(secondWrites[0].method).toBe('PUT');
  expect(secondWrites[0].body.changes).toEqual({ preferredName: 'Blake reviewed' });
  expect(errors).toEqual([]);
});


test('a delayed field catalog cannot silently drop edited identity values', async ({ page }) => {
  let deliverCatalog = (_response: unknown) => {};
  const catalogResponse = new Promise<unknown>(resolve => { deliverCatalog = resolve; });
  const { dialog, writes, errors } = await boot(page, { catalogResponse });
  try {
    await next(dialog, 1);
    await next(dialog, 2);
    await next(dialog, 3);
    await next(dialog, 4);
    await dialog.locator('#edit-field-passportNumber').fill('UNSAVED-PASSPORT');
    deliverCatalog({ countryCode: 'SA', fields: [{ key: 'iqamaNumber', label: 'Iqama Number', entityKey: 'iqamaNumber', complianceFieldKey: 'iqama_number', countries: ['SA'], visible: true }] });
    await expect(dialog.locator('#edit-field-passportNumber')).toHaveCount(0);
    await expect(dialog.getByRole('alert').filter({ hasText: 'Some edited fields are no longer available.' })).toBeVisible();
    await dialog.getByRole('button', { name: 'Next: Review', exact: true }).click();
    await expect(dialog.getByRole('button', { name: 'Step 5: Identity', exact: true })).toHaveAttribute('aria-current', 'step');
    expect(writes).toHaveLength(0);
    await dialog.getByRole('button', { name: 'Revert changes', exact: true }).click();
    await expect(dialog.getByRole('alert')).toHaveCount(0);
    await next(dialog, 5);
    await expect(dialog.getByRole('button', { name: 'Save changes', exact: true })).toBeDisabled();
    expect(writes).toHaveLength(0);
    expect(errors).toEqual([]);
  } finally {
    deliverCatalog({ countryCode: 'SA', fields: [] });
  }
});
