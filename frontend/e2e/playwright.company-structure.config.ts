import { defineConfig, devices } from '@playwright/test';

// Setup → Company structure (Companies, Branches, Locations, Departments). Every API request is intercepted;
// this suite never touches a real tenant. Start a dev server separately (e.g. `npx next dev -p 5198`).
export default defineConfig({
  testDir: '.',
  testMatch: /company-structure-.*\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-company-structure-results',
  use: {
    baseURL: process.env.COMPANY_STRUCTURE_BASE_URL || 'http://127.0.0.1:5198',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1280, height: 900 },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 900 } } }],
});
