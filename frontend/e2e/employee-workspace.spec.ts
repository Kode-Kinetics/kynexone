import { devices, expect, test, type Browser, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import fs from 'node:fs';
import path from 'node:path';
import { bootEmployeeWorkspaceFixture } from './employee-workspace.fixture';

const RESPONSIVE_CERTIFICATION_TITLES = new Set([
  'known employee link producers use canonical workspace URLs',
  'workspace has no serious accessibility violations or document overflow at every certified viewport',
  'Arabic workspace keeps RTL direction, logical navigation, and focus',
]);

test.beforeEach(async ({ page }, testInfo) => {
  if (testInfo.project.name !== 'desktop') {
    test.skip(!RESPONSIVE_CERTIFICATION_TITLES.has(testInfo.title), 'Desktop interaction contract is covered by the desktop project; responsive projects run the dedicated geometry, reduced-motion, RTL, and axe certifications.');
    await page.emulateMedia({ reducedMotion: 'reduce' });
  }
});

function readinessForTest() {
  return {
    employeeId: 901,
    state: 'NeedsAttention',
    score: 60,
    progress: { present: 3, requiredTotal: 5 },
    policy: { countryCode: 'SAU', tier: 'Activation', sources: ['Fixture policy'] },
    blocking: [],
    payBlocking: [],
    recommended: [],
    present: [],
    expiringSoon: [],
    disclaimer: 'Fixture readiness data.',
  };
}

async function bootNavigationFixture(page: Page, options: { locale?: 'en' | 'ar'; forcePopstate?: boolean } = {}) {
  await page.addInitScript(({ locale, forcePopstate }) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
    if (forcePopstate) Object.defineProperty(window, 'navigation', { configurable: true, value: undefined });
  }, { locale: options.locale ?? 'en', forcePopstate: options.forcePopstate ?? false });
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
  // Setup canonicalises its URL (serializeSetupPath adds path=) after first render. Wait for that replace,
  // or a history entry a test pushes in the meantime is overwritten by it and the guard assertions race.
  await expect(page).toHaveURL(/[?&]path=configuration(&|$)/);
}

async function makeDirty(page: Page, value = 'Retain navigation draft') {
  const input = page.getByPlaceholder('Task title', { exact: true });
  if (!await input.isVisible()) await page.getByRole('button', { name: 'Checklist Templates', exact: true }).click();
  await input.fill(value);
  return input;
}

function guardDialog(page: Page, name: string | RegExp = 'Discard changes?') {
  return page.getByRole('dialog', { name, exact: typeof name === 'string' });
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
  await expect(page.getByRole('button', { name: 'Open command search', exact: true })).toBeFocused();

  await page.getByTestId('company-switcher').getByRole('button', { name: 'Switch company' }).click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByTestId('company-switcher')).toContainText('fixture-company');
  await expect(page.getByTestId('company-switcher').getByRole('button', { name: 'Switch company' })).toBeFocused();

  await page.getByTestId('company-switcher').getByRole('button', { name: 'Switch company' }).click();
  await page.getByRole('menuitem', { name: /Second Fixture LLC/ }).click();
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByTestId('company-switcher')).toContainText('second-company');

  await makeDirty(page, 'Sign-out draft');

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
  await page.reload();
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible();
  await makeDirty(page, 'Forward guard draft');
  await expect.poll(() => page.evaluate(() => {
    const event = new Event('beforeunload', { cancelable: true });
    return window.dispatchEvent(event);
  })).toBe(false);
  await page.evaluate(() => history.forward());
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page).toHaveURL(originalUrl);
});

test('navigation guard does not prompt for clean navigation', async ({ page }) => {
  await bootNavigationFixture(page);
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Dashboard', exact: true }).click();
  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(guardDialog(page)).toHaveCount(0);
});

test('Setup activation adapter resumes keyboard navigation exactly once', async ({ page }) => {
  await bootNavigationFixture(page);
  await makeDirty(page, 'Adapter replay draft');
  await page.evaluate(() => {
    const original = history.pushState.bind(history);
    (window as Window & { __setupPushCount?: number }).__setupPushCount = 0;
    history.pushState = (...args) => {
      (window as Window & { __setupPushCount?: number }).__setupPushCount! += 1;
      return original(...args);
    };
  });
  const administration = page.getByRole('button', { name: 'Administration settings', exact: true });
  await administration.focus();
  await page.keyboard.press('Enter');
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Adapter replay draft');
  await expect(administration).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByRole('navigation', { name: 'Setup sections' })).toBeVisible();
  await expect(page).toHaveURL(/tab=systemSettings/);
  await expect(guardDialog(page)).toHaveCount(0);
  expect(await page.evaluate(() => (window as Window & { __setupPushCount?: number }).__setupPushCount)).toBe(1);
});

test('localized Arabic guard stays RTL and traps focus while async history confirmation is busy', async ({ page }) => {
  await bootNavigationFixture(page, { locale: 'ar' });
  await page.evaluate(() => history.pushState({ fixture: 1 }, '', `${location.pathname}${location.search}&step=ar`));
  await makeDirty(page, 'مسودة');
  await page.evaluate(() => history.back());

  const dialog = guardDialog(page, 'تجاهل التغييرات؟');
  await expect(dialog).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(dialog.getByRole('button', { name: 'إلغاء', exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'تجاهل ومغادرة', exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'إغلاق', exact: true })).toBeVisible();

  await page.evaluate(() => {
    const navigation = window.navigation;
    if (!navigation) return;
    const original = navigation.traverseTo.bind(navigation);
    (window as Window & { __traverseCount?: number }).__traverseCount = 0;
    Object.defineProperty(navigation, 'traverseTo', { configurable: true, value: (key: string) => {
      (window as Window & { __traverseCount?: number }).__traverseCount! += 1;
      const result = original(key);
      return { ...result, finished: new Promise((resolve, reject) => setTimeout(() => result.finished!.then(resolve, reject), 250)) };
    } });
  });
  await dialog.getByRole('button', { name: 'تجاهل ومغادرة', exact: true }).click();
  await dialog.getByRole('button', { name: 'جارٍ التنفيذ…', exact: true }).dispatchEvent('click');
  await expect(dialog.getByRole('status')).toHaveText('جارٍ التنفيذ…');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Tab');
  await expect(dialog).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await expect(dialog).toBeFocused();
  expect(await page.evaluate(() => (window as Window & { __traverseCount?: number }).__traverseCount)).toBe(1);
});

test('failed async navigation is announced and retry receives focus', async ({ page }) => {
  await bootNavigationFixture(page);
  await page.evaluate(() => history.pushState({ fixture: 1 }, '', `${location.pathname}${location.search}&step=error`));
  await makeDirty(page, 'Error draft');
  await page.evaluate(() => history.back());
  const dialog = guardDialog(page);
  await expect(dialog).toBeVisible();
  await page.evaluate(() => {
    const navigation = window.navigation;
    if (navigation) Object.defineProperty(navigation, 'traverseTo', { configurable: true, value: () => ({
      committed: Promise.reject(new Error('Traversal failed.')),
      finished: Promise.reject(new Error('Traversal failed.')),
    }) });
  });
  await dialog.getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Traversal failed.');
  await expect(dialog.getByRole('button', { name: 'Discard and leave', exact: true })).toBeFocused();
});

test('dirty real anchor and popstate fallback each execute an approved intent once', async ({ page }) => {
  await bootNavigationFixture(page, { forcePopstate: true });
  await makeDirty(page, 'Fallback draft');
  await page.evaluate(() => {
    const anchor = document.createElement('a');
    anchor.href = '/dashboard?from=anchor';
    anchor.textContent = 'Fixture anchor';
    document.body.append(anchor);
  });
  await page.getByRole('link', { name: 'Fixture anchor' }).click();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page).toHaveURL(/\/setup/);

  await page.evaluate(() => history.pushState({ fixture: 1 }, '', `${location.pathname}${location.search}&step=fallback`));
  await page.evaluate(() => history.back());
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(guardDialog(page)).toHaveCount(0);
});

test('mobile touch cancellation restores the persistent navigation opener', async ({ browser }) => {
  const context = await (browser as Browser).newContext({ ...devices['Pixel 7'], reducedMotion: 'reduce' });
  const page = await context.newPage();
  await bootNavigationFixture(page);
  expect(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(true);
  await makeDirty(page, 'Touch draft');
  const opener = page.getByRole('button', { name: 'Open navigation', exact: true });
  await opener.tap();
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Dashboard', exact: true }).tap();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).tap();
  await expect(opener).toBeFocused();
  await context.close();
});

test('Setup activation adapter resumes touch navigation exactly once', async ({ browser }) => {
  const context = await (browser as Browser).newContext({ ...devices['Pixel 7'], reducedMotion: 'reduce' });
  const page = await context.newPage();
  await bootNavigationFixture(page);
  await makeDirty(page, 'Touch adapter draft');
  await page.evaluate(() => {
    const original = history.pushState.bind(history);
    (window as Window & { __setupPushCount?: number }).__setupPushCount = 0;
    history.pushState = (...args) => {
      (window as Window & { __setupPushCount?: number }).__setupPushCount! += 1;
      return original(...args);
    };
  });
  await page.getByRole('button', { name: 'Administration settings', exact: true }).tap();
  await expect(guardDialog(page)).toBeVisible();
  await guardDialog(page).getByRole('button', { name: 'Discard and leave', exact: true }).tap();
  await expect(page).toHaveURL(/tab=systemSettings/);
  expect(await page.evaluate(() => (window as Window & { __setupPushCount?: number }).__setupPushCount)).toBe(1);
  await context.close();
});

test('guard remains topmost over an existing setup dialog and cancel preserves it', async ({ page }) => {
  await bootNavigationFixture(page);
  await page.getByRole('button', { name: 'Administration settings', exact: true }).click();
  await page.getByRole('button', { name: 'New Setting', exact: true }).click();
  const settingDialog = page.getByRole('dialog', { name: 'New Setting', exact: true });
  await expect(settingDialog).toBeVisible();
  await page.evaluate(() => history.back());
  await expect(guardDialog(page)).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(2);
  await guardDialog(page).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(settingDialog).toBeVisible();
});

test('workspace route opens direct and legacy employee URLs without loading the directory first', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);

  await page.goto('/people/901?section=employment');
  await expect(page.getByRole('heading', { name: 'QA Master Data Test 2026-10-17' })).toBeVisible();
  await expect(page).toHaveURL(/\/people\/901\?section=employment$/);
  expect(fixture.listCalls).toBe(0);

  await page.goto('/people?employeeId=901&tab=package&search=QA&page=2');
  await expect(page).toHaveURL(/\/people\/901\?section=pay-benefits&panel=package/);
  await expect(page.getByRole('heading', { name: 'QA Master Data Test 2026-10-17' })).toBeVisible();
  expect(fixture.listCalls).toBe(0);
});

test('workspace route rejects invalid and nonpositive employee IDs before API loading', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);

  await page.goto('/people/not-an-id');
  await expect(page.getByRole('heading', { name: 'Invalid employee link' })).toBeVisible();
  await page.goto('/people/0');
  await expect(page.getByRole('heading', { name: 'Invalid employee link' })).toBeVisible();

  expect([...fixture.detailCalls.values()].reduce((sum, value) => sum + value, 0)).toBe(0);
  expect([...fixture.readinessCalls.values()].reduce((sum, value) => sum + value, 0)).toBe(0);
});

test('workspace loading shows missing and forbidden recovery states with Back to People', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);
  fixture.detailMode.set(901, 'not-found');

  await page.goto('/people/901');
  await expect(page.getByRole('heading', { name: 'Employee record not found' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Back to People' })).toHaveAttribute('href', '/people');

  fixture.detailMode.set(901, 'forbidden');
  await page.getByRole('button', { name: 'Retry employee record' }).click();
  await expect(page.getByRole('heading', { name: 'You do not have access to this employee' })).toBeVisible();
});

test('workspace loading recovers from a network failure without discarding the route', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);
  fixture.detailMode.set(901, 'network');

  await page.goto('/people/901');
  await expect(page.getByRole('heading', { name: 'Employee record could not be reached' })).toBeVisible({ timeout: 15_000 });
  fixture.detailMode.set(901, 'success');
  await page.getByRole('button', { name: 'Retry employee record' }).click();
  await expect(page.getByRole('heading', { name: 'QA Master Data Test 2026-10-17' })).toBeVisible();
  await expect(page).toHaveURL(/\/people\/901$/);
});

test('workspace loading settles detail before readiness and retries readiness independently', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);
  fixture.readinessDelayMs.set(901, 700);
  fixture.readinessMode.set(901, 'server-error');

  await page.goto('/people/901');
  await expect(page.getByRole('heading', { name: 'QA Master Data Test 2026-10-17' })).toBeVisible();
  await expect(page.getByText('Loading readiness…', { exact: true })).toBeVisible();
  await expect(page.getByRole('alert').filter({ hasText: 'Readiness could not be loaded.' })).toBeVisible();

  fixture.readinessMode.set(901, 'success');
  fixture.readinessDelayMs.set(901, 0);
  await page.getByRole('button', { name: 'Retry readiness' }).click();
  await expect(page.getByText('3 of 5 complete', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'QA Master Data Test 2026-10-17' })).toBeVisible();
});

test('workspace loading can settle readiness before detail without rendering a previous identity', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);
  fixture.detailDelayMs.set(901, 700);

  await page.goto('/people/901');
  await expect(page.getByText('Loading employee record…', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'QA Master Data Test 2026-10-17' })).toBeVisible();
  await expect(page.getByText('3 of 5 complete', { exact: true })).toBeVisible();
});

test('workspace stale employee A responses never replace employee B after client navigation', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);
  fixture.detailDelayMs.set(901, 1_200);
  fixture.readinessDelayMs.set(901, 1_200);

  await page.goto('/people/901');
  await page.getByRole('button', { name: 'Open command search', exact: true }).click();
  await page.getByPlaceholder('Search modules, employees, reports…').fill('Second Route');
  await page.getByRole('button', { name: /Second Route Employee/ }).click();

  await expect(page).toHaveURL(/\/people\/902/);
  await expect(page.getByRole('heading', { name: 'Second Route Employee' })).toBeVisible();
  await page.waitForTimeout(1_400);
  await expect(page.getByRole('heading', { name: 'Second Route Employee' })).toBeVisible();
  await expect(page.getByText('QA Master Data Test 2026-10-17', { exact: true })).toHaveCount(0);
});

test('directory is full width and employee names are canonical links with exact return state', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page);

  await page.goto('/people?search=QA&status=Draft&readiness=NeedsAttention&access=not_invited&page=2');
  const employeeLink = page.getByRole('link', { name: 'QA Master Data Test 2026-10-17', exact: true });
  await expect(employeeLink).toBeVisible();
  await expect(page.getByText('Select an employee', { exact: true })).toHaveCount(0);
  await expect(employeeLink).toHaveAttribute('href', /\/people\/901\?.*returnTo=/);

  await employeeLink.focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/people\/901/);
  await page.getByRole('link', { name: 'Back to People' }).click();
  await expect(page).toHaveURL(/\/people\?search=QA&status=Draft&readiness=NeedsAttention&access=not_invited&page=2&anchor=employee-901/);
  await expect(employeeLink).toBeFocused();
});

test('directory filters, view, and page are canonical URL state and clear transient selection', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page);
  await page.goto('/people');
  await expect(page.getByRole('heading', { name: 'Employee Management' })).toBeVisible();

  await page.getByLabel('Select QA Master Data Test 2026-10-17').check();
  await page.getByLabel('Status filter').selectOption('Draft');
  await expect(page).toHaveURL(/status=Draft/);
  await expect(page.getByRole('status').filter({ hasText: 'Selection cleared' })).toBeVisible();

  await page.getByLabel('Readiness filter').selectOption('NeedsAttention');
  await expect(page).toHaveURL(/readiness=NeedsAttention/);
  await page.getByRole('tab', { name: 'Ex-Employees' }).click();
  await expect(page).toHaveURL(/view=ex/);
  await page.getByPlaceholder('Search former employee code, name, email').fill('former');
  await expect(page).toHaveURL(/search=former/);
});

test('Ex-Employees uses canonical search and page state and remains read only', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page);
  await page.goto('/people?view=ex&search=former&page=2');

  await expect(page.getByText('Read-only archive of former staff.', { exact: false })).toBeVisible();
  await expect(page.getByPlaceholder('Search former employee code, name, email')).toHaveValue('former');
  await expect(page.getByText(/Page 2 of/)).toBeVisible();
  await expect(page.getByRole('button', { name: /Add Employee/i })).toHaveCount(0);
});

test('workspace destination mapping renders one addressable record surface and one primary scroller', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page);
  await page.goto('/people/901?section=employment&panel=details');

  await expect(page.getByRole('heading', { name: 'Employment details' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Employee record sections' }).getByRole('link', { name: 'Employment' })).toHaveAttribute('aria-current', 'page');
  await expect(page.locator('[data-employee-workspace-scroll]')).toHaveCount(1);
  await expect(page.getByText('Evostel', { exact: true })).toBeVisible();
  await expect(page.getByText('Jeddah Office', { exact: true })).toBeVisible();
  await expect(page.getByText('QA Finance Manager', { exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Pay & Benefits' }).click();
  await expect(page).toHaveURL(/section=pay-benefits/);
  await expect(page.getByRole('heading', { name: 'Pay & Benefits' })).toBeVisible();
});

test('workspace overview distinguishes blockers from recommendations and presents the next action', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page);
  await page.goto('/people/901?section=overview');

  await expect(page.getByRole('heading', { name: 'Recommended next action' })).toBeVisible();
  await expect(page.getByText('Contract document', { exact: true })).toBeVisible();
  await expect(page.getByText('Blocks activation', { exact: true })).toBeVisible();
  await expect(page.getByText('IBAN', { exact: true })).toBeVisible();
  await expect(page.getByText('Blocks payroll', { exact: true })).toBeVisible();
});

test('route-owned employment editing preserves tenant values and follows organization master data', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page);
  await page.goto('/people/901/edit?section=employment');

  await expect(page.getByRole('heading', { name: 'Review employment details' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Employment type' })).toHaveValue('Seasonal');
  await expect(page.getByRole('combobox', { name: 'Contract type' })).toHaveValue('Unlimited Term');
  await expect(page.getByRole('combobox', { name: 'Department' })).toHaveValue('QA Finance');

  await page.getByRole('combobox', { name: 'Department' }).selectOption({ label: 'QA Operations' });
  await expect(page.getByRole('combobox', { name: 'Designation' })).toHaveValue('QA Operations Lead');
  await expect(page.getByRole('combobox', { name: 'Grade' })).toHaveValue('M1');
  await expect(page.getByLabel('Cost center')).toHaveValue('QA-OPS-CC-18');
  await expect(page.getByRole('combobox', { name: 'Line manager' })).toHaveValue('902');
  await expect(page.getByRole('combobox', { name: 'Line manager' }).locator('option')).toHaveText(['Select', 'Second Route Employee (EMP-SA-QA-OPS-18-2026-0002)']);
});

test('compliance view uses the nationality-resolved identity catalogue', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page);
  await page.goto('/people/901?section=compliance-documents');

  await expect(page.getByRole('heading', { name: 'Compliance & Documents' })).toBeVisible();
  await expect(page.getByText('National ID (Hawiyya)', { exact: true })).toBeVisible();
  await expect(page.getByText('1000000000', { exact: true })).toBeVisible();
  await expect(page.getByText('Iqama number', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Visa number', { exact: true })).toHaveCount(0);
});

test('onboarding readiness distinguishes recommendations from activation blockers', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);
  fixture.readinessBody.set(901, {
    ...readinessForTest(),
    blocking: [],
    payBlocking: [],
    recommended: [
      { key: 'contract', label: 'Contract document', category: 'document', reason: 'recommended' },
      { key: 'iban', label: 'IBAN (WPS)', category: 'payroll', reason: 'recommended' },
    ],
  });
  await page.goto('/people/901?section=access-onboarding');

  await expect(page.getByText('3 of 5 readiness checks are complete.', { exact: true })).toBeVisible();
  await expect(page.getByText('No activation blockers. 2 recommendations remain.', { exact: true })).toBeVisible();
  await expect(page.getByText(/required items are complete/i)).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Activate employee' })).toBeEnabled();
});

test('workspace permission persona never renders sensitive values or unauthorized mutation controls', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page, { permissions: ['employees.read', 'dashboard.read'] });
  await page.goto('/people/901?section=pay-benefits');

  await expect(page.getByText('SA4420000001234567891234', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Sensitive payroll details are restricted.', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: /Edit/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Delete employee/i })).toHaveCount(0);
  await page.getByRole('link', { name: 'Compliance & Documents' }).click();
  await expect(page.getByText('P-SECRET-901', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Upload document/i })).toHaveCount(0);
  expect(fixture.mutationCalls).toEqual([]);
});

test('workspace destination gives a permission-safe fallback for an unavailable panel', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page, { permissions: ['employees.read', 'dashboard.read'], disabledFeatures: ['release_a', 'succession'] });
  await page.goto('/people/901?section=activity&panel=development');

  await expect(page.getByRole('heading', { name: 'Activity' })).toBeVisible();
  await expect(page.getByText('This panel is not available for your current access.', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'View activity overview' })).toHaveAttribute('href', /section=activity/);
});

test('route-owned employee editing reaches Review before sending changed fields', async ({ page }) => {
  const fixture = await bootEmployeeWorkspaceFixture(page);
  await page.goto('/people/901/edit?mode=guided&section=personal');

  const flow = page.getByTestId('employee-edit-flow');
  await expect(flow).toBeVisible();
  await expect(flow.getByRole('navigation', { name: 'Employee setup progress' })).toBeVisible();
  await flow.getByRole('textbox', { name: /^Preferred name/ }).fill('QA Master Updated');
  await flow.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(flow).toContainText('QA Master Updated');
  expect(fixture.mutationCalls).toEqual([]);

  await flow.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(page).toHaveURL(/\/people\/901\?section=personal/);
  expect(fixture.mutations).toEqual([{
    method: 'PUT',
    path: '/api/employees/901',
    body: { effectiveDate: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/), changes: { preferredName: 'QA Master Updated' } },
  }]);
});

test('known employee link producers use canonical workspace URLs', async () => {
  const read = (file: string) => fs.readFileSync(path.join(process.cwd(), file), 'utf8');
  const producers = [
    'src/layouts/AppLayout.tsx',
    'src/views/NewHiresPage.tsx',
    'src/views/PayrollPage.tsx',
    'src/views/CompliancePage.tsx',
    'src/components/succession/KpiTiles.tsx',
  ];
  for (const producer of producers) {
    const source = read(producer);
    expect(source, producer).not.toContain('/people?employeeId=');
    expect(source, producer).toContain('employeeWorkspaceHref');
  }
});

test('workspace has no serious accessibility violations or document overflow at every certified viewport', async ({ page }, testInfo) => {
  await bootEmployeeWorkspaceFixture(page);
  await page.goto('/people/901?section=employment&panel=details');
  await expect(page.getByRole('heading', { name: 'Employment details' })).toBeVisible();

  const geometry = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
    scrollers: document.querySelectorAll('[data-employee-workspace-scroll]').length,
    reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
  }));
  expect(geometry.documentWidth - geometry.viewportWidth).toBeLessThanOrEqual(1);
  expect(geometry.scrollers).toBe(1);
  if (testInfo.project.name !== 'desktop') expect(geometry.reducedMotion).toBe(true);

  const violations = (await new AxeBuilder({ page }).analyze()).violations
    .filter(violation => violation.impact === 'serious' || violation.impact === 'critical');
  expect(violations).toEqual([]);
});

test('Arabic workspace keeps RTL direction, logical navigation, and focus', async ({ page }) => {
  await bootEmployeeWorkspaceFixture(page);
  await page.addInitScript(() => localStorage.setItem('kynexone-locale-choice-v2', 'ar'));
  await page.goto('/people/901?section=employment');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const employment = page.getByRole('navigation', { name: /Employee record sections|أقسام/ }).getByRole('link', { name: /Employment|التوظيف/ });
  await employment.focus();
  await expect(employment).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
});
