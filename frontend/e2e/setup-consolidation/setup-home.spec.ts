import { expect, test } from '@playwright/test';
import { ADMIN, EMPLOYEE, boot } from './fixture';

// The one home: the main menu no longer lists the moved pages, Setup is offered from the top-bar gear to anyone with a
// section to open, and never to an employee whose own-leave permission is also a Setup permission.

test('the main menu lists Setup once and none of the moved pages', async ({ page }) => {
  await boot(page, { persona: ADMIN, url: '/dashboard' });
  const menu = page.getByRole('navigation', { name: 'Primary navigation' });
  await menu.getByRole('button', { name: 'Administration', exact: true }).click({ timeout: 30_000 });
  await expect(menu.getByRole('button', { name: 'Setup', exact: true }).or(menu.getByRole('link', { name: 'Setup', exact: true }))).toHaveCount(1);
  for (const gone of ['Payslip Templates', 'Compliance Profiles', 'Tax Policies', 'Tenant Admin', 'User Management', 'Opening Balances']) {
    await expect(menu.getByText(gone, { exact: true }), gone).toHaveCount(0);
  }
});

test('the top-bar gear opens Setup', async ({ page }) => {
  await boot(page, { persona: ADMIN, url: '/dashboard' });
  const gear = page.getByRole('link', { name: 'Setup', exact: true }).and(page.getByTestId('topbar-setup'));
  await expect(gear).toBeVisible({ timeout: 30_000 });
  await gear.click();
  await expect(page).toHaveURL(/\/setup/);
  await expect(page.locator('#setup-section-heading')).toBeVisible({ timeout: 30_000 });
});

test('an employee is offered no Setup: no gear, no menu entry', async ({ page }) => {
  await boot(page, { persona: EMPLOYEE, url: '/dashboard' });
  await expect(page.getByRole('navigation', { name: 'Primary navigation' })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('topbar-setup')).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: 'Primary navigation' }).getByText('Setup', { exact: true })).toHaveCount(0);
});

test('a module panel that fails is contained: Setup keeps its menu and offers Retry', async ({ page }) => {
  // The holiday calendars list answered in a shape the editor does not expect: HolidayCalendarTab throws while rendering.
  const consoleErrors: string[] = [];
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
  await boot(page, { persona: ADMIN, url: '/setup?tab=holidayCalendars', mocks: { '/api/leave/holidays/calendars': { items: [] } } });
  const failed = page.locator('[data-module-section="holidayCalendars"]').getByRole('alert');
  await expect(failed).toContainText('This section could not be shown', { timeout: 30_000 });
  await expect(failed.getByRole('button', { name: 'Try again', exact: true })).toBeVisible();
  // The rest of Setup still works.
  await expect(page.getByRole('heading', { name: 'Something went wrong' })).toHaveCount(0);
  await expect(page.locator('#setup-section-heading')).toHaveText('Holiday calendars');
  await expect(page.getByRole('navigation', { name: 'Setup sections' }).getByRole('link', { name: 'Leave types', exact: true })).toBeVisible();
  // Try again re-mounts the editor (here it fails again, and is contained again).
  await failed.getByRole('button', { name: 'Try again', exact: true }).click();
  await expect(page.locator('[data-module-section="holidayCalendars"]').getByRole('alert')).toContainText('This section could not be shown');
  // The render error is still reported (React logs what a boundary caught), never swallowed.
  expect(consoleErrors.some(message => message.includes('calendars.filter'))).toBe(true);
});
