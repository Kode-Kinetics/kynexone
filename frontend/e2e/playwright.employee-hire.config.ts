import { defineConfig, devices } from '@playwright/test';

// Mocked API only (see hire/hireMocks.ts). Start the frontend separately: `next dev -p 5174` (NOT the stale :5173 image).
// CI does not use this file: the main `chromium` project collects employee-hire-wizard.spec.ts (npx playwright test --project=chromium).
export default defineConfig({
  testDir: '.',
  testMatch: /employee-hire-wizard\.spec\.ts|employee-hire\.a11y\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-employee-hire-results',
  use: { baseURL: process.env.EMPLOYEE_HIRE_BASE_URL || 'http://127.0.0.1:5174', actionTimeout: 10_000, trace: 'retain-on-failure' },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
