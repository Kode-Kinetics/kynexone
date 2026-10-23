import { defineConfig, devices } from '@playwright/test';

// App chrome (sidebar, bottom nav, Kody launcher, ⌘K palette) in English and Arabic. Every API call is intercepted
// by the spec; start the frontend separately (APP_CHROME_BASE_URL). This suite never changes a real organization.
export default defineConfig({
  testDir: '.', testMatch: /app-chrome\.spec\.ts/, workers: 1, retries: 0, timeout: 90_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-app-chrome-results',
  use: { baseURL: process.env.APP_CHROME_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    // Browser Pilot's size.
    { name: 'pilot', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 720 } } },
    { name: 'tablet', use: { ...devices['Desktop Chrome'], viewport: { width: 1024, height: 768 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
