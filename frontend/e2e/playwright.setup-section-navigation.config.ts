import { defineConfig, devices } from '@playwright/test';

// Setup & Administration section navigation: every API call is intercepted by the spec. Start the frontend
// separately (SETUP_EXPERIENCE_BASE_URL) — this suite never changes a real organization.
export default defineConfig({
  testDir: '.', testMatch: /setup-section-navigation\.spec\.ts/, workers: 1, retries: 0, timeout: 60_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-setup-section-navigation-results',
  use: { baseURL: process.env.SETUP_EXPERIENCE_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'laptop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } },
    { name: 'tablet', use: { ...devices['Desktop Chrome'], viewport: { width: 1024, height: 768 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
