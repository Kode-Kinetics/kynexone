import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// Demo round 1 front-end fixes (demo-r1-fe.spec.ts) against route mocks: a dev server only, no stack.
// R1FE_PORT picks the port (default 5186); the server's build folder is separate so a run never touches a shared .next build.
// R1FE_SHOTS=<dir> also saves screenshots.
const PORT = process.env.R1FE_PORT || '5186';
export default defineConfig({
  testDir: '.', testMatch: /demo-r1-fe\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  forbidOnly: !!process.env.CI,
  outputDir: '/tmp/kynex-demo-r1-fe-results',
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: {
    command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000,
    env: { NEXT_DIST_DIR: process.env.R1FE_DIST || '.next-claude-r1fe' },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1366, height: 900 } } }],
});
