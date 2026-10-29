import { expect, test, type Page, type Route } from '@playwright/test';

type Handler = (route: Route, path: string, method: string) => Promise<boolean> | boolean;

/** Boots a group-scope Admin with every API call intercepted; `handler` answers the screen under test. */
async function boot(page: Page, urlTab: string, handler: Handler, permissions: string[] = ['security.manage', 'organization.write', 'payroll.read', 'payroll.write', 'loans.read', 'loans.write', 'attendance.read', 'leave.read', 'leave.write']) {
  const company = { id: 'c1', name: 'Alpha', code: 'A', countryCode: 'SA', isActive: true };
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'rollout-fixture'));
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname; const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/me') return reply({ id: 'u1', tenantId: 't1', tenantSlug: 'fixture', fullName: 'Admin', roles: ['Admin'], permissions, isGroupScope: true, accountType: 'MultiCompany', companies: [company] });
    if (path === '/api/features/disabled-keys') return reply([]);
    if (path === '/api/capabilities') return reply({ features: { 'approvalDelegations.write': false, 'approvalAuthorities.write': false } });
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (await handler(route, path, method)) return;
    if (method === 'GET') return reply([]);
    return reply({});
  });
  await page.goto(`/setup?tab=${urlTab}`);
}

const leaveType = (over: Record<string, unknown>) => ({ id: 'lt', code: 'TRN', nameEn: 'Training leave', nameAr: '', category: 'General', isPaid: true, isHalfDayAllowed: false,
  isHourlyAllowed: false, requiresAttachment: false, requiresReason: false, maxConsecutiveDays: 0, colorCode: '#2F6BFF', isActive: true, sortOrder: 0, companyId: 'c1', createdAtUtc: '2026-01-01T00:00:00Z', ...over });

test('leave types: removing a used type turns it off with the reason, and Restore brings it back', async ({ page }) => {
  let off = false; const calls: string[] = [];
  await boot(page, 'leaveTypes', async (route, path, method) => {
    if (path === '/api/leave/types' && method === 'GET') { await route.fulfill({ json: [leaveType({ isActive: !off })] }); return true; }
    if (path === '/api/leave/types/lt' && method === 'DELETE') { off = true; calls.push('remove'); await route.fulfill({ json: { outcome: 'turnedOff', message: 'Turned off.', usage: { total: 2, summary: 'Used by 2 leave requests', items: [{ kind: 'leaveRequest', label: 'leave requests', count: 2 }] } } }); return true; }
    if (path === '/api/leave/types/lt/restore') { off = false; calls.push('restore'); await route.fulfill({ json: { outcome: 'restored', message: 'Turned back on.' } }); return true; }
    return false;
  });
  await page.getByTestId('org-row-remove').first().click();
  await page.getByRole('dialog').getByRole('button', { name: 'Remove', exact: true }).click();
  await expect(page.getByTestId('org-row-notice')).toContainText('Turned off Training leave');
  await page.getByTestId('show-turned-off').check();
  await page.getByTestId('org-row-restore').click();
  await expect.poll(() => calls.join(',')).toBe('remove,restore');
});

test('leave types: a statutory type asks for the floor warning before it is turned off', async ({ page }) => {
  let flag = '';
  await boot(page, 'leaveTypes', async (route, path, method) => {
    if (path === '/api/leave/types' && method === 'GET') { await route.fulfill({ json: [leaveType({ id: 'mat', code: 'MATERNITY', nameEn: 'Maternity leave', category: 'Maternity' })] }); return true; }
    if (path === '/api/leave/types/mat' && method === 'DELETE') { flag = new URL(route.request().url()).search; await route.fulfill({ json: { outcome: 'turnedOff', message: 'Turned off.', usage: { total: 0, summary: '', items: [] } } }); return true; }
    return false;
  });
  await page.getByTestId('org-row-remove').first().click();
  await expect(page.getByTestId('remove-warning')).toContainText('statutory floors still apply');
  await page.getByRole('dialog').getByRole('button', { name: 'Remove', exact: true }).click();
  await expect.poll(() => flag).toContain('acknowledgeStatutory=LEAVE_TYPE_STATUTORY');
});

test('approval authorities and delegations hide the form the server cannot save and say so', async ({ page }) => {
  await boot(page, 'approvalDelegations', async () => false);
  await expect(page.getByTestId('capability-note')).toContainText('Delegations are not available yet.');
  await expect(page.getByRole('button', { name: 'New Delegation' })).toHaveCount(0);
  await page.goto('/setup?tab=approvalAuthorities');
  await expect(page.getByTestId('capability-note')).toContainText('Approval limits are not available yet.');
  await expect(page.getByRole('button', { name: 'New Authority' })).toHaveCount(0);
});

test('a link to a section this person cannot open lands on their first section with a note', async ({ page }) => {
  await boot(page, 'roles', async () => false, ['organization.write']);
  await expect(page).toHaveURL(/unavailable=roles/);
  await expect(page.getByTestId('setup-section-redirected')).toBeVisible();
});

test('the audit log links to the sign-in and access log', async ({ page }) => {
  // The admin audit-log endpoint answers a page object ({ items, total, page, pageSize }), never a bare array.
  await boot(page, 'adminAuditLogs', async (route, path, method) => {
    if (path !== '/api/admin/audit-logs' || method !== 'GET') return false;
    await route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
    return true;
  }, ['security.manage', 'organization.write']);
  await expect(page.getByTestId('setup-related-section')).toContainText('Sign-in and access log');
});
