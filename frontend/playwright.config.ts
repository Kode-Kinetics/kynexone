import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
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
      testIgnore: [/auth\.setup\.ts/, /fixture\.teardown\.ts/, /pilot-critical\.spec\.ts/],
    },
    {
      name: 'cleanup',
      testMatch: /fixture\.teardown\.ts/,
      use: { ...devices['Desktop Chrome'], storageState: 'e2e/.auth/platform.json' },
    },
  ],
});
