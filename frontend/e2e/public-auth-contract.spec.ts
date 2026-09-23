import { expect, test } from '@playwright/test';
import {
  consumeFragmentToken,
  normalizeWorkspace,
  resolveWorkspaceAlias,
  safeLocalReturnPath,
  tokenFromFragment,
} from '../src/lib/publicAuth';

test.use({ storageState: { cookies: [], origins: [] } });

test('workspace aliases are deterministic and blank values fall through', () => {
  const params = new URLSearchParams('workspace=%20&workspace=Primary&Tenant=ignored&tenant=Secondary&tenantSlug=Third&w=Fourth');
  expect(resolveWorkspaceAlias(params)).toBe('primary');
  expect(resolveWorkspaceAlias(new URLSearchParams('workspace=%20&tenant=%20ACME%20&tenantSlug=third'))).toBe('acme');
  expect(resolveWorkspaceAlias(new URLSearchParams('tenantSlug=%20Third%20&w=Fourth'))).toBe('third');
  expect(resolveWorkspaceAlias(new URLSearchParams('workspace=&tenant=&tenantSlug=&w='))).toBe('');
  expect(normalizeWorkspace(' \tMixed-Case\n')).toBe('mixed-case');
  expect(safeLocalReturnPath('/reports?month=1')).toBe('/reports?month=1');
  for (const unsafe of ['https://evil.test', '//evil.test', '/\\evil.test', 'javascript:alert(1)', 'data:text/html,x']) {
    expect(safeLocalReturnPath(unsafe)).toBe('/dashboard');
  }
});

test('fragment capture preserves raw and encoded plus then scrubs the complete fragment', () => {
  const locations = [
    { hash: '#token=A+B%2FC%3D&ignored=1', pathname: '/reset-password', search: '?workspace=acme' },
    { hash: '#token=A%2BB%2FC%3D', pathname: '/reset-password', search: '?workspace=acme' },
  ];
  for (const location of locations) {
    let replacement = '';
    const history = {
      state: { marker: true },
      replaceState: (_state: unknown, _unused: string, url?: string | URL | null) => { replacement = String(url); },
    };
    expect(consumeFragmentToken(location, history)).toBe('A+B/C=');
    expect(replacement).toBe('/reset-password?workspace=acme');
  }
  expect(tokenFromFragment('#other=x')).toBe('');
});

test('login with stale storage makes no protected bootstrap or refresh request', async ({ page }) => {
  const apiRequests: string[] = [];
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'stale-access');
    localStorage.setItem('zayra_refresh_token', 'stale-refresh');
  });
  page.on('request', (request) => {
    if (new URL(request.url()).pathname.startsWith('/api/')) apiRequests.push(request.url());
  });

  await page.goto('/login?workspace=ACME');
  await expect(page.locator('#li-em')).toBeVisible();
  await page.waitForTimeout(300);

  expect(apiRequests).toEqual([]);
  await expect(page).toHaveURL(/\/login\?workspace=ACME$/);
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBe('stale-access');
  expect(await page.evaluate(() => localStorage.getItem('zayra_refresh_token'))).toBe('stale-refresh');
});

test('reset is fragment-token-first, isolated, exact-password, and single-submit', async ({ page }) => {
  let resetPosts = 0;
  let refreshPosts = 0;
  let body: Record<string, unknown> | undefined;
  let headers: Record<string, string> | undefined;
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'stale-access');
    localStorage.setItem('zayra_refresh_token', 'stale-refresh');
  });
  await page.route('**/api/auth/refresh', async (route) => {
    refreshPosts += 1;
    await route.fulfill({ status: 200, json: { accessToken: 'unexpected' } });
  });
  await page.route('**/api/auth/reset-password', async (route) => {
    resetPosts += 1;
    body = route.request().postDataJSON();
    headers = route.request().headers();
    await new Promise((resolve) => setTimeout(resolve, 100));
    await route.fulfill({ status: 204, body: '' });
  });

  await page.goto('/reset-password?workspace=%20&tenant=%20ACME%20#token=A+B%2FC%3D');
  await expect.poll(() => new URL(page.url()).hash).toBe('');
  await expect(page.locator('#credential-workspace')).toHaveValue('acme');
  expect(await page.locator('body').textContent()).not.toContain('A+B/C=');

  const exactPassword = '  Paß word🔐  ';
  await page.locator('#credential-password').fill(exactPassword);
  await page.locator('#credential-confirm').fill(exactPassword);
  await page.locator('button[type="submit"]').dblclick();
  await expect(page.getByRole('link', { name: 'Return to sign in' })).toHaveAttribute('href', '/login?workspace=acme');

  expect(resetPosts).toBe(1);
  expect(refreshPosts).toBe(0);
  expect(body).toEqual({ resetToken: 'A+B/C=', newPassword: exactPassword, tenantSlug: 'acme' });
  expect(headers?.authorization).toBeUndefined();
  expect(headers?.['x-company-id']).toBeUndefined();
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBe('stale-access');
  expect(await page.evaluate(() => localStorage.getItem('zayra_refresh_token'))).toBe('stale-refresh');
});

test('invitation ignores response tokens and a 401 never refreshes or replays', async ({ page }) => {
  let invitationPosts = 0;
  let refreshPosts = 0;
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'stale-access');
    localStorage.setItem('zayra_refresh_token', 'stale-refresh');
  });
  await page.route('**/api/auth/refresh', async (route) => {
    refreshPosts += 1;
    await route.fulfill({ status: 200, json: { accessToken: 'unexpected' } });
  });
  await page.route('**/api/auth/accept-invitation', async (route) => {
    invitationPosts += 1;
    await route.fulfill({
      status: 401,
      contentType: 'application/json',
      body: JSON.stringify({ message: 'Invitation token is invalid or expired.' }),
    });
  });

  await page.goto('/accept-invitation?workspace=Demo#token=invite-once');
  await page.locator('#credential-password').fill('  Exact Password!  ');
  await page.locator('#credential-confirm').fill('  Exact Password!  ');
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.locator('.lx-fault')).toContainText('invalid or expired');

  expect(invitationPosts).toBe(1);
  expect(refreshPosts).toBe(0);
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBe('stale-access');
  expect(await page.evaluate(() => localStorage.getItem('zayra_refresh_token'))).toBe('stale-refresh');
});

test('successful invitation stores no returned session credential', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'existing-access');
    localStorage.setItem('zayra_refresh_token', 'existing-refresh');
    localStorage.setItem('unrelated-preference', 'keep-me');
  });
  await page.route('**/api/auth/accept-invitation', async (route) => {
    await route.fulfill({
      status: 200,
      json: { accessToken: 'must-not-store', refreshToken: 'must-not-store-refresh' },
    });
  });
  await page.goto('/accept-invitation?workspace=Demo#token=invite-once');
  await page.locator('#credential-password').fill('  Exact Password!  ');
  await page.locator('#credential-confirm').fill('  Exact Password!  ');
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.getByRole('link', { name: 'Return to sign in' })).toHaveAttribute('href', '/login?workspace=demo');
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBe('existing-access');
  expect(await page.evaluate(() => localStorage.getItem('zayra_refresh_token'))).toBe('existing-refresh');
  expect(await page.evaluate(() => localStorage.getItem('unrelated-preference'))).toBe('keep-me');
});

test('query token is ignored and missing fragment sends no one-use request', async ({ page }) => {
  let posts = 0;
  await page.route('**/api/auth/reset-password', async (route) => {
    posts += 1;
    await route.fulfill({ status: 500 });
  });
  await page.goto('/reset-password?workspace=acme&token=query-secrets-are-forbidden');
  await page.locator('#credential-password').fill('LongEnough1!');
  await page.locator('#credential-confirm').fill('LongEnough1!');
  await page.getByRole('button', { name: 'Update password' }).click();
  await expect(page.locator('.lx-fault')).toContainText('missing its secure token');
  expect(posts).toBe(0);
});

test('missing workspace rejects before the one-use request', async ({ page }) => {
  let posts = 0;
  await page.route('**/api/auth/reset-password', async (route) => {
    posts += 1;
    await route.fulfill({ status: 500 });
  });
  await page.goto('/reset-password#token=fragment-secret');
  await page.locator('#credential-password').fill('LongEnough1!');
  await page.locator('#credential-confirm').fill('LongEnough1!');
  await page.getByRole('button', { name: 'Update password' }).click();
  await expect(page.locator('.lx-fault')).toContainText('Workspace is required');
  expect(posts).toBe(0);
});

test('ambiguous gateway failure locks the one-use form against replay', async ({ page }) => {
  let posts = 0;
  await page.route('**/api/auth/reset-password', async (route) => {
    posts += 1;
    await route.fulfill({ status: 502, json: { message: 'Bad gateway' } });
  });
  await page.goto('/reset-password?workspace=acme#token=fragment-secret');
  await page.locator('#credential-password').fill('LongEnough1!');
  await page.locator('#credential-confirm').fill('LongEnough1!');
  await page.getByRole('button', { name: 'Update password' }).click();
  await expect(page.locator('.lx-fault')).toContainText('may have succeeded');
  await expect(page.getByRole('link', { name: 'Return to sign in' }))
    .toHaveAttribute('href', '/login?workspace=acme');
  expect(posts).toBe(1);
});

test('impersonate query is inert and unsafe return paths remain on the login surface', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'existing-access'));
  await page.goto('/login?workspace=acme&impersonate=attacker-jwt&from=javascript:alert(1)');
  await expect(page.locator('#li-em')).toBeVisible();
  await expect(page).toHaveURL(/\/login\?/);
  expect(new URL(page.url()).searchParams.has('impersonate')).toBe(false);
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBe('existing-access');
});
