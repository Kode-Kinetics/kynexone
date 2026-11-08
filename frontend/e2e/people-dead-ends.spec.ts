import { expect, test, type Page } from '@playwright/test';
import { bootEmployeeWorkspaceFixture } from './employee-workspace.fixture';

/**
 * Hotfix: People dead ends (G-002 transfer approvals, G-003 older mobile profile change requests).
 * Every API call is intercepted; nothing is written. Start `next dev -p 5174` first (never the :5173 image: it is stale).
 *
 *   npx playwright test -c e2e/playwright.people-dead-ends.config.ts
 */

type TransferStepName = 'CurrentManager' | 'NewManager' | 'Hr';

const transferView = (over: Record<string, unknown> = {}, step: TransferStepName = 'CurrentManager') => ({
  transfer: {
    id: 'tr-1', employeeId: 901, currentBranch: 'Riyadh', currentDepartment: 'HR', currentDesignation: 'Officer',
    newBranch: '', newDepartment: 'Finance', newDesignation: '', effectiveDate: '2026-12-01', reason: 'Restructure',
    status: step === 'CurrentManager' ? 'PendingCurrentManager' : step === 'NewManager' ? 'PendingNewManager' : 'PendingHrApproval',
    createdAtUtc: '2026-11-01T08:00:00Z',
  },
  employeeName: 'Sam Subject', employeeCode: 'E-0901', currentManagerName: 'Cora Current', newManagerName: 'Nadia New',
  awaitingStep: step, awaitingName: step === 'CurrentManager' ? 'Cora Current' : step === 'NewManager' ? 'Nadia New' : null,
  canDecide: true, ...over,
});

interface Boot {
  arabic?: boolean;
  permissions: string[];
  pending?: unknown[];
  forEmployee?: unknown[];
  inbox?: unknown[];
  profileChanges?: unknown[];
  /** What the approve endpoint answers; default 200. */
  approveStatus?: number;
}

async function boot(page: Page, url: string, o: Boot) {
  const calls = { pending: 0, posts: [] as string[], decisions: [] as Array<{ path: string; body: unknown }> };
  let pending = [...(o.pending ?? [])];
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(arabic => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  }, o.arabic ?? false);
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/me') return reply({
      id: 'reviewer', employeeId: 17, tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Cora Current', roles: ['HR Manager'],
      permissions: o.permissions, accountType: 'Company', isGroupScope: false,
      companies: [{ id: 'fixture-company', name: 'Evostel', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en' });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/employees/transfers/pending') { calls.pending += 1; return reply(pending); }
    if (/^\/api\/employees\/transfers\/[^/]+\/approve-/.test(path) && request.method() === 'POST') {
      calls.posts.push(path);
      if ((o.approveStatus ?? 200) !== 200) return reply({ message: 'The staffing budget is full for Finance.' }, o.approveStatus);
      pending = [];
      return reply({});
    }
    if (path === '/api/employees/901/transfers') return reply(o.forEmployee ?? []);
    if (path === '/api/approval-requests') return reply({ items: [], total: 0, page: 1, pageSize: 25 });
    if (path === '/api/hr/profile-completion/inbox') return reply(o.inbox ?? []);
    if (/^\/api\/ess\/profile-change-requests\/[^/]+\/(approve|reject)$/.test(path) && request.method() === 'POST') {
      calls.decisions.push({ path, body: request.postDataJSON() });
      return reply({ id: 'lc-1', status: path.endsWith('approve') ? 'Approved' : 'Rejected' });
    }
    if (path === '/api/ess/profile-change-requests/mine') return reply(o.profileChanges ?? []);
    if (path === '/api/ess/hr-requests/my') return reply([]);
    if (path === '/api/hr-requests/categories') return reply([]);
    if (path === '/api/employees/901') return reply({ id: 901, employeeCode: 'E-0901', fullName: 'Sam Subject', englishName: 'Sam Subject', status: 'Active', transfers: [], documents: [], history: [], complianceRecords: [], companyId: 'fixture-company' });
    return reply({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(url);
  return { calls, errors };
}

/** The employee workspace on its shared fixture, plus this lane's transfer endpoints. */
async function bootWorkspace(page: Page, permissions: string[], forEmployee: unknown[]) {
  const posts: string[] = [];
  await bootEmployeeWorkspaceFixture(page, { permissions });
  await page.route('**/api/employees/901/transfers', route => route.fulfill({ json: forEmployee }));
  await page.route('**/api/employees/transfers/*/approve-*', route => { posts.push(new URL(route.request().url()).pathname); return route.fulfill({ json: {} }); });
  await page.goto('/people/901?section=employment&panel=transfers');
  return { posts };
}

const MANAGER = ['manager.approve', 'employees.read', 'dashboard.read', 'approvals.read'];
const HR = ['manager.approve', 'employees.approve', 'employees.read', 'employees.write', 'dashboard.read', 'approvals.read'];

test.describe('G-002 transfer approvals have a surface', () => {
  test('the Approval Center lists the transfer the manager may decide and approves it after a confirmation', async ({ page }) => {
    const { calls, errors } = await boot(page, '/approvals', { permissions: MANAGER, pending: [transferView()] });
    const panel = page.getByRole('region', { name: 'Transfers waiting for you' });
    await expect(panel).toBeVisible({ timeout: 45_000 });
    await expect(panel).toContainText('Sam Subject: Finance');
    await expect(panel).toContainText('Waiting for the current manager: Cora Current');
    await panel.getByRole('button', { name: 'Approve' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Approve this transfer?');
    expect(calls.posts, 'nothing is sent before the confirmation').toEqual([]);
    await dialog.getByRole('button', { name: 'Approve transfer' }).click();
    await expect(panel.getByRole('status')).toContainText('Approved. The transfer moved on to the next step.');
    expect(calls.posts).toEqual(['/api/employees/transfers/tr-1/approve-current-manager']);
    await expect(panel.getByText('Sam Subject: Finance')).toHaveCount(0);
    expect(errors).toEqual([]);
  });

  test('cancelling the confirmation approves nothing', async ({ page }) => {
    const { calls } = await boot(page, '/approvals', { permissions: MANAGER, pending: [transferView()] });
    const panel = page.getByRole('region', { name: 'Transfers waiting for you' });
    await panel.getByRole('button', { name: 'Approve' }).click({ timeout: 45_000 });
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();
    expect(calls.posts).toEqual([]);
    await expect(panel).toContainText('Sam Subject: Finance');
  });

  test('HR approves the last step through the HR endpoint and is told it was applied', async ({ page }) => {
    const { calls } = await boot(page, '/approvals', { permissions: HR, pending: [transferView({}, 'Hr')] });
    const panel = page.getByRole('region', { name: 'Transfers waiting for you' });
    await expect(panel).toContainText('Waiting for HR approval', { timeout: 45_000 });
    await panel.getByRole('button', { name: 'Approve' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Approve transfer' }).click();
    await expect(panel.getByRole('status')).toContainText('Approved and applied to the employee record.');
    expect(calls.posts).toEqual(['/api/employees/transfers/tr-1/approve-hr']);
  });

  test('a refused approval shows the server reason and leaves the transfer in the list', async ({ page }) => {
    await boot(page, '/approvals', { permissions: HR, pending: [transferView({}, 'Hr')], approveStatus: 409 });
    const panel = page.getByRole('region', { name: 'Transfers waiting for you' });
    await panel.getByRole('button', { name: 'Approve' }).click({ timeout: 45_000 });
    await page.getByRole('dialog').getByRole('button', { name: 'Approve transfer' }).click();
    await expect(panel.getByRole('alert')).toContainText('The staffing budget is full for Finance.');
    await expect(panel).toContainText('Sam Subject: Finance');
  });

  test('a user with neither approve permission never asks for transfers', async ({ page }) => {
    const { calls } = await boot(page, '/approvals', { permissions: ['dashboard.read', 'approvals.read'], pending: [transferView()] });
    await expect(page.getByRole('heading', { name: 'Approval Center' })).toBeVisible({ timeout: 45_000 });
    await expect(page.getByRole('region', { name: 'Transfers waiting for you' })).toHaveCount(0);
    expect(calls.pending).toBe(0);
  });

  test('the panel stays out of the way when nothing is waiting', async ({ page }) => {
    await boot(page, '/approvals', { permissions: MANAGER, pending: [] });
    await expect(page.getByRole('heading', { name: 'Approval Center' })).toBeVisible({ timeout: 45_000 });
    await expect(page.getByRole('region', { name: 'Transfers waiting for you' })).toHaveCount(0);
  });

  test('the requester sees the step and who holds it on the employee Transfers panel, with no Approve button', async ({ page }) => {
    await bootWorkspace(page, ['employees.read', 'manager.approve', 'dashboard.read'], [transferView({ canDecide: false }, 'NewManager')]);
    const panel = page.getByTestId('employee-transfers');
    await expect(panel).toBeVisible({ timeout: 45_000 });
    await expect(panel).toContainText('Waiting for the new manager: Nadia New');
    await expect(panel).toContainText('Finance');
    await expect(panel.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  });

  test('HR can approve from the employee Transfers panel', async ({ page }) => {
    const { posts } = await bootWorkspace(page, ['employees.read', 'employees.approve', 'manager.approve', 'dashboard.read'], [transferView({}, 'Hr')]);
    const panel = page.getByTestId('employee-transfers');
    await panel.getByRole('button', { name: 'Approve' }).click({ timeout: 45_000 });
    await page.getByRole('dialog').getByRole('button', { name: 'Approve transfer' }).click();
    await expect(panel.getByRole('status')).toContainText('Approved and applied to the employee record.');
    expect(posts).toEqual(['/api/employees/transfers/tr-1/approve-hr']);
  });

  test('Arabic: the panel reads in Arabic and the page is right-to-left', async ({ page }) => {
    await boot(page, '/approvals', { permissions: MANAGER, pending: [transferView()], arabic: true });
    const panel = page.getByRole('region', { name: 'عمليات النقل بانتظار قرارك' });
    await expect(panel).toBeVisible({ timeout: 45_000 });
    await expect(panel).toContainText('بانتظار المدير الحالي: Cora Current');
    await expect(panel.getByRole('button', { name: 'موافقة' })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  });
});

const legacyItem = {
  requestId: 'lc-1', employee: { id: 901, code: 'E-0901', name: 'Sam Subject' }, section: null, legacy: true, status: 'PendingHR',
  submittedAtUtc: '2026-11-02T08:00:00Z', goesToFinance: false, documents: [],
  diff: [
    { field: 'phone', labelKey: 'ess.profileChange.field.phone', before: '0500000000', after: '0555555555', changed: true },
    { field: 'personalEmail', labelKey: 'ess.profileChange.field.personalEmail', before: 'old@example.com', after: 'new@example.com', changed: true },
  ],
};

test.describe('G-003 older mobile profile change requests are no longer orphaned', () => {
  test('HR sees the request in the review inbox with the fields it changes, and can accept it', async ({ page }) => {
    const { calls } = await boot(page, '/people/onboarding-review', { permissions: HR, inbox: [legacyItem] });
    const item = page.getByTestId('inbox-item');
    await expect(item).toBeVisible({ timeout: 45_000 });
    await expect(item).toContainText('Sam Subject');
    await expect(item).toContainText('Personal details from the mobile app');
    await item.getByRole('button', { name: 'Review' }).click();
    const rows = item.getByTestId('diff-row');
    await expect(rows).toHaveCount(2);
    await expect(rows.first()).toContainText('Phone');
    await expect(rows.first()).toContainText('0500000000');
    await expect(rows.first()).toContainText('0555555555');
    await item.getByRole('button', { name: 'Accept' }).first().click();
    await item.getByRole('button', { name: 'Accept' }).last().click();
    expect(calls.decisions.map(d => d.path)).toEqual(['/api/ess/profile-change-requests/lc-1/approve']);
  });

  test('the employee sees the status of the request in My requests', async ({ page }) => {
    await boot(page, '/ess/requests', {
      permissions: ['ess.read', 'ess.write', 'dashboard.read'],
      profileChanges: [
        { id: 'lc-1', status: 'PendingHR', createdAtUtc: '2026-11-02T08:00:00Z', decidedAtUtc: null, fields: ['phone'], changes: { phone: '0555555555' }, reviewNote: null },
        { id: 'lc-2', status: 'Rejected', createdAtUtc: '2026-10-20T08:00:00Z', decidedAtUtc: '2026-10-21T08:00:00Z', fields: ['personalEmail'], changes: { personalEmail: 'x@y.test' }, reviewNote: 'That address bounces.' },
      ],
    });
    const card = page.getByTestId('my-profile-changes');
    await expect(card).toBeVisible({ timeout: 45_000 });
    await expect(card).toContainText('Waiting for HR');
    await expect(card).toContainText('Phone');
    await expect(card).toContainText('Sent back by HR');
    await expect(card).toContainText('That address bounces.');
  });

  test('Arabic: the employee status card reads in Arabic', async ({ page }) => {
    await boot(page, '/ess/requests', {
      arabic: true, permissions: ['ess.read', 'ess.write', 'dashboard.read'],
      profileChanges: [{ id: 'lc-1', status: 'PendingHR', createdAtUtc: '2026-11-02T08:00:00Z', decidedAtUtc: null, fields: ['phone'], changes: { phone: '0555555555' }, reviewNote: null }],
    });
    const card = page.getByTestId('my-profile-changes');
    await expect(card).toContainText('بانتظار الموارد البشرية', { timeout: 45_000 });
    await expect(card).toContainText('رقم الجوال');
  });
});
