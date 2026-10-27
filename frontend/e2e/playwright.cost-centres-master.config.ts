import { defineConfig, devices } from '@playwright/test';

// Cost accounting v1 (WP-F phase 2): the cost-centre master (Setup → Cost centres) and the department import's
// links-to-clear gate. Every API request is intercepted; this suite never touches a real tenant.
// Start a dev server separately (e.g. `npx next dev -p 5197`).
export default defineConfig({
  testDir: '.',
  testMatch: /cost-centres-master\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-cost-centres-master-results',
  use: {
    baseURL: process.env.COST_CENTRES_MASTER_BASE_URL || 'http://127.0.0.1:5197',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1280, height: 900 },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 900 } } },
    // CI's Browser Pilot runs `--project=chromium` = Desktop Chrome at its own 1280x720: the shortest viewport this screen is judged at.
    { name: 'ci-chromium', use: { ...devices['Desktop Chrome'] } },
  ],
});
