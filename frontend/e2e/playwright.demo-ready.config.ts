import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// Demo-readiness screen fixes against a mocked API, no stack, a dev server only (hire-walk-fixes.spec.ts).
// DEMO_READY_PORT picks the port (default 5183); the server is started for the run and left running if it was already up.
// DEMO_READY_DIST sets the server's NEXT_DIST_DIR so a run never touches a shared .next build.
// DEMO_READY_SHOTS_DIR=<dir> also saves screenshots.
const PORT = process.env.DEMO_READY_PORT || '5183';
export default defineConfig({
  testDir: '.', testMatch: /demo-ready-[ABC]\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  forbidOnly: !!process.env.CI,
  outputDir: '/tmp/kynex-demo-ready-results',
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: {
    command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000,
    env: { NEXT_DIST_DIR: process.env.DEMO_READY_DIST || '.next-claude-demoready' },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1366, height: 768 } } },
    { name: 'phone', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
