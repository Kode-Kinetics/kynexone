import { defineConfig, devices } from '@playwright/test';

// Setup → Payroll accounting, cost-accounting v1 screens (account rules, mappings, readiness), with every API
// request intercepted by gl-cost-centres.spec.ts. Start a dev server separately (e.g. `npx next dev -p 5197`);
// this suite never touches a real tenant.
export default defineConfig({
  testDir: '.',
  testMatch: /gl-cost-centres\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-gl-cost-centres-results',
  use: {
    baseURL: process.env.GL_COST_CENTRES_BASE_URL || 'http://127.0.0.1:5197',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    // CI's Browser Pilot runs specs at Desktop Chrome's default size; the tables must hold there too.
    { name: 'laptop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 720 } } },
  ],
});
