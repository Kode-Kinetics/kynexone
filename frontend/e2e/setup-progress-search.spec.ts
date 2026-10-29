import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { openSetupSection, setupSection, setupSectionEntries, setupSectionsToggle } from './setupSections';

// Wave 2 of the Setup design review (owner-approved 2026-10-22): setup progress from persisted data, and "Find a
// setting" in Setup's section column and the ⌘K palette. Every API call is intercepted; nothing is written.

const SHOTS = process.env.SETUP_W2_SHOTS_DIR;

// One fixture company with Guided setup's checklist: 9 items, 4 done (the server computes both the count on the
// chooser and the list from the same function; the fixture keeps them consistent the same way).
const READINESS = [
  { key: 'glMapping', label: 'GL posting accounts', labelAr: 'حسابات الترحيل المحاسبي', status: 'done', scope: 'company', href: '/setup?tab=glMapping&companyId=fixture-company' },
  { key: 'establishment', label: 'Approved positions and budget', labelAr: 'الوظائف المعتمدة والميزانية', status: 'recommended', scope: 'company', href: '/setup?tab=establishment&companyId=fixture-company' },
  { key: 'locations', label: 'Work locations and geofences', labelAr: 'مواقع العمل والنطاقات الجغرافية', status: 'done', scope: 'company', href: '/setup?tab=locations&companyId=fixture-company' },
  { key: 'gccSettings', label: 'Country settings (end of service, weekend)', labelAr: 'إعدادات الدولة (نهاية الخدمة، العطلة الأسبوعية)', status: 'todo', scope: 'company', href: '/setup?tab=gccSettings&companyId=fixture-company' },
  { key: 'statutoryRules', label: 'Statutory rules for the country', labelAr: 'الأنظمة النظامية للدولة', status: 'done', scope: 'company', href: '/setup?tab=statutoryRules&companyId=fixture-company' },
  { key: 'bankWps', label: 'Bank and WPS settings (before the first payment)', labelAr: 'إعدادات البنك وحماية الأجور (قبل أول دفعة)', status: 'todo', scope: 'company', href: '/payroll?companyId=fixture-company' },
  { key: 'nitaqat', label: 'Nitaqat establishment profile', labelAr: 'ملف المنشأة في نطاقات', status: 'recommended', scope: 'company', href: '/saudi-compliance?companyId=fixture-company' },
  { key: 'companyAdmin', label: 'Company super admin', labelAr: 'مسؤول الشركة الأعلى', status: 'done', scope: 'company', href: '/user-management?companyId=fixture-company' },
  { key: 'masterData', label: 'Employee reference lists (applies to all companies)', labelAr: 'القوائم المرجعية للموظفين (ينطبق على جميع الشركات)', status: 'todo', scope: 'workspace', href: '/setup?tab=masterData&companyId=fixture-company' },
] as const;
const DONE = READINESS.filter(item => item.status === 'done').length;
const TOTAL = READINESS.length;

const errorsByPage = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  errorsByPage.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
});
test.afterEach(async ({ page }) => {
  expect(errorsByPage.get(page), 'No application runtime errors').toEqual([]);
});

interface BootOptions { url?: string; arabic?: boolean; dark?: boolean; readOnly?: boolean; complete?: boolean; readinessFails?: boolean; dirtyGuard?: boolean; expectWizard?: boolean; permissions?: string[] }

// Saving the first drop-down list configures the workspace: like the server, Guided setup then stops listing it.
const SAVED_LIST = { id: 'type-1', code: 'NATIONALITY', nameEn: 'Nationality', nameAr: '', description: '', allowCustomValues: false, isActive: true, isSystemDefined: false };

async function boot(page: Page, options: BootOptions = {}) {
  const writes: string[] = [];
  const saved = { list: false };
  await page.addInitScript(({ arabic, dark }) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
    localStorage.setItem('kynexone.theme', dark ? 'dark' : 'light');
  }, { arabic: options.arabic ?? false, dark: options.dark ?? false });
  const checklist = () => (options.complete ? READINESS.map(item => ({ ...item, status: 'done' })) : READINESS)
    .filter(item => !(saved.list && item.key === 'masterData'));
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (request.method() !== 'GET') writes.push(path);
    if (request.method() === 'POST' && path === '/api/admin/master-data/types') { saved.list = true; return reply(SAVED_LIST); }
    if (path === '/api/admin/master-data/types' && saved.list) return reply([SAVED_LIST]);
    const readiness = checklist();
    const company = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
    const companies = [company, { ...company, id: 'second-company', legalNameEn: 'Second Fixture LLC' }];
    if (path === '/api/auth/me') return reply({
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer',
      roles: [options.readOnly ? 'Auditor' : 'Admin'],
      permissions: options.permissions ?? (options.readOnly
        ? ['organization.read']
        : ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'payroll.read', 'payroll.rates.read']),
      accountType: 'Group', isGroupScope: true,
      companies: companies.map(item => ({ id: item.id, name: item.legalNameEn, code: item.id, countryCode: 'SA', isActive: true })),
    });
    if (path === '/api/company-setup/context') return reply({
      canCreate: true, creationMode: 'GroupSelfServiceWithinLimit', accountType: 'Group', companiesUsed: 2, companiesAllowed: 5, isGroupScope: true,
      sisterCompanies: [], sharedCatalog: { departments: [], designations: [], grades: [] },
      manageableCompanies: [
        // Counted from the same checklist, as the server does.
        { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', legalNameAr: null, countryCode: 'SA', readinessDone: readiness.filter(item => item.status === 'done').length, readinessTotal: readiness.length, openDraftId: null },
        { id: 'second-company', legalNameEn: 'Second Fixture LLC', legalNameAr: null, countryCode: 'SA', readinessDone: 9, readinessTotal: 9, openDraftId: null },
      ],
    });
    if (path === '/api/company-setup/readiness/fixture-company') {
      if (options.readinessFails) return route.fulfill({ status: 500, json: { message: 'fixture failure' } });
      return reply(readiness);
    }
    if (path === '/api/company-setup/drafts') return reply([]);
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/country-packs/available') return reply([]);
    if (path === '/api/reference/countries') return reply([{ code: 'SA', name: 'Saudi Arabia' }]);
    if (path === '/api/admin/system-settings' || path === '/api/admin/master-data/types') return reply([]);
    // One active branch, and an inactive one that must not count.
    if (path === '/api/branches') return reply({ items: [
      { id: 'b1', companyId: 'fixture-company', code: 'HQ', nameEn: 'Riyadh HQ', isHeadOffice: true, isActive: true },
      { id: 'b2', companyId: 'fixture-company', code: 'OLD', nameEn: 'Closed branch', isHeadOffice: false, isActive: false },
    ], total: 2, page: 1, pageSize: 100 });
    if (path === '/api/statutory-rules' || path === '/api/finance/rates/statutory/pending') return reply([]);
    if (['/api/admin/numbering-rules', '/api/admin/gcc-settings', '/api/admin/fiscal-years', '/api/admin/locations', '/api/admin/notification-templates'].includes(path)) return reply([]);
    if (['/api/finance/gl/accounts', '/api/finance/gl/mappings', '/api/finance/gl/drivers', '/api/finance/rates/company', '/api/finance/rates/company/registry', '/api/finance/rates/statutory', '/api/finance/gl/periods'].includes(path)) return reply([]);
    if (path === '/api/dashboard/full') return route.fulfill({ status: 503, json: { message: 'Dashboard fixture unavailable' } });
    if (path === '/api/recruitment/onboarding/summary') return reply({ total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 });
    if (path === '/api/companies') return reply({ items: companies, total: companies.length, page: 1, pageSize: 100 });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    if (path === '/api/employees' || path === '/api/reports/catalog' || path.startsWith('/api/access/users')) return reply({ items: [], total: 0, page: 1, pageSize: 5 });
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(options.url ?? '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=overview');
  if (options.expectWizard) return { writes };
  await expect(page.locator('nav.setup-page-nav')).toBeAttached({ timeout: 30_000 });
  await expect(page.locator('#setup-section-heading')).toBeVisible({ timeout: 30_000 });
  return { writes };
}

const isWide = (page: Page) => page.viewportSize()!.width >= 1280;
const viewportName = (page: Page) => `${page.viewportSize()!.width}x${page.viewportSize()!.height}`;
const noPageOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
const sectionHeading = (page: Page) => page.locator('#setup-section-heading');
const areaRow = (page: Page, label: string) => page.getByTestId('setup-area-content').getByRole('link', { name: new RegExp(`^${label}`) });

async function shot(page: Page, name: string) {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

/** The "Find a setting" box, with the folded list (or phone sheet) opened first below 1280px. */
async function searchBox(page: Page, name = 'Find a setting') {
  const box = page.getByRole('textbox', { name, exact: true });
  if (!(await box.isVisible())) {
    const toggle = setupSectionsToggle(page);
    if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
  }
  await expect(box).toBeVisible();
  return box;
}
const results = (page: Page) => page.getByTestId('setup-search-results');

test('the progress line shows Guided setup’s number for the selected company and opens the items behind it', async ({ page }) => {
  const { writes } = await boot(page);
  const summary = page.getByTestId('setup-progress-summary');
  await expect(summary).toHaveText(`Workspace Fixture LLC · ${DONE} of ${TOTAL} setup items done`);
  await expect(summary).toHaveAttribute('aria-expanded', 'false');
  await shot(page, `${viewportName(page)}-en-all-setup-areas`);
  await summary.click();
  await expect(summary).toHaveAttribute('aria-expanded', 'true');
  const missing = page.getByRole('list', { name: 'Still to do', exact: true });
  await expect(missing).toBeVisible();
  // Exactly the items that are not done: total minus done.
  await expect(missing.getByRole('listitem')).toHaveCount(TOTAL - DONE);
  await expect(missing).toContainText('Country settings (end of service, weekend)');
  await expect(missing).toContainText('Bank and WPS settings (before the first payment)');
  await expect(missing).not.toContainText('GL posting accounts');
  await expect(page.getByTestId('setup-progress-items')).toContainText('The same checklist as Guided setup');
  await shot(page, `${viewportName(page)}-en-progress-items`);
  // Each item drills down to the screen that finishes it.
  await page.getByTestId('setup-progress-item-gccSettings').getByRole('link', { name: /Set it up/ }).click();
  await expect(page).toHaveURL(/[?&]tab=gccSettings(&|$)/);
  await expect(sectionHeading(page)).toHaveText('Country rules');
  expect(writes).toEqual([]);
});

test('the numbers reconcile with Guided setup and the navigation for the same fixture', async ({ page }) => {
  await boot(page);
  await expect(page.getByTestId('setup-progress-summary')).toContainText(`${DONE} of ${TOTAL} setup items done`);
  const guided = await setupSection(page, 'Guided setup');
  // The navigation's quiet count is the same number, and its accessible description says what it counts.
  await expect(guided.locator('[data-setup-entry-status]')).toHaveText(`${DONE} of ${TOTAL}`);
  await expect(guided).toHaveAccessibleDescription(`Workspace Fixture LLC: ${DONE} of ${TOTAL} setup items done`);
  await guided.click();
  await expect(sectionHeading(page)).toHaveText('Guided setup');
  await expect((await setupSection(page, 'Guided setup')).locator('[data-setup-entry-status]')).toHaveText(`${DONE} of ${TOTAL}`);
  const row = page.getByTestId('chooser-company').filter({ hasText: 'Workspace Fixture LLC' });
  await expect(row.getByTestId('chooser-readiness')).toHaveText(`${DONE} of ${TOTAL} done`);
  if (isWide(page)) await shot(page, `${viewportName(page)}-en-guided-count`);
});

test('with a company selected, the navigation’s Guided setup shows the chooser and the scope survives the round trip', async ({ page }) => {
  // The second company is the selected scope (a deep link to All setup areas selects it).
  await boot(page, { url: '/setup?tab=aiSetup&path=configuration&companyId=second-company&area=overview' });
  await expect(page.getByTestId('setup-progress-summary')).toHaveText(`Second Fixture LLC · ${TOTAL} of ${TOTAL} setup items done`);
  await (await setupSection(page, 'Guided setup')).click();
  await expect(sectionHeading(page)).toHaveText('Guided setup');
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
  await expect(page.getByTestId('chooser-company')).toHaveCount(2);
  // The address still carries the scope, and Setup did not jump into that company's wizard.
  await expect(page).toHaveURL(/[?&]path=guided(&|$)/);
  await expect(page).toHaveURL(/[?&]companyId=second-company(&|$)/);
  await expect(page).not.toHaveURL(/\/complete/);
  // A reload of that address is not a deep link either.
  await page.reload();
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
  await expect(page).not.toHaveURL(/\/complete/);
  // Nor is coming Back to it from another section.
  await openSetupSection(page, 'Grades');
  await expect(sectionHeading(page)).toHaveText('Grades');
  await page.goBack();
  await expect(sectionHeading(page)).toHaveText('Guided setup');
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
  await expect(page).not.toHaveURL(/\/complete/);
  // Back to All setup areas: the same company scope.
  await openSetupSection(page, 'All setup areas');
  await expect(sectionHeading(page)).toHaveText('All setup areas');
  await expect(page).toHaveURL(/[?&]companyId=second-company(&|$)/);
  await expect(page.getByTestId('setup-progress-summary')).toHaveText(`Second Fixture LLC · ${TOTAL} of ${TOTAL} setup items done`);
  if (isWide(page)) await shot(page, `${viewportName(page)}-en-guided-from-nav-scope-kept`);
});

test('Back from another screen to Guided setup shows the chooser, not a company wizard', async ({ page }) => {
  await boot(page, { url: '/setup?tab=aiSetup&path=configuration&companyId=second-company&area=overview' });
  await (await setupSection(page, 'Guided setup')).click();
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
  await expect(page).toHaveURL(/[?&]companyId=second-company(&|$)/);
  // Leave for another screen inside the app: a client-side route change, no document load.
  await page.evaluate(() => { (window as unknown as { sameDocument: boolean }).sameDocument = true; });
  await page.keyboard.press('ControlOrMeta+k');
  await page.getByPlaceholder('Search modules, employees, reports…').fill('Dashboard');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/dashboard/);
  for (let round = 0; round < 2; round++) {
    await page.goBack();
    await expect(sectionHeading(page)).toHaveText('Guided setup');
    await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
    await expect(page).not.toHaveURL(/\/complete/);
    expect(await page.evaluate(() => (window as unknown as { sameDocument?: boolean }).sameDocument)).toBe(true);
    if (round === 0) { await page.goForward(); await expect(page).toHaveURL(/\/dashboard/); }
  }
  // The scope is still the same company.
  await openSetupSection(page, 'All setup areas');
  await expect(page.getByTestId('setup-progress-summary')).toHaveText(`Second Fixture LLC · ${TOTAL} of ${TOTAL} setup items done`);
});

test('a record saved in an embedded setting is counted by the headline, its checklist and the navigation together', async ({ page }) => {
  const { writes } = await boot(page, { url: '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=workspace&setting=masterData' });
  const summary = page.getByTestId('setup-progress-summary');
  await expect(summary).toHaveText(`Workspace Fixture LLC · ${DONE} of ${TOTAL} setup items done`);
  // Save the workspace's first drop-down list in the embedded setting.
  await page.getByRole('region', { name: 'No drop-down lists yet' }).getByRole('button', { name: 'New list', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New type' });
  await dialog.getByPlaceholder('NATIONALITY').fill('NATIONALITY');
  await dialog.locator('input').nth(1).fill('Nationality');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes).toContain('/api/admin/master-data/types');
  // Back to the area: Guided setup no longer lists the workspace item, and every number says so.
  await page.getByTestId('setup-studio-workspace').getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Workspace', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Workspace', exact: true })).toBeVisible();
  await expect(summary).toHaveText(`Workspace Fixture LLC · ${DONE} of ${TOTAL - 1} setup items done`);
  await summary.click();
  const missing = page.getByRole('list', { name: 'Still to do', exact: true });
  await expect(missing.getByRole('listitem')).toHaveCount(TOTAL - 1 - DONE);
  await expect(page.getByTestId('setup-progress-item-masterData')).toHaveCount(0);
  // The navigation's count agrees with the headline.
  const guided = await setupSection(page, 'Guided setup');
  await expect(guided.locator('[data-setup-entry-status]')).toHaveText(`${DONE} of ${TOTAL - 1}`);
  if (!isWide(page)) await page.keyboard.press('Escape');
  // And the area counts the saved list as an active record.
  await expect(page.getByTestId('setup-area-blueprint').locator('[data-setup-destination="masterData"]')).toHaveAccessibleDescription('Done');
});

test('Payroll accounting tab results open Payroll accounting on that tab', async ({ page }) => {
  await boot(page, { url: '/setup?tab=companies', permissions: ['organization.read', 'organization.write', 'payroll.read', 'payroll.rates.read', 'finance.gl.read', 'dashboard.read'] });
  for (const [query, tabName, id] of [['company rates', 'Company Rates', 'companyRates'], ['statutory rates', 'Statutory Rates', 'statutory'], ['accounting periods', 'Accounting periods', 'periods']] as const) {
    const box = await searchBox(page);
    await box.fill(query);
    await results(page).getByRole('link', { name: new RegExp(`^${tabName}`) }).click();
    await expect(sectionHeading(page)).toHaveText('Payroll accounting');
    await expect(page).toHaveURL(new RegExp(`[?&]glTab=${id}(&|$)`));
    await expect(page.getByRole('tab', { name: tabName, exact: true })).toHaveAttribute('aria-selected', 'true');
  }
  // The tab request stays with its link: the next section's address does not carry it.
  await openSetupSection(page, 'Grades');
  await expect(sectionHeading(page)).toHaveText('Grades');
  expect(new URL(page.url()).searchParams.has('glTab')).toBe(false);
});

test('a Payroll accounting tab the reader may not see falls back to the first one they may', async ({ page }) => {
  // No rates permission: Company Rates is not a tab for this reader.
  await boot(page, { url: '/setup?tab=glMapping&glTab=companyRates', permissions: ['organization.read', 'organization.write', 'payroll.read', 'finance.gl.read', 'dashboard.read'] });
  await expect(sectionHeading(page)).toHaveText('Payroll accounting');
  await expect(page.getByRole('tab', { name: 'Company Rates', exact: true })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'Accounts & Mapping', exact: true })).toHaveAttribute('aria-selected', 'true');
  // An unknown tab falls back the same way.
  await page.goto('/setup?tab=glMapping&glTab=nonsense');
  await expect(page.getByRole('tab', { name: 'Accounts & Mapping', exact: true })).toHaveAttribute('aria-selected', 'true');
  // And search never offers the hidden tab.
  const box = await searchBox(page);
  await box.fill('company rates');
  await expect(results(page).getByRole('link', { name: /^Company Rates/ })).toHaveCount(0);
});

test('a link from outside Setup that names a company still opens its wizard', async ({ page }) => {
  await boot(page, { url: '/setup?tab=aiSetup&path=guided&companyId=fixture-company', expectWizard: true });
  await expect(page).toHaveURL(/\/setup\/company\/fixture-company\/complete/);
  // The old studio's bookmark form too.
  await page.goto('/setup?tab=aiSetup&mode=starter&companyId=fixture-company');
  await expect(page).toHaveURL(/\/setup\/company\/fixture-company\/complete/);
});

test('the navigation count is hidden once the checklist is done', async ({ page }) => {
  await boot(page, { complete: true });
  await expect(page.getByTestId('setup-progress-summary')).toHaveText(`Workspace Fixture LLC · ${TOTAL} of ${TOTAL} setup items done`);
  const guided = await setupSection(page, 'Guided setup');
  await expect(guided.locator('[data-setup-entry-status]')).toHaveCount(0);
  await expect(guided).toHaveAccessibleDescription('');
  // Below 1280px the helper opened the folded list; close it before using the page behind it.
  if (!isWide(page)) await page.keyboard.press('Escape');
  await page.getByTestId('setup-progress-summary').click();
  await expect(page.getByTestId('setup-progress-items')).toContainText('Every Guided setup item is done for this company.');
});

test('area statuses count only what can be checked, and an area with nothing to check shows none', async ({ page }) => {
  await boot(page);
  await expect(page.getByTestId('setup-area-status-definition')).toContainText('Done means at least one active record is saved');
  const status = (label: string) => areaRow(page, label).getByTestId('setup-area-status');
  // Company structure: companies and branches have active records (the inactive branch does not count), Guided setup's
  // locations item is done; cost centres and departments have none.
  await expect(status('Company structure')).toContainText('3 of 5');
  await expect(status('Company structure')).toHaveAttribute('data-state', 'partial');
  // Jobs & headcount: grades and designations have none; headcount & budget is a Guided setup item not done.
  await expect(status('Jobs & headcount')).toContainText('Not started');
  // Pay & benefits: bank and WPS is not done.
  await expect(status('Pay & benefits')).toHaveAttribute('data-state', 'notStarted');
  // Payroll & compliance: GL and statutory rules done (Guided setup); fiscal years and country rules not (Saudi compliance
  // is not open to this role, so not counted).
  await expect(status('Payroll & compliance')).toContainText('2 of 4');
  // Notifications have no record and no Guided setup item: not started. Workspace: the drop-down lists item is not done.
  await expect(status('Talent & employee services')).toHaveAttribute('data-state', 'notStarted');
  await expect(status('Talent & employee services')).toContainText('Not started');
  await expect(status('Workspace')).toContainText('Not started');
  // Nothing measurable: no status, not a made-up one.
  for (const label of ['Time & leave', 'Users & access']) {
    await expect(status(label), label).toHaveCount(0);
  }
  // Every status has words for assistive tech, not just a colour.
  await expect(areaRow(page, 'Payroll & compliance')).toHaveAccessibleName(/2 of 4 checked items set up/);
  await expect(areaRow(page, 'Jobs & headcount')).toHaveAccessibleName(/none of 3 checked items/);
  expect(await noPageOverflow(page)).toBe(true);
  // The area status drills down: each measurable row of the area says Done or Not started.
  await areaRow(page, 'Company structure').click();
  const blueprint = page.getByTestId('setup-area-blueprint');
  await expect(blueprint.getByTestId('setup-destination-status')).toHaveCount(5);
  await expect(blueprint.locator('[data-setup-destination="branches"]')).toHaveAccessibleDescription('Done');
  await expect(blueprint.locator('[data-setup-destination="costCenters"]')).toHaveAccessibleDescription('Not started');
  await expect(blueprint.locator('[data-setup-destination="people"]')).toHaveCount(0);
});

test('a checklist that cannot be read is never guessed', async ({ page }) => {
  await boot(page, { readinessFails: true });
  // The number itself comes from Guided setup's context, and is still shown; the list says it could not load.
  await page.getByTestId('setup-progress-summary').click();
  await expect(page.getByTestId('setup-progress-items')).toContainText('The checklist could not be loaded here.');
  // Items only Guided setup can answer drop out; a section with its own record check falls back to it. Payroll keeps
  // only fiscal years and country rules' record checks (none saved); Company structure falls back to the locations list.
  await expect(areaRow(page, 'Payroll & compliance').getByTestId('setup-area-status')).toContainText('Not started');
  await expect(areaRow(page, 'Payroll & compliance')).toHaveAccessibleName(/none of 2 checked items/);
  await expect(areaRow(page, 'Company structure').getByTestId('setup-area-status')).toBeVisible();
  // Time & leave has nothing measurable, readable or not: it shows no status.
  await expect(areaRow(page, 'Time & leave').getByTestId('setup-area-status')).toHaveCount(0);
});

test('the rail starts with the area’s next step as its one primary action', async ({ page }) => {
  test.skip(!isWide(page), 'The "In this area" rail is shown from 1280px.');
  await boot(page, { url: '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=companyStructure' });
  const rail = page.getByRole('complementary', { name: 'In this area' });
  const next = rail.getByTestId('setup-next-action');
  await expect(next).toContainText('Cost centres');
  await expect(rail.locator('.btn-primary')).toHaveCount(1);
  const action = rail.getByRole('button', { name: 'Add your first cost centre', exact: true });
  await expect(action).toHaveClass(/btn-primary/);
  // First in the rail.
  const [nextBox, otherBox] = await Promise.all([next.boundingBox(), rail.getByRole('button', { name: /^Companies/ }).boundingBox()]);
  expect(nextBox!.y).toBeLessThan(otherBox!.y);
  await shot(page, `${viewportName(page)}-en-rail-next-step`);
  await action.click();
  await expect(page.getByRole('heading', { level: 2, name: 'Cost centres', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/[?&]setting=costCenters(&|$)/);
  // On the setting itself, the next step is the following incomplete one.
  await expect(rail.getByRole('button', { name: 'Add your first department', exact: true })).toBeVisible();
  // An area with nothing measurable claims neither.
  await page.goto('/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=timeLeave');
  await expect(rail.getByRole('button', { name: /^Leave types/ })).toBeVisible();
  await expect(rail.getByTestId('setup-next-action')).toHaveCount(0);
  await expect(rail.getByTestId('setup-area-all-done')).toHaveCount(0);
});

test('Find a setting matches names, descriptions and synonyms, and opens a result', async ({ page }) => {
  const { writes } = await boot(page, { url: '/setup?tab=companies' });
  const box = await searchBox(page);
  await expect(box).toHaveAttribute('placeholder', 'Search settings');
  expect(Math.round((await box.boundingBox())!.height)).toBe(36);
  await box.fill('GRADES');
  const list = results(page);
  await expect(list.getByRole('link').first()).toContainText('Grades');
  await expect(list.getByRole('link').first()).toContainText('Jobs & headcount');
  // The groups give way to the results.
  await expect(setupSectionEntries(page).getByRole('list', { name: 'Company structure', exact: true })).toBeHidden();
  await shot(page, `${viewportName(page)}-en-search-grades`);
  const expectFound = async (query: string, labels: string[]) => {
    await box.fill(query);
    for (const label of labels) await expect(list.getByRole('link', { name: new RegExp(`^${label}`) }).first(), `${query} → ${label}`).toBeVisible();
  };
  await expectFound('gosi', ['Statutory rules', 'Payroll accounting']);
  await expectFound('SANED', ['Statutory rules', 'Payroll accounting']);
  await expectFound('GPSSA', ['Statutory rules', 'Payroll accounting']);
  await expectFound('grsia', ['Statutory rules', 'Payroll accounting']);
  await expectFound('cost center', ['Cost centres']);
  await expectFound('picklist', ['Drop-down lists']);
  await expectFound('dropdown', ['Drop-down lists']);
  await expectFound('smtp', ['Email delivery']);
  await expectFound('numbers', ['Record numbering']);
  await expectFound('sequence', ['Record numbering']);
  await expectFound('work week', ['Country rules']);
  await expectFound('Ramadan', ['Country rules']);
  await expectFound('WPS', ['Country rules']);
  // Diacritics are ignored.
  await expectFound('Câlendär', ['Holiday calendars']);
  // Modules' settings are Setup sections, captioned with their navigation group; Payroll accounting tabs keep theirs.
  await box.fill('leave types');
  await expect(list.getByRole('link', { name: /^Leave types/ })).toContainText('Time & leave');
  await box.fill('company rates');
  await expect(list.getByRole('link', { name: /^Company Rates/ })).toContainText('Payroll accounting');
  // Opening a result goes to its section and clears the search.
  await box.fill('smtp');
  await list.getByRole('link', { name: /^Email delivery/ }).click();
  await expect(sectionHeading(page)).toHaveText('Email delivery');
  await expect(page).toHaveURL(/[?&]tab=emailConfig(&|$)/);
  if (isWide(page)) {
    await expect(box).toHaveValue('');
    await expect(setupSectionEntries(page).getByRole('link', { name: 'Email delivery', exact: true })).toHaveAttribute('aria-current', 'page');
  }
  expect(await noPageOverflow(page)).toBe(true);
  expect(writes).toEqual([]);
});

test('Find a setting works from the keyboard: arrows move, Enter opens, Escape clears', async ({ page }) => {
  await boot(page, { url: '/setup?tab=companies' });
  const box = await searchBox(page);
  await box.focus();
  await page.keyboard.type('gosi');
  const links = results(page).getByRole('link');
  await expect(links.first()).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await expect(links.nth(0)).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(links.nth(1)).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await expect(links.nth(0)).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await expect(box).toBeFocused();
  // Escape clears the query and keeps focus in the box; the groups come back.
  await page.keyboard.press('Escape');
  await expect(box).toHaveValue('');
  await expect(box).toBeFocused();
  await expect(results(page)).toHaveCount(0);
  // Enter opens the best match.
  await page.keyboard.type('record numbering');
  await expect(links.first()).toContainText('Record numbering');
  await page.keyboard.press('Enter');
  await expect(sectionHeading(page)).toHaveText('Record numbering');
  // A focused result opens with Enter too.
  const again = await searchBox(page);
  await again.fill('grades');
  await again.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(sectionHeading(page)).toHaveText('Grades');
});

test('no match says so and offers All setup areas', async ({ page }) => {
  await boot(page, { url: '/setup?tab=companies' });
  const box = await searchBox(page);
  await box.fill('zzqx');
  const empty = page.getByTestId('setup-search-empty');
  await expect(empty).toContainText("No setting matches 'zzqx'");
  await shot(page, `${viewportName(page)}-en-search-empty`);
  await empty.getByRole('link', { name: 'Browse All setup areas', exact: true }).click();
  await expect(sectionHeading(page)).toHaveText('All setup areas');
});

test('opening a result with unsaved work asks first, and Cancel keeps the work', async ({ page }) => {
  await boot(page, { url: '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=talentServices&setting=onboardingChecklists' });
  const draft = page.getByPlaceholder('Task title', { exact: true });
  await draft.fill('Keep this task');
  const box = await searchBox(page);
  await box.fill('branches');
  const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
  await results(page).getByRole('link', { name: /^Branches/ }).click();
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(draft).toHaveValue('Keep this task');
  await expect(page).toHaveURL(/[?&]tab=aiSetup(&|$)/);
  await (await searchBox(page)).fill('branches');
  await results(page).getByRole('link', { name: /^Branches/ }).click();
  await dialog.getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(sectionHeading(page)).toHaveText('Branches');
});

test('Arabic: the progress line, statuses and search read in Arabic, and English names are still found', async ({ page }) => {
  await boot(page, { arabic: true });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByTestId('setup-progress-summary')).toHaveText(`Workspace Fixture LLC · اكتمل ${DONE} من ${TOTAL} من بنود الإعداد`);
  await expect(areaRow(page, 'الرواتب والامتثال').getByTestId('setup-area-status')).toContainText('2 من 4');
  await expect(areaRow(page, 'التوظيف').getByTestId('setup-area-status')).toHaveCount(0);
  expect(await noPageOverflow(page)).toBe(true);
  await shot(page, `${viewportName(page)}-ar-all-setup-areas`);
  const box = await searchBox(page, 'ابحث عن إعداد');
  await expect(box).toHaveAttribute('placeholder', 'ابحث في الإعدادات');
  await box.fill('الدَّرَجات');
  await expect(results(page).getByRole('link').first()).toContainText('الدرجات');
  await expect(results(page).getByRole('link').first()).toContainText('الوظائف والقوى العاملة');
  // English typed in the Arabic interface still finds the section, shown in Arabic.
  await box.fill('grades');
  await expect(results(page).getByRole('link').first()).toContainText('الدرجات');
  await box.fill('gosi');
  await expect(results(page).getByRole('link', { name: /^القواعد النظامية/ })).toBeVisible();
  await expect(results(page).getByRole('link', { name: /^محاسبة الرواتب/ })).toBeVisible();
  // Arabic captions: no uppercase or letter-spacing, at 13px.
  const caption = await results(page).getByRole('link').first().locator('span').nth(1).evaluate(element => { const css = getComputedStyle(element); return { size: css.fontSize, spacing: css.letterSpacing, transform: css.textTransform }; });
  expect(caption).toEqual({ size: '13px', spacing: 'normal', transform: 'none' });
  await shot(page, `${viewportName(page)}-ar-search`);
  await box.fill('zzqx');
  await expect(page.getByTestId('setup-search-empty')).toContainText('لا يوجد إعداد يطابق «zzqx»');
});

test('dark mode keeps progress and search legible', async ({ page }) => {
  await boot(page, { dark: true });
  await expect(page.locator('html')).toHaveClass(/(^|\s)dark(\s|$)/);
  await page.getByTestId('setup-progress-summary').click();
  const box = await searchBox(page);
  await box.fill('gosi');
  await shot(page, `${viewportName(page)}-dark-search`);
  const axe = await new AxeBuilder({ page }).include('[data-testid="setup-studio-workspace"]').include('[data-testid="setup-search-results"]').withRules(['color-contrast']).analyze();
  expect(axe.violations).toEqual([]);
  await box.fill('');
  if (isWide(page)) await shot(page, `${viewportName(page)}-dark-all-setup-areas`);
});

test('the ⌘K palette lists matching settings under Settings and opens one', async ({ page }) => {
  await boot(page, { url: '/setup?tab=companies' });
  await page.keyboard.press('ControlOrMeta+k');
  const input = page.getByPlaceholder('Search modules, employees, reports…');
  await expect(input).toBeVisible();
  await input.fill('smtp');
  const settings = page.getByTestId('command-palette-settings');
  await expect(settings).toBeVisible();
  await expect(settings.getByRole('group', { name: 'Settings' })).toBeVisible();
  await expect(settings.getByRole('button', { name: /Email delivery/ })).toContainText('Workspace');
  await input.fill('gosi');
  await expect(settings.getByRole('button', { name: /^Statutory rules/ })).toBeVisible();
  await expect(settings.getByRole('button', { name: /^Payroll accounting/ })).toBeVisible();
  await shot(page, `${viewportName(page)}-en-palette-settings`);
  await input.fill('picklist');
  await expect(settings.getByRole('button')).toHaveCount(1);
  // The palette's own arrows reach the Settings entry; Enter opens it.
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/[?&]tab=masterData(&|$)/);
  await expect(sectionHeading(page)).toHaveText('Drop-down lists');
});

test('a read-only viewer never finds a section their role cannot open, in Setup or in ⌘K', async ({ page }) => {
  const { writes } = await boot(page, { readOnly: true, url: '/setup?tab=companies' });
  const box = await searchBox(page);
  for (const query of ['grades', 'gosi', 'smtp', 'leave types', 'guided setup', 'company rates']) {
    await box.fill(query);
    await expect(page.getByTestId('setup-search-empty'), query).toBeVisible();
    // All setup areas is not theirs, so it is not offered either.
    await expect(page.getByTestId('setup-search-empty').getByRole('link')).toHaveCount(0);
  }
  await box.fill('companies');
  await expect(results(page).getByRole('link')).toHaveCount(1);
  await expect(results(page).getByRole('link').first()).toContainText('Companies');
  // No progress for a reader: Guided setup is not theirs and its context is never asked for.
  await expect(page.getByTestId('setup-progress-summary')).toHaveCount(0);
  await box.fill('');
  if (!isWide(page)) await page.keyboard.press('Escape');
  await page.keyboard.press('ControlOrMeta+k');
  const input = page.getByPlaceholder('Search modules, employees, reports…');
  await input.fill('grades');
  await expect(page.getByTestId('command-palette-settings')).toHaveCount(0);
  await input.fill('gosi');
  await expect(page.getByTestId('command-palette-settings')).toHaveCount(0);
  await input.fill('companies');
  await expect(page.getByTestId('command-palette-settings').getByRole('button')).toHaveCount(1);
  expect(writes).toEqual([]);
});

test('on a phone the search sits at the top of the bottom sheet', async ({ page }) => {
  test.skip(page.viewportSize()!.width >= 640, 'The bottom sheet is the phone layout.');
  await boot(page, { url: '/setup?tab=grades' });
  await setupSectionsToggle(page).click();
  const sheet = page.getByRole('dialog', { name: 'Setup sections', exact: true });
  await expect(sheet).toBeVisible();
  // Focus still starts on the current entry (wave 1), and the box is the first thing under the sheet's header.
  await expect(sheet.getByRole('link', { name: 'Grades', exact: true })).toBeFocused();
  const box = sheet.getByRole('textbox', { name: 'Find a setting', exact: true });
  const [boxRect, firstGroup] = await Promise.all([box.boundingBox(), sheet.getByText('Get started', { exact: true }).boundingBox()]);
  expect(boxRect!.y).toBeLessThan(firstGroup!.y);
  await box.fill('smtp');
  await shot(page, `${viewportName(page)}-en-sheet-search`);
  await sheet.getByRole('link', { name: /^Email delivery/ }).click();
  await expect(sheet).toBeHidden();
  await expect(sectionHeading(page)).toHaveText('Email delivery');
  expect(await noPageOverflow(page)).toBe(true);
});
