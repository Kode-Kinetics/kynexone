import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: /employee-workspace\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-employee-workspace-results',
  use: {
    baseURL: process.env.EMPLOYEE_WORKSPACE_BASE_URL || 'http://127.0.0.1:5197',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile-reduced-motion', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
    { name: 'reflow-320', use: { ...devices['Desktop Chrome'], viewport: { width: 320, height: 640 }, deviceScaleFactor: 2 } },
  ],
});
