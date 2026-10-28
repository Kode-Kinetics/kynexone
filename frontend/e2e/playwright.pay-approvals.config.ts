import { defineConfig, devices } from '@playwright/test';

// The pay approvals queue and the scoped approval view: every API call is intercepted by the spec. Start the frontend
// separately (PAY_APPROVALS_BASE_URL); this suite never changes a real organization.
export default defineConfig({
  testDir: '.', testMatch: /pay-approvals\.spec\.ts/, workers: 1, retries: 0, timeout: 60_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-pay-approvals-results',
  use: { baseURL: process.env.PAY_APPROVALS_BASE_URL || 'http://127.0.0.1:5198', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'pilot', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 720 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], viewport: { width: 360, height: 800 } } },
  ],
});
