import { test as setup } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { TENANT_STATE, tenantSessionKey } from './helpers';
import {
  ALMARAI_SLUG, companyEmail, GROUP_PASSWORD, groupEmail,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_EMP2, INTELLIFLOW_FINANCE, INTELLIFLOW_HR_MGR,
  INTELLIFLOW_SLUG,
  RASALMANAR_ADMIN, RASALMANAR_SLUG, TATA_SLUG,
} from './world';
import { sessionMismatches } from './identity/verify-session';

/**
 * Authenticate every persona the browser lane reuses ONCE, and persist the sessions.
 *
 * Specs used to log in per test, and the API rate-limits login, so most of those logins were
 * correctly rejected and the tests failed in the login helper — not on the routes they were meant to
 * check. The rate limiter is right; re-authenticating per test is what was wrong. Logging in once and
 * reusing the sessions keeps the suite honest (it still exercises a real login) while staying inside
 * the production rate limit, which is what makes it viable as a CI gate.
 *
 * Logins are paced under the API's 10-per-60s window, exactly as e2e/security-gate/auth.setup.ts
 * does. Without pacing the eleventh login gets a 429, this setup project throws, and because it is a
 * dependency of the `chromium` project the ENTIRE browser lane produces no result. The limiter is
 * respected, not raised: raising RateLimit:LoginPermitLimit to make the suite pass would weaken a
 * production brute-force control for the convenience of the tests.
 */
const LOGIN_PACING_MS = Number(process.env.E2E_LOGIN_PACING_MS ?? 7_000);

setup('authenticate every persona once', async ({ request }) => {
  // 12 personas × 7s pacing ≈ 84s.
  setup.setTimeout(240_000);

  // Every address and password below comes from e2e/world.ts — the same declaration
  // e2e/bootstrap/provision.ts provisions from. They were literals here, so a persona could be
  // spelled one way in this file and created another way by whatever seeded the database, and the
  // only symptom was a login failure in a setup project that fails the entire browser lane.
  const group = (role: string, slug = ALMARAI_SLUG) =>
    ({ email: groupEmail(role, slug), password: GROUP_PASSWORD, slug });
  const personas = [
    { ...INTELLIFLOW_ADMIN, slug: INTELLIFLOW_SLUG },
    { ...INTELLIFLOW_EMP1, slug: INTELLIFLOW_SLUG },
    // A SECOND employee. Employee-self-service specs mutate their own week/enrolment, so two of them
    // running against one persona interfere: the first submits the week and the second then finds no
    // Submit control. Two people is what the product would have.
    { ...INTELLIFLOW_EMP2, slug: INTELLIFLOW_SLUG },
    // Payroll maker/checker personas. The two-step approval in payroll-run-to-wps.spec.ts needs
    // THREE distinct users (the processor cannot approve, and the maker cannot finalise), so the
    // sessions are minted once here rather than three logins per test run.
    { ...INTELLIFLOW_HR_MGR, slug: INTELLIFLOW_SLUG },
    { ...INTELLIFLOW_FINANCE, slug: INTELLIFLOW_SLUG },
    { ...RASALMANAR_ADMIN, slug: RASALMANAR_SLUG },
    group('owner'),
    { email: companyEmail('admin', 'ALM-DAIRY-KSA'), password: GROUP_PASSWORD, slug: ALMARAI_SLUG },
    group('scoped.admin'),
    group('auditor'),
    group('compliance'),
    group('compliance', TATA_SLUG),
  ];
  const sessions: Record<string, { accessToken: string; refreshToken: string }> = {};
  for (const [index, persona] of personas.entries()) {
    if (index > 0) await new Promise((r) => setTimeout(r, LOGIN_PACING_MS));
    // One organisation per installation: the login carries no workspace.
    const response = await request.post('/api/auth/login', {
      data: { email: persona.email, password: persona.password },
    });
    if (!response.ok())
      throw new Error(
        `Persona setup login failed for ${persona.email} (${persona.slug}): ${response.status()}` +
        (response.status() === 429
          ? ' — rate limited. Raise E2E_LOGIN_PACING_MS; do NOT raise the API\'s login limit.'
          : ''),
      );
    const body = await response.json() as { accessToken?: string; token?: string; refreshToken?: string; user?: unknown };
    const accessToken = body.accessToken ?? body.token;
    if (!accessToken || !body.refreshToken)
      throw new Error(`Persona setup login returned incomplete tokens for ${persona.email} (${persona.slug}).`);
    // Every session this lane will reuse is the persona e2e/world.ts declares — role, exact AuthSeeder
    // permissions, scope, employee link — proven now, from the response already in hand (F07).
    const mismatches = sessionMismatches(persona.email, persona.slug, body.user);
    if (mismatches.length)
      throw new Error(`Persona ${persona.email} (${persona.slug}) signed in, but not as declared:\n  ${mismatches.join('\n  ')}`);
    sessions[tenantSessionKey(persona.email, persona.slug)] = {
      accessToken,
      refreshToken: body.refreshToken,
    };
  }
  await writeFile(TENANT_STATE, JSON.stringify(sessions), { mode: 0o600 });
});
