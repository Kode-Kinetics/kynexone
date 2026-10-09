import { defineConfig, devices } from '@playwright/test';

// Rendered fixtures cover default benefit preview and the authority-gated exception editor.
// Start this branch's frontend separately; every API request stays within the synthetic session.
export default defineConfig({
  testDir: '.',
  testMatch: /(?:employee-create-wizard|benefits-grade-defaults)\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-benefit-defaults-results',
  use: { baseURL: process.env.BENEFIT_DEFAULTS_BASE_URL || 'http://127.0.0.1:5293', actionTimeout: 10_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'phone', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
