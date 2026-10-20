import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.', testMatch: /(?:employee-create-wizard|employee-access-hr|employee-edit-wizard|setup-experience)\.spec\.ts/,
  grep: new RegExp(process.env.EMAIL_TEST_PATTERN || 'email regression'), workers: 1, retries: 0, timeout: 45_000,
  reporter: 'list', outputDir: '/tmp/kynex-email-manual-results',
  use: { baseURL: process.env.SETUP_EXPERIENCE_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 10_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'] } },
  ],
});
