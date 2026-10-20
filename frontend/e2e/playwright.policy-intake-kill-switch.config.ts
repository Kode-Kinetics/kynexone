import { defineConfig, devices } from '@playwright/test';

// Mocked browser contracts for the policy_intake kill switch; every API request is intercepted by the spec.
export default defineConfig({
  testDir: '.', testMatch: /policy-intake-kill-switch\.spec\.ts/, workers: 1, retries: 0, timeout: 60_000,
  reporter: 'list', outputDir: '/tmp/kynex-policy-intake-kill-switch-results',
  use: { baseURL: process.env.SETUP_EXPERIENCE_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 10_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'] } },
  ],
});
