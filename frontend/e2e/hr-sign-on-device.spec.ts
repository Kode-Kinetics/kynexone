import { expect, test } from '@playwright/test';
import { bootEsign, EMPLOYEE_NAME, EMPLOYEE_PASSWORD, HR_PASSWORD } from './ess/esignMocks';

const shots = process.env.P4_SHOTS;

test('HR requests a signature, then the employee signs in person with their own password', async ({ page }) => {
  const log = await bootEsign(page, { persona: 'hr' });
  const panel = page.getByTestId('signatures-panel');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('signature-row')).toHaveCount(3);
  await expect(panel).toContainText('not a qualified electronic signature');
  // CD66: profile documents say when a file was not virus-scanned, and refuse a quarantined one
  await expect(page.getByTestId('doc-scan').first()).toContainText('Not virus-scanned (trusted source)');
  await expect(page.getByTestId('doc-scan').nth(1)).toContainText('It cannot be downloaded');

  // request one more
  await panel.getByLabel('Document to sign').selectOption({ label: 'Handbook - handbook.pdf' });
  await panel.getByRole('button', { name: 'Request signature' }).click();
  await expect(panel.getByRole('status')).toContainText('Signature requested');
  expect(log.requests).toEqual([{ documentId: 'doc-4' }]);
  await expect(panel.getByTestId('signature-row')).toHaveCount(4);

  // sign in person
  await panel.getByTestId('signature-row').filter({ hasText: 'Employment contract' }).getByRole('button', { name: 'Sign in person' }).click();
  const dialog = page.getByRole('dialog', { name: /Sign in person/ });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId('pdf-preview')).toBeVisible();
  const sign = dialog.getByRole('button', { name: 'Sign', exact: true });
  await expect(sign).toBeDisabled();
  await dialog.getByLabel('The employee has read this document').check();
  await dialog.getByLabel(/types their full name/).fill(EMPLOYEE_NAME);
  const password = dialog.getByLabel(/The employee's own password/);
  await expect(password).toHaveAttribute('type', 'password');
  await expect(password).toHaveAttribute('autocomplete', 'new-password');
  // HR's own password does not work, and the field is emptied
  await password.fill(HR_PASSWORD);
  await sign.click();
  await expect(dialog.getByRole('alert')).toContainText("The employee's password or code is not correct");
  await expect(password).toHaveValue('');
  expect(log.deviceSigns).toEqual([]);
  if (shots) await page.screenshot({ path: `${shots}/p4-hr-device-en-1440.png`, animations: 'disabled' });
  await password.fill(EMPLOYEE_PASSWORD);
  await sign.click();
  await expect(dialog).toBeHidden();
  await expect(panel.getByRole('status')).toContainText('Signed.');
  await expect(panel.getByTestId('signature-row').filter({ hasText: 'Employment contract' })).toContainText('on the HR device');
  expect(log.deviceAuths.map(a => a.employeeId)).toEqual([901, 901]);
  // CD60: the step-up asks for a token bound to this document and the hash of the bytes on screen
  for (const a of log.deviceAuths) expect(a).toMatchObject({ documentId: 'doc-1', documentSha256: expect.stringMatching(/^[0-9a-f]{64}$/) });
  expect(log.deviceAuths[1].documentSha256).toBe(log.deviceSigns[0].documentSha256);
  expect(log.deviceSigns).toHaveLength(1);
  expect(log.deviceSigns[0]).toMatchObject({ stepUp: 'device-token-1', typedName: EMPLOYEE_NAME, consentVersion: 'ack-2' });

  await panel.getByTestId('signature-row').filter({ hasText: 'IT acceptable use' }).getByRole('button', { name: 'Check the record' }).click();
  await expect(panel.getByRole('status').filter({ hasText: 'The record is intact' })).toBeVisible();
  if (shots) await page.screenshot({ path: `${shots}/p4-hr-signatures-en-1440.png`, animations: 'disabled' });
  expect(log.errors).toEqual([]);
});

test('a record that no longer checks out is reported as such', async ({ page }) => {
  await bootEsign(page, { persona: 'hr', verifyValid: false });
  const panel = page.getByTestId('signatures-panel');
  await panel.getByTestId('signature-row').filter({ hasText: 'IT acceptable use' }).getByRole('button', { name: 'Check the record' }).click();
  await expect(panel.getByRole('alert').filter({ hasText: 'could not be verified' })).toBeVisible();
});
