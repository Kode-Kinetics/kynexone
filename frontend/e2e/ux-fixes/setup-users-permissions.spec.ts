import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { translate } from '../../src/i18n/translations';
import { ADMIN, boot, type ApiMocks } from '../setup-consolidation/fixture';

/**
 * Setup > Users and Setup > Permissions, planned for space (Screen Standard 1 and 2), at 1366x768 in English and Arabic,
 * every API call intercepted (nothing read or written):
 *
 *   Users        one slim pay-approval status line instead of a tall card, one toolbar row, a compact table:
 *                the first row starts above y=420 and at least six of 14 users are fully in view (it started near y=700 with 2 rows in view)
 *   Permissions  one sticky toolbar (filter, count, Collapse all / Expand all) and module cards side by side:
 *                at least two cards share a row at 1366 wide, and the toolbar stays in view while the list scrolls
 *
 * SPACE_SHOTS_DIR=<dir> also saves a screenshot per screen and language (the before/after evidence).
 */
const SHOTS = process.env.SPACE_SHOTS_DIR;
const LOCALES = ['en', 'ar'] as const;
type Locale = (typeof LOCALES)[number];
const COUNT_KEY = '{count, plural, one {# permission} other {# permissions}} in {modules, plural, one {# module} other {# modules}}';
const VIEWPORT = { width: 1366, height: 768 } as const;

async function shot(page: Page, screen: string, locale: Locale) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: `${SHOTS}/${process.env.SPACE_PHASE ?? 'run'}-${screen}-${locale}.png` });
}
const noSideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

const ROLE_NAMES = ['Admin', 'HR Manager', 'Finance Approver', 'Payroll Officer', 'Employee'];
const user = (n: number) => ({
  id: `user-${n}`, email: `person.number.${n}@fixture.test`, fullName: `Person Number ${n}`, phoneNumber: '', status: 'Active', isActive: true, isLocked: false,
  mustChangePassword: false, roles: n % 3 === 0 ? [ROLE_NAMES[n % 5], ROLE_NAMES[(n + 1) % 5], ROLE_NAMES[(n + 2) % 5]] : [ROLE_NAMES[n % 5]],
  accessMode: 'FullPortal', employeeId: n % 2 ? n : undefined, employeeName: n % 2 ? `Person Number ${n}` : null, employeeCode: n % 2 ? `E-${n}` : null,
  lastLoginAtUtc: '2026-11-18T09:30:00Z', createdAtUtc: '2026-01-01T00:00:00Z', mfaEnabled: n % 2 === 0,
});
const USERS = Array.from({ length: 14 }, (_, i) => user(i + 1));
const SETUP = {
  independentReviewerCount: 1, readyForNewHires: true, pendingCount: 46, checkedCount: 46, strandedCount: 0, hasMore: false,
  items: [{ employeeId: 41, name: 'Layla Al-Harbi', eligibleReviewers: [{ id: 'reviewer', name: 'Fatima Finance Approver' }] }],
};

const MODULES = ['Attendance', 'Employees', 'Leave', 'Loans', 'Organization', 'Payroll', 'Recruitment', 'Security'];
const PERMISSIONS = MODULES.flatMap(module => Array.from({ length: 8 }, (_, i) => ({
  id: `${module}-${i}`, key: `${module.toLowerCase()}.action_${i}`, module, description: `Allows the holder to perform action number ${i} in ${module}`,
})));

const usersMocks: ApiMocks = {
  '/api/access/users': { items: USERS, total: USERS.length, page: 1, pageSize: 20 },
  '/api/access/roles': ROLE_NAMES.map((name, i) => ({ id: `role-${i}`, name, description: name, isSystemRole: true, isActive: true, authorityLevel: i + 1, permissions: [] })),
  '/api/access/permissions': PERMISSIONS,
  '/api/pay-approvals/setup': SETUP,
  '/api/tenant-admin/usage': { activeUsers: 14, maxUsers: 50, activeEmployees: 14, maxEmployees: 100, storageUsedMb: 1 },
};

for (const locale of LOCALES) {
  const t = (key: string, params?: Record<string, string | number>) => translate(locale, key, params);
  test.describe(`Setup users and permissions (${locale} ${VIEWPORT.width}x${VIEWPORT.height})`, () => {
    test.use({ viewport: VIEWPORT });

    test('Users: a slim pay-approval line, one toolbar row, and at least six users in view', async ({ page }) => {
      await boot(page, { persona: ADMIN, locale, url: '/setup?tab=users', mocks: usersMocks });
      const rows = page.locator('main table tbody tr');
      await expect(rows.first()).toBeVisible({ timeout: 60_000 });
      await expect(rows).toHaveCount(14);
      const status = page.getByRole('region', { name: t('Independent pay approval') });
      await expect(status.getByText(t('Pending pay packages: {count}', { count: 46 }))).toBeVisible({ timeout: 30_000 });
      await shot(page, 'setup-users', locale);

      // The pay-approval status is one line: its explanation sits behind Details, collapsed.
      const details = status.getByRole('button', { name: t('Details') });
      await expect(details).toHaveAttribute('aria-expanded', 'false');
      expect((await status.boundingBox())!.height, 'pay-approval status height').toBeLessThanOrEqual(80);
      await expect(status.getByRole('button', { name: t('Create User') }), 'the toolbar owns Create User').toHaveCount(0);

      // One toolbar row: search, status filter, refresh, Create User, Invite employee share a line.
      const toolbar = [
        page.getByPlaceholder(t('Search name or email…')),
        page.getByRole('combobox').first(),
        page.getByRole('button', { name: t('Create User'), exact: true }),
        page.getByRole('button', { name: t('Invite employee'), exact: true }),
      ];
      const tops = await Promise.all(toolbar.map(async control => Math.round((await control.boundingBox())!.y)));
      expect(Math.max(...tops) - Math.min(...tops), 'the toolbar controls start on one line (px apart)').toBeLessThanOrEqual(12);
      expect((await toolbar[1].boundingBox())!.width, 'status filter width').toBeLessThanOrEqual(240);

      const firstTop = await rows.first().evaluate(el => Math.round(el.getBoundingClientRect().top));
      expect(firstTop, 'first user row top').toBeLessThanOrEqual(420); // about 300 locally; 420 leaves room for the wider CI fallback font
      const inView = await rows.evaluateAll((trs, h) => trs.filter(r => r.getBoundingClientRect().bottom <= h).length, VIEWPORT.height);
      expect(inView, 'user rows fully in view').toBeGreaterThanOrEqual(6); // 8 or more locally, 6 under the wider CI fallback font
      const scroller = await page.locator('main table').first().evaluate(el => el.parentElement!.scrollWidth - el.parentElement!.clientWidth);
      expect(scroller, 'the users table scrolls sideways by (px)').toBeLessThanOrEqual(40);
      expect(await noSideways(page)).toBeLessThanOrEqual(0);

      // Details opens the explanation and the reviewer list the card always had.
      await details.click();
      await expect(details).toHaveAttribute('aria-expanded', 'true');
      await expect(status.getByText(t('Each pay package still needs a reviewer who did not enter or change it and is not its employee. Password resets do not approve pay or activate employment.'))).toBeVisible();
    });

    test('Permissions: one sticky toolbar and module cards side by side', async ({ page }) => {
      await boot(page, { persona: ADMIN, locale, url: '/setup?tab=permissions', mocks: usersMocks });
      const cards = page.getByTestId('permission-module');
      await expect(cards.first()).toBeVisible({ timeout: 60_000 });
      await expect(cards).toHaveCount(8);
      await shot(page, 'setup-permissions', locale);

      const toolbar = page.getByTestId('permissions-toolbar');
      await expect(toolbar.getByPlaceholder(t('Filter permissions…'))).toBeVisible();
      await expect(toolbar).toContainText(t(COUNT_KEY, { count: 64, modules: 8 }));
      await expect(toolbar.getByRole('button', { name: t('Collapse all') })).toBeVisible();

      // Cards sit in columns: at least two start at different horizontal offsets within the first screenful.
      const lefts = await cards.evaluateAll(els => els.map(el => Math.round(el.getBoundingClientRect().left)));
      expect(new Set(lefts).size, 'distinct card left offsets (columns)').toBeGreaterThanOrEqual(2);
      const rowsInView = await page.getByTestId('permission-row').evaluateAll((els, h) => els.filter(el => el.getBoundingClientRect().bottom <= h).length, VIEWPORT.height);
      expect(rowsInView, 'permissions fully in view').toBeGreaterThanOrEqual(20);
      expect(await noSideways(page)).toBeLessThanOrEqual(0);

      // The toolbar stays in view while the list scrolls.
      await page.mouse.move(VIEWPORT.width / 2, VIEWPORT.height / 2);
      await page.mouse.wheel(0, 900);
      await expect.poll(() => page.evaluate(() => window.scrollY + (document.querySelector('main')?.scrollTop ?? 0))).toBeGreaterThan(200);
      const box = (await toolbar.boundingBox())!;
      expect(box.y, 'toolbar top while scrolled').toBeGreaterThanOrEqual(-1);
      expect(box.y, 'toolbar top while scrolled').toBeLessThan(200);

      // Collapse all folds every card to its header; Expand all opens them again; nothing is remembered.
      await toolbar.getByRole('button', { name: t('Collapse all') }).click();
      await expect(page.locator('[data-testid="permission-row"]:visible')).toHaveCount(0);
      await toolbar.getByRole('button', { name: t('Expand all') }).click();
      await expect(page.locator('[data-testid="permission-row"]:visible')).toHaveCount(64);

      // The filter narrows the list and the count follows.
      await toolbar.getByPlaceholder(t('Filter permissions…')).fill('payroll.');
      await expect(page.locator('[data-testid="permission-row"]:visible')).toHaveCount(8);
      await expect(toolbar).toContainText(t(COUNT_KEY, { count: 8, modules: 1 }));
    });
  });
}
