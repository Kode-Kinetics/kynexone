import { defineConfig, devices } from '@playwright/test';

// Focused UI coverage with every API request intercepted by setup-experience.spec.ts.
// Start the preview separately; this suite never changes a real organization.
export default defineConfig({
  testDir: '.',
  testMatch: /setup-experience\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-setup-experience-results',
  use: {
    baseURL: process.env.SETUP_EXPERIENCE_BASE_URL || 'http://127.0.0.1:5185',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 }, contextOptions: { reducedMotion: 'reduce' } } },
  ],
});
