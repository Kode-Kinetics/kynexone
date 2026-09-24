import { test as setup } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import {
  platformLogin, PLATFORM_STATE, TENANT_STATE, tenantSessionKey,
} from './helpers';
import {
  ALMARAI_SLUG, companyEmail, EVOSTEL_ADMIN, EVOSTEL_SLUG, GROUP_PASSWORD, groupEmail,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_EMP2, INTELLIFLOW_FINANCE, INTELLIFLOW_HR_MGR,
  INTELLIFLOW_SLUG,
  RASALMANAR_ADMIN, RASALMANAR_SLUG, TATA_SLUG,
} from './world';
import { provisionLimitedTenantFixture } from './limited-tenant-fixture';

/**
 * Authenticate ONCE and persist the session for every spec that needs platform admin.
 *
 * WHY THIS EXISTS. The suite previously called `platformLogin` in `beforeEach`, so a 17-route
 * sanity spec performed 17 logins in a few seconds. The API rate-limits platform login to
 * `RateLimiting:PlatformLoginPermitLimit` (default 5) per window, so most of those logins were
 * correctly rejected and the tests failed in the login helper — not on the routes they were
 * meant to check. The rate limiter is right; re-authenticating per test is what was wrong.
 *
 * Logging in once and reusing the storage state keeps the suite honest (it still exercises a
 * real login) while staying inside the production rate limit, which is what makes it viable
 * as a CI gate.
 */
/**
 * Pace persona logins under the API's 10-per-60s login window, exactly as
 * e2e/security-gate/auth.setup.ts does.
 *
 * This file authenticates TWELVE personas in a tight `for` loop with no pacing, so the eleventh
 * login onwards got a 429 and the setup project threw. The setup project is a `dependencies` of the
 * `chromium` project, so that single throw meant "8 did not run" — the ENTIRE browser lane, ~112
 * tests, produced no result at all. Its sibling config had the pacing; this one did not. That is
 * the same "a guard exists in one config and not its sibling" class that playwright.config.ts's own
 * comments call out for testIgnore and forbidOnly.
 *
 * The limiter is respected, not raised: raising RateLimit:LoginPermitLimit to make the suite pass
 * would weaken a production brute-force control for the convenience of the tests.
 */
const LOGIN_PACING_MS = Number(process.env.E2E_LOGIN_PACING_MS ?? 7_000);

setup('authenticate platform admin and provision isolated limited tenant', async ({ page, request }) => {
  // 12 personas × 7s pacing ≈ 84s, plus the platform login and fixture provisioning.
  setup.setTimeout(240_000);
  await platformLogin(page);
  await page.context().storageState({ path: PLATFORM_STATE });
  const token = await page.evaluate(() => localStorage.getItem('platform_access_token'));
  if (!token) throw new Error('Platform login completed without persisting a platform access token.');
  await provisionLimitedTenantFixture(request, token);

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
    { ...EVOSTEL_ADMIN, slug: EVOSTEL_SLUG },
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
    const response = await request.post('/api/auth/login', {
      data: { email: persona.email, password: persona.password, tenantSlug: persona.slug },
    });
    if (!response.ok())
      throw new Error(
        `Persona setup login failed for ${persona.email}/${persona.slug}: ${response.status()}` +
        (response.status() === 429
          ? ' — rate limited. Raise E2E_LOGIN_PACING_MS; do NOT raise the API\'s login limit.'
          : ''),
      );
    const body = await response.json() as { accessToken?: string; token?: string; refreshToken?: string };
    const accessToken = body.accessToken ?? body.token;
    if (!accessToken || !body.refreshToken)
      throw new Error(`Persona setup login returned incomplete tokens for ${persona.email}/${persona.slug}.`);
    sessions[tenantSessionKey(persona.email, persona.slug)] = {
      accessToken,
      refreshToken: body.refreshToken,
    };
  }
  await writeFile(TENANT_STATE, JSON.stringify(sessions), { mode: 0o600 });
});
