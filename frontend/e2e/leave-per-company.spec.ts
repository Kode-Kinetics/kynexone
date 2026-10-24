import { expect, test, type Page } from '@playwright/test';

// Leave types and policies belong to one company (2026-10-23). A mocked API: browser contracts only, no tenant writes.
const A = { id: 'company-a', legalNameEn: 'Alpha Arabia LLC' };
const B = { id: 'company-b', legalNameEn: 'Beta Gulf LLC' };
const types = [
  { id: 'annual-a', code: 'ANNUAL', nameEn: 'Annual A', category: 'Annual', isPaid: true, isActive: true, companyId: A.id },
  { id: 'annual-b', code: 'ANNUAL', nameEn: 'Annual B', category: 'Annual', isPaid: true, isActive: true, companyId: B.id },
  { id: 'exam-legacy', code: 'EXAM', nameEn: 'Exam legacy', category: 'Study', isPaid: true, isActive: true, companyId: null },
];
const policies = [
  { id: 'policy-a', name: 'Alpha annual', leaveTypeId: 'annual-a', companyId: A.id, countryCode: 'KSA', annualEntitlementDays: 21, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full' },
  { id: 'policy-b', name: 'Beta annual', leaveTypeId: 'annual-b', companyId: B.id, countryCode: 'UAE', annualEntitlementDays: 30, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full' },
];

async function boot(page: Page, url: string) {
  const writes: { path: string; body: unknown }[] = [];
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  if (process.env.LEAVE_DEBUG) page.on('console', m => console.log('CONSOLE', m.type(), m.text().slice(0, 300)));
  if (process.env.LEAVE_DEBUG) page.on('requestfailed', r => console.log('REQFAIL', r.url(), r.failure()?.errorText));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
  });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname; if (process.env.LEAVE_DEBUG) console.log('API', request.method(), path);
    const reply = (json: unknown) => route.fulfill({ json });
    if (request.method() !== 'GET') { writes.push({ path, body: request.postDataJSON() }); return reply({ id: 'saved' }); }
    if (path === '/api/auth/me') return reply({ id: 'hr', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'HR', roles: ['Admin'], permissions: ['leave.read', 'leave.write', 'leave.policy_manage', 'organization.read'], accountType: 'Group', isGroupScope: true, companies: [A, B].map(c => ({ id: c.id, name: c.legalNameEn, code: c.id, countryCode: 'SAU', isActive: true })) });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return reply([]);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/leave/holidays/calendars' || path === '/api/leave/holidays/blackouts') return reply([]);
    if (path === '/api/companies' || path === '/api/companies/all') return reply({ items: [A, B], total: 2, page: 1, pageSize: 100 });
    if (path === '/api/leave/types') return reply(types);
    if (path === '/api/leave/policies') return reply({ items: policies, total: policies.length, page: 1, pageSize: 100 });
    if (path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(url);
  // The dev server compiles the Leave view on first visit.
  await expect(page.getByRole('heading', { name: 'Leave & Absence Management' })).toBeVisible({ timeout: 90_000 });
  return { writes, errors };
}

test('leave types show their company, filter by company incl. unassigned, and a legacy type is assigned from its card', async ({ page }) => {
  const { writes, errors } = await boot(page, '/leave');
  await page.getByRole('tab', { name: 'Leave Types' }).click();
  const cards = page.getByTestId('leave-type-card');
  await expect(cards).toHaveCount(3);
  await expect(page.getByText('1 rows have no company yet', { exact: false })).toBeVisible();
  const filter = page.getByRole('tabpanel').getByRole('combobox', { name: 'Filter by company' });
  await filter.selectOption(B.id);
  await expect(cards).toHaveCount(1);
  await expect(cards.first()).toContainText('Beta Gulf LLC');
  await filter.selectOption({ label: 'Unassigned — choose the company' });
  await expect(cards).toHaveCount(1);
  await expect(cards.first()).toContainText('Unassigned');
  await cards.first().getByRole('combobox', { name: 'Company' }).selectOption(A.id);
  await expect.poll(() => writes.find(w => w.path === '/api/leave/types/exam-legacy')?.body).toEqual({ companyId: A.id });
  expect(errors).toEqual([]);
});

test('/leave?companyId= opens that company\'s policies, and a new policy asks for the company preselected', async ({ page }) => {
  const { writes, errors } = await boot(page, `/leave?companyId=${B.id}`);
  const rows = page.getByTestId('leave-policy-row');
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText('Beta annual');
  await expect(rows.first()).toContainText('Beta Gulf LLC');
  await page.getByRole('button', { name: 'New Policy' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('combobox', { name: 'Company' })).toHaveValue(B.id);
  // Only Beta's types (and the unassigned legacy one) are offered.
  const typeSelect = dialog.locator('select').filter({ has: page.locator('option', { hasText: 'Select…' }) });
  await expect(typeSelect.locator('option')).toHaveText(['Select…', 'Annual B', 'Exam legacy']);
  await dialog.getByPlaceholder('e.g. UAE Annual Leave — Full-Time').fill('Beta extra');
  await typeSelect.selectOption('annual-b');
  await dialog.getByRole('button', { name: /Create|Save/ }).last().click();
  await expect.poll(() => (writes.find(w => w.path === '/api/leave/policies')?.body as { companyId?: string } | undefined)?.companyId).toBe(B.id);
  expect(errors).toEqual([]);
});

test('editing a policy shows its company locked', async ({ page }) => {
  await boot(page, `/leave?companyId=${A.id}`);
  await page.getByTestId('leave-policy-row').first().getByRole('button', { name: 'Edit' }).click();
  const company = page.getByRole('dialog').getByRole('combobox', { name: 'Company' });
  await expect(company).toBeDisabled();
  await expect(page.getByRole('dialog')).toContainText("The company can't be changed after it is set.");
});
