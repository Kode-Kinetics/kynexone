import { expect, test, type Page } from '@playwright/test';
import { axeSeriousOrWorse, boot, field, NO_DELETE, WRITE_PERMISSION_WITHOUT_ROLE } from './company-structure.fixture';

/** Setup → Locations against a mocked API. */

const add = (page: Page) => page.getByRole('button', { name: 'Add Location', exact: true });
const fillBasics = async (page: Page, code = 'JED-HQ') => {
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Code').fill(code);
  await field(dialog, 'Name (EN)').fill('Jeddah Hub');
  await field(dialog, 'Country').selectOption('SA');
  return dialog;
};

test('create: a location with a code, name and country is saved; country is not preset', async ({ page }) => {
  const { writes, errors } = await boot(page, 'locations');
  await add(page).click();
  await expect(field(page.getByRole('dialog'), 'Country')).toHaveValue('');
  const dialog = await fillBasics(page);
  await field(dialog, 'Latitude').fill('21.5');
  await field(dialog, 'Longitude').fill('39.2');
  await field(dialog, 'Geofence Radius (m)').fill('120');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/admin/locations', body: { code: 'JED-HQ', countryCode: 'SA', latitude: 21.5, longitude: 39.2, geofenceRadiusMeters: 120 } });
  await expect(page.getByRole('cell', { name: 'Jeddah Hub', exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('coordinates are validated before the request', async ({ page }) => {
  const { writes } = await boot(page, 'locations');
  await add(page).click();
  const dialog = await fillBasics(page);
  const save = dialog.getByRole('button', { name: 'Save', exact: true });
  await field(dialog, 'Latitude').fill('91');
  await field(dialog, 'Longitude').fill('39');
  await save.click();
  await expect(dialog.getByRole('alert')).toContainText(/latitude/i);
  await field(dialog, 'Latitude').fill('21');
  await field(dialog, 'Longitude').fill('181');
  await save.click();
  await expect(dialog.getByRole('alert')).toContainText(/longitude/i);
  await field(dialog, 'Longitude').fill('');
  await save.click();
  await expect(dialog.getByRole('alert')).toContainText(/together/i);
  await field(dialog, 'Longitude').fill('39');
  await field(dialog, 'Geofence Radius (m)').fill('0');
  await save.click();
  await expect(dialog.getByRole('alert')).toContainText(/radius/i);
  await field(dialog, 'Geofence Radius (m)').fill('-5');
  await save.click();
  await expect(dialog.getByRole('alert')).toContainText(/radius/i);
  expect(writes).toHaveLength(0);
  await field(dialog, 'Geofence Radius (m)').fill('50');
  await save.click();
  await expect(dialog).toBeHidden();
  expect(writes).toHaveLength(1);
});

test('latitude 0 / longitude 0 is a valid point, not an empty field', async ({ page }) => {
  const { writes } = await boot(page, 'locations');
  await add(page).click();
  const dialog = await fillBasics(page);
  await field(dialog, 'Latitude').fill('0');
  await field(dialog, 'Longitude').fill('0');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0].body).toMatchObject({ latitude: 0, longitude: 0 });
});

test('edit: changing a location sends a PUT', async ({ page }) => {
  const { writes } = await boot(page, 'locations');
  await page.getByRole('button', { name: /Edit Riyadh Tower/ }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'City').fill('Riyadh Central');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'PUT', path: '/api/admin/locations/lo-1', body: { city: 'Riyadh Central' } });
});

test('a 409 duplicate code shows the server sentence', async ({ page }) => {
  const { ctl } = await boot(page, 'locations');
  await add(page).click();
  const dialog = await fillBasics(page, 'RYD-HQ');
  ctl.refuse = { status: 409, body: { code: 'location_code_taken', message: 'A location with code RYD-HQ already exists.' } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('A location with code RYD-HQ already exists.');
  await expect(dialog).toBeVisible();
});

test('the dialog cannot be closed while a save is in flight', async ({ page }) => {
  const { ctl } = await boot(page, 'locations');
  let release!: () => void;
  ctl.gate = new Promise<void>((r) => { release = r; });
  await page.getByRole('button', { name: /Edit Riyadh Tower/ }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeDisabled();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  release();
  await expect(dialog).toBeHidden();
});

test('a failed load says so and offers Retry', async ({ page }) => {
  const { ctl } = await boot(page, 'locations', { down: ['locations'] });
  await expect(page.getByRole('alert').filter({ hasText: 'This could not be loaded.' })).toBeVisible();
  await expect(page.getByText('No locations yet')).toHaveCount(0);
  ctl.down.clear();
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByRole('cell', { name: 'Riyadh Tower', exact: true })).toBeVisible();
});

test('an empty tenant shows the empty state', async ({ page }) => {
  await boot(page, 'locations', { empty: true });
  await expect(page.getByText('No locations yet')).toBeVisible();
  await expect(page.getByText('Add your first location, or import a CSV.')).toBeVisible();
});

test('a user holding organization.write without the Admin or HR Manager role sees no Add, Import, Edit, Remove or Restore', async ({ page }) => {
  await boot(page, 'locations', WRITE_PERMISSION_WITHOUT_ROLE);
  await expect(page.getByRole('cell', { name: 'Riyadh Tower', exact: true })).toBeVisible();
  await expect(add(page)).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Import/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Edit/ })).toHaveCount(0);
  await expect(page.getByTestId('org-row-remove')).toHaveCount(0);
});

test('without organization.delete, Remove and Restore are disabled with the reason', async ({ page }) => {
  await boot(page, 'locations', NO_DELETE);
  await page.getByLabel(/Show turned-off/).check();
  await expect(page.getByRole('button', { name: /Edit Riyadh Tower/ })).toBeEnabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled();
  await expect(page.getByTestId('org-row-restore')).toBeDisabled();
});

test('axe: list and dialog', async ({ page }) => {
  await boot(page, 'locations');
  await page.getByLabel(/Show turned-off/).check(); // the turned-off badge is part of the list
  expect(await axeSeriousOrWorse(page, 'main')).toEqual([]);
  await add(page).click();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

test('Arabic: right-to-left and translated', async ({ page }) => {
  const { errors } = await boot(page, 'locations', { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByRole('button', { name: 'إضافة موقع' }).click();
  const dialog = page.getByRole('dialog');
  await expect(field(dialog, 'خط العرض')).toBeVisible();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  expect(errors).toEqual([]);
});
