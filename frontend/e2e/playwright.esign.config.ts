import { defineConfig, devices } from '@playwright/test';

// Mocked API only (see ess/esignMocks.ts). Start the frontend separately: `next dev -p <free port>`, then
// ESIGN_BASE_URL=http://127.0.0.1:<port> npx playwright test -c e2e/playwright.esign.config.ts
export default defineConfig({
  testDir: '.',
  testMatch: /(ess-sign|hr-sign-on-device)(\.rtl|\.a11y)?\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-esign-results',
  use: { baseURL: process.env.ESIGN_BASE_URL || 'http://127.0.0.1:5186', actionTimeout: 15_000, trace: 'retain-on-failure' },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
