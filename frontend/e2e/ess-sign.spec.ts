import { expect, test } from '@playwright/test';
import { bootEsign, EMPLOYEE_NAME } from './ess/esignMocks';

const shots = process.env.P4_SHOTS;

test('the list shows what is waiting and what is done, and says what this is', async ({ page }) => {
  const log = await bootEsign(page);
  await expect(page.getByRole('heading', { name: 'Documents to sign', exact: true })).toBeVisible();
  await expect(page.getByTestId('sign-row')).toHaveCount(2);
  await expect(page.getByTestId('sign-done')).toContainText('IT acceptable use');
  await expect(page.getByTestId('not-qualified').first()).toContainText('not a qualified signature');
  if (shots) await page.screenshot({ path: `${shots}/p4-ess-list-en-1440.png`, animations: 'disabled' });
  expect(log.errors).toEqual([]);
});

test('read, type the name, confirm with the password, get a certificate', async ({ page }) => {
  const log = await bootEsign(page);
  await page.getByTestId('sign-row').first().getByRole('link', { name: 'Read and sign' }).click();
  await expect(page).toHaveURL(/\/ess\/sign\/doc-1$/);
  // the document is shown in the page (a same-origin blob: frame the site's headers allow), with a plain link as the way around it
  await expect(page.getByTestId('pdf-preview')).toBeVisible();
  expect(await page.getByTestId('pdf-preview').getAttribute('src')).toMatch(/^blob:/);
  await expect(page.getByRole('link', { name: 'Open the PDF in a new tab' })).toBeVisible();
  // the wording is real text, in both languages, one per direction
  await expect(page.getByTestId('consent-text').locator('[lang="en"]')).toContainText('not a qualified electronic signature');
  await expect(page.getByTestId('consent-text').locator('[lang="ar"]')).toHaveAttribute('dir', 'rtl');

  const sign = page.getByRole('button', { name: 'Sign', exact: true });
  await expect(sign).toBeDisabled();
  await page.getByLabel('I have read this document').check();
  await expect(sign).toBeDisabled();
  await page.getByLabel(/Type your full name/).fill('Somebody Else');
  await expect(sign).toBeEnabled();
  if (shots) await page.screenshot({ path: `${shots}/p4-ess-sign-en-1440.png`, animations: 'disabled' });

  // wrong name: the password is asked first, then the refusal lands beside the field and focus goes back to it
  await sign.click();
  await page.getByRole('dialog').getByLabel(/Password/).fill('anything');
  if (shots) await page.screenshot({ path: `${shots}/p4-ess-stepup-en-1440.png`, animations: 'disabled' });
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm and sign' }).click();
  const nameField = page.getByLabel(/Type your full name/);
  await expect(nameField).toHaveAttribute('aria-invalid', 'true');
  await expect(nameField).toBeFocused();
  await expect(page.getByRole('alert').filter({ hasText: 'does not match your record' })).toBeVisible();
  expect(log.signs).toEqual([]);

  // right name, with stray spaces and case
  await nameField.fill(`  ${EMPLOYEE_NAME.toUpperCase()}  `);
  await sign.click();
  await page.getByRole('dialog').getByLabel(/Password/).fill('employee-pass');
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm and sign' }).click();
  await expect(page.getByTestId('sign-success')).toBeVisible();
  await expect(page.getByTestId('verification-code')).toHaveText('WXYZ-2345-HJKL');
  await expect(page.getByTestId('not-qualified')).toBeVisible();
  if (shots) await page.screenshot({ path: `${shots}/p4-ess-success-en-1440.png`, animations: 'disabled' });

  // what the browser sent: the hash of the bytes it displayed, the wording version, the proof header, an idempotency key, and NO time
  expect(log.signs).toHaveLength(1);
  const sent = log.signs[0];
  expect(sent.documentSha256).toMatch(/^[0-9a-f]{64}$/);
  expect(sent.consentVersion).toBe('ack-1');
  expect(sent.typedName).toBe(EMPLOYEE_NAME.toUpperCase());
  expect(Object.keys(sent).filter(k => /time|date|signedat/i.test(k))).toEqual([]);
  expect(log.headers.at(-1)).toMatchObject({ 'x-step-up': 'step-up-token-1' });
  expect(log.headers.at(-1)!['idempotency-key']).not.toBe('');
  expect(log.stepUps.every(s => s.purpose === 'esign')).toBe(true);

  await page.getByRole('button', { name: 'Check the record' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'The record is intact' })).toBeVisible();
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download your certificate' }).click()]);
  expect(download.suggestedFilename()).toContain('WXYZ-2345-HJKL');
  expect(log.errors).toEqual([]);
});

test('a wrong password is explained in the dialog and can be retried', async ({ page }) => {
  const log = await bootEsign(page, { stepUpFailures: 1 }, '/ess/sign/doc-1');
  await page.getByLabel('I have read this document').check();
  await page.getByLabel(/Type your full name/).fill(EMPLOYEE_NAME);
  await page.getByRole('button', { name: 'Sign', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/Password/).fill('wrong');
  await dialog.getByRole('button', { name: 'Confirm and sign' }).click();
  await expect(dialog.getByRole('alert')).toContainText('The password or code is not correct');
  await expect(dialog.getByLabel(/Password/)).toHaveValue('');
  await dialog.getByLabel(/Password/).fill('employee-pass');
  await dialog.getByRole('button', { name: 'Confirm and sign' }).click();
  await expect(page.getByTestId('sign-success')).toBeVisible();
  expect(log.stepUps).toHaveLength(2);
});

test('a document replaced after it was opened is refused, and the page reloads it', async ({ page }) => {
  const log = await bootEsign(page, { signRefusal: { status: 409, code: 'document_changed' } }, '/ess/sign/doc-1');
  await page.getByLabel('I have read this document').check();
  await page.getByLabel(/Type your full name/).fill(EMPLOYEE_NAME);
  await page.getByRole('button', { name: 'Sign', exact: true }).click();
  await page.getByRole('dialog').getByLabel(/Password/).fill('employee-pass');
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm and sign' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'was replaced after you opened it' })).toBeVisible();
  expect(log.calls.filter(c => c.endsWith('/consent')).length).toBeGreaterThan(1);
  expect(log.signs).toEqual([]);
});

test('the preview needs no relaxed header: the page sets frame-ancestors only (none), and the frame loads without a policy error', async ({ page }) => {
  const log = await bootEsign(page, {}, '/ess/sign/doc-1');
  const response = await page.request.get('/ess/sign/doc-1');
  const csp = response.headers()['content-security-policy'] ?? '';
  expect(csp).not.toMatch(/default-src|frame-src|child-src/);
  expect(csp).toContain("frame-ancestors 'none'");
  expect(response.headers()['x-frame-options']).toBe('DENY');
  await expect(page.getByTestId('pdf-preview')).toBeVisible();
  expect(log.errors.filter(e => /Content Security Policy|Refused to frame/i.test(e))).toEqual([]);
});
