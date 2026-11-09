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
      expect(m.visibleRows, 'rows visible without scrolling').toBeGreaterThanOrEqual(4);
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
      const empty = page.locator('[data-empty-state]', { hasText: t(locale, 'No devices yet') });
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
