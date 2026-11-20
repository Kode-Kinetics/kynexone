import { expect, test, type Page } from '@playwright/test';
import { bootPeopleFixture, type Locale, type PeopleFixture } from './people-list.fixture';

/**
 * People module link crawl (route-mocked, see people-list.fixture.ts). From /people it clicks every link and button
 * that navigates or opens something, and from /people/[employeeId] every tab and link, and records any 404, blank
 * screen, console/page error, failed (4xx/5xx) request, unmocked request or dead button.
 *
 * Defects are `knownDefect(...)` tests (skipped; run red with PEOPLE_LIST_RUN_DEFECTS=1). The suite is green today.
 */

const RUN_DEFECTS = !!process.env.PEOPLE_LIST_RUN_DEFECTS;
function knownDefect(id: string, applies: boolean, reason: string) {
  test.fixme(applies && !RUN_DEFECTS, `${id}: ${reason}`);
}

interface Mark { errors: number; failed: number; unmocked: number }
const mark = (fx: PeopleFixture): Mark => ({ errors: fx.errors.length, failed: fx.failed.length, unmocked: fx.unmocked.length });
/** Everything the mock recorded since `from` must be empty: no console error, no 4xx/5xx, no request nobody mocked. */
function expectClean(fx: PeopleFixture, from: Mark, where: string) {
  expect({
    where,
    errors: fx.errors.slice(from.errors),
    failed: fx.failed.slice(from.failed),
    unmocked: fx.unmocked.slice(from.unmocked),
  }).toEqual({ where, errors: [], failed: [], unmocked: [] });
}

async function open(page: Page, opts: { locale?: Locale; count?: number; width?: number; height?: number; permissions?: string[]; path?: string } = {}) {
  await page.setViewportSize({ width: opts.width ?? 1366, height: opts.height ?? 768 });
  const fx = await bootPeopleFixture(page, { locale: opts.locale ?? 'en', count: opts.count ?? 25, permissions: opts.permissions });
  await page.goto(opts.path ?? '/people');
  if (!opts.path) await expect(page.locator('#employee-1000')).toBeVisible({ timeout: 120_000 });
  return fx;
}

const pagerOf = (page: Page) => page.getByTestId('employee-list-scroll').locator('xpath=following-sibling::div[1]');

/** A page is "not blank" when it has a heading and visible text, and is not Next's own 404 or the error boundary. */
async function expectRendered(page: Page, heading?: RegExp | string) {
  await expect(page.getByText('This page could not be found')).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Something went wrong' })).toHaveCount(0);
  if (heading) await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
  const text = await page.evaluate(() => document.body.innerText.trim().length);
  expect(text, 'the page has visible text').toBeGreaterThan(100);
}

const LOCALES: Locale[] = ['en', 'ar'];

test.describe('People crawl: left-menu links from /people', () => {
  const links: Array<{ name: string; url: RegExp; heading: RegExp | string }> = [
    { name: 'New Hires', url: /\/people\/new-hires$/, heading: 'New hires' },
    { name: 'Profile review', url: /\/people\/onboarding-review$/, heading: 'Profile completion review' },
    { name: 'Onboarding Tasks', url: /\/people\/onboarding-tasks$/, heading: 'Onboarding tasks' },
    { name: 'Org Chart', url: /\/org-chart$/, heading: 'Org Chart' },
    { name: 'HR Letters', url: /\/hr-letters$/, heading: 'HR Letters' },
  ];
  for (const link of links) {
    test(`${link.name} opens a rendered page with no errors, and Back returns to the list`, async ({ page }) => {
      const fx = await open(page);
      const from = mark(fx);
      await page.getByRole('button', { name: link.name, exact: true }).click();
      await expect(page).toHaveURL(link.url);
      await expectRendered(page, link.heading);
      expectClean(fx, from, link.name);
      await page.goBack();
      await expect(page).toHaveURL(/\/people$/);
      await expect(page.locator('#employee-1000')).toBeVisible();
    });
  }

  test('the directory row link navigates client-side (no reload) and still opens a new tab on ctrl/cmd-click and middle-click', async ({ page, context }) => {
    await open(page);
    const row = page.locator('#employee-1000');
    await expect(row).toHaveAttribute('href', /^\/people\/1000\?returnTo=/);
    for (const click of [{ modifiers: ['ControlOrMeta'] as Array<'ControlOrMeta'> }, { button: 'middle' as const }]) {
      const popup = context.waitForEvent('page');
      await row.click(click);
      const tab = await popup;
      await expect(tab).toHaveURL(/\/people\/1000\?returnTo=/);
      await tab.close();
      await expect(page).toHaveURL(/\/people$/);
    }
    // A plain click is a client-side transition: a marker set on window survives it (a full reload would drop it).
    await page.evaluate(() => { (window as unknown as { __spaMarker: boolean }).__spaMarker = true; });
    await row.focus();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/people\/1000\?returnTo=/);
    expect(await page.evaluate(() => (window as unknown as { __spaMarker?: boolean }).__spaMarker)).toBe(true);
  });

  test('the in-module pages link on to real employees (Onboarding Tasks and New Hires reach the workspace)', async ({ page }) => {
    const fx = await open(page, { path: '/people/onboarding-tasks' });
    await expect(page.getByRole('link', { name: 'Employee 01 Al-Harbi' }).first()).toBeVisible();
    const from = mark(fx);
    await page.getByRole('link', { name: 'Employee 01 Al-Harbi' }).first().click();
    await expect(page).toHaveURL(/\/people\/1000\?returnTo=%2Fpeople%2Fonboarding-tasks$/);
    await expectRendered(page, 'Employee 01 Al-Harbi');
    expectClean(fx, from, 'onboarding task -> workspace');
    await page.getByRole('link', { name: 'Back to People' }).click();
    await expect(page).toHaveURL(/\/people\/onboarding-tasks$/);

    await page.goto('/people/new-hires');
    await page.getByRole('tab', { name: /Activated/ }).or(page.getByRole('button', { name: /Activated/ })).first().click();
    const open_ = page.locator('main a[href^="/people/1000"]').first();
    await expect(open_).toBeVisible();
    await open_.click();
    await expect(page).toHaveURL(/\/people\/1000\?section=overview&returnTo=%2Fpeople%2Fnew-hires$/);
    await expectRendered(page, 'Employee 01 Al-Harbi');
    await page.getByRole('link', { name: 'Back to People' }).click();
    await expect(page).toHaveURL(/\/people\/new-hires$/);
  });

  test('the profile-review inbox links to the employee workspace', async ({ page }) => {
    await open(page, { path: '/people/onboarding-review' });
    await page.getByRole('button', { name: 'Review', exact: true }).first().click();
    await page.getByRole('link', { name: 'Open employee' }).first().click();
    await expect(page).toHaveURL(/\/people\/1000\?section=access-onboarding&returnTo=%2Fpeople%2Fonboarding-review$/);
    await expectRendered(page, 'Employee 01 Al-Harbi');
    await page.getByRole('link', { name: 'Back to People' }).click();
    await expect(page).toHaveURL(/\/people\/onboarding-review$/);
  });
});

for (const locale of LOCALES) {
  for (const size of [{ name: '1366x768', width: 1366, height: 768 }, { name: '390x844', width: 390, height: 844 }]) {
    test.describe(`People crawl: list row links and returnTo round trip, ${locale} ${size.name}`, () => {
      test('employee name opens the workspace and Back to People restores the same list, filter, page and anchor', async ({ page }) => {
        const fx = await open(page, { locale, count: 60, width: size.width, height: size.height });
        // Page 2 of 3, filtered to everyone, then open a row.
        // The pager buttons are translated, so the Arabic page names Next in Arabic.
        await pagerOf(page).getByRole('button', { name: locale === 'ar' ? 'التالي' : 'Next', exact: true }).click();
        await expect(page).toHaveURL(/[?&]page=2\b/);
        await expect(pagerOf(page)).toContainText('2');
        const rowLink = page.locator('#employee-1026');
        await expect(rowLink).toBeVisible();
        const from = mark(fx);
        await rowLink.scrollIntoViewIfNeeded();
        await rowLink.click();
        await expect(page).toHaveURL(/\/people\/1026\?returnTo=/);
        const url = new URL(page.url());
        expect(url.searchParams.get('returnTo')).toBe('/people?page=2&anchor=employee-1026');
        await expectRendered(page, 'Employee 27 Al-Harbi');
        if (locale === 'ar') await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
        expectClean(fx, from, 'open employee');

        await page.getByRole('link', { name: /Back to People|العودة/ }).first().click();
        await expect(page).toHaveURL(/\/people\?page=2&anchor=employee-1026$/);
        await expect(page.locator('#employee-1026')).toBeFocused();
        await expect(pagerOf(page)).toContainText('2');
        await expect(page.getByTestId('employee-list-scroll')).toBeVisible();
        expectClean(fx, from, 'return to list');
      });
    });
  }
}

test.describe('People crawl: toolbar and filters on /people', () => {
  test('Add Employee opens the create flow at /people/new and Cancel returns', async ({ page }) => {
    const fx = await open(page);
    const from = mark(fx);
    await page.getByRole('button', { name: 'Add Employee' }).first().click();
    await expect(page).toHaveURL(/\/people\/new$/);
    const dialog = page.getByRole('dialog', { name: 'Add Employee' });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(page).toHaveURL(/\/people(\?|$)/);
    expectClean(fx, from, 'add employee');
  });

  test('Export, CSV import template and Import CSV each do something (download, download, file chooser + preview)', async ({ page }) => {
    const fx = await open(page);
    const from = mark(fx);
    const exportDownload = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Export', exact: true }).click();
    expect((await exportDownload).suggestedFilename()).toBe('employees.csv');
    const templateDownload = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Template', exact: true }).click();
    expect((await templateDownload).suggestedFilename()).toBe('employees-template.csv');
    const chooser = page.waitForEvent('filechooser');
    await page.getByRole('button', { name: 'Import CSV' }).click();
    const picked = await chooser;
    await picked.setFiles({ name: 'people.csv', mimeType: 'text/csv', buffer: Buffer.from('Code,Name\nE1,Test Person\n') });
    await expect.poll(() => fx.writes.some(w => w.path === '/api/employees/import-preview')).toBe(true);
    expect(fx.requested).toEqual(expect.arrayContaining(['GET /api/employees/export', 'GET /api/employees/import-template']));
    expect(fx.errors.slice(from.errors), 'no console error through export, template and import').toEqual([]);
    expect(fx.failed.slice(from.failed)).toEqual([]);
  });

  test('every Self-service chip filters through the API and the URL; Everyone resets; an empty chip offers Clear filters', async ({ page }) => {
    const fx = await open(page);
    const from = mark(fx);
    const chips = page.getByTestId('access-filter').getByRole('button');
    const names = await chips.allInnerTexts();
    expect(names.length, 'Everyone + six access states').toBe(7);
    const states: Record<string, string> = {
      'Waiting for work email': 'waiting_for_work_email', 'No access yet': 'not_started', 'Code given, not signed in yet': 'code_given',
      'Using KynexOne': 'active', 'Access stopped': 'stopped',
    };
    for (const [label, state] of Object.entries(states)) {
      await page.getByTestId('access-filter').getByRole('button', { name: new RegExp(`^${label}`) }).click();
      await expect(page).toHaveURL(new RegExp(`access=${state}`));
      await expect(page.getByTestId('access-filter').getByRole('button', { name: new RegExp(`^${label}`) })).toHaveAttribute('aria-pressed', 'true');
      await expect.poll(() => fx.listQueries.some(q => q.includes(`access=${state}`))).toBe(true);
      await expect(page.locator('tbody tr').first()).toBeVisible();
    }
    await page.getByTestId('access-filter').getByRole('button', { name: /^Needs admin help/ }).click();
    await expect(page.getByText('No employees match this filter.')).toBeVisible();
    await page.getByRole('button', { name: 'Clear filters' }).click();
    await expect(page).not.toHaveURL(/access=/);
    await expect(page.locator('#employee-1000')).toBeVisible();
    await page.getByTestId('access-filter').getByRole('button', { name: /^Using KynexOne/ }).click();
    await page.getByTestId('access-filter').getByRole('button', { name: /^Everyone/ }).click();
    await expect(page).not.toHaveURL(/access=/);
    expectClean(fx, from, 'chips');
  });

  test('search, status and readiness filters narrow the list and are kept in the URL', async ({ page }) => {
    const fx = await open(page);
    const from = mark(fx);
    await page.getByRole('searchbox', { name: 'Search employees' }).fill('Employee 03');
    await expect(page).toHaveURL(/search=Employee(\+|%20)03/);
    await expect(page.locator('tbody tr')).toHaveCount(1);
    await page.getByRole('searchbox', { name: 'Search employees' }).fill('');
    await expect(page.locator('tbody tr')).toHaveCount(25);
    await page.getByRole('combobox', { name: 'Readiness filter' }).selectOption('Blocked');
    await expect(page).toHaveURL(/readiness=Blocked/);
    await expect.poll(() => fx.listQueries.some(q => q.includes('readiness=Blocked'))).toBe(true);
    await page.getByRole('combobox', { name: 'Readiness filter' }).selectOption('');
    await page.getByRole('combobox', { name: 'Status filter' }).selectOption({ index: 1 });
    await expect(page).toHaveURL(/status=/);
    expectClean(fx, from, 'filters');
  });

  test('Next/Prev page through 3 pages, ends are disabled, a double click on Next moves one page, browser Back works', async ({ page }) => {
    const fx = await open(page, { count: 60 });
    const from = mark(fx);
    const pager = pagerOf(page);
    const next = pager.getByRole('button', { name: 'Next', exact: true });
    const prev = pager.getByRole('button', { name: 'Prev', exact: true });
    await expect(pager).toContainText('Page 1 of 3');
    await expect(prev).toBeDisabled();
    await next.dblclick();
    await expect(pager).toContainText('Page 2 of 3');
    await expect(page).toHaveURL(/page=2/);
    await expect(page.locator('#employee-1025')).toBeVisible();
    await next.click();
    await expect(pager).toContainText('Page 3 of 3');
    await expect(next).toBeDisabled();
    await prev.click();
    await expect(pager).toContainText('Page 2 of 3');
    await page.goBack();
    await expect(pager).toContainText('Page 3 of 3');
    await page.goBack();
    await expect(pager).toContainText('Page 2 of 3');
    expectClean(fx, from, 'paging');
  });

  test('the Ex-Employees tab renders its table, links back to Current, and has no errors', async ({ page }) => {
    const fx = await open(page);
    const from = mark(fx);
    await page.getByRole('tab', { name: 'Ex-Employees' }).click();
    await expect(page.getByText('Former Employee 1')).toBeVisible();
    await expect(page.getByText('Former employees — retained for statutory audit')).toBeVisible();
    await page.getByRole('tab', { name: 'Current' }).click();
    await expect(page.locator('#employee-1000')).toBeVisible();
    expectClean(fx, from, 'ex-employees');
  });

  test('the profile badge opens the activation checklist (a window or the workspace), and focus is not lost', async ({ page }) => {
    knownDefect('PL-B1', true, 'the Profile badge (title "Profile 60% complete - open the activation checklist") only loads the employee and readiness in the background (two API calls) and shows nothing: the legacy detail panel no longer exists on the list. Link it to employeeWorkspaceHref(id, { section: "overview", returnTo }) like the name link');
    const fx = await open(page);
    const from = mark(fx);
    const badge = page.getByTitle(/open the activation checklist/).first();
    await badge.click();
    await expect.poll(async () => page.url().includes('/people/1000') || (await page.getByRole('dialog').count()) > 0, { message: 'the badge shows the checklist' }).toBe(true);
    expectClean(fx, from, 'profile badge');
  });

  test('unhappy: a list API 500 shows an error, not a blank table, and Refresh recovers', async ({ page }) => {
    const fx = await open(page);
    fx.listFailure = 500;
    await page.getByRole('button', { name: 'Refresh' }).click();
    await expect(page.getByRole('alert').first()).toBeVisible();
    await expect(page.getByText('Employee records unavailable')).toBeVisible();
    fx.listFailure = null;
    await page.getByRole('button', { name: 'Refresh' }).click();
    await expect(page.locator('#employee-1000')).toBeVisible();
    await expect(page.getByText('25 employee records')).toBeVisible();
  });

  test('unhappy: a dropped connection shows an error and Refresh recovers', async ({ page }) => {
    const fx = await open(page);
    fx.listFailure = 'network';
    await page.getByRole('button', { name: 'Refresh' }).click();
    await expect(page.getByRole('alert').first()).toBeVisible();
    fx.listFailure = null;
    await page.getByRole('button', { name: 'Refresh' }).click();
    await expect(page.locator('#employee-1000')).toBeVisible();
  });

  test('permission denied: employees.read only hides Add Employee, Export, Import and Reset; New Hires redirects instead of rendering blank', async ({ page }) => {
    const fx = await open(page, { permissions: ['employees.read', 'dashboard.read'] });
    await expect(page.getByRole('button', { name: 'Add Employee' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Export', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Import CSV' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Reset sign-in' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'New Hires', exact: true })).toHaveCount(0);
    const requestedBefore = fx.requested.length;
    await page.goto('/people/new-hires');
    await expect(page).not.toHaveURL(/new-hires/, { timeout: 15_000 });
    await expect(page.getByText('Access Denied').first(), 'a denial notice, not a silent redirect').toBeVisible();
    expect(fx.requested.slice(requestedBefore).filter(r => r.includes('/api/employees/drafts')), 'the gated page never asked for its data').toEqual([]);
  });
});

test.describe('People crawl: employee workspace /people/[employeeId]', () => {
  const sections = [
    { link: 'Overview', section: 'overview' }, { link: 'Personal', section: 'personal' }, { link: 'Employment', section: 'employment' },
    { link: 'Pay & benefits', section: 'pay-benefits' }, { link: 'Compliance & documents', section: 'compliance-documents' },
    { link: 'Access & onboarding', section: 'access-onboarding' }, { link: 'Activity', section: 'activity' },
  ];

  for (const locale of LOCALES) {
    test(`every section tab renders without errors and keeps the returnTo, ${locale}`, async ({ page }) => {
      const fx = await open(page, { locale, path: '/people/1000?returnTo=%2Fpeople%3Fpage%3D2%26anchor%3Demployee-1000' });
      await expect(page.getByRole('heading', { level: 1, name: 'Employee 01 Al-Harbi' })).toBeVisible({ timeout: 120_000 });
      const nav = page.getByRole('navigation', { name: /Employee record sections|أقسام/ });
      const from = mark(fx);
      const count = await nav.getByRole('link').count();
      expect(count).toBe(sections.length);
      for (let i = 0; i < sections.length; i++) {
        await nav.getByRole('link').nth(i).click();
        await expect(page).toHaveURL(new RegExp(`section=${sections[i].section}`));
        await expect(nav.getByRole('link').nth(i)).toHaveAttribute('aria-current', 'page');
        expect(new URL(page.url()).searchParams.get('returnTo'), `${sections[i].section} keeps returnTo`).toBe('/people?page=2&anchor=employee-1000');
        await expectRendered(page, 'Employee 01 Al-Harbi');
      }
      expectClean(fx, from, 'workspace tabs');
      await page.getByRole('link', { name: /Back to People|العودة/ }).first().click();
      await expect(page).toHaveURL(/\/people\?page=2&anchor=employee-1000$/);
    });
  }

  test('Edit full record, More actions links and the in-section Edit links resolve to rendered pages', async ({ page }) => {
    const fx = await open(page, { path: '/people/1000?returnTo=%2Fpeople' });
    await expect(page.getByRole('heading', { level: 1, name: 'Employee 01 Al-Harbi' })).toBeVisible({ timeout: 120_000 });
    const from = mark(fx);
    await page.getByRole('link', { name: 'Edit full record' }).click();
    await expect(page).toHaveURL(/\/people\/1000\/edit\?section=overview&returnTo=/);
    await expect(page.getByRole('heading', { level: 1, name: /Edit Employee/ }).or(page.getByRole('heading', { name: /Edit Employee/ })).first()).toBeVisible();
    await page.getByRole('link', { name: /Back|profile|People/i }).first().click();
    await expect(page).toHaveURL(/\/people\/1000(\?|$)/);

    await page.getByText('More actions').click();
    await page.getByRole('link', { name: 'View activity' }).click();
    await expect(page).toHaveURL(/section=activity/);
    const more = page.locator('details', { hasText: 'More actions' });
    if (!(await more.evaluate(el => (el as HTMLDetailsElement).open))) await page.getByText('More actions').click();
    await page.getByRole('link', { name: 'Edit employment' }).click();
    await expect(page).toHaveURL(/\/people\/1000\/edit\?section=employment/);
    await expectRendered(page);
    expectClean(fx, from, 'edit and more actions');
  });

  test('every internal link on every section resolves (no 404 / blank / error), crawled one by one', async ({ page }) => {
    test.setTimeout(240_000);
    const fx = await open(page, { path: '/people/1000?returnTo=%2Fpeople' });
    await expect(page.getByRole('heading', { level: 1, name: 'Employee 01 Al-Harbi' })).toBeVisible({ timeout: 120_000 });
    const hrefs = new Set<string>();
    for (const s of sections) {
      await page.goto(`/people/1000?section=${s.section}&returnTo=%2Fpeople`);
      await expect(page.getByRole('heading', { level: 1, name: 'Employee 01 Al-Harbi' })).toBeVisible();
      for (const href of await page.locator('main a[href^="/"]').evaluateAll(as => as.map(a => a.getAttribute('href') as string))) hrefs.add(href);
    }
    expect(hrefs.size, 'the workspace exposes links to crawl').toBeGreaterThan(10);
    const bad: string[] = [];
    for (const href of hrefs) {
      const from = mark(fx);
      const response = await page.goto(href);
      await page.getByRole('heading').first().waitFor({ timeout: 30_000 }).catch(() => undefined);
      const text = await page.evaluate(() => document.body.innerText);
      if ((response?.status() ?? 0) >= 400 || /could not be found|Something went wrong/.test(text) || text.trim().length < 100) bad.push(`${href}: ${response?.status()} ${text.slice(0, 80)}`);
      const noise = [...fx.errors.slice(from.errors), ...fx.failed.slice(from.failed), ...fx.unmocked.slice(from.unmocked)];
      if (noise.length) bad.push(`${href}: ${noise.join(' | ').slice(0, 300)}`);
    }
    expect(bad).toEqual([]);
  });

  test('unhappy: an unknown employee shows a not-found state with a way back; a bad id shows an invalid-link state', async ({ page }) => {
    const fx = await open(page, { path: '/people/99999?returnTo=%2Fpeople%3Fpage%3D2' });
    await expect(page.getByRole('heading', { name: 'Employee record not found' })).toBeVisible({ timeout: 120_000 });
    expect([...new Set(fx.failed)], 'only the expected 404 (the dev server runs effects twice)').toEqual(['404 GET /api/employees/99999']);
    await page.getByRole('link', { name: 'Back to People' }).click();
    await expect(page).toHaveURL(/\/people\?page=2$/);

    await page.goto('/people/abc');
    await expect(page.getByRole('heading', { name: 'Invalid employee link' })).toBeVisible();
    await page.getByRole('link', { name: 'Back to People' }).click();
    await expect(page).toHaveURL(/\/people$/);
  });

  test('a hostile returnTo is ignored (open-redirect guard)', async ({ page }) => {
    await open(page, { path: '/people/1000?returnTo=https%3A%2F%2Fevil.example%2Fpeople' });
    await expect(page.getByRole('heading', { level: 1, name: 'Employee 01 Al-Harbi' })).toBeVisible({ timeout: 120_000 });
    await expect(page.getByRole('link', { name: /Back to People/ }).first()).toHaveAttribute('href', '/people');
  });

  test('no dead buttons: every enabled button in the list toolbar and the workspace sections does something', async ({ page }) => {
    test.setTimeout(240_000);
    const fx = await open(page);
    const dead: string[] = [];
    const probe = async (scope: string, url: string, skip: RegExp) => {
      await page.goto(url);
      await expect(page.locator(scope).first()).toBeVisible({ timeout: 60_000 });
      const total = await page.locator(`${scope} button:visible`).count();
      for (let i = 0; i < total; i++) {
        await page.goto(url);
        await expect(page.locator(scope).first()).toBeVisible({ timeout: 60_000 });
        const button = page.locator(`${scope} button:visible`).nth(i);
        const label = ((await button.getAttribute('aria-label')) ?? (await button.innerText()) ?? '').trim().slice(0, 40) || `#${i}`;
        if (skip.test(label) || await button.isDisabled()) continue;
        await page.evaluate(() => {
          const w = window as unknown as { __mut: number };
          w.__mut = 0;
          new MutationObserver(records => { w.__mut += records.length; }).observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
        });
        const beforeUrl = page.url();
        const beforeRequests = fx.requested.length;
        let chooser = false;
        page.once('filechooser', () => { chooser = true; });
        let downloaded = false;
        page.once('download', () => { downloaded = true; });
        await button.click({ timeout: 5_000 }).catch(() => undefined);
        // Anything observable counts: a DOM change, a URL change, a request, a file chooser or a download. Poll for up to 3s so a
        // slow dev server is not mistaken for a dead control.
        const did = await expect.poll(async () => {
          const mutations = await page.evaluate(() => (window as unknown as { __mut?: number }).__mut ?? 0).catch(() => 1);
          return mutations > 0 || page.url() !== beforeUrl || fx.requested.length > beforeRequests || chooser || downloaded;
        }, { timeout: 3_000 }).toBe(true).then(() => true, () => false);
        if (!did) dead.push(`${url} :: "${label}"`);
      }
    };
    await probe('main', '/people', /^$/);
    await probe('.employee-workspace', '/people/1000?returnTo=%2Fpeople', /^$/);
    expect(dead, `dead buttons: ${dead.join(' ; ')}`).toEqual([]);
  });
});
