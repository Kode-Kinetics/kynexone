import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import { randomUUID } from 'node:crypto';
import { BASE_URL, ROLES, roleByKey, storageStatePath, tokenPath, type RoleFixture } from './roles';
import { stampActor } from '../identity/actor';
import { expectedPermissions, loadRoleCatalog } from '../identity/role-catalog';
import { evaluatePolicy } from '../identity/role-policy';
import { navigationItems } from '../../src/routes/navigation';

/**
 * THE FULL ROLE MATRIX — one real signed-in account for every tenant system role AuthSeeder installs.
 *
 * Nothing here is a hand-written copy of the matrix. The expected permissions are GENERATED from
 * backend-dotnet/…/AuthSeeder.cs (e2e/identity/role-catalog.ts); the separation-of-duties rules are
 * the few policy statements in e2e/identity/role-policy.ts; the API probes and the navigation check
 * are derived from each persona's own effective permissions and from src/routes/navigation.ts. So when
 * the catalog changes, this suite follows it — and when the RUNNING API disagrees with the checked-out
 * catalog (a stale build, a stale database), it fails.
 */

const catalog = loadRoleCatalog();
const tenantRoles = ROLES.filter((r): r is RoleFixture & { role: string; tenantSlug: string } =>
  r.role !== null && r.tenantSlug !== null);

function tokenFor(key: string): string {
  const p = tokenPath(key);
  if (!fs.existsSync(p)) throw new Error(`No stored token for '${key}'. The security-setup project must run first.`);
  return JSON.parse(fs.readFileSync(p, 'utf8')).token as string;
}

async function apiAs(key: string): Promise<APIRequestContext> {
  const role = roleByKey(key);
  stampActor({ email: role.email, tenantSlug: role.tenantSlug, via: `security-gate role '${key}'` });
  return pwRequest.newContext({
    baseURL: BASE_URL,
    timeout: 30_000,
    extraHTTPHeaders: { Authorization: `Bearer ${tokenFor(key)}` },
  });
}

async function effective(api: APIRequestContext, key: string): Promise<{ roles: string[]; permissions: string[] }> {
  const resp = await api.get('/api/auth/me');
  expect(resp.status(), `${key}: GET /api/auth/me`).toBe(200);
  const me = await resp.json();
  return { roles: [...(me.roles ?? [])].sort(), permissions: [...new Set<string>(me.permissions ?? [])].sort() };
}

test('the running API serves exactly the role catalog AuthSeeder.cs defines', async () => {
  const api = await apiAs('tenant-owner');
  try {
    const perms = await api.get('/api/access/permissions');
    expect(perms.status()).toBe(200);
    const liveKeys = ((await perms.json()) as Array<{ key: string }>).map((p) => p.key).sort();
    expect(liveKeys, 'the live permission catalog differs from AuthSeeder.EnsurePermissions').toEqual(catalog.permissions);

    const roles = await api.get('/api/access/roles');
    expect(roles.status()).toBe(200);
    const live = (await roles.json()) as Array<{ name: string; isSystem: boolean; permissions: string[] }>;
    for (const role of catalog.roles) {
      const row = live.find((r) => r.name === role.name);
      expect(row, `system role '${role.name}' is not installed in the group tenant`).toBeTruthy();
      expect([...new Set(row!.permissions)].sort(), `live '${role.name}' permissions`).toEqual(role.permissions);
    }
  } finally {
    await api.dispose();
  }
});

for (const fixture of tenantRoles) {
  const expected = expectedPermissions(catalog, [fixture.role]);
  const has = (key: string) => expected.includes(key);

  test.describe(`${fixture.label} [${fixture.role}]`, () => {
    test('signs in as exactly its AuthSeeder role and permissions', async () => {
      const api = await apiAs(fixture.key);
      try {
        const me = await effective(api, fixture.key);
        expect(me.roles, `${fixture.key} must hold exactly role ${fixture.role}`).toEqual([fixture.role]);
        const missing = expected.filter((p) => !me.permissions.includes(p));
        const extra = me.permissions.filter((p) => !expected.includes(p));
        expect({ missing, extra }, `${fixture.key} vs the ${fixture.role} role in AuthSeeder.cs`).toEqual({ missing: [], extra: [] });

        // Separation of duties, on what this signed-in account can ACTUALLY do.
        expect(evaluatePolicy(new Map([[fixture.role, new Set(me.permissions)]]), catalog.permissions)).toEqual([]);
      } finally {
        await api.dispose();
      }
    });

    test('API authority follows its permissions', async () => {
      const api = await apiAs(fixture.key);
      try {
        const probe = async (what: string, permission: string, call: () => Promise<{ status(): number }>, allowed: number[]) => {
          const status = (await call()).status();
          if (has(permission)) {
            expect(status, `${fixture.key} holds ${permission}: ${what} must pass authorization`).not.toBe(403);
            if (allowed.length) expect(allowed, `${fixture.key} holds ${permission}: ${what}`).toContain(status);
          } else {
            expect(status, `${fixture.key} lacks ${permission}: ${what} must be refused at the permission gate`).toBe(403);
          }
        };

        await probe('GET /api/employees', 'employees.read',
          () => api.get('/api/employees?page=1&pageSize=1'), [200]);
        await probe('GET /api/payroll/runs', 'payroll.read',
          () => api.get('/api/payroll/runs?page=1&pageSize=1'), [200]);
        await probe('GET /api/access/roles', 'security.manage',
          () => api.get('/api/access/roles'), [200]);
        // Mutations are probed on ids that cannot exist, so an authorized caller reaches the lookup
        // (404/400) and nothing is ever changed; an unauthorized caller must be stopped before it.
        await probe('POST /api/payroll/runs/{random}/approve', 'payroll.approve',
          () => api.post(`/api/payroll/runs/${randomUUID()}/approve`, {
            data: { comments: 'full-role-matrix authorization probe', expectedExcludedCount: 0 },
          }), []);
        await probe('PATCH /api/employees/{absent}/status', 'employees.write',
          () => api.patch('/api/employees/2147483647/status', {
            data: { status: 'Active', effectiveDate: '2026-09-25', reason: 'full-role-matrix authorization probe' },
          }), []);

        // No tenant role reaches platform administration, whatever it holds.
        const platform = await api.get('/api/platform/tenants');
        expect([401, 403], `${fixture.key} reached /api/platform/tenants`).toContain(platform.status());
      } finally {
        await api.dispose();
      }
    });

    // The browser half. Only the direction that matters for security is asserted: the menu never
    // offers a screen the persona holds NONE of the required permissions for. (Whether it shows every
    // screen it could also depends on feature flags and the module catalog, which is not RBAC.)
    if (has('dashboard.read')) {
      test('navigation never offers a screen it lacks every permission for', async ({ browser }) => {
        stampActor({ email: fixture.email, tenantSlug: fixture.tenantSlug, via: `security-gate role '${fixture.key}' (browser)` });
        const ctx = await browser.newContext({ baseURL: BASE_URL, storageState: storageStatePath(fixture.key) });
        try {
          const page = await ctx.newPage();
          await page.goto('/dashboard');
          const nav = page.locator('nav[aria-label="Primary navigation"]');
          // Positive control: an authenticated shell with a Dashboard entry. Without it every negative
          // assertion below would pass on a logged-out or error page.
          await expect(nav.getByRole('button', { name: 'Dashboard', exact: true }), `${fixture.key}: signed-in shell`)
            .toBeVisible({ timeout: 30_000 });
          for (const item of navigationItems) {
            const required = item.requiredPermissions ?? [];
            if (!required.length || required.some(has)) continue;
            await expect(
              nav.getByRole('button', { name: item.label, exact: true }),
              `${fixture.key} holds none of [${required.join(', ')}] yet the menu offers '${item.label}'`,
            ).toHaveCount(0);
          }
        } finally {
          await ctx.close();
        }
      });
    }
  });
}
