import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

/**
 * The employee import preview, fed the API's REAL responses (see playwright.employee-import.config.ts). A file
 * the commit would refuse must show the commit's own refusal with every bad row, and must not offer Confirm;
 * a clean file must offer it with the count the commit will produce; and a commit refusal is reported, not
 * swallowed.
 */
const fixtures = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'unit', 'fixtures', 'employeeImportResponses.json'), 'utf8')) as Record<string, any>;

const REFUSED_CSV = 'EmployeeCode,FullName\nFX1,Fixture One\nFX2,\nFX1,Fixture Twin\n';
const GOOD_CSV = 'EmployeeCode,FullName\nFX1,Fixture One\nFX2,Fixture Two\n';

async function boot(page: Page, handler: (path: string, method: string, body: any) => { status?: number; json: unknown } | undefined) {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'fixture'));
  await page.route('**/api/**', (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const handled = handler(url.pathname, request.method(), request.postData() ? request.postDataJSON() : null);
    if (handled) return route.fulfill({ status: handled.status ?? 200, json: handled.json });
    if (url.pathname === '/api/auth/me') return route.fulfill({ json: {
      id: 'user-1', tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Hala Officer', roles: ['HR Manager'],
      permissions: ['employees.read', 'employees.write', 'employees.bulk_import'],
      companies: [{ id: 'company-1', name: 'Pilot Demo', code: 'PILOT', countryCode: 'SA', isActive: true }],
    } });
    if (url.pathname === '/api/employees/field-catalog') return route.fulfill({ json: fixtures.FieldCatalogSaIndian });
    if (url.pathname === '/api/tenant-admin/localization') return route.fulfill({ json: { currencyCode: 'SAR' } });
    if (url.pathname === '/api/notifications' || url.pathname.includes('/features/')) return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  await page.goto('/people');
  return errors;
}

async function choose(page: Page, csv: string) {
  await page.locator('input[type="file"][accept=".csv,text/csv"]').first().setInputFiles({ name: 'employees.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
}

test('a file the commit would refuse shows the refusal with every bad row and offers no Confirm', async ({ page }, info) => {
  let committed = 0;
  const errors = await boot(page, (p, method) => {
    if (p === '/api/employees/import-preview' && method === 'POST') return { json: fixtures.PreviewRefused };
    if (p === '/api/employees/import' && method === 'POST') { committed++; return { status: 422, json: fixtures.ImportRefused }; }
    return undefined;
  });
  await choose(page, REFUSED_CSV);
  const dialog = page.getByRole('dialog');
  const refusal = dialog.getByTestId('import-commit-refusal');
  await expect(refusal).toBeVisible();
  await expect(refusal).toContainText('This file will not be imported.');
  await expect(refusal).toContainText("Row 3: FullName is empty — every row must name the person");
  await expect(refusal).toContainText("Row 4: EmployeeCode 'FX1' is also used by row 2");
  await expect(refusal).toContainText('Nothing has been imported.');
  await expect(dialog.getByRole('button', { name: 'Confirm import' })).toBeDisabled();
  // No "will be imported as inactive" reassurance next to a refusal: nobody is imported.
  await expect(dialog.getByText('will be imported as inactive')).toHaveCount(0);
  await expect(dialog.getByText('Refused', { exact: true }).first()).toBeVisible();
  await page.screenshot({ path: info.outputPath('refused-preview.png'), fullPage: false });
  expect(committed).toBe(0);
  expect(errors).toEqual([]);
});

test('a clean file offers Confirm, and a commit refusal is reported rather than swallowed', async ({ page }, info) => {
  const errors = await boot(page, (p, method) => {
    if (p === '/api/employees/import-preview' && method === 'POST') return { json: fixtures.PreviewGood };
    // The database changed between preview and commit: the commit refuses, and the screen must say why.
    if (p === '/api/employees/import' && method === 'POST') return { status: 422, json: fixtures.ImportRefused };
    return undefined;
  });
  await choose(page, GOOD_CSV);
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('import-commit-refusal')).toHaveCount(0);
  const confirm = dialog.getByRole('button', { name: 'Confirm import' });
  await expect(confirm).toBeEnabled();
  await confirm.click();
  await expect(page.getByText(/Import failed — Row 3 \(EmployeeCode 'FX2'\): FullName is empty/)).toBeVisible();
  await page.screenshot({ path: info.outputPath('commit-refusal-toast.png'), fullPage: false });
  expect(errors).toEqual([]);
});

test('a mis-shaped row (an unquoted 8,000) is refused naming the row and both cell counts', async ({ page }) => {
  const errors = await boot(page, (p, method) => {
    if (p === '/api/employees/import-preview' && method === 'POST') return { status: 422, json: fixtures.ImportShapeRefused };
    return undefined;
  });
  await choose(page, 'EmployeeCode,FullName,BasicSalary\nFX1,Fixture One,8,000\n');
  await expect(page.getByText(/Import failed — CSV row 2 has 9 cell\(s\) but the header declares 8 column\(s\)/)).toBeVisible();
  expect(errors).toEqual([]);
});
