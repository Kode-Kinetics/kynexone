import { defineConfig } from '@playwright/test';

// SUC acceptance journey against a REAL disposable stack (see succession-journey.spec.ts). Headed Google Chrome, one
// worker, no retries: each step writes evidence into docs/acceptance/capability-closure/evidence/suc/.
export default defineConfig({
  testDir: '.',
  testMatch: /succession-journey\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 300_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-suc-journey-results',
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5317',
    channel: 'chrome',
    headless: false,
    actionTimeout: 20_000,
    navigationTimeout: 60_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});
