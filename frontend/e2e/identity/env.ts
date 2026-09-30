/**
 * THE E2E ENVIRONMENT CONTRACT — every variable the fixture world, the bootstrap, the preflight and
 * the suites read, in one place.
 *
 * ── Why this exists (register item F07) ───────────────────────────────────────────────────────
 * On 28 Sep the fixture bootstrap authenticated as `admin@platform.local` because PLATFORM_ADMIN_EMAIL
 * was not exported to the test process, and e2e/world.ts silently fell back to that default. The API
 * under test had been booted with a DIFFERENT platform owner, so the bootstrap got HTTP 401 and the
 * connected all-role run never executed. Nothing had compared the two sides before provisioning.
 *
 * The platform owner is the one identity that crosses a process boundary: the API creates it at boot
 * (PlatformOwnerBootstrap) from PLATFORM_ADMIN_EMAIL / PLATFORM_ADMIN_PASSWORD, and the tests then have
 * to present the same pair. So the test side reads the SAME variable names the API reads, and it has
 * NO default: an unset variable is a loud preflight failure, never a guess. Every other identity is
 * created by the bootstrap from e2e/world.ts, so it is consistent with the specs by construction.
 *
 * This module is pure (it reads an env object, it does not throw at import), so the preflight's
 * decision logic can be unit-tested without a stack.
 */

export type Env = Record<string, string | undefined>;

/** The variables, by role. Names are part of the contract: CI and docs refer to them. */
export const E2E_ENV = {
  /** Same names the API's PlatformOwnerBootstrap reads. Required. No default. */
  platformEmail: 'PLATFORM_ADMIN_EMAIL',
  platformPassword: 'PLATFORM_ADMIN_PASSWORD',
  /** The frontend the browser drives. `E2E_BASE_URL` is the accepted alias. */
  baseUrl: 'PLAYWRIGHT_BASE_URL',
  baseUrlAlias: 'E2E_BASE_URL',
  /** The API, addressed directly (not through the frontend proxy). */
  apiBaseUrl: 'E2E_API_BASE_URL',
  /** The commit both the API and the frontend must have been built from. Required in CI. */
  expectedCommit: 'E2E_EXPECTED_COMMIT',
  /** The database NAME the API must be connected to. Optional locally, set in CI. */
  expectedDatabase: 'E2E_EXPECTED_DATABASE',
  /** Extra database hosts accepted as disposable (comma-separated). Loopback/single-label always are. */
  databaseHostAllowlist: 'E2E_DATABASE_HOST_ALLOWLIST',
  /** Extra app hosts accepted as disposable (comma-separated). Shared with disposable-host.guard.ts. */
  hostAllowlist: 'E2E_DESTRUCTIVE_HOST_ALLOWLIST',
  /** Local-only escape hatch when the stack cannot report its build commit. Refused in CI. */
  allowUnverifiedBuild: 'E2E_ALLOW_UNVERIFIED_BUILD',
  /** Login pacing under the API's login limiter. */
  loginPacingMs: 'E2E_LOGIN_PACING_MS',
} as const;

/**
 * Per-tenant fixture passwords. Generated per run in CI and REQUIRED there (e2e/world.ts refuses the
 * local defaults when CI=true). Locally they default, because the bootstrap creates the accounts with
 * the same values the specs then use.
 */
export const FIXTURE_PASSWORD_VARS = [
  'E2E_INTELLIFLOW_PASSWORD',
  'E2E_RASALMANAR_PASSWORD',
  'E2E_GROUP_PASSWORD',
  'E2E_EVOSTEL_PASSWORD',
] as const;

/**
 * Variables that used to name a SECOND identity for the same lane. Each one let a lane authenticate as
 * someone other than the persona e2e/world.ts declares, which is how two lanes ended up logging in as
 * different operators. They are retired; setting one is a preflight failure so a stale shell cannot
 * quietly point a lane at another tenant.
 */
export const RETIRED_ENV: Record<string, string> = {
  E2E_DEFAULT_TENANT_SLUG: 'the pilot lane now uses the IntelliFlow tenant declared in e2e/world.ts',
  E2E_DEFAULT_ADMIN_EMAIL: 'the pilot lane now uses INTELLIFLOW_ADMIN from e2e/world.ts',
  E2E_DEFAULT_ADMIN_PASSWORD: 'the pilot lane now uses E2E_INTELLIFLOW_PASSWORD via e2e/world.ts',
  E2E_MIN_EMPLOYEES: 'the floor is now the tenant\'s minActiveEmployees in e2e/world.ts',
};

export const DEFAULT_BASE_URL = 'http://localhost:5173';
export const DEFAULT_API_BASE_URL = 'http://localhost:5117';

const trimmed = (value: string | undefined): string | undefined => {
  const t = value?.trim();
  return t ? t : undefined;
};

export interface E2ETarget {
  /** The frontend origin, no trailing slash. */
  baseUrl: string;
  /** The API origin, no trailing slash. */
  apiBaseUrl: string;
}

/** Where the suites point. One resolution, used by every config, helper and the preflight. */
export function resolveTarget(env: Env = process.env): E2ETarget {
  const baseUrl = trimmed(env[E2E_ENV.baseUrl]) ?? trimmed(env[E2E_ENV.baseUrlAlias]) ?? DEFAULT_BASE_URL;
  const apiBaseUrl = trimmed(env[E2E_ENV.apiBaseUrl]) ?? DEFAULT_API_BASE_URL;
  return { baseUrl: baseUrl.replace(/\/+$/, ''), apiBaseUrl: apiBaseUrl.replace(/\/+$/, '') };
}

export interface PlatformIdentity {
  email: string;
  password: string;
}

/**
 * The platform owner as the environment states it — empty strings when unset. Deliberately not
 * defaulted: see the header. Callers that need it to authenticate go through the preflight, which
 * turns an empty value into a loud, specific failure.
 */
export function platformIdentityFromEnv(env: Env = process.env): PlatformIdentity {
  return {
    email: trimmed(env[E2E_ENV.platformEmail]) ?? '',
    password: env[E2E_ENV.platformPassword] ?? '',
  };
}

export const csv = (value: string | undefined): string[] =>
  (value ?? '').split(',').map((entry) => entry.trim().toLowerCase()).filter(Boolean);

export const isCi = (env: Env = process.env): boolean => !!trimmed(env.CI) && env.CI !== 'false';
