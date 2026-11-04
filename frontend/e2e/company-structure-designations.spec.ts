import { expect, test, type Page } from '@playwright/test';
import { axeSeriousOrWorse, boot, field, orphanLabels, NO_DELETE, WRITE_PERMISSION_WITHOUT_ROLE, CUSTOM_ROLE_WITH_PERMISSIONS } from './company-structure.fixture';

/** Setup → Designations against a mocked API. */

const add = (page: Page) => page.getByRole('button', { name: 'Add Designation', exact: true });

test('create: a designation is saved against its company', async ({ page }) => {
  const { writes, errors } = await boot(page, 'designations');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Company').selectOption('co-sa');
  await field(dialog, 'Code').fill('QA');
  await field(dialog, 'Title (EN)').fill('QA Engineer');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/designations', body: { companyId: 'co-sa', code: 'QA', titleEn: 'QA Engineer' } });
  await expect(page.getByRole('cell', { name: 'QA Engineer', exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('edit: renaming sends a PUT', async ({ page }) => {
  const { writes } = await boot(page, 'designations');
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Title (EN)').fill('Senior Software Engineer');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'PUT', path: '/api/designations/dg-swe', body: { titleEn: 'Senior Software Engineer' } });
});

test('a required field left empty is named before anything is sent', async ({ page }) => {
  const { writes } = await boot(page, 'designations');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toBeVisible();
  expect(writes).toEqual([]);
});

test('a 409 duplicate code shows the server sentence', async ({ page }) => {
  const { ctl } = await boot(page, 'designations');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Company').selectOption('co-sa');
  await field(dialog, 'Code').fill('SWE');
  await field(dialog, 'Title (EN)').fill('Dup');
  ctl.refuse = { status: 409, body: { code: 'designation_code_taken', message: 'A designation with code SWE already exists in this company.' } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('A designation with code SWE already exists in this company.');
});

test('a ProblemDetails validation error is shown', async ({ page }) => {
  const { ctl } = await boot(page, 'designations');
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  const dialog = page.getByRole('dialog');
  ctl.refuse = { status: 400, body: { title: 'One or more validation errors occurred.', errors: { Code: ['Code may only contain letters, digits and dashes.'] } } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Code may only contain letters, digits and dashes.');
});

test('a failed load says so and offers Retry', async ({ page }) => {
  const { ctl } = await boot(page, 'designations', { down: ['designations'] });
  await expect(page.getByRole('alert').filter({ hasText: 'This could not be loaded.' })).toBeVisible();
  await expect(page.getByText('No designations yet')).toHaveCount(0);
  ctl.down.clear();
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByRole('cell', { name: 'Software Engineer', exact: true })).toBeVisible();
});

test('an empty tenant shows the empty state', async ({ page }) => {
  await boot(page, 'designations', { empty: true });
  await expect(page.getByText('No designations yet')).toBeVisible();
  await expect(page.getByText('Add your first designation, or import a CSV.')).toBeVisible();
});

test('a user holding organization.write without the Admin or HR Manager role sees Add, Import, Edit and Remove disabled and the reason shown', async ({ page }) => {
  await boot(page, 'designations', WRITE_PERMISSION_WITHOUT_ROLE);
  await expect(page.getByRole('cell', { name: 'Software Engineer', exact: true })).toBeVisible();
  await expect(add(page)).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Import CSV' })).toBeDisabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeDisabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled();
  await expect(page.getByTestId('write-blocked-reason')).toContainText('Admin or HR Manager');
});

test('without organization.delete, Remove and Restore are disabled with the reason', async ({ page }) => {
  await boot(page, 'designations', NO_DELETE);
  await page.getByLabel(/Show turned-off/).check();
  await expect(page.getByRole('button', { name: /Edit Software Engineer/ })).toBeEnabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled();
  await expect(page.getByTestId('org-row-restore')).toBeDisabled();
  await expect(page.getByTestId('org-row-remove').first()).toHaveAttribute('title', /permission to remove or restore/);
});

test('the dialog cannot be closed while a save is in flight', async ({ page }) => {
  const { ctl } = await boot(page, 'designations');
  let release!: () => void;
  ctl.gate = new Promise<void>((r) => { release = r; });
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeDisabled();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  release();
  await expect(dialog).toBeHidden();
});

test('every field in the dialog is reachable by its label', async ({ page }) => {
  await boot(page, 'designations');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  expect(await orphanLabels(dialog)).toEqual([]);
  for (const name of ['Company', 'Code', 'Title (EN)', 'Title (AR)', 'Job Grade', 'Grade / Band', 'Job Level', 'Job Description']) {
    await expect(field(dialog, name), name).toBeVisible();
  }
  await expect(dialog.getByRole('checkbox', { name: 'Manager role' })).toBeVisible();
});

test('axe: list and dialog', async ({ page }) => {
  await boot(page, 'designations');
  await page.getByLabel(/Show turned-off/).check(); // the turned-off badge is part of the list
  await expect(page.getByText('Inactive', { exact: true }).first()).toBeVisible();
  expect(await axeSeriousOrWorse(page, 'main')).toEqual([]);
  await add(page).click();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

test('Arabic: right-to-left, translated, and the Arabic title is posted', async ({ page }) => {
  const { writes, errors } = await boot(page, 'designations', { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByRole('button', { name: 'إضافة مسمى وظيفي' }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'الشركة').selectOption('co-sa');
  await field(dialog, 'الرمز').fill('QA');
  await field(dialog, 'المسمى (بالإنجليزية)').fill('QA Engineer');
  await field(dialog, 'المسمى (بالعربية)').fill('مهندس جودة');
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  await dialog.getByRole('button', { name: 'حفظ', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/designations', body: { titleAr: 'مهندس جودة' } });
  expect(errors).toEqual([]);
});

test('a custom role holding every organization permission but not the Admin or HR Manager role sees the writes disabled with the reason (designations)', async ({ page }) => {
  await boot(page, 'designations', CUSTOM_ROLE_WITH_PERMISSIONS);
  await expect(page.getByRole('cell', { name: 'Software Engineer', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add Designation', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeDisabled();
  await expect(page.getByTestId('write-blocked-reason')).toContainText('Admin or HR Manager');
});

test('when the grade list cannot be loaded, the tab says so and Retry brings the grades back', async ({ page }) => {
  const { ctl } = await boot(page, 'designations', { down: ['grades'] });
  const notice = page.getByRole('alert').filter({ hasText: 'The grades could not be loaded.' });
  await expect(notice).toBeVisible();
  ctl.down.clear();
  await notice.getByRole('button', { name: 'Retry' }).click();
  await expect(notice).toBeHidden();
  await add(page).click();
  await field(page.getByRole('dialog'), 'Company').selectOption('co-sa');
  await expect(page.getByRole('dialog').getByRole('option', { name: 'Professional Grade 5' })).toHaveCount(1);
});
