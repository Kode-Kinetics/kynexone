import { defineConfig, devices } from '@playwright/test';

// Learning screens with every API request intercepted by learning.spec.ts (no backend, no real tenant).
// Start a dev server separately (e.g. `npx next dev -p 5311`).
export default defineConfig({
  testDir: '.',
  testMatch: /learning\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-learning-results',
  use: {
    baseURL: process.env.LEARNING_BASE_URL || 'http://127.0.0.1:5311',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
