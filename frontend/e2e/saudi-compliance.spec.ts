import { test, expect } from '@playwright/test';
import {
  apiLogin, apiPlatformLogin, tenantLogin,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_SLUG,
  EVOSTEL_ADMIN, EVOSTEL_SLUG,
} from './helpers';

// Track A — Saudi Regulatory Compliance E2E coverage.
// These tests assume the demo seed (IntelliFlow = Enterprise w/ all features,
// Evostel = Starter w/o qiwa_integration) and a running API + frontend.

test.describe('Saudi compliance — API authorization', () => {
  test('platform admin cannot access tenant saudi-compliance dashboard', async ({ request }) => {
    const token = await apiPlatformLogin(request);
    const resp = await request.get('/api/saudi-compliance/dashboard', {
      headers: { Authorization: `Bearer ${token}` },
    });
    // Platform token lacks tenant_id claim / tenant permissions.
    expect([401, 403]).toContain(resp.status());
  });

  test('IntelliFlow admin can access saudi-compliance dashboard', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/dashboard', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();
    expect(body).toHaveProperty('qiwa');
    expect(body).toHaveProperty('wps');
    expect(body).toHaveProperty('gosi');
    expect(body).toHaveProperty('actionItems');
  });

  // FeatureFlagGuardFilter must block a module the tenant has switched off. This used to be
  // asserted against `qiwa_integration`, which no longer demonstrates anything: Saudization is a
  // ModuleLock.Statutory module, so a stored `false` for it is deliberately NOT honoured and the
  // route stays open. The gate is now proved against an OPTIONAL module — one the fixture disables
  // in the same loop — and the statutory lock has its own test below.
  test('Evostel admin (shifts switched off) is blocked on the Shifts API', async ({ request }) => {
    const token = await apiLogin(request, EVOSTEL_ADMIN.email, EVOSTEL_ADMIN.password, EVOSTEL_SLUG);
    const resp = await request.get('/api/shifts/definitions', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(403);
    // Named, so this cannot pass for an unrelated reason: a 403 from RBAC, a missing route or a
    // bad token would not carry the guard's payload naming the module it refused.
    const body = await resp.json();
    expect(body.error).toBe('feature_not_enabled');
    expect(body.feature).toBe('shifts');
  });

  // POSITIVE CONTROL for the test above. Without it, deleting `/api/shifts` from the catalog's
  // RoutePrefixes — the exact regression that matters — would still leave that test green if the
  // route happened to 403 for some other reason. A tenant WITH the module must reach the same URL.
  test('the same Shifts route is reachable for a tenant that has the module on', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/shifts/definitions', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
  });

  /**
   * THE STATUTORY LOCK. A module whose obligation is imposed by law is not the tenant's to switch
   * off, and a stored `false` for one is deliberately ignored — see ModuleCatalog.CanDisable, whose
   * Statutory branch treats an UNKNOWN country as "the obligation applies" precisely so that a
   * tenant whose localisation row was never written cannot silently escape it.
   *
   * This is the control the old version of this file destroyed rather than pinned: named for the
   * Evostel fixture, it asserted that our Saudi pilot client MAY switch Saudization off. Nothing
   * else in the browser suite covers the lock, so a refactor that made statutory modules
   * disableable would have passed CI and shipped.
   */
  test('a statutory module cannot be switched off, and a stored false for one is ignored', async ({ request }) => {
    const token = await apiLogin(request, EVOSTEL_ADMIN.email, EVOSTEL_ADMIN.password, EVOSTEL_SLUG);
    const auth = { Authorization: `Bearer ${token}` };

    const resp = await request.get('/api/tenant-modules', { headers: auth });
    expect(resp.status()).toBe(200);
    const listing = await resp.json() as {
      countryCode: string | null;
      modules: Array<{
        key: string; enabled: boolean; canDisable: boolean;
        lockClass: string; lockReason: string | null;
      }>;
    };

    const saudization = listing.modules.find((m) => m.key === 'qiwa_integration');
    expect(saudization, 'the Saudization module must be in the catalogue').toBeTruthy();

    // NON-VACUITY, asserted rather than assumed. The setup fixture disables SIX features in one
    // loop, `recruitment` and `qiwa_integration` among them. If that loop silently stopped working,
    // every assertion below would pass for the wrong reason — `qiwa_integration` would read
    // enabled:true because nothing had ever tried to switch it off. So first prove the loop worked,
    // on an OPTIONAL module from the same loop, where a stored false IS honoured.
    const optional = listing.modules.find((m) => m.key === 'recruitment');
    expect(optional, 'the control module must be in the catalogue').toBeTruthy();
    expect(optional!.lockClass).toBe('Optional');
    expect(optional!.canDisable).toBe(true);
    expect(
      optional!.enabled,
      'the fixture disables recruitment in the same loop as qiwa_integration — if this is still ' +
      'enabled the loop did not run and the statutory assertions below prove nothing',
    ).toBe(false);

    // The lock itself.
    expect(saudization!.lockClass).toBe('Statutory');
    expect(saudization!.canDisable, 'Saudization is not the tenant\'s to switch off').toBe(false);
    expect(saudization!.lockReason?.length ?? 0).toBeGreaterThan(20);
    // The whole point: the fixture DID store `false` for this key, and it is not honoured.
    expect(
      saudization!.enabled,
      'a stored false on a statutory module must be ignored, not obeyed',
    ).toBe(true);

    // THE FIXTURE NOW HAS A COUNTRY, and this line was updated deliberately rather than relaxed.
    //
    // It used to assert the tenant had NO country, because POST /api/platform/tenants wrote no
    // localisation row — so the lock above was exercising the fail-closed UNKNOWN-country branch.
    // Platform admins now state a home jurisdiction at tenant creation (it sets the statutory
    // jurisdiction, the first company's country and the tenant's timezone), so the fixture is SA
    // and the lock is proven through the SA branch instead.
    //
    // The comment this replaces asked that the unknown-country branch not be left uncovered. It is
    // not: `TenantModuleBehaviourTests.Saudization_UnknownCountry_IsLockedClosed` calls
    // ModuleCatalog.CanDisable with a null country and asserts it stays locked, and a sibling
    // pins the same for an unrecognised code ("ZZZ"). Both were already there; that branch is now
    // unreachable through the API by construction, which is the stronger outcome.
    expect(
      listing.countryCode ?? null,
      'the fixture tenant states SA at creation, so the statutory lock above is proven through ' +
      'the SA branch; the unknown-country branch is pinned by ' +
      'TenantModuleBehaviourTests.Saudization_UnknownCountry_IsLockedClosed',
    ).toBe('SA');

    // The tenant-facing switch refuses outright rather than storing a value it would then ignore.
    const attempt = await request.put('/api/tenant-modules/qiwa_integration', {
      headers: auth, data: { enabled: false },
    });
    expect(attempt.status()).toBe(409);
    const refusal = await attempt.json();
    expect(refusal.code).toBe('module_not_disableable');
    expect(refusal.lockClass).toBe('Statutory');

    // And the runtime consequence: the gated route stays reachable.
    const qiwa = await request.get('/api/qiwa/connection', { headers: auth });
    expect(qiwa.status(), 'a locked module\'s routes are never gated off').toBe(200);
  });

  test('IntelliFlow employee cannot access QIWA configuration endpoint', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    const resp = await request.put('/api/qiwa/connection', {
      headers: { Authorization: `Bearer ${token}` },
      data: { establishmentId: '7000123456', establishmentName: 'X', unifiedOrganisationNumber: '1', environment: 'sandbox' },
    });
    // Employee lacks qiwa.configure permission.
    expect(resp.status()).toBe(403);
  });

  test('GOSI readiness endpoint returns 200 with disclaimer', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/gosi-readiness', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();
    expect(body).toHaveProperty('disclaimer');
    expect(body.disclaimer).toContain('illustrative');
    expect(body).toHaveProperty('employees');
  });

  test('QIWA readiness summary surfaces blocked employees with missing fields', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/qiwa/readiness-summary', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();
    expect(body).toHaveProperty('totalEmployees');
    expect(body).toHaveProperty('blockedFromSync');
    expect(body).toHaveProperty('blockedEmployees');
    expect(Array.isArray(body.blockedEmployees)).toBe(true);
  });

  test('WPS pre-export validation on a non-existent run returns 404', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.post('/api/payroll/runs/00000000-0000-0000-0000-000000000000/wps-validation', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(404);
  });
});

// ── Nitaqat / Saudization ────────────────────────────────────────────────────
// Shape-and-authorization only. The demo tenant may or may not have an MHRSD
// economic activity configured, and BOTH outcomes are correct product behaviour:
// a standing, or a named refusal. What must never happen is a 200 carrying
// neither, or a band with no numbers behind it.
test.describe('Saudi compliance — Nitaqat', () => {
  test('platform admin cannot reach tenant Nitaqat standing', async ({ request }) => {
    const token = await apiPlatformLogin(request);
    const resp = await request.get('/api/saudi-compliance/nitaqat', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect([401, 403]).toContain(resp.status());
  });

  test('Nitaqat standing returns either a band with its working, or a named refusal', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/nitaqat', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();
    expect(body).toHaveProperty('ok');

    if (body.ok) {
      const s = body.standing;
      expect(s).toBeTruthy();
      // A band is never shown without the numbers that produced it.
      for (const k of [
        'saudiWeighted', 'totalWeighted', 'achievedPercent', 'band',
        'currentBandFloorPercent', 'breakdown', 'scenario',
        'allInputsVerified', 'unverifiedInputs',
      ]) {
        expect(s).toHaveProperty(k);
      }
      expect(['Red', 'LowGreen', 'MediumGreen', 'HighGreen', 'Platinum']).toContain(s.band);
      expect(Array.isArray(s.breakdown)).toBe(true);
    } else {
      // A refusal is a designed state: it must name a reason and give a remedy.
      expect(body.standing).toBeNull();
      expect(body.refusal).toBeTruthy();
      expect(body.refusal.reason).toMatch(/^nitaqat_/);
      expect(body.refusal.remedy.length).toBeGreaterThan(0);
    }
  });

  test('the report catalogue offers a Saudization report', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/reports/catalog', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const catalog = await resp.json();
    expect(catalog.some((r: { key: string }) => r.key === 'compliance.saudization')).toBe(true);
  });

  test('hire impact requires a nationality', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/nitaqat/hire-impact?count=1', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(400);
    expect((await resp.json()).error).toBe('nationality_required');
  });
});

test.describe('Saudi compliance — UI', () => {
  test('saudi-compliance page loads for IntelliFlow admin', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/saudi-compliance');
    await expect(page.getByRole('heading', { name: 'Saudi Compliance' })).toBeVisible({ timeout: 15_000 });
    // No crash: at least one of the section cards is present.
    await expect(page.getByText('QIWA', { exact: true }).first()).toBeVisible();
  });
});
