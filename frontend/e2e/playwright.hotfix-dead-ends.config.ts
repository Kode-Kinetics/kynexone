import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// The hotfix dead ends against a mocked API, no stack, a dev server only:
//   G-004 documents-verification.spec.ts, G-006 settlement-run-duty.spec.ts, G-013 platform-activate-draft.spec.ts.
// HOTFIX_DEAD_ENDS_PORT picks the port (default 5190); the server is started for the run and left running if it was already up.
const PORT = process.env.HOTFIX_DEAD_ENDS_PORT || '5190';
export default defineConfig({
  testDir: '.', testMatch: /(documents-verification|settlement-run-duty|platform-activate-draft)\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  forbidOnly: !!process.env.CI,
  outputDir: '/tmp/kynex-hotfix-dead-ends-results',
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: { command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000 },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'phone', use: { ...devices['Pixel 7'] } },
  ],
});
