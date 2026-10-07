import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

const port = Number(process.env.LOAN_FIXTURE_PORT ?? 5183);
export default defineConfig({
  testDir: '.', testMatch: /loans-(?:separate-payments|governance)\.spec\.ts/, workers: 1, retries: 0,
  reporter: 'list', timeout: 90_000,
  outputDir: '/tmp/kynex-loan-playwright-results',
  use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: { command: `npx next dev -p ${port}`, url: `http://localhost:${port}/loans`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000 },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'phone', use: { ...devices['Pixel 7'] } },
  ],
});
