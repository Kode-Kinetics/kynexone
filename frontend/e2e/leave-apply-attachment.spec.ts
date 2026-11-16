import { expect, test } from '@playwright/test';
import { SHOTS, boot, openLeaveTab } from './leave-demo-setup-fixture';

/**
 * A leave type that requires a supporting document: the HR apply form takes the file, uploads it onto the employee's record first and
 * cites it by id (the server refuses the request without one). Mocked API.
 */

test('a leave type that needs a document: the apply form refuses to send without one, then uploads onto the employee and cites it', async ({ page }) => {
  const { calls, errors } = await boot(page, { roles: ['Employee'], permissions: ['leave.read', 'ess.read', 'ess.write'] });
  await openLeaveTab(page, 'Apply Leave');
  await page.locator('select').filter({ has: page.locator('option', { hasText: 'Unpaid Leave' }) }).selectOption('lt-unpaid');
  const picker = page.getByTestId('leave-apply-attachment');
  await expect(picker).toBeVisible();
  await expect(page.getByText('Upload after submission')).toHaveCount(0);
  const dates = page.locator('input[type=date]');
  await dates.nth(0).fill('2026-12-01');
  await dates.nth(1).fill('2026-12-01');
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/apply-attachment-en.png`, fullPage: true });

  await page.getByRole('button', { name: 'Submit Leave Request' }).click();
  await expect(page.getByText('Add a supporting document. This leave type needs one.')).toBeVisible();
  expect(calls.create).toHaveLength(0);
  expect(calls.essUpload).toBe(0);

  await picker.setInputFiles({ name: 'note.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4 fixture') });
  await page.getByRole('button', { name: 'Submit Leave Request' }).click();
  await expect.poll(() => calls.create.length).toBe(1);
  expect(calls.essUpload).toBe(1);
  expect(calls.hrUpload).toEqual([]);
  expect(calls.create[0]).toMatchObject({ employeeId: 9, leaveTypeId: 'lt-unpaid', attachmentDocumentId: 'doc-own' });
  await expect(page.getByText('Leave Request Submitted')).toBeVisible();
  expect(errors).toEqual([]);
});

test('HR filing for another employee uploads the document onto THAT employee, and a type without the requirement shows no picker', async ({ page }) => {
  const { calls } = await boot(page);
  await openLeaveTab(page, 'Apply Leave');
  await page.getByPlaceholder('Search by name or employee code…').fill('Omar');
  await page.getByText('Omar Saleh').first().click();
  const typeSelect = page.locator('select').filter({ has: page.locator('option', { hasText: 'Unpaid Leave' }) });
  await typeSelect.selectOption('lt-annual');
  await expect(page.getByTestId('leave-apply-attachment')).toHaveCount(0);
  await typeSelect.selectOption('lt-unpaid');
  await page.getByTestId('leave-apply-attachment').setInputFiles({ name: 'scan.png', mimeType: 'image/png', buffer: Buffer.from([0x89, 0x50, 0x4e, 0x47]) });
  const dates = page.locator('input[type=date]');
  await dates.nth(0).fill('2026-12-01');
  await dates.nth(1).fill('2026-12-02');
  await page.getByRole('button', { name: 'Submit Leave Request' }).click();
  await expect.poll(() => calls.create.length).toBe(1);
  expect(calls.hrUpload).toEqual(['/api/employees/21/documents']);
  expect(calls.essUpload).toBe(0);
  expect(calls.create[0]).toMatchObject({ employeeId: 21, attachmentDocumentId: 'doc-hr' });
});
