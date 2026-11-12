import { expect, test } from '@playwright/test';
import { SHOTS, YEAR, boot, openLeaveTab } from './leave-demo-setup-fixture';

/**
 * No leave balances for the year (found in a live rehearsal): the Leave Balance tab offers, to whoever holds leave.policy_manage (Admin, HR Manager),
 * to create them from the leave policies, once, and says what happened. Mocked API.
 * Run: npx playwright test -c e2e/playwright.leave-demo-setup.config.ts e2e/leave-open-year.spec.ts   (dev server on :5178)
 */

test('no balances for the year: an HR Manager creates them from the policies, and the list then shows them', async ({ page }) => {
  const { calls, errors } = await boot(page);
  await openLeaveTab(page, 'Leave Balance');
  await expect(page.getByText('No balance records found')).toBeVisible();
  const action = page.getByRole('button', { name: "Create this year's balances from the leave policies" });
  await expect(action).toBeVisible();
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/balances-empty-en.png`, fullPage: false });

  await action.click();
  await expect.poll(() => calls.openYear.length).toBe(1);
  expect(calls.openYear[0]).toEqual({ year: YEAR });
  await expect(page.getByTestId('open-year-note')).toContainText(`Created 1 balance for ${YEAR}`);
  await expect(page.getByText(`Sara Nasser · ${YEAR}`)).toBeVisible();
  await expect(page.getByRole('button', { name: "Create this year's balances from the leave policies" })).toHaveCount(0);
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/balances-created-en.png`, fullPage: false });
  expect(errors).toEqual([]);
});

test('an action that creates nothing says so and why, instead of claiming success', async ({ page }) => {
  await boot(page, { openYearResult: { balancesCreated: 0 } });
  await openLeaveTab(page, 'Leave Balance');
  await page.getByRole('button', { name: "Create this year's balances from the leave policies" }).click();
  await expect(page.getByTestId('open-year-note')).toContainText('No new balances were created');
  await expect(page.getByTestId('open-year-note')).toContainText('active policy');
});

test('the action is not offered without leave.policy_manage (leave.write alone files leave; it does not create balances)', async ({ page }) => {
  await boot(page, { permissions: ['leave.read', 'leave.write'] });
  await openLeaveTab(page, 'Leave Balance');
  await expect(page.getByText('No balance records found')).toBeVisible();
  await expect(page.getByTestId('open-year-balances')).toHaveCount(0);
});

test('the empty state and the action are in Arabic under the Arabic locale', async ({ page }) => {
  await boot(page, { locale: 'ar' });
  await page.goto('/leave');
  await page.getByRole('tab').nth(1).click({ timeout: 90_000 });
  const block = page.getByTestId('open-year-balances');
  await expect(block).toBeVisible();
  await expect(block.getByRole('button')).toHaveText('إنشاء أرصدة هذا العام من سياسات الإجازات');
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/balances-empty-ar.png`, fullPage: false });
});
