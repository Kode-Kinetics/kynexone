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
});
