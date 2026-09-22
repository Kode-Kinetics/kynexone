import { APIRequestContext, expect } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { EVOSTEL_ADMIN, EVOSTEL_SLUG } from './world';
import { assertDisposableHost } from './disposable-host.guard';

/**
 * Records the tenant id this run created, so teardown purges THAT tenant and not merely
 * "whatever currently occupies the evostel slug". Previously setup discarded the id and teardown
 * re-found it by slug, so the two could not distinguish "the fixture I made" from "a pre-existing
 * tenant that happens to be called evostel" — and it hard-erased either.
 */
// __dirname, not import.meta.url: Playwright transpiles these specs to CommonJS, and
// import.meta forces ESM semantics, which breaks the whole setup project at load time.
const OWNERSHIP_FILE = join(__dirname, '.auth', 'fixture-tenant.json');

/**
 * The URL the suite is ACTUALLY pointed at — resolved exactly as playwright.config.ts resolves
 * `use.baseURL`, including the localhost default.
 *
 * Both call sites below previously read `PLAYWRIGHT_BASE_URL ?? E2E_BASE_URL` with NO default. Run
 * the documented way (`npx playwright test`, no env exported) that is `undefined`, so
 * `assertDisposableHost` fail-closed on the very first setup step and the ENTIRE chromium lane —
 * every browser test in the suite — never ran. A guard that blocks the run it is meant to protect
 * is not protection; and "0 tests ran" is one CI config away from being read as "nothing broke".
 *
 * The safety property is unchanged: the resolved host is still handed to assertDisposableHost,
 * which still refuses anything that is not loopback or explicitly allowlisted.
 */
const RESOLVED_BASE_URL =
  process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL ?? 'http://localhost:5173';

function recordOwnership(tenantId: string, baseUrl: string): void {
  mkdirSync(dirname(OWNERSHIP_FILE), { recursive: true });
  writeFileSync(OWNERSHIP_FILE, JSON.stringify({ tenantId, baseUrl, createdAt: new Date().toISOString() }));
}

function readOwnership(): { tenantId: string; baseUrl: string } | null {
  if (!existsSync(OWNERSHIP_FILE)) return null;
  try { return JSON.parse(readFileSync(OWNERSHIP_FILE, 'utf8')); } catch { return null; }
}

function clearOwnership(): void {
  rmSync(OWNERSHIP_FILE, { force: true });
}

const LIMITED_FEATURES = [
  'ai_assistant',
  'recruitment',
  'performance',
  'shifts',
  'overtime',
  'qiwa_integration',
];

type TenantSummary = { id: string; slug: string };

function platformHeaders(token: string) {
  return { Authorization: `Bearer ${token}` };
}

async function findActiveFixture(request: APIRequestContext, headers: Record<string, string>) {
  const response = await request.get('/api/platform/tenants', { headers });
  expect(response.ok(), await response.text()).toBe(true);
  const tenants = await response.json() as TenantSummary[];
  return tenants.find((tenant) => tenant.slug === EVOSTEL_SLUG);
}

/**
 * `DELETE /api/platform/tenants/{id}` is currently a deliberate stub: PlatformController.DeleteTenant
 * answers 409 `tenant_delete_disabled` — "temporarily disabled pending an atomic
 * credential-revocation and recovery workflow" — and the purge endpoint is reached through it.
 *
 * That is a product decision, and the fixture must not pretend it is a test failure. But it must not
 * hide it either: the fixture tenant then SURVIVES the run, which is only acceptable because this
 * helper refuses to run anywhere but a disposable stack. So the one documented refusal is reported
 * and tolerated; anything else still fails.
 */
async function deleteAndPurge(
  request: APIRequestContext, headers: Record<string, string>, tenantId: string,
): Promise<'purged' | 'delete_disabled'> {
  const deleted = await request.delete(`/api/platform/tenants/${tenantId}?confirm=DELETE`, { headers });
  if (!deleted.ok()) {
    const body = await deleted.text();
    if (deleted.status() === 409 && body.includes('tenant_delete_disabled')) {
      console.log(
        `[fixture] Tenant deletion is disabled in this build, so fixture tenant ${tenantId} is left `
        + 'in place. It lives in a disposable database that dies with the run — this helper refuses '
        + 'to run against anything else. Re-enable PlatformController.DeleteTenant to restore cleanup.',
      );
      return 'delete_disabled';
    }
    expect(deleted.ok(), body).toBe(true);
  }
  const purged = await request.delete(`/api/platform/tenants/${tenantId}/purge?confirm=PURGE`, { headers });
  expect(purged.ok(), await purged.text()).toBe(true);
  return 'purged';
}

export async function purgeLimitedTenantFixture(request: APIRequestContext, token: string): Promise<void> {
  assertDisposableHost(RESOLVED_BASE_URL, 'purge the E2E tenant fixture');

  const headers = platformHeaders(token);
  const tenant = await findActiveFixture(request, headers);
  if (!tenant) return;

  // Only purge the tenant THIS run created. Without this, a pre-existing tenant that happens to
  // occupy the evostel slug is hard-erased by a suite that never created it.
  const owned = readOwnership();
  if (!owned || owned.tenantId !== tenant.id) {
    throw new Error(
      `[fixture] REFUSING to purge tenant ${tenant.id}: this run did not create it `
      + `(recorded owner: ${owned?.tenantId ?? 'none'}). Purge is an irreversible hard-erase. `
      + 'If this really is an orphan from a failed run, remove it deliberately by hand.',
    );
  }

  if (await deleteAndPurge(request, headers, tenant.id) === 'purged') clearOwnership();
}

export async function provisionLimitedTenantFixture(request: APIRequestContext, token: string): Promise<void> {
  // Provision deletes-and-purges any pre-existing evostel tenant before creating its own, so the
  // FIRST action of every browser run is a hard-erase. It needs the same guard as teardown.
  assertDisposableHost(RESOLVED_BASE_URL, 'provision the E2E tenant fixture');

  const headers = platformHeaders(token);
  const existing = await findActiveFixture(request, headers);
  if (existing) {
    if (await deleteAndPurge(request, headers, existing.id) === 'delete_disabled') {
      // The slug is occupied and cannot be freed. Re-apply the subscription and feature state this
      // fixture is FOR, so the lane runs against the shape it expects, and record that this run does
      // NOT own the tenant so teardown will not try to erase someone else's.
      recordOwnership(existing.id, RESOLVED_BASE_URL);
      await applyLimitedState(request, headers, existing.id);
      return;
    }
  }

  const created = await request.post('/api/platform/tenants', {
    headers,
    data: {
      name: 'Evostel E2E Fixture',
      slug: EVOSTEL_SLUG,
      adminEmail: EVOSTEL_ADMIN.email,
      adminFullName: 'E2E Tenant Administrator',
      adminPassword: EVOSTEL_ADMIN.password,
      plan: 'Starter',
      maxUsers: 10,
      maxEmployees: 50,
      billingEmail: 'billing@evostel.test',
      billingCycle: 'Monthly',
      monthlyAmount: 299,
      currencyCode: 'USD',
    },
  });
  expect(created.status(), await created.text()).toBe(201);
  const { tenantId } = await created.json() as { tenantId: string };
  recordOwnership(tenantId, RESOLVED_BASE_URL);
  await applyLimitedState(request, headers, tenantId);
}

/** The PastDue subscription and disabled features that make this a LIMITED tenant. */
async function applyLimitedState(
  request: APIRequestContext, headers: Record<string, string>, tenantId: string,
): Promise<void> {
  const subscription = await request.put(`/api/platform/tenants/${tenantId}/subscription`, {
    headers,
    data: {
      plan: 'Starter',
      status: 'PastDue',
      billingCycle: 'Monthly',
      monthlyAmount: 299,
      currencyCode: 'USD',
      maxEmployees: 50,
      maxUsers: 10,
      billingEmail: 'billing@evostel.test',
      expiresAtUtc: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString(),
    },
  });
  expect(subscription.ok(), await subscription.text()).toBe(true);

  for (const featureKey of LIMITED_FEATURES) {
    const feature = await request.put(`/api/platform/tenants/${tenantId}/features/${featureKey}`, {
      headers,
      data: { isEnabled: false },
    });
    expect(feature.ok(), `${featureKey}: ${await feature.text()}`).toBe(true);
  }
}
