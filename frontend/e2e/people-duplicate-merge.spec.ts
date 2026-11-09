import { expect, test, type Page } from '@playwright/test';
import { ALL_PERMISSIONS, bootPeopleFixture, type Locale } from './people-list.fixture';

/**
 * Duplicate warning in the Add Employee dialog (route-mocked, see people-list.fixture.ts). The dialog's Merge does NOT call
 * the resolve-duplicate endpoint: it abandons the draft and opens the matched record for editing, so an edit user (employees.write,
 * no employees.delete) keeps it. Only `mergeInto` calls the endpoint (it needs employees.delete) and has no screen entry point today;
 * its gate and 403 reason are covered in unit/duplicateMerge.spec.ts.
 */
const EDIT_ONLY = ALL_PERMISSIONS.filter((p) => p !== 'employees.delete');

async function openDuplicateWarning(page: Page, permissions: string[], locale: Locale) {
  await page.setViewportSize({ width: 1366, height: 900 });
  const fx = await bootPeopleFixture(page, { locale, permissions });
  // Registered after the fixture's catch-all, so it wins: the advisory check reports one strong match.
  await page.route('**/api/employees/duplicate-check', async (route) => {
    await route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ matches: [{ employeeId: 1000, employeeCode: 'EMP-1000', fullName: 'Employee 01 Al-Harbi', branch: 'Riyadh HQ', status: 'Active', matchType: 'strong', signals: ['Iqama number match'], canView: true }] }),
    });
  });
  await page.goto('/people/new');
  const dialog = page.getByRole('dialog').first();
  await expect(dialog).toBeVisible({ timeout: 120_000 });
  await page.locator('#employee-create-name').fill('Employee 01 Al-Harbi');
  // Date of birth sits under the collapsed "Additional personal details".
  await dialog.locator('details').evaluateAll((els) => els.forEach((el) => { (el as HTMLDetailsElement).open = true; }));
  await dialog.locator('input[type="date"]').first().fill('1990-01-01');
  await dialog.getByRole('button', { name: locale === 'ar' ? /^التالي/ : /^Next:/ }).first().click();
  const alert = dialog.getByRole('alert').filter({ hasText: 'EMP-1000' });
  await expect(alert).toBeVisible();
  return { fx, dialog, alert };
}

for (const locale of ['en', 'ar'] as const) {
  test(`edit-only user (${locale}): Merge and View existing stay available and no permission reason is shown`, async ({ page }) => {
    const { alert } = await openDuplicateWarning(page, EDIT_ONLY, locale);
    await expect(alert.getByRole('button', { name: locale === 'ar' ? /^دمج$/ : /^Merge$/ })).toBeVisible();
    await expect(alert.getByRole('button', { name: locale === 'ar' ? /عرض السجل الحالي/ : /View existing/ })).toBeVisible();
    await expect(alert.getByTestId('merge-needs-delete')).toHaveCount(0);
  });
}

test('edit-only user: Merge opens the existing record for editing and calls no merge endpoint', async ({ page }) => {
  const { fx, alert } = await openDuplicateWarning(page, EDIT_ONLY, 'en');
  await alert.getByRole('button', { name: 'Merge', exact: true }).click();
  // A cold `next dev` compiles the edit route first.
  await expect(page).toHaveURL(/\/people\/1000\/edit/, { timeout: 90_000 });
  expect(fx.writes.filter((w) => /duplicate/.test(w.path) && !w.path.endsWith('duplicate-check'))).toEqual([]);
});

test('user with employees.delete: Merge is offered and opens the existing record for editing', async ({ page }) => {
  const { fx, alert } = await openDuplicateWarning(page, ALL_PERMISSIONS, 'en');
  await alert.getByRole('button', { name: 'Merge', exact: true }).click();
  await expect(page).toHaveURL(/\/people\/1000\/edit/, { timeout: 90_000 });
  expect(fx.writes.filter((w) => /duplicate/.test(w.path) && !w.path.endsWith('duplicate-check'))).toEqual([]);
});
