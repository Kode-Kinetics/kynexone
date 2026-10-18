import { expect, test, type Page, type Route } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

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
async function boot(page: Page, options: { disabled?: boolean; restricted?: boolean; flagError?: boolean; area?: string; setting?: string; arabic?: boolean; group?: boolean; officer?: boolean; authDelay?: number; finance?: boolean; statutory?: boolean; beforeSetup?: boolean; readOnly?: boolean; intercept?: (route: Route) => Promise<boolean> } = {}) {
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
    if (await options.intercept?.(route)) return;
    const company = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
    const companies = options.group ? [company, { ...company, id: 'second-company', legalNameEn: 'Second Fixture LLC' }] : [company];
    if (path === '/api/auth/me') return reply({ id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: [options.readOnly ? 'Auditor' : options.officer ? 'HR Officer' : 'Admin'], permissions: options.readOnly ? ['organization.read'] : ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', ...(options.finance ? ['finance.gl.manage'] : []), ...(options.statutory ? ['payroll.rates.statutory_override'] : []), ...(options.restricted ? [] : ['learning.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'payroll.read', 'performance.read'])], accountType: 'Group', isGroupScope: true, companies: companies.map(item => ({ id: item.id, name: item.legalNameEn, code: item.id, countryCode: 'SAU', isActive: true })) });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/country-packs/available') return reply([]);
    if (path === '/api/reference/countries') return reply([{ code: 'SA', name: 'Saudi Arabia' }]);
    if (path === '/api/admin/system-settings') return reply([]);
    if (path === '/api/admin/master-data/types') return reply([]);
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
    if (path === '/api/leave/holidays/calendars' || path === '/api/leave/holidays/blackouts') return reply([]);
    if (path === '/api/leave/policies') return reply({ items: [{ id: 'existing-policy', name: 'Saved annual policy', leaveTypeId: 'annual-type', countryCode: 'KSA', annualEntitlementDays: 28, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full' }], total: 1 });
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  const setupUrl = `/setup?tab=aiSetup&path=configuration&companyId=fixture-company&source=retained${options.area ? `&area=${options.area}` : ''}${options.setting ? `&setting=${options.setting}` : ''}`;
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
  if (options.readOnly) await expect(page.getByRole('heading', { name: 'Companies', exact: true })).toBeVisible({ timeout: 30_000 });
  else await expect(page.getByTestId('setup-studio-workspace')).toBeVisible({ timeout: 30_000 });
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

// These fixtures intercept every API call. Saved/read-back assertions prove the
// canonical form contract; they are not server authorization or persistence proof.
async function maintenancePolicy(page: Page, options: { arabic?: boolean; failFirst?: boolean } = {}) {
  let saved = { id: 'existing-policy', name: 'Annual leave policy for regional operations and extended service teams', leaveTypeId: 'annual-type', countryCode: 'KSA', annualEntitlementDays: 28, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full' };
  let attempts = 0;
  await boot(page, { area: 'leave', setting: 'leavePolicies', arabic: options.arabic, intercept: async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/leave/types') { await route.fulfill({ json: [{ id: 'annual-type', code: 'ANNUAL', nameEn: 'Annual leave for regional operations and extended service teams', category: 'Annual', isPaid: true, isActive: true }] }); return true; }
    if (path === '/api/leave/policies') { await route.fulfill({ json: { items: [saved], total: 1 } }); return true; }
    if (path === '/api/leave/policies/existing-policy' && route.request().method() === 'PUT') {
      attempts++;
      if (options.failFirst && attempts === 1) await route.fulfill({ status: 503, json: { message: 'Temporary policy save failure. Retry your changes.' } });
      else { saved = { ...saved, ...route.request().postDataJSON() }; await route.fulfill({ json: saved }); }
      return true;
    }
    return false;
  } });
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  return page.getByRole('dialog', { name: 'Edit Leave Policy', exact: true });
}

test('tenant_admin_can_revisit_and_edit_saved_settings', async ({ page }) => {
  const dialog = await maintenancePolicy(page);
  await expect(dialog.getByLabel('Annual Days', { exact: true })).toHaveValue('28');
  await dialog.getByLabel('Annual Days', { exact: true }).fill('30');
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await page.reload();
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(dialog.getByLabel('Annual Days', { exact: true })).toHaveValue('30');
});

test('read_only_user_cannot_edit', async ({ page }) => {
  const { writes, reads } = await boot(page, { readOnly: true, area: 'leave', setting: 'leavePolicies' });
  await expect(page.getByTestId('setup-studio-workspace')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Add Company|New Policy|Edit/, exact: true })).toHaveCount(0);
  expect(reads.filter(path => path.startsWith('/api/leave/'))).toEqual([]);
  expect(writes).toEqual([]);
});

test('company_switch_does_not_reuse_other_company_draft', async ({ page }) => {
  await boot(page, { group: true });
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await page.getByLabel(/^Industry/).fill('Company A private draft');
  await page.getByRole('button', { name: 'Return to workspace', exact: true }).click();
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).not.toHaveValue('Company A private draft');
  await page.getByRole('button', { name: 'Return to workspace', exact: true }).click();
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Workspace Fixture LLC/ }).click();
  await page.getByRole('button', { name: 'Starter configuration draft', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Company A private draft');
});

test('forged_area_or_return_path_is_ignored', async ({ page }) => {
  const { writes } = await boot(page, { area: 'forged-area', setting: 'leavePolicies&returnTo=https%3A%2F%2Fexample.invalid%2Fcapture' });
  await expect(page.getByRole('heading', { name: 'Workspace overview', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'New Policy', exact: true })).toHaveCount(0);
  await selectArea(page, 'Leave and absence');
  await page.getByTestId('setup-area-content').getByRole('link', { name: 'Leave policies and holidays', exact: true }).click();
  await expect(page).toHaveURL(/127\.0\.0\.1:5197\/leave\?/);
  expect(writes).toEqual([]);
});

test('failed_save_retains_values', async ({ page }, info) => {
  const dialog = await maintenancePolicy(page, { failFirst: true });
  await dialog.getByLabel('Annual Days', { exact: true }).fill('31');
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).click();
  const alert = dialog.getByRole('alert');
  await expect(alert).toHaveText('Save failed.');
  await expect(alert).toBeFocused();
  await expect(dialog.getByLabel('Annual Days', { exact: true })).toHaveValue('31');
  await expect(alert).toBeInViewport();
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).scrollIntoViewIfNeeded();
  await expect(alert).toBeInViewport();
  await page.screenshot({ path: `/tmp/kynex-setup-final-evidence/error-${info.project.name}.png` });
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(dialog.getByLabel('Annual Days', { exact: true })).toHaveValue('31');
});

for (const arabic of [false, true]) {
  test(`workspace_axe_${arabic ? 'ar' : 'en'}`, async ({ page }, info) => {
    if (info.project.name === 'mobile') await page.setViewportSize({ width: 390, height: 844 });
    await boot(page, { arabic });
    expect((await new AxeBuilder({ page }).include('[data-testid="setup-studio-workspace"]').analyze()).violations.filter(v => v.impact === 'serious' || v.impact === 'critical')).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `/tmp/kynex-setup-final-evidence/workspace-${info.project.name}-${arabic ? 'ar' : 'en'}.png` });
  });
  test(`rtl_mobile_long_labels_remain_accessible_${arabic ? 'ar' : 'en'}`, async ({ page }, info) => {
    if (info.project.name === 'mobile') await page.setViewportSize({ width: 390, height: 844 });
    const dialog = await maintenancePolicy(page, { arabic });
    await expect(dialog).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('dir', arabic ? 'rtl' : 'ltr');
    await expect(dialog.getByLabel('Policy Name *', { exact: true })).toHaveValue(/regional operations/);
    expect((await new AxeBuilder({ page }).include('[role="dialog"]').analyze()).violations.filter(v => v.impact === 'serious' || v.impact === 'critical')).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    const close = dialog.getByRole('button', { name: 'Close', exact: true });
    const save = dialog.getByRole('button', { name: 'Save Changes', exact: true });
    await close.focus(); await page.keyboard.press('Shift+Tab'); await expect(save).toBeFocused();
    await page.keyboard.press('Tab'); await expect(close).toBeFocused();
    for (const control of [save, dialog.getByRole('button', { name: 'Cancel', exact: true })]) {
      await control.scrollIntoViewIfNeeded();
      expect(await control.evaluate(element => { const r = element.getBoundingClientRect(); return element.contains(document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2)); })).toBe(true);
    }
    await dialog.getByLabel('Annual Days', { exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: `/tmp/kynex-setup-final-evidence/leave-${info.project.name}-${arabic ? 'ar' : 'en'}.png` });
    await page.keyboard.press('Escape'); await expect(dialog).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeFocused();
  });
}

test('same_scope_company_refresh_keeps_newest_response', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'branches' });
  const held: Route[] = [];
  await page.route('**/api/companies?**', route => { held.push(route); });
  await page.evaluate(() => window.dispatchEvent(new Event('kynexone:companies-changed')));
  await expect.poll(() => held.length).toBe(1);
  await page.evaluate(() => window.dispatchEvent(new Event('kynexone:companies-changed')));
  await expect.poll(() => held.length).toBe(2);
  const company = (name: string) => ({ items: [{ id: 'fixture-company', legalNameEn: name, countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true }], total: 1 });
  await held[1].fulfill({ json: company('Newest company name') });
  await expect(page.getByTestId('setup-studio-workspace').getByText('Newest company name', { exact: true }).first()).toBeVisible();
  await held[0].fulfill({ json: company('Stale company name') });
  await page.getByRole('button', { name: 'Add Branch', exact: true }).click();
  await expect(page.getByRole('dialog').getByRole('option', { name: 'Newest company name', exact: true })).toBeAttached();
  await expect(page.getByRole('option', { name: 'Stale company name', exact: true })).toHaveCount(0);
});

for (const failCurrent of [false, true]) {
  test(`company_switch_discards_old_reference_responses_${failCurrent ? 'failure' : 'success'}`, async ({ page }) => {
    await boot(page, { group: true, area: 'organization', setting: 'branches' });
    const held: Route[] = [];
    await page.route(/\/api\/(companies|grades|organization\/cost-centers)\?/, route => { held.push(route); });
    const switchTo = async (name: RegExp) => {
      await page.getByTestId('company-switcher').getByRole('button').click();
      await page.getByRole('menuitem', { name }).click();
    };
    await switchTo(/Second Fixture LLC/);
    await expect(page).toHaveURL(/companyId=second-company/);
    await expect.poll(() => held.filter(route => route.request().headers()['x-company-id'] === 'second-company').length).toBeGreaterThanOrEqual(3);
    await switchTo(/Workspace Fixture LLC/);
    await expect(page).toHaveURL(/companyId=fixture-company/);
    await expect.poll(() => held.filter(route => route.request().headers()['x-company-id'] === 'fixture-company').length).toBeGreaterThanOrEqual(3);
    const oldScope = held.filter(route => route.request().headers()['x-company-id'] === 'second-company');
    const currentScope = held.filter(route => route.request().headers()['x-company-id'] === 'fixture-company');
    expect(new Set(oldScope.map(route => new URL(route.request().url()).pathname))).toEqual(new Set(['/api/companies', '/api/grades', '/api/organization/cost-centers']));
    expect(new Set(currentScope.map(route => new URL(route.request().url()).pathname))).toEqual(new Set(['/api/companies', '/api/grades', '/api/organization/cost-centers']));
    const fulfill = async (route: Route, old: boolean) => {
      const path = new URL(route.request().url()).pathname;
      const name = old ? 'STALE scope value' : 'Current scope value';
      const item = path.endsWith('companies') ? { id: 'fixture-company', legalNameEn: name, countryCode: 'SAU', isActive: true }
        : path.endsWith('grades') ? { id: 'grade', code: 'G1', name, level: 1, isActive: true }
        : { id: 'cost', code: 'CC1', name, isActive: true };
      await route.fulfill(failCurrent && !old ? { status: 503, json: { message: 'Current scope unavailable' } } : { json: { items: [item], total: 1 } });
    };
    for (const route of currentScope) await fulfill(route, false);
    await expect(page.getByTestId('setup-studio-workspace').getByText(failCurrent ? 'Workspace Fixture LLC' : 'Current scope value', { exact: true }).first()).toBeVisible();
    for (const route of oldScope) await fulfill(route, true);
    await page.getByRole('button', { name: 'Add Branch', exact: true }).click();
    await expect(page.getByRole('dialog').getByRole('option', { name: 'STALE scope value', exact: true })).toHaveCount(0);
    if (!failCurrent) await expect(page.getByRole('dialog').getByRole('option', { name: 'Current scope value', exact: true })).toBeAttached();
    await page.keyboard.press('Escape');
    // Select another existing form without reloading SetupPage or refetching the references.
    await page.getByRole('button', { name: 'Next: Departments', exact: true }).first().click();
    await page.getByRole('button', { name: 'Add Department', exact: true }).click();
    await expect(page.getByRole('dialog').getByRole('option', { name: 'STALE scope value', exact: true })).toHaveCount(0);
    if (!failCurrent) await expect(page.getByRole('dialog').getByRole('option', { name: 'Current scope value', exact: true })).toBeAttached();
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'Next: Designations', exact: true }).first().click();
    await page.getByRole('button', { name: 'Add Designation', exact: true }).click();
    await expect(page.getByRole('dialog').getByRole('option', { name: /STALE scope value/ })).toHaveCount(0);
    if (!failCurrent) await expect(page.getByRole('dialog').getByRole('option', { name: /Current scope value/ })).toBeAttached();
  });
}

test('shared_leave_modal_preserves_holiday_and_source_page_editors', async ({ page }) => {
  await boot(page, { area: 'leave', setting: 'holidayCalendars' });
  await page.getByText('Calendars', { exact: true }).locator('..').getByRole('button').click();
  const calendar = page.getByRole('dialog', { name: 'New Holiday Calendar', exact: true });
  await calendar.getByLabel('Calendar Name *', { exact: true }).fill('Local holiday fixture');
  await page.keyboard.press('Escape'); await expect(calendar).toHaveCount(0);
  await page.route('**/api/leave/reports/dashboard*', route => route.fulfill({ status: 503, json: { message: 'Fixture dashboard unavailable' } }));
  await page.goto('/leave');
  await page.getByRole('tab', { name: 'Policies', exact: true }).click();
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByRole('dialog').getByLabel('Annual Days', { exact: true })).toHaveValue('28');
  await page.keyboard.press('Escape');
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
  await expect(page.getByTestId('setup-focused-workspace').getByText('Applies to all companies', { exact: true })).toBeVisible();
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
  await page.getByTestId('setup-area-content').getByRole('link', { name: 'Leave policies and holidays', exact: true }).click();
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
  const browse = page.getByRole('button', { name: 'Browse all areas', exact: true });
  if (!await browse.isVisible()) {
    const back = page.getByRole('button', { name: /^Back to / }).first();
    if (await back.isVisible()) await back.click();
    await browse.waitFor({ state: 'visible', timeout: 2_000 }).catch(() => {});
  }
  if (!await browse.isVisible()) return;
  await browse.evaluate((button: HTMLButtonElement) => button.click());
  await page.getByRole('dialog', { name: 'Browse all setup areas', exact: true }).getByRole('button', { name: label, exact: true }).evaluate((button: HTMLButtonElement) => button.click());
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
  await page.getByTestId('setup-area-content').getByRole('button', { name: 'Onboarding checklists', exact: true }).click();
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
  await page.getByTestId('setup-area-content').getByRole('button', { name: 'Onboarding checklists', exact: true }).click();
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
  const openRules = () => page.getByTestId('setup-area-content').getByRole('button', { name: 'Statutory rules', exact: true }).click();
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
  await page.getByPlaceholder('Task title', { exact: true }).fill('Guard after remount');
  page.on('dialog', decline);
  await page.evaluate(() => history.go(-3));
  await expect.poll(() => prompts).toBe(2);
  await expect(page).toHaveURL(before);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Guard after remount');
});

// Owner-approved Guided Setup characterization. These tests intentionally use
// the current draft schema, existing exits, and synthetic APIs only.
const guidedPreview = {
  configurationVersion: 1,
  engine: 'Browser fixture',
  notes: [],
  draft: {
    branches: [{ code: 'HQ', nameEn: 'Head Office', city: 'Dammam', isHeadOffice: true }],
    departments: [], costCenters: [], designations: [], grades: [], gradePayComponents: [],
    leaveTypes: [], shifts: [], payComponents: [], statutoryRules: [], leavePolicies: [], benefitPlans: [],
    workingWeek: null, employeeIdRule: null, hrConfig: null, holidayCalendar: null,
    attendancePolicy: null, overtimePolicy: null, localization: null,
  },
};

async function openGuidedSetup(page: Page, options: Parameters<typeof boot>[1] = {}) {
  const activity = await boot(page, options);
  await page.getByRole('button', { name: 'Guided Setup', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Start with your company', exact: true })).toBeVisible();
  return activity;
}

async function seedCurrentGuidedDraft(page: Page, industry: string) {
  await page.evaluate(value => {
    localStorage.setItem('kynexone-setup-draft:v1:tenant:reviewer:fixture-company', JSON.stringify({
      version: 1,
      savedAt: '2026-10-18T00:00:00Z',
      step: 0,
      profile: {
        countryCode: 'SA', currencyCode: 'SAR', industry: value, companySize: '51-200',
        legalEntityName: 'Workspace Fixture LLC', sections: { entity: true },
        configuration: { attendanceMethods: ['WebCheckIn'], overtimeModes: ['PaidOvertime'], policySources: [] },
      },
    }));
  }, industry);
  await page.reload();
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
  await page.getByRole('button', { name: 'Guided Setup', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).toHaveValue(industry);
}

test('ai_setup_defaults_to_guided', async ({ page }) => {
  const { writes } = await boot(page);
  await page.goto('/setup?tab=aiSetup&companyId=fixture-company');
  await expect(page.getByRole('heading', { name: 'Start with your company', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Guided Setup', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('setup-studio-workspace')).toHaveCount(0);
  expect(writes).toEqual([]);
});

test.describe('path switch', () => {
  test('guided_path_switch_cancel_preserves_values_and_focus', async ({ page }) => {
    const { writes } = await openGuidedSetup(page);
    await page.getByLabel(/^Industry/).fill('Uncommitted guided services');
    const configuration = page.getByRole('button', { name: 'Configuration Center', exact: true });
    const before = page.url();
    await configuration.click();
    const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
    await expect(dialog).toContainText('Leave this form and discard unsaved changes?');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page).toHaveURL(before);
    await expect(page.getByLabel(/^Industry/)).toHaveValue('Uncommitted guided services');
    await expect(configuration).toBeFocused();
    expect(writes).toEqual([]);
  });

  test('guided_path_switch_confirm_discards_once', async ({ page }) => {
    const { writes } = await openGuidedSetup(page);
    await page.getByLabel(/^Industry/).fill('Persist before switching');
    const historyLength = await page.evaluate(() => history.length);
    await page.getByRole('button', { name: 'Configuration Center', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
    await expect(dialog).toHaveCount(1);
    await dialog.getByRole('button', { name: 'Discard and leave', exact: true }).click();
    await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
    await expect(page).toHaveURL(/path=configuration/);
    await expect(dialog).toHaveCount(0);
    expect(await page.evaluate(() => history.length)).toBe(historyLength + 1);
    expect(await page.evaluate(() => {
      const saved = JSON.parse(localStorage.getItem('kynexone-setup-draft:v1:tenant:reviewer:fixture-company') ?? 'null');
      return saved?.profile?.industry;
    })).toBe('Persist before switching');
    expect(writes).toEqual([]);
  });

  test('guided_unuploaded_file_blocks_configuration_switch', async ({ page }) => {
    const { writes } = await openGuidedSetup(page);
    await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
    await page.getByRole('button', { name: 'Upload document', exact: true }).click();
    const picker = page.locator('input[type=file]');
    await picker.setInputFiles({ name: 'pending-switch-policy.txt', mimeType: 'text/plain', buffer: Buffer.from('Do not lose this pending file.') });
    const configuration = page.getByRole('button', { name: 'Configuration Center', exact: true });
    const before = page.url();
    await configuration.click();
    const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
    await expect(dialog).toContainText('Selected files have not been uploaded');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page).toHaveURL(before);
    expect(await picker.evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('pending-switch-policy.txt');
    await expect(configuration).toBeFocused();
    expect(writes).toEqual([]);
  });

  test('guided_return_to_configuration_preserves_saved_draft', async ({ page }) => {
    const { writes } = await openGuidedSetup(page);
    await page.evaluate(() => localStorage.setItem(
      'kynexone-setup-draft:v1:tenant:reviewer:fixture-company',
      JSON.stringify({ profile: { industry: 'Saved return-path services' } }),
    ));
    await page.getByRole('button', { name: 'Return to workspace', exact: true }).click();
    await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
    await expect(page).toHaveURL(/path=configuration/);
    expect(await page.evaluate(() => {
      const saved = JSON.parse(localStorage.getItem('kynexone-setup-draft:v1:tenant:reviewer:fixture-company') ?? 'null');
      return saved?.profile?.industry;
    })).toBe('Saved return-path services');
    expect(writes).toEqual([]);
  });

  test('guided_pending_file_blocks_return_to_configuration', async ({ page }) => {
    const { writes } = await openGuidedSetup(page);
    await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
    await page.getByRole('button', { name: 'Upload document', exact: true }).click();
    const picker = page.locator('input[type=file]');
    await picker.setInputFiles({ name: 'pending-return-policy.txt', mimeType: 'text/plain', buffer: Buffer.from('Keep this return-path file.') });
    const exit = page.getByRole('button', { name: 'Return to workspace', exact: true });
    await exit.click();
    const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
    await expect(dialog).toContainText('Selected files have not been uploaded');
    await expect(page).toHaveURL(/path=guided/);
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    expect(await picker.evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('pending-return-policy.txt');
    await expect(exit).toBeFocused();
    expect(writes).toEqual([]);
  });
});

test('forged_company_configuration_is_fail_closed_until_explicit_recovery', async ({ page }) => {
  const { reads, writes } = await boot(page, { group: true });
  reads.length = 0;
  await page.goto('/setup?tab=aiSetup&path=configuration&companyId=forged-company&area=leave&setting=leavePolicies');
  await expect(page.getByRole('alert').getByRole('heading', { name: 'Company access required', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-studio-workspace')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'New Policy', exact: true })).toHaveCount(0);
  await page.waitForTimeout(250);
  expect(reads.filter(path => path === '/api/companies' || path.startsWith('/api/grades') || path.startsWith('/api/cost-centers') || path.startsWith('/api/leave/'))).toEqual([]);
  await expect(page).toHaveURL(/companyId=forged-company/);
  await page.getByRole('button', { name: 'Use Workspace Fixture LLC', exact: true }).click();
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
  await expect(page).toHaveURL(/companyId=fixture-company/);
  expect(writes).toEqual([]);
});

test('guided_setup_keeps_five_named_steps', async ({ page }) => {
  const { writes } = await openGuidedSetup(page);
  const steps = page.getByRole('navigation', { name: 'Company setup steps' }).getByRole('button');
  await expect(steps).toHaveCount(5);
  for (const [index, title] of ['Company details', 'Working week', 'People & pay', 'Grades & benefits', 'Review & create'].entries()) {
    await expect(steps.nth(index).getByText(title, { exact: true })).toHaveCount(1);
  }
  expect(writes).toEqual([]);
});

test('guided_setup_keeps_company_scope_and_change_company', async ({ page }) => {
  const { writes } = await openGuidedSetup(page, { group: true });
  await expect(page.getByText('Setting up Workspace Fixture LLC', { exact: true })).toBeVisible();
  await expect(page.getByText('SAU · SAR · Protected company scope', { exact: true })).toBeVisible();
  await expect(page.getByLabel(/^Legal entity name/)).toHaveValue('Workspace Fixture LLC');
  await expect(page.getByLabel(/^Legal entity name/)).toHaveAttribute('readonly', '');
  await page.getByRole('button', { name: 'Change company', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Choose a company to configure', exact: true })).toBeVisible();
  await page.getByRole('button', { name: /Second Fixture LLC/ }).click();
  await expect(page.getByText('Setting up Second Fixture LLC', { exact: true })).toBeVisible();
  await expect(page.getByLabel(/^Legal entity name/)).toHaveValue('Second Fixture LLC');
  expect(writes).toEqual([]);
});

test('guided_direct_entry_keeps_authorized_company_scope_and_recovers_draft', async ({ page }) => {
  const { writes } = await boot(page, { group: true });
  await page.addInitScript(() => localStorage.removeItem('kynexone-company:reviewer'));
  await page.evaluate(() => {
    localStorage.removeItem('kynexone-company:reviewer');
    localStorage.setItem('kynexone-setup-draft:v1:tenant:reviewer:fixture-company', JSON.stringify({
      version: 1,
      savedAt: '2026-10-18T00:00:00Z',
      step: 0,
      profile: {
        countryCode: 'SA', currencyCode: 'SAR', industry: 'Direct entry retained services', companySize: '51-200',
        legalEntityName: 'Workspace Fixture LLC', sections: { entity: true },
        configuration: { attendanceMethods: ['WebCheckIn'], overtimeModes: ['PaidOvertime'], policySources: [] },
      },
    }));
  });
  await page.goto('/setup?tab=aiSetup&mode=starter&companyId=fixture-company');
  await expect(page.getByRole('heading', { name: 'Start with your company', exact: true })).toBeVisible();
  await expect(page.getByText('Setting up Workspace Fixture LLC', { exact: true })).toBeVisible();
  await expect(page.getByText('SAU · SAR · Protected company scope', { exact: true })).toBeVisible();
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Direct entry retained services');
  await expect(page).toHaveURL(/mode=starter/);
  await expect(page).toHaveURL(/companyId=fixture-company/);
  await expect.poll(() => page.evaluate(() => localStorage.getItem('kynexone-company:reviewer'))).toBe('fixture-company');
  await expect(page.getByRole('heading', { name: 'Choose a company to configure', exact: true })).toHaveCount(0);
  expect(writes).toEqual([]);
});

test('guided_setup_keeps_save_policy_and_cancel_controls', async ({ page }) => {
  const { writes } = await openGuidedSetup(page);
  const save = page.getByRole('button', { name: 'Save for later', exact: true });
  const library = page.getByRole('button', { name: 'Policy library / updates', exact: true });
  const cancelSetup = page.getByRole('button', { name: 'Cancel setup', exact: true });
  await expect(save).toBeVisible();
  await expect(library).toBeVisible();
  await expect(cancelSetup).toBeVisible();
  await page.getByLabel(/^Industry/).fill('Guided regression services');
  await save.click();
  await expect(page.getByRole('status').filter({ hasText: 'Draft and preview edits saved' })).toBeVisible();
  await cancelSetup.click();
  const dialog = page.getByRole('dialog', { name: 'Confirm', exact: true });
  await expect(dialog).toContainText('Discard this setup draft');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Guided regression services');
  await expect(cancelSetup).toBeFocused();
  expect(writes).toEqual([]);
});

test('guided_setup_keeps_policy_upload_entry', async ({ page }) => {
  const { writes } = await openGuidedSetup(page);
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  const uploadEntry = page.getByRole('button', { name: 'Upload document', exact: true });
  await expect(uploadEntry).toBeVisible();
  await uploadEntry.click();
  await expect(page.locator('input[type=file]')).toBeAttached();
  await expect(page.getByRole('button', { name: 'Upload and review', exact: true })).toBeDisabled();
  expect(writes).toEqual([]);
});

test('guided_setup_continue_reaches_existing_review', async ({ page }) => {
  const { writes } = await openGuidedSetup(page);
  const controlFor = (label: string, selector: 'select' | 'input') =>
    page.locator('label').filter({ has: page.getByText(label, { exact: true }) }).locator(selector);
  await page.getByLabel(/^Industry/).fill('Guided regression services');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'How does your team work?', exact: true })).toBeVisible();
  await expect(controlFor('How do people work?', 'select')).toBeVisible();
  await expect(page.getByRole('group', { name: 'How is time recorded?', exact: true })).toBeVisible();
  await controlFor('How do people work?', 'select').selectOption('FieldRoster');
  await controlFor('Weekend (days off)', 'select').selectOption('Sat-Sun');

  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Set your people and pay preferences', exact: true })).toBeVisible();
  await expect(controlFor('Workforce', 'select')).toBeVisible();
  await expect(controlFor('Pay cycle', 'select')).toBeVisible();
  await controlFor('Workforce', 'select').selectOption('Mixed');
  await controlFor('Probation preference (months)', 'input').fill('6');
  await controlFor('Notice preference (days)', 'input').fill('45');

  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Configure your grades and benefits', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Salary grades', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Benefits', exact: true })).toBeVisible();
  await controlFor('How would you like to build your grades?', 'select').selectOption('manual');
  await page.getByRole('button', { name: 'Add salary grade', exact: true }).click();
  await page.getByLabel(/^Grade code/).fill('M2');

  await page.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(controlFor('Workforce', 'select')).toHaveValue('Mixed');
  await expect(controlFor('Probation preference (months)', 'input')).toHaveValue('6');
  await expect(controlFor('Notice preference (days)', 'input')).toHaveValue('45');
  await page.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(controlFor('How do people work?', 'select')).toHaveValue('FieldRoster');
  await expect(controlFor('Weekend (days off)', 'select')).toHaveValue('Sat-Sun');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.getByLabel(/^Grade code/)).toHaveValue('M2');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.getByRole('navigation', { name: 'Company setup steps' }).getByRole('button').nth(4)).toHaveAttribute('aria-current', 'step');
  await expect(page.getByRole('heading', { name: 'Choose what to include', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Generate draft', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Apply .*item/ })).toHaveCount(0);
  expect(writes).toEqual([]);
});

test('guided_setup_apply_remains_explicit', async ({ page }) => {
  const { writes } = await openGuidedSetup(page, { intercept: async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/setup-assistant/preview' && route.request().method() === 'POST') {
      await route.fulfill({ json: guidedPreview });
      return true;
    }
    if (path === '/api/setup-assistant/apply' && route.request().method() === 'POST') {
      await route.fulfill({ json: { applied: { branches: 1 }, total: 1 } });
      return true;
    }
    return false;
  } });
  await page.getByLabel(/^Industry/).fill('Guided regression services');
  for (let index = 0; index < 4; index++) await page.getByRole('button', { name: 'Continue', exact: true }).click();
  expect(writes).toEqual([]);
  await page.getByRole('button', { name: 'Generate draft', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply 1 item(s) to Workspace Fixture LLC', exact: true })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Your workspace changes only when you choose Apply.' })).toBeVisible();
  expect(writes).toEqual(['/api/setup-assistant/preview']);
  await page.getByRole('button', { name: 'Apply 1 item(s) to Workspace Fixture LLC', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Setup Applied', exact: true })).toBeVisible();
  expect(writes).toEqual(['/api/setup-assistant/preview', '/api/setup-assistant/apply']);
});

test('guided_policy_library_cancel_preserves_values_and_focus', async ({ page }) => {
  const { writes } = await boot(page);
  await seedCurrentGuidedDraft(page, 'Retained guided services');
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.getByRole('button', { name: 'Upload document', exact: true }).click();
  const picker = page.locator('input[type=file]');
  await picker.setInputFiles({ name: 'retained-policy.txt', mimeType: 'text/plain', buffer: Buffer.from('Retain this pending policy.') });
  await expect(page.getByRole('button', { name: 'Upload and review', exact: true })).toBeEnabled();
  const library = page.getByRole('button', { name: 'Policy library / updates', exact: true });
  await library.click();
  const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
  await expect(dialog).toContainText('Selected files have not been uploaded');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page).toHaveURL(/path=guided/);
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Retained guided services');
  expect(await picker.evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('retained-policy.txt');
  await expect(library).toBeFocused();
  expect(writes).toEqual([]);
});

test('guided_policy_library_confirm_discards_once', async ({ page }) => {
  const { writes } = await boot(page);
  await seedCurrentGuidedDraft(page, 'Confirmed guided services');
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.getByRole('button', { name: 'Upload document', exact: true }).click();
  await page.locator('input[type=file]').setInputFiles({ name: 'departing-policy.txt', mimeType: 'text/plain', buffer: Buffer.from('Confirm one guarded departure.') });
  await expect(page.getByRole('button', { name: 'Upload and review', exact: true })).toBeEnabled();
  const historyLength = await page.evaluate(() => history.length);
  await page.getByRole('button', { name: 'Policy library / updates', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
  await expect(dialog).toHaveCount(1);
  expect(await page.evaluate(() => history.length)).toBe(historyLength);
  await dialog.getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Policy library', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/tab=policyLibrary/);
  await expect(dialog).toHaveCount(0);
  expect(await page.evaluate(() => history.length)).toBe(historyLength + 1);
  expect(await page.evaluate(() => {
    const saved = JSON.parse(localStorage.getItem('kynexone-setup-draft:v1:tenant:reviewer:fixture-company') ?? 'null');
    return saved?.profile?.industry;
  })).toBe('Confirmed guided services');
  expect(writes).toEqual([]);
});

test('guided_unuploaded_file_blocks_existing_exit', async ({ page }) => {
  const { writes } = await boot(page);
  await seedCurrentGuidedDraft(page, 'Blocked exit services');
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.getByRole('button', { name: 'Upload document', exact: true }).click();
  const picker = page.locator('input[type=file]');
  await picker.setInputFiles({ name: 'blocked-policy.txt', mimeType: 'text/plain', buffer: Buffer.from('Block this existing exit.') });
  await expect(page.getByRole('button', { name: 'Upload and review', exact: true })).toBeEnabled();
  const exit = page.getByRole('button', { name: 'Return to workspace', exact: true });
  await exit.click();
  const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
  await expect(dialog).toContainText('Selected files have not been uploaded');
  await expect(page).toHaveURL(/path=guided/);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Blocked exit services');
  expect(await picker.evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('blocked-policy.txt');
  await expect(exit).toBeFocused();
  expect(writes).toEqual([]);
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
  await page.getByLabel(/^Industry/).fill('Retained company draft');
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.locator('input[type=file]').setInputFiles({ name: 'retained.txt', mimeType: 'text/plain', buffer: Buffer.from('Retained policy') });
  const original = page.url();
  const originalHistory = await page.evaluate(() => history.state);
  let prompts = 0;
  const decline = async (dialog: import('@playwright/test').Dialog) => { prompts++; await dialog.dismiss(); };
  page.on('dialog', decline);
  await page.getByRole('button', { name: 'Change company', exact: true }).click();
  await expect.poll(() => prompts).toBe(1);
  await expect(page).toHaveURL(original);
  expect(await page.evaluate(() => history.state)).toEqual(originalHistory);
  expect(await page.locator('input[type=file]').evaluate(input => (input as HTMLInputElement).files?.[0]?.name)).toBe('retained.txt');
  page.off('dialog', decline);
  page.once('dialog', async dialog => { prompts++; await dialog.accept(); });
  await page.getByRole('button', { name: 'Change company', exact: true }).click();
  await expect(page).not.toHaveURL(/companyId=/);
  await expect(page).toHaveURL(/mode=starter/);
  await expect(page.locator('input[type=file]')).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Choose a company to configure', exact: true })).toBeVisible();
  expect(prompts).toBe(2);
  await page.getByRole('button', { name: /Workspace Fixture LLC/ }).click();
  await expect(page).toHaveURL(/companyId=fixture-company/);
  await expect(page).toHaveURL(/mode=starter/);
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Retained company draft');
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  expect(await page.locator('input[type=file]').evaluate(input => (input as HTMLInputElement).files?.length)).toBe(0);
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

for (const destination of ['palette', 'profile', 'signout', 'sidebar_signout']) test(`shell_${destination}_protects_authored_values`, async ({ page }) => {
  // TopBar's user menu is intentionally desktop-only.
  if (destination !== 'palette') await page.setViewportSize({ width: 1440, height: 1000 });
  await boot(page, { area: 'onboarding', setting: 'onboarding' });
  await page.getByPlaceholder('Task title', { exact: true }).fill('Retain shell draft');
  const originalUrl = page.url();
  let prompts = 0; let accept = false;
  page.on('dialog', async dialog => { prompts++; if (accept) await dialog.accept(); else await dialog.dismiss(); });
  if (destination === 'palette') {
    await page.keyboard.press('Control+k');
    await page.getByPlaceholder('Search modules, employees, reports…').fill('Dashboard');
  } else if (destination !== 'sidebar_signout') await page.getByRole('button', { name: 'Open user menu' }).click();
  const target = destination === 'palette'
    ? page.getByRole('button', { name: /Dashboard.*\/dashboard/ })
    : destination === 'sidebar_signout' ? page.getByRole('button', { name: 'Sign out', exact: true })
    : page.locator('header').getByRole('button', { name: destination === 'profile' ? 'My Profile' : 'Sign out', exact: true });
  await target.click();
  expect(prompts).toBe(1);
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Retain shell draft');
  accept = true;
  await target.click();
  // This fixture has no ess.read: accepted profile navigation must preserve
  // the incumbent access-denied redirect, not briefly assert the pending URL.
  await expect(page).toHaveURL(destination === 'palette' || destination === 'profile' ? /\/dashboard$/ : /\/login$/);
  if (destination === 'profile') await expect(page.getByText('Access Denied', { exact: true })).toBeVisible();
  expect(prompts).toBe(2);
  if (destination === 'palette') {
    // The same seam is inert after Setup unmounts.
    await page.getByRole('button', { name: 'Open command search' }).click();
    await page.getByPlaceholder('Search modules, employees, reports…').fill('Dashboard');
    await page.getByRole('button', { name: /Dashboard.*\/dashboard/ }).click();
    await expect(page).toHaveURL(/\/dashboard$/);
    expect(prompts).toBe(2);
  }
});

for (const direct of [true] as const) test(`${direct ? 'direct' : 'embedded'}_same_destination_keeps_dirty_state`, async ({ page }) => {
  await boot(page, { area: direct ? 'payroll' : 'onboarding', setting: direct ? 'glMapping' : 'onboarding', finance: true });
  if (direct) await page.goto('/setup?tab=glMapping&companyId=fixture-company');
  else await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  const field = page.getByPlaceholder(direct ? 'Account name' : 'Task title', { exact: true });
  await field.fill('Retain same destination draft');
  const originalUrl = page.url();
  let prompts = 0; let accept = true;
  page.on('dialog', async dialog => { prompts++; if (accept) await dialog.accept(); else await dialog.dismiss(); });
  const same = page.getByRole('navigation', { name: 'Setup sections' }).getByRole('button', { name: 'GL & rates', exact: true });
  await same.click();
  expect(prompts).toBe(0);
  await expect(page).toHaveURL(originalUrl);
  await expect(field).toHaveValue('Retain same destination draft');
  accept = false;
  const depart = () => direct ? page.getByRole('button', { name: 'Policy library', exact: true }).click() : selectArea(page, 'Payroll');
  await depart();
  expect(prompts).toBe(1);
  await expect(field).toHaveValue('Retain same destination draft');
  accept = true;
  await depart();
  await expect(page).toHaveURL(direct ? /tab=policyLibrary/ : /area=payroll/);
  expect(prompts).toBe(2);
});

test('gl_internal_scope_and_subtabs_protect_authored_values', async ({ page }) => {
  await boot(page, { area: 'payroll', setting: 'glMapping', finance: true });
  const scope = page.getByLabel('Configuration scope');
  await expect(scope).toHaveValue('');
  const name = page.getByPlaceholder('Account name', { exact: true });
  await page.getByPlaceholder('5006', { exact: true }).fill('5010');
  await name.fill('Group authored account');
  let prompts = 0; let accept = false;
  page.on('dialog', async dialog => { prompts++; if (accept) await dialog.accept(); else await dialog.dismiss(); });
  await scope.selectOption('');
  await page.getByRole('tab', { name: 'Accounts & Mapping', exact: true }).click();
  expect(prompts).toBe(0);
  await scope.selectOption('fixture-company');
  expect(prompts).toBe(1);
  await expect(scope).toHaveValue('');
  await expect(name).toHaveValue('Group authored account');
  await page.getByRole('tab', { name: 'ERP API access', exact: true }).click();
  expect(prompts).toBe(2);
  await expect(name).toHaveValue('Group authored account');
  await page.getByRole('tab', { name: 'Accounts & Mapping', exact: true }).press('End');
  expect(prompts).toBe(3);
  await expect(name).toHaveValue('Group authored account');
  accept = true;
  await scope.selectOption('fixture-company');
  expect(prompts).toBe(4);
  await expect(scope).toHaveValue('fixture-company');
  await expect(name).toHaveValue('');
  await page.getByPlaceholder('5006', { exact: true }).fill('5020');
  await name.fill('Company authored account');
  const save = page.waitForRequest(request => request.method() === 'POST' && new URL(request.url()).pathname === '/api/finance/gl/accounts');
  await page.getByRole('button', { name: 'Add account', exact: true }).click();
  const request = await save;
  expect(new URL(request.url()).searchParams.get('companyId')).toBe('fixture-company');
  expect(request.postDataJSON()).toMatchObject({ code: '5020', name: 'Company authored account' });
  await expect(name).toHaveValue('');
  await name.fill('Discard on tab change');
  await page.getByRole('tab', { name: 'Accounts & Mapping', exact: true }).press('End');
  expect(prompts).toBe(5);
  await expect(page.getByRole('tab', { name: 'ERP API access', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Accounts & Mapping', exact: true }).click();
  await expect(name).toHaveValue('');
  expect(prompts).toBe(5);
});

test('gl_banner_navigation_guards_period_draft', async ({ page }) => {
  await boot(page, { area: 'payroll', setting: 'glMapping', finance: true, intercept: async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/finance/gl/control-accounts/health') {
      await route.fulfill({ json: { generatedAtUtc: '2026-10-14T00:00:00Z', companyId: null, healthy: false, violations: ['Review balance'], accounts: [{ driver: 'salary', account: '5000', nature: 'Debit', scopedBalance: -1, tenantBalance: -1, isHealthy: false, violation: 'Review balance' }] } }); return true;
    }
    if (path.endsWith('/periods')) { await route.fulfill({ json: [] }); return true; }
    return false;
  } });
  await page.getByRole('tab', { name: 'Accounting periods', exact: true }).click();
  await page.getByLabel('Note (optional)', { exact: true }).fill('Keep period explanation');
  let prompts = 0; let accept = false;
  page.on('dialog', async dialog => { prompts++; if (accept) await dialog.accept(); else await dialog.dismiss(); });
  const banner = page.getByRole('button', { name: 'Control account 5000: Review balance', exact: true });
  await banner.click();
  expect(prompts).toBe(1);
  await expect(page.getByLabel('Note (optional)', { exact: true })).toHaveValue('Keep period explanation');
  accept = true;
  await banner.click();
  expect(prompts).toBe(2);
  await expect(page.getByRole('tab', { name: 'Accounts & Mapping', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Accounting periods', exact: true }).click();
  await expect(page.getByLabel('Note (optional)', { exact: true })).toHaveValue('');
  expect(prompts).toBe(2);
});

test('gl_authored_values_are_guarded_but_search_is_not', async ({ page }) => {
  await boot(page, { area: 'payroll', setting: 'glMapping', finance: true });
  await page.getByRole('searchbox', { name: 'Search accounts by code or name' }).fill('filter only');
  await page.getByRole('combobox', { name: 'Filter by account type' }).selectOption('Expense');
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

test('gl_group_statutory_country_browsing_is_not_authored_work', async ({ page }) => {
  await boot(page, { area: 'payroll', setting: 'glMapping', statutory: true, group: true, intercept: async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/companies') {
      await route.fulfill({ json: { items: [
        { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' },
        { id: 'second-company', legalNameEn: 'Second Fixture LLC', countryCode: 'ARE', defaultCurrency: 'AED', isActive: true, approvalStatus: 'Active' },
      ], total: 2, page: 1, pageSize: 100 } }); return true;
    }
    if (path.startsWith('/api/finance/rates/')) { await route.fulfill({ json: [] }); return true; }
    return false;
  } });
  await page.getByRole('tab', { name: 'Statutory Rates', exact: true }).click();
  await page.getByLabel('Country', { exact: true }).selectOption('ARE');
  let prompts = 0;
  page.on('dialog', async dialog => { prompts++; await dialog.dismiss(); });
  await page.getByRole('button', { name: 'Policy library', exact: true }).click();
  await expect(page).toHaveURL(/tab=policyLibrary/);
  expect(prompts).toBe(0);
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
  const disclosure = page.getByRole('button', { name: 'Browse all areas', exact: true });
  await disclosure.focus(); await page.keyboard.press('Enter');
  await expect(disclosure).toHaveAttribute('aria-expanded', 'true');
  const target = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true }).getByRole('button', { name: 'Onboarding and confirmation', exact: true });
  await target.focus(); await page.keyboard.press('Enter');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Onboarding and confirmation', exact: true })).toBeVisible();
  await expect(disclosure).toHaveAttribute('aria-expanded', 'false');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: `/tmp/setup-studio-${testInfo.project.name}.png`, fullPage: true });
});

test('arabic_rtl_keeps_named_areas_and_scope_readable', async ({ page }, testInfo) => {
  await boot(page, { arabic: true, area: 'onboarding' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { level: 2, name: 'تهيئة الموظفين والتثبيت', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-area-content').getByText('ينطبق على جميع الشركات', { exact: true })).toBeVisible();
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
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: 'Leave policies and holidays', exact: true })).toHaveAttribute('href', '/leave?companyId=second-company&setupArea=leave');
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
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: 'Leave policies and holidays', exact: true })).toHaveAttribute('href', '/leave?setupArea=leave');
  expect(writes).toEqual([]);
});

test('whole_destination_row_is_action', async ({ page }) => {
  const { writes } = await boot(page, { area: 'organization' });
  const destination = page.getByRole('button', { name: 'Companies', exact: true });

  await expect(destination).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open settings', exact: true })).toHaveCount(0);
  await destination.click();

  await expect(page).toHaveURL(/area=organization&setting=companies/);
  await expect(page.getByRole('heading', { level: 2, name: 'Companies', exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});

test('focused_form_replaces_area_canvas', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'companies' });

  await expect(page.getByTestId('setup-focused-workspace')).toBeVisible();
  await expect(page.getByTestId('setup-area-blueprint')).toHaveCount(0);
  await expect(page.getByRole('heading', { level: 2, name: 'Companies', exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Add Company', exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Apply', exact: true })).toHaveCount(0);
});

test('focused_form_opens_at_heading_without_manual_scroll', async ({ page }) => {
  await boot(page, { area: 'organization' });
  const destination = page.getByRole('button', { name: 'Employee Reference Lists', exact: true });
  await destination.scrollIntoViewIfNeeded();
  await destination.click();

  const heading = page.getByRole('heading', { level: 2, name: 'Employee Reference Lists', exact: true });
  await expect(heading).toBeFocused();
  const box = await heading.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.y).toBeGreaterThanOrEqual(0);
  expect(box!.y + box!.height).toBeLessThan(page.viewportSize()!.height);
});

test('back_restores_origin_focus_and_scroll', async ({ page }) => {
  await boot(page, { area: 'organization' });
  const destination = page.getByRole('button', { name: 'Employee Reference Lists', exact: true });
  await destination.evaluate((element: HTMLButtonElement) => {
    element.focus();
    window.scrollTo({ top: 480 });
    element.click();
  });

  await expect(page.getByRole('heading', { level: 2, name: 'Employee Reference Lists', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Back to Organization and core HR', exact: true }).first().click();
  await expect(page.getByRole('heading', { level: 2, name: 'Organization and core HR', exact: true })).toBeVisible();
  await expect(destination).toBeFocused();
  await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBeGreaterThan(300);
});

test('back_after_previous_or_next_restores_current_row_focus_and_recorded_scroll', async ({ page }) => {
  await boot(page, { area: 'organization' });
  for (const adjacent of [
    { control: 'Previous: Branches', destination: 'Branches' },
    { control: 'Next: Designations', destination: 'Designations' },
  ]) {
    const departments = page.getByRole('button', { name: 'Departments', exact: true });
    const recordedScroll = await departments.evaluate((element: HTMLButtonElement) => {
      window.scrollTo({ top: 420, behavior: 'auto' });
      const scrollY = window.scrollY;
      element.click();
      return scrollY;
    });

    await page.getByRole('button', { name: adjacent.control, exact: true }).first().click();
    await expect(page.getByRole('heading', { level: 2, name: adjacent.destination, exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Back to Organization and core HR', exact: true }).first().click();

    await expect(page.getByRole('button', { name: adjacent.destination, exact: true })).toBeFocused();
    await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBe(recordedScroll);
  }
});

test('browser_back_forward_restores_matching_row_focus_and_scroll', async ({ page }) => {
  await boot(page, { area: 'organization' });
  const departments = page.getByRole('button', { name: 'Departments', exact: true });
  const recordedScroll = await departments.evaluate((element: HTMLButtonElement) => {
    window.scrollTo({ top: 420, behavior: 'auto' });
    const scrollY = window.scrollY;
    element.click();
    return scrollY;
  });
  await page.getByRole('button', { name: 'Next: Designations', exact: true }).first().click();
  await expect(page.getByRole('heading', { level: 2, name: 'Designations', exact: true })).toBeFocused();

  await page.goBack();
  await expect(page.getByRole('heading', { level: 2, name: 'Departments', exact: true })).toBeFocused();
  await page.goBack();
  await expect(page.getByRole('button', { name: 'Departments', exact: true })).toBeFocused();
  await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBe(recordedScroll);

  await page.goForward();
  await expect(page.getByRole('heading', { level: 2, name: 'Departments', exact: true })).toBeFocused();
  await page.goForward();
  await expect(page.getByRole('heading', { level: 2, name: 'Designations', exact: true })).toBeFocused();
});

test('previous_next_are_available_above_and_below_long_form', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'departments' });

  for (const position of ['top', 'bottom']) {
    const navigation = page.getByTestId(`focused-navigation-${position}`);
    await expect(navigation.getByRole('button', { name: 'Previous: Branches', exact: true })).toBeVisible();
    await expect(navigation.getByRole('button', { name: 'Next: Designations', exact: true })).toBeVisible();
  }
});

test('restricted_unknown_or_disabled_destination_never_mounts_editor', async ({ browser }) => {
  for (const mode of ['restricted', 'unknown', 'disabled'] as const) {
    const context = await browser.newContext();
    const page = await context.newPage();
    const options = mode === 'restricted'
      ? { restricted: true, area: 'onboarding', setting: 'onboarding' }
      : mode === 'unknown'
        ? { flagError: true, area: 'onboarding', setting: 'onboarding' }
        : { disabled: true, area: 'learning', setting: 'learning' };
    const { reads } = await boot(page, options);

    await expect(page.getByTestId('setup-focused-workspace')).toHaveCount(0);
    await expect(page.getByTestId('setup-area-blueprint')).toBeVisible();
    if (mode !== 'disabled') {
      expect(reads.filter(path => path.startsWith('/api/recruitment/onboarding'))).toEqual([]);
    }
    await context.close();
  }
});

test('permission_loss_in_another_session_revalidates_on_focus_and_stops_reads', async ({ page }) => {
  let allowed = true;
  const { reads } = await boot(page, { area: 'leave', setting: 'leavePolicies', intercept: async route => {
    if (new URL(route.request().url()).pathname !== '/api/auth/me') return false;
    await route.fulfill({ json: {
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: allowed ? ['Admin'] : [],
      permissions: ['organization.read', 'organization.write', ...(allowed ? ['leave.read'] : [])],
      accountType: 'Group', isGroupScope: true,
      companies: [{ id: 'fixture-company', name: 'Workspace Fixture LLC', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    } });
    return true;
  } });
  await expect(page.getByTestId('setup-focused-workspace')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeVisible();

  const adminPage = await page.context().newPage();
  await adminPage.exposeFunction('revokeFixtureAccess', () => { allowed = false; });
  await adminPage.setContent('<button type="button" onclick="window.revokeFixtureAccess()">Revoke setup access</button>');
  await adminPage.getByRole('button', { name: 'Revoke setup access', exact: true }).click();
  reads.length = 0;
  await page.bringToFront();
  // Headless Chromium does not emit a native focus event for bringToFront; deliver the same
  // browser lifecycle signal after the independent admin page changes the server fixture.
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));

  await expect(page.getByTestId('setup-focused-workspace')).toHaveCount(0);
  await expect(page.getByRole('status').getByText('Your role does not have access', { exact: true })).toBeVisible();
  await expect.poll(() => reads.filter(path => path === '/api/auth/me').length).toBe(1);
  expect(reads.filter(path => path.startsWith('/api/leave/'))).toEqual([]);
  await adminPage.close();
});

test('protected_403_revalidates_authoritative_permissions_and_unmounts_editor', async ({ page }) => {
  let allowed = true;
  const { reads } = await boot(page, { area: 'leave', setting: 'leavePolicies', intercept: async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/me') {
      await route.fulfill({ json: {
        id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: allowed ? ['Admin'] : [],
        permissions: ['organization.read', 'organization.write', ...(allowed ? ['leave.read'] : [])],
        accountType: 'Group', isGroupScope: true,
        companies: [{ id: 'fixture-company', name: 'Workspace Fixture LLC', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
      } });
      return true;
    }
    if (path === '/api/leave/policies/existing-policy' && route.request().method() === 'PUT' && !allowed) {
      await route.fulfill({ status: 403, json: { message: 'Setup access was revoked.' } });
      return true;
    }
    return false;
  } });
  await expect(page.getByTestId('setup-focused-workspace')).toBeVisible();
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit Leave Policy', exact: true });

  allowed = false;
  reads.length = 0;
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).click();

  await expect(page.getByTestId('setup-focused-workspace')).toHaveCount(0);
  await expect(page.getByRole('status').getByText('Your role does not have access', { exact: true })).toBeVisible();
  await expect.poll(() => reads.filter(path => path === '/api/auth/me').length).toBe(1);
  expect(reads.filter(path => path.startsWith('/api/leave/'))).toEqual([]);
});

test('stale_authorization_refresh_cannot_restore_revoked_permissions', async ({ page }) => {
  let authCall = 0;
  let releaseStale!: () => void;
  const staleResponse = new Promise<void>(resolve => { releaseStale = resolve; });
  await boot(page, { area: 'leave', setting: 'leavePolicies', intercept: async route => {
    if (new URL(route.request().url()).pathname !== '/api/auth/me') return false;
    authCall += 1;
    const allowed = authCall < 3;
    if (authCall === 2) await staleResponse;
    await route.fulfill({ json: {
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: allowed ? ['Admin'] : [],
      permissions: ['organization.read', 'organization.write', ...(allowed ? ['leave.read'] : [])],
      accountType: 'Group', isGroupScope: true,
      companies: [{ id: 'fixture-company', name: 'Workspace Fixture LLC', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    } });
    return true;
  } });
  await expect(page.getByTestId('setup-focused-workspace')).toBeVisible();

  await page.evaluate(() => window.dispatchEvent(new Event('kynexone:auth-changed')));
  await expect.poll(() => authCall).toBe(2);
  await page.evaluate(() => window.dispatchEvent(new Event('kynexone:auth-changed')));
  await expect(page.getByTestId('setup-focused-workspace')).toHaveCount(0);
  releaseStale();

  await page.waitForTimeout(300);
  await expect(page.getByTestId('setup-focused-workspace')).toHaveCount(0);
  await expect(page.getByRole('status').getByText('Your role does not have access', { exact: true })).toBeVisible();
});

test('tenant_setting_says_applies_to_all_companies', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'departments' });

  await expect(page.getByTestId('setup-focused-workspace').getByText('Applies to all companies', { exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-focused-workspace').getByText('Company scope', { exact: true })).toHaveCount(0);
  await expect(page.getByTestId('setup-focused-workspace').getByText('Tenant-wide controls', { exact: true })).toHaveCount(0);
});

test('focused_workspace_keeps_all_area_navigator_without_extra_editor', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'companies' });

  await expect(page.getByRole('button', { name: 'Browse all areas', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Browse all areas', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Browse all setup areas', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add Company', exact: true })).toHaveCount(1);
  await expect(page.getByTestId('setup-area-blueprint')).toHaveCount(0);
});

test('browse_all_areas_is_large_desktop_dialog', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop');
  await boot(page, { area: 'organization' });
  await page.getByRole('button', { name: 'Browse all areas', exact: true }).click();

  const dialog = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true });
  await expect(dialog).toBeVisible();
  const box = await dialog.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.width).toBeGreaterThan(800);
  expect(box!.height).toBeGreaterThan(500);
});

test('mobile_area_navigator_is_full_screen', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mobile');
  await boot(page, { area: 'organization' });
  await page.getByRole('button', { name: 'Browse all areas', exact: true }).click();

  const dialog = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true });
  const box = await dialog.boundingBox();
  const viewport = page.viewportSize()!;
  expect(box).not.toBeNull();
  expect(Math.abs(box!.width - viewport.width)).toBeLessThanOrEqual(2);
  expect(Math.abs(box!.height - viewport.height)).toBeLessThanOrEqual(2);
});

test('mobile_focused_navigation_controls_are_44px_touch_targets', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mobile');
  await boot(page, { area: 'organization', setting: 'departments' });

  const controls = [
    page.getByRole('button', { name: 'Back to Organization and core HR', exact: true }).first(),
    page.getByRole('button', { name: 'Previous: Branches', exact: true }).first(),
    page.getByRole('button', { name: 'Next: Designations', exact: true }).first(),
    page.getByRole('button', { name: 'Browse all areas', exact: true }),
  ];
  for (const control of controls) {
    const box = await control.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.width).toBeGreaterThanOrEqual(44);
    expect(box!.height).toBeGreaterThanOrEqual(44);
  }

  await page.getByRole('button', { name: 'Browse all areas', exact: true }).click();
  const close = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true }).getByRole('button', { name: 'Close', exact: true });
  const closeBox = await close.boundingBox();
  expect(closeBox).not.toBeNull();
  expect(closeBox!.width).toBeGreaterThanOrEqual(44);
  expect(closeBox!.height).toBeGreaterThanOrEqual(44);
});

test('navigator_contains_focus_closes_with_escape_and_restores_trigger', async ({ page }) => {
  await boot(page, { area: 'organization' });
  const trigger = page.getByRole('button', { name: 'Browse all areas', exact: true });
  await trigger.click();
  const dialog = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true });
  await expect(dialog.getByRole('heading', { name: 'Browse all setup areas', exact: true })).toBeFocused();

  await page.keyboard.press('Shift+Tab');
  await expect(dialog.getByRole('button', { name: 'Compliance and statutory administration', exact: true })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Close', exact: true })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(trigger).toBeFocused();
});

test('area_selection_focuses_area_heading', async ({ page }) => {
  await boot(page, { area: 'organization' });
  await page.getByRole('button', { name: 'Browse all areas', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true });
  await dialog.getByRole('button', { name: 'Payroll', exact: true }).click();

  await expect(page).toHaveURL(/area=payroll/);
  await expect(page.getByRole('heading', { level: 2, name: 'Payroll', exact: true })).toBeFocused();
});

test('arabic_uses_employee_reference_lists_rename', async ({ page }) => {
  await boot(page, { arabic: true, area: 'organization' });

  await expect(page.getByRole('button', { name: 'قوائم القيم المرجعية للموظفين', exact: true })).toBeVisible();
  await expect(page.getByText('Employee Reference Lists', { exact: true })).toHaveCount(0);
});
