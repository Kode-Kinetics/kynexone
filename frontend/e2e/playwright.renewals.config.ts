import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

/**
 * Contract renewals (Release A R4) against mocked API routes: the radar and the chain drawer in English and Arabic.
 * No backend, no database — a `next dev` server and page.route fixtures, like the loans lane.
 *
 *   npx playwright test -c e2e/playwright.renewals.config.ts
 */
const port = Number(process.env.RENEWALS_FIXTURE_PORT ?? 5186);
export default defineConfig({
  testDir: '.', testMatch: /renewals-radar\.spec\.ts/, workers: 1, retries: 0,
  reporter: 'list', timeout: 120_000,
  outputDir: '/tmp/kynex-renewals-playwright-results',
  use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: { command: `npx next dev -p ${port}`, url: `http://localhost:${port}/contract-renewals`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 240_000 },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
  ],
});
