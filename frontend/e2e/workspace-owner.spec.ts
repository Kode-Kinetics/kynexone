import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * The workspace owner and "Confirm it's you" on Setup → Users, over mocked API routes shaped as the contract:
 * the list's `isOwner`, POST /api/access/users/{id}/make-owner, any action answered 403 { code: 'step_up_required' }
 * until the request carries X-Step-Up, and POST /api/auth/step-up { password, totpCode? } → { stepUpToken,
 * expiresInSeconds } or 400 ERR_STEP_UP_CODE_REQUIRED / ERR_STEP_UP_WRONG. Who may do what is the server's rule
 * (OwnerAndStepUpTests); these prove the screens.
 *
 *   npx playwright test -c e2e/playwright.fixture.config.ts e2e/workspace-owner.spec.ts
 */

const SHOTS = process.env.WORKSPACE_OWNER_SHOTS;
async function shot(page: Page, name: string, outputPath: (n: string) => string) {
  const file = SHOTS ? path.join(SHOTS, `${name}.png`) : outputPath(`${name}.png`);
  if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: file, fullPage: true });
}

const ME = 'aaaaaaaa-0000-0000-0000-000000000001';
const base = {
  phoneNumber: '', status: 'Active', isActive: true, isLocked: false, mustChangePassword: false, mfaEnabled: false,
  accessMode: 'FullPortal', employeeId: 42, employeeName: 'x', employeeCode: 'EMP-1', createdAtUtc: '2026-10-01T09:00:00Z',
  lastLoginAtUtc: '2026-10-26T16:08:00Z', isOwner: false,
};
const owner = { ...base, id: '11111111-1111-1111-1111-111111111111', email: 'owner@kkdemo.com', fullName: 'Olivia Owner', employeeName: 'Olivia Owner', roles: ['Admin'], isOwner: true };
const amy = { ...base, id: '22222222-2222-2222-2222-222222222222', email: 'amy@kkdemo.com', fullName: 'Amy Admin', employeeName: 'Amy Admin', roles: ['Admin'] };
const sam = { ...base, id: '33333333-3333-3333-3333-333333333333', email: 'sam@kkdemo.com', fullName: 'Sam Staff', employeeName: 'Sam Staff', roles: ['Employee'] };

type Reply = { status: number; body?: unknown };
interface Call { method: string; path: string; body: any; stepUp: string | null }

async function mockApi(page: Page, opts: { meIsOwner?: boolean; stepUp?: Reply[]; selfOffPage?: boolean } = {}) {
  const calls: Call[] = [];
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
  });
  const self = { ...base, id: ME, email: 'me@kkdemo.com', fullName: 'Me Admin', employeeName: 'Me Admin', roles: ['Admin'], isOwner: opts.meIsOwner === true };
  let users = opts.meIsOwner ? [self, amy, sam] : [owner, self, amy, sam];
  const stepUpReplies = [...(opts.stepUp ?? [{ status: 200, body: { stepUpToken: 'confirmed-token', expiresInSeconds: 300 } }])];
  const permissions = ['users.manage', 'roles.manage', 'security.manage'];

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    let body: any = null;
    try { body = method === 'GET' ? null : request.postDataJSON(); } catch { body = null; }
    const stepUp = request.headers()['x-step-up'] ?? null;
    calls.push({ method, path: p, body, stepUp });

    if (p === '/api/auth/step-up' && method === 'POST') {
      const r = stepUpReplies.length > 1 ? stepUpReplies.shift()! : stepUpReplies[0];
      return json(r.body ?? {}, r.status);
    }
    const lock = p.match(/^\/api\/access\/users\/([^/]+)\/lock$/);
    if (lock && method === 'PATCH') {
      if (stepUp !== 'confirmed-token') return json({ code: 'step_up_required', error: 'step_up_required', message: "Confirm it's you to continue." }, 403);
      users = users.map((u) => (u.id === lock[1] ? { ...u, isLocked: true, status: 'Locked' } : u));
      return route.fulfill({ status: 204, body: '' });
    }
    const makeOwner = p.match(/^\/api\/access\/users\/([^/]+)\/make-owner$/);
    if (makeOwner && method === 'POST') {
      if (stepUp !== 'confirmed-token') return json({ code: 'step_up_required', error: 'step_up_required', message: "Confirm it's you to continue." }, 403);
      users = users.map((u) => ({ ...u, isOwner: u.id === makeOwner[1] }));
      return route.fulfill({ status: 204, body: '' });
    }
    if (method !== 'GET') return json({});
    if (p === '/api/auth/me') return json({ id: ME, tenantId: 't1', tenantSlug: 'kkdemo', email: 'me@kkdemo.com', fullName: 'Me Admin', roles: ['Admin'], accountType: 'SingleCompany', isGroupScope: true, companies: [], permissions });
    if (p === '/api/access/users') {
      // The signed-in owner on another page of the list (or filtered out by a search).
      const shown = opts.selfOffPage ? users.filter((u) => u.id !== ME) : users;
      return json({ items: shown, total: users.length, page: 1, pageSize: 20 });
    }
    const one = p.match(/^\/api\/access\/users\/([^/]+)$/);
    if (one) {
      const found = users.find((u) => u.id === decodeURIComponent(one[1]));
      return found ? json(found) : json({ message: 'Not found' }, 404);
    }
    if (p === '/api/access/roles') return json(['Admin', 'Employee'].map((name, index) => ({ id: String(index), name, permissions: [] })));
    if (p === '/api/access/ceiling') return json({ userId: ME, isAdmin: true, heldPermissions: permissions, roles: [] });
    if (p === '/api/tenant-admin/usage') return json({ activeUsers: 4, maxUsers: 20 });
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', currencyCode: 'SAR', countryCode: 'SA' });
    if (p.startsWith('/api/tenant-admin/')) return json({ message: 'Not found' }, 404);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications'
      || p === '/api/access/permissions' || p.includes('help-text')) return json([]);
    return json({ items: [], total: 0 });
  });
  return { calls, errors };
}

const rowOf = (page: Page, name: string) => page.locator('tbody tr').filter({ hasText: name });
/** Opens a row's "More actions" menu (portalled to <body>) and returns it. */
async function rowMenu(page: Page, name: string) {
  await rowOf(page, name).getByTestId('user-more-actions').click();
  return page.getByTestId('user-actions-menu');
}

async function openUsers(page: Page) {
  await page.goto('/setup?tab=users');
  await expect(rowOf(page, 'Amy Admin')).toBeVisible({ timeout: 30_000 });
}

test.use({ storageState: { cookies: [], origins: [] }, timezoneId: 'Asia/Riyadh' });

test.describe('Setup → Users: the workspace owner', () => {
  test('the owner is marked, and another Admin is offered nothing that changes their account', async ({ page }, info) => {
    const api = await mockApi(page);
    await openUsers(page);
    const ownerRow = rowOf(page, 'Olivia Owner');
    await expect(ownerRow.getByTestId('user-owner-badge')).toHaveText('Owner');
    // Only View: no "More actions" at all on the owner's row.
    await expect(ownerRow.getByTestId('user-more-actions')).toHaveCount(0);
    await expect(ownerRow.getByTestId('user-view')).toHaveCount(1);
    // Not the owner: "Make owner" is offered on nobody's row.
    await expect((await rowMenu(page, 'Amy Admin')).getByTestId('user-make-owner')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await shot(page, 'users-owner-en', info.outputPath.bind(info));
    expect(api.errors).toEqual([]);
  });

  test('the owner hands ownership to another Admin, after confirming it is them', async ({ page }, info) => {
    const api = await mockApi(page, { meIsOwner: true });
    await openUsers(page);
    await expect((await rowMenu(page, 'Sam Staff')).getByTestId('user-make-owner')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await (await rowMenu(page, 'Amy Admin')).getByTestId('user-make-owner').click();
    await expect(page.getByTestId('make-owner-explainer')).toContainText('Amy Admin will become the workspace owner');
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();

    const dialog = page.getByTestId('step-up-dialog');
    await expect(dialog).toBeVisible();
    await shot(page, 'step-up-dialog-en', info.outputPath.bind(info));
    await page.getByTestId('step-up-password').fill('My-Password-1!');
    await page.getByTestId('step-up-confirm').click();
    await expect(dialog).toHaveCount(0);
    await expect(rowOf(page, 'Amy Admin').getByTestId('user-owner-badge')).toBeVisible();

    const makeOwner = api.calls.filter((c) => c.path.endsWith('/make-owner'));
    expect(makeOwner.map((c) => c.stepUp)).toEqual([null, 'confirmed-token']);
    expect(api.calls.find((c) => c.path === '/api/auth/step-up')!.body).toEqual({ password: 'My-Password-1!' });
  });
});

test.describe('Setup → Users: the owner, wherever their own row is', () => {
  test('the owner sees "Make owner" even when their own row is not on this page', async ({ page }) => {
    await mockApi(page, { meIsOwner: true, selfOffPage: true });
    await openUsers(page);
    await expect((await rowMenu(page, 'Amy Admin')).getByTestId('user-make-owner')).toBeVisible();
  });
});

test.describe("Confirm it's you", () => {
  test('locking an Admin asks for the password, then the code, then repeats the request once', async ({ page }) => {
    const api = await mockApi(page, {
      stepUp: [
        { status: 400, body: { code: 'ERR_STEP_UP_CODE_REQUIRED' } },
        { status: 400, body: { code: 'ERR_STEP_UP_WRONG' } },
        { status: 200, body: { stepUpToken: 'confirmed-token', expiresInSeconds: 300 } },
      ],
    });
    await openUsers(page);
    await (await rowMenu(page, 'Amy Admin')).getByTestId('user-action-lock').click();
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();

    await page.getByTestId('step-up-password').fill('My-Password-1!');
    await page.getByTestId('step-up-confirm').click();
    await expect(page.getByTestId('step-up-code')).toBeVisible();
    await page.getByTestId('step-up-code').fill('123456');
    await page.getByTestId('step-up-confirm').click();
    await expect(page.getByTestId('step-up-error')).toContainText("didn't match");
    await page.getByTestId('step-up-code').fill('654321');
    await page.getByTestId('step-up-confirm').click();
    await expect(page.getByTestId('step-up-dialog')).toHaveCount(0);

    const locks = api.calls.filter((c) => c.path.endsWith('/lock'));
    expect(locks.map((c) => c.stepUp)).toEqual([null, 'confirmed-token']);
    expect(api.calls.filter((c) => c.path === '/api/auth/step-up').map((c) => c.body))
      .toEqual([{ password: 'My-Password-1!' }, { password: 'My-Password-1!', totpCode: '123456' }, { password: 'My-Password-1!', totpCode: '654321' }]);
  });

  test('cancelling sends nothing more', async ({ page }) => {
    const api = await mockApi(page);
    await openUsers(page);
    await (await rowMenu(page, 'Amy Admin')).getByTestId('user-action-lock').click();
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByTestId('step-up-cancel').click();
    await expect(page.getByTestId('step-up-dialog')).toHaveCount(0);
    expect(api.calls.filter((c) => c.path.endsWith('/lock'))).toHaveLength(1);
    expect(api.calls.some((c) => c.path === '/api/auth/step-up')).toBe(false);
  });
});
