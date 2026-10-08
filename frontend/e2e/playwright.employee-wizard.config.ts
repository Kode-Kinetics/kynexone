import { defineConfig, devices } from '@playwright/test';

// This suite intercepts every API request and never creates a real employee.
// Start the frontend separately so the suite can also run against an existing preview.
export default defineConfig({
  testDir: '.',
  testMatch: /employee-create-wizard\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-employee-wizard-results',
  use: {
    baseURL: process.env.EMPLOYEE_WIZARD_BASE_URL || 'http://127.0.0.1:5293',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile-reduced-motion', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 }, contextOptions: { reducedMotion: 'reduce' } } },
  ],
});
