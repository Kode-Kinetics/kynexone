import { test as setup } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import {
  platformLogin, PLATFORM_STATE, TENANT_STATE, tenantSessionKey,
  INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1,
  RASALMANAR_SLUG, RASALMANAR_ADMIN, EVOSTEL_SLUG, EVOSTEL_ADMIN,
} from './helpers';
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
setup('authenticate platform admin and provision isolated limited tenant', async ({ page, request }) => {
  setup.setTimeout(120_000);
  await platformLogin(page);
  await page.context().storageState({ path: PLATFORM_STATE });
  const token = await page.evaluate(() => localStorage.getItem('platform_access_token'));
  if (!token) throw new Error('Platform login completed without persisting a platform access token.');
  await provisionLimitedTenantFixture(request, token);

  const groupPassword = process.env.E2E_GROUP_PASSWORD ?? 'GroupDemo123!x';
  const personas = [
    { ...INTELLIFLOW_ADMIN, slug: INTELLIFLOW_SLUG },
    { ...INTELLIFLOW_EMP1, slug: INTELLIFLOW_SLUG },
    { ...RASALMANAR_ADMIN, slug: RASALMANAR_SLUG },
    { ...EVOSTEL_ADMIN, slug: EVOSTEL_SLUG },
    { email: 'owner@almarai-test.local', password: groupPassword, slug: 'almarai-test' },
    { email: 'admin@alm-dairy-ksa.almarai-test.local', password: groupPassword, slug: 'almarai-test' },
    { email: 'scoped.admin@almarai-test.local', password: groupPassword, slug: 'almarai-test' },
    { email: 'auditor@almarai-test.local', password: groupPassword, slug: 'almarai-test' },
    { email: 'compliance@almarai-test.local', password: groupPassword, slug: 'almarai-test' },
    { email: 'compliance@tata-test.local', password: groupPassword, slug: 'tata-test' },
  ];
  const sessions: Record<string, { accessToken: string; refreshToken: string }> = {};
  for (const persona of personas) {
    const response = await request.post('/api/auth/login', {
      data: { email: persona.email, password: persona.password, tenantSlug: persona.slug },
    });
    if (!response.ok())
      throw new Error(`Persona setup login failed for ${persona.email}/${persona.slug}: ${response.status()}`);
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
