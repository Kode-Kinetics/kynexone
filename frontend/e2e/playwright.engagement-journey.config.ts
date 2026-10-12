import { defineConfig } from '@playwright/test';

// ENG real-backend journey: headed Chrome against YOUR OWN disposable stack (never a deployed one).
//   Postgres :55512, API :5212 (Database__RunMigrationsOnStartup=true, PLATFORM_ADMIN_* set), `next dev -p 5312`.
//   PLATFORM_ADMIN_EMAIL=… PLATFORM_ADMIN_PASSWORD=… npx playwright test -c e2e/playwright.engagement-journey.config.ts
// Screenshots land in docs/acceptance/capability-closure/evidence/eng/.
export default defineConfig({
  testDir: '.',
  testMatch: /engagement-journey\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 600_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-engagement-journey-results',
  use: {
    baseURL: process.env.ENGAGEMENT_BASE_URL || 'http://127.0.0.1:5312',
    channel: 'chrome',
    headless: process.env.ENGAGEMENT_HEADLESS === '1',
    actionTimeout: 20_000,
    navigationTimeout: 90_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
});
