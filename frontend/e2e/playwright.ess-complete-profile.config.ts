import { defineConfig, devices } from '@playwright/test';

// Mocked API only (see ess/essMocks.ts). Start the frontend separately: `next dev -p <free port>`, then
// ESS_COMPLETE_BASE_URL=http://127.0.0.1:<port> npx playwright test -c e2e/playwright.ess-complete-profile.config.ts
export default defineConfig({
  testDir: '.',
  testMatch: /ess-complete-profile(\.rtl|\.a11y)?\.spec\.ts|employee-onboarding-completion\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-ess-complete-profile-results',
  use: { baseURL: process.env.ESS_COMPLETE_BASE_URL || 'http://127.0.0.1:5185', actionTimeout: 15_000, trace: 'retain-on-failure' },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
