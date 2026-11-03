import { expect, test } from '@playwright/test';
import { axeSeriousOrWorse, boot, field, companySa, READ_ONLY } from './company-structure.fixture';

/** Setup → Companies against a mocked API. New companies are created by the guided wizard, not a dialog. */

test('create: the Create company button opens the guided wizard', async ({ page }) => {
  await boot(page, 'companies');
  await page.getByRole('button', { name: 'Create company', exact: true }).click();
  await expect(page).toHaveURL(/\/setup\/new-company/);
});

test('edit: changing a company sends a PUT', async ({ page }) => {
  const { writes, errors } = await boot(page, 'companies');
  await page.getByRole('button', { name: /^Edit/ }).first().click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Trade Name').fill('Masar Kingdom');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'PUT', path: '/api/companies/co-sa', body: { tradeName: 'Masar Kingdom' } });
  expect(errors).toEqual([]);
});

test('every edit-dialog field is reachable by its visible label', async ({ page }) => {
  await boot(page, 'companies');
  await page.getByRole('button', { name: /^Edit/ }).first().click();
  const dialog = page.getByRole('dialog');
  for (const label of ['Legal Name (EN)', 'Legal Name (AR)', 'Trade Name', 'Country', 'Tax Number', 'Default Currency', 'Status', 'WPS Employer ID', 'GOSI Employer ID']) {
    await expect(field(dialog, label)).toHaveCount(1);
  }
});

test('a 409 duplicate registration number shows under the field', async ({ page }) => {
  const { ctl } = await boot(page, 'companies');
  await page.getByRole('button', { name: /^Edit/ }).first().click();
  const dialog = page.getByRole('dialog');
  ctl.refuse = { status: 409, body: { error: 'registration_number_taken', field: 'registrationNumber', message: 'Another company in this workspace already uses this registration number.' } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Another company in this workspace already uses this registration number.');
});

test('the save error banner is announced', async ({ page }) => {
  const { ctl } = await boot(page, 'companies');
  await page.getByRole('button', { name: /^Edit/ }).first().click();
  const dialog = page.getByRole('dialog');
  ctl.refuse = { status: 500, body: { code: 'internal_error', message: 'boom', traceId: 't-9' } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toBeVisible();
});

test('a failed load says so and offers Retry', async ({ page }) => {
  const { ctl } = await boot(page, 'companies', { down: ['companies'] });
  await expect(page.getByRole('alert').filter({ hasText: 'This could not be loaded.' })).toBeVisible();
  await expect(page.getByText('No companies yet')).toHaveCount(0);
  ctl.down.clear();
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByRole('cell', { name: companySa.legalNameEn })).toBeVisible();
});

test('an empty tenant shows the empty state', async ({ page }) => {
  await boot(page, 'companies', { empty: true });
  await expect(page.getByText('No companies yet')).toBeVisible();
});

test('a read-only user sees Create, Import, Edit and More actions disabled with the reason', async ({ page }) => {
  await boot(page, 'companies', READ_ONLY);
  await expect(page.getByRole('cell', { name: companySa.legalNameEn })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create company', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Import CSV' })).toBeDisabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeDisabled();
  await expect(page.getByRole('button', { name: /More actions/ }).first()).toBeDisabled();
  await expect(page.getByTestId('write-blocked-reason')).toContainText('Admin or HR Manager');
});

test('a user without the Admin or HR Manager role gets disabled write controls even holding organization.write', async ({ page }) => {
  await boot(page, 'companies', { roles: ['HR Officer'], permissions: ['organization.read', 'organization.write', 'dashboard.read'] });
  await expect(page.getByRole('cell', { name: companySa.legalNameEn })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeDisabled();
  await expect(page.getByTestId('write-blocked-reason')).toContainText('Admin or HR Manager');
});

test('Arabic: the blocked reason is translated', async ({ page }) => {
  await boot(page, 'companies', { locale: 'ar', roles: ['HR Officer'], permissions: ['organization.read', 'dashboard.read'] });
  await expect(page.getByTestId('write-blocked-reason')).toContainText('مدير الموارد البشرية');
});

test('suspending the only active company explains itself in the page, not a browser alert', async ({ page }) => {
  const { ctl, rows } = await boot(page, 'companies', { roles: ['Admin'] });
  rows.companies.splice(1, 1);
  page.on('dialog', (d) => { throw new Error(`native dialog: ${d.message()}`); });
  await page.reload();
  await page.getByRole('button', { name: /More actions: Masar KSA/ }).click();
  await page.getByRole('menuitem', { name: 'Suspend' }).click();
  ctl.refuse = { status: 409, body: { error: 'last_active_company', message: 'x' } };
  await page.getByRole('button', { name: 'Suspend', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: /only active company/i })).toBeVisible();
});

test('axe: list and dialog', async ({ page }) => {
  await boot(page, 'companies');
  expect(await axeSeriousOrWorse(page, 'main')).toEqual([]);
  await page.getByRole('button', { name: /^Edit/ }).first().click();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

test('Arabic: the Companies tab and its dialog have no English left', async ({ page }) => {
  const { errors } = await boot(page, 'companies', { locale: 'ar', roles: ['Admin'] });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByText('Not set')).toHaveCount(0);
  await page.getByRole('button', { name: /^تعديل/ }).first().click();
  const dialog = page.getByRole('dialog');
  for (const english of ['Edit Company', 'Legal Name (EN)', 'Trade Name', 'Tax Number', 'Default Currency', 'WPS Employer ID', 'GOSI Employer ID', 'Cancel', 'Save']) {
    await expect(dialog.getByText(english, { exact: true })).toHaveCount(0);
  }
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  expect(errors).toEqual([]);
});

test('a company with no country shows an Arabic warning, not English', async ({ page }) => {
  const { rows } = await boot(page, 'companies', { locale: 'ar' });
  rows.companies[0].countryCode = '';
  await page.reload();
  const alert = page.getByRole('alert').filter({ hasText: /الدولة/ });
  await expect(alert).toBeVisible();
  await expect(alert).not.toContainText('has no country set');
});
