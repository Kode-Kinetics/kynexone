import { defineConfig, devices } from '@playwright/test';

// Per-screen contracts (Screen Standard 8g): every screen, in English and Arabic, at 1366x768, 1920x1080 and 320x640, is
// held to the same contract (purpose line, one primary action in its zone, step strip on a multi-step flow, reflow). Every
// API call is intercepted by the specs, so they never read or change a real organization. Start the frontend separately
// (SCREEN_CONTRACT_BASE_URL). Assertions a screen does not meet yet are test.fixme with the failing assertion in the title;
// a Wave 1 screen turns them on by flipping its `ready` flag in e2e/screen-contract/screens.ts.
export default defineConfig({
  testDir: './screen-contract', testMatch: /\.spec\.ts$/, workers: 2, retries: 0, timeout: 90_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-screen-contract-results',
  use: { baseURL: process.env.SCREEN_CONTRACT_BASE_URL || 'http://127.0.0.1:5197', actionTimeout: 15_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'contract', use: { ...devices['Desktop Chrome'] } },
  ],
});
