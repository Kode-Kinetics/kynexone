import { expect, test, type Page } from '@playwright/test';

/**
 * Approval Center, fixture lane (e2e/playwright.fixture.config.ts).
 *
 * Production, 2026-10-03: a sole Admin's own employee changes filled the queue with identical
 * rows marked only "Watching". Maker-checker was right to refuse them; the screen gave no reason,
 * no way to tell the rows apart, and no way to take one back. The API now says why
 * (decisionBlockedReason), what changes (changeSummary) and whether the caller may withdraw.
 */

const EVIDENCE = process.env.APPROVALS_EVIDENCE_DIR;
const NOW = new Date('2026-10-04T06:00:00Z');

const USER = {
  id: 'u-noah', tenantId: 't1', tenantSlug: 'solo', email: 'noah@solo.test', fullName: 'Noah Williams',
  roles: ['Admin'], accountType: 'Tenant', isGroupScope: false, companies: [],
  permissions: ['dashboard.read', 'employees.read', 'approvals.read', 'approvals.decide', 'approvals.override'],
};

function row(over: Record<string, unknown>) {
  return {
    id: 'r1', workflowId: 'w1', entityName: 'EmployeeChangeRequest', entityId: 'c1',
    title: 'Employee change approval - EMP-SA-ADMIN-2026-0001 Noah Williams', status: 'Pending',
    currentStepOrder: 2, requestedByUserId: 'u-noah', requestedForEmployeeId: 1, companyId: null,
    currentApproverEmployeeId: null, currentApproverUserId: null, currentApproverName: '',
    currentApproverRole: 'HR Manager', currentApproverType: 'Role', currentQueue: 'Role:HR Manager',
    slaHours: 48, dueAtUtc: '2026-10-05T23:05:00Z', isOverdue: false, ageHours: 7, lastRoutedAtUtc: '2026-10-03T23:05:00Z',
    escalatedAtUtc: null, escalatedToRole: '', priority: 'High', createdAtUtc: '2026-10-03T23:05:00Z', completedAtUtc: null,
    decisions: [], canDecide: false, decisionBlockedReason: null, canWithdraw: false, changeSummary: null,
    ...over,
  };
}

const MINE = row({
  id: 'mine', changeSummary: 'IBAN, passport', canWithdraw: true,
  decisionBlockedReason: 'You requested this, so someone else must approve it (maker-checker). No other active user can approve it yet: give a colleague the HR Manager or Admin role in User Management, or withdraw it.',
});
const THEIRS = row({
  id: 'theirs', entityId: 'c2', requestedByUserId: 'u-other', changeSummary: 'salary', canDecide: true,
  title: 'Employee change approval - EMP-SA-ENG-0007 Omar Haddad',
});

async function open(page: Page) {
  let withdrawn = false;
  const withdrawBodies: unknown[] = [];
  await page.clock.setFixedTime(NOW);
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
  });
  await page.route('**/api/**', (route) => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(USER);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p === '/api/approval-requests/mine/withdraw' && req.method() === 'POST') {
      withdrawn = true;
      withdrawBodies.push(req.postDataJSON());
      return json({ ...MINE, status: 'Cancelled', canWithdraw: false, decisionBlockedReason: null });
    }
    if (p === '/api/approval-requests') {
      const items = withdrawn ? [THEIRS] : [MINE, THEIRS];
      return json({ items, total: items.length, page: 1, pageSize: 25 });
    }
    return json({ items: [], total: 0 });
  });
  await page.goto('/approvals');
  await expect(page.getByRole('heading', { name: 'Approval Center' })).toBeVisible({ timeout: 30_000 });
  return { withdrawBodies };
}

test('a self-requested change says why it cannot be approved, and what it changes', async ({ page }) => {
  await open(page);
  const mine = page.getByRole('row').filter({ hasText: 'EMP-SA-ADMIN-2026-0001' });
  await expect(mine.getByText('Changes: IBAN, passport')).toBeVisible();
  await expect(mine.getByText('Your request')).toBeVisible();
  await expect(page.getByText('Watching')).toHaveCount(0);
  // The decidable row still offers the decision.
  await expect(page.getByRole('row').filter({ hasText: 'Omar Haddad' }).getByRole('button', { name: 'Review' })).toBeVisible();
  if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/approvals-list.png` });

  await mine.getByRole('button', { name: 'Details' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('note')).toContainText('You requested this, so someone else must approve it');
  await expect(dialog.getByRole('note')).toContainText('No other active user can approve it yet');
  await expect(dialog.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Withdraw request' })).toBeVisible();
  if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/approvals-self-requested-detail.png` });
});

test('the requester can withdraw their own change, and it leaves the queue', async ({ page }) => {
  const { withdrawBodies } = await open(page);
  await page.getByRole('row').filter({ hasText: 'EMP-SA-ADMIN-2026-0001' }).getByRole('button', { name: 'Details' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Reason for withdrawing (optional)').fill('Submitted twice by mistake');
  await dialog.getByRole('button', { name: 'Withdraw request' }).click();

  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('row').filter({ hasText: 'EMP-SA-ADMIN-2026-0001' })).toHaveCount(0);
  await expect(page.getByRole('row').filter({ hasText: 'Omar Haddad' })).toBeVisible();
  expect(withdrawBodies).toEqual([{ reason: 'Submitted twice by mistake' }]);
});
