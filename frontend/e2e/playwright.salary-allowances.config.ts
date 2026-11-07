import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// Monthly cash allowances on a salary (assign, edit, hire-adjacent profile, Finance approval) against route mocks. Starts `next dev` from
// this checkout, so it exercises the code under review rather than a prebuilt image.
const port = Number(process.env.SALARY_ALLOWANCES_PORT ?? 5198);
export default defineConfig({
  testDir: '.', testMatch: /salary-cash-allowances\.spec\.ts/, workers: 1, retries: 0,
  reporter: 'list', timeout: 120_000,
  outputDir: '/tmp/kynex-salary-allowances-results',
  use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: { command: `npx next dev -p ${port}`, url: `http://localhost:${port}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 240_000 },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
