import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// Benefits by grade (Release A R1) against route mocks built from the backend's read models. Starts `next dev` from this
// checkout, so it exercises the code under review rather than a prebuilt image.
const port = Number(process.env.MATRIX_FIXTURE_PORT ?? 5197);
export default defineConfig({
  testDir: '.', testMatch: /benefits-by-grade-matrix\.spec\.ts/, workers: 1, retries: 0,
  reporter: 'list', timeout: 120_000,
  outputDir: '/tmp/kynex-matrix-playwright-results',
  use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: { command: `npx next dev -p ${port}`, url: `http://localhost:${port}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 240_000 },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
