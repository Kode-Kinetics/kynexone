import { test, expect, Page } from '@playwright/test';
import { PLATFORM_EMAIL } from './helpers';

/** Open the invite modal from the page-level "Add Member" control. */
async function openInviteModal(page: Page) {
  const addBtn = page
    .getByRole('button', { name: /add member|invite|new member/i })
    .or(page.getByRole('link', { name: /add member|invite|new member/i }));
  await addBtn.first().click();
  const dialog = page.getByRole('dialog').first();
  await expect(dialog).toBeVisible({ timeout: 8_000 });
  return dialog;
}

test.describe('Platform team management', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/platform/team');
    await page.waitForLoadState('networkidle');
  });

  test('team page loads without JS errors', async ({ page }) => {
    // The listener MUST be attached before the navigation it is meant to watch.
    // Attaching it after `beforeEach` had already navigated made every load-time
    // exception structurally uncatchable — the array could only ever be empty.
    // (platform-billing.spec.ts does it this way.)
    const jsErrors: string[] = [];
    page.on('pageerror', e => jsErrors.push(e.message));

    await page.goto('/platform/team');
    await page.waitForLoadState('networkidle');
    // Prove the page actually rendered; a blank document throws no errors either.
    await expect(page.getByRole('heading', { name: /platform team/i })).toBeVisible({ timeout: 10_000 });

    expect(jsErrors, `page errors during load: ${jsErrors.join(' | ')}`).toHaveLength(0);
  });

  test('team page shows at least one member (the platform admin account)', async ({ page }) => {
    // Previously this only asserted the page did not say "something went wrong"
    // or "unauthorized" — an empty list, a spinner that never resolved, and a
    // 404 body all satisfied it. Assert the member is actually listed.
    const memberRow = page.locator('tbody tr').filter({ hasText: PLATFORM_EMAIL });
    await expect(memberRow).toHaveCount(1);
    await expect(memberRow.first()).toBeVisible({ timeout: 10_000 });
    // The bootstrap platform account holds the Owner role.
    await expect(memberRow.first()).toContainText(/owner/i);

    // The header's member count must agree with the rows actually rendered.
    const rowCount = await page.locator('tbody tr').count();
    expect(rowCount, 'the team list must not be empty').toBeGreaterThan(0);
    await expect(page.locator('p').filter({ hasText: /Platform users are separate/ }).first())
      .toHaveText(new RegExp(`^${rowCount} members? · `));
  });

  test('"Add Member" or "Invite" button is present', async ({ page }) => {
    const addBtn = page
      .getByRole('button', { name: /add member|invite|new member/i })
      .or(page.getByRole('link', { name: /add member|invite|new member/i }));
    await expect(addBtn.first()).toBeVisible({ timeout: 10_000 });
  });

  test('clicking Add Member opens an invite modal', async ({ page }) => {
    const dialog = await openInviteModal(page);
    await expect(dialog).toContainText(/add platform team member/i);
  });

  test('invite modal email field rejects a malformed address', async ({ page }) => {
    // The old body was `if (count > 0) { … if (count > 0) { … } }` around a
    // "no crash" check, so it validated nothing at all — and passed when the
    // field or the submit button was missing entirely.
    const dialog = await openInviteModal(page);

    const emailInput = dialog.getByLabel(/email/i);
    await expect(emailInput, 'the invite modal must have an email field').toHaveCount(1);

    await emailInput.fill('not-an-email');
    const submitBtn = dialog.getByRole('button', { name: /add member/i });
    await expect(submitBtn).toBeVisible();
    await submitBtn.click();

    // A malformed address must be rejected, and the modal must stay open.
    expect(
      await emailInput.evaluate((el) => (el as HTMLInputElement).validity.valid),
      'a malformed address must fail email validation',
    ).toBe(false);
    expect(
      await emailInput.evaluate((el) => (el as HTMLInputElement).validationMessage),
      'the rejection must be reported to the user',
    ).not.toBe('');
    await expect(dialog, 'an invalid submission must not close the modal').toBeVisible();

    // Control: the same field ACCEPTS a well-formed address — otherwise the
    // assertion above would also pass on an input that is never valid.
    await emailInput.fill('valid.person@example.com');
    expect(await emailInput.evaluate((el) => (el as HTMLInputElement).validity.valid)).toBe(true);
  });

  test('cancel closes the invite modal', async ({ page }) => {
    // `if (await cancelBtn.count() > 0)` meant a missing Cancel button produced
    // a passing test with zero assertions.
    const dialog = await openInviteModal(page);
    const cancelBtn = dialog.getByRole('button', { name: /^\s*cancel\s*$/i });
    await expect(cancelBtn, 'the invite modal must offer a Cancel control').toBeVisible();
    await cancelBtn.click();
    await expect(page.getByRole('dialog')).toHaveCount(0, { timeout: 5_000 });
  });
});
