import { defineConfig } from '@playwright/test';

// CMP real-backend journey on a DISPOSABLE stack: Postgres :55513, API :5213 (migrated), `next dev -p 5313`.
// Headed Google Chrome. Project "seed" provisions the synthetic world through the API (bootstrap/compensation-seed.ts);
// "journey" drives the merit cycle end to end through the UI (compensation-journey.spec.ts).
//   E2E_API_BASE_URL=http://127.0.0.1:5213 COMPENSATION_BASE_URL=http://127.0.0.1:5313 \
//   PLATFORM_ADMIN_EMAIL=… PLATFORM_ADMIN_PASSWORD=… CMP_EVIDENCE_DIR=… \
//   npx playwright test -c e2e/playwright.compensation-real.config.ts
export default defineConfig({
  testDir: '.',
  workers: 1,
  retries: 0,
  timeout: 600_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-compensation-real-results',
  use: {
    baseURL: process.env.COMPENSATION_BASE_URL || 'http://127.0.0.1:5313',
    actionTimeout: 20_000,
    navigationTimeout: 90_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'seed', testMatch: /bootstrap\/compensation-seed\.ts/ },
    {
      name: 'journey', testMatch: /compensation-journey\.spec\.ts/, dependencies: ['seed'],
      use: { channel: 'chrome', headless: false, viewport: { width: 1440, height: 1000 } },
    },
  ],
});
