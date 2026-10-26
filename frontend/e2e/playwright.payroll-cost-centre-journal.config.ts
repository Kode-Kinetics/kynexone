import { defineConfig, devices } from '@playwright/test';

// Payroll → GL Journal (the cost-accounting Lock preview) and Payroll → Reports (journal by cost centre), cost-accounting v1,
// with every API request intercepted by payroll-cost-centre-journal.spec.ts. Start a dev server separately
// (e.g. `npx next dev -p 5198`); this suite never touches a real tenant.
export default defineConfig({
  testDir: '.',
  testMatch: /payroll-cost-centre-journal\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-payroll-cost-centre-journal-results',
  use: {
    baseURL: process.env.PAYROLL_CC_BASE_URL || 'http://127.0.0.1:5198',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1280, height: 900 },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 900 } } },
  ],
});
