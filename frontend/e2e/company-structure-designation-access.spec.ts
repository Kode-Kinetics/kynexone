import { expect, test } from '@playwright/test';
import { axeSeriousOrWorse, boot, field, MANAGER_PERMISSIONS } from './company-structure.fixture';

/** Setup → Designations → "Access roles" (designation-driven access) against a mocked API. */

// The form only asks (and the server only answers) for someone who holds security.manage.
const ON = { access: { enabled: true, canConfigure: true }, permissions: [...MANAGER_PERMISSIONS, 'security.manage'] };

test('the access roles field is not offered while the feature is off', async ({ page }) => {
  await boot(page, 'designations');
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  await expect(page.getByRole('dialog').getByText('Access roles')).toHaveCount(0);
});

test('the access roles field is not offered to someone who may not configure access', async ({ page }) => {
  await boot(page, 'designations', { access: { enabled: true, canConfigure: false }, permissions: [...MANAGER_PERMISSIONS, 'security.manage'] });
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  await expect(page.getByRole('dialog').getByText('Access roles')).toHaveCount(0);
});

test('someone without security.manage never asks for access options and sees no field (no 403 toast for every HR user)', async ({ page }) => {
  const { ctl } = await boot(page, 'designations', { access: { enabled: true, canConfigure: true } });
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  await expect(page.getByRole('dialog').getByText('Access roles')).toHaveCount(0);
  expect(ctl.accessOptionsCalls).toBe(0);
});

test('a user who may not configure access never sends the roles', async ({ page }) => {
  const { writes } = await boot(page, 'designations', { access: { enabled: true, canConfigure: false }, permissions: [...MANAGER_PERMISSIONS, 'security.manage'] });
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Title (EN)').fill('Senior Software Engineer');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0].method).toBe('PUT');
  expect(writes[0].body).not.toHaveProperty('accessRoles');
});

test('editing shows the roles the designation grants and saves a change to them', async ({ page }) => {
  const { writes, errors } = await boot(page, 'designations', ON);
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('Access roles', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('checkbox', { name: /Payroll Officer/ })).toBeChecked();
  await expect(dialog.getByRole('checkbox', { name: 'Manager Approves for the team' })).not.toBeChecked();
  await dialog.getByRole('checkbox', { name: 'Manager Approves for the team' }).check();
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'PUT', path: '/api/designations/dg-swe', body: { accessRoles: ['Payroll Officer', 'Manager'] } });
  expect(errors).toEqual([]);
});

test('a new designation can be created with access roles', async ({ page }) => {
  const { writes } = await boot(page, 'designations', ON);
  await page.getByRole('button', { name: 'Add Designation', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Company').selectOption('co-sa');
  await field(dialog, 'Code').fill('PAYO');
  await field(dialog, 'Title (EN)').fill('Payroll Officer');
  await dialog.getByRole('checkbox', { name: /Payroll Officer/ }).check();
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/designations', body: { code: 'PAYO', accessRoles: ['Payroll Officer'] } });
});

test('a refused role (privileged or above the user) shows the server sentence', async ({ page }) => {
  const { ctl } = await boot(page, 'designations', ON);
  await page.getByRole('button', { name: /Edit Software Engineer/ }).click();
  const dialog = page.getByRole('dialog');
  ctl.refuse = { status: 400, body: { error: 'privileged_role', message: "The role 'Admin' carries security administration and cannot be given automatically by a designation." } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('cannot be given automatically');
});

test('Arabic: the field and its guidance are translated', async ({ page }) => {
  await boot(page, 'designations', { ...ON, locale: 'ar' });
  await page.getByRole('button', { name: 'إضافة مسمى وظيفي' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('أدوار الوصول', { exact: true })).toBeVisible();
  await expect(dialog.getByText('Access roles')).toHaveCount(0);
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});
