import { expect, test } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type Persona } from '../setup-consolidation/fixture';

/** Destructive actions ask in a real dialog (focus starts on Cancel), and Cancel sends nothing. Native dialogs are gone. */
const employee: Persona = { roles: ['Employee'], permissions: [...ALL_PERMISSIONS] };
const mine = { id: 'lr9', employeeId: 7, employeeName: 'Me', departmentName: 'Finance', designationTitle: 'Analyst', leaveTypeId: 't1', leaveTypeName: 'Annual leave', startDate: '2026-12-01', endDate: '2026-12-03', totalDays: 3, status: 'Submitted', reason: '', submittedAtUtc: '2026-11-01T08:00:00Z', isEmergency: false };

test('Leave: Withdraw asks first, focus starts on Cancel, Cancel sends nothing, confirming sends one request', async ({ page }) => {
  const posts: string[] = [];
  page.on('dialog', d => { throw new Error(`a native dialog appeared: ${d.message()}`); });
  await boot(page, {
    persona: employee, url: '/leave',
    mocks: {
      '/api/leave/requests': { items: [mine], total: 1, page: 1, pageSize: 25 },
      '/api/leave/requests/lr9/withdraw': (_u: URL, method: string) => { if (method === 'POST') posts.push('withdraw'); return { ...mine, status: 'Withdrawn' }; },
    },
  });
  await page.getByRole('tab', { name: 'My Leave' }).or(page.getByRole('button', { name: 'My Leave' })).first().click();
  await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Withdraw this leave request?' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  expect(posts).toEqual([]);
  await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Withdraw request' }).click();
  await expect.poll(() => posts.length).toBe(1);
});

test('a long reason is sent whole: the server sets no length limit on these reasons, so the dialog invents none', async ({ page }) => {
  // Server check (backend-dotnet): leave reject/cancel/void and requisition reject take a free-text string with no
  // MaxLength/StringLength, and the payroll reversal, contra-journal and settlement-cancel reasons end in unbounded text columns.
  const posted: { reason?: string }[] = [];
  const request = { id: 'en1', employeeId: 7, employeeName: 'Layla Hassan', companyId: null, leaveTypeId: 't1', leaveTypeName: 'Annual leave', year: 2026, daysToEncash: 3, amountPerDay: 100, totalAmount: 300, currency: 'SAR', reason: '', status: 'PayrollApproved', hrNotes: '', payrollNotes: '', payrollRunId: null, payrollAdjustmentId: null, createdAtUtc: '2026-11-01T08:00:00Z', processedAtUtc: null, voidedAtUtc: null, voidReason: '' };
  await boot(page, {
    persona: { roles: ['Admin'], permissions: [...ALL_PERMISSIONS, 'payroll.approve', 'employees.approve'] }, url: '/leave',
    mocks: {
      '/api/leave/encashment': { items: [request], total: 1, page: 1, pageSize: 25 },
      '/api/leave/encashment/en1/void': request,
    },
  });
  await page.route('**/api/leave/encashment/en1/void', async (route, req) => { if (req.method() === 'POST') posted.push(req.postDataJSON()); await route.fallback(); });
  await page.getByRole('tab', { name: 'Encashment' }).or(page.getByRole('button', { name: 'Encashment' })).first().click();
  await page.getByRole('button', { name: 'Void', exact: true }).click();
  const long = 'x'.repeat(800);
  await page.getByTestId('confirm-reason').pressSequentially(long);
  await page.getByRole('dialog').getByRole('button', { name: 'Void encashment' }).click();
  await expect.poll(() => posted[0]?.reason?.length).toBe(800);
});
