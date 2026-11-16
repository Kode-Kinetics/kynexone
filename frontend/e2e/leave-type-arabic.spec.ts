import { expect, test } from '@playwright/test';
import { boot } from './leave-demo-setup-fixture';

/**
 * The Arabic name of a leave type is a reviewed suggestion ("organization-label"), never the phonetic reading. Mocked API.
 */

test('the leave type form asks for the reviewed Arabic wording (organization-label), not the phonetic reading', async ({ page }) => {
  const { calls } = await boot(page, { roles: ['Admin'], permissions: ['leave.read', 'leave.write', 'leave.policy_manage', 'organization.read'] });
  await page.goto('/setup?tab=leaveTypes');
  await page.getByRole('button', { name: 'New Leave Type' }).first().click({ timeout: 90_000 });
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Name (English) *').fill('Hajj Leave');
  await dialog.getByRole('button', { name: 'Suggest (AR)' }).click();
  await expect.poll(() => calls.transliterate.length).toBe(1);
  expect(calls.transliterate[0]).toMatchObject({ text: 'Hajj Leave', kind: 'organization-label' });
  await expect(dialog.locator('input[dir=rtl]')).toHaveValue('إجازة الحج');
});
