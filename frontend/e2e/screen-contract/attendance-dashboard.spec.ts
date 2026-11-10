import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { translate } from '../../src/i18n/translations';
import { LOCALES, type Locale } from './contract';
import { KIOSK_PERSONA, dailyRows, openAttendance } from './attendance-mocks';

/**
 * Attendance Live Dashboard, held to the Screen Standard beyond the shared contract (purpose line, one primary action,
 * reflow: screen-contract.spec.ts). Every API call is intercepted. Each test names the rule it holds:
 *   space      the daily table takes the full width at 1366 and never scrolls sideways (standard 1, example B-1)
 *   recording  the header's one primary action opens the record panel; the panel stays mounted (a run in progress survives)
 *   guidance   the punch fields have visible labels and an `i` (3b)
 *   empty      filtered / first-use / no-devices each hold exactly one action (3c, 8e)
 *   result     a saved punch lands on a ResultBanner with a next action (3d); a refusal says what to do (3e)
 *   a11y       axe has no serious or critical violation (12)
 *   phone      under 640 px no risk signal is hidden: a card list carries Missing punch and late (S1, S6)
 *   filters    the four KPI tiles are filter buttons and announce the result count (S4)
 *   panel      closed on load, one primary at a time, Close/Esc return focus, a run in progress stays visible when closed
 *   reflow     the open record panel reflows to one column at 320 px
 *   bidi       data text is isolated so Arabic and English mix without scrambling
 */
const t = (locale: Locale, key: string, params?: Record<string, string | number>) => translate(locale, key, params);

const VIEWPORTS = { desktop: { width: 1366, height: 768 }, phone: { width: 320, height: 640 } } as const;

async function tableMetrics(page: Page) {
  return page.evaluate(() => {
    const host = document.querySelector('[data-daily-table]');
    const table = host?.querySelector('table');
    const scroller = table?.parentElement;
    const rows = [...(table?.querySelectorAll('tbody tr') ?? [])];
    const first = rows[0]?.getBoundingClientRect();
    return {
      hostWidth: host ? host.getBoundingClientRect().width : 0,
      overflow: scroller ? scroller.scrollWidth - scroller.clientWidth : -1,
      rows: rows.length,
      firstRowBottom: first ? first.bottom : -1,
      visibleRows: rows.filter(r => r.getBoundingClientRect().bottom <= window.innerHeight).length,
    };
  });
}

const RESULTS_KEY = '{count, plural, one {# result} other {# results}}';
const header = (page: Page) => page.locator('[data-page-header] [data-primary-action] button');
const tilesOf = (page: Page, locale: Locale) => page.getByRole('group', { name: t(locale, 'Attendance at a glance') });
const recordRegion = (page: Page, locale: Locale) => page.getByRole('region', { name: t(locale, 'Record attendance') });
const TZ_MOCK = { '/api/tenant-admin/localization': { countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh', stated: true } };

for (const locale of LOCALES) {
  test.describe(`attendance dashboard (${locale})`, () => {
    test('space: at 1366 the daily table is at least 900 px wide, does not scroll sideways, and its first row is above the fold', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      const m = await tableMetrics(page);
      expect(m.hostWidth, 'table width').toBeGreaterThanOrEqual(900);
      expect(m.overflow, 'table scrolls sideways by (px)').toBeLessThanOrEqual(0);
      expect(m.firstRowBottom, 'first row bottom edge').toBeLessThanOrEqual(VIEWPORTS.desktop.height);
      expect(m.visibleRows, 'rows visible without scrolling').toBeGreaterThanOrEqual(12);
    });

    test('space: at 320 the table has no horizontal scroll and no document overflow', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.phone);
      await openAttendance(page, locale);
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      const m = await tableMetrics(page);
      expect(m.overflow, 'table scrolls sideways by (px)').toBeLessThanOrEqual(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
    });

    test('recording: the header primary opens the record panel (closed until then) and the panel keeps its state when closed again', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      const primary = page.locator('[data-page-header] [data-primary-action] button');
      await expect(primary).toHaveText(t(locale, 'Record attendance'));
      await expect(primary).toHaveAttribute('aria-expanded', 'false');
      const region = page.getByRole('region', { name: t(locale, 'Record attendance') });
      await expect(region, 'closed until asked for').toBeHidden();
      await primary.click();
      await expect(primary).toHaveAttribute('aria-expanded', 'true');
      await expect(region).toBeVisible();
      const reason = region.getByLabel(t(locale, 'Reason (required)'));
      await reason.fill('Device offline');
      await primary.click();
      await expect(region).toBeHidden();
      await primary.click();
      await expect(reason, 'the entry survives closing the panel').toHaveValue('Device offline');
    });

    test('guidance: the plain punch form labels its fields and explains source and direction with an i', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale, { persona: KIOSK_PERSONA });
      await page.locator('[data-page-header] [data-primary-action] button').click();
      const region = page.getByRole('region', { name: t(locale, 'Record attendance') });
      for (const label of ['Punch source', 'Punch direction']) {
        await expect(region.locator('label', { hasText: t(locale, label) }), `${label} has a visible label`).toBeVisible();
        await expect(region.getByRole('button', { name: t(locale, 'About {field}', { field: t(locale, label) }) }), `${label} has an i`).toBeVisible();
      }
    });

    test('empty: a status filter that matches nothing shows one action, Clear filters, which restores the rows', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale, { rows: status => (status ? [] : dailyRows()) });
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      await page.getByLabel(t(locale, 'Status filter')).selectOption('Late');
      const empty = page.locator('[data-daily-table] [data-empty-state="filtered"]');
      await expect(empty).toBeVisible();
      await expect(empty.locator('button, a')).toHaveCount(1);
      await expect(empty.locator('button')).toHaveText(t(locale, 'Clear filters'));
      await empty.locator('button').click();
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
    });

    test('empty: a day with no attendance shows one action, Record attendance, which opens the record panel', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale, { rows: [] });
      const empty = page.locator('[data-daily-table] [data-empty-state="first-use"]');
      await expect(empty).toBeVisible();
      await expect(empty.locator('button, a')).toHaveCount(1);
      await empty.locator('button').click();
      await expect(page.getByRole('region', { name: t(locale, 'Record attendance') })).toBeVisible();
    });

    test('empty: no devices shows one action, Add a device, that leads to the devices settings', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale, { devices: [] });
      const empty = page.locator('[data-empty-state]', { hasText: t(locale, 'No devices connected') });
      await expect(empty).toBeVisible();
      await expect(empty.locator('button, a')).toHaveCount(1);
      await expect(empty.locator('a')).toHaveAttribute('href', /\/setup\?tab=attendanceDevices/);
    });

    test('result: a saved punch ends on a ResultBanner in the live region with one next action; the page does not reload', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      const { writes } = await openAttendance(page, locale, { persona: KIOSK_PERSONA, extra: { '/api/attendance/punch/web': { id: 'raw-1' } } });
      await page.locator('[data-page-header] [data-primary-action] button').click();
      const region = page.getByRole('region', { name: t(locale, 'Record attendance') });
      await region.getByLabel(t(locale, 'Employee')).selectOption({ index: 1 });
      await region.getByRole('button', { name: t(locale, 'Save Punch') }).click();
      const result = page.locator('[role="status"][data-result]');
      await expect(result).toBeVisible();
      await expect(result).toContainText(t(locale, 'Punch saved to live attendance events.'));
      await expect(result.locator('[data-result-next] button')).toHaveCount(1);
      await expect(result.locator('[data-result-next] button'), 'the panel is already open, so no Record another').toHaveText(t(locale, 'Review records'));
      expect(writes).toContain('POST /api/attendance/punch/web');
    });

    test('result: a refused punch says what happened in words, never the raw HTTP text', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale, { persona: KIOSK_PERSONA, extra: { '/api/attendance/punch/web': { status: 403, body: {} } } });
      await page.locator('[data-page-header] [data-primary-action] button').click();
      const region = page.getByRole('region', { name: t(locale, 'Record attendance') });
      await region.getByLabel(t(locale, 'Employee')).selectOption({ index: 1 });
      await region.getByRole('button', { name: t(locale, 'Save Punch') }).click();
      const alert = page.locator('main [role="alert"]').filter({ hasText: /\S/ }).first();
      await expect(alert).toBeVisible();
      await expect(alert).not.toContainText('status code');
      await expect(alert).toContainText(t(locale, 'error.forbidden'));
      await expect(alert, 'the alert takes focus').toBeFocused();
      await expect(alert, 'what happened').toContainText(t(locale, 'The punch was not saved'));
      await expect(alert, 'what to do').toContainText(t(locale, 'Ask your HR administrator to grant you access.'));
    });

    test('rows: the Date column is gone (one date is filtered), department sits inline with the name, rows are about 40 px', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      const heads = await page.locator('[data-daily-table] thead th').allTextContents();
      expect(heads.map(h => h.trim().toLowerCase())).not.toContain(t(locale, 'Date').toLowerCase());
      const row = await page.locator('[data-daily-table] tbody tr').first().boundingBox();
      expect(row!.height, 'row height').toBeLessThanOrEqual(42);
      const cell = page.locator('[data-daily-table] tbody tr').first().locator('td').first();
      expect(await cell.locator('p').count(), 'department is inline, not a second paragraph').toBe(0);
    });

    test('phone: under 640 px every record is a card with its times, status, Missing punch and late minutes; at 640 the table returns', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.phone);
      await openAttendance(page, locale);
      const cards = page.locator('[data-daily-cards] > li');
      await expect(cards).toHaveCount(14);
      await expect(page.locator('[data-daily-table] table')).toBeHidden();
      const noura = cards.filter({ hasText: 'Noura Al-Qahtani' });
      await expect(noura).toContainText(t(locale, 'Missing punch'));
      await expect(noura).toContainText(t(locale, 'Present'));
      await expect(cards.filter({ hasText: 'Layla Hassan' })).toContainText(t(locale, '{minutes}m late', { minutes: 17 }));
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
      await page.setViewportSize({ width: 640, height: 800 });
      await expect(page.locator('[data-daily-table] table')).toBeVisible();
      await expect(page.locator('[data-daily-cards]')).toBeHidden();
    });

    test('filters: each KPI tile asks the server for its own records (tile=, never status=), and the live region announces the count', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      const dailyQueries: URLSearchParams[] = [];
      page.on('request', req => { const u = new URL(req.url()); if (u.pathname === '/api/attendance/daily') dailyQueries.push(u.searchParams); });
      await openAttendance(page, locale);
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      const tiles = page.getByRole('group', { name: t(locale, 'Attendance at a glance') });
      const status = page.getByLabel(t(locale, 'Status filter'));
      const live = page.locator('main [role="status"]').filter({ hasText: /\S/ });
      const expected: Array<[string, string, number]> = [['Present', 'present', 12], ['Absent', 'absent', 2], ['Late', 'late', 3], ['Missing Punch', 'missingPunch', 2]];
      for (const [label, tile, count] of expected) {
        dailyQueries.length = 0;
        const button = tiles.getByRole('button', { name: new RegExp(t(locale, label)) });
        await button.click();
        await expect(button).toHaveAttribute('aria-pressed', 'true');
        await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(count);
        await expect(live.first()).toContainText(t(locale, RESULTS_KEY, { count }));
        const sent = dailyQueries.at(-1)!;
        expect(sent.get('tile'), `${label} tile sends tile`).toBe(tile);
        expect(sent.has('status'), `${label} tile does not send status`).toBe(false);
        await expect(status, 'a tile leaves the status dropdown alone').toHaveValue('');
        await button.click();
        await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
        expect(dailyQueries.at(-1)!.has('tile'), 'unpressing clears the tile').toBe(false);
      }
    });

    test('filters: the status dropdown still sends status, and the tile and the dropdown clear each other', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      const dailyQueries: URLSearchParams[] = [];
      page.on('request', req => { const u = new URL(req.url()); if (u.pathname === '/api/attendance/daily') dailyQueries.push(u.searchParams); });
      await openAttendance(page, locale);
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      const tiles = page.getByRole('group', { name: t(locale, 'Attendance at a glance') });
      const status = page.getByLabel(t(locale, 'Status filter'));
      const missing = tiles.getByRole('button', { name: new RegExp(t(locale, 'Missing Punch')) });
      await missing.click();
      await expect(missing).toHaveAttribute('aria-pressed', 'true');
      dailyQueries.length = 0;
      await status.selectOption('Late');
      await expect(missing).toHaveAttribute('aria-pressed', 'false');
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(3);
      expect(dailyQueries.at(-1)!.get('status')).toBe('Late');
      expect(dailyQueries.at(-1)!.has('tile'), 'selecting a status clears the tile').toBe(false);
      await tiles.getByRole('button', { name: new RegExp(t(locale, 'Present')) }).click();
      await expect(status).toHaveValue('');
      expect(dailyQueries.at(-1)!.get('tile')).toBe('present');
      expect(dailyQueries.at(-1)!.has('status'), 'pressing a tile clears the status').toBe(false);
    });

    test('filters: Missing Punch is the server list, not a client filter of the first page', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      // The server answers tile=missingPunch with a record the first page of the day never contained.
      const firstPage = dailyRows().map(r => ({ ...r, missingPunch: false }));
      const beyond = { ...dailyRows(1)[0], id: 'd-beyond', employeeName: 'Zed Beyond', missingPunch: true };
      await openAttendance(page, locale, {
        extra: {
          '/api/attendance/daily': (url: URL) => {
            const items = url.searchParams.get('tile') === 'missingPunch' ? [beyond] : firstPage;
            return { items, total: items.length, page: 1, pageSize: 50 };
          },
        },
      });
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      await tilesOf(page, locale).getByRole('button', { name: new RegExp(t(locale, 'Missing Punch')) }).click();
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(1);
      await expect(page.locator('[data-daily-table] tbody')).toContainText('Zed Beyond');
    });

    test('panel: it stays closed on load; open, the header button reads Close (secondary) so the panel holds the only primary', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      await expect(recordRegion(page, locale)).toBeHidden();
      await header(page).click();
      await expect(header(page)).toHaveText(t(locale, 'Close'));
      await expect(header(page)).toHaveClass(/btn-secondary/);
      await expect(header(page)).toHaveAttribute('aria-expanded', 'true');
      await expect(page.locator('main .btn-primary:visible'), 'one primary in view: the panel submit').toHaveCount(1);
      await expect(recordRegion(page, locale).locator('.btn-primary')).toHaveCount(1);
    });

    test('panel: the ghost Close in its header and Esc both close it and return focus to the header button', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await header(page).click();
      const region = recordRegion(page, locale);
      await region.getByRole('button', { name: t(locale, 'Close') }).click();
      await expect(region).toBeHidden();
      await expect(header(page)).toBeFocused();
      await expect(header(page)).toHaveText(t(locale, 'Record attendance'));
      await header(page).click();
      await region.getByLabel(t(locale, 'Reason (required)')).focus();
      await page.keyboard.press('Escape');
      await expect(region).toBeHidden();
      await expect(header(page)).toBeFocused();
    });

    test('panel: a mass-entry run in progress shows "Recording n of m" in the header while closed, and the chip reopens the panel', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await page.route('**/api/attendance/events/bulk-entry', async route => {
        await new Promise(resolve => setTimeout(resolve, 4000));
        await route.fulfill({ json: { requested: 14, created: 14, duplicates: 0, failed: 0, errors: [] } });
      });
      await header(page).click();
      const region = recordRegion(page, locale);
      await region.getByRole('button', { name: t(locale, 'Select all shown') }).click();
      await region.getByLabel(t(locale, 'Reason (required)')).fill('Device offline');
      await expect(page.locator('[data-page-header] [role="status"]'), 'mounted before the chip').toHaveCount(1);
      await region.getByRole('button', { name: /×/ }).click();
      await header(page).click();
      await expect(region).toBeHidden();
      const live = page.locator('[data-page-header] [role="status"]');
      const chip = page.locator('[data-page-header]').getByRole('button', { name: /14/ });
      await expect(chip).toBeVisible();
      await expect(live, 'the run is announced through that same region').toContainText(/14/);
      await expect(chip.locator('[data-essential-motion]'), 'the spinner is essential motion').toHaveCount(1);
      await chip.click();
      await expect(region).toBeVisible();
      await expect(chip, 'gone once the run has finished').toHaveCount(0, { timeout: 15_000 });
    });

    test('guidance: the time zone clause is dropped when the company has none, and names the zone when it has', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await header(page).click();
      await expect(recordRegion(page, locale)).toContainText(t(locale, 'Enter an in time, an out time, or both.'));
      await expect(recordRegion(page, locale)).not.toContainText(t(locale, 'Times are in {zone}.', { zone: '' }).replace(/\s*\.$/, ''));
      const zoned = await page.context().newPage();
      await zoned.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(zoned, locale, { extra: TZ_MOCK });
      await header(zoned).click();
      await expect(recordRegion(zoned, locale)).toContainText(t(locale, 'Times are in {zone}.', { zone: 'Asia/Riyadh' }));
    });

    test('reflow: at 320 the open record panel is one column, dates fit, controls are 44 px and employee rows stack name over code and title', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.phone);
      await openAttendance(page, locale);
      await header(page).click();
      const region = recordRegion(page, locale);
      await expect(region).toBeVisible();
      await expect(region.locator('[data-employee-name]').first()).toBeVisible();
      const from = await region.getByLabel(t(locale, 'From date')).boundingBox();
      const to = await region.getByLabel(t(locale, 'To date')).boundingBox();
      expect(from!.x).toBeGreaterThanOrEqual(0);
      expect(from!.x + from!.width, 'From date inside the viewport').toBeLessThanOrEqual(320);
      expect(to!.x + to!.width, 'To date inside the viewport').toBeLessThanOrEqual(320);
      expect(to!.y, 'one column: To date under From date').toBeGreaterThan(from!.y + from!.height - 1);
      expect(from!.height, 'date control height').toBeGreaterThanOrEqual(44);
      const name = await region.locator('[data-employee-name]').first().boundingBox();
      const meta = await region.locator('[data-employee-meta]').first().boundingBox();
      expect(meta!.y, 'code and title under the name').toBeGreaterThanOrEqual(name!.y + name!.height - 1);
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
    });

    test('bidi: the Missing chip says what is missing, data text is isolated, and the assistant button does not cover the submit', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
      await expect(page.locator('[data-daily-table] tbody')).toContainText(t(locale, 'Missing punch'));
      await expect(page.locator('[data-daily-table] tbody')).toContainText(t(locale, 'Complete'));
      if (locale === 'ar') await expect(page.locator('[data-daily-table] tbody')).not.toContainText('مسح');
      expect(await page.locator('[data-daily-table] tbody tr td:first-child bdi').count()).toBeGreaterThanOrEqual(14);
      await expect(page.locator('main [dir="auto"]', { hasText: 'Late arrivals up in Operations' })).toHaveCount(1);
      await expect(page.locator('main bdi', { hasText: 'ZKTeco' }).first()).toBeVisible();
      await header(page).click();
      const submit = recordRegion(page, locale).locator('.btn-primary');
      await submit.evaluate(el => (el.closest('[data-submit-zone]') ?? el).scrollIntoView({ block: 'nearest' }));
      const box = await submit.boundingBox();
      const covered = await page.evaluate(({ x, y }) => {
        const el = document.elementFromPoint(x, y);
        return !!el && !el.closest('#attendance-record-panel');
      }, { x: box!.x + box!.width / 2, y: box!.y + box!.height / 2 });
      expect(covered, 'something floats over the submit button').toBe(false);
    });

    test('result: a finished mass entry lands on a ResultBanner (what, where, one next action) that takes focus', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await page.route('**/api/attendance/events/bulk-entry', route => route.fulfill({ json: { requested: 1, created: 1, duplicates: 0, failed: 0, errors: [] } }));
      await header(page).click();
      const region = recordRegion(page, locale);
      await region.getByRole('button', { name: t(locale, 'Select all shown') }).click();
      await region.getByLabel(t(locale, 'Reason (required)')).fill('Device offline');
      await region.getByRole('button', { name: /×/ }).click();
      const result = region.locator('[data-result]');
      await expect(result).toBeVisible({ timeout: 20_000 });
      await expect(result.locator('[data-result-changed]')).toContainText('14');
      await expect(result.locator('[data-result-where]')).toBeVisible();
      await expect(result.locator('[data-result-next] button, [data-result-next] a')).toHaveCount(1);
      await expect(result.locator('[data-result-next] button')).toHaveText(t(locale, 'Process now'));
      await expect(result, 'focus moves to the banner').toBeFocused();
    });

    test('guidance: with no company time zone the In-time help does not claim one', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await header(page).click();
      const region = recordRegion(page, locale);
      await region.getByRole('button', { name: t(locale, 'About {field}', { field: t(locale, 'In time') }) }).click();
      const tip = region.getByRole('tooltip').filter({ hasText: /\S/ }).first();
      await expect(tip).toBeVisible();
      await expect(tip).not.toContainText(locale === 'ar' ? 'توقيت الشركة' : 'company time zone');
      const zoned = await page.context().newPage();
      await zoned.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(zoned, locale, { extra: TZ_MOCK });
      await header(zoned).click();
      await recordRegion(zoned, locale).getByRole('button', { name: t(locale, 'About {field}', { field: t(locale, 'In time') }) }).click();
      await expect(recordRegion(zoned, locale).getByRole('tooltip').filter({ hasText: /\S/ }).first()).toContainText(locale === 'ar' ? 'توقيت الشركة' : 'company time zone');
    });

    test('phone: the first record starts high (date and status share a row, Refresh is an icon) and the KPI tiles are 44 px', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.phone);
      await openAttendance(page, locale);
      await expect(page.locator('[data-daily-cards] > li')).toHaveCount(14);
      const first = await page.locator('[data-daily-cards] > li').first().boundingBox();
      expect(first!.y, 'first record top (px)').toBeLessThanOrEqual(520);
      const date = await page.getByLabel(t(locale, 'Attendance date')).boundingBox();
      const status = await page.getByLabel(t(locale, 'Status filter')).boundingBox();
      expect(Math.abs(date!.y - status!.y), 'date and status on one row').toBeLessThanOrEqual(2);
      const refresh = page.getByRole('button', { name: t(locale, 'Refresh') });
      const rb = await refresh.boundingBox();
      expect(rb!.width, 'Refresh is an icon button').toBeLessThanOrEqual(48);
      expect(rb!.height).toBeGreaterThanOrEqual(44);
      for (const tile of await page.getByRole('group', { name: t(locale, 'Attendance at a glance') }).getByRole('button').all()) {
        expect((await tile.boundingBox())!.height, 'KPI tile height').toBeGreaterThanOrEqual(44);
      }
    });

    test('rows: someone with no punches is not "Complete": the table and card say No punches', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      const huda = page.locator('[data-daily-table] tbody tr', { hasText: 'Huda Saleh' });
      await expect(huda).toContainText(t(locale, 'No punches'));
      await expect(huda).not.toContainText(t(locale, 'Complete'));
      await page.setViewportSize(VIEWPORTS.phone);
      const card = page.locator('[data-daily-cards] > li', { hasText: 'Huda Saleh' });
      await expect(card).toContainText(t(locale, 'No punches'));
    });

    test('filters: a pressed KPI tile shows a check and the group says it filters', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      const tiles = page.getByRole('group', { name: t(locale, 'Attendance at a glance') });
      await expect(tiles).toHaveAccessibleDescription(t(locale, 'Select a count to show only those records.'));
      const late = tiles.getByRole('button', { name: new RegExp(t(locale, 'Late')) });
      await expect(late.locator('[data-pressed-check]')).toHaveCount(0);
      await late.click();
      await expect(late.locator('[data-pressed-check]')).toHaveCount(1);
      const gap = await tiles.evaluate(el => parseFloat(getComputedStyle(el).columnGap));
      expect(gap).toBeGreaterThanOrEqual(8);
    });

    test('panel: at 320 the header Close is stacked above the title so the title keeps the full width', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.phone);
      await openAttendance(page, locale);
      await header(page).click();
      const region = recordRegion(page, locale);
      const title = await region.getByRole('heading', { name: t(locale, 'Record attendance') }).boundingBox();
      expect(title!.width, 'title width').toBeGreaterThanOrEqual(200);
    });

    test('tabs: when the tab strip scrolls (phone) a cue shows at the clipped edge', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.phone);
      await openAttendance(page, locale);
      await expect(page.locator('[data-tabs-cue]').first()).toBeVisible();
      await page.setViewportSize(VIEWPORTS.desktop);
      await expect(page.locator('[data-tabs-cue]')).toHaveCount(0);
    });

    test('tabs: the scroll cue sits in its own gutter, outside the scrolling strip, so it never covers a tab label', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.phone);
      await openAttendance(page, locale);
      const cues = page.locator('[data-tabs-cue]');
      await expect(cues.first()).toBeVisible();
      const scroller = await page.locator('[role=tablist]').first().evaluate((el) => {
        let n: HTMLElement | null = el as HTMLElement;
        while (n && getComputedStyle(n).overflowX !== 'auto' && getComputedStyle(n).overflowX !== 'scroll') n = n.parentElement;
        return n ? n.getBoundingClientRect().toJSON() : null;
      });
      expect(scroller, 'scrolling strip').toBeTruthy();
      for (const box of await cues.evaluateAll((els) => els.map((e) => e.getBoundingClientRect().toJSON()))) {
        const overlapsX = box.left < scroller!.right && box.right > scroller!.left;
        expect(overlapsX, 'cue overlaps the scrolling strip').toBe(false);
      }
    });

    test('panel: the run chip sits in the action area and never wraps the purpose line (keeps 12 rows)', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.desktop);
      await openAttendance(page, locale);
      await page.route('**/api/attendance/events/bulk-entry', async route => {
        await new Promise(resolve => setTimeout(resolve, 3000));
        await route.fulfill({ json: { requested: 1, created: 1, duplicates: 0, failed: 0, errors: [] } });
      });
      await header(page).click();
      const region = recordRegion(page, locale);
      await region.getByRole('button', { name: t(locale, 'Select all shown') }).click();
      await region.getByLabel(t(locale, 'Reason (required)')).fill('Device offline');
      await region.getByRole('button', { name: /×/ }).click();
      await header(page).click();
      await expect(page.locator('[data-page-header]').getByRole('button', { name: /14/ })).toBeVisible();
      const purpose = await page.locator('[data-purpose]').boundingBox();
      expect(purpose!.height, 'purpose line height (one line)').toBeLessThanOrEqual(24);
    });

    test('filters: idle tiles say they filter in visible text on a phone, not only in a hover title', async ({ page }) => {
      await page.setViewportSize(VIEWPORTS.phone);
      await openAttendance(page, locale);
      await expect(page.locator('[data-kpi-hint]')).toBeVisible();
      await expect(page.locator('[data-kpi-hint]')).toHaveText(t(locale, 'Tap a count to filter the records.'));
    });

    for (const [name, viewport] of Object.entries(VIEWPORTS)) {
      test(`a11y: axe finds no serious or critical violation at ${name}`, async ({ page }) => {
        await page.setViewportSize(viewport);
        await openAttendance(page, locale);
        await expect(page.locator('[data-daily-table] tbody tr')).toHaveCount(14);
        const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
        const bad = results.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
        expect(bad.map(v => `${v.id}: ${v.nodes.slice(0, 2).map(n => n.target.join(' ')).join(' | ')}`)).toEqual([]);
      });
    }
  });
}
