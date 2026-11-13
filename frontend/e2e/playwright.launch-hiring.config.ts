import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// Launch-hiring forms (catalogue pickers, offer salary suggestion and band warning, stage gating) against a mocked API: a dev server only.
// LAUNCH_HIRING_PORT picks the port (default 5191); LAUNCH_HIRING_DIST keeps the run off any shared build. LAUNCH_HIRING_SHOTS=<dir> saves screenshots.
const PORT = process.env.LAUNCH_HIRING_PORT || '5191';
export default defineConfig({
  testDir: '.', testMatch: /launch-hiring-forms\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  forbidOnly: !!process.env.CI,
  outputDir: '/tmp/kynex-launch-hiring-results',
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: {
    command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000,
    env: { NEXT_DIST_DIR: process.env.LAUNCH_HIRING_DIST || '.next-claude-lhire' },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1366, height: 768 } } }],
});
