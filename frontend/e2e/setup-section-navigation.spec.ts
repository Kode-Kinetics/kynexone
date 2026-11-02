import { expect, test, type Locator, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { openSetupSection, setupSection, setupSectionEntries, setupSections, setupSectionsToggle } from './setupSections';

// Setup & Administration's section navigation: a grouped column of links on wide screens; below 1280px the list folds
// under the section header's "All sections" button (capped inline from 640px, a bottom sheet on a phone).
// Every API call is intercepted; these are navigation and layout contracts and never write tenant data.

const SHOTS = process.env.SETUP_NAV_SHOTS_DIR;

// Wave 1 (2026-10-22): groups, order and sentence-case names approved by the Integration Owner.
const WRITER_SECTIONS = {
  'Get started': ['Guided setup', 'All setup areas', 'Import organization', 'Policy library'],
  'Company structure': ['Companies', 'Branches', 'Locations', 'Cost centres', 'Departments'],
  'Jobs & headcount': ['Grades', 'Designations', 'Headcount & budget'],
  // 2026-10-24: every module's configuration is a Setup section; this fixture's administrator holds leave, recruitment
  // and payroll read, so those modules' settings are listed in their groups (lib/setupModuleSections.ts).
  'Time & leave': ['Leave types', 'Leave policies', 'Holiday calendars'],
  'Pay & benefits': ['Payslip templates'],
  // Notifications sits with the employee services (one area taxonomy, 2026-10-26), ahead of the module sections.
  'Talent & employee services': ['Notifications', 'Onboarding checklists'],
  'Payroll & compliance': ['Country rules', 'Statutory rules', 'Fiscal years', 'Payroll accounting', 'Tax policies'],
  Workspace: ['Drop-down lists', 'Record numbering', 'Email delivery', 'Workspace settings', 'Audit log'],
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
        : ['organization.read', 'organization.write', 'organization.costcenter.manage', 'organization.setup.apply', 'ai.query', 'dashboard.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'payroll.read'],
      accountType: 'Group', isGroupScope: !options.companyScoped,
      companies: companies.map(item => ({ id: item.id, name: item.legalNameEn, code: item.id, countryCode: 'SAU', isActive: true })),
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/country-packs/available') return reply([]);
    if (path === '/api/reference/countries') return reply([{ code: 'SA', name: 'Saudi Arabia' }]);
    if (path === '/api/admin/system-settings' || path === '/api/admin/master-data/types') return reply([]);
    if (path === '/api/branches' || path.startsWith('/api/branches/')) return reply({ items: [], total: 0, page: 1, pageSize: 100 });
    // These admin lists are plain arrays in the API.
    if (path === '/api/statutory-rules' || path === '/api/finance/rates/statutory/pending') return reply([]);
    if (['/api/admin/numbering-rules', '/api/admin/gcc-settings', '/api/admin/fiscal-years', '/api/admin/locations', '/api/admin/notification-templates'].includes(path)) return reply([]);
    if (path === '/api/finance/gl/accounts' || path === '/api/finance/gl/mappings') return reply([]);
    if (path === '/api/dashboard/full') return route.fulfill({ status: 503, json: { message: 'Dashboard fixture unavailable' } });
    if (path === '/api/recruitment/onboarding/summary') return reply({ total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 });
    if (path === '/api/companies') return reply({ items: companies, total: companies.length, page: 1, pageSize: 100 });
    // Modules' settings listed for this administrator (lib/setupModuleSections.ts): their lists are plain arrays.
    if (path === '/api/leave/types' || path.startsWith('/api/leave/holidays/') || path === '/api/payslip-templates' || path === '/api/company-tax-policies') return reply([]);
    // policy_intake is on in this fixture (absent from the disabled keys), so the Policy library is a section.
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(options.url ?? '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=overview');
  // Located by its class here so the same wait serves the Arabic boot, whose landmark name is translated.
  await expect(page.locator('nav.setup-page-nav')).toBeAttached({ timeout: 30_000 });
  await expect(page.locator('#setup-section-heading')).toBeVisible({ timeout: 30_000 });
  return { writes };
}

const isWide = (page: Page) => page.viewportSize()!.width >= 1280;
const isPhone = (page: Page) => page.viewportSize()!.width < 640;
const noPageOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
const sectionHeading = (page: Page) => page.locator('#setup-section-heading');

async function sectionLinks(page: Page, labels: readonly string[]): Promise<Locator[]> {
  return labels.map(label => setupSectionEntries(page).getByRole('link', { name: label, exact: true }));
}

/** Opens the folded list when the viewport folds it; the wide column needs nothing. */
async function showList(page: Page) {
  if (isWide(page)) return;
  const toggle = setupSectionsToggle(page);
  if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
}

async function shot(page: Page, name: string) {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

test('every permitted section is listed under its group and reachable without sideways scrolling', async ({ page }, info) => {
  const { writes } = await boot(page);
  const nav = setupSectionEntries(page);
  await showList(page);
  for (const [group, labels] of Object.entries(WRITER_SECTIONS)) {
    const list = nav.getByRole('list', { name: group, exact: true });
    await expect(list).toBeVisible();
    await expect(list.getByRole('link')).toHaveText([...labels]);
  }
  // The old names are gone, and Cost centres is an entry of its own now.
  for (const old of ['Setup Studio', 'Cost Centres & Budget', 'Cost centers', 'Employee Reference Lists', 'GCC Settings', 'GL & rates', 'Email / SMTP', 'System Settings', 'Audit Logs', 'Numbering']) {
    await expect(nav.getByRole('link', { name: old, exact: true })).toHaveCount(0);
  }
  const listBox = (await page.locator('#setup-section-list').boundingBox())!;
  const touchRows = await page.evaluate(() => !matchMedia('(min-width: 1024px) and (max-height: 900px) and (pointer: fine)').matches);
  for (const link of await sectionLinks(page, ALL_WRITER_SECTIONS)) {
    await link.scrollIntoViewIfNeeded();
    const box = (await link.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(listBox.x - 1);
    expect(box.x + box.width).toBeLessThanOrEqual(listBox.x + listBox.width + 1);
    // Labels wrap rather than clip.
    expect(await link.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    // Below 1280px the rows are touch targets (a short screen with a mouse keeps the compact rows).
    if (!isWide(page) && touchRows) expect(box.height).toBeGreaterThanOrEqual(44);
  }
  // Nothing inside the navigation scrolls or clips sideways (a visually hidden caption is 1px wide by design).
  expect(await page.locator('nav.setup-page-nav, #setup-section-list').evaluateAll(roots => roots.every(root => Array.from(root.querySelectorAll('*')).every(child => child.clientWidth <= 1 || child.scrollWidth <= child.clientWidth + 1 || getComputedStyle(child).overflowX === 'visible')))).toBe(true);
  expect(await noPageOverflow(page)).toBe(true);
  if (info.project.name === 'desktop') {
    // 2026-10-24: with every module's settings in Setup the list is longer than a screen, so (as in BambooHR's or
    // Salesforce's settings) the column scrolls on its own beside the section: each entry is brought fully on screen by
    // scrolling the column, with the page itself at the top, and "Find a setting" stays the quick way in.
    await page.evaluate(() => window.scrollTo(0, 0));
    for (const link of await sectionLinks(page, ALL_WRITER_SECTIONS)) {
      await link.scrollIntoViewIfNeeded();
      await expect(link).toBeInViewport({ ratio: 1 });
      expect(await page.evaluate(() => window.scrollY), 'reached by scrolling the column, not the page').toBe(0);
    }
  }
  if (isWide(page)) {
    // The column is not a card: no background, border or padding, only an inline-end divider.
    const style = await page.locator('#setup-section-list').evaluate(element => {
      const css = getComputedStyle(element);
      return { background: css.backgroundColor, borderTop: css.borderTopWidth, borderEnd: css.borderInlineEndWidth, paddingTop: css.paddingTop };
    });
    expect(style).toEqual({ background: 'rgba(0, 0, 0, 0)', borderTop: '0px', borderEnd: '1px', paddingTop: '0px' });
  }
  const axe = await new AxeBuilder({ page }).include('nav[aria-label="Setup sections"]').analyze();
  expect(axe.violations.filter(violation => violation.impact === 'serious' || violation.impact === 'critical')).toEqual([]);
  expect(writes).toEqual([]);
});

test('every entry opens a section whose header carries the same name', async ({ page }) => {
  test.skip(!isWide(page), 'One pass at the column widths is enough: the folded list renders the same entries.');
  await boot(page, { url: '/setup?tab=companies' });
  for (const label of ALL_WRITER_SECTIONS) {
    await openSetupSection(page, label);
    await expect(sectionHeading(page)).toHaveText(label);
    await expect(await setupSection(page, label)).toHaveAttribute('aria-current', 'page');
    await expect(page.getByRole('heading', { level: 2, name: label, exact: true }).first()).toBeVisible();
  }
});

test('the active section is marked, visible, and follows each selection with a history entry', async ({ page }) => {
  const { writes } = await boot(page);
  const nav = setupSectionEntries(page);
  await expect(sectionHeading(page)).toHaveText('All setup areas');
  if (isWide(page)) {
    await expect(nav.getByRole('link', { name: 'All setup areas', exact: true })).toHaveAttribute('aria-current', 'page');
    await shot(page, `${page.viewportSize()!.width}x${page.viewportSize()!.height}-en-all-setup-areas`);
  }
  await openSetupSection(page, 'Grades');
  await expect(page.getByRole('heading', { level: 2, name: 'Grades', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/[?&]tab=grades(&|$)/);
  await openSetupSection(page, 'Audit log');
  await expect(page.getByRole('heading', { level: 2, name: 'Audit log', exact: true })).toBeVisible();
  if (isWide(page)) {
    const active = nav.getByRole('link', { name: 'Audit log', exact: true });
    await expect(active).toHaveAttribute('aria-current', 'page');
    await expect(nav.locator('[aria-current="page"]')).toHaveCount(1);
    await expect(active).toBeInViewport({ ratio: 1 });
    const background = (label: string) => nav.getByRole('link', { name: label, exact: true }).evaluate(element => getComputedStyle(element).backgroundColor);
    const idle = await background('Grades');
    // No transition on the current entry: the highlight moves at once, so two entries are never lit together.
    expect(await background('Audit log')).not.toBe(idle);
    expect(await nav.getByRole('link', { name: 'Audit log', exact: true }).evaluate(element => getComputedStyle(element).transitionDuration)).toBe('0s');
    // The column stays with the user on a long section.
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await expect(active).toBeInViewport();
    await page.evaluate(() => window.scrollTo(0, 0));
  } else {
    // Below 1280px the section's name is said once, in its header; there is no second "current section" bar.
    await expect(sectionHeading(page)).toHaveText('Audit log');
    await expect(page.getByText('Audit log', { exact: true }).filter({ visible: true })).toHaveCount(1);
  }
  await page.goBack();
  await expect(page).toHaveURL(/[?&]tab=grades(&|$)/);
  await expect(page.getByRole('heading', { level: 2, name: 'Grades', exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});

test('entries are links: a plain click stays in the page, a Cmd/Ctrl-click opens the section in a new tab', async ({ page, context }) => {
  const { writes } = await boot(page, { url: '/setup?tab=companies' });
  await page.evaluate(() => { (window as unknown as { setupMarker: string }).setupMarker = 'same document'; });
  const grades = await setupSection(page, 'Grades');
  await expect(grades).toHaveAttribute('href', /^\/setup\?(.*&)?tab=grades(&|$)/);
  expect(new URL(await grades.getAttribute('href') ?? '', 'http://x').searchParams.has('area')).toBe(false);
  const branches = await setupSection(page, 'Branches');
  const opened = context.waitForEvent('page');
  await branches.click({ modifiers: ['ControlOrMeta'] });
  const tab = await opened;
  await expect(tab).toHaveURL(/\/setup\?(.*&)?tab=branches(&|$)/);
  await tab.close();
  // The modified click left this page where it was.
  await expect(page).toHaveURL(/[?&]tab=companies(&|$)/);
  await expect(sectionHeading(page)).toHaveText('Companies');
  // A plain click is handled in the page: no document load, the section and the address change together.
  await openSetupSection(page, 'Grades');
  await expect(sectionHeading(page)).toHaveText('Grades');
  await expect(page).toHaveURL(/[?&]tab=grades(&|$)/);
  expect(await page.evaluate(() => (window as unknown as { setupMarker?: string }).setupMarker)).toBe('same document');
  expect(writes).toEqual([]);
});

test('a ?tab= deep link opens its section and marks it, even low in the list', async ({ page }) => {
  await boot(page, { url: '/setup?tab=adminAuditLogs' });
  await expect(page.getByRole('heading', { level: 2, name: 'Audit log', exact: true })).toBeVisible();
  const nav = setupSectionEntries(page);
  await showList(page);
  await expect(nav.getByRole('link', { name: 'Audit log', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(nav.getByRole('link', { name: 'Audit log', exact: true })).toBeInViewport();
  // Cost centres used to open only from its link; it is an entry of its own now, and its link marks it.
  await page.goto('/setup?tab=costCenters');
  await expect(page.getByRole('heading', { level: 2, name: 'Cost centres', exact: true })).toBeVisible();
  await showList(page);
  await expect(nav.getByRole('link', { name: 'Cost centres', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(nav.locator('[aria-current="page"]')).toHaveCount(1);
});

test('switching section with unsaved work asks first, and Cancel keeps the work', async ({ page }) => {
  await boot(page, { url: '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=talentServices&setting=onboardingChecklists' });
  const draft = page.getByPlaceholder('Task title', { exact: true });
  await draft.fill('Keep this task');
  const dialog = page.getByRole('dialog', { name: 'Discard changes?', exact: true });
  await openSetupSection(page, 'Branches');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(draft).toHaveValue('Keep this task');
  await expect(page).toHaveURL(/[?&]tab=aiSetup(&|$)/);
  await openSetupSection(page, 'Branches');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Branches', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/[?&]tab=branches(&|$)/);
  expect(new URL(page.url()).searchParams.has('area')).toBe(false);
});

test('below 1280px the section header opens the grouped list, which closes on choice or Escape', async ({ page }) => {
  test.skip(isWide(page), 'The list folds away below 1280px.');
  await boot(page, { url: '/setup?tab=companies' });
  const nav = setupSectionEntries(page);
  const toggle = setupSectionsToggle(page);
  // One trailing secondary button in the h2 row, named for assistive tech as the section picker.
  await expect(toggle).toBeVisible();
  await expect(toggle).toHaveAccessibleName('Setup section All sections');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(toggle).toHaveClass(/btn-secondary/);
  const [toggleBox, headingBox] = await Promise.all([toggle.boundingBox(), sectionHeading(page).boundingBox()]);
  expect(Math.abs(toggleBox!.height - 36)).toBeLessThanOrEqual(1);
  expect(toggleBox!.y).toBeLessThan(headingBox!.y + headingBox!.height);
  await expect(nav.getByRole('link', { name: 'Grades', exact: true })).toBeHidden();
  // Still mounted while closed: the unsaved-changes guard re-clicks the original entry.
  await expect(page.locator('#setup-section-list a[href*="tab=grades"]')).toHaveCount(1);
  await shot(page, `${page.viewportSize()!.width}-en-list-closed`);
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  for (const link of await sectionLinks(page, ALL_WRITER_SECTIONS)) await expect(link).toBeAttached();
  expect(await noPageOverflow(page)).toBe(true);
  await shot(page, `${page.viewportSize()!.width}-en-list-open`);
  await page.keyboard.press('Escape');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(toggle).toBeFocused();
  await toggle.click();
  await (await setupSection(page, 'Grades')).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Grades', exact: true })).toBeVisible();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  // A keyboard choice must not drop focus to the document when the list collapses.
  await toggle.press('Enter');
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  // On a phone the list is a modal sheet whose focus starts on the current entry; let it land before moving on, or the
  // move races it (a longer list since every module's settings joined Setup makes the race visible).
  if (isPhone(page)) await expect(nav.getByRole('link', { name: 'Grades', exact: true })).toBeFocused();
  const branches = nav.getByRole('link', { name: 'Branches', exact: true });
  await branches.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { level: 2, name: 'Branches', exact: true })).toBeVisible();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(toggle).toBeFocused();
});

test('on a phone the list opens as a bottom sheet that holds focus and closes back to its button', async ({ page }) => {
  test.skip(!isPhone(page), 'The bottom sheet is the phone layout (below 640px).');
  await boot(page, { url: '/setup?tab=grades' });
  const toggle = setupSectionsToggle(page);
  const sheet = page.getByRole('dialog', { name: 'Setup sections', exact: true });
  await expect(sheet).toBeHidden();
  await expect(page.locator('#setup-section-list')).toBeAttached();
  await toggle.click();
  await expect(sheet).toBeVisible();
  await expect(sheet).toHaveAttribute('aria-modal', 'true');
  await expect(page.getByTestId('setup-section-scrim')).toBeVisible();
  // Focus starts on the current entry.
  await expect(sheet.getByRole('link', { name: 'Grades', exact: true })).toBeFocused();
  const viewport = page.viewportSize()!;
  const box = (await sheet.boundingBox())!;
  expect(Math.abs(box.y + box.height - viewport.height)).toBeLessThanOrEqual(1);
  expect(Math.abs(box.width - viewport.width)).toBeLessThanOrEqual(1);
  expect(box.height).toBeLessThanOrEqual(viewport.height * 0.85 + 1);
  // The sticky header keeps Close on screen while the list scrolls.
  const close = sheet.getByRole('button', { name: 'Close', exact: true });
  await sheet.evaluate(element => { element.scrollTop = element.scrollHeight; });
  await expect(close).toBeInViewport({ ratio: 1 });
  expect((await close.boundingBox())!.height).toBeGreaterThanOrEqual(44);
  await shot(page, `${viewport.width}-en-sheet-open`);
  // Tab stays inside the sheet.
  await sheet.getByRole('link', { name: 'Audit log', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(close).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await expect(sheet.getByRole('link', { name: 'Audit log', exact: true })).toBeFocused();
  // Close and the scrim both return focus to the button.
  await close.click();
  await expect(sheet).toBeHidden();
  await expect(toggle).toBeFocused();
  await toggle.click();
  await page.getByTestId('setup-section-scrim').click({ position: { x: 10, y: 10 } });
  await expect(sheet).toBeHidden();
  await expect(toggle).toBeFocused();
  await toggle.click();
  await sheet.getByRole('link', { name: 'Departments', exact: true }).click();
  await expect(sheet).toBeHidden();
  await expect(sectionHeading(page)).toHaveText('Departments');
  expect(await noPageOverflow(page)).toBe(true);
});

test('from 640px to 1279px the list is capped and opens with the current entry in view', async ({ page }) => {
  test.skip(isWide(page) || isPhone(page), 'The capped inline list is the tablet layout.');
  await boot(page, { url: '/setup?tab=adminAuditLogs' });
  const toggle = setupSectionsToggle(page);
  const list = page.locator('#setup-section-list');
  await toggle.click();
  await expect(list).toBeVisible();
  await expect(list).not.toHaveAttribute('role', 'dialog');
  const viewport = page.viewportSize()!;
  const metrics = await list.evaluate(element => ({ height: element.getBoundingClientRect().height, overflowY: getComputedStyle(element).overflowY, scrollable: element.scrollHeight > element.clientHeight }));
  expect(metrics.height).toBeLessThanOrEqual(viewport.height * 0.6 + 1);
  expect(metrics.overflowY).toBe('auto');
  // The current entry, last in the list, was scrolled into view inside the capped list.
  const current = list.getByRole('link', { name: 'Audit log', exact: true });
  const [listBox, currentBox] = await Promise.all([list.boundingBox(), current.boundingBox()]);
  expect(currentBox!.y).toBeGreaterThanOrEqual(listBox!.y - 1);
  expect(currentBox!.y + currentBox!.height).toBeLessThanOrEqual(listBox!.y + listBox!.height + 1);
  await shot(page, `${viewport.width}-en-capped-list`);
});

test('a skip link jumps past the list to the section, and a section change is announced', async ({ page }) => {
  await boot(page, { url: '/setup?tab=companies' });
  const nav = setupSectionEntries(page);
  const skip = nav.getByRole('link', { name: 'Skip to Companies', exact: true });
  // The first thing in the navigation, visible only when focused.
  expect(await setupSections(page).evaluate(element => element.querySelector('a, button')?.textContent)).toBe('Skip to Companies');
  await skip.focus();
  await expect(skip).toBeInViewport();
  expect((await skip.boundingBox())!.width).toBeGreaterThan(40);
  await page.keyboard.press('Enter');
  await expect(sectionHeading(page)).toBeFocused();
  await expect(sectionHeading(page)).toHaveAttribute('tabindex', '-1');
  await expect(page).toHaveURL(/[?&]tab=companies(&|$)/);
  const status = setupSections(page).locator('[aria-live="polite"]');
  await expect(status).toHaveText('');
  await openSetupSection(page, 'Grades');
  await expect(status).toHaveText('Grades shown');
  await expect(nav.getByRole('link', { name: 'Skip to Grades', exact: true })).toBeAttached();
});

test('Guided setup is reopened after a visit to another section, and settings addresses carry no area', async ({ page }) => {
  await boot(page, { url: '/setup?tab=aiSetup&path=guided&companyId=fixture-company' });
  await expect(sectionHeading(page)).toHaveText('Guided setup');
  await expect(page.getByTestId('guided-setup-chooser').getByRole('heading', { level: 2, name: 'Guided setup' })).toHaveCount(0);
  await openSetupSection(page, 'Grades');
  await expect(sectionHeading(page)).toHaveText('Grades');
  const gradesUrl = new URL(page.url());
  expect(gradesUrl.searchParams.has('area')).toBe(false);
  // A generic re-entry (tab=aiSetup from the same address) keeps the path the user chose.
  gradesUrl.searchParams.set('tab', 'aiSetup');
  await page.goto(`${gradesUrl.pathname}${gradesUrl.search}`);
  await expect(sectionHeading(page)).toHaveText('Guided setup');
  // From an area page, a settings section and back to Guided setup: still the chooser, not All setup areas.
  await page.goto('/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=payrollCompliance');
  await expect(page.getByRole('heading', { level: 2, name: 'Payroll & compliance', exact: true })).toBeVisible();
  await openSetupSection(page, 'Branches');
  await expect(sectionHeading(page)).toHaveText('Branches');
  expect(new URL(page.url()).searchParams.has('area')).toBe(false);
  await openSetupSection(page, 'Guided setup');
  await expect(sectionHeading(page)).toHaveText('Guided setup');
  await expect(page).toHaveURL(/[?&]path=guided(&|$)/);
  await showList(page);
  await expect(setupSectionEntries(page).getByRole('link', { name: 'Guided setup', exact: true })).toHaveAttribute('aria-current', 'page');
  if (isWide(page)) await shot(page, `${page.viewportSize()!.width}-en-guided`);
});

test('All setup areas: an overview without the side rail, then a breadcrumb on each area page', async ({ page }) => {
  await boot(page);
  const content = page.getByTestId('setup-area-content');
  await expect(sectionHeading(page)).toHaveText('All setup areas');
  // Retired from the overview: the duplicate entry buttons, the grey status box, the Next actions rail and Browse.
  for (const name of ['Policy library', 'Administration settings', 'Browse all areas']) {
    await expect(page.getByTestId('setup-studio-workspace').getByRole('button', { name, exact: true })).toHaveCount(0);
  }
  await expect(page.getByText('Configuration status is not assessed here. Open an area to review its saved settings.', { exact: true })).toHaveCount(0);
  await expect(page.getByTestId('setup-studio-workspace').getByRole('complementary')).toHaveCount(0);
  // Each area row is its name plus one line of description.
  const payroll = content.getByRole('link', { name: /^Payroll/ });
  await expect(payroll).toContainText('Accounting, fiscal years, country rules, statutory rules, taxes and compliance profiles.');
  if (isWide(page)) {
    // The area list takes the content width.
    const [contentBox, workspaceBox] = await Promise.all([content.boundingBox(), page.getByTestId('setup-studio-workspace').boundingBox()]);
    expect(Math.abs(contentBox!.width - workspaceBox!.width)).toBeLessThanOrEqual(2);
  }
  await payroll.click();
  await expect(page.getByRole('heading', { level: 2, name: 'Payroll & compliance', exact: true })).toBeFocused();
  const crumbs = content.getByRole('navigation', { name: 'Breadcrumb' });
  await expect(crumbs).toHaveText(/All setup areas\s*\/\s*Payroll/);
  await expect(crumbs.getByText('Payroll & compliance', { exact: true })).toHaveAttribute('aria-current', 'page');
  if (isWide(page)) {
    const rail = page.getByRole('complementary', { name: 'In this area' });
    await expect(rail).toBeVisible();
    await expect(rail.getByRole('button', { name: 'Continue next action' })).toHaveCount(0);
    await shot(page, `${page.viewportSize()!.width}x${page.viewportSize()!.height}-en-area-payroll`);
  }
  await crumbs.getByRole('link', { name: 'All setup areas', exact: true }).click();
  await expect(page).not.toHaveURL(/area=payrollCompliance/);
  await expect(content.getByRole('link', { name: /^Payroll/ })).toBeVisible();
});

test('empty states explain the section and offer its first step', async ({ page }) => {
  await boot(page, { url: '/setup?tab=masterData' });
  const empty = page.getByRole('region', { name: 'No drop-down lists yet' });
  await expect(empty).toBeVisible();
  await expect(empty).toContainText('Drop-down lists hold the choices people pick in forms, such as contract type or marital status.');
  // No empty right-hand panel beside it.
  await expect(page.getByText('Select a type to manage values', { exact: true })).toHaveCount(0);
  const contentBox = (await page.locator('#setup-setting-content').boundingBox())!;
  expect((await empty.boundingBox())!.width).toBeGreaterThanOrEqual(contentBox.width - 2);
  await empty.getByRole('button', { name: 'New list', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'New type' })).toBeVisible();
  await page.getByRole('dialog', { name: 'New type' }).getByRole('button', { name: 'Cancel', exact: true }).click();
  await shot(page, `${page.viewportSize()!.width}-en-drop-down-lists-empty`);

  await openSetupSection(page, 'Branches');
  const row = page.getByRole('cell').filter({ hasText: 'No branches yet' });
  await expect(row).toContainText('Add your first branch, or import a CSV.');
  // Empty-state text is never lighter than slate-500.
  const colours = await row.locator('p').evaluateAll(nodes => nodes.map(node => getComputedStyle(node).color));
  for (const colour of colours) expect(['rgb(148, 163, 184)', 'rgb(203, 213, 225)']).not.toContain(colour);
});

test('Arabic mirrors the section navigation and keeps every label readable', async ({ page }) => {
  await boot(page, { arabic: true, url: '/setup?tab=grades' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByRole('navigation', { name: 'أقسام الإعداد', exact: true })).toBeAttached();
  const arabicNav = setupSectionEntries(page, 'أقسام الإعداد');
  await showList(page);
  for (const group of ['البدء', 'هيكل الشركة', 'الوظائف والقوى العاملة', 'الرواتب والامتثال', 'مساحة العمل']) {
    const list = arabicNav.getByRole('list', { name: group, exact: true });
    await expect(list).toBeVisible();
    // No uppercase or letter-spacing on Arabic group labels, at 13px.
    const style = await arabicNav.getByText(group, { exact: true }).evaluate(element => { const css = getComputedStyle(element); return { transform: css.textTransform, spacing: css.letterSpacing, size: css.fontSize }; });
    expect(style).toEqual({ transform: 'none', spacing: 'normal', size: '13px' });
  }
  const active = arabicNav.getByRole('link', { name: 'الدرجات', exact: true });
  await expect(active).toHaveAttribute('aria-current', 'page');
  await expect(sectionHeading(page)).toHaveText('الدرجات');
  for (const link of await arabicNav.getByRole('listitem').getByRole('link').all()) {
    expect(await link.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  }
  if (isWide(page)) {
    // The column sits on the inline-start (right) side, beside the content.
    const navBox = (await page.getByRole('navigation', { name: 'أقسام الإعداد', exact: true }).boundingBox())!;
    const contentBox = (await page.locator('#setup-setting-content').boundingBox())!;
    expect(navBox.x).toBeGreaterThan(contentBox.x + contentBox.width - 1);
  }
  expect(await noPageOverflow(page)).toBe(true);
  await shot(page, `${page.viewportSize()!.width}-ar-grades${isWide(page) ? '' : '-list-open'}`);
});

test('dark mode keeps the navigation legible', async ({ page }) => {
  await boot(page, { dark: true, url: '/setup?tab=masterData' });
  await expect(page.locator('html')).toHaveClass(/(^|\s)dark(\s|$)/);
  await showList(page);
  const axe = await new AxeBuilder({ page }).include('nav[aria-label="Setup sections"]').withRules(['color-contrast']).analyze();
  expect(axe.violations).toEqual([]);
  await shot(page, `${page.viewportSize()!.width}-dark-drop-down-lists`);
});

test('a read-only viewer sees only the sections their role can open', async ({ page }) => {
  await boot(page, { readOnly: true, url: '/setup?tab=aiSetup' });
  const nav = setupSectionEntries(page);
  await showList(page);
  await expect(nav.getByRole('listitem')).toHaveCount(1);
  await expect(nav.getByRole('link', { name: 'Companies', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(nav.getByRole('list', { name: 'Get started', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { level: 2, name: 'Companies', exact: true })).toBeVisible();
});

test('a company-scoped group administrator sees only company-owned settings, Cost centres included', async ({ page }) => {
  await boot(page, { companyScoped: true, url: '/setup?tab=companies' });
  const nav = setupSectionEntries(page);
  await showList(page);
  await expect(nav.getByRole('listitem').getByRole('link')).toHaveText(['Guided setup', 'All setup areas', 'Policy library', 'Companies', 'Branches', 'Cost centres',
    // Departments, grades and designations are company-owned since the org catalog became per company.
    'Departments', 'Grades', 'Designations',
    // Modules' configuration keeps each module page's own rule, which company scope never narrowed.
    'Leave types', 'Leave policies', 'Holiday calendars', 'Payslip templates', 'Onboarding checklists', 'Payroll accounting', 'Tax policies', 'Audit log']);
  await openSetupSection(page, 'Cost centres');
  await expect(sectionHeading(page)).toHaveText('Cost centres');
  await expect(page).toHaveURL(/[?&]tab=costCenters(&|$)/);
  await expect(page.getByRole('button', { name: 'New cost centre', exact: true })).toBeVisible();
});
