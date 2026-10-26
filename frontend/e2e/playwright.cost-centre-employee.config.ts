import { defineConfig, devices } from '@playwright/test';

// Cost accounting v1 (WP-F phase 2): the employee's cost-centre field, its override dialogs, and every employee
// form that writes a cost centre (AM-30). Every API request is intercepted; this suite never touches a real tenant.
// Start a dev server separately (e.g. `npx next dev -p 5197`).
export default defineConfig({
  testDir: '.',
  testMatch: /cost-centre-employee\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-cost-centre-employee-results',
  use: {
    baseURL: process.env.COST_CENTRE_EMPLOYEE_BASE_URL || 'http://127.0.0.1:5197',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1280, height: 900 },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 900 } } }],
});
