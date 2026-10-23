import { defineConfig, devices } from '@playwright/test';

// Setup progress and "Find a setting": every API call is intercepted by the spec. Start the frontend separately
// (SETUP_EXPERIENCE_BASE_URL); this suite never changes a real organization.
export default defineConfig({
  testDir: '.', testMatch: /setup-progress-search\.spec\.ts/, workers: 1, retries: 0, timeout: 60_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-setup-progress-search-results',
  use: { baseURL: process.env.SETUP_EXPERIENCE_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    // CI's Browser Pilot runs these specs at Desktop Chrome's default size.
    { name: 'pilot', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 720 } } },
    { name: 'tablet', use: { ...devices['Desktop Chrome'], viewport: { width: 1024, height: 768 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
