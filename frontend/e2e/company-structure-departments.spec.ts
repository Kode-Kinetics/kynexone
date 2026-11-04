import { expect, test, type Page } from '@playwright/test';
import { READ_ONLY, axeSeriousOrWorse, boot, field, NO_DELETE, WRITE_PERMISSION_WITHOUT_ROLE, CUSTOM_ROLE_WITH_PERMISSIONS } from './company-structure.fixture';

/** Setup → Departments against a mocked API. */

const add = (page: Page) => page.getByRole('button', { name: 'Add Department', exact: true });

test('create: a department is saved against its company', async ({ page }) => {
  const { writes, errors } = await boot(page, 'departments');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Company').selectOption('co-sa');
  await field(dialog, 'Code').fill('HR');
  await field(dialog, 'Name (EN)').fill('People');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/departments', body: { companyId: 'co-sa', code: 'HR', nameEn: 'People' } });
  await expect(page.getByRole('cell', { name: 'People', exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('edit: renaming sends a PUT', async ({ page }) => {
  const { writes } = await boot(page, 'departments');
  await page.getByRole('button', { name: /Edit Finance/ }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Name (EN)').fill('Finance & Treasury');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'PUT', path: '/api/departments/de-fin', body: { nameEn: 'Finance & Treasury' } });
});

test('a 409 duplicate code shows the server sentence', async ({ page }) => {
  const { ctl } = await boot(page, 'departments');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Company').selectOption('co-sa');
  await field(dialog, 'Code').fill('FIN');
  await field(dialog, 'Name (EN)').fill('Dup');
  ctl.refuse = { status: 409, body: { code: 'department_code_taken', message: 'A department with code FIN already exists in this company.' } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('A department with code FIN already exists in this company.');
});

test('a ProblemDetails validation error is shown', async ({ page }) => {
  const { ctl } = await boot(page, 'departments');
  await page.getByRole('button', { name: /Edit Finance/ }).click();
  const dialog = page.getByRole('dialog');
  ctl.refuse = { status: 400, body: { title: 'One or more validation errors occurred.', errors: { Code: ['Code may only contain letters, digits and dashes.'] } } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Code may only contain letters, digits and dashes.');
});

test('a failed load says so and offers Retry', async ({ page }) => {
  const { ctl } = await boot(page, 'departments', { down: ['departments'] });
  await expect(page.getByRole('alert').filter({ hasText: 'This could not be loaded.' })).toBeVisible();
  await expect(page.getByText('No departments yet')).toHaveCount(0);
  ctl.down.clear();
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByRole('cell', { name: 'Finance', exact: true })).toBeVisible();
});

test('an empty tenant shows the empty state', async ({ page }) => {
  await boot(page, 'departments', { empty: true });
  await expect(page.getByText('No departments yet')).toBeVisible();
  await expect(page.getByText('Add your first department, or import a CSV.')).toBeVisible();
});

test('a user holding organization.write without the Admin or HR Manager role may add, import and edit (the server authorises by permission); Remove needs organization.delete', async ({ page }) => {
  await boot(page, 'departments', WRITE_PERMISSION_WITHOUT_ROLE);
  await expect(page.getByRole('cell', { name: 'Finance', exact: true })).toBeVisible();
  await expect(add(page)).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Import CSV' })).toBeEnabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeEnabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled(); // no organization.delete
  await expect(page.getByTestId('write-blocked-reason')).toHaveCount(0);
});

test('without organization.delete, Remove and Restore are disabled with the reason', async ({ page }) => {
  await boot(page, 'departments', NO_DELETE);
  await page.getByLabel(/Show turned-off/).check();
  await expect(page.getByRole('button', { name: /Edit Finance/ })).toBeEnabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled();
  await expect(page.getByTestId('org-row-restore')).toBeDisabled();
});

test('the dialog cannot be closed while a save is in flight', async ({ page }) => {
  const { ctl } = await boot(page, 'departments');
  let release!: () => void;
  ctl.gate = new Promise<void>((r) => { release = r; });
  await page.getByRole('button', { name: /Edit Finance/ }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeDisabled();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  release();
  await expect(dialog).toBeHidden();
});

test('axe: list and dialog', async ({ page }) => {
  await boot(page, 'departments');
  await page.getByLabel(/Show turned-off/).check(); // the turned-off badge is part of the list
  expect(await axeSeriousOrWorse(page, 'main')).toEqual([]);
  await add(page).click();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

test('Arabic: right-to-left and translated', async ({ page }) => {
  const { errors } = await boot(page, 'departments', { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByRole('button', { name: 'إضافة إدارة' }).click();
  const dialog = page.getByRole('dialog');
  await expect(field(dialog, 'الشركة')).toBeVisible();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  expect(errors).toEqual([]);
});

test('a custom role holding every organization permission is allowed to write (the server authorises by permission, not role name)', async ({ page }) => {
  await boot(page, 'departments', CUSTOM_ROLE_WITH_PERMISSIONS);
  await expect(page.getByRole('cell', { name: 'Finance', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add Department', exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeEnabled();
  await expect(page.getByTestId('write-blocked-reason')).toHaveCount(0);
  await expect(page.getByTestId('org-row-remove').first()).toBeEnabled();
});

test('a read-only user can still Export and download the CSV template (departments); only Import is blocked', async ({ page }) => {
  await boot(page, 'departments', READ_ONLY);
  await expect(page.getByRole('button', { name: 'Export' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Template' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Import CSV' })).toBeDisabled();
});
