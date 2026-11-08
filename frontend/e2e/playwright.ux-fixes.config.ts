import { defineConfig, devices } from '@playwright/test';

// The UX fixes of the Screen Standard programme: failures are said out loud (with a refusal, never an empty list),
// disabled controls say why, confirmations are real dialogs, Benefits has one home. Every API call is intercepted
// by the specs (e2e/ux-fixes/), so they never read or change a real organization. Start the frontend separately
// (UX_FIXES_BASE_URL).
export default defineConfig({
  testDir: './ux-fixes', testMatch: /\.spec\.ts$/, workers: 2, retries: 0, timeout: 90_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-ux-fixes-results',
  use: { baseURL: process.env.UX_FIXES_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 15_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
  ],
});
