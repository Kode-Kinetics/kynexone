import { defineConfig, devices } from '@playwright/test';

// The create-company wizard against its in-memory /api/company-setup contract (every API request is
// intercepted by company-setup-wizard.spec.ts). Start a dev server first: `npx next dev -p 5174`.
// The Browser Pilot lane also collects this spec in its `chromium` project.
export default defineConfig({
  testDir: '.',
  testMatch: /company-setup-wizard\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 120_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-company-wizard-results',
  use: {
    baseURL: process.env.COMPANY_WIZARD_BASE_URL || 'http://127.0.0.1:5174',
    actionTimeout: 15_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 }, contextOptions: { reducedMotion: 'reduce' } } },
  ],
});
