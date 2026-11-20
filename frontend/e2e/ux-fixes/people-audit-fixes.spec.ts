import { expect, test, type Page } from '@playwright/test';
import { bootPeopleFixture } from '../people-list.fixture';

/**
 * People list and shared chrome, from the UI audit: hit areas of at least 24 px on the list's links and checkboxes,
 * 32 px icon buttons in the header and footer, 28 px sidebar section headers, a skip link as the first tab stop,
 * a visible focus ring on the scrollable list, the Overview header that does not pretend to be a control, and a
 * page that says so (with Retry) when module state never arrives. Every API call is intercepted.
 *
 *   UX_FIXES_BASE_URL=http://127.0.0.1:<port> npx playwright test -c e2e/playwright.ux-fixes.config.ts people-audit-fixes
 */
const open = async (page: Page, locale: 'en' | 'ar' = 'en') => {
  const fixture = await bootPeopleFixture(page, { count: 14, locale });
  await page.goto('/people');
  await expect(page.locator('#employee-1000')).toBeVisible({ timeout: 45_000 });
  return fixture;
};

const size = async (page: Page, selector: string) => {
  const box = await page.locator(selector).first().boundingBox();
  expect(box, selector).not.toBeNull();
  return box!;
};

test.describe('hit areas', () => {
  test('employee name links and row checkboxes are at least 24 x 24', async ({ page }) => {
    await open(page);
    const link = await size(page, '#employee-1000');
    expect(link.height).toBeGreaterThanOrEqual(24);
    expect(link.width).toBeGreaterThanOrEqual(24);
    for (const label of ['Select all on this page', 'Select Employee 01 Al-Harbi']) {
      const box = await page.getByRole('checkbox', { name: label }).boundingBox();
      expect(box!.width, label).toBeGreaterThanOrEqual(24);
      expect(box!.height, label).toBeGreaterThanOrEqual(24);
    }
  });

  test('top bar search and sidebar sign out are at least 32 px; sidebar section headers at least 28 px high', async ({ page }) => {
    await open(page);
    const search = await page.getByRole('button', { name: 'Open command search' }).boundingBox();
    expect(search!.width).toBeGreaterThanOrEqual(32);
    expect(search!.height).toBeGreaterThanOrEqual(32);
    const signOut = await page.locator('aside').getByRole('button', { name: 'Sign out' }).boundingBox();
    expect(signOut!.width).toBeGreaterThanOrEqual(32);
    expect(signOut!.height).toBeGreaterThanOrEqual(32);
    const headers = page.locator('aside button[aria-controls^="sidebar-group-"]');
    expect(await headers.count()).toBeGreaterThan(0);
    for (let i = 0; i < await headers.count(); i++) {
      expect((await headers.nth(i).boundingBox())!.height).toBeGreaterThanOrEqual(28);
    }
  });
});

test.describe('keyboard', () => {
  test('the first Tab stop is a skip link that moves focus to the page content', async ({ page }) => {
    await open(page);
    await page.keyboard.press('Tab');
    const skip = page.getByTestId('skip-to-content');
    await expect(skip).toBeFocused();
    await expect(skip).toBeVisible();
    await expect(skip).toHaveText('Skip to main content');
    await page.keyboard.press('Enter');
    await expect(page.locator('#main-content')).toBeFocused();
  });

  test('the skip link is Arabic under Arabic and sits at the inline start', async ({ page }) => {
    await open(page, 'ar');
    await page.keyboard.press('Tab');
    const skip = page.getByTestId('skip-to-content');
    await expect(skip).toBeFocused();
    await expect(skip).toHaveText('انتقل إلى المحتوى الرئيسي');
    const box = await skip.boundingBox();
    const viewport = page.viewportSize()!;
    expect(box!.x + box!.width).toBeGreaterThan(viewport.width / 2);
  });

  test('the scrollable employee list shows a focus ring of at least 2 px when it has keyboard focus', async ({ page }) => {
    await open(page);
    const list = page.getByTestId('employee-list-scroll');
    for (let i = 0; i < 120; i++) {
      await page.keyboard.press('Tab');
      if (await list.evaluate((el) => el === document.activeElement)) break;
    }
    await expect(list).toBeFocused();
    const ring = await list.evaluate((el) => {
      const s = getComputedStyle(el);
      return { shadow: s.boxShadow, outlineWidth: parseFloat(s.outlineWidth), outlineStyle: s.outlineStyle };
    });
    const hasOutline = ring.outlineStyle !== 'none' && ring.outlineWidth >= 2;
    const hasRing = ring.shadow !== 'none' && /\b[2-9]px\b/.test(ring.shadow);
    expect(hasOutline || hasRing, JSON.stringify(ring)).toBe(true);
  });
});

test.describe('sidebar section headers', () => {
  test('Overview is a label, not a button that does nothing; the other sections announce and toggle', async ({ page }) => {
    await open(page);
    const nav = page.locator('aside nav');
    await expect(nav.getByRole('button', { name: /^Overview$/i })).toHaveCount(0);
    await expect(nav.getByText('Overview', { exact: true })).toBeVisible();
    // Its shortcuts stay reachable.
    await expect(nav.getByRole('button', { name: 'Dashboard' })).toBeVisible();
    const hr = nav.getByRole('button', { name: /HR & Time/i });
    await expect(hr).toHaveAttribute('aria-expanded', 'true');
    await hr.click();
    await expect(hr).toHaveAttribute('aria-expanded', 'false');
    await hr.click();
    await expect(hr).toHaveAttribute('aria-expanded', 'true');
  });
});

test.describe('module state never arrives', () => {
  test('after a wait the page says it could not load and Retry brings the list back', async ({ page }) => {
    test.setTimeout(120_000);
    await bootPeopleFixture(page, { count: 14 });
    // The module catalogue is the first thing the page waits for. Hold every request open (a stalled connection) until told.
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    await page.route('**/api/features/**', async (route) => {
      await gate;
      await route.fulfill({ json: [] }).catch(() => {});
    });
    await page.goto('/people');
    await expect(page.getByTestId('module-state-slow')).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole('heading', { name: 'This page could not be loaded.' })).toBeVisible();
    // Click Retry while the connection is still stalled: releasing first lets the original request answer, the page
    // recovers on its own and the button detaches mid-click (a race, not a Retry check).
    await page.getByRole('button', { name: 'Retry' }).click();
    release();
    await expect(page.locator('#employee-1000')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('module-state-slow')).toHaveCount(0);
  });
});

test.describe('Arabic on the People list', () => {
  test('the list, filters, table and shell caption are Arabic, with no English left in them', async ({ page }) => {
    await open(page, 'ar');
    await expect(page.getByRole('button', { name: 'إضافة موظف' })).toBeVisible();
    await expect(page.getByRole('tab', { name: 'الحالي' })).toBeVisible();
    await expect(page.getByRole('tab', { name: 'الموظفون السابقون' })).toBeVisible();
    await expect(page.getByText('عدد سجلات الموظفين: 14')).toBeVisible();
    await expect(page.getByRole('checkbox', { name: 'تحديد كل ما في هذه الصفحة' })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'تصفية الحالة' })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'القسم' })).toBeVisible();
    await expect(page.getByText('منصة إدارة القوى العاملة')).toBeVisible();
    await expect(page.getByText('صفحة 1 من 1')).toBeVisible();
    const table = page.getByTestId('employee-list-scroll');
    for (const english of ['Department', 'Designation', 'Branch', 'Status', 'Profile', 'Prev', 'Next']) {
      await expect(table.getByText(english, { exact: true }), english).toHaveCount(0);
    }
    const header = page.locator('h1').first().locator('xpath=..');
    await expect(header).not.toContainText('employee records');
    await expect(page.getByText('Workforce Platform')).toHaveCount(0);
  });
});
