import { defineConfig, devices } from '@playwright/test';

// Setup as the one home for configuration: every API call is intercepted by the specs (e2e/setup-consolidation/), so
// they never read or change a real organization. Start the frontend separately (SETUP_EXPERIENCE_BASE_URL).
export default defineConfig({
  testDir: './setup-consolidation', testMatch: /\.spec\.ts$/, workers: 2, retries: 0, timeout: 60_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-setup-consolidation-results',
  use: { baseURL: process.env.SETUP_EXPERIENCE_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
  ],
});
