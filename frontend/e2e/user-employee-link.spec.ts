import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * User Management → link an existing login to its employee record, and invite an employee with no login.
 *
 * The defect: an admin created Noah's login in Create User; Self-Service resolves the employee only from the
 * login↔employee link, which nothing in the UI could create. These specs drive the dialog over mocked API
 * routes (the server's rules are proven by backend EmployeeLoginLinkTests); what they prove is the screen:
 * the labelled entry points, the plain-language status, the reason requirement, the exact request sent, the
 * sign-out instruction, and the copyable invitation link when no email went out.
 */

const noah = {
  id: '11111111-1111-1111-1111-111111111111',
  email: 'noah.williams@kkdemo.com',
  fullName: 'Noah Williams',
  phoneNumber: '',
  status: 'Active',
  isActive: true,
  isLocked: false,
  mustChangePassword: false,
  roles: ['Employee'],
  accessMode: 'FullPortal',
  employeeId: null,
  createdAtUtc: '2026-10-01T09:00:00Z',
};

const employees = [
  { id: 42, publicId: 'p-42', employeeCode: 'EMP-0042', fullName: 'Noah Williams', department: 'Sales', status: 'Active' },
  // Invited, not Active: the picker must still find an employee who was invited but has no login yet.
  { id: 43, publicId: 'p-43', employeeCode: 'EMP-0043', fullName: 'Layla Haddad', department: 'Finance', status: 'Invited' },
  { id: 44, publicId: 'p-44', employeeCode: 'EMP-0044', fullName: 'Omar Saleh', department: 'Operations', status: 'Active' },
];

interface Captured { method: string; path: string; body: unknown }

async function openUserManagement(page: Page) {
  const writes: Captured[] = [];
  const errors: string[] = [];
  let linked = false;
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
  });
  await page.route('**/api/**', (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const pathname = url.pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

    if (request.method() !== 'GET') {
      writes.push({ method: request.method(), path: pathname, body: request.postDataJSON() });
      if (pathname === '/api/access/employee-logins/link-existing') {
        linked = true;
        return json({ employeeId: 42, userId: noah.id, email: noah.email, status: 'Active', accessMode: 'FullPortal', isActive: true, alreadyLinked: false });
      }
      if (pathname === '/api/access/employee-logins/invite') {
        return json({
          userId: '22222222-2222-2222-2222-222222222222', employeeId: 43, email: 'layla.haddad@kkdemo.com', accessMode: 'ESSOnly',
          status: 'Invited', invitationExpiresAtUtc: '2026-10-10T09:00:00Z',
          invitationUrl: 'https://app.example.test/accept-invitation?tenant=kkdemo&token=abc123',
          emailDeliveryConfigured: false, emailSent: false,
          deliveryMessage: 'No email delivery is configured for this workspace, so no invitation was sent. Share the invitation link with them directly.',
        }, 201);
      }
      return json({}); // Never touch a real account.
    }

    if (pathname === '/api/auth/me') return json({ id: 'admin-1', tenantId: 't1', email: 'admin@kkdemo.com', fullName: 'Tenant Admin', roles: ['Admin'], accountType: 'Group', isGroupScope: true, companies: [], permissions: ['users.manage', 'roles.manage', 'security.manage'] });
    if (pathname === '/api/access/users') {
      const row = linked ? { ...noah, employeeId: 42, employeeName: 'Noah Williams', employeeCode: 'EMP-0042' } : noah;
      return json({ items: [row], total: 1, page: 1, pageSize: 20 });
    }
    if (pathname === '/api/employees') {
      const search = (url.searchParams.get('search') ?? '').toLowerCase();
      const status = url.searchParams.get('status');
      const items = employees.filter((e) => (!status || e.status === status)
        && (e.fullName.toLowerCase().includes(search) || e.employeeCode.toLowerCase().includes(search)));
      return json({ items, total: items.length, page: 1, pageSize: 8 });
    }
    if (pathname === '/api/access/employee-logins/42') return json({
      employeeId: 42, employeeName: 'Noah Williams', workEmail: 'noah.williams@kkdemo.com', linkedLogin: null,
      matchingLogin: { userId: noah.id, email: noah.email, status: 'Active', accessMode: 'FullPortal', isActive: true },
      nextAction: 'link_existing', reason: null,
    });
    if (pathname === '/api/access/employee-logins/43') return json({
      employeeId: 43, employeeName: 'Layla Haddad', workEmail: 'layla.haddad@kkdemo.com', linkedLogin: null,
      matchingLogin: null, nextAction: 'invite', reason: null,
    });
    if (pathname === '/api/access/employee-logins/44') return json({
      employeeId: 44, employeeName: 'Omar Saleh', workEmail: 'omar.saleh@kkdemo.com', linkedLogin: null,
      matchingLogin: { userId: '33333333-3333-3333-3333-333333333333', email: 'omar.saleh@kkdemo.com', status: 'Active', accessMode: 'HRPortal', isActive: true },
      nextAction: 'blocked', reason: 'English server text', reasonCode: 'login_other_company', reasonSubject: 'Riyadh Branch',
    });
    if (pathname === '/api/access/roles') return json(['Admin', 'HR Manager', 'Employee'].map((name, index) => ({ id: String(index), name, permissions: [] })));
    if (pathname === '/api/access/ceiling') return json({ userId: 'admin-1', isAdmin: true, heldPermissions: ['users.manage', 'roles.manage', 'security.manage'], roles: [] });
    if (pathname === '/api/access/security-settings') return json({ passwordMinLength: 12, passwordRequireUppercase: true, passwordRequireLowercase: true, passwordRequireDigit: true, passwordRequireSpecial: true });
    if (pathname === '/api/tenant-admin/usage') return json({ activeUsers: 2, maxUsers: 20 });
    if (pathname === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', currencyCode: 'SAR', countryCode: 'SA' });
    if (pathname === '/api/features/disabled-keys' || pathname === '/api/features/modules' || pathname === '/api/notifications'
      || pathname === '/api/access/permissions' || pathname.includes('help-text')) return json([]);
    return json({ items: [], total: 0 });
  });
  await page.goto('/user-management');
  await expect(page.getByRole('heading', { name: 'User Management & Access Control' })).toBeVisible();
  return { writes, errors };
}

async function pickEmployee(page: Page, name: string) {
  const dialog = page.getByRole('dialog');
  await dialog.getByPlaceholder('Search employees by name or code').fill(name.split(' ')[0]);
  await dialog.getByRole('button', { name: new RegExp(name) }).click();
}

test('an existing login is linked to its employee record from the user row', async ({ page }, testInfo) => {
  const { writes, errors } = await openUserManagement(page);
  const row = page.getByRole('row').filter({ hasText: noah.email });
  await row.getByRole('button', { name: 'Link to employee record', exact: true }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('heading', { name: 'Link to employee record' })).toBeVisible();
  await expect(dialog.getByText(`Login: ${noah.email}`)).toBeVisible();
  await pickEmployee(page, 'Noah Williams');

  const status = dialog.getByTestId('employee-login-status');
  await expect(status.getByText(`The login ${noah.email} uses Noah Williams's work email.`, { exact: false })).toBeVisible();
  const linkButton = dialog.getByRole('button', { name: 'Link this login', exact: true });
  await expect(linkButton).toBeDisabled(); // A reason is required before anything is sent.
  expect(writes).toEqual([]);

  await dialog.getByLabel('Reason (kept in the audit trail)').fill('Login created in User Management before the employee record');
  await linkButton.click();

  await expect(dialog.getByRole('status')).toContainText('Linked to Noah Williams.');
  await expect(dialog.getByRole('status')).toContainText('Noah Williams must sign out and sign in again to see Self-Service.');
  expect(writes).toEqual([{
    method: 'POST',
    path: '/api/access/employee-logins/link-existing',
    body: { employeeId: 42, userId: noah.id, reason: 'Login created in User Management before the employee record' },
  }]);
  await page.screenshot({ path: testInfo.outputPath('employee-linked.png') });

  await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
  // The row now names the employee and no longer offers the link.
  await expect(row.getByText('Employee: Noah Williams (EMP-0042)')).toBeVisible();
  await expect(row.getByRole('button', { name: 'Link to employee record' })).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('an employee with no login is invited, and the link is copyable when no email went out', async ({ page, context }, testInfo) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']).catch(() => {});
  const { writes, errors } = await openUserManagement(page);
  await page.getByRole('button', { name: 'Invite employee', exact: true }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('heading', { name: 'Invite employee' })).toBeVisible();
  await pickEmployee(page, 'Layla Haddad');
  await expect(dialog.getByText('Layla Haddad has no login yet. An invitation to layla.haddad@kkdemo.com lets them set a password and use Self-Service.')).toBeVisible();

  await dialog.getByRole('button', { name: 'Send self-service invitation', exact: true }).click();
  await expect(dialog.getByText('No email delivery is configured for this workspace', { exact: false })).toBeVisible();
  const link = dialog.getByLabel('Invitation link', { exact: true });
  await expect(link).toHaveValue('https://app.example.test/accept-invitation?tenant=kkdemo&token=abc123');
  await dialog.getByRole('button', { name: 'Copy link', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Link copied', exact: true })).toBeVisible();

  expect(writes).toEqual([{ method: 'POST', path: '/api/access/employee-logins/invite', body: { employeeId: 43, accessMode: 'ESSOnly' } }]);
  await page.screenshot({ path: testInfo.outputPath('employee-invited.png') });
  expect(errors).toEqual([]);
});

test('a login that works in another company is explained, and nothing is offered that would widen its access', async ({ page }) => {
  const { writes, errors } = await openUserManagement(page);
  await page.getByRole('button', { name: 'Invite employee', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await pickEmployee(page, 'Omar Saleh');
  await expect(dialog.getByText('This login works in a different company. Give it access to Riyadh Branch first, or link it from that company.')).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Link this login' })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Send self-service invitation' })).toHaveCount(0);
  expect(writes).toEqual([]);
  expect(errors).toEqual([]);
});
