import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// Demo-polish screens (payroll readiness, pay approvals, compliance, home, offers, run selectors) against a mocked API: a dev server only.
// DEMO_POLISH_PORT picks the port (default 5174); DEMO_POLISH_DIST keeps the run off any shared build. DEMO_POLISH_SHOTS=<dir> saves screenshots.
const PORT = process.env.DEMO_POLISH_PORT || '5174';
export default defineConfig({
  testDir: '.', testMatch: /demo-polish\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  forbidOnly: !!process.env.CI,
  outputDir: '/tmp/kynex-demo-polish-results',
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: {
    command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000,
    env: { NEXT_DIST_DIR: process.env.DEMO_POLISH_DIST || '.next-claude' },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
});
