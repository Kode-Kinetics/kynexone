import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

/**
 * Browser check of the employee import preview and its refusal report, on `next dev`, with the API
 * route-mocked from REAL responses (unit/fixtures/employeeImportResponses.json, written and verified by the
 * backend test EmployeeImportPilotSafetyPostgresTests). No stack, no database.
 *
 *   EMPLOYEE_IMPORT_PORT=5189 npx playwright test -c e2e/playwright.employee-import.config.ts
 */
const port = Number(process.env.EMPLOYEE_IMPORT_PORT ?? 5189);
export default defineConfig({
  testDir: '.', testMatch: /employee-import-preview\.spec\.ts/, workers: 1, retries: 0,
  reporter: 'list', timeout: 120_000,
  outputDir: '/tmp/kynex-employee-import-playwright-results',
  use: { baseURL: `http://localhost:${port}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  // Never reuse: a port someone else is serving on would be tested instead of this branch.
  webServer: { command: `npx next dev -p ${port}`, url: `http://localhost:${port}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: false, timeout: 240_000 },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'phone', use: { ...devices['Pixel 7'] } },
  ],
});
