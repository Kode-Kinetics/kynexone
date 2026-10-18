import { devices, expect, test, type Browser, type Page } from '@playwright/test';

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
