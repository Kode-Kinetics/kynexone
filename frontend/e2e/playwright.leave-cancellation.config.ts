import { defineConfig, devices } from '@playwright/test';

// Leave cancellations and the approvals queue against a mocked API (e2e/leave-cancellation-approvals.spec.ts): a dev server only.
export default defineConfig({
  testDir: '.', testMatch: /leave-cancellation-approvals\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 180_000,
  outputDir: '/tmp/kynex-leave-cancellation-results',
  use: { baseURL: process.env.LEAVE_BASE_URL || 'http://127.0.0.1:5313', trace: 'retain-on-failure', actionTimeout: 15_000 },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
});
