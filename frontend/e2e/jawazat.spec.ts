import { expect, test, type Page } from '@playwright/test';

const reviewedPolicy = { schemaVersion: 1, employerAssistedEnabled: true, workerNotificationEnabled: true, maximumTripDays: 60, minimumPassportValidityDays: 120, ruleVersion: 'HR-2026-10', reviewedBy: 'reviewer-1', reviewedAtUtc: '2026-10-01T12:00:00Z' };
const policy = { profileId: 'profile-1', companyId: 'company-1', effectiveFrom: '2026-01-01', effectiveTo: null, policy: reviewedPolicy, canCreateEmployerAssisted: true, canRecordWorkerNotification: true };
const checks = [{ code: 'government_status', result: 'Unknown', evidenceSource: 'Provider unavailable', evidenceAtUtc: null, reason: 'Government records have not been verified.' }];
const evaluation = { policySnapshot: { ...policy, capturedAtUtc: '2026-10-04T12:00:00Z' }, checks, canSubmitToGovernment: false };
function ticket(body: any, state = 'PendingApproval') { return { id: 'request-1', employeeId: 17, companyId: 'company-1', subject: 'Exit and re-entry', status: 'InProgress', approvalRequestId: state === 'NotificationRecorded' ? null : 'approval-1', workflowVersion: 1, createdAtUtc: '2026-10-04T12:00:00Z', data: { ...body, schemaVersion: 1, internalState: state, providerState: 'NotSubmitted', policySnapshot: evaluation.policySnapshot, checks } }; }

async function boot(page: Page, route: string, role: string, handler: (path: string, method: string, body: any) => unknown | Promise<unknown>) {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('status of 503')) errors.push(message.text()); });
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'fixture'));
  await page.route('**/api/**', async interception => {
    const request = interception.request(); const path = new URL(request.url()).pathname;
    const response = await handler(path, request.method(), request.postData() ? request.postDataJSON() : null);
    if (response && typeof response === 'object' && '__status' in response && 'body' in response) return interception.fulfill({ status: Number(response.__status), json: response.body });
    if (response !== undefined) return interception.fulfill({ json: response });
    if (path === '/api/auth/me') return interception.fulfill({ json: { id: 'user-1', employeeId: 17, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Amira Mansour', roles: [role], permissions: ['ess.read', 'compliance.read', 'compliance.write'], companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] } });
    if (path.endsWith('/jawazat/capabilities')) return interception.fulfill({ json: { provider: 'Disabled', isAvailable: false, supportedOperations: [], message: 'Government connection is unavailable.' } });
    if (path.endsWith('/jawazat/policy')) return interception.fulfill({ json: policy });
    if (path.endsWith('/jawazat/evaluate')) return interception.fulfill({ json: evaluation });
    if (path.endsWith('/jawazat/requests')) return interception.fulfill({ json: [] });
    if (path === '/api/employees') return interception.fulfill({ json: { items: [{ id: 17, publicId: 'emp-1', fullName: 'Amira Mansour', employeeCode: 'EMP-17', department: 'Operations', status: 'Active' }], total: 1 } });
    if (path === '/api/tenant-admin/localization') return interception.fulfill({ json: { currencyCode: 'SAR' } });
    if (path.includes('/features/') || path === '/api/notifications') return interception.fulfill({ json: [] });
    return interception.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto(route);
  await expect(page).toHaveURL(url => `${url.pathname}${url.search}` === route);
  await expect(page).toHaveTitle(/Kynex/i);
  return errors;
}

async function fillTrip(page: Page) {
  await page.getByLabel('Departure date', { exact: true }).fill('2027-01-10');
  await page.getByLabel('Return date', { exact: true }).fill('2027-01-20');
  await page.getByLabel('Travel reason', { exact: true }).fill('Family visit');
}

test('employee previews and records a worker notification without employer consent or government issuance', async ({ page }, info) => {
  let created: any;
  const errors = await boot(page, '/ess/jawazat', 'Employee', (path, method, body) => {
    if (path.endsWith('/jawazat/policy')) return { ...policy, policy: { ...reviewedPolicy, employerAssistedEnabled: false, workerNotificationEnabled: false }, canCreateEmployerAssisted: false, canRecordWorkerNotification: true };
    if (path.endsWith('/jawazat/evaluate')) return { ...evaluation, checks: [...checks, { code: 'company_trip_limit', result: 'Failed', evidenceSource: 'Company policy', evidenceAtUtc: null, reason: 'Company trip guidance requires review; this does not block recording a notification.' }] };
    if (path.endsWith('/jawazat/requests') && method === 'POST') { created = body; return ticket(body, 'NotificationRecorded'); }
  });
  await expect(page.getByRole('heading', { name: 'My Jawazat requests' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  if (process.env.JAWAZAT_EVIDENCE_DIR) await page.screenshot({ path: `${process.env.JAWAZAT_EVIDENCE_DIR}/jawazat-entry-${info.project.name}.png` });
  await expect(page.getByPlaceholder('Search by name or code…')).toHaveCount(0);
  await page.getByLabel('Request route').selectOption('WorkerSelfServiceNotification');
  await fillTrip(page);
  await expect(page.getByRole('button', { name: 'Record notification', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Preview request', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Request preview' })).toContainText('Unknown');
  await expect(page.getByRole('region', { name: 'Request preview' })).toContainText('Government records have not been verified.');
  await page.getByRole('button', { name: 'Record notification', exact: true }).click();
  await expect(page.getByText('Notification recorded', { exact: true })).toBeVisible();
  await expect(page.getByText('This notification is not employer consent or a government-issued visa.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Submit to government' })).toHaveCount(0);
  expect(created.employeeId).toBeUndefined();
  expect(created.route).toBe('WorkerSelfServiceNotification');
  expect(created.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/);
  expect(errors).toEqual([]);
  if (process.env.JAWAZAT_EVIDENCE_DIR) await page.screenshot({ path: `${process.env.JAWAZAT_EVIDENCE_DIR}/jawazat-worker-${info.project.name}.png` });
});

test('HR selects an employee and invalidates the preview when travel details change', async ({ page }) => {
  let created: any;
  const errors = await boot(page, '/compliance?tab=jawazat', 'HR Manager', (path, method, body) => {
    if (path.endsWith('/jawazat/requests') && method === 'POST') { created = body; return ticket(body); }
  });
  await page.getByPlaceholder('Search by name or code…').fill('Amira');
  await page.getByRole('button', { name: /Amira Mansour EMP-17/ }).click();
  await fillTrip(page);
  await page.getByRole('button', { name: 'Preview request', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Request employer assistance', exact: true })).toBeEnabled();
  await page.getByLabel('Return date', { exact: true }).fill('2027-01-21');
  await expect(page.getByRole('button', { name: 'Request employer assistance', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Preview request', exact: true }).click();
  await page.getByRole('button', { name: 'Request employer assistance', exact: true }).click();
  await expect(page.getByText('Awaiting internal approval', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open Approvals Center' })).toBeVisible();
  expect(created.employeeId).toBe(17); expect(created.returnDate).toBe('2027-01-21');
  expect(errors).toEqual([]);
});

test('retry after an uncertain create keeps the same idempotency key and request', async ({ page }) => {
  const bodies: any[] = [];
  const errors = await boot(page, '/ess/jawazat', 'Employee', (path, method, body) => {
    if (path.endsWith('/jawazat/requests') && method === 'POST') {
      bodies.push(body);
      return bodies.length === 1 ? { __status: 503, body: { message: 'Connection interrupted. Retry this request.' } } : ticket(body);
    }
  });
  await fillTrip(page);
  await page.getByRole('button', { name: 'Preview request', exact: true }).click();
  await page.getByRole('button', { name: 'Request employer assistance', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Jawazat requests', exact: true }).getByRole('alert')).toContainText('Connection interrupted');
  await page.getByRole('button', { name: 'Request employer assistance', exact: true }).click();
  await expect(page.getByText('Awaiting internal approval', { exact: true })).toBeVisible();
  expect(bodies).toHaveLength(2); expect(bodies[1]).toEqual(bodies[0]);
  expect(errors).toEqual([]);
});

test('internal approval stays separate from unavailable government submission', async ({ page }, info) => {
  const approved = ticket({ route: 'EmployerAssisted', service: 'ExitReentryIssue', departureDate: '2027-01-10', returnDate: '2027-01-20', reason: 'Family visit' }, 'Approved');
  const errors = await boot(page, '/compliance?tab=jawazat', 'HR Manager', path => {
    if (path.endsWith('/jawazat/requests')) return [approved];
    if (path.endsWith('/jawazat/requests/request-1')) return approved;
  });
  await page.getByRole('button', { name: 'View request request-1' }).click();
  await expect(page.getByRole('region', { name: 'Request details' })).toContainText('Internally approved');
  await expect(page.getByRole('region', { name: 'Request details' })).toContainText('Government status: Not submitted');
  await expect(page.getByRole('button', { name: 'Submit to government' })).toBeDisabled();
  await expect(page.getByRole('link', { name: 'Open Approvals Center' })).toBeVisible();
  expect(errors).toEqual([]);
  if (process.env.JAWAZAT_EVIDENCE_DIR) await page.screenshot({ path: `${process.env.JAWAZAT_EVIDENCE_DIR}/jawazat-hr-${info.project.name}.png` });
});

test('late evaluation cannot restore a stale preview', async ({ page }) => {
  let resolve!: (value: unknown) => void;
  let started = false;
  await boot(page, '/ess/jawazat', 'Employee', path => {
    if (path.endsWith('/jawazat/evaluate')) { started = true; return new Promise(value => { resolve = value; }); }
  });
  await fillTrip(page);
  await page.getByRole('button', { name: 'Preview request', exact: true }).click();
  await expect.poll(() => started).toBe(true);
  await page.getByLabel('Return date', { exact: true }).fill('2027-01-22');
  resolve(evaluation);
  await expect(page.getByRole('button', { name: 'Preview request', exact: true })).toBeEnabled();
  await expect(page.getByRole('region', { name: 'Request preview' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Request employer assistance', exact: true })).toBeDisabled();
});

test('policy review retains existing compliance fields and stamps the actual reviewer', async ({ page }) => {
  const profile = { id: 'profile-1', companyId: 'company-1', countryCode: 'SA', jurisdiction: 'Existing jurisdiction', compliancePack: 'Existing pack', effectiveFrom: '2026-01-01', effectiveTo: null, status: 'Active', requiredFieldsJson: '[{"field":"IqamaNumber","failClosed":true}]', notes: 'Retain this evidence', jawazatPolicyJson: '' };
  let saved: any;
  const errors = await boot(page, '/compliance-profiles', 'Compliance Officer', (path, method, body) => {
    if (path.endsWith('/company-compliance-profiles/readiness')) return { profile, totalEmployees: 1, requiredFields: [], disclaimer: '' };
    if (path.endsWith('/company-compliance-profiles/profile-1/jawazat-policy') && method === 'PATCH') { saved = body; return { ...profile, notes: 'Concurrent administrator edit', jawazatPolicyJson: body.jawazatPolicyJson }; }
  });
  await expect(page.getByLabel('Enable employer-assisted requests')).not.toBeChecked();
  await expect(page.getByLabel('Policy rule version')).toHaveValue('');
  await page.getByLabel('Enable employer-assisted requests').check();
  await page.getByLabel('Maximum trip days').fill('60');
  await page.getByLabel('Minimum passport validity days').fill('120');
  await page.getByLabel('Policy rule version').fill('HR-REVIEW-2026');
  await expect(page.getByRole('button', { name: 'Save Jawazat policy' })).toBeDisabled();
  await page.getByLabel('I have reviewed this company policy and its rule version.').check();
  await page.getByRole('button', { name: 'Save Jawazat policy' }).click();
  await expect(page.getByRole('status')).toContainText('Jawazat policy saved');
  expect(Object.keys(saved)).toEqual(['jawazatPolicyJson']);
  expect(saved.requiredFieldsJson).toBeUndefined();
  expect(saved.notes).toBeUndefined(); expect(saved.jurisdiction).toBeUndefined();
  const savedPolicy = JSON.parse(saved.jawazatPolicyJson);
  expect(savedPolicy.reviewedBy).toBe('user-1'); expect(savedPolicy.ruleVersion).toBe('HR-REVIEW-2026');
  expect(Date.parse(savedPolicy.reviewedAtUtc)).toBeGreaterThan(Date.now() - 60_000);
  expect(errors).toEqual([]);
});
