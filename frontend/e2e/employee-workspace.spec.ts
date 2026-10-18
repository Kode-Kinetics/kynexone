import { expect, test, type Page } from '@playwright/test';

async function bootNavigationFixture(page: Page) {
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  });
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    const companies = [
      { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' },
      { id: 'second-company', legalNameEn: 'Second Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' },
    ];
    if (path === '/api/auth/me') return reply({
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'],
      permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', 'recruitment.read', 'recruitment.write'],
      accountType: 'Group', isGroupScope: true,
      companies: companies.map(company => ({ id: company.id, name: company.legalNameEn, code: company.id, countryCode: 'SAU', isActive: true })),
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/companies') return reply({ items: companies, total: companies.length, page: 1, pageSize: 100 });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/dashboard/full') return route.fulfill({ status: 503, json: { message: 'Dashboard fixture unavailable' } });
    if (path === '/api/recruitment/onboarding/summary') return reply({ total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 });
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/admin/system-settings' || path === '/api/country-packs/available') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/setup?tab=aiSetup&companyId=fixture-company&source=retained&area=onboarding&setting=onboarding');
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible({ timeout: 30_000 });
}

async function makeDirty(page: Page, value = 'Retain navigation draft') {
  const input = page.getByPlaceholder('Task title', { exact: true });
  if (!await input.isVisible()) await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await input.fill(value);
  return input;
}

function guardDialog(page: Page) {
  return page.getByRole('dialog', { name: 'Confirm', exact: true });
}

test('navigation guard cancels a dirty sidebar link, restores focus, and confirms it once', async ({ page }) => {
  await bootNavigationFixture(page);
  const input = await makeDirty(page);
  const originalUrl = page.url();
  const dashboard = page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Dashboard', exact: true });
  await dashboard.focus();
  await dashboard.click();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page).toHaveURL(originalUrl);
  await expect(input).toHaveValue('Retain navigation draft');
  await expect(dashboard).toBeFocused();

  await dashboard.click();
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page).toHaveURL(/\/dashboard$/);
  expect((await page.evaluate(() => history.length))).toBeGreaterThan(1);
});

test('navigation guard covers command search, company switching, and sign-out', async ({ page }) => {
  await bootNavigationFixture(page);
  await makeDirty(page, 'Shared guard draft');

  await page.getByRole('button', { name: 'Open command search', exact: true }).click();
  await page.getByPlaceholder('Search modules, employees, reports…').fill('dashboard');
  await page.getByRole('button', { name: /Dashboard/ }).last().click();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page).toHaveURL(/\/setup/);

  await page.getByTestId('company-switcher').getByRole('button', { name: 'Switch company' }).click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByTestId('company-switcher')).toContainText('fixture-company');

  await page.getByRole('button', { name: 'Open user menu', exact: true }).click();
  await page.getByText('Sign out', { exact: true }).click();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/);
});

test('navigation guard restores dirty Back and Forward and registers native unload protection', async ({ page }) => {
  await bootNavigationFixture(page);
  await page.evaluate(() => history.pushState({ fixture: 1 }, '', `${location.pathname}${location.search}&step=one`));
  await page.evaluate(() => history.pushState({ fixture: 2 }, '', `${location.pathname}${location.search.replace('&step=one', '')}&step=two`));
  await makeDirty(page, 'History guard draft');
  const originalUrl = page.url();

  expect(await page.evaluate(() => {
    const event = new Event('beforeunload', { cancelable: true });
    return window.dispatchEvent(event);
  })).toBe(false);

  await page.evaluate(() => history.back());
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('History guard draft');

  // Chromium ignores a second traversal issued in the same task as the restoring traversal.
  await page.waitForTimeout(100);
  await page.evaluate(() => history.back());
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page).not.toHaveURL(originalUrl);
  await page.evaluate(() => history.forward());
  await expect(page).toHaveURL(originalUrl);
});

test('navigation guard does not prompt for clean navigation', async ({ page }) => {
  await bootNavigationFixture(page);
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Dashboard', exact: true }).click();
  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(guardDialog(page)).toHaveCount(0);
});

test('navigation guard resumes the existing Setup confirmLeave caller once', async ({ page }) => {
  await bootNavigationFixture(page);
  await makeDirty(page, 'Adapter replay draft');
  await page.getByRole('button', { name: 'Administration settings', exact: true }).click();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByRole('navigation', { name: 'Setup sections' })).toBeVisible();
  await expect(page).toHaveURL(/tab=systemSettings/);
  await expect(guardDialog(page)).toHaveCount(0);
});
