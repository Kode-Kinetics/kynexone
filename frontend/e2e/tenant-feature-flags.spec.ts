import { test, expect, APIRequestContext } from '@playwright/test';
import {
  apiLogin,
  INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN,
  EVOSTEL_SLUG, EVOSTEL_ADMIN,
  apiPlatformLogin,
} from './helpers';

/**
 * Feature flag tests — verifies that:
 * 1. Disabled features return 403 from the API (backend enforcement).
 * 2. Enabled (or unguarded) features actually SERVE the resource — HTTP 200.
 * 3. Platform admin can toggle feature flags.
 *
 * Evostel has: ai_assistant=false, recruitment=false, performance=false, shifts=false
 * IntelliFlow has: all=true
 */

/**
 * A route that is NOT blocked by the feature guard must return the resource.
 * `not.toBe(403)` is satisfied by 401, 404, 429 and 500, so it could not
 * distinguish "the guard let it through" from "the route no longer exists".
 */
async function expectEnabledFeatureServes(
  request: APIRequestContext,
  endpoint: string,
  token: string,
): Promise<void> {
  const resp = await request.get(endpoint, { headers: { Authorization: `Bearer ${token}` } });
  expect(resp.status(), `GET ${endpoint} must be served (200), got ${resp.status()}: ${(await resp.text()).slice(0, 200)}`)
    .toBe(200);
  // A 200 with a non-JSON body would mean the proxy answered, not the API.
  const body = await resp.json();
  expect(body, `GET ${endpoint} returned 200 with no JSON payload`).not.toBeNull();
}

test.describe('Feature flag enforcement (API-level)', () => {
  let intelliflowToken: string;
  let evostelToken: string;

  test.beforeAll(async ({ request }) => {
    intelliflowToken = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    evostelToken     = await apiLogin(request, EVOSTEL_ADMIN.email, EVOSTEL_ADMIN.password, EVOSTEL_SLUG);
  });

  // ── Evostel: disabled features return 403 ────────────────────────────────────

  test('Evostel: AI assistant API returns 403 (feature disabled)', async ({ request }) => {
    const resp = await request.get('/api/ai/insights', {
      headers: { Authorization: `Bearer ${evostelToken}` },
    });
    expect(resp.status()).toBe(403);
  });

  test('Evostel: recruitment API returns 403 (feature disabled)', async ({ request }) => {
    const resp = await request.get('/api/recruitment/applications', {
      headers: { Authorization: `Bearer ${evostelToken}` },
    });
    expect(resp.status()).toBe(403);
  });

  test('Evostel: performance API returns 403 (feature disabled)', async ({ request }) => {
    const resp = await request.get('/api/performance/cycles', {
      headers: { Authorization: `Bearer ${evostelToken}` },
    });
    expect(resp.status()).toBe(403);
  });

  test('Evostel: shifts API returns 403 (feature disabled)', async ({ request }) => {
    const resp = await request.get('/api/shifts/definitions', {
      headers: { Authorization: `Bearer ${evostelToken}` },
    });
    expect(resp.status()).toBe(403);
  });

  test('Evostel: overtime API returns 403 (feature disabled)', async ({ request }) => {
    const resp = await request.get('/api/overtime/requests', {
      headers: { Authorization: `Bearer ${evostelToken}` },
    });
    expect(resp.status()).toBe(403);
  });

  // ── IntelliFlow: enabled features pass through ────────────────────────────────
  //
  // `expect(status).not.toBe(403)` was true of 404, 401, 429 and 500 alike, so a
  // deleted route or a crashed handler read as "the feature flag lets it
  // through". An enabled feature must actually SERVE the resource: assert 200.

  test('IntelliFlow: AI assistant API returns 200 (feature enabled)', async ({ request }) => {
    await expectEnabledFeatureServes(request, '/api/ai/insights', intelliflowToken);
  });

  test('IntelliFlow: recruitment API returns 200 (feature enabled)', async ({ request }) => {
    await expectEnabledFeatureServes(request, '/api/recruitment/applications', intelliflowToken);
  });

  test('IntelliFlow: performance API returns 200 (feature enabled)', async ({ request }) => {
    await expectEnabledFeatureServes(request, '/api/performance/cycles', intelliflowToken);
  });

  // ── Always-allowed routes bypass feature guard ────────────────────────────────

  test('Evostel: /api/employees is always allowed (not behind any feature flag)', async ({ request }) => {
    await expectEnabledFeatureServes(request, '/api/employees', evostelToken);
  });

  test('Evostel: /api/leave/requests is always allowed', async ({ request }) => {
    await expectEnabledFeatureServes(request, '/api/leave/requests', evostelToken);
  });

  test('Evostel: /api/attendance is always allowed', async ({ request }) => {
    await expectEnabledFeatureServes(request, '/api/attendance', evostelToken);
  });

  // ── Platform API: feature flag read ────────────────────────────────────────────

  test('Platform admin can read IntelliFlow feature flags', async ({ request }) => {
    const platformToken = await apiPlatformLogin(request);
    const tenants = await request.get('/api/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
    });
    expect(tenants.ok()).toBe(true);
    const list = await tenants.json();
    const intelliflow = (list as Array<{ slug: string; featureFlags?: unknown }>)
      .find(t => t.slug === INTELLIFLOW_SLUG);
    expect(intelliflow).toBeDefined();
  });

  // ── /api/features endpoint always allowed (tenant reads own flags) ─────────────

  test('Evostel tenant can read own feature flags via /api/features/disabled-keys', async ({ request }) => {
    const resp = await request.get('/api/features/disabled-keys', {
      headers: { Authorization: `Bearer ${evostelToken}` },
    });
    expect([200, 204]).toContain(resp.status());
  });
});
