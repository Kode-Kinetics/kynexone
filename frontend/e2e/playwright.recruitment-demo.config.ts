import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// The recruitment demo fixes against a mocked API, no stack, a dev server only (recruitment-demo-ux.spec.ts).
// RECRUITMENT_DEMO_PORT picks the port (default 5176); the server is started for the run and left running if it was already up.
// RECRUITMENT_DEMO_DIST sets the server's NEXT_DIST_DIR so a run never touches a shared .next build.
// RECRUITMENT_DEMO_SHOTS=<dir> also saves screenshots.
const PORT = process.env.RECRUITMENT_DEMO_PORT || '5176';
export default defineConfig({
  testDir: '.', testMatch: /recruitment-demo-ux\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  forbidOnly: !!process.env.CI,
  outputDir: '/tmp/kynex-recruitment-demo-results',
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: {
    command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000,
    env: { NEXT_DIST_DIR: process.env.RECRUITMENT_DEMO_DIST || '.next-claude-recux' },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1366, height: 768 } } },
    { name: 'phone', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
