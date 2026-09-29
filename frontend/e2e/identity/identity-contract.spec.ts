import { test, expect } from '@playwright/test';
import { FIXTURE_PASSWORD_VARS } from './env';
import { expectedPermissions, loadRoleCatalog, parseAuthSeeder, roleDefinition } from './role-catalog';
import { catalogRoleMap, evaluatePolicy, ROLE_POLICY, unknownPolicyKeys, unknownPolicyRoles } from './role-policy';

/**
 * Browserless proof that the identity contract and the role matrix are generated from — and agree
 * with — the backend's own AuthSeeder.cs. Fails on the PR, without a stack, when:
 *   • AuthSeeder changes shape so the matrix can no longer be generated;
 *   • a role bundle names a permission the catalog does not define (Ps() drops it silently at runtime);
 *   • a seeding change breaks a separation-of-duties rule;
 *   • a system role gains no persona in the security gate, or a persona names a role nobody installs.
 */

// world.ts refuses to fall back to committed passwords under CI=true. This file never talks to a stack,
// so it supplies throwaway values before loading the registry; they authenticate nothing anywhere.
// `require`, not `import()`: Playwright transpiles this tree to CommonJS, where a dynamic import of a
// TypeScript module is not transformed. Loading lazily is what lets the placeholders land first.
async function loadRegistry() {
  for (const name of FIXTURE_PASSWORD_VARS) process.env[name] ??= 'browserless-placeholder';
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const world = require('../world') as typeof import('../world');
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const roles = require('../security-gate/roles') as typeof import('../security-gate/roles');
  return { world, roles };
}

test.describe('role catalog is generated from AuthSeeder.cs', () => {
  const catalog = loadRoleCatalog();

  test('parses every role and the permission catalog', () => {
    expect(catalog.permissions.length).toBeGreaterThan(50);
    // Sanity of the parse, not a copy of the catalog: 15 system roles today, each at its own level.
    expect(catalog.roles.length).toBeGreaterThanOrEqual(15);
    const levels = catalog.roles.map((r) => r.authorityLevel);
    expect(new Set(levels).size).toBe(levels.length);
    for (const role of catalog.roles) expect(role.permissions.length, role.name).toBeGreaterThan(0);
    expect(roleDefinition(catalog, 'Admin').permissions).toEqual(catalog.permissions);
    expect(roleDefinition(catalog, 'Kiosk Operator').permissions).toEqual(['attendance.kiosk']);
    expect(roleDefinition(catalog, 'Employee').permissions).toEqual(['dashboard.read', 'ess.read', 'ess.write', 'profile.read']);
  });

  test('evaluates the predicate bundles (HR Director, HR Manager) rather than skipping them', () => {
    const hrDirector = roleDefinition(catalog, 'HR Director').permissions;
    expect(hrDirector).toContain('employees.write');
    expect(hrDirector).toContain('recruitment.approve');
    expect(hrDirector).toContain('payroll.read');
    expect(hrDirector).not.toContain('payroll.write');
    const hrManager = roleDefinition(catalog, 'HR Manager').permissions;
    expect(hrManager).toEqual(expect.arrayContaining(['employees.write', 'payroll.approve', 'loans.write']));
    expect(hrManager).not.toContain('payroll.lock');
  });

  test('no role bundle names a permission the catalog does not define', () => {
    const offenders = catalog.roles.filter((r) => r.unknownKeys.length).map((r) => `${r.name}: ${r.unknownKeys.join(', ')}`);
    expect(offenders, 'Ps() silently drops unknown keys, so these grants never happen').toEqual([]);
  });

  test('refuses a bundle shape it does not understand instead of guessing', () => {
    const source = `
      private async Task<List<Permission>> EnsurePermissions(CancellationToken ct)
      { var definitions = new (string Key, string Module, string Description)[] { ("a.read", "A", "x"), ("a.write", "A", "y") }; }
      public async Task<Role> EnsureTenantRolesAsync(Guid tenantId, CancellationToken ct = default)
      { await EnsureRole(tenantId, "Odd", "d", permissions.Where(x => x.Module == "A").ToList(), 1, true, ct); }`;
    expect(() => parseAuthSeeder(source)).toThrow(/Role 'Odd': unrecognised predicate fragment/);
    const ok = source.replace('x.Module == "A"', 'x.Key.StartsWith("a.") || x.Key is "a.read" or "b.read"');
    const [role] = parseAuthSeeder(ok).roles;
    expect(role.permissions).toEqual(['a.read', 'a.write']);
    expect(role.unknownKeys).toEqual(['b.read']);
  });
});

test.describe('separation-of-duties policy holds for the AuthSeeder catalog', () => {
  const catalog = loadRoleCatalog();

  test('every key and role the policy names exists (no vacuous "must lack")', () => {
    expect(unknownPolicyKeys(catalog)).toEqual([]);
    expect(unknownPolicyRoles(catalog)).toEqual([]);
  });

  for (const rule of ROLE_POLICY) {
    test(`policy: ${rule.id}`, () => {
      expect(evaluatePolicy(catalogRoleMap(catalog), catalog.permissions, [rule])).toEqual([]);
    });
  }

  test('the policy can fail: a Payroll Officer granted payroll.approve is caught', () => {
    const roles = catalogRoleMap(catalog);
    roles.get('Payroll Officer')!.add('payroll.approve');
    expect(evaluatePolicy(roles, catalog.permissions).join('\n')).toContain('payroll-officer-is-a-maker: Payroll Officer holds payroll.approve');
  });
});

test.describe('one identity registry', () => {
  test('every AuthSeeder role has a persona in the security gate, and every gate role is installed', async () => {
    const { roles } = await loadRegistry();
    const catalog = loadRoleCatalog();
    const covered = new Set(roles.ROLES.filter((r) => r.role).map((r) => r.role!));
    expect(catalog.roles.map((r) => r.name).filter((name) => !covered.has(name)), 'system roles with no gate persona').toEqual([]);
    for (const r of roles.ROLES.filter((x) => x.role)) expect(() => roleDefinition(catalog, r.role!)).not.toThrow();
  });

  test('every gate role IS a registered persona (no re-spelled addresses)', async () => {
    const { world, roles } = await loadRegistry();
    for (const role of roles.ROLES) {
      const persona = world.personaForLogin(role.email, role.tenantSlug);
      expect(persona, `${role.key} <${role.email}> is not in e2e/world.ts PERSONAS`).toBeTruthy();
      expect(persona!.role).toBe(role.role);
    }
  });

  test('persona keys are unique and every tenant persona names an installed role', async () => {
    const { world } = await loadRegistry();
    const catalog = loadRoleCatalog();
    const keys = world.PERSONAS.map((p) => p.key);
    expect(keys.filter((k, i) => keys.indexOf(k) !== i)).toEqual([]);
    for (const p of world.PERSONAS.filter((x) => x.role)) {
      expect(expectedPermissions(catalog, [p.role!]).length, `${p.key}`).toBeGreaterThan(0);
    }
  });

  test('the platform owner has no default: it is exactly what the environment says', async () => {
    const { world } = await loadRegistry();
    expect(world.PLATFORM_EMAIL).toBe((process.env.PLATFORM_ADMIN_EMAIL ?? '').trim());
    expect(world.PLATFORM_PASSWORD).toBe(process.env.PLATFORM_ADMIN_PASSWORD ?? '');
  });
});
