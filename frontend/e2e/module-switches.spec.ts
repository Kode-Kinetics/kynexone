import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiLogin, INTELLIFLOW_ADMIN, INTELLIFLOW_SLUG } from './helpers';
import { ALMARAI_SLUG, ORGANISATION_ADMIN } from './world';

/**
 * The module guard and the statutory lock, proven against the one organisation.
 *
 * These used to be proven by CONTRAST: an Enterprise tenant with every module on beside a per-run
 * "limited" tenant with several off. A client installation has one organisation, so the contrast is
 * now made in time instead: the organisation administrator switches optional modules OFF, the routes
 * behind them must refuse with the guard's own payload, and switching them back ON must restore them.
 *
 * Because module switches are organisation-wide, this file runs as the TEARDOWN of the `setup`
 * project (playwright.config.ts): after every browser spec has finished, so no other spec can observe
 * a module switched off. Every switch it turns off is turned back on in `finally`.
 */

/** Optional modules and one route each that the guard must close when the module is off. */
const GUARDED: Array<{ module: string; route: string }> = [
  { module: 'ai_assistant', route: '/api/ai/insights' },
  { module: 'recruitment', route: '/api/recruitment/applications' },
  { module: 'performance', route: '/api/performance/cycles' },
  { module: 'shifts', route: '/api/shifts/definitions' },
  { module: 'overtime', route: '/api/overtime/requests' },
];

/** Core routes no module switch may close. */
const ALWAYS_ALLOWED = ['/api/employees', '/api/leave/requests', '/api/attendance'];

const auth = (token: string) => ({ Authorization: `Bearer ${token}` });

/** A served route answers 200 with JSON: 401, 404, 429 and 500 are not "the guard let it through". */
async function expectServed(request: APIRequestContext, route: string, token: string): Promise<void> {
  const resp = await request.get(route, { headers: auth(token) });
  expect(resp.status(), `GET ${route} must be served (200), got ${resp.status()}: ${(await resp.text()).slice(0, 200)}`).toBe(200);
  expect(await resp.json(), `GET ${route} returned 200 with no JSON payload`).not.toBeNull();
}

async function setModule(request: APIRequestContext, token: string, module: string, enabled: boolean): Promise<void> {
  const resp = await request.put(`/api/tenant-modules/${module}`, { headers: auth(token), data: { enabled } });
  expect(resp.status(), `switch ${module} ${enabled ? 'on' : 'off'}: ${(await resp.text()).slice(0, 200)}`).toBe(200);
}

test.describe.configure({ mode: 'serial' });

test.describe('Module switches — the organisation administrator', () => {
  let adminToken: string;
  let intelliflowToken: string;

  test.beforeAll(async ({ request }) => {
    adminToken = await apiLogin(request, ORGANISATION_ADMIN.email, ORGANISATION_ADMIN.password, ALMARAI_SLUG);
    intelliflowToken = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
  });

  test('every module the licence grants is reported as licensed', async ({ request }) => {
    const resp = await request.get('/api/tenant-modules', { headers: auth(adminToken) });
    expect(resp.status()).toBe(200);
    const listing = await resp.json() as { modules: Array<{ key: string; licensed?: boolean }> };
    const unlicensed = listing.modules.filter((m) => m.licensed === false).map((m) => m.key);
    expect(unlicensed, 'the CI licence grants every module (scripts/ci/ci-licence.sh)').toEqual([]);
  });

  test('a switched-off optional module closes its routes, and switching it on restores them', async ({ request }) => {
    // Positive control first: with the modules on, a company administrator reaches every route.
    for (const { route } of GUARDED) await expectServed(request, route, intelliflowToken);

    try {
      for (const { module } of GUARDED) await setModule(request, adminToken, module, false);

      for (const { module, route } of GUARDED) {
        const resp = await request.get(route, { headers: auth(intelliflowToken) });
        expect(resp.status(), `GET ${route} with ${module} off`).toBe(403);
        // Named, so this cannot pass for an unrelated reason: a 403 from RBAC, a missing route or a bad
        // token would not carry the guard's payload naming the module it refused.
        const body = await resp.json();
        expect(body.error).toBe('feature_not_enabled');
        expect(body.feature).toBe(module);
      }

      // The core of the product is never behind a switch.
      for (const route of ALWAYS_ALLOWED) await expectServed(request, route, intelliflowToken);

      // And the organisation can read what it has switched off.
      const disabled = await request.get('/api/features/disabled-keys', { headers: auth(intelliflowToken) });
      expect(disabled.status()).toBe(200);
      const keys = await disabled.json() as string[];
      for (const { module } of GUARDED) expect(keys, `disabled-keys lists ${module}`).toContain(module);
    } finally {
      for (const { module } of GUARDED) await setModule(request, adminToken, module, true);
    }

    for (const { route } of GUARDED) await expectServed(request, route, intelliflowToken);
  });

  /**
   * THE STATUTORY LOCK. A module whose obligation is imposed by law is not the organisation's to switch
   * off — see ModuleCatalog.CanDisable. Nothing else in the browser suite covers the lock, so a refactor
   * that made statutory modules disableable would otherwise pass CI and ship. The unknown-country branch
   * is pinned by TenantModuleBehaviourTests.Saudization_UnknownCountry_IsLockedClosed.
   */
  test('a statutory module cannot be switched off', async ({ request }) => {
    const resp = await request.get('/api/tenant-modules', { headers: auth(adminToken) });
    expect(resp.status()).toBe(200);
    const listing = await resp.json() as {
      countryCode: string | null;
      modules: Array<{ key: string; enabled: boolean; canDisable: boolean; lockClass: string; lockReason: string | null }>;
    };
    expect(listing.countryCode, 'setup states SA as the home country').toBe('SA');

    const saudization = listing.modules.find((m) => m.key === 'qiwa_integration');
    expect(saudization, 'the Saudization module must be in the catalogue').toBeTruthy();
    expect(saudization!.lockClass).toBe('Statutory');
    expect(saudization!.canDisable, 'Saudization is not the organisation\'s to switch off').toBe(false);
    expect(saudization!.enabled).toBe(true);
    expect(saudization!.lockReason?.length ?? 0).toBeGreaterThan(20);

    // The switch refuses outright rather than storing a value it would then ignore.
    const attempt = await request.put('/api/tenant-modules/qiwa_integration', { headers: auth(adminToken), data: { enabled: false } });
    expect(attempt.status()).toBe(409);
    const refusal = await attempt.json();
    expect(refusal.code).toBe('module_not_disableable');
    expect(refusal.lockClass).toBe('Statutory');

    // And the runtime consequence: the locked module's routes stay reachable.
    const qiwa = await request.get('/api/qiwa/connection', { headers: auth(adminToken) });
    expect(qiwa.status(), 'a locked module\'s routes are never gated off').toBe(200);
  });
});
