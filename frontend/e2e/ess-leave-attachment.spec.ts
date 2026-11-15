import { expect, test, type Page } from '@playwright/test';

/**
 * Self-Service → Leave, against a mocked API: a leave type that needs a supporting document offers a way to attach one, the request is refused
 * on the page until one is chosen, the document is uploaded first and the request cites it by id; a half-day tick that is no longer shown is
 * not sent. Before this the form said "HR will ask you for it" while the server refused the request outright, so the leave could never be filed.
 *
 * Run: npx playwright test -c e2e/playwright.ess-workspace.config.ts e2e/ess-leave-attachment.spec.ts
 */

const types = [
  { id: 'lt-annual', code: 'ANNUAL', nameEn: 'Annual Leave', category: 'Annual', isActive: true, isPaid: true, isHalfDayAllowed: true, requiresAttachment: false, requiresReason: false },
  { id: 'lt-sick', code: 'SICK', nameEn: 'Sick Leave', category: 'Sick', isActive: true, isPaid: true, isHalfDayAllowed: false, requiresAttachment: true, requiresReason: false },
];

interface Calls { upload: number; apply: Array<Record<string, unknown>>; cancel: string[] }
interface BootOptions {
  /** Answer the apply call with this refusal (HTTP 400 `{ code, message, args }`) instead of accepting it. */
  refuseApply?: { code: string; message: string; args?: Record<string, string> };
  /** The caller's own leave requests. */
  requests?: Array<Record<string, unknown>>;
  arabic?: boolean;
}

async function boot(page: Page, opts: BootOptions = {}): Promise<Calls> {
  const calls: Calls = { upload: 0, apply: [], cancel: [] };
  const requests = (opts.requests ?? []).map((r) => ({ ...r }));
  await page.addInitScript((arabic) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
  }, !!opts.arabic);
  await page.route('**/api/**', async (route) => {
    const req = route.request();
    const path = new URL(req.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (path === '/api/auth/me') {
      return json({
        id: 'user-1', employeeId: 17, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Amira Mansour', roles: ['Employee'],
        permissions: ['ess.read', 'ess.write'], companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
      });
    }
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/leave/types') return json(types);
    if (path === '/api/ess/leave/balance') return json([]);
    if (path === '/api/leave/requests' && req.method() === 'GET') return json({ items: requests, total: requests.length, page: 1 });
    const cancelMatch = path.match(/^\/api\/leave\/requests\/([^/]+)\/cancel$/);
    if (cancelMatch && req.method() === 'POST') {
      calls.cancel.push(cancelMatch[1]);
      const row = requests.find((r) => r.id === cancelMatch[1]);
      if (row) row.status = 'CancellationRequested';
      return json(row ?? { id: cancelMatch[1], status: 'CancellationRequested' });
    }
    if (path === '/api/ess/documents' && req.method() === 'POST') { calls.upload += 1; return json({ id: 'doc-1' }, 201); }
    if (path === '/api/ess/leave/request') {
      calls.apply.push(req.postDataJSON());
      return opts.refuseApply ? json(opts.refuseApply, 400) : json({ id: 'lr-1' }, 201);
    }
    if (path === '/api/notifications') return json([]);
    return json({ items: [], total: 0 });
  });
  await page.goto('/ess/leave');
  return calls;
}

test('a leave type that needs a document asks for one, uploads it first, then cites it', async ({ page }) => {
  const calls = await boot(page);
  const form = page.getByTestId('my-leave-apply');
  await expect(form).toBeVisible({ timeout: 60_000 });

  await form.getByRole('combobox').selectOption('lt-sick');
  await form.locator('input[type=date]').first().fill('2027-01-07');
  await form.locator('input[type=date]').nth(1).fill('2027-01-07');
  const picker = page.getByTestId('my-leave-attachment');
  await expect(picker).toBeVisible();

  // Without the document the page refuses, and nothing reaches the server.
  await form.getByRole('button', { name: 'Send for approval' }).click();
  await expect(page.getByText('Add a supporting document. This leave type needs one.')).toBeVisible();
  expect(calls.upload + calls.apply.length).toBe(0);

  await picker.setInputFiles({ name: 'medical-note.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4 fixture') });
  await expect(form).toContainText('medical-note.pdf');
  await form.getByRole('button', { name: 'Send for approval' }).click();

  await expect(page.getByText('Your leave request was sent for approval.')).toBeVisible();
  expect(calls.upload).toBe(1);
  expect(calls.apply).toHaveLength(1);
  expect(calls.apply[0]).toMatchObject({ leaveTypeId: 'lt-sick', attachmentDocumentId: 'doc-1' });
});

test('a leave type with no document requirement shows no picker, and a half-day tick that is no longer shown is not sent', async ({ page }) => {
  const calls = await boot(page);
  const form = page.getByTestId('my-leave-apply');
  await expect(form).toBeVisible({ timeout: 60_000 });

  await form.getByRole('combobox').selectOption('lt-annual');
  await form.locator('input[type=date]').first().fill('2027-01-07');
  await form.locator('input[type=date]').nth(1).fill('2027-01-07');
  await expect(page.getByTestId('my-leave-attachment')).toHaveCount(0);
  await form.getByRole('checkbox', { name: 'Half a day only' }).check();
  // Widening the range hides the box; the leftover tick must not turn this into a half day.
  await form.locator('input[type=date]').nth(1).fill('2027-01-10');
  await expect(form.getByRole('checkbox', { name: 'Half a day only' })).toHaveCount(0);
  await form.locator('textarea').fill('Family trip');
  await form.getByRole('button', { name: 'Send for approval' }).click();

  await expect(page.getByText('Your leave request was sent for approval.')).toBeVisible();
  expect(calls.upload).toBe(0);
  expect(calls.apply[0]).toMatchObject({ leaveTypeId: 'lt-annual', dayType: 'Full' });
  expect(calls.apply[0]).not.toHaveProperty('attachmentDocumentId');
});

test('in Arabic, a leave balance that was never set up is refused in Arabic, with the year, not in the server English', async ({ page }) => {
  await boot(page, {
    arabic: true,
    refuseApply: {
      code: 'leave_balance_not_set_up',
      message: 'Leave balance for Annual Leave 2027 has not been set up.',
      args: { year: '2027', leaveType: 'Annual Leave' },
    },
  });
  const form = page.getByTestId('my-leave-apply');
  await expect(form).toBeVisible({ timeout: 60_000 });

  await form.getByRole('combobox').selectOption('lt-annual');
  await form.locator('input[type=date]').first().fill('2027-01-07');
  await form.locator('input[type=date]').nth(1).fill('2027-01-10');
  await form.locator('textarea').fill('رحلة عائلية');
  await form.getByRole('button', { name: 'إرسال للاعتماد' }).click();

  const notice = page.getByText('لم يُعدّ رصيد Annual Leave لسنة 2027 بعد. اطلب من الموارد البشرية إعداد رصيد إجازتك لسنة 2027.');
  await expect(notice).toBeVisible();
  await expect(page.getByText('has not been set up.')).toHaveCount(0);
});

test('in English, the same refusal names the leave type and the year', async ({ page }) => {
  await boot(page, { refuseApply: { code: 'leave_balance_not_set_up', message: 'x', args: { year: '2027', leaveType: 'Annual Leave' } } });
  const form = page.getByTestId('my-leave-apply');
  await expect(form).toBeVisible({ timeout: 60_000 });
  await form.getByRole('combobox').selectOption('lt-annual');
  await form.locator('input[type=date]').first().fill('2027-01-07');
  await form.locator('input[type=date]').nth(1).fill('2027-01-10');
  await form.locator('textarea').fill('Family trip');
  await form.getByRole('button', { name: 'Send for approval' }).click();
  await expect(page.getByText('Your Annual Leave balance for 2027 has not been set up yet. Ask HR to set up your 2027 leave balance.')).toBeVisible();
});

const isoInDays = (n: number) => {
  const d = new Date();
  d.setDate(d.getDate() + n);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};
const row = (id: string, status: string, startDate: string, endDate: string) => ({
  id, employeeId: 17, employeeName: 'Amira Mansour', leaveTypeId: 'lt-annual', leaveTypeName: 'Annual Leave',
  startDate, endDate, totalDays: 2, dayType: 'Full', status, reason: '', rejectionReason: '',
});

test('approved leave that has not started offers Request cancellation, and confirming it POSTs /cancel', async ({ page }) => {
  const calls = await boot(page, {
    requests: [
      row('lr-future', 'Approved', isoInDays(30), isoInDays(31)),
      row('lr-started', 'Approved', isoInDays(-3), isoInDays(2)),
      row('lr-pending', 'PendingManagerApproval', isoInDays(40), isoInDays(41)),
    ],
  });
  await expect(page.getByTestId('my-leave-apply')).toBeVisible({ timeout: 60_000 });

  // Only the future approved row offers it; a pending one keeps its plain Cancel, and started leave offers neither.
  const buttons = page.getByTestId('request-leave-cancellation');
  await expect(buttons).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Cancel request' })).toHaveCount(1);

  await buttons.click();
  await page.getByRole('dialog').getByRole('button', { name: 'Request cancellation' }).click();

  await expect(page.getByText('Cancellation requested. Your approver will review it.')).toBeVisible();
  expect(calls.cancel).toEqual(['lr-future']);
  // After the refresh the row shows its new status and no longer offers the button.
  await expect(buttons).toHaveCount(0);
});
