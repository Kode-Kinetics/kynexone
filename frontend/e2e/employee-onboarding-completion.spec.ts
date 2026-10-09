import { expect, test, type Page, type TestInfo } from '@playwright/test';
import type { EmployeeCompletion } from '../src/api/employeeCompletion';

const company = { id: 'completion-company', legalNameEn: 'Completion Test Company', countryCode: 'SA', defaultCurrency: 'SAR', emailDomain: 'example.test', workEmailPattern: 'first.last', isActive: true, approvalStatus: 'Active' };
const employee = {
  id: 901, publicId: 'completion-employee', employeeCode: 'COMP-901', fullName: 'Alex Morgan', englishName: 'Alex Morgan',
  arabicName: '', preferredName: 'Alex', gender: '', nationality: 'GB', maritalStatus: '', personalEmail: '', phone: '',
  workEmail: 'alex.it-issued@example.test', companyId: company.id, countryCode: 'SA', status: 'Draft', salary: 0,
  department: 'Operations', designation: 'Specialist', branch: 'Riyadh', readinessState: 'NeedsAttention',
  activationBlockersCount: 1, profileCompletenessScore: 30, accessState: 'not_started',
  complianceRecords: [], documents: [], history: [], transfers: [],
};
const requestId = '8c740ad8-e69d-4af3-b09f-36cbe15eb995';
const fieldLabels = ['Preferred name', 'Personal email', 'Mobile number', 'Marital status', 'Emergency contact name', 'Emergency contact phone'];
const fieldKeys = ['preferredName', 'personalEmail', 'phone', 'maritalStatus', 'emergencyContactName', 'emergencyContactPhone'];
const initialProfile = { preferredName: 'Alex', personalEmail: 'alex.personal@example.test', phone: '+966500000001', maritalStatus: 'Single', emergencyContactName: 'Morgan Contact', emergencyContactPhone: '+966500000002' };
interface Options { persona?: 'hr' | 'employee' | 'hr-readonly' | 'employee-readonly'; status?: EmployeeCompletion['status']; selfServiceAvailable?: boolean; changes?: Record<string, string>; reviewNote?: string }

async function boot(page: Page, options: Options = {}) {
  const persona = options.persona ?? 'hr';
  const isEmployee = persona.startsWith('employee');
  const readOnly = persona.endsWith('readonly');
  let state: EmployeeCompletion = {
    requested: options.status !== undefined && options.status !== 'NotRequested', status: options.status ?? 'NotRequested',
    selfServiceAvailable: options.selfServiceAvailable ?? isEmployee, profile: initialProfile,
    ...(options.status === 'PendingHR' ? { requestId, changes: options.changes ?? { preferredName: 'Alex submitted', phone: '+966500000003' } } : {}),
    ...(options.reviewNote ? { reviewNote: options.reviewNote } : {}),
  };
  const errors: string[] = [];
  const calls: Array<{ path: string; method: string; body: unknown }> = [];
  const profileSubmissions: Array<{ changes: Record<string, string> }> = [];
  const uploads: Array<{ contentType: string; body: string }> = [];
  const documents: Array<Record<string, unknown>> = [];
  let storedEmployee = { ...employee };
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'completion-browser-fixture');
    localStorage.setItem('kynexone.theme', 'light');
  });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method();
    const contentType = request.headers()['content-type'] ?? '';
    const body = request.postData() && contentType.includes('application/json') ? request.postDataJSON() : undefined;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (method !== 'GET') calls.push({ path, method, body });
    if (path === '/api/auth/me') return reply({
      id: 'completion-user', employeeId: isEmployee ? 901 : undefined, tenantId: 'completion-tenant', tenantSlug: 'completion-fixture', fullName: isEmployee ? 'Alex Morgan' : 'HR Reviewer',
      roles: [isEmployee ? 'Employee' : readOnly ? 'Auditor' : 'HR Manager'],
      permissions: isEmployee ? ['ess.read', ...(!readOnly ? ['ess.write'] : [])] : ['employees.read', ...(!readOnly ? ['employees.write', 'employees.approve', 'employees.sensitive'] : [])],
      companies: [{ id: company.id, name: company.legalNameEn, code: 'TEST', countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/companies') return reply(paged([company]));
    if (['/api/branches', '/api/departments', '/api/designations', '/api/grades', '/api/organization/cost-centers'].includes(path)) return reply(paged([]));
    if (path === '/api/employees/field-catalog') return reply({ countryCode: 'SA', fields: [] });
    if (path === '/api/employees') return reply(paged([storedEmployee]));
    if (path === '/api/employees/901') return reply(storedEmployee);
    if (path === '/api/employees/derive-work-email') return reply({ domain: company.emailDomain, pattern: 'first.last', localPart: 'alex.it-issued', workEmail: employee.workEmail, unique: true, status: 'derived' });
    if (path.endsWith('/readiness')) return reply({ employeeId: 901, state: 'NeedsAttention', score: 30, progress: { present: 1, requiredTotal: 2 }, policy: { countryCode: 'SA', tier: 'Standard', sources: [] }, blocking: [{ key: 'personal:gender', label: 'Gender', category: 'Personal', fix: { kind: 'field', target: 'gender' } }], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: 'Synthetic browser fixture.' });
    if (path === '/api/employee-access/summary') return reply({ not_started: 1 });
    if (path === '/api/employee-access/901') return reply({ employeeId: 901, employeeName: employee.fullName, employeeCode: employee.employeeCode, workEmail: employee.workEmail, state: 'not_started', canIssue: false, reasonCode: 'employee_ineligible', blockedReason: 'Draft employee', emailDelivery: false });
    if (path === '/api/employee-completion/901') {
      if (method === 'POST') state = { ...state, requested: true, status: 'Open' };
      return reply(state);
    }
    if (path === `/api/ess/profile-change-requests/${requestId}/approve` || path === `/api/ess/profile-change-requests/${requestId}/reject`) {
      if (path.endsWith('/approve')) storedEmployee = { ...storedEmployee, ...Object.fromEntries(Object.entries(state.changes ?? {}).filter(([key]) => fieldKeys.includes(key))) };
      state = { ...state, status: path.endsWith('/approve') ? 'Approved' : 'Rejected' };
      return reply({ success: true });
    }
    if (path === '/api/ess/employee-completion') return reply(state);
    if (path === '/api/ess/employee-completion/profile') {
      profileSubmissions.push(body);
      state = { ...state, status: 'PendingHR', requestId, changes: body.changes };
      return reply(state);
    }
    if (path === '/api/ess/documents') {
      if (method === 'POST') {
        uploads.push({ contentType, body: request.postData() ?? '' });
        documents.push({ id: 'uploaded-proof', documentType: 'Bank proof', fileName: 'synthetic-bank-proof.pdf', approvalStatus: 'Pending' });
        return reply(documents[documents.length - 1], 201);
      }
      return reply(documents);
    }
    if (path.includes('/features/') || path === '/api/notifications' || path.endsWith('/documents') || path.endsWith('/history') || path.endsWith('/pay-scale')) return reply([]);
    return reply(paged([]));
  });
  await page.goto(isEmployee ? '/ess/onboarding' : '/people');
  await expect(page).toHaveURL(isEmployee ? /\/ess\/onboarding(?:\?|$)/ : /\/people(?:\?|$)/);
  await expect(page).not.toHaveTitle('');
  if (isEmployee) await expect(page.getByRole('heading', { name: 'My employee details', exact: true })).toBeVisible();
  else {
    await page.getByRole('button', { name: 'Open profile for Alex Morgan', exact: true }).click();
    const dialog = page.getByRole('dialog').filter({ hasText: 'Alex Morgan' });
    await dialog.getByRole('button', { name: 'Open full profile', exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Close employee profile', exact: true })).toBeVisible();
  }
  return { errors, calls, profileSubmissions, uploads };
}

async function evidence(page: Page, info: TestInfo, name: string) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  const path = info.outputPath(`${name}-${info.project.name}.png`);
  await page.screenshot({ path, fullPage: false, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path, contentType: 'image/png' });
}

test('HR queues employee completion without granting sign-in access or changing a Draft record', async ({ page }, info) => {
  const { errors, calls } = await boot(page);
  const handoff = page.getByTestId('employee-completion-handoff');
  await expect(handoff).toContainText('HR must complete activation requirements first.');
  await handoff.getByRole('button', { name: 'Request employee details', exact: true }).click();
  await expect(handoff.getByRole('status')).toHaveText('Completion request saved');
  await handoff.scrollIntoViewIfNeeded();
  await evidence(page, info, 'hr-completion-queued');
  expect(calls).toEqual([{ path: '/api/employee-completion/901', method: 'POST', body: undefined }]);
  expect(errors).toEqual([]);
});

for (const decision of ['approve', 'reject'] as const) {
  test(`HR explicitly ${decision}s submitted contact details through the existing approval endpoint`, async ({ page }, info) => {
    const { errors, calls } = await boot(page, { status: 'PendingHR', changes: { preferredName: 'Alex submitted', phone: '+966500000003', salary: '987654321' } });
    const handoff = page.getByTestId('employee-completion-handoff');
    await expect(handoff).toContainText('Employee details awaiting HR review');
    await expect(handoff).toContainText('Alex submitted');
    await expect(handoff).not.toContainText('987654321');
    const action = handoff.getByRole('button', { name: decision === 'approve' ? 'Approve details' : 'Return for correction', exact: true });
    if (decision === 'reject') await expect(action).toBeDisabled();
    await handoff.getByLabel('Review note', { exact: true }).fill('Please verify the contact details.');
    await handoff.scrollIntoViewIfNeeded();
    await evidence(page, info, `hr-completion-${decision}-review`);
    expect(calls).toHaveLength(0);
    await action.click();
    await expect(handoff.getByRole('status')).toHaveText(decision === 'approve' ? 'Employee details approved' : 'Returned to employee for correction');
    const preferredName = page.locator('dl > div').filter({ has: page.locator('dt').filter({ hasText: /^Preferred name$/ }) });
    await expect(preferredName.getByRole('definition')).toHaveText(decision === 'approve' ? 'Alex submitted' : 'Alex');
    expect(calls).toEqual([{ path: `/api/ess/profile-change-requests/${requestId}/${decision}`, method: 'POST', body: { notes: 'Please verify the contact details.' } }]);
    expect(errors).toEqual([]);
  });
}

test('an employee submits only their six contact fields and waits for HR approval', async ({ page }, info) => {
  const { errors, calls, profileSubmissions } = await boot(page, { persona: 'employee', status: 'Open' });
  const completion = page.getByTestId('employee-self-completion');
  for (const label of fieldLabels) await expect(completion.getByRole(label === 'Marital status' ? 'combobox' : 'textbox', { name: label, exact: true })).toBeVisible();
  await expect(completion.getByLabel('Preferred name', { exact: true })).toHaveValue('Alex');
  await completion.getByLabel('Preferred name', { exact: true }).fill('Alex submitted');
  await completion.getByLabel('Mobile number', { exact: true }).fill('+966500000003');
  await expect(completion.getByLabel(/salary|IBAN|work email|English full name/i)).toHaveCount(0);
  await evidence(page, info, 'employee-contact-completion');
  await completion.getByRole('button', { name: 'Send details to HR', exact: true }).click();
  await expect(completion).toContainText('Your details were sent to HR for review. They do not change your record until approved.');
  await expect(completion.getByRole('button', { name: 'Send details to HR', exact: true })).toHaveCount(0);
  for (const label of fieldLabels) await expect(completion.getByRole(label === 'Marital status' ? 'combobox' : 'textbox', { name: label, exact: true })).toBeDisabled();
  expect(profileSubmissions).toHaveLength(1);
  expect(Object.keys(profileSubmissions[0].changes).sort()).toEqual([...fieldKeys].sort());
  expect(profileSubmissions[0].changes).toMatchObject({ preferredName: 'Alex submitted', phone: '+966500000003' });
  expect(calls.map(call => call.path)).toEqual(['/api/ess/employee-completion/profile']);
  await page.reload();
  await expect(page.getByTestId('employee-self-completion')).toContainText('Employee details awaiting HR review');
  await expect(page.getByLabel('Preferred name', { exact: true })).toHaveValue('Alex submitted');
  expect(errors).toEqual([]);
});

test('bank proof is uploaded for review without writing payment details', async ({ page }, info) => {
  const { errors, calls, uploads, profileSubmissions } = await boot(page, { persona: 'employee', status: 'Open' });
  const completion = page.getByTestId('employee-self-completion');
  await completion.getByRole('combobox', { name: 'Document type', exact: true }).selectOption('Bank proof');
  await expect(completion.getByLabel('Document number', { exact: true })).toHaveCount(0);
  await expect(completion.getByLabel('Expiry date', { exact: true })).toHaveCount(0);
  await completion.getByLabel('Document file', { exact: false }).setInputFiles({ name: 'synthetic-bank-proof.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\nSynthetic test fixture. No personal or bank data.\n%%EOF') });
  await completion.getByRole('button', { name: 'Upload for HR review', exact: true }).click();
  await expect(completion).toContainText('Document uploaded for HR review. Uploading bank proof does not change your payment details.');
  await expect(completion.getByRole('listitem').filter({ hasText: 'synthetic-bank-proof.pdf' })).toContainText('Pending');
  await completion.getByRole('listitem').filter({ hasText: 'synthetic-bank-proof.pdf' }).scrollIntoViewIfNeeded();
  await evidence(page, info, 'employee-bank-proof-pending');
  expect(uploads).toHaveLength(1);
  expect(uploads[0].contentType).toContain('multipart/form-data; boundary=');
  expect(uploads[0].body).toContain('name="documentType"');
  expect(uploads[0].body).toContain('Bank proof');
  expect(uploads[0].body).not.toContain('name="expiryDate"');
  expect(profileSubmissions).toHaveLength(0);
  expect(calls.map(call => call.path)).toEqual(['/api/ess/documents']);
  expect(errors).toEqual([]);
});

test('read-only self-service cannot submit contact details or upload documents', async ({ page }) => {
  const { errors, calls } = await boot(page, { persona: 'employee-readonly', status: 'Open' });
  const completion = page.getByTestId('employee-self-completion');
  await expect(completion.getByText('Your account can view self-service but cannot send requests. Ask HR if you need to.', { exact: true })).toHaveCount(2);
  await expect(completion.locator('input, select, textarea')).toHaveCount(0);
  await expect(completion.getByRole('button', { name: /Send details|Upload for/ })).toHaveCount(0);
  expect(calls).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('read-only HR cannot request or approve employee completion', async ({ page }) => {
  const { errors, calls } = await boot(page, { persona: 'hr-readonly', status: 'PendingHR' });
  await expect(page.getByTestId('employee-completion-handoff')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Request employee details|Approve details|Return for correction/ })).toHaveCount(0);
  expect(calls).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('returned details show the HR correction note and can be resubmitted', async ({ page }) => {
  const { errors, profileSubmissions } = await boot(page, { persona: 'employee', status: 'Rejected', reviewNote: 'Please correct the emergency phone number.' });
  const completion = page.getByTestId('employee-self-completion');
  await expect(completion).toContainText('Please correct the emergency phone number.');
  await expect(completion.getByLabel('Emergency contact phone', { exact: true })).toBeEditable();
  await completion.getByLabel('Emergency contact phone', { exact: true }).fill('+966500000004');
  await completion.getByRole('button', { name: 'Send details to HR', exact: true }).click();
  await expect(completion).toContainText('Employee details awaiting HR review');
  expect(profileSubmissions).toHaveLength(1);
  expect(profileSubmissions[0].changes.emergencyContactPhone).toBe('+966500000004');
  expect(errors).toEqual([]);
});
