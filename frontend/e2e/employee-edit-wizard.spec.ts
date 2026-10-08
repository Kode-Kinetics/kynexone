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
  payrollProfile: { bankName: 'Test Bank', iban: 'SA0380000000608010167519', salaryCurrency: 'SAR', paymentMethod: 'BankTransfer', wpsEligible: true, eosbEligible: true },
  passportNumber: 'TEST-PASSPORT-001', passportExpiryDate: '2030-01-31', iqamaNumber: '', gosiReference: '',
  readinessState: 'Ready', activationBlockersCount: 0, profileCompletenessScore: 90, accessState: 'active',
  complianceRecords: [], documents: [], history: [], transfers: [],
};
const stepNames = ['Profile', 'Employment', 'Payroll', 'Salary', 'Identity', 'Review'];
type EditBody = { effectiveDate: string; changes: Record<string, unknown> };
interface BootOptions { readOnly?: boolean; noSensitive?: boolean; customRole?: boolean; approval?: boolean; failFirstSave?: boolean; catalogResponse?: Promise<unknown> }

async function boot(page: Page, options: BootOptions = {}) {
  let employee = { ...initialEmployee };
  const writes: Array<{ method: string; body: EditBody }> = [];
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() !== 'error') return;
    // The retry fixture deliberately returns one 503; all other browser errors fail the case.
    if (options.failFirstSave && /Failed to load resource.*503/.test(message.text())) return;
    errors.push(`console: ${message.text()}`);
  });
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'employee-edit-fixture');
    localStorage.setItem('kynexone.theme', 'light');
  });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const body = request.postData() ? request.postDataJSON() : undefined;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (path === '/api/auth/me') return reply({
      id: 'edit-reviewer', tenantId: 'wizard-tenant', tenantSlug: 'wizard-fixture', fullName: 'HR Reviewer',
      roles: [options.readOnly ? 'Auditor' : options.customRole ? 'Custom HR' : 'HR Manager'],
      permissions: ['employees.read', ...(options.readOnly ? [] : ['employees.write', 'employees.approve']), ...(options.noSensitive || options.readOnly ? [] : ['employees.sensitive'])],
      companies: [{ id: company.id, name: company.legalNameEn, code: 'TEST', countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/companies') return reply(paged([company]));
    if (path === '/api/branches') return reply(paged([{ id: 'edit-branch', companyId: company.id, nameEn: 'Riyadh', code: 'RYD', isActive: true }]));
    if (path === '/api/departments') return reply(paged([{ id: 'edit-department', nameEn: 'Operations', code: 'OPS', isActive: true }]));
    if (path === '/api/designations') return reply(paged([{ id: 'edit-designation', titleEn: 'Specialist', code: 'SPEC', isActive: true }]));
    if (path === '/api/grades') return reply(paged([{ id: 'edit-grade', name: 'Professional', code: 'G5', minSalary: 5000, maxSalary: 15000, currency: 'SAR', isActive: true }]));
    if (path === '/api/organization/cost-centers') return reply(paged([]));
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
      if (options.readOnly || options.customRole) return reply({ message: 'Read-only fixture must never receive an employee mutation.' }, 403);
      if (options.failFirstSave && writes.length === 1) return reply({ message: 'Temporary save failure. Please try again.' }, 503);
      if (options.approval) {
        const immediate = Object.fromEntries(Object.entries(body.changes).filter(([key]) => key !== 'salary'));
        employee = { ...employee, ...immediate };
        return reply({ sensitiveFields: ['salary'], appliedFields: Object.keys(immediate), approvalRequestId: 'approval-fixture-001', alreadyPending: false }, 202);
      }
      employee = { ...employee, ...body.changes };
      return reply(employee);
    }
    if (path === '/api/employees/901') return reply(employee);
    if (path === '/api/employees') return reply(paged([employee]));
    if (path === '/api/employee-access/summary') return reply({ active: 1 });
    if (path === '/api/employee-access/901') return reply({
      employeeId: 901, employeeName: employee.fullName, employeeCode: employee.employeeCode, workEmail: employee.workEmail,
      state: 'active', canIssue: false, codeExpiresAtUtc: null, codeIssuedByName: null, lastCodeExpiredAtUtc: null,
      lastSignInAtUtc: '2026-10-01T10:00:00Z', stoppedReason: null, blockedCode: null, blockedReason: null, reasonCode: null,
    });
    if (path.endsWith('/readiness')) return reply({ employeeId: 901, state: 'Ready', score: 100, progress: { present: 4, requiredTotal: 4 }, policy: { countryCode: 'SA', tier: 'Standard', sources: [] }, blocking: [], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: 'Synthetic browser fixture.' });
    if (path.includes('/features/') || path === '/api/notifications' || path.endsWith('/documents') || path.endsWith('/history') || path.endsWith('/pay-scale')) return reply([]);
    return reply(paged([]));
  });
  await page.goto('/people');
  await page.getByRole('button', { name: 'Open profile for Alex Morgan', exact: true }).click();
  const dialog = page.getByRole('dialog').filter({ hasText: 'Alex Morgan' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('navigation', { name: 'Employee setup progress' })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Step 1: Profile', exact: true })).toHaveAttribute('aria-current', 'step');
  return { dialog, writes, errors };
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
  await expect(page.locator('[data-employee-step]:visible')).toHaveCSS('opacity', '1');
  await page.screenshot({ path, fullPage: false, animations: 'disabled', style: 'nextjs-portal { visibility: hidden; }' });
  await info.attach(name, { path, contentType: 'image/png' });
}

test('employee name opens a prefilled wizard and review saves only edited fields', async ({ page }, info) => {
  const { dialog, writes, errors } = await boot(page);
  await expect(dialog.getByRole('textbox', { name: /^English full name/ })).toHaveValue('Alex Morgan');
  await expect(dialog.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Alex');
  await expect(dialog.getByRole('button', { name: 'Save changes', exact: true })).toHaveCount(0);
  await evidence(page, info, 'edit-profile');
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Alex M');
  await next(dialog, 1);
  await expect(dialog.getByRole('textbox', { name: /^Job title/ })).toHaveValue('Operations specialist');
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('alex.it-issued');
  await dialog.getByRole('textbox', { name: /^Job title/ }).fill('Senior operations specialist');
  await dialog.getByRole('textbox', { name: 'Work email', exact: true }).fill('alex.updated-by-it');
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
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('alex.updated-by-it');
  await toReview(dialog, 2);
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText('Employee details saved.', { exact: true })).toBeVisible();
  expect(writes).toHaveLength(1);
  expect(writes[0]).toEqual({ method: 'PUT', body: { effectiveDate: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/), changes: { preferredName: 'Alex M', jobTitle: 'Senior operations specialist', workEmail: 'alex.updated-by-it@example.test' } } });
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
  await page.getByRole('button', { name: 'Open profile for Alex Morgan', exact: true }).click();
  const reopened = page.getByRole('dialog').filter({ hasText: 'Alex Morgan' });
  await expect(reopened.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Alex M');
  await next(reopened, 1);
  await next(reopened, 2);
  await next(reopened, 3);
  await expect(reopened.getByRole('spinbutton', { name: /^Salary/ })).toHaveValue('9000');
  expect(errors).toEqual([]);
});

test('read-only users see the same steps without edit controls and can open the full profile', async ({ page }, info) => {
  const { dialog, writes, errors } = await boot(page, { readOnly: true });
  for (let index = 0; index < stepNames.length; index++) {
    if (index > 0) await next(dialog, index);
    await expect(dialog.locator('input, select, textarea')).toHaveCount(0);
    await expect(dialog.getByRole('button', { name: /^Save/ })).toHaveCount(0);
    await expect(dialog.getByRole('button', { name: /^Edit / })).toHaveCount(0);
    const bounds = await dialog.boundingBox();
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(page.viewportSize()!.width + 1);
    expect(await dialog.evaluate(node => node.scrollWidth <= node.clientWidth + 1)).toBe(true);
  }
  await expect(dialog).toContainText('Alex Morgan');
  await evidence(page, info, 'employee-readonly-review');
  await dialog.getByRole('button', { name: 'Open full profile', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Close employee profile', exact: true })).toBeVisible();
  await expect(page.getByTestId('employee-access-card')).toBeVisible();
  expect(writes).toHaveLength(0);
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

test('a custom role with write permission remains read-only when the API role gate excludes it', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page, { customRole: true });
  await expect(dialog.locator('input, select, textarea')).toHaveCount(0);
  await toReview(dialog);
  await expect(dialog.getByRole('button', { name: /^Save/ })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: /^Edit / })).toHaveCount(0);
  expect(writes).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('invalid work-email changes stop at Employment until corrected', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page);
  await next(dialog, 1);
  const email = dialog.getByRole('textbox', { name: 'Work email', exact: true });
  await email.fill('alex+alias');
  await dialog.getByRole('button', { name: 'Next: Payroll', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Step 2: Employment', exact: true })).toHaveAttribute('aria-current', 'step');
  await expect(dialog.getByTestId('work-email-problem')).toContainText(/work email/i);
  expect(writes).toHaveLength(0);
  await email.fill('alex.it-issued');
  await next(dialog, 2);
  expect(writes).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('cancel protects unsaved changes and discarding restores the saved record on reopen', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page);
  await dialog.getByRole('textbox', { name: /^Preferred name/ }).fill('Unsaved preferred name');
  const confirmations: string[] = [];
  page.once('dialog', async confirmation => {
    confirmations.push(confirmation.message());
    await confirmation.dismiss();
  });
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Unsaved preferred name');
  page.once('dialog', async confirmation => {
    confirmations.push(confirmation.message());
    await confirmation.accept();
  });
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await page.getByRole('button', { name: 'Open profile for Alex Morgan', exact: true }).click();
  const reopened = page.getByRole('dialog').filter({ hasText: 'Alex Morgan' });
  await expect(reopened.getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Alex');
  expect(confirmations).toHaveLength(2);
  expect(confirmations.every(message => /discard.*unsaved/i.test(message))).toBe(true);
  expect(writes).toHaveLength(0);
  expect(errors).toEqual([]);
});


test('a late response for the first row cannot replace or save over the latest employee', async ({ page }) => {
  const { dialog, writes, errors } = await boot(page);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
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
  await page.reload();
  await page.getByRole('button', { name: 'Open profile for Alex Morgan', exact: true }).click();
  await expect.poll(() => firstRequested).toBe(true);
  await page.getByRole('button', { name: 'Open profile for Blake Carter', exact: true }).click();
  const latest = page.getByRole('dialog').filter({ hasText: 'Blake Carter' });
  await expect(latest.getByRole('textbox', { name: /^English full name/ })).toHaveValue('Blake Carter');
  const firstResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/employees/901');
  firstReleased = true;
  releaseFirst();
  await firstResponse;
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
