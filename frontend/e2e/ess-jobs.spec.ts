import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';

/**
 * Self-Service "Open positions": the read-only internal job board, against a mocked API (nothing is read or written).
 * Cases: the list, the debounced search, server-side paging with the server's own total, the detail panel (requirements and
 * responsibilities as lists), the empty, filtered-empty, error-then-retry and unlinked-login states, and that nothing on the
 * screen offers a way to respond to a position. English and Arabic, at 1366x768 and 390x844.
 * Run: npx playwright test -c e2e/playwright.ess-jobs.config.ts   (ESS_JOBS_SHOTS=<dir> also saves screenshots)
 */

const TITLES = ['Payroll Analyst', 'Site Engineer', 'HR Business Partner', 'Warehouse Supervisor', 'Finance Controller'];
const job = (n: number, over: Record<string, unknown> = {}) => ({
  id: `job-${n}`,
  jobCode: `JOB-${String(n).padStart(3, '0')}`,
  title: n <= 5 ? TITLES[n - 1] : `Position ${n}`,
  departmentName: ['Finance', 'Operations', 'People', 'Logistics'][n % 4],
  designationTitle: 'Senior Specialist',
  employmentType: n % 2 ? 'Full-Time' : 'Contract',
  location: ['Riyadh', 'Jeddah', 'Dammam'][n % 3],
  description: 'Own the monthly cycle end to end.\nWork with the finance team.',
  requirements: 'Bachelor degree in a related field\r\n- 3 years of experience\n\nFluent Arabic and English',
  responsibilities: '• Prepare and review the monthly run\nAnswer employee questions\nKeep the records audit-ready',
  publishedAtUtc: `2026-11-${String(1 + (n % 17)).padStart(2, '0')}T08:00:00Z`,
  ...over,
});
const ALL = Array.from({ length: 60 }, (_, i) => job(i + 1));

interface Boot {
  locale?: 'en' | 'ar';
  jobs?: ReturnType<typeof job>[];
  /** Answer the list with this status instead of the rows, for as long as the returned `control.failing` is true. */
  listStatus?: number;
  listBody?: unknown;
  detail?: (id: string) => { status: number; body: unknown } | null;
}

async function boot(page: Page, opts: Boot = {}) {
  const jobs = opts.jobs ?? ALL;
  const listCalls: Record<string, string>[] = [];
  const detailCalls: string[] = [];
  const writes: string[] = [];
  const errors: string[] = [];
  const control = { failing: true };
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript((locale) => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', locale); }, opts.locale ?? 'en');
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (request.method() !== 'GET') writes.push(`${request.method()} ${path}`);
    if (path === '/api/auth/me') return json({ id: 'u1', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Nora Alharbi', roles: ['Employee'], permissions: ['ess.read', 'ess.write'], companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] });
    if (path === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return json(['release_a', 'learning', 'succession', 'engagement', 'overtime']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/notifications') return json([]);
    if (path === '/api/ess/jobs') {
      listCalls.push(Object.fromEntries(url.searchParams.entries()));
      if (opts.listStatus && control.failing) {
        return json({ message: 'Your user account is not linked to an employee record.', messageAr: 'حسابك غير مرتبط بسجل موظف.' }, opts.listStatus);
      }
      if (opts.listBody !== undefined) return json(opts.listBody);
      const q = (url.searchParams.get('q') ?? '').toLowerCase();
      const page = Number(url.searchParams.get('page') ?? 1);
      const pageSize = Number(url.searchParams.get('pageSize') ?? 25);
      const matches = jobs.filter((j) => !q || j.title.toLowerCase().includes(q) || j.departmentName.toLowerCase().includes(q));
      return json({ items: matches.slice((page - 1) * pageSize, page * pageSize), total: matches.length, page, pageSize });
    }
    const one = /^\/api\/ess\/jobs\/([^/]+)$/.exec(path);
    if (one) {
      detailCalls.push(one[1]);
      const custom = opts.detail?.(one[1]);
      if (custom) return json(custom.body, custom.status);
      const found = jobs.find((j) => j.id === one[1]);
      return found ? json(found) : json({ message: 'Not found' }, 404);
    }
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto('/ess/jobs');
  return { listCalls, detailCalls, writes, errors, control };
}

async function shot(page: Page, name: string, locale: string) {
  const dir = process.env.ESS_JOBS_SHOTS;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.waitForTimeout(600); // let the drawer finish sliding in before it is photographed
  await page.screenshot({ path: `${dir}/${name}-${locale}-${page.viewportSize()!.width >= 1000 ? 'desktop' : 'phone'}.png` });
}

const noHorizontalScroll = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth);

for (const locale of ['en', 'ar'] as const) {
  const L = locale === 'en'
    ? {
      title: 'Open positions', search: /Search open positions/, searchInfo: /About Search open positions/, rangeFirst: 'Showing 1 to 25 of 60', rangeSecond: 'Showing 26 to 50 of 60', rangeLast: 'Showing 51 to 60 of 60',
      next: 'Next page', prev: 'Previous page', requirements: 'Requirements', responsibilities: 'Responsibilities', close: 'Close', retry: 'Retry',
      none: 'There are no open positions right now', clear: 'Clear search', unlinked: 'No employee profile for this login', unlinkedReason: 'Your user account is not linked to an employee record.',
      noLonger: /no longer open/, apply: /apply|application|submit/i, tab: 'Open positions',
    }
    : {
      title: 'الوظائف الشاغرة', search: /البحث في الوظائف الشاغرة/, searchInfo: /حول البحث في الوظائف الشاغرة|About/, rangeFirst: 'عرض 1 إلى 25 من 60', rangeSecond: 'عرض 26 إلى 50 من 60', rangeLast: 'عرض 51 إلى 60 من 60',
      next: 'الصفحة التالية', prev: 'الصفحة السابقة', requirements: 'المتطلبات', responsibilities: 'المسؤوليات', close: 'إغلاق', retry: 'إعادة المحاولة',
      none: 'لا توجد وظائف شاغرة حاليًا', clear: 'مسح البحث', unlinked: 'لا يوجد ملف موظف لهذا الحساب', unlinkedReason: 'حسابك غير مرتبط بسجل موظف.',
      noLonger: /لم تعد مفتوحة/, apply: /تقديم|تقدّم|أرسل/, tab: 'الوظائف الشاغرة',
    };
  const heading = (page: Page) => page.getByRole('heading', { level: 1, name: L.title });

  test.describe(`ess open positions (${locale})`, () => {
    test('the list shows title, department, location, type and a localised date, with the server total, and nothing to respond with', async ({ page }) => {
      const { listCalls, writes, errors } = await boot(page, { locale });
      await expect(heading(page)).toBeVisible();
      await expect(page.getByTestId('open-position-row')).toHaveCount(25);
      const first = page.getByTestId('open-position-row').first();
      await expect(first).toContainText('Payroll Analyst');
      await expect(first).toContainText('JOB-001');
      await expect(first).toContainText(/Operations|Logistics|People|Finance/);
      await expect(first).toContainText(/Jeddah|Riyadh|Dammam/);
      const dateText = (await first.innerText()).replace(/\s+/g, ' ');
      expect(dateText).toMatch(/2026/);
      expect(dateText).not.toMatch(/[٠-٩]/);
      await expect(page.getByTestId('open-positions-range')).toHaveText(L.rangeFirst);
      expect(listCalls[0]).toMatchObject({ page: '1', pageSize: '25' });
      expect(listCalls[0].q).toBeUndefined();
      // Read-only: no apply control anywhere on the page, no write request.
      await expect(page.getByRole('button', { name: L.apply })).toHaveCount(0);
      await expect(page.getByRole('link', { name: L.apply })).toHaveCount(0);
      expect(writes).toEqual([]);
      // Registered in the workspace: the tab is there and current.
      await expect(page.getByTestId('ess-workspace-nav').getByRole('link', { name: L.tab }).first()).toBeVisible();
      expect(await noHorizontalScroll(page)).toBe(true);
      expect(errors).toEqual([]);
      await shot(page, 'list', locale);
    });

    test('the i explains the search and is reachable by keyboard', async ({ page }) => {
      await boot(page, { locale });
      const info = page.getByRole('button', { name: L.searchInfo }).or(page.getByRole('button', { name: /^(More information|مزيد من المعلومات)/ })).first();
      await info.focus();
      await expect(page.getByRole('tooltip')).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByRole('tooltip')).toBeHidden();
    });

    test('search waits for a pause in typing, sends q, starts at page 1 and says what it found', async ({ page }) => {
      const { listCalls } = await boot(page, { locale });
      await expect(page.getByTestId('open-position-row')).toHaveCount(25);
      const before = listCalls.length;
      await page.getByTestId('open-positions-search').pressSequentially('payroll', { delay: 40 });
      await expect(page.getByTestId('open-position-row')).toHaveCount(1);
      const sent = listCalls.slice(before);
      expect(sent.map((c) => c.q)).toEqual(['payroll']);
      expect(sent[0].page).toBe('1');
      await expect(page.getByTestId('open-positions-range')).toContainText(locale === 'en' ? '1 to 1 of 1' : '1 إلى 1 من 1');
      await expect(page.getByTestId('open-positions-status')).toContainText(locale === 'en' ? '1 open position' : 'وظيفة شاغرة واحدة');
    });

    test('paging asks the server for each page and the range tells the truth on the first, middle and last', async ({ page }) => {
      const { listCalls } = await boot(page, { locale });
      const next = page.getByRole('button', { name: L.next });
      const prev = page.getByRole('button', { name: L.prev });
      await expect(prev).toBeDisabled();
      await next.click();
      await expect(page.getByTestId('open-positions-range')).toHaveText(L.rangeSecond);
      await expect(page.getByTestId('open-position-row').first()).toContainText('Position 26');
      expect(listCalls.at(-1)).toMatchObject({ page: '2', pageSize: '25' });
      await next.click();
      await expect(page.getByTestId('open-positions-range')).toHaveText(L.rangeLast);
      await expect(page.getByTestId('open-position-row')).toHaveCount(10);
      await expect(next).toBeDisabled();
      await prev.click();
      await expect(page.getByTestId('open-positions-range')).toHaveText(L.rangeSecond);
      await shot(page, 'page-2', locale);
    });

    test('a short list has no pager buttons and says how many there are', async ({ page }) => {
      await boot(page, { locale, jobs: ALL.slice(0, 7) });
      await expect(page.getByTestId('open-position-row')).toHaveCount(7);
      await expect(page.getByTestId('open-positions-range')).toHaveText(locale === 'en' ? 'Showing 1 to 7 of 7' : 'عرض 1 إلى 7 من 7');
      await expect(page.getByRole('button', { name: L.next })).toHaveCount(0);
    });

    test('opening a position shows its details, splits requirements and responsibilities into lists, and returns focus on close', async ({ page }) => {
      const { detailCalls, errors } = await boot(page, { locale });
      const row = page.getByTestId('open-position-row').first();
      await row.focus();
      await page.keyboard.press('Enter');
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog).toContainText('Payroll Analyst');
      await expect(dialog).toContainText('JOB-001');
      await expect(dialog).toContainText('Own the monthly cycle end to end.');
      await expect(dialog.getByRole('heading', { name: L.requirements })).toBeVisible();
      await expect(dialog.getByRole('heading', { name: L.responsibilities })).toBeVisible();
      await expect(page.getByTestId('open-position-requirements').getByRole('listitem')).toHaveText(['Bachelor degree in a related field', '3 years of experience', 'Fluent Arabic and English']);
      await expect(page.getByTestId('open-position-responsibilities').getByRole('listitem')).toHaveText(['Prepare and review the monthly run', 'Answer employee questions', 'Keep the records audit-ready']);
      // No pay information, no way to respond.
      await expect(dialog).not.toContainText(/salary|SAR|راتب|ريال/i);
      await expect(dialog.getByRole('button', { name: L.apply })).toHaveCount(0);
      await expect(dialog.getByRole('link')).toHaveCount(0);
      expect([...new Set(detailCalls)]).toEqual(['job-1']);
      await shot(page, 'detail', locale);
      if ((page.viewportSize()?.width ?? 0) < 1000) expect(await dialog.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(row).toBeFocused();
      expect(errors).toEqual([]);
    });

    test('a position that was closed in the meantime says so instead of showing stale text', async ({ page }) => {
      await boot(page, { locale, detail: () => ({ status: 404, body: { message: 'Not found' } }) });
      await page.getByTestId('open-position-row').first().click();
      await expect(page.getByTestId('open-position-detail-problem')).toContainText(L.noLonger);
      await page.getByRole('dialog').getByRole('button', { name: L.close, exact: true }).click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
    });

    test('a position with no description, requirements or responsibilities still opens without empty headings', async ({ page }) => {
      await boot(page, { locale, jobs: [job(1, { description: '', requirements: '  \n ', responsibilities: '' })] });
      await page.getByTestId('open-position-row').first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('heading', { name: L.requirements })).toHaveCount(0);
      await expect(dialog.getByRole('heading', { name: L.responsibilities })).toHaveCount(0);
      await expect(dialog).toContainText(locale === 'en' ? 'No further details were added' : 'لم تُضف تفاصيل أخرى');
    });

    test('no open positions: a first-use empty state with one next step, and no pager', async ({ page }) => {
      await boot(page, { locale, jobs: [] });
      const empty = page.locator('[data-empty-state="first-use"]');
      await expect(empty).toContainText(L.none);
      await expect(empty.getByRole('link')).toHaveCount(1);
      await expect(empty.getByRole('link')).toHaveAttribute('href', '/ess');
      await expect(page.getByTestId('open-positions-pager')).toHaveCount(0);
      await shot(page, 'empty', locale);
    });

    test('a search with no match is a filtered empty state that clears the search', async ({ page }) => {
      const { listCalls } = await boot(page, { locale });
      await expect(page.getByTestId('open-position-row')).toHaveCount(25);
      await page.getByTestId('open-positions-search').fill('zzzz');
      const empty = page.locator('[data-empty-state="filtered"]');
      await expect(empty).toContainText('zzzz');
      await empty.getByRole('button', { name: L.clear }).click();
      await expect(page.getByTestId('open-position-row')).toHaveCount(25);
      await expect(page.getByTestId('open-positions-search')).toHaveValue('');
      expect(listCalls.at(-1)?.q).toBeUndefined();
    });

    test('a failed load is an error with a reason and Retry, never "no open positions"; Retry recovers', async ({ page }) => {
      const { control } = await boot(page, { locale, listStatus: 500 });
      const error = page.getByTestId('open-positions-error');
      await expect(error).toBeVisible();
      await expect(page.locator('[data-empty-state]')).toHaveCount(0);
      await expect(page.getByTestId('open-position-row')).toHaveCount(0);
      await shot(page, 'error', locale);
      control.failing = false;
      await error.getByRole('button', { name: L.retry }).click();
      await expect(page.getByTestId('open-position-row')).toHaveCount(25);
    });

    test('a reply that is not a page of positions is a failed load, not an empty list', async ({ page }) => {
      await boot(page, { locale, listBody: '<html>502 Bad Gateway</html>' });
      await expect(page.getByTestId('open-positions-error')).toBeVisible();
      await expect(page.locator('[data-empty-state]')).toHaveCount(0);
    });

    test('a login with no employee record gets an explanation, in its language, and no search box', async ({ page }) => {
      await boot(page, { locale, listStatus: 404 });
      const unlinked = page.getByTestId('open-positions-unlinked');
      await expect(unlinked).toContainText(L.unlinked);
      await expect(page.getByTestId('open-positions-unlinked-reason')).toHaveText(L.unlinkedReason);
      await expect(page.getByTestId('open-positions-search')).toHaveCount(0);
      await shot(page, 'unlinked', locale);
    });

    test('a role without self-service access sees what is blocked, with Retry, not an empty list', async ({ page }) => {
      await boot(page, { locale, listStatus: 403 });
      await expect(page.getByTestId('open-positions-error')).toContainText(locale === 'en' ? 'permission' : 'صلاح');
      await expect(page.locator('[data-empty-state]')).toHaveCount(0);
    });

    test('the page is laid out for the viewer: direction, language and no horizontal scroll', async ({ page }) => {
      await boot(page, { locale });
      await expect(page.getByTestId('open-position-row')).toHaveCount(25);
      await expect(page.locator('html')).toHaveAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');
      await expect(page.locator('html')).toHaveAttribute('lang', locale);
      expect(await noHorizontalScroll(page)).toBe(true);
      // The workspace tabs and the list scroll vertically only.
      const widest = await page.getByTestId('open-positions-list').evaluate((el) => el.scrollWidth - el.clientWidth);
      expect(widest).toBeLessThanOrEqual(1);
    });
  });
}
