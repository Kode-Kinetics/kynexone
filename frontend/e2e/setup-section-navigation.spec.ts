import { expect, test, type Locator, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { openSetupSection, setupSections } from './setupSections';

// Setup & Administration's section navigation: a grouped column on wide screens, a section picker below that.
// Every API call is intercepted; these are navigation and layout contracts and never write tenant data.

const SHOTS = process.env.SETUP_NAV_SHOTS_DIR;

const WRITER_SECTIONS = {
  'Setup tools': ['Setup Studio', 'Import organization', 'Policy library'],
  Organization: ['Companies', 'Branches', 'Departments', 'Cost Centres & Budget', 'Locations'],
  'People & pay': ['Designations', 'Grades', 'Fiscal Years', 'GL & rates', 'GCC Settings', 'Statutory Rules'],
  System: ['Employee Reference Lists', 'Numbering', 'System Settings', 'Notifications', 'Email / SMTP', 'Audit Logs'],
} as const;
const ALL_WRITER_SECTIONS = Object.values(WRITER_SECTIONS).flat();

const errorsByPage = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  errorsByPage.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
});
test.afterEach(async ({ page }) => {
  expect(errorsByPage.get(page), 'No application runtime errors').toEqual([]);
});

async function boot(page: Page, options: { url?: string; arabic?: boolean; dark?: boolean; readOnly?: boolean; companyScoped?: boolean } = {}) {
  const writes: string[] = [];
  await page.addInitScript(({ arabic, dark }) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
    localStorage.setItem('kynexone.theme', dark ? 'dark' : 'light');
  }, { arabic: options.arabic ?? false, dark: options.dark ?? false });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (request.method() !== 'GET') writes.push(path);
    const company = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
    const companies = [company, { ...company, id: 'second-company', legalNameEn: 'Second Fixture LLC' }];
    if (path === '/api/auth/me') return reply({
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer',
      roles: [options.readOnly ? 'Auditor' : 'Admin'],
      permissions: options.readOnly
        ? ['organization.read']
        : ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'payroll.read'],
      accountType: 'Group', isGroupScope: !options.companyScoped,
      companies: companies.map(item => ({ id: item.id, name: item.legalNameEn, code: item.id, countryCode: 'SAU', isActive: true })),
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/country-packs/available') return reply([]);
    if (path === '/api/reference/countries') return reply([{ code: 'SA', name: 'Saudi Arabia' }]);
    if (path === '/api/admin/system-settings' || path === '/api/admin/master-data/types') return reply([]);
    if (path === '/api/finance/gl/accounts' || path === '/api/finance/gl/mappings') return reply([]);
    if (path === '/api/dashboard/full') return route.fulfill({ status: 503, json: { message: 'Dashboard fixture unavailable' } });
    if (path === '/api/recruitment/onboarding/summary') return reply({ total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 });
    if (path === '/api/companies') return reply({ items: companies, total: companies.length, page: 1, pageSize: 100 });
    // policy_intake is on in this fixture (absent from the disabled keys), so the Policy library is a section.
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(options.url ?? '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=overview');
  // Located by its class here so the same wait serves the Arabic boot, whose landmark name is translated.
  await expect(page.locator('nav.setup-page-nav')).toBeVisible({ timeout: 30_000 });
  return { writes };
}

const isWide = (page: Page) => page.viewportSize()!.width >= 1280;
const noPageOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);

async function sectionButtons(page: Page, labels: readonly string[]): Promise<Locator[]> {
  return labels.map(label => setupSections(page).getByRole('button', { name: label, exact: true }));
}

async function shot(page: Page, name: string) {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

test('every permitted section is listed under its group and reachable without sideways scrolling', async ({ page }, info) => {
  const { writes } = await boot(page);
  const nav = setupSections(page);
  if (!isWide(page)) await nav.getByRole('button', { name: /^Setup section/ }).click();
  for (const [group, labels] of Object.entries(WRITER_SECTIONS)) {
    const list = nav.getByRole('list', { name: group, exact: true });
    await expect(list).toBeVisible();
    await expect(list.getByRole('button')).toHaveText([...labels]);
  }
  await expect(nav.getByRole('button', { name: 'Cost centers', exact: true })).toHaveCount(0);
  const navBox = (await nav.boundingBox())!;
  for (const button of await sectionButtons(page, ALL_WRITER_SECTIONS)) {
    await button.scrollIntoViewIfNeeded();
    const box = (await button.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(navBox.x - 1);
    expect(box.x + box.width).toBeLessThanOrEqual(navBox.x + navBox.width + 1);
    // Labels wrap rather than clip.
    expect(await button.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  }
  // Nothing inside the navigation scrolls or clips sideways (a visually hidden caption is 1px wide by design).
  expect(await nav.evaluate(element => Array.from(element.querySelectorAll('*')).every(child => child.clientWidth <= 1 || child.scrollWidth <= child.clientWidth + 1 || getComputedStyle(child).overflowX === 'visible'))).toBe(true);
  expect(await noPageOverflow(page)).toBe(true);
  if (info.project.name === 'desktop') {
    // At 1440x1000 with the app sidebar open, the whole list is on screen at once.
    await page.evaluate(() => window.scrollTo(0, 0));
    for (const button of await sectionButtons(page, ALL_WRITER_SECTIONS)) await expect(button).toBeInViewport({ ratio: 1 });
  }
  const axe = await new AxeBuilder({ page }).include('nav[aria-label="Setup sections"]').analyze();
  expect(axe.violations.filter(violation => violation.impact === 'serious' || violation.impact === 'critical')).toEqual([]);
  expect(writes).toEqual([]);
});

test('the active section is marked, visible, and follows each selection with a history entry', async ({ page }) => {
  const { writes } = await boot(page);
  const nav = setupSections(page);
  if (isWide(page)) {
    await expect(nav.getByRole('button', { name: 'Setup Studio', exact: true })).toHaveAttribute('aria-current', 'page');
    await shot(page, `${page.viewportSize()!.width}-en-studio`);
  }
  await openSetupSection(page, 'Grades');
  await expect(page.getByRole('heading', { level: 2, name: 'Grades', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/[?&]tab=grades(&|$)/);
  await openSetupSection(page, 'Audit Logs');
  await expect(page.getByRole('heading', { level: 2, name: 'Audit Logs', exact: true })).toBeVisible();
  if (isWide(page)) {
    const active = nav.getByRole('button', { name: 'Audit Logs', exact: true });
    await expect(active).toHaveAttribute('aria-current', 'page');
    await expect(nav.locator('[aria-current="page"]')).toHaveCount(1);
    await expect(active).toBeInViewport({ ratio: 1 });
    const background = (label: string) => nav.getByRole('button', { name: label, exact: true }).evaluate(element => getComputedStyle(element).backgroundColor);
    const idle = await background('Grades');
    // Polled: the colour change is a short transition.
    await expect.poll(() => background('Audit Logs')).not.toBe(idle);
    // The column stays with the user on a long section.
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await expect(active).toBeInViewport();
    await page.evaluate(() => window.scrollTo(0, 0));
  } else {
    await expect(nav.getByRole('button', { name: /^Setup section/ })).toContainText('Audit Logs');
  }
  await page.goBack();
  await expect(page).toHaveURL(/[?&]tab=grades(&|$)/);
  await expect(page.getByRole('heading', { level: 2, name: 'Grades', exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});

test('a ?tab= deep link opens its section and marks it, even low in the list', async ({ page }) => {
  await boot(page, { url: '/setup?tab=adminAuditLogs' });
  await expect(page.getByRole('heading', { level: 2, name: 'Audit Logs', exact: true })).toBeVisible();
  const nav = setupSections(page);
  if (isWide(page)) {
    await expect(nav.getByRole('button', { name: 'Audit Logs', exact: true })).toHaveAttribute('aria-current', 'page');
    await expect(nav.getByRole('button', { name: 'Audit Logs', exact: true })).toBeInViewport();
  } else {
    await expect(nav.getByRole('button', { name: /^Setup section/ })).toContainText('Audit Logs');
  }
  // A section without an entry of its own still opens from its link and names itself in the picker.
  await page.goto('/setup?tab=costCenters');
  await expect(page.getByRole('heading', { level: 2, name: 'Cost centers', exact: true })).toBeVisible();
  await expect(nav.locator('[aria-current="page"]')).toHaveCount(0);
  if (!isWide(page)) await expect(nav.getByRole('button', { name: /^Setup section/ })).toContainText('Cost centers');
});

test('switching section with unsaved work asks first, and Cancel keeps the work', async ({ page }) => {
  await boot(page, { url: '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=onboarding&setting=onboarding' });
  const draft = page.getByPlaceholder('Task title', { exact: true });
  await draft.fill('Keep this task');
  const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
  await openSetupSection(page, 'Branches');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(draft).toHaveValue('Keep this task');
  await expect(page).toHaveURL(/[?&]tab=aiSetup(&|$)/);
  await setupSections(page).getByRole('button', { name: 'Branches', exact: true }).click();
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Branches', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/[?&]tab=branches(&|$)/);
});

test('narrow screens use a section picker that opens the same grouped list and closes on choice or Escape', async ({ page }) => {
  test.skip(isWide(page), 'The picker replaces the column below 1280px.');
  await boot(page, { url: '/setup?tab=companies' });
  const nav = setupSections(page);
  const picker = nav.getByRole('button', { name: /^Setup section/ });
  await expect(picker).toBeVisible();
  await expect(picker).toContainText('Companies');
  await expect(picker).toHaveAttribute('aria-expanded', 'false');
  await expect(nav.getByRole('button', { name: 'Grades', exact: true })).toBeHidden();
  await shot(page, `${page.viewportSize()!.width}-en-picker-closed`);
  await picker.click();
  await expect(picker).toHaveAttribute('aria-expanded', 'true');
  for (const button of await sectionButtons(page, ALL_WRITER_SECTIONS)) await expect(button).toBeVisible();
  expect(await noPageOverflow(page)).toBe(true);
  await shot(page, `${page.viewportSize()!.width}-en-picker-open`);
  await page.keyboard.press('Escape');
  await expect(picker).toHaveAttribute('aria-expanded', 'false');
  await expect(picker).toBeFocused();
  await picker.click();
  await nav.getByRole('button', { name: 'Grades', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Grades', exact: true })).toBeVisible();
  await expect(picker).toHaveAttribute('aria-expanded', 'false');
  await expect(picker).toContainText('Grades');
});

test('Arabic mirrors the section navigation and keeps every label readable', async ({ page }) => {
  await boot(page, { arabic: true, url: '/setup?tab=grades' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const nav = setupSections(page).or(page.getByRole('navigation', { name: 'أقسام الإعداد', exact: true }));
  const arabicNav = page.getByRole('navigation', { name: 'أقسام الإعداد', exact: true });
  await expect(arabicNav).toBeVisible();
  if (!isWide(page)) await arabicNav.getByRole('button', { name: /^قسم الإعداد/ }).click();
  for (const group of ['أدوات الإعداد', 'المنظمة', 'الأفراد والرواتب', 'النظام']) await expect(arabicNav.getByRole('list', { name: group, exact: true })).toBeVisible();
  const active = arabicNav.getByRole('button', { name: 'الدرجات', exact: true });
  await expect(active).toHaveAttribute('aria-current', 'page');
  for (const button of await arabicNav.getByRole('listitem').getByRole('button').all()) {
    expect(await button.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  }
  if (isWide(page)) {
    // The column sits on the inline-start (right) side, beside the content.
    const navBox = (await nav.first().boundingBox())!;
    const contentBox = (await page.locator('#setup-setting-content').boundingBox())!;
    expect(navBox.x).toBeGreaterThan(contentBox.x + contentBox.width - 1);
  }
  expect(await noPageOverflow(page)).toBe(true);
  await shot(page, `${page.viewportSize()!.width}-ar-grades${isWide(page) ? '' : '-picker-open'}`);
});

test('dark mode keeps the navigation legible', async ({ page }) => {
  await boot(page, { dark: true, url: '/setup?tab=masterData' });
  await expect(page.locator('html')).toHaveClass(/(^|\s)dark(\s|$)/);
  if (!isWide(page)) await setupSections(page).getByRole('button', { name: /^Setup section/ }).click();
  const axe = await new AxeBuilder({ page }).include('nav[aria-label="Setup sections"]').withRules(['color-contrast']).analyze();
  expect(axe.violations).toEqual([]);
  await shot(page, `${page.viewportSize()!.width}-dark-reference-lists`);
});

test('a read-only viewer sees only the sections their role can open', async ({ page }) => {
  await boot(page, { readOnly: true, url: '/setup?tab=aiSetup' });
  const nav = setupSections(page);
  if (!isWide(page)) await nav.getByRole('button', { name: /^Setup section/ }).click();
  await expect(nav.getByRole('listitem')).toHaveCount(1);
  await expect(nav.getByRole('button', { name: 'Companies', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(nav.getByRole('list', { name: 'Setup tools', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { level: 2, name: 'Companies', exact: true })).toBeVisible();
});

test('a company-scoped group administrator sees only company-owned settings', async ({ page }) => {
  await boot(page, { companyScoped: true, url: '/setup?tab=companies' });
  const nav = setupSections(page);
  if (!isWide(page)) await nav.getByRole('button', { name: /^Setup section/ }).click();
  await expect(nav.getByRole('listitem').getByRole('button')).toHaveText(['Setup Studio', 'Policy library', 'Companies', 'Branches', 'GL & rates', 'Audit Logs']);
});

test('the Policy starter keeps the full width and folds the sections into the picker', async ({ page }) => {
  await boot(page, { url: '/setup?tab=aiSetup&path=policyStarter&companyId=fixture-company' });
  const nav = setupSections(page);
  const picker = nav.locator('button[aria-expanded]');
  await expect(picker).toBeVisible();
  await expect(picker).toContainText('Setup Studio');
  await expect(nav.getByRole('button', { name: 'Grades', exact: true })).toBeHidden();
  const navBox = (await nav.boundingBox())!;
  const starterBox = (await page.locator('#setup-aiSetup').boundingBox())!;
  // Stacked above the starter, not beside it.
  expect(starterBox.y).toBeGreaterThanOrEqual(navBox.y + navBox.height - 1);
  expect(Math.abs(starterBox.width - navBox.width)).toBeLessThanOrEqual(2);
  await openSetupSection(page, 'Grades');
  await expect(page.getByRole('heading', { level: 2, name: 'Grades', exact: true })).toBeVisible();
  await shot(page, `${page.viewportSize()!.width}-en-grades-after-starter`);
});
