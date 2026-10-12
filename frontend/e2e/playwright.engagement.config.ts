import { defineConfig, devices } from '@playwright/test';

// Engagement surveys with every API request intercepted by engagement.spec.ts (synthetic data, no backend, no tenant).
// Start a dev server separately (e.g. `npx next dev -p 5312`), then:
//   ENGAGEMENT_BASE_URL=http://127.0.0.1:5312 npx playwright test -c e2e/playwright.engagement.config.ts
export default defineConfig({
  testDir: '.',
  testMatch: /(^|\/)engagement\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-engagement-results',
  use: {
    baseURL: process.env.ENGAGEMENT_BASE_URL || 'http://127.0.0.1:5312',
    actionTimeout: 15_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
