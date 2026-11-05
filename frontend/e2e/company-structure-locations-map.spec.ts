import { expect, test, type Page } from '@playwright/test';
import { axeSeriousOrWorse, boot, field } from './company-structure.fixture';

/**
 * Setup → Locations: search an address, pick a place, and see / adjust the pin. The geo endpoints are mocked; the
 * map tiles are answered with a blank image so nothing leaves the machine.
 */

const BLANK_TILE = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');
const NO_MATCH = 'No match for that address. Try fewer words, add the city, paste a Google Maps link, or enter coordinates.';
const SEARCH_DOWN = 'The address search is not available right now. Paste a Google Maps link, enter coordinates, or click the map.';

const olaya = { label: 'Olaya Street, Al Olaya, Riyadh, Saudi Arabia', name: 'Olaya Street', city: 'Riyadh', countryCode: 'SA', latitude: 24.69, longitude: 46.685 };
const tahlia = { label: 'Tahlia Street, Jeddah, Saudi Arabia', name: 'Tahlia Street', city: 'Jeddah', countryCode: 'SA', latitude: 21.5433, longitude: 39.1728 };

type Scope = ReturnType<Page['getByRole']>;

async function open(page: Page, options: Parameters<typeof boot>[2] = {}) {
  await page.route('https://tile.openstreetmap.org/**', (route) => route.fulfill({ status: 200, contentType: 'image/png', body: BLANK_TILE }));
  const ctx = await boot(page, 'locations', options);
  const geo = { searches: [] as string[], search: (() => ({ status: 200, json: [olaya, tahlia] as unknown })) as () => { status: number; json: unknown } };
  await page.route('**/api/geo/search-address**', (route) => {
    geo.searches.push(new URL(route.request().url()).searchParams.get('q') ?? '');
    return route.fulfill(geo.search());
  });
  await page.route('**/api/geo/resolve-map-link**', (route) => route.fulfill({ status: 200, json: { latitude: 24.5, longitude: 46.5 } }));
  await page.getByRole('button', { name: options.locale === 'ar' ? 'إضافة موقع' : 'Add Location', exact: true }).click();
  return { ...ctx, geo, dialog: page.getByRole('dialog') };
}

const basics = async (dialog: Scope) => {
  await field(dialog, 'Code').fill('RYD-OLY');
  await field(dialog, 'Name (EN)').fill('Olaya Office');
};
const findPlace = (dialog: Scope) => dialog.getByRole('button', { name: 'Find place', exact: true });
const save = (dialog: Scope) => dialog.getByRole('button', { name: 'Save', exact: true });

test('search, choose a candidate: coordinates, address, city and country are filled and saved', async ({ page }) => {
  const { dialog, writes, geo, errors } = await open(page);
  await basics(dialog);
  await dialog.getByTestId('pin-paste').fill('Olaya Street Riyadh');
  expect(geo.searches).toEqual([]); // never per keystroke
  await findPlace(dialog).click();
  await expect(dialog.getByTestId('pin-suggestion')).toHaveCount(2);
  expect(geo.searches).toEqual(['Olaya Street Riyadh']);
  await dialog.getByTestId('pin-suggestion').first().click();
  await expect(field(dialog, 'Latitude')).toHaveValue('24.69');
  await expect(field(dialog, 'Longitude')).toHaveValue('46.685');
  await expect(field(dialog, 'Address')).toHaveValue(olaya.label);
  await expect(field(dialog, 'City')).toHaveValue('Riyadh');
  await expect(field(dialog, 'Country')).toHaveValue('SA');
  await expect(field(dialog, 'Geofence Radius (m)')).toHaveValue('150');
  await expect(dialog.getByTestId('pin-status')).toContainText('24.690000, 46.685000');
  await save(dialog).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/admin/locations', body: { code: 'RYD-OLY', countryCode: 'SA', city: 'Riyadh', addressLine1: olaya.label, latitude: 24.69, longitude: 46.685, geofenceRadiusMeters: 150 } });
  expect(errors).toEqual([]);
});

test('choosing a place never overwrites a city or address the person typed, and offers the address instead', async ({ page }) => {
  const { dialog } = await open(page);
  await basics(dialog);
  await field(dialog, 'City').fill('Jeddah');
  await field(dialog, 'Address').fill('Building 7, my own wording');
  await dialog.getByTestId('pin-paste').fill('Olaya Street Riyadh');
  await findPlace(dialog).click();
  await dialog.getByTestId('pin-suggestion').first().click();
  await expect(field(dialog, 'Latitude')).toHaveValue('24.69');
  await expect(field(dialog, 'City')).toHaveValue('Jeddah');
  await expect(field(dialog, 'Address')).toHaveValue('Building 7, my own wording');
  await dialog.getByRole('button', { name: 'Replace the address with the place I chose', exact: true }).click();
  await expect(field(dialog, 'Address')).toHaveValue(olaya.label);
  await expect(field(dialog, 'City')).toHaveValue('Jeddah');
});

test('an address longer than the field is cut to its limit', async ({ page }) => {
  const { dialog, geo } = await open(page);
  geo.search = () => ({ status: 200, json: [{ ...olaya, label: 'X'.repeat(400) }] });
  await basics(dialog);
  await dialog.getByTestId('pin-paste').fill('Olaya');
  await findPlace(dialog).click();
  await dialog.getByTestId('pin-suggestion').first().click();
  await expect(field(dialog, 'Address')).toHaveValue('X'.repeat(240));
});

test('no match says so, and the location still saves', async ({ page }) => {
  const { dialog, writes, geo } = await open(page);
  geo.search = () => ({ status: 200, json: [] });
  await basics(dialog);
  await field(dialog, 'Country').selectOption('SA');
  await dialog.getByTestId('pin-paste').fill('zzzz qqqq');
  await findPlace(dialog).click();
  await expect(dialog.getByTestId('pin-suggestions')).toContainText(NO_MATCH);
  await save(dialog).click();
  await expect(dialog).toBeHidden();
  expect(writes[0].body).toMatchObject({ code: 'RYD-OLY', countryCode: 'SA' });
  expect(writes[0].body.latitude).toBeUndefined();
});

test('a failed search shows the error, and the location still saves', async ({ page }) => {
  const { dialog, writes, geo } = await open(page);
  geo.search = () => ({ status: 500, json: { code: 'internal_error', message: 'boom' } });
  await basics(dialog);
  await field(dialog, 'Country').selectOption('SA');
  await dialog.getByTestId('pin-paste').fill('Olaya Street');
  await findPlace(dialog).click();
  await expect(dialog.getByTestId('pin-feedback')).toContainText(SEARCH_DOWN);
  await field(dialog, 'Latitude').fill('24.7');
  await field(dialog, 'Longitude').fill('46.7');
  await save(dialog).click();
  await expect(dialog).toBeHidden();
  expect(writes[0].body).toMatchObject({ latitude: 24.7, longitude: 46.7 });
});

test('a pasted Google Maps link, a short link and "lat, lng" text all fill the coordinates', async ({ page }) => {
  const { dialog } = await open(page);
  const paste = dialog.getByTestId('pin-paste');
  await paste.fill('https://www.google.com/maps/@24.7136,46.6753,15z');
  await findPlace(dialog).click();
  await expect(field(dialog, 'Latitude')).toHaveValue('24.7136');
  await expect(field(dialog, 'Longitude')).toHaveValue('46.6753');
  await paste.fill('https://maps.app.goo.gl/abc123');
  await findPlace(dialog).click();
  await expect(field(dialog, 'Latitude')).toHaveValue('24.5');
  await expect(field(dialog, 'Longitude')).toHaveValue('46.5');
  await paste.fill('21.5433, 39.1728');
  await findPlace(dialog).click();
  await expect(field(dialog, 'Latitude')).toHaveValue('21.5433');
  await expect(field(dialog, 'Longitude')).toHaveValue('39.1728');
});

test('clicking the map moves the pin, and typing coordinates moves it back', async ({ page }) => {
  const { dialog } = await open(page);
  await expect(dialog.getByTestId('pin-status')).toContainText('Not pinned yet.');
  await dialog.locator('.leaflet-container').click();
  await expect(dialog.locator('.leaflet-marker-icon')).toBeVisible();
  await expect(field(dialog, 'Latitude')).not.toHaveValue('');
  await field(dialog, 'Latitude').fill('21.5');
  await field(dialog, 'Longitude').fill('39.2');
  await expect(dialog.getByTestId('pin-status')).toContainText('21.500000, 39.200000');
});

test('edit: the existing location opens with its pin on the map', async ({ page }) => {
  await page.route('https://tile.openstreetmap.org/**', (route) => route.fulfill({ status: 200, contentType: 'image/png', body: BLANK_TILE }));
  await boot(page, 'locations');
  await page.getByRole('button', { name: /Edit Riyadh Tower/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('pin-status')).toContainText('24.700000, 46.700000');
  await expect(dialog.locator('.leaflet-marker-icon')).toBeVisible();
  await expect(field(dialog, 'Latitude')).toHaveValue('24.7');
});

test('axe: the dialog with the search, a result list and the map', async ({ page }) => {
  const { dialog } = await open(page);
  await dialog.getByTestId('pin-paste').fill('Olaya Street Riyadh');
  await findPlace(dialog).click();
  await expect(dialog.getByTestId('pin-suggestion')).toHaveCount(2);
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

test('Arabic: the pin panel is translated and right-to-left', async ({ page }) => {
  const { dialog, errors } = await open(page, { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(dialog.getByRole('heading', { name: 'تثبيت الموقع على الخريطة' })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'بحث عن الموقع', exact: true })).toBeVisible();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  expect(errors).toEqual([]);
});
