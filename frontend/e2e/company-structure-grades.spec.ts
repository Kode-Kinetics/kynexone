import { expect, test, type Page } from '@playwright/test';
import { READ_ONLY, axeSeriousOrWorse, boot, field, orphanLabels, NO_DELETE, WRITE_PERMISSION_WITHOUT_ROLE, CUSTOM_ROLE_WITH_PERMISSIONS } from './company-structure.fixture';

/** Setup → Grades against a mocked API. */

const add = (page: Page) => page.getByRole('button', { name: 'Add Grade', exact: true });

test('create: a grade is saved against its company', async ({ page }) => {
  const { writes, errors } = await boot(page, 'grades');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Company').selectOption('co-sa');
  await field(dialog, 'Code').fill('G6');
  await field(dialog, 'Name').fill('Professional Grade 6');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/grades', body: { companyId: 'co-sa', code: 'G6', name: 'Professional Grade 6' } });
  await expect(page.getByRole('cell', { name: 'Professional Grade 6', exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('edit: renaming sends a PUT', async ({ page }) => {
  const { writes } = await boot(page, 'grades');
  await page.getByRole('button', { name: /Edit Professional Grade 5/ }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Name').fill('Senior Professional');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'PUT', path: '/api/grades/gr-5', body: { name: 'Senior Professional' } });
});

test('a required field left empty is named before anything is sent', async ({ page }) => {
  const { writes } = await boot(page, 'grades');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toBeVisible();
  expect(writes).toEqual([]);
});

test('a minimum above the maximum is refused before anything is sent', async ({ page }) => {
  const { writes } = await boot(page, 'grades');
  await page.getByRole('button', { name: /Edit Professional Grade 5/ }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Min').fill('10000');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Min salary cannot exceed max salary');
  expect(writes).toEqual([]);
});

test('a 409 duplicate code shows the server sentence', async ({ page }) => {
  const { ctl } = await boot(page, 'grades');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Company').selectOption('co-sa');
  await field(dialog, 'Code').fill('G5');
  await field(dialog, 'Name').fill('Dup');
  ctl.refuse = { status: 409, body: { code: 'grade_code_taken', message: 'A grade with code G5 already exists in this company.' } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('A grade with code G5 already exists in this company.');
});

test('a ProblemDetails validation error is shown', async ({ page }) => {
  const { ctl } = await boot(page, 'grades');
  await page.getByRole('button', { name: /Edit Professional Grade 5/ }).click();
  const dialog = page.getByRole('dialog');
  ctl.refuse = { status: 400, body: { title: 'One or more validation errors occurred.', errors: { Code: ['Code may only contain letters, digits and dashes.'] } } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Code may only contain letters, digits and dashes.');
});

test('a failed load says so and offers Retry', async ({ page }) => {
  const { ctl } = await boot(page, 'grades', { down: ['grades'] });
  await expect(page.getByRole('alert').filter({ hasText: 'This could not be loaded.' })).toBeVisible();
  await expect(page.getByText('No grades yet')).toHaveCount(0);
  ctl.down.clear();
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByRole('cell', { name: 'Professional Grade 5', exact: true })).toBeVisible();
});

test('an empty tenant shows the empty state', async ({ page }) => {
  await boot(page, 'grades', { empty: true });
  await expect(page.getByText('No grades yet')).toBeVisible();
  await expect(page.getByText('Add your first grade, or import a CSV.')).toBeVisible();
});

test('a user holding organization.write without the Admin or HR Manager role may add, import and edit (the server authorises by permission); Remove needs organization.delete', async ({ page }) => {
  await boot(page, 'grades', WRITE_PERMISSION_WITHOUT_ROLE);
  await expect(page.getByRole('cell', { name: 'Professional Grade 5', exact: true })).toBeVisible();
  await expect(add(page)).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Import CSV' })).toBeEnabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeEnabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled(); // no organization.delete
  await expect(page.getByTestId('write-blocked-reason')).toHaveCount(0);
});

test('without organization.delete, Remove and Restore are disabled with the reason', async ({ page }) => {
  await boot(page, 'grades', NO_DELETE);
  await page.getByLabel(/Show turned-off/).check();
  await expect(page.getByRole('button', { name: /Edit Professional Grade 5/ })).toBeEnabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled();
  await expect(page.getByTestId('org-row-restore')).toBeDisabled();
});

test('the dialog cannot be closed while a save is in flight', async ({ page }) => {
  const { ctl } = await boot(page, 'grades');
  let release!: () => void;
  ctl.gate = new Promise<void>((r) => { release = r; });
  await page.getByRole('button', { name: /Edit Professional Grade 5/ }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeDisabled();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  release();
  await expect(dialog).toBeHidden();
});

test('every field in the dialog is reachable by its label', async ({ page }) => {
  await boot(page, 'grades');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  expect(await orphanLabels(dialog)).toEqual([]);
  for (const name of ['Company', 'Code', 'Level', 'Name', 'Name (Arabic, optional)', 'Band', 'Min', 'Mid', 'Max', 'Currency']) {
    await expect(field(dialog, name), name).toBeVisible();
  }
});

test('axe: list and dialog', async ({ page }) => {
  await boot(page, 'grades');
  await page.getByLabel(/Show turned-off/).check(); // the turned-off badge is part of the list
  await expect(page.getByText('Inactive', { exact: true }).first()).toBeVisible();
  expect(await axeSeriousOrWorse(page, 'main')).toEqual([]);
  await add(page).click();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

test('Arabic: right-to-left, translated, and the Arabic name is posted', async ({ page }) => {
  const { writes, errors } = await boot(page, 'grades', { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByRole('button', { name: 'إضافة درجة' }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'الشركة').selectOption('co-sa');
  await field(dialog, 'الرمز').fill('G6');
  await field(dialog, 'الاسم', ).first().fill('Grade 6');
  await dialog.getByLabel('الاسم (بالعربية، اختياري)', { exact: true }).fill('الدرجة 6');
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  await dialog.getByRole('button', { name: 'حفظ', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/grades', body: { nameAr: 'الدرجة 6' } });
  expect(errors).toEqual([]);
});

test('a custom role holding every organization permission is allowed to write (the server authorises by permission, not role name)', async ({ page }) => {
  await boot(page, 'grades', CUSTOM_ROLE_WITH_PERMISSIONS);
  await expect(page.getByRole('cell', { name: 'Professional Grade 5', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add Grade', exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeEnabled();
  await expect(page.getByTestId('write-blocked-reason')).toHaveCount(0);
  await expect(page.getByTestId('org-row-remove').first()).toBeEnabled();
});

test('when a grade\'s pay scale cannot be loaded, saving is turned off so the lines are not replaced with nothing', async ({ page }) => {
  const { writes } = await boot(page, 'grades', { payScaleDown: true });
  await page.getByRole('button', { name: /Edit Professional Grade 5/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('alert').filter({ hasText: 'pay scale' })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
  expect(writes).toEqual([]);
});

test('a read-only user can still Export and download the CSV template (grades); only Import is blocked', async ({ page }) => {
  await boot(page, 'grades', READ_ONLY);
  await expect(page.getByRole('button', { name: 'Export' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Template' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Import CSV' })).toBeDisabled();
});
