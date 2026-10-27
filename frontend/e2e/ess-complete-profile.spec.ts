import { expect, test } from '@playwright/test';
import { bootEss, ID_INBOX_ITEM, INBOX_ITEMS } from './ess/essMocks';

/** P2 Browser Pilot (mocked API): the employee's list and sections, step-up, pending, the HR inbox round trip. */

test('only the sections HR still needs are listed, with progress and a state in words', async ({ page }) => {
  const log = await bootEss(page, { sections: ['person', 'emergency', 'bank'], states: { bank: 'Accepted' } });
  await expect(page.getByRole('heading', { name: 'Complete your profile', exact: true })).toBeVisible();
  await expect(page.getByTestId('section-person')).toContainText('Contact details');
  await expect(page.getByTestId('section-person')).toContainText('Please confirm');
  await expect(page.getByTestId('section-emergency')).toContainText('To do');
  await expect(page.getByTestId('section-bank')).toContainText('Accepted');
  await expect(page.getByTestId('section-id')).toHaveCount(0);
  await expect(page.getByText('1 of 3 sections done')).toBeVisible();
  await expect(page.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '1');
  expect(log.errors).toEqual([]);
});

test('Confirm or correct: current values are prefilled and confirming sends no values', async ({ page }) => {
  const log = await bootEss(page, { sections: ['person'] });
  await page.getByTestId('section-person').click();
  await expect(page.getByText('Why HR asks for this')).toBeVisible();
  await expect(page.getByText('Privacy notice ess-1.')).toBeVisible();
  await expect(page.getByLabel('Personal email (required)')).toHaveValue('sam@example.test');
  await page.getByRole('button', { name: 'Confirm these details' }).click();
  await expect(page.getByText('Sent to HR. You will see the result here.')).toBeVisible();
  expect(log.submits).toEqual([{ key: 'person', noticeVersion: 'ess-1', confirm: true, values: {}, documentIds: [] }]);
  await expect(page.getByTestId('section-person')).toContainText('Waiting for HR');
});

test('a pending section is read-only', async ({ page }) => {
  await bootEss(page, { sections: ['person'], states: { person: 'Pending' } });
  await page.getByTestId('section-person').click();
  await expect(page.getByText('Pending HR review. You cannot edit this section until HR replies.')).toBeVisible();
  await expect(page.getByLabel('Personal email (required)')).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Send to HR' })).toHaveCount(0);
});

test('changes requested shows the HR note and the section can be corrected and resent', async ({ page }) => {
  const log = await bootEss(page, { sections: ['person'], states: { person: 'ChangesRequested' }, note: 'Please use your mobile number.' });
  await page.getByTestId('section-person').click();
  await expect(page.getByText('HR asked for a change')).toBeVisible();
  await expect(page.getByText('Please use your mobile number.')).toBeVisible();
  await page.getByLabel('Mobile number (required)').fill('0500000042');
  await page.getByRole('button', { name: 'Send to HR' }).click();
  await expect(page.getByText('Sent to HR. You will see the result here.')).toBeVisible();
  expect(log.submits[0].values).toEqual({ phone: '0500000042' });
});

test('server field errors are shown on the field and the summary takes focus', async ({ page }) => {
  await bootEss(page, { sections: ['person'], states: { person: 'Missing' } });
  await page.getByTestId('section-person').click();
  await page.getByLabel('Mobile number (required)').fill('bad');
  await page.getByRole('button', { name: 'Send to HR' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Enter a valid mobile number.' })).toBeVisible();
  await expect(page.getByLabel('Mobile number (required)')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByText('Fix the highlighted fields to continue.')).toBeFocused();
});

test('identity and bank ask for the password first; a wrong password is explained; the right one sends the section', async ({ page }) => {
  const log = await bootEss(page, { sections: ['bank'], stepUpFailures: 1 });
  await page.getByTestId('section-bank').click();
  await page.getByLabel('IBAN (required)').fill('SA4420000001234567891234');
  await page.getByRole('button', { name: 'Send to HR' }).click();
  const dialog = page.getByRole('dialog', { name: 'Confirm it is you' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel('Password')).toBeFocused();
  await dialog.getByLabel('Password').fill('wrong');
  await dialog.getByRole('button', { name: 'Confirm', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('The password or code is not correct.');
  await dialog.getByLabel('Password').fill('Passw0rd!xyz');
  await dialog.getByRole('button', { name: 'Confirm', exact: true }).click();
  await expect(page.getByText('Sent to HR. You will see the result here.')).toBeVisible();
  expect(log.stepUps).toHaveLength(2);
  expect(log.headers.at(-1)?.['x-step-up']).toBe('step-up-token-1');
  expect(log.submits[0].values).toEqual({ iban: 'SA4420000001234567891234' });
  await expect(page.getByText('A bank change is checked by HR, approved by Finance, and takes effect from the next day.')).toBeVisible();
});

test('the dialog returns focus to the button that opened it when cancelled', async ({ page }) => {
  await bootEss(page, { sections: ['id'] });
  await page.getByTestId('section-id').click();
  await page.getByLabel('National ID number (required)').fill('1000000008');
  const send = page.getByRole('button', { name: 'Send to HR' });
  await send.click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(send).toBeFocused();
});

test('uploads are checked for type and size before they leave the browser, and listed once attached', async ({ page }) => {
  const log = await bootEss(page, { sections: ['documents'] });
  await page.getByTestId('section-documents').click();
  await page.getByLabel('File (PDF, JPG or PNG, up to 5 MB)').setInputFiles({ name: 'run.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('MZ') });
  await expect(page.getByText('Choose a PDF, JPG or PNG file.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Upload', exact: true })).toBeDisabled();
  await page.getByLabel('File (PDF, JPG or PNG, up to 5 MB)').setInputFiles({ name: 'big.pdf', mimeType: 'application/pdf', buffer: Buffer.alloc(5 * 1024 * 1024 + 1, 1) });
  await expect(page.getByText('This file is larger than 5 MB.')).toBeVisible();
  await page.getByLabel('File (PDF, JPG or PNG, up to 5 MB)').setInputFiles({ name: 'id-front.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n%%EOF') });
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  await expect(page.getByRole('listitem').filter({ hasText: 'id-front.pdf' })).toContainText('Checked');
  expect(log.uploads).toHaveLength(1);
  await page.getByRole('button', { name: 'Send to HR' }).click();
  await expect(page.getByText('Sent to HR. You will see the result here.')).toBeVisible();
  expect(log.submits[0].documentIds).toEqual(['doc-1']);
});

test('an API that predates sections falls back to the original page', async ({ page }) => {
  await bootEss(page, { legacyApi: true });
  await expect(page.getByRole('heading', { name: 'My employee details', exact: true })).toBeVisible();
});

test('HR reviews a submission: before and after in words, accept, and the bank row says it goes to Finance', async ({ page }) => {
  const log = await bootEss(page, { persona: 'hr' });
  await expect(page.getByRole('heading', { name: 'Profile completion review', exact: true })).toBeVisible();
  const items = page.getByTestId('inbox-item');
  await expect(items).toHaveCount(2);
  await expect(items.nth(1)).toContainText('Goes to Finance');
  await items.nth(0).getByRole('button', { name: 'Review' }).click();
  const person = items.nth(0);
  await expect(person.getByRole('row', { name: /Mobile number/ })).toContainText('Changed');
  await expect(person.getByRole('row', { name: /Personal email/ })).toContainText('Unchanged');
  await person.getByRole('button', { name: 'Accept', exact: true }).first().click();
  await person.getByLabel('Review note').fill('Checked');
  await person.getByRole('button', { name: 'Accept', exact: true }).last().click();
  await expect(page.getByText('Accepted and applied to the employee record.')).toBeVisible();
  expect(log.decisions).toEqual([{ id: 'req-person', verb: 'approve', body: { notes: 'Checked' } }]);
  const bank = page.getByTestId('inbox-item');
  await bank.getByRole('button', { name: 'Review' }).click();
  await bank.getByRole('button', { name: 'Accept', exact: true }).first().click();
  await bank.getByRole('button', { name: 'Accept', exact: true }).last().click();
  await expect(page.getByText('Accepted. Finance now has to approve the new bank details; they apply from the next day.')).toBeVisible();
  await expect(page.getByText('Nothing is waiting for review.')).toBeVisible();
});

test('HR sends a section back: the note is required', async ({ page }) => {
  const log = await bootEss(page, { persona: 'hr', hrItems: [INBOX_ITEMS[0]] });
  const item = page.getByTestId('inbox-item');
  await item.getByRole('button', { name: 'Review' }).click();
  await item.getByRole('button', { name: 'Request changes' }).click();
  const send = item.getByRole('button', { name: 'Send back to employee' });
  await expect(send).toBeDisabled();
  await item.getByLabel('Note to the employee (required)').fill('Use your own number.');
  await send.click();
  await expect(page.getByText('Sent back to the employee.')).toBeVisible();
  expect(log.decisions).toEqual([{ id: 'req-person', verb: 'reject', body: { notes: 'Use your own number.' } }]);
});

test('HR without the sensitive permission is told values are masked', async ({ page }) => {
  await bootEss(page, { persona: 'hr', hrPermissions: ['employees.read', 'employees.write'] });
  await expect(page.getByText('Values are masked. You need the sensitive-data permission to see them in full.')).toBeVisible();
});

test('an expatriate sees Iqama, passport and expiries, each expiry with its Hijri date beside it', async ({ page }) => {
  await bootEss(page, { sections: ['id'], idClass: 'expat' });
  await page.getByTestId('section-id').click();
  await expect(page.getByLabel('Iqama number')).toBeVisible();
  await expect(page.getByLabel('Passport number')).toBeVisible();
  await expect(page.getByLabel('National ID number (Emirates ID, QID or Civil ID)')).toHaveCount(0);
  const expiry = page.getByLabel('Iqama expiry date');
  await expect(expiry).toHaveValue('2027-02-02');
  await expect(page.getByTestId('hijri-iqamaExpiry')).toContainText('Hijri:');
  await expect(page.getByTestId('hijri-iqamaExpiry')).toContainText('1448');
  await expect(page.getByTestId('hijri-passportExpiry')).toHaveCount(0);
  await page.getByLabel('Passport expiry date').fill('2030-02-02');
  await expect(page.getByTestId('hijri-passportExpiry')).toContainText('AH');
  if (process.env.P2_SHOTS) await page.screenshot({ path: `${process.env.P2_SHOTS}/p2-ess-identity-hijri-en-1440.png`, animations: 'disabled' });
});

test('HR sees the Hijri date under each expiry in the review diff, before and after', async ({ page }) => {
  await bootEss(page, { persona: 'hr', hrItems: [ID_INBOX_ITEM] });
  const item = page.getByTestId('inbox-item');
  await item.getByRole('button', { name: 'Review' }).click();
  await expect(item.getByTestId('hijri-before-iqamaExpiry')).toContainText('Hijri:');
  await expect(item.getByTestId('hijri-after-iqamaExpiry')).toContainText('Hijri:');
  await expect(item.getByTestId('hijri-after-iqamaNumber')).toHaveCount(0);
});
