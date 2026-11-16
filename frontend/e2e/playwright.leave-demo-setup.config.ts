import { defineConfig, devices } from '@playwright/test';

// Leave balances opened from the leave policies, the apply form's supporting document, and the leave-type Arabic suggestion,
// against a mocked API (e2e/leave-open-year, leave-apply-attachment and leave-type-arabic specs): a dev server only (LEAVE_SETUP_BASE_URL, default :5178).
export default defineConfig({
  testDir: '.', testMatch: /leave-(open-year|apply-attachment|type-arabic)\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 180_000,
  outputDir: '/tmp/kynex-leave-demo-setup-results',
  use: { baseURL: process.env.LEAVE_SETUP_BASE_URL || 'http://127.0.0.1:5178', trace: 'retain-on-failure', actionTimeout: 15_000 },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
});
