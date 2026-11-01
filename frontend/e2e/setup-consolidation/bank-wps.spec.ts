import { expect, test } from '@playwright/test';
import { ADMIN, ALL_PERMISSIONS, boot, type Persona } from './fixture';

/**
 * Setup > Bank and WPS: the employer's bank-file settings, entered before any payroll run exists. The Guided setup item
 * "Bank and WPS settings (before the first payment)" opens here. Every API call is intercepted: nothing real is read or written.
 */
const FORMAT = { id: 'anb-connect-csv-v1', name: 'ANB Connect payroll CSV', bank: 'ANB', channel: 'Connect', sourceUrl: 'https://example.com/anb', reviewedOn: '2026-09-26', acceptanceStatus: 'not-bank-verified' };
const BASE = '/api/payroll/bank-exports';
const SETTINGS_PATH = `${BASE}/companies/fixture-company/settings`;
const url = '/setup?tab=bankWps&companyId=fixture-company';
const VALID = { formatId: FORMAT.id, molEstablishmentId: '7-1234567', mainAccountNumber: '1234567890123456', organizationName: 'Workspace Fixture LLC', organizationAddress1: 'Riyadh', organizationAddress2: '', organizationAddress3: '', companyName: 'Workspace Fixture LLC', narrative: 'Salaries', batchType: 'SAL', autoWpsUpload: false, nationalUnifiedNo: '' };

const withExport = (extra: string[] = []): Persona => ({ roles: ['Payroll Manager'], permissions: [...ALL_PERMISSIONS, 'payroll.export', ...extra] });
const mocks = (extra: Record<string, unknown> = {}) => ({ [`${BASE}/formats`]: [FORMAT], ...extra });

test('a payroll manager opens the form untouched, saves, and the settings go to that company', async ({ page }) => {
  const saved: unknown[] = [];
  const { errors } = await boot(page, {
    persona: withExport(['payroll.structure_manage']), url,
    mocks: mocks({ [SETTINGS_PATH]: (_u: URL, method: string) => method === 'PUT' ? VALID : { formatId: '' } }),
  });
  await page.route(`**${SETTINGS_PATH}`, async (route, request) => {
    if (request.method() === 'PUT') saved.push(request.postDataJSON());
    await route.fallback();
  });
  await expect(page.getByLabel('ANB main account number')).toBeVisible({ timeout: 30_000 });
  // The only format is offered as the default; that is not an unsaved edit.
  await expect(page.getByText(/Unsaved changes/)).toHaveCount(0);

  await page.getByLabel(/MOL establishment/i).fill('7-1234567');
  await page.getByLabel('ANB main account number').fill('1234567890123456');
  await page.getByLabel(/^Organisation name/i).fill('Workspace Fixture LLC');
  await page.getByLabel(/^Company name/i).fill('Workspace Fixture LLC');
  await page.getByLabel(/Organisation address line 1/i).fill('Riyadh');
  await page.getByLabel(/Organisation address line 2/i).fill('Riyadh');
  await page.getByLabel(/Organisation address line 3/i).fill('KSA');
  await page.getByLabel(/^Narrative/i).fill('Salaries');
  await page.getByLabel(/Batch type/i).selectOption({ index: 1 });
  await page.getByRole('button', { name: /Save employer setup/i }).click();
  await expect.poll(() => saved.length).toBe(1);
  expect(saved[0]).toMatchObject({ mainAccountNumber: '1234567890123456', formatId: FORMAT.id });
  expect(errors).toEqual([]);
});

test('a bad account number is refused in the form and nothing is sent', async ({ page }) => {
  const { writes } = await boot(page, { persona: withExport(['payroll.structure_manage']), url, mocks: mocks({ [SETTINGS_PATH]: { formatId: '' } }) });
  await expect(page.getByLabel('ANB main account number')).toBeVisible({ timeout: 30_000 });
  await page.getByLabel('ANB main account number').fill('123');
  await page.getByRole('button', { name: /Save employer setup/i }).click();
  await expect(page.getByText(/16/).first()).toBeVisible();
  expect(writes.filter(w => w.startsWith('PUT'))).toEqual([]);
});

test('someone who can read but not save is told so', async ({ page }) => {
  await boot(page, { persona: withExport(), url, mocks: mocks({ [SETTINGS_PATH]: { formatId: '' } }) });
  await expect(page.getByLabel('ANB main account number')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(/may lack the payroll structure permission/i)).toBeVisible();
});

test('a refused read explains itself and offers no retry that cannot work', async ({ page }) => {
  await boot(page, { persona: withExport(), url, mocks: mocks({ [SETTINGS_PATH]: { status: 403, body: { error: 'forbidden' } } }) });
  await expect(page.getByText(/need the payroll export permission/i)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('button', { name: /retry/i })).toHaveCount(0);
});

test('without the payroll export permission the section is not offered at all', async ({ page }) => {
  await boot(page, { persona: { roles: ['Payroll Manager'], permissions: [...ALL_PERMISSIONS, 'payroll.structure_manage'] }, url: '/setup?tab=guidedSetup', mocks: mocks() });
  await expect(page.getByRole('heading', { name: /Guided setup/i }).first()).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('link', { name: 'Bank and WPS' })).toHaveCount(0);
});

test('Setup scoped to a non-Saudi company explains it instead of showing an empty picker', async ({ page }) => {
  await boot(page, {
    persona: withExport(['payroll.structure_manage']), url, mocks: mocks({
      '/api/companies': { items: [{ id: 'fixture-company', legalNameEn: 'Dubai Fixture LLC', countryCode: 'AE', defaultCurrency: 'AED', isActive: true, approvalStatus: 'Active' }], total: 1, page: 1, pageSize: 100 },
    }),
  });
  await expect(page.getByText(/is not a Saudi company/i)).toBeVisible({ timeout: 30_000 });
});

test('Arabic users see the section in Arabic', async ({ page }) => {
  await boot(page, { persona: withExport(), url: '/setup?tab=guidedSetup', locale: 'ar', mocks: mocks() });
  await expect(page.getByRole('link', { name: 'البنك وحماية الأجور' })).toBeVisible({ timeout: 30_000 });
});
