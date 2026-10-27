import { expect, test, type Page, type Route } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import path from 'node:path';
import { openSetupSection, setupSection } from './setupSections';

const task5Evidence = (name: string) => path.resolve(__dirname, '../../docs/evidence/setup-studio-task5', name);

const areas = [
  ['companyStructure', 'Company structure'], ['jobsHeadcount', 'Jobs & headcount'],
  ['timeLeave', 'Time & leave'], ['payBenefits', 'Pay & benefits'],
  ['talentServices', 'Talent & employee services'], ['payrollCompliance', 'Payroll & compliance'],
  ['accessSecurity', 'Users & access'], ['workspace', 'Workspace'],
];

const browserErrors = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('Failed to load resource')) errors.push(message.text()); });
});
// The sign-in page's shader reports a handled fallback to its 2D painter when the
// headless GPU hands back no shader log. It is not a Setup runtime error.
const handledFallbacks = [/^\[LoginAuroraScene\] falling back to the 2D painter:/];
test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page)?.filter(error => !handledFallbacks.some(pattern => pattern.test(error))), 'No application runtime errors').toEqual([]);
});

// API interception is deliberate: these are browser navigation contracts, never tenant writes.
async function boot(page: Page, options: { disabled?: boolean; restricted?: boolean; flagError?: boolean; area?: string; setting?: string; section?: string; arabic?: boolean; group?: boolean; singleCompany?: boolean; officer?: boolean; authDelay?: number; finance?: boolean; statutory?: boolean; beforeSetup?: boolean; readOnly?: boolean; intercept?: (route: Route) => Promise<boolean> } = {}) {
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
    if (path === '/api/auth/me') return reply({ id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: [options.readOnly ? 'Auditor' : options.officer ? 'HR Officer' : 'Admin'], permissions: options.readOnly ? ['organization.read'] : ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', ...(options.finance ? ['finance.gl.manage'] : []), ...(options.statutory ? ['payroll.rates.statutory_override'] : []), ...(options.restricted ? [] : ['learning.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'payroll.read', 'performance.read'])], accountType: options.singleCompany ? 'SingleCompany' : 'Group', isGroupScope: true, companies: companies.map(item => ({ id: item.id, name: item.legalNameEn, code: item.id, countryCode: 'SAU', isActive: true })) });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/country-packs/available') return reply([]);
    if (path === '/api/reference/countries') return reply([{ code: 'SA', name: 'Saudi Arabia' }]);
    if (path === '/api/admin/system-settings') return reply([]);
    if (path === '/api/admin/locations') return reply([]);
    if (path === '/api/admin/master-data/types') return reply([]);
    if (path === '/api/finance/gl/accounts' || path === '/api/finance/gl/mappings') return reply([]);
    if (path === '/api/dashboard/full') return route.fulfill({ status: 503, json: { message: 'Dashboard fixture unavailable' } });
    if (path === '/api/recruitment/onboarding/summary') return reply({ total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 });
    if (path === '/api/companies') return reply({ items: companies, total: companies.length, page: 1, pageSize: 100 });
    // policy_intake is ON in this fixture: it is absent from the disabled keys (the real API lists it until the platform team enables it).
    if (path === '/api/features/disabled-keys') {
      if (options.flagError) return route.fulfill({ status: 503, json: { message: 'Fixture unavailable' } });
      return reply(options.disabled ? ['release_a', 'learning'] : ['release_a']);
    }
    if (path.includes('/features/')) return reply(options.disabled ? [{ key: 'learning', labelEn: 'Learning', enabled: false, navPaths: ['/learning'] }] : []);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/leave/types') return reply([{ id: 'annual-type', code: 'ANNUAL', nameEn: 'Annual', category: 'Annual', isPaid: true, isActive: true }]);
    if (path === '/api/leave/holidays/calendars' || path === '/api/leave/holidays/blackouts') return reply([]);
    if (path === '/api/leave/policies') return reply({ items: [{ id: 'existing-policy', companyId: 'fixture-company', name: 'Saved annual policy', leaveTypeId: 'annual-type', countryCode: 'KSA', annualEntitlementDays: 28, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full' }], total: 1 });
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  // A module's configuration is a Setup section of its own (`/setup?tab=leavePolicies`), not a setting inside an area.
  const setupUrl = options.section
    ? `/setup?tab=${options.section}&companyId=fixture-company`
    : `/setup?tab=aiSetup&path=configuration&companyId=fixture-company&source=retained${options.area ? `&area=${options.area}` : ''}${options.setting ? `&setting=${options.setting}` : ''}`;
  if (options.beforeSetup) {
    await page.goto('/dashboard?before=0');
    await expect(page.getByRole('navigation', { name: 'Primary navigation' })).toBeAttached();
    await page.evaluate(() => {
      history.pushState(history.state, '', '/dashboard?before=1');
      history.pushState(history.state, '', '/dashboard?before=2');
    });
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Administration', exact: true }).evaluate((button: HTMLButtonElement) => button.click());
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Setup', exact: true }).evaluate((button: HTMLButtonElement) => button.click());
    // A bare /setup entry opens Guided setup (the company setup chooser) by default (see ai_setup_defaults_to_guided);
    // the fixture then replaces that entry with the All setup areas destination.
    await expect(page.locator('#setup-section-heading')).toHaveText('Guided setup', { timeout: 30_000 });
    await expect(page.locator('#setup-aiSetup')).toBeVisible();
    await page.evaluate(url => history.replaceState({ fixtureMarker: 'preserved' }, '', url), setupUrl);
    await expect(page.getByRole('button', { name: 'Checklist Templates', exact: true })).toBeVisible();
  } else await page.goto(setupUrl);
  if (options.readOnly) await expect(page.getByRole('heading', { name: 'Companies', exact: true })).toBeVisible({ timeout: 30_000 });
  else if (options.section) await expect(page.locator(`[data-module-section="${options.section}"]`)).toBeVisible({ timeout: 30_000 });
  else await expect(page.getByTestId('setup-studio-workspace')).toBeVisible({ timeout: 30_000 });
  return { writes, reads };
}

// Unsaved Setup work is protected by the application's in-app leave guard
// (NavigationGuardContext), not by native window.confirm. Native dialogs remain
// only for destructive record actions, so any native prompt during a guarded
// departure is itself a regression.
function leaveGuard(page: Page) {
  return page.getByRole('dialog', { name: 'Discard changes?', exact: true });
}

function trackLeaveGuard(page: Page) {
  const native: string[] = [];
  page.on('dialog', async dialog => { native.push(dialog.message()); await dialog.dismiss(); });
  let shown = 0;
  const answer = async (choice: 'stay' | 'leave') => {
    const dialog = leaveGuard(page);
    await expect(dialog).toHaveCount(1);
    shown++;
    await dialog.getByRole('button', { name: choice === 'stay' ? 'Cancel' : 'Discard and leave', exact: true }).click();
    await expect(dialog).toHaveCount(0);
  };
  return {
    /** Keep the unsaved work: the guard appears once and Cancel closes it. */
    stay: () => answer('stay'),
    /** Discard and continue: the guard appears once and the departure proceeds. */
    leave: () => answer('leave'),
    /** No guard is open, exactly `count` were answered, and no native prompt appeared. */
    expectShown: async (count: number) => {
      await expect(leaveGuard(page)).toHaveCount(0);
      expect(shown).toBe(count);
      expect(native, 'Unsaved-work departures never fall back to native prompts').toEqual([]);
    },
  };
}

test('company_actions_edit_is_visible_without_hover_and_opens_existing_record', async ({ page }, info) => {
  const { writes } = await boot(page, { area: 'organization', setting: 'companies' });
  const row = page.getByRole('row').filter({ hasText: 'Workspace Fixture LLC' });
  const edit = row.getByRole('button', { name: 'Edit', exact: true });
  await page.mouse.move(0, 0);
  await expect(edit).toBeVisible();
  expect(await edit.evaluate(element => {
    for (let node: Element | null = element; node; node = node.parentElement) {
      if (Number(getComputedStyle(node).opacity) === 0) return false;
    }
    return true;
  }), 'Edit is painted without hover or focus').toBe(true);
  await edit.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Edit Company', exact: true })).toBeVisible();
  await expect(page.locator('input[placeholder="Acme Corp LLC"]')).toHaveValue('Workspace Fixture LLC');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Edit Company', exact: true })).toHaveCount(0);
  expect(writes).toEqual([]);
  await edit.scrollIntoViewIfNeeded();
  await page.mouse.move(0, 0);
  await page.screenshot({ path: `/tmp/company-actions-${info.project.name}.png`, fullPage: false });
});

test('company_actions_menu_keyboard_escape_and_confirmations_preserve_records', async ({ page }, info) => {
  const { writes } = await boot(page, { area: 'organization', setting: 'companies' });
  const row = page.getByRole('row').filter({ hasText: 'Workspace Fixture LLC' });
  const trigger = row.getByRole('button', { name: /More actions/ });
  await trigger.focus();
  await page.keyboard.press('Tab');
  const nextTabTarget = await page.locator(':focus').elementHandle();
  expect(nextTabTarget).not.toBeNull();
  await trigger.focus();
  await page.keyboard.press('ArrowDown');
  const menu = page.getByRole('menu', { name: /Workspace Fixture LLC/ });
  await expect(menu).toBeVisible();
  await expect(menu.getByRole('menuitem').first()).toBeFocused();
  await page.keyboard.press('End');
  await expect(menu.getByRole('menuitem', { name: 'Delete company', exact: true })).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(menu.getByRole('menuitem').first()).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await expect(menu.getByRole('menuitem').last()).toBeFocused();
  await page.keyboard.press('Home');
  await expect(menu.getByRole('menuitem').first()).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(menu).toHaveCount(0);
  await expect(trigger).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await expect(menu.getByRole('menuitem').last()).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await expect(menu).toHaveCount(0);
  await expect(row.getByRole('button', { name: 'Edit', exact: true })).toBeFocused();
  // Confirmations use the app's translated dialog, never the browser's native confirm(); cancelling writes nothing.
  const prompts: string[] = [];
  page.on('dialog', async dialog => { prompts.push(dialog.message()); await dialog.dismiss(); });
  const confirmations: Array<[string, string, string]> = [
    ['Suspend', 'Suspend company', 'Suspend Workspace Fixture LLC?'],
    ['Delete company', 'Delete company', 'Delete this company? This cannot be undone.'],
  ];
  for (const [action, title, message] of confirmations) {
    if (info.project.use.isMobile) await trigger.tap(); else await trigger.click();
    const item = menu.getByRole('menuitem', { name: action, exact: true });
    if (info.project.use.isMobile) await item.tap(); else await item.click();
    await expect(menu).toHaveCount(0);
    const dialog = page.getByRole('dialog', { name: title, exact: true });
    await expect(dialog).toContainText(message);
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(dialog).toHaveCount(0);
  }
  expect(prompts).toEqual([]);
  expect(writes).toEqual([]);
  await trigger.click();
  const box = await menu.boundingBox();
  expect(box).not.toBeNull();
  const viewport = page.viewportSize()!;
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(viewport.width);
  expect(box!.y + box!.height).toBeLessThanOrEqual(viewport.height);
  const accessibility = await new AxeBuilder({ page }).include('[role="menu"]').analyze();
  expect(accessibility.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')).toEqual([]);
  await page.screenshot({ path: `/tmp/company-actions-menu-${info.project.name}.png`, fullPage: false });
  await page.keyboard.press('Tab');
  await expect(menu).toHaveCount(0);
  expect(await nextTabTarget!.evaluate(element => element === document.activeElement)).toBe(true);
});

test('company_actions_read_only_has_no_mutation_controls', async ({ page }) => {
  const { writes } = await boot(page, { readOnly: true, area: 'organization', setting: 'companies' });
  await expect(page.getByRole('row').filter({ hasText: 'Workspace Fixture LLC' })).toBeVisible();
  await expect(page.getByRole('button', { name: /^(Edit|More actions|Add Company|Create company)/ })).toHaveCount(0);
  expect(writes).toEqual([]);
});

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
  let saved = { id: 'existing-policy', companyId: 'fixture-company', name: 'Annual leave policy for regional operations and extended service teams', leaveTypeId: 'annual-type', countryCode: 'KSA', annualEntitlementDays: 28, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full' };
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

test('leave_policy_takes_several_employment_types_locals_or_expats_service_and_encashment_cap', async ({ page }) => {
  let saved: Record<string, unknown> = { id: 'existing-policy', companyId: 'fixture-company', name: 'Annual leave', leaveTypeId: 'annual-type', countryCode: 'KSA', annualEntitlementDays: 22, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full', employmentType: '' };
  await boot(page, { area: 'leave', setting: 'leavePolicies', intercept: async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/admin/master-data/values') {
      // The tenant's reference list: codes are stored, labels shown.
      const values = url.searchParams.get('typeCode') === 'EmploymentType'
        ? [['FULL_TIME', 'Full-Time'], ['PART_TIME', 'Part-Time'], ['CONTRACT', 'Contract']].map(([code, valueEn], sortOrder) => ({ code, valueEn, valueAr: '', isActive: true, sortOrder }))
        : [];
      await route.fulfill({ json: values }); return true;
    }
    if (url.pathname === '/api/leave/types') { await route.fulfill({ json: [{ id: 'annual-type', code: 'ANNUAL', nameEn: 'Annual Leave', category: 'Annual', isPaid: true, isActive: true }] }); return true; }
    if (url.pathname === '/api/leave/policies') { await route.fulfill({ json: { items: [saved], total: 1 } }); return true; }
    if (url.pathname === '/api/leave/policies/existing-policy' && route.request().method() === 'PUT') {
      saved = { ...saved, ...route.request().postDataJSON() }; await route.fulfill({ json: saved }); return true;
    }
    return false;
  } });
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit Leave Policy', exact: true });
  const all = dialog.getByRole('checkbox', { name: 'All', exact: true });
  await expect(all).toBeChecked();
  await dialog.getByRole('checkbox', { name: 'Full-Time', exact: true }).check();
  await dialog.getByRole('checkbox', { name: 'Part-Time', exact: true }).check();
  await expect(all).not.toBeChecked();
  await dialog.getByLabel('Nationality', { exact: true }).selectOption('Expatriate');
  await dialog.getByLabel('Service required before use (months)', { exact: true }).fill('11');
  await expect(dialog.getByText('Can be taken after probation ends and after 11 month(s) of service.')).toBeVisible();
  await dialog.getByRole('checkbox', { name: 'Encashment Allowed', exact: true }).check();
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).click();
  await expect(dialog.getByText('Enter the maximum days per encashment, or untick Encashment Allowed.')).toBeVisible();
  await dialog.getByLabel('Max days per encashment *', { exact: true }).fill('10');
  await page.screenshot({ path: '/tmp/leave-policy-eligibility.png', fullPage: true });
  const request = page.waitForRequest(r => r.method() === 'PUT' && new URL(r.url()).pathname === '/api/leave/policies/existing-policy');
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).click();
  expect((await request).postDataJSON()).toMatchObject({
    employmentType: 'FULL_TIME,PART_TIME', nationalityScope: 'Expatriate', minimumServiceMonths: 11, appliesOnProbation: false,
    encashmentAllowed: true, encashmentMaxDays: 10,
  });
  // The list reads the saved codes back as their labels.
  await expect(page.getByText(/Full-Time, Part-Time/)).toBeVisible();
});

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
  await expect(page.getByRole('button', { name: /Add Company|Create company|New Policy|Edit/, exact: true })).toHaveCount(0);
  expect(reads.filter(path => path.startsWith('/api/leave/'))).toEqual([]);
  expect(writes).toEqual([]);
});

test('forged_area_or_return_path_is_ignored', async ({ page }) => {
  const { writes } = await boot(page, { area: 'forged-area', setting: 'leavePolicies&returnTo=https%3A%2F%2Fexample.invalid%2Fcapture' });
  // Wait for canonicalisation: the forged area, the setting it tried to borrow and the
  // return path are all dropped, so the overview below is the settled state, not a first paint.
  await expect(page).not.toHaveURL(/[?&](area=forged-area|setting=|returnTo=)/);
  await expect(page).toHaveURL(/[?&]path=configuration(&|$)/);
  // The overview is named by the section header; it has no area heading of its own.
  await expect(page.getByRole('heading', { level: 2, name: 'All setup areas', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'New Policy', exact: true })).toHaveCount(0);
  await selectArea(page, 'Time & leave');
  await page.getByTestId('setup-area-content').getByRole('link', { name: 'Leave requests and balances', exact: true }).click();
  await expect(page).toHaveURL(/^https?:\/\/[^/]+\/leave\?/);
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
        // Departments, designations and grades belong to a company: the fixture's rows say whose they are.
        : path.endsWith('grades') ? { id: 'grade', companyId: 'fixture-company', code: 'G1', name, level: 1, isActive: true }
        : { id: 'cost', companyId: 'fixture-company', code: 'CC1', name, isActive: true };
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
    // Company structure runs Branches, Locations, Cost centres, Departments: step to Departments without reloading.
    for (const next of ['Locations', 'Cost centres', 'Departments']) {
      await page.getByRole('button', { name: `Next: ${next}`, exact: true }).first().click();
    }
    await page.getByRole('button', { name: 'Add Department', exact: true }).click();
    await expect(page.getByRole('dialog').getByRole('option', { name: 'STALE scope value', exact: true })).toHaveCount(0);
    if (!failCurrent) await expect(page.getByRole('dialog').getByRole('option', { name: 'Current scope value', exact: true })).toBeAttached();
    await page.keyboard.press('Escape');
    // Designations is the next area's section (Jobs & headcount), reached through the persistent Setup sections list.
    await openSetupSection(page, 'Designations');
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
  // The policies editor is its own Setup section; the Leave page links to it and no longer carries a copy.
  await page.goto('/setup?tab=leavePolicies');
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
  const { reads } = await boot(page, { area: 'onboarding', setting: 'onboardingChecklists' });
  await expect(page.getByRole('button', { name: 'Checklist Templates', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-focused-workspace').getByText('Applies to all companies', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create Checklist', exact: true })).toBeVisible();
  await expect(page.getByPlaceholder('Employee ID', { exact: true })).toHaveCount(0);
  expect(reads.filter(path => path.includes('/onboarding/tasks') || path.includes('/onboarding/summary'))).toEqual([]);
});

test('existing_setup_deep_links_still_work', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'companies' });
  await expect(page.getByRole('button', { name: /Create company/ })).toBeVisible();
  await page.goto('/setup?tab=companies');
  await expect(page.getByRole('button', { name: /Create company/ })).toBeVisible();
});

test('single_company_with_an_existing_legal_entity_does_not_offer_an_impossible_add_action', async ({ page }) => {
  const { writes } = await boot(page, { area: 'organization', setting: 'companies', singleCompany: true });

  await expect(page.getByRole('row').filter({ hasText: 'Workspace Fixture LLC' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create company', exact: true })).toHaveCount(0);
  expect(writes).toEqual([]);
});

test('branch_arabic_suggestion_uses_organization_wording_instead_of_phonetic_guessing', async ({ page }) => {
  let requestBody: Record<string, unknown> | undefined;
  await boot(page, { area: 'organization', setting: 'branches', intercept: async route => {
    const request = route.request();
    if (request.method() !== 'POST' || new URL(request.url()).pathname !== '/api/localization/transliterate') return false;
    requestBody = request.postDataJSON();
    await route.fulfill({ json: { suggestion: 'مكتب جدة', requiresManualEntry: false } });
    return true;
  } });

  await page.getByRole('button', { name: 'Add Branch', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Add Branch', exact: true });
  await dialog.getByPlaceholder('Dubai Head Office', { exact: true }).fill('Jeddah Office');
  await dialog.getByRole('button', { name: 'Suggest (AR)', exact: true }).click();

  await expect(dialog.locator('input[dir="rtl"]')).toHaveValue('مكتب جدة');
  expect(requestBody).toEqual({ text: 'Jeddah Office', target: 'ar', kind: 'organization-label' });
});

test('department_arabic_suggestion_uses_organization_wording_instead_of_phonetic_guessing', async ({ page }) => {
  let requestBody: Record<string, unknown> | undefined;
  await boot(page, { area: 'organization', setting: 'departments', intercept: async route => {
    const request = route.request();
    if (request.method() !== 'POST' || new URL(request.url()).pathname !== '/api/localization/transliterate') return false;
    requestBody = request.postDataJSON();
    await route.fulfill({ json: { suggestion: 'عمليات جدة', requiresManualEntry: false } });
    return true;
  } });

  await page.getByRole('button', { name: 'Add Department', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Add Department', exact: true });
  await dialog.getByPlaceholder('Engineering', { exact: true }).fill('Jeddah Operations');
  await dialog.getByRole('button', { name: 'Suggest (AR)', exact: true }).click();

  await expect(dialog.locator('input[dir="rtl"]')).toHaveValue('عمليات جدة');
  expect(requestBody).toEqual({ text: 'Jeddah Operations', target: 'ar', kind: 'organization-label' });
});

test('company_limit_refusal_is_shown_at_the_first_wizard_step_with_the_server_reason', async ({ page }) => {
  // Creation moved to the create-company wizard: the plan limit is refused at step 1, before any
  // draft or company is written, with the server's own sentence.
  const refusal = 'Your plan allows up to 1 legal company. Upgrade your plan to add more.';
  const companyPosts: string[] = [];
  await boot(page, { area: 'organization', setting: 'companies', intercept: async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === 'POST' && (path === '/api/companies' || path.startsWith('/api/company-setup'))) { companyPosts.push(path); }
    if (path === '/api/company-setup/context') {
      await route.fulfill({ json: {
        canCreate: false, refusalCode: 'company_limit_reached', refusalMessage: refusal, creationMode: 'GroupSelfServiceWithinLimit',
        accountType: 'Group', companiesUsed: 1, companiesAllowed: 1, isGroupScope: true, sisterCompanies: [],
        sharedCatalog: { departments: [], designations: [], grades: [] },
      } });
      return true;
    }
    if (path === '/api/company-setup/drafts') { await route.fulfill({ json: [] }); return true; }
    return false;
  } });

  await page.getByRole('button', { name: 'Create company', exact: true }).click();
  await expect(page).toHaveURL(/\/setup\/new-company/);
  await expect(page.getByText(refusal, { exact: true })).toBeVisible();
  await expect(page.getByText('To add another company, ask your account manager to raise the company limit on your plan.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Start', exact: true })).toBeDisabled();
  expect(companyPosts).toEqual([]);
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
  const guard = trackLeaveGuard(page);
  await switchCompany();
  await guard.stay();
  await expect(page.getByPlaceholder('DXB-01', { exact: true })).toHaveValue('DRAFT-BRANCH');
  await expect(page.getByPlaceholder('Dubai Head Office', { exact: true })).toHaveValue('Keep this branch');
  await expect(page.getByTestId('company-switcher').getByRole('button')).toContainText('fixture-company');
  await guard.expectShown(1);
  const retainedRequest = page.waitForRequest(request => new URL(request.url()).pathname === '/api/companies');
  await page.evaluate(() => window.dispatchEvent(new Event('kynexone:companies-changed')));
  expect((await retainedRequest).headers()['x-company-id']).toBe('fixture-company');
  const refreshed = page.waitForRequest(request => new URL(request.url()).pathname === '/api/branches' && request.headers()['x-company-id'] === 'second-company');
  await switchCompany();
  await guard.leave();
  await refreshed;
  await guard.expectShown(2);
  await expect(page.getByRole('dialog', { name: 'Add Branch', exact: true })).toHaveCount(0);
  await expect(page.getByTestId('company-switcher').getByRole('button')).toContainText('second-company');
  expect(writes).toEqual([]);
});

test('unsaved_template_blocks_area_and_company_switch_when_declined', async ({ page }) => {
  await boot(page, { group: true, area: 'onboarding', setting: 'onboardingChecklists' });
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep this draft');
  const guard = trackLeaveGuard(page);
  await selectArea(page, 'Payroll & compliance');
  await guard.stay();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep this draft');
  // Cancelling the guard keeps the user where they were: in the still-open area navigator.
  const navigator = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true });
  await expect(navigator).toBeVisible();
  await navigator.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(navigator).toHaveCount(0);
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await guard.stay();
  await expect(page).toHaveURL(/companyId=fixture-company/);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep this draft');
  await guard.expectShown(2);
});

test('transitional_route_has_supported_return_to_selected_area', async ({ page }) => {
  await boot(page, { area: 'leave' });
  await page.getByTestId('setup-area-content').getByRole('link', { name: 'Leave requests and balances', exact: true }).click();
  await page.getByRole('link', { name: 'Back to All setup areas', exact: true }).click();
  await expect(page).toHaveURL(/area=timeLeave/);
  await expect(page).toHaveURL(/path=configuration/);
  await expect(page).toHaveURL(/companyId=fixture-company/);
});

test('source_pages_still_use_the_canonical_forms', async ({ page }) => {
  const { writes } = await boot(page);
  // The Leave page's old Policies tab address leads to the Setup section, which is the same editor.
  await page.goto('/leave?tab=policies');
  await expect(page).toHaveURL(/\/setup\?tab=leavePolicies/);
  await expect(page.getByText('Saved annual policy', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByPlaceholder('e.g. UAE Annual Leave — Full-Time')).toHaveValue('Saved annual policy');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  // Onboarding keeps its daily work (generating a joiner's tasks from a checklist) and links to the checklist editor,
  // which is Setup → Onboarding checklists and is the same editor.
  await page.goto('/recruitment/onboarding');
  await expect(page.getByPlaceholder('Employee ID', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Checklist Templates', exact: true })).toHaveCount(0);
  await page.getByRole('link', { name: 'Onboarding settings', exact: true }).click();
  await expect(page).toHaveURL(/\/setup\?tab=onboardingChecklists/);
  await expect(page.getByRole('button', { name: 'Create Checklist', exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});

async function selectArea(page: Page, label: string) {
  const browse = page.getByRole('button', { name: 'Browse all areas', exact: true });
  // The overview lists every area itself (it has no "Browse all areas"); each row is its name and a description.
  const overviewRow = page.getByTestId('setup-area-content').getByRole('link', { name: new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`) });
  if (!await browse.isVisible() && !await overviewRow.first().isVisible()) {
    const back = page.getByRole('button', { name: /^Back to / }).first();
    if (await back.isVisible()) await back.click();
    await browse.waitFor({ state: 'visible', timeout: 2_000 }).catch(() => {});
  }
  if (await browse.isVisible()) {
    await browse.evaluate((button: HTMLButtonElement) => button.click());
    await page.getByRole('dialog', { name: 'Browse all setup areas', exact: true }).getByRole('button', { name: label, exact: true }).evaluate((button: HTMLButtonElement) => button.click());
    return;
  }
  if (await overviewRow.first().isVisible()) await overviewRow.first().evaluate((link: HTMLAnchorElement) => link.click());
}

async function clickDashboard(page: Page) {
  const mobile = page.getByRole('navigation', { name: 'Primary', exact: true }).getByRole('link', { name: 'Home', exact: true });
  if (await mobile.isVisible()) await mobile.click();
  else await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Dashboard', exact: true }).click();
}

test('setup_studio_canonicalisation_never_rewrites_another_setup_section', async ({ page }) => {
  const { writes } = await boot(page, { group: true, area: 'leave' });
  // Older section URLs carried the Studio area. That area is not a Studio deep link: canonicalising it must not
  // turn the section back into All setup areas.
  for (const [url, heading] of [
    ['/setup?tab=policyLibrary&area=timeLeave&companyId=fixture-company', 'Policy library'],
    ['/setup?tab=grades&area=overview&companyId=fixture-company', 'Grades'],
  ] as const) {
    await page.goto(url);
    await expect(page.getByRole('heading', { level: 2, name: heading, exact: true })).toBeVisible();
    await page.waitForTimeout(500);
    await expect(page).toHaveURL(new RegExp(`[?&]tab=${url.split('tab=')[1].split('&')[0]}(&|$)`));
    await expect(page.getByTestId('setup-studio-workspace')).toBeHidden();
  }
  await openSetupSection(page, 'Policy library');
  await expect(page).toHaveURL(/[?&]tab=policyLibrary(&|$)/);
  await page.waitForTimeout(500);
  await expect(page).toHaveURL(/[?&]tab=policyLibrary(&|$)/);
  await expect(page.getByRole('heading', { level: 2, name: 'Policy library', exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});

// Wave 1: a settings section's address no longer carries the area (re-entry must respect the Guided setup / All setup
// areas choice), so All setup areas reopens at its overview; the company chosen meanwhile is kept.
test('return_from_library_preserves_company_and_opens_the_overview', async ({ page }) => {
  const { writes } = await boot(page, { group: true, area: 'leave' });
  await openSetupSection(page, 'Policy library');
  await expect(page).not.toHaveURL(/area=/);
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await openSetupSection(page, 'All setup areas');
  await expect(page).toHaveURL(/path=configuration/);
  await expect(page).not.toHaveURL(/area=timeLeave/);
  await expect(page).toHaveURL(/companyId=second-company/);
  await expect(page.getByTestId('setup-studio-workspace')).toContainText('Second Fixture LLC');
  expect(writes).toEqual([]);
});

test('cancel_return_keeps_dirty_edits_on_back_forward_and_sidebar', async ({ page }) => {
  await boot(page, { area: 'payroll' });
  await selectArea(page, 'Talent & employee services');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Talent & employee services', exact: true })).toBeVisible();
  await page.getByTestId('setup-area-content').getByRole('button', { name: 'Onboarding checklists', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep history draft');
  const guard = trackLeaveGuard(page);
  const originalUrl = page.url();
  await page.evaluate(() => history.back());
  await guard.stay();
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep history draft');
  await clickDashboard(page);
  await guard.stay();
  await expect(page).toHaveURL(originalUrl);
  // Chromium ignores a second traversal issued in the same task as the restoring traversal.
  await page.waitForTimeout(100);
  await page.evaluate(() => history.back());
  await guard.leave();
  await expect(page).not.toHaveURL(originalUrl);
  await guard.expectShown(3);
  await page.evaluate(() => history.forward());
  await expect(page).toHaveURL(originalUrl);
});

test('manual_areas_do_not_claim_document_prefill', async ({ page }) => {
  const { writes } = await boot(page, { area: 'leave' });
  await expect(page.getByTestId('setup-area-content')).toContainText('Enter and maintain this area manually');
  await expect(page.getByRole('button', { name: 'Fill from document', exact: true })).toHaveCount(0);
  await selectArea(page, 'Talent & employee services');
  await expect(page.getByTestId('setup-area-content')).toContainText('Enter and maintain this area manually');
  expect(writes).toEqual([]);
});

for (const fallback of [false, true]) test(`${fallback ? 'fallback_' : ''}declined_forward_keeps_editor_and_url`, async ({ page }) => {
  if (fallback) await page.addInitScript(() => Object.defineProperty(window, 'navigation', { value: undefined, configurable: true }));
  await boot(page, { area: 'onboarding', setting: 'onboardingChecklists' });
  await selectArea(page, 'Payroll & compliance');
  await expect(page).toHaveURL(/area=payrollCompliance/);
  await page.evaluate(() => history.back());
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep forward draft');
  const originalUrl = page.url();
  const guard = trackLeaveGuard(page);
  await page.evaluate(() => history.forward());
  await guard.stay();
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep forward draft');
  await guard.expectShown(1);
});

test('history_fallback_decline_retains_actual_editor', async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(window, 'navigation', { value: undefined, configurable: true }));
  await boot(page, { area: 'payroll', authDelay: 150 });
  await selectArea(page, 'Talent & employee services');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Talent & employee services', exact: true })).toBeVisible();
  await page.getByTestId('setup-area-content').getByRole('button', { name: 'Onboarding checklists', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep fallback draft');
  const originalUrl = page.url();
  const guard = trackLeaveGuard(page);
  await page.evaluate(() => history.back());
  await guard.stay();
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep fallback draft');
  await clickDashboard(page);
  await guard.leave();
  await expect(page).toHaveURL(/\/dashboard$/);
  await guard.expectShown(2);
  await page.evaluate(() => history.pushState(history.state, '', '/dashboard?history-check=1'));
  await expect(page).toHaveURL(/history-check=1/);
  await page.evaluate(() => history.back());
  await expect(page).toHaveURL(/\/dashboard$/);
});

test('administration_access_and_filters_do_not_create_unsaved_edits', async ({ page }) => {
  await boot(page);
  // The Studio's "Administration settings" button is gone; Workspace settings is a Setup section of its own.
  await openSetupSection(page, 'Workspace settings');
  await expect(page.getByRole('heading', { level: 2, name: 'Workspace settings', exact: true })).toBeVisible();
  await openSetupSection(page, 'Branches');
  const guard = trackLeaveGuard(page);
  await openSetupSection(page, 'Audit log');
  await page.getByPlaceholder('e.g. MasterDataType').fill('filtered');
  await openSetupSection(page, 'All setup areas');
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
  await guard.expectShown(0);
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
  const guard = trackLeaveGuard(page);
  await page.evaluate(() => history.back());
  await expect(page).not.toHaveURL(/setting=/);
  await guard.expectShown(0);
  await openRules();
  if (action === 'override') {
    await page.getByRole('button', { name: 'New override', exact: true }).click();
    await page.getByRole('dialog').getByRole('combobox', { name: 'Rule', exact: true }).selectOption('custom.allowance');
    await page.getByLabel("Your company's value", { exact: true }).fill('8');
    await page.getByLabel(/Approved reason and source/).fill('Preserve approved source');
  } else {
    await page.getByRole('button', { name: 'Reject', exact: true }).click();
    await page.getByLabel('Reason for rejecting (optional)', { exact: true }).fill('Preserve rejection reason');
  }
  const before = page.url();
  await page.evaluate(() => history.back());
  await guard.stay();
  await expect(page).toHaveURL(before);
  await expect(page.getByRole('dialog').getByRole('textbox').last()).toHaveValue(action === 'override' ? 'Preserve approved source' : 'Preserve rejection reason');
  const endpoint = action === 'override' ? '**/api/statutory-rules' : '**/api/finance/rates/statutory/override/pending-1/reject';
  await page.route(endpoint, route => route.request().method() === 'GET' ? route.fallback() : route.fulfill({ status: 500, json: { message: 'Fixture write failed' } }));
  const save = page.getByRole('dialog').getByRole('button', { name: action === 'override' ? 'Send for approval' : 'Reject', exact: true });
  await save.click();
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Fixture write failed');
  // Chromium ignores a second traversal issued in the same task as the restoring traversal.
  await page.waitForTimeout(100);
  await page.evaluate(() => history.back());
  await guard.stay();
  await expect(page).toHaveURL(before);
  await page.route(endpoint, route => route.request().method() === 'GET' ? route.fallback() : route.fulfill({ status: 204 }));
  await save.click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.waitForTimeout(100);
  await page.evaluate(() => history.back());
  await expect(page).not.toHaveURL(/setting=/);
  await guard.expectShown(2);
});

test('smtp_failed_initial_load_and_save_keep_authored_values_guarded', async ({ page }) => {
  await boot(page);
  await page.route('**/api/admin/system-settings**', route => route.fulfill({ status: 500, json: { message: 'Fixture settings unavailable' } }));
  await openSetupSection(page, 'Workspace settings');
  await openSetupSection(page, 'Email delivery');
  await expect(page).toHaveURL(/[?&]tab=emailConfig(&|$)/);
  await page.getByPlaceholder('smtp.example.com', { exact: true }).fill('smtp.retained.example');
  const before = page.url();
  const guard = trackLeaveGuard(page);
  await page.evaluate(() => history.back());
  await guard.stay();
  await expect(page).toHaveURL(before);
  await expect(page.getByPlaceholder('smtp.example.com', { exact: true })).toHaveValue('smtp.retained.example');
  const failed = page.waitForResponse(response => response.request().method() !== 'GET' && response.url().includes('/api/admin/system-settings'));
  await page.getByRole('button', { name: 'Save Settings', exact: true }).click();
  await failed;
  await expect(page.getByRole('button', { name: 'Save Settings', exact: true })).toBeEnabled();
  await page.evaluate(() => history.back());
  await guard.stay();
  await expect(page).toHaveURL(before);
  await page.route('**/api/admin/system-settings**', route => route.fulfill({ json: route.request().method() === 'GET' ? [] : {} }));
  await page.getByRole('button', { name: 'Save Settings', exact: true }).click();
  await expect(page.getByText('Settings saved successfully.', { exact: true })).toBeVisible();
  await page.evaluate(() => history.back());
  await expect(page).not.toHaveURL(before);
  await guard.expectShown(2);
});

test('forged_company_configuration_is_fail_closed_until_explicit_recovery', async ({ page }) => {
  const { writes } = await boot(page, { group: true });
  // Attribute reads to the document that issued them. Clearing a shared list after boot
  // also counted the boot page's own reference reads when they reached the fixture late.
  const forgedReads: string[] = [];
  page.on('request', request => {
    if (request.method() === 'GET' && request.frame().url().includes('companyId=forged-company')) forgedReads.push(new URL(request.url()).pathname);
  });
  await page.goto('/setup?tab=aiSetup&path=configuration&companyId=forged-company&area=timeLeave&setting=leavePolicies');
  await expect(page.getByRole('alert').getByRole('heading', { name: 'Company access required', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-studio-workspace')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'New Policy', exact: true })).toHaveCount(0);
  await page.waitForTimeout(250);
  expect(forgedReads.filter(path => path.startsWith('/api/'))).not.toHaveLength(0);
  expect(forgedReads.filter(path => path === '/api/companies' || path.startsWith('/api/grades') || path.startsWith('/api/cost-centers') || path.startsWith('/api/leave/'))).toEqual([]);
  await expect(page).toHaveURL(/companyId=forged-company/);
  await page.getByRole('button', { name: 'Use Workspace Fixture LLC', exact: true }).click();
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
  await expect(page).toHaveURL(/companyId=fixture-company/);
  expect(writes).toEqual([]);
});

test('canonical_save_clears_dirty_navigation_state', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'branches' });
  await page.getByRole('button', { name: 'Add Branch', exact: true }).click();
  await page.getByPlaceholder('DXB-01', { exact: true }).fill('SAVED');
  await page.getByPlaceholder('Dubai Head Office', { exact: true }).fill('Saved branch');
  await page.getByRole('dialog').getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const guard = trackLeaveGuard(page);
  await selectArea(page, 'Payroll & compliance');
  await expect(page).toHaveURL(/area=payrollCompliance/);
  await guard.expectShown(0);
});

for (const destination of ['palette', 'profile', 'signout', 'sidebar_signout']) test(`shell_${destination}_protects_authored_values`, async ({ page }) => {
  // TopBar's user menu is intentionally desktop-only.
  if (destination !== 'palette') await page.setViewportSize({ width: 1440, height: 1000 });
  await boot(page, { area: 'onboarding', setting: 'onboardingChecklists' });
  await page.getByPlaceholder('Task title', { exact: true }).fill('Retain shell draft');
  const originalUrl = page.url();
  const guard = trackLeaveGuard(page);
  const target = destination === 'palette'
    ? page.getByRole('button', { name: /Dashboard.*\/dashboard/ })
    : destination === 'sidebar_signout' ? page.getByRole('button', { name: 'Sign out', exact: true })
    : page.locator('header').getByRole('button', { name: destination === 'profile' ? 'My Profile' : 'Sign out', exact: true });
  // The palette and user menu close once an entry is chosen; reopen them for each attempt.
  const choose = async () => {
    if (destination === 'palette') {
      await page.keyboard.press('Control+k');
      await page.getByPlaceholder('Search modules, employees, reports…').fill('Dashboard');
    } else if (destination !== 'sidebar_signout' && !await target.isVisible()) await page.getByRole('button', { name: 'Open user menu' }).click();
    await target.click();
  };
  await choose();
  await guard.stay();
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Retain shell draft');
  await choose();
  await guard.leave();
  // This fixture has no ess.read: accepted profile navigation must preserve
  // the incumbent access-denied redirect, not briefly assert the pending URL.
  await expect(page).toHaveURL(destination === 'palette' || destination === 'profile' ? /\/dashboard$/ : /\/login$/);
  // Development Strict Mode replays PermissionGate's mount effect, so a dev server can show the toast twice.
  if (destination === 'profile') await expect(page.getByText('Access Denied', { exact: true }).first()).toBeVisible();
  await guard.expectShown(2);
  if (destination === 'palette') {
    // The same seam is inert after Setup unmounts.
    await page.getByRole('button', { name: 'Open command search' }).click();
    await page.getByPlaceholder('Search modules, employees, reports…').fill('Dashboard');
    await page.getByRole('button', { name: /Dashboard.*\/dashboard/ }).click();
    await expect(page).toHaveURL(/\/dashboard$/);
    await guard.expectShown(2);
  }
});

for (const direct of [true] as const) test(`${direct ? 'direct' : 'embedded'}_same_destination_keeps_dirty_state`, async ({ page }) => {
  await boot(page, { area: direct ? 'payroll' : 'onboarding', setting: direct ? 'glMapping' : 'onboarding', finance: true });
  if (direct) await page.goto('/setup?tab=glMapping&companyId=fixture-company');
  else await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  const field = page.getByPlaceholder(direct ? 'Account name' : 'Task title', { exact: true });
  await field.fill('Retain same destination draft');
  const originalUrl = page.url();
  const guard = trackLeaveGuard(page);
  await openSetupSection(page, 'Payroll accounting');
  await expect(page).toHaveURL(originalUrl);
  await expect(field).toHaveValue('Retain same destination draft');
  await guard.expectShown(0);
  const depart = () => direct ? openSetupSection(page, 'Policy library') : selectArea(page, 'Payroll & compliance');
  await depart();
  await guard.stay();
  await expect(field).toHaveValue('Retain same destination draft');
  await depart();
  await guard.leave();
  await expect(page).toHaveURL(direct ? /tab=policyLibrary/ : /area=payrollCompliance/);
  await guard.expectShown(2);
});

test('gl_internal_scope_and_subtabs_protect_authored_values', async ({ page }) => {
  await boot(page, { area: 'payroll', setting: 'glMapping', finance: true });
  const scope = page.getByLabel('Configuration scope');
  await expect(scope).toHaveValue('');
  const name = page.getByPlaceholder('Account name', { exact: true });
  await page.getByPlaceholder('5006', { exact: true }).fill('5010');
  await name.fill('Group authored account');
  const guard = trackLeaveGuard(page);
  await scope.selectOption('');
  await page.getByRole('tab', { name: 'Accounts & Mapping', exact: true }).click();
  await guard.expectShown(0);
  await scope.selectOption('fixture-company');
  await guard.stay();
  await expect(scope).toHaveValue('');
  await expect(name).toHaveValue('Group authored account');
  await page.getByRole('tab', { name: 'ERP API access', exact: true }).click();
  await guard.stay();
  await expect(name).toHaveValue('Group authored account');
  await page.getByRole('tab', { name: 'Accounts & Mapping', exact: true }).press('End');
  await guard.stay();
  await expect(name).toHaveValue('Group authored account');
  await guard.expectShown(3);
  await scope.selectOption('fixture-company');
  await guard.leave();
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
  await guard.leave();
  await expect(page.getByRole('tab', { name: 'ERP API access', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Accounts & Mapping', exact: true }).click();
  await expect(name).toHaveValue('');
  await guard.expectShown(5);
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
  const guard = trackLeaveGuard(page);
  const banner = page.getByRole('button', { name: 'Control account 5000: Review balance', exact: true });
  await banner.click();
  await guard.stay();
  await expect(page.getByLabel('Note (optional)', { exact: true })).toHaveValue('Keep period explanation');
  await banner.click();
  await guard.leave();
  await expect(page.getByRole('tab', { name: 'Accounts & Mapping', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Accounting periods', exact: true }).click();
  await expect(page.getByLabel('Note (optional)', { exact: true })).toHaveValue('');
  await guard.expectShown(2);
});

test('gl_authored_values_are_guarded_but_search_is_not', async ({ page }) => {
  await boot(page, { area: 'payroll', setting: 'glMapping', finance: true });
  await page.getByRole('searchbox', { name: 'Search accounts by code or name' }).fill('filter only');
  await page.getByRole('combobox', { name: 'Filter by account type' }).selectOption('Expense');
  const guard = trackLeaveGuard(page);
  const library = () => openSetupSection(page, 'Policy library');
  await library();
  await expect(page).toHaveURL(/tab=policyLibrary/);
  await guard.expectShown(0);
  // Settings addresses no longer carry the area, so the GL form is reopened from its own Setup section.
  await openSetupSection(page, 'Payroll accounting');
  await page.getByPlaceholder('5006', { exact: true }).fill('5010');
  await page.getByPlaceholder('Account name', { exact: true }).fill('Retain authored GL account');
  await library();
  await guard.stay();
  await expect(page.getByPlaceholder('Account name', { exact: true })).toHaveValue('Retain authored GL account');
  await library();
  await guard.leave();
  await expect(page).toHaveURL(/tab=policyLibrary/);
  await guard.expectShown(2);
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
  const guard = trackLeaveGuard(page);
  await openSetupSection(page, 'Policy library');
  await expect(page).toHaveURL(/tab=policyLibrary/);
  await guard.expectShown(0);
});

test('url_back_restores_area', async ({ page }) => {
  await boot(page, { area: 'leave' });
  await selectArea(page, 'Payroll & compliance');
  await expect(page).toHaveURL(/area=payrollCompliance/);
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Payroll & compliance', exact: true })).toBeVisible();
  await page.goBack();
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Time & leave', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Time & leave', exact: true })).toBeVisible();
});

test('feature_unavailable_does_not_mount_editor', async ({ page }) => {
  const { writes, reads } = await boot(page, { disabled: true, area: 'learning' });
  const content = page.getByTestId('setup-area-content');
  await expect(content.getByText('Not enabled for this tenant', { exact: true })).toBeVisible();
  // Talent & employee services also holds settings that are not behind the Learning switch (Notifications, Performance
  // cycles), so the guarantee is per destination: the Learning row is explained and is neither a link nor a button.
  const blueprint = page.getByTestId('setup-area-blueprint');
  const learning = blueprint.getByRole('listitem').filter({ hasText: 'Learning catalogue and rules' });
  await expect(learning).toContainText('Not enabled for this tenant');
  await expect(learning.getByRole('link')).toHaveCount(0);
  await expect(learning.getByRole('button')).toHaveCount(0);
  await expect(blueprint.locator('a[href*="/learning"]')).toHaveCount(0);
  expect(reads.filter(path => path.startsWith('/api/learning'))).toEqual([]);
  expect(writes).toEqual([]);
});

test('restricted_destination_explains_access_without_mounting_editor', async ({ page }) => {
  await boot(page, { restricted: true, area: 'learning' });
  // Several destinations of the area say so; the Learning one is the row this contract is about.
  const learning = page.getByTestId('setup-area-blueprint').getByRole('listitem').filter({ hasText: 'Learning catalogue and rules' });
  await expect(learning).toContainText('Your role does not have access');
  await expect(learning.getByRole('button')).toHaveCount(0);
  // No destination link (the breadcrumb back to All setup areas is navigation, not a destination).
  await expect(page.getByTestId('setup-area-blueprint').getByRole('link')).toHaveCount(0);
});

test('failed_availability_read_is_not_empty_configuration', async ({ page }) => {
  await boot(page, { flagError: true, area: 'organization' });
  await expect(page.getByTestId('setup-area-content').getByText('Availability could not be checked', { exact: true }).first()).toBeVisible();
  // No destination link (the breadcrumb back to All setup areas is navigation, not a destination).
  await expect(page.getByTestId('setup-area-blueprint').getByRole('link')).toHaveCount(0);
});

test('overview_does_not_claim_configuration_complete', async ({ page }) => {
  const { writes } = await boot(page, { area: 'invalid-area' });
  const content = page.getByTestId('setup-area-content');
  // Wave 1: the section header names the overview, and the grey "status is not assessed" box is gone; the overview
  // still claims nothing about completeness.
  await expect(page.locator('#setup-section-heading')).toHaveText('All setup areas');
  await expect(content).toHaveAccessibleName('All setup areas');
  await expect(content.getByText('Configuration status is not assessed here. Open an area to review its saved settings.', { exact: true })).toHaveCount(0);
  await expect(content.getByText(/100%|configuration complete|12 of 12/i)).toHaveCount(0);
  await expect(page.getByText('Workspace Fixture LLC', { exact: true }).last()).toBeVisible();
  expect(writes).toEqual([]);
});

// The page has one left-hand navigation: the grouped Setup sections column. All setup areas lists its areas in the
// overview (no side rail there); an area page adds a breadcrumb, "Browse all areas" and the "In this area" rail on the
// right. The Studio's former second column of area buttons beside the sections column was retired with the redesign.
test('desktop_configuration_keeps_one_left_navigation_with_areas_and_in_this_area_rail', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop');
  await boot(page, { area: 'organization' });

  const sections = page.getByRole('navigation', { name: 'Setup sections' });
  await expect(sections).toBeVisible();
  await expect(sections.getByRole('link', { name: 'All setup areas', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByRole('navigation', { name: 'Setup areas' })).toHaveCount(0);

  const nextActions = page.getByRole('complementary', { name: 'In this area' });
  await expect(nextActions).toBeVisible();
  // Readiness-driven next actions are not assessed yet, so the rail offers shortcuts, never a "continue" button.
  await expect(nextActions.getByRole('button', { name: 'Continue next action', exact: true })).toHaveCount(0);
  await expect(nextActions).toContainText('Companies');
  await expect(page.getByText(/proposed changes|blockers|applied records/i)).toHaveCount(0);
  const content = page.getByTestId('setup-area-content');
  const [sectionsBox, contentBox, nextBox] = await Promise.all([sections.boundingBox(), content.boundingBox(), nextActions.boundingBox()]);
  expect(sectionsBox!.x + sectionsBox!.width).toBeLessThanOrEqual(contentBox!.x);
  expect(contentBox!.x + contentBox!.width).toBeLessThanOrEqual(nextBox!.x);
  if (process.env.CAPTURE_TASK5_EVIDENCE === '1') await page.screenshot({ path: task5Evidence('approved-three-column-overview.png'), fullPage: false });

  const browse = page.getByRole('button', { name: 'Browse all areas', exact: true });
  await browse.click();
  const areaList = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true }).getByRole('navigation', { name: 'Setup areas' });
  for (const [, label] of areas) await expect(areaList.getByRole('button', { name: label, exact: true })).toBeVisible();
  await areaList.getByRole('button', { name: 'Time & leave', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Time & leave', exact: true })).toBeFocused();
  await expect(nextActions).toContainText('Leave types');
  await nextActions.getByRole('button', { name: /^Leave types/ }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Leave types', exact: true })).toBeFocused();
  await expect(sections).toBeVisible();
  await expect(nextActions).toBeVisible();
  if (process.env.CAPTURE_TASK5_EVIDENCE === '1') await page.screenshot({ path: task5Evidence('approved-three-column-configuration.png'), fullPage: false });
});

test('keyboard_and_mobile_area_navigation', async ({ page }, testInfo) => {
  await boot(page);
  // The overview lists every area as a link; "Browse all areas" is offered only once an area is open.
  await expect(page.getByRole('button', { name: 'Browse all areas', exact: true })).toHaveCount(0);
  const row = page.getByTestId('setup-area-content').getByRole('link', { name: /^Talent & employee services/ });
  await row.focus(); await page.keyboard.press('Enter');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Talent & employee services', exact: true })).toBeFocused();
  const disclosure = page.getByRole('button', { name: 'Browse all areas', exact: true });
  await disclosure.focus(); await page.keyboard.press('Enter');
  await expect(disclosure).toHaveAttribute('aria-expanded', 'true');
  const target = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true }).getByRole('button', { name: 'Time & leave', exact: true });
  await target.focus(); await page.keyboard.press('Enter');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { name: 'Time & leave', exact: true })).toBeVisible();
  await expect(disclosure).toHaveAttribute('aria-expanded', 'false');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: `/tmp/setup-studio-${testInfo.project.name}.png`, fullPage: true });
});

test('arabic_rtl_keeps_named_areas_and_scope_readable', async ({ page }, testInfo) => {
  await boot(page, { arabic: true, area: 'onboarding' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByTestId('setup-area-content').getByRole('heading', { level: 2, name: 'المواهب وخدمات الموظفين', exact: true })).toBeVisible();
  // Several settings of the area are tenant-wide, so the scope is read from one named destination (Notifications).
  await expect(page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'الإشعارات' })).toContainText('ينطبق على جميع الشركات');
  await expect(page.getByTestId('setup-area-content').getByRole('navigation', { name: 'مسار التنقل' })).toContainText('كل مجالات الإعداد');
  if (testInfo.project.name === 'desktop') {
    await expect(page.getByRole('complementary', { name: 'في هذا المجال' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'متابعة الإجراء التالي', exact: true })).toHaveCount(0);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: `/tmp/setup-studio-${testInfo.project.name}-ar.png`, fullPage: true });
});

test('group_company_switch_updates_scope_and_next_area_without_reset', async ({ page }) => {
  const { writes } = await boot(page, { group: true });
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await expect(page.getByTestId('setup-studio-workspace').getByText('Second Fixture LLC', { exact: true })).toBeVisible();
  await selectArea(page, 'Time & leave');
  await expect(page).toHaveURL(/companyId=second-company/);
  await expect(page.getByTestId('company-switcher').getByRole('button')).toContainText('second-company');
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: 'Leave requests and balances', exact: true })).toHaveAttribute('href', '/leave?companyId=second-company&setupArea=timeLeave');
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: 'All companies', exact: true }).click();
  await expect(page.getByTestId('setup-studio-workspace').getByText('All authorized companies', { exact: true })).toBeVisible();
  await selectArea(page, 'Payroll & compliance');
  await expect(page).toHaveURL(/area=payrollCompliance/);
  expect(new URL(page.url()).searchParams.has('companyId')).toBe(false);
  expect(writes).toEqual([]);
});

test('history_back_to_absent_company_restores_all_companies_scope', async ({ page }) => {
  const { writes } = await boot(page, { group: true });
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: 'All companies', exact: true }).click();
  await expect(page).not.toHaveURL(/companyId=/);
  await expect(page.getByTestId('setup-studio-workspace').getByText('All authorized companies', { exact: true })).toBeVisible();
  await selectArea(page, 'Time & leave');
  await expect(page).toHaveURL(/area=timeLeave/);
  await page.getByTestId('company-switcher').getByRole('button').click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await expect(page).toHaveURL(/companyId=second-company/);
  await page.goBack();
  await expect(page.locator('#setup-section-heading')).toHaveText('All setup areas');
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: /^Time & leave/ })).toBeVisible();
  await expect(page.getByTestId('setup-studio-workspace').getByText('All authorized companies', { exact: true })).toBeVisible();
  await expect(page.getByTestId('company-switcher').getByRole('button')).toContainText('All companies');
  await expect(page).not.toHaveURL(/companyId=/);
  await selectArea(page, 'Time & leave');
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: 'Leave requests and balances', exact: true })).toHaveAttribute('href', '/leave?setupArea=timeLeave');
  expect(writes).toEqual([]);
});

test('setup_section_history_records_every_tab_shown_even_when_the_server_is_slow', async ({ page }) => {
  const { writes } = await boot(page);
  // Hold every server round trip: a section switch shows its tab at once, so its
  // history entry must not wait for (or be lost behind) a router fetch.
  await page.route(url => url.searchParams.has('_rsc'), async route => {
    await new Promise(resolve => setTimeout(resolve, 1_500));
    await route.fallback();
  });
  const steps = [['Companies', 'companies'], ['Grades', 'grades'], ['Drop-down lists', 'masterData'], ['Grades', 'grades']] as const;
  for (const [label] of steps) {
    await openSetupSection(page, label);
    await expect(page.getByRole('heading', { level: 2, name: label, exact: true })).toBeVisible();
  }
  await expect(page).toHaveURL(/[?&]tab=grades(&|$)/);
  for (const [label, tab] of [...steps].reverse().slice(1)) {
    await page.goBack();
    await expect(page).toHaveURL(new RegExp(`[?&]tab=${tab}(&|$)`));
    await expect(page.getByRole('heading', { level: 2, name: label, exact: true })).toBeVisible();
  }
  await page.goForward();
  await expect(page).toHaveURL(/[?&]tab=grades(&|$)/);
  await expect(page.getByRole('heading', { level: 2, name: 'Grades', exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});

test('whole_destination_row_is_action', async ({ page }) => {
  const { writes } = await boot(page, { area: 'organization' });
  const destination = page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'Companies', exact: true });

  await expect(destination).toBeVisible();
  await expect(destination).toContainText('Group company register');
  await expect(destination).not.toContainText('Requires a selected company');
  await expect(destination).not.toContainText('Applies to Workspace Fixture LLC');
  await expect(page.getByRole('button', { name: 'Open settings', exact: true })).toHaveCount(0);
  await destination.click();

  await expect(page).toHaveURL(/[?&]area=companyStructure(&|$)/);
  await expect(page).toHaveURL(/[?&]setting=companies(&|$)/);
  await expect(page.getByRole('heading', { level: 2, name: 'Companies', exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});

test('single_company_companies_destination_uses_company_register_scope', async ({ page }) => {
  const { writes } = await boot(page, { area: 'organization', intercept: async route => {
    if (new URL(route.request().url()).pathname !== '/api/auth/me') return false;
    await route.fulfill({ json: {
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'],
      permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', 'learning.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'payroll.read', 'performance.read'],
      accountType: 'SingleCompany', isGroupScope: true,
      companies: [{ id: 'fixture-company', name: 'Workspace Fixture LLC', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    } });
    return true;
  } });
  const destination = page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'Companies', exact: true });

  await expect(destination).toContainText('Company register');
  await expect(destination).not.toContainText('Group company register');
  await expect(destination).not.toContainText('Applies to Workspace Fixture LLC');
  expect(writes).toEqual([]);
});

test('focused_form_replaces_area_canvas', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'companies' });

  await expect(page.getByTestId('setup-focused-workspace')).toBeVisible();
  await expect(page.getByTestId('setup-area-blueprint')).toHaveCount(0);
  await expect(page.getByRole('heading', { level: 2, name: 'Companies', exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Create company', exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Apply', exact: true })).toHaveCount(0);
});

test('focused_form_opens_at_heading_without_manual_scroll', async ({ page }) => {
  // Drop-down lists belong to the Workspace area (one taxonomy: the area is the navigation group).
  await boot(page, { area: 'workspace' });
  const destination = page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'Drop-down lists', exact: true });
  await destination.scrollIntoViewIfNeeded();
  await destination.click();

  const heading = page.getByRole('heading', { level: 2, name: 'Drop-down lists', exact: true });
  await expect(heading).toBeFocused();
  const box = await heading.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.y).toBeGreaterThanOrEqual(0);
  expect(box!.y + box!.height).toBeLessThan(page.viewportSize()!.height);
});

test('back_restores_origin_focus_and_scroll', async ({ page }) => {
  await boot(page, { area: 'workspace' });
  const destination = page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'Drop-down lists', exact: true });
  await destination.evaluate((element: HTMLButtonElement) => {
    element.focus();
    window.scrollTo({ top: 480 });
    element.click();
  });

  await expect(page.getByRole('heading', { level: 2, name: 'Drop-down lists', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Back to Workspace', exact: true }).first().click();
  await expect(page.getByRole('heading', { level: 2, name: 'Workspace', exact: true })).toBeVisible();
  await expect(destination).toBeFocused();
  await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBeGreaterThan(300);
});

test('back_after_previous_or_next_restores_current_row_focus_and_recorded_scroll', async ({ page }) => {
  await boot(page, { area: 'organization' });
  for (const adjacent of [
    { control: 'Previous: Locations', destination: 'Locations' },
    { control: 'Next: Departments', destination: 'Departments' },
  ]) {
    // Cost centres sits between Locations and Departments in Company structure, so it has both neighbours.
    const costCentres = page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'Cost centres', exact: true });
    const recordedScroll = await costCentres.evaluate((element: HTMLButtonElement) => {
      window.scrollTo({ top: 420, behavior: 'auto' });
      const scrollY = window.scrollY;
      element.click();
      return scrollY;
    });

    await page.getByRole('button', { name: adjacent.control, exact: true }).first().click();
    await expect(page.getByRole('heading', { level: 2, name: adjacent.destination, exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Back to Company structure', exact: true }).first().click();

    // The persistent Setup sections nav also lists these sections; the restored row is the area blueprint's.
    await expect(page.getByTestId('setup-area-blueprint').getByRole('button', { name: adjacent.destination, exact: true })).toBeFocused();
    await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBe(recordedScroll);
  }
});

test('browser_back_forward_restores_matching_row_focus_and_scroll', async ({ page }) => {
  await boot(page, { area: 'organization' });
  // Cost centres has both neighbours in Company structure; Departments is its Next.
  const costCentres = page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'Cost centres', exact: true });
  const recordedScroll = await costCentres.evaluate((element: HTMLButtonElement) => {
    window.scrollTo({ top: 420, behavior: 'auto' });
    const scrollY = window.scrollY;
    element.click();
    return scrollY;
  });
  await page.getByRole('button', { name: 'Next: Departments', exact: true }).first().click();
  await expect(page.getByRole('heading', { level: 2, name: 'Departments', exact: true })).toBeFocused();

  await page.goBack();
  await expect(page.getByRole('heading', { level: 2, name: 'Cost centres', exact: true })).toBeFocused();
  await page.goBack();
  await expect(page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'Cost centres', exact: true })).toBeFocused();
  await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBe(recordedScroll);

  await page.goForward();
  await expect(page.getByRole('heading', { level: 2, name: 'Cost centres', exact: true })).toBeFocused();
  await page.goForward();
  await expect(page.getByRole('heading', { level: 2, name: 'Departments', exact: true })).toBeFocused();
});

test('previous_next_are_available_above_and_below_long_form', async ({ page }) => {
  // Departments is the last Company structure setting, so a section with both neighbours is Cost centres.
  await boot(page, { area: 'organization', setting: 'costCenters' });

  for (const position of ['top', 'bottom']) {
    const navigation = page.getByTestId(`focused-navigation-${position}`);
    await expect(navigation.getByRole('button', { name: 'Previous: Locations', exact: true })).toBeVisible();
    await expect(navigation.getByRole('button', { name: 'Next: Departments', exact: true })).toBeVisible();
  }
});

test('restricted_unknown_or_disabled_destination_never_mounts_editor', async ({ browser }) => {
  for (const mode of ['restricted', 'unknown', 'disabled'] as const) {
    const context = await browser.newContext();
    const page = await context.newPage();
    const options = mode === 'restricted'
      ? { restricted: true, area: 'onboarding', setting: 'onboardingChecklists' }
      : mode === 'unknown'
        ? { flagError: true, area: 'onboarding', setting: 'onboardingChecklists' }
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

test('permission_loss_in_another_session_revalidates_on_focus_and_stops_reads', async ({ browser, page }) => {
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

  const adminContext = await browser.newContext();
  const adminPage = await adminContext.newPage();
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
  await adminContext.close();
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

// The same guarantees on a module's own Setup section (`/setup?tab=leavePolicies`), the main way in since every module's
// configuration moved into Setup: losing access unmounts the editor, stops its reads, and says why.
test('section_view_permission_loss_on_focus_unmounts_the_editor_and_says_why', async ({ page }) => {
  let allowed = true;
  const { reads } = await boot(page, { section: 'leavePolicies', intercept: async route => {
    if (new URL(route.request().url()).pathname !== '/api/auth/me') return false;
    await route.fulfill({ json: {
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: allowed ? ['Admin'] : [],
      permissions: ['organization.read', 'organization.write', ...(allowed ? ['leave.read'] : [])],
      accountType: 'Group', isGroupScope: true,
      companies: [{ id: 'fixture-company', name: 'Workspace Fixture LLC', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    } });
    return true;
  } });
  const editor = page.locator('[data-module-section="leavePolicies"]');
  await expect(editor.getByRole('button', { name: 'Edit', exact: true })).toBeVisible();
  allowed = false;
  reads.length = 0;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(editor).toHaveCount(0);
  // Setup rollout: a section the person can no longer open lands on their first available section, with a note saying why.
  await expect(page.locator('#setup-section-heading')).toHaveText('Companies');
  await expect(page.getByTestId('setup-section-redirected')).toHaveText('You cannot open Leave policies, so this is the first section available to you.');
  await expect.poll(() => reads.filter(path => path === '/api/auth/me').length).toBe(1);
  expect(reads.filter(path => path.startsWith('/api/leave/'))).toEqual([]);
});

test('section_view_protected_403_revalidates_and_unmounts_the_editor', async ({ page }) => {
  let allowed = true;
  const { reads } = await boot(page, { section: 'leavePolicies', intercept: async route => {
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
  const editor = page.locator('[data-module-section="leavePolicies"]');
  await editor.getByRole('button', { name: 'Edit', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit Leave Policy', exact: true });
  allowed = false;
  reads.length = 0;
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).click();
  await expect(editor).toHaveCount(0);
  await expect(page.locator('#setup-section-heading')).toHaveText('Companies');
  await expect(page.getByTestId('setup-section-redirected')).toHaveText('You cannot open Leave policies, so this is the first section available to you.');
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

test('public_auth_routes_ignore_focus_and_visibility_revalidation', async ({ browser }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  let authReads = 0;
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'stale-public-route-token'));
  await page.route('**/api/auth/me', async route => {
    authReads += 1;
    await route.fulfill({ json: { id: 'unexpected' } });
  });

  for (const route of ['/login', '/welcome']) {
    await page.goto(route);
    await page.evaluate(() => {
      window.dispatchEvent(new Event('focus'));
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await page.waitForTimeout(250);
  }

  expect(authReads).toBe(0);
  await context.close();
});

test('arabic_authorization_unavailable_status_and_retry_are_translated', async ({ page }) => {
  let unavailable = false;
  await boot(page, { arabic: true, area: 'leave', setting: 'leavePolicies', intercept: async route => {
    if (new URL(route.request().url()).pathname !== '/api/auth/me' || !unavailable) return false;
    await route.fulfill({ status: 500, json: { message: 'Identity service unavailable.' } });
    return true;
  } });

  unavailable = true;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));

  const status = page.getByTestId('authorization-revalidation-status');
  await expect(status.getByText('تعذر إعادة التحقق من الصلاحيات.', { exact: true })).toBeVisible();
  await expect(status.getByText('تم الاحتفاظ بتغييراتك غير المحفوظة. أعد المحاولة قبل المتابعة.', { exact: true })).toBeVisible();
  await expect(status.getByRole('button', { name: 'إعادة التحقق من الصلاحيات', exact: true })).toBeVisible();
});

test('transient_background_auth_failure_preserves_dirty_editor_and_blocks_actions_until_success', async ({ page }) => {
  let authState: 'allowed' | 'unavailable' = 'allowed';
  const { writes } = await boot(page, { area: 'leave', setting: 'leavePolicies', intercept: async route => {
    if (new URL(route.request().url()).pathname !== '/api/auth/me') return false;
    if (authState === 'unavailable') {
      await route.fulfill({ status: 500, json: { message: 'Identity service unavailable.' } });
      return true;
    }
    await route.fulfill({ json: {
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'],
      permissions: ['organization.read', 'organization.write', 'leave.read'], accountType: 'Group', isGroupScope: true,
      companies: [{ id: 'fixture-company', name: 'Workspace Fixture LLC', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    } });
    return true;
  } });
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit Leave Policy', exact: true });
  const annualDays = dialog.getByLabel('Annual Days', { exact: true });
  await annualDays.fill('31');

  authState = 'unavailable';
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));

  const status = page.getByTestId('authorization-revalidation-status');
  await expect(status).toContainText('Authorization could not be revalidated');
  await expect(page.getByTestId('setup-focused-workspace')).toBeVisible();
  await expect(dialog).toBeVisible();
  await expect(annualDays).toHaveValue('31');
  await expect(page.getByTestId('setup-authorization-boundary')).toHaveAttribute('inert', '');
  await dialog.getByRole('button', { name: 'Save Changes', exact: true }).click({ force: true });
  await expect(dialog).toBeVisible();
  await expect(annualDays).toHaveValue('31');
  expect(writes.filter(path => path === '/api/leave/policies/existing-policy')).toEqual([]);

  authState = 'allowed';
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(status).toHaveCount(0);
  await expect(page.getByTestId('setup-authorization-boundary')).not.toHaveAttribute('inert', '');
  await expect(annualDays).toHaveValue('31');
});

test('authorization_revalidation_hands_focus_to_status_and_retry_then_restores_the_editor', async ({ page }) => {
  let authState: 'allowed' | 'pending' = 'allowed';
  let releasePending!: () => void;
  const pendingResponse = new Promise<void>(resolve => { releasePending = resolve; });
  await boot(page, { area: 'leave', setting: 'leavePolicies', intercept: async route => {
    if (new URL(route.request().url()).pathname !== '/api/auth/me' || authState === 'allowed') return false;
    await pendingResponse;
    await route.fulfill({ status: 500, json: { message: 'Identity service unavailable.' } });
    return true;
  } });
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  const annualDays = page.getByRole('dialog', { name: 'Edit Leave Policy', exact: true }).getByLabel('Annual Days', { exact: true });
  await annualDays.fill('31');
  await annualDays.focus();

  authState = 'pending';
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  const status = page.getByTestId('authorization-revalidation-status');
  await expect(status).toBeFocused();
  releasePending();
  const retry = status.getByRole('button', { name: 'Retry authorization', exact: true });
  await expect(retry).toBeFocused();

  authState = 'allowed';
  await page.keyboard.press('Enter');
  await expect(status).toHaveCount(0);
  await expect(annualDays).toBeFocused();
  await expect(annualDays).toHaveValue('31');
});

test('a_quick_authorization_check_freezes_the_page_without_flashing_the_banner', async ({ page }) => {
  let delayMe = false;
  await boot(page, { area: 'leave', setting: 'leavePolicies', intercept: async route => {
    if (new URL(route.request().url()).pathname !== '/api/auth/me' || !delayMe) return false;
    // A typical answer: well under the banner delay.
    await new Promise(resolve => setTimeout(resolve, 150));
    return false;
  } });
  // Record every appearance of the banner and every freeze, however brief.
  await page.evaluate(() => {
    const w = window as unknown as { __bannerSeen: boolean; __inertSeen: boolean };
    w.__bannerSeen = false; w.__inertSeen = false;
    new MutationObserver(() => {
      if (document.querySelector('[data-testid="authorization-revalidation-status"]')) w.__bannerSeen = true;
      if (document.querySelector('[data-testid="setup-authorization-boundary"][inert]')) w.__inertSeen = true;
    }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['inert'] });
  });
  delayMe = true;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect.poll(() => page.evaluate(() => (window as unknown as { __inertSeen: boolean }).__inertSeen)).toBe(true);
  await expect(page.getByTestId('setup-authorization-boundary')).not.toHaveAttribute('inert', '');
  await page.waitForTimeout(1000);
  expect(await page.evaluate(() => (window as unknown as { __bannerSeen: boolean }).__bannerSeen)).toBe(false);
});

for (const arabic of [false, true]) {
  test(`a_slow_authorization_check_shows_the_whole_banner_below_the_top_bar${arabic ? '_in_arabic' : ''}`, async ({ page }, info) => {
    let pending = false;
    let release!: () => void;
    const held = new Promise<void>(resolve => { release = resolve; });
    await boot(page, { arabic, area: 'leave', setting: 'leavePolicies', intercept: async route => {
      if (new URL(route.request().url()).pathname !== '/api/auth/me' || !pending) return false;
      await held;
      return false;
    } });
    pending = true;
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    const status = page.getByTestId('authorization-revalidation-status');
    // Frozen at once, explained only once the check is slow.
    await expect(page.getByTestId('setup-authorization-boundary')).toHaveAttribute('inert', '');
    expect(await status.count()).toBe(0);
    await expect(status).toBeVisible();
    const heading = status.getByText(arabic ? 'جارٍ إعادة التحقق من صلاحياتك…' : 'Revalidating your access…', { exact: true });
    await expect(heading).toBeVisible();
    // Nothing is drawn over the heading: the top bar ends above the banner.
    const geometry = await page.evaluate(() => {
      const banner = document.querySelector('[data-testid="authorization-revalidation-status"]')!;
      const title = banner.querySelector('p')!.getBoundingClientRect();
      const header = document.querySelector('header')!.getBoundingClientRect();
      const hit = document.elementFromPoint(title.left + Math.min(20, title.width / 2), title.top + title.height / 2);
      return { titleTop: title.top, headerBottom: header.bottom, onTop: !!hit && banner.contains(hit), overflow: document.documentElement.scrollWidth > window.innerWidth + 1 };
    });
    expect(geometry.titleTop).toBeGreaterThanOrEqual(geometry.headerBottom);
    expect(geometry.onTop).toBe(true);
    expect(geometry.overflow).toBe(false);
    await page.screenshot({ path: info.outputPath(`authorization-banner-${arabic ? 'ar' : 'en'}.png`) });
    release();
    await expect(status).toHaveCount(0);
    await expect(page.getByTestId('setup-authorization-boundary')).not.toHaveAttribute('inert', '');
  });
}

test('background_401_clears_the_established_session', async ({ page }) => {
  let revoke = false;
  await boot(page, { area: 'leave', setting: 'leavePolicies', intercept: async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/me' && revoke) {
      await route.fulfill({ status: 401, json: { message: 'Session revoked.' } });
      return true;
    }
    if (path === '/api/auth/refresh') {
      await route.fulfill({ status: 401, json: { message: 'Session revoked.' } });
      return true;
    }
    return false;
  } });
  revoke = true;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));

  await expect(page).toHaveURL(/\/login/);
});

test('onboarding_checklists_mount_the_tracked_canonical_editor_and_api', async ({ page }) => {
  const { reads } = await boot(page, { area: 'onboarding', setting: 'onboardingChecklists', intercept: async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/recruitment/onboarding/checklists') {
      await route.fulfill({ json: [{
        id: 'new-hire-checklist', code: 'NEW_HIRE', name: 'New hire preparation', description: 'Canonical onboarding template',
        applicableTo: 'All', departmentName: '', isActive: true,
      }] });
      return true;
    }
    if (path === '/api/recruitment/onboarding/checklists/new-hire-checklist/template-tasks') {
      await route.fulfill({ json: [] });
      return true;
    }
    return false;
  } });

  await expect(page.getByTestId('setup-focused-workspace')).toBeVisible();
  await expect(page.getByText('Checklist Setup', { exact: true })).toBeVisible();
  await expect(page.getByPlaceholder('Code', { exact: true })).toBeVisible();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toBeVisible();
  await expect.poll(() => reads.filter(path => path === '/api/recruitment/onboarding/checklists').length).toBeGreaterThan(0);
  await expect.poll(() => reads.filter(path => path === '/api/recruitment/onboarding/checklists/new-hire-checklist/template-tasks').length).toBeGreaterThan(0);
});

for (const locale of [{ name: 'en', arabic: false }, { name: 'ar', arabic: true }]) {
  test(`onboarding_configuration_editor_axe_${locale.name}`, async ({ page }) => {
    await boot(page, { arabic: locale.arabic, area: 'onboarding', setting: 'onboardingChecklists', intercept: async route => {
      const path = new URL(route.request().url()).pathname;
      if (path === '/api/recruitment/onboarding/checklists') {
        await route.fulfill({ json: [{
          id: 'new-hire-checklist', code: 'NEW_HIRE', name: 'New hire preparation', description: 'Canonical onboarding template',
          applicableTo: 'All', departmentName: '', isActive: true,
        }] });
        return true;
      }
      if (path === '/api/recruitment/onboarding/checklists/new-hire-checklist/template-tasks') {
        await route.fulfill({ json: [] });
        return true;
      }
      return false;
    } });

    for (const label of ['Onboarding checklist', 'Checklist code', 'Checklist name', 'Applicable to', 'Department', 'Checklist description', 'Task title', 'Template category', 'Owner', 'Due offset days', 'Order', 'Task description']) {
      await expect(page.getByLabel(label, { exact: true })).toBeVisible();
    }
    const accessibility = await new AxeBuilder({ page }).include('[data-testid="setup-focused-workspace"]').analyze();
    expect(accessibility.violations.filter(violation => violation.impact === 'serious' || violation.impact === 'critical')).toEqual([]);
  });
}

test('tenant_setting_says_applies_to_all_companies', async ({ page }) => {
  // Departments are company-owned since the org catalog became per company; Drop-down lists stay tenant-wide.
  await boot(page, { area: 'workspace', setting: 'masterData' });

  await expect(page.getByTestId('setup-focused-workspace').getByText('Applies to all companies', { exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-focused-workspace').getByText('Company scope', { exact: true })).toHaveCount(0);
  await expect(page.getByTestId('setup-focused-workspace').getByText('Tenant-wide controls', { exact: true })).toHaveCount(0);
});

test('focused_workspace_keeps_all_area_navigator_without_extra_editor', async ({ page }) => {
  await boot(page, { area: 'organization', setting: 'companies' });

  await expect(page.getByRole('button', { name: 'Browse all areas', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Browse all areas', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Browse all setup areas', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create company', exact: true })).toHaveCount(1);
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
    page.getByRole('button', { name: 'Back to Company structure', exact: true }).first(),
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
  // Workspace is the last area of the one taxonomy, so it is the last tab stop before the dialog wraps.
  await expect(dialog.getByRole('button', { name: 'Workspace', exact: true })).toBeFocused();
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
  await dialog.getByRole('button', { name: 'Payroll & compliance', exact: true }).click();

  await expect(page).toHaveURL(/area=payrollCompliance/);
  await expect(page.getByRole('heading', { level: 2, name: 'Payroll & compliance', exact: true })).toBeFocused();
});

test('arabic_uses_the_drop_down_lists_name', async ({ page }) => {
  await boot(page, { arabic: true, area: 'workspace' });

  // The persistent Setup sections nav also lists this setting; the area row is the destination under test.
  await expect(page.getByTestId('setup-area-blueprint').getByRole('button', { name: 'القوائم المنسدلة', exact: true })).toBeVisible();
  await expect(await setupSection(page, 'القوائم المنسدلة', 'أقسام الإعداد')).toBeVisible();
  await expect(page.getByText('Drop-down lists', { exact: true })).toHaveCount(0);
});

test('english_and_arabic_paths_have_no_serious_or_critical_axe_findings', async ({ page }, testInfo) => {
  const arabicPage = await page.context().newPage();
  const arabicErrors: string[] = [];
  arabicPage.on('pageerror', error => arabicErrors.push(error.message));
  arabicPage.on('console', message => { if (message.type() === 'error' && !message.text().includes('Failed to load resource')) arabicErrors.push(message.text()); });
  const english = await boot(page);
  const arabic = await boot(arabicPage, { arabic: true });
  const locales = [
    { locale: 'en', target: page, direction: 'ltr', referenceLists: 'Drop-down lists' },
    { locale: 'ar', target: arabicPage, direction: 'rtl', referenceLists: 'القوائم المنسدلة' },
  ] as const;

  for (const { locale, target, direction, referenceLists } of locales) {
    await target.goto('/setup?tab=aiSetup&path=configuration&companyId=fixture-company&source=retained&area=workspace');
    await expect(target.getByTestId('setup-studio-workspace')).toBeVisible();
    await expect(target.locator('html')).toHaveAttribute('dir', direction);
    await expect(target.getByTestId('setup-area-blueprint').getByRole('button', { name: referenceLists, exact: true })).toBeVisible();
    let result = await new AxeBuilder({ page: target }).include('[data-testid="setup-studio-workspace"]').analyze();
    expect(result.violations.filter(violation => violation.impact === 'serious' || violation.impact === 'critical')).toEqual([]);
    if (process.env.CAPTURE_TASK5_EVIDENCE === '1' && testInfo.project.name === 'desktop') await target.screenshot({ path: task5Evidence(`configuration-${locale}-desktop.png`), fullPage: true });
  }
  expect([...english.writes, ...arabic.writes]).toEqual([]);
  expect(arabicErrors, 'No Arabic application runtime errors').toEqual([]);
  await arabicPage.close();
});

test('mobile_390_and_320_have_no_page_overflow', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const { writes } = await boot(page, { area: 'organization', setting: 'costCenters' });
  for (const width of [390, 320]) {
    await page.setViewportSize({ width, height: 720 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    const navigation = page.getByTestId('focused-navigation-top');
    await expect(navigation.getByRole('button', { name: 'Back to Company structure', exact: true })).toBeVisible();
    await expect(navigation.getByRole('button', { name: 'Next: Departments', exact: true })).toBeVisible();
    const browse = page.getByRole('button', { name: 'Browse all areas', exact: true });
    await browse.scrollIntoViewIfNeeded();
    await expect(browse).toBeInViewport();
    await browse.click();
    const dialog = page.getByRole('dialog', { name: 'Browse all setup areas', exact: true });
    await expect(dialog).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    if (process.env.CAPTURE_TASK5_EVIDENCE === '1' && testInfo.project.name === 'mobile') await page.screenshot({ path: task5Evidence(`configuration-${width}-navigator.png`) });
    await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  }
  expect(writes).toEqual([]);
});

test('rtl_long_labels_keep_navigation_reachable', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 320, height: 720 });
  const { writes } = await boot(page, { arabic: true, area: 'organization', setting: 'costCenters' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');

  const topNavigation = page.getByTestId('focused-navigation-top');
  for (const button of await topNavigation.getByRole('button').all()) {
    await button.scrollIntoViewIfNeeded();
    await expect(button).toBeInViewport();
  }
  const browse = page.getByRole('button', { name: 'استعراض جميع المجالات', exact: true });
  await browse.scrollIntoViewIfNeeded();
  await expect(browse).toBeInViewport();
  await browse.click();
  const dialog = page.getByRole('dialog');
  const longestArea = dialog.getByRole('button', { name: 'المواهب وخدمات الموظفين', exact: true });
  await longestArea.scrollIntoViewIfNeeded();
  await expect(longestArea).toBeInViewport();
  await expect(dialog.getByRole('button', { name: 'إغلاق', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  if (process.env.CAPTURE_TASK5_EVIDENCE === '1' && testInfo.project.name === 'mobile') await page.screenshot({ path: task5Evidence('configuration-rtl-320.png') });
  expect(writes).toEqual([]);
});

test('two_hundred_percent_zoom_keeps_actions_reachable', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 720, height: 900 });
  const { writes } = await boot(page, { area: 'organization', setting: 'costCenters' });

  for (const position of ['top', 'bottom']) {
    const navigation = page.getByTestId(`focused-navigation-${position}`);
    await navigation.scrollIntoViewIfNeeded();
    await expect(navigation.getByRole('button', { name: 'Back to Company structure', exact: true })).toBeInViewport();
    await expect(navigation.getByRole('button', { name: 'Previous: Locations', exact: true })).toBeInViewport();
    await expect(navigation.getByRole('button', { name: 'Next: Departments', exact: true })).toBeInViewport();
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  if (process.env.CAPTURE_TASK5_EVIDENCE === '1' && testInfo.project.name === 'desktop') await page.screenshot({ path: task5Evidence('configuration-200-percent.png'), fullPage: true });
  expect(writes).toEqual([]);
});

test('four_hundred_percent_reflow_has_no_two_axis_scroll', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 320, height: 720 });
  const { writes } = await boot(page, { area: 'organization', setting: 'costCenters' });

  await page.getByTestId('focused-navigation-bottom').scrollIntoViewIfNeeded();
  await expect(page.getByTestId('focused-navigation-bottom').getByRole('button', { name: 'Next: Departments', exact: true })).toBeInViewport();
  const overflow = await page.evaluate(() => ({
    page: document.documentElement.scrollWidth - window.innerWidth,
    twoAxis: [...document.querySelectorAll<HTMLElement>('body *')].filter(element => {
      const style = getComputedStyle(element);
      const xScrollable = /(auto|scroll)/.test(style.overflowX) && element.scrollWidth > element.clientWidth + 1;
      const yScrollable = /(auto|scroll)/.test(style.overflowY) && element.scrollHeight > element.clientHeight + 1;
      return xScrollable && yScrollable;
    }).map(element => element.getAttribute('data-testid') ?? element.tagName),
  }));
  expect(overflow.page).toBeLessThanOrEqual(1);
  expect(overflow.twoAxis).toEqual([]);
  if (process.env.CAPTURE_TASK5_EVIDENCE === '1' && testInfo.project.name === 'desktop') await page.screenshot({ path: task5Evidence('configuration-400-percent.png'), fullPage: true });
  expect(writes).toEqual([]);
});

test('dirty_cancel_preserves_url_values_scroll_and_focus', async ({ page }) => {
  const { writes } = await boot(page, { area: 'onboarding', setting: 'onboardingChecklists' });
  const taskTitle = page.getByLabel('Task title', { exact: true });
  await taskTitle.fill('Preserve this authored onboarding task');
  const back = page.getByTestId('focused-navigation-bottom').getByRole('button', { name: 'Back to Talent & employee services', exact: true });
  await back.scrollIntoViewIfNeeded();
  const before = { url: page.url(), scrollY: await page.evaluate(() => window.scrollY) };
  await back.click();
  const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();

  await expect(page).toHaveURL(before.url);
  await expect(taskTitle).toHaveValue('Preserve this authored onboarding task');
  await expect(back).toBeFocused();
  await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBe(Math.round(before.scrollY));
  expect(writes).toEqual([]);
});

test('dirty_confirm_navigates_once', async ({ page }) => {
  const { writes } = await boot(page, { area: 'onboarding', setting: 'onboardingChecklists' });
  await page.getByLabel('Task title', { exact: true }).fill('Discard this onboarding task once');
  const beforeHistory = await page.evaluate(() => history.length);
  await page.getByTestId('focused-navigation-top').getByRole('button', { name: 'Back to Talent & employee services', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
  await expect(dialog).toHaveCount(1);
  await dialog.getByRole('button', { name: 'Discard and leave', exact: true }).click();

  await expect(page).toHaveURL(/area=talentServices(?!.*setting=)/);
  await expect(page.getByTestId('setup-area-blueprint')).toBeVisible();
  await expect(dialog).toHaveCount(0);
  await page.waitForTimeout(250);
  expect(await page.evaluate(() => history.length)).toBe(beforeHistory + 1);
  expect(writes).toEqual([]);
});
