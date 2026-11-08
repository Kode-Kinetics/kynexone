import { expect, test, type Page } from '@playwright/test';

/**
 * The approver path for leave (2026-11-07). A mocked API: browser contracts only, no tenant writes.
 *  - An employee's request to cancel APPROVED leave had no screen: it sat "Cancellation requested" forever.
 *    The Leave > Approvals tab and the Approval Center now list it with Approve cancellation / Keep leave,
 *    behind a confirm, and say what happened.
 *  - The Leave > Approvals tab listed one status, so a request waiting at the HR step never appeared.
 */
const DECISION_HR_STEP = {
  id: 'req-hr', employeeId: 7, employeeName: 'Omar Haddad', departmentName: 'Ops', designationTitle: 'Officer', leaveTypeId: 'annual',
  leaveTypeName: 'Annual Leave', startDate: '2026-12-01', endDate: '2026-12-03', totalDays: 3, status: 'PendingHRApproval', reason: 'Family',
  isEmergency: false, submittedAtUtc: '2026-11-06T08:00:00Z', kind: 'Decision', cancellationReason: null, cancellationRequestedAtUtc: null,
};
const CANCELLATION = {
  id: 'req-cancel', employeeId: 9, employeeName: 'Rania Saleh', departmentName: 'Ops', designationTitle: 'Analyst', leaveTypeId: 'annual',
  leaveTypeName: 'Annual Leave', startDate: '2026-12-10', endDate: '2026-12-12', totalDays: 3, status: 'CancellationRequested', reason: 'Trip',
  isEmergency: false, submittedAtUtc: '2026-11-01T08:00:00Z', kind: 'Cancellation', cancellationReason: 'Plans changed', cancellationRequestedAtUtc: '2026-11-07T08:00:00Z',
};

async function boot(page: Page, url: string, queue: unknown[]) {
  const posts: { path: string; body: unknown }[] = [];
  const errors: string[] = [];
  let current = [...queue];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
  });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (request.method() === 'POST') {
      posts.push({ path, body: request.postDataJSON() });
      // A decided cancellation leaves the queue, as the server would.
      const id = path.split('/').slice(-2)[0];
      current = current.filter(x => (x as { id: string }).id !== id);
      return reply({ id, status: path.endsWith('cancel-approve') ? 'Cancelled' : 'Approved' });
    }
    if (path === '/api/auth/me') return reply({ id: 'mgr', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Mona Manager', roles: ['HR Manager'], permissions: ['leave.read', 'leave.write', 'leave.approve', 'approvals.read', 'approvals.decide', 'employees.read'], accountType: 'Tenant', isGroupScope: true, companies: [] });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return reply([]);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/leave/requests/awaiting-decision') return reply(current);
    if (path === '/api/leave/requests/statutory-history') return reply({});
    if (path === '/api/notifications') return reply([]);
    if (path === '/api/approval-requests' || path.startsWith('/api/approval-requests/')) return reply({ items: [], total: 0, page: 1, pageSize: 25, mine: 0, team: 0, overdue: 0, all: 0 });
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(url);
  return { posts, errors };
}

const confirmDialog = (page: Page) => page.getByRole('dialog');

test('Leave > Approvals lists the HR-step request and the cancellation; Approve cancellation confirms, posts, and says so', async ({ page }) => {
  const { posts, errors } = await boot(page, '/leave', [DECISION_HR_STEP, CANCELLATION]);
  await page.getByRole('tab', { name: 'Approvals' }).click({ timeout: 90_000 });

  const decision = page.getByTestId('leave-approval-decision');
  const cancellation = page.getByTestId('leave-approval-cancellation');
  await expect(decision).toHaveCount(1);
  await expect(decision).toContainText('Omar Haddad');
  await expect(decision).toContainText('Pending HR approval');
  await expect(cancellation).toHaveCount(1);
  await expect(cancellation).toContainText('Rania Saleh');
  await expect(cancellation).toContainText('Cancellation requested');
  await expect(cancellation).toContainText('Plans changed');

  await cancellation.getByTestId('leave-cancellation-approve').click();
  await expect(confirmDialog(page)).toContainText('Approve this cancellation?');
  await expect(confirmDialog(page)).toContainText('returned to their balance');
  expect(posts).toEqual([]); // nothing is sent until the confirm
  await confirmDialog(page).getByRole('button', { name: 'Approve cancellation' }).click();

  await expect.poll(() => posts.map(p => p.path)).toEqual(['/api/leave/requests/req-cancel/cancel-approve']);
  await expect(page.getByText('Cancellation approved. The leave was cancelled and Rania Saleh was told.')).toBeVisible();
  await expect(cancellation).toHaveCount(0);
  await expect(decision).toHaveCount(1);
  expect(errors).toEqual([]);
});

test('Keep leave declines the cancellation through its own endpoint, after a confirm', async ({ page }) => {
  const { posts, errors } = await boot(page, '/leave', [CANCELLATION]);
  await page.getByRole('tab', { name: 'Approvals' }).click({ timeout: 90_000 });

  await page.getByTestId('leave-cancellation-keep').click();
  await expect(confirmDialog(page)).toContainText('Keep this leave?');
  // Backing out sends nothing.
  await confirmDialog(page).getByRole('button', { name: 'Cancel' }).click();
  expect(posts).toEqual([]);

  await page.getByTestId('leave-cancellation-keep').click();
  await confirmDialog(page).getByRole('button', { name: 'Keep leave' }).click();
  await expect.poll(() => posts.map(p => p.path)).toEqual(['/api/leave/requests/req-cancel/cancel-reject']);
  await expect(page.getByText('Leave kept. Rania Saleh was told the cancellation was declined.')).toBeVisible();
  await expect(page.getByText('No pending approvals')).toBeVisible();
  expect(errors).toEqual([]);
});

test('the Approval Center shows the same pending cancellations above its queue', async ({ page }) => {
  const { posts, errors } = await boot(page, '/approvals', [CANCELLATION]);
  const strip = page.getByTestId('leave-cancellation-queue');
  await expect(strip).toBeVisible({ timeout: 90_000 });
  await expect(strip).toContainText('Rania Saleh');
  await strip.getByTestId('leave-cancellation-approve').click();
  await confirmDialog(page).getByRole('button', { name: 'Approve cancellation' }).click();
  await expect.poll(() => posts.map(p => p.path)).toEqual(['/api/leave/requests/req-cancel/cancel-approve']);
  await expect(strip).toHaveCount(0);
  expect(errors).toEqual([]);
});
