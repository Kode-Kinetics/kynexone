import { defineConfig, devices } from '@playwright/test';

// Statutory minimums as acknowledgeable warnings on the leave policy form and the "Rules below the legal minimum" list,
// against a mocked API (e2e/leave-floor-warnings.spec.ts): a dev server only.
export default defineConfig({
  testDir: '.', testMatch: /leave-floor-warnings\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 180_000,
  outputDir: '/tmp/kynex-leave-floor-warnings-results',
  use: { baseURL: process.env.FLOOR_BASE_URL || 'http://127.0.0.1:5187', trace: 'retain-on-failure', actionTimeout: 15_000 },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
});
