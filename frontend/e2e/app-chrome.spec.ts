import { expect, test, type Locator, type Page } from '@playwright/test';
import { translate } from '../src/i18n/translations';

// App chrome in English and Arabic: the Kody launcher must never cover a page's primary action, and the strings the
// 2026-10-22 design review found in English under Arabic stay translated. Every API call is intercepted.

const SHOTS = process.env.APP_CHROME_SHOTS_DIR;

/** Found in English on the Arabic Setup page and sidebar by the design review (decisions item 11). */
const LISTED_ENGLISH = [
  'Group Overview', 'Insights & Reports', 'Tax Policies', 'GOSI Filing', 'Parallel Run Variance', 'Home',
  'Import CSV', 'Template', 'Export', 'Importing…', 'Checking…',
];

const errorsByPage = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  errorsByPage.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
});
test.afterEach(async ({ page }) => {
  expect(errorsByPage.get(page), 'No application runtime errors').toEqual([]);
});

async function boot(page: Page, url: string, arabic: boolean, options: { manyCompanies?: boolean } = {}) {
  await page.addInitScript((ar) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', ar ? 'ar' : 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
    localStorage.setItem('kynexone.theme', 'light');
  }, arabic);
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (request.method() !== 'GET') return route.fulfill({ status: 405, json: { message: 'Read-only fixture' } });
    const company = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
    const companies = [company, { ...company, id: 'second-company', legalNameEn: 'Second Fixture LLC' }];
    // A long list makes the page scroll, so the last row's actions have to clear the launcher at the end.
    const listed = options.manyCompanies
      ? [...companies, ...Array.from({ length: 30 }, (_, i) => ({ ...company, id: `company-${i}`, legalNameEn: `Fixture Company ${i + 1}` }))]
      : companies;
    if (path === '/api/auth/me') return reply({
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'],
      permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', 'leave.read',
        'leave.write', 'recruitment.read', 'payroll.read', 'payroll.write', 'employees.read', 'employees.write', 'reports.read', 'compliance.read',
        'approvals.read', 'approvals.decide', 'attendance.read', 'ess.read'],
      accountType: 'Group', isGroupScope: true,
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
    if (path === '/api/companies') return reply({ items: listed, total: listed.length, page: 1, pageSize: 100 });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(url);
  await expect(page.locator('main').first()).toBeVisible({ timeout: 30_000 });
  // The Next.js dev-mode badge sits in the launcher's corner and over the bottom nav's More; it is not the app.
  await page.addStyleTag({ content: 'nextjs-portal { display: none !important; }' });
}

const launcherOf = (page: Page, arabic: boolean) =>
  page.getByRole('button', { name: translate(arabic ? 'ar' : 'en', 'Open Kody, HR assistant'), exact: true });

const escapeRegExp = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

type Box = { x: number; y: number; width: number; height: number };
const overlaps = (a: Box, b: Box) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;

/** Visible actions in the page body (primary ones, or every button and link) must not intersect the launcher. */
async function expectNoCover(page: Page, launcher: Locator, where: string, selector = '.btn-primary:visible') {
  const kody = (await launcher.boundingBox())!;
  const actions = page.locator('main').first().locator(selector);
  for (const button of await actions.all()) {
    const box = await button.boundingBox();
    if (!box) continue;
    const name = (await button.innerText()).trim();
    expect(overlaps(box, kody), `Kody covers "${name}" (${where})`).toBe(false);
  }
}

const SCREENS = [
  { name: 'setup-companies', url: '/setup?tab=companies&companyId=fixture-company', primary: ['Create company'], manyCompanies: true },
  { name: 'setup-grades', url: '/setup?tab=grades&companyId=fixture-company', primary: ['Add Grade', 'Add grade'] },
  { name: 'setup-departments', url: '/setup?tab=departments&companyId=fixture-company', primary: ['Add Department', 'Add department'] },
  { name: 'leave', url: '/leave', primary: [] },
  { name: 'approvals', url: '/approvals', primary: [] },
] as const;

for (const arabic of [false, true]) {
  const lang = arabic ? 'ar' : 'en';
  test.describe(`Kody launcher (${lang})`, () => {
    for (const screen of SCREENS) {
      test(`never covers a primary action on ${screen.name}`, async ({ page }, info) => {
        await boot(page, screen.url, arabic, { manyCompanies: 'manyCompanies' in screen && screen.manyCompanies });
        const launcher = launcherOf(page, arabic);
        await expect(launcher).toBeVisible();
        // Below 1280px it is the 48px circle; from 1280 it carries its name.
        const kody = (await launcher.boundingBox())!;
        if (page.viewportSize()!.width < 1280) {
          expect(Math.round(kody.width), 'launcher is a circle below 1280px').toBe(48);
          expect(Math.round(kody.height)).toBe(48);
        } else {
          expect(kody.width).toBeGreaterThan(48);
        }
        // The screen's own primary action has rendered (its label may be in either case while Setup is renamed).
        if (screen.primary.length) {
          const names = screen.primary.map(label => translate(lang, label));
          const pattern = new RegExp(`^\\s*(${names.map(escapeRegExp).join('|')})\\s*$`, 'i');
          await expect(page.locator('main').first().getByRole('button', { name: pattern }).first()).toBeVisible({ timeout: 15_000 });
        }
        await page.waitForTimeout(400); // the main fade-in settles

        await expectNoCover(page, launcher, 'at load');
        // Each primary action, brought into view the way a user scrolls to it.
        for (const button of await page.locator('main').first().locator('.btn-primary:visible').all()) {
          await button.evaluate(el => el.scrollIntoView({ block: 'end' }));
          await page.mouse.wheel(0, 0);
          const box = (await button.boundingBox())!;
          const vh = page.viewportSize()!.height;
          // Scrolled so it sits at the very bottom: if the page could scroll further, it must be able to clear Kody.
          const kodyNow = (await launcher.boundingBox())!;
          if (overlaps(box, kodyNow)) {
            await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
            expect(overlaps((await button.boundingBox())!, (await launcher.boundingBox())!), `"${(await button.innerText()).trim()}" can scroll clear of Kody (viewport ${vh})`).toBe(false);
          }
        }
        // At the end of the page, every last action (not only primary ones) sits clear of the launcher.
        await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
        await page.waitForTimeout(150);
        await expectNoCover(page, launcher, 'scrolled to the end', 'button:visible, a[href]:visible');
        if (SHOTS) await page.screenshot({ path: `${SHOTS}/${screen.name}-${lang}-${info.project.name}.png`, fullPage: false });
      });
    }
  });
}

test.describe('Arabic leftovers', () => {
  test('the Setup page and the sidebar show none of the listed English strings', async ({ page }, info) => {
    await boot(page, '/setup?tab=companies&companyId=fixture-company', true);
    const main = page.locator('main').first();
    await expect(main.getByRole('button', { name: translate('ar', 'Import CSV') })).toBeVisible({ timeout: 15_000 });
    const mainText = await main.innerText();

    // Open the sidebar (a drawer below 1024px) and expand every group in turn: it keeps one work area open.
    if (page.viewportSize()!.width < 1024) {
      const bottomNav = page.getByRole('navigation', { name: translate('ar', 'Primary') });
      await expect(bottomNav).toContainText('الرئيسية');
      await expect(bottomNav).not.toContainText('Home');
      await bottomNav.getByRole('button', { name: translate('ar', 'More') }).click();
    }
    const sidebar = page.locator('aside nav').first();
    await expect(sidebar).toBeVisible();
    let sidebarText = '';
    for (const header of await sidebar.locator(':scope > div > button[aria-expanded]').all()) {
      if (await header.getAttribute('aria-expanded') !== 'true') await header.click();
      sidebarText += `\n${await sidebar.innerText()}`;
      // No uppercase or letter-spacing on Arabic group labels.
      const label = header.locator('span').first();
      expect(await label.evaluate(el => getComputedStyle(el).textTransform)).toBe('none');
      expect(await label.evaluate(el => getComputedStyle(el).letterSpacing)).toBe('normal');
    }
    for (const english of LISTED_ENGLISH) {
      expect(mainText.split('\n').map(line => line.trim()), `Setup page shows "${english}"`).not.toContain(english);
      expect(sidebarText.split('\n').map(line => line.trim()), `sidebar shows "${english}"`).not.toContain(english);
    }
    for (const arabic of ['نظرة على المجموعة', 'التحليلات والتقارير', 'السياسات الضريبية', 'ملفات التأمينات الاجتماعية', 'فروقات التشغيل الموازي']) {
      expect(sidebarText, `sidebar shows «${arabic}»`).toContain(arabic);
    }
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/sidebar-ar-${info.project.name}.png` });
  });

  for (const arabic of [false, true]) {
    test(`the ⌘K palette describes access in words, not permission keys (${arabic ? 'ar' : 'en'})`, async ({ page }) => {
      await boot(page, '/setup?tab=companies&companyId=fixture-company', arabic);
      const lang = arabic ? 'ar' : 'en';
      await page.getByRole('button', { name: translate(lang, 'Import CSV') }).first().waitFor();
      await page.keyboard.press('ControlOrMeta+k');
      const input = page.getByPlaceholder(translate(lang, 'Search modules, employees, reports…'));
      await expect(input).toBeVisible();
      await input.fill('payroll');
      const palette = page.locator('div.fixed.inset-0.z-50');
      await expect(palette).toContainText(arabic ? 'يتطلب صلاحية الرواتب' : 'Needs payroll access');
      await expect(palette).not.toContainText('payroll.read');
      await expect(palette).not.toContainText('Requires');
    });
  }
});
