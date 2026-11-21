import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// Hire-to-Self-Service screen fixes against a mocked API, no stack, a dev server only (hire-walk-fixes.spec.ts).
// HIRE_WALK_PORT picks the port (default 5182); the server is started for the run and left running if it was already up.
// HIRE_WALK_DIST sets the server's NEXT_DIST_DIR so a run never touches a shared .next build.
// HIRE_WALK_SHOTS_DIR=<dir> also saves screenshots.
const PORT = process.env.HIRE_WALK_PORT || '5182';
export default defineConfig({
  testDir: '.', testMatch: /hire-walk-fixes\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  forbidOnly: !!process.env.CI,
  outputDir: '/tmp/kynex-hire-walk-results',
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: {
    command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000,
    env: { NEXT_DIST_DIR: process.env.HIRE_WALK_DIST || '.next-claude-hirefix' },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1366, height: 768 } } },
    { name: 'phone', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
