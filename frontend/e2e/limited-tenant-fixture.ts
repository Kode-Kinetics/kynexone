import { APIRequestContext, expect } from '@playwright/test';
import {
  EVOSTEL_ADMIN,
  EVOSTEL_SLUG,
} from './helpers';

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
  const headers = platformHeaders(token);
  const tenant = await findActiveFixture(request, headers);
  if (!tenant) return;

  const deleted = await request.delete(`/api/platform/tenants/${tenant.id}?confirm=DELETE`, { headers });
  expect(deleted.ok(), await deleted.text()).toBe(true);

  const purged = await request.delete(`/api/platform/tenants/${tenant.id}/purge?confirm=PURGE`, { headers });
  expect(purged.ok(), await purged.text()).toBe(true);
}

export async function provisionLimitedTenantFixture(request: APIRequestContext, token: string): Promise<void> {
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
