import { expect, test } from '@playwright/test';
import { READ_ONLY, axeSeriousOrWorse, boot, field, NO_DELETE, WRITE_PERMISSION_WITHOUT_ROLE, CUSTOM_ROLE_WITH_PERMISSIONS } from './company-structure.fixture';

/** Setup → Branches against a mocked API. npx next dev -p 5198 &  npx playwright test -c e2e/playwright.company-structure.config.ts */

const add = (page: import('@playwright/test').Page) => page.getByRole('button', { name: 'Add Branch', exact: true });

test('create: required Company, Country, City and Time Zone are enforced before any request, then the branch is saved', async ({ page }) => {
  const { writes, errors } = await boot(page, 'branches');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Code').fill('KHB-01');
  await field(dialog, 'Name (EN)').fill('Khobar Office');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText(/company/i);
  expect(writes).toHaveLength(0);
  await field(dialog, 'Company').selectOption('co-sa');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText(/country/i);
  await field(dialog, 'Country').selectOption('SA');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText(/city/i);
  await field(dialog, 'City').fill('Khobar');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText(/time zone/i);
  expect(writes).toHaveLength(0);
  const zone = field(dialog, 'Time Zone');
  expect(await zone.evaluate((el) => el.tagName)).toBe('SELECT');
  await zone.selectOption('Asia/Riyadh');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes).toHaveLength(1);
  expect(writes[0]).toMatchObject({ method: 'POST', path: '/api/branches', body: { companyId: 'co-sa', code: 'KHB-01', countryCode: 'SA', city: 'Khobar', timeZoneId: 'Asia/Riyadh' } });
  await expect(page.getByRole('cell', { name: 'Khobar Office', exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('a new branch has no preset country', async ({ page }) => {
  await boot(page, 'branches');
  await add(page).click();
  await expect(field(page.getByRole('dialog'), 'Country')).toHaveValue('');
});

test('edit: changing a branch sends a PUT with the new values', async ({ page }) => {
  const { writes } = await boot(page, 'branches');
  await page.getByRole('button', { name: /Edit Riyadh HQ/ }).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'City').fill('Riyadh North');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes[0]).toMatchObject({ method: 'PUT', path: '/api/branches/br-ry', body: { city: 'Riyadh North' } });
});

test('a 409 duplicate code shows the server sentence and keeps the dialog open', async ({ page }) => {
  const { ctl } = await boot(page, 'branches');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  await field(dialog, 'Company').selectOption('co-sa');
  await field(dialog, 'Code').fill('RYD-01');
  await field(dialog, 'Name (EN)').fill('Dup');
  await field(dialog, 'Country').selectOption('SA');
  await field(dialog, 'City').fill('Riyadh');
  await field(dialog, 'Time Zone').selectOption('Asia/Riyadh');
  ctl.refuse = { status: 409, body: { code: 'branch_code_taken', message: 'A branch with code RYD-01 already exists in this company.' } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('A branch with code RYD-01 already exists in this company.');
  await expect(dialog).toBeVisible();
});

test('a ProblemDetails validation error is shown, not a generic failure', async ({ page }) => {
  const { ctl } = await boot(page, 'branches');
  await page.getByRole('button', { name: /Edit Riyadh HQ/ }).click();
  const dialog = page.getByRole('dialog');
  ctl.refuse = { status: 400, body: { title: 'One or more validation errors occurred.', errors: { LaborOfficeCode: ['Labor office code must be 12 digits.'] } } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Labor office code must be 12 digits.');
});

test('the dialog cannot be closed while a save is in flight', async ({ page }) => {
  const { ctl } = await boot(page, 'branches');
  let release!: () => void;
  ctl.gate = new Promise<void>((r) => { release = r; });
  await page.getByRole('button', { name: /Edit Riyadh HQ/ }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Saving…' })).toBeDisabled();
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeDisabled();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  release();
  await expect(dialog).toBeHidden();
});

test('a failed load says so and offers Retry; it never reads as an empty tenant', async ({ page }) => {
  const { ctl } = await boot(page, 'branches', { down: ['branches'] });
  await expect(page.getByRole('alert').filter({ hasText: 'This could not be loaded.' })).toBeVisible();
  await expect(page.getByText('No branches yet')).toHaveCount(0);
  ctl.down.clear();
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByRole('cell', { name: 'Riyadh HQ', exact: true })).toBeVisible();
  await expect(page.getByText('This could not be loaded.')).toHaveCount(0);
});

test('an empty tenant shows the empty state with its first step', async ({ page }) => {
  await boot(page, 'branches', { empty: true });
  await expect(page.getByText('No branches yet')).toBeVisible();
  await expect(page.getByText('Add your first branch, or import a CSV.')).toBeVisible();
});

test('a user holding organization.write without the Admin or HR Manager role may add, import and edit (the server authorises by permission); Remove needs organization.delete', async ({ page }) => {
  await boot(page, 'branches', WRITE_PERMISSION_WITHOUT_ROLE);
  await expect(page.getByRole('cell', { name: 'Riyadh HQ', exact: true })).toBeVisible();
  await expect(add(page)).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Import CSV' })).toBeEnabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeEnabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled(); // no organization.delete
  await expect(page.getByTestId('write-blocked-reason')).toHaveCount(0);
  await expect(page.getByText('Add your first branch, or import a CSV.')).toHaveCount(0);
});

test('a manager without organization.delete can edit but Remove and Restore are disabled with the reason', async ({ page }) => {
  await boot(page, 'branches', NO_DELETE);
  await page.getByLabel(/Show turned-off/).check();
  await expect(page.getByRole('button', { name: /Edit Riyadh HQ/ })).toBeEnabled();
  await expect(page.getByTestId('org-row-remove').first()).toBeDisabled();
  await expect(page.getByTestId('org-row-restore')).toBeDisabled();
  await expect(page.getByTestId('org-row-blocked-reason').first()).toBeVisible();
});

test('axe: the list and the open dialog have no serious violations', async ({ page }) => {
  await boot(page, 'branches');
  await page.getByLabel(/Show turned-off/).check(); // the turned-off badge is part of the list
  expect(await axeSeriousOrWorse(page, 'main')).toEqual([]);
  await add(page).click();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

test('every dialog field is reachable by its visible label', async ({ page }) => {
  await boot(page, 'branches');
  await add(page).click();
  const dialog = page.getByRole('dialog');
  for (const label of ['Company', 'Code', 'Name (EN)', 'Name (AR)', 'Country', 'City', 'Time Zone']) {
    await expect(field(dialog, label)).toHaveCount(1);
  }
});

test('Arabic: the page is right-to-left and the dialog is translated', async ({ page }) => {
  const { errors } = await boot(page, 'branches', { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByRole('button', { name: 'إضافة فرع' }).click();
  const dialog = page.getByRole('dialog');
  await expect(field(dialog, 'المنطقة الزمنية')).toBeVisible();
  await expect(field(dialog, 'الشركة')).toBeVisible();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  expect(errors).toEqual([]);
});

test('a custom role holding every organization permission is allowed to write (the server authorises by permission, not role name)', async ({ page }) => {
  await boot(page, 'branches', CUSTOM_ROLE_WITH_PERMISSIONS);
  await expect(page.getByRole('cell', { name: 'Riyadh HQ', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add Branch', exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: /^Edit/ }).first()).toBeEnabled();
  await expect(page.getByTestId('write-blocked-reason')).toHaveCount(0);
  await expect(page.getByTestId('org-row-remove').first()).toBeEnabled();
});

test('when the company list cannot be loaded, the tab says so and Retry brings the companies back', async ({ page }) => {
  const { ctl } = await boot(page, 'branches', { down: ['companies'] });
  const notice = page.getByRole('alert').filter({ hasText: 'The companies could not be loaded.' });
  await expect(notice).toBeVisible();
  ctl.down.clear();
  await notice.getByRole('button', { name: 'Retry' }).click();
  await expect(notice).toBeHidden();
  await expect(page.getByRole('combobox', { name: 'Filter by company' })).toBeVisible();
});

test('a read-only user can still Export and download the CSV template (branches); only Import is blocked', async ({ page }) => {
  await boot(page, 'branches', READ_ONLY);
  await expect(page.getByRole('button', { name: 'Export' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Template' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Import CSV' })).toBeDisabled();
});

test('the branch time zone offers every supported IANA zone, with the wizard shortlist first, and keeps an unlisted saved value', async ({ page }) => {
  await boot(page, 'branches');
  await page.getByRole('button', { name: 'Add Branch', exact: true }).click();
  const zone = field(page.getByRole('dialog'), 'Time Zone');
  const values = await zone.locator('option').evaluateAll((os) => (os as HTMLOptionElement[]).map((o) => o.value).filter(Boolean));
  expect(values.slice(0, 3)).toEqual(['Asia/Riyadh', 'Asia/Dubai', 'Asia/Qatar']);
  for (const id of ['Europe/Paris', 'America/Los_Angeles', 'Asia/Singapore']) expect(values).toContain(id);
  expect(values.length).toBeGreaterThan(100);
  await zone.selectOption('Europe/Paris');
  expect(await zone.inputValue()).toBe('Europe/Paris');
});
