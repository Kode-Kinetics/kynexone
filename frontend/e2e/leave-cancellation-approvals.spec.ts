import { expect, test, type Page } from '@playwright/test';

/**
 * The approver path for leave (2026-11-07). A mocked API: browser contracts only, no tenant writes.
 *  - An employee's request to cancel APPROVED leave had no screen: it sat "Cancellation requested" forever.
 *    The Leave > Approvals tab and the Approval Center now list it with Approve cancellation / Keep leave,
 *    behind a confirm, and say what happened.
 *  - The Leave > Approvals tab listed one status, so a request waiting at the HR step never appeared.
 *  - A viewer who sees leave but does not decide it (HR Officer, Supervisor) keeps a read-only "Waiting on others".
 *  - A queue the server stopped reading at its cap says so, with the true total, instead of looking complete.
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
const WAITING_MANAGER_STEP = {
  id: 'req-wait-1', employeeId: 11, employeeName: 'Layla Nasser', leaveTypeName: 'Sick Leave', startDate: '2026-12-15', endDate: '2026-12-16', totalDays: 2,
  status: 'PendingManagerApproval', kind: 'Decision', stepNumber: 1, holderRole: 'Manager', holderNames: ['Mona Manager'],
};
const WAITING_HR_STEP = {
  id: 'req-wait-2', employeeId: 12, employeeName: 'Yusuf Aziz', leaveTypeName: 'Annual Leave', startDate: '2026-12-20', endDate: '2026-12-22', totalDays: 3,
  status: 'PendingHRApproval', kind: 'Decision', stepNumber: 2, holderRole: 'HR Manager', holderNames: [],
};

interface Options {
  roles?: string[];
  permissions?: string[];
  queue?: unknown[];
  waiting?: unknown[];
  queueTotal?: number;
  cut?: boolean;
}

async function boot(page: Page, url: string, opts: Options = {}) {
  const posts: { path: string; body: unknown }[] = [];
  const errors: string[] = [];
  let current = [...(opts.queue ?? [])];
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
    if (path === '/api/auth/me') return reply({ id: 'mgr', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Mona Manager', roles: opts.roles ?? ['HR Manager'], permissions: opts.permissions ?? ['leave.read', 'leave.write', 'leave.approve', 'approvals.read', 'approvals.decide', 'employees.read'], accountType: 'Tenant', isGroupScope: true, companies: [] });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return reply([]);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/leave/requests/awaiting-decision') return reply({ items: current, total: opts.queueTotal ?? current.length, truncated: opts.cut === true });
    if (path === '/api/leave/requests/waiting-on-others') return reply({ items: opts.waiting ?? [], total: (opts.waiting ?? []).length, truncated: false });
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
  const { posts, errors } = await boot(page, '/leave', { queue: [DECISION_HR_STEP, CANCELLATION] });
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
  const { posts, errors } = await boot(page, '/leave', { queue: [CANCELLATION] });
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
  const { posts, errors } = await boot(page, '/approvals', { queue: [CANCELLATION] });
  const strip = page.getByTestId('leave-cancellation-queue');
  await expect(strip).toBeVisible({ timeout: 90_000 });
  await expect(strip).toContainText('Rania Saleh');
  await strip.getByTestId('leave-cancellation-approve').click();
  await confirmDialog(page).getByRole('button', { name: 'Approve cancellation' }).click();
  await expect.poll(() => posts.map(p => p.path)).toEqual(['/api/leave/requests/req-cancel/cancel-approve']);
  await expect(strip).toHaveCount(0);
  expect(errors).toEqual([]);
});

for (const role of ['HR Officer', 'Supervisor']) {
  test(`${role} decides nothing but keeps a read-only "Waiting on others" with the step and who holds it`, async ({ page }) => {
    const { posts, errors } = await boot(page, '/leave', {
      roles: [role], permissions: ['leave.read', 'employees.read'], queue: [], waiting: [WAITING_MANAGER_STEP, WAITING_HR_STEP],
    });
    await page.getByRole('tab', { name: 'Approvals' }).click({ timeout: 90_000 });

    await expect(page.getByText('No pending approvals')).toBeVisible();
    const section = page.getByTestId('leave-waiting-on-others');
    await expect(section).toContainText('Waiting on others');
    const rows = section.getByTestId('leave-waiting-row');
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText('Layla Nasser');
    await expect(rows.nth(0)).toContainText('At step 1');
    await expect(rows.nth(0)).toContainText('Waiting for Mona Manager (Manager)');
    await expect(rows.nth(1)).toContainText('Yusuf Aziz');
    await expect(rows.nth(1)).toContainText('Waiting for HR Manager');
    // Read-only: nothing to press, nothing sent.
    await expect(section.getByRole('button')).toHaveCount(0);
    expect(posts).toEqual([]);
    expect(errors).toEqual([]);
  });
}

test('a queue the server stopped reading at its cap says so, with the true total', async ({ page }) => {
  const { errors } = await boot(page, '/leave', { queue: [DECISION_HR_STEP], queueTotal: 1500, cut: true });
  await page.getByRole('tab', { name: 'Approvals' }).click({ timeout: 90_000 });
  await expect(page.getByTestId('leave-queue-cut')).toContainText('Showing the first 1,000 of 1,500 pending requests.');
  expect(errors).toEqual([]);
});

test('a complete queue shows no cap notice', async ({ page }) => {
  await boot(page, '/leave', { queue: [DECISION_HR_STEP] });
  await page.getByRole('tab', { name: 'Approvals' }).click({ timeout: 90_000 });
  await expect(page.getByTestId('leave-approval-decision')).toHaveCount(1);
  await expect(page.getByTestId('leave-queue-cut')).toHaveCount(0);
});
