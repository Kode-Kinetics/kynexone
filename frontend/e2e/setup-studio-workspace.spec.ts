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
});
test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page), 'No application runtime errors').toEqual([]);
});

// API interception is deliberate: these are browser navigation contracts, never tenant writes.
async function boot(page: Page, options: { disabled?: boolean; restricted?: boolean; flagError?: boolean; area?: string; arabic?: boolean; group?: boolean } = {}) {
  const writes: string[] = [];
  const reads: string[] = [];
  await page.addInitScript(({ arabic }) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  }, { arabic: options.arabic });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (request.method() !== 'GET') writes.push(path); else reads.push(path);
    const company = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
    const companies = options.group ? [company, { ...company, id: 'second-company', legalNameEn: 'Second Fixture LLC' }] : [company];
    if (path === '/api/auth/me') return reply({ id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'], permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', ...(options.restricted ? [] : ['learning.read', 'leave.read', 'recruitment.read', 'payroll.read', 'performance.read'])], accountType: 'Group', isGroupScope: true, companies: companies.map(item => ({ id: item.id, name: item.legalNameEn, code: item.id, countryCode: 'SAU', isActive: true })) });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/companies') return reply({ items: companies, total: companies.length, page: 1, pageSize: 100 });
    if (path === '/api/features/disabled-keys') {
      if (options.flagError) return route.fulfill({ status: 503, json: { message: 'Fixture unavailable' } });
      return reply(options.disabled ? ['release_a', 'learning'] : ['release_a']);
    }
    if (path.includes('/features/')) return reply(options.disabled ? [{ key: 'learning', labelEn: 'Learning', enabled: false, navPaths: ['/learning'] }] : []);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(`/setup?tab=aiSetup&companyId=fixture-company&source=retained${options.area ? `&area=${options.area}` : ''}`);
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible({ timeout: 30_000 });
  return { writes, reads };
}

async function selectArea(page: Page, label: string) {
  const disclosure = page.getByRole('button', { name: /^Choose setup area/ });
  if (await disclosure.isVisible()) await disclosure.click();
  await page.getByRole('navigation', { name: 'Setup areas', exact: true }).getByRole('button', { name: label, exact: true }).click();
}

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
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: 'Open workspace', exact: true })).toHaveAttribute('href', '/leave?companyId=second-company');
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
  await expect(page.getByTestId('setup-area-content').getByRole('link', { name: 'Open workspace', exact: true })).toHaveAttribute('href', '/leave');
  expect(writes).toEqual([]);
});
