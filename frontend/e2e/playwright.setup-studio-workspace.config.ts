import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.', testMatch: /setup-studio-workspace\.spec\.ts/, workers: 1, retries: 0, timeout: 60_000,
  reporter: 'list', outputDir: '/tmp/kynex-setup-studio-workspace-results',
  use: { baseURL: process.env.SETUP_EXPERIENCE_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'] } },
  ],
});
