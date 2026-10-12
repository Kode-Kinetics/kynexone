import { defineConfig, devices } from '@playwright/test';

// Succession screens with every API request intercepted by succession.spec.ts (no backend, no real tenant).
// Start a dev server separately (e.g. `npx next dev -p 5317`).
export default defineConfig({
  testDir: '.',
  testMatch: /succession\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-succession-results',
  use: {
    baseURL: process.env.SUCCESSION_BASE_URL || 'http://127.0.0.1:5317',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
