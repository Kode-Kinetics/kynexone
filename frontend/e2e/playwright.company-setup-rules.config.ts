import { defineConfig, devices } from '@playwright/test';

// Step 5 parts of the company setup wizard against intercepted APIs (company-setup-rules-parts.spec.ts).
// Start a dev server first: `npx next dev -H 127.0.0.1 -p 5176` (never :5173 — a prebuilt image).
export default defineConfig({
  testDir: '.',
  testMatch: /company-setup-rules-parts\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 120_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-company-rules-results',
  use: {
    baseURL: process.env.COMPANY_WIZARD_BASE_URL || 'http://127.0.0.1:5176',
    actionTimeout: 15_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: '1440', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: '1280', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } },
    { name: '375', use: { ...devices['iPhone 13'], defaultBrowserType: 'chromium', viewport: { width: 375, height: 812 }, contextOptions: { reducedMotion: 'reduce' } } },
  ],
});
