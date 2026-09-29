import { defineConfig, devices } from '@playwright/test';

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
  reporter: 'list',
  use: {
    // FRONTEND_PORT=5173 per .env. Accept the established E2E_BASE_URL alias so
    // isolated audit stacks never fall back to an unrelated service on :5173.
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL ?? 'http://localhost:5173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    // A tenant-critical smoke lane must remain runnable even when the platform-admin
    // bootstrap account is intentionally absent or broken. Keeping it independent of
    // `setup` prevents one platform login from suppressing every tenant browser proof.
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
