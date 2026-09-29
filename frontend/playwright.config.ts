import { defineConfig, devices } from '@playwright/test';
import { resolveTarget } from './e2e/identity/env';

export default defineConfig({
  testDir: './e2e',
  // Hard pre-flight instead of `webServer`. See e2e/global-setup.ts for the full reasoning: this
  // stack is four docker-compose services plus seeded data, and `webServer` proves only that a port
  // is open — which is green for every blank-module bug this repo has actually shipped.
  globalSetup: './e2e/global-setup.ts',
  // The Wave 1 security gate has its OWN config (playwright.security.config.ts) with retries:0 and a
  // fail-never-skip setup. Left in scope here it would run nine extra logins on top of this suite's
  // own — tripping the API's 10-per-60s limiter — and would run the gate specs with retries:1, which
  // is precisely the retry-hides-a-flaky-authorization-bug behaviour that config exists to forbid.
  testIgnore: /security-gate\//,
  fullyParallel: false,
  // A stray `test.only` would otherwise shrink this 130-test lane to ONE test in CI, silently.
  // playwright.security.config.ts has had this; this config did not — the same "a guard exists in
  // one config and not its sibling" class as the testIgnore bug.
  forbidOnly: !!process.env.CI,
  // retries:1 is deliberate here (slow first paint on some pages) but it IS a trade-off: a retry
  // can hide an intermittent authorization bug. playwright.security.config.ts sets retries:0 for
  // exactly that reason. Do not raise this.
  retries: 1,
  workers: 1,
  // The actor reporter prints who each test acted as (e2e/identity/actor.ts), so a green line in the
  // CI log also says under whose session it went green.
  reporter: [['list'], ['./e2e/identity/actor-reporter.ts']],
  use: {
    // One resolution for every config, helper and the preflight (e2e/identity/env.ts): PLAYWRIGHT_BASE_URL,
    // then the E2E_BASE_URL alias, then :5173 — which the preflight then has to prove is the right stack.
    baseURL: resolveTarget().baseUrl,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    // Independent of the `setup` project, so a failing platform-UI login (the setup project drives the
    // platform login FORM) cannot suppress the tenant browser proof. It is NOT independent of the
    // platform owner itself any more: the world it logs in to only exists because the bootstrap created
    // it as that owner, and the global preflight (F07) refuses to run any lane unless the owner this
    // process presents authenticates against this API and the world is the verified one.
    {
      name: 'tenant-pilot',
      testMatch: /pilot-critical\.spec\.ts/,
      use: { ...devices['Desktop Chrome'] },
    },
    // Authenticates once; every platform spec reuses the session. Prevents the suite from
    // tripping the API's platform-login rate limit (default 5/window) with a login per test.
    { name: 'setup', testMatch: /auth\.setup\.ts/, teardown: 'cleanup' },
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], storageState: 'e2e/.auth/platform.json' },
      dependencies: ['setup'],
      // A project-level testIgnore REPLACES the top-level one rather than merging with it, so the
      // root `testIgnore: /security-gate\//` above stops applying the moment this array exists.
      // Without repeating it here the browser-pilot lane collects the 14 security-gate specs,
      // which depend on fixtures only the dedicated chrome-security-gate job provisions, and they
      // fail in milliseconds. Keep these two lists in sync.
      testIgnore: [/auth\.setup\.ts/, /fixture\.teardown\.ts/, /pilot-critical\.spec\.ts/, /security-gate\//],
    },
    // ── Mobile web ────────────────────────────────────────────────────────────────────────────
    // COST/VALUE (assessed 2026-09-17): all three projects were Desktop Chrome, so the responsive
    // web app had ZERO viewport coverage. The real Expo/React Native app lives in a separate repo
    // and is not what this covers — this is the responsive web app at phone width, where the demo
    // risk is a collapsed sidebar hiding navigation or a data table overflowing off-screen.
    //
    // Scoped to `pilot-critical.spec.ts` only, deliberately. Running all ~130 tests at a second
    // viewport would roughly double a lane that already takes minutes, for mostly duplicate
    // API-level assertions — and the freeze is Tuesday. One viewport × the critical-route walk
    // catches the layout regressions that matter at ~90s of extra runtime.
    //
    // Opt out with `--project=...` selection; it is NOT in the default critical path for CI gating
    // until it has a clean baseline.
    {
      name: 'mobile-pilot',
      testMatch: /pilot-critical\.spec\.ts/,
      use: { ...devices['Pixel 7'] },
    },
    {
      name: 'cleanup',
      testMatch: /fixture\.teardown\.ts/,
      use: { ...devices['Desktop Chrome'], storageState: 'e2e/.auth/platform.json' },
    },
  ],
});
