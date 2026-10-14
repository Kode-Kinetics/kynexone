import { expect, test, type Page } from '@playwright/test';

const areas = [
  ['organization', 'Organization and core HR'], ['recruitment', 'Recruitment'],
  ['onboarding', 'Onboarding and confirmation'], ['time', 'Time and attendance'],
  ['leave', 'Leave and absence'], ['compensation', 'Compensation and benefits'],
  ['payroll', 'Payroll'], ['performance', 'Performance and talent'],
  ['learning', 'Learning and development'], ['services', 'Employee services and relations'],
  ['offboarding', 'Offboarding and settlement'], ['compliance', 'Compliance and statutory administration'],
];

const browserErrors = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('Failed to load resource')) errors.push(message.text()); });
});
test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page), 'No application runtime errors').toEqual([]);
});

// API interception is deliberate: these are browser navigation contracts, never tenant writes.
async function boot(page: Page, options: { disabled?: boolean; restricted?: boolean; flagError?: boolean; area?: string; setting?: string; arabic?: boolean; group?: boolean; officer?: boolean; authDelay?: number; finance?: boolean; statutory?: boolean; beforeSetup?: boolean } = {}) {
  const writes: string[] = [];
  const reads: string[] = [];
  await page.addInitScript(({ arabic }) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  }, { arabic: options.arabic });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    if (path === '/api/auth/me' && options.authDelay) await new Promise(resolve => setTimeout(resolve, options.authDelay));
    const reply = (json: unknown) => route.fulfill({ json });
    if (request.method() !== 'GET') writes.push(path); else reads.push(path);
    const company = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
    const companies = options.group ? [company, { ...company, id: 'second-company', legalNameEn: 'Second Fixture LLC' }] : [company];
    if (path === '/api/auth/me') return reply({ id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: [options.officer ? 'HR Officer' : 'Admin'], permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', ...(options.finance ? ['finance.gl.manage'] : []), ...(options.statutory ? ['payroll.rates.statutory_override'] : []), ...(options.restricted ? [] : ['learning.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'payroll.read', 'performance.read'])], accountType: 'Group', isGroupScope: true, companies: companies.map(item => ({ id: item.id, name: item.legalNameEn, code: item.id, countryCode: 'SAU', isActive: true })) });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/country-packs/available') return reply([]);
    if (path === '/api/reference/countries') return reply([{ code: 'SA', name: 'Saudi Arabia' }]);
    if (path === '/api/admin/system-settings') return reply([]);
    if (path === '/api/finance/gl/accounts' || path === '/api/finance/gl/mappings') return reply([]);
    if (path === '/api/dashboard/full') return route.fulfill({ status: 503, json: { message: 'Dashboard fixture unavailable' } });
    if (path === '/api/recruitment/onboarding/summary') return reply({ total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 });
    if (path === '/api/companies') return reply({ items: companies, total: companies.length, page: 1, pageSize: 100 });
    if (path === '/api/features/disabled-keys') {
      if (options.flagError) return route.fulfill({ status: 503, json: { message: 'Fixture unavailable' } });
      return reply(options.disabled ? ['release_a', 'learning'] : ['release_a']);
    }
    if (path.includes('/features/')) return reply(options.disabled ? [{ key: 'learning', labelEn: 'Learning', enabled: false, navPaths: ['/learning'] }] : []);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/leave/types') return reply([{ id: 'annual-type', code: 'ANNUAL', nameEn: 'Annual', category: 'Annual', isPaid: true, isActive: true }]);
    if (path === '/api/leave/policies') return reply({ items: [{ id: 'existing-policy', name: 'Saved annual policy', leaveTypeId: 'annual-type', countryCode: 'KSA', annualEntitlementDays: 28, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full' }], total: 1 });
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  const setupUrl = `/setup?tab=aiSetup&companyId=fixture-company&source=retained${options.area ? `&area=${options.area}` : ''}${options.setting ? `&setting=${options.setting}` : ''}`;
  if (options.beforeSetup) {
    await page.goto('/dashboard?before=0');
    await expect(page.getByRole('navigation', { name: 'Primary navigation' })).toBeAttached();
    await page.evaluate(() => {
      history.pushState(history.state, '', '/dashboard?before=1');
      history.pushState(history.state, '', '/dashboard?before=2');
    });
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Administration', exact: true }).evaluate((button: HTMLButtonElement) => button.click());
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Setup', exact: true }).evaluate((button: HTMLButtonElement) => button.click());
    await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
    await page.evaluate(url => history.replaceState({ fixtureMarker: 'preserved' }, '', url), setupUrl);
    await expect(page.getByRole('button', { name: 'Checklist Templates', exact: true })).toBeVisible();
  } else await page.goto(setupUrl);
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible({ timeout: 30_000 });
  return { writes, reads };
}

test('manual_leave_edit_uses_existing_put', async ({ page }, info) => {
  const { writes } = await boot(page, { area: 'leave', setting: 'leavePolicies' });
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  const dialog = page.locator('div.fixed').filter({ has: page.getByRole('heading', { name: 'Edit Leave Policy', exact: true }) });
  await expect(dialog.locator('input[type=number]').first()).toHaveValue('28');
  await dialog.locator('input[type=number]').first().fill('30');
  await page.screenshot({ path: `/tmp/setup-canonical-leave-${info.project.name}.png`, fullPage: true });
  const request = page.waitForRequest(request => request.method() === 'PUT' && new URL(request.url()).pathname === '/api/leave/policies/existing-policy');
  await dialog.getByRole('button', { name: 'Save Changes' }).click();
  expect((await request).postDataJSON()).toMatchObject({ name: 'Saved annual policy', annualEntitlementDays: 30, leaveTypeId: 'annual-type' });
  expect(writes).toEqual(['/api/leave/policies/existing-policy']);
});

test('existing_record_identity_survives_edit', async ({ page }) => {
  await boot(page, { area: 'leave', setting: 'leavePolicies' });
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByPlaceholder('e.g. UAE Annual Leave — Full-Time')).toHaveValue('Saved annual policy');
  await page.getByPlaceholder('e.g. UAE Annual Leave — Full-Time').fill('Renamed annual policy');
  const request = page.waitForRequest(request => request.method() === 'PUT' && request.url().includes('/leave/policies/'));
  await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
  expect(new URL((await request).url()).pathname).toBe('/api/leave/policies/existing-policy');
});

test('unauthorized_editor_does_not_mount', async ({ page }) => {
  const { reads } = await boot(page, { officer: true, area: 'leave', setting: 'leavePolicies' });
  await expect(page.getByRole('button', { name: 'New Policy', exact: true })).toHaveCount(0);
  expect(reads.filter(path => path.startsWith('/api/leave/'))).toEqual([]);
});

test('tenant_wide_onboarding_not_labelled_company_only', async ({ page }) => {
  const { reads } = await boot(page, { area: 'onboarding', setting: 'onboarding' });
  await expect(page.getByRole('button', { name: 'Checklist Templates', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-area-content').getByText(/Onboarding checklists · Tenant-wide controls/)).toBeVisible();
  await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create Checklist', exact: true })).toBeVisible();
  await expect(page.getByPlaceholder('Employee ID', { exact: true })).toHaveCount(0);
  expect(reads.filter(path => path.includes('/onboarding/tasks') || path.includes('/onboarding/summary'))).toEqual([]);
});

test('existing_setup_deep_links_still_work', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'companies' });
  await expect(page.getByRole('button', { name: /Add Company/ })).toBeVisible();
  await page.goto('/setup?tab=companies');
  await expect(page.getByRole('button', { name: /Add Company/ })).toBeVisible();
});

test('direct_settings_company_switch_decline_preserves_draft_and_accept_refreshes_scope', async ({ page }) => {
  const { writes } = await boot(page, { group: true });
  await page.goto('/setup?tab=branches');
  await page.getByRole('button', { name: 'Add Branch', exact: true }).click();
  await page.getByPlaceholder('DXB-01', { exact: true }).fill('DRAFT-BRANCH');
  await page.getByPlaceholder('Dubai Head Office', { exact: true }).fill('Keep this branch');
  const switchCompany = async () => {
    // Exercise the global context action while its editor modal is open. The modal
    // covers the top bar, so dispatch to the actual controls instead of its backdrop.
    await page.getByTestId('company-switcher').getByRole('button').dispatchEvent('click');
    await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).dispatchEvent('click');
  };
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  await switchCompany();
  await expect(page.getByPlaceholder('DXB-01', { exact: true })).toHaveValue('DRAFT-BRANCH');
  await expect(page.getByPlaceholder('Dubai Head Office', { exact: true })).toHaveValue('Keep this branch');
  await expect(page.getByTestId('company-switcher').getByRole('button')).toContainText('fixture-company');
  expect(prompts).toBe(1);
  const retainedRequest = page.waitForRequest(request => new URL(request.url()).pathname === '/api/companies');
  await page.evaluate(() => window.dispatchEvent(new Event('kynexone:companies-changed')));
  expect((await retainedRequest).headers()['x-company-id']).toBe('fixture-company');
  page.off('dialog', decline);
  page.once('dialog', dialog => dialog.accept());
  const refreshed = page.waitForRequest(request => new URL(request.url()).pathname === '/api/branches' && request.headers()['x-company-id'] === 'second-company');
  await switchCompany();
  await refreshed;
  await expect(page.getByRole('dialog', { name: 'Add Branch', exact: true })).toHaveCount(0);
  await expect(page.getByTestId('company-switcher').getByRole('button')).toContainText('second-company');
  expect(writes).toEqual([]);
});

test('unsaved_template_blocks_area_and_company_switch_when_declined', async ({ page }) => {
  await boot(page, { group: true, area: 'onboarding', setting: 'onboarding' });
  await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep this draft');
  let prompts = 0;
  page.on('dialog', async dialog => { prompts++; await dialog.dismiss(); });
  await selectArea(page, 'Payroll');
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep this draft');
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await expect(page).toHaveURL(/companyId=fixture-company/);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep this draft');
  expect(prompts).toBe(2);
});

test('transitional_route_has_supported_return_to_selected_area', async ({ page }) => {
  await boot(page, { area: 'leave' });
  await page.getByTestId('setup-area-content').getByRole('link', { name: 'Open workspace', exact: true }).click();
  await page.getByRole('link', { name: 'Back to Setup Studio', exact: true }).click();
  await expect(page).toHaveURL(/area=leave/);
  await expect(page).toHaveURL(/companyId=fixture-company/);
});

test('source_pages_still_use_the_canonical_forms', async ({ page }) => {
  const { writes } = await boot(page);
  await page.goto('/leave');
  await page.getByRole('tab', { name: 'Policies', exact: true }).click();
  await expect(page.getByText('Saved annual policy', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByPlaceholder('e.g. UAE Annual Leave — Full-Time')).toHaveValue('Saved annual policy');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.goto('/recruitment/onboarding');
  await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create Checklist', exact: true })).toBeVisible();
  await expect(page.getByPlaceholder('Employee ID', { exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});

async function selectArea(page: Page, label: string) {
  const disclosure = page.getByRole('button', { name: /^Choose setup area/ });
  if (await disclosure.isVisible()) await disclosure.click();
  await page.getByRole('navigation', { name: 'Setup areas', exact: true }).getByRole('button', { name: label, exact: true }).click();
}

async function clickDashboard(page: Page) {
  const mobile = page.getByRole('navigation', { name: 'Primary', exact: true }).getByRole('link', { name: 'Home', exact: true });
  if (await mobile.isVisible()) await mobile.click();
  else await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Dashboard', exact: true }).click();
}

test('return_from_library_preserves_area_and_company', async ({ page }) => {
  const { writes } = await boot(page, { group: true, area: 'leave' });
  await page.getByRole('button', { name: 'Policy library', exact: true }).click();
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await page.getByRole('button', { name: 'Setup Studio', exact: true }).click();
  await expect(page).toHaveURL(/area=leave/);
  await expect(page).toHaveURL(/companyId=second-company/);
  await expect(page.getByTestId('setup-studio-workspace')).toContainText('Second Fixture LLC');
  expect(writes).toEqual([]);
});

test('cancel_return_keeps_dirty_edits_on_back_forward_and_sidebar', async ({ page }) => {
  await boot(page, { area: 'payroll' });
  await selectArea(page, 'Onboarding and confirmation');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Onboarding and confirmation', exact: true })).toBeVisible();
  await page.getByTestId('setup-area-content').getByRole('button', { name: 'Open settings', exact: true }).first().click();
  await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep history draft');
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  const originalUrl = page.url();
  await page.evaluate(() => history.back());
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep history draft');
  await clickDashboard(page);
  await expect.poll(() => prompts).toBe(2);
  await expect(page).toHaveURL(originalUrl);
  page.off('dialog', decline);
  page.once('dialog', dialog => dialog.accept());
  await page.evaluate(() => history.back());
  await expect(page).not.toHaveURL(originalUrl);
  await page.evaluate(() => history.forward());
  await expect(page).toHaveURL(originalUrl);
});

test('unsupported_area_does_not_claim_prefill', async ({ page }) => {
  const { writes } = await boot(page, { area: 'leave' });
  await expect(page.getByTestId('setup-area-content')).toContainText('Document filling is not connected for this area');
  await expect(page.getByRole('button', { name: 'Fill from document', exact: true })).toHaveCount(0);
  await selectArea(page, 'Onboarding and confirmation');
  await expect(page.getByTestId('setup-area-content')).toContainText('Onboarding task suggestions can be prepared');
  expect(writes).toEqual([]);
});

for (const fallback of [false, true]) test(`${fallback ? 'fallback_' : ''}declined_forward_keeps_editor_and_url`, async ({ page }) => {
  if (fallback) await page.addInitScript(() => Object.defineProperty(window, 'navigation', { value: undefined, configurable: true }));
  await boot(page, { area: 'onboarding', setting: 'onboarding' });
  await selectArea(page, 'Payroll');
  await expect(page).toHaveURL(/area=payroll/);
  await page.evaluate(() => history.back());
  await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep forward draft');
  const originalUrl = page.url();
  let prompts = 0;
  page.on('dialog', async dialog => { prompts++; await dialog.dismiss(); });
  await page.evaluate(() => history.forward());
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep forward draft');
});

test('history_fallback_decline_retains_actual_editor', async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(window, 'navigation', { value: undefined, configurable: true }));
  await boot(page, { area: 'payroll', authDelay: 150 });
  await selectArea(page, 'Onboarding and confirmation');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Onboarding and confirmation', exact: true })).toBeVisible();
  await page.getByTestId('setup-area-content').getByRole('button', { name: 'Open settings', exact: true }).first().click();
  await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep fallback draft');
  const originalUrl = page.url();
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  await page.evaluate(() => history.back());
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep fallback draft');
  page.off('dialog', decline);
  page.once('dialog', dialog => dialog.accept());
  await clickDashboard(page);
  await expect(page).toHaveURL(/\/dashboard$/);
  await page.evaluate(() => history.pushState(history.state, '', '/dashboard?history-check=1'));
  await expect(page).toHaveURL(/history-check=1/);
  await page.evaluate(() => history.back());
  await expect(page).toHaveURL(/\/dashboard$/);
});

test('administration_access_and_filters_do_not_create_unsaved_edits', async ({ page }) => {
  await boot(page);
  await page.getByRole('button', { name: 'Administration settings', exact: true }).click();
  await expect(page.getByRole('navigation', { name: 'Setup sections' })).toBeVisible();
  await page.getByRole('button', { name: 'Branches', exact: true }).click();
  let prompts = 0;
  page.on('dialog', async dialog => { prompts++; await dialog.dismiss(); });
  await page.getByRole('button', { name: 'Audit Logs', exact: true }).click();
  await page.getByPlaceholder('e.g. MasterDataType').fill('filtered');
  await page.getByRole('button', { name: 'Setup Studio', exact: true }).click();
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
  expect(prompts).toBe(0);
});

for (const action of ['override', 'rejection'] as const) test(`statutory_${action}_retains_edits_and_resets_after_save`, async ({ page }) => {
  await boot(page, { area: 'compliance', statutory: true });
  const rule = { id: 'rule-1', countryCode: 'SA', jurisdiction: 'SA', ruleKey: 'custom.allowance', ruleValue: '5', dataType: 'decimal', description: 'Fixture rule', effectiveFrom: '2020-01-01T00:00:00Z', effectiveTo: null, isTenantOverride: false };
  await page.route('**/api/country-packs/available', route => route.fulfill({ json: [{ countryCode: 'SA', nameEn: 'Saudi Arabia', nameAr: 'السعودية', jurisdictions: [{ code: 'SA', label: 'Saudi Arabia' }] }] }));
  await page.route('**/api/statutory-rules', route => route.fulfill({ json: [rule] }));
  await page.route('**/api/finance/rates/statutory/pending', route => route.fulfill({ json: [{ id: 'pending-1', kind: 'change', value: '7', effectiveFrom: '2030-01-01', effectiveTo: null, reason: 'Proposed change', reviewBy: null, requestedById: 'another-admin', requestedByName: 'Other admin', requestedAt: '2026-01-01T00:00:00Z', canApprove: true, companyId: null, companyName: null, ruleKey: 'custom.allowance', countryCode: 'SA', jurisdiction: 'SA', platformDefault: '5' }] }));
  const openRules = () => page.getByRole('listitem').filter({ has: page.getByRole('heading', { name: 'Statutory rules', exact: true }) }).getByRole('button', { name: 'Open settings', exact: true }).click();
  await openRules();
  await page.getByRole('textbox', { name: 'Search statutory rules' }).fill('custom');
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  await page.evaluate(() => history.back());
  await expect(page).not.toHaveURL(/setting=/);
  expect(prompts).toBe(0);
  await openRules();
  if (action === 'override') {
    await page.getByRole('button', { name: 'New override', exact: true }).click();
    await page.getByRole('dialog').getByRole('combobox', { name: 'Rule', exact: true }).selectOption('custom.allowance');
    await page.getByLabel('Override value', { exact: true }).fill('8');
    await page.getByLabel(/Approved reason and source/).fill('Preserve approved source');
  } else {
    await page.getByRole('button', { name: 'Reject', exact: true }).click();
    await page.getByLabel('Reason for rejecting (optional)', { exact: true }).fill('Preserve rejection reason');
  }
  const before = page.url();
  await page.evaluate(() => history.back());
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(before);
  await expect(page.getByRole('dialog').getByRole('textbox').last()).toHaveValue(action === 'override' ? 'Preserve approved source' : 'Preserve rejection reason');
  const endpoint = action === 'override' ? '**/api/statutory-rules' : '**/api/finance/rates/statutory/override/pending-1/reject';
  await page.route(endpoint, route => route.request().method() === 'GET' ? route.fallback() : route.fulfill({ status: 500, json: { message: 'Fixture write failed' } }));
  const save = page.getByRole('dialog').getByRole('button', { name: action === 'override' ? 'Send for approval' : 'Reject', exact: true });
  await save.click();
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Fixture write failed');
  await page.evaluate(() => history.back());
  await expect.poll(() => prompts).toBe(2);
  await expect(page).toHaveURL(before);
  await page.route(endpoint, route => route.request().method() === 'GET' ? route.fallback() : route.fulfill({ status: 204 }));
  await save.click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.evaluate(() => history.back());
  await expect(page).not.toHaveURL(/setting=/);
  expect(prompts).toBe(2);
});

test('smtp_failed_initial_load_and_save_keep_authored_values_guarded', async ({ page }) => {
  await boot(page);
  await page.route('**/api/admin/system-settings**', route => route.fulfill({ status: 500, json: { message: 'Fixture settings unavailable' } }));
  await page.getByRole('button', { name: 'Administration settings', exact: true }).click();
  await page.getByRole('button', { name: 'Email / SMTP', exact: true }).click();
  await page.getByPlaceholder('smtp.example.com', { exact: true }).fill('smtp.retained.example');
  const before = page.url();
  let prompts = 0;
  page.on('dialog', async dialog => { prompts++; await dialog.dismiss(); });
  await page.evaluate(() => history.back());
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(before);
  await expect(page.getByPlaceholder('smtp.example.com', { exact: true })).toHaveValue('smtp.retained.example');
  const failed = page.waitForResponse(response => response.request().method() !== 'GET' && response.url().includes('/api/admin/system-settings'));
  await page.getByRole('button', { name: 'Save Settings', exact: true }).click();
  await failed;
  await expect(page.getByRole('button', { name: 'Save Settings', exact: true })).toBeEnabled();
  await page.evaluate(() => history.back());
  await expect.poll(() => prompts).toBe(2);
  await expect(page).toHaveURL(before);
  await page.route('**/api/admin/system-settings**', route => route.fulfill({ json: route.request().method() === 'GET' ? [] : {} }));
  await page.getByRole('button', { name: 'Save Settings', exact: true }).click();
  await expect(page.getByText('Settings saved successfully.', { exact: true })).toBeVisible();
  await page.evaluate(() => history.back());
  await expect(page).not.toHaveURL(before);
  expect(prompts).toBe(2);
});

test('fallback_multiple_pre_setup_entries_restore_exact_url_and_history', async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(window, 'navigation', { value: undefined, configurable: true }));
  await boot(page, { beforeSetup: true, area: 'onboarding', setting: 'onboarding' });
  await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep multi-entry draft');
  const before = page.url();
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  await page.evaluate(() => history.go(-2));
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(before);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep multi-entry draft');
  expect(await page.evaluate(() => history.state.fixtureMarker)).toBe('preserved');
  page.off('dialog', decline);
  page.once('dialog', dialog => dialog.accept());
  await page.evaluate(() => history.go(-2));
  await expect(page).toHaveURL(/dashboard\?before=1$/);
  await page.evaluate(() => history.forward());
  await expect(page).toHaveURL(/dashboard\?before=2$/);
  await page.evaluate(() => history.forward());
  await expect(page).toHaveURL(before);
  await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Guard after remount');
  page.on('dialog', decline);
  await page.evaluate(() => history.go(-3));
  await expect.poll(() => prompts).toBe(2);
  await expect(page).toHaveURL(before);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Guard after remount');
});

test('legacy_draft_is_preserved_on_workspace_entry', async ({ page }) => {
  await boot(page, { area: 'onboarding' });
  await expect(page.locator('#setup-importOrganization')).toHaveCount(0);
  await expect(page.locator('.setup-studio')).toHaveCount(0);
  const draft = JSON.stringify({ version: 1, savedAt: '2026-10-14T00:00:00Z', step: 0, profile: { countryCode: 'SA', currencyCode: 'SAR', industry: 'Retained services', companySize: '51-200', legalEntityName: 'Workspace Fixture LLC', sections: { entity: true }, configuration: { attendanceMethods: ['WebCheckIn'], overtimeModes: ['PaidOvertime'], policySources: [] } } });
  const bundle = JSON.stringify({ tenantId: 'tenant', userId: 'reviewer', companyId: 'fixture-company', companyName: 'Workspace Fixture LLC', accountType: 'Group', sources: [{ documentId: 'doc', contentSha256: 'hash', name: 'Retained source' }], tasks: [{ id: 'task', taskTitle: 'Arrange access', taskDescription: '', category: 'Access', assignedToName: null, dueOffsetDays: null, isMandatory: null, condition: null, sourceQuote: 'Arrange access', sourceStart: 0, sourceLength: 14, documentId: 'doc', contentSha256: 'hash', sourceName: 'Retained source' }], edits: {}, saved: {} });
  await page.evaluate(({ draft, bundle }) => {
    localStorage.setItem('kynexone-setup-draft:v1:tenant:reviewer:fixture-company', draft);
    localStorage.setItem('kynexone-policy-form-draft:v1:tenant:reviewer:fixture-company', bundle);
  }, { draft, bundle });
  await page.reload();
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
  expect(await page.evaluate(() => localStorage.getItem('kynexone-setup-draft:v1:tenant:reviewer:fixture-company'))).toBe(draft);
  expect(await page.evaluate(() => localStorage.getItem('kynexone-policy-form-draft:v1:tenant:reviewer:fixture-company'))).toBe(bundle);
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Retained services');
  await page.getByRole('button', { name: 'Return to workspace', exact: true }).click();
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
  await expect(page).toHaveURL(/area=onboarding/);
  expect(await page.evaluate(() => localStorage.getItem('kynexone-policy-form-draft:v1:tenant:reviewer:fixture-company'))).toBe(bundle);
});

test('document_navigation_does_not_publish_and_retains_only_current_company_file', async ({ page }) => {
  const { writes } = await boot(page, { group: true, area: 'onboarding' });
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.getByRole('button', { name: 'Upload document', exact: true }).click();
  await page.locator('input[type=file]').setInputFiles({ name: 'unsent-policy.txt', mimeType: 'text/plain', buffer: Buffer.from('Arrange onboarding access.') });
  await page.getByRole('button', { name: 'Policy library / updates', exact: true }).click();
  await page.getByRole('button', { name: 'Setup Studio', exact: true }).click();
  expect(await page.locator('input[type=file]').evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('unsent-policy.txt');
  await page.getByRole('button', { name: 'Return to workspace', exact: true }).click();
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  expect(await page.locator('input[type=file]').evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('unsent-policy.txt');
  await page.getByRole('button', { name: 'Policy library / updates', exact: true }).click();
  await page.getByTestId('company-switcher').getByRole('button').click();
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await page.getByRole('button', { name: 'Setup Studio', exact: true }).click();
  await expect(page.getByLabel(/^Legal entity name/)).toHaveValue('Second Fixture LLC');
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.getByRole('button', { name: 'Upload document', exact: true }).click();
  expect(await page.locator('input[type=file]').evaluate(input => (input as HTMLInputElement).files?.length)).toBe(0);
  expect(writes).toEqual([]);
});

test('pending_file_departure_decline_retains_file_and_accept_leaves', async ({ page }) => {
  await boot(page, { area: 'onboarding' });
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.locator('input[type=file]').setInputFiles({ name: 'pending.txt', mimeType: 'text/plain', buffer: Buffer.from('Pending policy') });
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  await clickDashboard(page);
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(/mode=starter/);
  expect(await page.locator('input[type=file]').evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('pending.txt');
  page.off('dialog', decline);
  page.once('dialog', dialog => dialog.accept());
  await clickDashboard(page);
  await expect(page).toHaveURL(/\/dashboard$/);
});

test('history_company_change_confirms_pending_files_once', async ({ page }) => {
  await boot(page, { group: true });
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await page.getByRole('button', { name: 'Policy library / updates', exact: true }).click();
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await page.getByRole('button', { name: 'Setup Studio', exact: true }).click();
  await expect(page.getByLabel(/^Legal entity name/)).toHaveValue('Second Fixture LLC');
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.locator('input[type=file]').setInputFiles({ name: 'second.txt', mimeType: 'text/plain', buffer: Buffer.from('Second company policy') });
  let prompts = 0;
  page.on('dialog', async dialog => { prompts++; await dialog.accept(); });
  await page.evaluate(() => history.go(-2));
  await expect(page.getByLabel(/^Legal entity name/)).toHaveValue('Workspace Fixture LLC');
  expect(prompts).toBe(1);
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  expect(await page.locator('input[type=file]').evaluate(input => (input as HTMLInputElement).files?.length)).toBe(0);
});

test('starter_change_company_decline_keeps_url_and_file', async ({ page }) => {
  await boot(page, { group: true });
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.locator('input[type=file]').setInputFiles({ name: 'retained.txt', mimeType: 'text/plain', buffer: Buffer.from('Retained policy') });
  const original = page.url();
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  await page.getByRole('button', { name: 'Change company', exact: true }).click();
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(original);
  expect(await page.locator('input[type=file]').evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('retained.txt');
  page.off('dialog', decline);
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Change company', exact: true }).click();
  await expect(page).not.toHaveURL(/companyId=/);
  await expect(page).toHaveURL(/mode=starter/);
  await expect(page.locator('input[type=file]')).toHaveCount(0);
});

test('canonical_save_clears_dirty_navigation_state', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'branches' });
  await page.getByRole('button', { name: 'Add Branch', exact: true }).click();
  await page.getByPlaceholder('DXB-01', { exact: true }).fill('SAVED');
  await page.getByPlaceholder('Dubai Head Office', { exact: true }).fill('Saved branch');
  await page.getByRole('dialog').getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  let prompts = 0;
  page.on('dialog', async dialog => { prompts++; await dialog.dismiss(); });
  await selectArea(page, 'Payroll');
  await expect(page).toHaveURL(/area=payroll/);
  expect(prompts).toBe(0);
});

test('gl_authored_values_are_guarded_but_search_is_not', async ({ page }) => {
  await boot(page, { area: 'payroll', setting: 'glMapping', finance: true });
  await page.getByRole('searchbox', { name: 'Search accounts by code or name' }).fill('filter only');
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  await page.getByRole('button', { name: 'Policy library', exact: true }).click();
  await expect(page).toHaveURL(/tab=policyLibrary/);
  expect(prompts).toBe(0);
  await page.getByRole('button', { name: 'Setup Studio', exact: true }).click();
  await page.getByPlaceholder('5006', { exact: true }).fill('5010');
  await page.getByPlaceholder('Account name', { exact: true }).fill('Retain authored GL account');
  await page.getByRole('button', { name: 'Policy library', exact: true }).click();
  expect(prompts).toBe(1);
  await expect(page.getByPlaceholder('Account name', { exact: true })).toHaveValue('Retain authored GL account');
  page.off('dialog', decline);
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Policy library', exact: true }).click();
  await expect(page).toHaveURL(/tab=policyLibrary/);
  // GL save callbacks are intentionally not part of this bounded integration:
  // the conservative guard may also prompt after a successful GL save.
});

test('starter_return_stays_when_browser_draft_save_fails', async ({ page }) => {
  const { writes } = await boot(page, { area: 'leave' });
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await page.getByLabel(/^Industry/).fill('Retain on storage failure');
  await page.evaluate(() => {
    const original = Storage.prototype.setItem;
    Storage.prototype.setItem = function (key, value) {
      if (key.startsWith('kynexone-setup-draft:')) throw new DOMException('Full', 'QuotaExceededError');
      return original.call(this, key, value);
    };
  });
  await page.getByRole('button', { name: 'Return to workspace', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Retain on storage failure');
  await expect(page).toHaveURL(/mode=starter/);
  await expect(page.getByRole('status')).toContainText('could not save');
  expect(writes).toEqual([]);
});

test('all_areas_reachable_without_company_wizard', async ({ page }) => {
  const { writes } = await boot(page);
  await expect(page.getByRole('heading', { name: 'Start with your company', exact: true })).toHaveCount(0);
  for (const [id, label] of areas) {
    await selectArea(page, label);
    await expect(page.getByTestId('setup-area-content').getByRole('heading', { level: 2, name: label, exact: true })).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`area=${id}(?:&|$)`));
    expect(new URL(page.url()).searchParams.get('companyId')).toBe('fixture-company');
    expect(new URL(page.url()).searchParams.get('source')).toBe('retained');
  }
  expect(writes).toEqual([]);
});

test('url_back_restores_area', async ({ page }) => {
  await boot(page, { area: 'leave' });
  await selectArea(page, 'Payroll');
  await expect(page).toHaveURL(/area=payroll/);
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Payroll', exact: true })).toBeVisible();
  await page.goBack();
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Leave and absence', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Leave and absence', exact: true })).toBeVisible();
});

test('feature_unavailable_does_not_mount_editor', async ({ page }) => {
  const { writes, reads } = await boot(page, { disabled: true, area: 'learning' });
  const content = page.getByTestId('setup-area-content');
  await expect(content.getByText('Not enabled for this tenant', { exact: true })).toBeVisible();
  await expect(content.getByRole('link')).toHaveCount(0);
  expect(reads.filter(path => path.startsWith('/api/learning'))).toEqual([]);
  expect(writes).toEqual([]);
});

test('restricted_destination_explains_access_without_mounting_editor', async ({ page }) => {
  await boot(page, { restricted: true, area: 'learning' });
  await expect(page.getByTestId('setup-area-content').getByText('Your role does not have access', { exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-area-content').getByRole('link')).toHaveCount(0);
});

test('failed_availability_read_is_not_empty_configuration', async ({ page }) => {
  await boot(page, { flagError: true, area: 'organization' });
  await expect(page.getByTestId('setup-area-content').getByText('Availability could not be checked', { exact: true }).first()).toBeVisible();
  await expect(page.getByTestId('setup-area-content').getByRole('link')).toHaveCount(0);
});

test('overview_does_not_claim_configuration_complete', async ({ page }) => {
  const { writes } = await boot(page, { area: 'invalid-area' });
  const content = page.getByTestId('setup-area-content');
  await expect(content.getByRole('heading', { name: 'Workspace overview', exact: true })).toBeVisible();
  await expect(content.getByText('Configuration status is not assessed here. Open an area to review its saved settings.', { exact: true })).toBeVisible();
  await expect(content.getByText(/100%|configuration complete|12 of 12/i)).toHaveCount(0);
  await expect(page.getByText('Workspace Fixture LLC', { exact: true }).last()).toBeVisible();
  expect(writes).toEqual([]);
});

test('keyboard_and_mobile_area_navigation', async ({ page }, testInfo) => {
  await boot(page);
  const disclosure = page.getByRole('button', { name: /^Choose setup area/ });
  if (testInfo.project.name === 'mobile') {
    await disclosure.focus(); await page.keyboard.press('Enter');
    await expect(disclosure).toHaveAttribute('aria-expanded', 'true');
  }
  const target = page.getByRole('navigation', { name: 'Setup areas', exact: true }).getByRole('button', { name: 'Onboarding and confirmation', exact: true });
  await target.focus(); await page.keyboard.press('Enter');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Onboarding and confirmation', exact: true })).toBeVisible();
  if (testInfo.project.name === 'mobile') {
    await expect(disclosure).toHaveAttribute('aria-expanded', 'false');
    await expect(disclosure).toBeFocused();
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: `/tmp/setup-studio-${testInfo.project.name}.png`, fullPage: true });
});

test('arabic_rtl_keeps_named_areas_and_scope_readable', async ({ page }, testInfo) => {
  await boot(page, { arabic: true, area: 'onboarding' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { level: 2, name: 'تهيئة الموظفين والتثبيت', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-area-content').getByText('إعدادات على مستوى مساحة العمل', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: `/tmp/setup-studio-${testInfo.project.name}-ar.png`, fullPage: true });
});

test('group_company_switch_updates_scope_and_next_area_without_reset', async ({ page }) => {
  const { writes } = await boot(page, { group: true });
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await expect(page.getByTestId('setup-studio-workspace').getByText('Second Fixture LLC', { exact: true })).toBeVisible();
  await selectArea(page, 'Leave and absence');
  await expect(page).toHaveURL(/companyId=second-company/);
  await expect(page.getByTestId('company-switcher').getByRole('button')).toContainText('second-company');
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: 'Open workspace', exact: true })).toHaveAttribute('href', '/leave?companyId=second-company&setupArea=leave');
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: 'All companies', exact: true }).click();
  await expect(page.getByTestId('setup-studio-workspace').getByText('All authorized companies', { exact: true })).toBeVisible();
  await selectArea(page, 'Payroll');
  await expect(page).toHaveURL(/area=payroll/);
  expect(new URL(page.url()).searchParams.has('companyId')).toBe(false);
  expect(writes).toEqual([]);
});

test('history_back_to_absent_company_restores_all_companies_scope', async ({ page }) => {
  const { writes } = await boot(page, { group: true });
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: 'All companies', exact: true }).click();
  await expect(page).not.toHaveURL(/companyId=/);
  await expect(page.getByTestId('setup-studio-workspace').getByText('All authorized companies', { exact: true })).toBeVisible();
  await selectArea(page, 'Leave and absence');
  await expect(page).toHaveURL(/area=leave/);
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await expect(page).toHaveURL(/companyId=second-company/);
  await page.goBack();
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Workspace overview', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-studio-workspace').getByText('All authorized companies', { exact: true })).toBeVisible();
  await expect(page.getByTestId('company-switcher').getByRole('button')).toContainText('All companies');
  await expect(page).not.toHaveURL(/companyId=/);
  await selectArea(page, 'Leave and absence');
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: 'Open workspace', exact: true })).toHaveAttribute('href', '/leave?setupArea=leave');
  expect(writes).toEqual([]);
});
