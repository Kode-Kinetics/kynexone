import { defineConfig, devices } from '@playwright/test';

// Merit planning (/compensation) with every API request intercepted by compensation.spec.ts (synthetic data only).
// Start a dev server separately (e.g. `npx next dev -p 5313`); this suite never touches a real tenant.
export default defineConfig({
  testDir: '.',
  testMatch: /compensation\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 120_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-compensation-results',
  use: {
    baseURL: process.env.COMPENSATION_BASE_URL || 'http://127.0.0.1:5313',
    actionTimeout: 15_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
