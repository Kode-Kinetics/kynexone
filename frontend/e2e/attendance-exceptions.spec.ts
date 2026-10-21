/**
 * ATTENDANCE → EXCEPTIONS renders from the live API on a production build: the request succeeds, the list shows
 * exactly the open exceptions the API returned, and its empty state says so plainly.
 *
 * DATA. The IntelliFlow tenant provisioned by e2e/bootstrap/provision.ts. Whether it has open exceptions depends on
 * the provisioned attendance, so the screen is compared with the API, never with a fixed number.
 */
import { test, expect, Locator, Page } from '@playwright/test';
import { INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, tenantLogin } from './helpers';

const content = (page: Page): Locator => page.locator('main');

test.describe('Attendance — exceptions', () => {
  test('the Exceptions tab lists what the API reports', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/attendance', { waitUntil: 'domcontentloaded' });
    const tab = content(page).getByRole('tab', { name: 'Exceptions', exact: true });
    await expect(tab).toBeVisible({ timeout: 60_000 });

    const listed = page.waitForResponse(r => r.url().includes('/api/attendance/exceptions') && r.request().method() === 'GET');
    await tab.click();
    await expect(tab).toHaveAttribute('aria-selected', 'true');
    const response = await listed;
    expect(response.status(), 'the exceptions list must load').toBe(200);
    const body = await response.json() as { items: unknown[]; total: number };

    await expect(content(page).getByTestId('attendance-exceptions')).toBeVisible();
    await expect(content(page).getByText(`${body.total} exception(s)`)).toBeVisible();
    await expect(content(page).getByTestId('attendance-exception')).toHaveCount(body.items.length);
    await expect(content(page).getByText('No open exceptions. Nothing needs your attention.')).toHaveCount(body.items.length === 0 ? 1 : 0);
  });

  test('a list longer than one page says so, and Load more reaches every exception', async ({ page }) => {
    // The provisioned tenant has far fewer than 100 exceptions, so the API is answered here with two pages of a
    // 130-exception list, exactly as the server pages it (100, then 30), to prove the screen reaches all of them.
    const total = 130;
    const item = (i: number) => ({
      id: `00000000-0000-0000-0000-${String(i).padStart(12, '0')}`, employeeId: 1000 + i, employeeName: `Employee ${i}`,
      employeeCode: `E${i}`, workDate: '2026-10-05', exceptionType: 'MissingPunch', severity: 'High',
      description: 'Missing in or out punch.', isResolved: false, resolution: null, createdAtUtc: '2026-10-05T10:00:00Z',
      day: { status: 'Present', firstInUtc: '2026-10-05T06:00:00Z', lastOutUtc: null, lateMinutes: 0, earlyExitMinutes: 0, missingPunch: true },
    });
    const requestedPages: number[] = [];
    await page.route('**/api/attendance/exceptions?**', async route => {
      const url = new URL(route.request().url());
      const pageNo = Number(url.searchParams.get('page') ?? '1');
      const size = Number(url.searchParams.get('pageSize') ?? '100');
      requestedPages.push(pageNo);
      const start = (pageNo - 1) * size;
      const items = Array.from({ length: Math.max(0, Math.min(size, total - start)) }, (_, k) => item(start + k + 1));
      await route.fulfill({ json: { items, total, page: pageNo, pageSize: size } });
    });

    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/attendance', { waitUntil: 'domcontentloaded' });
    const tab = content(page).getByRole('tab', { name: 'Exceptions', exact: true });
    await expect(tab).toBeVisible({ timeout: 60_000 });
    await tab.click();

    await expect(content(page).getByText(`${total} exception(s)`)).toBeVisible();
    await expect(content(page).getByTestId('attendance-exception')).toHaveCount(100);
    await expect(content(page).getByRole('status').filter({ hasText: 'Showing 100 of 130 exceptions' })).toBeVisible();

    await content(page).getByRole('button', { name: 'Load more' }).click();
    await expect(content(page).getByTestId('attendance-exception')).toHaveCount(total);
    await expect(content(page).getByRole('button', { name: 'Load more' }), 'every exception the total counts is on screen').toHaveCount(0);
    expect(requestedPages).toEqual([1, 2]);
  });
});
