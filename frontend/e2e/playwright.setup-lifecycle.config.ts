import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: '.', testMatch: /setup-lifecycle\.spec\.ts/, workers: 1, retries: 0, timeout: 90_000,
  reporter: 'list', outputDir: '/tmp/kynex-setup-lifecycle-results',
  use: { baseURL: process.env.SETUP_EXPERIENCE_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }, { name: 'pilot', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 720 } } }, { name: 'mobile', use: { ...devices['Pixel 7'] } }],
});
