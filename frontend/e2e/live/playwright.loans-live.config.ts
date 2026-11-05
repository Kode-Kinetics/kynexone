import { defineConfig, devices } from '@playwright/test';
import { resolveTarget } from '../identity/env';

// Live loan lane: runs against an externally started disposable stack AFTER the preflight and bootstrap configs have
// provisioned and verified the fixture world (same order as CI's Browser Pilot job). No webServer, no mocks.
//   npx playwright test -c e2e/preflight/playwright.preflight.config.ts
//   npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts
//   npx playwright test -c e2e/live/playwright.loans-live.config.ts
export default defineConfig({
  testDir: '.', testMatch: /\.live\.spec\.ts$/, workers: 1, retries: 0, reporter: 'list',
  outputDir: '/tmp/kynex-loan-live-results',
  use: { baseURL: resolveTarget().baseUrl, trace: 'retain-on-failure', actionTimeout: 15_000, ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
});
