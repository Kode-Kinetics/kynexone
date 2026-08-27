import { test, expect } from '@playwright/test';
import { apiLogin, apiPlatformLogin, INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, EVOSTEL_SLUG, EVOSTEL_ADMIN } from './helpers';

/**
 * Subscription guard tests (API-level).
 * Evostel is PastDue with ExpiresAtUtc = now+7 days — so it passes through
 * but the API must set X-Subscription-Status: PastDue.
 *
 * A Suspended tenant (we test by toggling via platform admin) must get 402.
 */
test.describe('Subscription guard enforcement', () => {
  let evostelToken: string;

  test.beforeAll(async ({ request }) => {
    evostelToken = await apiLogin(request, EVOSTEL_ADMIN.email, EVOSTEL_ADMIN.password, EVOSTEL_SLUG);
  });

  // ── PastDue still works but signals via header ────────────────────────────────

  test('Evostel (PastDue, unexpired) API passes through with X-Subscription-Status header', async ({ request }) => {
    const resp = await request.get('/api/employees', {
      headers: { Authorization: `Bearer ${evostelToken}` },
    });
    // Must pass (PastDue but not expired)
    expect([200, 204]).toContain(resp.status());
    // Should signal PastDue in response header
    const statusHeader = resp.headers()['x-subscription-status'];
    expect(statusHeader).toBe('PastDue');
  });

  // ── Active tenant has no subscription warning header ──────────────────────────

  test('IntelliFlow (Active) API response has no subscription warning header', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp  = await request.get('/api/employees', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.ok()).toBe(true);
    const statusHeader = resp.headers()['x-subscription-status'];
    expect(statusHeader ?? '').not.toBe('PastDue');
  });

  // ── Suspend subscription while tenant identity remains active ────────────────────

  test('Suspended tenant receives 402 from protected API', async ({ request }) => {
    const platformToken = await apiPlatformLogin(request);

    // Find Evostel tenant ID
    const tenantsResp = await request.get('/api/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
    });
    expect(tenantsResp.ok()).toBe(true);
    const tenants = await tenantsResp.json();
    const evostel = (tenants as Array<{ slug: string; id: string }>).find(t => t.slug === EVOSTEL_SLUG);
    if (!evostel) {
      test.skip(true, 'Evostel tenant not found — seed may not have run');
      return;
    }

    const subscription = (status: 'Suspended' | 'PastDue') => ({
      plan: 'Starter',
      status,
      billingCycle: 'Monthly',
      monthlyAmount: 299,
      currencyCode: 'USD',
      maxEmployees: 50,
      maxUsers: 10,
      maxCompanies: 1,
      maxAdminUsers: 10,
      billingEmail: 'billing@evostel.com',
      expiresAtUtc: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString(),
    });

    // Keep Tenant.IsActive true. This ensures authentication succeeds and proves that the
    // subscription guard itself—not tenant-session validation—returns the 402.
    const suspendResp = await request.put(`/api/platform/tenants/${evostel.id}/subscription`, {
      headers: { Authorization: `Bearer ${platformToken}` },
      data: subscription('Suspended'),
    });
    expect(suspendResp.ok()).toBe(true);

    try {
      const blockedResp = await request.get('/api/employees', {
        headers: { Authorization: `Bearer ${evostelToken}` },
      });
      expect(blockedResp.status()).toBe(402);
      const body = await blockedResp.json();
      expect(JSON.stringify(body)).toContain('subscription_inactive');
    } finally {
      // Always restore the isolated fixture even when an assertion fails.
      const restoreResp = await request.put(`/api/platform/tenants/${evostel.id}/subscription`, {
        headers: { Authorization: `Bearer ${platformToken}` },
        data: subscription('PastDue'),
      });
      expect(restoreResp.ok()).toBe(true);
    }
  });

  // ── Auth and platform routes bypass subscription guard ────────────────────────

  test('/api/auth/login is not blocked by subscription guard (even if suspended)', async ({ request }) => {
    // Login itself must always succeed so the subscription status page can load.
    //
    // The old assertion was `expect([200, 401, 429]).toContain(status)`, which
    // accepted a REJECTED login (401) and a RATE-LIMITED login (429) as proof
    // that login is not blocked. Both are failures of the claim: if the guard
    // ever started rejecting PastDue credentials the test would still be green.
    const resp = await request.post('/api/auth/login', {
      data: { email: EVOSTEL_ADMIN.email, password: EVOSTEL_ADMIN.password, tenantSlug: EVOSTEL_SLUG },
    });
    expect(resp.status(), `login for a PastDue tenant must succeed: ${(await resp.text()).slice(0, 200)}`).toBe(200);

    // …and it must be a real session, not a 200 shell.
    const body = await resp.json() as { accessToken?: string; token?: string };
    const accessToken = body.accessToken ?? body.token;
    expect(accessToken, 'login response must carry an access token').toBeTruthy();
    expect(accessToken!.split('.')).toHaveLength(3);
  });
});
