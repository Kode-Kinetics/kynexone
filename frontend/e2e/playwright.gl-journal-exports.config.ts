import { defineConfig, devices } from '@playwright/test';

// Focused UI coverage for the ERP journal hand-off; every API request is intercepted by
// gl-journal-exports.spec.ts. Start a dev server separately; this suite never touches a real tenant.
export default defineConfig({
  testDir: '.',
  testMatch: /gl-journal-exports\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 120_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-gl-journal-exports-results',
  use: {
    baseURL: process.env.GL_EXPORTS_BASE_URL || 'http://127.0.0.1:5191',
    actionTimeout: 15_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
  ],
});
