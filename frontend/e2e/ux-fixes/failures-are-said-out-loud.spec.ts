import { expect, test } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type Persona } from '../setup-consolidation/fixture';

/**
 * A failure is said out loud: what happened, why, what to do (Screen Standard 3e). A failed load shows a Retry, never
 * "No requests found"; a refused action shows a refusal sentence and sends one request however often it is clicked.
 * Every API call is intercepted: nothing real is read or written.
 */
const persona = (extra: string[]): Persona => ({ roles: ['Admin'], permissions: [...ALL_PERMISSIONS, ...extra] });

test('HR Request Center: a failed list says so and offers Retry, instead of "No requests found"', async ({ page }) => {
  let healthy = false;
  await boot(page, {
    persona: persona(['approvals.read', 'approvals.write', 'approvals.decide']),
    url: '/hr-requests',
    mocks: {
      '/api/hr-requests': () => (!healthy
        ? { status: 500, body: { message: 'boom' } }
        : { items: [{ id: 'r1', subject: 'Salary certificate', status: 'Open', priority: 'Normal', categoryName: 'Letters', employeeId: 7, createdAtUtc: '2026-11-01T08:00:00Z', description: 'x' }], total: 1, page: 1, pageSize: 30 }),
      '/api/hr-requests/dashboard': { open: 0, inProgress: 0, resolved: 0, overdue: 0, recentRequests: [] },
    },
  });
  await page.getByRole('button', { name: 'All Requests' }).click();
  await expect(page.getByTestId('list-load-failed').first()).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText('No requests found')).toHaveCount(0);
  healthy = true;
  await page.getByRole('button', { name: 'Retry' }).first().click();
  await expect(page.getByText('Salary certificate')).toBeVisible();
});

test('Leave approvals: a refused approval shows what did not happen and why; a double click sends one request', async ({ page }) => {
  const posts: string[] = [];
  const request = { id: 'lr1', employeeId: 7, employeeName: 'Layla Hassan', departmentName: 'Finance', designationTitle: 'Analyst', leaveTypeId: 't1', leaveTypeName: 'Annual leave', startDate: '2026-12-01', endDate: '2026-12-03', totalDays: 3, status: 'PendingManagerApproval', reason: '', submittedAtUtc: '2026-11-01T08:00:00Z', isEmergency: false };
  await boot(page, {
    persona: persona(['leave.approve']),
    url: '/leave',
    mocks: {
      // The approvals tab reads what this viewer may decide now (a request at any step they hold, or a cancellation).
      '/api/leave/requests/awaiting-decision': { items: [{ ...request, kind: 'Decision', cancellationReason: null, cancellationRequestedAtUtc: null }], total: 1, truncated: false },
      '/api/leave/requests/waiting-on-others': { items: [], total: 0, truncated: false },
      '/api/leave/requests/statutory-history': {},
      '/api/leave/requests/lr1/approve': (_u: URL, method: string) => { if (method === 'POST') posts.push('approve'); return { status: 409, body: { code: 'conflict', message: 'The request was already decided by another approver.' } }; },
    },
  });
  await page.getByRole('tab', { name: 'Approvals' }).or(page.getByRole('button', { name: 'Approvals' })).first().click();
  const approve = page.getByRole('button', { name: 'Approve', exact: true });
  await expect(approve).toBeVisible({ timeout: 30_000 });
  await approve.dblclick();
  await expect(page.getByText(/The leave request was not approved: .*already decided by another approver/)).toBeVisible();
  expect(posts).toHaveLength(1);
});

test('Kody: a failed risk-score read says so and offers Retry, instead of "No risk scores yet."', async ({ page }) => {
  await boot(page, {
    persona: persona(['ai.query', 'ai.insights_view']),
    url: '/ai-assistant',
    mocks: {
      '/api/ai/status': { enabled: true, provider: 'test' },
      '/api/ai/risk-scores': { status: 500, body: { message: 'boom' } },
    },
  });
  await page.getByRole('button', { name: 'Risk Scores' }).click();
  await expect(page.getByTestId('list-load-failed')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText('No risk scores yet.')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Retry' })).toBeVisible();
});

test('HR Request Center: the heading and purpose line are Arabic under Arabic (they were English-only literals)', async ({ page }) => {
  await boot(page, { persona: persona(['approvals.read', 'approvals.write', 'approvals.decide']), url: '/hr-requests', locale: 'ar', mocks: { '/api/hr-requests/dashboard': { open: 0, inProgress: 0, resolved: 0, overdue: 0, recentRequests: [] } } });
  await expect(page.getByRole('heading', { level: 1, name: 'مركز طلبات الموارد البشرية' })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText('مكتب خدمة الموظفين')).toBeVisible();
});
