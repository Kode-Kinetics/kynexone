import { APIRequestContext, expect } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import {
  EVOSTEL_ADMIN,
  EVOSTEL_SLUG,
} from './helpers';
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

export async function purgeLimitedTenantFixture(request: APIRequestContext, token: string): Promise<void> {
  const baseUrl = process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL;
  assertDisposableHost(baseUrl, 'purge the E2E tenant fixture');

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

  const deleted = await request.delete(`/api/platform/tenants/${tenant.id}?confirm=DELETE`, { headers });
  expect(deleted.ok(), await deleted.text()).toBe(true);

  const purged = await request.delete(`/api/platform/tenants/${tenant.id}/purge?confirm=PURGE`, { headers });
  expect(purged.ok(), await purged.text()).toBe(true);
  clearOwnership();
}

export async function provisionLimitedTenantFixture(request: APIRequestContext, token: string): Promise<void> {
  const baseUrl = process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL;
  // Provision deletes-and-purges any pre-existing evostel tenant before creating its own, so the
  // FIRST action of every browser run is a hard-erase. It needs the same guard as teardown.
  assertDisposableHost(baseUrl, 'provision the E2E tenant fixture');

  const headers = platformHeaders(token);
  const existing = await findActiveFixture(request, headers);
  if (existing) {
    const deleted = await request.delete(`/api/platform/tenants/${existing.id}?confirm=DELETE`, { headers });
    expect(deleted.ok(), await deleted.text()).toBe(true);
    const purged = await request.delete(`/api/platform/tenants/${existing.id}/purge?confirm=PURGE`, { headers });
    expect(purged.ok(), await purged.text()).toBe(true);
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
  recordOwnership(tenantId, baseUrl!);

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
