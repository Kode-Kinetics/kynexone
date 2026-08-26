import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  // The Wave 1 security gate has its OWN config (playwright.security.config.ts) with retries:0 and a
  // fail-never-skip setup. Left in scope here it would run nine extra logins on top of this suite's
  // own — tripping the API's 10-per-60s limiter — and would run the gate specs with retries:1, which
  // is precisely the retry-hides-a-flaky-authorization-bug behaviour that config exists to forbid.
  testIgnore: /security-gate\//,
  fullyParallel: false,
  retries: 1,
  workers: 1,
  reporter: 'list',
  use: {
    // FRONTEND_PORT=5173 per .env; override with PLAYWRIGHT_BASE_URL if needed.
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    // Authenticates once; every platform spec reuses the session. Prevents the suite from
    // tripping the API's platform-login rate limit (default 5/window) with a login per test.
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], storageState: 'e2e/.auth/platform.json' },
      dependencies: ['setup'],
      testIgnore: /auth\.setup\.ts/,
    },
  ],
});
