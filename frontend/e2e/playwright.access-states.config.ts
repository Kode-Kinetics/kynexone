import { defineConfig, devices } from '@playwright/test';

// Employee active / Login enabled / Password configured on the profile, the activation blockers, and the creator notice.
// Every API call is intercepted by the spec. Start `next dev -p 5174` first (never the :5173 Docker image: it is stale).
export default defineConfig({
  testDir: '.', testMatch: /access-states\.spec\.ts/, workers: 1, retries: 0, timeout: 90_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-access-states-results',
  use: { baseURL: process.env.ACCESS_STATES_BASE_URL || 'http://127.0.0.1:5174', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], viewport: { width: 360, height: 800 } } },
  ],
});
