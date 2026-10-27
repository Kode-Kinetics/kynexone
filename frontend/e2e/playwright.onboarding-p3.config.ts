import { defineConfig, devices } from '@playwright/test';

// WP-P3 Browser Pilot: mocked API only. Start the frontend separately with `next dev -p <port>` (NOT the stale :5173 image) and
// set EMPLOYEE_HIRE_BASE_URL=http://127.0.0.1:<port>. The existing hire wizard specs run with e2e/playwright.employee-hire.config.ts.
export default defineConfig({
  testDir: '.',
  testMatch: /hire-wizard-template\.spec\.ts|setup-onboarding-templates\.spec\.ts|onboarding-worklist\.spec\.ts|onboarding-worklist-skip\.spec\.ts|onboarding-templates\.a11y\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-onboarding-p3-results',
  use: { baseURL: process.env.EMPLOYEE_HIRE_BASE_URL || 'http://127.0.0.1:5174', actionTimeout: 10_000, trace: 'retain-on-failure' },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
