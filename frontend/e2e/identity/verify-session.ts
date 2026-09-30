import { personaForLogin, tenantBySlug } from '../world';
import { expectedPermissions, loadRoleCatalog } from './role-catalog';
import { checkPersonaSession } from '../preflight/rules';

/**
 * Does a login response's `user` describe the persona e2e/world.ts declares for this address?
 * Returns the failed checks (empty when it does): exact role, exactly the AuthSeeder.cs permissions
 * for that role, the declared company scope and employee link.
 *
 * Every lane's setup project calls this on the logins it performs anyway, so each session a lane is
 * about to reuse is proven to be the declared persona at the moment it is minted — at no extra request.
 * Personas a lane fixture creates per run (not the bootstrap) are not in a declared tenant and are
 * skipped by callers.
 */
export function sessionMismatches(email: string, tenantSlug: string, user: any): string[] {
  const persona = personaForLogin(email, tenantSlug);
  if (!persona || persona.role === null || persona.tenantSlug === null) {
    return [`${email} @ ${tenantSlug} is not a persona in e2e/world.ts`];
  }
  const findings = checkPersonaSession({
    key: persona.key,
    email: persona.email,
    tenantSlug: persona.tenantSlug,
    role: persona.role,
    scope: persona.scope === 'companies' ? 'companies' : 'group',
    companyCodes: persona.companyCodes,
    employeeLinked: persona.employeeLinked,
    tenantCompanyCodes: tenantBySlug(persona.tenantSlug).companies.map((c) => c.code),
    expectedPermissions: expectedPermissions(loadRoleCatalog(), [persona.role]),
  }, {
    status: 200,
    tenantSlug: user?.tenantSlug,
    roles: user?.roles,
    permissions: user?.permissions,
    companyCodes: (user?.companies ?? []).map((c: any) => String(c.code)),
    isGroupScope: user?.isGroupScope,
    employeeId: user?.employeeId ?? null,
  });
  return findings.filter((f) => !f.ok).map((f) => `${f.check}: ${f.detail}`);
}
