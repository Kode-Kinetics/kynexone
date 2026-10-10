import { defineConfig, devices } from '@playwright/test';

// Setup → Statutory Rules with every API request intercepted by statutory-rules-approval.spec.ts.
// Start a dev server separately (e.g. `npx next dev -p 5197`); this suite never touches a real tenant.
export default defineConfig({
  testDir: '.',
  testMatch: /statutory-rules-approval\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-statutory-rules-results',
  use: {
    baseURL: process.env.STATUTORY_RULES_BASE_URL || process.env.GL_SETUP_BASE_URL || 'http://127.0.0.1:5197',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
