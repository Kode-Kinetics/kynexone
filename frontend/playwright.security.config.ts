import { defineConfig, devices } from '@playwright/test';

/**
 * WAVE 1 B3 — the Chrome role and isolation security gate.
 *
 * Deliberately a SEPARATE config from `playwright.config.ts`, for one reason: this one is intended to
 * become a required PR check, and a required check must be able to fail. The existing suite is
 * "CI-safe by design" — it `test.skip()`s when the stack is unreachable, so a completely dead backend
 * produces a green run. That is acceptable for an advisory suite and unacceptable for a gate.
 *
 * Requires a REAL stack: Postgres + backend (enterprise-group seed) + a PRODUCTION frontend build.
 * See docs/CHROME_SECURITY_GATE.md.
 */
export default defineConfig({
  testDir: './e2e/security-gate',

  // No retries. A security boundary that only holds on the second attempt is a flaky boundary, and
  // retrying it hides exactly the intermittent authorization bug this suite exists to catch.
  retries: 0,
  fullyParallel: false,
  workers: 1,

  // `forbidOnly` stops a stray `test.only` from silently shrinking the gate to one test in CI.
  forbidOnly: !!process.env.CI,

  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],
  timeout: 90_000,
  expect: { timeout: 15_000 },

  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: process.env.CI ? 'retain-on-failure' : 'off',
    actionTimeout: 20_000,
  },

  projects: [
    // Authenticates all nine roles ONCE, paced under the API's 10-per-60s login limiter, and writes a
    // storage state + bearer token per role. Everything downstream reuses those sessions, so the
    // limiter is respected rather than raised.
    { name: 'security-setup', testMatch: /security-gate\/auth\.setup\.ts/ },
    {
      name: 'security-gate',
      use: { ...devices['Desktop Chrome'] },
      dependencies: ['security-setup'],
      testIgnore: /auth\.setup\.ts/,
    },
  ],
});
